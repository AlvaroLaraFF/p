using System;
using System.Collections.Generic;
using System.Text;

namespace AceEngine.Cast
{
    /// <summary>What an elementary stream of a program carries, deduced from the PMT (stream_type plus descriptors).</summary>
    public enum EsKind
    {
        Unknown,
        Mpeg1Video, Mpeg2Video, H264, Hevc, OtherVideo,
        AacAdts, AacLatm, MpegAudio, Ac3, Eac3, Dts, OtherAudio,
        Subtitle, Teletext, Data
    }

    /// <summary>One elementary stream announced by the PMT.</summary>
    public sealed class EsInfo
    {
        public int Pid;
        public int StreamType;
        public EsKind Kind;
        /// <summary>ISO 639 language of the stream ("spa"), or "".</summary>
        public string Language = "";
        /// <summary>The ES_info descriptors as the PMT carries them (copied into the PMT the HLS segments open with).</summary>
        public byte[] Descriptors = new byte[0];

        public bool IsVideo { get { return Kind >= EsKind.Mpeg1Video && Kind <= EsKind.OtherVideo; } }
        public bool IsAudio { get { return Kind >= EsKind.AacAdts && Kind <= EsKind.OtherAudio; } }

        public override string ToString() { return "pid 0x" + Pid.ToString("x") + " type 0x" + StreamType.ToString("x2") + " " + Kind + (Language.Length > 0 ? " " + Language : ""); }
    }

    /// <summary>A program as the last PMT describes it.</summary>
    public sealed class ProgramInfo
    {
        public int ProgramNumber, PmtPid, PcrPid, Version;
        public readonly List<EsInfo> Streams = new List<EsInfo>();

        /// <summary>First video stream (null if there is none).</summary>
        public EsInfo Video { get { foreach (var s in Streams) if (s.IsVideo) return s; return null; } }
        public IEnumerable<EsInfo> AudioStreams { get { foreach (var s in Streams) if (s.IsAudio) yield return s; } }

