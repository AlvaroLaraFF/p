using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Tests.Sources;
using AceEngine.Util;

namespace AceEngine.Tests.Tv
{
    /// <summary>The real lists the user dumped (2026-10-16), loaded through the same path as the app (fake DNS + MyLinksPasteClient, and the M3U list) and saved only once.</summary>
    internal static class TvRealData
    {
        public const string Code1 = "a1b2c3d4e5f60718293a4b5c";
        public const string Code2 = "b2c3d4e5f60718293a4b5c6d";

        private static readonly Lazy<Dictionary<string, List<LinkEntry>>> Trees = new Lazy<Dictionary<string, List<LinkEntry>>>(LoadTrees);

        /// <summary>First code: 51 acestream links in 9 categories.</summary>
        public static List<LinkEntry> List1 { get { return Clone(Trees.Value[Code1]); } }
        /// <summary>Second code: includes the first and 256 more (307 distinct content_ids, plus labels, web pages and an invalid id).</summary>
        public static List<LinkEntry> List2 { get { return Clone(Trees.Value[Code2]); } }
        /// <summary>The M3U list the user dumped: the same unique links with their group.</summary>
        public static List<LinkEntry> M3u { get { return M3uParser.Parse(Encoding.UTF8.GetString(TestUtil.Fixture("real/mylinkspaste.m3u"))); } }

        private static List<LinkEntry> Clone(List<LinkEntry> l) { return new List<LinkEntry>(l); }

        private static Dictionary<string, List<LinkEntry>> LoadTrees()
        {
            return Task.Run(async () =>
            {
                var res = new Dictionary<string, List<LinkEntry>>();
                using (var dns = new FakeDnsServer())
                {
                    int n = 0;
                    foreach (var file in new[] { Code1 + ".json", Code2 + ".json" })
                    {
                        var root = MiniJson.AsObject(MiniJson.Parse(TestUtil.Fixture("real/" + file)));
                        foreach (var kv in root)
                        {
                            if (dns.Records.ContainsKey(kv.Key + ".elcano.top")) continue;
                            dns.Add(kv.Key + ".elcano.top", FakeDnsServer.EncodeRecord(kv.Value, n++ % 2 == 0));
                        }
                    }
                    var o = new DnsOptions { Servers = new List<string> { "127.0.0.1" }, Port = dns.Port, Timeout = TimeSpan.FromSeconds(3), UseDohFallback = false };
                    var client = new MyLinksPasteClient(new DnsTxtClient(o));
                    foreach (var code in new[] { Code1, Code2 })
                    {
                        var tree = await client.FetchAsync(code, CancellationToken.None).ConfigureAwait(false);
                        res[code] = tree.Entries;
                    }
                }
                return res;
            }).GetAwaiter().GetResult();
        }
    }
}
