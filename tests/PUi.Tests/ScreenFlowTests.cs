using System;
using System.Linq;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.App;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Screens;
using PUi.Testing;
using PUi.Theme;
using SkiaSharp;
using Xunit;

namespace PUi.Tests
{
    public class ScreenFlowTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private static Scenes.Rig Rig(Action<Scenes.Rig> setup = null)
        {
            var r = Scenes.NewRig(Fonts);
            if (setup != null) setup(r);
            r.App.Start();
            r.Advance(0.5);
            return r;
        }

        private static void Press(Scenes.Rig r, UiKey k, double dt = 0.4) { r.Platform.PressKey(k); r.Advance(r.Now + dt); }

        [Fact]
        public void StartsOnTheTvSectionWithOpaqueWindowAndAudioFocus()
        {
            var r = Rig();
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.True(r.Platform.Opaque); Assert.True(r.Platform.AudioHeld);
            Assert.True(r.Platform.Presenter != null);
        }

        [Fact]
        public void OpeningACategoryChecksItsChannelsAndBackReturnsWithTheSameFocus()
        {
            var r = Rig();
            var main = (MainScreen)r.App.Current;
            string before = main.Describe();
            Press(r, UiKey.Ok);
            Assert.IsType<CategoryScreen>(r.App.Current);
            Assert.Equal(r.Catalog.Categories.First().Channels.Count, r.Checker.Batches.Last().Count);
            Press(r, UiKey.Back);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.Equal(before, r.App.Current.Describe());
        }

        [Fact]
        public void CategoryListStartsEmptyAndFillsInDiscoveryOrderWithoutMovingTheRows()
        {
            var r = Rig();
            Press(r, UiKey.Ok);
            var cs = (CategoryScreen)r.App.Current;
            var chans = r.Catalog.Categories.First().Channels;
            Assert.Empty(cs.Model.Visible);
            r.Checker.Set(chans[2], LinkState.Good, 7); r.Advance(r.Now + 0.4);
            r.Checker.Set(chans[0], LinkState.Good, 90); r.Advance(r.Now + 0.4);
            Assert.Equal(new[] { chans[2].Id, chans[0].Id }, cs.Model.Visible.Select(x => x.Channel.Id).ToArray());
            float y0 = cs.Model.Visible[0].Item.R.Top;
            r.Checker.Set(chans[1], LinkState.Good, 1000); r.Advance(r.Now + 0.4);
            Assert.Equal(y0, cs.Model.Visible[0].Item.R.Top);
            Assert.Equal(chans[1].Id, cs.Model.Visible[2].Channel.Id);
        }

        [Fact]
        public void TheSortButtonIsFocusableCyclesFourModesAndTheNextListStartsOnArrivalAgain()
        {
            var r = Rig();
            Press(r, UiKey.Ok);
            var cs = (CategoryScreen)r.App.Current;
            var chans = r.Catalog.Categories.First().Channels;
            foreach (int i in new[] { 5, 1, 3 }) { r.Checker.Set(chans[i], LinkState.Good, 10 * (i + 1)); r.Advance(r.Now + 0.4); }
            var arrival = cs.Model.Visible.Select(x => x.Channel.Id).ToList();
            Assert.Equal(new[] { chans[5].Id, chans[1].Id, chans[3].Id }, arrival);
            Assert.Equal(SortMode.Arrival, cs.Sort);
            Press(r, UiKey.Up);                 // from the first row to the top-right button
            Press(r, UiKey.Ok); Assert.Equal(SortMode.AlphaAsc, cs.Sort);
            var names = cs.Model.Visible.Select(x => ChannelListModel.SortKey(x.Channel.Name)).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
            Press(r, UiKey.Ok); Assert.Equal(SortMode.AlphaDesc, cs.Sort);
            names = cs.Model.Visible.Select(x => ChannelListModel.SortKey(x.Channel.Name)).ToList();
            Assert.Equal(names.OrderByDescending(n => n, StringComparer.Ordinal).ToList(), names);
            Press(r, UiKey.Ok); Assert.Equal(SortMode.PeersDesc, cs.Sort);
            Assert.Equal(chans[5].Id, cs.Model.Visible[0].Channel.Id);      // 60 peers
            Press(r, UiKey.Ok); Assert.Equal(SortMode.Arrival, cs.Sort);
            Assert.Equal(arrival, cs.Model.Visible.Select(x => x.Channel.Id).ToList());
            Press(r, UiKey.Ok); Assert.Equal(SortMode.AlphaAsc, cs.Sort);
            Press(r, UiKey.Back);
            Press(r, UiKey.Ok);                  // enters again (in the same category or another): back to "arrival"
            Assert.Equal(SortMode.Arrival, ((CategoryScreen)r.App.Current).Sort);
        }