        /// <summary>Identity of the content of the program: changes when a stream appears, disappears or changes type or PID (not with a mere version bump).</summary>
        public string Signature
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append(ProgramNumber).Append('/').Append(PmtPid).Append('/').Append(PcrPid);
                foreach (var s in Streams) sb.Append(';').Append(s.Pid).Append(':').Append(s.StreamType).Append(':').Append((int)s.Kind);
                return sb.ToString();
            }
        }
    }

    /// <summary>CRC-32/MPEG-2 (polynomial 0x04C11DB7, no reflection), the one that closes the PSI sections.</summary>
    internal static class TsCrc
    {
        private static readonly uint[] Table = Make();

        private static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] d, int off, int len)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) crc = (crc << 8) ^ Table[((crc >> 24) ^ d[off + i]) & 0xFF];
            return crc;
        }
    }

    /// <summary>
    /// Reassembles the PSI sections of one PID (they can be spread over several TS packets). Only the section that starts in a packet is taken
    /// (plus the tail of the previous one); it checks the CRC, so a garbled section is never returned.
    /// </summary>
    internal sealed class PsiAssembler
    {
        private const int Pkt = 188;
        private readonly byte[] _buf = new byte[1024 + 3 + Pkt];
        private int _len, _need, _lastCc = -1;
        private bool _active;

        public void Reset() { _active = false; _len = 0; _need = 0; _lastCc = -1; }

        /// <summary>Feeds a packet of this PID. Returns a copy of the complete, CRC-verified section, or null.</summary>
        public byte[] Feed(byte[] d, int p)
        {
            if ((d[p + 1] & 0x80) != 0) { _active = false; return null; }                  // transport_error_indicator
            int afc = (d[p + 3] >> 4) & 3;
            if ((afc & 1) == 0) return null;
            int ps = p + 4;
            if ((afc & 2) != 0) { int af = d[p + 4]; if (af > 183) { _active = false; return null; } ps = p + 5 + af; }
            int end = p + Pkt;
            if (ps >= end) return null;
            bool pusi = (d[p + 1] & 0x40) != 0;
            int cc = d[p + 3] & 0xF;
            byte[] result = null;
            if (pusi)
            {
                int ptr = d[ps];
                int start = ps + 1;
                if (start + ptr > end) { _active = false; _lastCc = cc; return null; }
                if (_active && ptr > 0 && (_lastCc < 0 || cc == ((_lastCc + 1) & 0xF)))
                {
                    Append(d, start, ptr);
                    result = TryComplete();
                }
                _active = false; _len = 0; _need = 0;
                int s = start + ptr;
                if (s < end && d[s] != 0xFF)
                {
                    _active = true;
                    Append(d, s, end - s);
                    var r2 = TryComplete();
                    if (r2 != null) result = r2;
                }
            }
            else if (_active)
            {
                if (_lastCc >= 0 && cc != ((_lastCc + 1) & 0xF)) _active = false;           // a packet was lost: the section is garbled
                else { Append(d, ps, end - ps); result = TryComplete(); }
            }
            _lastCc = cc;
            return result;
        }

        private void Append(byte[] d, int off, int n)
        {
            if (_len + n > _buf.Length) { _active = false; return; }
            Buffer.BlockCopy(d, off, _buf, _len, n);
            _len += n;
        }

        private byte[] TryComplete()
        {
            if (!_active) return null;
            if (_need == 0 && _len >= 3) _need = 3 + (((_buf[1] & 0xF) << 8) | _buf[2]);
            if (_need == 0) return null;
            if (_need > 1024 + 3) { _active = false; return null; }
            if (_len < _need) return null;
            _active = false;
            if ((_buf[1] & 0x80) != 0)
            {
                if (_need < 12) return null;
                uint calc = TsCrc.Compute(_buf, 0, _need - 4);
                uint have = ((uint)_buf[_need - 4] << 24) | ((uint)_buf[_need - 3] << 16) | ((uint)_buf[_need - 2] << 8) | _buf[_need - 1];
                if (calc != have) return null;
            }
            var r = new byte[_need];
            Buffer.BlockCopy(_buf, 0, r, 0, _need);
            return r;
        }
    }

    /// <summary>
    /// Follows the PAT and the PMT of a transport stream and exposes the program as the last PMT describes it. Single-program streams are
    /// the norm; if the PAT lists several programs the first one is followed.
    /// </summary>
    internal sealed class TsProgramParser
    {
        private readonly PsiAssembler _pat = new PsiAssembler();
        private readonly PsiAssembler _pmt = new PsiAssembler();
        private int _pmtPid = -1;
        private int _programNumber;

        /// <summary>The last valid PMT (null until one has been seen).</summary>
        public ProgramInfo Program { get; private set; }
        public int PmtPid { get { return _pmtPid; } }
        /// <summary>Programs in the last PAT (0 until the PAT is known).</summary>
        public int ProgramCount { get; private set; }
        /// <summary>How many times the content of the program changed after the first PMT (see ProgramInfo.Signature).</summary>
        public int ContentChanges { get; private set; }

        /// <summary>true if the packet belongs to PSI this parser follows (PAT or the PMT).</summary>
        public bool Wants(int pid) { return pid == 0 || pid == _pmtPid; }

        public void FeedPacket(byte[] d, int p)
        {
            int pid = ((d[p + 1] & 0x1F) << 8) | d[p + 2];
            if (pid == 0)
            {
                var s = _pat.Feed(d, p);
                if (s != null) OnPat(s);
            }
            else if (pid == _pmtPid)
            {
                var s = _pmt.Feed(d, p);
                if (s != null) OnPmt(s);
            }
        }

        /// <summary>The stream was cut: half-received sections are discarded.</summary>
        public void Abort() { _pat.Reset(); _pmt.Reset(); }

        private void OnPat(byte[] s)
        {
            if (s[0] != 0x00 || s.Length < 12 || (s[5] & 1) == 0) return;
            int end = s.Length - 4;
            int count = 0, firstProgram = -1, firstPmt = -1;
            for (int i = 8; i + 4 <= end; i += 4)
            {
                int program = (s[i] << 8) | s[i + 1];
                int pid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                if (program == 0) continue;                                                  // network PID
                count++;
                if (firstProgram < 0) { firstProgram = program; firstPmt = pid; }
            }
            ProgramCount = count;
            if (firstPmt < 0) return;
            if (firstPmt != _pmtPid) { _pmtPid = firstPmt; _programNumber = firstProgram; _pmt.Reset(); }
        }

        private void OnPmt(byte[] s)
        {
            if (s[0] != 0x02 || s.Length < 16 || (s[5] & 1) == 0) return;
            int pn = (s[3] << 8) | s[4];
            if (_programNumber != 0 && pn != _programNumber) return;
            var prog = new ProgramInfo { ProgramNumber = pn, PmtPid = _pmtPid, Version = (s[5] >> 1) & 0x1F, PcrPid = ((s[8] & 0x1F) << 8) | s[9] };
            int pil = ((s[10] & 0xF) << 8) | s[11];
            int end = s.Length - 4;
            int i = 12 + pil;
            if (i > end) return;
            while (i + 5 <= end)
            {
                int type = s[i];
                int pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                int esLen = ((s[i + 3] & 0xF) << 8) | s[i + 4];
                int dEnd = Math.Min(end, i + 5 + esLen);
                var es = new EsInfo { Pid = pid, StreamType = type };
                if (dEnd > i + 5) { es.Descriptors = new byte[dEnd - (i + 5)]; Buffer.BlockCopy(s, i + 5, es.Descriptors, 0, es.Descriptors.Length); }
                ClassifyEs(es, s, i + 5, dEnd);
                prog.Streams.Add(es);
                i += 5 + esLen;
            }
            var old = Program;
            if (old != null && old.Signature != prog.Signature) ContentChanges++;
            Program = prog;
        }

        // ---------- stream types ----------

        internal static void ClassifyEs(EsInfo es, byte[] d, int dOff, int dEnd)
        {
            bool ac3 = false, eac3 = false, dts = false, aac = false, teletext = false, subtitle = false, opus = false;
            for (int k = dOff; k + 2 <= dEnd; )
            {
                int tag = d[k], len = d[k + 1];
                if (k + 2 + len > dEnd) break;
                int b = k + 2;
                switch (tag)
                {
                    case 0x0A:                                                               // ISO 639 language
                        if (len >= 3 && es.Language.Length == 0) es.Language = Encoding.ASCII.GetString(d, b, 3).ToLowerInvariant();
                        break;
                    case 0x05:                                                               // registration descriptor
                        if (len >= 4)
                        {
                            string fmt = Encoding.ASCII.GetString(d, b, 4);
                            if (fmt == "AC-3") ac3 = true; else if (fmt == "EAC3") eac3 = true; else if (fmt == "DTS1" || fmt == "DTS2" || fmt == "DTS3") dts = true; else if (fmt == "Opus") opus = true;
                        }
                        break;
                    case 0x6A: ac3 = true; break;                                            // DVB AC-3
                    case 0x7A: eac3 = true; break;                                           // DVB enhanced AC-3
                    case 0x7B: dts = true; break;                                            // DVB DTS
                    case 0x7C: aac = true; break;                                            // DVB AAC
                    case 0x56: teletext = true; break;
                    case 0x59: subtitle = true; break;
                }
                k += 2 + len;
            }
            switch (es.StreamType)
            {
                case 0x01: es.Kind = EsKind.Mpeg1Video; break;
                case 0x02: es.Kind = EsKind.Mpeg2Video; break;
                case 0x10: case 0x42: case 0xEA: es.Kind = EsKind.OtherVideo; break;
                case 0x1B: es.Kind = EsKind.H264; break;
                case 0x24: es.Kind = EsKind.Hevc; break;
                case 0x03: case 0x04: es.Kind = EsKind.MpegAudio; break;
                case 0x0F: es.Kind = EsKind.AacAdts; break;
                case 0x11: es.Kind = EsKind.AacLatm; break;
                case 0x81: es.Kind = EsKind.Ac3; break;
                case 0x87: es.Kind = EsKind.Eac3; break;
                case 0x82: case 0x85: case 0x86: case 0x8A: es.Kind = EsKind.Dts; break;
                case 0x06:                                                                   // private PES: the descriptors say what it is
                    if (ac3) es.Kind = EsKind.Ac3;
                    else if (eac3) es.Kind = EsKind.Eac3;
                    else if (dts) es.Kind = EsKind.Dts;
                    else if (aac) es.Kind = EsKind.AacLatm;                                  // the DVB AAC descriptor over private data is LATM in practice
                    else if (opus) es.Kind = EsKind.OtherAudio;
                    else if (teletext) es.Kind = EsKind.Teletext;
                    else if (subtitle) es.Kind = EsKind.Subtitle;
                    else es.Kind = EsKind.Data;
                    break;
                case 0x15: case 0x0B: case 0x0C: case 0x05: es.Kind = EsKind.Data; break;
                default: es.Kind = EsKind.Unknown; break;
            }
        }
    }
}
