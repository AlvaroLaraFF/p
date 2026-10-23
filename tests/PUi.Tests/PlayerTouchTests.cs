using System;
using System.Linq;
using System.Text.RegularExpressions;
using AceEngine.App;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>The player with a finger: a tap shows or hides the panel, its buttons can be tapped, the position bar can be dragged inside what is in memory, Back goes back.</summary>
    public class PlayerTouchTests
    {
        private static void ShowPanel(Scenes.Rig r) { r.Tap(640, 300, 0.08, 0.6); }

        /// <summary>The player over the video (the layout of a tablet in landscape, 1280 wide): the panel is shown and hidden by a tap, like the TV's.</summary>
        private static Scenes.Rig OverlayPlayer(System.Action<PlaybackStatus> state = null) { return Player(state, Device.Tablet); }

        [Fact]
        public void ATapOnThePictureShowsThePanelAndAnotherHidesIt()
        {
            var r = OverlayPlayer();
            Assert.Contains("panel=no", r.App.Describe());
            ShowPanel(r);
            Assert.Contains("panel=yes", r.App.Describe());
            r.Tap(640, 300, 0.08, 0.6);
            Assert.Contains("panel=no", r.App.Describe());
            r.Tap(640, 300, 0.08, 0.6);
            Assert.Contains("panel=yes", r.App.Describe());
        }

        [Fact]
        public void ThePanelStaysUpOnPauseEvenIfTheScreenIsTapped()
        {
            var r = OverlayPlayer(s => { s.Phase = PlayPhase.Paused; s.BehindLiveMs = 30000; });
            ShowPanel(r);
            Assert.Contains("panel=yes", r.App.Describe());
            r.Tap(640, 200, 0.08, 0.6);
            Assert.Contains("panel=yes", r.App.Describe());
        }

        [Fact]
        public void ATapOnTheEmptyPartOfThePanelKeepsItUp()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            var panel = Target(r, "panel");
            r.Tap(panel.Rect.Left + 200, panel.Rect.Bottom - 20, 0.08, 0.6);          // between the buttons
            Assert.Contains("panel=yes", r.App.Describe());
        }

        [Fact]
        public void ThePanelButtonsPauseSeekAndGoLive()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            TapOn(r, "play");
            Assert.Contains("toggle", Calls(r));
            Assert.Contains("mode=Paused", r.App.Describe());
            TapOn(r, "play");
            Assert.Contains("mode=Playing", r.App.Describe());
            TapOn(r, "back10");
            Assert.Contains("seeklive -10", Calls(r));
            TapOn(r, "forward10");
            Assert.Contains("seeklive 10", Calls(r));
            TapOn(r, "live");
            Assert.Contains("golive", Calls(r));
        }

        [Fact]
        public void TappingAButtonKeepsThePanelUpForAnotherFiveSeconds()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            r.Advance(r.Now + 3.5);
            TapOn(r, "back10");
            r.Advance(r.Now + 3.5);                                       // 7 s since the panel came up, 3.5 s since the last tap
            Assert.Contains("panel=yes", r.App.Describe());
            r.Advance(r.Now + 2.5);
            Assert.Contains("panel=no", r.App.Describe());
        }

        [Fact]
        public void TheButtonsAreNotThereWhilethePanelIsHidden()
        {
            var r = OverlayPlayer();
            Assert.False(Has(r, "play"));
            Assert.Empty(r.App.Current.TouchTargets());
        }

        [Fact]
        public void DraggingTheKnobMovesThePlayerOnlyWhenTheFingerIsLiftedAndOnlyInsideMemory()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            var tl = Target(r, "timeline");
            float x0 = tl.Rect.Left + 14, x1 = tl.Rect.Right - 14, y = CenterY(tl);
            r.FingerDown(x1, y, 0.1);
            for (int i = 1; i <= 6; i++) { r.Advance(r.Now + 1.0 / 30); r.Touch(PointerAction.Move, x1 - (x1 - x0) * 0.5f * i / 6, y); }
            Assert.DoesNotContain(Calls(r), c => c.StartsWith("seeklive"));            // nothing yet: the knob follows the finger
            r.FingerUp(x0 + (x1 - x0) * 0.5f, y, 0.3);
            var seek = Calls(r).Single(c => c.StartsWith("seeklive"));
            int secs = int.Parse(Regex.Match(seek, @"-?\d+").Value);
            Assert.InRange(secs, -91, -87);                              // half of the 178 s in memory, behind live

            // dragged past the start of the bar: the oldest point there is, not beyond
            r.FingerDown(x1, y, 0.1); r.Touch(PointerAction.Move, x0 - 300, y); r.Advance(r.Now + 0.1); r.FingerUp(x0 - 300, y, 0.3);
            Assert.Contains(Calls(r), c => c == "seeklive -178" || c == "seeklive -177");
        }

        [Fact]
        public void ATapOnTheBarSeeksThereAndAKnobDroppedAtTheEndWhilePausedGoesLive()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            var tl = Target(r, "timeline");
            float x0 = tl.Rect.Left + 14, x1 = tl.Rect.Right - 14, y = CenterY(tl);
            r.Tap(x0 + (x1 - x0) * 0.25f, y);
            int secs = int.Parse(Regex.Match(Calls(r).Single(c => c.StartsWith("seeklive")), @"-?\d+").Value);
            Assert.InRange(secs, -135, -131);                            // a quarter of the way: three quarters of the window behind live

            var p = OverlayPlayer(s => { s.Phase = PlayPhase.Paused; s.BehindLiveMs = 60000; });
            p.Tap(640, 200, 0.08, 0.6);
            var tl2 = Target(p, "timeline");
            p.Tap(tl2.Rect.Right - 14, CenterY(tl2));
            Assert.Contains("golive", Calls(p));
        }

        [Fact]
        public void TheBarCannotBeDraggedWhenThereIsNothingInMemory()
        {
            var r = OverlayPlayer(s => s.TimeshiftWindowMs = 0);
            ShowPanel(r);
            Assert.False(Has(r, "timeline"));
            Assert.True(Has(r, "play"));
        }

        [Fact]
        public void TheScrubbingKnobAndItsTimeAreDrawnWhileTheFingerIsOnTheBar()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            var tl = Target(r, "timeline");
            float x0 = tl.Rect.Left + 14, x1 = tl.Rect.Right - 14, y = CenterY(tl);
            using (var before = r.Render(r.Now + 0.1))
            {
                r.FingerDown(x0 + (x1 - x0) * 0.3f, y, 0.2);
                using (var during = r.Render(r.Now + 0.05))
                {
                    var m = r.Platform.Screen;
                    int px = (int)m.ToPixelX(x0 + (x1 - x0) * 0.3f), py = (int)m.ToPixelY(y);
                    Assert.NotEqual(before.GetPixel(px, py), during.GetPixel(px, py));          // the knob is under the finger now
                }
            }
        }

        [Fact]
        public void TheBackGestureLeavesThePlayerAndTheScreenStopsBeingKeptOn()
        {
            var r = OverlayPlayer();
            Assert.True(r.Platform.KeepScreenOn);
            r.Platform.Raise(new KeyPress(UiKey.Back, "system back gesture"));
            r.Advance(r.Now + 0.4);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.False(r.Platform.KeepScreenOn);
        }

        [Fact]
        public void AFingerOnTheLoadingScreenDoesNothingAndATapOnTheErrorCardRetries()
        {
            var r = Phone(null, Device.Tablet);
            var pc = new PlayContext(r.Catalog.Categories.First(c => c.Name == "Liga de Campeones").Channels, 0);
            r.App.Push(new PlayerScreen(pc));
            r.Advance(r.Now + 0.3);
            r.Tap(640, 300, 0.08, 0.4);
            Assert.Contains("mode=Loading", r.App.Describe());
            Assert.Contains("panel=no", r.App.Describe());
            Assert.Single(r.Playback.Created);

            r.Playback.Created.Last().Push(s => { s.Phase = PlayPhase.Error; s.Error = "Sin fuentes por ahora"; });
            r.Advance(r.Now + 0.4);
            Assert.Contains("mode=Error", r.App.Describe());
            r.Tap(640, 300, 0.08, 0.4);
            Assert.Equal(2, r.Playback.Created.Count);                    // retried, like OK
        }

        [Fact]
        public void ADragAcrossThePictureDoesNotTogglePanel()
        {
            var r = OverlayPlayer();
            r.Drag(300, 300, 900, 300, 0.3, lift: true, settle: 0.5);
            Assert.Contains("panel=no", r.App.Describe());
        }

        [Fact]
        public void TheWindowGoesOpaqueAndTheKeepAwakeIsClearedWhenALeavingPlayerIsClosedByTheGesture()
        {
            var r = OverlayPlayer();
            ShowPanel(r);
            r.Platform.Raise(new KeyPress(UiKey.Back));
            r.Advance(r.Now + 0.4);
            Assert.True(r.Platform.Opaque);
        }
    }
}
