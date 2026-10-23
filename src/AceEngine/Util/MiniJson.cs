using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AceEngine.Util
{
    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>
    /// Minimal in-house JSON (no dependencies): objects -> Dictionary&lt;string, object&gt;, arrays -> List&lt;object&gt;,
    /// strings, long (integers) or double, bool and null. Used for mylinkspaste, DoH, meta-trackers,
    /// the resolution cache and the get_media_files output. System.Text.Json would drag several packages onto the TV.
    /// </summary>
    public static class MiniJson
    {
        private const int MaxDepth = 64;

        /// <summary>Maximum input characters and values: a hostile JSON (e.g. a decompressed TXT or a DoH response) must not exhaust memory.</summary>
        public const int MaxLength = 8 * 1024 * 1024;
        public const int MaxValues = 400000;
        [ThreadStatic] private static int _values;

        // ---------- Reading ----------

        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length > MaxLength) throw new JsonException("JSON too large (" + text.Length + " characters)");
            _values = 0;
            int pos = 0;
            if (text.Length > 0 && text[0] == '﻿') pos = 1;
            SkipWs(text, ref pos);
            var v = ParseValue(text, ref pos, 0);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw new JsonException("data after the root value at position " + pos);
            return v;
        }

        public static bool TryParse(string text, out object value)
        {
            try { value = Parse(text); return true; }
            catch (JsonException) { value = null; return false; }
        }

        public static object Parse(byte[] utf8)
        {
            if (utf8 != null && utf8.Length > MaxLength) throw new JsonException("JSON too large (" + utf8.Length + " bytes)");
            return Parse(new UTF8Encoding(false, false).GetString(utf8));
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
        }

        private static object ParseValue(string s, ref int p, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("excessive nesting");
            if (++_values > MaxValues) throw new JsonException("too many values");
            if (p >= s.Length) throw new JsonException("unexpected end");
            char c = s[p];
            switch (c)
            {
                case '{': return ParseObject(s, ref p, depth);
                case '[': return ParseArray(s, ref p, depth);
                case '"': return ParseString(s, ref p);
                case 't': Expect(s, ref p, "true"); return true;
                case 'f': Expect(s, ref p, "false"); return false;
                case 'n': Expect(s, ref p, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref p);
                    throw new JsonException("unexpected character '" + c + "' at position " + p);
            }
        }

        private static void Expect(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) throw new JsonException("expected '" + word + "' at position " + p);
            p += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int p, int depth)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            p++;                                   // {
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return d; }
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"') throw new JsonException("expected a key at position " + p);
                string k = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':') throw new JsonException("expected ':' at position " + p);
                p++;
                SkipWs(s, ref p);
                d[k] = ParseValue(s, ref p, depth + 1);
                SkipWs(s, ref p);
                if (p >= s.Length) throw new JsonException("unterminated object");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return d; }
                throw new JsonException("expected ',' or '}' at position " + p);
            }
        }

        private static List<object> ParseArray(string s, ref int p, int depth)
        {
            var l = new List<object>();
            p++;                                   // [
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return l; }
            while (true)
            {
                SkipWs(s, ref p);
                l.Add(ParseValue(s, ref p, depth + 1));
                SkipWs(s, ref p);
                if (p >= s.Length) throw new JsonException("unterminated array");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return l; }
                throw new JsonException("expected ',' or ']' at position " + p);
            }
        }

        private static string ParseString(string s, ref int p)
        {
            p++;                                   // "
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= s.Length) throw new JsonException("unterminated string");
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new JsonException("control character in a string");
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) throw new JsonException("incomplete escape");
                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > s.Length) throw new JsonException("incomplete \\u escape");
                        int code;
                        if (!int.TryParse(s.Substring(p, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            throw new JsonException("invalid \\u escape");
                        sb.Append((char)code);       // surrogate pairs are rebuilt on their own when the two \u are concatenated
                        p += 4;
                        break;
                    default: throw new JsonException("invalid escape '\\" + e + "'");
                }
            }
        }

        private static object ParseNumber(string s, ref int p)
        {
            int start = p;
            if (s[p] == '-') p++;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            bool isInt = true;
            if (p < s.Length && s[p] == '.') { isInt = false; p++; while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++; }
            if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
            {
                isInt = false; p++;
                if (p < s.Length && (s[p] == '+' || s[p] == '-')) p++;
                while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            }
            string t = s.Substring(start, p - start);
            if (t == "-" || t.Length == 0) throw new JsonException("invalid number at position " + start);
            if (isInt)
            {
                long l;
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            }
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new JsonException("invalid number '" + t + "'");
        }

        // ---------- Tolerant access ----------

        public static Dictionary<string, object> AsObject(object v) { return v as Dictionary<string, object>; }
        public static List<object> AsArray(object v) { return v as List<object>; }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            var s = v as string;
            if (s != null) return s;
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? "true" : "false";
            return null;
        }

        public static long? GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            long l;
            var s = v as string;
            if (s != null && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            return null;
        }

        public static bool? GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            if (v is bool) return (bool)v;
            return null;
        }

        public static List<object> GetArray(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        // ---------- Writing ----------

        /// <summary>Serializes to compact JSON (or indented). Accepts the types Parse produces plus int, bool and IEnumerable.</summary>
        public static string Stringify(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, bool indent, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is long || v is int || v is short || v is byte || v is ulong || v is uint)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as System.Collections.IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, e.Value, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            var en = v as System.Collections.IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
