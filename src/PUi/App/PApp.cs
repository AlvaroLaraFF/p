using System;
using System.Collections.Generic;
using System.Diagnostics;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Sources;
using AceEngine.Util;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using SkiaSharp;

namespace PUi.App
{
    /// <summary>
    /// UI root: screen stack, key dispatch, frame pacing (every refresh while something moves fast, ~30 fps with only the
    /// background lights, nothing if everything is still), opaque or transparent window depending on the screen, audio focus and lifecycle.
    /// </summary>
    public sealed class PApp : IFramePresenter, INavigator, IDisposable
    {
        private readonly IPlatform _platform;
        private readonly TvServices _services;
        private readonly UiContext _ctx;
        private readonly Stack<IScreen> _stack = new Stack<IScreen>();
        private volatile bool _invalid = true;
        private double _lastDraw = -1;
        private IScreen _opaqueScreen;
        private bool _lastOpaque = true, _opaqueKnown, _suspended, _disposed;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Interval between frames when only the background lights move.</summary>
        public double AmbientInterval = 1.0 / 30.0;
        /// <summary>
        /// Rest: after CalmAfter seconds without touching the remote, the repeating decoration (the focus's fluid border, strip, live-dot pulse) slows down
        /// along a smooth curve and is stopped at RestAfter; the frame rate drops with its speed and, once stopped, nothing is drawn until something changes or a key is pressed.
        /// The loading indicators (spinner, skeleton shimmer, lamp, buffer ring) do not slow down. With a very large CalmAfter the old behavior returns (30 frames per s non-stop).
        /// </summary>
        public double CalmAfterSeconds { get { return _ctx.Decor.CalmAfter; } set { _ctx.Decor.CalmAfter = value; } }
        public double RestAfterSeconds { get { return _ctx.Decor.RestAfter; } set { _ctx.Decor.RestAfter = value; } }
        /// <summary>Maximum interval between frames while the decoration slows down (the speed drops and the rate with it: one step of the border stays just as short).</summary>
        public double DecorMaxInterval = 0.5;
        private double _lastInputAt = double.NaN;
        private bool _restSignalled;
        private volatile bool _inputPending;

        // drawing statistics (for the performance log)
        public long FramesDrawn { get; private set; }
        /// <summary>GPU warm-up after startup (one step per frame, off-screen). Disabled by default; the TV enables it.</summary>
        public bool GpuWarmupEnabled;
        private readonly GpuWarmup _warm = new GpuWarmup();
        public GpuWarmup Warmup { get { return _warm; } }
        public double DrawMsSum { get; private set; }
        public double DrawMsMax { get; private set; }
        public void ResetStats() { FramesDrawn = 0; DrawMsSum = 0; DrawMsMax = 0; }
        // Screen ticks (each vsync, NeedsFrame) by what was requested, for the performance log: how many there were, how many asked for a frame and why, and what the Update logic costs
        public long Ticks { get; private set; }
        public long TicksFull { get; private set; }
        public long TicksAmbient { get; private set; }
        public long TicksIdle { get; private set; }
        public long FramesFull { get; private set; }
        public long FramesAmbient { get; private set; }
        public double UpdateMsSum { get; private set; }
        /// <summary>true while the interface is at rest: the decoration is stopped and nothing is drawn.</summary>
        public bool DecorRest { get; private set; }
        /// <summary>Why the current screen asks for frames ("motion", "loading", "decor"; empty if it does not say): for the performance log.</summary>
        public string DemandReason { get { var ss = Current as SectionScreen; return ss != null && ss.DemandReason != null ? ss.DemandReason : ""; } }
        /// <summary>Seconds since the last key (for the log).</summary>
        public double QuietSeconds(double now) { return double.IsNaN(_lastInputAt) ? 0 : now - _lastInputAt; }
        /// <summary>Resets only the maximum (the accumulated counters keep going so per-interval averages can be computed).</summary>
        public void ResetStatsMax() { DrawMsMax = 0; }

        public PApp(TvServices services, IPlatform platform)
        {
            _services = services; _platform = platform;
            _ctx = new UiContext(services, platform, this, platform.Paths.FontsDir);
            platform.Render.SetPresenter(this);
            platform.Input.KeyPressed += OnKey;
            platform.Input.Pointer += OnPointer;
            platform.Render.ScreenChanged += OnScreenChanged;
        }

