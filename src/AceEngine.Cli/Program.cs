using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Cli
{
    internal static class Program
    {
        private const string Usage = @"ace - PC test tool for the C# Ace Stream engine (phase 1, milestones 1-7)

A bare 40-hex id and acestream://<40hex> are a content_id. For a BitTorrent infohash: magnet:, acestream:?infohash=
or the --infohash option.

Usage:
  ace resolve <link>                           Resolves the link to its transport file (content_id: P2P and TSService in parallel;
                                               infohash/magnet: P2P, the peer decides whether it is an Ace live stream (full descriptor) or a VOD (BEP9);
                                               file/URL: it is loaded) and prints type, infohash, content_id and files (get_media_files).
  ace play <link> [--index N] [--quality N]    Downloads over P2P (VOD or live) and prints the local URL and the state every second.
  ace verify <link> [--index N]                play (VOD) + automatic HTTP check (container headers, piece SHA-1, seek).
  ace serve <file>                             Serves a local file through the HTTP server (milestone 3 test).
  ace dht <hash>                               Peer search over the DHT (diagnostics).
  ace simlive [--minutes N] [--speed X] [--bitrate-kbps K] [--sources N] [--serve-only] [--save F.acelive]
                                               SIMULATED live stream (the sender in a local loop) read through the local URL at playback pace,
                                               checking there are no cuts. With --serve-only it prints the URL so it can be opened in VLC.
  ace list <code>                              mylinkspaste list (DNS TXT of <code>.elcano.top) with seeds and peers, sorted.
  ace m3u <file|url>                           Imports an M3U/M3U8 list and probes the links the same way as `list`.
  ace measure <list.m3u>                       MEASUREMENT of each channel's reality: scrape, DHT, descriptor, peer funnel, live position, first byte; CSV.
  ace peer-test host:port[,..] <infohash>      Connects to specific peers of a live stream with a custom EXTEND (--nt --peer-type --platform --pv --engine-version --tt --accessible --live-window --unsigned) and counts what they do.
  ace alive --source lists|search|both --label L --data DIR --out PREFIX [--seconds 40] [--workers 1] [--chunk 12] [--sample N] [--control 12] [--ages] [--check-only] [--loop SECONDS [--loop-play N]]
                                               Measures the accuracy of ""the app shows it as live -> it plays"": the real LiveChecker over the lists or the central search and real opening of the channels.
  ace idle [--source none|lists|search|both] [--seconds 60] [--warmup 15] [--no-dht] [--no-listen] [--no-upload] [--no-engine] [--retry-concurrency N] [--cycle-interval S] [--csv F]
                                               Engine CPU at rest with the TV's profile: process CPU, GC, threads, sockets and per-thread breakdown.
  ace cast <link> [--target IP] [--delivery hls|ts] [--seconds N] [--lan] [--profile cc2|cc3|gtv] [--check-only] [--segment-seconds S] [--window N]
                                               Opens the channel and serves it on the LAN like the app does when sending to a Chromecast (HLS) or a DLNA TV (progressive TS): prints the compatibility
                                               verdict and the URL (with its secret token). Only --target (this PC by default) may connect. For ffprobe / gst-launch / VLC checks.
  ace cast-sample --lists code1,code2 [--max N] [--concurrency 2] [--per-channel 40] [--out F.csv]
                                               Of the live channels of the lists, how many a Chromecast could take (H.264 + AAC) and why the rest cannot.
  ace dlna-scan [--seconds S] [--only IP]      Finds the UPnP/DLNA MediaRenderers (Smart TVs) of the network and asks them read-only questions (accepted formats, state). Plays nothing.
  ace probe <link>                             Health probe of a link: trackers (HTTP/UDP scrape), announce/DHT alternative, score.
  ace search <text> [--ace] [--m3u LIST] [--provider URL] [--explore KIND] [--max N]
                                               Search like the app's: channels from --m3u (local) and from the providers (--provider URL of a /search; --ace = Ace's central search).
      ace search <text> --ace --raw [--host URL] [--all-variants] [--page N] [--page-size N] [--timeout S]
                                               Raw signed request to the central search: HTTP status, body, response signature and decoded JSON (diagnostics).
  ace scrape <tracker> [infohash...] [--random N]   ONE batch scrape request (UDP up to 70, HTTP up to 50 infohashes) to a tracker; batch diagnostics.
      list/m3u options: --max N (probe only N)  --skip N (skip the first N)  --seconds N (maximum time, 300)  --all (also show dead and unknown)
      --no-group  --no-probe (list only)  --progress  --dns IP[,IP]  --no-doh  --doh-only  --no-announce-fallback  --no-dht-fallback
      Parallel probing (50 resolutions at a time by default; the count is grouped by tracker, in batches and throttled per host:port):
        --concurrency N          links resolved at once (content_id -> transport via DHT and peers); 50   (alias --probe-concurrency)
        --tracker-interval S     at most one request every S seconds to each tracker (host:port); 2
        --recheck S              a link at zero or without data is checked again after S seconds and only hidden if it is still at zero; 10 (0 = do not repeat)
        --stats                  at the end, a summary per tracker (requests, responses, timeouts, errors, rejections, latencies), of the DHT and of the resolution
        --recheck-unresolved     retry after --recheck the content_ids that nobody serves (by default not: with guaranteed resources it recovers none)
        --udp-batch N / --http-batch N   infohashes per request (70 / 50)     --tracker-concurrent 1|2   simultaneous requests to the same tracker
        --dht-inflight N         DHT queries in flight at most (400; each resolution uses 8)
        --resolve-conns N        simultaneous TCP connections of EACH resolution (15)      --resolve-budget N   connections of all the resolutions (750)
                                 resolutions admitted at once = min(--concurrency, budget / conns, dht-inflight / 8); the others wait their turn without spending their deadline
        --connect-timeout S / --handshake-timeout S   deadlines with each peer when resolving (engine: 8 / 15)      --stats-csv FILE   one line per resolution
        --ts-service             also use TSService when resolving (by default not: it is 4 requests per link to the same hosts)

resolve/play/verify options:
  --infohash           a bare 40-hex id is an infohash and not a content_id
  --index N            file inside the torrent (position in info['files'], like get_media_files)
  --quality N          quality of a multi-quality transport
  --timeout N          maximum resolution seconds (20)
  --no-p2p             do not resolve the content_id over P2P      --no-tsservice   do not use TSService
  --static-peer H:P    fixed peer (several separated by commas; debugging)
  --cache FILE         persistent cache of resolutions (JSON)
  --save FILE          (resolve, play) saves the transport file as it was received
  --json               (resolve) prints the get_media_files response as JSON     --dump  includes transport_file_data
  --max-buffer-mib N   RAM buffer (64 by default)
  --no-listen          no listening port     --no-dht   no DHT      --no-trackers   no trackers
  --live-mode chunked|close   transfer mode for live streams
  --seconds N          stop after N seconds (play)
  --analyze            (play of a live stream) reads the local URL and analyzes the received MPEG-TS stream (sync, continuity, PAT/PMT)
  --save-ts FILE       (with --analyze) saves the received stream
  --no-upload          do not upload anything (UploadSlots=0; PING is still answered)    --upload-slots N
  --sig strict|tolerant|off   verification of the pieces' RSA signature (tolerant by default)
  --no-sign-extend     do not sign the EXTEND with ed25519       --log-live   logs all the live control messages (-v/--debug)
  --read-ahead N       (verify) seconds of read-ahead of the simulated player (3)
  --seeks N            (verify) random seeks with latency measurement (5 by default)
  --simulate-seconds N / --bitrate-kbps N   (verify) paced simulated playback (20 s at 8000 kbit/s by default)
  --verbose / -v       detailed log on stderr   --debug     debug log
";

        private static int Main(string[] args)
        {
            try
            {
                return Run(args).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return 130; }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (Environment.GetEnvironmentVariable("ACE_TRACE") == "1") Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private sealed class Args
        {
            public string Command;
            public List<string> Positional = new List<string>();
            public Dictionary<string, string> Options = new Dictionary<string, string>();
            public bool Has(string k) { return Options.ContainsKey(k); }
            public string Get(string k, string d = null) { string v; return Options.TryGetValue(k, out v) ? v : d; }
            public int GetInt(string k, int d) { string v; int r; return Options.TryGetValue(k, out v) && int.TryParse(v, out r) ? r : d; }
        }

        private static Args Parse(string[] argv)
        {
            var a = new Args();
            var flags = new HashSet<string> { "--no-listen", "--no-dht", "--no-trackers", "--verbose", "-v", "--debug", "--no-pex", "--infohash", "--no-p2p", "--no-tsservice", "--json", "--dump", "--no-sign-extend", "--serve-only", "--all", "--no-group", "--no-probe", "--progress", "--no-doh", "--doh-only", "--no-announce-fallback", "--no-dht-fallback", "--stats", "--recheck-unresolved", "--ts-service", "--no-upload", "--analyze", "--log-live", "--full", "--unsigned", "--no-yourip", "--full-backoff", "--ban-non-ace", "--peer-hints", "--claim-accessible", "--advertise-cap", "--hedge", "--dht-last", "--probe", "--twice", "--tv-profile", "--legacy-live", "--check-only", "--ages", "--legacy-alive", "--compare-legacy", "--no-engine", "--stop-checker", "--resting", "--lan", "--no-checker", "--force-target", "--video" };
            foreach (var f in SearchCommands.Flags) flags.Add(f);
            for (int i = 0; i < argv.Length; i++)
            {
                var s = argv[i];
                if (a.Command == null) { a.Command = s; continue; }
                if (s.StartsWith("--") || s == "-v")
                {
                    if (flags.Contains(s)) a.Options[s] = "1";
                    else if (i + 1 < argv.Length) a.Options[s] = argv[++i];
                    else throw new ArgumentException("missing value for " + s);
                }
                else a.Positional.Add(s);
            }
            return a;
        }

        private static async Task<int> Run(string[] argv)
        {
            if (argv.Length == 0 || argv[0] == "-h" || argv[0] == "--help" || argv[0] == "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var a = Parse(argv);
            ConfigureLog(a);
            switch (a.Command)
            {
                case "resolve": return await Resolve(a);
                case "serve": return await Serve(a);
                case "play": return await Play(a, false);
                case "verify": return await Play(a, true);
                case "dht": return await DhtCmd(a);
                case "simlive": return await SimLiveCmd(a);
                case "list": return await ListCmd(a);
                case "m3u": return await M3uCmd(a);
                case "probe": return await ProbeCmd(a);
                case "scrape": return await ScrapeCmd(a);
                case "search": return await SearchCmd(a);
                case "measure": return await MeasureCmd(a);
                case "alive": return await AliveCmd(a);
                case "idle": return await IdleCmd(a);
                case "cast": return await CastCmd(a);
                case "cast-sample": return await CastSampleCmd(a);
                case "dlna-scan": return await DlnaScanCmd(a);
                case "peer-test": return await PeerTest.RunAsync(a.Positional, BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), CancellationToken.None);
                default:
                    Console.Error.WriteLine("unknown command: " + a.Command + "\n" + Usage);
                    return 2;
            }
        }

        private static async Task<int> CastCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await CastCommands.RunAsync(a.Positional, BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), x => AceLink.Parse(x, PlainHexKind(a)), cts.Token);
        }

        private static async Task<int> DlnaScanCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await DlnaCommands.ScanAsync((k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> CastSampleCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await CastCommands.SampleAsync(BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> AliveCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await Alive.RunAsync((k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> IdleCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await IdleCpu.RunAsync((k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> SearchCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            string text = a.Positional.Count > 0 ? string.Join(" ", a.Positional) : null;
            return await SearchCommands.Run(text, k => a.Get(k), k => a.Has(k), (k, d) => a.GetInt(k, d), cts.Token);
        }

        private static void ConfigureLog(Args a)
        {
            if (a.Has("--verbose") || a.Has("-v") || a.Has("--debug"))
            {
                AceLog.MinLevel = a.Has("--debug") ? LogLevel.Debug : LogLevel.Info;
                var t0 = DateTime.UtcNow;
                AceLog.Sink = (lvl, tag, msg) =>
                    Console.Error.WriteLine("[" + (DateTime.UtcNow - t0).TotalSeconds.ToString("000.0", CultureInfo.InvariantCulture) + " " + lvl.ToString().Substring(0, 1) + " " + tag + "] " + msg);
            }
            else
            {
                AceLog.MinLevel = LogLevel.Warn;
                AceLog.Sink = (lvl, tag, msg) => Console.Error.WriteLine("[" + lvl + " " + tag + "] " + msg);
            }
        }

        private static EngineOptions BuildOptions(Args a)
        {
            var o = new EngineOptions();
            if (a.Has("--no-listen")) o.EnableListener = false;
            if (a.Has("--no-dht")) o.EnableDht = false;
            if (a.Has("--no-trackers")) o.EnableTrackers = false;
            if (a.Has("--no-pex")) o.EnablePex = false;
            if (a.Has("--dht-nodes")) o.DhtNodesPath = a.Get("--dht-nodes");
            if (a.Has("--no-p2p")) o.Resolver.EnableP2p = false;
            if (a.Has("--no-tsservice")) o.Resolver.EnableTsService = false;
            if (a.Has("--timeout")) o.Resolver.Timeout = TimeSpan.FromSeconds(a.GetInt("--timeout", 20));
            if (a.Has("--cache")) o.Resolver.CachePath = a.Get("--cache");
            if (a.Has("--dump-dir")) o.Resolver.DumpDir = a.Get("--dump-dir");
            if (a.Has("--static-peer")) foreach (var sp in a.Get("--static-peer").Split(',')) if (sp.Trim().Length > 0) o.StaticPeers.Add(sp.Trim());
            if (a.Has("--no-upload")) { o.Live.UploadSlots = 0; o.UploadSlots = 0; }
            if (a.Has("--upload-slots")) { o.Live.UploadSlots = a.GetInt("--upload-slots", 4); }
            if (a.Has("--log-live")) o.Live.LogAllMessages = true;
            if (a.Has("--sig")) { var sg = a.Get("--sig"); o.Live.Signature = sg.StartsWith("s") ? AceEngine.Live.SignatureMode.Strict : sg.StartsWith("o") ? AceEngine.Live.SignatureMode.Off : AceEngine.Live.SignatureMode.Tolerant; }
            if (a.Has("--no-sign-extend")) o.Live.SignExtend = false;
            LiveTuning.Apply(o, (k, d) => a.Get(k, d), k => a.Has(k));
            if (a.Has("--max-buffer-mib")) o.MaxBufferBytes = a.GetInt("--max-buffer-mib", 64) * 1024L * 1024;
            var lm = a.Get("--live-mode");
            if (lm != null) o.Http.LiveTransfer = lm.StartsWith("close", StringComparison.OrdinalIgnoreCase) ? LiveTransfer.CloseDelimited : LiveTransfer.Chunked;
            return o;
        }

        private static AceLinkKind PlainHexKind(Args a)
        {
            return a.Has("--infohash") ? AceLinkKind.InfoHash : AceLinkKind.ContentId;
        }

        // ---------- ace resolve ----------

        private static async Task<int> Resolve(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace resolve <link>"); return 2; }
            var link = AceLink.Parse(a.Positional[0], PlainHexKind(a));
            Console.WriteLine("Link: " + link.Kind + " " + link.Value);
            var opt = BuildOptions(a);
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            bool network = link.Kind == AceLinkKind.ContentId || link.Kind == AceLinkKind.InfoHash || link.Kind == AceLinkKind.Magnet;
            DhtClient dht = null;
            try
            {
                if (network && opt.EnableDht) { dht = new DhtClient(opt); dht.Start(); }
                using (var resolver = new TransportResolver(opt, dht))
                {
                    ResolvedTransport res;
                    try { res = await resolver.ResolveAsync(link, cts.Token); }
                    catch (ResolveException ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
                    var tf = res.Transport;
                    Console.WriteLine("Source:     " + res.Source + " (" + res.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
                    if (a.Has("--save"))
                    {
                        File.WriteAllBytes(a.Get("--save"), tf.RawBytes);
                        Console.WriteLine("Saved:      " + a.Get("--save") + " (" + tf.RawBytes.Length + " bytes)");
                    }
                    PrintTransport(tf, res.HasContentId, res.RequestedContentId);
                    var mopt = new MediaFilesOptions { DumpTransportFile = a.Has("--dump") };
                    if (a.Has("--json")) Console.WriteLine(MediaFiles.Get(tf, mopt).ToJson(true));
                    return 0;
                }
            }
            finally
            {
                if (dht != null) dht.Dispose();
            }
        }

        private static void PrintTransport(TransportFile tf, bool hasContentId = true, string requestedContentId = null)
        {
            Console.WriteLine("Format:     " + tf.Format + (tf.IsNewFormat ? " (AceStreamTransport)" : ""));
            if (tf.Format != TransportFormat.PlainBencode) Console.WriteLine("IV used:    " + tf.IvUsed + (tf.PaddingWasValid ? "" : " (padding not PKCS#7)"));
            Console.WriteLine("Type:       " + tf.Type + (tf.IsLive ? " (live)" : " (VOD)"));
            Console.WriteLine("Name:       " + tf.Name);
            if (!hasContentId) Console.WriteLine("content_id: (not applicable: resolved by infohash)");
            else if (requestedContentId != null && requestedContentId != tf.ContentId)
                Console.WriteLine("content_id: " + requestedContentId + "  (the SHA-1 of the received file is " + tf.ContentId + ")");
            else Console.WriteLine("content_id: " + tf.ContentId);
            Console.WriteLine("infohash:   " + (tf.InfoHashHex ?? "(unknown for this type)"));
            if (tf.IsMulti)
            {
                for (int i = 0; i < tf.Qualities.Count; i++)
                {
                    var q = tf.Qualities[i];
                    Console.WriteLine("  quality " + i + ": " + q.Name + (q.Data == null ? " (not interpretable)" : " infohash " + q.Data.InfoHashHex + " bitrate " + (q.Data.Live != null ? q.Data.Live.Bitrate.ToString() : "?")));
                }
                return;
            }
            if (tf.Torrent != null)
            {
                var t = tf.Torrent;
                Console.WriteLine("Pieces:     " + t.PieceCount + " x " + t.PieceLength + " B  (total " + t.TotalLength + " B)");
                foreach (var tier in t.GetTrackerTiers()) Console.WriteLine("Trackers:   " + string.Join(" , ", tier));
            }
            if (tf.Live != null)
            {
                var lv = tf.Live;
                Console.WriteLine("Live:       authmethod=" + lv.AuthMethod + " piece_length=" + lv.PieceLength + " chunk_length=" + lv.ChunkLength + " bitrate=" + lv.Bitrate + " B/s ("
                    + (lv.Bitrate * 8 / 1000) + " kbit/s)" + (lv.Quality != null ? " quality=" + lv.Quality : "") + (lv.Categories.Count > 0 ? " categories=" + string.Join(",", lv.Categories) : "")
                    + (lv.Permanent ? " permanent" : "") + (lv.IsPrivate ? " private" : ""));
                if (lv.Trackers.Count > 0) Console.WriteLine("Trackers:   " + string.Join(" , ", lv.Trackers));
                if (lv.MetaTrackers.Count > 0) Console.WriteLine("Meta-trackers: " + string.Join(" , ", lv.MetaTrackers));
                if (lv.StartupNodes.Count > 0) Console.WriteLine("Startup nodes: " + string.Join(" , ", lv.StartupNodes));
            }
            var mf = MediaFiles.Get(tf);
            Console.WriteLine("Playable files (" + mf.Type + ", " + mf.TransportType + "):");
            foreach (var f in mf.Files)
                Console.WriteLine("  [" + f.Index + "] " + f.Filename + "  " + f.Mime + "  " + f.Size + " B");
        }

        // ---------- ace serve ----------

        private static async Task<int> Serve(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace serve <file>"); return 2; }
            using (var srv = new LocalHttpServer(BuildOptions(a).Http))
            {
                srv.Start();
                var session = new LocalFileSession(a.Positional[0]);
                Console.WriteLine(srv.Register(session));
                Console.WriteLine("Ctrl+C to stop.");
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
            }
            return 0;
        }

        // ---------- ace list / m3u / probe (milestone 7) ----------

        private static async Task<int> ListCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace list <code>"); return 2; }
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await ListCommands.ListAsync(a.Positional[0], BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> ScrapeCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace scrape <udp://host:port/announce | http://host/announce> [infohash...] [--random N]"); return 2; }
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await ListCommands.ScrapeAsync(a.Positional[0], a.Positional.Skip(1).ToList(), a.GetInt("--random", 0), cts.Token);
        }

        private static async Task<int> MeasureCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace measure <list.m3u> [--ids FILE] [--max N] [--skip N] [--seconds 45] [--warm-seconds 30] [--concurrency 5] [--mode cold|warm|both] [--label X] [--out F.csv]"); return 2; }
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await Measure.RunAsync(a.Positional[0], BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> M3uCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace m3u <file|url>"); return 2; }
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await ListCommands.M3uAsync(a.Positional[0], BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        private static async Task<int> ProbeCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace probe <link>"); return 2; }
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            return await ListCommands.ProbeAsync(a.Positional[0], BuildOptions(a), (k, d) => a.Get(k, d), k => a.Has(k), cts.Token);
        }

        // ---------- ace simlive ----------

        private static async Task<int> SimLiveCmd(Args a)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            var opt = BuildOptions(a);
            opt.EnableListener = false; opt.EnableDht = false; opt.EnableTrackers = false;
            return await SimLive.RunAsync(null, (k, d) => a.Get(k, d), k => a.Has(k), opt, cts.Token);
        }

        // ---------- ace dht ----------

        private static async Task<int> DhtCmd(Args a)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace dht <infohash|magnet>"); return 2; }
            var link = AceLink.Parse(a.Positional[0], AceLinkKind.InfoHash);
            byte[] ih = link.Magnet != null ? link.Magnet.InfoHash : Hex.Parse(link.Value);
            var opt = BuildOptions(a);
            using (var dht = new DhtClient(opt))
            {
                dht.Start();
                if (opt.DhtNodesPath != null) Console.WriteLine("saved nodes loaded: " + dht.LoadNodes(opt.DhtNodesPath));
                var found = new List<System.Net.IPEndPoint>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = await dht.GetPeersAsync(ih, ps => { lock (found) found.AddRange(ps); Console.WriteLine("  +" + ps.Count + " peers (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)"); },
                    TimeSpan.FromSeconds(a.GetInt("--seconds", 30)), CancellationToken.None);
                Console.WriteLine(n + " peers, " + dht.KnownNodes + " nodes, " + dht.QueriesSent + " queries, " + dht.ResponsesReceived + " responses, " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                if (opt.DhtNodesPath != null) { dht.SaveNodes(opt.DhtNodesPath); Console.WriteLine("nodes saved: " + dht.KnownNodes + " in " + opt.DhtNodesPath); }
                foreach (var p in found.Take(10)) Console.WriteLine("  " + p);
            }
            return 0;
        }

        // ---------- ace play / verify ----------

        private static string Fmt(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSec / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
        }

        private static string FmtBytes(long b)
        {
            if (b >= 1024L * 1024) return (b / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
            return (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KiB";
        }

        private static string StatusLine(IStreamSession s)
        {
            var st = s.Stats;
            return "[" + s.State + "] peers=" + st.Peers + " (seeds " + st.Seeds + ")"
                + " down=" + Fmt(st.DownloadBytesPerSec) + " up=" + Fmt(st.UploadBytesPerSec)
                + " buffer=" + st.BufferPercent + "% (" + FmtBytes(st.BufferedBytes) + ")"
                + " pieces=" + st.PiecesHave + "/" + st.PiecesTotal
                + " total=" + FmtBytes(st.DownloadedBytes);
        }

        private static async Task<int> Play(Args a, bool verify)
        {
            if (a.Positional.Count < 1) { Console.Error.WriteLine("usage: ace " + (verify ? "verify" : "play") + " <magnet|infohash|path.torrent> [--index N]"); return 2; }
            var link = AceLink.Parse(a.Positional[0], PlainHexKind(a));
            int? index = a.Has("--index") ? a.GetInt("--index", 0) : (int?)null;
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
            var opt = BuildOptions(a);

            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                OpenedStream opened;
                var swOpen = System.Diagnostics.Stopwatch.StartNew();
                try { opened = await host.OpenStreamAsync(link, new OpenOptions { FileIndex = index, QualityIndex = a.Has("--quality") ? a.GetInt("--quality", 0) : (int?)null }, cts.Token); }
                catch (NotSupportedException ex)
                {
                    Console.Error.WriteLine("not supported yet: " + ex.Message);
                    return 3;
                }
                catch (AceEngine.Resolve.ResolveException ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message + " (after " + swOpen.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
                    return 1;
                }
                Console.WriteLine("Opened in " + swOpen.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                var session = opened.Session;
                if (opened.IsDirect)
                {
                    if (opened.Transport != null) PrintTransport(opened.Transport, true, null);
                    Console.WriteLine("Direct URL (no engine): " + opened.Url);
                    return 0;
                }
                if (a.Has("--save") && opened.Transport != null)
                {
                    File.WriteAllBytes(a.Get("--save"), opened.Transport.RawBytes);
                    Console.WriteLine("Saved:      " + a.Get("--save") + " (" + opened.Transport.RawBytes.Length + " bytes)");
                }
                if (opened.Transport != null) PrintTransport(opened.Transport, link.Kind != AceLinkKind.InfoHash && link.Kind != AceLinkKind.Magnet, link.Kind == AceLinkKind.ContentId ? link.Value : null);
                Console.WriteLine("URL: " + opened.Url);
                Console.WriteLine("Peer port: " + host.ListenPort);

                var liveSession = session as AceEngine.Live.LiveSession;
                if (liveSession != null)
                {
                    int secs = a.GetInt("--seconds", 0);
                    var rc = await LivePlay.RunAsync(liveSession, opened.Url, secs, a.Has("--analyze"), a.Get("--save-ts"), cts.Token);
                    Console.WriteLine("Stopping...");
                    session.Dispose();
                    return rc;
                }
                var vod = (VodSession)session;
                if (verify)
                {
                    var vtask = Verifier.RunAsync(vod, opened.Url, cts.Token, a.GetInt("--simulate-seconds", 20), a.GetInt("--bitrate-kbps", 8000), a.GetInt("--seeks", 5), a.GetInt("--read-ahead", 3));
                    var ticker = Ticker(vod, cts.Token, a.GetInt("--seconds", 0));
                    var res = await vtask;
                    cts.Cancel();
                    try { await ticker; } catch (OperationCanceledException) { }
                    return res ? 0 : 1;
                }

                int seconds = a.GetInt("--seconds", 0);
                await Ticker(vod, cts.Token, seconds);
                Console.WriteLine("Stopping...");
                session.Dispose();
            }
            return 0;
        }

        private static async Task Ticker(VodSession vod, CancellationToken ct, int seconds)
        {
            bool announced = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    if (!announced && vod.Length.HasValue)
                    {
                        announced = true;
                        Console.WriteLine("Torrent: " + vod.TorrentName + "  file: [" + vod.File.Index + "] " + vod.FileName
                            + "  " + vod.Length.Value + " B  (" + vod.ContentType + ")");
                    }
                    Console.WriteLine(StatusLine(vod) + (AceLog.MinLevel == LogLevel.Debug ? "  {" + vod.DebugInfo + "}" : ""));
                    if (vod.State == SessionState.Failed)
                    {
                        Console.Error.WriteLine("the session has failed: " + (vod.Error == null ? "?" : vod.Error.Message));
                        return;
                    }
                    if (seconds > 0 && sw.Elapsed.TotalSeconds >= seconds) return;
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
