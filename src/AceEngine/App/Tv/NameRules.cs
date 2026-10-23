using System;
using System.Collections.Generic;
using System.Text;
using AceEngine.Sources;

namespace AceEngine.App.Tv
{
    /// <summary>Quality label carried by a channel name ("1080p", "720p", "FHD", "HD", "SD", "4K").</summary>
    internal static class QualityRules
    {
        /// <summary>Quality of a name: the highest of the labels it carries ("CAZA Y PESCA FHD HD" -> "FHD"), or "" if it carries none.</summary>
        public static string Detect(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string best = "";
            int bestRank = 0, depth = 0, n = name.Length;
            string lastTop = null;
            for (int i = 0; i < n;)
            {
                char c = name[i];
                if (c == '(' || c == '[') { depth++; i++; continue; }
                if (c == ')' || c == ']') { if (depth > 0) depth--; i++; continue; }
                if (!char.IsLetterOrDigit(c)) { i++; continue; }
                int j = i;
                while (j < n && char.IsLetterOrDigit(name[j])) j++;
                string tok = name.Substring(i, j - i).ToLowerInvariant();
                if (depth == 0) lastTop = tok;
                string label;
                int rank = Rank(tok, out label);
                // A loose "SD" only counts at the end of the name ("SD Huesca" is a club, not a quality)
                if (rank > bestRank && tok != "sd") { best = label; bestRank = rank; }
                i = j;
            }
            if (lastTop == "sd" && bestRank < 1) best = "SD";
            return best;
        }

        private static int Rank(string tok, out string label)
        {
            switch (tok)
            {
                case "4k": case "uhd": case "2160p": case "2160i": label = "4K"; return 5;
                case "1080p": case "1080i": label = "1080p"; return 4;
                case "fhd": label = "FHD"; return 4;
                case "720p": case "720i": label = "720p"; return 3;
                case "hd": label = "HD"; return 2;
                case "sd": case "576p": case "576i": case "480p": case "480i": case "360p": label = "SD"; return 1;
                default: label = ""; return 0;
            }
        }

