using System;
using System.Linq;
using AceEngine.Bencode;
using Xunit;

namespace AceEngine.Tests.Bencode
{
    public class BencodeParserTests
    {
        private static BValue P(string s) { return BencodeParser.Parse(TestUtil.Ascii(s)); }

        [Theory]
        [InlineData("i0e", 0L)]
        [InlineData("i42e", 42L)]
        [InlineData("i-7e", -7L)]
        [InlineData("i9223372036854775807e", long.MaxValue)]
        [InlineData("i-9223372036854775808e", long.MinValue)]
        public void ParsesIntegers(string text, long expected)
        {
            var v = (BInt)P(text);
            Assert.Equal(expected, v.Value);
        }

        [Theory]
        [InlineData("i-0e")]
        [InlineData("i03e")]
        [InlineData("i00e")]
        [InlineData("ie")]
        [InlineData("i-e")]
        [InlineData("i12")]
        [InlineData("i1x2e")]
        [InlineData("i9223372036854775808e")]
        [InlineData("i99999999999999999999e")]
        public void RejectsInvalidIntegers(string text)
        {
            Assert.Throws<BencodeException>(() => P(text));
        }

        [Fact]
        public void ParsesStringsIncludingBinaryAndEmpty()
        {
            Assert.Equal("spam", ((BString)P("4:spam")).AsText());
            Assert.Equal(0, ((BString)P("0:")).Count);
            var bin = TestUtil.Concat(TestUtil.Ascii("4:"), new byte[] { 0x00, 0xff, 0x80, 0x47 });
            var s = (BString)BencodeParser.Parse(bin);
            Assert.Equal(new byte[] { 0x00, 0xff, 0x80, 0x47 }, s.Value);
            Assert.False(s.IsUtf8());
        }

        [Theory]
        [InlineData("04:spam")]
        [InlineData("5:spam")]
        [InlineData("4spam")]
        [InlineData("-1:a")]
        [InlineData("")]
        public void RejectsInvalidStrings(string text)
        {
            Assert.Throws<BencodeException>(() => P(text));
        }

        [Fact]
        public void ParsesListsAndDicts()
        {
            var l = (BList)P("l4:spami3eli1eee");
            Assert.Equal(3, l.Count);
            Assert.Equal("spam", ((BString)l[0]).AsText());
            Assert.Equal(3, ((BInt)l[1]).Value);
            Assert.Equal(1, ((BInt)((BList)l[2])[0]).Value);

            var d = (BDict)P("d3:cow3:moo4:spam4:eggse");
            Assert.Equal("moo", d.GetString("cow"));
            Assert.Equal("eggs", d.GetString("spam"));
            Assert.Null(d.Get("nope"));
            Assert.Equal(new[] { "cow", "spam" }, d.Keys.ToArray());
        }

        [Theory]
        [InlineData("l")]
        [InlineData("li1e")]
        [InlineData("d3:key")]
        [InlineData("di1e3:vale")]
        [InlineData("x")]
        public void RejectsMalformedContainers(string text)
        {
            Assert.Throws<BencodeException>(() => P(text));
        }

        [Fact]
        public void RejectsExcessiveNesting()
        {
            var deep = new string('l', 500) + new string('e', 500);
            Assert.Throws<BencodeException>(() => P(deep));
        }

        [Fact]
        public void TrailingDataIsAllowedByDefaultAndCanBeForbidden()
        {
            var data = TestUtil.Ascii("i5eXYZ");
            int consumed;
            var v = BencodeParser.Parse(data, 0, data.Length, BencodeParseOptions.Default, out consumed);
            Assert.Equal(3, consumed);
            Assert.Equal(5, ((BInt)v).Value);
            Assert.Throws<BencodeException>(() => BencodeParser.Parse(data, new BencodeParseOptions { AllowTrailingData = false }));
        }

        [Fact]
        public void SortedKeysCanBeRequired()
        {
            var unsorted = TestUtil.Ascii("d1:bi1e1:ai2ee");
            Assert.NotNull(BencodeParser.Parse(unsorted));
            Assert.Throws<BencodeException>(() => BencodeParser.Parse(unsorted, new BencodeParseOptions { RequireSortedKeys = true }));
            var dup = TestUtil.Ascii("d1:ai1e1:ai2ee");
            Assert.Throws<BencodeException>(() => BencodeParser.Parse(dup, new BencodeParseOptions { RequireSortedKeys = true }));
        }

        [Fact]
        public void DuplicateKeysKeepLastForLookupButAllEntries()
        {
            var d = (BDict)P("d1:ai1e1:ai2ee");
            Assert.Equal(2, d.Count);
            Assert.Equal(2, d.GetInt("a"));
        }

        [Fact]
        public void TryParseReturnsNullOnError()
        {
            Assert.Null(BencodeParser.TryParse(TestUtil.Ascii("zzz")));
            Assert.NotNull(BencodeParser.TryParse(TestUtil.Ascii("i1e")));
        }

        [Fact]
        public void ParseHonoursOffsetAndCount()
        {
            var data = TestUtil.Ascii("XXd1:ai1eeYY");
            int consumed;
            var v = BencodeParser.Parse(data, 2, 8, BencodeParseOptions.Default, out consumed);
            Assert.Equal(8, consumed);
            Assert.IsType<BDict>(v);
            Assert.Equal(2, v.RawOffset);
        }
    }
}
