using System;
using AceEngine.App;
using ElmSharp;
using PUi.Input;
using PUi.Platform;
using Tizen.Multimedia;

namespace PTizen.Platform
{
    /// <summary>Samsung TV (Tizen 5.5): ElmSharp window, Skia Evas GL view, remote keys, audio focus and opaque or transparent window.</summary>
    internal sealed class TizenPlatform : IPlatform, IInputSource, IWindowControl, IAudioFocus, IUiThread, IPlatformPaths
    {
        private readonly Window _window;
        private readonly SkiaView _view;
        private readonly Action _exit;
        private readonly DecoderWarmer _warmer;
        private EcoreEvent<EcoreKeyEventArgs> _keyDown, _keyUp;
        private int _keyCount;
        private AudioStreamPolicy _audio;
        private bool _audioHeld;

        /// <summary>
        /// The app's audio policy (the one holding the focus). The Player uses it as its own (ApplyAudioStreamPolicy before Prepare): this way it does not ask for a focus of its own when created and the
        /// TV source does not get the audio back in the gap. null until the app acquires the focus.
        /// </summary>
        public AudioStreamPolicy AudioPolicy { get { return _audioHeld ? _audio : null; } }

        /// <summary>Current state of the playback audio focus (for the log of the transition to the player).</summary>
        public string AudioFocusText()
        {
            try { return _audio == null ? "no policy" : _audio.PlaybackFocusState.ToString() + (_audioHeld ? " (held)" : " (not held)"); }
            catch (Exception ex) { return "?" + ex.Message; }
        }
        private bool _opaque = true;
        private bool _keepScreenOn;
        // The platform object is created in OnCreate, on the Ecore main loop's thread: that is the interface thread
        private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

        public TizenPlatform(Window window, SkiaView view, string dataDir, string fontsDir, string resourceDir, Action exit)
        {
            _window = window; _view = view; DataDir = dataDir; FontsDir = fontsDir; _exit = exit;
            _warmer = new DecoderWarmer(window, resourceDir);
        }

        public string Name { get { return "tizen"; } }
        public IRenderHost Render { get { return _view; } }
        public IInputSource Input { get { return this; } }
        public IWindowControl Window { get { return this; } }
        public IAudioFocus Audio { get { return this; } }
        public IUiThread UiThread { get { return this; } }
        public IPlatformPaths Paths { get { return this; } }
        private readonly TizenSystemInfo _sysInfo = new TizenSystemInfo();
        public ISystemInfo SystemInfo { get { return _sysInfo; } }
        /// <summary>The Samsung remote: channel, digit and colour keys, D-pad; no touch and no keyboard.</summary>
        public PlatformCapabilities Capabilities { get { return PlatformCapabilities.FullRemote; } }
        /// <summary>The TV has no system keyboard to ask for: PUi's own one is used.</summary>
        public ITextInput TextInput { get { return null; } }
        /// <summary>The Evas view can dump what it drew.</summary>
        public ISnapshotSource Snapshot { get { return _view; } }
        public string DataDir { get; private set; }
        public string FontsDir { get; private set; }
        public void Exit() { _exit(); }

        // ---------------------------------------------------------------- keys
        public event Action<KeyPress> KeyPressed;
        /// <summary>The TV has no touch: never raised.</summary>
        public event Action<PointerEvent> Pointer { add { } remove { } }

        public void HookKeys()
        {
            // Channel +/- and the media keys have to be registered so the system does not consume them. Every pressed key is logged.
            foreach (var key in TizenKeys.GrabNames)
            {
                try { _window.KeyGrabEx(key); UdpLog.Info("keys", "KeyGrabEx " + key + " registered"); }
                catch (Exception e) { UdpLog.Warn("keys", "KeyGrabEx " + key + ": " + e.Message); }
            }
            _keyDown = new EcoreEvent<EcoreKeyEventArgs>(EcoreEventType.KeyDown, EcoreKeyEventArgs.Create);
            _keyDown.On += (s, e) =>
            {
                var kp = KeyNames.FromName(e.KeyName);
                UdpLog.Info("keys", "KEY name=" + e.KeyName + " code=" + e.KeyCode + " -> " + kp.Key + (kp.Key == UiKey.Char ? " '" + kp.Ch + "'" : ""));
                _keyCount++;
                Raise(kp);
            };
            _keyUp = new EcoreEvent<EcoreKeyEventArgs>(EcoreEventType.KeyUp, EcoreKeyEventArgs.Create);
            _keyUp.On += (s, e) => UdpLog.Info("keys", "KEY-UP name=" + e.KeyName + " focus=" + FocusText());
            // When the TV puts something of its own on top (Home menu, notices) the window loses focus WITHOUT OnPause: this shows in the log at exactly which step
            // it happens (e.g. on leaving the player with Back, 2026-10-17, the TV sometimes opens its Home and keeps the remote).
            _window.Focused += (s, e) => UdpLog.Info("ui", "window: focus received");
            _window.Unfocused += (s, e) => UdpLog.Warn("ui", "window: focus LOST (something of the TV has been put on top)");
            foreach (var ev in new[] { "fully_obscured", "unobscured", "iconified", "normal", "withdrawn" })
            {
                try { var name = ev; var se = new SmartEvent(_window, name); se.On += (s, e) => UdpLog.Info("ui", "window: " + name); _winEvents.Add(se); }
                catch (Exception ex) { UdpLog.Warn("ui", "window event " + ev + ": " + ex.Message); }
            }
        }

