using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Sources
{
    public class DnsTxtClientTests
    {
        private static DnsOptions Opt(FakeDnsServer s, Action<DnsOptions> tweak = null)
        {
            var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = s.Port, Timeout = TimeSpan.FromSeconds(2), UseDohFallback = false };
            if (tweak != null) tweak(o);
            return o;
        }

        [Fact]
        public void QueryBytes()
        {
            // id 0x1234, RD, 1 question; www.ab -> 3www2ab0; TXT (16) IN (1)
            Assert.Equal("1234" + "0100" + "0001" + "0000" + "0000" + "0000" + "03777777" + "026162" + "00" + "0010" + "0001",
                TestUtil.ToHex(DnsTxtClient.BuildQuery("www.ab", 0x1234)));
            // the code's uppercase letters are sent as is
            var q = DnsTxtClient.BuildQuery("LN6d7dDtYZX91TYkaoCvtCFanwPMTvIdkn1cV2zKnaGXp5S1S9.elcano.top.", 1);
            Assert.Contains("LN6d7dDtYZX91TYkaoCvtCFanwPMTvIdkn1cV2zKnaGXp5S1S9", Encoding.ASCII.GetString(q));
            Assert.Equal(50, q[12]);
            Assert.Throws<ArgumentException>(() => DnsTxtClient.BuildQuery(new string('a', 64) + ".top", 1));
            Assert.Throws<ArgumentException>(() => DnsTxtClient.BuildQuery("a..b", 1));
        }

        [Fact]
        public void ParsesMultiStringRecordsAndCompression()
        {
            // hand-made response: 1 question, 2 TXT answers; the first with two strings ("ab", "cd"), the second with one
            var hex = "abcd" + "8180" + "0001" + "0002" + "0000" + "0000"
                + "0161" + "00" + "0010" + "0001"
                + "c00c" + "0010" + "0001" + "0000003c" + "0006" + "026162" + "026364"
                + "c00c" + "0010" + "0001" + "0000003c" + "0003" + "02" + "7878";
            var r = DnsTxtClient.ParseResponse(TestUtil.FromHex(hex));
            Assert.Equal(0xabcd, r.Id); Assert.True(r.IsResponse); Assert.False(r.Truncated); Assert.Equal(0, r.Rcode);
            Assert.Equal(new[] { "abcd", "xx" }, r.Txt.ToArray());
            Assert.Null(DnsTxtClient.ParseResponse(new byte[5]));
            Assert.Null(DnsTxtClient.ParseResponse(TestUtil.FromHex(hex.Substring(0, hex.Length - 6))));        // truncated RDATA
        }

        [Fact]
        public async Task AnswersOverUdp()
        {
            using (var dns = new FakeDnsServer())
            {
                dns.Add("abc.elcano.top", "hola");
                var c = new DnsTxtClient(Opt(dns));
                var r = await c.QueryTxtAsync("abc.elcano.top", CancellationToken.None);
                Assert.Equal(new[] { "hola" }, r.ToArray());
                Assert.Single(dns.UdpNames); Assert.Empty(dns.TcpNames);
            }
        }

        [Fact]
        public async Task LongRecordsAreSplitIn255ByteStringsAndJoinedWithoutSeparator()
        {
            using (var dns = new FakeDnsServer { TruncateUdpOverBytes = 100000 })
            {
                var big = new string('Z', 700) + "fin";
                dns.Add("big.elcano.top", big);
                var r = await new DnsTxtClient(Opt(dns)).QueryTxtAsync("big.elcano.top", CancellationToken.None);
                Assert.Equal(big, r.Single());
            }
        }

        [Fact]
        public async Task TruncatedUdpResponseIsRepeatedOverTcp()
        {
            using (var dns = new FakeDnsServer { TruncateUdpOverBytes = 512 })
            {
                var big = new string('Q', 2200);
                dns.Add("code.elcano.top", big);
                var r = await new DnsTxtClient(Opt(dns)).QueryTxtAsync("code.elcano.top", CancellationToken.None);
                Assert.Equal(big, r.Single());
                Assert.Single(dns.UdpNames); Assert.Single(dns.TcpNames);               // UDP with TC=1 and then TCP
            }
        }

        [Fact]
        public async Task NxDomainGivesNoRecordsAndCasePreserved()
        {
            using (var dns = new FakeDnsServer())
            {
                var r = await new DnsTxtClient(Opt(dns)).QueryTxtAsync("NoExiste.elcano.top", CancellationToken.None);
                Assert.Empty(r);
                Assert.Equal("NoExiste.elcano.top", dns.UdpNames.Single());
            }
        }

        [Fact]
        public async Task IgnoresResponsesWithAnotherIdAndFailsOnServerError()
        {
            using (var dns = new FakeDnsServer { WrongIdFirst = true })
            {
                dns.Add("a.elcano.top", "ok");
                Assert.Equal("ok", (await new DnsTxtClient(Opt(dns)).QueryTxtAsync("a.elcano.top", CancellationToken.None)).Single());
                dns.ServfailAll = true;
                await Assert.ThrowsAsync<DnsException>(() => new DnsTxtClient(Opt(dns)).QueryTxtAsync("a.elcano.top", CancellationToken.None));
            }
        }

        [Fact]
        public async Task FailsWithoutAnswerWhenDohIsOff()
        {
            using (var dns = new FakeDnsServer { Silent = true })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Assert.ThrowsAsync<DnsException>(() => new DnsTxtClient(Opt(dns, o => { o.Timeout = TimeSpan.FromMilliseconds(400); o.UdpAttempts = 2; })).QueryTxtAsync("a.elcano.top", CancellationToken.None));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
            }
        }

        [Fact]
        public async Task FallsBackToDnsOverHttpsWhenPort53IsBlocked()
        {
            using (var dns = new FakeDnsServer { Silent = true })
            using (var doh = new FakeHttpServer(path =>
            {
                Assert.Contains("name=abc.elcano.top", path); Assert.Contains("type=TXT", path);
                // Google's data field brings the strings in quotes separated by spaces
                return new KeyValuePair<int, byte[]>(200, Encoding.UTF8.GetBytes("{\"Status\":0,\"Answer\":[{\"name\":\"abc.elcano.top.\",\"type\":16,\"TTL\":60,\"data\":\"\\\"aaa\\\" \\\"bbb\\\"\"},{\"name\":\"x\",\"type\":5,\"data\":\"cname\"}]}"));
            }))
            {
                var o = Opt(dns, x => { x.Timeout = TimeSpan.FromMilliseconds(300); x.UdpAttempts = 1; x.UseDohFallback = true; x.DohUrl = "http://127.0.0.1:" + doh.Port + "/resolve"; });
                var r = await new DnsTxtClient(o).QueryTxtAsync("abc.elcano.top", CancellationToken.None);
                Assert.Equal(new[] { "aaabbb" }, r.ToArray());
                Assert.Single(doh.Requests);
            }
        }

        [Fact]
        public void DohJsonParsing()
        {
            Assert.Empty(DnsTxtClient.ParseDohJson(Encoding.UTF8.GetBytes("{\"Status\":3}")));
            Assert.Empty(DnsTxtClient.ParseDohJson(Encoding.UTF8.GetBytes("{\"Status\":0}")));
            Assert.Throws<DnsException>(() => DnsTxtClient.ParseDohJson(Encoding.UTF8.GetBytes("{\"Status\":2}")));
            Assert.Equal("a\"b\\c", DnsTxtClient.UnquoteTxt("\"a\\\"b\\\\c\""));
            Assert.Equal("sin comillas", DnsTxtClient.UnquoteTxt("sin comillas"));
        }
    }

    public class MyLinksPasteTests
    {
        private static string Real(string name) { return Encoding.UTF8.GetString(TestUtil.Fixture("real/" + name)); }

        [Fact]
        public void DecodesTheRealUncompressedAndGzippedRecords()
        {
            // raw TXT records of the root of the two real codes, as the user dumped them: strings in quotes separated by spaces
            var a = MyLinksPasteClient.Decode(Real("a1b2c3d4e5f60718293a4b5c.txt"));
            Assert.Single(a);
            Assert.Equal("category", a[0].Type); Assert.Equal("695d5309d05ee80b5cd7efad", a[0].Ref); Assert.Equal("", a[0].Name);
            var b = MyLinksPasteClient.Decode(Real("b2c3d4e5f60718293a4b5c6d.txt"));
            Assert.NotEmpty(b);
            Assert.True(b.Count(e => e.Type == "category") >= 4);
            Assert.Contains(b, e => e.Type == null && e.Name.Contains("DESDE AQUI PARA ABAJO"));         // labels without a link
            Assert.Contains(b, e => e.Ref == "a1b2c3d4e5f60718293a4b5c");            // the second code includes the first
        }

        [Fact]
        public void DecodeToleratesTextPrefixNewlinesMissingPaddingAndPlainJson()
        {
            var elements = new List<object> { new Dictionary<string, object> { { "name", "A" }, { "url", "acestream://c8c33b2673efe7f386790c5966643820578f3535" } } };
            var plain = FakeDnsServer.EncodeRecord(elements, false);
            var gz = FakeDnsServer.EncodeRecord(elements, true);
            foreach (var rec in new[] { plain, gz, "text = \"" + plain + "\"", plain.TrimEnd('='), plain.Substring(0, 8) + "\n" + plain.Substring(8), "\"" + gz.Substring(0, 10) + "\" \"" + gz.Substring(10) + "\"" })
            {
                var e = MyLinksPasteClient.Decode(rec);
                Assert.Single(e);
                Assert.Equal("A", e[0].Name); Assert.Equal("acestream://c8c33b2673efe7f386790c5966643820578f3535", e[0].Url);
            }
            // gzip with a correct header but corrupt data: the bytes are used as they are and the JSON fails
            Assert.Throws<FormatException>(() => MyLinksPasteClient.Decode(Convert.ToBase64String(new byte[] { 0x1f, 0x8b, 1, 2, 3, 4 })));
        }

        [Theory]
        [InlineData("")]
        [InlineData("!!!!")]
        [InlineData("YQ==")]                                   // "a": not JSON
        [InlineData("MTIz")]                                   // "123": JSON, but not a list of items
        public void DecodeRejectsGarbage(string rec)
        {
            Assert.Throws<FormatException>(() => MyLinksPasteClient.Decode(rec));
        }

        [Fact]
        public void FlattenKeepsGroupsAndSubLinks()
        {
            var e = new MyLinksPasteClient.RawElement
            {
                Name = "Canal", Url = "",
                SubLinks = new List<MyLinksPasteClient.RawElement>
                {
                    new MyLinksPasteClient.RawElement { Name = "Canal 1080p", Url = "acestream://c8c33b2673efe7f386790c5966643820578f3535" },
                    new MyLinksPasteClient.RawElement { Name = "Canal web", Url = "http://agenda.test/x.html" }
                }
            };
            var f = MyLinksPasteClient.Flatten(e, "DEPORTES").ToList();
            Assert.Equal(2, f.Count);
            Assert.All(f, x => { Assert.Equal("DEPORTES", x.Group); Assert.Equal("Canal", x.Parent); });
            Assert.Equal(LinkKind.AceStream, f[0].Kind); Assert.Equal(LinkKind.Web, f[1].Kind);
            // without an inherited group, the group is the item's name
            Assert.Equal("Canal", MyLinksPasteClient.Flatten(e, null).First().Group);
        }

        /// <summary>Fake DNS that serves the real tree dumped by the user, one in two with gzip.</summary>
        private static FakeDnsServer ServeRealTree(out int codes)
        {
            var dns = new FakeDnsServer();
            codes = 0;
            foreach (var file in new[] { "a1b2c3d4e5f60718293a4b5c.json", "b2c3d4e5f60718293a4b5c6d.json" })
            {
                var root = MiniJson.AsObject(MiniJson.Parse(TestUtil.Fixture("real/" + file)));
                foreach (var kv in root)
                {
                    if (dns.Records.ContainsKey(kv.Key + ".elcano.top")) continue;
                    dns.Add(kv.Key + ".elcano.top", FakeDnsServer.EncodeRecord(kv.Value, codes % 2 == 0));
                    codes++;
                }
            }
            return dns;
        }

        [Fact]
        public async Task FetchesTheRealFirstCodeAndMatchesTheM3uTheUserDumped()
        {
            int codes;
            using (var dns = ServeRealTree(out codes))
            {
                var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(3), UseDohFallback = false };
                var client = new MyLinksPasteClient(new DnsTxtClient(o));
                var tree = await client.FetchAsync("a1b2c3d4e5f60718293a4b5c", CancellationToken.None);
                Assert.Equal(10, tree.Visited.Count);                                      // 1 root + 1 index + 8 categories (the codes from the dump)
                Assert.Empty(tree.Errors);
                var ace = tree.Entries.Where(e => e.Kind == LinkKind.AceStream).ToList();
                Assert.Equal(51, ace.Count);                                               // 51 of 51 are acestream://
                Assert.Equal(2, tree.Entries.Count(e => e.Kind == LinkKind.Label));        // "Actualizado: ..." and "EXTERNOS"
                Assert.All(ace, e => Assert.True(Hex.IsHex40(e.Link.Value)));
                // Each link's group is the category it hangs from
                Assert.Equal("BALONCESTO", ace.First(e => e.Url.EndsWith("c8c33b2673efe7f386790c5966643820578f3535")).Group);
                Assert.Equal("M+ Baloncesto 2 1080p *", ace.First().Name);

                // Each link of the first code is in the M3U the user dumped with the same name and group
                var m3u = M3uParser.Parse(Real("mylinkspaste.m3u"));
                var byUrl = m3u.GroupBy(e => e.Url).ToDictionary(g => g.Key, g => g.First());
                foreach (var e in ace)
                {
                    LinkEntry m;
                    Assert.True(byUrl.TryGetValue(e.Url, out m), "missing from the M3U: " + e.Url);
                    Assert.Equal(m.Name, e.Name); Assert.Equal(m.Group, e.Group);
                }
            }
        }

        [Fact]
        public async Task FetchesTheSecondRealCodeRecursivelyWithSharedReferencesAndNoLoops()
        {
            int codes;
            using (var dns = ServeRealTree(out codes))
            {
                // an artificial cycle: the first index points back to the root
                var extra = new List<object> { new Dictionary<string, object> { { "name", "bucle" }, { "type", "category" }, { "ref", "b2c3d4e5f60718293a4b5c6d" } } };
                dns.Add("cycle1.elcano.top", FakeDnsServer.EncodeRecord(extra, true));
                var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(3), UseDohFallback = false };
                var client = new MyLinksPasteClient(new DnsTxtClient(o));
                var tree = await client.FetchAsync("b2c3d4e5f60718293a4b5c6d", CancellationToken.None);
                Assert.Equal(30, tree.Visited.Count);
                var all = tree.Entries;
                var ace = all.Where(e => e.Kind == LinkKind.AceStream).ToList();
                Assert.Equal(307, ace.Count);                                              // 307 acestream:// (plus one with a non-hexadecimal character)
                Assert.Single(all, e => e.Kind == LinkKind.Unsupported);
                Assert.Equal(9, all.Count(e => e.Kind == LinkKind.Web));                   // schedule pages: not playable
                var dedup = LinkList.Dedupe(all);
                // the user's M3U holds the unique links: after deduplicating by id they match in number and in set
                var m3u = M3uParser.Parse(Real("mylinkspaste.m3u"));
                var ours = new HashSet<string>(dedup.Where(e => e.Kind == LinkKind.AceStream).Select(e => e.Link.Value));
                var theirs = new HashSet<string>(m3u.Where(e => e.Kind == LinkKind.AceStream).Select(e => e.Link.Value));
                Assert.Equal(theirs.Count, ours.Count);
                Assert.True(theirs.SetEquals(ours));
            }
        }

        [Fact]
        public async Task ErrorsInASubCodeAreIgnoredButTheRootMustExist()
        {
            using (var dns = new FakeDnsServer())
            {
                dns.Add("root.elcano.top", FakeDnsServer.EncodeRecord(new List<object>
                {
                    new Dictionary<string, object> { { "name", "malo" }, { "type", "category" }, { "ref", "nada" } },
                    new Dictionary<string, object> { { "name", "roto" }, { "type", "category" }, { "ref", "roto" } },
                    new Dictionary<string, object> { { "name", "X" }, { "url", "acestream://c8c33b2673efe7f386790c5966643820578f3535" } }
                }, false));
                dns.Add("roto.elcano.top", "esto no es base64 valido!!");
                var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(2), UseDohFallback = false };
                var client = new MyLinksPasteClient(new DnsTxtClient(o));
                var tree = await client.FetchAsync("root", CancellationToken.None);
                Assert.Single(tree.Entries);
                Assert.Equal(2, tree.Errors.Count);
                await Assert.ThrowsAsync<DnsException>(() => client.FetchAsync("inexistente", CancellationToken.None));
            }
        }

        [Fact]
        public async Task OnlyTheFirstTxtRecordIsUsed()
        {
            using (var dns = new FakeDnsServer())
            {
                dns.Add("a.elcano.top", FakeDnsServer.EncodeRecord(new List<object> { new Dictionary<string, object> { { "name", "primero" }, { "url", "acestream://c8c33b2673efe7f386790c5966643820578f3535" } } }, false),
                    FakeDnsServer.EncodeRecord(new List<object> { new Dictionary<string, object> { { "name", "segundo" }, { "url", "acestream://88113ee23295b8930ae328a88a8c1b861b8f21f1" } } }, false));
                var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(2), UseDohFallback = false };
                var tree = await new MyLinksPasteClient(new DnsTxtClient(o)).FetchAsync("a", CancellationToken.None);
                Assert.Equal("primero", tree.Entries.Single().Name);
            }
        }
    }

    public class M3uAndLinkTests
    {
        private static string Real(string name) { return Encoding.UTF8.GetString(TestUtil.Fixture("real/" + name)); }

        [Fact]
        public void ParsesTheRealM3uWith308Links()
        {
            var l = M3uParser.Parse(Real("mylinkspaste.m3u"));
            Assert.Equal(308, l.Count);
            // 307 are valid content_ids; one carries a non-hexadecimal character at the end (d4ff...f33y) and is not usable
            Assert.Equal(307, l.Count(e => e.Kind == LinkKind.AceStream));
            Assert.Single(l, e => e.Kind == LinkKind.Unsupported && e.Url.EndsWith("f33y"));
            Assert.Equal("M+ Baloncesto 2 1080p *", l[0].Name); Assert.Equal("BALONCESTO", l[0].Group);
            Assert.Equal("acestream://c8c33b2673efe7f386790c5966643820578f3535", l[0].Url);
            Assert.Equal("cid:c8c33b2673efe7f386790c5966643820578f3535", l[0].Key);
            Assert.Equal(308, LinkList.Dedupe(l).Count);                                   // the M3U already comes with only the unique ones
            Assert.Contains(l, e => e.Group == "LIGA DE CAMPEONES");
        }

        [Fact]
        public void ParsesAllTheLineVariants()
        {
            var text = "﻿#EXTM3U\r\n"
                + "#EXTINF:-1 tvg-id=\"a,b\" tvg-logo=\"http://x/l.png\" group-title=\"Depor,tes\",Canal 1, HD\r\n"
                + "acestream://c8c33b2673efe7f386790c5966643820578f3535\r\n"
                + "#EXTVLCOPT:network-caching=1000\r\n"
                + "#EXTGRP:Cine\r\n"
                + "#EXTINF:120,Peli\r\n"
                + "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel\r\n"
                + "http://ejemplo.test/live/index.m3u8\r\n"
                + "#EXTINF:-1 tvg-name=\"Solo atributo\",\r\n"
                + "https://ejemplo.test/agenda.html\r\n"
                + "\r\n"
                + "88113ee23295b8930ae328a88a8c1b861b8f21f1\r\n";
            var l = M3uParser.Parse(text);
            Assert.Equal(5, l.Count);
            Assert.Equal("Canal 1, HD", l[0].Name); Assert.Equal("Depor,tes", l[0].Group); Assert.Equal(LinkKind.AceStream, l[0].Kind);
            Assert.Equal("Peli", l[1].Name); Assert.Equal("Cine", l[1].Group); Assert.Equal(LinkKind.Magnet, l[1].Kind);
            Assert.Equal("ih:08ada5a7a6183aae1e09d831df6748d566095a10", l[1].Key);
            Assert.Equal(LinkKind.HttpStream, l[2].Kind); Assert.Equal("index.m3u8", l[2].Name);              // without #EXTINF: default name
            Assert.Equal("Solo atributo", l[3].Name); Assert.Equal(LinkKind.Web, l[3].Kind);
            Assert.Equal(LinkKind.AceStream, l[4].Kind); Assert.Equal("cid:88113ee23295b8930ae328a88a8c1b861b8f21f1", l[4].Key);
        }

        [Fact]
        public void RelativeUrlsAreResolvedAndHlsPlaylistsAreNotChannelLists()
        {
            var l = M3uParser.Parse("#EXTM3U\n#EXTINF:-1,A\nstream/a.ts\n#EXTINF:-1,B\n/b.m3u8\n", "http://srv.test/lists/main.m3u");
            Assert.Equal("http://srv.test/lists/stream/a.ts", l[0].Url); Assert.Equal("http://srv.test/b.m3u8", l[1].Url);
            var hls = M3uParser.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg1.ts\n#EXTINF:6.0,\nseg2.ts\n", "http://srv.test/live/index.m3u8");
            Assert.Single(hls);
            Assert.Equal("http://srv.test/live/index.m3u8", hls[0].Url);
            Assert.Empty(M3uParser.Parse(""));
            Assert.Empty(M3uParser.Parse("#EXTM3U\n"));
        }

        [Fact]
        public async Task LoadsFromFileAndFromUrl()
        {
            var text = "#EXTM3U\n#EXTINF:-1 group-title=\"G\",Uno\nacestream://c8c33b2673efe7f386790c5966643820578f3535\n";
            var path = Path.Combine(Path.GetTempPath(), "l-" + Guid.NewGuid().ToString("N") + ".m3u8");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            try
            {
                var f = await M3uParser.LoadAsync(path, null, CancellationToken.None);
                Assert.Equal("Uno", f.Single().Name);
                File.WriteAllBytes(path, Encoding.GetEncoding("iso-8859-1").GetBytes("#EXTM3U\n#EXTINF:-1,Niño\nacestream://c8c33b2673efe7f386790c5966643820578f3535\n"));
                Assert.Equal("Niño", (await M3uParser.LoadAsync(path, null, CancellationToken.None)).Single().Name);       // Latin-1
            }
            finally { File.Delete(path); }
            using (var srv = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(200, Encoding.UTF8.GetBytes(text))))
            {
                var u = await M3uParser.LoadAsync("http://127.0.0.1:" + srv.Port + "/lista.m3u", null, CancellationToken.None);
                Assert.Equal("G", u.Single().Group);
            }
            using (var bad = new FakeHttpServer(p => new KeyValuePair<int, byte[]>(404, new byte[0])))
                await Assert.ThrowsAsync<HttpRequestException>(() => M3uParser.LoadAsync("http://127.0.0.1:" + bad.Port + "/x.m3u", null, CancellationToken.None));
        }

        [Theory]
        [InlineData("acestream://c8c33b2673efe7f386790c5966643820578f3535", LinkKind.AceStream, "cid:c8c33b2673efe7f386790c5966643820578f3535")]
        [InlineData("ACESTREAM://C8C33B2673EFE7F386790C5966643820578F3535", LinkKind.AceStream, "cid:c8c33b2673efe7f386790c5966643820578f3535")]
        [InlineData("acestream:?infohash=c8c33b2673efe7f386790c5966643820578f3535", LinkKind.AceStream, "ih:c8c33b2673efe7f386790c5966643820578f3535")]
        [InlineData("c8c33b2673efe7f386790c5966643820578f3535", LinkKind.AceStream, "cid:c8c33b2673efe7f386790c5966643820578f3535")]
        [InlineData("magnet:?xt=urn:btih:c8c33b2673efe7f386790c5966643820578f3535", LinkKind.Magnet, "ih:c8c33b2673efe7f386790c5966643820578f3535")]
        [InlineData("http://a.test/live.m3u8?token=1", LinkKind.HttpStream, "http://a.test/live.m3u8?token=1")]
        [InlineData("https://a.test/video.TS", LinkKind.HttpStream, "https://a.test/video.TS")]
        [InlineData("https://agendas.test/agenda_futbol", LinkKind.Web, "https://agendas.test/agenda_futbol")]
        [InlineData("acestream://d4ff041287a43e3114d411d671c4b4e92e21f33y", LinkKind.Unsupported, "acestream://d4ff041287a43e3114d411d671c4b4e92e21f33y")]
        [InlineData("ftp://x/y", LinkKind.Unsupported, "ftp://x/y")]
        [InlineData("", LinkKind.Label, null)]
        public void ClassifiesUrls(string url, LinkKind kind, string key)
        {
            var e = LinkEntry.FromUrl("n", url);
            Assert.Equal(kind, e.Kind); Assert.Equal(key, e.Key);
            Assert.Equal(kind == LinkKind.AceStream || kind == LinkKind.Magnet || kind == LinkKind.HttpStream, e.IsProbeable);
        }

        [Fact]
        public void DedupeKeepsTheFirstNameAndLeavesLabelsAlone()
        {
            var l = new List<LinkEntry>
            {
                LinkEntry.FromUrl("cabecera", ""), LinkEntry.FromUrl("A", "acestream://c8c33b2673efe7f386790c5966643820578f3535"),
                LinkEntry.FromUrl("cabecera", ""), LinkEntry.FromUrl("B", "ACESTREAM://C8C33B2673EFE7F386790C5966643820578F3535"),
                LinkEntry.FromUrl("C", "acestream:?infohash=c8c33b2673efe7f386790c5966643820578f3535"), LinkEntry.FromUrl("web", "https://x.test/a"), LinkEntry.FromUrl("web", "https://x.test/a")
            };
            var d = LinkList.Dedupe(l);
            Assert.Equal(new[] { "cabecera", "A", "cabecera", "C", "web", "web" }, d.Select(x => x.Name).ToArray());       // content_id and infohash are different keys
        }
    }
}
