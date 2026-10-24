using System;
using System.Text;
using AceEngine.Cast;

namespace PAndroid
{
    /// <summary>The kinds of Cast device P! tells apart (by the model name the Cast SDK reports).</summary>
    public enum CastModelClass
    {
        /// <summary>Chromecast 1st, 2nd gen., HD and anything not recognised: H.264 High up to level 4.1 (1080p30). The most restrictive profile: the choice when in doubt.</summary>
        Chromecast2,
        /// <summary>Chromecast 3rd gen. and Ultra: H.264 High up to level 4.2 (1080p60).</summary>
        Chromecast3,
        /// <summary>Chromecast with Google TV, Google TV Streamer, Nvidia Shield: H.264 up to level 5.1 and 4K.</summary>
        GoogleTv,
        /// <summary>A speaker (Chromecast Audio, Google Home, Nest Mini / Audio): it plays no video, so it is not offered.</summary>
        AudioOnly
    }

    /// <summary>
    /// What a Chromecast can decode, deduced from the model name its mDNS announcement carries (CastDevice.getModelName). The names do not say the generation of a plain "Chromecast" (all of the
    /// dongles that are not Ultra or Google TV announce just that), so the user's 2015 one and every doubtful model get the most restrictive profile: a channel the device cannot play is
    /// refused with its reason instead of being sent to a black screen. A television with Cast built in announces its own model ("BRAVIA 4K GB", "Philips 55PUS8506"): doubtful, so the same.
    /// </summary>
    public static class CastModels
    {
        public static CastModelClass Classify(string modelName)
        {
            string m = (modelName ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0) return CastModelClass.Chromecast2;
            if (IsAudioOnly(m)) return CastModelClass.AudioOnly;
            if (m.Contains("google tv") || m.Contains("shield")) return CastModelClass.GoogleTv;
            if (m.Contains("ultra")) return CastModelClass.Chromecast3;
            return CastModelClass.Chromecast2;
        }

        private static bool IsAudioOnly(string m)
        {
            return m.Contains("chromecast audio") || m.Contains("google home") || m.Contains("home mini") || m.Contains("home max") || m.Contains("nest mini") || m.Contains("nest audio") || m.Contains("nest wifi");
        }

        /// <summary>The profile the engine checks channels against for this model; null for a device that plays no video.</summary>
        public static CastProfile ProfileFor(string modelName)
        {
            switch (Classify(modelName))
            {
                case CastModelClass.AudioOnly: return null;
                case CastModelClass.GoogleTv: return CastProfile.GoogleTv;
                case CastModelClass.Chromecast3: return CastProfile.Chromecast3;
                default: return CastProfile.Chromecast2;
            }
        }

        /// <summary>
        /// Whether a route found by the Cast SDK is a screen: the SDK says whether the device has video output (CastDevice.hasCapability(CAPABILITY_VIDEO_OUT)); when it does not say, the model decides.
        /// </summary>
        public static bool IsScreen(bool videoOutKnown, bool hasVideoOut, string modelName)
        {
            return videoOutKnown ? hasVideoOut : Classify(modelName) != CastModelClass.AudioOnly;
        }
    }

    /// <summary>The texts of the notification of a sending in progress and its identifiers. Pure, so that what the user reads is tested.</summary>
    public static class CastNotificationText
    {
        public const string ChannelId = "p-cast-sending";
        public const int NotificationId = 4201;
        public const string ActionStop = "org.p.app.cast.STOP";
        public const string ExtraDevice = "device", ExtraChannel = "channel", ExtraPaused = "paused";
        public const int MaxDevice = 40, MaxChannel = 70;

        /// <summary>The name of the notification channel in the system's settings.</summary>
        public const string ChannelName = "Envío a otra pantalla";
        public const string ChannelDescription = "Aviso mientras un canal se envía a una tele o a un Chromecast";
        public const string StopLabel = "Dejar de enviar";

        /// <summary>"Enviando a Salón".</summary>
        public static string Title(string device) { return "Enviando a " + Clean(device, MaxDevice, "la tele"); }

        /// <summary>The channel's name, with "En pausa · " before it while the device is paused.</summary>
        public static string Text(string channel, bool paused)
        {
            string c = Clean(channel, MaxChannel, "");
            return (paused ? (c.Length > 0 ? "En pausa · " : "En pausa") : "") + c;
        }

