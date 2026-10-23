using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>What happened with the queries of one or more searches (to know whether a resolution ran out of queries because of the global cap).</summary>
    public sealed class DhtSearchStats
    {
        public long Queries, Responses, Timeouts, SlotWaits, SlotWaitMs;
        public double SlotWaitSeconds { get { return Interlocked.Read(ref SlotWaitMs) / 1000.0; } }
    }

    /// <summary>
    /// Minimal managed mainline DHT client (BEP5): only peer search (get_peers) with bootstrap, a table of
    /// known nodes and ping replies. Same format as Ace's pymdht: all messages carry 'v'.
    /// Also serves content_id resolution (content_id as infohash): GetPeersAsync only receives 20 bytes.
    /// </summary>
    public sealed class DhtClient : IDisposable
    {
        private const string Tag = "dht";
        private const int K = 8;
        /// <summary>Parallel queries of each search (alpha).</summary>
        public const int SearchWidth = 8;
        private const int Alpha = SearchWidth;

        private sealed class Node
        {
            public byte[] Id;
            public IPEndPoint EndPoint;
            public bool Queried, Responded;
            public byte[] Distance;
        }

        private sealed class Pending
        {
            public IPEndPoint EndPoint;
            public TaskCompletionSource<BDict> Tcs = new TaskCompletionSource<BDict>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly EngineOptions _opt;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly Dictionary<string, Node> _table = new Dictionary<string, Node>();     // nodes that have responded
        private UdpClient _udp;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _tid;
        private bool _started;
        private List<IPEndPoint> _bootstrap;

        public DhtClient(EngineOptions options)
        {
            _opt = options;
            _slots = new SemaphoreSlim(options.DhtMaxInFlight > 0 ? options.DhtMaxInFlight : 1 << 20);
            NodeId = new byte[20];
            var rnd = new byte[20];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(rnd);
            NodeId = rnd;
        }

        /// <summary>Caps per search: a hostile node may answer with thousands of fake nodes or peers.</summary>
        private const int MaxPeersPerSearch = 5000, MaxCandidateNodes = 3000;

        public byte[] NodeId { get; private set; }
        public int Port { get; private set; }
        public int KnownNodes { get { lock (_gate) return _table.Count; } }
        public long QueriesSent;
        public long ResponsesReceived;
        /// <summary>Queries that got no answer in time (not counting those cancelled by the searcher).</summary>
        public long QueryTimeouts;
        /// <summary>Error responses (y=e) from nodes.</summary>
        public long ErrorResponses;
        /// <summary>Queries that had to wait for a slot because there were already DhtMaxInFlight in flight.</summary>
        public long QueriesQueued;
        private SemaphoreSlim _slots;
        private int _inFlight, _maxInFlight;

        /// <summary>Queries in flight right now and the maximum observed (never exceeds DhtMaxInFlight).</summary>
        public int InFlight { get { return Volatile.Read(ref _inFlight); } }
        /// <summary>Cap on queries in flight (EngineOptions.DhtMaxInFlight).</summary>
        public int MaxInFlight { get { return _opt.DhtMaxInFlight > 0 ? _opt.DhtMaxInFlight : int.MaxValue; } }
        public int MaxInFlightObserved { get { return Volatile.Read(ref _maxInFlight); } }

        public void Start(int port = 0)
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            var udp = new UdpClient(AddressFamily.InterNetwork);
            try { udp.Client.ReceiveBufferSize = 1024 * 1024; } catch { }       // with many searches at once the responses arrive in bursts; the default buffer (208 KiB) loses them
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp = udp;
            Port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            AceLog.Info(Tag, "DHT on UDP " + Port + ", id " + Hex.ToHex(NodeId).Substring(0, 8));
            var ignored = Task.Run(ReceiveLoopAsync);
        }

        // ---------- Persistent node table ----------

        /// <summary>
        /// Saves the nodes that responded (up to 'max', the most recent) to a small JSON. With an empty table each run starts from the 4 bootstrap routers and takes a few seconds
        /// to have nodes close to what it is looking for (the TV recreates the engine every time the app is paused).
        /// </summary>
        public void SaveNodes(string path, int max = 200)
        {
            List<Node> nodes;
            lock (_gate) nodes = _table.Values.Where(n => n.Id != null && n.Id.Length == 20).Reverse().Take(max).ToList();
            if (nodes.Count == 0) return;
            var list = new List<object>();
            foreach (var n in nodes) list.Add(new List<object> { Hex.ToHex(n.Id), n.EndPoint.ToString() });
            var root = new Dictionary<string, object>();
            root["v"] = 1L; root["nodes"] = list;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, MiniJson.Stringify(root), new System.Text.UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(tmp, path);
        }

        /// <summary>Loads nodes saved by SaveNodes into the table (existing ones are not overwritten). Returns how many went in. Dead ones discard themselves by not answering.</summary>
        public int LoadNodes(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length > 256 * 1024) return 0;
                object v;
                if (!MiniJson.TryParse(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8), out v)) return 0;
                var arr = MiniJson.GetArray(MiniJson.AsObject(v), "nodes");
                if (arr == null) return 0;
                int added = 0;
                lock (_gate)
                {
                    foreach (var item in arr)
                    {
                        var pair = item as List<object>;
                        if (pair == null || pair.Count != 2) continue;
                        var idHex = pair[0] as string; var epText = pair[1] as string;
                        string host; int port; IPAddress ip;
                        if (idHex == null || epText == null || !Hex.IsHex40(idHex)) continue;
                        if (!Net.TryParseHostPort(epText, out host, out port) || !IPAddress.TryParse(host, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || port <= 0) continue;
                        var ep = new IPEndPoint(ip, port);
                        if (_table.ContainsKey(ep.ToString())) continue;
                        if (_table.Count >= 400) break;
                        _table[ep.ToString()] = new Node { Id = Hex.Parse(idHex), EndPoint = ep, Responded = true };
                        added++;
                    }
                }
                return added;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        /// <summary>Adds a known node (e.g. 'nodes' from the .torrent).</summary>
        public async Task AddNodeAsync(string host, int port)
        {
            var ip = await Net.ResolveIPv4Async(host, _cts.Token).ConfigureAwait(false);
            if (ip == null) return;
            lock (_gate)
            {
                if (_bootstrap == null) _bootstrap = new List<IPEndPoint>();
                var ep = new IPEndPoint(ip, port);
                if (!_bootstrap.Any(e => e.Equals(ep))) _bootstrap.Add(ep);
            }
        }

        private async Task<List<IPEndPoint>> GetBootstrapAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_bootstrap != null && _bootstrapResolved) return new List<IPEndPoint>(_bootstrap);
            }
            var list = new List<IPEndPoint>();
            var tasks = new List<Task<IPAddress>>();
            var ports = new List<int>();
            foreach (var hp in _opt.DhtBootstrap)
            {
                string host; int port;
                if (!Net.TryParseHostPort(hp, out host, out port)) continue;
                tasks.Add(Net.ResolveIPv4Async(host, ct));
                ports.Add(port);
            }
            for (int i = 0; i < tasks.Count; i++)
            {
                var ip = await tasks[i].ConfigureAwait(false);
                if (ip != null) list.Add(new IPEndPoint(ip, ports[i]));
            }
            lock (_gate)
            {
                if (_bootstrap == null) _bootstrap = new List<IPEndPoint>();
                foreach (var ep in list) if (!_bootstrap.Any(e => e.Equals(ep))) _bootstrap.Add(ep);
                _bootstrapResolved = true;
                return new List<IPEndPoint>(_bootstrap);
            }
        }

        private bool _bootstrapResolved;

        // ---------- Receive ----------

        private async Task ReceiveLoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (ct.IsCancellationRequested) return; await Task.Delay(50).ConfigureAwait(false); continue; }
                try { HandleDatagram(r.Buffer, r.RemoteEndPoint); }
                catch (Exception ex) { AceLog.Debug(Tag, "datagram ignored: " + ex.Message); }
            }
        }

        private void HandleDatagram(byte[] data, IPEndPoint from)
        {
            BValue v = BencodeParser.TryParse(data);
            var d = v as BDict;
            if (d == null) return;
            var y = d.GetString("y");
            var t = d.GetBytes("t");
            if (t == null || t.Length == 0 || t.Length > 20) return;       // the transaction id is short; a long one would be a traffic reflection
            if (y == "r" || y == "e")
            {
                Pending p;
                string key = Hex.ToHex(t);
                lock (_gate)
                {
                    if (!_pending.TryGetValue(key, out p)) return;
                    if (!p.EndPoint.Equals(from)) return;            // only the queried node's response is accepted
                    _pending.Remove(key);
                }
                if (y == "r") { Interlocked.Increment(ref ResponsesReceived); p.Tcs.TrySetResult(d); }
                else { Interlocked.Increment(ref ErrorResponses); p.Tcs.TrySetResult(null); }
            }
            else if (y == "q")
            {
                var q = d.GetString("q");
                var reply = new BDict();
                reply.Add("t", BValue.Bytes(t));
                reply.Add("v", BValue.Bytes(_opt.DhtVersion));
                if (q == "ping")
                {
                    reply.Add("y", BValue.Str("r"));
                    var r = new BDict(); r.Add("id", BValue.Bytes(NodeId));
                    reply.Add("r", r);
                }
                else
                {
                    reply.Add("y", BValue.Str("e"));
                    reply.Add("e", BValue.List(BValue.Int(204), BValue.Str("Method Unknown")));
                }
                var bytes = BencodeWriter.Encode(reply);
                try { _udp.SendAsync(bytes, bytes.Length, from); } catch { }
            }
        }

        // ---------- Queries ----------

        private async Task<BDict> QueryAsync(IPEndPoint ep, string method, BDict args, TimeSpan timeout, CancellationToken ct, DhtSearchStats stats = null)
        {
            // global cap on queries in flight (EngineOptions.DhtMaxInFlight): the query timeout starts when the slot is obtained
            if (!_slots.Wait(0))
            {
                Interlocked.Increment(ref QueriesQueued);
                var wsw = System.Diagnostics.Stopwatch.StartNew();
                try { await _slots.WaitAsync(ct).ConfigureAwait(false); }
                finally
                {
                    if (stats != null) { Interlocked.Increment(ref stats.SlotWaits); Interlocked.Add(ref stats.SlotWaitMs, wsw.ElapsedMilliseconds); }
                }
            }
            if (ct.IsCancellationRequested) { _slots.Release(); ct.ThrowIfCancellationRequested(); }     // the slot may have arrived just as it was cancelled: nothing is sent
            if (stats != null) Interlocked.Increment(ref stats.Queries);
            int nowInFlight = Interlocked.Increment(ref _inFlight);
            int seenMax;
            while (nowInFlight > (seenMax = Volatile.Read(ref _maxInFlight))) Interlocked.CompareExchange(ref _maxInFlight, nowInFlight, seenMax);
            try
            {
                var resp = await QueryCoreAsync(ep, method, args, timeout, ct).ConfigureAwait(false);
                if (stats != null) { if (resp != null) Interlocked.Increment(ref stats.Responses); else if (!ct.IsCancellationRequested) Interlocked.Increment(ref stats.Timeouts); }
                return resp;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _slots.Release();
            }
        }

        private async Task<BDict> QueryCoreAsync(IPEndPoint ep, string method, BDict args, TimeSpan timeout, CancellationToken ct)
        {
            var tidBytes = new byte[4];
            int tid = Interlocked.Increment(ref _tid);
            Wire.PutU32(tidBytes, 0, (uint)tid);
            var p = new Pending { EndPoint = ep };
            string key = Hex.ToHex(tidBytes);
            lock (_gate) _pending[key] = p;

            var msg = new BDict();
            msg.Add("t", BValue.Bytes(tidBytes));
            msg.Add("y", BValue.Str("q"));
            msg.Add("q", BValue.Str(method));
            msg.Add("a", args);
            msg.Add("v", BValue.Bytes(_opt.DhtVersion));
            var bytes = BencodeWriter.Encode(msg);
            try
            {
                Interlocked.Increment(ref QueriesSent);
                await _udp.SendAsync(bytes, bytes.Length, ep).ConfigureAwait(false);
                var done = await Task.WhenAny(p.Tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                if (done == p.Tcs.Task) return p.Tcs.Task.Result;
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref QueryTimeouts);
                return null;
            }
            catch (SocketException) { return null; }
            catch (ObjectDisposedException) { return null; }
            finally
            {
                lock (_gate) _pending.Remove(key);
            }
        }

        /// <summary>Pings a node (diagnostic utility).</summary>
        public async Task<bool> PingAsync(IPEndPoint ep, CancellationToken ct)
        {
            var a = new BDict(); a.Add("id", BValue.Bytes(NodeId));
            var r = await QueryAsync(ep, "ping", a, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return r != null;
        }

        /// <summary>
        /// Iterative peer search for an infohash (BEP5). Calls onPeers with each batch of new peers.
        /// Ends by convergence, by time or by cancellation. Returns the number of distinct peers found.
        /// </summary>
        public async Task<int> GetPeersAsync(byte[] infoHash, Action<IList<IPEndPoint>> onPeers, TimeSpan timeout, CancellationToken ct, DhtSearchStats stats = null)
        {
            if (!_started) throw new InvalidOperationException("DHT not started");
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token))
            {
                cts.CancelAfter(timeout);
                var token = cts.Token;
                var cand = new Dictionary<string, Node>();
                var peersSeen = new HashSet<string>();
                int inFlight = 0;
                int queries = 0;
                var gate = new object();

                // Seed: table of the closest known nodes and bootstrap.
                lock (_gate)
                {
                    foreach (var n in _table.Values)
                    {
                        var c = new Node { Id = n.Id, EndPoint = n.EndPoint, Distance = Xor(n.Id, infoHash) };
                        cand[n.EndPoint.ToString()] = c;
                    }
                }
                List<IPEndPoint> boot = null;
                try { boot = await GetBootstrapAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return 0; }
                lock (gate)
                {
                    foreach (var ep in boot)
                    {
                        if (!cand.ContainsKey(ep.ToString()))
                            cand[ep.ToString()] = new Node { Id = null, EndPoint = ep, Distance = MaxDistance() };
                    }
                }
                AceLog.Debug(Tag, "get_peers " + Hex.ToHex(infoHash).Substring(0, 8) + ": " + cand.Count + " initial candidates");

                Func<Node> pick = () =>
                {
                    lock (gate)
                    {
                        // If K have already responded, only those closer than the K-th are queried.
                        byte[] bound = null;
                        var responded = cand.Values.Where(n => n.Responded).OrderBy(n => n.Distance, DistanceComparer.Instance).ToList();
                        if (responded.Count >= K) bound = responded[K - 1].Distance;
                        Node best = null;
                        foreach (var n in cand.Values)
                        {
                            if (n.Queried) continue;
                            if (bound != null && DistanceComparer.Instance.Compare(n.Distance, bound) >= 0) continue;
                            if (best == null || DistanceComparer.Instance.Compare(n.Distance, best.Distance) < 0) best = n;
                        }
                        if (best != null) { best.Queried = true; inFlight++; queries++; }
                        return best;
                    }
                };

                Func<Task> worker = async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var n = pick();
                        if (n == null)
                        {
                            bool others;
                            lock (gate) others = inFlight > 0;
                            if (!others) return;
                            await Task.Delay(40, token).ConfigureAwait(false);
                            continue;
                        }
                        try
                        {
                            var a = new BDict();
                            a.Add("id", BValue.Bytes(NodeId));
                            a.Add("info_hash", BValue.Bytes(infoHash));
                            var resp = await QueryAsync(n.EndPoint, "get_peers", a, TimeSpan.FromSeconds(3), token, stats).ConfigureAwait(false);
                            var r = resp == null ? null : resp.GetDict("r");
                            if (r != null)
                            {
                                lock (gate) n.Responded = true;
                                var id = r.GetBytes("id");
                                if (id != null && id.Length == 20)
                                {
                                    n.Id = id;
                                    n.Distance = Xor(id, infoHash);
                                    lock (_gate)
                                    {
                                        _table[n.EndPoint.ToString()] = new Node { Id = id, EndPoint = n.EndPoint, Responded = true };
                                        if (_table.Count > 400) _table.Remove(_table.Keys.First());
                                    }
                                }
                                var values = r.GetList("values");
                                if (values != null)
                                {
                                    var fresh = new List<IPEndPoint>();
                                    foreach (var v in values.Items)
                                    {
                                        var s = v as BString;
                                        if (s == null || s.Count != 6) continue;
                                        if (peersSeen.Count >= MaxPeersPerSearch) break;
                                        var pb = s.Value;
                                        int port = (pb[4] << 8) | pb[5];
                                        if (port == 0) continue;
                                        var ep = new IPEndPoint(new IPAddress(new[] { pb[0], pb[1], pb[2], pb[3] }), port);
                                        lock (gate) if (!peersSeen.Add(ep.ToString())) continue;
                                        fresh.Add(ep);
                                    }
                                    if (fresh.Count > 0) { try { onPeers(fresh); } catch (Exception ex) { AceLog.Warn(Tag, "onPeers: " + ex.Message); } }
                                }
                                var nodes = r.GetBytes("nodes");
                                if (nodes != null)
                                {
                                    lock (gate)
                                    {
                                        for (int i = 0; i + 26 <= nodes.Length; i += 26)
                                        {
                                            var nid = new byte[20];
                                            Buffer.BlockCopy(nodes, i, nid, 0, 20);
                                            int port = (nodes[i + 24] << 8) | nodes[i + 25];
                                            if (port == 0) continue;
                                            var ep = new IPEndPoint(new IPAddress(new[] { nodes[i + 20], nodes[i + 21], nodes[i + 22], nodes[i + 23] }), port);
                                            var key = ep.ToString();
                                            if (cand.ContainsKey(key)) continue;
                                            if (cand.Count >= MaxCandidateNodes) break;
                                            cand[key] = new Node { Id = nid, EndPoint = ep, Distance = Xor(nid, infoHash) };
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { return; }
                        finally
                        {
                            lock (gate) inFlight--;
                        }
                        lock (gate) { if (queries > 600) return; }
                    }
                };

                var workers = new List<Task>();
                for (int i = 0; i < Alpha; i++) workers.Add(Task.Run(worker));
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                AceLog.Debug(Tag, "get_peers finished: " + peersSeen.Count + " peers, " + queries + " queries, " + cand.Values.Count(n => n.Responded) + " responses");
                return peersSeen.Count;
            }
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var r = new byte[20];
            for (int i = 0; i < 20; i++) r[i] = (byte)(a[i] ^ b[i]);
            return r;
        }

        private static byte[] MaxDistance()
        {
            var r = new byte[20];
            for (int i = 0; i < 20; i++) r[i] = 0xff;
            return r;
        }

        private sealed class DistanceComparer : IComparer<byte[]>
        {
            public static readonly DistanceComparer Instance = new DistanceComparer();
            public int Compare(byte[] x, byte[] y)
            {
                for (int i = 0; i < 20; i++)
                    if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
                return 0;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { if (_udp != null) _udp.Dispose(); } catch { }
        }
    }
}
