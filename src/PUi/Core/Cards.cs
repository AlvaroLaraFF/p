using System;
using PUi.Gfx;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    public enum Signal { Live, Checking, NoSignal, Pending }

    /// <summary>Data of a channel card (recent or row): what changes the static drawing.</summary>
    public struct ChannelView
    {
        public string Name, Quality, Peers;
        public Signal State;
        /// <summary>Identifies the static drawing (sprite cache key).</summary>
        public string Key;

        public static ChannelView Make(string name, string quality, string peers, Signal state)
        {
            var v = new ChannelView { Name = name ?? "", Quality = quality ?? "", Peers = peers ?? "", State = state };
            v.Key = v.Name + "|" + v.Quality + "|" + v.Peers + "|" + state;
            return v;
        }
    }

    /// <summary>
    /// Drawing of the design's cards (132 px recents, 80 px categories and kinds, 92 px rows) with the same method as the technical test:
    /// the body and the text are drawn once into a resting sprite and a focused one (already at the focus scale, sharp), and per frame only the
    /// sprite, the shadow, the fluid border and whatever moves (spinner, pulse, shimmer) are stamped.
    /// </summary>
    public static class Cards
    {
        // ---------------------------------------------------------------- common skeleton
        public delegate void ContentDrawer(SKCanvas c, Gx gx, SKRect r, float f);

        /// <summary>
        /// Card with focus. The body (fill and border) is painted live, which is cheap and blends continuously between rest and focused (as two overlapping sprites,
        /// the translucent fill would add up twice and give a brightness spike at the start and end of the animation: that was the flicker). The expensive part (text,
        /// labels, icons) goes into a sprite, sharp at normal scale or at the focus scale. Shadow and fluid border as sprites. 'live' paints what moves.
        /// contentBounds limits the content sprite (by default, the whole card) and needFocusSprite=false avoids the second sprite (rows: they scale only 1.5 %).
        /// warm=true only creates the sprites (pre-warming the cache without drawing) so moving the focus does not create textures in the middle of the movement.
        /// </summary>
        public static void Card(UiContext u, SKCanvas c, Focusable it, float rad, double now, string key, float baseFill, SKColor restBorder, float borderW,
            float shadowDy, float shadowBlur, ContentDrawer content, Action<SKCanvas> live, float alpha = 1f, bool ring = true, bool warm = false,
            SKRect? contentBounds = null, bool needFocusSprite = true)
        {
            var r = it.R;
            var cb = contentBounds ?? r;
            var bounds = new SKRect((float)Math.Floor(cb.Left) - 2, (float)Math.Floor(cb.Top) - 2, (float)Math.Ceiling(cb.Right) + 2, (float)Math.Ceiling(cb.Bottom) + 2);
            string k0 = key + "|" + (int)bounds.Width + "x" + (int)bounds.Height;
            if (warm)
            {
                u.Sprites.Get(k0 + "|A", bounds, u.K, cv => content(cv, u.Gx, r, 0));
                if (needFocusSprite) u.Sprites.Get(k0 + "|B", bounds, u.K * it.FocusScale, cv => content(cv, u.Gx, r, 1));
                u.Fx.WarmShadow(r, rad, shadowBlur);
                u.Fx.WarmHalo(r, rad);
                return;
            }
            float f = it.Focus.Value(now), rg = it.Ring.Value(now);
            float scale = 1 + (it.FocusScale - 1) * f;
            float cx = it.CenterX, cy = it.CenterY;
            var gx = u.Gx;
            c.Save();
            c.Translate(cx, cy); c.Scale(scale); c.Translate(-cx, -cy);
            if (f > 0.01f) u.Fx.Shadow(c, r, rad, 0.55f * f * alpha, shadowDy, shadowBlur);
            gx.FillRR(c, r, rad, Tokens.White((baseFill + (0.13f - baseFill) * f) * alpha));
            if (restBorder.Alpha > 0 && f < 0.999f) gx.StrokeRR(c, r, rad, borderW, Tokens.WithAlpha(restBorder, restBorder.Alpha / 255f * (1 - f) * alpha));
            bool sharp = needFocusSprite && f > 0.5f;
            var img = sharp
                ? u.Sprites.TryGet(k0 + "|B", bounds, u.K * it.FocusScale, cv => content(cv, gx, r, 1))
                : u.Sprites.TryGet(k0 + "|A", bounds, u.K, cv => content(cv, gx, r, 0));
            if (img == null && sharp) img = u.Sprites.TryGet(k0 + "|A", bounds, u.K, cv => content(cv, gx, r, 0));     // the sharp one is not ready yet: the normal one is (already pre-warmed), or nothing
            if (img != null) gx.DrawImage(c, img, bounds, alpha);
            if (ring && rg > 0.01f) u.Fx.FluidRing(c, r, rad, rg, now);
            if (live != null) live(c);
            c.Restore();
        }

        // ---------------------------------------------------------------- pieces
        /// <summary>Quality label ("1080p"): white background .07, radius 8, padding 2x9. Returns the width.</summary>
        public static float Chip(Gx gx, SKCanvas c, string text, float x, float midY, float size = 15, int weight = 600, float alpha = 0.66f)
        {
            float w = gx.MeasureText(text, size, weight);
            float h = LineHeight(gx, size, weight) + 4;
            var rect = new SKRect(x, midY - h / 2, x + w + 18, midY + h / 2);
            gx.FillRR(c, rect, 8, Tokens.White(0.07f));
            gx.DrawText(c, text, x + 9, gx.Baseline(rect.Top, h, size, weight), size, weight, Tokens.TextA(alpha));
            return w + 18;
        }

        public static float LineHeight(Gx gx, float size, int weight) { return gx.LineHeight(size, weight); }

        // ---------------------------------------------------------------- sort button (header of the channel lists)
        public static string SortLabel(SortMode m) { return m == SortMode.AlphaAsc ? "A–Z" : m == SortMode.AlphaDesc ? "Z–A" : null; }

        public static float SortButtonWidth(Gx gx, SortMode m)
        {
            string lbl = SortLabel(m);
            float glyphW = lbl != null ? gx.MeasureText(lbl, 17, 700) : m == SortMode.PeersDesc ? 22 + 4 + 18 : 22;
            return 22 + 22 + 12 + glyphW + 22;
        }

        /// <summary>Sort button: sort icon and the mode (arrival, A-Z, Z-A, peers). Focusable, with fluid border.</summary>
        public static void SortButton(UiContext u, SKCanvas c, Focusable it, SortMode mode, double now, float height = 54, bool warm = false, float alpha = 1f)
        {
            if (u.Compact) { CompactCards.SortButton(u, c, it, mode, now, warm, alpha); return; }
            Card(u, c, it, height / 2, now, "sortbtn|" + mode, 0.06f, Tokens.White(0.10f), 1, 20, 56, (cv, gx, r, f) =>
            {
                float midY = r.Top + r.Height / 2, x = r.Left + 22;
                gx.DrawIcon(cv, Icons.SortBars, x + 11, midY, 22, Tokens.TextA(0.86f), 1.8f);
                x += 22 + 12;
                string lbl = SortLabel(mode);
                if (lbl != null) gx.DrawText(cv, lbl, x, gx.Baseline(r.Top, r.Height, 17, 700), 17, 700, Tokens.Text);
                else if (mode == SortMode.PeersDesc) { gx.DrawIcon(cv, Icons.People, x + 11, midY, 22, Tokens.Accent, 1.8f); gx.DrawIcon(cv, Icons.ArrowDown, x + 22 + 4 + 9, midY, 18, Tokens.TextA(0.86f), 2f); }
                else gx.DrawIcon(cv, Icons.Arrival, x + 11, midY, 22, Tokens.Accent, 1.8f);
            }, null, alpha, true, warm);
        }

        // ---------------------------------------------------------------- recent (132 px, blue border)
        public static void Recent(UiContext u, SKCanvas c, Focusable it, ChannelView v, double now, float alpha = 1f, bool warm = false)
        {
            if (u.Compact) { CompactCards.Recent(u, c, it, v, now, alpha, warm); return; }
            var r = it.R;
            Card(u, c, it, 28, now, "rec|" + v.Key, 0.06f, Tokens.WithAlpha(Tokens.Blue, 0.75f), 1.5f, 24, 64,
                (cv, gx, rr, f) => RecentContent(cv, gx, rr, v),
                cv => RecentLive(u, cv, r, v, now), alpha, true, warm);
        }

        private static void RecentContent(SKCanvas c, Gx gx, SKRect r, ChannelView v)
        {
            float chipH = 20.5f + 4, rowBottom = r.Bottom - 22, chipTop = rowBottom - chipH, midY = chipTop + chipH / 2;
            float x = r.Left + 26;
            string name = gx.Ellipsize(v.Name ?? "", 21, 700, r.Width - 52);
            gx.DrawText(c, name, r.Left + 26, gx.Baseline(r.Top + 22, 26.25f, 21, 700), 21, 700, Tokens.Text);
            if (v.State == Signal.Live)
            {
                gx.FillCircle(c, x + 3.5f, midY, 3.5f, Tokens.Accent);
                gx.DrawText(c, v.Peers ?? "", x + 14, gx.Baseline(chipTop, chipH, 15, 600), 15, 600, Tokens.TextA(0.66f));
                x += 14 + gx.MeasureText(v.Peers ?? "", 15, 600) + 12;
            }
            else if (v.State == Signal.Checking || v.State == Signal.Pending)
            {
                gx.StrokeCircle(c, x + 6.5f, midY, 5.5f, 2, Tokens.White(0.16f));
                x += 13 + 12;
            }
            else if (v.State == Signal.NoSignal)
            {
                gx.StrokeCircle(c, x + 6, midY, 5, 2, Tokens.TextA(0.5f));
                x += 12 + 12;
            }
            if (!string.IsNullOrEmpty(v.Quality)) Chip(gx, c, v.Quality, x, midY);
        }

        private static void RecentLive(UiContext u, SKCanvas c, SKRect r, ChannelView v, double now)
        {
            float chipH = 20.5f + 4, rowBottom = r.Bottom - 22, midY = rowBottom - chipH / 2;
            float x = r.Left + 26;
            if (v.State == Signal.Live) u.Fx.PulseDot(c, x + 3.5f, midY, 3.5f, now, 8);
            else if (v.State == Signal.Checking || v.State == Signal.Pending)
            {
                float ang = (float)((now % Tokens.SpinSecs) / Tokens.SpinSecs * 360.0);
                u.Gx.StrokeArc(c, x + 6.5f, midY, 5.5f, 2, Tokens.Accent2, ang - 45, 90);
            }
        }

        // ---------------------------------------------------------------- category (80 px, arrow)
        public static void Category(UiContext u, SKCanvas c, Focusable it, string name, double now, bool warm = false)
        {
            if (u.Compact) { CompactCards.Category(u, c, it, name, now, warm); return; }
            Card(u, c, it, 24, now, "cat|" + name, 0.05f, Tokens.White(0.09f), 1, 24, 64, (cv, gx, r, f) =>
            {
                string n = gx.Ellipsize(name ?? "", 23, 700, r.Width - 28 - 22 - 22 - 12);
                gx.DrawText(cv, n, r.Left + 28, gx.Baseline(r.Top, r.Height, 23, 700), 23, 700, Tokens.Text);
                gx.DrawIcon(cv, Icons.Chevron, r.Right - 22 - 11, r.Top + r.Height / 2, 22, Tokens.TextA(0.42f), 2f);
            }, null, 1f, true, warm);
        }

        /// <summary>Explore kind: icon, name and arrow.</summary>
        public static void Explore(UiContext u, SKCanvas c, Focusable it, string kindId, string name, double now, bool warm = false)
        {
            if (u.Compact) { CompactCards.Explore(u, c, it, kindId, name, now, warm); return; }
            Card(u, c, it, 24, now, "exp|" + kindId, 0.05f, Tokens.White(0.09f), 1, 24, 64, (cv, gx, r, f) =>
            {
                float midY = r.Top + r.Height / 2;
                gx.DrawIcon(cv, Icons.ForKind(kindId), r.Left + 26 + 14, midY, 28, Tokens.TextA(0.45f), 1.6f);
                string n = gx.Ellipsize(name ?? "", 23, 700, r.Width - 26 - 28 - 18 - 18 - 22 - 22);
                gx.DrawText(cv, n, r.Left + 26 + 28 + 18, gx.Baseline(r.Top, r.Height, 23, 700), 23, 700, Tokens.Text);
                gx.DrawIcon(cv, Icons.Chevron, r.Right - 22 - 11, midY, 22, Tokens.TextA(0.42f), 2f);
            }, null, 1f, true, warm);
        }

        // ---------------------------------------------------------------- channel row (92 px)
        public static void Row(UiContext u, SKCanvas c, Focusable it, ChannelView v, double now, float alpha = 1f, bool warm = false)
        {
            if (u.Compact) { CompactCards.Row(u, c, it, v, now, alpha, warm); return; }
            var r = it.R;
            bool dead = v.State == Signal.NoSignal;
            float baseFill = dead ? 0.03f : 0.05f;
            var border = dead ? Tokens.White(0.06f) : Tokens.White(0.09f);
            var gx = u.Gx;
            // sprite for the left part only (name, quality, shield): rows are very wide and almost all of it is empty
            float rightW = RowRightWidth(gx, v);
            float leftW = RowLeftWidth(gx, v, r.Width - 32 - 32 - rightW - 24);
            var cb = new SKRect(r.Left, r.Top, r.Left + 32 + leftW + 8, r.Bottom);
            Card(u, c, it, 24, now, "row|" + v.Key + "|" + (int)r.Width, baseFill, border, 1, 20, 56,
                (cv, g, rr, f) => RowLeft(cv, g, rr, v, rr.Width - 32 - 32 - rightW - 24),
                cv => RowLive(u, cv, r, v, now), alpha * (dead ? 0.4f : 1f), true, warm, cb, false);
        }

        private static float RowRightWidth(Gx gx, ChannelView v)
        {
            if (v.State == Signal.Live) return gx.MeasureText(v.Peers ?? "", 18, 600) + 9 + 8;
            if (v.State == Signal.Checking || v.State == Signal.Pending) return 16;
            if (v.State == Signal.NoSignal) return gx.MeasureText(v.Peers ?? "", 18, 600);
            return 0;
        }

        private static float RowLeftWidth(Gx gx, ChannelView v, float availName)
        {
            string name = gx.Ellipsize(v.Name ?? "", 25, 700, availNameFor(gx, v, availName));
            float w = gx.MeasureText(name, 25, 700);
            if (!string.IsNullOrEmpty(v.Quality)) w += 16 + gx.MeasureText(v.Quality, 15, 600) + 18;
            return w;
        }

        private static float availNameFor(Gx gx, ChannelView v, float avail)
        {
            float chipW = string.IsNullOrEmpty(v.Quality) ? 0 : gx.MeasureText(v.Quality, 15, 600) + 18;
            return avail - (chipW > 0 ? chipW + 16 : 0);
        }

        private static void RowLeft(SKCanvas c, Gx gx, SKRect r, ChannelView v, float availName)
        {
            float midY = r.Top + r.Height / 2;
            float x = r.Left + 32;
            string name = gx.Ellipsize(v.Name ?? "", 25, 700, availNameFor(gx, v, availName));
            gx.DrawText(c, name, x, gx.Baseline(r.Top, r.Height, 25, 700), 25, 700, Tokens.Text);
            x += gx.MeasureText(name, 25, 700);
            if (!string.IsNullOrEmpty(v.Quality)) { x += 16; x += Chip(gx, c, v.Quality, x, midY); }
        }

        /// <summary>Right side of the row (peers, checking or no signal): painted live, it is a short text or a shape.</summary>
        private static void RowLive(UiContext u, SKCanvas c, SKRect r, ChannelView v, double now)
        {
            var gx = u.Gx;
            float midY = r.Top + r.Height / 2, rightEdge = r.Right - 32;
            if (v.State == Signal.Checking || v.State == Signal.Pending) u.Fx.Spinner(c, rightEdge - 8, midY, 16, now, Tokens.Accent2);     // only the small spinner (no shimmer strip)
            else if (v.State == Signal.Live)
            {
                string p = v.Peers ?? "";
                float tw = gx.MeasureText(p, 18, 600);
                gx.DrawText(c, p, rightEdge - tw, gx.Baseline(r.Top, r.Height, 18, 600), 18, 600, Tokens.TextA(0.70f));
                u.Fx.PulseDot(c, rightEdge - tw - 9 - 4, midY, 4, now, 10);
            }
            else if (v.State == Signal.NoSignal)
            {
                // no signal (only without "solo vivos"): a 0 without a dot, dimmed along with the row
                string p = v.Peers ?? "";
                gx.DrawText(c, p, rightEdge - gx.MeasureText(p, 18, 600), gx.Baseline(r.Top, r.Height, 18, 600), 18, 600, Tokens.TextA(0.70f));
            }
        }
    }
}
