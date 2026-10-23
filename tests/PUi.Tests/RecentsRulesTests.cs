using System;
using System.Linq;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Screens;
using PUi.Testing;
using Xunit;

namespace PUi.Tests
{
    public class WatchTrackerTests
    {
        private static bool Run(WatchTracker w, PlayerMode m, double from, double to, double step = 1.0)
        {
            bool fired = false;
            for (double t = from; t <= to; t += step) fired |= w.Update(m, t);
            return fired;
        }

        [Fact]
        public void FiresOnceAfterMoreThanFiveMinutesOfPlayback()
        {
            var w = new WatchTracker();
            Assert.False(Run(w, PlayerMode.Playing, 0, 299));
            Assert.False(w.Qualified);
            Assert.True(Run(w, PlayerMode.Playing, 300, 302));
            Assert.True(w.Qualified);
            Assert.False(Run(w, PlayerMode.Playing, 303, 400));         // a single time
        }

        [Fact]
        public void LoadingAndPausesAreNotCountedButShortOnesDoNotReset()
        {
            var w = new WatchTracker();
            Run(w, PlayerMode.Loading, 0, 30);                          // loading does not count
            Assert.Equal(0, w.WatchedSeconds, 3);
            Run(w, PlayerMode.Playing, 31, 130);                        // 100 s
            Run(w, PlayerMode.Paused, 131, 170);                        // 40 s pause: it neither adds nor resets
            Assert.InRange(w.WatchedSeconds, 99, 101);
            Run(w, PlayerMode.Playing, 171, 330);                       // another 160 s -> 260 s
            Assert.False(w.Qualified);
            Run(w, PlayerMode.Buffering, 331, 350);                     // 20 s of buffering: it does not reset
            Assert.True(Run(w, PlayerMode.Playing, 351, 400));          // goes past 300 s
        }

        [Fact]
        public void AnInterruptionLongerThanAMinuteResetsTheCounter()
        {
            var w = new WatchTracker();
            Run(w, PlayerMode.Playing, 0, 250);
            Run(w, PlayerMode.Paused, 251, 330);                        // 80 s of pause
            Assert.Equal(0, w.WatchedSeconds, 3);
            Assert.False(Run(w, PlayerMode.Playing, 331, 600));         // 270 s from zero: not yet
            Assert.True(Run(w, PlayerMode.Playing, 601, 640));
        }

        [Fact]
        public void AnErrorResetsAndChangingChannelStartsAFreshCounter()
        {
            var w = new WatchTracker();
            Run(w, PlayerMode.Playing, 0, 200);
            w.Update(PlayerMode.Error, 201);
            Assert.Equal(0, w.WatchedSeconds, 3);
            Run(w, PlayerMode.Playing, 202, 400);
            Assert.False(w.Qualified);
            w.Reset();
            Assert.Equal(0, w.WatchedSeconds, 3);
            Assert.False(Run(w, PlayerMode.Playing, 500, 700));
        }

        [Fact]
        public void LongGapsBetweenUpdatesAreCappedSoASuspendedAppCannotQualify()
        {
            var w = new WatchTracker();
            w.Update(PlayerMode.Playing, 0);
            Assert.False(w.Update(PlayerMode.Playing, 10000));          // the app was stopped: at most 5 s
            Assert.True(w.WatchedSeconds <= 5.01);
        }
    }

    public class RecentsModelTests
    {
        private static (RecentsModel m, FakeChecker chk, FakeCatalog cat) Make(int stored)
        {
            var cat = new FakeCatalog();
            for (int i = 0; i < stored; i++) cat.Add("Cat", "Canal " + (i + 1));
            var m = new RecentsModel();
            m.SetStored(cat.Channels);
            return (m, new FakeChecker(), cat);
        }

        [Fact]
        public void NeverMoreThanTenAreShownEvenIfTwentyAreStoredAndAllLive()
        {
            var (m, chk, cat) = Make(20);
            foreach (var c in cat.Channels) chk.Set(c, LinkState.Good, 5);
            m.Refresh(chk, false, 0);
            Assert.Equal(10, m.Visible.Count);
            Assert.Equal(Enumerable.Range(1, 10).Select(i => "Canal " + i), m.Visible.Select(c => c.Channel.Name));
        }

        [Fact]
        public void WhileCheckingTheyAreShownAsWaitingAndDeadOnesFadeAndClosePeers()
        {
            var (m, chk, cat) = Make(5);
            m.Refresh(chk, false, 0);
            Assert.Equal(5, m.Visible.Count);
            Assert.All(m.Visible, c => Assert.Equal(Signal.Pending, c.View.State));         // design wait (spinner)
            chk.Set(cat.Channels[1], LinkState.Dead); chk.Set(cat.Channels[3], LinkState.Unknown);
            chk.Set(cat.Channels[0], LinkState.Good, 9);
            m.Refresh(chk, false, 1);
            Assert.Equal(new[] { "Canal 1", "Canal 3", "Canal 5" }, m.Visible.Select(c => c.Channel.Name));
            Assert.Equal(2, m.Fading.Count);                         // they fade away, they do not disappear abruptly
            Assert.True(m.Animating(1.1));
            // without gaps: the remaining ones take the first positions
            Assert.Equal(new[] { 200f, 200f + 330.4f, 200f + 2 * 330.4f }, m.Visible.Select(c => c.Item.R.Left).ToArray());
            // and they slide from where they were
            var c3 = m.Visible[1];
            Assert.True(c3.X.Value(1.0) > c3.TargetX);
            Assert.Equal(c3.TargetX, c3.X.Value(2.0), 1);
            m.Refresh(chk, false, 2);
            Assert.Empty(m.Fading);
        }

