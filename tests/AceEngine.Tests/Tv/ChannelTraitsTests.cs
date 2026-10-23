using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv
{
    /// <summary>Minimum resolution and language of the channels (Settings), with real names from the lists (dumped 2026-10-16).</summary>
    public class ChannelTraitsTests
    {
        private static CatalogSnapshot Real() { return CatalogBuilder.Build(TvRealData.List2); }

        // ------------------------------------------------------------------ quality
        [Theory]
        [InlineData("M+ Liga de Campeones 2 720p **", 720)]
        [InlineData("M+ Liga de Campeones 2 1080p **", 1080)]
        [InlineData("DAZN 1 FHD", 1080)]
        [InlineData("CALLE 13 HD", 720)]
        [InlineData("LA 1 4K", 2160)]
        [InlineData("DAZN F1 4K", 2160)]
        [InlineData("DAZN F1 SD", 480)]
        [InlineData("CAZA Y PESCA FHD HD", 1080)]                       // the highest of the tags
        [InlineData("Pluto TV Auto Motor Sport (720p)", 720)]
        [InlineData("Eurosport360 HD 1 (DE)", 720)]
        [InlineData("EUROSPORT 1", 0)]
        [InlineData("BEIN SPORTS 2", 0)]
        [InlineData("SKY SPORTS CRICKET", 0)]
        [InlineData("Canal 1 (1RFEF) (SOLO EVENTOS)", 0)]
        [InlineData("SD Huesca", 0)]                                    // a club, not a quality
        public void TheResolutionComesFromTheQualityTagInTheName(string name, int expected)
        {
            Assert.Equal(expected, ChannelResolution.FromLabel(QualityRules.Detect(name)));
        }

        [Fact]
        public void HdIs720pFhdIs1080pAndUhdIs4k()
        {
            Assert.Equal(720, ChannelResolution.FromLabel("HD"));
            Assert.Equal(1080, ChannelResolution.FromLabel("FHD"));
            Assert.Equal(1080, ChannelResolution.FromLabel("1080p"));
            Assert.Equal(2160, ChannelResolution.FromLabel("UHD"));
            Assert.Equal(2160, ChannelResolution.FromLabel("2160p"));
            Assert.Equal(0, ChannelResolution.FromLabel(""));
            Assert.Equal(0, ChannelResolution.FromLabel("xyz"));
        }

        [Fact]
        public void TheMinimumResolutionKeepsThatQualityOrHigherAndAnyDropsNothing()
        {
            var snap = Real();
            int any = snap.Channels.Count;
            int p720 = snap.Channels.Count(c => ChannelResolution.Passes(c, 720));
            int p1080 = snap.Channels.Count(c => ChannelResolution.Passes(c, 1080));
            int p4k = snap.Channels.Count(c => ChannelResolution.Passes(c, 2160));
            Assert.True(any > p720 && p720 >= p1080 && p1080 > p4k && p4k > 0, any + " " + p720 + " " + p1080 + " " + p4k);
            Assert.All(snap.Channels.Where(c => ChannelResolution.Passes(c, 1080)), c => Assert.True(c.Resolution >= 1080));
            // a channel with no recognizable quality only shows up with "Cualquiera" (any)
            var untagged = snap.Channels.Where(c => c.Quality.Length == 0).ToList();
            Assert.NotEmpty(untagged);
            Assert.All(untagged, c => { Assert.True(ChannelResolution.Passes(c, 0)); Assert.False(ChannelResolution.Passes(c, 720)); });
            // SD does not reach any minimum
            var sd = snap.Channels.Where(c => c.Quality == "SD").ToList();
            Assert.NotEmpty(sd);
            Assert.All(sd, c => Assert.False(ChannelResolution.Passes(c, 720)));
        }

        [Theory]
        [InlineData(0, 0)] [InlineData(1500, 480)] [InlineData(2000, 720)] [InlineData(3999, 720)] [InlineData(4000, 1080)] [InlineData(9999, 1080)] [InlineData(10000, 2160)]
        public void TheBitrateGivesAnApproximateResolutionWhenTheNameSaysNothing(int kbps, int expected)
        {
            Assert.Equal(expected, ChannelResolution.FromBitrateKbps(kbps));
        }

        [Fact]
        public void ABitrateLearnedWhenCheckingTheChannelGivesItAResolutionThatSurvivesARestart()
        {
            string dir = TvTempDir.Create();
            try
            {
                string id = "cid:" + new string('b', 38) + "01";
                var c = new Channel { Id = id, Name = "Canal sin calidad", Quality = "" };
                Assert.Equal(0, c.Resolution);
                ChannelHints.Attach(dir);
                ChannelHints.Learn(id, 5200);
                Assert.Equal(1080, c.Resolution);                       // from the descriptor
                Assert.True(ChannelResolution.Passes(c, 1080)); Assert.False(ChannelResolution.Passes(c, 2160));
                // the name takes precedence over the bitrate
                Assert.Equal(720, new Channel { Id = id, Name = "Otro HD", Quality = "HD" }.Resolution);
                // and the Ace search engine's metadata comes before the learned bitrate
                Assert.Equal(2160, new Channel { Id = id, Name = "Otro", Quality = "", MetaResolution = 2160 }.Resolution);
                ChannelHints.Detach();                                   // saves
                Assert.True(File.Exists(Path.Combine(dir, ChannelHints.FileName)));
                ChannelHints.Reset();
                Assert.Equal(0, c.Resolution);
                ChannelHints.Attach(dir);                                // "restart": what was saved is loaded
                Assert.Equal(1080, c.Resolution);
            }
            finally { ChannelHints.Detach(); try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ language: real names
        [Theory]
        [InlineData("beIN SPORTS HD 1 (TR)", "tr")]
        [InlineData("beIN SPORTS HABER (TR)", "tr")]
        [InlineData("BT Sport 1 [UK]", "en")]
        [InlineData("ESPN2 [US]", "en")]
        [InlineData("ESPN HD [US]", "en")]
        [InlineData("ESPN 2 [BE]", "nl")]
        [InlineData("FOX Sports 1 HD (US)", "en")]
        [InlineData("Canal de Tenis HD (ES)", "es")]
        [InlineData("M. Deportes HD (ES)", "es")]
        [InlineData("SuperTennis HD (ES)", "es")]
        [InlineData("CANAL+ Sport 2 (PL) HD", "pl")]
        [InlineData("CANAL+ Sport (PL)", "pl")]
        [InlineData("Polsat Sport Premium 1 (PL)", "pl")]
        [InlineData("TVP Sport HD (PL)", "pl")]
        [InlineData("Eurosport (PL)", "pl")]
        [InlineData("Eurosport360 HD 1 (DE)", "de")]
        [InlineData("Eurosport360 HD 6  (DE)", "de")]
        [InlineData("Magenta Sport 1 HD (DE)", "de")]
        [InlineData("RTL+ SPORT EVENT 1 (DE)", "de")]
        [InlineData("Sky Sport 1 HD (DE)", "de")]
        [InlineData("Eurosport 1 HD (RU)", "ru")]
        [InlineData("UFC HD (RU)", "ru")]
        [InlineData("Матч! Футбол 1 ( original ) (RU)", "ru")]
        [InlineData("Бокс ТВ", "ru")]                                   // no tag: by the script
        [InlineData("КХЛ HD", "ru")]
        [InlineData("SkySP PL (UK)", "en")]                             // "PL" here is Premier League; the parenthesis rules
        [InlineData("Canal+ Sport 3 PL", "pl")]                         // loose tag at the end
        [InlineData("FOX DEPORTES AR HD", "es")]
        [InlineData("Ziggo Sport (ES)", "es")]                          // the list's tag rules over the name
        [InlineData("ELEVEN DAZN 1", "pt")]
        [InlineData("ELEVEN DAZN 4 HD", "pt")]
        [InlineData("Sport TV 1", "pt")]
        [InlineData("SPORT TV+ HD", "pt")]
        [InlineData("Eleven Sports 1 [PL]", "pl")]                      // the tag wins over the known name
        [InlineData("ZIGGO SPORT 2", "nl")]
        [InlineData("SKY SPORTS ARENA", "en")]
        [InlineData("BT Sport 2", "en")]
        [InlineData("Auto Motor und Sport Channel", "de")]
        [InlineData("M+ Liga de Campeones 2 1080p **", "es")]
        [InlineData("DAZN LA LIGA 1 FHD", "es")]
        [InlineData("MOVISTAR DEPORTES 2 FHD", "es")]
        [InlineData("TELEDEPORTE FHD", "es")]
        [InlineData("ESPORT 3 FHD", "es")]
        [InlineData("Tennis Channel 1080p **", "en")]
        [InlineData("NBA League Pass 1080p *", "en")]
        // without a language signal: unknown, and the UI treats it as Spanish
        [InlineData("EUROSPORT 1 HD", "")]
        [InlineData("DAZN F1 FHD", "")]
        [InlineData("BEIN SPORTS 2", "")]
        [InlineData("CANAL+ SPORT 2", "")]
        [InlineData("Pluto TV Auto Motor Sport (720p)", "")]
        [InlineData("Canal 1 (1RFEF) (SOLO EVENTOS)", "")]
        [InlineData("NO ACESTREAM(MOTOR)", "")]
        [InlineData("Film+ HD", "")]
        [InlineData("noa4 Hamburg FHD", "")]
        public void TheLanguageOfRealChannelNames(string source, string expected)
        {
            string clean = ChannelNames.Clean(source);
            Assert.Equal(expected, ChannelLanguages.Detect(clean, source, null));
        }

        [Fact]
        public void TheCategoryNamedSportTvIsAListNameNotPortugueseAndInternationalCategoriesSayNothing()
        {
            // "SPORT TV" in the real lists is the name of a list (DAZN, M+ LaLiga...): its channels are not Portuguese
            Assert.Equal("", ChannelLanguages.Detect("DAZN 1", "DAZN 1", "SPORT TV"));
            Assert.Equal("", ChannelLanguages.Detect("EUROSPORT 2 HD", "EUROSPORT 2 HD", "SPORT TV INTERNACIONAL 1"));
            // but a category with a country does guide
            Assert.Equal("pt", ChannelLanguages.Detect("Canal X", "Canal X", "Portugal"));
            Assert.Equal("pl", ChannelLanguages.Detect("Canal X", "Canal X", "Polska [PL]"));
            Assert.Equal("en", ChannelLanguages.Detect("Canal X", "Canal X", "Premier League"));
        }

        [Fact]
        public void TheMetadataOfTheAceSearchWinsOverTheNameAndTheMarks()
        {
            Assert.Equal("pt", ChannelLanguages.Detect("Canal X", "Canal X (ES)", null, new[] { "por" }, null));
            Assert.Equal("pl", ChannelLanguages.Detect("Canal X", "Canal X", null, new string[0], new[] { "pl" }));
            Assert.Equal("en", ChannelLanguages.Detect("Canal X", "Canal X", null, new[] { "en-GB" }, null));
            Assert.Equal("es", ChannelLanguages.Detect("Canal X", "Canal X", null, new[] { "Spanish" }, null));
            Assert.Equal("de", ChannelLanguages.Detect("Canal X", "Canal X", null, new[] { "zzz" }, new[] { "de" }));     // unreadable language: the country is looked at
            Assert.Equal("", ChannelLanguages.Detect("Canal X", "Canal X", null, new[] { "zzz" }, new[] { "xx" }));
        }

        [Fact]
        public void NormalizeAcceptsCodesAndNames()
        {
            foreach (var s in new[] { "es", "ES", "spa", "Spanish", "español", "Castellano" }) Assert.Equal("es", ChannelLanguages.Normalize(s));
            foreach (var s in new[] { "en", "eng", "English", "Inglés" }) Assert.Equal("en", ChannelLanguages.Normalize(s));
            Assert.Equal("pt", ChannelLanguages.Normalize("por")); Assert.Equal("pl", ChannelLanguages.Normalize("polaco")); Assert.Equal("de", ChannelLanguages.Normalize("ger"));
            Assert.Equal("", ChannelLanguages.Normalize(null)); Assert.Equal("", ChannelLanguages.Normalize("")); Assert.Equal("", ChannelLanguages.Normalize("klingon")); Assert.Equal("", ChannelLanguages.Normalize("12"));
        }

        // ------------------------------------------------------------------ language: the real catalog
        [Fact]
        public void TheRealCatalogHasChannelsInSeveralLanguagesAndMostOfThemAreSpanishOrUnknown()
        {
            var snap = Real();
            var by = snap.Channels.GroupBy(c => c.Language).ToDictionary(g => g.Key, g => g.Count());
            foreach (var l in new[] { "es", "de", "ru", "pl", "tr", "en" }) Assert.True(by.ContainsKey(l) && by[l] > 0, "falta " + l + ": " + string.Join(",", by.Select(kv => kv.Key + "=" + kv.Value)));
            Assert.True(by["de"] >= 30, "de=" + by["de"]);               // Magenta Sport, RTL+, Eurosport360...
            Assert.True(by["ru"] >= 25, "ru=" + by["ru"]);
            Assert.True(by["pl"] >= 10, "pl=" + by["pl"]);
            int unknown = by.ContainsKey("") ? by[""] : 0;
            Assert.True(by["es"] + unknown > snap.Channels.Count / 2, "the lists are mostly Spanish");
        }

        [Fact]
        public void EveryRealChannelWithACountryMarkInItsNameGetsThatLanguage()
        {
            var map = new Dictionary<string, string> { { "DE", "de" }, { "RU", "ru" }, { "PL", "pl" }, { "TR", "tr" }, { "UK", "en" }, { "US", "en" }, { "ES", "es" }, { "BE", "nl" } };
            int checkedCount = 0;
            foreach (var e in TvRealData.List2)
            {
                string n = e.Name ?? "";
                foreach (var m in map)
                {
                    if (n.IndexOf("(" + m.Key + ")", StringComparison.Ordinal) < 0 && n.IndexOf("[" + m.Key + "]", StringComparison.Ordinal) < 0) continue;
                    // several parentheses in the same name: the last one rules
                    if (map.Count(o => n.Contains("(" + o.Key + ")") || n.Contains("[" + o.Key + "]")) > 1) continue;
                    Assert.Equal(m.Value, ChannelLanguages.Detect(ChannelNames.Clean(n), n, e.Group));
                    checkedCount++;
                }
            }
            Assert.True(checkedCount >= 80, "tags checked: " + checkedCount);
        }

        [Fact]
        public void EveryChannelOfTheRealCatalogHasALanguageTheSettingsCanOffer()
        {
            var snap = Real();
            var opts = ChannelLanguages.Options(snap.Channels, "");
            Assert.Equal(new[] { "", "es", "en", "pt", "pl" }, opts.Take(5).ToArray());
            foreach (var l in new[] { "de", "ru", "tr" }) Assert.Contains(l, opts);
            Assert.Equal(opts.Count, opts.Distinct().Count());
            foreach (var c in snap.Channels) Assert.True(c.Language == "" || opts.Contains(c.Language), c.Name + " -> " + c.Language);
            // the current language is offered even if it no longer appears in the data
            Assert.Contains("fi", ChannelLanguages.Options(snap.Channels, "fi"));
            // a catalog with no channels still offers Todos, Espanol, Ingles, Portugues and Polaco
            Assert.Equal(new[] { "", "es", "en", "pt", "pl" }, ChannelLanguages.Options(new Channel[0], "").ToArray());
        }

        [Fact]
        public void TheLanguageFilterKeepsSpanishAndUnknownWithSpanishAndOnlyThatLanguageWithTheRest()
        {
            var snap = Real();
            var s = new UserSettings();
            var all = ChannelFilter.Apply(snap.Channels, s, true);
            Assert.Equal(snap.Channels.Count, all.Count);                // Todos
            s.Language = "es";
            var es = ChannelFilter.Apply(snap.Channels, s, true);
            Assert.All(es, c => Assert.True(c.Language == "es" || c.Language == ""));
            Assert.DoesNotContain(es, c => c.Language == "de" || c.Language == "ru" || c.Language == "pl");
            Assert.Contains(es, c => c.Language == "");                  // the unknown ones show up with Espanol
            s.Language = "pl";
            var pl = ChannelFilter.Apply(snap.Channels, s, true);
            Assert.NotEmpty(pl);
            Assert.All(pl, c => Assert.Equal("pl", c.Language));        // the unknown ones do NOT show up with Polaco
            s.Language = "en";
            Assert.All(ChannelFilter.Apply(snap.Channels, s, true), c => Assert.Equal("en", c.Language));
            s.Language = "pt";
            Assert.All(ChannelFilter.Apply(snap.Channels, s, true), c => Assert.Equal("pt", c.Language));
        }

        [Fact]
        public void TheLanguageDoesNotApplyToRecentsButTheResolutionDoes()
        {
            var snap = Real();
            var s = new UserSettings { Language = "pl", MinResolution = 1080 };
            var withLang = ChannelFilter.Apply(snap.Channels, s, true);
            var recents = ChannelFilter.Apply(snap.Channels, s, false);
            Assert.True(recents.Count > withLang.Count);
            Assert.All(recents, c => Assert.True(c.Resolution >= 1080));
            Assert.Contains(recents, c => c.Language != "pl");
        }

        [Fact]
        public void CategoriesLeftWithoutVisibleChannelsDisappear()
        {
            var snap = Real();
            var s = new UserSettings();
            Assert.Equal(snap.Categories.Count, ChannelFilter.VisibleCategories(snap.Categories, s).Count);
            s.Language = "ru";
            var ru = ChannelFilter.VisibleCategories(snap.Categories, s);
            Assert.True(ru.Count > 0 && ru.Count < snap.Categories.Count, ru.Count + "/" + snap.Categories.Count);
            Assert.All(ru, cat => Assert.Contains(cat.Channels, c => ChannelFilter.Passes(c, s, true)));
            s.Language = ""; s.MinResolution = 2160;
            var k4 = ChannelFilter.VisibleCategories(snap.Categories, s);
            Assert.True(k4.Count > 0 && k4.Count < snap.Categories.Count);
            // with no channel that reaches: no category is left
            var none = new CatalogSnapshot(new Channel[0], new Category[0]);
            Assert.Empty(ChannelFilter.VisibleCategories(none.Categories, s));
            var onlyUnknownQuality = CatalogBuilder.Build(new[] { LinkEntry.FromUrl("Canal sin calidad", "acestream://" + CatalogBuilderTests.Cid(1), "General") });
            s.MinResolution = 720; s.Language = "";
            Assert.Empty(ChannelFilter.VisibleCategories(onlyUnknownQuality.Categories, s));
            s.MinResolution = 0;
            Assert.Single(ChannelFilter.VisibleCategories(onlyUnknownQuality.Categories, s));
        }

        // ------------------------------------------------------------------ saved settings
        [Fact]
        public void TheSettingsStoreKeepsTheResolutionAndTheLanguage()
        {
            string dir = TvTempDir.Create();
            try
            {
                var store = UserSettingsStore.InDirectory(dir);
                var s = new UserSettings { MinResolution = 1080, Language = "pt" };
                Assert.True(store.Save(s));
                var back = store.Load();
                Assert.Equal(1080, back.MinResolution); Assert.Equal("pt", back.Language);
                Assert.Equal(0, new UserSettings().MinResolution); Assert.Equal("", new UserSettings().Language);        // by default: Cualquiera and Todos
                Assert.Equal(0, UserSettingsStore.Parse("{}").MinResolution); Assert.Equal("", UserSettingsStore.Parse("{}").Language);
                // odd values: they are corrected
                Assert.Equal(720, UserSettingsStore.Parse("{\"minResolution\": 900}").MinResolution);
                Assert.Equal(2160, UserSettingsStore.Parse("{\"minResolution\": 99999}").MinResolution);
                Assert.Equal(0, UserSettingsStore.Parse("{\"minResolution\": -5}").MinResolution);
                Assert.Equal(0, UserSettingsStore.Parse("{\"minResolution\": \"mucho\"}").MinResolution);
                Assert.Equal("es", UserSettingsStore.Parse("{\"language\": \"Spanish\"}").Language);
                Assert.Equal("", UserSettingsStore.Parse("{\"language\": 5}").Language);
                Assert.Equal("", UserSettingsStore.Parse("{\"language\": \"klingon\"}").Language);
                // the saved JSON carries the two keys and the others continue
                string json = UserSettingsStore.ToJson(s);
                Assert.Contains("minResolution", json); Assert.Contains("language", json); Assert.Contains("onlyLive", json);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ Ace search engine: metadata
        [Fact]
        public void TheAceSearchItemsCarryBitrateLanguagesAndCountries()
        {
            var o = MiniJson.AsObject(MiniJson.Parse("{\"name\":\"BBC One HD\",\"infohash\":\"" + new string('a', 40) + "\",\"bitrate\":562500,\"languages\":[\"eng\"],\"countries\":[\"gb\"]}"));
            var r = RemoteResultParser.FromItem(o, null);
            Assert.NotNull(r);
            Assert.Equal(4500, r.BitrateKbps);
            Assert.Equal(new[] { "eng" }, r.Languages); Assert.Equal(new[] { "gb" }, r.Countries);
            var compact = MiniJson.AsObject(MiniJson.Parse("{\"n\":\"X\",\"i\":\"" + new string('b', 40) + "\",\"b\":312500}"));
            var r2 = RemoteResultParser.FromItem(compact, null);
            Assert.Equal(2500, r2.BitrateKbps); Assert.Empty(r2.Languages); Assert.Empty(r2.Countries);
            // Real item from the search engine (2026-10-17): "DAZN LaLiga 1080" with b=902408. It is BYTES/s (~7.2 Mbps; a live stream of that kind goes down at ~700 KB/s):
            // read as bits it came out as 480p and the minimum-resolution filter hid all the Ace results
            var real = RemoteResultParser.FromItem(MiniJson.AsObject(MiniJson.Parse("{\"n\":\"DAZN LaLiga 1080\",\"i\":\"" + new string('d', 40) + "\",\"b\":902408}")), null);
            Assert.Equal(1080, ChannelResolution.FromBitrateKbps(real.BitrateKbps));
            var junk = MiniJson.AsObject(MiniJson.Parse("{\"name\":\"X\",\"infohash\":\"" + new string('c', 40) + "\",\"bitrate\":-3,\"languages\":\"es, pt ,,toolonglanguagename\",\"countries\":5}"));
            var r3 = RemoteResultParser.FromItem(junk, null);
            Assert.Equal(0, r3.BitrateKbps); Assert.Equal(new[] { "es", "pt" }, r3.Languages); Assert.Empty(r3.Countries);
        }

        [Fact]
        public async Task RemoteResultsGetTheirResolutionFromTheNameOrTheBitrateAndTheirLanguageFromTheMetadata()
        {
            var snap = Real();
            var p = new TvFakeProvider("meta");
            string cid(int n) { return "acestream://" + CatalogBuilderTests.Cid(n); }
            p.OnSearch = (q, t) => Task.FromResult((IReadOnlyList<RemoteResult>)new List<RemoteResult>
            {
                new RemoteResult { Name = "Canal Uno", Url = cid(700), BitrateKbps = 5000, Languages = new[] { "por" } },
                new RemoteResult { Name = "Canal Dos 720p", Url = cid(701), BitrateKbps = 12000, Countries = new[] { "pl" } },
                new RemoteResult { Name = "Canal Tres (DE)", Url = cid(702) },
                new RemoteResult { Name = "Canal Siete", Url = cid(703) }
            });
            var svc = new SearchService(new TvFakeCatalog(snap), new[] { p });
            var r = await svc.SearchRemoteAsync("canal", CancellationToken.None);
            var by = r.ToDictionary(c => c.Name);
            Assert.Equal(1080, by["Canal Uno"].Resolution); Assert.Equal("pt", by["Canal Uno"].Language);
            Assert.Equal(720, by["Canal Dos 720p"].Resolution); Assert.Equal("pl", by["Canal Dos 720p"].Language);      // the name takes precedence over the bitrate
            Assert.Equal(0, by["Canal Tres (DE)"].Resolution); Assert.Equal("de", by["Canal Tres (DE)"].Language);
            Assert.Equal(0, by["Canal Siete"].Resolution); Assert.Equal("", by["Canal Siete"].Language);
        }
    }
}
