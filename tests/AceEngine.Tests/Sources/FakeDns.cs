using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Tests.Sources
{
    /// <summary>Fake DNS server (UDP and TCP on the same port) that answers TXT, with truncation, NXDOMAIN, wrong ID and silence.</summary>
    internal sealed class FakeDnsServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public readonly Dictionary<string, List<string>> Records = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly List<string> UdpNames = new List<string>(), TcpNames = new List<string>();
        public int TruncateUdpOverBytes = 512;
        public bool Silent;                         // does not answer over UDP nor TCP
        public bool WrongIdFirst;                   // before the good response it sends one with another ID
        public bool ServfailAll;
        public int Port { get; private set; }

        public FakeDnsServer()
        {
            while (true)
            {
                var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                try
                {
                    var tcp = new TcpListener(IPAddress.Loopback, port);
                    tcp.Start();
                    _udp = udp; _tcp = tcp; Port = port;
                    break;
                }
                catch (SocketException) { udp.Dispose(); }
            }
            Task.Run(UdpLoop);
            Task.Run(TcpLoop);
        }

        public void Add(string name, params string[] txt) { Records[name] = txt.ToList(); }

        private static string ReadName(byte[] q, ref int pos)
        {
            var sb = new StringBuilder();
            while (q[pos] != 0) { int l = q[pos++]; if (sb.Length > 0) sb.Append('.'); sb.Append(Encoding.ASCII.GetString(q, pos, l)); pos += l; }
            pos++;
            return sb.ToString();
        }

        private byte[] Answer(byte[] q, bool udp, ushort? idOverride = null)
        {
            int pos = 12;
            string name = ReadName(q, ref pos);
            int qend = pos + 4;
            lock (udp ? UdpNames : TcpNames) (udp ? UdpNames : TcpNames).Add(name);
            List<string> txt;
            bool found = Records.TryGetValue(name, out txt) && !ServfailAll;
            var ms = new MemoryStream();
            ushort id = idOverride ?? (ushort)((q[0] << 8) | q[1]);
            ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
            int rcode = ServfailAll ? 2 : (found ? 0 : 3);
            ms.WriteByte(0x81); ms.WriteByte((byte)(0x80 | rcode));
            ms.WriteByte(0); ms.WriteByte(1);
            ms.WriteByte(0); ms.WriteByte((byte)(found ? txt.Count : 0));
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
            ms.Write(q, 12, qend - 12);
            if (found)
            {
                foreach (var t in txt)
                {
                    var data = Encoding.UTF8.GetBytes(t);
                    var rd = new MemoryStream();
                    for (int i = 0; i < data.Length; i += 255)
                    {
                        int n = Math.Min(255, data.Length - i);
                        rd.WriteByte((byte)n); rd.Write(data, i, n);
                    }
                    if (data.Length == 0) rd.WriteByte(0);
                    var rdata = rd.ToArray();
                    ms.WriteByte(0xC0); ms.WriteByte(0x0C);                 // compression pointer to the question's name
                    ms.WriteByte(0); ms.WriteByte(16); ms.WriteByte(0); ms.WriteByte(1);
                    ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(60);
                    ms.WriteByte((byte)(rdata.Length >> 8)); ms.WriteByte((byte)rdata.Length);
                    ms.Write(rdata, 0, rdata.Length);
                }
            }
            var resp = ms.ToArray();
            if (udp && resp.Length > TruncateUdpOverBytes)
            {
                var tc = new byte[qend];
                Buffer.BlockCopy(resp, 0, tc, 0, qend);
                tc[2] |= 0x02; tc[6] = 0; tc[7] = 0;
                return tc;
            }
            return resp;
        }

        private async Task UdpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(); } catch { return; }
                if (Silent) continue;
                if (WrongIdFirst)
                {
                    var bad = Answer(r.Buffer, true, 0x1234);
                    await _udp.SendAsync(bad, bad.Length, r.RemoteEndPoint);
                }
                var resp = Answer(r.Buffer, true);
                await _udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
            }
        }

        private async Task TcpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _tcp.AcceptTcpClientAsync(); } catch { return; }
                var cc = c;
                var ignored = Task.Run(async () =>
                {
                    try
                    {
                        if (Silent) { await Task.Delay(60000, _cts.Token); return; }
                        var s = cc.GetStream();
                        var lb = new byte[2];
                        int got = 0;
                        while (got < 2) { int n = await s.ReadAsync(lb, got, 2 - got); if (n == 0) return; got += n; }
                        int len = (lb[0] << 8) | lb[1];
                        var q = new byte[len];
                        got = 0;
                        while (got < len) { int n = await s.ReadAsync(q, got, len - got); if (n == 0) return; got += n; }
                        var resp = Answer(q, false);
                        var framed = new byte[2 + resp.Length];
                        framed[0] = (byte)(resp.Length >> 8); framed[1] = (byte)resp.Length;
                        Buffer.BlockCopy(resp, 0, framed, 2, resp.Length);
                        await s.WriteAsync(framed, 0, framed.Length);
                    }
                    catch { }
                    finally { cc.Close(); }
                });
            }
        }

        /// <summary>Encodes items the way mylinkspaste does: JSON, optional gzip, Base64, split into 255-byte strings.</summary>
        public static string EncodeRecord(object elements, bool gzip, bool quoted = false)
        {
            var json = Encoding.UTF8.GetBytes(MiniJson.Stringify(elements));
            byte[] data = json;
            if (gzip)
            {
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(json, 0, json.Length);
                    data = ms.ToArray();
                }
            }
            return Convert.ToBase64String(data);
        }

        public void Dispose() { _cts.Cancel(); try { _udp.Dispose(); } catch { } try { _tcp.Stop(); } catch { } }
    }

    /// <summary>Minimal HTTP server that answers any GET with a fixed body (DoH, M3U lists...).</summary>
    internal sealed class FakeHttpServer : IDisposable
    {
        private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
        public readonly List<string> Requests = new List<string>();
        public Func<string, KeyValuePair<int, byte[]>> Handler;
        public string ContentType = "application/json";
        public int Port { get; private set; }

        public FakeHttpServer(Func<string, KeyValuePair<int, byte[]>> handler)
        {
            Handler = handler;
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
                var ignored = Task.Run(async () =>
                {
                    try
                    {
                        var s = cc.GetStream();
                        var sb = new StringBuilder(); var one = new byte[1];
                        while (!sb.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one, 0, 1) == 0) return; sb.Append((char)one[0]); }
                        var line = sb.ToString().Split('\n')[0].Trim();
                        lock (Requests) Requests.Add(line);
                        var path = line.Split(' ')[1];
                        var r = Handler(path);
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + r.Key + " X\r\nContent-Type: " + ContentType + "\r\nContent-Length: " + r.Value.Length + "\r\nConnection: close\r\n\r\n");
                        await s.WriteAsync(head, 0, head.Length); await s.WriteAsync(r.Value, 0, r.Value.Length);
                    }
                    catch { }
                    finally { cc.Close(); }
                });
            }
        }

        public void Dispose() { try { _l.Stop(); } catch { } }
    }
}