        /// <summary>Canonical spelling of a quality label ("1080P" -> "1080p", "fhd" -> "FHD"); false if the word is not a quality label.</summary>
        public static bool TryCanonical(string token, out string canonical)
        {
            string lower = token.ToLowerInvariant();
            canonical = token;
            switch (lower)
            {
                case "1080p": case "1080i": case "720p": case "720i": case "576p": case "576i": case "480p": case "480i": case "360p": case "2160p": case "2160i": canonical = lower; return true;
                case "4k": canonical = "4K"; return true;
                case "uhd": case "fhd": case "hd": case "sd": canonical = lower.ToUpperInvariant(); return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// List prefixes ("NEW ERA - ", "NEW ERA VI - ", "SPORT TV ", "NEW LOOP") that list authors put in front of categories and names: they say which
    /// list something comes from, and the UI does not tell lists apart. The real prefixes in the data (16-10-2026) are in <see cref="Prefixes"/>.
    /// </summary>
    internal static class ListRules
    {
        /// <summary>Known list prefixes, in lowercase and with the words separated by a space ("new_era" and "new era" are the same).</summary>
        public static readonly string[] Prefixes = { "new era", "new loop", "sport tv" };

        private const string Separators = "-–—:|>»·•/";

        private static bool IsSep(char c) { return Separators.IndexOf(c) >= 0; }

        /// <summary>
        /// Strips a list prefix from the start. It may be followed by a Roman numeral or a digit ("NEW ERA VI - X") and a separator ("-", ":", "|"...).
        /// With allowBare (categories) the prefix followed by a space is enough ("SPORT TV INTERNACIONAL 1" -> "INTERNACIONAL 1"); without it (channel names)
        /// a separator and something after it are needed. rest is left empty if only the prefix was there ("NEW ERA II").
        /// </summary>
        public static bool TryStripPrefix(string s, bool allowBare, out string rest)
        {
            rest = s;
            if (string.IsNullOrEmpty(s)) return false;
            int p = 0;
            while (p < s.Length && !char.IsLetterOrDigit(s[p])) p++;
            foreach (var prefix in Prefixes)
            {
                int q = MatchPrefix(s, p, prefix);
                if (q < 0) continue;
                // Roman numeral or digit of the list, only if followed by a separator or the end
                int r = SkipSpaces(s, q);
                int t = r;
                while (t < s.Length && char.IsLetterOrDigit(s[t])) t++;
                if (t > r)
                {
                    string tok = TextNorm.Normalize(s.Substring(r, t - r));
                    bool numeral = IsRomanOrSmallNumber(tok);
                    int after = SkipSpaces(s, t);
                    if (numeral && (after >= s.Length || IsSep(s[after]))) q = t;
                }
                bool sep = false;
                int u = SkipSpaces(s, q);
                while (u < s.Length && IsSep(s[u])) { sep = true; u++; u = SkipSpaces(s, u); }
                string tail = (sep ? s.Substring(u) : s.Substring(q)).Trim();
                if (!sep && !allowBare) continue;                  // a channel name only loses the prefix if it comes with a separator
                if (tail.Length == 0 && !allowBare) continue;
                rest = tail;
                return true;
            }
            return false;
        }

        private static int SkipSpaces(string s, int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '_')) p++;
            return p;
        }

        /// <summary>If s[p..] starts with the prefix (case- and accent-insensitive, with " " or "_" between words and a word boundary), returns where it ends; otherwise -1.</summary>
        private static int MatchPrefix(string s, int p, string prefix)
        {
            int i = p;
            for (int k = 0; k < prefix.Length; k++)
            {
                char want = prefix[k];
                if (want == ' ')
                {
                    if (i >= s.Length || (s[i] != ' ' && s[i] != '_')) return -1;
                    while (i < s.Length && (s[i] == ' ' || s[i] == '_')) i++;
                    continue;
                }
                if (i >= s.Length || TextNorm.FoldChar(s[i]) != want) return -1;
                i++;
            }
            if (i < s.Length && char.IsLetterOrDigit(s[i])) return -1;
            return i;
        }

        private static bool IsRomanOrSmallNumber(string folded)
        {
            if (folded.Length == 0) return false;
            if (folded.Length <= 2)
            {
                bool digits = true;
                foreach (char c in folded) if (!char.IsDigit(c)) { digits = false; break; }
                if (digits) return true;
            }
            if (folded.Length > 4) return false;
            foreach (char c in folded) if (c != 'i' && c != 'v' && c != 'x') return false;
            return true;
        }

        /// <summary>Removes the occurrences of a word (case- and accent-insensitive): the name of the originating list must not be seen in the UI.</summary>
        public static string RemoveWord(string s, string wordFolded)
        {
            if (string.IsNullOrEmpty(s) || s.Length < wordFolded.Length) return s;
            StringBuilder sb = null;
            int copied = 0;
            for (int i = 0; i < s.Length;)
            {
                if (!char.IsLetterOrDigit(s[i])) { i++; continue; }
                int j = i;
                while (j < s.Length && char.IsLetterOrDigit(s[j])) j++;
                if (j - i == wordFolded.Length && string.Equals(TextNorm.Normalize(s.Substring(i, j - i)), wordFolded, StringComparison.Ordinal))
                {
                    if (sb == null) sb = new StringBuilder(s.Length);
                    sb.Append(s, copied, i - copied);
                    copied = j;
                }
                i = j;
            }
            if (sb == null) return s;
            sb.Append(s, copied, s.Length - copied);
            return sb.ToString();
        }
    }

    /// <summary>Cleanup of a channel name for display.</summary>
    internal static class ChannelNames
    {
        public const int MaxSourceName = 200;
        public const int MaxName = 120;
        private const string Edge = " -–—|:·•>»/,";

