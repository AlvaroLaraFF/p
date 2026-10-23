using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Sources;
using Xunit;

namespace AceEngine.Tests.Tv
{
    public class TvSearchServiceTests : IDisposable
    {
        private readonly string _dir = TvTempDir.Create();
        public void Dispose() { TvTempDir.Delete(_dir); }

        private static CatalogSnapshot Real() { return CatalogBuilder.Build(TvRealData.List2); }
        private static string Norm(string s) { return TextNorm.Normalize(s); }

        private static CatalogSnapshot Snap(params string[] names)
        {
            return CatalogBuilder.Build(names.Select((n, i) => CatalogBuilderTests.Ace(i + 1, n, "G")).ToList());
        }

        // ---------- Local search ----------

        [Fact]
        public void LigaFindsChampionsLeagueAndLaLigaWithTheBestMatchesFirst()
        {
            var snap = Real();
            var svc = new SearchService(new TvFakeCatalog(snap));
            var r = svc.Search("liga");
            // all those containing "liga", not one more nor one less
            var expected = snap.Channels.Where(c => Norm(c.Name).Contains("liga")).ToList();
            Assert.Equal(expected.Count, r.Count);
            Assert.True(r.Count > 40);
            Assert.True(new HashSet<Channel>(expected).SetEquals(r));
            Assert.Contains(r, c => c.Name == "M+ Liga de Campeones 1080p");
            Assert.Contains(r, c => c.Name == "Liga de Campeones FHD");
            Assert.Contains(r, c => c.Name == "M+ LaLiga FHD");
            Assert.Contains(r, c => c.Name == "DAZN La Liga 1 FHD");
            Assert.Contains(r, c => c.Name == "Sky Sports LaLiga HD");
            // first those that start with the text, then those with a word that starts with it, then those that contain it
            Func<Channel, int> tier = c => { var n = Norm(c.Name); return n.StartsWith("liga") ? 0 : (" " + n).Contains(" liga") ? 1 : 2; };
            var tiers = r.Select(tier).ToList();
            Assert.Equal(tiers.OrderBy(t => t).ToList(), tiers);
            Assert.Equal(0, tiers.First());
            Assert.Contains(2, tiers);
            // within a group, the catalog's order
            for (int g = 0; g < 3; g++)
            {
                var inGroup = r.Where(c => tier(c) == g).ToList();
                Assert.Equal(snap.Channels.Where(c => expected.Contains(c) && tier(c) == g).ToList(), inGroup);
            }
        }

