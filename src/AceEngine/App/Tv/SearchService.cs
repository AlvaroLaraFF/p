using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Search and Explore. The local part is instantaneous: when the catalog changes an index is precomputed (normalized names and Explore kinds of each
    /// channel) and each keystroke only walks that index. The remote part queries all the providers at once, each with its own deadline, and leaves in the result
    /// what is not already in the catalog.
    /// </summary>
    public sealed class SearchService : ISearchService, IDisposable
    {
        private const string Tag = "search";
        public const int MaxRecentQueries = 10;
        public const int MaxQueryLength = 60;
        public const int MaxLocalResults = 500;
        public const int MaxRemoteResults = 300;
        public const int MaxFileBytes = 16 * 1024;
        public const string FileName = "searches.json";

        private static readonly IReadOnlyList<Channel> NoChannels = new Channel[0];
        private static readonly Task<IReadOnlyList<Channel>> NoChannelsTask = Task.FromResult(NoChannels);

        private sealed class Index
        {
            public IReadOnlyList<Channel> Source;
            public string[] Norm;
            public IReadOnlyList<Channel>[] ByKind;
        }

        private readonly IChannelCatalog _catalog;
        private readonly IRemoteSearchProvider[] _providers;
        private readonly string _path;
        private readonly IReadOnlyList<ExploreKind> _kinds;
        private readonly object _indexGate = new object();
        private readonly object _recentGate = new object();
        private volatile Index _index;
        private List<string> _recentList = new List<string>();
        private volatile IReadOnlyList<string> _recent = new string[0];

        /// <summary>Deadline of each remote provider: one that takes longer is ignored without delaying the others.</summary>
        public TimeSpan RemoteTimeout = TimeSpan.FromSeconds(10);

        /// <param name="catalog">The catalog being searched.</param>
        /// <param name="providers">Remote providers (there may be none).</param>
        /// <param name="dataDir">Data directory (recent searches go in data/searches.json). null = no persistence.</param>
        public SearchService(IChannelCatalog catalog, IEnumerable<IRemoteSearchProvider> providers = null, string dataDir = null)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            _catalog = catalog;
            var list = new List<IRemoteSearchProvider>();
            if (providers != null) foreach (var p in providers) if (p != null) list.Add(p);
            _providers = list.ToArray();
            _path = string.IsNullOrEmpty(dataDir) ? null : System.IO.Path.Combine(dataDir, FileName);
            var kinds = new List<ExploreKind>();
            for (int i = 0; i < ExploreRules.Ids.Length; i++) kinds.Add(new ExploreKind { Id = ExploreRules.Ids[i], Name = ExploreRules.Names[i] });
            _kinds = kinds.AsReadOnly();
            LoadRecent();
            _catalog.Changed += OnCatalogChanged;
        }

        public IReadOnlyList<ExploreKind> Kinds { get { return _kinds; } }

        public void Dispose() { _catalog.Changed -= OnCatalogChanged; }

        // ---------- Local index ----------

        private void OnCatalogChanged()
        {
            try { GetIndex(); }                       // recomputed right now, on the notifying thread, so that the first keystroke does not pay for it
            catch (Exception ex) { AceLog.Warn(Tag, "could not rebuild the index: " + ex.Message); }
        }

        private Index GetIndex()
        {
            var channels = _catalog.Channels ?? NoChannels;
            var ix = _index;
            if (ix != null && ReferenceEquals(ix.Source, channels)) return ix;
            lock (_indexGate)
            {
                ix = _index;
                if (ix != null && ReferenceEquals(ix.Source, channels)) return ix;
                ix = Build(channels);
                _index = ix;
                return ix;
            }
        }

        private static Index Build(IReadOnlyList<Channel> channels)
        {
            int n = channels.Count;
            var norm = new string[n];
            var lists = new List<Channel>[ExploreRules.Ids.Length];
            for (int k = 0; k < lists.Length; k++) lists[k] = new List<Channel>();
            for (int i = 0; i < n; i++)
            {
                var c = channels[i];
                norm[i] = TextNorm.Normalize(c.Name);
                int mask = ExploreRules.Classify(c);
                for (int k = 0; k < lists.Length; k++) if ((mask & (1 << k)) != 0) lists[k].Add(c);
            }
            var byKind = new IReadOnlyList<Channel>[lists.Length];
            for (int k = 0; k < lists.Length; k++) byKind[k] = lists[k].ToArray();
            return new Index { Source = channels, Norm = norm, ByKind = byKind };
        }

        // ---------- Local search ----------

        public IReadOnlyList<Channel> Search(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return NoChannels;
            string q = TextNorm.Normalize(text.Length > 200 ? text.Substring(0, 200) : text);
            if (q.Length == 0) return NoChannels;
            var tokens = q.IndexOf(' ') < 0 ? new[] { q } : q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ix = GetIndex();
            var channels = ix.Source;
            List<Channel> startsWith = null, wordStarts = null, contains = null;
            for (int i = 0; i < ix.Norm.Length; i++)
            {
                string nm = ix.Norm[i];
                bool all = true;
                for (int t = 0; t < tokens.Length; t++)
                    if (nm.IndexOf(tokens[t], StringComparison.Ordinal) < 0) { all = false; break; }
                if (!all) continue;
                if (nm.StartsWith(q, StringComparison.Ordinal)) (startsWith ?? (startsWith = new List<Channel>())).Add(channels[i]);
                else if (AllAtWordStart(nm, tokens)) (wordStarts ?? (wordStarts = new List<Channel>())).Add(channels[i]);
                else (contains ?? (contains = new List<Channel>())).Add(channels[i]);
            }
            int total = (startsWith == null ? 0 : startsWith.Count) + (wordStarts == null ? 0 : wordStarts.Count) + (contains == null ? 0 : contains.Count);
            if (total == 0) return NoChannels;
            var res = new Channel[Math.Min(total, MaxLocalResults)];
            int pos = 0;
            Fill(res, ref pos, startsWith);
            Fill(res, ref pos, wordStarts);
            Fill(res, ref pos, contains);
            return res;
        }

        private static void Fill(Channel[] into, ref int pos, List<Channel> from)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count && pos < into.Length; i++) into[pos++] = from[i];
        }

        /// <summary>Each searched word is the start of some word of the name.</summary>
        private static bool AllAtWordStart(string name, string[] tokens)
        {
            for (int t = 0; t < tokens.Length; t++)
            {
                bool found = false;
                int from = 0;
                while (!found)
                {
                    int at = name.IndexOf(tokens[t], from, StringComparison.Ordinal);
                    if (at < 0) break;
                    if (at == 0 || name[at - 1] == ' ') found = true;
                    else from = at + 1;
                }
                if (!found) return false;
            }
            return true;
        }

        // ---------- Explore ----------

        public IReadOnlyList<Channel> Explore(string kindId)
        {
            int k = KindIndex(kindId);
            return k < 0 ? NoChannels : GetIndex().ByKind[k];
        }

        private static int KindIndex(string kindId)
        {
            if (kindId == null) return -1;
            for (int i = 0; i < ExploreRules.Ids.Length; i++)
                if (string.Equals(ExploreRules.Ids[i], kindId, StringComparison.Ordinal)) return i;
            return -1;
        }

        // ---------- Remote ----------

        public Task<IReadOnlyList<Channel>> SearchRemoteAsync(string text, CancellationToken ct)
        {
            if (_providers.Length == 0) return NoChannelsTask;
            string q = TextNorm.CleanSpaces(text, 200);
            if (q.Length == 0) return NoChannelsTask;
            return RemoteAsync((p, t) => p.SearchAsync(q, t), ct);
        }

        public Task<IReadOnlyList<Channel>> ExploreRemoteAsync(string kindId, CancellationToken ct)
        {
            if (_providers.Length == 0 || KindIndex(kindId) < 0) return NoChannelsTask;
            return RemoteAsync((p, t) => p.ExploreAsync(kindId, t), ct);
        }

        private async Task<IReadOnlyList<Channel>> RemoteAsync(Func<IRemoteSearchProvider, CancellationToken, Task<IReadOnlyList<RemoteResult>>> call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var tasks = new Task<IReadOnlyList<RemoteResult>>[_providers.Length];
            for (int i = 0; i < tasks.Length; i++) tasks[i] = RunProviderAsync(_providers[i], call, ct);
            var lists = await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var byId = new Dictionary<string, CatalogBuilder.Candidate>(StringComparer.Ordinal);
            var cats = new Dictionary<string, string>(StringComparer.Ordinal);
            var metas = new Dictionary<string, RemoteResult>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var list in lists)
            {
                int taken = 0;
                foreach (var r in list)
                {
                    if (r == null) continue;
                    if (++taken > MaxRemoteResults) break;                           // a provider cannot fill the memory
                    if (r.Url == null || r.Url.Length > MyLinksPasteClient.MaxUrlLength) continue;                  // a cut-off link would be another link
                    var entry = LinkEntry.FromUrl(TvFiles.Clip(r.Name, MyLinksPasteClient.MaxNameLength), r.Url);
                    if (!entry.IsProbeable || string.IsNullOrEmpty(entry.Key)) continue;
                    if (_catalog.Find(entry.Key) != null) continue;                 // already in the catalog
                    var cand = CatalogBuilder.MakeCandidate(entry);
                    CatalogBuilder.Candidate prev;
                    if (byId.TryGetValue(entry.Key, out prev))
                    {
                        if (CatalogBuilder.IsBetter(cand, prev)) byId[entry.Key] = cand;
                        continue;
                    }
                    if (order.Count >= MaxRemoteResults) continue;
                    byId[entry.Key] = cand;
                    order.Add(entry.Key);
                    cats[entry.Key] = r.Category ?? "";
                    metas[entry.Key] = r;
                }
            }
            if (order.Count == 0) return NoChannels;
            var res = new Channel[order.Count];
            for (int i = 0; i < res.Length; i++)
            {
                string id = order[i];
                var c = byId[id];
                string ck = "", cn = "";
                if (cats[id].Trim().Length > 0) CategoryNames.Resolve(cats[id], out ck, out cn);   // informational: it does not go into the catalog
                var meta = metas[id];
                res[i] = new Channel
                {
                    Id = id, Name = c.Name, SourceName = c.Source, Quality = c.Quality, CategoryKey = ck, CategoryName = cn, Entry = c.Entry,
                    MetaResolution = ChannelResolution.FromBitrateKbps(meta.BitrateKbps),
                    Language = ChannelLanguages.Detect(c.Name, c.Source, cats[id], meta.Languages, meta.Countries)
                };
            }
            return res;
        }

        private async Task<IReadOnlyList<RemoteResult>> RunProviderAsync(IRemoteSearchProvider provider, Func<IRemoteSearchProvider, CancellationToken, Task<IReadOnlyList<RemoteResult>>> call, CancellationToken ct)
        {
            try
            {
                var r = await AsyncUtil.WithTimeout(t => call(provider, t), RemoteTimeout, ct).ConfigureAwait(false);
                return r ?? new RemoteResult[0];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "provider '" + provider.Name + "' failed: " + ex.GetType().Name + ": " + ex.Message);
                return new RemoteResult[0];
            }
        }

        // ---------- Recent searches ----------

        public IReadOnlyList<string> RecentQueries { get { return _recent; } }

        public void AddRecentQuery(string text)
        {
            string clean = TextNorm.CleanSpaces(text, MaxQueryLength);
            string key = TextNorm.Normalize(clean);
            if (key.Length == 0) return;
            lock (_recentGate)
            {
                var list = new List<string>(MaxRecentQueries) { clean };
                foreach (var s in _recentList)
                {
                    if (string.Equals(TextNorm.Normalize(s), key, StringComparison.Ordinal)) continue;
                    list.Add(s);
                    if (list.Count >= MaxRecentQueries) break;
                }
                _recentList = list;
                _recent = list.AsReadOnly();
                Save(list);
            }
        }

        private void Save(List<string> list)
        {
            if (_path == null) return;
            TvFiles.WriteAtomic(_path, MiniJson.Stringify(new Dictionary<string, object> { { "v", 1L }, { "items", list } }));
        }

        private void LoadRecent()
        {
            string text = TvFiles.ReadBounded(_path, MaxFileBytes);
            if (text == null) return;
            try
            {
                object v;
                if (!MiniJson.TryParse(text, out v)) return;
                var items = MiniJson.GetArray(MiniJson.AsObject(v), "items");
                if (items == null) return;
                var list = new List<string>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    string s = item as string;
                    if (s == null) continue;
                    s = TextNorm.CleanSpaces(s, MaxQueryLength);
                    string key = TextNorm.Normalize(s);
                    if (key.Length == 0 || !keys.Add(key)) continue;
                    list.Add(s);
                    if (list.Count >= MaxRecentQueries) break;
                }
                _recentList = list;
                _recent = list.AsReadOnly();
            }
            catch (Exception ex) { AceLog.Warn(Tag, "the stored recent searches could not be interpreted: " + ex.Message); }
        }
    }
}
