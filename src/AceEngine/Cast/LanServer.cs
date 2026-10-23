using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cast
{
    /// <summary>How the progressive MPEG-TS (the one DLNA renderers get) ends its body: renderers differ in what they accept for a stream with no length.</summary>
    public enum ProgressiveMode
    {
        /// <summary>HTTP/1.0 with no length and Connection: close. What most DLNA servers do for live streams.</summary>
        CloseDelimited,
        /// <summary>HTTP/1.1 with Transfer-Encoding: chunked.</summary>
        Chunked,
        /// <summary>HTTP/1.0 with a huge Content-Length (50 000 000 000), the "unknown duration" marker some Samsung models expect.</summary>
        BigContentLength
    }

    /// <summary>Settings of the server that exposes one channel to one device of the local network.</summary>
    public sealed class LanServerOptions
    {
        /// <summary>The address of the interface that reaches the device. Never 0.0.0.0: the server must not listen on mobile data, a hotspot or a VPN.</summary>
        public IPAddress BindAddress;
        /// <summary>0 = a random free port.</summary>
        public int Port;
        /// <summary>The only device that may connect (the Chromecast or the TV). Null = nobody, unless AllowLocalNetwork.</summary>
        public IPAddress Target;
        /// <summary>Also accept any address in the subnet of BindAddress (the option for receivers that fetch from another address than the one they announce).</summary>
        public bool AllowLocalNetwork;
        /// <summary>Mask of that subnet; null = /24.</summary>
        public IPAddress LocalNetworkMask;
        /// <summary>Simultaneous connections; the rest are closed on arrival.</summary>
        public int MaxConnections = 6;
        public TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(10);
        public TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);
        /// <summary>A write that makes no progress for this long closes the connection (a TV that pauses stops reading for a while; much longer than the live buffer holds is pointless).</summary>
        public TimeSpan WriteTimeout = TimeSpan.FromSeconds(120);
        /// <summary>The progressive stream is cut if the source gives nothing for this long.</summary>
        public TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);
        /// <summary>No request at all for this long: the server closes itself (nobody is watching any more).</summary>
        public TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
        public int MaxHeaderBytes = 4096;
        public int MaxHeaderCount = 40;
        public int MaxRequestsPerConnection = 2000;
        /// <summary>Answer the Cross-Origin Resource Sharing headers a web receiver needs (and OPTIONS).</summary>
        public bool Cors = true;

        // progressive TS
        public ProgressiveMode Progressive = ProgressiveMode.CloseDelimited;
        public string ProgressiveContentType = "video/mp2t";
        /// <summary>DLNA headers (contentFeatures.dlna.org, transferMode.dlna.org) on the progressive stream. Null = none.</summary>
        public DlnaStreamHeaders Dlna;
        public int StreamBlockBytes = 64 * 1024;

        /// <summary>Token supplied by the caller (tests); normally generated.</summary>
        public string Token;
    }

    /// <summary>The DLNA headers a renderer asks for when it opens a media URL.</summary>
    public sealed class DlnaStreamHeaders
    {
        /// <summary>DLNA.ORG_PN profile (e.g. "AVC_TS_HD_50_AAC_T"); empty = leave it out, which is what to do when the profile is not certain.</summary>
        public string ProfileName = "";
        /// <summary>DLNA.ORG_OP: "00" = no time or byte seek.</summary>
        public string Operation = "00";
        /// <summary>DLNA.ORG_FLAGS: 8 hex digits of flags plus 24 zeros.</summary>
        public string Flags = "01700000000000000000000000000000";

        public string ContentFeatures
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(ProfileName)) sb.Append("DLNA.ORG_PN=").Append(ProfileName).Append(';');
                sb.Append("DLNA.ORG_OP=").Append(Operation).Append(";DLNA.ORG_CI=0;DLNA.ORG_FLAGS=").Append(Flags);
                return sb.ToString();
            }
        }
    }

    /// <summary>What the LAN server serves: an HLS live stream, a progressive MPEG-TS stream, or both.</summary>
    internal interface ILanContent
    {
        bool OffersHls { get; }
        bool OffersProgressive { get; }
        /// <summary>Playlist text or null if there is nothing to list yet. 'ended' adds EXT-X-ENDLIST (the caller of the interface decides, the text carries it).</summary>
        string GetPlaylist(out bool ended);
        /// <summary>A master playlist with the codecs of the stream, or null (the media playlist is then the entry point).</summary>
        string GetMasterPlaylist();
        SegmentLease GetSegment(long seq);
        /// <summary>A reader of the progressive TS, positioned at the live point (or 'back' behind it).</summary>
        Task<IStreamReader> OpenProgressiveAsync(TimeSpan? back, CancellationToken ct);
        /// <summary>false once the channel is gone: the server answers 503.</summary>
        bool IsAvailable { get; }
    }

    public sealed class LanServerStats
    {
        public long Requests, PlaylistRequests, SegmentRequests, StreamRequests, Preflights, BytesSent;
        public long RejectedAddress, RejectedHost, BadToken, BadRequest, OverLimit, NotFound, Errors;
        public int ActiveConnections, PeakConnections;
        public DateTime LastRequestUtc;
        public string LastClient = "";
    }

    /// <summary>
    /// The HTTP server that lets ONE device of the local network fetch ONE channel. It is opened on demand on the address of the interface that reaches the device and
    /// closed when the sending stops. Defences: a 128-bit token in the path (wrong or missing = the same 404 as any unknown path), connections only from the target
    /// device (plus, optionally, its subnet), the Host header must be this server's own address, a small connection limit, strict request parsing, timeouts on every
    /// read and write, no directory listing, CORS only on the token routes.
    /// </summary>
    internal sealed class LanServer : IDisposable
    {
        private const string Tag = "cast-lan";
        private readonly LanServerOptions _o;
        private readonly ILanContent _content;
        private readonly string _token;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, TcpClient> _clients = new ConcurrentDictionary<int, TcpClient>();
        private TcpListener _listener;
        private Task _acceptLoop;
        private Timer _idle;
        private int _nextId, _active, _peak;
        private int _disposed;
        private readonly string _hostA, _hostB;
        private readonly object _statGate = new object();
        private long _requests, _playlists, _segments, _streams, _preflights, _bytes, _rejAddr, _rejHost, _badToken, _badReq, _overLimit, _notFound, _errors;
        private long _lastRequestTicks;
        private string _lastClient = "";
        private int _logBudget = 10;
        private DateTime _logWindow = DateTime.UtcNow;

        /// <summary>Raised once when the server closes itself (no request for IdleTimeout).</summary>
        public event Action<string> Closed;

        public LanServer(LanServerOptions options, ILanContent content)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (content == null) throw new ArgumentNullException("content");
            if (options.BindAddress == null || options.BindAddress.AddressFamily != AddressFamily.InterNetwork || options.BindAddress.Equals(IPAddress.Any))
                throw new ArgumentException("the server needs the address of one interface, not 0.0.0.0");
            if (options.Target == null && !options.AllowLocalNetwork) throw new ArgumentException("without a target device or the local network option nobody could connect");
            if (options.Target != null && options.Target.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("only IPv4 targets are supported");
            _o = options;
            _content = content;
            _token = string.IsNullOrEmpty(options.Token) ? NewToken() : options.Token;
            Address = options.BindAddress;
            _lastRequestTicks = DateTime.UtcNow.Ticks;
        }

        public IPAddress Address { get; private set; }
        public int Port { get; private set; }
        public bool IsRunning { get { return _listener != null && Volatile.Read(ref _disposed) == 0; } }
        /// <summary>The secret in the URLs. Never log it.</summary>
        public string Token { get { return _token; } }

        public string BaseUrl { get { return "http://" + Address + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/c/" + _token + "/"; } }
        public string PlaylistUrl { get { return BaseUrl + "index.m3u8"; } }
        public string MasterUrl { get { return BaseUrl + "master.m3u8"; } }
        public string StreamUrl { get { return BaseUrl + "live.ts"; } }

        /// <summary>128 random bits as 32 hex characters.</summary>
        public static string NewToken()
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Hex.ToHex(b);
        }

        public void Start()
        {
            if (_listener != null) throw new InvalidOperationException("already started");
            var l = new TcpListener(_o.BindAddress, _o.Port);
            l.Start(16);
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            // The Host header of a request to us is this address (with the port, unless it is 80)
            AceLog.Info(Tag, "LAN server listening on " + Address + ":" + Port + " for " + (_o.Target != null ? _o.Target.ToString() : "the local network") + (_o.AllowLocalNetwork && _o.Target != null ? " (and its subnet)" : ""));
            _acceptLoop = Task.Run(AcceptLoopAsync);
            _idle = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public LanServerStats GetStats()
        {
            lock (_statGate)
            {
                return new LanServerStats
                {
                    Requests = _requests, PlaylistRequests = _playlists, SegmentRequests = _segments, StreamRequests = _streams, Preflights = _preflights, BytesSent = _bytes,
                    RejectedAddress = _rejAddr, RejectedHost = _rejHost, BadToken = _badToken, BadRequest = _badReq, OverLimit = _overLimit, NotFound = _notFound, Errors = _errors,
                    ActiveConnections = Volatile.Read(ref _active), PeakConnections = Volatile.Read(ref _peak),
                    LastRequestUtc = new DateTime(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc), LastClient = _lastClient
                };
            }
        }

        /// <summary>Keeps the idle timer from firing (the sender knows the receiver is still there).</summary>
        public void Touch() { Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks); }

        private void CheckIdle()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_o.IdleTimeout <= TimeSpan.Zero) return;
            if (Volatile.Read(ref _active) > 0) { Touch(); return; }                          // someone is being served
            var last = new DateTime(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - last < _o.IdleTimeout) return;
            AceLog.Info(Tag, "no request for " + (int)_o.IdleTimeout.TotalSeconds + " s: closing the LAN server");
            var h = Closed;
            Dispose();
            if (h != null) { try { h("idle"); } catch (Exception ex) { AceLog.Warn(Tag, "Closed handler: " + ex.Message); } }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            var t = _idle; if (t != null) t.Dispose();
            try { if (_listener != null) _listener.Stop(); } catch { }
            foreach (var kv in _clients) { try { kv.Value.Close(); } catch { } }
            try { if (_acceptLoop != null) _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        // ---------- Accept ----------

        private IPAddress Remote(TcpClient c)
        {
            var ep = c.Client.RemoteEndPoint as IPEndPoint;
            if (ep == null) return null;
            var a = ep.Address;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            return a;
        }

        /// <summary>true if the address may connect: the target device, or (with the option) an address of the interface's subnet.</summary>
        internal static bool IsAllowed(IPAddress remote, LanServerOptions o)
        {
            if (remote == null || remote.AddressFamily != AddressFamily.InterNetwork) return false;
            if (o.Target != null && remote.Equals(o.Target)) return true;
            if (o.AllowLocalNetwork)
            {
                var mask = o.LocalNetworkMask ?? IPAddress.Parse("255.255.255.0");
                if (IpClass.IsLoopback(remote) && !IpClass.IsLoopback(o.BindAddress)) return false;
                if (IpClass.SameSubnet(remote, o.BindAddress, mask)) return true;
            }
            return false;
        }

        private void LogLimited(string msg)
        {
            lock (_statGate)
            {
                var now = DateTime.UtcNow;
                if (now - _logWindow > TimeSpan.FromMinutes(1)) { _logWindow = now; _logBudget = 10; }
                if (_logBudget <= 0) return;
                _logBudget--;
            }
            AceLog.Warn(Tag, msg);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
                catch (InvalidOperationException) { break; }

                var remote = Remote(client);
                if (!IsAllowed(remote, _o))
                {
                    Bump(ref _rejAddr);
                    LogLimited("connection refused from " + remote + " (not the target device)");
                    try { client.Client.LingerState = new LingerOption(true, 0); } catch { }
                    try { client.Close(); } catch { }
                    continue;
                }
                int now = Interlocked.Increment(ref _active);
                if (now > _o.MaxConnections)
                {
                    Interlocked.Decrement(ref _active);
                    Bump(ref _overLimit);
                    LogLimited("connection refused from " + remote + " (too many connections)");
                    try { client.Close(); } catch { }
                    continue;
                }
                int pk;
                while (now > (pk = Volatile.Read(ref _peak))) Interlocked.CompareExchange(ref _peak, now, pk);
                lock (_statGate) _lastClient = remote.ToString();
                var tc = client;
                var ignored = Task.Run(() => HandleConnectionAsync(tc, remote));
            }
        }

        private void Bump(ref long counter) { lock (_statGate) counter++; }

        // ---------- Connection ----------

        private sealed class Request
        {
            public string Method, Target, Path, Query;
            public bool Http10;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string n) { string v; return Headers.TryGetValue(n, out v) ? v : null; }
        }

        private sealed class BadRequest : Exception
        {
            public int Status;
            public BadRequest(int status, string msg) : base(msg) { Status = status; }
        }

        private sealed class Conn
        {
            public NetworkStream Stream;
            public byte[] Buf;
            public int Start, End;
            public bool LastWasNotFound;
        }

        private async Task HandleConnectionAsync(TcpClient client, IPAddress remote)
        {
            int id = Interlocked.Increment(ref _nextId);
            _clients[id] = client;
            try
            {
                client.NoDelay = true;
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
                var conn = new Conn { Stream = client.GetStream(), Buf = new byte[_o.MaxHeaderBytes + 1024] };
                int served = 0, strikes = 0;
                bool first = true;
                while (!_cts.IsCancellationRequested && served < _o.MaxRequestsPerConnection)
                {
                    Request req;
                    try
                    {
                        req = await ReadRequestAsync(conn, first ? _o.FirstRequestTimeout : _o.KeepAliveTimeout).ConfigureAwait(false);
                    }
                    catch (BadRequest bad)
                    {
                        Bump(ref _badReq);
                        await WriteSimpleAsync(conn.Stream, bad.Status, bad.Message, null, true).ConfigureAwait(false);
                        break;
                    }
                    if (req == null) break;
                    first = false;
                    served++;
                    Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
                    Bump(ref _requests);
                    bool keep = await DispatchAsync(conn, req, remote).ConfigureAwait(false);
                    if (!keep) break;
                    if (conn.LastWasNotFound && ++strikes >= 3) break;                      // a client that keeps guessing paths is dropped
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Bump(ref _errors);
                AceLog.Warn(Tag, "connection error: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                TcpClient dummy;
                _clients.TryRemove(id, out dummy);
                try { client.Close(); } catch { }
                Interlocked.Decrement(ref _active);
            }
        }

        private async Task<Request> ReadRequestAsync(Conn c, TimeSpan timeout)
        {
            using (var tcts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                if (timeout > TimeSpan.Zero) tcts.CancelAfter(timeout);
                int headerEnd;
                while ((headerEnd = IndexOfHeaderEnd(c.Buf, c.Start, c.End)) < 0)
                {
                    if (c.End - c.Start > _o.MaxHeaderBytes) throw new BadRequest(431, "Request Header Fields Too Large");
                    if (c.End == c.Buf.Length)
                    {
                        if (c.Start == 0) throw new BadRequest(431, "Request Header Fields Too Large");
                        Buffer.BlockCopy(c.Buf, c.Start, c.Buf, 0, c.End - c.Start);
                        c.End -= c.Start; c.Start = 0;
                    }
                    int n;
                    try { n = await c.Stream.ReadAsync(c.Buf, c.End, c.Buf.Length - c.End, tcts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { if (_cts.IsCancellationRequested) throw; return null; }
                    if (n == 0) return null;
                    c.End += n;
                }
                if (headerEnd - c.Start > _o.MaxHeaderBytes) throw new BadRequest(431, "Request Header Fields Too Large");
                string head;
                try { head = StrictAscii(c.Buf, c.Start, headerEnd - c.Start); }
                catch (FormatException) { throw new BadRequest(400, "Bad Request"); }
                c.Start = headerEnd + 4;                                                       // what follows (pipelining) stays in the buffer
                if (c.Start == c.End) { c.Start = 0; c.End = 0; }
                return ParseRequest(head, _o.MaxHeaderCount);
            }
        }

        private static string StrictAscii(byte[] b, int off, int len)
        {
            var c = new char[len];
            for (int i = 0; i < len; i++)
            {
                byte x = b[off + i];
                if (x > 126 || (x < 32 && x != '\r' && x != '\n' && x != '\t')) throw new FormatException("non-ASCII or control byte in the header");
                c[i] = (char)x;
            }
            return new string(c);
        }

        private static int IndexOfHeaderEnd(byte[] b, int start, int end)
        {
            for (int i = start; i + 3 < end; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        internal static bool IsTokenChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;
        }

        private static Request ParseRequest(string head, int maxHeaders)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines[0].Length == 0 || lines[0].Length > 600) throw new BadRequest(400, "Bad Request");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3) throw new BadRequest(400, "Bad Request");
            foreach (char c in parts[0]) if (!IsTokenChar(c)) throw new BadRequest(400, "Bad Request");
            if (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0") throw new BadRequest(parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? 505 : 400, "Bad Request");
            if (parts[1].Length == 0 || parts[1][0] != '/') throw new BadRequest(400, "Bad Request");
            var req = new Request { Method = parts[0], Target = parts[1], Http10 = parts[2] == "HTTP/1.0" };
            int q = req.Target.IndexOf('?');
            req.Path = q < 0 ? req.Target : req.Target.Substring(0, q);
            req.Query = q < 0 ? "" : req.Target.Substring(q + 1);
            if (lines.Length - 1 > maxHeaders) throw new BadRequest(431, "Request Header Fields Too Large");
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) throw new BadRequest(400, "Bad Request");
                if (line[0] == ' ' || line[0] == '\t') throw new BadRequest(400, "Bad Request");   // obsolete line folding
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new BadRequest(400, "Bad Request");
                string name = line.Substring(0, colon);
                foreach (char c in name) if (!IsTokenChar(c)) throw new BadRequest(400, "Bad Request");
                string value = line.Substring(colon + 1).Trim();
                string old;
                if (req.Headers.TryGetValue(name, out old))
                {
                    if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) throw new BadRequest(400, "Bad Request");   // two Host headers
                    req.Headers[name] = old + ", " + value;
                }
                else req.Headers[name] = value;
            }
            return req;
        }

        // ---------- Dispatch ----------

        private bool HostMatches(string host)
        {
            if (host == null) return false;
            string expected = Address + ":" + Port.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(host, expected, StringComparison.OrdinalIgnoreCase)) return true;
            return Port == 80 && string.Equals(host, Address.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static readonly string[] Hidden = new string[0];

        /// <summary>Serves one request. Returns true if the connection may be reused.</summary>
        private async Task<bool> DispatchAsync(Conn conn, Request req, IPAddress remote)
        {
            var stream = conn.Stream;
            conn.LastWasNotFound = false;
            bool close = req.Http10 || string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
            string host = req.Header("Host");
            if (host == null ? !req.Http10 : !HostMatches(host))
            {
                // DNS rebinding or a client that is not talking to us by address: refused before anything else is looked at
                Bump(ref _rejHost);
                await WriteSimpleAsync(stream, host == null ? 400 : 421, host == null ? "Bad Request" : "Misdirected Request", null, true).ConfigureAwait(false);
                return false;
            }
            string cl = req.Header("Content-Length");
            if (req.Header("Transfer-Encoding") != null || (cl != null && cl != "0"))
            {
                Bump(ref _badReq);
                await WriteSimpleAsync(stream, 400, "Bad Request", null, true).ConfigureAwait(false);
                return false;
            }
            // /c/<token>/<name>
            string name;
            if (!TryRoute(req.Path, out name))
            {
                conn.LastWasNotFound = true;
                Bump(ref _notFound);
                await WriteSimpleAsync(stream, 404, "Not Found", null, close).ConfigureAwait(false);
                return !close;
            }
            string cors = _o.Cors ? CorsHeaders(req, req.Method == "OPTIONS") : "";
            if (req.Method == "OPTIONS")
            {
                if (!KnownName(name)) { conn.LastWasNotFound = true; Bump(ref _notFound); await WriteSimpleAsync(stream, 404, "Not Found", null, close).ConfigureAwait(false); return !close; }
                Bump(ref _preflights);
                if (!_o.Cors) { await WriteSimpleAsync(stream, 405, "Method Not Allowed", "Allow: GET, HEAD\r\n", close).ConfigureAwait(false); return !close; }
                var sb = new StringBuilder("HTTP/1.1 204 No Content\r\n");
                sb.Append("Server: AceEngine\r\n").Append(cors).Append("Content-Length: 0\r\n").Append("Connection: ").Append(close ? "close" : "keep-alive").Append("\r\n\r\n");
                await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
                return !close;
            }
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                await WriteSimpleAsync(stream, 405, "Method Not Allowed", "Allow: GET, HEAD, OPTIONS\r\n" + cors, close).ConfigureAwait(false);
                return !close;
            }
            bool head = req.Method == "HEAD";
            if (!_content.IsAvailable)
            {
                await WriteSimpleAsync(stream, 503, "Service Unavailable", cors + "Retry-After: 2\r\n", true).ConfigureAwait(false);
                return false;
            }
            if (name == "index.m3u8" && _content.OffersHls) return await ServePlaylistAsync(stream, head, close, cors, false).ConfigureAwait(false);
            if (name == "master.m3u8" && _content.OffersHls && _content.GetMasterPlaylist() != null) return await ServePlaylistAsync(stream, head, close, cors, true).ConfigureAwait(false);
            if (name == "live.ts" && _content.OffersProgressive) return await ServeProgressiveAsync(stream, req, head, cors).ConfigureAwait(false);
            long seq;
            if (_content.OffersHls && TryParseSegmentName(name, out seq)) return await ServeSegmentAsync(stream, req, seq, head, close, cors).ConfigureAwait(false);
            conn.LastWasNotFound = true;
            Bump(ref _notFound);
            await WriteSimpleAsync(stream, 404, "Not Found", cors, close).ConfigureAwait(false);
            return !close;
        }

        private bool KnownName(string name)
        {
            long seq;
            return (name == "index.m3u8" && _content.OffersHls) || (name == "master.m3u8" && _content.OffersHls && _content.GetMasterPlaylist() != null) || (name == "live.ts" && _content.OffersProgressive) || (_content.OffersHls && TryParseSegmentName(name, out seq));
        }

        /// <summary>Extracts the name after /c/&lt;token&gt;/. A wrong token, a path outside /c/ or anything odd in it is simply "not a route".</summary>
        private bool TryRoute(string path, out string name)
        {
            name = null;
            if (path.Length > 120 || !path.StartsWith("/c/", StringComparison.Ordinal)) return false;
            int slash = path.IndexOf('/', 3);
            if (slash < 0) { return false; }
            string tok = path.Substring(3, slash - 3);
            string rest = path.Substring(slash + 1);
            bool ok = FixedTimeEquals(tok, _token);
            foreach (char c in rest) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.')) ok = false;
            if (!ok) { Bump(ref _badToken); return false; }
            name = rest;
            return rest.Length > 0;
        }

        internal static bool TryParseSegmentName(string name, out long seq)
        {
            seq = 0;
            if (name.Length < 5 || name.Length > 24 || name[0] != 's' || !name.EndsWith(".ts", StringComparison.Ordinal)) return false;
            string d = name.Substring(1, name.Length - 4);
            foreach (char c in d) if (c < '0' || c > '9') return false;
            return long.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out seq);
        }

        private string CorsHeaders(Request req, bool preflight)
        {
            var sb = new StringBuilder();
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Expose-Headers: Content-Length, Content-Range, Content-Type, Date, Accept-Ranges\r\n");
            if (preflight)
            {
                sb.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n");
                string asked = req.Header("Access-Control-Request-Headers");
                sb.Append("Access-Control-Allow-Headers: ").Append(SafeHeaderList(asked) ?? "Range, Content-Type, Accept, Origin, Cache-Control").Append("\r\n");
                sb.Append("Access-Control-Max-Age: 600\r\n");
                if (string.Equals(req.Header("Access-Control-Request-Private-Network"), "true", StringComparison.OrdinalIgnoreCase))
                    sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Echoes the requested header names only if they are plain header tokens (nothing that could be injected into the response).</summary>
        private static string SafeHeaderList(string asked)
        {
            if (string.IsNullOrWhiteSpace(asked) || asked.Length > 200) return null;
            var names = asked.Split(',');
            if (names.Length > 12) return null;
            var sb = new StringBuilder();
            foreach (var raw in names)
            {
                string n = raw.Trim();
                if (n.Length == 0 || n.Length > 40) return null;
                foreach (char c in n) if (!IsTokenChar(c)) return null;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(n);
            }
            return sb.ToString();
        }

        // ---------- HLS ----------

        private string Common(string contentType, string cacheControl, string cors)
        {
            return "Server: AceEngine\r\nDate: " + DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture) + "\r\nContent-Type: " + contentType + "\r\nCache-Control: " + cacheControl
                + "\r\nX-Content-Type-Options: nosniff\r\n" + cors;
        }

        private async Task<bool> ServePlaylistAsync(NetworkStream stream, bool head, bool close, string cors, bool master)
        {
            Bump(ref _playlists);
            bool ended = false;
            string text = master ? _content.GetMasterPlaylist() : _content.GetPlaylist(out ended);
            if (text == null)
            {
                await WriteSimpleAsync(stream, 503, "Service Unavailable", cors + "Retry-After: 1\r\n", close).ConfigureAwait(false);
                return !close;
            }
            var body = Encoding.ASCII.GetBytes(text);
            var sb = new StringBuilder("HTTP/1.1 200 OK\r\n");
            sb.Append(Common("application/vnd.apple.mpegurl", "no-cache, no-store", cors));
            sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\nConnection: ").Append(close ? "close" : "keep-alive").Append("\r\n\r\n");
            await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
            if (!head) { await WriteAsync(stream, body, 0, body.Length).ConfigureAwait(false); AddBytes(body.Length); }
            return !close;
        }

        private async Task<bool> ServeSegmentAsync(NetworkStream stream, Request req, long seq, bool head, bool close, string cors)
        {
            Bump(ref _segments);
            using (var seg = _content.GetSegment(seq))
            {
                if (seg == null)
                {
                    Bump(ref _notFound);
                    await WriteSimpleAsync(stream, 404, "Not Found", cors, close).ConfigureAwait(false);
                    return !close;
                }
                long length = seg.Length, first, last;
                var rr = RangeHeader.Parse(req.Header("Range"), length, out first, out last);
                var sb = new StringBuilder();
                if (rr == RangeResult.NotSatisfiable)
                {
                    sb.Append("HTTP/1.1 416 Range Not Satisfiable\r\n").Append(Common("video/mp2t", "max-age=60", cors));
                    sb.Append("Content-Range: bytes */").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\nContent-Length: 0\r\nConnection: ").Append(close ? "close" : "keep-alive").Append("\r\n\r\n");
                    await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
                    return !close;
                }
                if (rr == RangeResult.Satisfiable)
                {
                    sb.Append("HTTP/1.1 206 Partial Content\r\n").Append(Common("video/mp2t", "max-age=60", cors)).Append("Accept-Ranges: bytes\r\n");
                    sb.Append("Content-Range: bytes ").Append(first.ToString(CultureInfo.InvariantCulture)).Append('-').Append(last.ToString(CultureInfo.InvariantCulture)).Append('/').Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                else
                {
                    sb.Append("HTTP/1.1 200 OK\r\n").Append(Common("video/mp2t", "max-age=60", cors)).Append("Accept-Ranges: bytes\r\n");
                    first = 0; last = length - 1;
                }
                long n = last - first + 1;
                sb.Append("Content-Length: ").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\nConnection: ").Append(close ? "close" : "keep-alive").Append("\r\n\r\n");
                await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
                if (!head)
                {
                    long sent = 0;
                    while (sent < n)
                    {
                        int chunk = (int)Math.Min(64 * 1024, n - sent);
                        await WriteAsync(stream, seg.Buffer, (int)(first + sent), chunk).ConfigureAwait(false);
                        sent += chunk;
                    }
                    AddBytes(n);
                }
                return !close;
            }
        }

        // ---------- Progressive TS ----------

        private async Task<bool> ServeProgressiveAsync(NetworkStream stream, Request req, bool head, string cors)
        {
            Bump(ref _streams);
            TimeSpan? back;
            if (!TryParseBack(req.Query, out back)) { await WriteSimpleAsync(stream, 400, "Bad Request", cors, true).ConfigureAwait(false); return false; }
            var sb = new StringBuilder();
            switch (_o.Progressive)
            {
                case ProgressiveMode.Chunked: sb.Append("HTTP/1.1 200 OK\r\n"); break;
                default: sb.Append("HTTP/1.0 200 OK\r\n"); break;
            }
            sb.Append(Common(_o.ProgressiveContentType, "no-cache", cors));
            sb.Append("Accept-Ranges: none\r\n");
            if (_o.Dlna != null)
            {
                sb.Append("transferMode.dlna.org: Streaming\r\n");
                sb.Append("contentFeatures.dlna.org: ").Append(_o.Dlna.ContentFeatures).Append("\r\n");
            }
            if (_o.Progressive == ProgressiveMode.Chunked) sb.Append("Transfer-Encoding: chunked\r\n");
            else if (_o.Progressive == ProgressiveMode.BigContentLength) sb.Append("Content-Length: 50000000000\r\n");
            sb.Append("Connection: close\r\n\r\n");
            // the same headers for HEAD and GET: renderers probe with HEAD before they play
            await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
            if (head) return false;

            bool chunked = _o.Progressive == ProgressiveMode.Chunked;
            IStreamReader reader = await _content.OpenProgressiveAsync(back, _cts.Token).ConfigureAwait(false);
            long sent = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            AceLog.Info(Tag, "progressive stream opened for " + Address + " ← " + (_lastClient));
            try
            {
                using (reader)
                {
                    const int headroom = 10;
                    int block = Math.Max(1024, _o.StreamBlockBytes);
                    var frame = new byte[headroom + block + 2];
                    bool aligned = false;
                    byte[] alignBuf = null; int alignCount = 0;
                    while (!_cts.IsCancellationRequested)
                    {
                        int n;
                        if (aligned)
                        {
                            n = await ReadWithStallAsync(reader, frame, headroom, block).ConfigureAwait(false);
                            if (n <= 0) break;
                            await WriteFrameAsync(stream, frame, headroom, n, chunked).ConfigureAwait(false);
                            sent += n; AddBytes(n);
                            continue;
                        }
                        // The first bytes are aligned to a TS packet (a source without the live buffer can hand over a cut packet)
                        if (alignBuf == null) alignBuf = new byte[block + TsAligner.Needed + 16];
                        var tmp = new byte[Math.Min(block, alignBuf.Length - alignCount)];
                        n = await ReadWithStallAsync(reader, tmp, 0, tmp.Length).ConfigureAwait(false);
                        if (n <= 0) break;
                        Buffer.BlockCopy(tmp, 0, alignBuf, alignCount, n);
                        alignCount += n;
                        int p = TsAligner.FindSync(alignBuf, alignCount);
                        if (p >= 0)
                        {
                            int rest = alignCount - p;
                            int off = p;
                            while (rest > 0)
                            {
                                int c = Math.Min(block, rest);
                                Buffer.BlockCopy(alignBuf, off, frame, headroom, c);
                                await WriteFrameAsync(stream, frame, headroom, c, chunked).ConfigureAwait(false);
                                sent += c; AddBytes(c); off += c; rest -= c;
                            }
                            aligned = true;
                            continue;
                        }
                        int keep = Math.Min(alignCount, TsAligner.Needed - 1);
                        Buffer.BlockCopy(alignBuf, alignCount - keep, alignBuf, 0, keep);
                        alignCount = keep;
                    }
                    if (chunked) await WriteStringAsync(stream, "0\r\n\r\n").ConfigureAwait(false);
                }
            }
            finally
            {
                AceLog.Info(Tag, "progressive stream closed after " + sent + " bytes in " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            }
            return false;
        }

        private async Task<int> ReadWithStallAsync(IStreamReader reader, byte[] buf, int off, int count)
        {
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                if (_o.ReadStallTimeout > TimeSpan.Zero) stall.CancelAfter(_o.ReadStallTimeout);
                try { return await reader.ReadAsync(buf, off, count, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { return 0; }       // no data for too long: the stream ends
            }
        }

        private async Task WriteFrameAsync(NetworkStream stream, byte[] frame, int headroom, int n, bool chunked)
        {
            if (!chunked) { await WriteAsync(stream, frame, headroom, n).ConfigureAwait(false); return; }
            string hex = n.ToString("x", CultureInfo.InvariantCulture);
            int hl = hex.Length + 2;
            int start = headroom - hl;
            for (int i = 0; i < hex.Length; i++) frame[start + i] = (byte)hex[i];
            frame[start + hex.Length] = (byte)'\r'; frame[start + hex.Length + 1] = (byte)'\n';
            frame[headroom + n] = (byte)'\r'; frame[headroom + n + 1] = (byte)'\n';
            await WriteAsync(stream, frame, start, hl + n + 2).ConfigureAwait(false);
        }

        internal static bool TryParseBack(string query, out TimeSpan? back)
        {
            back = null;
            if (string.IsNullOrEmpty(query)) return true;
            foreach (var part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part.Substring(0, eq);
                if (!string.Equals(key, "back", StringComparison.OrdinalIgnoreCase)) continue;
                long ms;
                string val = eq < 0 ? "" : part.Substring(eq + 1);
                if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out ms) || ms > 3600000) return false;
                back = TimeSpan.FromMilliseconds(ms);
            }
            return true;
        }

        // ---------- Writing ----------

        private void AddBytes(long n) { lock (_statGate) _bytes += n; }

        private async Task WriteAsync(NetworkStream stream, byte[] b, int off, int n)
        {
            using (var w = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                if (_o.WriteTimeout > TimeSpan.Zero) w.CancelAfter(_o.WriteTimeout);
                await stream.WriteAsync(b, off, n, w.Token).ConfigureAwait(false);
            }
        }

        private Task WriteStringAsync(NetworkStream stream, string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            return WriteAsync(stream, b, 0, b.Length);
        }

        private async Task WriteSimpleAsync(NetworkStream stream, int status, string reason, string extra, bool close)
        {
            // extra may be "Header: v\r\n..." or null; a bare CORS string is allowed
            var body = Encoding.ASCII.GetBytes(reason + "\n");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: AceEngine\r\nContent-Type: text/plain\r\nContent-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
            if (!string.IsNullOrEmpty(extra)) sb.Append(extra);
            sb.Append("Connection: ").Append(close ? "close" : "keep-alive").Append("\r\n\r\n");
            try
            {
                await WriteStringAsync(stream, sb.ToString()).ConfigureAwait(false);
                await WriteAsync(stream, body, 0, body.Length).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
