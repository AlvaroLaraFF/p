using System;
using System.Runtime.CompilerServices;
using Android.Content;
using Android.Content.PM;
using AceEngine.App;
using AceEngine.Cast;
using PUi.Cast;

namespace PAndroid
{
    /// <summary>
    /// The one place that says whether the Google Cast SDK may be used on this device (see <see cref="CastPolicy"/>). The Cast sender that will connect to "Enviar a la tele" has to be created ONLY
    /// when <see cref="IsEnabled"/> is true, and every class of the SDK has to stay behind that check (in its own types, never named by the activity or the platform): on a Fire TV, or on any
    /// device without Google Play services, the SDK's classes cannot work and the app must start exactly as it does without Cast (the interface shows no Chromecast and DLNA keeps working).
    /// The SDK is named only by the classes of src/PAndroid/GoogleCast (a test checks it), reached from here through <see cref="CreateBridge"/>.
    /// </summary>
    internal static class AndroidCast
    {
        public static CastAvailability Availability { get; private set; }

        public static bool IsEnabled { get { return Availability == CastAvailability.Available; } }

        /// <summary>
        /// The Google Cast sender, or null if it cannot be made. Only call it when <see cref="IsEnabled"/> is true, on the main thread. NoInlining: the SDK's types are only loaded when this method runs,
        /// never because of a caller that is compiled on a device where they are useless.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IGoogleCastBridge CreateBridge(Context app, CastTargetList targets)
        {
            if (!IsEnabled) return null;
            try { return GoogleCast.GoogleCastBridge.Create(app, targets); }
            catch (Exception ex) { UdpLog.Warn("cast", "the Google Cast sender could not be started: " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        /// <summary>Decides once, at startup, and logs why. Never throws; a Fire TV is not even asked about Google Play services.</summary>
        public static CastAvailability Init(Context context, AndroidDeviceKind kind, CastMode mode)
        {
            bool gms = kind != AndroidDeviceKind.FireTv && mode != CastMode.Off && PlayServices.IsUsable(context);
            Availability = CastPolicy.Decide(kind, gms, mode);
            UdpLog.Info("cast", CastPolicy.Describe(Availability));
            return Availability;
        }
    }

    internal static class PlayServices
    {
        public const string Package = "com.google.android.gms";

        /// <summary>Google Play services are installed and enabled (the package is declared in the manifest's queries, or Android 11 would hide it). false on any doubt.</summary>
        public static bool IsUsable(Context context)
        {
            try
            {
#pragma warning disable CA1422
                var info = context.PackageManager.GetPackageInfo(Package, 0);
#pragma warning restore CA1422
                return info != null && info.ApplicationInfo != null && info.ApplicationInfo.Enabled;
            }
            catch (PackageManager.NameNotFoundException) { return false; }
            catch (Exception ex) { UdpLog.Warn("cast", "Google Play services check: " + ex.Message); return false; }
        }
    }
}
