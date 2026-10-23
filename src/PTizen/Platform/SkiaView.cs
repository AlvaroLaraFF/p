using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AceEngine.App;
using ElmSharp;
using PUi.Platform;
using SkiaSharp;

namespace PTizen.Platform
{
    /// <summary>
    /// Evas image object that shows a Skia surface with GPU (Evas GL + the GRContext of the TV's libSkiaSharp.so). The TV's libSkiaSharp.so does not
    /// export the Evas interface, so the GRGlInterface is assembled by hand with evas_gl_proc_address_get. evas_gl_make_current has to be called
    /// on every frame, and painting happens inside the image's pixels callback, which is triggered by marking it "dirty" from an Ecore animator (one tick per refresh).
    /// </summary>
    internal sealed class SkiaView : EvasObject, IRenderHost, ISnapshotSource
    {
        public int PixelW { get; private set; }
        public int PixelH { get; private set; }
        public string GpuInfo;
        public bool ImageAlpha = false;

        private IntPtr _evas, _image, _glEvas, _glCtx, _glSurf, _glCfg, _libGles;
        private GRContext _context;
        private SKSurface _surface;
        private GRBackendRenderTargetDesc _rt;
        private Evas.ImagePixelsCallback _cb;
        private IntPtr _animator;
        private IFramePresenter _presenter;
        private volatile bool _redraw;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile bool _paused;
        private volatile Action _oneShot;
        public int FramesPresented;
        /// <summary>0 = nothing, 1 = glFlush, 2 = glFinish after painting each frame (to rule out Evas showing the texture before the GPU finishes).</summary>
        public int SyncMode = 0;

        // ---- presentation diagnostics: counters and a frame-by-frame pixel probe ----
        private long _dirtied, _callbacks, _flushes, _renders;
        private double _lastTick, _lastCb;
        private static string GcInfo() { return "gc=" + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2); }
        private Evas.EventCallback _flushCb, _renderCb;
        private volatile bool _probing;
        private int[][] _probePts;
        private readonly System.Collections.Generic.List<string> _probeLog = new System.Collections.Generic.List<string>();
        private readonly byte[] _px = new byte[4];
        private double _probeT0;
        private readonly System.Collections.Generic.List<double> _tFlush = new System.Collections.Generic.List<double>(), _tDraw = new System.Collections.Generic.List<double>();

        /// <summary>Regularity of the presentation during the probe: intervals between Evas flushes and the delay from when we finish painting to the flush.</summary>
        public string TimingSummary()
        {
            double[] f, d;
            lock (_tFlush) f = _tFlush.ToArray();
            lock (_tDraw) d = _tDraw.ToArray();
            if (f.Length < 3) return "flushes=" + f.Length;
            var iv = new double[f.Length - 1];
            for (int i = 1; i < f.Length; i++) iv[i - 1] = f[i] - f[i - 1];
            var s = (double[])iv.Clone(); Array.Sort(s);
            int over = 0, under = 0; foreach (var x in iv) { if (x > 40) over++; if (x < 10) under++; }
            // delay: for each end of painting, the next flush
            var lags = new System.Collections.Generic.List<double>(); int j = 0;
            foreach (var t in d) { while (j < f.Length && f[j] < t) j++; if (j < f.Length) lags.Add(f[j] - t); }
            lags.Sort();
            return "flushes=" + f.Length + " interval_ms med=" + s[s.Length / 2].ToString("0.0") + " p95=" + s[(int)(s.Length * 0.95)].ToString("0.0") + " max=" + s[s.Length - 1].ToString("0.0")
                + " >40ms=" + over + " <10ms=" + under + (lags.Count > 0 ? " paint_to_flush_delay med=" + lags[lags.Count / 2].ToString("0.0") + " max=" + lags[lags.Count - 1].ToString("0.0") : "");
        }

        /// <summary>Accumulated counters: times the image was marked dirty, Evas paint callbacks, flushes and renders of the Evas canvas.</summary>
        public string EvasCounters() { return "dirty=" + _dirtied + " pixelscb=" + _callbacks + " flush=" + _flushes + " render=" + _renders + " presented=" + FramesPresented; }

