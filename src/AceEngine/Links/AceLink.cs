using System;
using System.Collections.Generic;
using AceEngine.Util;

namespace AceEngine.Links
{
    public enum AceLinkKind
    {
        /// <summary>SHA-1 of the transport file. Resolved over P2P.</summary>
        ContentId,
        /// <summary>BitTorrent infohash, 40 hex.</summary>
        InfoHash,
        Magnet,
        /// <summary>http/https URL of a transport file.</summary>
        TransportUrl,
        /// <summary>Local path (or file://) of a transport file.</summary>
        TransportFile
    }

    /// <summary>
    /// User link normalized to a descriptor: content_id, infohash, magnet, url or local file.
    /// Accepted forms: plain 40 hex, magnet:, acestream://&lt;id&gt;, acestream:?content_id|infohash|magnet|url|data=...,
    /// and paths/URLs ending in .torrent / .acelive.
    /// </summary>
    public sealed class AceLink
    {
        public AceLinkKind Kind { get; private set; }
        /// <summary>content_id / infohash in lowercase, full magnet, URL or path.</summary>
        public string Value { get; private set; }
        public MagnetLink Magnet { get; private set; }

        public static AceLink ContentId(string hex) { return new AceLink { Kind = AceLinkKind.ContentId, Value = hex.ToLowerInvariant() }; }
        public static AceLink InfoHash(string hex) { return new AceLink { Kind = AceLinkKind.InfoHash, Value = hex.ToLowerInvariant() }; }

        /// <summary>
        /// Interprets the text. plainHexIs decides what a plain 40-hex id means: in the Java client it
        /// is a content_id; the `ace play` CLI treats it as an infohash (the user's choice).
        /// </summary>
        public static AceLink Parse(string input, AceLinkKind plainHexIs = AceLinkKind.ContentId)
        {
            AceLink l;
            string err;
            if (!TryParse(input, plainHexIs, out l, out err)) throw new FormatException(err);
            return l;
        }

        public static bool TryParse(string input, AceLinkKind plainHexIs, out AceLink link, out string error)
        {
            link = null; error = null;
            if (string.IsNullOrWhiteSpace(input)) { error = "empty link"; return false; }
            input = input.Trim();

            if (Hex.IsHex40(input))
            {
                link = new AceLink { Kind = plainHexIs, Value = input.ToLowerInvariant() };
                return true;
            }

            if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                MagnetLink m;
                if (!MagnetLink.TryParse(input, out m, out error)) return false;
                link = new AceLink { Kind = AceLinkKind.Magnet, Value = input, Magnet = m };
                return true;
            }

            if (input.StartsWith("acestream:%3F", StringComparison.OrdinalIgnoreCase))
                input = "acestream:?" + input.Substring("acestream:%3F".Length);

            if (input.StartsWith("acestream:", StringComparison.OrdinalIgnoreCase))
                return TryParseAceStreamUri(input, out link, out error);

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                link = new AceLink { Kind = AceLinkKind.TransportUrl, Value = input };
                return true;
            }

            if (input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                string path;
                if (!TryFileUriToPath(input, out path)) { error = "invalid file URI"; return false; }
                link = new AceLink { Kind = AceLinkKind.TransportFile, Value = path };
                return true;
            }

            // Local path (scheme other than acestream and ends in .torrent) or any existing file.
            link = new AceLink { Kind = AceLinkKind.TransportFile, Value = input };
            return true;
        }

        private static bool TryParseAceStreamUri(string uri, out AceLink link, out string error)
        {
            link = null; error = null;
            string rest = uri.Substring("acestream:".Length);
            if (rest.StartsWith("//"))
            {
                // acestream://<40hex> (old format)
                string id = rest.Substring(2);
                int cut = id.IndexOfAny(new[] { '/', '?', '#' });
                if (cut >= 0) id = id.Substring(0, cut);
                if (!Hex.IsHex40(id)) { error = "acestream:// requires a 40-hex content_id"; return false; }
                link = ContentId(id);
                return true;
            }
            if (rest.StartsWith("?")) rest = rest.Substring(1);
            var query = ParseQuery(rest);
            string v;
            if (query.TryGetValue("content_id", out v))
            {
                if (!Hex.IsHex40(v)) { error = "invalid content_id"; return false; }
                link = ContentId(v); return true;
            }
            if (query.TryGetValue("infohash", out v))
            {
                if (!Hex.IsHex40(v)) { error = "invalid infohash"; return false; }
                link = InfoHash(v); return true;
            }
            if (query.TryGetValue("magnet", out v))
            {
                MagnetLink m;
                if (!MagnetLink.TryParse(v, out m, out error)) return false;
                link = new AceLink { Kind = AceLinkKind.Magnet, Value = v, Magnet = m };
                return true;
            }
            if (query.TryGetValue("url", out v))
            {
                link = new AceLink { Kind = AceLinkKind.TransportUrl, Value = v };
                return true;
            }
            if (query.TryGetValue("data", out v))
            {
                if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    link = new AceLink { Kind = AceLinkKind.TransportUrl, Value = v };
                else if (v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    string path;
                    if (!TryFileUriToPath(v, out path)) { error = "invalid file URI"; return false; }
                    link = new AceLink { Kind = AceLinkKind.TransportFile, Value = path };
                }
                else
                    link = new AceLink { Kind = AceLinkKind.TransportFile, Value = v };
                return true;
            }
            error = "Missing required params in AceStream URI";
            return false;
        }

        private static bool TryFileUriToPath(string uri, out string path)
        {
            path = null;
            try { path = new Uri(uri).LocalPath; return true; }
            catch (UriFormatException) { return false; }
        }

        private static Dictionary<string, string> ParseQuery(string q)
        {
            // parse_qs(keep_blank_values=True): the first value of each key is used.
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = Unescape(eq < 0 ? part : part.Substring(0, eq));
                string v = eq < 0 ? "" : Unescape(part.Substring(eq + 1));
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }

        private static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (UriFormatException) { return s; }
        }

        /// <summary>Canonical form acestream:?... (as_string of AceStreamUri).</summary>
        public string ToAceStreamUri()
        {
            switch (Kind)
            {
                case AceLinkKind.ContentId: return "acestream:?content_id=" + Value;
                case AceLinkKind.InfoHash: return "acestream:?infohash=" + Value;
                case AceLinkKind.Magnet: return "acestream:?magnet=" + Uri.EscapeDataString(Value);
                case AceLinkKind.TransportUrl: return "acestream:?url=" + Uri.EscapeDataString(Value);
                default: return "acestream:?data=" + Uri.EscapeDataString(Value);
            }
        }

        public override string ToString() { return Kind + ":" + Value; }
    }
}
