using System;

namespace PUi.Motion
{
    /// <summary>CSS cubic-bezier easing (x1,y1,x2,y2) sampled into a lookup table.</summary>
    public sealed class Bezier
    {
        readonly float[] _lut = new float[257];
        public Bezier(double x1, double y1, double x2, double y2)
        {
            // Build y(x) by sampling t densely, then invert by interpolation.
            var xs = new double[2049]; var ys = new double[2049];
            for (int i = 0; i <= 2048; i++)
            {
                double t = i / 2048.0, u = 1 - t;
                xs[i] = 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t;
                ys[i] = 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t;
            }
            int j = 0;
            for (int i = 0; i <= 256; i++)
            {
                double x = i / 256.0;
                while (j < 2048 && xs[j + 1] < x) j++;
                double span = xs[Math.Min(j + 1, 2048)] - xs[j];
                double f = span > 1e-9 ? (x - xs[j]) / span : 0;
                _lut[i] = (float)(ys[j] + (ys[Math.Min(j + 1, 2048)] - ys[j]) * f);
            }
        }
        public float this[double x]
        {
            get
            {
                if (x <= 0) return 0; if (x >= 1) return 1;
                double p = x * 256; int i = (int)p; float f = (float)(p - i);
                return _lut[i] + (_lut[i + 1] - _lut[i]) * f;
            }
        }
        public static readonly Bezier Ease = new Bezier(0.25, 0.1, 0.25, 1);
        public static readonly Bezier EaseInOut = new Bezier(0.42, 0, 0.58, 1);
        public static readonly Bezier Pop = new Bezier(0.2, 0.8, 0.2, 1);
        public static readonly Bezier Material = new Bezier(0.4, 0, 0.2, 1);
        public static readonly Bezier Linear = new Bezier(0, 0, 1, 1);
        public static readonly Bezier Smooth = new Bezier(0.45, 0, 0.55, 1);
    }

    /// <summary>A value that eases from its current position to a new target (like a CSS transition).</summary>
    public sealed class Anim
    {
        float _from, _to; double _t0, _dur; readonly Bezier _e;
        public Anim(float v, Bezier e) { _from = _to = v; _e = e; }
        public float Target => _to;
        /// <summary>Jumps to the value without animating.</summary>
        public void Snap(float v) { _from = _to = v; _dur = 0; }
        public Bezier Easing { get { return _e; } }
        public void Set(float to, double now, double dur)
        {
            if (Math.Abs(to - _to) < 1e-5) return;
            _from = Value(now); _to = to; _t0 = now; _dur = dur;
        }
        public float Value(double now)
        {
            if (_dur <= 0 || now >= _t0 + _dur) return _to;
            if (now <= _t0) return _from;
            return _from + (_to - _from) * _e[(now - _t0) / _dur];
        }
        public bool Active(double now) { return _dur > 0 && now < _t0 + _dur; }
    }

    public struct Keyframe { public float T, X, Y, S; public Keyframe(float t, float x, float y, float s) { T = t; X = x; Y = y; S = s; } }

    public static class Keyframes
    {
        public static readonly Keyframe[] Amb = {
            new Keyframe(0f, 0, 0, 1f), new Keyframe(.25f, 182, -78, 1.13f), new Keyframe(.5f, 78, 117, 1.05f), new Keyframe(.75f, -117, 39, 1.16f), new Keyframe(1f, 0, 0, 1f) };
        public static readonly Keyframe[] Amb2 = {
            new Keyframe(0f, 0, 0, 1.08f), new Keyframe(.3f, -169, 91, 1f), new Keyframe(.6f, -52, -117, 1.16f), new Keyframe(.85f, 104, -26, 1.04f), new Keyframe(1f, 0, 0, 1.08f) };

        public static void Sample(Keyframe[] k, double t01, out float x, out float y, out float s)
        {
            for (int i = 0; i < k.Length - 1; i++)
            {
                if (t01 >= k[i].T && t01 < k[i + 1].T)
                {
                    float p = (float)((t01 - k[i].T) / (k[i + 1].T - k[i].T));
                    float e = Bezier.EaseInOut[p];
                    x = k[i].X + (k[i + 1].X - k[i].X) * e; y = k[i].Y + (k[i + 1].Y - k[i].Y) * e; s = k[i].S + (k[i + 1].S - k[i].S) * e;
                    return;
                }
            }
            x = k[0].X; y = k[0].Y; s = k[0].S;
        }
    }
}