        /// <summary>Starts reading, after each painted frame, the pixels in the list (x,y in 1920x1080 coordinates). Tests only.</summary>
        public void ProbeStart(int[][] points) { lock (_probeLog) _probeLog.Clear(); lock (_tFlush) _tFlush.Clear(); lock (_tDraw) _tDraw.Clear(); _g1 = _g2 = null; _probePts = points; _probeT0 = _clock.Elapsed.TotalSeconds; _probing = true; }

        public System.Collections.Generic.List<string> ProbeStop() { _probing = false; lock (_probeLog) return new System.Collections.Generic.List<string>(_probeLog); }

        // ---- grid probe: reads the WHOLE surface on every frame and looks for "spikes" (cells that change and revert on the next frame) anywhere ----
        private const int GW = 48, GH = 27;
        private byte[] _full;
        private float[] _g1, _g2, _g0 = new float[GW * GH];
        private int _gridFrames;

        public bool GridProbe;

        private void GridFrame(System.Text.StringBuilder sb)
        {
            if (_full == null) _full = new byte[PixelW * PixelH * 4];
            Gles.glReadPixels(0, 0, PixelW, PixelH, Gles.GL_RGBA, Gles.GL_UNSIGNED_BYTE, _full);
            var cur = new float[GW * GH];
            double sum = 0;
            for (int cy = 0; cy < GH; cy++)
                for (int cx = 0; cx < GW; cx++)
                {
                    int px = (int)((cx + 0.5) * PixelW / GW), py = PixelH - 1 - (int)((cy + 0.5) * PixelH / GH);
                    int o = (py * PixelW + px) * 4;
                    float l = 0.2126f * _full[o] + 0.7152f * _full[o + 1] + 0.0722f * _full[o + 2];
                    cur[cy * GW + cx] = l; sum += l;
                }
            int chg = 0, spk = 0; var cells = new System.Text.StringBuilder();
            if (_g1 != null)
                for (int i = 0; i < cur.Length; i++)
                {
                    float d1 = Math.Abs(cur[i] - _g1[i]);
                    if (d1 > 10) chg++;
                    if (_g2 != null && Math.Abs(cur[i] - _g2[i]) <= 4 && Math.Abs(_g1[i] - _g2[i]) > 10 && d1 > 10 && spk++ < 8) cells.Append(' ').Append(i % GW).Append(':').Append(i / GW);
                }
            _g2 = _g1; _g1 = cur;
            sb.Append("G ").Append(chg).Append(' ').Append(spk).Append(' ').Append((int)(sum / cur.Length * 10)).Append(cells).Append(' ');
        }

        private void ProbeFrame()
        {
            var pts = _probePts;
            if (pts == null) return;
            Gles.glFinish();
            var sb = new System.Text.StringBuilder();
            sb.Append((int)((_clock.Elapsed.TotalSeconds - _probeT0) * 1000)).Append(' ');
            if (GridProbe) GridFrame(sb);
            foreach (var p in pts)
            {
                int gx = p[0] * PixelW / 1920, gy = PixelH - 1 - p[1] * PixelH / 1080;
                Gles.glReadPixels(gx, gy, 1, 1, Gles.GL_RGBA, Gles.GL_UNSIGNED_BYTE, _px);
                sb.Append(_px[0]).Append(',').Append(_px[1]).Append(',').Append(_px[2]).Append(',').Append(_px[3]).Append(' ');
            }
            lock (_probeLog) _probeLog.Add(sb.ToString().TrimEnd());
        }

        public SkiaView(EvasObject parent) : base(parent) { }

        protected override IntPtr CreateHandle(EvasObject parent)
        {
            _evas = Evas.evas_object_evas_get(parent.Handle);
            _image = Evas.evas_object_image_filled_add(_evas);
            Evas.evas_object_image_colorspace_set(_image, Evas.ColorspaceArgb8888);
            Evas.evas_object_image_smooth_scale_set(_image, true);
            Evas.evas_object_image_alpha_set(_image, ImageAlpha);
            return _image;
        }

