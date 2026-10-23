using SkiaSharp;

namespace PUi.Theme
{
    /// <summary>Design tokens taken from the design (1920x1080 canvas). Colors, times and measures that repeat across the screens.</summary>
    public static class Tokens
    {
        public const int W = 1920, H = 1080;

        /// <summary>Color with alpha in 0..1.</summary>
        public static SKColor C(byte r, byte g, byte b, float a = 1f) { return new SKColor(r, g, b, (byte)(a < 0 ? 0 : a > 1 ? 255 : a * 255f + 0.5f)); }
        public static SKColor White(float a) { return C(255, 255, 255, a); }
        public static SKColor Hex(string s) { return SKColor.Parse(s); }
        public static SKColor WithAlpha(SKColor c, float a) { return new SKColor(c.Red, c.Green, c.Blue, (byte)(a < 0 ? 0 : a > 1 ? 255 : a * 255f + 0.5f)); }
        public static SKColor Lerp(SKColor a, SKColor b, float t)
        {
            return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
        }

        // ---------- colors ----------
        public static readonly SKColor Bg = Hex("#070711");
        public static readonly SKColor BgPlayerLoading = Hex("#05050C");
        public static readonly SKColor Text = Hex("#F3F5F8");
        public static readonly SKColor Accent = Hex("#3FE8CF");      // light blue/turquoise: live, pulse, LIVE button
        public static readonly SKColor Accent2 = Hex("#9D6BFF");     // purple: spinners, progress, upload
        public static readonly SKColor Accent3 = Hex("#FFD166");     // yellow: fluid border and channel loading
        public static readonly SKColor Blue = Hex("#4F8CFF");        // border of the recents and of the fluid border
        public static readonly SKColor Warn = Hex("#FFC874");        // amber: unconfirmed, TSService without response
        public static readonly SKColor KnobOn = Hex("#06080C");

        public static SKColor TextA(float a) { return WithAlpha(Text, a); }

        /// <summary>Colors of the fluid border (cone): light blue, blue, purple, yellow, light blue.</summary>
        public static readonly SKColor[] RingColors = { Hex("#3FE8CF"), Hex("#4F8CFF"), Hex("#9D6BFF"), Hex("#FFD166"), Hex("#3FE8CF") };
        public static readonly float[] RingPositions = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        // ---------- times (seconds) ----------
        public const double FocusSecs = 0.24, RingFadeSecs = 0.35, ParallaxSecs = 1.8, RingTurnSecs = 7.0, SpinSecs = 0.9, PulseSecs = 1.8, ShimmerSecs = 1.4, DividerSecs = 16.0;
        public const double RiseSecs = 0.32, PanelInSecs = 0.36, PanelOutSecs = 0.24, ReorderSecs = 0.30, PanelHideSecs = 5.0, BufferRingSecs = 2.4, ToggleSecs = 0.24;

        // ---------- geometry of the main area ----------
        public const float MainLeft = 200, MainRight = 96, MainTop = 72, MainBottom = 56;
        public const float MainWidth = W - MainLeft - MainRight;           // 1624
        public const float RailX = 40, RailY = 40, RailW = 112, RailH = 1000, RailRadius = 36;
    }
}
