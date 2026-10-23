using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm
    {
        private readonly HashSet<string> _trackerLoops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _announced = new List<string>();

        private int AnnouncePort
        {
            get
            {
                int p = _listenPort();
                return p > 0 ? p : 6881;       // without a listener a fixed port is announced (nothing is listening)
            }
        }

        private long LeftBytes
        {
            get { lock (_sync) return Math.Max(1, _total); }
        }

        /// <summary>Launches an announce loop for each known tracker that does not have one yet.</summary>
        private void StartTrackers()
        {
            List<string> urls;
            lock (_sync) urls = _extraTrackers.ToList();
            foreach (var u in urls)
            {
                lock (_sync) { if (_disposed || !_trackerLoops.Add(u)) continue; }
                var url = u;
                Task.Run(() => TrackerLoopAsync(url));
            }
        }

        private async Task<AnnounceResult> AnnounceOnceAsync(string url, string ev, uint key, CancellationToken ct)
        {
            var req = new AnnounceRequest
            {
                InfoHash = _infoHash, PeerId = _peerId, Port = AnnouncePort,
                Uploaded = _upMeter.Total, Downloaded = _downMeter.Total, Left = LeftBytes, Event = ev, NumWant = 200
            };
            return await TrackerClient.AnnounceAsync(_http, url, req, key, ct).ConfigureAwait(false);
        }

        private async Task TrackerLoopAsync(string url)
        {
            var ct = _cts.Token;
            uint key = (uint)Rng.Next(1, int.MaxValue) & 0xffffff;
            string ev = "started";
            int failures = 0;
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "udp")
            {
                AceLog.Info(Tag, "unsupported tracker (" + uri.Scheme + "): " + url);
                return;
            }
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
                    if (PeerCount < 5) secs = Math.Min(secs, 120);
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

        private void AnnounceStopped()
        {
            List<string> urls;
            lock (_sync) urls = _announced.ToList();
            foreach (var u in urls)
            {
                var url = u;
                Task.Run(async () =>
                {
                    try
                    {
                        using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                            await AnnounceOnceAsync(url, "stopped", 0, c.Token).ConfigureAwait(false);
                    }
                    catch { }
                });
            }
        }

        private async Task DhtLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TorrentMetaNodes();
                    int n = await _dht.GetPeersAsync(_infoHash, peers => AddCandidates(peers, "dht"), TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                    AceLog.Info(Tag, "DHT: " + n + " peers in this search (known nodes " + _dht.KnownNodes + ")");
                    await Task.Delay(PeerCount < 5 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Warn(Tag, "DHT loop: " + ex.Message); }
        }

        private bool _nodesAdded;

        private void TorrentMetaNodes()
        {
            Transport.TorrentMeta m;
            lock (_sync) { m = _meta; if (m == null || _nodesAdded) return; _nodesAdded = true; }
            foreach (var kv in m.GetDhtNodes()) { var k = kv; Task.Run(() => _dht.AddNodeAsync(k.Key, k.Value)); }
        }
    }
}
