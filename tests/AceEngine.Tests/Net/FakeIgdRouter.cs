using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AceEngine.Tests.NetTests
{
    public enum IgdVariant
    {
        /// <summary>InternetGatewayDevice:1 with WANIPConnection:1 (absolute paths).</summary>
        IpV1,
        /// <summary>InternetGatewayDevice:2 with WANIPConnection:2.</summary>
        IpV2,
        /// <summary>IGD:1 with WANPPPConnection:1 nested two levels deep (WANDevice / WANConnectionDevice).</summary>
        PppNested,
        /// <summary>IGD:1 with WANIPConnection:1, URLBase and a controlURL relative without a leading slash.</summary>
        IpV1UrlBase
    }

    internal sealed class FakeMapping
    {
        public string Proto = "";
        public int ExternalPort, InternalPort, Lease;
        public string InternalClient = "", Description = "";
        public Stopwatch Age = Stopwatch.StartNew();
        public bool Expired { get { return Lease > 0 && Age.Elapsed.TotalSeconds >= Lease; } }
    }

    internal sealed class FakeSoapCall
    {
        public string Action = "";
        public List<KeyValuePair<string, string>> Args = new List<KeyValuePair<string, string>>();
        public string SoapActionHeader = "", ContentType = "", UserAgent = "", Path = "", ServiceNamespace = "";
        public string Arg(string name) { var kv = Args.FirstOrDefault(a => a.Key == name); return kv.Key == null ? null : kv.Value; }
    }

    internal sealed class FakeSsdpSearch
    {
        public string St = "";
        public IPEndPoint From, To;
        public bool Multicast { get { return To.Address.Equals(FakeUdpNetwork.SsdpGroup.Address); } }
    }

    /// <summary>
    /// Simulated router: SSDP over the virtual UDP network and a real HTTP server on the local loopback with the XML description and the IGD's SOAP actions over a table
    /// of stateful mappings (conflict 718, lease, 725, 402...). Reusable by the PortMapper, IgdClient and UpnpDiagnostics tests.
    /// </summary>
    internal sealed class FakeIgdRouter : IDisposable
    {
        public readonly FakeUdpNetwork Net;
        public readonly FakeHttpServer Http = new FakeHttpServer();
        public readonly IPEndPoint Endpoint;
        public IgdVariant Variant;

        // ----- behavior -----
        public volatile bool AnswerSsdp = true;
        public TimeSpan SsdpReplyDelay = TimeSpan.Zero;
        /// <summary>Only answers to these STs (by default IGD:1 and rootdevice; IpV2 adds IGD:2).</summary>
        public HashSet<string> SsdpTargets;
        public string ExternalIp = "203.0.113.7";
        public volatile int FailAddCode;
        public volatile int FailAllCode;
        public volatile bool OnlyPermanentLeases;
        public volatile bool SwallowAdd;
        public volatile bool GetSpecificBroken;
        public volatile bool HangSoap;
        /// <summary>All external ports are "busy" (718).</summary>
        public volatile bool ConflictAll;
        /// <summary>The router requires external port = internal (724).</summary>
        public volatile bool RequireSamePort;
        /// <summary>Rejects UDP mappings (501) and accepts TCP ones.</summary>
        public volatile bool RejectUdp;
        public readonly HashSet<int> ConflictPorts = new HashSet<int>();
        /// <summary>Only answers to whoever arrives from an IP that satisfies this (by default, everyone).</summary>
        public Func<IPAddress, bool> AcceptsFrom = ip => true;
        /// <summary>Replaces the LOCATION of the SSDP responses.</summary>
        public string LocationOverride;
        /// <summary>Replaces the whole XML description.</summary>
        public string DescriptionOverride;
        public Func<FakeHttpRequest, FakeHttpResponse> DescriptionResponder;

        // ----- state -----
        private readonly object _gate = new object();
        private readonly Dictionary<string, FakeMapping> _mappings = new Dictionary<string, FakeMapping>();
        public readonly ConcurrentQueue<FakeSoapCall> Calls = new ConcurrentQueue<FakeSoapCall>();
        public readonly ConcurrentQueue<FakeSsdpSearch> Searches = new ConcurrentQueue<FakeSsdpSearch>();

        public const string IpConnV1 = "urn:schemas-upnp-org:service:WANIPConnection:1";
        public const string IpConnV2 = "urn:schemas-upnp-org:service:WANIPConnection:2";
        public const string PppConnV1 = "urn:schemas-upnp-org:service:WANPPPConnection:1";

        public FakeIgdRouter(FakeUdpNetwork net, IgdVariant variant = IgdVariant.IpV1, string ip = "192.168.1.1")
        {
            Net = net;
            Variant = variant;
            Endpoint = new IPEndPoint(IPAddress.Parse(ip), 1900);
            Http.Handler = HandleHttp;
            net.Register(FakeUdpNetwork.SsdpGroup, OnSsdp);
            net.Register(Endpoint, OnSsdp);
        }

        public string ServiceType { get { return Variant == IgdVariant.IpV2 ? IpConnV2 : Variant == IgdVariant.PppNested ? PppConnV1 : IpConnV1; } }
        public string ControlPath { get { return Variant == IgdVariant.PppNested ? "/ctl/PPPConn" : "/ctl/IPConn"; } }
        public string DescriptionUrl { get { return "http://127.0.0.1:" + Http.Port + "/rootDesc.xml"; } }

        // ---------- mappings table ----------

        public IReadOnlyList<FakeMapping> Mappings { get { lock (_gate) { Expire(); return _mappings.Values.ToList(); } } }
        public FakeMapping Get(string proto, int port) { lock (_gate) { Expire(); FakeMapping m; return _mappings.TryGetValue(proto + ":" + port, out m) ? m : null; } }
        public bool Has(string proto, int port) { return Get(proto, port) != null; }
        public void Reboot() { lock (_gate) _mappings.Clear(); }
        public void Preload(string proto, int port, string client, int internalPort, int lease = 0, string desc = "otro") { lock (_gate) _mappings[proto + ":" + port] = new FakeMapping { Proto = proto, ExternalPort = port, InternalClient = client, InternalPort = internalPort, Lease = lease, Description = desc }; }
        public int CountCalls(string action) { return Calls.Count(c => c.Action == action); }
        public List<FakeSoapCall> CallsOf(string action) { return Calls.Where(c => c.Action == action).ToList(); }
        private void Expire() { foreach (var k in _mappings.Where(kv => kv.Value.Expired).Select(kv => kv.Key).ToList()) _mappings.Remove(k); }

        // ---------- SSDP ----------

        private HashSet<string> Targets()
        {
            if (SsdpTargets != null) return SsdpTargets;
            var t = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upnp:rootdevice", "ssdp:all" };
            t.Add(Variant == IgdVariant.IpV2 ? "urn:schemas-upnp-org:device:InternetGatewayDevice:2" : "urn:schemas-upnp-org:device:InternetGatewayDevice:1");
            return t;
        }

        private void OnSsdp(FakePacket p)
        {
            string text = Encoding.ASCII.GetString(p.Data);
            if (!text.StartsWith("M-SEARCH * HTTP/1.1", StringComparison.Ordinal)) return;
            string st = null; bool man = false;
            foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line.Substring(0, c).Trim().ToUpperInvariant(), v = line.Substring(c + 1).Trim();
                if (k == "ST") st = v;
                if (k == "MAN" && v == "\"ssdp:discover\"") man = true;
            }
            Searches.Enqueue(new FakeSsdpSearch { St = st ?? "", From = p.From, To = p.To });
            if (!AnswerSsdp || !man || st == null || !AcceptsFrom(p.From.Address)) return;
            if (!Targets().Contains(st)) return;
            if (SsdpReplyDelay > TimeSpan.Zero) Thread.Sleep(SsdpReplyDelay);
            string reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nEXT:\r\nLOCATION: " + (LocationOverride ?? DescriptionUrl) + "\r\nSERVER: FakeOS/1.0 UPnP/1.1 FakeIGD/2.0\r\nST: " + st
                + "\r\nUSN: uuid:fake-igd-0001::" + st + "\r\n\r\n";
            Net.Deliver(Endpoint, p.From, Encoding.ASCII.GetBytes(reply));
        }

        // ---------- HTTP ----------

        private string Description()
        {
            if (DescriptionOverride != null) return DescriptionOverride;
            string port = Http.Port.ToString();
            bool v2 = Variant == IgdVariant.IpV2, ppp = Variant == IgdVariant.PppNested, urlBase = Variant == IgdVariant.IpV1UrlBase;
            string wanType = v2 ? IpConnV2 : ppp ? PppConnV1 : IpConnV1;
            string ctl = ppp ? "/ctl/PPPConn" : "/ctl/IPConn";
            if (urlBase) ctl = "ctl/IPConn";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\r\n<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>");
            if (urlBase) sb.Append("<URLBase>http://127.0.0.1:" + port + "/</URLBase>");
            sb.Append("<device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:" + (v2 ? "2" : "1") + "</deviceType><friendlyName>Test router</friendlyName><manufacturer>Fake Inc</manufacturer><modelName>FR-1</modelName><modelNumber>1.0</modelNumber>");
            sb.Append("<serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><serviceId>urn:upnp-org:serviceId:L3Forwarding1</serviceId><controlURL>/ctl/L3F</controlURL><eventSubURL>/evt/L3F</eventSubURL><SCPDURL>/L3F.xml</SCPDURL></service></serviceList>");
            sb.Append("<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType><friendlyName>WANDevice</friendlyName>");
            sb.Append("<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId><controlURL>/ctl/CmnIfCfg</controlURL><eventSubURL>/evt/CmnIfCfg</eventSubURL><SCPDURL>/WANCfg.xml</SCPDURL></service></serviceList>");
            sb.Append("<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType><friendlyName>WANConnectionDevice</friendlyName>");
            sb.Append("<serviceList><service><serviceType>" + wanType + "</serviceType><serviceId>urn:upnp-org:serviceId:WANConn1</serviceId><controlURL>" + ctl + "</controlURL><eventSubURL>/evt/Conn</eventSubURL><SCPDURL>/WANConn.xml</SCPDURL></service></serviceList>");
            sb.Append("</device></deviceList></device></deviceList></device></root>");
            return sb.ToString();
        }

        private FakeHttpResponse HandleHttp(FakeHttpRequest req)
        {
            if (req.Method == "GET" && req.Path == "/rootDesc.xml")
            {
                if (DescriptionResponder != null) return DescriptionResponder(req);
                return FakeHttpResponse.Xml(Description());
            }
            if (req.Method == "POST" && req.Path == ControlPath) return HandleSoap(req);
            return new FakeHttpResponse { Status = 404, Reason = "Not Found" };
        }

        private static string Fault(int code, string text)
        {
            return "<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>" + text + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        }

        private FakeHttpResponse Soap(string action, string args)
        {
            return FakeHttpResponse.Xml("<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + "Response xmlns:u=\"" + ServiceType + "\">" + args + "</u:" + action + "Response></s:Body></s:Envelope>");
        }

        private FakeHttpResponse Err(int code, string text) { return FakeHttpResponse.Xml(Fault(code, text), 500); }

        private FakeHttpResponse HandleSoap(FakeHttpRequest req)
        {
            if (HangSoap) return new FakeHttpResponse { Hang = true };
            var call = new FakeSoapCall { SoapActionHeader = req.Header("SOAPACTION") ?? "", ContentType = req.Header("Content-Type") ?? "", UserAgent = req.Header("User-Agent") ?? "", Path = req.Path };
            try
            {
                using (var r = XmlReader.Create(new System.IO.StringReader(req.BodyText), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                {
                    int depth = 0;
                    r.MoveToContent();
                    while (!r.EOF)
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName != "Envelope" && r.LocalName != "Body")
                        {
                            if (call.Action.Length == 0) { call.Action = r.LocalName; call.ServiceNamespace = r.NamespaceURI; depth = r.Depth; r.Read(); continue; }
                            if (r.Depth == depth + 1)
                            {
                                string name = r.LocalName;
                                call.Args.Add(new KeyValuePair<string, string>(name, r.ReadElementContentAsString()));
                                continue;
                            }
                        }
                        r.Read();
                    }
                }
            }
            catch (XmlException) { return Err(402, "Invalid Args"); }
            Calls.Enqueue(call);
            string expected = "\"" + ServiceType + "#" + call.Action + "\"";
            if (call.SoapActionHeader != expected || call.ServiceNamespace != ServiceType) return Err(401, "Invalid Action");
            if (FailAllCode != 0) return Err(FailAllCode, "Forced failure");

            lock (_gate)
            {
                Expire();
                switch (call.Action)
                {
                    case "GetExternalIPAddress":
                        return Soap("GetExternalIPAddress", "<NewExternalIPAddress>" + ExternalIp + "</NewExternalIPAddress>");
                    case "AddPortMapping":
                        {
                            if (FailAddCode != 0) return Err(FailAddCode, "Forced add failure");
                            int ext, intp, lease;
                            if (!int.TryParse(call.Arg("NewExternalPort"), out ext) || !int.TryParse(call.Arg("NewInternalPort"), out intp) || !int.TryParse(call.Arg("NewLeaseDuration"), out lease)) return Err(402, "Invalid Args");
                            string proto = call.Arg("NewProtocol"), client = call.Arg("NewInternalClient");
                            IPAddress ipc;
                            if ((proto != "TCP" && proto != "UDP") || !IPAddress.TryParse(client ?? "", out ipc) || call.Arg("NewEnabled") != "1" || ext < 1 || ext > 65535 || intp < 1 || intp > 65535) return Err(402, "Invalid Args");
                            if (OnlyPermanentLeases && lease != 0) return Err(725, "OnlyPermanentLeasesSupported");
                            if (RejectUdp && proto == "UDP") return Err(501, "ActionFailed");
                            if (RequireSamePort && ext != intp) return Err(724, "SamePortValuesRequired");
                            if (ConflictAll || ConflictPorts.Contains(ext)) return Err(718, "ConflictInMappingEntry");
                            FakeMapping old;
                            if (_mappings.TryGetValue(proto + ":" + ext, out old) && old.InternalClient != client) return Err(718, "ConflictInMappingEntry");
                            if (!SwallowAdd) _mappings[proto + ":" + ext] = new FakeMapping { Proto = proto, ExternalPort = ext, InternalPort = intp, InternalClient = client, Description = call.Arg("NewPortMappingDescription") ?? "", Lease = lease };
                            return Soap("AddPortMapping", "");
                        }
                    case "GetSpecificPortMappingEntry":
                        {
                            int ext;
                            int.TryParse(call.Arg("NewExternalPort"), out ext);
                            FakeMapping m;
                            if (GetSpecificBroken || !_mappings.TryGetValue(call.Arg("NewProtocol") + ":" + ext, out m)) return Err(714, "NoSuchEntryInArray");
                            int remaining = m.Lease == 0 ? 0 : Math.Max(1, m.Lease - (int)m.Age.Elapsed.TotalSeconds);
                            return Soap("GetSpecificPortMappingEntry", "<NewInternalPort>" + m.InternalPort + "</NewInternalPort><NewInternalClient>" + m.InternalClient + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + m.Description + "</NewPortMappingDescription><NewLeaseDuration>" + remaining + "</NewLeaseDuration>");
                        }
                    case "GetGenericPortMappingEntry":
                        {
                            int idx;
                            int.TryParse(call.Arg("NewPortMappingIndex"), out idx);
                            var list = _mappings.Values.OrderBy(x => x.ExternalPort).ThenBy(x => x.Proto).ToList();
                            if (idx < 0 || idx >= list.Count) return Err(713, "SpecifiedArrayIndexInvalid");
                            var m = list[idx];
                            return Soap("GetGenericPortMappingEntry", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + m.ExternalPort + "</NewExternalPort><NewProtocol>" + m.Proto + "</NewProtocol><NewInternalPort>" + m.InternalPort + "</NewInternalPort><NewInternalClient>" + m.InternalClient + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + m.Description + "</NewPortMappingDescription><NewLeaseDuration>" + m.Lease + "</NewLeaseDuration>");
                        }
                    case "DeletePortMapping":
                        {
                            int ext;
                            int.TryParse(call.Arg("NewExternalPort"), out ext);
                            string key = call.Arg("NewProtocol") + ":" + ext;
                            if (!_mappings.Remove(key)) return Err(714, "NoSuchEntryInArray");
                            return Soap("DeletePortMapping", "");
                        }
                    default:
                        return Err(401, "Invalid Action");
                }
            }
        }

        public void Dispose()
        {
            Net.Unregister(FakeUdpNetwork.SsdpGroup, OnSsdp);
            Net.Unregister(Endpoint, OnSsdp);
            Http.Dispose();
        }
    }
}
