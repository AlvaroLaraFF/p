using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.P2P;

namespace AceEngine.Tests.Resolve
{
    /// <summary>
    /// Fake Ace engine that serves transport files over ut_metadata: handshake, extended handshake with
    /// ace_metadata_version, ut_metadata with local id 224, 'full' response with request_hash and Ace's quirks
    /// (total_size = block size in BEP9). Closes the connection on whatever Ace would close on.
    /// </summary>
    internal sealed class FakeAceMetadataPeer : IDisposable
    {
        public enum RequestHashMode { Echo, Wrong, None }

        private readonly TcpListener _l;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Bytes served for 'full' (the transport).</summary>
        public byte[] FullData;
        /// <summary>'info' served over BEP9 (16 KiB pieces).</summary>
        public byte[] InfoData;
        public RequestHashMode HashMode = RequestHashMode.Echo;
        public bool AdvertiseAce = true;
        /// <summary>false imitates an Ace engine with a live stream: it has no 'info' and the extended handshake goes WITHOUT metadata_size (observed with real peers, 2026-10-17).</summary>
        public bool AdvertiseMetadataSize = true;
        public bool UseAceProtocolName = true;        // answers with AceStreamProtocol (like the real live-stream engines)
        public bool RejectAll;
        /// <summary>Does the extended handshake and closes on receiving the 'full' request (the real support nodes, nt=3, do this: 2026-10-17).</summary>
        public bool CloseOnFullRequest;
        /// <summary>Closes the first N connections right after accepting them (a peer that is not there yet or a restart): to test retries.</summary>
        public int DropFirstConnections;
        public int HandshakeDelayMs;
        public int Connections, FullRequests, PieceRequests, ClosedByQuirk;
        public volatile string LastClientHandshakeDict;
        public volatile string LastClientPeerId;
        public readonly List<string> Events = new List<string>();

        public int Port { get; private set; }
        private readonly IPAddress _bind;
        public IPEndPoint EndPoint { get { return new IPEndPoint(_bind, Port); } }

