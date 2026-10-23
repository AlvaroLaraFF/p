using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    public class SsdpParseTests
    {
        private static byte[] Msg(string s) { return Encoding.ASCII.GetBytes(s); }

        private const string Good = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nEXT:\r\nlocation: http://192.168.1.1:5000/rootDesc.xml\r\nServer: Linux/4.4 UPnP/1.1 MiniUPnPd/2.1\r\nSt: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nUSN: uuid:abc::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        [Fact]
        public void Parses_valid_response_with_any_header_case()
        {
            SsdpResponse r;
            var b = Msg(Good);
            Assert.True(SsdpResponse.TryParse(b, b.Length, out r));
            Assert.Equal("http://192.168.1.1:5000/rootDesc.xml", r.Location.AbsoluteUri);
            Assert.Equal("urn:schemas-upnp-org:device:InternetGatewayDevice:1", r.St);
            Assert.Equal("Linux/4.4 UPnP/1.1 MiniUPnPd/2.1", r.Server);
            Assert.StartsWith("uuid:abc::", r.Usn);
            Assert.True(r.IsIgd);
            Assert.Equal(1, r.IgdVersion);
        }

        [Fact]
        public void Tolerates_bare_LF_and_missing_final_blank_line()
        {
            SsdpResponse r;
            var b = Msg("HTTP/1.1 200 OK\nLOCATION: http://10.0.0.1/desc.xml\nST: upnp:rootdevice");
            Assert.True(SsdpResponse.TryParse(b, b.Length, out r));
            Assert.Equal(80, r.Location.Port);
            Assert.False(r.IsIgd);
            Assert.Equal(0, r.IgdVersion);
        }

        [Fact]
        public void Igd_version_two_is_detected()
        {
            SsdpResponse r;
            var b = Msg("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.1/d.xml\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:2\r\n\r\n");
            Assert.True(SsdpResponse.TryParse(b, b.Length, out r));
            Assert.Equal(2, r.IgdVersion);
        }

        [Theory]
        [InlineData("")]
        [InlineData("HTTP/1.1 404 Not Found\r\nLOCATION: http://10.0.0.1/d.xml\r\n\r\n")]
        [InlineData("NOTIFY * HTTP/1.1\r\nLOCATION: http://10.0.0.1/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: \r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: https://10.0.0.1/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://router.local/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://user:pw@10.0.0.1/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.1/d x.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: ftp://10.0.0.1/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://[::1]/d.xml\r\n\r\n")]
        [InlineData("HTTP/1.1 200 OK\r\nLOCATION: file:///etc/passwd\r\n\r\n")]
        public void Rejects_malformed_responses(string text)
        {
            SsdpResponse r;
            var b = Msg(text);
            Assert.False(SsdpResponse.TryParse(b, b.Length, out r));
            Assert.Null(r);
        }

        [Fact]
        public void Rejects_binary_oversized_and_null_input()
        {
            SsdpResponse r;
            Assert.False(SsdpResponse.TryParse(null, 0, out r));
            Assert.False(SsdpResponse.TryParse(new byte[0], 0, out r));
            var withNul = Msg(Good); withNul[20] = 0;
            Assert.False(SsdpResponse.TryParse(withNul, withNul.Length, out r));
            var big = Msg(Good + new string('X', 6000));
            Assert.False(SsdpResponse.TryParse(big, big.Length, out r));
            Assert.False(SsdpResponse.TryParse(Msg(Good), 99999, out r));
            // too many headers
            var sb = new StringBuilder("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.1/d.xml\r\n");
            for (int i = 0; i < 100; i++) sb.Append("X-H" + i + ": v\r\n");
            var many = Msg(sb.ToString());
            Assert.False(SsdpResponse.TryParse(many, many.Length, out r));
        }

        [Fact]
        public void Garbage_never_throws_and_never_parses()
        {
            var rnd = new Random(1234);
            for (int i = 0; i < 5000; i++)
            {
                var b = new byte[rnd.Next(0, 600)];
                rnd.NextBytes(b);
                SsdpResponse r;
                Assert.False(SsdpResponse.TryParse(b, b.Length, out r));
            }
            // mutations of a valid response: it may parse or not, but it never throws
            var good = Msg(Good);
            for (int i = 0; i < 3000; i++)
            {
                var b = (byte[])good.Clone();
                for (int k = 0; k < 3; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                SsdpResponse r;
                SsdpResponse.TryParse(b, b.Length, out r);
            }
        }

        [Fact]
        public void Server_header_control_characters_are_neutralised()
        {
            SsdpResponse r;
            var b = Msg("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.1/d.xml\r\nSERVER: evil\u0001\u0007name\r\n\r\n");
            Assert.True(SsdpResponse.TryParse(b, b.Length, out r));
            Assert.Equal("evil??name", r.Server);
        }

        [Theory]
        [InlineData("http://192.168.1.1:80/d.xml", "192.168.1.1", false, true)]
        [InlineData("http://10.1.2.3/d.xml", "10.1.2.3", false, true)]
        [InlineData("http://172.16.0.1/d.xml", "172.16.0.1", false, true)]
        [InlineData("http://169.254.1.1/d.xml", "169.254.1.1", false, true)]
        [InlineData("http://100.64.0.1/d.xml", "100.64.0.1", false, true)]
        [InlineData("http://8.8.8.8/d.xml", "192.168.1.1", false, false)]
        [InlineData("http://203.0.113.5/d.xml", "203.0.113.5", false, false)]
        [InlineData("http://127.0.0.1:6878/d.xml", "192.168.1.1", false, false)]
        [InlineData("http://127.0.0.1:6878/d.xml", "192.168.1.1", true, true)]
        [InlineData("http://0.0.0.0/d.xml", "192.168.1.1", false, false)]
        [InlineData("http://239.255.255.250/d.xml", "192.168.1.1", false, false)]
        public void Location_must_point_into_the_LAN(string url, string sender, bool allowLoopback, bool expected)
        {
            Assert.Equal(expected, SsdpClient.LocationAcceptable(new Uri(url), IPAddress.Parse(sender), allowLoopback));
        }

        [Fact]
        public void MSearch_has_the_required_headers()
        {
            string s = Encoding.ASCII.GetString(SsdpClient.BuildMSearch("239.255.255.250", 1900, "upnp:rootdevice", 2, "ua/1"));
            Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", s);
            Assert.Contains("HOST: 239.255.255.250:1900\r\n", s);
            Assert.Contains("MAN: \"ssdp:discover\"\r\n", s);
            Assert.Contains("MX: 2\r\n", s);
            Assert.Contains("ST: upnp:rootdevice\r\n", s);
            Assert.EndsWith("\r\n\r\n", s);
        }
    }

    public class IpClassTests
    {
        [Theory]
        [InlineData("10.0.0.1", "private (RFC 1918)")]
        [InlineData("172.20.1.1", "private (RFC 1918)")]
        [InlineData("192.168.0.5", "private (RFC 1918)")]
        [InlineData("100.64.0.1", "CGNAT (100.64.0.0/10)")]
        [InlineData("100.127.255.254", "CGNAT (100.64.0.0/10)")]
        [InlineData("169.254.3.4", "link-local (169.254/16)")]
        [InlineData("127.0.0.1", "loopback")]
        [InlineData("203.0.113.7", "public")]
        [InlineData("8.8.8.8", "public")]
        [InlineData("172.32.0.1", "public")]
        [InlineData("100.128.0.1", "public")]
        [InlineData("0.0.0.0", "unspecified (0.0.0.0: WAN not connected)")]
        [InlineData("240.0.0.1", "reserved")]
        public void Describes_ranges(string ip, string expected)
        {
            Assert.Equal(expected, IpClass.Describe(IPAddress.Parse(ip)));
            Assert.Equal(expected == "public", IpClass.IsPublic(IPAddress.Parse(ip)));
        }

        [Fact]
        public void Subnet_helpers()
        {
            var mask = FakeNetworkInfo.Mask(24);
            Assert.True(IpClass.SameSubnet(IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.200"), mask));
            Assert.False(IpClass.SameSubnet(IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.2.5"), mask));
            Assert.Equal(24, IpClass.PrefixLength(mask));
            Assert.Equal(0, IpClass.PrefixLength(IPAddress.Any));
        }
    }

    public class SsdpDiscoveryTests
    {
        [Fact]
        public async Task Finds_router_by_multicast_and_unicast_and_reports_the_local_interface()
        {
            using (var lab = new NetLab())
            {
                var res = await lab.Ssdp().DiscoverAsync(NetLab.FastSsdp(), CancellationToken.None);
                Assert.True(res.AnyIgd);
                var igd = res.Responses.First(r => r.IsIgd);
                Assert.Equal(lab.Igd.DescriptionUrl, igd.Location.AbsoluteUri);
                Assert.Equal(IPAddress.Parse("192.168.1.50"), igd.LocalAddress);
                Assert.Equal(IPAddress.Parse("192.168.1.1"), igd.From.Address);
                Assert.Contains("FakeIGD", igd.Server);
                Assert.True(res.SocketsOpened == 1 && res.MessagesSent >= 3);
                // M-SEARCH over multicast and over unicast to the gateway
                Assert.Contains(lab.Igd.Searches, s => s.Multicast);
                Assert.Contains(lab.Igd.Searches, s => !s.Multicast && s.To.Address.Equals(IPAddress.Parse("192.168.1.1")) && s.To.Port == 1900);
                // the three STs of the brief
                var sts = lab.Igd.Searches.Select(s => s.St).Distinct().ToList();
                Assert.Contains("urn:schemas-upnp-org:device:InternetGatewayDevice:1", sts);
                Assert.Contains("urn:schemas-upnp-org:device:InternetGatewayDevice:2", sts);
                Assert.Contains("upnp:rootdevice", sts);
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000);
                Assert.Equal(0, lab.Net.OpenSockets);       // no socket leaks
            }
        }

        [Fact]
        public async Task Stops_shortly_after_the_first_igd_instead_of_waiting_the_whole_window()
        {
            using (var lab = new NetLab())
            {
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromSeconds(10);
                var sw = Stopwatch.StartNew();
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.True(res.AnyIgd);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "tardo " + sw.Elapsed);
            }
        }

        [Fact]
        public async Task Multicast_filtered_still_works_through_unicast_to_the_gateway()
        {
            using (var lab = new NetLab())
            {
                lab.Net.DropMulticast = true;
                var o = NetLab.FastSsdp();
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.True(res.AnyIgd);
                Assert.All(lab.Igd.Searches, s => Assert.False(s.Multicast));

                // multicast only: it finds nothing, and it is not an error
                var onlyMulti = NetLab.FastSsdp(); onlyMulti.UseUnicastToGateway = false; onlyMulti.Window = TimeSpan.FromMilliseconds(400);
                var none = await lab.Ssdp().DiscoverAsync(onlyMulti, CancellationToken.None);
                Assert.Empty(none.Responses);
                Assert.Empty(none.Errors);
                Assert.True(none.MessagesSent > 0);
            }
        }

        [Fact]
        public async Task No_router_returns_empty_after_the_window_and_leaves_no_sockets()
        {
            using (var lab = new NetLab(null))
            {
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromMilliseconds(400);
                var sw = Stopwatch.StartNew();
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.Empty(res.Responses);
                Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350));
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task Cancellation_returns_promptly_and_closes_sockets()
        {
            using (var lab = new NetLab(null))
            {
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromSeconds(30);
                using (var cts = new CancellationTokenSource())
                {
                    var t = lab.Ssdp().DiscoverAsync(o, cts.Token);
                    await Task.Delay(150);
                    var sw = Stopwatch.StartNew();
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
                }
                await Wait.UntilAsync(() => lab.Net.OpenSockets == 0, 2000);
                Assert.Equal(0, lab.Net.OpenSockets);
            }
        }

        [Fact]
        public async Task Several_interfaces_each_get_their_own_socket_and_the_reply_tells_which_one_reached_the_router()
        {
            using (var lab = new NetLab())
            {
                lab.Info.SetInterfaces(FakeNetworkInfo.Eth("eth0", "10.0.0.5", "10.0.0.1"), FakeNetworkInfo.Eth("wlan0", "192.168.1.50", "192.168.1.1"));
                lab.Igd.AcceptsFrom = ip => ip.ToString().StartsWith("192.168.1.");
                var res = await lab.Ssdp().DiscoverAsync(NetLab.FastSsdp(), CancellationToken.None);
                Assert.Equal(2, res.SocketsOpened);
                Assert.True(res.AnyIgd);
                Assert.All(res.Responses, r => Assert.Equal(IPAddress.Parse("192.168.1.50"), r.LocalAddress));
                var sources = lab.Igd.Searches.Select(s => s.From.Address.ToString()).Distinct().OrderBy(x => x).ToList();
                Assert.Equal(new[] { "10.0.0.5", "192.168.1.50" }, sources);
            }
        }

        [Fact]
        public async Task A_broken_interface_is_reported_but_the_other_works()
        {
            using (var lab = new NetLab())
            {
                lab.Info.SetInterfaces(FakeNetworkInfo.Eth("eth0", "10.0.0.5", "10.0.0.1"), FakeNetworkInfo.Eth("wlan0", "192.168.1.50", "192.168.1.1"));
                lab.Net.FailOpen = ip => ip.ToString() == "10.0.0.5";
                var res = await lab.Ssdp().DiscoverAsync(NetLab.FastSsdp(), CancellationToken.None);
                Assert.True(res.AnyIgd);
                Assert.Equal(1, res.SocketsOpened);
                Assert.Single(res.Errors);
                Assert.Contains("10.0.0.5", res.Errors[0]);
            }
        }

        [Fact]
        public async Task Without_interface_information_one_wildcard_socket_is_used()
        {
            using (var lab = new NetLab())
            {
                lab.Info.SetInterfaces();
                var res = await lab.Ssdp().DiscoverAsync(NetLab.FastSsdp(), CancellationToken.None);
                Assert.True(res.AnyIgd);
                Assert.Equal(1, res.SocketsOpened);
                Assert.Null(res.Responses.First().LocalAddress);
            }
        }

        [Fact]
        public async Task Hostile_responders_are_ignored_and_the_real_router_still_wins()
        {
            using (var lab = new NetLab())
            {
                var evil = new IPEndPoint(IPAddress.Parse("192.168.1.66"), 1900);
                Action<FakePacket> handler = p =>
                {
                    Func<string, byte[]> m = s => Encoding.ASCII.GetBytes(s);
                    lab.Net.Deliver(evil, p.From, m("HTTP/1.1 200 OK\r\nLOCATION: http://8.8.8.8/root.xml\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n"));
                    lab.Net.Deliver(evil, p.From, m("HTTP/1.1 200 OK\r\nLOCATION: http://127.0.0.1:6878/ace/getstream\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n"));
                    lab.Net.Deliver(evil, p.From, m("HTTP/1.1 200 OK\r\nLOCATION: http://evil.example/root.xml\r\nST: upnp:rootdevice\r\n\r\n"));
                    lab.Net.Deliver(evil, p.From, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
                    lab.Net.Deliver(evil, p.From, m("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.66/" + new string('a', 4000) + "\r\n\r\n"));
                };
                lab.Net.Register(FakeUdpNetwork.SsdpGroup, handler);
                lab.Net.Register(evil, handler);
                // Without loopback allowed (production) nothing happens: neither the hostile ones nor the test router, whose HTTP is on 127.0.0.1.
                var res = await lab.Ssdp().DiscoverAsync(new SsdpOptions { Window = TimeSpan.FromMilliseconds(700), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), AllowLoopbackLocation = false, StopAfterFirstIgd = false }, CancellationToken.None);
                Assert.Empty(res.Responses);
                var res2 = await lab.Ssdp().DiscoverAsync(new SsdpOptions { Window = TimeSpan.FromMilliseconds(700), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), AllowLoopbackLocation = true, StopAfterFirstIgd = false }, CancellationToken.None);
                Assert.Contains(res2.Responses, r => r.IsIgd && r.Location.AbsoluteUri == lab.Igd.DescriptionUrl);
                Assert.DoesNotContain(res2.Responses, r => r.Location.Host == "8.8.8.8" || r.Location.Host == "192.168.1.66");       // public, or with a 4000-character path
            }
        }

        [Fact]
        public async Task Lost_datagrams_are_covered_by_the_retries()
        {
            using (var lab = new NetLab())
            {
                int seen = 0;
                // the whole first round is lost (3 STs x multicast and unicast = 6 datagrams to the router)
                lab.Net.DropFilter = p => (p.To.Address.Equals(FakeUdpNetwork.SsdpGroup.Address) || p.To.Address.Equals(lab.Igd.Endpoint.Address)) && Interlocked.Increment(ref seen) <= 6;
                var res = await lab.Ssdp().DiscoverAsync(NetLab.FastSsdp(), CancellationToken.None);
                Assert.True(res.AnyIgd);
                Assert.True(res.FirstResponseAfter.Value >= TimeSpan.FromMilliseconds(80));       // it arrived in the second round
            }
        }

        [Fact]
        public async Task A_router_that_only_answers_rootdevice_is_still_found_but_not_flagged_as_igd()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.SsdpTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upnp:rootdevice" };
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromMilliseconds(500);
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.False(res.AnyIgd);
                var r = Assert.Single(res.Responses);
                Assert.Equal("upnp:rootdevice", r.St);
                Assert.Equal(lab.Igd.DescriptionUrl, r.Location.AbsoluteUri);
            }
        }

        [Fact]
        public async Task A_router_that_advertises_a_public_location_is_ignored()
        {
            using (var lab = new NetLab())
            {
                lab.Igd.LocationOverride = "http://203.0.113.9:80/rootDesc.xml";
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromMilliseconds(500);
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.Empty(res.Responses);
            }
        }

        [Fact]
        public async Task Response_floods_are_capped()
        {
            using (var lab = new NetLab(null))
            {
                var flooder = new IPEndPoint(IPAddress.Parse("192.168.1.77"), 1900);
                lab.Net.Register(FakeUdpNetwork.SsdpGroup, p =>
                {
                    for (int i = 0; i < 300; i++)
                        lab.Net.Deliver(flooder, p.From, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.77:" + (1000 + i) + "/d.xml\r\nST: upnp:rootdevice\r\n\r\n"));
                });
                var o = NetLab.FastSsdp(); o.Window = TimeSpan.FromMilliseconds(500); o.MaxResponses = 10;
                var res = await lab.Ssdp().DiscoverAsync(o, CancellationToken.None);
                Assert.Equal(10, res.Responses.Count);
            }
        }

        [Fact]
        public async Task Ranking_prefers_the_default_gateway_then_igd2_then_igd1_then_rootdevice()
        {
            var r = new SsdpResult();
            Func<string, string, TimeSpan, SsdpResponse> mk = (host, st, t) => new SsdpResponse { Location = new Uri("http://" + host + "/d.xml"), St = st, ReceivedAfter = t };
            r.Responses.Add(mk("192.168.1.9", "upnp:rootdevice", TimeSpan.FromMilliseconds(5)));
            r.Responses.Add(mk("192.168.1.8", "urn:schemas-upnp-org:device:InternetGatewayDevice:1", TimeSpan.FromMilliseconds(6)));
            r.Responses.Add(mk("192.168.1.7", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", TimeSpan.FromMilliseconds(7)));
            r.Responses.Add(mk("192.168.1.1", "upnp:rootdevice", TimeSpan.FromMilliseconds(8)));
            r.Responses.Add(mk("192.168.1.1", "urn:schemas-upnp-org:device:InternetGatewayDevice:1", TimeSpan.FromMilliseconds(9)));
            var ranked = r.Ranked(new[] { IPAddress.Parse("192.168.1.1") });
            Assert.Equal(new[] { "192.168.1.1", "192.168.1.7", "192.168.1.8", "192.168.1.9" }, ranked.Select(x => x.Location.Host).ToArray());
            await Task.CompletedTask;
        }
    }
}
