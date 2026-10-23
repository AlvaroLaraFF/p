using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;

namespace AceEngine.Cast.Dlna
{
    public sealed class DlnaOptions
    {
        public TimeSpan Timeout = TimeSpan.FromSeconds(5);
        public int MaxDescriptionBytes = 256 * 1024;
        public int MaxSoapBytes = 128 * 1024;
        public string UserAgent = "Linux/1.0 UPnP/1.1 P/1.0";
    }

    /// <summary>
    /// Control of a UPnP MediaRenderer: ConnectionManager (what it accepts), AVTransport (what to play, play, stop, pause, state) and RenderingControl (volume). Every call is bounded in
    /// size and time and goes only to the control URLs of the description, which are on the renderer's own address.
    /// </summary>
    public sealed class DlnaClient : IDisposable
    {
        private readonly BoundedHttp _http;
        private readonly SoapClient _soap;
        private readonly DlnaOptions _o;

        public DlnaClient(DlnaOptions options = null, HttpMessageHandler handler = null)
        {
            _o = options ?? new DlnaOptions();
            _http = new BoundedHttp(handler);
            _soap = new SoapClient(_http, _o.UserAgent, _o.MaxSoapBytes, _o.Timeout);
        }

        public DlnaOptions Options { get { return _o; } }

        /// <summary>Downloads and parses a renderer's description. Throws UpnpException.</summary>
        public async Task<DlnaRenderer> FetchDescriptionAsync(Uri location, CancellationToken ct)
        {
            if (location == null || location.Scheme != "http") throw new UpnpException(UpnpErrorKind.Rejected, "LOCATION is not an http URL");
            var req = new HttpRequestMessage(HttpMethod.Get, location);
            req.Headers.TryAddWithoutValidation("User-Agent", _o.UserAgent);
            var reply = await _http.SendAsync(req, _o.MaxDescriptionBytes, _o.Timeout, ct).ConfigureAwait(false);
            if (reply.Status != 200) throw new UpnpException(UpnpErrorKind.Protocol, "the description answered HTTP " + reply.Status, 0, reply.Status);
            return DlnaDescription.Parse(reply.Body, reply.Body.Length, location);
        }

        private static KeyValuePair<string, string> A(string n, string v) { return new KeyValuePair<string, string>(n, v); }

        private static DlnaService Need(DlnaService s, string what)
        {
            if (s == null || s.ControlUrl == null) throw new UpnpException(UpnpErrorKind.Protocol, "the renderer has no " + what + " service");
            return s;
        }

        private Task<Dictionary<string, string>> Av(DlnaRenderer r, string action, params KeyValuePair<string, string>[] args)
        {
            var s = Need(r.AvTransport, "AVTransport");
            return _soap.CallAsync(s.ControlUrl, s.ServiceType, action, args, CancellationToken.None);
        }

        private Task<Dictionary<string, string>> Av(DlnaRenderer r, CancellationToken ct, string action, params KeyValuePair<string, string>[] args)
        {
            var s = Need(r.AvTransport, "AVTransport");
            return _soap.CallAsync(s.ControlUrl, s.ServiceType, action, args, ct);
        }

