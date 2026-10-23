using System;
using System.Collections.Generic;
using AceEngine.App;
using PUi.Input;
using PUi.Platform;
using SkiaSharp;

namespace PUi.Testing
{
    /// <summary>
    /// Windowless desktop platform: CPU surface, simulated keys, touch and audio focus changes, and controlled time (for PNGs and tests). It follows the platform contract (PUi.Platform):
    /// the interface thread is the thread that created it (IsUiThread is true only there), a TV-like set of capabilities by default (Capabilities can be replaced to play a phone),
    /// and no snapshot source unless a test gives one.
    /// </summary>
    public sealed class DesktopPlatform : IPlatform, IRenderHost, IInputSource, IWindowControl, IAudioFocus, IUiThread, IPlatformPaths, IVideoPlacement, IOrientationControl
    {
        public string Name { get { return "desktop"; } }
        public IRenderHost Render { get { return this; } }
        public IInputSource Input { get { return this; } }
        public IWindowControl Window { get { return this; } }
        public IAudioFocus Audio { get { return this; } }
        public IUiThread UiThread { get { return this; } }
        public IPlatformPaths Paths { get { return this; } }
        public ISystemInfo SystemInfo { get { return new DesktopSystemInfo(); } }
        /// <summary>A full TV remote by default; a test that wants a phone or a Fire TV remote replaces it.</summary>
        public PlatformCapabilities Capabilities { get; set; } = PlatformCapabilities.FullRemote;
        /// <summary>Null unless a test sets one.</summary>
        public ISnapshotSource Snapshot { get; set; }
        /// <summary>A system keyboard that the tests drive (FakeTextInput); set it to null to play a platform without one. PUi only looks at it when Capabilities.HasTouch.</summary>
        public ITextInput TextInput { get; set; } = new FakeTextInput();

        public bool Exited;
        public void Exit() { Exited = true; }

        // IRenderHost
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        private ScreenInsets _insets;
        private float _density = 1f;
        /// <summary>Areas the system covers (a test can set them to play a phone).</summary>
        public ScreenInsets Insets { get { return _insets; } set { if (_insets.Equals(value)) return; _insets = value; RaiseScreenChanged(); } }
        /// <summary>Device pixels per dp (a phone of 6.5 inches and 2400x1080 has about 2.53).</summary>
        public float Density { get { return _density; } set { if (_density == value) return; _density = value; RaiseScreenChanged(); } }
        public ScreenMetrics Screen { get { return new ScreenMetrics(PixelWidth, PixelHeight, _insets, _density, Capabilities != null && Capabilities.HasTouch); } }
        public event Action ScreenChanged;
        private void RaiseScreenChanged() { var h = ScreenChanged; if (h != null) h(); }
        /// <summary>Resizes or rotates the surface and raises ScreenChanged, as a phone would.</summary>
        public void ChangeScreen(int pixelWidth, int pixelHeight, ScreenInsets insets = default(ScreenInsets))
        {
            PixelWidth = pixelWidth; PixelHeight = pixelHeight; _insets = insets;
            RaiseScreenChanged();
        }
        public IFramePresenter Presenter;
        public void SetPresenter(IFramePresenter presenter) { Presenter = presenter; }
        public void Start() { }
        public void Stop() { }
        public int Redraws;
        public void RequestRedraw() { Redraws++; }

        // IVideoPlacement (optional contract): what the player asked for, for the tests and for the bench
        /// <summary>The rectangle (pixels) the player last asked the video to be in; null = the whole surface (never asked, or cleared).</summary>
        public PixelRect? VideoRect;
        public int VideoRectCalls;
        public void SetVideoRect(PixelRect rect) { VideoRect = rect; VideoRectCalls++; Note("video rect " + rect); }
        public void ClearVideoRect() { if (VideoRect == null) return; VideoRect = null; Note("video rect cleared"); }

