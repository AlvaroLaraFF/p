using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.Sim;
using AceEngine.Streaming;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>End to end: simulated broadcaster -> swarm -> pump -> circular buffer -> local HTTP server -> real HTTP client.</summary>
    [Collection("Timeshift e2e")]
    public class TimeshiftServingTests
    {
        private static readonly SimTsProfile P = TsHarness.Profile;
        private const long Mib = 1024 * 1024;

        private static void AssertClean(SimStreamVerifier v, string what = "")
        {
            Assert.True(v.Bytes > 0, what + " nothing was received");
            Assert.Equal(0, v.SyncErrors);
            Assert.Equal(0, v.CorruptPackets);
            Assert.Equal(0, v.Duplicates);
            Assert.True(v.Gaps.Count == 0, what + " huecos: " + string.Join(",", v.Gaps));
        }

        private static async Task<bool> Until(Func<bool> c, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(25); }
            return c();
        }

        [Fact]
        public async Task LiveThroughTheBufferIsContinuousAlignedAndHasAWindow()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 4 * Mib))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                Assert.True(h.Session.TimeshiftEnabled);
                using (var c = await TsClient.OpenAsync(h.Opened.Url))
                {
                    Assert.Equal(HttpStatusCode.OK, c.Status);
                    await c.ReadForAsync(6000);
                    Assert.True(c.Bytes > 3 * Mib / 2, "bytes " + c.Bytes);
                    AssertClean(c.Verifier);
                }
                var snap = h.Session.GetTimeshift();
                Assert.True(snap.Enabled);
                Assert.True(snap.Window.TotalSeconds > 5, "ventana " + snap.Window);
                Assert.Empty(src.Violations);
                Assert.Equal(0, h.Session.LiveInfo.Rehooks);
            }
        }

        [Fact]
        public async Task BackStartsAboutTenSecondsBeforeTheLivePointAndRunsOnToTheEdgeWithoutGaps()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 5 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                await TsHarness.WaitWindowAsync(h.Session, 25);
                using (var live = await TsClient.OpenAsync(h.Opened.Url))
                using (var back = await TsClient.OpenAsync(h.Opened.Url + "?back=10000"))
                {
                    await live.ReadBytesAsync(1 * Mib);
                    await back.ReadBytesAsync(1 * Mib);
                    Assert.Equal(HttpStatusCode.OK, back.Status);
                    AssertClean(live.Verifier, "directo");
                    AssertClean(back.Verifier, "back");
                    double diff = P.TimeOf(live.Verifier.FirstPacket) - P.TimeOf(back.Verifier.FirstPacket);
                    // 10 s behind the live point, aligned to the previous GOP (1 s); the two connections open a few tenths of a second apart
                    Assert.InRange(diff, 9.7, 10.0 + 1.1 + 1.5);
                    // the lagging stream is served from memory (faster than the live stream) and stays continuous
                    await back.ReadBytesAsync(4 * Mib);
                    AssertClean(back.Verifier, "back (more)");
                }
            }
        }

        [Fact]
        public async Task StartsAtARandomAccessPointWithPatAndPmtFirst()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 4 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                await TsHarness.WaitWindowAsync(h.Session, 15);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                using (var resp = await http.GetAsync(h.Opened.Url + "?back=5000", HttpCompletionOption.ResponseHeadersRead))
                using (var st = await resp.Content.ReadAsStreamAsync())
                {
                    var first = new byte[188 * 3];
                    int got = 0;
                    while (got < first.Length) { int n = await st.ReadAsync(first, got, first.Length - got); if (n <= 0) break; got += n; }
                    Assert.Equal(first.Length, got);
                    Assert.Equal(0x47, first[0]); Assert.Equal(0x47, first[188]); Assert.Equal(0x47, first[376]);
                    Assert.Equal(0, ((first[1] & 0x1F) << 8) | first[2]);                       // PAT
                    Assert.Equal(SimTs.PmtPid, ((first[189] & 0x1F) << 8) | first[190]);        // PMT
                    Assert.True((first[377] & 0x40) != 0 && (first[381] & 0x40) != 0);          // start of GOP: PUSI + random_access
                }
            }
        }

        [Fact]
        public async Task BackIsClippedToTheWindowAndABadBackIsRejected()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 2 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                await TsHarness.WaitWindowAsync(h.Session, 1);
                using (var c = await TsClient.OpenAsync(h.Opened.Url + "?back=3600000"))        // one hour: it is trimmed to what is stored
                {
                    Assert.Equal(HttpStatusCode.OK, c.Status);
                    await c.ReadBytesAsync(512 * 1024);
                    AssertClean(c.Verifier);
                }
                foreach (var bad in new[] { "?back=abc", "?back=-5", "?back=", "?back=99999999999" })
                    using (var c = await TsClient.OpenAsync(h.Opened.Url + bad))
                        Assert.Equal(HttpStatusCode.BadRequest, c.Status);
                using (var c = await TsClient.OpenAsync(h.Opened.Url + "?foo=1&back=0"))        // unknown parameters are ignored
                {
                    Assert.Equal(HttpStatusCode.OK, c.Status);
                    await c.ReadBytesAsync(256 * 1024);
                }
            }
        }

        [Fact]
        public async Task BackIsIgnoredWhenTheSessionHasNoMemory()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 0))                              // without an in-memory live stream: as always
            {
                h.AddSource();
                await h.OpenAsync();
                Assert.False(h.Session.TimeshiftEnabled);
                Assert.False(h.Session.GetTimeshift().Enabled);
                using (var c = await TsClient.OpenAsync(h.Opened.Url + "?back=10000"))
                {
                    Assert.Equal(HttpStatusCode.OK, c.Status);
                    await c.ReadForAsync(3000);
                    Assert.True(c.Bytes > 300000);
                    AssertClean(c.Verifier);
                }
            }
        }

        [Fact]
        public async Task APausedClientIsNotCutAndResumesWithoutLossOrJumps()
        {
            // the server has no deadline of its own for a client that does not read (only TCP backpressure): ReadStallTimeout at 1 s proves it
            using (var h = TsHarness.Harness(TsHarness.Sim(), 5 * Mib, o => o.Http.ReadStallTimeout = TimeSpan.FromSeconds(1)))
            {
                h.AddSource();
                await h.OpenAsync();
                using (var c = await TsClient.OpenAsync(h.Opened.Url))
                {
                    await c.ReadBytesAsync(1 * Mib / 2);
                    long beforePause = c.Verifier.LastPacket;
                    await Task.Delay(6000);                                                     // pause: 24 s of stream without reading (window of ~50 s)
                    Assert.True(h.Session.GetTimeshift().Window.TotalSeconds > 12, "ventana " + h.Session.GetTimeshift().Window);
                    Assert.False(c.Ended);
                    await c.ReadBytesAsync(5 * Mib / 2);
                    AssertClean(c.Verifier, "after the pause");
                    Assert.False(c.Ended);
                    Assert.True(c.Verifier.LastPacket > beforePause + 8000);
                    var snap = h.Session.GetTimeshift();
                    Assert.True(snap.ReaderOpen && !snap.ReaderEvicted);
                    Assert.True(snap.ReaderBehindMs >= -10100, "the cursor cannot go past the edge: " + snap.ReaderBehindMs);
                }
            }
        }

        [Fact]
        public async Task APauseLongerThanTheWindowEvictsTheCursorAndTheConnectionEndsCleanly()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 2 * Mib))                       // 16 s stored, ~6 s of window
            {
                h.AddSource();
                await h.OpenAsync();
                using (var c = await TsClient.OpenAsync(h.Opened.Url))
                {
                    await c.ReadBytesAsync(512 * 1024);
                    await Task.Delay(1500);
                    Assert.False(h.Session.GetTimeshift().ReaderEvicted);
                    await Task.Delay(8000);                                                     // 32+ s of stream without reading: the cursor leaves the window
                    var snap = h.Session.GetTimeshift();
                    Assert.True(snap.ReaderEvicted, "the cursor should have been evicted: behind=" + snap.ReaderBehindMs + " window=" + snap.Window.TotalMilliseconds);
                    // the client unblocks: it receives what was already in the socket buffers and then the end of stream, cleanly
                    bool open = true;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (open && sw.Elapsed.TotalSeconds < 20) open = await c.ReadForAsync(1000);
                    Assert.False(open, "the connection had to end");
                    Assert.True(c.Ended);
                    AssertClean(c.Verifier, "up to the cut");
                }
                // the session is still alive: a new live connection works
                using (var c2 = await TsClient.OpenAsync(h.Opened.Url))
                {
                    await c2.ReadBytesAsync(512 * 1024);
                    AssertClean(c2.Verifier, "reapertura");
                }
            }
        }

        [Fact]
        public async Task SeveralConnectionsLiveTogetherEachWithItsOwnCursor()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 5 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                await TsHarness.WaitWindowAsync(h.Session, 20);
                using (var a = await TsClient.OpenAsync(h.Opened.Url))
                {
                    await a.ReadBytesAsync(512 * 1024);
                    // the player reopens (with another position) before the previous one closes: both stay alive
                    using (var b = await TsClient.OpenAsync(h.Opened.Url + "?back=12000"))
                    {
                        await Task.WhenAll(a.ReadBytesAsync(2 * Mib), b.ReadBytesAsync(2 * Mib));
                        AssertClean(a.Verifier, "a");
                        AssertClean(b.Verifier, "b");
                        Assert.False(a.Ended || b.Ended);
                        Assert.True(P.TimeOf(a.Verifier.FirstPacket) > P.TimeOf(b.Verifier.FirstPacket) + 10);
                        Assert.Equal(2, h.Session.TimeshiftBuffer.OpenReaders);
                    }
                    await a.ReadBytesAsync(512 * 1024);
                    AssertClean(a.Verifier, "a (after closing b)");
                }
                Assert.True(await Until(() => h.Session.TimeshiftBuffer.OpenReaders == 0, 5000));
            }
        }

        [Fact]
        public async Task ReopeningTheUrlGivesTheLivePointImmediatelyWithoutRehooking()
        {
            using (var h = TsHarness.Harness(TsHarness.Sim(), 4 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                long last1;
                using (var c = await TsClient.OpenAsync(h.Opened.Url)) { await c.ReadBytesAsync(1 * Mib); last1 = c.Verifier.LastPacket; }
                await Task.Delay(3000);                                                         // 12 s of stream with nobody reading
                using (var c = await TsClient.OpenAsync(h.Opened.Url))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await c.ReadBytesAsync(256 * 1024);
                    Assert.True(sw.ElapsedMilliseconds < 2000, "took " + sw.ElapsedMilliseconds + " ms: it must be served from memory");
                    Assert.True(c.Verifier.FirstPacket > last1, "it jumps to the live point");
                }
                Assert.Equal(0, h.Session.LiveInfo.Rehooks);
            }
        }

        private sealed class HoldSession : IStreamSession, ISessionHold
        {
            public volatile bool Hold;
            public volatile bool Disposed;
            public HoldSession() { Id = LocalFileSession.NewId(); }
            public string Id { get; private set; }
            public bool IsLive { get { return true; } }
            public long? Length { get { return null; } }
            public string ContentType { get { return "video/mp2t"; } }
            public SessionState State { get { return SessionState.Playing; } }
            public SessionStats Stats { get { return new SessionStats(); } }
            public bool HoldOpen { get { return Hold; } }
#pragma warning disable CS0067
            public event EventHandler<SessionState> StateChanged;
#pragma warning restore CS0067
            public Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct) { throw new NotSupportedException(); }
            public void Dispose() { Disposed = true; }
        }

        [Fact]
        public async Task ASessionOnHoldIsNotReapedForInactivityUntilItIsReleased()
        {
            using (var srv = new LocalHttpServer(new HttpServerOptions { SessionIdleTimeout = TimeSpan.FromSeconds(1) }))
            using (var http = new HttpClient())
            {
                srv.Start();
                var s = new HoldSession { Hold = true };
                var url = srv.Register(s);
                var r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));        // one use (no connections afterwards)
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                await Task.Delay(3500);                                                             // more than the inactivity deadline
                Assert.False(s.Disposed);
                Assert.Single(srv.Sessions);
                s.Hold = false;
                Assert.True(await Until(() => s.Disposed, 5000), "it should stop when released");
            }
        }

        [Fact]
        public async Task MemoryIsBoundedOverSeveralMinutesOfStreamWithTheBufferOn()
        {
            // 10 real s at speed x30 = 5 minutes of live stream (more than 3 are requested when consuming at live pace), consumed by a player at live pace; the buffer only stores 4 MiB (32 s)
            // (at x30 the broadcaster needs the long window and the swarm's normal store; so it lasts just enough to pass 3 minutes of stream)
            var sim = TsHarness.Sim(o => { o.TimeScale = 30; o.Bitrate = 131072; o.WindowPieces = 300; });
            sim.MediaProfile = new SimTsProfile { Bitrate = 131072 };
            using (var h = TsHarness.Harness(sim, 4 * Mib, o => o.Live.MaxBufferBytes = 32L * 1024 * 1024))
            {
                var src = h.AddSource(new SimPeerBehavior { NodeType = 2 });
                h.AddSource(new SimPeerBehavior { NodeType = 1, LagPieces = 3 });
                await h.OpenAsync();
                double rate = sim.Bitrate * sim.TimeScale;
                long maxWindowBytes = 0;
                var v = new SimStreamVerifier(sim.MediaProfile);
                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    try
                    {
                        using (var resp = await http.GetAsync(h.Opened.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        using (var st = await resp.Content.ReadAsStreamAsync())
                        {
                            var buf = new byte[32 * 1024];
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            long total = 0;
                            while (true)
                            {
                                int n = await st.ReadAsync(buf, 0, buf.Length, cts.Token);
                                if (n <= 0) break;
                                v.Feed(buf, 0, n); total += n;
                                var tb = h.Session.TimeshiftBuffer;
                                maxWindowBytes = Math.Max(maxWindowBytes, tb.WriteOffset - tb.OldestOffset);
                                double ahead = (total - rate * 3) / rate - sw.Elapsed.TotalSeconds;
                                if (total > rate * 3 && ahead > 0.005) await Task.Delay(TimeSpan.FromSeconds(ahead), cts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                AssertClean(v);
                Assert.True(v.Bytes / (double)sim.Bitrate > 180, "expected >3 min of stream but got " + v.Bytes / (double)sim.Bitrate);
                var buffer = h.Session.TimeshiftBuffer;
                Assert.True(maxWindowBytes <= buffer.CapacityBytes, "buffer memory " + maxWindowBytes);
                Assert.True(buffer.AllocatedBlocks <= buffer.MaxBlocks);
                Assert.True(buffer.RecycledBlocks > 50);                                    // it wrapped around many times
                var info = h.Session.LiveInfo;
                Assert.Equal(0, info.HolesSkipped); Assert.Equal(0, info.Rehooks); Assert.Equal(0, info.SignatureFailures);
                Assert.Empty(src.Violations);
                Assert.True(info.StoredPieces <= 32 * 1024 * 1024 / 65536);
            }
        }

        [Fact]
        public async Task PacedPlaybackKeepsItsDistanceToTheLivePointWithTheBufferOn()
        {
            var sim = TsHarness.Sim(o => o.TimeScale = 5);
            using (var h = TsHarness.Harness(sim, 4 * Mib))
            {
                h.AddSource();
                await h.OpenAsync();
                double rate = sim.Bitrate * sim.TimeScale;
                using (var c = await TsClient.OpenAsync(h.Opened.Url))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 10)
                    {
                        await c.ReadForAsync(250, c.Bytes + (long)(rate * 0.25));
                        double due = (c.Bytes - rate * 2) / rate - sw.Elapsed.TotalSeconds;
                        if (due > 0.01) await Task.Delay(TimeSpan.FromSeconds(due));
                    }
                    AssertClean(c.Verifier);
                    // client's distance to the edge: the start (10 s) plus what fits in the socket buffers, which is not little with this accelerated stream
                    var tb = h.Session.TimeshiftBuffer;
                    var last = new byte[188 * 3];
                    Assert.Equal(last.Length, tb.Peek(tb.WriteOffset - last.Length, last, 0, last.Length));
                    var edge = TsFeed.VideoNumbers(last).Last();
                    double lag = P.TimeOf(edge) - P.TimeOf(c.Verifier.LastPacket);
                    Assert.InRange(lag, 0, 30);
                    Assert.False(h.Session.GetTimeshift().ReaderEvicted);
                }
                Assert.Equal(0, h.Session.LiveInfo.Rehooks);
            }
        }
    }
}
