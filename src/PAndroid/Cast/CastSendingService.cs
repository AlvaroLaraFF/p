using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// The foreground service of a sending: while a channel is being sent to a television or a Chromecast the engine (the channel's P2P download, the HLS or the progressive stream the device
    /// fetches) runs in this process, and with the screen off Android would stop it. A foreground service of type mediaPlayback keeps the process alive, and its notification ("Enviando a ..." with
    /// a "Dejar de enviar" button) is how the user sees it and stops it; a Wi-Fi lock (high performance) keeps the radio awake and a partial wake lock keeps the CPU running. All of it ends with the
    /// sending. The service holds no state of its own: the notification is built from what the sending host gives it.
    /// </summary>
    [Service(Name = "org.p.app.CastSendingService", Exported = false, Label = "Envío a otra pantalla", ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
    public sealed class CastSendingService : Service
    {
        /// <summary>The user pressed "Dejar de enviar" in the notification: the cast controller stops the sending (and with it this service).</summary>
        public static event Action StopRequested;

        /// <summary>Between the host (startForegroundService, stopService) and onStartCommand: see <see cref="ForegroundStartGate"/>.</summary>
        internal static readonly ForegroundStartGate StartGate = new ForegroundStartGate();

        private PowerManager.WakeLock _wake;
        private WifiManager.WifiLock _wifi;

        public override IBinder OnBind(Intent intent) { return null; }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            try
            {
                if (intent != null && intent.Action == CastNotificationText.ActionStop)
                {
                    UdpLog.Info("cast", "stop requested from the notification");
                    var h = StopRequested;
                    if (h != null) h(); else { StopForegroundCompat(); StopSelf(); }
                    return StartCommandResult.NotSticky;
                }
                string device = intent != null ? intent.GetStringExtra(CastNotificationText.ExtraDevice) : null;
                string channel = intent != null ? intent.GetStringExtra(CastNotificationText.ExtraChannel) : null;
                bool paused = intent != null && intent.GetBooleanExtra(CastNotificationText.ExtraPaused, false);
                bool stopNow = false;
                try
                {
                    var n = CastNotifications.Create(this, device, channel, paused);
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(CastNotificationText.NotificationId, n, (ForegroundService)ForegroundServicePolicy.TypeFor((int)Build.VERSION.SdkInt));
                    else StartForeground(CastNotificationText.NotificationId, n);
                }
                finally
                {
                    // the sending ended before this ran (a send that failed at once): only now, after startForeground, can the service be stopped
                    stopNow = StartGate.Started();
                    if (stopNow) { UdpLog.Info("cast", "the sending ended while the foreground service was starting: stopping it"); StopForegroundCompat(); StopSelf(startId); }
                }
                if (stopNow) return StartCommandResult.NotSticky;
                AcquireLocks();
                UdpLog.Info("cast", "foreground service started (" + CastNotificationText.Title(device) + ")");
            }
            catch (Exception ex) { UdpLog.Warn("cast", "foreground service: " + ex.GetType().Name + ": " + ex.Message); }
            return StartCommandResult.NotSticky;
        }

        private void AcquireLocks()
        {
            try
            {
                if (_wake == null)
                {
                    var pm = (PowerManager)GetSystemService(PowerService);
                    if (pm != null) { _wake = pm.NewWakeLock(WakeLockFlags.Partial, "p:cast-sending"); _wake.SetReferenceCounted(false); }
                }
                if (_wake != null && !_wake.IsHeld) _wake.Acquire();
                if (_wifi == null)
                {
                    var wm = (WifiManager)ApplicationContext.GetSystemService(WifiService);
                    if (wm != null) { _wifi = wm.CreateWifiLock(WifiMode.FullHighPerf, "p:cast-sending"); _wifi.SetReferenceCounted(false); }
                }
                if (_wifi != null && !_wifi.IsHeld) _wifi.Acquire();
            }
            catch (Exception ex) { UdpLog.Warn("cast", "locks: " + ex.Message); }
        }

        private void ReleaseLocks()
        {
            try { if (_wifi != null && _wifi.IsHeld) _wifi.Release(); } catch (Exception ex) { UdpLog.Warn("cast", "wifi lock: " + ex.Message); }
            try { if (_wake != null && _wake.IsHeld) _wake.Release(); } catch (Exception ex) { UdpLog.Warn("cast", "wake lock: " + ex.Message); }
        }

        private void StopForegroundCompat()
        {
            try { if (Build.VERSION.SdkInt >= BuildVersionCodes.N) StopForeground(StopForegroundFlags.Remove); else StopForeground(true); }
            catch (Exception) { }
        }

        public override void OnDestroy()
        {
            ReleaseLocks();
            UdpLog.Info("cast", "foreground service stopped");
            base.OnDestroy();
        }
    }

    /// <summary>Builds and updates the notification of a sending.</summary>
    internal static class CastNotifications
    {
        public static void EnsureChannel(Context ctx)
        {
            if (!ForegroundServicePolicy.NeedsNotificationChannel((int)Build.VERSION.SdkInt)) return;
            var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);
            if (nm == null || nm.GetNotificationChannel(CastNotificationText.ChannelId) != null) return;
            var ch = new NotificationChannel(CastNotificationText.ChannelId, CastNotificationText.ChannelName, NotificationImportance.Low) { Description = CastNotificationText.ChannelDescription };
            ch.SetShowBadge(false);
            nm.CreateNotificationChannel(ch);
        }

        public static Notification Create(Context ctx, string device, string channel, bool paused)
        {
            EnsureChannel(ctx);
            Notification.Builder b = ForegroundServicePolicy.NeedsNotificationChannel((int)Build.VERSION.SdkInt) ? new Notification.Builder(ctx, CastNotificationText.ChannelId) : new Notification.Builder(ctx);
            int icon = ctx.Resources.GetIdentifier("ic_stat_cast", "drawable", ctx.PackageName);
            b.SetContentTitle(CastNotificationText.Title(device)).SetContentText(CastNotificationText.Text(channel, paused)).SetOngoing(true).SetOnlyAlertOnce(true).SetShowWhen(false);
            b.SetCategory(Notification.CategoryTransport).SetVisibility(NotificationVisibility.Public);
            if (icon != 0) b.SetSmallIcon(icon); else b.SetSmallIcon(Android.Resource.Drawable.IcMediaPlay);
            b.SetColor(Android.Graphics.Color.Rgb(0x3F, 0xE8, 0xCF));
            var launch = ctx.PackageManager.GetLaunchIntentForPackage(ctx.PackageName);
            if (launch != null) b.SetContentIntent(PendingIntent.GetActivity(ctx, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
            var stop = new Intent(ctx, typeof(CastSendingService)).SetAction(CastNotificationText.ActionStop);
            var stopPi = PendingIntent.GetService(ctx, 1, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var action = new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(ctx, icon != 0 ? icon : Android.Resource.Drawable.IcMediaPlay), CastNotificationText.StopLabel, stopPi).Build();
            b.AddAction(action);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S) b.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
            return b.Build();
        }

        public static void Update(Context ctx, string device, string channel, bool paused)
        {
            var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService);
            if (nm != null) nm.Notify(CastNotificationText.NotificationId, Create(ctx, device, channel, paused));
        }
    }
}
