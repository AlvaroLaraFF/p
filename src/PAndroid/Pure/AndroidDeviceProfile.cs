using System;
using AceEngine.App;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>What kind of device the one apk is running on.</summary>
    public enum AndroidDeviceKind
    {
        /// <summary>Android TV / Google TV (leanback or the television UI mode).</summary>
        Tv,
        /// <summary>Amazon Fire TV (feature amazon.hardware.fire_tv).</summary>
        FireTv,
        /// <summary>A phone: touch screen, small.</summary>
        Phone,
        /// <summary>A tablet: touch screen, smallest width of 600 dp or more.</summary>
        Tablet,
        /// <summary>No touch screen and not a TV (a set-top box, a PC with a keyboard): driven by keys like a TV.</summary>
        Box
    }

    public enum MemoryTier { Low, Mid, High }

    /// <summary>The facts the shell reads from the system to pick a profile; plain values, so the decision is testable on the PC.</summary>
    public sealed class DeviceFacts
    {
        public bool HasLeanback;
        /// <summary>The Amazon Fire TV system feature.</summary>
        public bool HasFireTvFeature;
        public bool HasTouchscreen;
        /// <summary>UiModeManager.CurrentModeType is UI_MODE_TYPE_TELEVISION.</summary>
        public bool IsTelevisionUiMode;
        /// <summary>A physical keyboard is attached (Configuration.keyboard is not NOKEYS).</summary>
        public bool HasKeyboard;
        /// <summary>An input device with a D-pad (a game pad, a remote, a TV box's controller) is attached.</summary>
        public bool HasDpad;
        /// <summary>ActivityManager.isLowRamDevice.</summary>
        public bool IsLowRamDevice;
        /// <summary>ActivityManager.MemoryInfo.totalMem, in bytes (0 = unknown).</summary>
        public long TotalMemBytes;
        public int SmallestWidthDp = 360;
        public int SdkInt;
        public string Manufacturer = "";
        public string Model = "";
        /// <summary>ActivityManager.memoryClass: the heap limit of an app, in MiB (0 = unknown). For the log.</summary>
        public int MemoryClassMiB;
        /// <summary>Build.SUPPORTED_ABIS, comma separated ("armeabi-v7a,armeabi" on a Fire TV stick). For the log.</summary>
        public string Abis = "";
        // Which keys the attached input devices can produce (KeyCharacterMap.DeviceHasKey): a remote's key layout often lists keys it has no button for, so these are only used where the
        // device kind does not decide by itself (an Android TV).
        public bool DeviceHasChannelKeys = true, DeviceHasDigitKeys = true, DeviceHasColorKeys = true;
    }

    /// <summary>
    /// What this device is and what the app does about it: the kind of device, the capabilities the interface sees, the memory budgets and the size to draw at. Detected once at startup
    /// from <see cref="DeviceFacts"/>; there is one apk and every difference between a TV, a Fire TV stick and a phone comes from here.
    /// </summary>
    public sealed class AndroidDeviceProfile
    {
        public AndroidDeviceKind Kind { get; private set; }
        public MemoryTier Memory { get; private set; }
        public PlatformCapabilities Capabilities { get; private set; }
        public bool IsLowRam { get; private set; }
        public long TotalMemMiB { get; private set; }
        /// <summary>Longest side, in pixels, of the surface the interface is drawn on; the compositor scales it up to the screen. 1280 is 720p.</summary>
        public int MaxRenderLongSide { get; private set; }
        /// <summary>A TV or box: no notch or system bar insets apply (the screen is the picture).</summary>
        public bool IgnoresSafeInsets { get { return Kind == AndroidDeviceKind.Tv || Kind == AndroidDeviceKind.FireTv || Kind == AndroidDeviceKind.Box; } }
        /// <summary>Touch is the main input (phone, tablet).</summary>
        public bool IsTouchDevice { get { return Kind == AndroidDeviceKind.Phone || Kind == AndroidDeviceKind.Tablet; } }
        // Memory budgets in MiB (the AppConfig defaults for this device; a configuration layer still overrides them)
        public int LiveBufferMiB { get; private set; }
        public int LiveTimeshiftMiB { get; private set; }
        public int VodBufferMiB { get; private set; }
        /// <summary>The Fire TV model Amazon documents for this device (null for anything else, or a model that is not listed).</summary>
        public FireTvModel KnownModel { get; private set; }
        /// <summary>A device with one of the weaker GPUs (IMG GE8300, Mali-450): drawn at 720p whatever its memory.</summary>
        public bool WeakGpu { get; private set; }
        /// <summary>Frames per second of the interface while only the animated background moves (PApp.AmbientInterval). 30 normally; 20 on a weak TV device, which is exactly three screen refreshes at 60 Hz.</summary>
        public int AmbientFps { get; private set; }
        public double AmbientInterval { get { return 1.0 / AmbientFps; } }
        /// <summary>The player keeps the screen on only while a channel is really playing (not paused, not held by another app's audio): Amazon requires TV apps to let the screensaver start otherwise.</summary>
        public bool ReleaseScreenWhenNotPlaying { get { return !IsTouchDevice; } }

        public const int Below720pLongSide = 1280, FullHdLongSide = 1920, PhoneLongSide = 3200, NormalAmbientFps = 30, WeakAmbientFps = 20;
        /// <summary>Under this much total memory, or on a device that says it is low-RAM, the profile is Low (a 1 GB stick reports about 0.9 GiB).</summary>
        public const long LowMemMiB = 1200, MidMemMiB = 2800;

        private AndroidDeviceProfile() { }

        public static AndroidDeviceKind DetectKind(DeviceFacts f)
        {
            if (f.HasFireTvFeature) return AndroidDeviceKind.FireTv;
            // a Fire TV that does not report its system feature still reports its model ("AFTSSS"); only a device without touch can be one
            if (!f.HasTouchscreen && FireTvModels.IsFireTv(f.Manufacturer, f.Model)) return AndroidDeviceKind.FireTv;
            if (f.HasLeanback || f.IsTelevisionUiMode) return AndroidDeviceKind.Tv;
            if (!f.HasTouchscreen) return AndroidDeviceKind.Box;
            return f.SmallestWidthDp >= 600 ? AndroidDeviceKind.Tablet : AndroidDeviceKind.Phone;
        }

        public static MemoryTier DetectMemory(DeviceFacts f)
        {
            long mib = f.TotalMemBytes / (1024 * 1024);
            // the system did not say how much memory there is: a Fire TV model that Amazon documents has its nominal RAM (the system reports about 10 % less)
            if (mib == 0) { var known = FireTvModels.Lookup(f.Model); if (known != null) mib = known.RamMiB * 9 / 10; }
            if (f.IsLowRamDevice || (mib > 0 && mib < LowMemMiB)) return MemoryTier.Low;
            if (mib > 0 && mib < MidMemMiB) return MemoryTier.Mid;
            return MemoryTier.High;
        }

        /// <param name="forcedKind">A configuration override (to try the TV interface on a phone); null = detect.</param>
        /// <param name="maxRenderLongSide">A configuration override of the draw size; 0 = the profile's own.</param>
        /// <param name="ambientFps">A configuration override of the background's frame rate; 0 = the profile's own.</param>
        public static AndroidDeviceProfile From(DeviceFacts f, AndroidDeviceKind? forcedKind = null, int maxRenderLongSide = 0, int ambientFps = 0)
        {
            var p = new AndroidDeviceProfile();
            p.Kind = forcedKind ?? DetectKind(f);
            p.Memory = DetectMemory(f);
            p.IsLowRam = f.IsLowRamDevice;
            p.TotalMemMiB = f.TotalMemBytes / (1024 * 1024);
            p.KnownModel = FireTvModels.Lookup(f.Model);
            p.WeakGpu = p.KnownModel != null && p.KnownModel.WeakGpu;
            switch (p.Kind)
            {
                case AndroidDeviceKind.FireTv:
                    // the Fire TV remote has no channel, digit or colour keys, whatever its key layout lists
                    p.Capabilities = new PlatformCapabilities(false, false, false, true, false, f.HasKeyboard);
                    break;
                case AndroidDeviceKind.Tv:
                case AndroidDeviceKind.Box:
                    p.Capabilities = new PlatformCapabilities(f.DeviceHasChannelKeys, f.DeviceHasDigitKeys, f.DeviceHasColorKeys, true, false, f.HasKeyboard);
                    break;
                default:
                    // phone or tablet: touch is the main input; a physical keyboard types, a game pad or remote with a D-pad navigates
                    p.Capabilities = f.HasKeyboard || f.HasDpad ? new PlatformCapabilities(false, false, false, f.HasDpad, true, f.HasKeyboard) : PlatformCapabilities.TouchOnly;
                    break;
            }
            switch (p.Memory)
            {
                case MemoryTier.Low: p.LiveBufferMiB = 16; p.LiveTimeshiftMiB = 32; p.VodBufferMiB = 24; break;
                case MemoryTier.Mid: p.LiveBufferMiB = 24; p.LiveTimeshiftMiB = 48; p.VodBufferMiB = 32; break;
                default: p.LiveBufferMiB = 24; p.LiveTimeshiftMiB = 64; p.VodBufferMiB = 48; break;
            }
            if (maxRenderLongSide > 0) p.MaxRenderLongSide = maxRenderLongSide;
            else if (p.Memory == MemoryTier.Low || (p.WeakGpu && !p.IsTouchDevice)) p.MaxRenderLongSide = Below720pLongSide;
            else p.MaxRenderLongSide = p.IsTouchDevice ? PhoneLongSide : FullHdLongSide;
            if (ambientFps > 0) p.AmbientFps = ambientFps;
            else p.AmbientFps = !p.IsTouchDevice && (p.Memory == MemoryTier.Low || p.WeakGpu) ? WeakAmbientFps : NormalAmbientFps;
            return p;
        }

        /// <summary>Sets the memory budgets of the engine to this device's. Used as the defaults layer of the configuration, so the user's configuration still wins.</summary>
        public void ApplyBudgets(AppConfig c)
        {
            c.LiveBufferMiB = LiveBufferMiB; c.LiveTimeshiftMiB = LiveTimeshiftMiB; c.VodBufferMiB = VodBufferMiB;
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            return Kind + (KnownModel != null ? " [" + KnownModel + "]" : "") + ", memory " + Memory + " (" + TotalMemMiB + " MiB" + (IsLowRam ? ", low-RAM device" : "") + (WeakGpu ? ", weak GPU" : "") +
                "), draws up to " + MaxRenderLongSide + " px, background " + AmbientFps + " fps, budgets live " + LiveBufferMiB + " / timeshift " + LiveTimeshiftMiB +
                " / vod " + VodBufferMiB + " MiB, keys: ch=" + Capabilities.HasChannelKeys + " digits=" + Capabilities.HasDigitKeys + " colors=" + Capabilities.HasColorKeys +
                ", remote=" + Capabilities.HasRemote + " touch=" + Capabilities.HasTouch + " keyboard=" + Capabilities.HasKeyboard;
        }
    }

    public enum PlayerKind { Exo, MediaPlayer }

    /// <summary>"softwareAudio": whether the app decodes MP2 audio itself (see Mp2Decoder).</summary>
    public enum SoftwareAudioMode
    {
        /// <summary>Only when the device has no MP2 decoder of its own (the default).</summary>
        Auto,
        /// <summary>Never: a channel with MP2 audio is silent, and says so, where the device cannot decode it.</summary>
        Off,
        /// <summary>Always, even where the device has an MP2 decoder (to test the software path on a device that has one, or when its decoder misbehaves).</summary>
        Force
    }

    /// <summary>"cast": whether "Enviar a la tele" through the Google Cast SDK may be offered. Never on a device without Google Play services, whatever this says.</summary>
    public enum CastMode { Auto, Off }

    /// <summary>The Android shell's own options, read from the configuration's "platform" section (AppConfig.Platform).</summary>
    public sealed class AndroidOptions
    {
        /// <summary>"player": "exo" (default) or "mediaplayer".</summary>
        public PlayerKind Player = PlayerKind.Exo;
        /// <summary>"deviceKind": tv, firetv, phone, tablet or box. Forces the profile (to try the TV interface on a phone, or the phone's on a TV).</summary>
        public AndroidDeviceKind? ForcedKind;
        /// <summary>"maxRenderSide": longest side in pixels of the drawing surface (0 = the profile's own).</summary>
        public int MaxRenderSide;
        /// <summary>"killOnExit": end the process when the activity finishes (default true: a clean start next time and the memory back).</summary>
        public bool KillProcessOnExit = true;
        /// <summary>"ambientFps": frames per second while only the background moves (0 = the profile's own: 30, 20 on a weak TV device).</summary>
        public int AmbientFps;
        /// <summary>"softwareAudio": "auto" (default), "off" or "force".</summary>
        public SoftwareAudioMode SoftwareAudio = SoftwareAudioMode.Auto;
        /// <summary>"cast": "auto" (default) or "off".</summary>
        public CastMode Cast = CastMode.Auto;
        /// <summary>"hdmiPause": a Fire TV pauses the channel when its HDMI output goes away (default true; false if a television makes the link flicker and the channel pauses for nothing).</summary>
        public bool HdmiPause = true;

        public static AndroidOptions From(System.Collections.Generic.IDictionary<string, object> platform)
        {
            var o = new AndroidOptions();
            if (platform == null) return o;
            object v;
            string s;
            if (platform.TryGetValue("player", out v) && (s = v as string) != null)
            {
                if (string.Equals(s, "mediaplayer", StringComparison.OrdinalIgnoreCase)) o.Player = PlayerKind.MediaPlayer;
                else if (string.Equals(s, "exo", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "exoplayer", StringComparison.OrdinalIgnoreCase)) o.Player = PlayerKind.Exo;
            }
            if (platform.TryGetValue("deviceKind", out v) && (s = v as string) != null)
            {
                switch (s.ToLowerInvariant())
                {
                    case "tv": o.ForcedKind = AndroidDeviceKind.Tv; break;
                    case "firetv": case "fire": o.ForcedKind = AndroidDeviceKind.FireTv; break;
                    case "phone": o.ForcedKind = AndroidDeviceKind.Phone; break;
                    case "tablet": o.ForcedKind = AndroidDeviceKind.Tablet; break;
                    case "box": o.ForcedKind = AndroidDeviceKind.Box; break;
                }
            }
            if (platform.TryGetValue("maxRenderSide", out v) && v is long) o.MaxRenderSide = (int)Math.Max(0, Math.Min((long)v, 8192));
            if (platform.TryGetValue("killOnExit", out v) && v is bool) o.KillProcessOnExit = (bool)v;
            if (platform.TryGetValue("ambientFps", out v) && v is long) o.AmbientFps = (int)Math.Max(0, Math.Min((long)v, 60));
            if (platform.TryGetValue("softwareAudio", out v) && (s = v as string) != null)
            {
                if (string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) o.SoftwareAudio = SoftwareAudioMode.Off;
                else if (string.Equals(s, "force", StringComparison.OrdinalIgnoreCase)) o.SoftwareAudio = SoftwareAudioMode.Force;
                else if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) o.SoftwareAudio = SoftwareAudioMode.Auto;
            }
            if (platform.TryGetValue("cast", out v) && (s = v as string) != null && string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) o.Cast = CastMode.Off;
            if (platform.TryGetValue("hdmiPause", out v) && v is bool) o.HdmiPause = (bool)v;
            return o;
        }
    }
}
