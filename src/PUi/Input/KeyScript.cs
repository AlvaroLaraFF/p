using System;
using System.Collections.Generic;
using System.Globalization;

namespace PUi.Input
{
    public sealed class ScriptStep
    {
        public KeyPress Key;
        public int WaitMs;
        public string Label;
    }

    /// <summary>
    /// Key script for unattended tests on the TV (there is no way to press the remote from the PC): a space-separated string with key names (ElmSharp / X11 ones:
    /// "Right", "Return", "XF86RaiseChannel", "XF86Back"...; or logical ones, the same on every platform: "Ok", "ChannelUp", "Back"... see KeyNames), "text:abc" (types those
    /// characters), "wait:2000" (ms) and "log:label".
    /// </summary>
    public static class KeyScript
    {
        public const int MaxSteps = 2000;

        public static List<ScriptStep> Parse(string script)
        {
            var steps = new List<ScriptStep>();
            if (string.IsNullOrWhiteSpace(script)) return steps;
            foreach (var tok in script.Split(new[] { ' ', '\t', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (steps.Count >= MaxSteps) break;
                if (tok.StartsWith("wait:", StringComparison.OrdinalIgnoreCase))
                {
                    int ms;
                    if (int.TryParse(tok.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) steps.Add(new ScriptStep { WaitMs = Math.Max(0, Math.Min(ms, 600000)) });
                }
                else if (tok.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (char c in tok.Substring(5)) steps.Add(new ScriptStep { Key = new KeyPress(UiKey.Char, c.ToString(), c) });
                }
                else if (tok.StartsWith("log:", StringComparison.OrdinalIgnoreCase)) steps.Add(new ScriptStep { Label = tok.Substring(4) });
                else
                {
                    var k = KeyNames.Parse(tok);
                    if (k.Key != UiKey.None) steps.Add(new ScriptStep { Key = k });
                }
            }
            return steps;
        }
    }
}
