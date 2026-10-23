using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Net
{
    /// <summary>A response to an M-SEARCH, already parsed. All of it comes from the local network: it is treated as untrusted.</summary>
    public sealed class SsdpResponse
    {
        /// <summary>URL of the device description (always http and with a literal IPv4; already checked).</summary>
        public Uri Location;
        public string St = "", Usn = "", Server = "";
        /// <summary>Who answered (source IP and port of the datagram).</summary>
        public IPEndPoint From;
        /// <summary>IP of the local interface the response arrived on.</summary>
        public IPAddress LocalAddress;
        /// <summary>Time since the search started.</summary>
        public TimeSpan ReceivedAfter;

        public bool IsIgd { get { return St.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) >= 0; } }
        /// <summary>Version announced in the ST (1 or 2), 0 if it is not an IGD.</summary>
        public int IgdVersion
        {
            get
            {
                if (!IsIgd) return 0;
                int i = St.LastIndexOf(':');
                int v;
                return i >= 0 && int.TryParse(St.Substring(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : 1;
            }
        }
        public override string ToString() { return From + " ST=" + St + " LOCATION=" + Location + (Server.Length > 0 ? " SERVER=" + Server : ""); }

        /// <summary>
        /// Parses a response datagram ("HTTP/1.1 200 OK" + headers). Returns false on anything odd: binary, too long, no LOCATION,
        /// a LOCATION that is not http://IPv4[:port]/path, unterminated headers... Never throws.
        /// </summary>
        public static bool TryParse(byte[] data, int length, out SsdpResponse response)
        {
            response = null;
            try
            {
                if (data == null || length < 12 || length > 4096 || length > data.Length) return false;
                var sb = new StringBuilder(length);
                for (int i = 0; i < length; i++)
                {
                    byte b = data[i];
                    if (b == 0) return false;                       // text: a NUL gives away binary
                    sb.Append((char)b);
                }
                string text = sb.ToString();
                int pos = 0;
                string first = NextLine(text, ref pos);
                if (first == null || !first.StartsWith("HTTP/1.", StringComparison.Ordinal)) return false;
                var parts = first.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[1] != "200") return false;

                string location = null, st = "", usn = "", server = "";
                bool haveSt = false, haveUsn = false, haveServer = false;
                int headers = 0;
                string line;
                while ((line = NextLine(text, ref pos)) != null)
                {
                    if (line.Length == 0) break;                    // end of headers
                    if (++headers > 40) return false;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (value.Length > 512) value = value.Substring(0, 512);
                    if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) { if (location == null) location = value; }
                    else if (name.Equals("ST", StringComparison.OrdinalIgnoreCase)) { if (!haveSt) { st = Clean(value); haveSt = true; } }
                    else if (name.Equals("USN", StringComparison.OrdinalIgnoreCase)) { if (!haveUsn) { usn = Clean(value); haveUsn = true; } }
                    else if (name.Equals("SERVER", StringComparison.OrdinalIgnoreCase)) { if (!haveServer) { server = Clean(value); haveServer = true; } }
                }
                if (string.IsNullOrEmpty(location)) return false;
                Uri uri;
                if (!TryParseLocation(location, out uri)) return false;
                response = new SsdpResponse { Location = uri, St = st, Usn = usn, Server = server };
                return true;
            }
            catch (Exception)
            {
                response = null;
                return false;
            }
        }

        /// <summary>http://IPv4[:port]/path; no user, no hostname, no scheme other than http.</summary>
        internal static bool TryParseLocation(string s, out Uri uri)
        {
            uri = null;
            if (s.Length > 300) return false;
            foreach (char c in s) if (c < 0x21 || c > 0x7e) return false;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return false;
            if (u.Scheme != "http") return false;
            if (!string.IsNullOrEmpty(u.UserInfo)) return false;
            IPAddress ip;
            if (u.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(u.Host, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            if (u.Port < 1 || u.Port > 65535) return false;
            uri = u;
            return true;
        }

        private static string Clean(string v)
        {
            var sb = new StringBuilder(v.Length);
            foreach (char c in v) sb.Append(c < 0x20 || c > 0x7e ? '?' : c);
            return sb.ToString();
        }

        private static string NextLine(string text, ref int pos)
        {
            if (pos >= text.Length) return null;
            int nl = text.IndexOf('\n', pos);
            string line;
            if (nl < 0) { line = text.Substring(pos); pos = text.Length; }
            else { line = text.Substring(pos, nl - pos); pos = nl + 1; }
            return line.TrimEnd('\r');
        }
    }

    public sealed class SsdpOptions
    {
        public static readonly string[] DefaultTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "upnp:rootdevice"
        };

        /// <summary>Total duration of the search.</summary>
        public TimeSpan Window = TimeSpan.FromSeconds(6);
        /// <summary>How many times the M-SEARCH round is repeated (UDP loses packets) and how often.</summary>
        public int Retries = 3;
        public TimeSpan RetryInterval = TimeSpan.FromMilliseconds(1000);
        /// <summary>MX of the M-SEARCH: maximum seconds the device may wait before answering.</summary>
        public int Mx = 2;
        public IList<string> SearchTargets = new List<string>(DefaultTargets);
        /// <summary>Send to 239.255.255.250:1900 (on each interface).</summary>
        public bool UseMulticast = true;
        /// <summary>Also send directly (unicast) to the gateways: many routers answer even if multicast is filtered.</summary>
        public bool UseUnicastToGateway = true;
        public IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
        public int Port = 1900;
        /// <summary>Finish shortly after the first IGD response instead of waiting for the whole window.</summary>
        public bool StopAfterFirstIgd = true;
        public TimeSpan GraceAfterFirstIgd = TimeSpan.FromMilliseconds(400);
        public int MaxResponses = 32;
        /// <summary>Tests only: accepts LOCATION with 127.x.x.x. In production a LOCATION to loopback would be an attempt to point at the device itself.</summary>
        public bool AllowLoopbackLocation = false;
        public string UserAgent = "Linux/1.0 UPnP/1.1 P/1.0";
        /// <summary>Called with each new response as soon as it arrives (on a receive thread), while the search is still listening. A handler's exception ends up in SsdpResult.Errors.</summary>
        public Action<SsdpResponse> OnResponse;

        public SsdpOptions Clone()
        {
            var c = (SsdpOptions)MemberwiseClone();
            c.SearchTargets = new List<string>(SearchTargets);
            return c;
        }
    }

    public sealed class SsdpResult
    {
        public readonly List<SsdpResponse> Responses = new List<SsdpResponse>();
        /// <summary>Send/bind errors (not repeated). Empty if all went well.</summary>
        public readonly List<string> Errors = new List<string>();
        public int SocketsOpened, MessagesSent;
        public TimeSpan Elapsed;
        public bool AnyIgd { get { return Responses.Any(r => r.IsIgd); } }
        public TimeSpan? FirstResponseAfter { get { return Responses.Count == 0 ? (TimeSpan?)null : Responses.Min(r => r.ReceivedAfter); } }

        /// <summary>
        /// Sorts the responses: first those of the default gateway, then IGD:2, IGD:1 and finally rootdevice; without repeating LOCATION.
        /// </summary>
        public List<SsdpResponse> Ranked(IEnumerable<IPAddress> gateways)
        {
            var gw = new HashSet<string>((gateways ?? new IPAddress[0]).Select(g => g.ToString()));
            return Responses
                .OrderBy(r => gw.Contains(r.Location.Host) ? 0 : 1)
                .ThenBy(r => r.IsIgd ? (r.IgdVersion == 2 ? 0 : 1) : 2)
                .ThenBy(r => r.ReceivedAfter)
                .GroupBy(r => r.Location.AbsoluteUri).Select(g => g.First()).ToList();
        }
    }

    /// <summary>SSDP discovery of the IGD: M-SEARCH on each interface (multicast) and unicast to the gateways; responses arrive unicast on the same socket.</summary>
    public sealed class SsdpClient
    {
        private readonly IUdpTransport _udp;
        private readonly INetworkInfo _net;

        public SsdpClient(IUdpTransport udp, INetworkInfo net)
        {
            _udp = udp ?? SystemUdpTransport.Default;
            _net = net ?? SystemNetworkInfo.Default;
        }

        public static byte[] BuildMSearch(string host, int port, string st, int mx, string userAgent)
        {
            var s = "M-SEARCH * HTTP/1.1\r\nHOST: " + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "\r\nMAN: \"ssdp:discover\"\r\nMX: " + mx.ToString(CultureInfo.InvariantCulture)
                + "\r\nST: " + st + "\r\nUSER-AGENT: " + userAgent + "\r\n\r\n";
            return Encoding.ASCII.GetBytes(s);
        }

        private sealed class Ctx
        {
            public LocalInterface Iface;
            public IUdpSocket Socket;
        }

        /// <summary>Looks for IGDs. Throws OperationCanceledException if <paramref name="ct"/> is cancelled; any other problem ends up in SsdpResult.Errors.</summary>
        public async Task<SsdpResult> DiscoverAsync(SsdpOptions o, CancellationToken ct)
        {
            if (o == null) o = new SsdpOptions();
            var res = new SsdpResult();
            var sw = Stopwatch.StartNew();
            var ifaces = new List<LocalInterface>();
            var all = _net.GetInterfaces();
            foreach (var i in all)
            {
                if (i == null || i.Address == null) continue;
                if (IpClass.IsLinkLocal(i.Address) && all.Count > 1) continue;     // 169.254.x.x usually has no router; only used if there is no other
                if (ifaces.Any(x => x.Address.Equals(i.Address))) continue;
                ifaces.Add(i);
            }
            if (ifaces.Count == 0) ifaces.Add(new LocalInterface { Name = "(any)", Address = IPAddress.Any });
            if (ifaces.Count > 6) ifaces = ifaces.Take(6).ToList();

            var ctxs = new List<Ctx>();
            foreach (var i in ifaces)
            {
                try { ctxs.Add(new Ctx { Iface = i, Socket = _udp.Open(i.Address, 0) }); res.SocketsOpened++; }
                catch (Exception ex) { AddError(res, "could not open UDP on " + i.Address + ": " + ex.Message); }
            }
            if (ctxs.Count == 0) { res.Elapsed = sw.Elapsed; return res; }

            var igdSeen = new TaskCompletionSource<bool>();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var token = cts.Token;
                var tasks = new List<Task>();
                foreach (var c in ctxs) { var cc = c; tasks.Add(Task.Run(() => ReceiveLoopAsync(cc, o, res, sw, igdSeen, token))); }
                tasks.Add(Task.Run(() => SendLoopAsync(ctxs, o, res, token)));
                try
                {
                    var windowEnd = Task.Delay(o.Window, token);
                    var first = await Task.WhenAny(igdSeen.Task, windowEnd).ConfigureAwait(false);
                    if (first == igdSeen.Task)
                    {
                        if (o.StopAfterFirstIgd) await Task.WhenAny(windowEnd, Task.Delay(o.GraceAfterFirstIgd, token)).ConfigureAwait(false);
                        else await windowEnd.ConfigureAwait(false);
                    }
                    else await windowEnd.ConfigureAwait(false);          // propagates the external cancellation
                }
                catch (OperationCanceledException) { /* external cancellation: cleaned up and rethrown below */ }
                cts.Cancel();
                foreach (var c in ctxs) { try { c.Socket.Dispose(); } catch (Exception) { } }
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
            }
            res.Elapsed = sw.Elapsed;
            ct.ThrowIfCancellationRequested();
            return res;
        }

        private async Task SendLoopAsync(List<Ctx> ctxs, SsdpOptions o, SsdpResult res, CancellationToken ct)
        {
            try
            {
                var group = new IPEndPoint(o.MulticastAddress, o.Port);
                for (int attempt = 0; attempt < Math.Max(1, o.Retries) && !ct.IsCancellationRequested; attempt++)
                {
                    foreach (var c in ctxs)
                    {
                        foreach (var st in o.SearchTargets)
                        {
                            if (o.UseMulticast)
                                await SendOneAsync(c, BuildMSearch(group.Address.ToString(), o.Port, st, o.Mx, o.UserAgent), group, res, ct).ConfigureAwait(false);
                            if (o.UseUnicastToGateway)
                            {
                                foreach (var gw in c.Iface.Gateways.Take(2))
                                    await SendOneAsync(c, BuildMSearch(gw.ToString(), o.Port, st, o.Mx, o.UserAgent), new IPEndPoint(gw, o.Port), res, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    if (attempt + 1 < o.Retries) await Task.Delay(o.RetryInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AddError(res, "SSDP send: " + ex.Message); }
        }

        private static async Task SendOneAsync(Ctx c, byte[] msg, IPEndPoint to, SsdpResult res, CancellationToken ct)
        {
            try
            {
                await c.Socket.SendToAsync(msg, to, ct).ConfigureAwait(false);
                lock (res) res.MessagesSent++;
            }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { throw new OperationCanceledException(); }
            catch (Exception ex) { AddError(res, "M-SEARCH from " + c.Iface.Address + " to " + to + ": " + ex.Message); }
        }

        private static async Task ReceiveLoopAsync(Ctx c, SsdpOptions o, SsdpResult res, Stopwatch sw, TaskCompletionSource<bool> igdSeen, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    UdpPacket p = await c.Socket.ReceiveAsync(ct).ConfigureAwait(false);
                    if (p == null) return;
                    SsdpResponse r;
                    if (p.Remote == null || !IpClass.IsIPv4(p.Remote.Address) || IpClass.IsMulticast(p.Remote.Address)) continue;
                    if (!SsdpResponse.TryParse(p.Data, p.Data.Length, out r)) continue;
                    if (!LocationAcceptable(r.Location, p.Remote.Address, o.AllowLoopbackLocation)) continue;
                    r.From = p.Remote;
                    r.LocalAddress = c.Iface.Address.Equals(IPAddress.Any) ? null : c.Iface.Address;
                    r.ReceivedAfter = sw.Elapsed;
                    lock (res)
                    {
                        if (res.Responses.Count >= o.MaxResponses) continue;
                        if (res.Responses.Any(x => x.Location.AbsoluteUri == r.Location.AbsoluteUri && x.St == r.St)) continue;
                        res.Responses.Add(r);
                    }
                    var h = o.OnResponse;
                    if (h != null) { try { h(r); } catch (Exception ex) { AddError(res, "SSDP response handler: " + ex.Message); } }
                    if (r.IsIgd) igdSeen.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AddError(res, "SSDP receive on " + c.Iface.Address + ": " + ex.Message); }
        }

        /// <summary>
        /// The LOCATION is only valid if it points to an address of the local network (private, link-local or CGNAT; never to the Internet nor, except in tests, to the device itself):
        /// this way a LAN machine cannot make us query an external server or a local service of the device.
        /// </summary>
        public static bool LocationAcceptable(Uri location, IPAddress sender, bool allowLoopback)
        {
            IPAddress host;
            if (location == null || !IPAddress.TryParse(location.Host, out host)) return false;
            if (IpClass.IsLoopback(host)) return allowLoopback;
            if (IpClass.IsMulticast(host) || IpClass.IsUnspecified(host)) return false;
            return IpClass.IsPrivate(host) || IpClass.IsLinkLocal(host) || IpClass.IsCgnat(host);
        }

        private static void AddError(SsdpResult res, string msg)
        {
            lock (res) { if (!res.Errors.Contains(msg) && res.Errors.Count < 20) res.Errors.Add(msg); }
        }
    }
}
