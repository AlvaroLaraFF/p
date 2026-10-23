namespace PUi.Platform
{
    public interface IPlatformPaths
    {
        /// <summary>App data directory (writable): settings, recents, list cache, trust.</summary>
        string DataDir { get; }
        /// <summary>Directory with the Manrope-400/600/700/800.ttf fonts.</summary>
        string FontsDir { get; }
    }
}
