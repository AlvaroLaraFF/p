using Rig = AceEngine.Tests.Timeshift.TsRig.Rig;
using System;
using System.Threading.Tasks;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>PlaybackSession with an in-memory live stream: rewind, +/-10 s and LIVE over the whole engine and a fake player.</summary>
    [Collection("Timeshift e2e")]
    public class TimeshiftSeekTests
    {
        private const long Mib = TsRig.Mib;

        private static Task<bool> Until(Func<bool> c, int ms) { return TsRig.Until(c, ms); }
        private static long BackOf(string url) { return TsRig.BackOf(url); }

        [Fact]
        public async Task SeeksReopenThePlayerOnTheSameEngineSessionAndNeverCountAsFailures()
        {
            // MaxLiveRetries = 0: if a seek counted as a player failure, playback would end in error
            using (var r = await Rig.StartAsync(4 * Mib, o => o.MaxLiveRetries = 0))
            {
                await r.WaitWindowAsync(18);
                var session = r.Pb.EngineSession;

                // -10 s: ?back=10000, same engine, the old player is released, "preparando" in the meantime
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.Equal(2, r.Count);
                Assert.Equal(10000, BackOf(r.At(1).Url));
                Assert.Same(session, r.Pb.EngineSession);
                Assert.True(r.At(0).Disposed);
                Assert.Contains(PlayPhase.Preparing, r.PhasesCopy());
                await Task.Delay(600);
                Assert.InRange(r.Pb.BehindLive.TotalSeconds, 9.5, 14.5);
                Assert.InRange(r.Pb.Status.BehindLiveMs, 9500, 14500);
                // the new player receives a continuous stream from a random access, with PAT/PMT in front
                Assert.True(await Until(() => r.At(1).BytesRead > 512 * 1024, 15000));
                Assert.Equal(0, r.At(1).Verifier.CorruptPackets);
                Assert.Equal(0, r.At(1).Verifier.SyncErrors);
                Assert.Empty(r.At(1).Verifier.Gaps);

                // forward until ~1 s from the live stream (< 2 s) = LIVE
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromMilliseconds(r.Pb.BehindLive.TotalMilliseconds - 1000));
                Assert.Equal(3, r.Count);
                Assert.DoesNotContain("back=", r.At(2).Url);
                await Task.Delay(400);
                Assert.Equal(TimeSpan.Zero, r.Pb.BehindLive);

                // two steps back and one forward: each seek is relative to what the UI shows
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-5));
                Assert.Equal(4, r.Count);
                Assert.Equal(5000, BackOf(r.At(3).Url));
                await Task.Delay(300);
                double shown = r.Pb.BehindLive.TotalMilliseconds;
                Assert.InRange(shown, 5000, 10000);
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-5));
                long b5 = BackOf(r.At(4).Url);
                Assert.InRange(b5, shown + 4000, shown + 6000);
                await Task.Delay(300);
                shown = r.Pb.BehindLive.TotalMilliseconds;
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(5));
                long b6 = BackOf(r.At(5).Url);
                Assert.InRange(b6, shown - 5800, shown - 4200);

                // LIVE from behind
                await r.Pb.GoLiveAsync();
                Assert.Equal(7, r.Count);
                Assert.DoesNotContain("back=", r.At(6).Url);
                await Task.Delay(400);
                Assert.Equal(TimeSpan.Zero, r.Pb.BehindLive);

                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.Equal(0, r.Pb.Status.Retry);
                Assert.Null(r.Pb.Status.Error);
                Assert.DoesNotContain(PlayPhase.Error, r.PhasesCopy());
                Assert.DoesNotContain(PlayPhase.Retrying, r.PhasesCopy());
            }
        }

        [Fact]
        public async Task SeekIsClippedToTheWindowAndStopsAtItsEnd()
        {
            using (var r = await Rig.StartAsync(2 * Mib))                                       // 16 s stored, ~12 s of window (live delay of 4 s)
            {
                await r.WaitWindowAsync(11.3);
                long maxWindowMs = (long)r.Pb.TimeshiftWindow.TotalMilliseconds;
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-60));
                Assert.Equal(2, r.Count);
                long back = BackOf(r.At(1).Url);
                maxWindowMs = Math.Max(maxWindowMs, (long)r.Pb.TimeshiftWindow.TotalMilliseconds);
                Assert.InRange(back, 4000, maxWindowMs - 2000);                                 // never further back than what is stored, and with the margin against eviction
                { var st = r.Pb.Status; Assert.True(st.BehindLiveMs <= st.TimeshiftWindowMs, st.BehindLiveMs + " > " + st.TimeshiftWindowMs); }
                // at the window's limit another step back does nothing or, at most, adjusts (the window changes by one block): it never passes the limit nor gets cut
                for (int i = 0; i < 3; i++)
                {
                    await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                    maxWindowMs = Math.Max(maxWindowMs, (long)r.Pb.TimeshiftWindow.TotalMilliseconds);
                }
                Assert.True(r.Count <= 4, "reaperturas: " + r.Count);
                Assert.True(BackOf(r.F.Last.Url) >= 4000 && BackOf(r.F.Last.Url) <= maxWindowMs - 2000, "back " + BackOf(r.F.Last.Url) + " (" + r.F.Last.Url + ") window max " + maxWindowMs + " reopenings " + r.Count);
                // and playback goes on without cuts (the cursor is not reached by the eviction)
                await Task.Delay(2000);
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.False(r.F.Last.Ended);
                Assert.Equal(0, r.Pb.Status.Retry);
            }
        }

        [Fact]
        public async Task GoLiveDoesNothingIfAlreadyLiveAndWorksFromAPauseAndSeekWhilePausedPlays()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.WaitWindowAsync(16);
                await r.Pb.GoLiveAsync();                                                       // already live: nothing
                Assert.Equal(1, r.Count);
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(5));                       // forward while live: nothing
                Assert.Equal(1, r.Count);

                r.Pb.Pause();
                await Task.Delay(2500);                                                         // 10 s back
                Assert.True(r.Pb.BehindLive.TotalSeconds > 5);
                await r.Pb.GoLiveAsync();
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.Equal(2, r.Count);
                Assert.DoesNotContain("back=", r.At(1).Url);
                Assert.False(r.At(1).IsPaused);
                await Task.Delay(500);
                Assert.Equal(TimeSpan.Zero, r.Pb.BehindLive);

                // a seek while paused plays from the new position
                r.Pb.Pause();
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-8));
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.Equal(3, r.Count);
                Assert.InRange(BackOf(r.At(2).Url), 8000, 14000);                               // 8 s more than what had already been lagged during the pause
                Assert.Equal(0, r.Pb.Status.Retry);
            }
        }

        [Fact]
        public async Task QuickConsecutiveSeeksAccumulateIntoOneReopen()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.WaitWindowAsync(22);
                var t1 = r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-5));
                var t2 = r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-5));
                var t3 = r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-5));
                await Task.WhenAll(t1, t2, t3);
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.True(r.Count <= 3, "too many reopenings: " + r.Count);
                Assert.Equal(15000, BackOf(r.F.Last.Url));
                Assert.Equal(0, r.Pb.Status.Retry);
            }
        }

        [Fact]
        public async Task APlayerErrorWhileBehindReopensAtTheLiveLikeToday()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.WaitWindowAsync(16);
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                Assert.Equal(2, r.Count);
                r.At(1).RaiseFailure(new PlayerFailure("error del reproductor: ConnectionFailed"));
                Assert.True(await Until(() => r.Count == 3 && r.Pb.Status.Phase == PlayPhase.Playing, 15000), "no se recupero: " + r.Pb.Status);
                Assert.DoesNotContain("back=", r.At(2).Url);
                Assert.Equal(1, r.Pb.Status.Retry);                                             // this one is a failure and spends a retry
            }
        }
    }
}
