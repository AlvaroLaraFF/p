using System;
using System.Collections.Generic;
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
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Resolve;
using AceEngine.Tests.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Probe
{
    /// <summary>Fake UDP tracker with scrape (action 2) and announce (action 1).</summary>
    internal sealed class FakeScrapeUdpTracker : IDisposable
    {
        private readonly UdpClient _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly Dictionary<string, int[]> Swarms = new Dictionary<string, int[]>();      // infohash hex -> {seeders, completed, leechers}
        public List<IPEndPoint> AnnouncePeers = new List<IPEndPoint>();
        public int Connects, Scrapes, Announces, DropFirst;
        public bool ErrorOnScrape;
        private int _seen;
        public int Port { get; private set; }
        public string Url { get { return "udp://127.0.0.1:" + Port + "/announce"; } }

        public FakeScrapeUdpTracker()
        {
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(); } catch { return; }
                if (Interlocked.Increment(ref _seen) <= DropFirst) continue;
                var b = r.Buffer;
                if (b.Length >= 16 && Wire.GetU64(b, 0) == 0x41727101980UL && Wire.GetU32(b, 8) == 0)
                {
                    Interlocked.Increment(ref Connects);
                    var resp = new byte[16];
                    Wire.PutU32(resp, 0, 0); Wire.PutU32(resp, 4, Wire.GetU32(b, 12)); Wire.PutU64(resp, 8, 0x0102030405060708UL);
                    await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
                else if (b.Length >= 16 && Wire.GetU64(b, 0) == 0x0102030405060708UL && Wire.GetU32(b, 8) == 2)
                {
                    Interlocked.Increment(ref Scrapes);
                    int n = (b.Length - 16) / 20;
                    if (ErrorOnScrape)
                    {
                        var e = new byte[8 + 4]; Wire.PutU32(e, 0, 3); Wire.PutU32(e, 4, Wire.GetU32(b, 12)); Encoding.ASCII.GetBytes("nope").CopyTo(e, 8);
                        await _udp.SendAsync(e, e.Length, r.RemoteEndPoint);
                        continue;
                    }
                    var resp = new byte[8 + 12 * n];
                    Wire.PutU32(resp, 0, 2); Wire.PutU32(resp, 4, Wire.GetU32(b, 12));
                    for (int i = 0; i < n; i++)
                    {
                        var ih = Hex.ToHex(b, 16 + 20 * i, 20);
                        int[] v; if (!Swarms.TryGetValue(ih, out v)) v = new[] { 0, 0, 0 };
                        Wire.PutU32(resp, 8 + 12 * i, (uint)v[0]); Wire.PutU32(resp, 12 + 12 * i, (uint)v[1]); Wire.PutU32(resp, 16 + 12 * i, (uint)v[2]);
                    }
                    await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
                else if (b.Length >= 98 && Wire.GetU64(b, 0) == 0x0102030405060708UL && Wire.GetU32(b, 8) == 1)
                {
                    Interlocked.Increment(ref Announces);
                    var resp = new byte[20 + AnnouncePeers.Count * 6];
                    Wire.PutU32(resp, 0, 1); Wire.PutU32(resp, 4, Wire.GetU32(b, 12)); Wire.PutU32(resp, 8, 1800);
                    int o = 20;
                    foreach (var p in AnnouncePeers) { Buffer.BlockCopy(p.Address.GetAddressBytes(), 0, resp, o, 4); resp[o + 4] = (byte)(p.Port >> 8); resp[o + 5] = (byte)p.Port; o += 6; }
                    await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
            }
        }

        public void Dispose() { try { _udp.Dispose(); } catch { } }
    }

    public class ScrapeBytesTests
    {
        [Theory]
        [InlineData("http://t.test/announce", "http://t.test/scrape")]
        [InlineData("http://t.test:6969/announce", "http://t.test:6969/scrape")]
        [InlineData("https://t.test/x/announce.php", "https://t.test/x/scrape.php")]
        [InlineData("http://t.test/announce?passkey=abc", "http://t.test/scrape?passkey=abc")]
        [InlineData("http://t.test/a/b/announce/", null)]
        [InlineData("http://t.test/tracker", null)]
        [InlineData("udp://t.test:1/announce", null)]
        [InlineData("no es una url", null)]
        public void AnnounceToScrapeUrl(string announce, string scrape)
        {
            Assert.Equal(scrape, HttpScrape.ScrapeUrl(announce));
        }

        [Fact]
        public void ScrapeUrlCarriesTheRawInfoHashPercentEncoded()
        {
            var ih = TestUtil.FromHex("00ff10205a7e7f808182838485868788899a9b9c");
            var url = HttpScrape.BuildUrl("http://t.test/announce?k=v", ih);
            Assert.StartsWith("http://t.test/scrape?k=v&info_hash=%00%FF%10%20", url);
            Assert.EndsWith("%9A%9B%9C", url);
        }

        private static byte[] Resp(byte[] ih, long complete, long incomplete, long? mri = null, bool withFile = true, string fail = null)
        {
            var d = new BDict();
            if (fail != null) { d.Add("failure reason", BValue.Str(fail)); return BencodeWriter.Encode(d); }
            var files = new BDict();
            if (withFile)
            {
                var f = new BDict();
                f.Add("complete", BValue.Int(complete)); f.Add("downloaded", BValue.Int(5)); f.Add("incomplete", BValue.Int(incomplete));
                files.Add(BValue.Bytes(ih) is BString k ? k : null, f);
            }
            d.Add("files", files);
            if (mri.HasValue) { var fl = new BDict(); fl.Add("min_request_interval", BValue.Int(mri.Value)); d.Add("flags", fl); }
            return BencodeWriter.Encode(d);
        }

        [Fact]
        public void ParsesHttpScrapeResponses()
        {
            var ih = TestUtil.FromHex("ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00");           // non-UTF-8 binary key in the 'files' dictionary
            var ok = HttpScrape.ParseResponse(Resp(ih, 12, 34), ih);
            Assert.Equal(ScrapeStatus.Ok, ok.Status); Assert.Equal(12, ok.Seeders); Assert.Equal(34, ok.Leechers); Assert.Equal(5, ok.Completed);
            var neg = HttpScrape.ParseResponse(Resp(ih, -3, -7), ih);                         // negatives are converted to 0
            Assert.Equal(ScrapeStatus.Ok, neg.Status); Assert.Equal(0, neg.Seeders); Assert.Equal(0, neg.Leechers);
            var other = new byte[20];
            Assert.Equal(ScrapeStatus.Invalid, HttpScrape.ParseResponse(Resp(ih, 1, 1), other).Status);          // (-2,-2): the infohash is not there
            var wait = HttpScrape.ParseResponse(Resp(ih, 0, 0, 900, withFile: false), ih);
            Assert.Equal(ScrapeStatus.Wait, wait.Status); Assert.Equal(900, wait.MinRequestInterval);              // (-3,-3)
            Assert.Equal(ScrapeStatus.Invalid, HttpScrape.ParseResponse(Resp(ih, 0, 0, fail: "no scrape"), ih).Status);
            Assert.Equal(ScrapeStatus.Invalid, HttpScrape.ParseResponse(Encoding.ASCII.GetBytes("<html>"), ih).Status);
            Assert.Equal(ScrapeStatus.Invalid, HttpScrape.ParseResponse(Encoding.ASCII.GetBytes("li1ee"), ih).Status);
        }

        [Fact]
        public void UdpScrapePacketLayoutsAndParsing()
        {
            var a = new byte[20]; a[0] = 0xaa; var b = new byte[20]; b[19] = 0xbb;
            var req = UdpScrape.BuildScrape(0x1122334455667788UL, 0xdeadbeef, new[] { a, b });
            Assert.Equal(16 + 40, req.Length);
            Assert.Equal("1122334455667788" + "00000002" + "deadbeef", TestUtil.ToHex(req.Take(16).ToArray()));
            Assert.Equal(a, req.Skip(16).Take(20).ToArray()); Assert.Equal(b, req.Skip(36).Take(20).ToArray());
            Assert.Throws<ArgumentException>(() => UdpScrape.BuildScrape(1, 1, new List<byte[]>()));
            Assert.Throws<ArgumentException>(() => UdpScrape.BuildScrape(1, 1, Enumerable.Range(0, 75).Select(i => new byte[20]).ToList()));

            // response: action 2, tid, [seeders, completed, leechers] x 2
            var resp = TestUtil.FromHex("00000002" + "deadbeef" + "0000000a" + "00000064" + "00000003" + "00000000" + "00000001" + "ffffffff");
            List<ScrapeResult> r; string err;
            Assert.True(UdpScrape.ParseScrape(resp, 0xdeadbeef, 2, out r, out err));
            Assert.Equal(10, r[0].Seeders); Assert.Equal(100, r[0].Completed); Assert.Equal(3, r[0].Leechers);
            Assert.Equal(0, r[1].Seeders); Assert.Equal(int.MaxValue, r[1].Leechers);
            Assert.False(UdpScrape.ParseScrape(resp, 0xdeadbef0, 2, out r, out err));               // different tid
            Assert.False(UdpScrape.ParseScrape(resp.Take(20).ToArray(), 0xdeadbeef, 2, out r, out err));          // short
            var errMsg = TestUtil.FromHex("00000003" + "deadbeef").Concat(Encoding.ASCII.GetBytes("prohibido")).ToArray();
            Assert.False(UdpScrape.ParseScrape(errMsg, 0xdeadbeef, 1, out r, out err));
            Assert.Equal("prohibido", err);
        }
    }

    public class ScrapeNetworkTests
    {
        private static readonly byte[] Ih = TestUtil.FromHex("08ada5a7a6183aae1e09d831df6748d566095a10");

        [Fact]
        public async Task UdpScrapeAgainstAFakeTracker()
        {
            using (var t = new FakeScrapeUdpTracker())
            {
                t.Swarms[Hex.ToHex(Ih)] = new[] { 174, 9000, 47 };
                var r = await UdpScrape.ScrapeAsync(t.Url, Ih, new[] { 2, 2 }, CancellationToken.None);
                Assert.Equal(ScrapeStatus.Ok, r.Status); Assert.Equal(174, r.Seeders); Assert.Equal(47, r.Leechers);
                Assert.Equal(1, t.Connects); Assert.Equal(1, t.Scrapes);
                // several infohashes in one request
                var other = new byte[20]; other[0] = 9;
                t.Swarms[Hex.ToHex(other)] = new[] { 1, 2, 3 };
                var list = await UdpScrape.ScrapeAsync("127.0.0.1", t.Port, "x", new[] { Ih, other }, new[] { 2 }, CancellationToken.None);
                Assert.Equal(new[] { 174, 1 }, list.Select(x => x.Seeders).ToArray());
            }
        }

        [Fact]
        public async Task UdpScrapeRetriesOnceAndThenGivesUp()
        {
            using (var t = new FakeScrapeUdpTracker { DropFirst = 1 })
            {
                t.Swarms[Hex.ToHex(Ih)] = new[] { 5, 0, 6 };
                var r = await UdpScrape.ScrapeAsync(t.Url, Ih, new[] { 1, 3 }, CancellationToken.None);       // the first connect is lost: the second attempt works
                Assert.Equal(ScrapeStatus.Ok, r.Status); Assert.Equal(5, r.Seeders);
            }
            using (var t = new FakeScrapeUdpTracker { DropFirst = 1000 })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await UdpScrape.ScrapeAsync(t.Url, Ih, new[] { 1, 1 }, CancellationToken.None);       // at most 2 attempts in total
                Assert.Equal(ScrapeStatus.NetworkError, r.Status);
                Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 4.5);
            }
            using (var t = new FakeScrapeUdpTracker { ErrorOnScrape = true })
            {
                var r = await UdpScrape.ScrapeAsync(t.Url, Ih, new[] { 2 }, CancellationToken.None);
                Assert.Equal(ScrapeStatus.Invalid, r.Status); Assert.Equal("nope", r.Error);
            }
            var u = await UdpScrape.ScrapeAsync("http://x/announce", Ih, new[] { 1 }, CancellationToken.None);
            Assert.Equal(ScrapeStatus.Unsupported, u.Status);
        }

        [Fact]
        public async Task HttpScrapeAgainstAFakeTracker()
        {
            var d = new BDict(); var files = new BDict(); var f = new BDict();
            f.Add("complete", BValue.Int(324)); f.Add("downloaded", BValue.Int(1)); f.Add("incomplete", BValue.Int(53));
            files.Add(BValue.Bytes(Ih) as BString, f); d.Add("files", files);
            string seen = null;
            using (var srv = new FakeHttpServer(p => { seen = p; return new KeyValuePair<int, byte[]>(200, BencodeWriter.Encode(d)); }))
            using (var http = new HttpClient())
            {
                var r = await HttpScrape.ScrapeAsync(http, "http://127.0.0.1:" + srv.Port + "/announce", Ih, TimeSpan.FromSeconds(3), CancellationToken.None);
                Assert.Equal(ScrapeStatus.Ok, r.Status); Assert.Equal(324, r.Seeders); Assert.Equal(53, r.Leechers);
                Assert.StartsWith("/scrape?info_hash=%08%AD%A5%A7", seen);
                var x = await HttpScrape.ScrapeAsync(http, "http://127.0.0.1:" + srv.Port + "/tracker", Ih, TimeSpan.FromSeconds(3), CancellationToken.None);
                Assert.Equal(ScrapeStatus.Unsupported, x.Status);
            }
            using (var srv = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(500, new byte[0])))
            using (var http = new HttpClient())
                Assert.Equal(ScrapeStatus.NetworkError, (await HttpScrape.ScrapeAsync(http, "http://127.0.0.1:" + srv.Port + "/announce", Ih, TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
            using (var http = new HttpClient())
                Assert.Equal(ScrapeStatus.NetworkError, (await HttpScrape.ScrapeAsync(http, "http://127.0.0.1:9/announce", Ih, TimeSpan.FromSeconds(1), CancellationToken.None)).Status);
        }
    }

    public class LinkProberTests
    {
        private static readonly byte[] Ih = TestUtil.FromHex("08ada5a7a6183aae1e09d831df6748d566095a10");

        private static (LinkProber prober, IDisposable[] owned) Make(EngineOptions opt, DhtClient dht = null)
        {
            var http = new HttpClient();
            var resolver = new TransportResolver(opt, dht, http);
            return (new LinkProber(resolver, dht, http, opt.Probe), new IDisposable[] { resolver, http });
        }

        private static EngineOptions Opt()
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false };
            o.Probe.UdpTimeoutsSeconds = new[] { 1, 1 };
            o.Probe.HttpTimeout = TimeSpan.FromSeconds(2);
            o.Probe.LinkTimeout = TimeSpan.FromSeconds(20);
            o.Resolver.EnableTsService = false;
            o.Resolver.Timeout = TimeSpan.FromSeconds(8);
            o.Resolver.WeakQuorum = 1;
            return o;
        }

        [Fact]
        public async Task SumsSeedersAndLeechersFromTheMaximumOfEachTracker()
        {
            using (var a = new FakeScrapeUdpTracker())
            using (var b = new FakeScrapeUdpTracker())
            {
                a.Swarms[Hex.ToHex(Ih)] = new[] { 0, 0, 40 };
                b.Swarms[Hex.ToHex(Ih)] = new[] { 0, 0, 55 };
                var opt = Opt();
                var m = Make(opt);
                var r = await m.prober.ProbeSwarmAsync(Ih, new[] { a.Url, b.Url }, null, CancellationToken.None);
                Assert.Equal(LinkState.Good, r.State);
                Assert.Equal(0, r.Seeders); Assert.Equal(55, r.Peers); Assert.Equal(55, r.Score);       // a live stream: complete = 0, the incomplete ones are enough
                Assert.False(r.Approximate);
                Assert.Equal(2, r.Trackers.Count(t => t.Status == ScrapeStatus.Ok));
                foreach (var d in m.owned) d.Dispose();
            }
        }

        [Fact]
        public async Task StopsAsSoonAsSomeoneHasSeeders()
        {
            using (var a = new FakeScrapeUdpTracker())
            {
                a.Swarms[Hex.ToHex(Ih)] = new[] { 7, 0, 3 };
                var opt = Opt();
                opt.Probe.TrackerParallelism = 1;
                var m = Make(opt);
                var trackers = new List<string> { a.Url }.Concat(Enumerable.Range(1, 5).Select(i => "udp://127.0.0.1:9/announce?i=" + i)).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await m.prober.ProbeSwarmAsync(Ih, trackers, null, CancellationToken.None);
                Assert.Equal(7, r.Seeders); Assert.Equal(3, r.Peers);
                foreach (var d in m.owned) d.Dispose();
            }
        }

        [Fact]
        public async Task TrackerThatAsksToWaitIsNotAskedAgain()
        {
            var ihHex = Hex.ToHex(Ih);
            var d = new BDict(); d.Add("files", new BDict()); var fl = new BDict(); fl.Add("min_request_interval", BValue.Int(900)); d.Add("flags", fl);
            int hits = 0;
            using (var srv = new FakeHttpServer(p => { Interlocked.Increment(ref hits); return new KeyValuePair<int, byte[]>(200, BencodeWriter.Encode(d)); }))
            {
                var opt = Opt();
                opt.Probe.UseAnnounceFallback = false; opt.Probe.UseDhtFallback = false;
                var m = Make(opt);
                string url = "http://127.0.0.1:" + srv.Port + "/announce";
                var r1 = await m.prober.ProbeSwarmAsync(Ih, new[] { url }, null, CancellationToken.None);
                Assert.Equal(LinkState.Unknown, r1.State);
                var r2 = await m.prober.ProbeSwarmAsync(Ih, new[] { url }, null, CancellationToken.None);
                Assert.Equal(1, hits);                                       // the second probe does not query the tracker (-3)
                Assert.Empty(r2.Trackers);
                foreach (var x in m.owned) x.Dispose();
            }
        }

        [Fact]
        public async Task InvalidAnswersMeanDeadAndNetworkErrorsMeanUnknown()
        {
            var opt = Opt();
            opt.Probe.UseAnnounceFallback = false; opt.Probe.UseDhtFallback = false;
            var m = Make(opt);
            using (var t = new FakeScrapeUdpTracker { ErrorOnScrape = true })
            {
                var r = await m.prober.ProbeSwarmAsync(Ih, new[] { t.Url }, null, CancellationToken.None);
                Assert.Equal(LinkState.Dead, r.State);
            }
            var net = await m.prober.ProbeSwarmAsync(Ih, new[] { "udp://127.0.0.1:9/announce" }, null, CancellationToken.None);
            Assert.Equal(LinkState.Unknown, net.State);
            using (var t = new FakeScrapeUdpTracker())       // the tracker does not know the infohash: (0,0) = unknown
            {
                var r = await m.prober.ProbeSwarmAsync(Ih, new[] { t.Url }, null, CancellationToken.None);
                Assert.Equal(LinkState.Unknown, r.State); Assert.Equal(0, r.Score);
            }
            foreach (var x in m.owned) x.Dispose();
        }

        [Fact]
        public async Task AnnounceFallbackCountsPeersAndMarksThemApproximate()
        {
            using (var t = new FakeScrapeUdpTracker())
            {
                t.AnnouncePeers = Enumerable.Range(1, 12).Select(i => new IPEndPoint(IPAddress.Parse("10.0.0." + i), 6881)).ToList();
                var opt = Opt();
                var m = Make(opt);
                var r = await m.prober.ProbeSwarmAsync(Ih, new[] { t.Url }, null, CancellationToken.None);       // the scrape gives (0,0,0) and there is no more
                Assert.Equal(LinkState.Good, r.State); Assert.True(r.Approximate); Assert.Equal("announce", r.Method); Assert.Equal(12, r.Peers);
                Assert.Equal(1, t.Announces);
                foreach (var x in m.owned) x.Dispose();
            }
        }

        [Fact]
        public async Task DhtFallbackCountsDistinctAddressesForTheInfoHashAndTheContentId()
        {
            using (var node = new FakeDhtNode())
            {
                for (int i = 1; i <= 7; i++) node.Peers.Add(new IPEndPoint(IPAddress.Parse("10.1.0." + i), 8621));
                var opt = Opt();
                opt.EnableDht = true;
                opt.DhtBootstrap = new List<string> { "127.0.0.1:" + node.Port };
                opt.Probe.DhtTimeout = TimeSpan.FromSeconds(5);
                using (var dht = new DhtClient(opt))
                {
                    dht.Start();
                    var m = Make(opt, dht);
                    var r = await m.prober.ProbeSwarmAsync(Ih, new[] { "udp://127.0.0.1:9/announce" }, "2940120bf034db79a7f5849846ccea0255172eae", CancellationToken.None);
                    Assert.Equal(LinkState.Good, r.State); Assert.True(r.Approximate); Assert.Equal("dht", r.Method); Assert.Equal(7, r.Peers);
                    Assert.Contains("2940120bf034db79a7f5849846ccea0255172eae", node.Hashes);
                    Assert.Contains(Hex.ToHex(Ih), node.Hashes);
                    foreach (var x in m.owned) x.Dispose();
                }
            }
        }

        [Fact]
        public async Task ProbesAContentIdByResolvingItsTransportFileAndScrapingItsTrackers()
        {
            using (var tracker = new FakeScrapeUdpTracker())
            {
                var sim = new LiveSimOptions { Trackers = tracker.Url + " udp://127.0.0.1:9/announce" };
                var model = new LiveStreamModel(sim);
                tracker.Swarms[Hex.ToHex(model.InfoHash)] = new[] { 0, 0, 33 };
                using (var meta = new FakeAceMetadataPeer { FullData = model.TransportBytes })
                {
                    var opt = Opt();
                    opt.StaticPeers.Add(meta.EndPoint.ToString());
                    var m = Make(opt);
                    var e = LinkEntry.FromUrl("Canal", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9");
                    var r = await m.prober.ProbeAsync(e, CancellationToken.None);
                    Assert.Equal(LinkState.Good, r.State); Assert.Equal(33, r.Score); Assert.Equal(Hex.ToHex(model.InfoHash), r.InfoHash);
                    Assert.Equal("magnet", r.Source);
                    // second time: the transport comes from the cache (1 h)
                    int before = meta.FullRequests;
                    var r2 = await m.prober.ProbeAsync(e, CancellationToken.None);
                    Assert.Equal(before, meta.FullRequests); Assert.Equal("local_db", r2.Source);
                    foreach (var x in m.owned) x.Dispose();
                }
            }
        }

        [Fact]
        public async Task UnresolvableContentIdIsUnknownNotDead()
        {
            var opt = Opt();
            opt.Resolver.EnableP2p = false;
            var m = Make(opt);
            var r = await m.prober.ProbeAsync(LinkEntry.FromUrl("x", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9"), CancellationToken.None);
            Assert.Equal(LinkState.Unknown, r.State);
            foreach (var x in m.owned) x.Dispose();
        }

        [Fact]
        public async Task HttpStreamLinksAreCheckedWithAShortRequest()
        {
            using (var ok = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(206, Encoding.ASCII.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n"))))
            using (var notM3u = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(200, Encoding.ASCII.GetBytes("<html>no soy una lista</html>"))))
            using (var gone = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(404, new byte[0])))
            {
                var opt = Opt();
                var m = Make(opt);
                var r1 = await m.prober.ProbeAsync(LinkEntry.FromUrl("a", "http://127.0.0.1:" + ok.Port + "/live.m3u8"), CancellationToken.None);
                Assert.Equal(LinkState.Good, r1.State); Assert.True(r1.NoCounts); Assert.Equal(0, r1.Score);
                Assert.Equal(LinkState.Dead, (await m.prober.ProbeAsync(LinkEntry.FromUrl("b", "http://127.0.0.1:" + notM3u.Port + "/live.m3u8"), CancellationToken.None)).State);
                Assert.Equal(LinkState.Dead, (await m.prober.ProbeAsync(LinkEntry.FromUrl("c", "http://127.0.0.1:" + gone.Port + "/a.ts"), CancellationToken.None)).State);
                Assert.Equal(LinkState.Unknown, (await m.prober.ProbeAsync(LinkEntry.FromUrl("d", "http://127.0.0.1:9/a.ts"), CancellationToken.None)).State);
                Assert.Equal(LinkState.Unsupported, (await m.prober.ProbeAsync(LinkEntry.FromUrl("e", "https://agenda.test/futbol"), CancellationToken.None)).State);
                Assert.Equal(LinkState.Unsupported, (await m.prober.ProbeAsync(LinkEntry.FromUrl("f", ""), CancellationToken.None)).State);
                foreach (var x in m.owned) x.Dispose();
            }
        }

        [Fact]
        public async Task PerLinkTimeoutGivesUnknown()
        {
            var opt = Opt();
            opt.Probe.LinkTimeout = TimeSpan.FromMilliseconds(300);
            opt.Resolver.Timeout = TimeSpan.FromSeconds(30);
            opt.StaticPeers.Add("10.255.255.1:8621");              // does not answer
            var m = Make(opt);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await m.prober.ProbeAsync(LinkEntry.FromUrl("x", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9"), CancellationToken.None);
            Assert.Equal(LinkState.Unknown, r.State);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
            foreach (var x in m.owned) x.Dispose();
        }
    }

    internal sealed class FakeProber : ILinkProber
    {
        public ProbeOptions Options { get; set; }
        public int Concurrent, MaxConcurrent;
        public readonly List<string> Order = new List<string>();
        public readonly Dictionary<string, int> Calls = new Dictionary<string, int>();
        public Func<LinkEntry, ProbeResult> Result = e => new ProbeResult { State = LinkState.Good, Peers = 1, Method = "fake" };
        public int DelayMs = 50;
        public int Cancelled;

        public async Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct)
        {
            int c = Interlocked.Increment(ref Concurrent);
            int m; while (c > (m = Volatile.Read(ref MaxConcurrent))) Interlocked.CompareExchange(ref MaxConcurrent, c, m);
            lock (Order) { Order.Add(entry.Key); int n; Calls.TryGetValue(entry.Key, out n); Calls[entry.Key] = n + 1; }
            try
            {
                await Task.Delay(DelayMs, ct);
                var r = Result(entry);
                r.TimeUtc = DateTime.UtcNow;
                return r;
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
    }

    public class ProbeSchedulerTests
    {
        private static List<LinkEntry> Entries(int n, Func<int, string> name = null)
        {
            return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl(name == null ? "Canal " + i : name(i), "acestream://" + i.ToString("x40"), "G" + (i % 2))).ToList();
        }

        [Fact]
        public async Task NeverRunsMoreThanFourAtATimeAndFinishesTheCycle()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 4 }, DelayMs = 60 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(30));
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.Equal(30, fp.Calls.Count);
                Assert.True(fp.MaxConcurrent <= 4, "concurrencia " + fp.MaxConcurrent);
                Assert.True(fp.MaxConcurrent >= 3, "it should take advantage of the concurrency: " + fp.MaxConcurrent);
                Assert.Equal(30, s.Snapshot().Count);
            }
        }

        [Fact]
        public async Task VisibleLinksAreProbedFirst()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 1 }, DelayMs = 10 };
            var entries = Entries(20);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.SetVisible(new[] { entries[15].Key, entries[7].Key, entries[19].Key });
                s.Start(entries);
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.Equal(new[] { entries[7].Key, entries[15].Key, entries[19].Key }, fp.Order.Take(3).OrderBy(x => x).ToArray().OrderBy(x => Array.IndexOf(new[] { entries[7].Key, entries[15].Key, entries[19].Key }, x)).ToArray());
                Assert.Equal(new HashSet<string>(new[] { entries[7].Key, entries[15].Key, entries[19].Key }), new HashSet<string>(fp.Order.Take(3)));
            }
        }

        [Fact]
        public async Task FreshResultsAreNotRepeatedInTheNextCycleButExpiredOnesAre()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 4, ResultTtl = TimeSpan.FromMilliseconds(700), CycleInterval = TimeSpan.FromMilliseconds(200) }, DelayMs = 10 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(5));
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.All(fp.Calls.Values, v => Assert.Equal(1, v));
                await Task.Delay(400);                                  // one more cycle, but the results are still valid
                Assert.All(fp.Calls.Values, v => Assert.Equal(1, v));
                await Task.Delay(1200);                                 // they already expired: they are repeated
                Assert.All(fp.Calls.Values, v => Assert.True(v >= 2, "v=" + v));
            }
        }

        [Fact]
        public async Task UnknownResultsAreRetriedInTheNextCycle()
        {
            int n = 0;
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 2, ResultTtl = TimeSpan.FromMilliseconds(100), CycleInterval = TimeSpan.FromMilliseconds(150) }, DelayMs = 5 };
            fp.Result = e => Interlocked.Increment(ref n) <= 1 ? new ProbeResult { State = LinkState.Unknown } : new ProbeResult { State = LinkState.Good, Peers = 5 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e1 = Entries(1);
                s.Start(e1);
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.Equal(LinkState.Unknown, s.GetResult(e1[0]).State);
                // The good result expires after 100 ms and is probed again every cycle: we wait to see it "Good" instead of looking at a fixed instant
                // (at that instant it may be in the middle of a re-probe, "Checking", especially with the machine loaded).
                var limit = DateTime.UtcNow.AddSeconds(10);
                while (s.GetResult(e1[0]).State != LinkState.Good && DateTime.UtcNow < limit) await Task.Delay(20);
                Assert.Equal(LinkState.Good, s.GetResult(e1[0]).State);
            }
        }

        [Fact]
        public async Task StopCancelsEverythingInFlightAndNothingNewStarts()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 4 }, DelayMs = 10000 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var entries = Entries(12);
                s.Start(entries);
                await Task.Delay(400);
                Assert.Equal(4, fp.Concurrent);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Stop();
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "Stop must cancel quickly");
                await Task.Delay(300);
                Assert.Equal(0, fp.Concurrent);
                Assert.Equal(4, fp.Cancelled);
                Assert.Equal(4, fp.Calls.Count);                         // no more were launched
                Assert.False(s.IsRunning);
                // those that were in progress do not leave a half-finished result
                Assert.All(entries.Take(4), e => Assert.Equal(LinkState.Pending, s.GetResult(e).State));
            }
        }

        [Fact]
        public async Task RestartingKeepsTheFreshResultsOfEarlierRuns()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 4, ResultTtl = TimeSpan.FromMinutes(1) }, DelayMs = 10 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var entries = Entries(6);
                s.Start(entries);
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                s.Stop();
                s.Start(entries);                                        // back to the listing: what is still valid is not repeated
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.All(fp.Calls.Values, v => Assert.Equal(1, v));
            }
        }

        [Fact]
        public async Task RaisesResultChangedWhileProbing()
        {
            var fp = new FakeProber { Options = new ProbeOptions { Concurrency = 2 }, DelayMs = 10 };
            var events = new List<string>();
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.ResultChanged += (e, r) => { lock (events) events.Add(e.Name + ":" + r.State); };
                s.Start(Entries(3));
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                await Task.Delay(200);
                lock (events)
                {
                    Assert.Equal(3, events.Count(x => x.EndsWith(":Checking")));
                    Assert.Equal(3, events.Count(x => x.EndsWith(":Good")));
                }
            }
        }

        // ---------- Ordering and filter ----------

        private static ListRow Row(string name, LinkState st, int seeds, int peers, string group = null, bool noCounts = false, bool approx = false)
        {
            return new ListRow { Entry = LinkEntry.FromUrl(name, "acestream://" + Guid.NewGuid().ToString("N") + "00000000", group), Result = new ProbeResult { State = st, Seeders = seeds, Peers = peers, NoCounts = noCounts, Approximate = approx } };
        }

        [Fact]
        public void OnlyLiveLinksAreShownSortedBySeedsPlusPeers()
        {
            var rows = new List<ListRow>
            {
                Row("d", LinkState.Good, 0, 5), Row("a", LinkState.Good, 10, 10), Row("b", LinkState.Good, 20, 0), Row("muerto", LinkState.Dead, 0, 0),
                Row("c", LinkState.Good, 3, 17), Row("desconocido", LinkState.Unknown, 0, 0), Row("pendiente", LinkState.Pending, 0, 0),
                Row("http", LinkState.Good, 0, 0, noCounts: true), Row("A2", LinkState.Good, 10, 10)
            };
            var shown = ProbeScheduler.Sort(rows, false, false).Select(r => r.Entry.Name).ToList();
            // score 20: a, A2 (10+10), b (20+0 = 20, more seeds) and c (3+17); tie broken by seeds and then by name
            Assert.Equal(new[] { "b", "a", "A2", "c", "d", "http" }, shown);
            var all = ProbeScheduler.Sort(rows, true, false).Select(r => r.Entry.Name).ToList();
            Assert.Equal(new[] { "b", "a", "A2", "c", "d", "http", "desconocido", "muerto", "pendiente" }, all);
        }

        [Fact]
        public void SortingWithinGroupsKeepsTheGroupsInOrderOfAppearance()
        {
            var rows = new List<ListRow>
            {
                Row("x1", LinkState.Good, 1, 1, "B"), Row("y1", LinkState.Good, 5, 5, "A"), Row("x2", LinkState.Good, 9, 9, "B"), Row("y2", LinkState.Good, 1, 0, "A"), Row("z", LinkState.Dead, 0, 0, "A")
            };
            var res = ProbeScheduler.Sort(rows, false, true).Select(r => r.Entry.Group + ":" + r.Entry.Name).ToList();
            Assert.Equal(new[] { "B:x2", "B:x1", "A:y1", "A:y2" }, res);
        }

        [Fact]
        public async Task SnapshotListsEachKeyOnceAndSkipsLabelsAndWebPages()
        {
            var fp = new FakeProber { Options = new ProbeOptions(), DelayMs = 5 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var entries = new List<LinkEntry>
                {
                    LinkEntry.FromUrl("etiqueta", ""), LinkEntry.FromUrl("web", "https://a.test/agenda"),
                    LinkEntry.FromUrl("uno", "acestream://" + 1.ToString("x40")), LinkEntry.FromUrl("repetido", "acestream://" + 1.ToString("x40")), LinkEntry.FromUrl("dos", "acestream://" + 2.ToString("x40"))
                };
                s.Start(entries);
                await s.WaitFirstCycleAsync(new CancellationTokenSource(5000).Token);
                Assert.Equal(new[] { "dos", "uno" }.Length, s.Snapshot().Count);
                Assert.Equal(2, fp.Calls.Count);
                Assert.Equal(LinkState.Unsupported, s.GetResult(entries[0]).State);
            }
        }
    }
}
