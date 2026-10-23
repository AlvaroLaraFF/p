using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using AceEngine.Util;
using PUi.App;
using PUi.Core;
using PUi.Demo;
using PUi.Host;
using PUi.Input;
using PUi.Screens;
using PUi.Testing;
using Xunit;

namespace PUi.Tests
{
    public class UiStatsTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private const string Core = @"ui\{fps=[\d.]+ ticks\{per_s=\d+ full=\d+ ambient=\d+ no_draw=\d+ frames_full=\d+ frames_ambient=\d+ logic_ms_s=[\d.]+ decor_speed=[\d.]+ rest=(yes|no) reason=\w*\} draw_avg=[\d.]+ms draw_max=[\d.]+ms \d+ sprites: surface \d+ ms, draw \d+ ms, flush \d+ ms, snapshot \d+ ms";

        [Fact]
        public void TheLineHasTheSameLayoutAsTheOneTheTvLogHasAlwaysHad()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            using (r.Render(1.0)) { }
            var st = new UiStats(() => r.App, () => "dirty=3 flush=2", () => "opaque/focus=yes") { RenderLabel = "evas" };
            string line = st.Sample();
            Assert.Matches("^" + Core + @" evas\{dirty=3 flush=2\} sprites=\d+/\d+MiB window=opaque/focus=yes .*\}$", line);
        }

        [Fact]
        public void EachSampleReportsOnlyWhatHappenedSinceThePreviousOne()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            var st = new UiStats(() => r.App);
            st.Sample();
            string quiet = st.Sample();                                    // nothing happened in between
            Assert.Matches(@"full=0 ambient=0 no_draw=0 frames_full=0 frames_ambient=0", quiet);
            r.Advance(r.Now + 0.5);                                        // 15 screen ticks
            string busy = st.Sample();
            var m = Regex.Match(busy, @"full=(\d+) ambient=(\d+) no_draw=(\d+)");
            int ticks = int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value);
            Assert.True(ticks >= 10, busy);
        }

        [Fact]
        public void WithoutPlatformPartsTheirSectionsAreLeftOut()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start();
            string line = new UiStats(() => r.App).Sample();
            Assert.Matches("^" + Core + @" sprites=\d+/\d+MiB .*\}$", line);
            Assert.DoesNotContain("window=", line); Assert.DoesNotContain("render{", line);
        }

        [Fact]
        public void BeforeTheAppExistsTheLineIsEmpty()
        {
            Assert.Equal("ui{}", new UiStats(() => null).Sample());
        }
    }

    public class KeyScriptRunnerTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private sealed class Run
        {
            public Scenes.Rig R; public KeyScriptRunner Runner;
            public readonly List<int> Delays = new List<int>();
            public readonly List<KeyPress> Keys = new List<KeyPress>();
            public readonly List<string> Labels = new List<string>();
            public bool Exiting;
        }

        private static Run Make()
        {
            var x = new Run { R = Scenes.NewRig(Fonts) };
            x.R.App.Start(); x.R.Advance(0.5);
            x.R.Platform.KeyPressed += k => x.Keys.Add(k);
            x.Runner = new KeyScriptRunner(x.R.App, x.R.Platform, k => x.R.Platform.Raise(k), () => x.Exiting, l => { x.Labels.Add(l); return true; });
            x.Runner.Delay = ms => { x.Delays.Add(ms); return Task.CompletedTask; };
            return x;
        }

        [Fact]
        public async Task PressesTheKeysThroughThePlatformWaitsAndTypesText()
        {
            var x = Make();
            await x.Runner.RunAsync("Down Return text:ab wait:700 XF86Back");
            Assert.Equal(new[] { UiKey.Down, UiKey.Ok, UiKey.Char, UiKey.Char, UiKey.Back }, x.Keys.Select(k => k.Key).ToArray());
            Assert.Equal(new[] { 'a', 'b' }, x.Keys.Where(k => k.Key == UiKey.Char).Select(k => k.Ch).ToArray());
            // first the start pause, then 300 ms after each pressed key and the wait of the script
            Assert.Equal(new[] { 2500, 300, 300, 300, 300, 700, 300 }, x.Delays.ToArray());
        }

        [Fact]
        public async Task AppPauseAndResumeCallThePAppLifecycleAndOtherLabelsGoToTheHook()
        {
            var x = Make();
            Assert.True(x.R.Platform.AudioHeld);
            await x.Runner.RunAsync("log:before log:app_pause log:paused log:app_resume log:snap_main log:probe_start");
            Assert.True(x.R.Platform.AudioHeld);                           // paused and resumed again
            // app_pause and app_resume are the runner's own; every other label (log:..., the platform's probes and captures) goes to the hook
            Assert.Equal(new[] { "before", "paused", "snap_main", "probe_start" }, x.Labels.ToArray());
        }

        [Fact]
        public async Task AppPauseReleasesTheAudioFocusUntilAppResume()
        {
            var x = Make();
            bool heldDuring = true;
            x.Runner = new KeyScriptRunner(x.R.App, x.R.Platform, k => x.R.Platform.Raise(k), () => x.Exiting, l => { heldDuring = x.R.Platform.AudioHeld; return true; });
            x.Runner.Delay = ms => Task.CompletedTask;
            await x.Runner.RunAsync("log:app_pause log:in_pause");
            Assert.False(heldDuring);
            Assert.False(x.R.Platform.AudioHeld);
            await x.Runner.RunAsync("log:app_resume");
            Assert.True(x.R.Platform.AudioHeld);
        }

        [Fact]
        public async Task StopsAsSoonAsTheAppIsExiting()
        {
            var x = Make();
            x.Runner.Delay = ms => { x.Delays.Add(ms); if (x.Delays.Count == 3) x.Exiting = true; return Task.CompletedTask; };
            await x.Runner.RunAsync("Down Down Down Down Down");
            Assert.True(x.Keys.Count < 5);
        }

        [Fact]
        public async Task UnknownTokensAreDroppedAndAnEmptyScriptDoesNothing()
        {
            var x = Make();
            await x.Runner.RunAsync("bogus XF86Nothing");
            await x.Runner.RunAsync("");
            Assert.Empty(x.Keys);
        }

        [Fact]
        public async Task AFailureOnTheInterfaceThreadReachesTheCaller()
        {
            var x = Make();
            x.Runner = new KeyScriptRunner(x.R.App, x.R.Platform, k => { throw new InvalidOperationException("boom"); }, () => false);
            x.Runner.Delay = ms => Task.CompletedTask;
            await Assert.ThrowsAsync<InvalidOperationException>(() => x.Runner.RunAsync("Down"));
        }
    }

    /// <summary>The lifecycle order AppHost guarantees, with the player open: these are the orders that fixed the TV regressions (the TV source sounding, the TV's Home menu).</summary>
    public class AppHostTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private sealed class Env
        {
            public DesktopPlatform Platform; public AppHost Host; public TvServices Services; public FakeCatalog Catalog; public FakeChecker Checker; public FakePlaybackFactory Playback;
            public readonly List<string> Events = new List<string>();
            public void Note(string e) { lock (Events) Events.Add(e); }
            public string[] Snapshot() { lock (Events) return Events.ToArray(); }
            public int At(string e) { var a = Snapshot(); int i = Array.IndexOf(a, e); Assert.True(i >= 0, "missing '" + e + "' in: " + string.Join(" | ", a)); return i; }
            public double T = 0;
            public void Tick(double to) { while (T < to) { T += 1.0 / 30; Host.App.NeedsFrame(T); } }
        }

        private sealed class Recorder : IDisposable
        {
            private readonly Env _e; public Recorder(Env e) { _e = e; }
            public void Dispose() { _e.Note("runtime disposed"); }
        }

        private static Env Make(AppConfig cfg = null, Action<AppHostHooks> tweak = null)
        {
            var e = new Env();
            e.Platform = new DesktopPlatform(Fonts);
            e.Platform.EventObserver = e.Note;
            FakeRecents rec;
            e.Services = FakeServices.Demo(out e.Catalog, out e.Checker, out rec, out e.Playback);
            e.Playback.Init = p => p.CallObserver = c => e.Note("player " + c);
            e.Services.Suspend = () => e.Note("services suspend");
            e.Services.Resume = () => e.Note("services resume");
            cfg = cfg ?? new AppConfig();
            cfg.DiagnosticsNetwork = false; cfg.StatsIntervalSeconds = 0;
            var hooks = new AppHostHooks
            {
                BeforePause = () => e.Note("hook before pause"),
                AfterResume = () => e.Note("hook after resume"),
                BeforeShutdown = () => e.Note("hook before shutdown"),
                BeforeStart = app => e.Note("hook before start"),
            };
            if (tweak != null) tweak(hooks);
            e.Host = new AppHost(e.Platform, cfg, hooks);
            return e;
        }

        private static Env Playing()
        {
            var e = Make();
            e.Host.Start(e.Services, new Recorder(e));
            e.Tick(0.5);
            e.Host.App.Push(new PlayerScreen(new PlayContext(e.Catalog.Categories.First().Channels, 0)));
            e.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 5; });
            e.Tick(1.5);
            Assert.False(e.Platform.Opaque);                               // video running: transparent window
            e.Events.Clear();
            return e;
        }

        [Fact]
        public void StartBuildsTheAppFromTheConfigurationAndStartsItAfterTheHook()
        {
            var cfg = new AppConfig { SpriteBudgetMs = 5, GpuWarmup = true };
            var e = Make(cfg);
            Assert.Null(e.Host.App);
            e.Host.Start(e.Services, null);
            Assert.NotNull(e.Host.App);
            Assert.Equal(5, e.Host.App.Context.Sprites.FrameBudgetMs);
            Assert.True(e.Host.App.GpuWarmupEnabled);
            Assert.Equal("hook before start", e.Snapshot()[0]);
            Assert.True(e.Platform.AudioHeld);                             // PApp.Start asked for the audio focus
            Assert.StartsWith("TV", e.Host.App.Describe());
        }

        [Fact]
        public void PauseRunsTheHookBeforeThePAppPauseAndThePlayerIsReleasedOnlyAfterTheWindowIsOpaque()
        {
            var e = Playing();
            e.Host.Pause();
            Assert.True(e.Platform.Opaque);
            int hook = e.At("hook before pause"), opaque = e.At("window opaque"), stop = e.At("player stop"), audio = e.At("audio release"), susp = e.At("services suspend");
            Assert.True(hook < opaque, "the warmer must be suspended before PApp.OnPause touches the window and the player");
            Assert.True(opaque < stop, "the window must be opaque BEFORE the video is released");
            Assert.True(stop < audio && audio < susp);
            Assert.Equal(0, hook);
        }

        [Fact]
        public void ResumeRunsThePAppResumeBeforeTheHook()
        {
            var e = Playing();
            e.Host.Pause();
            e.Events.Clear();
            e.Host.Resume();
            Assert.True(e.Platform.AudioHeld);
            int audio = e.At("audio acquire"), resume = e.At("services resume"), hook = e.At("hook after resume");
            Assert.True(audio < hook && resume < hook, "the warmer must come back only after the app is back");
            Assert.Equal(e.Snapshot().Length - 1, hook);
            Assert.IsType<PlayerScreen>(e.Host.App.Current);                // short pause: the same channel is reopened
        }

        [Fact]
        public void PauseAndResumeBeforeStartOnlyRunTheHooks()
        {
            var e = Make();
            e.Host.Pause(); e.Host.Resume();
            Assert.Equal(new[] { "hook before pause", "hook after resume" }, e.Snapshot());
        }

        [Fact]
        public void ShutdownSuspendsTheHookFirstThenStopsTheAppAndReleasesTheRuntime()
        {
            var e = Playing();
            Assert.False(e.Host.Exiting);
            e.Host.Shutdown();
            Assert.True(e.Host.Exiting);
            int hook = e.At("hook before shutdown"), opaque = e.At("window opaque"), stop = e.At("player stop"), rt = e.At("runtime disposed");
            Assert.Equal(0, hook);
            Assert.True(hook < opaque && opaque < stop && stop < rt);
        }

        [Fact]
        public void MarkExitingStopsBackgroundWork()
        {
            var e = Make();
            Assert.False(e.Host.Exiting);
            e.Host.MarkExiting();
            Assert.True(e.Host.Exiting);
        }

        [Fact]
        public void TheKeyScriptFromTheConfigurationRunsInTheBackgroundThroughTheInjectedPath()
        {
            var pressed = new System.Collections.Concurrent.ConcurrentQueue<UiKey>();
            DesktopPlatform platform = null;
            var e = Make(new AppConfig { KeyScript = "Down wait:10 Up" }, h => h.InjectKey = k => { pressed.Enqueue(k.Key); platform.Raise(k); });
            platform = e.Platform;
            e.Host.KeyScriptStartDelayMs = 0;
            e.Host.Start(e.Services, null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pressed.Count < 2 && sw.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(10);
            Assert.Equal(new[] { UiKey.Down, UiKey.Up }, pressed.ToArray());
        }

        [Fact]
        public void UiStatsLineIsTheAppsOnceStarted()
        {
            var e = Make();
            Assert.Equal("ui{}", e.Host.UiStatsLine());
            e.Host.Start(e.Services, null);
            Assert.StartsWith("ui{fps=", e.Host.UiStatsLine());
        }

        [Fact]
        public void StartLoggingSetsTheEngineLogFromTheConfiguration()
        {
            var sink = AceLog.Sink; var level = AceLog.MinLevel;
            try
            {
                AppHost.StartLogging(new AppConfig { LogLevel = "debug" });
                Assert.Equal(LogLevel.Debug, AceLog.MinLevel);
                AppHost.StartLogging(new AppConfig { LogLevel = "warn" });
                Assert.Equal(LogLevel.Warn, AceLog.MinLevel);
                AppHost.StartLogging(new AppConfig());
                Assert.Equal(LogLevel.Info, AceLog.MinLevel);
                Assert.NotNull(AceLog.Sink);
            }
            finally { AceLog.Sink = sink; AceLog.MinLevel = level; UdpLog.Configure(false, "", 0); }
        }
    }
}
