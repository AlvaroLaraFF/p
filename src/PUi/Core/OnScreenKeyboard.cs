using System;
using System.Collections.Generic;
using System.Text;
using PUi.Input;

namespace PUi.Core
{
    public enum KbAction { Char, Space, Backspace, Clear, Submit }

    public sealed class KbKey
    {
        public string Label = "";
        public KbAction Action;
        public char Ch;
        public int Col, Span = 1;
    }

    /// <summary>
    /// On-screen keyboard for the search, usable with the remote alone (arrows + OK): digits, letters (with the ñ), space, delete, clear and search.
    /// Pure logic (tested without drawing); the search screen draws it in the design's style.
    /// </summary>
    public sealed class OnScreenKeyboard
    {
        public const int Cells = 10;
        private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjklñ", "zxcvbnm-.&" };
        public readonly List<List<KbKey>> Layout = new List<List<KbKey>>();
        public int Row, Cell;
        public readonly StringBuilder Text = new StringBuilder();
        public int MaxLength = 60;
        public bool SubmitRequested;

        public OnScreenKeyboard()
        {
            foreach (var r in Rows)
            {
                var row = new List<KbKey>();
                for (int i = 0; i < r.Length; i++) row.Add(new KbKey { Label = r[i].ToString(), Action = KbAction.Char, Ch = r[i], Col = i });
                Layout.Add(row);
            }
            Layout.Add(new List<KbKey>
            {
                new KbKey { Action = KbAction.Space, Col = 0, Span = 4 },
                new KbKey { Action = KbAction.Backspace, Col = 4, Span = 2 },
                new KbKey { Action = KbAction.Clear, Col = 6, Span = 2 },
                new KbKey { Action = KbAction.Submit, Col = 8, Span = 2 },
            });
            Row = 1; Cell = 0;
        }

        public KbKey Focused { get { return KeyAtCell(Row, Cell); } }

        public KbKey KeyAtCell(int row, int cell)
        {
            foreach (var k in Layout[row]) if (cell >= k.Col && cell < k.Col + k.Span) return k;
            return Layout[row][Layout[row].Count - 1];
        }

        public void SetText(string s) { Text.Clear(); Text.Append((s ?? "").Length > MaxLength ? s.Substring(0, MaxLength) : s ?? ""); }
        public void Type(char c) { if (Text.Length < MaxLength) Text.Append(c); }
        public void Backspace() { if (Text.Length > 0) Text.Length--; }
        public void Clear() { Text.Clear(); }

        /// <summary>Moves the cursor; returns false if it goes off an edge.</summary>
        public bool Move(UiKey dir)
        {
            var cur = Focused;
            switch (dir)
            {
                case UiKey.Left: { int i = Layout[Row].IndexOf(cur); if (i <= 0) return false; Cell = Layout[Row][i - 1].Col; return true; }
                case UiKey.Right: { int i = Layout[Row].IndexOf(cur); if (i >= Layout[Row].Count - 1) return false; Cell = Layout[Row][i + 1].Col; return true; }
                case UiKey.Up: if (Row == 0) return false; Row--; return true;
                case UiKey.Down: if (Row >= Layout.Count - 1) return false; Row++; return true;
            }
            return false;
        }

        /// <summary>OK on the focused key. Returns the action performed.</summary>
        public KbAction Press()
        {
            var k = Focused;
            switch (k.Action)
            {
                case KbAction.Char: Type(k.Ch); break;
                case KbAction.Space: if (Text.Length > 0 && Text[Text.Length - 1] != ' ') Type(' '); break;
                case KbAction.Backspace: Backspace(); break;
                case KbAction.Clear: Clear(); break;
                case KbAction.Submit: SubmitRequested = true; break;
            }
            return k.Action;
        }
    }
}
