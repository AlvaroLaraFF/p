using System;
using System.IO;
using AceEngine.App.Tv;
using PUi.Gfx;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    public enum Section { Tv = 0, Search = 1, Settings = 2 }

    /// <summary>What the screens can ask of the application.</summary>
    public interface INavigator
    {
        void Push(IScreen screen);
        void Pop();
        /// <summary>Changes section (side menu): replaces the whole stack.</summary>
        void GoSection(Section section);
        void Exit();
        void Invalidate();
        void Post(Action action);
    }

    /// <summary>Shared drawing resources and services, for all the screens.</summary>
    public sealed class UiContext : IDisposable
    {
        public readonly TvServices Services;
        public readonly IPlatform Platform;
        public readonly INavigator Nav;
        public readonly Fonts Fonts;
        public readonly Gx Gx;
        public readonly SpriteCache Sprites;
        public readonly Fx Fx;
        public readonly Ambient Ambient;
        /// <summary>Clock of the repeating decoration (fluid border, strip, pulse): it slows down and stops when the remote has gone untouched for a while.</summary>
        public readonly Motion.DecorClock Decor = new Motion.DecorClock();
        private Lava _lava;
        private readonly string _imagesDir;
        private SKImage _logo;
        private bool _logoTried;

        public GRContext Gpu;
        public int PixelW = 1920, PixelH = 1080;
        /// <summary>The areas of the surface the system covers (pixels) and the screen density; set by PApp.Draw from the platform's ScreenMetrics. Nothing and 1 on a TV.</summary>
        public ScreenInsets Insets;
        public float Density = 1f;
        public double Now;
        private ScreenMetrics _surface;

        /// <summary>The surface and where the 1920 x 1080 canvas sits on it (see ScreenMetrics): recomputed only when the size, the insets or the density change.</summary>
        public ScreenMetrics Surface
        {
            get
            {
                var s = _surface;
                bool touch = HasTouch;
                if (s == null || s.PixelWidth != PixelW || s.PixelHeight != PixelH || s.Density != Density || s.Touch != touch || !s.SafeInsets.Equals(Insets)) { _surface = s = new ScreenMetrics(PixelW, PixelH, Insets, Density, touch); _style = null; _ambient = null; }
                return s;
            }
        }

        /// <summary>Pixels per logical unit.</summary>
        public float K { get { return Surface.Scale; } }

        /// <summary>The kind of device the interface is laid out for (Tv, PhonePortrait, TabletPortrait, TabletLandscape).</summary>
        public DeviceKind Kind { get { return Surface.Kind; } }
        /// <summary>A phone or a tablet (touch): the compact layouts. false on the TV, whose screens are the 1920 x 1080 design.</summary>
        public bool Compact { get { return Surface.IsCompact; } }
        /// <summary>The canvas's logical size: 1920 x 1080 on a TV; 390, 800 or 1280 wide and as tall as the safe area needs on a phone or a tablet.</summary>
        public float CW { get { return Surface.CanvasLogicalWidth; } }
        public float CH { get { return Surface.CanvasLogicalHeight; } }

        private CompactStyle _style;
        /// <summary>Sizes and places of the phone and tablet layouts for the current surface (null on a TV).</summary>
        public CompactStyle Style
        {
            get
            {
                var s = Surface;
                if (!s.IsCompact) return null;
                if (_style == null || !ReferenceEquals(_style.Source, s)) _style = new CompactStyle(s);
                return _style;
            }
        }

        private Ambient.Profile _ambient;
        private Ambient.Profile AmbientProfile
        {
            get
            {
                var s = Surface;
                if (!s.IsCompact) return Ambient.Tv;
                if (_ambient == null) _ambient = Ambient.ForCanvas(s.Kind, s.CanvasLogicalWidth, s.CanvasLogicalHeight);
                return _ambient;
            }
        }

        private ICastService _cast, _castWatched;
        /// <summary>"Enviar a la tele": the service if the platform (or the host) has one, otherwise one that is never available (no button is drawn). Settable by the host.
        /// When it becomes available or stops being, the interface redraws (the buttons appear or go).</summary>
        public ICastService Cast
        {
            get
            {
                var c = _cast ?? (Platform as ICastService) ?? NoCast.Instance;
                if (!ReferenceEquals(c, _castWatched))
                {
                    if (_castWatched != null) _castWatched.AvailabilityChanged -= OnCastChanged;
                    _castWatched = c;
                    c.AvailabilityChanged += OnCastChanged;
                }
                return c;
            }
            set { _cast = value; }
        }

        private void OnCastChanged() { try { Nav.Invalidate(); } catch (Exception) { } }

        private CastUi _castUi;
        /// <summary>The cast interface (the sheet, the strip and the notices of "Enviar a la tele"): made the first time it is asked for, which only happens on a phone or a tablet.</summary>
        public CastUi CastUi { get { return _castUi ?? (_castUi = new CastUi(this)); } }
        /// <summary>The cast interface exists (it was asked for): the TV never creates it.</summary>
        public bool HasCastUi { get { return _castUi != null; } }

        /// <summary>The optional platform contracts (video rectangle, orientation), or null where the platform does not have them (the Samsung TV).</summary>
        public IVideoPlacement VideoPlacement { get { return Optional<IVideoPlacement>(); } }
        public IOrientationControl OrientationControl { get { return Optional<IOrientationControl>(); } }

        private T Optional<T>() where T : class
        {
            var p = Platform;
            return (p as T) ?? (p.Window as T) ?? (p.Render as T);
        }

        /// <summary>The platform is driven by touch alone (a phone): the focus is not drawn until a key is pressed.</summary>
        public bool TouchFirst { get { var c = Platform.Capabilities; return c != null && c.HasTouch && !c.HasRemote; } }

        /// <summary>The platform has a touch screen: fingers drive the interface.</summary>
        public bool HasTouch { get { var c = Platform.Capabilities; return c != null && c.HasTouch; } }

        /// <summary>The whole surface in logical units: the canvas (0,0,1920,1080) when it fills the surface, and the canvas plus its bars on a letterboxed phone or tablet.</summary>
        public SKRect SurfaceLogical
        {
            get
            {
                var s = Surface;
                return s.HasBars ? new SKRect(s.SurfaceLeft, s.SurfaceTop, s.SurfaceRight, s.SurfaceBottom) : new SKRect(0, 0, s.CanvasLogicalWidth, s.CanvasLogicalHeight);
            }
        }

        /// <summary>The animated background on the whole surface, bars included (the canvas does not have to cover it: phones and tablets). It draws in pixels, whatever the canvas state is.</summary>
        public void DrawBackdrop(SKCanvas c, double now)
        {
            var s = Surface;
            Ambient.Draw(c, Gpu, now, PixelW, PixelH, s.Scale, s.CanvasLeft, s.CanvasTop, 1f, AmbientProfile);
        }

        public UiContext(TvServices services, IPlatform platform, INavigator nav, string fontsDir)
        {
            Services = services; Platform = platform; Nav = nav;
            Fonts = new Fonts(fontsDir);
            _imagesDir = Path.Combine(Path.GetDirectoryName((fontsDir ?? "").TrimEnd('/', '\\')) ?? "", "images");
            Gx = new Gx(Fonts);
            Sprites = new SpriteCache();
            Fx = new Fx(Gx, Sprites, Decor);
            Ambient = new Ambient();
        }

        public Lava Lava
        {
            get
            {
                var sf = Surface;
                if (sf.IsCompact)
                {
                    // a phone's or tablet's lamp is sized to its canvas: a new one when the canvas changes
                    string key = "c|" + (int)sf.CanvasLogicalWidth + "x" + (int)sf.CanvasLogicalHeight;
                    if (_lava == null || _lava.Key != key)
                    {
                        if (_lava != null) _lava.Dispose();
                        _lava = Lava.ForCanvas(Theme.Tokens.Accent2, Theme.Tokens.Accent, Theme.Tokens.Accent3, sf.CanvasLogicalWidth, sf.CanvasLogicalHeight);
                    }
                    return _lava;
                }
                if (_lava == null) _lava = new Lava(Theme.Tokens.Accent2, Theme.Tokens.Accent, Theme.Tokens.Accent3);
                return _lava;
            }
        }

        /// <summary>
        /// The P! logo for dark backgrounds (white "p", aqua "!"), with a transparent background. It sits in "images" next to the fonts folder
        /// (res/images on the TV, src/PUi/images on the PC). Null if it cannot be loaded: callers then draw the text logo.
        /// </summary>
        public SKImage Logo
        {
            get
            {
                if (_logoTried) return _logo;
                _logoTried = true;
                try
                {
                    using (var bmp = SKBitmap.Decode(Path.Combine(_imagesDir, "logo-light.png")))
                        if (bmp != null) _logo = SKImage.FromBitmap(bmp);
                }
                catch (Exception) { _logo = null; }
                return _logo;
            }
        }

        /// <summary>Drops the caches that can be rebuilt on demand (see PApp.TrimMemory).</summary>
        public void TrimMemory()
        {
            Sprites.Clear();
            Fx.TrimMemory();
            if (_lava != null) _lava.TrimMemory();
        }

        public void Dispose()
        {
            if (_castUi != null) _castUi.Dispose();
            if (_logo != null) _logo.Dispose();
            if (_lava != null) _lava.Dispose();
            Ambient.Dispose(); Fx.Dispose(); Sprites.Dispose(); Gx.Dispose(); Fonts.Dispose();
        }
    }
}
