using System;
using System.Collections.Generic;
using System.Diagnostics;
using Android.Content;
using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using AceEngine.App;
using PUi.Input;
using PUi.Platform;
using SkiaSharp;

namespace PAndroid
{
    /// <summary>
    /// The view where PUi draws the whole interface: a TextureView (so it can be opaque or transparent over the video SurfaceView below it) with our own EGL context, drawn from the MAIN thread on
    /// every Choreographer tick: one interface thread for everything, like on the TV. Skia (a GRContext over that EGL context) renders into the window's framebuffer and eglSwapBuffers hands the
    /// frame to the TextureView.
    ///
    /// Lifecycle of the pieces, from what the system does to a TextureView:
    ///  - The EGL display and context live as long as the view; they are created with the first surface and kept when the surface goes away (the app stopped, the view detached), so the Skia context
    ///    and everything uploaded to it survive. Only the EGL surface is created and destroyed with each SurfaceTexture.
    ///  - If the context itself is lost (a GPU reset), everything is rebuilt and ContextRecreated is raised before the next frame so the interface drops what it had uploaded.
    ///  - eglSwapBuffers on a SurfaceTexture can block when the consumer is not taking frames, and here the consumer is the same thread: so there is never more than one frame per tick, nothing is drawn
    ///    while the activity is stopped or the window is not visible, and the swap interval is 0 (asynchronous mode: a frame the consumer has not taken yet is replaced instead of waited for).
    ///  - The drawing buffer can be smaller than the view (weak devices draw 720p): SurfaceTexture.setDefaultBufferSize, and the compositor scales it up.
    /// </summary>
    internal sealed class AndroidSkiaView : TextureView, TextureView.ISurfaceTextureListener, IRenderHost, ISnapshotSource
    {
        private new const string Tag = "egl";
        private const int GlRgba8 = 0x8058;

        private readonly AndroidDeviceProfile _profile;
        private readonly float _density;
        private readonly int _maxLongSide;

        // ---- EGL (main thread only)
        private EGLDisplay _dpy;
        private EGLConfig _cfg;
        private int _stencilBits = 8, _samples;
        private EGLContext _ctx;
        private EGLSurface _surf;
        private bool _displayReady, _hasContext, _hasSurface, _isCurrent;
        private SurfaceTexture _st;
        private int _surfaceAttempts;
        private readonly int[] _q = new int[1];
        private int _contextGeneration;
        private bool _contextRecreated;

        // ---- Skia (main thread only)
        private GRContext _gr;
        private SKSurface _sk;
        private int _skW, _skH;
        private bool _purgeRequested;

        // ---- loop
        private IFramePresenter _presenter;
        private FrameCallback _callback;
        private volatile bool _redraw;
        private volatile bool _active = true;
        private readonly object _snapGate = new object();
        private readonly List<Action> _snapActions = new List<Action>();

        // ---- screen
        private ScreenInsets _insets;
        private int _bufW, _bufH;
        private ScreenMetrics _metrics;
        private int _lastViewW, _lastViewH;

        // ---- counters (for the statistics line)
        private long _frames, _swapFailures, _drawErrors;
        private double _drawMsLast, _swapMsLast, _drawMsMax;
        private long _lastErrorLogTicks;

        public AndroidSkiaView(Context context, AndroidDeviceProfile profile, int maxLongSideOverride = 0) : base(context)
        {
            _profile = profile;
            _density = context.Resources.DisplayMetrics.Density;
            _maxLongSide = maxLongSideOverride > 0 ? maxLongSideOverride : profile.MaxRenderLongSide;
            _metrics = ScreenMath.Metrics(0, 0, 0, 0, ScreenInsets.None, _density, profile != null && profile.Capabilities.HasTouch);
            SurfaceTextureListener = this;
            SetOpaque(true);
            Focusable = true;
            FocusableInTouchMode = true;
        }

