using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Streaming;

namespace AceEngine.Live
{
    /// <summary>Parameters of the live-with-memory circular buffer.</summary>
    internal sealed class TimeshiftOptions
    {
        /// <summary>Maximum data memory (the occupancy never exceeds it; the index and the readers are separate and bounded).</summary>
        public long BudgetBytes = 64L * 1024 * 1024;
        /// <summary>Block size (rounded to a multiple of 188). Blocks are requested on demand, one by one, and afterwards only reused.</summary>
        public int BlockBytes = 256 * 1024;
        /// <summary>Bytes per second of the descriptor: fallback clock when the stream carries no PCR.</summary>
        public double NominalBytesPerSecond = 262144;
        /// <summary>Delay of "live" relative to the buffer edge (the one the engine uses when attaching: live_buffer_time).</summary>
        public double LiveDelayMs = 10000;
        /// <summary>The most we go back from the requested point to align to a random access (if none is marked closer, the requested point is used).</summary>
        public double MaxRapDistanceMs = 12000;
    }

    /// <summary>Snapshot of the state of the live-with-memory for the playback session and the UI.</summary>
    public sealed class TimeshiftSnapshot
    {
        public bool Enabled;
        /// <summary>What is stored behind the live point.</summary>
        public TimeSpan Window;
        /// <summary>Everything stored (the window plus the live delay).</summary>
        public TimeSpan Buffered;
        public TimeSpan LiveDelay;
        /// <summary>Synthetic media clock of the buffer (ms): oldest data, live point and edge.</summary>
        public double OldestMs, LiveMs, EdgeMs;
        public long BufferedBytes, BudgetBytes, EvictedBytes, AllocatedBlocks;
        public int OpenReaders;
        /// <summary>The stream has ended (session stopped).</summary>
        public bool Completed;
        // ---- last opened reader (the player's) ----
        public long ReaderId;
        public bool ReaderOpen, ReaderEvicted;
        /// <summary>Distance to the live point of the first (media) byte served to it, when it was opened; negative if it started ahead.</summary>
        public double ReaderStartBehindMs;
        /// <summary>Media time (buffer clock) of the first media byte served to it.</summary>
        public double ReaderStartMs;
        /// <summary>Media time of its cursor (what has already been served) and the distance of that to the live point.</summary>
        public double ReaderCursorMs, ReaderBehindMs;
        public long ReaderDelivered;
    }

    /// <summary>
    /// In-RAM circular buffer of the already aligned MPEG-TS stream of a live stream (live with memory). One producer (the session's
    /// pump) and several readers with their own cursor. Data goes in fixed blocks that are reused when the oldest is evicted, so once it fills
    /// there are no more allocations. Only whole TS packets are stored (a packet cut by a hole is discarded), so that any
    /// offset that is a multiple of 188 is a packet boundary. It keeps the time index (PCR), the random-access index and the last PAT/PMT.
    /// </summary>
    internal sealed class TimeshiftBuffer : IDisposable
    {
        private const int Pkt = 188;
        private readonly object _gate = new object();
        private readonly int _bs;
        private readonly int _maxBlocks;
        private readonly byte[][] _blocks;
        private readonly TimeshiftOptions _o;
        private readonly TsStreamIndex _index;
        private long _writeOffset;
        private long _firstBlock;
        private long _allocated, _recycled, _bytesWritten, _droppedBytes, _lostSync;
        private bool _completed, _raw;
        private volatile bool _disposed;

        // packet reassembly and sync
        private readonly byte[] _carry = new byte[Pkt];
        private int _carryLen;
        private bool _synced = true;
        private readonly byte[] _rbuf = new byte[4 * Pkt];
        private readonly byte[] _rtmp = new byte[4 * Pkt];
        private int _rlen;

        private int _openReaders;
        private long _readerSeq;
        private TimeshiftReader _newest;

        // offsets at which the input stream was cut (Discontinuity), for consumers that need to know (the HLS segmenter); bounded
        private const int MaxCuts = 256;
        private readonly System.Collections.Generic.List<long> _cuts = new System.Collections.Generic.List<long>();
        private long _cutCount;

