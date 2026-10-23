using System;
using AceEngine.App;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Gms.Cast;
using Android.Gms.Cast.Framework;
using Android.Gms.Cast.Framework.Media;
using Android.Gms.Common.Apis;
using Android.OS;
using Android.Runtime;
using AndroidX.MediaRouter.Media;
using AceEngine.Cast;
using PUi.Cast;

namespace PAndroid.GoogleCast
{
    /// <summary>
    /// The Google Cast sender (IGoogleCastBridge) on the Cast SDK, the only class that names it. Discovery is MediaRouter's: a callback with the Cast selector (passive while the app is visible,
    /// active while the sheet is open) turns every route that is a screen into a target of the engine's list ("googlecast": kind GoogleCast, the device's IPv4 address, its profile by model).
    /// Connecting selects the route (the framework starts the session); loading is RemoteMediaClient.load of the HLS the engine prepared (live, unknown duration, the title); pause, volume and
    /// the end of the session come from the session and the media client. Everything of the SDK runs on the main thread (a Handler); the interface of this class may be called from any.
    ///
    /// Created ONLY when AndroidCast.IsEnabled (Google Play services present, not a Fire TV, not switched off): on any other device the SDK's classes are never touched.
    /// </summary>
    internal sealed class GoogleCastBridge : IGoogleCastBridge
    {
        private const string Tag = "cast";
        private readonly Context _app;
        private readonly CastTargetList _targets;
        private readonly Handler _main = new Handler(Looper.MainLooper);
        private readonly Dictionary<string, MediaRouter.RouteInfo> _routes = new Dictionary<string, MediaRouter.RouteInfo>();
        private CastContext _cast;
        private MediaRouter _router;
        private MediaRouteSelector _selector;
        private RouteCallback _routeCallback;
        private SessionListener _sessionListener;
        private MediaCallback _mediaCallback;
        private RemoteMediaClient _client;
        private bool _activeScan, _disposed;
        private volatile bool _loaded, _expectEnd;
        private TaskCompletionSource<bool> _starting;
        private string _startingId;

        public event Action<bool, int> StatusChanged;
        public event Action<string> SessionEnded;

        private GoogleCastBridge(Context app, CastTargetList targets) { _app = app; _targets = targets; }

        /// <summary>Creates the bridge and starts the passive search. Main thread (the activity's OnCreate).</summary>
        public static GoogleCastBridge Create(Context app, CastTargetList targets)
        {
            var b = new GoogleCastBridge(app, targets);
            b._cast = CastContext.GetSharedInstance(app);
            b._router = MediaRouter.GetInstance(app);
            b._selector = new MediaRouteSelector.Builder().AddControlCategory(CastMediaControlIntent.CategoryForCast(CastMediaControlIntent.DefaultMediaReceiverApplicationId)).Build();
            b._routeCallback = new RouteCallback(b.Rebuild);
            b._sessionListener = new SessionListener(b);
            b._mediaCallback = new MediaCallback(b);
            var sm = b._cast.SessionManager;
            // a session left over from an earlier run is not ours any more: the engine that served it is gone
            if (sm.CurrentSession != null) { try { sm.EndCurrentSession(true); } catch (Exception ex) { UdpLog.Warn(Tag, "ending a stale session: " + ex.Message); } }
            sm.AddSessionManagerListener(b._sessionListener, Java.Lang.Class.FromType(typeof(CastSession)));
            b._router.AddCallback(b._selector, b._routeCallback, MediaRouter.CallbackFlagRequestDiscovery);
            UdpLog.Info(Tag, "Google Cast sender ready (passive search)");
            return b;
        }

        private void Post(Action a)
        {
            _main.Post(() => { try { if (!_disposed) a(); } catch (Exception ex) { UdpLog.Warn(Tag, "main thread: " + ex.GetType().Name + ": " + ex.Message); } });
        }

