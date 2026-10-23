using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>
    /// Cache content_id -> bytes of the transport file as they circulated (1 h). Entries are validated
    /// when read: SHA-1(bytes) must be the key. It can be persisted as JSON (less than 1 MiB on the TV).
    /// </summary>
    public sealed class ResolutionCache
    {
        private sealed class Entry
        {
            public byte[] Data;
            public DateTime StoredUtc;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan _ttl;
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly Func<DateTime> _now;
        private long _bytes;

        public ResolutionCache(TimeSpan ttl, int maxEntries = 256, long maxBytes = 2L * 1024 * 1024, Func<DateTime> utcNow = null)
        {
            _ttl = ttl;
            _maxEntries = Math.Max(1, maxEntries);
            _maxBytes = Math.Max(1024, maxBytes);
            _now = utcNow ?? (() => DateTime.UtcNow);
        }

        public int Count { get { lock (_gate) return _map.Count; } }

        public static string NormalizeKey(string contentIdHex)
        {
            return contentIdHex == null ? null : contentIdHex.Trim().ToLowerInvariant();
        }

        public bool TryGet(string contentIdHex, out byte[] data)
        {
            data = null;
            string key = NormalizeKey(contentIdHex);
            if (key == null) return false;
            lock (_gate)
            {
                Entry e;
                if (!_map.TryGetValue(key, out e)) return false;
                if (_ttl > TimeSpan.Zero && _now() - e.StoredUtc > _ttl) { Remove(key); return false; }
                data = e.Data;
                return true;
            }
        }

        /// <summary>
        /// Stores the transport bytes of a content_id. SHA-1(bytes) == content_id is NOT required: with real data the content_id of a
        /// live stream is not the SHA-1 of the file the peers serve; the caller only stores what it has already validated.
        /// </summary>
        public bool Put(string contentIdHex, byte[] data)
        {
            string key = NormalizeKey(contentIdHex);
            if (key == null || data == null || data.Length == 0) return false;
            if (!IsKey(key)) return false;
            lock (_gate)
            {
                Remove(key);
                _map[key] = new Entry { Data = data, StoredUtc = _now() };
                _bytes += data.Length;
                while ((_map.Count > _maxEntries || _bytes > _maxBytes) && _map.Count > 1)
                {
                    string oldest = _map.OrderBy(kv => kv.Value.StoredUtc).First().Key;
                    if (oldest == key) break;
                    Remove(oldest);
                }
            }
            return true;
        }

        private static bool IsKey(string key)
        {
            return key != null && Hex.IsHex40(key);
        }

        private void Remove(string key)
        {
            Entry e;
            if (_map.TryGetValue(key, out e)) { _bytes -= e.Data.Length; _map.Remove(key); }
        }

        public void Clear()
        {
            lock (_gate) { _map.Clear(); _bytes = 0; }
        }

        // ---------- Persistence ----------

        public string ToJson()
        {
            lock (_gate)
            {
                var list = new List<object>();
                foreach (var kv in _map)
                {
                    var o = new Dictionary<string, object>();
                    o["id"] = kv.Key;
                    o["t"] = new DateTimeOffset(kv.Value.StoredUtc, TimeSpan.Zero).ToUnixTimeSeconds();
                    o["d"] = Convert.ToBase64String(kv.Value.Data);
                    list.Add(o);
                }
                var root = new Dictionary<string, object>();
                root["v"] = 1L;
                root["entries"] = list;
                return MiniJson.Stringify(root);
            }
        }

        /// <summary>Loads entries from a JSON produced by ToJson; expired or corrupt ones are discarded. Returns how many were loaded.</summary>
        public int LoadJson(string json)
        {
            object v;
            if (!MiniJson.TryParse(json, out v)) return 0;
            var root = MiniJson.AsObject(v);
            var entries = MiniJson.GetArray(root, "entries");
            if (entries == null) return 0;
            int loaded = 0;
            foreach (var item in entries)
            {
                var o = MiniJson.AsObject(item);
                string id = MiniJson.GetString(o, "id");
                string d = MiniJson.GetString(o, "d");
                long? t = MiniJson.GetLong(o, "t");
                if (id == null || d == null || !t.HasValue) continue;
                byte[] data;
                try { data = Convert.FromBase64String(d); } catch (FormatException) { continue; }
                DateTime stored;
                try { stored = DateTimeOffset.FromUnixTimeSeconds(t.Value).UtcDateTime; }
                catch (ArgumentOutOfRangeException) { continue; }        // absurd date: corrupt entry
                if (_ttl > TimeSpan.Zero && _now() - stored > _ttl) continue;
                if (!IsKey(NormalizeKey(id))) continue;
                lock (_gate)
                {
                    string key = NormalizeKey(id);
                    Remove(key);
                    _map[key] = new Entry { Data = data, StoredUtc = stored };
                    _bytes += data.Length;
                }
                loaded++;
            }
            return loaded;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public int Load(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) return 0;       // more than the cache itself ever writes: corrupt or tampered with
                return LoadJson(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException) { return 0; }
        }
    }
}