        // IOrientationControl (optional contract)
        /// <summary>Every orientation request the interface made, in order.</summary>
        public readonly List<OrientationRequest> OrientationRequests = new List<OrientationRequest>();
        public OrientationRequest? LastOrientation { get { return OrientationRequests.Count == 0 ? (OrientationRequest?)null : OrientationRequests[OrientationRequests.Count - 1]; } }
        /// <summary>When true the platform plays the shell: a request for Landscape or Portrait turns the surface (width and height swap) and raises ScreenChanged, as a phone would.</summary>
        public bool SimulateRotation;
        public void Request(OrientationRequest request)
        {
            OrientationRequests.Add(request); Note("orientation " + request);
            if (!SimulateRotation) return;
            bool wantLandscape = request == OrientationRequest.Landscape, wantPortrait = request == OrientationRequest.Portrait;
            bool landscape = PixelWidth >= PixelHeight;
            if ((wantLandscape && !landscape) || (wantPortrait && landscape))
            {
                var i = _insets;
                // the cutout (top in portrait) goes to the left in landscape and back; the gesture bar stays at the bottom
                ChangeScreen(PixelHeight, PixelWidth, wantLandscape ? new ScreenInsets(i.Top, 0, 0, i.Bottom) : new ScreenInsets(0, i.Left, 0, i.Bottom));
            }
        }

        // IInputSource
        public event Action<KeyPress> KeyPressed;
        public void Raise(KeyPress kp) { var h = KeyPressed; if (h != null) h(kp); }
        public void Press(string x11Name) { Raise(KeyNames.FromName(x11Name)); }
        public void PressKey(UiKey k, char ch = '\0') { Raise(new KeyPress(k, k.ToString(), ch)); }
        public void Type(string text) { foreach (char c in text) PressKey(UiKey.Char, c); }
        public event Action<PointerEvent> Pointer;
        /// <summary>A touch event in LOGICAL coordinates, like a touch platform raises it.</summary>
        public void Touch(PointerAction action, float x, float y, int id = 0, double time = 0) { var h = Pointer; if (h != null) h(new PointerEvent(action, x, y, time, id)); }

        // IWindowControl
        private bool _opaque = true;
        public bool Opaque { get { return _opaque; } set { _opaque = value; Note("window " + (value ? "opaque" : "transparent")); } }
        private bool _keepScreenOn;
        public bool KeepScreenOn { get { return _keepScreenOn; } set { _keepScreenOn = value; Note("keep screen on " + (value ? "yes" : "no")); } }
        // IAudioFocus
        public bool AudioHeld;
        void IAudioFocus.Acquire() { AudioHeld = true; Note("audio acquire"); }
        void IAudioFocus.Release() { AudioHeld = false; Note("audio release"); }
        public event Action<AudioFocusChange> Changed;
        /// <summary>The system changes the focus, as Android does.</summary>
        public void RaiseAudioFocus(AudioFocusChange change) { var h = Changed; if (h != null) h(change); }

        /// <summary>Called, in order, with every window and audio focus change ("window opaque", "audio release"...): for the tests that check the lifecycle order.</summary>
        public Action<string> EventObserver;
        private void Note(string e) { var h = EventObserver; if (h != null) h(e); }
        // IUiThread
        public readonly Queue<Action> Posted = new Queue<Action>();
        public bool ImmediatePost = true;
        public void Post(Action action) { if (ImmediatePost) action(); else Posted.Enqueue(action); }
        /// <summary>The thread that created the platform (a test's own thread): IsUiThread is true only there.</summary>
        public int UiThreadId = Environment.CurrentManagedThreadId;
        public bool IsUiThread { get { return Environment.CurrentManagedThreadId == UiThreadId; } }
        // IPlatformPaths
        public string DataDir { get; set; } = System.IO.Path.GetTempPath();
        public string FontsDir { get; set; }

        public DesktopPlatform(string fontsDir) { FontsDir = fontsDir; PixelWidth = 1920; PixelHeight = 1080; }
    }
}
