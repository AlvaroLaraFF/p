using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AceEngine.Tests.NetTests;

namespace AceEngine.Tests.Cast
{
    internal sealed class RendererCall
    {
        public string Action = "";
        public Dictionary<string, string> Args = new Dictionary<string, string>();
        public string Path = "", SoapAction = "";
    }

    /// <summary>
    /// A UPnP MediaRenderer on the loopback: description, ConnectionManager, AVTransport and RenderingControl over a real HTTP server, and what a TV does on Play (it fetches the
    /// URL with its own HTTP client, checks the Content-Type it gets and reports PLAYING once bytes flow).
    /// </summary>
    internal sealed class FakeRenderer : IDisposable
    {
        public readonly FakeHttpServer Http = new FakeHttpServer();
        public string Manufacturer = "Acme", Model = "TV-1", Name = "[TV] prueba", Udn = "uuid:fake-renderer-1";
        /// <summary>MIME types listed in GetProtocolInfo.</summary>
        public HashSet<string> SinkMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp2t", "video/mpeg" };
        /// <summary>The TV refuses (714) a SetAVTransportURI whose DIDL has another MIME type, and a stream that comes with another Content-Type.</summary>
        public HashSet<string> AcceptMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp2t", "video/mpeg" };
        public bool SupportPause = true;
        /// <summary>On Play the TV fetches the URL. false = it never does (a TV that ignores the URL).</summary>
        public bool FetchStream = true;
        public int SetUriFault;
        public bool RequireDlnaHeaders = true;
        public volatile string State = "NO_MEDIA_PRESENT";
        public volatile string Status = "OK";
        public int Volume = 20;
        public string LastUri = "", LastMetadata = "";
        public readonly ConcurrentQueue<RendererCall> Calls = new ConcurrentQueue<RendererCall>();
        public readonly ConcurrentQueue<string> StreamContentTypes = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<string> StreamFeatures = new ConcurrentQueue<string>();
        public long BytesFetched;
        private CancellationTokenSource _play;
        private readonly object _gate = new object();

        public FakeRenderer() { Http.Handler = Handle; }

        public Uri Location { get { return new Uri("http://127.0.0.1:" + Http.Port + "/dmr"); } }
        public int Count(string action) { return Calls.Count(c => c.Action == action); }
        public RendererCall Last(string action) { return Calls.LastOrDefault(c => c.Action == action); }

        private FakeHttpResponse Handle(FakeHttpRequest req)
        {
            if (req.Method == "GET" && req.Path == "/dmr") return FakeHttpResponse.Xml(Description());
            if (req.Method != "POST") return new FakeHttpResponse { Status = 404, Reason = "Not Found" };
            string service = req.Path.Contains("AVTransport") ? "urn:schemas-upnp-org:service:AVTransport:1" : req.Path.Contains("RenderingControl") ? "urn:schemas-upnp-org:service:RenderingControl:1" : "urn:schemas-upnp-org:service:ConnectionManager:1";
            RendererCall call;
            try { call = ParseCall(req); }
            catch (Exception) { return Fault(401, "Invalid Action"); }
            Calls.Enqueue(call);
            switch (call.Action)
            {
                case "GetProtocolInfo":
                    return Ok(service, "GetProtocolInfo", "<Source></Source><Sink>" + string.Join(",", SinkMimes.Select(m => "http-get:*:" + m + ":DLNA.ORG_PN=X;DLNA.ORG_FLAGS=ED100000000000000000000000000000")) + ",http-get:*:image/jpeg:*</Sink>");
                case "SetAVTransportURI":
                    {
                        if (SetUriFault != 0) return Fault(SetUriFault, "fault");
                        string meta = call.Args.ContainsKey("CurrentURIMetaData") ? call.Args["CurrentURIMetaData"] : "";
                        string mime = null;
                        try
                        {
                            var d = XDocument.Parse(meta);
                            var res = d.Descendants().First(e => e.Name.LocalName == "res");
                            var pi = (string)res.Attribute("protocolInfo");
                            mime = pi.Split(':')[2];
                        }
                        catch (Exception) { return Fault(402, "Invalid Args"); }
                        if (!AcceptMimes.Contains(mime)) return Fault(714, "Illegal MIME-Type");
                        LastUri = call.Args["CurrentURI"]; LastMetadata = meta;
                        State = "STOPPED";
                        return Ok(service, "SetAVTransportURI", "");
                    }
                case "Play":
                    StartPlaying();
                    return Ok(service, "Play", "");
                case "Stop":
                    StopPlaying();
                    return Ok(service, "Stop", "");
                case "Pause":
                    if (!SupportPause) return Fault(401, "Invalid Action");
                    lock (_gate) { if (State == "PLAYING") State = "PAUSED_PLAYBACK"; }
                    return Ok(service, "Pause", "");
                case "GetTransportInfo":
                    return Ok(service, "GetTransportInfo", "<CurrentTransportState>" + State + "</CurrentTransportState><CurrentTransportStatus>" + Status + "</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>");
                case "GetPositionInfo":
                    return Ok(service, "GetPositionInfo", "<Track>1</Track><TrackURI>" + System.Security.SecurityElement.Escape(LastUri) + "</TrackURI><RelTime>00:00:12</RelTime><AbsTime>NOT_IMPLEMENTED</AbsTime>");
                case "GetVolume":
                    return Ok(service, "GetVolume", "<CurrentVolume>" + Volume + "</CurrentVolume>");
                case "SetVolume":
                    Volume = int.Parse(call.Args["DesiredVolume"]);
                    return Ok(service, "SetVolume", "");
                default:
                    return Fault(401, "Invalid Action");
            }
        }

