using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;

namespace AceEngine.Tests.App
{
    /// <summary>
    /// The simplest player that keeps the IPlayerBackend contract, with no network: a clock that runs while playing and stops while paused, an optional duration (VOD) with
    /// seeking, and the three events. It is the reference the contract tests are written against and a stand-in for tests that do not need real bytes.
    /// URLs: anything containing "unreachable" fails with a recoverable PlayerFailure, "unsupported" with a fatal one.
    /// </summary>
    internal sealed class InMemoryPlayerBackend : IPlayerBackend
    {
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private long _basePos;                       // position when the clock was last restarted
        private bool _started, _paused, _disposed;
        private long _duration = -1;

        /// <summary>Duration given to VOD playbacks (ms).</summary>
        public long VodDurationMs = 90 * 60 * 1000;
        public int PrepareDelayMs = 50;

        public event Action<PlayerFailure> Failed;
        public event Action Completed;
        public event Action<int> Buffering;

        public long PositionMs
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed || !_started) return -1;
                    long p = _basePos + (_paused ? 0 : _clock.ElapsedMilliseconds);
                    return _duration > 0 ? Math.Min(p, _duration) : p;
                }
            }
        }

        public long DurationMs { get { lock (_gate) return _started ? _duration : -1; } }
        public string StreamInfo { get { return _started ? "in-memory video / audio" : ""; } }

        public async Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct)
        {
            if (url != null && url.Contains("unsupported")) throw new PlayerFailure("unsupported format", true);
            if (url != null && url.Contains("unreachable")) throw new PlayerFailure("cannot connect");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token))       // disposing while preparing ends the preparation
            {
                if (PrepareDelayMs > 0) await Task.Delay(PrepareDelayMs, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            lock (_gate)
            {
                if (_disposed) throw new OperationCanceledException();
                _duration = isLive ? -1 : VodDurationMs;
                _started = true; _basePos = 0; _clock.Restart();
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_disposed || !_started || _paused) return;
                _basePos += _clock.ElapsedMilliseconds; _clock.Reset(); _paused = true;
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_disposed || !_started || !_paused) return;
                _paused = false; _clock.Restart();
            }
        }

        public Task SeekAsync(long positionMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || !_started || _duration <= 0) return Task.CompletedTask;       // nothing to seek (live)
                _basePos = Math.Max(0, Math.Min(positionMs, _duration));
                if (!_paused) _clock.Restart();
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; _clock.Stop(); }
            _disposeCts.Cancel();
        }

        // the three events, for the tests (never after Dispose)
        public void RaiseFailure(PlayerFailure f) { var h = Failed; if (h != null && !_disposed) h(f); }
        public void RaiseCompleted() { var h = Completed; if (h != null && !_disposed) h(); }
        public void RaiseBuffering(int percent) { var h = Buffering; if (h != null && !_disposed) h(percent); }
    }
}
