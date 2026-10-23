using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AceEngine.Streaming;
using Xunit;

namespace AceEngine.Tests.Streaming
{
    /// <summary>Live stream (without Content-Length), chunked / close-delimited and TS alignment.</summary>
    public class LiveServingTests
    {
        private static byte[] TsPackets(int n, int startCounter = 0)
        {
            var b = new byte[188 * n];
            for (int i = 0; i < n; i++)
            {
                b[i * 188] = 0x47;
                b[i * 188 + 1] = 0x01;
                b[i * 188 + 3] = (byte)(0x10 | ((startCounter + i) & 0x0f));
                for (int j = 4; j < 188; j++) b[i * 188 + j] = (byte)((i * 31 + j) & 0xff);
            }
            return b;
        }

        private static byte[] Junk(int n)
        {
            // garbage with a stray 0x47 here and there and a false positive of two sync bytes
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i % 11 == 0 ? 0x47 : 0x33);
            if (n > 400) { b[100] = 0x47; b[100 + 188] = 0x47; b[100 + 376] = 0x00; }
            return b;
        }

        private static async Task<(string head, byte[] body)> RawGet(LocalHttpServer srv, string url, string version = "HTTP/1.1")
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, srv.Port);
                var s = c.GetStream();
                var req = Encoding.ASCII.GetBytes("GET " + new Uri(url).AbsolutePath + " " + version + "\r\nHost: x\r\n\r\n");
                await s.WriteAsync(req, 0, req.Length);
                var head = await HttpServerTests.ReadHeadAsync(s);
                var body = await HttpServerTests.ReadToEndAsync(s);
                return (head, body);
            }
        }

        private static byte[] DechunkAll(byte[] raw, out bool terminated)
        {
            var ms = new MemoryStream();
            int p = 0;
            terminated = false;
            while (true)
            {
                int eol = Array.IndexOf(raw, (byte)'\r', p);
                if (eol < 0) break;
                int size = Convert.ToInt32(Encoding.ASCII.GetString(raw, p, eol - p), 16);
                p = eol + 2;
                if (size == 0) { terminated = true; break; }
                ms.Write(raw, p, size);
                p += size;
                Assert.Equal((byte)'\r', raw[p]); Assert.Equal((byte)'\n', raw[p + 1]);
                p += 2;
            }
            return ms.ToArray();
        }

        [Fact]
        public async Task ChunkedLiveStartsAtFirstValidTsPacketAndHasNoContentLength()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { LiveTransfer = LiveTransfer.Chunked }))
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var packets = TsPackets(40);
                var junk = Junk(1234);
                live.Push(junk.Take(500).ToArray());                // the garbage arrives in small chunks
                live.Push(junk.Skip(500).ToArray());
                live.Push(packets.Take(1000).ToArray());            // and the packets cut in the middle
                live.Push(packets.Skip(1000).ToArray());
                live.Complete();

                var (head, raw) = await RawGet(srv, url);
                Assert.StartsWith("HTTP/1.1 200", head);
                Assert.Contains("Transfer-Encoding: chunked", head);
                Assert.DoesNotContain("Content-Length", head);
                Assert.DoesNotContain("Accept-Ranges", head);
                Assert.Contains("Content-Type: video/mp2t", head);
                bool term;
                var body = DechunkAll(raw, out term);
                Assert.True(term);
                Assert.Equal(packets, body);                         // exactly from the first valid packet
            }
        }

        [Fact]
        public async Task CloseDelimitedLiveUsesHttp10WithoutLengthAndClosesAtEnd()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { LiveTransfer = LiveTransfer.CloseDelimited }))
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var packets = TsPackets(30);
                live.Push(Junk(700));
                live.Push(packets);
                live.Complete();
                var (head, body) = await RawGet(srv, url);
                Assert.StartsWith("HTTP/1.0 200", head);
                Assert.Contains("Connection: close", head);
                Assert.DoesNotContain("Content-Length", head);
                Assert.DoesNotContain("Transfer-Encoding", head);
                Assert.Equal(packets, body);
            }
        }

        [Fact]
        public async Task StreamAlreadyAlignedIsSentUntouchedAndContinuesToFlow()
        {
            using (var srv = new LocalHttpServer())
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var first = TsPackets(10);
                live.Push(first);
                var task = RawGet(srv, url);
                await Task.Delay(300);
                var second = TsPackets(10, 10);
                live.Push(second);                                   // it keeps sending without realigning
                await Task.Delay(100);
                live.Complete();
                var (head, raw) = await task;
                bool term;
                var body = DechunkAll(raw, out term);
                Assert.Equal(first.Concat(second).ToArray(), body);
            }
        }

        [Fact]
        public async Task HttpClientCanConsumeChunkedLive()
        {
            using (var srv = new LocalHttpServer())
            using (var http = new HttpClient())
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var packets = TsPackets(200);
                live.Push(Junk(3000));
                for (int i = 0; i < 200; i += 20) live.Push(packets.Skip(i * 188).Take(20 * 188).ToArray());
                live.Complete();
                var body = await http.GetByteArrayAsync(url);
                Assert.Equal(packets, body);
            }
        }

        [Fact]
        public async Task RangeIsIgnoredInLive()
        {
            using (var srv = new LocalHttpServer())
            using (var http = new HttpClient())
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var packets = TsPackets(20);
                live.Push(packets);
                live.Complete();
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Range", "bytes=100-200");
                var r = await http.SendAsync(req);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Null(r.Content.Headers.ContentRange);
                Assert.Equal(packets, await r.Content.ReadAsByteArrayAsync());
            }
        }

        [Fact]
        public async Task LiveHeadReturnsHeadersOnly()
        {
            using (var srv = new LocalHttpServer())
            using (var http = new HttpClient())
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Equal("video/mp2t", r.Content.Headers.ContentType.MediaType);
                Assert.Null(r.Content.Headers.ContentLength);
            }
        }

        [Fact]
        public async Task Http10ClientGetsCloseDelimitedEvenInChunkedMode()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { LiveTransfer = LiveTransfer.Chunked }))
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var packets = TsPackets(10);
                live.Push(packets); live.Complete();
                var (head, body) = await RawGet(srv, url, "HTTP/1.0");
                Assert.StartsWith("HTTP/1.0 200", head);
                Assert.DoesNotContain("chunked", head);
                Assert.Equal(packets, body);
            }
        }

        [Fact]
        public async Task LiveWithoutSyncIsSentAsIsAfterScanLimit()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { MaxAlignScanBytes = 4096, LiveTransfer = LiveTransfer.CloseDelimited }))
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var noise = new byte[20000];
                for (int i = 0; i < noise.Length; i++) noise[i] = 0x55;
                live.Push(noise); live.Complete();
                var (head, body) = await RawGet(srv, url);
                Assert.True(body.Length > 0);
                Assert.All(body, b => Assert.Equal(0x55, b));
            }
        }

        [Fact]
        public async Task AlignmentCanBeDisabled()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { AlignLiveToTs = false, LiveTransfer = LiveTransfer.CloseDelimited }))
            {
                srv.Start();
                var live = new PushLiveSession();
                var url = srv.Register(live);
                var data = Junk(900).Concat(TsPackets(5)).ToArray();
                live.Push(data); live.Complete();
                var (head, body) = await RawGet(srv, url);
                Assert.Equal(data, body);
            }
        }
    }
}
