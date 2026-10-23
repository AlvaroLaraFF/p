// Interfaces that each platform implements (PTizen, PDesktop, PAndroid). PUi and the app depend only on them.
// The video player (open URL, play/pause, position, state, errors, buffering) is AceEngine.App.IPlayerBackend / IPlayerFactory:
// the platform implements it and puts it in TvServices.Playback; PUi only sees IPlayback.
//
// Contract in short (each interface documents its part):
//  - everything runs on the interface thread (IUiThread); IsUiThread tells the truth;
//  - input arrives as logical keys (IInputSource.KeyPressed; a touch platform maps its Back gesture to UiKey.Back) and, on touch platforms, as pointer events in logical
//    coordinates; PlatformCapabilities says which keys and input kinds exist;
//  - the drawing surface and its safe insets are IRenderHost.Screen; the window is opaque or transparent over the video and may be kept awake while the player is open;
//  - the audio focus is acquired on opening/resuming and released on pausing/leaving; the system may take it away (IAudioFocus.Changed);
//  - the platform may offer screen captures (ISnapshotSource) for the tests;
//  - a touch platform also offers the system keyboard (ITextInput): PUi draws the text field and asks for the keyboard to type in it. Elsewhere TextInput is null;
//  - optional contracts, found by PUi when the platform object (or its Window or Render) implements them; the Samsung TV implements none and nothing changes there:
//      IVideoPlacement (where on the surface the video goes: the player of a phone or a tablet draws in an area, not the whole screen),
//      IOrientationControl (the phone's full screen player asks for landscape and gives it back; ScreenChanged reports the result) and
//      ICastService ("Enviar a la tele"; the button is not shown until IsAvailable is true).
//  - a touch platform builds its ScreenMetrics with touch = true (IRenderHost.Screen): PUi lays itself out for a phone or a tablet from it (see ScreenMetrics, DeviceKind).

namespace PUi.Platform
{
    public interface IPlatform
    {
        /// <summary>"tizen", "desktop", "android"...</summary>
        string Name { get; }
        IRenderHost Render { get; }
        IInputSource Input { get; }
        IWindowControl Window { get; }
        IAudioFocus Audio { get; }
        IUiThread UiThread { get; }
        IPlatformPaths Paths { get; }
        /// <summary>System information for Settings.</summary>
        ISystemInfo SystemInfo { get; }
        /// <summary>What the device can do for the interface (keys the remote has, touch, keyboard).</summary>
        PlatformCapabilities Capabilities { get; }
        /// <summary>Screen captures for the tests, or null if the platform cannot take them.</summary>
        ISnapshotSource Snapshot { get; }
        /// <summary>The system keyboard, or null where there is none (the Samsung TV: PUi's own on-screen keyboard is used). Only used when Capabilities.HasTouch.</summary>
        ITextInput TextInput { get; }
        /// <summary>Closes the app (Back on the root screen).</summary>
        void Exit();
    }
}
