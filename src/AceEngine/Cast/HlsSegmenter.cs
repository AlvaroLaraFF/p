using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Cast
{
    /// <summary>
    /// Live HLS segmenter on top of the live-with-memory buffer. It reads the MPEG-TS the buffer holds, finds the H.264 key frames and publishes segments as
    /// byte ranges of the buffer (nothing is copied until a client asks for one). A segment starts at a key frame (an access unit with an IDR slice) and is
    /// served as PAT + PMT + the packets of the video, the chosen audio and the PCR; audio PES that straddle a cut are moved whole to the segment where they started,
    /// so no segment opens with half an audio frame.
    ///
    /// Cuts: at the key frame that brings the segment closest to <see cref="HlsOptions.TargetSegmentSeconds"/>. Durations come from the video PTS (the next key
    /// frame minus this one) and fall back to the buffer's PCR clock. The timeline restarts (EXT-X-DISCONTINUITY) when the engine cut the input (skipped hole,
    /// re-attach), when the PTS jumps, or when the program changes.
    /// </summary>
    internal sealed class HlsSegmenter : IHlsContent, IDisposable
    {
        private const string Tag = "hls";
        private const int Pkt = 188;
        private const int ScanChunk = Pkt * 512;
        private const long PtsMod = 1L << 33;
        private const double PtsHz = 90000.0;
        private const int AudioRing = 8;

        private readonly TimeshiftBuffer _buf;
        private readonly HlsOptions _o;
        private readonly object _gate = new object();
        private readonly Func<DateTime> _now;
        private readonly TsProgramParser _prog = new TsProgramParser();
        private readonly List<HlsSegment> _segs = new List<HlsSegment>();
        private readonly byte[] _scanBuf = new byte[ScanChunk];
        private readonly List<long> _cuts = new List<long>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _task;
        private bool _disposed, _ended;

        /// <summary>Pulsed whenever a segment is published or the state changes.</summary>
        public readonly AsyncSignal Changed = new AsyncSignal();

        // scan position
        private long _scan = -1;
        private long _cutsKnown;
        private int _cutIdx;
        private string _lastProgSig = "";

        // stream state
        private HlsFormat _fmt;
        private HlsSegment _open, _closing;
        private bool _awaitKey, _nextDisc;
        private double _awaitSinceMs = -1, _lastKeyClockMs = -1;
        private long _nextSeq;
        private double _gopSec;
        private long _lastKeyPts = -1;
        private double _lastKeyClock = -1;
        private readonly long[] _audioRing = new long[AudioRing];
        private int _audioRingCount;

        // access unit being scanned (a PUSI of the video PID)
        private bool _auActive, _auHasPts, _auSps;
        private long _auOff, _auPts;
        private int _auSkip, _auPackets, _zeros;
        private bool _expectNal;

        // playlist state
        private int _targetDuration = 1;
        private long _discRemoved;
        private DateTime? _pdtEnd;

        // statistics
        private long _published, _discontinuities, _scanned, _composed, _composeFailures, _nalFixes;
        private double _lastDuration;
        private bool _failed;
        private string _failReason = "";
        private CastIssueCode _failCode;

        public HlsSegmenter(TimeshiftBuffer buffer, HlsOptions options)
        {
            _buf = buffer;
            _o = options ?? new HlsOptions();
            _now = _o.UtcNow ?? (() => DateTime.UtcNow);
            _awaitKey = true;
        }

        public HlsOptions Options { get { return _o; } }

        // ---------- Running ----------

        /// <summary>Starts following the buffer in the background.</summary>
        public void Start()
        {
            if (_task != null) return;
            _task = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var signal = _buf.Appended.Wait();
                    Process();
                    if (IsEnded) break;
                    var tick = Task.Delay(500, ct);
                    await Task.WhenAny(signal, tick).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Warn(Tag, "segmenter loop: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private bool IsEnded { get { lock (_gate) return _ended || _disposed; } }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            Changed.Pulse();
        }

        /// <summary>Reads whatever the buffer has received since the last call and publishes the segments it completes. Safe to call from any thread.</summary>
        public void Process()
        {
            bool pulse;
            lock (_gate)
            {
                if (_disposed || _ended) return;
                long before = _published;
                bool endedBefore = _ended;
                try { ProcessLocked(); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    AceLog.Warn(Tag, "segmenter: " + ex.GetType().Name + ": " + ex.Message);
                    Fail(CastIssueCode.Other, "internal error: " + ex.Message);
                }
                pulse = _published != before || _ended != endedBefore || _failed;
            }
            if (pulse) Changed.Pulse();
        }

        private void ProcessLocked()
        {
            long write = _buf.WriteOffset;
            long oldest = _buf.OldestOffset;
            if (_scan < 0)
            {
                if (write - oldest < 3 * Pkt) return;
                _scan = StartOffset(oldest, write);
                _cutsKnown = _buf.CutCount;
                _cuts.Clear(); _buf.CutsFrom(_scan, _cuts); _cuts.Sort(); _cutIdx = 0;
                _awaitSinceMs = _buf.MsAt(_scan);
            }
            if (_scan < oldest)
            {
                // the buffer recycled what we had not read yet: a hole in the stream
                AceLog.Warn(Tag, "the segmenter fell behind the buffer; restarting at the oldest data");
                _scan = oldest;
                OnBreak(_scan);
            }
            if (_buf.CutCount != _cutsKnown)
            {
                _cutsKnown = _buf.CutCount;
                _cuts.Clear(); _buf.CutsFrom(_scan, _cuts); _cuts.Sort(); _cutIdx = 0;
            }
            while (_scan + Pkt <= write)
            {
                while (_cutIdx < _cuts.Count && _cuts[_cutIdx] <= _scan)
                {
                    if (_cuts[_cutIdx] == _scan) { _prog.Abort(); OnBreak(_scan); }
                    _cutIdx++;
                }
                long end = Math.Min(write, _scan + ScanChunk);
                if (_cutIdx < _cuts.Count && _cuts[_cutIdx] < end) end = _cuts[_cutIdx];
                int n = (int)(end - _scan);
                n -= n % Pkt;
                if (n <= 0) break;
                if (!_buf.CopyRange(_scan, _scan + n, _scanBuf, 0))
                {
                    _scan = Math.Max(_buf.OldestOffset, _scan);
                    OnBreak(_scan);
                    if (_scan + Pkt > _buf.WriteOffset) break;
                    continue;
                }
                for (int i = 0; i < n; i += Pkt) ScanPacket(_scanBuf, i, _scan + i);
                _scan += n;
                _scanned += n;
            }
            CheckStall(write);
            if (_buf.IsCompleted && _scan + Pkt > _buf.WriteOffset) FinishLocked();
        }

        private long StartOffset(long oldest, long write)
        {
            double edge = _buf.MsAt(write);
            long off = _buf.OffsetAtMs(edge - _o.StartLookbackSeconds * 1000.0);
            if (off < oldest) off = oldest;
            if (off > write) off = write;
            return oldest + (off - oldest) / Pkt * Pkt;
        }

        private void CheckStall(long write)
        {
            double edgeMs = _buf.MsAt(write);
            double limitMs = _o.MaxSegmentSeconds * 1000.0 * 1.5;
            if (_awaitKey && _awaitSinceMs >= 0 && edgeMs - _awaitSinceMs > limitMs)
                Fail(CastIssueCode.NoKeyframes, "no H.264 key frame in " + ((edgeMs - _awaitSinceMs) / 1000).ToString("0", CultureInfo.InvariantCulture) + " s of stream");
            else if (!_awaitKey && _lastKeyClockMs >= 0 && edgeMs - _lastKeyClockMs > limitMs * 1.5)
                Fail(CastIssueCode.NoKeyframes, "no H.264 key frame for " + ((edgeMs - _lastKeyClockMs) / 1000).ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        private void Fail(CastIssueCode code, string reason)
        {
            if (_failed) return;
            _failed = true; _failReason = reason; _failCode = code;
            AceLog.Warn(Tag, "segmenter failed: " + reason);
        }

        private void FinishLocked()
        {
            if (_ended) return;
            _awaitKey = false;
            long write = _buf.WriteOffset;
            var o = _open;
            if (o != null)
            {
                double dur = ContentDuration(o);
                if (dur >= _o.MinSegmentSeconds && write > o.Start) { CloseOpen(o, write, dur); ResolveClosing(write); }
                _open = null;
            }
            if (_closing != null) ResolveClosing(_closing.End);
            _ended = true;
        }

        // ---------- Packet scanning ----------

        private void ScanPacket(byte[] d, int p, long off)
        {
            if (d[p] != 0x47) return;
            int b1 = d[p + 1];
            if ((b1 & 0x80) != 0) return;
            int pid = ((b1 & 0x1F) << 8) | d[p + 2];
            bool pusi = (b1 & 0x40) != 0;
            if (_prog.Wants(pid))
            {
                _prog.FeedPacket(d, p);
                var prog = _prog.Program;
                if (prog != null && prog.Signature != _lastProgSig) OnProgram(prog, off);
                return;
            }
            var f = _fmt;
            if (f == null) return;
            int afc = (d[p + 3] >> 4) & 3;
            int ps = p + 4;
            if ((afc & 2) != 0)
            {
                int afLen = d[p + 4];
                if (afLen > 183) return;
                ps = p + 5 + afLen;
            }
            bool payload = (afc & 1) != 0 && ps < p + Pkt;
            if (pid == f.VideoPid)
            {
                if (!payload) return;
                if (pusi) { DecideAu(); BeginAu(d, ps, p + Pkt, off); }
                else if (_auActive) ScanAu(d, ps, p + Pkt);
            }
            else if (pid == f.AudioPid && pusi && payload) OnAudioStart(off);
        }

        private void OnProgram(ProgramInfo prog, long off)
        {
            _lastProgSig = prog.Signature;
            var v = prog.Video;
            if (v == null) { Fail(CastIssueCode.NoVideo, "the program has no video"); _fmt = null; return; }
            if (v.Kind != EsKind.H264) { Fail(v.Kind == EsKind.Hevc ? CastIssueCode.VideoHevc : v.Kind == EsKind.Mpeg2Video ? CastIssueCode.VideoMpeg2 : CastIssueCode.VideoUnsupported, "the video is not H.264 (" + v.Kind + ")"); _fmt = null; return; }
            var a = CastCompatibility.ChooseAudio(prog);
            var f = new HlsFormat
            {
                ProgramNumber = prog.ProgramNumber, PmtPid = prog.PmtPid, PcrPid = prog.PcrPid, VideoPid = v.Pid, VideoType = v.StreamType,
                AudioPid = a == null ? -1 : a.Pid, AudioType = a == null ? 0 : a.StreamType, AudioDescriptors = a == null ? null : a.Descriptors
            };
            if (f.PcrPid == 0x1FFF || f.PcrPid == 0) f.PcrPid = f.VideoPid;
            f.Signature = f.VideoPid + ":" + f.VideoType + "/" + f.AudioPid + ":" + f.AudioType + "/" + f.PcrPid + "/" + f.PmtPid;
            var old = _fmt;
            _fmt = f;
            if (old != null && old.Signature != f.Signature)
            {
                AceLog.Info(Tag, "the program changed (" + old.Signature + " -> " + f.Signature + "): new timeline");
                OnBreak(off);
            }
        }

        private void OnAudioStart(long off)
        {
            _audioRing[_audioRingCount % AudioRing] = off;
            _audioRingCount++;
            var o = _open;
            if (o != null && o.AudioStart < 0 && off >= o.Start) ResolveOpenAudio(off);
        }

        // ---------- Access units ----------

        private static long Parse33(byte[] d, int p)
        {
            return ((long)(d[p] & 0x0E) << 29) | ((long)d[p + 1] << 22) | ((long)(d[p + 2] & 0xFE) << 14) | ((long)d[p + 3] << 7) | (long)(d[p + 4] >> 1);
        }

        private static long SignedDelta(long a, long b)
        {
            long d = (a - b) & (PtsMod - 1);
            return d >= (PtsMod >> 1) ? d - PtsMod : d;
        }

        private void BeginAu(byte[] d, int ps, int end, long off)
        {
            _auActive = false;
            if (end - ps < 9 || d[ps] != 0 || d[ps + 1] != 0 || d[ps + 2] != 1 || (d[ps + 3] & 0xF0) != 0xE0) return;
            int hdr = d[ps + 8];
            bool hasPts = (d[ps + 7] & 0x80) != 0 && hdr >= 5 && ps + 14 <= end;
            _auOff = off; _auHasPts = hasPts; _auPts = hasPts ? Parse33(d, ps + 9) : 0;
            _auSps = false; _auPackets = 1; _zeros = 0; _expectNal = false; _sliceNeed = 0;
            _auActive = true;
            int dataStart = ps + 9 + hdr;
            _auSkip = Math.Max(0, dataStart - end);
            if (dataStart < end) ScanAu(d, dataStart, end, true);
        }

        private void ScanAu(byte[] d, int from, int to) { ScanAu(d, from, to, false); }

        private void ScanAu(byte[] d, int from, int to, bool first)
        {
            if (!first) _auPackets++;
            if (_auSkip > 0) { int sk = Math.Min(_auSkip, to - from); from += sk; _auSkip -= sk; }
            for (int i = from; i < to; i++)
            {
                byte b = d[i];
                if (_sliceNeed > 0)
                {
                    _sliceBuf[3 - _sliceNeed + 0] = b;
                    if (--_sliceNeed == 0) { Decide(IsIntraSlice(_sliceBuf)); return; }
                    continue;
                }
                if (_expectNal)
                {
                    _expectNal = false;
                    int type = b & 0x1F;
                    if (type == 7) _auSps = true;
                    else if (type == 5) { Decide(true); return; }
                    else if (type == 1 && _auSps) { _sliceNeed = 3; continue; }               // SPS followed by a non-IDR slice: a key frame only if the slice is intra
                    else if (type >= 1 && type <= 4) { Decide(false); return; }              // a slice with no parameter sets in front: SPS/PPS repeated before a P frame do not get here as keys
                    continue;
                }
                if (b == 0) _zeros++;
                else { if (b == 1 && _zeros >= 2) _expectNal = true; _zeros = 0; }
            }
            if (_auPackets >= 6 && _sliceNeed == 0) Decide(_auSps);                          // the first slice is far into the access unit: trust the SPS
        }

        private readonly byte[] _sliceBuf = new byte[3];
        private int _sliceNeed;

        /// <summary>slice_type of a slice header: I (2, 7) and SI (4, 9) slices start with first_mb_in_slice and slice_type, both Exp-Golomb.</summary>
        private static bool IsIntraSlice(byte[] b)
        {
            int bit = 0;
            uint first = ReadUe(b, ref bit);
            uint type = ReadUe(b, ref bit);
            if (bit < 0) return false;
            return type == 2 || type == 4 || type == 7 || type == 9;
        }

        private static uint ReadUe(byte[] b, ref int bit)
        {
            if (bit < 0) return 0;
            int zeros = 0;
            while (true)
            {
                if (bit >= b.Length * 8) { bit = -1; return 0; }
                int v = (b[bit >> 3] >> (7 - (bit & 7))) & 1;
                bit++;
                if (v == 1) break;
                if (++zeros > 16) { bit = -1; return 0; }
            }
            uint r = 0;
            for (int i = 0; i < zeros; i++)
            {
                if (bit >= b.Length * 8) { bit = -1; return 0; }
                r = (r << 1) | (uint)((b[bit >> 3] >> (7 - (bit & 7))) & 1);
                bit++;
            }
            return (1u << zeros) - 1 + r;
        }

        /// <summary>The next access unit starts: the previous one is decided with what was seen (SPS without a slice in sight counts as a key).</summary>
        private void DecideAu()
        {
            if (_auActive) Decide(_sliceNeed > 0 ? false : _auSps);
        }

        private void Decide(bool key)
        {
            if (!_auActive) return;
            _auActive = false;
            if (key) OnKey(_auOff, _auHasPts, _auPts);
            else CommitAu(_auOff, _auHasPts, _auPts);
        }

        /// <summary>A video access unit that is not a key frame: it belongs to the open segment.</summary>
        private void CommitAu(long off, bool hasPts, long pts)
        {
            var o = _open;
            if (o == null) return;
            o.Aus++;
            if (hasPts && o.FirstPts >= 0)
            {
                long d = SignedDelta(pts, o.LastPts);
                if (d < -2 * (long)PtsHz || d > 5 * (long)PtsHz)
                {
                    AceLog.Info(Tag, "PTS jump of " + (d / PtsHz).ToString("0.0", CultureInfo.InvariantCulture) + " s inside a segment: new timeline");
                    OnBreak(off);
                    return;
                }
                o.LastPts = pts;
                long fromFirst = SignedDelta(pts, o.FirstPts);
                if (fromFirst > o.MaxPtsDelta) o.MaxPtsDelta = fromFirst;
            }
            if (o.AudioStart < 0 && (o.MaxPtsDelta > 2.5 * PtsHz || o.Aus > 150)) ResolveOpenAudio(o.Start);   // the audio never showed: take everything
        }

        // ---------- Cutting ----------

        private double ClockMs(long off) { return _buf.MsAt(off); }

        private void OnKey(long x, bool hasPts, long pts)
        {
            double clock = ClockMs(x);
            _lastKeyClockMs = clock;
            var o = _open;
            if (o == null)
            {
                StartOpen(x, hasPts, pts, clock, _nextDisc);
                _nextDisc = false; _awaitKey = false;
                return;
            }
            double elapsed;
            bool disc = false;
            if (hasPts && o.FirstPts >= 0)
            {
                long gap = SignedDelta(pts, o.FirstPts + o.MaxPtsDelta);
                if (gap < -(long)(0.2 * PtsHz) || gap > 3 * (long)PtsHz) disc = true;
                elapsed = SignedDelta(pts, o.FirstPts) / PtsHz;
            }
            else elapsed = (clock - o.StartClockMs) / 1000.0;
            // GOP length seen between consecutive key frames
            double gopNow = 0;
            if (hasPts && _lastKeyPts >= 0) gopNow = SignedDelta(pts, _lastKeyPts) / PtsHz;
            else if (_lastKeyClock >= 0) gopNow = (clock - _lastKeyClock) / 1000.0;
            if (gopNow > 0 && gopNow < 60) _gopSec = gopNow;
            _lastKeyPts = hasPts ? pts : -1; _lastKeyClock = clock;

            if (disc)
            {
                AceLog.Info(Tag, "PTS discontinuity at a key frame: new timeline");
                double dur = ContentDuration(o);
                if (dur >= _o.MinSegmentSeconds) CloseOpen(o, x, dur);
                else DropOpen();
                StartOpen(x, hasPts, pts, clock, true);
                _gopSec = 0;
                return;
            }
            bool cut = elapsed >= _o.MinSegmentSeconds && (elapsed + _gopSec / 2 >= _o.TargetSegmentSeconds || elapsed >= _o.MaxSegmentSeconds);
            if (!cut)
            {
                o.Aus++;                                                                     // this key frame stays in the open segment
                if (hasPts && o.FirstPts >= 0)
                {
                    o.LastPts = pts;
                    long fromFirst = SignedDelta(pts, o.FirstPts);
                    if (fromFirst > o.MaxPtsDelta) o.MaxPtsDelta = fromFirst;
                }
                return;
            }
            CloseOpen(o, x, elapsed);
            StartOpen(x, hasPts, pts, clock, false);
        }

        private void StartOpen(long x, bool hasPts, long pts, double clock, bool discontinuity)
        {
            var f = _fmt;
            var s = new HlsSegment { Start = x, AudioStart = -1, AudioEnd = -1, End = -1, Discontinuity = discontinuity, Format = f, StartClockMs = clock };
            if (hasPts) { s.FirstPts = pts; s.LastPts = pts; }
            s.Aus = 1;
            _open = s;
            if (discontinuity) _discontinuities++;
            if (f.AudioPid < 0) { s.AudioStart = x; if (_closing != null) ResolveClosing(x); return; }
            // an audio PES that started right at (or after) the cut may have been seen already while the key frame was being decided
            long best = -1;
            int n = Math.Min(_audioRingCount, AudioRing);
            for (int k = 0; k < n; k++)
            {
                long a = _audioRing[(_audioRingCount - 1 - k) % AudioRing];
                if (a >= x && (best < 0 || a < best)) best = a;
            }
            if (best >= 0) ResolveOpenAudio(best);
        }

        private void DropOpen()
        {
            _open = null;
            if (_closing != null) ResolveClosing(_closing.End);
        }

        /// <summary>The open segment ends at 'end': it waits (in _closing) for the audio boundary before it is published.</summary>
        private void CloseOpen(HlsSegment o, long end, double duration)
        {
            if (_closing != null) ResolveOpenAudio(o.Start);                                 // the audio boundary of the previous cut never showed: use the cut itself
            if (o.AudioStart < 0) o.AudioStart = o.Start;
            o.End = end;
            o.Duration = Math.Max(0.001, Math.Round(duration, 3));                           // what the playlist prints is what the date chain adds up
            o.EndClockMs = ClockMs(end);
            _closing = o;
            _open = null;
        }

        private void ResolveOpenAudio(long a)
        {
            var o = _open;
            if (o != null && o.AudioStart < 0) o.AudioStart = a;
            if (_closing != null) ResolveClosing(a);
        }

        private void ResolveClosing(long a)
        {
            var c = _closing;
            _closing = null;
            if (c == null) return;
            c.AudioEnd = Math.Max(a, c.AudioStart);
            Publish(c);
        }

        private double ContentDuration(HlsSegment o)
        {
            if (o.FirstPts >= 0 && o.Aus > 1)
            {
                double frame = o.MaxPtsDelta / PtsHz / (o.Aus - 1);
                return o.MaxPtsDelta / PtsHz + Math.Min(frame, 0.5);
            }
            return Math.Max(0, (_buf.MsAt(Math.Max(o.Start, _scan)) - o.StartClockMs) / 1000.0);
        }

        /// <summary>The input was cut at 'off' (or the stream changed): what is open ends there and everything is ignored until the next key frame, which opens a new timeline.</summary>
        private void OnBreak(long off)
        {
            _auActive = false;
            var o = _open;
            if (o != null)
            {
                double dur = ContentDuration(o);
                if (dur >= _o.MinSegmentSeconds && off > o.Start) { CloseOpen(o, off, dur); ResolveClosing(off); }
                else DropOpen();
                _open = null;
            }
            else if (_closing != null) ResolveClosing(_closing.End);
            _awaitKey = true; _nextDisc = true;
            _awaitSinceMs = _buf.MsAt(off);
            _lastKeyPts = -1; _lastKeyClock = -1; _gopSec = 0;
            _audioRingCount = 0;
        }

        // ---------- Publication ----------

        private void Publish(HlsSegment s)
        {
            if (_nextSeq == 0 && s.Discontinuity) { s.Discontinuity = false; if (_discontinuities > 0) _discontinuities--; }   // nothing came before the first segment: there is no timeline to break
            s.Seq = _nextSeq++;
            s.PublishedUtc = _now();
            if (s.Discontinuity || !_pdtEnd.HasValue)
            {
                double behind = Math.Max(0, (_buf.MsAt(_buf.WriteOffset) - s.StartClockMs) / 1000.0);
                var anchor = s.PublishedUtc - TimeSpan.FromSeconds(behind);
                s.ProgramDate = _pdtEnd.HasValue && anchor < _pdtEnd.Value ? _pdtEnd.Value : anchor;
            }
            else s.ProgramDate = _pdtEnd.Value;
            _pdtEnd = s.ProgramDate + TimeSpan.FromSeconds(s.Duration);
            _lastDuration = s.Duration;
            int rounded = (int)Math.Round(s.Duration, MidpointRounding.AwayFromZero);
            if (rounded > _targetDuration) _targetDuration = rounded;
            _segs.Add(s);
            _published++;
            PruneLocked();
        }

        private static long MinOffset(HlsSegment s) { return s.AudioStart >= 0 ? Math.Min(s.Start, s.AudioStart) : s.Start; }

        private void PruneLocked()
        {
            long oldest = _buf.OldestOffset;
            int keep = Math.Max(1, _o.WindowSegments) + Math.Max(0, _o.RetainedSegments);
            while (_segs.Count > 0 && (_segs.Count > keep || MinOffset(_segs[0]) < oldest))
            {
                if (_segs[0].Discontinuity) _discRemoved++;                                  // its tag leaves the playlist with it
                _segs.RemoveAt(0);
            }
        }

        // ---------- Playlist ----------

        private List<HlsSegment> ListedLocked()
        {
            var res = new List<HlsSegment>();
            if (_segs.Count == 0) return res;
            // Listed only while it lies in the newer part of the buffer: ReserveFraction of the capacity in front of the eviction edge is left alone
            long reserve = _buf.WriteOffset - (long)(_buf.CapacityBytes * (1.0 - _o.ReserveFraction));
            int window = Math.Max(1, _o.WindowSegments);
            int first = Math.Max(0, _segs.Count - window);
            // do not list what is about to be recycled, but always list something
            while (first < _segs.Count - 1 && MinOffset(_segs[first]) < reserve) first++;
            for (int i = first; i < _segs.Count; i++) res.Add(_segs[i]);
            return res;
        }

        public string GetPlaylist(out bool ended)
        {
            lock (_gate)
            {
                ended = _ended;
                var listed = ListedLocked();
                if (listed.Count == 0) return null;
                // EXT-X-DISCONTINUITY-SEQUENCE: the discontinuity tags that went away with the segments before the first listed one
                long gone = _discRemoved;
                for (int i = 0; i < _segs.Count && _segs[i].Seq < listed[0].Seq; i++) if (_segs[i].Discontinuity) gone++;
                return HlsPlaylist.Build(listed, gone, _targetDuration, _ended, _o.ProgramDateTime);
            }
        }

        // ---------- Segments ----------

        public SegmentLease GetSegment(long seq)
        {
            HlsSegment seg = null;
            lock (_gate)
            {
                if (_segs.Count > 0)
                {
                    long i = seq - _segs[0].Seq;
                    if (i >= 0 && i < _segs.Count) seg = _segs[(int)i];
                }
            }
            if (seg == null) return null;
            var lease = Compose(seg);
            lock (_gate) { if (lease != null) _composed++; else _composeFailures++; }
            return lease;
        }

        private SegmentLease Compose(HlsSegment seg)
        {
            long scanEnd = Math.Max(seg.End, seg.AudioEnd);
            long raw = scanEnd - seg.Start;
            if (raw <= 0 || raw > _o.MaxSegmentBytes) return null;
            const int Head = 2 * Pkt;
            var buf = SegmentPool.Rent((int)raw + Head);
            if (!_buf.CopyRange(seg.Start, scanEnd, buf, Head)) { SegmentPool.Return(buf); return null; }
            var f = seg.Format;
            int w = Head, limit = Head + (int)raw;
            for (int r = Head; r + Pkt <= limit; r += Pkt)
            {
                if (buf[r] != 0x47 || (buf[r + 1] & 0x80) != 0) continue;
                int pid = ((buf[r + 1] & 0x1F) << 8) | buf[r + 2];
                long abs = seg.Start + (r - Head);
                bool keep;
                if (pid == f.VideoPid) keep = abs < seg.End;
                else if (pid == f.AudioPid) keep = abs >= seg.AudioStart && abs < seg.AudioEnd;
                else if (pid == f.PcrPid) keep = abs < seg.End;
                else keep = false;
                if (!keep) continue;
                if (w != r) Buffer.BlockCopy(buf, r, buf, w, Pkt);
                w += Pkt;
            }
            if (_o.NormalizeNalOrder)
            {
                int fixedAus = AnnexBFix.NormalizeParameterSets(buf, Head, w, f.VideoPid);
                if (fixedAus > 0) lock (_gate) _nalFixes += fixedAus;
            }
            int cc = (int)(seg.Seq & 0xF);
            TsPsiWriter.WritePat(buf, 0, f, cc);
            TsPsiWriter.WritePmt(buf, Pkt, f, cc);
            return new SegmentLease(buf, w);
        }

        // ---------- Statistics ----------

        public bool Failed { get { lock (_gate) return _failed; } }
        public string FailureReason { get { lock (_gate) return _failReason; } }
        public CastIssueCode FailureCode { get { lock (_gate) return _failCode; } }
        public bool Ended { get { lock (_gate) return _ended; } }

        /// <summary>Segments in the playlist right now and the media time they cover.</summary>
        public void GetWindow(out int segments, out double seconds)
        {
            lock (_gate)
            {
                var l = ListedLocked();
                segments = l.Count;
                seconds = 0;
                foreach (var s in l) seconds += s.Duration;
            }
        }

        public HlsStats GetStats()
        {
            lock (_gate)
            {
                var l = ListedLocked();
                var st = new HlsStats
                {
                    SegmentsPublished = _published, SegmentsListed = l.Count, Discontinuities = _discontinuities, LastSegmentSeconds = _lastDuration,
                    TargetSeconds = _o.TargetSegmentSeconds, ScannedBytes = _scanned, ComposedSegments = _composed, ComposeFailures = _composeFailures, NalOrderFixes = _nalFixes,
                    Ended = _ended, Failed = _failed, FailureReason = _failReason, FailureCode = _failCode, TargetDuration = _targetDuration
                };
                foreach (var s in l) st.WindowSeconds += s.Duration;
                if (l.Count > 0)
                {
                    st.FirstSeq = l[0].Seq; st.LastSeq = l[l.Count - 1].Seq;
                    st.PlaylistBehindEdgeSeconds = Math.Max(0, (_buf.MsAt(_buf.WriteOffset) - l[l.Count - 1].EndClockMs) / 1000.0);
                }
                return st;
            }
        }
    }
}
