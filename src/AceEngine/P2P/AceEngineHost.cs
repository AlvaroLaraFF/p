using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>Options when opening a link.</summary>
    public sealed class OpenOptions
    {
        /// <summary>VOD: file inside the torrent (position in info['files']).</summary>
        public int? FileIndex;
        /// <summary>Multi-quality transport: quality to play (the first one by default).</summary>
        public int? QualityIndex;
        /// <summary>Resolution already done by the caller (for example `ace measure`, which wants to keep the peers that served the descriptor). Null = it is resolved here.</summary>
        public ResolvedTransport Resolved;
        /// <summary>Live streams: RAM of the live-with-memory buffer of THIS session, instead of LiveOptions.TimeshiftBytes (sending to a Chromecast needs the buffer even where the player does not). Null = the engine's setting.</summary>
        public long? TimeshiftBytes;
    }

    /// <summary>Result of opening a link: the session already registered on the server and its local URL.</summary>
    public sealed class OpenedStream
    {
        /// <summary>Engine session; null if the link plays directly from its URL (IsDirect).</summary>
        public IStreamSession Session { get; internal set; }
        /// <summary>true for an hls/wrapper descriptor with a visible manifest_url: that URL is passed to the player without the engine (DIRECT mode).</summary>
        public bool IsDirect { get; internal set; }
        public string Url { get; internal set; }
        /// <summary>Parsed transport (null if it was opened by infohash or magnet without a file).</summary>
        public TransportFile Transport { get; internal set; }
        /// <summary>Trust in the channel descriptor (network quorum, first use...). Null if it does not apply.</summary>
        public TrustDecision Trust { get; internal set; }
    }

    /// <summary>
    /// Engine entry point: owns the local HTTP server, the shared DHT client and the peer listener,
    /// and turns a link into a stream session (VOD by infohash, magnet or .torrent file,
    /// content_id and live streams).
    /// </summary>
    public sealed class AceEngineHost : IDisposable
    {
        private const string Tag = "engine";
        private readonly EngineOptions _opt;
        private readonly LocalHttpServer _server;
        private readonly HttpClient _http;
        private DhtClient _dht;
        private ConnectionBudget _probeBudget;                  // TCP connections of the checker, shared by all its schedulers
        private PeerListener _listener;
        private TransportResolver _resolver;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Swarm> _swarms = new Dictionary<string, Swarm>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LiveSwarm> _liveSwarms = new Dictionary<string, LiveSwarm>(StringComparer.OrdinalIgnoreCase);
        private NodeKeys _nodeKeys;
        private bool _started, _disposed;
        private volatile IPortMapper _mapper;
        private bool _upnpWanted;

        /// <param name="portMapper">Port opener (UPnP/NAT-PMP), optional; the host becomes its owner (it stops and releases it on Dispose). If null, a real one is created when SetUpnpEnabled(true) is called.</param>
        public AceEngineHost(EngineOptions options = null, IPortMapper portMapper = null)
        {
            _opt = options ?? new EngineOptions();
            // EXTEND tells the truth about accessibility: only "accessible" if the port is open on the router (UPnP / NAT-PMP) or if someone from outside has already connected to us
            if (_opt.Live.AccessibleProvider == null) _opt.Live.AccessibleProvider = () => UpnpState == UpnpState.Open;
            if (portMapper != null) { _mapper = portMapper; portMapper.Changed += OnPortMapperChanged; }
            _server = new LocalHttpServer(_opt.Http);
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = HttpLimits.ClientBuffer };
        }

        public EngineOptions Options { get { return _opt; } }

        /// <summary>true when the engine has already been released (Dispose): it can no longer be used; whoever holds it must ask for the current one.</summary>
        public bool IsDisposed { get { lock (_gate) return _disposed; } }

        /// <summary>
        /// Applies, with the engine RUNNING, what the user changes in Settings, without restarting it:
        ///  - maximum upload (UploadKBps, from 128 KB/s to 2 MB/s): the global limiter is recreated on the next upload request, for VOD and live;
        ///  - TSService (TsServiceEnabled): it is queried on every link resolution, so it takes effect from the next one.
        /// Applied when the engine is RECREATED (not hot): the upload slots (not chosen by the user), the probe profile and the buffers (they come from AppConfig),
        /// and UPnP (managed by its own service). Upload cannot be disabled from here. Safe from any thread.
        /// </summary>
        public void ApplyUserSettings(AceEngine.App.Tv.UserSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            AceEngine.App.EngineOptionsFactory.ApplyUserSettings(_opt, settings);
            AceLog.Info(Tag, "settings applied hot: upload " + (_opt.UploadMaxBytesPerSec >> 10) + " KB/s, TSService " + (_opt.Resolver.EnableTsService ? "enabled" : "disabled"));
        }
        public LocalHttpServer Server { get { return _server; } }
        public int ListenPort { get { return _listener == null ? 0 : _listener.Port; } }

        /// <summary>
        /// Port announced to peers (extended handshake) and to trackers: the external port of the UPnP/NAT-PMP mapping when it is open, otherwise the listener's.
        /// 0 if there is no listener.
        /// </summary>
        public int AnnouncedPort
        {
            get
            {
                int lp = ListenPort;
                var m = _mapper;
                if (m != null && lp > 0 && m.State == UpnpState.Open)
                {
                    int p = m.OpenPort;
                    if (p > 0 && p < 65536) return p;
                }
                return lp;
            }
        }

        /// <summary>The port mapper (null until one is assigned or SetUpnpEnabled(true) is called). Assigning a new one stops and releases the previous one.</summary>
        public IPortMapper PortMapper
        {
            get { return _mapper; }
            set
            {
                IPortMapper old;
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException("AceEngineHost");
                    old = _mapper;
                    if (ReferenceEquals(old, value)) return;
                    _mapper = value;
                }
                if (old != null) { old.Changed -= OnPortMapperChanged; try { old.Stop(); old.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "previous mapper: " + ex.Message); } }
                if (value != null) value.Changed += OnPortMapperChanged;
                if (_upnpWanted) ApplyUpnp();
                OnPortMapperChanged();
            }
        }

        /// <summary>Mapping state or announced port changes (thread-pool thread). Re-read State/OpenPort of the PortMapper and AnnouncedPort.</summary>
        public event Action PortMapperChanged;

        private int _lastAnnounced;

        private void OnPortMapperChanged()
        {
            int now = AnnouncedPort;
            if (now != _lastAnnounced)
            {
                AceLog.Info(Tag, "port announced to peers and trackers: " + _lastAnnounced + " -> " + now);
                _lastAnnounced = now;
            }
            var h = PortMapperChanged;
            if (h != null) { try { h(); } catch (Exception ex) { AceLog.Warn(Tag, "PortMapperChanged: " + ex.Message); } }
        }

        public bool UpnpEnabled { get { return _upnpWanted; } }

        /// <summary>
        /// State for the Settings screen: Disabled if the setting is off; Unavailable if it is on but the engine has no listener to open;
        /// Searching while the engine or the mapper have not started; otherwise, the mapper's.
        /// </summary>
        public UpnpState UpnpState
        {
            get
            {
                if (!_upnpWanted) return UpnpState.Disabled;
                if (_started && ListenPort <= 0) return UpnpState.Unavailable;
                var m = _mapper;
                if (m == null) return UpnpState.Searching;
                var st = m.State;
                return st == UpnpState.Disabled ? UpnpState.Searching : st;
            }
        }

        /// <summary>External port opened on the router (for "Puerto 8621 abierto"); 0 if UpnpState is not Open.</summary>
        public int UpnpOpenPort
        {
            get
            {
                var m = _mapper;
                return UpnpState == UpnpState.Open && m != null ? m.OpenPort : 0;
            }
        }

        /// <summary>Creates the mapper when SetUpnpEnabled(true) needs it and none is assigned. By default, a real PortMapper (UPnP + NAT-PMP).</summary>
        public Func<IPortMapper> PortMapperFactory { get; set; }

        /// <summary>
        /// Enables or disables opening the P2P port on the router (UPnP setting). If the engine has not started yet, it is applied on start. Enabling creates a real PortMapper
        /// if none is assigned. It does not block: the state is followed through PortMapper.State / PortMapperChanged.
        /// </summary>
        public void SetUpnpEnabled(bool enabled)
        {
            IPortMapper m;
            lock (_gate)
            {
                if (_disposed) return;
                _upnpWanted = enabled;
                m = _mapper;
            }
            if (!enabled)
            {
                if (m != null) m.Stop();
                return;
            }
            ApplyUpnp();
        }

        private void ApplyUpnp()
        {
            IPortMapper m;
            int port;
            lock (_gate)
            {
                if (_disposed || !_upnpWanted || !_started) return;
                port = ListenPort;
                if (port <= 0) { AceLog.Warn(Tag, "UPnP: no listening port to open"); return; }
                if (_mapper == null) { var f = PortMapperFactory; _mapper = f != null ? f() : new PortMapper(); _mapper.Changed += OnPortMapperChanged; }
                m = _mapper;
            }
            try { m.Start(port, port); }
            catch (Exception ex) { AceLog.Warn(Tag, "UPnP: " + ex.Message); }
        }
        public int DhtNodes { get { return _dht == null ? 0 : _dht.KnownNodes; } }
        /// <summary>DHT client of the engine (diagnostics and tools; null if disabled).</summary>
        public DhtClient DhtClientForDiagnostics { get { return _dht; } }

        /// <summary>Link resolver (content_id via P2P + TSService, infohash, magnet, url, file) with its cache.</summary>
        public TransportResolver Resolver
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException("AceEngineHost");
                    if (_resolver == null) _resolver = new TransportResolver(_opt, _dht, _http);
                    return _resolver;
                }
            }
        }

        /// <summary>Probe scheduler over the resolver, the DHT and the engine's HTTP client. Start the engine first.</summary>
        /// <summary>Connection budget of the checker (null until the first scheduler is created or if TcpBudget is 0): used by the measurement tool.</summary>
        public ConnectionBudget ProbeBudget { get { lock (_gate) return _probeBudget; } }

        public ProbeScheduler CreateProbeScheduler()
        {
            if (!_started) Start();
            ConnectionBudget budget;
            lock (_gate) { if (_probeBudget == null && _opt.Probe.TcpBudget > 0) _probeBudget = new ConnectionBudget(_opt.Probe.TcpBudget); budget = _probeBudget; }
            var prober = new LinkProber(Resolver, _dht, _http, _opt.Probe, null, budget);
            if (_opt.Probe.VerifyLive)
            {
                NodeKeys keys;
                lock (_gate) { if (_nodeKeys == null) _nodeKeys = NodeKeys.Generate(); keys = _nodeKeys; }       // the same identity as the live streams
                prober.Live = new LiveProber(_opt, _dht, keys, _http, () => AnnouncedPort);
            }
            return new ProbeScheduler(prober, _opt.Probe);
        }

        /// <summary>Downloads a list of links: a mylinkspaste code (recursive DNS TXT) or the URL of an M3U list. No duplicates.</summary>
        public async Task<List<LinkEntry>> LoadLinksAsync(string codeOrUrl, CancellationToken ct)
        {
            if (codeOrUrl == null) throw new ArgumentNullException("codeOrUrl");
            if (codeOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || codeOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return LinkList.Dedupe(await M3uParser.LoadAsync(codeOrUrl, _http, ct).ConfigureAwait(false));
            var client = new MyLinksPasteClient(new DnsTxtClient(_opt.Dns, _http));
            var tree = await client.FetchAsync(codeOrUrl, ct).ConfigureAwait(false);
            return LinkList.Dedupe(tree.Entries);
        }

        /// <summary>get_media_files: resolves the link to a transport file and lists the playable files.</summary>
        public async Task<MediaFilesResult> GetMediaFilesAsync(AceLink link, MediaFilesOptions options, CancellationToken ct)
        {
            if (!_started) Start();
            if (options == null) options = new MediaFilesOptions();
            var res = await Resolver.ResolveAsync(link, ct, options.UseLocalCacheOnly).ConfigureAwait(false);
            var tf = res.Transport;
            if (tf.IsMulti) { /* MediaFiles.Get lists one entry per quality */ }
            return MediaFiles.Get(tf, options);
        }

        /// <summary>Starts the HTTP server, DHT and listener (according to the options).</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            _server.Start();
            if (_opt.EnableListener)
            {
                try
                {
                    _listener = new PeerListener(ih =>
                    {
                        lock (_gate)
                        {
                            string k = Hex.ToHex(ih);
                            Swarm s; LiveSwarm l;
                            if (_swarms.TryGetValue(k, out s)) return s;
                            return _liveSwarms.TryGetValue(k, out l) ? (IIncomingTarget)l : null;
                        }
                    });
                    _listener.Start(_opt.ListenPort, _opt.PreferredListenPort);
                }
                catch (Exception ex)
                {
                    AceLog.Warn(Tag, "could not open the listening port: " + ex.Message);
                    _listener = null;
                }
            }
            if (_opt.EnableDht)
            {
                try
                {
                    _dht = new DhtClient(_opt);
                    _dht.Start();
                    if (!string.IsNullOrEmpty(_opt.DhtNodesPath))
                    {
                        int loaded = _dht.LoadNodes(_opt.DhtNodesPath);
                        if (loaded > 0) AceLog.Info(Tag, "DHT: " + loaded + " saved nodes loaded from " + _opt.DhtNodesPath);
                        var dhtRef = _dht;
                        var ignored = Task.Run(async () =>
                        {
                            try { await Task.Delay(TimeSpan.FromSeconds(90)).ConfigureAwait(false); if (!_disposed) dhtRef.SaveNodes(_opt.DhtNodesPath); }
                            catch (Exception) { }
                        });
                    }
                }
                catch (Exception ex)
                {
                    AceLog.Warn(Tag, "DHT disabled: " + ex.Message);
                    _dht = null;
                }
            }
            _lastAnnounced = ListenPort;
            ApplyUpnp();
        }

        /// <summary>Opens a link and returns the session already registered on the server. For a magnet/infohash, the metadata is resolved in the background.</summary>
        public Task<OpenedStream> OpenAsync(AceLink link, int? fileIndex, CancellationToken ct)
        {
            return OpenStreamAsync(link, new OpenOptions { FileIndex = fileIndex }, ct);
        }

        public async Task<OpenedStream> OpenStreamAsync(AceLink link, OpenOptions options, CancellationToken ct)
        {
            if (!_started) Start();
            if (options == null) options = new OpenOptions();
            int? fileIndex = options.FileIndex;
            byte[] infoHash;
            TorrentMeta meta = null;
            TransportFile tf = null;
            List<string> trackers = null;
            List<IPEndPoint> hints = null;
            TrustDecision trust = null;

            switch (link.Kind)
            {
                case AceLinkKind.InfoHash:
                case AceLinkKind.Magnet:
                case AceLinkKind.ContentId:
                case AceLinkKind.TransportFile:
                case AceLinkKind.TransportUrl:
                    {
                        // infohash and magnet (e.g. a result from Ace's search) can be a live stream or a VOD and only the swarm knows:
                        // they are resolved to their descriptor over P2P (BEP9 or 'full' depending on the peer) and a live stream is played as a content_id.
                        if (link.Kind == AceLinkKind.Magnet)
                        {
                            trackers = link.Magnet.Trackers.ToList();
                            hints = ParseHints(link.Magnet.PeerHints);
                        }
                        // content_id -> cache / P2P / TSService; file and URL are loaded directly.
                        var resolved = options.Resolved ?? await Resolver.ResolveAsync(link, ct).ConfigureAwait(false);
                        AceLog.Info(Tag, "link resolved by " + resolved.Source + " in " + resolved.Elapsed.TotalSeconds.ToString("0.0") + " s");
                        tf = resolved.Transport;
                        trust = resolved.Trust;
                        if (tf.IsMulti)
                        {
                            int q = options.QualityIndex ?? 0;
                            if (q < 0 || q >= tf.Qualities.Count) throw new ArgumentException("el transporte tiene " + tf.Qualities.Count + " calidades; indice " + q + " fuera de rango");
                            tf = tf.SelectQuality(q);
                        }
                        if (tf.IsNewFormat && (tf.Type == "hls" || tf.Type == "wrapper" || tf.Type == "dash"))
                        {
                            // an HLS/wrapper descriptor with a visible manifest_url plays directly; dash does not exist even in the original
                            string manifest = tf.Root == null ? null : (tf.Root.GetString("manifest_url") ?? tf.Root.GetString("hidden_manifest_url"));
                            if (tf.Type != "dash" && !string.IsNullOrEmpty(manifest) && (manifest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || manifest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                            {
                                AceLog.Info(Tag, "descriptor " + tf.Type + " with manifest_url: playing directly " + manifest);
                                return new OpenedStream { Session = null, Url = manifest, Transport = tf, IsDirect = true, Trust = trust };
                            }
                            throw new NotSupportedException("transporte " + tf.Type + " sin manifest_url visible (o dash): no soportado");
                        }
                        if (tf.IsLive)
                        {
                            var liveOpened = await OpenLiveAsync(tf, options, ct, resolved.RequestedContentId, resolved.GoodPeers).ConfigureAwait(false);
                            liveOpened.Trust = trust;
                            return liveOpened;
                        }
                        if (tf.Torrent == null) throw new NotSupportedException("transporte " + tf.Type + " no es un torrent VOD");
                        meta = tf.Torrent;
                        infoHash = meta.InfoHash;
                        break;
                    }
                default:
                    throw new NotSupportedException(link.Kind.ToString());
            }

            Swarm swarm;
            string key = Hex.ToHex(infoHash);
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("AceEngineHost");
                if (_swarms.ContainsKey(key))
                    throw new InvalidOperationException("ya hay una sesion abierta para el infohash " + key);
                swarm = new Swarm(_opt, infoHash, meta, trackers, fileIndex ?? -1, _dht, _http, () => AnnouncedPort);
                _swarms[key] = swarm;
            }
            var session = new VodSession(swarm, s =>
            {
                lock (_gate) { Swarm x; if (_swarms.TryGetValue(key, out x) && x == swarm) _swarms.Remove(key); }
                _server.Unregister(s.Id);
            });
            swarm.Start();
            if (hints != null) swarm.AddCandidates(hints, "magnet x.pe");
            if (_opt.StaticPeers.Count > 0) swarm.AddCandidates(ParseHints(_opt.StaticPeers), "StaticPeers");
            string url = _server.Register(session);
            AceLog.Info(Tag, "session " + session.Id + " for " + key + " -> " + url);
            return new OpenedStream { Session = session, Url = url, Transport = tf, Trust = trust };
        }

        private async Task<OpenedStream> OpenLiveAsync(TransportFile tf, OpenOptions options, CancellationToken ct, string requestedContentId = null, IList<IPEndPoint> initialPeers = null)
        {
            LiveDescriptor desc = tf.Live;
            if (desc == null)
            {
                if (tf.Torrent != null && tf.Torrent.IsLive) desc = LiveDescriptor.FromLiveTorrent(tf.Torrent);
                else throw new NotSupportedException("transporte " + tf.Type + ": solo se reproducen directos BT clasicos (tt='bt'); los descriptores hls/dash/wrapper no se soportan");
            }
            LiveSwarm swarm;
            string key = Hex.ToHex(desc.InfoHash);
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("AceEngineHost");
                if (_liveSwarms.ContainsKey(key)) throw new InvalidOperationException("ya hay una sesion de directo abierta para el infohash " + key);
                if (_nodeKeys == null) _nodeKeys = NodeKeys.Generate();
                swarm = new LiveSwarm(_opt, desc, tf.RawBytes, requestedContentId, _dht, _http, () => AnnouncedPort, _nodeKeys);
                _liveSwarms[key] = swarm;
            }
            var session = new LiveSession(swarm, s =>
            {
                lock (_gate) { LiveSwarm x; if (_liveSwarms.TryGetValue(key, out x) && x == swarm) _liveSwarms.Remove(key); }
                RememberLivePeers(swarm, key, requestedContentId);
                _server.Unregister(s.Id);
            }, options.TimeshiftBytes);
            swarm.Start();
            // Peers that already worked with this channel (descriptor, live probe, previous sessions) and those that served the descriptor: they are tried first
            var initial = new List<IPEndPoint>();
            if (initialPeers != null) initial.AddRange(initialPeers);
            if (_opt.Live.UsePeerHints)
            {
                try
                {
                    initial.AddRange(Resolver.Hints.Get(key));
                    if (requestedContentId != null) initial.AddRange(Resolver.Hints.Get(requestedContentId));
                }
                catch (ObjectDisposedException) { }
            }
            if (initial.Count > 0) swarm.AddInitialPeers(initial.Distinct());
            string url = _server.Register(session);
            AceLog.Info(Tag, "live session " + session.Id + " '" + desc.Name + "' (infohash " + key + ") -> " + url);
            await Task.CompletedTask;
            return new OpenedStream { Session = session, Url = url, Transport = tf };
        }

        /// <summary>When a live session closes, the peers that answered well are remembered (EXTEND with pieces, even better if they served pieces): the next opening starts with them.</summary>
        private void RememberLivePeers(LiveSwarm swarm, string infoHashKey, string contentId)
        {
            if (!_opt.Live.UsePeerHints) return;
            try
            {
                var good = swarm.GoodPeers();
                if (good.Count == 0) return;
                var h = Resolver.Hints;
                h.Add(infoHashKey, good);
                if (contentId != null) h.Add(contentId, good);
            }
            catch (Exception ex) { AceLog.Debug(Tag, "could not remember the peers: " + ex.Message); }
        }

        private static List<IPEndPoint> ParseHints(IEnumerable<string> hints)
        {
            var res = new List<IPEndPoint>();
            foreach (var h in hints)
            {
                string host; int port;
                if (!Net.TryParseHostPort(h, out host, out port)) continue;
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    res.Add(new IPEndPoint(ip, port));
            }
            return res;
        }

        public void Dispose()
        {
            List<Swarm> swarms;
            List<LiveSwarm> liveSwarms;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                swarms = _swarms.Values.ToList();
                _swarms.Clear();
                liveSwarms = _liveSwarms.Values.ToList();
                _liveSwarms.Clear();
            }
            foreach (var s in _server.Sessions.ToList()) { try { s.Dispose(); } catch { } }
            foreach (var s in swarms) { try { s.Dispose(); } catch { } }
            foreach (var l in liveSwarms) { try { l.Dispose(); } catch { } }
            var mapper = _mapper;
            if (mapper != null) { mapper.Changed -= OnPortMapperChanged; try { mapper.Stop(); mapper.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "port mapper: " + ex.Message); } }
            if (_listener != null) _listener.Dispose();
            if (_resolver != null) _resolver.Dispose();
            if (_dht != null)
            {
                if (!string.IsNullOrEmpty(_opt.DhtNodesPath)) { try { _dht.SaveNodes(_opt.DhtNodesPath); } catch (Exception ex) { AceLog.Debug(Tag, "could not save the DHT nodes: " + ex.Message); } }
                _dht.Dispose();
            }
            _server.Dispose();
            _http.Dispose();
        }
    }
}
