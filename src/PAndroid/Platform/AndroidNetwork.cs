using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using AceEngine.App;
using AceEngine.Net;

namespace PAndroid
{
    /// <summary>
    /// The device's network configuration for the engine's UPnP and diagnostics, read through ConnectivityManager: since Android 10 an app cannot read /proc/net, so the gateway does not
    /// come out of System.Net.NetworkInformation. IPv4 addresses with their prefix and gateways per network, and a notification when the default network or its addresses change.
    /// </summary>
    internal sealed class AndroidNetworkInfo : INetworkInfo
    {
        private const string Tag = "net";
        private readonly ConnectivityManager _cm;
        private readonly object _gate = new object();
        private int _subscribers;
        private Callback _callback;
        private Action _changed;

        public AndroidNetworkInfo(Context applicationContext) { _cm = (ConnectivityManager)applicationContext.GetSystemService(Context.ConnectivityService); }

        public IReadOnlyList<LocalInterface> GetInterfaces()
        {
            var res = new List<LocalInterface>();
            if (_cm == null) return res;
            try
            {
#pragma warning disable CA1422
                var networks = _cm.GetAllNetworks();
#pragma warning restore CA1422
                if (networks == null) return res;
                foreach (var n in networks)
                {
                    try
                    {
                        var caps = _cm.GetNetworkCapabilities(n);
                        var lp = _cm.GetLinkProperties(n);
                        if (caps == null || lp == null) continue;
                        if (caps.HasTransport(TransportType.Vpn)) continue;
                        bool lan = caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet);
                        var gateways = new List<IPAddress>();
                        if (lp.Routes != null)
                            foreach (var r in lp.Routes)
                            {
                                if (!r.IsDefaultRoute) continue;
                                var gw = ToIPv4(r.Gateway);
                                if (gw != null && !gw.Equals(IPAddress.Any)) gateways.Add(gw);
                            }
                        if (lp.LinkAddresses == null) continue;
                        foreach (var la in lp.LinkAddresses)
                        {
                            var ip = ToIPv4(la.Address);
                            if (ip == null || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)) continue;
                            res.Add(new LocalInterface { Name = lp.InterfaceName ?? "", Address = ip, Mask = NetMath.PrefixToMask(la.PrefixLength), Gateways = new List<IPAddress>(gateways), SupportsMulticast = lan });
                        }
                    }
                    catch (Exception ex) { UdpLog.Warn(Tag, "network skipped: " + ex.Message); }
                }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "GetInterfaces: " + ex.Message); }
            return res;
        }

        private static IPAddress ToIPv4(Java.Net.InetAddress a)
        {
            if (!(a is Java.Net.Inet4Address)) return null;
            var b = a.GetAddress();
            return b != null && b.Length == 4 ? new IPAddress(b) : null;
        }

        /// <summary>The routing table decides, with the "connected UDP" trick: it needs no /proc and sends no packet, so it works as well on Android as anywhere.</summary>
        public IPAddress LocalAddressFor(IPAddress remote) { return SystemNetworkInfo.Default.LocalAddressFor(remote); }

        public event Action AddressesChanged
        {
            add
            {
                lock (_gate)
                {
                    _changed += value;
                    if (_subscribers++ == 0 && _cm != null)
                    {
                        try { _callback = new Callback(Raise); _cm.RegisterDefaultNetworkCallback(_callback); }
                        catch (Exception ex) { UdpLog.Warn(Tag, "RegisterDefaultNetworkCallback: " + ex.Message); _callback = null; }
                    }
                }
            }
            remove
            {
                lock (_gate)
                {
                    _changed -= value;
                    if (_subscribers > 0 && --_subscribers == 0 && _callback != null)
                    {
                        try { _cm.UnregisterNetworkCallback(_callback); } catch (Exception) { }
                        _callback = null;
                    }
                }
            }
        }

        private void Raise()
        {
            Action h;
            lock (_gate) h = _changed;
            if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(); } catch (Exception ex) { UdpLog.Warn(Tag, "AddressesChanged handler: " + ex.Message); } });
        }

        private sealed class Callback : ConnectivityManager.NetworkCallback
        {
            private readonly Action _raise;
            public Callback(Action raise) { _raise = raise; }
            public override void OnAvailable(Network network) { _raise(); }
            public override void OnLost(Network network) { _raise(); }
            public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) { _raise(); }
        }
    }

    /// <summary>
    /// WifiManager.MulticastLock: without it many Android devices drop the multicast packets that answer the SSDP search for the router (UPnP). The engine takes it only while it needs it. Without
    /// Wi-Fi (an Ethernet-only box) or without the permission it does nothing.
    /// </summary>
    internal sealed class AndroidMulticastLock : IMulticastLock
    {
        private readonly Context _app;
        private readonly string _tag;
        private WifiManager.MulticastLock _lock;

        /// <param name="tag">The lock's name. The engine's UPnP and the search for televisions each have their own lock: the locks are not reference counted, so sharing one would let one release the other's.</param>
        public AndroidMulticastLock(Context applicationContext, string tag = "p-upnp") { _app = applicationContext; _tag = tag; }

        public void Acquire()
        {
            try
            {
                if (_lock == null)
                {
                    var wifi = (WifiManager)_app.GetSystemService(Context.WifiService);
                    if (wifi == null) { UdpLog.Info("net", "no WifiManager: multicast lock not needed"); return; }
                    _lock = wifi.CreateMulticastLock(_tag);
                    _lock.SetReferenceCounted(false);
                }
                _lock.Acquire();
                UdpLog.Info("net", "multicast lock acquired");
            }
            catch (Exception ex) { UdpLog.Warn("net", "multicast lock: " + ex.Message); }
        }

        public void Release()
        {
            try
            {
                if (_lock != null && _lock.IsHeld) { _lock.Release(); UdpLog.Info("net", "multicast lock released"); }
            }
            catch (Exception ex) { UdpLog.Warn("net", "multicast lock release: " + ex.Message); }
        }
    }
}
