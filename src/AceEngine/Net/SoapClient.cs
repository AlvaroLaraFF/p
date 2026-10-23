using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AceEngine.Net
{
    /// <summary>
    /// UPnP SOAP client (control of any service): builds the envelope, sends it with the SOAPACTION header through <see cref="BoundedHttp"/> (no proxy, no redirects, bounded size
    /// and time) and reads the response as untrusted XML. Shared by the IGD client and the DLNA renderer control.
    /// </summary>
    internal sealed class SoapClient
    {
        private readonly BoundedHttp _http;
        private readonly string _userAgent;
        private readonly int _maxBytes;
        private readonly TimeSpan _timeout;

        public SoapClient(BoundedHttp http, string userAgent, int maxBytes, TimeSpan timeout)
        {
            _http = http; _userAgent = userAgent; _maxBytes = maxBytes; _timeout = timeout;
        }

        /// <summary>SOAP envelope of an action. Argument values are XML-escaped; names are the caller's (never external data). Characters XML cannot carry are dropped.</summary>
        public static string BuildEnvelope(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> args)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\r\n");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");
            foreach (var kv in args)
                sb.Append('<').Append(kv.Key).Append('>').Append(SecurityElement.Escape(XmlChars.Clean(kv.Value)) ?? "").Append("</").Append(kv.Key).Append('>');
            sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            return sb.ToString();
        }

        /// <summary>Calls the action and returns the out arguments. Throws UpnpException (Soap with the UPnP error code, Protocol, Transport, Timeout, TooLarge).</summary>
        public async Task<Dictionary<string, string>> CallAsync(Uri controlUrl, string serviceType, string action, IEnumerable<KeyValuePair<string, string>> args, CancellationToken ct)
        {
            if (controlUrl == null) throw new UpnpException(UpnpErrorKind.Protocol, "no control URL");
            var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            req.Headers.TryAddWithoutValidation("SOAPACTION", "\"" + serviceType + "#" + action + "\"");
            var content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(BuildEnvelope(serviceType, action, args)));
            content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
            req.Content = content;
            var reply = await _http.SendAsync(req, _maxBytes, _timeout, ct).ConfigureAwait(false);
            return ParseResponse(action, reply.Status, reply.Body);
        }

        /// <summary>Reads the response of an action (public to the tests so the fuzzing hits it without a network).</summary>
        internal static Dictionary<string, string> ParseResponse(string action, int status, byte[] body)
        {
            XmlNodeLite root = null;
            if (body != null && body.Length > 0)
            {
                try { root = SafeXml.Parse(body, body.Length); }
                catch (XmlException ex)
                {
                    if (status == 200) throw new UpnpException(UpnpErrorKind.Protocol, action + ": invalid XML response: " + ex.Message);
                }
            }
            var err = root == null ? null : root.Find("errorCode");
            if (status != 200 || (root != null && root.Find("Fault") != null))
            {
                int code = 0;
                if (err != null) int.TryParse(err.Text, NumberStyles.None, CultureInfo.InvariantCulture, out code);
                string desc = root == null ? null : root.Find("errorDescription") == null ? null : root.Find("errorDescription").Text;
                if (desc != null && desc.Length > 80) desc = desc.Substring(0, 80);
                throw new UpnpException(UpnpErrorKind.Soap, action + ": " + (code != 0 ? UpnpException.DescribeCode(code) : "HTTP " + status) + (string.IsNullOrEmpty(desc) ? "" : " (" + desc + ")"), code, status);
            }
            if (root == null) throw new UpnpException(UpnpErrorKind.Protocol, action + ": empty response");
            var resp = root.Find(action + "Response");
            if (resp == null) throw new UpnpException(UpnpErrorKind.Protocol, action + ": missing <" + action + "Response>");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in resp.Children) if (!result.ContainsKey(c.Name)) result[c.Name] = c.Text;
            return result;
        }
    }

    /// <summary>Text going into XML.</summary>
    internal static class XmlChars
    {
        /// <summary>Drops what XML 1.0 cannot carry (control characters other than tab, line feed and carriage return, lone surrogates, U+FFFE and U+FFFF).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
                if (!ok && char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    if (sb != null) sb.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }
                if (ok) { if (sb != null) sb.Append(c); continue; }
                if (sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
            }
            return sb == null ? s : sb.ToString();
        }
    }
}
