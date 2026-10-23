using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>`ace list`, `ace m3u` and `ace probe`: link sources + probing of seeds and peers.</summary>
    internal static class ListCommands
    {
        private static string Row(ListRow r, bool withGroup)
        {
            var res = r.Result;
            string counts;
            switch (res.State)
            {
                case LinkState.Good: counts = res.NoCounts ? "http live   " : ((res.LiveVerified ? "A " + (res.Live.ActivePeers + (res.Live.Saturated ? "+" : "")).PadLeft(3) + " . " : "") + "S " + res.Seeders.ToString(CultureInfo.InvariantCulture).PadLeft(3) + " . P " + res.Peers.ToString(CultureInfo.InvariantCulture).PadLeft(4) + (res.Approximate ? " approx" : "       ")); break;
                case LinkState.Dead: counts = "dead        " + (res.Live != null ? "(" + Shorten(res.Error, 50) + ")" : ""); break;
                case LinkState.Unknown: counts = "unknown     " + (res.Error != null ? "(" + Shorten(res.Error, 50) + ")" : ""); break;
                case LinkState.Checking: counts = "checking    "; break;
                default: counts = res.State.ToString().PadRight(12); break;
            }
            return counts + "  " + r.Entry.Name + (withGroup && !string.IsNullOrEmpty(r.Entry.Group) ? "   [" + r.Entry.Group + "]" : "") + "  " + r.Entry.Url;
        }

        private static string Shorten(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "~"; }

        private static EngineOptions Configure(EngineOptions o, Func<string, string, string> get, Func<string, bool> has)
        {
            if (has("--dns")) { o.Dns.Servers = get("--dns", "8.8.8.8").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); }
            if (has("--no-doh")) o.Dns.UseDohFallback = false;
            if (has("--doh-only")) { o.Dns.Servers = new List<string>(); }
            var inv = CultureInfo.InvariantCulture;
            if (has("--concurrency")) o.Probe.Concurrency = Math.Max(1, int.Parse(get("--concurrency", "50"), inv));
            else if (has("--probe-concurrency")) o.Probe.Concurrency = Math.Max(1, int.Parse(get("--probe-concurrency", "50"), inv));
            if (has("--tracker-interval")) o.Probe.TrackerInterval = TimeSpan.FromSeconds(Math.Max(0, double.Parse(get("--tracker-interval", "2"), inv)));
            if (has("--tracker-concurrent")) o.Probe.TrackerMaxConcurrent = Math.Max(1, Math.Min(2, int.Parse(get("--tracker-concurrent", "1"), inv)));
            if (has("--recheck")) o.Probe.RecheckDelay = TimeSpan.FromSeconds(Math.Max(0, double.Parse(get("--recheck", "10"), inv)));
            if (has("--udp-batch")) o.Probe.UdpBatchSize = Math.Max(1, Math.Min(UdpScrape.MaxHashesPerPacket, int.Parse(get("--udp-batch", "70"), inv)));
            if (has("--http-batch")) o.Probe.HttpBatchSize = Math.Max(1, int.Parse(get("--http-batch", "50"), inv));
            if (has("--dht-inflight")) o.DhtMaxInFlight = Math.Max(1, int.Parse(get("--dht-inflight", "128"), inv));
            if (has("--resolve-conns")) o.Probe.ResolveConnectionsPerLink = Math.Max(1, int.Parse(get("--resolve-conns", "15"), inv));
            if (has("--resolve-budget")) o.Probe.ResolveConnectionBudget = Math.Max(0, int.Parse(get("--resolve-budget", "750"), inv));
            if (has("--connect-timeout")) o.Probe.ResolveConnectTimeout = TimeSpan.FromSeconds(Math.Max(0, double.Parse(get("--connect-timeout", "0"), inv)));
            if (has("--handshake-timeout")) o.Probe.ResolveHandshakeTimeout = TimeSpan.FromSeconds(Math.Max(0, double.Parse(get("--handshake-timeout", "0"), inv)));
            if (has("--recheck-unresolved")) o.Probe.RecheckUnresolved = true;
            if (has("--ts-service")) o.Probe.ResolveViaTsService = true;
            if (has("--no-verify-live")) o.Probe.VerifyLive = false;
            if (has("--live-verify-concurrency")) o.Probe.LiveVerifyConcurrency = Math.Max(1, int.Parse(get("--live-verify-concurrency", "8"), inv));
            if (has("--live-target")) o.Probe.LiveProbe.TargetPeers = Math.Max(1, int.Parse(get("--live-target", "4"), inv));
            if (has("--live-conns")) o.Probe.LiveProbe.MaxConnections = Math.Max(1, int.Parse(get("--live-conns", "12"), inv));
            if (has("--live-max-seconds")) o.Probe.LiveProbe.MaxTime = TimeSpan.FromSeconds(Math.Max(2, double.Parse(get("--live-max-seconds", "12"), inv)));
            if (has("--no-announce-fallback")) o.Probe.UseAnnounceFallback = false;
            if (has("--no-dht-fallback")) o.Probe.UseDhtFallback = false;
            return o;
        }

        /// <summary>Probes the entries and prints them sorted. Returns 0 if there was at least one live link.</summary>
        public static async Task<int> ProbeAndPrintAsync(List<LinkEntry> entries, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            Configure(opt, get, has);
            int max = int.Parse(get("--max", "0"));
            int seconds = int.Parse(get("--seconds", "300"));
            bool showAll = has("--all"), byGroup = !has("--no-group");
            var probeable = entries.Where(e => e.IsProbeable).ToList();
            Console.WriteLine(entries.Count + " items; " + probeable.Count + " probeable ("
                + probeable.Count(e => e.Kind == LinkKind.AceStream) + " acestream, " + probeable.Count(e => e.Kind == LinkKind.Magnet) + " magnet, " + probeable.Count(e => e.Kind == LinkKind.HttpStream) + " http); "
                + entries.Count(e => e.Kind == LinkKind.Web) + " web pages and " + entries.Count(e => e.Kind == LinkKind.Label) + " labels are not probed");
            int skip = int.Parse(get("--skip", "0"));
            var toProbe = probeable.Skip(skip).ToList();
            if (max > 0) toProbe = toProbe.Take(max).ToList();
            if (toProbe.Count < probeable.Count) Console.WriteLine("(only " + toProbe.Count + " links are probed, --skip " + skip + " --max " + max + ")");

            DhtClient dht = null;
            if (opt.EnableDht) { dht = new DhtClient(opt); dht.Start(); }
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            using (var resolver = new TransportResolver(opt, dht, http))
            {
                var prober = new LinkProber(resolver, dht, http, opt.Probe);
                if (opt.Probe.VerifyLive) prober.Live = new LiveProber(opt, dht, null, http);
                using (var sched = new ProbeScheduler(prober, opt.Probe))
                using (var sampler = has("--stats") ? new LoadSampler(dht, resolver, TimeSpan.FromSeconds(2)) : null)
                {
                    int done = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    sched.ResultChanged += (e, r) =>
                    {
                        if (r.State == LinkState.Checking) return;
                        int n = Interlocked.Increment(ref done);
                        if (has("--progress")) Console.WriteLine("  [" + n + "/" + toProbe.Count + " " + sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s] " + r + "  " + e.Name);
                    };
                    sched.Start(toProbe);
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
                        try { await sched.WaitFirstCycleAsync(cts.Token); }
                        catch (OperationCanceledException) { Console.WriteLine("(probe time exhausted: " + seconds + " s; showing what was obtained)"); }
                    }
                    sched.Stop();                                   // on leaving the listing everything is cancelled
                    var rows = sched.Snapshot(showAll, byGroup);
                    Console.WriteLine("---- " + sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s; maximum observed concurrency " + sched.MaxObservedConcurrency + " ----");
                    var good = rows.Where(r => r.Result.State == LinkState.Good).ToList();
                    string lastGroup = null;
                    foreach (var r in rows)
                    {
                        if (byGroup && (r.Entry.Group ?? "") != lastGroup) { lastGroup = r.Entry.Group ?? ""; Console.WriteLine("== " + (lastGroup.Length == 0 ? "(no group)" : lastGroup)); }
                        Console.WriteLine(Row(r, !byGroup));
                    }
                    var all = toProbe.Select(e => sched.GetResult(e)).ToList();
                    Console.WriteLine("---- live " + all.Count(r => r.State == LinkState.Good) + ", dead " + all.Count(r => r.State == LinkState.Dead) + ", unknown " + all.Count(r => r.State == LinkState.Unknown)
                        + ", unchecked " + all.Count(r => r.State == LinkState.Pending || r.State == LinkState.Checking) + " ----");
                    if (has("--stats"))
                    {
                        if (sampler != null) sampler.Dispose();
                        if (get("--stats-csv", null) != null) StatsReport.WriteCsv(get("--stats-csv", null), sched, toProbe);
                        StatsReport.Print(sched, prober, dht, resolver, sampler, toProbe.GroupBy(e => e.Key).Select(g => g.First()).ToList(), sw.Elapsed.TotalSeconds, opt);
                    }
                    if (dht != null) dht.Dispose();
                    return good.Count > 0 ? 0 : 1;
                }
            }
        }

        public static async Task<int> ListAsync(string code, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            Configure(opt, get, has);
            Console.WriteLine("Querying " + code + "." + MyLinksPasteClient.DefaultSuffix + " ...");
            var dns = new DnsTxtClient(opt.Dns);
            var client = new MyLinksPasteClient(dns, get("--suffix", MyLinksPasteClient.DefaultSuffix));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LinkTree tree;
            try { tree = await client.FetchAsync(code, ct); }
            catch (DnsException ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
            Console.WriteLine(tree.Visited.Count + " codes queried in " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s; " + tree.Entries.Count + " items");
            foreach (var er in tree.Errors) Console.WriteLine("  warning: " + er);
            var entries = LinkList.Dedupe(tree.Entries);
            if (entries.Count != tree.Entries.Count) Console.WriteLine(tree.Entries.Count - entries.Count + " duplicates removed");
            if (has("--no-probe"))
            {
                string g = null;
                foreach (var e in entries) { if ((e.Group ?? "") != g) { g = e.Group ?? ""; Console.WriteLine("== " + g); } Console.WriteLine("  " + e.Kind.ToString().PadRight(10) + e.Name + "  " + e.Url); }
                return 0;
            }
            return await ProbeAndPrintAsync(entries, opt, get, has, ct);
        }

        public static async Task<int> M3uAsync(string source, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            Configure(opt, get, has);
            List<LinkEntry> entries;
            try { entries = await M3uParser.LoadAsync(source, null, ct); }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is UnauthorizedAccessException) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
            Console.WriteLine("M3U list " + source + ": " + entries.Count + " entries");
            entries = LinkList.Dedupe(entries);
            Console.WriteLine(entries.Count + " after removing duplicates");
            if (has("--no-probe"))
            {
                foreach (var e in entries) Console.WriteLine("  " + e.Kind.ToString().PadRight(10) + e.Name + "  [" + e.Group + "]  " + e.Url);
                return 0;
            }
            return await ProbeAndPrintAsync(entries, opt, get, has, ct);
        }

        /// <summary>`ace scrape`: ONE scrape request with several infohashes to a tracker (the given ones and N random ones), to check that batches work.</summary>
        public static async Task<int> ScrapeAsync(string tracker, List<string> hashes, int random, CancellationToken ct)
        {
            var list = new List<byte[]>();
            foreach (var h in hashes) { if (!Hex.IsHex40(h)) { Console.Error.WriteLine("invalid infohash: " + h); return 2; } list.Add(Hex.Parse(h)); }
            for (int i = 0; i < random; i++) { var b = new byte[20]; new Random().NextBytes(b); list.Add(b); }
            if (list.Count == 0) { Console.Error.WriteLine("at least one infohash is needed (or --random N)"); return 2; }
            var o = new ProbeOptions();
            using (var http = new HttpClient())
            {
                var transport = new NetScrapeTransport(http, o);
                int fit = transport.FitCount(tracker, list);
                if (fit < list.Count) { Console.WriteLine("(" + fit + " of " + list.Count + " fit in one request; " + fit + " are sent)"); list = list.Take(fit).ToList(); }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = await transport.ScrapeAsync(tracker, list, ct);
                Console.WriteLine("tracker " + tracker + ": one request with " + list.Count + " infohashes -> " + res.Count + " results in " + sw.ElapsedMilliseconds + " ms; UDP packets " + res[0].Packets + (res[0].HttpStatus > 0 ? "; HTTP " + res[0].HttpStatus : ""));
                int ok = 0;
                for (int i = 0; i < res.Count; i++)
                {
                    if (res[i].Status == ScrapeStatus.Ok) ok++;
                    if (res[i].Status == ScrapeStatus.Ok && res[i].Seeders + res[i].Leechers > 0 || i < 3) Console.WriteLine("  " + Hex.ToHex(list[i]) + "  " + res[i]);
                }
                Console.WriteLine(ok + " with an Ok response out of " + res.Count + "; " + res.Count(r => r.Status == ScrapeStatus.Ok && r.Seeders + r.Leechers > 0) + " with someone");
                return ok > 0 ? 0 : 1;
            }
        }

        public static async Task<int> ProbeAsync(string linkText, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            Configure(opt, get, has);
            var entry = LinkEntry.FromUrl(get("--name", linkText), linkText);
            if (entry.Kind == LinkKind.Unsupported || entry.Kind == LinkKind.Label || entry.Kind == LinkKind.Web)
            {
                // a bare id is a content_id
                AceLink l; string err;
                if (!AceLink.TryParse(linkText, has("--infohash") ? AceLinkKind.InfoHash : AceLinkKind.ContentId, out l, out err)) { Console.Error.WriteLine("link cannot be probed: " + linkText); return 2; }
                entry = LinkEntry.FromUrl(linkText, linkText);
            }
            if (has("--infohash") && entry.Link != null && entry.Link.Kind == AceLinkKind.ContentId) entry = LinkEntry.FromUrl(linkText, "acestream:?infohash=" + entry.Link.Value);
            Console.WriteLine("Link: " + entry.Kind + " " + (entry.Key ?? entry.Url));
            DhtClient dht = null;
            if (opt.EnableDht) { dht = new DhtClient(opt); dht.Start(); }
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            using (var resolver = new TransportResolver(opt, dht, http))
            {
                var prober = new LinkProber(resolver, dht, http, opt.Probe);
                if (opt.Probe.VerifyLive) prober.Live = new LiveProber(opt, dht, null, http);
                var res = await prober.ProbeAsync(entry, ct);
                if (res.State == LinkState.Good && prober.Live != null && res.InfoHash != null && entry.Link != null && entry.Link.Kind == AceLinkKind.ContentId)
                {
                    var lp = await prober.VerifyLiveAsync(new SwarmTarget { InfoHash = Hex.Parse(res.InfoHash), ContentId = entry.Link.Value }, ct);
                    if (lp != null) res.Live = lp;
                }
                Console.WriteLine("Result: " + res + (res.Error != null ? "  (" + res.Error + ")" : ""));
                if (res.Live != null) Console.WriteLine("Live probe: " + res.Live + (res.Live.Detail != null ? " [" + res.Live.Detail + "]" : "") + "  estimated connectable: " + res.Live.EstimatedConnectable);
                if (res.InfoHash != null) Console.WriteLine("infohash:  " + res.InfoHash + (res.Source != null ? "  (transport: " + res.Source + ")" : ""));
                foreach (var t in res.Trackers) Console.WriteLine("  " + t + (t.Elapsed.TotalMilliseconds > 0 ? "  " + t.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms" : ""));
                Console.WriteLine("Score (seeds + peers): " + res.Score + (res.Approximate ? " (approximate)" : "") + "; " + res.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                if (dht != null) dht.Dispose();
                return res.State == LinkState.Good ? 0 : 1;
            }
        }
    }
}
