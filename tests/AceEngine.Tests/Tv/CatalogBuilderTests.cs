using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
using AceEngine.Sources;
using Xunit;

namespace AceEngine.Tests.Tv
{
    public class CatalogBuilderTests
    {
        internal static string Cid(int n) { return n.ToString("x40"); }
        internal static LinkEntry Ace(int n, string name, string group) { return LinkEntry.FromUrl(name, "acestream://" + Cid(n), group); }

        // ---------- Real data ----------

        [Fact]
        public void RealSecondListBuildsOneChannelPerContentIdAndThirteenCategories()
        {
            var entries = TvRealData.List2;
            var snap = CatalogBuilder.Build(entries);
            // 307 distinct content_ids: the rest of the list is labels, 9 web pages and an invalid id
            int distinct = entries.Where(e => e.Kind == LinkKind.AceStream).Select(e => e.Key).Distinct().Count();
            Assert.Equal(307, distinct);
            Assert.Equal(307, snap.Channels.Count);
            Assert.Equal(13, snap.Categories.Count);
            Assert.Equal(snap.Channels.Count, snap.Channels.Select(c => c.Id).Distinct().Count());                   // zero duplicates per content_id
            Assert.Equal(snap.Channels.Count, snap.Categories.Sum(c => c.Channels.Count));                          // each channel in a single category
            Assert.All(snap.Channels, c => Assert.StartsWith("cid:", c.Id));
            Assert.All(snap.Channels, c => Assert.Same(snap.Find(c.Id), c));
        }

        [Fact]
        public void RealCategoriesAreMergedPrettyAndInFirstAppearanceOrder()
        {
            var snap = CatalogBuilder.Build(TvRealData.List2);
            // order of first appearance in the lists; "NEW ERA - X" joins "X"; "SPORT TV INTERNACIONAL 1/2/3" is "Internacional"; sections without a name
            // of their own ("NEW ERA II/III/V", "NEW LOOP", "SPORT TV", "NEW_ERA (OTROS)") fall into "Otros"
            Assert.Equal(new[]
            {
                "Baloncesto", "Eurosport", "Liga de Campeones", "Movistar", "Movistar Deportes", "Deportes", "NBA", "Tennis", "DAZN", "DAZN F1", "Otros", "Movistar Cine", "Internacional"
            }, snap.Categories.Select(c => c.Name).ToArray());
            var counts = snap.Categories.ToDictionary(c => c.Name, c => c.Channels.Count);
            Assert.Equal(3, counts["Baloncesto"]);
            Assert.Equal(1, counts["Eurosport"]);
            Assert.Equal(24, counts["Liga de Campeones"]);        // 16 + 8 of "NEW ERA - LIGA DE CAMPEONES"
            Assert.Equal(11, counts["Movistar"]);                 // 2 + 9
            Assert.Equal(29, counts["Movistar Deportes"]);        // 21 + 8
            Assert.Equal(20, counts["Deportes"]);                 // 4 + 12 + 4 of "NEW ERA VI - DEPORTES"
            Assert.Equal(3, counts["NBA"]);
            Assert.Equal(10, counts["DAZN"]);
            Assert.Equal(5, counts["DAZN F1"]);
            Assert.Equal(126, counts["Internacional"]);           // 35 + 46 + 45
            Assert.Equal(72, counts["Otros"]);
            Assert.All(snap.Categories, c => Assert.NotEmpty(c.Channels));                                           // the empty ones are not listed
            Assert.Equal(snap.Categories.Select(c => c.Name).Distinct().Count(), snap.Categories.Count);
            Assert.All(snap.Categories, c => Assert.All(c.Channels, ch => { Assert.Equal(c.Key, ch.CategoryKey); Assert.Equal(c.Name, ch.CategoryName); }));
        }

        [Fact]
        public void RealNamesAreCleanAndNoListNameReachesTheScreen()
        {
            var snap = CatalogBuilder.Build(TvRealData.List2);
            Assert.All(snap.Channels, c =>
            {
                Assert.DoesNotContain("*", c.Name);
                Assert.DoesNotContain("  ", c.Name);
                Assert.Equal(c.Name.Trim(), c.Name);
                Assert.NotEqual("", c.Name);
                string n = c.Name.ToLowerInvariant();
                Assert.False(n.StartsWith("new era") || n.StartsWith("new loop") || n.StartsWith("sport tv"), c.Name);
                Assert.DoesNotContain("elcano", n);
                Assert.DoesNotContain("elcano", c.CategoryName.ToLowerInvariant());
            });
            Assert.DoesNotContain(snap.Categories, c => c.Name.ToLowerInvariant().Contains("new era") || c.Name.ToLowerInvariant().Contains("elcano"));
            // the original name is kept separately
            Assert.Contains(snap.Channels, c => c.SourceName == "M+ Liga de Campeones 1080p **" && c.Name == "M+ Liga de Campeones 1080p");
        }

