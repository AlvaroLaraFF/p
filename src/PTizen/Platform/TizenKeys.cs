namespace PTizen.Platform
{
    /// <summary>
    /// What the TV shell has to ask the system for: the remote's keys that Samsung's Tizen consumes for itself unless the window registers them with KeyGrabEx.
    /// A pure table (no Tizen API) so it can be tested on the PC; the names are the X11 ones that PUi.Input.KeyNames understands.
    /// </summary>
    internal static class TizenKeys
    {
        public static readonly string[] GrabNames =
        {
            "XF86RaiseChannel", "XF86LowerChannel", "XF86ChannelUp", "XF86ChannelDown", "XF86AudioPlay", "XF86AudioPause", "XF86AudioStop", "XF86AudioRewind",
            "XF86AudioForward", "XF86AudioNext", "XF86AudioPrev", "XF86PlayBack", "XF86Info", "XF86Red", "XF86Green", "XF86Yellow", "XF86Blue",
            // Back too: without registering it, with the player's transparent window the TV receives it at the same time and opens its Home menu.
            // P! handles it entirely (at the root it leaves the app with Exit()).
            "XF86Back"
        };
    }
}
