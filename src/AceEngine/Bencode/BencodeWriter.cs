using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AceEngine.Bencode
{
    /// <summary>Key order when serializing dictionaries.</summary>
    public enum KeyOrder
    {
        /// <summary>Standard BitTorrent order: key bytes, ordinal comparison.</summary>
        Standard,
        /// <summary>Ace order: by repr((key, value)) encoded as UTF-8.</summary>
        Ace,
        /// <summary>Insertion order (the original file's, if it comes from a parse).</summary>
        Preserve
    }

    public sealed class BencodeWriteOptions
    {
        public KeyOrder Order = KeyOrder.Standard;

        /// <summary>
        /// If true and the value comes from a parse, its raw bytes are copied instead of re-serializing.
        /// This is what must be done for the infohash and the content_id.
        /// </summary>
        public bool UseRawBytes = false;
    }

    public static class BencodeWriter
    {
        public static byte[] Encode(BValue value)
        {
            return Encode(value, new BencodeWriteOptions());
        }

        public static byte[] Encode(BValue value, BencodeWriteOptions options)
        {
            if (value == null) throw new ArgumentNullException("value");
            using (var ms = new MemoryStream())
            {
                Write(ms, value, options ?? new BencodeWriteOptions());
                return ms.ToArray();
            }
        }

        private static void Write(Stream s, BValue v, BencodeWriteOptions o)
        {
            if (o.UseRawBytes && v.HasRaw)
            {
                s.Write(v.Source, v.Offset, v.Length);
                return;
            }
            var i = v as BInt;
            if (i != null)
            {
                WriteAscii(s, "i" + i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "e");
                return;
            }
            var str = v as BString;
            if (str != null) { WriteString(s, str.Value); return; }
            var l = v as BList;
            if (l != null)
            {
                s.WriteByte((byte)'l');
                foreach (var item in l.Items) Write(s, item, o);
                s.WriteByte((byte)'e');
                return;
            }
            var d = v as BDict;
            if (d != null)
            {
                s.WriteByte((byte)'d');
                foreach (var kv in OrderEntries(d, o.Order))
                {
                    WriteString(s, kv.Key.Value);
                    Write(s, kv.Value, o);
                }
                s.WriteByte((byte)'e');
                return;
            }
            throw new ArgumentException("unsupported bencode value type: " + v.GetType().Name);
        }

        private static IEnumerable<KeyValuePair<BString, BValue>> OrderEntries(BDict d, KeyOrder order)
        {
            var list = new List<KeyValuePair<BString, BValue>>(d.Entries);
            if (order == KeyOrder.Preserve) return list;
            if (order == KeyOrder.Standard)
            {
                list.Sort((a, b) => BencodeParser.CompareBytes(a.Key.Value, b.Key.Value));
                return list;
            }
            // Ace: sort by repr((k, v)).encode(); the key always decides (keys are unique).
            var keyed = new List<KeyValuePair<byte[], KeyValuePair<BString, BValue>>>(list.Count);
            foreach (var e in list)
                keyed.Add(new KeyValuePair<byte[], KeyValuePair<BString, BValue>>(
                    Encoding.UTF8.GetBytes("(" + PythonRepr.ReprKey(e.Key) + ", "), e));
            keyed.Sort((a, b) => BencodeParser.CompareBytes(a.Key, b.Key));
            var res = new List<KeyValuePair<BString, BValue>>(keyed.Count);
            foreach (var k in keyed) res.Add(k.Value);
            return res;
        }

        private static void WriteString(Stream s, byte[] b)
        {
            WriteAscii(s, b.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
            s.Write(b, 0, b.Length);
        }

        private static void WriteAscii(Stream s, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