        /// <summary>
        /// "M+ Liga de Campeones 2 1080p **" -> "M+ Liga de Campeones 2 1080p"; "NEW ERA - DAZN 1 FHD" -> "DAZN 1 FHD"; "LIGA DE CAMPEONES FHD" ->
        /// "Liga de Campeones FHD". Keeps the quality text. Returns "" if nothing is left.
        /// </summary>
        public static string Clean(string raw)
        {
            string s = TextNorm.CleanSpaces(raw, MaxSourceName);
            if (s.Length == 0) return "";
            s = TrimMarks(s);
            string rest;
            if (ListRules.TryStripPrefix(s, false, out rest)) s = rest;
            s = StripLinks(s);
            s = ListRules.RemoveWord(s, "elcano");
            s = TextNorm.CleanSpaces(s, MaxSourceName);
            s = s.Trim(Edge.ToCharArray());
            s = TrimMarks(s);
            s = TextNorm.ToTitle(s);
            if (s.Length > MaxName) s = s.Substring(0, MaxName).TrimEnd();
            return s;
        }

        private static readonly HashSet<string> Tlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "top", "online", "blog", "com", "net", "org", "info", "me", "tv", "io", "xyz", "site", "link", "club", "live", "es", "eu", "ru", "co",
            "app", "dev", "pro", "cc", "to", "ws", "biz", "fun", "space", "store", "vip", "win", "icu", "one", "world", "stream", "watch", "tk", "ml",
            "ga", "cf", "gq", "lat", "nu", "sx", "su", "is", "li", "ly", "gg", "sh", "click", "cam", "monster", "rest", "skin", "today", "zone"
        };
        private static readonly HashSet<string> JunkWords = new HashSet<string>(StringComparer.Ordinal) { "zeronet", "ipfs", "ipns", "vpn", "(vpn)", "[vpn]" };

