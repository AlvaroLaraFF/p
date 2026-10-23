using System;
using System.IO;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using ElmSharp;
using PTizen.Platform;
using PUi.Demo;
using PUi.Host;
using Tizen.Applications;

namespace PTizen
{
    /// <summary>
    /// P! on the TV: opaque window with an Evas GL view where Skia draws the whole interface (PUi); the engine and the app logic are AceEngine. This class keeps only what is
    /// the TV's own: the Elementary window and the Evas view, the platform object, the player factory and the points where the TV needs its own work in the common
    /// lifecycle (the decoder warmer and the Evas probes: AppHostHooks). Startup, pause, resume, shutdown, the key script and the statistics are PUi.Host.AppHost.
    /// </summary>
    internal sealed class PApplication : CoreUIApplication
    {
        private Window _window;
        private SkiaView _view;
        private TizenPlatform _platform;
        private AppHost _host;
        private AppConfig _config = new AppConfig();
        private TvRuntime _runtime;
        /// <summary>What the TV plugs into the engine. The TV needs nothing of the optional parts (its network info, player tuning and defaults are the engine's own), only its player and its config defaults.</summary>
        private readonly PlatformHooks _platformHooks = new PlatformHooks { ConfigDefaults = AppConfigDefaults.Tizen };

        protected override void OnCreate()
        {
            base.OnCreate();
            UdpLog.Configure(false, "", 0);
            _config = TizenConfig.Load(DirectoryInfo.Data, DirectoryInfo.Resource, _platformHooks.ConfigDefaults);
            AppHost.StartLogging(_config);

            _window = new Window("P") { Alpha = false };
            // Elementary's focus highlight comes ENABLED by default on the TV's window (seen in the log: FocusHighlightEnabled=True): with the remote's arrows
            // Elementary draws and animates its own highlight over the window, which has no widgets of its own (the whole interface is an Evas image): possible cause of the flicker when moving the remote.
            try
            {
                bool before = _window.FocusHighlightEnabled;
                _window.FocusHighlightEnabled = false;
                try { _window.FocusHighlightAnimation = false; } catch (Exception) { }
                UdpLog.Info("app", "Elementary focus highlight: before=" + before + " now=" + _window.FocusHighlightEnabled);
            }
            catch (Exception ex) { UdpLog.Warn("app", "FocusHighlight: " + ex.Message); }
            _window.Show();
            UdpLog.Info("app", "window: " + _window.Geometry.Width + "x" + _window.Geometry.Height + " screen=" + _window.ScreenSize.Width + "x" + _window.ScreenSize.Height);
            _view = new SkiaView(_window);
            _view.Resize(1920, 1080); _view.Move(0, 0); _view.Show();
            _view.Init(1920, 1080, m => UdpLog.Info("gpu", m));
            _platform = new TizenPlatform(_window, _view, DirectoryInfo.Data, Path.Combine(DirectoryInfo.Resource, "fonts"), DirectoryInfo.Resource, () => { _host.MarkExiting(); Ui.Post(() => Exit()); });
            _host = new AppHost(_platform, _config, new AppHostHooks
            {
                // The decoder warmer is the TV's: it must be suspended BEFORE PApp.OnPause, resumed AFTER PApp.OnResume and suspended before the window it uses is destroyed (see AppHost).
                BeforePause = _platform.SuspendWarmer,
                AfterResume = _platform.ResumeWarmer,
                BeforeShutdown = _platform.SuspendWarmer,
                BeforeStart = app =>
                {
                    _view.SyncMode = _config.GlSync == "finish" ? 2 : _config.GlSync == "flush" ? 1 : 0;
                    UdpLog.Info("ui", "GPU sync: " + _config.GlSync + "; sprite budget: " + _config.SpriteBudgetMs + " ms per frame");
                },
                RenderCounters = () => _view.EvasCounters(),
                RenderLabel = "evas",
                WindowState = () => _platform.WindowState(),
                InjectKey = _platform.Raise,
                ScriptLabel = ScriptLabel,
            }, _platformHooks);
            _platform.HookKeys();
            // The TV source stops sounding from the moment the app opens, not only when a channel starts.
            _platform.StartWarmer(_config.Warmer);
            if (!_config.Warmer) UdpLog.Warn("app", "decoder warmer DISABLED by the configuration (only for measuring): the TV source will keep sounding in the background");

            _host.StartMonitoring();
            var services = BuildServices();
            _host.Start(services, _runtime);
        }

        private TvServices BuildServices()
        {
            if (_config.UiDemo)
            {
                var demo = DemoMode.Create(_config);
                UdpLog.Info("app", "interface DEMO MODE (sample data, no engine)");
                return demo;
            }
            _platformHooks.Players = new TizenPlayerFactory(_window, () => _platform != null && _platform.EnsureTransparent(), _platform);
            _runtime = TvServicesFactory.Create(_config, DirectoryInfo.Data, _platformHooks, msg => UdpLog.Info("app", msg));
            return _runtime.Services;
        }

        /// <summary>Probe points (1920x1080 coordinates): inside the first four rows of a list, background and menu.</summary>
        private static int[][] ProbePoints()
        {
            return new[] { new[] { 1000, 225 }, new[] { 1000, 331 }, new[] { 1000, 437 }, new[] { 1000, 543 }, new[] { 1500, 40 }, new[] { 300, 1040 }, new[] { 96, 195 }, new[] { 1000, 100 } };
        }

        /// <summary>The TV's own labels of the key script (AppConfig.KeyScript): Evas presentation probes and GPU sync mode (the "snap_*" captures are the runner's, through ISnapshotSource). Runs on the main thread.</summary>
        private bool ScriptLabel(string label)
        {
            bool handled = true;
            if (label == "probe_start") { _view.GridProbe = false; _view.ProbeStart(ProbePoints()); }
            else if (label == "probe_grid") { _view.GridProbe = true; _view.ProbeStart(ProbePoints()); }
            else if (label.StartsWith("probe_stop", StringComparison.Ordinal))
            {
                var lines = _view.ProbeStop();
                string tag = label;
                UdpLog.Info("probe", tag + ": frames=" + lines.Count + " " + _view.EvasCounters() + " " + _view.TimingSummary());
                Task.Run(async () => { int n = 0; foreach (var l in lines) { UdpLog.Info("probe", tag + " " + l); if (++n % 40 == 0) await Task.Delay(40); } });
            }
            else if (label == "sync_none") _view.SyncMode = 0;
            else if (label == "sync_flush") _view.SyncMode = 1;
            else if (label == "sync_finish") _view.SyncMode = 2;
            else handled = false;
            return handled;
        }

        protected override void OnPause()
        {
            UdpLog.Info("app", "OnPause");
            if (_host != null) _host.Pause();
            base.OnPause();
        }

        protected override void OnResume()
        {
            UdpLog.Info("app", "OnResume");
            base.OnResume();
            if (_host != null) _host.Resume();
        }

        protected override void OnTerminate()
        {
            UdpLog.Info("app", "OnTerminate");
            if (_host != null) _host.Shutdown();
            base.OnTerminate();
        }

        private static void Main(string[] args)
        {
            UdpLog.LocalSink = (level, tag, msg) => Tizen.Log.Info("P", level + " " + tag + ": " + msg);      // dlog; the UDP destination is set from the configuration in OnCreate
            AppDomain.CurrentDomain.UnhandledException += (s, e) => UdpLog.Error("app", "UNHANDLED EXCEPTION: " + e.ExceptionObject);
            Elementary.Initialize();
            Elementary.ThemeOverlay();
            new PApplication().Run(args);
        }
    }
}
