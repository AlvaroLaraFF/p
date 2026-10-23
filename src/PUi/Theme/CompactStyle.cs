using System;
using PUi.Platform;
using SkiaSharp;

namespace PUi.Theme
{
    /// <summary>
    /// Where things go on a phone or a tablet. The measures are the design's (design-movil: 390 x 844, 800 x 1280 and 1280 x 800 CSS pixels, here logical units) and they are worked out
    /// from the logical canvas of the current surface (ScreenMetrics.CanvasLogicalWidth / Height: 390, 800 or 1280 wide, as tall as the safe area needs). Everything that depends on the
    /// height is a distance from the top or from the bottom edge, so a tall phone and a short one use the same numbers. The system bars and the notch are outside the canvas (it is the
    /// safe area), so the design's status-bar allowance is not repeated here: the designs' tops are brought up by what the status bar took.
    ///
    /// One instance belongs to one ScreenMetrics (UiContext.Style); a rotation or a resize makes a new one and the screens lay themselves out again.
    /// </summary>
    public sealed class CompactStyle
    {
        public readonly ScreenMetrics Source;
        public readonly DeviceKind Kind;
        /// <summary>The canvas's logical size.</summary>
        public readonly float W, H;
        /// <summary>A phone in portrait (390 units wide).</summary>
        public readonly bool Phone;
        /// <summary>Tablet, either orientation (bigger type than the phone's).</summary>
        public readonly bool Tablet;
        /// <summary>The sections are chosen from a rail at the left (tablet in landscape) instead of a bar at the bottom.</summary>
        public readonly bool SideRail;
        /// <summary>Columns of the category and Explore grids (2 on a phone, 3 on a tablet in portrait, 4 in landscape).</summary>
        public readonly int Cols;

        // ---------------------------------------------------------------- the section bar / rail
        /// <summary>The bar's pill (phone, tablet in portrait) or the rail (tablet in landscape).</summary>
        public readonly SKRect NavBox;
        public readonly SKRect[] NavItem = new SKRect[3];
        public readonly float NavBoxRadius, NavItemRadius, NavIcon;
        /// <summary>The logo above the rail's items (empty where there is no rail).</summary>
        public readonly SKRect RailLogo;
        /// <summary>Where the cast button goes on the rail (drawn only while a cast service is available).</summary>
        public readonly SKRect RailCast;

        // ---------------------------------------------------------------- the areas
        /// <summary>The viewport of a section's content: what scrolls and is clipped (below the header, above the bar, right of the rail).</summary>
        public readonly SKRect Main;
        /// <summary>Left and right edge of the grids, the recent cards and the divider on the TV page.</summary>
        public readonly float HomeL, HomeR;
        /// <summary>Left and right edge of the channel rows, the search field and the settings groups.</summary>
        public readonly float ListL, ListR;
        /// <summary>Header row of a page: vertical center and the size of its round buttons (back arrow, sort, cast).</summary>
        public readonly float HeaderCy, HeaderBtn;
        /// <summary>Where the page's title starts when there is a back arrow before it, and without one.</summary>
        public readonly float TitleX, TitleXNoBack;
        /// <summary>Right edge of the header's buttons.</summary>
        public readonly float HeaderRight;
        /// <summary>The back arrow of a page's header, and the cast button at the right end of it (portrait; on the landscape rail it is RailCast).</summary>
        public readonly SKRect BackBox, HeaderCastBox;

