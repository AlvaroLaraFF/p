using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Tests.NetTests;
using AceEngine.Tests.Timeshift;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>
    /// The IPlayerBackend contract as an executable list. Every player backend that can run on the PC derives from this class and says how to create one; the device players (Tizen,
    /// Android) cannot run here and are checked against the same list by hand on the device (see the F2/F3 steps of the plan). PlaybackSession's retry and state logic relies on
    /// each of these: if a test here is changed, the contract in IPlayerBackend.cs and the session's assumptions change with it.
    /// </summary>
    public abstract class PlayerBackendConformanceTests : IDisposable
    {
        private readonly List<IPlayerBackend> _made = new List<IPlayerBackend>();

        /// <summary>A new backend, not started.</summary>
        internal abstract IPlayerBackend CreateBackend();
        /// <summary>A URL that plays (endless).</summary>
        protected abstract string PlayableUrl { get; }
        /// <summary>A URL where nothing answers: preparing fails with a recoverable PlayerFailure.</summary>
        protected abstract string UnreachableUrl { get; }
        /// <summary>true for a backend that does VOD: duration and seeking are tested.</summary>
        protected virtual bool IsVod { get { return false; } }
        /// <summary>Makes preparing take several seconds, to cancel or dispose it midway.</summary>
        internal abstract void MakePrepareSlow(IPlayerBackend b);
        internal abstract void RaiseFailure(IPlayerBackend b, PlayerFailure f);
        internal abstract void RaiseCompleted(IPlayerBackend b);
        internal abstract void RaiseBuffering(IPlayerBackend b, int percent);

        private IPlayerBackend New() { var b = CreateBackend(); lock (_made) _made.Add(b); return b; }
        private async Task<IPlayerBackend> StartedAsync(bool live = true)
        {
            var b = New();
            await b.PrepareAndStartAsync(PlayableUrl, live, CancellationToken.None);
            return b;
        }

        public virtual void Dispose() { lock (_made) foreach (var b in _made) { try { b.Dispose(); } catch { } } }

        // ------------------------------------------------------------------ before and after

        [Fact]
        public void BeforeStartThereIsNoPositionNoDurationAndTheInfoIsNeverNull()
        {
            var b = New();
            Assert.Equal(-1, b.PositionMs);
            Assert.Equal(-1, b.DurationMs);
            Assert.NotNull(b.StreamInfo);
        }

        [Fact]
        public void PauseAndResumeBeforeStartDoNothingAndDoNotThrow()
        {
            var b = New();
            b.Pause(); b.Resume(); b.Pause();
            Assert.Equal(-1, b.PositionMs);
        }

        [Fact]
        public async Task SeekBeforeStartDoesNotThrow()
        {
            await New().SeekAsync(1000, CancellationToken.None);
        }

        // ------------------------------------------------------------------ playing

        [Fact]
        public async Task OnceStartedThePositionAdvancesAndTheInfoIsFilled()
        {
            var b = await StartedAsync();
            long p1 = b.PositionMs;
            Assert.True(p1 >= 0, "the position must be known once PrepareAndStartAsync has completed");
            await Task.Delay(400);
            long p2 = b.PositionMs;
            Assert.True(p2 >= p1 + 200, "the position does not advance while playing: " + p1 + " -> " + p2);
            Assert.NotNull(b.StreamInfo);
        }

        [Fact]
        public async Task ALiveStreamHasNoDuration()
        {
            var b = await StartedAsync(live: true);
            Assert.Equal(-1, b.DurationMs);
        }

        [Fact]
        public async Task AVodStreamReportsItsDuration()
        {
            if (!IsVod) return;
            var b = await StartedAsync(live: false);
            Assert.True(b.DurationMs > 0);
        }

        [Fact]
        public async Task ThePositionNeverGoesBackwardsInNormalPlayback()
        {
            var b = await StartedAsync();
            long last = -1;
            for (int i = 0; i < 10; i++)
            {
                long p = b.PositionMs;
                Assert.True(p >= last, "position went back: " + last + " -> " + p);
                last = p;
                await Task.Delay(30);
            }
        }

        // ------------------------------------------------------------------ pause and resume

        [Fact]
        public async Task PauseStopsThePositionAndResumeMovesItAgain()
        {
            var b = await StartedAsync();
            await Task.Delay(100);
            b.Pause();
            long p1 = b.PositionMs;
            await Task.Delay(350);
            long p2 = b.PositionMs;
            Assert.True(p2 - p1 <= 60, "the position kept advancing while paused: " + p1 + " -> " + p2);
            b.Resume();
            await Task.Delay(350);
            long p3 = b.PositionMs;
            Assert.True(p3 >= p2 + 200, "the position did not advance after resume: " + p2 + " -> " + p3);
        }

        [Fact]
        public async Task PauseAndResumeAreIdempotent()
        {
            var b = await StartedAsync();
            b.Pause(); b.Pause(); b.Pause();
            long p = b.PositionMs;
            Assert.True(p >= 0);
            b.Resume(); b.Resume(); b.Resume();
            await Task.Delay(250);
            Assert.True(b.PositionMs > p);
        }

        // ------------------------------------------------------------------ seek

        [Fact]
        public async Task SeekMovesTheVodPositionAndIsHarmlessOnLive()
        {
            if (IsVod)
            {
                var b = await StartedAsync(live: false);
                await b.SeekAsync(30000, CancellationToken.None);
                long p = b.PositionMs;
                Assert.InRange(p, 30000, 31500);
            }
            else
            {
                var b = await StartedAsync(live: true);
                await b.SeekAsync(30000, CancellationToken.None);          // on live the session reopens with ?back=: a seek is never asked, but must not hurt
                Assert.True(b.PositionMs >= 0);
            }
        }

        // ------------------------------------------------------------------ preparing: cancel, failure, dispose midway

        [Fact]
        public async Task APrepareWithACancelledTokenThrowsOperationCanceledAndTheInstanceCanStillBeDisposed()
        {
            var b = New();
            var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.PrepareAndStartAsync(PlayableUrl, true, cts.Token));
            b.Dispose();
            Assert.Equal(-1, b.PositionMs);
        }

        [Fact]
        public async Task CancellingWhilePreparingStopsPromptly()
        {
            var b = New();
            MakePrepareSlow(b);
            var cts = new CancellationTokenSource();
            var t = b.PrepareAndStartAsync(PlayableUrl, true, cts.Token);
            await Task.Delay(30);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
            Assert.True(sw.ElapsedMilliseconds < 1500, "cancelling took " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public async Task AnUnreachableUrlFailsWithARecoverablePlayerFailure()
        {
            var b = New();
            var f = await Assert.ThrowsAsync<PlayerFailure>(() => b.PrepareAndStartAsync(UnreachableUrl, true, CancellationToken.None));
            Assert.False(f.Fatal);
            Assert.False(string.IsNullOrEmpty(f.Message));
        }

        [Fact]
        public async Task DisposingWhilePreparingIsAllowedAndEndsThePrepare()
        {
            var b = New();
            MakePrepareSlow(b);
            var t = b.PrepareAndStartAsync(PlayableUrl, true, CancellationToken.None);
            await Task.Delay(30);
            b.Dispose();
            var done = await Task.WhenAny(t, Task.Delay(5000));
            Assert.Same(t, done);                                           // it does not hang
            Assert.Equal(-1, b.PositionMs);
            // whatever it ended with, it is a cancellation or a PlayerFailure; never another exception type
            try { await t; } catch (OperationCanceledException) { } catch (PlayerFailure) { }
        }

        // ------------------------------------------------------------------ dispose

        [Fact]
        public async Task DisposeIsIdempotentAndLeavesNoPosition()
        {
            var b = await StartedAsync();
            b.Dispose(); b.Dispose();
            Assert.Equal(-1, b.PositionMs);
            b.Pause(); b.Resume();                                          // harmless on a disposed player
            await b.SeekAsync(1000, CancellationToken.None);
            Assert.Equal(-1, b.PositionMs);
        }

        // ------------------------------------------------------------------ events

        [Fact]
        public async Task EventsAreDeliveredToSubscribersMadeBeforeStarting()
        {
            var b = New();
            PlayerFailure failure = null; int completed = 0; var buffering = new List<int>();
            b.Failed += f => failure = f; b.Completed += () => completed++; b.Buffering += buffering.Add;
            await b.PrepareAndStartAsync(PlayableUrl, true, CancellationToken.None);
            var fail = new PlayerFailure("interrupted", false);
            RaiseFailure(b, fail); RaiseCompleted(b); RaiseBuffering(b, 40); RaiseBuffering(b, 100);
            Assert.Same(fail, failure);
            Assert.Equal(1, completed);
            Assert.Equal(new[] { 40, 100 }, buffering.ToArray());
        }

        [Fact]
        public async Task NoEventIsRaisedAfterDispose()
        {
            var b = await StartedAsync();
            int events = 0;
            b.Failed += f => events++; b.Completed += () => events++; b.Buffering += p => events++;
            b.Dispose();
            RaiseFailure(b, new PlayerFailure("late")); RaiseCompleted(b); RaiseBuffering(b, 10);
            await Task.Delay(50);
            Assert.Equal(0, events);
        }

        // ------------------------------------------------------------------ threads

        [Fact]
        public async Task ConcurrentCallsFromSeveralThreadsAreSafe()
        {
            var b = await StartedAsync();
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var stop = DateTime.UtcNow.AddMilliseconds(400);
            var tasks = new List<Task>();
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        while (DateTime.UtcNow < stop)
                        {
                            long ignored = b.PositionMs; string info = b.StreamInfo; long d = b.DurationMs;
                            if (k % 2 == 0) { b.Pause(); b.Resume(); }
                        }
                    }
                    catch (Exception e) { errors.Enqueue(e); }
                }));
            }
            await Task.WhenAll(tasks);
            Exception first;
            Assert.False(errors.TryDequeue(out first), first == null ? "" : first.ToString());
        }
    }

    /// <summary>The in-memory reference player, VOD and live.</summary>
    public class InMemoryPlayerConformanceTests : PlayerBackendConformanceTests
    {
        internal override IPlayerBackend CreateBackend() { return new InMemoryPlayerBackend(); }
        protected override string PlayableUrl { get { return "mem://stream"; } }
        protected override string UnreachableUrl { get { return "mem://unreachable"; } }
        protected override bool IsVod { get { return true; } }
        internal override void MakePrepareSlow(IPlayerBackend b) { ((InMemoryPlayerBackend)b).PrepareDelayMs = 4000; }
        internal override void RaiseFailure(IPlayerBackend b, PlayerFailure f) { ((InMemoryPlayerBackend)b).RaiseFailure(f); }
        internal override void RaiseCompleted(IPlayerBackend b) { ((InMemoryPlayerBackend)b).RaiseCompleted(); }
        internal override void RaiseBuffering(IPlayerBackend b, int percent) { ((InMemoryPlayerBackend)b).RaiseBuffering(percent); }

        [Fact]
        public async Task AFatalPlayerFailureIsMarkedFatalForUnsupportedMedia()
        {
            var b = new InMemoryPlayerBackend();
            var f = await Assert.ThrowsAsync<PlayerFailure>(() => b.PrepareAndStartAsync("mem://unsupported", false, CancellationToken.None));
            Assert.True(f.Fatal);
        }

        [Fact]
        public async Task SeekBeyondTheEndStopsAtTheDuration()
        {
            var b = new InMemoryPlayerBackend { VodDurationMs = 10000 };
            await b.PrepareAndStartAsync("mem://x", false, CancellationToken.None);
            await b.SeekAsync(999999, CancellationToken.None);
            Assert.Equal(10000, b.PositionMs);
            await b.SeekAsync(-5, CancellationToken.None);
            Assert.InRange(b.PositionMs, 0, 500);
        }
    }

    /// <summary>The fake the engine's tests use, fed by a local HTTP server with an endless stream: it reads the URL like the TV's player.</summary>
    public class TsFakePlayerConformanceTests : PlayerBackendConformanceTests
    {
        private readonly FakeHttpServer _server = new FakeHttpServer();

        public TsFakePlayerConformanceTests()
        {
            _server.Handler = r => new FakeHttpResponse
            {
                Custom = async (s, ct) =>
                {
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: close\r\n\r\n");
                    await s.WriteAsync(head, 0, head.Length, ct);
                    var chunk = new byte[188 * 7];
                    for (int i = 0; i < chunk.Length; i += 188) chunk[i] = 0x47;
                    while (!ct.IsCancellationRequested) { await s.WriteAsync(chunk, 0, chunk.Length, ct); await Task.Delay(10, ct); }
                }
            };
        }

        internal override IPlayerBackend CreateBackend() { return new TsFakePlayer { PrepareDelayMs = 50 }; }
        protected override string PlayableUrl { get { return "http://127.0.0.1:" + _server.Port + "/live"; } }
        protected override string UnreachableUrl { get { return "http://127.0.0.1:1/"; } }
        internal override void MakePrepareSlow(IPlayerBackend b) { ((TsFakePlayer)b).PrepareDelayMs = 4000; }
        internal override void RaiseFailure(IPlayerBackend b, PlayerFailure f) { ((TsFakePlayer)b).RaiseFailure(f); }
        internal override void RaiseCompleted(IPlayerBackend b) { ((TsFakePlayer)b).RaiseCompleted(); }
        internal override void RaiseBuffering(IPlayerBackend b, int percent) { ((TsFakePlayer)b).RaiseBuffering(percent); }

        public override void Dispose() { base.Dispose(); _server.Dispose(); }
    }
}
