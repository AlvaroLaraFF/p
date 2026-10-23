using System;
using AceEngine.Live;

namespace AceEngine.App
{
    /// <summary>
    /// Estimates how far behind live what the user sees is. It is not known exactly: the TV player
    /// has its own buffer and the loopback sockets hold several seconds, so the delivered cursor is ahead of the picture.
    /// That is why the picture is modeled: it starts at the first byte that was served to the connection (media time of the buffer), advances in real time while
    /// the player is playing (not paused or loading) and never goes past the delivered cursor. The live point is the buffer's, smoothed between
    /// pieces (they arrive in bursts of a second or more). The expected error is ~2 s; the hysteresis avoids flickering between "live" and "-0:02".
    /// It is a pure class: time comes in as a parameter.
    /// </summary>
    internal sealed class BehindLiveTracker
    {
        /// <summary>Below this it counts as "at live" (and SeekBehindLive forward goes back to live).</summary>
        public const double LiveMarginMs = 2000;
        /// <summary>While "at live" it only switches to "behind" above this (hysteresis: between 2 and 4 s the state does not change).</summary>
        public const double EnterBehindMs = 4000;
        /// <summary>The smoothed live point goes between the buffer's and the buffer's plus this (pieces arrive in bursts).</summary>
        public const double LiveSlackMs = 1500;

        private readonly object _l = new object();
        private readonly double _rate;                       // media seconds per real second (1 on the TV; the tests with the accelerated sender raise it)
        private bool _started, _advancing, _atLive = true;
        private long _readerId;
        private double _playheadMs, _liveMs, _windowMs;
        private long _lastNowMs;
        private double _rawBehindMs, _reportedMs;

        public BehindLiveTracker(double mediaRate = 1.0) { _rate = mediaRate > 0 ? mediaRate : 1.0; }

        public long ReaderId { get { lock (_l) return _readerId; } }
        public bool Started { get { lock (_l) return _started; } }
        /// <summary>Distance from live with hysteresis (0 = at live): what is shown.</summary>
        public double ReportedMs { get { lock (_l) return _reportedMs; } }
        /// <summary>Distance without hysteresis.</summary>
        public double RawMs { get { lock (_l) return _rawBehindMs; } }
        public bool AtLive { get { lock (_l) return _atLive; } }

        public void Reset()
        {
            lock (_l) { _started = false; _advancing = false; _readerId = 0; _rawBehindMs = 0; _reportedMs = 0; _atLive = true; }
        }

        /// <summary>
        /// A new connection of the player: the picture starts at 'startMs' (first media byte served to it) minus the latency with which the
        /// player starts showing. atLive = live was requested (no rewind): it starts in the "at live" state.
        /// </summary>
        public void Start(long readerId, double startMs, double liveMs, bool atLive, long nowMs, double playerLatencyMs, bool advancing = true)
        {
            lock (_l)
            {
                _started = true; _readerId = readerId;
                _playheadMs = startMs - playerLatencyMs;
                _liveMs = liveMs; _lastNowMs = nowMs; _advancing = advancing;
                _atLive = atLive;
                RecomputeLocked(double.NaN);
                if (atLive) _reportedMs = 0;
            }
        }

        /// <summary>true while the picture advances in real time (playing); false when paused or loading.</summary>
        public void SetAdvancing(bool advancing, long nowMs)
        {
            lock (_l)
            {
                if (!_started) return;
                IntegrateLocked(nowMs);
                _advancing = advancing;
            }
        }

        /// <summary>Brings the model up to date with the buffer state. Returns the distance with hysteresis.</summary>
        public double Update(long nowMs, double bufferLiveMs, double bufferWindowMs, long cursorReaderId, double cursorMs)
        {
            lock (_l)
            {
                if (!_started) return 0;
                double dt = IntegrateLocked(nowMs);
                // smoothed live point: advances in real time within [buffer live, buffer live + slack]
                double live = _liveMs + dt * _rate;
                if (live < bufferLiveMs) live = bufferLiveMs;
                if (live > bufferLiveMs + LiveSlackMs) live = bufferLiveMs + LiveSlackMs;
                _liveMs = live;
                _windowMs = bufferWindowMs;
                RecomputeLocked(cursorReaderId == _readerId ? cursorMs : double.NaN);
                return _reportedMs;
            }
        }

        /// <summary>Distance (without hysteresis) at this instant without changing the state: for deciding on resume.</summary>
        public double PeekRawMs(long nowMs, double bufferLiveMs, long cursorReaderId, double cursorMs)
        {
            lock (_l)
            {
                if (!_started) return 0;
                double dt = _advancing ? Math.Max(0, nowMs - _lastNowMs) * _rate : 0;
                double playhead = _playheadMs + dt;
                if (cursorReaderId == _readerId && playhead > cursorMs) playhead = cursorMs;
                double live = Math.Max(bufferLiveMs, _liveMs + Math.Max(0, nowMs - _lastNowMs) * _rate);
                if (live > bufferLiveMs + LiveSlackMs) live = bufferLiveMs + LiveSlackMs;
                return Math.Max(0, live - playhead);
            }
        }

        private double IntegrateLocked(long nowMs)
        {
            double dt = Math.Max(0, nowMs - _lastNowMs);
            _lastNowMs = nowMs;
            if (_advancing) _playheadMs += dt * _rate;
            return dt;
        }

        private void RecomputeLocked(double cursorMs)
        {
            if (!double.IsNaN(cursorMs) && _playheadMs > cursorMs) _playheadMs = cursorMs;       // the picture cannot be ahead of what was served
            _rawBehindMs = Math.Max(0, _liveMs - _playheadMs);
            if (_atLive) { if (_rawBehindMs > EnterBehindMs) _atLive = false; }
            else if (_rawBehindMs < LiveMarginMs) _atLive = true;
            _reportedMs = _atLive ? 0 : _rawBehindMs;
        }
    }
}
