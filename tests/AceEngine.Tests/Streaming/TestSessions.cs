using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Streaming;

namespace AceEngine.Tests.Streaming
{
    /// <summary>In-memory VOD with configurable state and latency.</summary>
    internal class MemorySession : IStreamSession, ISessionReadiness
    {
        public readonly byte[] Data;
        public SessionState StateValue = SessionState.Playing;
        public Task ReadyTask = Task.CompletedTask;
        public int ReaderDelayMs;
        public volatile bool Disposed;
        public int OpenedReaders;
        public readonly List<long> OpenedOffsets = new List<long>();

        public MemorySession(byte[] data, string contentType = "video/mp4")
        {
            Data = data; ContentType = contentType; Id = AceEngine.Streaming.LocalFileSession.NewId();
        }

        public string Id { get; private set; }
        public bool IsLive { get { return false; } }
        public long? Length { get { return Data.Length; } }
        public string ContentType { get; private set; }
        public SessionState State { get { return StateValue; } }
        public SessionStats Stats { get { return new SessionStats(); } }
        public event EventHandler<SessionState> StateChanged;

        public Task WaitUntilReadyAsync(CancellationToken ct) { return ReadyTask; }

        public Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenedReaders);
            lock (OpenedOffsets) OpenedOffsets.Add(offset);
            return Task.FromResult<IStreamReader>(new Reader(this, offset));
        }

        public void Dispose()
        {
            Disposed = true;
            StateValue = SessionState.Stopped;
            var h = StateChanged; if (h != null) h(this, SessionState.Stopped);
        }

        private sealed class Reader : IStreamReader
        {
            private readonly MemorySession _s;
            private long _pos;
            public Reader(MemorySession s, long pos) { _s = s; _pos = pos; }
            public long Position { get { return _pos; } }
            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_s.ReaderDelayMs > 0) await Task.Delay(_s.ReaderDelayMs, ct);
                if (_s.ReaderDelayMs < 0) await Task.Delay(Timeout.Infinite, ct);    // total blocking
                int n = (int)Math.Min(count, _s.Data.Length - _pos);
                if (n <= 0) return 0;
                Buffer.BlockCopy(_s.Data, (int)_pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            public void Dispose() { }
        }
    }

    /// <summary>Simulated live stream: the test pushes blocks with Push and closes with Complete.</summary>
    internal sealed class PushLiveSession : IStreamSession
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _blocks = new List<byte[]>();
        private bool _completed;
        private TaskCompletionSource<bool> _signal = new TaskCompletionSource<bool>();

        public PushLiveSession() { Id = AceEngine.Streaming.LocalFileSession.NewId(); }
        public string Id { get; private set; }
        public bool IsLive { get { return true; } }
        public long? Length { get { return null; } }
        public string ContentType { get { return "video/mp2t"; } }
        public SessionState State { get; set; } = SessionState.Playing;
        public SessionStats Stats { get { return new SessionStats(); } }
#pragma warning disable CS0067
        public event EventHandler<SessionState> StateChanged;
#pragma warning restore CS0067
        public volatile bool Disposed;

        public void Push(byte[] block)
        {
            TaskCompletionSource<bool> old;
            lock (_gate) { _blocks.Add(block); old = _signal; _signal = new TaskCompletionSource<bool>(); }
            old.TrySetResult(true);
        }

        public void Complete()
        {
            TaskCompletionSource<bool> old;
            lock (_gate) { _completed = true; old = _signal; _signal = new TaskCompletionSource<bool>(); }
            old.TrySetResult(true);
        }

        public Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct)
        {
            return Task.FromResult<IStreamReader>(new Reader(this));
        }

        public void Dispose() { Disposed = true; }

        private sealed class Reader : IStreamReader
        {
            private readonly PushLiveSession _s;
            private int _block;
            private int _inBlock;
            private long _pos;
            public Reader(PushLiveSession s) { _s = s; }
            public long Position { get { return _pos; } }
            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                while (true)
                {
                    Task wait;
                    lock (_s._gate)
                    {
                        if (_block < _s._blocks.Count)
                        {
                            var b = _s._blocks[_block];
                            int n = Math.Min(count, b.Length - _inBlock);
                            Buffer.BlockCopy(b, _inBlock, buffer, offset, n);
                            _inBlock += n; _pos += n;
                            if (_inBlock == b.Length) { _block++; _inBlock = 0; }
                            return n;
                        }
                        if (_s._completed) return 0;
                        wait = _s._signal.Task;
                    }
                    var tcs = new TaskCompletionSource<bool>();
                    using (ct.Register(() => tcs.TrySetCanceled()))
                        await Task.WhenAny(wait, tcs.Task);
                    ct.ThrowIfCancellationRequested();
                }
            }
            public void Dispose() { }
        }
    }
}
