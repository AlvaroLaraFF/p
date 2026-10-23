using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>Rate meter with a sliding window (the original's 5 s short_term_rate).</summary>
    public sealed class RateMeter
    {
        private readonly long[] _buckets;
        private readonly int _bucketMs;
        private long _lastTicks;
        private int _cursor;
        private long _total;
        private readonly object _gate = new object();

        public RateMeter(int windowSeconds = 5)
        {
            _bucketMs = 250;
            _buckets = new long[windowSeconds * 1000 / _bucketMs];
            _lastTicks = Environment.TickCount;
        }

        public long Total { get { return Interlocked.Read(ref _total); } }

        public void Add(long bytes)
        {
            lock (_gate)
            {
                Advance();
                _buckets[_cursor] += bytes;
                _total += bytes;
            }
        }

        /// <summary>Bytes per second in the window.</summary>
        public double Rate
        {
            get
            {
                lock (_gate)
                {
                    Advance();
                    long sum = 0;
                    foreach (var b in _buckets) sum += b;
                    return sum * 1000.0 / (_buckets.Length * _bucketMs);
                }
            }
        }

        private void Advance()
        {
            int now = Environment.TickCount;
            int steps = (int)((uint)(now - (int)_lastTicks) / (uint)_bucketMs);
            if (steps <= 0) return;
            if (steps >= _buckets.Length)
            {
                Array.Clear(_buckets, 0, _buckets.Length);
                _cursor = 0;
            }
            else
            {
                for (int i = 0; i < steps; i++)
                {
                    _cursor = (_cursor + 1) % _buckets.Length;
                    _buckets[_cursor] = 0;
                }
            }
            _lastTicks += steps * _bucketMs;
        }
    }

    /// <summary>Asynchronous "something changed" signal: all waiters wake up on every Pulse.</summary>
    public sealed class AsyncSignal
    {
        private TaskCompletionSource<bool> _tcs = NewTcs();
        private readonly object _gate = new object();

        private static TaskCompletionSource<bool> NewTcs()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task Wait()
        {
            lock (_gate) return _tcs.Task;
        }

        public void Pulse()
        {
            TaskCompletionSource<bool> old;
            lock (_gate) { old = _tcs; _tcs = NewTcs(); }
            old.TrySetResult(true);
        }

        /// <summary>Waits for the next Pulse or the cancellation.</summary>
        public async Task WaitAsync(Task current, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) { await current.ConfigureAwait(false); return; }
            var cancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancel.TrySetCanceled()))
            {
                var done = await Task.WhenAny(current, cancel.Task).ConfigureAwait(false);
                if (done == cancel.Task) ct.ThrowIfCancellationRequested();
            }
        }
    }

    public static class PeerId
    {
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz.-";

        /// <summary>
        /// Ace-style peer_id: "R30---" + "---" + 11 characters from [0-9A-Za-z.-] (alphabet[byte &amp; 63]),
        /// 20 bytes in total. prefix is the first 9 characters.
        /// </summary>
        public static byte[] Generate(string prefix = "R30------")
        {
            var id = new byte[20];
            var pre = System.Text.Encoding.ASCII.GetBytes(prefix);
            int n = Math.Min(pre.Length, 20);
            Buffer.BlockCopy(pre, 0, id, 0, n);
            var rnd = new byte[20 - n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            for (int i = 0; i < rnd.Length; i++) id[n + i] = (byte)Alphabet[rnd[i] & 63];
            return id;
        }
    }

    internal static class Rng
    {
        private static readonly object Gate = new object();
        private static readonly Random R = new Random(Guid.NewGuid().GetHashCode());
        public static int Next(int max) { lock (Gate) return R.Next(max); }
        public static int Next(int min, int max) { lock (Gate) return R.Next(min, max); }
        public static double NextDouble() { lock (Gate) return R.NextDouble(); }
        public static void Shuffle<T>(System.Collections.Generic.IList<T> list)
        {
            lock (Gate)
            {
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = R.Next(i + 1);
                    T t = list[i]; list[i] = list[j]; list[j] = t;
                }
            }
        }
    }
}
