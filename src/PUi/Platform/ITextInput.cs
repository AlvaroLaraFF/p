using System;

namespace PUi.Platform
{
    /// <summary>What the action key of the system keyboard says.</summary>
    public enum TextInputAction { Search, Done }

    /// <summary>What PUi asks of the system keyboard when it opens it.</summary>
    public sealed class TextInputRequest
    {
        /// <summary>The text of the field now (the keyboard starts with it and the cursor at its end).</summary>
        public string Text = "";
        /// <summary>The longest text the field takes; the platform may cut what is typed beyond it.</summary>
        public int MaxLength = 60;
        /// <summary>The label of the keyboard's action key. Single line in every case: the key never inserts a line break, it raises Submitted.</summary>
        public TextInputAction Action = TextInputAction.Search;
    }

    /// <summary>
    /// The system keyboard (IME), for the platforms where the user types on a screen they touch. PUi draws the text field itself and keeps the text; this is how it asks the
    /// platform for a keyboard and receives what is typed. On the Samsung TV there is none (IPlatform.TextInput is null) and PUi's own keyboard, driven by the remote, is used.
    ///
    /// All members are called on the interface thread and all events are raised on it.
    ///
    /// The conversation:
    ///  - Show: the keyboard opens for a field that starts with request.Text. The platform keeps its own copy of the text (an invisible text field, an InputConnection) for the
    ///    keyboard to edit. It raises TextChanged with the WHOLE new text after every edit by the user (typing, deleting, a suggestion, a paste), and nothing for the text it was given.
    ///  - SetText: PUi changed the text itself (a recent search was tapped, the field was cleared): the platform replaces its copy and the keyboard follows. It raises nothing.
    ///  - Submitted: the user pressed the action key. The keyboard stays as it is; PUi asks for Hide when it wants it closed.
    ///  - Dismissed: the SYSTEM closed the keyboard (the user hid it, Back was used to close it, the app lost focus). It is not raised after Hide, nor after Submitted. The Back that
    ///    closed the keyboard is NOT also delivered as UiKey.Back.
    ///  - Hide: PUi closes the keyboard. Harmless if it is not shown.
    ///  - While the keyboard is shown the platform does not deliver typed characters as UiKey.Char / UiKey.Backspace as well: the text comes by TextChanged only.
    /// The keyboard may cover the lower part of the screen; PUi does not move for it (the search field is at the top).
    /// </summary>
    public interface ITextInput
    {
        /// <summary>The keyboard is up.</summary>
        bool IsShown { get; }
        void Show(TextInputRequest request);
        void SetText(string text);
        void Hide();

        event Action<string> TextChanged;
        event Action Submitted;
        event Action Dismissed;
    }
}
