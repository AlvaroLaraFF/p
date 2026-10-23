using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;

namespace AceEngine.Tests.NetTests
{
    /// <summary>A datagram traveling over the virtual network.</summary>
    internal sealed class FakePacket
    {
        public IPEndPoint From, To;
        public byte[] Data;
    }

    /// <summary>
    /// In-memory virtual UDP network (no real sockets or real multicast): sockets are opened with IUdpTransport.Open, services (simulated router) are registered at an
    /// endpoint (ip:port) or in a multicast group. Reusable by FakeIgdRouter and FakeNatPmpGateway.
    /// </summary>
    internal sealed class FakeUdpNetwork : IUdpTransport
    {
        public static readonly IPEndPoint SsdpGroup = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Action<FakePacket>>> _services = new Dictionary<string, List<Action<FakePacket>>>();
        private readonly List<FakeSocket> _sockets = new List<FakeSocket>();
        private int _nextPort = 41000;

        /// <summary>IP used by a socket opened without a specific address.</summary>
        public IPAddress DefaultLocal = IPAddress.Parse("192.168.1.50");
        /// <summary>Drops everything sent to a multicast group (multicast filtered by the system or the AP).</summary>
        public volatile bool DropMulticast;
        /// <summary>If not null, datagrams for which it returns true are lost.</summary>
        public Func<FakePacket, bool> DropFilter;
        /// <summary>If not null, prevents opening sockets on that IP (to test broken interfaces).</summary>
        public Func<IPAddress, bool> FailOpen;
        public readonly ConcurrentQueue<FakePacket> Sent = new ConcurrentQueue<FakePacket>();
        public int OpenSockets { get { lock (_gate) return _sockets.Count(s => !s.IsDisposed); } }
        public int TotalOpened;

        public IUdpSocket Open(IPAddress localAddress, int localPort)
        {
            if (FailOpen != null && localAddress != null && FailOpen(localAddress)) throw new System.Net.Sockets.SocketException(10049);
            var ip = localAddress == null || localAddress.Equals(IPAddress.Any) ? DefaultLocal : localAddress;
            lock (_gate)
            {
                int port = localPort != 0 ? localPort : _nextPort++;
                var s = new FakeSocket(this, ip, port, localAddress == null || localAddress.Equals(IPAddress.Any));
                _sockets.Add(s);
                TotalOpened++;
                return s;
            }
        }

        public static string Key(IPEndPoint ep) { return ep.Address + ":" + ep.Port; }

        /// <summary>Registers a service that receives the datagrams addressed to ep (unicast or multicast group).</summary>
        public void Register(IPEndPoint ep, Action<FakePacket> handler)
        {
            lock (_gate)
            {
                List<Action<FakePacket>> l;
                if (!_services.TryGetValue(Key(ep), out l)) _services[Key(ep)] = l = new List<Action<FakePacket>>();
                l.Add(handler);
            }
        }

        public void Unregister(IPEndPoint ep, Action<FakePacket> handler)
        {
            lock (_gate) { List<Action<FakePacket>> l; if (_services.TryGetValue(Key(ep), out l)) l.Remove(handler); }
        }

        /// <summary>Delivers a datagram to a destination (used by a service to answer, or to inject garbage).</summary>
        public void Deliver(IPEndPoint from, IPEndPoint to, byte[] data)
        {
            var p = new FakePacket { From = from, To = to, Data = (byte[])data.Clone() };
            Sent.Enqueue(p);
            if (DropFilter != null && DropFilter(p)) return;
            bool multicast = IpClass.IsMulticast(to.Address);
            if (multicast && DropMulticast) return;
            List<Action<FakePacket>> services = null;
            List<FakeSocket> targets;
            lock (_gate)
            {
                List<Action<FakePacket>> l;
                if (_services.TryGetValue(Key(to), out l)) services = new List<Action<FakePacket>>(l);
                targets = _sockets.Where(s => !s.IsDisposed && s.Accepts(to)).ToList();
            }
            if (services != null) foreach (var h in services) { var hh = h; ThreadPool.QueueUserWorkItem(_ => { try { hh(p); } catch (Exception) { } }); }
            foreach (var s in targets) s.Enqueue(new UdpPacket { Data = p.Data, Remote = from });
        }

