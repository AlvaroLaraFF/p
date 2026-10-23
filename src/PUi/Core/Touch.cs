using System;
using System.Collections.Generic;
using PUi.Platform;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>
    /// A place that reacts to a finger, in screen (logical) coordinates: the box as it is DRAWN, what a tap on it does, and where the focus goes. The hit zone is not stored: it is
    /// the box made at least as large as a fingertip needs (TouchSizes.MinTargetLogical) when the box is smaller, so the drawing never changes to make a button easier to hit.
    /// </summary>
    public sealed class TouchTarget
    {
        public string Id = "";
        /// <summary>The drawn box, in screen coordinates (the scroll already applied).</summary>
        public SKRect Rect;
        /// <summary>The part of the screen where the target can be touched at all (a list under a fixed header is not touchable under the header); null = everywhere.</summary>
        public SKRect? Clip;
        /// <summary>Corner radius of the pressed highlight.</summary>
        public float Radius = 24;
        /// <summary>Replaces the zone that would be worked out from Rect (a half of a pill that is as tall as its row, for instance).</summary>
        public SKRect? ZoneOverride;
        /// <summary>The element that takes the focus when the target is tapped (the tap is "focus + OK"); null = none.</summary>
        public Focusable Focus;
        /// <summary>What a tap does. Null = the screen's own "focus + OK" on Focus.</summary>
        public Action Tap;
        /// <summary>Highlight while the finger is down.</summary>
        public bool Press = true;
        /// <summary>When boxes overlap, the higher layer wins (a keyboard over the page, the halves of a selector over their row).</summary>
        public int Layer;

        public float Area { get { return Rect.Width * Rect.Height; } }
        public override string ToString() { return Id + " " + Rect.Left + "," + Rect.Top + " " + Rect.Width + "x" + Rect.Height; }
    }

    /// <summary>Physical sizes for touch, worked out from the screen's density and the canvas scale (ScreenMetrics).</summary>
    public static class TouchSizes
    {
        /// <summary>Smallest comfortable touch target, in millimetres.</summary>
        public const float MinMm = 7f;
        /// <summary>One density-independent unit (dp, 1/160 inch) in millimetres.</summary>
        public const float MmPerDp = 25.4f / 160f;

        /// <summary>The smallest side of a touch zone, in LOGICAL units: 7 mm on this screen.</summary>
        public static float MinTargetLogical(ScreenMetrics m) { return PhysicalToLogical(m, MinMm); }

        /// <summary>How far a finger may wander and still be a tap (8 dp), in logical units.</summary>
        public static float Slop(ScreenMetrics m) { return Math.Max(6f, PhysicalToLogical(m, 8 * MmPerDp)); }

        public static float PhysicalToLogical(ScreenMetrics m, float mm)
        {
            float s = m.Scale <= 0 ? 1 : m.Scale;
            return mm / MmPerDp * m.Density / s;
        }

        public static float LogicalToMm(ScreenMetrics m, float logical)
        {
            float d = m.Density <= 0 ? 1 : m.Density;
            return logical * m.Scale / d * MmPerDp;
        }
    }

    /// <summary>Which target a finger is on.</summary>
    public static class TouchHit
    {
        /// <summary>
        /// The zone of a target: its box, grown on EVERY side by what it lacks to be min wide and min tall (a 2.4 mm chip grows 4.6 mm each way), kept inside bounds and inside its Clip.
        /// Growing on every side, not just evenly around the center, means that a box with a neighbour hard against one side still reaches min through the other. Where zones overlap the
        /// nearest drawn box wins (see Find), so the growth only takes space that no box needs.
        /// </summary>
        public static SKRect Zone(TouchTarget t, float min, SKRect bounds)
        {
            SKRect z;
            if (t.ZoneOverride.HasValue) z = t.ZoneOverride.Value;
            else
            {
                var r = t.Rect;
                float gw = Math.Max(0, min - r.Width), gh = Math.Max(0, min - r.Height);
                z = new SKRect(r.Left - gw, r.Top - gh, r.Right + gw, r.Bottom + gh);
            }
            z = Intersect(z, bounds);
            if (t.Clip.HasValue) z = Intersect(z, t.Clip.Value);
            return z;
        }

        /// <summary>
        /// The target for a touch at (x, y). Every target whose DRAWN box or whose zone contains the point is a candidate. The highest layer wins; within a layer a box that is drawn
        /// there beats a zone that merely reaches there (the smaller box if boxes overlap). Between zones the empty space is shared in proportion to what each target lacks to be
        /// min wide and tall (a 2 mm chip, which lacks a lot, gets most of the gap between the big boxes around it; two buttons that lack the same split the gap in half): the
        /// target whose distance, divided by what it lacks, is smaller wins. So zones only ever take space that no box of their layer needs.
        /// </summary>
        public static TouchTarget Find(IReadOnlyList<TouchTarget> targets, float x, float y, float min, SKRect bounds)
        {
            TouchTarget best = null; bool bestExact = false; float bestScore = 0, bestArea = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                bool exact = t.Rect.Contains(x, y) && (!t.Clip.HasValue || t.Clip.Value.Contains(x, y));
                if (!exact)
                {
                    if (!t.ZoneOverride.HasValue && (x < t.Rect.Left - min || x > t.Rect.Right + min || y < t.Rect.Top - min || y > t.Rect.Bottom + min)) continue;       // too far to be in any zone
                    if (!Zone(t, min, bounds).Contains(x, y)) continue;
                }
                float dist = exact ? 0 : Distance(t.Rect, x, y), area = t.Area;
                float lack = Math.Max(1f, min - Math.Min(t.Rect.Width, t.Rect.Height)), score = dist / lack;
                bool better;
                if (best == null) better = true;
                else if (t.Layer != best.Layer) better = t.Layer > best.Layer;
                else if (exact != bestExact) better = exact;
                else if (exact) better = area <= bestArea;
                else if (Math.Abs(score - bestScore) > 0.0005f) better = score < bestScore;
                else better = area < bestArea;
                if (better) { best = t; bestExact = exact; bestScore = score; bestArea = area; }
            }
            return best;
        }

        public static float Distance(SKRect r, float x, float y)
        {
            float dx = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0, dy = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static SKRect Intersect(SKRect a, SKRect b)
        {
            float l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top), r = Math.Min(a.Right, b.Right), bt = Math.Min(a.Bottom, b.Bottom);
            return r < l || bt < t ? new SKRect(l, t, l, t) : new SKRect(l, t, r, bt);
        }

        public static SKRect Offset(SKRect r, float dx, float dy) { return new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy); }
    }

    /// <summary>Speed of a finger from its last few positions (units per second), for the flick that follows a drag.</summary>
    public sealed class VelocityTracker
    {
        private const int N = 8;
        private const double Window = 0.12, StaleAfter = 0.09;
        private readonly double[] _t = new double[N];
        private readonly float[] _p = new float[N];
        private int _count, _head;

        public void Reset() { _count = 0; _head = 0; }

        public void Add(double time, float position)
        {
            if (_count > 0 && time <= _t[(_head + N - 1) % N]) { _p[(_head + N - 1) % N] = position; return; }     // same instant: the latest position wins
            _t[_head] = time; _p[_head] = position; _head = (_head + 1) % N; if (_count < N) _count++;
        }

        /// <summary>Speed at the moment of lifting the finger; 0 if it had stopped (the last sample is older than StaleAfter) or there are no two samples.</summary>
        public float Velocity(double now, float max = 7000f)
        {
            if (_count < 2) return 0;
            int last = (_head + N - 1) % N;
            if (now - _t[last] > StaleAfter) return 0;
            int first = last;
            for (int i = 1; i < _count; i++)
            {
                int k = (_head + N - 1 - i) % N;
                if (_t[last] - _t[k] > Window) break;
                first = k;
            }
            double dt = _t[last] - _t[first];
            if (dt < 0.008) return 0;
            float v = (float)((_p[last] - _p[first]) / dt);
            return Math.Max(-max, Math.Min(max, v));
        }
    }

    /// <summary>
    /// One axis of touch scrolling: a position that follows the finger while it is down (pulled with a growing resistance past the ends), keeps going with friction when the finger is
    /// lifted at speed (inertia) and, past an end, comes back with a critically damped spring (a soft rebound with no wobble). Integrated in fixed steps from the time given to Advance,
    /// so the result does not depend on the frame rate.
    /// </summary>
    public sealed class Inertia
    {
        /// <summary>The resting range. Max below Min means "does not scroll".</summary>
        public float Min, Max;
        /// <summary>Exponential slowdown of the flick, per second.</summary>
        public float Friction = 3.3f;
        /// <summary>Spring of the rebound, rad/s (critically damped).</summary>
        public float Omega = 17f;
        /// <summary>The furthest the position can be pulled past an end by the finger, in the same units.</summary>
        public float MaxOverscroll = 150f;

        private const float Step = 1f / 240f;
        private float _pos, _vel, _raw;
        private double _t;
        private bool _dragging, _moving;

        public float Position { get { return _pos; } }
        public float Velocity { get { return _vel; } }
        public bool Dragging { get { return _dragging; } }
        /// <summary>Moving by itself after a flick or rebounding (the finger is up).</summary>
        public bool Coasting { get { return _moving; } }
        public bool Active { get { return _dragging || _moving; } }
        /// <summary>The position is outside the resting range (pulled or rebounding).</summary>
        public bool Overscrolled { get { return _pos < Min - 0.01f || _pos > UpperBound + 0.01f; } }
        private float UpperBound { get { return Math.Max(Min, Max); } }

        /// <summary>The finger is down and starts to drag from this position.</summary>
        public void Begin(float position, double now)
        {
            _pos = position; _raw = position; _vel = 0; _dragging = true; _moving = false; _t = now;
        }

        /// <summary>The finger moved: the position changes by delta (with resistance past the ends).</summary>
        public void DragBy(float delta)
        {
            if (!_dragging) return;
            _raw += delta;
            float hi = UpperBound;
            _pos = _raw < Min ? Min - Soft(Min - _raw) : _raw > hi ? hi + Soft(_raw - hi) : _raw;
        }

        private float Soft(float over) { return MaxOverscroll * (1f - (float)Math.Exp(-over / MaxOverscroll)); }

        /// <summary>The finger was lifted at this speed (units per second; 0 to stop). Starts the coasting if there is something to do.</summary>
        public void Release(float velocity, double now)
        {
            if (!_dragging) return;
            _dragging = false; _t = now;
            _vel = Math.Abs(velocity) < 25f ? 0 : velocity;
            _moving = _vel != 0 || Overscrolled;
        }

        /// <summary>Stops everything where it is (a finger came down on a moving list).</summary>
        public void Halt(double now)
        {
            _dragging = false; _moving = false; _vel = 0; _t = now;
        }

        /// <summary>Puts the position somewhere without any movement.</summary>
        public void Snap(float position, double now) { _pos = position; _raw = position; _vel = 0; _dragging = false; _moving = false; _t = now; }

        /// <summary>Advances the coasting up to 'now'. Does nothing while the finger drives the position.</summary>
        public void Advance(double now)
        {
            if (!_moving) { if (now > _t) _t = now; return; }
            float decay = (float)Math.Exp(-Friction * Step), hi = UpperBound;
            int guard = 0;
            while (_moving && _t + Step <= now && guard++ < 8000)
            {
                _t += Step;
                if (_pos < Min || _pos > hi)
                {
                    // past an end: critically damped spring toward it, keeping the speed the flick arrived with
                    float over = _pos < Min ? _pos - Min : _pos - hi;
                    float a = -Omega * Omega * over - 2f * Omega * _vel;
                    _vel += a * Step; _pos += _vel * Step;
                    float over2 = _pos < Min ? _pos - Min : _pos > hi ? _pos - hi : 0;
                    if (Math.Abs(over2) < 0.3f && Math.Abs(_vel) < 12f) Rest();
                }
                else
                {
                    _vel *= decay; _pos += _vel * Step;
                    if (Math.Abs(_vel) < 12f && _pos >= Min && _pos <= hi) Rest();
                }
            }
            if (!_moving && now > _t) _t = now;
        }

        private void Rest()
        {
            _moving = false; _vel = 0;
            float hi = UpperBound;
            if (_pos < Min) _pos = Min; else if (_pos > hi) _pos = hi;
            _raw = _pos;
        }
    }

    /// <summary>A strip that scrolls sideways by touch (the recent channels, the recent searches): where a drag moves it, and how to read and write its position.</summary>
    public sealed class HStrip
    {
        public readonly Inertia Phys = new Inertia();
        /// <summary>Screen box where a horizontal drag scrolls the strip.</summary>
        public Func<SKRect> Region;
        /// <summary>Largest position (0 = does not scroll).</summary>
        public Func<float> Max;
        /// <summary>Current position, in the strip's units (pixels).</summary>
        public Func<double, float> Get;
        /// <summary>Sets the position (no animation: the finger or the inertia is driving it).</summary>
        public Action<float> Set;
    }
}
