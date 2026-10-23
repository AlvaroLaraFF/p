using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;
using AceEngine.Net;

namespace AceEngine.Cast.Dlna
{
    /// <summary>A UPnP service of a renderer with its control URL already resolved.</summary>
    public sealed class DlnaService
    {
        public string ServiceType = "";
        public Uri ControlUrl;
        public override string ToString() { return ServiceType + " @ " + ControlUrl; }
    }

    /// <summary>A UPnP MediaRenderer as its description announces it. Everything in it came from the LAN and is bounded and cleaned.</summary>
    public sealed class DlnaRenderer
    {
        public Uri Location;
        public string Udn = "", FriendlyName = "", Manufacturer = "", ModelName = "", ModelNumber = "", DeviceType = "";
        public DlnaService AvTransport, RenderingControl, ConnectionManager;
        public IPAddress Address { get { IPAddress a; return Location != null && IPAddress.TryParse(Location.Host, out a) ? a : null; } }
        public bool IsUsable { get { return AvTransport != null; } }
        public override string ToString() { return FriendlyName + " [" + Manufacturer + " " + ModelName + "] " + Location; }
    }

    /// <summary>One entry of the renderer's GetProtocolInfo Sink list: protocol:network:contentFormat:additionalInfo.</summary>
    public sealed class DlnaProtocolInfo
    {
        public string Protocol = "", Network = "", ContentFormat = "", AdditionalInfo = "";
        public override string ToString() { return Protocol + ":" + Network + ":" + ContentFormat + ":" + AdditionalInfo; }

        public static List<DlnaProtocolInfo> ParseList(string csv)
        {
            var res = new List<DlnaProtocolInfo>();
            if (string.IsNullOrWhiteSpace(csv)) return res;
            foreach (var raw in csv.Split(','))
            {
                var p = raw.Trim().Split(new[] { ':' }, 4);
                if (p.Length < 3) continue;
                if (res.Count >= 512) break;
                res.Add(new DlnaProtocolInfo { Protocol = p[0], Network = p[1], ContentFormat = p[2].ToLowerInvariant(), AdditionalInfo = p.Length > 3 ? p[3] : "" });
            }
            return res;
        }
    }

    public enum DlnaTransportState { Unknown, Stopped, Playing, PausedPlayback, Transitioning, NoMediaPresent, PausedRecording, Recording }

    public sealed class DlnaTransportInfo
    {
        public DlnaTransportState State;
        public string RawState = "", Status = "", Speed = "";
        public bool IsError { get { return string.Equals(Status, "ERROR_OCCURRED", StringComparison.OrdinalIgnoreCase); } }

        public static DlnaTransportState ParseState(string s)
        {
            switch ((s ?? "").Trim().ToUpperInvariant())
            {
                case "STOPPED": return DlnaTransportState.Stopped;
                case "PLAYING": return DlnaTransportState.Playing;
                case "PAUSED_PLAYBACK": return DlnaTransportState.PausedPlayback;
                case "TRANSITIONING": return DlnaTransportState.Transitioning;
                case "NO_MEDIA_PRESENT": return DlnaTransportState.NoMediaPresent;
                case "PAUSED_RECORDING": return DlnaTransportState.PausedRecording;
                case "RECORDING": return DlnaTransportState.Recording;
                default: return DlnaTransportState.Unknown;
            }
        }
    }

    public sealed class DlnaPositionInfo
    {
        public string TrackUri = "", RelTime = "", AbsTime = "";
        /// <summary>RelTime in seconds, -1 if the renderer gives none or "NOT_IMPLEMENTED".</summary>
        public double RelSeconds = -1;

