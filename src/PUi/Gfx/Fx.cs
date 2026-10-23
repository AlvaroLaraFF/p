using System;
using System.Collections.Generic;
using PUi.Motion;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Design effects made to be cheap on the TV: shadow and halo as pre-blurred sprites (never a live blur per card),
    /// fluid border as a rotating conic gradient, spinner, pulse, skeleton shimmer and animated strip.
    /// </summary>
    public sealed class Fx : IDisposable
    {
        private const float ShadowPad = 100, HaloPad = 44;
        private readonly Gx _gx;
        private readonly SpriteCache _sprites;
        private readonly Dictionary<string, SKBitmap> _haloMasks = new Dictionary<string, SKBitmap>();
        private readonly SKPaint _p = new SKPaint { IsAntialias = true };

        private readonly DecorClock _decor;

        /// <summary>decor: clock of the repeating decoration (fluid border, strip, pulse); null = real time.</summary>
        public Fx(Gx gx, SpriteCache sprites, DecorClock decor = null) { _gx = gx; _sprites = sprites; _decor = decor; }

        /// <summary>Time of the decoration that repeats non-stop: the decoration clock (it slows down and stops at rest) or, if nobody drives it, real time.</summary>
        private double Decor(double now) { return _decor != null && _decor.Driven ? _decor.Value : now; }

        // ---------------------------------------------------------------- shadow (0 24px 64px rgba(0,0,0,.55) -> sigma 32)
        private SKImage ShadowSprite(SKRect r, float rad, float blur, out float pad, out SKRect bounds)
        {
            string key = "shadow:" + (int)Math.Round(r.Width) + "x" + (int)Math.Round(r.Height) + ":" + (int)rad + ":" + (int)blur;
            float p = blur * 1.6f;
            pad = p;
            var bb = new SKRect(-p, -p, r.Width + p, r.Height + p);
            bounds = bb;
            const float q = 2f;                                       // drawn at half resolution: it is very soft
            return _sprites.GetPixels(key, (int)Math.Ceiling(bb.Width / q), (int)Math.Ceiling(bb.Height / q), cv =>
            {
                using (var src = new SKBitmap((int)Math.Ceiling(bb.Width / q), (int)Math.Ceiling(bb.Height / q), SKColorType.Rgba8888, SKAlphaType.Premul))
                using (var dst = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                {
                    using (var sc = new SKCanvas(src)) using (var pt = new SKPaint { IsAntialias = true, Color = SKColors.Black })
                    { sc.Clear(SKColors.Transparent); sc.DrawRoundRect(new SKRect(p / q, p / q, (p + r.Width) / q, (p + r.Height) / q), rad / q, rad / q, pt); }
                    using (var dc = new SKCanvas(dst)) using (var pt = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blur / 2f / q, blur / 2f / q) })
                    { dc.Clear(SKColors.Transparent); dc.DrawBitmap(src, 0, 0, pt); }
                    using (var im = SKImage.FromBitmap(dst)) cv.DrawImage(im, 0, 0);
                }
            });
        }

        /// <summary>Frees the halo masks (they are rebuilt when a halo is next drawn).</summary>
        public void TrimMemory() { foreach (var b in _haloMasks.Values) b.Dispose(); _haloMasks.Clear(); }

        public void WarmShadow(SKRect r, float rad, float blur = 64) { float pad; SKRect b; ShadowSprite(r, rad, blur, out pad, out b); }
        public void WarmHalo(SKRect r, float rad) { HaloMask(r.Width, r.Height, rad); }

        public void Shadow(SKCanvas c, SKRect r, float rad, float alpha, float dy = 24, float blur = 64)
        {
            if (alpha <= 0.01f) return;
            float pad; SKRect bounds;
            var img = ShadowSprite(r, rad, blur, out pad, out bounds);
            // like a CSS box-shadow: the shadow is not painted under the box itself (the card is translucent)
            c.Save();
            using (var rr = new SKRoundRect(r, rad, rad)) c.ClipRoundRect(rr, SKClipOperation.Difference, true);
            _gx.DrawImage(c, img, new SKRect(r.Left - pad, r.Top - pad + dy, r.Left - pad + bounds.Width, r.Top - pad + dy + bounds.Height), Math.Min(1f, alpha));
            c.Restore();
        }

        // ---------------------------------------------------------------- fluid border
        private SKBitmap HaloMask(float w, float h, float rad)
        {
            string key = ((int)Math.Round(w)) + "x" + ((int)Math.Round(h)) + ":" + (int)rad;
            SKBitmap m;
            if (_haloMasks.TryGetValue(key, out m)) return m;
            const float q = 2f;
            int bw = (int)Math.Ceiling((w + 2 * HaloPad) / q), bh = (int)Math.Ceiling((h + 2 * HaloPad) / q);
            using (var src = new SKBitmap(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul))
            {
                m = new SKBitmap(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var c = new SKCanvas(src)) using (var p = new SKPaint { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 2 / q })
                { c.Clear(SKColors.Transparent); c.DrawRoundRect(new SKRect(HaloPad / q - 1 / q, HaloPad / q - 1 / q, HaloPad / q + w / q + 1 / q, HaloPad / q + h / q + 1 / q), (rad + 1) / q, (rad + 1) / q, p); }
                using (var c = new SKCanvas(m)) using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(12 / q, 12 / q) })
                { c.Clear(SKColors.Transparent); c.DrawBitmap(src, 0, 0, p); }
            }
            if (_haloMasks.Count > 64) { foreach (var b in _haloMasks.Values) b.Dispose(); _haloMasks.Clear(); }
            _haloMasks[key] = m;
            return m;
        }

        /// <summary>Fluid border (light blue, blue, purple, yellow; one turn every 7 s) of 2 px OUTSIDE r, with its blurred halo at 50 %.</summary>
        public void FluidRing(SKCanvas c, SKRect r, float rad, float opacity, double now, bool halo = true)
        {
            if (opacity <= 0.01f) return;
            now = Decor(now);
            float angle = (float)((now % Tokens.RingTurnSecs) / Tokens.RingTurnSecs * 360.0);
            float cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            var m = SKMatrix.MakeRotationDegrees(angle - 90, cx, cy);
            using (var sh = SKShader.CreateSweepGradient(new SKPoint(cx, cy), Tokens.RingColors, Tokens.RingPositions, m))
            {
                if (halo)
                {
                    var mask = HaloMask(r.Width, r.Height, rad);
                    var full = SKMatrix.MakeScale(2f, 2f); full.TransX = r.Left - HaloPad; full.TransY = r.Top - HaloPad;
                    using (var ms = SKShader.CreateBitmap(mask, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, full))
                    using (var comp = SKShader.CreateCompose(ms, sh, SKBlendMode.SrcIn))
                    {
                        _p.Style = SKPaintStyle.Fill; _p.MaskFilter = null; _p.Shader = comp; _p.Color = new SKColor(255, 255, 255, (byte)(0.5f * opacity * 255));
                        c.DrawRect(new SKRect(r.Left - HaloPad, r.Top - HaloPad, r.Right + HaloPad, r.Bottom + HaloPad), _p);
                    }
                }
                var outer = new SKRect(r.Left - 1, r.Top - 1, r.Right + 1, r.Bottom + 1);
                _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = 2; _p.Shader = sh; _p.MaskFilter = null; _p.Color = new SKColor(255, 255, 255, (byte)(opacity * 255));
                c.DrawRoundRect(outer, rad + 1, rad + 1, _p);
                _p.Shader = null; _p.Style = SKPaintStyle.Fill;
            }
        }

        // ---------------------------------------------------------------- small spinner (0.9 s)
        public void Spinner(SKCanvas c, float cx, float cy, float diameter, double now, SKColor color, double period = Tokens.SpinSecs)
        {
            float r = diameter / 2 - 1;
            _gx.StrokeCircle(c, cx, cy, r, 2, Tokens.White(0.16f));
            float ang = (float)((now % period) / period * 360.0);
            _gx.StrokeArc(c, cx, cy, r, 2, color, ang - 45, 90);
        }

        // ---------------------------------------------------------------- live dot with pulse (1.8 s)
        public void PulseDot(SKCanvas c, float cx, float cy, float radius, double now, float pulseMax)
        {
            float ph = (float)((Decor(now) % Tokens.PulseSecs) / Tokens.PulseSecs);
            if (ph < 0.7f)
            {
                float u = ph / 0.7f;
                float e = 1 - (1 - u) * (1 - u);                 // ease-out
                _gx.FillCircle(c, cx, cy, radius + pulseMax * e, Tokens.WithAlpha(Tokens.Accent, 0.55f * (1 - e)));
            }
            _gx.FillCircle(c, cx, cy, radius, Tokens.Accent);
        }

        // ---------------------------------------------------------------- skeleton with shimmer (1.4 s)
        public void Shimmer(SKCanvas c, SKRect r, float rad, double now)
        {
            float off = -600 + 1200 * (float)((now % Tokens.ShimmerSecs) / Tokens.ShimmerSecs);
            var cols = new[] { Tokens.White(0.04f), Tokens.White(0.14f), Tokens.White(0.04f) };
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(r.Left + off, 0), new SKPoint(r.Left + off + 1200, 0), cols, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Repeat))
                _gx.FillRR(c, r, rad, sh);
        }

        // ---------------------------------------------------------------- animated strip between recents and categories (16 s, back and forth)
        public void Divider(SKCanvas c, float x, float y, float w, double now)
        {
            const float h = 2;
            double cyc = (Decor(now) % (2 * Tokens.DividerSecs)) / Tokens.DividerSecs;
            float ph = Bezier.EaseInOut[cyc <= 1 ? cyc : 2 - cyc];
            float off = -ph * w;
            var colors = new[] { SKColors.White, SKColor.Parse("#BDF7EE"), SKColors.White, SKColor.Parse("#D9C8FF"), SKColors.White, SKColor.Parse("#BDF7EE") };
            var pos = new[] { 0f, .2f, .4f, .6f, .8f, 1f };
            var acol = new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 217), new SKColor(0, 0, 0, 217), new SKColor(0, 0, 0, 0) };   // mask x opacity .85
            using (var lg = SKShader.CreateLinearGradient(new SKPoint(x + off, 0), new SKPoint(x + off + 2 * w, 0), colors, pos, SKShaderTileMode.Clamp))
            using (var mk = SKShader.CreateLinearGradient(new SKPoint(x, 0), new SKPoint(x + w, 0), acol, new[] { 0f, .24f, .76f, 1f }, SKShaderTileMode.Clamp))
            using (var comp = SKShader.CreateCompose(mk, lg, SKBlendMode.SrcIn))
                _gx.FillRR(c, new SKRect(x, y, x + w, y + h), 1, comp);
        }

        public void Dispose()
        {
            foreach (var b in _haloMasks.Values) b.Dispose();
            _haloMasks.Clear(); _p.Dispose();
        }
    }
}
