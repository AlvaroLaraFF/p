using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Probe
{
    /// <summary>
    /// The results of Ace's central search engine (magnet:?xt=urn:btih:...) are checked like a content_id: their descriptor is resolved over P2P and the live stream is probed.
    /// Before, only peers announced by trackers and DHT were counted (425 of 444 results came out as alive and only 25 % opened).
    /// </summary>
    public class InfoHashProbeTests
    {
        private static EngineOptions Opt()
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false, EnableListener = false };
            o.Resolver.EnableTsService = false;
            o.Resolver.WeakFallbackSeconds = 0.3;
            o.Resolver.Timeout = TimeSpan.FromSeconds(3);
            o.Probe.ResolveTimeout = TimeSpan.FromSeconds(3);
            o.Probe.UdpTimeoutsSeconds = new[] { 1, 1 };
            o.Probe.RecheckDelay = TimeSpan.FromMilliseconds(300);
            o.Probe.CycleInterval = TimeSpan.FromMinutes(5);
            o.Probe.LiveProbe = new LiveProbeOptions
            {
                TargetPeers = 1, MaxConnections = 6, MaxTime = TimeSpan.FromSeconds(8), ConnectTimeout = TimeSpan.FromSeconds(1), HandshakeTimeout = TimeSpan.FromSeconds(1),
                MinObserve = TimeSpan.FromSeconds(1.5), MaxObserve = TimeSpan.FromSeconds(3), StaleAfter = TimeSpan.FromSeconds(2), SoftTime = TimeSpan.FromSeconds(1), UseDht = false, UseTrackers = false
            };
            return o;
        }

        private static LinkEntry Magnet(byte[] infoHash, string tracker = null)
        {
            return LinkEntry.FromUrl("Canal del buscador", "magnet:?xt=urn:btih:" + Hex.ToHex(infoHash) + (tracker != null ? "&tr=" + Uri.EscapeDataString(tracker) : ""));
        }

        private static async Task<ProbeResult> RunOnce(AceEngineHost host, LinkEntry e)
        {
            var sched = host.CreateProbeScheduler();
            try
            {
                sched.Start(new[] { e });
                await sched.WaitFirstCycleAsync(new CancellationTokenSource(30000).Token);
                return sched.GetResult(e);
            }
            finally { sched.Stop(); }
        }

        [Fact]
        public async Task TheOldBehaviourCountedAnnouncedPeersAndShowedAnUnservedMagnetAsAlive()
        {
            using (var tracker = new FakeScrapeUdpTracker())
            {
                var ih = new byte[20]; ih[0] = 0x42; ih[19] = 0x07;
                tracker.Swarms[Hex.ToHex(ih)] = new[] { 0, 0, 33 };
                var o = Opt(); o.Probe.ResolveInfoHashLinks = false;
                using (var host = new AceEngineHost(o))
                {
                    var r = await RunOnce(host, Magnet(ih, tracker.Url));
                    Assert.Equal(LinkState.Good, r.State);                       // 33 announced peers, none checked
                    Assert.False(r.LiveVerified);
                    Assert.Equal(33, r.Score);
                }
            }
        }

        [Fact]
        public async Task AMagnetWhoseDescriptorNobodyServesIsNotShownAsAlive()
        {
            using (var tracker = new FakeScrapeUdpTracker())
            {
                var ih = new byte[20]; ih[0] = 0x42; ih[19] = 0x07;
                tracker.Swarms[Hex.ToHex(ih)] = new[] { 0, 0, 33 };
                using (var host = new AceEngineHost(Opt()))
                {
                    var e = Magnet(ih, tracker.Url);
                    var r = await RunOnce(host, e);
                    Assert.Equal(LinkState.Unknown, r.State);                    // by announced peers it would come out alive; without a descriptor it cannot be opened
                    Assert.True(r.Unresolved);
                    Assert.Equal(ResolveFailure.NoServer, r.Failure);
                    var st = LiveChecker.Map(r);
                    Assert.False(st.IsLive);
                    Assert.True(st.IsNoSignal);
                }
            }
        }

        /// <summary>A simulated channel: tracker with announced peers, a peer that serves the descriptor via 'full' and peers that speak the live protocol (which the engine only knows through hints).</summary>
        private sealed class Rig : IDisposable
        {
            public readonly FakeScrapeUdpTracker Tracker = new FakeScrapeUdpTracker();
            public readonly LiveStreamModel Model;
            public readonly FakeAceMetadataPeer Meta;
            public readonly List<LiveSourceSimulator> Sources = new List<LiveSourceSimulator>();

            public Rig(int announced = 33)
            {
                Model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4, Trackers = Tracker.Url });
                Meta = new FakeAceMetadataPeer { FullData = Model.TransportBytes, AdvertiseMetadataSize = false };
                Tracker.Swarms[Hex.ToHex(Model.InfoHash)] = new[] { 0, 0, announced };
            }

            public LiveSourceSimulator AddSource(SimPeerBehavior b = null) { var s = new LiveSourceSimulator(Model, b); Sources.Add(s); return s; }

            public AceEngineHost Host()
            {
                var o = Opt(); o.StaticPeers.Add(Meta.EndPoint.ToString());
                var h = new AceEngineHost(o);
                h.Start();
                h.Resolver.Hints.Add(Hex.ToHex(Model.InfoHash), Sources.Select(x => x.EndPoint).ToList());      // the peers that really serve the live stream
                return h;
            }

            public LinkEntry Link() { return Magnet(Model.InfoHash, Tracker.Url); }

            public void Dispose()
            {
                foreach (var s in Sources) s.Dispose();
                Meta.Dispose();
                Tracker.Dispose();
            }
        }

        [Fact]
        public async Task AMagnetServedByAPeerIsResolvedAndVerifiedLikeAContentId()
        {
            using (var rig = new Rig())
            {
                rig.AddSource();
                using (var host = rig.Host())
                {
                    var e = rig.Link();
                    var r = await RunOnce(host, e);
                    Assert.True(r.State == LinkState.Good, r.State + " " + r.Error + " unresolved=" + r.Unresolved + " " + r.Live);
                    Assert.True(r.LiveVerified, r + " " + r.Error);
                    Assert.True(r.Live.ActivePeers >= 1);
                    Assert.False(r.Unresolved);
                    var st = LiveChecker.Map(r);
                    Assert.True(st.IsLive && st.Verified);
                    Assert.Equal(r.Live.ActivePeers, st.Peers);                  // the number shown is the observed one, not the 33 announced
                    Assert.True(rig.Meta.FullRequests >= 1);
                    // the connections of the resolution and the probe came out of the shared budget, and were returned
                    Assert.NotNull(host.ProbeBudget);
                    Assert.True(host.ProbeBudget.Peak > 0 && host.ProbeBudget.Peak <= host.ProbeBudget.Capacity, "pico " + host.ProbeBudget.Peak);
                    Assert.Equal(0, host.ProbeBudget.InUse);
                    // the descriptor stays in the cache: a second check (or opening the channel) does not ask anyone again
                    int asked = rig.Meta.FullRequests;
                    var again = await RunOnce(host, e);
                    Assert.Equal(LinkState.Good, again.State);
                    Assert.Equal(asked, rig.Meta.FullRequests);
                }
            }
        }

        [Fact]
        public async Task AMagnetWhoseEmitterIsStoppedIsDead()
        {
            using (var rig = new Rig())
            {
                rig.AddSource(new SimPeerBehavior { Frozen = true, NodeType = 1 });
                rig.AddSource(new SimPeerBehavior { Frozen = true, NodeType = 1 });
                using (var host = rig.Host())
                {
                    var r = await RunOnce(host, rig.Link());
                    Assert.True(r.State == LinkState.Dead, r.State + " " + r.Error + " unresolved=" + r.Unresolved + " " + r.Live);
                    Assert.Equal(LiveVerdict.Stale, r.Live.Verdict);
                    Assert.False(LiveChecker.Map(r).IsLive);
                }
            }
        }

        [Fact]
        public async Task AMagnetWithoutAnyReachableLivePeerIsUnknownAndRetried()
        {
            using (var rig = new Rig())
            using (var host = rig.Host())                                       // the descriptor is served (a node that only gives the file) but no peer speaks the live protocol
            {
                var r = await RunOnce(host, rig.Link());
                Assert.NotEqual(LinkState.Good, r.State);                       // 33 announced peers are not "alive"
                Assert.False(LiveChecker.Map(r).IsLive);
            }
        }

        // ---------- looped retries of what nobody serves ----------

        private static ProbeResult Unresolved(ResolveFailure f) { return new ProbeResult { State = LinkState.Unknown, Unresolved = true, Failure = f, Error = "x" }; }

        [Fact]
        public async Task ALinkNobodyServesIsResolvedAgainAndAgainWhileTheListIsOpen()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(100), RecheckDelay = TimeSpan.Zero, StarvedRetries = 0 } };
            fp.ResolveFn = (e, attempt) => e.Key == "cid:" + 1.ToString("x40") ? new ResolveStep { Final = Unresolved(ResolveFailure.NoServer) } : FakePhasedProber.Target(e);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var es = Enumerable.Range(0, 2).Select(i => LinkEntry.FromUrl("C" + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList();
                s.Start(es);
                await Task.Delay(1500);
                int unresolvedCalls, okCalls;
                lock (fp.ResolveCalls) { unresolvedCalls = fp.ResolveCalls["cid:" + 1.ToString("x40")]; okCalls = fp.ResolveCalls["cid:" + 2.ToString("x40")]; }
                Assert.True(unresolvedCalls >= 5, "what nobody serves is retried in a loop: " + unresolvedCalls);
                Assert.Equal(1, okCalls);                                   // what was already verified is not requested again within its cache
            }
        }

        [Fact]
        public void TheDefaultsOfTheLoop()
        {
            var o = new ProbeOptions();
            Assert.Equal(TimeSpan.FromSeconds(10), o.CycleInterval);        // minimum breather between retries of the same channel
            Assert.Equal(4, o.RetryConcurrency);
            Assert.True(o.ResolveInfoHashLinks);
        }
    }
}
