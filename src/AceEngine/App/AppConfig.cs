using System;
using System.Collections.Generic;
using AceEngine.Util;

namespace AceEngine.App
{
    /// <summary>A configured channel list (mylinkspaste code or URL of an M3U list). The name is only for the log: the UI does not tell lists apart.</summary>
    public sealed class ListSource
    {
        public string Code = "";
        public string Name = "";
        public ListSource() { }
        public ListSource(string code, string name = "") { Code = code; Name = name; }
    }

    /// <summary>A configurable search provider: URL template with {query} that returns an M3U list or JSON.</summary>
    public sealed class SearchProviderConfig
    {
        public string Name = "";
        public string Url = "";
    }

    /// <summary>
    /// App configuration. Read from a small JSON (64 KiB at most); anything not recognized is ignored.
    /// Defaults = normal user behavior; the rest (autoplay, key script, diagnostics) is for tests.
    /// What the user changes from Settings (only live, upload, UPnP, TSService, central search) lives in AceEngine.App.Tv.UserSettings.
    /// </summary>
    public sealed class AppConfig
    {
        public const int MaxConfigBytes = 64 * 1024;
        public const int MaxLists = 12;
        public const int MaxProviders = 8;

        // ---------- UDP log (dlog is blocked on the TV) ----------
        public bool LogEnabled = false;
        public string LogHost = "";
        public int LogPort = 9999;
        /// <summary>Engine log level: "debug", "info" (default), "warn".</summary>
        public string LogLevel = "info";

        // ---------- Channel lists ----------
        /// <summary>Lists merged into one at startup. Managed only through configuration (key "lists"; "favorites" is accepted as an old alias).</summary>
        public readonly List<ListSource> Lists = new List<ListSource>();
        /// <summary>Additional search providers ("searchProviders").</summary>
        public readonly List<SearchProviderConfig> SearchProviders = new List<SearchProviderConfig>();

        // ---------- Standalone test mode ----------
        /// <summary>If not empty, the app starts playing this link directly (content_id, acestream://, magnet...).</summary>
        public string AutoPlay = "";
        public string AutoPlayName = "";
        /// <summary>Seconds of playback in test mode before going back to the UI; 0 = no limit.</summary>
        public int AutoPlaySeconds = 0;
        /// <summary>After the test mode, exit the app (useful for unattended tests).</summary>
        public bool AutoPlayExit = false;
        /// <summary>The startup self-diagnosis includes network tests (DNS TXT over UDP and DoH).</summary>
        public bool DiagnosticsNetwork = true;
        /// <summary>Interval of the CPU and memory monitor, in seconds (0 = off).</summary>
        public int StatsIntervalSeconds = 5;
        /// <summary>UI test mode: uses sample data instead of the engine (to measure fps and check the look on the TV).</summary>
        public bool UiDemo = false;
        /// <summary>Synchronization with the GPU after painting each frame: "finish" (default: glFinish, Evas never shows a half-drawn texture), "flush" (glFlush) or "none".</summary>
        public string GlSync = "finish";
        /// <summary>Sprite creation budget per frame in ms (0 = no limit). On the TV a sprite costs ~12-60 ms.</summary>
        public int SpriteBudgetMs = 8;
        /// <summary>Draws once on the GPU, off screen, everything the UI uses right after startup (avoids the hitches of the first time).</summary>
        public bool GpuWarmup = false;
        /// <summary>
        /// TV decoder warmer (black clip in a loop from when the app opens, so that the TV source does not play sound). true by default: NEEDED. false only to measure on the TV how much
        /// of the process CPU is the warmer's (compare the [stats] lines with and without it, with the UI at rest): without it, terrestrial TV keeps playing in the background until a channel starts.
        /// </summary>
        public bool Warmer = true;
        /// <summary>TSService fallback path: off by default (the engine does not use it nor query those servers). For tests only; the code is kept.</summary>
        public bool TsService = false;
        /// <summary>Test key script (see PUi.Input.KeyScript): runs at startup, through the same path as the remote.</summary>
        public string KeyScript = "";

        // ---------- The shell's own options ----------
        public const int MaxPlatformOptions = 32;
        /// <summary>
        /// The "platform" object of the configuration, as it came (scalar values only): options that only a shell understands, such as the Android shell's player choice. The engine and
        /// the interface never read them. Layers merge key by key, like the rest of the configuration.
        /// </summary>
        public readonly Dictionary<string, object> Platform = new Dictionary<string, object>();

