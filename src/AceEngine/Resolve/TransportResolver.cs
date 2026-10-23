using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>A link could not be turned into a transport file ('cannot get transport file').</summary>
    public sealed class ResolveException : Exception
    {
        public ResolveException(string message) : base(message) { }
        public ResolveException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Result of resolving a link.</summary>
    public sealed class ResolvedTransport
    {
        public TransportFile Transport { get; internal set; }
        /// <summary>"local_db" (cache), "magnet" (P2P), "server" (TSService), "url", "file".</summary>
        public string Source { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        /// <summary>content_id requested (the link's). It may not match Transport.ContentId (SHA-1 of the file).</summary>
        public string RequestedContentId { get; internal set; }
        /// <summary>Peers that delivered the file over P2P (Ace engines of the real network): good initial candidates for the live stream.</summary>
        public IList<System.Net.IPEndPoint> GoodPeers { get; internal set; }
        /// <summary>false if the transport file was built from an infohash (minimal torrent): its SHA-1 is not a content_id.</summary>
        public bool HasContentId { get; internal set; }
        /// <summary>Trust in the descriptor (network quorum, first use, identity changes). Null if not applicable (local file, verified infohash).</summary>
        public TrustDecision Trust { get; internal set; }
    }

    /// <summary>
    /// Turns a link into a loaded transport file. content_id: local cache, and in parallel the P2P path
    /// (DHT + ut_metadata 'full') and TSService; the first one whose bytes satisfy SHA-1 == content_id wins. infohash and magnet: BEP9 and
    /// TSService (infohash). url and file: they are read and loaded.
    /// </summary>
    public sealed class TransportResolver : IDisposable
    {
        private const string Tag = "resolver";
        private readonly EngineOptions _opt;
        private readonly DhtClient _dht;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly MetadataFetcher _fetcher;
        private readonly TsServiceClient _ts;

        public ResolutionCache Cache { get; private set; }

        /// <summary>Peers that worked with each channel (served the descriptor, answered the live probe or served pieces): they are tried first when opening it.</summary>
        public PeerHintStore Hints { get; private set; }

        /// <summary>TCP connections currently open by the resolutions, the peak seen and those attempted (probe diagnostics).</summary>
        public int ActiveConnections { get { return _fetcher.ActiveConnections; } }
        public int PeakConnections { get { return _fetcher.PeakConnections; } }
        public long ConnectionAttempts { get { return _fetcher.ConnectionAttempts; } }

        /// <summary>Trust on first use of the channel descriptors.</summary>
        public TrustStore Trust { get; private set; }

        public TransportResolver(EngineOptions options, DhtClient dht, HttpClient http = null, ResolutionCache cache = null)
        {
            _opt = options ?? new EngineOptions();
            _dht = dht;
            if (http == null)
            {
                var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
                _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                _ownsHttp = true;
            }
            else _http = http;
            var r = _opt.Resolver;
            Cache = cache ?? new ResolutionCache(r.CacheTtl, r.CacheMaxEntries, r.CacheMaxBytes);
            if (cache == null && !string.IsNullOrEmpty(r.CachePath))
            {
                int n = Cache.Load(r.CachePath);
                if (n > 0) AceLog.Info(Tag, "resolution cache: " + n + " entries from " + r.CachePath);
            }
            Trust = new TrustStore(r.TrustPath);
            Hints = new PeerHintStore(r.HintsTtl);
            if (!string.IsNullOrEmpty(r.HintsPath))
            {
                int hn = Hints.Load(r.HintsPath);
                if (hn > 0) AceLog.Info(Tag, "peer hints: " + hn + " channels from " + r.HintsPath);
                Hints.Changed += ScheduleHintsSave;
            }
            _fetcher = new MetadataFetcher(_opt, dht, _http);
            _ts = new TsServiceClient(r, _http);
        }

        public async Task<ResolvedTransport> ResolveAsync(AceLink link, CancellationToken ct, bool localCacheOnly = false, bool allowTsService = true, ResolveProfile profile = null)
        {
            if (link == null) throw new ArgumentNullException("link");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ResolvedTransport res;
            switch (link.Kind)
            {
                case AceLinkKind.ContentId:
                    res = await ResolveContentIdAsync(link.Value, ct, localCacheOnly, allowTsService, profile).ConfigureAwait(false);
                    break;
                case AceLinkKind.InfoHash:
                    res = await ResolveInfoHashAsync(Hex.Parse(link.Value), null, null, ct, localCacheOnly, allowTsService, profile).ConfigureAwait(false);
                    break;
                case AceLinkKind.Magnet:
                    res = await ResolveInfoHashAsync(link.Magnet.InfoHash, link.Magnet.Trackers, ParseHints(link.Magnet.PeerHints), ct, localCacheOnly, allowTsService, profile).ConfigureAwait(false);
                    break;
                case AceLinkKind.TransportUrl:
                    {
                        if (localCacheOnly) throw new ResolveException("no_transport_file_in_local_cache");
                        byte[] data;
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromSeconds(30));
                            using (var resp = await _http.GetAsync(link.Value, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                            {
                                resp.EnsureSuccessStatusCode();
                                var len = resp.Content.Headers.ContentLength;
                                if (len.HasValue && len.Value > _opt.Resolver.MaxTransportBytes) throw new ResolveException("transport file too large: " + len.Value);
                                data = await HttpLimits.ReadBodyAsync(resp.Content, _opt.Resolver.MaxTransportBytes, cts.Token).ConfigureAwait(false);
                            }
                        }
                        if (data.Length > _opt.Resolver.MaxTransportBytes) throw new ResolveException("transport file too large: " + data.Length);
                        res = new ResolvedTransport { Transport = LoadTransport(data), Source = "url", HasContentId = true };
                        break;
                    }
                case AceLinkKind.TransportFile:
                    {
                        if (localCacheOnly) throw new ResolveException("no_transport_file_in_local_cache");
                        if (!_opt.AllowLocalFileLinks) throw new ResolveException("local files are not allowed in this configuration");
                        if (!File.Exists(link.Value)) throw new FileNotFoundException("the transport file does not exist", link.Value);
                        res = new ResolvedTransport { Transport = LoadTransport(File.ReadAllBytes(link.Value)), Source = "file", HasContentId = true };
                        break;
                    }
                default:
                    throw new ResolveException("missing content descriptor");
            }
            res.Elapsed = sw.Elapsed;
            return res;
        }

        private static List<IPEndPoint> ParseHints(IEnumerable<string> hints)
        {
            var res = new List<IPEndPoint>();
            foreach (var h in hints)
            {
                string host; int port;
                IPAddress ip;
                if (AceEngine.P2P.Net.TryParseHostPort(h, out host, out port) && IPAddress.TryParse(host, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    res.Add(new IPEndPoint(ip, port));
            }
            return res;
        }

        /// <summary>Loads the transport; a malformed file or URL throws TransportException as is.</summary>
        private TransportFile LoadTransport(byte[] data)
        {
            return TransportLoader.Load(data, new TransportLoadOptions { Strict = false });
        }

        /// <summary>What arrives from the network (P2P, TSService) and cannot be interpreted is a resolution failure.</summary>
        private TransportFile LoadResolved(byte[] data)
        {
            try { return TransportLoader.Load(data, new TransportLoadOptions { Strict = _opt.Resolver.StrictTransport }); }
            catch (TransportException ex) { throw new ResolveException("the transport file cannot be interpreted: " + ex.Message, ex); }
        }

        // ---------- content_id ----------

        private sealed class Raw
        {
            public byte[] Data;
            public string Source;
            public FetchReport Report;
        }

        private static IList<IPEndPoint> Snapshot(List<IPEndPoint> l)
        {
            lock (l) return l.ToList();
        }

        public async Task<ResolvedTransport> ResolveContentIdAsync(string contentIdHex, CancellationToken ct, bool localCacheOnly = false, bool allowTsService = true, ResolveProfile profile = null)
        {
            string id = contentIdHex.ToLowerInvariant();
            byte[] cached;
            if (Cache.TryGet(id, out cached))
            {
                AceLog.Info(Tag, "content_id " + id + ": in the local cache");
                var cachedTf = LoadTransport(cached);
                var known = Trust.Get(id);
                var cachedTrust = new TrustDecision { Level = known != null && known.Confirmed ? TrustLevel.Confirmed : TrustLevel.Unconfirmed };
                if (cachedTrust.Level == TrustLevel.Unconfirmed) cachedTrust.Note = "Canal no confirmado: solo lo ha entregado un par";
                return new ResolvedTransport { Transport = cachedTf, Source = "local_db", HasContentId = true, RequestedContentId = id, Trust = cachedTrust, GoodPeers = Hints.Get(id) };
            }
            if (localCacheOnly) throw new ResolveException("no_transport_file_in_local_cache");

            var r = _opt.Resolver;
            var hash = Hex.Parse(id);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (r.Timeout > TimeSpan.Zero) cts.CancelAfter(r.Timeout);
                var token = cts.Token;
                var pending = new List<Task<Raw>>();
                var good = new List<IPEndPoint>();
                if (r.EnableP2p)
                    pending.Add(Guarded(async () =>
                    {
                        var report = new FetchReport();
                        var seeds = new List<IPEndPoint>();
                        if (_opt.StaticPeers.Count > 0) seeds.AddRange(ParseHints(_opt.StaticPeers));
                        if (_opt.Live.UsePeerHints) seeds.AddRange(Hints.Get(id));              // those that already served this channel's descriptor or live stream: they are tried first
                        var data = await _fetcher.FetchAsync(hash, MetadataMode.AceFull, null, seeds.Count > 0 ? seeds : null, TimeSpan.Zero, token, good, report, profile).ConfigureAwait(false);
                        return data == null ? null : new Raw { Data = data, Source = "magnet", Report = report };
                    }, "P2P"));
                if (r.EnableTsService && allowTsService)
                    pending.Add(Guarded(async () =>
                    {
                        var data = await _ts.GetTorrentAsync(id, false, token).ConfigureAwait(false);
                        if (data == null) return null;
                        if (Hex.ToHex(HashUtil.Sha1(data)) != id)
                        {
                            AceLog.Warn(Tag, "TSService returned data whose SHA-1 (" + Hex.ToHex(HashUtil.Sha1(data)) + ") is not the content_id; discarded");
                            return null;
                        }
                        return new Raw { Data = data, Source = "server", Report = new FetchReport { ExactHash = true, Networks = 1, Peers = 1 } };
                    }, "TSService"));
                if (pending.Count == 0) throw new ResolveException("cannot get transport file: no resolution path enabled");

                Raw won = null;
                while (pending.Count > 0 && won == null)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);
                    var raw = await done.ConfigureAwait(false);
                    if (raw != null && raw.Data != null) won = raw;
                }
                try { cts.Cancel(); } catch { }
                ct.ThrowIfCancellationRequested();
                if (won == null) throw new ResolveException("cannot get transport file (content_id " + id + ")");

                AceLog.Info(Tag, "content_id " + id + ": resolved by " + won.Source + " (" + won.Data.Length + " bytes)");
                var tf = LoadResolved(won.Data);
                // Trust on first use: network quorum and a check that the channel identity has not changed (throws ResolveException if it changes without quorum)
                var trust = Trust.Evaluate(id, tf, won.Data, won.Report, Math.Max(1, r.WeakQuorum), r.TrustEnforce);
                AceLog.Info(Tag, "content_id " + id + ": trust " + trust);
                Cache.Put(id, won.Data);
                SaveCacheQuietly();
                var goodNow = Snapshot(good);
                Hints.Add(id, goodNow);
                return new ResolvedTransport { Transport = tf, Source = won.Source, HasContentId = true, RequestedContentId = id, GoodPeers = goodNow, Trust = trust };
            }
        }

        private static async Task<Raw> Guarded(Func<Task<Raw>> f, string name)
        {
            try { return await f().ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "path " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private long _hintsSavedTicks;
        private int _hintsSaving;

        /// <summary>Saves the hints at most once every 30 s (the TV writes to flash): the first change after that interval triggers the save.</summary>
        private void ScheduleHintsSave()
        {
            var path = _opt.Resolver.HintsPath;
            if (string.IsNullOrEmpty(path)) return;
            long now = Environment.TickCount;
            if (unchecked(now - Interlocked.Read(ref _hintsSavedTicks)) < 30000 && Interlocked.Read(ref _hintsSavedTicks) != 0) return;
            if (Interlocked.Exchange(ref _hintsSaving, 1) != 0) return;
            Interlocked.Exchange(ref _hintsSavedTicks, now == 0 ? 1 : now);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Thread.Sleep(2000); Hints.Save(path); }
                catch (Exception ex) { AceLog.Debug(Tag, "could not save the hints: " + ex.Message); }
                finally { Interlocked.Exchange(ref _hintsSaving, 0); }
            });
        }

        /// <summary>Saves the peer hints now (when closing the engine).</summary>
        public void SaveHintsNow()
        {
            var path = _opt.Resolver.HintsPath;
            if (string.IsNullOrEmpty(path)) return;
            try { Hints.Save(path); } catch (Exception ex) { AceLog.Debug(Tag, "could not save the hints: " + ex.Message); }
        }

        private void SaveCacheQuietly()
        {
            var path = _opt.Resolver.CachePath;
            if (string.IsNullOrEmpty(path)) return;
            try { Cache.Save(path); } catch (Exception ex) { AceLog.Debug(Tag, "could not save the cache: " + ex.Message); }
        }

        // ---------- infohash / magnet ----------

        /// <summary>Cap of a file stored in the infohash cache: a VOD 'info' with thousands of pieces does not fit in the TV's persistent cache.</summary>
        private const int MaxCachedInfoBytes = 256 * 1024;

        /// <summary>
        /// infohash -> transport file: 1) local cache; 2) P2P (and TSService if enabled), the first path that delivers something that
        /// matches the infohash. Over P2P each peer decides (MetadataMode.InfoHash): a VOD torrent gives its 'info' by BEP9 and an Ace live stream, which has no
        /// 'info', gives its complete descriptor ('full'). The result is always verified against the requested infohash (InfoHashVerifier). If it is a
        /// live stream, Transport.IsLive is true and it plays like a content_id; if it is VOD, Transport.Torrent is the minimal torrent (info + trackers of the link).
        /// </summary>
        public async Task<ResolvedTransport> ResolveInfoHashAsync(byte[] infoHash, IEnumerable<string> trackers, IEnumerable<IPEndPoint> peers,
            CancellationToken ct, bool localCacheOnly = false, bool allowTsService = true, ResolveProfile profile = null)
        {
            string ihex = Hex.ToHex(infoHash);
            var r = _opt.Resolver;
            var trackerList = trackers == null ? new List<string>() : trackers.ToList();

            // 1) local cache; it is checked against the infohash again when read (the cache is shared by all links and persisted on disk)
            byte[] cached;
            if (Cache.TryGet(ihex, out cached))
            {
                string why;
                var ctf = InfoHashVerifier.Match(cached, infoHash, r.StrictTransport, out why);
                if (ctf != null)
                {
                    AceLog.Info(Tag, "infohash " + ihex + ": in the local cache");
                    return new ResolvedTransport { Transport = WithLinkTrackers(ctf, trackerList), Source = "local_db", HasContentId = false, Trust = new TrustDecision { Level = TrustLevel.Verified }, GoodPeers = Hints.Get(ihex) };
                }
                AceLog.Warn(Tag, "infohash " + ihex + ": the cached file does not correspond to it (" + why + "); ignored");
            }
            if (localCacheOnly) throw new ResolveException("no_transport_file_in_local_cache");

            var peerList = (peers == null ? Enumerable.Empty<IPEndPoint>() : peers).Concat(_opt.StaticPeers.Count > 0 ? ParseHints(_opt.StaticPeers) : new List<IPEndPoint>()).ToList();
            if (_opt.Live.UsePeerHints) peerList.AddRange(Hints.Get(ihex).Where(h => !peerList.Contains(h)));          // those that already served this channel's descriptor or live stream: they are tried first
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (r.Timeout > TimeSpan.Zero) cts.CancelAfter(r.Timeout);
                var token = cts.Token;
                var pending = new List<Task<Raw>>();
                var good = new List<IPEndPoint>();
                if (r.EnableP2p)
                    pending.Add(Guarded(async () =>
                    {
                        var report = new FetchReport();
                        var data = await _fetcher.FetchAsync(infoHash, MetadataMode.InfoHash, trackerList, peerList, TimeSpan.Zero, token, good, report, profile).ConfigureAwait(false);
                        if (data == null) return null;
                        // BEP9 only gives the 'info': it is wrapped in a minimal torrent. A live stream already arrives as a transport file.
                        if (report.Payload == MetadataPayload.Info) data = BuildMinimalTorrent(data, trackerList);
                        return new Raw { Data = data, Source = "magnet", Report = report };
                    }, "P2P"));
                if (r.EnableTsService && allowTsService)
                    pending.Add(Guarded(async () =>
                    {
                        var data = await _ts.GetTorrentAsync(ihex, true, token).ConfigureAwait(false);
                        if (data == null) return null;
                        if (!InfoHashVerifier.Matches(data, infoHash, r.StrictTransport)) { AceLog.Warn(Tag, "TSService: the returned file does not match the infohash"); return null; }
                        return new Raw { Data = data, Source = "server" };
                    }, "TSService"));
                if (pending.Count == 0) throw new ResolveException("cannot get transport file: no resolution path enabled");

                Raw won = null;
                while (pending.Count > 0 && won == null)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);
                    var raw = await done.ConfigureAwait(false);
                    if (raw != null && raw.Data != null) won = raw;
                }
                try { cts.Cancel(); } catch { }
                ct.ThrowIfCancellationRequested();
                if (won == null) throw new ResolveException("cannot get transport file (infohash " + ihex + ")");
                AceLog.Info(Tag, "infohash " + ihex + ": resolved by " + won.Source + " (" + won.Data.Length + " bytes)");
                var tf = LoadResolved(won.Data);
                // the path has already checked it; it is repeated here with the final result so as not to depend on every path doing it
                if (tf.InfoHash == null || !HashUtil.ConstantTimeEquals(tf.InfoHash, infoHash))
                    throw new ResolveException("the received transport file does not match the infohash " + ihex);
                // the cache stores the 'info' without this link's trackers (another link to the same infohash may bring others)
                byte[] toCache = tf.Torrent != null && !tf.IsLive ? BuildMinimalTorrent(tf.Torrent.InfoRaw, null) : won.Data;
                if (toCache.Length <= MaxCachedInfoBytes)
                {
                    Cache.Put(ihex, toCache);
                    SaveCacheQuietly();
                }
                var goodNow = Snapshot(good);
                Hints.Add(ihex, goodNow);                                  // as with a content_id: those that served the descriptor are tried first when probing and opening
                return new ResolvedTransport { Transport = tf, Source = won.Source, HasContentId = false, GoodPeers = goodNow, Trust = new TrustDecision { Level = TrustLevel.Verified } };
            }
        }

        /// <summary>A cached torrent goes without trackers: those of the current link are put on it (a live stream does not need them, its descriptor carries them).</summary>
        private TransportFile WithLinkTrackers(TransportFile tf, IList<string> trackers)
        {
            if (tf.Torrent == null || tf.IsLive || trackers.Count == 0 || tf.Torrent.GetTrackerTiers().Count > 0) return tf;
            return LoadResolved(BuildMinimalTorrent(tf.Torrent.InfoRaw, trackers));
        }

        /// <summary>Minimal torrent {announce?, announce-list?, info} from the raw 'info' (MagnetLink.metainfo_retrieved).</summary>
        public static byte[] BuildMinimalTorrent(byte[] infoRaw, IList<string> trackers)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte((byte)'d');
                var tr = trackers == null ? new List<string>() : trackers.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
                if (tr.Count > 0)
                {
                    WriteKey(ms, "announce"); WriteStr(ms, tr[0]);
                    if (tr.Count > 1)
                    {
                        WriteKey(ms, "announce-list");
                        ms.WriteByte((byte)'l');
                        foreach (var t in tr) { ms.WriteByte((byte)'l'); WriteStr(ms, t); ms.WriteByte((byte)'e'); }
                        ms.WriteByte((byte)'e');
                    }
                }
                WriteKey(ms, "info");
                ms.Write(infoRaw, 0, infoRaw.Length);
                ms.WriteByte((byte)'e');
                return ms.ToArray();
            }
        }

        private static void WriteKey(Stream s, string k) { WriteStr(s, k); }

        private static void WriteStr(Stream s, string v)
        {
            var b = Encoding.UTF8.GetBytes(v);
            var h = Encoding.ASCII.GetBytes(b.Length.ToString() + ":");
            s.Write(h, 0, h.Length);
            s.Write(b, 0, b.Length);
        }

        public void Dispose()
        {
            SaveHintsNow();
            if (_ownsHttp) _http.Dispose();
        }
    }
}
