using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Probe
{
    public enum ScrapeStatus
    {
        Ok,
        /// <summary>(-1,-1) of the original: network error.</summary>
        NetworkError,
        /// <summary>(-2,-2): invalid response or the infohash is not there.</summary>
        Invalid,
        /// <summary>(-3,-3): the tracker asks to wait (flags.min_request_interval).</summary>
        Wait,
        /// <summary>The tracker does not support scrape (the announce URL cannot be converted).</summary>
        Unsupported
    }

    public sealed class ScrapeResult
    {
        public string Tracker;
        public ScrapeStatus Status;
        public int Seeders, Leechers, Completed;
        public int MinRequestInterval;
        public string Error;
        public TimeSpan Elapsed;
        /// <summary>HTTP code of the response (0 if there was none or it is UDP).</summary>
        public int HttpStatus;
        /// <summary>No answer in time (HTTP or UDP without response).</summary>
        public bool TimedOut;
        /// <summary>The tracker rejects us (HTTP 429/403/451/401, or a UDP error that speaks of a limit or a block): stay away.</summary>
        public bool Rejected;
        /// <summary>The response was neither bencode nor a dictionary (a proxy's error page, for example).</summary>
        public bool Malformed;
        /// <summary>Seconds the tracker asks for in Retry-After (0 if it does not say).</summary>
        public int RetryAfterSeconds;
        /// <summary>UDP: packets sent in the request (connect + scrape + retries).</summary>
        public int Packets;
        /// <summary>The tracker was never asked (waiting, benched or queue expired): it counts neither as a response nor as a failure.</summary>
        public bool Skipped;
        public override string ToString()
        {
            return Tracker + ": " + Status + (Status == ScrapeStatus.Ok ? " S=" + Seeders + " L=" + Leechers : (Error != null ? " (" + Error + ")" : ""));
        }
    }

    /// <summary>HTTP scrape (BEP 48, like Ace's tracker checker).</summary>
    public static class HttpScrape
    {
        /// <summary>
        /// announce -> scrape: if the last segment of the path starts with "announce" it is replaced by "scrape"; otherwise, the tracker does not
        /// support scrape and null is returned. The query of the original URL is kept.
        /// </summary>
        public static string ScrapeUrl(string announceUrl)
        {
            Uri u;
            if (!Uri.TryCreate(announceUrl, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
            string path = u.AbsolutePath;
            int slash = path.LastIndexOf('/');
            string last = path.Substring(slash + 1);
            if (!last.StartsWith("announce", StringComparison.OrdinalIgnoreCase)) return null;
            string newPath = path.Substring(0, slash + 1) + "scrape" + last.Substring("announce".Length);
            return u.GetLeftPart(UriPartial.Authority) + newPath + u.Query;
        }

        public static string BuildUrl(string announceUrl, byte[] infoHash)
        {
            return BuildUrl(announceUrl, new[] { infoHash });
        }

        /// <summary>BEP 48 with several infohashes: one info_hash parameter for each one in the same URL.</summary>
        public static string BuildUrl(string announceUrl, IList<byte[]> infoHashes)
        {
            string s = ScrapeUrl(announceUrl);
            if (s == null) return null;
            var sb = new StringBuilder(s);
            sb.Append(s.IndexOf('?') >= 0 ? '&' : '?');
            for (int i = 0; i < infoHashes.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append("info_hash=").Append(HttpTracker.PercentEncode(infoHashes[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// How many of the given infohashes fit in a single request: at most maxCount and without exceeding maxUrlLength URL characters
        /// (servers usually cut around 8 KiB and proxies earlier). Always returns at least 1 if there is any.
        /// </summary>
        public static int FitCount(string announceUrl, IList<byte[]> infoHashes, int maxCount, int maxUrlLength)
        {
            if (infoHashes.Count == 0) return 0;
            string s = ScrapeUrl(announceUrl);
            if (s == null) return Math.Min(infoHashes.Count, Math.Max(1, maxCount));
            int len = s.Length + 1;
            int n = 0;
            foreach (var h in infoHashes)
            {
                if (n >= maxCount) break;
                int add = "info_hash=".Length + HttpTracker.PercentEncode(h).Length + (n > 0 ? 1 : 0);
                if (n > 0 && len + add > maxUrlLength) break;
                len += add;
                n++;
            }
            return Math.Max(1, n);
        }

        public static ScrapeResult ParseResponse(byte[] body, byte[] infoHash)
        {
            return ParseBatchResponse(body, new[] { infoHash })[0];
        }

        /// <summary>A scrape response to several infohashes: one result for each, in the same order they were asked.</summary>
        public static IList<ScrapeResult> ParseBatchResponse(byte[] body, IList<byte[]> infoHashes)
        {
            var list = new List<ScrapeResult>();
            Func<ScrapeStatus, string, bool, IList<ScrapeResult>> all = (st, err, malformed) =>
            {
                foreach (var h in infoHashes) list.Add(new ScrapeResult { Status = st, Error = err, Malformed = malformed });
                return list;
            };
            BValue v;
            try { v = BencodeParser.Parse(body); }
            catch (BencodeException ex) { return all(ScrapeStatus.Invalid, "non-bencode response: " + ex.Message, true); }
            var d = v as BDict;
            if (d == null) return all(ScrapeStatus.Invalid, "the response is not a dictionary", true);
            var fail = d.GetString("failure reason");
            if (fail != null)
            {
                all(ScrapeStatus.Invalid, fail, false);
                if (LooksLikeRateLimit(fail)) foreach (var r in list) r.Rejected = true;
                return list;
            }
            var flags = d.GetDict("flags");
            long? mri = flags == null ? null : flags.GetInt("min_request_interval");
            var files = d.GetDict("files");
            if (files == null) return all(ScrapeStatus.Invalid, "no 'files'", false);
            var byHash = new Dictionary<string, BDict>(StringComparer.Ordinal);
            foreach (var e in files.Entries)
            {
                var f = e.Value as BDict;
                if (f == null || e.Key.Value.Length != 20) continue;
                byHash[Hex.ToHex(e.Key.Value)] = f;
            }
            foreach (var ih in infoHashes)
            {
                var r = new ScrapeResult();
                BDict f;
                if (byHash.TryGetValue(Hex.ToHex(ih), out f))
                {
                    // Negatives become 0 (getStatus)
                    r.Seeders = (int)Math.Max(0, f.GetInt("complete") ?? 0);
                    r.Leechers = (int)Math.Max(0, f.GetInt("incomplete") ?? 0);
                    r.Completed = (int)Math.Max(0, f.GetInt("downloaded") ?? 0);
                    r.Status = ScrapeStatus.Ok;
                    if (mri.HasValue) r.MinRequestInterval = (int)Math.Max(0, Math.Min(int.MaxValue, mri.Value));
                }
                else if (mri.HasValue) { r.Status = ScrapeStatus.Wait; r.MinRequestInterval = (int)Math.Max(0, Math.Min(int.MaxValue, mri.Value)); }
                else { r.Status = ScrapeStatus.Invalid; r.Error = "the infohash is not in the response"; }
                list.Add(r);
            }
            return list;
        }

        internal static bool LooksLikeRateLimit(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            return t.Contains("rate") || t.Contains("limit") || t.Contains("too many") || t.Contains("banned") || t.Contains(" ban") || t.Contains("blocked") || t.Contains("flood");
        }

        private static bool IsRejectionStatus(int code) { return code == 429 || code == 403 || code == 451 || code == 401 || code == 418; }
        private static bool IsNoScrapeStatus(int code) { return code == 404 || code == 405 || code == 501; }

        private static int ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders h)
        {
            var ra = h.RetryAfter;
            if (ra == null) return 0;
            if (ra.Delta.HasValue) return (int)Math.Max(0, Math.Min(86400, ra.Delta.Value.TotalSeconds));
            if (ra.Date.HasValue) return (int)Math.Max(0, Math.Min(86400, (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds));
            return 0;
        }

        public static async Task<ScrapeResult> ScrapeAsync(HttpClient http, string announceUrl, byte[] infoHash, TimeSpan timeout, CancellationToken ct)
        {
            return (await ScrapeBatchAsync(http, announceUrl, new[] { infoHash }, timeout, ct).ConfigureAwait(false))[0];
        }

        /// <summary>A single GET request with all the infohashes (BEP 48). Returns one result per infohash, in the same order.</summary>
        public static async Task<IList<ScrapeResult>> ScrapeBatchAsync(HttpClient http, string announceUrl, IList<byte[]> infoHashes, TimeSpan timeout, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IList<ScrapeResult> res = null;
            ScrapeStatus failStatus = ScrapeStatus.NetworkError; string failError = null; bool timedOut = false, rejected = false; int http_ = 0, retryAfter = 0;
            string url = BuildUrl(announceUrl, infoHashes);
            if (url == null) { failStatus = ScrapeStatus.Unsupported; failError = "the announce URL cannot be converted into a scrape"; }
            else
            {
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        cts.CancelAfter(timeout);
                        req.Headers.TryAddWithoutValidation("User-Agent", HttpTracker.UserAgent);
                        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            http_ = (int)resp.StatusCode;
                            retryAfter = ParseRetryAfter(resp.Headers);
                            var body = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.TrackerBody, cts.Token).ConfigureAwait(false);
                            if (IsRejectionStatus(http_)) { failError = "HTTP " + http_; rejected = true; }
                            else if (IsNoScrapeStatus(http_)) { failStatus = ScrapeStatus.Unsupported; failError = "HTTP " + http_ + " (the tracker does not support scrape)"; }
                            else if (!resp.IsSuccessStatusCode && body.Length == 0) failError = "HTTP " + http_;
                            else res = ParseBatchResponse(body, infoHashes);
                        }
                    }
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; failError = "timed out"; timedOut = true; }
                catch (HttpRequestException ex) { failError = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
            }
            if (res == null) res = infoHashes.Select(h => new ScrapeResult { Status = failStatus, Error = failError, TimedOut = timedOut, Rejected = rejected }).ToList();
            foreach (var r in res) { r.Tracker = announceUrl; r.Elapsed = sw.Elapsed; r.HttpStatus = http_; r.RetryAfterSeconds = retryAfter; }
            return res;
        }
    }

    internal static class Bytes
    {
        public static bool SequenceEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// UDP scraper (BEP 15) with memory: remembers the connection id of each tracker (valid 2 min per BEP 15; here 60 s) so as not to spend a connect on each
    /// batch, and the resolved IP of each host. Each request is a batch of up to ~70 infohashes in a single packet.
    /// </summary>
    public sealed class UdpScraper
    {
        private sealed class Conn { public ulong Id; public DateTime Expires; }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Conn> _conns = new Dictionary<string, Conn>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KeyValuePair<IPAddress, DateTime>> _dns = new Dictionary<string, KeyValuePair<IPAddress, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _now;
        /// <summary>0 = do not remember the connection id.</summary>
        public TimeSpan ConnectionTtl = TimeSpan.FromSeconds(60);
        public TimeSpan DnsTtl = TimeSpan.FromMinutes(10);

        public UdpScraper(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        private bool TryGetConn(string key, out ulong id)
        {
            id = 0;
            if (ConnectionTtl <= TimeSpan.Zero) return false;
            lock (_gate)
            {
                Conn c;
                if (_conns.TryGetValue(key, out c)) { if (_now() < c.Expires) { id = c.Id; return true; } _conns.Remove(key); }
                return false;
            }
        }

        private void PutConn(string key, ulong id)
        {
            if (ConnectionTtl <= TimeSpan.Zero) return;
            lock (_gate) _conns[key] = new Conn { Id = id, Expires = _now() + ConnectionTtl };
        }

        private void DropConn(string key) { lock (_gate) _conns.Remove(key); }

        private async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
        {
            IPAddress direct;
            if (IPAddress.TryParse(host, out direct)) return direct.AddressFamily == AddressFamily.InterNetwork ? direct : null;
            lock (_gate)
            {
                KeyValuePair<IPAddress, DateTime> e;
                if (_dns.TryGetValue(host, out e)) { if (_now() < e.Value) return e.Key; _dns.Remove(host); }
            }
            var ip = await AceEngine.P2P.Net.ResolveIPv4Async(host, ct).ConfigureAwait(false);
            if (ip != null && DnsTtl > TimeSpan.Zero) lock (_gate) _dns[host] = new KeyValuePair<IPAddress, DateTime>(ip, _now() + DnsTtl);
            return ip;
        }

        /// <summary>Scrape of several infohashes on a UDP tracker, with 'timeoutsSeconds.Length' attempts at most (BEP15, with bounded retries).</summary>
        public async Task<IList<ScrapeResult>> ScrapeAsync(string host, int port, string label, IList<byte[]> infoHashes, int[] timeoutsSeconds, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int packets = 0;
            Func<ScrapeStatus, string, bool, bool, IList<ScrapeResult>> fail = (st, err, timedOut, rejected) =>
                infoHashes.Select(h => new ScrapeResult { Tracker = label, Status = st, Error = err, Elapsed = sw.Elapsed, TimedOut = timedOut, Rejected = rejected, Packets = packets }).ToList();
            IPAddress ip = await ResolveAsync(host, ct).ConfigureAwait(false);
            if (ip == null) return fail(ScrapeStatus.NetworkError, "could not resolve " + host, false, false);
            string key = host + ":" + port;
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.Connect(new IPEndPoint(ip, port));
                Task<UdpReceiveResult> pending = null;
                Func<TimeSpan, Task<byte[]>> recv = async timeout =>
                {
                    if (pending == null) pending = udp.ReceiveAsync();
                    var done = await Task.WhenAny(pending, Task.Delay(timeout, ct)).ConfigureAwait(false);
                    if (done == pending) { var rr = await pending.ConfigureAwait(false); pending = null; return rr.Buffer; }
                    ct.ThrowIfCancellationRequested();
                    return null;
                };
                string lastError = "no response";
                bool lastTimedOut = true;
                foreach (var tsec in timeoutsSeconds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        ulong cid = 0;
                        bool connected = TryGetConn(key, out cid);
                        bool reused = connected;
                        if (!connected)
                        {
                            uint tid = (uint)Rng.Next(1, int.MaxValue);
                            var c = UdpTracker.BuildConnect(tid);
                            await udp.SendAsync(c, c.Length).ConfigureAwait(false);
                            packets++;
                            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(tsec);
                            while (!connected)
                            {
                                var left = deadline - DateTime.UtcNow;
                                if (left <= TimeSpan.Zero) break;
                                var resp = await recv(left).ConfigureAwait(false);
                                if (resp == null) break;
                                string err;
                                if (UdpTracker.ParseConnect(resp, tid, out cid, out err)) { connected = true; PutConn(key, cid); }
                                else if (err != null) return fail(ScrapeStatus.Invalid, err, false, HttpScrape.LooksLikeRateLimit(err));
                            }
                            if (!connected) { lastError = "no response to the connect"; lastTimedOut = true; continue; }
                        }
                        uint tid2 = (uint)Rng.Next(1, int.MaxValue);
                        var s = BuildScrape(cid, tid2, infoHashes);
                        await udp.SendAsync(s, s.Length).ConfigureAwait(false);
                        packets++;
                        var deadline2 = DateTime.UtcNow + TimeSpan.FromSeconds(tsec);
                        bool staleConnection = false;
                        while (!staleConnection)
                        {
                            var left = deadline2 - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero) break;
                            var resp = await recv(left).ConfigureAwait(false);
                            if (resp == null) break;
                            List<ScrapeResult> results; string err;
                            if (ParseScrape(resp, tid2, infoHashes.Count, out results, out err))
                            {
                                foreach (var r in results) { r.Tracker = label; r.Elapsed = sw.Elapsed; r.Packets = packets; }
                                return results;
                            }
                            if (err != null)
                            {
                                if (!reused) return fail(ScrapeStatus.Invalid, err, false, HttpScrape.LooksLikeRateLimit(err));
                                DropConn(key); lastError = err; lastTimedOut = false; staleConnection = true;       // the connection id may have expired: another attempt with a new connect
                            }
                        }
                        if (staleConnection) continue;
                        if (reused) DropConn(key);
                        lastError = "no response"; lastTimedOut = true;
                    }
                    catch (SocketException ex) { lastError = ex.SocketErrorCode.ToString(); lastTimedOut = false; pending = null; DropConn(key); }
                }
                return fail(ScrapeStatus.NetworkError, lastError, lastTimedOut, false);
            }
        }

        public static byte[] BuildScrape(ulong connectionId, uint tid, IList<byte[]> infoHashes)
        {
            return UdpScrape.BuildScrape(connectionId, tid, infoHashes);
        }

        public static bool ParseScrape(byte[] b, uint tid, int count, out List<ScrapeResult> results, out string error)
        {
            return UdpScrape.ParseScrape(b, tid, count, out results, out error);
        }
    }

    /// <summary>UDP scrape (BEP 15): connect (action 0) and scrape (action 2) with one or several infohashes.</summary>
    public static class UdpScrape
    {
        /// <summary>BEP 15: between 1 and 74 infohashes per scrape packet (fits in a 1500 B datagram).</summary>
        public const int MaxHashesPerPacket = 74;

        public static byte[] BuildScrape(ulong connectionId, uint tid, IList<byte[]> infoHashes)
        {
            if (infoHashes.Count < 1 || infoHashes.Count > MaxHashesPerPacket) throw new ArgumentException("BEP15: between 1 and 74 infohashes per scrape");
            var b = new byte[16 + 20 * infoHashes.Count];
            Wire.PutU64(b, 0, connectionId); Wire.PutU32(b, 8, 2); Wire.PutU32(b, 12, tid);
            for (int i = 0; i < infoHashes.Count; i++) Buffer.BlockCopy(infoHashes[i], 0, b, 16 + 20 * i, 20);
            return b;
        }

        /// <summary>Response: action (2), tid and, for each infohash, seeders, completed and leechers (32-bit big-endian integers). false if it is not the expected response.</summary>
        public static bool ParseScrape(byte[] b, uint tid, int count, out List<ScrapeResult> results, out string error)
        {
            results = null; error = null;
            if (b.Length >= 8 && Wire.GetU32(b, 0) == 3 && Wire.GetU32(b, 4) == tid) { error = b.Length > 8 ? Encoding.UTF8.GetString(b, 8, b.Length - 8) : "tracker error"; return false; }
            if (b.Length < 8 + 12 * count || Wire.GetU32(b, 0) != 2 || Wire.GetU32(b, 4) != tid) return false;
            results = new List<ScrapeResult>();
            for (int i = 0; i < count; i++)
            {
                int o = 8 + 12 * i;
                results.Add(new ScrapeResult
                {
                    Status = ScrapeStatus.Ok,
                    Seeders = (int)Math.Min(int.MaxValue, Wire.GetU32(b, o)),
                    Completed = (int)Math.Min(int.MaxValue, Wire.GetU32(b, o + 4)),
                    Leechers = (int)Math.Min(int.MaxValue, Wire.GetU32(b, o + 8))
                });
            }
            return true;
        }

        /// <summary>Scrape of several infohashes on a UDP tracker, without memory between calls (one connect per call). For repeated batches, use UdpScraper.</summary>
        public static Task<IList<ScrapeResult>> ScrapeAsync(string host, int port, string label, IList<byte[]> infoHashes, int[] timeoutsSeconds, CancellationToken ct)
        {
            var s = new UdpScraper { ConnectionTtl = TimeSpan.Zero, DnsTtl = TimeSpan.Zero };
            return s.ScrapeAsync(host, port, label, infoHashes, timeoutsSeconds, ct);
        }

        public static async Task<ScrapeResult> ScrapeAsync(string announceUrl, byte[] infoHash, int[] timeoutsSeconds, CancellationToken ct)
        {
            Uri u;
            if (!Uri.TryCreate(announceUrl, UriKind.Absolute, out u) || u.Scheme != "udp")
                return new ScrapeResult { Tracker = announceUrl, Status = ScrapeStatus.Unsupported, Error = "not udp://" };
            var l = await ScrapeAsync(u.Host, u.Port > 0 ? u.Port : 80, announceUrl, new[] { infoHash }, timeoutsSeconds, ct).ConfigureAwait(false);
            return l[0];
        }
    }
}
