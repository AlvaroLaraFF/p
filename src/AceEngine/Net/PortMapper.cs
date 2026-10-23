using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Util;

namespace AceEngine.Net
{
    /// <summary>
    /// Opens a router port for the P2P listener (UPnP IGD, with NAT-PMP as a fallback). Everything is asynchronous: Start/Stop do not block.
    /// Changed fires on a thread-pool thread (do not take UI locks inside the handler).
    /// </summary>
    public interface IPortMapper : IDisposable
    {
        /// <summary>Disabled until Start has been called (or after Stop); then Searching, Open or Unavailable.</summary>
        UpnpState State { get; }
        /// <summary>External port opened when State == Open; 0 otherwise.</summary>
        int OpenPort { get; }
        /// <summary>External IP of the router if known ("" if not).</summary>
        string ExternalIp { get; }
        /// <summary>"UPnP" or "NAT-PMP" when State == Open; "" otherwise.</summary>
        string Method { get; }
        event Action Changed;
        /// <summary>Starts opening <paramref name="internalPort"/> (TCP and UDP). Idempotent: with the same parameters it does nothing; with different ones, it replaces the mapping.</summary>
        void Start(int internalPort, int preferredExternalPort = 0);
        /// <summary>Stops maintaining the mapping and deletes it from the router in the background (short timeout). Does not block.</summary>
        void Stop();
    }

    public sealed class PortMapperOptions
    {
        /// <summary>Seams. Null = the real ones.</summary>
        public IUdpTransport Udp;
        public INetworkInfo Network;
        public HttpMessageHandler HttpHandler;

        public SsdpOptions Ssdp = new SsdpOptions();
        public IgdOptions Igd = new IgdOptions();
        public NatPmpOptions NatPmp = new NatPmpOptions();

        /// <summary>Description of the mapping on the router.</summary>
        public string Description = "P!";
        /// <summary>Requested lease (renewed at half). TimeSpan.Zero = request a permanent mapping.</summary>
        public TimeSpan Lease = TimeSpan.FromHours(1);
        public double RenewFraction = 0.5;
        /// <summary>How often to check that the mapping is still there (besides renewing it).</summary>
        public TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        /// <summary>Wait between attempts when there is no usable router; doubles up to RetryMax. A network change retries sooner.</summary>
        public TimeSpan RetryMin = TimeSpan.FromSeconds(30);
        public TimeSpan RetryMax = TimeSpan.FromMinutes(10);
        /// <summary>Margin to group bursts of network-change events.</summary>
        public TimeSpan NetworkChangeDebounce = TimeSpan.FromSeconds(2);
        /// <summary>Wait between the two attempts of a failed check/renewal before giving the mapping up for lost.</summary>
        public TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(3);
        /// <summary>Timeout to delete the mapping on stop.</summary>
        public TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
        /// <summary>How long Dispose waits for the deletion to finish (on the calling thread; Stop does not wait).</summary>
        public TimeSpan DisposeWait = TimeSpan.FromSeconds(1.5);

        public bool UseUpnp = true;
        public bool UseNatPmp = true;
        public bool MapUdp = true;
        /// <summary>If the router's external IP is not public (CGNAT, private, 0.0.0.0) the port will not be reachable from the Internet: Unavailable is declared instead of Open.</summary>
        public bool RequirePublicExternalIp = true;
        public int MaxPortAttempts = 6;
        /// <summary>Tests only: accepts a LOCATION to loopback.</summary>
        public bool AllowLoopbackLocation = false;
        public int MaxLocationsTried = 4;
    }

    /// <summary>Orchestrator: SSDP + IGD, NAT-PMP if it fails, renewal, detection of loss and of network change, deletion on stop.</summary>
    public sealed class PortMapper : IPortMapper
    {
        private const string Tag = "upnp";
        private readonly PortMapperOptions _o;
        private readonly INetworkInfo _net;
        private readonly SsdpClient _ssdp;
        private readonly IgdClient _igd;
        private readonly NatPmpClient _pmp;
        private readonly Random _rnd = new Random();
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private Run _run;
        private Task _lastTask = Task.CompletedTask;
        private UpnpState _state = UpnpState.Disabled;
        private int _openPort;
        private string _externalIp = "", _method = "", _detail = "";
        private bool _disposed, _subscribed;

