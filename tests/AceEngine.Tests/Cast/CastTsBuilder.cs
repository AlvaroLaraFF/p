using System;
using System.Collections.Generic;
using System.IO;
using AceEngine.Cast;

namespace AceEngine.Tests.Cast
{
    public enum TestAudio { Adts, Latm, Mp2, Mp3, Ac3, Eac3Private, None }
    public enum TestVideo { H264, Mpeg2, Hevc }
    /// <summary>How an encoder orders the NAL units that open a key access unit.</summary>
    public enum TestNalOrder { Canonical, SeiFirst, SpsFirst }

    /// <summary>Writes bits (for SPS) with the Exp-Golomb codes of H.264.</summary>
    internal sealed class BitWriter
    {
        private readonly List<byte> _b = new List<byte>();
        private int _cur, _n;

        public void Bit(int v) { _cur = (_cur << 1) | (v & 1); if (++_n == 8) { _b.Add((byte)_cur); _cur = 0; _n = 0; } }
        public void Bits(long v, int n) { for (int i = n - 1; i >= 0; i--) Bit((int)((v >> i) & 1)); }
        public void Ue(uint v)
        {
            uint x = v + 1;
            int len = 0;
            for (uint t = x; t > 1; t >>= 1) len++;
            for (int i = 0; i < len; i++) Bit(0);
            Bits(x, len + 1);
        }
        public void Se(int v) { Ue(v > 0 ? (uint)(2 * v - 1) : (uint)(-2 * v)); }
        public byte[] Finish()
        {
            Bit(1);
            while (_n != 0) Bit(0);
            return _b.ToArray();
        }
    }

    /// <summary>H.264 NAL units for the generated streams.</summary>
    internal static class H264Bytes
    {
        public static byte[] Epb(byte[] rbsp)
        {
            var o = new List<byte>();
            int zeros = 0;
            foreach (var b in rbsp)
            {
                if (zeros >= 2 && b <= 3) { o.Add(3); zeros = 0; }
                o.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return o.ToArray();
        }

        /// <summary>A real SPS NAL (header byte included).</summary>
        public static byte[] Sps(int width, int height, int profile = 100, int level = 40, double fps = 25, bool interlaced = false, bool vui = true, int chroma = 1, int bitDepth = 8, bool scaling = false)
        {
            var w = new BitWriter();
            w.Bits(profile, 8); w.Bits(0, 8); w.Bits(level, 8);
            w.Ue(0);
            if (profile >= 100)
            {
                w.Ue((uint)chroma);
                if (chroma == 3) w.Bit(0);
                w.Ue((uint)(bitDepth - 8)); w.Ue((uint)(bitDepth - 8));
                w.Bit(0);
                if (scaling)
                {
                    w.Bit(1);
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    {
                        if (i == 1) { w.Bit(1); int size = 16; for (int j = 0; j < size; j++) w.Se(j == 0 ? 2 : 0); }
                        else w.Bit(0);
                    }
                }
                else w.Bit(0);
            }
            w.Ue(0);                                   // log2_max_frame_num_minus4
            w.Ue(0); w.Ue(2);                          // poc type 0, log2_max_poc_lsb_minus4
            w.Ue(4); w.Bit(0);                         // max_num_ref_frames, gaps
            int mbsW = (width + 15) / 16;
            int mbsH = (height + 15) / 16;
            int mapUnits = interlaced ? (mbsH + 1) / 2 : mbsH;
            w.Ue((uint)(mbsW - 1)); w.Ue((uint)(mapUnits - 1));
            w.Bit(interlaced ? 0 : 1);
            if (interlaced) w.Bit(0);
            w.Bit(1);                                  // direct_8x8_inference
            int cropY = mbsH * 16 - height;
            if (cropY > 0)
            {
                w.Bit(1); w.Ue(0); w.Ue(0); w.Ue(0); w.Ue((uint)(cropY / (interlaced ? 4 : 2)));
            }
            else w.Bit(0);
            if (vui)
            {
                w.Bit(1);
                w.Bit(0); w.Bit(0); w.Bit(0); w.Bit(0);   // aspect, overscan, video signal, chroma loc
                w.Bit(1); w.Bits(1000, 32); w.Bits((long)(fps * 2000), 32); w.Bit(1);
                w.Bit(0); w.Bit(0); w.Bit(0); w.Bit(0);
            }
            else w.Bit(0);
            var rbsp = w.Finish();
            var epb = Epb(rbsp);
            var nal = new byte[epb.Length + 1];
            nal[0] = 0x67;
            Buffer.BlockCopy(epb, 0, nal, 1, epb.Length);
            return nal;
        }

        public static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };
        public static readonly byte[] Aud = { 0x09, 0xF0 };
    }

