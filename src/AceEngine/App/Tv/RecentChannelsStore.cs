using System;
using System.Collections.Generic;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// The last channels played (up to MaxStored = 20 are stored and the UI shows at most Max = 10 with peers; the most recent first, no repeats), stored in data/recents.json. For each one the minimum is stored to
    /// rebuild it (identifier, name, quality, link and category), so that it is still there even if the catalog has not loaded yet; when the catalog has
    /// the channel its instance is returned (the same one the rest of the UI uses). A damaged file is ignored.
    /// </summary>
    public sealed class RecentChannelsStore : IRecentChannels, IDisposable
    {
        /// <summary>Recents that the UI shows at most at once (those that have peers at that moment).</summary>
        public const int Max = 10;
        /// <summary>Recents that are stored: more than the visible ones, to be able to fill the gaps left by those without peers.</summary>
        public const int MaxStored = 20;
        public const int MaxFileBytes = 256 * 1024;
        public const string FileName = "recents.json";
        private const string Tag = "recents";

        private readonly IChannelCatalog _catalog;
        private readonly string _path;
        private readonly object _gate = new object();
        private List<Channel> _stored = new List<Channel>();             // the stored ones, the most recent first (only changed under _gate)
        private volatile IReadOnlyList<Channel> _items = new Channel[0];  // immutable snapshot

        /// <param name="catalog">To prefer its instance of each channel. May be null.</param>
        /// <param name="dataDir">Data directory (the file is data/recents.json). null = no persistence.</param>
        public RecentChannelsStore(IChannelCatalog catalog, string dataDir = null)
        {
            _catalog = catalog;
            _path = string.IsNullOrEmpty(dataDir) ? null : System.IO.Path.Combine(dataDir, FileName);
            _stored = Load();
            _items = Resolve(_stored);
            if (_catalog != null) _catalog.Changed += OnCatalogChanged;
        }

        public event Action Changed;

        public IReadOnlyList<Channel> Items { get { return _items; } }

        public void Played(Channel channel)
        {
            if (channel == null || string.IsNullOrEmpty(channel.Id)) return;
            var rec = channel;
            lock (_gate)
            {
                var list = new List<Channel>(MaxStored) { rec };
                foreach (var c in _stored)
                {
                    if (c.Id == rec.Id) continue;
                    list.Add(c);
                    if (list.Count >= MaxStored) break;
                }
                _stored = list;
                _items = Resolve(list);
                Save(list);
            }
            RaiseChanged();
        }

        public void Dispose()
        {
            if (_catalog != null) _catalog.Changed -= OnCatalogChanged;
        }

        private void OnCatalogChanged()
        {
            IReadOnlyList<Channel> before;
            lock (_gate)
            {
                before = _items;
                _items = Resolve(_stored);
            }
            if (!SameInstances(before, _items)) RaiseChanged();
        }

        private static bool SameInstances(IReadOnlyList<Channel> a, IReadOnlyList<Channel> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
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

        /// <summary>The catalog's instance if it exists; otherwise, the stored one.</summary>
        private IReadOnlyList<Channel> Resolve(List<Channel> stored)
        {
            var res = new List<Channel>(stored.Count);
            foreach (var s in stored)
            {
                Channel live = null;
                if (_catalog != null)
                {
                    try { live = _catalog.Find(s.Id); }
                    catch (Exception ex) { AceLog.Warn(Tag, "the catalog threw in Find: " + ex.Message); }
                }
                res.Add(live ?? s);
            }
            return res.AsReadOnly();
        }

        // ---------- Persistence ----------

        private void Save(List<Channel> list)
        {
            if (_path == null) return;
            var arr = new List<object>(list.Count);
            foreach (var c in list)
            {
                if (c.Entry == null || string.IsNullOrEmpty(c.Entry.Url)) continue;           // without a link it cannot be rebuilt
                arr.Add(new Dictionary<string, object>
                {
                    { "id", TvFiles.Clip(c.Id, 2100) }, { "n", TvFiles.Clip(c.Name, ChannelNames.MaxName) }, { "s", TvFiles.Clip(c.SourceName ?? c.Name, ChannelNames.MaxSourceName) },
                    { "q", TvFiles.Clip(c.Quality, 8) }, { "u", TvFiles.Clip(c.Entry.Url, MyLinksPasteClient.MaxUrlLength) },
                    { "ck", TvFiles.Clip(c.CategoryKey, 100) }, { "cn", TvFiles.Clip(c.CategoryName, CategoryNames.MaxName) }
                });
            }
            TvFiles.WriteAtomic(_path, MiniJson.Stringify(new Dictionary<string, object> { { "v", 1L }, { "items", arr } }));
        }

        private List<Channel> Load()
        {
            var res = new List<Channel>();
            string text = TvFiles.ReadBounded(_path, MaxFileBytes);
            if (text == null) return res;
            try
            {
                object v;
                if (!MiniJson.TryParse(text, out v)) return res;
                var items = MiniJson.GetArray(MiniJson.AsObject(v), "items");
                if (items == null) return res;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    if (res.Count >= MaxStored) break;
                    var o = MiniJson.AsObject(item);
                    if (o == null) continue;
                    string id = MiniJson.GetString(o, "id"), name = MiniJson.GetString(o, "n"), url = MiniJson.GetString(o, "u");
                    if (string.IsNullOrEmpty(id) || id.Length > 2100 || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || url.Length > MyLinksPasteClient.MaxUrlLength) continue;
                    name = TvFiles.Clip(name, ChannelNames.MaxName);
                    string cn = TvFiles.Clip(MiniJson.GetString(o, "cn") ?? "", CategoryNames.MaxName);
                    var entry = LinkEntry.FromUrl(name, url, cn);
                    if (!entry.IsProbeable || entry.Key != id || !ids.Add(id)) continue;      // a link that is no longer valid, or that is not the one for the identifier
                    res.Add(new Channel
                    {
                        Id = id,
                        Name = name,
                        SourceName = TvFiles.Clip(MiniJson.GetString(o, "s") ?? name, ChannelNames.MaxSourceName),
                        Quality = TvFiles.Clip(MiniJson.GetString(o, "q") ?? "", 8),
                        CategoryKey = TvFiles.Clip(MiniJson.GetString(o, "ck") ?? "", 100),
                        CategoryName = cn,
                        Entry = entry
                    });
                }
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "the stored recents could not be interpreted: " + ex.Message);
                res.Clear();
            }
            return res;
        }
    }
}
