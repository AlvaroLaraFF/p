using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Links;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Tests.P2P;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    /// <summary>The listener prefers Ace's port (8621), the host announces the mapping's external port and goes back to the listener's when it closes.</summary>
    public class HostPortTests : IDisposable
    {
        private readonly List<IDisposable> _own = new List<IDisposable>();

        public void Dispose() { foreach (var d in _own) { try { d.Dispose(); } catch (Exception) { } } }

        private T Own<T>(T d) where T : IDisposable { _own.Add(d); return d; }

        private static EngineOptions Opts(Action<EngineOptions> tweak = null)
        {
            var o = new EngineOptions { EnableListener = true, EnableDht = false, EnableTrackers = false, PreferredListenPort = 0 };
            if (tweak != null) tweak(o);
            return o;
        }

        private AceEngineHost Host(Action<EngineOptions> tweak = null, IPortMapper mapper = null, bool start = true)
        {
            var h = Own(new AceEngineHost(Opts(tweak), mapper));
            if (start) h.Start();
            return h;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        // ---------- preferred port ----------

        [Fact]
        public void Engine_options_default_to_the_ace_port_without_forcing_it()
        {
            var o = new EngineOptions();
            Assert.Equal(8621, o.PreferredListenPort);
            Assert.Equal(0, o.ListenPort);
        }

        [Fact]
        public void Listener_takes_the_preferred_port_when_it_is_free()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int p = FreePort();
                var h = new AceEngineHost(Opts(o => o.PreferredListenPort = p));
                try
                {
                    h.Start();
                    if (h.ListenPort != p && attempt < 4) continue;          // someone took the port in between: it retries
                    Assert.Equal(p, h.ListenPort);
                    return;
                }
                finally { h.Dispose(); }
            }
        }

        [Fact]
        public void Listener_falls_back_to_a_random_port_when_the_preferred_one_is_busy()
        {
            var busy = new TcpListener(IPAddress.Any, 0);
            busy.Start();
            try
            {
                int p = ((IPEndPoint)busy.LocalEndpoint).Port;
                var h = Host(o => o.PreferredListenPort = p);
                Assert.True(h.ListenPort > 0);
                Assert.NotEqual(p, h.ListenPort);
                Assert.Equal(h.ListenPort, h.AnnouncedPort);
            }
            finally { busy.Stop(); }
        }

        [Fact]
        public void An_explicit_listen_port_wins_over_the_preferred_one_and_fails_cleanly_when_busy()
        {
            int a = FreePort(), b = FreePort();
            var h = Host(o => { o.ListenPort = a; o.PreferredListenPort = b; });
            Assert.Equal(a, h.ListenPort);

            var busy = new TcpListener(IPAddress.Any, 0);
            busy.Start();
            try
            {
                int p = ((IPEndPoint)busy.LocalEndpoint).Port;
                var h2 = Host(o => { o.ListenPort = p; o.PreferredListenPort = b; });
                Assert.Equal(0, h2.ListenPort);                 // no alternative: no listener, and no exception
                Assert.Equal(0, h2.AnnouncedPort);
            }
            finally { busy.Stop(); }
        }

        [Fact]
        public void The_default_engine_options_listen_somewhere_even_when_8621_is_taken()
        {
            TcpListener taker = null;
            try { taker = new TcpListener(IPAddress.Any, 8621); taker.Start(); } catch (SocketException) { taker = null; }
            try
            {
                var h = Own(new AceEngineHost(new EngineOptions { EnableDht = false, EnableTrackers = false }));
                h.Start();
                Assert.True(h.ListenPort > 0);
                if (taker != null) Assert.NotEqual(8621, h.ListenPort);
            }
            finally { if (taker != null) taker.Stop(); }
        }

        // ---------- announced port ----------

        [Fact]
        public void Announced_port_follows_the_mapping_and_returns_to_the_listener_port()
        {
            var m = new FakePortMapper();
            var h = Host(mapper: m);
            int listen = h.ListenPort;
            Assert.True(listen > 0);
            Assert.Equal(listen, h.AnnouncedPort);
            Assert.Same(m, h.PortMapper);
            int changes = 0;
            h.PortMapperChanged += () => Interlocked.Increment(ref changes);

            Assert.False(h.UpnpEnabled);
            h.SetUpnpEnabled(true);
            Assert.True(h.UpnpEnabled);
            Assert.Equal(1, m.StartCalls);
            Assert.Equal(listen, m.LastInternalPort);               // the listener's port is opened
            Assert.Equal(listen, m.LastPreferred);
            Assert.Equal(listen, h.AnnouncedPort);                  // Searching: still the listener's

            m.Set(UpnpState.Open, 51234);
            Assert.Equal(51234, h.AnnouncedPort);                   // open: the external one
            Assert.Equal(listen, h.ListenPort);                     // the listener does not change

            m.Set(UpnpState.Open, listen);                          // opened on the same port
            Assert.Equal(listen, h.AnnouncedPort);

            m.Set(UpnpState.Open, 40000);
            m.Set(UpnpState.Unavailable, 0);                        // the mapping was lost
            Assert.Equal(listen, h.AnnouncedPort);

            m.Set(UpnpState.Open, 40001);
            Assert.Equal(40001, h.AnnouncedPort);
            h.SetUpnpEnabled(false);
            Assert.Equal(1, m.StopCalls);
            Assert.Equal(listen, h.AnnouncedPort);                  // closed: goes back to the original
            Assert.True(changes >= 5);

            // an Open state with port 0 or an implausible one does not fool the host
            m.Set(UpnpState.Open, 0);
            Assert.Equal(listen, h.AnnouncedPort);
        }

        [Fact]
        public async Task Peers_see_the_announced_port_in_the_extended_handshake()
        {
            var m = new FakePortMapper();
            var h = Host(mapper: m);
            var t = TestTorrent.Create(4 * 32768, 32768, seed: 3);
            var path = Path.Combine(Path.GetTempPath(), "p-" + Guid.NewGuid().ToString("N") + ".torrent");
            File.WriteAllBytes(path, t.TorrentBytes);
            try { await h.OpenAsync(AceLink.Parse(path), null, CancellationToken.None); }
            finally { File.Delete(path); }

            h.SetUpnpEnabled(true);
            m.Set(UpnpState.Open, 51234);
            Assert.Equal(51234, await AnnouncedByHandshake(h, t));

            m.Set(UpnpState.Unavailable, 0);
            Assert.Equal(h.ListenPort, await AnnouncedByHandshake(h, t));

        }

        private static async Task<int> AnnouncedByHandshake(AceEngineHost h, TestTorrent t)
        {
            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, h.ListenPort);
                var s = c.GetStream();
                var head = Wire.BuildHandshakeHead(new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 }, t.InfoHash);
                await s.WriteAsync(head, 0, head.Length);
                var reply = new byte[68];
                await ReadExact(s, reply, 68);
                await s.WriteAsync(PeerId.Generate(), 0, 20);
                while (true)
                {
                    var lb = new byte[4];
                    await ReadExact(s, lb, 4);
                    uint len = Wire.GetU32(lb, 0);
                    if (len == 0) continue;
                    var m = new byte[len];
                    await ReadExact(s, m, (int)len);
                    if (m[0] == WireId.Extended && m[1] == 0)
                    {
                        ExtendedHandshakeInfo info;
                        Assert.True(ExtensionMessages.TryParseExtendedHandshake(m, 2, m.Length - 2, out info));
                        return info.ListenPort ?? 0;
                    }
                }
            }
        }

        private static async Task ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int got = 0;
            using (var cts = new CancellationTokenSource(10000))
                while (got < count)
                {
                    int n = await s.ReadAsync(buf, got, count - got, cts.Token);
                    if (n == 0) throw new EndOfStreamException();
                    got += n;
                }
        }

        [Fact]
        public void Settings_screen_state_and_port()
        {
            var m = new FakePortMapper();
            var h = Host(mapper: m, start: false);
            Assert.Equal(UpnpState.Disabled, h.UpnpState);
            h.SetUpnpEnabled(true);
            Assert.Equal(UpnpState.Searching, h.UpnpState);          // switched on but the engine has not started yet
            h.Start();
            Assert.Equal(UpnpState.Searching, h.UpnpState);
            Assert.Equal(0, h.UpnpOpenPort);
            m.Set(UpnpState.Open, 8621);
            Assert.Equal(UpnpState.Open, h.UpnpState);
            Assert.Equal(8621, h.UpnpOpenPort);
            m.Set(UpnpState.Unavailable, 0);
            Assert.Equal(UpnpState.Unavailable, h.UpnpState);
            Assert.Equal(0, h.UpnpOpenPort);
            h.SetUpnpEnabled(false);
            Assert.Equal(UpnpState.Disabled, h.UpnpState);
            Assert.Equal(0, h.UpnpOpenPort);

            var nolisten = Host(o => o.EnableListener = false);
            nolisten.SetUpnpEnabled(true);
            Assert.Equal(UpnpState.Unavailable, nolisten.UpnpState);
        }

        [Fact]
        public void Enabling_upnp_before_start_applies_when_the_engine_starts()
        {
            var m = new FakePortMapper();
            var h = Host(mapper: m, start: false);
            h.SetUpnpEnabled(true);
            Assert.Equal(0, m.StartCalls);
            h.Start();
            Assert.Equal(1, m.StartCalls);
            Assert.Equal(h.ListenPort, m.LastInternalPort);
            h.Start();                                               // idempotent
            Assert.Equal(1, m.StartCalls);
        }

        [Fact]
        public void Without_a_listener_there_is_nothing_to_open()
        {
            var m = new FakePortMapper();
            var h = Host(o => o.EnableListener = false, mapper: m);
            h.SetUpnpEnabled(true);
            Assert.Equal(0, m.StartCalls);
            Assert.Equal(0, h.AnnouncedPort);
        }

        [Fact]
        public void A_mapper_is_created_by_the_factory_when_none_was_assigned_and_the_host_owns_it()
        {
            FakePortMapper made = null;
            var h = Host();
            h.PortMapperFactory = () => made = new FakePortMapper();
            Assert.Null(h.PortMapper);
            h.SetUpnpEnabled(true);
            Assert.NotNull(made);
            Assert.Same(made, h.PortMapper);
            Assert.Equal(1, made.StartCalls);
            h.SetUpnpEnabled(true);                                   // again: the same mapper, Start idempotent in the real one
            Assert.Same(made, h.PortMapper);
            h.Dispose();
            Assert.Equal(1, made.StopCalls);
            Assert.Equal(1, made.DisposeCalls);
        }

        [Fact]
        public void Replacing_the_mapper_disposes_the_old_one_and_starts_the_new_one_if_enabled()
        {
            var a = new FakePortMapper();
            var b = new FakePortMapper();
            var h = Host(mapper: a);
            h.SetUpnpEnabled(true);
            Assert.Equal(1, a.StartCalls);
            h.PortMapper = b;
            Assert.Equal(1, a.StopCalls);
            Assert.Equal(1, a.DisposeCalls);
            Assert.Equal(1, b.StartCalls);
            Assert.Same(b, h.PortMapper);
            // the old one no longer has influence
            a.Set(UpnpState.Open, 50000);
            Assert.Equal(h.ListenPort, h.AnnouncedPort);
            b.Set(UpnpState.Open, 50001);
            Assert.Equal(50001, h.AnnouncedPort);
            h.PortMapper = null;
            Assert.Equal(h.ListenPort, h.AnnouncedPort);
        }

        [Fact]
        public async Task Real_port_mapper_with_a_simulated_router_through_the_host()
        {
            using (var lab = new NetLab())
            {
                var h = Host();
                int listen = h.ListenPort;
                h.PortMapper = new PortMapper(lab.MapperOptions());
                h.SetUpnpEnabled(true);
                Assert.True(await Wait.UntilAsync(() => h.PortMapper.State == UpnpState.Open, 10000), "no abrio: " + ((PortMapper)h.PortMapper).Detail);
                Assert.Equal(listen, h.AnnouncedPort);                                 // the router gives the same port
                var tcp = lab.Igd.Get("TCP", listen);
                Assert.NotNull(tcp);
                Assert.Equal(listen, tcp.InternalPort);

                // it changes external port if there is a conflict
                h.SetUpnpEnabled(false);
                Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 5000));
                Assert.Equal(listen, h.AnnouncedPort);
                lab.Igd.ConflictPorts.Add(listen);
                h.SetUpnpEnabled(true);
                Assert.True(await Wait.UntilAsync(() => h.PortMapper.State == UpnpState.Open, 10000));
                Assert.NotEqual(listen, h.AnnouncedPort);
                Assert.Equal(h.PortMapper.OpenPort, h.AnnouncedPort);
                Assert.Equal(listen, h.ListenPort);

                h.Dispose();                                                            // the host's Dispose deletes the mapping
                Assert.Empty(lab.Igd.Mappings);
            }
        }
    }
}
