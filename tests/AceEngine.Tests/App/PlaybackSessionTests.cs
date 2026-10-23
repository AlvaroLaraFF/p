using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>Fake player that reads the local URL as the TV's would and lets tests trigger failures.</summary>
    internal sealed class FakePlayerBackend : IPlayerBackend
    {
        public static int Created;
        public Func<int, PlayerFailure> PrepareFailure;          // attempt (1..) -> failure on prepare, or null
        public bool HangOnPrepare;
        public long BytesRead;
        public bool Disposed;
        public int Attempt;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private DateTime _started;
        public event Action<PlayerFailure> Failed;
#pragma warning disable CS0067
        public event Action Completed;
#pragma warning restore CS0067
        public event Action<int> Buffering;
        public long PositionMs { get { return _started == default(DateTime) ? -1 : (long)(DateTime.UtcNow - _started).TotalMilliseconds; } }
        public long DurationMs { get { return -1; } }
        public string StreamInfo { get { return "fake h264 / mp2"; } }

        public void RaiseFailure(PlayerFailure f) { var h = Failed; if (h != null) h(f); }
        public void Pause() { }
        public void Resume() { }
        public Task SeekAsync(long positionMs, CancellationToken ct) { return Task.CompletedTask; }

        public async Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct)
        {
            Attempt = Interlocked.Increment(ref Created);
            if (HangOnPrepare) { await Task.Delay(Timeout.Infinite, ct); }
            var f = PrepareFailure != null ? PrepareFailure(Attempt) : null;
            if (f != null) throw f;
            var h = Buffering; if (h != null) h(100);
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync();
            _started = DateTime.UtcNow;
            var token = _cts.Token;
            var _ = Task.Run(async () =>
            {
                var buf = new byte[32 * 1024];
                try { while (!token.IsCancellationRequested) { int n = await stream.ReadAsync(buf, 0, buf.Length, token); if (n <= 0) break; Interlocked.Add(ref BytesRead, n); } }
                catch { }
                finally { stream.Dispose(); resp.Dispose(); http.Dispose(); }
            });
        }

        public void Dispose() { Disposed = true; _cts.Cancel(); }
    }

    internal sealed class FakePlayerFactory : IPlayerFactory
    {
        public readonly List<FakePlayerBackend> Players = new List<FakePlayerBackend>();
        public Action<FakePlayerBackend> Configure;
        public IPlayerBackend Create()
        {
            var p = new FakePlayerBackend();
            if (Configure != null) Configure(p);
            lock (Players) Players.Add(p);
            return p;
        }
    }

    public class PlaybackSessionTests
    {
        private static LinkEntry Entry(string path)
        {
            return new LinkEntry { Name = "Canal de prueba", Url = path, Kind = LinkKind.AceStream, Link = AceLink.Parse(path), Key = "t:" + path };
        }

        private static PlaybackOptions Fast()
        {
            return new PlaybackOptions { RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(20), PrepareTimeout = TimeSpan.FromSeconds(3) };
        }

        private static async Task<bool> Until(Func<bool> c, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(25); }
            return c();
        }

        [Fact]
        public async Task PlaysALiveStreamThenStopsAndReleasesTheSession()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory();
                using (var pb = new PlaybackSession(h.Host, factory, Fast()))
                {
                    var phases = new List<PlayPhase>();
                    pb.Changed += s => { lock (phases) { if (phases.Count == 0 || phases[phases.Count - 1] != s.Phase) phases.Add(s.Phase); } };
                    var run = pb.RunAsync(Entry(path), "Canal de prueba", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Playing, 20000), "no llego a Playing: " + pb.Status);
                    Assert.True(pb.Status.IsLive);
                    Assert.True(await Until(() => factory.Players[0].BytesRead > 200000, 10000), "the player did not read data");
                    Assert.True(await Until(() => pb.Status.PositionMs >= 300, 5000));
                    pb.Stop();
                    await run;
                    Assert.Equal(PlayPhase.Idle, pb.Status.Phase);
                    Assert.True(factory.Players[0].Disposed);
                    Assert.Empty(h.Host.Server.Sessions);
                    Assert.Contains(PlayPhase.Preparing, phases);
                    Assert.Contains(PlayPhase.Playing, phases);
                }
            }
        }

        [Fact]
        public async Task RetriesALivePlayerErrorThreeTimesThenGivesUp()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                // the player always fails on prepare (ErrorOccurred): 1 attempt + 3 retries and an error
                var factory = new FakePlayerFactory { Configure = p => p.PrepareFailure = n => new PlayerFailure("error del reproductor: InternalError") };
                using (var pb = new PlaybackSession(h.Host, factory, Fast()))
                {
                    var retries = new List<int>();
                    pb.Changed += s => { if (s.Phase == PlayPhase.Retrying) lock (retries) { if (!retries.Contains(s.Retry)) retries.Add(s.Retry); } };
                    await pb.RunAsync(Entry(path), "x", CancellationToken.None);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    Assert.False(pb.Status.ErrorIsFatal);
                    Assert.Equal(4, factory.Players.Count);
                    Assert.Equal(new[] { 1, 2, 3 }, retries.OrderBy(x => x).ToArray());
                    Assert.All(factory.Players, x => Assert.True(x.Disposed));
                }
            }
        }

        [Fact]
        public async Task RecoversWhenTheSecondAttemptWorks()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                int calls = 0;
                var factory = new FakePlayerFactory { Configure = p => p.PrepareFailure = n => Interlocked.Increment(ref calls) == 1 ? new PlayerFailure("error del reproductor: InternalError") : null };
                using (var pb = new PlaybackSession(h.Host, factory, Fast()))
                {
                    var run = pb.RunAsync(Entry(path), "x", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Playing && factory.Players.Count == 2, 25000), "no se recupero: " + pb.Status);
                    Assert.True(await Until(() => factory.Players[1].BytesRead > 100000, 10000), "the second player did not read");
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task ErrorWhilePlayingTriggersAReopenOfTheSameSession()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory();
                using (var pb = new PlaybackSession(h.Host, factory, Fast()))
                {
                    var run = pb.RunAsync(Entry(path), "x", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Playing, 20000));
                    var sessionBefore = pb.EngineSession;
                    factory.Players[0].RaiseFailure(new PlayerFailure("error del reproductor: ConnectionFailed"));
                    Assert.True(await Until(() => factory.Players.Count == 2 && pb.Status.Phase == PlayPhase.Playing, 20000), "no reabrio: " + pb.Status);
                    Assert.Same(sessionBefore, pb.EngineSession);          // same engine session: only the URL is reopened
                    Assert.True(factory.Players[0].Disposed);
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task FatalCodecErrorsAreNotRetried()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory { Configure = p => p.PrepareFailure = n => new PlayerFailure("Formato de audio/vídeo no soportado por la tele (AudioCodecNotSupported)", true) };
                using (var pb = new PlaybackSession(h.Host, factory, Fast()))
                {
                    await pb.RunAsync(Entry(path), "x", CancellationToken.None);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    Assert.True(pb.Status.ErrorIsFatal);
                    Assert.Single(factory.Players);
                    Assert.Contains("no soportado", pb.Status.Error);
                }
            }
        }

        [Fact]
        public async Task PrepareThatNeverEndsTimesOutAndRetries()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory { Configure = p => p.HangOnPrepare = true };
                var opt = Fast(); opt.PrepareTimeout = TimeSpan.FromMilliseconds(300); opt.MaxLiveRetries = 1;
                using (var pb = new PlaybackSession(h.Host, factory, opt))
                {
                    await pb.RunAsync(Entry(path), "x", CancellationToken.None);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    Assert.Equal(2, factory.Players.Count);
                    Assert.Contains("preparar", pb.Status.Error);
                }
            }
        }

        [Fact]
        public async Task UnresolvableContentIdRetriesThenReportsAReadableError()
        {
            var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            opt.Resolver.EnableP2p = false; opt.Resolver.EnableTsService = false; opt.Resolver.Timeout = TimeSpan.FromSeconds(2);
            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var e = LinkEntry.FromUrl("Canal", "acestream://" + new string('c', 40));
                var factory = new FakePlayerFactory();
                var po = Fast(); po.MaxResolveRetries = 2; po.KeepRetryingWithoutSource = false;       // this test looks at the final error; the loop has its own (NoSourceTests)
                using (var pb = new PlaybackSession(host, factory, po))
                {
                    await pb.RunAsync(e, "Canal", CancellationToken.None);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);       // no debug text: "Sin fuentes por ahora"
                    Assert.Empty(factory.Players);
                }
            }
        }

        [Fact]
        public async Task CancellationDuringResolveEndsCleanly()
        {
            var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            opt.Resolver.EnableP2p = false; opt.Resolver.EnableTsService = false;
            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var factory = new FakePlayerFactory();
                using (var pb = new PlaybackSession(host, factory, Fast()))
                using (var cts = new CancellationTokenSource())
                {
                    var run = pb.RunAsync(LinkEntry.FromUrl("C", "acestream://" + new string('d', 40)), "C", cts.Token);
                    cts.Cancel();
                    await run;
                    Assert.True(pb.Status.Phase == PlayPhase.Idle || pb.Status.Phase == PlayPhase.Error);
                }
            }
        }
    }
}