        /// <summary>Pulsed with every new piece of data and when the buffer ends or is released.</summary>
        public readonly AsyncSignal Appended = new AsyncSignal();

        public TimeshiftBuffer(TimeshiftOptions options)
        {
            _o = options;
            int bs = Math.Max(Pkt, (options.BlockBytes / Pkt) * Pkt);
            long quarter = options.BudgetBytes / 4 / Pkt * Pkt;                  // with small budgets the blocks shrink so that at least four fit
            if (quarter >= Pkt && quarter < bs) bs = (int)quarter;
            _bs = bs;
            _maxBlocks = (int)Math.Max(2, Math.Min(int.MaxValue / 2, options.BudgetBytes / _bs));
            _blocks = new byte[_maxBlocks][];
            _index = new TsStreamIndex(options.NominalBytesPerSecond);
        }

        public int BlockBytes { get { return _bs; } }
        public int MaxBlocks { get { return _maxBlocks; } }
        /// <summary>Maximum data memory it will end up occupying (blocks * block size).</summary>
        public long CapacityBytes { get { return (long)_maxBlocks * _bs; } }
        public long AllocatedBlocks { get { lock (_gate) return _allocated; } }
        public long RecycledBlocks { get { lock (_gate) return _recycled; } }
        public long WriteOffset { get { lock (_gate) return _writeOffset; } }
        public long OldestOffset { get { lock (_gate) return OldestLocked(); } }
        public long DroppedBytes { get { lock (_gate) return _droppedBytes; } }
        public long LostSyncCount { get { lock (_gate) return _lostSync; } }
        public int IndexSamples { get { lock (_gate) return _index.SampleCount; } }
        public int IndexAccessPoints { get { lock (_gate) return _index.RapCount; } }
        public bool HasPsi { get { lock (_gate) return _index.HasPsi; } }
        public bool IsRaw { get { lock (_gate) return _raw; } }
        public int OpenReaders { get { return Volatile.Read(ref _openReaders); } }
        internal bool IsDisposed { get { return _disposed; } }

        private long OldestLocked() { return _firstBlock * _bs; }

        // ---------- Producer ----------

        /// <summary>Adds a stream already aligned to TS packets (in chunks of any size). Whatever is not valid TS is discarded until sync is recovered.</summary>
        public void Append(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            lock (_gate)
            {
                if (_disposed || _completed) return;
                if (_raw) StoreLocked(data, offset, count);
                else AppendTsLocked(data, offset, offset + count);
            }
            Appended.Pulse();
        }

        /// <summary>The input stream has a cut (skipped hole, engine re-attach): the half packet is thrown away and the relative clock is restarted.</summary>
        public void Discontinuity()
        {
            lock (_gate)
            {
                if (_disposed || _completed) return;
                _droppedBytes += _carryLen;
                _carryLen = 0; _rlen = 0;
                _index.NotifyDiscontinuity();
                _cutCount++;
                if (_cuts.Count == 0 || _cuts[_cuts.Count - 1] != _writeOffset) _cuts.Add(_writeOffset);
                if (_cuts.Count > MaxCuts) _cuts.RemoveAt(0);
            }
        }

        /// <summary>The content is not MPEG-TS (the engine delivers it as is): it is stored without analysis, without index and with the bitrate clock.</summary>
        public void SetRawMode()
        {
            lock (_gate)
            {
                if (_raw) return;
                _raw = true;
                _carryLen = 0; _rlen = 0;
            }
        }

        /// <summary>No more stream will come in: the readers finish when they drain what is stored.</summary>
        public void Complete()
        {
            lock (_gate) { if (_completed) return; _completed = true; }
            Appended.Pulse();
        }

