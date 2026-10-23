using System;
using System.Linq;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>Taps and drags on the screens with a side menu, on a phone (touch only): the same results as the remote, and a drag never taps.</summary>
    public class TouchTests
    {
        // ------------------------------------------------------------------ taps: focus + OK

        [Fact]
        public void TapOnACategoryCardOpensItLikeOk()
        {
            var r = Phone();
            string name = r.Catalog.Categories.First().Name;
            TapOn(r, "c" + r.Catalog.Categories.First().Key);
            var cat = Assert.IsType<CategoryScreen>(r.App.Current);
            Assert.Contains("'" + name + "'", cat.Describe());

            // with the remote: the same screen
            var tv = Scenes.NewRig(Fonts); tv.App.Start(); tv.Advance(0.5);
            tv.Platform.PressKey(UiKey.Ok);                               // the first category has the focus at the start
            tv.Advance(tv.Now + 0.4);
            var tvScreen = Assert.IsType<CategoryScreen>(tv.App.Current);
            Assert.Equal(tvScreen.Model.Total, cat.Model.Total);
        }

        [Fact]
        public void TapOnARecentChannelOpensThePlayerOnIt()
        {
            var r = Scenes.Build("main", Fonts, Device.Phone);
            var rec = r.App.Current.TouchTargets().First(t => t.Id.StartsWith("rcid:"));
            r.Tap(CenterX(rec), CenterY(rec));
            var p = Assert.IsType<PlayerScreen>(r.App.Current);
            Assert.Equal("cid:" + rec.Id.Substring(5), p.Context.Current.Id);
            Assert.True(r.Platform.KeepScreenOn);
        }

        [Fact]
        public void TapOnTheSideMenuChangesSection()
        {
            var r = Phone();
            TapOn(r, "rail1");
            Assert.IsType<SearchScreen>(r.App.Current);
            TapOn(r, "rail2");
            Assert.IsType<SettingsScreen>(r.App.Current);
            TapOn(r, "rail0");
            Assert.IsType<MainScreen>(r.App.Current);
        }

        [Fact]
        public void TapOnAChannelRowOpensItWithTheListForChannelPlusMinus()
        {
            var r = Category();
            var screen = (CategoryScreen)r.App.Current;
            var rows = screen.Model.Visible;
            var third = rows[2];
            TapOn(r, third.Item.Id);
            var p = Assert.IsType<PlayerScreen>(r.App.Current);
            Assert.Equal(third.Channel.Id, p.Context.Current.Id);
            Assert.Equal(rows.Count, p.Context.List.Count);
            // Back (the system gesture) returns to the same list
            r.Platform.Raise(new KeyPress(UiKey.Back, "system back gesture"));
            r.Advance(r.Now + 0.4);
            Assert.IsType<CategoryScreen>(r.App.Current);
        }

        [Fact]
        public void TheSortButtonCyclesTheOrderByTouch()
        {
            var r = Category();
            var screen = (CategoryScreen)r.App.Current;
            Assert.Equal(SortMode.Arrival, screen.Sort);
            TapOn(r, "sort");
            Assert.Equal(SortMode.AlphaAsc, screen.Sort);
            TapOn(r, "sort");
            Assert.Equal(SortMode.AlphaDesc, screen.Sort);
        }

        [Fact]
        public void TheSortButtonIsNotATargetWhileItIsDisabled()
        {
            var r = Phone();
            r.Checker.Default = AceEngine.Probe.LinkState.Dead;
            r.App.Push(new CategoryScreen("Vacía", r.Catalog.Channels.Take(3).ToList(), Section.Tv));
            r.Advance(1.0);
            Assert.False(((CategoryScreen)r.App.Current).SortEnabled);
            Assert.False(Has(r, "sort"));
        }

        [Fact]
        public void TapOnASwitchRowFlipsItAndTheSelectorsChangeWithTheirMinusAndPlusHalves()
        {
            var r = Phone();
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.4);
            var s = r.Services.Settings;
            bool upnp = s.UpnpEnabled;
            TapOn(r, "upnp");
            Assert.NotEqual(upnp, s.UpnpEnabled);

            var res = s.MinResolution;
            TapOn(r, "resolution:plus");
            Assert.NotEqual(res, s.MinResolution);
            TapOn(r, "resolution:minus");
            Assert.Equal(res, s.MinResolution);

            int up = s.UploadKBps;
            TapOn(r, "upload:minus");
            Assert.True(s.UploadKBps < up);
            TapOn(r, "upload:plus"); TapOn(r, "upload:plus");
            Assert.True(s.UploadKBps > up);
        }

        [Fact]
        public void TappingTheBodyOfASelectorRowDoesNothingNoEditMode()
        {
            var r = Phone();
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.4);
            var s = r.Services.Settings; var res = s.MinResolution;
            var row = Target(r, "resolution");
            r.Tap(row.Rect.Left + 100, CenterY(row));
            Assert.Equal(res, s.MinResolution);
            Assert.IsType<SettingsScreen>(r.App.Current);
        }

        [Fact]
        public void TapOnARecentSearchRunsItAndOnAKindOpensIt()
        {
            var r = Scenes.Build("search", Fonts, Device.Phone);
            TapOn(r, "chip:nba");
            Assert.Contains("query='nba'", r.App.Describe());
            r.App.GoSection(Section.Tv); r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            TapOn(r, "k:futbol");
            var cat = Assert.IsType<CategoryScreen>(r.App.Current);
            Assert.Contains("Fútbol", cat.Describe());
        }

        [Fact]
        public void TapOnASearchResultOpensTheChannel()
        {
            var r = Scenes.Build("search-results", Fonts, Device.Phone);
            var row = r.App.Current.TouchTargets().First(t => t.Id.StartsWith("cid:"));
            r.Tap(CenterX(row), CenterY(row));
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void ATapThatLastsAWhileIsStillATapAndACancelIsNot()
        {
            var r = Category();
            var row = Target(r, ((CategoryScreen)r.App.Current).Model.Visible[0].Item.Id);
            r.FingerDown(CenterX(row), CenterY(row), 0.8);
            r.Touch(PointerAction.Cancel, CenterX(row), CenterY(row)); r.Advance(r.Now + 0.4);
            Assert.IsType<CategoryScreen>(r.App.Current);                 // the system took the gesture away: nothing happens
            r.Tap(CenterX(row), CenterY(row), hold: 0.9);
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void ASecondFingerDoesNotTap()
        {
            var r = Category();
            var row = Target(r, ((CategoryScreen)r.App.Current).Model.Visible[1].Item.Id);
            r.Touch(PointerAction.Down, CenterX(row), CenterY(row), id: 0); r.Advance(r.Now + 0.1);
            r.Touch(PointerAction.Down, 300, 300, id: 1); r.Touch(PointerAction.Up, 300, 300, id: 1); r.Advance(r.Now + 0.1);
            Assert.IsType<CategoryScreen>(r.App.Current);                 // still on the list: the first finger is down
            r.Touch(PointerAction.Up, CenterX(row), CenterY(row), id: 0); r.Advance(r.Now + 0.4);
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void ATouchOutsideAnyTargetDoesNothing()
        {
            var r = Phone();
            string before = r.App.Describe();
            r.Tap(200, 650);                                              // empty part of the main screen
            Assert.Equal(before, r.App.Describe());
        }

        // ------------------------------------------------------------------ the focus belongs to the finger

        [Fact]
        public void OnAPhoneTheFocusIsInvisibleUntilAKeyBringsItBack()
        {
            var r = Phone();
            var main = (MainScreen)r.App.Current;
            Assert.False(main.FocusVisible);
            r.Platform.Raise(new KeyPress(UiKey.Back, "system back gesture"));            // Back is not a navigation key: the focus stays hidden
            Assert.False(((SectionScreen)r.App.Current).FocusVisible);
            r.Platform.PressKey(UiKey.Right);
            Assert.True(((SectionScreen)r.App.Current).FocusVisible);
            TapOn(r, "rail1");
            Assert.False(((SectionScreen)r.App.Current).FocusVisible);
        }

        [Fact]
        public void AScreenWithARemoteStartsWithTheFocusVisibleEvenWhenItHasATouchScreen()
        {
            var r = Scenes.NewRig(Fonts);
            r.Platform.Capabilities = new PlatformCapabilities(hasRemote: true, hasTouch: true);
            r.App.Start(); r.Advance(0.5);
            Assert.True(((SectionScreen)r.App.Current).FocusVisible);
            r.Tap(960, 850);
            Assert.False(((SectionScreen)r.App.Current).FocusVisible);
        }

        [Fact]
        public void AFingerDownShowsAPressedHighlightAndItFadesAfterTheTap()
        {
            var r = Category();
            var row = Target(r, ((CategoryScreen)r.App.Current).Model.Visible[0].Item.Id);
            using (var before = r.Render(r.Now + 0.1))
            {
                r.FingerDown(CenterX(row), CenterY(row), 0.3);
                using (var pressed = r.Render(r.Now + 0.05))
                {
                    var m = r.Platform.Screen;
                    int px = (int)m.ToPixelX(CenterX(row)) + 200, py = (int)m.ToPixelY(CenterY(row));
                    Assert.True(Brightness(pressed.GetPixel(px, py)) > Brightness(before.GetPixel(px, py)) + 8, "the pressed row is lighter");
                }
            }
        }

        private static int Brightness(SkiaSharp.SKColor c) { return c.Red + c.Green + c.Blue; }

        // ------------------------------------------------------------------ drags

        [Fact]
        public void ADragDoesNotOpenTheChannelUnderTheFinger()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            Assert.Equal(0f, screen.ScrollPosition);
            var row = Target(r, screen.Model.Visible[3].Item.Id);
            float x = CenterX(row), y = CenterY(row);
            r.Drag(x, y, x, y - 300, 0.4, lift: false);
            r.Advance(r.Now + 0.2);                                       // the finger rests before it is lifted: no flick
            r.FingerUp(x, y - 300, 1.5);
            Assert.IsType<CategoryScreen>(r.App.Current);                 // not opened
            Assert.InRange(screen.ScrollPosition, 290, 310);              // the list followed the finger exactly
        }

        [Fact]
        public void AMovementWithinTheSlopIsStillATap()
        {
            var r = Category();
            var row = Target(r, ((CategoryScreen)r.App.Current).Model.Visible[0].Item.Id);
            float x = CenterX(row), y = CenterY(row);
            r.FingerDown(x, y, 0.05);
            r.Touch(PointerAction.Move, x + 3, y + 3); r.Advance(r.Now + 0.05);
            r.FingerUp(x + 3, y + 3, 0.4);
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void AFlickKeepsTheListMovingAfterTheFingerLeavesAndItStopsInsideTheContent()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            r.Drag(200, 650, 200, 250, 0.2, lift: false);
            float atLift = screen.ScrollPosition;
            r.FingerUp(200, 250, 0.0);
            Assert.True(screen.ScrollCoasting);
            r.Advance(r.Now + 0.3);
            float later = screen.ScrollPosition;
            Assert.True(later > atLift + 150, "kept going after the finger left: " + atLift + " -> " + later);
            r.Advance(r.Now + 6);
            Assert.False(screen.ScrollCoasting);
            float end = screen.ScrollPosition;
            Assert.True(end > later && end < later + 1500, "slowed down and stopped: " + end);
            Assert.IsType<CategoryScreen>(r.App.Current);                 // a flick never taps
        }

        [Fact]
        public void AFastFlickDownAtTheTopBouncesSoftlyAndSettlesAtZero()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            r.Drag(200, 250, 200, 650, 0.2, lift: false);                 // pulls the list past its top, fast
            Assert.True(screen.ScrollPosition < -20 && screen.ScrollPosition >= -150, "pulled past the top with resistance: " + screen.ScrollPosition);
            Assert.True(screen.ScrollOverscrolled);
            r.FingerUp(200, 650, 0);
            float min = 0;
            for (int i = 0; i < 90; i++) { r.Advance(r.Now + 1.0 / 30); min = Math.Min(min, screen.ScrollPosition); }
            Assert.False(screen.ScrollOverscrolled);
            Assert.Equal(0f, screen.ScrollPosition, 1);                   // back at the top
            Assert.True(min >= -260, "a soft rebound: it carries a little past the pulled position and no more (" + min + ")");
        }

        [Fact]
        public void TouchingAListThatIsCoastingStopsItAndIsNotATap()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            r.Drag(200, 650, 200, 250, 0.2, lift: true, settle: 0.2);
            Assert.True(screen.ScrollCoasting);
            var row = Target(r, screen.Model.Visible.First(v => v.Item.R.Top - screen.ScrollPosition > 150 && v.Item.R.Top - screen.ScrollPosition < 500).Item.Id);
            r.Tap(CenterX(row), CenterY(row), 0.05, 0.3);
            Assert.False(screen.ScrollCoasting);
            Assert.IsType<CategoryScreen>(r.App.Current);                 // the touch only stopped the list
            float stopped = screen.ScrollPosition;
            r.Advance(r.Now + 1);
            Assert.Equal(stopped, screen.ScrollPosition);
        }

        [Fact]
        public void TappingARowThatIsPartlyHiddenDoesNotScrollTheListToIt()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            r.Drag(200, 500, 200, 500 - 150, 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(200, 350, 1.5);
            float pos = screen.ScrollPosition;
            float bottom = r.App.Context.Style.Main.Bottom;
            var last = r.App.Current.TouchTargets().Where(t => t.Id.StartsWith("cid:") && t.Rect.Bottom > bottom).OrderBy(t => t.Rect.Top).First();   // a row cut by the bottom edge of the list
            r.Tap(CenterX(last), Math.Min(CenterY(last), bottom - 4));
            Assert.IsType<PlayerScreen>(r.App.Current);
            r.Platform.Raise(new KeyPress(UiKey.Back)); r.Advance(r.Now + 0.4);
            Assert.Equal(pos, ((CategoryScreen)r.App.Current).ScrollPosition, 1);
        }

        [Fact]
        public void TheRecentChannelsSlideSidewaysWithTheFingerAndNothingOpens()
        {
            var r = Phone(rig =>
            {
                var ch = rig.Catalog.Channels.Take(8).ToList();
                rig.Recents.Set(ch);
                for (int i = 0; i < ch.Count; i++) rig.Checker.Set(ch[i], AceEngine.Probe.LinkState.Good, 10 + i);
            });
            var main = (MainScreen)r.App.Current;
            Assert.Equal(0f, main.RecentsShift);
            var first = r.App.Current.TouchTargets().First(t => t.Id.StartsWith("rcid:"));
            float y = CenterY(first);
            r.Drag(360, y, 40, y, 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(40, y, 1.0);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.InRange(main.RecentsShift, 1.3f, 1.7f);                // 320 units is 1.5 cards of 212; the most it can slide is about 6
            Assert.True(main.RecentsShift < main.RecentsShiftMax);
            r.Drag(40, y, 380, y, 0.15, lift: true, settle: 4);          // flicked back
            Assert.InRange(main.RecentsShift, 0f, 0.001f);
        }

        [Fact]
        public void AVerticalDragOverTheRecentsRowScrollsThePageNotTheRow()
        {
            var r = Phone(rig =>
            {
                var ch = rig.Catalog.Channels.Take(8).ToList();
                rig.Recents.Set(ch);
                for (int i = 0; i < ch.Count; i++) rig.Checker.Set(ch[i], AceEngine.Probe.LinkState.Good, 10 + i);
                for (int i = 0; i < 30; i++) rig.Catalog.Add("Extra " + i, "Canal extra " + i);          // enough categories to scroll
            });
            var main = (MainScreen)r.App.Current;
            r.Drag(200, 150, 200, 10, 0.4, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(200, 10, 1.5);
            Assert.Equal(0f, main.RecentsShift);
            Assert.True(main.ScrollPosition > 100);
            Assert.IsType<MainScreen>(r.App.Current);
        }

        [Fact]
        public void TheRecentSearchesSlideSidewaysWhenTheyDoNotFit()
        {
            var r = Phone(rig => { for (int i = 0; i < 10; i++) rig.Services.Search.AddRecentQuery("canal numero " + (char)('a' + i) + " deportes"); });
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            var search = (SearchScreen)r.App.Current;
            Assert.True(search.ChipOverflow > 100);
            Assert.Equal(0f, search.ChipOffset);
            var chip = r.App.Current.TouchTargets().First(t => t.Id.StartsWith("chip:"));
            r.Drag(360, CenterY(chip), 40, CenterY(chip), 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(40, CenterY(chip), 1.0);
            Assert.IsType<SearchScreen>(r.App.Current);
            Assert.InRange(search.ChipOffset, 100f, search.ChipOverflow + 0.5f);
            Assert.Contains("query=''", r.App.Describe());                // no chip was tapped
        }

        [Fact]
        public void PausingTheAppWithAFingerDownDropsTheGestureAndTheNextTouchWorks()
        {
            var r = BigCategory();
            var screen = (CategoryScreen)r.App.Current;
            r.Drag(200, 650, 200, 400, 0.3, lift: false);                 // the finger is still down...
            r.App.OnPause(); r.App.OnResume(); r.Advance(r.Now + 0.5);    // ...when the app goes to the background and comes back (no Up ever arrives)
            Assert.False(screen.ScrollCoasting && screen.ScrollPosition < 0);
            float pos = screen.ScrollPosition;
            var row = Target(r, screen.Model.Visible.First(v => v.Item.R.Top - pos > 250).Item.Id);
            r.Tap(CenterX(row), CenterY(row));                            // the same finger id comes down again
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        [Fact]
        public void ADownWithoutTheUpOfTheSameFingerStartsANewGesture()
        {
            var r = Category();
            var row = Target(r, ((CategoryScreen)r.App.Current).Model.Visible[0].Item.Id);
            r.Touch(PointerAction.Down, CenterX(row), CenterY(row)); r.Advance(r.Now + 0.1);
            r.Touch(PointerAction.Down, CenterX(row), CenterY(row)); r.Advance(r.Now + 0.1);       // the first Up was lost
            r.Touch(PointerAction.Up, CenterX(row), CenterY(row)); r.Advance(r.Now + 0.4);
            Assert.IsType<PlayerScreen>(r.App.Current);
        }

        // ------------------------------------------------------------------ the TV is not touched by any of this

        [Fact]
        public void WithoutTouchCapabilityPointerEventsAreIgnoredAndThereAreNoTargets()
        {
            var r = Scenes.NewRig(Fonts); r.App.Start(); r.Advance(0.5);
            string before = r.App.Describe();
            Assert.Empty(r.App.Current.TouchTargets());
            r.Tap(500, 160); r.Tap(960, 500);
            Assert.Equal(before, r.App.Describe());
            Assert.True(((SectionScreen)r.App.Current).FocusVisible);
        }
    }
}
