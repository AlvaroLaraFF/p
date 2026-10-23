using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.P2P;
using Xunit;

namespace AceEngine.Tests.P2P
{
    /// <summary>Streaming piece picker: prebuffer / high / mid / low priorities.</summary>
    public class PickerTests
    {
        private sealed class Ctx : IPickerContext
        {
            public HashSet<int> Have = new HashSet<int>();
            public HashSet<int> PeerHasSet = new HashSet<int>();
            public HashSet<int> NotRequestable = new HashSet<int>();
            public Dictionary<int, int> Avail = new Dictionary<int, int>();
            public HashSet<int> FastHas = new HashSet<int>();
            bool IPickerContext.Have(int p) { return Have.Contains(p); }
            bool IPickerContext.PeerHas(int p) { return PeerHasSet.Contains(p); }
            bool IPickerContext.Requestable(int p) { return !NotRequestable.Contains(p); }
            int IPickerContext.Availability(int p) { int a; return Avail.TryGetValue(p, out a) ? a : 1; }
            bool IPickerContext.FastPeerHas(int p) { return FastHas.Contains(p); }
            public static Ctx PeerHasAll(int n) { var c = new Ctx(); for (int i = 0; i < n; i++) c.PeerHasSet.Add(i); return c; }
        }

        private static PickerWindow Win(int first, int last, int play, int high, int mid, int ahead, bool prebuf = false)
        {
            return new PickerWindow
            {
                FirstPiece = first, LastPiece = last, PlaybackPiece = play,
                HighEnd = Math.Min(last + 1, play + high),
                MidEnd = Math.Min(last + 1, play + high + mid),
                WindowEnd = Math.Min(last + 1, play + ahead),
                Prebuffering = prebuf
            };
        }

        [Fact]
        public void HighRangeIsStrictlySequentialFromPlaybackPosition()
        {
            var ctx = Ctx.PeerHasAll(100);
            ctx.Have.Add(10); ctx.Have.Add(11);
            var w = Win(0, 99, 10, 5, 10, 40);
            PickTier tier;
            Assert.Equal(12, PiecePickerVod.Pick(w, ctx, true, out tier));
            Assert.Equal(PickTier.High, tier);
            ctx.Have.Add(12);
            Assert.Equal(13, PiecePickerVod.Pick(w, ctx, true, out tier));
        }

        [Fact]
        public void HighSkipsPiecesThePeerLacksAndTheNonRequestable()
        {
            var ctx = Ctx.PeerHasAll(100);
            ctx.PeerHasSet.Remove(10);
            ctx.NotRequestable.Add(11);
            var w = Win(0, 99, 10, 5, 10, 40);
            PickTier tier;
            Assert.Equal(12, PiecePickerVod.Pick(w, ctx, true, out tier));
        }

        [Fact]
        public void MidRangePicksRarestAndBreaksTiesRandomly()
        {
            var ctx = Ctx.PeerHasAll(100);
            for (int p = 10; p < 15; p++) ctx.Have.Add(p);    // high complete
            var w = Win(0, 99, 10, 5, 10, 40);
            for (int p = 15; p < 25; p++) ctx.Avail[p] = 5;
            ctx.Avail[19] = 1;
            PickTier tier;
            Assert.Equal(19, PiecePickerVod.Pick(w, ctx, true, out tier));
            Assert.Equal(PickTier.Mid, tier);

            ctx.Avail[19] = 5;
            ctx.Avail[17] = 2; ctx.Avail[21] = 2;
            var seen = new HashSet<int>();
            for (int i = 0; i < 200; i++) seen.Add(PiecePickerVod.Pick(w, ctx, true, out tier));
            Assert.Equal(new HashSet<int> { 17, 21 }, seen);
        }

