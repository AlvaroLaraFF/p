using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AceEngine.App;
using AceEngine.App.Tv;
using PUi.Cast;
using PUi.Demo;
using PUi.Host;
using PUi.Input;
using PUi.Screens;

namespace PAndroid
{
    /// <summary>
    /// P! on Android: a video SurfaceView at the bottom and, over it, the TextureView where PUi draws the whole interface (the same sources as on the Samsung TV). This class only holds the activity's own
    /// part: it builds the platform pieces (view, player, audio focus, network, configuration, device profile), hands the system's events (keys, touch, lifecycle, memory) to the platform and to
    /// AppHost, and applies the screen lock and the system bars that the platform asks for (IOrientationControl). Startup, pause, resume and shutdown are PUi.Host.AppHost, the same as on the TV.
    ///
    /// Lifecycle: OnStart and OnStop are AppHost's Resume and Pause (OnPause also happens with the assistant, Alexa's overlay on a Fire TV, the quick settings or a split screen, and must not cut
    /// the channel: the audio focus the overlay takes is what pauses it, FireTvPackagingTests keeps OnPause empty); OnDestroy shuts down; onTrimMemory goes to PApp.TrimMemory.
    /// Also here: the notices of the player over the picture (NoticeOverlay), the HDMI monitor of a Fire TV (pause when the TV goes away) and the decoder report in the startup log.
    /// </summary>
    [Activity(Name = "org.p.app.MainActivity", Label = "P!", MainLauncher = true, Exported = true, Theme = "@style/PTheme", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round",
        Banner = "@drawable/banner", LaunchMode = LaunchMode.SingleTask, EnableOnBackInvokedCallback = false,
        WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateAlwaysHidden,
        ConfigurationChanges = ConfigChanges.ColorMode | ConfigChanges.Density | ConfigChanges.FontScale | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.LayoutDirection |
            ConfigChanges.Locale | ConfigChanges.Mcc | ConfigChanges.Mnc | ConfigChanges.Navigation | ConfigChanges.Orientation | ConfigChanges.ScreenLayout | ConfigChanges.ScreenSize |
            ConfigChanges.SmallestScreenSize | ConfigChanges.Touchscreen | ConfigChanges.UiMode)]
    [IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
    public sealed class MainActivity : Activity
    {
        private AndroidSkiaView _view;
        private VideoFrame _video;
        private AndroidPlatform _platform;
        private AndroidAudioFocus _audio;
        private AppHost _host;
        private TvRuntime _runtime;
        private AndroidOptions _options = new AndroidOptions();
        private AndroidDeviceProfile _profile;
        private HdmiMonitor _hdmi;
        private NoticeOverlay _notices;
        private Action<object, string> _noticeRaised;
        private Action<object> _noticeCleared;
        private bool _immersive;
        private CastController _cast;
        private Action _castStopRequested;
        private volatile bool _stopped;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            AndroidLog.InstallSink();                                   // also set by MainApplication; harmless to repeat
            base.OnCreate(savedInstanceState);
            var app = ApplicationContext;
            string data = FilesDir.AbsolutePath;
            string fonts = Path.Combine(data, "fonts");
            try { CopyAssets(data); } catch (Exception ex) { UdpLog.Error("app", "copying the fonts and images: " + ex); }

            // Configuration: the device profile depends on the "platform" options and the options on the layers, so the layers are read twice (they are tiny): first for the options
            var facts = AndroidDevice.ReadFacts(this);
            var layers = AndroidConfig.Layers(this);
            _options = AndroidOptions.From(AppConfigLoader.Load(layers, null, AppConfigDefaults.Android).Platform);
            _profile = AndroidDeviceProfile.From(facts, _options.ForcedKind, _options.MaxRenderSide, _options.AmbientFps);
            var profile = _profile;
            Action<AppConfig> defaults = c => { AppConfigDefaults.Android(c); profile.ApplyBudgets(c); };
            var config = AppConfigLoader.Load(layers, UdpLog.Engine, defaults);
            AppHost.StartLogging(config);
            UdpLog.Info("device", AndroidDevice.Describe(facts));
            UdpLog.Info("device", "profile: " + _profile.Describe() + "; player: " + _options.Player + (_options.ForcedKind.HasValue ? "; device kind FORCED by the configuration" : ""));
            // what the device can decode (MP2? AC-3?), read in the background and written to the log: the answer to "why has this channel no sound" is in the log of the device itself
            AndroidCodecs.Start();
            System.Threading.Tasks.Task.Run(() => AndroidCodecs.Log());
            AndroidCast.Init(this, _profile.Kind, _options.Cast);          // decides whether the Google Cast SDK may be used here (never on a Fire TV, never without Google Play services)
            string badKeys = AndroidDevice.CheckKeyCodes();
            if (badKeys.Length > 0) UdpLog.Warn("keys", "the key table does not match the SDK:" + badKeys); else UdpLog.Info("keys", "key table checked against the SDK");
            UdpLog.Info("config", "test configuration file: " + (AndroidConfig.DevOverridePath(this) ?? "(no external storage)"));

            // Views: the video at the bottom, the interface over it
            var root = new FrameLayout(this);
            _video = new VideoFrame(this);
            _view = new AndroidSkiaView(this, _profile, _options.MaxRenderSide);
            root.AddView(_video, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            // over everything: what the player tells the viewer about the channel (no decoder for its audio...)
            _notices = new NoticeOverlay(this, root, !_profile.IsTouchDevice);
            _noticeRaised = (owner, text) => _notices.Show(owner, text);
            _noticeCleared = owner => _notices.Hide(owner);
            PlayerNotices.Raised += _noticeRaised; PlayerNotices.Cleared += _noticeCleared;
            // A TV is landscape, a phone portrait, a tablet follows the sensor (OrientationPolicy). The lock goes before the first frame, so a phone does not start by drawing a landscape surface;
            // the bars need the window's decor view, which exists once there is a content view.
            bool immersive = OrientationPolicy.WantsImmersive(_profile.Kind, PUi.Platform.OrientationRequest.Device);
            LockScreen(OrientationPolicy.Startup(_profile.Kind));
            SetContentView(root);
            ApplyBars(immersive);

            _audio = new AndroidAudioFocus(app);
            _platform = new AndroidPlatform(this, _profile, _view, _video, _audio, data, fonts, RequestExit, ApplyOrientation);
            _view.ContextRecreated += () => { var a = _host != null ? _host.App : null; if (a != null) a.TrimMemory(); };
            var network = new AndroidNetworkInfo(app);
            var hooks = new PlatformHooks
            {
                ConfigDefaults = defaults,
                Network = network,
                Multicast = new AndroidMulticastLock(app),
                Players = new PlayerFactory(app, _video, _audio, _profile, _options),
                // the app going to the background (screen off, another app) stops the engine, unless a channel is being sent to another screen: then the engine is what serves it
                KeepEngineAlive = () => { var c = _cast; return c != null && c.Busy; },
            };
            _host = new AppHost(_platform, config, new AppHostHooks
            {
                RenderCounters = _view.Counters,
                RenderLabel = "egl",
                WindowState = () => _platform.InputState(),
                InjectKey = _platform.Raise,
                BeforeStart = app =>
                {
                    app.AmbientInterval = profile.AmbientInterval;       // 30 fps for the animated background, 20 on a weak TV stick (platform.ambientFps overrides it)
                    if (_cast != null) app.Context.Cast = _cast;          // "Enviar a la tele": only a phone or a tablet has it
                },
            }, hooks);
            _host.StartMonitoring();
            var services = BuildServices(config, hooks, data);
            StartCast(network);
            _host.Start(services, _runtime);
            // A Fire TV pauses when its screen goes away (the TV is switched off or to another input): Amazon requires it, and it spares the user's data
            if (_options.HdmiPause && HdmiPolicy.AppliesTo(_profile.Kind))
            {
                _hdmi = new HdmiMonitor(this, () => { if (_platform != null) _platform.Raise(new KeyPress(UiKey.Pause, "HDMI disconnected")); });
                _hdmi.Start();
            }
        }

        /// <summary>Builds "Enviar a la tele" on a phone or a tablet (nothing on a TV, a Fire TV or a box). Never lets a problem of it stop the app from starting.</summary>
        private void StartCast(AceEngine.Net.INetworkInfo network)
        {
            if (_runtime == null) return;                                                          // the interface demo has no engine
            try
            {
                _cast = AndroidCastFactory.Create(this, _profile, () => _runtime.Engine.Host, network);
                if (_cast == null) return;
                _castStopRequested = () => { var c = _cast; if (c != null) c.Stop(); };
                CastSendingService.StopRequested += _castStopRequested;
                // the sending ended while the app is in the background (stopped from the TV, the channel went down): nothing keeps the engine alive any more
                _cast.Idled += () => { if (_stopped && _runtime != null) System.Threading.Tasks.Task.Run(() => { try { _runtime.Services.Suspend(); } catch (Exception ex) { UdpLog.Warn("cast", "suspend: " + ex.Message); } }); };
                _cast.WarmUp();
            }
            catch (Exception ex) { UdpLog.Error("cast", "could not start 'Enviar a la tele': " + ex); _cast = null; }
        }

        private TvServices BuildServices(AppConfig config, PlatformHooks hooks, string data)
        {
            if (config.UiDemo)
            {
                UdpLog.Info("app", "interface DEMO MODE (sample data, no engine)");
                return DemoMode.Create(config);
            }
            _runtime = TvServicesFactory.Create(config, data, hooks, msg => UdpLog.Info("app", msg));
            return _runtime.Services;
        }

        /// <summary>The fonts and images PUi reads as files (UiContext takes both from FilesDir), copied from the apk's assets on first run and after each update.</summary>
        private void CopyAssets(string data)
        {
            string marker = Path.Combine(data, "assets.version");
            // changes with every install of the apk (its file's time), on every API level
            string version = AppInfo.Version + "/" + File.GetLastWriteTimeUtc(ApplicationInfo.SourceDir).Ticks;
            if (File.Exists(marker) && File.ReadAllText(marker) == version && Directory.Exists(Path.Combine(data, "fonts"))) return;
            foreach (var dir in new[] { "fonts", "images" })
            {
                Directory.CreateDirectory(Path.Combine(data, dir));
                foreach (var name in Assets.List(dir) ?? new string[0])
                    using (var src = Assets.Open(dir + "/" + name))
                    using (var dst = File.Create(Path.Combine(data, dir, name))) src.CopyTo(dst);
            }
            File.WriteAllText(marker, version);
        }

        /// <summary>Back on the root screen, or the exit of the app: ends the activity.</summary>
        private void RequestExit()
        {
            // a channel is being sent to another screen: Back on the first page leaves the app in the background instead of closing it (closing would end the sending)
            if (_cast != null && _cast.Busy) { RunOnUiThread(() => MoveTaskToBack(true)); return; }
            if (_host != null) _host.MarkExiting();
            RunOnUiThread(() => Finish());
        }

        // ================================================================ lifecycle
        protected override void OnStart()
        {
            base.OnStart();
            UdpLog.Info("app", "OnStart");
            _stopped = false;
            if (_view != null) _view.SetActive(true);
            if (_host != null) _host.Resume();
            if (_cast != null) _cast.WarmUp();                                                    // the televisions of this network are listed before the sheet opens
        }

        protected override void OnPause()
        {
            // Also happens with the assistant, the quick settings and a split screen, while the app stays visible: nothing is cut here
            UdpLog.Info("app", "OnPause");
            base.OnPause();
        }

        protected override void OnStop()
        {
            UdpLog.Info("app", "OnStop");
            _stopped = true;
            if (_host != null) _host.Pause();
            if (_view != null) _view.SetActive(false);
            base.OnStop();
        }

        protected override void OnDestroy()
        {
            UdpLog.Info("app", "OnDestroy (finishing=" + IsFinishing + ")");
            if (_castStopRequested != null) { CastSendingService.StopRequested -= _castStopRequested; _castStopRequested = null; }
            try { if (_cast != null) _cast.Dispose(); } catch (Exception ex) { UdpLog.Warn("cast", "dispose: " + ex.Message); }
            try { if (_host != null) _host.Shutdown(); } catch (Exception ex) { UdpLog.Warn("app", "shutdown: " + ex.Message); }
            try { if (_view != null) _view.ReleaseGpu(); } catch (Exception ex) { UdpLog.Warn("app", "releasing the GPU: " + ex.Message); }
            if (_hdmi != null) _hdmi.Stop();
            if (_platform != null) _platform.Detach();
            if (_noticeRaised != null) PlayerNotices.Raised -= _noticeRaised;
            if (_noticeCleared != null) PlayerNotices.Cleared -= _noticeCleared;
            base.OnDestroy();
            if (IsFinishing && _options.KillProcessOnExit)
            {
                // a clean start next time and the memory back at once (the process would otherwise stay cached with the engine's heap)
                UdpLog.Info("app", "process exit");
                Process.KillProcess(Process.MyPid());
            }
        }

        public override void OnTrimMemory(TrimMemory level)
        {
            base.OnTrimMemory(level);
            UdpLog.Info("app", "OnTrimMemory " + level);
            if ((int)level >= (int)TrimMemory.RunningLow) TrimCaches();
        }

        public override void OnLowMemory()
        {
            base.OnLowMemory();
            UdpLog.Warn("app", "OnLowMemory");
            TrimCaches();
        }

        private void TrimCaches()
        {
            try { var a = _host != null ? _host.App : null; if (a != null) a.TrimMemory(); if (_view != null) _view.RequestPurge(); }
            catch (Exception ex) { UdpLog.Warn("app", "trim: " + ex.Message); }
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus) ApplyBars(_immersive); else if (_platform != null) _platform.OnFocusLost();
        }

        // ================================================================ input
        public override bool DispatchKeyEvent(KeyEvent e)
        {
            if (_platform != null && _platform.OnKeyEvent(e)) return true;
            return base.DispatchKeyEvent(e);
        }

        public override bool DispatchTouchEvent(MotionEvent ev)
        {
            if (_platform != null && _platform.OnTouchEvent(ev)) return true;
            return base.DispatchTouchEvent(ev);
        }

        /// <summary>Safety net: Back normally arrives as a key event (above); if the system calls this instead, it is the same thing.</summary>
        public override void OnBackPressed()
        {
            if (_platform != null) _platform.OnBackPressed();
        }

        // ================================================================ orientation and system bars
        /// <summary>Locks the screen and hides or shows the system bars (the platform's IOrientationControl and the start of the app). Main thread.</summary>
        private void ApplyOrientation(ScreenLock screenLock, bool immersive)
        {
            LockScreen(screenLock);
            ApplyBars(immersive);
        }

        private void LockScreen(ScreenLock screenLock)
        {
            var wanted = screenLock == ScreenLock.Portrait ? Android.Content.PM.ScreenOrientation.Portrait
                : screenLock == ScreenLock.SensorLandscape ? Android.Content.PM.ScreenOrientation.SensorLandscape : Android.Content.PM.ScreenOrientation.Unspecified;
            try { if (RequestedOrientation != wanted) RequestedOrientation = wanted; }
            catch (Exception ex) { UdpLog.Warn("ui", "RequestedOrientation: " + ex.Message); }
        }

        /// <summary>
        /// Edge to edge always (the interface draws its background under the bars and keeps its canvas inside the safe area they leave: ScreenMetrics.SafeInsets). Immersive hides the bars (a TV, and
        /// the full screen player of a phone); otherwise they stay visible over the interface's background.
        /// </summary>
        private void ApplyBars(bool immersive)
        {
            _immersive = immersive;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    Window.SetDecorFitsSystemWindows(false);
                    var c = Window.InsetsController;
                    if (c != null)
                    {
                        if (immersive) { c.Hide(WindowInsets.Type.SystemBars()); c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe; }
                        else c.Show(WindowInsets.Type.SystemBars());
                    }
                }
                else
                {
                    var flags = SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
                    if (immersive) flags |= SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen;
#pragma warning disable CA1422
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(int)flags;
#pragma warning restore CA1422
                }
#pragma warning disable CA1422
                Window.SetStatusBarColor(Android.Graphics.Color.Transparent);
                Window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
#pragma warning restore CA1422
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) { Window.NavigationBarContrastEnforced = false; Window.StatusBarContrastEnforced = false; }
            }
            catch (Exception ex) { UdpLog.Warn("ui", "system bars: " + ex.Message); }
        }
    }
}
