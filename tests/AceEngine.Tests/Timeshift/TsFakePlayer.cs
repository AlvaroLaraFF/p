using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Sim;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>
    /// Fake player for the in-memory live stream: reads the local URL like the TV's one (without overrunning the buffer: on pause it stops reading and sends
    /// TCP backpressure to the server), has a position that stops on pause and can simulate the server cutting its connection.
    /// It keeps the IPlayerBackend contract (see PlayerBackendConformanceTests).
    /// </summary>
    internal sealed class TsFakePlayer : IPlayerBackend
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Stopwatch _clock = new Stopwatch();
        private volatile bool _paused;
        private HttpClient _http;
        private HttpResponseMessage _resp;
        public string Url;
        public int PrepareDelayMs = 200;
        public int Index;
        public long BytesRead;
        public volatile bool Disposed;
        public volatile bool Ended;
        public readonly SimStreamVerifier Verifier = new SimStreamVerifier(TsHarness.Profile);
        public int PauseCalls, ResumeCalls;

        private volatile bool _started;

        public event Action<PlayerFailure> Failed;
        public event Action Completed;
        public event Action<int> Buffering;

        public bool IsPaused { get { return _paused; } }
        public long PositionMs { get { return Disposed || !_started ? -1 : _clock.ElapsedMilliseconds; } }
        public long DurationMs { get { return -1; } }
        public string StreamInfo { get { return "fake h264 / aac"; } }

        // no event after Dispose (IPlayerBackend contract)
        public void RaiseFailure(PlayerFailure f) { var h = Failed; if (h != null && !Disposed) h(f); }
        public void RaiseCompleted() { var h = Completed; if (h != null && !Disposed) h(); }
        public void RaiseBuffering(int percent) { var h = Buffering; if (h != null && !Disposed) h(percent); }

        public async Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct)
        {
            try { await PrepareCoreAsync(url, ct); }
            catch (OperationCanceledException) { throw; }
            catch (PlayerFailure) { throw; }
            catch (Exception ex) { throw new PlayerFailure("fake player: " + ex.GetType().Name + " " + ex.Message); }       // contract: any failure is a PlayerFailure
        }

        private async Task PrepareCoreAsync(string url, CancellationToken callerToken)
        {
            Url = url;
            // disposing the player while it prepares ends the preparation too (IPlayerBackend contract)
            var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _cts.Token);
            var ct = linked.Token;
            if (PrepareDelayMs > 0) await Task.Delay(PrepareDelayMs, ct);
            ct.ThrowIfCancellationRequested();
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            _resp.EnsureSuccessStatusCode();
            var stream = await _resp.Content.ReadAsStreamAsync();
            _started = true;
            _clock.Start();
            var token = _cts.Token;
            var ignored = Task.Run(async () =>
            {
                var buf = new byte[32 * 1024];
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (_paused) { await Task.Delay(20); continue; }
                        // the read is not cancelled (cancelling closes the HttpClient connection): the read in progress ends with the end of stream
                        int n = await stream.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) { Ended = true; if (!token.IsCancellationRequested) RaiseCompleted(); break; }
                        Verifier.Feed(buf, 0, n);
                        Interlocked.Add(ref BytesRead, n);
                    }
                }
                catch { }
            });
        }

        public void Pause() { _paused = true; _clock.Stop(); Interlocked.Increment(ref PauseCalls); }
        public void Resume() { _paused = false; if (_started && !Disposed) _clock.Start(); Interlocked.Increment(ref ResumeCalls); }
        public Task SeekAsync(long positionMs, CancellationToken ct) { return Task.CompletedTask; }

        public void Dispose()
        {
            Disposed = true;
            _clock.Stop();
            _cts.Cancel();
            try { if (_resp != null) _resp.Dispose(); } catch { }
            try { if (_http != null) _http.Dispose(); } catch { }
        }
    }

    internal sealed class TsFakePlayerFactory : IPlayerFactory
    {
        public readonly List<TsFakePlayer> Players = new List<TsFakePlayer>();
        public Action<TsFakePlayer> Configure;
        public IPlayerBackend Create()
        {
            var p = new TsFakePlayer();
            lock (Players) { p.Index = Players.Count + 1; Players.Add(p); }
            if (Configure != null) Configure(p);
            return p;
        }
        public TsFakePlayer Last { get { lock (Players) return Players.Count == 0 ? null : Players[Players.Count - 1]; } }
        public int Count { get { lock (Players) return Players.Count; } }
    }
}
