using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Streaming;
using Xunit;

namespace AceEngine.Tests.Streaming
{
    /// <summary>Local HTTP server with a test session that serves a local file.</summary>
    public class HttpServerTests : IDisposable
    {
        private readonly string _dir;
        private readonly byte[] _data;
        private readonly string _file;
        private readonly LocalHttpServer _server;
        private readonly LocalFileSession _session;
        private readonly string _url;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public HttpServerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "p-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _data = new byte[3 * 1024 * 1024 + 123];
            new Random(42).NextBytes(_data);
            _file = Path.Combine(_dir, "video.mp4");
            File.WriteAllBytes(_file, _data);
            _server = new LocalHttpServer();
            _server.Start();
            _session = new LocalFileSession(_file);
            _url = _server.Register(_session);
        }

        public void Dispose()
        {
            _http.Dispose();
            _server.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string Sha1(byte[] b, int off, int len)
        {
            using (var s = SHA1.Create()) return BitConverter.ToString(s.ComputeHash(b, off, len)).Replace("-", "").ToLowerInvariant();
        }

        private async Task<HttpResponseMessage> Get(string url, string range = null, HttpMethod method = null)
        {
            var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            if (range != null) req.Headers.TryAddWithoutValidation("Range", range);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        }

        [Fact]
        public void BindsToLoopbackOnADynamicPort()
        {
            Assert.True(_server.Port > 0);
            Assert.StartsWith("http://127.0.0.1:" + _server.Port + "/stream/", _url);
            Assert.Equal(16, _session.Id.Length);
            Assert.Matches("^[0-9a-f]{16}$", _session.Id);
        }

        [Fact]
        public async Task FullGetReturns200WithLengthAndAcceptRanges()
        {
            var r = await Get(_url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(_data.Length, r.Content.Headers.ContentLength);
            Assert.Equal("bytes", r.Headers.AcceptRanges.Single());
            Assert.Equal("video/mp4", r.Content.Headers.ContentType.MediaType);
            Assert.Contains("no-cache", r.Headers.CacheControl.ToString());
            var body = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(_data.Length, body.Length);
            Assert.Equal(Sha1(_data, 0, _data.Length), Sha1(body, 0, body.Length));
        }

        [Theory]
        [InlineData("bytes=0-0", 0, 0)]
        [InlineData("bytes=1000000-1999999", 1000000, 1999999)]   // a typical player range
        [InlineData("bytes=3000000-", 3000000, 3145850)]
        [InlineData("bytes=-1000", 3144851, 3145850)]
        [InlineData("bytes=3145800-9999999", 3145800, 3145850)]
        public async Task RangeReturns206WithExactBytes(string range, long first, long last)
        {
            var r = await Get(_url, range);
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal(last - first + 1, r.Content.Headers.ContentLength);
            Assert.Equal(first, r.Content.Headers.ContentRange.From);
            Assert.Equal(last, r.Content.Headers.ContentRange.To);
            Assert.Equal(_data.Length, r.Content.Headers.ContentRange.Length);
            var body = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(last - first + 1, body.Length);
            Assert.Equal(Sha1(_data, (int)first, (int)(last - first + 1)), Sha1(body, 0, body.Length));
        }

        [Theory]
        [InlineData("bytes=3145851-")]
        [InlineData("bytes=5000000-6000000")]
        [InlineData("bytes=10-5")]
        [InlineData("bytes=abc")]
        [InlineData("bytes=-0")]
        public async Task InvalidRangeReturns416WithContentRangeStar(string range)
        {
            var r = await Get(_url, range);
            Assert.Equal((HttpStatusCode)416, r.StatusCode);
            Assert.Equal("bytes */" + _data.Length, string.Join("", r.Content.Headers.GetValues("Content-Range")));
        }

        [Fact]
        public async Task MultipleRangesUseTheFirstOne()
        {
            var r = await Get(_url, "bytes=5-9,100-200");
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            var body = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(_data.Skip(5).Take(5).ToArray(), body);
        }

        [Fact]
        public async Task UnknownRangeUnitIsIgnored()
        {
            var r = await Get(_url, "items=0-9");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(_data.Length, r.Content.Headers.ContentLength);
        }

        [Fact]
        public async Task HeadHasSameHeadersAndNoBodyAndOpensNoReader()
        {
            var mem = new MemorySession(new byte[5000]);
            var url = _server.Register(mem);
            var r = await Get(url, null, HttpMethod.Head);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(5000, r.Content.Headers.ContentLength);
            Assert.Equal("bytes", r.Headers.AcceptRanges.Single());
            Assert.Empty(await r.Content.ReadAsByteArrayAsync());
            var rr = await Get(url, "bytes=10-19", HttpMethod.Head);
            Assert.Equal(HttpStatusCode.PartialContent, rr.StatusCode);
            Assert.Equal(10, rr.Content.Headers.ContentLength);
            Assert.Equal(0, mem.OpenedReaders);
        }

        [Fact]
        public async Task GetOpensReaderAtRangeStart()
        {
            var mem = new MemorySession(new byte[100000]);
            var url = _server.Register(mem);
            (await Get(url, "bytes=12345-")).Dispose();
            var r = await Get(url, "bytes=12345-");
            await r.Content.ReadAsByteArrayAsync();
            Assert.Contains(12345L, mem.OpenedOffsets);
        }

        [Fact]
        public async Task UnknownPathsAnd404AndMethodErrors()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Get(_server.BaseUrl + "/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(_server.BaseUrl + "/stream/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(_server.BaseUrl + "/stream/0000000000000000")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(_server.BaseUrl + "/other/" + _session.Id)).StatusCode);
            var post = await _http.PostAsync(_url, new StringContent("x"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            Assert.Equal(new[] { "GET", "HEAD" }, post.Content.Headers.Allow.ToArray());
        }

        [Fact]
        public async Task QueryStringIsIgnored()
        {
            var r = await Get(_url + "?foo=bar");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        [Fact]
        public async Task KeepAliveServesSeveralRequestsOnOneConnection()
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                var s = c.GetStream();
                var path = new Uri(_url).AbsolutePath;
                for (int i = 0; i < 3; i++)
                {
                    var req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: x\r\nRange: bytes=" + (i * 10) + "-" + (i * 10 + 9) + "\r\n\r\n");
                    await s.WriteAsync(req, 0, req.Length);
                    var head = await ReadHeadAsync(s);
                    Assert.StartsWith("HTTP/1.1 206", head);
                    Assert.Contains("keep-alive", head);
                    var body = await ReadExactAsync(s, 10);
                    Assert.Equal(_data.Skip(i * 10).Take(10).ToArray(), body);
                }
            }
        }

        [Fact]
        public async Task ConnectionCloseAndHttp10CloseTheSocket()
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                var s = c.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + new Uri(_url).AbsolutePath + " HTTP/1.1\r\nHost: x\r\nRange: bytes=0-4\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(req, 0, req.Length);
                var all = await ReadToEndAsync(s);
                var text = Encoding.ASCII.GetString(all, 0, Math.Max(0, all.Length - 5));
                Assert.Contains("Connection: close", text);
            }
        }

        [Fact]
        public async Task OversizedHeadersGet431()
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                var s = c.GetStream();
                var big = "GET /stream/x HTTP/1.1\r\nX-Big: " + new string('a', 12000) + "\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(big);
                try { await s.WriteAsync(bytes, 0, bytes.Length); } catch (IOException) { }
                var all = await ReadToEndAsync(s);
                Assert.StartsWith("HTTP/1.1 431", Encoding.ASCII.GetString(all));
            }
        }

        [Fact]
        public async Task GarbageRequestGets400()
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                var s = c.GetStream();
                var bytes = Encoding.ASCII.GetBytes("HOLA\r\n\r\n");
                await s.WriteAsync(bytes, 0, bytes.Length);
                var all = await ReadToEndAsync(s);
                Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(all));
            }
        }

        [Theory]
        [InlineData("a.ts", "video/mp2t")]
        [InlineData("a.mkv", "video/x-matroska")]
        [InlineData("a.avi", "video/x-msvideo")]
        [InlineData("a.webm", "video/webm")]
        [InlineData("a.mp3", "audio/mpeg")]
        [InlineData("a.bin", "application/octet-stream")]
        public async Task ContentTypeFollowsExtension(string name, string mime)
        {
            var f = Path.Combine(_dir, name);
            File.WriteAllBytes(f, new byte[100]);
            var url = _server.Register(new LocalFileSession(f));
            var r = await Get(url, null, HttpMethod.Head);
            Assert.Equal(mime, r.Content.Headers.ContentType.MediaType);
        }

        [Fact]
        public async Task EmptyFileIs200WithZeroLengthAndAnyRangeIs416()
        {
            var f = Path.Combine(_dir, "empty.mp4");
            File.WriteAllBytes(f, new byte[0]);
            var url = _server.Register(new LocalFileSession(f));
            var r = await Get(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(0, r.Content.Headers.ContentLength);
            var r2 = await Get(url, "bytes=0-");
            Assert.Equal((HttpStatusCode)416, r2.StatusCode);
        }

        [Fact]
        public async Task ManyConcurrentRangeClientsGetTheirOwnData()
        {
            var tasks = Enumerable.Range(0, 24).Select(async i =>
            {
                long first = i * 100000L + 7;
                long last = first + 65536 + i;
                var r = await Get(_url, "bytes=" + first + "-" + last);
                var body = await r.Content.ReadAsByteArrayAsync();
                Assert.Equal(Sha1(_data, (int)first, (int)(last - first + 1)), Sha1(body, 0, body.Length));
            }).ToArray();
            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task UnregisterClosesActiveConnectionsAndRemovesUrl()
        {
            var mem = new MemorySession(new byte[10 * 1024 * 1024]) { ReaderDelayMs = -1 };   // reader that never returns
            var url = _server.Register(mem);
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                var s = c.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + new Uri(url).AbsolutePath + " HTTP/1.1\r\nHost: x\r\n\r\n");
                await s.WriteAsync(req, 0, req.Length);
                var head = await ReadHeadAsync(s);
                Assert.StartsWith("HTTP/1.1 200", head);
                await WaitUntil(() => _server.ActiveConnections(mem.Id) == 1);
                Assert.True(_server.Unregister(mem.Id));
                var rest = await ReadToEndAsync(s);     // the server closes the connection
                Assert.Empty(rest);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await Get(url)).StatusCode);
        }

        [Fact]
        public async Task StalledReaderIsClosedAfterTimeoutWhenPlaying()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { ReadStallTimeout = TimeSpan.FromMilliseconds(400) }))
            {
                srv.Start();
                var mem = new MemorySession(new byte[1000]) { ReaderDelayMs = -1, StateValue = SessionState.Playing };
                var url = srv.Register(mem);
                using (var c = new TcpClient())
                {
                    await c.ConnectAsync(IPAddress.Loopback, srv.Port);
                    var s = c.GetStream();
                    var req = Encoding.ASCII.GetBytes("GET " + new Uri(url).AbsolutePath + " HTTP/1.1\r\nHost: x\r\n\r\n");
                    await s.WriteAsync(req, 0, req.Length);
                    await ReadHeadAsync(s);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var rest = await ReadToEndAsync(s);
                    Assert.Empty(rest);
                    Assert.InRange(sw.ElapsedMilliseconds, 300, 5000);
                }
            }
        }

        [Fact]
        public async Task StalledReaderKeepsWaitingWhileBuffering()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { ReadStallTimeout = TimeSpan.FromMilliseconds(200) }))
            {
                srv.Start();
                var mem = new MemorySession(new byte[1000]) { ReaderDelayMs = 900, StateValue = SessionState.Buffering };
                var url = srv.Register(mem);
                var r = await _http.GetAsync(url);
                Assert.Equal(1000, (await r.Content.ReadAsByteArrayAsync()).Length);
            }
        }

        [Fact]
        public async Task RequestWaitsForSessionReadinessAndFailureGives503()
        {
            var gate = new TaskCompletionSource<bool>();
            var mem = new MemorySession(new byte[500]) { ReadyTask = gate.Task, StateValue = SessionState.Resolving };
            var url = _server.Register(mem);
            var pending = _http.GetAsync(url);
            await Task.Delay(300);
            Assert.False(pending.IsCompleted);
            mem.StateValue = SessionState.Playing;
            gate.SetResult(true);
            Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);

            var fail = new TaskCompletionSource<bool>();
            var mem2 = new MemorySession(new byte[500]) { ReadyTask = fail.Task };
            var url2 = _server.Register(mem2);
            var p2 = _http.GetAsync(url2);
            fail.SetException(new InvalidOperationException("no metadata"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await p2).StatusCode);
        }

        [Fact]
        public async Task FailedOrStoppedSessionGives503()
        {
            var mem = new MemorySession(new byte[10]) { StateValue = SessionState.Failed };
            var url = _server.Register(mem);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(url)).StatusCode);
        }

        [Fact]
        public async Task IdleSessionIsStoppedAfterLastConnectionCloses()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { SessionIdleTimeout = TimeSpan.FromMilliseconds(700) }))
            {
                srv.Start();
                var mem = new MemorySession(new byte[100]);
                var url = srv.Register(mem);
                // Without connections it never stops (there has not been a player yet).
                await Task.Delay(1500);
                Assert.False(mem.Disposed);
                (await _http.GetAsync(url)).Dispose();
                await WaitUntil(() => mem.Disposed, 6000);
                Assert.True(mem.Disposed);
                Assert.Empty(srv.Sessions);
            }
        }

        [Fact]
        public async Task LargeTransferKeepsIntegrity()
        {
            var big = new byte[24 * 1024 * 1024];
            new Random(7).NextBytes(big);
            var mem = new MemorySession(big);
            var url = _server.Register(mem);
            var r = await _http.GetAsync(url);
            var body = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(Sha1(big, 0, big.Length), Sha1(body, 0, body.Length));
        }

        [Fact]
        public async Task DisposingTheServerClosesConnections()
        {
            var srv = new LocalHttpServer();
            srv.Start();
            var mem = new MemorySession(new byte[1000]) { ReaderDelayMs = -1 };
            var url = srv.Register(mem);
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, srv.Port);
                var s = c.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + new Uri(url).AbsolutePath + " HTTP/1.1\r\nHost: x\r\n\r\n");
                await s.WriteAsync(req, 0, req.Length);
                await ReadHeadAsync(s);
                srv.Dispose();
                var rest = await ReadToEndAsync(s);
                Assert.Empty(rest);
            }
        }

        [Fact]
        public async Task AlternativeBindAddressWithSelfOnlyFilter()
        {
            // if the TV does not accept 127.0.0.1, it binds to the local IP and accepts only its own connections.
            using (var srv = new LocalHttpServer(new HttpServerOptions { BindAddress = IPAddress.Any, OnlyAcceptSelf = true }))
            {
                srv.Start();
                var mem = new MemorySession(new byte[100]);
                var url = srv.Register(mem);
                var r = await _http.GetAsync(url.Replace("0.0.0.0", "127.0.0.1"));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.StartsWith("http://0.0.0.0:", url);          // BaseUrl uses the bind address; the app will put the real IP
            }
        }

        [Fact]
        public void SecondStartAndDoubleRegisterAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => _server.Start());
            Assert.Throws<InvalidOperationException>(() => _server.Register(_session));
        }

        // ---------- socket utilities ----------

        internal static async Task<string> ReadHeadAsync(NetworkStream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            using (var cts = new CancellationTokenSource(10000))
            {
                while (!sb.ToString().EndsWith("\r\n\r\n"))
                {
                    int n = await s.ReadAsync(one, 0, 1, cts.Token);
                    if (n == 0) break;
                    sb.Append((char)one[0]);
                }
            }
            return sb.ToString();
        }

        internal static async Task<byte[]> ReadExactAsync(NetworkStream s, int count)
        {
            var buf = new byte[count];
            int got = 0;
            using (var cts = new CancellationTokenSource(10000))
                while (got < count)
                {
                    int n = await s.ReadAsync(buf, got, count - got, cts.Token);
                    if (n == 0) throw new EndOfStreamException();
                    got += n;
                }
            return buf;
        }

        internal static async Task<byte[]> ReadToEndAsync(NetworkStream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[8192];
            using (var cts = new CancellationTokenSource(15000))
                while (true)
                {
                    int n;
                    try { n = await s.ReadAsync(buf, 0, buf.Length, cts.Token); }
                    catch (IOException) { break; }
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                }
            return ms.ToArray();
        }

        internal static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
        }
    }
}
