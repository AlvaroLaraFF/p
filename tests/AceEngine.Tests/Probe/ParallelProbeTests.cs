using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Sim;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Resolve;
using AceEngine.Tests.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Probe
{
    internal sealed class FakeClock
    {
        public DateTime Now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan t) { Now += t; }
        public Func<DateTime> Func { get { return () => Now; } }
    }

    /// <summary>Simulated tracker for the count service: records each request (batch), its time and how many there are at once.</summary>
    internal sealed class FakeTransport : IScrapeTransport
    {
        public int Cap = 70;
        public int DelayMs = 0;
        public Func<string, IList<byte[]>, IList<ScrapeResult>> Handler;
        public readonly List<KeyValuePair<string, int>> Calls = new List<KeyValuePair<string, int>>();      // tracker -> batch size
        public readonly Dictionary<string, List<DateTime>> Starts = new Dictionary<string, List<DateTime>>();
        public readonly List<string> HashesSeen = new List<string>();
        private readonly Dictionary<string, int> _concurrent = new Dictionary<string, int>();
        public readonly Dictionary<string, int> MaxConcurrent = new Dictionary<string, int>();

        public int FitCount(string trackerUrl, IList<byte[]> infoHashes) { return Math.Min(Cap, infoHashes.Count); }


        public async Task<IList<ScrapeResult>> ScrapeAsync(string trackerUrl, IList<byte[]> infoHashes, CancellationToken ct)
        {
            string key = TrackerKey.From(trackerUrl);
            lock (this)
            {
                Calls.Add(new KeyValuePair<string, int>(key, infoHashes.Count));
                List<DateTime> l; if (!Starts.TryGetValue(key, out l)) Starts[key] = l = new List<DateTime>();
                l.Add(DateTime.UtcNow);
                int c; _concurrent.TryGetValue(key, out c); c++; _concurrent[key] = c;
                int m; MaxConcurrent.TryGetValue(key, out m); if (c > m) MaxConcurrent[key] = c;
                foreach (var h in infoHashes) HashesSeen.Add(Hex.ToHex(h));
            }
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, ct);
                if (Handler != null) return Handler(trackerUrl, infoHashes);
                return infoHashes.Select(h => new ScrapeResult { Tracker = trackerUrl, Status = ScrapeStatus.Ok, Seeders = h[0], Leechers = h[1] }).ToList();
            }
            finally { lock (this) _concurrent[key]--; }
        }

        public int CallsTo(string key) { lock (this) return Calls.Count(c => c.Key == key); }
    }

    internal static class PTest
    {
        public static byte[] Hash(int i)
        {
            var b = new byte[20];
            b[0] = (byte)(i % 250 + 1);                // simulated seeds
            b[1] = (byte)(i / 250 + 3);                // simulated peers
            b[2] = (byte)(i >> 8); b[3] = (byte)i; b[19] = 7;
            return b;
        }

        public static ProbeOptions Fast()
        {
            return new ProbeOptions
            {
                TrackerInterval = TimeSpan.FromMilliseconds(150), BatchWindow = TimeSpan.FromMilliseconds(40), MaxQueueWait = TimeSpan.FromSeconds(2),
                TrackerBackoffBase = TimeSpan.FromMilliseconds(200), CountTimeout = TimeSpan.FromSeconds(10)
            };
        }

        public static string Udp(string host, int port) { return "udp://" + host + ":" + port + "/announce"; }
    }

    public class TrackerLimiterTests
    {
        private static ProbeOptions Opts()
        {
            return new ProbeOptions
            {
                TrackerInterval = TimeSpan.FromSeconds(2), TrackerMaxConcurrent = 1, TrackerBackoffBase = TimeSpan.FromSeconds(5), TrackerBackoffMax = TimeSpan.FromMinutes(5),
                TrackerQuarantineAfter = 4, TrackerQuarantine = TimeSpan.FromMinutes(5), TrackerQuarantineMax = TimeSpan.FromMinutes(30),
                TrackerRejectedBackoff = TimeSpan.FromSeconds(60), TrackerQuarantineAfterRejections = 2, TrackerUnsupportedBench = TimeSpan.FromMinutes(30), DefaultWait = TimeSpan.FromMinutes(5)
            };
        }

        [Theory]
        [InlineData("udp://Tracker.Example.org:2710/announce", "tracker.example.org:2710")]
        [InlineData("http://tracker.example.org:2710/announce", "tracker.example.org:2710")]
        [InlineData("https://t.test/announce", "t.test:443")]
        [InlineData("http://t.test/announce", "t.test:80")]
        [InlineData("ws://t.test/announce", null)]
        [InlineData("basura", null)]
        public void KeyIsHostAndPortWithoutSchemeOrPath(string url, string key)
        {
            Assert.Equal(key, TrackerKey.From(url));
        }

        [Fact]
        public void FirstRequestIsImmediateAndThenAtMostOneEveryInterval()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Assert.Equal(TimeSpan.Zero, lim.Delay("t:1"));
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Ok, TimeSpan.FromMilliseconds(100), 5, 2, 0, 0);
            Assert.Equal(TimeSpan.FromSeconds(2), lim.Delay("t:1"));                      // the interval counts from start to start
            Assert.False(lim.TryBegin("t:1"));
            clock.Advance(TimeSpan.FromSeconds(1.5));
            Assert.Equal(TimeSpan.FromSeconds(0.5), lim.Delay("t:1"));
            Assert.False(lim.TryBegin("t:1"));
            clock.Advance(TimeSpan.FromSeconds(0.5));
            Assert.True(lim.TryBegin("t:1"));
            Assert.Equal(TimeSpan.Zero, lim.Delay("t:other"));                             // another tracker is not affected
            var st = lim.Stats("t:1");
            Assert.Equal(2.0, st.MinGapSeconds, 3);
        }

        [Fact]
        public void NeverMoreThanTheConfiguredNumberOfRequestsAtOnce()
        {
            var o = Opts(); o.TrackerInterval = TimeSpan.Zero; o.TrackerMaxConcurrent = 1;
            var clock = new FakeClock(); var lim = new TrackerLimiter(o, clock.Func);
            Assert.True(lim.TryBegin("t:1"));
            Assert.False(lim.TryBegin("t:1"));                                             // one in progress: another is not admitted
            Assert.True(lim.Delay("t:1") > TimeSpan.Zero);
            lim.End("t:1", RequestOutcome.Ok, TimeSpan.Zero, 1, 0, 0, 0);
            Assert.True(lim.TryBegin("t:1"));
            var o2 = Opts(); o2.TrackerInterval = TimeSpan.Zero; o2.TrackerMaxConcurrent = 2;
            var lim2 = new TrackerLimiter(o2, clock.Func);
            Assert.True(lim2.TryBegin("t:1")); Assert.True(lim2.TryBegin("t:1")); Assert.False(lim2.TryBegin("t:1"));
            var o3 = Opts(); o3.TrackerInterval = TimeSpan.Zero; o3.TrackerMaxConcurrent = 9;           // more than 2 is trimmed to 2
            var lim3 = new TrackerLimiter(o3, clock.Func);
            Assert.True(lim3.TryBegin("t:1")); Assert.True(lim3.TryBegin("t:1")); Assert.False(lim3.TryBegin("t:1"));
            Assert.Equal(2, lim3.Stats("t:1").MaxConcurrentObserved);
        }

        private static void Fail(TrackerLimiter lim, FakeClock clock, string key, RequestOutcome how = RequestOutcome.Timeout)
        {
            // waits as long as needed (interval, backoff) and fails one request
            clock.Advance(lim.Delay(key));
            Assert.True(lim.TryBegin(key));
            lim.End(key, how, TimeSpan.FromSeconds(8), 3, 2, 0, 0);
        }

        [Fact]
        public void BackoffDoublesAfterEachFailureAndNeverExceedsTheCap()
        {
            var clock = new FakeClock(); var o = Opts(); o.TrackerQuarantineAfter = 99; var lim = new TrackerLimiter(o, clock.Func);
            var expected = new[] { 5, 10, 20, 40, 80, 160, 300, 300 };
            foreach (var secs in expected)
            {
                clock.Advance(lim.Delay("t:1"));
                Assert.True(lim.TryBegin("t:1"));
                lim.End("t:1", RequestOutcome.Timeout, TimeSpan.FromSeconds(4), 1, 2, 0, 0);
                Assert.Equal(secs, lim.Delay("t:1").TotalSeconds, 3);                      // the backoff takes precedence over the 2 s interval
            }
            var st = lim.Stats("t:1");
            Assert.Equal(expected.Length, st.Timeouts); Assert.Equal(expected.Length, st.BackoffEvents); Assert.Equal(0, st.Quarantines);
        }

        [Fact]
        public void ARecoveredTrackerForgetsItsFailures()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Fail(lim, clock, "t:1"); Fail(lim, clock, "t:1");
            clock.Advance(lim.Delay("t:1"));
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Ok, TimeSpan.FromMilliseconds(80), 3, 2, 0, 0);
            Assert.Equal(0, lim.Stats("t:1").ConsecutiveFailures);
            Assert.Equal(2.0, lim.Delay("t:1").TotalSeconds, 3);                           // again only the interval
            Fail(lim, clock, "t:1");
            Assert.Equal(5.0, lim.Delay("t:1").TotalSeconds, 3);                           // and the backoff starts over from the base
        }

        [Fact]
        public void KeepsFailingMeansBenchedWithQuarantineThatDoublesAndEndsWithASuccessfulTrial()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            for (int i = 0; i < 4; i++) Fail(lim, clock, "t:1");
            Assert.True(lim.IsBenched("t:1"));
            Assert.Equal(1, lim.Stats("t:1").Quarantines);
            Assert.InRange(lim.Delay("t:1").TotalSeconds, 290, 300);                       // 5 min set aside
            Assert.False(lim.TryBegin("t:1"));
            clock.Advance(lim.Delay("t:1"));
            Assert.False(lim.IsBenched("t:1"));
            Assert.True(lim.TryBegin("t:1"));                                              // trial after the quarantine
            lim.End("t:1", RequestOutcome.Timeout, TimeSpan.FromSeconds(8), 1, 2, 0, 0);   // fails again: another quarantine, double
            Assert.True(lim.IsBenched("t:1"));
            Assert.InRange(lim.Delay("t:1").TotalSeconds, 590, 600);
            clock.Advance(lim.Delay("t:1"));
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Ok, TimeSpan.FromMilliseconds(100), 1, 2, 0, 0);  // this time it responds: everything forgotten
            Assert.False(lim.IsBenched("t:1"));
            Assert.Equal(2.0, lim.Delay("t:1").TotalSeconds, 3);
        }

        [Fact]
        public void QuarantineNeverExceedsItsMaximum()
        {
            var clock = new FakeClock(); var o = Opts(); o.TrackerQuarantineAfter = 1; var lim = new TrackerLimiter(o, clock.Func);
            for (int i = 0; i < 10; i++) Fail(lim, clock, "t:1");
            Assert.True(lim.Delay("t:1").TotalMinutes <= 30.01);
        }

        [Fact]
        public void RejectionsWaitAtLeastAMinuteHonourRetryAfterAndBenchQuickly()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Rejected, TimeSpan.FromMilliseconds(200), 50, 0, 0, 0);          // 429 without Retry-After
            Assert.Equal(60.0, lim.Delay("t:1").TotalSeconds, 3);
            Assert.False(lim.IsBenched("t:1"));
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Rejected, TimeSpan.FromMilliseconds(200), 50, 0, 0, 300);        // 2nd rejection, with a Retry-After of 300 s
            Assert.True(lim.IsBenched("t:1"));
            Assert.True(lim.Delay("t:1").TotalSeconds >= 299);
            Assert.Equal(2, lim.Stats("t:1").Rejections);
        }

        [Fact]
        public void TrackerWithoutScrapeIsLeftAloneForAWhileButIsNotAFailure()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Unsupported, TimeSpan.FromMilliseconds(50), 1, 0, 0, 0);
            Assert.True(lim.IsBenched("t:1"));
            Assert.InRange(lim.Delay("t:1").TotalMinutes, 29.9, 30.01);
            Assert.Equal(0, lim.Stats("t:1").ConsecutiveFailures);
            Assert.Equal(1, lim.Stats("t:1").Unsupported);
        }

        [Fact]
        public void MinRequestIntervalFromTheTrackerIsRespected()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Assert.True(lim.TryBegin("t:1"));
            lim.End("t:1", RequestOutcome.Ok, TimeSpan.FromMilliseconds(50), 3, 2, 900, 0);                  // flags.min_request_interval = 900
            Assert.Equal(900.0, lim.Delay("t:1").TotalSeconds, 3);
            lim.Wait("t:2", 0);                                                                              // -3 without a figure: 5 min by default
            Assert.Equal(300.0, lim.Delay("t:2").TotalSeconds, 3);
            lim.Wait("t:3", 99999);                                                                          // capped at 1 hour
            Assert.Equal(3600.0, lim.Delay("t:3").TotalSeconds, 3);
            Assert.Equal(900, lim.Stats("t:1").MinRequestIntervalAsked);
        }

        [Fact]
        public void AbortGivesTheSlotBackWithoutSpendingTheInterval()
        {
            var clock = new FakeClock(); var lim = new TrackerLimiter(Opts(), clock.Func);
            Assert.True(lim.TryBegin("t:1"));
            lim.Abort("t:1");
            Assert.Equal(TimeSpan.Zero, lim.Delay("t:1"));
            Assert.True(lim.TryBegin("t:1"));
        }
    }

    public class TrackerScrapeServiceTests
    {
        private static TrackerScrapeService Make(FakeTransport tr, ProbeOptions o, Func<DateTime> now = null)
        {
            return new TrackerScrapeService(tr, new TrackerLimiter(o, now), o);
        }

        [Fact]
        public async Task ManyLinksOnOneTrackerBecomeBatchedRequestsNotOnePerLink()
        {
            var tr = new FakeTransport { Cap = 70 };
            var o = PTest.Fast();
            using (var svc = Make(tr, o))
            {
                var url = PTest.Udp("t1.test", 2710);
                var tasks = Enumerable.Range(0, 120).Select(i => svc.ScrapeAsync(url, PTest.Hash(i), CancellationToken.None)).ToList();
                var res = await Task.WhenAll(tasks);
                Assert.Equal(120, res.Length);
                for (int i = 0; i < 120; i++) { Assert.Equal(ScrapeStatus.Ok, res[i].Status); Assert.Equal(PTest.Hash(i)[0], res[i].Seeders); Assert.Equal(PTest.Hash(i)[1], res[i].Leechers); }   // each link receives ITS result
                Assert.Equal(2, tr.Calls.Count);                                        // 120 infohashes = 70 + 50: two requests, not 120
                Assert.Equal(new[] { 70, 50 }, tr.Calls.Select(c => c.Value).ToArray());
                Assert.Equal(2, svc.RequestsSent);
            }
        }

        [Fact]
        public async Task EachTrackerGetsItsOwnBatchAndTheSameInfoHashTravelsOnlyOncePerBatch()
        {
            var tr = new FakeTransport();
            var o = PTest.Fast();
            using (var svc = Make(tr, o))
            {
                var a = PTest.Udp("a.test", 1); var b = "http://b.test:80/announce"; var c = PTest.Udp("c.test", 3);
                var tasks = new List<Task<ScrapeResult>>();
                for (int i = 0; i < 20; i++)
                {
                    tasks.Add(svc.ScrapeAsync(a, PTest.Hash(i), CancellationToken.None));
                    tasks.Add(svc.ScrapeAsync(b, PTest.Hash(i), CancellationToken.None));
                    if (i % 2 == 0) tasks.Add(svc.ScrapeAsync(c, PTest.Hash(i), CancellationToken.None));
                    tasks.Add(svc.ScrapeAsync(a, PTest.Hash(i), CancellationToken.None));               // the same infohash requested twice (two links with the same infohash)
                }
                await Task.WhenAll(tasks);
                Assert.Equal(1, tr.CallsTo("a.test:1")); Assert.Equal(1, tr.CallsTo("b.test:80")); Assert.Equal(1, tr.CallsTo("c.test:3"));
                Assert.Equal(20, tr.Calls.First(x => x.Key == "a.test:1").Value);                       // 40 requests of 20 distinct infohashes: 20 in the batch
                Assert.Equal(10, tr.Calls.First(x => x.Key == "c.test:3").Value);
            }
        }

        [Fact]
        public async Task UdpAndHttpOfTheSameHostAndPortShareTheQueue()
        {
            var tr = new FakeTransport();
            using (var svc = Make(tr, PTest.Fast()))
            {
                var t1 = svc.ScrapeAsync("udp://same.test:2710/announce", PTest.Hash(1), CancellationToken.None);
                var t2 = svc.ScrapeAsync("http://same.test:2710/announce", PTest.Hash(2), CancellationToken.None);
                await Task.WhenAll(t1, t2);
                Assert.Single(tr.Calls);
                Assert.Equal(2, tr.Calls[0].Value);
            }
        }

        [Fact]
        public async Task RequestsToTheSameTrackerAreSpacedByTheIntervalAndNeverOverlap()
        {
            var tr = new FakeTransport { DelayMs = 60 };
            var o = PTest.Fast(); o.TrackerInterval = TimeSpan.FromMilliseconds(250); o.BatchWindow = TimeSpan.Zero;
            using (var svc = Make(tr, o))
            {
                var url = PTest.Udp("slow.test", 9);
                var tasks = new List<Task<ScrapeResult>>();
                for (int wave = 0; wave < 8; wave++)
                {
                    for (int i = 0; i < 5; i++) tasks.Add(svc.ScrapeAsync(url, PTest.Hash(wave * 10 + i), CancellationToken.None));
                    await Task.Delay(90);
                }
                await Task.WhenAll(tasks);
                var starts = tr.Starts["slow.test:9"];
                Assert.True(starts.Count >= 3 && starts.Count <= 5, "peticiones: " + starts.Count);              // ~0.7 s of waves at 1 every 0.25 s, not one per wave
                for (int i = 1; i < starts.Count; i++) Assert.True((starts[i] - starts[i - 1]).TotalMilliseconds >= 230, "hueco " + (starts[i] - starts[i - 1]).TotalMilliseconds);
                Assert.Equal(1, tr.MaxConcurrent["slow.test:9"]);
                Assert.Equal(40, tr.HashesSeen.Count);                                                           // but everything was sent
            }
        }

        [Fact]
        public async Task AtMostTwoAtOnceWhenConfiguredAndNeverMore()
        {
            var tr = new FakeTransport { DelayMs = 250, Cap = 1 };
            var o = PTest.Fast(); o.TrackerInterval = TimeSpan.Zero; o.TrackerMaxConcurrent = 5; o.BatchWindow = TimeSpan.Zero;
            using (var svc = Make(tr, o))
            {
                var url = PTest.Udp("two.test", 9);
                var tasks = Enumerable.Range(0, 8).Select(i => svc.ScrapeAsync(url, PTest.Hash(i), CancellationToken.None)).ToList();
                await Task.WhenAll(tasks);
                Assert.Equal(2, tr.MaxConcurrent["two.test:9"]);
                Assert.Equal(8, tr.Calls.Count);
            }
        }

        [Fact]
        public async Task CanceledInfoHashesThatHaveNotLeftYetAreNeverSent()
        {
            var tr = new FakeTransport();
            var o = PTest.Fast(); o.BatchWindow = TimeSpan.FromMilliseconds(300);
            using (var svc = Make(tr, o))
            using (var cts = new CancellationTokenSource())
            {
                var url = PTest.Udp("c.test", 9);
                var keep = svc.ScrapeAsync(url, PTest.Hash(1), CancellationToken.None);
                var gone = svc.ScrapeAsync(url, PTest.Hash(2), cts.Token);
                cts.Cancel();                                                         // the link already has seeds from another tracker
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);
                await keep;
                Assert.Single(tr.Calls);
                Assert.Equal(1, tr.Calls[0].Value);
                Assert.DoesNotContain(Hex.ToHex(PTest.Hash(2)), tr.HashesSeen);
            }
        }

        [Fact]
        public async Task AFailingTrackerIsBackedOffThenBenchedAndLinksDoNotWaitForIt()
        {
            var tr = new FakeTransport { Handler = (u, h) => h.Select(x => new ScrapeResult { Tracker = u, Status = ScrapeStatus.NetworkError, Error = "sin respuesta", TimedOut = true, Packets = 2 }).ToList() };
            var o = PTest.Fast(); o.TrackerInterval = TimeSpan.FromMilliseconds(20); o.TrackerBackoffBase = TimeSpan.FromMilliseconds(100); o.TrackerQuarantineAfter = 3; o.MaxQueueWait = TimeSpan.FromSeconds(1);
            o.TrackerQuarantine = TimeSpan.FromMinutes(5);
            using (var svc = Make(tr, o))
            {
                var url = PTest.Udp("dead.test", 6969);
                int skipped = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int round = 0; round < 10; round++)
                {
                    var r = await svc.ScrapeAsync(url, PTest.Hash(round), CancellationToken.None);
                    if (r.Skipped) skipped++;
                }
                Assert.Equal(3, tr.Calls.Count);                                     // 3 failed requests (backoff 100, 200 ms) and then quarantine: no more
                Assert.True(skipped >= 6, "saltados: " + skipped);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "the links do not wait for a benched tracker");
                var st = svc.Limiter.Stats("dead.test:6969");
                Assert.Equal(3, st.Timeouts); Assert.Equal(1, st.Quarantines); Assert.True(st.BenchedNow); Assert.True(st.SkippedHashes >= 6);
            }
        }

        [Fact]
        public async Task ARejectingTrackerIsAskedOnlyOnce()
        {
            var tr = new FakeTransport { Handler = (u, h) => h.Select(x => new ScrapeResult { Tracker = u, Status = ScrapeStatus.NetworkError, Error = "HTTP 429", HttpStatus = 429, Rejected = true }).ToList() };
            var o = PTest.Fast();
            using (var svc = Make(tr, o))
            {
                var url = "http://limit.test:80/announce";
                var r1 = await svc.ScrapeAsync(url, PTest.Hash(1), CancellationToken.None);
                Assert.True(r1.Rejected);
                for (int i = 2; i < 8; i++) { var r = await svc.ScrapeAsync(url, PTest.Hash(i), CancellationToken.None); Assert.True(r.Skipped); }
                Assert.Single(tr.Calls);
                Assert.Equal(1, svc.Limiter.Stats("limit.test:80").Rejections);
            }
        }

        [Fact]
        public async Task TrackerThatAsksToWaitIsNotAskedAgainAndTheQueueIsDropped()
        {
            var tr = new FakeTransport { Handler = (u, h) => h.Select(x => new ScrapeResult { Tracker = u, Status = ScrapeStatus.Wait, MinRequestInterval = 900 }).ToList() };
            var o = PTest.Fast();
            using (var svc = Make(tr, o))
            {
                var url = PTest.Udp("w.test", 1);
                var r1 = await svc.ScrapeAsync(url, PTest.Hash(1), CancellationToken.None);
                Assert.Equal(ScrapeStatus.Wait, r1.Status); Assert.False(r1.Skipped);
                var r2 = await svc.ScrapeAsync(url, PTest.Hash(2), CancellationToken.None);
                Assert.True(r2.Skipped);
                Assert.Single(tr.Calls);
            }
        }

        [Fact]
        public async Task UnsupportedAnswerBenchesTheTracker()
        {
            var tr = new FakeTransport { Handler = (u, h) => h.Select(x => new ScrapeResult { Tracker = u, Status = ScrapeStatus.Unsupported, HttpStatus = 404 }).ToList() };
            using (var svc = Make(tr, PTest.Fast()))
            {
                var url = "http://noscrape.test:80/announce";
                await svc.ScrapeAsync(url, PTest.Hash(1), CancellationToken.None);
                var r2 = await svc.ScrapeAsync(url, PTest.Hash(2), CancellationToken.None);
                Assert.True(r2.Skipped);
                Assert.Single(tr.Calls);
                Assert.True(svc.Limiter.IsBenched("noscrape.test:80"));
            }
        }

        [Fact]
        public async Task InvalidTrackerUrlsAreUnsupportedWithoutTouchingTheNetwork()
        {
            var tr = new FakeTransport();
            using (var svc = Make(tr, PTest.Fast()))
            {
                var r = await svc.ScrapeAsync("basura", PTest.Hash(1), CancellationToken.None);
                Assert.Equal(ScrapeStatus.Unsupported, r.Status);
                Assert.Empty(tr.Calls);
            }
        }

        [Fact]
        public void BatchOutcomeClassification()
        {
            Func<ScrapeStatus, ScrapeResult> r = s => new ScrapeResult { Status = s };
            Assert.Equal(RequestOutcome.Ok, TrackerScrapeService.Classify(new[] { r(ScrapeStatus.Ok), r(ScrapeStatus.Invalid) }));
            Assert.Equal(RequestOutcome.Ok, TrackerScrapeService.Classify(new[] { r(ScrapeStatus.Invalid) }));                // the tracker answered even though it does not know the infohash
            Assert.Equal(RequestOutcome.Timeout, TrackerScrapeService.Classify(new[] { new ScrapeResult { Status = ScrapeStatus.NetworkError, TimedOut = true } }));
            Assert.Equal(RequestOutcome.Error, TrackerScrapeService.Classify(new[] { r(ScrapeStatus.NetworkError) }));
            Assert.Equal(RequestOutcome.Error, TrackerScrapeService.Classify(new[] { new ScrapeResult { Status = ScrapeStatus.Invalid, Malformed = true } }));
            Assert.Equal(RequestOutcome.Rejected, TrackerScrapeService.Classify(new[] { new ScrapeResult { Status = ScrapeStatus.NetworkError, Rejected = true } }));
            Assert.Equal(RequestOutcome.Unsupported, TrackerScrapeService.Classify(new[] { r(ScrapeStatus.Unsupported) }));
        }
    }

    public class BatchWireTests
    {
        private static byte[] DecodeHash(string pct)
        {
            var b = new List<byte>();
            for (int i = 0; i < pct.Length; i++)
            {
                if (pct[i] == '%') { b.Add(Convert.ToByte(pct.Substring(i + 1, 2), 16)); i += 2; }
                else b.Add((byte)pct[i]);
            }
            return b.ToArray();
        }

        private static List<byte[]> HashesInUrl(string pathAndQuery)
        {
            int q = pathAndQuery.IndexOf('?');
            var res = new List<byte[]>();
            foreach (var part in pathAndQuery.Substring(q + 1).Split('&'))
                if (part.StartsWith("info_hash=")) res.Add(DecodeHash(part.Substring(10)));
            return res;
        }

        [Fact]
        public void HttpUrlCarriesOneInfoHashParameterPerHash()
        {
            var url = HttpScrape.BuildUrl("http://t.test/announce?k=v", new[] { PTest.Hash(1), PTest.Hash(2), PTest.Hash(3) });
            Assert.StartsWith("http://t.test/scrape?k=v&info_hash=", url);
            Assert.Equal(3, url.Split(new[] { "info_hash=" }, StringSplitOptions.None).Length - 1);
            Assert.Equal(url, HttpScrape.BuildUrl("http://t.test/announce?k=v", new[] { PTest.Hash(1), PTest.Hash(2), PTest.Hash(3) }));
            Assert.Null(HttpScrape.BuildUrl("http://t.test/tracker", new[] { PTest.Hash(1) }));
        }

        [Fact]
        public void HttpBatchIsCappedAtFiftyAndByUrlLength()
        {
            var hashes = Enumerable.Range(0, 200).Select(i => { var h = new byte[20]; for (int k = 0; k < 20; k++) h[k] = (byte)(0x80 + i % 100 + k); return h; }).ToList();   // everything is encoded: 60 characters per infohash
            Assert.Equal(50, HttpScrape.FitCount("http://t.test/announce", hashes, 50, 100000));
            int fit = HttpScrape.FitCount("http://t.test/announce", hashes, 50, 2000);
            Assert.InRange(fit, 20, 33);
            var url = HttpScrape.BuildUrl("http://t.test/announce", hashes.Take(fit).ToList());
            Assert.True(url.Length <= 2000, "URL length " + url.Length);
            Assert.Equal(1, HttpScrape.FitCount("http://t.test/announce", hashes, 50, 10));                // at least one always fits
            Assert.Equal(0, HttpScrape.FitCount("http://t.test/announce", new List<byte[]>(), 50, 2000));
        }

        [Fact]
        public void ParsesMultiHashResponsesInTheOrderRequested()
        {
            var a = PTest.Hash(1); var b = PTest.Hash(2); var c = PTest.Hash(3);
            var files = new BDict();
            Func<int, int, BDict> entry = (s, l) => { var f = new BDict(); f.Add("complete", BValue.Int(s)); f.Add("incomplete", BValue.Int(l)); f.Add("downloaded", BValue.Int(0)); return f; };
            files.Add(BValue.Bytes(c) as BString, entry(30, 31));
            files.Add(BValue.Bytes(a) as BString, entry(10, 11));
            var d = new BDict(); d.Add("files", files);
            var r = HttpScrape.ParseBatchResponse(BencodeWriter.Encode(d), new[] { a, b, c });
            Assert.Equal(3, r.Count);
            Assert.Equal(ScrapeStatus.Ok, r[0].Status); Assert.Equal(10, r[0].Seeders); Assert.Equal(11, r[0].Leechers);
            Assert.Equal(ScrapeStatus.Invalid, r[1].Status);                                          // the tracker does not know this one
            Assert.Equal(ScrapeStatus.Ok, r[2].Status); Assert.Equal(30, r[2].Seeders);
            // flags.min_request_interval: what is known stays Ok with the figure; what is missing asks to wait
            var fl = new BDict(); fl.Add("min_request_interval", BValue.Int(120)); d.Add("flags", fl);
            r = HttpScrape.ParseBatchResponse(BencodeWriter.Encode(d), new[] { a, b });
            Assert.Equal(ScrapeStatus.Ok, r[0].Status); Assert.Equal(120, r[0].MinRequestInterval);
            Assert.Equal(ScrapeStatus.Wait, r[1].Status); Assert.Equal(120, r[1].MinRequestInterval);
            // failure reason: all invalid; if it talks about a limit, rejection
            var f2 = new BDict(); f2.Add("failure reason", BValue.Str("rate limit exceeded"));
            r = HttpScrape.ParseBatchResponse(BencodeWriter.Encode(f2), new[] { a, b });
            Assert.All(r, x => { Assert.Equal(ScrapeStatus.Invalid, x.Status); Assert.True(x.Rejected); });
            r = HttpScrape.ParseBatchResponse(Encoding.ASCII.GetBytes("<html>"), new[] { a });
            Assert.True(r[0].Malformed);
        }

        [Fact]
        public async Task OneHttpRequestCarriesTheWholeBatchAndEachLinkGetsItsOwnNumbers()
        {
            var seenUrls = new List<string>();
            using (var srv = new FakeHttpServer(p =>
            {
                lock (seenUrls) seenUrls.Add(p);
                var files = new BDict();
                foreach (var h in HashesInUrl(p)) { var f = new BDict(); f.Add("complete", BValue.Int(h[0])); f.Add("incomplete", BValue.Int(h[1])); f.Add("downloaded", BValue.Int(0)); files.Add(BValue.Bytes(h) as BString, f); }
                var d = new BDict(); d.Add("files", files);
                return new KeyValuePair<int, byte[]>(200, BencodeWriter.Encode(d));
            }))
            using (var http = new HttpClient())
            {
                var o = PTest.Fast(); o.HttpMaxUrlLength = 8000;
                var svc = new TrackerScrapeService(new NetScrapeTransport(http, o), new TrackerLimiter(o), o);
                var url = "http://127.0.0.1:" + srv.Port + "/announce";
                var res = await Task.WhenAll(Enumerable.Range(0, 120).Select(i => svc.ScrapeAsync(url, PTest.Hash(i), CancellationToken.None)));
                for (int i = 0; i < 120; i++) { Assert.Equal(ScrapeStatus.Ok, res[i].Status); Assert.Equal(PTest.Hash(i)[0], res[i].Seeders); Assert.Equal(PTest.Hash(i)[1], res[i].Leechers); }
                lock (seenUrls)
                {
                    Assert.Equal(3, seenUrls.Count);                                                  // 120 = 50 + 50 + 20, not 120 requests
                    Assert.Equal(new[] { 50, 50, 20 }, seenUrls.Select(u => HashesInUrl(u).Count).ToArray());
                    Assert.All(seenUrls, u => Assert.True(u.Length < o.HttpMaxUrlLength + 40, "URL length " + u.Length));
                }
                var st = svc.Limiter.Stats(TrackerKey.From(url));
                Assert.Equal(3, st.Requests); Assert.Equal(120, st.HashesSent); Assert.Equal(3, st.Responses); Assert.True(st.MinGapSeconds >= 0.14, "hueco " + st.MinGapSeconds);
            }
        }

        [Fact]
        public async Task HttpRejectionsAndMissingScrapeAreTold()
        {
            var ih = PTest.Hash(1);
            using (var srv = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(429, Encoding.ASCII.GetBytes("slow down"))))
            using (var http = new HttpClient())
            {
                var r = (await HttpScrape.ScrapeBatchAsync(http, "http://127.0.0.1:" + srv.Port + "/announce", new[] { ih, PTest.Hash(2) }, TimeSpan.FromSeconds(3), CancellationToken.None));
                Assert.All(r, x => { Assert.True(x.Rejected); Assert.Equal(429, x.HttpStatus); Assert.Equal(ScrapeStatus.NetworkError, x.Status); });
            }
            using (var srv = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(404, new byte[0])))
            using (var http = new HttpClient())
            {
                var r = (await HttpScrape.ScrapeBatchAsync(http, "http://127.0.0.1:" + srv.Port + "/announce", new[] { ih }, TimeSpan.FromSeconds(3), CancellationToken.None));
                Assert.Equal(ScrapeStatus.Unsupported, r[0].Status); Assert.Equal(404, r[0].HttpStatus);
            }
        }

        [Fact]
        public async Task UdpBatchOfSeventyGoesInOnePacketAndTheConnectionIsReused()
        {
            using (var t = new FakeScrapeUdpTracker())
            {
                var hashes = Enumerable.Range(0, 70).Select(i => PTest.Hash(i)).ToList();
                foreach (var h in hashes) t.Swarms[Hex.ToHex(h)] = new[] { h[0], 0, h[1] };
                var sc = new UdpScraper();
                var r1 = await sc.ScrapeAsync("127.0.0.1", t.Port, "x", hashes, new[] { 2 }, CancellationToken.None);
                Assert.Equal(70, r1.Count);
                for (int i = 0; i < 70; i++) { Assert.Equal(ScrapeStatus.Ok, r1[i].Status); Assert.Equal(hashes[i][0], r1[i].Seeders); Assert.Equal(hashes[i][1], r1[i].Leechers); }
                Assert.Equal(1, t.Connects); Assert.Equal(1, t.Scrapes); Assert.Equal(2, r1[0].Packets);       // connect + scrape
                var r2 = await sc.ScrapeAsync("127.0.0.1", t.Port, "x", hashes.Take(10).ToList(), new[] { 2 }, CancellationToken.None);
                Assert.Equal(1, t.Connects); Assert.Equal(2, t.Scrapes); Assert.Equal(1, r2[0].Packets);       // the connection id is reused: only the scrape
                // without memory (the static API): one connect per call
                await UdpScrape.ScrapeAsync("127.0.0.1", t.Port, "x", hashes.Take(2).ToList(), new[] { 2 }, CancellationToken.None);
                Assert.Equal(2, t.Connects);
            }
        }

        [Fact]
        public async Task UdpConnectionIdExpiresAndNeverGoesBeyondSeventyFour()
        {
            var clock = new FakeClock();
            using (var t = new FakeScrapeUdpTracker())
            {
                var sc = new UdpScraper(clock.Func) { ConnectionTtl = TimeSpan.FromSeconds(60) };
                var h = new List<byte[]> { PTest.Hash(1) };
                await sc.ScrapeAsync("127.0.0.1", t.Port, "x", h, new[] { 2 }, CancellationToken.None);
                clock.Advance(TimeSpan.FromSeconds(59));
                await sc.ScrapeAsync("127.0.0.1", t.Port, "x", h, new[] { 2 }, CancellationToken.None);
                Assert.Equal(1, t.Connects);
                clock.Advance(TimeSpan.FromSeconds(2));                                                          // 61 s passed: another connect
                await sc.ScrapeAsync("127.0.0.1", t.Port, "x", h, new[] { 2 }, CancellationToken.None);
                Assert.Equal(2, t.Connects);
            }
            Assert.Throws<ArgumentException>(() => UdpScrape.BuildScrape(1, 1, Enumerable.Range(0, 75).Select(i => new byte[20]).ToList()));
            Assert.Equal(16 + 20 * 74, UdpScrape.BuildScrape(1, 1, Enumerable.Range(0, 74).Select(i => new byte[20]).ToList()).Length);
        }

        [Fact]
        public async Task UdpAnswersWithAnErrorAfterTheCachedConnectionIdExpiredGetsOneFreshAttempt()
        {
            using (var t = new FakeScrapeUdpTracker())
            {
                var sc = new UdpScraper();
                var h = new List<byte[]> { PTest.Hash(1) };
                t.Swarms[Hex.ToHex(h[0])] = new[] { 4, 0, 5 };
                await sc.ScrapeAsync("127.0.0.1", t.Port, "x", h, new[] { 2 }, CancellationToken.None);
                t.ErrorOnScrape = true;                                                                          // the tracker "forgot" the connection: it answers with an error
                var r = await sc.ScrapeAsync("127.0.0.1", t.Port, "x", h, new[] { 1, 1 }, CancellationToken.None);
                Assert.Equal(ScrapeStatus.Invalid, r[0].Status);                                                 // second attempt with a new connect, which also gives an error
                Assert.Equal(2, t.Connects);
            }
        }

        [Fact]
        public async Task UdpSilenceIsATimeoutAndCountsItsPackets()
        {
            using (var t = new FakeScrapeUdpTracker { DropFirst = 1000 })
            {
                var r = await UdpScrape.ScrapeAsync("127.0.0.1", t.Port, "x", new List<byte[]> { PTest.Hash(1) }, new[] { 1, 1 }, CancellationToken.None);
                Assert.Equal(ScrapeStatus.NetworkError, r[0].Status); Assert.True(r[0].TimedOut); Assert.Equal(2, r[0].Packets);
            }
        }
    }

    // ---------- Phased scheduler ----------

    internal sealed class FakePhasedProber : IPhasedProber
    {
        public ProbeOptions Options { get; set; }
        public int ResolveDelayMs = 10, CountDelayMs = 10;
        public int Resolving, MaxResolving;
        public readonly Dictionary<string, int> ResolveCalls = new Dictionary<string, int>();
        public readonly Dictionary<string, List<DateTime>> CountCalls = new Dictionary<string, List<DateTime>>();
        public int FallbackCalls;
        public Func<LinkEntry, int, ResolveStep> ResolveFn;
        public Func<SwarmTarget, int, ProbeResult> CountFn = (t, round) => new ProbeResult { State = LinkState.Good, Peers = 3, Method = "scrape-udp", InfoHash = Hex.ToHex(t.InfoHash) };
        public Func<SwarmTarget, ProbeResult> FallbackFn = t => new ProbeResult { State = LinkState.Unknown, Method = "dht" };

        public static ResolveStep Target(LinkEntry e)
        {
            var ih = Hex.Parse(e.Link.Value);
            return new ResolveStep { Target = new SwarmTarget { InfoHash = ih, Trackers = new List<string> { "udp://t.test:1/announce" }, ContentId = e.Link.Value } };
        }

        public Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct) { throw new NotSupportedException(); }

        public async Task<ResolveStep> ResolveAsync(LinkEntry e, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Resolving);
            int m; while (n > (m = Volatile.Read(ref MaxResolving))) Interlocked.CompareExchange(ref MaxResolving, n, m);
            int attempt;
            lock (ResolveCalls) { ResolveCalls.TryGetValue(e.Key, out attempt); attempt++; ResolveCalls[e.Key] = attempt; }
            try
            {
                await Task.Delay(ResolveDelayMs, ct);
                return ResolveFn != null ? ResolveFn(e, attempt) : Target(e);
            }
            finally { Interlocked.Decrement(ref Resolving); }
        }

        public async Task<ProbeResult> CountAsync(SwarmTarget t, CancellationToken ct)
        {
            int round;
            string key = Hex.ToHex(t.InfoHash);
            lock (CountCalls) { List<DateTime> l; if (!CountCalls.TryGetValue(key, out l)) CountCalls[key] = l = new List<DateTime>(); l.Add(DateTime.UtcNow); round = l.Count; }
            await Task.Delay(CountDelayMs, ct);
            var r = CountFn(t, round);
            r.Rounds = 1;
            return r;
        }

        public async Task<ProbeResult> FallbackAsync(SwarmTarget t, CancellationToken ct)
        {
            Interlocked.Increment(ref FallbackCalls);
            await Task.Delay(5, ct);
            return FallbackFn(t);
        }
    }

    public class PhasedSchedulerTests
    {
        private static List<LinkEntry> Entries(int n)
        {
            return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList();
        }

        private static string Key(int i) { return (i + 1).ToString("x40"); }

        private static CancellationToken Tok(int ms = 15000) { return new CancellationTokenSource(ms).Token; }

        [Fact]
        public async Task ResolutionRunsAtTheConfiguredConcurrencyAndNeverMore()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 50 }, ResolveDelayMs = 150, CountDelayMs = 5 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(Entries(120));
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(120, fp.ResolveCalls.Count);
                Assert.True(fp.MaxResolving <= 50, "resolviendo a la vez: " + fp.MaxResolving);
                Assert.True(fp.MaxResolving >= 45, "it should use all 50: " + fp.MaxResolving);
                Assert.Equal(fp.MaxResolving, s.MaxObservedConcurrency);
                Assert.Equal(120, s.Stats.Resolved);
                Assert.Equal(120, s.Snapshot().Count);
            }
        }

        [Fact]
        public async Task TheDefaultIsFiftyAtATime()
        {
            Assert.Equal(50, new ProbeOptions().Concurrency);
            Assert.Equal(TimeSpan.FromSeconds(10), new ProbeOptions().RecheckDelay);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CountingDoesNotHoldAResolutionSlot()
        {
            // with a single resolution slot and a slow count (300 ms), if the count held the slot 6 links would take > 1.8 s
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 1, RecheckDelay = TimeSpan.Zero }, ResolveDelayMs = 20, CountDelayMs = 300 };
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Start(Entries(6));
                await s.WaitFirstCycleAsync(Tok());
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.3), "tardo " + sw.Elapsed.TotalSeconds);
                Assert.Equal(1, fp.MaxResolving);
            }
        }

        [Fact]
        public async Task LinkAtZeroIsCheckedAgainAfterTheDelayAndOnlyHiddenIfItStaysAtZero()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromMilliseconds(600) } };
            fp.CountFn = (t, round) =>
            {
                string k = Hex.ToHex(t.InfoHash);
                if (k == Key(0)) return new ProbeResult { State = LinkState.Good, Seeders = 2, Peers = 5, Method = "scrape-udp" };                   // alive at the first try
                if (k == Key(1)) return round == 1 ? new ProbeResult { State = LinkState.Unknown } : new ProbeResult { State = LinkState.Good, Peers = 4, Method = "scrape-udp" };          // ups and downs: to zero and then alive
                return new ProbeResult { State = LinkState.Unknown };                                                                                    // always at zero
            };
            var entries = Entries(3);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await Task.Delay(300);
                Assert.Equal(LinkState.Good, s.GetResult(entries[0]).State);
                Assert.Equal(LinkState.Checking, s.GetResult(entries[1]).State);                // at zero, but not yet discarded
                Assert.Equal(LinkState.Checking, s.GetResult(entries[2]).State);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Good, s.GetResult(entries[1]).State); Assert.Equal(2, s.GetResult(entries[1]).Rounds); Assert.Equal(4, s.GetResult(entries[1]).Peers);
                Assert.Equal(LinkState.Unknown, s.GetResult(entries[2]).State); Assert.Equal(2, s.GetResult(entries[2]).Rounds);
                Assert.Single(fp.CountCalls[Key(0)]);                                           // the alive one is not repeated
                Assert.Equal(2, fp.CountCalls[Key(1)].Count); Assert.Equal(2, fp.CountCalls[Key(2)].Count);
                var gap = fp.CountCalls[Key(2)][1] - fp.CountCalls[Key(2)][0];
                Assert.True(gap >= TimeSpan.FromMilliseconds(580), "the 2nd check happened after " + gap.TotalMilliseconds + " ms");
                Assert.Equal(2, s.Stats.Rechecked); Assert.Equal(1, s.Stats.GoodAfterRecheck); Assert.Equal(1, s.Stats.GoodFirstRound);
                Assert.Equal(1, s.Stats.FinalUnknown);
                Assert.Equal(2, s.Snapshot().Count);                                            // the one that stays at zero is hidden
            }
        }

        [Fact]
        public async Task NoDataAtAllIsTreatedLikeZeroAndRetried()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromMilliseconds(200) } };
            fp.CountFn = (t, round) => round == 1 ? new ProbeResult { State = LinkState.Unknown, Error = "ningun tracker respondio" } : new ProbeResult { State = LinkState.Good, Seeders = 1, Method = "scrape-http" };
            var entries = Entries(1);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Good, s.GetResult(entries[0]).State);
            }
        }

        [Fact]
        public async Task DeadOnlyIfBothRoundsWereInvalid()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromMilliseconds(100) } };
            fp.CountFn = (t, round) => new ProbeResult { State = Hex.ToHex(t.InfoHash) == Key(0) ? LinkState.Dead : (round == 1 ? LinkState.Dead : LinkState.Unknown) };
            var entries = Entries(2);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Dead, s.GetResult(entries[0]).State);
                Assert.Equal(LinkState.Unknown, s.GetResult(entries[1]).State);
            }
        }

        [Fact]
        public async Task PeersFoundByTheAlternativeWhileWaitingShowTheLinkAliveWithoutWaitingForTheRecheck()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromSeconds(30) } };
            fp.CountFn = (t, round) => new ProbeResult { State = LinkState.Unknown };
            fp.FallbackFn = t => new ProbeResult { State = LinkState.Good, Peers = 9, Approximate = true, Method = "dht" };
            var entries = Entries(1);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
                var r = s.GetResult(entries[0]);
                Assert.Equal(LinkState.Good, r.State); Assert.True(r.Approximate); Assert.Equal(9, r.Peers); Assert.Equal("dht", r.Method);
                Assert.Single(fp.CountCalls[Key(0)]);
            }
        }

        [Fact]
        public async Task UnresolvedContentIdIsTriedOnceMoreAfterTheDelay()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromMilliseconds(300), RecheckUnresolved = true } };
            fp.ResolveFn = (e, attempt) =>
            {
                if (e.Key == "cid:" + Key(0)) return attempt == 1 ? new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Unresolved = true, Error = "x" } } : FakePhasedProber.Target(e);       // on the second one it resolves
                return new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Unresolved = true, Error = "x" } };                 // never
            };
            var entries = Entries(2);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Good, s.GetResult(entries[0]).State);
                Assert.Equal(LinkState.Unknown, s.GetResult(entries[1]).State);
                Assert.Equal(2, fp.ResolveCalls[entries[0].Key]); Assert.Equal(2, fp.ResolveCalls[entries[1].Key]);                  // two attempts, no more
                Assert.Equal(2, s.Stats.ResolveRetried); Assert.Equal(1, s.Stats.ResolveRecovered);
            }
        }

        [Fact]
        public async Task ContentIdNobodyServesIsNotRetriedByDefault()
        {
            Assert.False(new ProbeOptions().RecheckUnresolved);
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromMilliseconds(100) } };
            fp.ResolveFn = (e, attempt) => new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Unresolved = true } };
            var entries = Entries(1);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(1, fp.ResolveCalls[entries[0].Key]);
            }
        }

        [Fact]
        public async Task WithoutRecheckTheAnswerIsFinalAtOnce()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.Zero } };
            fp.CountFn = (t, round) => new ProbeResult { State = LinkState.Unknown };
            var entries = Entries(2);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(LinkState.Unknown, s.GetResult(entries[0]).State);
                Assert.Single(fp.CountCalls[Key(0)]);
            }
        }

        [Fact]
        public async Task StoppingDuringTheWaitLeavesNothingHalfDone()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.FromSeconds(30) } };
            fp.CountFn = (t, round) => new ProbeResult { State = LinkState.Unknown };
            var entries = Entries(5);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await Task.Delay(400);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Stop();
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
                await Task.Delay(100);
                Assert.All(entries, e => Assert.Equal(LinkState.Pending, s.GetResult(e).State));
                Assert.False(s.IsRunning);
            }
        }

        [Fact]
        public async Task VisibleLinksAreResolvedFirst()
        {
            var order = new List<string>();
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 1, RecheckDelay = TimeSpan.Zero }, ResolveDelayMs = 5 };
            fp.ResolveFn = (e, a) => { lock (order) order.Add(e.Key); return FakePhasedProber.Target(e); };
            var entries = Entries(20);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.SetVisible(new[] { entries[15].Key, entries[7].Key });
                s.Start(entries);
                await s.WaitFirstCycleAsync(Tok());
                Assert.Equal(new HashSet<string>(new[] { entries[15].Key, entries[7].Key }), new HashSet<string>(order.Take(2)));
            }
        }
    }

    // ---------- End to end with real loopback trackers ----------

    public class BatchedProbeEndToEndTests
    {
        private static string Magnet(byte[] ih, params string[] trackers)
        {
            return "magnet:?xt=urn:btih:" + Hex.ToHex(ih) + string.Concat(trackers.Select(t => "&tr=" + Uri.EscapeDataString(t)));
        }

        private static LinkProber Prober(ProbeOptions o)
        {
            return new LinkProber(null, null, new HttpClient(), o);
        }

        private static ProbeOptions Opts()
        {
            var o = PTest.Fast();
            o.UdpTimeoutsSeconds = new[] { 2 };
            o.HttpTimeout = TimeSpan.FromSeconds(3);
            o.UseDhtFallback = false; o.UseAnnounceFallback = false;
            o.RecheckDelay = TimeSpan.FromMilliseconds(500);
            o.ResolveInfoHashLinks = false;                  // these tests are about a magnet's count by trackers, without resolving its descriptor
            return o;
        }

        [Fact]
        public async Task ThirtyLinksOnTheSameTrackerAreOneScrapeAndEachGetsItsOwnNumbers()
        {
            using (var t = new FakeScrapeUdpTracker())
            {
                var hashes = Enumerable.Range(0, 30).Select(i => PTest.Hash(i)).ToList();
                var entries = new List<LinkEntry>();
                for (int i = 0; i < 30; i++)
                {
                    t.Swarms[Hex.ToHex(hashes[i])] = i % 5 == 0 ? new[] { 0, 0, 0 } : new[] { i, 0, i * 2 };         // 6 with nobody
                    entries.Add(LinkEntry.FromUrl("c" + i, Magnet(hashes[i], t.Url)));
                }
                var o = Opts();
                var p = Prober(o);
                using (var s = new ProbeScheduler(p, o))
                {
                    s.Start(entries);
                    await s.WaitFirstCycleAsync(new CancellationTokenSource(30000).Token);
                    for (int i = 0; i < 30; i++)
                    {
                        var r = s.GetResult(entries[i]);
                        if (i % 5 == 0) { Assert.Equal(LinkState.Unknown, r.State); Assert.Equal(2, r.Rounds); }
                        else { Assert.Equal(LinkState.Good, r.State); Assert.Equal(i, r.Seeders); Assert.Equal(i * 2, r.Peers); Assert.Equal("scrape-udp", r.Method); }
                    }
                    // 1st round: 30 infohashes in ONE scrape packet; 2nd (10 s rule): only the 6 at zero, in ONE more packet
                    Assert.Equal(2, t.Scrapes);
                    Assert.Equal(1, t.Connects);                                                                       // and a single connect (the connection id is reused)
                    var st = p.Scrape.Limiter.Stats(TrackerKey.From(t.Url));
                    Assert.Equal(2, st.Requests); Assert.Equal(36, st.HashesSent); Assert.Equal(30, st.MaxBatch); Assert.Equal(1, st.MaxConcurrentObserved);
                    Assert.Equal(6, s.Stats.Rechecked); Assert.Equal(24, s.Stats.GoodFirstRound);
                }
            }
        }

        [Fact]
        public async Task LinksWithSeveralTrackersAskEachTrackerOnceAndKeepTheMaximum()
        {
            using (var a = new FakeScrapeUdpTracker())
            using (var b = new FakeScrapeUdpTracker())
            {
                var entries = new List<LinkEntry>();
                for (int i = 0; i < 12; i++)
                {
                    var ih = PTest.Hash(i);
                    a.Swarms[Hex.ToHex(ih)] = new[] { 0, 0, 10 + i };
                    b.Swarms[Hex.ToHex(ih)] = new[] { 0, 0, 30 + i };
                    entries.Add(LinkEntry.FromUrl("c" + i, Magnet(ih, a.Url, b.Url)));
                }
                var o = Opts(); o.RecheckDelay = TimeSpan.Zero;
                var p = Prober(o);
                using (var s = new ProbeScheduler(p, o))
                {
                    s.Start(entries);
                    await s.WaitFirstCycleAsync(new CancellationTokenSource(30000).Token);
                    for (int i = 0; i < 12; i++) Assert.Equal(30 + i, s.GetResult(entries[i]).Peers);                  // the maximum of the two
                    Assert.Equal(1, a.Scrapes); Assert.Equal(1, b.Scrapes);                                            // not 12 + 12
                }
            }
        }

        [Fact]
        public async Task ADeadTrackerDoesNotHoldBackLinksThatAnotherTrackerAnswers()
        {
            using (var good = new FakeScrapeUdpTracker())
            using (var dead = new FakeScrapeUdpTracker { DropFirst = 100000 })
            {
                var entries = new List<LinkEntry>();
                for (int i = 0; i < 8; i++)
                {
                    var ih = PTest.Hash(i);
                    good.Swarms[Hex.ToHex(ih)] = new[] { 1, 0, 1 };
                    entries.Add(LinkEntry.FromUrl("c" + i, Magnet(ih, dead.Url, good.Url)));
                }
                var o = Opts(); o.RecheckDelay = TimeSpan.Zero; o.UdpTimeoutsSeconds = new[] { 1 }; o.CountTimeout = TimeSpan.FromSeconds(10);
                var p = Prober(o);
                using (var s = new ProbeScheduler(p, o))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    s.Start(entries);
                    await s.WaitFirstCycleAsync(new CancellationTokenSource(30000).Token);
                    Assert.All(entries, e => Assert.Equal(LinkState.Good, s.GetResult(e).State));
                    await Task.Delay(1500);                                                                              // the request to the dead one is still in flight when the link has already finished with the other
                    var dst = p.Scrape.Limiter.Stats(TrackerKey.From(dead.Url)); Assert.Equal(1, dst.Requests); Assert.Equal(8, dst.HashesSent); Assert.Equal(1, dst.Timeouts);       // the dead one received ONE request with the 8 infohashes
                }
            }
        }

        [Fact]
        public async Task HttpAndUdpTrackersTogetherInOnePass()
        {
            var seen = 0;
            using (var udp = new FakeScrapeUdpTracker())
            using (var http = new FakeHttpServer(p =>
            {
                Interlocked.Increment(ref seen);
                var files = new BDict();
                int q = p.IndexOf('?');
                foreach (var part in p.Substring(q + 1).Split('&'))
                {
                    if (!part.StartsWith("info_hash=")) continue;
                    var h = new List<byte>(); var s = part.Substring(10);
                    for (int i = 0; i < s.Length; i++) { if (s[i] == '%') { h.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; } else h.Add((byte)s[i]); }
                    var f = new BDict(); f.Add("complete", BValue.Int(0)); f.Add("incomplete", BValue.Int(h[0])); f.Add("downloaded", BValue.Int(0));
                    files.Add(BValue.Bytes(h.ToArray()) as BString, f);
                }
                var d = new BDict(); d.Add("files", files);
                return new KeyValuePair<int, byte[]>(200, BencodeWriter.Encode(d));
            }))
            {
                var entries = new List<LinkEntry>();
                for (int i = 0; i < 10; i++)
                {
                    var ih = PTest.Hash(i);
                    entries.Add(LinkEntry.FromUrl("c" + i, Magnet(ih, "http://127.0.0.1:" + http.Port + "/announce", udp.Url)));
                }
                var o = Opts(); o.RecheckDelay = TimeSpan.Zero;
                var p = Prober(o);
                using (var s = new ProbeScheduler(p, o))
                {
                    s.Start(entries);
                    await s.WaitFirstCycleAsync(new CancellationTokenSource(30000).Token);
                    for (int i = 0; i < 10; i++) { var r = s.GetResult(entries[i]); Assert.Equal(LinkState.Good, r.State); Assert.Equal(PTest.Hash(i)[0], r.Peers); }
                    Assert.Equal(1, seen);
                    Assert.Equal(1, udp.Scrapes);
                }
            }
        }
    }

    // ---------- DHT and connections: global caps ----------

    public class LoadCapsTests
    {
        [Fact]
        public async Task DhtQueriesInFlightNeverExceedTheCapEvenWithManySearchesAtOnce()
        {
            // a "node" that never answers: each query holds its slot until the deadline (3 s)
            using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var opt = new EngineOptions { DhtMaxInFlight = 6 };
                opt.DhtBootstrap = new List<string> { "127.0.0.1:" + ((IPEndPoint)silent.Client.LocalEndPoint).Port };
                using (var dht = new DhtClient(opt))
                using (var cts = new CancellationTokenSource(1200))
                {
                    dht.Start();
                    var searches = Enumerable.Range(0, 20).Select(i => { var h = new byte[20]; h[0] = (byte)i; return dht.GetPeersAsync(h, ps => { }, TimeSpan.FromSeconds(5), cts.Token); }).ToList();
                    try { await Task.WhenAll(searches); } catch (OperationCanceledException) { }
                    Assert.True(dht.MaxInFlightObserved <= 6, "en vuelo: " + dht.MaxInFlightObserved);
                    Assert.True(dht.MaxInFlightObserved >= 5);
                    Assert.True(dht.QueriesQueued > 0, "20 searches x 8 queries must wait for a slot");
                    Assert.True(dht.QueriesSent <= 6 + 4, "enviadas " + dht.QueriesSent);                // without a response there are no new slots until 3 s
                }
            }
        }

        [Fact]
        public async Task DhtResponseRateIsCountedAndTimeoutsAreSeparated()
        {
            using (var node = new FakeDhtNode())
            {
                node.Peers.Add(new IPEndPoint(IPAddress.Parse("10.2.0.1"), 8621));
                var opt = new EngineOptions { DhtMaxInFlight = 128 };
                opt.DhtBootstrap = new List<string> { "127.0.0.1:" + node.Port };
                using (var dht = new DhtClient(opt))
                {
                    dht.Start();
                    int n = await dht.GetPeersAsync(new byte[20], ps => { }, TimeSpan.FromSeconds(4), CancellationToken.None);
                    Assert.Equal(1, n);
                    Assert.True(dht.QueriesSent >= 1); Assert.Equal(dht.QueriesSent, dht.ResponsesReceived); Assert.Equal(0, dht.QueryTimeouts);
                }
            }
        }

        [Fact]
        public void ConnectionCountersTrackActivePeakAndAttemptsAcrossResolutions()
        {
            var f = new AceEngine.Resolve.MetadataFetcher(new EngineOptions(), null, new HttpClient());
            f.NoteConnectionStart(); f.NoteConnectionStart(); f.NoteConnectionStart();
            Assert.Equal(3, f.ActiveConnections); Assert.Equal(3, f.PeakConnections);
            f.NoteConnectionEnd(); f.NoteConnectionEnd();
            Assert.Equal(1, f.ActiveConnections); Assert.Equal(3, f.PeakConnections); Assert.Equal(3, f.ConnectionAttempts);
        }

        [Fact]
        public async Task SearchStatsCountTheTimeAQueryWaitedForAFreeSlot()
        {
            using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var opt = new EngineOptions { DhtMaxInFlight = 2 };
                opt.DhtBootstrap = new List<string> { "127.0.0.1:" + ((IPEndPoint)silent.Client.LocalEndPoint).Port };
                using (var dht = new DhtClient(opt))
                using (var cts = new CancellationTokenSource(900))
                {
                    dht.Start();
                    var st = new DhtSearchStats();
                    var searches = Enumerable.Range(0, 4).Select(i => { var h = new byte[20]; h[0] = (byte)i; return dht.GetPeersAsync(h, ps => { }, TimeSpan.FromSeconds(5), cts.Token, st); }).ToList();
                    try { await Task.WhenAll(searches); } catch (OperationCanceledException) { }
                    Assert.True(st.Queries <= 2, "consultas " + st.Queries);                   // only two slots and nobody answers
                    Assert.True(st.SlotWaits >= 1, "esperas " + st.SlotWaits);                 // the other two searches waited
                    Assert.True(st.SlotWaitSeconds > 0.3, "espera " + st.SlotWaitSeconds);
                    Assert.Equal(2, dht.MaxInFlight);
                    Assert.Equal(8, DhtClient.SearchWidth);
                }
            }
        }

        [Fact]
        public void ProbeDefaultsMatchTheDesign()
        {
            var o = new ProbeOptions();
            Assert.Equal(2.0, o.TrackerInterval.TotalSeconds); Assert.Equal(1, o.TrackerMaxConcurrent);
            Assert.Equal(70, o.UdpBatchSize); Assert.Equal(50, o.HttpBatchSize);
            Assert.False(o.ResolveViaTsService);
            Assert.False(o.RecheckUnresolved);
            Assert.Equal(8, o.ResolveConnectionsPerLink); Assert.Equal(750, o.ResolveConnectionBudget); Assert.Equal(3, o.StarvedRetries);
            Assert.Equal(400, new EngineOptions().DhtMaxInFlight);
        }
    }

    // ---------- Concurrency does not change the result: per-link budget, admission and insufficient resources ----------

    public class ResolutionBudgetTests
    {
        private static LinkProber Make(ProbeOptions po, DhtClient dht = null, Action<EngineOptions> tweak = null)
        {
            var opt = new EngineOptions { EnableDht = false, EnableTrackers = false, Probe = po };
            opt.Resolver.EnableTsService = false;
            opt.Resolver.Timeout = TimeSpan.FromSeconds(30);
            opt.StaticPeers.Add("10.255.255.1:8621");                    // a peer that does not answer: the resolution lasts exactly its deadline
            if (tweak != null) tweak(opt);
            var http = new HttpClient();
            return new LinkProber(new TransportResolver(opt, dht, http), dht, http, po);
        }

        [Theory]
        [InlineData(50, 750, 15, 1000, 50)]       // PC: 50 at once, 15 connections each
        [InlineData(50, 150, 15, 1000, 10)]       // budget of 150: only 10 fit with their full budget
        [InlineData(12, 180, 15, 1000, 12)]       // TV
        [InlineData(50, 750, 15, 128, 16)]        // the DHT queries (8 per search) also count
        [InlineData(5, 750, 15, 1000, 5)]         // Concurrency is the ceiling
        [InlineData(50, 5, 15, 1000, 1)]          // never fewer than one
        public void AdmittedResolutionsAreThoseThatFitTheBudget(int conc, int budget, int perLink, int dhtInFlight, int expected)
        {
            var po = new ProbeOptions { Concurrency = conc, ResolveConnectionBudget = budget, ResolveConnectionsPerLink = perLink };
            var eo = new EngineOptions { DhtMaxInFlight = dhtInFlight };
            using (var dht = new DhtClient(eo))
            {
                var p = new LinkProber(null, dht, new HttpClient(), po);
                Assert.Equal(expected, p.AdmittedResolutions);
            }
        }

        [Fact]
        public void TheTvProfileIsPrudentAndStillFitsItsBudget()
        {
            var o = AceEngine.App.EngineOptionsFactory.Build(new AceEngine.App.AppConfig(), null);
            Assert.Equal(12, o.Probe.Concurrency);
            Assert.Equal(52, o.Probe.TcpBudget);                                       // a single figure for the TCP connections of all the checks
            Assert.Equal(128, o.DhtMaxInFlight);
            using (var dht = new DhtClient(o))
            {
                var p = new LinkProber(null, dht, new HttpClient(), o.Probe);
                Assert.Equal(12, p.AdmittedResolutions);                               // 12 resolutions x 8 DHT queries = 96 of the 128; the connections are limited by TcpBudget
            }
            var pc = new EngineOptions();
            Assert.Equal(50, pc.Probe.Concurrency);                                    // PC: 50 x 15 = 750 connections and 50 x 8 = 400 DHT queries
            using (var dht = new DhtClient(pc))
                Assert.Equal(50, new LinkProber(null, dht, new HttpClient(), pc.Probe).AdmittedResolutions);
        }

        [Fact]
        public async Task TheResolutionDeadlineStartsWhenTheLinkHasItsBudgetNotWhenItJoinsTheQueue()
        {
            // 2 admitted (30 / 15), 6 links at once, deadline of 600 ms: 3 batches. Each one must have its FULL 600 ms (waiting for a turn does not spend deadline).
            var po = new ProbeOptions { Concurrency = 50, ResolveConnectionBudget = 30, ResolveConnectionsPerLink = 15, ResolveTimeout = TimeSpan.FromMilliseconds(600) };
            var p = Make(po);
            Assert.Equal(2, p.AdmittedResolutions);
            var entries = Enumerable.Range(1, 6).Select(i => LinkEntry.FromUrl("c" + i, "acestream://" + i.ToString("x40"))).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var steps = await Task.WhenAll(entries.Select(e => p.ResolveAsync(e, CancellationToken.None)));
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(1700), "tardo " + sw.Elapsed.TotalSeconds);       // 3 batches x 0.6 s
            Assert.Equal(2, p.PeakResolving);                                                                      // never more than the admitted ones at once
            Assert.All(steps, st =>
            {
                Assert.NotNull(st.Final); Assert.True(st.Final.Unresolved);
                Assert.InRange(st.Final.ResolveElapsed.TotalMilliseconds, 500, 1100);                              // its deadline, without the turn wait
                Assert.Equal(ResolveFailure.NoServer, st.Final.Failure);                                          // it had all its resources: nobody serves it
                Assert.NotNull(st.Final.ResolveStats);
                Assert.Equal(15, st.Final.ResolveStats.BudgetConnections);
                Assert.True(st.Final.ResolveStats.Candidates >= 1);
            });
        }

        [Fact]
        public async Task CachedTransportsNeedNeitherATurnNorADeadline()
        {
            var po = new ProbeOptions { Concurrency = 50, ResolveConnectionBudget = 15, ResolveConnectionsPerLink = 15, ResolveTimeout = TimeSpan.FromSeconds(30) };
            using (var tracker = new FakeScrapeUdpTracker())
            {
                var sim = new LiveSimOptions { Trackers = tracker.Url };
                var model = new LiveStreamModel(sim);
                using (var meta = new FakeAceMetadataPeer { FullData = model.TransportBytes })
                {
                    var p = Make(po, null, o => { o.StaticPeers.Clear(); o.StaticPeers.Add(meta.EndPoint.ToString()); o.Resolver.WeakQuorum = 1; });
                    var e = LinkEntry.FromUrl("c", "acestream://54a4a28882d3cae1ca19de5d4b022ebea40351f9");
                    var first = await p.ResolveAsync(e, CancellationToken.None);
                    Assert.NotNull(first.Target); Assert.Equal("magnet", first.Target.Source);
                    Assert.NotNull(first.Target.Stats); Assert.True(first.Target.Stats.HandshakesOk >= 1);
                    // with the only slot taken by another resolution, the cached one goes through anyway
                    var other = LinkEntry.FromUrl("o", "acestream://" + 9.ToString("x40"));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var second = await p.ResolveAsync(e, CancellationToken.None);
                    Assert.Equal("local_db", second.Target.Source);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    public class ResolveFailureTests
    {
        [Fact]
        public void NobodyServesVersusStarved()
        {
            Assert.Equal(ResolveFailure.NoServer, LinkProber.ClassifyFailure(null));
            Assert.Equal(ResolveFailure.NoServer, LinkProber.ClassifyFailure(new ResolveStats { ElapsedSeconds = 12, Attempts = 80, ConnectTimeouts = 60 }));
            Assert.Equal(ResolveFailure.Starved, LinkProber.ClassifyFailure(new ResolveStats { ResourceErrors = 1 }));
            var waited = new ResolveStats { ElapsedSeconds = 10 };
            waited.Dht.SlotWaitMs = 30000;                               // 8 parallel queries waiting for a slot, 3 out of every 8 seconds
            Assert.True(waited.DhtWaitFraction > 0.25);
            Assert.Equal(ResolveFailure.Starved, LinkProber.ClassifyFailure(waited));
            var fine = new ResolveStats { ElapsedSeconds = 10 };
            fine.Dht.SlotWaitMs = 4000;
            Assert.Equal(ResolveFailure.NoServer, LinkProber.ClassifyFailure(fine));
        }

        [Fact]
        public void AttemptOutcomesAreClassified()
        {
            var s = new ResolveStats();
            s.NoteAttempt("connect timeout", false, 0, 8);
            s.NoteAttempt("handshake timeout", false, 0, 15);
            s.NoteAttempt("socket ConnectionRefused", false, 0, 0.1);
            s.NoteAttempt("socket TooManyOpenSockets", false, 0, 0.0);
            s.NoteAttempt("closed by peer", true, 120, 0.5);
            s.NoteAttempt("protocol Foo", true, 80, 0.3);
            s.NoteAttempt("cancelled", false, 0, 1);
            Assert.Equal(7, s.Attempts); Assert.Equal(1, s.ConnectTimeouts); Assert.Equal(1, s.HandshakeTimeouts); Assert.Equal(1, s.Refused); Assert.Equal(1, s.ResourceErrors);
            Assert.Equal(1, s.ClosedByPeer); Assert.Equal(1, s.ProtocolRejected); Assert.Equal(1, s.Cancelled); Assert.Equal(2, s.HandshakesOk);
            Assert.Equal(23, s.SlotSecondsTimeouts, 3);
            Assert.Equal(new[] { 120.0, 80.0 }, s.HandshakeOkMs.ToArray());
        }
    }

    public class StarvedResolutionTests
    {
        private static List<LinkEntry> Entries(int n) { return Enumerable.Range(0, n).Select(i => LinkEntry.FromUrl("Canal " + i, "acestream://" + (i + 1).ToString("x40"), "G")).ToList(); }
        private static ResolveStep Starved() { return new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Unresolved = true, Failure = ResolveFailure.Starved, Error = "sin recursos" } }; }
        private static ResolveStep NoServer() { return new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Unresolved = true, Failure = ResolveFailure.NoServer, Error = "nadie" } }; }

        [Fact]
        public async Task AResolutionThatRanOutOfResourcesIsRetriedAndNeverHiddenInTheMeantime()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.Zero, StarvedRetries = 3, StarvedRetryDelay = TimeSpan.FromMilliseconds(150) } };
            fp.ResolveFn = (e, attempt) => e.Key == "cid:" + 1.ToString("x40") ? (attempt <= 2 ? Starved() : FakePhasedProber.Target(e)) : NoServer();
            var entries = Entries(2);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await Task.Delay(120);
                Assert.Equal(LinkState.Checking, s.GetResult(entries[0]).State);                      // it keeps checking: it is not Unknown, not Dead
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                Assert.Equal(LinkState.Good, s.GetResult(entries[0]).State);                          // on the third attempt it had resources
                Assert.Equal(3, fp.ResolveCalls[entries[0].Key]);
                Assert.Equal(LinkState.Unknown, s.GetResult(entries[1]).State);                       // nobody serves it: definitive, a single attempt
                Assert.Equal(1, fp.ResolveCalls[entries[1].Key]);
                Assert.Equal(2, s.Stats.StarvedRetries); Assert.Equal(0, s.Stats.StarvedGiveUps);
            }
        }

        [Fact]
        public async Task StillWithoutResourcesAfterTheRetriesStaysCheckingAndTheNextCycleTriesAgainAtOnce()
        {
            var fp = new FakePhasedProber { Options = new ProbeOptions { Concurrency = 10, RecheckDelay = TimeSpan.Zero, StarvedRetries = 1, StarvedRetryDelay = TimeSpan.FromMilliseconds(50), CycleInterval = TimeSpan.FromMilliseconds(200), ResultTtl = TimeSpan.FromMinutes(5) } };
            fp.ResolveFn = (e, attempt) => attempt <= 3 ? Starved() : FakePhasedProber.Target(e);
            var entries = Entries(1);
            using (var s = new ProbeScheduler(fp, fp.Options))
            {
                s.Start(entries);
                await s.WaitFirstCycleAsync(new CancellationTokenSource(10000).Token);
                var r = s.GetResult(entries[0]);
                Assert.Equal(LinkState.Checking, r.State); Assert.Equal(ResolveFailure.Starved, r.Failure);         // neither hidden nor dead
                Assert.Empty(s.Snapshot().Where(x => x.Result.State == LinkState.Unknown || x.Result.State == LinkState.Dead));
                Assert.Equal(1, s.Stats.StarvedGiveUps);
                await Task.Delay(1200);                                                                          // the next cycle retries it without waiting for the 5 min TTL
                Assert.Equal(LinkState.Good, s.GetResult(entries[0]).State);
            }
        }
    }
}
