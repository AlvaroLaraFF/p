using System;
using PUi.Core;
using PUi.Gfx;
using PUi.Theme;
using SkiaSharp;

namespace PDesktop
{
    /// <summary>
    /// States sheet (equivalent of the design's States sheet) drawn with the same PUi components: focus, loading, states and motion. It serves to compare
    /// each piece on its own with the design and as a visual catalog.
    /// </summary>
    public static class StatesSheet
    {
        public static SKBitmap Render(UiContext u, double t)
        {
            const int W = 1920, H = 1600;
            var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(bmp))
            {
                var gx = u.Gx;
                u.PixelW = W; u.PixelH = H;
                c.Clear(Tokens.Bg);
                // background lights of the sheet's design
                gx.DrawImage(c, Ambient.MakeBlob(960, 760, 170, SKColor.Parse("#7B3DFF"), out float pa), new SKRect(-240 - pa, -260 - pa, -240 + 960 + pa, -260 + 760 + pa), 0.28f, SKFilterQuality.Medium);
                gx.DrawImage(c, Ambient.MakeBlob(1000, 760, 170, SKColor.Parse("#17CDB4"), out float pb), new SKRect(W - 780 - pb, H - 480 - pb, W - 780 + 1000 + pb, H - 480 + 760 + pb), 0.20f, SKFilterQuality.Medium);
                c.Save(); c.Translate(80, 64);
                float y = 0;
                // ---- Focus with the remote
                Label(gx, c, "ENFOQUE CON EL MANDO", 0, y); y += 40;
                float x = 0, colW = (1760 - 3 * 48) / 4f;
                CardAt(u, c, new SKRect(x + 24, y + 12, x + 24 + 302.4f, y + 12 + 132), 28, t, false, true, "DAZN La Liga 1 FHD", "41", "1080p", 1.06f); Caption(gx, c, "Reciente · reposo, borde azul", x, y + 190); x += colW + 48;
                CardAt(u, c, new SKRect(x + 24, y + 12, x + 24 + 302.4f, y + 12 + 132), 28, t, true, true, "M+ Liga de Campeones 1080p", "73", "1080p", 1.06f); Caption(gx, c, "Reciente · enfocado, borde fluido", x, y + 190); x += colW + 48;
                CardAt(u, c, new SKRect(x + 24, y + 32, x + 24 + 305.6f, y + 32 + 80), 24, t, false, false, "Movistar Deportes", null, null, 1.05f); Caption(gx, c, "Categoría · reposo", x, y + 190); x += colW + 48;
                CardAt(u, c, new SKRect(x + 24, y + 32, x + 24 + 305.6f, y + 32 + 80), 24, t, true, false, "Liga de Campeones", null, null, 1.05f); Caption(gx, c, "Categoría · enfocada, borde fluido", x, y + 190);
                y += 250;
                // ---- Loading
                Label(gx, c, "CARGA", 0, y); y += 40;
                colW = (1760 - 4 * 40) / 5f; x = 0;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 120)); u.Fx.Spinner(c, x + colW / 2, y + 60, 16, t, Tokens.Accent2); Caption(gx, c, "Giro pequeño · 0,9 s", x, y + 150); x += colW + 40;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 120)); BufferRingMini(u, c, x + colW / 2, y + 60, t); Caption(gx, c, "Búfer · aro grueso con borde fluido; solo velo", x, y + 150); x += colW + 40;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 120), 0.04f); u.Fx.Shimmer(c, new SKRect(x + 28, y + 28, x + 28 + (colW - 56) * 0.7f, y + 48), 10, t); u.Fx.Shimmer(c, new SKRect(x + 28, y + 78, x + 28 + (colW - 56) * 0.2f, y + 92), 7, t); Caption(gx, c, "Esqueleto con brillo · 1,4 s", x, y + 150); x += colW + 40;
                {
                    var r = new SKRect(x, y, x + colW, y + 120);
                    gx.FillRR(c, r, 24, Tokens.BgPlayerLoading); c.Save(); using (var rr = new SKRoundRect(r, 24, 24)) c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
                    c.Translate(r.Left, r.Top); c.Scale(r.Width / 1920f, r.Height / 1080f);
                    u.Lava.Draw(c, null, t);
                    c.Restore();
                    gx.StrokeRR(c, r, 24, 1, Tokens.White(0.09f)); Caption(gx, c, "Carga de canal · lámpara de lava", x, y + 150); x += colW + 40;
                }
                Panel(gx, c, new SKRect(x, y, x + colW, y + 120)); u.Fx.Spinner(c, x + 28 + 9, y + 60, 18, t, Tokens.Accent2);
                gx.FillRR(c, new SKRect(x + 28 + 34, y + 57, x + colW - 28, y + 63), 3, Tokens.White(0.10f)); gx.FillRR(c, new SKRect(x + 28 + 34, y + 57, x + 28 + 34 + (colW - 90) * 0.83f, y + 63), 3, Tokens.Accent2);
                Caption(gx, c, "Progreso de vivos", x, y + 150);
                y += 220;
                // ---- States
                Label(gx, c, "ESTADOS", 0, y); y += 40;
                x = 0;
                Pill(gx, c, x, y, 150); u.Fx.PulseDot(c, x + 34, y + 26, 4, t, 10); gx.DrawText(c, "73", x + 54, gx.Baseline(y, 52, 17, 600), 17, 600, Tokens.TextA(0.7f)); x += 180;
                Pill(gx, c, x, y, 70); u.Fx.Spinner(c, x + 35, y + 26, 16, t, Tokens.Accent2); x += 100;
                Pill(gx, c, x, y, 70, 0.04f); gx.StrokeCircle(c, x + 35, y + 26, 6, 2, Tokens.TextA(0.5f)); x += 100;
                // switch
                { float k = (float)(0.5 + 0.5 * Math.Sin(t * 2)); var track = new SKRect(x + 20, y + 5, x + 96, y + 47); gx.FillRR(c, track, 21, Tokens.Lerp(Tokens.White(0.16f), Tokens.Accent, k)); gx.FillCircle(c, track.Left + 21 + 34 * k, y + 26, 16, Tokens.Lerp(Tokens.TextA(0.85f), Tokens.KnobOn, k)); }
                Caption(gx, c, "Interruptor", x + 120, y + 14);
                y += 100;
                // ---- Motion
                Label(gx, c, "MOVIMIENTO", 0, y); y += 40;
                colW = (1760 - 2 * 40) / 3f; x = 0;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 180), 0.04f, 28);
                { var r = new SKRect(x + colW / 2 - 130, y + 40, x + colW / 2 + 130, y + 140); gx.FillRR(c, r, 22, Tokens.White(0.10f)); u.Fx.FluidRing(c, r, 22, 1f, t); gx.DrawText(c, "Teledeporte", r.Left + 20, gx.Baseline(r.Bottom - 18 - 24, 24, 17, 700), 17, 700, Tokens.Text); }
                Caption(gx, c, "Borde fluido · una vuelta cada 7 s", x, y + 210); x += colW + 40;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 180), 0.04f, 28); u.Fx.Divider(c, x + 40, y + 89, colW - 80, t); Caption(gx, c, "Franja · el color se desliza en 16 s", x, y + 210); x += colW + 40;
                Panel(gx, c, new SKRect(x, y, x + colW, y + 180), 0.03f, 28);
                { float dx = (float)(30 * Math.Sin(t * 1.25)); gx.FillRR(c, new SKRect(x + 60 + dx, y + 30, x + 300 + dx, y + 150), 60, Tokens.C(123, 61, 255, 0.35f)); gx.FillRR(c, new SKRect(x + colW - 280 - dx * 2, y + 40, x + colW - 60 - dx * 2, y + 160), 60, Tokens.C(23, 205, 180, 0.30f));
                  var f = new SKRect(x + 40 + (colW - 180) * (float)(0.5 + 0.5 * Math.Sin(t * 1.25)), y + 60, x + 140 + (colW - 180) * (float)(0.5 + 0.5 * Math.Sin(t * 1.25)), y + 120); gx.FillRR(c, f, 16, Tokens.White(0.14f)); gx.StrokeRR(c, f, 16, 2, Tokens.White(0.8f)); }
                Caption(gx, c, "Paralaje · el fondo se mueve al contrario que el foco", x, y + 210);
                c.Restore();
            }
            return bmp;
        }

        private static void Label(Gx gx, SKCanvas c, string s, float x, float y) { gx.DrawText(c, s, x, gx.Baseline(y, 22, 16, 700), 16, 700, Tokens.TextA(0.62f), 1.28f); }
        private static void Caption(Gx gx, SKCanvas c, string s, float x, float y) { gx.DrawText(c, s, x, gx.Baseline(y, 22, 16, 600), 16, 600, Tokens.TextA(0.62f)); }
        private static void Panel(Gx gx, SKCanvas c, SKRect r, float fill = 0.05f, float rad = 24) { gx.FillRR(c, r, rad, Tokens.White(fill)); gx.StrokeRR(c, r, rad, 1, Tokens.White(0.09f)); }
        private static void Pill(Gx gx, SKCanvas c, float x, float y, float w, float fill = 0.06f) { gx.FillRR(c, new SKRect(x, y, x + w, y + 52), 26, Tokens.White(fill)); }

        private static void BufferRingMini(UiContext u, SKCanvas c, float cx, float cy, double t)
        {
            float angle = (float)((t % 2.4) / 2.4 * 360.0);
            var m = SKMatrix.MakeRotationDegrees(angle - 90, cx, cy);
            using (var sh = SKShader.CreateSweepGradient(new SKPoint(cx, cy), Tokens.RingColors, Tokens.RingPositions, m))
            {
                var p = u.Gx.Paint; p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 10; p.Shader = sh; p.Color = SKColors.White; c.DrawCircle(cx, cy, 27, p); p.Shader = null; p.Style = SKPaintStyle.Fill;
            }
        }

        private static void CardAt(UiContext u, SKCanvas c, SKRect r, float rad, double t, bool focused, bool recent, string name, string peers, string q, float scale)
        {
            var it = new Focusable { Id = name, R = r, FocusScale = scale };
            it.SetFocused(focused, 0, true);
            if (recent) Cards.Recent(u, c, it, ChannelView.Make(name, q, peers, Signal.Live), t);
            else Cards.Category(u, c, it, name, t);
        }
    }
}
