using System;
using AceEngine.App;

namespace PTizen.Platform
{
    /// <summary>
    /// The TV's configuration layers: the common ones (packaged res/config.json, data/config.json, data/settings.json; see AppConfigLoader) and, last, the sdb tools directory,
    /// to change the configuration for tests without reinstalling (sdb push to /home/owner/share/tmp/sdk_tools/p-config.json).
    /// </summary>
    internal static class TizenConfig
    {
        public const string DevOverridePath = "/home/owner/share/tmp/sdk_tools/p-config.json";

        public static AppConfig Load(string dataDir, string resourceDir, Action<AppConfig> defaults = null)
        {
            var layers = AppConfigLoader.StandardFileLayers(dataDir, resourceDir);
            layers.Add(ConfigLayer.FromFile(DevOverridePath));
            return AppConfigLoader.Load(layers, UdpLog.Engine, defaults);
        }
    }
}
