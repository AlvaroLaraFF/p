namespace PUi.Platform
{
    /// <summary>
    /// What the device can do for the interface. The Samsung remote has everything on the TV side (channel, digit and colour keys, no touch); a Fire TV remote has no channel
    /// or digit keys; a phone has touch and maybe no remote at all. The interface (and later the shells' device profiles) decide what to show or how to navigate from this.
    /// </summary>
    public sealed class PlatformCapabilities
    {
        /// <summary>The remote has Channel +/- keys (UiKey.ChannelUp / ChannelDown).</summary>
        public bool HasChannelKeys { get; private set; }
        /// <summary>The remote has digit keys (typed characters 0-9).</summary>
        public bool HasDigitKeys { get; private set; }
        /// <summary>The remote has the red, green, yellow and blue keys.</summary>
        public bool HasColorKeys { get; private set; }
        /// <summary>A remote control (D-pad and OK) drives the interface: focus navigation is the primary input.</summary>
        public bool HasRemote { get; private set; }
        /// <summary>A touch screen drives the interface (IInputSource.Pointer is raised and the system Back gesture arrives as UiKey.Back).</summary>
        public bool HasTouch { get; private set; }
        /// <summary>A physical keyboard is attached (typing does not need the on-screen keyboard).</summary>
        public bool HasKeyboard { get; private set; }

        public PlatformCapabilities(bool hasChannelKeys = true, bool hasDigitKeys = true, bool hasColorKeys = true, bool hasRemote = true, bool hasTouch = false, bool hasKeyboard = false)
        {
            HasChannelKeys = hasChannelKeys; HasDigitKeys = hasDigitKeys; HasColorKeys = hasColorKeys; HasRemote = hasRemote; HasTouch = hasTouch; HasKeyboard = hasKeyboard;
        }

        /// <summary>A TV with a full remote: channel, digit and colour keys; no touch, no keyboard. The Samsung TV.</summary>
        public static PlatformCapabilities FullRemote { get { return new PlatformCapabilities(); } }

        /// <summary>A phone or tablet driven by touch alone: no remote keys of any kind (the system Back gesture still arrives as UiKey.Back), no physical keyboard. A shell that detects a
        /// keyboard or a game pad builds its own with hasKeyboard / hasRemote.</summary>
        public static PlatformCapabilities TouchOnly { get { return new PlatformCapabilities(hasChannelKeys: false, hasDigitKeys: false, hasColorKeys: false, hasRemote: false, hasTouch: true, hasKeyboard: false); } }
    }
}
