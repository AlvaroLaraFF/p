using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Sources
{
    public sealed class DnsOptions
    {
        /// <summary>DNS servers over UDP/TCP 53 (8.8.8.8, the same one the original app uses).</summary>
        public IList<string> Servers = new List<string> { "8.8.8.8" };
        public int Port = 53;
        /// <summary>Deadline per query (5 s, like the original).</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(5);
        /// <summary>Attempts over UDP before moving on to TCP or DoH.</summary>
        public int UdpAttempts = 2;
        /// <summary>Fallback DNS over HTTPS if port 53 is blocked.</summary>
        public bool UseDohFallback = true;
        public string DohUrl = "https://dns.google/resolve";
    }

    public sealed class DnsException : Exception
    {
        public int Rcode;
        public DnsException(string message, int rcode = -1) : base(message) { Rcode = rcode; }
    }

    /// <summary>
    /// Minimal DNS client for TXT records (RFC 1035), dependency-free: UDP to 8.8.8.8:53; if the response comes truncated (TC=1), TCP with a
    /// 2-byte length prefix; and DNS over HTTPS (dns.google/resolve, JSON) as a fallback. System.Net.Dns cannot ask for TXT.
    /// Each TXT record is several character-strings of up to 255 bytes that are concatenated without a separator.
    /// </summary>
    public sealed class DnsTxtClient
    {
        private const string Tag = "dns";
        private readonly DnsOptions _opt;
        private readonly HttpClient _http;

        public DnsTxtClient(DnsOptions options = null, HttpClient http = null)
        {
            _opt = options ?? new DnsOptions();
            _http = http;
        }

        // ---------- Messages (public for the tests) ----------

        /// <summary>TXT/IN query with RD=1 and no EDNS.</summary>
        public static byte[] BuildQuery(string name, ushort id)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("empty name", "name");
            var ms = new MemoryStream();
            ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
            ms.WriteByte(0x01); ms.WriteByte(0x00);                       // flags: RD
            ms.WriteByte(0); ms.WriteByte(1);                             // QDCOUNT
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
            int total = 1;
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var lb = Encoding.ASCII.GetBytes(label);                  // the labels are sent as they are, without changing case
                if (lb.Length == 0 || lb.Length > 63) throw new ArgumentException("invalid DNS label: '" + label + "'");
                ms.WriteByte((byte)lb.Length); ms.Write(lb, 0, lb.Length);
                total += 1 + lb.Length;
            }
            if (total > 255) throw new ArgumentException("DNS name too long");
            ms.WriteByte(0);
            ms.WriteByte(0); ms.WriteByte(16);                            // QTYPE TXT
            ms.WriteByte(0); ms.WriteByte(1);                             // QCLASS IN
            return ms.ToArray();
        }

        public sealed class Response
        {
            public ushort Id;
            public bool Truncated, IsResponse;
            public int Rcode;
            public List<string> Txt = new List<string>();
        }

        /// <summary>Maximum TXT records and TXT bytes accepted from a response (real codes use a record of ~2 KB).</summary>
        public const int MaxTxtRecords = 16;
        public const int MaxTxtBytes = 64 * 1024;
        public const int MaxResponseBytes = 65535;

        /// <summary>Interprets a response. Returns null if it is malformed.</summary>
        public static Response ParseResponse(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 12 || b.Length > MaxResponseBytes) return null;
                var r = new Response { Id = (ushort)((b[0] << 8) | b[1]) };
                r.IsResponse = (b[2] & 0x80) != 0;
                r.Truncated = (b[2] & 0x02) != 0;
                r.Rcode = b[3] & 0x0f;
                int qd = (b[4] << 8) | b[5], an = (b[6] << 8) | b[7];
                int pos = 12;
                for (int i = 0; i < qd; i++) { pos = SkipName(b, pos); pos += 4; }
                for (int i = 0; i < an; i++)
                {
                    pos = SkipName(b, pos);
                    if (pos + 10 > b.Length) return null;
                    int type = (b[pos] << 8) | b[pos + 1];
                    int rdlen = (b[pos + 8] << 8) | b[pos + 9];
                    pos += 10;
                    if (pos + rdlen > b.Length) return null;
                    if (type == 16 && r.Txt.Count < MaxTxtRecords)
                    {
                        var sb = new StringBuilder();
                        int p = pos, end = pos + rdlen;
                        var bytes = new List<byte>();
                        while (p < end)
                        {
                            int l = b[p++];
                            if (p + l > end) return null;
                            if (bytes.Count + l > MaxTxtBytes) return null;
                            for (int k = 0; k < l; k++) bytes.Add(b[p + k]);       // character-strings concatenated without a separator
                            p += l;
                        }
                        r.Txt.Add(Encoding.UTF8.GetString(bytes.ToArray()));
                    }
                    pos += rdlen;
                }
                return r;
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        private static int SkipName(byte[] b, int pos)
        {
            // Name with compression (pointers 11xxxxxx): it advances without following them
            while (true)
            {
                int l = b[pos];
                if (l == 0) return pos + 1;
                if ((l & 0xc0) == 0xc0) return pos + 2;
                pos += 1 + l;
            }
        }

        // ---------- Query ----------

        /// <summary>TXT records of 'name' (each one, with its strings concatenated). Empty list if there are no records; throws DnsException if everything fails.</summary>
        public async Task<IList<string>> QueryTxtAsync(string name, CancellationToken ct)
        {
            Exception last = null;
            foreach (var server in _opt.Servers)
            {
                IPAddress ip;
                if (!IPAddress.TryParse(server, out ip)) continue;
                try
                {
                    var r = await QueryServerAsync(ip, name, ct).ConfigureAwait(false);
                    if (r.Rcode == 3) return new List<string>();                       // NXDOMAIN: does not exist
                    if (r.Rcode != 0) throw new DnsException("the DNS server answered RCODE " + r.Rcode, r.Rcode);
                    return r.Txt;
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; last = new DnsException("timed out querying " + server); }
                catch (DnsException ex) { last = ex; if (ex.Rcode > 0) break; }
                catch (SocketException ex) { last = ex; }
                catch (IOException ex) { last = ex; }
            }
            if (_opt.UseDohFallback)
            {
                try
                {
                    AceLog.Info(Tag, "DNS 53 does not answer (" + (last == null ? "?" : last.Message) + "); trying DNS over HTTPS");
                    return await QueryDohAsync(name, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; last = new DnsException("timed out in DoH"); }
                catch (HttpRequestException ex) { last = ex; }
            }
            throw new DnsException("could not query the TXT of " + name + ": " + (last == null ? "no servers" : last.Message));
        }

        private async Task<Response> QueryServerAsync(IPAddress server, string name, CancellationToken ct)
        {
            bool truncated = false;
            for (int attempt = 0; attempt < Math.Max(1, _opt.UdpAttempts); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                ushort id = (ushort)Rng.Next(1, 65536);
                var query = BuildQuery(name, id);
                using (var udp = new UdpClient(server.AddressFamily))
                {
                    udp.Connect(server, _opt.Port);
                    await udp.SendAsync(query, query.Length).ConfigureAwait(false);
                    var deadline = DateTime.UtcNow + _opt.Timeout;
                    while (true)
                    {
                        var left = deadline - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) break;
                        var recv = udp.ReceiveAsync();
                        var done = await Task.WhenAny(recv, Task.Delay(left, ct)).ConfigureAwait(false);
                        if (done != recv) { ct.ThrowIfCancellationRequested(); break; }
                        var resp = ParseResponse(recv.Result.Buffer);
                        if (resp == null || !resp.IsResponse || resp.Id != id) continue;       // foreign or malformed datagram
                        if (resp.Truncated) { truncated = true; break; }
                        return resp;
                    }
                }
                if (truncated) break;
            }
            if (!truncated) throw new DnsException("no UDP response from " + server);
            AceLog.Debug(Tag, "truncated response (TC=1); repeating over TCP");
            return await QueryTcpAsync(server, name, ct).ConfigureAwait(false);
        }

        private async Task<Response> QueryTcpAsync(IPAddress server, string name, CancellationToken ct)
        {
            ushort id = (ushort)Rng.Next(1, 65536);
            var query = BuildQuery(name, id);
            using (var tcp = new TcpClient(server.AddressFamily))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_opt.Timeout);
                var connect = tcp.ConnectAsync(server, _opt.Port).Observed();
                var done = await Task.WhenAny(connect, Task.Delay(_opt.Timeout, cts.Token)).ConfigureAwait(false);
                if (done != connect) { ct.ThrowIfCancellationRequested(); throw new DnsException("timed out connecting over TCP to " + server); }
                await connect.ConfigureAwait(false);
                var s = tcp.GetStream();
                var framed = new byte[2 + query.Length];
                framed[0] = (byte)(query.Length >> 8); framed[1] = (byte)query.Length;
                Buffer.BlockCopy(query, 0, framed, 2, query.Length);
                await s.WriteAsync(framed, 0, framed.Length, cts.Token).ConfigureAwait(false);
                var lenBuf = new byte[2];
                await ReadExact(s, lenBuf, 2, cts.Token).ConfigureAwait(false);
                int len = (lenBuf[0] << 8) | lenBuf[1];
                var body = new byte[len];
                await ReadExact(s, body, len, cts.Token).ConfigureAwait(false);
                var resp = ParseResponse(body);
                if (resp == null || resp.Id != id) throw new DnsException("invalid TCP response");
                return resp;
            }
        }

        private static async Task ReadExact(Stream s, byte[] buf, int n, CancellationToken ct)
        {
            int got = 0;
            while (got < n)
            {
                int r = await s.ReadAsync(buf, got, n - got, ct).ConfigureAwait(false);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
        }

        // ---------- DNS over HTTPS (Google JSON format) ----------

        private async Task<IList<string>> QueryDohAsync(string name, CancellationToken ct)
        {
            var http = _http;
            bool own = false;
            if (http == null) { http = new HttpClient { Timeout = _opt.Timeout }; own = true; }
            try
            {
                string url = _opt.DohUrl + (_opt.DohUrl.IndexOf('?') >= 0 ? "&" : "?") + "name=" + Uri.EscapeDataString(name) + "&type=TXT";
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    cts.CancelAfter(_opt.Timeout);
                    req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var body = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.DohBody, cts.Token).ConfigureAwait(false);
                        return ParseDohJson(body);
                    }
                }
            }
            finally { if (own) http.Dispose(); }
        }

        public static IList<string> ParseDohJson(byte[] body)
        {
            var o = MiniJson.AsObject(MiniJson.Parse(body));
            long status = MiniJson.GetLong(o, "Status") ?? 0;
            if (status == 3) return new List<string>();
            if (status != 0) throw new DnsException("DoH: Status " + status, (int)status);
            var res = new List<string>();
            var answers = MiniJson.GetArray(o, "Answer");
            if (answers == null) return res;
            foreach (var a in answers)
            {
                var ao = MiniJson.AsObject(a);
                if ((MiniJson.GetLong(ao, "type") ?? 0) != 16) continue;
                string data = MiniJson.GetString(ao, "data");
                if (data == null) continue;
                res.Add(UnquoteTxt(data));
            }
            return res;
        }

        /// <summary>Google's data field carries the character-strings in quotes and separated by spaces: "a" "b" -> ab (with \" and \\ escaped).</summary>
        public static string UnquoteTxt(string data)
        {
            if (data.IndexOf('"') < 0) return data;
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < data.Length; i++)
            {
                char c = data[i];
                if (!inQ) { if (c == '"') inQ = true; continue; }
                if (c == '\\' && i + 1 < data.Length) { sb.Append(data[++i]); continue; }
                if (c == '"') { inQ = false; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
