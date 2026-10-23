using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using AceEngine.Util;

namespace AceEngine.Cast.Dlna
{
    public sealed class DlnaDiscoveryOptions
    {
        public static readonly string[] MediaRendererTargets =
        {
            "urn:schemas-upnp-org:device:MediaRenderer:1",
            "urn:schemas-upnp-org:device:MediaRenderer:2",
            "urn:schemas-upnp-org:service:AVTransport:1"
        };

        public SsdpOptions Ssdp = new SsdpOptions
        {
            // MX 1 (the least UPnP allows): a television may delay its answer up to MX seconds, and the sheet is often closed within two
            Window = TimeSpan.FromSeconds(4), Retries = 3, RetryInterval = TimeSpan.FromMilliseconds(700), Mx = 1,
            SearchTargets = new List<string>(MediaRendererTargets), UseMulticast = true, UseUnicastToGateway = false, StopAfterFirstIgd = false, MaxResponses = 64
        };
        public DlnaOptions Dlna = new DlnaOptions();
        /// <summary>Renderers kept at most.</summary>
        public int MaxRenderers = 24;
        public int ParallelFetches = 4;
        /// <summary>The LOCATION must be served by the machine that answered (the source IP of the datagram). Turn off only for multihomed devices that answer from another address.</summary>
        public bool RequireLocationFromSender = true;
        /// <summary>Tests only: accept a LOCATION on 127.x.</summary>
        public bool AllowLoopback = false;
    }

    public sealed class DlnaDiscoveryResult
    {
        public readonly List<DlnaRenderer> Renderers = new List<DlnaRenderer>();
        public readonly List<string> Notes = new List<string>();
        public int Responses, Rejected, FetchFailed;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Finds the UPnP MediaRenderers of the local network: M-SEARCH (it only asks, it never announces itself, so P! stays invisible), then the description of each one. A response
    /// is taken only if its LOCATION is a local-network IPv4 address served by the sender of the datagram.
    /// </summary>
    public sealed class DlnaDiscovery
    {
        private readonly SsdpClient _ssdp;
        private readonly DlnaClient _client;
        private readonly DlnaDiscoveryOptions _o;

        public DlnaDiscovery(IUdpTransport udp = null, INetworkInfo net = null, DlnaDiscoveryOptions options = null, DlnaClient client = null)
        {
            _o = options ?? new DlnaDiscoveryOptions();
            _ssdp = new SsdpClient(udp, net);
            _client = client ?? new DlnaClient(_o.Dlna);
        }

        /// <summary>
        /// One search. Each answer is described as soon as it arrives and <paramref name="found"/> hears of each usable renderer then (on any thread), so a list can fill while the search
        /// is still listening: what was found before a cancellation has already been reported.
        /// </summary>
        public async Task<DlnaDiscoveryResult> DiscoverAsync(CancellationToken ct, Action<DlnaRenderer> found = null)
        {
            var res = new DlnaDiscoveryResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = new Run { Result = res, Found = found, Clock = sw, Gate = new SemaphoreSlim(Math.Max(1, _o.ParallelFetches)), Token = ct };
            var so = _o.Ssdp.Clone();
            so.AllowLoopbackLocation = _o.AllowLoopback;
            so.OnResponse = r => OnResponse(run, r);
            SsdpResult ssdp = null;
            Exception failure = null;
            try { ssdp = await _ssdp.DiscoverAsync(so, ct).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            Task[] pending;
            lock (res) { run.Closed = true; pending = run.Fetches.ToArray(); }
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception) when (failure != null) { }                      // the search itself was cancelled or failed: that is what is thrown
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            foreach (var e in ssdp.Errors) res.Notes.Add(e);
            res.Responses = ssdp.Responses.Count;
            foreach (var d in run.Described.OrderBy(x => x.Key)) res.Renderers.Add(d.Value);
            res.Elapsed = sw.Elapsed;
            AceLog.Info("dlna", "discovery: " + res.Responses + " responses, " + res.Renderers.Count + " renderers in " + res.Elapsed.TotalSeconds.ToString("0.0") + " s");
            return res;
        }

