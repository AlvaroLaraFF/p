using System;
using System.Collections.Generic;
using System.Text;
using AceEngine.Util;

namespace AceEngine.Links
{
    /// <summary>
    /// Magnet link. Ace only understands xt=urn:btih:&lt;40 hex&gt; (base32 NOT supported); here base32 (32 characters)
    /// is also accepted as an extension. All 'tr' trackers are kept (Ace's MagnetLink
    /// kept only one).
    /// </summary>
    public sealed class MagnetLink
    {
        public byte[] InfoHash { get; private set; }
        public string InfoHashHex { get { return Hex.ToHex(InfoHash); } }
        public string DisplayName { get; private set; }
        public IList<string> Trackers { get; private set; }
        /// <summary>Suggested peers (x.pe=host:port).</summary>
        public IList<string> PeerHints { get; private set; }
        public IList<string> WebSeeds { get; private set; }
        public string Original { get; private set; }

        public static MagnetLink Parse(string uri)
        {
            MagnetLink m;
            string error;
            if (!TryParse(uri, out m, out error)) throw new FormatException(error);
            return m;
        }

        public static bool TryParse(string uri, out MagnetLink magnet, out string error)
        {
            magnet = null; error = null;
            if (uri == null || !uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                error = "not a magnet link";
                return false;
            }
            int q = uri.IndexOf('?');
            if (q < 0) { error = "magnet without parameters"; return false; }
            var m = new MagnetLink
            {
                Original = uri,
                Trackers = new List<string>(),
                PeerHints = new List<string>(),
                WebSeeds = new List<string>()
            };
            foreach (var part in uri.Substring(q + 1).Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part.Substring(0, eq);
                string val = eq < 0 ? "" : Decode(part.Substring(eq + 1));
                switch (key)
                {
                    case "xt":
                        if (val.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] ih = ParseInfoHash(val.Substring(9));
                            if (ih != null) m.InfoHash = ih;   // as Ace does: keep the last valid btih
                        }
                        break;
                    case "dn": m.DisplayName = val; break;
                    case "tr": if (val.Length > 0) m.Trackers.Add(val); break;
                    case "x.pe": if (val.Length > 0) m.PeerHints.Add(val); break;
                    case "ws": if (val.Length > 0) m.WebSeeds.Add(val); break;
                }
            }
            if (m.InfoHash == null)
            {
                error = "magnet without a valid xt=urn:btih: (40 hex or 32 base32)";
                return false;
            }
            magnet = m;
            return true;
        }

        /// <summary>40 hex or 32 base32 (RFC 4648) -> 20 bytes. Null if not valid.</summary>
        public static byte[] ParseInfoHash(string s)
        {
            if (s == null) return null;
            if (s.Length == 40)
            {
                byte[] b;
                return Hex.TryParse(s, out b) ? b : null;
            }
            if (s.Length == 32) return Base32Decode(s);
            return null;
        }

        private static byte[] Base32Decode(string s)
        {
            var res = new byte[20];
            int buffer = 0, bits = 0, pos = 0;
            foreach (char ch in s.ToUpperInvariant())
            {
                int v;
                if (ch >= 'A' && ch <= 'Z') v = ch - 'A';
                else if (ch >= '2' && ch <= '7') v = ch - '2' + 26;
                else return null;
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    if (pos >= 20) return null;
                    res[pos++] = (byte)((buffer >> bits) & 0xff);
                }
            }
            return pos == 20 ? res : null;
        }

        public static string Build(byte[] infoHash, string name = null, IEnumerable<string> trackers = null)
        {
            var sb = new StringBuilder("magnet:?xt=urn:btih:").Append(Hex.ToHex(infoHash));
            if (!string.IsNullOrEmpty(name)) sb.Append("&dn=").Append(Uri.EscapeDataString(name));
            if (trackers != null) foreach (var t in trackers) sb.Append("&tr=").Append(Uri.EscapeDataString(t));
            return sb.ToString();
        }

        private static string Decode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (UriFormatException) { return s; }
        }
    }
}
