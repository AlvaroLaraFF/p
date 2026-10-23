using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Live
{
    /// <summary>
    /// Live session: implements the session contract. No length; the reader delivers continuous MPEG-TS starting about 10 s behind
    /// live. Only one reader is active: opening another replaces the previous one and re-attaches to live (the server reopens the URL
    /// after a player error).
    /// </summary>
    public sealed class LiveSession : IStreamSession, ITimeshiftSession, ISessionHold
    {
        private readonly LiveSwarm _swarm;
        private readonly Action<LiveSession> _onDispose;
        private int _disposed;
        private SessionState _lastState = SessionState.Resolving;
        private readonly TimeshiftBuffer _ts;                 // null = no live with memory (LiveOptions.TimeshiftBytes = 0)
        private readonly TimeshiftPump _pump;

        internal LiveSession(LiveSwarm swarm, Action<LiveSession> onDispose, long? timeshiftBytes = null)
        {
            _swarm = swarm;
            _onDispose = onDispose;
            Id = LocalFileSession.NewId();
            swarm.PhaseChanged += OnPhaseChanged;
            var lo = swarm.Options.Live;
            long budget = timeshiftBytes ?? lo.TimeshiftBytes;
            if (budget > 0)
            {
                // Live with memory: the pump is the swarm's only reader and the HTTP readers read from the circular buffer
                _ts = new TimeshiftBuffer(new TimeshiftOptions
                {
                    BudgetBytes = budget, BlockBytes = lo.TimeshiftBlockBytes,
                    NominalBytesPerSecond = swarm.Descriptor.Bitrate, LiveDelayMs = lo.BufferSeconds * 1000.0
                });
                _pump = new TimeshiftPump(swarm, _ts);
                _pump.Start();
            }
        }

        private volatile bool _hold;

        /// <summary>The local server does not stop this session for inactivity while it is true (the playback session sets it during a pause).</summary>
        public bool HoldOpen { get { return _hold; } set { _hold = value; } }

        /// <summary>true if this session keeps what was already served (pause, rewind, +/-10 s).</summary>
        public bool TimeshiftEnabled { get { return _ts != null; } }

        /// <summary>Window, delay and cursor of the player of the live with memory (Enabled = false if disabled).</summary>
        public TimeshiftSnapshot GetTimeshift()
        {
            var t = _ts;
            if (t == null || Volatile.Read(ref _disposed) != 0) return new TimeshiftSnapshot();
            return t.Snapshot();
        }

        internal TimeshiftBuffer TimeshiftBuffer { get { return _ts; } }

        public string Id { get; private set; }
        public bool IsLive { get { return true; } }
        public long? Length { get { return null; } }
        public string ContentType { get { return "video/mp2t"; } }
        public string Name { get { return _swarm.Descriptor.Name; } }
        public Transport.LiveDescriptor Descriptor { get { return _swarm.Descriptor; } }

        public SessionState State
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return SessionState.Stopped;
                switch (_swarm.Phase)
                {
                    case LivePhase.Resolving: return SessionState.Resolving;
                    case LivePhase.Connecting: return SessionState.Connecting;
                    case LivePhase.Buffering: return SessionState.Buffering;
                    case LivePhase.Playing: return SessionState.Playing;
                    default: return SessionState.Stopped;
                }
            }
        }

        public SessionStats Stats { get { return _swarm.GetStats(); } }
        public LiveStats LiveInfo { get { return _swarm.GetLiveStats(); } }
        public string DebugInfo { get { return _swarm.DebugInfo(); } }

        /// <summary>Connection funnel with the peers and observed live position (diagnostics and liveness criterion).</summary>
        public LiveFunnel Funnel { get { return _swarm.GetFunnel(); } }

        public event EventHandler<SessionState> StateChanged;

        private void OnPhaseChanged(LivePhase phase)
        {
            var st = State;
            var h = StateChanged;
            if (h != null && st != _lastState)
            {
                _lastState = st;
                h(this, st);
            }
        }

        public Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct)
        {
            return OpenReaderBehindAsync(null, ct);
        }

        /// <summary>With memory: reader of the circular buffer (at the live point, or behind). Without it: the swarm's single reader as always.</summary>
        public Task<IStreamReader> OpenReaderBehindAsync(TimeSpan? behindLive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException("LiveSession");
            var ts = _ts;
            if (ts != null) return Task.FromResult<IStreamReader>(new TimeshiftStreamReader(ts.OpenReader(behindLive)));
            var rs = _swarm.OpenReader();
            return Task.FromResult<IStreamReader>(new LiveReader(_swarm, rs));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_pump != null) _pump.Dispose(); } catch { }
            try { _swarm.Dispose(); } catch (Exception ex) { AceLog.Warn("session", "live swarm Dispose: " + ex.Message); }
            try { if (_ts != null) _ts.Dispose(); } catch { }
            var h = StateChanged;
            if (h != null) { try { h(this, SessionState.Stopped); } catch { } }
            if (_onDispose != null) _onDispose(this);
        }

        /// <summary>HTTP reader of the live with memory: own cursor in the circular buffer; it ends (0) if the cursor is evicted.</summary>
        private sealed class TimeshiftStreamReader : IStreamReader
        {
            private readonly TimeshiftReader _r;
            public TimeshiftStreamReader(TimeshiftReader r) { _r = r; }
            public long Position { get { return _r.Delivered; } }
            public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) { return count <= 0 ? Task.FromResult(0) : _r.ReadAsync(buffer, offset, count, ct); }
            public void Dispose() { _r.Dispose(); }
        }

        private sealed class LiveReader : IStreamReader
        {
            private readonly LiveSwarm _swarm;
            private readonly LiveReaderState _rs;
            private volatile bool _disposed;

            public LiveReader(LiveSwarm swarm, LiveReaderState rs) { _swarm = swarm; _rs = rs; }

            public long Position { get { return _rs.BytesDelivered; } }

            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (count <= 0) return 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_disposed) throw new ObjectDisposedException("LiveReader");
                    var signal = _swarm.Changed.Wait();                  // captured BEFORE checking: wakeups are not lost
                    int n;
                    var st = _swarm.TryRead(_rs, buffer, offset, count, out n);
                    if (st == LiveReadResult.Data) return n;
                    if (st == LiveReadResult.Stopped) return 0;
                    // Wait: woken by every new piece, and at most every 250 ms in case the gap policy changes
                    var tick = Task.Delay(250, ct);
                    await Task.WhenAny(signal, tick).ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _swarm.CloseReader(_rs);
            }
        }
    }
}
