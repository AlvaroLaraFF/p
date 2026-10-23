using System;
using System.Collections.Generic;
using PUi.Core;
using PUi.Gfx;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// The phone and tablet chrome of the screens with sections: the sections along the bottom (phone, tablet in portrait: a floating pill) or down the left (tablet in landscape: a rail like the
    /// TV's), the back arrow of the subpages and the cast button (only while a cast service is available). The screens draw their own header and content on top of this; when the surface
    /// changes (rotation, split screen) the chrome is placed again and the screen rebuilds itself (OnSurfaceChanged) without losing its place. None of it runs on the TV.
    /// </summary>
    public abstract partial class SectionScreen
    {
        private ScreenMetrics _laidFor;

        /// <summary>The sections are in a bar along the bottom (phone, tablet in portrait).</summary>
        protected bool BottomBar { get { return Ctx != null && Ctx.Compact && !Ctx.Style.SideRail; } }

        /// <summary>The canvas in logical units: 1920 x 1080 on a TV, the phone's or tablet's own size otherwise.</summary>
        protected SKRect CanvasRect { get { return Ctx == null || !Ctx.Compact ? new SKRect(0, 0, Tokens.W, Tokens.H) : new SKRect(0, 0, Ctx.CW, Ctx.CH); } }

        /// <summary>The surface changed (rotation, split screen, insets) on a phone or a tablet: lay the page out again for the new size. Focus, scroll and the data are kept.</summary>
        protected virtual void OnSurfaceChanged(double now) { }

        /// <summary>Header drawn over the fixed part of the page (the logo, the title, the back arrow, the search field), outside the scrolled content. Phones and tablets only.</summary>
        protected virtual void DrawHeader(SKCanvas c, double now) { }

        /// <summary>The page has a back arrow before its title (subpages: a category, a list of results).</summary>
        protected virtual bool HasBackArrow { get { return !IsRoot; } }

        /// <summary>Where the page's cast button goes (null: this page has none). Drawn and touchable only while the cast service is available.</summary>
        protected virtual SKRect? CastBox { get { return null; } }

        private void LayoutNav()
        {
            var st = Ctx.Style;
            for (int i = 0; i < 3; i++) RailItems[i].R = st.NavItem[i];
        }

        // ---------------------------------------------------------------- touch targets of the chrome
        private void CollectChromeTargets(List<TouchTarget> into)
        {
            var st = Ctx.Style;
            if (HasBackArrow) into.Add(new TouchTarget { Id = "back", Rect = st.BackBox, Radius = st.BackBox.Height / 2, Tap = () => GoBack() });
            var cast = CastBox;
            if (cast.HasValue && Ctx.Cast.IsAvailable) into.Add(new TouchTarget { Id = "cast", Rect = cast.Value, Radius = cast.Value.Height / 2, Tap = () => Ctx.CastUi.Open(null) });
        }

        // ---------------------------------------------------------------- drawing
        /// <summary>The back arrow (a plain icon on the page: no disc, as in the design) and the cast button.</summary>
        protected void DrawChrome(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            if (HasBackArrow) CompactCards.RoundButton(Ctx, c, st.BackBox, Icons.Back, 24, 2f, 0f, 0f, Tokens.Text);
            DrawCast(c);
        }

        protected void DrawCast(SKCanvas c)
        {
            var cast = CastBox;
            if (!cast.HasValue || !Ctx.Cast.IsAvailable) return;
            bool rail = Ctx.Style.SideRail;
            // while something is being sent (or a device is waiting for a channel) the button is aqua: it is the way back to the controls
            if (Ctx.CastUi.IsCasting || Ctx.CastUi.Armed != null)
            {
                var r = cast.Value;
                Ctx.Gx.FillRR(c, r, r.Height / 2, Tokens.C(63, 232, 207, 0.16f));
                Ctx.Gx.StrokeRR(c, r, r.Height / 2, 1, Tokens.C(63, 232, 207, 0.55f));
                Ctx.Gx.DrawIcon(c, Icons.Cast, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, rail ? 24 : 22, Tokens.Accent, 1.8f);
                return;
            }
            CompactCards.RoundButton(Ctx, c, cast.Value, Icons.Cast, rail ? 24 : 22, 1.8f, rail ? 0f : 0.06f, rail ? 0f : 0.12f, rail ? Tokens.TextA(0.66f) : Tokens.Text);
        }

        /// <summary>The user chose a channel in a list: while it is being sent to another screen (or a screen is waiting for one) it goes there and the list does not open the player. Never on the TV.</summary>
        protected bool CastRedirect(AceEngine.App.Tv.Channel channel) { return Ctx != null && Ctx.Compact && Ctx.CastUi.TryRedirect(channel); }

        private void DrawNavCompact(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            var gx = Ctx.Gx;
            var b = st.NavBox;
            var bounds = new SKRect((float)Math.Floor(b.Left) - 4, (float)Math.Floor(b.Top) - 4, (float)Math.Ceiling(b.Right) + 4, (float)Math.Ceiling(b.Bottom) + 4);
            var img = Ctx.Sprites.Get("cnav|" + Ctx.Kind + "|" + (int)Section + "|" + (int)st.W + "x" + (int)st.H, bounds, Ctx.K, cv => DrawNavStatic(cv));
            gx.DrawImage(c, img, bounds);
            for (int i = 0; i < 3; i++)
            {
                var it = RailItems[i];
                float f = it.Focus.Value(now), ring = it.Ring.Value(now);
                bool active = i == (int)Section;
                if (f > 0.01f)
                {
                    if (!active) gx.FillRR(c, it.R, st.NavItemRadius, Tokens.White(0.12f * f));
                    gx.DrawIcon(c, RailIcon(i), it.CenterX, it.CenterY, st.NavIcon, Tokens.TextA(f), 1.8f);
                }
                if (ring > 0.01f) Ctx.Fx.FluidRing(c, it.R, st.NavItemRadius, ring, now);
            }
            if (st.SideRail) DrawCast(c);
        }

        private void DrawNavStatic(SKCanvas c)
        {
            var st = Ctx.Style;
            var gx = Ctx.Gx;
            var b = st.NavBox;
            if (st.SideRail)
            {
                gx.FillRR(c, b, st.NavBoxRadius, Tokens.White(0.06f));
                gx.StrokeRR(c, b, st.NavBoxRadius, 1, Tokens.White(0.10f));
                var logo = Ctx.Logo;
                var lr = st.RailLogo;
                if (logo != null)
                {
                    float lw = lr.Width, lh = lw * logo.Height / logo.Width;
                    gx.DrawImage(c, logo, new SKRect(lr.Left, lr.Top, lr.Left + lw, lr.Top + lh), 1f, SKFilterQuality.High);
                }
                else DrawTextLogo(c, (lr.Left + lr.Right) / 2, lr.Top + 34, 26);
            }
            else
            {
                gx.FillRR(c, b, st.NavBoxRadius, Tokens.C(20, 20, 34, 0.72f));
                gx.StrokeRR(c, b, st.NavBoxRadius, 1, Tokens.White(0.10f));
            }
            for (int i = 0; i < 3; i++)
            {
                var it = RailItems[i];
                bool active = i == (int)Section;
                if (active)
                {
                    gx.FillRR(c, it.R, st.NavItemRadius, Tokens.C(157, 107, 255, 0.24f));
                    gx.StrokeRR(c, it.R, st.NavItemRadius, 1, Tokens.C(157, 107, 255, 0.45f));
                }
                gx.DrawIcon(c, RailIcon(i), it.CenterX, it.CenterY, st.NavIcon, active ? Tokens.Text : Tokens.TextA(0.66f), 1.8f);
            }
        }

        /// <summary>The text "P!" in place of the logo image (when it cannot be loaded).</summary>
        protected void DrawTextLogo(SKCanvas c, float cx, float baseline, float size)
        {
            var gx = Ctx.Gx;
            float wP = gx.MeasureText("P", size, 800, -size / 25f), wB = gx.MeasureText("!", size, 800, -size / 25f);
            float lx = cx - (wP + wB) / 2;
            gx.DrawText(c, "P", lx, baseline, size, 800, Tokens.Text, -size / 25f);
            gx.DrawText(c, "!", lx + wP, baseline, size, 800, Tokens.Accent, -size / 25f);
        }

        /// <summary>The logo, as wide as asked or as tall as asked (whichever is not zero), with its left edge at x and centered on cy. Falls back to the text logo.</summary>
        protected void DrawLogoAt(SKCanvas c, float x, float cy, float height)
        {
            var logo = Ctx.Logo;
            if (logo == null) { DrawTextLogo(c, x + height * 0.8f, cy + height * 0.3f, height * 0.9f); return; }
            float w = height * logo.Width / logo.Height;
            Ctx.Gx.DrawImage(c, logo, new SKRect(x, cy - height / 2, x + w, cy + height / 2), 1f, SKFilterQuality.High);
        }
    }
}
