using System;
using System.Collections.Generic;
using System.Linq;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Builds the list of ACTIVE search providers from AppConfig.SearchProviders and the user's settings. GetProviders() is called on every
    /// search and queries the settings again each time: turning the central search on or off in Settings takes effect live, without rebuilding anything.
    ///  - The configured providers (HttpSearchProvider) are reused as long as neither their name nor their URL changes, to keep their 120 s cache; those removed from
    ///    the configuration are discarded. At most AppConfig.MaxProviders.
    ///  - The Ace central search (AceCentralSearchProvider) is only in the list if UserSettings.AceSearchEnabled is true AT THAT MOMENT; in addition the provider itself
    ///    checks it again on every call, so that a reference someone keeps stops querying the network as soon as it is turned off.
    /// Safe from several threads.
    /// </summary>
    public sealed class SearchProviderFactory
    {
        private readonly Func<AppConfig> _config;
        private readonly UserSettings _settings;
        private readonly HttpSearchOptions _httpOptions;
        private readonly AceCentralOptions _aceOptions;
        private readonly object _gate = new object();
        private readonly Dictionary<string, HttpSearchProvider> _http = new Dictionary<string, HttpSearchProvider>(StringComparer.Ordinal);
        private AceCentralSearchProvider _ace;

        public SearchProviderFactory(AppConfig config, UserSettings settings, HttpSearchOptions httpOptions = null, AceCentralOptions aceOptions = null)
            : this(() => config, settings, httpOptions, aceOptions)
        {
            if (config == null) throw new ArgumentNullException("config");
        }

        /// <summary>With the configuration in a function, for whoever may replace the whole AppConfig.</summary>
        public SearchProviderFactory(Func<AppConfig> config, UserSettings settings, HttpSearchOptions httpOptions = null, AceCentralOptions aceOptions = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (settings == null) throw new ArgumentNullException("settings");
            _config = config;
            _settings = settings;
            _httpOptions = httpOptions;
            _aceOptions = aceOptions;
        }

        /// <summary>Providers to query now, in order: first the ones configured by the user and, if enabled, the Ace central search.</summary>
        public IReadOnlyList<IRemoteSearchProvider> GetProviders()
        {
            var list = new List<IRemoteSearchProvider>();
            var cfg = _config();
            lock (_gate)
            {
                var keep = new HashSet<string>(StringComparer.Ordinal);
                if (cfg != null)
                {
                    var configured = cfg.SearchProviders.ToList();                    // copy: the list can be changed from another thread
                    foreach (var pc in configured)
                    {
                        if (pc == null || list.Count >= AppConfig.MaxProviders) continue;
                        string key = (pc.Name ?? "") + "\n" + (pc.Url ?? "");
                        if (!keep.Add(key)) continue;                                  // two equal entries: a single query
                        HttpSearchProvider p;
                        if (!_http.TryGetValue(key, out p))
                        {
                            p = new HttpSearchProvider(new SearchProviderConfig { Name = pc.Name, Url = pc.Url }, _httpOptions);
                            _http[key] = p;
                        }
                        if (p.IsValid) list.Add(p);
                    }
                }
                foreach (var gone in _http.Keys.Where(k => !keep.Contains(k)).ToList()) _http.Remove(gone);
                if (_settings.AceSearchEnabled)
                {
                    if (_ace == null) _ace = new AceCentralSearchProvider(() => _settings.AceSearchEnabled, _aceOptions);
                    list.Add(_ace);
                }
            }
            return list;
        }
    }
}
