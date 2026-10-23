using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Live;
using AceEngine.Util;
using PUi.Motion;
using PUi.Platform;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>What the cast interface needs from the player screen while it is open: which channel it plays, its open engine session (to check the channel against a device without opening it again) and how to stop it.</summary>
    public interface ICastLocalPlayer
    {
        Channel Channel { get; }
        /// <summary>The engine's live session of the channel being played, or null.</summary>
        LiveSession OpenSession { get; }
        /// <summary>Stops the local playback and completes when the engine session is released (the engine allows one session per channel). Idempotent.</summary>
        Task ReleaseAsync();
    }

    /// <summary>
    /// "Enviar a la tele" on a phone or a tablet, as the interface sees it: the sheet with the devices, the strip over the lists while sending, the notices, and the bridge between what the user
    /// chooses (a device, a channel) and <see cref="ICastService"/>. It belongs to the UiContext and draws over whatever screen is current (PApp calls Update, Draw, OnPointer and OnKey); the
    /// screens only ask it three things: open the sheet (the cast buttons), redirect a channel the user chose in a list (<see cref="TryRedirect"/>) and, in the player, show the sending state
    /// (<see cref="View"/>). Nothing here runs on the TV: every entry point does nothing unless the interface is compact and the service is available.
    ///
    /// The flow: a button opens the sheet (<see cref="Open"/>), which asks the service to search; with a channel (the one being watched, or the one being sent) each device is checked against it and
    /// the ones that cannot play it are dimmed with the reason; a tap on a device sends the channel (the local playback is released first); a tap with no channel in view arms the device: the next
    /// channel chosen in a list is sent to it. While sending, choosing a channel anywhere sends that one to the same device.
    /// </summary>
    public sealed partial class CastUi : IDisposable
    {
        public const double ToastSeconds = 4.5;

        private readonly UiContext _ctx;
        private readonly object _gate = new object();
        private ICastService _watched;
        private bool _open, _disposed;
        private readonly Anim _anim = new Anim(0, Bezier.Pop);
        private Channel _channel;
        private CancellationTokenSource _checkCts;
        private readonly Dictionary<string, CastCheck> _checks = new Dictionary<string, CastCheck>();
        private readonly HashSet<string> _checking = new HashSet<string>();
        private string _devicesSig = "";
        private volatile bool _changed;
        private readonly ConcurrentQueue<string> _notices = new ConcurrentQueue<string>();
        private volatile bool _sendInFlight;
        private CastDevice _pendingDevice;
        private Channel _pendingChannel;
        private CancellationTokenSource _sendCts;
        private string _toast = ""; private bool _toastWarn; private double _toastAt = -100;
        private readonly Inertia _scroll = new Inertia();

        public CastUi(UiContext ctx) { _ctx = ctx; }

        // ---------------------------------------------------------------- state

        /// <summary>The sheet is open (or closing: its animation may still be running).</summary>
        public bool IsOpen { get { return _open; } }

        /// <summary>The channel the open sheet checks the devices against (null: none in view).</summary>
        public Channel SheetChannel { get { return _channel; } }

        /// <summary>A device chosen with no channel in view: the next channel chosen in a list is sent to it. null if none.</summary>
        public CastDevice Armed { get; private set; }

        /// <summary>The player screen that is open, if one is (it registers itself): the local playback to release before sending, and the open session to check against.</summary>
        public ICastLocalPlayer Local { get; set; }

        /// <summary>The current screen is the player: it shows the sending state itself, so the strip over the lists is not drawn. Set by PApp.</summary>
        public bool OnPlayerScreen { get; set; }

        /// <summary>The text of the notice on screen ("" = none).</summary>
        public string ToastText { get { return _toast; } }

        private ICastService Cast { get { return _ctx.Cast; } }

        /// <summary>The function can be used here: a phone or a tablet whose platform has a cast service that says it is available.</summary>
        public bool Available { get { return _ctx.Compact && Cast.IsAvailable; } }

        /// <summary>A send has been started and has not finished (connecting): the status of the service may still say Idle.</summary>
        public bool SendInFlight { get { return _sendInFlight; } }

        /// <summary>
        /// What is being sent, as the interface shows it: the service's status, or, in the moment between the user's choice and the service's first answer, a Starting status for the device and
        /// channel that were chosen (so the screen never flashes back to "normal" in between).
        /// </summary>
        public CastStatus View
        {
            get
            {
                var st = Cast.Status ?? CastStatus.Idle;
                if (st.IsActive) return st;
                if (_sendInFlight)
                {
                    CastDevice d; Channel c;
                    lock (_gate) { d = _pendingDevice; c = _pendingChannel; }
                    if (d != null) return new CastStatus { Phase = CastPhase.Starting, Device = d, Channel = c };
                }
                return st;
            }
        }

        /// <summary>A channel is being sent, or about to be.</summary>
        public bool IsCasting { get { return Available && View.IsActive; } }

        // ---------------------------------------------------------------- the service's events

        private void Watch()
        {
            var c = Cast;
            if (ReferenceEquals(c, _watched)) return;
            if (_watched != null) { _watched.Changed -= OnServiceChanged; _watched.Notice -= OnServiceNotice; }
            _watched = c;
            c.Changed += OnServiceChanged; c.Notice += OnServiceNotice;
        }

        private void OnServiceChanged() { _changed = true; try { _ctx.Nav.Invalidate(); } catch (Exception) { } }

        private void OnServiceNotice(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _notices.Enqueue(text);
            try { _ctx.Nav.Invalidate(); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- opening and closing the sheet

        /// <summary>
        /// A cast button was pressed. <paramref name="channel"/> is the channel in view (the player's); null from a page with no channel: then the one being sent, if any, is the channel the
        /// devices are checked against.
        /// </summary>
        public void Open(Channel channel)
        {
            if (!Available || _disposed) return;
            Watch();
            if (_open) return;
            _channel = channel ?? (View.IsActive ? View.Channel : null);
            _open = true;
            _scroll.Snap(0, _ctx.Now);
            _anim.Set(1, _ctx.Now, 0.32);
            lock (_gate) { foreach (var k in _checks.Where(x => !x.Value.Known).Select(x => x.Key).ToList()) _checks.Remove(k); }
            try { Cast.BeginSearch(); } catch (Exception ex) { AceLog.Warn("ui", "cast search: " + ex.Message); }
            _devicesSig = Signature(Cast.Devices);
            StartChecks();
            _ctx.Nav.Invalidate();
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            ResetSheetGesture();
            _anim.Set(0, _ctx.Now, 0.24);
            CancelChecks();
            try { Cast.EndSearch(); } catch (Exception ex) { AceLog.Warn("ui", "cast search: " + ex.Message); }
            _ctx.Nav.Invalidate();
        }

        /// <summary>The app goes to the background: the sheet closes at once and the search stops (a sending in progress is not touched).</summary>
        public void Suspend()
        {
            if (_disposed) return;
            if (_open)
            {
                _open = false; _anim.Snap(0); ResetSheetGesture(); CancelChecks();
                try { Cast.EndSearch(); } catch (Exception) { }
            }
            ResetPillGesture();
        }

        // ---------------------------------------------------------------- what the user chooses

        /// <summary>The user chose a channel in a list. While a channel is being sent, or a device is armed, it goes to that device and this returns true (the list must not open the player).</summary>
        public bool TryRedirect(Channel channel)
        {
            if (channel == null || !Available) return false;
            Watch();
            var v = View;
            var device = v.IsActive ? v.Device : Armed;
            if (device == null) return false;
            if (v.IsActive && v.Channel != null && v.Channel.Id == channel.Id && v.Phase == CastPhase.Sending) return true;      // already there
            Send(device, channel);
            return true;
        }

        /// <summary>Sends <paramref name="channel"/> to <paramref name="device"/>, releasing the local playback first. Returns at once; the outcome shows in <see cref="View"/> and as a notice.</summary>
        public void Send(CastDevice device, Channel channel)
        {
            if (device == null || channel == null || !Available) return;
            Watch();
            Armed = null;
            var cts = new CancellationTokenSource();
            CancellationTokenSource old;
            lock (_gate) { old = _sendCts; _sendCts = cts; _pendingDevice = device; _pendingChannel = channel; }
            if (old != null) { try { old.Cancel(); } catch (ObjectDisposedException) { } }
            _sendInFlight = true;
            var cast = Cast;
            var local = Local;
            Func<Task> release = local == null ? null : (Func<Task>)local.ReleaseAsync;
            AceLog.Info("ui", "cast: send '" + channel.Name + "' to " + device);
            _ctx.Nav.Invalidate();
            Task.Run(async () =>
            {
                try { await cast.SendAsync(device, channel, release, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AceLog.Warn("ui", "cast send: " + ex.Message); OnServiceNotice("No se ha podido enviar el canal."); }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_sendCts, cts)) { _sendInFlight = false; _pendingDevice = null; _pendingChannel = null; } }
                    try { _ctx.Nav.Invalidate(); } catch (Exception) { }
                }
            });
        }

        /// <summary>"Dejar de enviar" (or "Cancelar" while it connects).</summary>
        public void StopSending()
        {
            CancellationTokenSource cts;
            lock (_gate) { cts = _sendCts; }
            if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            Armed = null;
            try { Cast.Stop(); } catch (Exception ex) { AceLog.Warn("ui", "cast stop: " + ex.Message); }
            _ctx.Nav.Invalidate();
        }

        public void CancelArmed() { Armed = null; _ctx.Nav.Invalidate(); }

        /// <summary>Pause or resume on the device.</summary>
        public void TogglePause()
        {
            var v = View;
            if (v.Phase != CastPhase.Sending) return;
            try { Cast.SetPaused(!v.Paused); } catch (Exception ex) { AceLog.Warn("ui", "cast pause: " + ex.Message); }
        }

        public void SetVolume(int percent)
        {
            try { Cast.SetVolume(Math.Max(0, Math.Min(100, percent))); } catch (Exception ex) { AceLog.Warn("ui", "cast volume: " + ex.Message); }
        }

        /// <summary>A short message over the screen (a minute fraction: it goes by itself).</summary>
        public void ShowToast(string text, bool warn = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            _toast = text; _toastWarn = warn; _toastAt = _ctx.Now;
            _ctx.Nav.Invalidate();
        }

        // ---------------------------------------------------------------- the check of a channel against the devices

        private static string CheckKey(Channel ch, CastDevice d) { return (ch == null ? "" : ch.Id) + "|" + d.Id; }

        /// <summary>The answer of the check for a device, or null while it is not known yet.</summary>
        public CastCheck CheckOf(CastDevice d)
        {
            var ch = _channel;
            if (ch == null || d == null) return null;
            CastCheck c;
            lock (_gate) return _checks.TryGetValue(CheckKey(ch, d), out c) ? c : null;
        }

        /// <summary>The check of this device is running.</summary>
        public bool IsChecking(CastDevice d)
        {
            var ch = _channel;
            if (ch == null || d == null) return false;
            lock (_gate) return _checking.Contains(CheckKey(ch, d));
        }

        private void CancelChecks()
        {
            var c = _checkCts; _checkCts = null;
            if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
            lock (_gate) _checking.Clear();
        }

        private void StartChecks()
        {
            CancelChecks();
            var ch = _channel;
            if (ch == null) return;
            var cast = Cast;
            var cur = View;
            var todo = new List<CastDevice>();
            lock (_gate)
                foreach (var d in cast.Devices)
                {
                    if (cur.IsActive && cur.Device != null && cur.Device.Id == d.Id) continue;       // the one it is being sent to needs no check
                    if (_checks.ContainsKey(CheckKey(ch, d))) continue;
                    todo.Add(d); _checking.Add(CheckKey(ch, d));
                }
            if (todo.Count == 0) return;
            var cts = new CancellationTokenSource(); _checkCts = cts;
            var local = Local;
            LiveSession open = local != null && local.Channel != null && local.Channel.Id == ch.Id ? local.OpenSession : null;
            Task.Run(async () =>
            {
                foreach (var d in todo)
                {
                    if (cts.IsCancellationRequested) break;
                    CastCheck r;
                    try { r = await cast.CheckAsync(d, ch, open, cts.Token).ConfigureAwait(false) ?? CastCheck.Unknown; }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { AceLog.Warn("ui", "cast check: " + ex.Message); r = CastCheck.Unknown; }
                    lock (_gate) { _checks[CheckKey(ch, d)] = r; _checking.Remove(CheckKey(ch, d)); }
                    try { _ctx.Nav.Invalidate(); } catch (Exception) { }
                }
                lock (_gate) foreach (var d in todo) _checking.Remove(CheckKey(ch, d));
                try { _ctx.Nav.Invalidate(); } catch (Exception) { }
            });
        }

        private static string Signature(IReadOnlyList<CastDevice> devices)
        {
            return string.Join("|", devices.Select(d => d.Id + "/" + d.Name));
        }

        // ---------------------------------------------------------------- frame

        /// <summary>
        /// Advances the logic and says how much has to be drawn: every refresh while something moves (the sheet, a drag, a notice, a pressed button); a steady low rate for the spinners (searching,
        /// checking, connecting); only the slow decoration for the pulse of a sending in progress (it slows down and stops at rest); nothing otherwise.
        /// </summary>
        public FrameDemand Update(double now)
        {
            if (_disposed || !Available) return FrameDemand.Idle;
            Watch();
            string text;
            while (_notices.TryDequeue(out text)) { _toast = text; _toastWarn = true; _toastAt = now; }
            if (_changed)
            {
                _changed = false;
                if (_open)
                {
                    string sig = Signature(Cast.Devices);
                    if (sig != _devicesSig) { _devicesSig = sig; StartChecks(); }
                }
            }
            _scroll.Advance(now);
            if (_anim.Active(now) || _scroll.Active || _scrolling || _sliding || _press.Active(now) || ToastVisible(now)) return FrameDemand.Full;
            var v = View;
            var demand = FrameDemand.Idle;
            if (_open)
            {
                if (Cast.Searching) demand = FrameDemand.Ambient;
                lock (_gate) if (_checking.Count > 0) demand = FrameDemand.Ambient;
            }
            if (v.Phase == CastPhase.Starting && (_open || PillVisible)) demand = FrameDemand.Ambient;
            if (demand == FrameDemand.Idle && v.Phase == CastPhase.Sending && (_open || PillVisible)) demand = FrameDemand.Decor;       // the pulse
            return demand;
        }

        private bool ToastVisible(double now) { return _toast.Length > 0 && now - _toastAt < ToastSeconds + 0.4; }

        public void Dispose()
        {
            _disposed = true;
            CancelChecks();
            if (_watched != null) { _watched.Changed -= OnServiceChanged; _watched.Notice -= OnServiceNotice; _watched = null; }
        }
    }
}