        private void AppendTsLocked(byte[] d, int i, int end)
        {
            while (i < end)
            {
                if (!_synced) { ResyncLocked(d, i, end); return; }
                if (_carryLen > 0)
                {
                    int take = Math.Min(Pkt - _carryLen, end - i);
                    Buffer.BlockCopy(d, i, _carry, _carryLen, take);
                    _carryLen += take; i += take;
                    if (_carryLen < Pkt) return;
                    _carryLen = 0;
                    if (_carry[0] != 0x47)
                    {
                        _synced = false; _lostSync++;
                        Buffer.BlockCopy(_carry, 0, _rbuf, 0, Pkt); _rlen = Pkt;
                        ResyncLocked(d, i, end);
                        return;
                    }
                    _index.ScanPacket(_carry, 0, _writeOffset);
                    StoreLocked(_carry, 0, Pkt);
                    continue;
                }
                int p = i;
                while (p + Pkt <= end && d[p] == 0x47) { _index.ScanPacket(d, p, _writeOffset + (p - i)); p += Pkt; }
                if (p > i) { StoreLocked(d, i, p - i); i = p; }
                if (i >= end) return;
                if (end - i < Pkt && d[i] == 0x47)
                {
                    Buffer.BlockCopy(d, i, _carry, 0, end - i);                    // a packet cut by the end of the chunk: it is completed with the next one
                    _carryLen = end - i;
                    return;
                }
                _synced = false; _lostSync++;                                      // does not start with 0x47: sync is searched (3 consecutive packets)
            }
        }

        private void ResyncLocked(byte[] d, int i, int end)
        {
            while (i < end)
            {
                int take = Math.Min(_rbuf.Length - _rlen, end - i);
                Buffer.BlockCopy(d, i, _rbuf, _rlen, take);
                _rlen += take; i += take;
                int p = TsAligner.FindSync(_rbuf, _rlen);
                if (p >= 0)
                {
                    int len = _rlen - p;
                    _droppedBytes += p;
                    Buffer.BlockCopy(_rbuf, p, _rtmp, 0, len);
                    _rlen = 0; _synced = true;
                    AppendTsLocked(_rtmp, 0, len);
                    if (i < end) AppendTsLocked(d, i, end);
                    return;
                }
                if (_rlen >= _rbuf.Length)
                {
                    int keep = 2 * Pkt;                                            // what precedes can no longer be the start of a valid sequence
                    _droppedBytes += _rlen - keep;
                    Buffer.BlockCopy(_rbuf, _rlen - keep, _rbuf, 0, keep);
                    _rlen = keep;
                }
            }
        }

        private void StoreLocked(byte[] d, int off, int cnt)
        {
            while (cnt > 0)
            {
                long wb = _writeOffset / _bs;
                int inner = (int)(_writeOffset - wb * _bs);
                int slot = (int)(wb % _maxBlocks);
                if (inner == 0)
                {
                    if (wb - _firstBlock >= _maxBlocks) { _firstBlock = wb - _maxBlocks + 1; _recycled++; }
                    if (_blocks[slot] == null) { _blocks[slot] = new byte[_bs]; _allocated++; }
                }
                int take = Math.Min(cnt, _bs - inner);
                Buffer.BlockCopy(d, off, _blocks[slot], inner, take);
                _writeOffset += take; _bytesWritten += take;
                off += take; cnt -= take;
            }
            _index.Prune(OldestLocked());
        }

        // ---------- State ----------

