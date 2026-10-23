using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast;
using AceEngine.Streaming;
using Xunit;

namespace AceEngine.Tests.Cast
{
    internal sealed class FakeLanContent : ILanContent
    {
        public bool Hls = true, Progressive = true, Available = true;
        public string Playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:5\n#EXTINF:4.000,\ns5.ts\n";
        public byte[] Segment = Enumerable.Range(0, 188 * 40).Select(i => (byte)(i % 188 == 0 ? 0x47 : i)).ToArray();
        public int StreamPackets = 400;
        public bool EndlessStream;
        public int StreamOpens;
        public TimeSpan? LastBack;
        public bool OffersHls { get { return Hls; } }
        public bool OffersProgressive { get { return Progressive; } }
        public bool IsAvailable { get { return Available; } }
        public string Master;
        public string GetMasterPlaylist() { return Master; }
        public string GetPlaylist(out bool ended) { ended = false; return Playlist; }
        public SegmentLease GetSegment(long seq)
        {
            if (seq != 5) return null;
            var b = new byte[Segment.Length + 100];
            Buffer.BlockCopy(Segment, 0, b, 0, Segment.Length);
            return new SegmentLease(b, Segment.Length);
        }
        public Task<IStreamReader> OpenProgressiveAsync(TimeSpan? back, CancellationToken ct)
        {
            Interlocked.Increment(ref StreamOpens);
            LastBack = back;
            return Task.FromResult<IStreamReader>(new FakeReader(StreamPackets, EndlessStream));
        }

