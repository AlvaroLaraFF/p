using System;

namespace AceEngine.Live
{
    /// <summary>
    /// Index of the MPEG-TS stream stored in the live-with-memory buffer: media clock from the PCRs, random
    /// access points (random_access_indicator or H.264/HEVC IDR / MPEG-2 sequence at the start of a video PES) and the last PAT/PMT.
    /// Not thread-safe: TimeshiftBuffer uses it under its lock.
    ///
    /// The clock is synthetic and monotonic: each PCR adds the difference from the previous one if it is reasonable (0 &lt; d &lt;= 1.5 s, without
    /// discontinuity_indicator and without a stream cut in between) and, if not, what the bytes say at the stream's rate. This way it withstands the
    /// 33-bit overflow, sender restarts, skipped holes and streams without PCR (then it is just bytes / bitrate).
    /// </summary>
    internal sealed class TsStreamIndex
    {
        private const int Pkt = 188;
        private const double SampleStepMs = 100;                 // one clock sample every ~100 ms of media
        private const double WeakStepMs = 100;                   // and a "weak" access (video PUSI) at most every 100 ms
        private const long PcrModulus = (1L << 33) * 300;        // the PCR wraps around after ~26.5 hours
        private const double MaxPcrStepMs = 1500;                // a bigger jump between two consecutive PCRs is not taken as real time
        private const int MaxScan = 4096;                        // limit of entries a random-access search walks through
        private const int MinSampleBytes = 10 * Pkt;             // minimum separation between samples and between accesses: a hostile sender
        private const int MinRapBytes = 4096;                    // cannot make the index grow (<1.5 % of the budget in the worst case)

        private struct Sample { public long Off; public double Ms; }
        private struct Rap { public long Off; public bool Strong; }

        private readonly double _nominal;                        // bytes per second of the descriptor (for when there is no PCR)
        private Sample[] _samples = new Sample[256];
        private int _sHead, _sCount;
        private Rap[] _raps = new Rap[64];
        private int _rHead, _rCount;

        // clock
        private bool _havePcr, _forceJump;
        private long _lastPcr27, _lastPcrOff;
        private double _clockMs;
        private int _pcrPid = -1;                                // PCR PID (the PMT's; until it is known, the first one that carries PCR)
        private bool _pcrPidFromPmt;

        // tables
        private readonly byte[] _pidType = new byte[8192];       // stream_type declared in the PMT (0 = not declared)
        private int _pmtPid = -1;
        private int _primaryPid = -1;                            // PID whose PES start marks the "weak" accesses
        private readonly Psi _pat = new Psi();
        private readonly Psi _pmt = new Psi();
        private byte[] _lastPmtSection;
        private bool _strongSeen;
        private double _lastWeakMs = double.NegativeInfinity;

        public long PcrCount, SampleTotal, RapTotal, ClockJumps;

        public TsStreamIndex(double nominalBytesPerSecond)
        {
            _nominal = nominalBytesPerSecond > 0 ? nominalBytesPerSecond : 262144;
        }

        public int SampleCount { get { return _sCount; } }
        public int RapCount { get { return _rCount; } }
        public int PcrPid { get { return _pcrPid; } }
        public int PmtPid { get { return _pmtPid; } }
        public bool HasPsi { get { return _pat.DoneCount > 0 && _pmt.DoneCount > 0; } }

        // ---------- Clock ----------

        /// <summary>Bytes per second measured with the retained samples (or the nominal ones while there are not enough).</summary>
        private double Rate()
        {
            if (_sCount >= 2)
            {
                var a = _samples[_sHead]; var b = _samples[_sHead + _sCount - 1];
                double dms = b.Ms - a.Ms;
                if (dms >= 1000 && b.Off > a.Off) return (b.Off - a.Off) * 1000.0 / dms;
            }
            return _nominal;
        }

