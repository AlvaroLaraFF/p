using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
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
    /// `ace measure`: measures the reality of each channel in a list: scrape, DHT, descriptor resolution, connection funnel with the
    /// peers (TCP, handshake, EXTEND, HAVE, UNCHOKE, pieces), live position progress and first video byte. One line per channel and mode (cold = just
    /// resolved, with the peers that served the descriptor; warm = descriptor in the cache, without them, like when opening an already probed channel). It is used with a single engine
    /// shared by all the channels, like the app. It is not part of the product: it is the measurement tool.
    /// </summary>
    internal static class Measure
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Row
        {
            public string Pass, Mode, Name, Group, ContentId, InfoHash = "";
            // scrape and DHT
            public string ProbeState = "", ScrapeSeeds = "", ScrapePeers = "", ScrapeTrackers = "";
            public int DhtCandidates = -1, TrackerCandidates = -1;
            // resolution
            public bool Resolved; public double ResolveSeconds = -1; public string ResolveSource = "", ResolveFail = "";
            public int GoodPeers, ResCandidates, ResAttempts, ResConnTimeouts, ResRefused, ResHandshakes, ResClosed;
            public int Trust = -1;
            // session
            public double Seconds; public bool Opened; public string OpenError = "";
            public LiveFunnel F;
            public double FirstByteSeconds = -1; public long Bytes; public int SecondsWithData; public double MbitPerSec;
            public long TsPackets, TsSyncLoss, TsCcErrors, TsTransportErrors;
            public double PlayableSeconds = -1;
            public int PiecesVerified, SigFail, Rehooks, HolesSkipped;
            public long BitrateBytes;
            public LiveProbeResult Lp;
            public bool Played; public string FailClass = "", FailDetail = "";
            public string StatsLine = "";
        }

        public static async Task<int> RunAsync(string listPath, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            var inv = Inv;
            string pass = get("--label", "pass");
            string outCsv = get("--out", "logs/peers/measure.csv");
            string peersCsv = get("--peers-out", Path.ChangeExtension(outCsv, null) + "_peers.csv");
            string mode = get("--mode", "both");
            int seconds = int.Parse(get("--seconds", "45"), inv);
            int warmSeconds = int.Parse(get("--warm-seconds", "30"), inv);
            int conc = int.Parse(get("--concurrency", "5"), inv);
            opt.Resolver.Timeout = TimeSpan.FromSeconds(int.Parse(get("--resolve-timeout", "30"), inv));
            Configure(opt, get, has);

            var entries = M3uParser.Parse(File.ReadAllText(listPath)).Where(e => e.IsProbeable && e.Kind == LinkKind.AceStream).ToList();
            entries = LinkList.Dedupe(entries);
            if (has("--ids"))
            {
                var ids = new List<string>(File.ReadAllLines(get("--ids", null)).Select(l => l.Trim().Split(' ', ';', ',', '\t')[0].ToLowerInvariant()).Where(l => l.Length == 40));
                entries = ids.Select(id => entries.FirstOrDefault(e => e.Link != null && e.Link.Value.Equals(id, StringComparison.OrdinalIgnoreCase))).Where(e => e != null).ToList();
            }
            int skip = int.Parse(get("--skip", "0"), inv), max = int.Parse(get("--max", "0"), inv);
            entries = entries.Skip(skip).ToList();
            if (max > 0) entries = entries.Take(max).ToList();
            Console.WriteLine("measure '" + pass + "': " + entries.Count + " channels, mode " + mode + ", " + seconds + " s (warm " + warmSeconds + " s), concurrency " + conc + " -> " + outCsv);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv)));
            var rows = new List<Row>();
            var peerRows = new List<string>();
            var gate = new object();
            bool header = !File.Exists(outCsv);
            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                // Warm up the DHT as in the app (which has been running for a while): with an empty table the first resolutions of each pass come out much worse
                var swWarm = System.Diagnostics.Stopwatch.StartNew();
                while (host.DhtNodes < int.Parse(get("--dht-warm-nodes", "120"), inv) && swWarm.Elapsed < TimeSpan.FromSeconds(int.Parse(get("--dht-warm-max", "25"), inv)))
                {
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
                    if (swWarm.Elapsed.TotalSeconds > 3 && host.DhtNodes < 30)
                    {
                        // the table grows by asking for neighbors: a random search fills it
                        var rnd = new byte[20]; new Random().NextBytes(rnd);
                        var dht = host.DhtClientForDiagnostics;
                        if (dht != null) { var t = dht.GetPeersAsync(rnd, ps => { }, TimeSpan.FromSeconds(4), ct); }
                    }
                }
                Console.WriteLine("DHT warmed up: " + host.DhtNodes + " nodes in " + swWarm.Elapsed.TotalSeconds.ToString("0.0", inv) + " s");
                var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var prober = new LinkProber(host.Resolver, null, http, opt.Probe);
                    using (var sem = new SemaphoreSlim(conc))
                    {
                        int rounds = (mode == "resolve" && has("--twice")) ? 2 : 1;
                        for (int round = 1; round <= rounds; round++)
                        {
                            string passName = rounds > 1 ? pass + "_r" + round : pass;
                            if (round == 2) { host.Resolver.Cache.Clear(); Console.WriteLine("--- second resolution round (empty cache; peer hints are kept) ---"); }
                            int done = 0;
                            var swAll = System.Diagnostics.Stopwatch.StartNew();
                            var tasks = entries.Select(e => Task.Run(async () =>
                            {
                                await sem.WaitAsync(ct).ConfigureAwait(false);
                                try
                                {
                                    var rs = await MeasureChannelAsync(host, http, prober, e, passName, mode, seconds, warmSeconds, has("--probe"), opt, ct).ConfigureAwait(false);
                                    lock (gate)
                                    {
                                        foreach (var r in rs)
                                        {
                                            rows.Add(r);
                                            File.AppendAllText(outCsv, (header ? CsvHeader() + "\n" : "") + CsvLine(r) + "\n");
                                            header = false;
                                            if (r.F != null) foreach (var t in r.F.Peers) peerRows.Add(PeerLine(r, t));
                                        }
                                        done++;
                                        var first = rs[0];
                                        Console.WriteLine("[" + done + "/" + entries.Count + " " + swAll.Elapsed.TotalSeconds.ToString("0", inv) + "s] " + string.Join(" | ", rs.Select(r => r.Mode + ":" + (r.Played ? "OK " + r.FirstByteSeconds.ToString("0.0", inv) + "s" : "FAIL " + r.FailClass))) + "  S" + first.ScrapeSeeds + "/P" + first.ScrapePeers + "  " + first.Name
                                            + (mode == "resolve" ? "  resolved=" + first.Resolved + " " + first.ResolveSeconds.ToString("0.0", inv) + "s" : ""));
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex) { Console.Error.WriteLine("error in " + e.Name + ": " + ex); }
                                finally { sem.Release(); }
                            })).ToList();
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                    }
                }
            }
            bool hdr = !File.Exists(peersCsv);
            File.AppendAllText(peersCsv, (hdr ? PeerHeader() + "\n" : "") + string.Join("\n", peerRows) + (peerRows.Count > 0 ? "\n" : ""));
            Summary(rows);
            return 0;
        }

        private static void Configure(EngineOptions o, Func<string, string, string> get, Func<string, bool> has)
        {
            // Experiment options for the "after": they are read here so variants can be tried without recompiling.
            if (has("--tv-profile"))
            {
                // The settings the TV app uses (EngineOptionsFactory + default AppConfig): 24 MiB live buffer, strict signature, 2 upload slots and 256 KB/s, limited DHT
                var tv = AceEngine.App.EngineOptionsFactory.Build(new AceEngine.App.AppConfig(), null);
                o.Live.MaxBufferBytes = tv.Live.MaxBufferBytes; o.Live.Signature = tv.Live.Signature; o.Live.UploadSlots = tv.Live.UploadSlots; o.UploadSlots = tv.UploadSlots;
                o.UploadMaxBytesPerSec = tv.UploadMaxBytesPerSec; o.DhtMaxInFlight = tv.DhtMaxInFlight; o.Resolver.StrictTransport = tv.Resolver.StrictTransport; o.Resolver.TrustEnforce = tv.Resolver.TrustEnforce;
                o.Http.LiveTransfer = tv.Http.LiveTransfer; o.MaxBufferBytes = tv.MaxBufferBytes;
                o.Live.TimeshiftBytes = tv.Live.TimeshiftBytes; o.Live.TimeshiftBlockBytes = tv.Live.TimeshiftBlockBytes;               // live with memory (64 MiB): the local server's reader goes through the circular buffer
            }
            LiveTuning.Apply(o, get, has);
            var inv = Inv;
            if (has("--res-connect-timeout")) o.Resolver.ConnectTimeout = TimeSpan.FromSeconds(double.Parse(get("--res-connect-timeout", "8"), inv));
            if (has("--res-handshake-timeout")) o.Resolver.HandshakeTimeout = TimeSpan.FromSeconds(double.Parse(get("--res-handshake-timeout", "15"), inv));
            if (has("--res-conns")) o.Resolver.MaxConnections = int.Parse(get("--res-conns", "30"), inv);
            if (has("--live-target")) o.Probe.LiveProbe.TargetPeers = Math.Max(1, int.Parse(get("--live-target", "4"), inv));
            if (has("--live-conns")) o.Probe.LiveProbe.MaxConnections = Math.Max(1, int.Parse(get("--live-conns", "12"), inv));
            if (has("--live-max-seconds")) o.Probe.LiveProbe.MaxTime = TimeSpan.FromSeconds(Math.Max(2, double.Parse(get("--live-max-seconds", "12"), inv)));
        }

        // ---------- one channel ----------

        private static async Task<List<Row>> MeasureChannelAsync(AceEngineHost host, HttpClient http, LinkProber prober, LinkEntry e, string pass, string mode, int seconds, int warmSeconds, bool doProbe, EngineOptions opt, CancellationToken ct)
        {
            var res = new List<Row>();
            var link = e.Link;
            var cold = new Row { Pass = pass, Mode = "cold", Name = e.Name, Group = e.Group ?? "", ContentId = link.Value };
            var profile = new ResolveProfile();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ResolvedTransport resolved = null;
            try { resolved = await host.Resolver.ResolveAsync(link, ct, false, true, profile).ConfigureAwait(false); }
            catch (ResolveException ex) { cold.ResolveFail = Shorten(ex.Message); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { cold.ResolveFail = ex.GetType().Name + ": " + Shorten(ex.Message); }
            cold.ResolveSeconds = sw.Elapsed.TotalSeconds;
            var st = profile.Stats;
            cold.ResCandidates = st.Candidates; cold.ResAttempts = st.Attempts; cold.ResConnTimeouts = st.ConnectTimeouts; cold.ResRefused = st.Refused; cold.ResHandshakes = st.HandshakesOk; cold.ResClosed = st.ClosedByPeer;
            cold.DhtCandidates = st.Candidates;
            if (resolved == null || resolved.Transport == null || !resolved.Transport.IsLive || resolved.Transport.Live == null)
            {
                if (mode == "resolve" && resolved != null) { cold.Resolved = true; cold.ResolveSource = resolved.Source; }
                cold.Resolved = resolved != null;
                if (resolved != null) cold.FailClass = "not_bt_live";
                else cold.FailClass = "no_descriptor";
                cold.FailDetail = resolved == null ? ResolveWhy(cold) : "";
                res.Add(cold);
                return res;
            }
            cold.BitrateBytes = resolved.Transport.Live.Bitrate;
            if (mode == "resolve") { cold.Resolved = true; cold.ResolveSource = resolved.Source; cold.GoodPeers = resolved.GoodPeers == null ? 0 : resolved.GoodPeers.Count; res.Add(cold); return res; }
            if (mode == "probe")
            {
                // Only the criterion: resolve, count by trackers and probe the live stream; no playback session. It is joined afterwards with what each channel played.
                cold.Mode = "probe"; cold.Resolved = true; cold.ResolveSource = resolved.Source; cold.GoodPeers = resolved.GoodPeers == null ? 0 : resolved.GoodPeers.Count;
                cold.InfoHash = Hex.ToHex(resolved.Transport.Live.InfoHash); cold.BitrateBytes = resolved.Transport.Live.Bitrate;
                var lt = new SwarmTarget { InfoHash = resolved.Transport.Live.InfoHash, Trackers = resolved.Transport.Live.Trackers.ToList(), ContentId = link.Value };
                ProbeResult cnt = null;
                try { cnt = await prober.CountAsync(lt, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; } catch (Exception) { }
                FillScrape(cold, cnt);
                var lpr0 = await new LiveProber(opt, host.DhtClientForDiagnostics, null, http, () => host.AnnouncedPort).ProbeAsync(resolved.Transport.Live, resolved.GoodPeers, link.Value, opt.Probe.LiveProbe, ct).ConfigureAwait(false);
                cold.Lp = lpr0;
                res.Add(cold);
                return res;
            }
            cold.Resolved = true; cold.ResolveSource = resolved.Source; cold.GoodPeers = resolved.GoodPeers == null ? 0 : resolved.GoodPeers.Count;
            cold.Trust = resolved.Trust == null ? -1 : (int)resolved.Trust.Level;

            // scrape (in parallel with the session)
            var live = resolved.Transport.Live;
            var target = new SwarmTarget { InfoHash = live.InfoHash, Trackers = live.Trackers.ToList(), ContentId = link.Value };
            var scrapeTask = Task.Run(async () =>
            {
                try { return await prober.CountAsync(target, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                catch (Exception) { return null; }
            });

            string ihHex = Hex.ToHex(live.InfoHash);
            cold.InfoHash = ihHex;
            host.Resolver.Hints.Remove(link.Value); host.Resolver.Hints.Remove(ihHex);          // the cold measurement does not inherit hints from other runs
            if (mode == "cold" || mode == "both")
            {
                await RunSessionAsync(host, link, resolved, cold, seconds, ct).ConfigureAwait(false);
                res.Add(cold);
                host.Resolver.Hints.Remove(link.Value); host.Resolver.Hints.Remove(ihHex);      // what the cold session left behind does not count for the one afterwards
            }
            LiveProbeResult lpr = null;
            if (doProbe)
            {
                var po = opt.Probe.LiveProbe;
                var lprober = new LiveProber(opt, host.DhtClientForDiagnostics, null, http, () => host.AnnouncedPort);
                try
                {
                    lpr = await lprober.ProbeAsync(live, resolved.GoodPeers, link.Value, po, ct).ConfigureAwait(false);
                    if (lpr.GoodPeers.Count > 0 && opt.Live.UsePeerHints) { host.Resolver.Hints.Add(link.Value, lpr.GoodPeers); host.Resolver.Hints.Add(ihHex, lpr.GoodPeers); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
                foreach (var r in res) r.Lp = lpr;
            }
            var sr = await scrapeTask.ConfigureAwait(false);
            foreach (var r in res) FillScrape(r, sr);

            if (mode == "warm" || mode == "both")
            {
                var warm = new Row { InfoHash = ihHex, Lp = lpr, Pass = pass, Mode = "warm", Name = e.Name, Group = e.Group ?? "", ContentId = link.Value, Resolved = true, ResolveSource = "local_db", ResolveSeconds = 0, GoodPeers = 0, BitrateBytes = cold.BitrateBytes };
                FillScrape(warm, sr);
                warm.DhtCandidates = cold.DhtCandidates;
                await RunSessionAsync(host, link, null, warm, warmSeconds, ct).ConfigureAwait(false);
                res.Add(warm);
            }
            if (res.Count == 0) { FillScrape(cold, sr); res.Add(cold); }
            return res;
        }

        private static void FillScrape(Row r, ProbeResult sr)
        {
            if (sr == null) { r.ProbeState = "?"; return; }
            r.ProbeState = sr.State.ToString();
            r.ScrapeSeeds = sr.Seeders.ToString(Inv); r.ScrapePeers = sr.Peers.ToString(Inv);
            r.ScrapeTrackers = string.Join(" ", sr.Trackers.Select(t => ShortTracker(t.Tracker) + "=" + (t.Status == ScrapeStatus.Ok ? t.Seeders + "/" + t.Leechers : (t.TimedOut ? "timeout" : t.Status.ToString()))));
        }

        private static string ShortTracker(string t)
        {
            try { var u = new Uri(t); return u.Host + ":" + u.Port; } catch { return t; }
        }

        private static string Shorten(string s) { s = (s ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' '); return s.Length > 90 ? s.Substring(0, 90) : s; }

        private static string ResolveWhy(Row c)
        {
            if (c.ResCandidates == 0) return "no_dht_peers";
            if (c.ResAttempts > 0 && c.ResHandshakes == 0) return "no_peer_answers(" + c.ResCandidates + " cand, " + c.ResAttempts + " attempts, " + c.ResConnTimeouts + " timeouts, " + c.ResRefused + " refusals)";
            return "peers_without_descriptor(" + c.ResHandshakes + " hs)";
        }

        // ---------- one playback session ----------

        private static async Task RunSessionAsync(AceEngineHost host, AceLink link, ResolvedTransport resolved, Row row, double seconds, CancellationToken ct)
        {
            row.Seconds = seconds;
            OpenedStream opened = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                opened = await host.OpenStreamAsync(link, new OpenOptions { Resolved = resolved }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { row.OpenError = ex.GetType().Name + ": " + Shorten(ex.Message); row.FailClass = "our_error"; row.FailDetail = row.OpenError; return; }
            var session = opened.Session as LiveSession;
            if (session == null) { row.FailClass = "not_bt_live"; if (opened.Session != null) opened.Session.Dispose(); return; }
            row.Opened = true;
            var an = new TsAnalyzer();
            long bytes = 0; double firstByte = -1; int dataSeconds = 0; long lastBucketBytes = 0;
            var swPlay = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    rcts.CancelAfter(TimeSpan.FromSeconds(seconds));
                    var reader = Task.Run(async () =>
                    {
                        try
                        {
                            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                            using (var resp = await http.GetAsync(opened.Url, HttpCompletionOption.ResponseHeadersRead, rcts.Token))
                            using (var stream = await resp.Content.ReadAsStreamAsync())
                            {
                                var buf = new byte[64 * 1024];
                                while (!rcts.IsCancellationRequested)
                                {
                                    int n = await stream.ReadAsync(buf, 0, buf.Length, rcts.Token);
                                    if (n <= 0) break;
                                    if (firstByte < 0) firstByte = swPlay.Elapsed.TotalSeconds;
                                    Interlocked.Add(ref bytes, n);
                                    an.Feed(buf, 0, n);
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception) { }
                    });
                    while (!rcts.IsCancellationRequested)
                    {
                        try { await Task.Delay(1000, rcts.Token); } catch (OperationCanceledException) { break; }
                        long b = Interlocked.Read(ref bytes);
                        if (b > lastBucketBytes) dataSeconds++;
                        lastBucketBytes = b;
                    }
                    try { await reader; } catch (Exception) { }
                }
                var li = session.LiveInfo;
                var f = session.Funnel;
                row.F = f;
                row.PiecesVerified = (int)li.PiecesVerified; row.SigFail = (int)li.SignatureFailures; row.Rehooks = li.Rehooks; row.HolesSkipped = li.HolesSkipped;
                row.PlayableSeconds = f.PlayableSeconds;
                row.StatsLine = session.DebugInfo;
            }
            finally
            {
                try { session.Dispose(); } catch (Exception) { }
            }
            row.Bytes = Interlocked.Read(ref bytes);
            row.FirstByteSeconds = firstByte;
            row.SecondsWithData = dataSeconds;
            row.MbitPerSec = swPlay.Elapsed.TotalSeconds > 0 ? row.Bytes * 8.0 / 1e6 / swPlay.Elapsed.TotalSeconds : 0;
            row.TsPackets = an.Packets; row.TsSyncLoss = an.SyncLosses; row.TsTransportErrors = an.TransportErrors; row.TsCcErrors = an.CcErrors.Values.Sum();
            row.TrackerCandidates = row.F.CandidatesBySource.ContainsKey("tracker") ? row.F.CandidatesBySource["tracker"] : 0;
            if (row.F.CandidatesBySource.ContainsKey("dht")) row.DhtCandidates = Math.Max(row.DhtCandidates, row.F.CandidatesBySource["dht"]);
            Classify(row, seconds);
        }

        /// <summary>The channel "plays" if the first byte arrived and the stream then kept arriving (>= 60 % of the remaining seconds with data and at least 1 MB).</summary>
        private static void Classify(Row r, double seconds)
        {
            var f = r.F;
            double after = seconds - Math.Max(0, r.FirstByteSeconds);
            // It plays if the first byte arrived and the stream kept at >= 80 % of the channel's bitrate during the rest of the window (the prebuffer burst counts in its favor)
            r.Played = r.FirstByteSeconds >= 0 && r.Bytes >= 0.8 * r.BitrateBytes * Math.Max(5, after) && r.TsSyncLoss == 0;
            if (r.Played) { r.FailClass = ""; return; }
            if (f == null) { r.FailClass = "our_error"; return; }
            // data arrives but it is not MPEG-TS (the TV's player rejects it with "Failed to prepare"): it is not a peers problem
            if (r.FirstByteSeconds >= 0 && r.Bytes >= 500 * 1000 && (r.TsSyncLoss > 0 || r.TsPackets == 0)) { r.FailClass = "content_not_ts"; r.FailDetail = "bytes=" + r.Bytes + " ts_packets=" + r.TsPackets + " sync_losses=" + r.TsSyncLoss; return; }
            if (f.Candidates == 0) { r.FailClass = "no_peers"; r.FailDetail = ""; return; }
            if (f.TcpOk == 0)
            {
                int to = f.CloseClasses.ContainsKey("tcp_timeout") ? f.CloseClasses["tcp_timeout"] : 0;
                int rf = f.CloseClasses.ContainsKey("tcp_refused") ? f.CloseClasses["tcp_refused"] : 0;
                r.FailClass = f.Tried == 0 ? "no_attempts" : "tcp_no_connection"; r.FailDetail = "tried=" + f.Tried + " timeout=" + to + " refused=" + rf;
                return;
            }
            if (f.HandshakeOk == 0) { r.FailClass = "closed_in_handshake"; r.FailDetail = "tcp=" + f.TcpOk; return; }
            if (f.ExtendOk == 0) { r.FailClass = "closed_after_handshake"; r.FailDetail = "hs=" + f.HandshakeOk; return; }
            if (f.HaveSeen == 0)
            {
                int cl = f.CloseClasses.ContainsKey("after_extend") ? f.CloseClasses["after_extend"] : 0;
                r.FailClass = cl > 0 ? "closed_after_extend" : "no_pieces"; r.FailDetail = "ext=" + f.ExtendOk + " after_extend=" + cl; return;
            }
            if (f.RecentHave == 0) { r.FailClass = "no_recent_pieces"; r.FailDetail = "have=" + f.HaveSeen; return; }
            bool advancing = f.LivePosAdvances > 0 && f.LivePosLastChangeSeconds >= f.ElapsedSeconds - 20;
            if (!advancing) { r.FailClass = "sender_stopped"; r.FailDetail = "advances=" + f.LivePosAdvances + " last_change=" + f.LivePosLastChangeSeconds.ToString("0.0", Inv) + "s"; return; }
            if (f.Unchoked == 0) { r.FailClass = "no_unchoke"; r.FailDetail = "recent=" + f.RecentHave; return; }
            if (f.Served == 0) { r.FailClass = "no_service"; r.FailDetail = "unchoke=" + f.Unchoked; return; }
            if (r.FirstByteSeconds < 0) { r.FailClass = r.PlayableSeconds < 0 ? "prebuffer_incomplete" : "our_error"; r.FailDetail = r.PlayableSeconds < 0 ? f.Served + " peers serve but the prebuffer pieces do not arrive in time" : "pieces and prebuffer but no first byte"; return; }
            r.FailClass = "insufficient_flow"; r.FailDetail = "bytes=" + r.Bytes + " secs_with_data=" + r.SecondsWithData + " sync=" + r.TsSyncLoss;
        }

        // ---------- CSV ----------

        private static string CsvHeader()
        {
            return string.Join(";", "pass", "mode", "content_id", "infohash", "name", "group", "probe_state", "scrape_seeds", "scrape_peers", "scrape_trackers",
                "dht_candidates", "tracker_candidates", "cand_total", "cand_by_source",
                "resolved", "resolve_s", "resolve_source", "resolve_fail", "good_peers", "res_cand", "res_attempts", "res_conn_timeouts", "res_refused", "res_handshakes", "res_closed",
                "tried", "tcp_ok", "hs_ok", "ext_ok", "have_seen", "recent_have", "unchoked", "served", "incoming", "incoming_hs", "accessible_peers", "node_types",
                "first_tcp_s", "first_hs_s", "first_ext_s", "first_have_s", "first_unchoke_s", "first_piece_s", "livepos_first_s", "playable_s",
                "livepos_first", "livepos_last", "livepos_advances", "livepos_last_change_s",
                "bitrate_kbps", "first_byte_s", "bytes", "mbit_s", "secs_with_data", "ts_packets", "ts_sync_loss", "ts_cc_err", "pieces_ok", "sig_fail", "rehooks", "holes",
                "played", "fail_class", "fail_detail", "close_classes", "by_source", "elapsed_s",
                "lp_verdict", "lp_active", "lp_sat", "lp_est", "lp_cand", "lp_tried", "lp_tcp", "lp_hs", "lp_ext", "lp_recent", "lp_adv", "lp_s");
        }

        private static string D(double v) { return v < 0 ? "" : v.ToString("0.0", Inv); }

        private static string CsvLine(Row r)
        {
            var f = r.F ?? new LiveFunnel();
            string candSrc = string.Join(",", f.CandidatesBySource.Select(k => k.Key + ":" + k.Value));
            string closes = string.Join(",", f.CloseClasses.OrderByDescending(k => k.Value).Select(k => k.Key + ":" + k.Value));
            string bySrc = string.Join(",", f.BySource.Select(k => k.Key + ":" + string.Join("/", k.Value)));
            string nts = string.Join(",", f.NodeTypes.Select(k => "nt" + k.Key + ":" + k.Value));
            return string.Join(";", r.Pass, r.Mode, r.ContentId, r.InfoHash, Q(r.Name), Q(r.Group), r.ProbeState, r.ScrapeSeeds, r.ScrapePeers, Q(r.ScrapeTrackers),
                r.DhtCandidates < 0 ? "" : r.DhtCandidates.ToString(Inv), r.TrackerCandidates < 0 ? "" : r.TrackerCandidates.ToString(Inv), f.Candidates, Q(candSrc),
                r.Resolved ? 1 : 0, D(r.ResolveSeconds), r.ResolveSource, Q(r.ResolveFail), r.GoodPeers, r.ResCandidates, r.ResAttempts, r.ResConnTimeouts, r.ResRefused, r.ResHandshakes, r.ResClosed,
                f.Tried, f.TcpOk, f.HandshakeOk, f.ExtendOk, f.HaveSeen, f.RecentHave, f.Unchoked, f.Served, f.Incoming, f.IncomingHandshakeOk, f.Accessible, Q(nts),
                D(f.FirstTcpSeconds), D(f.FirstHandshakeSeconds), D(f.FirstExtendSeconds), D(f.FirstHaveSeconds), D(f.FirstUnchokeSeconds), D(f.FirstPieceSeconds), D(f.LivePosFirstSeconds), D(r.PlayableSeconds),
                f.LivePosFirst, f.LivePosLast, f.LivePosAdvances, D(f.LivePosLastChangeSeconds),
                (r.BitrateBytes * 8 / 1000).ToString(Inv), D(r.FirstByteSeconds), r.Bytes, r.MbitPerSec.ToString("0.00", Inv), r.SecondsWithData, r.TsPackets, r.TsSyncLoss, r.TsCcErrors, r.PiecesVerified, r.SigFail, r.Rehooks, r.HolesSkipped,
                r.Played ? 1 : 0, r.FailClass, Q(r.FailDetail), Q(closes), Q(bySrc), D(f.ElapsedSeconds),
                r.Lp == null ? "" : r.Lp.Verdict.ToString(), r.Lp == null ? "" : r.Lp.ActivePeers.ToString(Inv), r.Lp == null ? "" : (r.Lp.Saturated ? "1" : "0"), r.Lp == null ? "" : r.Lp.EstimatedConnectable.ToString(Inv),
                r.Lp == null ? "" : r.Lp.Candidates.ToString(Inv), r.Lp == null ? "" : r.Lp.Tried.ToString(Inv), r.Lp == null ? "" : r.Lp.TcpOk.ToString(Inv), r.Lp == null ? "" : r.Lp.HandshakeOk.ToString(Inv),
                r.Lp == null ? "" : r.Lp.ExtendOk.ToString(Inv), r.Lp == null ? "" : r.Lp.WithRecentPieces.ToString(Inv), r.Lp == null ? "" : r.Lp.Advances.ToString(Inv), r.Lp == null ? "" : D(r.Lp.Seconds));
        }

        private static string Q(string s)
        {
            if (s == null) return "";
            return s.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string PeerHeader()
        {
            return string.Join(";", "pass", "mode", "content_id", "endpoint", "source", "incoming", "attempt", "stage", "close_class", "close_reason", "tcp_ms", "hs_ms", "ext_ms", "have_ms", "unchoke_ms", "piece_ms", "closed_ms",
                "remote_proto", "v", "nt", "pv", "platform", "tt", "signed", "peer_type", "accessible", "head_claim", "max_have", "have_msgs", "bytes", "pieces");
        }

        private static string PeerLine(Row r, LivePeerTrace t)
        {
            return string.Join(";", r.Pass, r.Mode, r.ContentId, t.EndPoint, t.Source, t.Incoming ? 1 : 0, t.Attempt, (int)t.Stage + ":" + t.Stage, t.CloseClass, Q(t.CloseReason),
                Rel(t.TcpAbs, t.StartAbs), Rel(t.HandshakeAbs, t.StartAbs), Rel(t.ExtendAbs, t.StartAbs), Rel(t.HaveAbs, t.StartAbs), Rel(t.UnchokeAbs, t.StartAbs), Rel(t.PieceAbs, t.StartAbs), Rel(t.ClosedAbs, t.StartAbs),
                t.RemoteProtocol, t.V, t.Nt, t.Pv, t.Platform, t.Tt, t.HadSignature ? 1 : 0, t.MiPeerType, t.Accessible, t.HeadClaim, t.MaxHave, t.HaveMessages, t.Bytes, t.Pieces);
        }

        private static string Rel(long abs, long start) { return abs < 0 ? "" : (abs - start).ToString(Inv); }

        // ---------- summary ----------

        private static void Summary(List<Row> rows)
        {
            foreach (var mode in rows.Select(r => r.Mode).Distinct())
            {
                var rs = rows.Where(r => r.Mode == mode).ToList();
                Console.WriteLine("---- " + mode + ": " + rs.Count + " channels, playing " + rs.Count(r => r.Played) + " ----");
                foreach (var g in rs.GroupBy(r => r.Played ? "ok" : r.FailClass).OrderByDescending(g => g.Count()))
                    Console.WriteLine("  " + g.Key.PadRight(24) + g.Count());
            }
        }
    }
}
