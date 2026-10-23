using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Probe
{
    /// <summary>What the live probe of a channel concludes.</summary>
    public enum LiveVerdict
    {
        /// <summary>It could not be checked (no descriptor, no time, cancelled).</summary>
        Unknown,
        /// <summary>There are connectable peers with recent pieces and the live position advances: it can be played.</summary>
        Alive,
        /// <summary>There are connectable peers with pieces, but the live position does not advance: the sender is stopped.</summary>
        Stale,
        /// <summary>There are candidates, but none completes handshake and EXTEND (NAT, full nodes, nobody serves the channel).</summary>
        Unreachable,
        /// <summary>There are connected peers, but none has recent pieces of the live stream.</summary>
        NoPieces
    }

    /// <summary>Result of the live probe: what was observed by the peers, not what trackers or the DHT announce.</summary>
    public sealed class LiveProbeResult
    {
        public LiveVerdict Verdict = LiveVerdict.Unknown;
        /// <summary>Distinct candidates known at the end (descriptor, hints, DHT) and how many were tried.</summary>
        public int Candidates, Tried;
        /// <summary>Distinct peers that reached each stage: TCP, handshake, EXTEND.</summary>
        public int TcpOk, HandshakeOk, ExtendOk;
        /// <summary>Connected peers with recent pieces of the live stream (BITFIELD/HAVE or mi.max_piece near the head).</summary>
        public int WithRecentPieces;
        /// <summary>
        /// ACTIVE CONNECTABLE peers observed: connected, with recent pieces and with the live stream advancing. It is the number shown instead of the scrape.
        /// It stops at LiveProbeOptions.TargetPeers, so "ActivePeers == TargetPeers" means "at least that many" (see Saturated).
        /// </summary>
        public int ActivePeers;
        /// <summary>The peer target was reached and it stopped: the real figure is greater or equal.</summary>
        public bool Saturated;
        /// <summary>
        /// Estimate of the connectable peers of the swarm: fraction of those tried that connected, applied to the known candidates. More honest than the scrape,
        /// but with the uncertainty of a small sample; 0 if it could not be estimated.
        /// </summary>
        public int EstimatedConnectable;
        public long HeadFirst = -1, HeadLast = -1;
        /// <summary>How many times the head of the live stream was seen to grow while observing.</summary>
        public int Advances;
        public double Seconds;
        /// <summary>Peers that answered well (to try them first when playing).</summary>
        public List<IPEndPoint> GoodPeers = new List<IPEndPoint>();
        /// <summary>Remote nodes by type (nt) among those that did the EXTEND.</summary>
        public Dictionary<int, int> NodeTypes = new Dictionary<int, int>();
        public string Detail;
        /// <summary>When the probe was done (to reuse it while it does not expire).</summary>
        public DateTime TimeUtc = DateTime.UtcNow;
        /// <summary>
        /// Consecutive times a channel that was alive has come out negative without being removed from the list (ProbeOptions.AliveGrace): 0 = this is a real verdict;
        /// 1 = it is the previous "alive" verdict, kept because the last check was negative (an isolated failure does not make a card disappear).
        /// </summary>
        public int Strikes;

        /// <summary>The previous alive verdict, kept one more cycle: it keeps the time (expired, so it is checked again in the next cycle) and notes the streak.</summary>
        public LiveProbeResult Held()
        {
            var h = (LiveProbeResult)MemberwiseClone();
            h.Strikes = Strikes + 1;
            h.GoodPeers = new List<IPEndPoint>(GoodPeers);
            h.NodeTypes = new Dictionary<int, int>(NodeTypes);
            return h;
        }

        public override string ToString()
        {
            return Verdict + " active=" + ActivePeers + (Saturated ? "+" : "") + " (cand " + Candidates + ", tried " + Tried + ", tcp " + TcpOk + ", hs " + HandshakeOk + ", ext " + ExtendOk
                + ", recent " + WithRecentPieces + ", advances " + Advances + ") " + Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }
    }

    /// <summary>Parameters of the live probe (connection and time budget per channel).</summary>
    public sealed class LiveProbeOptions
    {
        /// <summary>Connected peers with recent pieces from which it is considered checked (and it stops connecting).</summary>
        public int TargetPeers = 4;
        /// <summary>Simultaneous TCP connections of ONE probe.</summary>
        public int MaxConnections = 6;
        /// <summary>Connection attempts at most per channel.</summary>
        public int MaxAttempts = 160;
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2), HandshakeTimeout = TimeSpan.FromSeconds(3);
        /// <summary>Maximum time of the probe of a channel.</summary>
        public TimeSpan MaxTime = TimeSpan.FromSeconds(10);
        /// <summary>After this time ONE peer with recent pieces and the head advancing is enough to consider the channel alive (it does not wait to gather TargetPeers).</summary>
        public TimeSpan SoftTime = TimeSpan.FromSeconds(3.5);
        /// <summary>Minimum time a peer is observed after its EXTEND to see the head grow (extended up to 1.5 pieces if the channel emits very spaced pieces).</summary>
        public TimeSpan MinObserve = TimeSpan.FromSeconds(4);
        public TimeSpan MaxObserve = TimeSpan.FromSeconds(9);
        /// <summary>A channel is not considered stopped before this time: the peers that are still connecting may bring the missing advance.</summary>
        public TimeSpan StaleAfter = TimeSpan.FromSeconds(6);
        /// <summary>A piece is recent if it is within these seconds of the live head.</summary>
        public double RecentSeconds = 60;
        /// <summary>Also look for peers on the DHT (by infohash) during the probe.</summary>
        public bool UseDht = true;
        public TimeSpan DhtTimeout = TimeSpan.FromSeconds(8);
        /// <summary>
        /// Ask the descriptor's trackers for peers with ONE announce (numwant 50) and unregister right away with "stopped" (a tracker returns no peers on "stopped"): they yield much more than the DHT
        /// (10 % complete the handshake versus 1-5 %). It leaves no trace on the tracker. The HTTP client is only needed for http(s) trackers; udp:// ones do not need it.
        /// </summary>
        public bool UseTrackers = true;
        public int TrackerCount = 2;
        public int TrackerTimeoutSeconds = 3;

        /// <summary>Copy with another maximum deadline (the other times are bounded so that they fit in it).</summary>
        public LiveProbeOptions WithMaxTime(TimeSpan maxTime)
        {
            var c = (LiveProbeOptions)MemberwiseClone();
            c.MaxTime = maxTime;
            if (c.SoftTime > maxTime) c.SoftTime = maxTime;
            if (c.StaleAfter > maxTime) c.StaleAfter = maxTime;
            if (c.MaxObserve > maxTime) c.MaxObserve = maxTime;
            if (c.MinObserve > maxTime) c.MinObserve = maxTime;
            if (c.DhtTimeout > maxTime) c.DhtTimeout = maxTime;
            return c;
        }
    }

    /// <summary>
    /// TRUE live probe: instead of trusting what trackers and the DHT announce (peers of the last 30-60 min, almost all behind NAT or already switched off), it connects to a few
    /// peers, does the handshake and the Ace EXTEND and observes whether any peer announces recent pieces of the live stream and whether the live head ADVANCES. It does not download video.
    /// Cost: up to MaxConnections sockets for a few seconds and a few KB (BITFIELD, HAVE, MYINFO); it stops as soon as there are TargetPeers peers with pieces and advance.
    /// </summary>
    public sealed class LiveProber
    {
        private const string Tag = "liveprobe";
        private readonly EngineOptions _opt;
        private readonly DhtClient _dht;
        private readonly NodeKeys _keys;
        private readonly System.Net.Http.HttpClient _http;
        private readonly Func<int> _listenPort;

        /// <param name="http">For announces to http(s) trackers; null = udp only.</param>
        /// <param name="listenPort">Port announced to the trackers (the listener's or the one opened on the router); null = 6881.</param>
        public LiveProber(EngineOptions options, DhtClient dht, NodeKeys keys = null, System.Net.Http.HttpClient http = null, Func<int> listenPort = null)
        {
            _opt = options;
            _dht = dht;
            _keys = keys ?? NodeKeys.Generate();
            _http = http;
            _listenPort = listenPort;
        }

        private sealed class Peer
        {
            public IPEndPoint Ep;
            public string Source;
            public bool Tcp, Handshake, Extend, Bitfield;
            /// <summary>It sent something swarm-related after the EXTEND (BITFIELD, HAVE, MYINFO, BROADCAST): it is not a full node that sends PEERS and closes.</summary>
            public bool Engaged;
            public long Head = -1, HeadAtExtend = -1, MaxHave = -1;
            public int Haves;
            public int Nt = -1;
            public long V;
            public bool Advanced;
            public long ExtendMs = -1;
            public bool Done;
            /// <summary>The peer closed the conversation on its own (CLOSE, end of the connection or error) before the probe ended; false = it was still connected at the end.</summary>
            public bool Ended;
        }

        private sealed class Run
        {
            public readonly object Gate = new object();
            public readonly LiveDescriptor Desc;
            public readonly byte[] PeerId;
            public readonly List<IPEndPoint> Queue = new List<IPEndPoint>();
            public readonly Dictionary<string, string> Sources = new Dictionary<string, string>();
            public readonly HashSet<string> Known = new HashSet<string>();
            public readonly List<Peer> Peers = new List<Peer>();
            public int Tried, Active;
            /// <summary>Candidate sources (DHT, trackers) that have not finished yet: while there is any, more may arrive.</summary>
            public int PendingSources;
            public long BestHead = -1;
            public long RecentPieces;
            public double Pps;
            public int Advances;
            public long HeadFirst = -1, HeadLast = -1;
            public bool Stop;
            public byte[] ExtendFrame;
            public long StartMs;
            public Run(LiveDescriptor d, byte[] peerId) { Desc = d; PeerId = peerId; }
        }

        /// <summary>Probes a live stream. <paramref name="priority"/>: already known peers (descriptor, hints from earlier sessions), which are tried before those of the DHT.</summary>
        public async Task<LiveProbeResult> ProbeAsync(LiveDescriptor desc, IEnumerable<IPEndPoint> priority, string contentId, LiveProbeOptions po, CancellationToken ct)
        {
            if (po == null) po = new LiveProbeOptions();
            var res = new LiveProbeResult();
            if (desc == null || desc.InfoHash == null || desc.PieceLength <= 0 || desc.Bitrate <= 0) { res.Detail = "descriptor without piece_length or bitrate"; return res; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = new Run(desc, PeerId.Generate(_opt.PeerIdPrefix));
            run.Pps = (double)desc.Bitrate / desc.PieceLength;
            run.RecentPieces = (long)Math.Ceiling(po.RecentSeconds * run.Pps) + 2;
            run.StartMs = LivePeer.NowMs();
            // own EXTEND: the same for all peers (without yourip), signed only once
            var lo = _opt.Live;
            var state = new LiveLocalState { ListenPort = 0, LiveWindowSize = 100 };
            long ts = (long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            run.ExtendFrame = LiveExtend.BuildFrame(LiveExtend.BuildPayload(lo, state, lo.PeerType, null, ts, _keys.Seed, _keys.PublicKey));

            if (priority != null) AddCandidates(run, priority, "hint", true);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(po.MaxTime);
                var token = cts.Token;
                Task dhtTask = Task.CompletedTask;
                if (po.UseDht && _dht != null && _opt.EnableDht)
                {
                    var hash = desc.InfoHash;
                    lock (run.Gate) run.PendingSources++;
                    dhtTask = Task.Run(async () =>
                    {
                        try { await _dht.GetPeersAsync(hash, ps => AddCandidates(run, ps, "dht", false), po.DhtTimeout, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { AceLog.Debug(Tag, "DHT: " + ex.Message); }
                        finally { lock (run.Gate) run.PendingSources--; }
                    });
                }
                Task trackerTask = Task.CompletedTask;
                if (po.UseTrackers && desc.Trackers != null && desc.Trackers.Count > 0)
                {
                    var urls = desc.Trackers.Distinct().Where(t => TrackerClient.IsSupported(t) && (_http != null || t.StartsWith("udp", StringComparison.OrdinalIgnoreCase))).Take(Math.Max(1, po.TrackerCount)).ToList();
                    lock (run.Gate) run.PendingSources += urls.Count;
                    trackerTask = Task.WhenAll(urls.Select(u => Task.Run(async () =>
                    {
                        try { await AnnounceAsync(run, u, desc.InfoHash, po, token).ConfigureAwait(false); }
                        finally { lock (run.Gate) run.PendingSources--; }
                    })));
                }
                var workers = new List<Task>();
                for (int i = 0; i < Math.Max(1, po.MaxConnections); i++) workers.Add(Task.Run(() => WorkerAsync(run, po, token)));
                // Watcher: decides the end (target reached, or deadline)
                var judge = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(100, token).ConfigureAwait(false);
                            if (Enough(run, po)) { lock (run.Gate) run.Stop = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } return; }
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (Exception) { }
                lock (run.Gate) run.Stop = true;
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                try { await judge.ConfigureAwait(false); } catch (Exception) { }
                try { await dhtTask.ConfigureAwait(false); } catch (Exception) { }
                try { await trackerTask.ConfigureAwait(false); } catch (Exception) { }
            }
            ct.ThrowIfCancellationRequested();
            Finish(run, po, res);
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>A normal announce (gives up to 50 peers) and right away "stopped" so as not to stay registered on the tracker.</summary>
        private async Task AnnounceAsync(Run run, string url, byte[] infoHash, LiveProbeOptions po, CancellationToken ct)
        {
            int port = _listenPort == null ? 0 : _listenPort();
            if (port <= 0) port = 6881;
            uint key = (uint)Rng.Next(1, int.MaxValue) & 0xffffff;
            var req = new AnnounceRequest { InfoHash = infoHash, PeerId = run.PeerId, Port = port, Uploaded = 0, Downloaded = 0, Left = 1, Event = null, NumWant = 50 };
            bool registered = false;
            try
            {
                var res = await TrackerClient.AnnounceAsync(_http, url, req, key, ct, po.TrackerTimeoutSeconds).ConfigureAwait(false);
                registered = !res.Failed;
                if (!res.Failed) AddCandidates(run, res.Peers, "tracker", false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Debug(Tag, "announce " + url + ": " + ex.Message); }
            if (!registered) return;
            try
            {
                var stop = new AnnounceRequest { InfoHash = infoHash, PeerId = run.PeerId, Port = port, Uploaded = 0, Downloaded = 0, Left = 1, Event = "stopped", NumWant = 0 };
                // without the probe's token: even if the probe ends earlier, the unregistration is attempted (2 s at most)
                await TrackerClient.AnnounceAsync(_http, url, stop, key, CancellationToken.None, 2).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        private static void AddCandidates(Run run, IEnumerable<IPEndPoint> eps, string source, bool priority)
        {
            lock (run.Gate)
            {
                foreach (var ep in eps)
                {
                    if (ep == null || ep.Port <= 0 || ep.Address.AddressFamily != AddressFamily.InterNetwork || ep.Address.Equals(IPAddress.Any)) continue;
                    if (run.Known.Count > 2000) break;
                    if (!run.Known.Add(ep.ToString())) continue;
                    run.Sources[ep.ToString()] = source;
                    if (priority) run.Queue.Insert(0, ep); else run.Queue.Add(ep);
                }
            }
        }

        private IPEndPoint Next(Run run, LiveProbeOptions po)
        {
            lock (run.Gate)
            {
                if (run.Stop || run.Queue.Count == 0 || run.Tried >= po.MaxAttempts) return null;
                var ep = run.Queue[0];
                run.Queue.RemoveAt(0);
                run.Tried++;
                return ep;
            }
        }

        private async Task WorkerAsync(Run run, LiveProbeOptions po, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var ep = Next(run, po);
                if (ep == null)
                {
                    // Nothing to try now: more may arrive from the DHT; it waits a bit and looks again. With no queue, no pending DHT and nobody talking, there is nothing left to wait for
                    lock (run.Gate) { if (run.Stop || run.Tried >= po.MaxAttempts || (run.Queue.Count == 0 && run.PendingSources == 0 && run.Active == 0)) return; }
                    try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                    continue;
                }
                string src; lock (run.Gate) run.Sources.TryGetValue(ep.ToString(), out src);
                var p = new Peer { Ep = ep, Source = src };
                lock (run.Gate) { run.Peers.Add(p); run.Active++; }
                try { await TalkAsync(run, p, po, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AceLog.Debug(Tag, ep + ": " + ex.GetType().Name + " " + ex.Message); }
                finally { lock (run.Gate) { p.Done = true; p.Ended = !ct.IsCancellationRequested; run.Active--; } }
            }
        }

        // ---------- One peer ----------

        private async Task TalkAsync(Run run, Peer p, LiveProbeOptions po, CancellationToken ct)
        {
            using (var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true })
            {
                var connect = tcp.ConnectAsync(p.Ep.Address, p.Ep.Port).Observed();
                var done = await Task.WhenAny(connect, Task.Delay(po.ConnectTimeout, ct)).ConfigureAwait(false);
                if (done != connect) { ct.ThrowIfCancellationRequested(); try { tcp.Close(); } catch (Exception) { } return; }
                await connect.ConfigureAwait(false);       // propagates the rejection
                p.Tcp = true;
                using (var reg = ct.Register(() => { try { tcp.Close(); } catch (Exception) { } }))
                {
                    var stream = tcp.GetStream();
                    var hs = LivePeer.BuildHandshake(false, _opt.HandshakeReserved, run.Desc.InfoHash, run.PeerId);
                    await stream.WriteAsync(hs, 0, hs.Length, ct).ConfigureAwait(false);
                    // peer's handshake
                    var one = new byte[1];
                    using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        hsCts.CancelAfter(po.HandshakeTimeout);
                        try
                        {
                            await StreamUtil.ReadExactAsync(stream, one, 1, hsCts.Token).ConfigureAwait(false);
                            int plen = one[0];
                            if (plen != 19 && plen != 17) return;
                            var rest = new byte[plen + 8 + 20 + 20];
                            await StreamUtil.ReadExactAsync(stream, rest, rest.Length, hsCts.Token).ConfigureAwait(false);
                            for (int i = 0; i < 20; i++) if (rest[plen + 8 + i] != run.Desc.InfoHash[i]) return;
                        }
                        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); return; }
                        catch (System.IO.IOException) { return; }
                    }
                    p.Handshake = true;
                    // The first and only thing sent is the EXTEND; afterwards it only answers PING
                    await stream.WriteAsync(run.ExtendFrame, 0, run.ExtendFrame.Length, ct).ConfigureAwait(false);
                    var lenBuf = new byte[4];
                    var deadline = LivePeer.NowMs() + (long)po.MaxTime.TotalMilliseconds;
                    while (!ct.IsCancellationRequested)
                    {
                        await StreamUtil.ReadExactAsync(stream, lenBuf, 4, ct).ConfigureAwait(false);
                        uint len = Wire.GetU32(lenBuf, 0);
                        if (len == 0) continue;
                        if (len > 128 * 1024) return;                  // an EXTEND is a few hundred bytes and a PEERS about 5 KB: nothing legitimate comes close
                        var msg = await StreamUtil.ReadMessageAsync(stream, (int)len, ct).ConfigureAwait(false);
                        if (!Handle(run, p, msg, stream)) return;
                    }
                }
            }
        }

        /// <summary>false = finish with this peer.</summary>
        private bool Handle(Run run, Peer p, byte[] msg, System.IO.Stream stream)
        {
            byte id = msg[0];
            if (!p.Extend && id != LiveId.Extend) return false;       // anything before the EXTEND is a violation
            switch (id)
            {
                case LiveId.Extend:
                    {
                        if (p.Extend) return true;
                        LiveExtendInfo info;
                        if (msg.Length < 2 || msg[1] != 0 || !LiveExtend.TryParse(msg, out info, false)) return false;
                        if (info.Tt != null && info.Tt != _opt.Live.TransportType) return false;
                        p.Extend = true; p.Nt = info.Nt; p.V = info.V;
                        p.ExtendMs = LivePeer.NowMs();
                        long head = Math.Max(info.Lsp, Math.Max(info.MiLsp, info.MiMaxPiece));
                        lock (run.Gate)
                        {
                            p.HeadAtExtend = head; p.Head = head;
                            if (info.MiMaxPiece >= 0) p.MaxHave = info.MiMaxPiece;
                            NoteHeadLocked(run, p, head, false);
                        }
                        return true;
                    }
                case LiveId.Have:
                    {
                        ulong idx; double? ts;
                        if (!LiveWire.TryParseHave(msg, out idx, out ts)) return false;
                        lock (run.Gate) { p.Engaged = true; p.Haves++; if ((long)idx > p.MaxHave) p.MaxHave = (long)idx; NoteHeadLocked(run, p, (long)idx, true); }
                        return true;
                    }
                case LiveId.Bitfield:
                    {
                        ulong first; int nbits, off; double? t0, t1;
                        if (!LiveWire.TryParseBitfield(msg, out first, out nbits, out off, out t0, out t1)) return false;
                        long last = -1;
                        for (int j = nbits - 1; j >= 0; j--) if (LiveWire.BitAt(msg, off, j)) { last = (long)first + j; break; }
                        lock (run.Gate) { p.Engaged = true; p.Bitfield = true; if (last > p.MaxHave) p.MaxHave = last; if (last >= 0) NoteHeadLocked(run, p, last, true); }
                        return true;
                    }
                case LiveId.MyInfo:
                    {
                        var d = LiveWire.ParseMyInfo(msg);
                        if (d == null) return true;
                        p.Engaged = true;
                        var lsp = d.GetInt("r"); var mx = d.GetInt("j");
                        long claim = lsp.HasValue && lsp.Value >= 0 ? lsp.Value : (mx.HasValue ? mx.Value : -1);
                        if (mx.HasValue && mx.Value > p.MaxHave) lock (run.Gate) p.MaxHave = mx.Value;
                        if (claim >= 0) lock (run.Gate) NoteHeadLocked(run, p, claim, true);
                        return true;
                    }
                case LiveId.Broadcast:
                case LiveId.BroadcastV2:
                    {
                        LiveBroadcast bc;
                        if (LiveWire.TryParseBroadcast(msg, out bc) && (bc.MessageId == 0 || bc.MessageId == 4))
                        {
                            ulong idx; double ts;
                            if (LiveWire.TryParseLastPiece(bc.Payload, out idx, out ts)) lock (run.Gate) { p.Engaged = true; NoteHeadLocked(run, p, (long)idx, true); }
                        }
                        return true;
                    }
                case LiveId.Ping:
                    {
                        int seq, size;
                        if (LiveWire.TryParsePingPong(msg, out seq, out size))
                        {
                            var pong = LiveWire.Pong(seq, Math.Max(0, Math.Min(size, 4096)));
                            try { stream.Write(pong, 0, pong.Length); } catch (Exception) { return false; }
                        }
                        return true;
                    }
                case LiveId.Close:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>A hint of a peer's live position. 'later' = arrived after the EXTEND: if it exceeds the head that the same peer gave, the head advances.</summary>
        private static void NoteHeadLocked(Run run, Peer p, long head, bool later)
        {
            if (head < 0) return;
            if (head > p.Head)
            {
                if (later && p.Head >= 0) { p.Advanced = true; run.Advances++; }
                p.Head = head;
            }
            if (head > run.BestHead) run.BestHead = head;
            if (run.HeadFirst < 0) run.HeadFirst = head;
            if (head > run.HeadLast) run.HeadLast = head;
        }

        // ---------- Verdict ----------

        private static bool IsRecent(Run run, Peer p)
        {
            // Recent pieces = evidence that it HAS pieces (mi.max_piece of the EXTEND, BITFIELD or HAVE), not just that it knows where the head is (lsp)
            long best = run.BestHead;
            return p.Extend && p.Engaged && best >= 0 && p.MaxHave >= 0 && p.MaxHave >= best - run.RecentPieces;
        }

        /// <summary>Seconds the head must be observed to consider a sender stopped: the normal amount, or 1.5 pieces if the channel emits very spaced pieces.</summary>
        private static double ObserveNeeded(Run run, LiveProbeOptions po)
        {
            return Math.Min(po.MaxObserve.TotalSeconds, Math.Max(po.MinObserve.TotalSeconds, 2.5 / Math.Max(0.01, run.Pps)));
        }

        private bool Enough(Run run, LiveProbeOptions po)
        {
            lock (run.Gate)
            {
                int recent = 0; bool advanced = run.Advances > 0;
                long firstExtend = long.MaxValue;
                foreach (var p in run.Peers)
                    if (IsRecent(run, p)) { recent++; if (p.ExtendMs > 0 && p.ExtendMs < firstExtend) firstExtend = p.ExtendMs; }
                if (recent >= po.TargetPeers && advanced) return true;                  // checked: alive
                if (recent >= 1 && advanced && (LivePeer.NowMs() - run.StartMs) >= po.SoftTime.TotalMilliseconds) return true;
                // Stopped: several peers that STAY connected and with pieces, the head still for the whole observation time and past StaleAfter
                // (a node that shows the BITFIELD and closes right away is no good as a witness, and the peers that are still connecting may bring the advance)
                int open = 0; long firstOpen = long.MaxValue;
                foreach (var p in run.Peers)
                    if (!p.Ended && IsRecent(run, p)) { open++; if (p.ExtendMs > 0 && p.ExtendMs < firstOpen) firstOpen = p.ExtendMs; }
                if (!advanced && open >= 2 && firstOpen != long.MaxValue && (LivePeer.NowMs() - firstOpen) / 1000.0 >= ObserveNeeded(run, po) && (LivePeer.NowMs() - run.StartMs) >= po.StaleAfter.TotalMilliseconds) return true;
                return false;
            }
        }

        private void Finish(Run run, LiveProbeOptions po, LiveProbeResult res)
        {
            lock (run.Gate)
            {
                res.Candidates = run.Known.Count; res.Tried = run.Tried;
                res.HeadFirst = run.HeadFirst; res.HeadLast = run.HeadLast; res.Advances = run.Advances;
                int recent = 0;
                long now = LivePeer.NowMs();
                foreach (var p in run.Peers)
                {
                    if (p.Tcp) res.TcpOk++;
                    if (p.Handshake) res.HandshakeOk++;
                    if (p.Extend)
                    {
                        res.ExtendOk++;
                        int c; res.NodeTypes.TryGetValue(p.Nt, out c); res.NodeTypes[p.Nt] = c + 1;
                    }
                    if (IsRecent(run, p)) { recent++; res.GoodPeers.Add(p.Ep); }
                }
                res.WithRecentPieces = recent;
                bool advanced = run.Advances > 0;
                if (res.ExtendOk == 0) res.Verdict = res.Tried == 0 ? LiveVerdict.Unknown : LiveVerdict.Unreachable;
                else if (recent == 0) res.Verdict = LiveVerdict.NoPieces;
                else if (advanced) res.Verdict = LiveVerdict.Alive;
                else
                {
                    // Still head: stopped if there are peers with pieces that STAY connected and enough time has been observed (channels with very spaced pieces); otherwise, inconclusive
                    double needed = ObserveNeeded(run, po);
                    double observed = 0; int open = 0;
                    foreach (var p in run.Peers)
                        if (p.Extend && p.ExtendMs > 0 && !p.Ended && IsRecent(run, p)) { open++; observed = Math.Max(observed, (now - p.ExtendMs) / 1000.0); }
                    res.Verdict = open >= 1 && observed >= needed ? LiveVerdict.Stale : LiveVerdict.Unknown;
                    res.Detail = "observed " + observed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s of " + needed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " with " + open + " open peer(s)";
                }
                res.ActivePeers = res.Verdict == LiveVerdict.Alive ? Math.Min(recent, po.TargetPeers + 6) : 0;
                res.Saturated = res.Verdict == LiveVerdict.Alive && recent >= po.TargetPeers;
                // Estimate: success rate of the connections to the candidates that were tried, applied to those that remained (bounded)
                if (res.Tried >= 10)
                {
                    double rate = (double)res.ExtendOk / res.Tried;
                    res.EstimatedConnectable = (int)Math.Round(Math.Max(res.ExtendOk, rate * res.Candidates));
                }
                else res.EstimatedConnectable = res.ExtendOk;
            }
        }

    }
}
