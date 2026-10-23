using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AceEngine.App.Tv;
using AceEngine.Sources;
using Xunit;

namespace AceEngine.Tests.Tv
{
    /// <summary>Random text (control, text direction, stray surrogates, other alphabets, symbols) against everything that processes names: it never throws and respects the caps.</summary>
    public class TvFuzzTests
    {
        private static readonly string[] Pieces =
        {
            "NEW ERA", "new_era", "SPORT TV", "NEW LOOP", " - ", " | ", "ELCANO", "elcano", "*", "**", "★", "(", ")", "[", "]", "1.", "01 -", "(2)", " 2", "HD", "FHD", "1080p", "720P", "4K", "SD", "M+",
            "F1", "DAZN", "de", "del", "LIGA", "liga", "Fútbol", "ÑOÑO", "Матч", "КХЛ", "日本語", "عربي", "‮", "​", "\u0000", "\u0007", "\t", "\r\n", "\uD83D", "\uDE00", "😀", "  ", "   ",
            "(DE)", "[UK]", " PL", "+", "++", "═══", "́", "ß", "Ĳ", "𝗗𝗔𝗭𝗡", "x", "Z", "9", "99999"
        };

        private static string Junk(Random rnd, int maxPieces)
        {
            var sb = new StringBuilder();
            int n = rnd.Next(0, maxPieces);
            for (int i = 0; i < n; i++)
            {
                if (rnd.Next(5) == 0) sb.Append((char)rnd.Next(0, 0x3000));              // any character from the basic plane
                else sb.Append(Pieces[rnd.Next(Pieces.Length)]);
            }
            return sb.ToString();
        }

        private static string[] Words(string s) { return TextNorm.Normalize(s).Split(' '); }

        [Fact]
        public void RandomNamesAndGroupsNeverThrowAndRespectTheBounds()
        {
            var rnd = new Random(20261017);
            var entries = new List<LinkEntry>();
            for (int i = 0; i < 4000; i++)
            {
                string url = rnd.Next(4) == 0 ? "https://cdn.example/" + i + ".m3u8" : "acestream://" + CatalogBuilderTests.Cid(1 + rnd.Next(1500));
                entries.Add(LinkEntry.FromUrl(Junk(rnd, 12), url, rnd.Next(5) == 0 ? null : Junk(rnd, 8)));
            }
            var snap = CatalogBuilder.Build(entries);
            Assert.True(snap.Channels.Count > 1000);
            foreach (var c in snap.Channels)
            {
                Assert.True(c.Name.Length > 0 && c.Name.Length <= 120, "nombre: " + c.Name.Length);
                Assert.True(c.CategoryName.Length > 0 && c.CategoryName.Length <= 60);
                Assert.True(c.SourceName.Length <= 200);
                Assert.Equal(c.Name.Trim(), c.Name);
                Assert.DoesNotContain("  ", c.Name);
                Assert.DoesNotContain("  ", c.CategoryName);
                foreach (char ch in c.Name + c.CategoryName)
                    Assert.False(char.IsControl(ch) || (ch >= '‪' && ch <= '‮') || (ch >= '⁦' && ch <= '⁩') || ch == '​' || ch == '﻿', "forbidden character U+" + ((int)ch).ToString("X4"));
                Assert.DoesNotContain("elcano", Words(c.Name));
                Assert.DoesNotContain("elcano", Words(c.CategoryName));
                Assert.False(c.Name[c.Name.Length - 1] == '*' || c.Name[0] == '*', "nombre=[" + c.Name + "] origen=[" + c.SourceName + "]");
                Assert.Same(snap.Find(c.Id), c);
            }
            Assert.Equal(snap.Channels.Count, snap.Categories.Sum(c => c.Channels.Count));
            Assert.All(snap.Categories, c => Assert.NotEmpty(c.Channels));
            Assert.Equal(snap.Categories.Count, snap.Categories.Select(c => c.Key).Distinct().Count());
        }

        [Fact]
        public void RandomQueriesAndKindsNeverThrow()
        {
            var rnd = new Random(7);
            var entries = new List<LinkEntry>();
            for (int i = 0; i < 1500; i++) entries.Add(CatalogBuilderTests.Ace(i + 1, Junk(rnd, 8), Junk(rnd, 4)));
            var svc = new SearchService(new TvFakeCatalog(CatalogBuilder.Build(entries)));
            for (int i = 0; i < 3000; i++)
            {
                string q = Junk(rnd, 6);
                var r = svc.Search(q);
                Assert.True(r.Count <= SearchService.MaxLocalResults);
                svc.AddRecentQuery(q);
            }
            Assert.True(svc.RecentQueries.Count <= SearchService.MaxRecentQueries);
            foreach (var k in svc.Kinds) svc.Explore(k.Id);
        }

        [Fact]
        public void NormalizeAndTitleAreIdempotentOnTheirOwnOutput()
        {
            var rnd = new Random(3);
            for (int i = 0; i < 3000; i++)
            {
                string s = TextNorm.CleanSpaces(Junk(rnd, 8), 200);
                string n = TextNorm.Normalize(s);
                Assert.Equal(n, TextNorm.Normalize(n));
                string t = TextNorm.ToTitle(s);
                Assert.Equal(TextNorm.Normalize(s), TextNorm.Normalize(t));            // switching to Title Case does not change the text, only the capitalization
            }
        }
    }
}
