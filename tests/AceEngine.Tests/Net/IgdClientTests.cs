using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using Xunit;

namespace AceEngine.Tests.NetTests
{
    public class IgdDescriptionTests
    {
        private static IgdClient Client(double timeoutSeconds = 3) { return new IgdClient(new IgdOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }); }

        [Theory]
        [InlineData(IgdVariant.IpV1, FakeIgdRouter.IpConnV1, "/ctl/IPConn")]
        [InlineData(IgdVariant.IpV2, FakeIgdRouter.IpConnV2, "/ctl/IPConn")]
        [InlineData(IgdVariant.PppNested, FakeIgdRouter.PppConnV1, "/ctl/PPPConn")]
        [InlineData(IgdVariant.IpV1UrlBase, FakeIgdRouter.IpConnV1, "/ctl/IPConn")]
        public async Task Finds_the_wan_service_in_every_variant_even_when_nested(IgdVariant variant, string serviceType, string path)
        {
            using (var lab = new NetLab(variant))
            using (var c = Client())
            {
                var d = await c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None);
                Assert.True(d.IsInternetGateway);
                Assert.Equal("Test router", d.FriendlyName);
                Assert.Equal("Fake Inc", d.Manufacturer);
                Assert.Equal("FR-1", d.ModelName);
                var svc = Assert.Single(d.Services);           // Layer3Forwarding and WANCommonInterfaceConfig do not count
                Assert.Equal(serviceType, svc.ServiceType);
                Assert.Equal("http://127.0.0.1:" + lab.Igd.Http.Port + path, svc.ControlUrl.AbsoluteUri);
            }
        }

        private static IgdDescription Parse(string xml, string location = "http://192.168.1.1:5000/rootDesc.xml")
        {
            var b = Encoding.UTF8.GetBytes(xml);
            return IgdDescription.Parse(b, b.Length, new Uri(location));
        }

        private const string Wrap = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\">{0}<device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>{1}</device></root>";

        private static string Svc(string type, string url) { return "<service><serviceType>" + type + "</serviceType><serviceId>x</serviceId><controlURL>" + url + "</controlURL></service>"; }

        [Fact]
        public void Orders_services_ip2_then_ip1_then_ppp_wherever_they_are()
        {
            string xml = string.Format(Wrap, "",
                "<serviceList>" + Svc(FakeIgdRouter.PppConnV1, "/ppp") + "</serviceList>"
                + "<deviceList><device><serviceList>" + Svc(FakeIgdRouter.IpConnV1, "/ip1") + "</serviceList>"
                + "<deviceList><device><serviceList>" + Svc(FakeIgdRouter.IpConnV2, "/ip2") + "</serviceList></device></deviceList></device></deviceList>");
            var d = Parse(xml);
            Assert.Equal(new[] { "/ip2", "/ip1", "/ppp" }, d.Services.Select(s => s.ControlUrl.AbsolutePath).ToArray());
            Assert.Equal(new[] { 0, 1, 2 }, d.Services.Select(s => s.Rank).ToArray());
        }

        [Fact]
        public void UrlBase_resolves_relative_paths_and_location_is_the_fallback()
        {
            var withBase = Parse(string.Format(Wrap, "<URLBase>http://192.168.1.1:49152/base/</URLBase>", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "ctl/ip") + "</serviceList>"));
            Assert.Equal("http://192.168.1.1:49152/base/ctl/ip", withBase.Services[0].ControlUrl.AbsoluteUri);
            var noBase = Parse(string.Format(Wrap, "", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "ctl/ip") + "</serviceList>"), "http://192.168.1.1:5000/a/rootDesc.xml");
            Assert.Equal("http://192.168.1.1:5000/a/ctl/ip", noBase.Services[0].ControlUrl.AbsoluteUri);
            var abs = Parse(string.Format(Wrap, "", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "/ctl/ip") + "</serviceList>"), "http://192.168.1.1:5000/a/rootDesc.xml");
            Assert.Equal("http://192.168.1.1:5000/ctl/ip", abs.Services[0].ControlUrl.AbsoluteUri);
        }

        [Fact]
        public void Absolute_control_url_on_the_same_host_is_accepted_but_other_hosts_are_not()
        {
            var ok = Parse(string.Format(Wrap, "", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "http://192.168.1.1:8080/ctl") + "</serviceList>"));
            Assert.Equal("http://192.168.1.1:8080/ctl", ok.Services[0].ControlUrl.AbsoluteUri);
            var evil = Parse(string.Format(Wrap, "", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "http://203.0.113.9/ctl") + Svc(FakeIgdRouter.IpConnV2, "http://evil.example/ctl") + Svc(FakeIgdRouter.PppConnV1, "https://192.168.1.1/ctl") + "</serviceList>"));
            Assert.Empty(evil.Services);
        }

        [Fact]
        public void Foreign_URLBase_is_ignored()
        {
            var d = Parse(string.Format(Wrap, "<URLBase>http://203.0.113.9:80/</URLBase>", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "/ctl") + "</serviceList>"));
            Assert.Equal("http://192.168.1.1:5000/ctl", d.Services[0].ControlUrl.AbsoluteUri);
        }

        [Fact]
        public void Unknown_service_types_and_missing_fields_are_skipped()
        {
            var d = Parse(string.Format(Wrap, "", "<serviceList>" + Svc("urn:schemas-upnp-org:service:WANIPConnection:3", "/c3") + Svc("urn:other:service:WANIPConnection:1", "/c") + "<service><serviceType>" + FakeIgdRouter.IpConnV1 + "</serviceType></service>" + Svc("  " + FakeIgdRouter.IpConnV1 + " ", "/ok") + "</serviceList>"));
            Assert.Equal("/ok", Assert.Single(d.Services).ControlUrl.AbsolutePath);
        }

        [Theory]
        [InlineData("<!DOCTYPE root [<!ENTITY a \"x\">]><root>&a;</root>")]
        [InlineData("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]><root><device>&lol3;</device></root>")]
        [InlineData("<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><root><device><deviceType>&xxe;</deviceType></device></root>")]
        [InlineData("<?xml version=\"1.0\"?><!DOCTYPE root SYSTEM \"http://203.0.113.9/evil.dtd\"><root><device/></root>")]
        [InlineData("esto no es xml")]
        [InlineData("")]
        [InlineData("<html><body>login</body></html>")]
        [InlineData("<root><device></root>")]
        [InlineData("<root/><root/>")]
        [InlineData("<root></root>")]
        public void Malicious_or_invalid_descriptions_are_rejected_with_a_protocol_error(string xml)
        {
            var b = Encoding.UTF8.GetBytes(xml);
            var ex = Assert.Throws<UpnpException>(() => IgdDescription.Parse(b, b.Length, new Uri("http://192.168.1.1/d.xml")));
            Assert.Equal(UpnpErrorKind.Protocol, ex.Kind);
        }

        [Fact]
        public void Deep_nesting_and_element_floods_are_rejected()
        {
            var sb = new StringBuilder("<root>");
            for (int i = 0; i < 400; i++) sb.Append("<device>");
            var deep = Encoding.UTF8.GetBytes(sb.ToString());
            Assert.Throws<UpnpException>(() => IgdDescription.Parse(deep, deep.Length, new Uri("http://192.168.1.1/d.xml")));

            var flood = new StringBuilder("<root><device><serviceList>");
            for (int i = 0; i < 20000; i++) flood.Append("<service/>");
            flood.Append("</serviceList></device></root>");
            var fb = Encoding.UTF8.GetBytes(flood.ToString());
            Assert.Throws<UpnpException>(() => IgdDescription.Parse(fb, fb.Length, new Uri("http://192.168.1.1/d.xml")));
        }

        [Fact]
        public void Huge_text_nodes_are_truncated_not_stored()
        {
            var xml = "<root><device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><friendlyName>" + new string('A', 100000) + "</friendlyName></device></root>";
            var d = Parse(xml);
            Assert.True(d.FriendlyName.Length <= 80);
        }

        [Fact]
        public void Leading_whitespace_before_the_xml_declaration_is_tolerated()
        {
            var d = Parse("\r\n  " + string.Format(Wrap, "", "<serviceList>" + Svc(FakeIgdRouter.IpConnV1, "/c") + "</serviceList>"));
            Assert.Single(d.Services);
        }

        // ---------- description HTTP ----------

        [Fact]
        public async Task Oversized_descriptions_are_cut_off()
        {
            using (var lab = new NetLab())
            using (var c = new IgdClient(new IgdOptions { Timeout = TimeSpan.FromSeconds(3), MaxDescriptionBytes = 2000 }))
            {
                // with a declared Content-Length of 300 KB
                lab.Igd.DescriptionResponder = req => FakeHttpResponse.Xml("<root>" + new string('x', 300000) + "</root>");
                var ex = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None));
                Assert.Equal(UpnpErrorKind.TooLarge, ex.Kind);

                // infinite body without Content-Length: it is cut when the limit is reached and the server sees the connection closed
                int written = 0;
                lab.Igd.DescriptionResponder = req => new FakeHttpResponse
                {
                    Custom = async (s, ct) =>
                    {
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                        await s.WriteAsync(head, 0, head.Length, ct);
                        var chunk = new byte[1000];
                        for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)'a';
                        try { while (!ct.IsCancellationRequested) { await s.WriteAsync(chunk, 0, chunk.Length, ct); Interlocked.Add(ref written, chunk.Length); await Task.Delay(1); } } catch (Exception) { }
                    }
                };
                var sw = Stopwatch.StartNew();
                var ex2 = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None));
                Assert.Equal(UpnpErrorKind.TooLarge, ex2.Kind);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
            }
        }

        [Fact]
        public async Task Slow_drip_and_silent_servers_hit_the_deadline()
        {
            using (var lab = new NetLab())
            using (var c = new IgdClient(new IgdOptions { Timeout = TimeSpan.FromMilliseconds(400) }))
            {
                lab.Igd.DescriptionResponder = req => new FakeHttpResponse { Hang = true };
                var sw = Stopwatch.StartNew();
                var ex = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Timeout, ex.Kind);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 300, 2500);

                lab.Igd.DescriptionResponder = req => new FakeHttpResponse
                {
                    Custom = async (s, ct) =>
                    {
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 100000\r\n\r\n");
                        await s.WriteAsync(head, 0, head.Length, ct);
                        try { for (int i = 0; i < 1000; i++) { await s.WriteAsync(new byte[] { (byte)'<' }, 0, 1, ct); await Task.Delay(100, ct); } } catch (Exception) { }
                    }
                };
                var ex2 = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Timeout, ex2.Kind);
            }
        }

        [Fact]
        public async Task Http_errors_redirects_resets_and_refused_connections_become_upnp_errors()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var url = new Uri(lab.Igd.DescriptionUrl);
                lab.Igd.DescriptionResponder = req => new FakeHttpResponse { Status = 404, Reason = "Not Found" };
                var e404 = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(url, CancellationToken.None));
                Assert.Equal(404, e404.HttpStatus);

                var redirect = new FakeHttpResponse { Status = 302, Reason = "Found" };
                redirect.Headers["Location"] = "http://203.0.113.9/steal.xml";
                lab.Igd.DescriptionResponder = req => redirect;
                var e302 = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(url, CancellationToken.None));
                Assert.Equal(302, e302.HttpStatus);              // the redirect is not followed

                lab.Igd.DescriptionResponder = req => new FakeHttpResponse { CloseSilently = true };
                var eReset = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(url, CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Transport, eReset.Kind);

                var port = lab.Igd.Http.Port;
                lab.Igd.Http.Dispose();
                var eRefused = await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri("http://127.0.0.1:" + port + "/x"), CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Transport, eRefused.Kind);
            }
        }

        [Fact]
        public async Task Non_http_locations_are_rejected_without_connecting()
        {
            using (var c = Client())
            {
                await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri("https://192.168.1.1/d.xml"), CancellationToken.None));
                await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(new Uri("file:///etc/passwd"), CancellationToken.None));
                await Assert.ThrowsAsync<UpnpException>(() => c.FetchDescriptionAsync(null, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Cancelling_a_hanging_request_returns_promptly()
        {
            using (var lab = new NetLab())
            using (var c = Client(30))
            using (var cts = new CancellationTokenSource())
            {
                lab.Igd.DescriptionResponder = req => new FakeHttpResponse { Hang = true };
                var t = c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), cts.Token);
                await Task.Delay(150);
                var sw = Stopwatch.StartNew();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            }
        }
    }

    public class IgdSoapTests
    {
        private static IgdClient Client(double timeoutSeconds = 3) { return new IgdClient(new IgdOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }); }

        private static async Task<IgdService> Service(NetLab lab, IgdClient c)
        {
            var d = await c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None);
            return d.Services[0];
        }

        [Theory]
        [InlineData(IgdVariant.IpV1)]
        [InlineData(IgdVariant.IpV2)]
        [InlineData(IgdVariant.PppNested)]
        public async Task GetExternalIPAddress_uses_the_exact_wire_format(IgdVariant variant)
        {
            using (var lab = new NetLab(variant))
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                var ip = await c.GetExternalIPAddressAsync(svc, CancellationToken.None);
                Assert.Equal(IPAddress.Parse("203.0.113.7"), ip);
                var call = Assert.Single(lab.Igd.Calls);
                Assert.Equal("GetExternalIPAddress", call.Action);
                Assert.Equal("\"" + lab.Igd.ServiceType + "#GetExternalIPAddress\"", call.SoapActionHeader);        // with quotes
                Assert.Equal("text/xml; charset=\"utf-8\"", call.ContentType);
                Assert.Contains("UPnP/1.1", call.UserAgent);
                Assert.Equal(lab.Igd.ControlPath, call.Path);
                var req = lab.Igd.Http.Requests.Last();
                Assert.Equal("POST", req.Method);
                Assert.Equal("close", req.Header("Connection"));
                Assert.StartsWith("<?xml", req.BodyText);
                Assert.Contains("xmlns:u=\"" + lab.Igd.ServiceType + "\"", req.BodyText);
                Assert.Contains("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"", req.BodyText);
            }
        }

        [Fact]
        public async Task AddPortMapping_sends_the_arguments_in_spec_order_and_the_router_stores_them()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                await c.AddPortMappingAsync(svc, "TCP", 8621, IPAddress.Parse("192.168.1.50"), 8621, "P!", 3600, CancellationToken.None);
                var call = lab.Igd.CallsOf("AddPortMapping").Single();
                Assert.Equal(new[] { "NewRemoteHost", "NewExternalPort", "NewProtocol", "NewInternalPort", "NewInternalClient", "NewEnabled", "NewPortMappingDescription", "NewLeaseDuration" }, call.Args.Select(a => a.Key).ToArray());
                Assert.Equal(new[] { "", "8621", "TCP", "8621", "192.168.1.50", "1", "P!", "3600" }, call.Args.Select(a => a.Value).ToArray());
                var m = lab.Igd.Get("TCP", 8621);
                Assert.NotNull(m);
                Assert.Equal("192.168.1.50", m.InternalClient);
                Assert.Equal(3600, m.Lease);

                var e = await c.GetSpecificPortMappingEntryAsync(svc, "TCP", 8621, CancellationToken.None);
                Assert.Equal(8621, e.InternalPort);
                Assert.Equal("192.168.1.50", e.InternalClient);
                Assert.True(e.Enabled);
                Assert.Equal("P!", e.Description);
                Assert.InRange(e.LeaseDuration, 1, 3600);

                await c.AddPortMappingAsync(svc, "UDP", 8621, IPAddress.Parse("192.168.1.50"), 8621, "P!", 0, CancellationToken.None);
                Assert.True(lab.Igd.Has("UDP", 8621));
                Assert.True(await c.DeletePortMappingAsync(svc, "TCP", 8621, CancellationToken.None));
                Assert.False(lab.Igd.Has("TCP", 8621));
                Assert.False(await c.DeletePortMappingAsync(svc, "TCP", 8621, CancellationToken.None));       // 714: it no longer existed
                Assert.Null(await c.GetSpecificPortMappingEntryAsync(svc, "TCP", 8621, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Special_characters_in_arguments_are_escaped()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                await c.AddPortMappingAsync(svc, "TCP", 9000, IPAddress.Parse("192.168.1.50"), 9000, "a&b<c>\"d'</NewLeaseDuration><x>", 60, CancellationToken.None);
                Assert.Equal("a&b<c>\"d'</NewLeaseDuration><x>", lab.Igd.Get("TCP", 9000).Description);
                Assert.Equal(60, lab.Igd.Get("TCP", 9000).Lease);
            }
        }

        [Fact]
        public async Task GetGenericPortMappingEntry_enumerates_until_the_index_is_invalid()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.Preload("TCP", 1000, "192.168.1.9", 1000);
                lab.Igd.Preload("UDP", 2000, "192.168.1.10", 2001, 60, "otro2");
                var e0 = await c.GetGenericPortMappingEntryAsync(svc, 0, CancellationToken.None);
                var e1 = await c.GetGenericPortMappingEntryAsync(svc, 1, CancellationToken.None);
                var e2 = await c.GetGenericPortMappingEntryAsync(svc, 2, CancellationToken.None);
                Assert.Equal(1000, e0.ExternalPort);
                Assert.Equal("TCP", e0.Protocol);
                Assert.Equal(2000, e1.ExternalPort);
                Assert.Equal(2001, e1.InternalPort);
                Assert.Equal("otro2", e1.Description);
                Assert.Null(e2);
            }
        }

        [Fact]
        public async Task Upnp_errors_carry_their_code_and_unavailable_codes_are_flagged()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.ConflictPorts.Add(8621);
                var e718 = await Assert.ThrowsAsync<UpnpException>(() => c.AddPortMappingAsync(svc, "TCP", 8621, IPAddress.Parse("192.168.1.50"), 8621, "P!", 60, CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Soap, e718.Kind);
                Assert.Equal(718, e718.ErrorCode);
                Assert.Equal(500, e718.HttpStatus);
                Assert.False(e718.IsUnavailable);
                Assert.Contains("conflict", e718.Message);

                foreach (int code in new[] { 501, 402, 606 })
                {
                    lab.Igd.FailAllCode = code;
                    var e = await Assert.ThrowsAsync<UpnpException>(() => c.GetExternalIPAddressAsync(svc, CancellationToken.None));
                    Assert.Equal(code, e.ErrorCode);
                    Assert.True(e.IsUnavailable);
                }
                lab.Igd.FailAllCode = 725;
                var e725 = await Assert.ThrowsAsync<UpnpException>(() => c.AddPortMappingAsync(svc, "TCP", 8000, IPAddress.Parse("192.168.1.50"), 8000, "P!", 60, CancellationToken.None));
                Assert.Equal(725, e725.ErrorCode);
            }
        }

        [Fact]
        public async Task Argument_validation()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.AddPortMappingAsync(svc, "TCP", 0, IPAddress.Loopback, 80, "x", 1, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.AddPortMappingAsync(svc, "TCP", 80, IPAddress.Loopback, 70000, "x", 1, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentException>(() => c.AddPortMappingAsync(svc, "SCTP", 80, IPAddress.Loopback, 80, "x", 1, CancellationToken.None));
                await Assert.ThrowsAsync<UpnpException>(() => c.GetExternalIPAddressAsync(null, CancellationToken.None));
                Assert.Empty(lab.Igd.Calls);
            }
        }

        private static async Task<UpnpException> Hostile(NetLab lab, IgdClient c, IgdService svc, FakeHttpResponse reply)
        {
            var original = lab.Igd.Http.Handler;
            lab.Igd.Http.Handler = req => req.Method == "POST" ? reply : original(req);
            return await Assert.ThrowsAsync<UpnpException>(() => c.GetExternalIPAddressAsync(svc, CancellationToken.None));
        }

        [Fact]
        public async Task Hostile_soap_replies_become_protocol_errors()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                var bomb = "<?xml version=\"1.0\"?><!DOCTYPE s [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><s:Envelope xmlns:s=\"x\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"y\"><NewExternalIPAddress>&b;</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
                var e1 = await Hostile(lab, c, svc, FakeHttpResponse.Xml(bomb));
                Assert.Equal(UpnpErrorKind.Protocol, e1.Kind);

                var e2 = await Hostile(lab, c, svc, FakeHttpResponse.Xml("<s:Envelope xmlns:s=\"x\"><s:Body/></s:Envelope>"));
                Assert.Equal(UpnpErrorKind.Protocol, e2.Kind);

                var e3 = await Hostile(lab, c, svc, new FakeHttpResponse());
                Assert.Equal(UpnpErrorKind.Protocol, e3.Kind);

                var e4 = await Hostile(lab, c, svc, FakeHttpResponse.Xml("<Envelope><Body><GetExternalIPAddressResponse><NewExternalIPAddress>no-es-una-ip</NewExternalIPAddress></GetExternalIPAddressResponse></Body></Envelope>"));
                Assert.Equal(UpnpErrorKind.Protocol, e4.Kind);

                var e5 = await Hostile(lab, c, svc, FakeHttpResponse.Xml("<Envelope><Body><GetExternalIPAddressResponse><NewExternalIPAddress>::1</NewExternalIPAddress></GetExternalIPAddressResponse></Body></Envelope>"));
                Assert.Equal(UpnpErrorKind.Protocol, e5.Kind);

                var e6 = await Hostile(lab, c, svc, FakeHttpResponse.Xml("garbage not xml", 200));
                Assert.Equal(UpnpErrorKind.Protocol, e6.Kind);

                var e7 = await Hostile(lab, c, svc, new FakeHttpResponse { Status = 500, Reason = "Internal Server Error", Body = Encoding.ASCII.GetBytes("<html>boom</html>") });
                Assert.Equal(UpnpErrorKind.Soap, e7.Kind);
                Assert.Equal(500, e7.HttpStatus);
                Assert.Equal(0, e7.ErrorCode);

                var e8 = await Hostile(lab, c, svc, new FakeHttpResponse { Status = 404, Reason = "Not Found" });
                Assert.Equal(404, e8.HttpStatus);
                Assert.True(e8.IsUnavailable);

                var e9 = await Hostile(lab, c, svc, FakeHttpResponse.Xml("<x>" + new string('a', 200000) + "</x>"));       // more than 64 KiB
                Assert.Equal(UpnpErrorKind.TooLarge, e9.Kind);

                var e10 = await Hostile(lab, c, svc, new FakeHttpResponse { CloseSilently = true });
                Assert.Equal(UpnpErrorKind.Transport, e10.Kind);
            }
        }

        [Fact]
        public async Task A_fault_inside_a_200_reply_is_still_an_error()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                var e = await Hostile(lab, c, svc, FakeHttpResponse.Xml("<s:Envelope xmlns:s=\"x\"><s:Body><s:Fault><detail><UPnPError><errorCode>718</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>", 200));
                Assert.Equal(718, e.ErrorCode);
            }
        }

        [Fact]
        public async Task Soap_calls_have_a_deadline_and_can_be_cancelled()
        {
            using (var lab = new NetLab())
            using (var c = Client(0.4))
            {
                var svc = await Service(lab, c);
                lab.Igd.HangSoap = true;
                var sw = Stopwatch.StartNew();
                var e = await Assert.ThrowsAsync<UpnpException>(() => c.GetExternalIPAddressAsync(svc, CancellationToken.None));
                Assert.Equal(UpnpErrorKind.Timeout, e.Kind);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 300, 2500);
            }
            using (var lab = new NetLab())
            using (var c = Client(30))
            using (var cts = new CancellationTokenSource())
            {
                var svc = await Service(lab, c);
                lab.Igd.HangSoap = true;
                var t = c.GetExternalIPAddressAsync(svc, cts.Token);
                await Task.Delay(150);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
            }
        }
    }

    public class IgdMapperTests
    {
        private static readonly IPAddress Me = IPAddress.Parse("192.168.1.50");
        private static IgdClient Client() { return new IgdClient(new IgdOptions { Timeout = TimeSpan.FromSeconds(3) }); }

        private static async Task<IgdService> Service(NetLab lab, IgdClient c)
        {
            return (await c.FetchDescriptionAsync(new Uri(lab.Igd.DescriptionUrl), CancellationToken.None)).Services[0];
        }

        private static Task<IgdMapResult> Map(IgdClient c, IgdService svc, int preferred = 8621, int lease = 3600, bool udp = true, int attempts = 6, List<string> log = null, CancellationToken ct = default(CancellationToken), int internalPort = 8621)
        {
            return IgdMapper.MapAsync(c, svc, Me, internalPort, preferred, "P!", lease, udp, attempts, new Random(7), log == null ? (Action<string>)null : log.Add, ct);
        }

        [Fact]
        public void Candidates_start_with_the_preferred_port_then_neighbours_then_random_high_ports()
        {
            var l = IgdMapper.Candidates(8621, 6, new Random(1));
            Assert.Equal(6, l.Count);
            Assert.Equal(new[] { 8621, 8622, 8623, 8624 }, l.Take(4).ToArray());
            Assert.Equal(l.Count, l.Distinct().Count());
            Assert.All(l.Skip(4), p => Assert.InRange(p, 20000, 59999));
            Assert.Equal(new[] { 65535 }, IgdMapper.Candidates(65535, 1, new Random(1)).ToArray());
            Assert.True(IgdMapper.Candidates(65535, 3, new Random(1)).All(p => p <= 65535));
        }

        [Fact]
        public async Task Maps_tcp_and_udp_on_the_preferred_port_with_a_limited_lease()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                var r = await Map(c, svc);
                Assert.Equal(8621, r.ExternalPort);
                Assert.Equal(3600, r.LeaseSeconds);
                Assert.True(r.UdpMapped);
                Assert.False(r.Unverified);
                Assert.True(lab.Igd.Has("TCP", 8621));
                Assert.True(lab.Igd.Has("UDP", 8621));
                Assert.Equal("192.168.1.50", lab.Igd.Get("TCP", 8621).InternalClient);      // the local IP used to reach the router
                Assert.Equal("P!", lab.Igd.Get("TCP", 8621).Description);
            }
        }

        [Fact]
        public async Task Conflict_718_moves_to_the_next_free_port()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.ConflictPorts.Add(8621);
                var log = new List<string>();
                var r = await Map(c, svc, log: log);
                Assert.Equal(8622, r.ExternalPort);
                Assert.True(lab.Igd.Has("TCP", 8622) && lab.Igd.Has("UDP", 8622));
                Assert.False(lab.Igd.Has("TCP", 8621));
                Assert.Contains(log, l => l.Contains("8621") && l.Contains("718"));
            }
        }

        [Fact]
        public async Task A_mapping_owned_by_another_lan_host_is_a_conflict_but_our_own_is_refreshed()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.Preload("TCP", 8621, "192.168.1.99", 8621);
                var r = await Map(c, svc);
                Assert.Equal(8622, r.ExternalPort);
                Assert.Equal("192.168.1.99", lab.Igd.Get("TCP", 8621).InternalClient);          // another machine's is not overwritten

                lab.Igd.Preload("TCP", 9000, "192.168.1.50", 9000);
                var again = await Map(c, svc, preferred: 9000, internalPort: 9000);
                Assert.Equal(9000, again.ExternalPort);
            }
        }

        [Fact]
        public async Task When_the_first_ports_are_taken_it_falls_back_to_a_high_port_and_gives_up_if_everything_conflicts()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                foreach (int p in new[] { 8621, 8622, 8623, 8624 }) lab.Igd.ConflictPorts.Add(p);
                var r = await Map(c, svc);
                Assert.InRange(r.ExternalPort, 20000, 59999);

                int before = lab.Igd.CallsOf("AddPortMapping").Count;
                lab.Igd.ConflictAll = true;
                var ex = await Assert.ThrowsAsync<UpnpException>(() => Map(c, svc, attempts: 5));
                Assert.Equal(718, ex.ErrorCode);
                Assert.Equal(5, lab.Igd.CallsOf("AddPortMapping").Count - before);          // 5 attempts and no more
            }
        }

        [Fact]
        public async Task OnlyPermanentLeases_725_is_retried_with_lease_zero_and_remembered()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.OnlyPermanentLeases = true;
                var r = await Map(c, svc);
                Assert.Equal(0, r.LeaseSeconds);
                Assert.Equal(8621, r.ExternalPort);
                Assert.Equal(0, lab.Igd.Get("TCP", 8621).Lease);
                Assert.True(r.UdpMapped);
                var adds = lab.Igd.CallsOf("AddPortMapping");
                Assert.Equal("3600", adds[0].Arg("NewLeaseDuration"));          // first with a lease
                Assert.Equal("0", adds[1].Arg("NewLeaseDuration"));             // then permanent
                Assert.Equal("0", adds[2].Arg("NewLeaseDuration"));             // and the UDP one already goes with 0, without repeating the error
                Assert.Equal(3, adds.Count);
            }
        }

        [Fact]
        public async Task Invalid_args_with_a_lease_get_one_retry_with_lease_zero_then_give_up()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.FailAddCode = 402;
                var ex = await Assert.ThrowsAsync<UpnpException>(() => Map(c, svc));
                Assert.Equal(402, ex.ErrorCode);
                Assert.True(ex.IsUnavailable);
                Assert.Equal(2, lab.Igd.CallsOf("AddPortMapping").Count);
                Assert.Empty(lab.Igd.Mappings);
            }
        }

        [Theory]
        [InlineData(606)]
        [InlineData(501)]
        [InlineData(728)]
        public async Task Unavailable_style_errors_stop_immediately_without_trying_other_ports(int code)
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.FailAddCode = code;
                var ex = await Assert.ThrowsAsync<UpnpException>(() => Map(c, svc, lease: 0));
                Assert.Equal(code, ex.ErrorCode);
                Assert.Single(lab.Igd.CallsOf("AddPortMapping"));
            }
        }

        [Fact]
        public async Task SamePortValuesRequired_724_falls_back_to_external_equals_internal()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.RequireSamePort = true;
                var r = await Map(c, svc, preferred: 9000, internalPort: 8621);
                Assert.Equal(8621, r.ExternalPort);
            }
        }

        [Fact]
        public async Task A_router_that_hides_its_mappings_is_accepted_but_flagged_unverified()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.GetSpecificBroken = true;
                var log = new List<string>();
                var r = await Map(c, svc, log: log);
                Assert.True(r.Unverified);
                Assert.Equal(8621, r.ExternalPort);
                Assert.Contains(log, l => l.Contains("without verification"));
            }
        }

        [Fact]
        public async Task A_router_that_acknowledges_but_stores_nothing_is_flagged_unverified()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                lab.Igd.SwallowAdd = true;
                var r = await Map(c, svc);
                Assert.True(r.Unverified);
                Assert.Empty(lab.Igd.Mappings);
            }
        }

        [Fact]
        public async Task A_mapping_the_router_reports_for_another_client_is_not_ours()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                // the router "accepts" but stores it for another client (broken router): the next port is tried
                var original = lab.Igd.Http.Handler;
                lab.Igd.Http.Handler = req =>
                {
                    var resp = original(req);
                    if (req.Method == "POST" && req.BodyText.Contains("GetSpecificPortMappingEntry") && req.BodyText.Contains(">8621<"))
                        return FakeHttpResponse.Xml("<Envelope><Body><GetSpecificPortMappingEntryResponse><NewInternalPort>8621</NewInternalPort><NewInternalClient>192.168.1.77</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>x</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration></GetSpecificPortMappingEntryResponse></Body></Envelope>");
                    return resp;
                };
                var r = await Map(c, svc);
                Assert.Equal(8622, r.ExternalPort);
            }
        }

        [Fact]
        public async Task Cancelling_after_the_mapping_was_created_removes_it()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            using (var cts = new CancellationTokenSource())
            {
                var svc = await Service(lab, c);
                var original = lab.Igd.Http.Handler;
                lab.Igd.Http.Handler = req =>
                {
                    if (req.Method == "POST" && req.BodyText.Contains("GetSpecificPortMappingEntry")) { cts.Cancel(); return new FakeHttpResponse { Hang = true }; }
                    return original(req);
                };
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Map(c, svc, ct: cts.Token));
                Assert.True(await Wait.UntilAsync(() => lab.Igd.Mappings.Count == 0, 4000), "the mapping was left open: " + string.Join(",", lab.Igd.Mappings.Select(m => m.Proto + m.ExternalPort)));
            }
        }

        [Fact]
        public async Task Remove_deletes_both_protocols_and_tolerates_missing_ones()
        {
            using (var lab = new NetLab())
            using (var c = Client())
            {
                var svc = await Service(lab, c);
                var r = await Map(c, svc);
                Assert.True(await IgdMapper.RemoveAsync(c, svc, r.ExternalPort, true, null, CancellationToken.None));
                Assert.Empty(lab.Igd.Mappings);
                Assert.True(await IgdMapper.RemoveAsync(c, svc, r.ExternalPort, true, null, CancellationToken.None));        // they were not there anymore: it is still a success
                lab.Igd.FailAllCode = 501;
                Assert.False(await IgdMapper.RemoveAsync(c, svc, 8621, true, null, CancellationToken.None));
            }
        }
    }
}
