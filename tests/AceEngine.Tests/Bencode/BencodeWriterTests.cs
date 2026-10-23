using System;
using System.Text;
using AceEngine.Bencode;
using Xunit;

namespace AceEngine.Tests.Bencode
{
    public class BencodeWriterTests
    {
        [Fact]
        public void WritesBasicTypes()
        {
            Assert.Equal("i-5e", Encoding.ASCII.GetString(BencodeWriter.Encode(BValue.Int(-5))));
            Assert.Equal("4:spam", Encoding.ASCII.GetString(BencodeWriter.Encode(BValue.Str("spam"))));
            Assert.Equal("l1:ai2ee", Encoding.ASCII.GetString(BencodeWriter.Encode(BValue.List(BValue.Str("a"), BValue.Int(2)))));
        }

        [Fact]
        public void StandardOrderSortsByKeyBytes()
        {
            var d = new BDict();
            d.Add("spam", BValue.Str("eggs"));
            d.Add("cow", BValue.Str("moo"));
            Assert.Equal("d3:cow3:moo4:spam4:eggse", Encoding.ASCII.GetString(BencodeWriter.Encode(d)));
        }

        [Fact]
        public void RoundTripOfCanonicalInputIsIdentical()
        {
            var text = "d8:announce3:url4:infod6:lengthi1e4:name1:n12:piece lengthi2e6:pieces0:ee";
            var v = BencodeParser.Parse(TestUtil.Ascii(text));
            Assert.Equal(text, Encoding.ASCII.GetString(BencodeWriter.Encode(v)));
        }

        [Fact]
        public void AceOrderEqualsStandardOrderForOrdinaryKeys()
        {
            // for normal ASCII keys both orders coincide.
            var d = new BDict();
            foreach (var k in new[] { "pieces", "piece length", "name", "length", "files", "private", "live", "root hash", "name.utf-8" })
                d.Add(k, BValue.Int(1));
            var std = BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Standard });
            var ace = BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Ace });
            Assert.Equal(std, ace);
        }

        [Fact]
        public void AceOrderDivergesWhenAKeyIsAPrefixFollowedByLowChar()
        {
            // "ab!" versus "ab": repr "'ab!'" < "'ab'" because '!' (0x21) < "'" (0x27).
            var d = new BDict();
            d.Add("ab", BValue.Int(1));
            d.Add("ab!", BValue.Int(2));
            var std = Encoding.ASCII.GetString(BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Standard }));
            var ace = Encoding.ASCII.GetString(BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Ace }));
            Assert.Equal("d2:abi1e3:ab!i2ee", std);
            Assert.Equal("d3:ab!i2e2:abi1ee", ace);
        }

        [Fact]
        public void AceOrderPlacesPrefixBeforeLongerKeyForHighChars()
        {
            var d = new BDict();
            d.Add("ab~", BValue.Int(2));
            d.Add("ab", BValue.Int(1));
            Assert.Equal("d2:abi1e3:ab~i2ee", Encoding.ASCII.GetString(BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Ace })));
        }

        [Fact]
        public void NestedDictsAreSortedRecursively()
        {
            var inner = new BDict();
            inner.Add("b", BValue.Int(1));
            inner.Add("a", BValue.Int(2));
            var d = new BDict();
            d.Add("x", inner);
            Assert.Equal("d1:xd1:ai2e1:bi1eee", Encoding.ASCII.GetString(BencodeWriter.Encode(d)));
        }

        [Theory]
        [InlineData("abc", "'abc'")]
        [InlineData("it's", "\"it's\"")]
        [InlineData("a\"b", "'a\"b'")]
        [InlineData("a'b\"c", "'a\\'b\"c'")]
        [InlineData("back\\slash", "'back\\\\slash'")]
        [InlineData("tab\there", "'tab\\there'")]
        [InlineData("nl\n", "'nl\\n'")]
        [InlineData("\u0001", "'\\x01'")]
        [InlineData("café", "'café'")]
        public void PythonReprOfStrings(string input, string expected)
        {
            Assert.Equal(expected, PythonRepr.ReprStr(input));
        }

        [Fact]
        public void PythonReprOfBytes()
        {
            Assert.Equal("b'ab\\x00\\xff'", PythonRepr.ReprBytes(new byte[] { 0x61, 0x62, 0x00, 0xff }));
            Assert.Equal("b\"it's\"", PythonRepr.ReprBytes(Encoding.ASCII.GetBytes("it's")));
        }

        [Fact]
        public void NonUtf8KeysUseBytesRepr()
        {
            var key = new BString(new byte[] { 0xff, 0x41 });
            Assert.Equal("b'\\xffA'", PythonRepr.ReprKey(key));
        }
    }
}
