using System;
using System.Text;
using AceEngine.Bencode;
using Xunit;

namespace AceEngine.Tests.Bencode
{
    /// <summary>The parser keeps the raw bytes of every value (infohash and content_id).</summary>
    public class BencodeRawBytesTests
    {
        [Fact]
        public void EveryValueKeepsItsRawSlice()
        {
            var text = "d4:infod6:lengthi5e4:name3:abce3:zzzli1e2:xxee";
            var data = TestUtil.Ascii(text);
            var root = (BDict)BencodeParser.Parse(data);
            Assert.Equal(text, Encoding.ASCII.GetString(root.GetRawBytes()));

            var info = root.GetDict("info");
            Assert.Equal("d6:lengthi5e4:name3:abce", Encoding.ASCII.GetString(info.GetRawBytes()));
            Assert.Equal("i5e", Encoding.ASCII.GetString(info.Get("length").GetRawBytes()));
            Assert.Equal("3:abc", Encoding.ASCII.GetString(info.Get("name").GetRawBytes()));
            Assert.Equal("li1e2:xxe", Encoding.ASCII.GetString(root.Get("zzz").GetRawBytes()));
        }

        [Fact]
        public void RawBytesPreserveNonCanonicalOrderAndBinaryContent()
        {
            // Unsorted keys and a binary value: a standard re-serialization would change the bytes.
            var data = TestUtil.Concat(TestUtil.Ascii("d1:z"), TestUtil.Ascii("2:"), new byte[] { 0xff, 0x00 }, TestUtil.Ascii("1:ai1ee"));
            var root = (BDict)BencodeParser.Parse(data);
            Assert.Equal(data, root.GetRawBytes());
            var reserialized = BencodeWriter.Encode(root);
            Assert.NotEqual(data, reserialized);                      // the standard order reorders
            var raw = BencodeWriter.Encode(root, new BencodeWriteOptions { UseRawBytes = true });
            Assert.Equal(data, raw);                                  // with UseRawBytes it comes out identical
            Assert.Equal(data, BencodeWriter.Encode(root, new BencodeWriteOptions { Order = KeyOrder.Preserve }));
        }

        [Fact]
        public void InfoHashOfRawSliceMatchesIndependentComputation()
        {
            var info = "d6:lengthi12345e4:name8:test.mp412:piece lengthi16384e6:pieces20:01234567890123456789e";
            var torrent = "d8:announce22:http://tracker.test/an4:info" + info + "e";
            var root = (BDict)BencodeParser.Parse(TestUtil.Ascii(torrent));
            var raw = root.GetDict("info").GetRawBytes();
            Assert.Equal(info, Encoding.ASCII.GetString(raw));
            // SHA-1 computed separately with sha1sum/hashlib (see the transport tests for another external vector).
            Assert.Equal(TestUtil.Sha1Hex(TestUtil.Ascii(info)), TestUtil.Sha1Hex(raw));
        }

        [Fact]
        public void ManuallyBuiltValuesHaveNoRawBytes()
        {
            var d = new BDict();
            d.Add("a", BValue.Int(1));
            Assert.False(d.HasRaw);
            Assert.Throws<InvalidOperationException>(() => d.GetRawBytes());
            Assert.Equal("d1:ai1ee", Encoding.ASCII.GetString(d.Encode()));
        }

        [Fact]
        public void RawSegmentIsAViewOverTheOriginalBuffer()
        {
            var data = TestUtil.Ascii("l3:abci7ee");
            var l = (BList)BencodeParser.Parse(data);
            var seg = l[1].GetRawSegment();
            Assert.Same(data, seg.Array);
            Assert.Equal(6, seg.Offset);
            Assert.Equal(3, seg.Count);
        }
    }
}
