using System;
using AceEngine.App;
using Android.App;
using Android.Content;
using Android.OS;
using PUi.Cast;

namespace PAndroid
{
    /// <summary>
    /// What the phone does while something is being sent (ISendingHost): starts the foreground service (its notification, the Wi-Fi lock and the wake lock: see <see cref="CastSendingService"/>) when a
    /// send begins, keeps its notification up to date (a paused channel, another channel) and stops it when the sending ends. The first time a channel is sent it also asks for the notification
    /// permission (Android 13 and later), once or twice at most, with the system's own dialog: the sending works without it.
    /// </summary>
    internal sealed class AndroidSendingHost : ISendingHost
    {
        private readonly Context _app;
        private readonly Action _askNotificationPermission;
        private bool _running;

        public AndroidSendingHost(Context applicationContext, Action askNotificationPermission) { _app = applicationContext; _askNotificationPermission = askNotificationPermission; }

        public void Begin(string deviceName, string channelName)
        {
            try
            {
                var i = new Intent(_app, typeof(CastSendingService));
                i.PutExtra(CastNotificationText.ExtraDevice, deviceName ?? "");
                i.PutExtra(CastNotificationText.ExtraChannel, channelName ?? "");
                i.PutExtra(CastNotificationText.ExtraPaused, false);
                if (ForegroundServicePolicy.NeedsStartForegroundService((int)Build.VERSION.SdkInt)) _app.StartForegroundService(i); else _app.StartService(i);
                _running = true;
            }
            catch (Exception ex) { UdpLog.Warn("cast", "starting the foreground service: " + ex.GetType().Name + ": " + ex.Message); }
            try { if (_askNotificationPermission != null) _askNotificationPermission(); } catch (Exception ex) { UdpLog.Warn("cast", "notification permission: " + ex.Message); }
        }

        public void Update(string deviceName, string channelName, bool paused)
        {
            if (!_running) return;
            try { CastNotifications.Update(_app, deviceName, channelName, paused); }
            catch (Exception ex) { UdpLog.Warn("cast", "updating the notification: " + ex.Message); }
        }

        public void End()
        {
            if (!_running) return;
            _running = false;
            try { _app.StopService(new Intent(_app, typeof(CastSendingService))); }
            catch (Exception ex) { UdpLog.Warn("cast", "stopping the foreground service: " + ex.Message); }
        }
    }

    /// <summary>Asks for POST_NOTIFICATIONS the way <see cref="NotificationPermissionPolicy"/> says: from Android 13, not granted, at most twice.</summary>
    internal static class AndroidNotificationPermission
    {
        public const int RequestCode = 4202;
        private const string Prefs = "p-cast", AskedKey = "notification-asked";

        public static void MaybeAsk(Activity activity)
        {
            if (activity == null) return;
            int sdk = (int)Build.VERSION.SdkInt;
            if (sdk < NotificationPermissionPolicy.FirstSdkWithPermission) return;
            var prefs = activity.GetSharedPreferences(Prefs, FileCreationMode.Private);
            int asked = prefs.GetInt(AskedKey, 0);
            bool granted = activity.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Android.Content.PM.Permission.Granted;
            if (NotificationPermissionPolicy.Decide(sdk, granted, asked) != NotificationAsk.Ask) return;
            prefs.Edit().PutInt(AskedKey, asked + 1).Apply();
            UdpLog.Info("cast", "asking for the notification permission (time " + (asked + 1) + ")");
            activity.RunOnUiThread(() => { try { activity.RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, RequestCode); } catch (Exception ex) { UdpLog.Warn("cast", "permission request: " + ex.Message); } });
        }
    }
}
