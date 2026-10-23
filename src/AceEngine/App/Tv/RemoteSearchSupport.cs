using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// From the 12 Explore kinds (ExploreKind.Id) to the Ace Stream catalog category (the 20 values of validate_channel_category / category_values of the
    /// official app: informational, entertaining, educational, movies, documentaries, sport, fashion, music, regional, ethnic, religion, teleshop, erotic_18_plus,
    /// other_18_plus, cyber_games, amateur, webcam, kids, series, promotion; plus other). The catalog has no sport subcategories: the five sport kinds
    /// ask for "sport" and the fine classification (football, basketball...) is done by the search service by keywords in the name.
    /// "internacional" -> "regional" is a CHOICE OF OUR OWN (the catalog has no "international" category).
    /// </summary>
    public static class ExploreCategories
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "futbol", "sport" }, { "baloncesto", "sport" }, { "motor", "sport" }, { "tenis", "sport" }, { "deportes", "sport" },
            { "noticias", "informational" }, { "cine", "movies" }, { "series", "series" }, { "documentales", "documentaries" },
            { "musica", "music" }, { "infantil", "kids" }, { "internacional", "regional" }
        };

        /// <summary>Ace catalog category for an Explore kind, or null if there is none.</summary>
        public static string For(string kindId)
        {
            string c;
            return kindId != null && Map.TryGetValue(kindId.Trim(), out c) ? c : null;
        }
    }

    /// <summary>A provider could not give results (network, format, limits). The providers catch it and leave it in LastError: it does not leave them.</summary>
    internal sealed class RemoteFetchException : Exception
    {
        public RemoteFetchException(string message) : base(message) { }
    }

    /// <summary>Bounded text download for the providers: http/https only, maximum deadline and size, redirects by hand (it does not go to odd schemes).</summary>
    internal static class RemoteHttp
    {
        /// <summary>Message of RemoteFetchException when a host does not answer in time (no HTTP response arrived).</summary>
        internal const string TimeoutMessage = "timed out";

        public const string UserAgent = "P-TV/1.0";
        private static HttpClient _shared;
        private static readonly object Gate = new object();

        /// <summary>One HttpClient for all the providers: no automatic redirection, no cookies, with decompression (bounded when reading) and no timeout of its own (each request sets it).</summary>
        public static HttpClient Shared
        {
            get
            {
                lock (Gate)
                {
                    if (_shared == null)
                    {
                        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
                        _shared = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                    }
                    return _shared;
                }
            }
        }

        public static bool IsHttp(Uri u) { return u != null && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps); }

        public static async Task<string> GetTextAsync(HttpClient http, Uri uri, int maxBytes, TimeSpan timeout, int maxRedirects, string accept, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try
                {
                    var current = uri;
                    for (int hop = 0; ; hop++)
                    {
                        if (!IsHttp(current)) throw new RemoteFetchException("only http or https URLs are accepted");
                        using (var req = new HttpRequestMessage(HttpMethod.Get, current))
                        {
                            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                            if (accept != null) req.Headers.TryAddWithoutValidation("Accept", accept);
                            using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                            {
                                int code = (int)resp.StatusCode;
                                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                                {
                                    if (hop >= maxRedirects) throw new RemoteFetchException("too many redirects");
                                    var loc = resp.Headers.Location;
                                    if (loc == null) throw new RemoteFetchException("redirect without a target");
                                    var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                                    if (!IsHttp(next)) throw new RemoteFetchException("redirect to a disallowed scheme (" + next.Scheme + ")");
                                    if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp) throw new RemoteFetchException("redirect from https to http rejected");
                                    current = next;
                                    continue;
                                }
                                if (!resp.IsSuccessStatusCode) throw new RemoteFetchException("HTTP " + code);
                                byte[] body;
                                try { body = await HttpLimits.ReadBodyAsync(resp.Content, maxBytes, cts.Token).ConfigureAwait(false); }
                                catch (System.IO.InvalidDataException) { throw new RemoteFetchException("response too large (maximum " + maxBytes + " bytes)"); }
                                var text = Encoding.UTF8.GetString(body);
                                return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new RemoteFetchException(TimeoutMessage);
                }
            }
        }
    }

    /// <summary>In-memory cache of results (120 s like search_memory_cache_ttl), bounded. Returns copies: whoever receives it can modify them.</summary>
    internal sealed class RemoteResultCache
    {
        private sealed class Entry { public DateTime AtUtc; public List<RemoteResult> Items; }
        private readonly TimeSpan _ttl;
        private readonly int _max;
        private readonly Func<DateTime> _now;
        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public RemoteResultCache(TimeSpan ttl, int max = 32, Func<DateTime> now = null)
        {
            _ttl = ttl; _max = Math.Max(1, max); _now = now ?? (() => DateTime.UtcNow);
        }

        public bool TryGet(string key, out IReadOnlyList<RemoteResult> items)
        {
            items = null;
            if (_ttl <= TimeSpan.Zero) return false;
            lock (_map)
            {
                Entry e;
                if (!_map.TryGetValue(key, out e)) return false;
                if (_now() - e.AtUtc >= _ttl) { _map.Remove(key); return false; }
                items = e.Items.Select(Clone).ToList();
                return true;
            }
        }

        public void Put(string key, IReadOnlyList<RemoteResult> items)
        {
            if (_ttl <= TimeSpan.Zero) return;
            lock (_map)
            {
                if (_map.Count >= _max && !_map.ContainsKey(key))
                {
                    var victim = _map.OrderBy(kv => kv.Value.AtUtc).First().Key;
                    _map.Remove(victim);
                }
                _map[key] = new Entry { AtUtc = _now(), Items = items.Select(Clone).ToList() };
            }
        }

        private static RemoteResult Clone(RemoteResult r)
        {
            return new RemoteResult { Name = r.Name, Url = r.Url, Category = r.Category, Peers = r.Peers, Seeds = r.Seeds, BitrateKbps = r.BitrateKbps, Languages = r.Languages, Countries = r.Countries };
        }
    }

    /// <summary>
    /// Interprets the response of a search provider. The list that arrives is UNTRUSTED input: everything is bounded (lengths, count, types) and what is not
    /// understood is discarded without throwing. Formats: JSON of the engine's /search ({"result":{"results":[{"name","items":[...]}]}}, with items of long or
    /// compact names n/i/u/b/c/a/au/ci/s/d), a bare JSON list, or an M3U list (/search/m3u: #EXTINF + http://host/ace/getstream?infohash=...).
    /// </summary>
    internal static class RemoteResultParser
    {
        public const int MaxNameLength = 200, MaxCategoryLength = 60, MaxUrlLength = 2048;

        /// <summary>Text to display: no controls or text-direction marks, spaces collapsed, trimmed without splitting a surrogate pair.</summary>
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            bool lastSpace = true;
            for (int i = 0; i < s.Length && sb.Length < max; i++)
            {
                char ch = s[i];
                if (char.IsHighSurrogate(ch))
                {
                    // a complete surrogate pair is kept (if it fits); a lone one (garbage) is dropped
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) && sb.Length + 2 <= max) { sb.Append(ch).Append(s[i + 1]); lastSpace = false; i++; }
                    continue;
                }
                if (char.IsLowSurrogate(ch)) continue;
                bool blank = char.IsWhiteSpace(ch) || char.IsControl(ch) || (ch >= '\u202A' && ch <= '\u202E') || (ch >= '\u2066' && ch <= '\u2069') || ch == '\u200E' || ch == '\u200F' || ch == '\uFEFF' || ch == '\u200B';
                if (blank) { if (!lastSpace) { sb.Append(' '); lastSpace = true; } continue; }
                sb.Append(ch);
                lastSpace = false;
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.ToString();
        }

        /// <summary>A result link in the form the engine knows how to play (acestream://, magnet: or http stream URL); null if it is no good.</summary>
        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string u = raw.Trim();
            if (u.Length > MaxUrlLength) return null;
            foreach (char c in u) if (char.IsControl(c) || char.IsWhiteSpace(c)) return null;
            if (Hex.IsHex40(u)) return "acestream://" + u.ToLowerInvariant();
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (!Uri.TryCreate(u, UriKind.Absolute, out uri)) return null;
                // The Ace engine exposes its results as http://host:6878/ace/getstream?infohash=<40hex> (or ?id=<content_id>): the useful link is the infohash / the id
                if (uri.AbsolutePath.StartsWith("/ace/", StringComparison.OrdinalIgnoreCase))
                {
                    string ih = QueryValue(uri.Query, "infohash");
                    if (ih != null && Hex.IsHex40(ih)) return "magnet:?xt=urn:btih:" + ih.ToLowerInvariant();
                    string id = QueryValue(uri.Query, "id") ?? QueryValue(uri.Query, "content_id") ?? QueryValue(uri.Query, "pid");
                    if (id != null && Hex.IsHex40(id)) return "acestream://" + id.ToLowerInvariant();
                }
            }
            var e = LinkEntry.FromUrl("", u);
            return e.Kind == LinkKind.AceStream || e.Kind == LinkKind.Magnet || e.Kind == LinkKind.HttpStream ? u : null;
        }

        private static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(part.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Uri.UnescapeDataString(part.Substring(eq + 1)); } catch (UriFormatException) { return null; }
            }
            return null;
        }

        /// <summary>Detects the format from the content (the Content-Type of a home-made provider cannot be trusted) and interprets it. Throws RemoteFetchException if it is none of them.</summary>
        public static List<RemoteResult> ParseAuto(string text, int max)
        {
            if (text == null) throw new RemoteFetchException("empty response");
            var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.Length == 0) throw new RemoteFetchException("empty response");
            if (t[0] == '{' || t[0] == '[')
            {
                object root;
                if (!MiniJson.TryParse(t, out root)) throw new RemoteFetchException("malformed JSON");
                return ParseJson(root, max);
            }
            if (t.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || t.IndexOf("#EXTINF", StringComparison.OrdinalIgnoreCase) >= 0) return ParseM3u(t, max);
            throw new RemoteFetchException("unrecognized response format (neither JSON nor M3U)");
        }

        public static List<RemoteResult> ParseM3u(string text, int max)
        {
            var res = new List<RemoteResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in M3uParser.Parse(text))
            {
                if (res.Count >= max) break;
                var url = NormalizeUrl(e.Url);
                if (url == null || !seen.Add(url)) continue;
                string name = Clean(e.Name, MaxNameLength);
                if (name.Length == 0) continue;
                res.Add(new RemoteResult { Name = name, Url = url, Category = Clean(e.Group, MaxCategoryLength) });
            }
            return res;
        }

        public static List<RemoteResult> ParseJson(object root, int max)
        {
            List<object> results = null;
            var obj = MiniJson.AsObject(root);
            if (obj != null)
            {
                object err;
                if (obj.TryGetValue("error", out err) && IsRealError(err)) throw new RemoteFetchException("the provider returned an error: " + Clean(ErrorText(err), 120));
                object inner;
                if (obj.TryGetValue("result", out inner))
                {
                    results = MiniJson.AsArray(inner);
                    var io = MiniJson.AsObject(inner);
                    if (results == null && io != null) results = MiniJson.GetArray(io, "results");
                }
                else results = MiniJson.GetArray(obj, "results") ?? MiniJson.GetArray(obj, "items");
            }
            else results = MiniJson.AsArray(root);
            if (results == null) throw new RemoteFetchException("the JSON has no results list");

            var res = new List<RemoteResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in results)
            {
                if (res.Count >= max) break;
                var d = MiniJson.AsObject(r);
                if (d == null) continue;
                var items = MiniJson.GetArray(d, "items");
                if (items != null)
                {
                    string group = Str(d, "name", "n");
                    foreach (var it in items)
                    {
                        if (res.Count >= max) break;
                        var item = FromItem(MiniJson.AsObject(it), group);
                        if (item != null && seen.Add(item.Url)) res.Add(item);
                    }
                }
                else
                {
                    var item = FromItem(d, null);
                    if (item != null && seen.Add(item.Url)) res.Add(item);
                }
            }
            return res;
        }

        private static bool IsRealError(object err)
        {
            if (err == null) return false;
            if (err is bool) return (bool)err;
            var s = err as string;
            if (s != null) return s.Trim().Length > 0;
            return true;                                                    // {"code":..., "message":...} or something else
        }

        private static string ErrorText(object err)
        {
            var o = MiniJson.AsObject(err);
            return o != null ? (Str(o, "message", "msg") ?? "error") : (err as string ?? "error");
        }

        /// <summary>An item (long or compact names) to RemoteResult; null if it is disabled, has no useful link or has no name.</summary>
        public static RemoteResult FromItem(Dictionary<string, object> it, string groupName)
        {
            if (it == null) return null;
            if (Truthy(it, "disabled") || Truthy(it, "d")) return null;                // those marked as disabled by the provider are not shown
            string name = Clean(Str(it, "name", "n") ?? groupName, MaxNameLength);
            if (name.Length == 0) return null;
            string url = null;
            string ih = Str(it, "infohash", "i");
            if (ih != null && Hex.IsHex40(ih.Trim())) url = "magnet:?xt=urn:btih:" + ih.Trim().ToLowerInvariant();
            if (url == null)
            {
                string cid = Str(it, "content_id");
                if (cid != null && Hex.IsHex40(cid.Trim())) url = "acestream://" + cid.Trim().ToLowerInvariant();
            }
            if (url == null) url = NormalizeUrl(Str(it, "url", "u"));
            if (url == null) return null;
            string cat = null;
            object c;
            if (it.TryGetValue("categories", out c) || it.TryGetValue("c", out c))
            {
                var arr = MiniJson.AsArray(c);
                if (arr != null) { foreach (var x in arr) { var s = x as string; if (!string.IsNullOrWhiteSpace(s)) { cat = s; break; } } }
                else cat = c as string;
            }
            return new RemoteResult
            {
                Name = name, Url = url, Category = Clean(cat, MaxCategoryLength),
                Peers = Count(it, "peers", "leechers"), Seeds = Count(it, "seeds", "seeders"),
                BitrateKbps = Bitrate(it), Languages = Strings(it, "languages"), Countries = Strings(it, "countries")
            };
        }

        /// <summary>Bitrate of the item (the "bitrate"/"b" field is in bit/s) in kbit/s; 0 if missing or absurd.</summary>
        private static int Bitrate(Dictionary<string, object> it)
        {
            foreach (var k in new[] { "bitrate", "b" })
            {
                object v;
                if (!it.TryGetValue(k, out v) || v == null) continue;
                double x = v is long ? (long)v : v is double ? (double)v : -1;
                if (double.IsNaN(x) || x <= 0 || x > 1e9) continue;
                return (int)Math.Min(125000, x * 8 / 1000);                     // Ace gives the bitrate in BYTES per second (like the live descriptor; see LiveSwarm)
            }
            return 0;
        }

        /// <summary>Short list of texts of a field ("languages": ["eng"]); at most 4 of up to 8 letters.</summary>
        private static string[] Strings(Dictionary<string, object> it, string key)
        {
            object v;
            if (!it.TryGetValue(key, out v) || v == null) return new string[0];
            var res = new List<string>();
            var arr = MiniJson.AsArray(v);
            if (arr != null) { foreach (var x in arr) { var s = x as string; if (!string.IsNullOrWhiteSpace(s) && s.Length <= 8 && res.Count < 4) res.Add(s.Trim()); } }
            else { var s = v as string; if (!string.IsNullOrWhiteSpace(s)) foreach (var p in s.Split(',')) { var t = p.Trim(); if (t.Length > 0 && t.Length <= 8 && res.Count < 4) res.Add(t); } }
            return res.ToArray();
        }

        private static string Str(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys)
            {
                var s = MiniJson.GetString(d, k);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        private static bool Truthy(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            if (v is long) return (long)v != 0;
            if (v is double) return (double)v != 0;
            var s = v as string;
            return s != null && (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Non-negative and reasonable number (&lt;= 1 000 000), or -1 if the provider does not give it or it is garbage.</summary>
        private static int Count(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys)
            {
                object v;
                if (!d.TryGetValue(k, out v) || v == null) continue;
                double x;
                if (v is long) x = (long)v;
                else if (v is double) x = (double)v;
                else continue;
                if (double.IsNaN(x) || x < 0 || x > 1000000) return -1;
                return (int)x;
            }
            return -1;
        }
    }
}