        // ---------------------------------------------------------------- surface
        private volatile bool _screenDirty = true;
        private int _screenPw, _screenPh;
        private ScreenInsets _screenInsets;
        private float _screenDensity = 1f;

        /// <summary>The platform's surface changed (rotation, split screen, insets): the next frame reads its metrics again.</summary>
        private void OnScreenChanged() { _screenDirty = true; Invalidate(); }

        /// <summary>
        /// Insets and density of the platform's ScreenMetrics, read only when the platform says they changed or the drawn size is not the one they were read for (the platform's Screen can
        /// allocate: the TV is asked once, not on every frame). They only apply if they describe the surface being drawn.
        /// </summary>
        private void ReadScreen(int pixelWidth, int pixelHeight)
        {
            if (!_screenDirty && pixelWidth == _screenPw && pixelHeight == _screenPh) return;
            _screenDirty = false; _screenPw = pixelWidth; _screenPh = pixelHeight;
            _screenInsets = ScreenInsets.None; _screenDensity = 1f;
            try
            {
                var m = _platform.Render.Screen;
                if (m != null && m.PixelWidth == pixelWidth && m.PixelHeight == pixelHeight) { _screenInsets = m.SafeInsets; _screenDensity = m.Density; }
            }
            catch (Exception ex) { AceLog.Warn("ui", "screen: " + ex.Message); }
        }

        /// <summary>
        /// Takes the platform's size, insets and density into the context BEFORE anything is laid out or drawn: the screens of a phone or a tablet are laid out from the logical canvas, which
        /// depends on them (on the TV nothing depends on them and this changes nothing). Called when the app starts and on every tick, so a rotation or a resize reaches the layout even
        /// before the next frame is drawn; Draw takes the size of the surface it is given.
        /// </summary>
        private void SyncSurface()
        {
            int pw, ph;
            try { pw = _platform.Render.PixelWidth; ph = _platform.Render.PixelHeight; }
            catch (Exception) { return; }
            if (pw <= 0 || ph <= 0) return;
            ReadScreen(pw, ph);
            _ctx.PixelW = pw; _ctx.PixelH = ph; _ctx.Insets = _screenInsets; _ctx.Density = _screenDensity;
        }

        public UiContext Context { get { return _ctx; } }
        public IScreen Current { get { lock (_stack) return _stack.Count == 0 ? null : _stack.Peek(); } }
        public int Depth { get { lock (_stack) return _stack.Count; } }

        public string Describe()
        {
            var s = Current;
            return s == null ? "(no screen)" : s.Describe();
        }

        // ---------------------------------------------------------------- lifecycle
        public void Start()
        {
            _platform.Audio.Acquire();
            _services.Catalog.Start();
            var cfg = _services.Config;
            SyncSurface();
            Push(new MainScreen());
            if (!string.IsNullOrEmpty(cfg.AutoPlay))
            {
                var entry = LinkEntry.FromUrl(string.IsNullOrEmpty(cfg.AutoPlayName) ? "Prueba" : cfg.AutoPlayName, cfg.AutoPlay);
                var ch = new Channel { Id = entry.Key ?? entry.Url, Name = entry.Name, SourceName = entry.Name, Entry = entry };
                Push(new PlayerScreen(new PlayContext(new List<Channel> { ch }, 0)));
            }
            _platform.Render.Start();
        }

        /// <summary>After an app pause (Home, source change) shorter than this, coming back reopens the same channel live; if it was longer, it returns to the list.</summary>
        public double ResumeSameChannelSeconds = 120;
        /// <summary>Clock in seconds (for tests).</summary>
        public Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private PlayContext _resumeCtx;
        private double _pausedAt;

        /// <summary>
        /// The app goes to the background (Home, source change). With a player open: the player and the engine session are stopped, the player is REMOVED from the stack at once
        /// (the list it was opened from stays, without reactivating it because the engine stops) and the window goes back to opaque before leaving (so on return it cannot stay
        /// transparent showing the TV source); the audio focus is released and the probing and the P2P network are stopped (nothing keeps downloading in the background).
        /// </summary>
        public void OnPause()
        {
            if (_suspended) return;
            _suspended = true;
            _pausedAt = Clock();
            var s = Current;
            var player = s as PlayerScreen;
            _resumeCtx = player != null && player.Context.Current != null ? new PlayContext(player.Context.List, player.Context.Index) : null;
            if (s != null) s.Suspend();
            if (_ctx.Compact && _ctx.HasCastUi) _ctx.CastUi.Suspend();
            if (player != null) PopSilently();
            ForceOpaque();
            _platform.Audio.Release();
            if (_services.Suspend != null) _services.Suspend();
            Log("app paused" + (_resumeCtx != null ? " (with channel '" + _resumeCtx.Current.Name + "' open)" : ""));
        }

