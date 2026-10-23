using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    public class HttpSearchProviderTests
    {
        private const string Ih = "0123456789abcdef0123456789abcdef01234567";
        private const string Cid = "fedcba9876543210fedcba9876543210fedcba98";

        private static HttpSearchProvider Make(string url, Action<HttpSearchOptions> tweak = null, string name = "Prueba")
        {
            var o = new HttpSearchOptions { CacheTtl = TimeSpan.Zero };
            if (tweak != null) tweak(o);
            return new HttpSearchProvider(new SearchProviderConfig { Name = name, Url = url }, o);
        }

        private static string Item(string name, string ih, string extra = "")
        {
            return "{\"name\":\"" + name + "\",\"infohash\":\"" + ih + "\",\"categories\":[\"general\"]" + extra + "}";
        }

        [Fact]
        public async Task ParsesAnM3uListWithEngineLinksAndDropsWhatItCannotPlay()
        {
            string m3u = "#EXTM3U\n"
                + "#EXTINF:-1 tvg-id=\"1\" tvg-name=\"BBC One\" tvg-language=\"eng\" group-title=\"general\",BBC One HD\nhttp://127.0.0.1:6878/ace/getstream?infohash=" + Ih + "\n"
                + "#EXTINF:-1 group-title=\"sport\",Eurosport\nacestream://" + Cid + "\n"
                + "#EXTINF:-1,Directo HLS\nhttp://tv.example/live/stream.m3u8\n"
                + "#EXTINF:-1,Malo 1\nfile:///etc/passwd\n#EXTINF:-1,Malo 2\njavascript:alert(1)\n#EXTINF:-1,Pagina\nhttp://example.org/agenda\n"
                + "#EXTINF:-1,Repetido\nhttp://127.0.0.1:6878/ace/getstream?infohash=" + Ih.ToUpperInvariant() + "\n";
            using (var srv = new Tv2HttpServer(p => Tv2Response.Text(m3u, "audio/x-mpegurl")))
            {
                var r = await Make(srv.Url + "/search/m3u?q={query}").SearchAsync("bbc", CancellationToken.None);
                Assert.Equal(3, r.Count);
                Assert.Equal("BBC One HD", r[0].Name); Assert.Equal("magnet:?xt=urn:btih:" + Ih, r[0].Url); Assert.Equal("general", r[0].Category);
                Assert.Equal("Eurosport", r[1].Name); Assert.Equal("acestream://" + Cid, r[1].Url); Assert.Equal("sport", r[1].Category);
                Assert.Equal("http://tv.example/live/stream.m3u8", r[2].Url);
                Assert.All(r, x => { Assert.Equal(-1, x.Peers); Assert.Equal(-1, x.Seeds); });
            }
        }

        [Fact]
        public async Task ParsesTheJsonSearchFormatInLongAndCompactNames()
        {
            string json = "{\"result\":{\"total\":4,\"time\":0.01,\"request_time\":0.1,\"results\":["
                + "{\"name\":\"BBC One\",\"items\":[" + Item("BBC One HD", Ih, ",\"bitrate\":4500000,\"availability\":0.9,\"status\":2,\"disabled\":false") + ","
                + Item("Apagado", Cid, ",\"disabled\":true") + ",{\"name\":\"Sin enlace\"},{\"name\":\"Mal infohash\",\"infohash\":\"zz\"}]},"
                + "{\"name\":\"Compacto\",\"items\":[{\"n\":\"Item compacto\",\"i\":\"" + Cid + "\",\"c\":[\"sport\"],\"a\":0.8,\"s\":1,\"d\":0},{\"n\":\"Off\",\"i\":\"" + Ih + "\",\"d\":1}]},"
                + "{\"name\":\"Solo url\",\"items\":[{\"n\":\"Flujo http\",\"u\":\"http://x.test/a.m3u8\"}]}]}}";
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json(json)))
            {
                var r = await Make(srv.Url + "/search").SearchAsync("bbc", CancellationToken.None);
                Assert.Equal(new[] { "BBC One HD", "Item compacto", "Flujo http" }, r.Select(x => x.Name).ToArray());
                Assert.Equal("magnet:?xt=urn:btih:" + Ih, r[0].Url);
                Assert.Equal("magnet:?xt=urn:btih:" + Cid, r[1].Url);
                Assert.Equal("sport", r[1].Category);
                Assert.Equal("http://x.test/a.m3u8", r[2].Url);
            }
        }

        [Fact]
        public async Task AcceptsAPlainJsonListAndRejectsAnErrorEnvelope()
        {
            using (var ok = new Tv2HttpServer(p => Tv2Response.Json("[" + Item("Uno", Ih) + ",3,null,{\"name\":\"Dos\",\"content_id\":\"" + Cid + "\",\"peers\":12,\"seeds\":3}]")))
            {
                var r = await Make(ok.Url + "/s?q={query}").SearchAsync("x", CancellationToken.None);
                Assert.Equal(2, r.Count);
                Assert.Equal("acestream://" + Cid, r[1].Url); Assert.Equal(12, r[1].Peers); Assert.Equal(3, r[1].Seeds);
            }
            using (var bad = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":null,\"error\":{\"code\":0,\"message\":\"search is not available\"}}")))
            {
                var prov = Make(bad.Url + "/s?q={query}");
                Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
                Assert.Contains("search is not available", prov.LastError);
            }
        }

        [Fact]
        public async Task TheQueryIsUrlEncodedAndCannotAlterTheRequest()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[]}}")))
            {
                var prov = Make(srv.Url + "/find?q={query}&x=1");
                await prov.SearchAsync("a&b=c#d é +%/\\\"", CancellationToken.None);
                var path = srv.Requests.Single();
                Assert.Equal("a&b=c#d é +%/\\\"", Tv2HttpServer.Param(path, "q"));
                Assert.Equal("1", Tv2HttpServer.Param(path, "x"));
                Assert.DoesNotContain("#", path);
                // controls and text-direction marks do not pass; the empty query does not go out to the network
                var before = srv.Count;
                Assert.Empty(await prov.SearchAsync("  \r\n\u202E ", CancellationToken.None));
                Assert.Equal(before, srv.Count);
                await prov.SearchAsync("ab\r\nHost: evil\u202Ecd", CancellationToken.None);
                Assert.Equal("ab Host: evil cd", Tv2HttpServer.Param(srv.Requests.Last(), "q"));
            }
        }

        [Fact]
        public async Task WeirdUnicodeInTheQueryAndInTheNamesNeverBreaksAnything()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[{\"name\":\"Ca\\ud800nal \\ud83d\\ude00 largo\",\"infohash\":\"" + Ih + "\"}]}}")))
            {
                var prov = Make(srv.Url + "/find?q={query}");
                var r = await prov.SearchAsync("\uD800abc\uDC00 \uD83D\uDE00 \u0000\u0007", CancellationToken.None);       // stray surrogates and controls
                Assert.Equal("abc \U0001F600", Tv2HttpServer.Param(srv.Requests.Single(), "q"));
                Assert.Single(r);
                Assert.Equal("Canal \U0001F600 largo", r[0].Name);                                                                // the stray surrogate in the name is discarded
                // a 200-character name right on a surrogate pair is not split
                Assert.Equal("", RemoteResultParser.Clean("\uD83D", 5));
                var s = RemoteResultParser.Clean(new string('a', 199) + "\U0001F600", 200);
                Assert.Equal(199, s.Length);
            }
        }

        [Fact]
        public async Task AUrlWithoutQueryIsABaseEndpointAndGetsTheStandardParameters()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[]}}")))
            {
                var prov = Make(srv.Url + "/search");
                await prov.SearchAsync("bbc news", CancellationToken.None);
                Assert.Equal("/search?query=bbc%20news&page=0&page_size=25", srv.Requests[0]);
                var withQs = Make(srv.Url + "/search?token=abc");
                await withQs.SearchAsync("x", CancellationToken.None);
                Assert.Equal("/search?token=abc&query=x&page=0&page_size=25", srv.Requests[1]);
                Assert.True(prov.SupportsCategory);
            }
        }

        [Fact]
        public async Task ExploreUsesTheCategoryWhenTheTemplateAllowsIt()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[" + Item("Canal", Ih) + "]}}")))
            {
                // base URL: category=sport, without 'query'
                var b = Make(srv.Url + "/search");
                var r = await b.ExploreAsync("futbol", CancellationToken.None);
                Assert.Single(r);
                Assert.Equal("/search?page=0&page_size=100&category=sport", srv.Requests[0]);
                // template with {category}
                var t = Make(srv.Url + "/cat/{category}?q={query}");
                Assert.Single(await t.ExploreAsync("noticias", CancellationToken.None));
                Assert.Equal("/cat/informational?q=", srv.Requests[1]);
                // template with {query} but without a category: nothing, and no request
                int n = srv.Count;
                var noCat = Make(srv.Url + "/s?q={query}");
                Assert.False(noCat.SupportsCategory);
                Assert.Empty(await noCat.ExploreAsync("cine", CancellationToken.None));
                // unknown type
                Assert.Empty(await b.ExploreAsync("nada", CancellationToken.None));
                Assert.Empty(await b.ExploreAsync(null, CancellationToken.None));
                Assert.Equal(n, srv.Count);
            }
        }

        [Theory]
        [InlineData("futbol", "sport")]
        [InlineData("baloncesto", "sport")]
        [InlineData("noticias", "informational")]
        [InlineData("cine", "movies")]
        [InlineData("series", "series")]
        [InlineData("documentales", "documentaries")]
        [InlineData("musica", "music")]
        [InlineData("infantil", "kids")]
        [InlineData("internacional", "regional")]
        [InlineData("FUTBOL", "sport")]
        [InlineData("nope", null)]
        public void ExploreKindsMapToTheAceCatalogCategories(string kind, string category)
        {
            Assert.Equal(category, ExploreCategories.For(kind));
        }

        [Fact]
        public async Task AnEnormousResponseIsRejectedWithoutBufferingIt()
        {
            using (var big = new Tv2HttpServer(p => new Tv2Response { Body = new byte[3 * 1024 * 1024] }))
            using (var endless = new Tv2HttpServer(p => new Tv2Response { EndlessMiB = 500 }))
            using (var lies = new Tv2HttpServer(p => new Tv2Response { Body = new byte[10], DeclaredLength = 50 * 1024 * 1024 }))
            {
                foreach (var srv in new[] { big, endless, lies })
                {
                    var prov = Make(srv.Url + "/s?q={query}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
                    Assert.Contains("too large", prov.LastError);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "tardo " + sw.Elapsed);
                }
            }
        }

        [Fact]
        public async Task AResponseWithManyResultsIsCappedAndLongNamesAreClipped()
        {
            var sb = new StringBuilder("{\"result\":{\"results\":[");
            for (int i = 0; i < 2000; i++) sb.Append(i > 0 ? "," : "").Append("{\"name\":\"").Append(new string('n', 500)).Append(i).Append("\",\"infohash\":\"").Append(i.ToString("x40")).Append("\"}");
            sb.Append("]}}");
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json(sb.ToString()) ))
            {
                var r = await Make(srv.Url + "/s?q={query}", o => o.MaxBytes = 4 * 1024 * 1024).SearchAsync("x", CancellationToken.None);
                Assert.Equal(200, r.Count);
                Assert.All(r, x => Assert.True(x.Name.Length <= 200));
            }
        }

        [Fact]
        public async Task ASlowServerOrASlowBodyTimesOut()
        {
            using (var slow = new Tv2HttpServer(p => new Tv2Response { Body = Encoding.UTF8.GetBytes("{\"result\":{\"results\":[]}}"), DelayMs = 3000 }))
            using (var drip = new Tv2HttpServer(p => new Tv2Response { Body = new byte[200000], BodyChunkDelayMs = 100 }))
            {
                foreach (var srv in new[] { slow, drip })
                {
                    var prov = Make(srv.Url + "/s?q={query}", o => o.Timeout = TimeSpan.FromMilliseconds(500));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
                    Assert.Contains("timed out", prov.LastError);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "tardo " + sw.Elapsed);
                }
            }
        }

        [Fact]
        public async Task CancellationIsNotAFailure()
        {
            using (var slow = new Tv2HttpServer(p => new Tv2Response { Body = new byte[2], DelayMs = 3000 }))
            {
                var prov = Make(slow.Url + "/s?q={query}");
                using (var cts = new CancellationTokenSource(200))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prov.SearchAsync("x", cts.Token));
            }
        }

        [Fact]
        public async Task RedirectsAreFollowedOnlyToHttpAndHttpsAndOnlyAFewTimes()
        {
            using (var target = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[" + Item("Destino", Ih) + "]}}")))
            using (var hop = new Tv2HttpServer(p => Tv2Response.Redirect(target.Url + "/final")))
            using (var toFile = new Tv2HttpServer(p => Tv2Response.Redirect("file:///etc/passwd")))
            using (var toFtp = new Tv2HttpServer(p => Tv2Response.Redirect("ftp://example.org/x")))
            using (var loop = new Tv2HttpServer(p => Tv2Response.Redirect("/again")))
            {
                var ok = Make(hop.Url + "/s?q={query}");
                var r = await ok.SearchAsync("x", CancellationToken.None);
                Assert.Single(r); Assert.Equal("Destino", r[0].Name);
                Assert.Equal("/final", target.Requests.Single());
                foreach (var srv in new[] { toFile, toFtp })
                {
                    var bad = Make(srv.Url + "/s?q={query}");
                    Assert.Empty(await bad.SearchAsync("x", CancellationToken.None));
                    Assert.Contains("disallowed scheme", bad.LastError);
                }
                var l = Make(loop.Url + "/s?q={query}");
                Assert.Empty(await l.SearchAsync("x", CancellationToken.None));
                Assert.Contains("too many redirects", l.LastError);
                Assert.True(loop.Count <= 5);
            }
        }

        [Fact]
        public async Task ServerErrorsAndGarbageGiveAnEmptyListNotAnException()
        {
            foreach (var resp in new[] { new Tv2Response { Status = 500 }, new Tv2Response { Status = 404, Body = Encoding.UTF8.GetBytes("no") }, Tv2Response.Text("<html>hola</html>", "text/html"),
                Tv2Response.Json("{\"result\":"), Tv2Response.Json("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[["),
                Tv2Response.Json("{\"result\":{\"results\":\"nope\"}}"), new Tv2Response() })
            {
                var r0 = resp;
                using (var srv = new Tv2HttpServer(p => r0))
                {
                    var prov = Make(srv.Url + "/s?q={query}");
                    Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
                    Assert.NotNull(prov.LastError);
                }
            }
            // without a server
            var dead = Make("http://127.0.0.1:9/s?q={query}");
            Assert.Empty(await dead.SearchAsync("x", CancellationToken.None));
            Assert.NotNull(dead.LastError);
        }

        [Fact]
        public async Task ServerErrorStatusIsReportedAndSuccessClearsIt()
        {
            int status = 500;
            using (var srv = new Tv2HttpServer(p => status == 200 ? Tv2Response.Json("{\"result\":{\"results\":[" + Item("A", Ih) + "]}}") : new Tv2Response { Status = status }))
            {
                var prov = Make(srv.Url + "/s?q={query}");
                Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
                Assert.Equal("HTTP 500", prov.LastError);
                status = 200;
                Assert.Single(await prov.SearchAsync("x", CancellationToken.None));
                Assert.Null(prov.LastError);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("ftp://example.org/{query}")]
        [InlineData("file:///etc/passwd?{query}")]
        [InlineData("javascript:alert({query})")]
        [InlineData("http://{query}.example.org/")]
        [InlineData("http://example.org:{query}/")]
        [InlineData("/relativa?q={query}")]
        [InlineData("no es una url {query}")]
        public async Task InvalidTemplatesNeverTouchTheNetworkAndNeverThrow(string url)
        {
            var prov = Make(url);
            Assert.False(prov.IsValid);
            Assert.Empty(await prov.SearchAsync("x", CancellationToken.None));
            Assert.Empty(await prov.ExploreAsync("futbol", CancellationToken.None));
            Assert.NotNull(prov.LastError);
        }

        [Fact]
        public void TheNameFallsBackToTheHostAndIsClean()
        {
            Assert.Equal("127.0.0.1", Make("http://127.0.0.1:1/s?q={query}", null, "  ").Name);
            Assert.Equal("Mi proveedor", Make("http://127.0.0.1:1/s?q={query}", null, " Mi\r\nproveedor ").Name);
        }

        [Fact]
        public async Task ResultsAreCachedBrieflyPerQuery()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[" + Item("A", Ih) + "]}}")))
            {
                var prov = new HttpSearchProvider(new SearchProviderConfig { Name = "c", Url = srv.Url + "/s?q={query}" });
                var a = await prov.SearchAsync("Bbc", CancellationToken.None);
                a[0].Name = "mutado";                                                            // whoever receives it can change it without affecting the cache
                var b = await prov.SearchAsync("bbc", CancellationToken.None);
                Assert.Equal("A", b[0].Name);
                Assert.Equal(1, srv.Count);
                await prov.SearchAsync("otra", CancellationToken.None);
                Assert.Equal(2, srv.Count);
            }
        }

        [Fact]
        public async Task GzipResponsesAreDecodedWithTheSameLimit()
        {
            using (var srv = new Tv2HttpServer(p => new Tv2Response { Body = Encoding.UTF8.GetBytes("{\"result\":{\"results\":[" + Item("Z", Ih) + "]}}"), Gzip = true }))
            using (var bomb = new Tv2HttpServer(p => new Tv2Response { Body = new byte[20 * 1024 * 1024], Gzip = true }))
            {
                Assert.Single(await Make(srv.Url + "/s?q={query}").SearchAsync("x", CancellationToken.None));
                var b = Make(bomb.Url + "/s?q={query}");
                Assert.Empty(await b.SearchAsync("x", CancellationToken.None));
                Assert.Contains("too large", b.LastError);
            }
        }
    }

    // ---------- Ace central search engine against a fake server that checks the signature ----------

    internal sealed class FakeAceCentral : IDisposable
    {
        private static readonly byte[] ReqKey = Encoding.ASCII.GetBytes("xXzhTdAd1pOjL1nDAVI9TXlIBvpy3DVe5cwMao0oBivSrxLysaGwSGEI");
        private static readonly byte[] RespKey = Encoding.ASCII.GetBytes("FEv5QHTKzoTZG5oWTXUpmetdjf4cfSYVs8sZDkbzBdupR6la7xaH3");
        public readonly Tv2HttpServer Server;
        public readonly List<IDictionary<string, string>> Queries = new List<IDictionary<string, string>>();
        public int BadSignatures;
        /// <summary>Content (already decoded JSON) that answers a query with its parameters.</summary>
        public Func<IDictionary<string, string>, string> Content = q => "{\"total\":0,\"time\":0.01,\"results\":[]}";
        public bool SignWithNonce = true, CorruptSign;

        public FakeAceCentral()
        {
            Server = new Tv2HttpServer(Handle);
        }

        public string Url { get { return Server.Url; } }

        private Tv2Response Handle(string path)
        {
            int q = path.IndexOf('?');
            if (!path.StartsWith("/a?", StringComparison.Ordinal)) return new Tv2Response { Status = 404 };
            var parms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in path.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                parms[part.Substring(0, eq)] = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            // INDEPENDENT check of the signature: sha1("k=v#k=v..." + KEY) with the keys sorted in ordinal order and without _s
            string sig = parms["_s"];
            var text = string.Join("#", parms.Where(kv => kv.Key != "_s").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            using (var sha = SHA1.Create())
            {
                var expected = Hex.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text).Concat(ReqKey).ToArray()));
                if (expected != sig) { Interlocked.Increment(ref BadSignatures); return new Tv2Response { Status = 403, Body = Encoding.UTF8.GetBytes("bad") }; }
                lock (Queries) Queries.Add(parms);
                string content = Content(parms);
                string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                var tail = SignWithNonce ? Encoding.ASCII.GetBytes(parms["_r"]) : new byte[0];
                string sign = Hex.ToHex(sha.ComputeHash(Encoding.ASCII.GetBytes(data).Concat(tail).Concat(RespKey).ToArray()));
                if (CorruptSign) sign = new string('0', 40);
                return Tv2Response.Json("{\"data\":\"" + data + "\",\"sign\":\"" + sign + "\"}");
            }
        }

        public void Dispose() { Server.Dispose(); }
    }

    public class AceCentralSearchProviderTests
    {
        private const string Ih = "0123456789abcdef0123456789abcdef01234567";
        private const string Cid = "fedcba9876543210fedcba9876543210fedcba98";

        private static AceCentralOptions Opts(params string[] hosts)
        {
            return new AceCentralOptions { Hosts = hosts, CacheTtl = TimeSpan.Zero, HostTimeout = TimeSpan.FromSeconds(3), TotalTimeout = TimeSpan.FromSeconds(10), Lang = "es", PreferredLanguages = "spa", PreferredCountries = "es" };
        }

        private static string Grouped(params string[] items)
        {
            return "{\"total\":" + items.Length + ",\"time\":0.01,\"results\":[{\"name\":\"Canal\",\"items\":[" + string.Join(",", items) + "],\"icons\":[{\"type\":0,\"url\":\"http://x/y.png\"}],\"epg\":[{\"s\":1,\"e\":2,\"n\":\"Programa\"}]}]}";
        }

        [Fact]
        public void TheRequestSignatureMatchesTheReferenceComputedInPython()
        {
            // Vectors computed separately with Python (hashlib + urllib.parse.quote_plus)
            var p1 = new Dictionary<string, string> { { "a", "bbc news ñ&=+/" }, { "h", "0" }, { "i", "25" }, { "m", "4" } };
            Assert.Equal("/a?_n=3.2.22&_p=android&_r=123456789&_t=1792177534&_v=3022200&a=bbc+news+%C3%B1%26%3D%2B%2F&h=0&i=25&m=4&_s=36a98a14f9bf27c61dd695e4ca46b1f3c03c317f",
                AceCentralSearchProvider.BuildPath("a", p1, "123456789", 1792177534));
            var p2 = new Dictionary<string, string> { { "c", "sport" }, { "h", "1" }, { "i", "100" }, { "m", "4" }, { "j", "es" }, { "s", "spa" }, { "t", "es" } };
            Assert.Equal("/a?_n=3.2.22&_p=android&_r=9223372036854775807&_t=1792177999&_v=3022200&c=sport&h=1&i=100&j=es&m=4&s=spa&t=es&_s=173cbf09275db39b3758ddd511fccf7002fd9292",
                AceCentralSearchProvider.BuildPath("a", p2, "9223372036854775807", 1792177999));
            Assert.Throws<ArgumentException>(() => AceCentralSearchProvider.BuildPath("a", new Dictionary<string, string> { { "_r", "1" } }, "1", 1));
            Assert.Throws<ArgumentException>(() => AceCentralSearchProvider.BuildPath("a", new Dictionary<string, string> { { "_s", "x" } }, "1", 1));
        }

        [Fact]
        public void TheResponseSignatureMatchesTheReferenceComputedInPython()
        {
            // data = base64('{"total": 0}'); the signatures sha1(data + nonce + RESPONSE_KEY) and sha1(data + RESPONSE_KEY) come from Python
            const string data = "eyJ0b3RhbCI6IDB9";
            string withNonce = "{\"data\":\"" + data + "\",\"sign\":\"f597f44c17625e038a24556f1fa0a648a789cd6c\"}";
            string noNonce = "{\"data\":\"" + data + "\",\"sign\":\"e5d93885ae6e88f6bf8be10a2eb44559959513b6\"}";
            string zeros = "{\"data\":\"" + data + "\",\"sign\":\"" + new string('0', 40) + "\"}";
            // with the correct signature the content gets read, and it has neither time nor results: it is rejected BY ITS CONTENT
            Assert.Contains("missing required fields", Reject(withNonce, "123456789"));
            Assert.Contains("missing required fields", Reject(noNonce, "123456789"));
            // with another nonce the signature with nonce no longer matches (and neither does the other variant): it is rejected by the signature
            Assert.Contains("signature", Reject(withNonce, "999"));
            Assert.Contains("signature", Reject(zeros, "123456789"));
        }

        private static string Reject(string text, string nonce)
        {
            return Assert.Throws<RemoteFetchException>(() => { int g; long t; AceCentralSearchProvider.ParseResponse(text, nonce, 1 << 20, 100, out g, out t); }).Message;
        }

        [Fact]
        public async Task IsOffAndSilentWhenTheSettingIsOff()
        {
            using (var fake = new FakeAceCentral())
            {
                bool on = false;
                var p = new AceCentralSearchProvider(() => on, Opts(fake.Url));
                Assert.False(p.IsEnabled);
                Assert.Empty(await p.SearchAsync("bbc", CancellationToken.None));
                Assert.Empty(await p.ExploreAsync("futbol", CancellationToken.None));
                Assert.Equal(0, fake.Server.Count);                                   // not a single request with the setting off
                Assert.True(new UserSettings().AceSearchEnabled);                     // on by default (user's decision, 2026-10-17)
                fake.Content = q => Grouped("{\"n\":\"BBC One HD\",\"i\":\"" + Ih + "\",\"c\":[\"general\"]}");
                on = true;                                                            // it is queried on every call: turning it on takes effect immediately
                Assert.Single(await p.SearchAsync("bbc", CancellationToken.None));
                Assert.Equal(1, fake.Server.Count);
                on = false;
                Assert.Empty(await p.SearchAsync("otra", CancellationToken.None));
                Assert.Equal(1, fake.Server.Count);
                Assert.Contains("claves", AceCentralSearchProvider.Notice);
            }
        }

        [Fact]
        public async Task SearchSendsTheSignedCompactRequestAndMapsTheCompactItems()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q => Grouped(
                    "{\"n\":\"BBC One HD\",\"i\":\"" + Ih + "\",\"b\":4500000,\"c\":[\"general\",\"x\"],\"a\":0.92,\"au\":1700000100,\"ci\":123,\"s\":2,\"d\":0,\"languages\":[\"eng\"],\"countries\":[\"gb\"]}",
                    "{\"n\":\"Apagado\",\"i\":\"" + Cid + "\",\"d\":1}",
                    "{\"n\":\"Solo url\",\"u\":\"http://x.test/live.m3u8\"}",
                    "{\"n\":\"Sin nada\"}",
                    "{\"i\":\"" + Cid + "\"}");
                var p = new AceCentralSearchProvider(null, Opts(fake.Url));
                var r = await p.SearchAsync("bbc news", CancellationToken.None);
                Assert.Equal(new[] { "BBC One HD", "Solo url", "Canal" }, r.Select(x => x.Name).ToArray());           // the item without a name takes the group's; the disabled one and the one without a link are not listed
                Assert.Equal("magnet:?xt=urn:btih:" + Ih, r[0].Url); Assert.Equal("general", r[0].Category);
                Assert.Equal("http://x.test/live.m3u8", r[1].Url);
                Assert.Equal("magnet:?xt=urn:btih:" + Cid, r[2].Url);
                Assert.Null(p.LastError);
                Assert.Equal(0, fake.BadSignatures);
                var q = fake.Queries.Single();
                Assert.Equal("bbc news", q["a"]); Assert.Equal("0", q["h"]); Assert.Equal("50", q["i"]); Assert.Equal("4", q["m"]);
                Assert.Equal("es", q["j"]); Assert.Equal("spa", q["s"]); Assert.Equal("es", q["t"]);
                Assert.Equal("3022200", q["_v"]); Assert.Equal("3.2.22", q["_n"]); Assert.Equal("android", q["_p"]);
                Assert.True(long.Parse(q["_r"]) > 0); Assert.True(long.Parse(q["_t"]) > 1700000000);
                Assert.False(q.ContainsKey("_a") || q.ContainsKey("_b") || q.ContainsKey("_c") || q.ContainsKey("c"));
            }
        }

        [Fact]
        public async Task ExploreAsksForTheAceCategoryAndPagesWhilePagesAreFull()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q =>
                {
                    int page = int.Parse(q["h"]);
                    var sb = new StringBuilder("{\"total\":150,\"time\":0.1,\"results\":[");
                    int n = page == 0 ? 100 : 50;
                    for (int i = 0; i < n; i++)
                        sb.Append(i > 0 ? "," : "").Append("{\"name\":\"C").Append(page).Append('_').Append(i).Append("\",\"items\":[{\"n\":\"Canal ").Append(page).Append('_').Append(i).Append("\",\"i\":\"").Append((page * 1000 + i).ToString("x40")).Append("\",\"c\":[\"sport\"]}]}");
                    return sb.Append("]}").ToString();
                };
                var p = new AceCentralSearchProvider(null, Opts(fake.Url));
                var r = await p.ExploreAsync("futbol", CancellationToken.None);
                Assert.Equal(150, r.Count);
                Assert.Equal(2, fake.Queries.Count);
                Assert.All(fake.Queries, q => { Assert.Equal("sport", q["c"]); Assert.False(q.ContainsKey("a")); Assert.Equal("100", q["i"]); });
                Assert.Equal(new[] { "0", "1" }, fake.Queries.Select(q => q["h"]).ToArray());
                Assert.Empty(await p.ExploreAsync("no existe", CancellationToken.None));
            }
        }

        [Fact]
        public async Task ExploreResultsAreCachedSoOneCategoryServesSeveralKinds()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q => Grouped("{\"n\":\"Canal\",\"i\":\"" + Ih + "\"}");
                var o = Opts(fake.Url); o.CacheTtl = TimeSpan.FromSeconds(60);
                var p = new AceCentralSearchProvider(null, o);
                await p.ExploreAsync("futbol", CancellationToken.None);
                await p.ExploreAsync("baloncesto", CancellationToken.None);
                await p.ExploreAsync("tenis", CancellationToken.None);
                Assert.Equal(1, fake.Server.Count);
                await p.ExploreAsync("cine", CancellationToken.None);
                Assert.Equal(2, fake.Server.Count);
            }
        }

        [Fact]
        public async Task AResponseWithoutAValidSignatureIsDiscardedAndTheNextHostIsTried()
        {
            using (var bad = new FakeAceCentral { CorruptSign = true })
            using (var good = new FakeAceCentral())
            {
                bad.Content = q => Grouped("{\"n\":\"Falso\",\"i\":\"" + Cid + "\"}");
                good.Content = q => Grouped("{\"n\":\"Bueno\",\"i\":\"" + Ih + "\"}");
                for (int round = 0; round < 6; round++)                                  // the hosts are shuffled: the order does not matter
                {
                    var p = new AceCentralSearchProvider(null, Opts(bad.Url, "http://127.0.0.1:9", good.Url));
                    var r = await p.SearchAsync("x", CancellationToken.None);
                    Assert.Equal("Bueno", r.Single().Name);
                }
                // only the bad one: nothing, and the reason is the signature
                var only = new AceCentralSearchProvider(null, Opts(bad.Url));
                Assert.Empty(await only.SearchAsync("x", CancellationToken.None));
                Assert.Contains("signature", only.LastError);
            }
        }

        [Fact]
        public async Task AConnectionCutInTheMiddleOfTheBodyFallsThroughToTheNextHost()
        {
            using (var cut = new Tv2HttpServer(p => new Tv2Response { Body = new byte[10], DeclaredLength = 5000 }))
            using (var good = new FakeAceCentral())
            {
                good.Content = q => Grouped("{\"n\":\"Bueno\",\"i\":\"" + Ih + "\"}");
                for (int round = 0; round < 4; round++)
                {
                    var p = new AceCentralSearchProvider(null, Opts(cut.Url, good.Url));
                    Assert.Equal("Bueno", (await p.SearchAsync("x", CancellationToken.None)).Single().Name);
                }
            }
        }

        [Fact]
        public async Task WhenNoHostAnswersTheBreakerSkipsTheNetworkForTheCooldown()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q => Grouped("{\"n\":\"Canal\",\"i\":\"" + Ih + "\"}");
                var clock = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
                var o = Opts("http://127.0.0.1:9");                                     // closed port: no connection, as with the blocked IP
                o.UtcNow = () => clock;
                var p = new AceCentralSearchProvider(null, o);
                Assert.Empty(await p.SearchAsync("liga", CancellationToken.None));
                Assert.Contains("pause", p.LastError);
                o.Hosts = new[] { fake.Url };                                           // even if the server comes back, no one is asked during the pause
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Assert.Empty(await p.SearchAsync("liga", CancellationToken.None));
                Assert.Empty(await p.ExploreAsync("futbol", CancellationToken.None));
                Assert.True(sw.ElapsedMilliseconds < 500, "with the circuit breaker open the search must not wait");
                Assert.Equal(0, fake.Server.Count);
                Assert.Contains("retry", p.LastError);
                clock = clock.AddSeconds(301);                                          // once the pause is over, it tries again
                Assert.Single(await p.SearchAsync("liga", CancellationToken.None));
                Assert.Equal(1, fake.Server.Count);
                Assert.Null(p.LastError);
            }
        }

        [Fact]
        public async Task AHostThatAnswersBadlyDoesNotTripTheBreaker()
        {
            using (var bad = new FakeAceCentral { CorruptSign = true })
            using (var down = new Tv2HttpServer(r => new Tv2Response { Status = 503, Body = new byte[0] }))
            using (var good = new FakeAceCentral())
            {
                bad.Content = q => Grouped("{\"n\":\"Falso\",\"i\":\"" + Cid + "\"}");
                good.Content = q => Grouped("{\"n\":\"Bueno\",\"i\":\"" + Ih + "\"}");
                var o = Opts(bad.Url, down.Url);                                        // there is network: a bad signature and HTTP 503 are not a block
                var p = new AceCentralSearchProvider(null, o);
                Assert.Empty(await p.SearchAsync("x", CancellationToken.None));
                Assert.DoesNotContain("pause", p.LastError);
                o.Hosts = new[] { good.Url };
                Assert.Equal("Bueno", (await p.SearchAsync("x", CancellationToken.None)).Single().Name);
                o.Hosts = new[] { "http://127.0.0.1:9" };
                o.BreakerCooldown = TimeSpan.Zero;                                      // Zero: no circuit breaker
                Assert.Empty(await p.SearchAsync("otra", CancellationToken.None));
                o.Hosts = new[] { good.Url };
                Assert.Single(await p.SearchAsync("otra", CancellationToken.None));
            }
        }

        [Fact]
        public async Task ResponsesSignedWithoutTheNonceAreAcceptedToo()
        {
            using (var fake = new FakeAceCentral { SignWithNonce = false })
            {
                fake.Content = q => Grouped("{\"n\":\"Canal\",\"i\":\"" + Ih + "\"}");
                Assert.Single(await new AceCentralSearchProvider(null, Opts(fake.Url)).SearchAsync("x", CancellationToken.None));
            }
        }

        [Fact]
        public async Task BadResponsesAreRejectedWithoutThrowing()
        {
            foreach (var content in new[] { "{\"total\":1,\"time\":1}", "[]", "{\"total\":1,\"time\":1,\"results\":5}", "no es json", "" })
            {
                using (var fake = new FakeAceCentral())
                {
                    var c = content;
                    fake.Content = q => c;
                    var p = new AceCentralSearchProvider(null, Opts(fake.Url));
                    Assert.Empty(await p.SearchAsync("x", CancellationToken.None));
                    Assert.NotNull(p.LastError);
                }
            }
            using (var junk = new Tv2HttpServer(p => Tv2Response.Json("{\"data\":5,\"sign\":null}")))
            using (var empty = new Tv2HttpServer(p => new Tv2Response()))
            using (var err = new Tv2HttpServer(p => new Tv2Response { Status = 500 }))
            using (var huge = new Tv2HttpServer(p => new Tv2Response { Body = new byte[3 * 1024 * 1024] }))
                foreach (var srv in new[] { junk, empty, err, huge })
                {
                    var p = new AceCentralSearchProvider(null, Opts(srv.Url));
                    Assert.Empty(await p.SearchAsync("x", CancellationToken.None));
                    Assert.NotNull(p.LastError);
                }
        }

        [Fact]
        public async Task ADecodedResponseThatIsTooBigIsRejected()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q => "{\"total\":1,\"time\":1,\"results\":[],\"pad\":\"" + new string('x', 600 * 1024) + "\"}";
                var o = Opts(fake.Url); o.MaxDecodedBytes = 100 * 1024; o.MaxBytes = 4 * 1024 * 1024;
                var p = new AceCentralSearchProvider(null, o);
                Assert.Empty(await p.SearchAsync("x", CancellationToken.None));
                Assert.Contains("too large", p.LastError);
            }
        }
    }

    // ---------- The provider factory reads the settings on every call ----------

    public class SearchProviderFactoryTests
    {
        private static AppConfig Cfg(params string[] urls)
        {
            var c = new AppConfig();
            for (int i = 0; i < urls.Length; i++) c.SearchProviders.Add(new SearchProviderConfig { Name = "P" + i, Url = urls[i] });
            return c;
        }

        [Fact]
        public void TheCentralSearchIsInTheListOnlyWhileTheSettingIsOn()
        {
            var s = new UserSettings { AceSearchEnabled = false };
            var f = new SearchProviderFactory(Cfg("http://a.test/s?q={query}"), s);
            var l1 = f.GetProviders();
            Assert.Single(l1); Assert.IsType<HttpSearchProvider>(l1[0]);
            s.AceSearchEnabled = true; s.NotifyChanged();
            var l2 = f.GetProviders();
            Assert.Equal(2, l2.Count); Assert.IsType<AceCentralSearchProvider>(l2[1]); Assert.Equal("Ace", l2[1].Name);
            Assert.Same(l2[1], f.GetProviders()[1]);                                   // the same instance (it keeps its cache)
            var ace = (AceCentralSearchProvider)l2[1];
            Assert.True(ace.IsEnabled);
            s.AceSearchEnabled = false;
            Assert.False(ace.IsEnabled);                                               // an old reference finds out too
            Assert.Single(f.GetProviders());
        }

        [Fact]
        public void ConfiguredProvidersAreReusedDroppedAndBounded()
        {
            var c = Cfg("http://a.test/s?q={query}", "http://b.test/s", "ftp://mal/{query}", "http://a.test/s?q={query}");
            var f = new SearchProviderFactory(c, new UserSettings { AceSearchEnabled = false });
            var l1 = f.GetProviders();
            Assert.Equal(3, l1.Count);                                                 // the invalid one is not listed; "P0" and "P3" have different names and both are listed
            Assert.Same(l1[0], f.GetProviders()[0]);
            c.SearchProviders.RemoveAt(0);
            var l2 = f.GetProviders();
            Assert.Equal(2, l2.Count);
            Assert.DoesNotContain(l1[0], l2);
            var many = new AppConfig();
            for (int i = 0; i < 20; i++) many.SearchProviders.Add(new SearchProviderConfig { Name = "n" + i, Url = "http://h" + i + ".test/s?q={query}" });
            Assert.Equal(AppConfig.MaxProviders, new SearchProviderFactory(many, new UserSettings { AceSearchEnabled = false }).GetProviders().Count);
        }

        [Fact]
        public async Task ProvidersFromTheFactoryWorkEndToEndAgainstLoopbackServers()
        {
            using (var srv = new Tv2HttpServer(p => Tv2Response.Json("{\"result\":{\"results\":[{\"name\":\"Canal\",\"infohash\":\"0123456789abcdef0123456789abcdef01234567\"}]}}")))
            {
                var s = new UserSettings { AceSearchEnabled = false };
                var f = new SearchProviderFactory(Cfg(srv.Url + "/s?q={query}"), s);
                var all = new List<RemoteResult>();
                foreach (var p in f.GetProviders()) all.AddRange(await p.SearchAsync("canal", CancellationToken.None));
                Assert.Single(all);
            }
        }
    }
}
