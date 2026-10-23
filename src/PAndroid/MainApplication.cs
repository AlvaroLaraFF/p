using System;
using Android.App;
using Android.Runtime;

namespace PAndroid
{
    /// <summary>
    /// The process: the log goes to logcat and unhandled exceptions are recorded from the very first moment, before any activity exists. The activity builds everything else.
    /// pageSizeCompat: libSkiaSharp.so of SkiaSharp 2.88.9 (the version PUi is tied to while the Samsung TV is a target) is aligned to 4 KB pages; on a device whose kernel uses 16 KB pages
    /// (Android 15 and later, still rare) this asks the system to run the app in its compatibility mode instead of refusing it. Older systems ignore the attribute.
    /// </summary>
    [Application(Label = "P!", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round", Banner = "@drawable/banner", Theme = "@style/PTheme",
        NetworkSecurityConfig = "@xml/network_security_config", AllowBackup = false, SupportsRtl = true, UsesCleartextTraffic = false, LargeHeap = false, PageSizeCompat = "enabled")]
    public sealed class MainApplication : Application
    {
        public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        public override void OnCreate()
        {
            AndroidLog.InstallSink();
            AndroidLog.InstallUnhandledHandlers();
            base.OnCreate();
        }
    }
}
