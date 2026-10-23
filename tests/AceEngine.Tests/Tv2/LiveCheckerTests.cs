using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Probe;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    /// <summary>Programmable prober: result by key, optional gate to hold a probe back and a record of what was cancelled.</summary>
    internal sealed class ScriptedProber : ILinkProber
    {
        public ProbeOptions Options { get; set; }
        public readonly ConcurrentDictionary<string, ProbeResult> Results = new ConcurrentDictionary<string, ProbeResult>();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> Gates = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        public readonly ConcurrentDictionary<string, int> DelayMs = new ConcurrentDictionary<string, int>();
        public readonly ConcurrentDictionary<string, int> Calls = new ConcurrentDictionary<string, int>();
        public readonly ConcurrentQueue<string> Started = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<string> Cancelled = new ConcurrentQueue<string>();

        public ScriptedProber(int concurrency = 6)
        {
            Options = new ProbeOptions { Concurrency = concurrency, CycleInterval = TimeSpan.FromMinutes(5), ResultTtl = TimeSpan.FromSeconds(60) };
        }

        public int CallsTo(string key) { int n; return Calls.TryGetValue(key, out n) ? n : 0; }

        public TaskCompletionSource<bool> Gate(string key)
        {
            return Gates.GetOrAdd(key, k => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public async Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct)
        {
            Calls.AddOrUpdate(entry.Key, 1, (k, n) => n + 1);
            Started.Enqueue(entry.Key);
            try
            {
                TaskCompletionSource<bool> gate;
                if (Gates.TryGetValue(entry.Key, out gate)) await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                int d;
                if (DelayMs.TryGetValue(entry.Key, out d)) await Task.Delay(d, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { Cancelled.Enqueue(entry.Key); throw; }
            ProbeResult r;
            if (!Results.TryGetValue(entry.Key, out r)) r = new ProbeResult { State = LinkState.Unknown };
            return new ProbeResult { State = r.State, Seeders = r.Seeders, Peers = r.Peers, Approximate = r.Approximate, NoCounts = r.NoCounts, Unconfirmed = r.Unconfirmed, Method = r.Method, TimeUtc = DateTime.UtcNow };
        }
    }

    internal static class Ch
    {
        public static Channel Make(int i, string name = null)
        {
            var e = LinkEntry.FromUrl(name ?? ("Canal " + i), "acestream://" + i.ToString("x40"));
            return new Channel { Id = e.Key, Name = e.Name, SourceName = e.Name, Entry = e };
        }
        public static string Key(int i) { return "cid:" + i.ToString("x40"); }
        public static ProbeResult Good(int seeds, int peers, bool approx = false, bool unconfirmed = false)
        {
            return new ProbeResult { State = LinkState.Good, Seeders = seeds, Peers = peers, Approximate = approx, Unconfirmed = unconfirmed };
        }
    }

    [Collection(AceEngine.Tests.RealtimeCollection.Name)]

    public class LiveCheckerTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d) try { d.Dispose(); } catch { } }

        private LiveChecker Make(Func<ProbeScheduler> sched, LiveCheckerOptions o = null)
        {
            var c = new LiveChecker(sched, o);
            _d.Add(c);
            return c;
        }

        private ProbeScheduler Sched(ScriptedProber p) { var s = new ProbeScheduler(p, p.Options); _d.Add(s); return s; }

        [Fact]
        public async Task ReportsCheckingThenTheResultOfEachChannelInArrivalOrder()
        {
            var p = new ScriptedProber();
            var delays = new[] { 900, 300, 600, 120 };
            var chs = Enumerable.Range(1, 4).Select(i => Ch.Make(i)).ToList();
            for (int i = 0; i < 4; i++) { p.DelayMs[Ch.Key(i + 1)] = delays[i]; p.Results[Ch.Key(i + 1)] = Ch.Good(i + 1, 10 * (i + 1), approx: i == 2); }
            var sched = Sched(p);
            var checker = Make(() => sched);
            var events = new ConcurrentQueue<KeyValuePair<string, ChannelStatus>>();
            checker.StatusChanged += (c, s) => events.Enqueue(new KeyValuePair<string, ChannelStatus>(c.Id, s));
            Assert.Equal(LinkState.Pending, checker.GetStatus(chs[0]).State);
            checker.Check(chs);
            Assert.Equal(4, checker.Total);
            Assert.True(checker.Running);
            Assert.True(await Wait.Until(() => !checker.Running && checker.Done == 4));
            Assert.True(await Wait.Until(() => events.Count == 8));
            var list = events.ToList();
            // each channel: first Checking and then the result
            foreach (var c in chs)
            {
                var mine = list.Where(e => e.Key == c.Id).Select(e => e.Value).ToList();
                Assert.Equal(2, mine.Count);
                Assert.Equal(LinkState.Checking, mine[0].State);
                Assert.True(mine[1].IsLive);
            }
            // results arrive in the order the probes finish: 4 (120 ms), 2 (300 ms), 3 (600 ms), 1 (900 ms)
            var finals = list.Where(e => e.Value.State == LinkState.Good).Select(e => e.Key).ToList();
            Assert.Equal(new[] { Ch.Key(4), Ch.Key(2), Ch.Key(3), Ch.Key(1) }, finals);
            // translation of ProbeResult to ChannelStatus
            var s3 = checker.GetStatus(chs[2]);
            Assert.Equal(30, s3.Peers); Assert.Equal(3, s3.Seeds); Assert.True(s3.Approximate); Assert.False(s3.Unconfirmed); Assert.Equal(33, s3.Score);
            Assert.True(checker.GetStatus(chs[0]).IsLive); Assert.False(checker.GetStatus(chs[0]).Approximate);
            Assert.False(checker.Running);
        }

        [Fact]
        public async Task TheVisibleChannelsAreProbedFirst()
        {
            var p = new ScriptedProber(1);                              // one slot: the start order is the priority order
            var chs = Enumerable.Range(1, 30).Select(i => Ch.Make(i)).ToList();
            foreach (var c in chs) p.Results[c.Id] = Ch.Good(1, 1);
            var sched = Sched(p);
            var checker = Make(() => sched, new LiveCheckerOptions { VisibleCount = 12 });
            checker.Check(chs);
            Assert.True(await Wait.Until(() => checker.Done == 30, 10000));
            var order = p.Started.ToList();
            Assert.Equal(30, order.Count);
            Assert.Equal(chs.Take(12).Select(c => c.Id).OrderBy(x => x), order.Take(12).OrderBy(x => x));
            // Check with another order: the first ones of the new list are the visible ones
            var rev = chs.AsEnumerable().Reverse().ToList();
            var p2 = new ScriptedProber(1);
            foreach (var c in chs) p2.Results[c.Id] = Ch.Good(1, 1);
            var s2 = Sched(p2);
            var checker2 = Make(() => s2, new LiveCheckerOptions { VisibleCount = 3 });
            checker2.Check(rev);
            Assert.True(await Wait.Until(() => checker2.Done == 30, 10000));
            Assert.Equal(rev.Take(3).Select(c => c.Id).OrderBy(x => x), p2.Started.Take(3).OrderBy(x => x));
        }

        [Fact]
        public async Task CheckReplacesThePreviousBatchAndCancelsItsProbes()
        {
            var p = new ScriptedProber();
            foreach (var i in new[] { 1, 2, 3 }) { p.Gate(Ch.Key(i)); p.Results[Ch.Key(i)] = Ch.Good(9, 9); }
            p.Results[Ch.Key(4)] = Ch.Good(1, 2);
            var sched = Sched(p);
            var checker = Make(() => sched);
            var events = new ConcurrentQueue<string>();
            checker.StatusChanged += (c, s) => events.Enqueue(c.Id);
            var old = new[] { Ch.Make(1), Ch.Make(2), Ch.Make(3) };
            checker.Check(old);
            Assert.True(await Wait.Until(() => p.Started.Count == 3));
            Assert.All(old, c => Assert.Equal(LinkState.Checking, checker.GetStatus(c).State));
            var newChannel = Ch.Make(4);
            checker.Check(new[] { newChannel });
            Assert.Equal(1, checker.Total);
            Assert.True(await Wait.Until(() => p.Cancelled.Count == 3));
            Assert.True(await Wait.Until(() => checker.GetStatus(newChannel).IsLive));
            Assert.All(old, c => Assert.Equal(LinkState.Pending, checker.GetStatus(c).State));        // the cancelled ones do not stay "checking"
            int eventsBefore = events.Count(e => e != newChannel.Id);
            foreach (var i in new[] { 1, 2, 3 }) p.Gate(Ch.Key(i)).TrySetResult(true);                  // even if released, they no longer count
            await Task.Delay(200);
            Assert.All(old, c => Assert.Equal(LinkState.Pending, checker.GetStatus(c).State));
            Assert.Equal(1, checker.Done); Assert.False(checker.Running);
            Assert.Equal(eventsBefore, events.Count(e => e != newChannel.Id));                                // the channels of the old batch no longer notify
        }

        [Fact]
        public async Task FreshResultsAreKeptBetweenBatches()
        {
            var p = new ScriptedProber();
            for (int i = 1; i <= 3; i++) p.Results[Ch.Key(i)] = Ch.Good(i, i);
            p.DelayMs[Ch.Key(3)] = 300;
            var sched = Sched(p);
            var checker = Make(() => sched);
            var a = Ch.Make(1); var b = Ch.Make(2); var c = Ch.Make(3);
            checker.Check(new[] { a, b });
            Assert.True(await Wait.Until(() => checker.Done == 2));
            checker.Check(new[] { a, b, c });                          // the new batch includes the two already checked
            Assert.Equal(3, checker.Total);
            Assert.Equal(2, checker.Done);                              // they already count as checked
            Assert.True(checker.Running);
            Assert.True(checker.GetStatus(a).IsLive);                   // they are shown instantly
            Assert.Equal(1, checker.GetStatus(a).Seeds);
            Assert.True(await Wait.Until(() => checker.Done == 3 && !checker.Running));
            Assert.Equal(1, p.CallsTo(Ch.Key(1))); Assert.Equal(1, p.CallsTo(Ch.Key(2))); Assert.Equal(1, p.CallsTo(Ch.Key(3)));
            // duplicates and nulls in the list do not get in the way
            checker.Check(new Channel[] { a, null, a, c, new Channel { Id = "x", Name = "sin enlace" } });
            Assert.Equal(2, checker.Total);
        }

        [Fact]
        public async Task StopCancelsWhatIsRunningButKeepsTheFreshResults()
        {
            var p = new ScriptedProber();
            p.Results[Ch.Key(1)] = Ch.Good(4, 4);
            p.Gate(Ch.Key(2)); p.Results[Ch.Key(2)] = Ch.Good(1, 1);
            var sched = Sched(p);
            var checker = Make(() => sched);
            var a = Ch.Make(1); var b = Ch.Make(2);
            var events = new ConcurrentQueue<string>();
            checker.StatusChanged += (c, s) => events.Enqueue(c.Id);
            checker.Check(new[] { a, b });
            Assert.True(await Wait.Until(() => checker.GetStatus(a).IsLive && checker.GetStatus(b).State == LinkState.Checking));
            Assert.True(checker.Running);
            checker.Stop();
            Assert.False(checker.Running);
            Assert.True(await Wait.Until(() => p.Cancelled.Count == 1));
            Assert.Equal(LinkState.Pending, checker.GetStatus(b).State);
            Assert.True(checker.GetStatus(a).IsLive);                    // current: it is kept
            int n = events.Count;
            p.Gate(Ch.Key(2)).TrySetResult(true);
            await Task.Delay(200);
            Assert.Equal(n, events.Count);
            checker.Stop(); checker.Stop();                              // idempotent
            // it can be checked again after stopping
            checker.Check(new[] { b });
            Assert.True(await Wait.Until(() => checker.GetStatus(b).IsLive));
        }

        [Fact]
        public async Task ResultsExpireWhenNoBatchIsActive()
        {
            var p = new ScriptedProber();
            p.Results[Ch.Key(1)] = Ch.Good(2, 2);
            var sched = Sched(p);
            var checker = Make(() => sched, new LiveCheckerOptions { ResultTtl = TimeSpan.FromMilliseconds(400) });
            var a = Ch.Make(1);
            checker.Check(new[] { a });
            Assert.True(await Wait.Until(() => checker.GetStatus(a).IsLive));
            await Task.Delay(600);
            Assert.True(checker.GetStatus(a).IsLive);                    // with the batch active, the latest known state keeps being shown
            checker.Stop();
            Assert.Equal(LinkState.Pending, checker.GetStatus(a).State);  // stopped and expired: Pending
            Assert.Equal(LinkState.Pending, checker.GetStatus(null).State);
            Assert.Equal(LinkState.Pending, checker.GetStatus(new Channel { Id = "x" }).State);
        }

        [Fact]
        public async Task StatesOtherThanLiveAreReportedAsIs()
        {
            var p = new ScriptedProber();
            p.Results[Ch.Key(1)] = new ProbeResult { State = LinkState.Dead };
            p.Results[Ch.Key(2)] = new ProbeResult { State = LinkState.Unknown };
            var sched = Sched(p);
            var checker = Make(() => sched);
            var web = new Channel { Id = "w", Name = "Agenda", Entry = LinkEntry.FromUrl("Agenda", "https://agenda.test/futbol") };
            var chs = new[] { Ch.Make(1), Ch.Make(2), web };
            checker.Check(chs);
            Assert.Equal(2, checker.Total);                              // the web link is not probed
            Assert.True(await Wait.Until(() => checker.Done == 2));
            Assert.True(checker.GetStatus(chs[0]).IsNoSignal); Assert.True(checker.GetStatus(chs[1]).IsNoSignal);
            Assert.Equal(LinkState.Unsupported, checker.GetStatus(web).State);
            Assert.False(checker.GetStatus(web).IsLive);
        }

        [Fact]
        public async Task UnconfirmedChannelsAreMarked()
        {
            var p = new ScriptedProber();
            p.Results[Ch.Key(1)] = Ch.Good(0, 5, unconfirmed: true);
            p.Results[Ch.Key(2)] = Ch.Good(0, 5);
            var sched = Sched(p);
            var checker = Make(() => sched);
            var chs = new[] { Ch.Make(1), Ch.Make(2) };
            var seen = new ConcurrentQueue<ChannelStatus>();
            checker.StatusChanged += (c, s) => { if (s.IsLive) seen.Enqueue(s); };
            checker.Check(chs);
            Assert.True(await Wait.Until(() => checker.Done == 2));
            Assert.True(checker.GetStatus(chs[0]).Unconfirmed);
            Assert.False(checker.GetStatus(chs[1]).Unconfirmed);
            Assert.True(await Wait.Until(() => seen.Count == 2));
            Assert.Equal(1, seen.Count(s => s.Unconfirmed));
        }

        [Fact]
        public async Task ARecreatedEngineGetsANewSchedulerAndTheOldOneIsDiscarded()
        {
            var p1 = new ScriptedProber(); var p2 = new ScriptedProber();
            p1.Results[Ch.Key(1)] = Ch.Good(1, 11);
            p1.Gate(Ch.Key(2)); p1.Results[Ch.Key(2)] = Ch.Good(1, 111);           // the old engine never gets to give it (it is released first)
            p2.Results[Ch.Key(1)] = Ch.Good(1, 22);
            p2.Results[Ch.Key(2)] = Ch.Good(1, 222);
            p2.Results[Ch.Key(3)] = Ch.Good(1, 333);
            var s1 = Sched(p1); var s2 = Sched(p2);
            var current = s1;
            var checker = Make(() => current);
            var a = Ch.Make(1); var b = Ch.Make(2); var c = Ch.Make(3);
            checker.Check(new[] { a, b });
            Assert.True(await Wait.Until(() => checker.GetStatus(a).IsLive && p1.Started.Count == 2));
            // Suspend / Resume: the engine is released and another one is created
            current = s2;
            checker.Check(new[] { a, b, c });
            Assert.True(await Wait.Until(() => checker.GetStatus(c).IsLive && checker.GetStatus(b).IsLive));
            Assert.True(await Wait.Until(() => p1.Cancelled.Count == 1));             // what was running in the old scheduler was cancelled
            Assert.False(s1.IsRunning); Assert.True(s2.IsRunning);
            Assert.Equal(0, p2.CallsTo(Ch.Key(1)));                                    // the current result for 'a' passed to the new one: it is not probed again
            Assert.Equal(11, checker.GetStatus(a).Peers);
            Assert.Equal(222, checker.GetStatus(b).Peers);                             // and 'b' was given by the new one
            p1.Gate(Ch.Key(2)).TrySetResult(true);                                     // the old one can no longer count
            await Task.Delay(200);
            Assert.Equal(222, checker.GetStatus(b).Peers);
            Assert.Equal(3, checker.Done); Assert.False(checker.Running);
        }

        [Fact]
        public void ASchedulerThatCannotBeCreatedLeavesThingsQuiet()
        {
            int calls = 0;
            var checker = Make(() => { calls++; throw new ObjectDisposedException("AceEngineHost"); });
            var chs = new[] { Ch.Make(1), Ch.Make(2) };
            checker.Check(chs);                                           // does not throw
            Assert.False(checker.Running);
            Assert.Equal(2, checker.Total); Assert.Equal(0, checker.Done);
            Assert.Equal(LinkState.Pending, checker.GetStatus(chs[0]).State);
            checker.Stop();
            Assert.Equal(1, calls);
            var none = Make(() => (ProbeScheduler)null);
            none.Check(chs);
            Assert.False(none.Running);
        }

        [Fact]
        public async Task AFailingListenerDoesNotBreakTheChecker()
        {
            var p = new ScriptedProber();
            p.Results[Ch.Key(1)] = Ch.Good(1, 1);
            var sched = Sched(p);
            var checker = Make(() => sched);
            checker.StatusChanged += (c, s) => { throw new InvalidOperationException("broken listener"); };
            var a = Ch.Make(1);
            checker.Check(new[] { a });
            Assert.True(await Wait.Until(() => checker.GetStatus(a).IsLive));
        }

        [Fact]
        public async Task DisposeStopsEverythingAndLaterCallsAreHarmless()
        {
            var p = new ScriptedProber();
            p.Gate(Ch.Key(1));
            var sched = Sched(p);
            var checker = new LiveChecker(() => sched);
            var a = Ch.Make(1);
            checker.Check(new[] { a });
            Assert.True(await Wait.Until(() => p.Started.Count == 1));
            checker.Dispose();
            checker.Dispose();
            Assert.True(await Wait.Until(() => p.Cancelled.Count == 1));
            Assert.False(checker.Running);
            checker.Check(new[] { a });
            checker.Stop();
            Assert.Equal(1, p.CallsTo(Ch.Key(1)));
        }

        [Fact]
        public async Task WorksWithTheSimulationFakeProber()
        {
            var fp = new AceEngine.Sim.FakeProber { Delay = TimeSpan.FromMilliseconds(30) };
            var chs = Enumerable.Range(1, 5).Select(i => Ch.Make(i)).ToList();
            fp.Results[Ch.Key(1)] = Ch.Good(3, 7);
            fp.Results[Ch.Key(2)] = new ProbeResult { State = LinkState.Dead };
            fp.Results[Ch.Key(3)] = Ch.Good(0, 40, approx: true);
            var sched = new ProbeScheduler(fp, fp.Options); _d.Add(sched);
            var checker = Make(() => sched);
            checker.Check(chs);
            Assert.True(await Wait.Until(() => checker.Done == 5 && !checker.Running));
            Assert.Equal(7, checker.GetStatus(chs[0]).Peers); Assert.Equal(3, checker.GetStatus(chs[0]).Seeds);
            Assert.True(checker.GetStatus(chs[1]).IsNoSignal);
            Assert.True(checker.GetStatus(chs[2]).Approximate);
            Assert.True(checker.GetStatus(chs[4]).IsNoSignal);           // the FakeProber gives Unknown to those with no scheduled result
            // second batch with the channels that gave stream: current, not one more call to the prober (what is unverified, on the other hand, is retried while the batch stays open)
            checker.Stop();
            int calls = fp.Calls;
            checker.Check(new[] { chs[0], chs[2] });
            await Task.Delay(300);
            Assert.Equal(calls, fp.Calls);
            Assert.Equal(2, checker.Done);
        }

        [Fact]
        public async Task ManyChannelsStayBoundedAndConsistent()
        {
            var p = new ScriptedProber(12);
            var chs = Enumerable.Range(1, 600).Select(i => Ch.Make(i)).ToList();
            foreach (var c in chs) p.Results[c.Id] = Ch.Good(1, 1);
            var sched = Sched(p);
            var checker = Make(() => sched, new LiveCheckerOptions { MaxCachedResults = 200 });
            checker.Check(chs);
            var ok = await Wait.Until(() => checker.Done == 600 && !checker.Running, 20000);
            Assert.True(ok, "hechos " + checker.Done + " de " + checker.Total + ", llamadas " + p.Calls.Count + ", activo " + sched.IsRunning);
            Assert.All(chs, c => Assert.True(checker.GetStatus(c).IsLive));            // those of the current batch are never discarded even if they exceed the cap
        }
    }

    public class ChannelOrderTests
    {
        private static Channel C(string name, int idx) { var c = Ch.Make(idx, name); return c; }

        private static ChannelStatus Live(int peers, int seeds = 0) { return new ChannelStatus { State = LinkState.Good, Peers = peers, Seeds = seeds }; }
        private static ChannelStatus St(LinkState s) { return new ChannelStatus { State = s }; }

        private static List<string> Order(IReadOnlyList<Channel> rows, Dictionary<string, ChannelStatus> st)
        {
            return ChannelOrder.ByLivePeers(rows, c => { ChannelStatus s; return st.TryGetValue(c.Name, out s) ? s : null; }).Select(c => c.Name).ToList();
        }

        [Fact]
        public void LiveChannelsGoFirstByScoreThenSeedsThenName()
        {
            var rows = new[] { C("d", 1), C("a", 2), C("c", 3), C("b", 4), C("e", 5) };
            var st = new Dictionary<string, ChannelStatus>
            {
                { "d", Live(5, 5) }, { "a", Live(10, 0) }, { "c", Live(3, 7) }, { "b", Live(10, 0) }, { "e", Live(1, 0) }
            };
            // a and b tie on everything (10 peers): by name; c and d also add up to 10... d: 5+5=10 with 5 seeds, c: 3+7=10 with 7 seeds
            Assert.Equal(new[] { "c", "d", "a", "b", "e" }, Order(rows, st));
        }

        [Fact]
        public void CheckingKeepTheirRelativePositionAfterTheLiveOnesAndNoSignalGoLast()
        {
            var rows = new[] { C("z", 1), C("dead1", 2), C("ver1", 3), C("y", 4), C("unk", 5), C("ver2", 6), C("x", 7), C("pend", 8) };
            var st = new Dictionary<string, ChannelStatus>
            {
                { "z", Live(1) }, { "y", Live(50) }, { "x", Live(2) },
                { "dead1", St(LinkState.Dead) }, { "unk", St(LinkState.Unknown) },
                { "ver1", St(LinkState.Checking) }, { "ver2", St(LinkState.Checking) }
                // "pend" with no known state (null) = Pending
            };
            Assert.Equal(new[] { "y", "x", "z", "ver1", "ver2", "pend", "dead1", "unk" }, Order(rows, st));
        }

        [Fact]
        public void StableWhileResultsKeepArriving()
        {
            var rows = Enumerable.Range(0, 20).Select(i => C("canal" + i, i + 1)).ToList();
            var st = new Dictionary<string, ChannelStatus>();
            var before = Order(rows, st);
            Assert.Equal(rows.Select(c => c.Name), before);                             // nobody checked: it stays as it was
            st["canal7"] = Live(9);
            var o1 = Order(rows, st);
            Assert.Equal("canal7", o1[0]);
            Assert.Equal(rows.Select(c => c.Name).Where(n => n != "canal7"), o1.Skip(1));     // the rest does not move
            st["canal3"] = St(LinkState.Dead);
            var o2 = Order(rows, st);
            Assert.Equal("canal3", o2.Last());
            Assert.Equal(rows.Select(c => c.Name).Where(n => n != "canal7" && n != "canal3"), o2.Skip(1).Take(18));
            // with the same state the result does not change between calls nor depend on the input order of equals
            Assert.Equal(o2, Order(rows, st));
        }

        [Fact]
        public void HttpStreamsWithNoCountsAreLiveWithScoreZero()
        {
            var rows = new[] { C("hls", 1), C("p2p", 2), C("dead", 3) };
            var st = new Dictionary<string, ChannelStatus> { { "hls", Live(0, 0) }, { "p2p", Live(4) }, { "dead", St(LinkState.Dead) } };
            Assert.Equal(new[] { "p2p", "hls", "dead" }, Order(rows, st));
        }

        [Fact]
        public void HandlesNullsAndPureFunctionSemantics()
        {
            Assert.Empty(ChannelOrder.ByLivePeers(null, c => null));
            var rows = new Channel[] { C("b", 1), null, C("a", 2) };
            var st = new Dictionary<string, ChannelStatus> { { "a", Live(2) }, { "b", Live(2) } };
            Assert.Equal(new[] { "a", "b" }, Order(rows, st));
            Assert.Equal(new[] { "b", "a" }, ChannelOrder.ByLivePeers(new[] { C("b", 1), C("a", 2) }, c => (ChannelStatus)null).Select(c => c.Name).ToArray());     // without states: it is not touched
            Assert.Equal(2, ChannelOrder.ByLivePeers(new[] { C("b", 1), C("a", 2) }, c => { throw new InvalidOperationException(); }).Count);
            var input = new[] { C("x", 1), C("y", 2) };
            var copy = input.ToArray();
            ChannelOrder.ByLivePeers(input, c => c.Name == "y" ? Live(3) : null);
            Assert.Equal(copy, input);                                                  // it does not modify the input
        }

        [Fact]
        public async Task WorksOverTheLiveCheckerToo()
        {
            var p = new ScriptedProber();
            for (int i = 1; i <= 3; i++) p.Results[Ch.Key(i)] = Ch.Good(0, i * 10);
            var sched = new ProbeScheduler(p, p.Options);
            using (var checker = new LiveChecker(() => sched))
            {
                var chs = new[] { Ch.Make(1, "uno"), Ch.Make(2, "dos"), Ch.Make(3, "tres") };
                Assert.Equal(new[] { "uno", "dos", "tres" }, ChannelOrder.ByLivePeers(chs, checker).Select(c => c.Name).ToArray());
                checker.Check(chs);
                Assert.True(await Wait.Until(() => checker.Done == 3));
                Assert.Equal(new[] { "tres", "dos", "uno" }, ChannelOrder.ByLivePeers(chs, checker).Select(c => c.Name).ToArray());
            }
            sched.Dispose();
        }
    }

    // ---------- With the real engine: recreated engine and unconfirmed channels ----------

    public class LiveCheckerWithTheEngineTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d.AsEnumerable().Reverse()) try { d.Dispose(); } catch { } }

        private static EngineOptions Opt()
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false, EnableListener = false };
            o.Resolver.EnableTsService = false;
            o.Resolver.WeakFallbackSeconds = 0.3;
            o.Resolver.Timeout = TimeSpan.FromSeconds(10);
            o.Probe.UdpTimeoutsSeconds = new[] { 1, 1 };
            o.Probe.CycleInterval = TimeSpan.FromMinutes(5);
            o.Probe.VerifyLive = false;             // these tests are about the checker and trust with fake trackers; the live probe has its own (Peers/)
            return o;
        }

        [Fact]
        public async Task TheCheckerFollowsTheCurrentEngineAfterItIsRecreated()
        {
            using (var web = new Tv2HttpServer(p => Tv2Response.Text("#EXTM3U\n#EXT-X-VERSION:3\n", "application/vnd.apple.mpegurl")))
            {
                var ch = new Channel { Id = "http://x", Name = "Flujo", Entry = LinkEntry.FromUrl("Flujo", web.Url + "/live.m3u8") };
                var other = new Channel { Id = "http://y", Name = "Otro", Entry = LinkEntry.FromUrl("Otro", web.Url + "/otro.m3u8") };
                AceEngineHost host = new AceEngineHost(Opt());
                var hosts = new List<AceEngineHost> { host };
                var checker = new LiveChecker(() => host); _d.Add(checker);
                checker.Check(new[] { ch });
                Assert.True(await Wait.Until(() => checker.GetStatus(ch).IsLive));
                Assert.True(checker.GetStatus(ch).Peers == 0 && checker.GetStatus(ch).Seeds == 0);          // live http stream: without figures
                // Suspend: the engine is released; Resume: another one is created
                host.Dispose();
                Assert.True(host.IsDisposed);
                host = new AceEngineHost(Opt()); hosts.Add(host); _d.Add(host);
                checker.Check(new[] { ch, other });
                Assert.Equal(2, checker.Total);
                Assert.True(await Wait.Until(() => checker.GetStatus(other).IsLive && checker.Done == 2));
                Assert.True(checker.GetStatus(ch).IsLive);                  // the current result from the previous engine is kept
                Assert.Equal(1, web.Requests.Count(r => r.StartsWith("/live.m3u8")));                       // it was not probed again: the result was seeded into the new scheduler
                // the current engine is released and there is no other: it does not blow up
                host.Dispose();
                checker.Check(new[] { ch });
                Assert.False(checker.Running);
            }
        }

        private static async Task<ProbeResult> ProbeOnce(EngineOptions opt, LinkEntry e)
        {
            using (var host = new AceEngineHost(opt))
            {
                var sched = host.CreateProbeScheduler();
                try
                {
                    sched.Start(new[] { e });
                    await sched.WaitFirstCycleAsync(CancellationToken.None);
                    return sched.GetResult(e);
                }
                finally { sched.Stop(); }
            }
        }

        [Fact]
        public async Task AChannelDeliveredBySingleNetworkIsUnconfirmedAndTwoNetworksConfirmIt()
        {
            using (var tracker = new FakeScrapeUdpTracker())
            {
                var model = new LiveStreamModel(new LiveSimOptions { Trackers = tracker.Url });
                tracker.Swarms[Hex.ToHex(model.InfoHash)] = new[] { 0, 0, 33 };
                var e = LinkEntry.FromUrl("Canal", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9");
                // 1) a single peer: unconfirmed (same rule as playback, TrustLevel.Unconfirmed)
                using (var meta = new FakeAceMetadataPeer { FullData = model.TransportBytes })
                {
                    var o = Opt(); o.StaticPeers.Add(meta.EndPoint.ToString());
                    var r = await ProbeOnce(o, e);
                    Assert.Equal(LinkState.Good, r.State); Assert.Equal(33, r.Score);
                    Assert.True(r.Unconfirmed);
                }
                // 2) two peers from distinct /24 networks give the same file: confirmed
                using (var m1 = new FakeAceMetadataPeer("127.0.0.1") { FullData = model.TransportBytes })
                using (var m2 = new FakeAceMetadataPeer("127.0.1.1") { FullData = model.TransportBytes })
                {
                    var o = Opt(); o.StaticPeers.Add(m1.EndPoint.ToString()); o.StaticPeers.Add(m2.EndPoint.ToString());
                    var r = await ProbeOnce(o, e);
                    Assert.Equal(LinkState.Good, r.State);
                    Assert.False(r.Unconfirmed);
                }
            }
        }

        [Fact]
        public async Task TheCheckerShowsAnUnconfirmedChannelAndKeepsItWhenResolvedFromTheCache()
        {
            using (var tracker = new FakeScrapeUdpTracker())
            using (var meta = new FakeAceMetadataPeer { })
            {
                var model = new LiveStreamModel(new LiveSimOptions { Trackers = tracker.Url });
                tracker.Swarms[Hex.ToHex(model.InfoHash)] = new[] { 0, 0, 12 };
                meta.FullData = model.TransportBytes;
                var o = Opt(); o.StaticPeers.Add(meta.EndPoint.ToString());
                var host = new AceEngineHost(o); _d.Add(host);
                var checker = new LiveChecker(() => host, new LiveCheckerOptions()); _d.Add(checker);
                var e = LinkEntry.FromUrl("Canal", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9");
                var ch = new Channel { Id = e.Key, Name = "Canal", Entry = e };
                checker.Check(new[] { ch });
                Assert.True(await Wait.Until(() => checker.GetStatus(ch).IsLive, 15000));
                var st = checker.GetStatus(ch);
                Assert.True(st.Unconfirmed); Assert.Equal(12, st.Peers);
                // a new batch with a new engine and the same resolution cache and trust on disk is not needed: the old host's cache is not shared, but the
                // same host resolves again from its local cache and keeps the mark
                checker.Stop();
                var again = await ProbeOnce2(host, e);
                Assert.True(again.Unconfirmed);
                Assert.Equal("local_db", again.Source);
            }
        }

        private static async Task<ProbeResult> ProbeOnce2(AceEngineHost host, LinkEntry e)
        {
            var sched = host.CreateProbeScheduler();
            try
            {
                sched.Start(new[] { e });
                await sched.WaitFirstCycleAsync(CancellationToken.None);
                return sched.GetResult(e);
            }
            finally { sched.Stop(); }
        }
    }
}
