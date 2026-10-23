using System;
using System.Threading;
using AceEngine.P2P;
using Xunit;

namespace AceEngine.Tests.P2P
{
    /// <summary>
    /// The download window follows the last reader created or the one that has read most recently. With two player connections
    /// (data + probe of the end of the file) they must not fight over the window.
    /// </summary>
    public class ReaderOwnershipTests
    {
        private static Swarm NewSwarm(TestTorrent t)
        {
            var opt = new EngineOptions { EnableDht = false, EnableTrackers = false, EnableListener = false, KeepBehindBytes = 64 * 1024 };
            return new Swarm(opt, t.InfoHash, t.Transport.Torrent, null, -1, null, null, null);
        }

        [Fact]
        public void NewReaderTakesOverAndOldOneGetsItBackWhenTheNewOneLeaves()
        {
            var t = TestTorrent.Create(100 * 16384, 16384, seed: 30);
            using (var sw = NewSwarm(t))
            {
                var a = new object(); var b = new object();
                sw.RegisterReader(a, 0);
                Assert.Equal(0, sw.PlaybackPiece);
                sw.ReaderMoved(a, 16384L * 5, true);
                Assert.Equal(5, sw.PlaybackPiece);

                sw.RegisterReader(b, 16384L * 95);                 // probe of the end: takes control (it is a "jump")
                Assert.Equal(95, sw.PlaybackPiece);

                sw.ReaderMoved(a, 16384L * 6, true);               // A keeps reading but B is the owner and is active
                Assert.Equal(95, sw.PlaybackPiece);
                sw.ReaderMoved(a, 16384L * 6, false);              // A blocked waiting: does not move it either
                Assert.Equal(95, sw.PlaybackPiece);

                sw.UnregisterReader(b);                            // B finishes: the window goes back to A, where it was
                Assert.Equal(6, sw.PlaybackPiece);
            }
        }

        [Fact]
        public void PrebufferIsAbandonedWhenThePlayerJumpsBeforeItEnds()
        {
            var t = TestTorrent.Create(100 * 16384, 16384, seed: 33);
            using (var sw = NewSwarm(t))
            {
                Assert.True(sw.PrebufferActive);
                var a = new object();
                sw.RegisterReader(a, 0);
                sw.ReaderMoved(a, 16384L * 2, true);
                Assert.True(sw.PrebufferActive);                  // normal advance: the prebuffer follows
                sw.RegisterReader(new object(), 16384L * 95);     // jump (more than 1 MiB) before finishing
                Assert.False(sw.PrebufferActive);
            }
            using (var sw = NewSwarm(t))
            {
                sw.RegisterReader(new object(), 16384L * 50);     // the first reader can start wherever it wants
                Assert.True(sw.PrebufferActive);
            }
        }

        [Fact]
        public void LastReaderOnlyLosesOwnershipToActiveOnes()
        {
            var t = TestTorrent.Create(100 * 16384, 16384, seed: 31);
            using (var sw = NewSwarm(t))
            {
                var a = new object();
                sw.RegisterReader(a, 16384L * 40);
                sw.UnregisterReader(a);
                Assert.Equal(40, sw.PlaybackPiece);                // with no readers it stays where it was
                var b = new object();
                sw.RegisterReader(b, 16384L * 10);
                Assert.Equal(10, sw.PlaybackPiece);
            }
        }

        [Fact]
        public void ForwardPlaybackTrimsPiecesBehindButSeeksKeepThem()
        {
            var t = TestTorrent.Create(100 * 16384, 16384, seed: 32);
            using (var sw = NewSwarm(t))
            {
                for (int p = 0; p < 20; p++) sw.InjectVerifiedPiece(p, t.Piece(p));
                var a = new object();
                sw.RegisterReader(a, 0);
                // continuous playback piece by piece: whatever is more than KeepBehind (4 pieces) behind is released
                for (int p = 1; p <= 12; p++) sw.ReaderMoved(a, 16384L * p, true);
                Assert.False(sw.IsPieceStored(0));
                Assert.False(sw.IsPieceStored(7));
                Assert.True(sw.IsPieceStored(8));
                Assert.True(sw.IsPieceStored(19));

                // a jump far away (another connection / the user seeks) does not discard what has already been downloaded
                var b = new object();
                sw.RegisterReader(b, 16384L * 90);
                Assert.True(sw.IsPieceStored(8));
                Assert.True(sw.IsPieceStored(19));
            }
        }
    }
}
