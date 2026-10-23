using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.P2P;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace idle`: engine CPU at rest, as the TV leaves it with the app open on a category doing nothing.
    /// It starts the engine with the TV's profile (EngineOptionsFactory, upload active, DHT, listening) and, depending on --source, opens a channel screen with the
    /// real checker (first pass and retries in a loop) or opens none. It measures for --seconds, in windows of --interval seconds: process CPU
    /// (user + system, % of one core), GC collections and allocated bytes, threads, thread-pool work items per second, active timers and
    /// TCP sockets; at the end, the CPU broken down by system thread. The switches (--no-dht, --no-listen, --no-upload, --retry-concurrency, --cycle-interval)
    /// serve to split the consumption among the parts in separate runs. It is not part of the product.
    /// </summary>
    internal static class IdleCpu
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static List<Channel> Load(string source, string lists, string queries, CancellationToken ct)
        {
            var res = new List<Channel>();
            if (source == "lists" || source == "both")
            {
                var all = new List<LinkEntry>();
                using (var loader = new ListLoader())
                    foreach (var code in lists.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var entries = loader.LoadAsync(new ListSource(code.Trim(), code.Trim()), ct).GetAwaiter().GetResult();
                        Console.WriteLine("list " + code + ": " + entries.Count + " links");
                        all.AddRange(entries);
                    }
                res.AddRange(CatalogBuilder.Build(all).Channels);
            }
            if (source == "search" || source == "both")
            {
                var seen = new HashSet<string>(res.Select(c => c.Id), StringComparer.Ordinal);
                var central = new AceCentralSearchProvider(null, new AceCentralOptions());
                using (var svc = new SearchService(new NoCatalog(), new IRemoteSearchProvider[] { central }))
                    foreach (var q in queries.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var list = q.StartsWith("explore:", StringComparison.Ordinal) ? svc.ExploreRemoteAsync(q.Substring(8), ct).GetAwaiter().GetResult() : svc.SearchRemoteAsync(q, ct).GetAwaiter().GetResult();
                        int n = 0;
                        foreach (var c in list) if (seen.Add(c.Id)) { res.Add(c); n++; }
                        Console.WriteLine("search '" + q + "': " + list.Count + " results, " + n + " new");
                    }
            }
            return res;
        }

        private sealed class NoCatalog : IChannelCatalog
        {
            public CatalogState State { get { return CatalogState.Ready; } }
            public string Error { get { return null; } }
            public IReadOnlyList<Category> Categories { get { return new Category[0]; } }
            public IReadOnlyList<Channel> Channels { get { return new Channel[0]; } }
            public Channel Find(string id) { return null; }
            public void Start() { }
            public event Action Changed { add { } remove { } }
        }

        /// <summary>Accumulated CPU (clock ticks, 1/100 s) of each thread of the process, with its name.</summary>
        private static Dictionary<int, KeyValuePair<string, long>> ThreadTicks()
        {
            var res = new Dictionary<int, KeyValuePair<string, long>>();
            try
            {
                foreach (var dir in Directory.GetDirectories("/proc/self/task"))
                {
                    try
                    {
                        int tid = int.Parse(Path.GetFileName(dir), Inv);
                        var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                        int lp = stat.IndexOf('('), rp = stat.LastIndexOf(')');
                        string comm = stat.Substring(lp + 1, rp - lp - 1);
                        var f = stat.Substring(rp + 2).Split(' ');
                        long ticks = long.Parse(f[11], Inv) + long.Parse(f[12], Inv);
                        res[tid] = new KeyValuePair<string, long>(comm, ticks);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return res;
        }

        private static string F(double v, string fmt = "0.0") { return v.ToString(fmt, Inv); }

        public static async Task<int> RunAsync(Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            string label = get("--label", "idle");
            string source = get("--source", "none");
            string dataDir = get("--data", Path.Combine(Path.GetTempPath(), "idle-data"));
            double seconds = double.Parse(get("--seconds", "60"), Inv);
            double interval = double.Parse(get("--interval", "10"), Inv);
            double warmup = double.Parse(get("--warmup", "15"), Inv);
            int max = has("--max") ? int.Parse(get("--max", "0"), Inv) : 0;
            string csv = get("--csv", null);
            Directory.CreateDirectory(dataDir);

            var cfg = new AppConfig();
            var settings = new UserSettings();
            var opt = EngineOptionsFactory.Build(cfg, settings, dataDir);
            if (has("--no-dht")) opt.EnableDht = false;
            if (has("--no-listen")) opt.EnableListener = false;
            if (has("--no-upload")) { opt.UploadSlots = 0; opt.Live.UploadSlots = 0; }
            if (has("--retry-concurrency")) opt.Probe.RetryConcurrency = int.Parse(get("--retry-concurrency", "4"), Inv);
            if (has("--cycle-interval")) opt.Probe.CycleInterval = TimeSpan.FromSeconds(double.Parse(get("--cycle-interval", "10"), Inv));
            if (has("--tcp-budget")) opt.Probe.TcpBudget = int.Parse(get("--tcp-budget", "52"), Inv);
            if (has("--idle-conc")) opt.Probe.IdleRetryConcurrency = int.Parse(get("--idle-conc", "1"), Inv);
            if (has("--idle-pause")) opt.Probe.IdleRetryPause = TimeSpan.FromSeconds(double.Parse(get("--idle-pause", "10"), Inv));
            if (has("--no-engine")) { }                                                        // the runtime only, to measure the process baseline

            var channels = new List<Channel>();
            if (source != "none") channels = Load(source, get("--lists", ""), get("--queries", "dazn,movistar"), ct);
            if (max > 0) channels = channels.OrderBy(c => c.Id, StringComparer.Ordinal).Take(max).ToList();
            Console.WriteLine("idle '" + label + "': source " + source + " (" + channels.Count + " channels), dht=" + opt.EnableDht + " listen=" + opt.EnableListener + " upload=" + opt.UploadSlots + " retries=" + opt.Probe.RetryConcurrency + " cycle=" + opt.Probe.CycleInterval.TotalSeconds + " s, window " + seconds + " s after " + warmup + " s of warm-up");

            var proc = Process.GetCurrentProcess();
            using (var gauge = new Alive.TcpGauge())
            {
                AceEngineHost host = null;
                LiveChecker lc = null;
                if (!has("--no-engine"))
                {
                    host = new AceEngineHost(opt);
                    host.Start();
                    if (channels.Count > 0)
                    {
                        lc = new LiveChecker(() => host);
                        lc.Check(channels);
                    }
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(warmup), ct).ConfigureAwait(false);
                    // --stop-checker: the checker has already done its first pass and the screen is left: what remains is the engine with a warm DHT, the listener and the upload, without probes
                    // --resting: the interface is already at rest (30 s without touching the remote): the retries slow down
                    if (has("--resting") && lc != null) { lc.SetResting(true); Console.WriteLine("rest: retries go at a low rate (" + opt.Probe.IdleRetryConcurrency + " at a time, " + opt.Probe.IdleRetryPause.TotalSeconds + " s of breathing room)"); }
                    if (has("--stop-checker") && lc != null) { lc.Stop(); Console.WriteLine("checker stopped (as when leaving the screen); the engine alone is measured"); await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                    var clock = Stopwatch.StartNew();
                    var t0 = ThreadTicks();
                    double cpu0 = Cpu(proc), user0 = User(proc);
                    long alloc0 = GC.GetTotalAllocatedBytes(false); long tp0 = ThreadPool.CompletedWorkItemCount;
                    int g00 = GC.CollectionCount(0), g10 = GC.CollectionCount(1), g20 = GC.CollectionCount(2);
                    if (csv != null && !File.Exists(csv)) File.AppendAllText(csv, "label;t_s;cpu_pct;user_pct;gc0;gc1;gc2;alloc_mb_s;threads;tpitems_s;timers;sockets;budget;done;total\n");
                    Console.WriteLine("  t(s)  cpu%   usr%  gc0/1/2  alloc MB/s  thrds  tp/s  timers  sockets  budget  checked");
                    double lastT = 0, lastCpu = cpu0, lastUser = user0; long lastAlloc = alloc0, lastTp = tp0; int l0 = g00, l1 = g10, l2 = g20;
                    var samples = new List<double>();
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        double wait = Math.Min(interval, seconds - clock.Elapsed.TotalSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                        proc.Refresh();
                        double t = clock.Elapsed.TotalSeconds, dt = t - lastT;
                        double cpu = Cpu(proc), user = User(proc);
                        long alloc = GC.GetTotalAllocatedBytes(false), tp = ThreadPool.CompletedWorkItemCount;
                        int a0 = GC.CollectionCount(0), a1 = GC.CollectionCount(1), a2 = GC.CollectionCount(2);
                        double cpuPct = (cpu - lastCpu) / dt * 100.0, userPct = (user - lastUser) / dt * 100.0;
                        samples.Add(cpuPct);
                        string budget = host != null && host.ProbeBudget != null ? host.ProbeBudget.InUse + "/" + host.ProbeBudget.Capacity : "-";
                        Console.WriteLine("  " + F(t, "0").PadLeft(4) + "  " + F(cpuPct, "0.0").PadLeft(5) + "  " + F(userPct, "0.0").PadLeft(5) + "  " + (a0 - l0) + "/" + (a1 - l1) + "/" + (a2 - l2) + "  " + F((alloc - lastAlloc) / 1048576.0 / dt, "0.00").PadLeft(10)
                            + "  " + proc.Threads.Count.ToString().PadLeft(5) + "  " + F((tp - lastTp) / dt, "0").PadLeft(4) + "  " + System.Threading.Timer.ActiveCount.ToString().PadLeft(6) + "  " + gauge.Now.ToString().PadLeft(7) + "  " + budget.PadLeft(6)
                            + "  " + (lc == null ? "-" : lc.Done + "/" + lc.Total));
                        if (csv != null)
                            File.AppendAllText(csv, string.Join(";", label, F(t, "0.0"), F(cpuPct), F(userPct), a0 - l0, a1 - l1, a2 - l2, F((alloc - lastAlloc) / 1048576.0 / dt, "0.00"), proc.Threads.Count, F((tp - lastTp) / dt, "0"), System.Threading.Timer.ActiveCount, gauge.Now, budget, lc == null ? 0 : lc.Done, lc == null ? 0 : lc.Total) + "\n");
                        lastT = t; lastCpu = cpu; lastUser = user; lastAlloc = alloc; lastTp = tp; l0 = a0; l1 = a1; l2 = a2;
                    }
                    double total = clock.Elapsed.TotalSeconds;
                    proc.Refresh();
                    double cpuAll = (Cpu(proc) - cpu0) / total * 100.0, userAll = (User(proc) - user0) / total * 100.0;
                    samples.Sort();
                    Console.WriteLine("RESULT " + label + ": cpu avg " + F(cpuAll) + "% of one core (user " + F(userAll) + "%, system " + F(cpuAll - userAll) + "%), window median " + F(samples[samples.Count / 2]) + "%, max " + F(samples[samples.Count - 1])
                        + "%; GC gen0/1/2 per s " + F((GC.CollectionCount(0) - g00) / total, "0.00") + "/" + F((GC.CollectionCount(1) - g10) / total, "0.00") + "/" + F((GC.CollectionCount(2) - g20) / total, "0.00")
                        + "; allocated " + F((GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0 / total, "0.00") + " MB/s; pool work items " + F((ThreadPool.CompletedWorkItemCount - tp0) / total, "0") + "/s; threads " + proc.Threads.Count);
                    // breakdown by thread
                    var t1 = ThreadTicks();
                    var byName = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var kv in t1)
                    {
                        long before = 0; KeyValuePair<string, long> b;
                        if (t0.TryGetValue(kv.Key, out b)) before = b.Value;
                        double secs = (kv.Value.Value - before) / 100.0;
                        double d; byName.TryGetValue(kv.Value.Key, out d); byName[kv.Value.Key] = d + secs;
                    }
                    Console.WriteLine("  CPU by thread name (% of one core, window average):");
                    foreach (var kv in byName.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine("    " + kv.Key.PadRight(20) + F(kv.Value / total * 100.0, "0.0") + "%");
                    if (lc != null) Console.WriteLine("  checker: " + lc.Done + "/" + lc.Total + " checked, " + (host.ProbeBudget == null ? "" : "budget peak " + host.ProbeBudget.Peak + "/" + host.ProbeBudget.Capacity));
                    if (host != null) Console.WriteLine("  DHT: " + host.DhtNodes + " nodes");
                }
                finally
                {
                    if (lc != null) { lc.Stop(); lc.Dispose(); }
                    if (host != null) host.Dispose();
                }
            }
            return 0;
        }

        private static double Cpu(Process p) { return p.TotalProcessorTime.TotalSeconds; }
        private static double User(Process p) { return p.UserProcessorTime.TotalSeconds; }
    }
}
