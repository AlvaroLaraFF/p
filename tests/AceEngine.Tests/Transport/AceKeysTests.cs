using System.Text;
using AceEngine.Transport;
using Xunit;

namespace AceEngine.Tests.Transport
{
    /// <summary>The keys are given in ASCII and in hex: both forms must match.</summary>
    public class AceKeysTests
    {
        [Fact]
        public void KeyTdefV3MatchesSpecString()
        {
            Assert.Equal("%E0(tK8r]8KKU=crz!Vuex0b#I)H+!0n}%f0]L_x0ch++?-<#YHwXkvM6UL", Encoding.ASCII.GetString(AceKeys.KeyTdefV3));
            Assert.Equal(59, AceKeys.KeyTdefV3.Length);
        }

        [Fact]
        public void KeyTdefV2AndTsLive()
        {
            Assert.Equal("=Atl6GD#Vb+#QwW9zJy34lBOcM-7R7G)", Encoding.ASCII.GetString(AceKeys.KeyTdefV2));
            Assert.Equal(32, AceKeys.KeyTdefV2.Length);
            Assert.Equal("tslive_key", Encoding.ASCII.GetString(AceKeys.KeyTsLive));
        }

        [Fact]
        public void KeyTransportV1MatchesSpecStringAndHas29Bytes()
        {
            // the original description says 30 bytes; the hex form and the string itself give 29.
            var expected = "{M1DX|~\rrG]OaSKkKPS%A_Anb9i_w";
            Assert.Equal(expected, Encoding.ASCII.GetString(AceKeys.KeyTransportV1));
            Assert.Equal(29, AceKeys.KeyTransportV1.Length);
            Assert.Equal(0x0D, AceKeys.KeyTransportV1[7]);
        }

        [Fact]
        public void KeyTransportV2MatchesSpecString()
        {
            Assert.Equal("?#hOBwCmQ_G^m)dOiBDS[OH`x>;g?/G^cej)~PmZ$bE", Encoding.ASCII.GetString(AceKeys.KeyTransportV2));
            Assert.Equal(43, AceKeys.KeyTransportV2.Length);
        }

        [Fact]
        public void HeadersMatchSpec()
        {
            Assert.Equal(new byte[] { 1, 2, 3, 4, 0x11, 2, 0x65, 0x2e }, AceKeys.HeaderLegacyV3);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, AceKeys.HeaderTsLive);
            Assert.Equal(new byte[] { 0x11, 2, 0x65, 0x2e }, AceKeys.HeaderLegacyV2);
            Assert.Equal("AceStreamTransport", Encoding.ASCII.GetString(AceKeys.MagicAceStreamTransport));
            Assert.Equal(18, AceKeys.MagicAceStreamTransport.Length);
        }
    }
}
