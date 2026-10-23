using System;
using AceEngine.Net;

namespace AceEngine.App
{
    /// <summary>
    /// What a platform plugs into the engine's services (no Skia, no UI): its player, how it reads the network, the multicast lock if it needs one, playback tuning measured with its
    /// player and its own defaults for AppConfig. Everything is optional except Players (to play anything); null means "what the engine does on its own", which is what the Samsung TV uses.
    /// </summary>
    public sealed class PlatformHooks
    {
        /// <summary>Creates the platform's video player (one per playback attempt). Required by the real services.</summary>
        public IPlayerFactory Players;

        /// <summary>The device's network configuration, for platforms where System.Net.NetworkInformation does not work (since Android 10 /proc/net cannot be read). Null = SystemNetworkInfo. Used by the UPnP port mapper and the network diagnostics.</summary>
        public INetworkInfo Network;

        /// <summary>Taken while UPnP is wanted and the engine runs. Null = not needed.</summary>
        public IMulticastLock Multicast;

        /// <summary>
        /// Asked when the app goes to the background (TvServices.Suspend): true = something is being sent to another screen, so the engine (the channel it serves, its P2P network, its LAN server)
        /// stays alive; only the channel probing stops. The shell has to call Suspend again when the sending ends in the background. Null = the engine always stops (the TV).
        /// </summary>
        public Func<bool> KeepEngineAlive;

        /// <summary>Playback tuning (for example TimeshiftPlayerLatency, which is measured with each platform's player). Null = the defaults, which were measured with the Samsung player.</summary>
        public PlaybackOptions Playback;

        /// <summary>The platform's default values for AppConfig, applied before any configuration layer (AppConfigLoader.Load's defaults argument). Null = the built-in values, which are the TV's.</summary>
        public Action<AppConfig> ConfigDefaults;
    }

    /// <summary>The AppConfig default values of each platform. The built-in ones in AppConfig are the Samsung TV's, so its entry only restates them; new platforms get their own measured values.</summary>
    public static class AppConfigDefaults
    {
        /// <summary>Samsung TV (Tizen 5.5, 32-bit ARM, no battery): GPU sync with glFinish, at most ~8 ms of sprite creation per frame, the decoder warmer on (it silences the TV source).</summary>
        public static void Tizen(AppConfig c)
        {
            c.GlSync = "finish";
            c.SpriteBudgetMs = 8;
            c.GpuWarmup = false;
            c.Warmer = true;
        }

        /// <summary>
        /// Android (TV, Fire TV, phone, tablet): no Evas, so no GPU sync; no decoder warmer (there is no terrestrial TV source to silence); the UDP log is OFF (the log goes to logcat; a
        /// configuration layer or deploy-android.sh turns the UDP one on). The sprite budget is the TV's: a frame that creates sprites for more than 8 ms is a frame lost anywhere.
        /// The Android shell then applies the device profile's memory budgets as a second defaults layer.
        /// </summary>
        public static void Android(AppConfig c)
        {
            c.LogEnabled = false;
            c.GlSync = "none";
            c.SpriteBudgetMs = 8;
            c.GpuWarmup = false;
            c.Warmer = false;
        }
    }
}
