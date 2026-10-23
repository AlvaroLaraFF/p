using PUi.Platform;

namespace PAndroid
{
    /// <summary>What the activity locks its screen to (ActivityInfo.screenOrientation, as plain values so the rule is testable on the PC).</summary>
    public enum ScreenLock
    {
        /// <summary>What the system says: the sensor, as far as the user's rotation lock allows.</summary>
        Unspecified,
        /// <summary>Vertical, the normal way up.</summary>
        Portrait,
        /// <summary>Horizontal, whichever side the device is held on.</summary>
        SensorLandscape
    }

    /// <summary>
    /// The orientation rules of the Android shell. A TV or a box is always landscape. A phone is used only in portrait: it is locked there when the app starts, turns to landscape only for the
    /// full screen player (PUi asks with IOrientationControl) and goes back when PUi asks. A tablet follows the sensor, except that full screen asks for landscape too. The system bars are
    /// visible in the normal screens of a phone or tablet (the canvas is the safe area they leave) and hidden (immersive) in full screen and on a TV.
    /// </summary>
    public static class OrientationPolicy
    {
        public static bool IsTouchDevice(AndroidDeviceKind kind) { return kind == AndroidDeviceKind.Phone || kind == AndroidDeviceKind.Tablet; }

        /// <summary>The lock when the app starts.</summary>
        public static ScreenLock Startup(AndroidDeviceKind kind)
        {
            switch (kind)
            {
                case AndroidDeviceKind.Phone: return ScreenLock.Portrait;
                case AndroidDeviceKind.Tablet: return ScreenLock.Unspecified;
                default: return ScreenLock.SensorLandscape;
            }
        }

        /// <summary>The lock for a request of the interface.</summary>
        public static ScreenLock For(AndroidDeviceKind kind, OrientationRequest request)
        {
            if (!IsTouchDevice(kind)) return ScreenLock.SensorLandscape;          // a TV has one orientation
            switch (request)
            {
                case OrientationRequest.Landscape: return ScreenLock.SensorLandscape;
                case OrientationRequest.Portrait: return ScreenLock.Portrait;
                default: return Startup(kind);                                      // Device: back to the shell's own rule
            }
        }

        /// <summary>The system bars are hidden: always on a TV, and on a phone or tablet only in full screen (the landscape request).</summary>
        public static bool WantsImmersive(AndroidDeviceKind kind, OrientationRequest request)
        {
            return !IsTouchDevice(kind) || request == OrientationRequest.Landscape;
        }
    }
}