        int IRenderHost.PixelWidth { get { return PixelW; } }
        int IRenderHost.PixelHeight { get { return PixelH; } }
        /// <summary>Fixed 1920x1080, nothing covered.</summary>
        public ScreenMetrics Screen { get { return new ScreenMetrics(PixelW, PixelH); } }
        /// <summary>The TV's surface never changes: never raised.</summary>
        public event Action ScreenChanged { add { } remove { } }
        public void SetPresenter(IFramePresenter presenter) { _presenter = presenter; }
        public void RequestRedraw() { _redraw = true; }

        public void SetImageAlpha(bool a) { ImageAlpha = a; if (_image != IntPtr.Zero) Evas.evas_object_image_alpha_set(_image, a); }

        public void Init(int pw, int ph, Action<string> log)
        {
            PixelW = pw; PixelH = ph;
            Evas.evas_object_image_alpha_set(_image, ImageAlpha);
            Evas.evas_object_image_size_set(_image, pw, ph);
            InitGpu(log);
        }

        private IntPtr GetProc(object ctx, string name)
        {
            var p = Evas.evas_gl_proc_address_get(_glEvas, name);
            if (p == IntPtr.Zero) p = Egl.eglGetProcAddress(name);
            if (p == IntPtr.Zero && _libGles != IntPtr.Zero) p = Dl.dlsym(_libGles, name);
            return p;
        }

