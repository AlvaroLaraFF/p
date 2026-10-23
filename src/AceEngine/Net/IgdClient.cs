using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AceEngine.Net
{
    public enum UpnpErrorKind
    {
        /// <summary>The router answered with a UPnP error (ErrorCode) or an HTTP error (HttpStatus).</summary>
        Soap,
        /// <summary>Could not talk to the router (connection refused, reset...).</summary>
        Transport,
        Timeout,
        /// <summary>Response larger than the limit.</summary>
        TooLarge,
        /// <summary>The response is not what was expected (invalid XML, no WAN service...).</summary>
        Protocol,
        /// <summary>Discarded for security (URL outside the LAN, different host...).</summary>
        Rejected
    }

    /// <summary>Error talking to an IGD. ErrorCode is the UPnP code of the SOAP response (0 if none).</summary>
    public sealed class UpnpException : Exception
    {
        public UpnpErrorKind Kind { get; private set; }
        public int ErrorCode { get; private set; }
        public int HttpStatus { get; private set; }

        public UpnpException(UpnpErrorKind kind, string message) : base(message) { Kind = kind; }
        public UpnpException(UpnpErrorKind kind, string message, int errorCode, int httpStatus) : base(message) { Kind = kind; ErrorCode = errorCode; HttpStatus = httpStatus; }

        public const int ActionNotAuthorized = 606, InvalidArgs = 402, ActionFailed = 501, NoSuchEntry = 714, ArrayIndexInvalid = 713, WildcardNotPermittedExt = 716,
            ConflictInMappingEntry = 718, SamePortValuesRequired = 724, OnlyPermanentLeasesSupported = 725, ExternalPortOnlyWildcard = 727, NoPortMapsAvailable = 728, ConflictWithOtherMechanisms = 729;

        public static string DescribeCode(int code)
        {
            switch (code)
            {
                case 401: return "invalid action";
                case 402: return "invalid arguments";
                case 501: return "action failed";
                case 606: return "action not authorized";
                case 713: return "index out of range";
                case 714: return "no such mapping";
                case 715: return "wildcard not permitted in the source IP";
                case 716: return "wildcard not permitted in the external port";
                case 718: return "conflict: external port already mapped";
                case 724: return "internal and external ports must match";
                case 725: return "only permanent mappings are supported (lease 0)";
                case 726: return "only a wildcard remote IP is supported";
                case 727: return "only a wildcard external port is supported";
                case 728: return "no mappings left";
                case 729: return "conflict with another router mechanism";
                default: return "error " + code;
            }
        }

        /// <summary>Errors meaning "this router offers no port mapping here" (no point in trying more ports).</summary>
        public bool IsUnavailable { get { return Kind == UpnpErrorKind.Soap && (ErrorCode == ActionNotAuthorized || ErrorCode == InvalidArgs || ErrorCode == ActionFailed || ErrorCode == 401 || HttpStatus == 404 || HttpStatus == 403 || HttpStatus == 405); } }
    }

    /// <summary>A WANIPConnection / WANPPPConnection service of the IGD, with its control URL already resolved.</summary>
    public sealed class IgdService
    {
        public string ServiceType = "";
        public string ServiceId = "";
        public Uri ControlUrl;
        /// <summary>Preference: 0 = WANIPConnection:2, 1 = WANIPConnection:1, 2 = WANPPPConnection:1.</summary>
        public int Rank;
        public override string ToString() { return ServiceType + " @ " + ControlUrl; }
    }

    public sealed class IgdDescription
    {
        public Uri Location;
        /// <summary>Base URL against which relative paths were resolved (URLBase if valid and on the same host; otherwise LOCATION).</summary>
        public Uri BaseUrl;
        public string DeviceType = "", FriendlyName = "", Manufacturer = "", ModelName = "", ModelNumber = "";
        /// <summary>WAN connection services found (also in child devices), the preferred one first.</summary>
        public List<IgdService> Services = new List<IgdService>();

        public bool IsInternetGateway { get { return DeviceType.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) >= 0; } }

        private static readonly string[] WanTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1"
        };

        /// <summary>Parses the device's XML description. Throws UpnpException (Protocol/Rejected) if it is not an acceptable XML.</summary>
        public static IgdDescription Parse(byte[] xml, int length, Uri location)
        {
            XmlNodeLite root;
            try { root = SafeXml.Parse(xml, length); }
            catch (XmlException ex) { throw new UpnpException(UpnpErrorKind.Protocol, "invalid XML description: " + ex.Message); }
            if (!string.Equals(root.Name, "root", StringComparison.OrdinalIgnoreCase)) throw new UpnpException(UpnpErrorKind.Protocol, "the description has no <root> element");

            var d = new IgdDescription { Location = location, BaseUrl = location };
            string urlBase = root.ChildText("URLBase");
            if (!string.IsNullOrEmpty(urlBase) && urlBase.Length <= 300)
            {
                Uri b;
                // A URLBase pointing to another host is ignored: the router cannot make us talk to a third party.
                if (Uri.TryCreate(urlBase, UriKind.Absolute, out b) && b.Scheme == "http" && string.Equals(b.Host, location.Host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(b.UserInfo))
                    d.BaseUrl = b;
            }
            var device = root.Child("device");
            if (device == null) throw new UpnpException(UpnpErrorKind.Protocol, "the description has no <device>");
            d.DeviceType = device.ChildText("deviceType") ?? "";
            d.FriendlyName = Short(device.ChildText("friendlyName"));
            d.Manufacturer = Short(device.ChildText("manufacturer"));
            d.ModelName = Short(device.ChildText("modelName"));
            d.ModelNumber = Short(device.ChildText("modelNumber"));

            int order = 0;
            var found = new List<KeyValuePair<int, IgdService>>();
            CollectServices(device, d, 0, found, ref order);
            d.Services = found.OrderBy(k => k.Value.Rank).ThenBy(k => k.Key).Select(k => k.Value)
                .GroupBy(s => s.ServiceType + "|" + s.ControlUrl.AbsoluteUri).Select(g => g.First()).Take(6).ToList();
            return d;
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            if (s.Length > 80) s = s.Substring(0, 80);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c < 0x20 ? ' ' : c);
            return sb.ToString();
        }

        private static void CollectServices(XmlNodeLite device, IgdDescription d, int depth, List<KeyValuePair<int, IgdService>> found, ref int order)
        {
            if (depth > 8) return;
            var list = device.Child("serviceList");
            if (list != null)
            {
                foreach (var svc in list.Children)
                {
                    if (!string.Equals(svc.Name, "service", StringComparison.OrdinalIgnoreCase)) continue;
                    string type = svc.ChildText("serviceType");
                    string control = svc.ChildText("controlURL");
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(control)) continue;
                    int rank = -1;
                    for (int i = 0; i < WanTypes.Length; i++) if (string.Equals(type, WanTypes[i], StringComparison.OrdinalIgnoreCase)) rank = i;
                    if (rank < 0) continue;
                    Uri url = ResolveControl(d, control);
                    if (url == null) continue;
                    found.Add(new KeyValuePair<int, IgdService>(order++, new IgdService { ServiceType = WanTypes[rank], ServiceId = Short(svc.ChildText("serviceId")), ControlUrl = url, Rank = rank }));
                }
            }
            var sub = device.Child("deviceList");
            if (sub != null)
                foreach (var child in sub.Children)
                    if (string.Equals(child.Name, "device", StringComparison.OrdinalIgnoreCase)) CollectServices(child, d, depth + 1, found, ref order);
        }

        private static Uri ResolveControl(IgdDescription d, string control)
        {
            if (control.Length > 512) return null;
            foreach (char c in control) if (c < 0x21 || c > 0x7e) return null;
            Uri url;
            if (!Uri.TryCreate(d.BaseUrl, control, out url)) return null;
            if (url.Scheme != "http" || !string.IsNullOrEmpty(url.UserInfo)) return null;
            // The control URL must be on the same machine as the description: SOAP is not sent anywhere else.
            if (!string.Equals(url.Host, d.Location.Host, StringComparison.OrdinalIgnoreCase)) return null;
            return url;
        }
    }

    public sealed class IgdOptions
    {
        /// <summary>Timeout of each HTTP request (description or SOAP action), headers and body included.</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(5);
        public int MaxDescriptionBytes = 256 * 1024;
        public int MaxSoapBytes = 64 * 1024;
        public string UserAgent = "Linux/1.0 UPnP/1.1 P/1.0";
    }

    /// <summary>An entry of the router's mapping table.</summary>
    public sealed class PortMappingEntry
    {
        public int ExternalPort, InternalPort, LeaseDuration;
        public string Protocol = "", InternalClient = "", Description = "";
        public bool Enabled;
    }

    /// <summary>
    /// SOAP client for the IGD (UPnP Internet Gateway Device v1/v2): downloads the description and calls GetExternalIPAddress, AddPortMapping,
    /// GetSpecificPortMappingEntry, GetGenericPortMappingEntry and DeletePortMapping. Everything is bounded in size and time.
    /// </summary>
    public sealed class IgdClient : IDisposable
    {
        private readonly BoundedHttp _http;
        private readonly SoapClient _soap;
        private readonly IgdOptions _o;

        public IgdClient(IgdOptions options = null, HttpMessageHandler handler = null)
        {
            _o = options ?? new IgdOptions();
            _http = new BoundedHttp(handler);
            _soap = new SoapClient(_http, _o.UserAgent, _o.MaxSoapBytes, _o.Timeout);
        }

        public IgdOptions Options { get { return _o; } }

        /// <summary>Downloads and parses the device description.</summary>
        public async Task<IgdDescription> FetchDescriptionAsync(Uri location, CancellationToken ct)
        {
            if (location == null || location.Scheme != "http") throw new UpnpException(UpnpErrorKind.Rejected, "LOCATION is not an http URL");
            var req = new HttpRequestMessage(HttpMethod.Get, location);
            AddCommonHeaders(req);
            var reply = await _http.SendAsync(req, _o.MaxDescriptionBytes, _o.Timeout, ct).ConfigureAwait(false);
            if (reply.Status != 200) throw new UpnpException(UpnpErrorKind.Protocol, "the description answered HTTP " + reply.Status, 0, reply.Status);
            return IgdDescription.Parse(reply.Body, reply.Body.Length, location);
        }

        /// <summary>External IP of the router (may be 0.0.0.0 if the WAN is not connected). Throws UpnpException.</summary>
        public async Task<IPAddress> GetExternalIPAddressAsync(IgdService svc, CancellationToken ct)
        {
            var r = await CallAsync(svc, "GetExternalIPAddress", new KeyValuePair<string, string>[0], ct).ConfigureAwait(false);
            string v;
            IPAddress ip;
            if (!r.TryGetValue("NewExternalIPAddress", out v) || !IPAddress.TryParse((v ?? "").Trim(), out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new UpnpException(UpnpErrorKind.Protocol, "GetExternalIPAddress without a valid IP");
            return ip;
        }

        /// <summary>AddPortMapping (empty RemoteHost = any, enabled). <paramref name="protocol"/>: "TCP" or "UDP". leaseSeconds 0 = permanent.</summary>
        public async Task AddPortMappingAsync(IgdService svc, string protocol, int externalPort, IPAddress internalClient, int internalPort, string description, int leaseSeconds, CancellationToken ct)
        {
            CheckPort(externalPort); CheckPort(internalPort);
            await CallAsync(svc, "AddPortMapping", new[]
            {
                A("NewRemoteHost", ""),
                A("NewExternalPort", externalPort),
                A("NewProtocol", Proto(protocol)),
                A("NewInternalPort", internalPort),
                A("NewInternalClient", internalClient.ToString()),
                A("NewEnabled", "1"),
                A("NewPortMappingDescription", description ?? ""),
                A("NewLeaseDuration", Math.Max(0, leaseSeconds))
            }, ct).ConfigureAwait(false);
        }

        /// <summary>Returns the mapping or null if it does not exist (error 714).</summary>
        public async Task<PortMappingEntry> GetSpecificPortMappingEntryAsync(IgdService svc, string protocol, int externalPort, CancellationToken ct)
        {
            CheckPort(externalPort);
            Dictionary<string, string> r;
            try
            {
                r = await CallAsync(svc, "GetSpecificPortMappingEntry", new[] { A("NewRemoteHost", ""), A("NewExternalPort", externalPort), A("NewProtocol", Proto(protocol)) }, ct).ConfigureAwait(false);
            }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap && ex.ErrorCode == UpnpException.NoSuchEntry) { return null; }
            var e = new PortMappingEntry { ExternalPort = externalPort, Protocol = Proto(protocol) };
            e.InternalClient = Get(r, "NewInternalClient");
            e.InternalPort = ToInt(Get(r, "NewInternalPort"));
            e.Enabled = Get(r, "NewEnabled").Trim() != "0";
            e.Description = Get(r, "NewPortMappingDescription");
            e.LeaseDuration = ToInt(Get(r, "NewLeaseDuration"));
            return e;
        }

        /// <summary>Entry number <paramref name="index"/> of the table (null if the index is out of range: 713/714).</summary>
        public async Task<PortMappingEntry> GetGenericPortMappingEntryAsync(IgdService svc, int index, CancellationToken ct)
        {
            Dictionary<string, string> r;
            try { r = await CallAsync(svc, "GetGenericPortMappingEntry", new[] { A("NewPortMappingIndex", index) }, ct).ConfigureAwait(false); }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap && (ex.ErrorCode == UpnpException.ArrayIndexInvalid || ex.ErrorCode == UpnpException.NoSuchEntry)) { return null; }
            var e = new PortMappingEntry();
            e.ExternalPort = ToInt(Get(r, "NewExternalPort"));
            e.Protocol = Get(r, "NewProtocol");
            e.InternalClient = Get(r, "NewInternalClient");
            e.InternalPort = ToInt(Get(r, "NewInternalPort"));
            e.Enabled = Get(r, "NewEnabled").Trim() != "0";
            e.Description = Get(r, "NewPortMappingDescription");
            e.LeaseDuration = ToInt(Get(r, "NewLeaseDuration"));
            return e;
        }

        /// <summary>Deletes the mapping. Returns false if it no longer existed (714); throws UpnpException on any other error.</summary>
        public async Task<bool> DeletePortMappingAsync(IgdService svc, string protocol, int externalPort, CancellationToken ct)
        {
            CheckPort(externalPort);
            try
            {
                await CallAsync(svc, "DeletePortMapping", new[] { A("NewRemoteHost", ""), A("NewExternalPort", externalPort), A("NewProtocol", Proto(protocol)) }, ct).ConfigureAwait(false);
                return true;
            }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap && ex.ErrorCode == UpnpException.NoSuchEntry) { return false; }
        }

        // ---------- SOAP ----------

        private static KeyValuePair<string, string> A(string name, string value) { return new KeyValuePair<string, string>(name, value); }
        private static KeyValuePair<string, string> A(string name, int value) { return new KeyValuePair<string, string>(name, value.ToString(CultureInfo.InvariantCulture)); }

        private static string Proto(string p)
        {
            if (string.Equals(p, "TCP", StringComparison.OrdinalIgnoreCase)) return "TCP";
            if (string.Equals(p, "UDP", StringComparison.OrdinalIgnoreCase)) return "UDP";
            throw new ArgumentException("protocol " + p);
        }

        private static void CheckPort(int p) { if (p < 1 || p > 65535) throw new ArgumentOutOfRangeException("port", p, "port out of range"); }
        private static string Get(Dictionary<string, string> r, string key) { string v; return r.TryGetValue(key, out v) && v != null ? v : ""; }
        private static int ToInt(string s) { int v; return int.TryParse((s ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : 0; }

        private void AddCommonHeaders(HttpRequestMessage req)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", _o.UserAgent);
        }

        /// <summary>SOAP envelope of an action (public so tests can check the exact format).</summary>
        public static string BuildEnvelope(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> args)
        {
            return SoapClient.BuildEnvelope(serviceType, action, args);
        }

        private Task<Dictionary<string, string>> CallAsync(IgdService svc, string action, KeyValuePair<string, string>[] args, CancellationToken ct)
        {
            if (svc == null || svc.ControlUrl == null) throw new UpnpException(UpnpErrorKind.Protocol, "no WAN service");
            return _soap.CallAsync(svc.ControlUrl, svc.ServiceType, action, args, ct);
        }

        public void Dispose() { _http.Dispose(); }
    }
}