    /// <summary>
    /// Generator of a realistic H.264 + audio MPEG transport stream: PAT/PMT, PCR, PES with PTS, SPS/PPS/IDR at each GOP, P frames, ADTS audio, and the continuity
    /// counters of a real muxer. Stateful: Generate() can be called again to continue the same stream; Break() starts a new timeline (as a sender restart would).
    /// </summary>
    internal sealed class TsGen
    {
        public int Fps = 25, GopFrames = 50, Width = 1920, Height = 1080, Level = 40, Profile = 100;
        public bool Interlaced;
        public int VideoPid = 0x100, AudioPid = 0x101, PmtPid = 0x1000, ProgramNumber = 1;
        public TestVideo Video = TestVideo.H264;
        public TestAudio Audio = TestAudio.Adts;
        public int IdrBytes = 24000, PBytes = 2400;
        public bool RepeatSpsOnEveryFrame;
        public bool NonIdrKey;                // keys are I slices (nal 1) with SPS in front, not IDR
        public bool SpsLate;                  // pad the first packet of a key AU so the IDR slice starts in a later packet
        public bool OmitPts;
        public TestNalOrder NalOrder = TestNalOrder.Canonical;
        public bool ExtraPidNoise = true;     // a teletext-like PID and null packets in between
        public bool Pcr = true;
        public long PtsBase = 90000L * 3600;
        public double PcrLeadSeconds = 0.7;
        public int AudioFramesPerPes = 6;
        public double AudioAheadSeconds = 0.1;
        public int SpsWidthOverride = 0;

        private readonly int[] _cc = new int[8192];
        private long _frame;                  // frames generated so far (decode order == display order, no B frames)
        private double _audioNext;            // seconds of audio generated so far
        private double _muxTime;              // mux clock (seconds) at the end of what has been generated
        private double _lastPsi = -1, _lastPcr = -1;
        private long _pcrBase = 27000000L * 100;
        private byte[] _sps;
        private int _mpegAudioKind;

        public double MuxSeconds { get { return _muxTime; } }
        public long FramesGenerated { get { return _frame; } }

        /// <summary>A new timeline: PTS and PCR jump to unrelated values, the continuity counters restart.</summary>
        public void Break(long newPtsBase = -1, bool resetCc = true)
        {
            PtsBase = newPtsBase >= 0 ? newPtsBase : (PtsBase + 90000L * 5000) & ((1L << 33) - 1);
            _pcrBase += 27000000L * 7777;
            _pcrBase &= ((1L << 33) * 300) - 1;
            if (resetCc) Array.Clear(_cc, 0, _cc.Length);
            _frame = 0; _audioNext = 0; _muxTime = 0; _lastPsi = -1; _lastPcr = -1;
        }

        public byte[] Generate(double seconds)
        {
            var ms = new MemoryStream();
            long frames = (long)Math.Round(seconds * Fps);
            for (long i = 0; i < frames; i++) EmitFrame(ms);
            return ms.ToArray();
        }

        public byte[] GenerateFrames(int frames)
        {
            var ms = new MemoryStream();
            for (int i = 0; i < frames; i++) EmitFrame(ms);
            return ms.ToArray();
        }

