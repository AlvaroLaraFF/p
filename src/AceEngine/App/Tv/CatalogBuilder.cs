using System;
using System.Collections.Generic;
using AceEngine.Sources;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Result of assembling the catalog: channels and categories already grouped. It is a snapshot that nobody modifies after creating it (it can be read from
    /// any thread); to change the catalog another one is built.
    /// </summary>
    public sealed class CatalogSnapshot
    {
        public static readonly CatalogSnapshot Empty = new CatalogSnapshot(new Channel[0], new Category[0]);

        private readonly Dictionary<string, Channel> _byId;

        public IReadOnlyList<Channel> Channels { get; private set; }
        public IReadOnlyList<Category> Categories { get; private set; }

        public CatalogSnapshot(IReadOnlyList<Channel> channels, IReadOnlyList<Category> categories)
        {
            Channels = channels;
            Categories = categories;
            _byId = new Dictionary<string, Channel>(channels.Count, StringComparer.Ordinal);
            foreach (var c in channels) _byId[c.Id] = c;
        }

        public Channel Find(string id)
        {
            Channel c;
            return id != null && _byId.TryGetValue(id, out c) ? c : null;
        }
    }

    /// <summary>
    /// Assembles the single catalog from the links of all the lists (pure: no network or disk). The rules: only playable links, one channel per content_id with
    /// its best name, clean name, equivalent categories merged, and each channel in the
    /// category of its first appearance.
    /// </summary>
    public static class CatalogBuilder
    {
        /// <summary>Links examined at most (the lists are untrusted input).</summary>
        public const int MaxEntries = 200000;
        /// <summary>Distinct channels that enter the catalog at most.</summary>
        public const int MaxChannels = 20000;
        private const int MaxKeyLength = 4096;

        /// <summary>A possible name of a channel, already clean, with its quality and the link it comes from.</summary>
        internal sealed class Candidate
        {
            public string Name, Source, Quality;
            public bool Curated;
            public LinkEntry Entry;
        }

        private sealed class Slot
        {
            public string Id, CategoryKey, CategoryName, RawGroup;
            public Candidate Best;
        }

        public static CatalogSnapshot Build(IEnumerable<LinkEntry> entries)
        {
            if (entries == null) return CatalogSnapshot.Empty;
            var byId = new Dictionary<string, Slot>(StringComparer.Ordinal);
            var order = new List<Slot>();
            var groups = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);     // original group -> resolved (key, name)
            var names = new Dictionary<string, string>(StringComparer.Ordinal);                             // category key -> name of the first appearance
            int seen = 0;
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (++seen > MaxEntries) break;
                if (!e.IsProbeable || string.IsNullOrEmpty(e.Key) || e.Key.Length > MaxKeyLength) continue;

                var cand = MakeCandidate(e);

                Slot slot;
                if (byId.TryGetValue(e.Key, out slot))
                {
                    if (IsBetter(cand, slot.Best)) slot.Best = cand;
                    continue;
                }
                if (order.Count >= MaxChannels) continue;

                string rawGroup = TvFiles.Clip(e.Group, 200);                // the start is enough: Resolve looks no further and a huge group is not stored
                KeyValuePair<string, string> g;
                if (!groups.TryGetValue(rawGroup, out g))
                {
                    string k, n;
                    CategoryNames.Resolve(rawGroup, out k, out n);
                    g = new KeyValuePair<string, string>(k, n);
                    groups[rawGroup] = g;
                }
                string catName;
                if (!names.TryGetValue(g.Key, out catName)) { catName = g.Value; names[g.Key] = catName; }
                slot = new Slot { Id = e.Key, CategoryKey = g.Key, CategoryName = catName, RawGroup = rawGroup, Best = cand };
                byId[e.Key] = slot;
                order.Add(slot);
            }

            var channels = new List<Channel>(order.Count);
            foreach (var s in order)
            {
                channels.Add(new Channel
                {
                    Id = s.Id,
                    Name = s.Best.Name,
                    SourceName = s.Best.Source,
                    Quality = s.Best.Quality,
                    CategoryKey = s.CategoryKey,
                    CategoryName = s.CategoryName,
                    Entry = s.Best.Entry,
                    Language = ChannelLanguages.Detect(s.Best.Name, s.Best.Source, s.RawGroup, null, null)
                });
            }
            return FromChannels(channels);
        }

        internal static Candidate MakeCandidate(LinkEntry e)
        {
            string clean = ChannelNames.Clean(e.Name);
            if (clean.Length == 0) clean = ChannelNames.Fallback(e);
            string source = TextNorm.CleanSpaces(e.Name, ChannelNames.MaxSourceName);
            bool up;
            return new Candidate
            {
                Name = clean,
                Source = source,
                Quality = QualityRules.Detect(clean),
                Curated = !TextNorm.IsUniformCase(source, out up),
                Entry = e
            };
        }

        /// <summary>
        /// Groups already assembled channels (for example those from the copy on disk) into categories, in order of first appearance; empty categories do not exist
        /// because they are only created when a channel is found. Removes the ones repeated by Id.
        /// </summary>
        public static CatalogSnapshot FromChannels(IEnumerable<Channel> channels)
        {
            var list = new List<Channel>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cats = new List<Category>();
            var byKey = new Dictionary<string, Category>(StringComparer.Ordinal);
            foreach (var c in channels)
            {
                if (c == null || string.IsNullOrEmpty(c.Id) || !ids.Add(c.Id)) continue;
                string key = string.IsNullOrEmpty(c.CategoryKey) ? CategoryNames.OthersKey : c.CategoryKey;
                Category cat;
                if (!byKey.TryGetValue(key, out cat))
                {
                    cat = new Category { Key = key, Name = string.IsNullOrEmpty(c.CategoryName) ? CategoryNames.OthersName : c.CategoryName };
                    byKey[key] = cat;
                    cats.Add(cat);
                }
                cat.Channels.Add(c);
                list.Add(c);
            }
            return new CatalogSnapshot(list.AsReadOnly(), cats.AsReadOnly());
        }

        /// <summary>
        /// Criterion for the best name of a channel that appears under different names. A total order that does not depend on the order of the lists:
        /// 1) the one with a quality label; 2) the one that is neither all uppercase nor all lowercase (name curated by the author);
        /// 3) the longest (more information); 4) the smallest in alphabetical order, and lastly the original text and the URL.
        /// </summary>
        internal static bool IsBetter(Candidate a, Candidate b)
        {
            bool aq = a.Quality.Length > 0, bq = b.Quality.Length > 0;
            if (aq != bq) return aq;
            if (a.Curated != b.Curated) return a.Curated;
            if (a.Name.Length != b.Name.Length) return a.Name.Length > b.Name.Length;
            int c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0) return c < 0;
            c = string.CompareOrdinal(a.Source, b.Source);
            if (c != 0) return c < 0;
            return string.CompareOrdinal(a.Entry.Url ?? "", b.Entry.Url ?? "") < 0;
        }
    }
}
