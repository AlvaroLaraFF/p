using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.P2P;
using Xunit;

namespace AceEngine.Tests.P2P
{
    public class TrackerTests
    {
        private static readonly byte[] Ih = TestUtil.FromHex("00ff10205a7e7f808182838485868788899a9b9c");
        private static readonly byte[] Pid = Encoding.ASCII.GetBytes("R30------abcdefghijk");

        private static AnnounceRequest Req(string ev = "started")
        {
            return new AnnounceRequest { InfoHash = Ih, PeerId = Pid, Port = 8621, Left = 1000, Event = ev };
        }

        [Fact]
        public void HttpPercentEncodingOfRawBytes()
        {
            // 0x00 0xff 0x10 0x20 'Z' '~' 0x7f ... -> only A-Za-z0-9 - _ . ~ are left unencoded
            Assert.Equal("%00%FF%10%20Z~%7F%80%81%82%83%84%85%86%87%88%89%9A%9B%9C", HttpTracker.PercentEncode(Ih).Replace("%5A", "Z").Replace("%7E", "~"));
            Assert.Equal("R30------abcdefghijk", HttpTracker.PercentEncode(Pid));
        }

        [Fact]
        public void HttpUrlHasAllParametersInAceOrder()
        {
            var url = HttpTracker.BuildUrl("http://t.test/announce", Req(), "AbCdEf");
            Assert.StartsWith("http://t.test/announce?info_hash=", url);
            int ip = url.IndexOf("&peer_id="), po = url.IndexOf("&port="), up = url.IndexOf("&uploaded="), dl = url.IndexOf("&downloaded="),
                le = url.IndexOf("&left="), nw = url.IndexOf("&numwant="), np = url.IndexOf("&no_peer_id=1&compact=1"), ev = url.IndexOf("&event=started"), ke = url.IndexOf("&key=AbCdEf");
            Assert.True(ip > 0 && ip < po && po < up && up < dl && dl < le && le < nw && nw < np && np < ev && ev < ke, url);
            Assert.Contains("&port=8621&", url);
            Assert.Contains("&left=1000&", url);
            Assert.Contains("&numwant=200&", url);
            // if the URL already has '?' it is joined with '&'
            Assert.StartsWith("http://t.test/a?k=v&info_hash=", HttpTracker.BuildUrl("http://t.test/a?k=v", Req(null), null));
            Assert.DoesNotContain("event=", HttpTracker.BuildUrl("http://t.test/a", Req(null), null));
        }

        [Fact]
        public void ParsesCompactAndDictionaryPeersAndFailure()
        {
            var d = new BDict();
            d.Add("interval", BValue.Int(900)); d.Add("min interval", BValue.Int(60)); d.Add("complete", BValue.Int(7)); d.Add("incomplete", BValue.Int(3));
            d.Add("peers", BValue.Bytes(new byte[] { 1, 2, 3, 4, 0x1a, 0xe1, 5, 6, 7, 8, 0, 80, 9, 9, 9, 9, 0, 0 }));   // the last one has port 0
            var r = HttpTracker.ParseResponse(BencodeWriter.Encode(d));
            Assert.False(r.Failed);
            Assert.Equal(900, r.Interval); Assert.Equal(60, r.MinInterval); Assert.Equal(7, r.Seeders); Assert.Equal(3, r.Leechers);
            Assert.Equal(new[] { "1.2.3.4:6881", "5.6.7.8:80" }, r.Peers.Select(p => p.ToString()).ToArray());

            var pd = new BDict(); pd.Add("ip", BValue.Str("10.0.0.5")); pd.Add("port", BValue.Int(7000));
            var d2 = new BDict(); d2.Add("peers", BValue.List(pd));
            Assert.Equal("10.0.0.5:7000", HttpTracker.ParseResponse(BencodeWriter.Encode(d2)).Peers.Single().ToString());

            var f = HttpTracker.ParseResponse(Encoding.ASCII.GetBytes("d14:failure reason4:nopee"));
            Assert.True(f.Failed); Assert.Equal("nope", f.FailureReason);
            Assert.True(HttpTracker.ParseResponse(Encoding.ASCII.GetBytes("<html>")).Failed);
        }

        [Fact]
        public void UdpConnectRequestBytes()
        {
            Assert.Equal("0000041727101980" + "00000000" + "12345678", TestUtil.ToHex(UdpTracker.BuildConnect(0x12345678)));
        }

