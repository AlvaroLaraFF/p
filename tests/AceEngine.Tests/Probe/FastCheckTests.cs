using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Probe;
using AceEngine.Sources;
using AceEngine.Tests.Peers;
using Xunit;

namespace AceEngine.Tests.Probe
{
    /// <summary>Shared connection budget.</summary>
    public class ConnectionBudgetTests
    {
        [Fact]
        public async Task NeverHandsOutMoreThanTheCapacity()
        {
            var b = new ConnectionBudget(20);
            int now = 0, max = 0;
            var tasks = Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
            {
                int u = await b.AcquireAsync(i % 2 == 0 ? 8 : 7, i % 3 == 0 ? ConnectionBudget.High : ConnectionBudget.Low, CancellationToken.None);
                int n = Interlocked.Add(ref now, u);
                int m; while (n > (m = Volatile.Read(ref max))) Interlocked.CompareExchange(ref max, n, m);
                await Task.Delay(5);
                Interlocked.Add(ref now, -u);
                b.Release(u);
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.True(max <= 20, "pico " + max);
            Assert.Equal(0, b.InUse);
            Assert.True(b.Peak <= 20);
            Assert.Equal(0, b.Waiting);
        }

        [Fact]
        public async Task HighPriorityOvertakesWaitingLowPriority()
        {
            var b = new ConnectionBudget(10);
            int first = await b.AcquireAsync(10, ConnectionBudget.Low, CancellationToken.None);
            var order = new List<string>();
            var low = Task.Run(async () => { int u = await b.AcquireAsync(6, ConnectionBudget.Low, CancellationToken.None); lock (order) order.Add("low"); await Task.Delay(30); b.Release(u); });
            await Task.Delay(50);
            var high = Task.Run(async () => { int u = await b.AcquireAsync(6, ConnectionBudget.High, CancellationToken.None); lock (order) order.Add("high"); await Task.Delay(30); b.Release(u); });
            await Task.Delay(50);
            Assert.Equal(2, b.Waiting);
            b.Release(first);
            await Task.WhenAll(low, high);
            Assert.Equal(new[] { "high", "low" }, order.ToArray());
        }

        [Fact]
        public async Task ACancelledWaitLeavesTheQueueAndHoldsNothing()
        {
            var b = new ConnectionBudget(5);
            int u = await b.AcquireAsync(5, ConnectionBudget.Low, CancellationToken.None);
            var cts = new CancellationTokenSource();
            var w = b.AcquireAsync(3, ConnectionBudget.Low, cts.Token);
            await Task.Delay(30);
            Assert.Equal(1, b.Waiting);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w);
            Assert.Equal(0, b.Waiting);
            b.Release(u);
            Assert.Equal(0, b.InUse);
            int again = await b.AcquireAsync(5, ConnectionBudget.Low, CancellationToken.None);
            Assert.Equal(5, again);
        }

        [Fact]
        public async Task ARequestBiggerThanTheCapacityIsClampedInsteadOfWaitingForever()
        {
            var b = new ConnectionBudget(4);
            int u = await b.AcquireAsync(100, ConnectionBudget.Low, new CancellationTokenSource(2000).Token);
            Assert.Equal(4, u);
            b.Release(u);
        }
    }

    public class SharedBudgetTests
    {
        [Fact]
        public void AllSchedulersOfAnEngineShareOneConnectionBudgetOfTheConfiguredSize()
        {
            var o = new AceEngine.P2P.EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            o.Probe.TcpBudget = 40;
            using (var host = new AceEngine.P2P.AceEngineHost(o))
            {
                Assert.Null(host.ProbeBudget);
                using (var a = host.CreateProbeScheduler())
                using (var b = host.CreateProbeScheduler())
                {
                    Assert.NotNull(host.ProbeBudget);
                    Assert.Equal(40, host.ProbeBudget.Capacity);
                }
            }
            var tv = AceEngine.App.EngineOptionsFactory.Build(new AceEngine.App.AppConfig(), null);
            Assert.Equal(52, tv.Probe.TcpBudget);                              // the TV and the PC, the same limit
            Assert.Equal(52, new AceEngine.P2P.EngineOptions().Probe.TcpBudget);
        }
    }