        /// <param name="bind">Listen address: any 127.x.y.z goes to the local loopback; the quorum counts /24 networks, so each fake peer uses its own /24.</param>
        public FakeAceMetadataPeer(string bind = "127.0.0.1")
        {
            _bind = IPAddress.Parse(bind);
            _l = new TcpListener(_bind, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                Interlocked.Increment(ref Connections);
                var cc = c;
                var ignored = Task.Run(() => Serve(cc));
            }
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

        private async Task Serve(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                if (Volatile.Read(ref Connections) <= DropFirstConnections) return;
                var s = c.GetStream();
                var ct = _cts.Token;
                var hs = new byte[68];
                await ReadExact(s, hs, 68, ct);                         // full handshake from the client (MiniBitTorrent sends it all at once)
                if (hs[0] != 19 || Encoding.ASCII.GetString(hs, 1, 19) != "BitTorrent protocol") { lock (Events) Events.Add("invalid pstr"); return; }
                if ((hs[20 + 5] & 0x10) == 0) { lock (Events) Events.Add("no LTEP"); return; }
                LastClientPeerId = Encoding.ASCII.GetString(hs, 48, 8);
                var ih = new byte[20]; Buffer.BlockCopy(hs, 28, ih, 0, 20);
                if (HandshakeDelayMs > 0) await Task.Delay(HandshakeDelayMs, ct);

                var name = UseAceProtocolName ? "AceStreamProtocol" : "BitTorrent protocol";
                var resp = new List<byte>();
                resp.Add((byte)name.Length); resp.AddRange(Encoding.ASCII.GetBytes(name));
                resp.AddRange(UseAceProtocolName ? new byte[8] : new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 });
                resp.AddRange(ih);
                resp.AddRange(Encoding.ASCII.GetBytes("R30------" + Guid.NewGuid().ToString("N").Substring(0, 11)));
                await s.WriteAsync(resp.ToArray(), 0, resp.Count, ct);

                // Ace extended handshake: ut_metadata = 224
                var m = new BDict();
                m.Add("ut_pex", BValue.Int(1));
                m.Add("ut_metadata", BValue.Int(224));
                var d = new BDict();
                d.Add("m", m); d.Add("p", BValue.Int(8621)); d.Add("v", BValue.Str("ACEStream 2.0")); d.Add("e", BValue.Int(0));
                if (AdvertiseMetadataSize)
                {
                    if (InfoData != null) d.Add("metadata_size", BValue.Int(InfoData.Length));
                    else if (FullData != null) d.Add("metadata_size", BValue.Int(FullData.Length));
                }
                if (AdvertiseAce) d.Add("ace_metadata_version", BValue.Int(1));
                var eh = Wire.Extended(0, BencodeWriter.Encode(d));
                await s.WriteAsync(eh, 0, eh.Length, ct);

                int clientMetadataId = 0;
                var lenBuf = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await ReadExact(s, lenBuf, 4, ct);
                    uint len = Wire.GetU32(lenBuf, 0);
                    if (len == 0) continue;
                    var msg = new byte[len];
                    await ReadExact(s, msg, (int)len, ct);
                    if (msg[0] != WireId.Extended) continue;
                    if (msg[1] == 0)
                    {
                        BValue v = BencodeParser.Parse(msg, 2, msg.Length - 2, new BencodeParseOptions(), out int used);
                        var cd = (BDict)v;
                        LastClientHandshakeDict = Encoding.UTF8.GetString(msg, 2, used);
                        var cm = cd.GetDict("m");
                        clientMetadataId = (int)(cm.GetInt("ut_metadata") ?? 0);
                        continue;
                    }
                    if (msg[1] != 224) { lock (Events) Events.Add("unknown extension id " + msg[1]); Interlocked.Increment(ref ClosedByQuirk); return; }
                    var req = (BDict)BencodeParser.Parse(msg, 2, msg.Length - 2, new BencodeParseOptions(), out int used2);
                    int type = (int)req.GetInt("msg_type");
                    if (type != 0) { Interlocked.Increment(ref ClosedByQuirk); return; }
                    if (RejectAll)
                    {
                        var rj = new BDict(); rj.Add("msg_type", BValue.Int(2)); rj.Add("piece", BValue.Int(req.GetInt("piece") ?? 0));
                        var f = Wire.Extended((byte)clientMetadataId, BencodeWriter.Encode(rj));
                        await s.WriteAsync(f, 0, f.Length, ct);
                        continue;
                    }
                    if ((req.GetInt("full") ?? 0) != 0)
                    {
                        Interlocked.Increment(ref FullRequests);
                        if (CloseOnFullRequest) return;
                        var rd = new BDict();
                        rd.Add("msg_type", BValue.Int(1)); rd.Add("full", BValue.Int(1)); rd.Add("total_size", BValue.Int(FullData.Length));
                        if (HashMode == RequestHashMode.Echo) rd.Add("request_hash", BValue.Bytes(ih));
                        else if (HashMode == RequestHashMode.Wrong) rd.Add("request_hash", BValue.Bytes(new byte[20]));
                        var head = BencodeWriter.Encode(rd);
                        var payload = head.Concat(FullData).ToArray();
                        var f = Wire.Extended((byte)clientMetadataId, payload);
                        await s.WriteAsync(f, 0, f.Length, ct);
                    }
                    else
                    {
                        Interlocked.Increment(ref PieceRequests);
                        int piece = (int)req.GetInt("piece");
                        int start = piece * 16384;
                        if (InfoData == null || start >= InfoData.Length) { Interlocked.Increment(ref ClosedByQuirk); return; }
                        int n = Math.Min(16384, InfoData.Length - start);
                        var rd = new BDict();
                        rd.Add("msg_type", BValue.Int(1)); rd.Add("piece", BValue.Int(piece)); rd.Add("total_size", BValue.Int(n));   // Ace quirk: block size
                        var payload = BencodeWriter.Encode(rd).Concat(InfoData.Skip(start).Take(n)).ToArray();
                        var f = Wire.Extended((byte)clientMetadataId, payload);
                        await s.WriteAsync(f, 0, f.Length, ct);
                    }
                }
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch { } }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _l.Stop(); } catch { }
        }
    }

    /// <summary>Minimal HTTP server (fake TSService): serves /gettorrent and validates the request signature as the server would.</summary>
    internal sealed class FakeTsService : IDisposable
    {
        private readonly TcpListener _l;
        public readonly List<string> Requests = new List<string>();
        public Func<IDictionary<string, string>, string> Handler;          // returns the body; null = 404
        public byte[] Secret;
        public int Status = 200;
        public int Port { get; private set; }
        public string Url { get { return "http://127.0.0.1:" + Port; } }

        public FakeTsService(byte[] secret)
        {
            Secret = secret;
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

        public static IDictionary<string, string> ParseQuery(string pathAndQuery)
        {
            var d = new Dictionary<string, string>();
            int q = pathAndQuery.IndexOf('?');
            if (q < 0) return d;
            foreach (var part in pathAndQuery.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                d[Uri.UnescapeDataString(part.Substring(0, eq).Replace('+', ' '))] = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return d;
        }

        /// <summary>Expected signature: sha1('#'.join(sorted('k=v' without _s)) + secret).</summary>
        public bool SignatureOk(IDictionary<string, string> q)
        {
            string sig;
            if (!q.TryGetValue("_s", out sig)) return false;
            var items = q.Where(kv => kv.Key != "_s").Select(kv => kv.Key + "=" + kv.Value).OrderBy(x => x, StringComparer.Ordinal);
            var data = Encoding.UTF8.GetBytes(string.Join("#", items)).Concat(Secret).ToArray();
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return AceEngine.Util.Hex.ToHex(sha.ComputeHash(data)) == sig;
        }

        private async Task Serve(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var sb = new StringBuilder();
                var one = new byte[1];
                while (!sb.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one, 0, 1) == 0) return; sb.Append((char)one[0]); }
                var line = sb.ToString().Split('\n')[0].Trim();
                lock (Requests) Requests.Add(line);
                var path = line.Split(' ')[1];
                var q = ParseQuery(path);
                string body = null;
                if (Handler != null && path.StartsWith("/gettorrent")) body = SignatureOk(q) ? Handler(q) : "";
                var bytes = Encoding.UTF8.GetBytes(body ?? "");
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + Status + " X\r\nContent-Type: text/xml\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head, 0, head.Length);
                await s.WriteAsync(bytes, 0, bytes.Length);
            }
            catch { }
            finally { c.Close(); }
        }

        public void Dispose() { try { _l.Stop(); } catch { } }
    }
}