        public static double ParseTime(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return -1;
            var p = t.Trim().Split(':');
            if (p.Length != 3) return -1;
            double h, m, s;
            if (!double.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out h) || !double.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out m)
                || !double.TryParse(p[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out s)) return -1;
            if (h < 0) return -1;
            return h * 3600 + m * 60 + s;
        }
    }

    /// <summary>Reads the XML description of a renderer (untrusted input).</summary>
    public static class DlnaDescription
    {
        public const string AvTransportType = "urn:schemas-upnp-org:service:AVTransport:";
        public const string RenderingControlType = "urn:schemas-upnp-org:service:RenderingControl:";
        public const string ConnectionManagerType = "urn:schemas-upnp-org:service:ConnectionManager:";

        /// <summary>
        /// Parses the description. The control URLs must be on the same host as LOCATION (nothing is sent anywhere else); a URLBase on another host is ignored. Throws UpnpException
        /// (Protocol) if the XML is not acceptable. A description without a MediaRenderer is returned too (IsUsable is false).
        /// </summary>
        public static DlnaRenderer Parse(byte[] xml, int length, Uri location)
        {
            XmlNodeLite root;
            try { root = SafeXml.Parse(xml, length); }
            catch (XmlException ex) { throw new UpnpException(UpnpErrorKind.Protocol, "invalid XML description: " + ex.Message); }
            if (!string.Equals(root.Name, "root", StringComparison.OrdinalIgnoreCase)) throw new UpnpException(UpnpErrorKind.Protocol, "the description has no <root> element");
            var device = root.Child("device");
            if (device == null) throw new UpnpException(UpnpErrorKind.Protocol, "the description has no <device>");

            var r = new DlnaRenderer { Location = location };
            Uri baseUrl = location;
            string urlBase = root.ChildText("URLBase");
            if (!string.IsNullOrEmpty(urlBase) && urlBase.Length <= 300)
            {
                Uri b;
                if (Uri.TryCreate(urlBase, UriKind.Absolute, out b) && b.Scheme == "http" && string.Equals(b.Host, location.Host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(b.UserInfo)) baseUrl = b;
            }
            // the MediaRenderer device (the root or an embedded one) gives the names; the services are collected from the whole tree
            var renderer = FindRenderer(device, 0) ?? device;
            r.DeviceType = Short(renderer.ChildText("deviceType"));
            r.FriendlyName = Short(renderer.ChildText("friendlyName"));
            if (r.FriendlyName.Length == 0) r.FriendlyName = Short(device.ChildText("friendlyName"));
            r.Manufacturer = Short(renderer.ChildText("manufacturer"));
            r.ModelName = Short(renderer.ChildText("modelName"));
            r.ModelNumber = Short(renderer.ChildText("modelNumber"));
            r.Udn = Short(renderer.ChildText("UDN"));
            if (r.Udn.Length == 0) r.Udn = Short(device.ChildText("UDN"));
            bool isRenderer = FindRenderer(device, 0) != null;
            Collect(device, location, baseUrl, r, 0);
            if (!isRenderer) { r.AvTransport = null; }                                       // an AVTransport without a MediaRenderer is something else (a media server)
            return r;
        }

        private static XmlNodeLite FindRenderer(XmlNodeLite device, int depth)
        {
            if (depth > 8) return null;
            string t = device.ChildText("deviceType") ?? "";
            if (t.IndexOf(":device:MediaRenderer:", StringComparison.OrdinalIgnoreCase) >= 0) return device;
            var sub = device.Child("deviceList");
            if (sub != null)
                foreach (var c in sub.Children)
                    if (string.Equals(c.Name, "device", StringComparison.OrdinalIgnoreCase)) { var f = FindRenderer(c, depth + 1); if (f != null) return f; }
            return null;
        }

        private static void Collect(XmlNodeLite device, Uri location, Uri baseUrl, DlnaRenderer r, int depth)
        {
            if (depth > 8) return;
            var list = device.Child("serviceList");
            if (list != null)
                foreach (var svc in list.Children)
                {
                    if (!string.Equals(svc.Name, "service", StringComparison.OrdinalIgnoreCase)) continue;
                    string type = svc.ChildText("serviceType");
                    string control = svc.ChildText("controlURL");
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(control)) continue;
                    var url = Resolve(location, baseUrl, control);
                    if (url == null) continue;
                    var s = new DlnaService { ServiceType = Short(type), ControlUrl = url };
                    if (Starts(type, AvTransportType)) { if (r.AvTransport == null || Version(type) > Version(r.AvTransport.ServiceType)) r.AvTransport = s; }
                    else if (Starts(type, RenderingControlType)) { if (r.RenderingControl == null) r.RenderingControl = s; }
                    else if (Starts(type, ConnectionManagerType)) { if (r.ConnectionManager == null) r.ConnectionManager = s; }
                }
            var sub = device.Child("deviceList");
            if (sub != null)
                foreach (var c in sub.Children)
                    if (string.Equals(c.Name, "device", StringComparison.OrdinalIgnoreCase)) Collect(c, location, baseUrl, r, depth + 1);
        }

        private static bool Starts(string s, string prefix) { return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); }

        private static int Version(string type)
        {
            int i = type.LastIndexOf(':'); int v;
            return i >= 0 && int.TryParse(type.Substring(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static Uri Resolve(Uri location, Uri baseUrl, string control)
        {
            if (control.Length > 512) return null;
            foreach (char c in control) if (c < 0x21 || c > 0x7e) return null;
            Uri url;
            if (!Uri.TryCreate(baseUrl, control, out url)) return null;
            if (url.Scheme != "http" || !string.IsNullOrEmpty(url.UserInfo)) return null;
            if (!string.Equals(url.Host, location.Host, StringComparison.OrdinalIgnoreCase)) return null;      // control only on the machine that announced itself
            return url;
        }

        internal static string Short(string s)
        {
            if (s == null) return "";
            if (s.Length > 100) s = s.Substring(0, 100);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c < 0x20 || c == 0x7f ? ' ' : c);
            return sb.ToString().Trim();
        }
    }

    /// <summary>DIDL-Lite metadata for SetAVTransportURI.</summary>
    public static class DidlLite
    {
        public const string ClassVideoItem = "object.item.videoItem";

        /// <summary>
        /// The metadata of a live channel: one item with its title, class and a res with the protocolInfo. Everything that goes in is escaped (the title is the channel's name, which
        /// comes from a list). The caller passes the result as the CurrentURIMetaData argument, which the SOAP layer escapes once more.
        /// </summary>
        public static string Build(string title, string url, string mimeType, string dlnaFeatures)
        {
            string protocolInfo = "http-get:*:" + mimeType + ":" + (string.IsNullOrEmpty(dlnaFeatures) ? "*" : dlnaFeatures);
            var sb = new StringBuilder();
            sb.Append("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\">");
            sb.Append("<item id=\"0\" parentID=\"-1\" restricted=\"1\">");
            sb.Append("<dc:title>").Append(Esc(Title(title))).Append("</dc:title>");
            sb.Append("<upnp:class>").Append(ClassVideoItem).Append("</upnp:class>");
            sb.Append("<res protocolInfo=\"").Append(Esc(protocolInfo)).Append("\">").Append(Esc(url)).Append("</res>");
            sb.Append("</item></DIDL-Lite>");
            return sb.ToString();
        }

        private static string Title(string t)
        {
            t = XmlChars.Clean(t ?? "");
            if (t.Length > 120) t = t.Substring(0, 120);
            t = t.Trim();
            return t.Length == 0 ? "P!" : t;
        }

        /// <summary>XML text and attribute escaping.</summary>
        public static string Esc(string s)
        {
            return System.Security.SecurityElement.Escape(XmlChars.Clean(s)) ?? "";
        }
    }
}