        /// <summary>Media time (ms of the synthetic clock) at the given offset.</summary>
        public double MsAt(long offset)
        {
            if (_sCount == 0) return offset * 1000.0 / _nominal;
            double rate = Rate();
            int idx = FindSampleLE(offset);
            if (idx < 0) { var s0 = _samples[_sHead]; return s0.Ms - (s0.Off - offset) * 1000.0 / rate; }
            var s = _samples[_sHead + idx];
            if (idx == _sCount - 1) return s.Ms + (offset - s.Off) * 1000.0 / rate;
            var n = _samples[_sHead + idx + 1];
            if (n.Off <= s.Off) return s.Ms;
            return s.Ms + (double)(offset - s.Off) / (n.Off - s.Off) * (n.Ms - s.Ms);
        }

        /// <summary>Stream offset at the given media time (extrapolates with the measured rate outside the samples).</summary>
        public long OffsetAt(double ms)
        {
            if (_sCount == 0) return Math.Max(0, (long)(ms * _nominal / 1000.0));
            double rate = Rate();
            int lo = 0, hi = _sCount - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_samples[_sHead + mid].Ms <= ms) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            long off;
            if (idx < 0) { var s0 = _samples[_sHead]; off = s0.Off - (long)((s0.Ms - ms) * rate / 1000.0); }
            else
            {
                var s = _samples[_sHead + idx];
                if (idx == _sCount - 1) off = s.Off + (long)((ms - s.Ms) * rate / 1000.0);
                else
                {
                    var n = _samples[_sHead + idx + 1];
                    off = n.Ms <= s.Ms ? s.Off : s.Off + (long)((ms - s.Ms) / (n.Ms - s.Ms) * (n.Off - s.Off));
                }
            }
            return Math.Max(0, off);
        }