        private sealed class Run
        {
            public DlnaDiscoveryResult Result;
            public Action<DlnaRenderer> Found;
            public System.Diagnostics.Stopwatch Clock;
            public SemaphoreSlim Gate;
            public CancellationToken Token;
            public bool Closed;
            public readonly List<string> Locations = new List<string>();
            public readonly List<Task> Fetches = new List<Task>();
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The renderers by the order their answer arrived in.</summary>
            public readonly List<KeyValuePair<int, DlnaRenderer>> Described = new List<KeyValuePair<int, DlnaRenderer>>();
        }

        private void OnResponse(Run run, SsdpResponse r)
        {
            var res = run.Result;
            lock (res)
            {
                if (run.Closed) return;
                if (_o.RequireLocationFromSender && r.From != null && !string.Equals(r.Location.Host, r.From.Address.ToString(), StringComparison.Ordinal))
                {
                    res.Rejected++;
                    res.Notes.Add("ignored " + r.From.Address + ": its LOCATION points at " + r.Location.Host);
                    return;
                }
                if (run.Locations.Contains(r.Location.AbsoluteUri) || run.Locations.Count >= _o.MaxRenderers * 2) return;
                run.Locations.Add(r.Location.AbsoluteUri);
                int order = run.Locations.Count - 1;
                var location = r.Location;
                run.Fetches.Add(Task.Run(() => DescribeAsync(run, location, order)));       // never under this lock: the handler of a found renderer runs at its end
            }
        }

        private async Task DescribeAsync(Run run, Uri location, int order)
        {
            var res = run.Result;
            DlnaRenderer r;
            await run.Gate.WaitAsync(run.Token).ConfigureAwait(false);
            try { r = await _client.FetchDescriptionAsync(location, run.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (UpnpException ex) { lock (res) { res.FetchFailed++; res.Notes.Add("description of " + location + ": " + ex.Message); } return; }
            catch (Exception ex) { lock (res) { res.FetchFailed++; res.Notes.Add("description of " + location + ": " + ex.GetType().Name); } return; }
            finally { run.Gate.Release(); }
            if (r == null || !r.IsUsable) return;
            lock (res)
            {
                if (run.Described.Count >= _o.MaxRenderers || !run.Keys.Add(r.Udn.Length > 0 ? r.Udn : r.Location.AbsoluteUri)) return;
                run.Described.Add(new KeyValuePair<int, DlnaRenderer>(order, r));
            }
            AceLog.Info("dlna", "found '" + r.FriendlyName + "' at " + location.Host + " after " + run.Clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            var h = run.Found;
            if (h != null) { try { h(r); } catch (Exception ex) { AceLog.Warn("dlna", "found handler: " + ex.Message); } }
        }

        /// <summary>The target for the "send to" list.</summary>
        public static CastTarget ToTarget(DlnaRenderer r)
        {
            return new CastTarget
            {
                Kind = CastTargetKind.Dlna,
                Id = "dlna:" + (r.Udn.Length > 0 ? r.Udn : r.Location.Host),
                Name = r.FriendlyName.Length > 0 ? r.FriendlyName : (r.Manufacturer + " " + r.ModelName).Trim().Length > 0 ? (r.Manufacturer + " " + r.ModelName).Trim() : r.Location.Host,
                Manufacturer = r.Manufacturer, Model = r.ModelName, Address = r.Address, Renderer = r,
                Capabilities = CastCapabilities.ProgressiveTs | CastCapabilities.AnyCodec | CastCapabilities.Pause | (r.RenderingControl != null ? CastCapabilities.Volume : CastCapabilities.None),
                Provider = "dlna", LastSeenUtc = DateTime.UtcNow
            };
        }
    }
}