        // Needed by the runtime when Java hands the view back to managed code.
        public AndroidSkiaView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        // ================================================================ IRenderHost
        public int PixelWidth { get { return _metrics.PixelWidth; } }
        public int PixelHeight { get { return _metrics.PixelHeight; } }
        public ScreenMetrics Screen { get { return _metrics; } }
        public event Action ScreenChanged;
        /// <summary>The GPU context was lost and rebuilt: whatever was uploaded to the old one is gone (the interface must drop its sprites). Raised on the main thread, right before the frame.</summary>
        public event Action ContextRecreated;

        public void SetPresenter(IFramePresenter presenter) { _presenter = presenter; }
        public void RequestRedraw() { _redraw = true; }

        public void Start()
        {
            if (_callback != null) return;
            _callback = new FrameCallback(this);
            Choreographer.Instance.PostFrameCallback(_callback);
        }

        public void Stop()
        {
            var cb = _callback; _callback = null;
            if (cb != null) { try { Choreographer.Instance.RemoveFrameCallback(cb); } catch (Exception) { } }
        }

        /// <summary>The activity is visible (true) or stopped (false). Nothing is drawn while it is stopped.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (active) _redraw = true;
        }

        /// <summary>Opaque (all screens but the player: cheaper, covers the video) or transparent (the video shows through). Main thread.</summary>
        public void SetContentOpaque(bool opaque)
        {
            if (IsOpaque != opaque) SetOpaque(opaque);
            _redraw = true;
        }

        /// <summary>Asks to drop the GPU memory Skia keeps for reuse, on the next frame (onTrimMemory).</summary>
        public void RequestPurge() { _purgeRequested = true; _redraw = true; }

        private sealed class FrameCallback : Java.Lang.Object, Choreographer.IFrameCallback
        {
            private readonly AndroidSkiaView _v;
            public FrameCallback(AndroidSkiaView v) { _v = v; }
            public void DoFrame(long frameTimeNanos)
            {
                if (!ReferenceEquals(_v._callback, this)) return;
                Choreographer.Instance.PostFrameCallback(this);            // first: whatever happens below, the loop goes on
                _v.Tick(frameTimeNanos / 1e9);
            }
        }

        private void Tick(double now)
        {
            if (!_active) return;
            var pr = _presenter;
            if (pr == null) return;
            bool need = false;
            try { need = pr.NeedsFrame(now); }
            catch (Exception ex) { LogRateLimited("NeedsFrame", ex); }
            bool snapshot;
            lock (_snapGate) snapshot = _snapActions.Count > 0;
            if (!need && !_redraw && !snapshot && !_purgeRequested && !_contextRecreated) return;
            if (WindowVisibility != ViewStates.Visible || (!_hasSurface && _st == null)) { _redraw = true; return; }       // draws as soon as there is somewhere to draw
            _redraw = false;
            DrawFrame(pr, now);
        }

