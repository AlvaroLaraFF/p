using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Net
{
    /// <summary>A received UDP datagram.</summary>
    public sealed class UdpPacket
    {
        public byte[] Data;
        public IPEndPoint Remote;
    }

    /// <summary>Minimal IPv4 UDP socket. Only one ReceiveAsync consumer at a time.</summary>
    public interface IUdpSocket : IDisposable
    {
        IPEndPoint LocalEndPoint { get; }
        Task SendToAsync(byte[] data, IPEndPoint remote, CancellationToken ct);
        /// <summary>Waits for a datagram. Throws OperationCanceledException if cancelled (the socket stays valid); returns null if the socket was closed.</summary>
        Task<UdpPacket> ReceiveAsync(CancellationToken ct);
        /// <summary>Joins a multicast group on the interface with that IP (null = whichever the system picks). Throws if the system does not allow it.</summary>
        void JoinMulticastGroup(IPAddress group, IPAddress localInterface);
    }

    /// <summary>Seam for creating UDP sockets: the real one uses System.Net.Sockets; tests use a virtual network (no real multicast).</summary>
    public interface IUdpTransport
    {
        /// <summary>Opens an IPv4 UDP socket bound to <paramref name="localAddress"/> (null = any) and <paramref name="localPort"/> (0 = ephemeral). With a specific IP it also sets the multicast outgoing interface.</summary>
        IUdpSocket Open(IPAddress localAddress, int localPort);
    }

    /// <summary>Real transport over System.Net.Sockets.Socket (netstandard2.0: SocketTaskExtensions with ArraySegment, no Memory).</summary>
    public sealed class SystemUdpTransport : IUdpTransport
    {
        public static readonly SystemUdpTransport Default = new SystemUdpTransport();

        public IUdpSocket Open(IPAddress localAddress, int localPort)
        {
            return new SystemUdpSocket(localAddress, localPort);
        }
    }

    internal sealed class SystemUdpSocket : IUdpSocket
    {
        private const int BufferSize = 8192;        // a legitimate SSDP/NAT-PMP datagram takes less than 1 KiB; anything that does not fit is truncated and discarded when parsing
        private readonly Socket _s;
        private readonly IPAddress _local;
        private readonly object _gate = new object();
        private readonly byte[] _buf = new byte[BufferSize];
        private Task<SocketReceiveFromResult> _pending;
        private bool _disposed;

        public SystemUdpSocket(IPAddress localAddress, int localPort)
        {
            _local = localAddress ?? IPAddress.Any;
            _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                try { _s.IOControl((IOControlCode)(-1744830452), new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }        // SIO_UDP_CONNRESET on Windows; it does not exist on other systems
                _s.Bind(new IPEndPoint(_local, localPort));
                if (!_local.Equals(IPAddress.Any))
                {
                    try { _s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _local.GetAddressBytes()); } catch (Exception) { }
                }
                try { _s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4); } catch (Exception) { }      // UPnP 1.1 asks for TTL 2-4 for SSDP
            }
            catch (Exception)
            {
                _s.Dispose();
                throw;
            }
        }

        public IPEndPoint LocalEndPoint { get { lock (_gate) { if (_disposed) return null; try { return (IPEndPoint)_s.LocalEndPoint; } catch (Exception) { return null; } } } }

        public async Task SendToAsync(byte[] data, IPEndPoint remote, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Socket s;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException("SystemUdpSocket"); s = _s; }
            await s.SendToAsync(new ArraySegment<byte>(data), SocketFlags.None, remote).ConfigureAwait(false);
        }

        public async Task<UdpPacket> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task<SocketReceiveFromResult> t;
                lock (_gate)
                {
                    if (_disposed) return null;
                    // If a previous wait was cancelled, the receive is still in progress: it is reused (the buffer is single) instead of starting another.
                    if (_pending == null) _pending = _s.ReceiveFromAsync(new ArraySegment<byte>(_buf), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
                    t = _pending;
                }
                if (!t.IsCompleted)
                {
                    var cancelled = new TaskCompletionSource<bool>();
                    using (ct.Register(() => cancelled.TrySetResult(true)))
                    {
                        var done = await Task.WhenAny(t, cancelled.Task).ConfigureAwait(false);
                        if (done != t) throw new OperationCanceledException(ct);
                    }
                }
                SocketReceiveFromResult r;
                try
                {
                    r = await t.ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return null; }
                catch (SocketException ex)
                {
                    lock (_gate) { if (_pending == t) _pending = null; if (_disposed) return null; }
                    // ICMP "port unreachable" from a previous send: not a socket failure.
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionRefused) continue;
                    if (ex.SocketErrorCode == SocketError.OperationAborted || ex.SocketErrorCode == SocketError.Interrupted) return null;
                    throw;
                }
                byte[] data = new byte[r.ReceivedBytes];
                lock (_gate)
                {
                    if (_pending == t) { Buffer.BlockCopy(_buf, 0, data, 0, r.ReceivedBytes); _pending = null; }
                }
                return new UdpPacket { Data = data, Remote = r.RemoteEndPoint as IPEndPoint };
            }
        }

        public void JoinMulticastGroup(IPAddress group, IPAddress localInterface)
        {
            _s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, localInterface ?? IPAddress.Any));
        }

        public void Dispose()
        {
            Task<SocketReceiveFromResult> pending;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                pending = _pending;
            }
            try { _s.Dispose(); } catch (Exception) { }
            if (pending != null) { var ignored = pending.ContinueWith(x => { var e = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }      // the pending receive fails on close: it is observed so that it does not go unobserved
        }
    }
}