        private void EmitFrame(MemoryStream ms)
        {
            double t = (double)_frame / Fps;
            _muxTime = t;
            // audio that is due before this frame's mux time
            while (Audio != TestAudio.None && _audioNext < t + AudioAheadSeconds) EmitAudioPes(ms);
            if (_lastPsi < 0 || _muxTime - _lastPsi >= 0.1) { WritePsi(ms); _lastPsi = _muxTime; }
            if (ExtraPidNoise && _frame % 7 == 0)
            {
                WriteRaw(ms, 0x1FFF, false, new byte[184], 0, false);                       // null packet
                WriteRaw(ms, 0x300, true, new byte[] { 0, 0, 1, 0xBD, 0, 10, 0x80, 0, 0, 1, 2, 3, 4, 5 }, 0, false);   // some private PES on another PID
            }
            bool key = _frame % GopFrames == 0;
            WriteVideoAu(ms, key, t);
            _frame++;
            _muxTime = (double)_frame / Fps;
        }

        private static void Rnd(byte[] b, int off, int n, long seed)
        {
            long s = seed * 2654435761L + 12345;
            for (int i = 0; i < n; i++) { s = s * 6364136223846793005L + 1442695040888963407L; b[off + i] = (byte)(s >> 56); if (b[off + i] == 0) b[off + i] = 0x55; }
        }

        private long Pts90(double seconds) { return (PtsBase + (long)Math.Round((seconds + PcrLeadSeconds) * 90000)) & ((1L << 33) - 1); }
        private long Pcr27(double mux) { return (_pcrBase + (long)Math.Round(mux * 27000000)) % (((1L << 33) * 300)); }

        private void WriteVideoAu(MemoryStream ms, bool key, double t)
        {
            var au = new List<byte>();
            var aud = new List<byte>(); aud.AddRange(new byte[] { 0, 0, 0, 1 }); aud.AddRange(H264Bytes.Aud);
            var ps = new List<byte>();
            var sei = new List<byte>();
            if (Video == TestVideo.H264)
            {
                if (key || RepeatSpsOnEveryFrame)
                {
                    if (_sps == null) _sps = H264Bytes.Sps(SpsWidthOverride > 0 ? SpsWidthOverride : Width, Height, Profile, Level, Fps, Interlaced);
                    ps.AddRange(new byte[] { 0, 0, 0, 1 }); ps.AddRange(_sps);
                    ps.AddRange(new byte[] { 0, 0, 0, 1 }); ps.AddRange(H264Bytes.Pps);
                }
                if (key && NalOrder != TestNalOrder.Canonical)
                {
                    sei.AddRange(new byte[] { 0, 0, 0, 1, 0x06, 0x05, 0x11 }); for (int u = 0; u < 16; u++) sei.Add((byte)(0x20 + u)); sei.AddRange(new byte[] { 0x41, 0x80 });
                }
            }
            if (NalOrder == TestNalOrder.SeiFirst) { au.AddRange(aud); au.AddRange(sei); au.AddRange(ps); }
            else if (NalOrder == TestNalOrder.SpsFirst) { au.AddRange(ps); au.AddRange(aud); au.AddRange(sei); }
            else { au.AddRange(aud); au.AddRange(ps); }
            if (Video == TestVideo.H264)
            {
                if (key && SpsLate)
                {
                    // a long SEI between the parameter sets and the slice: the slice starts in the second packet
                    au.AddRange(new byte[] { 0, 0, 1, 0x06 }); var seiBig = new byte[200]; Rnd(seiBig, 0, seiBig.Length, _frame); au.AddRange(seiBig);
                }
                au.AddRange(new byte[] { 0, 0, 1, (byte)(key && !NonIdrKey ? 0x65 : 0x41) });
                au.Add((byte)(key ? 0x88 : 0x9A));                       // first_mb_in_slice = 0, slice_type 7 (I) or 5 (P)
            }
            else if (Video == TestVideo.Mpeg2)
            {
                if (key) au.AddRange(new byte[] { 0, 0, 1, 0xB3, 0x78, 0x04, 0x38, 0x13 });
                au.AddRange(new byte[] { 0, 0, 1, 0x00, 0x00, 0x0F, 0xFF });
            }
            else
            {
                if (key) { au.AddRange(new byte[] { 0, 0, 0, 1, 0x40, 0x01, 0x0C }); au.AddRange(new byte[] { 0, 0, 0, 1, 0x42, 0x01, 0x01 }); }
                au.AddRange(new byte[] { 0, 0, 0, 1, (byte)(key ? 0x26 : 0x02), 0x01 });
            }
            int body = key ? IdrBytes : PBytes;
            var filler = new byte[body];
            Rnd(filler, 0, body, _frame);
            au.AddRange(filler);

            // PES header with PTS (and DTS == PTS-less: no B frames)
            var pes = new List<byte>();
            pes.AddRange(new byte[] { 0, 0, 1, 0xE0, 0, 0 });
            if (OmitPts) pes.AddRange(new byte[] { 0x80, 0x00, 0x00 });
            else
            {
                pes.AddRange(new byte[] { 0x80, 0x80, 5 });
                long pts = Pts90(t);
                pes.Add((byte)(0x21 | (((pts >> 30) & 7) << 1)));
                pes.Add((byte)(pts >> 22)); pes.Add((byte)(((pts >> 15) << 1) | 1));
                pes.Add((byte)(pts >> 7)); pes.Add((byte)((pts << 1) | 1));
            }
            pes.AddRange(au);
            WritePes(ms, VideoPid, pes.ToArray(), key, t);
        }

