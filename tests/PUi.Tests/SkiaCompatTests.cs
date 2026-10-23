using PUi.Gfx;
using SkiaSharp;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The tests run on the net8.0 build of PUi, which must be the one compiled against SkiaSharp 2.88 (the netstandard2.0 one, for the TV, is checked on the .tpk).</summary>
    public class SkiaCompatTests
    {
        [Fact]
        public void TheNet8BuildUsesSkiaSharp288()
        {
            Assert.Equal("2.88", SkiaCompat.ApiLevel);
            Assert.Equal(2, typeof(SKCanvas).Assembly.GetName().Version.Major);
        }
    }
}
