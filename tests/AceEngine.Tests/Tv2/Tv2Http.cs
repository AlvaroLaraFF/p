using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Tests.Tv2
{
    /// <summary>Programmed response of a fake HTTP server on the local loopback.</summary>
    internal sealed class Tv2Response
    {
        public int Status = 200;
        public string ContentType = "application/json";
        public byte[] Body = new byte[0];
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        /// <summary>Wait before answering (slow server).</summary>
        public int DelayMs;
        /// <summary>If &gt; 0, the body is sent in 1 KiB chunks with this pause between them (slow body).</summary>
        public int BodyChunkDelayMs;
        /// <summary>Infinite (chunked) body of this size in MiB at most, to test the read limit.</summary>
        public int EndlessMiB;
        /// <summary>Declared Content-Length different from the real one (0 = the real one).</summary>
        public long DeclaredLength;
        public bool Gzip;

        public static Tv2Response Json(string json) { return new Tv2Response { Body = Encoding.UTF8.GetBytes(json) }; }
        public static Tv2Response Text(string text, string type) { return new Tv2Response { Body = Encoding.UTF8.GetBytes(text), ContentType = type }; }
        public static Tv2Response Redirect(string location, int status = 302)
        {
            var r = new Tv2Response { Status = status };
            r.Headers.Add(new KeyValuePair<string, string>("Location", location));
            return r;
        }
    }

    /// <summary>Minimal HTTP/1.1 server on the local loopback with responses programmed by a function (raw path and query -> response). Records each request.</summary>
    internal sealed class Tv2HttpServer : IDisposable
    {
        private readonly TcpListener _l;
        private readonly Func<string, Tv2Response> _handler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public readonly List<string> Requests = new List<string>();
        public readonly List<string> UserAgents = new List<string>();
        public int Port { get; private set; }
        public string Url { get { return "http://127.0.0.1:" + Port; } }
        public int Count { get { lock (Requests) return Requests.Count; } }

        public Tv2HttpServer(Func<string, Tv2Response> handler, int port = 0)
        {
            _handler = handler;
            _l = new TcpListener(IPAddress.Loopback, port);
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
                while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await s.ReadAsync(one, 0, 1) == 0) return;
                    sb.Append((char)one[0]);
                    if (sb.Length > 65536) return;
                }
                var lines = sb.ToString().Split('\n');
                var path = lines[0].Trim().Split(' ')[1];
                lock (Requests)
                {
                    Requests.Add(path);
                    foreach (var h in lines) if (h.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) UserAgents.Add(h.Substring(11).Trim());
                }
                var r = _handler(path) ?? new Tv2Response { Status = 404 };
                if (r.DelayMs > 0) await Task.Delay(r.DelayMs, _cts.Token);
                byte[] body = r.Body;
                if (r.Gzip)
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true)) gz.Write(body, 0, body.Length);
                        body = ms.ToArray();
                    }
                }
                var head = new StringBuilder();
                head.Append("HTTP/1.1 ").Append(r.Status).Append(" X\r\nContent-Type: ").Append(r.ContentType).Append("\r\nConnection: close\r\n");
                foreach (var h in r.Headers) head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                if (r.Gzip) head.Append("Content-Encoding: gzip\r\n");
                if (r.EndlessMiB > 0) head.Append("Transfer-Encoding: chunked\r\n");
                else head.Append("Content-Length: ").Append(r.DeclaredLength > 0 ? r.DeclaredLength : body.Length).Append("\r\n");
                head.Append("\r\n");
                var hb = Encoding.ASCII.GetBytes(head.ToString());
                await s.WriteAsync(hb, 0, hb.Length);
                if (r.EndlessMiB > 0)
                {
                    var chunk = new byte[16384];
                    for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)' ';
                    var pre = Encoding.ASCII.GetBytes(chunk.Length.ToString("x") + "\r\n");
                    var post = new byte[] { 13, 10 };
                    long limit = r.EndlessMiB * 1024L * 1024;
                    for (long sent = 0; sent < limit; sent += chunk.Length)
                    {
                        await s.WriteAsync(pre, 0, pre.Length);
                        await s.WriteAsync(chunk, 0, chunk.Length);
                        await s.WriteAsync(post, 0, post.Length);
                    }
                    return;
                }
                if (r.BodyChunkDelayMs > 0)
                {
                    for (int o = 0; o < body.Length; o += 1024)
                    {
                        await s.WriteAsync(body, o, Math.Min(1024, body.Length - o));
                        await s.FlushAsync();
                        await Task.Delay(r.BodyChunkDelayMs, _cts.Token);
                    }
                }
                else await s.WriteAsync(body, 0, body.Length);
            }
            catch { }
            finally { try { c.Close(); } catch { } }
        }

        public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }

        /// <summary>Decoded value of a parameter of a path with a query, or null.</summary>
        public static string Param(string pathAndQuery, string name)
        {
            int q = pathAndQuery.IndexOf('?');
            if (q < 0) return null;
            foreach (var part in pathAndQuery.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                if (part.Substring(0, eq) == name) return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return null;
        }
    }
}
