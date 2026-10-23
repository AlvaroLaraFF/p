using System.Linq;
using PUi.Input;
using Xunit;

namespace PAndroid.Tests
{
    public class KeyTests
    {
        private static UiKey K(int code, int uni = 0, bool ctrl = false, bool alt = false) { return AndroidKeys.Map(code, uni, ctrl, alt).Key; }

        [Theory]
        [InlineData(KeyCodes.DpadUp, UiKey.Up)]
        [InlineData(KeyCodes.DpadDown, UiKey.Down)]
        [InlineData(KeyCodes.DpadLeft, UiKey.Left)]
        [InlineData(KeyCodes.DpadRight, UiKey.Right)]
        [InlineData(KeyCodes.DpadCenter, UiKey.Ok)]
        [InlineData(KeyCodes.Enter, UiKey.Ok)]
        [InlineData(KeyCodes.NumpadEnter, UiKey.Ok)]
        [InlineData(KeyCodes.ButtonA, UiKey.Ok)]
        [InlineData(KeyCodes.Back, UiKey.Back)]
        [InlineData(KeyCodes.Escape, UiKey.Back)]
        [InlineData(KeyCodes.ButtonB, UiKey.Back)]
        [InlineData(KeyCodes.Del, UiKey.Backspace)]
        public void NavigationKeysOfARemoteAKeyboardAndAGamePad(int code, UiKey expected) { Assert.Equal(expected, K(code)); }

        [Theory]
        [InlineData(KeyCodes.ChannelUp, UiKey.ChannelUp)]
        [InlineData(KeyCodes.PageUp, UiKey.ChannelUp)]
        [InlineData(KeyCodes.ChannelDown, UiKey.ChannelDown)]
        [InlineData(KeyCodes.PageDown, UiKey.ChannelDown)]
        public void ChannelKeysAreTheRemotesAndPageUpDownOfAKeyboard(int code, UiKey expected) { Assert.Equal(expected, K(code)); }

        [Theory]
        [InlineData(KeyCodes.MediaPlayPause, UiKey.PlayPause)]
        [InlineData(KeyCodes.Space, UiKey.PlayPause)]
        [InlineData(KeyCodes.HeadsetHook, UiKey.PlayPause)]
        [InlineData(KeyCodes.MediaPlay, UiKey.Play)]
        [InlineData(KeyCodes.MediaPause, UiKey.Pause)]
        [InlineData(KeyCodes.MediaStop, UiKey.Stop)]
        [InlineData(KeyCodes.MediaRewind, UiKey.Rewind)]
        [InlineData(KeyCodes.MediaPrevious, UiKey.Rewind)]
        [InlineData(KeyCodes.MediaSkipBackward, UiKey.Rewind)]
        [InlineData(KeyCodes.MediaFastForward, UiKey.Forward)]
        [InlineData(KeyCodes.MediaNext, UiKey.Forward)]
        [InlineData(KeyCodes.MediaSkipForward, UiKey.Forward)]
        public void MediaKeys(int code, UiKey expected) { Assert.Equal(expected, K(code)); }

        [Theory]
        [InlineData(KeyCodes.Info, UiKey.Info)]
        [InlineData(KeyCodes.Menu, UiKey.Info)]
        [InlineData(KeyCodes.ProgRed, UiKey.Red)]
        [InlineData(KeyCodes.ProgGreen, UiKey.Green)]
        [InlineData(KeyCodes.ProgYellow, UiKey.Yellow)]
        [InlineData(KeyCodes.ProgBlue, UiKey.Blue)]
        [InlineData(KeyCodes.F1, UiKey.Red)]
        [InlineData(KeyCodes.F2, UiKey.Green)]
        [InlineData(KeyCodes.F3, UiKey.Yellow)]
        [InlineData(KeyCodes.F4, UiKey.Blue)]
        public void InfoMenuAndColourKeys(int code, UiKey expected) { Assert.Equal(expected, K(code)); }

        [Theory]
        [InlineData(KeyCodes.Home)]
        [InlineData(KeyCodes.VolumeUp)]
        [InlineData(KeyCodes.VolumeDown)]
        [InlineData(KeyCodes.VolumeMute)]
        [InlineData(KeyCodes.Mute)]
        [InlineData(KeyCodes.Power)]
        [InlineData(KeyCodes.Assist)]
        [InlineData(KeyCodes.VoiceAssist)]
        [InlineData(KeyCodes.Search)]
        [InlineData(KeyCodes.Settings)]
        [InlineData(KeyCodes.AppSwitch)]
        [InlineData(KeyCodes.Guide)]
        [InlineData(KeyCodes.Camera)]
        public void TheSystemKeysAreNeverMappedEvenWhenTheyCarryACharacter(int code)
        {
            Assert.True(AndroidKeys.IsSystemKey(code));
            Assert.Equal(UiKey.None, K(code));
            Assert.Equal(UiKey.None, K(code, 'a'));
        }

        [Fact]
        public void NoMappedKeyIsASystemKey()
        {
            for (int code = 0; code < 400; code++)
                if (AndroidKeys.IsSystemKey(code)) Assert.Equal(UiKey.None, K(code, 'x'));
        }

