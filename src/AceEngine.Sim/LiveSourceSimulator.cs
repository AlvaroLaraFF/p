using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Sim
{
    /// <summary>Behavior of a simulated peer (failures that are injected to test the client).</summary>
    public sealed class SimPeerBehavior
    {
        /// <summary>nt it announces: 2 = source, 1 = client (relay), 3 = support node.</summary>
        public int NodeType = 2;
        public int Version = 3022200;
        public bool SignExtend = true;
        public bool RequireClientSignature = false;
        /// <summary>The peer only has the pieces up to Head - LagPieces (it behaves like a relay that is behind the source).</summary>
        public int LagPieces = 0;
        public HashSet<long> Missing = new HashSet<long>();            // neither announces nor serves them
        public HashSet<long> CorruptSignature = new HashSet<long>();   // serves them with one byte of the signature changed
        public HashSet<long> CorruptContent = new HashSet<long>();     // serves them with one byte of the content changed
        public int ServeDelayMs = 0;                                   // delay before answering each REQUEST
        public long BytesPerSecond = 0;                                // 0 = no limit
        public bool NeverAnswerRequests = false;
        public bool ChokeAll = false;                                  // never sends UNCHOKE
        public bool SendTimestamps = false;                            // HAVE/BITFIELD with timestamps (real peers do not send them)
        public bool WideChunk = true;                                  // H width (v > 3002399)
        public bool BroadcastLastPiece = true;
        public bool RespondWithBtProtocolName = false;                 // answer "BitTorrent protocol" instead of AceStreamProtocol
        public bool UnsignedExtend = false;                            // emits the EXTEND without a signature (old versions)
        public string TransportType = "bt";
        /// <summary>Like a full node (seen on the real network): sends its EXTEND and a PEERS and closes the connection with CLOSE (13) without ever sending pieces.</summary>
        public bool CloseAfterExtend = false;
        /// <summary>Shows the BITFIELD and closes after 300 ms (a support node that kicks us out after introducing itself).</summary>
        public bool CloseAfterBitfield = false;
        /// <summary>The sender is stopped: the live head stays where it was on first use (there are peers and old pieces, but the position does not advance).</summary>
        public bool Frozen = false;
        /// <summary>Announces no piece (empty BITFIELD, no HAVE) even though it did the EXTEND.</summary>
        public bool NoPieces = false;
        /// <summary>Pause before answering the handshake (simulates a slow peer or a NAT with latency).</summary>
        public int HandshakeDelayMs = 0;
        /// <summary>Announces is_accessible = 0 in its EXTEND.</summary>
        public bool NotAccessible = false;
    }

    /// <summary>
    /// Simulated live sender/peer (source side) based on real captures: handshake (AceStreamProtocol), signed EXTEND as the
    /// first message, windowed BITFIELD, HAVE filtered by CHANGEPOS, PIECE by chunks, MYINFO, PING/PONG and BROADCAST_V2 of the last piece.
    /// It behaves like the original in what closes the connection and records each client violation in Violations.
    /// </summary>
    public sealed class LiveSourceSimulator : IDisposable
    {
        private readonly LiveStreamModel _m;
        private readonly SimPeerBehavior _b;
        private readonly TcpListener _l;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly byte[] _seed, _pub;
        private readonly byte[] _peerId;
        private readonly List<TcpClient> _clients = new List<TcpClient>();

        public readonly List<string> Violations = new List<string>();
        public readonly List<string> Events = new List<string>();
        public int Connections, Requests, PiecesServed, Pings, ChangePositions, Interested;
        public long BytesServed;
        public volatile bool AnyClientSignatureInvalid;
        public readonly List<long> ClientVersions = new List<long>();
        public int Port { get; private set; }
        public IPEndPoint EndPoint { get { return new IPEndPoint(IPAddress.Loopback, Port); } }
        public LiveStreamModel Model { get { return _m; } }

        public LiveSourceSimulator(LiveStreamModel model, SimPeerBehavior behavior = null, byte[] bindIp = null)
        {
            _m = model;
            _b = behavior ?? new SimPeerBehavior();
            Ed25519.GenerateKeyPair(out _seed, out _pub);
            _peerId = PeerId.Generate("R30------");
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
        }

        public SimPeerBehavior Behavior { get { return _b; } }

        private long _frozenHead = long.MinValue;
        private long Head
        {
            get
            {
                if (_b.Frozen)
                {
                    if (_frozenHead == long.MinValue) _frozenHead = _m.Head - _b.LagPieces;
                    return _frozenHead;
                }
                return _m.Head - _b.LagPieces;
            }
        }

        private void Violate(string text)
        {
            lock (Violations) Violations.Add(text);
        }

        private void Event(string text)
        {
            lock (Events) { Events.Add(text); if (Events.Count > 500) Events.RemoveAt(0); }
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                lock (_clients) _clients.Add(c);
                Interlocked.Increment(ref Connections);
                var cc = c;
                var ignored = Task.Run(() => Serve(cc));
            }
        }

        public void DropAllConnections()
        {
            lock (_clients) { foreach (var c in _clients) { try { c.Close(); } catch { } } _clients.Clear(); }
        }

        private static async Task ReadExact(Stream s, byte[] b, int n, CancellationToken ct)
        {
            int got = 0;
            while (got < n)
            {
                int r = await s.ReadAsync(b, got, n - got, ct);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
        }

        private sealed class Conn
        {
            public NetworkStream S;
            public readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);
            public bool GotExtend, Interested, Unchoked;
            public long RemotePos = -1, RemoteWindow;
            public long LastAnnounced = long.MinValue;
            public long RemoteVersion;
            public bool RemoteWide = true;
            public long NextFreeTicks;
        }

        private async Task Send(Conn c, byte[] frame)
        {
            await c.WriteLock.WaitAsync(_cts.Token);
            try { await c.S.WriteAsync(frame, 0, frame.Length, _cts.Token); }
            finally { c.WriteLock.Release(); }
        }

        private async Task Serve(TcpClient tc)
        {
            var c = new Conn();
            try
            {
                tc.NoDelay = true;
                c.S = tc.GetStream();
                var ct = _cts.Token;
                // --- handshake: 1 length byte + name + reserved + infohash + peer_id ---
                var one = new byte[1];
                await ReadExact(c.S, one, 1, ct);
                int plen = one[0];
                if (plen != 19 && plen != 17) { Violate("pstrlen " + plen); return; }
                var rest = new byte[plen + 8 + 20 + 20];
                await ReadExact(c.S, rest, rest.Length, ct);
                string pstr = Encoding.ASCII.GetString(rest, 0, plen);
                if (pstr != "BitTorrent protocol" && pstr != "AceStreamProtocol") { Violate("pstr " + pstr); return; }
                var ih = new byte[20]; Buffer.BlockCopy(rest, plen + 8, ih, 0, 20);
                if (!ih.SequenceEqual(_m.InfoHash)) { Event("unknown infohash " + Hex.ToHex(ih)); return; }
                if (_b.HandshakeDelayMs > 0) await Task.Delay(_b.HandshakeDelayMs, ct);
                bool ace = !_b.RespondWithBtProtocolName;
                var resp = new List<byte>();
                string name = ace ? "AceStreamProtocol" : "BitTorrent protocol";
                resp.Add((byte)name.Length); resp.AddRange(Encoding.ASCII.GetBytes(name));
                resp.AddRange(ace ? new byte[8] : new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 });
                resp.AddRange(_m.InfoHash); resp.AddRange(_peerId);
                await c.S.WriteAsync(resp.ToArray(), 0, resp.Count, ct);

                // --- signed EXTEND, the first thing that is sent ---
                await Send(c, BuildExtend(((IPEndPoint)tc.Client.RemoteEndPoint).Address));
                if (_b.CloseAfterExtend)
                {
                    await Send(c, new byte[] { 0, 0, 0, 3, LiveId.Peers, 0, 0 });
                    await Send(c, new byte[] { 0, 0, 0, 1, LiveId.Close });
                    Event("CloseAfterExtend: closed after the EXTEND");
                    return;
                }
                var pump = Task.Run(() => Pump(c, tc));

                var lenBuf = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await ReadExact(c.S, lenBuf, 4, ct);
                    uint len = Wire.GetU32(lenBuf, 0);
                    if (len == 0) continue;
                    if (len > 8u * 1024 * 1024) { Violate("message too large " + len); return; }
                    var msg = new byte[len];
                    await ReadExact(c.S, msg, (int)len, ct);
                    byte id = msg[0];
                    if (!c.GotExtend && id != LiveId.Extend) { Violate("Close on message before extend (" + LiveId.Name(id) + ")"); return; }
                    if (!await Handle(c, id, msg)) return;
                }
            }
            catch (Exception) { }
            finally { try { tc.Close(); } catch { } lock (_clients) _clients.Remove(tc); }
        }

        private byte[] BuildExtend(IPAddress remote)
        {
            var o = new LiveOptions
            {
                EngineVersion = _b.Version, NodeType = _b.NodeType, Platform = 1, SignExtend = _b.SignExtend && !_b.UnsignedExtend, TransportType = _b.TransportType
            };
            var st = new LiveLocalState
            {
                Lsp = Head, Position = Head, MinPiece = Math.Max(_m.FirstIndex, Head - _m.Options.WindowPieces + 1), MaxPiece = Head, DownloadWindowEnd = Head,
                LiveWindowSize = _m.Options.WindowPieces, ListenPort = Port, Accessible = !_b.NotAccessible
            };
            if (_b.NoPieces) { st.MinPiece = -1; st.MaxPiece = -1; st.Position = -1; }
            var payload = LiveExtend.BuildPayload(o, st, 0, remote.GetAddressBytes(), (long)(_m.Elapsed), _seed, _pub);
            return LiveExtend.BuildFrame(payload);
        }

        private bool Has(long idx)
        {
            return idx >= _m.FirstIndex && idx <= Head && idx > Head - _m.Options.WindowPieces && !_b.Missing.Contains(idx);
        }

        private async Task<bool> Handle(Conn c, byte id, byte[] msg)
        {
            switch (id)
            {
                case LiveId.Extend:
                    {
                        if (c.GotExtend) return true;
                        if (msg.Length < 3 || msg[1] != 0) { Violate("EXTEND with id " + (msg.Length > 1 ? msg[1].ToString() : "?")); return false; }
                        LiveExtendInfo info;
                        if (!LiveExtend.TryParse(msg, out info)) { Violate("malformed EXTEND"); return false; }
                        if (info.HasSignature && info.SignatureValid == false) { AnyClientSignatureInvalid = true; Violate("signature verification failed, close connection"); return false; }
                        if (!info.HasSignature && _b.RequireClientSignature) { Violate("EXTEND without signature"); return false; }
                        if (info.Tt != _b.TransportType) { Violate("transport type mismatch: " + info.Tt); return false; }
                        if (info.Pv < 2) Event("old protocol version " + info.Pv);
                        lock (ClientVersions) ClientVersions.Add(info.V);
                        c.GotExtend = true;
                        c.RemoteVersion = info.V;
                        c.RemoteWide = _b.WideChunk;
                        // BITFIELD (window) right after the EXTEND, without timestamps like real peers
                        await SendBitfield(c);
                        if (_b.CloseAfterBitfield) { await Task.Delay(300); Event("CloseAfterBitfield"); return false; }
                        return true;
                    }
                case LiveId.Choke: case LiveId.Unchoke: case LiveId.NotInterested:
                    if (msg.Length != 1) { Violate("Close on bad simple message"); return false; }
                    if (id == LiveId.NotInterested) c.Interested = false;
                    return true;
                case LiveId.Interested:
                    if (msg.Length != 1) { Violate("Close on bad simple message"); return false; }
                    c.Interested = true;
                    Interlocked.Increment(ref Interested);
                    if (!_b.ChokeAll && !c.Unchoked) { c.Unchoked = true; await Send(c, LiveWire.Simple(LiveId.Unchoke)); }
                    return true;
                case LiveId.ChangePos:
                    {
                        ulong idx; uint win;
                        if (!LiveWire.TryParseChangePos(msg, out idx, out win)) { Violate("CHANGEPOS with length " + msg.Length); return false; }
                        c.RemotePos = (long)idx; c.RemoteWindow = win;
                        Interlocked.Increment(ref ChangePositions);
                        return true;
                    }
                case LiveId.Request:
                case LiveId.HpRequest:
                    {
                        ulong idx; int chunk;
                        if (!LiveWire.TryParseRequest(msg, out idx, out chunk)) { Violate("REQUEST with length " + msg.Length); return false; }
                        if (msg.Length != (c.RemoteWide ? 11 : 10)) { Violate("REQUEST with unexpected chunk width (len " + msg.Length + ", remote v " + c.RemoteVersion + ")"); return false; }
                        Interlocked.Increment(ref Requests);
                        if (!c.Interested) { Violate("REQUEST without INTERESTED"); return false; }
                        if (!c.Unchoked || _b.NeverAnswerRequests) return true;
                        if (!Has((long)idx) || chunk >= _m.Chunks) return true;
                        var rid = idx; var rch = chunk;
                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                if (_b.ServeDelayMs > 0) await Task.Delay(_b.ServeDelayMs, _cts.Token);
                                await ServeChunk(c, (long)rid, rch);
                            }
                            catch { }
                        });
                        return true;
                    }
                case LiveId.Cancel:
                    return true;
                case LiveId.Ping:
                    {
                        int seq, size;
                        if (!LiveWire.TryParsePingPong(msg, out seq, out size)) { Violate("PING with length " + msg.Length); return false; }
                        Interlocked.Increment(ref Pings);
                        await Send(c, LiveWire.Pong(seq, size));
                        return true;
                    }
                case LiveId.Pong:
                    return true;
                case LiveId.MyInfo:
                    if (LiveWire.ParseMyInfo(msg) == null) { Violate("invalid MYINFO"); return false; }
                    return true;
                case LiveId.Close:
                    return false;
                case LiveId.Have: case LiveId.Lost: case LiveId.Bitfield:
                    return true;
                default:
                    if (!LiveId.IsKnown(id)) { Violate("got_message: close on unknown message " + id); return false; }
                    return true;
            }
        }

        private async Task ServeChunk(Conn c, long idx, int chunk)
        {
            if (_b.BytesPerSecond > 0) await Throttle(c, _m.ChunkLength);
            var piece = _m.GetPiece(idx);
            if (_b.CorruptSignature.Contains(idx) || _b.CorruptContent.Contains(idx))
            {
                piece = (byte[])piece.Clone();
                if (_b.CorruptSignature.Contains(idx)) piece[piece.Length - 5] ^= 0x55;
                if (_b.CorruptContent.Contains(idx)) piece[100] ^= 0x55;
            }
            int off = chunk * _m.ChunkLength;
            int n = Math.Min(_m.ChunkLength, _m.PieceLength - off);
            var frame = LiveWire.Piece((ulong)idx, _m.Timestamp(idx), chunk, c.RemoteWide, piece, off, n);
            await Send(c, frame);
            Interlocked.Increment(ref PiecesServed);
            Interlocked.Add(ref BytesServed, n);
        }

        /// <summary>CUMULATIVE rate limit per connection: chunks go out one after another at BytesPerSecond (a slow peer but with steady progress, which has no "stall" to detect).</summary>
        private async Task Throttle(Conn c, int bytes)
        {
            long due;
            lock (c)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long start = Math.Max(now, c.NextFreeTicks);
                due = start + (long)((double)bytes / _b.BytesPerSecond * System.Diagnostics.Stopwatch.Frequency);
                c.NextFreeTicks = due;
            }
            double waitSeconds = (double)(due - System.Diagnostics.Stopwatch.GetTimestamp()) / System.Diagnostics.Stopwatch.Frequency;
            if (waitSeconds > 0.001) await Task.Delay(TimeSpan.FromSeconds(waitSeconds), _cts.Token);
        }

        private async Task SendBitfield(Conn c)
        {
            if (_b.NoPieces) return;
            long head = Head;
            long first = Math.Max(_m.FirstIndex, head - _m.Options.WindowPieces + 1);
            int nbits = (int)(head - first + 1);
            if (nbits <= 0) return;
            var bits = new byte[(nbits + 7) / 8];
            for (int j = 0; j < nbits; j++) if (Has(first + j)) bits[j >> 3] |= (byte)(0x80 >> (j & 7));
            double? t0 = null, t1 = null;
            if (_b.SendTimestamps) { t0 = _m.Timestamp(first); t1 = _m.Timestamp(head); }
            c.LastAnnounced = head;
            await Send(c, LiveWire.Bitfield((ulong)first, nbits, bits, t0, t1));
        }

        /// <summary>Announces new pieces, MYINFO every second and the last piece via BROADCAST_V2.</summary>
        private async Task Pump(Conn c, TcpClient tc)
        {
            var ct = _cts.Token;
            DateTime lastMyInfo = DateTime.MinValue;
            long lastBroadcast = long.MinValue;
            ulong seq = (ulong)Rng.Next(1000000);
            try
            {
                while (!ct.IsCancellationRequested && tc.Connected)
                {
                    await Task.Delay(40, ct);
                    if (!c.GotExtend) continue;
                    long head = Head;
                    if (c.LastAnnounced == long.MinValue) c.LastAnnounced = head;
                    for (long p = c.LastAnnounced + 1; p <= head && !_b.NoPieces; p++)
                    {
                        c.LastAnnounced = p;
                        if (_b.Missing.Contains(p)) continue;
                        // "skip before pos" / "skip after window"
                        if (c.RemotePos >= 0 && c.RemoteWindow != 0 && (p < c.RemotePos || p >= c.RemotePos + c.RemoteWindow)) continue;
                        await Send(c, LiveWire.Have((ulong)p, _b.SendTimestamps ? (double?)_m.Timestamp(p) : null));
                    }
                    if (_b.BroadcastLastPiece && head != lastBroadcast)
                    {
                        lastBroadcast = head;
                        var payload = LiveWire.LastPiecePayloadV2((ulong)head, (long)_m.Timestamp(head));
                        await Send(c, LiveWire.BroadcastV2(63, 64, new byte[] { 5, 231, 25, 132 }, Port, _peerId, seq++, 4, (ulong)DateTime.UtcNow.Ticks / 10, payload));
                    }
                    if ((DateTime.UtcNow - lastMyInfo).TotalSeconds >= 1)
                    {
                        lastMyInfo = DateTime.UtcNow;
                        var st = new LiveLocalState { Lsp = head, Position = head, MinPiece = Math.Max(_m.FirstIndex, head - _m.Options.WindowPieces + 1), MaxPiece = head, DownloadWindowEnd = head, LiveWindowSize = _m.Options.WindowPieces, Accessible = !_b.NotAccessible };
                        if (_b.NoPieces) { st.MinPiece = -1; st.MaxPiece = -1; st.Position = -1; }
                        var d = new BDict();
                        LiveExtend.AddMyInfoLetters(d, st, 0);
                        await Send(c, LiveWire.MyInfo(d));
                    }
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _l.Stop(); } catch { }
            DropAllConnections();
        }
    }

    internal static class Rng
    {
        private static readonly object Gate = new object();
        private static readonly Random R = new Random();
        public static int Next(int max) { lock (Gate) return R.Next(max); }
    }
}
