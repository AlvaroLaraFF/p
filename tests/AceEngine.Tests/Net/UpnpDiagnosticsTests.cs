using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    public class UpnpDiagnosticsTests
    {
        private static readonly Regex LineFormat = new Regex(@"^UPNP\[[a-z0-9-]+\] (OK|FAIL|INCONCLUSIVE): .+$");

        /// <summary>Test connection without going out to the network: the local IP and (if hairpin) the external one reach the real listener over loopback.</summary>
        private static Func<IPEndPoint, TimeSpan, CancellationToken, Task<bool>> Connector(bool hairpin, string externalIp = "203.0.113.7")
        {
            return async (ep, timeout, ct) =>
            {
                if (ep.Address.ToString() == externalIp && !hairpin) return false;
                using (var c = new TcpClient())
                {
                    await c.ConnectAsync(IPAddress.Loopback, ep.Port);
                    return true;
                }
            };
        }

        private static DiagnosticsOptions Options(NetLab lab, bool hairpin = true)
        {
            return new DiagnosticsOptions
            {
                Udp = lab.Net,
                Network = lab.Info,
                AllowLoopbackLocation = true,
                Ssdp = new SsdpOptions { Window = TimeSpan.FromMilliseconds(800), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), GraceAfterFirstIgd = TimeSpan.FromMilliseconds(100) },
                Igd = new IgdOptions { Timeout = TimeSpan.FromSeconds(2) },
                NatPmp = new NatPmpOptions { InitialTimeout = TimeSpan.FromMilliseconds(40), MaxAttempts = 3 },
                Connect = Connector(hairpin),
                ConnectTimeout = TimeSpan.FromSeconds(1),
                MulticastLoopbackWait = TimeSpan.FromMilliseconds(300)
            };
        }

        private static async Task<KeyValuePair<UpnpDiagnosticsReport, List<string>>> Run(NetLab lab, DiagnosticsOptions o = null, CancellationToken ct = default(CancellationToken))
        {
            var lines = new List<string>();
            var rep = await UpnpDiagnostics.RunAsync(l => { lock (lines) lines.Add(l); }, ct, o ?? Options(lab));
            return new KeyValuePair<UpnpDiagnosticsReport, List<string>>(rep, lines);
        }

        private static void Expect(UpnpDiagnosticsReport r, string step, DiagResult result, string contains = null)
        {
            var s = r.Find(step);
            Assert.True(s != null, "missing step " + step + "; steps: " + string.Join(", ", r.Steps.Select(x => x.Name)));
            Assert.True(s.Result == result, "step " + step + ": expected " + result + " but was " + s.Line);
            if (contains != null) Assert.Contains(contains, s.Detail);
        }

        [Fact]
        public async Task Full_run_against_the_simulated_router_reports_every_step_and_cleans_up()
        {
            using (var lab = new NetLab())
            {
                var run = await Run(lab);
                var r = run.Key; var lines = run.Value;

                Assert.All(lines, l => Assert.Matches(LineFormat, l));          // format UPNP[step] OK|FALLO|NO CONCLUYENTE: detail
                Assert.Equal(r.Steps.Select(s => s.Line), lines);

                Expect(r, "start", DiagResult.Ok);
                Expect(r, "network", DiagResult.Ok, "wlan0 192.168.1.50/24");
                Expect(r, "gateway", DiagResult.Ok, "192.168.1.1");
                Expect(r, "multicast-local", DiagResult.Ok);
                Expect(r, "ssdp-multicast", DiagResult.Ok, "LOCATION=" + lab.Igd.DescriptionUrl);
                Expect(r, "ssdp-unicast", DiagResult.Ok, "SERVER=FakeOS/1.0 UPnP/1.1 FakeIGD/2.0");
                Expect(r, "igd", DiagResult.Ok, "Test router");
                Assert.Contains("WANIPConnection:1", r.Find("igd").Detail);
                Assert.Contains("192.168.1.50", r.Find("igd").Detail);
                Expect(r, "external-ip", DiagResult.Ok, "203.0.113.7");
                Expect(r, "mapping", DiagResult.Ok, "-> 192.168.1.50:");
                Expect(r, "incoming-local", DiagResult.Ok);
                Expect(r, "incoming-router", DiagResult.Ok, "203.0.113.7:");
                Expect(r, "mapping-deleted", DiagResult.Ok);
                Expect(r, "natpmp", DiagResult.Fail, "does not answer");
                Expect(r, "summary", DiagResult.Ok);

                Assert.True(r.MulticastDiscovery && r.UnicastDiscovery && r.IgdFound && r.ExternalIpPublic && r.UpnpMapping && r.IncomingLocal && r.IncomingViaRouter);
                Assert.False(r.NatPmpAnswers);
                Assert.Equal("203.0.113.7", r.ExternalIp);
                Assert.Equal(DiagResult.Ok, r.Overall);
                Assert.False(r.Cancelled);

                // the test mapping was set and removed (TCP and UDP), with the correct local IP and a port of its own, not 8621
                Assert.Empty(lab.Igd.Mappings);
                var add = lab.Igd.CallsOf("AddPortMapping").First();
                Assert.Equal("192.168.1.50", add.Arg("NewInternalClient"));
                Assert.Equal("P! test", add.Arg("NewPortMappingDescription"));
                Assert.Equal("120", add.Arg("NewLeaseDuration"));
                Assert.NotEqual("8621", add.Arg("NewInternalPort"));
                Assert.Equal(add.Arg("NewInternalPort"), add.Arg("NewExternalPort"));

                // it only talks to the local network: no datagram goes out to a public IP
                Assert.All(lab.Net.Sent, p => Assert.True(IpClass.IsMulticast(p.To.Address) || IpClass.IsPrivate(p.To.Address), "non-local destination: " + p.To));
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task Natpmp_gateway_is_probed_mapped_and_cleaned()
        {
            using (var lab = new NetLab(IgdVariant.IpV1, true))
            {
                var r = (await Run(lab)).Key;
                Expect(r, "natpmp", DiagResult.Ok, "203.0.113.7");
                Expect(r, "natpmp-mapping", DiagResult.Ok);
                Expect(r, "natpmp-deleted", DiagResult.Ok);
                Assert.True(r.NatPmpAnswers && r.NatPmpMapping);
                Assert.Empty(lab.Pmp.Mappings);
                Assert.Empty(lab.Igd.Mappings);
            }
        }

        [Fact]
        public async Task Natpmp_only_router_still_gets_the_incoming_test_through_its_mapping()
        {
            using (var lab = new NetLab(null, true))
            {
                var run = await Run(lab);
                var r = run.Key;
                Expect(r, "igd", DiagResult.Inconclusive);
                Expect(r, "mapping", DiagResult.Inconclusive);
                Expect(r, "natpmp-mapping", DiagResult.Ok);
                Expect(r, "incoming-router", DiagResult.Ok, "NAT-PMP");
                Assert.True(r.NatPmpMapping);
                Assert.False(r.UpnpMapping);
                Assert.Equal(DiagResult.Ok, r.Overall);
            }
        }

        [Fact]
        public async Task Hairpin_failure_is_inconclusive_with_instructions_not_a_failure()
        {
            using (var lab = new NetLab())
            {
                var r = (await Run(lab, Options(lab, hairpin: false))).Key;
                Expect(r, "incoming-local", DiagResult.Ok);
                Expect(r, "incoming-router", DiagResult.Inconclusive, "hairpin");
                Assert.Contains("203.0.113.7:", r.Find("incoming-router").Detail);
                Assert.False(r.IncomingViaRouter);
                Assert.True(r.IncomingLocal);
                Assert.Equal(DiagResult.Ok, r.Overall);
            }
        }

        [Fact]
        public async Task Local_incoming_failure_is_reported()
        {
            using (var lab = new NetLab())
            {
                var o = Options(lab);
                o.Connect = (ep, t, ct) => Task.FromResult(false);
                var r = (await Run(lab, o)).Key;
                Expect(r, "incoming-local", DiagResult.Fail, "192.168.1.50");
                Assert.False(r.IncomingLocal);
                Assert.NotEqual(DiagResult.Ok, r.Overall);
            }
        }

        [Fact]
        public async Task Connection_accepted_but_not_by_our_listener_does_not_count()
        {
            using (var lab = new NetLab())
            {
                // someone answers the connect (e.g. the router itself) but the connection does not reach the listener
                var o = Options(lab);
                o.Connect = (ep, t, ct) => Task.FromResult(true);
                var r = (await Run(lab, o)).Key;
                Expect(r, "incoming-local", DiagResult.Fail);
                Expect(r, "incoming-router", DiagResult.Inconclusive);
            }
        }

        [Fact]
        public async Task No_router_gives_inconclusive_discovery_and_a_failing_summary()
        {
            using (var lab = new NetLab(null, false))
            {
                var r = (await Run(lab)).Key;
                Expect(r, "network", DiagResult.Ok);
                Expect(r, "ssdp-multicast", DiagResult.Inconclusive, "no response");
                Expect(r, "ssdp-unicast", DiagResult.Inconclusive, "no response");
                Expect(r, "igd", DiagResult.Inconclusive);
                Expect(r, "external-ip", DiagResult.Inconclusive);
                Expect(r, "mapping", DiagResult.Inconclusive);
                Expect(r, "natpmp", DiagResult.Fail);
                Expect(r, "incoming-local", DiagResult.Ok);                     // the listener itself can still be tested
                Expect(r, "incoming-router", DiagResult.Inconclusive, "there is no mapping");
                Expect(r, "summary", DiagResult.Fail);
                Assert.Equal(DiagResult.Fail, r.Overall);
                Assert.False(r.IgdFound);
            }
        }

        [Fact]
        public async Task Blocked_multicast_with_a_working_unicast_path_is_called_out()
        {
            using (var lab = new NetLab())
            {
                lab.Net.DropMulticast = true;
                var r = (await Run(lab)).Key;
                Expect(r, "multicast-local", DiagResult.Inconclusive, "did not come back");
                Expect(r, "ssdp-multicast", DiagResult.Inconclusive);
                Expect(r, "ssdp-unicast", DiagResult.Ok);
                Expect(r, "ssdp-summary", DiagResult.Inconclusive, "multicast is filtered");
                Expect(r, "igd", DiagResult.Ok);
                Expect(r, "mapping", DiagResult.Ok);
                Assert.False(r.MulticastDiscovery);
                Assert.True(r.UnicastDiscovery);
                Assert.True(r.UpnpMapping);
            }
        }

        [Fact]
        public async Task A_system_that_refuses_sockets_is_reported_as_a_failure()
        {
            using (var lab = new NetLab())
            {
                lab.Net.FailOpen = ip => true;
                var r = (await Run(lab)).Key;
                Expect(r, "multicast-local", DiagResult.Fail);
                Expect(r, "ssdp-multicast", DiagResult.Fail, "could not send");
                Expect(r, "igd", DiagResult.Inconclusive);
                Assert.Equal(DiagResult.Fail, r.Overall);
            }
        }

        [Fact]
        public async Task No_network_at_all()
        {
            using (var lab = new NetLab(null))
            {
                lab.Info.SetInterfaces();
                var r = (await Run(lab)).Key;
                Expect(r, "network", DiagResult.Fail, "no active IPv4 interface");
                Expect(r, "gateway", DiagResult.Fail);
                Expect(r, "multicast-local", DiagResult.Fail);
                Expect(r, "natpmp", DiagResult.Inconclusive);
                Expect(r, "summary", DiagResult.Fail);
            }
        }

        [Theory]
        [InlineData("100.64.1.1", "CGNAT")]
        [InlineData("192.168.50.2", "private")]
        [InlineData("0.0.0.0", "the WAN is not connected")]
        public async Task Non_public_external_ips_fail_the_external_ip_and_incoming_steps(string ip, string text)
        {
            using (var lab = new NetLab())
            {
                lab.Igd.ExternalIp = ip;
                var r = (await Run(lab)).Key;
                Expect(r, "external-ip", DiagResult.Fail, text);
                Assert.False(r.ExternalIpPublic);
                if (ip != "0.0.0.0")
                {
                    Expect(r, "mapping", DiagResult.Ok);             // the router accepts the mapping even though it does not serve from the Internet
                    Expect(r, "incoming-router", DiagResult.Fail, "no inbound access from the Internet");
                    Assert.Equal(DiagResult.Fail, r.Overall);
                }
            }
        }

        [Fact]
        public async Task Mapping_refusals_are_explained()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.FailAddCode = 606;
                var r = (await Run(lab)).Key;
                Expect(r, "igd", DiagResult.Ok);
                Expect(r, "mapping", DiagResult.Fail, "not authorized");
                Assert.False(r.UpnpMapping);
                Assert.Null(r.Find("mapping-deleted"));
            }
        }

        [Fact]
        public async Task Routers_with_odd_behaviour_are_described()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.OnlyPermanentLeases = true;
                var r = (await Run(lab)).Key;
                Expect(r, "mapping", DiagResult.Ok, "lease 0 (permanent");
                Expect(r, "mapping-deleted", DiagResult.Ok);
                Assert.Empty(lab.Igd.Mappings);
            }
            using (var lab = new NetLab())
            {
                lab.Igd.GetSpecificBroken = true;
                var r = (await Run(lab)).Key;
                Expect(r, "mapping", DiagResult.Inconclusive, "does not show the mapping");
                Assert.False(r.UpnpMapping);
            }
            using (var lab = new NetLab())
            {
                lab.Igd.ConflictPorts.Add(0);
                lab.Igd.ConflictAll = true;
                var r = (await Run(lab)).Key;
                Expect(r, "mapping", DiagResult.Fail, "conflict");
            }
        }

        [Fact]
        public async Task A_hostile_description_is_a_failed_igd_step()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.DescriptionOverride = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY a \"x\">]><root><device/></root>";
                var r = (await Run(lab)).Key;
                Expect(r, "igd", DiagResult.Fail, "XML");
                Expect(r, "mapping", DiagResult.Inconclusive);
            }
            using (var lab = new NetLab())
            {
                lab.Igd.DescriptionOverride = "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType><serviceList/></device></root>";
                var r = (await Run(lab)).Key;
                Expect(r, "igd", DiagResult.Fail, "WANIPConnection");
            }
        }

        [Fact]
        public async Task Cancellation_returns_a_partial_report_quickly_and_leaves_no_mapping()
        {
            using (var lab = new NetLab())
            using (var cts = new CancellationTokenSource())
            {
                lab.Igd.HangSoap = true;
                var o = Options(lab);
                o.Igd = new IgdOptions { Timeout = TimeSpan.FromSeconds(30) };
                var t = Run(lab, o, cts.Token);
                await Task.Delay(1500);
                var sw = Stopwatch.StartNew();
                cts.Cancel();
                var r = (await t).Key;
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
                Assert.True(r.Cancelled);
                Expect(r, "cancelled", DiagResult.Inconclusive);
                Expect(r, "summary", DiagResult.Inconclusive);
                Assert.Empty(lab.Igd.Mappings);
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task A_throwing_or_missing_log_callback_does_not_break_the_run()
        {
            using (var lab = new NetLab())
            {
                var r1 = await UpnpDiagnostics.RunAsync(l => { throw new InvalidOperationException("broken log"); }, CancellationToken.None, Options(lab));
                Assert.Equal(DiagResult.Ok, r1.Overall);
                var r2 = await UpnpDiagnostics.RunAsync(null, CancellationToken.None, Options(lab));
                Assert.Equal(DiagResult.Ok, r2.Overall);
            }
        }

        [Fact]
        public async Task The_summary_line_is_the_last_one_and_carries_the_verdict()
        {
            using (var lab = new NetLab())
            {
                var run = await Run(lab);
                Assert.StartsWith("UPNP[summary] OK:", run.Value.Last());
                Assert.Contains("multicast yes", run.Key.Summary);
                Assert.Contains("203.0.113.7 (public)", run.Key.Summary);
            }
        }
    }

    /// <summary>Tests with real sockets on the local loopback (no multicast): the real transport and the system's network seam.</summary>
    public class SystemTransportTests
    {
        private static int FreeUdpPort()
        {
            using (var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)s.Client.LocalEndPoint).Port;
        }

        [Fact]
        public async Task Real_udp_sockets_exchange_datagrams_and_cancelling_a_receive_keeps_the_socket_usable()
        {
            var t = new SystemUdpTransport();
            using (var a = t.Open(IPAddress.Loopback, 0))
            using (var b = t.Open(IPAddress.Loopback, 0))
            {
                Assert.Equal(IPAddress.Loopback, a.LocalEndPoint.Address);
                // a cancelled wait does not lose the next datagram
                using (var cts = new CancellationTokenSource(100))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.ReceiveAsync(cts.Token));
                await b.SendToAsync(new byte[] { 1, 2, 3 }, a.LocalEndPoint, CancellationToken.None);
                var p = await a.ReceiveAsync(CancellationToken.None);
                Assert.Equal(new byte[] { 1, 2, 3 }, p.Data);
                Assert.Equal(b.LocalEndPoint.Port, p.Remote.Port);
                // and answers
                await a.SendToAsync(new byte[] { 9 }, p.Remote, CancellationToken.None);
                var q = await b.ReceiveAsync(CancellationToken.None);
                Assert.Equal(new byte[] { 9 }, q.Data);
            }
        }

        [Fact]
        public async Task Disposing_a_socket_with_a_pending_receive_returns_null_and_does_not_throw()
        {
            var t = new SystemUdpTransport();
            var a = t.Open(IPAddress.Loopback, 0);
            var pending = a.ReceiveAsync(CancellationToken.None);
            await Task.Delay(100);
            a.Dispose();
            var done = await Task.WhenAny(pending, Task.Delay(3000));
            Assert.Same(pending, done);
            Assert.Null(await pending);
            Assert.Null(a.LocalEndPoint);
            a.Dispose();                                                    // idempotent
            await Assert.ThrowsAsync<ObjectDisposedException>(() => a.SendToAsync(new byte[1], new IPEndPoint(IPAddress.Loopback, 9), CancellationToken.None));
        }

        [Fact]
        public async Task Sending_to_a_closed_port_does_not_poison_the_socket()
        {
            var t = new SystemUdpTransport();
            using (var a = t.Open(IPAddress.Loopback, 0))
            using (var b = t.Open(IPAddress.Loopback, 0))
            {
                await a.SendToAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, FreeUdpPort()), CancellationToken.None);       // ICMP port unreachable
                await Task.Delay(100);
                await b.SendToAsync(new byte[] { 7 }, a.LocalEndPoint, CancellationToken.None);
                using (var cts = new CancellationTokenSource(3000))
                {
                    var p = await a.ReceiveAsync(cts.Token);
                    Assert.Equal(new byte[] { 7 }, p.Data);
                }
            }
        }

        [Fact]
        public async Task Ssdp_end_to_end_over_real_loopback_sockets_with_the_unicast_path()
        {
            using (var lab = new NetLab())
            using (var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)responder.Client.LocalEndPoint).Port;
                var net = new FakeNetworkInfo(FakeNetworkInfo.Eth("lo-test", "127.0.0.1", "127.0.0.1", 8));
                var cts = new CancellationTokenSource();
                var serve = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        UdpReceiveResult r;
                        try { r = await responder.ReceiveAsync(); } catch (Exception) { return; }
                        string text = Encoding.ASCII.GetString(r.Buffer);
                        if (!text.StartsWith("M-SEARCH * HTTP/1.1") || !text.Contains("InternetGatewayDevice:1")) continue;
                        Assert.Contains("HOST: 127.0.0.1:" + port, text);        // the unicast HOST is the gateway
                        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: " + lab.Igd.DescriptionUrl + "\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nSERVER: real/1.0\r\n\r\n");
                        await responder.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                    }
                });
                var ssdp = new SsdpClient(new SystemUdpTransport(), net);
                var res = await ssdp.DiscoverAsync(new SsdpOptions { UseMulticast = false, UseUnicastToGateway = true, Port = port, Window = TimeSpan.FromSeconds(3), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), AllowLoopbackLocation = true, GraceAfterFirstIgd = TimeSpan.FromMilliseconds(50) }, CancellationToken.None);
                Assert.True(res.AnyIgd, string.Join(";", res.Errors));
                Assert.Equal("real/1.0", res.Responses.First().Server);
                Assert.Equal(IPAddress.Loopback, res.Responses.First().LocalAddress);
                cts.Cancel();
                responder.Dispose();
                await serve;
            }
        }

        [Fact]
        public async Task Natpmp_end_to_end_over_real_loopback_sockets()
        {
            using (var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)gateway.Client.LocalEndPoint).Port;
                var serve = Task.Run(async () =>
                {
                    while (true)
                    {
                        UdpReceiveResult r;
                        try { r = await gateway.ReceiveAsync(); } catch (Exception) { return; }
                        if (r.Buffer.Length == 2)
                        {
                            var resp = new byte[] { 0, 128, 0, 0, 0, 0, 0, 5, 203, 0, 113, 9 };
                            await gateway.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                        }
                    }
                });
                var c = new NatPmpClient(new SystemUdpTransport(), new NatPmpOptions { Port = port, InitialTimeout = TimeSpan.FromMilliseconds(200), MaxAttempts = 3 });
                var ext = await c.GetExternalAddressAsync(IPAddress.Loopback, IPAddress.Loopback, CancellationToken.None);
                Assert.Equal(IPAddress.Parse("203.0.113.9"), ext.Address);
                Assert.Equal(5u, ext.Epoch);
                gateway.Dispose();
                await serve;
                // with nobody listening (ICMP unreachable on loopback) it ends with NoResponse, without hanging
                var ex = await Assert.ThrowsAsync<NatPmpException>(() => c.GetExternalAddressAsync(IPAddress.Loopback, IPAddress.Loopback, CancellationToken.None));
                Assert.True(ex.NoResponse);
            }
        }

        [Fact]
        public void System_network_info_works_on_this_machine()
        {
            var info = new SystemNetworkInfo();
            var ifaces = info.GetInterfaces();
            Assert.All(ifaces, i => { Assert.NotNull(i.Address); Assert.False(IPAddress.IsLoopback(i.Address)); Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, i.Address.AddressFamily); });
            Assert.Equal(IPAddress.Loopback, info.LocalAddressFor(IPAddress.Loopback));
            Assert.Null(info.LocalAddressFor(null));
            Assert.Null(info.LocalAddressFor(IPAddress.IPv6Loopback));
            // subscribing and unsubscribing does not throw nor leave anything hanging
            Action h = () => { };
            info.AddressesChanged += h;
            info.AddressesChanged -= h;
        }
    }
}
