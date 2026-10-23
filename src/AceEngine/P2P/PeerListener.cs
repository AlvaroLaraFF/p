using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>
    /// Listens for incoming peer connections (not essential). Does the half of the handshake that Ace expects
    /// as responder: reads the first 48 bytes, routes by infohash and the swarm replies with 68 and reads the peer_id.
    /// Live handshakes (AceStreamProtocol, 17 bytes) reach the target with aceProtocolName = true.
    /// </summary>
    /// <summary>Destination of an incoming connection already identified by infohash: the VOD swarm or a live one.</summary>
    internal interface IIncomingTarget
    {
        bool Accept(TcpClient tcp, bool aceProtocolName, HandshakeInfo head);
    }

    internal sealed class PeerListener : IDisposable
    {
        private const string Tag = "listener";
        private TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<byte[], IIncomingTarget> _lookup;
        private readonly object _gate = new object();
        private readonly System.Collections.Generic.Dictionary<string, int> _pendingByIp = new System.Collections.Generic.Dictionary<string, int>();
        private int _pending;

        /// <summary>Half-open incoming handshakes at a time (in total and per IP): each holds a socket for up to 15 s, so without a cap a slow client would exhaust them.</summary>
        public const int MaxPendingHandshakes = 48, MaxPendingPerIp = 4;

        public PeerListener(Func<byte[], IIncomingTarget> lookup) { _lookup = lookup; }
        public int Port { get; private set; }

        /// <summary>
        /// Opens the listening port. With <paramref name="port"/> = 0 and <paramref name="preferredPort"/> &gt; 0 it tries that port first (8621, Ace's) and, if it is
        /// busy, uses a random one; with a specific port there is no alternative (it throws on failure).
        /// </summary>
        public void Start(int port, int preferredPort = 0)
        {
            TcpListener l = null;
            if (port == 0 && preferredPort > 0 && preferredPort < 65536)
            {
                try { l = new TcpListener(IPAddress.Any, preferredPort); l.Start(); }
                catch (SocketException ex)
                {
                    AceLog.Info(Tag, "preferred port " + preferredPort + " unavailable (" + ex.SocketErrorCode + "): using a random one");
                    try { if (l != null) l.Stop(); } catch (Exception) { }
                    l = null;
                }
            }
            if (l == null)
            {
                l = new TcpListener(IPAddress.Any, port);
                l.Start();
            }
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            AceLog.Info(Tag, "listening for peers on TCP port " + Port);
            var ignored = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
                catch (InvalidOperationException) { return; }
                var tc = c;
                string ip = null;
                try { ip = ((IPEndPoint)tc.Client.RemoteEndPoint).Address.ToString(); } catch (Exception) { }
                bool admit;
                lock (_gate)
                {
                    int perIp;
                    _pendingByIp.TryGetValue(ip ?? "?", out perIp);
                    admit = _pending < MaxPendingHandshakes && perIp < MaxPendingPerIp;
                    if (admit) { _pending++; _pendingByIp[ip ?? "?"] = perIp + 1; }
                }
                if (!admit) { try { tc.Close(); } catch { } continue; }
                var ignored = Task.Run(async () =>
                {
                    try { await HandleAsync(tc).ConfigureAwait(false); }
                    finally
                    {
                        lock (_gate)
                        {
                            _pending--;
                            int n;
                            if (_pendingByIp.TryGetValue(ip ?? "?", out n)) { if (n <= 1) _pendingByIp.Remove(ip ?? "?"); else _pendingByIp[ip ?? "?"] = n - 1; }
                        }
                    }
                });
            }
        }

        private async Task HandleAsync(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var buf = new byte[Wire.HandshakeHeadLength];
                int total;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(15));      // Ace closes at 15 s
                    // First the length byte of the protocol name: 19 (BitTorrent protocol) or 17 (AceStreamProtocol, live only)
                    if (await s.ReadAsync(buf, 0, 1, cts.Token).ConfigureAwait(false) == 0) { c.Close(); return; }
                    if (buf[0] == 19) total = Wire.HandshakeHeadLength;
                    else if (buf[0] == 17) total = 1 + 17 + 8 + 20;
                    else { c.Close(); return; }
                    int got = 1;
                    while (got < total)
                    {
                        int n = await s.ReadAsync(buf, got, total - got, cts.Token).ConfigureAwait(false);
                        if (n == 0) { c.Close(); return; }
                        got += n;
                    }
                }
                HandshakeInfo head;
                bool ace = buf[0] == 17;
                if (ace)
                {
                    if (System.Text.Encoding.ASCII.GetString(buf, 1, 17) != "AceStreamProtocol") { c.Close(); return; }
                    head = new HandshakeInfo { Reserved = new byte[8], InfoHash = new byte[20] };
                    Buffer.BlockCopy(buf, 18, head.Reserved, 0, 8);
                    Buffer.BlockCopy(buf, 26, head.InfoHash, 0, 20);
                }
                else if (!Wire.TryParseHandshake(buf, buf.Length, out head)) { c.Close(); return; }
                var target = _lookup(head.InfoHash);
                if (target == null || !target.Accept(c, ace, head)) c.Close();
            }
            catch (Exception)
            {
                try { c.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
        }
    }
}
