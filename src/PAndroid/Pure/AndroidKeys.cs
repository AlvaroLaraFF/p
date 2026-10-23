using System.Collections.Generic;
using PUi.Input;

namespace PAndroid
{
    /// <summary>
    /// Android key codes (android.view.KeyEvent.KEYCODE_*, stable API values) as plain integers, so the table below is pure: it needs no Android API and the tests run on the PC.
    /// MainActivity checks at startup that these values still match the SDK's Keycode enum and logs any difference.
    /// </summary>
    public static class KeyCodes
    {
        public const int Home = 3, Back = 4, Call = 5, EndCall = 6;
        public const int Digit0 = 7, Digit9 = 16;
        public const int DpadUp = 19, DpadDown = 20, DpadLeft = 21, DpadRight = 22, DpadCenter = 23;
        public const int VolumeUp = 24, VolumeDown = 25, Power = 26, Camera = 27;
        public const int Space = 62, Enter = 66, Del = 67, Menu = 82, Notification = 83, Search = 84;
        public const int MediaPlayPause = 85, MediaStop = 86, MediaNext = 87, MediaPrevious = 88, MediaRewind = 89, MediaFastForward = 90, Mute = 91;
        public const int PageUp = 92, PageDown = 93;
        public const int ButtonA = 96, ButtonB = 97, ButtonX = 99, ButtonY = 100, ButtonL1 = 102, ButtonR1 = 103, ButtonL2 = 104, ButtonR2 = 105;
        public const int ButtonThumbL = 106, ButtonThumbR = 107, ButtonStart = 108, ButtonSelect = 109, ButtonMode = 110;
        public const int Escape = 111, MediaPlay = 126, MediaPause = 127;
        public const int F1 = 131, F2 = 132, F3 = 133, F4 = 134;
        public const int Numpad0 = 144, Numpad9 = 153, NumpadEnter = 160;
        public const int VolumeMute = 164, Info = 165, ChannelUp = 166, ChannelDown = 167, Tv = 170, Guide = 172, Settings = 176, TvPower = 177, TvInput = 178;
        public const int StbPower = 179, StbInput = 180, AvrPower = 181, AvrInput = 182;
        public const int ProgRed = 183, ProgGreen = 184, ProgYellow = 185, ProgBlue = 186, AppSwitch = 187;
        public const int Assist = 219, BrightnessDown = 220, BrightnessUp = 221, Sleep = 223, Wakeup = 224, LastChannel = 229, VoiceAssist = 231;
        public const int HeadsetHook = 79;
        public const int MediaSkipForward = 272, MediaSkipBackward = 273, SoftSleep = 276, AllApps = 284;
    }

    /// <summary>
    /// The key table of the Android shell: Android key code (plus the character the key types, if any) to the logical keys of the interface. One table for TV remotes (Android TV, Google TV),
    /// the Fire TV remote, game pads and keyboards. Keys the system owns (Home, volume, power, the assistant button...) are never mapped, so the shell does not consume them.
    /// </summary>
    public static class AndroidKeys
    {
        private static readonly HashSet<int> SystemKeys = new HashSet<int>
        {
            KeyCodes.Home, KeyCodes.Call, KeyCodes.EndCall, KeyCodes.VolumeUp, KeyCodes.VolumeDown, KeyCodes.Power, KeyCodes.Camera, KeyCodes.Notification, KeyCodes.Search,
            KeyCodes.Mute, KeyCodes.VolumeMute, KeyCodes.Settings, KeyCodes.TvInput, KeyCodes.AppSwitch, KeyCodes.Assist, KeyCodes.VoiceAssist, KeyCodes.BrightnessDown, KeyCodes.BrightnessUp,
            KeyCodes.Sleep, KeyCodes.Wakeup, KeyCodes.SoftSleep, KeyCodes.AllApps, KeyCodes.Guide, KeyCodes.LastChannel,
            // the keys of a TV or a Fire TV Edition remote that drive the TV itself (power, the "Live" key, inputs) and the game pad's guide button
            KeyCodes.Tv, KeyCodes.TvPower, KeyCodes.StbPower, KeyCodes.StbInput, KeyCodes.AvrPower, KeyCodes.AvrInput, KeyCodes.ButtonMode,
        };

