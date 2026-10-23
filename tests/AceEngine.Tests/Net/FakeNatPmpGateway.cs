using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;

namespace AceEngine.Tests.NetTests
{
    internal sealed class FakeNatMapping
    {
        public bool Tcp;
        public int InternalPort, ExternalPort;
        public uint Lifetime;
        public Stopwatch Age = Stopwatch.StartNew();
        public bool Expired { get { return Age.Elapsed.TotalSeconds >= Lifetime; } }
    }

    /// <summary>Simulated NAT-PMP gateway (RFC 6886) over the virtual UDP network: opcodes 0, 1 and 2, epoch, mappings table and configurable failures.</summary>
    internal sealed class FakeNatPmpGateway : IDisposable
    {
        private readonly FakeUdpNetwork _net;
        public readonly IPEndPoint Endpoint;
        private readonly object _gate = new object();
        private readonly List<FakeNatMapping> _mappings = new List<FakeNatMapping>();
        private Stopwatch _since = Stopwatch.StartNew();
        private uint _epochBase = 1000;
        private int _received;

        public volatile bool Respond = true;
        /// <summary>Ignores the first N datagrams (to test retries).</summary>
        public int DropFirst;
        /// <summary>If not 0, all responses carry this result code.</summary>
        public int ForcedResult;
        public string ExternalIp = "203.0.113.7";
        /// <summary>The assigned port is the suggested one plus this (the router "picks another").</summary>
        public int ExternalPortOffset;
        /// <summary>Cap on the granted lifetime (0 = the requested one).</summary>
        public uint LifetimeCap;
        /// <summary>If not null, responses are sent from this origin (spoofing).</summary>
        public IPEndPoint SpoofSource;
        /// <summary>Before each real response, sends this garbage datagram from the correct origin.</summary>
        public byte[] JunkBeforeReply;
        public readonly ConcurrentQueue<byte[]> Requests = new ConcurrentQueue<byte[]>();

        public FakeNatPmpGateway(FakeUdpNetwork net, string ip = "192.168.1.1", int port = 5351)
        {
            _net = net;
            Endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            net.Register(Endpoint, OnPacket);
        }

        public uint Epoch { get { lock (_gate) return _epochBase + (uint)_since.Elapsed.TotalSeconds; } }
        /// <summary>Router restart: the epoch starts over and the mappings are lost.</summary>
        public void Reboot() { lock (_gate) { _since = Stopwatch.StartNew(); _epochBase = 0; _mappings.Clear(); } }
        public IReadOnlyList<FakeNatMapping> Mappings { get { lock (_gate) { _mappings.RemoveAll(m => m.Expired); return _mappings.ToList(); } } }
        public bool Has(bool tcp, int internalPort) { return Mappings.Any(m => m.Tcp == tcp && m.InternalPort == internalPort); }
        public int RequestCount { get { return Requests.Count; } }

        private void Reply(IPEndPoint to, byte[] data)
        {
            var from = SpoofSource ?? Endpoint;
            if (JunkBeforeReply != null) _net.Deliver(Endpoint, to, JunkBeforeReply);
            _net.Deliver(from, to, data);
        }

        private static void Put32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private void OnPacket(FakePacket p)
        {
            Requests.Enqueue(p.Data);
            if (Interlocked.Increment(ref _received) <= DropFirst) return;
            if (!Respond) return;
            var d = p.Data;
            if (d.Length < 2) return;
            if (d[0] != 0)
            {
                var bad = new byte[8]; bad[1] = 128; bad[3] = 1; Put32(bad, 4, Epoch);
                Reply(p.From, bad);
                return;
            }
            byte op = d[1];
            if (op == 0 && d.Length == 2)
            {
                var r = new byte[12];
                r[1] = 128; r[3] = (byte)ForcedResult; Put32(r, 4, Epoch);
                var ip = IPAddress.Parse(ExternalIp).GetAddressBytes();
                Array.Copy(ip, 0, r, 8, 4);
                Reply(p.From, r);
                return;
            }
            if ((op == 1 || op == 2) && d.Length == 12)
            {
                bool tcp = op == 2;
                int intPort = (d[4] << 8) | d[5];
                int sugg = (d[6] << 8) | d[7];
                uint life = ((uint)d[8] << 24) | ((uint)d[9] << 16) | ((uint)d[10] << 8) | d[11];
                var r = new byte[16];
                r[1] = (byte)(128 + op); r[3] = (byte)ForcedResult; Put32(r, 4, Epoch);
                r[8] = d[4]; r[9] = d[5];
                lock (_gate)
                {
                    _mappings.RemoveAll(m => m.Expired);
                    if (ForcedResult == 0)
                    {
                        if (life == 0)
                        {
                            _mappings.RemoveAll(m => m.Tcp == tcp && m.InternalPort == intPort);
                            r[10] = 0; r[11] = 0;
                        }
                        else
                        {
                            var existing = _mappings.FirstOrDefault(m => m.Tcp == tcp && m.InternalPort == intPort);
                            int ext = existing != null ? existing.ExternalPort : (sugg != 0 ? sugg : intPort) + ExternalPortOffset;
                            if (existing == null) while (_mappings.Any(m => m.Tcp == tcp && m.ExternalPort == ext)) ext++;
                            uint granted = LifetimeCap != 0 ? Math.Min(life, LifetimeCap) : life;
                            _mappings.RemoveAll(m => m.Tcp == tcp && m.InternalPort == intPort);
                            _mappings.Add(new FakeNatMapping { Tcp = tcp, InternalPort = intPort, ExternalPort = ext, Lifetime = granted });
                            r[10] = (byte)(ext >> 8); r[11] = (byte)ext;
                            Put32(r, 12, granted);
                        }
                    }
                }
                Reply(p.From, r);
                return;
            }
            var unsupported = new byte[8]; unsupported[1] = (byte)(128 + op); unsupported[3] = 5; Put32(unsupported, 4, Epoch);
            Reply(p.From, unsupported);
        }

        public void Dispose() { _net.Unregister(Endpoint, OnPacket); }
    }
}
