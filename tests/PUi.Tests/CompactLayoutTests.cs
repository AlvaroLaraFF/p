using System;
using System.Collections.Generic;
using System.Linq;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using SkiaSharp;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>
    /// The layouts of a phone and of a tablet (design-movil): what is where on each kind of device, that a finger reaches it, that the page is laid out again when the screen turns and that the
    /// TV is not touched by any of it.
    /// </summary>
    public class CompactLayoutTests
    {
        private static IEnumerable<TouchTarget> Group(Scenes.Rig r, string group) { return r.App.Current.TouchTargets().Where(t => t.Focus != null && t.Focus.Group == group); }
        private static int Columns(IEnumerable<TouchTarget> targets) { return targets.Select(t => (int)Math.Round(t.Rect.Left)).Distinct().Count(); }

        // ------------------------------------------------------------------ the TV page on each kind

        [Fact]
        public void ThePhoneShowsTwoColumnsOfCategoriesAndTheRecentsInARowWithTheSectionsAtTheBottom()
        {
            var r = Scenes.Build("main", Fonts, Device.Phone);
            var st = r.App.Context.Style;
            Assert.Equal(DeviceKind.PhonePortrait, r.App.Context.Kind);
            var cats = Group(r, "grid").ToList();
            Assert.Equal(10, cats.Count);
            Assert.Equal(2, Columns(cats));
            Assert.Equal(169f, cats[0].Rect.Width, 1);                                       // (390 - 2 x 20 - 12) / 2
            Assert.Equal(60f, cats[0].Rect.Height, 1);
            var recents = Group(r, "recents").ToList();
            Assert.Equal(200f, recents[0].Rect.Width, 1); Assert.Equal(108f, recents[0].Rect.Height, 1);
            Assert.Equal(recents[0].Rect.Top, recents[1].Rect.Top);                          // one row...
            Assert.True(recents[1].Rect.Left > recents[0].Rect.Right);                       // ...that slides sideways
            // the three sections: a bar along the bottom, centered, under everything else
            var nav = Enumerable.Range(0, 3).Select(i => Target(r, "rail" + i)).ToList();
            Assert.Equal(nav[0].Rect.Top, nav[2].Rect.Top);
            Assert.True(nav[0].Rect.Left < nav[1].Rect.Left && nav[1].Rect.Left < nav[2].Rect.Left);
            Assert.Equal(64f, nav[0].Rect.Width, 1); Assert.Equal(48f, nav[0].Rect.Height, 1);
            Assert.True(nav[0].Rect.Top > cats.Max(t => t.Rect.Bottom));
            Assert.Equal(st.W / 2, (nav[0].Rect.Left + nav[2].Rect.Right) / 2, 1);
        }

        [Fact]
        public void ATabletInPortraitShowsThreeColumnsAndALandscapeOneFourWithTheRailOnTheLeft()
        {
            var p = Scenes.Build("main", Fonts, Device.TabletPortrait);
            Assert.Equal(DeviceKind.TabletPortrait, p.App.Context.Kind);
            Assert.Equal(3, Columns(Group(p, "grid")));
            var pr = Group(p, "recents").ToList();
            Assert.Equal(3, pr.Count(t => t.Rect.Right <= 800));                             // three recents across
            Assert.True(Target(p, "rail0").Rect.Top > Group(p, "grid").Max(t => t.Rect.Bottom));

            var l = Scenes.Build("main", Fonts, Device.Tablet);
            Assert.Equal(DeviceKind.TabletLandscape, l.App.Context.Kind);
            var cats = Group(l, "grid").ToList();
            Assert.Equal(4, Columns(cats));
            Assert.Equal(4, Group(l, "recents").Count(t => t.Rect.Right <= 1280));
            var rail = Enumerable.Range(0, 3).Select(i => Target(l, "rail" + i)).ToList();
            Assert.Equal(rail[0].Rect.Left, rail[2].Rect.Left);                              // one above the other
            Assert.True(rail[0].Rect.Top < rail[1].Rect.Top && rail[1].Rect.Top < rail[2].Rect.Top);
            Assert.True(rail.All(t => t.Rect.Right < cats.Min(c => c.Rect.Left)));           // left of the content
            Assert.Equal(60f, rail[0].Rect.Width, 1);
        }

        [Fact]
        public void APhoneWhoseSurfaceIsLandscapeIsLaidOutLikeATabletInLandscape()
        {
            var r = Scenes.Build("main", Fonts, Device.PhoneLandscape);
            Assert.Equal(DeviceKind.TabletLandscape, r.App.Context.Kind);
            Assert.True(r.App.Context.Surface.IsPhone);
            Assert.Equal(4, Columns(Group(r, "grid")));
            Assert.True(r.App.Context.Surface.CanvasLogicalWidth < 1280f);                  // 1 unit = 1 dp: narrower than a tablet's canvas
        }

        [Fact]
        public void TheCategoriesAndTheKindsFillTwoThreeAndFourColumns()
        {
            foreach (var kv in new[] { new KeyValuePair<Device, int>(Device.Phone, 2), new KeyValuePair<Device, int>(Device.TabletPortrait, 3), new KeyValuePair<Device, int>(Device.Tablet, 4) })
            {
                var r = Scenes.Build("search", Fonts, kv.Key);
                Assert.Equal(kv.Value, Columns(Group(r, "grid")));
                Assert.Equal(12, Group(r, "grid").Count());
            }
        }

        // ------------------------------------------------------------------ a finger reaches the new things

        [Fact]
        public void TheBottomBarAndTheRailChangeSectionByTouchOnEveryDevice()
        {
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet })
            {
                var r = TouchRig.Phone(null, device);
                TapOn(r, "rail1"); Assert.IsType<SearchScreen>(r.App.Current);
                TapOn(r, "rail2"); Assert.IsType<SettingsScreen>(r.App.Current);
                TapOn(r, "rail0"); Assert.IsType<MainScreen>(r.App.Current);
            }
        }

        [Fact]
        public void TheBackArrowOfASubpageLeavesItAndTheRootPagesHaveNone()
        {
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet })
            {
                var r = TouchRig.Phone(null, device);
                Assert.False(Has(r, "back"));                                                // the TV page is a root: no arrow
                var list = r.Catalog.Categories.First().Channels.ToList();
                r.Checker.Default = AceEngine.Probe.LinkState.Good;
                r.App.Push(new CategoryScreen("Liga", list, Section.Tv)); r.Advance(r.Now + 1.0);
                Assert.True(Has(r, "back"));
                TapOn(r, "back");
                Assert.IsType<MainScreen>(r.App.Current);
                r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.4);
                Assert.False(Has(r, "back"));
            }
        }

        [Fact]
        public void TheRowsAreFullWidthAndTheirTargetsAreTheDrawnRows()
        {
            var r = Category();
            var rows = Group(r, "rows").ToList();
            Assert.True(rows.Count >= 5);
            Assert.All(rows, t => { Assert.Equal(358f, t.Rect.Width, 1); Assert.Equal(64f, t.Rect.Height, 1); });
            Assert.Equal(74f, rows[1].Rect.Top - rows[0].Rect.Top, 1);                       // 64 + 10
            var sort = Target(r, "sort");
            Assert.Equal(44f, sort.Rect.Height, 1);
            Assert.True(sort.Rect.Bottom < rows[0].Rect.Top);                                // the header is above the rows
        }

        [Fact]
        public void TheSettingsGroupsHaveRoundButtonsOf44AndTheSwitchesFlipWithATap()
        {
            var r = TouchRig.Phone(null, Device.Phone);
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.5);
            var plus = Target(r, "language:plus"); var minus = Target(r, "language:minus");
            Assert.Equal(44f, plus.Rect.Width, 1); Assert.Equal(44f, plus.Rect.Height, 1);
            Assert.Equal(44f, minus.Rect.Width, 1);
            var s = r.Services.Settings;
            bool live = s.OnlyLive;
            TapOn(r, "onlylive");
            Assert.NotEqual(live, s.OnlyLive);
            var before = s.Language; TapOn(r, "language:plus"); TapOn(r, "language:minus");
            Assert.Equal(before, s.Language);
        }

        [Fact]
        public void TheSearchFieldStaysAtTheTopWhileTheResultsScrollUnderIt()
        {
            var r = TouchRig.Phone(rig => { for (int i = 0; i < 40; i++) rig.Catalog.Add("Liga extra " + i, "Liga canal " + i); rig.Checker.Default = AceEngine.Probe.LinkState.Good; }, Device.Phone);
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            r.Platform.Type("liga"); r.Advance(r.Now + 0.8);
            var search = (SearchScreen)r.App.Current;
            var field = Target(r, "field");
            float top = field.Rect.Top;
            r.Drag(200, 650, 200, 250, 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(200, 250, 1.0);
            Assert.True(search.ScrollPosition > 100);
            Assert.Equal(top, Target(r, "field").Rect.Top);                                  // the field did not move
        }

        // ------------------------------------------------------------------ turning the tablet

        [Fact]
        public void TurningATabletLaysTheTvPageOutAgainWithTheSameScreenAndTheSameFocus()
        {
            var r = TouchRig.Phone(null, Device.Tablet);
            r.Platform.PressKey(UiKey.Right); r.Platform.PressKey(UiKey.Down); r.Advance(r.Now + 0.4);           // a key drives it: the focus is on a card
            var screen = r.App.Current;
            string focus = r.App.Describe();
            Assert.Contains("[focus ", focus);
            Assert.Equal(4, Columns(Group(r, "grid")));
            r.Platform.ChangeScreen(1600, 2560, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.Same(screen, r.App.Current);
            Assert.Equal(DeviceKind.TabletPortrait, r.App.Context.Kind);
            Assert.Equal(focus, r.App.Describe());
            Assert.Equal(3, Columns(Group(r, "grid")));
            Assert.True(Target(r, "rail0").Rect.Top > Group(r, "grid").Max(t => t.Rect.Bottom));              // the rail became a bar under the page
            r.Platform.ChangeScreen(2560, 1600, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.Same(screen, r.App.Current);
            Assert.Equal(4, Columns(Group(r, "grid")));
            Assert.Equal(focus, r.App.Describe());
        }

        [Fact]
        public void TurningATabletKeepsAListItsPositionItsFocusAndItsRows()
        {
            var r = TouchRig.Phone(null, Device.Tablet);
            var list = r.Catalog.Channels.ToList();
            r.Checker.Default = AceEngine.Probe.LinkState.Checking;
            for (int i = 0; i < list.Count; i++) r.Checker.Set(list[i], AceEngine.Probe.LinkState.Good, 10 + i % 50);
            r.App.Push(new CategoryScreen("Todos", list, Section.Tv)); r.Advance(r.Now + 1.0);
            var screen = (CategoryScreen)r.App.Current;
            r.Platform.PressKey(UiKey.Down); r.Platform.PressKey(UiKey.Down); r.Advance(r.Now + 0.4);
            string focus = r.App.Describe();
            int rows = screen.Model.Visible.Count;
            r.Drag(640, 500, 640, 300, 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(640, 300, 1.0);
            float scroll = screen.ScrollPosition;
            Assert.True(scroll > 100);
            r.Platform.ChangeScreen(1600, 2560, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.Same(screen, r.App.Current);
            Assert.Equal(rows, screen.Model.Visible.Count);
            Assert.Equal(focus, r.App.Describe());
            Assert.True(screen.ScrollPosition >= 0);
            var row = Group(r, "rows").First();
            Assert.Equal(r.App.Context.Style.ListR - r.App.Context.Style.ListL, row.Rect.Width, 1);          // the rows follow the new width
        }

        [Fact]
        public void TurningATabletKeepsTheSearchAndTheSettingsPage()
        {
            var r = TouchRig.Phone(rig => rig.Checker.Default = AceEngine.Probe.LinkState.Good, Device.Tablet);
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            r.Platform.Type("liga"); r.Advance(r.Now + 0.8);
            string d = r.App.Describe();
            r.Platform.ChangeScreen(1600, 2560, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.Equal(d, r.App.Describe());
            Assert.True(Group(r, "rows").Any());
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.5);
            var s = r.Services.Settings; var lang = s.Language;
            r.Platform.ChangeScreen(2560, 1600, new ScreenInsets(0, 48, 0, 32)); r.Advance(r.Now + 0.6);
            Assert.IsType<SettingsScreen>(r.App.Current);
            TapOn(r, "language:plus");
            Assert.NotEqual(lang, s.Language);
        }

        [Fact]
        public void ChangingTheInsetsMovesTheCanvasAndTheChromeStaysInsideTheSafeArea()
        {
            var r = TouchRig.Phone(null, Device.Phone);
            r.Platform.ChangeScreen(1080, 2400, new ScreenInsets(0, 200, 0, 150)); r.Advance(r.Now + 0.5);
            var st = r.App.Context.Style;
            Assert.Equal(200f, r.App.Context.Surface.CanvasTop);
            Assert.True(Target(r, "rail0").Rect.Bottom <= st.H);
            Assert.Equal((2400f - 200 - 150) / (1080f / 390f), st.H, 1);                     // the canvas is what the bars leave
            var nav = Enumerable.Range(0, 3).Select(i => Target(r, "rail" + i)).ToList();
            Assert.All(nav, t => Assert.True(t.Rect.Bottom < st.H));
        }

        // ------------------------------------------------------------------ the cast button: the component is there, hidden until a service says so

        [Fact]
        public void TheCastButtonIsNotShownWhileThereIsNoService()
        {
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet })
            {
                var r = TouchRig.Phone(null, device);
                Assert.False(Has(r, "cast"));
                Assert.False(r.App.Context.Cast.IsAvailable);                                // the default answers "not available"
                using (var shot = r.Render(r.Now + 0.1)) { }
            }
        }

        [Fact]
        public void WhenAServiceBecomesAvailableTheButtonAppearsAndPressingItOpensTheSheet()
        {
            var cast = new FakeCast();
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet })
            {
                var r = TouchRig.Phone(null, device);
                r.App.Context.Cast = cast;
                cast.IsAvailable = false;
                Assert.False(Has(r, "cast"));
                cast.IsAvailable = true;
                r.Advance(r.Now + 0.3);
                Assert.True(Has(r, "cast"));
                int before = cast.BeginSearchCalls;
                TapOn(r, "cast");
                Assert.True(r.App.Context.CastUi.IsOpen);                                    // from the TV page there is no channel: the sheet lists the devices
                Assert.Equal(before + 1, cast.BeginSearchCalls);                             // and looks for them
                Assert.Empty(cast.SendCalls);
                using (var shot = r.Render(r.Now + 0.1)) { }
                r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.5);
                Assert.False(r.App.Context.CastUi.IsOpen);
                cast.IsAvailable = false;
                r.Advance(r.Now + 0.3);
                Assert.False(Has(r, "cast"));
            }
        }

        // ------------------------------------------------------------------ legibility

        [Fact]
        public void NoTextOnAnyScreenOfAnyDeviceIsSmallerThanTwelveCssPixels()
        {
            var bad = new List<string>();
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet, Device.PhoneLandscape })
                foreach (var scene in Scenes.All.Concat(Scenes.TouchOnly))
                {
                    var r = Scenes.Build(scene, Fonts, device);
                    var gx = r.App.Context.Gx;
                    gx.ResetTextStats();
                    using (var shot = r.Render(r.Now + 0.3)) { }
                    if (gx.SmallestTextSize < 12f) bad.Add(device.Name + " " + scene + ": '" + gx.SmallestTextSample + "' at " + gx.SmallestTextSize);
                }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        // ------------------------------------------------------------------ the TV keeps its layout

        [Fact]
        public void TheTvKeepsTheDesignLayoutAndNoCompactMachinery()
        {
            var r = Scenes.Build("main", Fonts);
            var ctx = r.App.Context;
            Assert.Equal(DeviceKind.Tv, ctx.Kind);
            Assert.False(ctx.Compact);
            Assert.Null(ctx.Style);
            Assert.Equal(1920f, ctx.CW); Assert.Equal(1080f, ctx.CH);
            Assert.Empty(r.App.Current.TouchTargets());
            var cat = Scenes.Build("category", Fonts);
            var model = ((CategoryScreen)cat.App.Current).Model;
            Assert.Equal(200f, model.Left); Assert.Equal(1624f, model.Width); Assert.Equal(179f, model.Top); Assert.Equal(92f, model.RowH); Assert.Equal(14f, model.Gap);
            // the rail is where it always was
            var tv = Scenes.Build("main", Fonts);
            tv.Platform.PressKey(UiKey.Left);
            Assert.Contains("[menu 0]", tv.App.Describe());
        }

        [Fact]
        public void EveryScreenOfEveryDeviceRendersAndTheBackgroundIsOpaqueEverywhereButTheVideo()
        {
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet, Device.PhoneLandscape })
                foreach (var scene in Scenes.All)
                {
                    var r = Scenes.Build(scene, Fonts, device);
                    using (var bmp = r.Render(r.Now + 0.3))
                    {
                        var m = r.Platform.Screen;
                        // with the fake video drawn (the bench's), every pixel of every screen is opaque, bars and notch included
                        foreach (var p in new[] { new[] { 2, 2 }, new[] { m.PixelWidth - 3, 2 }, new[] { 2, m.PixelHeight - 3 }, new[] { m.PixelWidth - 3, m.PixelHeight - 3 }, new[] { m.PixelWidth / 2, m.PixelHeight / 2 } })
                            Assert.True(bmp.GetPixel(p[0], p[1]).Alpha == 255, device.Name + " " + scene + " at " + p[0] + "," + p[1]);
                    }
                }
        }
    }
}