        /// <summary>Keys the system keeps for itself (Home, volume, power, assistant...): they are never consumed.</summary>
        public static bool IsSystemKey(int keyCode) { return SystemKeys.Contains(keyCode); }

        /// <summary>
        /// The logical key for an Android key code. <paramref name="unicodeChar"/> is what KeyEvent.UnicodeChar says the key types (0 if none; a dead key has its high bit set);
        /// <paramref name="ctrl"/> and <paramref name="alt"/> mean a shortcut, not typing. Returns UiKey.None for a key the interface has no use for.
        /// </summary>
        public static KeyPress Map(int keyCode, int unicodeChar = 0, bool ctrl = false, bool alt = false)
        {
            if (IsSystemKey(keyCode)) return new KeyPress(UiKey.None, Name(keyCode));
            UiKey k = UiKey.None;
            switch (keyCode)
            {
                case KeyCodes.DpadUp: k = UiKey.Up; break;
                case KeyCodes.DpadDown: k = UiKey.Down; break;
                case KeyCodes.DpadLeft: k = UiKey.Left; break;
                case KeyCodes.DpadRight: k = UiKey.Right; break;
                case KeyCodes.DpadCenter: case KeyCodes.Enter: case KeyCodes.NumpadEnter: case KeyCodes.ButtonA: k = UiKey.Ok; break;
                case KeyCodes.Back: case KeyCodes.Escape: case KeyCodes.ButtonB: k = UiKey.Back; break;
                case KeyCodes.Del: k = UiKey.Backspace; break;
                case KeyCodes.ChannelUp: case KeyCodes.PageUp: k = UiKey.ChannelUp; break;
                case KeyCodes.ChannelDown: case KeyCodes.PageDown: k = UiKey.ChannelDown; break;
                // Play/Pause is a toggle on a Fire TV remote; the stick presses of a game pad (Amazon's guidelines: "Left/Right Stick Press: Play/Pause") do the same
                case KeyCodes.MediaPlayPause: case KeyCodes.Space: case KeyCodes.HeadsetHook: case KeyCodes.ButtonThumbL: case KeyCodes.ButtonThumbR: k = UiKey.PlayPause; break;
                case KeyCodes.MediaPlay: k = UiKey.Play; break;
                case KeyCodes.MediaPause: k = UiKey.Pause; break;
                case KeyCodes.MediaStop: k = UiKey.Stop; break;
                // The shoulder buttons are rewind / fast forward on Amazon's game controllers ("Left Shoulder (L1): Rewind", "Right Shoulder (R1): Fast Forward")
                case KeyCodes.MediaRewind: case KeyCodes.MediaPrevious: case KeyCodes.MediaSkipBackward: case KeyCodes.ButtonL1: k = UiKey.Rewind; break;
                case KeyCodes.MediaFastForward: case KeyCodes.MediaNext: case KeyCodes.MediaSkipForward: case KeyCodes.ButtonR1: k = UiKey.Forward; break;
                // Menu (a remote's and a game controller's) shows and hides the playback panel; on a game pad without a Menu key, Y and Start are the "menu" (Amazon: "Other Game Controller Button: Y")
                case KeyCodes.Info: case KeyCodes.Menu: case KeyCodes.ButtonY: case KeyCodes.ButtonStart: k = UiKey.Info; break;
                case KeyCodes.ProgRed: case KeyCodes.F1: k = UiKey.Red; break;
                case KeyCodes.ProgGreen: case KeyCodes.F2: k = UiKey.Green; break;
                case KeyCodes.ProgYellow: case KeyCodes.F3: k = UiKey.Yellow; break;
                case KeyCodes.ProgBlue: case KeyCodes.F4: k = UiKey.Blue; break;
            }
            if (k != UiKey.None) return new KeyPress(k, Name(keyCode));
            char c;
            if (!ctrl && !alt && TypedChar(unicodeChar, out c)) return new KeyPress(UiKey.Char, Name(keyCode), c);
            return new KeyPress(UiKey.None, Name(keyCode));
        }

