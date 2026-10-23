using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Sim;
using AceEngine.Tests.Live;
using AceEngine.Tests;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Peers
{
    /// <summary>Connection strategy with the peers of a live stream: measured funnel, full nodes, non-Ace clients, peer hints and network penalization.</summary>
    [Collection(AceEngine.Tests.RealtimeCollection.Name)]
    public class PeerStrategyTests
    {
        private static async Task<bool> WaitFor(Func<bool> cond, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }

        [Fact]
        public async Task TheFunnelCountsEveryStageOfAGoodPeer()
        {
            var sim = new LiveSimOptions { TimeScale = 4 };
            using (var h = new LiveHarness(sim))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                var v = await h.ReadAsync(4);
                Assert.True(v.Bytes > 100000);
                var f = h.Session.Funnel;
                Assert.Equal(1, f.Tried);
                Assert.Equal(1, f.TcpOk);
                Assert.Equal(1, f.HandshakeOk);
                Assert.Equal(1, f.ExtendOk);
                Assert.Equal(1, f.HaveSeen);
                Assert.Equal(1, f.RecentHave);
                Assert.Equal(1, f.Unchoked);
                Assert.Equal(1, f.Served);
                Assert.True(f.LivePosAdvances > 0, f.Describe());
                Assert.True(f.FirstHandshakeSeconds >= 0 && f.FirstExtendSeconds >= f.FirstHandshakeSeconds);
                Assert.True(f.FirstPieceSeconds >= f.FirstUnchokeSeconds);
                Assert.True(f.PlayableSeconds > 0);
                var tr = f.Peers.Single();
                Assert.Equal(LiveStage.Served, tr.Stage);
                Assert.Equal(2, tr.Nt);                       // the simulator is a source
                Assert.Equal(1, tr.Accessible);
                Assert.True(tr.Pieces > 0 && tr.Bytes > 0);
                Assert.Equal("open", tr.CloseClass);
                Assert.Contains("StaticPeers", f.CandidatesBySource.Keys);
            }
        }

        [Fact]
        public async Task ClosedPortsAndSilentPeersAreClassified()
        {
            var sim = new LiveSimOptions { TimeScale = 4 };
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int dead = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            using (var h = new LiveHarness(sim, o => { o.HandshakeTimeout = TimeSpan.FromSeconds(1); o.Live.HandshakeTimeout = TimeSpan.FromSeconds(1); o.StaticPeers.Add("127.0.0.1:" + dead); }))
            {
                h.AddSource();
                var slow = h.AddSource(new SimPeerBehavior { HandshakeDelayMs = 5000 });
                await h.OpenAsync();
                await h.ReadAsync(3);
                var f = h.Session.Funnel;
                Assert.True(f.CloseClasses.ContainsKey("hs_timeout"), f.Describe());
                Assert.True(f.TcpOk >= 2);
                Assert.Equal(1, f.HandshakeOk);
            }
        }

        private static LiveHarness FullPeerHarness(bool backoff)
        {
            var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }, o => { o.Live.FullPeerBackoff = backoff; });
            return h;
        }

        [Fact]
        public async Task AFullNodeThatClosesAfterTheExtendIsNotRedialedEveryTwoSeconds()
        {
            using (var h = FullPeerHarness(true))
            {
                h.AddSource();
                var full = h.AddSource(new SimPeerBehavior { CloseAfterExtend = true, NodeType = 1 });
                await h.OpenAsync();
                await h.ReadAsync(7);
                Assert.True(full.Connections >= 1);
                Assert.True(full.Connections <= 2, "the full node received " + full.Connections + " connections in 7 s");
                var f = h.Session.Funnel;
                Assert.True(f.CloseClasses.ContainsKey("after_extend"), f.Describe());
            }
        }

        [Fact]
        public async Task WithoutTheBackoffAFullNodeIsRedialedEveryTwoSeconds()
        {
            using (var h = FullPeerHarness(false))
            {
                h.AddSource();
                var full = h.AddSource(new SimPeerBehavior { CloseAfterExtend = true, NodeType = 1 });
                await h.OpenAsync();
                await h.ReadAsync(7);
                Assert.True(full.Connections >= 3, "without backoff it is dialed again: " + full.Connections);
            }
        }

        /// <summary>Peer that speaks plain BitTorrent: handshake with the infohash, EXTEND without nt/pv/v and a standard BITFIELD. Counts the connections.</summary>
        private sealed class PlainBtPeer : IDisposable
        {
            private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
            private readonly byte[] _infoHash;
            public int Connections;
            public PlainBtPeer(byte[] infoHash)
            {
                _infoHash = infoHash;
                _l.Start();
                Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = await _l.AcceptTcpClientAsync(); } catch (Exception) { return; }
                        Interlocked.Increment(ref Connections);
                        var cc = c;
                        var ignored = Task.Run(async () =>
                        {
                            try
                            {
                                var s = cc.GetStream();
                                var buf = new byte[68];
                                int got = 0;
                                while (got < 68) { int n = await s.ReadAsync(buf, got, 68 - got); if (n <= 0) return; got += n; }
                                var hs = Wire.BuildHandshake(new byte[] { 0, 0, 0, 0, 0, 0x10, 0, 0 }, _infoHash, PeerId.Generate("-TR2940-"));
                                await s.WriteAsync(hs, 0, hs.Length);
                                var ext = Wire.Extended(0, System.Text.Encoding.ASCII.GetBytes("d1:md11:ut_metadatai1ee1:pi6881ee"));
                                await s.WriteAsync(ext, 0, ext.Length);
                                var bf = new byte[] { 0, 0, 0, 3, 5, 0xff, 0xff };            // BitTorrent BITFIELD (2 bytes) which in a live stream is "bad bitfield"
                                await s.WriteAsync(bf, 0, bf.Length);
                                await Task.Delay(500);
                            }
                            catch (Exception) { }
                            finally { try { cc.Close(); } catch (Exception) { } }
                        });
                    }
                });
            }
            public IPEndPoint EndPoint { get { return (IPEndPoint)_l.LocalEndpoint; } }
            public void Dispose() { _l.Stop(); }
        }

        private static async Task<double> SecondsToPlayableWithASlowAndAFastPeer(bool hedge)
        {
            var sim = new LiveSimOptions { TimeScale = 1, PieceLength = 262144, Bitrate = 524288 };          // 2 pieces/s: prebuffer of 20 pieces of 256 KiB
            using (var h = new LiveHarness(sim, o => { o.Live.HedgeSlowPieces = hedge; o.Live.PrebufferTimeoutSeconds = 120; }))
            {
                for (int i = 0; i < 5; i++) h.AddSource(new SimPeerBehavior { NodeType = 1, BytesPerSecond = 32768 });          // five slow but steady ones (8 s per 256 KiB piece, always making progress): they keep half of the prebuffer pieces
                h.AddSource(new SimPeerBehavior { NodeType = 1 });
                await h.OpenAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(60))
                {
                    if (h.Session.LiveInfo.Playable) return sw.Elapsed.TotalSeconds;
                    await Task.Delay(50);
                }
                return 999;
            }
        }

        [Fact]
        public async Task HedgingSlowPiecesLetsThePrebufferFinishWithTheFastPeer()
        {
            double with = await SecondsToPlayableWithASlowAndAFastPeer(true);
            double without = await SecondsToPlayableWithASlowAndAFastPeer(false);
            Assert.True(with < 7, "with the redundant request: " + with);
            Assert.True(without > with + 5, "without it the prebuffer waits for the slow peers: " + without + " versus " + with);
        }

        /// <summary>Peers that accept the TCP and never answer the handshake (what a NAT or a hung peer does to a live stream): the good one goes last.</summary>
        private static async Task<double> SecondsToFirstExtendAmongSilentPeers(bool legacy, int silent)
        {
            var listeners = new List<TcpListener>();
            var held = new List<TcpClient>();
            var model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4 });
            var good = new LiveSourceSimulator(model);
            var file = Path.Combine(Path.GetTempPath(), "silent-" + Guid.NewGuid().ToString("N") + ".acelive");
            File.WriteAllBytes(file, model.TransportBytes);
            try
            {
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false, HandshakeTimeout = TimeSpan.FromSeconds(6), ConnectTimeout = TimeSpan.FromSeconds(6) };
                if (legacy) { opt.Live.ConnectTimeout = TimeSpan.Zero; opt.Live.HandshakeTimeout = TimeSpan.Zero; opt.Live.MaxPendingConnects = 16; }
                else { opt.Live.ConnectTimeout = TimeSpan.FromSeconds(2); opt.Live.HandshakeTimeout = TimeSpan.FromSeconds(2); opt.Live.MaxPendingConnects = 48; }
                for (int i = 0; i < silent; i++)
                {
                    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); listeners.Add(l);
                    var cl = l;
                    var ignored = Task.Run(async () => { try { while (true) { var c = await cl.AcceptTcpClientAsync(); lock (held) held.Add(c); } } catch (Exception) { } });   // they are kept: if the collector closed the socket the peer would no longer be "hung"
                    opt.StaticPeers.Add(l.LocalEndpoint.ToString());
                }
                opt.StaticPeers.Add(good.EndPoint.ToString());                  // the only good one, the last in the queue
                using (var host = new AceEngineHost(opt))
                {
                    host.Start();
                    var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), CancellationToken.None);
                    var session = (LiveSession)opened.Session;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(40))
                    {
                        if (session.Funnel.ExtendOk >= 1) return sw.Elapsed.TotalSeconds;
                        await Task.Delay(50);
                    }
                    return 999;
                }
            }
            finally { good.Dispose(); foreach (var l in listeners) { try { l.Stop(); } catch (Exception) { } } lock (held) foreach (var c in held) { try { c.Close(); } catch (Exception) { } } try { File.Delete(file); } catch (Exception) { } }
        }

        [Fact]
        public async Task ASparseSwarmIsFoundMuchFasterWithShortTimeoutsAndMoreConnectionSlots()
        {
            double legacy = await SecondsToFirstExtendAmongSilentPeers(true, 40);
            double now = await SecondsToFirstExtendAmongSilentPeers(false, 40);
            Assert.True(now < 5, "with 2 s deadlines and 48 slots: " + now);
            Assert.True(legacy > 12, "with 16 slots and 6 s per hung peer (3 rounds): " + legacy);
        }

        [Fact]
        public async Task AClientThatIsNotAnAceEngineIsDroppedForTheWholeSession()
        {
            var model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4 });
            using (var good = new LiveSourceSimulator(model))
            using (var plain = new PlainBtPeer(model.InfoHash))
            {
                var file = Path.Combine(Path.GetTempPath(), "np-" + Guid.NewGuid().ToString("N") + ".acelive");
                File.WriteAllBytes(file, model.TransportBytes);
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
                opt.Live.BanNonAcePeers = true;
                opt.StaticPeers.Add(plain.EndPoint.ToString());
                opt.StaticPeers.Add(good.EndPoint.ToString());
                try
                {
                    using (var host = new AceEngineHost(opt))
                    {
                        host.Start();
                        var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), CancellationToken.None);
                        var session = (LiveSession)opened.Session;
                        await Task.Delay(6000);
                        Assert.True(plain.Connections >= 1);
                        Assert.True(plain.Connections <= 1, "the non-Ace client received " + plain.Connections + " connections");
                        Assert.True(session.Funnel.ExtendOk >= 1);
                    }
                }
                finally { try { File.Delete(file); } catch (Exception) { } }
            }
        }

        [Fact]
        public async Task KnownPeersOfAChannelAreTriedFirstEvenIfTheDescriptorCameFromTheCache()
        {
            var model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4 });
            using (var src = new LiveSourceSimulator(model))
            {
                var file = Path.Combine(Path.GetTempPath(), "hint-" + Guid.NewGuid().ToString("N") + ".acelive");
                File.WriteAllBytes(file, model.TransportBytes);
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
                opt.Live.UsePeerHints = true;
                try
                {
                    using (var host = new AceEngineHost(opt))
                    {
                        host.Start();
                        host.Resolver.Hints.Add(Hex.ToHex(model.InfoHash), new[] { src.EndPoint });          // what the live probe left behind
                        var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), CancellationToken.None);
                        var session = (LiveSession)opened.Session;
                        Assert.True(await WaitFor(() => session.Funnel.ExtendOk >= 1, 6000), session.Funnel.Describe());
                        Assert.Contains("resolution", session.Funnel.CandidatesBySource.Keys);
                    }
                }
                finally { try { File.Delete(file); } catch (Exception) { } }
            }
        }

        [Fact]
        public async Task WithoutHintsThereIsNobodyToCallAndNothingHappens()
        {
            var model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4 });
            using (var src = new LiveSourceSimulator(model))
            {
                var file = Path.Combine(Path.GetTempPath(), "nohint-" + Guid.NewGuid().ToString("N") + ".acelive");
                File.WriteAllBytes(file, model.TransportBytes);
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
                opt.Live.UsePeerHints = false;
                try
                {
                    using (var host = new AceEngineHost(opt))
                    {
                        host.Start();
                        host.Resolver.Hints.Add(Hex.ToHex(model.InfoHash), new[] { src.EndPoint });
                        var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), CancellationToken.None);
                        await Task.Delay(1500);
                        Assert.Equal(0, src.Connections);
                    }
                }
                finally { try { File.Delete(file); } catch (Exception) { } }
            }
        }

        [Fact]
        public async Task PeersThatServedPiecesAreRememberedWhenTheSessionEnds()
        {
            var model = new LiveStreamModel(new LiveSimOptions { TimeScale = 4 });
            using (var src = new LiveSourceSimulator(model))
            {
                var file = Path.Combine(Path.GetTempPath(), "rem-" + Guid.NewGuid().ToString("N") + ".acelive");
                File.WriteAllBytes(file, model.TransportBytes);
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
                opt.Live.UsePeerHints = true;
                opt.StaticPeers.Add(src.EndPoint.ToString());
                try
                {
                    using (var host = new AceEngineHost(opt))
                    {
                        host.Start();
                        var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), CancellationToken.None);
                        var session = (LiveSession)opened.Session;
                        Assert.True(await WaitFor(() => session.Funnel.HaveSeen >= 1, 6000));
                        session.Dispose();
                        var hints = host.Resolver.Hints.Get(Hex.ToHex(model.InfoHash));
                        Assert.Contains(src.EndPoint, hints);
                    }
                }
                finally { try { File.Delete(file); } catch (Exception) { } }
            }
        }

        private static LiveSwarm NewSwarm(Action<LiveOptions> tweak)
        {
            var model = new LiveStreamModel(new LiveSimOptions());
            var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            tweak(opt.Live);
            return new LiveSwarm(opt, model.Descriptor, model.TransportBytes, null, null, null, () => 0, NodeKeys.Generate());
        }

        private static void Fail(LiveSwarm sw, IPEndPoint ep, string reason)
        {
            var p = new LivePeer(ep, false) { Connecting = true };
            p.Trace.Reach(LiveStage.Connecting);
            sw.OnPeerClosed(p, reason);
        }

        private static AceEngine.Live.LiveExtendInfo OurExtend(LiveSwarm sw)
        {
            var frame = sw.BuildExtendFrame(new LivePeer(new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621), false));
            // frame = 4 bytes of length + [20, 0] + bencode; TryParse expects msg[0] = 20
            var msg = new byte[frame.Length - 4];
            Buffer.BlockCopy(frame, 4, msg, 0, msg.Length);
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(msg, out info));
            return info;
        }

        [Fact]
        public void TheExtendTellsTheTruthAboutAccessibilityAndUpload()
        {
            var plain = OurExtend(NewSwarm(l => { }));
            Assert.Equal(0, plain.Mi.GetInt("is_accessible"));
            Assert.Equal(0, plain.Mi.GetInt("top_up_rate"));
            Assert.Equal(1, plain.Nt);
            Assert.Equal("bt", plain.Tt);

            var upnp = OurExtend(NewSwarm(l => l.AccessibleProvider = () => true));              // the port was opened by UPnP / NAT-PMP / a forward
            Assert.Equal(1, upnp.Mi.GetInt("is_accessible"));

            var claim = OurExtend(NewSwarm(l => l.ClaimAccessible = true));
            Assert.Equal(1, claim.Mi.GetInt("is_accessible"));
        }

        [Fact]
        public void TheUploadCapIsOnlyAdvertisedWhenAskedAndWhenUploadIsOn()
        {
            Func<bool, int, long, LiveExtendInfo> make = (advertise, slots, cap) =>
            {
                var model = new LiveStreamModel(new LiveSimOptions());
                var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false, UploadMaxBytesPerSec = cap };
                opt.Live.AdvertiseUploadCap = advertise; opt.Live.UploadSlots = slots;
                return OurExtend(new LiveSwarm(opt, model.Descriptor, model.TransportBytes, null, null, null, () => 0, NodeKeys.Generate()));
            };
            Assert.Equal(0, make(false, 2, 2097152).Mi.GetInt("top_up_rate"));
            Assert.Equal(2097152, make(true, 2, 2097152).Mi.GetInt("top_up_rate"));
            Assert.Equal(2097152, make(true, 2, 2097152).Mi.GetInt("top_session_up_rate"));
            Assert.Equal(0, make(true, 0, 2097152).Mi.GetInt("top_up_rate"));                // without upload nothing is offered
            Assert.Equal(0, make(true, 2, 0).Mi.GetInt("top_up_rate"));                      // without a configured limit no number is made up either
        }

        [Fact]
        public void CandidatesFromANetworkThatNeverAnswersGoToTheBack()
        {
            var sw = NewSwarm(l => l.NetworkPenaltyAfter = 3);
            var bad = Enumerable.Range(1, 10).Select(i => new IPEndPoint(IPAddress.Parse("104.28.0." + i), 8621)).ToList();
            var good = new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621);
            sw.AddCandidates(bad, "tracker");
            sw.AddCandidates(new[] { good }, "tracker");
            Assert.Equal(bad[0], sw.NextCandidatesForTest(1)[0]);                      // no history: arrival order
            for (int i = 0; i < 3; i++) Fail(sw, bad[i], "connect timeout");
            var next = sw.NextCandidatesForTest(20);
            Assert.Equal(good, next[0]);                                               // the network with no responses goes to the end
            Assert.Equal(8, next.Count);                                               // the three failed ones wait for their backoff
        }

        [Fact]
        public void WithoutThePenaltyTheOrderIsTheArrivalOrder()
        {
            var sw = NewSwarm(l => l.NetworkPenaltyAfter = 0);
            var bad = Enumerable.Range(1, 10).Select(i => new IPEndPoint(IPAddress.Parse("104.28.0." + i), 8621)).ToList();
            var good = new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621);
            sw.AddCandidates(bad, "tracker");
            sw.AddCandidates(new[] { good }, "tracker");
            for (int i = 0; i < 3; i++) Fail(sw, bad[i], "connect timeout");
            Assert.Equal(bad[3], sw.NextCandidatesForTest(1)[0]);
        }

        [Fact]
        public void AKnownPeerBeatsAnyAnnouncedOne()
        {
            var sw = NewSwarm(l => { });
            var announced = new IPEndPoint(IPAddress.Parse("85.54.18.2"), 8621);
            var known = new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621);
            sw.AddCandidates(new[] { announced }, "tracker");
            sw.AddCandidates(new[] { known }, "resolution", true);
            Assert.Equal(known, sw.NextCandidatesForTest(1)[0]);
        }

        [Fact]
        public void ANetworkWithOneAnswerIsNeverPenalized()
        {
            var sw = NewSwarm(l => l.NetworkPenaltyAfter = 3);
            var net = Enumerable.Range(1, 8).Select(i => new IPEndPoint(IPAddress.Parse("85.54.0." + i), 8621)).ToList();
            var other = new IPEndPoint(IPAddress.Parse("62.1.1.1"), 8621);
            sw.AddCandidates(net, "tracker");
            sw.AddCandidates(new[] { other }, "tracker");
            var ok = new LivePeer(net[0], false) { Connecting = true };
            ok.Trace.Reach(LiveStage.TcpOk);
            sw.OnPeerClosed(ok, "closed");
            for (int i = 1; i < 5; i++) Fail(sw, net[i], "connect timeout");
            Assert.Equal(net[5], sw.NextCandidatesForTest(1)[0]);                      // the 85.54 network answered once: it stays in its place
        }

        [Fact]
        public async Task ACachedDescriptorStillComesWithThePeersThatServedItBefore()
        {
            const string id = "2940120bf034db79a7f5849846ccea0255172eae";
            var opt = new EngineOptions { EnableDht = false, EnableTrackers = false };
            opt.Resolver.EnableP2p = false; opt.Resolver.EnableTsService = false;
            using (var r = new AceEngine.Resolve.TransportResolver(opt, null))
            {
                r.Cache.Put(id, TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive"));
                var none = await r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None, true);
                Assert.Equal("local_db", none.Source);
                Assert.True(none.GoodPeers == null || none.GoodPeers.Count == 0);
                var peer = new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621);
                r.Hints.Add(id, new[] { peer });
                var again = await r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None, true);
                Assert.Equal("local_db", again.Source);
                Assert.Equal(new[] { peer }, again.GoodPeers.ToArray());
            }
        }

        [Fact]
        public void HintsAreSavedAndLoadedFromTheConfiguredFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "hints-" + Guid.NewGuid().ToString("N") + ".json");
            var peer = new IPEndPoint(IPAddress.Parse("85.54.18.1"), 8621);
            try
            {
                var opt = new EngineOptions { EnableDht = false, EnableTrackers = false };
                opt.Resolver.HintsPath = path;
                using (var r = new AceEngine.Resolve.TransportResolver(opt, null)) { r.Hints.Add("2940120bf034db79a7f5849846ccea0255172eae", new[] { peer }); r.SaveHintsNow(); }
                Assert.True(File.Exists(path));
                using (var r2 = new AceEngine.Resolve.TransportResolver(opt, null))
                    Assert.Equal(new[] { peer }, r2.Hints.Get("2940120bf034db79a7f5849846ccea0255172eae").ToArray());
            }
            finally { try { File.Delete(path); } catch (Exception) { } }
        }

        [Fact]
        public void TheTvProfileLimitsTheSocketsOfTheLiveProbe()
        {
            var o = AceEngine.App.EngineOptionsFactory.Build(new AceEngine.App.AppConfig(), null);
            Assert.True(o.Probe.VerifyLive);
            Assert.True(o.Probe.LiveVerifyConcurrency * o.Probe.LiveProbe.MaxConnections <= 80, "sockets of the live probe");
        }
    }
}
