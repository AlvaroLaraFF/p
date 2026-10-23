using System;

namespace PUi.Platform
{
    public enum ScreenOrientation { Landscape, Portrait }

    /// <summary>Space at the edges of the surface that the system covers or that is not safe to draw important things in (status and navigation bars, notch, rounded corners), in PIXELS of the drawing surface. Zero on a TV.</summary>
    public struct ScreenInsets
    {
        public int Left, Top, Right, Bottom;
        public ScreenInsets(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        public bool IsEmpty { get { return Left == 0 && Top == 0 && Right == 0 && Bottom == 0; } }
        public static readonly ScreenInsets None = new ScreenInsets();
    }

    /// <summary>
    /// Size and shape of the drawing surface, for the layout. PUi draws in LOGICAL units on a CANVAS: Scale is the pixels per logical unit (the same K the drawing uses). What the canvas is
    /// depends on the kind of device (<see cref="Kind"/>, decided here from the capabilities and the physical size of the screen):
    ///
    ///  TV (no touch): the canvas is always 1920 x 1080, the TV's design.
    ///   - A surface with the design's 16:9 shape (within 2 %) and nothing covered (a TV, a 16:9 window) is filled edge to edge: Scale = PixelWidth / 1920, as always. The Samsung TV takes this path.
    ///   - Any other surface (a 21:9 monitor, a 16:9 screen with an overscan inset) gets the canvas letterboxed: scaled to the largest size that fits inside the SAFE area (the surface minus
    ///     SafeInsets) and centered, moved as far as needed to stay inside the safe area. The animated background is drawn on the whole surface, bars included.
    ///
    ///  Phone or tablet (touch): the canvas is the SAFE AREA itself, with the design's units: 390 units wide on a phone in portrait, 800 on a tablet in portrait and 1280 on a tablet in
    ///   landscape (a phone whose surface is landscape, the full screen player, gets 1 unit = 1 dp, between 640 and 1280 wide). The height is whatever the safe area gives at that scale
    ///   (<see cref="CanvasLogicalHeight"/>), so the layouts adapt to tall and short screens and to rotation: they are worked out from the logical width and height, not from a fixed
    ///   size. The system bars and the notch are outside the canvas, so nothing important ever goes under them; the background is drawn on the whole surface.
    ///
    /// Logical coordinates (what PUi and PointerEvent use) have their origin at the canvas's top-left corner: the TV canvas is 0..1920 x 0..1080, and the bars of the surface are negative or
    /// beyond (SurfaceLeft ... SurfaceBottom). A platform turns a touch position into PointerEvent coordinates with ToLogicalX / ToLogicalY, which take the canvas position into account,
    /// and puts the video where the interface says (IVideoPlacement; the rectangle is converted with ToPixelX / ToPixelY).
    /// </summary>
    public sealed class ScreenMetrics
    {
        public const int DesignWidth = 1920, DesignHeight = 1080;
        /// <summary>A smaller side (in dp) under this is a phone.</summary>
        public const float PhoneMaxDp = 600f;
        /// <summary>Logical width of the canvas of each kind (a phone's landscape surface is worked out, see above).</summary>
        public const int PhonePortraitWidth = 390, TabletPortraitWidth = 800, TabletLandscapeWidth = 1280;

        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        /// <summary>The system-covered edges, in pixels.</summary>
        public ScreenInsets SafeInsets { get; private set; }
        /// <summary>Device pixels per density-independent unit (Android's density; 1 where the platform does not say). To size touch targets on small screens and to tell a phone from a tablet.</summary>
        public float Density { get; private set; }
        /// <summary>A touch screen drives the interface (PlatformCapabilities.HasTouch). Without it the device is a TV whatever its size.</summary>
        public bool Touch { get; private set; }

        private readonly float _scale, _left, _top, _w, _h, _lw, _lh;
        private readonly DeviceKind _kind;
        private readonly bool _phone;

