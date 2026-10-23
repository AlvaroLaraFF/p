using AceEngine.Streaming;
using Xunit;

namespace AceEngine.Tests.Streaming
{
    public class RangeHeaderTests
    {
        private static RangeResult P(string h, long len, out long a, out long b) { return RangeHeader.Parse(h, len, out a, out b); }

        [Fact]
        public void NoHeaderOrOtherUnitMeansNone()
        {
            long a, b;
            Assert.Equal(RangeResult.None, P(null, 100, out a, out b));
            Assert.Equal(RangeResult.None, P("", 100, out a, out b));
            Assert.Equal(RangeResult.None, P("items=0-5", 100, out a, out b));
        }

        [Theory]
        [InlineData("bytes=0-9", 100, 0, 9)]
        [InlineData("bytes=10-", 100, 10, 99)]
        [InlineData("bytes=-10", 100, 90, 99)]
        [InlineData("bytes=-1000", 100, 0, 99)]        // suffix larger than the file: everything
        [InlineData("bytes=50-1000", 100, 50, 99)]     // end clipped to the size
        [InlineData("bytes=99-99", 100, 99, 99)]
        [InlineData("bytes=0-9, 20-29", 100, 0, 9)]    // several ranges: the first
        [InlineData("BYTES=5-6", 100, 5, 6)]
        [InlineData("bytes= 5 - 6 ", 100, 5, 6)]
        public void Satisfiable(string header, long len, long first, long last)
        {
            long a, b;
            Assert.Equal(RangeResult.Satisfiable, P(header, len, out a, out b));
            Assert.Equal(first, a);
            Assert.Equal(last, b);
        }

        [Theory]
        [InlineData("bytes=100-", 100)]
        [InlineData("bytes=100-200", 100)]
        [InlineData("bytes=50-10", 100)]
        [InlineData("bytes=-0", 100)]
        [InlineData("bytes=abc", 100)]
        [InlineData("bytes=a-b", 100)]
        [InlineData("bytes=-", 100)]
        [InlineData("bytes=0-0", 0)]
        [InlineData("bytes=-5", 0)]
        [InlineData("bytes=-1-5", 100)]
        public void NotSatisfiable(string header, long len)
        {
            long a, b;
            Assert.Equal(RangeResult.NotSatisfiable, P(header, len, out a, out b));
        }
    }

    public class TsAlignerTests
    {
        private static byte[] Packets(int n)
        {
            var b = new byte[188 * n];
            for (int i = 0; i < n; i++) { b[i * 188] = 0x47; for (int j = 1; j < 188; j++) b[i * 188 + j] = (byte)(j + i); }
            return b;
        }

        [Fact]
        public void FindsSyncAtZero()
        {
            var p = Packets(4);
            Assert.Equal(0, TsAligner.FindSync(p, p.Length));
        }

        [Fact]
        public void SkipsGarbageAndFalsePositives()
        {
            var junk = new byte[300];
            for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i % 7 == 0 ? 0x47 : 0x11);   // stray 0x47s
            junk[10] = 0x47; junk[10 + 188] = 0x47;           // two out of three: false positive
            junk[300 - 188] = 0x11;                           // avoids a casual sync with the real packets
            var data = new byte[junk.Length + 188 * 4];
            System.Buffer.BlockCopy(junk, 0, data, 0, junk.Length);
            System.Buffer.BlockCopy(Packets(4), 0, data, junk.Length, 188 * 4);
            Assert.Equal(300, TsAligner.FindSync(data, data.Length));
        }

        [Fact]
        public void NeedsEnoughDataToDecide()
        {
            var p = Packets(2);
            Assert.Equal(-1, TsAligner.FindSync(p, p.Length));       // only 376 bytes: the third cannot be checked
            Assert.Equal(-1, TsAligner.FindSync(new byte[1000], 1000));
        }
    }
}
