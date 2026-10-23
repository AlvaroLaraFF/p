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
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    public class UserSettingsStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "p-settings-" + Guid.NewGuid().ToString("N"));
        public UserSettingsStoreTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string P(string name = "settings.json") { return Path.Combine(_dir, name); }

        [Fact]
        public void MissingFileGivesTheDesignDefaults()
        {
            var s = new UserSettingsStore(P()).Load();
            Assert.True(s.OnlyLive); Assert.Equal(512, s.UploadKBps); Assert.True(s.UpnpEnabled); Assert.False(s.TsServiceEnabled); Assert.True(s.AceSearchEnabled);
            Assert.NotNull(new UserSettingsStore(null).Load());
        }

        [Fact]
        public void AnOldFileTurnsTheCentralSearchOnButAVersion2FileKeepsTheUsersChoice()
        {
            // settings.json from before version 2: its "false" was the old default value, not a choice
            File.WriteAllText(P(), "{\"onlyLive\":false,\"uploadKBps\":256,\"aceSearchEnabled\":false}");
            var s = new UserSettingsStore(P()).Load();
            Assert.True(s.AceSearchEnabled);
            Assert.False(s.OnlyLive); Assert.Equal(256, s.UploadKBps);                // the rest is respected
            // since version 2 what is saved rules, also when off
            s.AceSearchEnabled = false;
            Assert.True(new UserSettingsStore(P()).Save(s));
            Assert.Contains("\"version\"", File.ReadAllText(P()));
            Assert.False(new UserSettingsStore(P()).Load().AceSearchEnabled);
            File.WriteAllText(P(), "{\"version\":\"dos\",\"aceSearchEnabled\":false}");   // version that is not a number: like an old file
            Assert.True(new UserSettingsStore(P()).Load().AceSearchEnabled);
        }

        [Fact]
        public void TsServiceIsOffByDefaultAndAnOldSavedValueIsIgnoredAndNotWrittenAnymore()
        {
            Assert.False(new UserSettings().TsServiceEnabled);
            Assert.False(new AppConfig().TsService);
            // an old settings.json with the fallback route enabled (it was the default value) does NOT enable it
            File.WriteAllText(P(), "{\"onlyLive\":false,\"tsServiceEnabled\":true,\"uploadKBps\":1024}");
            var s = new UserSettingsStore(P()).Load();
            Assert.False(s.TsServiceEnabled); Assert.False(s.OnlyLive); Assert.Equal(1024, s.UploadKBps);
            // and it is no longer written
            Assert.DoesNotContain("tsServiceEnabled", UserSettingsStore.ToJson(new UserSettings { TsServiceEnabled = true }));
            // the engine does not use it nor query those servers
            var o = EngineOptionsFactory.Build(new AppConfig(), s, null);
            Assert.False(o.Resolver.EnableTsService);
            Assert.False(o.Probe.ResolveViaTsService);
            // only the configuration key (tests) turns it on
            Assert.True(AppConfig.Parse("{\"tsService\":true}").TsService);
            Assert.False(AppConfig.Parse("{\"tsService\":\"si\"}").TsService);
        }

        [Fact]
        public void SaveAndLoadRoundTripWithoutLeavingTemporaryFiles()
        {
            var store = new UserSettingsStore(Path.Combine(_dir, "sub", "settings.json"));        // the directory does not exist: it is created
            var s = new UserSettings { OnlyLive = false, UploadKBps = 2048, UpnpEnabled = false, AceSearchEnabled = true };
            Assert.True(store.Save(s));
            var l = store.Load();
            Assert.False(l.OnlyLive); Assert.Equal(2048, l.UploadKBps); Assert.False(l.UpnpEnabled); Assert.True(l.AceSearchEnabled);
            // second save: it replaces
            s.UploadKBps = 128; s.AceSearchEnabled = false;
            Assert.True(store.Save(s));
            l = store.Load();
            Assert.Equal(128, l.UploadKBps); Assert.False(l.AceSearchEnabled);
            Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(Path.Combine(_dir, "sub")).Select(Path.GetFileName).ToArray());
            // the JSON is readable by AppConfig (same file in ConfigLoader's layers) without touching its upload
            var cfg = AppConfig.Parse(File.ReadAllText(store.FilePath));
            Assert.Equal(new AppConfig().UploadMaxBytesPerSec, cfg.UploadMaxBytesPerSec);
            Assert.Equal(new AppConfig().UploadSlots, cfg.UploadSlots);
        }

        [Fact]
        public void SaveNeverThrowsWhenTheTargetCannotBeWritten()
        {
            Directory.CreateDirectory(P("es-un-directorio"));
            Assert.False(new UserSettingsStore(P("es-un-directorio")).Save(new UserSettings()));
            Assert.False(new UserSettingsStore(null).Save(new UserSettings()));
            Assert.False(new UserSettingsStore(P()).Save(null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("esto no es json")]
        [InlineData("{")]
        [InlineData("[1,2,3]")]
        [InlineData("42")]
        [InlineData("null")]
        [InlineData("\"hola\"")]
        [InlineData("{\"onlyLive\":\"no\",\"uploadKBps\":\"mucho\",\"upnpEnabled\":3,\"tsServiceEnabled\":null,\"aceSearchEnabled\":[true]}")]
        public void GarbageGivesTheDefaults(string json)
        {
            File.WriteAllText(P(), json);
            var s = new UserSettingsStore(P()).Load();
            Assert.True(s.OnlyLive); Assert.Equal(512, s.UploadKBps); Assert.True(s.UpnpEnabled); Assert.False(s.TsServiceEnabled); Assert.True(s.AceSearchEnabled);
        }

        [Fact]
        public void BinaryGarbageAndOversizedFilesGiveTheDefaults()
        {
            var rnd = new Random(7);
            var junk = new byte[5000]; rnd.NextBytes(junk);
            File.WriteAllBytes(P(), junk);
            Assert.Equal(512, new UserSettingsStore(P()).Load().UploadKBps);
            // 64 KiB + 1: it is ignored even if it is valid JSON
            var big = "{\"uploadKBps\":2048,\"relleno\":\"" + new string('x', UserSettingsStore.MaxFileBytes) + "\"}";
            File.WriteAllText(P(), big);
            var s = new UserSettingsStore(P()).Load();
            Assert.Equal(512, s.UploadKBps);
            // exactly at the limit it does fit: it is read
            File.WriteAllText(P(), "{\"uploadKBps\":2048,\"relleno\":\"" + new string('x', UserSettingsStore.MaxFileBytes - 60) + "\"}");
            Assert.Equal(2048, new UserSettingsStore(P()).Load().UploadKBps);
        }

        [Theory]
        [InlineData("0", 128)]
        [InlineData("-5", 128)]
        [InlineData("1", 128)]
        [InlineData("128", 128)]
        [InlineData("192", 128)]            // midpoint between 128 and 256: the lower one
        [InlineData("193", 256)]
        [InlineData("300", 256)]
        [InlineData("384", 256)]            // tie between 256 and 512: the lower one
        [InlineData("385", 512)]
        [InlineData("700", 512)]
        [InlineData("900", 1024)]
        [InlineData("1500", 1024)]
        [InlineData("1600", 2048)]
        [InlineData("99999999", 2048)]
        [InlineData("1e30", 2048)]
        [InlineData("-1e30", 128)]
        [InlineData("\"700\"", 512)]
        [InlineData("512.4", 512)]
        public void UploadIsCorrectedToTheNearestAllowedStep(string value, int expected)
        {
            File.WriteAllText(P(), "{\"uploadKBps\":" + value + "}");
            Assert.Equal(expected, new UserSettingsStore(P()).Load().UploadKBps);
            Assert.Contains(expected, UserSettings.UploadStepsKBps);
        }

        [Fact]
        public void TheOldFormatIsMigratedAndUploadEnabledIsIgnored()
        {
            File.WriteAllText(P(), "{\"uploadEnabled\":false,\"uploadMaxBytesPerSec\":262144,\"uploadSlots\":2}");
            var s = new UserSettingsStore(P()).Load();
            Assert.Equal(256, s.UploadKBps);
            // the upload cannot be turned off: there is no property for that, and on saving the old keys disappear
            var store = new UserSettingsStore(P());
            Assert.True(store.Save(s));
            var text = File.ReadAllText(P());
            Assert.DoesNotContain("uploadEnabled", text); Assert.DoesNotContain("uploadMaxBytesPerSec", text); Assert.DoesNotContain("uploadSlots", text);
            Assert.Contains("\"uploadKBps\": 256", text);
            // uploadKBps takes precedence over the old format
            File.WriteAllText(P(), "{\"uploadKBps\":2048,\"uploadMaxBytesPerSec\":131072}");
            Assert.Equal(2048, new UserSettingsStore(P()).Load().UploadKBps);
            // bytes of another size: rounded to the nearest step
            File.WriteAllText(P(), "{\"uploadMaxBytesPerSec\":1000000}");
            Assert.Equal(1024, new UserSettingsStore(P()).Load().UploadKBps);
            File.WriteAllText(P(), "{\"uploadMaxBytesPerSec\":1000}");
            Assert.Equal(128, new UserSettingsStore(P()).Load().UploadKBps);
        }

        [Fact]
        public void SaveWritesTheCorrectedUploadWithoutChangingTheObject()
        {
            var store = new UserSettingsStore(P());
            var s = new UserSettings { UploadKBps = 300 };
            Assert.True(store.Save(s));
            Assert.Equal(300, s.UploadKBps);
            Assert.Equal(256, store.Load().UploadKBps);
        }

        [Fact]
        public void ABomIsTolerated()
        {
            File.WriteAllBytes(P(), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"onlyLive\":false}")).ToArray());
            Assert.False(new UserSettingsStore(P()).Load().OnlyLive);
        }

        [Fact]
        public void ConcurrentSavesAlwaysLeaveAValidFile()
        {
            var store = new UserSettingsStore(P());
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                for (int k = 0; k < 25; k++) { store.Save(new UserSettings { UploadKBps = UserSettings.UploadStepsKBps[(i + k) % 5], OnlyLive = (k % 2) == 0 }); store.Load(); }
            })).ToArray();
            Task.WaitAll(tasks);
            var s = store.Load();
            Assert.Contains(s.UploadKBps, UserSettings.UploadStepsKBps);
            Assert.Single(Directory.GetFiles(_dir));
        }
    }

    public class EngineSettingsTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d) try { d.Dispose(); } catch { } }

        [Fact]
        public void BuildAppliesTheUserSettingsOnTopOfTheTvProfile()
        {
            var o = EngineOptionsFactory.Build(new AppConfig { UploadSlots = 3 }, new UserSettings { UploadKBps = 1024, TsServiceEnabled = false }, null);
            Assert.Equal(1024 * 1024, o.UploadMaxBytesPerSec);
            Assert.False(o.Resolver.EnableTsService);
            Assert.Equal(3, o.UploadSlots); Assert.Equal(3, o.Live.UploadSlots);
            // the TV's prudent profile for the probe stays the same
            Assert.Equal(12, o.Probe.Concurrency); Assert.Equal(52, o.Probe.TcpBudget); Assert.Equal(128, o.DhtMaxInFlight);
            Assert.False(EngineOptionsFactory.Build(new AppConfig(), new UserSettings(), null).Resolver.EnableTsService);          // by default it is not used (removed from the app)
            Assert.True(EngineOptionsFactory.Build(new AppConfig(), new UserSettings { TsServiceEnabled = true }, null).Resolver.EnableTsService);   // the engine's code is kept
            Assert.Equal(512 * 1024, EngineOptionsFactory.Build(new AppConfig(), new UserSettings(), null).UploadMaxBytesPerSec);
            Assert.Equal(512 * 1024, EngineOptionsFactory.Build(new AppConfig(), (UserSettings)null, null).UploadMaxBytesPerSec);
        }

        [Fact]
        public void TheOldOverloadKeepsUsingTheConfigurationValues()
        {
            var o = EngineOptionsFactory.Build(new AppConfig { UploadSlots = 5, UploadMaxBytesPerSec = 1000 }, null);
            Assert.Equal(1000, o.UploadMaxBytesPerSec); Assert.Equal(5, o.UploadSlots);
        }

        [Theory]
        [InlineData(-100, 128)]
        [InlineData(0, 128)]
        [InlineData(1, 128)]
        [InlineData(128, 128)]
        [InlineData(300, 300)]
        [InlineData(2048, 2048)]
        [InlineData(100000, 2048)]
        [InlineData(int.MaxValue, 2048)]
        [InlineData(int.MinValue, 128)]
        public void TheUploadRateIsAlwaysBetween128KBpsAnd2MBps(int kbps, int expectedKBps)
        {
            Assert.Equal(expectedKBps * 1024L, EngineOptionsFactory.UploadBytesPerSec(kbps));
            var o = EngineOptionsFactory.Build(new AppConfig(), new UserSettings { UploadKBps = kbps }, null);
            Assert.Equal(expectedKBps * 1024L, o.UploadMaxBytesPerSec);
            Assert.NotNull(o.UploadLimiter);
        }

        [Fact]
        public void NoUserSettingOrConfigurationCanTurnTheUploadOff()
        {
            // neither the programmatic configuration with 0 slots nor any Settings value leaves the upload at zero
            var cfg = new AppConfig { UploadSlots = 0, UploadMaxBytesPerSec = 0 };
            var o = EngineOptionsFactory.Build(cfg, new UserSettings { UploadKBps = 0 }, null);
            Assert.True(o.UploadSlots >= 1); Assert.True(o.Live.UploadSlots >= 1); Assert.True(o.UploadMaxBytesPerSec >= 128 * 1024);
            Assert.True(EngineOptionsFactory.Build(cfg, null).UploadSlots >= 1);
            Assert.True(AppConfig.Parse("{\"uploadSlots\":0,\"uploadEnabled\":false}").UploadSlots >= 1);
            // and the configuration JSON does not know about 'uploadEnabled'
            Assert.DoesNotContain("uploadEnabled", new AppConfig().ToJson());
            Assert.DoesNotContain(typeof(UserSettings).GetFields(), f => f.Name.IndexOf("upload", StringComparison.OrdinalIgnoreCase) >= 0 && f.FieldType == typeof(bool));
        }

        [Fact]
        public void ApplyUserSettingsChangesTheRunningEngineWithoutRestartingIt()
        {
            var host = new AceEngineHost(EngineOptionsFactory.Build(new AppConfig(), new UserSettings(), null)); _d.Add(host);
            var limiter0 = host.Options.UploadLimiter;
            Assert.Equal(512 * 1024, limiter0.Rate);
            host.ApplyUserSettings(new UserSettings { UploadKBps = 128, TsServiceEnabled = false });
            Assert.Equal(128 * 1024, host.Options.UploadMaxBytesPerSec);
            Assert.Equal(128 * 1024, host.Options.UploadLimiter.Rate);
            Assert.NotSame(limiter0, host.Options.UploadLimiter);
            Assert.False(host.Options.Resolver.EnableTsService);
            Assert.True(host.Options.UploadSlots >= 1);                         // the slots are not touched
            host.ApplyUserSettings(new UserSettings { UploadKBps = 2048, TsServiceEnabled = true });
            Assert.Equal(2048 * 1024, host.Options.UploadLimiter.Rate); Assert.True(host.Options.Resolver.EnableTsService);
            Assert.Throws<ArgumentNullException>(() => host.ApplyUserSettings(null));
            Assert.False(host.IsDisposed);
            host.Dispose();
            Assert.True(host.IsDisposed);
        }

        [Fact]
        public void ApplyUserSettingsKeepsTheToolHooksOfTheEngineIntact()
        {
            // UploadSlots = 0 (CLI --no-upload and tests) still exists as a tool: applying settings does not touch it
            var o = new EngineOptions { UploadSlots = 0 };
            var host = new AceEngineHost(o); _d.Add(host);
            host.ApplyUserSettings(new UserSettings());
            Assert.Equal(0, o.UploadSlots);
        }

        [Fact]
        public async Task TsServiceCanBeSwitchedOnAndOffWhileTheEngineRuns()
        {
            var t1 = TestUtil.Fixture("real/38f7b2044e549df2039ff26cefa6f9a60c854d5e.acelive");
            var t2 = TestUtil.Fixture("real/54a4a28882d3cae1ca19de5d4b022ebea40351f9.acelive");
            string id1 = Hex.ToHex(HashUtil.Sha1(t1)), id2 = Hex.ToHex(HashUtil.Sha1(t2));
            using (var ts = new FakeTsService(new ResolverOptions().TsSecret))
            {
                ts.Handler = q => "<response r=\"1\"><torrent>" + Convert.ToBase64String(q["pid"] == id1 ? t1 : t2) + "</torrent></response>";
                var o = EngineOptionsFactory.Build(new AppConfig(), new UserSettings { TsServiceEnabled = true }, null);
                o.EnableDht = false; o.EnableTrackers = false; o.EnableListener = false;
                o.Resolver.EnableP2p = false;
                o.Resolver.TsHosts = new List<string> { ts.Url };
                var host = new AceEngineHost(o); _d.Add(host);
                Assert.Equal("server", (await host.Resolver.ResolveAsync(AceLink.ContentId(id1), CancellationToken.None)).Source);
                Assert.Equal(1, ts.Requests.Count);
                host.ApplyUserSettings(new UserSettings { TsServiceEnabled = false });
                // without TSService and without P2P there is no route: the next resolution does not even touch the server
                await Assert.ThrowsAsync<ResolveException>(() => host.Resolver.ResolveAsync(AceLink.ContentId(id2), CancellationToken.None));
                Assert.Equal(1, ts.Requests.Count);
                host.ApplyUserSettings(new UserSettings { TsServiceEnabled = true });
                Assert.Equal("server", (await host.Resolver.ResolveAsync(AceLink.ContentId(id2), CancellationToken.None)).Source);
                Assert.Equal(2, ts.Requests.Count);
            }
        }

        // ---------- The upload service respects the limit, also after changing it on the fly ----------

        /// <summary>A client that requests 'blocks' blocks of 16 KiB, one every 'paceMs' (the limiter's initial burst is 64 KiB: requesting everything at once only measures the burst). Returns what was served and how long the request took.</summary>
        private static async Task<KeyValuePair<int, double>> ServedBlocks(AceEngineHost host, TestTorrent t, int blocks, int paceMs)
        {
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
                var end = DateTime.UtcNow.AddSeconds(60);
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
                var until = DateTime.UtcNow.AddSeconds(8);
                while (!unchoked && DateTime.UtcNow < until) await Task.Delay(50);
                Assert.True(unchoked, "the engine did not unchoke the client");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < blocks; i++)
                {
                    var r = Wire.Request(i % 8, 0, 16384);
                    await s.WriteAsync(r, 0, r.Length);
                    await Task.Delay(paceMs);
                }
                await Task.Delay(500);                               // what is still in transit
                return new KeyValuePair<int, double>(Volatile.Read(ref pieces), sw.Elapsed.TotalSeconds);
            }
        }

        [Fact]
        public async Task TheUploadServiceHonoursTheNewMaximumAfterApplyUserSettings()
        {
            var t = TestTorrent.Create(8 * 65536, 65536, seed: 43);
            var seeder = new FakeSeeder(t); _d.Add(seeder);
            var o = EngineOptionsFactory.Build(new AppConfig(), new UserSettings { UploadKBps = 2048 }, null);
            o.EnableListener = true; o.EnableDht = false; o.EnableTrackers = false; o.AllowLocalFileLinks = true;
            o.StaticPeers.Add(seeder.EndPoint.ToString());
            var host = new AceEngineHost(o); _d.Add(host);
            host.Start();
            var path = Path.Combine(Path.GetTempPath(), "p-set-" + Guid.NewGuid().ToString("N") + ".torrent");
            File.WriteAllBytes(path, t.TorrentBytes);
            OpenedStream opened;
            try { opened = await host.OpenAsync(AceLink.Parse(path), null, CancellationToken.None); } finally { File.Delete(path); }
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) await http.GetByteArrayAsync(opened.Url);       // all in RAM

            // At 2 MB/s, a demand of ~800 KB/s (one 16 KiB block every 20 ms) is served almost entirely
            var fast = await ServedBlocks(host, t, 40, 20);
            Assert.True(fast.Key >= 36, "a 2 MB/s se sirvieron " + fast.Key + " de 40");
            // The user lowers the upload to 128 KB/s in Settings: without restarting anything only the 64 KiB burst (4 blocks) and 8 blocks per second fit
            host.ApplyUserSettings(new UserSettings { UploadKBps = 128 });
            Assert.Equal(128 * 1024, host.Options.UploadLimiter.Rate);
            var slow = await ServedBlocks(host, t, 40, 20);
            double allowed = 4 + 8 * slow.Value + 3;
            Assert.InRange(slow.Key, 1, (int)allowed);
            Assert.True(slow.Key < fast.Key - 10, "with the new limit it served " + slow.Key + " versus " + fast.Key);
            Assert.True(host.Options.UploadLimiter.Denied > 0);
            // and on raising it again, everything is served again
            host.ApplyUserSettings(new UserSettings { UploadKBps = 2048 });
            var again = await ServedBlocks(host, t, 40, 20);
            Assert.True(again.Key >= 36, "at 2 MB/s again it served " + again.Key + " de 40");
        }
    }
}