        public PortMapper(PortMapperOptions options = null)
        {
            _o = options ?? new PortMapperOptions();
            _net = _o.Network ?? SystemNetworkInfo.Default;
            var udp = _o.Udp ?? SystemUdpTransport.Default;
            _ssdp = new SsdpClient(udp, _net);
            _igd = new IgdClient(_o.Igd, _o.HttpHandler);
            _pmp = new NatPmpClient(udp, _o.NatPmp);
        }

        public event Action Changed;

        public UpnpState State { get { lock (_gate) return _state; } }
        public int OpenPort { get { lock (_gate) return _openPort; } }
        public string ExternalIp { get { lock (_gate) return _externalIp; } }
        public string Method { get { lock (_gate) return _method; } }
        /// <summary>Short explanation of the current state ("" if none): why it is unavailable, what is being done...</summary>
        public string Detail { get { lock (_gate) return _detail; } }

        // ---------- Lifecycle ----------

        private sealed class Run
        {
            public CancellationTokenSource Cts = new CancellationTokenSource();
            public int InternalPort, Preferred;
            public Task Task = Task.CompletedTask;
            public volatile ActiveMapping Active;
        }

        private sealed class ActiveMapping
        {
            public string Method = "";
            public IPAddress InternalIp;
            public int InternalPort, ExternalPort;
            public string ExternalIp = "";
            public int LeaseSeconds;
            public bool UdpMapped;
            /// <summary>The router does not show the mapping in GetSpecificPortMappingEntry although it accepted it: that query is not used to watch it.</summary>
            public bool Unverified;
            public DateTime LastRenewUtc = DateTime.UtcNow;
            public IPAddress Router;
            public IgdService Service;
            public IPAddress Gateway;
            public uint Epoch;
            public DateTime EpochAtUtc = DateTime.UtcNow;
        }

        private sealed class Established
        {
            public ActiveMapping Mapping;
            public string Detail = "";
        }

        public void Start(int internalPort, int preferredExternalPort = 0)
        {
            if (internalPort < 1 || internalPort > 65535) throw new ArgumentOutOfRangeException("internalPort");
            if (preferredExternalPort < 0 || preferredExternalPort > 65535) throw new ArgumentOutOfRangeException("preferredExternalPort");
            bool changed;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("PortMapper");
                if (_run != null && _run.InternalPort == internalPort && _run.Preferred == preferredExternalPort) return;
                var prev = _run;
                if (prev != null) prev.Cts.Cancel();
                var run = new Run { InternalPort = internalPort, Preferred = preferredExternalPort };
                _run = run;
                var previousTask = _lastTask;
                changed = SetStateLocked(UpnpState.Searching, 0, "", "", "searching for router");
                if (!_subscribed) { _subscribed = true; try { _net.AddressesChanged += OnNetworkChanged; } catch (Exception) { _subscribed = false; } }
                run.Task = Task.Run(() => RunAsync(run, previousTask));
                _lastTask = run.Task;
            }
            if (changed) RaiseChanged();
        }

        public void Stop()
        {
            bool changed;
            Run old;
            lock (_gate)
            {
                old = _run;
                _run = null;
                changed = SetStateLocked(UpnpState.Disabled, 0, "", "", "");
                if (_subscribed) { _subscribed = false; try { _net.AddressesChanged -= OnNetworkChanged; } catch (Exception) { } }
            }
            // The mapping is deleted by the cycle itself when it sees the cancellation; Stop does not wait.
            if (old != null) old.Cts.Cancel();
            if (changed) RaiseChanged();
        }

        public void Dispose()
        {
            Task pending;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
            lock (_gate) pending = _lastTask;
            try { pending.Wait(_o.DisposeWait); } catch (Exception) { }
            try { _igd.Dispose(); } catch (Exception) { }
        }

