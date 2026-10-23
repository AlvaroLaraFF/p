using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    public sealed class HttpSearchOptions
    {
        /// <summary>Deadline of each request (connection, redirects and body).</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(8);
        /// <summary>Maximum size of the response (decompressed).</summary>
        public int MaxBytes = 1024 * 1024;
        public int MaxRedirects = 3;
        /// <summary>Results per page when searching and exploring (page_size of /search: 25 in the official app, 100 to dump a catalog).</summary>
        public int SearchPageSize = 25, ExplorePageSize = 100;
        /// <summary>Results returned at most per call.</summary>
        public int MaxResults = 200;
        /// <summary>In-memory cache of results per query (120 s in the official engine). Zero = no cache.</summary>
        public TimeSpan CacheTtl = TimeSpan.FromSeconds(120);
        public int MaxQueryLength = 200;
    }

    /// <summary>
    /// Search provider configurable by the user: a URL with {query} that returns the /search format of the Ace engine (JSON) or an
    /// M3U list (/search/m3u). The template accepts {query}, {category}, {page} and {page_size}; if it has neither {query} nor {category} it is treated as the base URL of a
    /// /search and ?query=&amp;page=&amp;page_size=[&amp;category=] are appended to it, as the official app does.
    /// Security: http/https only; the query is encoded (it cannot alter the URL, and {query} cannot be in the host); redirects by hand (at most 3, only to
    /// http/https, never from https to http); response of 1 MiB at most and a deadline of 8 s; the content is UNTRUSTED input and is bounded (RemoteResultParser).
    /// Never throws (except OperationCanceledException if the caller cancels): on failure it returns an empty list and leaves the reason in LastError.
    /// ExploreAsync: if the template admits a category ({category} or base URL) it asks for the Ace catalog category of the kind (ExploreCategories); otherwise it returns empty.
    /// </summary>
    public sealed class HttpSearchProvider : IRemoteSearchProvider
    {
        private const string Tag = "search";
        private static readonly IReadOnlyList<RemoteResult> None = new RemoteResult[0];
        private readonly string _name;
        private readonly string _template;
        private readonly string _invalid;
        private readonly bool _templateMode, _hasQuery, _hasCategory;
        private readonly HttpSearchOptions _opt;
        private readonly HttpClient _http;
        private readonly RemoteResultCache _cache;
        private volatile string _lastError;

        public HttpSearchProvider(SearchProviderConfig config, HttpSearchOptions options = null, HttpClient http = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            _opt = options ?? new HttpSearchOptions();
            _http = http ?? RemoteHttp.Shared;
            _cache = new RemoteResultCache(_opt.CacheTtl);
            string url = (config.Url ?? "").Trim();
            _template = url;
            _hasQuery = url.Contains("{query}");
            _hasCategory = url.Contains("{category}");
            _templateMode = _hasQuery || _hasCategory;
            Uri u;
            if (url.Length == 0 || url.Length > RemoteResultParser.MaxUrlLength) _invalid = "URL empty or too long";
            else if (!Uri.TryCreate(url.Replace("{query}", "q").Replace("{category}", "c").Replace("{page_size}", "1").Replace("{page}", "0"), UriKind.Absolute, out u) || !RemoteHttp.IsHttp(u)) _invalid = "the provider URL must be http or https";
            else if (HostHasPlaceholder(url)) _invalid = "a template cannot have {query} or {category} in the host";
            string n = (config.Name ?? "").Trim();
            if (n.Length == 0) { Uri hu; n = Uri.TryCreate(url, UriKind.Absolute, out hu) ? hu.Host : "provider"; }
            _name = RemoteResultParser.Clean(n, 100);
        }

        public string Name { get { return _name; } }
        /// <summary>Reason for the last failure, or null if the last call went well.</summary>
        public string LastError { get { return _lastError; } }
        /// <summary>false if the configured URL is no good (not http/https, empty...): the provider exists but queries nothing.</summary>
        public bool IsValid { get { return _invalid == null; } }
        /// <summary>true if Explore can ask this provider for a category.</summary>
        public bool SupportsCategory { get { return _hasCategory || !_templateMode; } }

        private static bool HostHasPlaceholder(string template)
        {
            int scheme = template.IndexOf("://", StringComparison.Ordinal);
            int pathStart = template.IndexOf('/', scheme < 0 ? 0 : scheme + 3);
            string authority = pathStart < 0 ? template : template.Substring(0, pathStart);
            return authority.Contains("{");
        }

        public Task<IReadOnlyList<RemoteResult>> SearchAsync(string query, CancellationToken ct)
        {
            string q = RemoteResultParser.Clean(query, _opt.MaxQueryLength);
            if (q.Length == 0) return Task.FromResult(None);
            if (_templateMode && !_hasQuery) { _lastError = "the template does not accept {query}"; return Task.FromResult(None); }
            return GetAsync("s|" + q.ToLowerInvariant(), () => BuildUrl(q, null, _opt.SearchPageSize), ct);
        }

        public Task<IReadOnlyList<RemoteResult>> ExploreAsync(string kindId, CancellationToken ct)
        {
            string cat = ExploreCategories.For(kindId);
            if (cat == null || !SupportsCategory) return Task.FromResult(None);
            return GetAsync("e|" + cat, () => BuildUrl(null, cat, _opt.ExplorePageSize), ct);
        }

        /// <summary>Final URL of a query (public for the tests). query == null: no text (catalog by category).</summary>
        public string BuildUrl(string query, string category, int pageSize)
        {
            if (_templateMode)
            {
                return _template.Replace("{query}", Uri.EscapeDataString(query ?? ""))
                    .Replace("{category}", Uri.EscapeDataString(category ?? ""))
                    .Replace("{page_size}", pageSize.ToString(CultureInfo.InvariantCulture))
                    .Replace("{page}", "0");
            }
            string sep = _template.IndexOf('?') < 0 ? "?" : (_template.EndsWith("?", StringComparison.Ordinal) || _template.EndsWith("&", StringComparison.Ordinal) ? "" : "&");
            var s = _template + sep;
            if (!string.IsNullOrEmpty(query)) s += "query=" + Uri.EscapeDataString(query) + "&";
            s += "page=0&page_size=" + pageSize.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(category)) s += "&category=" + Uri.EscapeDataString(category);
            return s;
        }

        private async Task<IReadOnlyList<RemoteResult>> GetAsync(string cacheKey, Func<string> buildUrl, CancellationToken ct)
        {
            if (_invalid != null) { _lastError = _invalid; return None; }
            IReadOnlyList<RemoteResult> cached;
            if (_cache.TryGet(cacheKey, out cached)) { _lastError = null; return cached; }
            try
            {
                Uri uri;
                if (!Uri.TryCreate(buildUrl(), UriKind.Absolute, out uri)) throw new RemoteFetchException("invalid URL");
                string text = await RemoteHttp.GetTextAsync(_http, uri, _opt.MaxBytes, _opt.Timeout, _opt.MaxRedirects, "application/json, audio/x-mpegurl;q=0.9, */*;q=0.5", ct).ConfigureAwait(false);
                var list = RemoteResultParser.ParseAuto(text, _opt.MaxResults);
                _cache.Put(cacheKey, list);
                _lastError = null;
                return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _lastError = ex is RemoteFetchException ? ex.Message : ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                AceLog.Debug(Tag, _name + ": " + _lastError);
                return None;
            }
        }
    }
}
