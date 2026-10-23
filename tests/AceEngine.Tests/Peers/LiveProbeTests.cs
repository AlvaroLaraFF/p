using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Peers
{
    /// <summary>Live probe (LiveProber) against simulated peers: alive, stopped broadcaster, no connectable peers, no pieces and early stop.</summary>
    public class LiveProberTests
    {
        private static EngineOptions Opt()
        {
            return new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
        }

        private static LiveProbeOptions Po(int target = 3, double maxSeconds = 8)
        {
            return new LiveProbeOptions
            {
                TargetPeers = target, MaxConnections = 8, MaxTime = TimeSpan.FromSeconds(maxSeconds),
                ConnectTimeout = TimeSpan.FromSeconds(1), HandshakeTimeout = TimeSpan.FromSeconds(1),
                MinObserve = TimeSpan.FromSeconds(1.5), MaxObserve = TimeSpan.FromSeconds(3), UseDht = false
            };
        }

        private static int ClosedPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static LiveStreamModel Model(double scale = 4)
        {
            return new LiveStreamModel(new LiveSimOptions { TimeScale = scale });
        }

        [Fact]
        public async Task AliveWhenAPeerHasRecentPiecesAndTheHeadAdvances()
        {
            var m = Model();
            using (var src = new LiveSourceSimulator(m))
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { src.EndPoint }, null, Po(1), CancellationToken.None);
                Assert.Equal(LiveVerdict.Alive, r.Verdict);
                Assert.True(r.Advances > 0, r.ToString());
                Assert.Equal(1, r.ActivePeers);
                Assert.Equal(1, r.TcpOk); Assert.Equal(1, r.HandshakeOk); Assert.Equal(1, r.ExtendOk);
                Assert.Contains(src.EndPoint, r.GoodPeers);
                Assert.Empty(src.Violations);
                Assert.False(src.AnyClientSignatureInvalid);
                Assert.True(r.Seconds < 6, "tardo " + r.Seconds);
            }
        }

        [Fact]
        public async Task StaleWhenThePeersHavePiecesButTheHeadNeverMoves()
        {
            var m = Model();
            using (var a = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 1 }))
            using (var b = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 1 }))
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { a.EndPoint, b.EndPoint }, null, Po(3, 10), CancellationToken.None);
                Assert.Equal(LiveVerdict.Stale, r.Verdict);
                Assert.Equal(0, r.Advances);
                Assert.Equal(0, r.ActivePeers);
                Assert.True(r.WithRecentPieces >= 2);
                Assert.True(r.Seconds < 8, "it should stop when it sees the head standing still: " + r.Seconds);
            }
        }

        [Fact]
        public async Task PeersThatShowTheirBitfieldAndHangUpAreNotWitnessesOfAStoppedEmitter()
        {
            var m = Model();
            using (var a = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 3, CloseAfterBitfield = true }))
            using (var b = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 3, CloseAfterBitfield = true }))
            {
                var prober = new LiveProber(Opt(), null);
                var po = Po(3, 9); po.StaleAfter = TimeSpan.FromSeconds(1);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { a.EndPoint, b.EndPoint }, null, po, CancellationToken.None);
                Assert.NotEqual(LiveVerdict.Stale, r.Verdict);
                Assert.NotEqual(LiveVerdict.Alive, r.Verdict);
                Assert.Equal(0, r.ActivePeers);
            }
        }

        [Fact]
        public async Task AStoppedEmitterIsNotDeclaredBeforeStaleAfter()
        {
            var m = Model();
            using (var a = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 1 }))
            using (var b = new LiveSourceSimulator(m, new SimPeerBehavior { Frozen = true, NodeType = 1 }))
            {
                var po = Po(3, 12); po.StaleAfter = TimeSpan.FromSeconds(4);
                var r = await new LiveProber(Opt(), null).ProbeAsync(m.Descriptor, new[] { a.EndPoint, b.EndPoint }, null, po, CancellationToken.None);
                Assert.Equal(LiveVerdict.Stale, r.Verdict);
                Assert.True(r.Seconds >= 3.9, "it is not declared stopped before StaleAfter: " + r.Seconds);
            }
        }

        [Fact]
        public async Task UnreachableWhenNobodyAcceptsTheConnection()
        {
            var m = Model();
            var prober = new LiveProber(Opt(), null);
            var dead = Enumerable.Range(0, 6).Select(i => new IPEndPoint(IPAddress.Loopback, ClosedPort())).ToList();
            var r = await prober.ProbeAsync(m.Descriptor, dead, null, Po(2, 5), CancellationToken.None);
            Assert.Equal(LiveVerdict.Unreachable, r.Verdict);
            Assert.Equal(0, r.TcpOk);
            Assert.Equal(6, r.Tried);
            Assert.Equal(0, r.ActivePeers);
            Assert.True(r.Seconds < 3, "with no candidates to probe there is no reason to wait for the deadline: " + r.Seconds);
        }

        [Fact]
        public async Task PeersThatAcceptTcpButNeverAnswerTheHandshakeAreUnreachable()
        {
            var m = Model();
            using (var slow = new LiveSourceSimulator(m, new SimPeerBehavior { HandshakeDelayMs = 4000 }))
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { slow.EndPoint }, null, Po(1, 5), CancellationToken.None);
                Assert.Equal(LiveVerdict.Unreachable, r.Verdict);
                Assert.Equal(1, r.TcpOk);
                Assert.Equal(0, r.HandshakeOk);
            }
        }

        [Fact]
        public async Task NoPiecesWhenConnectedPeersHoldNothing()
        {
            var m = Model();
            using (var a = new LiveSourceSimulator(m, new SimPeerBehavior { NoPieces = true, NodeType = 1 }))
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { a.EndPoint }, null, Po(1, 4), CancellationToken.None);
                Assert.Equal(LiveVerdict.NoPieces, r.Verdict);
                Assert.Equal(1, r.ExtendOk);
                Assert.Equal(0, r.WithRecentPieces);
            }
        }

        [Fact]
        public async Task OneGoodPeerAmongDeadOnesIsEnough()
        {
            var m = Model();
            using (var good = new LiveSourceSimulator(m))
            {
                var list = Enumerable.Range(0, 10).Select(i => new IPEndPoint(IPAddress.Loopback, ClosedPort())).ToList();
                list.Add(good.EndPoint);
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, list, null, Po(1), CancellationToken.None);
                Assert.Equal(LiveVerdict.Alive, r.Verdict);
                Assert.True(r.Tried >= 1);
                Assert.Equal(good.EndPoint, r.GoodPeers.Single());
            }
        }

        [Fact]
        public async Task ASinglePeerIsEnoughOnceTheSoftTimeHasPassed()
        {
            var m = Model();
            using (var only = new LiveSourceSimulator(m))
            {
                var po = Po(4, 12);
                po.SoftTime = TimeSpan.FromSeconds(1.5);
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { only.EndPoint }, null, po, CancellationToken.None);
                Assert.Equal(LiveVerdict.Alive, r.Verdict);
                Assert.Equal(1, r.ActivePeers);
                Assert.False(r.Saturated);
                Assert.True(r.Seconds < 5, "it did not wait for the maximum deadline: " + r.Seconds);
            }
        }

        [Fact]
        public async Task PeersFromTheTrackerAreUsedAndTheProbeUnregistersRightAfter()
        {
            using (var tracker = new AceEngine.Tests.Probe.FakeScrapeUdpTracker())
            using (var src = new LiveSourceSimulator(new LiveStreamModel(new LiveSimOptions { TimeScale = 4, Trackers = tracker.Url })))
            {
                var m = src.Model;
                tracker.AnnouncePeers = new List<IPEndPoint> { src.EndPoint };
                var po = Po(1); po.UseTrackers = true;
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, null, null, po, CancellationToken.None);
                Assert.Equal(LiveVerdict.Alive, r.Verdict);
                Assert.Equal(src.EndPoint, r.GoodPeers.Single());
                Assert.True(await WaitUntil(() => tracker.Announces >= 2, 3000), "announces: " + tracker.Announces);      // one to ask for peers and another (stopped) to unregister
            }
        }

        [Fact]
        public async Task TrackersCanBeSwitchedOff()
        {
            using (var tracker = new AceEngine.Tests.Probe.FakeScrapeUdpTracker())
            using (var src = new LiveSourceSimulator(new LiveStreamModel(new LiveSimOptions { TimeScale = 4, Trackers = tracker.Url })))
            {
                tracker.AnnouncePeers = new List<IPEndPoint> { src.EndPoint };
                var po = Po(1, 3); po.UseTrackers = false;
                var r = await new LiveProber(Opt(), null).ProbeAsync(src.Model.Descriptor, null, null, po, CancellationToken.None);
                Assert.Equal(0, tracker.Announces);
                Assert.Equal(LiveVerdict.Unknown, r.Verdict);                  // with no candidate there is nothing to conclude
            }
        }

        private static async Task<bool> WaitUntil(Func<bool> c, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (c()) return true; await Task.Delay(30); }
            return c();
        }

        [Fact]
        public async Task StopsAsSoonAsTheTargetNumberOfPeersIsReached()
        {
            var m = Model();
            var srcs = Enumerable.Range(0, 6).Select(i => new LiveSourceSimulator(m, new SimPeerBehavior { NodeType = 1 })).ToList();
            try
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, srcs.Select(s => s.EndPoint), null, Po(3, 12), CancellationToken.None);
                Assert.Equal(LiveVerdict.Alive, r.Verdict);
                Assert.True(r.ActivePeers >= 3, r.ToString());
                Assert.True(r.Saturated);
                Assert.True(r.Seconds < 8, "without waiting for the deadline: " + r.Seconds);
            }
            finally { foreach (var s in srcs) s.Dispose(); }
        }

        [Fact]
        public async Task PeersThatCloseRightAfterTheExtendAreNotCountedAsAlive()
        {
            var m = Model();
            using (var full = new LiveSourceSimulator(m, new SimPeerBehavior { CloseAfterExtend = true, NodeType = 1 }))
            {
                var prober = new LiveProber(Opt(), null);
                var r = await prober.ProbeAsync(m.Descriptor, new[] { full.EndPoint }, null, Po(1, 4), CancellationToken.None);
                Assert.NotEqual(LiveVerdict.Alive, r.Verdict);
                Assert.Equal(0, r.ActivePeers);
            }
        }

        [Fact]
        public async Task ADescriptorWithoutBitrateCannotBeProbed()
        {
            var prober = new LiveProber(Opt(), null);
            var r = await prober.ProbeAsync(null, null, null, Po(), CancellationToken.None);
            Assert.Equal(LiveVerdict.Unknown, r.Verdict);
        }

        [Fact]
        public async Task CancellationPropagates()
        {
            var m = Model();
            using (var cts = new CancellationTokenSource())
            using (var slow = new LiveSourceSimulator(m, new SimPeerBehavior { HandshakeDelayMs = 4000 }))
            {
                var prober = new LiveProber(Opt(), null);
                var t = prober.ProbeAsync(m.Descriptor, new[] { slow.EndPoint }, null, Po(1, 20), cts.Token);
                cts.CancelAfter(300);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
            }
        }
    }

    public class PeerHintStoreTests
    {
        private static IPEndPoint Ep(int a, int port = 8621) { return new IPEndPoint(IPAddress.Parse("10.0.0." + a), port); }

        [Fact]
        public void KeepsTheMostRecentFirstWithoutDuplicates()
        {
            var s = new PeerHintStore();
            s.Add("k", new[] { Ep(1), Ep(2) });
            s.Add("k", new[] { Ep(3), Ep(1) });
            var l = s.Get("k");
            Assert.Equal(new[] { Ep(1), Ep(3), Ep(2) }, l.ToArray());
        }

        [Fact]
        public void BoundsPeersPerKeyAndKeys()
        {
            var s = new PeerHintStore(maxKeys: 3, maxPerKey: 4);
            s.Add("a", Enumerable.Range(1, 10).Select(i => Ep(i)));
            Assert.Equal(4, s.Get("a").Count);
            Assert.Equal(Ep(10), s.Get("a")[0]);
            var clock = new[] { DateTime.UtcNow };
            var s2 = new PeerHintStore(maxKeys: 3, maxPerKey: 4, utcNow: () => clock[0]);
            for (int i = 0; i < 5; i++) { clock[0] = clock[0].AddSeconds(1); s2.Add("k" + i, new[] { Ep(i + 1) }); }
            Assert.Equal(3, s2.Count);
            Assert.Empty(s2.Get("k0"));
            Assert.Single(s2.Get("k4"));
        }

        [Fact]
        public void EntriesExpire()
        {
            var now = DateTime.UtcNow;
            var s = new PeerHintStore(TimeSpan.FromMinutes(10), utcNow: () => now);
            s.Add("k", new[] { Ep(1) });
            now = now.AddMinutes(9);
            Assert.Single(s.Get("k"));
            now = now.AddMinutes(2);
            Assert.Empty(s.Get("k"));
        }

        [Fact]
        public void SurvivesAJsonRoundTripAndDropsExpiredOrCorruptEntries()
        {
            var now = DateTime.UtcNow;
            var s = new PeerHintStore(TimeSpan.FromHours(3), utcNow: () => now);
            s.Add("aa", new[] { Ep(1), Ep(2, 8650) });
            now = now.AddHours(2);
            s.Add("bb", new[] { Ep(3) });
            var json = s.ToJson();
            var s2 = new PeerHintStore(TimeSpan.FromHours(3), utcNow: () => now);
            Assert.Equal(2, s2.LoadJson(json));
            Assert.Equal(new[] { Ep(2, 8650), Ep(1) }, s2.Get("aa").ToArray());
            now = now.AddHours(2);                                               // "aa" is already 4 h old, "bb" 2 h
            var s3 = new PeerHintStore(TimeSpan.FromHours(3), utcNow: () => now);
            Assert.Equal(1, s3.LoadJson(json));
            Assert.Empty(s3.Get("aa"));
            Assert.Single(s3.Get("bb"));
            Assert.Equal(0, new PeerHintStore().LoadJson("{no es json"));
            Assert.Equal(0, new PeerHintStore().LoadJson("{\"v\":1,\"keys\":[{\"k\":\"x\",\"t\":1,\"p\":[\"no-es-un-par\"]}]}"));
        }

        [Fact]
        public void IgnoresGarbageAndMissingKeys()
        {
            var s = new PeerHintStore();
            s.Add(null, new[] { Ep(1) });
            s.Add("k", new IPEndPoint[] { null, new IPEndPoint(IPAddress.Any, 1), new IPEndPoint(IPAddress.Parse("1.2.3.4"), 0) });
            Assert.Empty(s.Get("k"));
            Assert.Empty(s.Get("nada"));
            Assert.Empty(s.Get(null));
        }
    }
}
