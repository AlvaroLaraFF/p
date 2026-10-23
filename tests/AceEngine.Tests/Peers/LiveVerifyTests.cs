using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Probe;
using AceEngine.Sources;
using AceEngine.Tests.Probe;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Peers
{
    /// <summary>A phased prober that also verifies the live stream (ILiveVerifier) with a verdict per channel.</summary>
    internal sealed class FakeVerifyingProber : IPhasedProber, ILiveVerifier
    {
        private readonly FakePhasedProber _inner = new FakePhasedProber { ResolveDelayMs = 5, CountDelayMs = 5 };
        public ProbeOptions Options { get { return _inner.Options; } set { _inner.Options = value; } }
        public Func<SwarmTarget, LiveProbeResult> VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = 4, Saturated = true };
        public int VerifyDelayMs = 20;
        public int Verifying, MaxVerifying, VerifyCalls;

        public Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct) { return _inner.ProbeAsync(entry, ct); }
        public Task<ResolveStep> ResolveAsync(LinkEntry e, CancellationToken ct) { return _inner.ResolveAsync(e, ct); }
        public Task<ProbeResult> CountAsync(SwarmTarget t, CancellationToken ct) { return _inner.CountAsync(t, ct); }
        public Task<ProbeResult> FallbackAsync(SwarmTarget t, CancellationToken ct) { return _inner.FallbackAsync(t, ct); }

        public async Task<LiveProbeResult> VerifyLiveAsync(SwarmTarget target, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Verifying);
            int m; while (n > (m = Volatile.Read(ref MaxVerifying))) Interlocked.CompareExchange(ref MaxVerifying, n, m);
            Interlocked.Increment(ref VerifyCalls);
            try
            {
                await Task.Delay(VerifyDelayMs, ct);
                return VerdictFn(target);
            }
            finally { Interlocked.Decrement(ref Verifying); }
        }
    }

    public class LiveVerifyTests
    {
        [Fact]
        public void TheCheckerShowsTheObservedActivePeersInsteadOfTheScrape()
        {
            var verified = new ProbeResult { State = LinkState.Good, Seeders = 2, Peers = 2388, Live = new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = 4, Saturated = true, EstimatedConnectable = 120 } };
            var s = AceEngine.App.Tv.LiveChecker.Map(verified);
            Assert.True(s.Verified);
            Assert.Equal(4, s.Peers);
            Assert.Equal(0, s.Seeds);
            Assert.Equal(4, s.Score);
            Assert.Equal("4+", s.PeersText);
            Assert.Equal(120, s.Estimated);
            Assert.True(s.IsLive);

            var raw = new ProbeResult { State = LinkState.Good, Seeders = 2, Peers = 2388 };
            var r = AceEngine.App.Tv.LiveChecker.Map(raw);
            Assert.False(r.Verified);
            Assert.Equal(2390, r.Score);
            Assert.Equal("2390", r.PeersText);

            var stale = new ProbeResult { State = LinkState.Dead, Error = "el emisor esta parado", Seeders = 1, Peers = 20, Live = new LiveProbeResult { Verdict = LiveVerdict.Stale } };
            var d = AceEngine.App.Tv.LiveChecker.Map(stale);
            Assert.False(d.IsLive);
            Assert.True(d.IsNoSignal);
            Assert.Contains("parado", d.Reason);
        }

        [Fact]
        public void VerifiedChannelsAreOrderedBeforeRawCountsAndTheEstimateBreaksTies()
        {
            var a = new AceEngine.App.Tv.ChannelStatus { State = LinkState.Good, Verified = true, AtLeast = true, Peers = 4, Estimated = 30 };
            var b = new AceEngine.App.Tv.ChannelStatus { State = LinkState.Good, Verified = true, AtLeast = true, Peers = 4, Estimated = 300 };
            var raw = new AceEngine.App.Tv.ChannelStatus { State = LinkState.Good, Peers = 2388 };
            var few = new AceEngine.App.Tv.ChannelStatus { State = LinkState.Good, Verified = true, Peers = 2, Estimated = 2000 };
            Assert.True(b.OrderKey > a.OrderKey);
            Assert.True(a.OrderKey > few.OrderKey);                 // more observed peers beats a larger estimate
            Assert.True(few.OrderKey > raw.OrderKey);               // a verified channel goes before one with a huge raw count that has not been checked
        }

        private static List<LinkEntry> Entries(int n)
        {
            return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList();
        }

        private static CancellationToken Tok(int ms = 15000) { return new CancellationTokenSource(ms).Token; }

        [Fact]
        public async Task AliveChannelShowsTheObservedActivePeersInsteadOfTheScrape()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CountBeforeVerify = true } };
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
                    Assert.Equal(4, r.DisplayPeers);               // not the scrape's 3
                    Assert.Equal(3, r.Score);                      // the scrape is still there, but it is not what is shown
                }
            }
        }

        [Fact]
        public async Task StaleEmitterBecomesDeadAndUnreachableBecomesUnknown()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4 } };
            fp.VerdictFn = t =>
            {
                int i = Convert.ToInt32(Hex.ToHex(t.InfoHash).TrimStart('0'), 16);
                if (i == 1) return new LiveProbeResult { Verdict = LiveVerdict.Stale };
                if (i == 2) return new LiveProbeResult { Verdict = LiveVerdict.Unreachable, Tried = 30 };
                if (i == 3) return new LiveProbeResult { Verdict = LiveVerdict.NoPieces };
                if (i == 4) return null;                                           // it could not be checked: it keeps the count
                return new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = 2 };
            };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(5);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Dead, s.GetResult(es[0]).State);
                Assert.Contains("stopped", s.GetResult(es[0]).Error);
                Assert.Equal(LinkState.Unknown, s.GetResult(es[1]).State);
                Assert.Contains("connectable", s.GetResult(es[1]).Error);
                Assert.Equal(LinkState.Unknown, s.GetResult(es[2]).State);
                Assert.Equal(LinkState.Good, s.GetResult(es[3]).State);
                Assert.False(s.GetResult(es[3]).LiveVerified);
                Assert.Equal(3, s.GetResult(es[3]).DisplayPeers);                  // unverified: the raw count
                Assert.Equal(LinkState.Good, s.GetResult(es[4]).State);
                // The listing only shows the live ones
                var rows = s.Snapshot();
                Assert.Equal(2, rows.Count);
            }
        }

        [Fact]
        public async Task VerificationsRunAtTheirOwnConcurrencyAndNeverMore()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 30, LiveVerifyConcurrency = 3 }, VerifyDelayMs = 80 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(20));
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(20, fp.VerifyCalls);
                Assert.True(fp.MaxVerifying <= 3, "verificando a la vez: " + fp.MaxVerifying);
                Assert.True(fp.MaxVerifying >= 2, "it should use the quota: " + fp.MaxVerifying);
            }
        }

        [Fact]
        public async Task VerificationCanBeSwitchedOff()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, VerifyLive = false } };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(2);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(0, fp.VerifyCalls);
                Assert.Equal(LinkState.Good, s.GetResult(es[0]).State);
                Assert.Null(s.GetResult(es[0]).Live);
            }
        }

        [Fact]
        public async Task TheScrapeResultCanBePublishedFirstAndTheVerifiedOneAfter()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, PublishBeforeVerify = true, CountBeforeVerify = true }, VerifyDelayMs = 150 };
            var seen = new List<string>();
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.ResultChanged += (e, r) => { lock (seen) seen.Add(r.State + (r.LiveVerified ? "+live" : "")); };
                var es = Entries(1);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                await Task.Delay(100);
                lock (seen) Assert.Equal(new[] { "Checking", "Good", "Good+live" }, seen.ToArray());
            }
        }

        [Fact]
        public async Task VerifiedChannelsAreListedByObservedPeersNotByScrape()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4 } };
            fp.VerdictFn = t =>
            {
                int i = Convert.ToInt32(Hex.ToHex(t.InfoHash).TrimStart('0'), 16);
                return new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = i == 1 ? 1 : i == 2 ? 5 : 3, EstimatedConnectable = i * 10 };
            };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Entries(3);
                s.Start(es);
                await s.WaitFirstCycleAsync(Tok());
                var order = s.Snapshot().Select(r => r.Entry.Name).ToArray();
                Assert.Equal(new[] { "Canal 1", "Canal 2", "Canal 0" }, order);          // 5, 3, 1 active peers
            }
        }
    }
}
