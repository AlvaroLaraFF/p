using System;
using System.Globalization;
using System.Text;

namespace AceEngine.Bencode
{
    /// <summary>
    /// Reproduces Python 3's repr() for str and bytes. Ace's bencode sorts a dict's keys
    /// by str((key, value)).encode(), so the key's repr is needed to
    /// re-serialize the same way as Ace (only used when serializing with KeyOrder.Ace).
    /// </summary>
    public static class PythonRepr
    {
        public static string ReprStr(string s)
        {
            bool hasSingle = s.IndexOf('\'') >= 0;
            bool hasDouble = s.IndexOf('"') >= 0;
            char quote = (hasSingle && !hasDouble) ? '"' : '\'';
            var sb = new StringBuilder(s.Length + 2);
            sb.Append(quote);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == quote || ch == '\\') { sb.Append('\\').Append(ch); }
                else if (ch == '\t') sb.Append("\\t");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch < 0x20 || ch == 0x7f) sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                else if (ch < 0x7f) sb.Append(ch);
                else
                {
                    int cp = ch;
                    int consumed = 1;
                    if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        cp = char.ConvertToUtf32(ch, s[i + 1]);
                        consumed = 2;
                    }
                    if (IsPrintable(s, i, cp))
                    {
                        sb.Append(s, i, consumed);
                    }
                    else if (cp <= 0xff) sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                    else if (cp <= 0xffff) sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
                    i += consumed - 1;
                }
            }
            sb.Append(quote);
            return sb.ToString();
        }

        private static bool IsPrintable(string s, int index, int codePoint)
        {
            UnicodeCategory cat = codePoint > 0xffff
                ? CharUnicodeInfo.GetUnicodeCategory(s, index)
                : CharUnicodeInfo.GetUnicodeCategory((char)codePoint);
            switch (cat)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
                default:
                    return true;
            }
        }

        public static string ReprBytes(byte[] b)
        {
            bool hasSingle = Array.IndexOf(b, (byte)'\'') >= 0;
            bool hasDouble = Array.IndexOf(b, (byte)'"') >= 0;
            char quote = (hasSingle && !hasDouble) ? '"' : '\'';
            var sb = new StringBuilder(b.Length + 3);
            sb.Append('b').Append(quote);
            foreach (var x in b)
            {
                char ch = (char)x;
                if (ch == quote || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch == '\t') sb.Append("\\t");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (x < 0x20 || x >= 0x7f) sb.Append("\\x").Append(((int)x).ToString("x2", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            sb.Append(quote);
            return sb.ToString();
        }

        /// <summary>
        /// repr of a key as Ace would see it: str if the bytes are valid UTF-8
        /// (bdecode_dict decodes keys to str) and bytes otherwise.
        /// </summary>
        public static string ReprKey(BString key)
        {
            string s;
            if (key.TryGetUtf8(out s)) return ReprStr(s);
            return ReprBytes(key.Value);
        }
    }
}
