using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AceEngine.Util;

namespace AceEngine.App
{
    /// <summary>The layer is larger than AppConfig.MaxConfigBytes.</summary>
    public sealed class ConfigTooLargeException : Exception
    {
        public ConfigTooLargeException(string name) : base(name + " is larger than " + AppConfig.MaxConfigBytes + " bytes") { }
    }

    /// <summary>
    /// One layer of the configuration: a name for the log and a source of JSON TEXT, not a path: on Android the packaged layer comes from Assets/ (no file), on the
    /// TV from files, and tests use plain strings.
    /// </summary>
    public sealed class ConfigLayer
    {
        public readonly string Name;
        private readonly Func<string> _read;

        /// <param name="read">Returns the JSON text, null if the layer does not exist (no file, no asset), and throws if it cannot be read (ConfigTooLargeException if it is too large).</param>
        public ConfigLayer(string name, Func<string> read) { Name = name; _read = read; }

        /// <summary>The JSON text, or null if the layer does not exist.</summary>
        public string Read() { return _read(); }

        /// <summary>A layer in a file (UTF-8). The size is checked before reading it.</summary>
        public static ConfigLayer FromFile(string path)
        {
            return new ConfigLayer(path, () =>
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length > AppConfig.MaxConfigBytes) throw new ConfigTooLargeException(path);
                return File.ReadAllText(path, Encoding.UTF8);
            });
        }

        /// <summary>A layer with the text in hand (tests, assets already read, defaults).</summary>
        public static ConfigLayer FromText(string name, string json) { return new ConfigLayer(name, () => json); }
    }

    /// <summary>Reads the configuration in layers: each layer overrides the fields of the previous one.</summary>
    public static class AppConfigLoader
    {
        public const string ConfigFile = "config.json";
        /// <summary>The settings the user changes in the interface.</summary>
        public const string SettingsFile = "settings.json";

        /// <summary>
        /// The usual layers, in order: packaged resource (resourceDir/config.json), app data (dataDir/config.json) and the settings the user changes in the interface
        /// (dataDir/settings.json). A shell adds its own after these (on the TV, the sdb tools file for tests without reinstalling).
        /// </summary>
        public static List<ConfigLayer> StandardFileLayers(string dataDir, string resourceDir)
        {
            return new List<ConfigLayer>
            {
                ConfigLayer.FromFile(Path.Combine(resourceDir ?? "", ConfigFile)),
                ConfigLayer.FromFile(Path.Combine(dataDir ?? "", ConfigFile)),
                ConfigLayer.FromFile(Path.Combine(dataDir ?? "", SettingsFile)),
            };
        }

        /// <summary>
        /// Applies the layers in order on top of a new AppConfig (after <paramref name="defaults"/>, the platform's default values, if given). A missing layer is skipped;
        /// one that is too large, unreadable or not valid JSON is skipped too and the others still apply. Everything is reported to <paramref name="log"/> (level, tag, text).
        /// </summary>
        public static AppConfig Load(IEnumerable<ConfigLayer> layers, Action<LogLevel, string, string> log = null, Action<AppConfig> defaults = null)
        {
            var cfg = new AppConfig();
            if (defaults != null) defaults(cfg);
            if (layers == null) return cfg;
            foreach (var layer in layers)
            {
                try
                {
                    string text = layer.Read();
                    if (text == null) { Say(log, LogLevel.Info, "no " + layer.Name); continue; }
                    if (Encoding.UTF8.GetByteCount(text) > AppConfig.MaxConfigBytes) { Say(log, LogLevel.Warn, layer.Name + " too large"); continue; }
                    cfg.ApplyJson(text);
                    Say(log, LogLevel.Info, "read " + layer.Name);
                }
                catch (ConfigTooLargeException) { Say(log, LogLevel.Warn, layer.Name + " too large"); }
                catch (Exception e) { Say(log, LogLevel.Warn, layer.Name + ": " + e.Message); }
            }
            return cfg;
        }

        private static void Say(Action<LogLevel, string, string> log, LogLevel level, string msg)
        {
            if (log != null) log(level, "config", msg);
        }
    }
}
