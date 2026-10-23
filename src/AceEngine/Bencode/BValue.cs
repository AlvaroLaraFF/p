using System;
using System.Collections.Generic;
using System.Text;

namespace AceEngine.Bencode
{
    /// <summary>
    /// Bencode value. When it comes from a parse it keeps the original buffer and the span
    /// [RawOffset, RawOffset+RawLength) it occupies, so its raw bytes can be obtained
    /// (infohash, content_id). Hand-built values have no raw span.
    /// </summary>
    public abstract class BValue
    {
        internal byte[] Source;
        internal int Offset;
        internal int Length;

        /// <summary>true if the value comes from a parse and keeps its original bytes.</summary>
        public bool HasRaw { get { return Source != null; } }

        public int RawOffset { get { return Offset; } }
        public int RawLength { get { return Length; } }

        /// <summary>Copy of this value's original bytes. Throws if there is no raw span.</summary>
        public byte[] GetRawBytes()
        {
            if (Source == null) throw new InvalidOperationException("The value does not keep raw bytes (it does not come from a parse).");
            var r = new byte[Length];
            Buffer.BlockCopy(Source, Offset, r, 0, Length);
            return r;
        }

        /// <summary>View (no copy) of this value's original bytes.</summary>
        public ArraySegment<byte> GetRawSegment()
        {
            if (Source == null) throw new InvalidOperationException("The value does not keep raw bytes (it does not come from a parse).");
            return new ArraySegment<byte>(Source, Offset, Length);
        }

        /// <summary>Value bytes: the raw ones if they exist; otherwise the standard serialization.</summary>
        public byte[] Encode()
        {
            return HasRaw ? GetRawBytes() : BencodeWriter.Encode(this);
        }

        internal void SetRaw(byte[] source, int offset, int length)
        {
            Source = source; Offset = offset; Length = length;
        }

        // Handy factories for building values by hand (tests, a hand-made TorrentDef).
        public static BInt Int(long v) { return new BInt(v); }
        public static BString Str(string s) { return new BString(Encoding.UTF8.GetBytes(s)); }
        public static BString Bytes(byte[] b) { return new BString(b); }
        public static BList List(params BValue[] items) { var l = new BList(); l.AddRange(items); return l; }
    }

    public sealed class BInt : BValue
    {
        public long Value { get; private set; }
        public BInt(long value) { Value = value; }
        public override string ToString() { return "i" + Value + "e"; }
    }

    public sealed class BString : BValue
    {
        public byte[] Value { get; private set; }
        public BString(byte[] value) { Value = value ?? new byte[0]; }
        public int Count { get { return Value.Length; } }

        /// <summary>true if the bytes are valid UTF-8.</summary>
        public bool IsUtf8()
        {
            string s;
            return TryGetUtf8(out s);
        }

        public bool TryGetUtf8(out string s)
        {
            try
            {
                s = new UTF8Encoding(false, true).GetString(Value);
                return true;
            }
            catch (ArgumentException)
            {
                s = null;
                return false;
            }
        }

        /// <summary>UTF-8 text (with substitution of invalid bytes).</summary>
        public string AsText() { return Encoding.UTF8.GetString(Value); }

        public override string ToString() { return Value.Length + ":" + AsText(); }
    }

    public sealed class BList : BValue
    {
        private readonly List<BValue> _items = new List<BValue>();
        public IList<BValue> Items { get { return _items; } }
        public int Count { get { return _items.Count; } }
        public BValue this[int index] { get { return _items[index]; } }
        public void Add(BValue v) { _items.Add(v); }
        public void AddRange(IEnumerable<BValue> vs) { _items.AddRange(vs); }
        public override string ToString() { return "l(" + _items.Count + ")e"; }
    }

    /// <summary>
    /// Bencode dictionary that keeps the order of appearance (and duplicates) of the file.
    /// Lookups return the last occurrence of a key (like a Python dict).
    /// </summary>
    public sealed class BDict : BValue
    {
        private readonly List<KeyValuePair<BString, BValue>> _entries = new List<KeyValuePair<BString, BValue>>();
        private readonly Dictionary<string, BValue> _index = new Dictionary<string, BValue>(StringComparer.Ordinal);

        public IList<KeyValuePair<BString, BValue>> Entries { get { return _entries; } }
        public int Count { get { return _entries.Count; } }

        public void Add(string key, BValue value) { Add(BValue.Str(key), value); }

        public void Add(BString key, BValue value)
        {
            _entries.Add(new KeyValuePair<BString, BValue>(key, value));
            // The index uses Latin-1 over the bytes: it is bijective and does not depend on the key being valid UTF-8.
            _index[Latin1(key.Value)] = value;
        }

        public IEnumerable<string> Keys
        {
            get { foreach (var e in _entries) yield return e.Key.AsText(); }
        }

        public bool ContainsKey(string key) { return _index.ContainsKey(Latin1(Encoding.UTF8.GetBytes(key))); }

        public bool TryGetValue(string key, out BValue value)
        {
            return _index.TryGetValue(Latin1(Encoding.UTF8.GetBytes(key)), out value);
        }

        /// <summary>Value of the key or null if it does not exist.</summary>
        public BValue Get(string key)
        {
            BValue v;
            return TryGetValue(key, out v) ? v : null;
        }

        public BValue this[string key] { get { return Get(key); } }

        public long? GetInt(string key)
        {
            var i = Get(key) as BInt;
            return i == null ? (long?)null : i.Value;
        }

        public byte[] GetBytes(string key)
        {
            var s = Get(key) as BString;
            return s == null ? null : s.Value;
        }

        public string GetString(string key)
        {
            var s = Get(key) as BString;
            return s == null ? null : s.AsText();
        }

        public BList GetList(string key) { return Get(key) as BList; }
        public BDict GetDict(string key) { return Get(key) as BDict; }

        internal static string Latin1(byte[] b)
        {
            var sb = new StringBuilder(b.Length);
            foreach (var x in b) sb.Append((char)x);
            return sb.ToString();
        }

        public override string ToString() { return "d(" + _entries.Count + ")e"; }
    }
}
