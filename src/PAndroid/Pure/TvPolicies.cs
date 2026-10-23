using System;
using System.Collections.Generic;
using System.Linq;

namespace PAndroid
{
    /// <summary>
    /// When the screen is kept on. The interface asks for it while the player is open (IWindowControl.KeepScreenOn: a menu may let the screen sleep); a TV device must let the screensaver start
    /// when nothing is playing, whatever the player screen says: Amazon's requirements for multimedia apps on Fire TV ("Requirement 6.2: you must release your wake locks if playback is not active
    /// for any reason (the movie ended, the buffering screen is shown, you lost the audio focus)... your application is expected to let the device screensaver kick in"). A paused
    /// channel is the case that matters: left on pause, the TV would otherwise stay lit for ever.
    /// </summary>
    public static class ScreenKeepPolicy
    {
        /// <param name="requestedByInterface">The player screen is open.</param>
        /// <param name="releasesWhenNotPlaying">A TV device (AndroidDeviceProfile.ReleaseScreenWhenNotPlaying); a phone keeps the screen on for as long as the player is open.</param>
        /// <param name="playbackPaused">The viewer paused it, or another app has the audio focus and the player is held.</param>
        public static bool KeepOn(bool requestedByInterface, bool releasesWhenNotPlaying, bool playbackPaused)
        {
            return requestedByInterface && !(releasesWhenNotPlaying && playbackPaused);
        }
    }

    public enum HdmiChange { None, Lost, Restored }

    /// <summary>
    /// The HDMI output of a Fire TV: whether a screen is attached, from the audio output devices the system lists (AudioDeviceCallback: the preferred way on Fire OS 6 and later, says Amazon's
    /// "Handling HDMI Events"). Amazon's rule: "Applications must pause video playback when an HDMI disconnection event is received... when an HDMI sink reconnection event message is received,
    /// playback should remain paused until the user presses the PLAY button". The TV being turned off, or its input switched, is a disconnection: a stick that keeps streaming into the dark
    /// uses the user's data and keeps the device from sleeping.
    /// </summary>
    public sealed class HdmiState
    {
        /// <summary>AudioDeviceInfo.TYPE_HDMI, TYPE_HDMI_ARC and TYPE_HDMI_EARC.</summary>
        public const int TypeHdmi = 9, TypeHdmiArc = 10, TypeHdmiEarc = 29;

        private readonly HashSet<int> _ids = new HashSet<int>();

        public static bool IsHdmiType(int audioDeviceType) { return audioDeviceType == TypeHdmi || audioDeviceType == TypeHdmiArc || audioDeviceType == TypeHdmiEarc; }

        /// <summary>At least one HDMI output is attached.</summary>
        public bool Connected { get { return _ids.Count > 0; } }

        /// <summary>The system reports devices as they are added (and, when the callback is registered, the ones that are already there).</summary>
        public HdmiChange Added(IEnumerable<KeyValuePair<int, int>> idAndType)
        {
            bool before = Connected;
            foreach (var d in idAndType) if (IsHdmiType(d.Value)) _ids.Add(d.Key);
            return !before && Connected ? HdmiChange.Restored : HdmiChange.None;
        }

        public HdmiChange Removed(IEnumerable<KeyValuePair<int, int>> idAndType)
        {
            bool before = Connected;
            foreach (var d in idAndType) if (IsHdmiType(d.Value)) _ids.Remove(d.Key);
            return before && !Connected ? HdmiChange.Lost : HdmiChange.None;
        }
    }

    /// <summary>
    /// What to do about an HDMI disconnection. A display that changes its refresh rate or resolution (the "match original frame rate" setting) can drop and re-raise the link within a second,
    /// and some televisions do it when they wake: pausing then would be a nuisance, so the pause waits a moment and is cancelled by the link coming back.
    /// </summary>
    public static class HdmiPolicy
    {
        /// <summary>How long a disconnection has to last before playback is paused.</summary>
        public const int PauseAfterMs = 1500;

        /// <summary>Only a Fire TV (and a box, which has the same kind of output): a phone has its own screen, and an Android TV set has no HDMI sink to lose.</summary>
        public static bool AppliesTo(AndroidDeviceKind kind) { return kind == AndroidDeviceKind.FireTv || kind == AndroidDeviceKind.Box; }
    }

    public enum CastAvailability
    {
        /// <summary>Google Play services are there: the Cast SDK can be used.</summary>
        Available,
        /// <summary>A Fire TV: Amazon's devices have no Google Play services, and the Cast SDK needs them. Never probed.</summary>
        FireTv,
        /// <summary>The device has no (enabled) Google Play services.</summary>
        NoPlayServices,
        /// <summary>The configuration says no ("cast": "off").</summary>
        Disabled
    }

    /// <summary>
    /// Whether "Enviar a la tele" through the Google Cast SDK may even be started. The SDK is a client of Google Play services: where they are missing (Fire OS, an Android device without
    /// Google's apps) it fails, and classes that need them can throw when first used. So everything of the Cast SDK stays behind this decision: the shell asks <see cref="Decide"/> once at
    /// startup, and only when it says Available does it create the Cast service and touch the SDK's classes; otherwise the interface keeps NoCast and the app starts exactly as it does without Cast.
    /// </summary>
    public static class CastPolicy
    {
        public static CastAvailability Decide(AndroidDeviceKind kind, bool playServicesUsable, CastMode mode)
        {
            if (mode == CastMode.Off) return CastAvailability.Disabled;
            if (kind == AndroidDeviceKind.FireTv) return CastAvailability.FireTv;                 // not even asked: Fire OS has none
            return playServicesUsable ? CastAvailability.Available : CastAvailability.NoPlayServices;
        }

        public static string Describe(CastAvailability a)
        {
            switch (a)
            {
                case CastAvailability.Available: return "cast: available (Google Play services found)";
                case CastAvailability.FireTv: return "cast: off, a Fire TV has no Google Play services";
                case CastAvailability.NoPlayServices: return "cast: off, this device has no usable Google Play services";
                default: return "cast: off by the configuration";
            }
        }
    }
}

namespace PAndroid
{
    /// <summary>
    /// Which devices offer "Enviar a la tele" at all. Only a phone or a tablet (a touch screen): a TV, a Fire TV and a box are the RECEIVERS (the user watches on them, and P! on them is itself something
    /// to send to), so they never show the button and never search for screens. Fire TV in particular: it has no Google Play services (no Cast), and as a sender it makes no sense. A Fire tablet
    /// (touch, no Google services) sends to DLNA televisions only; a phone with Play services also to Chromecasts.
    /// </summary>
    public static class SendPolicy
    {
        public static bool Offers(AndroidDeviceKind kind) { return kind == AndroidDeviceKind.Phone || kind == AndroidDeviceKind.Tablet; }

        /// <summary>One line for the log: what this device sends to.</summary>
        public static string Describe(AndroidDeviceKind kind, CastAvailability cast)
        {
            if (!Offers(kind)) return "send: off, a " + kind + " is a receiver";
            return cast == CastAvailability.Available ? "send: DLNA televisions and Google Cast" : "send: DLNA televisions only (" + CastPolicy.Describe(cast) + ")";
        }
    }
}
