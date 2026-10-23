using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace simlive`: sets up a simulated live sender and one or several relay peers, opens the live stream with the engine and reads it
    /// through the local URL at playback pace, checking that the MPEG-TS stream has no cuts (the packets carry their number).
    /// </summary>
    internal static class SimLive
    {
        public static async Task<int> RunAsync(string[] args, Func<string, string, string> get, Func<string, bool> has, EngineOptions opt, CancellationToken ct)
        {
            double minutes = double.Parse(get("--minutes", "3"), CultureInfo.InvariantCulture);
            double speed = double.Parse(get("--speed", "1"), CultureInfo.InvariantCulture);
            int kbps = int.Parse(get("--bitrate-kbps", "2000"), CultureInfo.InvariantCulture);
            int pieceKib = int.Parse(get("--piece-kib", "256"), CultureInfo.InvariantCulture);
            int chunkKib = int.Parse(get("--chunk-kib", "16"), CultureInfo.InvariantCulture);
            int sources = int.Parse(get("--sources", "3"), CultureInfo.InvariantCulture);
            string save = get("--save", null);
            bool serveOnly = has("--serve-only");

            var sim = new LiveSimOptions
            {
                Name = "Canal simulado (ace simlive)", Bitrate = kbps * 1000 / 8, PieceLength = pieceKib * 1024, ChunkLength = chunkKib * 1024, TimeScale = speed
            };
            var model = new LiveStreamModel(sim);
            var srcs = new List<LiveSourceSimulator>();
            for (int i = 0; i < sources; i++)
                srcs.Add(new LiveSourceSimulator(model, new SimPeerBehavior { NodeType = i == 0 ? 2 : 1, LagPieces = i == 0 ? 0 : i * 2, ServeDelayMs = i == 0 ? 0 : 20 * i }));
            foreach (var s in srcs) opt.StaticPeers.Add(s.EndPoint.ToString());
            string file = save ?? Path.Combine(Path.GetTempPath(), "simlive-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".acelive");
            File.WriteAllBytes(file, model.TransportBytes);
            Console.WriteLine("Descriptor: " + file + "  infohash " + Hex.ToHex(model.InfoHash) + "  (" + kbps + " kbit/s, pieces of " + pieceKib + " KiB, " + model.PiecesPerSecond.ToString("0.00", CultureInfo.InvariantCulture) + " pieces/s)");
            Console.WriteLine("Simulated senders: " + string.Join(", ", srcs.Select(s => s.EndPoint.ToString())));

            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var opened = await host.OpenStreamAsync(AceLink.Parse(file), new OpenOptions(), ct);
                var session = (LiveSession)opened.Session;
                Console.WriteLine("URL: " + opened.Url);
                if (serveOnly)
                {
                    Console.WriteLine("Open the URL in a player (VLC). Ctrl+C to stop.");
                    try { while (!ct.IsCancellationRequested) { await Task.Delay(5000, ct); Console.WriteLine(LivePlay.StatusLine(session)); } } catch (OperationCanceledException) { }
                    foreach (var s in srcs) s.Dispose();
                    return 0;
                }

                var v = new SimStreamVerifier();
                double rate = sim.Bitrate * speed;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double maxStall = 0; long lastTotal = 0; var lastMove = System.Diagnostics.Stopwatch.StartNew();
                long maxLagMs = 0, minLagMs = long.MaxValue; long samples = 0;
                var statusTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(5000, ct);
                            var li = session.LiveInfo;
                            // Delay seen by the HTTP client: packets produced by the sender minus the last packet received (the engine's does not count
                            // what is already in the sockets' buffers)
                            if (li.Playable && v.LastPacket >= 0)
                            {
                                double produced = ((model.Head + 1 - model.FirstIndex) * (double)model.ContentSize - sim.StartMisalignment) / 188.0;
                                long ms = (long)((produced - v.LastPacket) * 188.0 / sim.Bitrate * 1000);
                                if (ms > maxLagMs) maxLagMs = ms;
                                if (ms < minLagMs) minLagMs = ms;
                                samples++;
                            }
                            Console.WriteLine("t=" + sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s  " + LivePlay.StatusLine(session) + "  received=" + (v.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB gaps=" + v.Gaps.Count + " max stall=" + maxStall.ToString("0.00", CultureInfo.InvariantCulture) + "s");
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                {
                    cts.CancelAfter(TimeSpan.FromMinutes(minutes));
                    try
                    {
                        using (var resp = await http.GetAsync(opened.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        {
                            var buf = new byte[32 * 1024];
                            long total = 0, burst = (long)(rate * 3);
                            var play = System.Diagnostics.Stopwatch.StartNew();
                            while (true)
                            {
                                int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                                if (n <= 0) break;
                                v.Feed(buf, 0, n);
                                total += n;
                                if (total != lastTotal) { double st = lastMove.Elapsed.TotalSeconds; if (lastTotal > 0 && st > maxStall) maxStall = st; lastMove.Restart(); lastTotal = total; }
                                if (total > burst)
                                {
                                    double ahead = (double)(total - burst) / rate - play.Elapsed.TotalSeconds;
                                    if (ahead > 0.005) await Task.Delay(TimeSpan.FromSeconds(ahead), cts.Token);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                var li2 = session.LiveInfo;
                double streamSeconds = v.Bytes / (double)sim.Bitrate;
                Console.WriteLine("---");
                Console.WriteLine("Real duration " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s; stream received " + streamSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s (" + (v.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB)");
                Console.WriteLine("TS packets " + v.Packets + ", sync losses " + v.SyncErrors + ", corrupt packets " + v.CorruptPackets + ", repeated " + v.Duplicates + ", gaps " + v.Gaps.Count + " (" + v.TotalLost + " packets)");
                Console.WriteLine("Maximum stall without receiving data: " + maxStall.ToString("0.00", CultureInfo.InvariantCulture) + " s; delay relative to live: " + (samples > 0 ? (minLagMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " - " + (maxLagMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" : "n/a"));
                Console.WriteLine("Pieces verified " + li2.PiecesVerified + ", signature failures " + li2.SignatureFailures + ", holes skipped " + li2.HolesSkipped + ", rehooks " + li2.Rehooks + ", peers " + li2.Peers);
                foreach (var s in srcs) if (s.Violations.Count > 0) Console.WriteLine("Client VIOLATIONS according to source " + s.Port + ": " + string.Join("; ", s.Violations));
                Console.WriteLine("Served by the sources: " + string.Join(", ", srcs.Select(s => s.Port + "=" + (s.BytesServed / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB")));
                session.Dispose();
                foreach (var s in srcs) s.Dispose();
                bool ok = v.SyncErrors == 0 && v.CorruptPackets == 0 && v.Duplicates == 0 && v.Gaps.Count == 0 && streamSeconds > minutes * 60 * 0.85 && srcs.All(s => s.Violations.Count == 0);
                Console.WriteLine(ok ? "RESULT: correct, no cuts" : "RESULT: THERE ARE PROBLEMS");
                return ok ? 0 : 1;
            }
        }
    }
}