        private void StartPlaying()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_play != null) _play.Cancel();
                cts = _play = new CancellationTokenSource();
                State = "TRANSITIONING";
            }
            if (!FetchStream) return;
            string url = LastUri;
            var ignored = Task.Run(async () =>
            {
                try
                {
                    using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                    {
                        // a TV probes with HEAD first
                        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cts.Token).ConfigureAwait(false);
                        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            string ct = resp.Content.Headers.ContentType == null ? "" : resp.Content.Headers.ContentType.MediaType;
                            StreamContentTypes.Enqueue(ct);
                            IEnumerable<string> f;
                            StreamFeatures.Enqueue(resp.Headers.TryGetValues("contentFeatures.dlna.org", out f) ? f.First() : "");
                            if (!resp.IsSuccessStatusCode || !AcceptMimes.Contains(ct) || (RequireDlnaHeaders && !resp.Headers.Contains("transferMode.dlna.org")))
                            {
                                State = "STOPPED"; Status = "ERROR_OCCURRED"; return;
                            }
                            using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                var buf = new byte[32 * 1024];
                                long got = 0;
                                while (true)
                                {
                                    int n = await s.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                    if (n <= 0) break;
                                    got += n; Interlocked.Add(ref BytesFetched, n);
                                    if (got > 188 * 40 && State == "TRANSITIONING") State = "PLAYING";
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
                finally { if (!cts.IsCancellationRequested && State != "PAUSED_PLAYBACK") State = "STOPPED"; }
            });
        }

        public void StopPlaying()
        {
            lock (_gate) { if (_play != null) { _play.Cancel(); _play = null; } State = "STOPPED"; }
        }

        /// <summary>The user presses Stop on the TV's own remote.</summary>
        public void UserStops() { StopPlaying(); }

        private string Description()
        {
            return "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion><device>"
                + "<deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>" + Name + "</friendlyName><manufacturer>" + Manufacturer + "</manufacturer><modelName>" + Model + "</modelName><UDN>" + Udn + "</UDN>"
                + "<serviceList>"
                + "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/upnp/control/RenderingControl1</controlURL></service>"
                + "<service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><controlURL>/upnp/control/ConnectionManager1</controlURL></service>"
                + "<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/upnp/control/AVTransport1</controlURL></service>"
                + "</serviceList></device></root>";
        }

        private static RendererCall ParseCall(FakeHttpRequest req)
        {
            var doc = XDocument.Parse(req.BodyText);
            var body = doc.Descendants().First(e => e.Name.LocalName == "Body");
            var action = body.Elements().First();
            var c = new RendererCall { Action = action.Name.LocalName, Path = req.Path, SoapAction = req.Header("SOAPACTION") ?? "" };
            foreach (var a in action.Elements()) c.Args[a.Name.LocalName] = a.Value;
            return c;
        }

        private static FakeHttpResponse Ok(string service, string action, string inner)
        {
            return FakeHttpResponse.Xml("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + "Response xmlns:u=\"" + service + "\">" + inner + "</u:" + action + "Response></s:Body></s:Envelope>");
        }

        private static FakeHttpResponse Fault(int code, string desc)
        {
            return FakeHttpResponse.Xml("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>" + desc + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>", 500);
        }

        public void Dispose() { StopPlaying(); Http.Dispose(); }
    }
}
