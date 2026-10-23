using System;
using AceEngine.Links;
using Xunit;

namespace AceEngine.Tests.Links
{
    public class LinkTests
    {
        private const string Hex40 = "0123456789abcdef0123456789abcdef01234567";

        [Fact]
        public void PlainHexDefaultsToContentIdLikeTheJavaClient()
        {
            var l = AceLink.Parse(Hex40.ToUpperInvariant());
            Assert.Equal(AceLinkKind.ContentId, l.Kind);
            Assert.Equal(Hex40, l.Value);
        }

        [Fact]
        public void PlainHexCanBeTreatedAsInfohash()
        {
            var l = AceLink.Parse(Hex40, AceLinkKind.InfoHash);
            Assert.Equal(AceLinkKind.InfoHash, l.Kind);
        }

        [Theory]
        [InlineData("acestream://0123456789abcdef0123456789abcdef01234567", AceLinkKind.ContentId)]
        [InlineData("acestream:?content_id=0123456789abcdef0123456789abcdef01234567", AceLinkKind.ContentId)]
        [InlineData("acestream:%3Fcontent_id=0123456789abcdef0123456789abcdef01234567", AceLinkKind.ContentId)]
        [InlineData("acestream:?infohash=0123456789ABCDEF0123456789abcdef01234567", AceLinkKind.InfoHash)]
        [InlineData("acestream:content_id=0123456789abcdef0123456789abcdef01234567", AceLinkKind.ContentId)]
        public void ParsesAceStreamUris(string input, AceLinkKind kind)
        {
            var l = AceLink.Parse(input);
            Assert.Equal(kind, l.Kind);
            Assert.Equal(Hex40, l.Value);
        }

        [Fact]
        public void ParsesMagnetsAndEmbeddedMagnets()
        {
            var m = "magnet:?xt=urn:btih:" + Hex40 + "&dn=Mi+Peli&tr=udp%3A%2F%2Ft.test%3A80&tr=http%3A%2F%2Fh.test%2Fa";
            var l = AceLink.Parse(m);
            Assert.Equal(AceLinkKind.Magnet, l.Kind);
            Assert.Equal(Hex40, l.Magnet.InfoHashHex);
            Assert.Equal("Mi Peli", l.Magnet.DisplayName);
            Assert.Equal(new[] { "udp://t.test:80", "http://h.test/a" }, new System.Collections.Generic.List<string>(l.Magnet.Trackers).ToArray());

            var emb = AceLink.Parse("acestream:?magnet=" + Uri.EscapeDataString(m));
            Assert.Equal(AceLinkKind.Magnet, emb.Kind);
            Assert.Equal(Hex40, emb.Magnet.InfoHashHex);
        }

        [Fact]
        public void ParsesUrlsAndLocalFiles()
        {
            Assert.Equal(AceLinkKind.TransportUrl, AceLink.Parse("https://x.test/a.torrent").Kind);
            Assert.Equal(AceLinkKind.TransportUrl, AceLink.Parse("acestream:?url=" + Uri.EscapeDataString("http://x.test/a.torrent")).Kind);
            var f = AceLink.Parse("/tmp/a.torrent");
            Assert.Equal(AceLinkKind.TransportFile, f.Kind);
            Assert.Equal("/tmp/a.torrent", f.Value);
            Assert.Equal("/tmp/a b.torrent", AceLink.Parse("file:///tmp/a%20b.torrent").Value);
            Assert.Equal("/tmp/a.acelive", AceLink.Parse("acestream:?data=" + Uri.EscapeDataString("/tmp/a.acelive")).Value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("acestream://xyz")]
        [InlineData("acestream:?foo=bar")]
        [InlineData("acestream:?content_id=nothex")]
        [InlineData("magnet:?dn=sinhash")]
        [InlineData("magnet:sinquery")]
        public void RejectsInvalid(string input)
        {
            AceLink l; string err;
            Assert.False(AceLink.TryParse(input, AceLinkKind.ContentId, out l, out err));
            Assert.NotNull(err);
        }

        [Fact]
        public void MagnetBase32AndLastBtihWins()
        {
            // 20 bytes 0x00..0x13 in base32
            var bytes = new byte[20];
            for (int i = 0; i < 20; i++) bytes[i] = (byte)i;
            var b32 = Base32(bytes);
            var m = MagnetLink.Parse("magnet:?xt=urn:btih:" + b32);
            Assert.Equal(bytes, m.InfoHash);
            var two = MagnetLink.Parse("magnet:?xt=urn:btih:" + Hex40 + "&xt=urn:btih:" + b32);
            Assert.Equal(bytes, two.InfoHash);
            var btmh = MagnetLink.Parse("magnet:?xt=urn:btmh:1220aaaa&xt=urn:btih:" + Hex40);
            Assert.Equal(Hex40, btmh.InfoHashHex);
        }

        [Fact]
        public void BuildAndToAceStreamUri()
        {
            var ih = AceEngine.Util.Hex.Parse(Hex40);
            var m = MagnetLink.Build(ih, "a b", new[] { "udp://t:1" });
            Assert.Equal("magnet:?xt=urn:btih:" + Hex40 + "&dn=a%20b&tr=udp%3A%2F%2Ft%3A1", m);
            Assert.Equal("acestream:?content_id=" + Hex40, AceLink.ContentId(Hex40).ToAceStreamUri());
        }

        private static string Base32(byte[] data)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var sb = new System.Text.StringBuilder();
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { bits -= 5; sb.Append(alphabet[(buffer >> bits) & 31]); }
            }
            if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }
    }
}