        // ---------------------------------------------------------------- discovery
        public void SetActiveScan(bool on)
        {
            Post(() =>
            {
                if (_activeScan == on) return;
                _activeScan = on;
                _router.RemoveCallback(_routeCallback);
                _router.AddCallback(_selector, _routeCallback, MediaRouter.CallbackFlagRequestDiscovery | (on ? MediaRouter.CallbackFlagPerformActiveScan : 0));
                Rebuild();
            });
        }

        private void Rebuild()
        {
            var list = new List<CastTarget>();
            var routes = new Dictionary<string, MediaRouter.RouteInfo>();
            foreach (var r in _router.Routes)
            {
                try
                {
                    if (r.IsDefault || !r.IsEnabled || !r.MatchesSelector(_selector)) continue;
                    var dev = CastDevice.GetFromBundle(r.Extras);
                    if (dev == null) continue;
                    if (!CastModels.IsScreen(true, dev.HasCapability(CastDevice.CapabilityVideoOut), dev.ModelName)) continue;        // a speaker plays no video
                    var ip = Ipv4Of(dev);
                    if (ip == null) continue;                                                                                          // the engine serves to an IPv4 address
                    string id = "gcast:" + dev.DeviceId;
                    list.Add(new CastTarget
                    {
                        Kind = CastTargetKind.GoogleCast, Id = id, Name = string.IsNullOrEmpty(dev.FriendlyName) ? r.Name : dev.FriendlyName, Manufacturer = "Google", Model = dev.ModelName ?? "",
                        Address = ip, Capabilities = CastCapabilities.Hls | CastCapabilities.Pause | CastCapabilities.Volume, Profile = CastModels.ProfileFor(dev.ModelName), Provider = "googlecast"
                    });
                    routes[id] = r;
                }
                catch (Exception ex) { UdpLog.Warn(Tag, "route skipped: " + ex.Message); }
            }
            lock (_routes) { _routes.Clear(); foreach (var kv in routes) _routes[kv.Key] = kv.Value; }
            _targets.Update("googlecast", list);
        }

        private static IPAddress Ipv4Of(CastDevice dev)
        {
            try
            {
                var a = dev.HasIPv4Address ? dev.IpAddress : null;
                var bytes = a == null ? null : a.GetAddress();
                if (bytes != null && bytes.Length == 4) return new IPAddress(bytes);
                var any = dev.InetAddress as Java.Net.Inet4Address;
                bytes = any == null ? null : any.GetAddress();
                return bytes != null && bytes.Length == 4 ? new IPAddress(bytes) : null;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- session
        public async Task ConnectAsync(CastTarget target, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    var sm = _cast.SessionManager;
                    var cur = sm.CurrentCastSession;
                    if (cur != null && cur.IsConnected && cur.CastDevice != null && "gcast:" + cur.CastDevice.DeviceId == target.Id) { AttachClient(cur); tcs.TrySetResult(true); return; }
                    MediaRouter.RouteInfo route;
                    lock (_routes) { if (!_routes.TryGetValue(target.Id, out route)) { tcs.TrySetException(new InvalidOperationException("the Chromecast is no longer in the list")); return; } }
                    _starting = tcs; _startingId = target.Id;
                    if (sm.CurrentSession != null) { _expectEnd = true; sm.EndCurrentSession(true); }
                    _router.SelectRoute(route);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(25))).ConfigureAwait(false);
                if (done != tcs.Task) { tcs.TrySetException(new TimeoutException("the Cast session did not start in 25 s")); }
                await tcs.Task.ConfigureAwait(false);
            }
        }

        private void AttachClient(CastSession s)
        {
            var c = s.RemoteMediaClient;
            if (c == null || ReferenceEquals(c, _client)) return;
            if (_client != null) { try { _client.UnregisterCallback(_mediaCallback); } catch (Exception) { } }
            _client = c;
            c.RegisterCallback(_mediaCallback);
        }

        private void OnSessionStarted(Java.Lang.Object session)
        {
            var s = AsCastSession(session);
            if (s == null) return;
            AttachClient(s);
            _expectEnd = false;
            var tcs = _starting;
            if (tcs != null && s.CastDevice != null && "gcast:" + s.CastDevice.DeviceId == _startingId) { _starting = null; tcs.TrySetResult(true); }
            UdpLog.Info(Tag, "Cast session started with " + (s.CastDevice != null ? s.CastDevice.FriendlyName : "?"));
        }