        [Fact]
        public void UdpAnnounceRequestLayout()
        {
            var b = UdpTracker.BuildAnnounce(0x1122334455667788UL, 0xaabbccdd, Req("started"), 0x01020304);
            Assert.Equal(98, b.Length);
            Assert.Equal("1122334455667788", TestUtil.ToHex(b.AsSpan(0, 8).ToArray()));
            Assert.Equal("00000001", TestUtil.ToHex(b.AsSpan(8, 4).ToArray()));          // announce action
            Assert.Equal("aabbccdd", TestUtil.ToHex(b.AsSpan(12, 4).ToArray()));
            Assert.Equal(Ih, b.AsSpan(16, 20).ToArray());
            Assert.Equal(Pid, b.AsSpan(36, 20).ToArray());
            Assert.Equal(1000UL, Wire.GetU64(b, 64));                                     // left
            Assert.Equal(2u, Wire.GetU32(b, 80));                                         // started
            Assert.Equal(0x01020304u, Wire.GetU32(b, 88));                                // key
            Assert.Equal(200u, Wire.GetU32(b, 92));                                       // num_want
            Assert.Equal(8621, Wire.GetU16(b, 96));
            Assert.Equal(3u, Wire.GetU32(UdpTracker.BuildAnnounce(1, 1, Req("stopped"), 0), 80));
            Assert.Equal(1u, Wire.GetU32(UdpTracker.BuildAnnounce(1, 1, Req("completed"), 0), 80));
            Assert.Equal(0u, Wire.GetU32(UdpTracker.BuildAnnounce(1, 1, Req(null), 0), 80));
        }

        [Fact]
        public void UdpResponsesAreValidated()
        {
            var conn = new byte[16];
            Wire.PutU32(conn, 0, 0); Wire.PutU32(conn, 4, 77); Wire.PutU64(conn, 8, 42);
            ulong cid; string err;
            Assert.True(UdpTracker.ParseConnect(conn, 77, out cid, out err));
            Assert.Equal(42UL, cid);
            Assert.False(UdpTracker.ParseConnect(conn, 78, out cid, out err));              // different tid
            var announce = new byte[26];
            Wire.PutU32(announce, 0, 1); Wire.PutU32(announce, 4, 5); Wire.PutU32(announce, 8, 1800); Wire.PutU32(announce, 12, 4); Wire.PutU32(announce, 16, 9);
            announce[20] = 1; announce[21] = 2; announce[22] = 3; announce[23] = 4; announce[24] = 0x1a; announce[25] = 0xe1;
            var res = new AnnounceResult();
            Assert.True(UdpTracker.ParseAnnounce(announce, 5, res, out err));
            Assert.Equal(1800, res.Interval); Assert.Equal(9, res.Seeders); Assert.Equal(4, res.Leechers);
            Assert.Equal("1.2.3.4:6881", res.Peers.Single().ToString());
            Assert.False(UdpTracker.ParseAnnounce(announce, 6, new AnnounceResult(), out err));
            var errMsg = new byte[8 + 4]; Wire.PutU32(errMsg, 0, 3); Wire.PutU32(errMsg, 4, 5); Encoding.ASCII.GetBytes("nope").CopyTo(errMsg, 8);
            Assert.False(UdpTracker.ParseAnnounce(errMsg, 5, new AnnounceResult(), out err));
            Assert.Equal("nope", err);
        }

        [Fact]
        public async Task UdpAnnounceAgainstFakeTracker()
        {
            using (var t = new FakeUdpTracker())
            {
                t.Peers.Add(new IPEndPoint(IPAddress.Parse("9.8.7.6"), 1234));
                var r = await UdpTracker.AnnounceAsync("127.0.0.1", t.Port, "udp://fake", Req(), 1, new[] { 2, 2 }, CancellationToken.None);
                Assert.False(r.Failed, r.FailureReason);
                Assert.Equal("9.8.7.6:1234", r.Peers.Single().ToString());
                Assert.Equal(1, t.Announces);
            }
        }

        [Fact]
        public async Task UdpAnnounceRetriesAfterLostDatagrams()
        {
            using (var t = new FakeUdpTracker { DropFirstN = 1 })
            {
                t.Peers.Add(new IPEndPoint(IPAddress.Parse("9.8.7.6"), 1234));
                var r = await UdpTracker.AnnounceAsync("127.0.0.1", t.Port, "udp://fake", Req(), 1, new[] { 1, 2 }, CancellationToken.None);
                Assert.False(r.Failed, r.FailureReason);
                Assert.Single(r.Peers);
            }
        }

        [Fact]
        public async Task UdpAnnounceFailsWithoutAnswer()
        {
            using (var t = new FakeUdpTracker { DropFirstN = 1000 })
            {
                var r = await UdpTracker.AnnounceAsync("127.0.0.1", t.Port, "udp://fake", Req(), 1, new[] { 1 }, CancellationToken.None);
                Assert.True(r.Failed);
            }
        }

