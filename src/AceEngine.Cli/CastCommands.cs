using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Cast;
using AceEngine.Links;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace cast`: exposes a channel on the local network the way the app does when it sends to a Chromecast or a TV (HLS or progressive TS), so it can be played from another
    /// program (ffprobe, gst-launch, VLC) to validate the segments. `ace cast-sample`: how many channels of the lists a Chromecast could take. Test tools; not part of the product.
    /// </summary>
    internal static class CastCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The address of the interface the default route goes out through (what a device on the LAN would call this PC).</summary>
        public static IPAddress DefaultLanAddress()
        {
            var net = SystemNetworkInfo.Default;
            foreach (var i in net.GetInterfaces()) if (i.Gateways.Count > 0 && IpClass.IsPrivate(i.Address)) return i.Address;
            foreach (var i in net.GetInterfaces()) if (IpClass.IsPrivate(i.Address)) return i.Address;
            return null;
        }

        private static string Fmt(double bytesPerSec)
        {
            return bytesPerSec >= 1048576 ? (bytesPerSec / 1048576).ToString("0.0", Inv) + " MB/s" : (bytesPerSec / 1024).ToString("0", Inv) + " KB/s";
        }

        // ---------- ace cast ----------

        public static async Task<int> RunAsync(List<string> positional, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, Func<string, AceLink> parse, CancellationToken ct)
        {
            if (positional.Count < 1)
            {
                Console.Error.WriteLine("usage: ace cast <link> [--target IP] [--delivery hls|ts] [--seconds N] [--lan] [--profile cc2|cc3|gtv] [--check-only] [--timeshift-mib N]");
                return 2;
            }
            var link = parse(positional[0]);
            IPAddress target = null;
            if (has("--target")) { if (!IPAddress.TryParse(get("--target", null), out target)) { Console.Error.WriteLine("invalid --target"); return 2; } }
            else
            {
                target = DefaultLanAddress();
                if (target == null) { Console.Error.WriteLine("no LAN address found; pass --target"); return 1; }
                Console.WriteLine("Target (the only address that may connect): " + target + "  (this PC; pass --target to authorise another device)");
            }
            var profile = (get("--profile", "cc2") ?? "cc2") == "cc3" ? CastProfile.Chromecast3 : get("--profile", "cc2") == "gtv" ? CastProfile.GoogleTv : CastProfile.Chromecast2;
            opt.Live.TimeshiftBytes = int.Parse(get("--timeshift-mib", "64"), Inv) * 1024L * 1024;
            var delivery = (get("--delivery", "hls") ?? "hls") == "ts" ? CastDelivery.ProgressiveTs : CastDelivery.Hls;

            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var so = new CastSourceOptions { TimeshiftBytes = opt.Live.TimeshiftBytes, IdleTimeout = TimeSpan.FromMinutes(int.Parse(get("--idle-minutes", "5"), Inv)) };
                if (has("--segment-seconds")) so.Hls.TargetSegmentSeconds = double.Parse(get("--segment-seconds", "4"), Inv);
                if (has("--window")) so.Hls.WindowSegments = int.Parse(get("--window", "8"), Inv);
                if (has("--force-target")) so.AllowAnyTarget = true;
                using (var src = new CastSource(() => host, null, so))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (has("--check-only"))
                    {
                        var v = await src.CheckAsync(link, profile, ct).ConfigureAwait(false);
                        Console.WriteLine("Verdict after " + sw.Elapsed.TotalSeconds.ToString("0.0", Inv) + " s: " + v);
                        return v.IsCompatible ? 0 : v.IsIncompatible ? 3 : 4;
                    }
                    src.StateChanged += s => Console.WriteLine("[" + sw.Elapsed.TotalSeconds.ToString("0.0", Inv) + " s] state " + s);
                    var res = await src.StartAsync(new CastRequest
                    {
                        Link = link, Name = positional[0], Target = target, Profile = profile, Delivery = delivery, AllowLocalNetwork = has("--lan"),
                        Dlna = delivery == CastDelivery.ProgressiveTs ? new DlnaStreamHeaders() : null
                    }, ct).ConfigureAwait(false);
                    Console.WriteLine("Verdict: " + res.Verdict);
                    if (!res.Ok) { Console.WriteLine("Not sent: " + res.Reason + "  [" + res.Detail + "]"); return res.Failure == CastFailure.Incompatible ? 3 : 1; }
                    Console.WriteLine("Ready after " + sw.Elapsed.TotalSeconds.ToString("0.0", Inv) + " s");
                    Console.WriteLine("URL: " + res.Url);
                    if (res.MediaPlaylistUrl != res.Url) Console.WriteLine("Media playlist: " + res.MediaPlaylistUrl);
                    Console.WriteLine("Content-Type: " + res.ContentType);
                    int seconds = int.Parse(get("--seconds", "0"), Inv);
                    try
                    {
                        while (!ct.IsCancellationRequested && src.State == CastState.Casting)
                        {
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                            var st = src.GetStats();
                            Console.WriteLine("[" + sw.Elapsed.TotalSeconds.ToString("0", Inv) + " s] peers=" + st.Peers + " down=" + Fmt(st.DownloadBytesPerSec) + " buffer=" + (st.BufferFill * 100).ToString("0", Inv) + "% lag=" + st.SwarmLagSeconds.ToString("0.0", Inv) + "s"
                                + " segments=" + st.SegmentsListed + "/" + st.SegmentsPublished + " window=" + st.WindowSeconds.ToString("0.0", Inv) + "s behindEdge=" + st.PlaylistBehindEdgeSeconds.ToString("0.0", Inv) + "s"
                                + " requests=" + st.Requests + " (pl " + st.PlaylistRequests + ", seg " + st.SegmentRequests + ", ts " + st.StreamRequests + ") sent=" + (st.BytesSent / 1024) + " KiB conns=" + st.ActiveConnections
                                + " rejected=" + st.RejectedConnections + " lastReq=" + (st.SecondsSinceLastRequest < 0 ? "-" : st.SecondsSinceLastRequest.ToString("0", Inv) + "s ago"));
                            if (seconds > 0 && sw.Elapsed.TotalSeconds >= seconds) break;
                        }
                    }
                    catch (OperationCanceledException) { }
                    Console.WriteLine("Stopping...");
                    src.Stop();
                }
            }
            return 0;
        }

        // ---------- ace cast-sample ----------

        private sealed class Row
        {
            public Channel Ch;
            public CastVerdict Verdict;
            public string Error = "";
            public double Seconds;
        }

        public static async Task<int> SampleAsync(EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            string codes = get("--lists", "");
            if (codes.Length == 0) { Console.Error.WriteLine("usage: ace cast-sample --lists code1,code2 [--max N] [--concurrency 2] [--per-channel 40] [--out file.csv] [--dht-warm 20] [--seed N] [--check-budget 15]"); return 2; }
            int max = int.Parse(get("--max", "40"), Inv);
            int conc = Math.Max(1, int.Parse(get("--concurrency", "2"), Inv));
            double perChannel = double.Parse(get("--per-channel", "40"), Inv);
            int seed = int.Parse(get("--seed", "1"), Inv);
            string outCsv = get("--out", null);
            string dataDir = get("--data", Path.Combine(Path.GetTempPath(), "cast-sample-data"));
            Directory.CreateDirectory(dataDir);

            // the app's engine profile, with a small live buffer and few peers per channel: the check opens channels, it does not watch them
            var o = EngineOptionsFactory.Build(new AppConfig(), new UserSettings(), dataDir);
            o.EnableListener = false;
            o.Live.TimeshiftBytes = 8L * 1024 * 1024;
            o.Live.MaxPeers = int.Parse(get("--live-peers", "14"), Inv);
            o.Live.MaxPendingConnects = 12;
            o.Live.UploadSlots = 0; o.UploadSlots = 0;

            var all = new List<LinkEntry>();
            using (var loader = new ListLoader())
                foreach (var code in codes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var entries = await loader.LoadAsync(new ListSource(code.Trim(), code.Trim()), ct).ConfigureAwait(false);
                    Console.WriteLine("list: " + entries.Count + " links");
                    all.AddRange(entries);
                }
            var channels = CatalogBuilder.Build(all).Channels.ToList();
            Console.WriteLine("channels: " + channels.Count);
            var rnd = new Random(seed);
            using (var host = new AceEngineHost(o))
            {
                host.Start();
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                while (host.DhtNodes < 100 && sw0.Elapsed.TotalSeconds < double.Parse(get("--dht-warm", "20"), Inv)) await Task.Delay(500, ct).ConfigureAwait(false);
                Console.WriteLine("DHT: " + host.DhtNodes + " nodes");

                // which ones are live right now: the app's own checker (about 56 TCP connections at most)
                List<Channel> live;
                if (has("--no-checker")) live = channels.OrderBy(c => rnd.Next()).ToList();
                else
                {
                    var pool = channels.OrderBy(c => rnd.Next()).Take(int.Parse(get("--pool", "150"), Inv)).ToList();
                    using (var lc = new LiveChecker(() => host))
                    {
                        lc.Check(pool);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (lc.Running && sw.Elapsed.TotalSeconds < double.Parse(get("--check-budget", "240"), Inv))
                        {
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                            if (lc.Done % 25 == 0) Console.WriteLine("  checked " + lc.Done + "/" + lc.Total + " (" + sw.Elapsed.TotalSeconds.ToString("0", Inv) + " s)");
                        }
                        lc.Stop();
                        live = pool.Where(c => lc.GetStatus(c).IsLive).OrderByDescending(c => lc.GetStatus(c).Peers).ToList();
                    }
                    Console.WriteLine("live now: " + live.Count + "/" + pool.Count);
                }
                if (live.Count > max) live = live.Take(max).ToList();

                var so = new CastSourceOptions { CheckTimeshiftBytes = o.Live.TimeshiftBytes, OpenTimeout = TimeSpan.FromSeconds(perChannel), AnalysisTimeout = TimeSpan.FromSeconds(12) };
                using (var src = new CastSource(() => host, null, so))
                {
                    var rows = new List<Row>();
                    var queue = new Queue<Channel>(live);
                    var tasks = new List<Task>();
                    var gate = new object();
                    for (int w = 0; w < conc; w++)
                        tasks.Add(Task.Run(async () =>
                        {
                            while (true)
                            {
                                Channel c;
                                lock (gate) { if (queue.Count == 0) return; c = queue.Dequeue(); }
                                var row = new Row { Ch = c };
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                                {
                                    cts.CancelAfter(TimeSpan.FromSeconds(perChannel + 30));
                                    try { row.Verdict = await src.CheckAsync(c.Entry.Link, CastProfile.Chromecast2, cts.Token).ConfigureAwait(false); }
                                    catch (OperationCanceledException) { row.Error = "timeout"; }
                                    catch (Exception ex) { row.Error = ex.GetType().Name + ": " + ex.Message; }
                                }
                                row.Seconds = sw.Elapsed.TotalSeconds;
                                lock (gate)
                                {
                                    rows.Add(row);
                                    Console.WriteLine("  [" + rows.Count + "/" + live.Count + "] " + Trunc(c.Name, 34).PadRight(34) + " " + Describe(row));
                                }
                            }
                        }));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    Summary(rows);
                    if (outCsv != null)
                    {
                        var sb = new StringBuilder("name;id;result;summary;video;audio;reason;seconds;error\n");
                        foreach (var r in rows)
                            sb.Append(Q(r.Ch.Name)).Append(';').Append(Q(r.Ch.Id)).Append(';').Append(Result(r)).Append(';').Append(r.Verdict == null ? "" : r.Verdict.Summary).Append(';')
                              .Append(r.Verdict == null ? "" : Q(r.Verdict.VideoDescription)).Append(';').Append(r.Verdict == null ? "" : Q(r.Verdict.AudioDescription)).Append(';')
                              .Append(r.Verdict == null ? "" : Q(r.Verdict.Reason)).Append(';').Append(r.Seconds.ToString("0.0", Inv)).Append(';').Append(Q(r.Error)).Append('\n');
                        File.WriteAllText(outCsv, sb.ToString());
                        Console.WriteLine("written " + outCsv);
                    }
                }
            }
            return 0;
        }

        private static string Q(string s) { return (s ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' '); }
        private static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        private static string Result(Row r)
        {
            if (r.Verdict == null) return "error";
            return r.Verdict.IsCompatible ? "compatible" : r.Verdict.IsIncompatible ? "incompatible" : "unknown";
        }

        private static string Describe(Row r)
        {
            if (r.Verdict == null) return "ERROR " + r.Error + " (" + r.Seconds.ToString("0", Inv) + " s)";
            return Result(r).ToUpperInvariant() + " " + r.Verdict.Summary + " | " + r.Verdict.VideoDescription + " | " + r.Verdict.AudioDescription + (r.Verdict.Reason.Length > 0 ? " | " + r.Verdict.Reason : "") + " (" + r.Seconds.ToString("0", Inv) + " s)";
        }

        private static void Summary(List<Row> rows)
        {
            Console.WriteLine();
            int ok = rows.Count(r => r.Verdict != null && r.Verdict.IsCompatible);
            int bad = rows.Count(r => r.Verdict != null && r.Verdict.IsIncompatible);
            int unk = rows.Count(r => r.Verdict != null && r.Verdict.Support == CastSupport.Unknown);
            int err = rows.Count(r => r.Verdict == null);
            int answered = ok + bad;
            Console.WriteLine("Sample: " + rows.Count + " channels opened; verdict for " + answered + ": " + ok + " compatible (" + (answered == 0 ? 0 : 100.0 * ok / answered).ToString("0", Inv) + " %), " + bad + " incompatible; no verdict: " + unk + " unknown, " + err + " errors");
            Console.WriteLine("By stream (video+audio):");
            foreach (var g in rows.Where(r => r.Verdict != null && r.Verdict.Support != CastSupport.Unknown).GroupBy(r => r.Verdict.Summary).OrderByDescending(g => g.Count()))
                Console.WriteLine("  " + g.Count().ToString().PadLeft(3) + "  " + g.Key);
            Console.WriteLine("By reason:");
            foreach (var g in rows.Where(r => r.Verdict != null && r.Verdict.IsIncompatible).GroupBy(r => r.Verdict.Reason).OrderByDescending(g => g.Count()))
                Console.WriteLine("  " + g.Count().ToString().PadLeft(3) + "  " + g.Key);
            Console.WriteLine("Pictures:");
            foreach (var g in rows.Where(r => r.Verdict != null && r.Verdict.Stream != null && r.Verdict.Stream.Sps != null).GroupBy(r => r.Verdict.Stream.Sps.Width + "x" + r.Verdict.Stream.Sps.Height + (r.Verdict.Stream.Sps.FrameMbsOnly ? "p" : "i")).OrderByDescending(g => g.Count()))
                Console.WriteLine("  " + g.Count().ToString().PadLeft(3) + "  " + g.Key);
        }
    }
}
