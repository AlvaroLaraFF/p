using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AceEngine.App.Tv;
using AceEngine.Sources;
using Xunit;

namespace AceEngine.Tests.Tv
{
    public class RecentChannelsStoreTests : IDisposable
    {
        private readonly string _dir = TvTempDir.Create();
        public void Dispose() { TvTempDir.Delete(_dir); }
        private string FilePath { get { return Path.Combine(_dir, "recents.json"); } }

        private static Channel Ch(int n, string name = null)
        {
            var e = LinkEntry.FromUrl(name ?? ("Canal " + n), "acestream://" + CatalogBuilderTests.Cid(n), "Cat");
            return new Channel { Id = e.Key, Name = name ?? ("Canal " + n), SourceName = (name ?? ("Canal " + n)) + " *", Quality = "1080p", CategoryKey = "cat", CategoryName = "Cat", Entry = e };
        }

        [Fact]
        public void KeepsTheTwentyMostRecentFirstWithoutRepeatsSoTheScreenCanFillGaps()
        {
            var store = new RecentChannelsStore(null, _dir);
            Assert.Empty(store.Items);
            Assert.Equal(10, RecentChannelsStore.Max); Assert.Equal(20, RecentChannelsStore.MaxStored);
            for (int i = 1; i <= 24; i++) store.Played(Ch(i));
            Assert.Equal(20, store.Items.Count);
            Assert.Equal(Enumerable.Range(5, 20).Reverse().Select(i => "Canal " + i).ToArray(), store.Items.Select(c => c.Name).ToArray());

            store.Played(Ch(10));                                        // repeated: it goes to the top, without duplicating
            Assert.Equal(20, store.Items.Count);
            Assert.Equal("Canal 10", store.Items[0].Name);
            Assert.Equal("Canal 24", store.Items[1].Name);
            Assert.Equal(1, store.Items.Count(c => c.Name == "Canal 10"));
            Assert.Equal("Canal 5", store.Items[19].Name);

            store.Played(Ch(10));                                        // the same again: the order does not change
            Assert.Equal("Canal 10", store.Items[0].Name);
            Assert.Equal(20, store.Items.Count);
        }

        [Fact]
        public void PersistsAndReloadsWhatItNeedsToRebuildTheChannel()
        {
            var a = new RecentChannelsStore(null, _dir);
            a.Played(Ch(1)); a.Played(Ch(2, "M+ Liga de Campeones 1080p"));
            var b = new RecentChannelsStore(null, _dir);
            Assert.Equal(new[] { "M+ Liga de Campeones 1080p", "Canal 1" }, b.Items.Select(c => c.Name).ToArray());
            var c0 = b.Items[0];
            Assert.Equal("cid:" + CatalogBuilderTests.Cid(2), c0.Id);
            Assert.Equal("1080p", c0.Quality);
            Assert.Equal("cat", c0.CategoryKey);
            Assert.Equal("Cat", c0.CategoryName);
            Assert.Equal("acestream://" + CatalogBuilderTests.Cid(2), c0.Entry.Url);
            Assert.True(c0.Entry.IsProbeable);
            Assert.Equal(c0.Id, c0.Entry.Key);
            Assert.Equal("M+ Liga de Campeones 1080p *", c0.SourceName);
            string json = File.ReadAllText(FilePath);
            Assert.True(json.Length < 2000);
        }

        [Fact]
        public void PrefersTheCatalogInstanceWhenItExists()
        {
            var inCatalog = Ch(1, "Canal 1 del catalogo");
            var catalog = new TvFakeCatalog(CatalogBuilder.FromChannels(new[] { inCatalog }));
            var played = Ch(1, "Canal 1");                              // another instance of the same channel
            var other = Ch(2);                                          // it is not in the catalog
            var a = new RecentChannelsStore(catalog, _dir);
            a.Played(other); a.Played(played);
            Assert.Same(inCatalog, a.Items[0]);
            Assert.Same(other, a.Items[1]);                             // without a catalog, the one that was passed

            // after restarting, with the catalog already loaded: the catalog's one; the one that is no longer there, rebuilt
            var b = new RecentChannelsStore(catalog, _dir);
            Assert.Same(inCatalog, b.Items[0]);
            Assert.Equal("Canal 2", b.Items[1].Name);
            Assert.NotSame(other, b.Items[1]);
            Assert.NotNull(b.Items[1].Entry);
        }

        [Fact]
        public void WhenTheCatalogLoadsLaterTheItemsSwitchToItsInstances()
        {
            var empty = new TvFakeCatalog(CatalogSnapshot.Empty);
            var first = new RecentChannelsStore(empty, _dir);
            first.Played(Ch(1));
            first.Dispose();
            var store = new RecentChannelsStore(empty, _dir);
            int changed = 0;
            store.Changed += () => Interlocked.Increment(ref changed);
            var stored = store.Items[0];
            var real = Ch(1, "Canal 1 real");
            empty.Set(CatalogBuilder.FromChannels(new[] { real }));
            Assert.Same(real, store.Items[0]);
            Assert.NotSame(stored, store.Items[0]);
            Assert.Equal(1, changed);
            empty.Set(CatalogBuilder.FromChannels(new[] { real }));       // another Changed with no changes: it does not notify
            Assert.Equal(1, changed);
            store.Dispose();
            Assert.Equal(0, empty.ChangedSubscribers);
        }

        [Fact]
        public void ChangedFiresOnPlayedAndSurvivesBadSubscribers()
        {
            var store = new RecentChannelsStore(null, _dir);
            int n = 0;
            store.Changed += () => { throw new InvalidOperationException("mal"); };
            store.Changed += () => n++;
            store.Played(Ch(1)); store.Played(Ch(2));
            Assert.Equal(2, n);
            store.Played(null);
            store.Played(new Channel());                                  // without an identifier
            Assert.Equal(2, n);
            Assert.Equal(2, store.Items.Count);
        }