        private void OnSessionStartFailed(int error)
        {
            var tcs = _starting; _starting = null;
            if (tcs != null) tcs.TrySetException(new InvalidOperationException("the Cast session could not be started (code " + error + ")"));
        }

        private void OnSessionEnded(int error)
        {
            if (_client != null) { try { _client.UnregisterCallback(_mediaCallback); } catch (Exception) { } _client = null; }
            _loaded = false;
            if (_expectEnd) { _expectEnd = false; return; }
            var tcs = _starting; _starting = null;
            if (tcs != null) { tcs.TrySetException(new InvalidOperationException("the Cast session ended while starting (code " + error + ")")); return; }
            UdpLog.Info(Tag, "the Cast session ended by itself (code " + error + ")");
            Raise("El envío ha terminado: se ha parado desde otro dispositivo");
        }

        private void Raise(string text) { var h = SessionEnded; if (h != null) { try { h(text); } catch (Exception ex) { UdpLog.Warn(Tag, "SessionEnded handler: " + ex.Message); } } }

        private static CastSession AsCastSession(Java.Lang.Object o)
        {
            if (o == null) return null;
            try { return o as CastSession ?? o.JavaCast<CastSession>(); } catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- loading and control
        public async Task LoadAsync(CastResult result, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    var session = _cast.SessionManager.CurrentCastSession;
                    var client = session == null ? null : session.RemoteMediaClient;
                    if (client == null) { tcs.TrySetException(new InvalidOperationException("there is no Cast session to load on")); return; }
                    var meta = new MediaMetadata(MediaMetadata.MediaTypeGeneric);
                    meta.PutString(MediaMetadata.KeyTitle, result.Title ?? "");
                    // live, unknown duration: the receiver follows the playlist as it slides (no ENDLIST) and the default receiver plays it with its HLS player
                    var info = new MediaInfo.Builder(result.Url).SetContentUrl(result.Url).SetContentType(result.ContentType).SetStreamType(MediaInfo.StreamTypeLive).SetStreamDuration(MediaInfo.UnknownDuration).SetMetadata(meta).Build();
                    var req = new MediaLoadRequestData.Builder().SetMediaInfo(info).SetAutoplay(Java.Lang.Boolean.True).Build();
                    var pending = client.Load(req);
                    pending.SetResultCallback(new ResultCallback(ok => { if (ok) { _loaded = true; tcs.TrySetResult(true); } else tcs.TrySetException(new InvalidOperationException("the Chromecast refused the channel")); }));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                if (done != tcs.Task) tcs.TrySetException(new TimeoutException("the Chromecast did not answer the load in 30 s"));
                await tcs.Task.ConfigureAwait(false);
            }
        }

        public void Pause() { Post(() => { var c = _client; if (c != null) c.Pause(); }); }
        public void Resume() { Post(() => { var c = _client; if (c != null) c.Play(); }); }

        public void SetVolume(int percent)
        {
            Post(() =>
            {
                var s = _cast.SessionManager.CurrentCastSession;
                if (s == null) return;
                try { s.Volume = Math.Max(0, Math.Min(100, percent)) / 100.0; } catch (Exception ex) { UdpLog.Warn(Tag, "volume: " + ex.Message); }
            });
        }

        public void Disconnect()
        {
            Post(() =>
            {
                var sm = _cast.SessionManager;
                _loaded = false;
                if (sm.CurrentSession == null) { _expectEnd = false; return; }
                _expectEnd = true;                                                  // the end of THIS session is our doing: not told to the user
                try { sm.EndCurrentSession(true); } catch (Exception ex) { UdpLog.Warn(Tag, "ending the session: " + ex.Message); _expectEnd = false; }
                _main.PostDelayed(() => _expectEnd = false, 4000);
            });
        }

