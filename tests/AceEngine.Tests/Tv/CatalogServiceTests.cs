using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Sources;
using AceEngine.Tests.Sources;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv
{
    public class CatalogServiceTests : IDisposable
    {
        private readonly string _dir = TvTempDir.Create();
        private readonly List<CatalogService> _services = new List<CatalogService>();

        public void Dispose()
        {
            foreach (var s in _services) s.Dispose();
            TvTempDir.Delete(_dir);
        }

        private static AppConfig Config(params string[] codes)
        {
            var c = new AppConfig();
            foreach (var code in codes) c.Lists.Add(new ListSource(code, "lista " + code));
            return c;
        }

        private CatalogService Make(AppConfig cfg, IListLoader loader, bool disk = true)
        {
            var s = new CatalogService(cfg, loader, disk ? _dir : null) { ListTimeout = TimeSpan.FromSeconds(5) };
            _services.Add(s);
            return s;
        }

        private static List<LinkEntry> L(params LinkEntry[] e) { return e.ToList(); }
        private static LinkEntry Ace(int n, string name, string group) { return CatalogBuilderTests.Ace(n, name, group); }
        private string CachePath { get { return Path.Combine(_dir, "catalog.json"); } }

        private static void Done(CatalogService s) { Assert.True(s.LoadTask.Wait(10000), "the load did not finish"); }

        [Fact]
        public void LoadsAllListsAndMergesThemInConfigurationOrder()
        {
            var loader = new TvFakeListLoader();
            // the second list answers before the first: the catalog's order is the configuration's, not the arrival order
            var slow = new TaskCompletionSource<List<LinkEntry>>();
            loader.Script["aaa"] = t => slow.Task;
            loader.Ok("bbb", L(Ace(3, "Canal C", "NEW ERA - DAZN"), Ace(4, "Canal D", "LIGA")));
            var svc = Make(Config("aaa", "bbb"), loader);
            int changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            Assert.Equal(CatalogState.Loading, svc.State);
            Assert.Empty(svc.Channels);
            svc.Start();
            TvWait.Until(() => loader.CallCount == 2, "both lists are requested at once");
            Assert.Equal(CatalogState.Loading, svc.State);                         // one is still missing
            slow.SetResult(L(Ace(1, "Canal A", "DAZN"), Ace(2, "Canal B", "FUTBOL")));
            Done(svc);

            Assert.Equal(CatalogState.Ready, svc.State);
            Assert.Equal("", svc.Error);
            Assert.Equal(new[] { "Canal A", "Canal B", "Canal C", "Canal D" }, svc.Channels.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "DAZN", "Futbol", "Liga" }, svc.Categories.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "Canal A", "Canal C" }, svc.Categories[0].Channels.Select(c => c.Name).ToArray());
            Assert.Equal("Canal B", svc.Find("cid:" + CatalogBuilderTests.Cid(2)).Name);
            Assert.Null(svc.Find("cid:nada"));
            Assert.Equal(1, changed);
            Assert.True(File.Exists(CachePath));
        }

        [Fact]
        public void StartIsIdempotent()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa"), loader);
            svc.Start(); svc.Start(); svc.Start();
            Done(svc);
            svc.Start();
            Done(svc);
            Assert.Equal(1, loader.CallCount);
            Assert.Equal(CatalogState.Ready, svc.State);
        }

        [Fact]
        public void ADeadListDoesNotHideTheOthers()
        {
            var loader = new TvFakeListLoader();
            loader.Fail("aaa", "timed out");
            loader.Ok("bbb", L(Ace(1, "Canal B", "X")));
            var svc = Make(Config("aaa", "bbb"), loader);
            svc.Start();
            Done(svc);
            Assert.Equal(CatalogState.Ready, svc.State);
            Assert.Single(svc.Channels);
            Assert.Contains("1 de 2", svc.Error);
            Assert.DoesNotContain("aaa", svc.Error);                              // the error does not reveal codes or list names
            Assert.DoesNotContain("lista", svc.Error.Replace("listas", ""));
        }

        [Fact]
        public void BothDeadWithoutACopyIsFailedAndStartRetries()
        {
            var loader = new TvFakeListLoader();
            loader.Fail("aaa"); loader.Fail("bbb");
            var svc = Make(Config("aaa", "bbb"), loader);
            int changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            svc.Start();
            Done(svc);
            Assert.Equal(CatalogState.Failed, svc.State);
            Assert.Equal(CatalogService.ErrorAllFailed, svc.Error);
            Assert.Empty(svc.Channels);
            Assert.Empty(svc.Categories);
            Assert.Equal(1, changed);
            Assert.False(File.Exists(CachePath));                                 // nothing is saved if nothing was loaded

            // when the network comes back, Start retries (the UI calls it from "Reintentar")
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            svc.Start();
            Done(svc);
            Assert.Equal(CatalogState.Ready, svc.State);
            Assert.Single(svc.Channels);
            Assert.Contains("1 de 2", svc.Error);
            Assert.True(changed >= 3);                                            // Loading, and then Ready
        }

        [Fact]
        public void BothDeadWithACopyShowsTheCopyAndSaysSo()
        {
            var first = new TvFakeListLoader();
            first.Ok("aaa", L(Ace(1, "Canal A", "X"), Ace(2, "Canal B", "X")));
            first.Ok("bbb", L(Ace(3, "Canal C", "Y")));
            var s1 = Make(Config("aaa", "bbb"), first);
            s1.Start(); Done(s1);
            Assert.Equal(3, s1.Channels.Count);
            s1.Dispose();

            var dead = new TvFakeListLoader();
            dead.Fail("aaa"); dead.Fail("bbb");
            var s2 = Make(Config("aaa", "bbb"), dead);
            s2.Start(); Done(s2);
            Assert.Equal(CatalogState.Ready, s2.State);
            Assert.Equal(new[] { "Canal A", "Canal B", "Canal C" }, s2.Channels.Select(c => c.Name).ToArray());
            Assert.Contains("copia", s2.Error);
            Assert.Contains("2 de 2", s2.Error);
        }

        [Fact]
        public void TheCopyOnDiskMakesItReadyRightAwayAndTheRefreshChangesTheContent()
        {
            var first = new TvFakeListLoader();
            first.Ok("aaa", L(Ace(1, "Canal A", "X"), Ace(2, "Canal B", "X")));
            var s1 = Make(Config("aaa"), first);
            s1.Start(); Done(s1);
            s1.Dispose();

            var gate = new TaskCompletionSource<List<LinkEntry>>();
            var second = new TvFakeListLoader();
            second.Script["aaa"] = t => gate.Task;
            var s2 = Make(Config("aaa"), second);
            var states = new List<string>();
            s2.Changed += () => { lock (states) states.Add(s2.State + ":" + s2.Channels.Count); };
            s2.Start();
            TvWait.Until(() => s2.State == CatalogState.Ready, "the copy is shown without waiting for the network");
            Assert.Equal(new[] { "Canal A", "Canal B" }, s2.Channels.Select(c => c.Name).ToArray());
            Assert.Equal("", s2.Error);
            Assert.False(s2.LoadTask.IsCompleted);                                 // it keeps refreshing in the background

            gate.SetResult(L(Ace(2, "Canal B", "X"), Ace(3, "Canal Nuevo", "Z")));
            Done(s2);
            Assert.Equal(new[] { "Canal B", "Canal Nuevo" }, s2.Channels.Select(c => c.Name).ToArray());
            Assert.Equal(new[] { "X", "Z" }, s2.Categories.Select(c => c.Name).ToArray());
            lock (states) Assert.Equal(new[] { "Ready:2", "Ready:2" }, states.ToArray());      // copy and then refresh (other content)

            // and the copy was updated
            var gate3 = new TvFakeListLoader();
            gate3.Fail("aaa");
            var s3 = Make(Config("aaa"), gate3);
            s3.Start(); Done(s3);
            Assert.Equal(new[] { "Canal B", "Canal Nuevo" }, s3.Channels.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void ARefreshWithTheSameContentDoesNotFireChanged()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa"), loader);
            int changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            svc.Start(); Done(svc);
            Assert.Equal(1, changed);
            var channels = svc.Channels;
            svc.Refresh(); Done(svc);
            svc.Refresh(); Done(svc);
            Assert.Equal(1, changed);
            Assert.Equal(3, loader.CallCount);
            Assert.Same(channels, svc.Channels);                                   // the same snapshot continues
        }

        [Fact]
        public void ARefreshWithNewContentFiresChangedAndLeavesTheOldSnapshotUntouched()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa"), loader);
            int changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            svc.Start(); Done(svc);
            var oldChannels = svc.Channels;
            var oldCategories = svc.Categories;
            loader.Ok("aaa", L(Ace(1, "Canal A", "X"), Ace(2, "Canal B", "X")));
            svc.Refresh(); Done(svc);
            Assert.Equal(2, changed);
            Assert.Equal(2, svc.Channels.Count);
            Assert.Single(oldChannels);                                            // whoever read before still sees the same thing
            Assert.Single(oldCategories[0].Channels);
        }

        [Fact]
        public void AListThatFailsKeepsItsLastCopyWhileTheOthersRefresh()
        {
            var first = new TvFakeListLoader();
            first.Ok("aaa", L(Ace(1, "Canal A", "X")));
            first.Ok("bbb", L(Ace(2, "Canal B", "Y")));
            var s1 = Make(Config("aaa", "bbb"), first);
            s1.Start(); Done(s1);
            s1.Dispose();

            var second = new TvFakeListLoader();
            second.Fail("aaa");
            second.Ok("bbb", L(Ace(2, "Canal B", "Y"), Ace(3, "Canal C", "Y")));
            var s2 = Make(Config("aaa", "bbb"), second);
            s2.Start(); Done(s2);
            Assert.Equal(new[] { "Canal A", "Canal B", "Canal C" }, s2.Channels.Select(c => c.Name).ToArray());
            Assert.Contains("1 de 2", s2.Error);
            Assert.Contains("copia", s2.Error);
        }

        [Fact]
        public void WithoutADiskCopyAFailedRefreshKeepsWhatWasLoadedInThisSession()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            loader.Ok("bbb", L(Ace(2, "Canal B", "Y")));
            var svc = Make(Config("aaa", "bbb"), loader, disk: false);
            svc.Start(); Done(svc);
            Assert.Equal(2, svc.Channels.Count);
            loader.Fail("aaa"); loader.Fail("bbb");
            svc.Refresh(); Done(svc);
            Assert.Equal(CatalogState.Ready, svc.State);
            Assert.Equal(2, svc.Channels.Count);
            Assert.Contains("2 de 2", svc.Error);
            Assert.Contains("copia", svc.Error);
            loader.Ok("bbb", L(Ace(3, "Canal C", "Y")));
            svc.Refresh(); Done(svc);
            Assert.Equal(new[] { "Canal A", "Canal C" }, svc.Channels.Select(c => c.Name).ToArray());
            Assert.Contains("1 de 2", svc.Error);
        }

        [Fact]
        public void AnEmptyListCountsAsFailedAndKeepsTheCopy()
        {
            var first = new TvFakeListLoader();
            first.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var s1 = Make(Config("aaa"), first);
            s1.Start(); Done(s1);
            s1.Dispose();
            var second = new TvFakeListLoader();
            second.Ok("aaa", L(LinkEntry.FromUrl("Actualizado", "", null), LinkEntry.FromUrl("Agenda", "https://x.example/a.html", "A")));
            var s2 = Make(Config("aaa"), second);
            s2.Start(); Done(s2);
            Assert.Equal(CatalogState.Ready, s2.State);
            Assert.Single(s2.Channels);
            Assert.Contains("copia", s2.Error);
        }

        [Fact]
        public void ListsWithoutPlayableLinksAreFailedWithItsOwnMessage()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(LinkEntry.FromUrl("Agenda", "https://x.example/a.html", "A")));
            var svc = Make(Config("aaa"), loader);
            svc.Start(); Done(svc);
            Assert.Equal(CatalogState.Failed, svc.State);
            Assert.Equal(CatalogService.ErrorNoChannels, svc.Error);
        }

        [Fact]
        public void NoConfiguredListsIsFailed()
        {
            var svc = Make(new AppConfig(), new TvFakeListLoader());
            svc.Start(); Done(svc);
            Assert.Equal(CatalogState.Failed, svc.State);
            Assert.Equal(CatalogService.ErrorNoLists, svc.Error);
        }

        [Fact]
        public void TheSameListTwiceIsLoadedOnce()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa", "aaa", " aaa "), loader);
            svc.Start(); Done(svc);
            Assert.Equal(1, loader.CallCount);
        }

        [Fact]
        public void ASlowListIsAbandonedAfterTheTimeout()
        {
            var loader = new TvFakeListLoader();
            loader.Script["aaa"] = t => new TaskCompletionSource<List<LinkEntry>>().Task;      // it never finishes nor looks at the token
            loader.Ok("bbb", L(Ace(1, "Canal B", "X")));
            var svc = Make(Config("aaa", "bbb"), loader);
            svc.ListTimeout = TimeSpan.FromMilliseconds(200);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            svc.Start(); Done(svc);
            Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
            Assert.Equal(CatalogState.Ready, svc.State);
            Assert.Single(svc.Channels);
            Assert.Contains("1 de 2", svc.Error);
        }

        [Fact]
        public void DisposeCancelsTheLoadsAndSilencesChanged()
        {
            var loader = new TvFakeListLoader();
            CancellationToken seen = CancellationToken.None;
            var entered = new ManualResetEventSlim();
            loader.Script["aaa"] = t => { seen = t; entered.Set(); return Task.Delay(Timeout.Infinite, t).ContinueWith(x => new List<LinkEntry>(), TaskContinuationOptions.OnlyOnRanToCompletion); };
            var svc = Make(Config("aaa"), loader);
            int changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            svc.Start();
            Assert.True(entered.Wait(5000));
            svc.Dispose();
            Assert.True(seen.IsCancellationRequested);
            Assert.True(svc.LoadTask.Wait(5000));
            Assert.Equal(0, changed);
            Assert.Equal(CatalogState.Loading, svc.State);
            svc.Start();                                                           // after Dispose it does nothing
            svc.Refresh();
            Assert.Equal(1, loader.CallCount);
            svc.Dispose();                                                         // double Dispose
        }

        [Fact]
        public void ASubscriberThatThrowsDoesNotBreakTheOthers()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa"), loader);
            int ok = 0;
            svc.Changed += () => { throw new InvalidOperationException("mal"); };
            svc.Changed += () => Interlocked.Increment(ref ok);
            svc.Start(); Done(svc);
            Assert.Equal(1, ok);
            Assert.Equal(CatalogState.Ready, svc.State);
        }

        // ---------- Copy on disk ----------

        [Fact]
        public void ACorruptOrOversizedCopyIsIgnored()
        {
            foreach (var junk in new[] { "{not json", "[1,2,3]", "{\"lists\":5}", "{\"lists\":[{\"id\":1,\"entries\":7}]}", "\u0000\u0001\u0002" })
            {
                File.WriteAllText(CachePath, junk);
                var loader = new TvFakeListLoader();
                var gate = new TaskCompletionSource<List<LinkEntry>>();
                loader.Script["aaa"] = t => gate.Task;
                var svc = Make(Config("aaa"), loader);
                svc.Start();
                TvWait.Until(() => loader.CallCount == 1, "it requests the list");
                Assert.Equal(CatalogState.Loading, svc.State);                    // there is no valid copy
                gate.SetResult(L(Ace(1, "Canal A", "X")));
                Done(svc);
                Assert.Equal(CatalogState.Ready, svc.State);
                svc.Dispose();
            }
            // larger than the cap: it is not even read
            File.WriteAllBytes(CachePath, new byte[CatalogService.MaxCacheBytes + 10]);
            var l2 = new TvFakeListLoader();
            var g2 = new TaskCompletionSource<List<LinkEntry>>();
            l2.Script["aaa"] = t => g2.Task;
            var s2 = Make(Config("aaa"), l2);
            s2.Start();
            TvWait.Until(() => l2.CallCount == 1, "it requests the list");
            Assert.Equal(CatalogState.Loading, s2.State);
            g2.SetResult(L(Ace(1, "Canal A", "X")));
            Done(s2);
            Assert.True(new FileInfo(CachePath).Length < CatalogService.MaxCacheBytes);        // and it was replaced by a good one
        }

        [Fact]
        public void ACopyOfOtherListsIsNotUsed()
        {
            var first = new TvFakeListLoader();
            first.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var s1 = Make(Config("aaa"), first);
            s1.Start(); Done(s1);
            s1.Dispose();

            var gate = new TaskCompletionSource<List<LinkEntry>>();
            var second = new TvFakeListLoader();
            second.Script["zzz"] = t => gate.Task;
            var s2 = Make(Config("zzz"), second);
            s2.Start();
            TvWait.Until(() => second.CallCount == 1, "it requests the list");
            Assert.Equal(CatalogState.Loading, s2.State);
            Assert.Empty(s2.Channels);
            gate.SetResult(L(Ace(5, "Canal Z", "X")));
            Done(s2);
            Assert.Equal(new[] { "Canal Z" }, s2.Channels.Select(c => c.Name).ToArray());
        }

        [Fact]
        public void TheCopyDoesNotStoreTheListCodeAndDropsTheInvalidEntries()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("secretcode123", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("secretcode123"), loader);
            svc.Start(); Done(svc);
            string text = File.ReadAllText(CachePath);
            Assert.DoesNotContain("secretcode123", text);
            // a copy with broken entries: the good ones stay
            var id = CatalogService.ListId(new ListSource("secretcode123"));
            File.WriteAllText(CachePath, "{\"v\":1,\"lists\":[{\"id\":\"" + id + "\",\"entries\":[[\"Bueno\",\"acestream://" + CatalogBuilderTests.Cid(5) + "\",\"G\"],[1,2],[\"x\"],[\"Web\",\"http://a.example/x.html\",\"G\"],\"nada\",[\"Roto\",\"acestream://zz\",\"G\"]]}]}");
            var gate = new TaskCompletionSource<List<LinkEntry>>();
            var l2 = new TvFakeListLoader();
            l2.Script["secretcode123"] = t => gate.Task;
            var s2 = Make(Config("secretcode123"), l2);
            s2.Start();
            TvWait.Until(() => s2.State == CatalogState.Ready, "it uses the copy");
            Assert.Equal(new[] { "Bueno" }, s2.Channels.Select(c => c.Name).ToArray());
            gate.SetException(new IOException("no network"));
            Done(s2);
        }

        [Fact]
        public void WorksWithoutADataDirectory()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", L(Ace(1, "Canal A", "X")));
            var svc = Make(Config("aaa"), loader, disk: false);
            svc.Start(); Done(svc);
            Assert.Equal(CatalogState.Ready, svc.State);
        }

        [Fact]
        public void MergesNamesAndCategoriesLikeTheBuilder()
        {
            var loader = new TvFakeListLoader();
            loader.Ok("aaa", TvRealData.List1);
            loader.Ok("bbb", TvRealData.List2);
            var svc = Make(Config("aaa", "bbb"), loader);
            svc.Start(); Done(svc);
            Assert.Equal(307, svc.Channels.Count);
            Assert.Equal(13, svc.Categories.Count);
            Assert.Equal("", svc.Error);
            // the copy on disk returns exactly the same
            var gate = new TaskCompletionSource<List<LinkEntry>>();
            var l2 = new TvFakeListLoader();
            l2.Script["aaa"] = t => gate.Task; l2.Script["bbb"] = t => gate.Task;
            var s2 = Make(Config("aaa", "bbb"), l2);
            s2.Start();
            TvWait.Until(() => s2.State == CatalogState.Ready, "copia");
            Assert.Equal(svc.Channels.Select(c => c.Id + "|" + c.Name + "|" + c.Quality + "|" + c.CategoryName).ToArray(), s2.Channels.Select(c => c.Id + "|" + c.Name + "|" + c.Quality + "|" + c.CategoryName).ToArray());
            Assert.Equal(svc.Categories.Select(c => c.Name).ToArray(), s2.Categories.Select(c => c.Name).ToArray());
            gate.SetCanceled();
            Done(s2);
        }

        // ---------- Real loader ----------

        [Fact]
        public async Task TheRealLoaderFetchesAMylinkspasteCodeOverDnsAndAnM3uOverHttp()
        {
            using (var dns = new FakeDnsServer())
            {
                int n = 0;
                foreach (var file in new[] { TvRealData.Code1 + ".json", TvRealData.Code2 + ".json" })
                {
                    var root = MiniJson.AsObject(MiniJson.Parse(TestUtil.Fixture("real/" + file)));
                    foreach (var kv in root)
                        if (!dns.Records.ContainsKey(kv.Key + ".elcano.top")) dns.Add(kv.Key + ".elcano.top", FakeDnsServer.EncodeRecord(kv.Value, n++ % 2 == 0));
                }
                var dnsOpt = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(3), UseDohFallback = false };
                var m3uBytes = TestUtil.Fixture("real/mylinkspaste.m3u");
                var listener = new HttpListener();
                string prefix = null;
                for (int attempt = 0; attempt < 20 && prefix == null; attempt++)
                {
                    int port = 20000 + new Random().Next(30000);
                    try { listener.Prefixes.Clear(); listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start(); prefix = "http://127.0.0.1:" + port + "/"; }
                    catch (HttpListenerException) { }
                }
                Assert.NotNull(prefix);
                var serve = Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await listener.GetContextAsync().ConfigureAwait(false); } catch (Exception) { return; }
                        ctx.Response.ContentType = "audio/x-mpegurl";
                        ctx.Response.ContentLength64 = m3uBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(m3uBytes, 0, m3uBytes.Length).ConfigureAwait(false);
                        ctx.Response.Close();
                    }
                });
                try
                {
                    using (var loader = new ListLoader(dnsOpt))
                    {
                        var viaDns = await loader.LoadAsync(new ListSource(TvRealData.Code2), CancellationToken.None);
                        Assert.Equal(307, viaDns.Count(e => e.Kind == LinkKind.AceStream));
                        var viaHttp = await loader.LoadAsync(new ListSource(prefix + "lista.m3u"), CancellationToken.None);
                        Assert.Equal(307, viaHttp.Count(e => e.Kind == LinkKind.AceStream));
                        // the two lists give the same catalog: 307 channels
                        var svcLoader = new TvFakeListLoader();
                        svcLoader.Ok("a", viaDns); svcLoader.Ok("b", viaHttp);
                        var svc = Make(Config("a", "b"), svcLoader, disk: false);
                        svc.Start(); Done(svc);
                        Assert.Equal(307, svc.Channels.Count);

                        await Assert.ThrowsAsync<FormatException>(() => loader.LoadAsync(new ListSource("no es un codigo!"), CancellationToken.None));
                        await Assert.ThrowsAsync<ArgumentNullException>(() => loader.LoadAsync(null, CancellationToken.None));
                    }
                    // the service with the real loader and the two lists
                    using (var loader = new ListLoader(dnsOpt))
                    {
                        var cfg = Config(TvRealData.Code2, prefix + "lista.m3u", "caidaaaaaaaaaaaaaaaaaaaa");
                        var svc = Make(cfg, loader, disk: false);
                        svc.ListTimeout = TimeSpan.FromSeconds(20);
                        svc.Start(); Done(svc);
                        Assert.Equal(CatalogState.Ready, svc.State);
                        Assert.Equal(307, svc.Channels.Count);
                        Assert.Contains("1 de 3", svc.Error);
                    }
                }
                finally { listener.Stop(); listener.Close(); try { await serve; } catch (Exception) { } }
            }
        }
    }
}
