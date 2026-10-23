using System;
using System.Collections.Generic;

namespace AceEngine.Sim
{
    /// <summary>
    /// Synthetic, deterministic MPEG-TS stream: packet n (188 bytes) carries its number n in the payload, so a reader can detect lost,
    /// repeated or out-of-order packets. Every 200 packets there is a valid PAT and PMT (H.264 on PID 0x100 and AAC on 0x101).
    /// </summary>
    public static class SimTs
    {
        public const int PacketSize = 188;
        public const int VideoPid = 0x100, AudioPid = 0x101, PmtPid = 0x1000;
        public const int PsiPeriod = 200;

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                t[i] = c;
            }
            return t;
        }

        private static uint Crc32Mpeg(byte[] d, int off, int len)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < len; i++) crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ d[off + i]) & 0xff];
            return crc;
        }

        /// <summary>Packet number n of the stream.</summary>
        public static byte[] Packet(long n)
        {
            var p = new byte[PacketSize];
            for (int i = 4; i < PacketSize; i++) p[i] = 0xff;
            p[0] = 0x47;
            if (n % PsiPeriod == 0)
            {
                bool pat = (n / PsiPeriod) % 2 == 0;
                int pid = pat ? 0 : PmtPid;
                p[1] = (byte)(0x40 | (pid >> 8)); p[2] = (byte)(pid & 0xff); p[3] = (byte)(0x10 | (int)((n / PsiPeriod / 2) & 0xf));
                p[4] = 0;      // pointer_field
                var sec = new List<byte>();
                if (pat)
                {
                    sec.AddRange(new byte[] { 0x00, 0xB0, 13, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), (byte)(PmtPid & 0xff) });
                }
                else
                {
                    sec.AddRange(new byte[] { 0x02, 0xB0, 23, 0x00, 0x01, 0xC1, 0x00, 0x00, (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xff), 0xF0, 0x00,
                        0x1B, (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xff), 0xF0, 0x00, 0x0F, (byte)(0xE0 | (AudioPid >> 8)), (byte)(AudioPid & 0xff), 0xF0, 0x00 });
                }
                var arr = sec.ToArray();
                uint crc = Crc32Mpeg(arr, 0, arr.Length);
                Buffer.BlockCopy(arr, 0, p, 5, arr.Length);
                p[5 + arr.Length] = (byte)(crc >> 24); p[6 + arr.Length] = (byte)(crc >> 16); p[7 + arr.Length] = (byte)(crc >> 8); p[8 + arr.Length] = (byte)crc;
                return p;
            }
            p[1] = (byte)(VideoPid >> 8); p[2] = (byte)(VideoPid & 0xff);
            p[3] = (byte)(0x10 | (int)(n & 0xf));            // payload only; continuity counter = n mod 16
            for (int i = 0; i < 8; i++) p[4 + i] = (byte)(n >> (56 - 8 * i));
            for (int i = 12; i < PacketSize; i++) p[i] = (byte)((n * 31 + i * 7) & 0xff);
            return p;
        }

        /// <summary>Content that is not TS (without any periodic 0x47).</summary>
        public static byte[] Raw(long offset, int count)
        {
            var r = new byte[count];
            for (int i = 0; i < count; i++) { r[i] = (byte)((offset + i) * 131 + 17); if (r[i] == 0x47) r[i] = 0x48; }
            return r;
        }

        /// <summary>count bytes of the stream from offset 'offset' (the stream starts with 'garbage' bytes of garbage and then packet 0).</summary>
        public static byte[] Content(int garbage, long offset, int count)
        {
            var r = new byte[count];
            for (int i = 0; i < count; )
            {
                long pos = offset + i - garbage;
                if (pos < 0) { r[i] = (byte)(0xA5 ^ (i & 0xff)); if (r[i] == 0x47) r[i] = 0x46; i++; continue; }
                long n = pos / PacketSize;
                int inPkt = (int)(pos % PacketSize);
                var pkt = Packet(n);
                int take = Math.Min(count - i, PacketSize - inPkt);
                Buffer.BlockCopy(pkt, inPkt, r, i, take);
                i += take;
            }
            return r;
        }
    }

    /// <summary>
    /// Verifies a stream produced by SimTs: 0x47 sync every 188 bytes, content of each packet (deterministic) and that the packet numbers
    /// are consecutive (each jump is a gap; how many packets are missing is recorded).
    /// </summary>
    public sealed class SimStreamVerifier
    {
        private const int PacketSize = SimTs.PacketSize;
        private byte[] _carry = new byte[0];
        private long _expected = -1;                         // number of the next expected packet
        public long Bytes, Packets, DataPackets, SyncErrors, CorruptPackets, Duplicates;
        public readonly List<long> Gaps = new List<long>();     // packets lost in each jump
        public long FirstPacket = -1, LastPacket = -1;
        public bool Started;
        private readonly SimTsProfile _profile;

        public SimStreamVerifier() { }

        /// <summary>Verifier of a stream with a media profile (SimTsProfile): the packet number is in the last 8 bytes.</summary>
        public SimStreamVerifier(SimTsProfile profile) { _profile = profile; }

        public void Feed(byte[] buf, int off, int count)
        {
            Bytes += count;
            var all = new byte[_carry.Length + count];
            Buffer.BlockCopy(_carry, 0, all, 0, _carry.Length);
            Buffer.BlockCopy(buf, off, all, _carry.Length, count);
            int pos = 0;
            if (!Started)
            {
                if (all.Length < 3 * PacketSize) { _carry = all; return; }
                if (all[0] != 0x47 || all[PacketSize] != 0x47 || all[2 * PacketSize] != 0x47) SyncErrors++;
                Started = true;
            }
            while (pos + PacketSize <= all.Length)
            {
                if (all[pos] != 0x47) { SyncErrors++; pos++; continue; }
                Packets++;
                int pid = ((all[pos + 1] & 0x1f) << 8) | all[pos + 2];
                if (pid == SimTs.VideoPid)
                {
                    long n = 0;
                    if (_profile != null) n = SimTsProfile.NumberOf(all, pos);
                    else for (int i = 0; i < 8; i++) n = (n << 8) | all[pos + 4 + i];
                    var expect = n < 0 || n > (1L << 40) ? new byte[PacketSize] : _profile != null ? _profile.Packet(n) : SimTs.Packet(n);
                    bool same = n % SimTs.PsiPeriod != 0;
                    for (int i = 0; same && i < PacketSize; i++) if (all[pos + i] != expect[i]) same = false;
                    if (!same) CorruptPackets++;
                    else
                    {
                        DataPackets++;
                        if (FirstPacket < 0) FirstPacket = n;
                        if (_expected >= 0)
                        {
                            if (n > _expected) Gaps.Add(n - _expected);
                            else if (n < _expected) Duplicates++;
                        }
                        _expected = n + 1; LastPacket = n;
                    }
                }
                else if (pid == 0 || pid == SimTs.PmtPid)
                {
                    if (_expected >= 0)
                    {
                        long psi = ((_expected + SimTs.PsiPeriod - 1) / SimTs.PsiPeriod) * SimTs.PsiPeriod;     // the PSI are the packets that are multiples of 200
                        var expect = SimTs.Packet(psi);
                        bool same = true;
                        for (int i = 0; same && i < PacketSize; i++) if (all[pos + i] != expect[i]) same = false;
                        if (!same) CorruptPackets++;
                        else
                        {
                            if (psi > _expected) Gaps.Add(psi - _expected);
                            _expected = psi + 1;
                        }
                    }
                }
                else CorruptPackets++;
                pos += PacketSize;
            }
            _carry = new byte[all.Length - pos];
            Buffer.BlockCopy(all, pos, _carry, 0, _carry.Length);
        }

        public long TotalLost { get { long t = 0; foreach (var g in Gaps) t += g; return t; } }
    }
}
