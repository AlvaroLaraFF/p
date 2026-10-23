using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cli
{
    internal static class LivePlay
    {
        private static string Fmt(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSec / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
        }

        public static string StatusLine(LiveSession s)
        {
            var st = s.Stats;
            var li = s.LiveInfo;
            return "[" + s.State + "] peers=" + st.Peers + " (unchoked " + li.Unchoked + ", cand " + li.Candidates + ")"
                + " down=" + Fmt(st.DownloadBytesPerSec) + " up=" + Fmt(st.UploadBytesPerSec)
                + " live=" + li.LivePos + " pos=" + li.OutPiece + " lag=" + li.LagSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                + " buffer=" + st.BufferPercent + "% pieces=" + li.StoredPieces + "+" + li.ActivePieces
                + " signatures ok/fail=" + li.PiecesVerified + "/" + li.SignatureFailures + (li.AcceptingUnverified ? " (UNVERIFIED)" : "")
                + " holes=" + li.HolesSkipped + " rehooks=" + li.Rehooks;
        }

        /// <summary>Shows the state every second and, if requested, reads the local URL for 'seconds' s and analyzes the received TS stream.</summary>
        public static async Task<int> RunAsync(LiveSession session, string url, int seconds, bool analyze, string saveTs, CancellationToken ct, bool realTime = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var an = new TsAnalyzer();
            Task reader = Task.CompletedTask;
            long firstByteMs = -1;
            long bytes = 0;
            using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (analyze)
                {
                    reader = Task.Run(async () =>
                    {
                        try
                        {
                            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, rcts.Token))
                            {
                                Console.WriteLine("HTTP " + (int)resp.StatusCode + " " + resp.Content.Headers.ContentType + " transfer-encoding=" + string.Join(",", resp.Headers.TransferEncoding.Select(x => x.Value)));
                                using (var stream = await resp.Content.ReadAsStreamAsync())
                                using (var file = saveTs != null ? File.Create(saveTs) : null)
                                {
                                    var buf = new byte[64 * 1024];
                                    while (!rcts.IsCancellationRequested)
                                    {
                                        int n = await stream.ReadAsync(buf, 0, buf.Length, rcts.Token);
                                        if (n <= 0) break;
                                        if (firstByteMs < 0) firstByteMs = sw.ElapsedMilliseconds;
                                        bytes += n;
                                        an.Feed(buf, 0, n);
                                        if (file != null) file.Write(buf, 0, n);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Console.Error.WriteLine("HTTP reader: " + ex.Message); }
                    });
                }
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(1000, ct);
                        Console.WriteLine(StatusLine(session) + (AceLog.MinLevel == LogLevel.Debug ? "  {" + session.DebugInfo + "}" : ""));
                        if (seconds > 0 && sw.Elapsed.TotalSeconds >= seconds) break;
                    }
                }
                catch (OperationCanceledException) { }
                rcts.Cancel();
                try { await reader; } catch (OperationCanceledException) { }
            }
            if (analyze)
            {
                Console.WriteLine("First byte at " + (firstByteMs < 0 ? "(never)" : (firstByteMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s") + "; received: " + an.Summary());
                if (bytes == 0) return 1;
                return an.SyncLosses == 0 && an.Packets > 0 ? 0 : 1;
            }
            return 0;
        }
    }
}
