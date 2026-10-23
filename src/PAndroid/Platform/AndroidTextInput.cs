using System;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using AceEngine.App;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>
    /// The system keyboard for PUi's text fields (ITextInput). PUi draws the field and keeps its text; this keeps a copy in an Editable that the keyboard edits through an InputConnection on the
    /// drawing view, and raises TextChanged with the whole text after every edit (typing, deleting, composing, a suggestion, a paste).
    ///  - The view is a text editor ONLY while the keyboard is shown (see AndroidSkiaView.OnCheckIsTextEditor): otherwise the system would send every key of a remote through the keyboard first.
    ///  - While it is shown nothing is delivered as UiKey.Char / Backspace: the platform sends those keys here instead (TypeChar, Backspace), for a hardware keyboard.
    ///  - Dismissed is raised only when the SYSTEM closes it: the Back that closes the keyboard (the view sees it before the keyboard does, OnKeyPreIme, and it is not also UiKey.Back), the keyboard
    ///    hiding itself (the IME insets going away, Android 11 and later), or the app losing focus. Never after Hide or Submitted.
    /// All members run on the main thread, which is the interface thread.
    /// </summary>
    internal sealed class AndroidTextInput : ITextInput
    {
        private const string Tag = "ime";
        private readonly Activity _activity;
        private readonly AndroidSkiaView _view;
        private readonly SpannableStringBuilder _text = new SpannableStringBuilder();
        private bool _shown;
        private int _max = 60;
        private TextInputAction _action = TextInputAction.Search;
        private string _lastRaised = "";

        public AndroidTextInput(Activity activity, AndroidSkiaView view)
        {
            _activity = activity; _view = view;
        }

        public bool IsShown { get { return _shown; } }

        public event Action<string> TextChanged;
        public event Action Submitted;
        public event Action Dismissed;

        // ================================================================ ITextInput
        public void Show(TextInputRequest request)
        {
            if (request == null) request = new TextInputRequest();
            _max = Math.Max(1, request.MaxLength); _action = request.Action;
            SetBuffer(Clip(request.Text ?? ""));
            _shown = true;
            _view.Post(() =>
            {
                if (!_shown) return;
                try
                {
                    _view.RequestFocus();
                    var imm = Imm();
                    if (imm == null) return;
                    imm.RestartInput(_view);                        // the view is a text editor now: a new input session, starting with the text above
                    imm.ShowSoftInput(_view, ShowFlags.Implicit);
                }
                catch (Exception ex) { UdpLog.Warn(Tag, "Show: " + ex.Message); }
            });
            UdpLog.Info(Tag, "keyboard requested (" + _action + ", max " + _max + ")");
        }

        public void SetText(string text)
        {
            SetBuffer(Clip(text ?? ""));
            if (_shown) { try { var imm = Imm(); if (imm != null) imm.RestartInput(_view); } catch (Exception ex) { UdpLog.Warn(Tag, "SetText: " + ex.Message); } }
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            CloseKeyboard();
        }

        // ================================================================ the system closes it
        /// <summary>The system closed the keyboard (or Back was used to close it, or the app lost focus). Raises Dismissed once.</summary>
        public void SystemDismissed(string why)
        {
            if (!_shown) return;
            _shown = false;
            UdpLog.Info(Tag, "keyboard dismissed by the system (" + why + ")");
            CloseKeyboard();
            var h = Dismissed;
            if (h != null) { try { h(); } catch (Exception ex) { UdpLog.Warn(Tag, "Dismissed handler: " + ex.Message); } }
        }

        private void CloseKeyboard()
        {
            try
            {
                var imm = Imm();
                if (imm != null) { imm.HideSoftInputFromWindow(_view.WindowToken, HideSoftInputFlags.None); imm.RestartInput(_view); }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "closing the keyboard: " + ex.Message); }
        }

        private InputMethodManager Imm() { return (InputMethodManager)_activity.GetSystemService(Context.InputMethodService); }

        // ================================================================ hardware keyboard while the keyboard is shown
        public void TypeChar(char c)
        {
            if (!_shown) return;
            if (_text.Length() >= _max) return;
            _text.Append(c.ToString());
            Selection.SetSelection(_text, _text.Length());
            OnEdited();
        }

        public void Backspace()
        {
            if (!_shown || _text.Length() == 0) return;
            _text.Delete(_text.Length() - 1, _text.Length());
            Selection.SetSelection(_text, _text.Length());
            OnEdited();
        }

        public void Submit()
        {
            if (!_shown) return;
            var h = Submitted;
            if (h != null) { try { h(); } catch (Exception ex) { UdpLog.Warn(Tag, "Submitted handler: " + ex.Message); } }
        }

        // ================================================================ the InputConnection the keyboard edits through
        /// <summary>Called by the view's OnCreateInputConnection while the keyboard is shown.</summary>
        public IInputConnection CreateConnection(EditorInfo outAttrs)
        {
            // one line of plain text (the action key never inserts a break), no extracted full-screen editor, the action key says Search or Done
            outAttrs.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
            outAttrs.ImeOptions = ImeFlags.NoExtractUi | ImeFlags.NoFullscreen | (ImeFlags)(int)(_action == TextInputAction.Done ? ImeAction.Done : ImeAction.Search);
            outAttrs.InitialSelStart = outAttrs.InitialSelEnd = _text.Length();
            return new Connection(this, _view);
        }

        private void OnEdited()
        {
            if (_text.Length() > _max)
            {
                _text.Delete(_max, _text.Length());
                BaseInputConnection.RemoveComposingSpans(_text);
                Selection.SetSelection(_text, _text.Length());
            }
            string s = _text.ToString();
            if (s == _lastRaised) return;
            _lastRaised = s;
            var h = TextChanged;
            if (h != null) { try { h(s); } catch (Exception ex) { UdpLog.Warn(Tag, "TextChanged handler: " + ex.Message); } }
        }

        private void SetBuffer(string text)
        {
            _text.Clear();
            _text.Append(text);
            BaseInputConnection.RemoveComposingSpans(_text);
            Selection.SetSelection(_text, _text.Length());
            _lastRaised = text;                  // what PUi itself put there is not an edit by the user
        }

        private string Clip(string s) { return s.Length <= _max ? s : s.Substring(0, _max); }

        private sealed class Connection : BaseInputConnection
        {
            private readonly AndroidTextInput _o;
            public Connection(AndroidTextInput owner, View target) : base(target, true) { _o = owner; }

            public override IEditable Editable { get { return _o._text; } }

            public override bool CommitText(Java.Lang.ICharSequence text, int newCursorPosition) { bool r = base.CommitText(text, newCursorPosition); _o.OnEdited(); return r; }
            public override bool SetComposingText(Java.Lang.ICharSequence text, int newCursorPosition) { bool r = base.SetComposingText(text, newCursorPosition); _o.OnEdited(); return r; }
            public override bool DeleteSurroundingText(int beforeLength, int afterLength) { bool r = base.DeleteSurroundingText(beforeLength, afterLength); _o.OnEdited(); return r; }
            public override bool DeleteSurroundingTextInCodePoints(int beforeLength, int afterLength) { bool r = base.DeleteSurroundingTextInCodePoints(beforeLength, afterLength); _o.OnEdited(); return r; }
            public override bool FinishComposingText() { bool r = base.FinishComposingText(); _o.OnEdited(); return r; }

            public override bool PerformEditorAction(ImeAction actionCode)
            {
                _o.Submit();
                return true;
            }
        }
    }
}
