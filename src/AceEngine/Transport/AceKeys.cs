using System;
using System.Text;
using AceEngine.Util;

namespace AceEngine.Transport
{
    /// <summary>
    /// AES keys embedded in the original engine. They are those of the original application:
    /// they have to be reused to interoperate with the existing transport files
    /// (compatibility dependency). CONFIRMED against real files.
    /// </summary>
    public static class AceKeys
    {
        /// <summary>Header \x01\x02\x03\x04\x11\x02e. + AES with KeyTdefV3 (protected torrents).</summary>
        public static readonly byte[] KeyTdefV3 = Hex.Parse(
            "25453028744b38725d384b4b553d63727a21567565783062234929482b21306e7d2566305d4c5f783063682b2b3f2d3c23594877586b764d36554c");

        /// <summary>Header \x01\x02\x03\x04 + AES with "tslive_key".</summary>
        public static readonly byte[] KeyTsLive = Encoding.ASCII.GetBytes("tslive_key");

        /// <summary>Header \x11\x02e. + AES with KeyTdefV2.</summary>
        public static readonly byte[] KeyTdefV2 = Encoding.ASCII.GetBytes("=Atl6GD#Vb+#QwW9zJy34lBOcM-7R7G)");

        /// <summary>
        /// AceStreamTransport version 1 (29 bytes; the original documentation says 30 by mistake).
        /// It contains a 0x0D at index 7.
        /// </summary>
        public static readonly byte[] KeyTransportV1 = Hex.Parse("7b4d3144587c7e0d72475d4f61534b6b4b505325415f416e6239695f77");

        /// <summary>AceStreamTransport version 2 (43 bytes).</summary>
        public static readonly byte[] KeyTransportV2 = Hex.Parse(
            "3f23684f4277436d515f475e6d29644f694244535b4f4860783e3b673f2f475e63656a297e506d5a246245");

        public static readonly byte[] HeaderLegacyV3 = { 0x01, 0x02, 0x03, 0x04, 0x11, 0x02, (byte)'e', (byte)'.' };
        public static readonly byte[] HeaderTsLive = { 0x01, 0x02, 0x03, 0x04 };
        public static readonly byte[] HeaderLegacyV2 = { 0x11, 0x02, (byte)'e', (byte)'.' };
        public static readonly byte[] MagicAceStreamTransport = Encoding.ASCII.GetBytes("AceStreamTransport");

    }
}
