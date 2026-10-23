namespace PUi.Input
{
    public struct KeyPress
    {
        public UiKey Key;
        /// <summary>Typed character (only with Key = Char).</summary>
        public char Ch;
        public string RawName;

        public KeyPress(UiKey key, string raw = null, char ch = '\0') { Key = key; Ch = ch; RawName = raw; }
        public override string ToString() { return Key + (Key == UiKey.Char ? " '" + Ch + "'" : "") + " (" + RawName + ")"; }
    }
}
