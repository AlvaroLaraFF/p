using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Tests.P2P;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.App
{
    public class RateLimiterTests
    {
        [Fact]
        public void AllowsABurstThenLimitsToTheConfiguredRate()
        {
            var l = new RateLimiter(100000);
            int granted = 0;
            for (int i = 0; i < 100; i++) if (l.TryTake(16384)) granted++;
            Assert.InRange(granted, 3, 8);                    // small initial burst (64 KiB) and then nothing is left
            Assert.True(l.Denied > 0);
            Thread.Sleep(400);                                // 400 ms at 100 kB/s = 40 kB: two more blocks
            int more = 0;
            for (int i = 0; i < 10; i++) if (l.TryTake(16384)) more++;
            Assert.InRange(more, 1, 3);
        }

        [Fact]
        public void OptionsExposeNoLimiterWhenZero()
        {
            var o = new EngineOptions { UploadMaxBytesPerSec = 0 };
            Assert.Null(o.UploadLimiter);
            o.UploadMaxBytesPerSec = 5000;
            var a = o.UploadLimiter;
            Assert.NotNull(a);
            Assert.Same(a, o.UploadLimiter);
            o.UploadMaxBytesPerSec = 9000;
            Assert.Equal(9000, o.UploadLimiter.Rate);
        }
    }

    public class UploadSettingsTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d) try { d.Dispose(); } catch { } }

        private async Task<int> RequestBlocks(Action<EngineOptions> tweak, int blocks)
        {
            var t = TestTorrent.Create(8 * 65536, 65536, seed: 41);
            var seeder = new FakeSeeder(t); _d.Add(seeder);
            var o = new EngineOptions { EnableListener = true, EnableDht = false, EnableTrackers = false };
            o.StaticPeers.Add(seeder.EndPoint.ToString());
            tweak(o);
            var host = new AceEngineHost(o); _d.Add(host);
            host.Start();
            var path = Path.Combine(Path.GetTempPath(), "p-up-" + Guid.NewGuid().ToString("N") + ".torrent");
            File.WriteAllBytes(path, t.TorrentBytes);
            OpenedStream opened;
            try { opened = await host.OpenAsync(AceLink.Parse(path), null, CancellationToken.None); } finally { File.Delete(path); }
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) await http.GetByteArrayAsync(opened.Url);       // all in RAM

            int pieces = 0;
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, host.ListenPort);
                var s = c.GetStream();
                var head = Wire.BuildHandshakeHead(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 }, t.InfoHash);
                await s.WriteAsync(head, 0, head.Length);
                var reply = new byte[68]; int got = 0;
                while (got < 68) got += await s.ReadAsync(reply, got, 68 - got);
                var pid = PeerId.Generate(); await s.WriteAsync(pid, 0, 20);
                await s.WriteAsync(Wire.Interested(), 0, 5);
                bool unchoked = false;
                var end = DateTime.UtcNow.AddSeconds(3);
                var reader = Task.Run(async () =>
                {
                    var lb = new byte[4];
                    try
                    {
                        while (DateTime.UtcNow < end)
                        {
                            int g = 0; while (g < 4) { int n = await s.ReadAsync(lb, g, 4 - g); if (n == 0) return; g += n; }
                            uint len = Wire.GetU32(lb, 0); if (len == 0) continue;
                            var m = new byte[len]; g = 0; while (g < len) { int n = await s.ReadAsync(m, g, (int)len - g); if (n == 0) return; g += n; }
                            if (m[0] == WireId.Unchoke) unchoked = true;
                            if (m[0] == WireId.Piece) Interlocked.Increment(ref pieces);
                        }
                    }
                    catch { }
                });
                var until = DateTime.UtcNow.AddSeconds(5);
                while (!unchoked && DateTime.UtcNow < until) await Task.Delay(50);
                if (unchoked)
                    for (int i = 0; i < blocks; i++) { var r = Wire.Request(i % 8, 0, 16384); await s.WriteAsync(r, 0, r.Length); }
                await Task.Delay(1500);
            }
            return pieces;
        }

        [Fact]
        public async Task UploadEnabledServesBlocks()
        {
            int n = await RequestBlocks(o => { o.UploadSlots = 4; o.UploadMaxBytesPerSec = 0; }, 20);
            Assert.True(n >= 15, "blocks served: " + n);
        }

        [Fact]
        public async Task UploadRateLimitDropsRequestsOverTheBudget()
        {
            int n = await RequestBlocks(o => { o.UploadSlots = 4; o.UploadMaxBytesPerSec = 32 * 1024; }, 40);
            Assert.InRange(n, 1, 12);                 // burst of ~64 KiB and then about 32 kB/s
        }

        [Fact]
        public async Task UploadDisabledServesNothingAndNeverUnchokes()
        {
            int n = await RequestBlocks(o => { o.UploadSlots = 0; }, 20);
            Assert.Equal(0, n);
        }
    }
}
