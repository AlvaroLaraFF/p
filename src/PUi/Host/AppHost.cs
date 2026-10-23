using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Util;
using PUi.App;
using PUi.Platform;
using PUi.Screens;

namespace PUi.Host
{
    /// <summary>
    /// Startup, pause, resume and shutdown of the app, common to every shell (the Tizen application, the Android activity): configures the log, builds the PApp from
    /// the configuration, runs the key script and the periodic statistics, and drives the lifecycle IN THE ORDER the platforms need. The shell keeps what is its own: the
    /// window, the view, the platform object, the services' player factory and the AppHostHooks.
    ///
    /// Lifecycle order (from regressions seen on the TV; do not change it without a test on the device):
    ///  - Pause: BeforePause hook, then PApp.OnPause. The TV's decoder warmer has to be suspended BEFORE PApp.OnPause: that call closes the player, and on release the player
    ///    would ask for the warmer again with the app already hidden (the TV source then sounds, or the TV's Home menu stops opening).
    ///    Inside PApp.OnPause the window goes opaque BEFORE the video is released.
    ///  - Resume: PApp.OnResume, then AfterResume hook (the warmer comes back after the app, never before).
    ///  - Shutdown: BeforeShutdown hook while the window still exists, then PApp.OnPause, render loop stop and dispose, then the services and the monitor.
    /// </summary>
    public sealed class AppHost
    {
        /// <summary>Application id, the same on Tizen and Android.</summary>
        public const string AppId = "org.p.app";

        private readonly IPlatform _platform;
        private readonly AppHostHooks _hooks;
        private readonly PlatformHooks _platformHooks;
        private readonly UiStats _stats;
        private PApp _app;
        private IDisposable _runtime;
        private ProcMonitor _monitor;
        private volatile bool _exiting;

        /// <param name="platformHooks">What the platform plugs into the engine (here only its network info, for the network diagnostics); null = none.</param>
        public AppHost(IPlatform platform, AppConfig config, AppHostHooks hooks = null, PlatformHooks platformHooks = null)
        {
            _platform = platform; Config = config ?? new AppConfig(); _hooks = hooks ?? new AppHostHooks(); _platformHooks = platformHooks;
            _stats = new UiStats(() => _app, _hooks.RenderCounters, _hooks.WindowState) { RenderLabel = _hooks.RenderLabel };
        }

        public AppConfig Config { get; private set; }
        /// <summary>Pause before the first step of the key script (the interface has to have drawn its first screen). Set before Start; the tests set it to 0.</summary>
        public int KeyScriptStartDelayMs = 2500;
        /// <summary>The interface; null until Start.</summary>
        public PApp App { get { return _app; } }
        /// <summary>The app is closing (the platform's exit was requested, or Shutdown ran): background work such as the key script stops.</summary>
        public bool Exiting { get { return _exiting; } }
        public void MarkExiting() { _exiting = true; }

        /// <summary>The "ui{...}" statistics line.</summary>
        public string UiStatsLine() { return _stats.Sample(); }

        /// <summary>
        /// First thing a shell does once it has the configuration: sets the UDP log destination, the engine's log level and sink, and writes the startup lines. (The platform's own
        /// log, UdpLog.LocalSink, has to be set before.)
        /// </summary>
        public static void StartLogging(AppConfig cfg)
        {
            UdpLog.Configure(cfg.LogEnabled, cfg.LogHost, cfg.LogPort);
            AceLog.MinLevel = cfg.LogLevel == "debug" ? LogLevel.Debug : cfg.LogLevel == "warn" ? LogLevel.Warn : LogLevel.Info;
            AceLog.Sink = UdpLog.Engine;
            UdpLog.Info("app", "==== P! starting (" + AppId + " " + AppInfo.Version + ") ====");
            UdpLog.Info("app", "config: " + cfg.ToJson().Replace("\n", " "));
            UdpLog.Info("app", StartupDiagnostics.RuntimeLine());
        }

