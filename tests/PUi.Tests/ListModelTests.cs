using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Core;
using PUi.Demo;
using Xunit;

namespace PUi.Tests
{
    /// <summary>Rules of the channel lists (category, Explore, results): empty at the start, stable arrival order, four sort modes.</summary>
    public class ChannelListModelTests
    {
        private static (ChannelListModel m, FakeChecker chk, FakeCatalog cat) Make(int n = 6)
        {
            var cat = new FakeCatalog();
            for (int i = 0; i < n; i++) cat.Add("Cat", "Canal " + (char)('A' + i));
            var chk = new FakeChecker { Default = LinkState.Checking };
            var m = new ChannelListModel();
            m.SetChannels(cat.Channels);
            return (m, chk, cat);
        }

        private static List<string> Names(ChannelListModel m) { return m.Visible.Select(r => r.Channel.Name.Replace("Canal ", "")).ToList(); }

        // ------------------------------------------------------------------ arrival
        [Fact]
        public void WithOnlyLiveTheListStartsEmptyWhileEverythingIsBeingChecked()
        {
            var (m, chk, cat) = Make();
            m.Refresh(chk, true, 0);
            Assert.Empty(m.Visible);
            Assert.Equal(6, m.Total);
            Assert.Equal(0, m.Done);
            Assert.Equal(0, m.Hidden);
        }

        [Fact]
        public void LiveChannelsEnterAtTheEndInTheOrderTheyAreDiscovered()
        {
            var (m, chk, cat) = Make();
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[3], LinkState.Good, 10); m.Refresh(chk, true, 1);
            Assert.Equal(new[] { "D" }, Names(m));
            chk.Set(cat.Channels[0], LinkState.Good, 99); m.Refresh(chk, true, 2);
            chk.Set(cat.Channels[5], LinkState.Good, 1); m.Refresh(chk, true, 3);
            chk.Set(cat.Channels[1], LinkState.Good, 50); m.Refresh(chk, true, 4);
            Assert.Equal(new[] { "D", "A", "F", "B" }, Names(m));     // neither alphabetical, nor by peers, nor the list's
            Assert.Equal(new[] { 0, 1, 2, 3 }, m.Visible.Select(r => r.Seq).ToArray());
        }