        private readonly System.Collections.Generic.List<SmartEvent> _winEvents = new System.Collections.Generic.List<SmartEvent>();

        private string FocusText()
        {
            try { return Elm.elm_win_focus_get(_window.Handle) ? "yes" : "NO"; } catch (Exception) { return "?"; }
        }

        /// <summary>Window state for the statistics log: opaque or with alpha, and whether it has keyboard focus.</summary>
        public string WindowState() { return (_opaque ? "opaque" : "alpha") + "/focus=" + FocusText() + "/keys=" + _keyCount + "/img=" + _view.FramesPresented; }

        /// <summary>
        /// The window must be transparent BEFORE creating the player and giving it the window as its display: changing Window.Alpha with the video already running recreates the
        /// surface and the system interrupts playback (PlaybackInterrupted ResourceConflict). Returns true if it changed (you have to wait for it to be recreated).
        /// </summary>
        public bool EnsureTransparent()
        {
            if (!_opaque) return false;
            ((IWindowControl)this).Opaque = false;
            return true;
        }

        public void Raise(KeyPress kp) { var h = KeyPressed; if (h != null) h(kp); }

        // ---------------------------------------------------------------- opaque window (50-60 fps) or transparent (video behind)
        bool IWindowControl.Opaque
        {
            get { return _opaque; }
            set
            {
                if (_opaque == value) return;
                _opaque = value;
                try { _window.Alpha = !value; _view.SetImageAlpha(!value); _view.RequestRedraw(); UdpLog.Info("ui", "window " + (value ? "opaque" : "transparent") + " (focus=" + FocusText() + ")"); }
                catch (Exception e) { UdpLog.Warn("ui", "Window.Alpha: " + e.Message); }
            }
        }

        /// <summary>The TV keeps the screen on while video plays by itself: the value is only kept.</summary>
        bool IWindowControl.KeepScreenOn { get { return _keepScreenOn; } set { _keepScreenOn = value; } }

        // ---------------------------------------------------------------- audio focus: the background TV source stops sounding
        /// <summary>Nobody takes the focus from the app on the TV (it only silences the TV source): never raised.</summary>
        event Action<AudioFocusChange> IAudioFocus.Changed { add { } remove { } }

        void IAudioFocus.Acquire()
        {
            if (_audioHeld) return;
            try
            {
                if (_audio == null)
                {
                    _audio = new AudioStreamPolicy(AudioStreamType.Media);
                    _audio.FocusStateChanged += (s, e) => UdpLog.Info("audio", "focus: state=" + e.FocusState + " options=" + e.FocusOptions + " reason=" + e.Reason);
                }
                _audio.AcquireFocus(AudioStreamFocusOptions.Playback, (AudioStreamBehaviors)0, "p");
                _audioHeld = true;
                UdpLog.Info("audio", "focus acquired: " + _audio.PlaybackFocusState);
            }
            catch (Exception e) { UdpLog.Warn("audio", "could not acquire the focus: " + e.Message); }
        }

        void IAudioFocus.Release()
        {
            if (_audio == null || !_audioHeld) return;
            try { _audio.ReleaseFocus(AudioStreamFocusOptions.Playback, (AudioStreamBehaviors)0, "p"); _audioHeld = false; UdpLog.Info("audio", "focus released"); }
            catch (Exception e) { UdpLog.Warn("audio", "could not release the focus: " + e.Message); }
        }

        // ---------------------------------------------------------------- main thread
        void IUiThread.Post(Action action) { Ui.Post(action); }
        bool IUiThread.IsUiThread { get { return Environment.CurrentManagedThreadId == _uiThreadId; } }

        // ---------------------------------------------------------------- decoder warmer (see DecoderWarmer.cs)
        /// <summary>Keeps the decoder busy with a black, silent clip while there is no channel: the TV source goes quiet from the moment the app opens.</summary>
        public void StartWarmer(bool enabled = true) { _warmer.Enabled = enabled; _warmer.Start(); }
        /// <summary>Releases the decoder right before creating the real channel's Player (a single decoder on this TV).</summary>
        public void StopWarmerForHandoff() { _warmer.StopForHandoff(); }
        /// <summary>The app is hidden or closed: releases the decoder and does not take it again until ResumeWarmer.</summary>
        public void SuspendWarmer() { _warmer.Suspend(); }
        /// <summary>The app is visible again.</summary>
        public void ResumeWarmer() { _warmer.Resume(); }
    }
}