        private int FindSampleLE(long offset)
        {
            int lo = 0, hi = _sCount - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_samples[_sHead + mid].Off <= offset) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return idx;
        }

        private void AddSample(long off, double ms)
        {
            if (_sCount > 0)
            {
                var last = _samples[_sHead + _sCount - 1];
                if (off <= last.Off || ms <= last.Ms) return;                    // the index is strictly increasing in both magnitudes
            }
            if (_sHead + _sCount == _samples.Length)
            {
                if (_sHead > 0) { Array.Copy(_samples, _sHead, _samples, 0, _sCount); _sHead = 0; }
                else Array.Resize(ref _samples, _samples.Length * 2);
            }
            _samples[_sHead + _sCount] = new Sample { Off = off, Ms = ms };
            _sCount++;
            SampleTotal++;
        }

        private void OnPcr(long pcr27, long off, bool discontinuity)
        {
            PcrCount++;
            if (!_havePcr)
            {
                _clockMs = off * 1000.0 / _nominal;
                _havePcr = true;
            }
            else
            {
                double expect = (off - _lastPcrOff) * 1000.0 / Rate();
                long d27 = pcr27 - _lastPcr27;
                if (d27 < 0) d27 += PcrModulus;
                double dms = d27 / 27000.0;
                if (!_forceJump && !discontinuity && dms > 0 && dms <= MaxPcrStepMs) _clockMs += dms;
                else { _clockMs += Math.Max(0, expect); ClockJumps++; }
            }
            _forceJump = false;
            _lastPcr27 = pcr27; _lastPcrOff = off;
            if (_sCount == 0 || (_clockMs - _samples[_sHead + _sCount - 1].Ms >= SampleStepMs && off - _samples[_sHead + _sCount - 1].Off >= MinSampleBytes)) AddSample(off, _clockMs);
        }

        /// <summary>The stream was cut (skipped hole, re-attach): the next PCR is not compared with the previous one and half-finished sections are lost.</summary>
        public void NotifyDiscontinuity()
        {
            _forceJump = true;
            _pat.Abort(); _pmt.Abort();
        }

        // ---------- Random accesses ----------

        private void AddRap(long off, bool strong)
        {
            if (_rCount > 0)
            {
                var last = _raps[_rHead + _rCount - 1];
                if (last.Off >= off) return;
                if (off - last.Off < MinRapBytes)
                {
                    if (strong && !last.Strong) { _raps[_rHead + _rCount - 1] = new Rap { Off = off, Strong = true }; _strongSeen = true; }
                    return;
                }
            }
            if (_rHead + _rCount == _raps.Length)
            {
                if (_rHead > 0) { Array.Copy(_raps, _rHead, _raps, 0, _rCount); _rHead = 0; }
                else Array.Resize(ref _raps, _raps.Length * 2);
            }
            _raps[_rHead + _rCount] = new Rap { Off = off, Strong = strong };
            _rCount++;
            RapTotal++;
            if (strong) _strongSeen = true;
        }

        /// <summary>Last random access point in [minOffset, offset], or -1. Prefers the truly marked ones (RAI/IDR) within maxBackBytes.</summary>
        public long FindRapAtOrBefore(long offset, long minOffset, long maxBackBytes)
        {
            int lo = 0, hi = _rCount - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_raps[_rHead + mid].Off <= offset) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            long weak = -1;
            for (int i = idx, n = 0; i >= 0 && n < MaxScan; i--, n++)
            {
                var r = _raps[_rHead + i];
                if (r.Off < minOffset) break;
                if (r.Strong) return offset - r.Off <= maxBackBytes ? r.Off : weak;
                if (weak < 0) weak = r.Off;
            }
            return weak;
        }

        /// <summary>First random access point in [offset, maxOffset], or -1 (same criterion: the marked ones first).</summary>
        public long FindRapAtOrAfter(long offset, long maxOffset, long maxForwardBytes)
        {
            int lo = 0, hi = _rCount - 1, idx = _rCount;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_raps[_rHead + mid].Off >= offset) { idx = mid; hi = mid - 1; } else lo = mid + 1;
            }
            long weak = -1;
            for (int i = idx, n = 0; i < _rCount && n < MaxScan; i++, n++)
            {
                var r = _raps[_rHead + i];
                if (r.Off > maxOffset) break;
                if (r.Strong) return r.Off - offset <= maxForwardBytes ? r.Off : weak;
                if (weak < 0) weak = r.Off;
            }
            return weak;
        }

        /// <summary>Discards from the index what precedes the offset (one earlier entry is kept so as to be able to interpolate).</summary>
        public void Prune(long oldestOffset)
        {
            while (_sCount > 1 && _samples[_sHead + 1].Off <= oldestOffset) { _sHead++; _sCount--; }
            if (_sHead > 512 && _sHead > _sCount) { Array.Copy(_samples, _sHead, _samples, 0, _sCount); _sHead = 0; }
            while (_rCount > 0 && _raps[_rHead].Off < oldestOffset) { _rHead++; _rCount--; }
            if (_rHead > 512 && _rHead > _rCount) { Array.Copy(_raps, _rHead, _raps, 0, _rCount); _rHead = 0; }
            if (_sCount == 0) _sHead = 0;
            if (_rCount == 0) _rHead = 0;
        }

        // ---------- Packet analysis ----------

        private static bool IsVideoType(int t)
        {
            return t == 0x01 || t == 0x02 || t == 0x10 || t == 0x1B || t == 0x24 || t == 0x42 || t == 0xEA;
        }

        /// <summary>Analyzes the packet starting at d[p] that is going to be stored at offset 'off'.</summary>
        public void ScanPacket(byte[] d, int p, long off)
        {
            int b1 = d[p + 1];
            if ((b1 & 0x80) != 0) return;                                        // transport_error_indicator
            int pid = ((b1 & 0x1F) << 8) | d[p + 2];
            bool pusi = (b1 & 0x40) != 0;
            int afc = (d[p + 3] >> 4) & 3;
            int ps = p + 4;
            int afLen = 0, flags = 0;
            bool hasAf = (afc & 2) != 0;
            if (hasAf)
            {
                afLen = d[p + 4];
                if (afLen > 183) return;
                if (afLen >= 1) flags = d[p + 5];
                ps = p + 5 + afLen;
            }
            bool hasPayload = (afc & 1) != 0 && ps < p + Pkt;

            // PCR (that of the PID declared by the PMT; before it is known, that of the first one that carries it)
            if (hasAf && afLen >= 7 && (flags & 0x10) != 0 && (pid == _pcrPid || (_pcrPid < 0 && !_pcrPidFromPmt)))
            {
                if (_pcrPid < 0) _pcrPid = pid;
                long bas = ((long)d[p + 6] << 25) | ((long)d[p + 7] << 17) | ((long)d[p + 8] << 9) | ((long)d[p + 9] << 1) | (long)(d[p + 10] >> 7);
                int ext = ((d[p + 10] & 1) << 8) | d[p + 11];
                OnPcr(bas * 300 + ext, off, (flags & 0x80) != 0);
            }

            // PSI
            if (pid == 0)
            {
                if (hasPayload && _pat.Feed(d, p, ps, pusi)) OnPat();
            }
            else if (pid == _pmtPid)
            {
                if (hasPayload && _pmt.Feed(d, p, ps, pusi)) OnPmt();
            }

            // random access on the main PID (video; if there is none, audio or the PCR's)
            int type = _pidType[pid];
            bool primary = pid == _primaryPid || (_primaryPid < 0 && pid == _pcrPid);
            if (!primary && !IsVideoType(type)) return;
            bool rai = hasAf && afLen >= 1 && (flags & 0x40) != 0;
            bool idr = pusi && hasPayload && IsVideoType(type) && StartsIdr(d, ps, p + Pkt, type);
            if (rai || idr) { AddRap(off, true); return; }
            if (pusi && !_strongSeen)
            {
                double ms = _havePcr ? _clockMs + (off - _lastPcrOff) * 1000.0 / Rate() : off * 1000.0 / _nominal;
                if (ms - _lastWeakMs >= WeakStepMs) { _lastWeakMs = ms; AddRap(off, false); }
            }
        }

        /// <summary>true if the payload (start of a video PES) carries an IDR or the sequence headers that precede it.</summary>
        private static bool StartsIdr(byte[] d, int s, int e, int streamType)
        {
            if (e - s < 9 || d[s] != 0 || d[s + 1] != 0 || d[s + 2] != 1) return false;
            int es = s + 9 + d[s + 8];                                           // after the PES header
            if (es >= e) return false;
            for (int k = es; k + 3 < e; k++)
            {
                if (d[k] != 0 || d[k + 1] != 0 || d[k + 2] != 1) continue;
                int nal = d[k + 3];
                if (streamType == 0x1B)
                {
                    int t = nal & 0x1F;
                    if (t == 5 || t == 7) return true;                           // IDR or SPS
                    if (t == 1 || t == 2 || t == 3 || t == 4) return false;      // picture slice without headers in front
                }
                else if (streamType == 0x24)
                {
                    int t = (nal >> 1) & 0x3F;
                    if ((t >= 16 && t <= 21) || t == 32 || t == 33) return true; // IRAP, VPS, SPS
                    if (t <= 9) return false;
                }
                else
                {
                    if (nal == 0xB3 || nal == 0xB8) return true;                 // MPEG-2: sequence / GOP header
                    if (nal == 0x00) return false;                               // picture
                }
                k += 2;
            }
            return false;
        }

        // ---------- PSI ----------

        private void OnPat()
        {
            var s = _pat.Section; int len = _pat.SectionLength;
            if (len < 12 || s[0] != 0x00 || (s[5] & 1) == 0) { _pat.Discard(); return; }
            int end = Math.Min(len - 4, 3 + (((s[1] & 0xF) << 8) | s[2]) - 4);
            int pmt = -1;
            for (int i = 8; i + 4 <= end; i += 4)
            {
                int program = (s[i] << 8) | s[i + 1];
                if (program == 0) continue;
                pmt = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                break;
            }
            if (pmt < 0) { _pat.Discard(); return; }
            if (pmt != _pmtPid) { _pmtPid = pmt; _pmt.Discard(); _lastPmtSection = null; }
        }

        private void OnPmt()
        {
            var s = _pmt.Section; int len = _pmt.SectionLength;
            if (len < 16 || s[0] != 0x02 || (s[5] & 1) == 0) { _pmt.Discard(); return; }
            if (_lastPmtSection != null && _lastPmtSection.Length == len)
            {
                bool same = true;
                for (int i = 0; i < len && same; i++) if (_lastPmtSection[i] != s[i]) same = false;
                if (same) return;
            }
            _lastPmtSection = new byte[len];
            Buffer.BlockCopy(s, 0, _lastPmtSection, 0, len);
            int pcr = ((s[8] & 0x1F) << 8) | s[9];
            int pil = ((s[10] & 0xF) << 8) | s[11];
            int end = len - 4;
            Array.Clear(_pidType, 0, _pidType.Length);
            int video = -1, audio = -1;
            for (int i = 12 + pil; i + 5 <= end; )
            {
                int type = s[i];
                int pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                int esLen = ((s[i + 3] & 0xF) << 8) | s[i + 4];
                _pidType[pid] = (byte)type;
                if (IsVideoType(type)) { if (video < 0) video = pid; }
                else if (audio < 0 && (type == 0x03 || type == 0x04 || type == 0x0F || type == 0x11 || type == 0x81)) audio = pid;
                i += 5 + esLen;
            }
            _primaryPid = video >= 0 ? video : audio;
            if (pcr != _pcrPid) { if (_pcrPid >= 0) _forceJump = true; _pcrPid = pcr; }
            _pcrPidFromPmt = true;
        }

        /// <summary>The last complete PAT and PMT (original packets, consecutive), to prepend them to a reader that starts mid-stream. Null if either is missing.</summary>
        public byte[] PsiPrefix()
        {
            if (_pat.DoneCount == 0 || _pmt.DoneCount == 0) return null;
            var r = new byte[(_pat.DoneCount + _pmt.DoneCount) * Pkt];
            Buffer.BlockCopy(_pat.DoneRaw, 0, r, 0, _pat.DoneCount * Pkt);
            Buffer.BlockCopy(_pmt.DoneRaw, 0, r, _pat.DoneCount * Pkt, _pmt.DoneCount * Pkt);
            return r;
        }

        /// <summary>Reassembles a PSI section spread over up to 6 packets (PMTs with many descriptors do not always fit in one).</summary>
        private sealed class Psi
        {
            private const int MaxPackets = 6;
            public readonly byte[] Section = new byte[MaxPackets * 184];
            public int SectionLength;                                            // of the complete section (the one OnPat / OnPmt see)
            private readonly byte[] _cur = new byte[MaxPackets * 184];
            private readonly byte[] _curRaw = new byte[MaxPackets * Pkt];
            private int _curLen, _curPackets, _need;
            private bool _active;
            public readonly byte[] DoneRaw = new byte[MaxPackets * Pkt];
            public int DoneCount;

            public void Abort() { _active = false; }
            public void Discard() { DoneCount = 0; SectionLength = 0; }

            /// <summary>true if this packet completes a section (it stays in Section / SectionLength and its packets are saved as the "last" ones).</summary>
            public bool Feed(byte[] d, int p, int ps, bool pusi)
            {
                int end = p + Pkt;
                if (pusi)
                {
                    int ptr = d[ps];
                    int start = ps + 1 + ptr;
                    if (start >= end) { _active = false; return false; }
                    _curLen = 0; _curPackets = 0; _need = 0; _active = true;
                    Buffer.BlockCopy(d, start, _cur, 0, end - start); _curLen = end - start;
                }
                else
                {
                    if (!_active || _curPackets >= MaxPackets) { _active = false; return false; }
                    int n = end - ps;
                    if (n <= 0 || _curLen + n > _cur.Length) { _active = false; return false; }
                    Buffer.BlockCopy(d, ps, _cur, _curLen, n); _curLen += n;
                }
                Buffer.BlockCopy(d, p, _curRaw, _curPackets * Pkt, Pkt);
                _curPackets++;
                if (_need == 0 && _curLen >= 3) _need = 3 + (((_cur[1] & 0xF) << 8) | _cur[2]);
                if (_need > 0 && _need > _cur.Length) { _active = false; return false; }
                if (_need > 0 && _curLen >= _need)
                {
                    _active = false;
                    Buffer.BlockCopy(_cur, 0, Section, 0, _need);
                    SectionLength = _need;
                    Buffer.BlockCopy(_curRaw, 0, DoneRaw, 0, _curPackets * Pkt);
                    DoneCount = _curPackets;
                    return true;
                }
                return false;
            }
        }
    }
}