        private void WritePes(MemoryStream ms, int pid, byte[] pes, bool randomAccess, double mux)
        {
            int pos = 0;
            bool first = true;
            while (pos < pes.Length)
            {
                var p = new byte[188];
                p[0] = 0x47;
                p[1] = (byte)((first ? 0x40 : 0) | (pid >> 8)); p[2] = (byte)pid;
                int remaining = pes.Length - pos;
                bool wantPcr = first && Pcr && pid == VideoPid && (_lastPcr < 0 || mux - _lastPcr >= 0.039);
                bool rai = first && randomAccess && pid == VideoPid;
                int afBase = (wantPcr || rai) ? 1 + (wantPcr ? 6 : 0) : -1;
                int cap = 184 - (afBase >= 0 ? afBase + 1 : 0);
                int n = Math.Min(remaining, cap);
                int stuff = cap - n;
                int afLen = afBase < 0 ? (stuff == 0 ? -1 : stuff - 1) : afBase + stuff;
                int cc = _cc[pid]++ & 0xF;
                if (afLen >= 0)
                {
                    p[3] = (byte)(0x30 | cc);
                    p[4] = (byte)afLen;
                    for (int q = 5; q < 5 + afLen; q++) p[q] = 0xFF;
                    if (afLen > 0)
                    {
                        p[5] = (byte)((rai ? 0x40 : 0) | (wantPcr ? 0x10 : 0));
                        if (wantPcr)
                        {
                            long v = Pcr27(mux);
                            long bas = v / 300, ext = v % 300;
                            p[6] = (byte)(bas >> 25); p[7] = (byte)(bas >> 17); p[8] = (byte)(bas >> 9); p[9] = (byte)(bas >> 1);
                            p[10] = (byte)(((bas & 1) << 7) | 0x7E | (ext >> 8)); p[11] = (byte)ext;
                            _lastPcr = mux;
                        }
                    }
                    Buffer.BlockCopy(pes, pos, p, 5 + afLen, n);
                }
                else
                {
                    p[3] = (byte)(0x10 | cc);
                    Buffer.BlockCopy(pes, pos, p, 4, n);
                }
                pos += n;
                first = false;
                ms.Write(p, 0, 188);
            }
        }

        private void WriteRaw(MemoryStream ms, int pid, bool pusi, byte[] payload, int cc, bool useCc)
        {
            var p = new byte[188];
            p[0] = 0x47; p[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8)); p[2] = (byte)pid;
            int c = pid == 0x1FFF ? 0 : _cc[pid]++ & 0xF;
            p[3] = (byte)(0x10 | c);
            for (int i = 4; i < 188; i++) p[i] = 0xFF;
            Buffer.BlockCopy(payload, 0, p, 4, Math.Min(payload.Length, 184));
            ms.Write(p, 0, 188);
        }

