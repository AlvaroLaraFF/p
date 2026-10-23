using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    /// <summary>The diagnostic tool `ace search --ace --raw` (AceCentralSearchProvider.RawRequestAsync and SignedUrl): what it reports for each host.</summary>
    public class AceCentralDiagnosticsTests
    {
        private static AceCentralOptions Opts(int hostTimeoutMs = 3000)
        {
            return new AceCentralOptions { CacheTtl = TimeSpan.Zero, HostTimeout = TimeSpan.FromMilliseconds(hostTimeoutMs), TotalTimeout = TimeSpan.FromSeconds(10), Lang = "es", PreferredLanguages = "spa", PreferredCountries = "es" };
        }

        [Fact]
        public async Task ARawRequestReportsStatusSignatureAndTheDecodedJson()
        {
            using (var fake = new FakeAceCentral())
            {
                fake.Content = q => "{\"total\":1,\"time\":0.5,\"results\":[]}";
                var p = new AceCentralSearchProvider(null, Opts());
                var r = await p.RawRequestAsync(fake.Url, "liga", null, 0, 10, false, CancellationToken.None);
                Assert.Equal(200, r.HttpStatus);
                Assert.Null(r.Error); Assert.Null(r.ParseProblem);
                Assert.Equal("nonce", r.SignVariant);
                Assert.Equal("{\"total\":1,\"time\":0.5,\"results\":[]}", r.Json);
                Assert.Contains("a=liga", r.Url); Assert.Contains("_s=", r.Url);
                Assert.Equal(0, fake.BadSignatures);
                Assert.Equal("10", fake.Queries.Single()["i"]);
            }
        }

        [Fact]
        public async Task TheResponseSignatureVariantIsReported()
        {
            using (var fake = new FakeAceCentral { SignWithNonce = false })
            {
                var p = new AceCentralSearchProvider(null, Opts());
                var r = await p.RawRequestAsync(fake.Url, "x", null, 0, 5, false, CancellationToken.None);
                Assert.Equal("plain", r.SignVariant);
            }
        }

        [Fact]
        public async Task ABadResponseSignatureIsReportedButTheContentIsStillShownForDiagnosis()
        {
            using (var fake = new FakeAceCentral { CorruptSign = true })
            {
                var p = new AceCentralSearchProvider(null, Opts());
                var r = await p.RawRequestAsync(fake.Url, "x", null, 0, 5, true, CancellationToken.None);
                Assert.Equal(200, r.HttpStatus);
                Assert.Null(r.SignVariant);
                Assert.Contains("bad sign", r.ParseProblem);
                Assert.NotNull(r.Json);                                   // the diagnostic shows what arrived even though the app would discard it
            }
        }

        [Fact]
        public async Task AllVariantsFindsASignatureTheAppDoesNotAccept()
        {
            const string json = "{\"total\":0,\"time\":0,\"results\":[]}";
            string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var key = Encoding.ASCII.GetBytes("FEv5QHTKzoTZG5oWTXUpmetdjf4cfSYVs8sZDkbzBdupR6la7xaH3");
            string sign;
            using (var sha = SHA1.Create()) sign = Hex.ToHex(sha.ComputeHash(key.Concat(Encoding.ASCII.GetBytes(data)).ToArray()));      // key + data
            using (var server = new Tv2HttpServer(path => Tv2Response.Json("{\"data\":\"" + data + "\",\"sign\":\"" + sign + "\"}")))
            {
                var p = new AceCentralSearchProvider(null, Opts());
                Assert.Null((await p.RawRequestAsync(server.Url, "x", null, 0, 5, false, CancellationToken.None)).SignVariant);
                Assert.Equal("key+data", (await p.RawRequestAsync(server.Url, "x", null, 0, 5, true, CancellationToken.None)).SignVariant);
            }
        }

        [Fact]
        public async Task HttpErrorsGarbageAndSilenceAreAllReportedWithoutThrowing()
        {
            var p = new AceCentralSearchProvider(null, Opts(600));
            using (var s403 = new Tv2HttpServer(path => new Tv2Response { Status = 403, Body = Encoding.UTF8.GetBytes("forbidden") }))
            {
                var r = await p.RawRequestAsync(s403.Url, "x", null, 0, 5, false, CancellationToken.None);
                Assert.Equal(403, r.HttpStatus); Assert.Equal("forbidden", r.Body); Assert.Null(r.SignVariant);
            }
            using (var junk = new Tv2HttpServer(path => Tv2Response.Text("<html>hola</html>", "text/html")))
            {
                var r = await p.RawRequestAsync(junk.Url, "x", null, 0, 5, false, CancellationToken.None);
                Assert.Equal(200, r.HttpStatus); Assert.Equal("the body is not JSON", r.ParseProblem);
            }
            using (var slow = new Tv2HttpServer(path => new Tv2Response { DelayMs = 3000 }))
            {
                var r = await p.RawRequestAsync(slow.Url, "x", null, 0, 5, false, CancellationToken.None);
                Assert.Equal(0, r.HttpStatus); Assert.Contains("timed out", r.Error);
            }
            string dead;
            using (var gone = new Tv2HttpServer(path => new Tv2Response())) dead = gone.Url;
            var d = await p.RawRequestAsync(dead, "x", null, 0, 5, false, CancellationToken.None);
            Assert.Equal(0, d.HttpStatus); Assert.NotNull(d.Error);
            var bad = await p.RawRequestAsync("http://%zz", "x", null, 0, 5, false, CancellationToken.None);
            Assert.Equal(0, bad.HttpStatus); Assert.NotNull(bad.Error);
        }

        [Fact]
        public async Task TheSignedUrlIsAcceptedByTheServerWithoutBeingSentByTheProvider()
        {
            using (var fake = new FakeAceCentral())
            {
                var p = new AceCentralSearchProvider(null, Opts());
                string url = p.SignedUrl(fake.Url, "champions league", "sport", 2, 25);
                Assert.Equal(0, fake.Server.Count);                               // building the URL does not touch the network
                Assert.Contains("a=champions+league", url); Assert.Contains("c=sport", url); Assert.Contains("h=2", url); Assert.Contains("i=25", url);
                using (var http = new HttpClient())
                {
                    var body = await http.GetStringAsync(url);
                    Assert.Contains("\"data\"", body);
                }
                Assert.Equal(0, fake.BadSignatures);
                Assert.Equal("champions league", fake.Queries.Single()["a"]);
            }
        }
    }
}
