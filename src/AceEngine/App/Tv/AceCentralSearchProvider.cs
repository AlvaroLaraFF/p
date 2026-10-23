using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    public sealed class AceCentralOptions
    {
        /// <summary>Service hosts. Plain HTTP, like the official engine. They are shuffled and tried in order.</summary>
        public IList<string> Hosts = new List<string>(AceCentralSearchProvider.DefaultHosts);
        /// <summary>Deadline per host and total for a call (the official engine waits 30 s per host).</summary>
        public TimeSpan HostTimeout = TimeSpan.FromSeconds(8), TotalTimeout = TimeSpan.FromSeconds(20);
        public int MaxBytes = 1024 * 1024;
        /// <summary>Maximum size of the JSON already decoded from base64.</summary>
        public int MaxDecodedBytes = 4 * 1024 * 1024;
        public int SearchPageSize = 50, ExplorePageSize = 100, ExplorePages = 2;
        public int MaxResults = 300;
        public TimeSpan CacheTtl = TimeSpan.FromSeconds(120);
        /// <summary>Circuit breaker: if in a call no host manages to answer (no connection or no answer in time: the operator's block),
        /// it is not tried again for this time and searches do not wait. 300 s like the official engine. Zero = no circuit breaker.</summary>
        public TimeSpan BreakerCooldown = TimeSpan.FromSeconds(300);
        /// <summary>Circuit breaker clock (replaceable in tests).</summary>
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;
        public int MaxQueryLength = 200;
        /// <summary>UI language (parameter j, e.g. "es") and language and country preferences (s and t, e.g. "spa" and "es"). Null = those of the system culture; "" = not sent.</summary>
        public string Lang, PreferredLanguages, PreferredCountries;
    }

    /// <summary>
    /// Client of the Ace Stream central search: the same protocol used by SearchManager of CoreApp.so, with the two keys carried by the official app.
    ///  - Request: GET http://s5.torrentstream.{net,org,info}/a?... with the parameters shortened to one letter (query-&gt;a, category-&gt;c, page-&gt;h, page_size-&gt;i, lang-&gt;j,
    ///    api_version-&gt;m=4, preferred_languages-&gt;s, preferred_countries-&gt;t), plus _v=3022200, _n=3.2.22, _p=android, _r (nonce) and _t (time), and the signature
    ///    _s = SHA1("k=v#k=v#..." + REQUEST_KEY) with the keys in ordinal order and the values unescaped (in the URL they go through quote_plus). _a, _b and _c are not sent.
    ///  - Response: {"data": base64(JSON), "sign": sha1}; accepted only if sign == SHA1(data + _r + RESPONSE_KEY) (or, if that does not match, SHA1(data + RESPONSE_KEY): the
    ///    second component of the signature is INFERRED); if none matches it is discarded and the next host is tried. The results are compact items
    ///    (n, i, u, b, c, a, au, ci, s, d) inside groups; only the infohash (or the url if there is no infohash) and the name reach RemoteResult.
    ///  - Explore: the Ace catalog category of the kind (ExploreCategories), in one or two pages of 100.
    /// IT IS A PRIVATE ACE SERVICE AND THIS USES ITS KEYS: it may stop working without notice (keys rotate, the API version changes), it may breach its terms of use and
    /// every query travels unencrypted with the user's IP. It only works if isEnabled returns true (UserSettings.AceSearchEnabled, ON by default by
    /// decision of the user on 17-10-2026), which is queried on EVERY call; with the setting off it does not touch the network. Tested against the real service on 17-10-2026:
    /// request signature accepted and response signature sha1(data + _r + key) confirmed. The user's operator blocks the IP of s5.* from time to time: hence the
    /// circuit breaker (AceCentralOptions.BreakerCooldown), so that the search does not wait 20 s every time while the block lasts.
    /// Never throws (except OperationCanceledException if the caller cancels): on failure it returns empty and leaves the reason in LastError.
    /// </summary>
    public sealed partial class AceCentralSearchProvider : IRemoteSearchProvider
    {
        private const string Tag = "acesearch";
        private static readonly IReadOnlyList<RemoteResult> None = new RemoteResult[0];

        /// <summary>Notice for the user when turning on the central search in Settings.</summary>
        public const string Notice = "El buscador central de Ace usa las claves de la aplicacion oficial de Ace Stream: puede dejar de funcionar en cualquier momento y su uso puede ir contra las condiciones del servicio. Las busquedas viajan sin cifrar junto con tu direccion IP.";

        /// <summary>
        /// The three s5 names and the route that Ace's signed anti-block list gives for them: the same IP with port 8082, which also
        /// answers the search (tested on 17-10-2026). It goes straight to the IP: it skips a DNS block or one on port 80 only, not a block of the whole IP.
        /// </summary>
        public static readonly string[] DefaultHosts = { "http://s5.torrentstream.net", "http://s5.torrentstream.org", "http://s5.torrentstream.info", "http://51.15.26.167:8082" };

        // Keys embedded in CoreApp.so (both appear in the binary's string table)
        internal static readonly byte[] RequestKey = Encoding.ASCII.GetBytes("xXzhTdAd1pOjL1nDAVI9TXlIBvpy3DVe5cwMao0oBivSrxLysaGwSGEI");
        internal static readonly byte[] ResponseKey = Encoding.ASCII.GetBytes("FEv5QHTKzoTZG5oWTXUpmetdjf4cfSYVs8sZDkbzBdupR6la7xaH3");
        internal const string EngineVersionCode = "3022200", EngineVersionName = "3.2.22", Platform = "android";

        private readonly Func<bool> _isEnabled;
        private readonly AceCentralOptions _opt;
        private readonly HttpClient _http;
        private readonly RemoteResultCache _cache;
        private volatile string _lastError;
        private long _closedUntilTicks;                                             // circuit breaker open until (UTC); 0 = closed

        /// <summary>Whether in a call some host managed to give an HTTP response (even if it was an error or a bad signature): then there is a network and the circuit breaker does not trip.</summary>
        private sealed class CallState { public volatile bool Answered; }

        /// <param name="isEnabled">Evaluated on every call (UserSettings.AceSearchEnabled). Null = always on (only for tests or tools).</param>
        public AceCentralSearchProvider(Func<bool> isEnabled, AceCentralOptions options = null, HttpClient http = null)
        {
            _isEnabled = isEnabled;
            _opt = options ?? new AceCentralOptions();
            _http = http ?? RemoteHttp.Shared;
            _cache = new RemoteResultCache(_opt.CacheTtl);
        }

        public string Name { get { return "Ace"; } }
        public string LastError { get { return _lastError; } }
        public bool IsEnabled { get { return _isEnabled == null || _isEnabled(); } }

        public Task<IReadOnlyList<RemoteResult>> SearchAsync(string query, CancellationToken ct)
        {
            string q = RemoteResultParser.Clean(query, _opt.MaxQueryLength);
            if (q.Length == 0) return Task.FromResult(None);
            return RunAsync("s|" + q.ToLowerInvariant(), q, null, _opt.SearchPageSize, 1, ct);
        }

        public Task<IReadOnlyList<RemoteResult>> ExploreAsync(string kindId, CancellationToken ct)
        {
            string cat = ExploreCategories.For(kindId);
            if (cat == null) return Task.FromResult(None);
            return RunAsync("e|" + cat, null, cat, _opt.ExplorePageSize, Math.Max(1, _opt.ExplorePages), ct);
        }

        private async Task<IReadOnlyList<RemoteResult>> RunAsync(string cacheKey, string query, string category, int pageSize, int pages, CancellationToken ct)
        {
            if (!IsEnabled) { _lastError = null; return None; }                       // off in Settings: not a single request
            IReadOnlyList<RemoteResult> cached;
            if (_cache.TryGet(cacheKey, out cached)) { _lastError = null; return cached; }
            long until = Interlocked.Read(ref _closedUntilTicks), now = _opt.UtcNow().Ticks;
            if (until > now)
            {
                _lastError = "no connection to the central search; will retry in " + (int)Math.Ceiling(TimeSpan.FromTicks(until - now).TotalSeconds) + " s";
                return None;
            }
            var state = new CallState();
            try
            {
                using (var total = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    total.CancelAfter(_opt.TotalTimeout);
                    var all = new List<RemoteResult>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        for (int page = 0; page < pages && all.Count < _opt.MaxResults; page++)
                        {
                            var got = await FetchPageAsync(query, category, page, pageSize, state, total.Token).ConfigureAwait(false);
                            foreach (var r in got.Items)
                            {
                                if (all.Count >= _opt.MaxResults) break;
                                if (seen.Add(r.Url)) all.Add(r);
                            }
                            if (got.Groups < pageSize || (page + 1) * (long)pageSize >= got.Total) break;      // last page
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RemoteFetchException(RemoteHttp.TimeoutMessage); }
                    _cache.Put(cacheKey, all);
                    _lastError = null;
                    return all;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _lastError = ex is RemoteFetchException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                if (!state.Answered && _opt.BreakerCooldown > TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref _closedUntilTicks, (_opt.UtcNow() + _opt.BreakerCooldown).Ticks);
                    _lastError += " (no host answers: pause of " + (int)_opt.BreakerCooldown.TotalSeconds + " s)";
                }
                AceLog.Info(Tag, "central search: " + _lastError);
                return None;
            }
        }

        private sealed class PageResult
        {
            public List<RemoteResult> Items;
            /// <summary>Channels (groups) the page brought and total matches according to the server.</summary>
            public int Groups;
            public long Total;
        }

        /// <summary>A page: tries the hosts (shuffled) until one answers with a correctly signed response. Throws RemoteFetchException if none works.</summary>
        private async Task<PageResult> FetchPageAsync(string query, string category, int page, int pageSize, CallState state, CancellationToken ct)
        {
            var p = BuildParams(query, category, page, pageSize);

            var hosts = _opt.Hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().TrimEnd('/')).ToList();
            if (hosts.Count == 0) throw new RemoteFetchException("no hosts configured");
            Rng.Shuffle(hosts);
            string lastProblem = null;
            foreach (var host in hosts)
            {
                ct.ThrowIfCancellationRequested();
                string nonce = NewNonce();
                string path = BuildPath("a", p, nonce, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Uri uri;
                if (!Uri.TryCreate(host + path, UriKind.Absolute, out uri) || !RemoteHttp.IsHttp(uri)) { lastProblem = "invalid host: " + host; continue; }
                try
                {
                    string body = await RemoteHttp.GetTextAsync(_http, uri, _opt.MaxBytes, _opt.HostTimeout, 2, "application/json", ct).ConfigureAwait(false);
                    state.Answered = true;
                    var res = new PageResult();
                    res.Items = ParseResponse(body, nonce, _opt.MaxDecodedBytes, _opt.MaxResults, out res.Groups, out res.Total);
                    return res;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    // network down, connection cut halfway, HTTP, signature or format: any failure of a host moves on to the next
                    lastProblem = host + ": " + (ex is RemoteFetchException ? ex.Message : ex.GetType().Name + " " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                    if (ex is RemoteFetchException && ex.Message != RemoteHttp.TimeoutMessage) state.Answered = true;       // HTTP 4xx/5xx, redirects, size: there was an answer
                }
            }
            throw new RemoteFetchException("no central search host answered correctly (" + lastProblem + ")");
        }

        /// <summary>Server parameters (already shortened to one letter, without the service ones _v/_n/_p/_r/_t/_s) of a search or of an Explore page.</summary>
        internal Dictionary<string, string> BuildParams(string query, string category, int page, int pageSize)
        {
            var p = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(query)) p["a"] = query;
            if (!string.IsNullOrEmpty(category)) p["c"] = category;
            p["h"] = page.ToString(CultureInfo.InvariantCulture);
            p["i"] = pageSize.ToString(CultureInfo.InvariantCulture);
            p["m"] = "4";                                                              // api_version that forces search_internal
            string lang = Pref(_opt.Lang, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            string langs = Pref(_opt.PreferredLanguages, SafeThreeLetter());
            string countries = Pref(_opt.PreferredCountries, SafeRegion());
            if (lang != null) p["j"] = lang;
            if (langs != null) p["s"] = langs;
            if (countries != null) p["t"] = countries;
            return p;
        }

        private static string Pref(string configured, string fallback)
        {
            if (configured != null) return configured.Length == 0 ? null : configured;
            return string.IsNullOrEmpty(fallback) || fallback == "iv" || fallback == "ivl" ? null : fallback.ToLowerInvariant();
        }

        private static string SafeThreeLetter() { try { return CultureInfo.CurrentUICulture.ThreeLetterISOLanguageName; } catch (Exception) { return null; } }
        private static string SafeRegion() { try { return new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName; } catch (Exception) { return null; } }

        // ---------- Request ----------

        /// <summary>_r = random.randint(1, sys.maxsize).</summary>
        internal static string NewNonce()
        {
            long v = ((long)Rng.Next(0, int.MaxValue) << 31) | (long)Rng.Next(0, int.MaxValue);
            if (v <= 0) v = 1;
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// "/&lt;code&gt;?k=v&amp;...&amp;_s=signature" for the parameters ALREADY shortened (a, c, h, i...). Adds _v, _n, _p, _r and _t; the keys are sorted in ordinal order; the signature carries the
        /// values unescaped and the URL, with quote_plus. Throws ArgumentException if a parameter uses a reserved name.
        /// </summary>
        internal static string BuildPath(string code, IDictionary<string, string> serverParams, string nonce, long unixTime)
        {
            var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in serverParams)
            {
                if (kv.Value == null) continue;
                if (kv.Key == "_r" || kv.Key == "_t" || kv.Key == "_v" || kv.Key == "_s" || kv.Key == "_n" || kv.Key == "_p") throw new ArgumentException("Cannot use reserved parameter '" + kv.Key + "'");
                all[kv.Key] = kv.Value;
            }
            all["_v"] = EngineVersionCode;
            all["_n"] = EngineVersionName;
            all["_p"] = Platform;
            all["_r"] = nonce;
            all["_t"] = unixTime.ToString(CultureInfo.InvariantCulture);
            var sigText = Encoding.UTF8.GetBytes(string.Join("#", all.Select(kv => kv.Key + "=" + kv.Value)));
            var buf = new byte[sigText.Length + RequestKey.Length];
            Buffer.BlockCopy(sigText, 0, buf, 0, sigText.Length);
            Buffer.BlockCopy(RequestKey, 0, buf, sigText.Length, RequestKey.Length);
            string sig = Hex.ToHex(HashUtil.Sha1(buf));
            var sb = new StringBuilder("/").Append(code).Append('?');
            foreach (var kv in all) sb.Append(kv.Key).Append('=').Append(QuotePlus(kv.Value)).Append('&');
            sb.Append("_s=").Append(sig);
            return sb.ToString();
        }

        /// <summary>urllib.parse.quote_plus: letters, digits and _.-~ as they are, space as '+', the rest as %XX (UTF-8).</summary>
        internal static string QuotePlus(string s)
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

        // ---------- Response ----------

        /// <summary>
        /// Verifies the response signature, decodes it and turns it into results. Throws RemoteFetchException if the response is empty, malformed, badly signed or lacks
        /// the required fields (total, time, results). 'groups' is how many results (channels) the page brought and 'total' the total number of matches.
        /// </summary>
        internal static List<RemoteResult> ParseResponse(string text, string nonce, int maxDecodedBytes, int maxResults, out int groups, out long total)
        {
            groups = 0; total = 0;
            if (string.IsNullOrWhiteSpace(text)) throw new RemoteFetchException("empty response");
            object env;
            if (!MiniJson.TryParse(text.Trim(), out env)) throw new RemoteFetchException("malformed response");
            var o = MiniJson.AsObject(env);
            var data = o == null ? null : o.ContainsKey("data") ? o["data"] as string : null;
            var sign = o == null ? null : o.ContainsKey("sign") ? o["sign"] as string : null;
            if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(sign)) throw new RemoteFetchException("malformed response (missing data or sign)");
            // The signed message is the base64 TEXT of 'data' (bytes(data)), not its decoded content
            var dataBytes = Encoding.UTF8.GetBytes(data);
            bool ok = SameHex(Sign(dataBytes, Encoding.ASCII.GetBytes(nonce ?? "")), sign) || SameHex(Sign(dataBytes, null), sign);
            if (!ok) throw new RemoteFetchException("incorrect response signature (bad sign): discarded");
            byte[] json;
            try { json = Convert.FromBase64String(data); } catch (FormatException) { throw new RemoteFetchException("malformed response (base64)"); }
            if (json.Length > maxDecodedBytes) throw new RemoteFetchException("response too large");
            object root;
            if (!MiniJson.TryParse(Encoding.UTF8.GetString(json), out root)) throw new RemoteFetchException("malformed response (JSON)");
            var r = MiniJson.AsObject(root);
            if (r == null || !r.ContainsKey("total") || !r.ContainsKey("time") || !r.ContainsKey("results")) throw new RemoteFetchException("missing required fields (total, time, results)");
            var arr = MiniJson.GetArray(r, "results");
            if (arr == null) throw new RemoteFetchException("results is not a list");
            total = MiniJson.GetLong(r, "total") ?? 0;
            groups = arr.Count;
            return RemoteResultParser.ParseJson(root, Math.Max(1, maxResults));
        }

        private static byte[] Sign(byte[] data, byte[] nonce)
        {
            int n = data.Length + (nonce == null ? 0 : nonce.Length) + ResponseKey.Length;
            var buf = new byte[n];
            Buffer.BlockCopy(data, 0, buf, 0, data.Length);
            int o = data.Length;
            if (nonce != null) { Buffer.BlockCopy(nonce, 0, buf, o, nonce.Length); o += nonce.Length; }
            Buffer.BlockCopy(ResponseKey, 0, buf, o, ResponseKey.Length);
            return HashUtil.Sha1(buf);
        }

        private static bool SameHex(byte[] hash, string hex)
        {
            if (hex == null || hex.Length != hash.Length * 2) return false;
            return HashUtil.ConstantTimeEquals(Encoding.ASCII.GetBytes(Hex.ToHex(hash)), Encoding.ASCII.GetBytes(hex.ToLowerInvariant()));
        }
    }
}
