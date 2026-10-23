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
using AceEngine.Crypto;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Live
{
    /// <summary>A live client written in the test that talks to the engine through its listening port (to check what the engine sends and serves).</summary>
    internal sealed class RawLiveClient : IDisposable
    {
        private readonly TcpClient _tcp = new TcpClient();
        private NetworkStream _s;
        public readonly byte[] PeerId = PeerIdBytes();
        public LiveExtendInfo RemoteExtend;
        public int LocalPort { get { return ((IPEndPoint)_tcp.Client.LocalEndPoint).Port; } }
        public readonly List<KeyValuePair<byte, byte[]>> Received = new List<KeyValuePair<byte, byte[]>>();
        public bool Closed;
        public string RemoteProtocol;
        private readonly byte[] _seed, _pub;

        private static byte[] PeerIdBytes() { return Encoding.ASCII.GetBytes("R30------" + Guid.NewGuid().ToString("N").Substring(0, 11)); }

        public RawLiveClient() { Ed25519.GenerateKeyPair(out _seed, out _pub); }

        public async Task ConnectAsync(int port, byte[] infoHash, bool aceName = true, bool sendExtend = true)
        {
            await _tcp.ConnectAsync(IPAddress.Loopback, port);
            _s = _tcp.GetStream();
            var hs = LivePeer_BuildHandshake(aceName, infoHash);
            await _s.WriteAsync(hs, 0, hs.Length);
            var one = new byte[1];
            await ReadExact(one, 1);
            var rest = new byte[one[0] + 8 + 20 + 20];
            await ReadExact(rest, rest.Length);
            RemoteProtocol = Encoding.ASCII.GetString(rest, 0, one[0]);
            if (sendExtend) await SendExtendAsync();
        }

        private byte[] LivePeer_BuildHandshake(bool aceName, byte[] ih)
        {
            string name = aceName ? "AceStreamProtocol" : "BitTorrent protocol";
            var b = new List<byte> { (byte)name.Length };
            b.AddRange(Encoding.ASCII.GetBytes(name));
            b.AddRange(aceName ? new byte[8] : new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 });
            b.AddRange(ih); b.AddRange(PeerId);
            return b.ToArray();
        }

        public async Task SendExtendAsync(int version = 3022200)
        {
            var o = new LiveOptions { EngineVersion = version };
            var payload = LiveExtend.BuildPayload(o, new LiveLocalState { Lsp = -1, Position = -1 }, 0, null, 1, _seed, _pub);
            await SendAsync(LiveExtend.BuildFrame(payload));
        }

        public async Task SendAsync(byte[] frame) { await _s.WriteAsync(frame, 0, frame.Length); }

        private async Task ReadExact(byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = await _s.ReadAsync(b, got, n - got);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
        }

        /// <summary>Reads messages for 'ms' milliseconds (or until 'until' returns true) and stores them in Received.</summary>
        public async Task PumpAsync(int ms, Func<bool> until = null)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            var lenBuf = new byte[4];
            while (DateTime.UtcNow < end && !(until != null && until()))
            {
                try
                {
                    var left = end - DateTime.UtcNow;
                    using (var cts = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)))
                    {
                        int got = 0;
                        while (got < 4) { int r = await _s.ReadAsync(lenBuf, got, 4 - got, cts.Token); if (r == 0) { Closed = true; return; } got += r; }
                        uint len = Wire.GetU32(lenBuf, 0);
                        if (len == 0) continue;
                        var msg = new byte[len];
                        got = 0;
                        while (got < len) { int r = await _s.ReadAsync(msg, got, (int)len - got, cts.Token); if (r == 0) { Closed = true; return; } got += r; }
                        lock (Received) Received.Add(new KeyValuePair<byte, byte[]>(msg[0], msg));
                        if (msg[0] == LiveId.Extend && msg[1] == 0 && RemoteExtend == null) LiveExtend.TryParse(msg, out RemoteExtend);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { Closed = true; return; }
                catch (ObjectDisposedException) { Closed = true; return; }
            }
        }

        public IEnumerable<byte[]> Of(byte id) { lock (Received) return Received.Where(r => r.Key == id).Select(r => r.Value).ToList(); }
        public void Dispose() { try { _tcp.Close(); } catch { } }
    }

    /// <summary>Raw fake peer the engine connects to: after the handshake it runs a script (to test how the engine reacts to a faulty peer).</summary>
    internal sealed class RawLiveServer : IDisposable
    {
        private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
        private readonly byte[] _ih;
        private readonly Func<NetworkStream, Task> _script;
        private readonly bool _wrongInfoHash;
        public volatile bool Connected;
        public volatile bool EngineClosedFirst;
        public int Port { get; private set; }
        public IPEndPoint EndPoint { get { return new IPEndPoint(IPAddress.Loopback, Port); } }

        public RawLiveServer(byte[] infoHash, Func<NetworkStream, Task> script, bool wrongInfoHash = false)
        {
            _ih = infoHash; _script = script; _wrongInfoHash = wrongInfoHash;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                var cc = c;
                var ignored = Task.Run(async () =>
                {
                    try
                    {
                        Connected = true;
                        var s = cc.GetStream();
                        var buf = new byte[68];
                        int got = 0;
                        while (got < 68) { int r = await s.ReadAsync(buf, got, 68 - got); if (r == 0) return; got += r; }
                        var resp = new List<byte> { 19 };
                        resp.AddRange(Encoding.ASCII.GetBytes("BitTorrent protocol"));
                        resp.AddRange(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 });
                        resp.AddRange(_wrongInfoHash ? Enumerable.Repeat((byte)7, 20).ToArray() : _ih);
                        resp.AddRange(Encoding.ASCII.GetBytes("R30------evil" + Guid.NewGuid().ToString("N").Substring(0, 7)));
                        await s.WriteAsync(resp.ToArray(), 0, resp.Count);
                        await _script(s);
                        // wait for the engine to close
                        var one = new byte[1];
                        using (var cts = new CancellationTokenSource(3000))
                        {
                            try { int r = await s.ReadAsync(one, 0, 1, cts.Token); while (r > 0) r = await s.ReadAsync(one, 0, 1, cts.Token); EngineClosedFirst = true; }
                            catch (IOException) { EngineClosedFirst = true; }
                            catch (OperationCanceledException) { }
                        }
                    }
                    catch { EngineClosedFirst = true; }
                    finally { cc.Close(); }
                });
            }
        }

        public void Dispose() { try { _l.Stop(); } catch { } }
    }


    public class LiveNetworkTests
    {
        private static LiveSimOptions Sim(Action<LiveSimOptions> tweak = null)
        {
            var o = new LiveSimOptions { TimeScale = 4, Bitrate = 262144, PieceLength = 65536, ChunkLength = 16384 };
            if (tweak != null) tweak(o);
            return o;
        }

        // ---------- Upload and serving other peers ----------

        [Fact]
        public async Task ServesPiecesToAnotherClientAndAnswersPingWithPong()
        {
            using (var h = new LiveHarness(Sim(), o => { o.EnableListener = true; o.Live.UploadSlots = 2; o.Live.RechokeIntervalSeconds = 0.2; }))
            {
                h.AddSource();
                await h.OpenAsync();
                var warm = h.ReadAsync(4);
                await warm;
                var model = h.Model;
                var pub = RsaPublicKey.Parse(model.Descriptor.PubKey);

                using (var c = new RawLiveClient())
                {
                    await c.ConnectAsync(h.Host.ListenPort, model.InfoHash);
                    await c.PumpAsync(3000, () => c.Of(LiveId.Bitfield).Any());
                    // the engine's EXTEND: signed with ed25519 and with the fields of the live protocol
                    Assert.NotNull(c.RemoteExtend);
                    Assert.True(c.RemoteExtend.HasSignature); Assert.True(c.RemoteExtend.SignatureValid.Value);
                    Assert.Equal(1, c.RemoteExtend.Nt); Assert.Equal("bt", c.RemoteExtend.Tt); Assert.Equal(3022200, c.RemoteExtend.V);
                    Assert.Equal(2, c.RemoteExtend.UtMetadataId);
                    Assert.Equal(0, c.RemoteExtend.MiPeerType);
                    Assert.True(c.Received.First().Key == LiveId.Extend, "the first thing the engine sends is the EXTEND");
                    var dump = Environment.GetEnvironmentVariable("ACE_DUMP_EXTEND");
                    if (!string.IsNullOrEmpty(dump)) File.WriteAllText(dump, Hex.ToHex(c.Received.First().Value));       // to verify it with an independent implementation (Python)
                    // BITFIELD of the window of pieces it has (without timestamps) and MYINFO
                    var bf = c.Of(LiveId.Bitfield).First();
                    ulong first; int nbits, off; double? t0, t1;
                    Assert.True(LiveWire.TryParseBitfield(bf, out first, out nbits, out off, out t0, out t1));
                    Assert.Null(t0);
                    Assert.True(nbits > 20);
                    Assert.True(c.Of(LiveId.MyInfo).Any());
                    long idx = (long)first + nbits - 3;               // a recent piece
                    Assert.True(LiveWire.BitAt(bf, off, nbits - 3));

                    // without INTERESTED there is no UNCHOKE; with INTERESTED, there is
                    await c.SendAsync(LiveWire.Simple(LiveId.Interested));
                    await c.PumpAsync(4000, () => c.Of(LiveId.Unchoke).Any());
                    Assert.True(c.Of(LiveId.Unchoke).Any(), "the engine must UNCHOKE");

                    for (int ch = 0; ch < model.Chunks; ch++) await c.SendAsync(LiveWire.Request(LiveId.Request, (ulong)idx, ch, true));
                    await c.PumpAsync(4000, () => c.Of(LiveId.Piece).Count() >= model.Chunks);
                    var pieces = c.Of(LiveId.Piece).ToList();
                    Assert.Equal(model.Chunks, pieces.Count);
                    var assembled = new byte[model.PieceLength];
                    foreach (var p in pieces)
                    {
                        ulong pidx; double ts; int chunk, doff;
                        Assert.True(LiveWire.TryParsePiece(p, true, out pidx, out ts, out chunk, out doff));
                        Assert.Equal((ulong)idx, pidx);
                        Assert.Equal(model.Timestamp(idx), ts, 3);
                        Buffer.BlockCopy(p, doff, assembled, chunk * model.ChunkLength, p.Length - doff);
                    }
                    Assert.Equal(model.GetPiece(idx), assembled);                    // bytes identical to the piece signed by the broadcaster
                    Assert.True(pub.VerifySha1(assembled, 0, model.ContentSize, assembled, model.ContentSize));

                    // PING -> PONG with 'size' zeroed bytes
                    await c.SendAsync(LiveWire.Ping(7, 100));
                    await c.PumpAsync(3000, () => c.Of(LiveId.Pong).Any());
                    var pong = c.Of(LiveId.Pong).Single();
                    int seq, size;
                    Assert.True(LiveWire.TryParsePingPong(pong, out seq, out size));
                    Assert.Equal(7, seq); Assert.Equal(100, size); Assert.Equal(9 + 100, pong.Length);
                    Assert.True(pong.Skip(9).All(b => b == 0));
                    Assert.True(h.Session.Stats.UploadedBytes >= model.PieceLength);
                }
            }
        }

        [Fact]
        public async Task ChangePosFiltersTheHavesTheEngineSends()
        {
            using (var h = new LiveHarness(Sim(), o => { o.EnableListener = true; o.Live.UploadSlots = 2; }))
            {
                h.AddSource();
                await h.OpenAsync();
                var reading = h.ReadAsync(8);
                await Task.Delay(1500);
                using (var c = new RawLiveClient())
                {
                    await c.ConnectAsync(h.Host.ListenPort, h.Model.InfoHash);
                    await c.PumpAsync(1500);
                    var bf0 = c.Of(LiveId.Bitfield).Count();
                    // position far in the future with a window of 5 pieces: no HAVE must arrive
                    await c.SendAsync(LiveWire.ChangePos((ulong)(h.Model.Head + 100000), 5));
                    lock (c.Received) c.Received.Clear();
                    await c.PumpAsync(2500);
                    Assert.Empty(c.Of(LiveId.Have));
                    // window of 0 (no filtering) and an old position: the HAVEs come back
                    await c.SendAsync(LiveWire.ChangePos(0, 0));
                    await c.PumpAsync(2500, () => c.Of(LiveId.Have).Count() >= 3);
                    Assert.True(c.Of(LiveId.Have).Count() >= 3, "HAVE after CHANGEPOS without a filter");
                    // 9-byte HAVE (no timestamp) until it knows the peer wants them
                    Assert.All(c.Of(LiveId.Have), m => Assert.Equal(9, m.Length));
                }
                await reading;
            }
        }

        [Fact]
        public async Task UploadSlotsZeroNeverAnnouncesNorUnchokesButStillAnswersPing()
        {
            using (var h = new LiveHarness(Sim(), o => { o.EnableListener = true; o.Live.UploadSlots = 0; o.Live.RechokeIntervalSeconds = 0.2; }))
            {
                h.AddSource();
                await h.OpenAsync();
                var reading = h.ReadAsync(6);
                await Task.Delay(1500);
                using (var c = new RawLiveClient())
                {
                    await c.ConnectAsync(h.Host.ListenPort, h.Model.InfoHash);
                    await c.SendAsync(LiveWire.Simple(LiveId.Interested));
                    await c.SendAsync(LiveWire.Ping(3, 16));
                    await c.PumpAsync(2500);
                    Assert.Empty(c.Of(LiveId.Bitfield)); Assert.Empty(c.Of(LiveId.Have)); Assert.Empty(c.Of(LiveId.Unchoke));
                    Assert.Single(c.Of(LiveId.Pong));
                    await c.SendAsync(LiveWire.Request(LiveId.Request, (ulong)(h.Model.Head - 5), 0, true));
                    await c.PumpAsync(1000);
                    Assert.Empty(c.Of(LiveId.Piece));
                }
                await reading;
                Assert.Equal(0, h.Session.Stats.UploadedBytes);
            }
        }

        [Fact]
        public async Task AcceptsBitTorrentProtocolNameAndRejectsTheWrongInfoHash()
        {
            using (var h = new LiveHarness(Sim(), o => { o.EnableListener = true; }))
            {
                h.AddSource();
                await h.OpenAsync();
                var reading = h.ReadAsync(4);
                using (var c = new RawLiveClient())
                {
                    await c.ConnectAsync(h.Host.ListenPort, h.Model.InfoHash, aceName: false);
                    await c.PumpAsync(1500, () => c.RemoteExtend != null);
                    Assert.Equal("BitTorrent protocol", c.RemoteProtocol);        // the incoming one answers with the same name it received
                    Assert.NotNull(c.RemoteExtend);
                }
                using (var c2 = new RawLiveClient())
                {
                    bool failed = false;
                    try { await c2.ConnectAsync(h.Host.ListenPort, new byte[20]); await c2.PumpAsync(1000); failed = c2.Closed || c2.RemoteExtend == null; }
                    catch (Exception) { failed = true; }
                    Assert.True(failed);
                }
                await reading;
            }
        }

        // ---------- Faulty peers ----------

        public static IEnumerable<object[]> BadPeerScripts()
        {
            yield return new object[] { "bitfield before the EXTEND" };
            yield return new object[] { "tt distinto" };
            yield return new object[] { "id desconocido 17" };
            yield return new object[] { "choke with length 2" };
            yield return new object[] { "64 MiB message" };
            yield return new object[] { "extend with garbage" };
            yield return new object[] { "different infohash" };
            yield return new object[] { "strict invalid signature" };
            yield return new object[] { "piece with a nonexistent chunk" };
        }

        [Theory]
        [MemberData(nameof(BadPeerScripts))]
        public async Task BadPeersAreDroppedAndTheGoodOneKeepsPlaying(string script)
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim, o => { if (script == "strict invalid signature") o.Live.StrictRemoteSignature = true; }))
            {
                var good = h.AddSource();
                var ih = h.Model.InfoHash;
                Func<NetworkStream, Task> run;
                byte[] seed, pub;
                Ed25519.GenerateKeyPair(out seed, out pub);
                Func<string, byte[]> extend = tt =>
                {
                    var o = new LiveOptions { TransportType = tt };
                    return LiveExtend.BuildFrame(LiveExtend.BuildPayload(o, new LiveLocalState { Lsp = h.Model.Head }, 0, null, 1, seed, pub));
                };
                switch (script)
                {
                    case "bitfield before the EXTEND":
                        run = async s => { var f = LiveWire.Bitfield(5, 8, new byte[] { 0xff }, null, null); await s.WriteAsync(f, 0, f.Length); }; break;
                    case "tt distinto":
                        run = async s => { var f = extend("hls"); await s.WriteAsync(f, 0, f.Length); }; break;
                    case "id desconocido 17":
                        run = async s => { var f = extend("bt"); await s.WriteAsync(f, 0, f.Length); var u = new byte[] { 0, 0, 0, 1, 17 }; await s.WriteAsync(u, 0, u.Length); }; break;
                    case "choke with length 2":
                        run = async s => { var f = extend("bt"); await s.WriteAsync(f, 0, f.Length); var u = new byte[] { 0, 0, 0, 2, 0, 0 }; await s.WriteAsync(u, 0, u.Length); }; break;
                    case "64 MiB message":
                        run = async s => { var f = extend("bt"); await s.WriteAsync(f, 0, f.Length); var u = new byte[] { 4, 0, 0, 0, 7 }; await s.WriteAsync(u, 0, u.Length); }; break;
                    case "extend with garbage":
                        run = async s => { var g = new byte[] { 0, 0, 0, 6, 20, 0, (byte)'x', (byte)'x', (byte)'x', (byte)'x' }; await s.WriteAsync(g, 0, g.Length); }; break;
                    case "strict invalid signature":
                        run = async s =>
                        {
                            var f = extend("bt");
                            int at = LiveExtend.IndexOf(f, Encoding.ASCII.GetBytes("2:tt2:bt"), 0);
                            f[at + 6] = (byte)'x';                          // the signature no longer matches (and tt would also be different): the signature is checked first
                            // restore tt so that only the signature fails
                            f[at + 6] = (byte)'b';
                            int p = LiveExtend.IndexOf(f, Encoding.ASCII.GetBytes("7:node_id32:"), 0);
                            f[p + 12 + 3] ^= 0x01;                          // the public key is altered: the signature does not verify
                            await s.WriteAsync(f, 0, f.Length);
                        };
                        break;
                    case "piece with a nonexistent chunk":
                        run = async s =>
                        {
                            var f = extend("bt"); await s.WriteAsync(f, 0, f.Length);
                            var data = new byte[16384];
                            var pc = LiveWire.Piece((ulong)h.Model.Head, 1.0, 40, true, data, 0, data.Length);
                            await Task.Delay(300);
                            await s.WriteAsync(pc, 0, pc.Length);
                        };
                        break;
                    default:
                        run = s => Task.CompletedTask; break;
                }
                using (var evil = new RawLiveServer(ih, run, script == "different infohash"))
                {
                    h.Options.StaticPeers.Add(evil.EndPoint.ToString());
                    await h.OpenAsync();
                    var v = await h.ReadAsync(5);
                    Assert.True(v.Bytes > 200000, "the good peer must keep serving: " + h.Session.DebugInfo);
                    Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets);
                    Assert.True(evil.Connected, "the engine should have connected to the faulty peer");
                    Assert.True(evil.EngineClosedFirst, "the engine must close the connection to the faulty peer (" + script + ")");
                }
            }
        }

        // ---------- Peer discovery ----------

        [Fact]
        public async Task FindsPeersThroughTheDescriptorTrackerAndAnnouncesTheLiveInfoHash()
        {
            using (var tracker = new FakeHttpTracker())
            using (var h = new LiveHarness(Sim(), o => { o.EnableTrackers = true; }))
            {
                var src = h.AddSource();
                h.Options.StaticPeers.Clear();
                tracker.Peers.Add(src.EndPoint);
                h.Model.SetDiscovery(tracker.AnnounceUrl, null, null);
                // without StaticPeers: the only path is the tracker (AddSource adds the peer to StaticPeers inside OpenAsync)
                var sources = h.Sources.ToList(); h.Sources.Clear();
                await h.OpenAsync();
                h.Sources.AddRange(sources);
                var v = await h.ReadAsync(6);
                AssertPlays(v);
                var line = tracker.Requests.First();
                Assert.Contains("info_hash=" + HttpTracker.PercentEncode(h.Model.InfoHash), line);
                Assert.Contains("event=started", line);
                Assert.Contains("left=1", line);
            }
        }

        [Fact]
        public async Task FindsPeersThroughAMetaTrackerWithJson()
        {
            // meta-tracker: GET <url>?infohash=<hex>&cv=<version> -> {"trackers": [...], "startup_nodes": ["ip:port"], "interval": n}
            using (var h = new LiveHarness(Sim(), o => { o.EnableTrackers = true; }))
            {
                var src = h.AddSource();
                var requests = new List<string>();
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                var srv = Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                        var s = c.GetStream();
                        var sb = new StringBuilder(); var one = new byte[1];
                        while (!sb.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one, 0, 1) == 0) break; sb.Append((char)one[0]); }
                        lock (requests) requests.Add(sb.ToString().Split('\n')[0].Trim());
                        var body = Encoding.UTF8.GetBytes("{\"trackers\":[],\"startup_nodes\":[\"" + src.EndPoint + "\"],\"interval\":3600}");
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        await s.WriteAsync(head, 0, head.Length); await s.WriteAsync(body, 0, body.Length);
                        c.Close();
                    }
                });
                h.Model.SetDiscovery(null, new[] { "http://127.0.0.1:" + port + "/mt" }, null);
                var sources = h.Sources.ToList(); h.Sources.Clear();
                await h.OpenAsync();
                h.Sources.AddRange(sources);
                var v = await h.ReadAsync(6);
                l.Stop();
                AssertPlays(v);
                lock (requests) Assert.Contains("GET /mt?infohash=" + Hex.ToHex(h.Model.InfoHash) + "&cv=3022200 ", requests.First() + " ");
            }
        }

        [Fact]
        public async Task StartupNodesFromTheDescriptorAreUsedAsNormalPeers()
        {
            using (var h = new LiveHarness(Sim()))
            {
                var src = h.AddSource();
                h.Model.SetDiscovery(null, null, new[] { src.EndPoint.ToString() });
                var sources = h.Sources.ToList(); h.Sources.Clear();
                await h.OpenAsync();
                h.Sources.AddRange(sources);
                AssertPlays(await h.ReadAsync(5));
            }
        }

        [Fact]
        public async Task FindsPeersThroughTheDhtByInfoHashAndByContentId()
        {
            using (var node = new FakeDhtNode())
            using (var h = new LiveHarness(Sim(), o => { o.EnableDht = true; }))
            {
                var src = h.AddSource();
                node.Peers.Add(src.EndPoint);
                h.Options.DhtBootstrap = new List<string> { "127.0.0.1:" + node.Port };
                var sources = h.Sources.ToList(); h.Sources.Clear();
                string contentId = "2940120bf034db79a7f5849846ccea0255172eae";
                using (var meta = new FakeAceMetadataPeer { FullData = h.Model.TransportBytes })
                {
                    h.Options.StaticPeers.Add(meta.EndPoint.ToString());
                    h.Options.Resolver.WeakQuorum = 1;
                    h.Options.Resolver.EnableTsService = false;
                    await h.OpenAsync(AceLink.ContentId(contentId));
                    h.Sources.AddRange(sources);
                    AssertPlays(await h.ReadAsync(8));
                    var hashes = node.Hashes.Distinct().ToList();
                    Assert.Contains(Hex.ToHex(h.Model.InfoHash), hashes);              // by the descriptor's infohash
                    Assert.Contains(contentId, hashes);                                // and by the content_id (alias the engines announce)
                }
            }
        }

        [Fact]
        public async Task ContentIdIsResolvedByP2pAndThenPlayedEndToEnd()
        {
            string contentId = "54a4a28882d3cae1ca19de5d4b022ebea40351f9";
            using (var h = new LiveHarness(Sim(), o => { o.Resolver.EnableTsService = false; o.Resolver.WeakQuorum = 1; }))
            {
                var src = h.AddSource();
                using (var meta = new FakeAceMetadataPeer { FullData = h.Model.TransportBytes })
                {
                    h.Options.StaticPeers.Add(meta.EndPoint.ToString());
                    await h.OpenAsync(AceLink.Parse("acestream://" + contentId));
                    Assert.NotNull(h.Opened.Transport);
                    Assert.True(h.Opened.Transport.IsLive);
                    AssertPlays(await h.ReadAsync(6));
                    Assert.Equal(1, meta.FullRequests >= 1 ? 1 : 0);
                    // get_media_files of that content_id: it comes from the cache
                    var mf = await h.Host.GetMediaFilesAsync(AceLink.Parse(contentId), new AceEngine.Transport.MediaFilesOptions(), CancellationToken.None);
                    Assert.Equal("live", mf.Type); Assert.Equal("bt", mf.TransportType);
                    Assert.Equal(Hex.ToHex(h.Model.InfoHash), mf.Infohash);
                }
            }
        }

        private static void AssertPlays(SimStreamVerifier v)
        {
            Assert.True(v.Bytes > 300000, "bytes " + v.Bytes);
            Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets); Assert.Empty(v.Gaps);
        }
    }
}
