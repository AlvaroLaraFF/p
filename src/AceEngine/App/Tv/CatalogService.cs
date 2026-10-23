using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// The app's catalog: loads the configured lists (AppConfig.Lists) in parallel, merges them into one with <see cref="CatalogBuilder"/> and saves a
    /// copy on disk (data/catalog.json) so that at startup it shows right away and refreshes in the background. If a list fails, the others are shown
    /// (and for the one that failed its last saved copy is kept); Error reports it. The collections it exposes are snapshots that do not change: each change
    /// publishes new ones and fires Changed on a pool thread.
    /// </summary>
    public sealed class CatalogService : IChannelCatalog, IDisposable
    {
        private const string Tag = "catalog";
        public const int MaxCacheBytes = 8 * 1024 * 1024;
        public const int MaxEntriesPerList = 30000;
        public const int MaxCachedEntries = 40000;
        public const string CacheFileName = "catalog.json";

        public const string ErrorNoLists = "No hay listas configuradas.";
        public const string ErrorAllFailed = "No se pudieron cargar las listas. Comprueba la conexión a internet.";
        public const string ErrorNoChannels = "Las listas no contienen canales reproducibles.";

        private sealed class Info
        {
            public CatalogState Value;
            public string Error = "";
            public CatalogSnapshot Content = CatalogSnapshot.Empty;
        }

        private sealed class ListResult
        {
            public string Id;
            public List<LinkEntry> Entries;          // the playable ones, without repeats; null if it failed
            public bool Threw;                       // the load threw (network, format...), unlike a list that arrived empty
        }

        private readonly AppConfig _config;
        private readonly IListLoader _loader;
        private readonly string _cachePath;
        private readonly object _gate = new object();
        private readonly object _cacheGate = new object();
        private volatile Info _state = new Info { Value = CatalogState.Loading };
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loadTask = Task.FromResult(true);
        private bool _started, _running;
        private volatile bool _disposed;
        private string _lastCacheText;
        private readonly Dictionary<string, List<LinkEntry>> _memory = new Dictionary<string, List<LinkEntry>>(StringComparer.Ordinal);   // last good load of each list (only the load in progress touches it)

        /// <summary>Deadline to load each list (the TV network may be slow, but a hung list must not leave the catalog loading forever).</summary>
        public TimeSpan ListTimeout = TimeSpan.FromSeconds(45);

        /// <param name="dataDir">App data directory (the copy goes in data/catalog.json). null = no copy on disk.</param>
        public CatalogService(AppConfig config, IListLoader loader, string dataDir = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (loader == null) throw new ArgumentNullException("loader");
            _config = config;
            _loader = loader;
            _cachePath = string.IsNullOrEmpty(dataDir) ? null : System.IO.Path.Combine(dataDir, CacheFileName);
        }

        public event Action Changed;

        public CatalogState State { get { return _state.Value; } }
        public string Error { get { return _state.Error; } }
        public IReadOnlyList<Category> Categories { get { return _state.Content.Categories; } }
        public IReadOnlyList<Channel> Channels { get { return _state.Content.Channels; } }
        public Channel Find(string id) { return _state.Content.Find(id); }

        /// <summary>The load in progress (or the last one, already finished). To wait for it to end in tests or before exiting.</summary>
        public Task LoadTask { get { lock (_gate) return _loadTask; } }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _running) return;
                if (_started && _state.Value != CatalogState.Failed) return;     // already loaded or loading; after a failure, Start retries
                BeginLocked();
            }
        }

        /// <summary>Downloads the lists again (for example when coming back from the background). Does nothing if a load is already in progress.</summary>
        public void Refresh()
        {
            lock (_gate)
            {
                if (_disposed || _running) return;
                BeginLocked();
            }
        }

        private void BeginLocked()
        {
            bool retry = _started && _state.Value == CatalogState.Failed;
            _started = true;
            _running = true;
            var ct = _cts.Token;
            _loadTask = Task.Run(() => RunAsync(ct, retry));
        }

        public void Dispose()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                cts = _cts;
            }
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ---------- Load cycle ----------

        private async Task RunAsync(CancellationToken ct, bool retry)
        {
            try
            {
                if (retry) Publish(CatalogSnapshot.Empty, CatalogState.Loading, "");
                var sources = Sources();
                if (sources.Count == 0) { Publish(CatalogSnapshot.Empty, CatalogState.Failed, ErrorNoLists); return; }
                var ids = new List<string>(sources.Count);
                foreach (var s in sources) ids.Add(ListId(s));

                // 1) the saved copy: shown right away (if it is for the current lists)
                var cached = ReadCache(ids);
                foreach (var id in ids)                                          // what was already loaded in this session is worth more than the disk (and works without a disk)
                {
                    List<LinkEntry> mem;
                    if (_memory.TryGetValue(id, out mem)) cached[id] = mem;
                }
                if (cached.Count > 0)
                {
                    var fromCopy = Merge(ids, null, cached);
                    if (fromCopy.Channels.Count > 0) Publish(fromCopy, CatalogState.Ready, "");
                }
                if (ct.IsCancellationRequested) return;

                // 2) download all the lists at once; each with its own deadline and without one bringing down the others
                var tasks = new Task<ListResult>[sources.Count];
                for (int i = 0; i < sources.Count; i++) tasks[i] = FetchAsync(sources[i], ids[i], ct);
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                // 3) merge: the freshly downloaded list or, if it failed, its last copy
                var fresh = new Dictionary<string, List<LinkEntry>>(StringComparer.Ordinal);
                int failed = 0, stale = 0, anyException = 0;
                foreach (var r in results)
                {
                    if (r.Entries != null) fresh[r.Id] = r.Entries;
                    else
                    {
                        failed++;
                        if (r.Threw) anyException++;
                        if (cached.ContainsKey(r.Id)) stale++;
                    }
                }
                foreach (var kv in fresh) _memory[kv.Key] = kv.Value;
                var content = Merge(ids, fresh, cached);
                CatalogState state;
                string error = "";
                if (content.Channels.Count == 0)
                {
                    state = CatalogState.Failed;
                    error = anyException > 0 ? ErrorAllFailed : ErrorNoChannels;
                }
                else
                {
                    state = CatalogState.Ready;
                    if (failed > 0)
                        error = stale > 0
                            ? "No se pudieron actualizar " + failed + " de " + sources.Count + " listas: se muestra la última copia guardada."
                            : "No se pudieron cargar " + failed + " de " + sources.Count + " listas.";
                }
                Publish(content, state, error);
                if (fresh.Count > 0) WriteCache(ids, fresh, cached);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AceLog.Error(Tag, "unexpected failure while loading the lists: " + ex);
                try { if (_state.Content.Channels.Count == 0) Publish(CatalogSnapshot.Empty, CatalogState.Failed, ErrorAllFailed); }
                catch (Exception) { }
            }
            finally
            {
                lock (_gate) _running = false;
            }
        }

        private List<ListSource> Sources()
        {
            var res = new List<ListSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in _config.Lists.ToArray())
            {
                if (s == null) continue;
                string norm;
                InputClassifier.Classify(s.Code, out norm);
                if (!seen.Add(norm)) continue;
                res.Add(s);
                if (res.Count >= AppConfig.MaxLists) break;
            }
            return res;
        }

        private async Task<ListResult> FetchAsync(ListSource source, string id, CancellationToken ct)
        {
            var res = new ListResult { Id = id };
            try
            {
                var entries = await AsyncUtil.WithTimeout(t => _loader.LoadAsync(source, t), ListTimeout, ct).ConfigureAwait(false);
                var playable = Playable(entries);
                if (playable.Count == 0) AceLog.Warn(Tag, "list '" + source.Name + "': no playable links");
                else
                {
                    res.Entries = playable;
                    AceLog.Info(Tag, "list '" + source.Name + "': " + playable.Count + " playable links");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                res.Threw = true;
                AceLog.Warn(Tag, "list '" + source.Name + "' could not be loaded: " + ex.GetType().Name + ": " + ex.Message);
            }
            return res;
        }

        /// <summary>The playable links of a list, without repeats (the first of each content_id) and capped.</summary>
        private static List<LinkEntry> Playable(IEnumerable<LinkEntry> entries)
        {
            var res = new List<LinkEntry>();
            if (entries == null) return res;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (e == null || !e.IsProbeable || string.IsNullOrEmpty(e.Key) || !seen.Add(e.Key)) continue;
                res.Add(e);
                if (res.Count >= MaxEntriesPerList) break;
            }
            return res;
        }

        /// <summary>Joins the lists in the configuration order (not in arrival order) so that the categories always come out in the same order.</summary>
        private static CatalogSnapshot Merge(List<string> ids, Dictionary<string, List<LinkEntry>> fresh, Dictionary<string, List<LinkEntry>> cached)
        {
            var all = new List<LinkEntry>();
            foreach (var id in ids)
            {
                List<LinkEntry> l;
                if (fresh != null && fresh.TryGetValue(id, out l)) all.AddRange(l);
                else if (cached != null && cached.TryGetValue(id, out l)) all.AddRange(l);
            }
            return CatalogBuilder.Build(all);
        }

        // ---------- Publication ----------

        private void Publish(CatalogSnapshot content, CatalogState state, string error)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var old = _state;
                if (old.Value == state && string.Equals(old.Error, error, StringComparison.Ordinal) && SameContent(old.Content, content)) return;
                _state = new Info { Value = state, Error = error ?? "", Content = content };
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            foreach (Action a in h.GetInvocationList())
            {
                try { a(); }
                catch (Exception ex) { AceLog.Warn(Tag, "a Changed subscriber threw: " + ex.Message); }
            }
        }

        private static bool SameContent(CatalogSnapshot a, CatalogSnapshot b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Channels.Count != b.Channels.Count || a.Categories.Count != b.Categories.Count) return false;
            for (int i = 0; i < a.Channels.Count; i++)
            {
                var x = a.Channels[i];
                var y = b.Channels[i];
                if (x.Id != y.Id || x.Name != y.Name || x.Quality != y.Quality || x.CategoryKey != y.CategoryKey || x.CategoryName != y.CategoryName) return false;
            }
            return true;
        }

        // ---------- Copy on disk ----------

        /// <summary>Stable identifier of a list for the copy on disk: part of the SHA-1 of the code (the code itself is not saved).</summary>
        internal static string ListId(ListSource source)
        {
            string norm;
            InputClassifier.Classify(source.Code, out norm);
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private Dictionary<string, List<LinkEntry>> ReadCache(List<string> wanted)
        {
            var res = new Dictionary<string, List<LinkEntry>>(StringComparer.Ordinal);
            string text = TvFiles.ReadBounded(_cachePath, MaxCacheBytes);
            if (text == null) return res;
            lock (_cacheGate) _lastCacheText = text;
            try
            {
                object v;
                if (!MiniJson.TryParse(text, out v)) return res;
                var root = MiniJson.AsObject(v);
                var lists = MiniJson.GetArray(root, "lists");
                if (lists == null) return res;
                int total = 0;
                foreach (var item in lists)
                {
                    var o = MiniJson.AsObject(item);
                    string id = MiniJson.GetString(o, "id");
                    var arr = MiniJson.GetArray(o, "entries");
                    if (id == null || arr == null || !wanted.Contains(id) || res.ContainsKey(id)) continue;
                    var entries = new List<LinkEntry>();
                    foreach (var row in arr)
                    {
                        var cols = MiniJson.AsArray(row);
                        if (cols == null || cols.Count < 2) continue;
                        string name = cols[0] as string, url = cols[1] as string, group = cols.Count > 2 ? cols[2] as string : null;
                        if (name == null || url == null || url.Length > MyLinksPasteClient.MaxUrlLength) continue;
                        var e = LinkEntry.FromUrl(TvFiles.Clip(name, MyLinksPasteClient.MaxNameLength), url, group == null ? null : TvFiles.Clip(group, 200));
                        if (!e.IsProbeable) continue;
                        entries.Add(e);
                        if (entries.Count >= MaxEntriesPerList || ++total >= MaxCachedEntries) break;
                    }
                    if (entries.Count > 0) res[id] = Playable(entries);
                    if (total >= MaxCachedEntries) break;
                }
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "the saved copy could not be interpreted: " + ex.Message);
                res.Clear();
            }
            return res;
        }

        private void WriteCache(List<string> ids, Dictionary<string, List<LinkEntry>> fresh, Dictionary<string, List<LinkEntry>> cached)
        {
            if (_cachePath == null) return;
            var lists = new List<object>();
            int total = 0;
            foreach (var id in ids)
            {
                List<LinkEntry> l;
                if (!fresh.TryGetValue(id, out l) && !cached.TryGetValue(id, out l)) continue;
                var rows = new List<object>(l.Count);
                foreach (var e in l)
                {
                    if (total >= MaxCachedEntries) break;
                    rows.Add(new object[] { e.Name ?? "", e.Url ?? "", e.Group ?? "" });
                    total++;
                }
                lists.Add(new Dictionary<string, object> { { "id", id }, { "entries", rows } });
            }
            string text = MiniJson.Stringify(new Dictionary<string, object> { { "v", 1L }, { "lists", lists } });
            if (Encoding.UTF8.GetByteCount(text) > MaxCacheBytes) { AceLog.Warn(Tag, "the catalog copy is too large; not saved"); return; }
            lock (_cacheGate)
            {
                if (_disposed || string.Equals(text, _lastCacheText, StringComparison.Ordinal)) return;
                if (TvFiles.WriteAtomic(_cachePath, text)) _lastCacheText = text;
            }
        }
    }
}
