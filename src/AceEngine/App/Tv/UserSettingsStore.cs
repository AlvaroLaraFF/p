using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Loads and saves UserSettings (what the user changes in Settings) in data/settings.json.
    ///  - Reading bounded to 64 KiB; a missing, unreadable, too large or garbage file gives the default values (never throws).
    ///  - Out-of-range values corrected: UploadKBps to the nearest allowed value of UserSettings.UploadStepsKBps; booleans of another type are ignored.
    ///  - Atomic write (temporary file + replace): a power cut does not leave half a JSON.
    ///  - Compatibility with the old settings.json (uploadEnabled / uploadMaxBytesPerSec / uploadSlots): if there is uploadMaxBytesPerSec and no uploadKBps it is
    ///    used, rounded to the nearest step; uploadEnabled is IGNORED (upload can no longer be turned off); uploadSlots is not a user setting.
    /// The same file is also read by AppConfig (ConfigLoader layers), which ignores the keys it does not know.
    /// </summary>
    public sealed class UserSettingsStore
    {
        public const int MaxFileBytes = 64 * 1024;
        /// <summary>Version of the settings.json format (2: central search on by default).</summary>
        public const int SettingsVersion = 2;
        public const string FileName = "settings.json";

        private readonly string _path;
        private readonly object _gate = new object();

        public UserSettingsStore(string path) { _path = path; }

        /// <summary>Path of data/settings.json inside a data directory.</summary>
        public static UserSettingsStore InDirectory(string dataDir) { return new UserSettingsStore(Path.Combine(dataDir ?? "", FileName)); }

        public string FilePath { get { return _path; } }

        /// <summary>Reads the settings. Always returns a new, valid object.</summary>
        public UserSettings Load()
        {
            string text = null;
            lock (_gate) text = ReadText();
            return text == null ? new UserSettings() : Parse(text);
        }

        private string ReadText()
        {
            if (string.IsNullOrEmpty(_path)) return null;
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists) return null;
                if (fi.Length > MaxFileBytes) { AceLog.Warn("settings", _path + " too large (" + fi.Length + " bytes): using the default values"); return null; }
                // Read with a cap even if the file grows between the check and the read
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[4096];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > MaxFileBytes) return null;
                    }
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                AceLog.Warn("settings", "could not read " + _path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Interprets a settings JSON. Tolerant: whatever is not understood stays at its default value.</summary>
        public static UserSettings Parse(string json)
        {
            var s = new UserSettings();
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxFileBytes) return s;
            object v;
            if (!MiniJson.TryParse(json, out v)) return s;
            var o = MiniJson.AsObject(v);
            if (o == null) return s;
            bool? b;
            if ((b = MiniJson.GetBool(o, "onlyLive")).HasValue) s.OnlyLive = b.Value;
            if ((b = MiniJson.GetBool(o, "upnpEnabled")).HasValue) s.UpnpEnabled = b.Value;
            // "tsServiceEnabled" from an old settings.json is IGNORED on purpose: TSService is removed from the app (see UserSettings.TsServiceEnabled)
            // The central search became on by default in version 2 of the file: the "false" of an earlier file was the old default value
            // (not a user choice) and is ignored. From version 2 on what is stored is respected.
            double version;
            if (TryNumber(o, "version", out version) && version >= SettingsVersion && (b = MiniJson.GetBool(o, "aceSearchEnabled")).HasValue) s.AceSearchEnabled = b.Value;
            double minRes;
            if (TryNumber(o, "minResolution", out minRes)) s.MinResolution = ChannelResolution.NormalizeStep((int)Math.Max(0, Math.Min(minRes, 100000)));
            string lang = MiniJson.GetString(o, "language");
            if (lang != null) s.Language = ChannelLanguages.Normalize(lang);
            double kbps;
            if (TryNumber(o, "uploadKBps", out kbps)) s.UploadKBps = NearestUploadKBps(kbps);
            else
            {
                // Old format: bytes per second (uploadEnabled is ignored on purpose)
                double bytes;
                if (TryNumber(o, "uploadMaxBytesPerSec", out bytes)) s.UploadKBps = NearestUploadKBps(bytes / 1024.0);
            }
            return s;
        }

        private static bool TryNumber(Dictionary<string, object> o, string key, out double value)
        {
            value = 0;
            object v;
            if (!o.TryGetValue(key, out v) || v == null) return false;
            if (v is long) { value = (long)v; return true; }
            if (v is double) { value = (double)v; return !double.IsNaN(value); }
            var s = v as string;
            return s != null && s.Length < 32 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value);
        }

        /// <summary>The nearest value of UserSettings.UploadStepsKBps (in a tie, the smaller); any garbage (NaN, negatives, huge) falls on an extreme.</summary>
        public static int NearestUploadKBps(double kbps)
        {
            var steps = UserSettings.UploadStepsKBps;
            if (double.IsNaN(kbps)) return new UserSettings().UploadKBps;
            if (kbps <= steps[0]) return steps[0];
            if (kbps >= steps[steps.Length - 1]) return steps[steps.Length - 1];       // the extremes before the computation: with huge values all the distances tie because of rounding
            int best = steps[0];
            double bestDist = double.MaxValue;
            foreach (int step in steps)
            {
                double d = Math.Abs(step - kbps);
                if (d < bestDist) { bestDist = d; best = step; }
            }
            return best;
        }

        public static string ToJson(UserSettings s)
        {
            var o = new Dictionary<string, object>
            {
                { "version", (long)SettingsVersion }, { "onlyLive", s.OnlyLive }, { "minResolution", (long)ChannelResolution.NormalizeStep(s.MinResolution) }, { "language", ChannelLanguages.Normalize(s.Language) },
                { "uploadKBps", (long)NearestUploadKBps(s.UploadKBps) },
                { "upnpEnabled", s.UpnpEnabled }, { "aceSearchEnabled", s.AceSearchEnabled }
            };
            return MiniJson.Stringify(o, true);
        }

        /// <summary>Saves the settings (with UploadKBps corrected) atomically. Returns false if it could not (the reason goes to the log); never throws.</summary>
        public bool Save(UserSettings s)
        {
            if (s == null || string.IsNullOrEmpty(_path)) return false;
            lock (_gate)
            {
                string tmp = _path + ".tmp";
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(tmp, ToJson(s), new UTF8Encoding(false));
                    ReplaceFile(tmp, _path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    AceLog.Warn("settings", "could not save " + _path + ": " + ex.Message);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* nothing to do */ }
                    return false;
                }
            }
        }

        private static void ReplaceFile(string tmp, string path)
        {
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            try { File.Replace(tmp, path, null); }
            catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
            {
                // File systems without File.Replace (FAT, some Tizen): delete and move
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
    }
}