        [Fact]
        public void LowRangeComesAfterMidAndStopsAtWindowEnd()
        {
            var ctx = Ctx.PeerHasAll(100);
            for (int p = 10; p < 25; p++) ctx.Have.Add(p);    // high + mid complete
            var w = Win(0, 99, 10, 5, 10, 20);                 // window up to 30
            PickTier tier;
            int c = PiecePickerVod.Pick(w, ctx, true, out tier);
            Assert.InRange(c, 25, 29);
            Assert.Equal(PickTier.Low, tier);
            for (int p = 25; p < 30; p++) ctx.Have.Add(p);
            Assert.Equal(-1, PiecePickerVod.Pick(w, ctx, true, out tier));   // nothing outside the window (bounded memory)
        }

        [Fact]
        public void NothingBeforePlaybackPosition()
        {
            var ctx = Ctx.PeerHasAll(100);
            var w = Win(0, 99, 50, 5, 10, 20);
            PickTier tier;
            for (int i = 0; i < 100; i++) Assert.InRange(PiecePickerVod.Pick(w, ctx, true, out tier), 50, 69);
        }

        [Fact]
        public void PrebufferWinsAndPicksRarestAmongNeeded()
        {
            var ctx = Ctx.PeerHasAll(100);
            var w = Win(0, 99, 0, 5, 10, 30, prebuf: true);
            w.PrebufNeeded = new List<int> { 0, 1, 2, 3, 4, 5, 99 };
            w.PrebufHighPriority = new List<int> { 0, 1, 99 };
            ctx.Avail[99] = 1; ctx.Avail[0] = 3; ctx.Avail[1] = 3;
            PickTier tier;
            Assert.Equal(99, PiecePickerVod.Pick(w, ctx, true, out tier));
            Assert.Equal(PickTier.Prebuffer, tier);
            ctx.Have.Add(99); ctx.Have.Add(0); ctx.Have.Add(1);
            ctx.Avail[2] = 3; ctx.Avail[3] = 3; ctx.Avail[5] = 3; ctx.Avail[4] = 1;
            Assert.Equal(4, PiecePickerVod.Pick(w, ctx, true, out tier));
            for (int p = 2; p <= 5; p++) ctx.Have.Add(p);
            w.Prebuffering = false;                        // prebuffer done: back to the high sequence
            w.HighEnd = 12; w.MidEnd = 20;
            Assert.Equal(6, PiecePickerVod.Pick(w, ctx, true, out tier));
            Assert.Equal(PickTier.High, tier);
        }

        [Fact]
        public void SlowPeersOnlyGetLowPieces()
        {
            var ctx = Ctx.PeerHasAll(100);
            var w = Win(0, 99, 10, 5, 10, 40);
            PickTier tier;
            int c = PiecePickerVod.Pick(w, ctx, false, out tier);
            Assert.Equal(PickTier.Low, tier);
            Assert.InRange(c, 25, 49);
        }

        [Fact]
        public void SlowPeerGetsHighPieceWhenNoFastPeerHasIt()
        {
            var ctx = new Ctx();
            ctx.PeerHasSet.Add(11);             // the slow peer only has 11 (which is high)
            var w = Win(0, 99, 10, 5, 10, 40);
            PickTier tier;
            Assert.Equal(-1, PiecePickerVod.Pick(w, new Ctx { PeerHasSet = { 11 }, FastHas = { 11 } }, false, out tier));
            Assert.Equal(11, PiecePickerVod.Pick(w, ctx, false, out tier));
            Assert.Equal(PickTier.High, tier);
        }

        [Fact]
        public void LastPieceBoundsAreRespected()
        {
            var ctx = Ctx.PeerHasAll(20);
            var w = Win(5, 14, 12, 5, 10, 40);        // file = pieces 5..14
            PickTier tier;
            var picks = new HashSet<int>();
            for (int i = 0; i < 100; i++) { int c = PiecePickerVod.Pick(w, ctx, true, out tier); if (c >= 0) picks.Add(c); }
            Assert.All(picks, p => Assert.InRange(p, 12, 14));
        }
    }
}