        /// <summary>On return: the audio focus is re-acquired; after a short pause the same channel is reopened live, after a long one the previous screen stays (list or TV).</summary>
        public void OnResume()
        {
            if (!_suspended) return;
            _suspended = false;
            _inputPending = true;                                   // coming back to the app counts as touching the remote: the decoration recovers its speed
            _platform.Audio.Acquire();
            if (_services.Resume != null) _services.Resume();
            double away = Clock() - _pausedAt;
            var ctx = _resumeCtx; _resumeCtx = null;
            if (ctx != null && ctx.Current != null && away < ResumeSameChannelSeconds)
            {
                Log("back after " + (int)away + " s: reopening '" + ctx.Current.Name + "' live");
                Push(new PlayerScreen(ctx));
            }
            else
            {
                if (ctx != null) Log("back after " + (int)away + " s: staying on the previous screen");
                var s = Current; if (s != null) s.Resume();
            }
            Invalidate();
        }

        /// <summary>
        /// The system is short of memory (Android's onTrimMemory): drops every cache that can be rebuilt (sprites and their GPU textures, halo masks, the loading lamp's surfaces); what is on
        /// screen is rebuilt on the next frame, so a frame is requested. Call it on the interface thread, never from inside a draw. It does not touch the screen stack, the engine or
        /// the background image (one image that is rebuilt in the background: dropping it would flash the fallback gradient). Nothing calls it on the Samsung TV.
        /// </summary>
        public void TrimMemory()
        {
            if (_disposed) return;
            _ctx.TrimMemory();
            Log("memory trimmed");
            Invalidate();
        }

        /// <summary>Removes the top screen WITHOUT entering the one below (the engine is stopped: the list is reactivated in OnResume).</summary>
        private void PopSilently()
        {
            IScreen top = null;
            lock (_stack) { if (_stack.Count > 1) top = _stack.Pop(); }
            if (top == null) return;
            Log("silent pop " + top.Name);
            top.Leave();
        }

        private void ForceOpaque()
        {
            try { _platform.Window.Opaque = true; } catch (Exception ex) { AceLog.Warn("ui", "window: " + ex.Message); }
            _lastOpaque = true; _opaqueKnown = true; _opaqueScreen = Current;
        }

        // ---------------------------------------------------------------- navigation
        public void Push(IScreen screen)
        {
            IScreen prev;
            lock (_stack) prev = _stack.Count == 0 ? null : _stack.Peek();
            if (prev != null) prev.Leave();
            lock (_stack) _stack.Push(screen);
            Log("push " + screen.Name);
            screen.Enter(_ctx);
            Invalidate();
        }

        public void Pop()
        {
            IScreen top, next = null;
            lock (_stack)
            {
                if (_stack.Count <= 1) { top = null; }
                else { top = _stack.Pop(); next = _stack.Peek(); }
            }
            if (top == null) { Exit(); return; }
            Log("pop " + top.Name + " -> " + next.Name);
            top.Leave();
            next.Enter(_ctx);
            Invalidate();
        }

        public void GoSection(Section section)
        {
            var cur = Current as SectionScreen;
            if (cur != null && cur.Section == section) return;
            IScreen scr = section == Section.Tv ? (IScreen)new MainScreen() : section == Section.Search ? (IScreen)new SearchScreen() : new SettingsScreen();
            var old = new List<IScreen>();
            lock (_stack) { while (_stack.Count > 0) old.Add(_stack.Pop()); _stack.Push(scr); }
            if (old.Count > 0) old[0].Leave();
            Log("section " + section);
            scr.Enter(_ctx);
            Invalidate();
        }

        public void Exit()
        {
            Log("exit");
            var s = Current; if (s != null) s.Leave();
            _platform.Audio.Release();
            _platform.Exit();
        }