        [Theory]
        [InlineData(KeyCodes.Digit0, '0')]
        [InlineData(KeyCodes.Digit9, '9')]
        [InlineData(KeyCodes.Numpad0, '0')]
        [InlineData(KeyCodes.Numpad9, '9')]
        [InlineData(29, 'a')]
        [InlineData(29, 'A')]
        [InlineData(76, '/')]
        [InlineData(56, '.')]
        [InlineData(69, '-')]
        [InlineData(74, ':')]
        [InlineData(0, 'ñ')]
        [InlineData(0, 'é')]
        public void TypedCharactersCarryTheCharacter(int code, int uni)
        {
            var kp = AndroidKeys.Map(code, uni);
            Assert.Equal(UiKey.Char, kp.Key);
            Assert.Equal((char)uni, kp.Ch);
        }

        [Fact]
        public void ADeadKeyAControlCharacterAndAShortcutTypeNothing()
        {
            Assert.Equal(UiKey.None, K(0, unchecked((int)0x80000000) | '`'));    // combining accent bit
            Assert.Equal(UiKey.None, K(0, 7));                                  // bell
            Assert.Equal(UiKey.None, K(0, 0x1F600));                            // outside the BMP
            Assert.Equal(UiKey.None, K(29, 'a', ctrl: true));
            Assert.Equal(UiKey.None, K(29, 'a', alt: true));
            Assert.Equal(UiKey.None, K(29, 0));
        }

        [Fact]
        public void DigitKeysOfARemoteAreTypedDigitsForTheInterface()
        {
            // a TV remote's 0-9 keys arrive as KEYCODE_0..9 with the digit as their character
            for (int i = 0; i <= 9; i++)
            {
                var kp = AndroidKeys.Map(KeyCodes.Digit0 + i, '0' + i);
                Assert.Equal(UiKey.Char, kp.Key); Assert.Equal((char)('0' + i), kp.Ch);
            }
        }

        [Fact]
        public void OnlyNavigationChannelSeekAndTypingKeysRepeat()
        {
            foreach (var k in new[] { UiKey.Up, UiKey.Down, UiKey.Left, UiKey.Right, UiKey.ChannelUp, UiKey.ChannelDown, UiKey.Rewind, UiKey.Forward, UiKey.Backspace, UiKey.Char })
                Assert.True(AndroidKeys.AllowsRepeat(k), k.ToString());
            foreach (var k in new[] { UiKey.Ok, UiKey.Back, UiKey.Play, UiKey.Pause, UiKey.PlayPause, UiKey.Stop, UiKey.Info, UiKey.Red, UiKey.Green, UiKey.Yellow, UiKey.Blue, UiKey.None })
                Assert.False(AndroidKeys.AllowsRepeat(k), k.ToString());
        }

        [Fact]
        public void KeyNamesAreReadableInTheLog()
        {
            Assert.Equal("KEYCODE_DPAD_UP", AndroidKeys.Name(KeyCodes.DpadUp));
            Assert.Equal("KEYCODE_999", AndroidKeys.Name(999));
            Assert.Equal("KEYCODE_BACK", AndroidKeys.Map(KeyCodes.Back).RawName);
        }

        // ------------------------------------------------------------------ the dispatcher

        [Fact]
        public void ADownIsConsumedAndRaisedAndItsUpIsConsumedWithoutRaising()
        {
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            Assert.True(d.OnKey(KeyCodes.Back, true, 0, 0, false, false, out p, out raise));
            Assert.True(raise); Assert.Equal(UiKey.Back, p.Key);
            Assert.True(d.OnKey(KeyCodes.Back, false, 0, 0, false, false, out p, out raise));
            Assert.False(raise);
            Assert.False(d.OnKey(KeyCodes.Back, false, 0, 0, false, false, out p, out raise));        // the second up is nobody's
        }

        [Fact]
        public void AnUnmappedKeyIsNotConsumedNeitherItsRelease()
        {
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            Assert.False(d.OnKey(KeyCodes.VolumeUp, true, 0, 0, false, false, out p, out raise)); Assert.False(raise);
            Assert.False(d.OnKey(KeyCodes.VolumeUp, false, 0, 0, false, false, out p, out raise));
            Assert.False(d.OnKey(KeyCodes.Home, true, 0, 0, false, false, out p, out raise));
        }

        [Fact]
        public void HeldArrowsRepeatButAHeldOkOrBackActsOnce()
        {
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            d.OnKey(KeyCodes.DpadRight, true, 0, 0, false, false, out p, out raise);
            for (int i = 1; i < 4; i++) { Assert.True(d.OnKey(KeyCodes.DpadRight, true, i, 0, false, false, out p, out raise)); Assert.True(raise); }
            d.OnKey(KeyCodes.Back, true, 0, 0, false, false, out p, out raise); Assert.True(raise);
            for (int i = 1; i < 4; i++) { Assert.True(d.OnKey(KeyCodes.Back, true, i, 0, false, false, out p, out raise)); Assert.False(raise); }
            d.OnKey(KeyCodes.DpadCenter, true, 0, 0, false, false, out p, out raise); Assert.True(raise);
            Assert.True(d.OnKey(KeyCodes.DpadCenter, true, 2, 0, false, false, out p, out raise)); Assert.False(raise);
        }

        [Fact]
        public void ResetForgetsTheKeysHeldDown()
        {
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            d.OnKey(KeyCodes.Back, true, 0, 0, false, false, out p, out raise);
            d.Reset();
            Assert.False(d.OnKey(KeyCodes.Back, false, 0, 0, false, false, out p, out raise));
        }

        [Fact]
        public void TypingWithAKeyboardGoesThroughAsCharacters()
        {
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            Assert.True(d.OnKey(29, true, 0, 'a', false, false, out p, out raise));
            Assert.True(raise); Assert.Equal(UiKey.Char, p.Key); Assert.Equal('a', p.Ch);
        }
    }
}