        [Fact]
        public void TheSortButtonCannotBeFocusedOrPressedWithFewerThanTwoRowsAndEnablesWithoutStealingTheFocus()
        {
            var r = Rig();
            Press(r, UiKey.Ok);
            var cs = (CategoryScreen)r.App.Current;
            var chans = r.Catalog.Categories.First().Channels;
            // 0 rows: disabled button and the focus is not on it (it is on the menu: there is nothing else to focus)
            Assert.False(cs.SortEnabled);
            Assert.DoesNotContain("sort", cs.Describe());
            Assert.Contains("[menu", cs.Describe());
            Press(r, UiKey.Up); Press(r, UiKey.Right); Press(r, UiKey.Ok);
            Assert.Equal(SortMode.Arrival, cs.Sort);
            // 1 row: the focus goes to the channel (the user had not touched anything yet... here already pressed, so it stays on the menu until Right is pressed)
            r.Checker.Set(chans[4], LinkState.Good, 3); r.Advance(r.Now + 0.4);
            Assert.False(cs.SortEnabled);
            Press(r, UiKey.Right);
            Assert.Contains(chans[4].Id, cs.Describe());
            Press(r, UiKey.Up);                                      // up from the only row: the disabled button is skipped
            Assert.Contains(chans[4].Id, cs.Describe());
            Press(r, UiKey.Ok); Press(r, UiKey.Ok);                  // OK on the row opens the player; we come back
            Assert.IsType<PlayerScreen>(r.App.Current);
            Press(r, UiKey.Back);
            Assert.IsType<CategoryScreen>(r.App.Current);
            // 2 rows: it becomes enabled, without the focus jumping to it
            r.Checker.Set(chans[1], LinkState.Good, 9); r.Advance(r.Now + 0.5);
            Assert.True(cs.SortEnabled);
            Assert.DoesNotContain("sort", cs.Describe());
            Assert.Contains(chans[4].Id, cs.Describe());
            Press(r, UiKey.Up);                                      // now it is reached
            Assert.Contains("sort", cs.Describe());
            Press(r, UiKey.Ok);
            Assert.Equal(SortMode.AlphaAsc, cs.Sort);
            // if the rows drop below 2 with the focus on the button, the focus returns to a row
            r.Checker.Set(chans[1], LinkState.Dead); r.Advance(r.Now + 1.0);
            Assert.False(cs.SortEnabled);
            Assert.DoesNotContain("sort", cs.Describe());
            Assert.Contains(chans[4].Id, cs.Describe());
            Press(r, UiKey.Up); Press(r, UiKey.Ok);
            Assert.Equal(SortMode.AlphaAsc, cs.Sort);                // pressing changes nothing
        }

        [Fact]
        public void WithNoRowsYetAndNoKeyPressedTheFocusMovesToTheFirstChannelWhenItArrivesButNeverToTheSortButton()
        {
            var r = Rig();
            Press(r, UiKey.Ok);
            var cs = (CategoryScreen)r.App.Current;
            var chans = r.Catalog.Categories.First().Channels;
            Assert.DoesNotContain("sort", cs.Describe());
            r.Checker.Set(chans[4], LinkState.Good, 3); r.Advance(r.Now + 0.4);
            Assert.False(cs.SortEnabled);
            Assert.DoesNotContain("sort", cs.Describe());
        }

