using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;
using AceEngine.Net;
using AceEngine.Tests.NetTests;
using Xunit;

namespace AceEngine.Tests.Cast
{
    public class DlnaSoapTests
    {
        private static KeyValuePair<string, string> A(string n, string v) { return new KeyValuePair<string, string>(n, v); }

        [Fact]
        public void TheEnvelopeHasTheExactShapeAndEscapesEveryArgument()
        {
            string xml = SoapClient.BuildEnvelope("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI", new[] { A("InstanceID", "0"), A("CurrentURI", "http://192.168.1.50:8000/c/ab/live.ts?x=1&y=2"), A("CurrentURIMetaData", "<DIDL-Lite a=\"b\">'x'</DIDL-Lite>") });
            Assert.Equal("<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
                + "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID><CurrentURI>http://192.168.1.50:8000/c/ab/live.ts?x=1&amp;y=2</CurrentURI>"
                + "<CurrentURIMetaData>&lt;DIDL-Lite a=&quot;b&quot;&gt;&apos;x&apos;&lt;/DIDL-Lite&gt;</CurrentURIMetaData></u:SetAVTransportURI></s:Body></s:Envelope>", xml);
            var doc = SafeXml.Parse(Encoding.UTF8.GetBytes(xml), Encoding.UTF8.GetByteCount(xml));
            Assert.Equal("<DIDL-Lite a=\"b\">'x'</DIDL-Lite>", doc.Find("CurrentURIMetaData").Text);                 // it round-trips
        }

        [Fact]
        public void CharactersXmlCannotCarryAreDroppedSoTheEnvelopeIsAlwaysValid()
        {
            string nasty = "a\u0000b\u0001c\u0008d\u000be\u001ff\ud800g\udfffh￾i￿jé中😀k";
            string xml = SoapClient.BuildEnvelope("urn:x:service:S:1", "A", new[] { A("V", nasty) });
            var doc = SafeXml.Parse(Encoding.UTF8.GetBytes(xml), Encoding.UTF8.GetByteCount(xml));
            Assert.Equal("abcdefghijé中😀k", doc.Find("V").Text);
        }

        [Fact]
        public void ASuccessfulResponseGivesItsOutArguments()
        {
            string xml = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetTransportInfoResponse xmlns:u=\"urn:x\"><CurrentTransportState>PLAYING</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed></u:GetTransportInfoResponse></s:Body></s:Envelope>";
            var r = SoapClient.ParseResponse("GetTransportInfo", 200, Encoding.UTF8.GetBytes(xml));
            Assert.Equal("PLAYING", r["CurrentTransportState"]);
            Assert.Equal("playing", r["currenttransportstate"].ToLowerInvariant());
            Assert.Equal("OK", r["CurrentTransportStatus"]);
        }

        [Fact]
        public void AFaultCarriesTheUpnpErrorCode()
        {
            string xml = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>714</errorCode><errorDescription>Illegal MIME-Type</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            var ex = Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("SetAVTransportURI", 500, Encoding.UTF8.GetBytes(xml)));
            Assert.Equal(UpnpErrorKind.Soap, ex.Kind);
            Assert.Equal(714, ex.ErrorCode);
            Assert.Equal(500, ex.HttpStatus);
            Assert.Contains("Illegal MIME-Type", ex.Message);
        }

        [Fact]
        public void AFaultInsideAnHttp200IsStillAnError()
        {
            string xml = "<s:Envelope xmlns:s=\"x\"><s:Body><s:Fault><detail><UPnPError><errorCode>402</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            var ex = Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("Play", 200, Encoding.UTF8.GetBytes(xml)));
            Assert.Equal(402, ex.ErrorCode);
        }

