using System;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Streaming
{
    /// <summary>State of a stream session.</summary>
    public enum SessionState
    {
        Resolving,
        Connecting,
        Buffering,
        Playing,
        Stopped,
        Failed
    }

    /// <summary>Snapshot of statistics for the UI (peers, speed, buffer %, pieces).</summary>
    public sealed class SessionStats
    {
        public int Peers { get; set; }
        public int Seeds { get; set; }
        public double DownloadBytesPerSec { get; set; }
        public double UploadBytesPerSec { get; set; }
        /// <summary>0..100: progress of the prebuffer and, afterwards, fill of the window ahead of playback.</summary>
        public int BufferPercent { get; set; }
        public long BufferedBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public long UploadedBytes { get; set; }
        public int PiecesHave { get; set; }
        public int PiecesTotal { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Contract between the engine and the local HTTP server. A session publishes a stream; each HTTP
    /// connection gets its own reader and marks the playback position for the engine.
    /// </summary>
    public interface IStreamSession : IDisposable
    {
        /// <summary>Random, 16 hex; part of the URL.</summary>
        string Id { get; }
        bool IsLive { get; }
        /// <summary>VOD: file size; live: null.</summary>
        long? Length { get; }
        string ContentType { get; }
        SessionState State { get; }
        SessionStats Stats { get; }
        /// <summary>VOD: offset = start byte. Live: ignored (starts at the live position).</summary>
        Task<IStreamReader> OpenReaderAsync(long offset, CancellationToken ct);
        event EventHandler<SessionState> StateChanged;
    }

    public interface IStreamReader : IDisposable
    {
        /// <summary>
        /// Blocks until there is data. Returns 0 only at the end (VOD) or when the session stops.
        /// Throws OperationCanceledException if ct is cancelled.
        /// </summary>
        Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
        long Position { get; }
    }

    /// <summary>
    /// Optional extension of the contract (our own decision): a session that does not know its
    /// length yet (e.g. a magnet resolving metadata) lets the server wait until it is ready before
    /// answering the GET. Throws if the session fails. That way the URL can be printed before the metadata is available.
    /// </summary>
    public interface ISessionReadiness
    {
        Task WaitUntilReadyAsync(CancellationToken ct);
    }

    /// <summary>
    /// Optional extension of the contract: a live stream with memory (timeshift) can start serving behind the live point. The local
    /// server uses it with the URL parameter ?back=&lt;ms&gt;.
    /// </summary>
    public interface ITimeshiftSession
    {
        /// <summary>true if the session keeps what was already served (otherwise ?back is ignored and the live stream is served as usual).</summary>
        bool TimeshiftEnabled { get; }

        /// <summary>
        /// Reader that starts 'behindLive' behind the live point (clamped to what is stored and aligned to a previous random access point,
        /// with the PAT/PMT prepended); null = at the live point.
        /// </summary>
        Task<IStreamReader> OpenReaderBehindAsync(TimeSpan? behindLive, CancellationToken ct);
    }

    /// <summary>
    /// Optional extension of the contract: a session may ask the local server not to stop it for inactivity even if it has no connections
    /// (SessionIdleTimeout). Live with memory uses it during a pause, in which the player may lose the connection and resumes later.
    /// </summary>
    public interface ISessionHold
    {
        bool HoldOpen { get; }
    }

    /// <summary>Live transfer mode.</summary>
    public enum LiveTransfer
    {
        /// <summary>HTTP/1.1 with Transfer-Encoding: chunked (like the original).</summary>
        Chunked,
        /// <summary>HTTP/1.0 without length and Connection: close: the most compatible for infinite streams.</summary>
        CloseDelimited
    }
}