        /// <summary>Logs the memory state and starts the periodic CPU, memory and interface statistics ([stats] lines) if the configuration asks for them.</summary>
        public void StartMonitoring()
        {
            _monitor = new ProcMonitor(_stats.Sample);
            StartupDiagnostics.LogMemory();
            _monitor.Start(Config.StatsIntervalSeconds);
        }

        /// <summary>
        /// Builds the interface on the services the shell prepared, applies the configuration to it, starts it and, if configured, the key script and the network diagnostics.
        /// </summary>
        /// <param name="runtime">What has to be released on exit (the real services' TvRuntime); null for the demo mode.</param>
        public void Start(TvServices services, IDisposable runtime)
        {
            _runtime = runtime;
            var app = new PApp(services, _platform);
            _app = app;
            app.Context.Sprites.FrameBudgetMs = Config.SpriteBudgetMs;           // on the TV creating a sprite costs ~12-60 ms: at most ~1-2 per frame (never 20 at once)
            app.GpuWarmupEnabled = Config.GpuWarmup;
            if (_hooks.BeforeStart != null) _hooks.BeforeStart(app);
            app.Start();
            UdpLog.Info("app", "interface ready; screen: " + app.Describe());
            if (!string.IsNullOrEmpty(Config.KeyScript))
            {
                Action<PUi.Input.KeyPress> press = key => { if (_hooks.InjectKey != null) _hooks.InjectKey(key); else app.OnKey(key); };
                var runner = new KeyScriptRunner(app, _platform.UiThread, press, () => _exiting, _hooks.ScriptLabel, _platform.Snapshot, (name, png) => SnapUploader.PutAsync(Config.LogHost, name, png)) { StartDelayMs = KeyScriptStartDelayMs };
                Task.Run(() => runner.RunAsync(Config.KeyScript));
            }
            if (Config.DiagnosticsNetwork && !Config.UiDemo)
                Task.Run(async () =>
                {
                    // network diagnostics: multicast, IGD, NAT-PMP and incoming connections (local network only; the lines go out through the UDP log)
                    await Task.Delay(8000);
                    try
                    {
                        var net = _platformHooks != null ? _platformHooks.Network : null;
                        if (net == null) await AceEngine.Net.UpnpDiagnostics.RunAsync(line => { }, CancellationToken.None);
                        else await AceEngine.Net.UpnpDiagnostics.RunAsync(line => { }, CancellationToken.None, new AceEngine.Net.DiagnosticsOptions { Network = net });
                    }
                    catch (Exception ex) { UdpLog.Warn("upnp", "diagnostics: " + ex.Message); }
                });
        }

        /// <summary>The platform asks to pause (Home, source change). See the class remarks for the order.</summary>
        public void Pause()
        {
            if (_hooks.BeforePause != null) _hooks.BeforePause();
            if (_app != null) _app.OnPause();
            // In the background no memory is held: after stopping the engine, a full collection frees the live-stream buffers (without this the heap stays at ~57 MiB while the app is on Home)
            Task.Run(async () => { await Task.Delay(1500).ConfigureAwait(false); GC.Collect(2, GCCollectionMode.Forced, true); GC.WaitForPendingFinalizers(); GC.Collect(); UdpLog.Info("app", "memory freed after the pause: " + (GC.GetTotalMemory(false) >> 20) + " MiB managed"); });
        }

        /// <summary>The platform is visible again. See the class remarks for the order.</summary>
        public void Resume()
        {
            if (_app != null) _app.OnResume();
            if (_hooks.AfterResume != null) _hooks.AfterResume();
        }

        /// <summary>The platform closes the app: stops the interface and the render loop, then the services and the monitor.</summary>
        public void Shutdown()
        {
            _exiting = true;
            if (_hooks.BeforeShutdown != null) _hooks.BeforeShutdown();
            try { if (_app != null) { _app.OnPause(); _platform.Render.Stop(); _app.Dispose(); } } catch (Exception ex) { UdpLog.Warn("app", "shutdown: " + ex.Message); }
            if (_runtime != null) _runtime.Dispose();
            if (_monitor != null) { UdpLog.Info("diag", "monitor: " + _monitor.Summary()); _monitor.Dispose(); }
        }
    }
}
