using System.Collections.Generic;
using System.Text;
using PUi.Input;

namespace PAndroid
{
    /// <summary>Which kind of controller produces a key (Amazon documents the Fire TV controllers in "Remote Control Input", "Game Controller Input" and "Controller Behavior Guidelines").</summary>
    public enum ControllerKind
    {
        /// <summary>Every Fire TV remote: Alexa Voice Remote, Lite and Pro, the remotes of Fire TV Edition televisions, the Fire TV Cube's. Amazon: "all buttons, Android events and behavior guidelines are the same for all remotes".</summary>
        FireTvRemote,
        /// <summary>The Amazon Fire Game Controller and any Bluetooth game pad (the HID gamepad profile).</summary>
        GamePad,
        /// <summary>The first generation Fire Game Controller, which also has the remote's Play/Pause, Rewind and Fast Forward keys.</summary>
        FireGameController1stGen,
        /// <summary>A Bluetooth or USB keyboard.</summary>
        Keyboard
    }

    /// <summary>One physical button of a controller: the Android key code it sends to the app and what the interface does with it.</summary>
    public sealed class ControllerKey
    {
        public readonly ControllerKind Source;
        public readonly string Button;
        public readonly int KeyCode;
        /// <summary>The logical key of the interface (UiKey.None for the keys that belong to the system).</summary>
        public readonly UiKey Key;
        /// <summary>The activity consumes the event (true) or lets the system have it (false: Home, volume, power, the assistant...).</summary>
        public readonly bool Consumed;
        public readonly string Note;

        public ControllerKey(ControllerKind source, string button, int keyCode, UiKey key, bool consumed, string note = "")
        {
            Source = source; Button = button; KeyCode = keyCode; Key = key; Consumed = consumed; Note = note;
        }
    }

    /// <summary>
    /// The complete table of the keys of the Fire TV controllers and what P! does with each one. It is documentation that is also a test: <see cref="AndroidKeys"/> is the code that decides, this
    /// table says what it has to decide, and tests/PAndroid.Tests/FireTvKeyTests checks every row against it (and against the interface's player). Sources, all on developer.amazon.com:
    /// docs/fire-tv/remote-input.html (remote buttons and their KeyEvent constants), docs/fire-tv/game-controller-input.html (game controller buttons, primary and secondary events) and
    /// docs/fire-tv/controller-behavior-guidelines.html (what Amazon suggests each button does: Menu shows and hides the playback chrome, L1/R1 are rewind and fast forward, the stick presses are
    /// play/pause, Home, the microphone and the volume are the system's). The rows for Home, the microphone, the app buttons and the TV controls are what Amazon says they are: the system's.
    /// </summary>
    public static class FireTvKeyTable
    {
        private static readonly List<ControllerKey> _rows = Build();

        public static IReadOnlyList<ControllerKey> Rows { get { return _rows; } }

