using System;

namespace PUi.Screens
{
    /// <summary>App data that the interface shows in Settings.</summary>
    public static class AppInfo
    {
        /// <summary>
        /// "major.minor.patch" of the app, read from the PUi assembly version: the only place where it is written is AppVersion in Directory.Build.props
        /// (tizen-manifest.xml has to repeat it by hand: PTizen's build and PUi.Tests both check that they match).
        /// </summary>
        public static string Version { get { return _version; } }

        private static readonly string _version = FromAssembly();

        private static string FromAssembly()
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
        }
    }
}
