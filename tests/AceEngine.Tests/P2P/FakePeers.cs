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
using AceEngine.P2P;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Tests.P2P
{
    /// <summary>Builds synthetic torrents (random content, real SHA-1 hashes).</summary>
    internal sealed class TestTorrent
    {
        public byte[] Content;
        public int PieceLength;
        public string Name;
        public byte[] TorrentBytes;
        public byte[] InfoBytes;
        public byte[] InfoHash;
        public int PieceCount;
        public TransportFile Transport;

        public static TestTorrent Create(int length, int pieceLength, string name = "test.mp4", string announce = null, int seed = 1,
            IList<KeyValuePair<string, byte[]>> files = null)
        {
            var t = new TestTorrent { PieceLength = pieceLength, Name = name };
            if (files == null)
            {
                t.Content = new byte[length];
                new Random(seed).NextBytes(t.Content);
            }
            else
            {
                t.Content = files.SelectMany(f => f.Value).ToArray();
            }
            t.PieceCount = (t.Content.Length + pieceLength - 1) / pieceLength;
            var pieces = new byte[t.PieceCount * 20];
            using (var sha = SHA1.Create())
                for (int i = 0; i < t.PieceCount; i++)
                {
                    int n = Math.Min(pieceLength, t.Content.Length - i * pieceLength);
                    Buffer.BlockCopy(sha.ComputeHash(t.Content, i * pieceLength, n), 0, pieces, i * 20, 20);
                }
            var info = new BDict();
            info.Add("name", BValue.Str(name));
            info.Add("piece length", BValue.Int(pieceLength));
            info.Add("pieces", BValue.Bytes(pieces));
            if (files == null) info.Add("length", BValue.Int(t.Content.Length));
            else
            {
                var fl = new BList();
                foreach (var f in files)
                {
                    var fd = new BDict();
                    fd.Add("length", BValue.Int(f.Value.Length));
                    fd.Add("path", BValue.List(f.Key.Split('/').Select(x => (BValue)BValue.Str(x)).ToArray()));
                    fl.Add(fd);
                }
                info.Add("files", fl);
            }
            var root = new BDict();
            if (announce != null) root.Add("announce", BValue.Str(announce));
            root.Add("info", info);
            t.TorrentBytes = BencodeWriter.Encode(root);
            t.Transport = TransportLoader.Load(t.TorrentBytes);
            t.InfoBytes = t.Transport.Torrent.InfoRaw;
            t.InfoHash = t.Transport.Torrent.InfoHash;
            return t;
        }

        public byte[] Piece(int i)
        {
            int n = Math.Min(PieceLength, Content.Length - i * PieceLength);
            var p = new byte[n];
            Buffer.BlockCopy(Content, i * PieceLength, p, 0, n);
            return p;
        }
    }

    /// <summary>
    /// Fake peer that behaves like an Ace engine on VOD: accepts the handshake in two steps, announces
    /// BITFIELD as the first non-EXTEND message, closes the connection on PORT/Fast/unknown extension ids or REQUEST
    /// without INTERESTED, and serves ut_metadata with Ace's total_size (block size).
    /// </summary>
    internal sealed class FakeSeeder : IDisposable
    {
        private readonly TestTorrent _t;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _conns = new List<Task>();
        private readonly List<TcpClient> _clients = new List<TcpClient>();

        public bool AceStrict = true;
        public bool AdvertiseMetadata = true;
        public bool AceTotalSizeQuirk = true;
        public bool CloakBitfield = false;
        public HashSet<int> Missing = new HashSet<int>();       // pieces it neither announces nor serves
        public HashSet<int> Corrupt = new HashSet<int>();       // pieces it serves with corrupt data
        public int BlockDelayMs = 0;
        public bool NeverRespond = false;                       // accepts REQUEST and stays silent (snubbing)
        public bool ChokeAlways = false;
        public bool SendPexOf = false;
        public List<IPEndPoint> PexPeers = new List<IPEndPoint>();
        public bool UseFastBits = false;                        // announces DHT+Fast to check that the client does not send PORT/Fast
        public string PeerIdPrefix = "R30------";

        public volatile bool ForbiddenMessageSeen;
        public volatile bool RequestWithoutInterestSeen;
        public volatile bool UnknownExtSeen;
        public volatile bool BitfieldAfterOthers;
        public volatile string ClientVersion;
        public volatile bool ClientSentExtHandshake;
        public int Connections;
        public int BlocksServed;
        public int RequestsReceived;
        public int MetadataPiecesServed;
        public readonly ConcurrentList<string> Events = new ConcurrentList<string>();

        public int Port { get; private set; }
        public IPEndPoint EndPoint { get { return new IPEndPoint(Address, Port); } }

        public IPAddress Address { get; private set; }

        public FakeSeeder(TestTorrent t, IPAddress bind = null)
        {
            _t = t;
            Address = bind ?? IPAddress.Loopback;
            _listener = new TcpListener(Address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                lock (_clients) _clients.Add(c);
                Interlocked.Increment(ref Connections);
                var ignored = Task.Run(() => Handle(c));
            }
        }

        private static async Task ReadExact(NetworkStream s, byte[] buf, int count, CancellationToken ct)
        {
            int got = 0;
            while (got < count)
            {
                int n = await s.ReadAsync(buf, got, count - got, ct);
                if (n == 0) throw new EndOfStreamException();
                got += n;
            }
        }

        private async Task Handle(TcpClient c)
        {
            var ct = _cts.Token;
            var wlock = new SemaphoreSlim(1);
            try
            {
                c.NoDelay = true;
                var s = c.GetStream();
                Func<byte[], Task> send = async frame =>
                {
                    await wlock.WaitAsync(ct);
                    try { await s.WriteAsync(frame, 0, frame.Length, ct); }
                    finally { wlock.Release(); }
                };

                // Ace-style responder: reads 48, writes 68, reads 20
                var head = new byte[48];
                await ReadExact(s, head, 48, ct);
                HandshakeInfo hi;
                if (!Wire.TryParseHandshake(head, 48, out hi) || !hi.InfoHash.SequenceEqual(_t.InfoHash)) { c.Close(); return; }
                var myId = PeerId.Generate(PeerIdPrefix);
                var reserved = UseFastBits ? new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0x05 } : new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 };
                await send(Wire.BuildHandshake(reserved, _t.InfoHash, myId));
                var theirId = new byte[20];
                await ReadExact(s, theirId, 20, ct);
                Events.Add("handshake ok, peer_id " + Encoding.ASCII.GetString(theirId));

                // EXTEND handshake first (like Ace), with Ace's m
                if (hi.SupportsLtep)
                {
                    var m = new BDict();
                    m.Add("NS_CS", BValue.Int(227)); m.Add("Tr_OVERLAYSWARM", BValue.Int(253)); m.Add("ut_pex", BValue.Int(1));
                    m.Add("Tr_G2G_v2", BValue.Int(235));
                    if (AdvertiseMetadata) m.Add("ut_metadata", BValue.Int(224));
                    m.Add("Tr_LIVE_v1", BValue.Int(254));
                    var d = new BDict();
                    d.Add("m", m); d.Add("p", BValue.Int(Port)); d.Add("v", BValue.Str("ACEStream 2.0")); d.Add("e", BValue.Int(0));
                    if (AdvertiseMetadata)
                    {
                        d.Add("metadata_size", BValue.Int(_t.InfoBytes.Length));
                        d.Add("ace_metadata_version", BValue.Int(1));
                    }
                    await send(Wire.Extended(0, BencodeWriter.Encode(d)));
                }

                // BITFIELD: first non-EXTEND message. Optionally "cloaked": removes 2-4 pieces and sends them as loose HAVEs
                var bf = new Bitfield(_t.PieceCount);
                for (int i = 0; i < _t.PieceCount; i++) if (!Missing.Contains(i)) bf.Set(i);
                var cloaked = new List<int>();
                if (CloakBitfield)
                    for (int i = 0; i < Math.Min(3, _t.PieceCount); i++) if (bf[i]) { bf.Set(i, false); cloaked.Add(i); }
                await send(Wire.Bitfield(bf.ToBytes()));
                foreach (var h in cloaked) await send(Wire.Have(h));

                if (SendPexOf && PexPeers.Count > 0 && hi.SupportsLtep)
                {
                    // the client announces ut_pex=1; we wait to receive its handshake to learn the id (done below)
                }

                bool peerInterested = false, choked = true;
                bool gotAnyNonExtend = false;
                int clientPexId = 0, clientMetaId = 0;
                var lenBuf = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await ReadExact(s, lenBuf, 4, ct);
                    uint len = Wire.GetU32(lenBuf, 0);
                    if (len == 0) continue;
                    var msg = new byte[len];
                    await ReadExact(s, msg, (int)len, ct);
                    byte id = msg[0];
                    switch (id)
                    {
                        case 2:
                            gotAnyNonExtend = true;
                            peerInterested = true;
                            if (!ChokeAlways) { choked = false; await send(Wire.Unchoke()); }
                            break;
                        case 3: gotAnyNonExtend = true; peerInterested = false; break;
                        case 0: case 1: case 4: gotAnyNonExtend = true; break;
                        case 5:
                            if (gotAnyNonExtend) BitfieldAfterOthers = true;
                            gotAnyNonExtend = true;
                            break;
                        case 6:
                            {
                                gotAnyNonExtend = true;
                                Interlocked.Increment(ref RequestsReceived);
                                if (!peerInterested)
                                {
                                    RequestWithoutInterestSeen = true;
                                    if (AceStrict) { c.Close(); return; }
                                }
                                if (choked || NeverRespond) break;
                                int idx = (int)Wire.GetU32(msg, 1), begin = (int)Wire.GetU32(msg, 5), l = (int)Wire.GetU32(msg, 9);
                                if (l > 131072) { if (AceStrict) { c.Close(); return; } }
                                if (Missing.Contains(idx)) { if (AceStrict) { c.Close(); return; } break; }
                                var data = _t.Piece(idx);
                                if (Corrupt.Contains(idx)) { data = (byte[])data.Clone(); for (int i = 0; i < data.Length; i += 97) data[i] ^= 0xff; }
                                var frame = Wire.Piece(idx, begin, data, begin, l);
                                var dly = BlockDelayMs;
                                if (dly > 0) await Task.Delay(dly, ct);
                                await send(frame);
                                Interlocked.Increment(ref BlocksServed);
                                break;
                            }
                        case 7: gotAnyNonExtend = true; break;
                        case 8: gotAnyNonExtend = true; break;
                        case 9:
                        case 13: case 14: case 15: case 16: case 17:
                            ForbiddenMessageSeen = true;
                            Events.Add("forbidden message id=" + id);
                            if (AceStrict) { c.Close(); return; }
                            break;
                        case 20:
                            {
                                byte ext = msg[1];
                                if (ext == 0)
                                {
                                    ClientSentExtHandshake = true;
                                    ExtendedHandshakeInfo info;
                                    if (ExtensionMessages.TryParseExtendedHandshake(msg, 2, msg.Length - 2, out info))
                                    {
                                        ClientVersion = info.Client;
                                        clientPexId = info.UtPexId; clientMetaId = info.UtMetadataId;
                                        // Ace closes if it receives extension ids it does not have in its 'm': the client only announces ut_pex and ut_metadata
                                        foreach (var k in info.M.Keys)
                                            if (k != "ut_pex" && k != "ut_metadata") { UnknownExtSeen = true; Events.Add("unknown announced extension " + k); }
                                        if (SendPexOf && clientPexId != 0 && PexPeers.Count > 0)
                                            await send(Wire.Extended((byte)clientPexId, ExtensionMessages.BuildPex(PexPeers)));
                                    }
                                }
                                else if (ext == 224)
                                {
                                    MetadataMessage mm;
                                    if (ExtensionMessages.TryParseMetadataMessage(msg, 2, msg.Length - 2, out mm) && mm.MsgType == 0 && !mm.Full)
                                    {
                                        int start = mm.Piece * 16384;
                                        int n = Math.Min(16384, _t.InfoBytes.Length - start);
                                        if (clientMetaId == 0) { UnknownExtSeen = true; break; }
                                        long total = AceTotalSizeQuirk ? n : _t.InfoBytes.Length;      // total_size = BLOCK size in Ace
                                        await send(Wire.Extended((byte)clientMetaId, ExtensionMessages.BuildMetadataData(mm.Piece, total, _t.InfoBytes, start, n)));
                                        Interlocked.Increment(ref MetadataPiecesServed);
                                    }
                                }
                                else if (ext == 1) { }
                                else
                                {
                                    UnknownExtSeen = true;
                                    Events.Add("unknown ext id " + ext);
                                    if (AceStrict) { c.Close(); return; }
                                }
                                break;
                            }
                        default:
                            ForbiddenMessageSeen = true;
                            Events.Add("unknown message id=" + id);
                            if (AceStrict) { c.Close(); return; }
                            break;
                    }
                }
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch { } }
        }

        public void DropAllConnections()
        {
            lock (_clients) foreach (var c in _clients) try { c.Close(); } catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            DropAllConnections();
        }
    }

    internal sealed class ConcurrentList<T>
    {
        private readonly List<T> _l = new List<T>();
        public void Add(T x) { lock (_l) _l.Add(x); }
        public List<T> ToList() { lock (_l) return new List<T>(_l); }
        public override string ToString() { return string.Join("; ", ToList()); }
    }

    /// <summary>Fake HTTP tracker: answers any announce with the given list of peers.</summary>
    internal sealed class FakeHttpTracker : IDisposable
    {
        private readonly TcpListener _l;
        public readonly List<string> Requests = new List<string>();
        public List<IPEndPoint> Peers = new List<IPEndPoint>();
        public bool Fail;
        public int Port { get; private set; }
        public string AnnounceUrl { get { return "http://127.0.0.1:" + Port + "/announce"; } }

        public FakeHttpTracker()
        {
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                var cc = c;
                var ignored = Task.Run(() => Serve(cc));
            }
        }

        private async Task Serve(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var sb = new StringBuilder();
                var one = new byte[1];
                while (!sb.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one, 0, 1) == 0) return; sb.Append((char)one[0]); }
                var line = sb.ToString().Split('\n')[0];
                lock (Requests) Requests.Add(line.Trim());
                byte[] body;
                if (Fail) body = Encoding.ASCII.GetBytes("d14:failure reason9:not todaye");
                else
                {
                    var comp = new List<byte>();
                    foreach (var p in Peers) { comp.AddRange(p.Address.GetAddressBytes()); comp.Add((byte)(p.Port >> 8)); comp.Add((byte)p.Port); }
                    var d = new BDict();
                    d.Add("interval", BValue.Int(60)); d.Add("complete", BValue.Int(1)); d.Add("incomplete", BValue.Int(0));
                    d.Add("peers", BValue.Bytes(comp.ToArray()));
                    body = BencodeWriter.Encode(d);
                }
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head, 0, head.Length);
                await s.WriteAsync(body, 0, body.Length);
            }
            catch { }
            finally { c.Close(); }
        }

        public void Dispose() { try { _l.Stop(); } catch { } }
    }

    /// <summary>Fake UDP tracker (BEP15).</summary>
    internal sealed class FakeUdpTracker : IDisposable
    {
        private readonly UdpClient _udp;
        public List<IPEndPoint> Peers = new List<IPEndPoint>();
        public int Announces;
        public int DropFirstN;           // ignores the first N datagrams (retry test)
        private int _seen;
        public int Port { get; private set; }

        public FakeUdpTracker()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(); } catch { return; }
                if (Interlocked.Increment(ref _seen) <= DropFirstN) continue;
                var b = r.Buffer;
                if (b.Length >= 16 && Wire.GetU64(b, 0) == 0x41727101980UL && Wire.GetU32(b, 8) == 0)
                {
                    var resp = new byte[16];
                    Wire.PutU32(resp, 0, 0); Wire.PutU32(resp, 4, Wire.GetU32(b, 12)); Wire.PutU64(resp, 8, 0x1122334455667788UL);
                    await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
                else if (b.Length >= 98 && Wire.GetU64(b, 0) == 0x1122334455667788UL && Wire.GetU32(b, 8) == 1)
                {
                    Interlocked.Increment(ref Announces);
                    var resp = new byte[20 + Peers.Count * 6];
                    Wire.PutU32(resp, 0, 1); Wire.PutU32(resp, 4, Wire.GetU32(b, 12)); Wire.PutU32(resp, 8, 1800);
                    Wire.PutU32(resp, 12, 1); Wire.PutU32(resp, 16, 2);
                    int o = 20;
                    foreach (var p in Peers)
                    {
                        Buffer.BlockCopy(p.Address.GetAddressBytes(), 0, resp, o, 4);
                        resp[o + 4] = (byte)(p.Port >> 8); resp[o + 5] = (byte)p.Port; o += 6;
                    }
                    await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
            }
        }

        public void Dispose() { _udp.Dispose(); }
    }

    /// <summary>Fake DHT node: answers get_peers with empty 'values' and 'nodes' or with another node.</summary>
    internal sealed class FakeDhtNode : IDisposable
    {
        private readonly UdpClient _udp;
        public List<IPEndPoint> Peers = new List<IPEndPoint>();
        public FakeDhtNode Next;                  // node it refers to in 'nodes'
        public int GetPeersReceived;
        public readonly List<string> Hashes = new List<string>();
        public byte[] Id = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();
        public int Port { get; private set; }

        public FakeDhtNode()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (true)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(); } catch { return; }
                var d = BencodeParser.TryParse(r.Buffer) as BDict;
                if (d == null || d.GetString("y") != "q") continue;
                var t = d.GetBytes("t");
                var q = d.GetString("q");
                var reply = new BDict();
                reply.Add("t", BValue.Bytes(t));
                reply.Add("y", BValue.Str("r"));
                var rr = new BDict();
                rr.Add("id", BValue.Bytes(Id));
                if (q == "get_peers")
                {
                    Interlocked.Increment(ref GetPeersReceived);
                    var qa = d.GetDict("a");
                    var qh = qa == null ? null : qa.GetBytes("info_hash");
                    if (qh != null) lock (Hashes) Hashes.Add(AceEngine.Util.Hex.ToHex(qh));
                    rr.Add("token", BValue.Str("tok"));
                    if (Peers.Count > 0)
                        rr.Add("values", BValue.List(Peers.Select(p =>
                        {
                            var b = p.Address.GetAddressBytes().Concat(new[] { (byte)(p.Port >> 8), (byte)p.Port }).ToArray();
                            return (BValue)BValue.Bytes(b);
                        }).ToArray()));
                    if (Next != null)
                    {
                        var node = Next.Id.Concat(IPAddress.Loopback.GetAddressBytes()).Concat(new[] { (byte)(Next.Port >> 8), (byte)Next.Port }).ToArray();
                        rr.Add("nodes", BValue.Bytes(node));
                    }
                    else rr.Add("nodes", BValue.Bytes(new byte[0]));
                }
                reply.Add("r", rr);
                var bytes = BencodeWriter.Encode(reply);
                await _udp.SendAsync(bytes, bytes.Length, r.RemoteEndPoint);
            }
        }

        public void Dispose() { _udp.Dispose(); }
    }
}
