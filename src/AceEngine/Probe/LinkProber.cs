using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Probe
{
    /// <summary>What the scheduler needs from a prober (the tests use a fake one).</summary>
    public interface ILinkProber
    {
        ProbeOptions Options { get; }
        Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct);
    }

    /// <summary>
    /// Health probe of a link, in phases:
    ///  1) ResolveAsync: content_id -> transport file (1 h cache) -> infohash and trackers (DHT and peers; this is what is parallelized to 50);
    ///  2) CountAsync: HTTP/UDP scrape of the trackers, GROUPED by tracker: the infohashes pending for each tracker travel together in a batch
    ///     (UDP ~70, HTTP up to 50) with a brake per host:port (TrackerScrapeService); the maximum of each counter is kept;
    ///  3) FallbackAsync: if there are no numbers, DHT (approximate). ProbeSwarmAsync also tries an announce with numwant=50 (only for a single link).
    /// A link is alive if seeds + peers &gt; 0 (on a live stream 'complete' may always be 0).
    /// </summary>
    public sealed class LinkProber : IPhasedProber, ILiveVerifier
    {
        private const string Tag = "probe";
        private readonly TransportResolver _resolver;
        private readonly DhtClient _dht;
        private readonly HttpClient _http;
        private readonly ProbeOptions _opt;
        private readonly TrackerScrapeService _scrape;
        private readonly SemaphoreSlim _fallbackGate;
        private readonly SemaphoreSlim _resolveGate;
        private int _resolving, _peakResolving;

        private readonly ConnectionBudget _budget;

        /// <param name="budget">Shared connection budget (resolving and probing the live stream ask for it); null = only the quotas of each phase.</param>
        public LinkProber(TransportResolver resolver, DhtClient dht, HttpClient http, ProbeOptions options = null, TrackerScrapeService scrape = null, ConnectionBudget budget = null)
        {
            _budget = budget;
            _resolver = resolver;
            _dht = dht;
            _http = http;
            _opt = options ?? new ProbeOptions();
            _scrape = scrape ?? new TrackerScrapeService(new NetScrapeTransport(http, _opt), new TrackerLimiter(_opt), _opt);
            _fallbackGate = new SemaphoreSlim(Math.Max(1, _opt.FallbackConcurrency));
            // Resolutions admitted at once: those that fit in the connection and DHT query budget, with 'Concurrency' as the ceiling
            int admitted = Math.Max(1, _opt.Concurrency);
            if (_opt.ResolveConnectionBudget > 0 && _opt.ResolveConnectionsPerLink > 0) admitted = Math.Min(admitted, Math.Max(1, _opt.ResolveConnectionBudget / _opt.ResolveConnectionsPerLink));
            if (_dht != null && _dht.MaxInFlight < int.MaxValue) admitted = Math.Min(admitted, Math.Max(1, _dht.MaxInFlight / DhtClient.SearchWidth));
            AdmittedResolutions = admitted;
            _resolveGate = new SemaphoreSlim(admitted);
        }

        /// <summary>content_id resolutions that run at once at most (the rest wait their turn without spending their deadline).</summary>
        public int AdmittedResolutions { get; private set; }
        /// <summary>Resolutions over the network running right now and the maximum seen.</summary>
        public int ResolvingNow { get { return Volatile.Read(ref _resolving); } }
        public int PeakResolving { get { return Volatile.Read(ref _peakResolving); } }

        public ProbeOptions Options { get { return _opt; } }

        /// <summary>The tracker counting service (with the brake and the statistics per tracker).</summary>
        public TrackerScrapeService Scrape { get { return _scrape; } }

        public async Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProbeResult res;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_opt.LinkTimeout);
                    try { res = await ProbeCoreAsync(entry, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { res = new ProbeResult { State = LinkState.Unknown, Error = "timed out" }; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                AceLog.Debug(Tag, entry.Name + ": " + ex.GetType().Name + " " + ex.Message);
                res = new ProbeResult { State = LinkState.Unknown, Error = ex.Message };
            }
            res.TimeUtc = DateTime.UtcNow;
            res.Elapsed = sw.Elapsed;
            return res;
        }

        private async Task<ProbeResult> ProbeCoreAsync(LinkEntry e, CancellationToken ct)
        {
            var step = await ResolveAsync(e, ct).ConfigureAwait(false);
            if (step.Final != null) return step.Final;
            var t = step.Target;
            var result = await ProbeSwarmAsync(t.InfoHash, t.Trackers, t.ContentId, ct).ConfigureAwait(false);
            result.Source = t.Source;
            result.ResolveElapsed = t.ResolveElapsed;
            result.Unconfirmed = t.Unconfirmed;
            return result;
        }

        // ---------- Phase 1: resolve ----------

        /// <summary>
        /// Phase 1: from the link to the infohash and the trackers. Returns a final result if there is no swarm to count (http, unsupported, unresolved)
        /// and, otherwise, the SwarmTarget. Unresolved content_ids stay Unknown with Unresolved = true.
        /// </summary>
        public async Task<ResolveStep> ResolveAsync(LinkEntry e, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            switch (e.Kind)
            {
                case LinkKind.HttpStream:
                    return Final(_opt.ProbeHttpLinks ? await ProbeHttpAsync(e.Url, ct).ConfigureAwait(false) : new ProbeResult { State = LinkState.Unsupported });
                case LinkKind.AceStream:
                case LinkKind.Magnet:
                    break;
                default:
                    return Final(new ProbeResult { State = LinkState.Unsupported });
            }

            byte[] infoHash;
            var trackers = new List<string>();
            string contentId = null;
            string source = null;
            ResolveStats stats = null;
            bool unconfirmed = false;
            int bitrateKbps = 0;
            var work = sw;                          // how long the resolution itself took (without the wait for a turn)
            var link = e.Link;
            if (link.Kind == AceLinkKind.Magnet && !_opt.ResolveInfoHashLinks)
            {
                infoHash = link.Magnet.InfoHash;
                trackers.AddRange(link.Magnet.Trackers);
            }
            else if (link.Kind == AceLinkKind.InfoHash && !_opt.ResolveInfoHashLinks)
            {
                infoHash = Hex.Parse(link.Value);
            }
            else
            {
                // content_id (and, with ResolveInfoHashLinks, infohash and magnet: the results of the Ace central search): the descriptor must be known.
                // An infohash that nobody serves over P2P cannot be opened either: without a descriptor there is no broadcaster key nor a way to connect to the live stream.
                ResolvedTransport rt = null;
                var profile = NewProfile();
                // 1) the local cache needs neither a slot nor a deadline
                try { rt = await _resolver.ResolveAsync(link, ct, true).ConfigureAwait(false); }
                catch (ResolveException) { }
                if (rt == null)
                {
                    // 2) over the network: it waits for its turn WITHOUT spending the deadline; the deadline starts once it has a connection and DHT query budget
                    await _resolveGate.WaitAsync(ct).ConfigureAwait(false);
                    int budgetUnits = 0;
                    if (_budget != null)
                    {
                        try { budgetUnits = await _budget.AcquireAsync(Math.Max(1, _opt.ResolveConnectionsPerLink), ConnectionBudget.Low, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { _resolveGate.Release(); throw; }
                    }
                    int now = Interlocked.Increment(ref _resolving);
                    int prev;
                    while (now > (prev = Volatile.Read(ref _peakResolving))) Interlocked.CompareExchange(ref _peakResolving, now, prev);
                    work = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            rcts.CancelAfter(_opt.ResolveTimeout);
                            try { rt = await _resolver.ResolveAsync(link, rcts.Token, false, _opt.ResolveViaTsService, profile).ConfigureAwait(false); }
                            catch (ResolveException ex) { return Unresolved(ex.Message, profile, work); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unresolved("the content_id was not resolved", profile, work); }
                        }
                    }
                    finally { Interlocked.Decrement(ref _resolving); if (budgetUnits > 0) _budget.Release(budgetUnits); _resolveGate.Release(); }
                }
                stats = (rt.Source == "local_db") ? null : profile.Stats;       // from the cache there is nothing to count
                unconfirmed = rt.Trust != null && rt.Trust.Level == TrustLevel.Unconfirmed;       // same rule as playback (OpenedStream.Trust)
                if (unconfirmed) AceLog.Debug(Tag, "[trust] unconfirmed " + link.Value + " (" + (rt.Trust.Note ?? "single peer") + ")");     // debugging only: the UI does not show it
                var tf = rt.Transport;
                source = rt.Source;
                contentId = link.Kind == AceLinkKind.ContentId ? link.Value : null;
                if (tf.IsMulti)
                {
                    var q = tf.Qualities.FirstOrDefault(x => x.Data != null);
                    if (q == null) return Final(new ProbeResult { State = LinkState.Unknown, Error = "multi-quality without interpretable qualities", Source = source, ResolveElapsed = sw.Elapsed, Unconfirmed = unconfirmed });
                    tf = q.Data;
                }
                if (tf.InfoHash == null)
                {
                    string manifest = tf.Root == null ? null : (tf.Root.GetString("manifest_url") ?? tf.Root.GetString("hidden_manifest_url"));
                    if (!string.IsNullOrEmpty(manifest) && tf.Type != "dash")
                    {
                        var h = await ProbeHttpAsync(manifest, ct).ConfigureAwait(false);
                        h.Source = source;
                        h.Unconfirmed = unconfirmed;
                        return Final(h);
                    }
                    return Final(new ProbeResult { State = LinkState.Unsupported, Error = "transport " + tf.Type + " without manifest_url", Source = source, Unconfirmed = unconfirmed });
                }
                infoHash = tf.InfoHash;
                if (tf.Live != null) { trackers.AddRange(tf.Live.Trackers); bitrateKbps = (int)Math.Min(125000, tf.Live.Bitrate * 8 / 1000); }   // Bitrate is in bytes/s (LiveSwarm: pieces/s = Bitrate / piece size)
                else if (tf.Torrent != null) foreach (var tier in tf.Torrent.GetTrackerTiers()) trackers.AddRange(tier);
                if (link.Kind == AceLinkKind.Magnet) trackers.AddRange(link.Magnet.Trackers);        // also, the trackers of the link itself
            }
            if (trackers.Count == 0 && (link.Kind != AceLinkKind.ContentId)) trackers.AddRange(_opt.DefaultTrackers);
            return new ResolveStep
            {
                Target = new SwarmTarget { InfoHash = infoHash, Trackers = trackers.Distinct().ToList(), ContentId = contentId, Link = link, Source = source, ResolveElapsed = work.Elapsed, Stats = stats, Unconfirmed = unconfirmed, BitrateKbps = bitrateKbps }
            };
        }

        private static ResolveStep Final(ProbeResult r) { return new ResolveStep { Final = r }; }

        /// <summary>Budget and deadlines of THIS resolution.</summary>
        private ResolveProfile NewProfile()
        {
            return new ResolveProfile
            {
                MaxConnections = _opt.ResolveConnectionsPerLink,
                ConnectTimeout = _opt.ResolveConnectTimeout,
                HandshakeTimeout = _opt.ResolveHandshakeTimeout
            };
        }

        private ResolveStep Unresolved(string error, ResolveProfile profile, System.Diagnostics.Stopwatch sw)
        {
            return Final(new ProbeResult
            {
                State = LinkState.Unknown, Error = error, Unresolved = true, ResolveElapsed = sw.Elapsed, ResolveStats = profile.Stats,
                Failure = ClassifyFailure(profile.Stats)
            });
        }

        /// <summary>
        /// "Nobody serves it" versus "lack of resources": if the resolution could not use its resources (system errors from too many sockets, or the DHT queries
        /// waited for a slot for a good part of the deadline) it did not conclude, and the link is retried instead of being considered not served.
        /// </summary>
        internal static ResolveFailure ClassifyFailure(ResolveStats st)
        {
            if (st == null) return ResolveFailure.NoServer;
            if (st.ResourceErrors > 0 || st.DhtWaitFraction > 0.25) return ResolveFailure.Starved;
            return ResolveFailure.NoServer;
        }

        // ---------- Phase 2: count by trackers (grouped) ----------

        /// <summary>One tracker per host:port (if there are udp and http of the same one, the udp), in random order and at most MaxTrackersPerLink.</summary>
        internal List<string> SelectTrackers(IEnumerable<string> trackers)
        {
            var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var t in trackers)
            {
                var key = TrackerKey.From(t);
                if (key == null) continue;
                string cur;
                if (!byKey.TryGetValue(key, out cur)) { byKey[key] = t; order.Add(key); }
                else if (!cur.StartsWith("udp:", StringComparison.OrdinalIgnoreCase) && t.StartsWith("udp:", StringComparison.OrdinalIgnoreCase)) byKey[key] = t;
            }
            var list = order.Select(k => byKey[k]).ToList();
            Rng.Shuffle(list);                                             // random order, like the original tracker checker
            if (list.Count > _opt.MaxTrackersPerLink) list = list.Take(_opt.MaxTrackersPerLink).ToList();
            return list;
        }

        /// <summary>
        /// A round of counting: asks each tracker for the infohash through the batch service; keeps the maximum of each counter and
        /// stops as soon as there are seeds (the infohashes that have not gone out in a batch yet are no longer sent). After CountTimeout what has been gathered is used.
        /// </summary>
        public async Task<ProbeResult> CountAsync(SwarmTarget target, CancellationToken ct)
        {
            var infoHash = target.InfoHash;
            var res = new ProbeResult { InfoHash = Hex.ToHex(infoHash), Rounds = 1, Source = target.Source, ResolveElapsed = target.ResolveElapsed, ResolveStats = target.Stats, Unconfirmed = target.Unconfirmed, BitrateKbps = target.BitrateKbps };
            var list = SelectTrackers(target.Trackers);
            int seeds = 0, peers = 0;
            bool anyOk = false, anyInvalid = false;
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (_opt.CountTimeout > TimeSpan.Zero) stop.CancelAfter(_opt.CountTimeout);
                var tasks = new List<Task>();
                foreach (var t in list)
                {
                    var tracker = t;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var r = await _scrape.ScrapeAsync(tracker, infoHash, stop.Token).ConfigureAwait(false);
                            if (r.Skipped) return;                              // it was never asked (waiting or benched)
                            lock (res)
                            {
                                res.Trackers.Add(r);
                                if (r.Status == ScrapeStatus.Ok) { anyOk = true; seeds = Math.Max(seeds, r.Seeders); peers = Math.Max(peers, r.Leechers); if (string.IsNullOrEmpty(res.Method)) res.Method = r.Tracker.StartsWith("udp", StringComparison.OrdinalIgnoreCase) ? "scrape-udp" : "scrape-http"; }
                                else if (r.Status == ScrapeStatus.Invalid) anyInvalid = true;
                            }
                            if (r.Status == ScrapeStatus.Ok && r.Seeders > 0) { try { stop.Cancel(); } catch { } }      // there are seeds: no need for more
                        }
                        catch (OperationCanceledException) { }
                    }));
                }
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            ct.ThrowIfCancellationRequested();
            lock (res)
            {
                res.Seeders = seeds; res.Peers = peers;
                res.State = ProbeResult.Classify(res.Seeders, res.Peers, anyInvalid && !anyOk);
                if (res.Method == null) res.Method = "scrape";
            }
            return res;
        }

        // ---------- Phase 4: live probe ----------

        /// <summary>Live prober (created by the engine with its DHT and its options). Null = the tracker/DHT count is all that is done.</summary>
        public LiveProber Live { get; set; }

        /// <summary>
        /// Connects to a few peers of the channel and checks whether they announce recent pieces and whether the head of the live stream advances (LiveProber). The descriptor comes from the resolution cache
        /// (it was just resolved) and the peers that served it or that already worked with the channel go first. The peers that answer well are remembered for the next open.
        /// </summary>
        public async Task<LiveProbeResult> VerifyLiveAsync(SwarmTarget target, CancellationToken ct)
        {
            var lp = Live;
            if (lp == null || target == null) return null;
            bool byContentId = target.ContentId != null && Hex.IsHex40(target.ContentId);
            bool byInfoHash = !byContentId && target.Link != null && (target.Link.Kind == AceLinkKind.Magnet || target.Link.Kind == AceLinkKind.InfoHash);
            if (!byContentId && !byInfoHash) return null;
            ResolvedTransport rt;
            try
            {
                // the descriptor was just resolved in phase 1: it comes from the resolution cache
                rt = byContentId ? await _resolver.ResolveContentIdAsync(target.ContentId, ct, true).ConfigureAwait(false) : await _resolver.ResolveAsync(target.Link, ct, true).ConfigureAwait(false);
            }
            catch (ResolveException) { return null; }
            var tf = rt.Transport;
            if (tf != null && tf.IsMulti) { var q = tf.Qualities.FirstOrDefault(x => x.Data != null); tf = q == null ? null : q.Data; }
            if (tf == null || tf.Live == null) return null;
            // The check deadline (CheckBudget) covers resolve + probe: the probe gets what the resolution did not spend (at least 3 s). Waiting for a turn does not count.
            var po = _opt.LiveProbe;
            if (_opt.CheckBudget > TimeSpan.Zero)
            {
                var left = _opt.CheckBudget - target.ResolveElapsed;
                if (left < TimeSpan.FromSeconds(3)) left = TimeSpan.FromSeconds(3);
                if (left < po.MaxTime) po = po.WithMaxTime(left);
            }
            int units = 0;
            if (_budget != null) units = await _budget.AcquireAsync(po.MaxConnections + 1, ConnectionBudget.High, ct).ConfigureAwait(false);       // +1: the http announce to a tracker
            LiveProbeResult res;
            try { res = await lp.ProbeAsync(tf.Live, rt.GoodPeers, target.ContentId ?? Hex.ToHex(tf.Live.InfoHash), po, ct).ConfigureAwait(false); }
            finally { if (units > 0) _budget.Release(units); }
            if (res.GoodPeers.Count > 0)
            {
                if (byContentId) _resolver.Hints.Add(target.ContentId, res.GoodPeers);
                _resolver.Hints.Add(Hex.ToHex(tf.Live.InfoHash), res.GoodPeers);
            }
            return res;
        }

        // ---------- Phase 3: fallback ----------

        /// <summary>DHT fallback: counts the distinct peers of get_peers for the infohash and the content_id. Approximate.</summary>
        public async Task<ProbeResult> FallbackAsync(SwarmTarget target, CancellationToken ct)
        {
            var res = new ProbeResult { InfoHash = Hex.ToHex(target.InfoHash), State = LinkState.Unknown, Method = "dht" };
            if (!_opt.UseDhtFallback || _dht == null) return res;
            await _fallbackGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int n = await DhtCountAsync(target.InfoHash, target.ContentId, ct).ConfigureAwait(false);
                if (n > 0) { res.Peers = n; res.Approximate = true; res.State = LinkState.Good; }
            }
            finally { _fallbackGate.Release(); }
            return res;
        }

        // ---------- Whole swarm: count + fallbacks (a single link) ----------

        /// <summary>Probes an infohash with its trackers (public for `ace probe` and the tests): one round and, if there are no numbers, announce and DHT.</summary>
        public async Task<ProbeResult> ProbeSwarmAsync(byte[] infoHash, IList<string> trackers, string contentId, CancellationToken ct)
        {
            var target = new SwarmTarget { InfoHash = infoHash, Trackers = trackers, ContentId = contentId };
            var res = await CountAsync(target, ct).ConfigureAwait(false);
            bool anyInvalid = res.State == LinkState.Dead;

            // fallback if the trackers give no numbers
            if (res.Seeders + res.Peers == 0)
            {
                int approx = 0;
                string method = null;
                if (_opt.UseAnnounceFallback)
                {
                    var list = trackers.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
                    int n = await AnnounceCountAsync(list, infoHash, ct).ConfigureAwait(false);
                    if (n > 0) { approx = n; method = "announce"; }
                }
                if (approx == 0 && _opt.UseDhtFallback && _dht != null)
                {
                    int n = await DhtCountAsync(infoHash, contentId, ct).ConfigureAwait(false);
                    if (n > 0) { approx = n; method = "dht"; }
                }
                if (approx > 0) { res.Peers = approx; res.Approximate = true; res.Method = method; res.State = LinkState.Good; }
                else res.State = ProbeResult.Classify(0, 0, anyInvalid);
            }
            return res;
        }

        private async Task<int> AnnounceCountAsync(IList<string> trackers, byte[] ih, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            var pid = PeerId.Generate("R30------");
            var sem = new SemaphoreSlim(Math.Max(1, _opt.TrackerParallelism));
            var tasks = new List<Task>();
            foreach (var t in trackers.Where(TrackerClient.IsSupported).Take(8))
            {
                var tracker = t;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var req = new AnnounceRequest { InfoHash = ih, PeerId = pid, Port = 6881, Left = 1, Event = null, NumWant = _opt.AnnounceNumWant };
                        var r = await TrackerClient.AnnounceAsync(_http, tracker, req, (uint)Rng.Next(1, int.MaxValue) & 0xffffff, ct, (int)_opt.HttpTimeout.TotalSeconds).ConfigureAwait(false);
                        if (!r.Failed) lock (seen) foreach (var p in r.Peers) seen.Add(p.ToString());
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AceLog.Debug(Tag, "announce " + tracker + ": " + ex.Message); }
                    finally { sem.Release(); }
                }));
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            lock (seen) return seen.Count;
        }

        private async Task<int> DhtCountAsync(byte[] ih, string contentId, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            var hashes = new List<byte[]> { ih };
            if (contentId != null && Hex.IsHex40(contentId)) hashes.Add(Hex.Parse(contentId));
            var tasks = hashes.Select(h => _dht.GetPeersAsync(h, ps => { lock (seen) foreach (var p in ps) seen.Add(p.Address.ToString()); }, _opt.DhtTimeout, ct)).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            lock (seen) return seen.Count;
        }

        // ---------- http stream links ----------

        private async Task<ProbeResult> ProbeHttpAsync(string url, CancellationToken ct)
        {
            var res = new ProbeResult { Method = "http" };
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    cts.CancelAfter(_opt.HttpLinkTimeout);
                    req.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) { res.State = LinkState.Dead; res.Error = "HTTP " + (int)resp.StatusCode; return res; }
                        string path = url.Split('?')[0];
                        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                        {
                            var buf = new byte[256];
                            using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                int n = await s.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                var head = System.Text.Encoding.UTF8.GetString(buf, 0, n).TrimStart('\uFEFF', ' ', '\r', '\n');
                                if (!head.StartsWith("#EXTM3U", StringComparison.Ordinal)) { res.State = LinkState.Dead; res.Error = "not an M3U list"; return res; }
                            }
                        }
                        res.State = LinkState.Good;
                        res.NoCounts = true;
                        return res;
                    }
                }
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; res.State = LinkState.Unknown; res.Error = "timed out"; return res; }
            catch (HttpRequestException ex) { res.State = LinkState.Unknown; res.Error = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return res; }
        }
    }
}
