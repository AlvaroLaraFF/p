using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>Download phase (the session translates it to SessionState).</summary>
    internal enum SwarmPhase { Resolving, Connecting, Buffering, Playing, Stopped }

    internal sealed class ActivePiece
    {
        public int Index;
        public int Length;
        public int BlockCount;
        public byte[] Data;
        public byte[] State;          // 0 free, 1 requested, 2 received
        public byte[] ReqCount;       // live requests per block (endgame may duplicate)
        public PeerConnection[] Source;
        public int Received;
        public bool Verifying;
        public long[] ReqAtMs;        // when the block was first requested (to duplicate the urgent ones)
        /// <summary>Blocks in state 0 (free). Maintained by hand so that HasFree is O(1).</summary>
        public int Free;

        public bool HasFree { get { return Free > 0; } }

        public int FirstFree()
        {
            for (int i = 0; i < BlockCount; i++) if (State[i] == 0) return i;
            return -1;
        }
    }

    internal sealed class Candidate
    {
        public IPEndPoint EndPoint;
        public int Failures;
        public long NextAttemptMs;
        public bool InUse;
        public string Source;
    }

    /// <summary>
    /// VOD download of a torrent: peers, streaming piece picker, bounded in-RAM store and
    /// SHA-1 verification. All shared state is protected by _sync; network I/O happens outside the lock.
    /// Split into several partial files: Peers, Download, Upload, Metadata and Discovery.
    /// </summary>
    internal sealed partial class Swarm : IPeerHost, IPickerContext, IDisposable
    {
        private const string Tag = "swarm";

        private readonly EngineOptions _opt;
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly byte[] _infoHash;
        private readonly byte[] _peerId;
        private readonly DhtClient _dht;
        private readonly HttpClient _http;
        private readonly Func<int> _listenPort;
        private readonly List<string> _extraTrackers = new List<string>();
        private readonly int _requestedFileIndex;
        private bool _disposed;
        private bool _started;
        private Task _tickTask;

        // ---- torrent ----
        private TorrentMeta _meta;
        private int _pieceCount;
        private int _pieceLen;
        private long _total;
        private TorrentFileEntry _file;
        private int _firstPiece, _lastPiece;
        private double _bitrate;

        // ---- pieces ----
        private Bitfield _have;                                   // verified pieces held in RAM
        private readonly Dictionary<int, byte[]> _store = new Dictionary<int, byte[]>();
        private long _storeBytes;
        private readonly Dictionary<int, ActivePiece> _active = new Dictionary<int, ActivePiece>();
        private long _activeBytes;
        private int[] _avail;
        private readonly HashSet<int> _pinned = new HashSet<int>();

        // ---- playback ----
        private long _playPos;                                    // relative to the file
        private int _playPiece;
        private readonly PickerWindow _win = new PickerWindow();
        private bool _prebufDone;
        private int _highLen, _midLen, _aheadLen;
        private bool _hasReader;

        // ---- peers ----
        private readonly List<PeerConnection> _peers = new List<PeerConnection>();      // handshake completed
        private readonly HashSet<PeerConnection> _all = new HashSet<PeerConnection>();    // connecting + ready
        private readonly Dictionary<string, Candidate> _cands = new Dictionary<string, Candidate>();
        private readonly HashSet<string> _banned = new HashSet<string>();
        private int _connecting;

        // ---- statistics ----
        private readonly RateMeter _downMeter = new RateMeter(5);
        private readonly RateMeter _upMeter = new RateMeter(5);
        private long _hashFails;
        private long _discardedBytes;
        private SwarmPhase _phase = SwarmPhase.Resolving;
        private DateTime _startedUtc = DateTime.UtcNow;
        private string _message;

        /// <summary>Wakes up the readers and those waiting for metadata.</summary>
        public readonly AsyncSignal Changed = new AsyncSignal();
        private readonly TaskCompletionSource<bool> _metaReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<SwarmPhase> PhaseChanged;

        public Swarm(EngineOptions options, byte[] infoHash, TorrentMeta meta, IEnumerable<string> magnetTrackers,
                     int fileIndex, DhtClient dht, HttpClient http, Func<int> listenPort)
        {
            _opt = options;
            _infoHash = infoHash;
            _peerId = PeerId.Generate(options.PeerIdPrefix);
            _dht = dht;
            _http = http;
            _listenPort = listenPort ?? (() => 0);
            _requestedFileIndex = fileIndex;
            if (magnetTrackers != null) _extraTrackers.AddRange(magnetTrackers);
            if (meta != null) ApplyMetadataLocked(meta);        // nobody sees it yet: no lock needed
        }

        // ---------- IPeerHost ----------

        public EngineOptions Options { get { return _opt; } }
        public byte[] InfoHash { get { return _infoHash; } }
        public byte[] LocalPeerId { get { return _peerId; } }

        public bool HasMetadata { get { lock (_sync) return _meta != null; } }
        public Task MetadataReady { get { return _metaReady.Task; } }
        public TorrentMeta Meta { get { lock (_sync) return _meta; } }
        public TorrentFileEntry SelectedFile { get { lock (_sync) return _file; } }
        public long FileLength { get { lock (_sync) return _file == null ? 0 : _file.Length; } }
        public bool IsDisposed { get { return _disposed; } }

        // ---------- Lifecycle ----------

        public void Start()
        {
            lock (_sync)
            {
                if (_started || _disposed) return;
                _started = true;
            }
            AceLog.Info(Tag, "start " + Hex.ToHex(_infoHash).Substring(0, 8) + ", peer_id " + System.Text.Encoding.ASCII.GetString(_peerId));
            _tickTask = Task.Run(() => TickLoopAsync());
            if (_opt.EnableTrackers) StartTrackers();
            if (_opt.EnableDht && _dht != null) Task.Run(() => DhtLoopAsync());
        }

        public void Dispose()
        {
            List<PeerConnection> toClose;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                toClose = new List<PeerConnection>(_all);
            }
            try { _cts.Cancel(); } catch { }
            AnnounceStopped();
            foreach (var p in toClose) p.Close("session stopped");
            lock (_sync)
            {
                _store.Clear(); _storeBytes = 0;
                _active.Clear(); _activeBytes = 0;
                _peers.Clear();
                SetPhaseLocked(SwarmPhase.Stopped);
            }
            Changed.Pulse();
            _metaReady.TrySetCanceled();
        }

        // ---------- Metadata / file selection ----------

        private void ApplyMetadataLocked(TorrentMeta meta)
        {
            if (meta.IsLive) throw new NotSupportedException("el torrent es un directo");
            _meta = meta;
            _pieceCount = meta.PieceCount;
            _pieceLen = (int)meta.PieceLength;
            _total = meta.TotalLength;
            if (meta.Pieces == null) throw new NotSupportedException("torrent Merkle sin 'pieces' no soportado");

            // File selection: the index is the position in info['files'].
            var media = meta.GetFilesWithIndexes(MimeTypes.VideoExtensions);
            TorrentFileEntry file = null;
            if (_requestedFileIndex >= 0)
            {
                file = meta.Files.FirstOrDefault(f => f.Index == _requestedFileIndex);
                if (file == null) throw new ArgumentException("el torrent no tiene el fichero de indice " + _requestedFileIndex
                    + " (hay " + meta.Files.Count + ")");
            }
            else if (media.Count > 0) file = media[0];
            else file = meta.Files[0];
            _file = file;
            if (file.Length > 0)
            {
                _firstPiece = (int)(file.Offset / _pieceLen);
                _lastPiece = (int)((file.Offset + file.Length - 1) / _pieceLen);
            }
            else { _firstPiece = 0; _lastPiece = -1; }

            _have = new Bitfield(_pieceCount);
            _avail = new int[_pieceCount];

            double br = _opt.AssumedBitrateBytesPerSec;
            if (meta.XTsProperties != null)
            {
                var tb = meta.XTsProperties.GetInt("bitrate");
                if (tb.HasValue && tb.Value > 0) { br = tb.Value; AceLog.Info(Tag, "bitrate from x-ts-properties: " + tb.Value); }
            }
            _bitrate = Math.Max(br, 1);

            _playPos = 0;
            _playPiece = _firstPiece;
            ComputeLengthsLocked();
            ResetPrebufferLocked(_firstPiece);
            UpdateWindowLocked();

            foreach (var t in meta.GetTrackerTiers()) foreach (var u in t) if (!_extraTrackers.Contains(u)) _extraTrackers.Add(u);
            if (_phase == SwarmPhase.Resolving) _phase = SwarmPhase.Connecting;
            AceLog.Info(Tag, "metadata: '" + meta.Name + "', " + _pieceCount + " pieces of " + _pieceLen + " B; file [" + file.Index + "] '"
                + file.Path + "' " + file.Length + " B, pieces " + _firstPiece + ".." + _lastPiece
                + "; windows high=" + _highLen + " mid=" + _midLen + " ahead=" + _aheadLen);
            _metaReady.TrySetResult(true);
        }

        // ---------- Windows (Tribler-style values) ----------

        private void ComputeLengthsLocked()
        {
            long pl = _pieceLen;
            long slack = Math.Max(2 * pl, _opt.BufferSlackBytes);
            int ahead = (int)Math.Max(3, (_opt.MaxBufferBytes - _opt.KeepBehindBytes - slack) / pl);
            int high = (int)Math.Max(_opt.HighRangeMinPieces,
                Math.Max(Math.Ceiling(_opt.HighRangeSeconds * _bitrate / pl), Math.Ceiling((double)_opt.HighRangeMinBytes / pl)));
            high = Math.Min(high, ahead);
            int mid = Math.Min(_opt.MidFactor * high, ahead - high);
            _highLen = high; _midLen = Math.Max(0, mid); _aheadLen = ahead;
            _urgentLen = (int)Math.Max(_opt.UrgentMinPieces, Math.Min(_opt.UrgentMaxPieces, Math.Ceiling(_opt.UrgentSeconds * _bitrate / pl)));
        }

        private int _urgentLen;

        private int KeepBehindPieces
        {
            get { return (int)Math.Max(1, (_opt.KeepBehindBytes + _pieceLen - 1) / _pieceLen); }
        }

        private void ResetPrebufferLocked(int startPiece)
        {
            var needed = new List<int>();
            var hi = new List<int>();
            int count = (int)Math.Max(_opt.PrebufferMinPieces,
                Math.Max(Math.Ceiling(_opt.PrebufferSeconds * _bitrate / _pieceLen), Math.Ceiling((double)_opt.PrebufferMinBytes / _pieceLen)));
            count = Math.Min(count, _aheadLen);
            for (int i = 0; i < count && startPiece + i <= _lastPiece; i++) needed.Add(startPiece + i);
            for (int i = 0; i < Math.Min(2, needed.Count); i++) hi.Add(needed[i]);
            _pinned.Clear();
            for (int i = 0; i < _opt.PrebufferTailPieces; i++)
            {
                int tp = _lastPiece - i;
                if (tp >= _firstPiece && !needed.Contains(tp)) { needed.Add(tp); hi.Add(tp); _pinned.Add(tp); }
            }
            _win.PrebufNeeded = needed;
            _win.PrebufHighPriority = hi;
            _prebufDone = needed.Count == 0;
            _win.Prebuffering = !_prebufDone;
        }

        /// <summary>Recomputes the windows from the playback position.</summary>
        private void UpdateWindowLocked()
        {
            if (_meta == null) return;
            _win.FirstPiece = _firstPiece;
            _win.LastPiece = _lastPiece;
            _win.PlaybackPiece = _playPiece;
            _win.HighEnd = Math.Min(_lastPiece + 1, _playPiece + _highLen);
            _win.MidEnd = Math.Min(_lastPiece + 1, _win.HighEnd + _midLen);
            _win.WindowEnd = Math.Min(_lastPiece + 1, _playPiece + _aheadLen);
            _win.Prebuffering = !_prebufDone;
        }

        private bool InHighRange(int piece)
        {
            return piece >= _win.PlaybackPiece && piece < _win.HighEnd;
        }

        private bool InWindow(int piece)
        {
            return piece >= _playPiece - KeepBehindPieces && piece < _win.WindowEnd;
        }

        /// <summary>Seconds until the player will need the piece (the original's piece_due, native in Ace).</summary>
        private double PieceDueSeconds(int piece)
        {
            long start = (long)piece * _pieceLen - _file.Offset;
            double ahead = start - _playPos;
            return ahead <= 0 ? 0 : ahead / _bitrate;
        }

        // ---------- Phase / statistics ----------

        private void SetPhaseLocked(SwarmPhase p)
        {
            if (_phase == p) return;
            _phase = p;
            var h = PhaseChanged;
            if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(p); } catch { } });
        }

        public SwarmPhase Phase { get { lock (_sync) return _phase; } }

        public SessionStats GetStats()
        {
            lock (_sync)
            {
                var s = new SessionStats
                {
                    Peers = _peers.Count,
                    Seeds = _peers.Count(p => p.IsSeed),
                    DownloadBytesPerSec = _downMeter.Rate,
                    UploadBytesPerSec = _upMeter.Rate,
                    DownloadedBytes = _downMeter.Total,
                    UploadedBytes = _upMeter.Total,
                    BufferedBytes = _storeBytes + _activeBytes,
                    Message = _message
                };
                if (_meta != null)
                {
                    s.PiecesTotal = Math.Max(0, _lastPiece - _firstPiece + 1);
                    int have = 0;
                    for (int p = _firstPiece; p <= _lastPiece; p++) if (_have[p]) have++;
                    s.PiecesHave = have;
                    s.BufferPercent = BufferPercentLocked();
                }
                return s;
            }
        }

        private int BufferPercentLocked()
        {
            if (!_prebufDone)
            {
                if (_win.PrebufNeeded.Count == 0) return 100;
                int got = 0;
                foreach (var p in _win.PrebufNeeded) if (_have[p]) got++;
                return got * 100 / _win.PrebufNeeded.Count;
            }
            int span = Math.Max(1, _win.HighEnd - _win.PlaybackPiece);
            int run = 0;
            for (int p = _win.PlaybackPiece; p < _win.HighEnd && _have[p]; p++) run++;
            return Math.Min(100, run * 100 / span);
        }

        public int PeerCount { get { lock (_sync) return _peers.Count; } }

        private int CountHaveRun()
        {
            int run = 0;
            for (int p = _win.PlaybackPiece; p < _win.HighEnd && _have[p]; p++) run++;
            return run;
        }

        internal bool PrebufferActive { get { lock (_sync) return !_prebufDone; } }

        internal int PlaybackPiece { get { lock (_sync) return _playPiece; } }
        internal int StoredPieces { get { lock (_sync) return _store.Count; } }
        internal bool IsPieceStored(int piece) { lock (_sync) return _store.ContainsKey(piece); }

        /// <summary>Tests only: inserts a verified piece into the store as if it had arrived from the network.</summary>
        internal void InjectVerifiedPiece(int piece, byte[] data)
        {
            lock (_sync)
            {
                _store[piece] = data; _storeBytes += data.Length; _have.Set(piece);
            }
        }

        public string DebugInfo()
        {
            lock (_sync)
            {
                return "peers=" + _peers.Count + " connecting=" + _connecting + " candidates=" + _cands.Count
                    + " store=" + _store.Count + " active=" + _active.Count + " hashFails=" + _hashFails
                    + " discarded=" + _discardedBytes + " B play=" + _playPiece + " high=[" + _win.PlaybackPiece + "," + _win.HighEnd + ") window<" + _win.WindowEnd
                    + " found-high=" + CountHaveRun() + " active-high=" + _active.Keys.Count(k => k >= _win.PlaybackPiece && k < _win.HighEnd)
                    + " unchoked=" + _peers.Count(p => !p.PeerChoking) + " bad=" + _peers.Count(p => p.BadPerformance > 0) + " waiting=" + Volatile.Read(ref _waiters);
            }
        }
    }
}