        public void Post(Action action) { _platform.UiThread.Post(action); }
        public void Invalidate() { _invalid = true; _platform.Render.RequestRedraw(); }

        private static void Log(string m) { AceLog.Info("ui", m); }

        /// <summary>A UI operation that takes more than 40 ms blocks the main thread (dropped frames): it is logged with the screen so it can be fixed.</summary>
        private static void SlowLog(string what, long startTicks, IScreen s)
        {
            double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            if (ms > 40) AceLog.Warn("ui", "SLOW " + what + ": " + (int)ms + " ms in " + s.Name);
        }

        /// <summary>
        /// The cast interface (sheet, strip, notices) works on top of the current screen: only on a phone or a tablet whose platform can send. On the TV this is false and nothing of it is created, drawn or asked.
        /// It also tells the cast interface whether the player is the current screen (the player draws the sending state itself).
        /// </summary>
        private bool CastUiTakes(IScreen current)
        {
            if (!_ctx.Compact || !_ctx.Cast.IsAvailable) return false;
            _ctx.CastUi.OnPlayerScreen = current is PlayerScreen;
            return true;
        }

        // ---------------------------------------------------------------- keys
        public void OnKey(KeyPress key)
        {
            if (_disposed || _suspended) return;
            var s = Current;
            if (s == null) return;
            _inputPending = true;                                   // the next screen tick records the time of this key (rest: see CalmAfterSeconds)
            long t0 = Stopwatch.GetTimestamp();
            if (CastUiTakes(s) && _ctx.CastUi.OnKey(key)) { Invalidate(); return; }         // the open sheet of "Enviar a la tele" takes the keys (Back closes it)
            try { s.OnKey(key); } catch (Exception ex) { AceLog.Warn("ui", "key " + key + ": " + ex); }
            SlowLog("key " + key.Key, t0, s);
            Invalidate();
        }

        // ---------------------------------------------------------------- touch
        /// <summary>
        /// A finger event in logical coordinates (see PointerEvent). Only platforms with PlatformCapabilities.HasTouch use it: anywhere else it is ignored, so the remote-driven
        /// interface (the Samsung TV) never changes because of it.
        /// </summary>
        public void OnPointer(PointerEvent e)
        {
            if (_disposed || _suspended) return;
            var caps = _platform.Capabilities;
            if (caps == null || !caps.HasTouch) return;
            var s = Current;
            if (s == null) return;
            _inputPending = true;                                   // touching counts as using the interface: the decoration recovers its speed
            if (e.Time <= 0) e.Time = _ctx.Now;                     // a platform that does not stamp its events: the time of the last frame
            long t0 = Stopwatch.GetTimestamp();
            if (CastUiTakes(s) && _ctx.CastUi.OnPointer(e)) { Invalidate(); return; }       // the sheet, the strip or the notice of "Enviar a la tele" took the finger
            try { s.OnPointer(e); } catch (Exception ex) { AceLog.Warn("ui", "pointer " + e + ": " + ex); }
            SlowLog("pointer " + e.Action, t0, s);
            Invalidate();
        }