        /// <summary>Snapshot of the state, with the data of the last opened reader (the player's).</summary>
        public TimeshiftSnapshot Snapshot()
        {
            lock (_gate)
            {
                var s = new TimeshiftSnapshot { Enabled = true, BudgetBytes = _o.BudgetBytes, AllocatedBlocks = _allocated, OpenReaders = _openReaders, Completed = _completed };
                s.LiveDelay = TimeSpan.FromMilliseconds(_o.LiveDelayMs);
                long oldest = OldestLocked();
                s.BufferedBytes = _writeOffset - oldest;
                s.EvictedBytes = oldest;
                if (_writeOffset > 0)
                {
                    s.OldestMs = _index.MsAt(oldest);
                    s.EdgeMs = _index.MsAt(_writeOffset);
                    s.LiveMs = s.EdgeMs - _o.LiveDelayMs;
                    s.Buffered = TimeSpan.FromMilliseconds(Math.Max(0, s.EdgeMs - s.OldestMs));
                    s.Window = TimeSpan.FromMilliseconds(Math.Max(0, s.LiveMs - s.OldestMs));
                }
                var r = _newest;
                if (r != null)
                {
                    s.ReaderId = r.Id;
                    s.ReaderOpen = !r.IsClosed;
                    s.ReaderEvicted = r.Cursor < oldest;
                    s.ReaderStartBehindMs = r.StartBehindMs;
                    s.ReaderStartMs = r.StartMs;
                    s.ReaderDelivered = r.Delivered;
                    long cur = Math.Max(r.Cursor, oldest);
                    s.ReaderCursorMs = _index.MsAt(Math.Min(cur, _writeOffset));
                    s.ReaderBehindMs = s.LiveMs - s.ReaderCursorMs;
                }
                return s;
            }
        }

        // ---------- Readers ----------

