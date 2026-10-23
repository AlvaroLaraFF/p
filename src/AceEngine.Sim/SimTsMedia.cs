using System;

namespace AceEngine.Sim
{
    /// <summary>
    /// "With media" profile of the synthetic MPEG-TS stream (live with memory): besides the numbering of each packet (which goes in the packet's LAST 8 bytes,
    /// so it does not depend on whether the packet carries an adaptation field), the stream has a real timeline. It is constant
    /// (packet n goes out at n * 188 / Bitrate seconds), with a PCR on the video PID every PcrIntervalMs and a GOP every GopMs: the first packet
    /// of the GOP has PUSI, random_access_indicator and a PES header with an H.264 AUD + SPS + IDR. The PAT/PMT are SimTs's (every 200 packets).
    /// </summary>
    public sealed class SimTsProfile
    {
        /// <summary>Bytes per second of the stream (the descriptor's bitrate).</summary>
        public int Bitrate = 262144;
        public int PcrIntervalMs = 40;
        public int GopMs = 1000;
        /// <summary>Mark the random_access_indicator on the first packet of each GOP (otherwise it is only told apart by the IDR in the payload).</summary>
        public bool MarkRandomAccess = true;
        /// <summary>Emit the PCR (if false the stream has no clock reference).</summary>
        public bool EmitPcr = true;
        /// <summary>Origin of the PCR (in seconds): it does not start at 0 so that the subtraction is exercised.</summary>
        public double PcrOriginSeconds = 10;

        public int PcrEveryPackets { get { return Math.Max(2, (int)Math.Round(PcrIntervalMs / 1000.0 * Bitrate / SimTs.PacketSize)); } }
        public int GopPackets { get { return Math.Max(4, (int)Math.Round(GopMs / 1000.0 * Bitrate / SimTs.PacketSize)); } }

        /// <summary>Seconds of stream at which packet n goes out.</summary>
        public double TimeOf(long n) { return n * (double)SimTs.PacketSize / Bitrate; }

        /// <summary>Number of the packet that goes out at second t.</summary>
        public long PacketAt(double seconds) { return (long)Math.Floor(seconds * Bitrate / SimTs.PacketSize); }

        public static bool IsPsi(long n) { return n % SimTs.PsiPeriod == 0; }

        /// <summary>true if packet n is the first of a GOP (PUSI + IDR). If it would fall on a PSI packet it is delayed to the next one.</summary>
        public bool IsGopStart(long n)
        {
            if (n < 1 || IsPsi(n)) return false;
            int g = GopPackets;
            long r = n % g;
            return r == 1 || (r == 2 && IsPsi(n - 1));
        }

        public bool HasPcr(long n)
        {
            return EmitPcr && n >= 1 && !IsPsi(n) && n % PcrEveryPackets == 3;
        }

        /// <summary>PCR value (27 MHz) of packet n.</summary>
        public long Pcr27(long n)
        {
            return (long)Math.Round((PcrOriginSeconds + TimeOf(n)) * 27000000.0);
        }

        /// <summary>Packet number n of the stream with media. The PSI are SimTs's.</summary>
        public byte[] Packet(long n)
        {
            if (IsPsi(n)) return SimTs.Packet(n);
            var p = new byte[SimTs.PacketSize];
            for (int i = 4; i < SimTs.PacketSize; i++) p[i] = (byte)((n * 31 + i * 7) & 0xff);
            bool gop = IsGopStart(n), pcr = HasPcr(n);
            p[0] = 0x47;
            int pid = SimTs.VideoPid;
            p[1] = (byte)((gop ? 0x40 : 0) | (pid >> 8)); p[2] = (byte)(pid & 0xff);
            int cc = (int)(n & 0xf);
            int pos = 4;
            if (gop || pcr)
            {
                p[3] = (byte)(0x30 | cc);                                    // adaptation field + payload
                int afLen = 1 + (pcr ? 6 : 0);
                p[4] = (byte)afLen;
                p[5] = (byte)((gop && MarkRandomAccess ? 0x40 : 0) | (pcr ? 0x10 : 0));
                if (pcr)
                {
                    long v = Pcr27(n);
                    long bas = v / 300, ext = v % 300;
                    p[6] = (byte)(bas >> 25); p[7] = (byte)(bas >> 17); p[8] = (byte)(bas >> 9); p[9] = (byte)(bas >> 1);
                    p[10] = (byte)(((bas & 1) << 7) | 0x7e | (ext >> 8)); p[11] = (byte)ext;
                }
                pos = 5 + afLen;
            }
            else p[3] = (byte)(0x10 | cc);                                   // payload only
            if (gop)
            {
                var pes = new byte[] { 0, 0, 1, 0xE0, 0, 0, 0x80, 0x80, 5, 0x21, 0, 1, 0, 1,                // PES with PTS
                    0, 0, 0, 1, 0x09, 0xF0,                                                                  // AUD
                    0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1E, 0xDA, 0x01,                                          // SPS (type 7)
                    0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,                                                      // PPS
                    0, 0, 0, 1, 0x65, 0x88, 0x84 };                                                          // IDR (type 5)
                Buffer.BlockCopy(pes, 0, p, pos, pes.Length);
            }
            for (int i = 0; i < 8; i++) p[SimTs.PacketSize - 8 + i] = (byte)(n >> (56 - 8 * i));
            return p;
        }

        /// <summary>Number of a video packet of the stream with media.</summary>
        public static long NumberOf(byte[] d, int packetStart)
        {
            long n = 0;
            for (int i = 0; i < 8; i++) n = (n << 8) | d[packetStart + SimTs.PacketSize - 8 + i];
            return n;
        }

        /// <summary>count bytes of the stream from 'offset' (with 'garbage' bytes of garbage at the start, like SimTs.Content).</summary>
        public byte[] Content(int garbage, long offset, int count)
        {
            var r = new byte[count];
            for (int i = 0; i < count; )
            {
                long pos = offset + i - garbage;
                if (pos < 0) { r[i] = (byte)(0xA5 ^ (i & 0xff)); if (r[i] == 0x47) r[i] = 0x46; i++; continue; }
                long n = pos / SimTs.PacketSize;
                int inPkt = (int)(pos % SimTs.PacketSize);
                var pkt = Packet(n);
                int take = Math.Min(count - i, SimTs.PacketSize - inPkt);
                Buffer.BlockCopy(pkt, inPkt, r, i, take);
                i += take;
            }
            return r;
        }
    }
}
