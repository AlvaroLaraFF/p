using System;
using AceEngine.App;
using Android.App;
using AceEngine.Cast;
using AceEngine.Net;
using AceEngine.P2P;
using PUi.Cast;

namespace PAndroid
{
    /// <summary>
    /// Builds the phone's and the tablet's "Enviar a la tele": the engine's CastService (DLNA televisions: found by the engine, controlled by it), the Google Cast sender if the device may use it
    /// (AndroidCast.IsEnabled) and the sending host (foreground service, locks, notification), joined by the cast controller PUi talks to. On a TV, a Fire TV or a box there is nothing to build:
    /// they are the receivers and the interface never shows the function (see <see cref="SendPolicy"/>).
    /// </summary>
    internal static class AndroidCastFactory
    {
        /// <returns>The controller, or null where the device does not send.</returns>
        public static CastController Create(Activity activity, AndroidDeviceProfile profile, Func<AceEngineHost> engineHost, INetworkInfo network)
        {
            UdpLog.Info("cast", SendPolicy.Describe(profile.Kind, AndroidCast.Availability));
            if (!SendPolicy.Offers(profile.Kind)) return null;
            var app = activity.ApplicationContext;
            var options = new CastServiceOptions { DlnaRetention = TimeSpan.FromSeconds(40) };
            // the HLS segments are byte ranges of the channel's live buffer: it needs room for the playlist's window and a reserve, so not less than 48 MiB even on a device with little memory
            options.Source.TimeshiftBytes = Math.Max(48, profile.LiveTimeshiftMiB) * 1024L * 1024;
            var service = new CastService(engineHost, network, null, options);
            var bridge = AndroidCast.CreateBridge(app, service.Targets);
            var host = new AndroidSendingHost(app, () => AndroidNotificationPermission.MaybeAsk(activity));
            return new CastController(new CastEngine(service), bridge, host, new AndroidMulticastLock(app, "p-cast"));
        }
    }
}
