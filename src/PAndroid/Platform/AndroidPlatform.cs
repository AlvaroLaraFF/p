using System;
using System.Collections.Generic;
using Android.App;
using Android.OS;
using Android.Views;
using AceEngine.App;
using PUi.Input;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>
    /// The Android platform object that PUi sees: the drawing view (AndroidSkiaView), keys and touch translated to the contract, the system keyboard, the opaque or transparent window over the
    /// video and where the video goes (IVideoPlacement), the screen orientation (IOrientationControl), audio focus, the main thread and the app's paths. Keys and touch come from the activity (DispatchKeyEvent / DispatchTouchEvent), which hands them to OnKeyEvent and OnTouchEvent.
    /// </summary>
    internal sealed class AndroidPlatform : IPlatform, IInputSource, IWindowControl, IUiThread, IPlatformPaths, IVideoPlacement, IOrientationControl
    {
        private readonly Activity _activity;
        private readonly AndroidDeviceProfile _profile;
        private readonly AndroidSkiaView _view;
        private readonly VideoFrame _video;
        private readonly AndroidAudioFocus _audio;
        private readonly AndroidTextInput _textInput;
        private readonly ISystemInfo _sysInfo;
        private readonly Action _exit;
        private readonly Action<ScreenLock, bool> _applyOrientation;
        private readonly Handler _main = new Handler(Looper.MainLooper);
        // The platform object is created in OnCreate, on the main thread: that is the interface thread
        private readonly int _uiThreadId = System.Environment.CurrentManagedThreadId;
        private readonly KeyDispatcher _keys = new KeyDispatcher();
        private readonly List<TouchPoint> _touchPoints = new List<TouchPoint>(10);
        private readonly List<PointerEvent> _pointerEvents = new List<PointerEvent>(10);
        private bool _opaque = true;
        private int _keyCount, _touchCount;
        private PixelRect? _videoRect;                     // where PUi wants the video, in pixels of the drawing surface; null = the whole screen
        private OrientationRequest _orientation = OrientationRequest.Device;

        /// <param name="applyOrientation">Applies a screen lock and the immersive mode (the activity's side of IOrientationControl).</param>
        public AndroidPlatform(Activity activity, AndroidDeviceProfile profile, AndroidSkiaView view, VideoFrame video, AndroidAudioFocus audio, string dataDir, string fontsDir, Action exit, Action<ScreenLock, bool> applyOrientation)
        {
            _activity = activity; _profile = profile; _view = view; _video = video; _audio = audio; DataDir = dataDir; FontsDir = fontsDir; _exit = exit; _applyOrientation = applyOrientation;
            _sysInfo = new AndroidSystemInfo(activity.ApplicationContext, profile);
            if (profile.Capabilities.HasTouch)
            {
                _textInput = new AndroidTextInput(activity, view);
                _view.TextInput = _textInput;
            }
            // the rectangle PUi gave the video is in pixels of the drawing surface: when the surface or the view changes it goes where it belongs in the view
            _view.ScreenChanged += PlaceVideo;
            PlaybackMonitor.Changed += ApplyKeepScreenOn;
        }

        /// <summary>The activity is going: stops listening to the players.</summary>
        public void Detach() { PlaybackMonitor.Changed -= ApplyKeepScreenOn; }

        public string Name { get { return "android"; } }
        public IRenderHost Render { get { return _view; } }
        public IInputSource Input { get { return this; } }
        public IWindowControl Window { get { return this; } }
        public IAudioFocus Audio { get { return _audio; } }
        public IUiThread UiThread { get { return this; } }
        public IPlatformPaths Paths { get { return this; } }
        public ISystemInfo SystemInfo { get { return _sysInfo; } }
        public PlatformCapabilities Capabilities { get { return _profile.Capabilities; } }
        public ISnapshotSource Snapshot { get { return _view; } }
        /// <summary>The system keyboard on touch devices (PUi asks for it to type in Search); null on a TV or a box, where PUi's own keyboard is driven by the remote.</summary>
        public ITextInput TextInput { get { return _textInput; } }
        public string DataDir { get; private set; }
        public string FontsDir { get; private set; }
        public AndroidDeviceProfile Profile { get { return _profile; } }
        public void Exit() { _exit(); }

        // ================================================================ IVideoPlacement
        /// <summary>
        /// The video goes in this rectangle of the surface (the 16:9 area at the top of a phone's player, the whole surface in landscape): the picture is fitted inside it and PUi leaves those
        /// pixels transparent. The rectangle is in pixels of the drawing surface, which may be smaller than the view.
        /// </summary>
        public void SetVideoRect(PixelRect rect)
        {
            _videoRect = rect;
            if (((IUiThread)this).IsUiThread) PlaceVideo(); else _main.Post(PlaceVideo);
        }

        public void ClearVideoRect()
        {
            _videoRect = null;
            if (((IUiThread)this).IsUiThread) PlaceVideo(); else _main.Post(PlaceVideo);
        }

        private void PlaceVideo()
        {
            try
            {
                var r = _videoRect;
                if (!r.HasValue) { _video.ClearRect(); return; }
                var screen = _view.Screen;
                int l, t, w, h;
                ScreenMath.SurfaceRectToView(r.Value, _view.Width, _view.Height, screen.PixelWidth, screen.PixelHeight, out l, out t, out w, out h);
                _video.SetRect(l, t, w, h);
            }
            catch (Exception ex) { UdpLog.Warn("ui", "placing the video: " + ex.Message); }
        }

        // ================================================================ IOrientationControl
        /// <summary>
        /// PUi's wish for the screen orientation (see OrientationPolicy): "Pantalla completa" asks for Landscape, leaving it asks for Portrait (a phone) or Device (a tablet). The activity locks
        /// the screen and hides or shows the system bars; the surface that results comes through ScreenChanged. A TV has one orientation and ignores it.
        /// </summary>
        public void Request(OrientationRequest request)
        {
            _orientation = request;
            var kind = _profile.Kind;
            var scr = OrientationPolicy.For(kind, request);
            bool immersive = OrientationPolicy.WantsImmersive(kind, request);
            UdpLog.Info("ui", "orientation requested: " + request + " -> " + scr + (immersive ? ", system bars hidden" : ", system bars shown"));
            Action apply = () => { try { _applyOrientation(scr, immersive); } catch (Exception ex) { UdpLog.Warn("ui", "orientation: " + ex.Message); } };
            if (((IUiThread)this).IsUiThread) apply(); else _main.Post(apply);
        }

        /// <summary>The system bars the activity should show now: hidden on a TV and in full screen.</summary>
        public bool WantsImmersive { get { return OrientationPolicy.WantsImmersive(_profile.Kind, _orientation); } }

        // ================================================================ keys
        public event Action<KeyPress> KeyPressed;
        public event Action<PointerEvent> Pointer;

        /// <summary>Raises a logical key as if the user had pressed it (the key script, and the system Back).</summary>
        public void Raise(KeyPress kp) { var h = KeyPressed; if (h != null) h(kp); }

        /// <summary>
        /// A key event from the activity. Returns true if it is consumed. The system Back button and gesture arrive as KEYCODE_BACK and become UiKey.Back (with targetSdk 35 the gesture still sends
        /// the key); Home, volume, power and the assistant button are not mapped and so not consumed. While the system keyboard is shown the typed keys of a hardware keyboard go to it instead of
        /// being raised as keys, Enter submits, and Back closes the keyboard instead of leaving the screen.
        /// </summary>
        public bool OnKeyEvent(KeyEvent e)
        {
            KeyPress press; bool raise;
            int code = (int)e.KeyCode;
            bool down = e.Action == KeyEventActions.Down;
            var ti = _textInput;
            if (ti != null && ti.IsShown && TextKey(ti, code, e, down)) return true;
            if (!_keys.OnKey(code, down, e.RepeatCount, e.UnicodeChar, e.IsCtrlPressed, e.IsAltPressed, out press, out raise))
            {
                // A key the interface does not use and the system keeps (Home, volume, the buttons of a TV remote...): logged, so the codes of a new remote can be learned from the log
                if (down && e.RepeatCount == 0) UdpLog.Info("keys", "KEY " + AndroidKeys.Name(code) + " code=" + code + " -> left to the system" + (AndroidKeys.IsSystemKey(code) ? " (system key)" : " (not used)") + " device=" + DeviceName(e));
                return false;
            }
            if (raise)
            {
                if (e.RepeatCount == 0) { _keyCount++; UdpLog.Info("keys", "KEY " + AndroidKeys.Name(code) + " code=" + code + " -> " + press.Key + (press.Key == UiKey.Char ? " '" + press.Ch + "'" : "") + " device=" + DeviceName(e)); }
                Raise(press);
            }
            return true;
        }

        /// <summary>"id:name" of the input device that sent the key, for the log ("Amazon Fire TV Remote", a game pad's name...).</summary>
        private static string DeviceName(KeyEvent e)
        {
            try { var d = e.Device; return d != null ? d.Id + ":" + d.Name : e.DeviceId.ToString(); }
            catch (Exception) { return e.DeviceId.ToString(); }
        }

        /// <summary>The key handling while the keyboard is shown. true if the key was taken (its release is consumed along with it).</summary>
        private bool TextKey(AndroidTextInput ti, int code, KeyEvent e, bool down)
        {
            if (code == KeyCodes.Back) { if (down && e.RepeatCount == 0) ti.SystemDismissed("Back"); return true; }
            if (code == KeyCodes.Enter || code == KeyCodes.NumpadEnter) { if (down && e.RepeatCount == 0) ti.Submit(); return true; }
            var kp = AndroidKeys.Map(code, e.UnicodeChar, e.IsCtrlPressed, e.IsAltPressed);
            if (kp.Key == UiKey.Char) { if (down) ti.TypeChar(kp.Ch); return true; }
            if (kp.Key == UiKey.Backspace) { if (down) ti.Backspace(); return true; }
            return false;
        }

        /// <summary>The window lost focus: the releases of the keys held down will not arrive, and the system keyboard goes with it.</summary>
        public void OnFocusLost()
        {
            _keys.Reset();
            var ti = _textInput;
            if (ti != null) ti.SystemDismissed("focus lost");
        }

        /// <summary>Back as the system reports it when it does not send the key event: the same as the key.</summary>
        public void OnBackPressed()
        {
            var ti = _textInput;
            if (ti != null && ti.IsShown) ti.SystemDismissed("Back");
            else Raise(new KeyPress(UiKey.Back, "OnBackPressed"));
        }

        /// <summary>
        /// A touch event from the activity, as PointerEvents in the interface's logical coordinates (origin at its canvas corner: Screen.ToLogicalX / ToLogicalY, which also know where a letterboxed
        /// canvas sits), with the event's own time on the clock of NeedsFrame. Returns true if the platform has touch and somebody listens (the event is consumed).
        /// </summary>
        public bool OnTouchEvent(MotionEvent e)
        {
            if (!_profile.Capabilities.HasTouch || Pointer == null) return false;
            int viewW = _view.Width, viewH = _view.Height;
            if (viewW <= 0 || viewH <= 0) return false;
            var screen = _view.Screen;
            _touchPoints.Clear(); _pointerEvents.Clear();
            for (int i = 0; i < e.PointerCount; i++) _touchPoints.Add(new TouchPoint(e.GetPointerId(i), e.GetX(i), e.GetY(i)));
            // the view fills the window, so the event's coordinates are the view's; the surface may be smaller than the view
            TouchTranslator.Translate((int)e.Action, _touchPoints, e.EventTime / 1000.0, screen, ScreenMath.SurfaceScale(viewW, screen.PixelWidth), ScreenMath.SurfaceScale(viewH, screen.PixelHeight), _pointerEvents);
            if (_pointerEvents.Count == 0) return true;
            if (e.ActionMasked == MotionEventActions.Down) _touchCount++;
            foreach (var pe in _pointerEvents) { var h = Pointer; if (h != null) h(pe); }
            return true;
        }

        public string InputState() { return "keys=" + _keyCount + "/touches=" + _touchCount; }

        // ================================================================ window
        /// <summary>
        /// Opaque (all screens but the player) or transparent over the video. The video surface below is never hidden: when the interface is opaque it simply covers it.
        /// </summary>
        bool IWindowControl.Opaque
        {
            get { return _opaque; }
            set
            {
                if (_opaque == value) return;
                _opaque = value;
                UdpLog.Info("ui", "window " + (value ? "opaque" : "transparent"));
                if (((IUiThread)this).IsUiThread) _view.SetContentOpaque(value); else _main.Post(() => _view.SetContentOpaque(value));
            }
        }

        private bool _keepScreenOn;                   // what the interface asks for: the player screen is open
        private bool _screenIsOn;                     // what is applied to the view
        bool IWindowControl.KeepScreenOn
        {
            get { return _keepScreenOn; }
            set { _keepScreenOn = value; ApplyKeepScreenOn(); }
        }

        /// <summary>
        /// The screen stays on while the player screen is open AND a channel is playing; on a TV device a paused channel (the viewer's pause, or another app's audio holding it: Alexa) lets
        /// the screensaver start, as Amazon requires (ScreenKeepPolicy). Any thread.
        /// </summary>
        private void ApplyKeepScreenOn()
        {
            bool on = ScreenKeepPolicy.KeepOn(_keepScreenOn, _profile.ReleaseScreenWhenNotPlaying, PlaybackMonitor.AnyPaused);
            Action apply = () =>
            {
                try
                {
                    _view.KeepScreenOn = on;
                    if (on != _screenIsOn) { _screenIsOn = on; UdpLog.Info("ui", "keep screen on: " + (on ? "yes" : "no" + (_keepScreenOn ? " (the channel is paused: the screensaver may start)" : ""))); }
                }
                catch (Exception ex) { UdpLog.Warn("ui", "KeepScreenOn: " + ex.Message); }
            };
            if (((IUiThread)this).IsUiThread) apply(); else _main.Post(apply);
        }

        // ================================================================ main thread
        void IUiThread.Post(Action action)
        {
            _main.Post(() =>
            {
                try { action(); }
                catch (Exception ex) { UdpLog.Error("ui", "exception on the main thread: " + ex); }
            });
        }

        bool IUiThread.IsUiThread { get { return System.Environment.CurrentManagedThreadId == _uiThreadId; } }
    }
}
