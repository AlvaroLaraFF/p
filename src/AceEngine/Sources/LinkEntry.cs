using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.Links;
using AceEngine.Util;

namespace AceEngine.Sources
{
    public enum LinkKind
    {
        /// <summary>Header or caption without a link (empty url).</summary>
        Label,
        /// <summary>acestream://, content_id or plain infohash.</summary>
        AceStream,
        Magnet,
        /// <summary>http(s) to a playable stream (.m3u8, .ts, .mp4...): it can be given to the player without the engine.</summary>
        HttpStream,
        /// <summary>http(s) to a web page (schedule...): not playable.</summary>
        Web,
        Unsupported
    }

    /// <summary>A link of a list (mylinkspaste or M3U), already classified.</summary>
    public sealed class LinkEntry
    {
        public string Name { get; set; }
        public string Url { get; set; }
        /// <summary>Group or category it belongs to (group-title in M3U; mylinkspaste category).</summary>
        public string Group { get; set; }
        public LinkKind Kind { get; set; }
        /// <summary>Normalized link (null if it is neither AceStream nor magnet).</summary>
        public AceLink Link { get; set; }
        /// <summary>Identifier to deduplicate and cache: content_id or infohash in lowercase, or the URL.</summary>
        public string Key { get; set; }
        /// <summary>Name of the expandable group a sub-link comes from, if any.</summary>
        public string Parent { get; set; }

        public static readonly string[] StreamExtensions = { ".m3u8", ".m3u", ".ts", ".mp4", ".mkv", ".mpd", ".flv", ".aac", ".mp3", ".webm", ".avi" };

        /// <summary>Classifies a URL.</summary>
        public static LinkEntry FromUrl(string name, string url, string group = null, string parent = null)
        {
            var e = new LinkEntry { Name = name ?? "", Url = url ?? "", Group = group, Parent = parent };
            string u = (url ?? "").Trim();
            if (u.Length == 0) { e.Kind = LinkKind.Label; e.Key = null; return e; }
            if (u.StartsWith("acestream:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || Hex.IsHex40(u))
            {
                AceLink l;
                string err;
                // A link of a list can never point to a local file (acestream:?data=/path): what arrives from the network is not used as a path.
                if (AceLink.TryParse(u, AceLinkKind.ContentId, out l, out err) && l.Kind != AceLinkKind.TransportFile)
                {
                    e.Link = l;
                    e.Kind = l.Kind == AceLinkKind.Magnet ? LinkKind.Magnet : LinkKind.AceStream;
                    e.Key = l.Kind == AceLinkKind.Magnet ? l.Magnet.InfoHashHex : l.Value;
                    if (l.Kind == AceLinkKind.Magnet) e.Key = "ih:" + e.Key;
                    else if (l.Kind == AceLinkKind.InfoHash) e.Key = "ih:" + l.Value;
                    else if (l.Kind == AceLinkKind.ContentId) e.Key = "cid:" + l.Value;
                    return e;
                }
                e.Kind = LinkKind.Unsupported; e.Key = u;
                return e;
            }
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Key = u;
                string path = u;
                int cut = path.IndexOfAny(new[] { '?', '#' });
                if (cut >= 0) path = path.Substring(0, cut);
                e.Kind = StreamExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) ? LinkKind.HttpStream : LinkKind.Web;
                return e;
            }
            e.Kind = LinkKind.Unsupported; e.Key = u;
            return e;
        }

        public bool IsProbeable { get { return Kind == LinkKind.AceStream || Kind == LinkKind.Magnet || Kind == LinkKind.HttpStream; } }

        public override string ToString() { return Name + " " + Url; }
    }

    public static class LinkList
    {
        /// <summary>Removes duplicates by Key keeping the first name. Labels and non-playable links are kept without deduplicating.</summary>
        public static List<LinkEntry> Dedupe(IEnumerable<LinkEntry> entries)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var res = new List<LinkEntry>();
            foreach (var e in entries)
            {
                if (e.Key == null || !e.IsProbeable) { res.Add(e); continue; }
                if (seen.Add(e.Key)) res.Add(e);
            }
            return res;
        }
    }
}
