using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>Sets up the complete engine (simulated broadcaster with a TS stream with media) with the in-memory live stream enabled.</summary>
    internal static class TsHarness
    {
        /// <summary>1 Mbit/s (131,072 B/s): pulls only a few MiB per minute of stream so the tests (which run in parallel with other memory-sensitive ones) stay light.</summary>
        public const int Bitrate = 131072;
        public static readonly SimTsProfile Profile = TsFeed.Profile(bitrate: Bitrate);

        public static LiveSimOptions Sim(Action<LiveSimOptions> tweak = null)
        {
            var o = new LiveSimOptions { TimeScale = 4, Bitrate = Bitrate, PieceLength = 65536, ChunkLength = 16384, MediaProfile = Profile, WindowPieces = 60 };            // short broadcaster window: its piece cache (double) is what weighs the most
            if (tweak != null) tweak(o);
            return o;
        }

        public static LiveHarness Harness(LiveSimOptions sim, long timeshiftBytes, Action<EngineOptions> tweak = null)
        {
            return new LiveHarness(sim, o => { o.Live.TimeshiftBytes = timeshiftBytes; o.Live.MaxBufferBytes = 4 * 1024 * 1024; if (tweak != null) tweak(o); });
        }

        /// <summary>Waits for the stored window to reach 'seconds'.</summary>
        public static async Task<TimeshiftSnapshot> WaitWindowAsync(LiveSession s, double seconds, int timeoutMs = 60000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                var snap = s.GetTimeshift();
                if (snap.Window.TotalSeconds >= seconds) return snap;
                await Task.Delay(50);
            }
            var last = s.GetTimeshift();
            Assert.True(false, "the window did not reach " + seconds + " s: " + last.Window.TotalSeconds + " s " + s.DebugInfo);
            return last;
        }
    }

    /// <summary>
    /// Minimal HTTP client that reads the live stream's URL like the player and verifies the stream; it can stop reading (pause). The read that is left
    /// pending when the deadline ends is kept for the next call (cancelling an HttpClient read closes the connection).
    /// </summary>
    internal sealed class TsClient : IDisposable
    {
        private readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private HttpResponseMessage _resp;
        private System.IO.Stream _stream;
        private readonly byte[] _buf = new byte[32 * 1024];
        private Task<int> _pending;
        public readonly SimStreamVerifier Verifier;
        public long Bytes;
        public bool Ended;
        public System.Net.HttpStatusCode Status;

        private TsClient(SimTsProfile profile) { Verifier = new SimStreamVerifier(profile ?? TsHarness.Profile); }

        public static async Task<TsClient> OpenAsync(string url, SimTsProfile profile = null)
        {
            var c = new TsClient(profile);
            c._resp = await c._http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            c.Status = c._resp.StatusCode;
            if (c._resp.IsSuccessStatusCode) c._stream = await c._resp.Content.ReadAsStreamAsync();
            return c;
        }

        /// <summary>Reads everything from the connection for 'ms' real ms (or up to maxBytes). Returns false if the connection ended.</summary>
        public async Task<bool> ReadForAsync(int ms, long maxBytes = long.MaxValue)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (Bytes < maxBytes && !Ended)
            {
                if (_pending == null) _pending = _stream.ReadAsync(_buf, 0, _buf.Length);
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                var done = await Task.WhenAny(_pending, Task.Delay(left));
                if (done != _pending) break;
                Consume(await _pending);
            }
            return !Ended;
        }

        private void Consume(int n)
        {
            _pending = null;
            if (n <= 0) { Ended = true; return; }
            Verifier.Feed(_buf, 0, n);
            Bytes += n;
        }

        /// <summary>Reads 'bytes' more bytes (or up to the end). Fails if it takes longer than timeoutMs.</summary>
        public async Task<bool> ReadBytesAsync(long bytes, int timeoutMs = 30000)
        {
            long target = Bytes + bytes;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Bytes < target && !Ended)
            {
                if (_pending == null) _pending = _stream.ReadAsync(_buf, 0, _buf.Length);
                var left = deadline - DateTime.UtcNow;
                var done = left <= TimeSpan.Zero ? null : await Task.WhenAny(_pending, Task.Delay(left));
                if (done != _pending) throw new TimeoutException("read of " + bytes + " bytes: only got " + (Bytes - (target - bytes)));
                Consume(await _pending);
            }
            return !Ended;
        }

        public void Dispose()
        {
            try { if (_stream != null) _stream.Dispose(); } catch { }
            try { if (_resp != null) _resp.Dispose(); } catch { }
            _http.Dispose();
            if (_pending != null) { var ignored = _pending.ContinueWith(t => { var e = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        }
    }
}