        [Fact]
        public void ExtraStoredOnesOnlyFillGapsWhenTheyAreKnownToBeLive()
        {
            var (m, chk, cat) = Make(15);
            for (int i = 0; i < 10; i++) chk.Set(cat.Channels[i], LinkState.Good, 3);
            chk.Set(cat.Channels[2], LinkState.Dead); chk.Set(cat.Channels[5], LinkState.Dead);
            m.Refresh(chk, false, 0);
            Assert.Equal(8, m.Visible.Count);                         // 11 and 12 have not been checked yet: they do not enter
            chk.Set(cat.Channels[10], LinkState.Good, 7);
            chk.Set(cat.Channels[11], LinkState.Dead);
            chk.Set(cat.Channels[12], LinkState.Good, 4);
            m.Refresh(chk, false, 1);
            Assert.Equal(10, m.Visible.Count);
            Assert.Equal("Canal 11", m.Visible[8].Channel.Name);
            Assert.Equal("Canal 13", m.Visible[9].Channel.Name);
            Assert.DoesNotContain(m.Visible, c => c.Channel.Name == "Canal 14" || c.Channel.Name == "Canal 15");
        }

        [Fact]
        public void EverythingDeadLeavesNothingToShow()
        {
            var (m, chk, cat) = Make(4);
            foreach (var c in cat.Channels) chk.Set(c, LinkState.Dead);
            m.Refresh(chk, false, 0);
            Assert.Empty(m.Visible);
        }

        [Fact]
        public void LiveOnesWithFewPeersStillCount()
        {
            var (m, chk, cat) = Make(2);
            chk.Set(cat.Channels[0], LinkState.Good, 1);
            m.Refresh(chk, false, 0);
            Assert.Equal(2, m.Visible.Count);
            Assert.Equal("1", m.Visible[0].View.Peers);
        }
    }

    public class RecentsScreenTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        [Fact]
        public void TvSectionChecksAllStoredRecentsAndShowsOnlyThoseWithPeers()
        {
            var r = Scenes.NewRig(Fonts);
            var names = new[] { "M+ Liga de Campeones 1080p", "DAZN La Liga 1 FHD", "M+ Baloncesto 1080p", "Teledeporte 1080p", "Eurosport 2 1080p" };
            var ch = names.Select(n => r.Catalog.Channels.First(c => c.Name == n)).ToList();
            r.Recents.Set(ch);
            r.App.Start(); r.Advance(0.5);
            Assert.Equal(5, r.Checker.Batches.Last().Count);                                  // in a batch
            Assert.Contains("recents=5/5", r.App.Current.Describe());                       // waiting (spinner)
            r.Checker.Set(ch[0], LinkState.Good, 73); r.Checker.Set(ch[1], LinkState.Dead); r.Checker.Set(ch[2], LinkState.Good, 9);
            r.Checker.Set(ch[3], LinkState.Unknown); r.Checker.Set(ch[4], LinkState.Dead);
            r.Advance(r.Now + 1.0);
            Assert.Contains("recents=2/5", r.App.Current.Describe());
        }

        [Fact]
        public void WhenTheFocusedRecentDiesTheFocusMovesToItsNeighbour()
        {
            var r = Scenes.NewRig(Fonts);
            var ch = r.Catalog.Channels.Take(3).ToList();
            r.Recents.Set(ch);
            foreach (var c in ch) r.Checker.Set(c, LinkState.Good, 5);
            r.App.Start(); r.Advance(0.5);
            r.Platform.PressKey(UiKey.Right); r.Advance(r.Now + 0.4);            // focus on the second
            Assert.Contains("focus r" + ch[1].Id, r.App.Current.Describe());
            r.Checker.Set(ch[1], LinkState.Dead);
            r.Advance(r.Now + 0.6);
            Assert.Contains("recents=2/3", r.App.Current.Describe());
            Assert.Contains("focus r" + ch[2].Id, r.App.Current.Describe());       // the third takes its place
        }

        [Fact]
        public void AChannelEntersRecentsOnlyAfterMoreThanFiveMinutesOfRealPlayback()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            var ch = r.Catalog.Channels[0];
            r.App.Push(new PlayerScreen(new PlayContext(new[] { ch }, 0)));
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 0.5);
            Assert.Empty(r.Recents.Items);                                            // opening it is not enough
            r.Advance(r.Now + 120); Assert.Empty(r.Recents.Items);
            r.Advance(r.Now + 150); Assert.Empty(r.Recents.Items);                    // ~4.5 min
            r.Advance(r.Now + 60);
            Assert.Single(r.Recents.Items);
            Assert.Equal(ch.Id, r.Recents.Items[0].Id);
        }

        [Fact]
        public void ZappingBeforeFiveMinutesDoesNotAddAnything()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            var list = r.Catalog.Categories.First().Channels;
            r.App.Push(new PlayerScreen(new PlayContext(list, 0)));
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 200);
            r.Platform.PressKey(UiKey.ChannelUp); r.Advance(r.Now + 0.4);              // channel change: new counter
            r.Playback.Created[1].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; });
            r.Advance(r.Now + 200);
            Assert.Empty(r.Recents.Items);
            r.Advance(r.Now + 110);
            Assert.Single(r.Recents.Items);
            Assert.Equal(list[1].Id, r.Recents.Items[0].Id);
        }
    }
}
