using System;
using System.Diagnostics;

namespace AceEngine.Util
{
    /// <summary>
    /// Token bucket to limit upload (bytes per second, with a burst of at most one second). Shared by all engine sessions
    /// so the limit is global. TryTake does not block: if there are no tokens, the caller drops the request (the peer will repeat it
    /// or ask another one).
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly object _gate = new object();
        private readonly long _rate;
        private double _tokens;
        private long _last;
        private long _granted, _denied;

        public RateLimiter(long bytesPerSecond)
        {
            _rate = Math.Max(1, bytesPerSecond);
            _tokens = Math.Min(_rate, 64 * 1024);
            _last = Stopwatch.GetTimestamp();
        }

        public long Rate { get { return _rate; } }
        public long Granted { get { lock (_gate) return _granted; } }
        public long Denied { get { lock (_gate) return _denied; } }

        public bool TryTake(int bytes)
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                double dt = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                _tokens = Math.Min(Math.Max(_rate, bytes), _tokens + dt * _rate);
                if (_tokens < bytes) { _denied += bytes; return false; }
                _tokens -= bytes;
                _granted += bytes;
                return true;
            }
        }
    }
}