        [Fact]
        public void ItemsAreAnImmutableSnapshot()
        {
            var store = new RecentChannelsStore(null, _dir);
            store.Played(Ch(1));
            var before = store.Items;
            store.Played(Ch(2));
            Assert.Single(before);
            Assert.Equal(2, store.Items.Count);
            Assert.Throws<NotSupportedException>(() => ((IList<Channel>)before).Add(Ch(3)));
        }

        [Fact]
        public void WorksWithoutADataDirectory()
        {
            var store = new RecentChannelsStore(null);
            store.Played(Ch(1));
            Assert.Single(store.Items);
        }

        [Fact]
        public void ConcurrentPlaysKeepTheListConsistent()
        {
            var store = new RecentChannelsStore(null, _dir);
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() => { for (int i = 0; i < 50; i++) store.Played(Ch(1 + (t * 7 + i) % 25)); })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            var items = store.Items;
            Assert.Equal(20, items.Count);
            Assert.Equal(20, items.Select(c => c.Id).Distinct().Count());
            Assert.Equal(20, new RecentChannelsStore(null, _dir).Items.Count);
        }

        // ---------- Damaged files ----------

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("garbage \u0000\u0001")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"items\":5}")]
        [InlineData("{\"items\":[1,\"a\",null,[],{}]}")]
        [InlineData("{\"items\":[{\"id\":\"x\"}]}")]
        [InlineData("{\"items\":[{\"id\":\"cid:abc\",\"n\":\"Canal\",\"u\":\"acestream://zz\"}]}")]
        public void ACorruptFileIsIgnored(string content)
        {
            File.WriteAllText(FilePath, content);
            var store = new RecentChannelsStore(null, _dir);
            Assert.Empty(store.Items);
            store.Played(Ch(1));                                          // and it can keep being used (and rewrites the file)
            Assert.Single(new RecentChannelsStore(null, _dir).Items);
        }

        [Fact]
        public void AnOversizedFileIsNotRead()
        {
            var sb = new StringBuilder("{\"items\":[");
            sb.Append("{\"id\":\"cid:" + CatalogBuilderTests.Cid(1) + "\",\"n\":\"Canal\",\"u\":\"acestream://" + CatalogBuilderTests.Cid(1) + "\"}");
            sb.Append(new string(' ', RecentChannelsStore.MaxFileBytes)).Append("]}");
            File.WriteAllText(FilePath, sb.ToString());
            Assert.Empty(new RecentChannelsStore(null, _dir).Items);
        }

        [Fact]
        public void OnlyTwentyValidDistinctItemsAreLoadedAndStringsAreBounded()
        {
            var items = new List<string>();
            for (int i = 1; i <= 30; i++)
                items.Add("{\"id\":\"cid:" + CatalogBuilderTests.Cid(i) + "\",\"n\":\"" + new string('N', 1500) + i + "\",\"s\":\"" + new string('S', 1500) + "\",\"q\":\"" + new string('Q', 500) +
                    "\",\"u\":\"acestream://" + CatalogBuilderTests.Cid(i) + "\",\"ck\":\"" + new string('K', 1500) + "\",\"cn\":\"" + new string('C', 1500) + "\"}");
            items.Insert(1, items[0]);                                    // a repeated one
            File.WriteAllText(FilePath, "{\"items\":[" + string.Join(",", items) + "]}");
            Assert.InRange(new FileInfo(FilePath).Length, 100 * 1024, RecentChannelsStore.MaxFileBytes - 1);
            var store = new RecentChannelsStore(null, _dir);
            Assert.Equal(20, store.Items.Count);
            Assert.Equal(20, store.Items.Select(c => c.Id).Distinct().Count());
            Assert.All(store.Items, c => Assert.True(c.Name.Length <= 120 && c.SourceName.Length <= 200 && c.Quality.Length <= 8 && c.CategoryKey.Length <= 100 && c.CategoryName.Length <= 60));
        }

        [Fact]
        public void AnItemWhoseLinkDoesNotMatchItsIdIsDropped()
        {
            string good = "{\"id\":\"cid:" + CatalogBuilderTests.Cid(1) + "\",\"n\":\"Bueno\",\"u\":\"acestream://" + CatalogBuilderTests.Cid(1) + "\"}";
            string mismatch = "{\"id\":\"cid:" + CatalogBuilderTests.Cid(2) + "\",\"n\":\"Otro\",\"u\":\"acestream://" + CatalogBuilderTests.Cid(3) + "\"}";
            string web = "{\"id\":\"http://a.example/x.html\",\"n\":\"Web\",\"u\":\"http://a.example/x.html\"}";
            string path = "{\"id\":\"cid:" + CatalogBuilderTests.Cid(4) + "\",\"n\":\"Ruta\",\"u\":\"acestream:?data=/etc/passwd\"}";
            File.WriteAllText(FilePath, "{\"items\":[" + mismatch + "," + web + "," + path + "," + good + "]}");
            var store = new RecentChannelsStore(null, _dir);
            Assert.Equal(new[] { "Bueno" }, store.Items.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void UnwritableDirectoryDoesNotBreakPlayed()
        {
            string blocker = Path.Combine(_dir, "file");
            File.WriteAllText(blocker, "x");
            var store = new RecentChannelsStore(null, Path.Combine(blocker, "sub"));          // a file where there should be a directory
            store.Played(Ch(1));
            Assert.Single(store.Items);
        }
    }
}