        /// <summary>A printable character the key types (letters, digits, punctuation, symbols); false for control characters, dead keys and anything outside the BMP.</summary>
        public static bool TypedChar(int unicodeChar, out char c)
        {
            c = '\0';
            if (unicodeChar <= 32 || unicodeChar > 0xFFFF) return false;       // negative = dead key (COMBINING_ACCENT bit)
            char ch = (char)unicodeChar;
            if (!(char.IsLetterOrDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))) return false;
            c = ch;
            return true;
        }

        /// <summary>true for the keys that act again while held down: navigation, channel change, seek and typing. A held OK, Back, colour or play key acts once.</summary>
        public static bool AllowsRepeat(UiKey key)
        {
            switch (key)
            {
                case UiKey.Up: case UiKey.Down: case UiKey.Left: case UiKey.Right:
                case UiKey.ChannelUp: case UiKey.ChannelDown: case UiKey.Rewind: case UiKey.Forward: case UiKey.Backspace: case UiKey.Char:
                    return true;
            }
            return false;
        }

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { KeyCodes.Back, "KEYCODE_BACK" }, { KeyCodes.DpadUp, "KEYCODE_DPAD_UP" }, { KeyCodes.DpadDown, "KEYCODE_DPAD_DOWN" }, { KeyCodes.DpadLeft, "KEYCODE_DPAD_LEFT" },
            { KeyCodes.DpadRight, "KEYCODE_DPAD_RIGHT" }, { KeyCodes.DpadCenter, "KEYCODE_DPAD_CENTER" }, { KeyCodes.Enter, "KEYCODE_ENTER" }, { KeyCodes.NumpadEnter, "KEYCODE_NUMPAD_ENTER" },
            { KeyCodes.Escape, "KEYCODE_ESCAPE" }, { KeyCodes.Del, "KEYCODE_DEL" }, { KeyCodes.Menu, "KEYCODE_MENU" }, { KeyCodes.Info, "KEYCODE_INFO" }, { KeyCodes.Space, "KEYCODE_SPACE" },
            { KeyCodes.PageUp, "KEYCODE_PAGE_UP" }, { KeyCodes.PageDown, "KEYCODE_PAGE_DOWN" }, { KeyCodes.ChannelUp, "KEYCODE_CHANNEL_UP" }, { KeyCodes.ChannelDown, "KEYCODE_CHANNEL_DOWN" },
            { KeyCodes.MediaPlayPause, "KEYCODE_MEDIA_PLAY_PAUSE" }, { KeyCodes.MediaPlay, "KEYCODE_MEDIA_PLAY" }, { KeyCodes.MediaPause, "KEYCODE_MEDIA_PAUSE" }, { KeyCodes.MediaStop, "KEYCODE_MEDIA_STOP" },
            { KeyCodes.MediaRewind, "KEYCODE_MEDIA_REWIND" }, { KeyCodes.MediaFastForward, "KEYCODE_MEDIA_FAST_FORWARD" }, { KeyCodes.MediaNext, "KEYCODE_MEDIA_NEXT" }, { KeyCodes.MediaPrevious, "KEYCODE_MEDIA_PREVIOUS" },
            { KeyCodes.ProgRed, "KEYCODE_PROG_RED" }, { KeyCodes.ProgGreen, "KEYCODE_PROG_GREEN" }, { KeyCodes.ProgYellow, "KEYCODE_PROG_YELLOW" }, { KeyCodes.ProgBlue, "KEYCODE_PROG_BLUE" },
            { KeyCodes.ButtonA, "KEYCODE_BUTTON_A" }, { KeyCodes.ButtonB, "KEYCODE_BUTTON_B" }, { KeyCodes.Home, "KEYCODE_HOME" }, { KeyCodes.VolumeUp, "KEYCODE_VOLUME_UP" }, { KeyCodes.VolumeDown, "KEYCODE_VOLUME_DOWN" },
            { KeyCodes.Power, "KEYCODE_POWER" }, { KeyCodes.VolumeMute, "KEYCODE_VOLUME_MUTE" }, { KeyCodes.Assist, "KEYCODE_ASSIST" }, { KeyCodes.VoiceAssist, "KEYCODE_VOICE_ASSIST" }, { KeyCodes.Search, "KEYCODE_SEARCH" },
            { KeyCodes.Guide, "KEYCODE_GUIDE" }, { KeyCodes.Settings, "KEYCODE_SETTINGS" },
            { KeyCodes.ButtonX, "KEYCODE_BUTTON_X" }, { KeyCodes.ButtonY, "KEYCODE_BUTTON_Y" }, { KeyCodes.ButtonL1, "KEYCODE_BUTTON_L1" }, { KeyCodes.ButtonR1, "KEYCODE_BUTTON_R1" },
            { KeyCodes.ButtonL2, "KEYCODE_BUTTON_L2" }, { KeyCodes.ButtonR2, "KEYCODE_BUTTON_R2" }, { KeyCodes.ButtonThumbL, "KEYCODE_BUTTON_THUMBL" }, { KeyCodes.ButtonThumbR, "KEYCODE_BUTTON_THUMBR" },
            { KeyCodes.ButtonStart, "KEYCODE_BUTTON_START" }, { KeyCodes.ButtonSelect, "KEYCODE_BUTTON_SELECT" }, { KeyCodes.ButtonMode, "KEYCODE_BUTTON_MODE" },
            { KeyCodes.Tv, "KEYCODE_TV" }, { KeyCodes.TvInput, "KEYCODE_TV_INPUT" }, { KeyCodes.TvPower, "KEYCODE_TV_POWER" }, { KeyCodes.MediaSkipForward, "KEYCODE_MEDIA_SKIP_FORWARD" },
            { KeyCodes.MediaSkipBackward, "KEYCODE_MEDIA_SKIP_BACKWARD" }, { KeyCodes.LastChannel, "KEYCODE_LAST_CHANNEL" }, { KeyCodes.AppSwitch, "KEYCODE_APP_SWITCH" },
        };

        /// <summary>"KEYCODE_DPAD_UP" for the common keys, "KEYCODE_&lt;n&gt;" for the rest: for the log, where every key press is recorded so the codes of a new remote can be learned.</summary>
        public static string Name(int keyCode)
        {
            string n;
            return Names.TryGetValue(keyCode, out n) ? n : "KEYCODE_" + keyCode;
        }
    }

    /// <summary>
    /// Decides what the activity does with each Android key event: which ones are consumed and which become a logical key. It remembers the keys whose press it consumed, so their release is
    /// consumed too (otherwise Back would run its default action on the release).
    /// </summary>
    public sealed class KeyDispatcher
    {
        private readonly HashSet<int> _down = new HashSet<int>();

        /// <summary>
        /// Handles one key event. Returns true if the event is consumed (the activity must not pass it on). <paramref name="press"/> is a logical key to raise only when <paramref name="raise"/> is true:
        /// a repeat of a key that does not repeat is consumed without raising anything.
        /// </summary>
        public bool OnKey(int keyCode, bool isDown, int repeatCount, int unicodeChar, bool ctrl, bool alt, out KeyPress press, out bool raise)
        {
            press = default(KeyPress); raise = false;
            if (!isDown)
            {
                if (_down.Remove(keyCode)) return true;
                return false;
            }
            var kp = AndroidKeys.Map(keyCode, unicodeChar, ctrl, alt);
            if (kp.Key == UiKey.None) return false;
            _down.Add(keyCode);
            if (repeatCount > 0 && !AndroidKeys.AllowsRepeat(kp.Key)) return true;
            press = kp; raise = true;
            return true;
        }

        /// <summary>Forgets the pressed keys (the window lost focus: the releases will not arrive).</summary>
        public void Reset() { _down.Clear(); }
    }
}