        public ScreenMetrics(int pixelWidth, int pixelHeight, ScreenInsets safeInsets = default(ScreenInsets), float density = 1f, bool touch = false)
        {
            PixelWidth = Math.Max(0, pixelWidth); PixelHeight = Math.Max(0, pixelHeight); SafeInsets = safeInsets; Density = density > 0 ? density : 1f; Touch = touch;
            _lw = DesignWidth; _lh = DesignHeight;
            if (PixelWidth <= 0 || PixelHeight <= 0) return;
            float l = Math.Max(0, safeInsets.Left), t = Math.Max(0, safeInsets.Top), r = Math.Max(0, safeInsets.Right), b = Math.Max(0, safeInsets.Bottom);
            float safeW = Math.Max(1, PixelWidth - l - r), safeH = Math.Max(1, PixelHeight - t - b);
            if (touch)
            {
                bool landscape = PixelWidth >= PixelHeight;
                _phone = Math.Min(PixelWidth, PixelHeight) / Density < PhoneMaxDp;
                _kind = landscape ? DeviceKind.TabletLandscape : _phone ? DeviceKind.PhonePortrait : DeviceKind.TabletPortrait;
                float lw;
                if (_kind == DeviceKind.PhonePortrait) lw = PhonePortraitWidth;
                else if (_kind == DeviceKind.TabletPortrait) lw = TabletPortraitWidth;
                else if (_phone) lw = Math.Max(640, Math.Min(TabletLandscapeWidth, (float)Math.Round(safeW / Density)));
                else lw = TabletLandscapeWidth;
                _scale = safeW / lw;
                _lw = lw; _lh = safeH / _scale;
                _left = l; _top = t; _w = safeW; _h = safeH;
                return;
            }
            _kind = DeviceKind.Tv;
            if (safeInsets.IsEmpty && IsDesignAspect)
            {
                // the design's own shape, nothing covered: the canvas fills the surface (the TV's case; exactly what the interface always did)
                _scale = PixelWidth / (float)DesignWidth; _left = 0; _top = 0; _w = PixelWidth; _h = PixelHeight;
                return;
            }
            _scale = Math.Min(safeW / DesignWidth, safeH / DesignHeight);
            _w = DesignWidth * _scale; _h = DesignHeight * _scale;
            _left = Snap(Clamp((PixelWidth - _w) / 2, l, PixelWidth - r - _w));
            _top = Snap(Clamp((PixelHeight - _h) / 2, t, PixelHeight - b - _h));
        }

        private static float Clamp(float v, float lo, float hi) { return hi < lo ? lo : v < lo ? lo : v > hi ? hi : v; }
        private static float Snap(float v) { return (float)Math.Floor(v + 0.5); }          // whole pixels: the sprites are drawn crisp

        /// <summary>The TV's: 1920x1080, nothing covered.</summary>
        public static ScreenMetrics Tv { get { return new ScreenMetrics(DesignWidth, DesignHeight); } }

        public float AspectRatio { get { return PixelHeight <= 0 ? 0 : (float)PixelWidth / PixelHeight; } }
        public ScreenOrientation Orientation { get { return PixelWidth >= PixelHeight ? ScreenOrientation.Landscape : ScreenOrientation.Portrait; } }
        /// <summary>The surface has the design's 16:9 shape (within 2 %): the TV layout needs no adaptation.</summary>
        public bool IsDesignAspect { get { return Math.Abs(AspectRatio - 16f / 9f) < 0.04f; } }

        // ---------------------------------------------------------------- the kind of device
        /// <summary>Tv, or one of the three phone and tablet layouts (see DeviceKind).</summary>
        public DeviceKind Kind { get { return _kind; } }
        /// <summary>A touch device, whatever its layout.</summary>
        public bool IsCompact { get { return _kind != DeviceKind.Tv; } }
        /// <summary>The screen is a phone's (a smaller side under 600 dp), even while its surface is landscape (the full screen player).</summary>
        public bool IsPhone { get { return _phone; } }
        /// <summary>The smaller side of the screen in dp (0 on an empty surface).</summary>
        public float SmallestWidthDp { get { return Math.Min(PixelWidth, PixelHeight) / Density; } }

