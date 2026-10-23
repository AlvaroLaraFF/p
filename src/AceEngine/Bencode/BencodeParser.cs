using System;
using System.Text;

namespace AceEngine.Bencode
{
    public sealed class BencodeParseOptions
    {
        /// <summary>
        /// Allows trailing garbage after the root value (Ace's bdecode uses sloppy=1 by default).
        /// Defaults to true.
        /// </summary>
        public bool AllowTrailingData = true;

        /// <summary>If true, requires sorted keys without duplicates (Ace's bdecode does not require it). Defaults to false.</summary>
        public bool RequireSortedKeys = false;

        /// <summary>Maximum nesting depth (defense against hostile input).</summary>
        public int MaxDepth = 100;

        /// <summary>
        /// Maximum number of values (integers, strings, lists and dictionaries) in total. Each value costs tens of bytes of memory even if it takes 3 in the
        /// input ("i0e"), so without a cap an input of a few MiB would exhaust the TV's memory. A real torrent with thousands of files stays under about 100 000.
        /// </summary>
        public int MaxElements = 250000;

        /// <summary>Maximum input bytes accepted (transport files range from hundreds of bytes to a few MiB).</summary>
        public int MaxInputBytes = 32 * 1024 * 1024;

        public static readonly BencodeParseOptions Default = new BencodeParseOptions();
    }

    /// <summary>
    /// In-house bencode parser. Every returned value keeps its original byte span (BValue.GetRawBytes),
    /// essential for the infohash and content_id.
    /// Ace's rules: integers with "-0" or leading zeros and lengths with leading zeros are an error.
    /// </summary>
    public static class BencodeParser
    {
        public static BValue Parse(byte[] data)
        {
            int consumed;
            return Parse(data, 0, data == null ? 0 : data.Length, BencodeParseOptions.Default, out consumed);
        }

        public static BValue Parse(byte[] data, BencodeParseOptions options)
        {
            int consumed;
            return Parse(data, 0, data == null ? 0 : data.Length, options, out consumed);
        }

        /// <summary>Parses one value at the start of data[offset..offset+count). consumed = bytes the value occupies.</summary>
        public static BValue Parse(byte[] data, int offset, int count, BencodeParseOptions options, out int consumed)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (options == null) options = BencodeParseOptions.Default;
            if (offset < 0 || count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0) throw new BencodeException("empty input", 0);

            if (count > options.MaxInputBytes) throw new BencodeException("input too large (" + count + " bytes)", 0);
            var ctx = new Ctx { Data = data, End = offset + count, Options = options };
            int pos = offset;
            var v = ParseValue(ctx, ref pos, 0);
            consumed = pos - offset;
            if (!options.AllowTrailingData && pos != ctx.End)
                throw new BencodeException("data after the root value", pos);
            return v;
        }

        /// <summary>Parses and returns null instead of throwing.</summary>
        public static BValue TryParse(byte[] data, BencodeParseOptions options = null)
        {
            try { return Parse(data, options ?? BencodeParseOptions.Default); }
            catch (BencodeException) { return null; }
        }

        private sealed class Ctx
        {
            public byte[] Data;
            public int End;
            public BencodeParseOptions Options;
            public int Elements;
        }

        private static BValue ParseValue(Ctx c, ref int pos, int depth)
        {
            if (depth > c.Options.MaxDepth) throw new BencodeException("excessive nesting", pos);
            if (++c.Elements > c.Options.MaxElements) throw new BencodeException("too many elements", pos);
            if (pos >= c.End) throw new BencodeException("unexpected end of data", pos);
            int start = pos;
            byte b = c.Data[pos];
            BValue result;
            if (b == (byte)'i') result = ParseInt(c, ref pos);
            else if (b == (byte)'l') result = ParseList(c, ref pos, depth);
            else if (b == (byte)'d') result = ParseDict(c, ref pos, depth);
            else if (b >= (byte)'0' && b <= (byte)'9') result = ParseString(c, ref pos);
            else throw new BencodeException("unexpected byte 0x" + b.ToString("x2"), pos);
            result.SetRaw(c.Data, start, pos - start);
            return result;
        }

