using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.Util;
using PUi.Platform;

namespace PUi.Cast
{
    public sealed class CastControllerOptions
    {
        /// <summary>While the sheet is open the search is repeated this often (quietly: the list only changes if something appeared or went).</summary>
        public TimeSpan RefreshInterval = TimeSpan.FromSeconds(12);
        /// <summary>While sending: how often a Chromecast session is vouched for to the engine, and a DLNA television's state (paused, volume) is read.</summary>
        public TimeSpan PollInterval = TimeSpan.FromSeconds(4);
        /// <summary>How long a call to a television may take before it is given up.</summary>
        public TimeSpan CallTimeout = TimeSpan.FromSeconds(6);
    }

    /// <summary>
    /// The phone's and the tablet's "Enviar a la tele": the <see cref="ICastService"/> the interface uses, made of the engine's send service (DLNA televisions are found and controlled by it; for a
    /// Chromecast it prepares the HLS), the Google Cast bridge of the shell (finds the Chromecasts, connects, loads, controls) and what the shell does while something is being sent. No platform
    /// type is named here, so all of it is tested on the PC with doubles.
    ///
    ///  - Devices: the engine's target list, one entry per screen. While the sheet is open it is searched (DLNA; the multicast lock is held for each search) and refreshed every few seconds.
    ///  - Check: the verdict of the engine for the channel and the device's profile, as one sentence. A Chromecast that is also a DLNA television (an Android TV) is offered as playable when
    ///    only the DLNA way can play the channel, and the send then goes that way.
    ///  - Send: the local playback is released (the engine allows one session per channel), then the engine prepares the channel and, for a Chromecast, the session is opened in parallel and
    ///    told to load; a send replaces the previous one, even while that one is still connecting.
    ///  - While sending: the state of the device is followed (a Chromecast through its session; a television by asking it, lightly), the engine is told the receiver is alive, and the
    ///    notification and the locks of the shell are held. If the sending ends by itself (the user stops it from the TV, the channel goes down, the device goes away) the user is told and
    ///    everything is released.
    /// </summary>
    public sealed class CastController : ICastService, IDisposable
    {
        private const string Tag = "cast";
        private readonly object _gate = new object();
        private readonly ICastEngine _engine;
        private readonly IGoogleCastBridge _bridge;
        private readonly ISendingHost _host;
        private readonly IMulticastLock _multicast;
        private readonly CastControllerOptions _o;
        private CastStatus _status = CastStatus.Idle;
        private IReadOnlyList<CastDevice> _devices = new CastDevice[0];
        private readonly HashSet<string> _noPause = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CastTarget> _viaAlternative = new Dictionary<string, CastTarget>(StringComparer.Ordinal);
        private bool _available = true, _scanning, _sheetOpen, _disposed, _polling;
        private volatile bool _searching, _stopping;
        private CancellationTokenSource _scanCts, _sendCts;
        private Timer _refresh, _poll;
        private int _generation;
        private CastTarget _current;
        private string _castSessionId;

        public CastController(ICastEngine engine, IGoogleCastBridge bridge, ISendingHost host, IMulticastLock multicast, CastControllerOptions options = null)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            _engine = engine; _bridge = bridge; _host = host; _multicast = multicast; _o = options ?? new CastControllerOptions();
            engine.Targets.Changed += RebuildDevices;
            engine.StateChanged += OnEngineState;
            if (bridge != null) { bridge.StatusChanged += OnBridgeStatus; bridge.SessionEnded += OnBridgeEnded; }
            RebuildDevices();
        }

        // ---------------------------------------------------------------- the contract
        public bool IsAvailable { get { return _available; } }
        public event Action AvailabilityChanged;
        public event Action Changed;
        public event Action<string> Notice;
        /// <summary>Raised when a sending that was active is over (by the user or by itself): the shell lets the engine sleep again if the app is in the background.</summary>
        public event Action Idled;

