using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast.Dlna;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cast
{
    public sealed class CastServiceOptions
    {
        public CastSourceOptions Source = new CastSourceOptions();
        public DlnaDiscoveryOptions Discovery = new DlnaDiscoveryOptions();
        /// <summary>How long a renderer has, after Play, to say PLAYING and to ask for the stream before the next variant is tried.</summary>
        public TimeSpan PlayWait = TimeSpan.FromSeconds(12);
        public TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        /// <summary>While sending to a renderer, how often its state is checked to notice that the user stopped it from the TV.</summary>
        public TimeSpan WatchInterval = TimeSpan.FromSeconds(3);
        /// <summary>Grace after the renderer starts playing before a STOPPED state is taken as "the user stopped it".</summary>
        public TimeSpan WatchGrace = TimeSpan.FromSeconds(15);
        public int MaxVariants = 4;
        public DlnaOptions Dlna = new DlnaOptions();
        /// <summary>
        /// A renderer that answered a search and does not in the next one stays in the list this long (a television that misses one M-SEARCH does not flicker out of the picker while it is
        /// open and being refreshed). Zero = each search replaces the list with what it found.
        /// </summary>
        public TimeSpan DlnaRetention = TimeSpan.Zero;
    }

    /// <summary>
    /// The engine's "send to" service: ONE list of screens (<see cref="Targets"/>), and one send/stop pair that works for every kind. DLNA screens are found and controlled here; Google Cast
    /// screens are found by the Cast SDK of the Android shell, added with <c>Targets.Update("googlecast", …)</c>, and for them <see cref="SendAsync"/> prepares the HLS and returns the URL
    /// (the shell does the LOAD, see <see cref="ICastSource"/>).
    /// </summary>
    public sealed class CastService : IDisposable
    {
        private const string Tag = "cast";
        private readonly Func<AceEngineHost> _host;
        private readonly INetworkInfo _net;
        private readonly IUdpTransport _udp;
        private readonly CastServiceOptions _o;
        private readonly CastSource _source;
        private readonly DlnaClient _dlna;
        private readonly object _gate = new object();
        private CancellationTokenSource _sendCts;
        private LiveSession _dlnaSession;
        private CastTarget _target;
        private DlnaVariant _variant;
        private Timer _watch;
        private long _playingSinceTicks;
        private int _stoppedPolls;
        private CastState _state = CastState.Idle;
        private string _reason = "";
        private bool _disposed;
        private readonly Dictionary<string, CastTarget> _dlnaSeen = new Dictionary<string, CastTarget>();
        private readonly object _dlnaPublish = new object();

        public CastService(Func<AceEngineHost> host, INetworkInfo net = null, IUdpTransport udp = null, CastServiceOptions options = null, DlnaClient dlna = null)
        {
            _host = host; _net = net ?? SystemNetworkInfo.Default; _udp = udp;
            _o = options ?? new CastServiceOptions();
            _source = new CastSource(host, _net, _o.Source);
            _dlna = dlna ?? new DlnaClient(_o.Dlna);
            Targets = new CastTargetList();
        }

        /// <summary>The list the interface shows.</summary>
        public CastTargetList Targets { get; private set; }
        /// <summary>The Cast source underneath (statistics, verdicts).</summary>
        public CastSource Source { get { return _source; } }
        public CastState State { get { lock (_gate) return _state; } }
        public string Reason { get { lock (_gate) return _reason; } }
        public CastTarget CurrentTarget { get { lock (_gate) return _target; } }
        public DlnaVariant CurrentVariant { get { lock (_gate) return _variant; } }
        public event Action<CastState> StateChanged;

        private void SetState(CastState s, string reason = "")
        {
            Action<CastState> h;
            lock (_gate) { if (_state == s && _reason == reason) return; _state = s; _reason = reason; h = StateChanged; }
            AceLog.Info(Tag, "service state: " + s + (reason.Length > 0 ? " (" + reason + ")" : ""));
            if (h != null) { try { h(s); } catch (Exception ex) { AceLog.Warn(Tag, "StateChanged handler: " + ex.Message); } }
        }

        // ---------- Discovery ----------

        /// <summary>
        /// Looks for DLNA renderers and replaces the "dlna" targets of the list with what it finds. Each one is listed as soon as it is described, not at the end of the search: a sheet
        /// closed before the search ends has still shown it, and keeps it (only a search that ends drops the renderers missing for longer than the retention).
        /// </summary>
        public async Task<DlnaDiscoveryResult> DiscoverDlnaAsync(CancellationToken ct)
        {
            var d = new DlnaDiscovery(_udp, _net, _o.Discovery, _dlna);
            var res = await d.DiscoverAsync(ct, r => PublishDlna(new[] { DlnaDiscovery.ToTarget(r) }, false)).ConfigureAwait(false);
            PublishDlna(res.Renderers.Select(DlnaDiscovery.ToTarget), true);
            return res;
        }

        private void PublishDlna(IEnumerable<CastTarget> found, bool purge)
        {
            lock (_dlnaPublish)                                          // one at a time: an older list never overwrites a newer one
            {
                List<CastTarget> keep;
                lock (_dlnaSeen)
                {
                    if (purge)
                    {
                        var now = DateTime.UtcNow;
                        foreach (var k in _dlnaSeen.Keys.ToList())
                            if (_o.DlnaRetention <= TimeSpan.Zero || now - _dlnaSeen[k].LastSeenUtc > _o.DlnaRetention) _dlnaSeen.Remove(k);
                    }
                    foreach (var t in found) _dlnaSeen[t.Id] = t;
                    keep = _dlnaSeen.Values.ToList();
                }
                Targets.Update("dlna", keep);
            }
        }

        // ---------- Can this channel go to this screen? ----------

        /// <summary>
        /// Whether <paramref name="link"/> can be sent to <paramref name="target"/>, for greying out entries of the picker. A tuner TV (DLNA, AnyCodec) takes any channel and costs
        /// nothing to answer; for a Chromecast the channel is opened for a few seconds to read its codecs (the answer is remembered, see <see cref="Source"/>).
        /// </summary>
        public async Task<CastVerdict> CheckAsync(AceLink link, CastTarget target, CancellationToken ct)
        {
            if (link == null) throw new ArgumentNullException("link");
            var profile = target != null && target.Profile != null ? target.Profile : CastProfile.Chromecast2;
            if (target != null && target.Supports(CastCapabilities.AnyCodec))
                return new CastVerdict { Profile = profile, Support = CastSupport.Compatible };
            return await _source.CheckAsync(link, profile, ct).ConfigureAwait(false);
        }

        /// <summary>The verdict of a live session that is already open (the channel being watched): immediate, no extra connections.</summary>
        public Task<CastVerdict> CheckOpenSessionAsync(LiveSession session, CastTarget target, CancellationToken ct)
        {
            var profile = target != null && target.Profile != null ? target.Profile : CastProfile.Chromecast2;
            return CastAnalysis.AnalyzeSessionAsync(session, profile, _o.Source.OpenTimeout, _o.Source.AnalysisTimeout, ct);
        }

        // ---------- Send ----------

        public async Task<CastResult> SendAsync(CastTarget target, AceLink link, string name, CancellationToken ct)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (link == null) throw new ArgumentNullException("link");
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("CastService");
            }
            StopInternal("replaced", false);
            lock (_gate) { cts = _sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct); _target = target; _variant = null; }
            try
            {
                switch (target.Kind)
                {
                    case CastTargetKind.GoogleCast: return await SendCastAsync(target, link, name, cts.Token).ConfigureAwait(false);
                    case CastTargetKind.Dlna: return await SendDlnaAsync(target, link, name, cts.Token).ConfigureAwait(false);
                    default: return Fail(CastFailure.InvalidTarget, "Este tipo de dispositivo todavía no está soportado", target.Kind + " targets are not supported yet");
                }
            }
            catch (OperationCanceledException)
            {
                StopInternal("cancelled", true);
                return Fail(CastFailure.Cancelled, "Cancelado", "cancelled");
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "send failed: " + ex.GetType().Name + ": " + ex.Message);
                StopInternal("failed", true);
                SetState(CastState.Failed, ex.Message);
                return Fail(CastFailure.OpenFailed, "No se ha podido enviar el canal: " + ex.Message, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static CastResult Fail(CastFailure f, string reason, string detail) { return new CastResult { Ok = false, Failure = f, Reason = reason, Detail = detail }; }

        private async Task<CastResult> SendCastAsync(CastTarget target, AceLink link, string name, CancellationToken ct)
        {
            SetState(CastState.Opening);
            _source.StateChanged += OnSourceState;
            var res = await _source.StartAsync(new CastRequest
            {
                Link = link, Name = name ?? "", Target = target.Address, Profile = target.Profile ?? CastProfile.Chromecast2, Delivery = CastDelivery.Hls
            }, ct).ConfigureAwait(false);
            if (res.Ok) SetState(CastState.Casting);
            else SetState(res.Failure == CastFailure.Incompatible ? CastState.Incompatible : res.Failure == CastFailure.Cancelled ? CastState.Stopped : CastState.Failed, res.Reason);
            return res;
        }

        private void OnSourceState(CastState s)
        {
            // the source stopped by itself (idle, channel ended): the service follows
            if (s == CastState.Stopped) { var t = CurrentTarget; if (t != null && t.Kind == CastTargetKind.GoogleCast) SetState(CastState.Stopped, "the sending ended"); }
        }

        private async Task<CastResult> SendDlnaAsync(CastTarget target, AceLink link, string name, CancellationToken ct)
        {
            var r = target.Renderer;
            if (r == null || !r.IsUsable) return Fail(CastFailure.InvalidTarget, "El televisor no admite recibir contenido (sin AVTransport)", "the renderer has no AVTransport service");
            SetState(CastState.Opening);
            var host = _host();
            var opened = await host.OpenStreamAsync(link, new OpenOptions { TimeshiftBytes = _o.Source.TimeshiftBytes }, ct).ConfigureAwait(false);
            var live = opened.Session as LiveSession;
            if (live == null)
            {
                if (opened.Session != null) { try { opened.Session.Dispose(); } catch { } }
                SetState(CastState.Failed, "not a live stream");
                return Fail(CastFailure.OpenFailed, "Solo se pueden enviar canales en directo", "the link did not open as a live stream");
            }
            lock (_gate) _dlnaSession = live;

            // what the TV says it accepts decides the order of the variants to try
            List<DlnaProtocolInfo> sink = null;
            try { sink = await _dlna.GetProtocolInfoAsync(r, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { AceLog.Info(Tag, "GetProtocolInfo failed (" + ex.Message + "): the default variants are tried"); }
            var variants = DlnaQuirks.Prefer(DlnaQuirks.VariantsFor(r.Manufacturer, r.ModelName), sink).Take(Math.Max(1, _o.MaxVariants)).ToList();
            var failures = new List<string>();
            CastVerdict verdict = null;
            foreach (var v in variants)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) _variant = v;
                var req = new CastRequest
                {
                    Session = live, Name = name ?? "", Target = target.Address, Profile = target.Profile ?? CastProfile.Chromecast2, Delivery = CastDelivery.ProgressiveTs,
                    Dlna = v.Headers, Progressive = v.Mode, ProgressiveContentType = v.MimeType
                };
                var started = await _source.StartAsync(req, ct).ConfigureAwait(false);
                if (!started.Ok) { SetState(CastState.Failed, started.Reason); StopInternal("server failed", true); started.Verdict = started.Verdict ?? verdict; return started; }
                verdict = started.Verdict;
                string metadata = DidlLite.Build(name, started.Url, v.MimeType, v.Features);
                string why = await TryVariantAsync(r, started.Url, metadata, ct).ConfigureAwait(false);
                if (why == null)
                {
                    lock (_gate) _playingSinceTicks = DateTime.UtcNow.Ticks;
                    _stoppedPolls = 0;
                    _watch = new Timer(_ => WatchRenderer(), null, _o.WatchInterval, _o.WatchInterval);
                    SetState(CastState.Casting);
                    AceLog.Info(Tag, "the renderer plays with variant " + v);
                    return new CastResult { Ok = true, Url = started.Url, ContentType = v.MimeType, IsLive = true, Title = name ?? "", Verdict = verdict };
                }
                failures.Add(v.Name + ": " + why);
                AceLog.Info(Tag, "variant " + v.Name + " did not work: " + why);
                try { await _dlna.StopAsync(r, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            }
            StopInternal("no variant worked", true);
            SetState(CastState.Failed, "the TV did not accept the channel");
            return new CastResult { Ok = false, Failure = CastFailure.OpenFailed, Reason = "El televisor no ha aceptado el canal", Detail = string.Join("; ", failures), Verdict = verdict };
        }

        /// <summary>SetAVTransportURI + Play, then waits for the renderer to say PLAYING and to fetch the stream. null = it works; otherwise why not.</summary>
        private async Task<string> TryVariantAsync(DlnaRenderer r, string url, string metadata, CancellationToken ct)
        {
            try
            {
                await _dlna.SetAvTransportUriAsync(r, url, metadata, ct).ConfigureAwait(false);
                await _dlna.PlayAsync(r, ct).ConfigureAwait(false);
            }
            catch (UpnpException ex) { return ex.Message; }
            var end = DateTime.UtcNow + _o.PlayWait;
            DlnaTransportInfo last = null;
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(_o.PollInterval, ct).ConfigureAwait(false);
                try { last = await _dlna.GetTransportInfoAsync(r, ct).ConfigureAwait(false); }
                catch (UpnpException) { continue; }
                var st = _source.GetStats();
                if (last.IsError) return "the renderer reports ERROR_OCCURRED";
                if (st.StreamRequests > 0 && last.State == DlnaTransportState.Playing) return null;
                if (st.StreamRequests > 0 && last.State == DlnaTransportState.Transitioning) continue;
                if (st.StreamRequests > 0 && (last.State == DlnaTransportState.Stopped || last.State == DlnaTransportState.NoMediaPresent) && DateTime.UtcNow > end - TimeSpan.FromTicks(_o.PlayWait.Ticks / 2)) return "it fetched the stream and stopped (" + last.RawState + ")";
            }
            var s2 = _source.GetStats();
            if (s2.StreamRequests == 0) return "the renderer never asked for the stream (state " + (last == null ? "unknown" : last.RawState) + ")";
            return "the renderer did not reach PLAYING (state " + (last == null ? "unknown" : last.RawState) + ")";
        }

        // ---------- Watching the renderer ----------

        private void WatchRenderer()
        {
            try
            {
                CastTarget t; DlnaRenderer r;
                lock (_gate) { t = _target; r = t == null ? null : t.Renderer; }
                if (r == null || State != CastState.Casting) return;
                var task = _dlna.GetTransportInfoAsync(r, CancellationToken.None);
                if (!task.Wait(TimeSpan.FromSeconds(6))) return;
                var info = task.Result;
                if (info.State == DlnaTransportState.Stopped || info.State == DlnaTransportState.NoMediaPresent)
                {
                    var since = new DateTime(Interlocked.Read(ref _playingSinceTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - since < _o.WatchGrace) return;
                    if (++_stoppedPolls >= 2) { AceLog.Info(Tag, "the renderer stopped (" + info.RawState + "): ending the sending"); StopInternal("stopped from the TV", true, true); }
                }
                else _stoppedPolls = 0;
            }
            catch (Exception ex) { AceLog.Debug(Tag, "watch: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
        }

        // ---------- Control of the renderer ----------

        private DlnaRenderer ActiveRenderer()
        {
            lock (_gate) { var t = _target; return t != null && t.Kind == CastTargetKind.Dlna && _state == CastState.Casting ? t.Renderer : null; }
        }

        /// <summary>Pause on the TV. false if there is nothing to pause or the TV has no Pause (the caller can Stop instead).</summary>
        public async Task<bool> PauseAsync(CancellationToken ct)
        {
            var r = ActiveRenderer();
            return r != null && await _dlna.PauseAsync(r, ct).ConfigureAwait(false);
        }

        public async Task<bool> ResumeAsync(CancellationToken ct)
        {
            var r = ActiveRenderer();
            if (r == null) return false;
            await _dlna.PlayAsync(r, ct).ConfigureAwait(false);
            return true;
        }

        public async Task<DlnaTransportInfo> GetTransportAsync(CancellationToken ct)
        {
            var r = ActiveRenderer();
            return r == null ? null : await _dlna.GetTransportInfoAsync(r, ct).ConfigureAwait(false);
        }

        /// <summary>Volume of the TV (0-100), -1 if there is none.</summary>
        public async Task<int> GetVolumeAsync(CancellationToken ct)
        {
            var r = ActiveRenderer();
            return r == null || r.RenderingControl == null ? -1 : await _dlna.GetVolumeAsync(r, ct).ConfigureAwait(false);
        }

        public async Task SetVolumeAsync(int volume, CancellationToken ct)
        {
            var r = ActiveRenderer();
            if (r != null && r.RenderingControl != null) await _dlna.SetVolumeAsync(r, volume, ct).ConfigureAwait(false);
        }

        // ---------- Stop ----------

        public void Stop() { StopInternal("stopped", true); }

        private void StopInternal(string reason, bool setState, bool fromWatch = false)
        {
            CancellationTokenSource cts; LiveSession live; CastTarget t; Timer w;
            lock (_gate) { cts = _sendCts; _sendCts = null; live = _dlnaSession; _dlnaSession = null; t = _target; w = _watch; _watch = null; }
            if (cts == null && live == null && t == null && State == CastState.Idle) return;
            if (w != null && !fromWatch) w.Dispose();
            else if (w != null) { var ww = w; Task.Run(() => ww.Dispose()); }
            try { if (cts != null) cts.Cancel(); } catch (ObjectDisposedException) { }
            // the TV is told to stop (it keeps the last frame otherwise), without waiting long
            if (t != null && t.Kind == CastTargetKind.Dlna && t.Renderer != null && State == CastState.Casting)
            {
                try { var st = _dlna.StopAsync(t.Renderer, CancellationToken.None); st.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            }
            _source.StateChanged -= OnSourceState;
            try { _source.Stop(); } catch (Exception ex) { AceLog.Warn(Tag, "source stop: " + ex.Message); }
            if (live != null) { try { live.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "session dispose: " + ex.Message); } }
            lock (_gate) { _target = null; _variant = null; }
            if (setState) SetState(CastState.Stopped, reason);
            if (cts != null) { try { cts.Dispose(); } catch { } }
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            StopInternal("disposed", false);
            _source.Dispose();
            _dlna.Dispose();
        }
    }
}