        private static BInt ParseInt(Ctx c, ref int pos)
        {
            int p = pos + 1;
            int digitsStart = p;
            bool neg = false;
            if (p < c.End && c.Data[p] == (byte)'-') { neg = true; p++; }
            int numStart = p;
            while (p < c.End && c.Data[p] >= (byte)'0' && c.Data[p] <= (byte)'9') p++;
            if (p >= c.End || c.Data[p] != (byte)'e') throw new BencodeException("unterminated integer or invalid characters", p);
            int digits = p - numStart;
            if (digits == 0) throw new BencodeException("empty integer", numStart);
            if (c.Data[numStart] == (byte)'0' && digits > 1) throw new BencodeException("integer with leading zeros", numStart);
            if (neg && c.Data[numStart] == (byte)'0') throw new BencodeException("integer '-0'", digitsStart);
            if (digits > 19) throw new BencodeException("integer too large", numStart);
            ulong acc = 0;
            for (int i = numStart; i < p; i++)
            {
                ulong d = (ulong)(c.Data[i] - (byte)'0');
                if (acc > (ulong.MaxValue - d) / 10) throw new BencodeException("integer too large", numStart);
                acc = acc * 10 + d;
            }
            long val;
            if (neg)
            {
                if (acc > (ulong)long.MaxValue + 1UL) throw new BencodeException("integer too large", numStart);
                val = acc == (ulong)long.MaxValue + 1UL ? long.MinValue : -(long)acc;
            }
            else
            {
                if (acc > (ulong)long.MaxValue) throw new BencodeException("integer too large", numStart);
                val = (long)acc;
            }
            pos = p + 1;
            return new BInt(val);
        }

        private static BString ParseString(Ctx c, ref int pos)
        {
            int p = pos;
            int lenStart = p;
            while (p < c.End && c.Data[p] >= (byte)'0' && c.Data[p] <= (byte)'9') p++;
            if (p >= c.End || c.Data[p] != (byte)':') throw new BencodeException("string length without ':'", p);
            int digits = p - lenStart;
            if (c.Data[lenStart] == (byte)'0' && digits > 1) throw new BencodeException("length with leading zeros", lenStart);
            if (digits > 10) throw new BencodeException("string length too large", lenStart);
            long len = 0;
            for (int i = lenStart; i < p; i++) len = len * 10 + (c.Data[i] - (byte)'0');
            p++; // ':'
            if (len > c.End - p) throw new BencodeException("string longer than the data", p);
            var bytes = new byte[len];
            Buffer.BlockCopy(c.Data, p, bytes, 0, (int)len);
            pos = p + (int)len;
            return new BString(bytes);
        }

        private static BList ParseList(Ctx c, ref int pos, int depth)
        {
            var list = new BList();
            pos++; // 'l'
            while (true)
            {
                if (pos >= c.End) throw new BencodeException("unterminated list", pos);
                if (c.Data[pos] == (byte)'e') { pos++; break; }
                list.Add(ParseValue(c, ref pos, depth + 1));
            }
            return list;
        }

        private static BDict ParseDict(Ctx c, ref int pos, int depth)
        {
            var dict = new BDict();
            pos++; // 'd'
            BString last = null;
            while (true)
            {
                if (pos >= c.End) throw new BencodeException("unterminated dictionary", pos);
                if (c.Data[pos] == (byte)'e') { pos++; break; }
                int keyStart = pos;
                if (c.Data[pos] < (byte)'0' || c.Data[pos] > (byte)'9') throw new BencodeException("dictionary key is not a string", pos);
                var key = ParseString(c, ref pos);
                key.SetRaw(c.Data, keyStart, pos - keyStart);
                if (c.Options.RequireSortedKeys && last != null && CompareBytes(last.Value, key.Value) >= 0)
                    throw new BencodeException("unsorted or duplicate keys", keyStart);
                last = key;
                if (pos >= c.End) throw new BencodeException("dictionary without a value for the key", pos);
                var value = ParseValue(c, ref pos, depth + 1);
                dict.Add(key, value);
            }
            return dict;
        }

        internal static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return a.Length == b.Length ? 0 : (a.Length < b.Length ? -1 : 1);
        }
    }
}