    /// <summary>Expiry rule: an available channel is valid for 10 minutes and on expiry it is not rechecked on its own; negatives are retried in the background.</summary>
    [Collection(AceEngine.Tests.RealtimeCollection.Name)]
    public class FastCheckSchedulerTests
    {
        private static List<LinkEntry> Entries(int n)
        {
            return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList();
        }

        private static CancellationToken Tok(int ms = 15000) { return new CancellationTokenSource(ms).Token; }

        [Fact]
        public async Task TheDefaultsAreTheUsersRule()
        {
            var o = new ProbeOptions();
            Assert.Equal(TimeSpan.FromSeconds(15), o.CheckBudget);
            Assert.Equal(TimeSpan.FromMinutes(10), o.AvailableTtl);
            Assert.False(o.CountBeforeVerify);
            Assert.True(o.TcpBudget > 0 && o.TcpBudget <= 56);
            // the probe (connection units of one channel) fits several times in the budget
            Assert.True(o.TcpBudget / (o.LiveProbe.MaxConnections + 1) >= 6);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task AFastCheckSkipsTheCountOfAnnouncedPeers()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4 } };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(3);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                foreach (var e in es)
                {
                    var r = s.GetResult(e);
                    Assert.Equal(LinkState.Good, r.State);
                    Assert.True(r.LiveVerified);
                    Assert.Equal(4, r.Peers);                             // what was observed
                    Assert.Equal(0, r.Seeders);
                    Assert.Equal("live", r.Method);
                }
            }
        }

        [Fact]
        public async Task AnAvailableChannelWhoseCacheExpiredIsCheckedAgainWhileItIsPresentAndTheCardNeverGoes()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, AvailableTtl = TimeSpan.FromMilliseconds(250), CycleInterval = TimeSpan.FromMilliseconds(80) }, VerifyDelayMs = 60 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(2);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(2, fp.VerifyCalls);
                var end = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < end)
                {
                    foreach (var e in es) Assert.Equal(LinkState.Good, s.GetResult(e).State);       // at no moment "checking" nor without signal: the card stays
                    await Task.Delay(10);
                }
                Assert.True(fp.VerifyCalls >= 4, "still present: requested again on expiry (" + fp.VerifyCalls + ")");
            }
        }

        [Fact]
        public async Task NeverCheckedChannelsGoBeforeRetriesAndRetriesRotateOldestFirst()
        {
            var order = new List<string>();
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 1, LiveVerifyConcurrency = 1, CycleInterval = TimeSpan.FromMilliseconds(50) }, VerifyDelayMs = 5 };
            fp.VerdictFn = t => { lock (order) order.Add(AceEngine.Util.Hex.ToHex(t.InfoHash).TrimStart('0')); return new LiveProbeResult { Verdict = LiveVerdict.NoPieces }; };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(6);
                s.Start(es.Take(3).ToList());
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(new[] { "1", "2", "3" }, order.Take(3).ToArray());
                s.Start(es);                                              // 3 new channels arrive (remote results) with the previous 3 already checked and without signal
                await s.WaitFirstCycleAsync(Tok());
                var second = order.Skip(3).Take(6).ToArray();
                Assert.Equal(new[] { "4", "5", "6" }, second.Take(3).ToArray());      // those never checked, before any retry
                Assert.Equal(new[] { "1", "2", "3" }, second.Skip(3).ToArray());      // and then the retries, the one that has gone longest without being tried first
                // and in rotation: the next pass starts with the one that has been waiting longest (the 4th, which was tried before the 1st in the previous pass)
                var end = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < end && order.Count < 15) await Task.Delay(20);
                Assert.True(order.Count >= 15);
                Assert.Equal(new[] { "4", "5", "6", "1", "2", "3" }, order.Skip(9).Take(6).ToArray());
            }
        }

        [Fact]
        public async Task RetriesOfUnverifiedChannelsAreCappedButTheFirstPassIsNot()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 20, LiveVerifyConcurrency = 20, RetryConcurrency = 2, CycleInterval = TimeSpan.FromMilliseconds(30) }, VerifyDelayMs = 80 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.Unreachable, Tried = 3 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(10));
                await s.WaitFirstCycleAsync(Tok());
                Assert.True(fp.MaxVerifying > 2, "the first pass uses everything: " + fp.MaxVerifying);
                Assert.Equal(10, fp.VerifyCalls);
                fp.MaxVerifying = Volatile.Read(ref fp.Verifying);
                await Task.Delay(1500);
                Assert.True(fp.VerifyCalls > 14, "se reintenta: " + fp.VerifyCalls);
                Assert.True(fp.MaxVerifying <= 2, "retries of what is unverified go two at a time at most: " + fp.MaxVerifying);
            }
        }

        [Fact]
        public async Task LeavingTheScreenCancelsWhatIsRunningAndNothingIsStartedAfterwards()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 8, LiveVerifyConcurrency = 8, CycleInterval = TimeSpan.FromMilliseconds(20) }, VerifyDelayMs = 3000 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(12));
                await Task.Delay(400);
                Assert.True(fp.Verifying > 0);
                s.Cancel();                                               // leave the screen
                var end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end && Volatile.Read(ref fp.Verifying) > 0) await Task.Delay(10);
                Assert.Equal(0, fp.Verifying);                           // what was in progress is cancelled (it does not wait for its 3 s)
                int calls = fp.VerifyCalls;
                await Task.Delay(600);
                Assert.Equal(calls, fp.VerifyCalls);                      // and nothing more starts: nothing is left in the background
                Assert.False(s.IsRunning);
                // those that were being checked do not stay "checking" forever
                foreach (var e in Entries(12)) Assert.NotEqual(LinkState.Checking, s.GetResult(e).State);
            }
        }

        [Fact]
        public async Task AFreshAvailableChannelIsNotCheckedAgainWhenAskedAgainWithinTheCache()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, AvailableTtl = TimeSpan.FromMinutes(10), CycleInterval = TimeSpan.FromMilliseconds(80) } };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(2);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(2, fp.VerifyCalls);
            }
        }

        [Fact]
        public async Task ChannelsThatDidNotShowAnythingAreRetriedInTheBackgroundWhileTheListIsOpen()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, ResultTtl = TimeSpan.FromMilliseconds(100), CycleInterval = TimeSpan.FromMilliseconds(80) } };
            int calls = 0;
            fp.VerdictFn = t => Interlocked.Increment(ref calls) <= 1 ? new LiveProbeResult { Verdict = LiveVerdict.NoPieces } : new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = 2 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Unknown, s.GetResult(e).State);       // nothing arrived: it is not shown
                var end = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < end && s.GetResult(e).State != LinkState.Good) await Task.Delay(50);
                Assert.Equal(LinkState.Good, s.GetResult(e).State);          // on the second one, it arrives
                Assert.Equal(2, s.GetResult(e).Peers);
            }
        }

        [Fact]
        public async Task AnAvailableChannelBeingRecheckedStaysShownUntilTheNewResultArrives()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, AvailableTtl = TimeSpan.FromMilliseconds(50), CycleInterval = TimeSpan.FromMinutes(5) }, VerifyDelayMs = 600 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Good, s.GetResult(e).State);
                await Task.Delay(120);                                        // the cache expires
                s.Start(new[] { e });                                         // it is requested again: the probe takes 600 ms
                await Task.Delay(250);
                Assert.Equal(LinkState.Good, s.GetResult(e).State);          // in the middle of the probe the card is still there (it does not go through "checking")
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(2, fp.VerifyCalls);
                Assert.Equal(LinkState.Good, s.GetResult(e).State);
            }
        }

        [Fact]
        public async Task ASingleNegativeDoesNotRemoveAnAvailableChannelButTwoDo()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, AvailableTtl = TimeSpan.FromMilliseconds(100), ResultTtl = TimeSpan.FromMilliseconds(100), CycleInterval = TimeSpan.FromMilliseconds(80) } };
            int calls = 0;
            fp.VerdictFn = t => Interlocked.Increment(ref calls) == 1 ? new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = 3 } : new LiveProbeResult { Verdict = LiveVerdict.Stale };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Good, s.GetResult(e).State);
                await Task.Delay(250);
                s.Start(new[] { e });                                         // it is requested again: the check comes out negative once
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(2, calls);
                var held = s.GetResult(e);
                Assert.Equal(LinkState.Good, held.State);                    // it stays
                Assert.Equal(3, held.Peers);
                var end = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < end && s.GetResult(e).State == LinkState.Good) await Task.Delay(50);
                Assert.Equal(LinkState.Dead, s.GetResult(e).State);          // the second negative in a row does remove it
            }
        }
    }
}
