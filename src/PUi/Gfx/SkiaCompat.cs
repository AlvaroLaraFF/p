namespace PUi.Gfx
{
    /// <summary>
    /// The ONLY place in PUi where conditional compilation is allowed (a test enforces it). PUi builds against SkiaSharp 1.60 (netstandard2.0, the TV's) and SkiaSharp 2.88
    /// (net8.0: desktop, tests, Android). Both accept the same drawing code today; if an API ever differs, the shim goes here and the rest of PUi stays free of #if.
    /// </summary>
    public static class SkiaCompat
    {
#if NETSTANDARD2_0
        /// <summary>SkiaSharp API generation this build of PUi was compiled against.</summary>
        public const string ApiLevel = "1.60";
#else
        /// <summary>SkiaSharp API generation this build of PUi was compiled against.</summary>
        public const string ApiLevel = "2.88";
#endif
    }
}
