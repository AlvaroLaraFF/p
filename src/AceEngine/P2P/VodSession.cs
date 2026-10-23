using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>
    /// VOD session over BitTorrent: implements the session contract. The HTTP server
    /// consumes it without knowing what is behind. Each HTTP connection gets its own reader; the last one that reads sets
    /// the playback position of the piece picker.
    /// </summary>
    public sealed class VodSession : IStreamSession, ISessionReadiness
    {
        private readonly Swarm _swarm;
        private readonly Action<VodSession> _onDispose;
        private int _disposed;
        private SessionState _lastState = SessionState.Resolving;

        internal VodSession(Swarm swarm, Action<VodSession> onDispose)
        {
            _swarm = swarm;
            _onDispose = onDispose;
            Id = LocalFileSession.NewId();
            swarm.PhaseChanged += OnPhaseChanged;
        }

        public string Id { get; private set; }
        public bool IsLive { get { return false; } }

        public long? Length { get { return _swarm.HasMetadata ? _swarm.FileLength : (long?)null; } }

        public string ContentType
        {
            get
            {
                var f = _swarm.SelectedFile;
                return f == null ? "application/octet-stream" : MimeTypes.GetMimeType(f.Path);
            }
        }

        /// <summary>Name of the file being played (after resolving the metadata).</summary>
        public string FileName { get { var f = _swarm.SelectedFile; return f == null ? null : f.Path; } }

        /// <summary>Name of the torrent.</summary>
        public string TorrentName { get { var m = _swarm.Meta; return m == null ? null : m.Name; } }

        public byte[] InfoHash { get { return _swarm.InfoHash; } }

        /// <summary>Torrent metadata (null while a magnet resolves them).</summary>
        public Transport.TorrentMeta Meta { get { return _swarm.Meta; } }

        /// <summary>Entry of info['files'] being played (null until there is metadata).</summary>
        public Transport.TorrentFileEntry File { get { return _swarm.SelectedFile; } }

        /// <summary>Diagnostics: internal counters of the swarm.</summary>
        public string DebugInfo { get { return _swarm.DebugInfo(); } }

        public SessionState State
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return SessionState.Stopped;
                if (_swarm.Fatal != null) return SessionState.Failed;
                switch (_swarm.Phase)
                {
                    case SwarmPhase.Resolving: return SessionState.Resolving;
                    case SwarmPhase.Connecting: return SessionState.Connecting;
                    case SwarmPhase.Buffering: return SessionState.Buffering;
                    case SwarmPhase.Playing: return SessionState.Playing;
                    default: return SessionState.Stopped;
                }
            }
        }

        public SessionStats Stats { get { return _swarm.GetStats(); } }

        public Exception Error { get { return _swarm.Fatal; } }

        public event EventHandler<SessionState> StateChanged;

        private void OnPhaseChanged(SwarmPhase phase)
        {
            var st = State;
            var h = StateChanged;
            if (h != null && st != _lastState)
            {
                _lastState = st;
                h(this, st);
            }
        }

        public async Task WaitUntilReadyAsync(CancellationToken ct)
        {
            var t = _swarm.MetadataReady;
            if (!ct.CanBeCanceled) { await t.ConfigureAwait(false); return; }
            var cancel = new TaskCompletionSource<bool>();
            using (ct.Register(() => cancel.TrySetCanceled()))
            {
                var done = await Task.WhenAny(t, cancel.Task).ConfigureAwait(false);
                if (done == cancel.Task) ct.ThrowIfCancellationRequested();
                await t.ConfigureAwait(false);
            }
        }

        public async Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct)
        {
            await WaitUntilReadyAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException("VodSession");
            long len = _swarm.FileLength;
            if (offset < 0 || offset > len) throw new ArgumentOutOfRangeException("offset");
            var reader = new VodReader(_swarm, offset);
            _swarm.RegisterReader(reader, offset);
            return reader;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _swarm.Dispose(); } catch (Exception ex) { AceLog.Warn("session", "swarm Dispose: " + ex.Message); }
            var h = StateChanged;
            if (h != null) { try { h(this, SessionState.Stopped); } catch { } }
            if (_onDispose != null) _onDispose(this);
        }

        private sealed class VodReader : IStreamReader
        {
            private readonly Swarm _swarm;
            private long _pos;
            private volatile bool _disposed;

            public VodReader(Swarm swarm, long pos) { _swarm = swarm; _pos = pos; }

            public long Position { get { return _pos; } }

            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (count <= 0) return 0;
                bool waiting = false;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_disposed) throw new ObjectDisposedException("VodReader");
                        var signal = _swarm.Changed.Wait();            // captured BEFORE checking: wakeups are not lost
                        int n;
                        var st = _swarm.TryRead(_pos, buffer, offset, count, out n);
                        if (st == Swarm.ReadStatus.Data)
                        {
                            _pos += n;
                            _swarm.ReaderMoved(this, _pos, true);
                            return n;
                        }
                        if (st == Swarm.ReadStatus.Eof || st == Swarm.ReadStatus.Stopped) return 0;
                        _swarm.ReaderMoved(this, _pos, false);
                        if (!waiting) { waiting = true; _swarm.ReaderWaiting(true); _swarm.NotifyWaiting(); }
                        await _swarm.Changed.WaitAsync(signal, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (waiting) _swarm.ReaderWaiting(false);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _swarm.UnregisterReader(this);
            }
        }
    }
}