        private void EmitAudioPes(MemoryStream ms)
        {
            var es = new List<byte>();
            double start = _audioNext;
            int frames = AudioFramesPerPes;
            for (int i = 0; i < frames; i++)
            {
                switch (Audio)
                {
                    case TestAudio.Adts:
                        {
                            int len = 7 + 90;
                            es.AddRange(new byte[] { 0xFF, 0xF1, (byte)((1 << 6) | (3 << 2) | 0), (byte)((2 << 6) | (len >> 11)), (byte)(len >> 3), (byte)(((len & 7) << 5) | 0x1F), 0xFC });
                            var pl = new byte[90]; Rnd(pl, 0, pl.Length, (long)(start * 1000) + i); es.AddRange(pl);
                            break;
                        }
                    case TestAudio.Latm:
                        {
                            int len = 40;
                            es.Add(0x56); es.Add((byte)(0xE0 | (len >> 8))); es.Add((byte)len);
                            var pl = new byte[len]; Rnd(pl, 0, pl.Length, i); es.AddRange(pl);
                            break;
                        }
                    case TestAudio.Mp2:
                        // MPEG-1 layer II, 48 kHz, 192 kbit/s: frame 576 bytes
                        es.AddRange(new byte[] { 0xFF, 0xFD, 0xA4, 0x04 }); es.AddRange(new byte[572]); break;
                    case TestAudio.Mp3:
                        es.AddRange(new byte[] { 0xFF, 0xFB, 0x90, 0x04 }); es.AddRange(new byte[413]); break;
                    default:
                        es.AddRange(new byte[] { 0x0B, 0x77, 0, 0, 0x1E, 0x40 }); es.AddRange(new byte[300]); break;
                }
                _audioNext += 1024.0 / 48000;
            }
            var pes = new List<byte>();
            long pts = Pts90(start);
            pes.AddRange(new byte[] { 0, 0, 1, 0xC0 });
            int plen = 3 + 5 + es.Count;
            pes.Add((byte)(plen >> 8)); pes.Add((byte)plen);
            pes.AddRange(new byte[] { 0x80, 0x80, 5 });
            pes.Add((byte)(0x21 | (((pts >> 30) & 7) << 1)));
            pes.Add((byte)(pts >> 22)); pes.Add((byte)(((pts >> 15) << 1) | 1));
            pes.Add((byte)(pts >> 7)); pes.Add((byte)((pts << 1) | 1));
            pes.AddRange(es);
            WritePes(ms, AudioPid, pes.ToArray(), false, start);
        }