        /// <summary>
        /// Opens a reader with its own cursor. Without 'behindLive' it starts at the live point (edge minus the startup delay) aligned to the first
        /// following random access; with 'behindLive' it starts that much time before the live point (clamped to what is stored), aligned to the
        /// previous random access. Prepends the last PAT/PMT when the stream does not start at the first stored byte.
        /// </summary>
        public TimeshiftReader OpenReader(TimeSpan? behindLive)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("TimeshiftBuffer");
                long oldest = OldestLocked();
                long start = oldest;
                if (_writeOffset > 0)
                {
                    double edgeMs = _index.MsAt(_writeOffset);
                    double liveMs = edgeMs - _o.LiveDelayMs;
                    // When rewinding we do not start glued to the oldest data: eviction goes on while the player prepares and would take the cursor away
                    // before the first read. One block is left free (0.5 s at 4 Mbit/s) or, if little is stored, a quarter.
                    long floor = oldest + Math.Min((long)_bs, (_writeOffset - oldest) / 4 / Pkt * Pkt);
                    long liveOff = Math.Max(oldest, Math.Min(_writeOffset, _index.OffsetAt(liveMs)));
                    if (floor > liveOff) floor = liveOff;                                     // without a window there is nothing to reserve
                    double targetMs = liveMs;
                    if (behindLive.HasValue) targetMs = liveMs - Math.Max(0, behindLive.Value.TotalMilliseconds);
                    double floorMs = _index.MsAt(floor);
                    if (behindLive.HasValue && behindLive.Value > TimeSpan.Zero && targetMs < floorMs) targetMs = floorMs;
                    long target = _index.OffsetAt(targetMs);
                    if (target < oldest) target = oldest;
                    if (target > _writeOffset) target = _writeOffset;
                    if (behindLive.HasValue && behindLive.Value > TimeSpan.Zero && target < floor) target = floor;
                    long rapDist = (long)(_o.MaxRapDistanceMs * RateLocked() / 1000.0);
                    long chosen = -1;
                    if (!_raw)
                    {
                        if (behindLive.HasValue && behindLive.Value > TimeSpan.Zero)
                        {
                            chosen = _index.FindRapAtOrBefore(target, floor, rapDist);
                            if (chosen < 0) chosen = _index.FindRapAtOrAfter(target, _writeOffset, rapDist);
                        }
                        else chosen = _index.FindRapAtOrAfter(target, _writeOffset, rapDist);
                        if (chosen < 0) chosen = target;
                        chosen = oldest + ((chosen - oldest) / Pkt) * Pkt;         // packet boundary (the initial block always is one)
                    }
                    else chosen = target;
                    start = chosen;
                }
                return MakeReaderLocked(start);
            }
        }

        /// <summary>Reader with its own cursor starting at an absolute offset: clamped to what is stored and aligned down to a packet boundary (it does not look for random accesses).</summary>
        public TimeshiftReader OpenReaderAt(long offset)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("TimeshiftBuffer");
                long oldest = OldestLocked();
                long start = Math.Max(oldest, Math.Min(_writeOffset, offset));
                if (!_raw) start = oldest + ((start - oldest) / Pkt) * Pkt;
                return MakeReaderLocked(start);
            }
        }

        private TimeshiftReader MakeReaderLocked(long start)
        {
            long oldest = OldestLocked();
            double liveMs = 0, startMs = 0;
            if (_writeOffset > 0)
            {
                liveMs = _index.MsAt(_writeOffset) - _o.LiveDelayMs;
                startMs = _index.MsAt(start);
            }
            byte[] prefix = null;
            if (!_raw && start > oldest) prefix = _index.PsiPrefix();
            var r = new TimeshiftReader(this, ++_readerSeq, start, prefix, startMs, _writeOffset > 0 ? liveMs - startMs : 0);
            _newest = r;
            Interlocked.Increment(ref _openReaders);
            return r;
        }

        private double RateLocked()
        {
            // bytes per second: what is measured in the index if possible, otherwise the nominal one (asked from the index through two points of the buffer)
            long oldest = OldestLocked();
            if (_writeOffset - oldest < Pkt) return _o.NominalBytesPerSecond;
            double ms = _index.MsAt(_writeOffset) - _index.MsAt(oldest);
            return ms >= 1000 ? (_writeOffset - oldest) * 1000.0 / ms : _o.NominalBytesPerSecond;
        }

        internal void ReaderClosed()
        {
            Interlocked.Decrement(ref _openReaders);
        }

        /// <summary>Copies up to count bytes from the cursor; returns how many (0 = none yet, -1 = the cursor was evicted or the stream ended).</summary>
        internal int ReadAt(ref long cursor, byte[] buf, int off, int count, out bool evicted)
        {
            evicted = false;
            lock (_gate)
            {
                if (_disposed) return -1;
                long oldest = OldestLocked();
                if (cursor < oldest) { evicted = true; return -1; }
                long avail = _writeOffset - cursor;
                if (avail <= 0) return _completed ? -1 : 0;
                int total = (int)Math.Min(count, avail);
                int done = 0;
                while (done < total)
                {
                    long b = cursor / _bs;
                    int inner = (int)(cursor - b * _bs);
                    int take = Math.Min(total - done, _bs - inner);
                    Buffer.BlockCopy(_blocks[(int)(b % _maxBlocks)], inner, buf, off + done, take);
                    cursor += take; done += take;
                }
                return done;
            }
        }

        // ---------- Consumers that work with absolute offsets (HLS segmenter) ----------

        /// <summary>true once the input has ended (Complete).</summary>
        internal bool IsCompleted { get { lock (_gate) return _completed; } }

        /// <summary>How many cuts the input stream has had (Discontinuity calls), monotonic.</summary>
        internal long CutCount { get { lock (_gate) return _cutCount; } }

        /// <summary>Adds to 'into' the absolute offsets of the recent cuts of the input that are at or after 'from' (the packet at that offset is the first after the cut).</summary>
        internal void CutsFrom(long from, System.Collections.Generic.List<long> into)
        {
            lock (_gate) foreach (var c in _cuts) if (c >= from) into.Add(c);
        }

        /// <summary>
        /// Copies [start, end) into dst at dstOff in one step. false (and nothing usable in dst) if the range is not entirely stored any more or not yet written.
        /// Done under the lock so a segment is never a mix of old and recycled blocks.
        /// </summary>
        internal bool CopyRange(long start, long end, byte[] dst, int dstOff)
        {
            if (end < start || dstOff < 0 || dst.Length - dstOff < end - start) return false;
            lock (_gate)
            {
                if (_disposed || start < OldestLocked() || end > _writeOffset) return false;
                long cursor = start;
                int done = 0, total = (int)(end - start);
                while (done < total)
                {
                    long b = cursor / _bs;
                    int inner = (int)(cursor - b * _bs);
                    int take = Math.Min(total - done, _bs - inner);
                    Buffer.BlockCopy(_blocks[(int)(b % _maxBlocks)], inner, dst, dstOff + done, take);
                    cursor += take; done += take;
                }
                return true;
            }
        }

        /// <summary>Offset of the stream at a media time of the buffer clock (inverse of MsAt, extrapolated outside the samples).</summary>
        internal long OffsetAtMs(double ms) { lock (_gate) return _index.OffsetAt(ms); }

        // ---------- Tests and diagnostics ----------

        /// <summary>Media time (ms of the buffer clock) at an offset.</summary>
        internal double MsAt(long offset) { lock (_gate) return _index.MsAt(offset); }

        /// <summary>Copies up to count bytes from an absolute offset without opening a reader (tests). -1 if it is no longer there.</summary>
        internal int Peek(long offset, byte[] buf, int off, int count)
        {
            long c = offset; bool ev;
            int n = ReadAt(ref c, buf, off, count, out ev);
            return n;
        }

        internal TsStreamIndex IndexForTests { get { return _index; } }
        internal object GateForTests { get { return _gate; } }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                for (int i = 0; i < _blocks.Length; i++) _blocks[i] = null;
            }
            Appended.Pulse();
        }
    }

    /// <summary>Reader of a TimeshiftBuffer: own cursor, without blocking the producer or other readers.</summary>
    internal sealed class TimeshiftReader : IDisposable
    {
        private readonly TimeshiftBuffer _buf;
        private readonly byte[] _prefix;
        private int _prefixPos;
        private long _cursor;
        private long _delivered;
        private int _closed;
        private bool _evicted;

        internal TimeshiftReader(TimeshiftBuffer buf, long id, long start, byte[] prefix, double startMs, double startBehindMs)
        {
            _buf = buf; Id = id; StartOffset = start; _cursor = start; _prefix = prefix; StartMs = startMs; StartBehindMs = startBehindMs;
        }

        public long Id { get; private set; }
        /// <summary>Absolute offset of the first byte of the stream (after the prepended PAT/PMT).</summary>
        public long StartOffset { get; private set; }
        public double StartMs { get; private set; }
        public double StartBehindMs { get; private set; }
        internal long Cursor { get { return Volatile.Read(ref _cursor); } }
        /// <summary>Bytes delivered (includes the prepended PAT/PMT).</summary>
        public long Delivered { get { return Interlocked.Read(ref _delivered); } }
        /// <summary>The cursor was evicted by the buffer (the reader will finish).</summary>
        public bool Evicted { get { return _evicted; } }
        public bool IsClosed { get { return Volatile.Read(ref _closed) != 0; } }
        public int PrefixLength { get { return _prefix == null ? 0 : _prefix.Length; } }

        /// <summary>>0 = bytes copied; 0 = no data yet; -1 = end (closed, evicted or stream ended).</summary>
        public int TryRead(byte[] buf, int off, int count)
        {
            if (count <= 0) return 0;
            if (IsClosed || _buf.IsDisposed) return -1;
            if (_prefix != null && _prefixPos < _prefix.Length)
            {
                int n = Math.Min(count, _prefix.Length - _prefixPos);
                Buffer.BlockCopy(_prefix, _prefixPos, buf, off, n);
                _prefixPos += n;
                Interlocked.Add(ref _delivered, n);
                return n;
            }
            long c = _cursor;
            bool ev;
            int r = _buf.ReadAt(ref c, buf, off, count, out ev);
            if (r > 0)
            {
                Volatile.Write(ref _cursor, c);
                Interlocked.Add(ref _delivered, r);
            }
            else if (ev) _evicted = true;
            return r;
        }

        /// <summary>Reads waiting for data. Returns 0 at the end (closed, evicted or end of the stream). Cancelable.</summary>
        public async Task<int> ReadAsync(byte[] buf, int off, int count, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var signal = _buf.Appended.Wait();                    // captured BEFORE checking: wakeups are not lost
                int r = TryRead(buf, off, count);
                if (r > 0) return r;
                if (r < 0) return 0;
                await _buf.Appended.WaitAsync(signal, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _buf.ReaderClosed();
        }
    }
}
