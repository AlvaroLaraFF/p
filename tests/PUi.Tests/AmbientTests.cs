using System.Diagnostics;
using PUi.Gfx;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The fixed background of the menus: gradient without banding (TPDF dithering) computed once.</summary>
    public class AmbientTests
    {
        [Fact]
        public void Build_SmoothGradientWithoutBands()
        {
            var sw = Stopwatch.StartNew();
            int margin;
            using (var bmp = Ambient.Build(1920, 1080, out margin))
            {
                sw.Stop();
                Assert.Equal(1920 + 2 * margin, bmp.Width);
                Assert.Equal(1080 + 2 * margin, bmp.Height);
                // Row through the sky-blue light: averaging in 16 px blocks (what the eye perceives) there are no steps larger than 2 tones
                // between neighboring blocks, and inside each block there is dithering (it is not a flat tone, which is what produces banding).
                int y = 950 + margin, blocks = 0, dithered = 0;
                double prev = -1;
                for (int x0 = 1000 + margin; x0 + 16 <= 1920 + margin; x0 += 16)
                {
                    double sum = 0; int min = 255, max = 0;
                    for (int x = x0; x < x0 + 16; x++) { int g = bmp.GetPixel(x, y).Green; sum += g; if (g < min) min = g; if (g > max) max = g; }
                    double mean = sum / 16;
                    if (prev >= 0) Assert.InRange(System.Math.Abs(mean - prev), 0, 2.0);
                    prev = mean; blocks++;
                    if (max > min) dithered++;
                }
                Assert.True(dithered > blocks * 3 / 4, "the gradient must be dithered in almost all blocks");
            }
            Assert.True(sw.ElapsedMilliseconds < 20000, "background computation too slow: " + sw.ElapsedMilliseconds + " ms");
        }
    }
}