        private byte[] Crc(byte[] sec, int len)
        {
            uint crc = TsCrcForTests.Compute(sec, 0, len);
            return new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc };
        }

        private void WritePsi(MemoryStream ms)
        {
            // PAT
            var pat = new byte[] { 0x00, 0xB0, 13, 0x00, 0x01, 0xC1, 0, 0, (byte)(ProgramNumber >> 8), (byte)ProgramNumber, (byte)(0xE0 | (PmtPid >> 8)), (byte)PmtPid };
            WriteSection(ms, 0, pat);
            // PMT
            var es = new List<byte>();
            int vtype = Video == TestVideo.H264 ? 0x1B : Video == TestVideo.Mpeg2 ? 0x02 : 0x24;
            es.AddRange(new byte[] { (byte)vtype, (byte)(0xE0 | (VideoPid >> 8)), (byte)VideoPid, 0xF0, 0 });
            switch (Audio)
            {
                case TestAudio.Adts: es.AddRange(new byte[] { 0x0F, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 6, 0x0A, 4, (byte)'s', (byte)'p', (byte)'a', 0 }); break;
                case TestAudio.Latm: es.AddRange(new byte[] { 0x11, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 0 }); break;
                case TestAudio.Mp2: es.AddRange(new byte[] { 0x03, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 0 }); break;
                case TestAudio.Mp3: es.AddRange(new byte[] { 0x04, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 0 }); break;
                case TestAudio.Ac3: es.AddRange(new byte[] { 0x81, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 0 }); break;
                case TestAudio.Eac3Private: es.AddRange(new byte[] { 0x06, (byte)(0xE0 | (AudioPid >> 8)), (byte)AudioPid, 0xF0, 2, 0x7A, 0 }); break;
            }
            if (ExtraPidNoise) es.AddRange(new byte[] { 0x06, 0xE3, 0x00, 0xF0, 2, 0x56, 0 });
            int pcrPid = VideoPid;
            var pmt = new List<byte> { 0x02, 0xB0, 0, (byte)(ProgramNumber >> 8), (byte)ProgramNumber, 0xC1, 0, 0, (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0 };
            pmt.AddRange(es);
            int secLen = pmt.Count - 3 + 4;
            pmt[1] = (byte)(0xB0 | (secLen >> 8)); pmt[2] = (byte)secLen;
            WriteSection(ms, PmtPid, pmt.ToArray());
        }

        private void WriteSection(MemoryStream ms, int pid, byte[] sec)
        {
            var crc = Crc(sec, sec.Length);
            var p = new byte[188];
            for (int i = 0; i < 188; i++) p[i] = 0xFF;
            p[0] = 0x47; p[1] = (byte)(0x40 | (pid >> 8)); p[2] = (byte)pid; p[3] = (byte)(0x10 | (_cc[pid]++ & 0xF)); p[4] = 0;
            Buffer.BlockCopy(sec, 0, p, 5, sec.Length);
            Buffer.BlockCopy(crc, 0, p, 5 + sec.Length, 4);
            ms.Write(p, 0, 188);
        }
    }

    internal static class TsCrcForTests
    {
        public static uint Compute(byte[] d, int off, int len)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++)
            {
                crc ^= (uint)d[off + i] << 24;
                for (int k = 0; k < 8; k++) crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
            }
            return crc;
        }
    }

    /// <summary>Reads a generated or served transport stream back, packet by packet, for the assertions.</summary>
    internal sealed class TsReader
    {
        public sealed class Pkt
        {
            public int Index, Pid, Cc;
            public bool Pusi, Rai, Pcr;
            public long PcrValue = -1;
            public byte[] Raw;
            public int PayloadStart;
            public bool HasPayload;
            public long Pts = -1;
            public bool Idr;
        }

        public static List<Pkt> Parse(byte[] d, int off, int len)
        {
            var l = new List<Pkt>();
            for (int i = off, n = 0; i + 188 <= off + len; i += 188, n++)
            {
                var p = new Pkt { Index = n, Raw = new byte[188] };
                Buffer.BlockCopy(d, i, p.Raw, 0, 188);
                var b = p.Raw;
                if (b[0] != 0x47) throw new InvalidDataException("sync lost at packet " + n);
                p.Pid = ((b[1] & 0x1F) << 8) | b[2];
                p.Pusi = (b[1] & 0x40) != 0;
                int afc = (b[3] >> 4) & 3;
                p.Cc = b[3] & 0xF;
                int ps = 4;
                if ((afc & 2) != 0)
                {
                    int afLen = b[4];
                    if (afLen >= 1) { p.Rai = (b[5] & 0x40) != 0; p.Pcr = (b[5] & 0x10) != 0 && afLen >= 7; }
                    if (p.Pcr)
                    {
                        long bas = ((long)b[6] << 25) | ((long)b[7] << 17) | ((long)b[8] << 9) | ((long)b[9] << 1) | (long)(b[10] >> 7);
                        p.PcrValue = bas * 300 + (((b[10] & 1) << 8) | b[11]);
                    }
                    ps = 5 + afLen;
                }
                p.HasPayload = (afc & 1) != 0 && ps < 188;
                p.PayloadStart = ps;
                if (p.Pusi && p.HasPayload && ps + 14 <= 188 && b[ps] == 0 && b[ps + 1] == 0 && b[ps + 2] == 1)
                {
                    if ((b[ps + 7] & 0x80) != 0)
                        p.Pts = ((long)(b[ps + 9] & 0x0E) << 29) | ((long)b[ps + 10] << 22) | ((long)(b[ps + 11] & 0xFE) << 14) | ((long)b[ps + 12] << 7) | (long)(b[ps + 13] >> 1);
                    int es = ps + 9 + b[ps + 8];
                    for (int k = es; k + 3 < 188; k++)
                        if (b[k] == 0 && b[k + 1] == 0 && b[k + 2] == 1 && (b[k + 3] & 0x1F) == 5) { p.Idr = true; break; }
                }
                l.Add(p);
            }
            return l;
        }
    }
}
