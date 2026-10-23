using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Streaming
{
    /// <summary>
    /// Test session: serves a local file as if it were a VOD. Each reader opens its own FileStream.
    /// It is used to validate the HTTP server without a network and as the basis of `ace serve`.
    /// </summary>
    public sealed class LocalFileSession : IStreamSession
    {
        private readonly string _path;
        private volatile SessionState _state = SessionState.Playing;

        public LocalFileSession(string path, string contentType = null)
        {
            _path = path;
            Length = new FileInfo(path).Length;
            ContentType = contentType ?? MimeTypes.GetMimeType(path);
            Id = NewId();
        }

        public static string NewId()
        {
            var b = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Hex.ToHex(b);
        }

        public string Id { get; private set; }
        public bool IsLive { get { return false; } }
        public long? Length { get; private set; }
        public string ContentType { get; private set; }
        public SessionState State { get { return _state; } }
        public SessionStats Stats { get { return new SessionStats { BufferPercent = 100 }; } }

        public event EventHandler<SessionState> StateChanged;

        public Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_state == SessionState.Stopped) throw new ObjectDisposedException("LocalFileSession");
            if (offset < 0 || offset > Length.Value) throw new ArgumentOutOfRangeException("offset");
            return Task.FromResult<IStreamReader>(new FileReader(_path, offset));
        }

        public void Dispose()
        {
            if (_state == SessionState.Stopped) return;
            _state = SessionState.Stopped;
            var h = StateChanged;
            if (h != null) h(this, SessionState.Stopped);
        }

        private sealed class FileReader : IStreamReader
        {
            private readonly FileStream _fs;
            public FileReader(string path, long offset)
            {
                _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                _fs.Seek(offset, SeekOrigin.Begin);
            }
            public long Position { get { return _fs.Position; } }
            public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                return await _fs.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
            }
            public void Dispose() { _fs.Dispose(); }
        }
    }
}
