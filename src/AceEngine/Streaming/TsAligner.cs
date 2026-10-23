using System;

namespace AceEngine.Streaming
{
    /// <summary>
    /// MPEG-TS alignment of the output of a live stream: skips bytes up to a position p with
    /// buf[p]==0x47, buf[p+188]==0x47 and buf[p+376]==0x47 (three checks to avoid false positives).
    /// </summary>
    public static class TsAligner
    {
        public const int PacketSize = 188;
        public const int Needed = PacketSize * 2 + 1;   // bytes needed from p to check the three sync bytes

        /// <summary>First sync position in buf[0..count), or -1 if there is none (or there is not enough data to decide).</summary>
        public static int FindSync(byte[] buf, int count)
        {
            for (int p = 0; p + Needed <= count; p++)
            {
                if (buf[p] == 0x47 && buf[p + PacketSize] == 0x47 && buf[p + 2 * PacketSize] == 0x47) return p;
            }
            return -1;
        }
    }
}