        // ---------------------------------------------------------------- the canvas
        /// <summary>Pixels per logical unit.</summary>
        public float Scale { get { return _scale; } }
        /// <summary>Where the canvas sits on the surface, in pixels (the whole surface for a TV; the safe area for a phone or a tablet).</summary>
        public float CanvasLeft { get { return _left; } }
        public float CanvasTop { get { return _top; } }
        public float CanvasWidth { get { return _w; } }
        public float CanvasHeight { get { return _h; } }
        /// <summary>The canvas's size in LOGICAL units: 1920 x 1080 on a TV; 390, 800 or 1280 wide and as tall as the safe area gives on a phone or a tablet.</summary>
        public float CanvasLogicalWidth { get { return _lw; } }
        public float CanvasLogicalHeight { get { return _lh; } }
        /// <summary>The height of a 16:9 picture as wide as the canvas (the player in portrait).</summary>
        public float VideoHeightLogical { get { return _lw * 9f / 16f; } }
        /// <summary>The canvas does not cover the whole surface (bars at the sides or above and below, or a notch moved it).</summary>
        public bool HasBars { get { return _scale > 0 && (_left != 0 || _top != 0 || _w < PixelWidth - 0.5f || _h < PixelHeight - 0.5f); } }

        /// <summary>The whole surface in logical units, relative to the canvas origin: SurfaceLeft and SurfaceTop are zero or negative, SurfaceRight and SurfaceBottom are the canvas's size or more.</summary>
        public float SurfaceLeft { get { return _scale <= 0 ? 0 : -_left / _scale; } }
        public float SurfaceTop { get { return _scale <= 0 ? 0 : -_top / _scale; } }
        public float SurfaceRight { get { return _scale <= 0 ? 0 : (PixelWidth - _left) / _scale; } }
        public float SurfaceBottom { get { return _scale <= 0 ? 0 : (PixelHeight - _top) / _scale; } }

        /// <summary>The surface's size in logical units (the same as SurfaceRight - SurfaceLeft): 1920 x 1080 on a TV, wider than 1920 on a letterboxed one.</summary>
        public float LogicalWidth { get { return _scale <= 0 ? 0 : PixelWidth / _scale; } }
        public float LogicalHeight { get { return _scale <= 0 ? 0 : PixelHeight / _scale; } }

        /// <summary>Pixel position on the surface to logical coordinates (origin at the canvas's top-left corner). This is what a platform puts in PointerEvent.</summary>
        public float ToLogicalX(float pixelX) { return _scale <= 0 ? 0 : (pixelX - _left) / _scale; }
        public float ToLogicalY(float pixelY) { return _scale <= 0 ? 0 : (pixelY - _top) / _scale; }
        public float ToPixelX(float logicalX) { return _left + logicalX * _scale; }
        public float ToPixelY(float logicalY) { return _top + logicalY * _scale; }

        /// <summary>The safe insets in logical units (distance from each edge of the SURFACE).</summary>
        public ScreenInsets SafeInsetsLogical
        {
            get
            {
                float s = _scale;
                if (s <= 0) return ScreenInsets.None;
                return new ScreenInsets((int)Math.Round(SafeInsets.Left / s), (int)Math.Round(SafeInsets.Top / s), (int)Math.Round(SafeInsets.Right / s), (int)Math.Round(SafeInsets.Bottom / s));
            }
        }

        /// <summary>A rectangle of the canvas in logical units, as pixels of the surface (for IVideoPlacement), snapped to whole pixels.</summary>
        public PixelRect ToPixels(float left, float top, float right, float bottom)
        {
            return new PixelRect((int)Math.Round(ToPixelX(left)), (int)Math.Round(ToPixelY(top)), (int)Math.Round(ToPixelX(right)), (int)Math.Round(ToPixelY(bottom)));
        }

        public override bool Equals(object obj)
        {
            var o = obj as ScreenMetrics;
            return o != null && o.PixelWidth == PixelWidth && o.PixelHeight == PixelHeight && o.SafeInsets.Equals(SafeInsets) && o.Density == Density && o.Touch == Touch;
        }

        public override int GetHashCode() { return PixelWidth * 397 ^ PixelHeight; }
        public override string ToString() { return PixelWidth + "x" + PixelHeight + " " + Orientation + " " + _kind + (SafeInsets.IsEmpty ? "" : " insets " + SafeInsets.Left + "," + SafeInsets.Top + "," + SafeInsets.Right + "," + SafeInsets.Bottom) + " density " + Density; }
    }
}