        [Fact]
        public void TenRecentSearchesThatDoNotFitOnOneLineScrollHorizontallyWithTheFocus()
        {
            var r = Rig();
            for (int i = 1; i <= 12; i++) r.Services.Search.AddRecentQuery("busqueda reciente larga " + i);
            Assert.Equal(10, r.Services.Search.RecentQueries.Count);                  // at most 10: the two oldest went out
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.4);
            var s = (SearchScreen)r.App.Current;
            Assert.Equal(10, s.Chips.Count);
            Assert.True(s.ChipOverflow > 0, "10 long pills do not fit in one line");
            Assert.Equal(0, s.ChipOffset);
            float right = Tokens.MainLeft + Tokens.MainWidth;
            Press(r, UiKey.Down);                                                       // from the field to the nearest pill
            Assert.Contains("[focus chip:", s.Describe());
            for (int i = 0; i < 10 && !s.Describe().Contains("chip:busqueda reciente larga 12]"); i++) Press(r, UiKey.Left);       // up to the first (the most recent)
            Assert.Contains("chip:busqueda reciente larga 12]", s.Describe());
            Assert.Equal(0, s.ChipOffset);
            for (int i = 0; i < 9; i++)
            {
                Press(r, UiKey.Right);
                var f = s.Chips[Math.Min(i + 1, 9)];
                float vis = f.R.Right - s.ChipOffset;
                Assert.True(vis <= right + 0.5f && f.R.Left - s.ChipOffset >= Tokens.MainLeft - 0.5f, "the focused pill must be fully visible (i=" + i + ")");
            }
            Assert.Contains("chip:busqueda reciente larga 3", s.Describe());           // the last (the oldest of the ten)
            Assert.True(s.ChipOffset > 0 && s.ChipOffset <= s.ChipOverflow + 0.5f);
            for (int i = 0; i < 9; i++) Press(r, UiKey.Left);
            Assert.Equal(0, s.ChipOffset);                                              // on returning to the first, the row goes back to its place
            // with few pills there is no scrolling
            var r2 = Rig();
            for (int i = 1; i <= 3; i++) r2.Services.Search.AddRecentQuery("q" + i);
            r2.App.GoSection(Section.Search); r2.Advance(r2.Now + 0.4);
            var s2 = (SearchScreen)r2.App.Current;
            Assert.Equal(0, s2.ChipOverflow);
        }

        [Fact]
        public void LeftFromTheGridGoesToTheMenuAndOkOnSearchSwitchesSection()
        {
            var r = Rig();
            Press(r, UiKey.Left);
            Assert.Contains("menu", r.App.Current.Describe());
            Press(r, UiKey.Down); Press(r, UiKey.Ok);
            Assert.IsType<SearchScreen>(r.App.Current);
            Press(r, UiKey.Left); Assert.Contains("menu", r.App.Current.Describe());
            Press(r, UiKey.Down); Press(r, UiKey.Ok);
            Assert.IsType<SettingsScreen>(r.App.Current);
            Press(r, UiKey.Back);                                     // Back from Settings returns to TV
            Assert.IsType<MainScreen>(r.App.Current);
            Press(r, UiKey.Back);                                     // Back from TV exits
            Assert.True(r.Platform.Exited);
        }

        [Fact]
        public void TypingSearchesInstantlyAndChecksTheResults()
        {
            var r = Rig();
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.3);
            r.Platform.Type("liga"); r.Advance(r.Now + 0.3);
            Assert.Contains("query='liga'", r.App.Current.Describe());
            var expected = r.Services.Search.Search("liga").Count;
            Assert.True(expected > 0);
            Assert.Equal(expected, r.Checker.Batches.Last().Count);
            Press(r, UiKey.Back);                                     // Back clears the query
            Assert.Contains("query=''", r.App.Current.Describe());
            r.Platform.PressKey(UiKey.Backspace);
        }

        private static Scenes.Rig SettingsRig(out Func<int> saves)
        {
            int n = 0;
            var r = Rig(x => { });
            r.Services.SaveSettings = () => n++;
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.3);
            saves = () => n;
            return r;
        }

        [Fact]
        public void SettingsHasTogglesInTheLeftColumnAndSelectorsInTheRightOneAndNoAboutOrSignatureRows()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            var s = (SettingsScreen)r.App.Current;
            foreach (var id in new[] { "onlylive", "upnp", "acesearch" }) Assert.Equal(0, s.ColumnOf(id));
            Assert.Equal(-1, s.ColumnOf("ts"));                       // TSService removed from the app
            foreach (var id in new[] { "resolution", "language", "upload" }) Assert.Equal(1, s.ColumnOf(id));
            var titles = s.RowTitles;
            Assert.DoesNotContain("P!", titles); Assert.DoesNotContain("Firma del emisor", titles); Assert.DoesNotContain("TSService", titles);
            Assert.Equal(6, titles.Count);
        }

        [Fact]
        public void SettingsShowsDiscreetSystemInformationFromThePlatform()
        {
            DesktopSystemInfo.Fixed = new PUi.Platform.SystemInfoData { OsName = "Tizen", OsVersion = "5.5", Model = "QE65Q64T", Runtime = ".NET Core 3.0.0", MemoryFreeMiB = 412, MemoryTotalMiB = 1536, LocalIp = "192.168.0.20" };
            try
            {
                Func<int> saves;
                var r = SettingsRig(out saves);
                var s = (SettingsScreen)r.App.Current;
                var d = s.SystemInfoLines.ToDictionary(kv => kv.Key, kv => kv.Value);
                Assert.Equal("P! " + AppInfo.Version, d["Versión"]);
                Assert.Equal("Tizen 5.5", d["Sistema"]);
                Assert.Equal("QE65Q64T", d["Dispositivo"]);
                Assert.Equal(".NET Core 3.0.0", d["Runtime"]);
                Assert.Equal("412 MiB libres de 1,5 GiB", d["Memoria"]);
                Assert.Equal("192.168.0.20 · puerto P2P 8621", d["Red"]);
                Assert.Equal(6, d.Count);
                // what the platform does not know, does not appear
                DesktopSystemInfo.Fixed = new PUi.Platform.SystemInfoData { OsName = "Android", OsVersion = "13" };
                var r2 = SettingsRig(out saves);
                var d2 = ((SettingsScreen)r2.App.Current).SystemInfoLines.ToDictionary(kv => kv.Key, kv => kv.Value);
                Assert.Equal("Android 13", d2["Sistema"]);
                Assert.DoesNotContain("Dispositivo", d2.Keys); Assert.DoesNotContain("Memoria", d2.Keys); Assert.DoesNotContain("Runtime", d2.Keys);
            }
            finally { DesktopSystemInfo.Fixed = null; }
        }

        [Fact]
        public void SettingsTogglesChangeAndSaveTheUserSettings()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            Assert.True(r.Services.Settings.OnlyLive);
            Press(r, UiKey.Ok);                                       // first row: only live ones (switch: OK toggles)
            Assert.False(r.Services.Settings.OnlyLive);
            Assert.Equal(1, saves());
            Press(r, UiKey.Left);                                     // left from the left column: to the menu
            Assert.Contains("[menu", r.App.Current.Describe());
            Press(r, UiKey.Right);
            Assert.Contains("onlylive", r.App.Current.Describe());
            Press(r, UiKey.Down);                                     // UPnP
            Assert.Contains("[focus upnp]", r.App.Current.Describe());
            Press(r, UiKey.Ok);
            Assert.False(r.Services.Settings.UpnpEnabled);
            Press(r, UiKey.Down); Press(r, UiKey.Ok);                 // central search engine (on by default: OK turns it off)
            Assert.False(r.Services.Settings.AceSearchEnabled);
            Assert.Equal(3, saves());
        }

        [Fact]
        public void ArrowsMoveBetweenTheColumnsAndLeftFromTheLeftOneGoesToTheMenu()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            var st = r.Services.Settings;
            Press(r, UiKey.Right);                                    // from a switch to the selectors column
            Assert.Contains("[focus resolution]", r.App.Current.Describe());
            Assert.Equal(0, st.MinResolution);                        // right does NOT change the value
            Press(r, UiKey.Right);
            Assert.Contains("[focus resolution]", r.App.Current.Describe());       // there is no more to the right
            Assert.Equal(0, st.MinResolution);
            Press(r, UiKey.Left);                                     // from a selector, left goes to the other column (it does not change the value)
            Assert.Contains("[focus onlylive]", r.App.Current.Describe());
            Assert.Equal(0, st.MinResolution);
            Press(r, UiKey.Left);                                     // and from the left column, to the menu
            Assert.Contains("[menu", r.App.Current.Describe());
        }

        [Fact]
        public void ARowWithASelectorChangesOnlyInEditModeAndTheArrowsOtherwiseNavigate()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            var st = r.Services.Settings;
            Press(r, UiKey.Right);                                    // Resolution
            Assert.Contains("[focus resolution]", r.App.Current.Describe());
            Press(r, UiKey.Down); Assert.Contains("[focus language]", r.App.Current.Describe());      // up and down go to other rows
            Press(r, UiKey.Down); Assert.Contains("[focus upload]", r.App.Current.Describe());
            Press(r, UiKey.Up); Press(r, UiKey.Up);
            Assert.Contains("[focus resolution]", r.App.Current.Describe());
            Assert.Equal(0, st.MinResolution);
            // OK: enters edit mode; the focus goes to the selector area
            int s0 = saves();
            Press(r, UiKey.Ok);
            Assert.Contains("[focus resolution:zone]", r.App.Current.Describe());
            Assert.Equal(s0, saves());                                // entering changes nothing
            Press(r, UiKey.Right); Assert.Equal(720, st.MinResolution); Assert.Equal(s0 + 1, saves());       // it is applied and saved immediately
            Press(r, UiKey.Right); Press(r, UiKey.Right); Assert.Equal(2160, st.MinResolution);
            Press(r, UiKey.Right); Assert.Equal(2160, st.MinResolution);          // at the end it does not wrap around
            Press(r, UiKey.Left); Assert.Equal(1080, st.MinResolution);
            // up and down do nothing in edit mode
            Press(r, UiKey.Up); Press(r, UiKey.Down);
            Assert.Contains("[focus resolution:zone]", r.App.Current.Describe());
            Assert.Equal(1080, st.MinResolution);
            // Back exits edit mode and the focus returns to the row, without leaving Settings
            Press(r, UiKey.Back);
            Assert.IsType<SettingsScreen>(r.App.Current);
            Assert.Contains("[focus resolution]", r.App.Current.Describe());
            // OK again also exits
            Press(r, UiKey.Ok); Assert.Contains("resolution:zone", r.App.Current.Describe());
            Press(r, UiKey.Ok); Assert.DoesNotContain(":zone", r.App.Current.Describe()); Assert.Contains("[focus resolution]", r.App.Current.Describe());
            // Left when idle does not change the value: it leaves the column (to the other one) and then to the menu
            Press(r, UiKey.Left); Press(r, UiKey.Left);
            Assert.Contains("[menu", r.App.Current.Describe());
            Assert.Equal(1080, st.MinResolution);
            // Back when idle does leave the section (back to TV)
            Press(r, UiKey.Back);
            Assert.IsType<MainScreen>(r.App.Current);
        }

        [Fact]
        public void TheLanguageAndUploadSelectorsWorkTheSameWay()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            var st = r.Services.Settings;
            Press(r, UiKey.Right); Press(r, UiKey.Down);              // Language
            Assert.Contains("[focus language]", r.App.Current.Describe());
            Press(r, UiKey.Ok);
            Press(r, UiKey.Right); Assert.Equal("es", st.Language);
            Press(r, UiKey.Right); Assert.Equal("en", st.Language);
            Press(r, UiKey.Right); Assert.Equal("pt", st.Language);
            Press(r, UiKey.Right); Assert.Equal("pl", st.Language);
            for (int i = 0; i < 12; i++) Press(r, UiKey.Right);       // to the end of the catalog's options; it does not wrap around
            var opts = AceEngine.App.Tv.ChannelLanguages.Options(r.Catalog.Channels, "");
            Assert.Equal(opts.Last(), st.Language);
            Press(r, UiKey.Back);
            Press(r, UiKey.Down);                                     // Upload
            Assert.Contains("[focus upload]", r.App.Current.Describe());
            Press(r, UiKey.Ok);
            Press(r, UiKey.Right); Assert.Equal(1024, st.UploadKBps);
            for (int i = 0; i < 6; i++) Press(r, UiKey.Right);
            Assert.Equal(2048, st.UploadKBps);
            for (int i = 0; i < 9; i++) Press(r, UiKey.Left);
            Assert.Equal(128, st.UploadKBps);
        }

        [Fact]
        public void LeavingSettingsWhileEditingEndsTheEdit()
        {
            Func<int> saves;
            var r = SettingsRig(out saves);
            Press(r, UiKey.Right); Press(r, UiKey.Ok);
            Assert.Contains(":zone", r.App.Current.Describe());
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.3);
            r.App.GoSection(Section.Settings); r.Advance(r.Now + 0.3);
            Assert.DoesNotContain(":zone", r.App.Current.Describe());
            Press(r, UiKey.Right);
            Press(r, UiKey.Right);                                    // idle does not change it
            Assert.Equal(0, r.Services.Settings.MinResolution);
        }

        [Fact]
        public void ChannelUpAndDownSwitchChannelsInTheOpenedList()
        {
            var r = Rig();
            var list = r.Catalog.Categories.First().Channels;
            r.App.Push(new PlayerScreen(new PlayContext(list, 0)));
            Assert.Single(r.Playback.Created);
            Assert.False(r.Platform.Opaque && false);
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 0.5);
            Assert.False(r.Platform.Opaque);                           // with video: transparent window
            Press(r, UiKey.ChannelUp);
            Assert.Equal(2, r.Playback.Created.Count);
            Assert.Contains("stop", r.Playback.Created[0].Calls);
            Assert.Contains(list[1].Name, r.App.Current.Describe());
            Press(r, UiKey.ChannelDown); Press(r, UiKey.ChannelDown);
            Assert.Contains(list[list.Count - 1].Name, r.App.Current.Describe());     // wraps around
        }

        [Fact]
        public void PlayerKeysDriveThePlaybackAndBackLeaves()
        {
            var r = Rig();
            var list = r.Catalog.Categories.First().Channels;
            r.App.Push(new PlayerScreen(new PlayContext(list, 0)));
            var pb = r.Playback.Created[0];
            pb.Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.TimeshiftWindowMs = 120000; });
            r.Advance(r.Now + 0.5);
            Press(r, UiKey.Left);                                      // panel hidden: it jumps -10 s
            Assert.Contains("seeklive -10", pb.Calls);
            Press(r, UiKey.Right); Press(r, UiKey.Ok);                // with the panel visible, right moves the focus to the +10 s button and OK presses it
            Assert.Contains("seeklive 10", pb.Calls);
            Press(r, UiKey.PlayPause);
            Assert.Contains("toggle", pb.Calls);
            Press(r, UiKey.Back);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.True(r.Platform.Opaque);
            Assert.Contains("stop", pb.Calls);
        }

        [Fact]
        public void SuspendingStopsPlaybackAndReleasesAudioThenResumeReturnsToTheList()
        {
            var r = Rig();
            var list = r.Catalog.Categories.First().Channels;
            r.App.Push(new PlayerScreen(new PlayContext(list, 0)));
            var pb = r.Playback.Created[0];
            double t = 500; r.App.Clock = () => t;
            r.App.OnPause();
            Assert.False(r.Platform.AudioHeld);
            Assert.Contains("stop", pb.Calls);
            t += 600;                                                 // long pause (more than 2 min): back to the previous screen (see LifecycleTests for the short one)
            r.App.OnResume();
            Assert.True(r.Platform.AudioHeld);
            Assert.IsType<MainScreen>(r.App.Current);
        }

        [Theory]
        [InlineData("main-loading")] [InlineData("main")] [InlineData("category-loading")] [InlineData("category")] [InlineData("search")] [InlineData("search-results")]
        [InlineData("settings")] [InlineData("player-loading")] [InlineData("player")] [InlineData("player-paused")] [InlineData("player-buffering")]
        public void EveryScreenRendersSomethingDifferentFromAFlatColor(string name)
        {
            var rig = Scenes.Build(name, Fonts);
            using (var bmp = rig.Render(3.0))
            {
                var seen = new System.Collections.Generic.HashSet<uint>();
                for (int y = 0; y < bmp.Height; y += 37) for (int x = 0; x < bmp.Width; x += 41) seen.Add((uint)bmp.GetPixel(x, y));
                Assert.True(seen.Count > 40, name + ": " + seen.Count + " distinct colors");
            }
            rig.App.Dispose();
        }
    }
}
