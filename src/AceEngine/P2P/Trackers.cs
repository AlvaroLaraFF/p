using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.P2P
{
    public sealed class AnnounceRequest
    {
        public byte[] InfoHash;
        public byte[] PeerId;
        public int Port;
        public long Uploaded;
        public long Downloaded;
        public long Left;
        /// <summary>"started", "completed", "stopped" or null.</summary>
        public string Event;
        public int NumWant = 200;
    }

    public sealed class AnnounceResult
    {
        public string Tracker;
        public int Interval = 1800;
        public int MinInterval = 300;
        public int Seeders = -1;
        public int Leechers = -1;
        public List<IPEndPoint> Peers = new List<IPEndPoint>();
        public string FailureReason;
        public string Warning;
        public bool Failed { get { return FailureReason != null; } }
    }

    /// <summary>HTTP tracker: GET with info_hash/peer_id percent-encoded from raw bytes, bencode response.</summary>
    public static class HttpTracker
    {
        public const string UserAgent = "ACEStream/ACEStream-2.0";

        public static string BuildUrl(string announce, AnnounceRequest r, string key)
        {
            var sb = new StringBuilder(announce);
            sb.Append(announce.IndexOf('?') >= 0 ? '&' : '?');
            sb.Append("info_hash=").Append(PercentEncode(r.InfoHash));
            sb.Append("&peer_id=").Append(PercentEncode(r.PeerId));
            sb.Append("&port=").Append(r.Port.ToString(CultureInfo.InvariantCulture));
            sb.Append("&uploaded=").Append(r.Uploaded.ToString(CultureInfo.InvariantCulture));
            sb.Append("&downloaded=").Append(r.Downloaded.ToString(CultureInfo.InvariantCulture));
            sb.Append("&left=").Append(r.Left.ToString(CultureInfo.InvariantCulture));
            sb.Append("&numwant=").Append(r.NumWant.ToString(CultureInfo.InvariantCulture));
            sb.Append("&no_peer_id=1&compact=1");
            if (!string.IsNullOrEmpty(r.Event)) sb.Append("&event=").Append(r.Event);
            if (!string.IsNullOrEmpty(key)) sb.Append("&key=").Append(key);
            return sb.ToString();
        }

        public static string PercentEncode(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 3);
            foreach (var b in data)
            {
                if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '-' || b == '_' || b == '.' || b == '~')
                    sb.Append((char)b);
                else
                    sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static AnnounceResult ParseResponse(byte[] body)
        {
            var res = new AnnounceResult();
            BValue v;
            try { v = BencodeParser.Parse(body); }
            catch (BencodeException ex) { res.FailureReason = "response is not bencode: " + ex.Message; return res; }
            var d = v as BDict;
            if (d == null) { res.FailureReason = "response is not a dictionary"; return res; }
            var fail = d.GetString("failure reason");
            if (fail != null) { res.FailureReason = fail; return res; }
            res.Warning = d.GetString("warning message");
            res.Interval = (int)(d.GetInt("interval") ?? 1800);
            res.MinInterval = (int)(d.GetInt("min interval") ?? 300);
            res.Seeders = (int)(d.GetInt("complete") ?? -1);
            res.Leechers = (int)(d.GetInt("incomplete") ?? -1);
            var peers = d.Get("peers");
            var compact = peers as BString;
            if (compact != null) ParseCompact(compact.Value, res.Peers);
            var list = peers as BList;
            if (list != null)
            {
                foreach (var item in list.Items)
                {
                    var pd = item as BDict;
                    if (pd == null) continue;
                    var ip = pd.GetString("ip");
                    var port = pd.GetInt("port");
                    IPAddress addr;
                    if (ip != null && port.HasValue && port.Value > 0 && port.Value < 65536 && IPAddress.TryParse(ip, out addr)
                        && addr.AddressFamily == AddressFamily.InterNetwork)
                        res.Peers.Add(new IPEndPoint(addr, (int)port.Value));
                }
            }
            // peers6 is ignored (the original ignores it too).
            return res;
        }

        public static void ParseCompact(byte[] data, List<IPEndPoint> into)
        {
            for (int i = 0; i + 6 <= data.Length; i += 6)
            {
                int port = (data[i + 4] << 8) | data[i + 5];
                if (port == 0) continue;
                into.Add(new IPEndPoint(new IPAddress(new[] { data[i], data[i + 1], data[i + 2], data[i + 3] }), port));
            }
        }

        public static async Task<AnnounceResult> AnnounceAsync(HttpClient http, string announce, AnnounceRequest r, string key, CancellationToken ct)
        {
            var url = BuildUrl(announce, r, key);
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    var body = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.TrackerBody, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode && body.Length == 0)
                        return new AnnounceResult { Tracker = announce, FailureReason = "HTTP " + (int)resp.StatusCode };
                    var res = ParseResponse(body);
                    res.Tracker = announce;
                    return res;
                }
            }
        }
    }

    /// <summary>UDP tracker (BEP15; the original's is a simplified version without retries).</summary>
    public static class UdpTracker
    {
        public const long ProtocolId = 0x41727101980L;

        public static byte[] BuildConnect(uint tid)
        {
            var b = new byte[16];
            Wire.PutU64(b, 0, (ulong)ProtocolId); Wire.PutU32(b, 8, 0); Wire.PutU32(b, 12, tid);
            return b;
        }

        public static bool ParseConnect(byte[] b, uint tid, out ulong connectionId, out string error)
        {
            connectionId = 0; error = null;
            if (b.Length >= 8 && Wire.GetU32(b, 0) == 3) { error = ErrorText(b); return false; }
            if (b.Length < 16 || Wire.GetU32(b, 0) != 0 || Wire.GetU32(b, 4) != tid) return false;
            connectionId = Wire.GetU64(b, 8);
            return true;
        }

        public static byte[] BuildAnnounce(ulong connectionId, uint tid, AnnounceRequest r, uint key)
        {
            var b = new byte[98];
            Wire.PutU64(b, 0, connectionId); Wire.PutU32(b, 8, 1); Wire.PutU32(b, 12, tid);
            Buffer.BlockCopy(r.InfoHash, 0, b, 16, 20);
            Buffer.BlockCopy(r.PeerId, 0, b, 36, 20);
            Wire.PutU64(b, 56, (ulong)r.Downloaded);
            Wire.PutU64(b, 64, (ulong)r.Left);
            Wire.PutU64(b, 72, (ulong)r.Uploaded);
            uint ev = r.Event == "completed" ? 1u : r.Event == "started" ? 2u : r.Event == "stopped" ? 3u : 0u;
            Wire.PutU32(b, 80, ev);
            Wire.PutU32(b, 84, 0);                       // ip
            Wire.PutU32(b, 88, key);
            Wire.PutU32(b, 92, unchecked((uint)r.NumWant));
            Wire.PutU16(b, 96, (ushort)r.Port);
            return b;
        }

        public static bool ParseAnnounce(byte[] b, uint tid, AnnounceResult res, out string error)
        {
            error = null;
            if (b.Length >= 8 && Wire.GetU32(b, 0) == 3 && Wire.GetU32(b, 4) == tid) { error = ErrorText(b); return false; }
            if (b.Length < 20 || Wire.GetU32(b, 0) != 1 || Wire.GetU32(b, 4) != tid) return false;
            res.Interval = (int)Wire.GetU32(b, 8);
            res.Leechers = (int)Wire.GetU32(b, 12);
            res.Seeders = (int)Wire.GetU32(b, 16);
            var peers = new byte[b.Length - 20];
            Buffer.BlockCopy(b, 20, peers, 0, peers.Length);
            HttpTracker.ParseCompact(peers, res.Peers);
            return true;
        }

        private static string ErrorText(byte[] b)
        {
            return b.Length > 8 ? Encoding.UTF8.GetString(b, 8, b.Length - 8) : "tracker error";
        }

        public static async Task<AnnounceResult> AnnounceAsync(string host, int port, string label, AnnounceRequest r, uint key, int[] timeoutsSeconds, CancellationToken ct)
        {
            IPAddress ip = await Net.ResolveIPv4Async(host, ct).ConfigureAwait(false);
            if (ip == null) return new AnnounceResult { Tracker = label, FailureReason = "could not resolve " + host };
            var ep = new IPEndPoint(ip, port);
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.Connect(ep);
                Task<UdpReceiveResult> pending = null;

                Func<TimeSpan, Task<byte[]>> recv = async timeout =>
                {
                    if (pending == null) pending = udp.ReceiveAsync();
                    var done = await Task.WhenAny(pending, Task.Delay(timeout, ct)).ConfigureAwait(false);
                    if (done == pending)
                    {
                        var rr = await pending.ConfigureAwait(false);       // if it failed, it throws (SocketException)
                        pending = null;
                        return rr.Buffer;
                    }
                    ct.ThrowIfCancellationRequested();
                    return null;
                };

                string lastError = "no response";
                foreach (var tsec in timeoutsSeconds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        uint tid = (uint)Rng.Next(1, int.MaxValue);
                        var c = BuildConnect(tid);
                        await udp.SendAsync(c, c.Length).ConfigureAwait(false);
                        ulong cid = 0;
                        bool connected = false;
                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(tsec);
                        while (!connected)
                        {
                            var left = deadline - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero) break;
                            var resp = await recv(left).ConfigureAwait(false);
                            if (resp == null) break;
                            string err;
                            if (ParseConnect(resp, tid, out cid, out err)) connected = true;
                            else if (err != null) return new AnnounceResult { Tracker = label, FailureReason = err };
                        }
                        if (!connected) continue;

                        uint tid2 = (uint)Rng.Next(1, int.MaxValue);
                        var a = BuildAnnounce(cid, tid2, r, key);
                        await udp.SendAsync(a, a.Length).ConfigureAwait(false);
                        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(tsec);
                        while (true)
                        {
                            var left = deadline - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero) break;
                            var resp = await recv(left).ConfigureAwait(false);
                            if (resp == null) break;
                            var res = new AnnounceResult { Tracker = label };
                            string err;
                            if (ParseAnnounce(resp, tid2, res, out err)) return res;
                            if (err != null) return new AnnounceResult { Tracker = label, FailureReason = err };
                        }
                    }
                    catch (SocketException ex)
                    {
                        lastError = ex.SocketErrorCode.ToString();
                        pending = null;
                    }
                }
                return new AnnounceResult { Tracker = label, FailureReason = lastError };
            }
        }
    }

    /// <summary>Announce to a tracker of any supported scheme (http, https, udp). Used by VOD, live and probing.</summary>
    public static class TrackerClient
    {
        public static bool IsSupported(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "udp";
        }

        public static async Task<AnnounceResult> AnnounceAsync(HttpClient http, string url, AnnounceRequest req, uint key, CancellationToken ct, int timeoutSeconds = 30)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return new AnnounceResult { Tracker = url, FailureReason = "invalid tracker URL" };
            using (var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                tcts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                if (uri.Scheme == "http" || uri.Scheme == "https")
                    return await HttpTracker.AnnounceAsync(http, url, req, key.ToString("x6", CultureInfo.InvariantCulture).Substring(0, 6), tcts.Token).ConfigureAwait(false);
                if (uri.Scheme == "udp")
                    return await UdpTracker.AnnounceAsync(uri.Host, uri.Port > 0 ? uri.Port : 80, url, req, key, new[] { 5, 10, 15 }, tcts.Token).ConfigureAwait(false);
            }
            return new AnnounceResult { Tracker = url, FailureReason = "unsupported scheme: " + uri.Scheme };
        }
    }

    internal static class Net
    {
        public static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken ct)
        {
            IPAddress direct;
            if (IPAddress.TryParse(host, out direct)) return direct.AddressFamily == AddressFamily.InterNetwork ? direct : null;
            try
            {
                var task = Dns.GetHostAddressesAsync(host);
                var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
                if (done != task) { ct.ThrowIfCancellationRequested(); return null; }
                foreach (var a in task.Result) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            }
            catch (SocketException) { }
            catch (AggregateException) { }
            return null;
        }

        public static bool TryParseHostPort(string s, out string host, out int port)
        {
            host = null; port = 0;
            int colon = s.LastIndexOf(':');
            if (colon <= 0) return false;
            host = s.Substring(0, colon);
            return int.TryParse(s.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port < 65536;
        }
    }
}
