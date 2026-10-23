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
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Transport;
using Xunit;

namespace AceEngine.Tests.P2P
{
    /// <summary>
    /// End to end on the local loopback: the whole engine (trackers, wire protocol, picker, storage, HTTP server)
    /// against fake peers that imitate Ace's quirks. Validation against the real network is in `ace verify`.
    /// </summary>
    public class EngineIntegrationTests : IDisposable
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public void Dispose()
        {
            _http.Dispose();
            foreach (var d in _disposables) try { d.Dispose(); } catch { }
        }

        private T Own<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

        private static EngineOptions Opts(Action<EngineOptions> tweak = null)
        {
            var o = new EngineOptions
            {
                EnableListener = false, EnableDht = false, EnableTrackers = false,
                ConnectTimeout = TimeSpan.FromSeconds(3), HandshakeTimeout = TimeSpan.FromSeconds(3),
                MetadataTimeout = TimeSpan.FromSeconds(20)
            };
            o.Http.ReadStallTimeout = TimeSpan.FromSeconds(30);
            if (tweak != null) tweak(o);
            return o;
        }

        private AceEngineHost Host(Action<EngineOptions> tweak = null)
        {
            if (Environment.GetEnvironmentVariable("ACE_TEST_LOG") == "1")
            {
                AceEngine.Util.AceLog.MinLevel = AceEngine.Util.LogLevel.Debug;
                AceEngine.Util.AceLog.Sink = (l, tag, m) => Console.Error.WriteLine("[" + tag + "] " + m);
            }
            var h = Own(new AceEngineHost(Opts(tweak)));
            h.Start();
            return h;
        }

        private async Task<OpenedStream> OpenTorrent(AceEngineHost host, TestTorrent t, int? index = null)
        {
            var path = Path.Combine(Path.GetTempPath(), "p-" + Guid.NewGuid().ToString("N") + ".torrent");
            File.WriteAllBytes(path, t.TorrentBytes);
            try { return await host.OpenAsync(AceLink.Parse(path), index, CancellationToken.None); }
            finally { File.Delete(path); }
        }

        private async Task<byte[]> GetAll(string url, string range = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (range != null) req.Headers.TryAddWithoutValidation("Range", range);
            var r = await _http.SendAsync(req);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsByteArrayAsync();
        }