        [Fact]
        public void RealNamesKeepTheQualityTextAndExposeTheQualityLabel()
        {
            var snap = CatalogBuilder.Build(TvRealData.List2);
            var byName = snap.Channels.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            Assert.Equal("1080p", byName["M+ Liga de Campeones 1080p"].Quality);
            Assert.Equal("720p", byName["M+ Liga de Campeones 2 720p"].Quality);
            Assert.Equal("FHD", byName["DAZN La Liga 1 FHD"].Quality);          // the design shows "DAZN La Liga 1 FHD" (the list says "DAZN LA LIGA 1 FHD")
            Assert.Equal("4K", byName["DAZN F1 4K"].Quality);
            Assert.Equal("SD", byName["DAZN F1 SD"].Quality);
            Assert.Equal("HD", byName["Eurosport 2 HD"].Quality);
            Assert.Equal("", byName["DAZN 1"].Quality);
            Assert.Equal("1080p", byName["Teledeporte 1080p"].Quality);
            Assert.Equal("Teledeporte FHD", byName["Teledeporte FHD"].Name);
            Assert.Equal("720p", byName["Pluto TV Auto Motor Sport (720p)"].Quality);
            // names in other alphabets are not touched
            Assert.Contains(snap.Channels, c => c.Name == "Матч! ТВ HD (RU)");
            foreach (var c in snap.Channels) Assert.Equal(QualityOf(c.Name), c.Quality);
        }