        private void OnNetworkChanged()
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        }

        private bool SetStateLocked(UpnpState state, int port, string ip, string method, string detail)
        {
            bool changed = _state != state || _openPort != port || _externalIp != ip || _method != method;
            _state = state; _openPort = port; _externalIp = ip; _method = method; _detail = detail ?? "";
            return changed;
        }

        private void Update(Run run, UpnpState state, int port, string ip, string method, string detail)
        {
            bool changed;
            lock (_gate)
            {
                if (_run != run) return;
                changed = SetStateLocked(state, port, ip ?? "", method ?? "", detail);
            }
            if (changed)
            {
                AceLog.Info(Tag, "state " + state + (port > 0 ? " port " + port : "") + (string.IsNullOrEmpty(method) ? "" : " (" + method + ")") + (string.IsNullOrEmpty(detail) ? "" : ": " + detail));
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            foreach (Action a in h.GetInvocationList())
            {
                try { a(); } catch (Exception ex) { AceLog.Warn(Tag, "Changed handler: " + ex.Message); }
            }
        }

        private static void Log(string msg) { AceLog.Info(Tag, msg); }

        // ---------- Main loop ----------

        private async Task RunAsync(Run run, Task previous)
        {
            var ct = run.Cts.Token;
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            try
            {
                var backoff = _o.RetryMin;
                while (!ct.IsCancellationRequested)
                {
                    Update(run, UpnpState.Searching, 0, "", "", "searching for router");
                    Established r;
                    try { r = await EstablishAsync(run, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { r = new Established { Detail = "unexpected error: " + ex.Message }; }

                    if (r.Mapping != null)
                    {
                        var m = r.Mapping;
                        run.Active = m;
                        backoff = _o.RetryMin;
                        Update(run, UpnpState.Open, m.ExternalPort, m.ExternalIp, m.Method, "port " + m.ExternalPort + " opened by " + m.Method + (m.Unverified ? " (unverified)" : ""));
                        var openedAt = DateTime.UtcNow;
                        string lost = await MaintainAsync(run, m, ct).ConfigureAwait(false);
                        run.Active = null;
                        Update(run, UpnpState.Searching, 0, "", "", lost);
                        await RemoveQuietlyAsync(m).ConfigureAwait(false);       // above all if the local IP changed: the old mapping would block the same external port
                        if (DateTime.UtcNow - openedAt < TimeSpan.FromSeconds(5)) await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);       // brake against fast loops
                        continue;
                    }
                    Update(run, UpnpState.Unavailable, 0, "", "", r.Detail);
                    await WaitWakeAsync(backoff, ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(_o.RetryMax.Ticks, backoff.Ticks * 2));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Warn(Tag, "mapping cycle ended with an error: " + ex.Message); }
            finally
            {
                var m = run.Active;
                run.Active = null;
                if (m != null) await RemoveQuietlyAsync(m).ConfigureAwait(false);
            }
        }

        private async Task<bool> WaitWakeAsync(TimeSpan t, CancellationToken ct)
        {
            if (t > TimeSpan.FromDays(1)) t = TimeSpan.FromDays(1);
            bool woken = await _wake.WaitAsync(t, ct).ConfigureAwait(false);
            if (woken && _o.NetworkChangeDebounce > TimeSpan.Zero) await Task.Delay(_o.NetworkChangeDebounce, ct).ConfigureAwait(false);
            return woken;
        }

        // ---------- Establishing the mapping ----------

        private async Task<Established> EstablishAsync(Run run, CancellationToken ct)
        {
            var notes = new List<string>();
            if (_o.UseUpnp)
            {
                var m = await EstablishUpnpAsync(run, notes, ct).ConfigureAwait(false);
                if (m != null) return new Established { Mapping = m };
            }
            if (_o.UseNatPmp)
            {
                var m = await EstablishNatPmpAsync(run, notes, ct).ConfigureAwait(false);
                if (m != null) return new Established { Mapping = m };
            }
            if (notes.Count == 0) notes.Add("UPnP and NAT-PMP disabled");
            return new Established { Detail = string.Join("; ", notes) };
        }

        private int LeaseSeconds { get { return _o.Lease <= TimeSpan.Zero ? 0 : (int)Math.Max(1, Math.Min(int.MaxValue, _o.Lease.TotalSeconds)); } }

        private List<IPAddress> Gateways()
        {
            return _net.GetInterfaces().SelectMany(i => i.Gateways).Where(g => g != null).GroupBy(g => g.ToString()).Select(g => g.First()).ToList();
        }

        private IPAddress ResolveLocalAddress(IPAddress router, IPAddress ssdpInterface) { return _net.ResolveLocalAddress(router, ssdpInterface); }

        private async Task<ActiveMapping> EstablishUpnpAsync(Run run, List<string> notes, CancellationToken ct)
        {
            var so = _o.Ssdp;
            so.AllowLoopbackLocation = _o.AllowLoopbackLocation;
            SsdpResult found = await _ssdp.DiscoverAsync(so, ct).ConfigureAwait(false);
            if (found.Responses.Count == 0)
            {
                notes.Add("UPnP: no router answered the M-SEARCH" + (found.Errors.Count > 0 ? " (" + found.Errors[0] + ")" : ""));
                return null;
            }
            var ranked = found.Ranked(Gateways()).Take(Math.Max(1, _o.MaxLocationsTried)).ToList();
            foreach (var resp in ranked)
            {
                ct.ThrowIfCancellationRequested();
                IgdDescription desc;
                try { desc = await _igd.FetchDescriptionAsync(resp.Location, ct).ConfigureAwait(false); }
                catch (UpnpException ex) { notes.Add("UPnP " + resp.Location.Host + ": " + ex.Message); continue; }
                if (desc.Services.Count == 0) { notes.Add("UPnP " + resp.Location.Host + ": no WANIPConnection/WANPPPConnection service"); continue; }
                IPAddress routerIp = IPAddress.Parse(resp.Location.Host);
                IPAddress local = ResolveLocalAddress(routerIp, resp.LocalAddress);
                if (local == null) { notes.Add("UPnP: could not determine the local IP to reach " + routerIp); continue; }
                foreach (var svc in desc.Services.Take(3))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        IPAddress ext = null;
                        try { ext = await _igd.GetExternalIPAddressAsync(svc, ct).ConfigureAwait(false); }
                        catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap || ex.Kind == UpnpErrorKind.Protocol) { Log("GetExternalIPAddress: " + ex.Message); }
                        if (ext != null && IpClass.IsUnspecified(ext)) { notes.Add("UPnP " + routerIp + ": the router has no external IP (WAN not connected)"); continue; }
                        if (ext != null && _o.RequirePublicExternalIp && !IpClass.IsPublic(ext))
                        {
                            notes.Add("UPnP " + routerIp + ": the external IP " + ext + " is " + IpClass.Describe(ext) + ": the port would not be reachable from the Internet");
                            continue;
                        }
                        var mr = await IgdMapper.MapAsync(_igd, svc, local, run.InternalPort, run.Preferred > 0 ? run.Preferred : run.InternalPort, _o.Description, LeaseSeconds, _o.MapUdp, _o.MaxPortAttempts, _rnd, Log, ct).ConfigureAwait(false);
                        Log("UPnP: port " + mr.ExternalPort + " opened on " + routerIp + " (" + svc.ServiceType + ", lease " + mr.LeaseSeconds + " s, UDP " + (mr.UdpMapped ? "yes" : "no") + ", internal IP " + local + ")");
                        return new ActiveMapping
                        {
                            Method = "UPnP", InternalIp = local, InternalPort = run.InternalPort, ExternalPort = mr.ExternalPort, ExternalIp = ext == null ? "" : ext.ToString(),
                            LeaseSeconds = mr.LeaseSeconds, UdpMapped = mr.UdpMapped, Unverified = mr.Unverified, Router = routerIp, Service = svc, LastRenewUtc = DateTime.UtcNow
                        };
                    }
                    catch (UpnpException ex)
                    {
                        notes.Add("UPnP " + routerIp + " " + ShortService(svc) + ": " + ex.Message);
                        // No response or broken connection: the other services of the same router will not answer either; no more time is wasted on it.
                        if (ex.Kind == UpnpErrorKind.Timeout || ex.Kind == UpnpErrorKind.Transport) break;
                    }
                }
            }
            return null;
        }

        private static string ShortService(IgdService svc)
        {
            int i = svc.ServiceType.LastIndexOf(':', svc.ServiceType.Length - 1);
            int j = i > 0 ? svc.ServiceType.LastIndexOf(':', i - 1) : -1;
            return j >= 0 ? svc.ServiceType.Substring(j + 1) : svc.ServiceType;
        }

        private async Task<ActiveMapping> EstablishNatPmpAsync(Run run, List<string> notes, CancellationToken ct)
        {
            var gateways = Gateways();
            if (gateways.Count == 0) { notes.Add("NAT-PMP: no known gateway"); return null; }
            foreach (var gw in gateways.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                IPAddress local = ResolveLocalAddress(gw, null);
                if (local == null) { notes.Add("NAT-PMP: no local IP towards " + gw); continue; }
                try
                {
                    var ext = await _pmp.GetExternalAddressAsync(gw, local, ct).ConfigureAwait(false);
                    if (IpClass.IsUnspecified(ext.Address)) { notes.Add("NAT-PMP " + gw + ": the router has no external IP"); continue; }
                    if (_o.RequirePublicExternalIp && !IpClass.IsPublic(ext.Address))
                    {
                        notes.Add("NAT-PMP " + gw + ": the external IP " + ext.Address + " is " + IpClass.Describe(ext.Address) + ": the port would not be reachable from the Internet");
                        continue;
                    }
                    uint life = (uint)(LeaseSeconds == 0 ? 3600 : LeaseSeconds);      // NAT-PMP has no permanent mappings: lifetime 0 means delete
                    var map = await _pmp.MapAsync(gw, local, true, run.InternalPort, run.Preferred > 0 ? run.Preferred : run.InternalPort, life, ct).ConfigureAwait(false);
                    if (map.ExternalPort < 1 || map.LifetimeSeconds == 0) { notes.Add("NAT-PMP " + gw + ": mapping rejected (port " + map.ExternalPort + ", lifetime " + map.LifetimeSeconds + ")"); continue; }
                    bool udp = false;
                    if (_o.MapUdp)
                    {
                        try
                        {
                            var um = await _pmp.MapAsync(gw, local, false, run.InternalPort, map.ExternalPort, life, ct).ConfigureAwait(false);
                            udp = um.ExternalPort == map.ExternalPort;
                            if (!udp) { Log("NAT-PMP: UDP received port " + um.ExternalPort + " instead of " + map.ExternalPort + ": releasing it"); try { await _pmp.DeleteAsync(gw, local, false, run.InternalPort, ct).ConfigureAwait(false); } catch (NatPmpException) { } }
                        }
                        catch (NatPmpException ex) { Log("NAT-PMP UDP unavailable: " + ex.Message); }
                    }
                    Log("NAT-PMP: port " + map.ExternalPort + " opened on " + gw + " (lifetime " + map.LifetimeSeconds + " s, UDP " + (udp ? "yes" : "no") + ", internal IP " + local + ")");
                    return new ActiveMapping
                    {
                        Method = "NAT-PMP", InternalIp = local, InternalPort = run.InternalPort, ExternalPort = map.ExternalPort, ExternalIp = ext.Address.ToString(),
                        LeaseSeconds = (int)Math.Min(map.LifetimeSeconds, int.MaxValue), UdpMapped = udp, Router = gw, Gateway = gw, Epoch = map.Epoch, EpochAtUtc = DateTime.UtcNow, LastRenewUtc = DateTime.UtcNow
                    };
                }
                catch (NatPmpException ex) { notes.Add("NAT-PMP " + gw + ": " + ex.Message); }
            }
            return null;
        }

        // ---------- Maintenance ----------

        /// <summary>Renews and watches the mapping until it is lost (returns the reason) or cancelled (throws OperationCanceledException).</summary>
        private async Task<string> MaintainAsync(Run run, ActiveMapping m, CancellationToken ct)
        {
            int failures = 0;
            bool immediate = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!immediate)
                {
                    TimeSpan wait = _o.CheckInterval;
                    if (m.LeaseSeconds > 0)
                    {
                        var dueIn = m.LastRenewUtc + TimeSpan.FromSeconds(m.LeaseSeconds * _o.RenewFraction) - DateTime.UtcNow;
                        if (dueIn < wait) wait = dueIn;
                    }
                    if (wait < TimeSpan.FromMilliseconds(20)) wait = TimeSpan.FromMilliseconds(20);
                    await WaitWakeAsync(wait, ct).ConfigureAwait(false);
                }
                immediate = false;

                var cur = _net.LocalAddressFor(m.Router);
                if (cur != null && !cur.Equals(IPAddress.Any) && !IpClass.IsLoopback(cur) && !cur.Equals(m.InternalIp))
                    return "the local IP changed from " + m.InternalIp + " to " + cur;

                bool renew = m.LeaseSeconds > 0 && DateTime.UtcNow >= m.LastRenewUtc + TimeSpan.FromSeconds(m.LeaseSeconds * _o.RenewFraction) - TimeSpan.FromMilliseconds(30);
                string problem = await RefreshAsync(m, renew, ct).ConfigureAwait(false);
                if (problem == null)
                {
                    failures = 0;
                    Update(run, UpnpState.Open, m.ExternalPort, m.ExternalIp, m.Method, "port " + m.ExternalPort + " opened by " + m.Method);
                    continue;
                }
                if (problem.StartsWith("external IP", StringComparison.Ordinal)) return problem;
                Log("mapping check failed (" + (failures + 1) + "): " + problem);
                if (++failures >= 2) return problem;
                await Task.Delay(_o.RefreshRetryDelay, ct).ConfigureAwait(false);
                immediate = true;
            }
        }

        /// <summary>Renews or checks. Returns null if all is well, or the problem.</summary>
        private async Task<string> RefreshAsync(ActiveMapping m, bool renew, CancellationToken ct)
        {
            try
            {
                string newIp = null;
                if (m.Method == "UPnP")
                {
                    if (renew)
                    {
                        m.LeaseSeconds = await IgdMapper.AddWithLeaseFallbackAsync(_igd, m.Service, "TCP", m.ExternalPort, m.InternalIp, m.InternalPort, _o.Description, m.LeaseSeconds, Log, ct).ConfigureAwait(false);
                        if (m.UdpMapped)
                        {
                            try { await _igd.AddPortMappingAsync(m.Service, "UDP", m.ExternalPort, m.InternalIp, m.InternalPort, _o.Description, m.LeaseSeconds, ct).ConfigureAwait(false); }
                            catch (UpnpException ex) { Log("UDP renewal: " + ex.Message); }
                        }
                        m.LastRenewUtc = DateTime.UtcNow;
                    }
                    else if (!m.Unverified)
                    {
                        var entry = await _igd.GetSpecificPortMappingEntryAsync(m.Service, "TCP", m.ExternalPort, ct).ConfigureAwait(false);
                        if (entry == null || (entry.InternalClient.Length > 0 && entry.InternalClient.Trim() != m.InternalIp.ToString()) || (entry.InternalPort != 0 && entry.InternalPort != m.InternalPort))
                        {
                            Log("the router no longer has our mapping (reboot or expired): recreating it");
                            await IgdMapper.AddWithLeaseFallbackAsync(_igd, m.Service, "TCP", m.ExternalPort, m.InternalIp, m.InternalPort, _o.Description, m.LeaseSeconds, Log, ct).ConfigureAwait(false);
                            if (m.UdpMapped) { try { await _igd.AddPortMappingAsync(m.Service, "UDP", m.ExternalPort, m.InternalIp, m.InternalPort, _o.Description, m.LeaseSeconds, ct).ConfigureAwait(false); } catch (UpnpException) { } }
                            m.LastRenewUtc = DateTime.UtcNow;
                        }
                    }
                    try { var ip = await _igd.GetExternalIPAddressAsync(m.Service, ct).ConfigureAwait(false); newIp = ip.ToString(); }
                    catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap) { /* the external IP is optional */ }
                }
                else
                {
                    if (!renew)
                    {
                        var ext = await _pmp.GetExternalAddressAsync(m.Gateway, m.InternalIp, ct).ConfigureAwait(false);
                        newIp = ext.Address.ToString();
                        double elapsed = (DateTime.UtcNow - m.EpochAtUtc).TotalSeconds;
                        bool reset = ext.Epoch < m.Epoch || (ext.Epoch - m.Epoch) + 2 < elapsed * 7 / 8;        // RFC 6886 3.2.1: the router rebooted and lost the mappings
                        m.Epoch = ext.Epoch; m.EpochAtUtc = DateTime.UtcNow;
                        if (reset) { Log("NAT-PMP: the router rebooted (epoch): mapping again"); renew = true; }
                    }
                    if (renew)
                    {
                        var map = await _pmp.MapAsync(m.Gateway, m.InternalIp, true, m.InternalPort, m.ExternalPort, (uint)Math.Max(60, m.LeaseSeconds), ct).ConfigureAwait(false);
                        m.ExternalPort = map.ExternalPort; m.Epoch = map.Epoch; m.EpochAtUtc = DateTime.UtcNow;
                        if (map.LifetimeSeconds > 0) m.LeaseSeconds = (int)Math.Min(map.LifetimeSeconds, int.MaxValue);
                        if (m.UdpMapped) { try { await _pmp.MapAsync(m.Gateway, m.InternalIp, false, m.InternalPort, m.ExternalPort, (uint)Math.Max(60, m.LeaseSeconds), ct).ConfigureAwait(false); } catch (NatPmpException) { } }
                        m.LastRenewUtc = DateTime.UtcNow;
                    }
                }
                if (newIp != null && newIp != m.ExternalIp)
                {
                    IPAddress parsed;
                    if (IPAddress.TryParse(newIp, out parsed) && (IpClass.IsUnspecified(parsed) || _o.RequirePublicExternalIp && !IpClass.IsPublic(parsed)))
                        return "external IP " + newIp + " is no longer public (" + IpClass.Describe(parsed) + ")";
                    Log("the external IP changed from '" + m.ExternalIp + "' to '" + newIp + "'");
                    m.ExternalIp = newIp;
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------- Deletion ----------

        private async Task RemoveQuietlyAsync(ActiveMapping m)
        {
            using (var cts = new CancellationTokenSource(_o.StopTimeout))
            {
                try
                {
                    if (m.Method == "UPnP")
                        await IgdMapper.RemoveAsync(_igd, m.Service, m.ExternalPort, m.UdpMapped, Log, cts.Token).ConfigureAwait(false);
                    else
                    {
                        try { await _pmp.DeleteAsync(m.Gateway, m.InternalIp, true, m.InternalPort, cts.Token).ConfigureAwait(false); } catch (NatPmpException ex) { Log("NAT-PMP: could not delete TCP: " + ex.Message); }
                        if (m.UdpMapped) { try { await _pmp.DeleteAsync(m.Gateway, m.InternalIp, false, m.InternalPort, cts.Token).ConfigureAwait(false); } catch (NatPmpException) { } }
                    }
                    Log("mapping of port " + m.ExternalPort + " (" + m.Method + ") deleted");
                }
                catch (OperationCanceledException) { Log("deleting mapping " + m.ExternalPort + " not completed (timeout)"); }
                catch (Exception ex) { Log("deleting mapping " + m.ExternalPort + ": " + ex.Message); }
            }
        }
    }
}
