using System;
using System.Diagnostics;
using AceEngine.Util;
using PUi.Core;
using PUi.Gfx;
using PUi.Theme;
using SkiaSharp;

namespace PUi.App
{
    /// <summary>
    /// GPU warm-up at startup: the first time each kind of drawing is used (fill, border, text, shadow, fluid ring, lamp...) Skia compiles its
    /// programs and the screen freezes for 100-450 ms. Here everything the interface uses is drawn once, on a small off-screen surface and one step per frame,
    /// right after startup, so those hitches do not happen later when opening a screen, the keyboard or a channel.
    /// </summary>
    public sealed class GpuWarmup
    {
        public const int Steps = 6;
        private int _step;
        public bool Done { get { return _step >= Steps; } }
        public int StepsDone { get { return _step; } }
        public double TotalMs;

        public void Step(UiContext u, double now)
        {
            if (Done || u.Gpu == null) return;
            int step = _step++;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                using (var s = SpriteCache.NewSurface(u.Gpu, 640, 360))
                {
                    var c = s.Canvas;
                    c.Clear(SKColors.Transparent);
                    c.Scale(1 / 3f);
                    Draw(u, c, step, now);
                    c.Flush();
                }
            }
            catch (Exception ex) { AceLog.Warn("ui", "GPU warm-up, step " + step + ": " + ex.Message); }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            TotalMs += ms;
            AceLog.Info("ui", "GPU warm-up: step " + (step + 1) + "/" + Steps + " in " + (int)ms + " ms" + (Done ? " (total " + (int)TotalMs + " ms)" : ""));
        }

        private static Focusable Item(float x, float y, float w, float h, bool focused, float scale = 1.05f)
        {
            var f = new Focusable { R = new SKRect(x, y, x + w, y + h), FocusScale = scale };
            f.SetFocused(focused, 0, true);
            return f;
        }

        private static void Draw(UiContext u, SKCanvas c, int step, double now)
        {
            var gx = u.Gx;
            switch (step)
            {
                case 0:        // background and strip
                    u.Ambient.Draw(c, u.Gpu, now, 640, 360);
                    u.Fx.Divider(c, 200, 300, 1200, now);
                    break;
                case 1:        // category and Explore cards, sort button
                    foreach (bool foc in new[] { false, true })
                    {
                        Cards.Category(u, c, Item(200, 100, 300, 80, foc), "Categoria de ejemplo", now);
                        Cards.Explore(u, c, Item(540, 100, 300, 80, foc), "futbol", "Fútbol", now);
                        Cards.SortButton(u, c, Item(900, 100, 160, 54, foc, 1f), SortMode.Arrival, now);
                        Cards.SortButton(u, c, Item(1100, 100, 160, 54, foc, 1f), SortMode.AlphaAsc, now);
                    }
                    break;
                case 2:        // rows and recents in every state
                    foreach (bool foc in new[] { false, true })
                    {
                        Cards.Row(u, c, Item(200, 200, 1200, 92, foc, 1.015f), ChannelView.Make("Canal de ejemplo", "1080p", "73", Signal.Live), now);
                        Cards.Row(u, c, Item(200, 300, 1200, 92, foc, 1.015f), ChannelView.Make("Otro canal", "720p", "", Signal.Checking), now);
                        Cards.Row(u, c, Item(200, 400, 1200, 92, foc, 1.015f), ChannelView.Make("Sin senal", "", "", Signal.NoSignal), now);
                        Cards.Recent(u, c, Item(200, 500, 300, 132, foc, 1.06f), ChannelView.Make("Reciente de ejemplo", "FHD", "41", Signal.Live), now);
                    }
                    break;
                case 3:        // text at the interface's sizes and weights, icons, spinner, dot
                    {
                        float y = 60;
                        foreach (var sz in new[] { 15f, 16f, 17f, 18f, 22f, 24f, 25f, 26f, 30f, 44f, 56f })
                            foreach (var w in new[] { 400, 600, 700, 800 })
                            { gx.DrawText(c, "Liga de Campeones 1080p 0123456789", 100 + (w - 400) * 2.5f, y, sz, w, Tokens.Text); y += sz * 0.9f; if (y > 1000) y = 60; }
                        float x = 1300;
                        foreach (var ic in new[] { Icons.Tv, Icons.Search, Icons.Sliders, Icons.Chevron, Icons.People, Icons.ArrowDown, Icons.ArrowUp, Icons.Seeds, Icons.EyeOff, Icons.Back10, Icons.Forward10, Icons.Play, Icons.Pause, Icons.Backspace, Icons.Space, Icons.SortBars, Icons.Arrival, Icons.Close, Icons.Alert })
                        { gx.DrawIcon(c, ic, x, 100, 30, Tokens.Text, 1.8f); x += 40; if (x > 1800) x = 1300; }
                        u.Fx.Spinner(c, 1300, 300, 18, now, Tokens.Accent2);
                        u.Fx.PulseDot(c, 1400, 300, 4.5f, now, 10);
                        u.Fx.Shimmer(c, new SKRect(1300, 340, 1700, 400), 20, now);
                        gx.FillRR(c, new SKRect(1300, 440, 1700, 560), 30, Tokens.White(0.1f));
                        gx.StrokeRR(c, new SKRect(1300, 440, 1700, 560), 30, 1.5f, Tokens.WithAlpha(Tokens.Warn, 0.7f));
                        gx.StrokeArc(c, 1500, 700, 40, 6, Tokens.TextA(0.9f), -90, 240);
                        gx.Line(c, 1300, 800, 1700, 800, 3, Tokens.Text);
                        gx.FillCircle(c, 1500, 900, 30, Tokens.Accent);
                        gx.StrokeCircle(c, 1600, 900, 30, 2, Tokens.TextA(0.6f));
                    }
                    break;
                case 4:        // lava lamp of the channel loading
                    u.Lava.Draw(c, u.Gpu, now);
                    break;
                case 5:        // panels with gradients and shadows (keyboard, player panel)
                    {
                        var r = new SKRect(300, 500, 1500, 900);
                        u.Fx.Shadow(c, r, 36, 0.55f, 24, 64);
                        gx.FillRR(c, r, 36, Tokens.C(10, 12, 20, 0.80f));
                        gx.StrokeRR(c, r, 36, 1, Tokens.White(0.14f));
                        u.Fx.FluidRing(c, new SKRect(400, 600, 700, 680), 30, 1f, now);
                        gx.FillRR(c, new SKRect(0, 0, 1920, 1080), 0, Tokens.C(5, 6, 12, 0.28f));
                    }
                    break;
            }
        }
    }
}
