using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AceEngine.App;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.App
{
    public class AppConfigLoaderTests
    {
        private sealed class Lines
        {
            public readonly List<string> All = new List<string>();
            public void Add(LogLevel level, string tag, string msg) { All.Add(level + " " + tag + ": " + msg); }
        }

        [Fact]
        public void LayersApplyInOrderAndEachOneOverridesTheFieldsOfThePreviousOne()
        {
            var log = new Lines();
            var cfg = AppConfigLoader.Load(new[]
            {
                ConfigLayer.FromText("packaged", "{\"uploadSlots\":2,\"vodBufferMiB\":30,\"lists\":[{\"code\":\"aaa\"}]}"),
                ConfigLayer.FromText("data", "{\"uploadSlots\":3,\"lists\":[{\"code\":\"bbb\",\"name\":\"B\"}]}"),
                ConfigLayer.FromText("settings", "{\"uploadSlots\":5}"),
            }, log.Add);
            Assert.Equal(5, cfg.UploadSlots);                 // the last layer wins
            Assert.Equal(30, cfg.VodBufferMiB);               // a field only the first one sets stays
            Assert.Single(cfg.Lists); Assert.Equal("bbb", cfg.Lists[0].Code);   // lists are replaced as a whole
            Assert.Equal(new[] { "Info config: read packaged", "Info config: read data", "Info config: read settings" }, log.All.ToArray());
        }

        [Fact]
        public void AMissingLayerIsSkippedAndTheOthersStillApply()
        {
            var log = new Lines();
            var cfg = AppConfigLoader.Load(new[] { new ConfigLayer("absent", () => null), ConfigLayer.FromText("present", "{\"uploadSlots\":7}") }, log.Add);
            Assert.Equal(7, cfg.UploadSlots);
            Assert.Equal(new[] { "Info config: no absent", "Info config: read present" }, log.All.ToArray());
        }

        [Fact]
        public void ALayerThatFailsToReadIsReportedAndSkipped()
        {
            var log = new Lines();
            var cfg = AppConfigLoader.Load(new[] { new ConfigLayer("broken", () => { throw new IOException("disk gone"); }), ConfigLayer.FromText("ok", "{\"uploadSlots\":6}") }, log.Add);
            Assert.Equal(6, cfg.UploadSlots);
            Assert.Equal("Warn config: broken: disk gone", log.All[0]);
        }

        [Fact]
        public void ATooLargeLayerIsRejectedByBytesNotByCharacters()
        {
            var log = new Lines();
            // 40000 two-byte characters: 40000 chars but 80000 bytes, over the 64 KiB limit
            string big = "{\"autoPlay\":\"" + new string('\u00e9', 40000) + "\"}";
            Assert.True(big.Length < AppConfig.MaxConfigBytes && Encoding.UTF8.GetByteCount(big) > AppConfig.MaxConfigBytes);
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("big", big), ConfigLayer.FromText("small", "{\"uploadSlots\":9}") }, log.Add);
            Assert.Equal("", cfg.AutoPlay);
            Assert.Equal(9, cfg.UploadSlots);
            Assert.Equal("Warn config: big too large", log.All[0]);
        }

        [Fact]
        public void InvalidJsonIsIgnoredWithoutBreakingTheConfiguration()
        {
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("junk", "this is {not json"), ConfigLayer.FromText("ok", "{\"uploadSlots\":4}") });
            Assert.Equal(4, cfg.UploadSlots);
        }

        [Fact]
        public void PlatformDefaultsApplyBeforeAnyLayer()
        {
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("user", "{\"spriteBudgetMs\":3}") }, null, c => { c.SpriteBudgetMs = 20; c.GlSync = "none"; });
            Assert.Equal(3, cfg.SpriteBudgetMs);              // the layer overrides the platform default
            Assert.Equal("none", cfg.GlSync);                 // a field no layer sets keeps the platform default
        }

        [Fact]
        public void NoLayersGivesTheBuiltInDefaults()
        {
            Assert.Equal(new AppConfig().ToJson(), AppConfigLoader.Load(null).ToJson());
            Assert.Equal(new AppConfig().ToJson(), AppConfigLoader.Load(new ConfigLayer[0]).ToJson());
        }

        [Fact]
        public void FileLayersReadTheStandardFilesInOrderAndSkipTheMissingOnes()
        {
            string root = Path.Combine(Path.GetTempPath(), "p-cfg-" + Guid.NewGuid().ToString("N"));
            string res = Path.Combine(root, "res"), data = Path.Combine(root, "data");
            Directory.CreateDirectory(res); Directory.CreateDirectory(data);
            try
            {
                File.WriteAllText(Path.Combine(res, "config.json"), "{\"uploadSlots\":2,\"vodBufferMiB\":20}");
                File.WriteAllText(Path.Combine(data, "settings.json"), "{\"uploadSlots\":8}");      // data/config.json does not exist
                var log = new Lines();
                var layers = AppConfigLoader.StandardFileLayers(data, res);
                Assert.Equal(3, layers.Count);
                var cfg = AppConfigLoader.Load(layers, log.Add);
                Assert.Equal(8, cfg.UploadSlots);
                Assert.Equal(20, cfg.VodBufferMiB);
                Assert.Equal("Info config: read " + Path.Combine(res, "config.json"), log.All[0]);
                Assert.Equal("Info config: no " + Path.Combine(data, "config.json"), log.All[1]);
                Assert.Equal("Info config: read " + Path.Combine(data, "settings.json"), log.All[2]);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void AFileOverTheLimitIsNotEvenRead()
        {
            string f = Path.Combine(Path.GetTempPath(), "p-cfg-big-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(f, "{\"uploadSlots\":3,\"pad\":\"" + new string('x', AppConfig.MaxConfigBytes) + "\"}");
                var log = new Lines();
                var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromFile(f) }, log.Add);
                Assert.Equal(new AppConfig().UploadSlots, cfg.UploadSlots);
                Assert.Equal("Warn config: " + f + " too large", log.All[0]);
            }
            finally { try { File.Delete(f); } catch { } }
        }

        [Fact]
        public void NullDirectoriesDoNotThrow()
        {
            var layers = AppConfigLoader.StandardFileLayers(null, null);
            Assert.Equal(3, layers.Count);
            AppConfigLoader.Load(layers);
        }
    }
}
