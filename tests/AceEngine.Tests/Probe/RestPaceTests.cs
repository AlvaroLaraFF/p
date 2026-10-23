using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Probe;
using AceEngine.Sources;
using AceEngine.Tests.Peers;
using AceEngine.Tests.Tv2;
using Xunit;

namespace AceEngine.Tests.Probe
{
    /// <summary>With the UI idle, the looped retries of the unverified slow down; the first pass and the return to activity do not.</summary>
    public class RestPaceTests
    {
        private static List<LinkEntry> Entries(int n) { return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList(); }
        private static CancellationToken Tok(int ms = 15000) { return new CancellationTokenSource(ms).Token; }

        private static FakeVerifyingProber Prober(Action<ProbeOptions> tweak = null)
        {
            var o = new ProbeOptions { Concurrency = 20, LiveVerifyConcurrency = 20, RetryConcurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(30), IdleRetryConcurrency = 1, IdleRetryPause = TimeSpan.FromMilliseconds(400) };
            if (tweak != null) tweak(o);
            var fp = new FakeVerifyingProber { Options = o, VerifyDelayMs = 40 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };       // nobody serves any: all are retries
            return fp;
        }

        [Fact]
        public void TheDefaultsLowTheRetryRateOnlyInRest()
        {
            var o = new ProbeOptions();
            Assert.Equal(4, o.RetryConcurrency);
            Assert.Equal(1, o.IdleRetryConcurrency);
            Assert.True(o.IdleRetryPause >= TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task InRestRetriesGoOneAtATimeWithABreathAndMuchSlowerThanNormal()
        {
            // normal pace: 4 at a time
            var fpA = Prober();
            using (var s = new ProbeScheduler(fpA, fpA.Options))
            {
                s.Start(Entries(8));
                await s.WaitFirstCycleAsync(Tok());
                fpA.MaxVerifying = 0; int c0 = fpA.VerifyCalls;
                await Task.Delay(2000);
                int normal = fpA.VerifyCalls - c0;
                Assert.True(fpA.MaxVerifying >= 3, "normally several run at once: " + fpA.MaxVerifying);
                Assert.True(normal >= 12, "normal pace: " + normal);
            }
            // idle: one at a time and with a breather
            var fpB = Prober();
            using (var s = new ProbeScheduler(fpB, fpB.Options))
            {
                s.Start(Entries(8));
                await s.WaitFirstCycleAsync(Tok());
                s.SetResting(true);
                Assert.True(s.Resting);
                await Task.Delay(300);                                                    // what was already in progress finishes
                fpB.MaxVerifying = 0; int c0 = fpB.VerifyCalls;
                await Task.Delay(2000);
                int rest = fpB.VerifyCalls - c0;
                Assert.True(fpB.MaxVerifying <= 1, "when idle, one at a time: " + fpB.MaxVerifying);
                Assert.True(rest <= 6, "with a 400 ms breather after each 40 ms attempt about 5 fit in 2 s: " + rest);
                Assert.True(rest >= 1, "but it keeps retrying");
            }
        }

        [Fact]
        public async Task TheFirstPassIsNotSlowedDownByRest()
        {
            var fp = Prober();
            fp.VerifyDelayMs = 80;
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.SetResting(true);                                                       // the UI is already idle when the list is opened
                s.Start(Entries(10));
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(10, fp.VerifyCalls);
                Assert.True(fp.MaxVerifying > 3, "the first pass (never checked) uses all the concurrency: " + fp.MaxVerifying);
            }
        }

        [Fact]
        public async Task ATouchRestoresTheNormalRateWithoutWaitingForTheBreath()
        {
            var fp = Prober(o => o.IdleRetryPause = TimeSpan.FromSeconds(30));
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(8));
                await s.WaitFirstCycleAsync(Tok());
                s.SetResting(true);
                await Task.Delay(500);
                int c0 = fp.VerifyCalls;
                await Task.Delay(1500);
                Assert.True(fp.VerifyCalls - c0 <= 2, "when idle with a 30 s breather there are almost no retries: " + (fp.VerifyCalls - c0));
                s.SetResting(false);                                                      // the remote is touched
                int c1 = fp.VerifyCalls;
                fp.MaxVerifying = 0;
                await Task.Delay(2000);
                Assert.True(fp.VerifyCalls - c1 >= 8, "it recovers the normal pace right away (it does not wait out the 30 s breather): " + (fp.VerifyCalls - c1));
                Assert.True(fp.MaxVerifying >= 3);
            }
        }

        [Fact]
        public async Task LeavingTheScreenWhileRestingFreesTheRetrySlots()
        {
            var fp = Prober(o => o.IdleRetryPause = TimeSpan.FromSeconds(30));
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(8);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                s.SetResting(true);
                await Task.Delay(500);                                                    // a retry in its 30 s breather
                s.Cancel();                                                               // leaves the screen
                await Task.Delay(300);
                s.SetResting(false);
                s.Start(es);                                                              // enters again: the slots were not left held
                int c0 = fp.VerifyCalls; fp.MaxVerifying = 0;
                await Task.Delay(2000);
                Assert.True(fp.VerifyCalls - c0 >= 8, "it retries normally: " + (fp.VerifyCalls - c0));
                Assert.True(fp.MaxVerifying >= 3);
            }
        }

        [Fact]
        public async Task TheCheckerPassesTheRestSignalToItsSchedulerAndToTheNextOne()
        {
            var fp = Prober();
            using (var s1 = new ProbeScheduler(fp, fp.Options))
            using (var s2 = new ProbeScheduler(fp, fp.Options))
            {
                ProbeScheduler cur = s1;
                using (var lc = new LiveChecker(() => cur))
                {
                    var rest = (IRestAware)lc;
                    rest.SetResting(true);                                                // before there is any batch
                    lc.Check(Enumerable.Range(1, 3).Select(i => Ch.Make(i)).ToList());
                    Assert.True(s1.Resting);
                    rest.SetResting(false);
                    Assert.False(s1.Resting);
                    rest.SetResting(true);
                    cur = s2;                                                             // the engine was recreated: another scheduler
                    lc.Check(Enumerable.Range(1, 3).Select(i => Ch.Make(i)).ToList());
                    Assert.True(s2.Resting);
                    using (var async = new AsyncLiveChecker(lc))
                    {
                        ((IRestAware)async).SetResting(false);
                        Assert.False(s2.Resting);
                    }
                    await Task.Delay(50);
                }
            }
        }
    }
}
