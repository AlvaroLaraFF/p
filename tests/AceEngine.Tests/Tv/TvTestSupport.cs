using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Sources;

namespace AceEngine.Tests.Tv
{
    /// <summary>Fake catalog: fixed content that can be changed, with its Changed event.</summary>
    internal sealed class TvFakeCatalog : IChannelCatalog
    {
        private CatalogSnapshot _snap;
        public TvFakeCatalog(CatalogSnapshot snap) { _snap = snap; State = CatalogState.Ready; }
        public CatalogState State { get; set; }
        public string Error { get { return ""; } }
        public IReadOnlyList<Category> Categories { get { return _snap.Categories; } }
        public IReadOnlyList<Channel> Channels { get { return _snap.Channels; } }
        public Channel Find(string id) { return _snap.Find(id); }
        public void Start() { }
        public event Action Changed;
        public void Set(CatalogSnapshot snap) { _snap = snap; var h = Changed; if (h != null) h(); }
        public int ChangedSubscribers { get { return Changed == null ? 0 : Changed.GetInvocationList().Length; } }
    }

    /// <summary>Fake list loader: each list (by code) answers with whatever the script says.</summary>
    internal sealed class TvFakeListLoader : IListLoader
    {
        public readonly Dictionary<string, Func<CancellationToken, Task<List<LinkEntry>>>> Script = new Dictionary<string, Func<CancellationToken, Task<List<LinkEntry>>>>();
        public readonly List<string> Calls = new List<string>();
        public int CallCount { get { lock (Calls) return Calls.Count; } }

        public Task<List<LinkEntry>> LoadAsync(ListSource source, CancellationToken ct)
        {
            lock (Calls) Calls.Add(source.Code);
            Func<CancellationToken, Task<List<LinkEntry>>> f;
            if (!Script.TryGetValue(source.Code, out f)) throw new InvalidOperationException("list without a script: " + source.Code);
            return f(ct);
        }

        public void Ok(string code, List<LinkEntry> entries) { Script[code] = t => Task.FromResult(entries); }
        public void Fail(string code, string message = "no network") { Script[code] = t => { throw new System.Net.Http.HttpRequestException(message); }; }
    }

    internal sealed class TvFakeProvider : IRemoteSearchProvider
    {
        public string Name { get; set; }
        public Func<string, CancellationToken, Task<IReadOnlyList<RemoteResult>>> OnSearch = (q, t) => Task.FromResult((IReadOnlyList<RemoteResult>)new RemoteResult[0]);
        public Func<string, CancellationToken, Task<IReadOnlyList<RemoteResult>>> OnExplore = (k, t) => Task.FromResult((IReadOnlyList<RemoteResult>)new RemoteResult[0]);
        public int SearchCalls, ExploreCalls;
        public TvFakeProvider(string name) { Name = name; }
        public Task<IReadOnlyList<RemoteResult>> SearchAsync(string query, CancellationToken ct) { Interlocked.Increment(ref SearchCalls); return OnSearch(query, ct); }
        public Task<IReadOnlyList<RemoteResult>> ExploreAsync(string kindId, CancellationToken ct) { Interlocked.Increment(ref ExploreCalls); return OnExplore(kindId, ct); }
    }

    internal static class TvWait
    {
        /// <summary>Waits (polling) for a condition to be met; fails with the message if it does not arrive.</summary>
        public static void Until(Func<bool> cond, string what, int timeoutMs = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new Xunit.Sdk.XunitException("timed out waiting for: " + what);
                Thread.Sleep(5);
            }
        }
    }

    internal static class TvTempDir
    {
        public static string Create()
        {
            string d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p-tv-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(d);
            return d;
        }
        public static void Delete(string d) { try { System.IO.Directory.Delete(d, true); } catch (Exception) { } }
    }
}
