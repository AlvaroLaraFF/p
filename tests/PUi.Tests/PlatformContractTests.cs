using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AceEngine.App;
using PUi.Core;
using PUi.Host;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The pieces of the platform contract that exist for the shells to come (Android TV, Fire TV, phones): on the Samsung TV they do nothing, here they are exercised through the desktop platform.</summary>
    public class PlatformContractTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        // ------------------------------------------------------------------ interface thread

        [Fact]
        public void IsUiThreadIsTrueOnlyOnTheInterfaceThread()
        {
            var p = new DesktopPlatform(Fonts);
            Assert.True(p.UiThread.IsUiThread);
            bool other = true;
            var t = new System.Threading.Thread(() => other = p.UiThread.IsUiThread);          // a real other thread (Task.Wait can run the task inline)
            t.Start(); t.Join();
            Assert.False(other);
        }

        [Fact]
        public void PostedWorkRunsWhenTheTestSaysSoAndAlwaysThroughPost()
        {
            var p = new DesktopPlatform(Fonts) { ImmediatePost = false };
            int ran = 0;
            p.UiThread.Post(() => ran++);
            Assert.Equal(0, ran);
            while (p.Posted.Count > 0) p.Posted.Dequeue()();
            Assert.Equal(1, ran);
        }

        // ------------------------------------------------------------------ audio focus

        [Fact]
        public void AudioFocusChangesAreReportedAndTheInterfaceDoesNotReactToThemYet()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            var seen = new List<AudioFocusChange>();
            r.Platform.Audio.Changed += seen.Add;
            string before = r.App.Describe();
            foreach (var c in new[] { AudioFocusChange.LostTransient, AudioFocusChange.Duck, AudioFocusChange.Gained, AudioFocusChange.Lost })
                r.Platform.RaiseAudioFocus(c);
            Assert.Equal(new[] { AudioFocusChange.LostTransient, AudioFocusChange.Duck, AudioFocusChange.Gained, AudioFocusChange.Lost }, seen.ToArray());
            Assert.Equal(before, r.App.Describe());
            Assert.True(r.Platform.AudioHeld);
        }

        // ------------------------------------------------------------------ capabilities

        [Fact]
        public void TheDefaultCapabilitiesAreTheSamsungRemote()
        {
            var c = new DesktopPlatform(Fonts).Capabilities;
            Assert.True(c.HasChannelKeys); Assert.True(c.HasDigitKeys); Assert.True(c.HasColorKeys); Assert.True(c.HasRemote);
            Assert.False(c.HasTouch); Assert.False(c.HasKeyboard);
        }

        [Fact]
        public void ADevicesCapabilitiesCanBeDescribed()
        {
            var fireTv = new PlatformCapabilities(hasChannelKeys: false, hasDigitKeys: false, hasColorKeys: false);
            Assert.False(fireTv.HasChannelKeys); Assert.False(fireTv.HasDigitKeys); Assert.False(fireTv.HasColorKeys); Assert.True(fireTv.HasRemote);
            var phone = new PlatformCapabilities(false, false, false, hasRemote: false, hasTouch: true);
            Assert.True(phone.HasTouch); Assert.False(phone.HasRemote); Assert.False(phone.HasKeyboard);
            var p = new DesktopPlatform(Fonts) { Capabilities = phone };
            Assert.Same(phone, ((IPlatform)p).Capabilities);
        }

        // ------------------------------------------------------------------ keep the screen on, only in the player

        [Fact]
        public void TheScreenIsKeptOnOnlyWhileThePlayerIsOpen()
        {
            var r = Scenes.NewRig(Fonts);
            var events = new List<string>();
            r.Platform.EventObserver = events.Add;
            r.App.Start(); r.Advance(0.5);
            Assert.False(r.Platform.KeepScreenOn);
            Assert.DoesNotContain("keep screen on yes", events);                              // menus may let the screen sleep
            r.App.Push(new PlayerScreen(new PlayContext(r.Catalog.Categories.First().Channels, 0)));
            Assert.True(r.Platform.KeepScreenOn);
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 0.6);
            r.App.OnPause();                                                                   // Home: the player is closed
            Assert.False(r.Platform.KeepScreenOn);
            r.App.OnResume();                                                                  // a short pause: the same channel is reopened
            Assert.IsType<PlayerScreen>(r.App.Current);
            Assert.True(r.Platform.KeepScreenOn);
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.4);                           // Back leaves the player
            Assert.False(r.Platform.KeepScreenOn);
        }

        [Fact]
        public void ChangingChannelKeepsTheScreenOn()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            r.App.Push(new PlayerScreen(new PlayContext(r.Catalog.Categories.First().Channels, 0)));
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 0.6);
            r.Platform.PressKey(UiKey.ChannelUp); r.Advance(r.Now + 0.4);
            Assert.True(r.Platform.KeepScreenOn);
        }

        // ------------------------------------------------------------------ trim memory

        [Theory]
        [InlineData("main")]
        [InlineData("main-loading")]
        [InlineData("settings")]
        public void TrimMemoryDropsTheRebuildableCachesAndTheNextFramesRebuildThem(string scene)
        {
            var r = Scenes.Build(scene, Fonts);
            using (r.Render(2.0)) { }
            var sprites = r.App.Context.Sprites;
            int count = sprites.Count;
            Assert.True(count > 0, "the scene drew nothing worth caching");
            int redraws = r.Platform.Redraws;
            r.App.TrimMemory();
            Assert.Equal(0, sprites.Count);
            Assert.Equal(0, sprites.Bytes);
            Assert.True(r.Platform.Redraws > redraws, "a frame has to be requested to rebuild what is on screen");
            using (var bmp = r.Render(2.1)) Assert.Equal(255, bmp.GetPixel(900, 500).Alpha);   // it still draws a full frame
            Assert.True(sprites.Count > 0);                                                    // and caches what it draws again
            r.App.TrimMemory(); r.App.TrimMemory();                                            // repeated trims are harmless
            using (r.Render(2.2)) { }
        }

        [Fact]
        public void TrimMemoryAfterDisposeDoesNothing()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start();
            r.App.Dispose();
            r.App.TrimMemory();
        }

        [Fact]
        public void TrimMemoryKeepsTheScreenStackAndTheFocus()
        {
            var r = Scenes.Build("category", Fonts);
            using (r.Render(2.0)) { }
            string before = r.App.Describe(); int depth = r.App.Depth;
            r.App.TrimMemory();
            Assert.Equal(before, r.App.Describe()); Assert.Equal(depth, r.App.Depth);
        }

        // ------------------------------------------------------------------ screen captures

        private sealed class FakeSnapshot : ISnapshotSource
        {
            public Action Pending; public byte[] Png = new byte[] { 1, 2, 3 };
            public void AfterNextFrame(Action action) { Pending = action; }
            public byte[] SnapshotPng() { return Png; }
        }

        [Fact]
        public async Task SnapLabelsCaptureAfterTheNextFrameAndHandTheNamedPngToTheSink()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            var snap = new FakeSnapshot();
            var got = new System.Collections.Concurrent.ConcurrentQueue<Tuple<string, byte[]>>();
            var runner = new KeyScriptRunner(r.App, r.Platform, k => r.Platform.Raise(k), () => false, null, snap, (name, png) => got.Enqueue(Tuple.Create(name, png)));
            runner.Delay = ms => Task.CompletedTask;
            await runner.RunAsync("log:snap_main");
            Assert.NotNull(snap.Pending);                                                      // asked for the next frame, nothing captured yet
            Assert.Empty(got);
            snap.Pending();                                                                    // the frame was drawn
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (got.IsEmpty && sw.ElapsedMilliseconds < 10000) System.Threading.Thread.Sleep(5);
            Tuple<string, byte[]> t;
            Assert.True(got.TryDequeue(out t));
            Assert.Equal("snap_main.png", t.Item1);
            Assert.Equal(snap.Png, t.Item2);
        }

        [Fact]
        public async Task AFrameThatCannotBeReadSendsNothing()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start();
            var snap = new FakeSnapshot { Png = null };
            int sunk = 0;
            var runner = new KeyScriptRunner(r.App, r.Platform, k => { }, () => false, null, snap, (n, p) => sunk++);
            runner.Delay = ms => Task.CompletedTask;
            await runner.RunAsync("log:snap_x");
            snap.Pending();
            System.Threading.Thread.Sleep(50);
            Assert.Equal(0, sunk);
        }

        [Fact]
        public async Task WithoutASnapshotSourceSnapLabelsGoToThePlatformHookLikeAnyOther()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start();
            var labels = new List<string>();
            var runner = new KeyScriptRunner(r.App, r.Platform, k => { }, () => false, l => { labels.Add(l); return true; });
            runner.Delay = ms => Task.CompletedTask;
            await runner.RunAsync("log:snap_a");
            Assert.Equal(new[] { "snap_a" }, labels.ToArray());
            Assert.Null(((IPlatform)r.Platform).Snapshot);
        }

        // ------------------------------------------------------------------ touch (the contract; what the interface does with it is in TouchTests)

        [Fact]
        public void PointerEventsReachTheInputSourceInLogicalCoordinates()
        {
            var p = new DesktopPlatform(Fonts);
            var seen = new List<PointerEvent>();
            p.Input.Pointer += seen.Add;
            p.Touch(PointerAction.Down, 960, 540, id: 0, time: 1.5);
            p.Touch(PointerAction.Move, 970, 500, id: 0, time: 1.55);
            p.Touch(PointerAction.Up, 970, 500, id: 0, time: 1.6);
            p.Touch(PointerAction.Cancel, 0, 0, id: 1);
            Assert.Equal(new[] { PointerAction.Down, PointerAction.Move, PointerAction.Up, PointerAction.Cancel }, seen.Select(e => e.Action).ToArray());
            Assert.Equal(960, seen[0].X); Assert.Equal(540, seen[0].Y); Assert.Equal(1.5, seen[0].Time); Assert.Equal(1, seen[3].Id);
        }

        [Fact]
        public void ATvWithoutTouchIgnoresPointerEventsAndNothingBreaks()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            string before = r.App.Describe();
            r.Platform.Touch(PointerAction.Down, 400, 300);
            r.Platform.Touch(PointerAction.Move, 410, 300);
            r.Platform.Touch(PointerAction.Up, 410, 300);
            r.Advance(r.Now + 0.3);
            Assert.Equal(before, r.App.Describe());
        }

        [Fact]
        public void TheSystemBackGestureArrivesAsTheBackKeyAndGoesBack()
        {
            var r = Scenes.Build("category", Fonts);
            Assert.IsType<CategoryScreen>(r.App.Current);
            r.Platform.Raise(new KeyPress(UiKey.Back, "system back gesture"));              // what a touch platform raises for its Back gesture
            r.Advance(r.Now + 0.4);
            Assert.IsType<MainScreen>(r.App.Current);
        }

        // ------------------------------------------------------------------ screen metrics

        [Fact]
        public void ATvIs1920x1080LandscapeWithNothingCovered()
        {
            var m = ScreenMetrics.Tv;
            Assert.Equal(1920, m.PixelWidth); Assert.Equal(1080, m.PixelHeight);
            Assert.Equal(ScreenOrientation.Landscape, m.Orientation);
            Assert.True(m.IsDesignAspect);
            Assert.Equal(16f / 9f, m.AspectRatio, 3);
            Assert.Equal(1f, m.Scale); Assert.Equal(1080f, m.LogicalHeight); Assert.True(m.SafeInsets.IsEmpty);
            Assert.Equal(m, new DesktopPlatform(Fonts).Render.Screen);
        }

        [Fact]
        public void APhoneInPortraitIsTallerThanWideAndItsInsetsAndPointsConvertToLogicalUnits()
        {
            var m = new ScreenMetrics(1080, 2340, new ScreenInsets(0, 117, 0, 63), density: 2.75f);
            Assert.Equal(ScreenOrientation.Portrait, m.Orientation);
            Assert.False(m.IsDesignAspect);
            Assert.Equal(0.5625f, m.Scale, 4);                                               // 1080 / 1920
            Assert.Equal(1920f, m.LogicalWidth);
            Assert.Equal(4160f, m.LogicalHeight, 1);                                         // 2340 / 0.5625
            // the 1920x1080 canvas (1080x607.5 px here) is centered in the safe area, and logical coordinates are measured from its top-left corner
            Assert.Equal(960f, m.ToLogicalX(540), 3);
            Assert.Equal(866f, m.CanvasTop); Assert.Equal(0f, m.CanvasLeft);
            Assert.Equal(540f, m.ToLogicalY(m.CanvasTop + 303.75f), 2);
            var li = m.SafeInsetsLogical;
            Assert.Equal(208, li.Top); Assert.Equal(112, li.Bottom); Assert.Equal(0, li.Left);
            Assert.Equal(2.75f, m.Density);
        }

        [Fact]
        public void ATabletOrAWindowWithAnotherShapeIsNotTheDesignAspect()
        {
            Assert.False(new ScreenMetrics(2000, 1200).IsDesignAspect);                       // 16:10
            Assert.True(new ScreenMetrics(1600, 900).IsDesignAspect);
            Assert.Equal(ScreenOrientation.Landscape, new ScreenMetrics(2000, 1200).Orientation);
            Assert.Equal(1200f * 1920 / 2000, new ScreenMetrics(2000, 1200).LogicalHeight, 1);
        }

        [Fact]
        public void AnEmptySurfaceDoesNotDivideByZero()
        {
            var m = new ScreenMetrics(0, 0);
            Assert.Equal(0f, m.AspectRatio); Assert.Equal(0f, m.LogicalHeight); Assert.Equal(0f, m.ToLogicalX(10)); Assert.True(m.SafeInsetsLogical.IsEmpty);
        }

        [Fact]
        public void RotatingTheSurfaceRaisesScreenChangedWithTheNewMetrics()
        {
            var p = new DesktopPlatform(Fonts);
            int raised = 0;
            p.Render.ScreenChanged += () => raised++;
            Assert.Equal(ScreenOrientation.Landscape, p.Render.Screen.Orientation);
            p.ChangeScreen(1080, 2340, new ScreenInsets(0, 100, 0, 50));
            Assert.Equal(1, raised);
            Assert.Equal(ScreenOrientation.Portrait, p.Render.Screen.Orientation);
            Assert.Equal(100, p.Render.Screen.SafeInsets.Top);
            Assert.Equal(1080, p.Render.PixelWidth);
        }
    }
}
