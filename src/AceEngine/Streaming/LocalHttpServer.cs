using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Streaming
{
    public sealed class HttpServerOptions
    {
        /// <summary>Listening address. Loopback by default; another address (with OnlyAcceptSelf) is the alternative if the TV does not accept 127.0.0.1.</summary>
        public IPAddress BindAddress = IPAddress.Loopback;

        /// <summary>TCP port; 0 = the system picks a free one.</summary>
        public int Port = 0;

        /// <summary>Live mode. Chunked by default, like the original.</summary>
        public LiveTransfer LiveTransfer = LiveTransfer.Chunked;

        /// <summary>Send block size (64 KiB).</summary>
        public int BlockSize = 64 * 1024;

        /// <summary>If ReadAsync returns nothing in this time and the session is not Buffering/Connecting/Resolving, the connection is closed.</summary>
        public TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>A session without open connections for this time (counted from when there was one) is stopped. TimeSpan.Zero = disabled.</summary>
        public TimeSpan SessionIdleTimeout = TimeSpan.FromSeconds(30);

        /// <summary>A session that never receives connections is cleaned up after this time. TimeSpan.Zero = disabled.</summary>
        public TimeSpan SessionNeverUsedTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Maximum size of the HTTP headers.</summary>
        public int MaxHeaderBytes = 8 * 1024;

        /// <summary>Maximum time to receive the first request of a new connection.</summary>
        public TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Wait time for the next request on a keep-alive connection.</summary>
        public TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Align the live stream to the first valid TS packet.</summary>
        public bool AlignLiveToTs = true;

        /// <summary>Maximum bytes scanned to align before sending as is.</summary>
        public int MaxAlignScanBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Diagnostics (MP4 failure on the TV): always answer 200 with the full length, without Accept-Ranges or 206, even if the client sends Range.
        /// </summary>
        public bool IgnoreRangeHeader = false;

        /// <summary>Accept only connections whose source IP is our own (for a BindAddress other than loopback).</summary>
        public bool OnlyAcceptSelf = false;
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on 127.0.0.1: GET and HEAD of /stream/&lt;id&gt;. VOD with Content-Length,
    /// Accept-Ranges and 206/416; live with chunked or close-delimited and TS alignment. One reader per connection.
    /// </summary>
    public sealed class LocalHttpServer : IDisposable
    {
        private const string Tag = "http";

        private sealed class Entry
        {
            public IStreamSession Session;
            public CancellationTokenSource Cts = new CancellationTokenSource();
            public int Connections;
            public bool EverConnected;
            public DateTime LastActivityUtc = DateTime.UtcNow;
            public DateTime RegisteredUtc = DateTime.UtcNow;
        }

        private readonly HttpServerOptions _opt;
        private readonly ConcurrentDictionary<string, Entry> _sessions = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, TcpClient> _clients = new ConcurrentDictionary<int, TcpClient>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpListener _listener;
        private Task _acceptLoop;
        private Timer _reaper;
        private int _nextClientId;
        private bool _disposed;

        public LocalHttpServer() : this(null) { }

        public LocalHttpServer(HttpServerOptions options)
        {
            _opt = options ?? new HttpServerOptions();
        }

        public HttpServerOptions Options { get { return _opt; } }
        public int Port { get; private set; }
        public bool IsRunning { get { return _listener != null && !_disposed; } }

        public string BaseUrl
        {
            get { return "http://" + FormatHost(_opt.BindAddress) + ":" + Port.ToString(CultureInfo.InvariantCulture); }
        }

        private static string FormatHost(IPAddress a)
        {
            if (IPAddress.IsLoopback(a) && a.AddressFamily == AddressFamily.InterNetwork) return "127.0.0.1";
            return a.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + a + "]" : a.ToString();
        }

        public void Start()
        {
            if (_listener != null) throw new InvalidOperationException("the server is already started");
            var l = new TcpListener(_opt.BindAddress, _opt.Port);
            l.Start();
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            AceLog.Info(Tag, "HTTP server listening on " + BaseUrl + " (live: " + _opt.LiveTransfer + ")");
            _acceptLoop = Task.Run(() => AcceptLoopAsync());
            _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        // ---------- Sessions ----------

        /// <summary>Registers the session and returns its URL: http://127.0.0.1:&lt;port&gt;/stream/&lt;id&gt;.</summary>
        public string Register(IStreamSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (_listener == null) throw new InvalidOperationException("server not started");
            var e = new Entry { Session = session };
            if (!_sessions.TryAdd(session.Id, e)) throw new InvalidOperationException("there is already a session with the id " + session.Id);
            return GetUrl(session.Id);
        }

        public string GetUrl(string id)
        {
            return BaseUrl + "/stream/" + id;
        }

        /// <summary>Removes the session and closes its connections. It does not Dispose the session.</summary>
        public bool Unregister(string id)
        {
            Entry e;
            if (!_sessions.TryRemove(id, out e)) return false;
            try { e.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        public IEnumerable<IStreamSession> Sessions
        {
            get { foreach (var kv in _sessions) yield return kv.Value.Session; }
        }

        public int ActiveConnections(string id)
        {
            Entry e;
            return _sessions.TryGetValue(id, out e) ? Volatile.Read(ref e.Connections) : 0;
        }

        private void Reap()
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _sessions)
                {
                    var e = kv.Value;
                    if (Volatile.Read(ref e.Connections) > 0) continue;
                    var hold = e.Session as ISessionHold;
                    if (hold != null && hold.HoldOpen) { e.LastActivityUtc = now; continue; }           // e.g. pause of a live stream with memory
                    bool expire = false;
                    if (e.EverConnected)
                        expire = _opt.SessionIdleTimeout > TimeSpan.Zero && now - e.LastActivityUtc > _opt.SessionIdleTimeout;
                    else
                        expire = _opt.SessionNeverUsedTimeout > TimeSpan.Zero && now - e.RegisteredUtc > _opt.SessionNeverUsedTimeout;
                    if (expire && _sessions.TryRemove(kv.Key, out e))
                    {
                        AceLog.Info(Tag, "session " + kv.Key + " without connections: stopping");
                        try { e.Cts.Cancel(); } catch (ObjectDisposedException) { }
                        try { e.Session.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "session Dispose: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "reaper: " + ex.Message);
            }
        }

        // ---------- Accept ----------

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
                catch (InvalidOperationException) { break; }

                if (_opt.OnlyAcceptSelf && !IsSelf(client))
                {
                    AceLog.Warn(Tag, "connection rejected (not local): " + client.Client.RemoteEndPoint);
                    client.Close();
                    continue;
                }
                var tc = client;
                var ignored = Task.Run(() => HandleConnectionAsync(tc));
            }
        }

        private static bool IsSelf(TcpClient c)
        {
            var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
            var local = ((IPEndPoint)c.Client.LocalEndPoint).Address;
            return IPAddress.IsLoopback(remote) || remote.Equals(local);
        }

        // ---------- Connection ----------

        private sealed class Request
        {
            public string Method;
            public string Target;
            public string Path;
            public string Query;
            public bool Http10;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
        }

        private sealed class ConnectionState
        {
            public NetworkStream Stream;
            public byte[] Buf = new byte[8192 + 4096];
            public int Start;
            public int End;
        }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            int cid = Interlocked.Increment(ref _nextClientId);
            _clients[cid] = client;
            try
            {
                client.NoDelay = true;
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
                var cs = new ConnectionState { Stream = client.GetStream() };
                bool first = true;
                while (!_cts.IsCancellationRequested)
                {
                    Request req;
                    int status = 0;
                    try
                    {
                        req = await ReadRequestAsync(cs, first ? _opt.FirstRequestTimeout : _opt.KeepAliveTimeout, _cts.Token).ConfigureAwait(false);
                    }
                    catch (HttpProtocolException hex)
                    {
                        status = hex.Status;
                        await WriteSimpleAsync(cs.Stream, status, hex.Message, false, null, _cts.Token).ConfigureAwait(false);
                        break;
                    }
                    if (req == null) break;     // clean close or timeout
                    first = false;
                    bool keepAlive = await DispatchAsync(cs, req).ConfigureAwait(false);
                    if (!keepAlive) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }                 // the player closed: it is treated as a disconnect
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "connection error: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                TcpClient dummy;
                _clients.TryRemove(cid, out dummy);
                try { client.Close(); } catch { }
            }
        }

        private sealed class HttpProtocolException : Exception
        {
            public int Status { get; private set; }
            public HttpProtocolException(int status, string message) : base(message) { Status = status; }
        }

        private async Task<Request> ReadRequestAsync(ConnectionState cs, TimeSpan timeout, CancellationToken ct)
        {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeout > TimeSpan.Zero) timeoutCts.CancelAfter(timeout);
                int headerEnd;
                while ((headerEnd = IndexOfHeaderEnd(cs.Buf, cs.Start, cs.End)) < 0)
                {
                    if (cs.End - cs.Start > _opt.MaxHeaderBytes)
                        throw new HttpProtocolException(431, "Request Header Fields Too Large");
                    if (cs.End == cs.Buf.Length)
                    {
                        // compact
                        if (cs.Start > 0)
                        {
                            Buffer.BlockCopy(cs.Buf, cs.Start, cs.Buf, 0, cs.End - cs.Start);
                            cs.End -= cs.Start; cs.Start = 0;
                        }
                        else throw new HttpProtocolException(431, "Request Header Fields Too Large");
                    }
                    int n;
                    try
                    {
                        n = await cs.Stream.ReadAsync(cs.Buf, cs.End, cs.Buf.Length - cs.End, timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        return null;     // timeout waiting for a request
                    }
                    if (n == 0) return null;
                    cs.End += n;
                }
                if (headerEnd - cs.Start > _opt.MaxHeaderBytes)
                    throw new HttpProtocolException(431, "Request Header Fields Too Large");

                string head = Encoding.ASCII.GetString(cs.Buf, cs.Start, headerEnd - cs.Start);
                cs.Start = headerEnd + 4;
                if (cs.Start == cs.End) { cs.Start = 0; cs.End = 0; }
                return ParseRequest(head);
            }
        }

        private static int IndexOfHeaderEnd(byte[] buf, int start, int end)
        {
            for (int i = start; i + 3 < end; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') return i;
            return -1;
        }

        private static Request ParseRequest(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
                throw new HttpProtocolException(400, "Bad Request");
            var req = new Request { Method = parts[0], Target = parts[1] };
            req.Http10 = parts[2] == "HTTP/1.0";
            if (parts[2] != "HTTP/1.0" && parts[2] != "HTTP/1.1") throw new HttpProtocolException(505, "HTTP Version Not Supported");
            int q = req.Target.IndexOf('?');
            req.Path = q < 0 ? req.Target : req.Target.Substring(0, q);
            req.Query = q < 0 ? "" : req.Target.Substring(q + 1);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new HttpProtocolException(400, "Bad Request");
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string existing;
                if (req.Headers.TryGetValue(name, out existing)) req.Headers[name] = existing + ", " + value;
                else req.Headers[name] = value;
            }
            return req;
        }

        /// <summary>Serves a request. Returns true if the connection can be reused.</summary>
        private async Task<bool> DispatchAsync(ConnectionState cs, Request req)
        {
            var ct = _cts.Token;
            bool wantsClose = req.Http10 || string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                await WriteSimpleAsync(cs.Stream, 405, "Method Not Allowed", false, "Allow: GET, HEAD\r\n", ct).ConfigureAwait(false);
                return false;
            }
            string cl = req.Header("Content-Length");
            if (req.Header("Transfer-Encoding") != null || (cl != null && cl != "0"))
            {
                await WriteSimpleAsync(cs.Stream, 400, "Bad Request", false, null, ct).ConfigureAwait(false);
                return false;
            }

            const string prefix = "/stream/";
            if (!req.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                await WriteSimpleAsync(cs.Stream, 404, "Not Found", !wantsClose, null, ct).ConfigureAwait(false);
                return !wantsClose;
            }
            string id = req.Path.Substring(prefix.Length);
            Entry entry;
            if (id.Length == 0 || !_sessions.TryGetValue(id, out entry))
            {
                await WriteSimpleAsync(cs.Stream, 404, "Not Found", !wantsClose, null, ct).ConfigureAwait(false);
                return !wantsClose;
            }

            Interlocked.Increment(ref entry.Connections);
            entry.EverConnected = true;
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Cts.Token))
                {
                    return await ServeAsync(cs.Stream, req, entry, wantsClose, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // session stopped or server closed: the connection is cut
                return false;
            }
            finally
            {
                entry.LastActivityUtc = DateTime.UtcNow;
                Interlocked.Decrement(ref entry.Connections);
            }
        }

        private async Task<bool> ServeAsync(NetworkStream stream, Request req, Entry entry, bool wantsClose, CancellationToken ct)
        {
            var session = entry.Session;
            bool head = req.Method == "HEAD";

            // Wait for the session to know its length (e.g. a magnet resolving metadata).
            var ready = session as ISessionReadiness;
            if (ready != null)
            {
                try { await ready.WaitUntilReadyAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AceLog.Warn(Tag, "session " + session.Id + " not available: " + ex.Message);
                    await WriteSimpleAsync(stream, 503, "Service Unavailable", false, null, ct).ConfigureAwait(false);
                    return false;
                }
            }
            if (session.State == SessionState.Failed || session.State == SessionState.Stopped)
            {
                await WriteSimpleAsync(stream, 503, "Service Unavailable", false, null, ct).ConfigureAwait(false);
                return false;
            }

            if (session.IsLive || !session.Length.HasValue)
            {
                // ?back=<ms>: live with memory, start those milliseconds behind the live point
                TimeSpan? back;
                if (!TryParseBack(req.Query, out back))
                {
                    await WriteSimpleAsync(stream, 400, "Bad Request", false, null, ct).ConfigureAwait(false);
                    return false;
                }
                return await ServeLiveAsync(stream, req, session, head, back, ct).ConfigureAwait(false);
            }
            return await ServeVodAsync(stream, req, session, head, wantsClose, ct).ConfigureAwait(false);
        }

        // ---------- VOD ----------

        private async Task<bool> ServeVodAsync(NetworkStream stream, Request req, IStreamSession session, bool head, bool wantsClose, CancellationToken ct)
        {
            long length = session.Length.Value;
            long first, last;
            RangeResult rr;
            if (_opt.IgnoreRangeHeader) { rr = RangeResult.None; first = 0; last = length - 1; }
            else rr = RangeHeader.Parse(req.Header("Range"), length, out first, out last);
            if (length == 0 && rr == RangeResult.None) { first = 0; last = -1; }

            var sb = new StringBuilder();
            if (rr == RangeResult.NotSatisfiable)
            {
                sb.Append("HTTP/1.1 416 Range Not Satisfiable\r\n");
                AppendCommon(sb, session.ContentType, true);
                sb.Append("Content-Range: bytes */").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("Content-Length: 0\r\n");
                sb.Append("Connection: ").Append(wantsClose ? "close" : "keep-alive").Append("\r\n\r\n");
                await WriteStringAsync(stream, sb.ToString(), ct).ConfigureAwait(false);
                return !wantsClose;
            }

            long nbytes = last - first + 1;
            if (rr == RangeResult.Satisfiable)
            {
                sb.Append("HTTP/1.1 206 Partial Content\r\n");
                AppendCommon(sb, session.ContentType, true);
                sb.Append("Content-Range: bytes ").Append(first.ToString(CultureInfo.InvariantCulture)).Append('-')
                  .Append(last.ToString(CultureInfo.InvariantCulture)).Append('/').Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            else
            {
                sb.Append("HTTP/1.1 200 OK\r\n");
                AppendCommon(sb, session.ContentType, !_opt.IgnoreRangeHeader);
            }
            sb.Append("Content-Length: ").Append(nbytes.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: ").Append(wantsClose ? "close" : "keep-alive").Append("\r\n\r\n");
            await WriteStringAsync(stream, sb.ToString(), ct).ConfigureAwait(false);

            if (head || nbytes == 0) return !wantsClose;

            AceLog.Debug(Tag, "VOD " + session.Id + " bytes " + first + "-" + last + "/" + length);
            long remaining = nbytes;
            var buffer = new byte[Math.Max(1024, _opt.BlockSize)];
            using (var reader = await session.OpenReaderAsync(first, ct).ConfigureAwait(false))
            {
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int n = await ReadWithStallGuardAsync(reader, session, buffer, 0, want, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        // End or session stopped before fulfilling Content-Length: the only way to signal it is to cut.
                        AceLog.Debug(Tag, "VOD " + session.Id + ": reader without data with " + remaining + " bytes pending; cutting");
                        return false;
                    }
                    await stream.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                    remaining -= n;
                }
            }
            return !wantsClose;
        }

        // ---------- Live ----------

        /// <summary>Largest rewind accepted in ?back (one day): an absurd value does not overflow the time calculations.</summary>
        private const long MaxBackMs = 24L * 3600 * 1000;

        /// <summary>Extracts ?back=&lt;ms&gt; from the query. Without the parameter, back = null (live). false if malformed (negative, non-numeric or over a day).</summary>
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
                if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out ms) || ms < 0 || ms > MaxBackMs) return false;
                back = TimeSpan.FromMilliseconds(ms);
            }
            return true;
        }

        private async Task<bool> ServeLiveAsync(NetworkStream stream, Request req, IStreamSession session, bool head, TimeSpan? back, CancellationToken ct)
        {
            // HTTP/1.0 does not support chunked: it falls back to close-delimited.
            bool chunked = _opt.LiveTransfer == LiveTransfer.Chunked && !req.Http10;
            var sb = new StringBuilder();
            sb.Append(chunked ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.0 200 OK\r\n");
            AppendCommon(sb, session.ContentType, false);       // without Accept-Ranges or Content-Length
            if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: close\r\n\r\n");
            await WriteStringAsync(stream, sb.ToString(), ct).ConfigureAwait(false);
            if (head) return false;

            var tsSession = session as ITimeshiftSession;
            if (back.HasValue && (tsSession == null || !tsSession.TimeshiftEnabled)) back = null;            // without memory there is nothing behind: the live stream is served
            AceLog.Info(Tag, "LIVE " + session.Id + (chunked ? " chunked" : " close-delimited") + ": connection open" + (back.HasValue ? " (back=" + (long)back.Value.TotalMilliseconds + " ms)" : "")
                + " (ua=" + (req.Header("User-Agent") ?? "-") + ")");
            long liveSent = 0;
            string liveEnd = "end";
            var liveClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            const int headroom = 10;                         // room for "<hex>\r\n" in front of the data
            int blockSize = Math.Max(1024, _opt.BlockSize);
            var frame = new byte[headroom + blockSize + 2];
            using (var reader = back.HasValue ? await tsSession.OpenReaderBehindAsync(back, ct).ConfigureAwait(false) : await session.OpenReaderAsync(0, ct).ConfigureAwait(false))
            {
                bool aligned = !_opt.AlignLiveToTs;
                byte[] alignBuf = null;
                int alignCount = 0;
                long scanned = 0;
                var tmp = new byte[blockSize];

                while (true)
                {
                    int n;
                    if (aligned)
                    {
                        n = await ReadWithStallGuardAsync(reader, session, frame, headroom, blockSize, ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        await WriteFrameAsync(stream, frame, headroom, n, chunked, ct).ConfigureAwait(false);
                        liveSent += n;
                        continue;
                    }

                    // Alignment phase to the first valid TS packet
                    if (alignBuf == null) alignBuf = new byte[blockSize + TsAligner.Needed + 16];
                    n = await ReadWithStallGuardAsync(reader, session, tmp, 0, Math.Min(tmp.Length, alignBuf.Length - alignCount), ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        // End before being able to decide: send from the first 0x47 if there is one.
                        int single = Array.IndexOf(alignBuf, (byte)0x47, 0, alignCount);
                        if (single >= 0 && alignCount - single > 0)
                            await WriteFrameCopyAsync(stream, frame, headroom, alignBuf, single, alignCount - single, chunked, ct).ConfigureAwait(false);
                        break;
                    }
                    Buffer.BlockCopy(tmp, 0, alignBuf, alignCount, n);
                    alignCount += n;
                    int p = TsAligner.FindSync(alignBuf, alignCount);
                    if (p >= 0)
                    {
                        if (p > 0) AceLog.Debug(Tag, "LIVE " + session.Id + ": aligned to TS skipping " + (scanned + p) + " bytes");
                        await WriteFrameCopyAsync(stream, frame, headroom, alignBuf, p, alignCount - p, chunked, ct).ConfigureAwait(false);
                        aligned = true;
                        continue;
                    }
                    // No sync yet: discard what can no longer be a start (keep the last Needed-1 bytes).
                    int keep = Math.Min(alignCount, TsAligner.Needed - 1);
                    scanned += alignCount - keep;
                    if (scanned > _opt.MaxAlignScanBytes)
                    {
                        AceLog.Warn(Tag, "LIVE " + session.Id + ": no TS sync found in " + scanned + " bytes; sending as is");
                        await WriteFrameCopyAsync(stream, frame, headroom, alignBuf, 0, alignCount, chunked, ct).ConfigureAwait(false);
                        aligned = true;
                        continue;
                    }
                    Buffer.BlockCopy(alignBuf, alignCount - keep, alignBuf, 0, keep);
                    alignCount = keep;
                }
            }
            if (chunked)
            {
                var term = Encoding.ASCII.GetBytes("0\r\n\r\n");
                await stream.WriteAsync(term, 0, term.Length, ct).ConfigureAwait(false);
            }
            }
            catch (Exception ex) { liveEnd = ex is OperationCanceledException ? "cancelled (session stopped or server closed)" : ex.GetType().Name + ": " + ex.Message; throw; }
            finally { AceLog.Info(Tag, "LIVE " + session.Id + ": connection closed after " + liveSent + " bytes in " + liveClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s (" + liveEnd + ")"); }
            return false;
        }

        private static async Task WriteFrameCopyAsync(NetworkStream stream, byte[] frame, int headroom, byte[] src, int srcOffset, int count, bool chunked, CancellationToken ct)
        {
            // frame holds blockSize bytes of data; src may be somewhat larger: it is sent in pieces.
            int capacity = frame.Length - headroom - 2;
            while (count > 0)
            {
                int n = Math.Min(capacity, count);
                Buffer.BlockCopy(src, srcOffset, frame, headroom, n);
                await WriteFrameAsync(stream, frame, headroom, n, chunked, ct).ConfigureAwait(false);
                srcOffset += n; count -= n;
            }
        }

        /// <summary>Writes the n bytes that are in frame[headroom..headroom+n) with or without chunked framing.</summary>
        private static Task WriteFrameAsync(NetworkStream stream, byte[] frame, int headroom, int n, bool chunked, CancellationToken ct)
        {
            if (!chunked) return stream.WriteAsync(frame, headroom, n, ct);
            string hex = n.ToString("x", CultureInfo.InvariantCulture);
            int hl = hex.Length + 2;
            int start = headroom - hl;
            for (int i = 0; i < hex.Length; i++) frame[start + i] = (byte)hex[i];
            frame[start + hex.Length] = (byte)'\r';
            frame[start + hex.Length + 1] = (byte)'\n';
            frame[headroom + n] = (byte)'\r';
            frame[headroom + n + 1] = (byte)'\n';
            return stream.WriteAsync(frame, start, hl + n + 2, ct);
        }

        // ---------- Read with a wait limit ----------

        private async Task<int> ReadWithStallGuardAsync(IStreamReader reader, IStreamSession session, byte[] buf, int offset, int count, CancellationToken ct)
        {
            var readTask = reader.ReadAsync(buf, offset, count, ct);
            if (readTask.IsCompleted) return await readTask.ConfigureAwait(false);
            while (true)
            {
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var delay = Task.Delay(_opt.ReadStallTimeout, delayCts.Token);
                    var done = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
                    if (done == readTask)
                    {
                        delayCts.Cancel();          // releases the timer: there is one read per 64 KiB block
                        return await readTask.ConfigureAwait(false);
                    }
                }
                ct.ThrowIfCancellationRequested();
                var st = session.State;
                if (st == SessionState.Buffering || st == SessionState.Connecting || st == SessionState.Resolving)
                    continue;        // the session is loading: keep waiting
                AceLog.Warn(Tag, "no data for " + _opt.ReadStallTimeout.TotalSeconds + " s (state " + st + "); closing the connection");
                throw new OperationCanceledException("read stall");
            }
        }

        // ---------- Writing headers ----------

        private static void AppendCommon(StringBuilder sb, string contentType, bool acceptRanges)
        {
            sb.Append("Server: AceEngine\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Content-Type: ").Append(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType).Append("\r\n");
            if (acceptRanges) sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
        }

        private static async Task WriteStringAsync(NetworkStream stream, string s, CancellationToken ct)
        {
            var b = Encoding.ASCII.GetBytes(s);
            await stream.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
        }

        private static async Task WriteSimpleAsync(NetworkStream stream, int status, string reason, bool keepAlive, string extraHeaders, CancellationToken ct)
        {
            var body = Encoding.ASCII.GetBytes(reason + "\n");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: AceEngine\r\n");
            sb.Append("Content-Type: text/plain\r\n");
            sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (extraHeaders != null) sb.Append(extraHeaders);
            sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        }

        // ---------- Shutdown ----------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            if (_reaper != null) _reaper.Dispose();
            try { if (_listener != null) _listener.Stop(); } catch { }
            foreach (var kv in _sessions) { try { kv.Value.Cts.Cancel(); } catch { } }
            foreach (var kv in _clients) { try { kv.Value.Close(); } catch { } }
            try { if (_acceptLoop != null) _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