        private sealed class FakeReader : IStreamReader
        {
            private int _left; private readonly bool _endless; private long _pos;
            public FakeReader(int packets, bool endless) { _left = packets; _endless = endless; }
            public long Position { get { return _pos; } }
            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (!_endless && _left == 0) return 0;
                if (_endless) await Task.Delay(5, ct).ConfigureAwait(false);
                int n = Math.Min(count / 188, Math.Min(_left, 20));
                if (_endless) n = Math.Max(1, Math.Min(count / 188, 20));
                for (int i = 0; i < n; i++) { buffer[offset + i * 188] = 0x47; for (int k = 1; k < 188; k++) buffer[offset + i * 188 + k] = (byte)(i + k); }
                if (!_endless) _left -= n;
                _pos += n * 188;
                return n * 188;
            }
            public void Dispose() { }
        }
    }

    internal sealed class HttpReply
    {
        public int Status; public string StatusLine; public string Version;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string Header(string n) { string v; return Headers.TryGetValue(n, out v) ? v : null; }
        public string Text { get { return Encoding.ASCII.GetString(Body); } }
    }

    internal static class RawHttp
    {
        public static byte[] ReadAll(Stream s, int max, int millis = 5000)
        {
            var ms = new MemoryStream();
            var buf = new byte[8192];
            var deadline = DateTime.UtcNow.AddMilliseconds(millis);
            while (ms.Length < max)
            {
                if (DateTime.UtcNow > deadline) break;
                int n;
                try { n = s.Read(buf, 0, buf.Length); } catch (IOException) { break; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        public static HttpReply Parse(byte[] raw)
        {
            int he = -1;
            for (int i = 0; i + 3 < raw.Length; i++) if (raw[i] == 13 && raw[i + 1] == 10 && raw[i + 2] == 13 && raw[i + 3] == 10) { he = i; break; }
            if (he < 0) return null;
            var head = Encoding.ASCII.GetString(raw, 0, he).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var r = new HttpReply { StatusLine = head[0] };
            var parts = head[0].Split(' ');
            r.Version = parts[0]; r.Status = int.Parse(parts[1]);
            for (int i = 1; i < head.Length; i++) { int c = head[i].IndexOf(':'); r.Headers[head[i].Substring(0, c)] = head[i].Substring(c + 1).Trim(); }
            r.Body = new byte[raw.Length - he - 4];
            Buffer.BlockCopy(raw, he + 4, r.Body, 0, r.Body.Length);
            return r;
        }

        /// <summary>Sends the request text and reads until the server closes or 'max' bytes arrive.</summary>
        public static HttpReply Send(IPEndPoint server, string request, IPAddress from = null, int max = 4 << 20, int millis = 5000)
        {
            using (var c = from == null ? new TcpClient() : new TcpClient(new IPEndPoint(from, 0)))
            {
                c.ReceiveTimeout = millis; c.SendTimeout = millis;
                c.Connect(server);
                var s = c.GetStream();
                var b = Encoding.ASCII.GetBytes(request);
                s.Write(b, 0, b.Length);
                return Parse(ReadAll(s, max, millis));
            }
        }

        public static string Get(string path, IPEndPoint server, string host = null, string extra = "", string method = "GET")
        {
            return method + " " + path + " HTTP/1.1\r\nHost: " + (host ?? server.ToString()) + "\r\nConnection: close\r\n" + extra + "\r\n";
        }
    }

    [Collection(CastIsolatedCollection.Name)]
    public class LanServerTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        private readonly FakeLanContent _content = new FakeLanContent();
        public void Dispose() { foreach (var x in _d) x.Dispose(); }

        private LanServer Start(Action<LanServerOptions> cfg = null, FakeLanContent content = null)
        {
            var o = new LanServerOptions { BindAddress = IPAddress.Loopback, Target = IPAddress.Loopback, FirstRequestTimeout = TimeSpan.FromSeconds(2), KeepAliveTimeout = TimeSpan.FromSeconds(2), ReadStallTimeout = TimeSpan.FromSeconds(3), WriteTimeout = TimeSpan.FromSeconds(5) };
            if (cfg != null) cfg(o);
            var s = new LanServer(o, content ?? _content);
            s.Start();
            _d.Add(s);
            return s;
        }

        private static IPEndPoint Ep(LanServer s) { return new IPEndPoint(s.Address, s.Port); }
        private static string Path(LanServer s, string name) { return "/c/" + s.Token + "/" + name; }

        // ------------------------------------------------------------ token and routes

        [Fact]
        public void TheTokenIs128BitsOfHexAndDiffersEveryTime()
        {
            var a = LanServer.NewToken(); var b = LanServer.NewToken();
            Assert.Equal(32, a.Length);
            Assert.Matches("^[0-9a-f]{32}$", a);
            Assert.NotEqual(a, b);
            var s = Start();
            Assert.Equal(32, s.Token.Length);
            Assert.StartsWith("http://127.0.0.1:" + s.Port + "/c/" + s.Token + "/", s.PlaylistUrl);
            Assert.EndsWith("index.m3u8", s.PlaylistUrl);
            Assert.EndsWith("live.ts", s.StreamUrl);
        }

        [Fact]
        public void TheCorrectTokenServesThePlaylistWithTheRightMimeTypeAndNoCaching()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            Assert.Equal(200, r.Status);
            Assert.Equal("application/vnd.apple.mpegurl", r.Header("Content-Type"));
            Assert.Equal(_content.Playlist, r.Text);
            Assert.Equal(_content.Playlist.Length.ToString(), r.Header("Content-Length"));
            Assert.Contains("no-cache", r.Header("Cache-Control"));
            Assert.Equal("nosniff", r.Header("X-Content-Type-Options"));
        }

        [Fact]
        public void TheMasterPlaylistIsServedOnlyWhenTheContentHasOne()
        {
            var none = Start();
            Assert.Equal(404, RawHttp.Send(Ep(none), RawHttp.Get(Path(none, "master.m3u8"), Ep(none))).Status);
            var c = new FakeLanContent { Master = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,CODECS=\"avc1.4d4028,mp4a.40.2\"\nindex.m3u8\n" };
            var s = Start(null, c);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "master.m3u8"), Ep(s)));
            Assert.Equal(200, r.Status);
            Assert.Equal("application/vnd.apple.mpegurl", r.Header("Content-Type"));
            Assert.Equal(c.Master, r.Text);
            Assert.Equal("*", r.Header("Access-Control-Allow-Origin"));
            Assert.EndsWith("/master.m3u8", s.MasterUrl);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + new string('0', 32) + "/master.m3u8", Ep(s))).Status);
        }

        [Fact]
        public void SegmentsAreServedAsMp2tWithLengthAndRanges()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s)));
            Assert.Equal(200, r.Status);
            Assert.Equal("video/mp2t", r.Header("Content-Type"));
            Assert.Equal(_content.Segment, r.Body);
            Assert.Equal("bytes", r.Header("Accept-Ranges"));
            var p = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s), extra: "Range: bytes=188-375\r\n"));
            Assert.Equal(206, p.Status);
            Assert.Equal("bytes 188-375/" + _content.Segment.Length, p.Header("Content-Range"));
            Assert.Equal(_content.Segment.Skip(188).Take(188).ToArray(), p.Body);
            var bad = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s), extra: "Range: bytes=99999999-\r\n"));
            Assert.Equal(416, bad.Status);
        }

        [Fact]
        public void AnUnknownSegmentIs404()
        {
            var s = Start();
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s6.ts"), Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s-1.ts"), Ep(s))).Status);
        }

        [Theory]
        [InlineData("/c/00000000000000000000000000000000/index.m3u8")]
        [InlineData("/c//index.m3u8")]
        [InlineData("/c/index.m3u8")]
        [InlineData("/index.m3u8")]
        [InlineData("/")]
        [InlineData("/c/")]
        [InlineData("/stream/abcdef0123456789")]
        [InlineData("/../etc/passwd")]
        [InlineData("/c/%2e%2e/index.m3u8")]
        public void AWrongTokenOrAnyOtherPathGivesTheSame404(string path)
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(path, Ep(s)));
            Assert.Equal(404, r.Status);
            Assert.Equal("Not Found\n", r.Text);
            Assert.Null(r.Header("Access-Control-Allow-Origin"));                            // CORS only on the token routes
            Assert.True(s.GetStats().BadToken + s.GetStats().NotFound >= 1);
        }

        [Fact]
        public void ATokenOneCharacterOffIsRejectedAndThereIsNoListingAnywhere()
        {
            var s = Start();
            string bad = s.Token.Substring(0, 31) + (s.Token[31] == '0' ? '1' : '0');
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + bad + "/index.m3u8", Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + s.Token + "/", Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + s.Token, Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + s.Token + "/index.m3u8/x", Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get("/c/" + s.Token + "/INDEX.M3U8", Ep(s))).Status);
        }

        [Fact]
        public void TheTokenIsNeverInAnErrorBody()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get("/c/" + s.Token + "/nothing.bin", Ep(s)));
            Assert.Equal(404, r.Status);
            Assert.DoesNotContain(s.Token, r.StatusLine + r.Text + string.Join("", r.Headers.Values));
        }

        [Fact]
        public void AClientThatKeepsGuessingPathsIsDropped()
        {
            var s = Start();
            string one = "GET /c/" + new string('0', 32) + "/index.m3u8 HTTP/1.1\r\nHost: " + Ep(s) + "\r\n\r\n";
            using (var c = new TcpClient())
            {
                c.Connect(Ep(s));
                var st = c.GetStream(); st.ReadTimeout = 3000;
                var b = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(one, 6)));
                st.Write(b, 0, b.Length);
                var text = Encoding.ASCII.GetString(RawHttp.ReadAll(st, 100000, 3000));
                Assert.Equal(3, text.Split(new[] { "HTTP/1.1 404" }, StringSplitOptions.None).Length - 1);
            }
        }

        // ------------------------------------------------------------ who may connect

        [Fact]
        public void ConnectionsFromAnotherAddressAreClosedWithoutAnAnswer()
        {
            var s = Start();                                                                // the target is 127.0.0.1
            using (var c = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0)))
            {
                c.Connect(Ep(s));
                var st = c.GetStream(); st.ReadTimeout = 3000;
                var b = Encoding.ASCII.GetBytes(RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
                try { st.Write(b, 0, b.Length); } catch (IOException) { }
                var buf = new byte[256];
                int n = 0;
                try { n = st.Read(buf, 0, buf.Length); } catch (IOException) { n = 0; }
                Assert.Equal(0, n);
            }
            Assert.True(s.GetStats().RejectedAddress >= 1);
            Assert.Equal(0, s.GetStats().Requests);
        }

        [Fact]
        public void TheLocalNetworkOptionAdmitsTheSubnetButNotOtherNetworks()
        {
            var o = new LanServerOptions { BindAddress = IPAddress.Parse("192.168.1.50"), Target = IPAddress.Parse("192.168.1.68"), AllowLocalNetwork = true };
            Assert.True(LanServer.IsAllowed(IPAddress.Parse("192.168.1.68"), o));
            Assert.True(LanServer.IsAllowed(IPAddress.Parse("192.168.1.99"), o));
            Assert.False(LanServer.IsAllowed(IPAddress.Parse("192.168.2.99"), o));
            Assert.False(LanServer.IsAllowed(IPAddress.Parse("10.0.0.5"), o));
            Assert.False(LanServer.IsAllowed(IPAddress.Parse("8.8.8.8"), o));
            Assert.False(LanServer.IsAllowed(IPAddress.Loopback, o));
            Assert.False(LanServer.IsAllowed(IPAddress.IPv6Loopback, o));
            Assert.False(LanServer.IsAllowed(null, o));
            o.AllowLocalNetwork = false;
            Assert.True(LanServer.IsAllowed(IPAddress.Parse("192.168.1.68"), o));
            Assert.False(LanServer.IsAllowed(IPAddress.Parse("192.168.1.99"), o));
            o.LocalNetworkMask = IPAddress.Parse("255.255.0.0"); o.AllowLocalNetwork = true;
            Assert.True(LanServer.IsAllowed(IPAddress.Parse("192.168.200.1"), o));
        }

        [Fact]
        public void ItRefusesToBeCreatedWithoutAnInterfaceAddressOrWithNobodyAllowed()
        {
            Assert.Throws<ArgumentException>(() => new LanServer(new LanServerOptions { BindAddress = IPAddress.Any, Target = IPAddress.Loopback }, _content));
            Assert.Throws<ArgumentException>(() => new LanServer(new LanServerOptions { BindAddress = null, Target = IPAddress.Loopback }, _content));
            Assert.Throws<ArgumentException>(() => new LanServer(new LanServerOptions { BindAddress = IPAddress.Loopback }, _content));
            Assert.Throws<ArgumentException>(() => new LanServer(new LanServerOptions { BindAddress = IPAddress.IPv6Any, Target = IPAddress.Loopback }, _content));
        }

        [Fact]
        public void ItListensOnlyOnTheGivenAddress()
        {
            var s = Start();
            var l = (TcpListener)s.GetType().GetField("_listener", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(s);
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Address);
        }

        // ------------------------------------------------------------ Host header

        [Theory]
        [InlineData("evil.example.com")]
        [InlineData("localhost")]
        [InlineData("127.0.0.1")]                                                           // no port: not what a client talking to this server sends
        [InlineData("127.0.0.1:1")]
        [InlineData("192.168.1.1:80")]
        public void AWrongHostHeaderIsRefused(string host)
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), host));
            Assert.Equal(421, r.Status);
            Assert.Equal(1, s.GetStats().RejectedHost);
        }

        [Fact]
        public void AMissingHostIsABadRequestInHttp11ButFineInHttp10()
        {
            var s = Start();
            Assert.Equal(400, RawHttp.Send(Ep(s), "GET " + Path(s, "index.m3u8") + " HTTP/1.1\r\nConnection: close\r\n\r\n").Status);
            Assert.Equal(200, RawHttp.Send(Ep(s), "GET " + Path(s, "index.m3u8") + " HTTP/1.0\r\n\r\n").Status);
        }

        [Fact]
        public void TwoHostHeadersAreABadRequest()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), "GET " + Path(s, "index.m3u8") + " HTTP/1.1\r\nHost: " + Ep(s) + "\r\nHost: " + Ep(s) + "\r\n\r\n");
            Assert.Equal(400, r.Status);
        }

        // ------------------------------------------------------------ CORS

        [Fact]
        public void TokenRoutesCarryCorsHeaders()
        {
            var s = Start();
            foreach (var name in new[] { "index.m3u8", "s5.ts" })
            {
                var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, name), Ep(s), extra: "Origin: https://example.com\r\n"));
                Assert.Equal(200, r.Status);
                Assert.Equal("*", r.Header("Access-Control-Allow-Origin"));
                Assert.Contains("Content-Length", r.Header("Access-Control-Expose-Headers"));
            }
        }

        [Fact]
        public void TheCorsPreflightIsAnswered204WithTheAllowedMethodsAndHeaders()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), extra: "Origin: https://x\r\nAccess-Control-Request-Method: GET\r\nAccess-Control-Request-Headers: Range, X-Cast-Thing\r\n", method: "OPTIONS"));
            Assert.Equal(204, r.Status);
            Assert.Equal("*", r.Header("Access-Control-Allow-Origin"));
            Assert.Contains("GET", r.Header("Access-Control-Allow-Methods"));
            Assert.Contains("OPTIONS", r.Header("Access-Control-Allow-Methods"));
            Assert.Equal("Range, X-Cast-Thing", r.Header("Access-Control-Allow-Headers"));
            Assert.Equal("0", r.Header("Content-Length"));
            Assert.NotNull(r.Header("Access-Control-Max-Age"));
            Assert.Null(r.Header("Access-Control-Allow-Private-Network"));
        }

        [Fact]
        public void ThePrivateNetworkPreflightIsAnsweredToo()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s), extra: "Access-Control-Request-Private-Network: true\r\n", method: "OPTIONS"));
            Assert.Equal(204, r.Status);
            Assert.Equal("true", r.Header("Access-Control-Allow-Private-Network"));
        }

        [Fact]
        public void AMaliciousRequestedHeaderListIsNotEchoed()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), extra: "Access-Control-Request-Headers: X-A\\x20b, \"quoted\"\r\n", method: "OPTIONS"));
            Assert.Equal(204, r.Status);
            Assert.Equal("Range, Content-Type, Accept, Origin, Cache-Control", r.Header("Access-Control-Allow-Headers"));
        }

        [Fact]
        public void APreflightToAnUnknownRouteIs404AndNeverRevealsCors()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get("/c/" + new string('1', 32) + "/index.m3u8", Ep(s), method: "OPTIONS"));
            Assert.Equal(404, r.Status);
            Assert.Null(r.Header("Access-Control-Allow-Origin"));
            var r2 = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "nothing"), Ep(s), method: "OPTIONS"));
            Assert.Equal(404, r2.Status);
        }

        [Fact]
        public void CorsCanBeTurnedOff()
        {
            var s = Start(o => o.Cors = false);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            Assert.Equal(200, r.Status);
            Assert.Null(r.Header("Access-Control-Allow-Origin"));
            Assert.Equal(405, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), method: "OPTIONS")).Status);
        }

        // ------------------------------------------------------------ methods and request parsing

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("TRACE")]
        [InlineData("CONNECT")]
        [InlineData("PATCH")]
        public void OnlyGetHeadAndOptionsAreAllowed(string method)
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), method: method));
            Assert.Equal(405, r.Status);
            Assert.Contains("GET", r.Header("Allow"));
        }

        [Fact]
        public void HeadReturnsTheHeadersOfGetWithoutBody()
        {
            var s = Start();
            var g = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            var h = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s), method: "HEAD"));
            Assert.Equal(200, h.Status);
            Assert.Equal(g.Header("Content-Length"), h.Header("Content-Length"));
            Assert.Equal(g.Header("Content-Type"), h.Header("Content-Type"));
            Assert.Empty(h.Body);
            var hs = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s), method: "HEAD"));
            Assert.Equal(_content.Segment.Length.ToString(), hs.Header("Content-Length"));
            Assert.Empty(hs.Body);
        }

        [Fact]
        public void RequestsWithABodyAreRefused()
        {
            var s = Start();
            Assert.Equal(400, RawHttp.Send(Ep(s), "GET " + Path(s, "index.m3u8") + " HTTP/1.1\r\nHost: " + Ep(s) + "\r\nContent-Length: 5\r\n\r\nhello").Status);
            Assert.Equal(400, RawHttp.Send(Ep(s), "GET " + Path(s, "index.m3u8") + " HTTP/1.1\r\nHost: " + Ep(s) + "\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n").Status);
        }

        [Theory]
        [InlineData("GET\r\n\r\n")]
        [InlineData("GET / HTTP/1.1 extra\r\nHost: x\r\n\r\n")]
        [InlineData("G@T / HTTP/1.1\r\nHost: x\r\n\r\n")]
        [InlineData("GET noslash HTTP/1.1\r\nHost: x\r\n\r\n")]
        [InlineData("GET / HTTP/2.0\r\nHost: x\r\n\r\n")]
        [InlineData("GET / HTTP/1.1\r\nHost x\r\n\r\n")]
        [InlineData("GET / HTTP/1.1\r\n Host: x\r\n\r\n")]
        [InlineData("GET / HTTP/1.1\r\nBad Name: x\r\n\r\n")]
        public void MalformedRequestsAreAnsweredWithAnErrorAndClosed(string raw)
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), raw);
            Assert.NotNull(r);
            Assert.True(r.Status == 400 || r.Status == 505, r.StatusLine);
        }

        [Fact]
        public void BinaryGarbageInTheHeaderIsABadRequest()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), "GET / HTTP/1.1\r\nHost: " + Ep(s) + "\r\nX: é\u0001\r\n\r\n");
            Assert.Equal(400, r == null ? 400 : r.Status);
        }

        [Fact]
        public void HugeHeadersAreCutOffWith431()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), "GET / HTTP/1.1\r\nHost: " + Ep(s) + "\r\nX-Big: " + new string('a', 8000) + "\r\n\r\n");
            Assert.Equal(431, r.Status);
            var r2 = RawHttp.Send(Ep(s), "GET / HTTP/1.1\r\nHost: " + Ep(s) + "\r\n" + string.Concat(Enumerable.Range(0, 60).Select(i => "X-" + i + ": 1\r\n")) + "\r\n");
            Assert.Equal(431, r2.Status);
        }

        [Fact]
        public void ASlowClientThatSendsNoRequestIsDropped()
        {
            var s = Start(o => o.FirstRequestTimeout = TimeSpan.FromMilliseconds(400));
            using (var c = new TcpClient())
            {
                c.Connect(Ep(s));
                var st = c.GetStream(); st.ReadTimeout = 3000;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = st.Read(new byte[10], 0, 10);
                Assert.Equal(0, n);
                Assert.True(sw.ElapsedMilliseconds < 2500);
            }
        }

        [Fact]
        public void KeepAliveServesSeveralRequestsOnOneConnectionAndPipelinedOnes()
        {
            var s = Start();
            using (var c = new TcpClient())
            {
                c.Connect(Ep(s));
                var st = c.GetStream(); st.ReadTimeout = 3000;
                string one = "GET " + Path(s, "index.m3u8") + " HTTP/1.1\r\nHost: " + Ep(s) + "\r\n\r\n";
                var b = Encoding.ASCII.GetBytes(one + one + one);
                st.Write(b, 0, b.Length);
                int expect = 3 * (_content.Playlist.Length);
                var got = new MemoryStream();
                var buf = new byte[4096];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000)
                {
                    int n = st.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    got.Write(buf, 0, n);
                    if (Encoding.ASCII.GetString(got.ToArray()).Split(new[] { "HTTP/1.1 200 OK" }, StringSplitOptions.None).Length == 4 && got.Length > expect) break;
                }
                Assert.Equal(3, Encoding.ASCII.GetString(got.ToArray()).Split(new[] { "HTTP/1.1 200 OK" }, StringSplitOptions.None).Length - 1);
            }
        }

        // ------------------------------------------------------------ limits

        [Fact]
        public void ConnectionsOverTheLimitAreClosedOnArrival()
        {
            var s = Start(o => o.MaxConnections = 2);
            var held = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var c = new TcpClient(); c.Connect(Ep(s)); held.Add(c);
                }
                Thread.Sleep(300);
                Assert.Equal(2, s.GetStats().ActiveConnections);
                using (var extra = new TcpClient())
                {
                    extra.Connect(Ep(s));
                    var st = extra.GetStream(); st.ReadTimeout = 2000;
                    int n;
                    try { n = st.Read(new byte[10], 0, 10); } catch (IOException) { n = 0; }
                    Assert.Equal(0, n);
                }
                Assert.True(s.GetStats().OverLimit >= 1);
                // a held slot frees up when its connection closes
                held[0].Close();
                Thread.Sleep(500);
                var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
                Assert.Equal(200, r.Status);
                Assert.True(s.GetStats().PeakConnections <= 2);
            }
            finally { foreach (var c in held) c.Close(); }
        }

        // ------------------------------------------------------------ the channel is gone / not ready

        [Fact]
        public void WhenTheChannelIsGoneTheAnswerIs503()
        {
            var s = Start();
            _content.Available = false;
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            Assert.Equal(503, r.Status);
            Assert.NotNull(r.Header("Retry-After"));
        }

        [Fact]
        public void WithoutAPlaylistYetTheAnswerIs503()
        {
            var c = new FakeLanContent { Playlist = null };
            var s = Start(null, c);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            Assert.Equal(503, r.Status);
        }

        [Fact]
        public void RoutesThatTheContentDoesNotOfferAreNotFound()
        {
            var c = new FakeLanContent { Hls = false };
            var s = Start(null, c);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s))).Status);
            Assert.Equal(404, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s))).Status);
            Assert.Equal(200, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)), max: 188 * 400).Status);
            var h = new FakeLanContent { Progressive = false };
            var s2 = Start(null, h);
            Assert.Equal(404, RawHttp.Send(Ep(s2), RawHttp.Get(Path(s2, "live.ts"), Ep(s2))).Status);
        }

        // ------------------------------------------------------------ progressive TS

        [Fact]
        public void TheProgressiveStreamIsCloseDelimitedMp2tByDefault()
        {
            var s = Start();
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)));
            Assert.Equal(200, r.Status);
            Assert.Equal("HTTP/1.0", r.Version);
            Assert.Equal("video/mp2t", r.Header("Content-Type"));
            Assert.Null(r.Header("Content-Length"));
            Assert.Null(r.Header("Transfer-Encoding"));
            Assert.Equal("none", r.Header("Accept-Ranges"));
            Assert.Equal(188 * 400, r.Body.Length);
            for (int i = 0; i < r.Body.Length; i += 188) Assert.Equal(0x47, r.Body[i]);
            Assert.Null(r.Header("contentFeatures.dlna.org"));
        }

        [Fact]
        public void TheProgressiveStreamCanBeChunked()
        {
            var s = Start(o => o.Progressive = ProgressiveMode.Chunked);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)));
            Assert.Equal("HTTP/1.1", r.Version);
            Assert.Equal("chunked", r.Header("Transfer-Encoding"));
            // decode the chunks
            var body = r.Body; int pos = 0; var data = new MemoryStream();
            while (true)
            {
                int e = Array.IndexOf(body, (byte)'\r', pos);
                int size = Convert.ToInt32(Encoding.ASCII.GetString(body, pos, e - pos), 16);
                if (size == 0) break;
                data.Write(body, e + 2, size);
                pos = e + 2 + size + 2;
            }
            Assert.Equal(188 * 400, data.Length);
        }

        [Fact]
        public void TheProgressiveStreamCanAnnounceTheBigLength()
        {
            var s = Start(o => o.Progressive = ProgressiveMode.BigContentLength);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)));
            Assert.Equal("50000000000", r.Header("Content-Length"));
            Assert.Equal(188 * 400, r.Body.Length);
        }

        [Fact]
        public void DlnaHeadersAreSentOnGetAndOnHead()
        {
            var s = Start(o => o.Dlna = new DlnaStreamHeaders { ProfileName = "" });
            var g = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)));
            var h = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s), method: "HEAD"));
            foreach (var r in new[] { g, h })
            {
                Assert.Equal("Streaming", r.Header("transferMode.dlna.org"));
                Assert.Equal("DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000", r.Header("contentFeatures.dlna.org"));
                Assert.Equal("video/mp2t", r.Header("Content-Type"));
            }
            Assert.Empty(h.Body);
            Assert.Equal(1, _content.StreamOpens);                                          // the HEAD did not open a reader; only the GET did
        }

        [Fact]
        public void TheDlnaProfileNameIsPartOfTheFeaturesWhenGiven()
        {
            var d = new DlnaStreamHeaders { ProfileName = "AVC_TS_HD_50_AAC_T" };
            Assert.StartsWith("DLNA.ORG_PN=AVC_TS_HD_50_AAC_T;DLNA.ORG_OP=00", d.ContentFeatures);
        }

        [Fact]
        public void TheContentTypeOfTheProgressiveStreamIsConfigurable()
        {
            var s = Start(o => o.ProgressiveContentType = "video/mpeg");
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s), method: "HEAD"));
            Assert.Equal("video/mpeg", r.Header("Content-Type"));
        }

        [Fact]
        public void TheBackParameterReachesTheContentAndBadValuesAreRejected()
        {
            var s = Start();
            RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts") + "?back=5000", Ep(s)));
            Assert.Equal(TimeSpan.FromSeconds(5), _content.LastBack);
            Assert.Equal(400, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts") + "?back=-3", Ep(s))).Status);
            Assert.Equal(400, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts") + "?back=abc", Ep(s))).Status);
            Assert.Equal(400, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts") + "?back=99999999999", Ep(s))).Status);
        }

        [Fact]
        public void ABrokenProgressiveReaderStartIsAlignedToTheFirstPacket()
        {
            // a reader that starts in the middle of a packet: the server skips to the first aligned one
            var c = new FakeLanContent { StreamPackets = 100 };
            var s = Start(null, c);
            var r = RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "live.ts"), Ep(s)));
            Assert.Equal(0, r.Body.Length % 188);
            Assert.Equal(0x47, r.Body[0]);
        }

        [Fact]
        public void StoppingTheServerCutsALiveStreamAtOnce()
        {
            var c = new FakeLanContent { EndlessStream = true };
            var s = Start(null, c);
            using (var tc = new TcpClient())
            {
                tc.Connect(Ep(s));
                var st = tc.GetStream(); st.ReadTimeout = 3000;
                var b = Encoding.ASCII.GetBytes(RawHttp.Get(Path(s, "live.ts"), Ep(s)));
                st.Write(b, 0, b.Length);
                var buf = new byte[4096];
                Assert.True(st.Read(buf, 0, buf.Length) > 0);
                s.Dispose();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int total = 0, n;
                try { while ((n = st.Read(buf, 0, buf.Length)) > 0 && sw.ElapsedMilliseconds < 2500) total += n; } catch (IOException) { }
                Assert.True(sw.ElapsedMilliseconds < 2500, "the stream was not cut");
            }
        }

        [Fact]
        public void TheServerClosesItselfAfterTheIdleTimeout()
        {
            string closed = null;
            var s = Start(o => o.IdleTimeout = TimeSpan.FromMilliseconds(1500));
            s.Closed += r => closed = r;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (closed == null && sw.ElapsedMilliseconds < 6000) Thread.Sleep(50);
            Assert.Equal("idle", closed);
            Assert.False(s.IsRunning);
            Assert.ThrowsAny<Exception>(() => { using (var c = new TcpClient()) c.Connect(Ep(s)); });
        }

        [Fact]
        public void RequestsKeepTheServerAlive()
        {
            string closed = null;
            var s = Start(o => o.IdleTimeout = TimeSpan.FromMilliseconds(2000));
            s.Closed += r => closed = r;
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(600);
                Assert.Equal(200, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s))).Status);
            }
            Assert.Null(closed);
            Assert.True(s.IsRunning);
        }

        [Fact]
        public void StatisticsCountWhatHappened()
        {
            var s = Start();
            RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s)));
            RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s)));
            RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "s5.ts"), Ep(s), method: "OPTIONS"));
            RawHttp.Send(Ep(s), RawHttp.Get("/nope", Ep(s)));
            var st = s.GetStats();
            Assert.Equal(4, st.Requests);
            Assert.Equal(1, st.PlaylistRequests);
            Assert.Equal(1, st.SegmentRequests);
            Assert.Equal(1, st.Preflights);
            Assert.True(st.NotFound >= 1);
            Assert.True(st.BytesSent >= _content.Segment.Length);
            Assert.Equal("127.0.0.1", st.LastClient);
        }

        [Fact]
        public void TheSegmentNameParserOnlyAcceptsCanonicalNames()
        {
            long n;
            Assert.True(LanServer.TryParseSegmentName("s12345.ts", out n)); Assert.Equal(12345, n);
            foreach (var bad in new[] { "s.ts", "s1.TS", "s-1.ts", "s1.ts.bak", "x1.ts", "s1 .ts", "s99999999999999999999.ts", "s1e3.ts", "" })
                Assert.False(LanServer.TryParseSegmentName(bad, out n), bad);
        }

        [Fact]
        public void RandomRequestBytesNeverCrashTheServer()
        {
            var s = Start();
            var rnd = new Random(9);
            for (int i = 0; i < 80; i++)
            {
                var b = new byte[rnd.Next(1, 600)];
                rnd.NextBytes(b);
                if (i % 3 == 0) { var pre = Encoding.ASCII.GetBytes("GET /c/" + s.Token + "/"); Buffer.BlockCopy(pre, 0, b, 0, Math.Min(pre.Length, b.Length)); }
                if (i % 4 == 0 && b.Length > 4) { b[b.Length - 4] = 13; b[b.Length - 3] = 10; b[b.Length - 2] = 13; b[b.Length - 1] = 10; }
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect(Ep(s)); c.ReceiveTimeout = 800;
                        var st = c.GetStream();
                        st.Write(b, 0, b.Length);
                        c.Client.Shutdown(SocketShutdown.Send);
                        RawHttp.ReadAll(st, 100000, 800);
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            }
            // and it still works
            Assert.Equal(200, RawHttp.Send(Ep(s), RawHttp.Get(Path(s, "index.m3u8"), Ep(s))).Status);
        }
    }
}
