using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace alive`: measures the real accuracy of "the app shows it as live -> it plays". It uses the same path as the app: the
    /// engine with the TV's profile (EngineOptionsFactory), the real LiveChecker over the same Channels of the lists or of the central
    /// search results, and to open a channel host.OpenStreamAsync + the local server, like PlaybackSession. It is not part of the product.
    ///
    /// Phases: 1) check the whole source (what the app would see); 2) the channels that came out live are checked again in small batches
    /// (fresh check) and opened right away: "age 0"; 3) a control of those that did not come out live; 4) optional (--ages): the same channels
    /// are opened again 5 and 15 minutes after a fresh check. The limit of simultaneous TCP connections is measured in the process itself (TcpGauge).
    /// </summary>
    internal static class Alive
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------- TCP sockets of the process (Linux) ----------

        internal sealed class TcpGauge : IDisposable
        {
            private readonly Timer _timer;
            private int _peak, _now, _peakEstablished;
            public TcpGauge() { _timer = new Timer(_ => Sample(), null, 500, 500); }
            public int Peak { get { return Volatile.Read(ref _peak); } }
            public int Now { get { return Volatile.Read(ref _now); } }
            public int PeakEstablished { get { return Volatile.Read(ref _peakEstablished); } }
            public int ResetPeak() { return Interlocked.Exchange(ref _peak, 0); }

            private void Sample()
            {
                try
                {
                    var inodes = new HashSet<string>();
                    foreach (var f in Directory.GetFiles("/proc/self/fd"))
                    {
                        string t = null;
                        try { t = new FileInfo(f).LinkTarget; } catch (Exception) { }
                        if (t != null && t.StartsWith("socket:[", StringComparison.Ordinal)) inodes.Add(t.Substring(8, t.Length - 9));
                    }
                    int open = 0, est = 0;
                    foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
                    {
                        if (!File.Exists(file)) continue;
                        bool first = true;
                        foreach (var line in File.ReadLines(file))
                        {
                            if (first) { first = false; continue; }
                            var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length < 10 || !inodes.Contains(p[9])) continue;
                            string st = p[3];
                            if (st != "01" && st != "02") continue;                                                        // ESTABLISHED or SYN_SENT
                            string rem = p[2];
                            if (rem.StartsWith("0100007F", StringComparison.Ordinal) || rem.StartsWith("00000000000000000000000001000000", StringComparison.Ordinal)) continue;      // loopback
                            open++; if (st == "01") est++;
                        }
                    }
                    Volatile.Write(ref _now, open);
                    int prev;
                    while (open > (prev = Volatile.Read(ref _peak))) Interlocked.CompareExchange(ref _peak, open, prev);
                    while (est > (prev = Volatile.Read(ref _peakEstablished))) Interlocked.CompareExchange(ref _peakEstablished, est, prev);
                }
                catch (Exception) { }
            }
            public void Dispose() { _timer.Dispose(); }
        }

        // ---------- data ----------

        private sealed class Item
        {
            public Channel Ch;
            public string Source = "", Query = "";
            public ChannelStatus Full = new ChannelStatus();
            public double FullAt = -1;
            /// <summary>With --compare-legacy: what the app would show WITHOUT the reliable-live changes (same network, same minutes).</summary>
            public ChannelStatus Legacy;
            public string Kind { get { return Ch.Entry != null && Ch.Entry.Kind == LinkKind.Magnet ? "magnet" : Ch.Entry != null && Ch.Entry.Kind == LinkKind.AceStream ? "cid" : "other"; } }
        }

        private sealed class EmptyCatalog : IChannelCatalog
        {
            public CatalogState State { get { return CatalogState.Ready; } }
            public string Error { get { return null; } }
            public IReadOnlyList<Category> Categories { get { return new Category[0]; } }
            public IReadOnlyList<Channel> Channels { get { return new Channel[0]; } }
            public Channel Find(string id) { return null; }
            public void Start() { }
            public event Action Changed { add { } remove { } }
        }

        private sealed class PlayResult
        {
            public bool Opened; public string OpenError = "";
            public double OpenSeconds = -1, FirstByte = -1, Seconds, PostSeconds;
            public long Bytes; public int SecondsWithData;
            public long TsPackets, TsSync;
            public double BitrateKbps;
            public string Kind = "";
            public int PeersEnd, Candidates, Tried, TcpOk, HandshakeOk, ExtendOk, RecentHave, Unchoked, Served;
            public string Class = "";
        }

        private static readonly object CsvGate = new object();

        private static string Q(string s) { return s == null ? "" : s.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' '); }
        private static string F(double v) { return v < 0 ? "" : v.ToString("0.0", Inv); }

        private static void Append(string path, string header, string line)
        {
            lock (CsvGate)
            {
                bool hdr = !File.Exists(path);
                File.AppendAllText(path, (hdr ? header + "\n" : "") + line + "\n");
            }
        }

        // ---------- entry point ----------

        public static async Task<int> RunAsync(Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            string label = get("--label", "run");
            string source = get("--source", "lists");
            string outPrefix = get("--out", "logs/alive/" + label);
            string dataDir = get("--data", Path.Combine(Path.GetTempPath(), "alive-data"));
            int playSeconds = int.Parse(get("--seconds", "40"), Inv);
            int workers = int.Parse(get("--workers", "1"), Inv);
            int chunk = int.Parse(get("--chunk", "12"), Inv);
            int sample = int.Parse(get("--sample", "0"), Inv);
            int controlN = int.Parse(get("--control", "12"), Inv);
            double checkTimeout = double.Parse(get("--check-timeout", "1500"), Inv);
            int seed = int.Parse(get("--seed", "1"), Inv);
            var rnd = new Random(seed);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPrefix)));
            Directory.CreateDirectory(dataDir);

            var cfg = new AppConfig();
            var settings = new UserSettings();
            var opt = EngineOptionsFactory.Build(cfg, settings, dataDir);
            // The checker's connection budget is the app's (EngineOptionsFactory: 56 TCP); here they can only be changed by hand
            if (has("--tcp-budget")) opt.Probe.TcpBudget = int.Parse(get("--tcp-budget", "52"), Inv);
            if (has("--resolve-conc")) opt.Probe.Concurrency = int.Parse(get("--resolve-conc", "12"), Inv);
            if (has("--verify-conc")) opt.Probe.LiveVerifyConcurrency = int.Parse(get("--verify-conc", "10"), Inv);
            if (has("--verify-conns")) opt.Probe.LiveProbe.MaxConnections = int.Parse(get("--verify-conns", "6"), Inv);
            if (has("--check-budget")) opt.Probe.CheckBudget = TimeSpan.FromSeconds(double.Parse(get("--check-budget", "15"), Inv));
            if (has("--pending")) opt.Live.MaxPendingConnects = int.Parse(get("--pending", "48"), Inv);
            if (has("--no-listen")) opt.EnableListener = false;
            ApplyTweaks(opt, get, has);

            Console.WriteLine("alive '" + label + "': source " + source + ", " + workers + " worker(s), window " + playSeconds + " s, data in " + dataDir);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var gauge = new TcpGauge())
            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var swWarm = System.Diagnostics.Stopwatch.StartNew();
                while (host.DhtNodes < 120 && swWarm.Elapsed < TimeSpan.FromSeconds(25))
                {
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                    if (swWarm.Elapsed.TotalSeconds > 3 && host.DhtNodes < 30)
                    {
                        var rn = new byte[20]; rnd.NextBytes(rn);
                        var dht = host.DhtClientForDiagnostics;
                        if (dht != null) { var t = dht.GetPeersAsync(rn, ps => { }, TimeSpan.FromSeconds(4), ct); }
                    }
                }
                Console.WriteLine("DHT warmed up: " + host.DhtNodes + " nodes in " + swWarm.Elapsed.TotalSeconds.ToString("0.0", Inv) + " s");

                // ---- 0) the sources ----
                var items = new List<Item>();
                if (source == "lists" || source == "both") items.AddRange(await LoadListsAsync(get("--lists", ""), ct));
                if (source == "search" || source == "both") items.AddRange(await LoadSearchAsync(get("--queries", "explore:futbol,explore:cine,explore:series,explore:deportes,dazn,M+,movistar"), ct));
                Console.WriteLine("channels: " + items.Count + " (" + items.Count(i => i.Kind == "cid") + " acestream, " + items.Count(i => i.Kind == "magnet") + " magnet)");
                if (has("--max")) items = items.OrderBy(i => rnd.Next()).Take(int.Parse(get("--max", "0"), Inv)).ToList();

                // ---- 0b) a list open for a while: first pass and retries in a loop (--loop SECONDS) ----
                if (has("--loop")) return await LoopModeAsync(host, items, int.Parse(get("--loop", "180"), Inv), has("--loop-play") ? int.Parse(get("--loop-play", "6"), Inv) : 0, playSeconds, outPrefix, label, clock, gauge, ct).ConfigureAwait(false);

                // ---- 1) the app's check ----
                string checkCsv = outPrefix + "_check.csv";
                if (File.Exists(checkCsv)) File.Delete(checkCsv);
                bool compare = has("--compare-legacy");
                if (compare)
                {
                    // first what the old app would show (only for comparison; the network is the same)
                    bool r0 = opt.Probe.ResolveInfoHashLinks;
                    opt.Probe.ResolveInfoHashLinks = false;
                    gauge.ResetPeak();
                    double tl = clock.Elapsed.TotalSeconds;
                    await CheckAllAsync(host, items, (it, st, at) => { it.Legacy = st; }, checkTimeout, clock, gauge, ct).ConfigureAwait(false);
                    opt.Probe.ResolveInfoHashLinks = r0;
                    Console.WriteLine("old check: " + (clock.Elapsed.TotalSeconds - tl).ToString("0", Inv) + " s; live " + items.Count(i => i.Legacy != null && i.Legacy.State == AceEngine.Probe.LinkState.Good) + "/" + items.Count);
                }
                gauge.ResetPeak();
                double t0 = clock.Elapsed.TotalSeconds;
                await CheckAllAsync(host, items, (it, st, at) => { it.Full = st; if (at >= 0) it.FullAt = at; }, checkTimeout, clock, gauge, ct).ConfigureAwait(false);
                double checkSecs = clock.Elapsed.TotalSeconds - t0;
                int checkPeak = gauge.Peak;
                foreach (var it in items) Append(checkCsv, CheckHeader, CheckLine(label, it, t0));
                var alive = items.Where(i => i.Full.State == AceEngine.Probe.LinkState.Good).ToList();
                Console.WriteLine("check: " + checkSecs.ToString("0", Inv) + " s, TCP socket peak " + checkPeak + " (established " + gauge.PeakEstablished + "); live " + alive.Count + ", verified " + alive.Count(i => i.Full.Verified)
                    + ", dead " + items.Count(i => i.Full.State == AceEngine.Probe.LinkState.Dead) + ", unknown " + items.Count(i => i.Full.State == AceEngine.Probe.LinkState.Unknown) + ", inconclusive " + items.Count(i => i.Full.IsChecking));
                Console.WriteLine("  by kind (live/total): acestream " + alive.Count(i => i.Kind == "cid") + "/" + items.Count(i => i.Kind == "cid") + ", magnet " + alive.Count(i => i.Kind == "magnet") + "/" + items.Count(i => i.Kind == "magnet"));
                if (has("--check-only")) return 0;

                // ---- 1b) stability: the same fresh check repeated over the live ones (cheap: without opening any channel) ----
                if (has("--stability"))
                {
                    var offsets = get("--stability", "0,120,240,360,600,900").Split(',').Select(x => double.Parse(x, Inv)).ToList();
                    string stabCsv = outPrefix + "_stab.csv";
                    if (File.Exists(stabCsv)) File.Delete(stabCsv);
                    var aliveCh = alive.Select(i => i.Ch).ToList();
                    int prevConc = opt.Probe.LiveVerifyConcurrency;
                    opt.Probe.LiveVerifyConcurrency = int.Parse(get("--stab-verify-conc", "4"), Inv);
                    double tS = clock.Elapsed.TotalSeconds;
                    gauge.ResetPeak();
                    for (int ri = 0; ri < offsets.Count; ri++)
                    {
                        await WaitUntilAsync(clock, tS + offsets[ri], ct).ConfigureAwait(false);
                        double at = clock.Elapsed.TotalSeconds;
                        var res = await RecheckAsync(host, aliveCh, 900, ct).ConfigureAwait(false);
                        foreach (var it in alive)
                        {
                            var st = res[it.Ch.Id];
                            Append(stabCsv, StabHeader, string.Join(";", label, ri, F(at - tS), it.Kind, it.Ch.Id, Q(it.Ch.Name), st.State, st.Verified ? 1 : 0, st.Peers, st.PeersText, Q(st.Reason)));
                        }
                        Console.WriteLine("  stability round " + ri + " (+" + (at - tS).ToString("0", Inv) + " s): still live " + res.Values.Count(v => v.State == AceEngine.Probe.LinkState.Good) + "/" + alive.Count + "; took " + (clock.Elapsed.TotalSeconds - at).ToString("0", Inv) + " s; socket peak " + gauge.Peak);
                    }
                    opt.Probe.LiveVerifyConcurrency = prevConc;
                }

                // ---- 2) accuracy at age 0 ----
                string playCsv = outPrefix + "_play.csv";
                if (File.Exists(playCsv)) File.Delete(playCsv);
                var toPlay = alive.OrderBy(i => rnd.Next()).ToList();
                if (sample > 0 && toPlay.Count > sample) toPlay = toPlay.Take(sample).ToList();
                if (compare)
                {
                    // those the old app showed as live and the current one no longer does: a sample, to see whether they really played
                    var onlyLegacy = items.Where(i => i.Legacy != null && i.Legacy.State == AceEngine.Probe.LinkState.Good && i.Full.State != AceEngine.Probe.LinkState.Good).OrderBy(i => rnd.Next()).Take(int.Parse(get("--sample-b", "60"), Inv)).ToList();
                    toPlay = toPlay.Concat(onlyLegacy).ToList();
                    Console.WriteLine("sample: " + (toPlay.Count - onlyLegacy.Count) + " live now + " + onlyLegacy.Count + " that only the old app showed");
                }
                gauge.ResetPeak();
                int done = 0;
                var recheckOf = new Dictionary<string, ChannelStatus>(StringComparer.Ordinal);
                var recheckAt = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int k = 0; k < toPlay.Count; k += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    var part = toPlay.Skip(k).Take(chunk).ToList();
                    var rc = await RecheckAsync(host, part.Select(p => p.Ch).ToList(), 120, ct).ConfigureAwait(false);
                    double rcAt = clock.Elapsed.TotalSeconds;
                    foreach (var kv in rc) { recheckOf[kv.Key] = kv.Value; recheckAt[kv.Key] = rcAt; }
                    await PlayAllAsync(host, part, workers, playSeconds, id => recheckOf[id], id => recheckAt[id], "p0", clock, label, playCsv, gauge, ct).ConfigureAwait(false);
                    done += part.Count;
                    Console.WriteLine("  age 0: " + done + "/" + toPlay.Count + " opened; socket peak " + gauge.Peak);
                }

                // ---- 3) control: what the app did NOT show ----
                if (controlN > 0)
                {
                    var notAlive = items.Where(i => i.Full.State == AceEngine.Probe.LinkState.Dead || i.Full.State == AceEngine.Probe.LinkState.Unknown).OrderBy(i => rnd.Next()).Take(controlN).ToList();
                    foreach (var it in notAlive) { recheckOf[it.Ch.Id] = it.Full; recheckAt[it.Ch.Id] = it.FullAt; }
                    await PlayAllAsync(host, notAlive, workers, int.Parse(get("--control-seconds", playSeconds.ToString(Inv)), Inv), id => recheckOf[id], id => recheckAt[id], "control", clock, label, playCsv, gauge, ct).ConfigureAwait(false);
                    Console.WriteLine("  control: " + notAlive.Count + " opened");
                }

                // ---- 4) ages ----
                if (has("--ages"))
                {
                    var okNow = toPlay.Where(i => recheckOf.ContainsKey(i.Ch.Id) && recheckOf[i.Ch.Id].State == AceEngine.Probe.LinkState.Good).OrderBy(i => rnd.Next()).ToList();
                    int per = Math.Min(int.Parse(get("--age-sample", "16"), Inv), okNow.Count / 2);
                    var g5 = okNow.Take(per).ToList();
                    var g15 = okNow.Skip(per).Take(per).ToList();
                    Console.WriteLine("ages: " + g5.Count + " channels at 5 min and " + g15.Count + " at 15");
                    var fresh = await RecheckAsync(host, g5.Concat(g15).Select(p => p.Ch).ToList(), 180, ct).ConfigureAwait(false);
                    double atFresh = clock.Elapsed.TotalSeconds;
                    var freshAt = fresh.Keys.ToDictionary(x => x, x => atFresh);
                    double age5 = double.Parse(get("--age1", "300"), Inv), age15 = double.Parse(get("--age2", "900"), Inv);
                    await WaitUntilAsync(clock, atFresh + age5, ct).ConfigureAwait(false);
                    await PlayAllAsync(host, g5, workers, playSeconds, id => fresh[id], id => freshAt[id], "age5", clock, label, playCsv, gauge, ct).ConfigureAwait(false);
                    await WaitUntilAsync(clock, atFresh + age15, ct).ConfigureAwait(false);
                    await PlayAllAsync(host, g15, workers, playSeconds, id => fresh[id], id => freshAt[id], "age15", clock, label, playCsv, gauge, ct).ConfigureAwait(false);
                }
                Console.WriteLine("end: " + clock.Elapsed.TotalMinutes.ToString("0.0", Inv) + " min; global TCP socket peak in the last phase " + gauge.Peak);
            }
            return 0;
        }

        /// <summary>
        /// A list open for a while, like the app: LiveChecker.Check of all the channels and it is left running for 'seconds' s (first pass and retries in a loop of what is unverified). It records when each
        /// card comes out, how many there are when the first pass ends and how many more thanks to the retries, the connection peak, and on leaving (Stop) that nothing is left running. With playLate > 0 it opens
        /// the first cards that came out in the retries to check that they play.
        /// </summary>
        private static async Task<int> LoopModeAsync(AceEngineHost host, List<Item> items, int seconds, int playLate, int playSeconds, string outPrefix, string label, System.Diagnostics.Stopwatch clock, TcpGauge gauge, CancellationToken ct)
        {
            var byId = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var i in items) byId[i.Ch.Id] = i;
            var firstGood = new Dictionary<string, double>(StringComparer.Ordinal);
            var lc = new LiveChecker(() => host);
            double t0 = clock.Elapsed.TotalSeconds;
            lc.StatusChanged += (c, st) =>
            {
                if (c != null && st.IsLive && byId.ContainsKey(c.Id)) lock (firstGood) { if (!firstGood.ContainsKey(c.Id)) firstGood[c.Id] = clock.Elapsed.TotalSeconds - t0; }
            };
            gauge.ResetPeak();
            lc.Check(items.Select(i => i.Ch).ToList());
            double firstPassAt = -1; int cardsAtFirstPass = -1, lastReport = -1;
            Console.WriteLine("open list: " + items.Count + " channels, " + seconds + " s");
            while (clock.Elapsed.TotalSeconds - t0 < seconds)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                double el = clock.Elapsed.TotalSeconds - t0;
                int cards; lock (firstGood) cards = firstGood.Count;
                if (firstPassAt < 0 && lc.Total > 0 && lc.Done >= lc.Total) { firstPassAt = el; cardsAtFirstPass = cards; Console.WriteLine("  FIRST PASS finished at " + el.ToString("0", Inv) + " s: " + cards + " cards"); }
                if ((int)el / 15 != lastReport) { lastReport = (int)el / 15; Console.WriteLine("  " + el.ToString("0", Inv) + " s: checked " + lc.Done + "/" + lc.Total + ", cards " + cards + ", sockets " + gauge.Now + " (peak " + gauge.Peak + "), budget " + (host.ProbeBudget == null ? -1 : host.ProbeBudget.InUse) + "/" + (host.ProbeBudget == null ? -1 : host.ProbeBudget.Capacity)); }
            }
            int finalCards; lock (firstGood) finalCards = firstGood.Count;
            int peak = gauge.Peak;
            Console.WriteLine("RESULT: first pass " + (firstPassAt < 0 ? "NOT FINISHED" : firstPassAt.ToString("0", Inv) + " s with " + cardsAtFirstPass + " cards") + "; at the end (" + seconds + " s) " + finalCards + " cards; more thanks to the retries: " + (firstPassAt < 0 ? finalCards : finalCards - cardsAtFirstPass) + "; socket peak " + peak);
            // leaving the screen: everything is cancelled and nothing is left running in the background
            double tStop = clock.Elapsed.TotalSeconds;
            lc.Stop();
            await Task.Delay(4000, ct).ConfigureAwait(false);
            Console.WriteLine("after leaving (Stop, +4 s): sockets " + gauge.Now + ", budget in use " + (host.ProbeBudget == null ? -1 : host.ProbeBudget.InUse) + ", checking " + lc.Running);
            string csv = outPrefix + "_loop.csv";
            if (File.Exists(csv)) File.Delete(csv);
            foreach (var it in items)
            {
                double fg; bool has; lock (firstGood) has = firstGood.TryGetValue(it.Ch.Id, out fg);
                var st = lc.GetStatus(it.Ch);
                Append(csv, "run;kind;id;name;first_card_s;phase;state;peers", string.Join(";", label, it.Kind, it.Ch.Id, Q(it.Ch.Name), has ? F(fg) : "", has && firstPassAt >= 0 && fg > firstPassAt ? "retry" : has ? "first" : "", st.State, st.PeersText));
            }
            if (playLate > 0)
            {
                var late = items.Where(i => { double f; lock (firstGood) return firstGood.TryGetValue(i.Ch.Id, out f) && firstPassAt >= 0 && f > firstPassAt; }).Take(playLate).ToList();
                Console.WriteLine("opening " + late.Count + " cards that came out in the retries");
                foreach (var it in late) { it.Full = new ChannelStatus { State = AceEngine.Probe.LinkState.Good, Verified = true }; }
                string playCsv = outPrefix + "_loopplay.csv";
                if (File.Exists(playCsv)) File.Delete(playCsv);
                await PlayAllAsync(host, late, 2, playSeconds, id => byId[id].Full, id => tStop, "late", clock, label, playCsv, gauge, ct).ConfigureAwait(false);
            }
            return 0;
        }

        /// <summary>The real LiveChecker over all the channels (like the app when opening a category): records each one's final state and when it arrived.</summary>
        private static async Task CheckAllAsync(AceEngineHost host, List<Item> items, Action<Item, ChannelStatus, double> set, double timeoutS, System.Diagnostics.Stopwatch clock, TcpGauge gauge, CancellationToken ct)
        {
            var lc = new LiveChecker(() => host);
            var byId = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var i in items) byId[i.Ch.Id] = i;
            lc.StatusChanged += (c, st) =>
            {
                Item it;
                if (c != null && byId.TryGetValue(c.Id, out it) && !st.IsChecking) set(it, st, clock.Elapsed.TotalSeconds);
            };
            lc.Check(items.Select(i => i.Ch).ToList());
            var swc = System.Diagnostics.Stopwatch.StartNew();
            int lastDone = -1;
            while (lc.Running && swc.Elapsed.TotalSeconds < timeoutS)
            {
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                if (lc.Done != lastDone) { lastDone = lc.Done; if (lc.Done % 25 == 0) Console.WriteLine("  checked " + lc.Done + "/" + lc.Total + " in " + swc.Elapsed.TotalSeconds.ToString("0", Inv) + " s; sockets now " + gauge.Now + ", peak " + gauge.Peak); }
            }
            lc.Stop();
            foreach (var it in items) { var st = lc.GetStatus(it.Ch); set(it, st, -1); }
            lc.Dispose();
        }

        /// <summary>Engine variants to try changes without recompiling (the tool only).</summary>
        private static void ApplyTweaks(EngineOptions o, Func<string, string, string> get, Func<string, bool> has)
        {
            LiveTuning.Apply(o, get, has);
            if (has("--legacy-alive")) { o.Probe.ResolveInfoHashLinks = false; }          // the behavior from before the reliable-live work
        }

        private static async Task WaitUntilAsync(System.Diagnostics.Stopwatch clock, double at, CancellationToken ct)
        {
            double d = at - clock.Elapsed.TotalSeconds;
            if (d > 0) { Console.WriteLine("  waiting " + d.ToString("0", Inv) + " s ..."); await Task.Delay(TimeSpan.FromSeconds(d), ct).ConfigureAwait(false); }
        }

        // ---------- load sources ----------

        private static async Task<List<Item>> LoadListsAsync(string codes, CancellationToken ct)
        {
            var all = new List<LinkEntry>();
            using (var loader = new ListLoader())
                foreach (var code in codes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var entries = await loader.LoadAsync(new ListSource(code.Trim(), code.Trim()), ct).ConfigureAwait(false);
                    Console.WriteLine("list " + code + ": " + entries.Count + " links");
                    all.AddRange(entries);
                }
            var snap = CatalogBuilder.Build(all);
            return snap.Channels.Select(c => new Item { Ch = c, Source = "lists", Query = c.CategoryName }).ToList();
        }

        private static async Task<List<Item>> LoadSearchAsync(string queries, CancellationToken ct)
        {
            var res = new List<Item>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var central = new AceCentralSearchProvider(null, new AceCentralOptions());
            using (var svc = new SearchService(new EmptyCatalog(), new IRemoteSearchProvider[] { central }))
            {
                foreach (var q in queries.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    IReadOnlyList<Channel> list;
                    if (q.StartsWith("explore:", StringComparison.Ordinal)) list = await svc.ExploreRemoteAsync(q.Substring(8), ct).ConfigureAwait(false);
                    else list = await svc.SearchRemoteAsync(q, ct).ConfigureAwait(false);
                    int n = 0;
                    foreach (var c in list) if (seen.Add(c.Id)) { res.Add(new Item { Ch = c, Source = "search", Query = q }); n++; }
                    Console.WriteLine("search '" + q + "': " + list.Count + " results, " + n + " new" + (central.LastError != null ? " (warning: " + central.LastError + ")" : ""));
                }
            }
            return res;
        }

        // ---------- check with a new scheduler (without memory of earlier results) ----------

        private static async Task<Dictionary<string, ChannelStatus>> RecheckAsync(AceEngineHost host, IList<Channel> channels, double timeoutS, CancellationToken ct)
        {
            var res = new Dictionary<string, ChannelStatus>(StringComparer.Ordinal);
            var sched = host.CreateProbeScheduler();
            try
            {
                using (var lc = new LiveChecker(() => sched))
                {
                    lc.Check(channels.ToList());
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (lc.Running && sw.Elapsed.TotalSeconds < timeoutS)
                    {
                        try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    }
                    lc.Stop();
                    foreach (var c in channels) res[c.Id] = lc.GetStatus(c);
                }
            }
            finally { sched.Dispose(); }
            return res;
        }

        // ---------- open channels ----------

        private static async Task PlayAllAsync(AceEngineHost host, List<Item> part, int workers, int seconds, Func<string, ChannelStatus> jit, Func<string, double> jitAt, string phase,
            System.Diagnostics.Stopwatch clock, string label, string csv, TcpGauge gauge, CancellationToken ct)
        {
            var queue = new Queue<Item>(part);
            var tasks = new List<Task>();
            for (int w = 0; w < Math.Max(1, workers); w++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        Item it;
                        lock (queue) { if (queue.Count == 0) return; it = queue.Dequeue(); }
                        ct.ThrowIfCancellationRequested();
                        double start = clock.Elapsed.TotalSeconds;
                        var r = await PlayAsync(host, it.Ch, seconds, ct).ConfigureAwait(false);
                        var js = jit(it.Ch.Id);
                        double age = start - jitAt(it.Ch.Id);
                        Append(csv, PlayHeader, PlayLine(label, phase, it, js, age, start, r, gauge.Peak));
                        Console.WriteLine("    [" + phase + "] " + (r.Class.PadRight(14)) + " fb=" + (r.FirstByte < 0 ? "--" : r.FirstByte.ToString("0.0", Inv)) + "s  full=" + it.Full.State + (it.Full.Verified ? "/" + it.Full.PeersText : "") + " jit=" + js.State + (js.Verified ? "/" + js.PeersText : "") + "  " + it.Kind + "  " + Trim(it.Ch.Name, 44));
                    }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static string Trim(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n); }

        private static async Task<PlayResult> PlayAsync(AceEngineHost host, Channel ch, int seconds, CancellationToken ct)
        {
            var r = new PlayResult { Seconds = seconds };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            OpenedStream opened = null;
            using (var wcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wcts.CancelAfter(TimeSpan.FromSeconds(seconds));
                int resolveFailures = 0;
                while (opened == null)
                {
                    try { opened = await host.OpenStreamAsync(ch.Entry.Link, new OpenOptions(), wcts.Token).ConfigureAwait(false); }
                    catch (ResolveException rex) when (resolveFailures < 2 && !wcts.IsCancellationRequested)
                    {
                        // PlaybackSession: the resolution is retried 2 times with a 2 s wait
                        resolveFailures++; r.OpenError = rex.Message;
                        try { await Task.Delay(2000, wcts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    }
                    catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; r.OpenError = "timed out opening"; break; }
                    catch (Exception ex) { r.OpenError = ex.GetType().Name + ": " + ex.Message; break; }
                }
                r.OpenSeconds = sw.Elapsed.TotalSeconds;
                if (opened == null) { r.Class = r.OpenError.Contains("timed out") || r.OpenError.Contains("cannot get transport") ? "no_descriptor" : "no_open"; r.OpenError = Q(r.OpenError); return r; }
                r.Opened = true;
                var session = opened.Session;
                var live = session as LiveSession;
                r.Kind = live != null ? "live" : (session == null ? "direct_url" : "vod");
                if (opened.Transport != null && opened.Transport.Live != null) r.BitrateKbps = opened.Transport.Live.Bitrate * 8 / 1000.0;
                var an = new TsAnalyzer();
                long bytes = 0;
                double firstByte = -1;
                int dataSeconds = 0;
                try
                {
                    using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(wcts.Token))
                    {
                        long lastBucket = 0;
                        var reader = Task.Run(async () =>
                        {
                            try
                            {
                                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                                using (var resp = await http.GetAsync(opened.Url, HttpCompletionOption.ResponseHeadersRead, rcts.Token).ConfigureAwait(false))
                                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    var buf = new byte[64 * 1024];
                                    while (!rcts.IsCancellationRequested)
                                    {
                                        int n = await stream.ReadAsync(buf, 0, buf.Length, rcts.Token).ConfigureAwait(false);
                                        if (n <= 0) break;
                                        if (firstByte < 0) firstByte = sw.Elapsed.TotalSeconds;
                                        Interlocked.Add(ref bytes, n);
                                        if (r.Kind != "vod") an.Feed(buf, 0, n);
                                    }
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception) { }
                        });
                        while (!rcts.IsCancellationRequested)
                        {
                            try { await Task.Delay(1000, rcts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                            long b = Interlocked.Read(ref bytes);
                            if (b > lastBucket) dataSeconds++;
                            lastBucket = b;
                            // with data flowing it is measured for a while and stopped (a channel that gives no data uses up the whole window)
                            if (firstByte >= 0 && sw.Elapsed.TotalSeconds - firstByte >= 15) { try { rcts.Cancel(); } catch (ObjectDisposedException) { } }
                        }
                        try { await reader.ConfigureAwait(false); } catch (Exception) { }
                    }
                    if (session != null) { var st = session.Stats; if (st != null) r.PeersEnd = st.Peers; }
                    if (live != null)
                    {
                        var fn = live.Funnel;
                        if (fn != null) { r.Candidates = fn.Candidates; r.Tried = fn.Tried; r.TcpOk = fn.TcpOk; r.HandshakeOk = fn.HandshakeOk; r.ExtendOk = fn.ExtendOk; r.RecentHave = fn.RecentHave; r.Unchoked = fn.Unchoked; r.Served = fn.Served; }
                    }
                }
                finally { try { if (session != null) session.Dispose(); } catch (Exception) { } }
                r.Bytes = Interlocked.Read(ref bytes);
                r.FirstByte = firstByte;
                r.SecondsWithData = dataSeconds;
                r.TsPackets = an.Packets; r.TsSync = an.SyncLosses;
                r.PostSeconds = firstByte < 0 ? 0 : Math.Max(0, Math.Min(seconds, sw.Elapsed.TotalSeconds) - firstByte);
                Classify(r);
            }
            return r;
        }

        /// <summary>
        /// plays: the first byte arrives (in time) with a valid TS stream and at >= 80 % of the channel's bitrate; data: valid video arrives but weaker; no_ts: bytes arrive that are not
        /// MPEG-TS; no_data: nothing arrives (with the funnel's reason: no_peers, tcp_no_connection, no_recent_pieces...).
        /// </summary>
        private static void Classify(PlayResult r)
        {
            bool live = r.Kind == "live";
            if (r.FirstByte >= 0)
            {
                if (live && (r.TsSync > 0 || r.TsPackets == 0) && r.Bytes >= 300 * 1000) { r.Class = "no_ts"; return; }
                double need = 0.8 * (r.BitrateKbps * 1000 / 8) * Math.Max(5, r.PostSeconds);
                r.Class = (r.Bytes >= need && (r.BitrateKbps > 0 || r.Bytes > 2 * 1024 * 1024)) ? "plays" : (r.Bytes >= 500 * 1000 ? "weak_data" : "little_data");
                return;
            }
            if (live)
            {
                if (r.Candidates == 0) r.Class = "no_peers";
                else if (r.TcpOk == 0) r.Class = "tcp_no_connection";
                else if (r.ExtendOk == 0) r.Class = "no_extend";
                else if (r.RecentHave == 0) r.Class = "no_recent_pieces";
                else if (r.Served == 0) r.Class = "no_service";
                else r.Class = "no_data";
            }
            else r.Class = "no_data";
        }

        // ---------- CSV ----------

        private const string StabHeader = "run;round;offset_s;kind;id;name;state;verified;peers;peers_text;reason";
        private const string CheckHeader = "run;source;query;kind;id;name;state;verified;peers;peers_text;at_least;estimated;seeds;approximate;unconfirmed;reason;final_at_s;legacy_state;legacy_peers";
        private static string CheckLine(string label, Item it, double t0)
        {
            var s = it.Full;
            return string.Join(";", label, it.Source, Q(it.Query), it.Kind, it.Ch.Id, Q(it.Ch.Name), s.State, s.Verified ? 1 : 0, s.Peers, s.PeersText, s.AtLeast ? 1 : 0, s.Estimated, s.Seeds, s.Approximate ? 1 : 0, s.Unconfirmed ? 1 : 0, Q(s.Reason), F(it.FullAt < 0 ? -1 : it.FullAt - t0), it.Legacy == null ? "" : it.Legacy.State.ToString(), it.Legacy == null ? "" : it.Legacy.PeersText);
        }

        private const string PlayHeader = "run;phase;legacy_state;source;query;kind;id;name;full_state;full_verified;full_peers;full_text;jit_state;jit_verified;jit_peers;jit_text;jit_reason;age_s;start_s;opened;open_error;open_s;link_kind;fb_s;bytes;secs_data;post_s;ts_packets;ts_sync;bitrate_kbps;peers_end;cand;tried;tcp_ok;hs_ok;ext_ok;recent;unchoke;served;class;sockets_peak";
        private static string PlayLine(string label, string phase, Item it, ChannelStatus js, double age, double start, PlayResult r, int sockets)
        {
            var f = it.Full;
            return string.Join(";", label, phase, it.Legacy == null ? "" : it.Legacy.State.ToString(), it.Source, Q(it.Query), it.Kind, it.Ch.Id, Q(it.Ch.Name), f.State, f.Verified ? 1 : 0, f.Peers, f.PeersText, js.State, js.Verified ? 1 : 0, js.Peers, js.PeersText, Q(js.Reason),
                F(age), F(start), r.Opened ? 1 : 0, Q(r.OpenError), F(r.OpenSeconds), r.Kind, F(r.FirstByte), r.Bytes, r.SecondsWithData, F(r.PostSeconds), r.TsPackets, r.TsSync, r.BitrateKbps.ToString("0", Inv), r.PeersEnd,
                r.Candidates, r.Tried, r.TcpOk, r.HandshakeOk, r.ExtendOk, r.RecentHave, r.Unchoked, r.Served, r.Class, sockets);
        }
    }
}