        private static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) && v != null ? v : ""; }

        // ---------- ConnectionManager ----------

        /// <summary>The formats the renderer says it can play (the Sink of GetProtocolInfo). Throws UpnpException.</summary>
        public async Task<List<DlnaProtocolInfo>> GetProtocolInfoAsync(DlnaRenderer r, CancellationToken ct)
        {
            var s = Need(r.ConnectionManager, "ConnectionManager");
            var d = await _soap.CallAsync(s.ControlUrl, s.ServiceType, "GetProtocolInfo", new KeyValuePair<string, string>[0], ct).ConfigureAwait(false);
            return DlnaProtocolInfo.ParseList(Get(d, "Sink"));
        }

        // ---------- AVTransport ----------

        /// <summary>SetAVTransportURI with the URL and its DIDL-Lite (see <see cref="DidlLite.Build"/>).</summary>
        public Task SetAvTransportUriAsync(DlnaRenderer r, string url, string metadata, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 1024) throw new ArgumentException("url");
            return Av(r, ct, "SetAVTransportURI", A("InstanceID", "0"), A("CurrentURI", url), A("CurrentURIMetaData", metadata ?? ""));
        }

        public Task PlayAsync(DlnaRenderer r, CancellationToken ct) { return Av(r, ct, "Play", A("InstanceID", "0"), A("Speed", "1")); }

        /// <summary>Stop; false if the renderer was not playing anything (718/701/…): not an error for whoever wants it stopped.</summary>
        public async Task<bool> StopAsync(DlnaRenderer r, CancellationToken ct)
        {
            try { await Av(r, ct, "Stop", A("InstanceID", "0")).ConfigureAwait(false); return true; }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap && (ex.ErrorCode == 701 || ex.ErrorCode == 718 || ex.ErrorCode == 501)) { return false; }
        }

        /// <summary>Pause is an optional action: false if the renderer does not have it or cannot pause what it is playing now (401, 602, 701, 501).</summary>
        public async Task<bool> PauseAsync(DlnaRenderer r, CancellationToken ct)
        {
            try { await Av(r, ct, "Pause", A("InstanceID", "0")).ConfigureAwait(false); return true; }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap && (ex.ErrorCode == 401 || ex.ErrorCode == 602 || ex.ErrorCode == 701 || ex.ErrorCode == 501 || ex.HttpStatus == 405 || ex.HttpStatus == 404)) { return false; }
        }

        public async Task<DlnaTransportInfo> GetTransportInfoAsync(DlnaRenderer r, CancellationToken ct)
        {
            var d = await Av(r, ct, "GetTransportInfo", A("InstanceID", "0")).ConfigureAwait(false);
            string st = Get(d, "CurrentTransportState");
            return new DlnaTransportInfo { RawState = Clip(st), State = DlnaTransportInfo.ParseState(st), Status = Clip(Get(d, "CurrentTransportStatus")), Speed = Clip(Get(d, "CurrentSpeed")) };
        }

        public async Task<DlnaPositionInfo> GetPositionInfoAsync(DlnaRenderer r, CancellationToken ct)
        {
            var d = await Av(r, ct, "GetPositionInfo", A("InstanceID", "0")).ConfigureAwait(false);
            return new DlnaPositionInfo { TrackUri = Clip(Get(d, "TrackURI")), RelTime = Clip(Get(d, "RelTime")), AbsTime = Clip(Get(d, "AbsTime")), RelSeconds = DlnaPositionInfo.ParseTime(Get(d, "RelTime")) };
        }

        /// <summary>The actions the renderer allows right now ("Play,Stop,Pause"); empty if it does not say.</summary>
        public async Task<List<string>> GetCurrentTransportActionsAsync(DlnaRenderer r, CancellationToken ct)
        {
            var res = new List<string>();
            try
            {
                var d = await Av(r, ct, "GetCurrentTransportActions", A("InstanceID", "0")).ConfigureAwait(false);
                foreach (var a in Get(d, "Actions").Split(',')) { var t = a.Trim(); if (t.Length > 0 && t.Length < 24 && res.Count < 16) res.Add(t); }
            }
            catch (UpnpException ex) when (ex.Kind == UpnpErrorKind.Soap) { }
            return res;
        }

        // ---------- RenderingControl ----------

        public async Task<int> GetVolumeAsync(DlnaRenderer r, CancellationToken ct)
        {
            var s = Need(r.RenderingControl, "RenderingControl");
            var d = await _soap.CallAsync(s.ControlUrl, s.ServiceType, "GetVolume", new[] { A("InstanceID", "0"), A("Channel", "Master") }, ct).ConfigureAwait(false);
            int v;
            return int.TryParse(Get(d, "CurrentVolume").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        public Task SetVolumeAsync(DlnaRenderer r, int volume, CancellationToken ct)
        {
            var s = Need(r.RenderingControl, "RenderingControl");
            if (volume < 0) volume = 0; if (volume > 100) volume = 100;
            return _soap.CallAsync(s.ControlUrl, s.ServiceType, "SetVolume", new[] { A("InstanceID", "0"), A("Channel", "Master"), A("DesiredVolume", volume.ToString(CultureInfo.InvariantCulture)) }, ct);
        }

        private static string Clip(string s) { return s != null && s.Length > 80 ? s.Substring(0, 80) : (s ?? ""); }

        public void Dispose() { _http.Dispose(); }
    }
}
