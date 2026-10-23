namespace PUi.Platform
{
    /// <summary>Opaque window (all screens except the player: smoother and it covers the TV source) or transparent (video behind).</summary>
    public interface IWindowControl
    {
        bool Opaque { get; set; }

        /// <summary>
        /// The screen must not dim or go to sleep while true. The interface sets it ONLY while the player is open (a menu may let the TV or the phone sleep) and clears it on leaving the
        /// player. Platforms where the system already keeps the screen on during video (the Samsung TV) ignore it.
        /// </summary>
        bool KeepScreenOn { get; set; }
    }
}
