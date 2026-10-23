using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    internal static class Wait
    {
        public static async Task<bool> Until(Func<bool> cond, int timeoutMs = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(15); }
            return cond();
        }
    }

    public class TsServiceClientObserverTests
    {
        private static ResolverOptions Opt(params string[] hosts)
        {
            return new ResolverOptions { TsHosts = hosts.ToList(), TsMaxHosts = 5, TsRequestTimeout = TimeSpan.FromMilliseconds(400) };
        }

        private static string Id = "c8c33b2673efe7f386790c5966643820578f3535";

        [Fact]
        public async Task EveryRealRequestIsClassified()
        {
            var torrent = Enumerable.Range(0, 100).Select(i => (byte)(i * 3)).ToArray();
            var servers = new List<Tv2HttpServer>();
            Func<Tv2Response, string> host = r => { var s = new Tv2HttpServer(p => r); servers.Add(s); return s.Url; };
            try
            {
                var cases = new Dictionary<TsAttemptKind, Tv2Response>
                {
                    { TsAttemptKind.File, Tv2Response.Text("<response r=\"1\"><torrent>" + Convert.ToBase64String(torrent) + "</torrent></response>", "text/xml") },
                    { TsAttemptKind.Answered, Tv2Response.Text("<response><status>error</status><message>unknown pid</message></response>", "text/xml") },
                    { TsAttemptKind.Empty, new Tv2Response() },
                    { TsAttemptKind.HttpError, new Tv2Response { Status = 451, Body = System.Text.Encoding.UTF8.GetBytes("<html>451</html>") } },
                    { TsAttemptKind.Timeout, new Tv2Response { DelayMs = 2000, Body = new byte[1] } },
                    { TsAttemptKind.Garbage, Tv2Response.Text("<html>HTTP 451 bloqueado</html>", "text/html") },
                };
                foreach (var kv in cases)
                {
                    var seen = new List<TsAttempt>();
                    var o = Opt(host(kv.Value));
                    o.TsObserver = seen.Add;
                    var got = await new TsServiceClient(o, new HttpClient()).GetTorrentAsync(Id, false, CancellationToken.None);
                    Assert.Equal(kv.Key == TsAttemptKind.File ? torrent : null, got);
                    var a = Assert.Single(seen);
                    Assert.Equal(kv.Key, a.Kind);
                    Assert.Equal(kv.Key == TsAttemptKind.File || kv.Key == TsAttemptKind.Answered, a.Success);
                }
                // without a server
                var seen2 = new List<TsAttempt>();
                var o2 = Opt("http://127.0.0.1:9"); o2.TsObserver = seen2.Add;
                await new TsServiceClient(o2, new HttpClient()).GetTorrentAsync(Id, false, CancellationToken.None);
                Assert.Equal(TsAttemptKind.NetworkError, Assert.Single(seen2).Kind);
            }
            finally { foreach (var s in servers) s.Dispose(); }
        }

        [Fact]
        public async Task ACancelledRequestIsNotAFailureOfTheService()
        {
            using (var slow = new Tv2HttpServer(p => new Tv2Response { DelayMs = 3000, Body = new byte[1] }))
            {
                var seen = new List<TsAttempt>();
                var o = Opt(slow.Url); o.TsRequestTimeout = TimeSpan.FromSeconds(10); o.TsObserver = seen.Add;
                using (var cts = new CancellationTokenSource(200))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TsServiceClient(o, new HttpClient()).GetTorrentAsync(Id, false, cts.Token));
                Assert.Empty(seen);
            }
        }

        [Fact]
        public async Task ABrokenObserverNeverBreaksTheResolution()
        {
            using (var srv = new Tv2HttpServer(p => new Tv2Response()))
            {
                var o = Opt(srv.Url); o.TsObserver = a => { throw new InvalidOperationException("roto"); };
                Assert.Null(await new TsServiceClient(o, new HttpClient()).GetTorrentAsync(Id, false, CancellationToken.None));
            }
        }

        [Fact]
        public async Task TheActiveCheckStopsAtTheFirstHostThatAnswers()
        {
            using (var ok = new Tv2HttpServer(p => Tv2Response.Text("<response><status>error</status></response>", "text/xml")))
            {
                var seen = new List<TsAttempt>();
                var o = Opt(ok.Url, ok.Url, ok.Url); o.TsObserver = seen.Add;
                var r = await new TsServiceClient(o, new HttpClient()).CheckAsync(3, CancellationToken.None);
                Assert.True(r.Responding); Assert.Single(r.Attempts);
                Assert.Equal(1, ok.Count);
                Assert.Empty(seen);                                   // the active check does not go through the observer of the real requests
                Assert.Matches("^[0-9a-f]{40}$", Tv2HttpServer.Param(ok.Requests[0], "pid"));
            }
            using (var dead = new Tv2HttpServer(p => new Tv2Response()))
            {
                var r = await new TsServiceClient(Opt(dead.Url, dead.Url, dead.Url, dead.Url, dead.Url), new HttpClient()).CheckAsync(3, CancellationToken.None);
                Assert.False(r.Responding); Assert.Equal(3, r.Attempts.Count);
                Assert.Equal(3, dead.Count);
            }
        }
    }

    public class TsServiceMonitorTests : IDisposable
    {
        private readonly List<IDisposable> _d = new List<IDisposable>();
        public void Dispose() { foreach (var d in _d) try { d.Dispose(); } catch { } }

        private static ResolverOptions Opt(params string[] hosts)
        {
            return new ResolverOptions { TsHosts = hosts.ToList(), TsMaxHosts = 4, TsRequestTimeout = TimeSpan.FromMilliseconds(500) };
        }

        private TsServiceMonitor Mon(UserSettings s, ResolverOptions r = null, Func<DateTime> clock = null, TsServiceMonitorOptions o = null)
        {
            var m = new TsServiceMonitor(s, r, o, clock);
            _d.Add(m);
            return m;
        }

        private static TsAttempt Ok() { return new TsAttempt { Host = "h", Kind = TsAttemptKind.File }; }
        private static TsAttempt Fail(TsAttemptKind k = TsAttemptKind.Empty) { return new TsAttempt { Host = "h", Kind = k }; }

        [Fact]
        public void StateFollowsTheSwitchAndStartsUnknown()
        {
            var off = new UserSettings { TsServiceEnabled = false };
            var m = Mon(off);
            Assert.Equal(TsServiceState.Disabled, m.State);
            m.Record(Ok());                                       // off: ignored
            Assert.Equal(TsServiceState.Disabled, m.State);
            Assert.False(m.RequestCheck(true));
            Assert.Equal(TsServiceState.Unknown, Mon(new UserSettings { TsServiceEnabled = true }).State);
        }

        [Fact]
        public async Task RealRequestsMoveTheStateAfterThreeConsecutiveFailures()
        {
            var m = Mon(new UserSettings { TsServiceEnabled = true });
            int changes = 0;
            m.Changed += () => Interlocked.Increment(ref changes);
            m.Record(Fail()); m.Record(Fail());
            Assert.Equal(TsServiceState.Unknown, m.State);        // 2 failures: not known yet
            m.Record(Fail(TsAttemptKind.Timeout));
            Assert.Equal(TsServiceState.NoResponse, m.State);
            m.Record(Fail(TsAttemptKind.NetworkError));          // more failures: stays the same and does not notify again
            m.Record(Ok());
            Assert.Equal(TsServiceState.Responding, m.State);
            m.Record(Fail()); m.Record(Fail());
            Assert.Equal(TsServiceState.Responding, m.State);    // two failures in a row are not enough
            m.Record(new TsAttempt { Kind = TsAttemptKind.Answered });
            m.Record(Fail()); m.Record(Fail());
            Assert.Equal(TsServiceState.Responding, m.State);    // a success breaks the streak
            m.Record(Fail());
            Assert.Equal(TsServiceState.NoResponse, m.State);
            Assert.True(await Wait.Until(() => Volatile.Read(ref changes) == 3));        // NoResponse, Responding, NoResponse: each change notifies once and only once
            await Task.Delay(100);
            Assert.Equal(3, Volatile.Read(ref changes));
            m.Record(null);                                       // garbage: ignored
        }

        [Fact]
        public async Task RealResolutionsFeedTheMonitorThroughTheEngineOptions()
        {
            using (var ts = new FakeTsService(new ResolverOptions().TsSecret))
            {
                var t = TestUtil.Fixture("real/38f7b2044e549df2039ff26cefa6f9a60c854d5e.acelive");
                string id = Hex.ToHex(HashUtil.Sha1(t));
                ts.Handler = q => "";                              // empty HTTP 200: what the servers answer today
                var settings = new UserSettings { TsServiceEnabled = true };
                var mon = Mon(settings);
                var o = EngineOptionsFactory.Build(new AppConfig(), settings, null);
                o.EnableDht = false; o.EnableTrackers = false; o.Resolver.EnableP2p = false;
                o.Resolver.TsHosts = new List<string> { ts.Url, ts.Url, ts.Url, ts.Url }; o.Resolver.TsMaxHosts = 4;
                mon.Attach(o);
                using (var r = new TransportResolver(o, null))
                {
                    await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None));
                    Assert.Equal(4, ts.Requests.Count);
                    Assert.Equal(TsServiceState.NoResponse, mon.State);                // "Sin respuesta del servidor"
                    // the service works again
                    ts.Handler = q => "<response r=\"1\"><torrent>" + Convert.ToBase64String(t) + "</torrent></response>";
                    Assert.Equal("server", (await r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None)).Source);
                    Assert.Equal(TsServiceState.Responding, mon.State);
                }
            }
        }

        [Fact]
        public async Task TheActiveCheckKnowsTheStateWithoutPlayingAnything()
        {
            using (var empty = new FakeTsService(new ResolverOptions().TsSecret))
            {
                empty.Handler = q => "";
                var m = Mon(new UserSettings { TsServiceEnabled = true }, Opt(empty.Url));
                Assert.True(m.RequestCheck());
                await m.WhenCheckDone;
                Assert.Equal(TsServiceState.NoResponse, m.State);
                Assert.InRange(empty.Requests.Count, 1, 3);
            }
            using (var alive = new FakeTsService(new ResolverOptions().TsSecret))
            {
                alive.Handler = q => "<response><status>error</status></response>";            // alive, but it does not have that channel
                var m = Mon(new UserSettings { TsServiceEnabled = true }, Opt(alive.Url));
                int changes = 0; m.Changed += () => Interlocked.Increment(ref changes);
                Assert.True(m.RequestCheck());
                await m.WhenCheckDone;
                Assert.Equal(TsServiceState.Responding, m.State);
                Assert.Single(alive.Requests);                                                  // with the service alive it is ONE request
                Assert.True(await Wait.Until(() => Volatile.Read(ref changes) == 1));
            }
            // one dead host and one alive: the next one is tried
            using (var alive = new FakeTsService(new ResolverOptions().TsSecret))
            {
                alive.Handler = q => "<response/>";
                var m = Mon(new UserSettings { TsServiceEnabled = true }, Opt("http://127.0.0.1:9", alive.Url));
                m.RequestCheck();
                await m.WhenCheckDone;
                Assert.Equal(TsServiceState.Responding, m.State);
            }
            // the service does not exist
            var none = Mon(new UserSettings { TsServiceEnabled = true }, Opt("http://127.0.0.1:9"));
            none.RequestCheck();
            await none.WhenCheckDone;
            Assert.Equal(TsServiceState.NoResponse, none.State);
        }

        [Fact]
        public async Task ActiveChecksAreSpacedAtLeastTenMinutes()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Text("<response/>", "text/xml")))
            {
                var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
                var m = Mon(new UserSettings { TsServiceEnabled = true }, Opt(srv.Url), () => now);
                Assert.True(m.RequestCheck());
                await m.WhenCheckDone;
                Assert.Equal(1, srv.Count);
                now = now.AddMinutes(9);
                Assert.False(m.RequestCheck()); Assert.Equal(1, srv.Count);
                now = now.AddMinutes(2);
                Assert.True(m.RequestCheck()); await m.WhenCheckDone; Assert.Equal(2, srv.Count);
                Assert.False(m.RequestCheck());                                                  // just done
                Assert.True(m.RequestCheck(true)); await m.WhenCheckDone; Assert.Equal(3, srv.Count);       // asking for it by hand skips it
                // a real request also counts as a recent check
                now = now.AddMinutes(30);
                m.Record(Ok());
                now = now.AddMinutes(5);
                Assert.False(m.RequestCheck());
                now = now.AddMinutes(6);
                Assert.True(m.RequestCheck()); await m.WhenCheckDone;
            }
        }

        [Fact]
        public async Task OnlyOneActiveCheckRunsAtATime()
        {
            using (var slow = new Tv2HttpServer(p => new Tv2Response { DelayMs = 400, Body = System.Text.Encoding.UTF8.GetBytes("<response/>") }))
            {
                var m = Mon(new UserSettings { TsServiceEnabled = true }, Opt(slow.Url));
                Assert.True(m.RequestCheck(true));
                Assert.False(m.RequestCheck(true));
                await m.WhenCheckDone;
                Assert.Equal(1, slow.Count);
            }
        }

        [Fact]
        public async Task TurningTheSwitchOnChecksRightAwayAndOffDisablesIt()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Text("<response/>", "text/xml")))
            {
                var s = new UserSettings { TsServiceEnabled = false };
                var m = Mon(s, Opt(srv.Url));
                int changes = 0; m.Changed += () => Interlocked.Increment(ref changes);
                s.NotifyChanged();                                              // a change that does not affect TSService: nothing
                Assert.Equal(TsServiceState.Disabled, m.State); Assert.Equal(0, srv.Count);
                s.TsServiceEnabled = true; s.NotifyChanged();
                Assert.True(await Wait.Until(() => m.State == TsServiceState.Responding));       // Unknown -> check -> Responding
                Assert.Equal(1, srv.Count);
                s.TsServiceEnabled = false; s.NotifyChanged();
                Assert.Equal(TsServiceState.Disabled, m.State);
                m.Record(Ok());
                Assert.Equal(TsServiceState.Disabled, m.State);
                s.TsServiceEnabled = true; s.NotifyChanged();                    // on reactivating it is checked again (even if little time has passed)
                Assert.True(await Wait.Until(() => srv.Count == 2));
                Assert.True(await Wait.Until(() => Volatile.Read(ref changes) >= 4));
            }
        }

        [Fact]
        public async Task TurningTheSwitchOffCancelsACheckInProgress()
        {
            using (var slow = new Tv2HttpServer(p => new Tv2Response { DelayMs = 1500, Body = System.Text.Encoding.UTF8.GetBytes("<response/>") }))
            {
                var s = new UserSettings { TsServiceEnabled = true };
                var o = Opt(slow.Url); o.TsRequestTimeout = TimeSpan.FromSeconds(10);
                var m = Mon(s, o);
                m.RequestCheck(true);
                await Wait.Until(() => slow.Count == 1);
                s.TsServiceEnabled = false; s.NotifyChanged();
                await m.WhenCheckDone;
                Assert.Equal(TsServiceState.Disabled, m.State);                  // the result of the cancelled check changes nothing
            }
        }

        [Fact]
        public async Task AfterDisposeNothingChanges()
        {
            var s = new UserSettings { TsServiceEnabled = true };
            var m = new TsServiceMonitor(s);
            int changes = 0; m.Changed += () => Interlocked.Increment(ref changes);
            m.Dispose();
            m.Dispose();
            m.Record(Fail()); m.Record(Fail()); m.Record(Fail());
            Assert.False(m.RequestCheck(true));
            s.TsServiceEnabled = false; s.NotifyChanged();
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref changes));
        }

        [Fact]
        public async Task ManyThreadsRecordingAtOnceAreSafe()
        {
            var m = Mon(new UserSettings { TsServiceEnabled = true });
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => { for (int k = 0; k < 200; k++) m.Record((k + i) % 7 == 0 ? Ok() : Fail()); })).ToArray();
            await Task.WhenAll(tasks);
            Assert.True(m.State == TsServiceState.Responding || m.State == TsServiceState.NoResponse);
        }
    }
}
