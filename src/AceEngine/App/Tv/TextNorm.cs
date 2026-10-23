using System;
using System.Collections.Generic;
using System.Text;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Text normalization for keys, searches and classification. All deterministic and independent of the culture and of ICU (on the TV invariant
    /// mode may be on, where string.Normalize does not work): accent folding uses a table of its own for Latin-1 and Latin Extended-A.
    /// </summary>
    internal static class TextNorm
    {
        /// <summary>Lowercase base letter of U+00C0..U+017F (the multiplication and division signs become a space).</summary>
        private const string LatinFold =
            "aaaaaaaceeeeiiiidnooooo ouuuuytsaaaaaaaceeeeiiiidnooooo ouuuuytyaaaaaaccccccccddddeeeeeeeeeegggggggghhhhiiiiiiiiiiiijjkkkllllllllllnnnnnnnnnoooooooorrrrrrssssssssttttttuuuuuuuuuuuuwwyyyzzzzzzs";

        /// <summary>Lowercase without accent (Latin letters only; the rest goes through ToLowerInvariant).</summary>
        public static char FoldChar(char c)
        {
            if (c < 128) return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
            if (c >= 0xC0 && c <= 0x17F) return LatinFold[c - 0xC0];
            return char.ToLowerInvariant(c);
        }

        /// <summary>
        /// Comparison key: lowercase, no accents, and everything that is not a letter or digit becomes a space (collapsed and trimmed). A "+" attached to a
        /// letter or digit is kept ("m+", "canal+", "rtl+"); loose or decorative "+" are not.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool gap = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c0 = s[i];
                if (c0 >= 0x300 && c0 <= 0x36F) continue;               // combining accent mark: ignored
                char c = FoldChar(c0);
                if (char.IsLetterOrDigit(c))
                {
                    if (gap && sb.Length > 0) sb.Append(' ');
                    gap = false;
                    sb.Append(c);
                }
                else if (c == '+' && !gap && sb.Length > 0 && char.IsLetterOrDigit(sb[sb.Length - 1])) sb.Append('+');
                else gap = true;
            }
            return sb.ToString();
        }

        /// <summary>true if it is a Latin letter (A-Z and the accented ones of Latin-1 / Latin Extended-A).</summary>
        public static bool IsLatinLetter(char c)
        {
            if (c < 128) return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            if (c >= 0xC0 && c <= 0x17F) return c != 0xD7 && c != 0xF7;
            return false;
        }

        public static bool IsUpper(char c) { return char.IsUpper(c); }

        /// <summary>
        /// Removes control characters, text-direction characters, zero-width characters and odd spaces; collapses the spaces and trims. A list is untrusted
        /// input: none of that must reach the screen.
        /// </summary>
        public static string CleanSpaces(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, maxLength));
            bool gap = false;
            for (int i = 0; i < s.Length && sb.Length < maxLength; i++)
            {
                char c = s[i];
                if (c == '​' || c == '‌' || c == '‍' || c == '⁠' || c == '﻿' || c == '‎' || c == '‏' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) continue;
                if (char.IsHighSurrogate(c))
                {
                    // a valid pair (emoji, mathematical letters) is kept; a lone surrogate is dropped
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) && sb.Length + 2 <= maxLength)
                    {
                        if (gap && sb.Length > 0) sb.Append(' ');
                        gap = false;
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { gap = true; continue; }
                if (gap && sb.Length > 0) sb.Append(' ');
                gap = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- Uppercase / title ----------

        /// <summary>Words that are always written as they are (acronyms and brands), by normalized key.</summary>
        private static readonly Dictionary<string, string> Known = BuildKnown();

        private static Dictionary<string, string> BuildKnown()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] upper = { "tv", "tve", "rtve", "tdt", "dazn", "nba", "nfl", "nhl", "mlb", "acb", "ufc", "wwe", "atp", "wta", "fifa", "uefa", "espn", "fox", "hbo", "amc", "axn",
                "cnn", "bbc", "bt", "rtl", "zdf", "ard", "orf", "tnt", "mtv", "itv", "rai", "tcm", "tvp", "hd", "fhd", "uhd", "sd", "vod", "usa", "gp", "vip", "dw", "eu",
                // loose countries that appear after the name ("FOX DEPORTES AR HD"); "de", "es" and "it" do not: they are words
                "uk", "us", "ar", "mx", "br", "pl", "tr", "ru", "fr", "pt", "nl" };
            foreach (var u in upper) d[u] = u.ToUpperInvariant();
            d["laliga"] = "LaLiga";
            d["motogp"] = "MotoGP";
            d["bein"] = "beIN";
            d["m+"] = "M+";
            d["4k"] = "4K";
            return d;
        }

        private static readonly HashSet<string> SmallWords = new HashSet<string>(StringComparer.Ordinal) { "de", "del", "y", "en", "con", "para", "por" };

        /// <summary>true if it is a short Roman numeral (I, II, III, IV, V, VI...), which is written in uppercase.</summary>
        private static bool IsRoman(string folded)
        {
            if (folded.Length == 0 || folded.Length > 4) return false;
            foreach (char c in folded) if (c != 'i' && c != 'v' && c != 'x') return false;
            return true;
        }

        /// <summary>
        /// The name has uniform case (all uppercase or all lowercase) and then it is converted to "Title Case": only the words without digits are looked at
        /// ("720p" or "4K" do not count), at least two letters are needed and any letter of another alphabet (Cyrillic, Arabic...) leaves it as it is.
        /// </summary>
        public static bool IsUniformCase(string name, out bool allUpper)
        {
            allUpper = false;
            if (string.IsNullOrEmpty(name)) return false;
            int letters = 0;
            bool anyLower = false, anyUpper = false;
            int start = 0;
            for (int i = 0; i <= name.Length; i++)
            {
                if (i < name.Length && name[i] != ' ') continue;
                bool hasDigit = false;
                int tokenLetters = 0;
                bool tl = false, tu = false;
                for (int j = start; j < i; j++)
                {
                    char c = name[j];
                    if (char.IsDigit(c)) hasDigit = true;
                    else if (char.IsLetter(c))
                    {
                        if (!IsLatinLetter(c)) return false;
                        tokenLetters++;
                        if (char.IsLower(c)) tl = true; else tu = true;
                    }
                }
                if (!hasDigit) { letters += tokenLetters; anyLower |= tl; anyUpper |= tu; }
                start = i + 1;
            }
            if (letters < 2 || (anyLower && anyUpper)) return false;
            allUpper = anyUpper;
            return true;
        }

        /// <summary>
        /// Converts a uniform-case name to "Title Case": "LIGA DE CAMPEONES" -> "Liga de Campeones", respecting known acronyms (NBA, DAZN, TV, LaLiga),
        /// words with digits ("F1", "4K", "1080p") and country codes in parentheses ("(DE)"). Connecting words ("de", "del", "y"...) go in
        /// lowercase except the first. A name with mixed upper and lower case is not touched.
        /// </summary>
        public static string ToTitle(string name)
        {
            bool up;
            if (!IsUniformCase(name, out up)) return name;
            var sb = new StringBuilder(name.Length);
            int idx = 0;
            int start = 0;
            for (int i = 0; i <= name.Length; i++)
            {
                if (i < name.Length && name[i] != ' ') continue;
                if (i > start)
                {
                    if (idx > 0) sb.Append(' ');
                    AppendWord(sb, name.Substring(start, i - start), idx, up);
                    idx++;
                }
                start = i + 1;
            }
            return sb.ToString();
        }

        private static void AppendWord(StringBuilder sb, string tok, int index, bool allUpper)
        {
            // separates opening and closing signs ("(DE)", "[UK]", "HD,")
            int a = 0, b = tok.Length;
            while (a < b && !char.IsLetterOrDigit(tok[a])) a++;
            while (b > a && !char.IsLetterOrDigit(tok[b - 1]) && tok[b - 1] != '+') b--;
            string pre = tok.Substring(0, a), core = tok.Substring(a, b - a), post = tok.Substring(b);
            sb.Append(pre);
            if (core.Length == 0) { sb.Append(post); return; }

            bool hasDigit = false;
            foreach (char c in core) if (char.IsDigit(c)) { hasDigit = true; break; }
            string key = Normalize(core);
            string known;
            if (hasDigit)
            {
                // "1080P" -> "1080p"; the other words with digits are left as they are if the name was in uppercase ("F1", "RTL2") and are capitalized if it was in lowercase
                if (Known.TryGetValue(key, out known)) sb.Append(known);
                else
                {
                    string q;
                    if (QualityRules.TryCanonical(core, out q) || allUpper) sb.Append(q); else AppendCapitalized(sb, core);
                }
            }
            else if (Known.TryGetValue(key, out known)) sb.Append(known);
            else if (core.EndsWith("+", StringComparison.Ordinal) && core.Length <= 4) sb.Append(core.ToUpperInvariant());
            else if ((pre.IndexOf('(') >= 0 || pre.IndexOf('[') >= 0) && core.Length >= 2 && core.Length <= 3 && (post.IndexOf(')') >= 0 || post.IndexOf(']') >= 0)) sb.Append(core.ToUpperInvariant());
            else if (IsRoman(key)) sb.Append(core.ToUpperInvariant());
            else if (index > 0 && SmallWords.Contains(key)) sb.Append(core.ToLowerInvariant());
            else AppendCapitalized(sb, core);
            sb.Append(post);
        }

        private static void AppendCapitalized(StringBuilder sb, string w)
        {
            bool up = true;
            foreach (char c in w)
            {
                if (char.IsLetter(c)) { sb.Append(up ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); up = false; }
                else { sb.Append(c); up = c == '-' || c == '/'; }
            }
        }
    }
}