        public IReadOnlyList<CastDevice> Devices { get { lock (_gate) return _devices; } }
        public bool Searching { get { return _searching; } }
        public CastStatus Status { get { lock (_gate) return _status; } }
        /// <summary>Something is being sent or about to be: the engine must stay alive when the app goes to the background.</summary>
        public bool Busy { get { return Status.IsActive; } }

        public void SetAvailable(bool available)
        {
            if (_available == available) return;
            _available = available;
            var h = AvailabilityChanged; if (h != null) h();
        }

        private void RaiseChanged() { var h = Changed; if (h != null) { try { h(); } catch (Exception ex) { AceLog.Warn(Tag, "Changed handler: " + ex.Message); } } }
        private void RaiseNotice(string text) { var h = Notice; if (h != null) { try { h(text); } catch (Exception ex) { AceLog.Warn(Tag, "Notice handler: " + ex.Message); } } }

        // ---------------------------------------------------------------- devices
        private void RebuildDevices()
        {
            var items = _engine.Targets.Items;
            var list = new List<CastDevice>();
            lock (_gate)
            {
                foreach (var t in items)
                {
                    if (t.Kind == CastTargetKind.PApp) continue;
                    bool cast = t.Kind == CastTargetKind.GoogleCast;
                    list.Add(new CastDevice
                    {
                        Id = t.Id, Name = t.Name, Kind = cast ? CastDeviceKind.Chromecast : CastDeviceKind.Tv,
                        CanPause = (cast || t.Supports(CastCapabilities.Pause)) && !_noPause.Contains(t.Id),
                        CanVolume = cast || t.Supports(CastCapabilities.Volume)
                    });
                }
                _devices = list;
            }
            RaiseChanged();
        }

        // ---------------------------------------------------------------- search
        public void BeginSearch()
        {
            lock (_gate) { if (_disposed) return; _sheetOpen = true; }
            if (_bridge != null) { try { _bridge.SetActiveScan(true); } catch (Exception ex) { AceLog.Warn(Tag, "bridge scan: " + ex.Message); } }
            StartScan(true);
            lock (_gate) { if (_refresh == null && !_disposed) _refresh = new Timer(_ => RefreshTick(), null, _o.RefreshInterval, _o.RefreshInterval); }
        }

        /// <summary>
        /// A quiet search ahead of the sheet (the app starts or comes back to the front), so the televisions are already listed when it opens: a DLNA television may take a second to answer
        /// and the sheet is often closed before. Nothing while sending, or if a search is already going on.
        /// </summary>
        public void WarmUp()
        {
            lock (_gate) { if (_disposed || _status.IsActive) return; }
            StartScan(false);
        }

        public void EndSearch()
        {
            Timer refresh; CancellationTokenSource cts;
            lock (_gate) { _sheetOpen = false; refresh = _refresh; _refresh = null; cts = _scanCts; }
            if (refresh != null) refresh.Dispose();
            if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            if (_bridge != null) { try { _bridge.SetActiveScan(false); } catch (Exception ex) { AceLog.Warn(Tag, "bridge scan: " + ex.Message); } }
            SetSearching(false);
        }

        private void RefreshTick()
        {
            bool open; lock (_gate) open = _sheetOpen && !_disposed;
            if (open) StartScan(false);
        }

        private void SetSearching(bool on)
        {
            if (_searching == on) return;
            _searching = on;
            RaiseChanged();
        }