        // ---------- Network and memory ----------
        /// <summary>Upload slots. Upload is ALWAYS on (without it peers serve worse); the maximum is chosen by the user in Settings.</summary>
        public int UploadSlots = 4;
        /// <summary>Upload maximum in bytes/s if the user has not yet chosen one in Settings.</summary>
        public long UploadMaxBytesPerSec = 512 * 1024;
        public int VodBufferMiB = 48;
        /// <summary>Memory for live (pieces ahead of playback and, in pause/rewind mode, what was already served).</summary>
        public int LiveBufferMiB = 24;
        /// <summary>Extra memory for what was already served of the live stream (pause, rewind and +/-10 s). Its window in seconds depends on the channel bitrate.</summary>
        public int LiveTimeshiftMiB = 64;
        /// <summary>"chunked" or "close".</summary>
        public string LiveTransfer = "chunked";
        public bool EnableListener = true;
        /// <summary>Verification of the RSA signature of each piece of a live stream: "strict" (default in the app), "tolerant" or "off".</summary>
        public string Signature = "strict";
        /// <summary>A known channel whose broadcaster key changes without a clear quorum is rejected (true) or only warned about (false).</summary>
        public bool TrustEnforce = true;

        public static AppConfig Parse(string json)
        {
            var c = new AppConfig();
            if (string.IsNullOrWhiteSpace(json)) return c;
            if (json.Length > MaxConfigBytes) return c;
            object v;
            if (!MiniJson.TryParse(json, out v)) return c;
            var o = MiniJson.AsObject(v);
            if (o == null) return c;
            c.Apply(o);
            return c;
        }

