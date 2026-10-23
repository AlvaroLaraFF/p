using System;

namespace PUi.Motion
{
    /// <summary>
    /// Clock of the decoration that repeats non-stop: the focus's fluid border (one turn every 7 s), the strip between recents and categories (16 s), the live dot with pulse (1.8 s).
    /// While the remote is in use it advances like real time (the decoration is exactly the design's). After CalmAfter seconds untouched it SLOWS DOWN gradually, along a
    /// smooth curve, until it stops completely at RestAfter seconds, and the frame rate drops with the speed (at half speed, half the frames: each step of the border stays
    /// just as short, so it does not look jerky); stopped, the interface stops drawing. A key brings it back to normal speed instantly.
    /// It only counts for the decorative parts: the loading indicators (spinner, skeleton shimmer, lamp, buffer ring) follow real time.
    /// </summary>
    public sealed class DecorClock
    {
        /// <summary>Seconds without touching the remote after which the decoration starts to slow down, and at which it is stopped.</summary>
        public double CalmAfter = 10, RestAfter = 30;
        /// <summary>Below this relative speed it is considered stopped (the border turns less than 2 degrees per second): no frames.</summary>
        public const double StopBelow = 0.04;

        private double _value, _last = double.NaN, _rate = 1;

        /// <summary>true when someone has been advancing it (the application); otherwise whoever draws uses the real time it is given (states sheets, tests).</summary>
        public bool Driven { get { return !double.IsNaN(_last); } }

        /// <summary>Decoration time in seconds: what the fluid border, the strip and the pulse use instead of real time.</summary>
        public double Value { get { return _value; } }
        /// <summary>Current speed relative to real time: 1 in use, 0 stopped.</summary>
        public double Rate { get { return _rate; } }
        /// <summary>true if the decoration is stopped (no frame is needed for it).</summary>
        public bool Stopped { get { return _rate < StopBelow; } }

        /// <summary>Relative speed after 'quietSeconds' without touching the remote: 1 until CalmAfter, smooth curve (cosine) down to 0 at RestAfter, 0 afterwards.</summary>
        public double RateFor(double quietSeconds)
        {
            if (quietSeconds <= CalmAfter) return 1;
            if (quietSeconds >= RestAfter) return 0;
            double u = (quietSeconds - CalmAfter) / (RestAfter - CalmAfter);
            return 0.5 * (1 + Math.Cos(Math.PI * u));
        }

        /// <summary>
        /// Advances the clock up to 'now' (the interface's clock, in seconds) with the speed that applies after 'quietSeconds' without touching the remote. It can be called several times per
        /// frame; a 'now' that does not advance does nothing.
        /// </summary>
        public void Advance(double now, double quietSeconds)
        {
            _rate = RateFor(quietSeconds);
            if (double.IsNaN(_last)) { _last = now; _value = now; return; }       // the first time it matches real time: the phases are the design's
            double dt = now - _last;
            if (dt <= 0) return;
            _last = now;
            _value += dt * _rate;
        }
    }
}
