using System;
using System.IO;
using AceEngine.App.Tv;
using AceEngine.P2P;
using AceEngine.Streaming;

namespace AceEngine.App
{
    /// <summary>Converts the app configuration into engine parameters (memory budget of the 32-bit TV).</summary>
    public static class EngineOptionsFactory
    {
        public static EngineOptions Build(AppConfig cfg, string dataDir)
        {
            var o = new EngineOptions();
            o.MaxBufferBytes = cfg.VodBufferMiB * 1024L * 1024;
            o.Live.MaxBufferBytes = cfg.LiveBufferMiB * 1024L * 1024;
            o.Live.TimeshiftBytes = Math.Max(0, cfg.LiveTimeshiftMiB) * 1024L * 1024;          // live with memory: pause, rewind and LIVE (0 = no memory)
            o.EnableListener = cfg.EnableListener;
            o.Http.LiveTransfer = cfg.LiveTransfer == "close" ? LiveTransfer.CloseDelimited : LiveTransfer.Chunked;
            // Upload is always on (without it peers serve worse); only the maximum is adjusted.
            o.UploadSlots = Math.Max(1, cfg.UploadSlots);
            o.Live.UploadSlots = Math.Max(1, cfg.UploadSlots);
            o.UploadMaxBytesPerSec = cfg.UploadMaxBytesPerSec;
            o.AllowLocalFileLinks = false;
            o.Live.Signature = cfg.Signature == "off" ? AceEngine.Live.SignatureMode.Off : cfg.Signature == "tolerant" ? AceEngine.Live.SignatureMode.Tolerant : AceEngine.Live.SignatureMode.Strict;
            // Channel checking on the TV (32-bit ARM, ~500 MiB free, 4096 descriptors, home network): all checks share ONE budget of 52 outgoing TCP connections (measured peak ~54, below the ~60 of the home network)
            // (same as on the PC), so the number of channels at once comes out on its own: 7 live probes (6 connections + 1 announce) or 7 resolutions
            // (8 connections), or a mix. A channel gets 15 s in total (resolve + probe). The cap on simultaneous resolutions and DHT queries (128, 8 per resolution) leaves room for playback.
            o.Probe.Concurrency = 12;
            o.Probe.TcpBudget = 52;
            o.DhtMaxInFlight = 128;
            o.Probe.LiveVerifyConcurrency = 10;
            o.Resolver.TrustEnforce = cfg.TrustEnforce;
            o.Resolver.StrictTransport = true;
            if (!string.IsNullOrEmpty(dataDir))
            {
                try { Directory.CreateDirectory(dataDir); o.Resolver.CachePath = Path.Combine(dataDir, "resolver-cache.json"); o.Resolver.HintsPath = Path.Combine(dataDir, "peer-hints.json"); o.DhtNodesPath = Path.Combine(dataDir, "dht-nodes.json"); o.Resolver.TrustPath = Path.Combine(dataDir, "trust.json"); }
                catch (Exception) { /* no disk cache */ }
            }
            return o;
        }

        /// <summary>Limits of the upload selectable in Settings (UserSettings.UploadStepsKBps): from 128 KB/s to 2 MB/s.</summary>
        public const long MinUploadBytesPerSec = 128L * 1024, MaxUploadBytesPerSec = 2048L * 1024;

        /// <summary>
        /// Same as Build(cfg, dataDir), and also applies what the user chooses in Settings: upload maximum (UploadKBps, clamped to 128 KB/s - 2 MB/s) and
        /// TSService. Upload is ALWAYS on: at least one slot, and no user setting turns it off (the CLI `--no-upload` and the test hooks
        /// with UploadSlots = 0 are tools, not options).
        /// </summary>
        public static EngineOptions Build(AppConfig cfg, UserSettings settings, string dataDir)
        {
            var o = Build(cfg, dataDir);
            int slots = Math.Max(1, cfg.UploadSlots);
            o.UploadSlots = slots;
            o.Live.UploadSlots = slots;
            ApplyUserSettings(o, settings);
            return o;
        }

        /// <summary>Upload maximum in bytes/s for a Settings value in KB/s: clamped to [128 KB/s, 2 MB/s].</summary>
        public static long UploadBytesPerSec(int uploadKBps)
        {
            return Math.Max(MinUploadBytesPerSec, Math.Min(MaxUploadBytesPerSec, uploadKBps * 1024L));
        }

        /// <summary>
        /// Dumps the user settings into the engine options. Serves both when building and with the engine running (AceEngineHost.ApplyUserSettings):
        /// the upload limit and TSService are read on every request, so they apply from the next one without restarting. It does not touch the upload slots.
        /// </summary>
        public static void ApplyUserSettings(EngineOptions o, UserSettings settings)
        {
            if (o == null) throw new ArgumentNullException("o");
            if (settings == null) settings = new UserSettings();
            o.SetUploadMaxBytesPerSec(UploadBytesPerSec(settings.UploadKBps));
            o.Resolver.EnableTsService = settings.TsServiceEnabled;
        }
    }
}
