using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Sim;
using AceEngine.Streaming;
using AceEngine.Tests.Live;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Streaming;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Fuzz
{
    /// <summary>
    /// Network-level fuzzing: a hostile peer sends garbage (random wire messages, mutated, with lying lengths) to the real engine, and the engine
    /// has to keep working: the hostile connection is closed or ignored, there are no unexpected exceptions in the log, memory does not blow up and a normal
    /// client is still served.
    /// </summary>
    public class NetworkFuzzTests
    {
        private sealed class LogCapture : IDisposable
        {
            public readonly List<string> Lines = new List<string>();
            private readonly Action<LogLevel, string, string> _prev;
            private readonly LogLevel _prevLevel;
            public LogCapture()
            {
                _prev = AceLog.Sink; _prevLevel = AceLog.MinLevel;
                AceLog.MinLevel = LogLevel.Warn;
                AceLog.Sink = (l, tag, m) => { lock (Lines) Lines.Add(l + " [" + tag + "] " + m); };
            }
            public List<string> Unexpected(string needle)
            {
                lock (Lines) return Lines.Where(x => x.Contains(needle) && (x.Contains("error:") || x.Contains("Exception") || x.Contains("excepcion"))).ToList();
            }
            public void Dispose() { AceLog.Sink = _prev; AceLog.MinLevel = _prevLevel; }
        }

        private static byte[] Frame(byte id, byte[] payload)
        {
            var b = new byte[5 + payload.Length];
            Wire.PutU32(b, 0, (uint)(1 + payload.Length)); b[4] = id;
            Buffer.BlockCopy(payload, 0, b, 5, payload.Length);
            return b;
        }

        /// <summary>A wire frame (BitTorrent or live): valid, mutated, with an unknown id or with a lying length.</summary>
        private static byte[] RandomFrame(Random r, IList<byte[]> templates, int maxId)
        {
            int kind = r.Next(10);
            if (kind < 5 && templates.Count > 0)
            {
                var t = templates[r.Next(templates.Count)];
                var m = FuzzKit.Mutate(r, t, templates);
                if (r.Next(3) == 0) return m;                        // not framed properly
                return Frame(m.Length > 0 ? m[0] : (byte)0, m.Skip(1).ToArray());
            }
            if (kind < 8)
            {
                var payload = FuzzKit.Random(r, 200);
                return Frame((byte)(r.Next(4) == 0 ? r.Next(256) : r.Next(maxId + 1)), payload);
            }
            if (kind == 8)
            {
                // lying length: announces a lot and sends little
                var b = new byte[4 + r.Next(0, 20)];
                r.NextBytes(b);
                Wire.PutU32(b, 0, (uint)r.Next(1, r.Next(2) == 0 ? 600000 : 100));
                return b;
            }
            return FuzzKit.Random(r, 64);
        }

        // ---------------------------------------------------------------- VOD

        [Fact]
        public async Task VodPeerWireGarbageDoesNotBreakTheSwarm()
        {
            using (var cap = new LogCapture())
            {
                var t = TestTorrent.Create(8 * 32768, 32768, seed: 31);
                using (var seeder = new FakeSeeder(t))
                using (var host = new AceEngineHost(new EngineOptions { EnableListener = true, EnableDht = false, EnableTrackers = false, ConnectTimeout = TimeSpan.FromSeconds(3), HandshakeTimeout = TimeSpan.FromSeconds(3) }))
                {
                    host.Options.StaticPeers.Add(seeder.EndPoint.ToString());
                    host.Start();
                    var path = Path.Combine(Path.GetTempPath(), "p-fuzz-" + Guid.NewGuid().ToString("N") + ".torrent");
                    File.WriteAllBytes(path, t.TorrentBytes);
                    OpenedStream opened;
                    try { opened = await host.OpenAsync(AceLink.Parse(path), null, CancellationToken.None); } finally { File.Delete(path); }
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        var body = await http.GetByteArrayAsync(opened.Url);
                        Assert.Equal(t.Content.Length, body.Length);
                    }
                    long memBefore = GC.GetTotalMemory(true);

                    var templates = new List<byte[]>
                    {
                        Wire.Have(3), Wire.Bitfield(new byte[1] { 0xff }), Wire.Request(1, 0, 16384), Wire.Request(1, 99999, 16384), Wire.Cancel(1, 0, 16384),
                        Wire.Piece(1, 0, new byte[100], 0, 100), Wire.Extended(0, ExtensionMessages.BuildExtendedHandshake("fuzz", 1234, true, true, 100, null)),
                        Wire.Extended(2, ExtensionMessages.BuildMetadataRequest(0)), Wire.Extended(1, ExtensionMessages.BuildPex(new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5) })),
                        Wire.Interested(), Wire.NotInterested(), Wire.Choke(), Wire.Unchoke(), Wire.KeepAlive
                    }.Select(f => f.Skip(4).ToArray()).Where(f => f.Length > 0).ToList();
                    var rnd = new Random(77);
                    var ports = new List<int>();
                    int connections = 0, sent = 0;
                    var end = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < end && connections < 120)
                    {
                        connections++;
                        using (var c = new TcpClient())
                        {
                            try
                            {
                                await c.ConnectAsync(IPAddress.Loopback, host.ListenPort);
                                ports.Add(((IPEndPoint)c.Client.LocalEndPoint).Port);
                                var s = c.GetStream();
                                var head = Wire.BuildHandshakeHead(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 }, t.InfoHash);
                                await s.WriteAsync(head, 0, head.Length);
                                var drain = Task.Run(async () => { var buf = new byte[4096]; try { while (await s.ReadAsync(buf, 0, buf.Length) > 0) { } } catch { } });
                                await Task.Delay(30);
                                var pid = PeerId.Generate();
                                await s.WriteAsync(pid, 0, 20);
                                for (int i = 0; i < rnd.Next(1, 60); i++)
                                {
                                    var f = RandomFrame(rnd, templates, 20);
                                    await s.WriteAsync(f, 0, f.Length);
                                    sent++;
                                }
                                await Task.Delay(rnd.Next(5, 40));
                            }
                            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException) { }
                        }
                    }
                    // also a pure-garbage handshake and a handshake with an infohash that does not exist
                    for (int i = 0; i < 40; i++)
                    {
                        using (var c = new TcpClient())
                        {
                            try
                            {
                                await c.ConnectAsync(IPAddress.Loopback, host.ListenPort);
                                var junk = FuzzKit.Random(rnd, 200);
                                await c.GetStream().WriteAsync(junk, 0, junk.Length);
                                await Task.Delay(5);
                            }
                            catch (Exception ex) when (ex is IOException || ex is SocketException) { }
                        }
                    }

                    // the engine is still alive: a normal peer is served (handshake + EXTEND + BITFIELD) and the session keeps serving
                    using (var c = new TcpClient())
                    {
                        await c.ConnectAsync(IPAddress.Loopback, host.ListenPort);
                        var s = c.GetStream();
                        var head = Wire.BuildHandshakeHead(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 }, t.InfoHash);
                        await s.WriteAsync(head, 0, head.Length);
                        var reply = new byte[68];
                        int got = 0;
                        using (var cts = new CancellationTokenSource(8000))
                            while (got < 68) { int n = await s.ReadAsync(reply, got, 68 - got, cts.Token); if (n == 0) break; got += n; }
                        Assert.Equal(68, got);
                    }
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        var body = await http.GetByteArrayAsync(opened.Url);
                        Assert.Equal(t.Content, body);
                    }
                    GC.Collect();
                    long growth = GC.GetTotalMemory(true) - memBefore;
                    Assert.True(growth < FuzzKit.MemoryBudget, "memory grew " + growth / 1024 / 1024 + " MiB");
                    Console.WriteLine("[fuzz] vod-wire: " + connections + " hostile connections, " + sent + " frames, memory +" + growth / 1024 + " KiB");
                    foreach (var p in ports.Distinct())
                    {
                        var bad = cap.Unexpected(":" + p);
                        Assert.True(bad.Count == 0, "unexpected exceptions: " + string.Join("\n", bad.Take(5)));
                    }
                    var anyBad = cap.Lines.Where(x => x.Contains("NullReference") || x.Contains("IndexOutOfRange") || x.Contains("InvalidCast") || x.Contains("OutOfMemory") || x.Contains("ArgumentOutOfRange") || x.Contains("OverflowException") || x.Contains("DivideByZero")).ToList();
                    Assert.True(anyBad.Count == 0, "unexpected exceptions: " + string.Join("\n", anyBad.Take(5)));
                }
            }
        }

        // ---------------------------------------------------------------- live

        [Fact]
        public async Task LivePeerWireGarbageDoesNotBreakTheSwarm()
        {
            using (var cap = new LogCapture())
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4, Bitrate = 262144, PieceLength = 65536, ChunkLength = 16384 }, o => { o.EnableListener = true; o.Live.UploadSlots = 2; }))
            {
                h.AddSource();
                await h.OpenAsync();
                await h.ReadAsync(3);
                var model = h.Model;
                var templates = new List<byte[]>
                {
                    LiveWire.Have(1000, 1.5), LiveWire.Have(7, null), LiveWire.Bitfield(1000, 40, new byte[5], null, null), LiveWire.Request(LiveId.Request, 5, 3, true), LiveWire.Request(LiveId.Request, 5, 3, false),
                    LiveWire.Request(LiveId.HpRequest, 5, 3, true), LiveWire.Request(LiveId.Cancel, 5, 3, true), LiveWire.Piece(5, 1.5, 3, true, new byte[100], 0, 100), LiveWire.Simple(LiveId.Interested),
                    LiveWire.Simple(LiveId.Unchoke), LiveWire.Simple(LiveId.Choke), LiveWire.Simple(LiveId.Ping), LiveWire.Simple(LiveId.Close)
                }.Select(f => f.Skip(4).ToArray()).ToList();
                var d = new BDict(); d.Add("a", BValue.Int(1)); d.Add("t", BValue.Int(2));
                templates.Add(LiveWire.MyInfo(d).Skip(4).ToArray());
                var rnd = new Random(78);
                var ports = new List<int>();
                int sent = 0, connections = 0;
                long memBefore = GC.GetTotalMemory(true);
                var end = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < end && connections < 80)
                {
                    connections++;
                    using (var c = new RawLiveClient())
                    {
                        try
                        {
                            await c.ConnectAsync(h.Host.ListenPort, model.InfoHash, aceName: rnd.Next(2) == 0, sendExtend: rnd.Next(4) != 0);
                            ports.Add(c.LocalPort);
                            for (int i = 0; i < rnd.Next(1, 60); i++)
                            {
                                await c.SendAsync(RandomFrame(rnd, templates, 40));
                                sent++;
                            }
                            await c.PumpAsync(rnd.Next(5, 40));
                        }
                        catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is EndOfStreamException || ex is OperationCanceledException) { }
                    }
                }
                // live playback continues (the simulated player receives data) and the engine serves a normal client
                var v = await h.ReadAsync(3);
                Assert.True(v.Bytes > 100000, "the live stream stopped serving after the garbage: " + v.Bytes + " bytes; " + h.Session.DebugInfo);
                using (var c = new RawLiveClient())
                {
                    await c.ConnectAsync(h.Host.ListenPort, model.InfoHash);
                    await c.PumpAsync(3000, () => c.Of(LiveId.Bitfield).Any());
                    Assert.NotNull(c.RemoteExtend);
                }
                GC.Collect();
                long growth = GC.GetTotalMemory(true) - memBefore;
                Assert.True(growth < FuzzKit.MemoryBudget, "memory grew " + growth / 1024 / 1024 + " MiB");
                Console.WriteLine("[fuzz] live-wire: " + connections + " hostile connections, " + sent + " frames, memory +" + growth / 1024 + " KiB");
                var anyBad = cap.Lines.Where(x => x.Contains("NullReference") || x.Contains("IndexOutOfRange") || x.Contains("InvalidCast") || x.Contains("OutOfMemory") || x.Contains("ArgumentOutOfRange") || x.Contains("OverflowException") || x.Contains("DivideByZero") || x.Contains("KeyNotFound")).ToList();
                Assert.True(anyBad.Count == 0, "unexpected exceptions: " + string.Join("\n", anyBad.Take(5)));
            }
        }

        // ---------------------------------------------------------------- P2P resolution

        private sealed class GarbageMetadataPeer : IDisposable
        {
            private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Random _r;
            public int Connections;
            public IPEndPoint EndPoint { get { return new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_l.LocalEndpoint).Port); } }

            private readonly bool _liveSwarm;

            /// <param name="liveSwarm">true: imitates an engine of a live stream (without metadata_size) that answers 'full' with garbage or with another channel's descriptor.</param>
            public GarbageMetadataPeer(int seed, bool liveSwarm = false)
            {
                _liveSwarm = liveSwarm;
                _r = new Random(seed);
                _l.Start();
                Task.Run(Loop);
            }

            private async Task Loop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                    Interlocked.Increment(ref Connections);
                    var ignored = Task.Run(() => Handle(c));
                }
            }

            private async Task Handle(TcpClient c)
            {
                try
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var ct = _cts.Token;
                        var buf = new byte[68];
                        int got = 0;
                        while (got < 68) { int n = await s.ReadAsync(buf, got, 68 - got, ct); if (n == 0) return; got += n; }
                        int mode;
                        lock (_r) mode = _liveSwarm ? 4 + _r.Next(2) : _r.Next(4);
                        // Ace handshake with the requested infohash followed by garbage
                        var hs = new List<byte> { 17 };
                        hs.AddRange(Encoding.ASCII.GetBytes("AceStreamProtocol")); hs.AddRange(new byte[8]); hs.AddRange(buf.Skip(28).Take(20)); hs.AddRange(new byte[20]);
                        await s.WriteAsync(hs.ToArray(), 0, hs.Count, ct);
                        if (mode == 0) { var junk = new byte[4096]; lock (_r) _r.NextBytes(junk); await s.WriteAsync(junk, 0, junk.Length, ct); }
                        else if (mode == 1)
                        {
                            // giant length and little body
                            await s.WriteAsync(new byte[] { 0x7f, 0xff, 0xff, 0xff, 20, 0, 1, 2, 3 }, 0, 9, ct);
                        }
                        else if (mode == 2)
                        {
                            // Ace engine extended handshake and, after the request, a response with an absurd total_size and filler data
                            var d = new BDict(); var m = new BDict(); m.Add("ut_metadata", BValue.Int(2)); d.Add("m", m); d.Add("ace_metadata_version", BValue.Int(1)); d.Add("metadata_size", BValue.Int(int.MaxValue));
                            var payload = BencodeWriter.Encode(d);
                            var f = Wire.Extended(0, payload);
                            await s.WriteAsync(f, 0, f.Length, ct);
                            var junkBody = new byte[1000]; lock (_r) _r.NextBytes(junkBody);
                            var resp = new BDict(); resp.Add("msg_type", BValue.Int(1)); resp.Add("full", BValue.Int(1)); resp.Add("total_size", BValue.Int(999999999)); resp.Add("request_hash", BValue.Bytes(buf.Skip(28).Take(20).ToArray()));
                            var rp = BencodeWriter.Encode(resp).Concat(junkBody).ToArray();
                            var rf = Wire.Extended(2, rp);
                            await Task.Delay(100, ct);
                            await s.WriteAsync(rf, 0, rf.Length, ct);
                        }
                        else if (mode == 4 || mode == 5)
                        {
                            // Ace engine of a live stream (extended handshake WITHOUT metadata_size, like the real ones): when asked for 'full' it answers with garbage or with the
                            // descriptor of another channel, always with request_hash = what was asked for (MetadataFetcher's InfoHash mode)
                            var d = new BDict(); var m = new BDict(); m.Add("ut_metadata", BValue.Int(2)); d.Add("m", m); d.Add("ace_metadata_version", BValue.Int(1));
                            var f = Wire.Extended(0, BencodeWriter.Encode(d));
                            await s.WriteAsync(f, 0, f.Length, ct);
                            byte[] body;
                            if (mode == 4) { body = new byte[lockedNext(900)]; lock (_r) _r.NextBytes(body); }
                            else body = TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive");        // an authentic descriptor, but of another channel
                            var resp = new BDict(); resp.Add("msg_type", BValue.Int(1)); resp.Add("full", BValue.Int(1)); resp.Add("total_size", BValue.Int(body.Length)); resp.Add("request_hash", BValue.Bytes(buf.Skip(28).Take(20).ToArray()));
                            var rf = Wire.Extended(2, BencodeWriter.Encode(resp).Concat(body).ToArray());
                            await Task.Delay(100, ct);
                            await s.WriteAsync(rf, 0, rf.Length, ct);
                        }
                        else
                        {
                            for (int i = 0; i < 30; i++)
                            {
                                var junk = new byte[lockedNext(300)]; lock (_r) _r.NextBytes(junk);
                                await s.WriteAsync(junk, 0, junk.Length, ct);
                            }
                        }
                        await Task.Delay(500, ct);
                    }
                }
                catch { }
            }

            private int lockedNext(int max) { lock (_r) return _r.Next(1, max); }
            public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
        }

        [Fact]
        public async Task HostileMetadataPeersCannotHangOrBreakTheResolver()
        {
            var opt = new EngineOptions { EnableDht = false, EnableTrackers = false, EnableListener = false, ConnectTimeout = TimeSpan.FromSeconds(2), HandshakeTimeout = TimeSpan.FromSeconds(2) };
            opt.Resolver.InactiveTimeout = TimeSpan.FromSeconds(2);
            var peers = Enumerable.Range(0, 8).Select(i => new GarbageMetadataPeer(100 + i)).ToList();
            try
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long before = GC.GetTotalMemory(true);
                var got = await f.FetchAsync(Hex.Parse(new string('a', 40)), MetadataMode.AceFull, null, peers.Select(p => p.EndPoint), TimeSpan.FromSeconds(6), CancellationToken.None);
                Assert.Null(got);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "tardo " + sw.Elapsed.TotalSeconds);
                var f2 = new MetadataFetcher(opt, null, new HttpClient());
                Assert.Null(await f2.FetchAsync(Hex.Parse(new string('b', 40)), MetadataMode.Bep9, null, peers.Select(p => p.EndPoint), TimeSpan.FromSeconds(5), CancellationToken.None));
                // infohash of a link from the search engine: the peer chooses between BEP9 and 'full'; neither garbage nor the authentic descriptor of ANOTHER channel is accepted
                var f3 = new MetadataFetcher(opt, null, new HttpClient());
                Assert.Null(await f3.FetchAsync(Hex.Parse(new string('c', 40)), MetadataMode.InfoHash, null, peers.Select(p => p.EndPoint), TimeSpan.FromSeconds(5), CancellationToken.None));
                var liveSwarm = Enumerable.Range(0, 6).Select(i => new GarbageMetadataPeer(200 + i, true)).ToList();
                try
                {
                    Assert.Null(await f3.FetchAsync(Hex.Parse(new string('d', 40)), MetadataMode.InfoHash, null, liveSwarm.Select(p => p.EndPoint), TimeSpan.FromSeconds(5), CancellationToken.None));
                    Assert.True(liveSwarm.Sum(p => p.Connections) > 0);
                }
                finally { foreach (var p in liveSwarm) p.Dispose(); }
                GC.Collect();
                Assert.True(GC.GetTotalMemory(true) - before < FuzzKit.MemoryBudget);
                Assert.True(peers.Sum(p => p.Connections) > 0);
            }
            finally { foreach (var p in peers) p.Dispose(); }
        }

        // ---------------------------------------------------------------- DHT

        [Fact]
        public async Task DhtIgnoresGarbageDatagramsAndStillAnswersPing()
        {
            var opt = new EngineOptions { EnableDht = true };
            using (var dht = new DhtClient(opt))
            using (var other = new DhtClient(opt))
            {
                dht.Start(); other.Start();
                var rnd = new Random(79);
                var templates = new List<byte[]>
                {
                    TestUtil.Ascii("d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe"),
                    TestUtil.Ascii("d1:rd2:id20:abcdefghij0123456789e1:t2:aa1:y1:re"),
                    TestUtil.Ascii("d1:eli201e5:errore1:t2:aa1:y1:ee"),
                    TestUtil.Ascii("d1:ad2:id20:abcdefghij01234567894:name3:fooe1:q9:get_peers1:t2:aa1:y1:qe")
                };
                using (var u = new UdpClient())
                {
                    var target = new IPEndPoint(IPAddress.Loopback, dht.Port);
                    for (int i = 0; i < 4000; i++)
                    {
                        var d = rnd.Next(3) == 0 ? FuzzKit.Random(rnd, 600) : FuzzKit.Mutate(rnd, templates[rnd.Next(templates.Count)], templates);
                        await u.SendAsync(d, d.Length, target);
                    }
                    // a 60 KB datagram and a huge transaction id
                    var big = new byte[60000]; rnd.NextBytes(big);
                    await u.SendAsync(big, big.Length, target);
                    var t = Encoding.ASCII.GetBytes("d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t5000:" + new string('x', 5000) + "1:y1:qe");
                    await u.SendAsync(t, t.Length, target);
                }
                await Task.Delay(300);
                using (var cts = new CancellationTokenSource(5000))
                    Assert.True(await other.PingAsync(new IPEndPoint(IPAddress.Loopback, dht.Port), cts.Token), "the DHT stopped answering the ping after the garbage");
            }
        }

        // ---------------------------------------------------------------- local HTTP server

        [Fact]
        public async Task LocalHttpServerSurvivesMalformedRequests()
        {
            var data = new byte[200000]; new Random(5).NextBytes(data);
            var session = new MemorySession(data);
            using (var server = new LocalHttpServer())
            {
                server.Start();
                var url = server.Register(session);
                var port = server.Port;
                var rnd = new Random(80);
                var seeds = new List<byte[]>
                {
                    TestUtil.Ascii("GET /stream/" + session.Id + " HTTP/1.1\r\nHost: x\r\nRange: bytes=0-100\r\n\r\n"),
                    TestUtil.Ascii("HEAD /stream/" + session.Id + " HTTP/1.0\r\n\r\n"),
                    TestUtil.Ascii("GET / HTTP/1.1\r\n\r\n"),
                    TestUtil.Ascii("POST /stream/x HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello")
                };
                var tasks = new List<Task>();
                for (int w = 0; w < 4; w++)
                {
                    int seed = rnd.Next();
                    tasks.Add(Task.Run(async () =>
                    {
                        var r = new Random(seed);
                        for (int i = 0; i < 150; i++)
                        {
                            using (var c = new TcpClient())
                            {
                                try
                                {
                                    await c.ConnectAsync(IPAddress.Loopback, port);
                                    var s = c.GetStream();
                                    byte[] req;
                                    int kind = r.Next(5);
                                    if (kind == 0) req = FuzzKit.Random(r, 300);
                                    else if (kind == 1) req = Encoding.ASCII.GetBytes("GET /stream/" + session.Id + " HTTP/1.1\r\nX: " + new string('a', r.Next(1, 20000)) + "\r\n\r\n");     // huge header
                                    else if (kind == 2) req = Encoding.ASCII.GetBytes("GET /stream/" + session.Id + " HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, r.Next(1, 500)).Select(n => "H" + n + ": v\r\n")) + "\r\n");
                                    else req = FuzzKit.Mutate(r, seeds[r.Next(seeds.Count)], seeds);
                                    await s.WriteAsync(req, 0, req.Length);
                                    if (r.Next(3) == 0) { await Task.Delay(r.Next(1, 20)); continue; }       // it leaves without reading
                                    var buf = new byte[1024];
                                    using (var cts = new CancellationTokenSource(500)) { try { await s.ReadAsync(buf, 0, buf.Length, cts.Token); } catch (OperationCanceledException) { } }
                                }
                                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException) { }
                            }
                        }
                    }));
                }
                await Task.WhenAll(tasks);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var body = await http.GetByteArrayAsync(url);
                    Assert.Equal(data, body);
                    var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.TryAddWithoutValidation("Range", "bytes=1000-1999");
                    var resp = await http.SendAsync(req);
                    Assert.Equal(System.Net.HttpStatusCode.PartialContent, resp.StatusCode);
                }
            }
        }

        // ---------------------------------------------------------------- HTTP limits

        [Fact]
        public async Task EndlessOrHugeHttpBodiesAreCutAtTheLimit()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            var cts = new CancellationTokenSource();
            long sent = 0;
            var server = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                    var ignored = Task.Run(async () =>
                    {
                        try
                        {
                            using (c)
                            {
                                var s = c.GetStream();
                                var rb = new byte[2048]; await s.ReadAsync(rb, 0, rb.Length);
                                string req = Encoding.ASCII.GetString(rb);
                                if (req.Contains("/huge"))
                                {
                                    var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2000000000\r\n\r\n");
                                    await s.WriteAsync(h, 0, h.Length);
                                    var chunk = new byte[65536];
                                    while (!cts.IsCancellationRequested) { await s.WriteAsync(chunk, 0, chunk.Length); Interlocked.Add(ref sent, chunk.Length); }
                                }
                                else
                                {
                                    var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n");
                                    await s.WriteAsync(h, 0, h.Length);
                                    var chunk = new byte[65536];
                                    var head = Encoding.ASCII.GetBytes("10000\r\n");
                                    var tail = Encoding.ASCII.GetBytes("\r\n");
                                    while (!cts.IsCancellationRequested) { await s.WriteAsync(head, 0, head.Length); await s.WriteAsync(chunk, 0, chunk.Length); await s.WriteAsync(tail, 0, tail.Length); Interlocked.Add(ref sent, chunk.Length); }
                                }
                            }
                        }
                        catch { }
                    });
                }
            });
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = HttpLimits.ClientBuffer })
                {
                    long before = GC.GetTotalMemory(true);
                    foreach (var path in new[] { "/huge", "/endless" })
                    {
                        using (var resp = await http.GetAsync("http://127.0.0.1:" + port + path, HttpCompletionOption.ResponseHeadersRead))
                            await Assert.ThrowsAsync<InvalidDataException>(() => HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.TrackerBody, CancellationToken.None));
                    }
                    GC.Collect();
                    Assert.True(GC.GetTotalMemory(true) - before < FuzzKit.MemoryBudget);
                    // the normal path (ResponseContentRead) is also bounded by the client's cap
                    await Assert.ThrowsAnyAsync<Exception>(() => http.GetByteArrayAsync("http://127.0.0.1:" + port + "/endless"));
                    Assert.True(Interlocked.Read(ref sent) < 400L * 1024 * 1024, "the client kept reading too much");
                }
            }
            finally { cts.Cancel(); l.Stop(); }
        }
    }
}