        private static string QualityOf(string name)
        {
            // check independent of the implementation: highest tag present as a word
            var words = name.Split(new[] { ' ', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
            foreach (var q in new[] { "4k", "1080p", "fhd", "720p", "hd" })
                if (words.Contains(q)) return q == "4k" ? "4K" : q == "fhd" ? "FHD" : q == "hd" ? "HD" : q;
            return words.Count > 0 && words[words.Count - 1] == "sd" ? "SD" : "";
        }

        [Fact]
        public void TheDesignCategoriesAreThere()
        {
            var snap = CatalogBuilder.Build(TvRealData.List2);
            var names = new HashSet<string>(snap.Categories.Select(c => c.Name));
            foreach (var n in new[] { "Liga de Campeones", "Movistar", "Movistar Deportes", "DAZN", "DAZN F1", "Deportes", "Baloncesto", "NBA", "Eurosport", "Internacional" })
                Assert.Contains(n, names);
        }

        [Fact]
        public void RealFirstListIsASubsetAndMergingBothInAnyOrderGivesTheSameChannelsAndNames()
        {
            var l1 = TvRealData.List1;
            var l2 = TvRealData.List2;
            var only1 = CatalogBuilder.Build(l1);
            Assert.Equal(51, only1.Channels.Count);
            Assert.Equal(new[] { "Baloncesto", "Eurosport", "Liga de Campeones", "Movistar", "Movistar Deportes", "Deportes", "NBA", "Tennis" }, only1.Categories.Select(c => c.Name).ToArray());

            var ab = CatalogBuilder.Build(l1.Concat(l2));
            var ba = CatalogBuilder.Build(l2.Concat(l1));
            Assert.Equal(307, ab.Channels.Count);
            Assert.Equal(307, ba.Channels.Count);
            Assert.Equal(13, ab.Categories.Count);
            Assert.Equal(13, ba.Categories.Count);
            foreach (var c in ab.Channels) Assert.Equal(c.Name, ba.Find(c.Id).Name);       // the best name does not depend on the order of the lists
            foreach (var c in ab.Channels) Assert.Equal(c.Quality, ba.Find(c.Id).Quality);
        }

        [Fact]
        public void TheSameLinksFromTheM3uListAreTheSameChannels()
        {
            var tree = CatalogBuilder.Build(TvRealData.List2);
            var m3u = TvRealData.M3u;
            Assert.Equal(308, m3u.Count);                                    // 307 valid ones and the id with a "y"
            var both = CatalogBuilder.Build(tree.Channels.Select(c => c.Entry).Concat(m3u));
            Assert.Equal(307, both.Channels.Count);
            Assert.Equal(tree.Categories.Count, both.Categories.Count);
            foreach (var c in tree.Channels) Assert.Equal(c.Name, both.Find(c.Id).Name);
        }

        // ---------- Merging and rules ----------

        [Fact]
        public void EquivalentCategoriesOfTwoListsAreMergedAndKeepTheOrderOfFirstAppearance()
        {
            var a = new List<LinkEntry> { Ace(1, "Canal A 1080p", "NEW ERA - LIGA DE CAMPEONES"), Ace(2, "Canal B", "NEW ERA - DAZN") };
            var b = new List<LinkEntry> { Ace(3, "Canal C", "LIGA DE CAMPEONES 2"), Ace(4, "Canal D", "Liga de Campeones (3)"), Ace(5, "Canal E", "1. Dazn") };
            var snap = CatalogBuilder.Build(a.Concat(b));
            Assert.Equal(new[] { "Liga de Campeones", "DAZN" }, snap.Categories.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Canal A 1080p", "Canal C", "Canal D" }, snap.Categories[0].Channels.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Canal B", "Canal E" }, snap.Categories[1].Channels.Select(c => c.Name).ToArray());
            Assert.Equal("liga de campeones", snap.Categories[0].Key);
        }

        [Theory]
        [InlineData("NEW ERA - LIGA DE CAMPEONES", "liga de campeones", "Liga de Campeones")]
        [InlineData("LIGA DE CAMPEONES 2", "liga de campeones", "Liga de Campeones")]
        [InlineData("Liga de Campeones", "liga de campeones", "Liga de Campeones")]
        [InlineData("1. DEPORTES", "deportes", "Deportes")]
        [InlineData("01 - Deportes", "deportes", "Deportes")]
        [InlineData("(2) NBA", "nba", "NBA")]
        [InlineData("[3] NBA", "nba", "NBA")]
        [InlineData("1) Deportes", "deportes", "Deportes")]
        [InlineData("#3 NBA", "nba", "NBA")]
        [InlineData("NBA Nº 2", "nba", "NBA")]
        [InlineData("2019 Mundial", "2019 mundial", "2019 Mundial")]
        [InlineData("NBA (2)", "nba", "NBA")]
        [InlineData("NEW ERA VI - DEPORTES", "deportes", "Deportes")]
        [InlineData("NEW ERA - MOVISTAR DEPORTES", "movistar deportes", "Movistar Deportes")]
        [InlineData("new_era - dazn f1", "dazn f1", "DAZN F1")]
        [InlineData("SPORT TV INTERNACIONAL 2", "internacional", "Internacional")]
        [InlineData("★ FÚTBOL ★", "futbol", "Fútbol")]
        [InlineData("═══ EXTERNOS ═══", "externos", "Externos")]
        [InlineData("  Fútbol   Americano  ", "futbol americano", "Fútbol Americano")]
        [InlineData("M+ Series", "m+ series", "M+ Series")]
        [InlineData("MOVISTAR+ CINE", "movistar+ cine", "Movistar+ Cine")]
        [InlineData("NEW ERA VI - MOVISTAR CINE", "movistar cine", "Movistar Cine")]
        [InlineData("COPA DEL REY", "copa del rey", "Copa del Rey")]
        [InlineData("NEW_ERA (OTROS)", "otros", "Otros")]
        [InlineData("NEW ERA II", "otros", "Otros")]
        [InlineData("NEW ERA III", "otros", "Otros")]
        [InlineData("NEW LOOP", "otros", "Otros")]
        [InlineData("SPORT TV", "otros", "Otros")]
        [InlineData("ELCANO", "otros", "Otros")]
        [InlineData("", "otros", "Otros")]
        [InlineData("***", "otros", "Otros")]
        // what must NOT be touched
        [InlineData("24 Horas", "24 horas", "24 Horas")]
        [InlineData("F1", "f1", "F1")]
        [InlineData("4K Cine", "4k cine", "4K Cine")]
        [InlineData("TV 3", "tv 3", "TV 3")]
        [InlineData("La 1", "la 1", "La 1")]
        [InlineData("Eurosport 2020", "eurosport 2020", "Eurosport 2020")]
        [InlineData("NEW ERAS", "new eras", "New Eras")]
        public void CategoryNamesAreNormalizedAndPrettified(string raw, string key, string name)
        {
            var snap = CatalogBuilder.Build(new[] { Ace(1, "Canal", raw) });
            var c = Assert.Single(snap.Categories);
            Assert.Equal(key, c.Key);
            Assert.Equal(name, c.Name);
        }

        [Fact]
        public void OnlyPlayableLinksEnterTheCatalog()
        {
            var entries = new List<LinkEntry>
            {
                LinkEntry.FromUrl("Actualizado: 16/10/2026 11:51 UTC", "", "ELCANO"),
                LinkEntry.FromUrl("═══ EXTERNOS ═══", "", null),
                LinkEntry.FromUrl("Agenda", "https://agendas.example/agenda_futbol", "EVENTOS"),          // web page
                LinkEntry.FromUrl("Roto", "acestream://" + Cid(9).Replace('0', 'y'), "X"),                // invalid id
                LinkEntry.FromUrl("Otro esquema", "ftp://x/y.ts", "X"),
                LinkEntry.FromUrl("Flujo", "https://cdn.example/live/canal.m3u8?token=1", "X"),
                LinkEntry.FromUrl("Imán", "magnet:?xt=urn:btih:" + Cid(7), "X"),
                Ace(1, "Bueno", "X"),
                null
            };
            var snap = CatalogBuilder.Build(entries);
            Assert.Equal(3, snap.Channels.Count);
            Assert.Equal(new[] { "Flujo", "Imán", "Bueno" }, snap.Channels.Select(c => c.Name).ToArray());
            Assert.Equal("https://cdn.example/live/canal.m3u8?token=1", snap.Channels[0].Id);
            Assert.Equal("ih:" + Cid(7), snap.Channels[1].Id);
            Assert.Equal("cid:" + Cid(1), snap.Channels[2].Id);
            Assert.All(snap.Channels, c => Assert.True(c.Entry.IsProbeable));
        }

        [Fact]
        public void ACategoryWithOnlyUnplayableLinksDoesNotAppear()
        {
            var snap = CatalogBuilder.Build(new[] { LinkEntry.FromUrl("Agenda", "https://x.example/a.html", "AGENDAS"), Ace(1, "Canal", "DEPORTES") });
            Assert.Equal(new[] { "Deportes" }, snap.Categories.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void AChannelInTwoCategoriesBelongsToTheFirstOneAndIsNotLost()
        {
            var snap = CatalogBuilder.Build(new[]
            {
                Ace(1, "Canal", "FUTBOL"), Ace(2, "Otro", "FUTBOL"),
                Ace(1, "Canal", "FAVORITOS"),                    // same channel in a second category
                Ace(3, "Solo", "FAVORITOS"),
                Ace(2, "Otro", "REPETIDOS")                      // this category ends up empty: it is not listed
            });
            Assert.Equal(new[] { "Futbol", "Favoritos" }, snap.Categories.Select(c => c.Name).ToArray());
            Assert.Equal(3, snap.Channels.Count);
            Assert.Equal("futbol", snap.Find("cid:" + Cid(1)).CategoryKey);
            Assert.Equal(new[] { "Canal", "Otro" }, snap.Categories[0].Channels.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Solo" }, snap.Categories[1].Channels.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void EntriesWithoutGroupGoToOthers()
        {
            var snap = CatalogBuilder.Build(new[] { Ace(1, "Canal", null), Ace(2, "Canal 2", "") });
            var c = Assert.Single(snap.Categories);
            Assert.Equal("Otros", c.Name);
            Assert.Equal(2, c.Channels.Count);
        }

        // ---------- Best name ----------

        [Fact]
        public void TheBestNameDoesNotDependOnTheOrderOfTheLists()
        {
            // the same channel with different names: the one with quality wins, then the one not all in uppercase, then the longest, then the lowest
            var names = new[] { "LIGA DE CAMPEONES", "LIGA DE CAMPEONES FHD", "M+ Liga de Campeones 1080p **", "Liga de Campeones HD *", "NEW ERA - LIGA DE CAMPEONES FHD" };
            var all = names.Select((n, i) => Ace(1, n, "G" + i)).ToList();
            string expected = null;
            var rnd = new Random(42);
            for (int round = 0; round < 40; round++)
            {
                var order = all.OrderBy(x => rnd.Next()).ToList();
                var snap = CatalogBuilder.Build(order);
                var c = Assert.Single(snap.Channels);
                if (expected == null) expected = c.Name;
                Assert.Equal(expected, c.Name);
                Assert.Equal("1080p", c.Quality);
            }
            Assert.Equal("M+ Liga de Campeones 1080p", expected);
        }

        [Fact]
        public void BestNamePrefersQualityThenHandWrittenCaseThenMoreInformation()
        {
            Func<string[], string> best = ns => CatalogBuilder.Build(ns.Select((n, i) => Ace(1, n, "G")).ToList()).Channels[0].Name;
            Assert.Equal("Canal HD", best(new[] { "Canal", "CANAL", "Canal HD" }));                          // quality
            Assert.Equal("Canal Uno FHD", best(new[] { "CANAL UNO FHD", "Canal Uno FHD" }));                 // case: the more careful one (both end the same, the lowest wins)
            Assert.Equal("Movistar Plus FHD", best(new[] { "MOVISTAR PLUS FHD", "Movistar Plus FHD", "Movistar FHD" }));
            Assert.Equal("Canal Uno 1080p", best(new[] { "Canal 1080p", "Canal Uno 1080p" }));               // more information
        }

        [Fact]
        public void TheEntryOfTheWinningNameIsTheOneKept()
        {
            var a = Ace(1, "canal", "G");
            var b = Ace(1, "Canal Mejor 720p", "G");
            Assert.Same(b, CatalogBuilder.Build(new[] { a, b }).Channels[0].Entry);
            Assert.Same(b, CatalogBuilder.Build(new[] { b, a }).Channels[0].Entry);
        }

        // ---------- Names ----------

        [Theory]
        [InlineData("M+ Baloncesto 2 1080p *", "M+ Baloncesto 2 1080p", "1080p")]
        [InlineData("M+ Baloncesto 2 1080p **", "M+ Baloncesto 2 1080p", "1080p")]
        [InlineData("M+ Baloncesto 2 1080p * *", "M+ Baloncesto 2 1080p", "1080p")]
        [InlineData("  M+   Liga   de Campeones   720p  ", "M+ Liga de Campeones 720p", "720p")]
        [InlineData("NEW ERA - DAZN 1 FHD", "DAZN 1 FHD", "FHD")]
        [InlineData("NEW ERA VI - DAZN F1 FHD", "DAZN F1 FHD", "FHD")]
        [InlineData("New Era: Movistar Plus HD", "Movistar Plus HD", "HD")]
        [InlineData("LIGA DE CAMPEONES 2 FHD", "Liga de Campeones 2 FHD", "FHD")]
        [InlineData("DAZN LA LIGA 1 FHD", "DAZN La Liga 1 FHD", "FHD")]
        [InlineData("M+ LALIGA", "M+ LaLiga", "")]
        [InlineData("M+ VAMOS 2 720p *", "M+ Vamos 2 720p", "720p")]
        [InlineData("DAZN F1 4K", "DAZN F1 4K", "4K")]
        [InlineData("LA 1 4K", "La 1 4K", "4K")]
        [InlineData("CAZA Y PESCA FHD HD", "Caza y Pesca FHD HD", "FHD")]
        [InlineData("beIN SPORTS HD 1 (TR)", "beIN SPORTS HD 1 (TR)", "HD")]
        [InlineData("BEIN SPORTS 2", "beIN Sports 2", "")]
        [InlineData("SKY SPORTS LALIGA HD", "Sky Sports LaLiga HD", "HD")]
        [InlineData("RTL+ SPORT EVENT 1 (DE)", "RTL+ Sport Event 1 (DE)", "")]
        [InlineData("Pluto TV Auto Motor Sport (720p)", "Pluto TV Auto Motor Sport (720p)", "720p")]
        [InlineData("FOX DEPORTES AR HD", "FOX Deportes AR HD", "HD")]
        [InlineData("Eurosport 1 1080P", "Eurosport 1 1080P", "1080p")]
        [InlineData("eurosport 1 1080P", "Eurosport 1 1080p", "1080p")]
        [InlineData("DISCOVERY CHANNEL", "Discovery Channel", "")]
        [InlineData("canal cocina fhd", "Canal Cocina FHD", "FHD")]
        [InlineData("Матч! ТВ HD (RU)", "Матч! ТВ HD (RU)", "HD")]
        [InlineData("КХЛ ТВ (RU)", "КХЛ ТВ (RU)", "")]
        [InlineData("Teledeporte SD", "Teledeporte SD", "SD")]
        [InlineData("SD Huesca TV", "SD Huesca TV", "")]
        [InlineData("Canal 4K Plus HD", "Canal 4K Plus HD", "4K")]
        [InlineData("Disney Channel", "Disney Channel", "")]
        [InlineData("Esport3 1080p *", "Esport3 1080p", "1080p")]
        [InlineData("LUZ —> New Era", "LUZ —> New Era", "")]
        [InlineData("Canal Elcano HD", "Canal HD", "HD")]
        // real names from Ace's search engine and from the descriptors (2026-10-17): the broadcaster's advertising is not shown
        [InlineData("DAZN LaLiga 1080 elcano.top zeronet http://127.0.0.1:43110/18D6dPcsjLrjg2hhnYqKzNh2W6QtXrDwF/ https://ipfs.io/ipns/elcano.top", "DAZN LaLiga 1080", "")]
        [InlineData("DAZN 1 1080 elcano.top zeronet http://127.0.0.1:43110/18D6dPcsjLrjg2hhnYqKzNh2W6QtXrDwF/ https://ipfs.io/ipns/elcano.top", "DAZN 1 1080", "")]
        [InlineData("DAZN LaLiga Shickat | shickat.online (VPN) | shickat.blog | https://ipfs.io/ipns/k51qzi5uqu5dgni30z98zbli9a3vv2xtp8cek1xjikfsels3a259i5clyta19p", "DAZN LaLiga", "")]
        [InlineData("RFEFTV 1080 elcano.top https://x.com/ace_stream_ https://t.me/acestreamf1", "Rfeftv 1080", "")]
        [InlineData("elcano.top M+ LaLiga 1080p", "M+ LaLiga 1080p", "1080p")]
        [InlineData("Canal 24h @canal24 t.me/canal24", "Canal 24h", "")]
        [InlineData("La 1 1.5 HD", "La 1 1.5 HD", "HD")]                    // a number with a dot is not a domain
        [InlineData("Sky Sports - Main Event HD", "Sky Sports - Main Event HD", "HD")]     // without links, the name's hyphens stay
        public void ChannelNamesAreCleaned(string raw, string name, string quality)
        {
            var snap = CatalogBuilder.Build(new[] { Ace(1, raw, "G") });
            var c = Assert.Single(snap.Channels);
            Assert.Equal(name, c.Name);
            Assert.Equal(quality, c.Quality);
        }

        [Fact]
        public void NamelessEntriesGetAReadableNameAndStayInTheCatalog()
        {
            var snap = CatalogBuilder.Build(new[] { Ace(1, "", "G"), Ace(2, "  **  ", "G"), LinkEntry.FromUrl("", "https://cdn.example/live/canal5.m3u8", "G") });
            Assert.Equal(3, snap.Channels.Count);
            Assert.Equal("Canal 00000000", snap.Channels[0].Name);
            Assert.Equal("Canal 00000000", snap.Channels[1].Name.Substring(0, 14));
            Assert.Equal("canal5.m3u8", snap.Channels[2].Name);
        }

        // ---------- Hostile input ----------

        [Fact]
        public void HostileLinksNeverThrowAndAreBounded()
        {
            var entries = new List<LinkEntry>();
            entries.Add(Ace(1, new string('A', 100000), new string('G', 100000)));
            entries.Add(Ace(2, "‮evil‬​ name\u0000\u0007\t\r\n", "‮\u0000 grupo ​"));
            entries.Add(Ace(3, "\uD83D", "\uDE00"));                                    // stray surrogates
            entries.Add(Ace(4, "Emoji 😀 ok", "G"));
            entries.Add(new LinkEntry { Name = null, Url = "x", Group = null, Kind = LinkKind.AceStream, Key = "cid:" + Cid(5) });
            entries.Add(new LinkEntry { Name = "k", Url = "x", Group = "g", Kind = LinkKind.HttpStream, Key = new string('k', 100000) });
            entries.Add(new LinkEntry { Name = "z", Url = "x", Group = "g", Kind = LinkKind.AceStream, Key = null });
            var snap = CatalogBuilder.Build(entries);
            Assert.Equal(5, snap.Channels.Count);
            Assert.All(snap.Channels, c => Assert.True(c.Name.Length <= 120 && c.CategoryName.Length <= 60 && c.SourceName.Length <= 200));
            Assert.All(snap.Channels, c => Assert.DoesNotContain(c.Name, ch => ch < ' ' || (ch >= '‪' && ch <= '‮') || ch == '​'));
            Assert.Equal("Evil Name", snap.Find("cid:" + Cid(2)).Name);
            Assert.Equal("Grupo", snap.Find("cid:" + Cid(2)).CategoryName);
        }

        [Fact]
        public void HugeListsAreCapped()
        {
            var entries = new List<LinkEntry>();
            for (int i = 0; i < CatalogBuilder.MaxChannels + 500; i++) entries.Add(Ace(i + 1, "Canal " + i, "G" + (i % 7)));
            var snap = CatalogBuilder.Build(entries);
            Assert.Equal(CatalogBuilder.MaxChannels, snap.Channels.Count);
            Assert.Equal(7, snap.Categories.Count);
        }

        [Fact]
        public void NullAndEmptyInputGiveAnEmptyCatalog()
        {
            Assert.Empty(CatalogBuilder.Build(null).Channels);
            Assert.Empty(CatalogBuilder.Build(new LinkEntry[0]).Categories);
            Assert.Null(CatalogSnapshot.Empty.Find("x"));
            Assert.Null(CatalogSnapshot.Empty.Find(null));
        }

        [Fact]
        public void FromChannelsGroupsByKeyAndDropsRepeatedIds()
        {
            var a = new Channel { Id = "a", Name = "A", CategoryKey = "k1", CategoryName = "Uno" };
            var b = new Channel { Id = "b", Name = "B", CategoryKey = "k2", CategoryName = "Dos" };
            var a2 = new Channel { Id = "a", Name = "A2", CategoryKey = "k2", CategoryName = "Dos" };
            var c = new Channel { Id = "c", Name = "C", CategoryKey = "k1", CategoryName = "Uno" };
            var snap = CatalogBuilder.FromChannels(new[] { a, b, a2, c, null });
            Assert.Equal(new[] { "a", "b", "c" }, snap.Channels.Select(x => x.Id).ToArray());
            Assert.Equal(new[] { "Uno", "Dos" }, snap.Categories.Select(x => x.Name).ToArray());
            Assert.Equal(new[] { "a", "c" }, snap.Categories[0].Channels.Select(x => x.Id).ToArray());
        }

        // ---------- Text ----------

        [Theory]
        [InlineData("Fútbol", "futbol")]
        [InlineData("ÁÉÍÓÚÜÑ ñ ç", "aeiouun n c")]
        [InlineData("M+ Liga", "m+ liga")]
        [InlineData("++ DEPORTES ++", "deportes")]
        [InlineData("Canal+ Sport", "canal+ sport")]
        [InlineData("a--b__c", "a b c")]
        [InlineData("Ñandú", "nandu")]
        [InlineData("Łódź", "lodz")]
        [InlineData("é", "e")]
        [InlineData("Матч! ТВ", "матч тв")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizeFoldsCaseAccentsAndSymbols(string raw, string expected)
        {
            Assert.Equal(expected, TextNorm.Normalize(raw));
        }

        [Theory]
        [InlineData("LIGA DE CAMPEONES", "Liga de Campeones")]
        [InlineData("DAZN F1 FHD", "DAZN F1 FHD")]
        [InlineData("NBA", "NBA")]
        [InlineData("ESPN HD (US)", "ESPN HD (US)")]
        [InlineData("tv de la sexta", "TV de La Sexta")]
        [InlineData("COPA DEL REY", "Copa del Rey")]
        [InlineData("Liga de Campeones", "Liga de Campeones")]
        [InlineData("beIN Sports", "beIN Sports")]
        [InlineData("MOVISTAR VI", "Movistar VI")]
        [InlineData("DE", "De")]
        [InlineData("A", "A")]
        public void TitleCaseRespectsAcronymsAndOnlyChangesUniformCase(string raw, string expected)
        {
            Assert.Equal(expected, TextNorm.ToTitle(raw));
        }
    }
}
