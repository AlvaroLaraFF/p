using System;
using PUi.Platform;

namespace PUi.Testing
{
    /// <summary>
    /// A system keyboard that a test drives (the platform side of ITextInput): it records what PUi asks and lets the test be the user (type, delete, press the action key, dismiss).
    /// It follows the contract: TextChanged carries the whole text after a user edit and is not raised for what PUi sets; Dismissed is not raised after Hide or Submitted.
    /// </summary>
    public sealed class FakeTextInput : ITextInput
    {
        public bool IsShown { get; private set; }
        /// <summary>The text the keyboard holds now.</summary>
        public string Text = "";
        public TextInputRequest LastRequest;
        public int ShowCount, HideCount;

        public event Action<string> TextChanged;
        public event Action Submitted;
        public event Action Dismissed;

        // ---- what PUi calls
        public void Show(TextInputRequest request) { IsShown = true; LastRequest = request; Text = request.Text ?? ""; ShowCount++; }
        public void SetText(string text) { Text = text ?? ""; }
        public void Hide() { IsShown = false; HideCount++; }

        // ---- what the user does
        public void Type(string typed)
        {
            if (!IsShown) return;
            Text += typed;
            if (LastRequest != null && Text.Length > LastRequest.MaxLength) Text = Text.Substring(0, LastRequest.MaxLength);
            var h = TextChanged; if (h != null) h(Text);
        }

        public void Backspace()
        {
            if (!IsShown || Text.Length == 0) return;
            Text = Text.Substring(0, Text.Length - 1);
            var h = TextChanged; if (h != null) h(Text);
        }

        /// <summary>The action key (Search / Done).</summary>
        public void PressAction() { if (!IsShown) return; var h = Submitted; if (h != null) h(); }

        /// <summary>The system closes the keyboard (the user hid it, or Back was used to close it).</summary>
        public void Dismiss() { if (!IsShown) return; IsShown = false; var h = Dismissed; if (h != null) h(); }
    }
}