        // ---------------------------------------------------------------- frames
        public bool NeedsFrame(double now)
        {
            if (_disposed || _suspended) return false;
            var s = Current;
            if (s == null) return false;
            _ctx.Now = now;
            SyncSurface();
            if (double.IsNaN(_lastInputAt) || _inputPending) { _inputPending = false; _lastInputAt = now; }
            _ctx.Decor.Advance(now, now - _lastInputAt);
            // At rest (the decoration stopped: 30 s without touching the remote) the checker slows down the retry rate; pressing a key restores it
            bool rest = _ctx.Decor.Stopped;
            if (rest != _restSignalled)
            {
                _restSignalled = rest;
                var aware = _services.Checker as IRestAware;
                if (aware != null) { try { aware.SetResting(rest); } catch (Exception ex) { AceLog.Warn("ui", "rest: " + ex.Message); } }
                Log(rest ? "rest: the interface stops drawing and the checker slows down the retry rate" : "activity: the interface and the checker return to the normal rate");
            }
            FrameDemand d;
            long u0 = Stopwatch.GetTimestamp();
            try { d = s.Update(now); } catch (Exception ex) { AceLog.Warn("ui", "Update: " + ex); d = FrameDemand.Full; }
            if (CastUiTakes(s)) { try { var cd = _ctx.CastUi.Update(now); if (cd > d) d = cd; } catch (Exception ex) { AceLog.Warn("ui", "cast Update: " + ex); } }
            SlowLog("update", u0, s);
            UpdateMsSum += (Stopwatch.GetTimestamp() - u0) * 1000.0 / Stopwatch.Frequency;
            if (_invalid) { d = FrameDemand.Full; }
            // when the screen changes it is applied again: the platform may have made the window transparent on its own before creating the player
            if (!ReferenceEquals(s, _opaqueScreen)) { _opaqueScreen = s; _opaqueKnown = false; }
            if (s.WindowOpaque != _lastOpaque || !_opaqueKnown)
            {
                try { _platform.Window.Opaque = s.WindowOpaque; } catch (Exception ex) { AceLog.Warn("ui", "window: " + ex.Message); }
                _lastOpaque = s.WindowOpaque; _opaqueKnown = true;
                d = FrameDemand.Full;
            }
            Ticks++;
            if (d == FrameDemand.Idle) { TicksIdle++; return false; }
            if (d == FrameDemand.Decor)
            {
                // only repeating decoration: at normal speed, the usual rate; slowing down, the rate drops with the speed; stopped, nothing (the last image stays on screen)
                double rate = _ctx.Decor.Rate;
                if (_ctx.Decor.Stopped) { TicksIdle++; DecorRest = true; return false; }
                DecorRest = false;
                TicksAmbient++;
                double interval = Math.Min(DecorMaxInterval, AmbientInterval / Math.Max(rate, 0.01));
                bool dueDecor = _lastDraw < 0 || now - _lastDraw >= interval - 0.004;
                if (dueDecor) FramesAmbient++;
                return dueDecor;
            }
            if (d == FrameDemand.Ambient)
            {
                TicksAmbient++;
                bool due = _lastDraw < 0 || now - _lastDraw >= AmbientInterval - 0.004;
                if (due) FramesAmbient++;
                return due;
            }
            TicksFull++; FramesFull++;
            return true;
        }

        public void Draw(SKCanvas canvas, GRContext context, double now, int pixelWidth, int pixelHeight)
        {
            var s = Current;
            if (s == null || _disposed) return;
            long t0 = Stopwatch.GetTimestamp();
            _invalid = false; _lastDraw = now;
            if (!double.IsNaN(_lastInputAt)) _ctx.Decor.Advance(now, now - _lastInputAt);
            ReadScreen(pixelWidth, pixelHeight);
            _ctx.Gpu = context; _ctx.PixelW = pixelWidth; _ctx.PixelH = pixelHeight; _ctx.Insets = _screenInsets; _ctx.Density = _screenDensity; _ctx.Now = now;
            _ctx.Sprites.BeginFrame(context);
            canvas.Save();
            var surface = _ctx.Surface;
            if (surface.CanvasLeft != 0 || surface.CanvasTop != 0) canvas.Translate(surface.CanvasLeft, surface.CanvasTop);       // letterbox: the canvas is not at the corner
            canvas.Scale(_ctx.K);
            if (GpuWarmupEnabled && !_warm.Done && FramesDrawn >= 8) _warm.Step(_ctx, now);
            int created0 = _ctx.Sprites.Created;
            try { s.Draw(canvas, now); }
            catch (Exception ex) { AceLog.Warn("ui", "Draw " + s.Name + ": " + ex); }
            if (CastUiTakes(s)) { try { _ctx.CastUi.Draw(canvas, now); } catch (Exception ex) { AceLog.Warn("ui", "cast Draw: " + ex); } }
            SlowLog("draw (new sprites " + (_ctx.Sprites.Created - created0) + ")", t0, s);
            canvas.Restore();
            _ctx.Sprites.EndFrame();
            if (_ctx.Sprites.Deferred > 0) Invalidate();             // sprites remain to be created (per-frame budget): another frame
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            FramesDrawn++; DrawMsSum += ms; if (ms > DrawMsMax) DrawMsMax = ms;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _platform.Input.KeyPressed -= OnKey; _platform.Input.Pointer -= OnPointer; _platform.Render.ScreenChanged -= OnScreenChanged; } catch { }
            lock (_stack) { foreach (var s in _stack) { try { s.Leave(); } catch { } } _stack.Clear(); }
            _ctx.Dispose();
        }
    }
}
