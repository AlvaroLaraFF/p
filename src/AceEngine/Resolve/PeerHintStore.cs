using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>
    /// KNOWN peers that worked with a channel: those that served the descriptor when resolving it, those that answered the live probe and those that served
    /// pieces in a previous session. When an already probed channel is opened the descriptor comes from the cache (without peers), so without this the live stream would start from zero
    /// with tracker and DHT candidates, of which only 5-10 % answer. Key: content_id or infohash in hexadecimal (both are stored). Expires by age.
    /// Memory only and bounded: 256 keys x 24 peers.
    /// </summary>
    public sealed class PeerHintStore
    {
        private sealed class Entry
        {
            public readonly LinkedList<IPEndPoint> Peers = new LinkedList<IPEndPoint>();
            public DateTime Updated;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _ttl;
        private readonly int _maxKeys, _maxPerKey;
        private readonly Func<DateTime> _now;

        public PeerHintStore(TimeSpan? ttl = null, int maxKeys = 256, int maxPerKey = 24, Func<DateTime> utcNow = null)
        {
            _ttl = ttl ?? TimeSpan.FromMinutes(30);
            _maxKeys = Math.Max(1, maxKeys);
            _maxPerKey = Math.Max(1, maxPerKey);
            _now = utcNow ?? (() => DateTime.UtcNow);
        }

        public int Count { get { lock (_gate) return _map.Count; } }

        /// <summary>Adds peers to a key; the most recent go first and the oldest are lost when going over the cap.</summary>
        public void Add(string key, IEnumerable<IPEndPoint> peers)
        {
            if (string.IsNullOrEmpty(key) || peers == null) return;
            lock (_gate)
            {
                Entry e;
                if (!_map.TryGetValue(key, out e))
                {
                    if (_map.Count >= _maxKeys) RemoveOldestLocked();
                    _map[key] = e = new Entry();
                }
                foreach (var p in peers)
                {
                    if (p == null || p.Port <= 0 || p.Address.Equals(IPAddress.Any)) continue;
                    var node = e.Peers.First;
                    while (node != null) { var next = node.Next; if (node.Value.Equals(p)) e.Peers.Remove(node); node = next; }
                    e.Peers.AddFirst(p);
                    while (e.Peers.Count > _maxPerKey) e.Peers.RemoveLast();
                }
                e.Updated = _now();
            }
            var h = Changed;
            if (h != null) h();
        }

        /// <summary>Known peers of a key (empty if there are none or they expired).</summary>
        public IList<IPEndPoint> Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return new List<IPEndPoint>();
            lock (_gate)
            {
                Entry e;
                if (!_map.TryGetValue(key, out e)) return new List<IPEndPoint>();
                if (_ttl > TimeSpan.Zero && _now() - e.Updated > _ttl) { _map.Remove(key); return new List<IPEndPoint>(); }
                return e.Peers.ToList();
            }
        }

        public void Remove(string key)
        {
            lock (_gate) _map.Remove(key);
        }

        // ---------- Persistence (small JSON: 256 keys x 24 peers at most) ----------

        public string ToJson()
        {
            lock (_gate)
            {
                var list = new List<object>();
                foreach (var kv in _map)
                {
                    var o = new Dictionary<string, object>();
                    o["k"] = kv.Key;
                    o["t"] = new DateTimeOffset(kv.Value.Updated, TimeSpan.Zero).ToUnixTimeSeconds();
                    o["p"] = kv.Value.Peers.Select(p => (object)p.ToString()).ToList();
                    list.Add(o);
                }
                var root = new Dictionary<string, object>();
                root["v"] = 1L;
                root["keys"] = list;
                return MiniJson.Stringify(root);
            }
        }

        /// <summary>Loads the keys of a JSON produced by ToJson; expired or corrupt ones are discarded. Returns how many were loaded.</summary>
        public int LoadJson(string json)
        {
            object v;
            if (!MiniJson.TryParse(json, out v)) return 0;
            var keys = MiniJson.GetArray(MiniJson.AsObject(v), "keys");
            if (keys == null) return 0;
            int loaded = 0;
            foreach (var item in keys)
            {
                var o = MiniJson.AsObject(item);
                string k = MiniJson.GetString(o, "k");
                long? t = MiniJson.GetLong(o, "t");
                var ps = MiniJson.GetArray(o, "p");
                if (k == null || k.Length > 64 || !t.HasValue || ps == null) continue;
                DateTime updated;
                try { updated = DateTimeOffset.FromUnixTimeSeconds(t.Value).UtcDateTime; } catch (ArgumentOutOfRangeException) { continue; }
                if (_ttl > TimeSpan.Zero && _now() - updated > _ttl) continue;
                var eps = new List<IPEndPoint>();
                foreach (var x in ps)
                {
                    var str = x as string;
                    if (str == null) continue;
                    string host; int port; IPAddress ip;
                    if (AceEngine.P2P.Net.TryParseHostPort(str, out host, out port) && IPAddress.TryParse(host, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) eps.Add(new IPEndPoint(ip, port));
                    if (eps.Count >= _maxPerKey) break;
                }
                if (eps.Count == 0) continue;
                lock (_gate)
                {
                    if (_map.Count >= _maxKeys && !_map.ContainsKey(k)) RemoveOldestLocked();
                    var e = new Entry { Updated = updated };
                    foreach (var p in eps) e.Peers.AddLast(p);
                    _map[k] = e;
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
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return 0;
                return LoadJson(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (IOException) { return 0; }
        }

        /// <summary>Fired (on the calling thread) every time the hints change: to save them with a delay.</summary>
        public event Action Changed;

        private void RemoveOldestLocked()
        {
            string oldest = null; DateTime t = DateTime.MaxValue;
            foreach (var kv in _map) if (kv.Value.Updated < t) { t = kv.Value.Updated; oldest = kv.Key; }
            if (oldest != null) _map.Remove(oldest);
        }
    }
}
