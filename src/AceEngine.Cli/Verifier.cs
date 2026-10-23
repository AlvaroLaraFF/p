using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace verify`: end-to-end check through the local URL (milestone 4). Independent of the engine's internal
    /// verification: the bytes that arrive over HTTP are hashed again with SHA-1 against the torrent's 'pieces'
    /// list, the container headers are checked and seeks are made (Range in the middle, backwards and at the end).
    /// </summary>
    internal static class Verifier
    {
        private sealed class Counters { public int Verified, Failed, Skipped; }

        public static async Task<bool> RunAsync(VodSession vod, string url, CancellationToken ct, int simulateSeconds = 20, int bitrateKbps = 8000, int seeks = 5, double readAhead = 3)
        {
            bool ok = true;
            Console.WriteLine("== verify: waiting for metadata...");
            await vod.WaitUntilReadyAsync(ct);
            var meta = vod.Meta;
            var file = vod.File;
            long len = file.Length;
            Console.WriteLine("== verify: " + file.Path + " (" + len + " B, pieces of " + meta.PieceLength + " B)");
            var c = new Counters();
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                // 1) HEAD
                var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
                bool headOk = head.IsSuccessStatusCode && head.Content.Headers.ContentLength == len
                    && head.Headers.AcceptRanges.Contains("bytes");
                ok &= Report("HEAD: 200, Content-Length=" + len + ", Accept-Ranges: bytes", headOk,
                    "status " + (int)head.StatusCode + " len " + head.Content.Headers.ContentLength);
                Console.WriteLine("   Content-Type: " + head.Content.Headers.ContentType);

                // 2) Start of the file: container headers + SHA-1 of the covered pieces
                long headBytes = Math.Min(len, Math.Max(2L * 1024 * 1024, meta.PieceLength * 2));
                var r = await GetRange(http, url, 0, headBytes - 1, ct, len);
                ok &= Report("Range 0-" + (headBytes - 1) + " (start) -> 206 in " + r.Millis + " ms", r.Ok, r.Error);
                if (r.Ok)
                {
                    ok &= ReportContainer(r.Data, file.Path);
                    VerifyPieces(meta, file, 0, r.Data, c);
                }

                if (len > 4L * 1024 * 1024)
                {
                    // 3) Jump to the middle of the file (aligned to the middle of a piece to exercise the inner offset)
                    long mid = len / 2 + 12345;
                    long size = Math.Min(len - mid, Math.Max(2L * 1024 * 1024, meta.PieceLength * 2));
                    var m = await GetRange(http, url, mid, mid + size - 1, ct, len);
                    ok &= Report("Range " + mid + "-" + (mid + size - 1) + " (jump to the middle) -> 206 in " + m.Millis + " ms", m.Ok, m.Error);
                    if (m.Ok) VerifyPieces(meta, file, mid, m.Data, c);

                    // 4) Jump backwards (the area may already have been evicted from RAM)
                    long back = len / 4 + 777;
                    long bsize = Math.Min(len - back, 1024 * 1024);
                    var b = await GetRange(http, url, back, back + bsize - 1, ct, len);
                    ok &= Report("Range " + back + "-" + (back + bsize - 1) + " (jump back) -> 206 in " + b.Millis + " ms", b.Ok, b.Error);
                    if (b.Ok) VerifyPieces(meta, file, back, b.Data, c);
                }

                // 5) End of the file (suffix)
                long tail = Math.Min(len, 256 * 1024);
                var t = await GetRange(http, url, len - tail, len - 1, ct, len, "bytes=-" + tail);
                ok &= Report("Range bytes=-" + tail + " (end) -> 206 in " + t.Millis + " ms", t.Ok, t.Error);
                if (t.Ok) VerifyPieces(meta, file, len - tail, t.Data, c);

                // 5b) Random jumps: latency until the first 256 KiB are available
                if (seeks > 0 && len > 16L * 1024 * 1024)
                {
                    var rnd = new Random(12345);
                    var lat = new List<long>();
                    for (int i = 0; i < seeks; i++)
                    {
                        long off = (long)(rnd.NextDouble() * (len - 1024 * 1024));
                        var sr = await GetRange(http, url, off, off + 256 * 1024 - 1, ct, len);
                        ok &= Report("random jump " + (i + 1) + "/" + seeks + " to " + off + " -> " + sr.Millis + " ms", sr.Ok, sr.Error);
                        if (sr.Ok) { lat.Add(sr.Millis); VerifyPieces(meta, file, off, sr.Data, c); }
                    }
                    if (lat.Count > 0)
                    {
                        lat.Sort();
                        Console.WriteLine("   jump latency: min " + lat[0] + " ms, median " + lat[lat.Count / 2] + " ms, max " + lat[lat.Count - 1] + " ms");
                    }
                }

                // 6) Simulated playback: sequential read paced at a fixed bitrate, counting cuts
                if (simulateSeconds > 0 && len > 8L * 1024 * 1024)
                    ok &= await SimulatePlayback(http, url, len, len * 3 / 10, simulateSeconds, bitrateKbps * 125L, readAhead, ct);

                // 7) MP4/MOV container structure: the sizes of the top-level boxes must add up to the length
                string ext = MimeTypes.GetExtension(file.Path);
                if (ext == "mp4" || ext == "m4v" || ext == "mov")
                    ok &= await WalkMp4Boxes(http, url, len, ct);
            }
            Console.WriteLine("== Independent SHA-1 of pieces served over HTTP: " + c.Verified + " correct, " + c.Failed + " incorrect, "
                + c.Skipped + " not checkable (they overlap another file or the range does not cover them whole)");
            ok &= Report("all the checked pieces match 'pieces' and there was at least one", c.Failed == 0 && c.Verified > 0, "");
            Console.WriteLine(ok ? "== VERIFICATION CORRECT" : "== VERIFICATION WITH ERRORS");
            return ok;
        }

        /// <summary>
        /// Simulates a player with little read-ahead (3 s by default, like a VLC with low network caching): it asks for data only when it has less than that many seconds of buffer,
        /// consumes it at bytesPerSec and counts the cuts (empty buffer). It exercises the server's TCP backpressure.
        /// </summary>
        private static async Task<bool> SimulatePlayback(HttpClient http, string url, long len, long start, int seconds, long bytesPerSec, double readAheadSeconds, CancellationToken ct)
        {
            Console.WriteLine("== simulated playback: " + seconds + " s at " + (bytesPerSec * 8 / 1000) + " kbit/s from byte " + start);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", "bytes=" + start + "-");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int stalls = 0; double stallSeconds = 0;
            long received = 0;
            double playedBytes = 0;           // bytes consumed by the "player"
            double lastClock = 0;
            bool playing = false;
            double startupTime = -1;
            using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            using (var stream = await resp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[64 * 1024];
                while (sw.Elapsed.TotalSeconds < seconds + 30 && !ct.IsCancellationRequested)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    if (playing)
                    {
                        playedBytes += (now - lastClock) * bytesPerSec;
                        if (playedBytes > received)
                        {
                            stalls++;
                            playing = false;                         // empty buffer: cut until 2 s are recovered
                            playedBytes = received;
                        }
                    }
                    else if (received - playedBytes >= bytesPerSec * 2)
                    {
                        if (startupTime < 0) startupTime = now;
                        playing = true;
                    }
                    else if (startupTime >= 0) stallSeconds += now - lastClock;
                    lastClock = now;
                    if (startupTime >= 0 && now - startupTime >= seconds) break;
                    double bufferedSeconds = (received - playedBytes) / (double)bytesPerSec;
                    if (bufferedSeconds > readAheadSeconds) { await Task.Delay(100, ct); continue; }       // the player does not read any more
                    var readTask = stream.ReadAsync(buf, 0, buf.Length, ct);
                    int n = await readTask;
                    if (n == 0) break;
                    received += n;
                }
            }
            Console.WriteLine("   startup (2 s of buffer): " + (startupTime < 0 ? "did not arrive" : startupTime.ToString("0.0", CultureInfo.InvariantCulture) + " s")
                + "; received " + received / 1024 + " KiB; cuts: " + stalls + "; stopped time: " + stallSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            return Report("simulated playback with no more than 1 cut after startup", startupTime >= 0 && stalls <= 1, "cuts " + stalls);
        }

        private sealed class RangeResult
        {
            public bool Ok;
            public string Error;
            public byte[] Data;
            public long Millis;
        }

        private static async Task<RangeResult> GetRange(HttpClient http, string url, long first, long last, CancellationToken ct, long totalLen, string headerOverride = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", headerOverride ?? ("bytes=" + first + "-" + last));
            using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                var res = new RangeResult();
                if ((int)resp.StatusCode != 206) { res.Error = "status " + (int)resp.StatusCode; res.Millis = sw.ElapsedMilliseconds; return res; }
                var cr = resp.Content.Headers.ContentRange;
                long expected = last - first + 1;
                if (cr == null || cr.From != first || cr.To != last || cr.Length != totalLen || resp.Content.Headers.ContentLength != expected)
                { res.Error = "incorrect Content-Range/Content-Length: " + cr + " len=" + resp.Content.Headers.ContentLength; res.Millis = sw.ElapsedMilliseconds; return res; }
                var ms = new MemoryStream((int)expected);
                using (var s = await resp.Content.ReadAsStreamAsync())
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0) ms.Write(buf, 0, n);
                }
                res.Millis = sw.ElapsedMilliseconds;
                res.Data = ms.ToArray();
                if (res.Data.Length != expected) { res.Error = "received " + res.Data.Length + " of " + expected + " bytes"; return res; }
                res.Ok = true;
                return res;
            }
        }

        private static void VerifyPieces(TorrentMeta meta, TorrentFileEntry file, long fileOffset, byte[] data, Counters c)
        {
            long absStart = file.Offset + fileOffset;
            long absEnd = absStart + data.Length;
            long fileAbsEnd = file.Offset + file.Length;
            long pl = meta.PieceLength;
            int firstPiece = (int)(absStart / pl);
            int lastPiece = (int)((absEnd - 1) / pl);
            using (var sha = SHA1.Create())
            {
                for (int p = firstPiece; p <= lastPiece && p < meta.PieceCount; p++)
                {
                    long ps = p * pl;
                    long pe = ps + meta.GetPieceLength(p);
                    if (ps < absStart || pe > absEnd || ps < file.Offset || pe > fileAbsEnd) { c.Skipped++; continue; }
                    var hash = sha.ComputeHash(data, (int)(ps - absStart), (int)(pe - ps));
                    if (HashUtil.ConstantTimeEquals(hash, meta.GetPieceHash(p))) c.Verified++;
                    else { c.Failed++; Console.WriteLine("   !! piece " + p + " does NOT match"); }
                }
            }
        }

        private static bool ReportContainer(byte[] data, string path)
        {
            string ext = MimeTypes.GetExtension(path);
            if (data.Length < 16) return Report("container header", false, "too little data");
            if (ext == "mp4" || ext == "m4v" || ext == "mov")
            {
                string type = Encoding.ASCII.GetString(data, 4, 4);
                bool ok = type == "ftyp";
                string brand = Encoding.ASCII.GetString(data, 8, 4);
                return Report("MP4 header: box '" + type + "' brand '" + brand + "'", ok, "ftyp expected");
            }
            if (ext == "mkv" || ext == "webm" || ext == "mka")
            {
                bool ok = data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3;
                return Report("Matroska/WebM header: EBML 1A45DFA3", ok, "bytes " + Hex.ToHex(data, 0, 4));
            }
            if (ext == "ts" || ext == "m2ts" || ext == "mpegts")
            {
                int step = ext == "m2ts" ? 192 : 188;
                int off = ext == "m2ts" ? 4 : 0;
                bool ok = true;
                for (int i = 0; i < 20 && off + i * step < data.Length; i++) ok &= data[off + i * step] == 0x47;
                return Report("MPEG-TS sync (0x47 every " + step + " bytes)", ok, "");
            }
            if (ext == "avi")
            {
                bool ok = Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "AVI ";
                return Report("AVI header: RIFF....AVI", ok, "");
            }
            Console.WriteLine("   (no container checker for ." + ext + ")");
            return true;
        }

        private static async Task<bool> WalkMp4Boxes(HttpClient http, string url, long len, CancellationToken ct)
        {
            Console.WriteLine("== walk of the top-level MP4 boxes (header Range requests across the whole file)");
            long off = 0;
            var boxes = new List<string>();
            while (off < len && boxes.Count < 200)
            {
                long end = Math.Min(len - 1, off + 15);
                var r = await GetRange(http, url, off, end, ct, len);
                if (!r.Ok) return Report("box at " + off, false, r.Error);
                long size = (r.Data[0] << 24) | (r.Data[1] << 16) | (r.Data[2] << 8) | r.Data[3];
                string type = Encoding.ASCII.GetString(r.Data, 4, 4);
                if (size == 1 && r.Data.Length >= 16)
                {
                    size = 0;
                    for (int i = 0; i < 8; i++) size = (size << 8) | r.Data[8 + i];
                }
                else if (size == 0) size = len - off;
                if (size < 8) return Report("box '" + type + "' at " + off + " with size " + size, false, "invalid size");
                boxes.Add(type + "@" + off + "(" + size + ")");
                off += size;
            }
            Console.WriteLine("   boxes: " + string.Join(" ", boxes));
            return Report("the boxes add up to exactly " + len + " bytes and there is a moov", off == len && boxes.Any(b => b.StartsWith("moov")), "they add up to " + off);
        }

        private static bool Report(string what, bool ok, string detail)
        {
            Console.WriteLine((ok ? "  [ OK ] " : "  [FAIL] ") + what + (ok || string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
            return ok;
        }
    }
}
