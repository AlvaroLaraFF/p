using System;
using PUi.Gfx;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>
    /// The cards and buttons of the phone and tablet layouts (design-movil), drawn with the same method as the TV's (Cards.Card: the body live, the text in a resting sprite, the fluid border when
    /// a remote or a keyboard has the focus). Sizes are the design's CSS pixels, which are logical units here. Cards.Recent / Category / Explore / Row / SortButton send their calls here when
    /// the interface is compact, so the screens ask for a card in one way on every device.
    /// </summary>
    public static class CompactCards
    {
        private static readonly SKColor[] Tints = { Tokens.Accent, Tokens.Blue, Tokens.Accent2, Tokens.Accent3 };

        /// <summary>The tint of the n-th Explore tile (the design cycles four colors).</summary>
        public static SKColor Tint(int n) { return Tints[((n % 4) + 4) % 4]; }

        // ---------------------------------------------------------------- status pieces (peers, spinner, quality)
        private static float ChipWidth(Gx gx, string q, float size) { return string.IsNullOrEmpty(q) ? 0 : gx.MeasureText(q, size, 600) + 16; }

        /// <summary>Width of the "dot and peers" / spinner / zero part.</summary>
        private static float StatusWidth(Gx gx, ChannelView v, float size)
        {
            switch (v.State)
            {
                case Signal.Live: return 7 + 6 + gx.MeasureText(v.Peers ?? "", size, 600);
                case Signal.Checking: case Signal.Pending: return 12;
                case Signal.NoSignal: return gx.MeasureText(v.Peers ?? "", size, 600);
            }
            return 0;
        }

        private static void ChipC(Gx gx, SKCanvas c, string text, float x, float midY, float size)
        {
            float w = gx.MeasureText(text, size, 600), h = gx.LineHeight(size, 600) + 4;
            var rect = new SKRect(x, midY - h / 2, x + w + 16, midY + h / 2);
            gx.FillRR(c, rect, 7, Tokens.White(0.08f));
            gx.DrawText(c, text, x + 8, gx.Baseline(rect.Top, h, size, 600), size, 600, Tokens.TextA(0.70f));
        }

        /// <summary>Draws the status (static parts) from x to the right, then the quality chip. Returns where the dot or the spinner is, for the live overlay (x of its center; NaN if none).</summary>
        private static float StatusStatic(Gx gx, SKCanvas c, ChannelView v, float x, float midY, float size, bool chip, out bool spinner)
        {
            float cx = float.NaN; spinner = false;
            float lh = gx.LineHeight(size, 600);
            switch (v.State)
            {
                case Signal.Live:
                    gx.FillCircle(c, x + 3.5f, midY, 3.5f, Tokens.Accent); cx = x + 3.5f;
                    gx.DrawText(c, v.Peers ?? "", x + 13, gx.Baseline(midY - lh / 2, lh, size, 600), size, 600, Tokens.TextA(0.70f));
                    x += 13 + gx.MeasureText(v.Peers ?? "", size, 600);
                    break;
                case Signal.Checking: case Signal.Pending:
                    gx.StrokeCircle(c, x + 6, midY, 5, 2, Tokens.White(0.16f)); cx = x + 6; spinner = true;
                    x += 12;
                    break;
                case Signal.NoSignal:
                    gx.DrawText(c, v.Peers ?? "", x, gx.Baseline(midY - lh / 2, lh, size, 600), size, 600, Tokens.TextA(0.70f));
                    x += gx.MeasureText(v.Peers ?? "", size, 600);
                    break;
            }
            if (chip && !string.IsNullOrEmpty(v.Quality)) { if (StatusWidth(gx, v, size) > 0) x += 10; ChipC(gx, c, v.Quality, x, midY, size); }
            return cx;
        }

        private static void StatusLive(UiContext u, SKCanvas c, ChannelView v, float cx, float midY, double now, float pulseMax)
        {
            if (float.IsNaN(cx)) return;
            if (v.State == Signal.Live) u.Fx.PulseDot(c, cx, midY, 3.5f, now, pulseMax);
            else if (v.State == Signal.Checking || v.State == Signal.Pending)
            {
                float ang = (float)((now % Tokens.SpinSecs) / Tokens.SpinSecs * 360.0);
                u.Gx.StrokeArc(c, cx, midY, 5, 2, Tokens.Accent2, ang - 45, 90);
            }
        }

        // ---------------------------------------------------------------- recent
        /// <summary>Recent channel: name (up to two lines), then peers or spinner and the quality. Blue border.</summary>
        public static void Recent(UiContext u, SKCanvas c, Focusable it, ChannelView v, double now, float alpha, bool warm)
        {
            var st = u.Style;
            float rad = st.Phone ? 22 : 24, padX = st.Phone ? 16 : 20, padY = st.Phone ? 16 : 18, small = st.Phone ? 13 : 14;
            var r = it.R;
            Cards.Card(u, c, it, rad, now, "crec|" + v.Key + "|" + (int)r.Width + "x" + (int)r.Height + "|" + (st.Phone ? "p" : "t"), 0.06f, Tokens.WithAlpha(Tokens.Blue, 0.75f), 1.5f, 24, 64,
                (cv, gx, rr, f) => RecentContent(cv, gx, rr, v, st.Phone),
                cv =>
                {
                    float chipH = u.Gx.LineHeight(small, 600) + 4;
                    StatusLive(u, cv, v, r.Left + padX + (v.State == Signal.Live ? 3.5f : 6f), r.Bottom - padY - chipH / 2, now, 7);
                }, alpha, true, warm);
        }

        private static void RecentContent(SKCanvas c, Gx gx, SKRect r, ChannelView v, bool phone)
        {
            float padX = phone ? 16 : 20, padY = phone ? 16 : 18, nameSize = phone ? 16 : 18, small = phone ? 13 : 14;
            float lineH = nameSize * 1.25f;
            var lines = gx.WrapText(v.Name ?? "", nameSize, 700, r.Width - 2 * padX, 2);
            for (int i = 0; i < lines.Count; i++)
                gx.DrawText(c, lines[i], r.Left + padX, gx.Baseline(r.Top + padY + i * lineH, lineH, nameSize, 700), nameSize, 700, Tokens.Text);
            float chipH = gx.LineHeight(small, 600) + 4;
            float midY = r.Bottom - padY - chipH / 2;
            bool spin;
            StatusStatic(gx, c, v, r.Left + padX, midY, small, true, out spin);
        }

        // ---------------------------------------------------------------- category and explore tiles
        public static void Category(UiContext u, SKCanvas c, Focusable it, string name, double now, bool warm)
        {
            var st = u.Style;
            float rad = st.Phone ? 18 : 20, size = st.Phone ? 16 : st.SideRail ? 18 : 17;
            float padL = st.Phone ? 18 : st.SideRail ? 22 : 20, padR = st.Phone ? 12 : st.SideRail ? 16 : 14, chev = st.Phone ? 18 : 20;
            Cards.Card(u, c, it, rad, now, "ccat|" + name + "|" + (int)it.R.Width + "x" + (int)it.R.Height, 0.05f, Tokens.White(0.09f), 1, 24, 64, (cv, gx, r, f) =>
            {
                string n = gx.Ellipsize(name ?? "", size, 700, r.Width - padL - padR - chev - 8);
                gx.DrawText(cv, n, r.Left + padL, gx.Baseline(r.Top, r.Height, size, 700), size, 700, Tokens.Text);
                gx.DrawIcon(cv, Icons.Chevron, r.Right - padR - chev / 2, r.Top + r.Height / 2, chev, Tokens.TextA(0.42f), 2f);
            }, null, 1f, true, warm);
        }

        public static void Explore(UiContext u, SKCanvas c, Focusable it, string kindId, string name, double now, bool warm)
        {
            var st = u.Style;
            float size = st.Phone ? 16 : st.SideRail ? 18 : 17;
            int n = it.Tag is int ? (int)it.Tag : 0;
            var tint = Tint(n);
            Cards.Card(u, c, it, 20, now, "cexp|" + kindId + "|" + n % 4 + "|" + (int)it.R.Width, 0.05f, Tokens.White(0.09f), 1, 24, 64, (cv, gx, r, f) =>
            {
                float midY = r.Top + r.Height / 2;
                gx.FillRR(cv, new SKRect(r.Left + 14, midY - 15, r.Left + 24, midY + 15), 5, Tokens.WithAlpha(tint, 0.85f));
                float tx = r.Left + 14 + 10 + 12;
                string t = gx.Ellipsize(name ?? "", size, 700, r.Right - 14 - tx);
                gx.DrawText(cv, t, tx, gx.Baseline(r.Top, r.Height, size, 700), size, 700, Tokens.Text);
            }, null, 1f, true, warm);
        }

        // ---------------------------------------------------------------- channel row
        public static float RowHeight(CompactStyle st) { return st.Phone ? 64 : 68; }
        public static float RowGap(CompactStyle st) { return st.Phone ? 10 : 12; }

        public static void Row(UiContext u, SKCanvas c, Focusable it, ChannelView v, double now, float alpha, bool warm)
        {
            var st = u.Style;
            var r = it.R;
            bool dead = v.State == Signal.NoSignal;
            float size = st.Phone ? 16 : 18, small = st.Phone ? 13 : 14;
            float padL = 18, padR = 16, rad = 20;
            Cards.Card(u, c, it, rad, now, "crow|" + v.Key + "|" + (int)r.Width + "x" + (int)r.Height, dead ? 0.03f : 0.06f, dead ? Tokens.White(0.06f) : Tokens.White(0.10f), 1, 20, 56,
                (cv, gx, rr, f) =>
                {
                    float chipW = ChipWidth(gx, v.Quality, small), stW = StatusWidth(gx, v, small);
                    float rightW = stW + (chipW > 0 ? (stW > 0 ? 10 : 0) + chipW : 0);
                    string name = gx.Ellipsize(v.Name ?? "", size, 700, rr.Width - padL - padR - rightW - 12);
                    gx.DrawText(cv, name, rr.Left + padL, gx.Baseline(rr.Top, rr.Height, size, 700), size, 700, Tokens.Text);
                    bool spin;
                    StatusStatic(gx, cv, v, rr.Right - padR - rightW, rr.Top + rr.Height / 2, small, true, out spin);
                },
                cv =>
                {
                    var gx = u.Gx;
                    float chipW = ChipWidth(gx, v.Quality, small), stW = StatusWidth(gx, v, small);
                    float rightW = stW + (chipW > 0 ? (stW > 0 ? 10 : 0) + chipW : 0);
                    float x = r.Right - padR - rightW + (v.State == Signal.Live ? 3.5f : 6f);
                    StatusLive(u, cv, v, x, r.Top + r.Height / 2, now, 7);
                }, alpha * (dead ? 0.4f : 1f), true, warm, null, false);
        }

        // ---------------------------------------------------------------- sort button
        /// <summary>Width of the sort button: a circle with the icon for "arrival", a pill with the mode's label otherwise.</summary>
        public static float SortWidth(Gx gx, SortMode m, float height)
        {
            if (m == SortMode.Arrival) return height;
            string lbl = Cards.SortLabel(m);
            float glyph = lbl != null ? gx.MeasureText(lbl, 14, 700) : 18 + 4 + 14;
            return 14 + 20 + 8 + glyph + 14;
        }

        public static void SortButton(UiContext u, SKCanvas c, Focusable it, SortMode mode, double now, bool warm, float alpha)
        {
            var r = it.R;
            Cards.Card(u, c, it, r.Height / 2, now, "csort|" + mode + "|" + (int)r.Width + "x" + (int)r.Height, 0.06f, Tokens.White(0.12f), 1, 20, 56, (cv, gx, rr, f) =>
            {
                float midY = rr.Top + rr.Height / 2;
                if (mode == SortMode.Arrival) { gx.DrawIcon(cv, Icons.SortArrows, (rr.Left + rr.Right) / 2, midY, 20, Tokens.Text, 1.8f); return; }
                float x = rr.Left + 14;
                gx.DrawIcon(cv, Icons.SortArrows, x + 10, midY, 20, Tokens.TextA(0.86f), 1.8f);
                x += 20 + 8;
                string lbl = Cards.SortLabel(mode);
                if (lbl != null) gx.DrawText(cv, lbl, x, gx.Baseline(rr.Top, rr.Height, 14, 700), 14, 700, Tokens.Text);
                else { gx.DrawIcon(cv, Icons.People, x + 9, midY, 18, Tokens.Accent, 1.8f); gx.DrawIcon(cv, Icons.ArrowDown, x + 18 + 4 + 7, midY, 14, Tokens.TextA(0.86f), 2f); }
            }, null, alpha, true, warm);
        }

        // ---------------------------------------------------------------- round buttons (back, cast)
        /// <summary>A round icon button of a header: a faint disc (optional), a thin border (optional) and the icon. Not focusable: the finger is its pointer.</summary>
        public static void RoundButton(UiContext u, SKCanvas c, SKRect r, Icon icon, float iconSize, float stroke, float fillAlpha, float borderAlpha, SKColor iconColor)
        {
            var gx = u.Gx;
            if (fillAlpha > 0.001f) gx.FillRR(c, r, r.Height / 2, Tokens.White(fillAlpha));
            if (borderAlpha > 0.001f) gx.StrokeRR(c, r, r.Height / 2, 1, Tokens.White(borderAlpha));
            gx.DrawIcon(c, icon, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, iconSize, iconColor, stroke);
        }
    }
}