        /// <summary>
        /// Removes from a name the advertising of whoever broadcasts it (mainly in Ace search results): links ("https://t.me/x",
        /// "http://127.0.0.1:43110/..."), loose domains ("elcano.top", "shickat.online"), "@user", "zeronet"/"ipfs"/"(VPN)", the word that gives its name
        /// to those domains ("Shickat" from "shickat.online") and the separators left dangling. "DAZN LaLiga 1080 elcano.top zeronet http://... " ->
        /// "DAZN LaLiga 1080". Numbers with a dot ("1.5") and normal names are not touched.
        /// </summary>
        internal static string StripLinks(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf('.') < 0 && s.IndexOf('@') < 0 && s.IndexOf("zeronet", StringComparison.OrdinalIgnoreCase) < 0 && s.IndexOf("vpn", StringComparison.OrdinalIgnoreCase) < 0)) return s;
            var tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var isJunk = new bool[tokens.Length];
            bool any = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                string label;
                if (IsLinkToken(tokens[i], out label) || JunkWords.Contains(TextNorm.Normalize(tokens[i])))
                {
                    if (label != null && label.Length >= 3) labels.Add(label);
                    isJunk[i] = any = true;
                }
            }
            if (!any) return s;
            // What comes after the first link is more advertising: it is cut there (if there is already a name before it; if the link is at the start, it is skipped).
            // The word that gives its name to the removed domains ("Shickat" from "shickat.online") is also removed when loose.
            var keep = new List<string>(tokens.Length);
            bool content = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (isJunk[i]) { if (content) break; continue; }
                string bare = tokens[i].Trim(Edge.ToCharArray());
                if (bare.Length > 0 && labels.Contains(TextNorm.Normalize(bare))) continue;
                keep.Add(tokens[i]);
                if (bare.Length > 0) content = true;
            }
            return string.Join(" ", keep).Trim(Edge.ToCharArray()).Trim();
        }

        /// <summary>"https://...", "www.x.com", "elcano.top", "t.me/canal", "@user". In label, the first part of the domain ("elcano") so that it is also removed when loose.</summary>
        private static bool IsLinkToken(string t, out string label)
        {
            label = null;
            string w = t.Trim('(', ')', '[', ']', '<', '>', ',', ';', '|', '"', '\'');
            if (w.Length == 0) return false;
            if (w[0] == '@' && w.Length > 2) return true;
            int scheme = w.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                string rest = w.Substring(scheme + 3);
                int slash = rest.IndexOf('/');
                string host = slash >= 0 ? rest.Substring(0, slash) : rest;
                label = HostLabel(host);
                return true;
            }
            int sl = w.IndexOf('/');
            string h = sl >= 0 ? w.Substring(0, sl) : w;
            if (h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) { label = HostLabel(h); return true; }
            int dot = h.LastIndexOf('.');
            if (dot <= 0 || dot == h.Length - 1) return false;
            string tld = h.Substring(dot + 1);
            if (!Tlds.Contains(tld)) return false;
            foreach (char c in h) if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return false;
            bool letters = false;
            foreach (char c in h.Substring(0, dot)) if (char.IsLetter(c)) { letters = true; break; }
            if (!letters) return false;                                             // "1.5" is not a domain
            label = HostLabel(h);
            return true;
        }

        private static string HostLabel(string host)
        {
            int colon = host.IndexOf(':');
            if (colon >= 0) host = host.Substring(0, colon);
            var parts = host.Split('.');
            if (parts.Length < 2) return null;
            string first = parts[0].Equals("www", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 ? parts[1] : parts[parts.Length - 2];
            foreach (char c in first) if (!char.IsLetter(c)) return null;          // IPs and labels with digits are not removed when loose
            string norm = TextNorm.Normalize(first);
            return norm == "ipfs" || norm == "t" || norm == "x" ? null : norm;
        }

        /// <summary>Removes the author's reliability marks ("*", "**", stars) and the spaces at the ends.</summary>
        private static string TrimMarks(string s)
        {
            int a = 0, b = s.Length;
            while (b > a && (s[b - 1] == '*' || s[b - 1] == '★' || s[b - 1] == '☆' || s[b - 1] == ' ')) b--;
            while (a < b && (s[a] == '*' || s[a] == '★' || s[a] == '☆' || s[a] == ' ')) a++;
            return s.Substring(a, b - a);
        }

        /// <summary>Fallback name for a link without a name: the last part of the URL or the start of the identifier.</summary>
        public static string Fallback(LinkEntry e)
        {
            string key = e.Key ?? "";
            if (e.Kind == LinkKind.HttpStream)
            {
                string u = e.Url ?? "";
                int q = u.IndexOfAny(new[] { '?', '#' });
                if (q >= 0) u = u.Substring(0, q);
                int slash = u.LastIndexOf('/');
                string last = slash >= 0 && slash < u.Length - 1 ? u.Substring(slash + 1) : "";
                last = TextNorm.CleanSpaces(last, 60);
                if (last.Length > 0) return last;
                return "Canal";
            }
            int colon = key.IndexOf(':');
            string id = colon >= 0 ? key.Substring(colon + 1) : key;
            return id.Length >= 8 ? "Canal " + id.Substring(0, 8) : "Canal";
        }
    }

    /// <summary>
    /// Key and nice name of a category. The categories of several lists are joined by key: lowercase, no accents, no list prefix
    /// ("NEW ERA - "), no numbering ("1.", "(2)", "01 -", "INTERNACIONAL 1"), no decorative symbols and with the spaces together.
    /// </summary>
    internal static class CategoryNames
    {
        public const string OthersKey = "otros";
        public const string OthersName = "Otros";
        public const int MaxName = 60;

        public static void Resolve(string raw, out string key, out string name)
        {
            string s = TextNorm.CleanSpaces(raw, 200);
            // sometimes the numbering comes before the prefix ("1. NEW ERA - X") and sometimes the prefix before the numbering; it repeats until nothing changes
            for (int round = 0; round < 3; round++)
            {
                string before = s;
                s = StripLeadingNumbering(s);
                string rest;
                if (ListRules.TryStripPrefix(s, true, out rest)) s = rest;
                if (string.Equals(s, before, StringComparison.Ordinal)) break;
            }
            s = ListRules.RemoveWord(s, "elcano");
            s = TextNorm.CleanSpaces(s, 200);
            s = Unwrap(TrimDecoration(s));
            s = StripTrailingNumbering(s);
            s = Unwrap(TrimDecoration(s));

            key = TextNorm.Normalize(s);
            if (key.Length == 0) { key = OthersKey; name = OthersName; return; }
            s = TextNorm.ToTitle(s);
            if (s.Length > MaxName) s = s.Substring(0, MaxName).TrimEnd();
            name = s;
        }

        /// <summary>Removes what is not a letter or digit from the ends ("═══ X ═══", "- X -"), except a parenthesis or bracket that closes something opened inside.</summary>
        private static string TrimDecoration(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && !char.IsLetterOrDigit(s[a]) && s[a] != '(' && s[a] != '[') a++;
            while (b > a && !char.IsLetterOrDigit(s[b - 1]) && s[b - 1] != ')' && s[b - 1] != ']' && s[b - 1] != '+') b--;
            return s.Substring(a, b - a);
        }

        /// <summary>"(OTROS)" -> "OTROS".</summary>
        private static string Unwrap(string s)
        {
            if (s.Length >= 2 && ((s[0] == '(' && s[s.Length - 1] == ')') || (s[0] == '[' && s[s.Length - 1] == ']')) && s.IndexOf(s[0] == '(' ? ')' : ']', 1, s.Length - 2) < 0)
                return TrimDecoration(s.Substring(1, s.Length - 2));
            return s;
        }

        /// <summary>"1. DEPORTES", "01 - DEPORTES", "(2) NBA" -> without the numbering. "24 Horas" and "4K Cine" are not touched (the digit is not followed by a dot, dash or parenthesis).</summary>
        private static string StripLeadingNumbering(string s)
        {
            int p = 0;
            bool open = false, hash = false;
            if (p < s.Length && (s[p] == '(' || s[p] == '[' || s[p] == '#')) { open = s[p] != '#'; hash = s[p] == '#'; p++; }
            int d = p;
            while (p < s.Length && char.IsDigit(s[p]) && p - d < 3) p++;
            if (p == d || (p < s.Length && char.IsDigit(s[p]))) return s;
            bool closed = false;
            if (p < s.Length && (s[p] == ')' || s[p] == ']')) { closed = true; p++; }
            int q = p;
            while (q < s.Length && s[q] == ' ') q++;
            bool sep = q < s.Length && (s[q] == '.' || s[q] == ':' || s[q] == '-' || s[q] == '–' || s[q] == '—' || s[q] == ')');
            if (!(closed || sep || hash)) return s;          // "(2) X", "1) X", "#3 X", "1. X", "01 - X"
            while (q < s.Length && (s[q] == '.' || s[q] == ':' || s[q] == '-' || s[q] == '–' || s[q] == '—' || s[q] == ')' || s[q] == ' ')) q++;
            string rest = s.Substring(q);
            return rest.Length == 0 ? s : rest;
        }

        /// <summary>"INTERNACIONAL 1" -> "INTERNACIONAL"; "LIGA DE CAMPEONES (2)" -> "LIGA DE CAMPEONES"; "F1" and "TV 3" stay as they are.</summary>
        private static string StripTrailingNumbering(string s)
        {
            int e = s.Length;
            if (e > 0 && (s[e - 1] == ')' || s[e - 1] == ']')) e--;
            int digitsEnd = e;
            while (e > 0 && char.IsDigit(s[e - 1])) e--;
            int digits = digitsEnd - e;
            if (digits == 0 || digits > 2) return s;
            // the number may come with "n.", "nº" or "#" (and spaces between the mark and the digit)
            int w = e;
            int sp = w;
            while (sp > 0 && s[sp - 1] == ' ') sp--;
            if (sp > 0 && s[sp - 1] == '#') w = sp - 1;
            else if (sp > 1 && (s[sp - 1] == '.' || s[sp - 1] == 'º' || s[sp - 1] == '°') && (s[sp - 2] == 'n' || s[sp - 2] == 'N') && (sp == 2 || s[sp - 3] == ' ')) w = sp - 2;
            else if (w > 0 && (s[w - 1] == '(' || s[w - 1] == '[')) w--;
            // it must start a word: what precedes is a space or a separator
            if (w > 0 && s[w - 1] != ' ' && !IsDash(s[w - 1])) return s;
            string head = TrimDecoration(s.Substring(0, w));
            int significant = 0;
            foreach (char c in head) if (char.IsLetterOrDigit(c)) significant++;
            return significant >= 3 ? head : s;
        }

        private static bool IsDash(char c) { return c == '-' || c == '–' || c == '—' || c == ':' || c == '|'; }
    }
}