        /// <summary>The media client reports a change: paused or not, the device's volume, and whether the receiver stopped playing what was loaded.</summary>
        private void OnMediaStatus()
        {
            var c = _client; var s = _cast.SessionManager.CurrentCastSession;
            if (c == null) return;
            var status = c.MediaStatus;
            int vol = s != null ? (int)Math.Round(s.Volume * 100) : -1;
            int state = c.PlayerState;
            if (_loaded && state == MediaStatus.PlayerStateIdle && !_expectEnd)
            {
                int why = c.IdleReason;
                if (why == MediaStatus.IdleReasonError) { _loaded = false; Raise("El Chromecast no ha podido reproducir el canal"); return; }
                if (why == MediaStatus.IdleReasonFinished || why == MediaStatus.IdleReasonCanceled) { _loaded = false; Raise("El envío ha terminado: el Chromecast ha dejado de reproducir el canal"); return; }
            }
            var h = StatusChanged;
            if (h != null) h(c.IsPaused, vol);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _main.Post(() =>
            {
                try
                {
                    _router.RemoveCallback(_routeCallback);
                    var sm = _cast.SessionManager;
                    sm.RemoveSessionManagerListener(_sessionListener, Java.Lang.Class.FromType(typeof(CastSession)));
                    if (_client != null) _client.UnregisterCallback(_mediaCallback);
                    if (sm.CurrentSession != null) sm.EndCurrentSession(true);
                }
                catch (Exception ex) { UdpLog.Warn(Tag, "dispose: " + ex.Message); }
            });
            _targets.Update("googlecast", new CastTarget[0]);
        }

        // ---------------------------------------------------------------- the SDK's callbacks
        private sealed class RouteCallback : MediaRouter.Callback
        {
            private readonly Action _changed;
            public RouteCallback(Action changed) { _changed = changed; }
            public override void OnRouteAdded(MediaRouter router, MediaRouter.RouteInfo route) { _changed(); }
            public override void OnRouteChanged(MediaRouter router, MediaRouter.RouteInfo route) { _changed(); }
            public override void OnRouteRemoved(MediaRouter router, MediaRouter.RouteInfo route) { _changed(); }
        }

        private sealed class SessionListener : Java.Lang.Object, ISessionManagerListener
        {
            private readonly GoogleCastBridge _b;
            public SessionListener(GoogleCastBridge b) { _b = b; }
            public void OnSessionStarting(Java.Lang.Object session) { }
            public void OnSessionStarted(Java.Lang.Object session, string sessionId) { _b.OnSessionStarted(session); }
            public void OnSessionStartFailed(Java.Lang.Object session, int error) { _b.OnSessionStartFailed(error); }
            public void OnSessionEnding(Java.Lang.Object session) { }
            public void OnSessionEnded(Java.Lang.Object session, int error) { _b.OnSessionEnded(error); }
            public void OnSessionResuming(Java.Lang.Object session, string sessionId) { }
            public void OnSessionResumed(Java.Lang.Object session, bool wasSuspended) { _b.OnSessionStarted(session); }
            public void OnSessionResumeFailed(Java.Lang.Object session, int error) { _b.OnSessionStartFailed(error); }
            public void OnSessionSuspended(Java.Lang.Object session, int reason) { UdpLog.Info(Tag, "Cast session suspended (" + reason + ")"); }
        }

        private sealed class MediaCallback : RemoteMediaClient.Callback
        {
            private readonly GoogleCastBridge _b;
            public MediaCallback(GoogleCastBridge b) { _b = b; }
            public override void OnStatusUpdated() { _b.OnMediaStatus(); }
        }

        private sealed class ResultCallback : Java.Lang.Object, IResultCallback
        {
            private readonly Action<bool> _done;
            public ResultCallback(Action<bool> done) { _done = done; }
            public void OnResult(Java.Lang.Object result)
            {
                bool ok = false;
                try { var r = result as IResult ?? result.JavaCast<IResult>(); ok = r != null && r.Status != null && r.Status.IsSuccess; }
                catch (Exception ex) { UdpLog.Warn(Tag, "load result: " + ex.Message); }
                _done(ok);
            }
        }
    }
}
