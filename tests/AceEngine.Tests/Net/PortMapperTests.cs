using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Net;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    public class PortMapperTests
    {
        private static async Task<bool> WaitState(IPortMapper m, UpnpState s, int ms = 10000)
        {
            return await Wait.UntilAsync(() => m.State == s, ms);
        }

        private static async Task OpenIt(PortMapper m, int port = 8621, int preferred = -1)
        {
            m.Start(port, preferred < 0 ? port : preferred);
            Assert.True(await WaitState(m, UpnpState.Open), "never reached Open; state " + m.State + ": " + m.Detail);
        }

        [Fact]
        public async Task Opens_the_port_through_the_igd_with_states_events_and_a_clean_stop()
        {
            using (var lab = new NetLab())
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                var seen = new List<string>();
                m.Changed += () => { lock (seen) seen.Add(m.State + ":" + m.OpenPort + ":" + m.Method); };
                Assert.Equal(UpnpState.Disabled, m.State);
                Assert.Equal(0, m.OpenPort);
                Assert.Equal("", m.ExternalIp);
                Assert.Equal("", m.Method);

                m.Start(8621, 8621);
                Assert.Equal(UpnpState.Searching, m.State);                        // synchronous, without waiting for the network
                Assert.True(await WaitState(m, UpnpState.Open));
                Assert.Equal(8621, m.OpenPort);
                Assert.Equal("203.0.113.7", m.ExternalIp);
                Assert.Equal("UPnP", m.Method);

                var tcp = lab.Igd.Get("TCP", 8621);
                var udp = lab.Igd.Get("UDP", 8621);
                Assert.NotNull(tcp); Assert.NotNull(udp);
                Assert.Equal("192.168.1.50", tcp.InternalClient);
                Assert.Equal(8621, tcp.InternalPort);
                Assert.Equal("P!", tcp.Description);
                Assert.Equal(3600, tcp.Lease);                                      // limited lease, not permanent

                lock (seen) { Assert.Equal(new[] { "Searching:0:", "Open:8621:UPnP" }, seen.ToArray()); }

                m.Stop();
                Assert.Equal(UpnpState.Disabled, m.State);                         // Stop is immediate for whoever is watching
                Assert.Equal(0, m.OpenPort);
                Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 5000), "the router keeps the mapping after Stop");
                lock (seen) { Assert.Equal("Disabled:0:", seen.Last()); }
                Assert.True(await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000));
            }
        }

        [Fact]
        public async Task Dispose_deletes_the_mapping_before_returning()
        {
            using (var lab = new NetLab())
            {
                var m = new PortMapper(lab.MapperOptions());
                await OpenIt(m);
                m.Dispose();
                Assert.Empty(lab.Igd.Mappings);
                m.Dispose();                                                        // idempotent
                Assert.Throws<ObjectDisposedException>(() => m.Start(8621));
                Assert.False(lab.Info.HasSubscribers);                              // no hanging subscriptions
            }
        }

        [Fact]
        public async Task Start_is_idempotent_and_changing_the_port_replaces_the_mapping()
        {
            using (var lab = new NetLab())
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                await OpenIt(m);
                int adds = lab.Igd.CallsOf("AddPortMapping").Count;
                int events = 0;
                m.Changed += () => Interlocked.Increment(ref events);
                m.Start(8621, 8621);
                m.Start(8621, 8621);
                await Task.Delay(300);
                Assert.Equal(adds, lab.Igd.CallsOf("AddPortMapping").Count);        // nothing was repeated
                Assert.Equal(0, events);
                Assert.Equal(UpnpState.Open, m.State);

                m.Start(8700, 8700);
                Assert.True(await Wait.UntilAsync(() => m.State == UpnpState.Open && m.OpenPort == 8700));
                Assert.False(lab.Igd.Has("TCP", 8621));                              // the previous one was deleted
                Assert.False(lab.Igd.Has("UDP", 8621));
                Assert.True(lab.Igd.Has("TCP", 8700));
                Assert.Equal(8700, lab.Igd.Get("TCP", 8700).InternalPort);
            }
        }

        [Fact]
        public async Task Preferred_external_port_can_differ_from_the_internal_one()
        {
            using (var lab = new NetLab())
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                await OpenIt(m, 40001, 8621);
                Assert.Equal(8621, m.OpenPort);
                Assert.Equal(40001, lab.Igd.Get("TCP", 8621).InternalPort);
            }
        }

        [Fact]
        public async Task A_busy_external_port_moves_to_another_one()
        {
            using (var lab = new NetLab())
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                lab.Igd.ConflictPorts.Add(8621);
                await OpenIt(m);
                Assert.Equal(8622, m.OpenPort);
                Assert.Equal(8621, lab.Igd.Get("TCP", 8622).InternalPort);          // the internal one is still the listener's
            }
        }

        [Fact]
        public async Task Routers_that_only_accept_permanent_leases_get_lease_zero_and_are_checked_not_renewed()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    lab.Igd.OnlyPermanentLeases = true;
                    await OpenIt(m);
                    Assert.Equal(0, lab.Igd.Get("TCP", 8621).Lease);
                    int adds = lab.Igd.CallsOf("AddPortMapping").Count;
                    await Task.Delay(700);
                    Assert.Equal(adds, lab.Igd.CallsOf("AddPortMapping").Count);    // there are no renewals: only checks
                    Assert.True(lab.Igd.CallsOf("GetSpecificPortMappingEntry").Count >= 3);
                    m.Stop();
                    Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 5000));      // the permanent one is deleted on stop
                }
            }
        }

        [Fact]
        public async Task The_lease_is_renewed_at_half_time_so_the_mapping_outlives_it()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.Lease = TimeSpan.FromSeconds(3);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    Assert.Equal(3, lab.Igd.Get("TCP", 8621).Lease);
                    await Task.Delay(6500);                                          // more than double the lease
                    Assert.Equal(UpnpState.Open, m.State);
                    Assert.True(lab.Igd.Has("TCP", 8621), "the router let the mapping expire");
                    Assert.True(lab.Igd.Has("UDP", 8621));
                    var adds = lab.Igd.CallsOf("AddPortMapping").Where(c => c.Arg("NewProtocol") == "TCP").ToList();
                    Assert.True(adds.Count >= 3, "TCP renewals: " + (adds.Count - 1));        // initial + ~every 1.5 s
                    Assert.All(adds, a => Assert.Equal("3", a.Arg("NewLeaseDuration")));
                }
            }
        }

        [Fact]
        public async Task A_router_that_forgets_the_mapping_gets_it_back()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    lab.Igd.Reboot();
                    Assert.False(lab.Igd.Has("TCP", 8621));
                    Assert.True(await Wait.UntilAsync(() => lab.Igd.Has("TCP", 8621) && lab.Igd.Has("UDP", 8621), 5000), "the mapping was not recreated");
                    Assert.Equal(UpnpState.Open, m.State);
                    Assert.Equal(8621, m.OpenPort);
                }
            }
        }

        [Fact]
        public async Task A_router_that_stops_answering_goes_to_searching_then_unavailable_and_comes_back()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions();
                o.CheckInterval = TimeSpan.FromMilliseconds(150);
                o.Igd = new IgdOptions { Timeout = TimeSpan.FromMilliseconds(300) };
                using (var m = new PortMapper(o))
                {
                    var states = new List<UpnpState>();
                    m.Changed += () => { lock (states) states.Add(m.State); };
                    await OpenIt(m);
                    lab.Igd.HangSoap = true;
                    lab.Igd.AnswerSsdp = false;
                    Assert.True(await WaitState(m, UpnpState.Unavailable), "estado " + m.State);
                    Assert.Equal(0, m.OpenPort);
                    Assert.Equal("", m.Method);
                    lock (states) Assert.Contains(UpnpState.Searching, states.Skip(2));       // it went through Searching when the mapping was lost
                    lab.Igd.HangSoap = false;
                    lab.Igd.AnswerSsdp = true;
                    Assert.True(await WaitState(m, UpnpState.Open), "no se recupero; " + m.Detail);
                    Assert.Equal(8621, m.OpenPort);
                }
            }
        }

        [Fact]
        public async Task Stop_does_not_block_even_when_the_router_hangs()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.StopTimeout = TimeSpan.FromSeconds(1); o.Igd = new IgdOptions { Timeout = TimeSpan.FromSeconds(5) };
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    lab.Igd.HangSoap = true;
                    var sw = Stopwatch.StartNew();
                    m.Stop();
                    Assert.True(sw.ElapsedMilliseconds < 200, "Stop took " + sw.ElapsedMilliseconds + " ms");
                    Assert.Equal(UpnpState.Disabled, m.State);
                }
            }
        }

        [Fact]
        public async Task Dispose_with_a_hanging_router_waits_at_most_DisposeWait()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.StopTimeout = TimeSpan.FromSeconds(10); o.DisposeWait = TimeSpan.FromMilliseconds(500); o.Igd = new IgdOptions { Timeout = TimeSpan.FromSeconds(10) };
                var m = new PortMapper(o);
                await OpenIt(m);
                lab.Igd.HangSoap = true;
                var sw = Stopwatch.StartNew();
                m.Dispose();
                Assert.InRange(sw.ElapsedMilliseconds, 0, 2500);
            }
        }

        // ---------- NAT-PMP ----------

        [Fact]
        public async Task Falls_back_to_natpmp_when_no_igd_answers()
        {
            using (var lab = new NetLab(null, true))
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                await OpenIt(m);
                Assert.Equal("NAT-PMP", m.Method);
                Assert.Equal(8621, m.OpenPort);
                Assert.Equal("203.0.113.7", m.ExternalIp);
                Assert.True(lab.Pmp.Has(true, 8621));
                Assert.True(lab.Pmp.Has(false, 8621));
                m.Stop();
                Assert.True(await Wait.UntilAsync(() => lab.Pmp.Mappings.Count == 0, 5000), "NAT-PMP keeps the mapping after Stop");
            }
        }

        [Fact]
        public async Task Natpmp_is_also_used_when_the_igd_exists_but_refuses()
        {
            using (var lab = new NetLab(IgdVariant.IpV1, true))
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                lab.Igd.FailAllCode = 501;
                await OpenIt(m);
                Assert.Equal("NAT-PMP", m.Method);
            }
        }

        [Fact]
        public async Task Natpmp_external_port_chosen_by_the_router_is_reported()
        {
            using (var lab = new NetLab(null, true))
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                lab.Pmp.ExternalPortOffset = 3;
                await OpenIt(m);
                Assert.Equal(8624, m.OpenPort);
                Assert.True(lab.Pmp.Has(true, 8621));
            }
        }

        [Fact]
        public async Task Natpmp_mapping_is_renewed_and_survives_its_lifetime()
        {
            using (var lab = new NetLab(null, true))
            {
                lab.Pmp.LifetimeCap = 3;
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    await Task.Delay(5500);
                    Assert.Equal(UpnpState.Open, m.State);
                    Assert.True(lab.Pmp.Has(true, 8621), "it expired without being renewed");
                    int maps = lab.Pmp.Requests.Count(r => r.Length == 12 && r[1] == 2);
                    Assert.True(maps >= 3, "TCP requests " + maps);
                }
            }
        }

        [Fact]
        public async Task Natpmp_router_reboot_is_detected_by_the_epoch()
        {
            using (var lab = new NetLab(null, true))
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    lab.Pmp.Reboot();
                    Assert.False(lab.Pmp.Has(true, 8621));
                    Assert.True(await Wait.UntilAsync(() => lab.Pmp.Has(true, 8621), 5000), "it was not mapped again after the restart");
                    Assert.Equal(UpnpState.Open, m.State);
                }
            }
        }

        // ---------- No router / difficult networks ----------

        [Fact]
        public async Task Nothing_answering_is_unavailable_with_a_reason_and_retries_when_a_router_appears()
        {
            using (var lab = new NetLab(null))
            {
                var o = lab.MapperOptions(); o.RetryMin = TimeSpan.FromMilliseconds(250);
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.Equal(0, m.OpenPort);
                    Assert.Contains("UPnP", m.Detail);
                    Assert.Contains("NAT-PMP", m.Detail);
                    using (var router = new FakeIgdRouter(lab.Net))
                    {
                        Assert.True(await WaitState(m, UpnpState.Open, 15000), "no reintento: " + m.Detail);
                        Assert.True(router.Has("TCP", 8621));
                    }
                }
            }
        }

        [Fact]
        public async Task Retry_backoff_doubles_up_to_the_maximum()
        {
            using (var lab = new NetLab(null))
            {
                var o = lab.MapperOptions(); o.RetryMin = TimeSpan.FromMilliseconds(200); o.RetryMax = TimeSpan.FromMilliseconds(400); o.UseNatPmp = false;
                o.Ssdp.Window = TimeSpan.FromMilliseconds(50); o.Ssdp.Retries = 1;
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    await Task.Delay(2500);
                    int rounds = lab.Net.Sent.Count(p => p.To.Equals(FakeUdpNetwork.SsdpGroup) && Encoding.ASCII.GetString(p.Data).Contains("InternetGatewayDevice:1"));
                    Assert.InRange(rounds, 4, 14);                                   // ~ every 400 ms at the end: neither a flood nor stopped
                }
            }
        }

        [Fact]
        public async Task A_network_change_retries_immediately_instead_of_waiting_the_backoff()
        {
            using (var lab = new NetLab(null))
            {
                var o = lab.MapperOptions(); o.RetryMin = TimeSpan.FromMinutes(5); o.RetryMax = TimeSpan.FromMinutes(5);
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.True(lab.Info.HasSubscribers);
                    using (var router = new FakeIgdRouter(lab.Net))
                    {
                        lab.Info.RaiseAddressesChanged();
                        Assert.True(await WaitState(m, UpnpState.Open, 8000), "it did not react to the network change: " + m.Detail);
                    }
                }
            }
        }

        [Fact]
        public async Task Local_ip_change_deletes_the_old_mapping_and_maps_the_new_address()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    Assert.Equal("192.168.1.50", lab.Igd.Get("TCP", 8621).InternalClient);
                    lab.Info.SetInterfaces(FakeNetworkInfo.Eth("wlan0", "192.168.1.60", "192.168.1.1"));
                    lab.Info.RaiseAddressesChanged();
                    Assert.True(await Wait.UntilAsync(() => { var t = lab.Igd.Get("TCP", 8621); return t != null && t.InternalClient == "192.168.1.60"; }, 12000), "no se remapeo a la IP nueva");
                    Assert.True(await WaitState(m, UpnpState.Open));
                    Assert.Equal(1, lab.Igd.Mappings.Count(x => x.Proto == "TCP"));
                }
            }
        }

        [Fact]
        public async Task Internal_ip_is_the_one_used_to_reach_the_router_not_the_first_interface()
        {
            using (var lab = new NetLab())
            {
                lab.Info.SetInterfaces(FakeNetworkInfo.Eth("eth0", "10.0.0.5", "10.0.0.1"), FakeNetworkInfo.Eth("wlan0", "192.168.1.50", "192.168.1.1"));
                lab.Info.Route = remote => IPAddress.Parse("192.168.1.50");
                lab.Igd.AcceptsFrom = ip => ip.ToString().StartsWith("192.168.1.");
                using (var m = new PortMapper(lab.MapperOptions()))
                {
                    await OpenIt(m);
                    Assert.Equal("192.168.1.50", lab.Igd.Get("TCP", 8621).InternalClient);
                }
                // if the routing table says nothing useful, the interface the router answered through is valid
                lab.Igd.Reboot();
                lab.Info.Route = remote => null;
                using (var m = new PortMapper(lab.MapperOptions()))
                {
                    await OpenIt(m);
                    Assert.Equal("192.168.1.50", lab.Igd.Get("TCP", 8621).InternalClient);
                }
                // and a route pointing to an IP that is not ours or to loopback is discarded
                lab.Igd.Reboot();
                lab.Info.Route = remote => IPAddress.Loopback;
                using (var m = new PortMapper(lab.MapperOptions()))
                {
                    await OpenIt(m);
                    Assert.Equal("192.168.1.50", lab.Igd.Get("TCP", 8621).InternalClient);
                }
            }
        }

        [Theory]
        [InlineData("100.64.3.4", "CGNAT")]
        [InlineData("100.100.1.1", "CGNAT")]
        [InlineData("192.168.0.2", "private")]
        [InlineData("10.20.30.40", "private")]
        [InlineData("0.0.0.0", "WAN")]
        public async Task A_non_public_external_ip_is_unavailable_and_nothing_is_mapped(string externalIp, string expectedInDetail)
        {
            using (var lab = new NetLab(IgdVariant.IpV1, true))
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                lab.Igd.ExternalIp = externalIp;
                lab.Pmp.ExternalIp = externalIp;
                m.Start(8621, 8621);
                Assert.True(await WaitState(m, UpnpState.Unavailable));
                Assert.Contains(expectedInDetail, m.Detail);
                Assert.Empty(lab.Igd.CallsOf("AddPortMapping"));
                Assert.Empty(lab.Pmp.Mappings);
            }
        }

        [Fact]
        public async Task A_non_public_ip_can_be_allowed()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.ExternalIp = "100.64.3.4";
                var o = lab.MapperOptions(); o.RequirePublicExternalIp = false;
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    Assert.Equal("100.64.3.4", m.ExternalIp);
                }
            }
        }

        [Fact]
        public async Task The_external_ip_turning_private_later_drops_the_mapping()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    lab.Igd.ExternalIp = "100.64.9.9";
                    Assert.True(await WaitState(m, UpnpState.Unavailable), "estado " + m.State);
                    Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 5000));
                }
            }
        }

        [Fact]
        public async Task A_changed_external_ip_is_reported()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(150);
                using (var m = new PortMapper(o))
                {
                    await OpenIt(m);
                    int events = 0;
                    m.Changed += () => Interlocked.Increment(ref events);
                    lab.Igd.ExternalIp = "198.51.100.77";
                    Assert.True(await Wait.UntilAsync(() => m.ExternalIp == "198.51.100.77", 5000));
                    Assert.Equal(UpnpState.Open, m.State);
                    Assert.True(events >= 1);
                }
            }
        }

        [Fact]
        public async Task UDP_refused_still_opens_the_tcp_port()
        {
            using (var lab = new NetLab())
            using (var m = new PortMapper(lab.MapperOptions()))
            {
                lab.Igd.RejectUdp = true;
                await OpenIt(m);
                Assert.True(lab.Igd.Has("TCP", 8621));
                Assert.False(lab.Igd.Has("UDP", 8621));
                m.Stop();
                Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 5000));
            }
        }

        [Fact]
        public async Task A_router_that_hides_its_table_opens_unverified_without_thrashing()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(100);
                using (var m = new PortMapper(o))
                {
                    lab.Igd.GetSpecificBroken = true;
                    await OpenIt(m);
                    Assert.Contains("unverified", m.Detail);
                    int adds = lab.Igd.CallsOf("AddPortMapping").Count;
                    await Task.Delay(600);
                    Assert.Equal(adds, lab.Igd.CallsOf("AddPortMapping").Count);
                }
            }
        }

        [Fact]
        public async Task A_description_with_a_dtd_is_rejected_and_the_mapper_survives()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.DescriptionOverride = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><root><device><deviceType>&x;</deviceType></device></root>";
                var o = lab.MapperOptions(); o.RetryMin = TimeSpan.FromMilliseconds(200); o.UseNatPmp = false;
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.Contains("XML", m.Detail);
                    Assert.Empty(lab.Igd.Calls);
                    lab.Igd.DescriptionOverride = null;
                    Assert.True(await WaitState(m, UpnpState.Open, 15000));
                }
            }
        }

        [Fact]
        public async Task A_gigantic_description_and_a_silent_description_server_do_not_hang_the_mapper()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.DescriptionResponder = req => FakeHttpResponse.Xml("<root>" + new string('x', 2000000) + "</root>");
                var o = lab.MapperOptions(); o.RetryMin = TimeSpan.FromMilliseconds(200); o.UseNatPmp = false;
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    lab.Igd.DescriptionResponder = req => new FakeHttpResponse { Hang = true };
                    var o2 = lab.MapperOptions(); o2.Igd = new IgdOptions { Timeout = TimeSpan.FromMilliseconds(300) }; o2.UseNatPmp = false;
                    using (var m2 = new PortMapper(o2))
                    {
                        var sw = Stopwatch.StartNew();
                        m2.Start(8621, 8621);
                        Assert.True(await WaitState(m2, UpnpState.Unavailable));
                        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));
                    }
                }
            }
        }

        // ---------- Concurrency / life cycle ----------

        [Fact]
        public async Task Stopping_during_the_search_is_prompt_and_restarting_works()
        {
            using (var lab = new NetLab(null))
            {
                var o = lab.MapperOptions(); o.Ssdp.Window = TimeSpan.FromSeconds(30);
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    await Task.Delay(150);
                    var sw = Stopwatch.StartNew();
                    m.Stop();
                    Assert.Equal(UpnpState.Disabled, m.State);
                    using (var router = new FakeIgdRouter(lab.Net))
                    {
                        var o2 = lab.MapperOptions();
                        m.Start(8621, 8621);                                         // the previous search (30 s) must have been cancelled
                        Assert.True(await WaitState(m, UpnpState.Open, 8000), "it did not open after restarting; " + m.Detail);
                        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9));
                    }
                }
            }
        }

        [Fact]
        public async Task Disposing_mid_search_returns_quickly_and_leaks_no_sockets()
        {
            using (var lab = new NetLab(null))
            {
                var o = lab.MapperOptions(); o.Ssdp.Window = TimeSpan.FromSeconds(30);
                var m = new PortMapper(o);
                m.Start(8621, 8621);
                await Task.Delay(150);
                var sw = Stopwatch.StartNew();
                m.Dispose();
                Assert.True(sw.ElapsedMilliseconds < 1500, "Dispose took " + sw.ElapsedMilliseconds);
                Assert.True(await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 3000));
            }
        }

        [Fact]
        public async Task Concurrent_start_and_stop_from_many_threads_ends_clean()
        {
            using (var lab = new NetLab())
            {
                var m = new PortMapper(lab.MapperOptions());
                var errors = new List<Exception>();
                var rnd = new Random(5);
                var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
                {
                    var r = new Random(i);
                    for (int k = 0; k < 60; k++)
                    {
                        try
                        {
                            switch (r.Next(4))
                            {
                                case 0: m.Start(8621, 8621); break;
                                case 1: m.Start(8700 + r.Next(3), 0); break;
                                case 2: m.Stop(); break;
                                default: var s = m.State; var p = m.OpenPort; var e = m.ExternalIp; var me = m.Method; break;
                            }
                            Thread.Sleep(r.Next(0, 6));
                        }
                        catch (Exception ex) { lock (errors) errors.Add(ex); }
                    }
                })).ToArray();
                await Task.WhenAll(tasks);
                Assert.Empty(errors);
                m.Start(8621, 8621);
                Assert.True(await WaitState(m, UpnpState.Open, 15000), "after the storm it never reached Open: " + m.State + " " + m.Detail);
                m.Dispose();
                Assert.Empty(lab.Igd.Mappings);                                      // no orphan mapping is left
            }
        }

        [Fact]
        public async Task Changed_only_fires_for_observable_changes_and_a_throwing_handler_does_not_break_the_mapper()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.CheckInterval = TimeSpan.FromMilliseconds(100);
                using (var m = new PortMapper(o))
                {
                    int events = 0;
                    m.Changed += () => { Interlocked.Increment(ref events); throw new InvalidOperationException("broken handler"); };
                    await OpenIt(m);
                    int before = events;
                    await Task.Delay(700);
                    Assert.Equal(before, events);                                    // checking is not changing
                    Assert.Equal(UpnpState.Open, m.State);
                }
            }
        }

        [Fact]
        public void Start_validates_its_arguments()
        {
            using (var m = new PortMapper(new PortMapperOptions { Udp = new FakeUdpNetwork(), Network = new FakeNetworkInfo() }))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => m.Start(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => m.Start(70000));
                Assert.Throws<ArgumentOutOfRangeException>(() => m.Start(8621, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => m.Start(8621, 65536));
                Assert.Equal(UpnpState.Disabled, m.State);
            }
        }

        [Fact]
        public async Task With_both_methods_disabled_it_says_so()
        {
            using (var lab = new NetLab())
            {
                var o = lab.MapperOptions(); o.UseUpnp = false; o.UseNatPmp = false;
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.Contains("disabled", m.Detail);
                    Assert.Empty(lab.Igd.Searches);
                }
            }
        }

        [Fact]
        public async Task A_router_that_only_answers_rootdevice_still_opens_the_port()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.SsdpTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upnp:rootdevice" };
                using (var m = new PortMapper(lab.MapperOptions()))
                {
                    await OpenIt(m);
                    Assert.True(lab.Igd.Has("TCP", 8621));
                }
            }
        }

        [Fact]
        public async Task A_router_advertising_a_public_location_is_unavailable_not_followed()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.LocationOverride = "http://203.0.113.9/rootDesc.xml";
                var o = lab.MapperOptions(); o.UseNatPmp = false;
                using (var m = new PortMapper(o))
                {
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.Empty(lab.Igd.Http.Requests);                              // no HTTP request was made
                }
            }
        }

        [Fact]
        public async Task A_hanging_router_does_not_make_the_mapper_try_every_service_and_location()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.HangSoap = true;
                var o = lab.MapperOptions(); o.UseNatPmp = false; o.Igd = new IgdOptions { Timeout = TimeSpan.FromMilliseconds(400) };
                using (var m = new PortMapper(o))
                {
                    var sw = Stopwatch.StartNew();
                    m.Start(8621, 8621);
                    Assert.True(await WaitState(m, UpnpState.Unavailable));
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), "tardo " + sw.Elapsed);
                    Assert.Contains("no response", m.Detail);
                }
            }
        }

        [Fact]
        public async Task IGD_v2_and_ppp_routers_work_through_the_mapper()
        {
            foreach (var variant in new[] { IgdVariant.IpV2, IgdVariant.PppNested, IgdVariant.IpV1UrlBase })
            {
                using (var lab = new NetLab(variant))
                using (var m = new PortMapper(lab.MapperOptions()))
                {
                    await OpenIt(m);
                    Assert.True(lab.Igd.Has("TCP", 8621), variant.ToString());
                }
            }
        }
    }
}