        [Fact]
        public void ExistingRowsNeverMoveWhenMoreArrive()
        {
            var (m, chk, cat) = Make(8);
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[4], LinkState.Good, 5); m.Refresh(chk, true, 0.5); chk.Set(cat.Channels[2], LinkState.Good, 9); m.Refresh(chk, true, 1);
            var first = m.Visible[0]; var second = m.Visible[1];
            float y0 = first.Item.R.Top, y1 = second.Item.R.Top;
            for (int i = 0; i < 8; i++)
            {
                if (i == 4 || i == 2) continue;
                chk.Set(cat.Channels[i], i % 3 == 0 ? LinkState.Dead : LinkState.Good, 1000 - i);
                m.Refresh(chk, true, 2 + i);
                Assert.Equal(y0, first.Item.R.Top); Assert.Equal(y1, second.Item.R.Top);
                Assert.Equal(y0, first.TargetY); Assert.False(first.Y.Active(2.5 + i));      // nor is it animated
                Assert.Same(first, m.Visible[0]); Assert.Same(second, m.Visible[1]);
            }
            Assert.Equal("E", Names(m)[0]); Assert.Equal("C", Names(m)[1]);
        }

        [Fact]
        public void NewRowAppearsInPlaceWithoutJumpingAndNeverBeforeItIsReady()
        {
            var (m, chk, cat) = Make(3);
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[1], LinkState.Good, 3); m.Refresh(chk, true, 10);
            var row = m.Visible[0];
            Assert.Equal(m.Top, row.Item.R.Top);
            Assert.Equal(row.Item.R.Top, row.Y.Value(10), 3);                  // it is already in its place, it does not travel
            Assert.Equal(0.0, (double)row.Appear.Value(10), 3);                // and it starts transparent
            Assert.Equal(0.0, (double)row.Appear.Value(10.04), 2);             // a couple of frames with the texture still unseen
            double prev = -1;
            for (double t = 10.05; t <= 10.5; t += 0.02) { double v = row.Appear.Value(t); Assert.True(v >= prev - 1e-9); prev = v; }     // monotonic
            Assert.Equal(1.0, (double)row.Appear.Value(11), 3);
        }

        [Fact]
        public void CheckingAndNoSignalChannelsNeverEnterButNoSignalOnesAreCounted()
        {
            var (m, chk, cat) = Make();
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[2], LinkState.Dead); chk.Set(cat.Channels[4], LinkState.Unknown); chk.Set(cat.Channels[1], LinkState.Good, 3);
            m.Refresh(chk, true, 1);
            Assert.Equal(new[] { "B" }, Names(m));
            Assert.Equal(2, m.Hidden);
            Assert.Equal(3, m.Done);
        }

        [Fact]
        public void ARowThatStopsBeingLiveFadesOutAndTheOthersCloseTheGapKeepingTheirOrder()
        {
            var (m, chk, cat) = Make(4);
            m.Refresh(chk, true, 0);
            foreach (int i in new[] { 2, 0, 3, 1 }) { chk.Set(cat.Channels[i], LinkState.Good, 10); m.Refresh(chk, true, 1 + i); }
            Assert.Equal(new[] { "C", "A", "D", "B" }, Names(m));
            var a = m.Visible[1]; var d = m.Visible[2];
            float yd = d.Item.R.Top;
            chk.Set(cat.Channels[0], LinkState.Dead); m.Refresh(chk, true, 10);
            Assert.Equal(new[] { "C", "D", "B" }, Names(m));
            Assert.Single(m.Fading); Assert.Same(a, m.Fading[0]);
            Assert.True(d.Item.R.Top < yd);                                // it moves up to take the gap...
            Assert.Equal(yd, d.Y.Value(10), 2);                            // ...animated
            Assert.True(m.Animating(10.1));
            m.Refresh(chk, true, 12);
            Assert.Empty(m.Fading);
            Assert.Equal(1, m.Hidden);
        }

        [Fact]
        public void AChannelThatReturnsToLifeEntersAgainAtTheEnd()
        {
            var (m, chk, cat) = Make(3);
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[0], LinkState.Good, 5); chk.Set(cat.Channels[1], LinkState.Good, 5); m.Refresh(chk, true, 1);
            chk.Set(cat.Channels[0], LinkState.Dead); m.Refresh(chk, true, 2);
            chk.Set(cat.Channels[0], LinkState.Good, 5); m.Refresh(chk, true, 3);
            Assert.Equal(new[] { "B", "A" }, Names(m));
        }

        [Fact]
        public void WithoutOnlyLiveTheUnverifiedOnesComeAfterTheWholeCheckAtTheEndAndWithZero()
        {
            var (m, chk, cat) = Make(5);
            m.Refresh(chk, false, 0);
            Assert.Empty(m.Visible);                                                     // nothing to show until something arrives
            chk.Set(cat.Channels[3], LinkState.Good, 80); chk.Set(cat.Channels[1], LinkState.Dead); chk.Set(cat.Channels[4], LinkState.Good, 3);
            m.Refresh(chk, false, 1);
            Assert.Equal(new[] { "D", "E" }, Names(m));                                  // the live ones come in as they arrive; B is already negative but A and C are still being checked
            var before = m.Visible.Select(r => r.Item.R.Top).ToArray();
            chk.Set(cat.Channels[0], LinkState.Unknown); chk.Set(cat.Channels[2], LinkState.Unknown);
            m.Refresh(chk, false, 2);
            Assert.Equal(new[] { "D", "E", "A", "B", "C" }, Names(m));                   // once the checking is finished the others come out, at the end
            Assert.Equal(before, m.Visible.Take(2).Select(r => r.Item.R.Top).ToArray()); // without those already there moving
            Assert.All(m.Visible, r => Assert.False(r.Y.Active(2.1)));
            Assert.Equal(0, m.Hidden);
            Assert.Equal(Signal.NoSignal, m.Visible[3].View.State); Assert.Equal("0", m.Visible[3].View.Peers);
            Assert.Equal(Signal.Live, m.Visible[0].View.State); Assert.Equal("80", m.Visible[0].View.Peers);
            Assert.Empty(m.Fading);
        }

        [Fact]
        public void WithoutOnlyLiveAListedUnverifiedChannelIsNotRemovedWhenMoreChannelsStartChecking()
        {
            var (m, chk, cat) = Make(2);
            chk.Set(cat.Channels[0], LinkState.Good, 5); chk.Set(cat.Channels[1], LinkState.Unknown);
            m.Refresh(chk, false, 0);
            Assert.Equal(new[] { "A", "B" }, Names(m));
            var more = new FakeCatalog(); more.Add("Cat", "Canal A"); more.Add("Cat", "Canal B"); more.Add("Cat", "Canal C");
            m.SetChannels(more.Channels);                                                 // remote results arrive: a new one, being checked
            chk.Set(more.Channels[0], LinkState.Good, 5); chk.Set(more.Channels[1], LinkState.Unknown);
            m.Refresh(chk, false, 1);
            Assert.Equal(new[] { "A", "B" }, Names(m));
        }

        [Fact]
        public void TurningOnlyLiveOnAfterwardsListsOnlyTheLiveOnesAndTurningItOffListsAll()
        {
            var (m, chk, cat) = Make(4);
            chk.Set(cat.Channels[2], LinkState.Good, 7); chk.Set(cat.Channels[0], LinkState.Dead); chk.Set(cat.Channels[1], LinkState.Dead); chk.Set(cat.Channels[3], LinkState.Unknown);
            m.Refresh(chk, false, 0);
            Assert.Equal(4, m.Visible.Count);
            m.Refresh(chk, true, 1);
            Assert.Equal(new[] { "C" }, Names(m));
            m.Refresh(chk, false, 2);
            Assert.Equal(4, m.Visible.Count);
        }

        [Fact]
        public void ProgressCountsFinishedChannelsOfTheBatch()
        {
            var (m, chk, cat) = Make(4);
            m.Refresh(chk, true, 0);
            Assert.Equal(0, m.Done); Assert.Equal(4, m.Total);
            chk.Set(cat.Channels[0], LinkState.Good, 5); chk.Set(cat.Channels[1], LinkState.Unknown);
            m.Refresh(chk, true, 1);
            Assert.Equal(2, m.Done);
        }

        [Fact]
        public void RowsKeepTheirIdentity()
        {
            var (m, chk, cat) = Make(3);
            m.Refresh(chk, true, 0);
            chk.Set(cat.Channels[1], LinkState.Good, 5); m.Refresh(chk, true, 1);
            var item = m.Visible[0].Item;
            chk.Set(cat.Channels[0], LinkState.Good, 50); m.Refresh(chk, true, 2);
            Assert.Same(item, m.Visible[0].Item);
            Assert.Equal(cat.Channels[1].Id, item.Id);
        }

        // ------------------------------------------------------------------ order
        private static (ChannelListModel m, FakeChecker chk, FakeCatalog cat) LiveSet()
        {
            var cat = new FakeCatalog();
            foreach (var n in new[] { "Teledeporte", "Álbum TV", "Zeta", "Eurosport", "Antena 3", "DAZN" }) cat.Add("Cat", n);
            var chk = new FakeChecker { Default = LinkState.Checking };
            var m = new ChannelListModel();
            m.SetChannels(cat.Channels);
            int[] peers = { 10, 40, 3, 40, 700, 25 };
            int[] arrival = { 3, 0, 5, 1, 4, 2 };         // order in which they are discovered: Album(1), Eurosport(3), DAZN(5), Teledeporte(0), Antena 3(4), Zeta(2)
            m.Refresh(chk, true, 0);
            double t = 1;
            foreach (var idx in arrival.Select((pos, i) => (pos, i)).OrderBy(p => p.pos).Select(p => p.i))
            { chk.Set(cat.Channels[idx], LinkState.Good, peers[idx]); m.Refresh(chk, true, t++); }
            return (m, chk, cat);
        }

        private static List<string> Full(ChannelListModel m) { return m.Visible.Select(r => r.Channel.Name).ToList(); }

        [Fact]
        public void ArrivalIsTheDefaultOrder()
        {
            var (m, chk, cat) = LiveSet();
            Assert.Equal(new[] { "Álbum TV", "Eurosport", "DAZN", "Teledeporte", "Antena 3", "Zeta" }, Full(m));
        }

        [Fact]
        public void AlphabeticalAscendingIgnoresAccentsAndCase()
        {
            var (m, chk, cat) = LiveSet();
            m.Refresh(chk, true, SortMode.AlphaAsc, 20);
            Assert.Equal(new[] { "Álbum TV", "Antena 3", "DAZN", "Eurosport", "Teledeporte", "Zeta" }, Full(m));
        }

        [Fact]
        public void AlphabeticalDescendingIsTheExactReverse()
        {
            var (m, chk, cat) = LiveSet();
            m.Refresh(chk, true, SortMode.AlphaAsc, 20); var asc = Full(m);
            m.Refresh(chk, true, SortMode.AlphaDesc, 21);
            Assert.Equal(new[] { "Zeta", "Teledeporte", "Eurosport", "DAZN", "Antena 3", "Álbum TV" }, Full(m));
            asc.Reverse(); Assert.Equal(asc, Full(m));
        }

        [Fact]
        public void MostPeersFirstBreaksTiesByArrival()
        {
            var (m, chk, cat) = LiveSet();
            m.Refresh(chk, true, SortMode.PeersDesc, 20);
            Assert.Equal(new[] { "Antena 3", "Álbum TV", "Eurosport", "DAZN", "Teledeporte", "Zeta" }, Full(m));       // 700, 40, 40 (Album arrived first), 25, 10, 3
        }

        [Fact]
        public void PeersModeKeepsLiveOnesFirstWhenNotOnlyLive()
        {
            var cat = new FakeCatalog();
            foreach (var n in new[] { "A", "B", "C", "D" }) cat.Add("Cat", n);
            var chk = new FakeChecker { Default = LinkState.Checking };
            var m = new ChannelListModel(); m.SetChannels(cat.Channels);
            chk.Set(cat.Channels[1], LinkState.Good, 5); chk.Set(cat.Channels[3], LinkState.Good, 50); chk.Set(cat.Channels[0], LinkState.Dead);
            m.Refresh(chk, false, SortMode.PeersDesc, 0);
            Assert.Equal(new[] { "D", "B" }, Full(m));                  // C is still being checked: for now only the live ones
            chk.Set(cat.Channels[2], LinkState.Unknown);
            m.Refresh(chk, false, SortMode.PeersDesc, 1);
            Assert.Equal(new[] { "D", "B", "A", "C" }, Full(m));        // live ones by peers; then the rest in their arrival order
        }

        [Fact]
        public void FourCyclesGoBackToArrivalAndRestoreTheDiscoveryOrder()
        {
            var (m, chk, cat) = LiveSet();
            var arrival = Full(m);
            var seen = new List<List<string>>();
            double t = 30;
            for (int i = 1; i <= 4; i++) { m.Refresh(chk, true, (SortMode)(i % 4), t); t += 1; seen.Add(Full(m)); }
            Assert.NotEqual(arrival, seen[0]); Assert.NotEqual(seen[0], seen[1]); Assert.NotEqual(seen[1], seen[2]);
            Assert.Equal(arrival, seen[3]);
        }

        [Fact]
        public void ChangingTheOrderAnimatesTheRowsForTheStandardReorderTimeAndKeepsTheirIdentity()
        {
            var (m, chk, cat) = LiveSet();
            var items = m.Visible.ToDictionary(r => r.Channel.Id, r => r.Item);
            var moved = m.Visible.First(r => r.Channel.Name == "Teledeporte");
            float y0 = moved.Item.R.Top;
            m.Refresh(chk, true, SortMode.AlphaAsc, 20);
            Assert.True(moved.Item.R.Top > y0);                         // moves down from the fourth to the fifth place
            Assert.Equal(y0, moved.Y.Value(20), 2);                     // it starts where it was...
            Assert.True(moved.Y.Active(20.1));
            Assert.Equal(moved.TargetY, moved.Y.Value(20.4), 1);        // ...and arrives in 0.3 s
            Assert.All(m.Visible, r => Assert.Same(items[r.Channel.Id], r.Item));
            Assert.Empty(m.Fading);
        }

        [Fact]
        public void ChangingTheOrderDoesNotRestartTheEntryAnimation()
        {
            var (m, chk, cat) = LiveSet();
            m.Refresh(chk, true, 40);
            Assert.All(m.Visible, r => Assert.Equal(1.0, (double)r.Appear.Value(41), 3));
            m.Refresh(chk, true, SortMode.PeersDesc, 41);
            Assert.All(m.Visible, r => Assert.Equal(1.0, (double)r.Appear.Value(41.01), 3));
        }

        [Fact]
        public void AChannelArrivingInAlphabeticalModeTakesItsAlphabeticalPlace()
        {
            var cat = new FakeCatalog();
            foreach (var n in new[] { "Delta", "Alfa", "Charlie", "Bravo" }) cat.Add("Cat", n);
            var chk = new FakeChecker { Default = LinkState.Checking };
            var m = new ChannelListModel(); m.SetChannels(cat.Channels);
            m.Refresh(chk, true, SortMode.AlphaAsc, 0);
            chk.Set(cat.Channels[0], LinkState.Good, 1); m.Refresh(chk, true, SortMode.AlphaAsc, 1);
            chk.Set(cat.Channels[2], LinkState.Good, 1); m.Refresh(chk, true, SortMode.AlphaAsc, 2);
            chk.Set(cat.Channels[1], LinkState.Good, 1); m.Refresh(chk, true, SortMode.AlphaAsc, 3);
            Assert.Equal(new[] { "Alfa", "Charlie", "Delta" }, Full(m));
            m.Refresh(chk, true, SortMode.Arrival, 4);
            Assert.Equal(new[] { "Delta", "Charlie", "Alfa" }, Full(m));
        }

        [Fact]
        public void SortKeyIsCaseAndAccentInsensitive()
        {
            Assert.Equal(ChannelListModel.SortKey("ÁLBUM"), ChannelListModel.SortKey("album"));
            Assert.Equal("cine+ ñoño", ChannelListModel.SortKey("CINÉ+ ÑOÑO"));
        }
    }
}
