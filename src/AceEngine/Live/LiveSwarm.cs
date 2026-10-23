using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Live
{
    internal enum LivePhase { Resolving, Connecting, Buffering, Playing, Stopped }

    /// <summary>ed25519 key pair of the node. One per process is enough: peers only use it to identify us.</summary>
    public sealed class NodeKeys
    {
        public byte[] Seed { get; private set; }
        public byte[] PublicKey { get; private set; }
        public NodeKeys(byte[] seed, byte[] pub) { Seed = seed; PublicKey = pub; }
        public static NodeKeys Generate()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            return new NodeKeys(seed, pub);
        }
    }

    internal sealed class LivePiece
    {
        public long Index;
        public byte[] Data;           // complete piece: content || signature (PL bytes)
        public double Timestamp;
        public bool Announced;        // announced with HAVE/BITFIELD (LOST must be sent when evicting it)
    }

    internal sealed class LiveActive
    {
        public long Index;
        public byte[] Buf;
        public bool[] Got;
        public int GotCount;
        public LivePeer Owner;                          // peer the piece was requested from (null = unassigned)
        public LivePeer Second;                         // second peer (duplicate request of an urgent piece)
        public long AssignedMs, LastProgressMs, CreatedMs;
        public double Timestamp;
        public readonly HashSet<LivePeer> Tried = new HashSet<LivePeer>();
        public readonly List<LivePeer> Sources = new List<LivePeer>();
        public int Attempts;
    }

    internal sealed class LiveCandidate
    {
        public IPEndPoint EndPoint;
        public int Failures;
        public long NextAttemptMs;
        public bool InUse;
        public string Source;
        public bool Priority;
        public int Attempts;
        public string Key;                // EndPoint.ToString(), computed once
        public uint Net;                  // /16 network (NetKey)
    }

    /// <summary>
    /// Spectator client of a classic live stream (tt='bt'): peers, own protocol (ids 10-39), live position, sequential download
    /// by pieces of chunks, RSA verification and continuous MPEG-TS output. All shared state is protected by _sync; I/O is outside it.
    /// Split into partials: Peers (messages), Download, Position (startup, holes, output) and Discovery.
    /// </summary>
    internal sealed partial class LiveSwarm : ILivePeerHost, IIncomingTarget, IDisposable
    {
        private const string Tag = "live";

        private readonly EngineOptions _opt;
        private readonly LiveOptions _lo;
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly LiveDescriptor _desc;
        private readonly byte[] _transportBytes;
        private readonly string _requestedContentId;
        private readonly byte[] _infoHash;
        private readonly byte[] _peerId;
        private readonly DhtClient _dht;
        private readonly HttpClient _http;
        private readonly Func<int> _listenPort;
        private readonly NodeKeys _keys;
        private readonly RsaPublicKey _rsa;
        private readonly bool _signed;
        private readonly int _pl, _cl, _chunks, _signSize, _cbs;
        private readonly double _pps;
        private readonly int _prebufPieces, _keepBehind, _ahead, _windowPieces, _maxPieces;
        private bool _disposed, _started;
        private Task _tickTask;
        private int _learnedPeerType = -1;
        private int _exchangeLogged;

        // ---- pieces ----
        private readonly Dictionary<long, LivePiece> _store = new Dictionary<long, LivePiece>();
        private readonly Dictionary<long, LiveActive> _active = new Dictionary<long, LiveActive>();
        private long _storeBytes;
        private long _discardedBytes;

        // ---- peers ----
        private readonly List<LivePeer> _peers = new List<LivePeer>();
        private readonly HashSet<LivePeer> _all = new HashSet<LivePeer>();
        private readonly Dictionary<string, LiveCandidate> _cands = new Dictionary<string, LiveCandidate>();
        private readonly HashSet<string> _banned = new HashSet<string>();
        private int _connecting;
        private readonly List<LivePeerTrace> _traces = new List<LivePeerTrace>();
        private readonly Dictionary<string, HashSet<string>> _candBySource = new Dictionary<string, HashSet<string>>();
        private const int MaxTraces = 800;

        // ---- statistics ----
        private readonly RateMeter _downMeter = new RateMeter(5);
        private readonly RateMeter _upMeter = new RateMeter(5);
        private long _topUpRate;
        private LivePhase _phase = LivePhase.Resolving;
        private string _message;
        private long _startedMs;

        public readonly AsyncSignal Changed = new AsyncSignal();
        public event Action<LivePhase> PhaseChanged;

        public LiveSwarm(EngineOptions options, LiveDescriptor desc, byte[] transportBytes, string requestedContentId, DhtClient dht, HttpClient http,
            Func<int> listenPort, NodeKeys keys)
        {
            _opt = options;
            _lo = options.Live;
            _desc = desc;
            _transportBytes = transportBytes;
            _requestedContentId = requestedContentId;
            _infoHash = desc.InfoHash;
            _peerId = PeerId.Generate(options.PeerIdPrefix);
            _dht = dht;
            _http = http;
            _listenPort = listenPort ?? (() => 0);
            _keys = keys;

            _pl = (int)desc.PieceLength;
            _cl = (int)Math.Max(1, Math.Min(desc.ChunkLength <= 0 ? desc.PieceLength : desc.ChunkLength, desc.PieceLength));
            if (_pl <= 0 || desc.Bitrate <= 0) throw new TransportException("descriptor de directo sin piece_length o bitrate");
            _chunks = (_pl + _cl - 1) / _cl;
            if (desc.AuthMethod == "RSA")
            {
                try { _rsa = RsaPublicKey.Parse(desc.PubKey); }
                catch (FormatException ex) { throw new TransportException("el 'pubkey' del descriptor no es una clave RSA DER valida: " + ex.Message, ex); }
                _signSize = _rsa.SignSize;
                _signed = true;
            }
            else
            {
                // 'None' or unknown: no signature, the piece is pure content
                _signSize = 0;
                if (desc.AuthMethod != "None") AceLog.Warn(Tag, "authmethod '" + desc.AuthMethod + "' not supported: pieces are treated as unsigned");
            }
            _cbs = _pl - _signSize;
            if (_cbs <= 0) throw new TransportException("piece_length menor que la firma");

            _pps = (double)desc.Bitrate / _pl;
            _prebufPieces = Math.Max(2, (int)Math.Ceiling(_lo.BufferSeconds * _pps));
            _maxPieces = (int)Math.Max(_prebufPieces + 6, _lo.MaxBufferBytes / _pl);
            _keepBehind = (int)Math.Max(1, Math.Min((int)Math.Ceiling(_lo.KeepBehindSeconds * _pps), _maxPieces / 4));
            _ahead = (int)Math.Max(_prebufPieces + 3, Math.Min((int)Math.Ceiling(_lo.AheadSeconds * _pps), _maxPieces - _keepBehind - 2));
            _windowPieces = (int)Math.Max(_ahead, Math.Ceiling(_lo.LiveWindowSeconds * _pps));
        }

        // ---------- ILivePeerHost ----------

        public EngineOptions Options { get { return _opt; } }
        public byte[] InfoHash { get { return _infoHash; } }
        public byte[] LocalPeerId { get { return _peerId; } }
        public LiveDescriptor Descriptor { get { return _desc; } }
        public bool IsDisposed { get { return _disposed; } }
        public double PiecesPerSecond { get { return _pps; } }
        public int PieceLength { get { return _pl; } }
        public int ContentBlockSize { get { return _cbs; } }

        // ---------- Lifecycle ----------

        public void Start()
        {
            lock (_sync)
            {
                if (_started || _disposed) return;
                _started = true;
                _startedMs = LivePeer.NowMs();
                SetPhaseLocked(LivePhase.Connecting);
            }
            AceLog.Info(Tag, "start '" + _desc.Name + "' infohash " + Hex.ToHex(_infoHash) + ", pieces of " + _pl + " B (content " + _cbs + ", signature " + _signSize
                + ", chunks of " + _cl + " x" + _chunks + "), " + _pps.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " pieces/s, prebuffer "
                + _prebufPieces + " pieces, ahead " + _ahead + ", behind " + _keepBehind + ", peer_id " + System.Text.Encoding.ASCII.GetString(_peerId));
            _tickTask = Task.Run(() => TickLoopAsync());
            StartDiscovery();
        }

        public void Dispose()
        {
            List<LivePeer> toClose;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                toClose = new List<LivePeer>(_all);
            }
            try { _cts.Cancel(); } catch { }
            foreach (var p in toClose) p.Close("session stopped");
            lock (_sync)
            {
                _store.Clear(); _storeBytes = 0; _active.Clear(); _peers.Clear();
                SetPhaseLocked(LivePhase.Stopped);
            }
            Changed.Pulse();
        }

        private void SetPhaseLocked(LivePhase p)
        {
            if (_phase == p) return;
            _phase = p;
            var h = PhaseChanged;
            if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(p); } catch { } });
        }

        public LivePhase Phase { get { lock (_sync) return _phase; } }

        // ---------- Candidates and connections ----------

        private const int MaxCandidates = 3000;

        public void AddCandidates(IEnumerable<IPEndPoint> eps, string source, bool priority = false)
        {
            int added = 0;
            lock (_sync)
            {
                foreach (var ep in eps)
                {
                    if (ep == null || ep.Port <= 0) continue;
                    if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.Loopback) && ep.Port == _listenPort()) continue;
                    string k = ep.ToString();
                    LiveCandidate c;
                    NoteCandidateSourceLocked(source, k);
                    if (_cands.TryGetValue(k, out c)) { if (priority) c.Priority = true; continue; }
                    if (_cands.Count >= MaxCandidates && !priority) continue;       // trackers, DHT and startup nodes can give huge, hostile lists
                    _cands[k] = new LiveCandidate { EndPoint = ep, Source = source, Priority = priority, Key = k, Net = NetKey(ep.Address) };
                    added++;
                }
            }
            if (added > 0) AceLog.Debug(Tag, "+" + added + " candidates (" + source + ")");
        }

        /// <summary>Grouped source for the statistics: tracker, dht:ih, dht:cid, resolution (+ hints), startup_node, meta_tracker, StaticPeers, incoming.</summary>
        internal static string SourceGroup(string source)
        {
            if (source == null) return "?";
            if (source.StartsWith("dht:", StringComparison.Ordinal)) { int j = source.IndexOf(':', 4); return j > 0 ? source.Substring(0, j) : source; }
            int i = source.IndexOf(':');
            return i > 0 ? source.Substring(0, i) : source;
        }

        private void NoteCandidateSourceLocked(string source, string key)
        {
            string g = SourceGroup(source);
            HashSet<string> set;
            if (!_candBySource.TryGetValue(g, out set)) _candBySource[g] = set = new HashSet<string>();
            if (set.Count < MaxCandidates) set.Add(key);
        }

        // /16 -> [reached, connect timeouts]: a whole network that never answers (CGNAT, WARP...) is left for last
        private readonly Dictionary<uint, int[]> _netStats = new Dictionary<uint, int[]>();

        private static uint NetKey(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 ? (uint)((b[0] << 8) | b[1]) : 0u;
        }

        /// <summary>Lower is better: known peers (descriptor, startup nodes, hints from the same channel) before the announced ones; networks with no response at all, last.</summary>
        private int CandidateRankLocked(LiveCandidate c)
        {
            int r = c.Priority ? 0 : 100;
            // Measured: of the peers the tracker gives, 10 % complete the handshake; of those from the DHT, 1-5 % (almost all are gone or do not carry this live stream)
            if (!c.Priority && _lo.DeprioritizeDht && c.Source != null && c.Source.StartsWith("dht", StringComparison.Ordinal)) r += 15;
            int[] ns;
            if (_lo.NetworkPenaltyAfter > 0 && _netStats.TryGetValue(c.Net, out ns) && ns[0] == 0 && ns[1] >= _lo.NetworkPenaltyAfter) r += 50;
            return r + Math.Min(c.Failures, 20);
        }

        /// <summary>The next candidates that would be tried, in order (tests only).</summary>
        internal List<IPEndPoint> NextCandidatesForTest(int n)
        {
            lock (_sync)
            {
                long now = LivePeer.NowMs();
                return _cands.Values.Where(c => !c.InUse && c.NextAttemptMs <= now && !_banned.Contains(c.Key)).OrderBy(c => CandidateRankLocked(c)).Take(n).Select(c => c.EndPoint).ToList();
            }
        }

        private void NoteNetworkLocked(LivePeer peer, bool reached)
        {
            if (peer.Incoming) return;
            var k = NetKey(peer.EndPoint.Address);
            int[] ns;
            if (!_netStats.TryGetValue(k, out ns)) { if (_netStats.Count > 4000) return; _netStats[k] = ns = new int[2]; }
            if (reached) ns[0]++; else ns[1]++;
        }

        private void ConnectMoreLocked()
        {
            if (_disposed) return;
            long now = LivePeer.NowMs();
            if (_all.Count >= _lo.MaxPeers) return;
            int slots = _lo.MaxPendingConnects - _connecting;
            if (slots <= 0) return;
            // The best 'slots' free candidates, by rank and, on ties, by arrival order (without sorting all 3000 on every 100 ms tick)
            var pick = new List<KeyValuePair<int, LiveCandidate>>(Math.Min(slots, 64));
            foreach (var c in _cands.Values)
            {
                if (c.InUse || c.NextAttemptMs > now) continue;
                if (_banned.Contains(c.Key)) continue;
                int rank = CandidateRankLocked(c);
                if (pick.Count >= slots)
                {
                    if (rank >= pick[pick.Count - 1].Key) continue;           // does not improve on the worst of those chosen
                    pick.RemoveAt(pick.Count - 1);
                }
                int at = pick.Count;
                while (at > 0 && pick[at - 1].Key > rank) at--;               // inserts after those of equal rank (stable)
                pick.Insert(at, new KeyValuePair<int, LiveCandidate>(rank, c));
            }
            if (pick.Count == 0) return;
            foreach (var kv in pick)
            {
                var c = kv.Value;
                if (_connecting >= _lo.MaxPendingConnects || _all.Count >= _lo.MaxPeers) break;
                c.InUse = true;
                c.Attempts++;
                var peer = new LivePeer(c.EndPoint, false) { Connecting = true, Label = c.Source };
                peer.Trace.Source = c.Source;
                peer.Trace.Attempt = c.Attempts;
                if (_traces.Count < MaxTraces) _traces.Add(peer.Trace);
                _all.Add(peer);
                _connecting++;
                Task.Run(async () =>
                {
                    await peer.RunOutgoingAsync(this, _lo).ConfigureAwait(false);
                });
            }
        }

        bool IIncomingTarget.Accept(System.Net.Sockets.TcpClient tcp, bool aceProtocolName, HandshakeInfo head)
        {
            return AddIncoming(tcp, aceProtocolName, head.Reserved);
        }

        /// <summary>Incoming connection (listener): the handshake was already read up to the infohash.</summary>
        public bool AddIncoming(System.Net.Sockets.TcpClient tcp, bool aceName, byte[] reserved)
        {
            var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
            if (ep == null) return false;
            LivePeer peer;
            lock (_sync)
            {
                if (_disposed || _all.Count >= _lo.MaxPeers + 10) return false;
                if (_banned.Contains(ep.ToString())) return false;
                peer = new LivePeer(ep, true) { Connecting = true, Label = "incoming" };
                peer.Trace.Source = "incoming";
                if (_traces.Count < MaxTraces) _traces.Add(peer.Trace);
                _all.Add(peer);
                _connecting++;
            }
            Task.Run(async () => await peer.RunIncomingAsync(this, tcp, aceName, reserved).ConfigureAwait(false));
            return true;
        }

        public bool OnPeerReady(LivePeer peer)
        {
            lock (_sync)
            {
                if (peer.Connecting) { peer.Connecting = false; _connecting = Math.Max(0, _connecting - 1); }
                if (_disposed) return false;
                if (peer.RemotePeerId != null)
                {
                    foreach (var p in _peers)
                        if (Bytes.Equal(p.RemotePeerId, peer.RemotePeerId)) { AceLog.Debug(Tag, peer + " duplicate (same peer_id as " + p + ")"); return false; }
                }
                peer.Ready = true;
                _peers.Add(peer);
                if (peer.Incoming && !_inboundVerified && IsPublicAddress(peer.EndPoint.Address)) { _inboundVerified = true; AceLog.Info(Tag, "incoming connection from " + peer.EndPoint + ": the port is reachable from outside"); }
                LiveCandidate c;
                if (_cands.TryGetValue(peer.EndPoint.ToString(), out c)) c.Failures = 0;
                AceLog.Debug(Tag, peer + " handshake: " + peer.RemoteProtocol + " reserved " + Hex.ToHex(peer.RemoteReserved) + " peer_id " + System.Text.Encoding.ASCII.GetString(peer.RemotePeerId));
                return true;
            }
        }

        public void OnPeerClosed(LivePeer peer, string reason)
        {
            lock (_sync)
            {
                if (peer.Connecting) { peer.Connecting = false; _connecting = Math.Max(0, _connecting - 1); }
                _all.Remove(peer);
                _peers.Remove(peer);
                LiveCandidate c;
                var t = peer.Trace;
                if (!peer.Incoming)
                {
                    NoteNetworkLocked(peer, t.Stage >= LiveStage.TcpOk);
                    if (_cands.TryGetValue(peer.EndPoint.ToString(), out c))
                    {
                        c.InUse = false;
                        long now = LivePeer.NowMs();
                        if (!peer.Ready) { c.Failures++; c.NextAttemptMs = now + (long)(Math.Min(600, 20 * Math.Pow(2, Math.Min(c.Failures, 5))) * 1000); }
                        else if (_lo.FullPeerBackoff && t.Stage < LiveStage.HaveSeen && !peer.Snubbed && reason != null && !IsLocalClose(reason))
                        {
                            // The peer did the handshake and the EXTEND and closed on us without announcing anything (full node: it sends PEERS and CLOSE): retrying after 2 s fills it with nothing again
                            c.Failures++;
                            c.NextAttemptMs = now + (long)(Math.Min(300, 15 * Math.Pow(2, Math.Min(c.Failures, 5))) * 1000);
                        }
                        else c.NextAttemptMs = now + 2000;            // a peer that already worked is retried soon
                    }
                }
                ReleasePeerRequestsLocked(peer);
                if (peer.Ready) AceLog.Debug(Tag, peer + " closed: " + reason);
            }
            Changed.Pulse();
        }

        private static bool IsLocalClose(string reason)
        {
            return reason == "session stopped" || reason == "cancelled" || reason == "closed" || reason == "no pieces" || reason == "no activity" || reason.StartsWith("evicted", StringComparison.Ordinal);
        }

        private void BanLocked(LivePeer peer, string why)
        {
            AceLog.Info(Tag, peer + " evicted: " + why);
            _banned.Add(peer.EndPoint.ToString());
            var p = peer;
            ThreadPool.QueueUserWorkItem(_ => p.Close("evicted: " + why));
        }

        // ---------- Own EXTEND ----------

        public byte[] BuildExtendFrame(LivePeer peer)
        {
            LiveLocalState st;
            int peerType;
            lock (_sync) { st = LocalStateLocked(); peerType = PeerTypeLocked(); }
            var ip = peer.EndPoint.Address.GetAddressBytes();
            var payload = LiveExtend.BuildPayload(_lo, st, peerType, ip.Length == 4 ? ip : null, (long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency), _keys == null ? null : _keys.Seed,
                _keys == null ? null : _keys.PublicKey);
            if (_exchangeLogged < _lo.LogExchangePeers)
            {
                int n = Interlocked.Increment(ref _exchangeLogged);
                if (n <= _lo.LogExchangePeers)
                {
                    try
                    {
                        var d = BencodeParser.Parse(payload) as BDict;
                        AceLog.Info(Tag, peer + " EXTEND sent: " + LiveExtend.Describe(d));
                    }
                    catch (BencodeException) { }
                }
            }
            return LiveExtend.BuildFrame(payload);
        }

        private int PeerTypeLocked()
        {
            return _lo.LearnPeerType && _learnedPeerType >= 0 ? _learnedPeerType : _lo.PeerType;
        }

        /// <summary>Someone from outside has completed an incoming handshake with us: the port is really open.</summary>
        private bool _inboundVerified;

        private bool IsAccessibleLocked()
        {
            if (_lo.ClaimAccessible || _inboundVerified) return true;
            var f = _lo.AccessibleProvider;
            if (f == null) return false;
            try { return f(); } catch (Exception) { return false; }
        }

        private static bool IsPublicAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 0 || b[0] >= 224) return false;
            return true;
        }

        private long AdvertisedTopUpRateLocked()
        {
            long v = _topUpRate;
            if (_lo.AdvertiseUploadCap && _lo.UploadSlots > 0)
            {
                long cap = _opt.UploadMaxBytesPerSec;
                if (cap > v) v = cap;
            }
            return v;
        }

        private LiveLocalState LocalStateLocked()
        {
            var s = new LiveLocalState
            {
                Lsp = _livePos,
                Position = _started2 ? _outPiece : -1,
                LiveWindowSize = _windowPieces,
                DownRate = (long)_downMeter.Rate,
                UpRate = (long)_upMeter.Rate,
                TopUpRate = AdvertisedTopUpRateLocked(),
                TopSessionUpRate = AdvertisedTopUpRateLocked(),
                ListenPort = _listenPort(),
                Accessible = IsAccessibleLocked()
            };
            if (_store.Count > 0)
            {
                long min = long.MaxValue, max = 0;
                foreach (var k in _store.Keys) { if (k < min) min = k; if (k > max) max = k; }
                s.MinPiece = min; s.MaxPiece = max;
                s.DownloadWindowEnd = _started2 ? _outPiece + _ahead : max;
            }
            else s.DownloadWindowEnd = _started2 ? _outPiece + _ahead : -1;
            return s;
        }

        // ---------- Statistics ----------

        public SessionStats GetStats()
        {
            lock (_sync)
            {
                var s = new SessionStats
                {
                    Peers = _peers.Count,
                    Seeds = _peers.Count(p => p.Ext != null && p.Ext.IsAuthorizedNode),
                    DownloadBytesPerSec = _downMeter.Rate,
                    UploadBytesPerSec = _upMeter.Rate,
                    DownloadedBytes = _downMeter.Total,
                    UploadedBytes = _upMeter.Total,
                    BufferedBytes = _storeBytes + _active.Values.Sum(a => (long)a.GotCount * _cl),
                    PiecesHave = _store.Count,
                    PiecesTotal = 0,
                    Message = _message,
                    BufferPercent = BufferPercentLocked()
                };
                return s;
            }
        }

        public LiveStats GetLiveStats()
        {
            lock (_sync)
            {
                return new LiveStats
                {
                    Phase = _phase, LivePos = _livePos, OutPiece = _outPiece, Started = _started2, Playable = _playable,
                    LagSeconds = _livePos >= 0 && _started2 && _livePos >= _outPiece ? (_livePos - _outPiece) / _pps : 0,
                    StoredPieces = _store.Count, ActivePieces = _active.Count, Peers = _peers.Count, Unchoked = _peers.Count(p => !p.PeerChoking),
                    PiecesVerified = _sigOk, SignatureFailures = _sigFail, AcceptingUnverified = _acceptUnverified,
                    HolesSkipped = _holesSkipped, PiecesSkipped = _piecesSkipped, Rehooks = _rehooks, DiscardedBytes = _discardedBytes,
                    PrebufferPieces = _prebufPieces, PiecesPerSecond = _pps, Candidates = _cands.Count, Connecting = _connecting,
                    Bytes = _bytesOut, StarvedMs = _starvedMs
                };
            }
        }

        /// <summary>Outgoing peers that answered well (EXTEND with pieces; first those that served pieces), to remember them as hints of the channel.</summary>
        public IList<IPEndPoint> GoodPeers(int max = 24)
        {
            lock (_sync)
            {
                return _traces.Where(t => !t.Incoming && t.Stage >= LiveStage.HaveSeen)
                    .OrderByDescending(t => (int)t.Stage).ThenByDescending(t => t.Pieces)
                    .Select(t => t.EndPoint).Distinct().Take(max)
                    .Select(e => { string h; int p; IPAddress ip; return AceEngine.P2P.Net.TryParseHostPort(e, out h, out p) && IPAddress.TryParse(h, out ip) ? new IPEndPoint(ip, p) : null; })
                    .Where(e => e != null).ToList();
            }
        }

        /// <summary>Connection funnel with the peers (distinct peers per stage), live position and per-peer traces. For `ace measure`, the tests and the liveness criterion.</summary>
        public LiveFunnel GetFunnel()
        {
            lock (_sync)
            {
                long now = LivePeer.NowMs();
                var f = new LiveFunnel { ElapsedSeconds = (now - _startedMs) / 1000.0 };
                foreach (var kv in _candBySource) f.CandidatesBySource[kv.Key] = kv.Value.Count;
                f.Candidates = _cands.Count;
                f.RecentWindow = (long)Math.Ceiling(60 * _pps) + 2;
                long best = _livePos;
                foreach (var t in _traces) if (t.HeadClaim > best) best = t.HeadClaim;
                // per endpoint: the best stage reached and the most advanced trace
                var byEp = new Dictionary<string, LivePeerTrace>();
                foreach (var t in _traces)
                {
                    if (t.Incoming) { f.Incoming++; if (t.Stage >= LiveStage.HandshakeOk) f.IncomingHandshakeOk++; }
                    LivePeerTrace cur;
                    if (!byEp.TryGetValue(t.EndPoint, out cur) || t.Stage > cur.Stage || (t.Stage == cur.Stage && t.StartAbs > cur.StartAbs)) byEp[t.EndPoint] = t;
                }
                foreach (var t in byEp.Values)
                {
                    if (!t.Incoming && t.Stage >= LiveStage.Connecting) f.Tried++;
                    if (t.Incoming && t.Stage < LiveStage.HandshakeOk) { }
                    if (t.Stage >= LiveStage.TcpOk) f.TcpOk++;
                    if (t.Stage >= LiveStage.HandshakeOk) f.HandshakeOk++;
                    if (t.Stage >= LiveStage.ExtendOk) { f.ExtendOk++; int c; f.NodeTypes.TryGetValue(t.Nt, out c); f.NodeTypes[t.Nt] = c + 1; if (t.Accessible == 1) f.Accessible++; }
                    if (t.Stage >= LiveStage.HaveSeen) f.HaveSeen++;
                    if (t.Stage >= LiveStage.Unchoked) f.Unchoked++;
                    if (t.Stage >= LiveStage.Served) f.Served++;
                    bool recent = t.Stage >= LiveStage.HaveSeen && best >= 0 && t.MaxHave >= best - f.RecentWindow;
                    if (recent) f.RecentHave++;
                    int[] row;
                    string src = SourceGroup(t.Source);
                    if (!f.BySource.TryGetValue(src, out row)) f.BySource[src] = row = new int[5];
                    if (!t.Incoming && t.Stage >= LiveStage.Connecting) row[0]++;
                    if (t.Stage >= LiveStage.TcpOk) row[1]++;
                    if (t.Stage >= LiveStage.HandshakeOk) row[2]++;
                    if (t.Stage >= LiveStage.ExtendOk) row[3]++;
                    if (recent) row[4]++;
                    string cc = t.CloseClass;
                    int n; f.CloseClasses.TryGetValue(cc, out n); f.CloseClasses[cc] = n + 1;
                }
                f.LivePosFirst = _livePosFirst; f.LivePosLast = _livePos; f.LivePosAdvances = _livePosAdvances;
                if (_livePosFirstMs > 0) f.LivePosFirstSeconds = (_livePosFirstMs - _startedMs) / 1000.0;
                if (_livePosChangeMs > 0) f.LivePosLastChangeSeconds = (_livePosChangeMs - _startedMs) / 1000.0;
                if (_playableMs > 0) f.PlayableSeconds = (_playableMs - _startedMs) / 1000.0;
                foreach (var t in _traces)
                {
                    f.FirstTcpSeconds = Earliest(f.FirstTcpSeconds, t.TcpAbs, _startedMs);
                    f.FirstHandshakeSeconds = Earliest(f.FirstHandshakeSeconds, t.HandshakeAbs, _startedMs);
                    f.FirstExtendSeconds = Earliest(f.FirstExtendSeconds, t.ExtendAbs, _startedMs);
                    f.FirstHaveSeconds = Earliest(f.FirstHaveSeconds, t.HaveAbs, _startedMs);
                    f.FirstUnchokeSeconds = Earliest(f.FirstUnchokeSeconds, t.UnchokeAbs, _startedMs);
                    f.FirstPieceSeconds = Earliest(f.FirstPieceSeconds, t.PieceAbs, _startedMs);
                }
                f.Peers.AddRange(_traces);
                return f;
            }
        }

        private static double Earliest(double cur, long abs, long startMs)
        {
            if (abs < 0) return cur;
            double s = (abs - startMs) / 1000.0;
            return cur < 0 || s < cur ? s : cur;
        }

        private int BufferPercentLocked()
        {
            if (!_started2) return 0;
            if (!_playable)
            {
                int got = 0;
                for (long p = _prebufStart; p < _prebufStart + _prebufCount; p++) if (_store.ContainsKey(p)) got++;
                return Math.Min(100, got * 100 / Math.Max(1, _prebufCount));
            }
            int run = 0;
            for (long p = _outPiece; p < _outPiece + _prebufPieces && _store.ContainsKey(p); p++) run++;
            return Math.Min(100, run * 100 / Math.Max(1, _prebufPieces));
        }

        public string DebugInfo()
        {
            lock (_sync)
            {
                return "peers=" + _peers.Count + " connecting=" + _connecting + " cand=" + _cands.Count + " store=" + _store.Count + " active=" + _active.Count
                    + " out=" + _outPiece + " live=" + _livePos + " unchoked=" + _peers.Count(p => !p.PeerChoking) + " sigOk=" + _sigOk + " sigFail=" + _sigFail;
            }
        }
    }

    /// <summary>Internal counters of a live stream (for the CLI, the tests and the UI).</summary>
    public sealed class LiveStats
    {
        internal LivePhase Phase;
        public long LivePos = -1;
        public long OutPiece;
        public bool Started, Playable;
        public double LagSeconds;
        public int StoredPieces, ActivePieces, Peers, Unchoked, Candidates, Connecting;
        public long PiecesVerified, SignatureFailures;
        public bool AcceptingUnverified;
        public int HolesSkipped, PiecesSkipped, Rehooks;
        public long DiscardedBytes, Bytes, StarvedMs;
        public int PrebufferPieces;
        public double PiecesPerSecond;
    }
}
