using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;

namespace PAndroid
{
    /// <summary>Reads from the system the facts the device profile decides on (see AndroidDeviceProfile).</summary>
    internal static class AndroidDevice
    {
        public static DeviceFacts ReadFacts(Context context)
        {
            var f = new DeviceFacts { SdkInt = (int)Build.VERSION.SdkInt, Manufacturer = Build.Manufacturer ?? "", Model = Build.Model ?? "" };
            try { var abis = Build.SupportedAbis; if (abis != null) f.Abis = string.Join(",", abis); } catch (Exception) { }
            try
            {
                var pm = context.PackageManager;
                f.HasLeanback = pm.HasSystemFeature(PackageManager.FeatureLeanback);
                f.HasFireTvFeature = pm.HasSystemFeature("amazon.hardware.fire_tv");
                f.HasTouchscreen = pm.HasSystemFeature(PackageManager.FeatureTouchscreen);
            }
            catch (Exception ex) { AceEngine.App.UdpLog.Warn("device", "features: " + ex.Message); }
            try
            {
                var ui = (UiModeManager)context.GetSystemService(Context.UiModeService);
                f.IsTelevisionUiMode = ui != null && ui.CurrentModeType == UiMode.TypeTelevision;
            }
            catch (Exception) { }
            try
            {
                var cfg = context.Resources.Configuration;
                f.SmallestWidthDp = cfg.SmallestScreenWidthDp > 0 ? cfg.SmallestScreenWidthDp : 360;
                // a hardware keyboard that is not hidden (a Bluetooth keyboard on a phone or tablet); a TV remote without keys does not count
                f.HasKeyboard = cfg.Keyboard == Android.Content.Res.KeyboardType.Qwerty && cfg.HardKeyboardHidden == HardKeyboardHidden.No;
            }
            catch (Exception) { }
            try
            {
                var am = (ActivityManager)context.GetSystemService(Context.ActivityService);
                f.IsLowRamDevice = am.IsLowRamDevice;
                f.MemoryClassMiB = am.MemoryClass;
                var mi = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                f.TotalMemBytes = mi.TotalMem;
            }
            catch (Exception) { }
            try
            {
                foreach (int id in InputDevice.GetDeviceIds())
                {
                    var d = InputDevice.GetDevice(id);
                    if (d == null || d.IsVirtual) continue;
                    if ((d.Sources & InputSourceType.Dpad) == InputSourceType.Dpad || (d.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad) { f.HasDpad = true; break; }
                }
            }
            catch (Exception) { }
            try
            {
                // which keys the attached input devices can produce; a remote's key layout may list keys it has no button for, which is why the Fire TV profile ignores these
                f.DeviceHasChannelKeys = KeyCharacterMap.DeviceHasKey(Keycode.ChannelUp) && KeyCharacterMap.DeviceHasKey(Keycode.ChannelDown);
                f.DeviceHasDigitKeys = KeyCharacterMap.DeviceHasKey(Keycode.Num5);
                f.DeviceHasColorKeys = KeyCharacterMap.DeviceHasKey(Keycode.ProgRed);
            }
            catch (Exception) { }
            return f;
        }

        /// <summary>The one line of facts for the log, to learn what a new device reports.</summary>
        public static string Describe(DeviceFacts f)
        {
            return f.Manufacturer + " " + f.Model + " (Android SDK " + f.SdkInt + ", build " + (Build.Display ?? "?") + ", ABIs " + f.Abis + "): leanback=" + f.HasLeanback + " fireTv=" + f.HasFireTvFeature + " touch=" + f.HasTouchscreen + " tvUiMode=" + f.IsTelevisionUiMode +
                " keyboard=" + f.HasKeyboard + " dpad=" + f.HasDpad + " smallestWidth=" + f.SmallestWidthDp + "dp lowRam=" + f.IsLowRamDevice + " totalMem=" + (f.TotalMemBytes >> 20) + "MiB heapLimit=" + f.MemoryClassMiB + "MiB" +
                " deviceKeys(ch=" + f.DeviceHasChannelKeys + ",digits=" + f.DeviceHasDigitKeys + ",colors=" + f.DeviceHasColorKeys + ")";
        }

        /// <summary>
        /// Compares the key code constants of the pure table (PAndroid.KeyCodes) with the SDK's Keycode enum: returns the names that differ (empty if the table is right). Run once at startup
        /// and logged: the table is tested on the PC, where the SDK is not, so this is its only check against the real values.
        /// </summary>
        public static string CheckKeyCodes()
        {
            var bad = new System.Text.StringBuilder();
            void Check(string name, int mine, Keycode sdk) { if (mine != (int)sdk) bad.Append(' ').Append(name).Append('=').Append(mine).Append("!=").Append((int)sdk); }
            Check("Home", KeyCodes.Home, Keycode.Home); Check("Back", KeyCodes.Back, Keycode.Back);
            Check("Digit0", KeyCodes.Digit0, Keycode.Num0); Check("Digit9", KeyCodes.Digit9, Keycode.Num9);
            Check("DpadUp", KeyCodes.DpadUp, Keycode.DpadUp); Check("DpadDown", KeyCodes.DpadDown, Keycode.DpadDown); Check("DpadLeft", KeyCodes.DpadLeft, Keycode.DpadLeft);
            Check("DpadRight", KeyCodes.DpadRight, Keycode.DpadRight); Check("DpadCenter", KeyCodes.DpadCenter, Keycode.DpadCenter);
            Check("VolumeUp", KeyCodes.VolumeUp, Keycode.VolumeUp); Check("VolumeDown", KeyCodes.VolumeDown, Keycode.VolumeDown); Check("Power", KeyCodes.Power, Keycode.Power);
            Check("Space", KeyCodes.Space, Keycode.Space); Check("Enter", KeyCodes.Enter, Keycode.Enter); Check("Del", KeyCodes.Del, Keycode.Del); Check("Menu", KeyCodes.Menu, Keycode.Menu);
            Check("Search", KeyCodes.Search, Keycode.Search);
            Check("MediaPlayPause", KeyCodes.MediaPlayPause, Keycode.MediaPlayPause); Check("MediaStop", KeyCodes.MediaStop, Keycode.MediaStop); Check("MediaNext", KeyCodes.MediaNext, Keycode.MediaNext);
            Check("MediaPrevious", KeyCodes.MediaPrevious, Keycode.MediaPrevious); Check("MediaRewind", KeyCodes.MediaRewind, Keycode.MediaRewind);
            Check("MediaFastForward", KeyCodes.MediaFastForward, Keycode.MediaFastForward); Check("Mute", KeyCodes.Mute, Keycode.Mute);
            Check("PageUp", KeyCodes.PageUp, Keycode.PageUp); Check("PageDown", KeyCodes.PageDown, Keycode.PageDown);
            Check("ButtonA", KeyCodes.ButtonA, Keycode.ButtonA); Check("ButtonB", KeyCodes.ButtonB, Keycode.ButtonB);
            Check("Escape", KeyCodes.Escape, Keycode.Escape); Check("MediaPlay", KeyCodes.MediaPlay, Keycode.MediaPlay); Check("MediaPause", KeyCodes.MediaPause, Keycode.MediaPause);
            Check("F1", KeyCodes.F1, Keycode.F1); Check("F4", KeyCodes.F4, Keycode.F4);
            Check("Numpad0", KeyCodes.Numpad0, Keycode.Numpad0); Check("Numpad9", KeyCodes.Numpad9, Keycode.Numpad9); Check("NumpadEnter", KeyCodes.NumpadEnter, Keycode.NumpadEnter);
            Check("VolumeMute", KeyCodes.VolumeMute, Keycode.VolumeMute); Check("Info", KeyCodes.Info, Keycode.Info);
            Check("ChannelUp", KeyCodes.ChannelUp, Keycode.ChannelUp); Check("ChannelDown", KeyCodes.ChannelDown, Keycode.ChannelDown); Check("Guide", KeyCodes.Guide, Keycode.Guide);
            Check("Settings", KeyCodes.Settings, Keycode.Settings); Check("TvInput", KeyCodes.TvInput, Keycode.TvInput);
            Check("ProgRed", KeyCodes.ProgRed, Keycode.ProgRed); Check("ProgGreen", KeyCodes.ProgGreen, Keycode.ProgGreen); Check("ProgYellow", KeyCodes.ProgYellow, Keycode.ProgYellow);
            Check("ProgBlue", KeyCodes.ProgBlue, Keycode.ProgBlue); Check("AppSwitch", KeyCodes.AppSwitch, Keycode.AppSwitch);
            Check("Assist", KeyCodes.Assist, Keycode.Assist); Check("VoiceAssist", KeyCodes.VoiceAssist, Keycode.VoiceAssist); Check("HeadsetHook", KeyCodes.HeadsetHook, Keycode.Headsethook);
            Check("LastChannel", KeyCodes.LastChannel, Keycode.LastChannel); Check("MediaSkipForward", KeyCodes.MediaSkipForward, Keycode.MediaSkipForward);
            Check("MediaSkipBackward", KeyCodes.MediaSkipBackward, Keycode.MediaSkipBackward); Check("AllApps", KeyCodes.AllApps, Keycode.AllApps);
            Check("ButtonX", KeyCodes.ButtonX, Keycode.ButtonX); Check("ButtonY", KeyCodes.ButtonY, Keycode.ButtonY);
            Check("ButtonL1", KeyCodes.ButtonL1, Keycode.ButtonL1); Check("ButtonR1", KeyCodes.ButtonR1, Keycode.ButtonR1);
            Check("ButtonL2", KeyCodes.ButtonL2, Keycode.ButtonL2); Check("ButtonR2", KeyCodes.ButtonR2, Keycode.ButtonR2);
            Check("ButtonThumbL", KeyCodes.ButtonThumbL, Keycode.ButtonThumbl); Check("ButtonThumbR", KeyCodes.ButtonThumbR, Keycode.ButtonThumbr);
            Check("ButtonStart", KeyCodes.ButtonStart, Keycode.ButtonStart); Check("ButtonSelect", KeyCodes.ButtonSelect, Keycode.ButtonSelect); Check("ButtonMode", KeyCodes.ButtonMode, Keycode.ButtonMode);
            Check("Tv", KeyCodes.Tv, Keycode.Tv); Check("TvPower", KeyCodes.TvPower, Keycode.TvPower);
            Check("StbPower", KeyCodes.StbPower, Keycode.StbPower); Check("StbInput", KeyCodes.StbInput, Keycode.StbInput);
            Check("AvrPower", KeyCodes.AvrPower, Keycode.AvrPower); Check("AvrInput", KeyCodes.AvrInput, Keycode.AvrInput);
            return bad.ToString();
        }
    }
}
