using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AceEngine.App;
using AceEngine.App.Tv;
using PUi.App;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>
    /// The player of a phone and of a tablet: the page in portrait (the video is a 16:9 area at the top, the controls are always there), the controls over the video in landscape, full screen
    /// by turning the screen, the video rectangle and the orientation requests the platform receives.
    /// </summary>
    public class CompactPlayerTests
    {
        private static PlayerScreen Screen(Scenes.Rig r) { return (PlayerScreen)r.App.Current; }

        // ------------------------------------------------------------------ the page (portrait)

        [Fact]
        public void OnAPhoneInPortraitTheVideoIsA16By9AreaAtTheTopAndThePlatformIsToldWhere()
        {
            var r = Player(null, Device.Phone);
            var p = Screen(r);
            Assert.True(p.PageLayout);
            var m = r.Platform.Screen;
            var rect = r.Platform.VideoRect;
            Assert.True(rect.HasValue);
            var expect = m.ToPixels(0, 0, 390, r.App.Context.Style.VideoHeight);
            Assert.Equal(expect, rect.Value);
            Assert.Equal(expect, p.VideoPixels.Value);
            Assert.Equal(0, rect.Value.Left); Assert.Equal(1080, rect.Value.Right);
            Assert.Equal(118, rect.Value.Top);                                               // right under the status bar
            Assert.InRange((double)rect.Value.Height / rect.Value.Width, 0.559, 0.566);       // 9:16
            Assert.True(rect.Value.Bottom < m.PixelHeight / 2);
        }

        [Fact]
        public void TheVideoRectangleIsClearedWhenThePlayerCloses()
        {
            var r = Player(null, Device.Phone);
            Assert.True(r.Platform.VideoRect.HasValue);
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.4);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.False(r.Platform.VideoRect.HasValue);
        }

        [Fact]
        public void TheVideoRectangleIsOnlyTouchedWhenItChanges()
        {
            var r = Player(null, Device.Phone);
            int calls = r.Platform.VideoRectCalls;
            r.Advance(r.Now + 2.0);
            Assert.Equal(calls, r.Platform.VideoRectCalls);
        }

        [Fact]
        public void ThePageHasTheInformationTheBarTheControlsTheActionsAndMoreChannels()
        {
            var r = Player(null, Device.Phone);
            foreach (var id in new[] { "back", "timeline", "back10", "play", "forward10", "live", "fullscreen", "more0", "more1", "more2" })
                Assert.True(Has(r, id), id);
            Assert.False(Has(r, "cast"));                                                    // no service: no button, and the full screen button takes the whole row
            var full = Target(r, "fullscreen");
            Assert.Equal(350f, full.Rect.Width, 1); Assert.Equal(52f, full.Rect.Height, 1);
            Assert.Equal(56f, Target(r, "back10").Rect.Width, 1); Assert.Equal(76f, Target(r, "play").Rect.Width, 1);
            var more = Target(r, "more0");
            Assert.Equal(358f, more.Rect.Width, 1); Assert.Equal(56f, more.Rect.Height, 1);
            var vh = r.App.Context.Style.VideoHeight;
            Assert.True(Target(r, "play").Rect.Top > vh);                                    // everything is below the video
        }

        [Fact]
        public void ThePageButtonsPauseSeekAndGoLiveAndTheBarSeeksWhereItIsTapped()
        {
            var r = Player(null, Device.Phone);
            TapOn(r, "play");
            Assert.Contains("toggle", Calls(r));
            Assert.Contains("mode=Paused", r.App.Describe());
            TapOn(r, "play");
            TapOn(r, "back10"); Assert.Contains("seeklive -10", Calls(r));
            TapOn(r, "forward10"); Assert.Contains("seeklive 10", Calls(r));
            TapOn(r, "live"); Assert.Contains("golive", Calls(r));
            var tl = Target(r, "timeline");
            float x0 = tl.Rect.Left + 14, x1 = tl.Rect.Right - 14, y = CenterY(tl);
            r.Tap(x0 + (x1 - x0) * 0.25f, y);
            int secs = int.Parse(Regex.Match(Calls(r).Last(c => c.StartsWith("seeklive") && !c.EndsWith(" 10") && !c.EndsWith(" -10")), @"-?\d+").Value);
            Assert.InRange(secs, -135, -131);
        }

        [Fact]
        public void ATapOnThePictureOfThePageDoesNothingAndThePanelIsAlwaysThere()
        {
            var r = Player(null, Device.Phone);
            Assert.Contains("panel=yes", r.App.Describe());
            r.Tap(195, 100, 0.08, 0.6);
            Assert.Contains("panel=yes", r.App.Describe());
            Assert.DoesNotContain(Calls(r), c => c.StartsWith("toggle") || c.StartsWith("seek"));
            r.Advance(r.Now + 8);                                                            // it does not hide after five seconds either
            Assert.Contains("panel=yes", r.App.Describe());
            Assert.True(Has(r, "play"));
        }

        [Fact]
        public void TheMoreChannelsOfTheListOpenOnATap()
        {
            var r = Player(null, Device.Phone);
            var pc = Screen(r).Context;
            Assert.Equal(0, pc.Index);
            TapOn(r, "more1");
            Assert.Equal(2, pc.Index);                                                       // the second of the channels after this one
            Assert.Equal(2, r.Playback.Created.Count);                                       // a new playback
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void TheArrowLeavesThePlayerFromThePageAndFromTheLoadingScreenAndTheErrorScreen()
        {
            var r = Player(null, Device.Phone);
            TapOn(r, "back"); Assert.IsType<MainScreen>(r.App.Current);

            var l = TouchRig.Phone(null, Device.Phone);
            l.App.Push(new PlayerScreen(new PlayContext(l.Catalog.Categories.First().Channels, 0))); l.Advance(l.Now + 0.3);
            Assert.Contains("mode=Loading", l.App.Describe());
            TapOn(l, "back"); Assert.IsType<MainScreen>(l.App.Current);

            var e = TouchRig.Phone(null, Device.Phone);
            e.App.Push(new PlayerScreen(new PlayContext(e.Catalog.Categories.First().Channels, 0))); e.Advance(e.Now + 0.3);
            e.Playback.Created.Last().Push(s => { s.Phase = PlayPhase.Error; s.Error = "Sin fuentes por ahora"; });
            e.Advance(e.Now + 0.4);
            Assert.Contains("mode=Error", e.App.Describe());
            e.Tap(195, 400, 0.08, 0.4);                                                      // a tap on the card retries, like OK
            Assert.Equal(2, e.Playback.Created.Count);
            e.Playback.Created.Last().Push(s => { s.Phase = PlayPhase.Error; s.Error = "Sin fuentes por ahora"; });
            e.Advance(e.Now + 0.4);
            TapOn(e, "back");                                                                // the arrow leaves instead of retrying
            Assert.IsType<MainScreen>(e.App.Current);
        }

        // ------------------------------------------------------------------ full screen turns the video, not the app

        [Fact]
        public void FullScreenOnAPhoneAsksForLandscapeAndShowsThePlayerOverTheVideo()
        {
            var r = Player(null, Device.Phone);
            r.Platform.SimulateRotation = true;
            var p = Screen(r);
            Assert.False(p.IsFullscreen);
            TapOn(r, "fullscreen");
            r.Advance(r.Now + 0.6);
            Assert.Equal(OrientationRequest.Landscape, r.Platform.LastOrientation);
            Assert.True(p.IsFullscreen);
            Assert.Same(p, r.App.Current);                                                   // the same player, the same playback
            Assert.Single(r.Playback.Created);
            Assert.Equal(DeviceKind.TabletLandscape, r.App.Context.Kind);
            Assert.True(p.OverlayLayout);
            var rect = r.Platform.VideoRect.Value;
            Assert.Equal(new PixelRect(0, 0, 2400, 1080), rect);                             // the whole surface
            Assert.Contains("panel=yes", r.App.Describe());                                  // the controls are up
            Assert.True(Has(r, "compress")); Assert.True(Has(r, "play")); Assert.True(Has(r, "back"));
            Assert.False(Has(r, "fullscreen"));
        }

        [Fact]
        public void TheCompressButtonTheArrowAndBackLeaveFullScreenBeforeLeavingThePlayer()
        {
            foreach (string way in new[] { "compress", "back", "key" })
            {
                var r = Player(null, Device.Phone);
                r.Platform.SimulateRotation = true;
                TapOn(r, "fullscreen"); r.Advance(r.Now + 0.6);
                Assert.True(Screen(r).IsFullscreen);
                if (way == "key") { r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.6); }
                else { TapOn(r, way); r.Advance(r.Now + 0.6); }
                Assert.IsType<PlayerScreen>(r.App.Current);                                  // still in the player
                Assert.False(Screen(r).IsFullscreen);
                Assert.Equal(OrientationRequest.Portrait, r.Platform.LastOrientation);       // a phone goes back to vertical
                Assert.Equal(DeviceKind.PhonePortrait, r.App.Context.Kind);
                Assert.True(Screen(r).PageLayout);
                Assert.Equal(r.Platform.Screen.ToPixels(0, 0, 390, r.App.Context.Style.VideoHeight), r.Platform.VideoRect.Value);
                r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.4);                    // a second Back leaves the player
                Assert.IsType<MainScreen>(r.App.Current);
            }
        }

        [Fact]
        public void LeavingThePlayerOrPausingTheAppInFullScreenGivesTheOrientationBack()
        {
            var r = Player(null, Device.Phone);
            r.Platform.SimulateRotation = true;
            TapOn(r, "fullscreen"); r.Advance(r.Now + 0.6);
            r.App.OnPause();                                                                 // Home: the player is closed
            Assert.Equal(OrientationRequest.Portrait, r.Platform.LastOrientation);
            r.Advance(r.Now + 0.4);
            Assert.False(r.Platform.VideoRect.HasValue);
        }

        [Fact]
        public void OnATabletFullScreenGivesTheOrientationBackToTheDeviceNotToPortrait()
        {
            var r = Player(null, Device.TabletPortrait);
            r.Platform.SimulateRotation = true;
            TapOn(r, "fullscreen"); r.Advance(r.Now + 0.6);
            Assert.Equal(OrientationRequest.Landscape, r.Platform.LastOrientation);
            Assert.Equal(DeviceKind.TabletLandscape, r.App.Context.Kind);
            TapOn(r, "compress"); r.Advance(r.Now + 0.4);
            Assert.Equal(OrientationRequest.Device, r.Platform.LastOrientation);
        }

        [Fact]
        public void ATabletAlreadyInLandscapeHasNoFullScreenButtonAndItsArrowLeavesThePlayer()
        {
            var r = Player(null, Device.Tablet);
            var p = Screen(r);
            Assert.True(p.OverlayLayout);
            r.Tap(640, 300, 0.08, 0.6);                                                      // a tap on the picture shows the controls over it
            Assert.Contains("panel=yes", r.App.Describe());
            Assert.False(Has(r, "fullscreen")); Assert.False(Has(r, "compress"));
            Assert.Empty(r.Platform.OrientationRequests);                                    // it never asked for anything
            TapOn(r, "back");
            Assert.IsType<MainScreen>(r.App.Current);
        }

        // ------------------------------------------------------------------ turning the device while the player is open

        [Fact]
        public void TurningATabletInThePlayerSwitchesBetweenThePageAndTheOverlayWithoutRestartingTheChannel()
        {
            var r = Player(null, Device.TabletPortrait);
            var p = Screen(r);
            Assert.True(p.PageLayout);
            var portraitRect = r.Platform.VideoRect.Value;
            r.Platform.ChangeScreen(2560, 1600, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.Same(p, r.App.Current);
            Assert.True(p.OverlayLayout);
            Assert.Single(r.Playback.Created);
            Assert.Contains("mode=Playing", r.App.Describe());
            Assert.NotEqual(portraitRect, r.Platform.VideoRect.Value);
            Assert.Equal(new PixelRect(0, 0, 2560, 1600), r.Platform.VideoRect.Value);
            r.Platform.ChangeScreen(1600, 2560, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.True(p.PageLayout);
            Assert.Equal(portraitRect, r.Platform.VideoRect.Value);
            Assert.Single(r.Playback.Created);
        }

        // ------------------------------------------------------------------ cast

        [Fact]
        public void TheCastActionAppearsInThePlayerOnlyWhileAServiceIsAvailableAndOpensTheSheetForTheChannel()
        {
            var cast = new FakeCast();
            var r = Player(null, Device.Phone);
            r.App.Context.Cast = cast;
            Assert.False(Has(r, "cast"));
            cast.IsAvailable = true; r.Advance(r.Now + 0.3);
            Assert.True(Has(r, "cast"));
            Assert.Equal(170f, Target(r, "cast").Rect.Width, 1);                             // shares the row with full screen
            Assert.Equal(170f, Target(r, "fullscreen").Rect.Width, 1);
            TapOn(r, "cast");
            Assert.True(r.App.Context.CastUi.IsOpen);
            Assert.Equal(Screen(r).Context.Current.Id, r.App.Context.CastUi.SheetChannel.Id);   // the sheet checks the devices against the channel in view
            using (var shot = r.Render(r.Now + 0.1)) { }

            var t = Player(null, Device.Tablet);
            t.App.Context.Cast = cast; cast.IsAvailable = true; t.Advance(t.Now + 0.3);
            t.Tap(640, 300, 0.08, 0.6);
            Assert.True(Has(t, "cast"));
            TapOn(t, "cast");
            Assert.True(t.App.Context.CastUi.IsOpen);
        }

        // ------------------------------------------------------------------ a platform without the optional contracts (the Samsung TV)

        private sealed class NoContractPlatform : IPlatform
        {
            private readonly DesktopPlatform _p;
            public NoContractPlatform(DesktopPlatform p) { _p = p; }
            public string Name { get { return "plain"; } }
            public IRenderHost Render { get { return new RenderOnly(_p); } }
            public IInputSource Input { get { return _p.Input; } }
            public IWindowControl Window { get { return new WindowOnly(_p); } }
            public IAudioFocus Audio { get { return _p.Audio; } }
            public IUiThread UiThread { get { return _p.UiThread; } }
            public IPlatformPaths Paths { get { return _p.Paths; } }
            public ISystemInfo SystemInfo { get { return _p.SystemInfo; } }
            public PlatformCapabilities Capabilities { get { return _p.Capabilities; } }
            public ISnapshotSource Snapshot { get { return null; } }
            public ITextInput TextInput { get { return _p.TextInput; } }
            public void Exit() { _p.Exit(); }
            private sealed class RenderOnly : IRenderHost
            {
                private readonly DesktopPlatform _p;
                public RenderOnly(DesktopPlatform p) { _p = p; }
                public int PixelWidth { get { return _p.PixelWidth; } }
                public int PixelHeight { get { return _p.PixelHeight; } }
                public ScreenMetrics Screen { get { return _p.Screen; } }
                public event Action ScreenChanged { add { _p.ScreenChanged += value; } remove { _p.ScreenChanged -= value; } }
                public void SetPresenter(IFramePresenter presenter) { _p.SetPresenter(presenter); }
                public void Start() { }
                public void Stop() { }
                public void RequestRedraw() { _p.RequestRedraw(); }
            }
            private sealed class WindowOnly : IWindowControl
            {
                private readonly DesktopPlatform _p;
                public WindowOnly(DesktopPlatform p) { _p = p; }
                public bool Opaque { get { return _p.Opaque; } set { _p.Opaque = value; } }
                public bool KeepScreenOn { get { return _p.KeepScreenOn; } set { _p.KeepScreenOn = value; } }
            }
        }

        [Fact]
        public void APlatformWithoutTheOptionalContractsStillPlaysAndHasNoFullScreenButton()
        {
            var dp = new DesktopPlatform(Fonts) { Capabilities = PlatformCapabilities.TouchOnly };
            var d = Device.Phone;
            dp.PixelWidth = d.Width; dp.PixelHeight = d.Height; dp.Insets = d.Insets; dp.Density = d.Density;
            FakeCatalog cat; FakeChecker chk; FakeRecents rec; FakePlaybackFactory pbf;
            var services = FakeServices.Demo(out cat, out chk, out rec, out pbf);
            var app = new PApp(services, new NoContractPlatform(dp));
            app.Start();
            double now = 0;
            Action<double> adv = to => { while (now < to) { now = Math.Min(to, now + 1.0 / 30); app.NeedsFrame(now); } };
            adv(0.5);
            app.Push(new PlayerScreen(new PlayContext(cat.Categories.First().Channels, 0)));
            Scenes.WaitStarted(pbf.Created.Last());
            pbf.Created.Last().Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 9; s.TimeshiftWindowMs = 178000; });
            adv(now + 0.5);
            var ids = app.Current.TouchTargets().Select(t => t.Id).ToList();
            Assert.Contains("play", ids);
            Assert.DoesNotContain("fullscreen", ids);                                        // nothing could turn the screen: the button is not offered
            Assert.False(dp.VideoRect.HasValue);                                             // and nobody was told about a video rectangle
            Assert.Empty(dp.OrientationRequests);
            app.Pop(); adv(now + 0.3);
            app.Dispose();
        }
    }
}
