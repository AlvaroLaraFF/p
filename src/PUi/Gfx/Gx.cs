using System;
using System.Collections.Generic;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Drawing utilities over SkiaSharp (common subset of 1.60 and 2.88): rounded rectangles, inner borders as in CSS, text with the
    /// baseline computed like the browser, text with spacing, vector icons, images. Reuses SKPaint (one Gx per drawing thread).
    /// </summary>
    public sealed class Gx : IDisposable
    {
        private readonly Fonts _fonts;
        private readonly SKPaint _p = new SKPaint { IsAntialias = true };
        private readonly SKPaint _text = new SKPaint { IsAntialias = true, SubpixelText = true };
        private readonly SKPaint _img = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Low };
        private int _nullPathLogged;
        private readonly Dictionary<string, float> _widths = new Dictionary<string, float>();

        public Gx(Fonts fonts) { _fonts = fonts; }
        public Fonts Fonts { get { return _fonts; } }
        public SKPaint Paint { get { return _p; } }

        // ---------------------------------------------------------------- shapes
        public void FillRR(SKCanvas c, SKRect r, float rad, SKColor col)
        {
            _p.Style = SKPaintStyle.Fill; _p.Shader = null; _p.MaskFilter = null; _p.Color = col;
            if (rad <= 0) c.DrawRect(r, _p); else c.DrawRoundRect(r, rad, rad, _p);
        }

        public void FillRR(SKCanvas c, SKRect r, float rad, SKShader sh, byte alpha = 255)
        {
            _p.Style = SKPaintStyle.Fill; _p.MaskFilter = null; _p.Shader = sh; _p.Color = new SKColor(255, 255, 255, alpha);
            if (rad <= 0) c.DrawRect(r, _p); else c.DrawRoundRect(r, rad, rad, _p);
            _p.Shader = null;
        }

        /// <summary>Border of 'width' px INSIDE the rectangle (like CSS border with box-sizing: border-box).</summary>
        public void StrokeRR(SKCanvas c, SKRect r, float rad, float width, SKColor col)
        {
            _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = width; _p.Shader = null; _p.MaskFilter = null; _p.Color = col;
            var rr = new SKRect(r.Left + width / 2, r.Top + width / 2, r.Right - width / 2, r.Bottom - width / 2);
            float rad2 = Math.Max(0, rad - width / 2);
            if (rad2 <= 0) c.DrawRect(rr, _p); else c.DrawRoundRect(rr, rad2, rad2, _p);
            _p.Style = SKPaintStyle.Fill;
        }

        public void FillCircle(SKCanvas c, float cx, float cy, float r, SKColor col)
        {
            _p.Style = SKPaintStyle.Fill; _p.Shader = null; _p.MaskFilter = null; _p.Color = col;
            c.DrawCircle(cx, cy, r, _p);
        }

        public void StrokeCircle(SKCanvas c, float cx, float cy, float r, float width, SKColor col)
        {
            _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = width; _p.Shader = null; _p.MaskFilter = null; _p.Color = col;
            c.DrawCircle(cx, cy, r, _p);
            _p.Style = SKPaintStyle.Fill;
        }

        /// <summary>Arc (small spinner): sweep of 'sweep' degrees from 'start' (0 = at 12 o'clock).</summary>
        public void StrokeArc(SKCanvas c, float cx, float cy, float r, float width, SKColor col, float startDeg, float sweepDeg)
        {
            _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = width; _p.Shader = null; _p.MaskFilter = null; _p.Color = col; _p.StrokeCap = SKStrokeCap.Butt;
            using (var path = new SKPath()) { path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), startDeg - 90, sweepDeg); c.DrawPath(path, _p); }
            _p.Style = SKPaintStyle.Fill;
        }

        public void Line(SKCanvas c, float x1, float y1, float x2, float y2, float width, SKColor col)
        {
            _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = width; _p.Shader = null; _p.MaskFilter = null; _p.Color = col; _p.StrokeCap = SKStrokeCap.Round;
            c.DrawLine(x1, y1, x2, y2, _p);
            _p.Style = SKPaintStyle.Fill; _p.StrokeCap = SKStrokeCap.Butt;
        }

        // ---------------------------------------------------------------- text
        private SKPaint TextPaint(float size, int weight, SKColor col)
        {
            _text.Typeface = _fonts.Get(weight); _text.TextSize = size; _text.Color = col; _text.Shader = null; _text.MaskFilter = null;
            return _text;
        }

        public float MeasureText(string s, float size, int weight)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string key = weight + ":" + size + ":" + s;
            float w;
            if (_widths.TryGetValue(key, out w)) return w;
            w = TextPaint(size, weight, SKColors.White).MeasureText(s);
            if (_widths.Count > 4000) _widths.Clear();
            _widths[key] = w;
            return w;
        }

        /// <summary>Width with letter spacing (letterSpacing in px, may be negative).</summary>
        public float MeasureText(string s, float size, int weight, float letterSpacing)
        {
            return MeasureText(s, size, weight) + (string.IsNullOrEmpty(s) ? 0 : s.Length * letterSpacing);
        }

        /// <summary>Baseline for a text line of height 'lineH' starting at 'top' (like CSS: the text centered in the line box).</summary>
        public float Baseline(float top, float lineH, float size, int weight)
        {
            var fm = TextPaint(size, weight, SKColors.White).FontMetrics;
            float h = fm.Descent - fm.Ascent;
            return top + (lineH - h) / 2 - fm.Ascent;
        }

        /// <summary>CSS "normal" line height (ascent + descent + gap) for that font and size.</summary>
        public float LineHeight(float size, int weight)
        {
            var fm = TextPaint(size, weight, SKColors.White).FontMetrics;
            return fm.Descent - fm.Ascent + fm.Leading;
        }

        /// <summary>The smallest text size drawn since the last reset, and one text of that size (for the tests that check the legibility of the phone and tablet layouts: nothing under 12 CSS px).</summary>
        public float SmallestTextSize = float.MaxValue;
        public string SmallestTextSample = "";
        public void ResetTextStats() { SmallestTextSize = float.MaxValue; SmallestTextSample = ""; }

        public void DrawText(SKCanvas c, string s, float x, float baseline, float size, int weight, SKColor col, float letterSpacing = 0)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (size < SmallestTextSize) { SmallestTextSize = size; SmallestTextSample = s; }
            var p = TextPaint(size, weight, col);
            if (Math.Abs(letterSpacing) < 0.3f) { c.DrawText(s, x, baseline, p); return; }
            var pts = new SKPoint[s.Length];
            for (int i = 0; i < s.Length; i++) pts[i] = new SKPoint(x + (i == 0 ? 0 : p.MeasureText(s.Substring(0, i))) + i * letterSpacing, baseline);
            c.DrawPositionedText(s, pts, p);
        }

        /// <summary>Aligned text: align 0 = left (x is the left edge), 0.5 = centered (x is the center), 1 = right (x is the right edge).</summary>
        public void DrawTextAligned(SKCanvas c, string s, float x, float baseline, float size, int weight, SKColor col, float align, float letterSpacing = 0)
        {
            float w = MeasureText(s, size, weight, letterSpacing);
            DrawText(c, s, x - w * align, baseline, size, weight, col, letterSpacing);
        }

        public string Ellipsize(string s, float size, int weight, float maxW)
        {
            if (string.IsNullOrEmpty(s) || MeasureText(s, size, weight) <= maxW) return s;
            int lo = 1, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (MeasureText(s.Substring(0, mid).TrimEnd() + "…", size, weight) <= maxW) lo = mid; else hi = mid - 1;
            }
            return s.Substring(0, lo).TrimEnd() + "…";
        }

        /// <summary>Splits the text into lines of at most maxW (by words), at most maxLines.</summary>
        public List<string> WrapText(string s, float size, int weight, float maxW, int maxLines = 4)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(s)) return lines;
            var words = s.Split(' ');
            string cur = "";
            for (int i = 0; i < words.Length; i++)
            {
                string t = cur.Length == 0 ? words[i] : cur + " " + words[i];
                if (cur.Length > 0 && MeasureText(t, size, weight) > maxW)
                {
                    lines.Add(cur); cur = words[i];
                    if (lines.Count == maxLines - 1) { cur = string.Join(" ", words, i, words.Length - i); break; }
                }
                else cur = t;
            }
            if (cur.Length > 0) lines.Add(Ellipsize(cur, size, weight, maxW));
            return lines;
        }

        // ---------------------------------------------------------------- images
        public void DrawImage(SKCanvas c, SKImage img, SKRect dst, float alpha = 1f, SKFilterQuality q = SKFilterQuality.Low)
        {
            if (img == null || alpha <= 0.002f) return;
            _img.FilterQuality = q; _img.Color = new SKColor(255, 255, 255, (byte)(alpha >= 1f ? 255 : alpha * 255f + 0.5f)); _img.ImageFilter = null; _img.ColorFilter = null; _img.Shader = null;
            c.DrawImage(img, dst, _img);
        }

        public void DrawImage(SKCanvas c, SKImage img, float x, float y, float alpha = 1f)
        {
            if (img == null) return;
            DrawImage(c, img, new SKRect(x, y, x + img.Width, y + img.Height), alpha);
        }

        public SKPaint ImagePaint { get { return _img; } }

        // ---------------------------------------------------------------- icons
        /// <summary>24x24 vector icon (rounded stroke) centered at cx,cy at 'size' px.</summary>
        public void DrawIcon(SKCanvas c, Icon icon, float cx, float cy, float size, SKColor col, float strokeWidth = 1.8f)
        {
            if (icon == null) return;
            c.Save(); c.Translate(cx - size / 2, cy - size / 2); c.Scale(size / 24f);
            _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = strokeWidth; _p.StrokeCap = SKStrokeCap.Round; _p.StrokeJoin = SKStrokeJoin.Round; _p.Color = col; _p.Shader = null; _p.MaskFilter = null;
            for (int i = 0; i < icon.StrokePaths.Count; i++)
            {
                var path = icon.StrokePaths[i];
                if (path == null) { if (_nullPathLogged++ < 3) AceEngine.Util.AceLog.Warn("ui", "icon with null path (stroke " + i + " of " + icon.StrokePaths.Count + ", fills " + icon.FillPaths.Count + ")"); continue; }
                c.DrawPath(path, _p);
            }
            _p.Style = SKPaintStyle.Fill;
            for (int i = 0; i < icon.FillPaths.Count; i++) { var path = icon.FillPaths[i]; if (path != null) c.DrawPath(path, _p); }
            _p.StrokeCap = SKStrokeCap.Butt; _p.StrokeJoin = SKStrokeJoin.Miter;
            c.Restore();
        }

        public void Dispose() { _p.Dispose(); _text.Dispose(); _img.Dispose(); }
    }
}