        [Fact]
        public void NbaFindsTheNbaChannels()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()));
            var r = svc.Search("nba");
            Assert.Equal(new[] { "NBA ESPN 720p", "NBA League Pass 1080p", "NBA League Pass 1080p" }, r.Select(c => c.Name).ToArray());
            Assert.Equal(r.Select(c => c.Id).ToArray(), svc.Search("NBA").Select(c => c.Id).ToArray());
        }

        [Fact]
        public void AllWordsMustMatchInAnyOrder()
        {
            var snap = Real();
            var svc = new SearchService(new TvFakeCatalog(snap));
            var r = svc.Search("dazn f1");
            var expected = snap.Channels.Where(c => Norm(c.Name).Contains("dazn") && Norm(c.Name).Contains("f1")).ToList();
            Assert.True(expected.Count >= 10);
            Assert.Equal(expected.Count, r.Count);
            Assert.All(r, c => { Assert.Contains("dazn", Norm(c.Name)); Assert.Contains("f1", Norm(c.Name)); });
            Assert.DoesNotContain(r, c => c.Name == "DAZN La Liga 1 FHD");
            Assert.True(new HashSet<string>(svc.Search("f1 dazn").Select(c => c.Id)).SetEquals(r.Select(c => c.Id)));
            // "dazn f1" as the start of the name goes before "Eleven DAZN ..."
            Assert.StartsWith("DAZN F1", r[0].Name);
            Assert.Empty(svc.Search("dazn nba"));
        }

        [Fact]
        public void IgnoresCaseAccentsAndPunctuation()
        {
            var svc = new SearchService(new TvFakeCatalog(Snap("Fútbol Total", "M+ Liga de Campeones 1080p", "Canal+ Sport", "Ñoño TV")));
            Assert.Equal(new[] { "Fútbol Total" }, svc.Search("futbol").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Fútbol Total" }, svc.Search("FÚTBOL").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Fútbol Total" }, svc.Search("fut-bol".Replace("-", "")).Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "M+ Liga de Campeones 1080p" }, svc.Search("  M+   LIGA ").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "M+ Liga de Campeones 1080p" }, svc.Search("m+").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Canal+ Sport" }, svc.Search("canal+").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Canal+ Sport" }, svc.Search("CANAL SPORT").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Ñoño TV" }, svc.Search("nono").Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "M+ Liga de Campeones 1080p" }, svc.Search("de").Select(c => c.Name).ToArray());
        }

        [Fact]
        public void EmptyOrMeaninglessTextGivesNothing()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()));
            Assert.Empty(svc.Search(null));
            Assert.Empty(svc.Search(""));
            Assert.Empty(svc.Search("   \t "));
            Assert.Empty(svc.Search("***"));
            Assert.Empty(svc.Search("zzzzzzqqqq"));
            Assert.Empty(svc.Search(new string('x', 5000)));
        }

        [Fact]
        public void TheThreeGroupsKeepCatalogOrderInsideEachOne()
        {
            var svc = new SearchService(new TvFakeCatalog(Snap("Zeta Liga", "Aligator", "Liga Alfa", "Xliga", "La Liga Beta", "Liga Omega")));
            Assert.Equal(new[] { "Liga Alfa", "Liga Omega", "Zeta Liga", "La Liga Beta", "Aligator", "Xliga" }, svc.Search("liga").Select(c => c.Name).ToArray());
        }

        [Fact]
        public void ManyMatchesAreCappedAndTheBestOnesComeFirst()
        {
            var names = new List<string>();
            for (int i = 0; i < 3000; i++) names.Add("Canal " + i);
            for (int i = 0; i < 700; i++) names.Add("Acanal " + i);
            var svc = new SearchService(new TvFakeCatalog(Snap(names.ToArray())));
            var r = svc.Search("canal");
            Assert.Equal(SearchService.MaxLocalResults, r.Count);
            Assert.All(r, c => Assert.StartsWith("Canal ", c.Name));
            Assert.Equal("Canal 0", r[0].Name);
        }

        [Fact]
        public void TheIndexFollowsTheCatalog()
        {
            var cat = new TvFakeCatalog(Snap("Uno", "Dos"));
            var svc = new SearchService(cat);
            Assert.Single(svc.Search("uno"));
            Assert.Empty(svc.Search("tres"));
            cat.Set(Snap("Tres", "Cuatro", "Uno Plus"));
            Assert.Empty(svc.Search("dos"));
            Assert.Single(svc.Search("tres"));
            Assert.Equal(new[] { "Uno Plus" }, svc.Search("uno").Select(c => c.Name).ToArray());
            svc.Dispose();
            Assert.Equal(0, cat.ChangedSubscribers);
        }

        [Fact]
        public void ResultsAreStableSnapshotsWhileTheCatalogSwapsUnderneath()
        {
            var cat = new TvFakeCatalog(Snap("Canal A", "Canal B"));
            var svc = new SearchService(cat);
            var first = svc.Search("canal");
            var firstExplore = svc.Explore("deportes");
            var errors = new List<Exception>();
            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                threads.Add(new Thread(() =>
                {
                    try { for (int i = 0; i < 300; i++) { var r = svc.Search("canal"); Assert.True(r.Count >= 1); svc.Explore("futbol"); } }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }));
            }
            var swapper = new Thread(() => { for (int i = 0; i < 100; i++) cat.Set(Snap("Canal X" + i, "Canal Y" + i, "Otro")); });
            threads.ForEach(t => t.Start()); swapper.Start();
            threads.ForEach(t => t.Join()); swapper.Join();
            Assert.Empty(errors);
            Assert.Equal(2, first.Count);
            Assert.Equal("Canal A", first[0].Name);
            Assert.Empty(firstExplore);
        }

        [Fact]
        public void SearchingTwentyThousandChannelsIsFast()
        {
            var names = new string[CatalogBuilder.MaxChannels];
            for (int i = 0; i < names.Length; i++) names[i] = "Canal " + (i % 997) + " " + (i % 13 == 0 ? "Liga" : "Deporte") + " " + i;
            var svc = new SearchService(new TvFakeCatalog(Snap(names)));
            svc.Search("liga");                                                       // warm up
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = 0;
            for (int i = 0; i < 100; i++) total += svc.Search(i % 2 == 0 ? "can" : "liga 99").Count;
            sw.Stop();
            Assert.True(total > 0);
            Assert.True(sw.ElapsedMilliseconds < 3000, "100 searches over 20000 channels took " + sw.ElapsedMilliseconds + " ms");
        }

        // ---------- Recent searches ----------

        [Fact]
        public void RecentQueriesKeepTenMostRecentFirstWithoutRepeatsAndTheOldestLeavesFirst()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()), null, _dir);
            Assert.Empty(svc.RecentQueries);
            for (int i = 1; i <= 12; i++) svc.AddRecentQuery("busqueda " + i);
            // FIFO: the new ones come in at the front and the two oldest (1 and 2) have gone out
            Assert.Equal(new[] { "busqueda 12", "busqueda 11", "busqueda 10", "busqueda 9", "busqueda 8", "busqueda 7", "busqueda 6", "busqueda 5", "busqueda 4", "busqueda 3" }, svc.RecentQueries.ToArray());
            Assert.Equal(10, svc.RecentQueries.Count);
            svc.AddRecentQuery("busqueda 5");                                          // repeated: it is not duplicated, it goes to the first
            Assert.Equal(new[] { "busqueda 5", "busqueda 12", "busqueda 11", "busqueda 10", "busqueda 9", "busqueda 8", "busqueda 7", "busqueda 6", "busqueda 4", "busqueda 3" }, svc.RecentQueries.ToArray());
            svc.AddRecentQuery("  BÚSQUEDA   4 ");                                    // same text ignoring case, accents and spaces: it goes up with the new spelling
            Assert.Equal(new[] { "BÚSQUEDA 4", "busqueda 5", "busqueda 12", "busqueda 11", "busqueda 10", "busqueda 9", "busqueda 8", "busqueda 7", "busqueda 6", "busqueda 3" }, svc.RecentQueries.ToArray());
            svc.AddRecentQuery(null); svc.AddRecentQuery(""); svc.AddRecentQuery("   "); svc.AddRecentQuery("***");
            Assert.Equal(10, svc.RecentQueries.Count);
            Assert.Equal("BÚSQUEDA 4", svc.RecentQueries[0]);
            svc.AddRecentQuery("nueva");                                               // with 10 full, the new one comes in and the oldest goes (the 3rd)
            Assert.Equal("nueva", svc.RecentQueries[0]);
            Assert.DoesNotContain("busqueda 3", svc.RecentQueries.ToArray());
            Assert.Equal(10, svc.RecentQueries.Count);
        }

        [Fact]
        public void TenRecentQueriesArePersistedAndReloadedInOrder()
        {
            var a = new SearchService(new TvFakeCatalog(Real()), null, _dir);
            for (int i = 1; i <= 14; i++) a.AddRecentQuery("consulta " + i);
            var b = new SearchService(new TvFakeCatalog(Real()), null, _dir);
            Assert.Equal(a.RecentQueries.ToArray(), b.RecentQueries.ToArray());
            Assert.Equal(10, b.RecentQueries.Count);
            Assert.Equal("consulta 14", b.RecentQueries[0]);
            Assert.Equal("consulta 5", b.RecentQueries[9]);
        }

        [Fact]
        public void RecentQueriesArePersistentAndTheLongOnesAreClipped()
        {
            var a = new SearchService(new TvFakeCatalog(Real()), null, _dir);
            a.AddRecentQuery("nba"); a.AddRecentQuery(new string('x', 500)); a.AddRecentQuery("liga");
            var b = new SearchService(new TvFakeCatalog(Real()), null, _dir);
            Assert.Equal(3, b.RecentQueries.Count);
            Assert.Equal("liga", b.RecentQueries[0]);
            Assert.Equal(SearchService.MaxQueryLength, b.RecentQueries[1].Length);
            Assert.Equal("nba", b.RecentQueries[2]);
        }

        [Fact]
        public void RecentQueriesToleratesBadFiles()
        {
            string path = Path.Combine(_dir, "searches.json");
            foreach (var junk in new[] { "", "{", "[]", "{\"items\":3}", "\u0000\u0001", "{\"items\":[1,null,{},[]]}" })
            {
                File.WriteAllText(path, junk);
                var svc = new SearchService(new TvFakeCatalog(Real()), null, _dir);
                Assert.Empty(svc.RecentQueries);
                svc.AddRecentQuery("ok");
                Assert.Equal(new[] { "ok" }, new SearchService(new TvFakeCatalog(Real()), null, _dir).RecentQueries.ToArray());
            }
            // too many, repeated and mixed with garbage: at most 10 valid and distinct ones
            File.WriteAllText(path, "{\"items\":[\"a\",\"A\",5,\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\",\"l\"]}");
            Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" }, new SearchService(new TvFakeCatalog(Real()), null, _dir).RecentQueries.ToArray());
            // huge file: it is not even read
            File.WriteAllText(path, "{\"items\":[\"a\"],\"pad\":\"" + new string('p', SearchService.MaxFileBytes) + "\"}");
            Assert.Empty(new SearchService(new TvFakeCatalog(Real()), null, _dir).RecentQueries);
        }

        [Fact]
        public void RecentQueriesWorkWithoutADataDirectoryAndAreSnapshots()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()));
            svc.AddRecentQuery("a");
            var before = svc.RecentQueries;
            svc.AddRecentQuery("b");
            Assert.Single(before);
            Assert.Equal(2, svc.RecentQueries.Count);
        }

        // ---------- Explore ----------

        [Fact]
        public void TheTwelveKindsAreInTheDesignOrder()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()));
            Assert.Equal(new[] { "futbol", "baloncesto", "motor", "tenis", "deportes", "noticias", "cine", "series", "documentales", "musica", "infantil", "internacional" }, svc.Kinds.Select(k => k.Id).ToArray());
            Assert.Equal(new[] { "Fútbol", "Baloncesto", "Motor", "Tenis", "Deportes", "Noticias", "Cine", "Series", "Documentales", "Música", "Infantil", "Internacional" }, svc.Kinds.Select(k => k.Name).ToArray());
        }

        [Fact]
        public void ExploreOnTheRealListsGivesWhatIsExpectedForEveryKind()
        {
            var snap = Real();
            var svc = new SearchService(new TvFakeCatalog(snap));
            Func<string, List<string>> names = k => svc.Explore(k).Select(c => c.Name).ToList();

            var counts = svc.Kinds.ToDictionary(k => k.Id, k => svc.Explore(k.Id).Count);
            Assert.Equal(58, counts["futbol"]);
            Assert.Equal(7, counts["baloncesto"]);
            Assert.Equal(20, counts["motor"]);
            Assert.Equal(4, counts["tenis"]);
            Assert.Equal(272, counts["deportes"]);
            Assert.Equal(2, counts["noticias"]);
            Assert.Equal(7, counts["cine"]);
            Assert.Equal(5, counts["series"]);
            Assert.Equal(1, counts["documentales"]);
            Assert.Equal(0, counts["musica"]);                                         // there is no music in these lists
            Assert.Equal(2, counts["infantil"]);
            Assert.Equal(128, counts["internacional"]);

            // football: Liga de Campeones (from the two lists), LaLiga, Hypermotion, Gol Play...
            var football = names("futbol");
            Assert.True(snap.Channels.Where(c => Norm(c.Name).Contains("liga de campeones")).All(c => football.Contains(c.Name)));
            Assert.Equal(snap.Channels.Count(c => Norm(c.Name).Contains("liga de campeones")), svc.Explore("futbol").Count(c => Norm(c.Name).Contains("liga de campeones")));
            Assert.Contains("DAZN La Liga 1 FHD", football);
            Assert.Contains("M+ LaLiga FHD", football);
            Assert.Contains("Gol Play FHD", football);
            Assert.Contains("Hypermotion FHD", football);
            Assert.Contains("Sky Sports Football", football);
            Assert.DoesNotContain("M+ Baloncesto 1080p", football);
            // basketball
            var basket = names("baloncesto");
            Assert.Contains("M+ Baloncesto 1080p", basket);
            Assert.Contains("NBA League Pass 1080p", basket);
            // motor
            Assert.True(svc.Explore("motor").Count(c => Norm(c.Name).Contains("f1")) >= 10);
            Assert.Contains("DAZN MotoGP FHD", names("motor"));
            Assert.Contains("Motorvision", names("motor"));
            // tennis
            Assert.Contains("Tennis Channel 1080p", names("tenis"));
            Assert.Contains("SuperTennis HD (ES)", names("tenis"));
            // sports: the general drawer contains the disciplines and does not contain what is not sport
            var sports = new HashSet<string>(svc.Explore("deportes").Select(c => c.Id));
            foreach (var k in new[] { "futbol", "baloncesto", "motor", "tenis" }) Assert.All(svc.Explore(k), c => Assert.Contains(c.Id, sports));
            Assert.Contains("Teledeporte 1080p", names("deportes"));
            Assert.Contains("Eurosport 2 1080p", names("deportes"));
            Assert.DoesNotContain("Movistar Estrenos FHD", names("deportes"));
            Assert.DoesNotContain("Nickelodeon HD", names("deportes"));
            Assert.DoesNotContain("La 1 FHD", names("deportes"));
            // the rest
            Assert.Contains("Movistar Estrenos FHD", names("cine"));
            Assert.Contains("Movistar Cine Español FHD", names("cine"));
            Assert.Contains("Movistar Originales FHD", names("series"));
            Assert.Contains("Comedy Central HD", names("series"));
            Assert.Contains("Movistar Documentales FHD", names("documentales"));
            Assert.Contains("Nickelodeon HD", names("infantil"));
            Assert.Contains("Disney Channel", names("infantil"));
            Assert.Contains("Sky Sport News (DE)", names("noticias"));
            // international: the whole "Internacional" category and those marked with a country
            var intl = new HashSet<string>(svc.Explore("internacional").Select(c => c.Id));
            Assert.All(snap.Categories.First(c => c.Name == "Internacional").Channels, c => Assert.Contains(c.Id, intl));
            Assert.Contains("Canal+ Sport 3 PL", names("internacional"));
            Assert.Contains("FOX Deportes AR HD", names("internacional"));
            Assert.DoesNotContain("M+ Liga de Campeones 1080p", names("internacional"));
            Assert.DoesNotContain("Teledeporte 1080p", names("internacional"));
        }

        [Fact]
        public void ExploreKeepsCatalogOrderAndIsInstantAndStable()
        {
            var snap = Real();
            var svc = new SearchService(new TvFakeCatalog(snap));
            var index = snap.Channels.Select((c, i) => new { c.Id, i }).ToDictionary(x => x.Id, x => x.i);
            foreach (var k in svc.Kinds)
            {
                var l = svc.Explore(k.Id);
                Assert.Equal(l.Select(c => index[c.Id]).OrderBy(i => i).ToList(), l.Select(c => index[c.Id]).ToList());
                Assert.Same(l, svc.Explore(k.Id));                                     // precomputed: not rebuilt on every call
                Assert.Equal(l.Count, l.Select(c => c.Id).Distinct().Count());
            }
            Assert.Empty(svc.Explore("no-existe"));
            Assert.Empty(svc.Explore(null));
            Assert.Empty(svc.Explore(""));
            Assert.Empty(svc.Explore("FUTBOL"));                                       // the ids are the contract's, in lowercase
        }

        [Theory]
        [InlineData("Movistar Hypermotion", "", "futbol")]
        [InlineData("DAZN F1 FHD", "", "motor")]
        [InlineData("Formula 1 Live", "", "motor")]
        [InlineData("Canal de Tenis HD", "", "tenis")]
        [InlineData("Basket TV", "", "baloncesto")]
        [InlineData("ACB Plus", "", "baloncesto")]
        [InlineData("Euronews", "", "noticias")]
        [InlineData("24 Horas", "", "noticias")]
        [InlineData("Canal Hollywood", "", "cine")]
        [InlineData("AXN White", "", "series")]
        [InlineData("National Geographic", "", "documentales")]
        [InlineData("Discovery Channel", "", "documentales")]
        [InlineData("MTV Hits", "", "musica")]
        [InlineData("Radio Clasica", "", "musica")]
        [InlineData("Cartoon Network", "", "infantil")]
        [InlineData("Boing", "", "infantil")]
        [InlineData("Clan TVE", "", "infantil")]
        [InlineData("Canal 13", "Fútbol", "futbol")]                                   // the category decides when the name says nothing
        [InlineData("Canal 13", "Cine", "cine")]
        [InlineData("Canal 13", "Infantil", "infantil")]
        [InlineData("Eurosport 1 (DE)", "", "internacional")]
        [InlineData("Canal Sur", "Internacional", "internacional")]
        [InlineData("TF1 [FR]", "", "internacional")]
        [InlineData("Canal Plus PL", "", "internacional")]
        [InlineData("Россия 1", "", "internacional")]
        [InlineData("Eurosport (ES)", "", "deportes")]
        public void KeywordClassification(string name, string category, string kind)
        {
            var ch = new Channel { Id = "x", Name = name, SourceName = name, CategoryName = category, CategoryKey = TextNorm.Normalize(category), Entry = LinkEntry.FromUrl(name, "acestream://" + CatalogBuilderTests.Cid(1)) };
            var svc = new SearchService(new TvFakeCatalog(CatalogBuilder.FromChannels(new[] { ch })));
            Assert.Contains(ch, svc.Explore(kind));
        }

        [Theory]
        [InlineData("La 1 FHD", "", "deportes")]
        [InlineData("Serie A TV", "", "series")]                                       // "Serie A" is Italian football, not a series
        [InlineData("Canal Hollywood", "", "internacional")]
        [InlineData("Eurosport (ES)", "", "internacional")]                            // (ES) is Spain
        [InlineData("DAZN MotoGP", "Movistar Cine", "cine")]                           // the name takes precedence over the category
        [InlineData("Transportes TV", "", "deportes")]
        [InlineData("Teletienda", "", "tenis")]
        public void KeywordClassificationNegatives(string name, string category, string kind)
        {
            var ch = new Channel { Id = "x", Name = name, SourceName = name, CategoryName = category, CategoryKey = TextNorm.Normalize(category), Entry = LinkEntry.FromUrl(name, "acestream://" + CatalogBuilderTests.Cid(1)) };
            var svc = new SearchService(new TvFakeCatalog(CatalogBuilder.FromChannels(new[] { ch })));
            Assert.DoesNotContain(ch, svc.Explore(kind));
        }

        // ---------- Remote ----------

        private static Task<IReadOnlyList<RemoteResult>> Results(params RemoteResult[] r) { return Task.FromResult((IReadOnlyList<RemoteResult>)r); }
        private static RemoteResult R(string name, int cid, string category = "") { return new RemoteResult { Name = name, Url = "acestream://" + CatalogBuilderTests.Cid(cid), Category = category }; }

        [Fact]
        public async Task WithoutProvidersTheRemoteMethodsReturnEmptyAtOnce()
        {
            var svc = new SearchService(new TvFakeCatalog(Real()));
            var t1 = svc.SearchRemoteAsync("liga", CancellationToken.None);
            var t2 = svc.ExploreRemoteAsync("futbol", CancellationToken.None);
            Assert.True(t1.IsCompleted && t2.IsCompleted);                             // without waiting for anything
            Assert.Empty(await t1);
            Assert.Empty(await t2);
            var svc2 = new SearchService(new TvFakeCatalog(Real()), new IRemoteSearchProvider[0]);
            Assert.True(svc2.SearchRemoteAsync("liga", CancellationToken.None).IsCompleted);
        }

        [Fact]
        public async Task RemoteResultsAreConvertedCleanedAndFiltered()
        {
            var snap = Real();
            var inCatalog = snap.Channels[3];
            var p1 = new TvFakeProvider("uno");
            p1.OnSearch = (q, t) => Results(
                R("CANAL NUEVO FHD *", 900, "NEW ERA - DEPORTES"),
                new RemoteResult { Name = "Ya esta", Url = inCatalog.Entry.Url },
                R("Canal Nuevo 1080p **", 900),                                        // the same channel with another name
                new RemoteResult { Name = "Pagina", Url = "http://agenda.example/a.html" },
                new RemoteResult { Name = "Basura", Url = "no es un enlace" },
                new RemoteResult { Name = "Muy largo", Url = "https://cdn.example/" + new string('a', 3000) + ".m3u8" },
                new RemoteResult { Name = "Sin url", Url = null },
                null,
                new RemoteResult { Name = "Hex plano", Url = CatalogBuilderTests.Cid(901) },
                new RemoteResult { Name = "Iman", Url = "magnet:?xt=urn:btih:" + CatalogBuilderTests.Cid(902) },
                new RemoteResult { Name = "Flujo", Url = "https://cdn.example/live.m3u8" },
                new RemoteResult { Name = "  ", Url = "acestream://" + CatalogBuilderTests.Cid(903), Category = "" });
            var p2 = new TvFakeProvider("dos");
            p2.OnSearch = (q, t) => Results(R("Segundo", 904), R("canal nuevo", 900));
            var svc = new SearchService(new TvFakeCatalog(snap), new[] { p1, p2 });
            var r = await svc.SearchRemoteAsync("  canal nuevo ", CancellationToken.None);

            Assert.Equal(new[] { "cid:" + CatalogBuilderTests.Cid(900), "cid:" + CatalogBuilderTests.Cid(901), "ih:" + CatalogBuilderTests.Cid(902), "https://cdn.example/live.m3u8", "cid:" + CatalogBuilderTests.Cid(903), "cid:" + CatalogBuilderTests.Cid(904) },
                r.Select(c => c.Id).ToArray());
            Assert.DoesNotContain(r, c => c.Id == inCatalog.Id);
            Assert.Equal("Canal Nuevo 1080p", r[0].Name);                              // the best name (with quality), clean
            Assert.Equal("1080p", r[0].Quality);
            Assert.Equal("Deportes", r[0].CategoryName);                               // informational
            Assert.Equal("deportes", r[0].CategoryKey);
            Assert.Equal("Hex plano", r[1].Name);
            Assert.All(r, c => { Assert.True(c.Entry.IsProbeable); Assert.NotEqual("", c.Name); Assert.DoesNotContain("*", c.Name); });
            Assert.StartsWith("Canal ", r[4].Name);                                    // without a name: a readable one
            Assert.Equal("", r[1].CategoryName);
            Assert.Equal(1, p1.SearchCalls);
            Assert.Equal(1, p2.SearchCalls);
            Assert.Equal(0, p1.ExploreCalls);
        }

        [Fact]
        public async Task TheProvidersReceiveTheTrimmedQueryAndTheKind()
        {
            string gotQuery = null, gotKind = null;
            var p = new TvFakeProvider("p");
            p.OnSearch = (q, t) => { gotQuery = q; return Results(); };
            p.OnExplore = (k, t) => { gotKind = k; return Results(R("De tenis", 5)); };
            var svc = new SearchService(new TvFakeCatalog(Real()), new[] { p });
            await svc.SearchRemoteAsync("  dazn   f1  ", CancellationToken.None);
            Assert.Equal("dazn f1", gotQuery);
            var r = await svc.ExploreRemoteAsync("tenis", CancellationToken.None);
            Assert.Equal("tenis", gotKind);
            Assert.Single(r);
            // nothing to search or a type that does not exist: nobody is called
            p.SearchCalls = 0; p.ExploreCalls = 0;
            Assert.Empty(await svc.SearchRemoteAsync("   ", CancellationToken.None));
            Assert.Empty(await svc.SearchRemoteAsync(null, CancellationToken.None));
            Assert.Empty(await svc.ExploreRemoteAsync("no-existe", CancellationToken.None));
            Assert.Equal(0, p.SearchCalls + p.ExploreCalls);
        }

        [Fact]
        public async Task ProvidersRunInParallel()
        {
            var aStarted = new TaskCompletionSource<bool>();
            var bStarted = new TaskCompletionSource<bool>();
            var a = new TvFakeProvider("a");
            a.OnSearch = async (q, t) => { aStarted.TrySetResult(true); await bStarted.Task; return new RemoteResult[] { R("De A", 1) }; };
            var b = new TvFakeProvider("b");
            b.OnSearch = async (q, t) => { bStarted.TrySetResult(true); await aStarted.Task; return new RemoteResult[] { R("De B", 2) }; };
            var svc = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new[] { a, b });
            var done = svc.SearchRemoteAsync("x", CancellationToken.None);
            Assert.Same(done, await Task.WhenAny(done, Task.Delay(5000)));              // if they were one after another, it would hang
            Assert.Equal(new[] { "De A", "De B" }, (await done).Select(c => c.Name).ToArray());     // in the providers' order
        }

        [Fact]
        public async Task ASlowProviderIsIgnoredAfterItsDeadlineWithoutBlockingTheOthers()
        {
            var slow = new TvFakeProvider("lento");
            slow.OnSearch = (q, t) => new TaskCompletionSource<IReadOnlyList<RemoteResult>>().Task;     // it never finishes and does not look at the token
            var fast = new TvFakeProvider("rapido");
            fast.OnSearch = (q, t) => Results(R("Rapido", 1));
            var svc = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new[] { slow, fast }) { RemoteTimeout = TimeSpan.FromMilliseconds(250) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await svc.SearchRemoteAsync("x", CancellationToken.None);
            Assert.InRange(sw.ElapsedMilliseconds, 200, 4000);
            Assert.Equal(new[] { "Rapido" }, r.Select(c => c.Name).ToArray());
        }

        [Fact]
        public async Task ADefaultTenSecondDeadlineIsConfigured()
        {
            Assert.Equal(TimeSpan.FromSeconds(10), new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty)).RemoteTimeout);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task AProviderThatThrowsOrReturnsNothingDoesNotBreakTheOthers()
        {
            var syncThrow = new TvFakeProvider("lanza");
            syncThrow.OnSearch = (q, t) => { throw new InvalidOperationException("mal"); };
            var asyncThrow = new TvFakeProvider("falla");
            asyncThrow.OnSearch = async (q, t) => { await Task.Yield(); throw new System.Net.Http.HttpRequestException("503"); };
            var nulls = new TvFakeProvider("nulo");
            nulls.OnSearch = (q, t) => Task.FromResult<IReadOnlyList<RemoteResult>>(null);
            var returnsNull = new TvFakeProvider("tarea nula");
            returnsNull.OnSearch = (q, t) => null;
            var ok = new TvFakeProvider("bien");
            ok.OnSearch = (q, t) => Results(R("Bien", 1));
            var svc = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new IRemoteSearchProvider[] { syncThrow, asyncThrow, nulls, returnsNull, ok });
            var r = await svc.SearchRemoteAsync("x", CancellationToken.None);
            Assert.Equal(new[] { "Bien" }, r.Select(c => c.Name).ToArray());
            // and if all fail, empty and without an exception
            var svc2 = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new IRemoteSearchProvider[] { syncThrow, asyncThrow });
            Assert.Empty(await svc2.SearchRemoteAsync("x", CancellationToken.None));
            Assert.Empty(await svc2.ExploreRemoteAsync("futbol", CancellationToken.None));
        }

        [Fact]
        public async Task CancellationStopsTheCallAndReachesTheProviders()
        {
            CancellationToken seen = CancellationToken.None;
            var started = new TaskCompletionSource<bool>();
            var p = new TvFakeProvider("p");
            p.OnSearch = async (q, t) => { seen = t; started.TrySetResult(true); await Task.Delay(Timeout.Infinite, t); return new RemoteResult[0]; };
            var svc = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new[] { p });
            using (var cts = new CancellationTokenSource())
            {
                var task = svc.SearchRemoteAsync("x", cts.Token);
                await started.Task;
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.True(seen.IsCancellationRequested);
            }
            // an already cancelled token does not even call the providers
            p.SearchCalls = 0;
            var already = new CancellationTokenSource(); already.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SearchRemoteAsync("x", already.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ExploreRemoteAsync("futbol", already.Token));
            Assert.Equal(0, p.SearchCalls);
        }

        [Fact]
        public async Task ARemoteFloodIsCapped()
        {
            var p = new TvFakeProvider("mucho");
            p.OnSearch = (q, t) => Results(Enumerable.Range(1, 2000).Select(i => R("Canal " + i, i)).ToArray());
            var svc = new SearchService(new TvFakeCatalog(CatalogSnapshot.Empty), new[] { p });
            var r = await svc.SearchRemoteAsync("x", CancellationToken.None);
            Assert.Equal(SearchService.MaxRemoteResults, r.Count);
        }
    }
}
