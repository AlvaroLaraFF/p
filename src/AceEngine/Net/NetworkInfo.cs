using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AceEngine.Net
{
    /// <summary>A local IPv4 address with its interface (what is needed to choose which way out to take to the router).</summary>
    public sealed class LocalInterface
    {
        public string Name = "";
        public IPAddress Address;
        public IPAddress Mask;
        /// <summary>IPv4 gateways of the interface (may be empty).</summary>
        public List<IPAddress> Gateways = new List<IPAddress>();
        public bool SupportsMulticast = true;
        public override string ToString() { return Name + " " + Address + (Mask != null ? "/" + IpClass.PrefixLength(Mask) : "") + (Gateways.Count > 0 ? " gw " + string.Join(",", Gateways) : ""); }
    }

    /// <summary>Seam over the device's network configuration. Tests replace it with a double.</summary>
    public interface INetworkInfo
    {
        /// <summary>IPv4 unicast addresses of the active interfaces, excluding loopback and tunnels.</summary>
        IReadOnlyList<LocalInterface> GetInterfaces();

        /// <summary>Local IP the system would use to reach <paramref name="remote"/> (null if there is no route). Sends no packet.</summary>
        IPAddress LocalAddressFor(IPAddress remote);

        /// <summary>The device's addresses or routes change (may never fire on platforms without support). Thread-pool thread.</summary>
        event Action AddressesChanged;
    }

    /// <summary>Real implementation: System.Net.NetworkInformation. Everything is in try/catch because on some platforms (Tizen) parts of the API throw.</summary>
    public sealed class SystemNetworkInfo : INetworkInfo
    {
        public static readonly SystemNetworkInfo Default = new SystemNetworkInfo();
        private readonly object _gate = new object();
        private int _subscribers;
        private NetworkAddressChangedEventHandler _handler;
        private Action _changed;

        public IReadOnlyList<LocalInterface> GetInterfaces()
        {
            var res = new List<LocalInterface>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception) { return res; }
            foreach (var ni in all)
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    var gws = new List<IPAddress>();
                    foreach (var g in props.GatewayAddresses)
                        if (g != null && g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gws.Add(g.Address);
                    bool mc = true;
                    try { mc = ni.SupportsMulticast; } catch (Exception) { }
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address) || ua.Address.Equals(IPAddress.Any)) continue;
                        IPAddress mask = null;
                        try { mask = ua.IPv4Mask; } catch (Exception) { }
                        res.Add(new LocalInterface { Name = ni.Name, Address = ua.Address, Mask = mask, Gateways = new List<IPAddress>(gws), SupportsMulticast = mc });
                    }
                }
                catch (Exception) { /* unreadable interface: ignored */ }
            }
            return res;
        }

        public IPAddress LocalAddressFor(IPAddress remote)
        {
            if (remote == null || remote.AddressFamily != AddressFamily.InterNetwork) return null;
            try
            {
                // A "connected" UDP only consults the system's routing table: no packet is sent.
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(remote, 9));
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep == null || ep.Address.Equals(IPAddress.Any)) return null;
                    return ep.Address;
                }
            }
            catch (Exception) { return null; }
        }

        public event Action AddressesChanged
        {
            add
            {
                lock (_gate)
                {
                    _changed += value;
                    if (_subscribers++ == 0)
                    {
                        try
                        {
                            _handler = (s, e) => { var h = _changed; if (h != null) h(); };
                            NetworkChange.NetworkAddressChanged += _handler;
                        }
                        catch (Exception) { _handler = null; }
                    }
                }
            }
            remove
            {
                lock (_gate)
                {
                    _changed -= value;
                    if (_subscribers > 0 && --_subscribers == 0 && _handler != null)
                    {
                        try { NetworkChange.NetworkAddressChanged -= _handler; } catch (Exception) { }
                        _handler = null;
                    }
                }
            }
        }
    }

    public static class NetworkInfoExtensions
    {
        /// <summary>
        /// Local IP used to reach <paramref name="router"/>: the one the system routing table says (checking that it is one of our addresses and not loopback);
        /// otherwise, the interface the SSDP response arrived on; otherwise, the interface that shares a subnet with the router. Null if it cannot be determined.
        /// </summary>
        public static IPAddress ResolveLocalAddress(this INetworkInfo net, IPAddress router, IPAddress ssdpInterface)
        {
            var ifaces = net.GetInterfaces();
            var viaRoute = net.LocalAddressFor(router);
            if (viaRoute != null && !viaRoute.Equals(IPAddress.Any) && !IpClass.IsLoopback(viaRoute))
            {
                bool known = ifaces.Count == 0;
                foreach (var i in ifaces) if (i.Address.Equals(viaRoute)) known = true;
                if (known) return viaRoute;
            }
            if (ssdpInterface != null && !ssdpInterface.Equals(IPAddress.Any)) return ssdpInterface;
            foreach (var i in ifaces) if (i.Mask != null && IpClass.SameSubnet(i.Address, router, i.Mask)) return i.Address;
            return null;
        }
    }

    /// <summary>Classification of IPv4 addresses (private ranges, CGNAT...).</summary>
    public static class IpClass
    {
        public static bool IsIPv4(IPAddress a) { return a != null && a.AddressFamily == AddressFamily.InterNetwork; }

        /// <summary>RFC 1918: 10/8, 172.16/12, 192.168/16.</summary>
        public static bool IsPrivate(IPAddress a)
        {
            if (!IsIPv4(a)) return false;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>100.64.0.0/10 (RFC 6598): carriers' CGNAT space.</summary>
        public static bool IsCgnat(IPAddress a)
        {
            if (!IsIPv4(a)) return false;
            var b = a.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        public static bool IsLinkLocal(IPAddress a) { if (!IsIPv4(a)) return false; var b = a.GetAddressBytes(); return b[0] == 169 && b[1] == 254; }
        public static bool IsLoopback(IPAddress a) { return IsIPv4(a) && a.GetAddressBytes()[0] == 127; }
        public static bool IsUnspecified(IPAddress a) { return IsIPv4(a) && a.Equals(IPAddress.Any); }
        public static bool IsMulticast(IPAddress a) { return IsIPv4(a) && a.GetAddressBytes()[0] >= 224 && a.GetAddressBytes()[0] <= 239; }

        /// <summary>true if the address belongs to the local network (private, link-local, CGNAT or loopback): a destination a LAN device can point at without going out to the Internet.</summary>
        public static bool IsLanLike(IPAddress a) { return IsPrivate(a) || IsLinkLocal(a) || IsCgnat(a) || IsLoopback(a); }

        /// <summary>true if it seems reachable from the Internet: not private, not CGNAT, not link-local, not loopback, not unspecified, not multicast/reserved.</summary>
        public static bool IsPublic(IPAddress a)
        {
            if (!IsIPv4(a)) return false;
            if (IsPrivate(a) || IsCgnat(a) || IsLinkLocal(a) || IsLoopback(a) || IsUnspecified(a)) return false;
            var b = a.GetAddressBytes();
            if (b[0] >= 224) return false;                      // multicast and reserved (class D/E)
            if (b[0] == 0) return false;
            return true;
        }

        public static string Describe(IPAddress a)
        {
            if (a == null) return "unknown";
            if (!IsIPv4(a)) return "no IPv4";
            if (IsUnspecified(a)) return "unspecified (0.0.0.0: WAN not connected)";
            if (IsLoopback(a)) return "loopback";
            if (IsPrivate(a)) return "private (RFC 1918)";
            if (IsCgnat(a)) return "CGNAT (100.64.0.0/10)";
            if (IsLinkLocal(a)) return "link-local (169.254/16)";
            if (!IsPublic(a)) return "reserved";
            return "public";
        }

        public static int PrefixLength(IPAddress mask)
        {
            if (mask == null) return 0;
            int n = 0;
            foreach (var b in mask.GetAddressBytes()) for (int i = 7; i >= 0; i--) if ((b & (1 << i)) != 0) n++; else return n;
            return n;
        }

        public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            if (!IsIPv4(a) || !IsIPv4(b) || mask == null) return false;
            var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
            return true;
        }
    }
}
