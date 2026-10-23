using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Sources;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace search`: the app's search from the command line.
    ///   ace search "liga" [--ace] [--m3u LIST] [--provider URL]        search like the app's (local ones from --m3u first, then the providers')
    ///   ace search --explore futbol [--ace]                            Explore a kind
    ///   ace search "liga" --ace --raw [--host http://...]              raw signed request to ONE host: HTTP status, body, signature and decoded JSON (diagnostics)
    /// </summary>
    internal static class SearchCommands
    {
        public static readonly string[] Flags = { "--ace", "--raw", "--all-variants", "--no-merge", "--url-only", "--bad-sign" };

        private sealed class StaticCatalog : IChannelCatalog
        {
            private readonly CatalogSnapshot _snap;
            public StaticCatalog(CatalogSnapshot snap) { _snap = snap; }
            public CatalogState State { get { return CatalogState.Ready; } }
            public string Error { get { return null; } }
            public IReadOnlyList<Category> Categories { get { return _snap.Categories; } }
            public IReadOnlyList<Channel> Channels { get { return _snap.Channels; } }
            public Channel Find(string id) { return _snap.Find(id); }
            public void Start() { }
            public event Action Changed { add { } remove { } }
        }

        public static async Task<int> Run(string text, Func<string, string> get, Func<string, bool> has, Func<string, int, int> getInt, CancellationToken ct)
        {
            bool ace = has("--ace");
            string kind = get("--explore");
            if (string.IsNullOrWhiteSpace(text) && kind == null && !has("--raw")) { Console.Error.WriteLine("usage: ace search <text> [--ace] [--m3u LIST] [--provider URL] [--explore KIND] [--raw [--host URL] [--all-variants]]"); return 2; }
            var opt = new AceCentralOptions();
            if (has("--timeout")) { opt.HostTimeout = TimeSpan.FromSeconds(getInt("--timeout", 8)); opt.TotalTimeout = TimeSpan.FromSeconds(Math.Max(getInt("--timeout", 8) * 2, 20)); }
            if (get("--host") != null) opt.Hosts = new List<string> { get("--host") };
            if (get("--lang") != null) opt.Lang = get("--lang");
            if (get("--langs") != null) opt.PreferredLanguages = get("--langs");
            if (get("--countries") != null) opt.PreferredCountries = get("--countries");
            var central = new AceCentralSearchProvider(null, opt);

            if (has("--raw"))
            {
                var hosts = opt.Hosts.ToList();
                int rc = 1;
                foreach (var host in hosts)
                {
                    string cat = kind != null ? ExploreCategories.For(kind) : get("--category");
                    if (has("--url-only"))
                    {
                        // only the signed URL (nothing is sent); --bad-sign spoils it to compare the server's response
                        string u = central.SignedUrl(host, string.IsNullOrWhiteSpace(text) ? null : text, cat, getInt("--page", 0), getInt("--page-size", 10));
                        Console.WriteLine(has("--bad-sign") ? u.Substring(0, u.Length - 4) + "0000" : u);
                        rc = 0;
                        continue;
                    }
                    Console.WriteLine("== " + host + "  (" + (text ?? "") + (cat != null ? " category=" + cat : "") + ")");
                    var r = await central.RawRequestAsync(host, string.IsNullOrWhiteSpace(text) ? null : text, cat, getInt("--page", 0), getInt("--page-size", 10), has("--all-variants"), ct);
                    Console.WriteLine("URL:        " + r.Url);
                    Console.WriteLine("HTTP:       " + (r.HttpStatus == 0 ? "no response" : r.HttpStatus.ToString(CultureInfo.InvariantCulture)) + "  " + r.ElapsedMs + " ms");
                    if (r.Error != null) Console.WriteLine("Error:      " + r.Error);
                    if (r.Body.Length > 0) Console.WriteLine("Body:       " + (r.Body.Length > 600 ? r.Body.Substring(0, 600) + "... (" + r.Body.Length + " B)" : r.Body));
                    if (r.HttpStatus == 200 && r.Body.Length > 0)
                    {
                        Console.WriteLine("Resp sig:   " + (r.SignVariant ?? "DOES NOT MATCH"));
                        if (r.ParseProblem != null) Console.WriteLine("Problem:    " + r.ParseProblem);
                        if (r.Json != null) Console.WriteLine("JSON:       " + (has("--full") || r.Json.Length <= 4000 ? r.Json : r.Json.Substring(0, 4000) + "... (" + r.Json.Length + " B)"));
                        if (r.SignVariant != null) rc = 0;
                    }
                }
                return rc;
            }

            var providers = new List<IRemoteSearchProvider>();
            if (get("--provider") != null) providers.Add(new HttpSearchProvider(new SearchProviderConfig { Name = "cli", Url = get("--provider") }));
            if (ace) providers.Add(new AceCentralSearchProvider(null, opt));
            CatalogSnapshot snap = CatalogSnapshot.Empty;
            string m3u = get("--m3u");
            if (m3u != null)
            {
                string body = m3u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await new System.Net.Http.HttpClient().GetStringAsync(m3u) : File.ReadAllText(m3u);
                snap = CatalogBuilder.Build(M3uParser.Parse(body));
                Console.WriteLine("Local catalog: " + snap.Channels.Count + " channels (" + m3u + ")");
            }
            var settings = new UserSettings();
            if (get("--min-res") != null) settings.MinResolution = getInt("--min-res", 0);
            if (get("--language") != null) settings.Language = get("--language");
            using (var svc = new SearchService(new StaticCatalog(snap), providers))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<Channel> local, remote;
                if (kind != null) { local = svc.Explore(kind); remote = await svc.ExploreRemoteAsync(kind, ct); }
                else { local = svc.Search(text); remote = await svc.SearchRemoteAsync(text, ct); }
                Console.WriteLine("Local: " + local.Count + "   New remote: " + remote.Count + "   (" + sw.ElapsedMilliseconds + " ms)");
                int shown = 0, max = getInt("--max", 40);
                foreach (var c in local) { if (shown++ >= max) break; Print("local ", c); }
                foreach (var c in remote) { if (shown++ >= max) break; Print("remote", c); }
                foreach (var p in providers.OfType<AceCentralSearchProvider>()) if (p.LastError != null) Console.WriteLine("Central search warning: " + p.LastError);
                foreach (var p in providers.OfType<HttpSearchProvider>()) if (p.LastError != null) Console.WriteLine("Provider warning: " + p.LastError);
                return local.Count + remote.Count > 0 ? 0 : 1;
            }
        }

        private static void Print(string tag, Channel c)
        {
            Console.WriteLine(tag + "  " + (c.Resolution > 0 ? c.Resolution + "p" : "  ?").PadLeft(6) + "  " + (c.Language.Length > 0 ? c.Language : "--") + "  " + c.Name + "   [" + (c.CategoryName.Length > 0 ? c.CategoryName : "-") + "]  " + c.Id);
        }
    }
}
