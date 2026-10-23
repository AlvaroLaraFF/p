using Rig = AceEngine.Tests.Timeshift.TsRig.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>PlaybackSession with an in-memory live stream: pause and resume over the whole engine and a fake player.</summary>
    [Collection(AceEngine.Tests.RealtimeCollection.Name)]
    public class TimeshiftPlaybackTests
    {
        private const long Mib = TsRig.Mib;
        private const double TimeScale = TsRig.TimeScale;

        private static Task<bool> Until(Func<bool> c, int ms) { return TsRig.Until(c, ms); }
        private static long BackOf(string url) { return TsRig.BackOf(url); }

        [Fact]
        public async Task LiveWithMemoryPlaysAtLiveWithAGrowingWindowAndNoFlicker()
        {
            using (var r = await Rig.StartAsync(4 * Mib, settle: false))
            {
                Assert.True(r.Pb.Status.IsLive);
                Assert.Equal(0, r.Pb.BehindLive.TotalSeconds);
                var windows = new List<long>();
                var behinds = new List<long>();
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(100);
                    var s = r.Pb.Status;
                    windows.Add(s.TimeshiftWindowMs); behinds.Add(s.BehindLiveMs);
                }
                Assert.All(behinds, b => Assert.Equal(0, b));                                  // live, it does not flicker to "-0:02"
                Assert.True(windows.Last() > windows.First(), "the window grows: " + windows.First() + " -> " + windows.Last());
                Assert.True(r.Pb.TimeshiftWindow.TotalSeconds > 0);
                Assert.Contains("timeshift{", r.Pb.EngineDetail);
                Assert.True(r.At(0).BytesRead > 512 * 1024);
            }
        }

        [Fact]
        public async Task PauseKeepsThePumpRunningBehindLiveGrowsAndResumeContinuesWhereItWas()
        {
            // short StallTimeout: the pause lasts longer than the stall deadline and, on resume, the "old" position must not trigger the failure
            using (var r = await Rig.StartAsync(4 * Mib, o => { o.StallTimeout = TimeSpan.FromSeconds(1.5); o.StaleLiveTimeout = TimeSpan.FromSeconds(1); }))
            {
                await r.WaitWindowAsync(12);
                var buffer = r.Live.TimeshiftBuffer;
                long w0 = buffer.WriteOffset;
                double window0 = r.Pb.TimeshiftWindow.TotalSeconds;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                r.Pb.Pause();
                Assert.Equal(PlayPhase.Paused, r.Pb.Status.Phase);
                Assert.True(r.At(0).IsPaused);
                Assert.True(r.Live.HoldOpen, "during the pause the server must not stop the session for inactivity");
                await Task.Delay(2500);                                                         // 10 s of stream (and more than StallTimeout)
                double mediaPaused = sw.Elapsed.TotalSeconds * TimeScale;
                Assert.Equal(PlayPhase.Paused, r.Pb.Status.Phase);
                Assert.Null(r.Pb.Status.Warning);
                Assert.InRange(r.Pb.BehindLive.TotalSeconds, mediaPaused - 1.5, mediaPaused + 5.5);         // what the pause lasts plus what was already lagging (latency, preparation)
                Assert.InRange(r.Pb.Status.BehindLiveMs, (mediaPaused - 1.5) * 1000, (mediaPaused + 5.5) * 1000);
                Assert.True(buffer.WriteOffset > w0 + 600 * 1024, "the pump must keep filling the buffer during the pause");
                Assert.True(r.Pb.TimeshiftWindow.TotalSeconds > window0 + 5, "the window grows during the pause");
                { var st = r.Pb.Status; Assert.True(st.BehindLiveMs <= st.TimeshiftWindowMs, st.BehindLiveMs + " > " + st.TimeshiftWindowMs); }

                r.Pb.Resume();
                Assert.True(await Until(() => r.Pb.Status.Phase == PlayPhase.Playing, 5000));
                Assert.Equal(1, r.Count);                                                       // the same player is resumed, where it was
                Assert.Equal(1, r.At(0).ResumeCalls);
                Assert.False(r.Live.HoldOpen);
                await Task.Delay(2500);                                                         // without the stall deadline (already expired during the pause) triggering anything
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.InRange(r.Pb.BehindLive.TotalSeconds, mediaPaused - 1.5, mediaPaused + 14);          // it stays behind
                Assert.Equal(0, r.Pb.Status.Retry);
                Assert.False(r.At(0).Ended);
                Assert.Equal(1, r.Count);

                r.Pb.TogglePause();
                Assert.Equal(PlayPhase.Paused, r.Pb.Status.Phase);
                r.Pb.TogglePause();
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);
                Assert.Equal(1, r.Count);
            }
        }

        [Fact]
        public async Task AnyPauseLongerThanTheWindowComesBackToLiveOnResume()
        {
            using (var r = await Rig.StartAsync(3 * Mib / 2))                                   // ~12 s stored: a window of about 6-8 s
            {
                await r.WaitWindowAsync(3);
                var session = r.Pb.EngineSession;
                r.Pb.Pause();
                await Task.Delay(4000);                                                         // 16 s of stream unseen: more than the window
                Assert.Equal(PlayPhase.Paused, r.Pb.Status.Phase);
                { var st = r.Pb.Status; Assert.True(st.BehindLiveMs <= st.TimeshiftWindowMs, st.BehindLiveMs + " > " + st.TimeshiftWindowMs); }
                r.Pb.Resume();
                Assert.True(await Until(() => r.Count == 2 && r.Pb.Status.Phase == PlayPhase.Playing, 15000), "it did not return to live: " + r.Pb.Status + " " + r.Count);
                Assert.DoesNotContain("back=", r.At(1).Url);                                    // reopened live
                Assert.Same(session, r.Pb.EngineSession);                                       // on the same engine session
                Assert.True(r.At(0).Disposed);
                Assert.Equal(0, r.Pb.Status.Retry);
                Assert.DoesNotContain(PlayPhase.Retrying, r.PhasesCopy());
                Assert.True(await Until(() => r.Pb.BehindLive == TimeSpan.Zero, 3000));
            }
        }

        [Fact]
        public async Task ConnectionLostWhilePausedIsNotAFailureAndResumeReopensAtTheSamePosition()
        {
            using (var r = await Rig.StartAsync(4 * Mib, o => o.MaxLiveRetries = 0))
            {
                await r.WaitWindowAsync(14);
                r.Pb.Pause();
                await Task.Delay(1500);                                                         // 6 s back
                r.At(0).RaiseCompleted();                                                       // the server closes (e.g. cursor evicted) and the player sees it as the end
                r.At(0).RaiseFailure(new PlayerFailure("error del reproductor: ConnectionFailed"));
                await Task.Delay(700);
                Assert.Equal(PlayPhase.Paused, r.Pb.Status.Phase);                              // it stays paused: it is not treated as a failure
                Assert.Equal(1, r.Count);
                r.Pb.Resume();
                Assert.True(await Until(() => r.Count == 2 && r.Pb.Status.Phase == PlayPhase.Playing, 10000), "no reabrio: " + r.Pb.Status);
                long back = BackOf(r.At(1).Url);
                Assert.InRange(back, 5000, 14000);                                              // at the same position (not at the live point)
                Assert.Equal(0, r.Pb.Status.Retry);
            }
        }

        [Fact]
        public async Task WithoutMemoryTheLiveBehavesLikeToday()
        {
            using (var r = await Rig.StartAsync(0))
            {
                Assert.True(r.Pb.Status.IsLive);
                r.Pb.Pause();
                Assert.Equal(PlayPhase.Playing, r.Pb.Status.Phase);                             // pause is ignored on a live stream without memory
                Assert.False(r.At(0).IsPaused);
                Assert.Equal(TimeSpan.Zero, r.Pb.TimeshiftWindow);
                Assert.Equal(TimeSpan.Zero, r.Pb.BehindLive);
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                await r.Pb.GoLiveAsync();
                Assert.Equal(1, r.Count);
                Assert.Equal(0, r.Pb.Status.TimeshiftWindowMs);
                await Task.Delay(500);
                Assert.Equal(0, r.Pb.Status.BehindLiveMs);
            }
        }

        [Fact]
        public async Task StoppingWithASeekPendingCompletesTheTask()
        {
            using (var r = await Rig.StartAsync(4 * Mib, null, p => p.PrepareDelayMs = 400))
            {
                await r.WaitWindowAsync(16);
                var t = r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                await Task.Delay(100);
                r.Pb.Stop();
                await t.WaitAsync(TimeSpan.FromSeconds(10));
                await r.Run.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(PlayPhase.Idle, r.Pb.Status.Phase);
                Assert.Equal(TimeSpan.Zero, r.Pb.BehindLive);
            }
        }

        [Fact]
        public async Task SeekAndPauseBeforePlayingDoNothing()
        {
            using (var r = await Rig.StartAsync(4 * Mib, waitPlaying: false))
            {
                r.Pb.Pause();
                await r.Pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(-10));
                await r.Pb.GoLiveAsync();
                Assert.True(await Until(() => r.Pb.Status.Phase == PlayPhase.Playing, 30000));
                Assert.Equal(1, r.F.Count);
            }
        }
    }
}
