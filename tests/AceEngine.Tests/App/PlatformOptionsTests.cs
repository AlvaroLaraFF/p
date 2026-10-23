using System.Linq;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>The "platform" section of the configuration (options only a shell reads) and the Android defaults.</summary>
    public class PlatformOptionsTests
    {
        [Fact]
        public void TheAndroidDefaultsTurnTheTvOnlyThingsOffAndTheUdpLogToo()
        {
            var c = new AppConfig();
            AppConfigDefaults.Android(c);
            Assert.False(c.LogEnabled);
            Assert.Equal("none", c.GlSync);
            Assert.False(c.Warmer);
            Assert.False(c.GpuWarmup);
            Assert.Equal(8, c.SpriteBudgetMs);
        }

        [Fact]
        public void TheAndroidDefaultsComeBeforeTheLayersSoAConfigurationStillOverridesThem()
        {
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("test", "{\"log\":{\"enabled\":true,\"host\":\"10.0.0.2\"},\"spriteBudgetMs\":3}") }, null, AppConfigDefaults.Android);
            Assert.True(cfg.LogEnabled); Assert.Equal("10.0.0.2", cfg.LogHost); Assert.Equal(3, cfg.SpriteBudgetMs);
            Assert.Equal("none", cfg.GlSync);
        }

        [Fact]
        public void ThePlatformSectionKeepsScalarValuesAndLayersMergeKeyByKey()
        {
            var cfg = AppConfigLoader.Load(new[]
            {
                ConfigLayer.FromText("one", "{\"platform\":{\"player\":\"exo\",\"maxRenderSide\":1280,\"flag\":true}}"),
                ConfigLayer.FromText("two", "{\"platform\":{\"player\":\"mediaplayer\"}}"),
            });
            Assert.Equal("mediaplayer", cfg.Platform["player"]);
            Assert.Equal(1280L, cfg.Platform["maxRenderSide"]);
            Assert.Equal(true, cfg.Platform["flag"]);
        }

        [Fact]
        public void ThePlatformSectionIgnoresNestedValuesAndStaysBounded()
        {
            var many = string.Join(",", Enumerable.Range(0, 100).Select(i => "\"k" + i + "\":" + i));
            var cfg = AppConfig.Parse("{\"platform\":{\"obj\":{\"a\":1},\"arr\":[1],\"nul\":null," + many + "}}");
            Assert.False(cfg.Platform.ContainsKey("obj")); Assert.False(cfg.Platform.ContainsKey("arr")); Assert.False(cfg.Platform.ContainsKey("nul"));
            Assert.Equal(AppConfig.MaxPlatformOptions, cfg.Platform.Count);
        }

        [Fact]
        public void ThePlatformSectionIsInTheJsonOnlyWhenItHasSomethingAndRoundTrips()
        {
            Assert.DoesNotContain("platform", new AppConfig().ToJson());
            var a = new AppConfig(); a.Platform["player"] = "mediaplayer"; a.Platform["maxRenderSide"] = 1280L;
            var b = AppConfig.Parse(a.ToJson());
            Assert.Equal("mediaplayer", b.Platform["player"]);
            Assert.Equal(1280L, b.Platform["maxRenderSide"]);
        }

        [Fact]
        public void ALongPlatformStringIsClipped()
        {
            var cfg = AppConfig.Parse("{\"platform\":{\"x\":\"" + new string('a', 1000) + "\"}}");
            Assert.Equal(256, ((string)cfg.Platform["x"]).Length);
        }
    }
}
