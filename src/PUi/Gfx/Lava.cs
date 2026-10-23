using System;
using PUi.Motion;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Lava lamp of the channel loading (design PlayerLoading): 6 large organic-shaped blobs (border-radius that morphs every 10-15 s)
    /// that translate and rotate very slowly (30-42 s) with "screen" blending, blurred (70 px) and with saturation at 140 %. It is composed at 1/8 resolution
    /// (it is a very soft image) and stretched: the blur costs almost nothing.
    /// </summary>
    public sealed class Lava : IDisposable
    {
        private sealed class Blob
        {
            public float L, T, W, H, Opacity; public int Motion; public double Dur, Delay; public SKColor Color; public double MorphDur, MorphDelay;
        }

        // border-radius morph (a% b% c% d% / e% f% g% h%): TL.x TR.x BR.x BL.x / TL.y TR.y BR.y BL.y
        private static readonly float[][] Morph =
        {
            new[] { 42f, 58, 63, 37, 41, 44, 56, 59 }, new[] { 63f, 37, 45, 55, 58, 62, 38, 42 },
            new[] { 30f, 70, 34, 66, 64, 32, 68, 36 }, new[] { 56f, 44, 72, 28, 33, 61, 39, 67 }, new[] { 42f, 58, 63, 37, 41, 44, 56, 59 }
        };
        private static readonly float[] MorphT = { 0, .25f, .5f, .75f, 1 };
        private static readonly Keyframe[][] Moves =
        {
            new[] { new Keyframe(0, -160, 420, 0), new Keyframe(.25f, 260, 80, 35), new Keyframe(.5f, 720, -360, 80), new Keyframe(.75f, 300, -60, 130), new Keyframe(1, -160, 420, 0) },   // lv-a (S = rotation in degrees)
            new[] { new Keyframe(0, 220, -380, 0), new Keyframe(.3f, -420, -60, -40), new Keyframe(.6f, -760, 380, -95), new Keyframe(.85f, -120, 180, -150), new Keyframe(1, 220, -380, 0) }, // lv-b
            new[] { new Keyframe(0, 0, 520, 10), new Keyframe(.35f, -520, 0, 60), new Keyframe(.65f, 160, -480, 110), new Keyframe(1, 0, 520, 10) },                                           // lv-c
            new[] { new Keyframe(0, -80, -440, 0), new Keyframe(.4f, 520, 260, 70), new Keyframe(.7f, -300, 420, 150), new Keyframe(1, -80, -440, 0) },                                         // lv-d
        };

        private Blob[] _blobs;
        private SKSurface _a, _b; private int _w, _h; private GRContext _ctx;
        private readonly SKPaint _p = new SKPaint { IsAntialias = true };
        private readonly SKPaint _img = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        private SKColorFilter _sat140;
        private float _q = 8f;                                     // drawing resolution: 1/8 (the TV); a phone's smaller canvas needs more
        private float _soft = 70f;                                 // the design's blur (softness)
        private float _cw = 2400, _ch = 1560, _ox = -240, _oy = -240;      // the design's container: inset -240 => 2400 x 1560 starting at (-240,-240)
        private float _moveScale = 1f;                             // the blobs' travel relative to the TV's
        private string _key = "tv";

        /// <summary>What this lamp was made for: the TV's, or a phone's or tablet's canvas of that size (UiContext makes a new one when the canvas changes).</summary>
        public string Key { get { return _key; } }

        /// <summary>
        /// The lamp for a phone or a tablet (design PhonePlayerLoading: a few soft blobs of purple, light blue and yellow drifting across the whole screen): the same morphing blobs and the same
        /// blending as the TV's, with sizes and travel worked out from the canvas (w x h logical units), spread over all of it.
        /// </summary>
        public static Lava ForCanvas(SKColor accent2, SKColor accent, SKColor accent3, float w, float h)
        {
            var lava = new Lava(accent2, accent, accent3);
            float pad = 60, bs = Math.Max(240f, Math.Min(w, h) * 0.8f);
            lava._cw = w + 2 * pad; lava._ch = h + 2 * pad; lava._ox = -pad; lava._oy = -pad;
            lava._q = 2f; lava._soft = Math.Max(60f, Math.Min(w, h) * 0.2f); lava._moveScale = bs / 1280f * 1.2f;
            lava._key = "c|" + (int)w + "x" + (int)h;
            // positions are of the blob's top-left corner inside the container (which starts pad above and left of the canvas)
            lava._blobs = new[]
            {
                new Blob { L = pad - 0.15f * bs, T = pad + 0.10f * h, W = bs, H = bs * 1.1f, Opacity = .55f, Motion = 0, Dur = 9 * 3.4, Delay = 0, Color = accent2, MorphDur = 12, MorphDelay = 0 },
                new Blob { L = pad + w - 0.80f * bs, T = pad + 0.42f * h, W = bs * 0.95f, H = bs, Opacity = .45f, Motion = 1, Dur = 11 * 3.4, Delay = 0, Color = accent, MorphDur = 15, MorphDelay = -4 },
                new Blob { L = pad + 0.20f * w, T = pad + 0.70f * h, W = bs * 0.75f, H = bs * 0.8f, Opacity = .30f, Motion = 2, Dur = 8 * 3.8, Delay = 0, Color = accent3, MorphDur = 10, MorphDelay = -2 },
                new Blob { L = pad + 0.55f * w, T = pad - 0.10f * bs, W = bs * 0.7f, H = bs * 0.75f, Opacity = .38f, Motion = 3, Dur = 42, Delay = -12, Color = accent2, MorphDur = 14, MorphDelay = -7 },
                new Blob { L = pad - 0.20f * bs, T = pad + 0.60f * h, W = bs * 0.8f, H = bs * 0.8f, Opacity = .35f, Motion = 2, Dur = 36, Delay = -18, Color = accent, MorphDur = 11, MorphDelay = -3 },
            };
            return lava;
        }

        public Lava(SKColor accent2, SKColor accent, SKColor accent3)
        {
            _blobs = new[]
            {
                new Blob { L = -60, T = -60, W = 1280, H = 1800, Opacity = .70f, Motion = 0, Dur = 34, Delay = 0, Color = accent2, MorphDur = 12, MorphDelay = 0 },
                new Blob { L = 1160, T = 160, W = 1160, H = 1680, Opacity = .60f, Motion = 1, Dur = 38, Delay = 0, Color = accent, MorphDur = 15, MorphDelay = -4 },
                new Blob { L = 560, T = 520, W = 1000, H = 1440, Opacity = .55f, Motion = 2, Dur = 30, Delay = 0, Color = accent3, MorphDur = 10, MorphDelay = -2 },
                new Blob { L = 1500, T = -300, W = 880, H = 1320, Opacity = .60f, Motion = 3, Dur = 42, Delay = -12, Color = accent2, MorphDur = 14, MorphDelay = -7 },
                new Blob { L = 0, T = 640, W = 840, H = 1200, Opacity = .55f, Motion = 2, Dur = 36, Delay = -18, Color = accent, MorphDur = 11, MorphDelay = -3 },
                new Blob { L = 1200, T = -160, W = 800, H = 1120, Opacity = .50f, Motion = 0, Dur = 40, Delay = -21, Color = accent3, MorphDur = 13, MorphDelay = -9 },
            };
            float s = 1.4f, a = 0.213f * (1 - s), b = 0.715f * (1 - s), c = 0.072f * (1 - s);
            _sat140 = SKColorFilter.CreateColorMatrix(new float[] { a + s, b, c, 0, 0, a, b + s, c, 0, 0, a, b, c + s, 0, 0, 0, 0, 0, 1, 0 });
        }

        private static double Phase(double now, double dur, double delay) { double t = (now - delay) % dur; if (t < 0) t += dur; return t / dur; }

        private static SKPath OrganicPath(float w, float h, float[] r)
        {
            // rectangle with different elliptical corners (CSS border-radius): radii in %, adding up to 100 % per side
            float tlx = r[0] / 100 * w, trx = r[1] / 100 * w, brx = r[2] / 100 * w, blx = r[3] / 100 * w;
            float tly = r[4] / 100 * h, try_ = r[5] / 100 * h, bry = r[6] / 100 * h, bly = r[7] / 100 * h;
            var p = new SKPath();
            float l = -w / 2, t = -h / 2, rr = w / 2, b = h / 2;
            p.MoveTo(l + tlx, t);
            p.LineTo(rr - trx, t);
            p.ArcTo(new SKRect(rr - 2 * trx, t, rr, t + 2 * try_), -90, 90, false);
            p.LineTo(rr, b - bry);
            p.ArcTo(new SKRect(rr - 2 * brx, b - 2 * bry, rr, b), 0, 90, false);
            p.LineTo(l + blx, b);
            p.ArcTo(new SKRect(l, b - 2 * bly, l + 2 * blx, b), 90, 90, false);
            p.LineTo(l, t + tly);
            p.ArcTo(new SKRect(l, t, l + 2 * tlx, t + 2 * tly), 180, 90, false);
            p.Close();
            return p;
        }

        private static void MorphAt(double ph, float[] outR)
        {
            int i = 0;
            while (i < MorphT.Length - 2 && ph >= MorphT[i + 1]) i++;
            float p = (float)((ph - MorphT[i]) / (MorphT[i + 1] - MorphT[i]));
            float e = Bezier.EaseInOut[p];
            for (int k = 0; k < 8; k++) outR[k] = Morph[i][k] + (Morph[i + 1][k] - Morph[i][k]) * e;
        }

        /// <summary>
        /// Draws the lamp full screen (the canvas is already scaled to 1920x1080 logical). cover is the whole surface in logical units when it is bigger than the canvas (phone letterbox):
        /// the lamp, very soft, is simply stretched to reach it.
        /// </summary>
        public void Draw(SKCanvas canvas, GRContext ctx, double now, SKRect? cover = null)
        {
            float cw = _cw, ch = _ch, Q = _q;
            int pw = (int)Math.Ceiling(cw / Q), ph = (int)Math.Ceiling(ch / Q);
            if (_a == null || _w != pw || _h != ph || !ReferenceEquals(_ctx, ctx))
            {
                if (_a != null) _a.Dispose(); if (_b != null) _b.Dispose();
                _a = SpriteCache.NewSurface(ctx, pw, ph); _b = SpriteCache.NewSurface(ctx, pw, ph); _w = pw; _h = ph; _ctx = ctx;
            }
            var ca = _a.Canvas;
            ca.Clear(SKColors.Transparent);
            var radii = new float[8];
            foreach (var b in _blobs)
            {
                var mv = Moves[b.Motion];
                double mp = Phase(now, b.Dur, b.Delay);
                float x, y, rot;
                Sample(mv, mp, out x, out y, out rot);
                x *= _moveScale; y *= _moveScale;
                MorphAt(Phase(now, b.MorphDur, b.MorphDelay), radii);
                ca.Save();
                ca.Scale(1 / Q);
                ca.Translate(b.L + b.W / 2 + x, b.T + b.H / 2 + y);
                ca.RotateDegrees(rot);
                using (var path = OrganicPath(b.W, b.H, radii))
                {
                    _p.Style = SKPaintStyle.Fill; _p.Shader = null; _p.BlendMode = SKBlendMode.Screen;
                    _p.Color = new SKColor(b.Color.Red, b.Color.Green, b.Color.Blue, (byte)(b.Opacity * 255));
                    ca.DrawPath(path, _p);
                }
                ca.Restore();
            }
            _p.BlendMode = SKBlendMode.SrcOver;
            // blur (70 logical px = 70/8 px here) and saturation at 140 %, on the small surface
            var cb = _b.Canvas;
            cb.Clear(SKColors.Transparent);
            using (var snapA = _a.Snapshot())
            using (var blur = SKImageFilter.CreateBlur(_soft / Q, _soft / Q))
            using (var paint = new SKPaint { ImageFilter = blur, ColorFilter = _sat140, FilterQuality = SKFilterQuality.Low })
                cb.DrawImage(snapA, 0, 0, paint);
            using (var snapB = _b.Snapshot())
            {
                _img.Color = SKColors.White; _img.FilterQuality = SKFilterQuality.Medium;
                var dst = new SKRect(_ox, _oy, _ox + cw, _oy + ch);
                if (cover.HasValue) dst = new SKRect(Math.Min(dst.Left, cover.Value.Left), Math.Min(dst.Top, cover.Value.Top), Math.Max(dst.Right, cover.Value.Right), Math.Max(dst.Bottom, cover.Value.Bottom));
                canvas.DrawImage(snapB, dst, _img);
            }
        }

        private static void Sample(Keyframe[] k, double t01, out float x, out float y, out float s)
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

        /// <summary>Frees the two off-screen surfaces (Draw creates them again when needed).</summary>
        public void TrimMemory()
        {
            if (_a != null) _a.Dispose(); if (_b != null) _b.Dispose();
            _a = null; _b = null; _ctx = null;
        }

        public void Dispose()
        {
            if (_a != null) _a.Dispose(); if (_b != null) _b.Dispose(); if (_sat140 != null) _sat140.Dispose(); _p.Dispose(); _img.Dispose();
        }
    }
}