        private static List<ControllerKey> Build()
        {
            var r = new List<ControllerKey>();
            const ControllerKind R = ControllerKind.FireTvRemote, G = ControllerKind.GamePad, G1 = ControllerKind.FireGameController1stGen, K = ControllerKind.Keyboard;

            // ---- the Fire TV remotes (Alexa Voice Remote, Lite, Pro, Fire TV Edition television remotes)
            r.Add(new ControllerKey(R, "Back", KeyCodes.Back, UiKey.Back, true, "returns to the previous screen; on the player it leaves it"));
            r.Add(new ControllerKey(R, "Menu", KeyCodes.Menu, UiKey.Info, true, "shows or hides the information panel of the player (Amazon: \"in media apps use Menu to show or hide the playback chrome\")"));
            r.Add(new ControllerKey(R, "Select", KeyCodes.DpadCenter, UiKey.Ok, true));
            r.Add(new ControllerKey(R, "Up", KeyCodes.DpadUp, UiKey.Up, true));
            r.Add(new ControllerKey(R, "Down", KeyCodes.DpadDown, UiKey.Down, true));
            r.Add(new ControllerKey(R, "Left", KeyCodes.DpadLeft, UiKey.Left, true));
            r.Add(new ControllerKey(R, "Right", KeyCodes.DpadRight, UiKey.Right, true));
            r.Add(new ControllerKey(R, "Play/Pause", KeyCodes.MediaPlayPause, UiKey.PlayPause, true, "a toggle; consumed even where it does nothing so that it does not reach another app (Amazon requirement 1.6)"));
            r.Add(new ControllerKey(R, "Rewind", KeyCodes.MediaRewind, UiKey.Rewind, true, "10 seconds back"));
            r.Add(new ControllerKey(R, "Fast Forward", KeyCodes.MediaFastForward, UiKey.Forward, true, "10 seconds forward"));
            r.Add(new ControllerKey(R, "Channel up", KeyCodes.ChannelUp, UiKey.ChannelUp, true, "Alexa Voice Remote Pro and Fire TV Edition remotes only"));
            r.Add(new ControllerKey(R, "Channel down", KeyCodes.ChannelDown, UiKey.ChannelDown, true, "Alexa Voice Remote Pro and Fire TV Edition remotes only"));
            // Home and the microphone never reach the app (Amazon: "a system event that cannot be intercepted"); if one ever does, it is not consumed
            r.Add(new ControllerKey(R, "Home", KeyCodes.Home, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Microphone / Alexa", KeyCodes.VoiceAssist, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Microphone / Alexa (assistant)", KeyCodes.Assist, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Search", KeyCodes.Search, UiKey.None, false, "system"));
            // Volume, mute and power exist on the remotes with TV controls and on Fire TV Edition remotes; Amazon: "these buttons cannot be mapped to events in third-party apps"
            r.Add(new ControllerKey(R, "Volume up", KeyCodes.VolumeUp, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Volume down", KeyCodes.VolumeDown, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Mute", KeyCodes.VolumeMute, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "Power", KeyCodes.Power, UiKey.None, false, "system"));
            // Guide, Live and the input keys of Fire TV Edition television remotes: the Fire OS launcher owns them
            r.Add(new ControllerKey(R, "Guide", KeyCodes.Guide, UiKey.None, false, "system: the Fire OS live guide"));
            r.Add(new ControllerKey(R, "Live (TV)", KeyCodes.Tv, UiKey.None, false, "system: the Live tab"));
            r.Add(new ControllerKey(R, "Input", KeyCodes.TvInput, UiKey.None, false, "system"));
            r.Add(new ControllerKey(R, "TV power", KeyCodes.TvPower, UiKey.None, false, "system"));
            // The app buttons (Prime Video, Netflix...) and the two custom buttons of the Pro remote send no key code to apps ("cannot be mapped"): nothing to list. The number pad of a
            // television manufacturer's remote types digits
            for (int i = 0; i <= 9; i++) r.Add(new ControllerKey(R, "Digit " + i, KeyCodes.Digit0 + i, UiKey.Char, true, "TV manufacturers' remotes: the digit is typed"));

            // ---- game controllers
            r.Add(new ControllerKey(G, "A", KeyCodes.ButtonA, UiKey.Ok, true, "its secondary event, DPAD_CENTER, never arrives because A is consumed"));
            r.Add(new ControllerKey(G, "B", KeyCodes.ButtonB, UiKey.Back, true, "same as Back"));
            r.Add(new ControllerKey(G, "X", KeyCodes.ButtonX, UiKey.None, false, "Amazon: does nothing"));
            r.Add(new ControllerKey(G, "Y", KeyCodes.ButtonY, UiKey.Info, true, "Amazon's \"menu\" on a game pad without a Menu key"));
            r.Add(new ControllerKey(G, "Menu", KeyCodes.Menu, UiKey.Info, true));
            r.Add(new ControllerKey(G, "Back", KeyCodes.Back, UiKey.Back, true));
            r.Add(new ControllerKey(G, "Start", KeyCodes.ButtonStart, UiKey.Info, true, "the menu button of most Bluetooth game pads"));
            r.Add(new ControllerKey(G, "Select", KeyCodes.ButtonSelect, UiKey.None, false));
            r.Add(new ControllerKey(G, "Mode (guide)", KeyCodes.ButtonMode, UiKey.None, false, "system"));
            r.Add(new ControllerKey(G, "Left shoulder (L1)", KeyCodes.ButtonL1, UiKey.Rewind, true, "10 seconds back"));
            r.Add(new ControllerKey(G, "Right shoulder (R1)", KeyCodes.ButtonR1, UiKey.Forward, true, "10 seconds forward"));
            r.Add(new ControllerKey(G, "Left stick press", KeyCodes.ButtonThumbL, UiKey.PlayPause, true));
            r.Add(new ControllerKey(G, "Right stick press", KeyCodes.ButtonThumbR, UiKey.PlayPause, true));
            r.Add(new ControllerKey(G, "Left trigger (L2)", KeyCodes.ButtonL2, UiKey.None, false, "an axis on the Fire controller (volume up, the system's); a digital key on other pads"));
            r.Add(new ControllerKey(G, "Right trigger (R2)", KeyCodes.ButtonR2, UiKey.None, false, "an axis on the Fire controller (volume down, the system's); a digital key on other pads"));
            // D-pad and left stick: the system turns the hat and stick axes into DPAD key events when the app does not handle the motion events (the app does not)
            r.Add(new ControllerKey(G, "D-pad / left stick up", KeyCodes.DpadUp, UiKey.Up, true, "secondary event of AXIS_HAT_Y / AXIS_Y"));
            r.Add(new ControllerKey(G, "D-pad / left stick down", KeyCodes.DpadDown, UiKey.Down, true, "secondary event of AXIS_HAT_Y / AXIS_Y"));
            r.Add(new ControllerKey(G, "D-pad / left stick left", KeyCodes.DpadLeft, UiKey.Left, true, "secondary event of AXIS_HAT_X / AXIS_X"));
            r.Add(new ControllerKey(G, "D-pad / left stick right", KeyCodes.DpadRight, UiKey.Right, true, "secondary event of AXIS_HAT_X / AXIS_X"));
            r.Add(new ControllerKey(G1, "Play/Pause", KeyCodes.MediaPlayPause, UiKey.PlayPause, true));
            r.Add(new ControllerKey(G1, "Rewind", KeyCodes.MediaRewind, UiKey.Rewind, true));
            r.Add(new ControllerKey(G1, "Fast Forward", KeyCodes.MediaFastForward, UiKey.Forward, true));

            // ---- a keyboard (a TV box, or a phone or tablet with one)
            r.Add(new ControllerKey(K, "Enter", KeyCodes.Enter, UiKey.Ok, true));
            r.Add(new ControllerKey(K, "Keypad Enter", KeyCodes.NumpadEnter, UiKey.Ok, true));
            r.Add(new ControllerKey(K, "Escape", KeyCodes.Escape, UiKey.Back, true));
            r.Add(new ControllerKey(K, "Space", KeyCodes.Space, UiKey.PlayPause, true));
            r.Add(new ControllerKey(K, "Page up", KeyCodes.PageUp, UiKey.ChannelUp, true));
            r.Add(new ControllerKey(K, "Page down", KeyCodes.PageDown, UiKey.ChannelDown, true));
            r.Add(new ControllerKey(K, "Backspace", KeyCodes.Del, UiKey.Backspace, true));
            return r;
        }

        /// <summary>The table as the README prints it: one row per button, grouped by controller.</summary>
        public static string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Controller | Button | Android key | P! does |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var row in _rows)
                sb.Append("| ").Append(row.Source).Append(" | ").Append(row.Button).Append(" | `").Append(AndroidKeys.Name(row.KeyCode)).Append("` | ")
                  .Append(row.Consumed ? row.Key.ToString() : "left to the system").AppendLine(" |");
            return sb.ToString();
        }
    }
}
