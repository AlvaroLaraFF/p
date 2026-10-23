using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.App
{
    public class AppConfigTests
    {
        [Fact]
        public void DefaultsAreSafeForTheUser()
        {
            var c = new AppConfig();
            Assert.InRange(c.UploadMaxBytesPerSec, 1, 1024 * 1024);
            Assert.InRange(c.UploadSlots, 1, 16);
            Assert.Equal("", c.AutoPlay);
            Assert.Equal("", c.KeyScript);
            Assert.False(c.LogEnabled);
            Assert.Equal("", c.LogHost);
            Assert.Empty(c.Lists);
        }

        [Fact]
        public void ParsesValuesAndClampsOutOfRangeOnes()
        {
            var c = AppConfig.Parse("{\"log\":{\"enabled\":false,\"host\":\"10.0.0.2\",\"port\":1234},\"autoPlay\":\"acestream://abc\",\"uploadSlots\":99,\"vodBufferMiB\":100000,\"liveBufferMiB\":1,\"statsIntervalSeconds\":-5,\"x\":{\"y\":[1,2]}}");
            Assert.False(c.LogEnabled);
            Assert.Equal("10.0.0.2", c.LogHost);
            Assert.Equal(1234, c.LogPort);
            Assert.Equal("acestream://abc", c.AutoPlay);
            Assert.Equal(16, c.UploadSlots);
            Assert.Equal(256, c.VodBufferMiB);
            Assert.Equal(8, c.LiveBufferMiB);
            Assert.Equal(0, c.StatsIntervalSeconds);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"log\": 5, \"uploadSlots\": \"abc\"}")]
        public void IgnoresGarbage(string json)
        {
            var c = AppConfig.Parse(json);
            Assert.Equal(new AppConfig().UploadSlots, c.UploadSlots);
        }

        [Fact]
        public void RoundTripsThroughJson()
        {
            var a = new AppConfig { AutoPlay = "x", UploadSlots = 3, KeyScript = "Right Return" };
            a.Lists.Add(new ListSource("a1b2c3d4e5f60718293a4b5c", "una"));
            a.SearchProviders.Add(new SearchProviderConfig { Name = "p", Url = "https://example.org/search?q={query}" });
            var b = AppConfig.Parse(a.ToJson());
            Assert.Equal("x", b.AutoPlay);
            Assert.Equal(3, b.UploadSlots);
            Assert.Equal("Right Return", b.KeyScript);
            Assert.Equal("a1b2c3d4e5f60718293a4b5c", b.Lists.Single().Code);
            Assert.Equal("https://example.org/search?q={query}", b.SearchProviders.Single().Url);
        }

        [Fact]
        public void ListsAcceptTheLegacyFavoritesKeyAndStayBounded()
        {
            var c = AppConfig.Parse("{\"favorites\":[{\"code\":\"a1b2c3d4e5f60718293a4b5c\",\"name\":\"Elcano\"},\"b2c3d4e5f60718293a4b5c6d\",{\"name\":\"sin codigo\"}]}");
            Assert.Equal(2, c.Lists.Count);
            Assert.Equal("b2c3d4e5f60718293a4b5c6d", c.Lists[1].Code);
            var many = "{\"lists\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => "\"code" + i + "\"")) + "]}";
            Assert.Equal(AppConfig.MaxLists, AppConfig.Parse(many).Lists.Count);
        }

        [Fact]
        public void UploadIsAlwaysOnInTheEngineOptions()
        {
            var o = EngineOptionsFactory.Build(new AppConfig { UploadSlots = 5, UploadMaxBytesPerSec = 1000, LiveBufferMiB = 20, VodBufferMiB = 40, LiveTransfer = "close" }, null);
            Assert.Equal(5, o.UploadSlots);
            Assert.Equal(5, o.Live.UploadSlots);
            Assert.Equal(1000, o.UploadMaxBytesPerSec);
            Assert.Equal(20L << 20, o.Live.MaxBufferBytes);
            Assert.Equal(40L << 20, o.MaxBufferBytes);
            Assert.Equal(AceEngine.Streaming.LiveTransfer.CloseDelimited, o.Http.LiveTransfer);
        }
    }

    public class InputClassifierTests
    {
        [Theory]
        [InlineData("a1b2c3d4e5f60718293a4b5c", InputKind.Code, "a1b2c3d4e5f60718293a4b5c")]
        [InlineData("  LN6d7dDtYZX91TYkaoCvtCFanwPMTvIdkn1cV2zKnaGXp5S1S9  ", InputKind.Code, "LN6d7dDtYZX91TYkaoCvtCFanwPMTvIdkn1cV2zKnaGXp5S1S9")]
        [InlineData("mylinkspaste://abc123/", InputKind.Code, "abc123")]
        [InlineData("acestream://abc123", InputKind.Code, "abc123")]
        [InlineData("https://example.org/list.m3u8", InputKind.M3uUrl, "https://example.org/list.m3u8")]
        [InlineData("", InputKind.Invalid, "")]
        [InlineData("has space", InputKind.Invalid, "has space")]
        [InlineData("under_score", InputKind.Invalid, "under_score")]
        [InlineData("-abc", InputKind.Invalid, "-abc")]
        [InlineData("http://", InputKind.Invalid, "http://")]
        [InlineData("ftp://x/y", InputKind.Invalid, "ftp://x/y")]
        public void Classifies(string text, InputKind kind, string normalized)
        {
            string n;
            Assert.Equal(kind, InputClassifier.Classify(text, out n));
            Assert.Equal(normalized, n);
        }

        [Fact]
        public void RejectsLabelsLongerThan63()
        {
            string n;
            Assert.Equal(InputKind.Invalid, InputClassifier.Classify(new string('a', 64), out n));
            Assert.Equal(InputKind.Code, InputClassifier.Classify(new string('a', 63), out n));
        }
    }
}