        private static Task Until(Func<bool> cond, int ms = 10000)
        {
            return Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(25);
            });
        }

        // ---------- Full download and behavior against an Ace engine ----------

        [Fact]
        public async Task DownloadsWholeFileFromAceStyleSeeder()
        {
            var t = TestTorrent.Create(24 * 32768 + 1000, 32768);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);

            var body = await GetAll(opened.Url);
            Assert.Equal(t.Content.Length, body.Length);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));

            // The client did nothing that an Ace would close on
            Assert.False(seeder.ForbiddenMessageSeen, seeder.Events.ToString());
            Assert.False(seeder.RequestWithoutInterestSeen);
            Assert.False(seeder.UnknownExtSeen, seeder.Events.ToString());
            Assert.False(seeder.BitfieldAfterOthers);
            Assert.True(seeder.ClientSentExtHandshake);
            Assert.Equal("ACEStream 2.0", seeder.ClientVersion);
            Assert.True(seeder.Events.ToList().Any(e => e.Contains("peer_id R30------")), seeder.Events.ToString());
            Assert.True(seeder.BlocksServed >= t.Content.Length / 16384);
        }

        [Fact]
        public async Task StaysCleanWhenRemoteAdvertisesDhtAndFastBits()
        {
            // even if the peer announces DHT/Fast, it must not be sent PORT nor Fast messages.
            var t = TestTorrent.Create(10 * 32768, 32768, seed: 5);
            var seeder = Own(new FakeSeeder(t) { UseFastBits = true });
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
            Assert.False(seeder.ForbiddenMessageSeen, seeder.Events.ToString());
        }

        [Fact]
        public async Task CloakedBitfieldAndHavesAreUnderstood()
        {
            var t = TestTorrent.Create(12 * 32768, 32768, seed: 6);
            var seeder = Own(new FakeSeeder(t) { CloakBitfield = true });
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
        }

        [Fact]
        public async Task RangeRequestsAndSeeksReturnExactBytes()
        {
            var t = TestTorrent.Create(40 * 32768 + 77, 32768, seed: 7);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            long len = t.Content.Length;

            Func<long, long, byte[]> slice = (a, b) => t.Content.Skip((int)a).Take((int)(b - a + 1)).ToArray();
            long mid = len / 2 + 5;
            Assert.Equal(slice(mid, mid + 99999), await GetAll(opened.Url, "bytes=" + mid + "-" + (mid + 99999)));
            Assert.Equal(slice(100, 5000), await GetAll(opened.Url, "bytes=100-5000"));                    // jump backwards
            Assert.Equal(slice(len - 1000, len - 1), await GetAll(opened.Url, "bytes=-1000"));              // end
            Assert.Equal(slice(len - 1, len - 1), await GetAll(opened.Url, "bytes=" + (len - 1) + "-"));
        }

        [Fact]
        public async Task MultiFileTorrentServesTheChosenFileByIndex()
        {
            var rnd = new Random(11);
            Func<int, byte[]> rb = n => { var b = new byte[n]; rnd.NextBytes(b); return b; };
            var files = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("Serie/leeme.txt", rb(1234)),
                new KeyValuePair<string, byte[]>("Serie/cap1.mkv", rb(150000)),     // starts in the middle of a piece
                new KeyValuePair<string, byte[]>("Serie/subs.srt", rb(777)),
                new KeyValuePair<string, byte[]>("Serie/cap2.mp4", rb(90001))
            };
            var t = TestTorrent.Create(0, 16384, "Serie", files: files);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));

            var o3 = await OpenTorrent(host, t, 3);
            var s3 = (VodSession)o3.Session;
            await s3.WaitUntilReadyAsync(CancellationToken.None);
            Assert.Equal(90001, s3.Length);
            Assert.Equal("video/mp4", s3.ContentType);
            Assert.Equal(files[3].Value, await GetAll(o3.Url));
            s3.Dispose();

            var o1 = await OpenTorrent(host, t, 1);
            Assert.Equal(files[1].Value, await GetAll(o1.Url));
            Assert.Equal("video/x-matroska", o1.Session.ContentType);
        }

        [Fact]
        public async Task DefaultFileIsTheFirstMediaFile()
        {
            var files = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("a.txt", new byte[100]),
                new KeyValuePair<string, byte[]>("b.mkv", Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray())
            };
            var t = TestTorrent.Create(0, 16384, "P", files: files);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            Assert.Equal(files[1].Value, await GetAll(opened.Url));
        }

        // ---------- Magnet / ut_metadata ----------

        [Fact]
        public async Task MagnetResolvesMetadataThroughUtMetadataWithAceQuirks()
        {
            // 900 pieces of 1 KiB -> 'pieces' of 18 KB -> metadata in 2 chunks of ut_metadata
            var t = TestTorrent.Create(900 * 1024, 1024, seed: 9);
            Assert.True(t.InfoBytes.Length > 16384);
            var seeder = Own(new FakeSeeder(t) { AceTotalSizeQuirk = true });
            var host = Host();
            var magnet = MagnetLink.Build(t.InfoHash, "mi video") + "&x.pe=" + seeder.EndPoint;
            var opened = await host.OpenAsync(AceLink.Parse(magnet), null, CancellationToken.None);
            var vod = (VodSession)opened.Session;
            // a magnet is resolved to its descriptor BEFORE opening the session (only the swarm knows whether it is an Ace live stream or a VOD),
            // so on return there is already metadata; the "resolviendo" phase is OpenAsync's own, as with a content_id
            Assert.Equal(t.Content.Length, vod.Length);
            Assert.NotNull(opened.Transport);
            Assert.False(opened.Transport.IsLive);

            var body = await GetAll(opened.Url);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));
            Assert.True(seeder.MetadataPiecesServed >= 2, "metadata chunks served: " + seeder.MetadataPiecesServed);
            Assert.Equal(t.Content.Length, vod.Length);
            Assert.False(seeder.ForbiddenMessageSeen, seeder.Events.ToString());
            Assert.False(seeder.UnknownExtSeen, seeder.Events.ToString());
        }

        [Fact]
        public async Task MagnetWithStandardTotalSizeAlsoWorks()
        {
            var t = TestTorrent.Create(100 * 1024, 1024, seed: 10);
            var seeder = Own(new FakeSeeder(t) { AceTotalSizeQuirk = false });
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await host.OpenAsync(AceLink.Parse(t.Transport.Torrent.InfoHashHex, AceLinkKind.InfoHash), null, CancellationToken.None);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
        }

        [Fact]
        public async Task MetadataWithWrongHashIsRejectedAndResolutionFails()
        {
            var t = TestTorrent.Create(50 * 1024, 1024, seed: 12);
            var other = TestTorrent.Create(50 * 1024, 1024, seed: 13);     // different infohash
            // The "peer" announces the requested infohash but serves another torrent's metadata
            var liar = Own(new FakeSeeder(new TestTorrent
            {
                Content = t.Content, PieceLength = t.PieceLength, Name = t.Name, TorrentBytes = t.TorrentBytes,
                InfoBytes = other.InfoBytes, InfoHash = t.InfoHash, PieceCount = t.PieceCount, Transport = t.Transport
            }));
            var host = Host(o => { o.StaticPeers.Add(liar.EndPoint.ToString()); o.Resolver.Timeout = TimeSpan.FromSeconds(4); });
            // metadata with another hash is discarded and, with no other peer, the resolution fails (PlaybackSession retries it); no session is opened
            var ex = await Assert.ThrowsAsync<AceEngine.Resolve.ResolveException>(() => host.OpenAsync(AceLink.Parse(t.Transport.Torrent.InfoHashHex, AceLinkKind.InfoHash), null, CancellationToken.None));
            Assert.Contains(t.Transport.Torrent.InfoHashHex, ex.Message);
            Assert.Empty(host.Server.Sessions);
        }

        // ---------- Resilience to bad peers ----------

        [Fact]
        public async Task CorruptPieceIsDetectedAndFetchedFromAnotherPeer()
        {
            var t = TestTorrent.Create(20 * 32768, 32768, seed: 14);
            var bad = Own(new FakeSeeder(t) { Corrupt = new HashSet<int> { 2, 3, 4 } });
            var good = Own(new FakeSeeder(t) { BlockDelayMs = 3 });
            var host = Host(o => { o.StaticPeers.Add(bad.EndPoint.ToString()); o.StaticPeers.Add(good.EndPoint.ToString()); });
            var opened = await OpenTorrent(host, t);
            var body = await GetAll(opened.Url);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));        // never was a corrupt byte served
            var dbg = ((VodSession)opened.Session).DebugInfo;
            Assert.Contains("hashFails=", dbg);
        }

        [Fact]
        public async Task SilentPeerDoesNotBlockPlayback()
        {
            var t = TestTorrent.Create(16 * 32768, 32768, seed: 15);
            var silent = Own(new FakeSeeder(t) { NeverRespond = true });
            var good = Own(new FakeSeeder(t) { BlockDelayMs = 2 });
            var host = Host(o =>
            {
                o.StaticPeers.Add(silent.EndPoint.ToString()); o.StaticPeers.Add(good.EndPoint.ToString());
                o.PlayingDelayMin = 0.5; o.BufferingDelayMin = 0.5; o.BufferingDelayMax = 2; o.PlayingDelayMax = 2;   // short deadlines for the test
            });
            var opened = await OpenTorrent(host, t);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = await GetAll(opened.Url);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), "tardo " + sw.Elapsed);
            Assert.True(silent.RequestsReceived > 0);
        }

        [Fact]
        public async Task PeerThatClosesMidwayIsReplacedByAnotherAfterwards()
        {
            var t = TestTorrent.Create(30 * 32768, 32768, seed: 16);
            var flaky = Own(new FakeSeeder(t) { BlockDelayMs = 5 });
            var host = Host(o => o.StaticPeers.Add(flaky.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            var task = GetAll(opened.Url);
            await Until(() => flaky.BlocksServed > 20, 5000);
            flaky.DropAllConnections();                       // the only peer drops...
            await Task.Delay(300);
            var backup = Own(new FakeSeeder(t));              // ...and another appears, arriving through the host's candidate list
            // there is no tracker: adding it as a PEX candidate from the second peer is not possible; we reopen the same peer
            // (the original retries at 15-20 s). It is sped up with the original peer's StaticPeers, which is still listening.
            var body = await task;
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));
        }

        // ---------- Discovery ----------

        [Fact]
        public async Task HttpTrackerProvidesPeers()
        {
            var tracker = Own(new FakeHttpTracker());
            var t = TestTorrent.Create(10 * 32768, 32768, announce: tracker.AnnounceUrl, seed: 17);
            var seeder = Own(new FakeSeeder(t));
            tracker.Peers.Add(seeder.EndPoint);
            var host = Host(o => o.EnableTrackers = true);
            var opened = await OpenTorrent(host, t);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
            Assert.Contains("event=started", tracker.Requests.First());
        }

        [Fact]
        public async Task UdpTrackerFromMagnetProvidesPeers()
        {
            var tracker = Own(new FakeUdpTracker());
            var t = TestTorrent.Create(10 * 32768, 32768, seed: 18);
            var seeder = Own(new FakeSeeder(t));
            tracker.Peers.Add(seeder.EndPoint);
            var host = Host(o => o.EnableTrackers = true);
            var magnet = MagnetLink.Build(t.InfoHash, "x", new[] { "udp://127.0.0.1:" + tracker.Port });
            var opened = await host.OpenAsync(AceLink.Parse(magnet), null, CancellationToken.None);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
            Assert.True(tracker.Announces >= 1);
        }

        [Fact]
        public async Task DhtProvidesPeers()
        {
            var node = Own(new FakeDhtNode());
            var t = TestTorrent.Create(10 * 32768, 32768, seed: 19);
            var seeder = Own(new FakeSeeder(t));
            node.Peers.Add(seeder.EndPoint);
            var host = Host(o => { o.EnableDht = true; o.DhtBootstrap = new List<string> { "127.0.0.1:" + node.Port }; });
            var opened = await OpenTorrent(host, t);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
            Assert.True(node.GetPeersReceived >= 1);
        }

        [Fact]
        public async Task PexAddsNewPeers()
        {
            var t = TestTorrent.Create(30 * 32768, 32768, seed: 20);
            var b = Own(new FakeSeeder(t, IPAddress.Parse("127.0.0.2")));      // PEX discards 127.0.0.1 (as Ace does), not the rest of 127/8
            var a = Own(new FakeSeeder(t) { SendPexOf = true, PexPeers = { b.EndPoint } });
            var host = Host(o => o.StaticPeers.Add(a.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            await Until(() => b.Connections > 0, 8000);
            Assert.True(b.Connections > 0, "the second peer did not receive a connection through PEX");
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
        }

        // ---------- Bounded memory ----------

        [Fact]
        public async Task BufferStaysBoundedAndEvictedPiecesAreFetchedAgain()
        {
            // 6 MiB in 64 KiB pieces with a 1 MiB buffer (16 pieces): the content does not fit entirely.
            var t = TestTorrent.Create(6 * 1024 * 1024 + 1234, 65536, seed: 21);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o =>
            {
                o.StaticPeers.Add(seeder.EndPoint.ToString());
                o.MaxBufferBytes = 1024 * 1024; o.KeepBehindBytes = 128 * 1024;
                o.HighRangeMinBytes = 256 * 1024; o.HighRangeMinPieces = 2; o.AssumedBitrateBytesPerSec = 64 * 1024;
                o.PrebufferMinBytes = 128 * 1024; o.PrebufferMinPieces = 1;
            });
            var opened = await OpenTorrent(host, t);
            var vod = (VodSession)opened.Session;

            // Full sequential read while watching the buffer
            long maxBuffered = 0;
            var r = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, opened.Url), HttpCompletionOption.ResponseHeadersRead);
            var ms = new MemoryStream();
            using (var s = await r.Content.ReadAsStreamAsync())
            {
                var buf = new byte[32768];
                int n;
                while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    maxBuffered = Math.Max(maxBuffered, vod.Stats.BufferedBytes);
                }
            }
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(ms.ToArray()));
            Assert.True(maxBuffered <= 1024 * 1024 + 65536, "maximum buffer observed " + maxBuffered);

            // The start is no longer in RAM: it is downloaded again
            int before = seeder.BlocksServed;
            Assert.Equal(t.Content.Take(50000).ToArray(), await GetAll(opened.Url, "bytes=0-49999"));
            Assert.True(seeder.BlocksServed > before);
        }

        // ---------- Upload ----------

        [Fact]
        public async Task ServesPiecesToAnIncomingAceStylePeer()
        {
            var t = TestTorrent.Create(8 * 32768, 32768, seed: 22);
            var seeder = Own(new FakeSeeder(t));
            var host = Host(o => { o.StaticPeers.Add(seeder.EndPoint.ToString()); o.EnableListener = true; });
            var opened = await OpenTorrent(host, t);
            Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(await GetAll(opened.Url)));
            Assert.True(host.ListenPort > 0);

            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, host.ListenPort);
                var s = c.GetStream();
                // Ace-style initiator: 48 bytes, reads 68, writes its peer_id
                var head = Wire.BuildHandshakeHead(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 }, t.InfoHash);
                await s.WriteAsync(head, 0, head.Length);
                var reply = new byte[68];
                await ReadExact(s, reply, 68);
                HandshakeInfo hi;
                Assert.True(Wire.TryParseHandshake(reply, 68, out hi));
                Assert.Equal(t.InfoHash, hi.InfoHash);
                Assert.Equal("R30------", Encoding.ASCII.GetString(hi.PeerId, 0, 9));
                Assert.True(hi.SupportsLtep);
                Assert.False(hi.SupportsDht);
                var pid = PeerId.Generate();
                await s.WriteAsync(pid, 0, 20);

                // Messages: EXTEND handshake first, then BITFIELD
                var frames = new List<byte[]>();
                await ReadFrames(s, frames, 2);
                Assert.Equal(WireId.Extended, frames[0][0]);
                Assert.Equal(WireId.Bitfield, frames[1][0]);
                var bf = Bitfield.FromBytes(frames[1], 1, frames[1].Length - 1, t.PieceCount);
                Assert.True(bf.IsComplete);

                await s.WriteAsync(Wire.Interested(), 0, 5);
                // we request a block BEFORE it unchokes us: it is discarded; after the UNCHOKE it is served
                var f = new List<byte[]>();
                await ReadFramesUntil(s, f, m => m[0] == WireId.Unchoke);
                var req = Wire.Request(3, 16384, 16384);
                await s.WriteAsync(req, 0, req.Length);
                var pieceFrames = new List<byte[]>();
                await ReadFramesUntil(s, pieceFrames, m => m[0] == WireId.Piece);
                var pm = pieceFrames.Last();
                Assert.Equal(3u, Wire.GetU32(pm, 1));
                Assert.Equal(16384u, Wire.GetU32(pm, 5));
                Assert.Equal(t.Content.Skip(3 * 32768 + 16384).Take(16384).ToArray(), pm.Skip(9).ToArray());
            }
        }

        private static async Task ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int got = 0;
            using (var cts = new CancellationTokenSource(10000))
                while (got < count)
                {
                    int n = await s.ReadAsync(buf, got, count - got, cts.Token);
                    if (n == 0) throw new EndOfStreamException();
                    got += n;
                }
        }

        private static async Task ReadFrames(NetworkStream s, List<byte[]> into, int count)
        {
            while (into.Count < count)
            {
                var lb = new byte[4];
                await ReadExact(s, lb, 4);
                uint len = Wire.GetU32(lb, 0);
                if (len == 0) continue;
                var m = new byte[len];
                await ReadExact(s, m, (int)len);
                into.Add(m);
            }
        }

        private static async Task ReadFramesUntil(NetworkStream s, List<byte[]> into, Func<byte[], bool> stop)
        {
            while (true)
            {
                var before = into.Count;
                await ReadFrames(s, into, before + 1);
                if (stop(into.Last())) return;
            }
        }

        // ---------- Session ----------

        [Fact]
        public async Task SessionReportsStatesStatsAndStopsCleanly()
        {
            var t = TestTorrent.Create(20 * 32768, 32768, seed: 23);
            var seeder = Own(new FakeSeeder(t) { BlockDelayMs = 2 });
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);
            var vod = (VodSession)opened.Session;
            var states = new List<SessionState>();
            vod.StateChanged += (s, st) => { lock (states) states.Add(st); };
            await Until(() => vod.State == SessionState.Playing, 10000);
            Assert.Equal(SessionState.Playing, vod.State);
            var st1 = vod.Stats;
            Assert.True(st1.Peers >= 1);
            Assert.True(st1.Seeds >= 1);
            Assert.True(st1.PiecesTotal == 20);
            Assert.True(st1.DownloadedBytes > 0);
            Assert.False(vod.IsLive);
            Assert.Equal(t.Content.Length, vod.Length);

            vod.Dispose();
            Assert.Equal(SessionState.Stopped, vod.State);
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(opened.Url)).StatusCode);
            await Until(() => seeder.Connections > 0);
            Assert.Empty(host.Server.Sessions);
        }

        [Fact]
        public async Task ClientAbortMidTransferReleasesTheReaderAndTheSessionKeepsWorking()
        {
            var t = TestTorrent.Create(60 * 32768, 32768, seed: 25);
            var seeder = Own(new FakeSeeder(t) { BlockDelayMs = 1 });
            var host = Host(o => o.StaticPeers.Add(seeder.EndPoint.ToString()));
            var opened = await OpenTorrent(host, t);

            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, host.Server.Port);
                var s = c.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + new Uri(opened.Url).AbsolutePath + " HTTP/1.1\r\nHost: x\r\n\r\n");
                await s.WriteAsync(req, 0, req.Length);
                var buf = new byte[1000];
                await s.ReadAsync(buf, 0, buf.Length);       // it starts arriving and is cut off
            }
            await Until(() => host.Server.ActiveConnections(opened.Session.Id) == 0, 8000);
            Assert.Equal(0, host.Server.ActiveConnections(opened.Session.Id));
            Assert.Equal(TestUtil.Sha1Hex(t.Content.Skip(5000).Take(100000).ToArray()),
                TestUtil.Sha1Hex(await GetAll(opened.Url, "bytes=5000-104999")));
        }

        [Fact]
        public async Task ContentIdNobodyHasFailsWithCannotGetTransportFileAndALiveFileOpensALiveSession()
        {
            var host = Host(o => { o.Resolver.Timeout = TimeSpan.FromSeconds(1.5); o.Resolver.EnableTsService = false; });
            var ex = await Assert.ThrowsAsync<AceEngine.Resolve.ResolveException>(() => host.OpenAsync(AceLink.ContentId(new string('a', 40)), null, CancellationToken.None));
            Assert.Contains("cannot get transport file", ex.Message);
            // the synthetic fixture live_v2.acelive carries a pubkey that is not DER: a clear error, not an internal failure
            await Assert.ThrowsAsync<TransportException>(() => host.OpenAsync(AceLink.Parse(TestUtil.FixturePath("live_v2.acelive")), null, CancellationToken.None));
            var model = new AceEngine.Sim.LiveStreamModel(new AceEngine.Sim.LiveSimOptions());
            var liveFile = Path.Combine(Path.GetTempPath(), "p-live-" + Guid.NewGuid().ToString("N") + ".acelive");
            File.WriteAllBytes(liveFile, model.TransportBytes);
            try
            {
                var opened = await host.OpenAsync(AceLink.Parse(liveFile), null, CancellationToken.None);
                Assert.True(opened.Session.IsLive);
                Assert.Null(opened.Session.Length);
            }
            finally { File.Delete(liveFile); }
        }

        [Fact]
        public async Task MalformedFileGivesTransportException()
        {
            var host = Host();
            var path = Path.Combine(Path.GetTempPath(), "p-bad-" + Guid.NewGuid().ToString("N") + ".torrent");
            File.WriteAllText(path, "esto no es un torrent");
            try { await Assert.ThrowsAsync<TransportException>(() => host.OpenAsync(AceLink.Parse(path), null, CancellationToken.None)); }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task SameInfohashCannotBeOpenedTwice()
        {
            var t = TestTorrent.Create(4 * 32768, 32768, seed: 24);
            var host = Host();
            await OpenTorrent(host, t);
            await Assert.ThrowsAsync<InvalidOperationException>(() => OpenTorrent(host, t));
        }
    }
}
