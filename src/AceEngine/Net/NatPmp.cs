using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Net
{
    public sealed class NatPmpOptions
    {
        /// <summary>UDP port of the gateway (RFC 6886: 5351).</summary>
        public int Port = 5351;
        /// <summary>Wait after the first send; it doubles on each retry (RFC 6886 3.1: 250 ms, 500 ms, 1 s...). The RFC asks for up to 9 sends (64 s): here, 4 by default (3.75 s).</summary>
        public TimeSpan InitialTimeout = TimeSpan.FromMilliseconds(250);
        public int MaxAttempts = 4;
    }

    /// <summary>NAT-PMP error: ResultCode 1..5 of the RFC, or 0 with NoResponse = the gateway did not answer.</summary>
    public sealed class NatPmpException : Exception
    {
        public int ResultCode { get; private set; }
        public bool NoResponse { get; private set; }
        public NatPmpException(string message, int resultCode, bool noResponse) : base(message) { ResultCode = resultCode; NoResponse = noResponse; }

        public static string DescribeCode(int code)
        {
            switch (code)
            {
                case 1: return "unsupported version";
                case 2: return "not authorized or disabled on the router";
                case 3: return "network failure (the router has no external IP)";
                case 4: return "out of resources (mapping table full)";
                case 5: return "unsupported operation";
                default: return "code " + code;
            }
        }
    }

    public sealed class NatPmpExternalAddress
    {
        public IPAddress Address;
        /// <summary>Seconds since the router (re)started its mapping table: if it goes down, the router rebooted and the mapping must be redone.</summary>
        public uint Epoch;
    }

    public sealed class NatPmpMapping
    {
        public bool Tcp;
        public int InternalPort, ExternalPort;
        public uint LifetimeSeconds, Epoch;
    }

    /// <summary>NAT-PMP client (RFC 6886) over the UDP seam. Responses only count if they come from the queried gateway and its port.</summary>
    public sealed class NatPmpClient
    {
        private readonly IUdpTransport _udp;
        private readonly NatPmpOptions _o;

        public NatPmpClient(IUdpTransport udp, NatPmpOptions options = null)
        {
            _udp = udp ?? SystemUdpTransport.Default;
            _o = options ?? new NatPmpOptions();
        }

        /// <summary>Opcode 0: external IP of the router.</summary>
        public async Task<NatPmpExternalAddress> GetExternalAddressAsync(IPAddress gateway, IPAddress localAddress, CancellationToken ct)
        {
            var resp = await ExchangeAsync(gateway, localAddress, new byte[] { 0, 0 }, 0, 12, ct).ConfigureAwait(false);
            return new NatPmpExternalAddress { Epoch = ReadU32(resp, 4), Address = new IPAddress(new[] { resp[8], resp[9], resp[10], resp[11] }) };
        }

        /// <summary>Opcode 1 (UDP) or 2 (TCP): requests a mapping. suggestedExternal 0 = whichever the router picks. lifetime 0 + suggestedExternal 0 = delete (RFC 6886 3.4).</summary>
        public async Task<NatPmpMapping> MapAsync(IPAddress gateway, IPAddress localAddress, bool tcp, int internalPort, int suggestedExternal, uint lifetimeSeconds, CancellationToken ct)
        {
            if (internalPort < 1 || internalPort > 65535 || suggestedExternal < 0 || suggestedExternal > 65535) throw new ArgumentOutOfRangeException("port");
            byte op = (byte)(tcp ? 2 : 1);
            var req = new byte[12];
            req[0] = 0; req[1] = op;
            req[4] = (byte)(internalPort >> 8); req[5] = (byte)internalPort;
            req[6] = (byte)(suggestedExternal >> 8); req[7] = (byte)suggestedExternal;
            req[8] = (byte)(lifetimeSeconds >> 24); req[9] = (byte)(lifetimeSeconds >> 16); req[10] = (byte)(lifetimeSeconds >> 8); req[11] = (byte)lifetimeSeconds;
            var resp = await ExchangeAsync(gateway, localAddress, req, op, 16, ct).ConfigureAwait(false);
            return new NatPmpMapping
            {
                Tcp = tcp,
                InternalPort = internalPort,
                ExternalPort = (resp[10] << 8) | resp[11],
                LifetimeSeconds = ReadU32(resp, 12),
                Epoch = ReadU32(resp, 4)
            };
        }

        /// <summary>Deletes a mapping (lifetime 0, suggested external port 0).</summary>
        public Task DeleteAsync(IPAddress gateway, IPAddress localAddress, bool tcp, int internalPort, CancellationToken ct)
        {
            return MapAsync(gateway, localAddress, tcp, internalPort, 0, 0, ct);
        }

        private async Task<byte[]> ExchangeAsync(IPAddress gateway, IPAddress localAddress, byte[] request, byte op, int expectedLength, CancellationToken ct)
        {
            if (gateway == null || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("IPv4 gateway required");
            var gw = new IPEndPoint(gateway, _o.Port);
            using (var sock = _udp.Open(localAddress, 0))
            {
                var timeout = _o.InitialTimeout;
                for (int attempt = 0; attempt < Math.Max(1, _o.MaxAttempts); attempt++)
                {
                    await sock.SendToAsync(request, gw, ct).ConfigureAwait(false);
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        var left = timeout - sw.Elapsed;
                        if (left <= TimeSpan.Zero) break;
                        UdpPacket p;
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(left);
                            try { p = await sock.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; break; }
                        }
                        if (p == null) throw new NatPmpException("UDP socket closed", 0, false);
                        if (p.Remote == null || !p.Remote.Address.Equals(gateway) || p.Remote.Port != _o.Port) continue;       // only the queried gateway
                        byte[] d = p.Data;
                        if (d.Length < 8 || d[0] != 0) continue;                                // version 0
                        if (d[1] != (byte)(128 + op) && d[1] != 128) continue;                  // another operation (or response of an unsupported operation: opcode 128)
                        int result = (d[2] << 8) | d[3];
                        if (result != 0) throw new NatPmpException("NAT-PMP: " + NatPmpException.DescribeCode(result), result, false);
                        if (d[1] != (byte)(128 + op) || d.Length != expectedLength) continue;
                        if (op != 0 && (d[8] != request[4] || d[9] != request[5])) continue;         // response to another request (another internal port): keep waiting for ours
                        return d;
                    }
                    timeout = TimeSpan.FromTicks(timeout.Ticks * 2);
                }
            }
            throw new NatPmpException("the gateway " + gateway + ":" + _o.Port + " does not respond to NAT-PMP", 0, true);
        }

        private static uint ReadU32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }
    }
}
