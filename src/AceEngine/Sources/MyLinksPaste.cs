using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Sources
{
    /// <summary>Result of querying a mylinkspaste code: the whole tree, already flattened.</summary>
    public sealed class LinkTree
    {
        public string RootCode;
        public List<LinkEntry> Entries = new List<LinkEntry>();
        /// <summary>Codes queried, in order, with the number of elements of each one.</summary>
        public List<KeyValuePair<string, int>> Visited = new List<KeyValuePair<string, int>>();
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// mylinkspaste link source: TXT record of &lt;code&gt;.elcano.top with a Base64 JSON (optional gzip). The categories point to other
    /// codes and are walked recursively (with a visited set); the sub-links of a group are flattened keeping their group.
    /// </summary>
    public sealed class MyLinksPasteClient
    {
        private const string Tag = "mylinks";
        public const string DefaultSuffix = "elcano.top";
        private readonly DnsTxtClient _dns;
        private readonly string _suffix;

        /// <summary>Cap on codes queried in a tree (defense against huge cycles or hostile lists).</summary>
        public int MaxCodes = 400;
        public int MaxDepth = 12;
        /// <summary>Simultaneous DNS queries when walking the categories.</summary>
        public int Concurrency = 6;

        public MyLinksPasteClient(DnsTxtClient dns, string suffix = DefaultSuffix)
        {
            _dns = dns;
            _suffix = suffix;
        }

        // ---------- Decoding a record ----------

        /// <summary>
        /// record -> elements: strips "text = " and the quotes, Base64 (tolerates line breaks and missing padding), gunzip if applicable, UTF-8, JSON.
        /// Throws FormatException if it cannot be interpreted.
        /// </summary>
        /// <summary>A real TXT is about 2 KB; up to 64 KiB of record and 2 MiB of decompressed JSON are accepted.</summary>
        public const int MaxRecordChars = 128 * 1024;
        public const int MaxJsonBytes = 2 * 1024 * 1024;
        /// <summary>Elements of a whole tree (all the categories together).</summary>
        public const int MaxEntriesTotal = 30000;
        public const int MaxNameLength = 200;
        public const int MaxUrlLength = 2048;

        public static List<RawElement> Decode(string record)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (record.Length > MaxRecordChars) throw new FormatException("TXT record too large");
            string clean = record.Replace("text = ", "").Replace("\"", "").Trim();
            var sb = new StringBuilder();
            foreach (char c in clean) if (!char.IsWhiteSpace(c)) sb.Append(c);
            string b64 = sb.ToString();
            if (b64.Length == 0) throw new FormatException("TXT record empty");
            b64 = b64.Replace('-', '+').Replace('_', '/');                   // also url-safe Base64
            int mod = b64.Length % 4;
            if (mod == 2) b64 += "=="; else if (mod == 3) b64 += "="; else if (mod == 1) throw new FormatException("invalid Base64");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new FormatException("the TXT record is not Base64"); }

            byte[] json = bytes;
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                try
                {
                    using (var ms = new MemoryStream(bytes))
                    using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                    using (var outMs = new MemoryStream())
                    {
                        var buf = new byte[8192];
                        int n;
                        long total = 0;
                        while ((n = gz.Read(buf, 0, buf.Length)) > 0) { outMs.Write(buf, 0, n); total += n; if (total > MaxJsonBytes) throw new FormatException("JSON too large"); }
                        json = outMs.ToArray();
                    }
                }
                catch (InvalidDataException) { json = bytes; }               // like the original: if gunzip fails the bytes are used as they are
            }
            object v;
            try { v = MiniJson.Parse(new UTF8Encoding(false, false).GetString(json)); }
            catch (JsonException ex) { throw new FormatException("invalid JSON: " + ex.Message); }
            var list = v as List<object>;
            if (list == null)
            {
                var single = MiniJson.AsObject(v);
                if (single == null) throw new FormatException("the JSON is not a list of elements");
                list = new List<object> { single };
            }
            var res = new List<RawElement>();
            foreach (var item in list)
            {
                var e = ToElement(item);
                if (e != null) res.Add(e);
            }
            return res;
        }

        /// <summary>A stream element of the JSON.</summary>
        public sealed class RawElement
        {
            public string Name, Url, Type, Ref;
            public List<RawElement> SubLinks;
        }

        private static RawElement ToElement(object item)
        {
            var o = MiniJson.AsObject(item);
            if (o == null) return null;
            var e = new RawElement
            {
                Name = Clip(MiniJson.GetString(o, "name") ?? "", MaxNameLength),
                Url = Clip(MiniJson.GetString(o, "url"), MaxUrlLength),
                Type = Clip(MiniJson.GetString(o, "type"), 32),
                Ref = Clip(MiniJson.GetString(o, "ref"), 64)
            };
            var subs = MiniJson.GetArray(o, "subLinks") ?? MiniJson.GetArray(o, "sublinks");
            if (subs != null)
            {
                e.SubLinks = new List<RawElement>();
                foreach (var s in subs) { var se = ToElement(s); if (se != null) e.SubLinks.Add(se); }
            }
            return e;
        }

        private static string Clip(string s, int n) { return s == null || s.Length <= n ? s : s.Substring(0, n); }

        /// <summary>Flattens an element: a group with subLinks gives one candidate per sub-link (with its group); the others, the element itself.</summary>
        public static IEnumerable<LinkEntry> Flatten(RawElement e, string group)
        {
            if (e.SubLinks != null && e.SubLinks.Count > 0)
            {
                string g = string.IsNullOrEmpty(group) ? e.Name : group;
                if (!string.IsNullOrEmpty(e.Url))
                    yield return LinkEntry.FromUrl(e.Name, e.Url, g);
                foreach (var s in e.SubLinks)
                    foreach (var x in Flatten(s, g).Select(y => { if (y.Parent == null) y.Parent = e.Name; return y; })) yield return x;
                yield break;
            }
            yield return LinkEntry.FromUrl(e.Name, e.Url, group);
        }

        // ---------- Recursive query ----------

        public async Task<LinkTree> FetchAsync(string code, CancellationToken ct)
        {
            var tree = new LinkTree { RootCode = code };
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var gate = new object();
            var sem = new SemaphoreSlim(Math.Max(1, Concurrency));
            var results = new Dictionary<string, List<LinkEntry>>();       // code -> own elements, to keep the order of the tree
            var order = new List<string>();

            // Each code is queried once; the categories of a code are queried in parallel and their results are assembled afterwards in order.
            Func<string, string, int, Task<List<object>>> visit = null;
            visit = async (c, group, depth) =>
            {
                var parts = new List<object>();                              // LinkEntry | Task<List<object>>
                lock (gate) { if (!visited.Add(c)) return parts; if (visited.Count > MaxCodes) { tree.Errors.Add("limit of " + MaxCodes + " codes reached"); return parts; } }
                List<RawElement> els;
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    IList<string> recs = await _dns.QueryTxtAsync(c + "." + _suffix, ct).ConfigureAwait(false);
                    if (recs.Count == 0) { lock (gate) tree.Errors.Add(c + ": no TXT records"); return parts; }
                    els = Decode(recs[0]);                                   // ONLY the first TXT record
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is DnsException || ex is FormatException)
                {
                    lock (gate) tree.Errors.Add(c + ": " + ex.Message);
                    return parts;                                            // if there is already something, the error is ignored
                }
                finally { sem.Release(); }
                lock (gate) tree.Visited.Add(new KeyValuePair<string, int>(c, els.Count));
                AceLog.Debug(Tag, c + ": " + els.Count + " elements");

                var tasks = new List<Task<List<object>>>();
                foreach (var e in els)
                {
                    if (e.Type == "category" && !string.IsNullOrEmpty(e.Ref))
                    {
                        if (depth + 1 > MaxDepth) { lock (gate) tree.Errors.Add(c + ": maximum depth"); continue; }
                        string g = string.IsNullOrEmpty(e.Name) ? group : e.Name;
                        var t = visit(e.Ref, g, depth + 1);
                        tasks.Add(t);
                        parts.Add(t);
                    }
                    else foreach (var x in Flatten(e, group)) parts.Add(x);
                }
                if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);
                return parts;
            };

            var root = await visit(code, null, 0).ConfigureAwait(false);
            if (tree.Visited.Count == 0)
                throw new DnsException(tree.Errors.Count > 0 ? tree.Errors[0] : "No information found for code " + code);
            Assemble(root, tree.Entries);
            if (tree.Entries.Count > MaxEntriesTotal)
            {
                tree.Errors.Add("list trimmed to " + MaxEntriesTotal + " elements");
                tree.Entries.RemoveRange(MaxEntriesTotal, tree.Entries.Count - MaxEntriesTotal);
            }
            return tree;
        }

        private static void Assemble(List<object> parts, List<LinkEntry> into)
        {
            foreach (var p in parts)
            {
                var le = p as LinkEntry;
                if (le != null) { into.Add(le); continue; }
                var t = (Task<List<object>>)p;
                Assemble(t.Result, into);
            }
        }
    }
}