        public CompactStyle(ScreenMetrics m)
        {
            Source = m; Kind = m.Kind;
            W = m.CanvasLogicalWidth; H = m.CanvasLogicalHeight;
            Phone = Kind == DeviceKind.PhonePortrait;
            Tablet = !Phone;
            SideRail = Kind == DeviceKind.TabletLandscape;
            Cols = Phone ? 2 : SideRail ? 4 : 3;
            float h = H;
            if (Phone)
            {
                // bottom bar: a 64 high pill, 16 from the sides
                NavBox = new SKRect(16, h - 74, W - 16, h - 10);
                NavBoxRadius = 32; NavItemRadius = 24; NavIcon = 24;
                for (int i = 0; i < 3; i++)
                {
                    float cx = NavBox.Left + 10 + (NavBox.Width - 20) * (i + 0.5f) / 3f;
                    NavItem[i] = new SKRect(cx - 32, NavBox.Top + 8, cx + 32, NavBox.Top + 56);
                }
                Main = new SKRect(0, 64, W, h - 84);
                HomeL = 20; HomeR = W - 20; ListL = 16; ListR = W - 16;
                HeaderCy = 30; HeaderBtn = 44; TitleX = 56; TitleXNoBack = 20; HeaderRight = W - 12;
                BackBox = new SKRect(8, HeaderCy - 22, 52, HeaderCy + 22);
                HeaderCastBox = new SKRect(W - 20 - 44, HeaderCy - 22, W - 20, HeaderCy + 22);
            }
            else if (!SideRail)
            {
                // tablet in portrait: a 360 x 72 pill centered
                NavBox = new SKRect((W - 360) / 2, h - 88, (W + 360) / 2, h - 16);
                NavBoxRadius = 36; NavItemRadius = 27; NavIcon = 26;
                for (int i = 0; i < 3; i++)
                {
                    float cx = NavBox.Left + 14 + (NavBox.Width - 28) * (i + 0.5f) / 3f;
                    NavItem[i] = new SKRect(cx - 38, NavBox.Top + 9, cx + 38, NavBox.Top + 63);
                }
                Main = new SKRect(0, 88, W, h - 104);
                HomeL = 32; HomeR = W - 32; ListL = 32; ListR = W - 32;
                HeaderCy = 40; HeaderBtn = 48; TitleX = 92; TitleXNoBack = 32; HeaderRight = W - 28;
                BackBox = new SKRect(28, HeaderCy - 24, 76, HeaderCy + 24);
                HeaderCastBox = new SKRect(W - 32 - 52, HeaderCy - 26, W - 32, HeaderCy + 26);
            }
            else
            {
                // tablet in landscape: a rail 88 wide at the left with the logo, the three sections and the cast slot at the bottom
                NavBox = new SKRect(16, 12, 104, h - 12);
                NavBoxRadius = 30; NavItemRadius = 20; NavIcon = 26;
                RailLogo = new SKRect(NavBox.Left + 21, NavBox.Top + 28, NavBox.Left + 67, NavBox.Top + 28 + 46);
                float y = NavBox.Top + 28 + 46 + 18 + 14;
                for (int i = 0; i < 3; i++) { NavItem[i] = new SKRect(NavBox.Left + 14, y, NavBox.Left + 74, y + 60); y += 74; }
                RailCast = new SKRect(NavBox.Left + 14, NavBox.Bottom - 28 - 60, NavBox.Left + 74, NavBox.Bottom - 28);
                Main = new SKRect(116, 0, W, h);
                HomeL = 132; HomeR = W - 32; ListL = 132; ListR = W - 32;
                HeaderCy = 52; HeaderBtn = 48; TitleX = 188; TitleXNoBack = 132; HeaderRight = W - 32;
                BackBox = new SKRect(124, HeaderCy - 24, 172, HeaderCy + 24);
                HeaderCastBox = new SKRect();
            }
        }

        /// <summary>The recent cards and the grids stop short of the bar: how far down the page's content can go before the bar starts (bottom edge of Main).</summary>
        public float MainBottom { get { return Main.Bottom; } }

        /// <summary>A 16:9 picture as wide as the canvas (the player in portrait): its height.</summary>
        public float VideoHeight { get { return (float)Math.Round(W * 9f / 16f * 10f) / 10f; } }

        public override string ToString() { return Kind + " " + W + "x" + H; }
    }
}