        [Theory]
        [InlineData(200, "")]
        [InlineData(200, "not xml at all")]
        [InlineData(200, "<a><b></a>")]
        [InlineData(200, "<s:Envelope xmlns:s=\"x\"><s:Body></s:Body></s:Envelope>")]
        [InlineData(500, "")]
        [InlineData(404, "<html>nope</html>")]
        [InlineData(200, "<!DOCTYPE x [<!ENTITY a \"b\">]><x>&a;</x>")]
        public void BrokenResponsesBecomeUpnpExceptionsNotCrashes(int status, string body)
        {
            Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("Play", status, Encoding.UTF8.GetBytes(body)));
        }

        [Fact]
        public void AnXxeAttemptIsRejected()
        {
            string xml = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><s:Envelope xmlns:s=\"x\"><s:Body><u:PlayResponse xmlns:u=\"y\"><A>&xxe;</A></u:PlayResponse></s:Body></s:Envelope>";
            Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("Play", 200, Encoding.UTF8.GetBytes(xml)));
        }

        [Fact]
        public void ABillionLaughsDocumentIsRejectedAndDeepOrHugeOnesToo()
        {
            var deep = new StringBuilder();
            for (int i = 0; i < 200; i++) deep.Append("<a>");
            Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("Play", 200, Encoding.UTF8.GetBytes("<s:Envelope xmlns:s=\"x\"><s:Body><u:PlayResponse xmlns:u=\"y\">" + deep + "</u:PlayResponse></s:Body></s:Envelope>")));
            var wide = new StringBuilder("<s:Envelope xmlns:s=\"x\"><s:Body><u:PlayResponse xmlns:u=\"y\">");
            for (int i = 0; i < 6000; i++) wide.Append("<e/>");
            wide.Append("</u:PlayResponse></s:Body></s:Envelope>");
            Assert.Throws<UpnpException>(() => SoapClient.ParseResponse("Play", 200, Encoding.UTF8.GetBytes(wide.ToString())));
        }

        [Fact]
        public void RandomBytesNeverCrashTheResponseParser()
        {
            var rnd = new Random(21);
            string good = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetPositionInfoResponse xmlns:u=\"urn:x\"><RelTime>00:00:12</RelTime></u:GetPositionInfoResponse></s:Body></s:Envelope>";
            for (int i = 0; i < 3000; i++)
            {
                byte[] b;
                if (i % 3 == 0) { b = new byte[rnd.Next(0, 300)]; rnd.NextBytes(b); }
                else { b = Encoding.UTF8.GetBytes(good); for (int m = 0; m < 1 + rnd.Next(8); m++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256); }
                try { SoapClient.ParseResponse("GetPositionInfo", i % 2 == 0 ? 200 : 500, b); }
                catch (UpnpException) { }
            }
        }

        [Fact]
        public void IgdEnvelopeIsStillTheSameThroughTheSharedClient()
        {
            string a = IgdClient.BuildEnvelope("urn:schemas-upnp-org:service:WANIPConnection:1", "GetExternalIPAddress", new KeyValuePair<string, string>[0]);
            string b = SoapClient.BuildEnvelope("urn:schemas-upnp-org:service:WANIPConnection:1", "GetExternalIPAddress", new KeyValuePair<string, string>[0]);
            Assert.Equal(a, b);
        }
    }

    public class DidlTests
    {
        private static XmlNodeLite Parse(string s) { return SafeXml.Parse(Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetByteCount(s)); }

        [Fact]
        public void TheMetadataHasTheTitleTheClassAndTheResWithItsProtocolInfo()
        {
            string d = DidlLite.Build("Liga de Campeones 2", "http://192.168.1.50:5000/c/0123/live.ts", "video/mpeg", "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000");
            var root = Parse(d);
            Assert.Equal("DIDL-Lite", root.Name);
            var item = root.Child("item");
            Assert.Equal("Liga de Campeones 2", item.ChildText("title"));
            Assert.Equal("object.item.videoItem", item.ChildText("class"));
            Assert.Equal("http://192.168.1.50:5000/c/0123/live.ts", item.ChildText("res"));
            Assert.Contains("protocolInfo=\"http-get:*:video/mpeg:DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\"", d);
            Assert.Contains("xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"", d);
            Assert.Contains("restricted=\"1\"", d);
        }

        [Fact]
        public void ATitleFromAListCannotBreakOutOfTheXml()
        {
            string evil = "</dc:title><upnp:class>object.container</upnp:class><x a=\"1\" & 'q' \u0001\u0000>";
            string d = DidlLite.Build(evil, "http://h/?a=1&b=<2>\"", "video/mp2t", null);
            var root = Parse(d);                                                              // it is well formed
            var item = root.Child("item");
            Assert.Equal("object.item.videoItem", item.ChildText("class"));                  // the injected class did not become an element
            Assert.Equal(1, item.Children.Count(c => c.Name == "class"));
            Assert.Equal("</dc:title><upnp:class>object.container</upnp:class><x a=\"1\" & 'q' >", item.ChildText("title"));
            Assert.Equal("http://h/?a=1&b=<2>\"", item.ChildText("res"));
            Assert.Contains("protocolInfo=\"http-get:*:video/mp2t:*\"", d);
        }

        [Fact]
        public void LongOrEmptyTitlesAreCutOrReplaced()
        {
            Assert.Equal(120, Parse(DidlLite.Build(new string('x', 5000), "http://h/", "video/mp2t", "")).Child("item").ChildText("title").Length);
            Assert.Equal("P!", Parse(DidlLite.Build("", "http://h/", "video/mp2t", "")).Child("item").ChildText("title"));
            Assert.Equal("P!", Parse(DidlLite.Build(null, "http://h/", "video/mp2t", "")).Child("item").ChildText("title"));
            Assert.Equal("P!", Parse(DidlLite.Build("  \u0001 ", "http://h/", "video/mp2t", "")).Child("item").ChildText("title"));
            Assert.Equal("Café 中文", Parse(DidlLite.Build("Café 中文", "http://h/", "video/mp2t", "")).Child("item").ChildText("title"));
        }

        [Fact]
        public void TheMetadataSurvivesBeingPutInsideASoapArgument()
        {
            string d = DidlLite.Build("A & B <live>", "http://192.168.1.50:5000/c/0123/live.ts", "video/mp2t", "DLNA.ORG_OP=00");
            string xml = SoapClient.BuildEnvelope("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI", new[] { new KeyValuePair<string, string>("CurrentURIMetaData", d) });
            var back = Parse(xml).Find("CurrentURIMetaData").Text;
            Assert.Equal(d, back);
            Assert.Equal("A & B <live>", Parse(back).Child("item").ChildText("title"));
        }

        [Fact]
        public void RandomTitlesAlwaysGiveWellFormedMetadata()
        {
            var rnd = new Random(8);
            for (int i = 0; i < 500; i++)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < rnd.Next(0, 60); k++) sb.Append((char)rnd.Next(0, 0xFFFF));
                var d = DidlLite.Build(sb.ToString(), "http://h/" + sb, "video/mp2t", "x");
                Parse(d);
            }
        }
    }

    public class DlnaDescriptionTests
    {
        private static string Fixture(string n) { return File.ReadAllText(TestUtil.FixturePath(n)); }
        private static DlnaRenderer Parse(string xml, string loc = "http://192.168.1.68:9197/dmr")
        {
            var b = Encoding.UTF8.GetBytes(xml);
            return DlnaDescription.Parse(b, b.Length, new Uri(loc));
        }

        [Fact]
        public void TheRealSamsungDescriptionIsParsed()
        {
            var r = Parse(Fixture("samsung-q-dmr.xml"));
            Assert.Equal("[TV] tele", r.FriendlyName);
            Assert.Equal("Samsung Electronics", r.Manufacturer);
            Assert.Equal("QE65Q64TAUXXC", r.ModelName);
            Assert.Equal("AllShare1.0", r.ModelNumber);
            Assert.StartsWith("uuid:", r.Udn);
            Assert.Equal("urn:schemas-upnp-org:device:MediaRenderer:1", r.DeviceType);
            Assert.True(r.IsUsable);
            Assert.Equal("urn:schemas-upnp-org:service:AVTransport:1", r.AvTransport.ServiceType);
            Assert.Equal("http://192.168.1.68:9197/upnp/control/AVTransport1", r.AvTransport.ControlUrl.ToString());
            Assert.Equal("http://192.168.1.68:9197/upnp/control/RenderingControl1", r.RenderingControl.ControlUrl.ToString());
            Assert.Equal("http://192.168.1.68:9197/upnp/control/ConnectionManager1", r.ConnectionManager.ControlUrl.ToString());
            Assert.Equal(IPAddress.Parse("192.168.1.68"), r.Address);
        }

        private static string Desc(string device, string urlBase = "") { return "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\">" + urlBase + "<device>" + device + "</device></root>"; }

        private const string Services = "<serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/avt</controlURL></service>"
            + "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>rc</controlURL></service></serviceList>";

        [Fact]
        public void RelativeControlUrlsResolveAgainstTheLocationOrAUrlBaseOnTheSameHost()
        {
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + Services), "http://10.0.0.5:8080/desc/dmr.xml");
            Assert.Equal("http://10.0.0.5:8080/avt", r.AvTransport.ControlUrl.ToString());
            Assert.Equal("http://10.0.0.5:8080/desc/rc", r.RenderingControl.ControlUrl.ToString());
            var b = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + Services, "<URLBase>http://10.0.0.5:8080/base/</URLBase>"), "http://10.0.0.5:8080/desc/dmr.xml");
            Assert.Equal("http://10.0.0.5:8080/avt", b.AvTransport.ControlUrl.ToString());
            Assert.Equal("http://10.0.0.5:8080/base/rc", b.RenderingControl.ControlUrl.ToString());
        }

        [Fact]
        public void AUrlBaseOnAnotherHostIsIgnoredAndAControlUrlOnAnotherHostIsDropped()
        {
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + Services, "<URLBase>http://203.0.113.9/</URLBase>"), "http://10.0.0.5:8080/d.xml");
            Assert.Equal("http://10.0.0.5:8080/avt", r.AvTransport.ControlUrl.ToString());
            string foreign = "<serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>http://203.0.113.9/steal</controlURL></service></serviceList>";
            var f = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + foreign), "http://10.0.0.5:8080/d.xml");
            Assert.Null(f.AvTransport);
            Assert.False(f.IsUsable);
            string loopback = "<serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>http://127.0.0.1:22/</controlURL></service></serviceList>";
            Assert.Null(Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + loopback), "http://10.0.0.5:8080/d.xml").AvTransport);
        }

        [Fact]
        public void BadControlUrlsAreDropped()
        {
            foreach (var bad in new[] { "file:///etc/passwd", "ftp://10.0.0.5/x", "http://user:pw@10.0.0.5/x", "/with space", "/" + new string('a', 600), "javascript:alert(1)" })
            {
                string s = "<serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>" + System.Security.SecurityElement.Escape(bad) + "</controlURL></service></serviceList>";
                Assert.Null(Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" + s), "http://10.0.0.5:8080/d.xml").AvTransport);
            }
        }

        [Fact]
        public void ServicesInAnEmbeddedMediaRendererAreFoundAndTheNamesComeFromIt()
        {
            string inner = "<deviceList><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>Salon</friendlyName><manufacturer>Sony</manufacturer><modelName>KD-55</modelName><UDN>uuid:abc</UDN>" + Services + "</device></deviceList>";
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType><friendlyName>Hub</friendlyName>" + inner));
            Assert.Equal("Salon", r.FriendlyName);
            Assert.Equal("Sony", r.Manufacturer);
            Assert.Equal("uuid:abc", r.Udn);
            Assert.True(r.IsUsable);
        }

        [Fact]
        public void AMediaServerIsNotARenderer()
        {
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType><friendlyName>NAS</friendlyName>" + Services));
            Assert.False(r.IsUsable);
        }

        [Fact]
        public void TheHighestAvTransportVersionWins()
        {
            string s = "<serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/v1</controlURL></service><service><serviceType>urn:schemas-upnp-org:service:AVTransport:3</serviceType><controlURL>/v3</controlURL></service></serviceList>";
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:2</deviceType>" + s));
            Assert.EndsWith("/v3", r.AvTransport.ControlUrl.ToString());
            Assert.EndsWith(":3", r.AvTransport.ServiceType);
        }

        [Fact]
        public void NamesAreCleanedAndTruncated()
        {
            var r = Parse(Desc("<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>" + new string('x', 500) + "</friendlyName><manufacturer>A\tB\nC</manufacturer>" + Services));
            Assert.True(r.FriendlyName.Length <= 100);
            Assert.Equal("A B C", r.Manufacturer);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<root/>")]
        [InlineData("<html><body>hola</body></html>")]
        [InlineData("<root><nodevice/></root>")]
        [InlineData("<!DOCTYPE root [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><root><device>&x;</device></root>")]
        public void NotADescriptionIsAProtocolError(string xml)
        {
            Assert.Throws<UpnpException>(() => Parse(xml));
        }

        [Fact]
        public void FuzzedDescriptionsNeverCrash()
        {
            var rnd = new Random(33);
            string good = Fixture("samsung-q-dmr.xml");
            for (int i = 0; i < 1500; i++)
            {
                var b = Encoding.UTF8.GetBytes(good);
                int muts = 1 + rnd.Next(10);
                for (int m = 0; m < muts; m++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                if (i % 7 == 0) Array.Resize(ref b, rnd.Next(1, b.Length));
                try { DlnaDescription.Parse(b, b.Length, new Uri("http://192.168.1.68:9197/dmr")); }
                catch (UpnpException) { }
            }
        }
    }

    public class DlnaValueTests
    {
        [Fact]
        public void ProtocolInfoListIsParsedIncludingColonsInTheAdditionalInfo()
        {
            var l = DlnaProtocolInfo.ParseList("http-get:*:video/mpeg:DLNA.ORG_PN=MPEG_TS_NA_ISO;DLNA.ORG_FLAGS=ED1:00, http-get:*:Video/MP2T:*,garbage,rtsp-rtp-udp:*:audio/mpeg:*, ,http-get:*:image/jpeg");
            Assert.Equal(4, l.Count);
            Assert.Equal("video/mpeg", l[0].ContentFormat);
            Assert.Equal("DLNA.ORG_PN=MPEG_TS_NA_ISO;DLNA.ORG_FLAGS=ED1:00", l[0].AdditionalInfo);
            Assert.Equal("video/mp2t", l[1].ContentFormat);
            Assert.Equal("rtsp-rtp-udp", l[2].Protocol);
            Assert.Equal("", l[3].AdditionalInfo);
            Assert.Empty(DlnaProtocolInfo.ParseList(null));
            Assert.Equal(512, DlnaProtocolInfo.ParseList(string.Join(",", Enumerable.Repeat("http-get:*:a/b:*", 2000))).Count);
        }

        [Theory]
        [InlineData("PLAYING", DlnaTransportState.Playing)]
        [InlineData("playing", DlnaTransportState.Playing)]
        [InlineData(" STOPPED ", DlnaTransportState.Stopped)]
        [InlineData("PAUSED_PLAYBACK", DlnaTransportState.PausedPlayback)]
        [InlineData("TRANSITIONING", DlnaTransportState.Transitioning)]
        [InlineData("NO_MEDIA_PRESENT", DlnaTransportState.NoMediaPresent)]
        [InlineData("BUFFERING", DlnaTransportState.Unknown)]
        [InlineData("", DlnaTransportState.Unknown)]
        [InlineData(null, DlnaTransportState.Unknown)]
        public void TransportStatesAreParsed(string raw, DlnaTransportState expected)
        {
            Assert.Equal(expected, DlnaTransportInfo.ParseState(raw));
        }

        [Theory]
        [InlineData("00:00:12", 12)]
        [InlineData("01:02:03.5", 3723.5)]
        [InlineData("0:01:00", 60)]
        [InlineData("NOT_IMPLEMENTED", -1)]
        [InlineData("", -1)]
        [InlineData("-01:00:00", -1)]
        [InlineData("00:00", -1)]
        [InlineData("a:b:c", -1)]
        public void PositionTimesAreParsed(string raw, double seconds)
        {
            Assert.Equal(seconds, DlnaPositionInfo.ParseTime(raw), 3);
        }

        [Fact]
        public void TheSamsungVariantsStartWithVideoMpegAndTheSinkReordersThem()
        {
            var v = DlnaQuirks.VariantsFor("Samsung Electronics", "QE65Q64T");
            Assert.Equal("video/mpeg", v[0].MimeType);
            Assert.Equal(ProgressiveMode.BigContentLength, v[0].Mode);
            Assert.Contains(v, x => x.ProfileName == "MPEG_TS_NA_ISO");
            Assert.Equal("video/mp2t", DlnaQuirks.VariantsFor("Acme", "x")[0].MimeType);
            Assert.Equal("video/mp2t", DlnaQuirks.VariantsFor("LG Electronics", "x")[0].MimeType);
            var sink = new List<DlnaProtocolInfo> { new DlnaProtocolInfo { Protocol = "http-get", ContentFormat = "video/vnd.dlna.mpeg-tts" } };
            var lg = DlnaQuirks.Prefer(DlnaQuirks.VariantsFor("LG Electronics", "x"), sink);
            Assert.Equal("video/vnd.dlna.mpeg-tts", lg[0].MimeType);
            Assert.Equal(DlnaQuirks.VariantsFor("LG Electronics", "x").Count, lg.Count);
            Assert.Equal(v[0].Name, DlnaQuirks.Prefer(v, new DlnaProtocolInfo[0])[0].Name);
            Assert.Equal(DlnaQuirks.SenderPacedFlags.Length, 32);
            Assert.StartsWith("DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=8D5", v[0].Features);
        }
    }

    [Collection(CastIsolatedCollection.Name)]
    public class DlnaDiscoveryTests : IDisposable
    {
        private readonly FakeUdpNetwork _net = new FakeUdpNetwork();
        private readonly FakeNetworkInfo _info = FakeNetworkInfo.Home();
        private readonly List<FakeRenderer> _renderers = new List<FakeRenderer>();
        public void Dispose() { foreach (var r in _renderers) r.Dispose(); }

        private FakeRenderer AddRenderer(string ip = "192.168.1.68", Action<FakeRenderer> cfg = null, string locationHost = null)
        {
            var r = new FakeRenderer();
            if (cfg != null) cfg(r);
            _renderers.Add(r);
            var ep = new IPEndPoint(IPAddress.Parse(ip), 1900);
            Action<FakePacket> handler = p =>
            {
                string text = Encoding.ASCII.GetString(p.Data);
                if (!text.StartsWith("M-SEARCH", StringComparison.Ordinal)) return;
                string st = text.Split(new[] { "\r\n" }, StringSplitOptions.None).First(l => l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase)).Substring(3).Trim();
                if (st != "urn:schemas-upnp-org:device:MediaRenderer:1" && st != "urn:schemas-upnp-org:service:AVTransport:1") return;
                string resp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\nLOCATION: http://" + (locationHost ?? "127.0.0.1") + ":" + r.Http.Port + "/dmr\r\nSERVER: Linux/1.0 UPnP/1.0\r\nST: " + st + "\r\nUSN: " + r.Udn + "::" + st + "\r\n\r\n";
                _net.Deliver(ep, p.From, Encoding.ASCII.GetBytes(resp));
            };
            _net.Register(FakeUdpNetwork.SsdpGroup, handler);
            return r;
        }

        private DlnaDiscovery Discovery(Action<DlnaDiscoveryOptions> cfg = null)
        {
            var o = new DlnaDiscoveryOptions { AllowLoopback = true, RequireLocationFromSender = false };
            o.Ssdp = new SsdpOptions { Window = TimeSpan.FromMilliseconds(900), Retries = 2, RetryInterval = TimeSpan.FromMilliseconds(100), SearchTargets = new List<string>(DlnaDiscoveryOptions.MediaRendererTargets), UseUnicastToGateway = false, StopAfterFirstIgd = false, AllowLoopbackLocation = true };
            o.Dlna = new DlnaOptions { Timeout = TimeSpan.FromSeconds(3) };
            if (cfg != null) cfg(o);
            return new DlnaDiscovery(_net, _info, o);
        }

        [Fact]
        public async Task TheServiceKeepsARendererThatMissesASearchForTheRetentionTimeAndThenDropsIt()
        {
            var a = AddRenderer("192.168.1.68", x => { x.Name = "[TV] sala"; x.Udn = "uuid:s1"; });
            var o = new CastServiceOptions { DlnaRetention = TimeSpan.FromMilliseconds(1500) };
            o.Discovery = new DlnaDiscoveryOptions { AllowLoopback = true, RequireLocationFromSender = false, Ssdp = new SsdpOptions { Window = TimeSpan.FromMilliseconds(500), Retries = 1, RetryInterval = TimeSpan.FromMilliseconds(100), SearchTargets = new List<string>(DlnaDiscoveryOptions.MediaRendererTargets), UseUnicastToGateway = false, StopAfterFirstIgd = false, AllowLoopbackLocation = true }, Dlna = new DlnaOptions { Timeout = TimeSpan.FromSeconds(2) } };
            using (var s = new CastService(() => null, _info, _net, o))
            {
                int changes = 0; s.Targets.Changed += () => changes++;
                await s.DiscoverDlnaAsync(CancellationToken.None);
                Assert.Single(s.Targets.Items);
                a.Dispose();                                                      // the television stops answering (its description cannot be fetched any more)
                await s.DiscoverDlnaAsync(CancellationToken.None);
                Assert.Single(s.Targets.Items);                                   // one missed search: it is still listed
                Assert.Equal(1, changes);                                         // and the list did not flicker
                await Task.Delay(1700);
                await s.DiscoverDlnaAsync(CancellationToken.None);
                Assert.Empty(s.Targets.Items);                                    // missing for longer than the retention: gone
            }
        }

        [Fact]
        public async Task WithNoRetentionEachSearchReplacesTheList()
        {
            var a = AddRenderer("192.168.1.68", x => { x.Name = "[TV] sala"; x.Udn = "uuid:s1"; });
            var o = new CastServiceOptions();
            o.Discovery = new DlnaDiscoveryOptions { AllowLoopback = true, RequireLocationFromSender = false, Ssdp = new SsdpOptions { Window = TimeSpan.FromMilliseconds(500), Retries = 1, RetryInterval = TimeSpan.FromMilliseconds(100), SearchTargets = new List<string>(DlnaDiscoveryOptions.MediaRendererTargets), UseUnicastToGateway = false, StopAfterFirstIgd = false, AllowLoopbackLocation = true }, Dlna = new DlnaOptions { Timeout = TimeSpan.FromSeconds(2) } };
            using (var s = new CastService(() => null, _info, _net, o))
            {
                await s.DiscoverDlnaAsync(CancellationToken.None);
                Assert.Single(s.Targets.Items);
                a.Dispose();
                await s.DiscoverDlnaAsync(CancellationToken.None);
                Assert.Empty(s.Targets.Items);
            }
        }

        [Fact]
        public async Task EachRendererIsReportedOnceAsSoonAsItIsDescribedWhileTheSearchIsStillListening()
        {
            AddRenderer("192.168.1.68", x => { x.Name = "[TV] sala"; x.Udn = "uuid:s1"; });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var seen = new List<KeyValuePair<string, TimeSpan>>();
            var res = await Discovery(o => o.Ssdp.Window = TimeSpan.FromSeconds(2)).DiscoverAsync(CancellationToken.None, r => { lock (seen) seen.Add(new KeyValuePair<string, TimeSpan>(r.FriendlyName, sw.Elapsed)); });
            var done = sw.Elapsed;
            Assert.Single(seen);                                                             // once, though it answered two search targets in two rounds
            Assert.Equal("[TV] sala", seen[0].Key);
            Assert.True(done - seen[0].Value > TimeSpan.FromSeconds(0.8), "reported at " + seen[0].Value + ", the search ended at " + done);
            Assert.Single(res.Renderers);
        }

        [Fact]
        public async Task ASearchClosedBeforeItsEndHasAlreadyListedWhatItFoundAndKeepsIt()
        {
            AddRenderer("192.168.1.68", x => { x.Name = "[TV] sala"; x.Udn = "uuid:s1"; });
            var o = new CastServiceOptions { DlnaRetention = TimeSpan.FromSeconds(40) };
            o.Discovery = new DlnaDiscoveryOptions { AllowLoopback = true, RequireLocationFromSender = false, Ssdp = new SsdpOptions { Window = TimeSpan.FromSeconds(5), Retries = 1, RetryInterval = TimeSpan.FromMilliseconds(100), SearchTargets = new List<string>(DlnaDiscoveryOptions.MediaRendererTargets), UseUnicastToGateway = false, StopAfterFirstIgd = false, AllowLoopbackLocation = true }, Dlna = new DlnaOptions { Timeout = TimeSpan.FromSeconds(2) } };
            using (var s = new CastService(() => null, _info, _net, o))
            using (var cts = new CancellationTokenSource())
            {
                s.Targets.Changed += () => { if (s.Targets.Items.Count > 0) Task.Run(() => cts.Cancel()); };   // the sheet is closed as soon as the television shows
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.DiscoverDlnaAsync(cts.Token));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), "the search was not cut short: " + sw.Elapsed);
                Assert.Single(s.Targets.Items);
                Assert.Equal("[TV] sala", s.Targets.Items[0].Name);
            }
        }

        [Fact]
        public async Task ARendererThatAnswersTheMSearchIsFoundAndDescribed()
        {
            var r = AddRenderer(cfg: x => { x.Name = "[TV] sala"; x.Manufacturer = "Samsung Electronics"; x.Model = "QE65"; x.Udn = "uuid:s1"; });
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            Assert.Single(res.Renderers);
            var d = res.Renderers[0];
            Assert.Equal("[TV] sala", d.FriendlyName);
            Assert.Equal("Samsung Electronics", d.Manufacturer);
            Assert.Equal("uuid:s1", d.Udn);
            Assert.NotNull(d.AvTransport);
            // the searches asked for MediaRenderer and AVTransport, never for everything
            var targets = _net.Sent.Select(p => Encoding.ASCII.GetString(p.Data)).Where(t => t.StartsWith("M-SEARCH")).SelectMany(t => t.Split(new[] { "\r\n" }, StringSplitOptions.None)).Where(l => l.StartsWith("ST:")).Select(l => l.Substring(3).Trim()).Distinct().ToList();
            Assert.All(targets, t => Assert.True(t.Contains("MediaRenderer") || t.Contains("AVTransport"), t));
        }

        [Fact]
        public async Task SeveralRenderersAndTheTwoSearchTargetsGiveOneEntryPerDevice()
        {
            AddRenderer("192.168.1.68", x => { x.Name = "A"; x.Udn = "uuid:a"; });
            AddRenderer("192.168.1.69", x => { x.Name = "B"; x.Udn = "uuid:b"; });
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            Assert.Equal(2, res.Renderers.Count);
            Assert.Equal(new[] { "A", "B" }, res.Renderers.Select(x => x.FriendlyName).OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task ALocationOnAnotherHostThanTheSenderIsIgnoredByDefault()
        {
            AddRenderer("192.168.1.68", locationHost: "192.168.1.200");
            var res = await Discovery(o => { o.RequireLocationFromSender = true; o.AllowLoopback = false; o.Ssdp.AllowLoopbackLocation = false; }).DiscoverAsync(CancellationToken.None);
            Assert.Empty(res.Renderers);
            Assert.True(res.Rejected >= 1);
            Assert.Contains(res.Notes, n => n.Contains("LOCATION"));
        }

        [Fact]
        public async Task ALocationOnTheInternetIsNeverFetched()
        {
            AddRenderer("192.168.1.68", locationHost: "8.8.8.8");
            var res = await Discovery(o => { o.RequireLocationFromSender = false; }).DiscoverAsync(CancellationToken.None);
            Assert.Empty(res.Renderers);
            Assert.Equal(0, res.Responses);                                                  // dropped by the SSDP layer
        }

        [Fact]
        public async Task ABrokenDescriptionIsSkippedAndTheOthersStillWork()
        {
            var bad = AddRenderer("192.168.1.68", x => { x.Udn = "uuid:bad"; });
            bad.Http.Handler = req => FakeHttpResponse.Xml("<html>no</html>");
            AddRenderer("192.168.1.69", x => { x.Name = "good"; x.Udn = "uuid:good"; });
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            Assert.Single(res.Renderers);
            Assert.Equal("good", res.Renderers[0].FriendlyName);
            Assert.Equal(1, res.FetchFailed);
        }

        [Fact]
        public async Task NothingOnTheNetworkIsAnEmptyListNotAnError()
        {
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            Assert.Empty(res.Renderers);
        }

        [Fact]
        public async Task GarbageDatagramsAreIgnored()
        {
            AddRenderer("192.168.1.68", x => { x.Name = "ok"; x.Udn = "uuid:ok"; });
            var rnd = new Random(4);
            _net.Register(FakeUdpNetwork.SsdpGroup, p =>
            {
                for (int i = 0; i < 20; i++) { var g = new byte[rnd.Next(1, 400)]; rnd.NextBytes(g); _net.Deliver(new IPEndPoint(IPAddress.Parse("192.168.1.99"), 1900), p.From, g); }
                _net.Deliver(new IPEndPoint(IPAddress.Parse("192.168.1.98"), 1900), p.From, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\nLOCATION: http://127.0.0.1:1/x\r\n\r\n"));
            });
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            Assert.Single(res.Renderers);
            Assert.Equal("ok", res.Renderers[0].FriendlyName);
        }

        [Fact]
        public async Task TheTargetForTheListIsBuiltFromTheRenderer()
        {
            AddRenderer("192.168.1.68", x => { x.Name = "[TV] sala"; x.Manufacturer = "LG"; x.Model = "OLED"; x.Udn = "uuid:lg"; });
            var res = await Discovery().DiscoverAsync(CancellationToken.None);
            var t = DlnaDiscovery.ToTarget(res.Renderers[0]);
            Assert.Equal(CastTargetKind.Dlna, t.Kind);
            Assert.Equal("dlna:uuid:lg", t.Id);
            Assert.Equal("[TV] sala", t.Name);
            Assert.True(t.Supports(CastCapabilities.ProgressiveTs | CastCapabilities.AnyCodec | CastCapabilities.Volume));
            Assert.False(t.Supports(CastCapabilities.Hls));
            Assert.Equal(IPAddress.Loopback, t.Address);
        }
    }

    public class CastTargetListTests
    {
        private static CastTarget T(CastTargetKind k, string id, string name, string ip) { return new CastTarget { Kind = k, Id = id, Name = name, Address = IPAddress.Parse(ip), Capabilities = CastCapabilities.Hls }; }

        [Fact]
        public void EachProviderReplacesItsOwnTargetsAndTheListIsSortedByName()
        {
            var l = new CastTargetList();
            int changes = 0;
            l.Changed += () => changes++;
            l.Update("dlna", new[] { T(CastTargetKind.Dlna, "dlna:b", "Zeta TV", "192.168.1.70"), T(CastTargetKind.Dlna, "dlna:a", "Alfa TV", "192.168.1.71") });
            l.Update("googlecast", new[] { T(CastTargetKind.GoogleCast, "gcast:1", "Chromecast sala", "192.168.1.80") });
            Assert.Equal(new[] { "Alfa TV", "Chromecast sala", "Zeta TV" }, l.Items.Select(t => t.Name).ToArray());
            Assert.Equal(2, changes);
            l.Update("dlna", new[] { T(CastTargetKind.Dlna, "dlna:a", "Alfa TV", "192.168.1.71") });
            Assert.Equal(new[] { "Alfa TV", "Chromecast sala" }, l.Items.Select(t => t.Name).ToArray());
            l.Update("googlecast", null);
            Assert.Single(l.Items);
            Assert.Equal(4, changes);
        }

        [Fact]
        public void NoChangeNoEvent()
        {
            var l = new CastTargetList();
            int changes = 0;
            l.Changed += () => changes++;
            l.Update("dlna", new[] { T(CastTargetKind.Dlna, "dlna:a", "A", "192.168.1.71") });
            l.Update("dlna", new[] { T(CastTargetKind.Dlna, "dlna:a", "A", "192.168.1.71") });
            Assert.Equal(1, changes);
        }

        [Fact]
        public void TheSameScreenReachableTwoWaysIsOneEntryWithTheCastWayFirst()
        {
            var l = new CastTargetList();
            l.Update("dlna", new[] { T(CastTargetKind.Dlna, "dlna:tv", "Sony TV", "192.168.1.90") });
            l.Update("googlecast", new[] { T(CastTargetKind.GoogleCast, "gcast:tv", "Sony TV (Cast)", "192.168.1.90") });
            var items = l.Items;
            Assert.Single(items);
            Assert.Equal(CastTargetKind.GoogleCast, items[0].Kind);
            Assert.Equal("dlna:tv", items[0].Alternatives.Single().Id);
            Assert.Equal("dlna:tv", l.Find("dlna:tv").Id);
            Assert.Equal(CastTargetKind.Dlna, l.Find("dlna:tv").Kind);
            Assert.Null(l.Find("nope"));
        }

        [Fact]
        public void InvalidTargetsAreSkippedAndDuplicateIdsKeepTheFirst()
        {
            var l = new CastTargetList();
            l.Update("dlna", new[] { null, new CastTarget { Id = "x", Address = null }, new CastTarget { Id = "", Address = IPAddress.Loopback }, T(CastTargetKind.Dlna, "dlna:a", "First", "192.168.1.71"), T(CastTargetKind.Dlna, "dlna:a", "Second", "192.168.1.72") });
            Assert.Single(l.Items);
            Assert.Equal("First", l.Items[0].Name);
            Assert.Equal("dlna", l.Items[0].Provider);
        }

        [Fact]
        public void ItIsSafeFromSeveralThreads()
        {
            var l = new CastTargetList();
            Parallel.For(0, 200, i => { l.Update("p" + (i % 4), Enumerable.Range(0, 5).Select(k => T(CastTargetKind.Dlna, "id" + (i % 4) + "-" + k, "N" + k, "10.0." + (i % 4) + "." + (k + 1)))); var x = l.Items; });
            Assert.Equal(20, l.Items.Count);
        }
    }
}