        // ================================================================ drawing
        private void DrawFrame(IFramePresenter presenter, double now)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (!EnsureReady()) { _redraw = true; return; }
            EGL14.EglQuerySurface(_dpy, _surf, EGL14.EglWidth, _q, 0); int w = _q[0];
            EGL14.EglQuerySurface(_dpy, _surf, EGL14.EglHeight, _q, 0); int h = _q[0];
            if (w <= 0 || h <= 0) { _redraw = true; return; }
            if (_sk == null || w != _skW || h != _skH) RebuildSkiaSurface(w, h);
            if (_sk == null) return;
            if (_contextRecreated)
            {
                _contextRecreated = false;
                var cr = ContextRecreated;
                if (cr != null) { try { cr(); } catch (Exception ex) { LogRateLimited("ContextRecreated", ex); } }
            }
            if (_purgeRequested) { _purgeRequested = false; try { _gr.PurgeResources(); } catch (Exception ex) { LogRateLimited("PurgeResources", ex); } }
            bool drawn = false;
            try
            {
                var canvas = _sk.Canvas;
                presenter.Draw(canvas, _gr, now, w, h);
                canvas.Flush();
                _gr.Flush();
                drawn = true;
            }
            catch (Exception ex) { _drawErrors++; LogRateLimited("Draw", ex); }
            RunSnapshotActions();
            long t1 = Stopwatch.GetTimestamp();
            if (!drawn) return;
            bool ok = EGL14.EglSwapBuffers(_dpy, _surf);
            long t2 = Stopwatch.GetTimestamp();
            _drawMsLast = (t1 - t0) * 1000.0 / Stopwatch.Frequency; _swapMsLast = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
            if (_drawMsLast > _drawMsMax) _drawMsMax = _drawMsLast;
            _frames++;
            if (!ok) { _swapFailures++; OnSwapFailed(EGL14.EglGetError()); }
            else _surfaceAttempts = 0;
        }

        private void RebuildSkiaSurface(int w, int h)
        {
            DisposeSkiaSurface();
            try
            {
                using (var rt = new GRBackendRenderTarget(w, h, _samples, _stencilBits, new GRGlFramebufferInfo(0, GlRgba8)))
                    _sk = SKSurface.Create(_gr, rt, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
                _skW = w; _skH = h;
                if (_sk == null) UdpLog.Error(Tag, "Skia could not wrap the window framebuffer (" + w + "x" + h + ")");
                else UdpLog.Info(Tag, "drawing surface " + w + "x" + h);
            }
            catch (Exception ex) { UdpLog.Error(Tag, "Skia surface: " + ex); _sk = null; }
            UpdateMetrics();
        }

        private void DisposeSkiaSurface()
        {
            var s = _sk; _sk = null; _skW = _skH = 0;
            if (s != null) { try { s.Dispose(); } catch (Exception) { } }
        }

        // ================================================================ EGL
        /// <summary>Makes sure there is a context, a surface and a Skia context, and that they are current. false if there is nothing to draw on yet.</summary>
        private bool EnsureReady()
        {
            if (!_hasContext && !CreateContext()) return false;
            if (!_hasSurface)
            {
                if (_st == null || _surfaceAttempts >= 3) return false;
                _surfaceAttempts++;
                if (!CreateSurface()) return false;
            }
            if (!_isCurrent)
            {
                if (!EGL14.EglMakeCurrent(_dpy, _surf, _surf, _ctx)) { UdpLog.Warn(Tag, "eglMakeCurrent failed: 0x" + EGL14.EglGetError().ToString("x")); return false; }
                _isCurrent = true;
                EGL14.EglSwapInterval(_dpy, 0);
            }
            if (_gr == null && !CreateSkiaContext()) return false;
            return true;
        }

        private bool CreateContext()
        {
            if (!_displayReady)
            {
                _dpy = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
                var ver = new int[2];
                if (_dpy == null || !EGL14.EglInitialize(_dpy, ver, 0, ver, 1)) { UdpLog.Error(Tag, "eglInitialize failed: 0x" + EGL14.EglGetError().ToString("x")); return false; }
                _displayReady = true;
                UdpLog.Info(Tag, "EGL " + ver[0] + "." + ver[1] + " " + EGL14.EglQueryString(_dpy, 0x3053 /* EGL_VENDOR */) + " / " + EGL14.EglQueryString(_dpy, 0x3054 /* EGL_VERSION */));
            }
            // RGBA 8888 with a stencil (Skia clips with it); falls back to less if the device has no such configuration
            var configs = new EGLConfig[1];
            var num = new int[1];
            bool found = false;
            foreach (int[] request in new[] { new[] { 8, 0, 8 }, new[] { 8, 16, 8 }, new[] { 8, 0, 0 } })
            {
                var attribs = new[]
                {
                    EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit, EGL14.EglSurfaceType, EGL14.EglWindowBit,
                    EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, request[0],
                    EGL14.EglDepthSize, request[1], EGL14.EglStencilSize, request[2], EGL14.EglNone
                };
                if (EGL14.EglChooseConfig(_dpy, attribs, 0, configs, 0, 1, num, 0) && num[0] > 0) { found = true; break; }
            }
            if (!found) { UdpLog.Error(Tag, "no EGL configuration (ES2, RGBA8888, window)"); return false; }
            _cfg = configs[0];
            var attrib = new int[1];
            _stencilBits = EGL14.EglGetConfigAttrib(_dpy, _cfg, 0x3026 /* EGL_STENCIL_SIZE */, attrib, 0) ? attrib[0] : 0;
            _samples = EGL14.EglGetConfigAttrib(_dpy, _cfg, 0x3031 /* EGL_SAMPLES */, attrib, 0) ? attrib[0] : 0;
            _ctx = EGL14.EglCreateContext(_dpy, _cfg, EGL14.EglNoContext, new[] { EGL14.EglContextClientVersion, 2, EGL14.EglNone }, 0);
            if (_ctx == null || _ctx.Equals(EGL14.EglNoContext)) { UdpLog.Error(Tag, "eglCreateContext failed: 0x" + EGL14.EglGetError().ToString("x")); _ctx = null; return false; }
            _hasContext = true; _isCurrent = false;
            _contextGeneration++;
            UdpLog.Info(Tag, "EGL context #" + _contextGeneration + " created");
            return true;
        }

        private bool CreateSurface()
        {
            var s = EGL14.EglCreateWindowSurface(_dpy, _cfg, _st, new[] { EGL14.EglNone }, 0);
            if (s == null || s.Equals(EGL14.EglNoSurface)) { UdpLog.Warn(Tag, "eglCreateWindowSurface failed: 0x" + EGL14.EglGetError().ToString("x")); return false; }
            _surf = s; _hasSurface = true; _isCurrent = false;
            return true;
        }

        private bool CreateSkiaContext()
        {
            try
            {
                var iface = GRGlInterface.Create();
                if (iface == null) { UdpLog.Error(Tag, "no GL interface for Skia"); return false; }
                _gr = GRContext.CreateGl(iface);
                if (_gr == null) { UdpLog.Error(Tag, "Skia could not create its GPU context"); return false; }
                if (_profile != null && _profile.Memory == MemoryTier.Low) _gr.SetResourceCacheLimit(32L * 1024 * 1024);
                UdpLog.Info(Tag, "Skia GPU context ready");
                return true;
            }
            catch (Exception ex) { UdpLog.Error(Tag, "Skia context: " + ex); _gr = null; return false; }
        }

        /// <summary>Lets go of the EGL surface (keeping the context). Safe to call with nothing to release.</summary>
        private void DestroySurface()
        {
            DisposeSkiaSurface();
            if (_hasSurface)
            {
                try
                {
                    if (_isCurrent) EGL14.EglMakeCurrent(_dpy, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                    EGL14.EglDestroySurface(_dpy, _surf);
                }
                catch (Exception ex) { UdpLog.Warn(Tag, "destroying the EGL surface: " + ex.Message); }
            }
            _surf = null; _hasSurface = false; _isCurrent = false;
        }

        /// <summary>The GPU context is gone: everything that belonged to it is dropped and rebuilt with the next frame.</summary>
        private void DestroyContext()
        {
            DestroySurface();
            var gr = _gr; _gr = null;
            if (gr != null) { try { gr.AbandonContext(true); gr.Dispose(); } catch (Exception) { } }
            if (_hasContext)
            {
                try { EGL14.EglDestroyContext(_dpy, _ctx); } catch (Exception) { }
            }
            _ctx = null; _hasContext = false; _isCurrent = false;
        }

        private void OnSwapFailed(int err)
        {
            if (err == EGL14.EglContextLost)
            {
                UdpLog.Warn(Tag, "EGL context lost: rebuilding the GPU context");
                DestroyContext();
                _contextRecreated = true; _surfaceAttempts = 0; _redraw = true;
            }
            else if (err == EGL14.EglBadSurface || err == EGL14.EglBadNativeWindow)
            {
                UdpLog.Warn(Tag, "EGL surface lost (0x" + err.ToString("x") + "): recreating it");
                DestroySurface();
                _redraw = true;
            }
            else UdpLog.Warn(Tag, "eglSwapBuffers failed: 0x" + err.ToString("x"));
        }

        /// <summary>Releases everything (the activity is being destroyed).</summary>
        public void ReleaseGpu()
        {
            Stop();
            DestroyContext();
            if (_displayReady) { try { EGL14.EglTerminate(_dpy); } catch (Exception) { } _displayReady = false; }
        }

        // ================================================================ TextureView.ISurfaceTextureListener
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            UdpLog.Info(Tag, "surface available " + width + "x" + height);
            _st = surface; _surfaceAttempts = 0;
            ApplyBufferSize(width, height);
            UpdateMetrics();
            _redraw = true;
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            UdpLog.Info(Tag, "surface destroyed");
            DestroySurface();                // before the SurfaceTexture goes away; the context and Skia's resources stay
            _st = null;
            return true;                     // the TextureView releases the SurfaceTexture
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {
            ApplyBufferSize(width, height);  // the TextureView has just set the buffer to the view's size: ours may be smaller
            UpdateMetrics();
            _redraw = true;
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

        private void ApplyBufferSize(int viewW, int viewH)
        {
            ScreenMath.RenderSize(viewW, viewH, _maxLongSide, out _bufW, out _bufH);
            try { if (_st != null) _st.SetDefaultBufferSize(_bufW, _bufH); } catch (Exception ex) { UdpLog.Warn(Tag, "setDefaultBufferSize: " + ex.Message); }
        }

        // ================================================================ metrics
        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            if (_st != null) ApplyBufferSize(w, h); else ScreenMath.RenderSize(w, h, _maxLongSide, out _bufW, out _bufH);
            UpdateMetrics();
        }

        public override WindowInsets OnApplyWindowInsets(WindowInsets insets)
        {
            try
            {
                var ins = ScreenInsets.None;
                if (_profile != null && !_profile.IgnoresSafeInsets && insets != null)
                {
                    int l, t, r, b;
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                    {
                        // the bars, the notch and the strip where the system's Home gesture cannot be overridden by the app (not the side strips of the Back gesture: the canvas would lose its width)
                        var i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.MandatorySystemGestures());
                        l = i.Left; t = i.Top; r = i.Right; b = i.Bottom;
                    }
                    else
                    {
#pragma warning disable CA1422
                        l = insets.SystemWindowInsetLeft; t = insets.SystemWindowInsetTop; r = insets.SystemWindowInsetRight; b = insets.SystemWindowInsetBottom;
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                        {
                            var g = insets.MandatorySystemGestureInsets;
                            if (g != null) { l = Math.Max(l, g.Left); t = Math.Max(t, g.Top); r = Math.Max(r, g.Right); b = Math.Max(b, g.Bottom); }
                        }
#pragma warning restore CA1422
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                        {
                            var dc = insets.DisplayCutout;
                            if (dc != null) { l = Math.Max(l, dc.SafeInsetLeft); t = Math.Max(t, dc.SafeInsetTop); r = Math.Max(r, dc.SafeInsetRight); b = Math.Max(b, dc.SafeInsetBottom); }
                        }
                    }
                    ins = new ScreenInsets(l, t, r, b);
                }
                if (!ins.Equals(_insets)) { _insets = ins; UpdateMetrics(); }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "insets: " + ex.Message); }
            try
            {
                // Android 11 and later tell when the keyboard goes away by itself (its hide button, a gesture): that is a dismissal by the system
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R && insets != null)
                {
                    bool visible = insets.IsVisible(WindowInsets.Type.Ime());
                    var t = TextInput;
                    if (_imeWasVisible && !visible && t != null && t.IsShown) t.SystemDismissed("keyboard hidden");
                    _imeWasVisible = visible;
                }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "ime insets: " + ex.Message); }
            return base.OnApplyWindowInsets(insets);
        }

        protected override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            int bw = _sk != null ? _skW : _bufW, bh = _sk != null ? _skH : _bufH;
            var m = ScreenMath.Metrics(Width, Height, bw, bh, _insets, Context != null ? Context.Resources.DisplayMetrics.Density : _density, _profile != null && _profile.Capabilities.HasTouch);
            bool viewMoved = Width != _lastViewW || Height != _lastViewH;        // the same surface in a different view: the video and the touches follow the view
            _lastViewW = Width; _lastViewH = Height;
            if (m.Equals(_metrics) && !viewMoved) return;
            _metrics = m;
            UdpLog.Info(Tag, "screen " + m);
            var h = ScreenChanged;
            if (h != null) { try { h(); } catch (Exception ex) { UdpLog.Warn(Tag, "ScreenChanged handler: " + ex.Message); } }
        }

        protected override void OnWindowVisibilityChanged(ViewStates visibility)
        {
            base.OnWindowVisibilityChanged(visibility);
            if (visibility == ViewStates.Visible) _redraw = true;
        }

        // ================================================================ ISnapshotSource
        public void AfterNextFrame(Action action)
        {
            lock (_snapGate) _snapActions.Add(action);
            RequestRedraw();
        }

        public byte[] SnapshotPng()
        {
            var sk = _sk;
            if (sk == null) return null;
            try
            {
                var info = new SKImageInfo(_skW, _skH, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var bmp = new SKBitmap(info))
                {
                    if (!sk.ReadPixels(info, bmp.GetPixels(), bmp.RowBytes, 0, 0)) return null;
                    using (var img = SKImage.FromBitmap(bmp))
                    using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                        return data == null ? null : data.ToArray();
                }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "snapshot: " + ex.Message); return null; }
        }

        private void RunSnapshotActions()
        {
            Action[] actions;
            lock (_snapGate)
            {
                if (_snapActions.Count == 0) return;
                actions = _snapActions.ToArray(); _snapActions.Clear();
            }
            foreach (var a in actions) { try { a(); } catch (Exception ex) { UdpLog.Warn(Tag, "snapshot action: " + ex.Message); } }
        }

        // ================================================================ system keyboard
        /// <summary>The system keyboard for PUi's text fields; set by the platform when the device is a touch one (see AndroidTextInput).</summary>
        public AndroidTextInput TextInput { get; set; }
        private bool _imeWasVisible;

        /// <summary>The view is a text editor only while the keyboard is wanted: otherwise the system would route every key event through the keyboard's input session before the activity sees it.</summary>
        public override bool OnCheckIsTextEditor() { var t = TextInput; return t != null && t.IsShown; }

        public override IInputConnection OnCreateInputConnection(EditorInfo outAttrs)
        {
            var t = TextInput;
            return t != null && t.IsShown ? t.CreateConnection(outAttrs) : null;
        }

        /// <summary>With the keyboard on screen the Back key goes through here BEFORE the keyboard handles it: it closes the keyboard and is not delivered as a Back key.</summary>
        public override bool OnKeyPreIme(Keycode keyCode, KeyEvent e)
        {
            var t = TextInput;
            if (keyCode == Keycode.Back && t != null && t.IsShown)
            {
                if (e.Action == KeyEventActions.Down) t.SystemDismissed("Back");
                return true;
            }
            return base.OnKeyPreIme(keyCode, e);
        }

        // ================================================================ statistics and log
        /// <summary>For the statistics line: frames drawn, how long the last draw and swap took, the slowest draw, failures and the size of the surface.</summary>
        public string Counters()
        {
            return "frames=" + _frames + " draw=" + _drawMsLast.ToString("0.0") + "ms(max " + _drawMsMax.ToString("0.0") + ") swap=" + _swapMsLast.ToString("0.0") + "ms swapFail=" + _swapFailures +
                " drawErr=" + _drawErrors + " surf=" + _skW + "x" + _skH + " ctx=#" + _contextGeneration;
        }

        /// <summary>Resets the slowest-draw record (the statistics line does it after each report).</summary>
        public void ResetCounters() { _drawMsMax = 0; }

        private void LogRateLimited(string what, Exception ex)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastErrorLogTicks < Stopwatch.Frequency) return;          // at most one line per second: a failing frame repeats every frame
            _lastErrorLogTicks = now;
            UdpLog.Error(Tag, what + ": " + ex);
        }
    }
}