        [Fact]
        public async Task HttpAnnounceAgainstFakeTracker()
        {
            using (var t = new FakeHttpTracker())
            using (var http = new HttpClient())
            {
                t.Peers.Add(new IPEndPoint(IPAddress.Parse("9.8.7.6"), 1234));
                var r = await HttpTracker.AnnounceAsync(http, t.AnnounceUrl, Req(), "XyZ123", CancellationToken.None);
                Assert.False(r.Failed, r.FailureReason);
                Assert.Equal("9.8.7.6:1234", r.Peers.Single().ToString());
                var line = t.Requests.Single();
                Assert.Contains("info_hash=%00%FF%10%20Z~", line.Replace("%5A", "Z").Replace("%7E", "~"));
                Assert.Contains("peer_id=R30------abcdefghijk", line);
                Assert.Contains("event=started", line);
                Assert.Contains("key=XyZ123", line);
            }
        }

        [Fact]
        public async Task HttpAnnounceReportsTrackerFailure()
        {
            using (var t = new FakeHttpTracker { Fail = true })
            using (var http = new HttpClient())
            {
                var r = await HttpTracker.AnnounceAsync(http, t.AnnounceUrl, Req(), null, CancellationToken.None);
                Assert.True(r.Failed);
                Assert.Equal("not today", r.FailureReason);
            }
        }
    }

    public class DhtTests
    {
        [Fact]
        public async Task LookupFollowsNodesAndCollectsPeers()
        {
            using (var a = new FakeDhtNode())
            using (var b = new FakeDhtNode())
            {
                a.Next = b;
                b.Peers.Add(new IPEndPoint(IPAddress.Parse("11.22.33.44"), 5555));
                b.Peers.Add(new IPEndPoint(IPAddress.Parse("55.66.77.88"), 6666));
                var opt = new EngineOptions { DhtBootstrap = new List<string> { "127.0.0.1:" + a.Port } };
                using (var dht = new DhtClient(opt))
                {
                    dht.Start();
                    var found = new List<IPEndPoint>();
                    int n = await dht.GetPeersAsync(new byte[20], ps => { lock (found) found.AddRange(ps); }, TimeSpan.FromSeconds(10), CancellationToken.None);
                    Assert.Equal(2, n);
                    Assert.Equal(new[] { "11.22.33.44:5555", "55.66.77.88:6666" }, found.Select(p => p.ToString()).OrderBy(x => x).ToArray());
                    Assert.True(a.GetPeersReceived >= 1);
                    Assert.True(b.GetPeersReceived >= 1);
                    Assert.True(dht.KnownNodes >= 2);
                }
            }
        }

        [Fact]
        public async Task LookupEndsWhenNobodyAnswers()
        {
            var opt = new EngineOptions { DhtBootstrap = new List<string> { "127.0.0.1:9" } };      // closed port
            using (var dht = new DhtClient(opt))
            {
                dht.Start();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = await dht.GetPeersAsync(new byte[20], ps => { }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(0, n);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9));
            }
        }

        [Fact]
        public async Task LookupHonoursCancellation()
        {
            using (var a = new FakeDhtNode())
            {
                var opt = new EngineOptions { DhtBootstrap = new List<string> { "127.0.0.1:" + a.Port } };
                using (var dht = new DhtClient(opt))
                using (var cts = new CancellationTokenSource())
                {
                    dht.Start();
                    cts.Cancel();
                    int n = await dht.GetPeersAsync(new byte[20], ps => { }, TimeSpan.FromSeconds(10), cts.Token);
                    Assert.Equal(0, n);
                }
            }
        }

        [Fact]
        public async Task AnswersPingAndMessagesCarryVersionTag()
        {
            var opt = new EngineOptions();
            using (var dht = new DhtClient(opt))
            using (var peer = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                dht.Start();
                var q = new BDict();
                q.Add("t", BValue.Str("aa")); q.Add("y", BValue.Str("q")); q.Add("q", BValue.Str("ping"));
                var a = new BDict(); a.Add("id", BValue.Bytes(new byte[20])); q.Add("a", a);
                var bytes = BencodeWriter.Encode(q);
                await peer.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, dht.Port));
                var task = peer.ReceiveAsync();
                var done = await Task.WhenAny(task, Task.Delay(3000));
                Assert.Same(task, done);
                var resp = (BDict)BencodeParser.Parse((await task).Buffer);
                Assert.Equal("r", resp.GetString("y"));
                Assert.Equal(dht.NodeId, resp.GetDict("r").GetBytes("id"));
                Assert.Equal(opt.DhtVersion, resp.GetBytes("v"));      // v = NS\x01\x18
            }
        }
    }
}
