using System;
using System.Collections.Generic;

namespace PUi.Input
{
    /// <summary>
    /// Key names, a pure table with no platform code. Two vocabularies map to the logical keys (UiKey):
    ///  - X11 names, which is what ElmSharp gives on the TV ("Return", "XF86RaiseChannel", "KP_3"...): FromName. The names of Channel +/- and of the media keys are
    ///    INFERRED and several variants are accepted; on the TV every pressed key is recorded in the log so they can be adjusted. Which of them the TV needs registered
    ///    (KeyGrabEx) is the TV shell's business: PTizen's TizenKeys.
    ///  - logical names, the UiKey names themselves ("Ok", "ChannelUp", "Back"...): FromLogical. They are the same on every platform, so a key script written with them
    ///    works on any shell.
    /// Parse accepts both (key scripts).
    /// </summary>
    public static class KeyNames
    {
        private static readonly Dictionary<string, UiKey> Logical = BuildLogical();

        private static Dictionary<string, UiKey> BuildLogical()
        {
            var d = new Dictionary<string, UiKey>(StringComparer.OrdinalIgnoreCase);
            foreach (UiKey k in Enum.GetValues(typeof(UiKey)))
                if (k != UiKey.None && k != UiKey.Char) d[k.ToString()] = k;
            return d;
        }

        /// <summary>The logical name of a key ("ChannelUp"), the same on every platform; null for None and for Char (a typed character has no name of its own).</summary>
        public static string LogicalName(UiKey key)
        {
            return key == UiKey.None || key == UiKey.Char ? null : key.ToString();
        }

        /// <summary>A key by its logical name (case does not matter); None if it is not one.</summary>
        public static KeyPress FromLogical(string name)
        {
            UiKey k;
            if (!string.IsNullOrEmpty(name) && Logical.TryGetValue(name, out k)) return new KeyPress(k, name);
            return new KeyPress(UiKey.None, name);
        }

        /// <summary>
        /// A key by any of its names: the X11 ones first (so a name that already meant something keeps meaning it) and then the logical ones. For key scripts.
        /// </summary>
        public static KeyPress Parse(string name)
        {
            var k = FromName(name);
            return k.Key != UiKey.None ? k : FromLogical(name);
        }

        /// <summary>A key by its X11 name, as ElmSharp reports it on the TV (and as the desktop test bench simulates it).</summary>
        public static KeyPress FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return new KeyPress(UiKey.None, name);
            switch (name)
            {
                case "Up": case "KP_Up": return new KeyPress(UiKey.Up, name);
                case "Down": case "KP_Down": return new KeyPress(UiKey.Down, name);
                case "Left": case "KP_Left": return new KeyPress(UiKey.Left, name);
                case "Right": case "KP_Right": return new KeyPress(UiKey.Right, name);
                case "Return": case "KP_Enter": case "Select": case "XF86Select": case "XF86Ok": return new KeyPress(UiKey.Ok, name);
                case "XF86Back": case "Escape": case "XF86Exit": case "XF86Stop_Back": return new KeyPress(UiKey.Back, name);
                case "BackSpace": return new KeyPress(UiKey.Backspace, name);
                case "XF86RaiseChannel": case "XF86ChannelUp": case "Prior": case "Page_Up": case "KP_Prior": return new KeyPress(UiKey.ChannelUp, name);
                case "XF86LowerChannel": case "XF86ChannelDown": case "Next": case "Page_Down": case "KP_Next": return new KeyPress(UiKey.ChannelDown, name);
                case "XF86AudioPlay": return new KeyPress(UiKey.Play, name);
                case "XF86AudioPause": return new KeyPress(UiKey.Pause, name);
                case "XF86PlayBack": case "XF86AudioPlayPause": case "space": return new KeyPress(UiKey.PlayPause, name);
                case "XF86AudioStop": return new KeyPress(UiKey.Stop, name);
                case "XF86AudioRewind": case "XF86AudioPrev": return new KeyPress(UiKey.Rewind, name);
                case "XF86AudioForward": case "XF86AudioNext": return new KeyPress(UiKey.Forward, name);
                case "XF86Info": case "Menu": case "XF86Menu": return new KeyPress(UiKey.Info, name);
                case "XF86Red": case "F1": return new KeyPress(UiKey.Red, name);
                case "XF86Green": case "F2": return new KeyPress(UiKey.Green, name);
                case "XF86Yellow": case "F3": return new KeyPress(UiKey.Yellow, name);
                case "XF86Blue": case "F4": return new KeyPress(UiKey.Blue, name);
                case "minus": return new KeyPress(UiKey.Char, name, '-');
                case "period": case "KP_Decimal": return new KeyPress(UiKey.Char, name, '.');
                case "slash": case "KP_Divide": return new KeyPress(UiKey.Char, name, '/');
                case "colon": return new KeyPress(UiKey.Char, name, ':');
                case "underscore": return new KeyPress(UiKey.Char, name, '_');
                case "equal": return new KeyPress(UiKey.Char, name, '=');
                case "question": return new KeyPress(UiKey.Char, name, '?');
                case "ampersand": return new KeyPress(UiKey.Char, name, '&');
                case "percent": return new KeyPress(UiKey.Char, name, '%');
            }
            if (name.Length == 1 && (char.IsLetterOrDigit(name[0]))) return new KeyPress(UiKey.Char, name, name[0]);
            if (name.StartsWith("KP_", StringComparison.Ordinal) && name.Length == 4 && char.IsDigit(name[3])) return new KeyPress(UiKey.Char, name, name[3]);
            return new KeyPress(UiKey.None, name);
        }
    }
}