        private void InitGpu(Action<string> log)
        {
            _libGles = Dl.dlopen("libGLESv2.so", 2);
            _glEvas = Evas.evas_gl_new(_evas);
            var cfg = new Evas.Config { color_format = 1, depth_bits = 0, stencil_bits = 4, options_bits = 0, multisample_bits = 0, gles_version = 0 };
            _glCfg = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Evas.Config)));
            Marshal.StructureToPtr(cfg, _glCfg, false);
            _glCtx = Evas.evas_gl_context_version_create(_glEvas, IntPtr.Zero, 2);
            if (_glCtx == IntPtr.Zero) _glCtx = Evas.evas_gl_context_create(_glEvas, IntPtr.Zero);
            _glSurf = Evas.evas_gl_surface_create(_glEvas, _glCfg, PixelW, PixelH);
            Evas.NativeSurfaceOpenGL ns;
            Evas.evas_gl_native_surface_get(_glEvas, _glSurf, out ns);
            Evas.evas_object_image_native_surface_set(_image, ref ns);
            bool cur = Evas.evas_gl_make_current(_glEvas, _glSurf, _glCtx);
            Gles.glViewport(0, 0, PixelW, PixelH);
            GpuInfo = "GL_RENDERER=" + Gles.Str(Gles.GL_RENDERER) + " GL_VERSION=" + Gles.Str(Gles.GL_VERSION);
            log("evas gl ctx=" + _glCtx + " surf=" + _glSurf + " tex=" + ns.texture_id + " fbo=" + ns.framebuffer_id + " make_current=" + cur + " " + GpuInfo);

            var iface = GRGlInterface.AssembleGlesInterface(GetProc);
            if (iface == null) throw new InvalidOperationException("no GRGlInterface");
            _context = GRContext.Create(GRBackend.OpenGL, iface);
            if (_context == null) throw new InvalidOperationException("GRContext.Create returned null");
            int fbo, samples, stencil;
            Gles.glGetIntegerv(Gles.GL_FRAMEBUFFER_BINDING, out fbo);
            Gles.glGetIntegerv(Gles.GL_SAMPLES, out samples);
            Gles.glGetIntegerv(Gles.GL_STENCIL_BITS, out stencil);
            _rt = new GRBackendRenderTargetDesc
            {
                Width = PixelW, Height = PixelH,
                Config = SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? GRPixelConfig.Bgra8888 : GRPixelConfig.Rgba8888,
                Origin = GRSurfaceOrigin.BottomLeft,
                SampleCount = samples, StencilBits = stencil, RenderTargetHandle = (IntPtr)fbo,
            };
            _surface = SKSurface.Create(_context, _rt);
            if (_surface == null) throw new InvalidOperationException("SKSurface.Create(GPU) returned null");
            log("skia gpu ready fbo=" + fbo + " samples=" + samples + " stencil=" + stencil);
            _cb = (d, o) => OnPixels();
            Evas.evas_object_image_pixels_get_callback_set(_image, _cb, IntPtr.Zero);
            _flushCb = (d, e, i) => { _flushes++; if (_probing) lock (_tFlush) _tFlush.Add(_clock.Elapsed.TotalMilliseconds); };
            _renderCb = (d, e, i) => { _renders++; };
            try
            {
                Evas.evas_event_callback_add(_evas, Evas.CallbackRenderFlushPost, _flushCb, IntPtr.Zero);
                Evas.evas_event_callback_add(_evas, Evas.CallbackRenderPost, _renderCb, IntPtr.Zero);
            }
            catch (Exception ex) { log("Evas render counters not available: " + ex.Message); }
            Evas.evas_object_image_pixels_dirty_set(_image, true);
        }

        public void Start()
        {
            if (_animator != IntPtr.Zero) return;
            _animator = EcoreAnimator.AddAnimator(() =>
            {
                if (_paused || _presenter == null) return true;
                double now = _clock.Elapsed.TotalSeconds;
                if (_lastTick > 0 && now - _lastTick > 0.100) UdpLog.Warn("ui", "animator stalled " + (int)((now - _lastTick) * 1000) + " ms (the main thread was busy) " + GcInfo());
                _lastTick = now;
                bool need;
                try { need = _presenter.NeedsFrame(now); } catch (Exception ex) { UdpLog.Error("ui", "NeedsFrame: " + ex); need = false; }
                if (need || _redraw) { _redraw = false; _dirtied++; Evas.evas_object_image_pixels_dirty_set(_image, true); }
                return true;
            });
        }

        public void Stop() { if (_animator != IntPtr.Zero) { EcoreAnimator.RemoveAnimator(_animator); _animator = IntPtr.Zero; } }
        public void Pause(bool p) { _paused = p; }

        /// <summary>Runs 'action' within the next frame, with the GL context active (to dump the surface to PNG).</summary>
        public void AfterNextFrame(Action action) { _oneShot = action; _redraw = true; }

        private void OnPixels()
        {
            _callbacks++;
            double cbStart = _clock.Elapsed.TotalSeconds;
            if (_lastCb > 0 && cbStart - _lastCb > 0.150 && !_paused) UdpLog.Warn("ui", "paint: gap of " + (int)((cbStart - _lastCb) * 1000) + " ms since the previous callback " + GcInfo());
            _lastCb = cbStart;
            if (_surface == null || _presenter == null) { UdpLog.Warn("ui", "paint callback without surface or presenter: presenting WITHOUT drawing"); return; }
            Evas.evas_gl_make_current(_glEvas, _glSurf, _glCtx);
            _context.ResetContext();
            try { _presenter.Draw(_surface.Canvas, _context, _clock.Elapsed.TotalSeconds, PixelW, PixelH); }
            catch (Exception ex) { UdpLog.Error("ui", "Draw: " + ex); }
            _surface.Canvas.Flush();
            _context.Flush();
            if (SyncMode == 1) Gles.glFlush(); else if (SyncMode == 2) Gles.glFinish();
            FramesPresented++;
            double drawMs = (_clock.Elapsed.TotalSeconds - cbStart) * 1000;
            if (drawMs > 60) UdpLog.Warn("ui", "slow paint: " + (int)drawMs + " ms " + GcInfo());
            if (_probing) lock (_tDraw) _tDraw.Add(_clock.Elapsed.TotalMilliseconds);
            if (_probing) { try { ProbeFrame(); } catch (Exception ex) { UdpLog.Warn("ui", "probe: " + ex.Message); _probing = false; } }
            var shot = _oneShot;
            if (shot != null) { _oneShot = null; try { shot(); } catch (Exception ex) { UdpLog.Warn("ui", "capture: " + ex.Message); } }
        }

        /// <summary>PNG dump of the current frame (to check on the PC what is drawn on the TV).</summary>
        public byte[] SnapshotPng()
        {
            try
            {
                using (var img = _surface.Snapshot())
                using (var d = img.Encode(SKEncodedImageFormat.Png, 90)) return d.ToArray();
            }
            catch { return null; }
        }
    }
}
