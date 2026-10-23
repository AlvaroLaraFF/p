using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Net
{
    public enum DiagResult { Ok, Fail, Inconclusive }

    /// <summary>A step of the diagnostic: appears in the log as "UPNP[name] OK|FAIL|INCONCLUSIVE: detail".</summary>
    public sealed class DiagStep
    {
        public string Name = "";
        public DiagResult Result;
        public string Detail = "";
        public string Label { get { return Result == DiagResult.Ok ? "OK" : Result == DiagResult.Fail ? "FAIL" : "INCONCLUSIVE"; } }
        public string Line { get { return "UPNP[" + Name + "] " + Label + ": " + Detail; } }
        public override string ToString() { return Line; }
    }

    public sealed class UpnpDiagnosticsReport
    {
        public readonly List<DiagStep> Steps = new List<DiagStep>();
        /// <summary>The multicast M-SEARCH got a response.</summary>
        public bool MulticastDiscovery;
        /// <summary>The unicast M-SEARCH to the gateway got a response.</summary>
        public bool UnicastDiscovery;
        public bool IgdFound;
        public string ExternalIp = "";
        public bool ExternalIpPublic;
        /// <summary>A test AddPortMapping was granted and deleted over UPnP.</summary>
        public bool UpnpMapping;
        public bool NatPmpAnswers, NatPmpMapping;
        /// <summary>The own listener accepted a connection made to the local IP.</summary>
        public bool IncomingLocal;
        /// <summary>The own listener accepted a connection made to external-IP:external-port (only if the router allows hairpin).</summary>
        public bool IncomingViaRouter;
        public bool Cancelled;
        public DiagResult Overall;
        public string Summary = "";

        public DiagStep Find(string name) { return Steps.FirstOrDefault(s => s.Name == name); }
        public override string ToString() { return string.Join("\n", Steps.Select(s => s.Line)); }
    }

    public sealed class DiagnosticsOptions
    {
        /// <summary>Seams. Null = the real ones.</summary>
        public IUdpTransport Udp;
        public INetworkInfo Network;
        public HttpMessageHandler Http;
        public SsdpOptions Ssdp = new SsdpOptions { Window = TimeSpan.FromSeconds(4) };
        public IgdOptions Igd = new IgdOptions();
        public NatPmpOptions NatPmp = new NatPmpOptions();
        /// <summary>
        /// Test TCP connection (to the local IP or to external-IP:port) -> true if established. Null = real TcpClient with the given timeout.
        /// Tests replace it so as not to go out to the network.
        /// </summary>
        public Func<IPEndPoint, TimeSpan, CancellationToken, Task<bool>> Connect;
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        public TimeSpan MulticastLoopbackWait = TimeSpan.FromSeconds(1);
        /// <summary>Lease of the test mapping, in seconds (deleted at the end; if the process dies, it expires on its own).</summary>
        public int TestLeaseSeconds = 120;
        public bool AllowLoopbackLocation;
    }

    /// <summary>
    /// Network diagnostic for the TV (run once and read the log): checks interfaces, multicast, SSDP, the IGD description, external IP (public or CGNAT),
    /// test mapping, NAT-PMP and incoming connections. It only talks to the local network; it sends nothing to third parties or to checking services on the Internet.
    /// Each step is written as "UPNP[step] OK|FAIL|INCONCLUSIVE: detail".
    /// </summary>
    public static class UpnpDiagnostics
    {
        /// <summary>Runs the real diagnostic. If cancelled, returns the partial report with Cancelled = true (does not throw).</summary>
        public static Task<UpnpDiagnosticsReport> RunAsync(Action<string> log, CancellationToken ct)
        {
            return RunAsync(log, ct, null);
        }

        public static async Task<UpnpDiagnosticsReport> RunAsync(Action<string> log, CancellationToken ct, DiagnosticsOptions options)
        {
            var run = new Runner(log, options ?? new DiagnosticsOptions());
            try { await run.RunAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                run.Report.Cancelled = true;
                run.Add("cancelled", DiagResult.Inconclusive, "diagnostic cancelled before finishing");
            }
            catch (Exception ex)
            {
                run.Add("error", DiagResult.Fail, "unexpected exception: " + ex.GetType().Name + ": " + ex.Message);
            }
            run.Finish();
            return run.Report;
        }

        private sealed class Incoming : IDisposable
        {
            private readonly TcpListener _l;
            private readonly List<IPEndPoint> _accepted = new List<IPEndPoint>();
            public int Port { get; private set; }

            public Incoming()
            {
                _l = new TcpListener(IPAddress.Any, 0);
                _l.Start(8);
                Port = ((IPEndPoint)_l.LocalEndpoint).Port;
                var ignored = Task.Run(LoopAsync);
            }

            private async Task LoopAsync()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync().ConfigureAwait(false); }
                    catch (Exception) { return; }
                    try { lock (_accepted) _accepted.Add(c.Client.RemoteEndPoint as IPEndPoint); } catch (Exception) { lock (_accepted) _accepted.Add(null); }
                    try { c.Close(); } catch (Exception) { }
                }
            }

            public int Count { get { lock (_accepted) return _accepted.Count; } }
            public IPEndPoint Last { get { lock (_accepted) return _accepted.Count == 0 ? null : _accepted[_accepted.Count - 1]; } }

            public async Task<bool> WaitForAsync(int countAtLeast, TimeSpan timeout, CancellationToken ct)
            {
                var until = DateTime.UtcNow + timeout;
                while (Count < countAtLeast)
                {
                    if (DateTime.UtcNow >= until) return false;
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
                return true;
            }

            public void Dispose() { try { _l.Stop(); } catch (Exception) { } }
        }

        private sealed class Runner
        {
            public readonly UpnpDiagnosticsReport Report = new UpnpDiagnosticsReport();
            private readonly Action<string> _log;
            private readonly DiagnosticsOptions _o;
            private readonly INetworkInfo _net;
            private readonly IUdpTransport _udp;
            private IgdClient _igd;
            private IgdService _svc;
            private IPAddress _routerIp, _local, _extIp;
            private List<IPAddress> _gateways = new List<IPAddress>();
            private List<SsdpResponse> _ranked = new List<SsdpResponse>();
            private bool _incomingDone;

            public Runner(Action<string> log, DiagnosticsOptions o)
            {
                _log = log;
                _o = o;
                _net = o.Network ?? SystemNetworkInfo.Default;
                _udp = o.Udp ?? SystemUdpTransport.Default;
            }

            public void Add(string name, DiagResult result, string detail)
            {
                var step = new DiagStep { Name = name, Result = result, Detail = Clean(detail) };
                Report.Steps.Add(step);
                Emit(step.Line);
            }

            private void Emit(string line)
            {
                AceLog.Info("upnp", line);
                if (_log != null) { try { _log(line); } catch (Exception) { } }
            }

            private static string Clean(string s)
            {
                if (s == null) return "";
                var sb = new StringBuilder(s.Length);
                foreach (char c in s) sb.Append(c < 0x20 ? ' ' : c);
                string r = sb.ToString();
                return r.Length > 600 ? r.Substring(0, 600) + "..." : r;
            }

            private static string Sec(TimeSpan t) { return t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"; }

            public async Task RunAsync(CancellationToken ct)
            {
                Add("start", DiagResult.Ok, "UPnP / NAT-PMP / incoming connections diagnostic (local network only; nothing is sent to the Internet)");
                _igd = new IgdClient(_o.Igd, _o.Http);
                Incoming incoming = null;
                try
                {
                    try { incoming = new Incoming(); }
                    catch (Exception ex) { Add("listen", DiagResult.Fail, "could not open a listening TCP port: " + ex.Message); }
                    CheckNetwork();
                    await CheckMulticastLocalAsync(ct).ConfigureAwait(false);
                    await DiscoverAsync(ct).ConfigureAwait(false);
                    await CheckIgdAsync(ct).ConfigureAwait(false);
                    await CheckExternalIpAsync(ct).ConfigureAwait(false);
                    await CheckUpnpMappingAsync(incoming, ct).ConfigureAwait(false);
                    await CheckNatPmpAsync(incoming, ct).ConfigureAwait(false);
                    if (!_incomingDone) await CheckIncomingAsync(incoming, 0, "", ct).ConfigureAwait(false);
                }
                finally
                {
                    if (incoming != null) incoming.Dispose();
                    _igd.Dispose();
                }
            }

            // ---------- (a) network ----------

            private void CheckNetwork()
            {
                var ifaces = _net.GetInterfaces();
                if (ifaces.Count == 0)
                    Add("network", DiagResult.Fail, "no active IPv4 interface (no network, or the system does not allow listing interfaces)");
                else
                    Add("network", DiagResult.Ok, ifaces.Count + " interface(s): " + string.Join(" | ", ifaces.Select(i => i.Name + " " + i.Address + (i.Mask != null ? "/" + IpClass.PrefixLength(i.Mask) : "") + (i.SupportsMulticast ? "" : " (no multicast)"))));
                _gateways = ifaces.SelectMany(i => i.Gateways).GroupBy(g => g.ToString()).Select(g => g.First()).ToList();
                if (_gateways.Count == 0) { Add("gateway", DiagResult.Fail, "no default gateway (NAT-PMP and the unicast search cannot be tested)"); return; }
                var parts = new List<string>();
                foreach (var gw in _gateways)
                {
                    var local = _net.LocalAddressFor(gw);
                    parts.Add(gw + " (local IP to reach it: " + (local == null ? "?" : local.ToString()) + ")");
                }
                Add("gateway", DiagResult.Ok, string.Join(" | ", parts));
            }

            // ---------- (b) multicast ----------

            private async Task CheckMulticastLocalAsync(CancellationToken ct)
            {
                var iface = _net.GetInterfaces().FirstOrDefault(i => i.SupportsMulticast && !IpClass.IsLinkLocal(i.Address));
                if (iface == null) { Add("multicast-local", DiagResult.Fail, "no interface supports multicast"); return; }
                var group = IPAddress.Parse("239.255.77.77");
                var payload = Encoding.ASCII.GetBytes("P!-multicast-test");
                try
                {
                    using (var rx = _udp.Open(IPAddress.Any, 0))
                    using (var tx = _udp.Open(iface.Address, 0))
                    {
                        rx.JoinMulticastGroup(group, iface.Address);
                        int port = rx.LocalEndPoint.Port;
                        await tx.SendToAsync(payload, new IPEndPoint(group, port), ct).ConfigureAwait(false);
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(_o.MulticastLoopbackWait);
                            try
                            {
                                while (true)
                                {
                                    var p = await rx.ReceiveAsync(cts.Token).ConfigureAwait(false);
                                    if (p == null) break;
                                    if (p.Data.SequenceEqual(payload)) { Add("multicast-local", DiagResult.Ok, "the system joins multicast groups and delivers datagrams on " + iface.Name + " " + iface.Address); return; }
                                }
                            }
                            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; }
                        }
                        Add("multicast-local", DiagResult.Inconclusive, "could join the group and send, but the datagram did not come back in " + Sec(_o.MulticastLoopbackWait) + " (some drivers do not loop back their own multicast)");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Add("multicast-local", DiagResult.Fail, "the system does not allow multicast on " + iface.Name + ": " + ex.Message); }
            }

            private async Task DiscoverAsync(CancellationToken ct)
            {
                var ssdp = new SsdpClient(_udp, _net);
                var om = _o.Ssdp.Clone(); om.UseMulticast = true; om.UseUnicastToGateway = false; om.AllowLoopbackLocation = _o.AllowLoopbackLocation;
                var ou = _o.Ssdp.Clone(); ou.UseMulticast = false; ou.UseUnicastToGateway = true; ou.AllowLoopbackLocation = _o.AllowLoopbackLocation;
                var tm = ssdp.DiscoverAsync(om, ct);
                Task<SsdpResult> tu = _gateways.Count > 0 ? ssdp.DiscoverAsync(ou, ct) : null;
                var rm = await tm.ConfigureAwait(false);
                SsdpResult ru = tu == null ? null : await tu.ConfigureAwait(false);

                Report.MulticastDiscovery = rm.Responses.Count > 0;
                DescribeSsdp("ssdp-multicast", rm, "239.255.255.250:1900");
                if (ru == null) Add("ssdp-unicast", DiagResult.Inconclusive, "no known gateway to ask");
                else { Report.UnicastDiscovery = ru.Responses.Count > 0; DescribeSsdp("ssdp-unicast", ru, string.Join(",", _gateways) + ":1900"); }
                if (!Report.MulticastDiscovery && Report.UnicastDiscovery)
                    Add("ssdp-summary", DiagResult.Inconclusive, "multicast got no response but unicast to the router did: multicast is filtered (the engine uses both)");

                var all = new SsdpResult();
                all.Responses.AddRange(rm.Responses);
                if (ru != null) all.Responses.AddRange(ru.Responses);
                _ranked = all.Ranked(_gateways);
            }

            private void DescribeSsdp(string name, SsdpResult r, string target)
            {
                if (r.Responses.Count > 0)
                {
                    var first = r.Responses.OrderBy(x => x.ReceivedAfter).First();
                    Add(name, DiagResult.Ok, r.Responses.Count + " response(s) from " + string.Join(",", r.Responses.Select(x => x.From.Address.ToString()).Distinct())
                        + "; the first after " + (int)first.ReceivedAfter.TotalMilliseconds + " ms: ST=" + first.St + " SERVER=" + first.Server + " LOCATION=" + first.Location);
                }
                else if (r.MessagesSent == 0)
                    Add(name, DiagResult.Fail, "could not send any M-SEARCH to " + target + (r.Errors.Count > 0 ? ": " + string.Join("; ", r.Errors.Take(3)) : " (" + r.SocketsOpened + " socket(s) open)"));
                else
                    Add(name, DiagResult.Inconclusive, r.MessagesSent + " M-SEARCH sent to " + target + " from " + r.SocketsOpened + " interface(s), no response in " + Sec(r.Elapsed)
                        + " (multicast/unicast filtered, or the router has UPnP disabled)" + (r.Errors.Count > 0 ? ". Errors: " + string.Join("; ", r.Errors.Take(2)) : ""));
            }

            // ---------- (c) IGD and external IP ----------

            private async Task CheckIgdAsync(CancellationToken ct)
            {
                if (_ranked.Count == 0) { Add("igd", DiagResult.Inconclusive, "no device to query (SSDP got no responses)"); return; }
                var failures = new List<string>();
                foreach (var resp in _ranked.Take(4))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var d = await _igd.FetchDescriptionAsync(resp.Location, ct).ConfigureAwait(false);
                        if (d.Services.Count == 0) { failures.Add(resp.Location.Host + ": " + (d.DeviceType.Length > 0 ? d.DeviceType : "device") + " without WANIPConnection/WANPPPConnection"); continue; }
                        _routerIp = IPAddress.Parse(resp.Location.Host);
                        _local = _net.ResolveLocalAddress(_routerIp, resp.LocalAddress);
                        _svc = d.Services[0];
                        Report.IgdFound = true;
                        Add("igd", DiagResult.Ok, "'" + d.FriendlyName + "' " + d.Manufacturer + " " + d.ModelName + " " + d.ModelNumber + " at " + resp.Location + "; services: "
                            + string.Join(", ", d.Services.Select(s => s.ServiceType.Substring(s.ServiceType.LastIndexOf(':', s.ServiceType.Length - 3) + 1) + " " + s.ControlUrl.PathAndQuery))
                            + "; local IP to reach the router: " + (_local == null ? "?" : _local.ToString()));
                        // Among the services, the first whose external IP answers and is not 0.0.0.0
                        foreach (var s in d.Services)
                        {
                            try { var ip = await _igd.GetExternalIPAddressAsync(s, ct).ConfigureAwait(false); if (!IpClass.IsUnspecified(ip)) { _svc = s; break; } }
                            catch (UpnpException) { }
                        }
                        return;
                    }
                    catch (UpnpException ex) { failures.Add(resp.Location + ": " + ex.Message); }
                }
                Add("igd", DiagResult.Fail, "no device that answered SSDP offers a usable IGD: " + string.Join(" | ", failures));
            }

            private async Task CheckExternalIpAsync(CancellationToken ct)
            {
                if (_svc == null) { Add("external-ip", DiagResult.Inconclusive, "no IGD to query"); return; }
                try
                {
                    var ip = await _igd.GetExternalIPAddressAsync(_svc, ct).ConfigureAwait(false);
                    _extIp = ip;
                    Report.ExternalIp = ip.ToString();
                    Report.ExternalIpPublic = IpClass.IsPublic(ip);
                    if (IpClass.IsUnspecified(ip)) Add("external-ip", DiagResult.Fail, "the router returns 0.0.0.0: the WAN is not connected");
                    else if (Report.ExternalIpPublic) Add("external-ip", DiagResult.Ok, ip + " is public: the router has its own Internet IP");
                    else Add("external-ip", DiagResult.Fail, ip + " is " + IpClass.Describe(ip) + ": the router is behind another NAT (CGNAT/double NAT), an open port would not be reachable from the Internet");
                }
                catch (UpnpException ex) { Add("external-ip", DiagResult.Fail, "GetExternalIPAddress: " + ex.Message); }
            }

            // ---------- (d) UPnP mapping ----------

            private async Task CheckUpnpMappingAsync(Incoming incoming, CancellationToken ct)
            {
                if (_svc == null) { Add("mapping", DiagResult.Inconclusive, "no IGD: AddPortMapping is not tested"); return; }
                if (_local == null) { Add("mapping", DiagResult.Fail, "could not determine the local IP used to reach the router"); return; }
                int internalPort = incoming != null ? incoming.Port : 40000 + new Random().Next(20000);
                var notes = new List<string>();
                IgdMapResult m;
                try
                {
                    m = await IgdMapper.MapAsync(_igd, _svc, _local, internalPort, internalPort, "P! test", _o.TestLeaseSeconds, true, 4, new Random(), notes.Add, ct).ConfigureAwait(false);
                }
                catch (UpnpException ex)
                {
                    Add("mapping", DiagResult.Fail, "test AddPortMapping (TCP " + internalPort + "): " + ex.Message + (notes.Count > 0 ? " [" + string.Join("; ", notes) + "]" : ""));
                    return;
                }
                string detail = "TCP " + m.ExternalPort + " -> " + _local + ":" + internalPort + (m.UdpMapped ? " (and UDP)" : " (no UDP)") + ", lease " + (m.LeaseSeconds == 0 ? "0 (permanent: the router does not support lease)" : m.LeaseSeconds + " s");
                if (notes.Count > 0) detail += " [" + string.Join("; ", notes) + "]";
                Add("mapping", m.Unverified ? DiagResult.Inconclusive : DiagResult.Ok, detail + (m.Unverified ? "; the router said OK but does not show the mapping when queried (GetSpecificPortMappingEntry)" : "; confirmed with GetSpecificPortMappingEntry"));

                // (f) incoming, with the mapping in place
                if (!m.Unverified) Report.UpnpMapping = true;
                await CheckIncomingAsync(incoming, m.ExternalPort, "UPnP", ct).ConfigureAwait(false);

                // deletion
                try
                {
                    await IgdMapper.RemoveAsync(_igd, _svc, m.ExternalPort, m.UdpMapped, notes.Add, ct).ConfigureAwait(false);
                    var left = await _igd.GetSpecificPortMappingEntryAsync(_svc, "TCP", m.ExternalPort, ct).ConfigureAwait(false);
                    if (left == null) Add("mapping-deleted", DiagResult.Ok, "DeletePortMapping TCP " + m.ExternalPort + ": the router no longer shows it");
                    else Add("mapping-deleted", DiagResult.Fail, "the router still shows the TCP mapping " + m.ExternalPort + " after DeletePortMapping (it will expire by lease in " + _o.TestLeaseSeconds + " s)");
                }
                catch (UpnpException ex) { Add("mapping-deleted", DiagResult.Fail, "DeletePortMapping: " + ex.Message + " (the mapping will expire by lease)"); }
            }

            // ---------- (e) NAT-PMP ----------

            private async Task CheckNatPmpAsync(Incoming incoming, CancellationToken ct)
            {
                if (_gateways.Count == 0) { Add("natpmp", DiagResult.Inconclusive, "no gateway: NAT-PMP is not tested"); return; }
                var pmp = new NatPmpClient(_udp, _o.NatPmp);
                foreach (var gw in _gateways.Take(3))
                {
                    ct.ThrowIfCancellationRequested();
                    var local = _net.ResolveLocalAddress(gw, null);
                    if (local == null) { Add("natpmp", DiagResult.Fail, "no local IP towards " + gw); continue; }
                    NatPmpExternalAddress ext;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { ext = await pmp.GetExternalAddressAsync(gw, local, ct).ConfigureAwait(false); }
                    catch (NatPmpException ex)
                    {
                        Add("natpmp", DiagResult.Fail, gw + ":" + _o.NatPmp.Port + " " + (ex.NoResponse ? "does not answer NAT-PMP in " + Sec(sw.Elapsed) + " (normal if the router does not offer it)" : ex.Message));
                        continue;
                    }
                    Report.NatPmpAnswers = true;
                    Add("natpmp", DiagResult.Ok, gw + " answers: external IP " + ext.Address + " (" + IpClass.Describe(ext.Address) + "), epoch " + ext.Epoch + ", " + (int)sw.Elapsed.TotalMilliseconds + " ms");
                    int internalPort = incoming != null ? incoming.Port : 40000;
                    try
                    {
                        var map = await pmp.MapAsync(gw, local, true, internalPort, internalPort, (uint)_o.TestLeaseSeconds, ct).ConfigureAwait(false);
                        Report.NatPmpMapping = true;
                        Add("natpmp-mapping", DiagResult.Ok, "TCP " + map.ExternalPort + " -> " + local + ":" + internalPort + ", lifetime " + map.LifetimeSeconds + " s");
                        if (!_incomingDone) await CheckIncomingAsync(incoming, map.ExternalPort, "NAT-PMP", ct, ext.Address).ConfigureAwait(false);
                        try
                        {
                            await pmp.DeleteAsync(gw, local, true, internalPort, ct).ConfigureAwait(false);
                            Add("natpmp-deleted", DiagResult.Ok, "TCP mapping " + map.ExternalPort + " deleted (lifetime 0)");
                        }
                        catch (NatPmpException ex) { Add("natpmp-deleted", DiagResult.Fail, ex.Message + " (it will expire by lifetime)"); }
                    }
                    catch (NatPmpException ex) { Add("natpmp-mapping", DiagResult.Fail, gw + ": " + ex.Message); }
                    return;
                }
            }

            // ---------- (f) incoming connections ----------

            private async Task<bool> ConnectAsync(IPEndPoint ep, CancellationToken ct)
            {
                if (_o.Connect != null) return await _o.Connect(ep, _o.ConnectTimeout, ct).ConfigureAwait(false);
                using (var c = new TcpClient(AddressFamily.InterNetwork))
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(_o.ConnectTimeout);
                    var t = c.ConnectAsync(ep.Address, ep.Port).Observed();
                    var ignored = t.ContinueWith(x => { var e = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    var cancelled = new TaskCompletionSource<bool>();
                    using (cts.Token.Register(() => cancelled.TrySetResult(true)))
                    {
                        var done = await Task.WhenAny(t, cancelled.Task).ConfigureAwait(false);
                        if (done != t) { ct.ThrowIfCancellationRequested(); return false; }
                    }
                    return !t.IsFaulted && !t.IsCanceled;
                }
            }

            private async Task CheckIncomingAsync(Incoming incoming, int externalPort, string method, CancellationToken ct, IPAddress natPmpExternalIp = null)
            {
                _incomingDone = true;
                if (incoming == null) { Add("incoming", DiagResult.Fail, "there is no test listening port"); return; }
                IPAddress local = _local ?? _net.GetInterfaces().Select(i => i.Address).FirstOrDefault();
                // 1) The own listener accepts connections on the local IP
                if (local == null) Add("incoming-local", DiagResult.Inconclusive, "no local IP to test");
                else
                {
                    int before = incoming.Count;
                    bool connected = await ConnectAsync(new IPEndPoint(local, incoming.Port), ct).ConfigureAwait(false);
                    bool accepted = connected && await incoming.WaitForAsync(before + 1, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
                    Report.IncomingLocal = accepted;
                    if (accepted) Add("incoming-local", DiagResult.Ok, "the TCP listener " + incoming.Port + " accepts connections on " + local + " (the system does not block them locally)");
                    else Add("incoming-local", DiagResult.Fail, "could not connect to " + local + ":" + incoming.Port + " from the device itself (firewall or listener failure)");
                }
                // 2) Through the router (hairpin)
                IPAddress ext = natPmpExternalIp ?? _extIp;
                if (externalPort <= 0)
                {
                    Add("incoming-router", DiagResult.Inconclusive, "there is no mapping on the router: only the local connection was tested");
                    return;
                }
                if (ext == null || IpClass.IsUnspecified(ext)) { Add("incoming-router", DiagResult.Inconclusive, "the external IP is unknown: cannot test " + method + " from outside"); return; }
                if (!IpClass.IsPublic(ext))
                {
                    Add("incoming-router", DiagResult.Fail, "the external IP " + ext + " is " + IpClass.Describe(ext) + ": no inbound access from the Internet even though the mapping exists");
                    return;
                }
                int count = incoming.Count;
                bool ok = await ConnectAsync(new IPEndPoint(ext, externalPort), ct).ConfigureAwait(false);
                bool arrived = ok && await incoming.WaitForAsync(count + 1, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
                Report.IncomingViaRouter = arrived;
                if (arrived)
                    Add("incoming-router", DiagResult.Ok, "the connection to " + ext + ":" + externalPort + " looped back through the router and reached the listener (source seen " + incoming.Last + "): the " + method + " mapping works");
                else
                    Add("incoming-router", DiagResult.Inconclusive, "the connection to " + ext + ":" + externalPort + " from inside did not reach the listener. Many routers do not support hairpin NAT, so this does not prove the port is closed. "
                        + "Definitive check: from another network (e.g. mobile data) connect over TCP to " + ext + ":" + externalPort + " while the app is open");
            }

            public void Finish()
            {
                var r = Report;
                var parts = new List<string>();
                parts.Add("multicast " + (r.MulticastDiscovery ? "yes" : "no") + ", unicast " + (r.UnicastDiscovery ? "yes" : "no"));
                parts.Add("IGD " + (r.IgdFound ? "yes" : "no"));
                parts.Add("external IP " + (r.ExternalIp.Length == 0 ? "?" : r.ExternalIp + (r.ExternalIpPublic ? " (public)" : " (NOT public)")));
                parts.Add("UPnP mapping " + (r.UpnpMapping ? "yes" : "no") + ", NAT-PMP " + (r.NatPmpMapping ? "yes" : "no"));
                parts.Add("incoming local " + (r.IncomingLocal ? "yes" : "no") + ", via router " + (r.IncomingViaRouter ? "yes" : "inconclusive"));
                bool mapped = r.UpnpMapping || r.NatPmpMapping;
                DiagResult overall;
                if (r.Cancelled) overall = DiagResult.Inconclusive;
                else if (mapped && r.ExternalIp.Length > 0 && !r.ExternalIpPublic) overall = DiagResult.Fail;      // CGNAT: the mapping exists but is of no use from the Internet
                else if (mapped && r.IncomingLocal) overall = DiagResult.Ok;
                else if (!mapped && !r.IgdFound && !r.NatPmpAnswers) overall = DiagResult.Fail;
                else overall = DiagResult.Inconclusive;
                r.Overall = overall;
                r.Summary = string.Join("; ", parts);
                Add("summary", overall, r.Summary);
            }
        }
    }
}
