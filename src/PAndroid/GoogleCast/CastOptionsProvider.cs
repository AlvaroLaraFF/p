using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Gms.Cast;
using Android.Gms.Cast.Framework;
using Android.Gms.Cast.Framework.Media;
using Android.Runtime;

// The Cast framework finds its options by the class named in this meta-data (read when CastContext is first created, which only happens behind AndroidCast.IsEnabled)
[assembly: MetaData("com.google.android.gms.cast.framework.OPTIONS_PROVIDER_CLASS_NAME", Value = "org.p.app.CastOptionsProvider")]

namespace PAndroid.GoogleCast
{
    /// <summary>
    /// The options of the Cast sender: Google's Default Media Receiver (CC1AD845: nothing to register, nothing to publish, and the receiver plays the HLS the engine serves), no notification and no
    /// media session of the SDK (the app has its own: "Enviando a ..." of the sending service), no session that survives the app (the engine serving the channel does not), and the receiver
    /// application is stopped when the session ends so that the Chromecast goes back to what it was doing.
    /// </summary>
    [Register("org.p.app.CastOptionsProvider")]
    public sealed class CastOptionsProvider : Java.Lang.Object, IOptionsProvider
    {
        public CastOptionsProvider() { }
        public CastOptionsProvider(System.IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        public CastOptions GetCastOptions(Context appContext)
        {
            var media = new CastMediaOptions.Builder().SetNotificationOptions(null).SetMediaSessionEnabled(false).Build();
            return new CastOptions.Builder()
                .SetReceiverApplicationId(CastMediaControlIntent.DefaultMediaReceiverApplicationId)
                .SetCastMediaOptions(media)
                .SetResumeSavedSession(false)
                .SetEnableReconnectionService(false)
                .SetStopReceiverApplicationWhenEndingSession(true)
                .Build();
        }

        public IList<SessionProvider> GetAdditionalSessionProviders(Context appContext) { return null; }
    }
}
