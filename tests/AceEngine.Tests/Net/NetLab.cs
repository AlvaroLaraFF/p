using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;

namespace AceEngine.Tests.NetTests
{
    /// <summary>Network test environment: virtual UDP network, fake network configuration and (optionally) a simulated IGD router and NAT-PMP gateway.</summary>
    internal sealed class NetLab : IDisposable
    {
        public readonly FakeUdpNetwork Net = new FakeUdpNetwork();
        public readonly FakeNetworkInfo Info = FakeNetworkInfo.Home();
        public FakeIgdRouter Igd;
        public FakeNatPmpGateway Pmp;

        public NetLab(IgdVariant? igd = IgdVariant.IpV1, bool natPmp = false)
        {
            if (igd.HasValue) Igd = new FakeIgdRouter(Net, igd.Value);
            if (natPmp) Pmp = new FakeNatPmpGateway(Net);
        }

        public static SsdpOptions FastSsdp()
        {
            return new SsdpOptions { Window = TimeSpan.FromSeconds(2), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), GraceAfterFirstIgd = TimeSpan.FromMilliseconds(50), AllowLoopbackLocation = true };
        }

        /// <summary>PortMapper options with short deadlines and the seams pointing at this lab.</summary>
        public PortMapperOptions MapperOptions()
        {
            return new PortMapperOptions
            {
                Udp = Net,
                Network = Info,
                AllowLoopbackLocation = true,
                Ssdp = new SsdpOptions { Window = TimeSpan.FromMilliseconds(600), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), GraceAfterFirstIgd = TimeSpan.FromMilliseconds(50), AllowLoopbackLocation = true },
                Igd = new IgdOptions { Timeout = TimeSpan.FromSeconds(2) },
                NatPmp = new NatPmpOptions { InitialTimeout = TimeSpan.FromMilliseconds(40), MaxAttempts = 3 },
                RetryMin = TimeSpan.FromMilliseconds(250),
                RetryMax = TimeSpan.FromMilliseconds(1000),
                NetworkChangeDebounce = TimeSpan.FromMilliseconds(20),
                RefreshRetryDelay = TimeSpan.FromMilliseconds(50),
                StopTimeout = TimeSpan.FromSeconds(2),
                DisposeWait = TimeSpan.FromSeconds(3),
                CheckInterval = TimeSpan.FromMinutes(5)
            };
        }

        public SsdpClient Ssdp() { return new SsdpClient(Net, Info); }

        public void Dispose()
        {
            if (Igd != null) Igd.Dispose();
            if (Pmp != null) Pmp.Dispose();
        }
    }

    internal static class Wait
    {
        public static async Task<bool> UntilAsync(Func<bool> cond, int timeoutMs = 8000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (cond()) return true;
                await Task.Delay(15).ConfigureAwait(false);
            }
            return cond();
        }
    }

    /// <summary>Test double of IPortMapper for the host tests.</summary>
    internal sealed class FakePortMapper : IPortMapper
    {
        private readonly object _gate = new object();
        private AceEngine.App.Tv.UpnpState _state = AceEngine.App.Tv.UpnpState.Disabled;
        private int _port;
        public int StartCalls, StopCalls, DisposeCalls;
        public int LastInternalPort, LastPreferred;

        public AceEngine.App.Tv.UpnpState State { get { lock (_gate) return _state; } }
        public int OpenPort { get { lock (_gate) return _port; } }
        public string ExternalIp { get { return State == AceEngine.App.Tv.UpnpState.Open ? "203.0.113.7" : ""; } }
        public string Method { get { return State == AceEngine.App.Tv.UpnpState.Open ? "UPnP" : ""; } }
        public event Action Changed;

        public void Start(int internalPort, int preferredExternalPort = 0)
        {
            StartCalls++; LastInternalPort = internalPort; LastPreferred = preferredExternalPort;
            Set(AceEngine.App.Tv.UpnpState.Searching, 0);
        }

        public void Stop() { StopCalls++; Set(AceEngine.App.Tv.UpnpState.Disabled, 0); }
        public void Dispose() { DisposeCalls++; }

        public void Set(AceEngine.App.Tv.UpnpState state, int port)
        {
            lock (_gate) { _state = state; _port = port; }
            var h = Changed;
            if (h != null) h();
        }
    }
}
