namespace AceEngine.Net
{
    /// <summary>
    /// Some platforms drop incoming multicast packets to save battery unless the app asks for them (Android's WifiManager.MulticastLock): without it the SSDP search for the router
    /// (UPnP) gets no answers. The shell implements it; the engine takes it while it needs multicast (UPnP wanted and the engine running) and releases it afterwards. Acquire and Release
    /// are never nested: the engine calls them in pairs. Never needed on the Samsung TV.
    /// </summary>
    public interface IMulticastLock
    {
        void Acquire();
        void Release();
    }
}
