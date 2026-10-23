using System;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Transport;
using Xunit;

namespace AceEngine.Tests.Transport
{
    public class InfoHashAndContentIdTests
    {
        private static byte[] Torrent(string beforeInfo, string info, string afterInfo)
        {
            return Encoding.ASCII.GetBytes("d" + beforeInfo + "4:info" + info + afterInfo + "e");
        }

        private const string StdInfo = "d6:lengthi5e4:name3:abc12:piece lengthi16384e6:pieces20:01234567890123456789e";

        [Fact]
        public void InfohashIsSha1OfTheRawInfoSlice()
        {
            var tf = TransportLoader.Load(Torrent("8:announce3:url", StdInfo, ""));
            Assert.Equal(TestUtil.Sha1Hex(TestUtil.Ascii(StdInfo)), tf.InfoHashHex);
        }

        [Fact]
        public void ContentIdIsSha1OfTheBytesAsReceivedNeverReserialized()
        {
            // Unsorted top-level keys (info before announce): a standard re-serialization
            // would change the bytes and therefore the content_id.
            var data = Encoding.ASCII.GetBytes("d4:info" + StdInfo + "8:announce3:urle");
            var tf = TransportLoader.Load(data);
            Assert.Equal(TestUtil.Sha1Hex(data), tf.ContentId);
            var reserialized = BencodeWriter.Encode(tf.Root);
            Assert.NotEqual(TestUtil.Sha1Hex(reserialized), tf.ContentId);
        }

        [Fact]
        public void InfohashUsesOriginalBytesEvenWhenKeyOrderIsNotAces()
        {
            // 'ab' and 'ab!' have a different order in Ace's bencode. The file comes in the standard order.
            var info = "d2:abi1e3:ab!i2e6:lengthi5e4:name1:n12:piece lengthi16384e6:pieces20:01234567890123456789e";
            var tf = TransportLoader.Load(Torrent("", info, ""));
            Assert.Equal(TestUtil.Sha1Hex(TestUtil.Ascii(info)), tf.InfoHashHex);
            // Re-serializing in Ace's order would be different -> it is diagnostic only (a warning is logged).
            Assert.NotEqual(tf.Torrent.InfoHashHex, TestUtil.ToHex(tf.Torrent.ComputeAceReserializedInfoHash()));
        }

        [Fact]
        public void AceReserializationEqualsRawForStandardTorrents()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("plain.torrent"));
            Assert.Equal(tf.Torrent.InfoHash, tf.Torrent.ComputeAceReserializedInfoHash());
        }

        [Fact]
        public void KnownPublicTorrentInfohash()
        {
            // Real torrent from the Blender Foundation (Sintel, CC-BY) downloaded from webtorrent.io. The expected infohash
            // is the one from the published magnet link (a source independent of this code).
            var path = TestUtil.FixturePath("sintel.torrent");
            if (!System.IO.File.Exists(path)) return;
            var tf = TransportLoader.Load(System.IO.File.ReadAllBytes(path));
            Assert.Equal("08ada5a7a6183aae1e09d831df6748d566095a10", tf.InfoHashHex);
            Assert.Equal("Sintel", tf.Name);
            Assert.True(tf.Torrent.IsMultiFile);
        }

        [Fact]
        public void TorrentValidationRejectsInconsistentMetadata()
        {
            Assert.Throws<TransportException>(() => TransportLoader.Load(Torrent("", "d6:lengthi5e4:name3:abc12:piece lengthi16384e6:pieces19:0123456789012345678e", "")));
            Assert.Throws<TransportException>(() => TransportLoader.Load(Torrent("", "d6:lengthi50000e4:name3:abc12:piece lengthi16384e6:pieces20:01234567890123456789e", "")));
            Assert.Throws<TransportException>(() => TransportLoader.Load(Torrent("", "d4:name3:abc12:piece lengthi16384e6:pieces20:01234567890123456789e", "")));
            Assert.Throws<TransportException>(() => TransportLoader.Load(Torrent("", "d6:lengthi5e4:name3:abc12:piece lengthi0e6:pieces20:01234567890123456789e", "")));
        }

        [Fact]
        public void OldStyleLiveTorrentIsRecognised()
        {
            // info['live'] marks a live stream in the old torrent format. Without pieces.
            var info = "d4:livedd10:authmethod4:None6:pubkey0:ee4:name4:chane";
            var data = Encoding.ASCII.GetBytes("d4:info" + info + "e");
            // 'live' is a dict with authmethod and pubkey; the syntax above carries an extra 'd' on purpose, so it is built properly:
            var live = new BDict();
            live.Add("authmethod", BValue.Str("None"));
            live.Add("pubkey", BValue.Bytes(new byte[] { 1, 2, 3 }));
            var infod = new BDict();
            infod.Add("name", BValue.Str("chan"));
            infod.Add("live", live);
            var root = new BDict();
            root.Add("info", infod);
            var bytes = BencodeWriter.Encode(root);
            var tf = TransportLoader.Load(bytes);
            Assert.True(tf.IsLive);
            Assert.True(tf.Torrent.IsLive);
            Assert.Equal("None", tf.Torrent.LiveInfo.GetString("authmethod"));
            Assert.Equal(TestUtil.Sha1Hex(infod.Encode()), tf.InfoHashHex);
            var mf = MediaFiles.Get(tf);
            Assert.Equal("live", mf.Type);
            Assert.Single(mf.Files);
            Assert.Equal("video/mp2t", mf.Files[0].Mime);
        }

        [Fact]
        public void FromInfoBytesKeepsRawInfohash()
        {
            var info = TestUtil.Ascii(StdInfo);
            var meta = TorrentMeta.FromInfoBytes(info);
            Assert.Equal(TestUtil.Sha1Hex(info), meta.InfoHashHex);
            Assert.Equal("abc", meta.Name);
            Assert.Null(meta.Announce);
            Assert.Empty(meta.GetTrackerTiers());
        }

        [Fact]
        public void Utf8NameAndPathArePreferred()
        {
            var info = "d5:filesld6:lengthi5e4:pathl3:cam5:a.mp4e10:path.utf-8l3:cam5:b.mp4eee4:name1:n10:name.utf-84:buen12:piece lengthi16384e6:pieces20:01234567890123456789e";
            var tf = TransportLoader.Load(Torrent("", info, ""));
            Assert.Equal("buen", tf.Name);
            Assert.Equal("cam/b.mp4", tf.Torrent.Files[0].Path);
        }
    }
}