        /// <summary>Whitespace collapsed, control characters dropped, cut at <paramref name="max"/> characters with an ellipsis; <paramref name="fallback"/> if nothing is left.</summary>
        public static string Clean(string s, int max, string fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            string r = sb.ToString();
            if (r.Length == 0) return fallback;
            if (r.Length > max) r = r.Substring(0, Math.Max(1, max - 1)).TrimEnd() + "…";
            return r;
        }
    }

    public enum NotificationAsk { No, Ask }

    /// <summary>
    /// When to ask for the notification permission (Android 13 and later). Not before: the permission does not exist. Not if it is granted. Not after two refusals (the system stops showing its
    /// dialog by itself after two, and asking again would be nagging): the sending works without it, the notification just does not show in the shade (the foreground service still keeps the app alive).
    /// It is asked for at the moment it matters: the first time a channel is sent.
    /// </summary>
    public static class NotificationPermissionPolicy
    {
        public const int FirstSdkWithPermission = 33, MaxAsks = 2;

        public static NotificationAsk Decide(int sdkInt, bool granted, int timesAsked)
        {
            if (sdkInt < FirstSdkWithPermission) return NotificationAsk.No;
            if (granted) return NotificationAsk.No;
            return timesAsked >= MaxAsks ? NotificationAsk.No : NotificationAsk.Ask;
        }
    }

    /// <summary>What the foreground service of a sending needs from the system, by API level.</summary>
    public static class ForegroundServicePolicy
    {
        /// <summary>ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK.</summary>
        public const int TypeMediaPlayback = 2;

        /// <summary>The type to start the foreground service with: from Android 10 the type can be given when starting; before, there is none. Android 14 requires it (and the permission FOREGROUND_SERVICE_MEDIA_PLAYBACK).</summary>
        public static int TypeFor(int sdkInt) { return sdkInt >= 29 ? TypeMediaPlayback : 0; }

        /// <summary>From Android 8 a service started from the app has to be started with startForegroundService and call startForeground within five seconds.</summary>
        public static bool NeedsStartForegroundService(int sdkInt) { return sdkInt >= 26; }

        /// <summary>The notification channel exists from Android 8; before, the notification has a priority of its own.</summary>
        public static bool NeedsNotificationChannel(int sdkInt) { return sdkInt >= 26; }
    }

    /// <summary>
    /// A service started with startForegroundService must call startForeground before it is stopped: stopping it earlier makes Android end the whole app
    /// (ForegroundServiceDidNotStartInTimeException), and a send that fails at once does exactly that, since onStartCommand runs later on the main thread. The host asks this gate before
    /// stopping the service: while a start is on its way the stop is deferred, and the service, once in the foreground, stops itself. A start that never reached onStartCommand (the process
    /// lives on, the service did not) does not hold a stop back for ever: after <see cref="MaxWaitMs"/> the stop goes ahead.
    /// </summary>
    public sealed class ForegroundStartGate
    {
        /// <summary>Longer than the time Android gives a service to call startForeground (5 to 10 s).</summary>
        public const long MaxWaitMs = 15000;

        private readonly object _lock = new object();
        private int _pending;          // starts asked for whose onStartCommand has not run yet
        private long _lastStartMs;
        private bool _stopAfterStart;

        /// <summary>The host is about to call startForegroundService (a new sending: a stop asked for before it no longer applies).</summary>
        public void Starting(long nowMs) { lock (_lock) { _pending++; _lastStartMs = nowMs; _stopAfterStart = false; } }

        /// <summary>startForegroundService threw: nothing is on its way for that start.</summary>
        public void StartFailed() { lock (_lock) { if (_pending > 0) _pending--; } }

        /// <summary>The host wants the service stopped. true = stop it now; false = a start is on its way, the service stops itself once it is in the foreground.</summary>
        public bool RequestStop(long nowMs)
        {
            lock (_lock)
            {
                if (_pending > 0 && nowMs - _lastStartMs < MaxWaitMs) { _stopAfterStart = true; return false; }
                _pending = 0; _stopAfterStart = false;
                return true;
            }
        }

        /// <summary>The service has called startForeground (or tried to) for one start. true = the sending ended meanwhile: the service stops itself now.</summary>
        public bool Started()
        {
            lock (_lock)
            {
                if (_pending > 0) _pending--;
                if (_pending > 0 || !_stopAfterStart) return false;
                _stopAfterStart = false;
                return true;
            }
        }
    }
}
