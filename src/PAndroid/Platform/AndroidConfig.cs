using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Content;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// The Android layers of the configuration, from the weakest to the strongest:
    ///   1. Assets/config.json                              packaged with the apk;
    ///   2. FilesDir/config.json                            the app's data;
    ///   3. FilesDir/settings.json                          what the user changes in Settings;
    ///   4. GetExternalFilesDir(null)/p-config.json         for tests: `adb push p-config.json /sdcard/Android/data/org.p.app/files/` and restart the app.
    /// The platform's defaults (AppConfigDefaults.Android and the device profile's budgets) come before all of them.
    /// </summary>
    internal static class AndroidConfig
    {
        public const string DevOverrideName = "p-config.json";

        public static List<ConfigLayer> Layers(Context context)
        {
            string data = context.FilesDir.AbsolutePath;
            var layers = new List<ConfigLayer>
            {
                new ConfigLayer("assets/" + AppConfigLoader.ConfigFile, () => ReadAsset(context, AppConfigLoader.ConfigFile)),
                ConfigLayer.FromFile(Path.Combine(data, AppConfigLoader.ConfigFile)),
                ConfigLayer.FromFile(Path.Combine(data, AppConfigLoader.SettingsFile)),
            };
            layers.Add(new ConfigLayer("external " + DevOverrideName, () =>
            {
                var dir = context.GetExternalFilesDir(null);
                if (dir == null) return null;
                return ConfigLayer.FromFile(Path.Combine(dir.AbsolutePath, DevOverrideName)).Read();
            }));
            return layers;
        }

        /// <summary>The path where the test configuration goes, for the log and for deploy-android.sh's message; null if the external storage is not available.</summary>
        public static string DevOverridePath(Context context)
        {
            var dir = context.GetExternalFilesDir(null);
            return dir == null ? null : Path.Combine(dir.AbsolutePath, DevOverrideName);
        }

        private static string ReadAsset(Context context, string name)
        {
            try
            {
                using (var s = context.Assets.Open(name))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    if (ms.Length > AppConfig.MaxConfigBytes) throw new ConfigTooLargeException("assets/" + name);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Java.IO.FileNotFoundException) { return null; }
        }
    }
}
