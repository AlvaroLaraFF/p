using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Tests.NetTests
{
    internal sealed class FakeHttpRequest
    {
        public string Method = "", Path = "", Version = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string BodyText { get { return Encoding.UTF8.GetString(Body); } }
        public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
    }

    internal sealed class FakeHttpResponse
    {
        public int Status = 200;
        public string Reason = "OK";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public byte[] Body = new byte[0];
        /// <summary>Never answers (the connection stays open until the server is closed).</summary>
        public bool Hang;
        /// <summary>Closes the connection without writing anything.</summary>
        public bool CloseSilently;
        /// <summary>If not null, writes the stream by hand (hostile responses: infinite body, drip...).</summary>
        public Func<Stream, CancellationToken, Task> Custom;

        public static FakeHttpResponse Xml(string xml, int status = 200)
        {
            var r = new FakeHttpResponse { Status = status, Reason = status == 200 ? "OK" : "Internal Server Error", Body = new UTF8Encoding(false).GetBytes(xml) };
            r.Headers["Content-Type"] = "text/xml; charset=\"utf-8\"";
            return r;
        }
    }

    /// <summary>Minimal HTTP/1.1 server on the local loopback (TcpListener), with full control over what it answers: used by FakeIgdRouter.</summary>
    internal sealed class FakeHttpServer : IDisposable
    {
        private readonly TcpListener _l;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
        public readonly ConcurrentQueue<FakeHttpRequest> Requests = new ConcurrentQueue<FakeHttpRequest>();
        public Func<FakeHttpRequest, FakeHttpResponse> Handler = r => new FakeHttpResponse { Status = 404, Reason = "Not Found" };
        public int Port { get; private set; }
        public int Connections;

        public FakeHttpServer()
        {
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start(16);
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            var ignored = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _clients.Add(c);
                Interlocked.Increment(ref Connections);
                var ignored = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var req = await ReadRequest(s).ConfigureAwait(false);
                if (req == null) return;
                Requests.Enqueue(req);
                FakeHttpResponse resp;
                try { resp = Handler(req); } catch (Exception ex) { resp = new FakeHttpResponse { Status = 500, Reason = "Handler", Body = Encoding.UTF8.GetBytes(ex.Message) }; }
                if (resp.CloseSilently) return;
                if (resp.Hang) { try { await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } return; }
                if (resp.Custom != null) { await resp.Custom(s, _cts.Token).ConfigureAwait(false); return; }
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 ").Append(resp.Status).Append(' ').Append(resp.Reason).Append("\r\n");
                foreach (var kv in resp.Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                sb.Append("Content-Length: ").Append(resp.Body.Length).Append("\r\nConnection: close\r\n\r\n");
                var head = Encoding.ASCII.GetBytes(sb.ToString());
                await s.WriteAsync(head, 0, head.Length, _cts.Token).ConfigureAwait(false);
                if (resp.Body.Length > 0) await s.WriteAsync(resp.Body, 0, resp.Body.Length, _cts.Token).ConfigureAwait(false);
                await s.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch (Exception) { } }
        }

        private static async Task<FakeHttpRequest> ReadRequest(NetworkStream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[1];
            while (ms.Length < 65536)
            {
                int n = await s.ReadAsync(buf, 0, 1).ConfigureAwait(false);
                if (n == 0) return null;
                ms.WriteByte(buf[0]);
                int l = (int)ms.Length;
                if (l >= 4)
                {
                    var raw = ms.GetBuffer();
                    if (raw[l - 4] == '\r' && raw[l - 3] == '\n' && raw[l - 2] == '\r' && raw[l - 1] == '\n') break;
                }
            }
            string head = Encoding.ASCII.GetString(ms.ToArray());
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var req = new FakeHttpRequest();
            var first = lines[0].Split(' ');
            if (first.Length < 3) return null;
            req.Method = first[0]; req.Path = first[1]; req.Version = first[2];
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            int len = 0;
            string cl = req.Header("Content-Length");
            if (cl != null) int.TryParse(cl, out len);
            if (len > 0 && len < 1024 * 1024)
            {
                var body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = await s.ReadAsync(body, got, len - got).ConfigureAwait(false);
                    if (n == 0) break;
                    got += n;
                }
                req.Body = body;
            }
            return req;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _l.Stop(); } catch (Exception) { }
            foreach (var c in _clients) { try { c.Close(); } catch (Exception) { } }
        }
    }
}
