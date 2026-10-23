using System;

namespace PUi.Core
{
    /// <summary>
    /// Decides when a channel "counts" as watched to enter Recents: it has to play for more than 5 minutes IN A ROW (real playback).
    /// Only the time with the video playing adds up. An interruption (pause, buffer refill, retry) does not add up but does not reset either if it lasts
    /// less than 60 s; if it lasts longer, or there is an error, the counter goes back to zero. Switching channel creates a new counter. Seeking back or forward does not reset.
    /// </summary>
    public sealed class WatchTracker
    {
        public const double ThresholdSeconds = 300.0;
        public const double MaxGapSeconds = 60.0;

        private double _last = -1, _watched, _gap;
        private PlayerMode _prev = PlayerMode.Loading;

        public double WatchedSeconds { get { return _watched; } }
        /// <summary>The threshold has been reached (only once per counter).</summary>
        public bool Qualified { get; private set; }

        public void Reset() { _last = -1; _watched = 0; _gap = 0; Qualified = false; _prev = PlayerMode.Loading; }

        /// <summary>Advances the counter up to 'now' with the player mode. Returns true the first time the threshold is reached.</summary>
        public bool Update(PlayerMode mode, double now)
        {
            if (_last < 0) { _last = now; _prev = mode; return false; }
            double dt = Math.Max(0, Math.Min(now - _last, 5.0));        // if the app was paused, it is not counted all at once
            _last = now;
            if (Qualified) return false;
            if (mode == PlayerMode.Error) { _watched = 0; _gap = 0; _prev = mode; return false; }
            if (mode == PlayerMode.Playing) { _watched += dt; _gap = 0; }
            else { _gap += dt; if (_gap > MaxGapSeconds) { _watched = 0; _gap = 0; } }
            _prev = mode;
            if (_watched > ThresholdSeconds) { Qualified = true; return true; }
            return false;
        }
    }
}