        internal void Send(FakeSocket from, byte[] data, IPEndPoint to)
        {
            Deliver(new IPEndPoint(from.Ip, from.Port), to, data);
        }

        internal sealed class FakeSocket : IUdpSocket
        {
            private readonly FakeUdpNetwork _net;
            private readonly ConcurrentQueue<UdpPacket> _queue = new ConcurrentQueue<UdpPacket>();
            private readonly SemaphoreSlim _sem = new SemaphoreSlim(0);
            private readonly List<IPAddress> _groups = new List<IPAddress>();
            private readonly bool _wildcard;
            public readonly IPAddress Ip;
            public readonly int Port;
            private volatile bool _disposed;

            public FakeSocket(FakeUdpNetwork net, IPAddress ip, int port, bool wildcard) { _net = net; Ip = ip; Port = port; _wildcard = wildcard; }
            public bool IsDisposed { get { return _disposed; } }
            public IPEndPoint LocalEndPoint { get { return new IPEndPoint(_wildcard ? IPAddress.Any : Ip, Port); } }

            public bool Accepts(IPEndPoint to)
            {
                if (IpClass.IsMulticast(to.Address))
                {
                    lock (_groups) return to.Port == Port && _groups.Contains(to.Address);
                }
                return to.Port == Port && (to.Address.Equals(Ip));
            }

            public void Enqueue(UdpPacket p) { if (_disposed) return; _queue.Enqueue(p); try { _sem.Release(); } catch (ObjectDisposedException) { } }

            public Task SendToAsync(byte[] data, IPEndPoint remote, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (_disposed) throw new ObjectDisposedException("FakeSocket");
                _net.Send(this, data, remote);
                return Task.CompletedTask;
            }

            public async Task<UdpPacket> ReceiveAsync(CancellationToken ct)
            {
                while (true)
                {
                    if (_disposed) return null;
                    try { await _sem.WaitAsync(ct).ConfigureAwait(false); }
                    catch (ObjectDisposedException) { return null; }
                    if (_disposed) return null;
                    UdpPacket p;
                    if (_queue.TryDequeue(out p)) return p;
                }
            }

            public void JoinMulticastGroup(IPAddress group, IPAddress localInterface)
            {
                lock (_groups) _groups.Add(group);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { _sem.Release(1000); } catch (Exception) { }
            }
        }
    }

    /// <summary>Fake network configuration: interfaces, routes and change events.</summary>
    internal sealed class FakeNetworkInfo : INetworkInfo
    {
        private readonly object _gate = new object();
        private List<LocalInterface> _ifaces = new List<LocalInterface>();
        public Func<IPAddress, IPAddress> Route;

        public static LocalInterface Eth(string name, string ip, string gateway, int prefix = 24)
        {
            var li = new LocalInterface { Name = name, Address = IPAddress.Parse(ip), Mask = Mask(prefix), SupportsMulticast = true };
            if (gateway != null) li.Gateways.Add(IPAddress.Parse(gateway));
            return li;
        }

        public static IPAddress Mask(int prefix)
        {
            uint m = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
            return new IPAddress(new[] { (byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m });
        }

        public FakeNetworkInfo(params LocalInterface[] ifaces) { _ifaces = ifaces.ToList(); }

        public static FakeNetworkInfo Home() { return new FakeNetworkInfo(Eth("wlan0", "192.168.1.50", "192.168.1.1")); }

        public void SetInterfaces(params LocalInterface[] ifaces) { lock (_gate) _ifaces = ifaces.ToList(); }

        public IReadOnlyList<LocalInterface> GetInterfaces() { lock (_gate) return _ifaces.ToList(); }

        public IPAddress LocalAddressFor(IPAddress remote)
        {
            if (Route != null) return Route(remote);
            lock (_gate)
            {
                foreach (var i in _ifaces) if (IpClass.SameSubnet(i.Address, remote, i.Mask)) return i.Address;
                return _ifaces.Count == 0 ? null : _ifaces[0].Address;
            }
        }

        public event Action AddressesChanged;
        public void RaiseAddressesChanged() { var h = AddressesChanged; if (h != null) h(); }
        public bool HasSubscribers { get { return AddressesChanged != null; } }
    }
}
