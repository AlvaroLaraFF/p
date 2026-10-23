using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Links;
using AceEngine.Probe;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Live;
using AceEngine.Tests.Peers;
using AceEngine.Tests.Tv2;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>
    /// The player and the channel checker talk to each other. One way: no sources after 15 s, a stopped broadcaster or a lost session = the channel stops
    /// counting as verified, its card is updated with the checker's rule and it joins the retries. The other way: stream arrives = fresh verification (10 min).
    /// </summary>
    public class PlaybackEvidencePolicyTests
    {
        private static LinkEntry E(int i) { return LinkEntry.FromUrl("Canal " + i, "acestream://" + i.ToString("x40"), "G"); }

        private static ProbeResult Shown(int peers = 3, int strikes = 0, bool saturated = false)
        {
            return new ProbeResult
            {
                State = LinkState.Good, Peers = peers, Method = "live", BitrateKbps = 800, InfoHash = "ab", Source = "cache", TimeUtc = DateTime.UtcNow.AddMinutes(-3), Rounds = 1,
                Live = new LiveProbeResult { Verdict = LiveVerdict.Alive, ActivePeers = peers, Saturated = saturated, EstimatedConnectable = 40, Strikes = strikes, TimeUtc = DateTime.UtcNow.AddMinutes(-3) }
            };
        }

        private static ProbeResult NotShown()
        {
            return new ProbeResult { State = LinkState.Unknown, Error = "sin piezas", TimeUtc = DateTime.UtcNow.AddMinutes(-1), Live = new LiveProbeResult { Verdict = LiveVerdict.NoPieces } };
        }

        private static PlaybackEvidence Ev(int i, PlaybackSignal s, int peers = 0, string detail = null) { return new PlaybackEvidence { Entry = E(i), Signal = s, Peers = peers, Detail = detail }; }

        [Fact]
        public void StreamArrivingIsAFreshVerificationOfAChannelNeverChecked()
        {
            var now = DateTime.UtcNow;
            var r = ProbeScheduler.ApplyPlayback(null, Ev(1, PlaybackSignal.Streaming, 2), new ProbeOptions(), now);
            Assert.Equal(LinkState.Good, r.State);
            Assert.True(r.LiveVerified);
            Assert.Equal(2, r.Peers); Assert.Equal(2, r.Live.ActivePeers); Assert.False(r.Live.Saturated);
            Assert.Equal(0, r.Live.Strikes);
            Assert.Equal(now, r.TimeUtc); Assert.Equal(now, r.Live.TimeUtc);
            Assert.Equal("playback", r.Method);
            // the card uses the same scale as the probe: 1, 2, 3, 4+
            var many = ProbeScheduler.ApplyPlayback(null, Ev(1, PlaybackSignal.Streaming, 11), new ProbeOptions(), now);
            Assert.Equal(4, many.Live.ActivePeers); Assert.True(many.Live.Saturated);
            Assert.Equal("4+", LiveChecker.Map(many).PeersText);
            var none = ProbeScheduler.ApplyPlayback(null, Ev(1, PlaybackSignal.Streaming, 0), new ProbeOptions(), now);
            Assert.Equal(1, none.Live.ActivePeers);                                      // stream arrived: at least one peer
        }

        [Fact]
        public void StreamTurnsAHiddenChannelIntoAvailableAndClearsTheStrike()
        {
            var r = ProbeScheduler.ApplyPlayback(NotShown(), Ev(1, PlaybackSignal.Streaming, 3), new ProbeOptions(), DateTime.UtcNow);
            Assert.Equal(LinkState.Good, r.State); Assert.True(r.LiveVerified); Assert.Null(r.Error);
            var held = ProbeScheduler.ApplyPlayback(Shown(2, 1), Ev(1, PlaybackSignal.Streaming, 9), new ProbeOptions(), DateTime.UtcNow);
            Assert.Equal(0, held.Live.Strikes);                                           // the streak of negatives is cleared
            Assert.Equal(2, held.Peers);                                                  // and the probe's figure is kept (the session's is on a different scale)
            Assert.Equal(800, held.BitrateKbps); Assert.Equal("ab", held.InfoHash);       // and what was known about the descriptor too
            Assert.True((DateTime.UtcNow - held.TimeUtc).TotalSeconds < 5);
        }

        [Theory]
        [InlineData(PlaybackSignal.NoSource, LinkState.Unknown)]
        [InlineData(PlaybackSignal.SessionLost, LinkState.Unknown)]
        [InlineData(PlaybackSignal.Stalled, LinkState.Dead)]
        public void ARealFailureRemovesTheCardAtOnceWithTheDefaultRule(PlaybackSignal signal, LinkState expected)
        {
            var o = new ProbeOptions();
            Assert.Equal(2, o.PlaybackFailureStrikes);
            var r = ProbeScheduler.ApplyPlayback(Shown(3), Ev(1, signal, 0, "15 s sin datos"), o, DateTime.UtcNow);
            Assert.Equal(expected, r.State);
            Assert.False(r.LiveVerified);                                                 // it stops counting as verified
            Assert.NotNull(r.Error);
            var status = LiveChecker.Map(r);
            Assert.False(status.IsLive); Assert.False(status.Verified);
            Assert.Equal(800, r.BitrateKbps);                                             // and what was known about the channel is not lost
        }

        [Fact]
        public void WithOneStrikePerFailureTheFirstKeepsTheCardAndTheSecondRemovesIt()
        {
            var o = new ProbeOptions { PlaybackFailureStrikes = 1 };
            var first = ProbeScheduler.ApplyPlayback(Shown(3), Ev(1, PlaybackSignal.NoSource), o, DateTime.UtcNow);
            Assert.Equal(LinkState.Good, first.State);                                    // a negative does not remove the card
            Assert.True(first.LiveVerified);
            Assert.Equal(1, first.Live.Strikes);
            Assert.Equal(3, first.Peers);
            Assert.Equal(DateTime.MinValue, first.TimeUtc);                               // but it no longer counts as fresh: it is looked at again on the next pass
            var second = ProbeScheduler.ApplyPlayback(first, Ev(1, PlaybackSignal.NoSource), o, DateTime.UtcNow);
            Assert.Equal(LinkState.Unknown, second.State);                                // two in a row do
            Assert.False(second.LiveVerified);
        }

        [Fact]
        public void AFailureOfAChannelThatWasNotShownChangesNothing()
        {
            var o = new ProbeOptions();
            Assert.Null(ProbeScheduler.ApplyPlayback(null, Ev(1, PlaybackSignal.NoSource), o, DateTime.UtcNow));
            Assert.Null(ProbeScheduler.ApplyPlayback(NotShown(), Ev(1, PlaybackSignal.NoSource), o, DateTime.UtcNow));
        }
    }

    public class PlaybackFeedbackSchedulerTests
    {
        private static List<LinkEntry> Entries(int n) { return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList(); }
        private static CancellationToken Tok(int ms = 15000) { return new CancellationTokenSource(ms).Token; }
        private static PlaybackEvidence Ev(LinkEntry e, PlaybackSignal s, int peers = 0) { return new PlaybackEvidence { Entry = e, Signal = s, Peers = peers, Detail = "prueba" }; }

        [Fact]
        public async Task AChannelThatFailedInThePlayerLeavesTheTenMinuteCacheAndIsProbedAgainWhileItsScreenIsOpen()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(80), AvailableTtl = TimeSpan.FromMinutes(10) }, VerifyDelayMs = 10 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                Assert.True(s.GetResult(e).LiveVerified);
                await Task.Delay(500);
                Assert.Equal(1, fp.VerifyCalls);                                          // no warnings: the 10 min cache considers it good and nothing is touched
                var changes = new List<ProbeResult>();
                s.ResultChanged += (en, r) => { lock (changes) changes.Add(r); };
                fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };       // and there really is nothing anymore
                s.NotePlayback(Ev(e, PlaybackSignal.NoSource));
                Assert.False(s.GetResult(e).LiveVerified);                                // it stops counting as verified right away
                Assert.Equal(LinkState.Unknown, s.GetResult(e).State);
                Assert.True(await Wait.Until(() => fp.VerifyCalls >= 2, 5000), "the looped retry picks it up: " + fp.VerifyCalls);
                lock (changes) Assert.Contains(changes, c => c.Method == "playback");     // and the UI found out about the change
                Assert.False(s.GetResult(e).LiveVerified);
            }
        }

        [Fact]
        public async Task AStreamFromThePlayerIsAFreshVerificationSoTheChannelIsNotProbedAgain()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(60) }, VerifyDelayMs = 10 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };       // the probe sees nothing...
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                Assert.True(await Wait.Until(() => fp.VerifyCalls >= 3, 5000), "it is retried in a loop");
                s.NotePlayback(Ev(e, PlaybackSignal.Streaming, 3));                       // ...but the player receives stream
                var r = s.GetResult(e);
                Assert.Equal(LinkState.Good, r.State); Assert.True(r.LiveVerified); Assert.Equal(3, r.Peers);
                await Task.Delay(150);                                                    // in case there was a retry in flight
                int calls = fp.VerifyCalls;
                await Task.Delay(700);
                Assert.Equal(calls, fp.VerifyCalls);                                      // it is valid for 10 min: it is not probed again
                Assert.True(s.GetResult(e).LiveVerified);
            }
        }

        [Fact]
        public async Task WhatThePlayerSawWinsOverAProbeThatStartedBeforeAndFinishesAfter()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(60) }, VerifyDelayMs = 500 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };       // the (slow) probe will end up saying there is nothing
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                Assert.True(await Wait.Until(() => fp.Verifying == 1, 5000));              // the probe is in progress
                s.NotePlayback(Ev(e, PlaybackSignal.Streaming, 3));                       // meanwhile, the player receives stream
                Assert.True(s.GetResult(e).LiveVerified);
                await s.WaitFirstCycleAsync(Tok());                                       // the probe finishes...
                Assert.True(s.GetResult(e).LiveVerified, "...but its result, older than the notice, does not override what the player saw");
                Assert.Equal("playback", s.GetResult(e).Method);
                await Task.Delay(300);
                Assert.Equal(1, fp.VerifyCalls);                                          // and the channel is valid for 10 min: it is not probed again
            }
        }

        [Fact]
        public async Task WithOneStrikePerFailureAPlayerFailureAndOneNegativeProbeInARowRemoveTheCard()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(80), PlaybackFailureStrikes = 1 }, VerifyDelayMs = 10 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.Stale };
                s.NotePlayback(Ev(e, PlaybackSignal.NoSource));
                Assert.Equal(LinkState.Good, s.GetResult(e).State);                       // the first negative (the player's) does not remove it
                Assert.True(await Wait.Until(() => s.GetResult(e).State == LinkState.Dead, 5000), "the second one (the probe) does");
            }
        }

        [Fact]
        public async Task APlayerFailureOfAChannelThatCameBackIsHealedByTheNextProbe()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(60) }, VerifyDelayMs = 10 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var e = Entries(1)[0];
                s.Start(new[] { e });
                await s.WaitFirstCycleAsync(Tok());
                s.NotePlayback(Ev(e, PlaybackSignal.NoSource));
                Assert.False(s.GetResult(e).LiveVerified);
                Assert.True(await Wait.Until(() => s.GetResult(e).LiveVerified && s.GetResult(e).Method == "live", 5000), "the probe sees it again and the card comes back");
            }
        }
    }

    public class PlaybackFeedbackCheckerTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d) try { d.Dispose(); } catch { } }

        private ProbeScheduler Sched(FakeVerifyingProber p) { var s = new ProbeScheduler(p, p.Options); _d.Add(s); return s; }
        private LiveChecker Make(Func<ProbeScheduler> sched) { var c = new LiveChecker(sched); _d.Add(c); return c; }
        private static PlaybackEvidence Ev(Channel c, PlaybackSignal s, int peers = 0) { return new PlaybackEvidence { Entry = c.Entry, Signal = s, Peers = peers, Detail = "prueba" }; }

        [Fact]
        public async Task APlayerFailureTurnsTheCardOffAndRaisesTheStatusChange()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMinutes(5) }, VerifyDelayMs = 10 };
            var sched = Sched(fp);
            var checker = Make(() => sched);
            var chs = Enumerable.Range(1, 3).Select(i => Ch.Make(i)).ToList();
            var events = new List<KeyValuePair<string, ChannelStatus>>();
            checker.StatusChanged += (c, st) => { lock (events) events.Add(new KeyValuePair<string, ChannelStatus>(c.Id, st)); };
            checker.Check(chs);
            Assert.True(await Wait.Until(() => !checker.Running && checker.Done == 3, 10000));
            Assert.True(chs.All(c => checker.GetStatus(c).IsLive));
            lock (events) events.Clear();
            ((IPlaybackReporter)checker).Report(Ev(chs[1], PlaybackSignal.NoSource));
            Assert.True(await Wait.Until(() => !checker.GetStatus(chs[1]).IsLive, 5000), "the card of the channel that failed in the player is turned off");
            Assert.True(checker.GetStatus(chs[0]).IsLive && checker.GetStatus(chs[2]).IsLive);        // the others carry on
            Assert.False(checker.GetStatus(chs[1]).Verified);
            Assert.True(await Wait.Until(() => { lock (events) return events.Any(e => e.Key == chs[1].Id && !e.Value.IsLive); }, 5000), "and the UI finds out");
        }

        [Fact]
        public async Task AStreamFromThePlayerMakesAChannelNobodyCouldProbeAppearAndStayFreshForTenMinutes()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMilliseconds(60) }, VerifyDelayMs = 10 };
            fp.VerdictFn = t => new LiveProbeResult { Verdict = LiveVerdict.NoPieces };
            var sched = Sched(fp);
            var checker = Make(() => sched);
            var chs = new List<Channel> { Ch.Make(1) };
            var events = new List<ChannelStatus>();
            checker.StatusChanged += (c, st) => { lock (events) events.Add(st); };
            checker.Check(chs);
            Assert.True(await Wait.Until(() => fp.VerifyCalls >= 2, 10000));
            Assert.False(checker.GetStatus(chs[0]).IsLive);
            ((IPlaybackReporter)checker).Report(Ev(chs[0], PlaybackSignal.Streaming, 2));
            Assert.True(await Wait.Until(() => checker.GetStatus(chs[0]).IsLive, 5000));
            var st = checker.GetStatus(chs[0]);
            Assert.True(st.Verified); Assert.Equal(2, st.Peers);
            Assert.True(await Wait.Until(() => { lock (events) return events.Any(e => e.IsLive); }, 5000), "the card appears");
            await Task.Delay(200);
            int calls = fp.VerifyCalls;
            await Task.Delay(600);
            Assert.Equal(calls, fp.VerifyCalls);                                          // and it is not probed again
            Assert.True(checker.GetStatus(chs[0]).IsLive);
        }

        [Fact]
        public async Task ReportsBeforeAnyBatchLandInTheCacheAndTheNextBatchDoesNotProbeThatChannel()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions { Concurrency = 4, CycleInterval = TimeSpan.FromMinutes(5) }, VerifyDelayMs = 10 };
            var sched = Sched(fp);
            var checker = Make(() => sched);
            var chs = Enumerable.Range(1, 2).Select(i => Ch.Make(i)).ToList();
            ((IPlaybackReporter)checker).Report(Ev(chs[0], PlaybackSignal.Streaming, 3));            // the player opened the channel without anyone checking
            Assert.True(checker.GetStatus(chs[0]).IsLive);
            ((IPlaybackReporter)checker).Report(Ev(chs[1], PlaybackSignal.NoSource));                  // a failure of a channel nobody was showing changes nothing
            Assert.False(checker.GetStatus(chs[1]).IsLive);
            checker.Check(chs);
            Assert.True(await Wait.Until(() => !checker.Running && checker.Done == 2, 10000));
            Assert.Equal(1, fp.VerifyCalls);                                              // only the one that was not known was probed
            Assert.True(checker.GetStatus(chs[0]).IsLive);
        }

        [Fact]
        public void IgnoresGarbageAndAfterDisposeAndTheAsyncWrapperForwards()
        {
            var fp = new FakeVerifyingProber { Options = new ProbeOptions() };
            var sched = Sched(fp);
            var inner = new LiveChecker(() => sched);
            var async = new AsyncLiveChecker(inner);
            _d.Add(async);
            var c = Ch.Make(7);
            var reporter = (IPlaybackReporter)async;
            reporter.Report(null);
            reporter.Report(new PlaybackEvidence());
            reporter.Report(new PlaybackEvidence { Entry = LinkEntry.FromUrl("x", "ftp://nada"), Signal = PlaybackSignal.Streaming });
            reporter.Report(Ev(c, PlaybackSignal.Streaming, 1));
            Assert.True(inner.GetStatus(c).IsLive);                                       // the UI wrapper passes it to the real checker
            inner.Dispose();
            reporter.Report(Ev(c, PlaybackSignal.NoSource));                              // after Dispose it does nothing and does not throw
        }
    }

    public class PlaybackFeedbackSessionTests
    {
        private sealed class Recorder : IPlaybackReporter
        {
            public readonly List<PlaybackEvidence> Seen = new List<PlaybackEvidence>();
            public void Report(PlaybackEvidence e) { lock (Seen) Seen.Add(e); }
            public int Count(PlaybackSignal s) { lock (Seen) return Seen.Count(x => x.Signal == s); }
        }

        private static async Task<bool> Until(Func<bool> c, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(25); }
            return c();
        }

        private static LinkEntry EntryOf(string path)
        {
            return new LinkEntry { Name = "Canal", Url = path, Kind = LinkKind.AceStream, Link = AceLink.Parse(path), Key = "t:" + path };
        }

        private static PlaybackOptions Fast(double noSourceSeconds)
        {
            return new PlaybackOptions
            {
                RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(30), PrepareTimeout = TimeSpan.FromSeconds(3),
                OpenTimeout = TimeSpan.FromSeconds(noSourceSeconds), NoSourceRetryDelay = TimeSpan.FromMilliseconds(100), StreamingRenewal = TimeSpan.FromSeconds(1)
            };
        }

        [Fact]
        public async Task WhenStreamArrivesThePlayerTellsTheCheckerAndKeepsRenewingItWhileItPlays()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var rec = new Recorder();
                var entry = EntryOf(path);
                using (var pb = new PlaybackSession(h.Host, new FakePlayerFactory(), Fast(15), rec))
                {
                    var run = pb.RunAsync(entry, "Canal", CancellationToken.None);
                    Assert.True(await Until(() => rec.Count(PlaybackSignal.Streaming) >= 1, 20000), "it did not report that stream was arriving: " + pb.Status);
                    var first = rec.Seen[0];
                    Assert.Same(entry, first.Entry);
                    Assert.True(first.Peers >= 1);
                    Assert.Equal(0, rec.Count(PlaybackSignal.NoSource));
                    Assert.True(await Until(() => rec.Count(PlaybackSignal.Streaming) >= 3, 15000), "while playing, it renews the verification: " + rec.Count(PlaybackSignal.Streaming));
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task AChannelWithoutSourcesIsReportedEveryTimeItFailsAndNeverAsStreaming()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                var path = h.PrepareHost();                                              // the descriptor is there, but nobody serves the live stream
                var rec = new Recorder();
                using (var pb = new PlaybackSession(h.Host, new FakePlayerFactory(), Fast(1.0), rec))
                {
                    var run = pb.RunAsync(EntryOf(path), "Canal", CancellationToken.None);
                    Assert.True(await Until(() => rec.Count(PlaybackSignal.NoSource) >= 2, 25000), "it did not report that there are no sources: " + pb.Status + " / " + rec.Count(PlaybackSignal.NoSource));
                    Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);
                    Assert.Equal(0, rec.Count(PlaybackSignal.Streaming));
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task ABroadcasterThatStopsIsReportedAsStalled()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                var src = h.AddSource();
                var path = h.PrepareHost();
                var rec = new Recorder();
                var opt = Fast(15);
                opt.StaleLiveTimeout = TimeSpan.FromSeconds(2);
                opt.StallTimeout = TimeSpan.FromSeconds(60);
                using (var pb = new PlaybackSession(h.Host, new FakePlayerFactory(), opt, rec))
                {
                    var run = pb.RunAsync(EntryOf(path), "Canal", CancellationToken.None);
                    Assert.True(await Until(() => rec.Count(PlaybackSignal.Streaming) >= 1, 20000), "no llego a reproducir: " + pb.Status);
                    src.Behavior.Frozen = true;                                          // the broadcaster stops: the live piece no longer advances
                    Assert.True(await Until(() => rec.Count(PlaybackSignal.Stalled) >= 1, 25000), "it did not report that the live stream is not advancing: " + pb.Status);
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task TheWholeLoopPlayerToCheckerOnTheSameEngine()
        {
            // The player and a real LiveChecker on the same engine: when stream arrives the channel's card comes out available with fresh verification; when sources run out, it is turned off
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var entry = EntryOf(path);
                var ch = new Channel { Id = entry.Key, Name = entry.Name, SourceName = entry.Name, Entry = entry };
                using (var checker = new LiveChecker(() => h.Host))
                {
                    var events = new List<ChannelStatus>();
                    checker.StatusChanged += (c, st) => { lock (events) events.Add(st); };
                    Assert.False(checker.GetStatus(ch).IsLive);
                    using (var pb = new PlaybackSession(h.Host, new FakePlayerFactory(), Fast(15), checker))
                    {
                        var run = pb.RunAsync(entry, "Canal", CancellationToken.None);
                        Assert.True(await Until(() => checker.GetStatus(ch).IsLive, 20000), "the player's stream did not verify the channel: " + pb.Status);
                        Assert.True(checker.GetStatus(ch).Verified);
                        pb.Stop(); await run;
                    }
                    // another playback of the same channel with the broadcaster already stopped and no peers: no sources -> the checker turns the card off
                    using (var host2 = new AceEngine.P2P.AceEngineHost(new AceEngine.P2P.EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }))
                    {
                        host2.Start();
                        using (var pb2 = new PlaybackSession(host2, new FakePlayerFactory(), Fast(1.0), checker))
                        {
                            var run2 = pb2.RunAsync(entry, "Canal", CancellationToken.None);
                            Assert.True(await Until(() => !checker.GetStatus(ch).IsLive, 25000), "the card stays available after failing in the player: " + pb2.Status);
                            pb2.Stop(); await run2;
                        }
                    }
                }
            }
        }
    }
}