        private void StartScan(bool visible)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_disposed || _scanning) return;
                _scanning = true; cts = _scanCts = new CancellationTokenSource();
            }
            if (visible) SetSearching(true);
            Task.Run(async () =>
            {
                if (_multicast != null) { try { _multicast.Acquire(); } catch (Exception ex) { AceLog.Warn(Tag, "multicast lock: " + ex.Message); } }
                try { await _engine.DiscoverAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AceLog.Warn(Tag, "DLNA search failed: " + ex.GetType().Name + ": " + ex.Message); }
                finally
                {
                    if (_multicast != null) { try { _multicast.Release(); } catch (Exception ex) { AceLog.Warn(Tag, "multicast lock: " + ex.Message); } }
                    lock (_gate) { _scanning = false; }
                    if (visible) SetSearching(false);
                }
            });
        }

        // ---------------------------------------------------------------- check
        private static string AltKey(string deviceId, string channelId) { return deviceId + "|" + channelId; }

        public async Task<CastCheck> CheckAsync(CastDevice device, Channel channel, LiveSession open, CancellationToken ct)
        {
            try
            {
                if (device == null || channel == null) return CastCheck.Unknown;
                var link = channel.Entry == null ? null : channel.Entry.Link;
                if (link == null) return CastCheck.No(CastMessages.NotSendable);
                var target = _engine.Targets.Find(device.Id);
                if (target == null) return CastCheck.Unknown;
                var verdict = await _engine.CheckAsync(link, target, open, ct).ConfigureAwait(false);
                if (verdict == null) return CastCheck.Unknown;
                if (verdict.Support == CastSupport.Compatible) return CastCheck.Ok();
                if (verdict.Support != CastSupport.Incompatible) return CastCheck.Unknown;
                // a Chromecast that is also a DLNA television (Android TV): the television decodes anything, so it can play what the Chromecast way cannot
                var alt = target.Kind == CastTargetKind.GoogleCast ? target.Alternatives.FirstOrDefault(a => a.Kind == CastTargetKind.Dlna && a.Supports(CastCapabilities.AnyCodec)) : null;
                if (alt != null)
                {
                    lock (_gate) _viaAlternative[AltKey(device.Id, channel.Id)] = alt;
                    return CastCheck.Ok();
                }
                return CastCheck.No(CastMessages.Incompatible(verdict, device.Kind));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AceLog.Warn(Tag, "check failed: " + ex.GetType().Name + ": " + ex.Message); return CastCheck.Unknown; }
        }

        // ---------------------------------------------------------------- send
        private bool SetStatus(int gen, CastStatus st)
        {
            lock (_gate) { if (gen != _generation) return false; _status = st; }
            RaiseChanged();
            return true;
        }

        private bool IsCurrent(int gen) { lock (_gate) return gen == _generation; }

        public async Task<bool> SendAsync(CastDevice device, Channel channel, Func<Task> releaseLocal, CancellationToken ct)
        {
            if (device == null || channel == null) return false;
            int gen; CancellationTokenSource cts, prev;
            lock (_gate)
            {
                if (_disposed) return false;
                prev = _sendCts; cts = _sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct); gen = ++_generation;
            }
            if (prev != null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } }
            var token = cts.Token;
            try
            {
                if (releaseLocal != null)
                {
                    try { await releaseLocal().ConfigureAwait(false); }
                    catch (Exception ex) { AceLog.Warn(Tag, "releasing the local playback: " + ex.Message); }
                }
                token.ThrowIfCancellationRequested();
                var link = channel.Entry == null ? null : channel.Entry.Link;
                if (link == null) return FailStart(gen, CastMessages.NotSendable);
                var target = TargetFor(device, channel);
                if (target == null) return FailStart(gen, CastMessages.DeviceGone);
                if (target.Kind == CastTargetKind.GoogleCast && _bridge == null) return FailStart(gen, "Este móvil no puede enviar a un Chromecast");

                StopPolling();
                if (!SetStatus(gen, new CastStatus { Phase = CastPhase.Starting, Device = device, Channel = channel })) return false;
                lock (_gate) _current = target;
                if (_host != null) { try { _host.Begin(device.Name, channel.Name); } catch (Exception ex) { AceLog.Warn(Tag, "sending host: " + ex.Message); } }
                // another device than the one the Cast session is on: that session ends
                if (_castSessionId != null && _castSessionId != target.Id) { _stopping = true; try { _bridge.Disconnect(); } catch (Exception) { } finally { _stopping = false; } _castSessionId = null; }

                bool isCast = target.Kind == CastTargetKind.GoogleCast;
                Task connect = null;
                if (isCast && _castSessionId != target.Id) connect = ConnectAsync(target, token);
                CastResult res;
                try { res = await _engine.SendAsync(target, link, channel.Name, token).ConfigureAwait(false); }
                catch (Exception) { if (connect != null) { try { await connect.ConfigureAwait(false); } catch (Exception) { } } throw; }
                if (!res.Ok)
                {
                    if (connect != null) { try { await connect.ConfigureAwait(false); } catch (Exception) { } if (IsCurrent(gen)) DisconnectBridgeQuietly(); }
                    return FailStart(gen, res.Failure == CastFailure.Cancelled ? null : CastMessages.StartFailed(res, device.Kind));
                }
                if (isCast)
                {
                    if (connect != null)
                    {
                        try { await connect.ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            AceLog.Warn(Tag, "Cast session failed: " + ex.Message);
                            if (IsCurrent(gen)) _engine.Stop();
                            return FailStart(gen, "No se ha podido conectar con " + device.Name);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    try { await _bridge.LoadAsync(res, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AceLog.Warn(Tag, "Cast load failed: " + ex.Message);
                        if (IsCurrent(gen)) { _engine.Stop(); DisconnectBridgeQuietly(); }
                        return FailStart(gen, "El Chromecast no ha podido cargar el canal");
                    }
                }
                token.ThrowIfCancellationRequested();
                if (!SetStatus(gen, new CastStatus { Phase = CastPhase.Sending, Device = device, Channel = channel, Volume = -1 })) return false;
                if (_host != null) { try { _host.Update(device.Name, channel.Name, false); } catch (Exception) { } }
                StartPolling(gen);
                AceLog.Info(Tag, "sending '" + channel.Name + "' to " + device);
                return true;
            }
            catch (OperationCanceledException)
            {
                // cancelled by the user (Stop), by a newer send, or by the caller: the one that cancelled has already taken care of the state
                if (IsCurrent(gen)) FailStart(gen, null);
                return false;
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "send failed: " + ex.GetType().Name + ": " + ex.Message);
                return FailStart(gen, "No se ha podido enviar el canal");
            }
        }

        private CastTarget TargetFor(CastDevice device, Channel channel)
        {
            var t = _engine.Targets.Find(device.Id);
            if (t == null) return null;
            CastTarget alt;
            lock (_gate) if (_viaAlternative.TryGetValue(AltKey(device.Id, channel.Id), out alt)) return alt;
            return t;
        }

        private Task ConnectAsync(CastTarget target, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                await _bridge.ConnectAsync(target, ct).ConfigureAwait(false);
                _castSessionId = target.Id;
            });
        }

        private void DisconnectBridgeQuietly()
        {
            if (_bridge == null) return;
            bool was = _stopping; _stopping = true;
            try { _bridge.Disconnect(); } catch (Exception ex) { AceLog.Warn(Tag, "bridge disconnect: " + ex.Message); }
            finally { _stopping = was; }
            _castSessionId = null;
        }

        /// <summary>The send did not work (or was cancelled: text null): everything of it is released and, if it is still the current one, the status is idle again and the user is told.</summary>
        private bool FailStart(int gen, string text)
        {
            bool mine = IsCurrent(gen);
            if (mine)
            {
                _stopping = true;
                try { _engine.Stop(); } catch (Exception ex) { AceLog.Warn(Tag, "engine stop: " + ex.Message); }
                _stopping = false;
                // a Cast session that was being opened (or is open) for this send goes with it: it can still come up after the send was cancelled
                CastTarget attempted; lock (_gate) attempted = _current;
                if (attempted != null && attempted.Kind == CastTargetKind.GoogleCast) DisconnectBridgeQuietly();
                StopPolling();
                lock (_gate) { _status = CastStatus.Idle; _current = null; }
                if (_host != null) { try { _host.End(); } catch (Exception ex) { AceLog.Warn(Tag, "sending host: " + ex.Message); } }
                RaiseChanged();
                var idled = Idled; if (idled != null) idled();
                if (text != null) RaiseNotice(text);
            }
            return false;
        }

        // ---------------------------------------------------------------- stop and the end of a sending
        public void Stop()
        {
            CancellationTokenSource cts;
            bool wasActive;
            lock (_gate) { _generation++; cts = _sendCts; wasActive = _status.IsActive; }
            if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            Teardown();
            if (wasActive) FinishIdle(null);
        }

        /// <summary>Ends what is being sent without telling the user (they asked for it) or with a notice (it ended by itself).</summary>
        private void EndByItself(string notice)
        {
            lock (_gate) { if (!_status.IsActive) return; _generation++; }
            Teardown();
            FinishIdle(notice);
        }

        private void Teardown()
        {
            _stopping = true;
            try { _engine.Stop(); } catch (Exception ex) { AceLog.Warn(Tag, "engine stop: " + ex.Message); }
            if (_bridge != null) { try { _bridge.Disconnect(); } catch (Exception ex) { AceLog.Warn(Tag, "bridge disconnect: " + ex.Message); } }
            _castSessionId = null;
            StopPolling();
            _stopping = false;
        }

        private void FinishIdle(string notice)
        {
            lock (_gate) { _status = CastStatus.Idle; _current = null; }
            if (_host != null) { try { _host.End(); } catch (Exception ex) { AceLog.Warn(Tag, "sending host: " + ex.Message); } }
            RaiseChanged();
            var idled = Idled; if (idled != null) idled();
            if (notice != null) RaiseNotice(notice);
        }

        private void OnEngineState(CastState s)
        {
            if (s != CastState.Stopped && s != CastState.Failed) return;
            if (_stopping) return;
            if (Status.Phase != CastPhase.Sending) return;
            AceLog.Info(Tag, "the engine stopped by itself: " + _engine.Reason);
            EndByItself(CastMessages.Ended(_engine.Reason));
        }

        private void OnBridgeEnded(string notice)
        {
            if (_stopping) return;
            var st = Status;
            if (!st.IsActive || st.Device == null || st.Device.Kind != CastDeviceKind.Chromecast) return;
            if (st.Phase == CastPhase.Starting) return;                              // a failed connection is handled by the send itself
            AceLog.Info(Tag, "the Cast session ended by itself");
            EndByItself(string.IsNullOrEmpty(notice) ? "El envío ha terminado" : notice);
        }

        private void OnBridgeStatus(bool paused, int volume)
        {
            var st = Status;
            if (st.Phase != CastPhase.Sending || st.Device == null || st.Device.Kind != CastDeviceKind.Chromecast) return;
            UpdateStatus(paused, volume);
        }

        // ---------------------------------------------------------------- following the device while sending
        private void StartPolling(int gen)
        {
            StopPolling();
            lock (_gate) { _poll = new Timer(_ => PollTick(gen), null, _o.PollInterval, _o.PollInterval); }
        }

        private void StopPolling()
        {
            Timer t;
            lock (_gate) { t = _poll; _poll = null; }
            if (t != null) t.Dispose();
        }

        private async void PollTick(int gen)
        {
            try
            {
                lock (_gate) { if (_polling || gen != _generation) return; _polling = true; }
                var st = Status;
                if (st.Phase != CastPhase.Sending || st.Device == null) return;
                if (st.Device.Kind == CastDeviceKind.Chromecast) { _engine.KeepAlive(); return; }
                using (var cts = new CancellationTokenSource(_o.CallTimeout))
                {
                    bool paused = st.Paused; int vol = st.Volume;
                    var info = await _engine.GetTransportAsync(cts.Token).ConfigureAwait(false);
                    if (info != null)
                    {
                        if (info.State == DlnaTransportState.PausedPlayback) paused = true;
                        else if (info.State == DlnaTransportState.Playing) paused = false;
                    }
                    if (st.Device.CanVolume) { try { int v = await _engine.GetVolumeAsync(cts.Token).ConfigureAwait(false); if (v >= 0) vol = v; } catch (Exception) { } }
                    if (IsCurrent(gen)) UpdateStatus(paused, vol);
                }
            }
            catch (Exception ex) { AceLog.Debug(Tag, "poll: " + ex.GetType().Name + ": " + ex.Message); }
            finally { lock (_gate) _polling = false; }
        }

        private void UpdateStatus(bool paused, int volume)
        {
            CastStatus next, cur;
            lock (_gate)
            {
                cur = _status;
                if (cur.Phase != CastPhase.Sending) return;
                if (cur.Paused == paused && cur.Volume == volume) return;
                next = new CastStatus { Phase = cur.Phase, Device = cur.Device, Channel = cur.Channel, Paused = paused, Volume = volume };
                _status = next;
            }
            if (_host != null && next.Device != null && next.Channel != null) { try { _host.Update(next.Device.Name, next.Channel.Name, paused); } catch (Exception) { } }
            RaiseChanged();
        }

        // ---------------------------------------------------------------- pause and volume
        public void SetPaused(bool paused) { Task.Run(() => SetPausedAsync(paused)); }

        private async Task SetPausedAsync(bool paused)
        {
            try
            {
                var st = Status; CastTarget target; lock (_gate) target = _current;
                if (st.Phase != CastPhase.Sending || st.Device == null || target == null) return;
                if (st.Device.Kind == CastDeviceKind.Chromecast)
                {
                    if (paused) _bridge.Pause(); else _bridge.Resume();
                    UpdateStatus(paused, st.Volume);
                    return;
                }
                bool ok;
                using (var cts = new CancellationTokenSource(_o.CallTimeout))
                {
                    try { ok = paused ? await _engine.PauseAsync(cts.Token).ConfigureAwait(false) : await _engine.ResumeAsync(cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { AceLog.Warn(Tag, "pause/resume: " + ex.Message); ok = false; }
                }
                if (!ok && paused)
                {
                    // this television has no Pause (it is optional in UPnP): the button goes away and the user is told
                    lock (_gate) _noPause.Add(st.Device.Id);
                    var cur = Status;
                    if (cur.Phase == CastPhase.Sending && cur.Device != null && cur.Device.Id == st.Device.Id)
                        lock (_gate) _status = new CastStatus { Phase = cur.Phase, Device = new CastDevice { Id = cur.Device.Id, Name = cur.Device.Name, Kind = cur.Device.Kind, CanPause = false, CanVolume = cur.Device.CanVolume }, Channel = cur.Channel, Paused = false, Volume = cur.Volume };
                    RebuildDevices();
                    RaiseNotice(CastMessages.NoPause);
                    return;
                }
                if (ok) UpdateStatus(paused, Status.Volume);
            }
            catch (Exception ex) { AceLog.Warn(Tag, "pause: " + ex.Message); }
        }

        public void SetVolume(int percent) { Task.Run(() => SetVolumeAsync(Math.Max(0, Math.Min(100, percent)))); }

        private async Task SetVolumeAsync(int percent)
        {
            try
            {
                var st = Status;
                if (st.Phase != CastPhase.Sending || st.Device == null) return;
                if (st.Device.Kind == CastDeviceKind.Chromecast) _bridge.SetVolume(percent);
                else using (var cts = new CancellationTokenSource(_o.CallTimeout)) await _engine.SetVolumeAsync(percent, cts.Token).ConfigureAwait(false);
                UpdateStatus(Status.Paused, percent);
            }
            catch (Exception ex) { AceLog.Warn(Tag, "volume: " + ex.Message); }
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            EndSearch();
            try { Stop(); } catch (Exception) { }
            _engine.Targets.Changed -= RebuildDevices;
            _engine.StateChanged -= OnEngineState;
            if (_bridge != null) { _bridge.StatusChanged -= OnBridgeStatus; _bridge.SessionEnded -= OnBridgeEnded; try { _bridge.Dispose(); } catch (Exception) { } }
            try { _engine.Dispose(); } catch (Exception) { }
        }
    }
}