        /// <summary>Applies a JSON on top of the current values (ignores what it does not understand).</summary>
        public void ApplyJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxConfigBytes) return;
            object v;
            if (!MiniJson.TryParse(json, out v)) return;
            var o = MiniJson.AsObject(v);
            if (o != null) Apply(o);
        }

        /// <summary>Applies on top of the current values the fields present in the JSON.</summary>
        public void Apply(Dictionary<string, object> o)
        {
            bool? b;
            long? l;
            string s;
            var log = MiniJson.GetObject(o, "log");
            if (log != null)
            {
                if ((b = MiniJson.GetBool(log, "enabled")).HasValue) LogEnabled = b.Value;
                if ((s = MiniJson.GetString(log, "host")) != null) LogHost = Clip(s, 255);
                if ((l = MiniJson.GetLong(log, "port")).HasValue && l.Value > 0 && l.Value < 65536) LogPort = (int)l.Value;
                if ((s = MiniJson.GetString(log, "level")) != null) LogLevel = Clip(s, 16).ToLowerInvariant();
            }
            if ((s = MiniJson.GetString(o, "autoPlay")) != null) AutoPlay = Clip(s, 2048).Trim();
            if ((s = MiniJson.GetString(o, "autoPlayName")) != null) AutoPlayName = Clip(s, 200);
            if ((l = MiniJson.GetLong(o, "autoPlaySeconds")).HasValue) AutoPlaySeconds = (int)Math.Max(0, Math.Min(l.Value, 86400));
            if ((b = MiniJson.GetBool(o, "autoPlayExit")).HasValue) AutoPlayExit = b.Value;
            if ((b = MiniJson.GetBool(o, "diagnosticsNetwork")).HasValue) DiagnosticsNetwork = b.Value;
            if ((l = MiniJson.GetLong(o, "statsIntervalSeconds")).HasValue) StatsIntervalSeconds = (int)Math.Max(0, Math.Min(l.Value, 600));
            if ((s = MiniJson.GetString(o, "keyScript")) != null) KeyScript = Clip(s, 16384);
            if ((s = MiniJson.GetString(o, "glSync")) != null) { s = Clip(s, 8).ToLowerInvariant(); if (s == "finish" || s == "flush" || s == "none") GlSync = s; }
            if ((b = MiniJson.GetBool(o, "gpuWarmup")).HasValue) GpuWarmup = b.Value;
            if ((b = MiniJson.GetBool(o, "warmer")).HasValue) Warmer = b.Value;
            if ((b = MiniJson.GetBool(o, "tsService")).HasValue) TsService = b.Value;
            if ((l = MiniJson.GetLong(o, "spriteBudgetMs")).HasValue) SpriteBudgetMs = (int)Math.Max(0, Math.Min(l.Value, 200));
            if ((b = MiniJson.GetBool(o, "uiDemo")).HasValue) UiDemo = b.Value;
            var lists = MiniJson.GetArray(o, "lists") ?? MiniJson.GetArray(o, "favorites");
            if (lists != null)
            {
                Lists.Clear();
                foreach (var f in lists)
                {
                    if (Lists.Count >= MaxLists) break;
                    var fd = MiniJson.AsObject(f);
                    string code = fd != null ? MiniJson.GetString(fd, "code") : f as string;
                    if (string.IsNullOrWhiteSpace(code) || code.Length > 2048) continue;
                    string name = fd != null ? MiniJson.GetString(fd, "name") : null;
                    Lists.Add(new ListSource(code.Trim(), Clip(name ?? "", 100).Trim()));
                }
            }
            var provs = MiniJson.GetArray(o, "searchProviders");
            if (provs != null)
            {
                SearchProviders.Clear();
                foreach (var p in provs)
                {
                    if (SearchProviders.Count >= MaxProviders) break;
                    var pd = MiniJson.AsObject(p);
                    if (pd == null) continue;
                    string url = MiniJson.GetString(pd, "url");
                    if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) continue;
                    SearchProviders.Add(new SearchProviderConfig { Name = Clip(MiniJson.GetString(pd, "name") ?? "", 100), Url = url.Trim() });
                }
            }
            var plat = MiniJson.GetObject(o, "platform");
            if (plat != null)
                foreach (var kv in plat)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.Length > 64) continue;
                    if (!(kv.Value is string || kv.Value is long || kv.Value is double || kv.Value is bool)) continue;
                    if (!Platform.ContainsKey(kv.Key) && Platform.Count >= MaxPlatformOptions) continue;
                    var str = kv.Value as string;
                    Platform[kv.Key] = str != null ? Clip(str, 256) : kv.Value;
                }
            if ((l = MiniJson.GetLong(o, "uploadSlots")).HasValue) UploadSlots = (int)Math.Max(1, Math.Min(l.Value, 16));
            if ((l = MiniJson.GetLong(o, "uploadMaxBytesPerSec")).HasValue) UploadMaxBytesPerSec = Math.Max(16 * 1024, Math.Min(l.Value, 1L << 30));
            if ((l = MiniJson.GetLong(o, "vodBufferMiB")).HasValue) VodBufferMiB = (int)Math.Max(8, Math.Min(l.Value, 256));
            if ((l = MiniJson.GetLong(o, "liveBufferMiB")).HasValue) LiveBufferMiB = (int)Math.Max(8, Math.Min(l.Value, 128));
            if ((l = MiniJson.GetLong(o, "liveTimeshiftMiB")).HasValue) LiveTimeshiftMiB = (int)Math.Max(0, Math.Min(l.Value, 256));
            if ((s = MiniJson.GetString(o, "liveTransfer")) != null) LiveTransfer = Clip(s, 16).ToLowerInvariant();
            if ((b = MiniJson.GetBool(o, "enableListener")).HasValue) EnableListener = b.Value;
            if ((s = MiniJson.GetString(o, "signature")) != null) { s = Clip(s, 16).ToLowerInvariant(); if (s == "strict" || s == "tolerant" || s == "off") Signature = s; }
            if ((b = MiniJson.GetBool(o, "trustEnforce")).HasValue) TrustEnforce = b.Value;
        }

        private static string Clip(string s, int max) { return s.Length <= max ? s : s.Substring(0, max); }

        public string ToJson()
        {
            var lists = new List<object>();
            foreach (var l in Lists) lists.Add(new Dictionary<string, object> { { "code", l.Code }, { "name", l.Name } });
            var provs = new List<object>();
            foreach (var p in SearchProviders) provs.Add(new Dictionary<string, object> { { "name", p.Name }, { "url", p.Url } });
            var o = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "enabled", LogEnabled }, { "host", LogHost }, { "port", (long)LogPort }, { "level", LogLevel } } },
                { "lists", lists }, { "searchProviders", provs },
                { "autoPlay", AutoPlay }, { "autoPlayName", AutoPlayName }, { "autoPlaySeconds", (long)AutoPlaySeconds }, { "autoPlayExit", AutoPlayExit },
                { "diagnosticsNetwork", DiagnosticsNetwork }, { "statsIntervalSeconds", (long)StatsIntervalSeconds }, { "keyScript", KeyScript }, { "uiDemo", UiDemo }, { "glSync", GlSync }, { "spriteBudgetMs", (long)SpriteBudgetMs }, { "gpuWarmup", GpuWarmup }, { "warmer", Warmer }, { "tsService", TsService },
                { "uploadSlots", (long)UploadSlots }, { "uploadMaxBytesPerSec", UploadMaxBytesPerSec },
                { "vodBufferMiB", (long)VodBufferMiB }, { "liveBufferMiB", (long)LiveBufferMiB }, { "liveTimeshiftMiB", (long)LiveTimeshiftMiB }, { "liveTransfer", LiveTransfer },
                { "enableListener", EnableListener }, { "signature", Signature }, { "trustEnforce", TrustEnforce }
            };
            if (Platform.Count > 0) o["platform"] = new Dictionary<string, object>(Platform);
            return MiniJson.Stringify(o, true);
        }
    }
}
