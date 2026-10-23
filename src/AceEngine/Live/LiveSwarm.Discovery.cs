using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Live
{
    internal sealed partial class LiveSwarm
    {
        private readonly List<string> _announced = new List<string>();
        private readonly HashSet<string> _trackerLoops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int AnnouncePort
        {
            get { int p = _listenPort(); return p > 0 ? p : 6881; }
        }

        /// <summary>Known initial peers (e.g. those that served the descriptor): they are tried first.</summary>
        public void AddInitialPeers(IEnumerable<IPEndPoint> peers)
        {
            AddCandidates(peers, "resolution", true);
        }

        private void StartDiscovery()
        {
            // startup_nodes of the descriptor: normal peers for the client, with priority
            if (_lo.UseStartupNodes) AddCandidates(ParseNodes(_desc.StartupNodes), "startup_node", true);
            if (_opt.StaticPeers.Count > 0) AddCandidates(ParseNodes(_opt.StaticPeers), "StaticPeers", true);

            if (_opt.EnableTrackers && _lo.UseDescriptorTrackers)
            {
                foreach (var t in _desc.Trackers.Distinct()) StartTracker(t);
            }
            if (_lo.UseMetaTrackers && _opt.EnableTrackers)
                foreach (var mt in _desc.MetaTrackers.Distinct()) { var url = mt; Task.Run(() => MetaTrackerLoopAsync(url)); }
            if (_opt.EnableDht && _dht != null && !_desc.IsPrivate) Task.Run(() => DhtLoopAsync());
        }

        private static List<IPEndPoint> ParseNodes(IEnumerable<string> nodes)
        {
            var res = new List<IPEndPoint>();
            foreach (var s in nodes)
            {
                string host; int port; IPAddress ip;
                if (AceEngine.P2P.Net.TryParseHostPort(s, out host, out port) && IPAddress.TryParse(host, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    res.Add(new IPEndPoint(ip, port));
            }
            return res;
        }

        // ---------- Trackers ----------

        private void StartTracker(string url)
        {
            if (!TrackerClient.IsSupported(url)) { AceLog.Debug(Tag, "unsupported tracker: " + url); return; }
            lock (_sync) { if (_disposed || !_trackerLoops.Add(url)) return; }
            Task.Run(() => TrackerLoopAsync(url));
        }

        private async Task<AnnounceResult> AnnounceOnceAsync(string url, string ev, uint key, CancellationToken ct)
        {
            var req = new AnnounceRequest
            {
                InfoHash = _infoHash, PeerId = _peerId, Port = AnnouncePort,
                Uploaded = _upMeter.Total, Downloaded = _downMeter.Total, Left = 1, Event = ev, NumWant = 200
            };
            return await TrackerClient.AnnounceAsync(_http, url, req, key, ct).ConfigureAwait(false);
        }

        private async Task TrackerLoopAsync(string url)
        {
            var ct = _cts.Token;
            uint key = (uint)Rng.Next(1, int.MaxValue) & 0xffffff;
            string ev = "started";
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                AnnounceResult res;
                try { res = await AnnounceOnceAsync(url, ev, key, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { res = new AnnounceResult { Tracker = url, FailureReason = ex.GetType().Name + ": " + ex.Message }; }
                TimeSpan delay;
                if (!res.Failed)
                {
                    failures = 0;
                    lock (_sync) { if (ev == "started" && !_announced.Contains(url)) _announced.Add(url); }
                    AceLog.Info(Tag, "tracker " + url + ": " + res.Peers.Count + " peers (S=" + res.Seeders + " L=" + res.Leechers + ", interval " + res.Interval + " s)");
                    AddCandidates(res.Peers, "tracker");
                    ev = null;
                    int secs = Math.Max(60, Math.Min(res.Interval, 600));
                    int peers; lock (_sync) peers = _peers.Count;
                    if (peers < 5) secs = Math.Min(secs, 60);
                    delay = TimeSpan.FromSeconds(secs);
                }
                else
                {
                    failures++;
                    AceLog.Info(Tag, "tracker " + url + " failed: " + res.FailureReason);
                    delay = TimeSpan.FromSeconds(Math.Min(300, 20 * Math.Pow(2, Math.Min(failures, 4))));
                }
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ---------- Meta-trackers ----------

        private async Task MetaTrackerLoopAsync(string baseUrl)
        {
            var ct = _cts.Token;
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.FromSeconds(3600);
                try
                {
                    string url = baseUrl + (baseUrl.IndexOf('?') >= 0 ? "&" : "?") + "infohash=" + Hex.ToHex(_infoHash) + "&cv=" + _lo.EngineVersion.ToString(CultureInfo.InvariantCulture);
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        using (var resp = await _http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            var body = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.JsonBody, cts.Token).ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode) throw new System.Net.Http.HttpRequestException("HTTP " + (int)resp.StatusCode);
                            var o = MiniJson.AsObject(MiniJson.Parse(body));
                            var trackers = MiniJson.GetArray(o, "trackers");
                            var nodes = MiniJson.GetArray(o, "startup_nodes");
                            int nt = 0;
                            if (trackers != null) foreach (var t in trackers) { var s = t as string; if (s != null) { StartTracker(s); nt++; } }
                            if (nodes != null)
                            {
                                var eps = ParseNodes(nodes.OfType<string>());
                                AddCandidates(eps, "meta_tracker", true);
                            }
                            long? interval = MiniJson.GetLong(o, "interval");
                            if (interval.HasValue && interval.Value > 30) delay = TimeSpan.FromSeconds(Math.Min(interval.Value, 7200));
                            AceLog.Info(Tag, "meta-tracker " + baseUrl + ": " + nt + " trackers, " + (nodes == null ? 0 : nodes.Count) + " startup_nodes");
                            failures = 0;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    failures++;
                    AceLog.Info(Tag, "meta-tracker " + baseUrl + ": " + ex.Message);
                    delay = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(failures, 4))));
                }
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ---------- DHT: by infohash and by content_id (alias) ----------

        private async Task DhtLoopAsync()
        {
            var ct = _cts.Token;
            var hashes = new List<byte[]> { _infoHash };
            if (_lo.DhtByContentId && _requestedContentId != null && Hex.IsHex40(_requestedContentId)) hashes.Add(Hex.Parse(_requestedContentId));
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var h in hashes)
                    {
                        string name = Hex.ToHex(h).Substring(0, 8);
                        string label = Bytes.Equal(h, _infoHash) ? "dht:ih" : "dht:cid";        // by infohash or by content_id: the second gives peers that know the channel but not always its swarm
                        int n = await _dht.GetPeersAsync(h, ps => AddCandidates(ps, label), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                        AceLog.Info(Tag, "DHT " + name + " (" + label + "): " + n + " peers (nodes " + _dht.KnownNodes + ")");
                    }
                    int peers; lock (_sync) peers = _peers.Count;
                    await Task.Delay(peers < 8 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Warn(Tag, "DHT loop: " + ex.Message); }
        }
    }
}
