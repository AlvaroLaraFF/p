using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>What happened with ONE request to ONE TSService host (to know whether the service answers, without storing anything of the response).</summary>
    public enum TsAttemptKind
    {
        /// <summary>Answered with an interpretable transport file.</summary>
        File,
        /// <summary>Answered with a well-formed XML document but without a file (for example "I do not have that channel"): the service is alive and speaks the protocol.</summary>
        Answered,
        /// <summary>HTTP 200 with an empty body (what the torrentstream.* servers answer today).</summary>
        Empty,
        /// <summary>HTTP code that is not 2xx (451, 404, 5xx...).</summary>
        HttpError,
        /// <summary>The request deadline expired (TsRequestTimeout).</summary>
        Timeout,
        /// <summary>Could not connect or resolve the name.</summary>
        NetworkError,
        /// <summary>Answered something that is neither XML nor a torrent (an HTML page, garbage, a response that is too large).</summary>
        Garbage
    }

    /// <summary>Result of a request to a TSService host.</summary>
    public sealed class TsAttempt
    {
        public string Host;
        public TsAttemptKind Kind;
        public string Detail;
        public TimeSpan Elapsed;
        /// <summary>The service answered usefully (file or valid XML): it counts as "responds".</summary>
        public bool Success { get { return Kind == TsAttemptKind.File || Kind == TsAttemptKind.Answered; } }
        public override string ToString() { return Host + ": " + Kind + (Detail != null ? " (" + Detail + ")" : ""); }
    }

    /// <summary>Result of the TSService active check: one request (or, if the hosts fail, up to maxHosts).</summary>
    public sealed class TsCheckResult
    {
        public bool Responding;
        public readonly List<TsAttempt> Attempts = new List<TsAttempt>();
    }

    /// <summary>
    /// Client of the TSService fallback path: signed GET to /gettorrent?pid=&lt;content_id&gt; (or infohash=) on the
    /// torrentstream.* servers. The whole format is INFERRED: signature sha1(items sorted and joined by '#' + secret),
    /// XML response with 'torrent' in base64. Since the exact format is UNKNOWN, reading the response is tolerant and the
    /// only guarantee required is SHA-1(torrent) == content_id.
    /// </summary>
    public sealed class TsServiceClient
    {
        private const string Tag = "tsservice";
        private readonly ResolverOptions _opt;
        private readonly HttpClient _http;

        public TsServiceClient(ResolverOptions options, HttpClient http)
        {
            _opt = options;
            _http = http;
        }

        // ---------- Request ----------

        /// <summary>Builds the signed URL. 'random' is the value of _r (it is compared with the 'r' of the response).</summary>
        public static string BuildUrl(string host, string method, IDictionary<string, string> userParams, ResolverOptions opt, long unixTime, long random)
        {
            var p = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in userParams)
            {
                if (kv.Key.Length > 0 && kv.Key[0] == '_') throw new ArgumentException("Cannot use reserved parameter '" + kv.Key + "'");
                p[kv.Key] = kv.Value;
            }
            p["_r"] = random.ToString(CultureInfo.InvariantCulture);
            p["_t"] = unixTime.ToString(CultureInfo.InvariantCulture);
            p["_v"] = opt.TsEngineVersion;
            foreach (var kv in opt.TsExtraParams) p[kv.Key] = kv.Value;

            // sig = sha1('#'.join(sorted('k=v')) + SECRET) (pattern of the original's Core/Ads/Manager.py)
            var items = p.Select(kv => kv.Key + "=" + kv.Value).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var body = Encoding.UTF8.GetBytes(string.Join("#", items));
            var all = new byte[body.Length + opt.TsSecret.Length];
            Buffer.BlockCopy(body, 0, all, 0, body.Length);
            Buffer.BlockCopy(opt.TsSecret, 0, all, body.Length, opt.TsSecret.Length);
            string sig = Hex.ToHex(HashUtil.Sha1(all));

            var sb = new StringBuilder(host.TrimEnd('/'));
            sb.Append('/').Append(method).Append('?');
            foreach (var kv in p) sb.Append(kv.Key).Append('=').Append(QuotePlus(kv.Value)).Append('&');
            sb.Append("_s=").Append(sig);
            return sb.ToString();
        }

        private static string QuotePlus(string s)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '_' || b == '.' || b == '-' || b == '~') sb.Append((char)b);
                else if (b == ' ') sb.Append('+');
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Asks the servers for the transport file of a content_id (or an infohash). Returns the bytes (unverified: the caller
        /// does that) or null if no server has it. Throws OperationCanceledException if cancelled. Each request to a host is
        /// notified to ResolverOptions.TsObserver (a cancellation by the caller, for example because P2P won first, is NOT a service failure).
        /// </summary>
        public async Task<byte[]> GetTorrentAsync(string idHex, bool isInfoHash, CancellationToken ct)
        {
            var hosts = _opt.TsHosts.ToList();
            Shuffle(hosts);
            int tried = 0;
            foreach (var host in hosts)
            {
                if (tried >= _opt.TsMaxHosts) break;
                ct.ThrowIfCancellationRequested();
                tried++;
                byte[] torrent;
                var attempt = await AttemptAsync(host, idHex, isInfoHash, ct).ConfigureAwait(false);
                torrent = attempt.Item2;
                Notify(attempt.Item1);
                if (torrent != null) return torrent;
            }
            return null;
        }

        /// <summary>
        /// Active check: asks for a file that does not exist (a random id) and sees whether the service answers anything useful. It stops at the first host that answers and
        /// tries at most maxHosts: with the service alive it is ONE request. It does not notify TsObserver (the caller keeps the result).
        /// </summary>
        public async Task<TsCheckResult> CheckAsync(int maxHosts, CancellationToken ct)
        {
            var res = new TsCheckResult();
            var hosts = _opt.TsHosts.ToList();
            Shuffle(hosts);
            var idBytes = new byte[20];
            for (int i = 0; i < idBytes.Length; i++) idBytes[i] = (byte)Rng.Next(256);
            string id = Hex.ToHex(idBytes);
            foreach (var host in hosts.Take(Math.Max(1, maxHosts)))
            {
                ct.ThrowIfCancellationRequested();
                var a = await AttemptAsync(host, id, false, ct).ConfigureAwait(false);
                res.Attempts.Add(a.Item1);
                if (a.Item1.Success) { res.Responding = true; break; }
            }
            return res;
        }

        private void Notify(TsAttempt a)
        {
            var obs = _opt.TsObserver;
            if (obs == null) return;
            try { obs(a); } catch (Exception ex) { AceLog.Debug(Tag, "observer: " + ex.Message); }
        }

        /// <summary>A signed request to a host. Throws OperationCanceledException only if the caller cancels it.</summary>
        private async Task<Tuple<TsAttempt, byte[]>> AttemptAsync(string host, string idHex, bool isInfoHash, CancellationToken ct)
        {
            var ps = new Dictionary<string, string>();
            ps[isInfoHash ? "infohash" : "pid"] = idHex;
            long random = Rng.Next(1, int.MaxValue);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string url = BuildUrl(host, _opt.TsGetMethod, ps, _opt, now, random);
            var a = new TsAttempt { Host = host, Kind = TsAttemptKind.NetworkError };
            byte[] torrent = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_opt.TsRequestTimeout);
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", HttpTracker.UserAgent);
                        AceLog.Debug(Tag, "GET " + url);
                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            var body = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.TsServiceBody, cts.Token).ConfigureAwait(false);
                            AceLog.Info(Tag, host + ": HTTP " + (int)resp.StatusCode + ", " + body.Length + " bytes");
                            if (!resp.IsSuccessStatusCode) { a.Kind = TsAttemptKind.HttpError; a.Detail = "HTTP " + (int)resp.StatusCode; }
                            else if (body.Length == 0) a.Kind = TsAttemptKind.Empty;
                            else
                            {
                                string error;
                                bool wellFormed;
                                torrent = ParseResponseCore(body, idHex, random, _opt, out error, out wellFormed);
                                if (torrent != null) a.Kind = TsAttemptKind.File;
                                else
                                {
                                    a.Kind = wellFormed ? TsAttemptKind.Answered : TsAttemptKind.Garbage;
                                    a.Detail = error;
                                    AceLog.Info(Tag, host + ": response without a torrent (" + (error ?? "?") + ")");
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AceLog.Info(Tag, host + ": timed out");
                a.Kind = TsAttemptKind.Timeout;
            }
            catch (HttpRequestException ex)
            {
                AceLog.Info(Tag, host + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                a.Kind = TsAttemptKind.NetworkError;
                a.Detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            }
            catch (InvalidDataException ex)
            {
                AceLog.Info(Tag, host + ": " + ex.Message);
                a.Kind = TsAttemptKind.Garbage;
                a.Detail = ex.Message;
            }
            a.Elapsed = sw.Elapsed;
            return Tuple.Create(a, torrent);
        }

        // ---------- Response (tolerant) ----------

        /// <summary>
        /// Extracts the torrent from the response. It accepts XML with a 'torrent' element (base64) at any level, a response that is
        /// directly base64 and a binary response whose SHA-1 is the id. Returns null and leaves the reason in 'error' if there is no torrent.
        /// </summary>
        public static byte[] ParseResponse(byte[] body, string idHex, long requestRandom, ResolverOptions opt, out string error)
        {
            bool wellFormed;
            return ParseResponseCore(body, idHex, requestRandom, opt, out error, out wellFormed);
        }

        /// <summary>Like ParseResponse and also says whether the response was well-formed XML (even if it carries no torrent): the service speaks the protocol.</summary>
        internal static byte[] ParseResponseCore(byte[] body, string idHex, long requestRandom, ResolverOptions opt, out string error, out bool wellFormed)
        {
            error = null;
            wellFormed = false;
            if (body == null || body.Length == 0) { error = "Empty response"; return null; }

            string text = Encoding.UTF8.GetString(body).TrimStart('﻿').Trim();
            if (text.StartsWith("<", StringComparison.Ordinal))
            {
                XDocument doc;
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    using (var rd = XmlReader.Create(new StringReader(text), settings)) doc = XDocument.Load(rd);
                }
                catch (XmlException ex) { error = "invalid XML: " + ex.Message; return null; }

                var root = doc.Root;
                wellFormed = root != null && !string.Equals(root.Name.LocalName, "html", StringComparison.OrdinalIgnoreCase);       // an HTML page (block, error) is not the service
                // Attributes of the root: sig and r. They are checked and logged; they are only required if configured.
                string r = Attr(root, "r"), sig = Attr(root, "sig");
                if (r != null)
                {
                    long rv;
                    bool rOk = long.TryParse(r, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rv) && rv == requestRandom;
                    if (!rOk) AceLog.Warn(Tag, "check_response: bad random: response_random=" + r + " request_random=" + requestRandom);
                    if (!rOk && opt.TsVerifyResponseSignature) { error = "Bad random"; return null; }
                }
                if (sig != null)
                {
                    // sha1(text without ' sig="..."' + secret) (INFERRED)
                    string stripped = text.Replace(" sig=\"" + sig + "\"", "");
                    var payload = Encoding.UTF8.GetBytes(stripped);
                    var all = new byte[payload.Length + opt.TsSecret.Length];
                    Buffer.BlockCopy(payload, 0, all, 0, payload.Length);
                    Buffer.BlockCopy(opt.TsSecret, 0, all, payload.Length, opt.TsSecret.Length);
                    bool ok = string.Equals(Hex.ToHex(HashUtil.Sha1(all)), sig, StringComparison.OrdinalIgnoreCase);
                    AceLog.Info(Tag, "response signature (sha1(text+secret)): " + (ok ? "matches" : "does NOT match (INFERRED signature format)"));
                    if (!ok && opt.TsVerifyResponseSignature) { error = "Bad signature"; return null; }
                }
                else if (opt.TsVerifyResponseSignature) { error = "Missing signature"; return null; }

                var tor = FindElement(doc, "torrent");
                if (tor == null || string.IsNullOrWhiteSpace(tor.Value))
                {
                    var status = FindElement(doc, "status") ?? FindElement(doc, "error") ?? FindElement(doc, "message");
                    error = status != null ? "status=" + status.Value.Trim() : "no 'torrent' tag";
                    return null;
                }
                var data = DecodeBase64(tor.Value);
                if (data == null) { error = "torrent is not base64"; return null; }
                var cs = FindElement(doc, "checksum");
                if (cs != null && idHex.Length == 40)
                {
                    string c = cs.Value.Trim().ToLowerInvariant();
                    string actual = Hex.ToHex(HashUtil.Sha1(data));
                    if (c.Length == 40 && c != actual) AceLog.Warn(Tag, "response checksum (" + c + ") differs from the SHA-1 of the data (" + actual + ")");
                }
                return data;
            }

            // Not XML: it may be the torrent in base64, or the raw torrent.
            string sha = Hex.ToHex(HashUtil.Sha1(body));
            if (sha == idHex) return body;
            var b64 = DecodeBase64(text);
            if (b64 != null && b64.Length > 0) return b64;
            error = "unrecognized response";
            return null;
        }

        private static string Attr(XElement e, string name)
        {
            if (e == null) return null;
            var a = e.Attributes().FirstOrDefault(x => string.Equals(x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return a == null ? null : a.Value;
        }

        private static XElement FindElement(XDocument doc, string name)
        {
            return doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static byte[] DecodeBase64(string s)
        {
            try
            {
                var clean = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (clean.Length == 0 || clean.Length % 4 != 0) return null;
                return Convert.FromBase64String(clean);
            }
            catch (FormatException) { return null; }
        }

        private static void Shuffle<T>(IList<T> list)
        {
            Rng.Shuffle(list);
        }
    }
}
