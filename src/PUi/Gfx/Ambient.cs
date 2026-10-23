using System;
using System.Threading;
using System.Threading.Tasks;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Background of the menus: #070711 with three blurred lights (purple, blue, light blue), STILL, and a parallax following the focus (1.8 s).
    /// User decision (2026-10-17): fixed background of maximum quality; the animated bubbles only on channel loading (Lava).
    ///
    /// The image is computed ONCE, in the background, in floating point and at the surface's real resolution (with a margin for the
    /// parallax), and quantized to 8 bits with triangular dithering (TPDF) of ±1 level: no banding and no visible grain. After that each
    /// frame only copies that image shifted by a whole number of pixels (the dithering does not get smeared). While it is being computed, the
    /// same lights are drawn as Skia gradients with dither.
    /// </summary>
    public sealed class Ambient : IDisposable
    {
        public struct LightDef
        {
            public float Left, Top, W, H, Sigma, Opacity, Scale; public SKColor Color;
            public LightDef(float left, float top, float w, float h, float sigma, string color, float opacity, float scale)
            { Left = left; Top = top; W = w; H = h; Sigma = sigma; Color = SKColor.Parse(color); Opacity = opacity; Scale = scale; }
        }

        /// <summary>The lights of a background and how far it moves: the TV's (<see cref="Tv"/>) or one worked out for a phone or a tablet (<see cref="ForCanvas"/>). Positions are in logical units of the canvas.</summary>
        public sealed class Profile
        {
            public string Id = "tv";
            public LightDef[] Lights;
            /// <summary>Parallax amplitude and the margin kept around the image to shift it (logical units).</summary>
            public float Amp = 330f, Margin = 170f;
            /// <summary>The canvas's logical size (the parallax is relative to it).</summary>
            public float CanvasW = Tokens.W, CanvasH = Tokens.H;
        }

        // Design positions (a 2240x1400 layer starting at (-160,-160)) at instant 0 of their keyframes (Amb: scale 1; Amb2: 1.08).
        /// <summary>The TV's background: three lights on the 1920 x 1080 canvas.</summary>
        public static readonly Profile Tv = new Profile
        {
            Id = "tv",
            Lights = new[]
            {
                new LightDef(-160 + -120, -160 + -160, 940, 780, 160, "#7B3DFF", 0.32f, 1.00f),
                new LightDef(-160 + 980, -160 + 480, 640, 520, 160, "#2B5BFF", 0.12f, 1.08f),
                new LightDef(-160 + (2240 + 100 - 1000), -160 + (1400 + 180 - 740), 1000, 740, 170, "#17CDB4", 0.22f, 1.08f),
            }
        };

        /// <summary>
        /// The background of a phone or a tablet: the two lights of the design's home screens (purple at the top left, light blue at the bottom right) placed on the canvas of that
        /// kind, which is as tall as the surface needs. The parallax travels in proportion to the canvas's width (the TV's is 1920 wide).
        /// </summary>
        public static Profile ForCanvas(DeviceKind kind, float w, float h)
        {
            LightDef purple, teal;
            if (kind == DeviceKind.PhonePortrait)
            {
                purple = new LightDef(-140, -120, 420, 380, 110, "#7B3DFF", 0.34f, 1f);
                teal = new LightDef(w + 160 - 420, h - 60 - 360 + 20, 420, 360, 120, "#17CDB4", 0.20f, 1f);
            }
            else if (kind == DeviceKind.TabletPortrait)
            {
                purple = new LightDef(-160, -160, 640, 560, 140, "#7B3DFF", 0.32f, 1f);
                teal = new LightDef(w + 200 - 640, h - 100 - 520 + 20, 640, 520, 150, "#17CDB4", 0.20f, 1f);
            }
            else
            {
                purple = new LightDef(-100, -140, 680, 560, 140, "#7B3DFF", 0.32f, 1f);
                teal = new LightDef(w + 80 - 720, h + 160 - 540, 720, 540, 150, "#17CDB4", 0.20f, 1f);
            }
            float k = w / Tokens.W;
            return new Profile { Id = kind + "|" + (int)w + "x" + (int)h, Lights = new[] { purple, teal }, Amp = 330f * k, Margin = 170f * k, CanvasW = w, CanvasH = h };
        }

        private const float ParallaxK = 0.9f;  // a single layer: average of the design's two speeds (0.6 and 1.2)
        private const int ProfileSize = 4096;

        private readonly Anim _px = new Anim(0, Bezier.Pop), _py = new Anim(0, Bezier.Pop);
        private readonly SKPaint _blit = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        private readonly SKPaint _grad = new SKPaint { IsAntialias = false, IsDither = true };
        private SKShader[] _fallbackShaders; private string _fallbackFor;
        private volatile SKImage _image; private int _imgW, _imgH, _imgMargin; private float _imgK, _imgOx, _imgOy; private string _imgProfile;
        private Task _building; private int _buildW, _buildH; private float _buildK, _buildOx, _buildOy; private string _buildProfile;
        private int _disposed;

        /// <summary>Center of the focused element (logical coordinates): the background moves the opposite way, in steps of 0.1 like the design.</summary>
        public void FocusMoved(float cx, float cy, double now)
        {
            FocusMoved(cx, cy, now, Tokens.W, Tokens.H);
        }

        /// <summary>The same on a canvas of another size (a phone or a tablet).</summary>
        public void FocusMoved(float cx, float cy, double now, float canvasW, float canvasH)
        {
            float px = (float)Math.Round((cx / canvasW - 0.5) * 10) / 10, py = (float)Math.Round((cy / canvasH - 0.5) * 10) / 10;
            _px.Set(px, now, Tokens.ParallaxSecs); _py.Set(py, now, Tokens.ParallaxSecs);
        }

        public bool Parallaxing(double now) { return _px.Active(now) || _py.Active(now); }

        /// <summary>Draws the whole background (opaque) on a surface that the 1920x1080 canvas fills edge to edge. The canvas arrives scaled to 1920x1080 logical; pixelW/pixelH are the surface's.</summary>
        public void Draw(SKCanvas canvas, GRContext ctx, double now, int pixelW, int pixelH, float opacity = 1f)
        {
            Draw(canvas, ctx, now, pixelW, pixelH, pixelW / (float)Tokens.W, 0, 0, opacity);
        }

        /// <summary>
        /// Draws the whole background (opaque) on the whole surface when the canvas does not cover it (letterbox on a phone or a tablet): k is the pixels per logical unit and
        /// originX/originY the pixel position of the canvas's top-left corner. The lights keep their place relative to the canvas and simply go on into the bars.
        /// </summary>
        public void Draw(SKCanvas canvas, GRContext ctx, double now, int pixelW, int pixelH, float k, float originX, float originY, float opacity = 1f, Profile profile = null)
        {
            profile = profile ?? Tv;
            EnsureBuilt(pixelW, pixelH, k, originX, originY, profile);
            float offX = -_px.Value(now) * profile.Amp * ParallaxK, offY = -_py.Value(now) * profile.Amp * 0.7f * ParallaxK;
            var img = _image;
            if (img != null && _imgW == pixelW && _imgH == pixelH && _imgK == k && _imgOx == originX && _imgOy == originY && _imgProfile == profile.Id)
            {
                canvas.Save();
                canvas.ResetMatrix();
                int dx = (int)Math.Round(-_imgMargin + offX * k), dy = (int)Math.Round(-_imgMargin + offY * k);
                _blit.Color = new SKColor(255, 255, 255, (byte)(opacity * 255));
                canvas.DrawImage(img, dx, dy, _blit);
                canvas.Restore();
                return;
            }
            DrawFallback(canvas, profile, offX, offY, opacity, originX == 0 && originY == 0 ? new SKRect(0, 0, profile.CanvasW, profile.CanvasH) : new SKRect(-originX / k, -originY / k, (pixelW - originX) / k, (pixelH - originY) / k));
        }

        private void EnsureBuilt(int pixelW, int pixelH, float k, float originX, float originY, Profile profile)
        {
            if (pixelW <= 0 || pixelH <= 0 || k <= 0) return;
            if (_building != null && _buildW == pixelW && _buildH == pixelH && _buildK == k && _buildOx == originX && _buildOy == originY && _buildProfile == profile.Id) return;
            _buildW = pixelW; _buildH = pixelH; _buildK = k; _buildOx = originX; _buildOy = originY; _buildProfile = profile.Id;
            _building = Task.Run(() =>
            {
                try
                {
                    int marginPx;
                    var bmp = Build(pixelW, pixelH, k, originX, originY, out marginPx, profile);
                    if (Volatile.Read(ref _disposed) != 0) { bmp.Dispose(); return; }
                    var img = SKImage.FromBitmap(bmp);
                    bmp.Dispose();
                    var old = _image;
                    _imgW = pixelW; _imgH = pixelH; _imgMargin = marginPx; _imgK = k; _imgOx = originX; _imgOy = originY; _imgProfile = profile.Id;
                    _image = img;
                    if (old != null) old.Dispose();
                }
                catch (Exception) { /* no image: the fallback gradient keeps being used */ }
            });
        }

        /// <summary>Computes the background in floating point and quantizes it with TPDF dithering. Public for the test bench.</summary>
        public static SKBitmap Build(int pixelW, int pixelH, out int marginPx)
        {
            return Build(pixelW, pixelH, pixelW / (float)Tokens.W, 0, 0, out marginPx);
        }

        /// <summary>The same with the canvas at pixel (originX, originY) and k pixels per logical unit (see Draw).</summary>
        public static SKBitmap Build(int pixelW, int pixelH, float k, float originX, float originY, out int marginPx, Profile profile = null)
        {
            profile = profile ?? Tv;
            var Lights = profile.Lights;
            marginPx = (int)Math.Ceiling(profile.Margin * k);
            int w = pixelW + 2 * marginPx, h = pixelH + 2 * marginPx;
            int m = marginPx;
            var bg = Tokens.Bg;
            float bgR = bg.Red, bgG = bg.Green, bgB = bg.Blue;

            // Radial profile of each light (blurred ellipse: 0.5·erfc((d − a)/(σ·√2)), with d up to a + 2.6σ) in a table.
            int n = Lights.Length;
            var prof = new float[n][];
            var cx = new float[n]; var cy = new float[n]; var invRx = new float[n]; var invRy = new float[n];
            var lr = new float[n]; var lg = new float[n]; var lb = new float[n];
            for (int i = 0; i < n; i++)
            {
                var L = Lights[i];
                float ax = L.W / 2f, ext = 2.6f * L.Sigma;
                prof[i] = new float[ProfileSize + 1];
                for (int j = 0; j <= ProfileSize; j++)
                {
                    double d = j / (double)ProfileSize * (ax + ext);
                    prof[i][j] = (float)(0.5 * Erfc((d - ax) / (L.Sigma * Math.Sqrt(2))) * L.Opacity);
                }
                prof[i][ProfileSize] = 0;
                cx[i] = (L.Left + L.W / 2f) * k + originX; cy[i] = (L.Top + L.H / 2f) * k + originY;
                invRx[i] = 1f / ((ax + ext) * L.Scale * k); invRy[i] = 1f / ((L.H / 2f + ext) * L.Scale * k);
                lr[i] = L.Color.Red; lg[i] = L.Color.Green; lb[i] = L.Color.Blue;
            }

            var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var pixels = new byte[w * h * 4];
            Parallel.For(0, h, row =>
            {
                uint seed = (uint)(row * 2654435761u) ^ 0x9E3779B9u;
                float y = row - m;
                int o = row * w * 4;
                for (int col = 0; col < w; col++)
                {
                    float x = col - m;
                    float r = bgR, g = bgG, b = bgB;
                    for (int i = 0; i < n; i++)
                    {
                        float ux = (x - cx[i]) * invRx[i], uy = (y - cy[i]) * invRy[i];
                        float t = (float)Math.Sqrt(ux * ux + uy * uy);
                        if (t >= 1f) continue;
                        float a = prof[i][(int)(t * ProfileSize)];
                        r += (lr[i] - r) * a; g += (lg[i] - g) * a; b += (lb[i] - b) * a;
                    }
                    pixels[o++] = Dither(r, ref seed);
                    pixels[o++] = Dither(g, ref seed);
                    pixels[o++] = Dither(b, ref seed);
                    pixels[o++] = 255;
                }
            });
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
            return bmp;
        }

        // TPDF: sum of two uniforms in (-0.5, 0.5) => triangular in (-1, 1); rounding to the nearest integer.
        private static byte Dither(float v, ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5; float u1 = (s & 0xFFFFFF) / 16777216f;
            s ^= s << 13; s ^= s >> 17; s ^= s << 5; float u2 = (s & 0xFFFFFF) / 16777216f;
            int q = (int)Math.Floor(v + (u1 + u2 - 1f) + 0.5f);
            return (byte)(q < 0 ? 0 : q > 255 ? 255 : q);
        }

        private void DrawFallback(SKCanvas c, Profile profile, float offX, float offY, float opacity, SKRect clip)
        {
            var Lights = profile.Lights;
            if (_fallbackShaders == null || _fallbackFor != profile.Id)
            {
                if (_fallbackShaders != null) foreach (var old in _fallbackShaders) if (old != null) old.Dispose();
                _fallbackFor = profile.Id;
                _fallbackShaders = new SKShader[Lights.Length];
                for (int i = 0; i < Lights.Length; i++) _fallbackShaders[i] = MakeGradient(Lights[i]);
            }
            c.Save();
            c.ClipRect(clip);
            c.DrawColor(Tokens.Bg, SKBlendMode.Src);
            for (int i = 0; i < Lights.Length; i++)
            {
                var L = Lights[i];
                float ext = 2.6f * L.Sigma;
                c.Save();
                c.Translate(L.Left + L.W / 2f + offX, L.Top + L.H / 2f + offY);
                c.Scale((L.W / 2f + ext) * L.Scale, (L.H / 2f + ext) * L.Scale);
                _grad.Shader = _fallbackShaders[i];
                _grad.Color = new SKColor(255, 255, 255, (byte)(opacity * 255));
                c.DrawRect(new SKRect(-1, -1, 1, 1), _grad);
                c.Restore();
            }
            c.Restore();
        }

        private static SKShader MakeGradient(LightDef L)
        {
            const int stops = 24;
            float ax = L.W / 2f, ext = 2.6f * L.Sigma;
            var colors = new SKColor[stops]; var pos = new float[stops];
            for (int i = 0; i < stops; i++)
            {
                float t = i / (float)(stops - 1);
                double a = i == stops - 1 ? 0 : 0.5 * Erfc((t * (ax + ext) - ax) / (L.Sigma * Math.Sqrt(2)));
                colors[i] = new SKColor(L.Color.Red, L.Color.Green, L.Color.Blue, (byte)Math.Round(Math.Max(0, Math.Min(1, a * L.Opacity)) * 255));
                pos[i] = t;
            }
            return SKShader.CreateRadialGradient(new SKPoint(0, 0), 1f, colors, pos, SKShaderTileMode.Clamp);
        }

        /// <summary>Blurred light as a small texture (1/4) that afterwards is only moved and scaled; the test player and the states sheet use it.</summary>
        public static SKImage MakeBlob(float w, float h, float sigma, SKColor color, out float pad)
        {
            const float q = 4f;
            pad = (float)Math.Ceiling(sigma * 2.6f);
            int bw = (int)Math.Ceiling((w + 2 * pad) / q), bh = (int)Math.Ceiling((h + 2 * pad) / q);
            using (var src = new SKBitmap(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul))
            using (var dst = new SKBitmap(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul))
            {
                using (var c = new SKCanvas(src)) using (var p = new SKPaint { IsAntialias = true, Color = color })
                { c.Clear(SKColors.Transparent); c.DrawOval(new SKRect(pad / q, pad / q, pad / q + w / q, pad / q + h / q), p); }
                using (var c = new SKCanvas(dst)) using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma / q, sigma / q) })
                { c.Clear(SKColors.Transparent); c.DrawBitmap(src, 0, 0, p); }
                return SKImage.FromBitmap(dst);
            }
        }

        // Abramowitz and Stegun 7.1.26 approximation (error < 1.5e-7), enough for a light profile.
        private static double Erfc(double x)
        {
            double z = Math.Abs(x), t = 1.0 / (1.0 + 0.3275911 * z);
            double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
            return x >= 0 ? 1 - erf : 1 + erf;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            var img = _image; _image = null; if (img != null) img.Dispose();
            if (_fallbackShaders != null) foreach (var s in _fallbackShaders) if (s != null) s.Dispose();
            _blit.Dispose(); _grad.Dispose();
        }
    }
}
