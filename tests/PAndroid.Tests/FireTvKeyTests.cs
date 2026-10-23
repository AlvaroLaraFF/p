using System.Collections.Generic;
using System.Linq;
using AceEngine.App;
using PUi.Core;
using PUi.Input;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>
    /// The keys of the Fire TV controllers (FireTvKeyTable: the Alexa Voice Remote, its Lite and Pro versions, the remotes of Fire TV Edition televisions, Amazon's game controllers and generic
    /// game pads), checked against the key table the activity uses and against what the interface's player does with each one.
    /// </summary>
    public class FireTvKeyTests
    {
        public static IEnumerable<object[]> AllRows()
        {
            for (int i = 0; i < FireTvKeyTable.Rows.Count; i++) yield return new object[] { i };
        }

        private static int Unicode(ControllerKey row)
        {
            // a digit key of a remote arrives with the digit as its character
            return row.Key == UiKey.Char ? '0' + (row.KeyCode - KeyCodes.Digit0) : 0;
        }

        [Theory]
        [MemberData(nameof(AllRows))]
        public void EveryButtonOfTheTableIsMappedToItsLogicalKey(int index)
        {
            var row = FireTvKeyTable.Rows[index];
            var kp = AndroidKeys.Map(row.KeyCode, Unicode(row));
            Assert.True(row.Key == kp.Key, row.Source + " / " + row.Button + " (" + AndroidKeys.Name(row.KeyCode) + "): expected " + row.Key + " but the table says " + kp.Key);
        }

        [Theory]
        [MemberData(nameof(AllRows))]
        public void EveryButtonIsConsumedOrLeftToTheSystemAsTheTableSays(int index)
        {
            var row = FireTvKeyTable.Rows[index];
            var d = new KeyDispatcher();
            KeyPress p; bool raise;
            bool consumed = d.OnKey(row.KeyCode, true, 0, Unicode(row), false, false, out p, out raise);
            Assert.True(row.Consumed == consumed, row.Source + " / " + row.Button + ": consumed=" + consumed);
            Assert.Equal(row.Consumed, raise);
            // and its release is consumed exactly when its press was (otherwise Back would run its default action on the release)
            bool upConsumed = d.OnKey(row.KeyCode, false, 0, 0, false, false, out p, out raise);
            Assert.Equal(row.Consumed, upConsumed);
            Assert.False(raise);
        }

        [Fact]
        public void TheSystemsKeysAreNeverConsumedWhicheverControllerSendsThem()
        {
            foreach (var row in FireTvKeyTable.Rows.Where(r => r.Key == UiKey.None))
            {
                Assert.False(row.Consumed, row.Button);
                // the keys the system owns are not consumed even with a character attached to the key event
                var d = new KeyDispatcher(); KeyPress p; bool raise;
                if (AndroidKeys.IsSystemKey(row.KeyCode)) Assert.False(d.OnKey(row.KeyCode, true, 0, 'a', false, false, out p, out raise), row.Button);
                else Assert.False(d.OnKey(row.KeyCode, true, 0, 0, false, false, out p, out raise), row.Button);
            }
            // Home, the volume and the assistant (Alexa) in particular
            foreach (int code in new[] { KeyCodes.Home, KeyCodes.VolumeUp, KeyCodes.VolumeDown, KeyCodes.VolumeMute, KeyCodes.Mute, KeyCodes.VoiceAssist, KeyCodes.Assist, KeyCodes.Search, KeyCodes.Power })
                Assert.True(AndroidKeys.IsSystemKey(code), AndroidKeys.Name(code));
        }

        [Fact]
        public void TheTableHasNoDuplicatesAndCoversEveryControllerKind()
        {
            var seen = new HashSet<string>();
            foreach (var row in FireTvKeyTable.Rows) Assert.True(seen.Add(row.Source + ":" + row.KeyCode + ":" + row.Button), "duplicate row " + row.Button);
            foreach (ControllerKind kind in new[] { ControllerKind.FireTvRemote, ControllerKind.GamePad, ControllerKind.FireGameController1stGen, ControllerKind.Keyboard })
                Assert.Contains(FireTvKeyTable.Rows, r => r.Source == kind);
        }

        [Fact]
        public void EveryKeyThatAFireTvRemoteDeliversAndIsDocumentedIsInTheTable()
        {
            // Amazon, "Remote Control Input", input event reference: Back, Menu, Select, the four arrows, Play/Pause, Rewind, Fast Forward, Channel up and down
            var remote = FireTvKeyTable.Rows.Where(r => r.Source == ControllerKind.FireTvRemote).Select(r => r.KeyCode).ToList();
            foreach (int code in new[] { KeyCodes.Back, KeyCodes.Menu, KeyCodes.DpadCenter, KeyCodes.DpadUp, KeyCodes.DpadDown, KeyCodes.DpadLeft, KeyCodes.DpadRight,
                                         KeyCodes.MediaPlayPause, KeyCodes.MediaRewind, KeyCodes.MediaFastForward, KeyCodes.ChannelUp, KeyCodes.ChannelDown })
                Assert.Contains(code, remote);
        }

        // ------------------------------------------------------------------ what the interface's player does with each of them

        private static PlayerController Playing()
        {
            var c = new PlayerController { CanChangeChannel = true };
            c.OnStatus(new PlaybackStatus { Phase = PlayPhase.Playing, IsLive = true, TimeshiftWindowMs = 60000 }, 0);
            c.Tick(0.0);
            return c;
        }

        private static UiKey Of(int code) { return AndroidKeys.Map(code).Key; }

        [Fact]
        public void MenuShowsTheInformationPanelAndHidesItAgain()
        {
            var c = Playing();
            Assert.False(c.PanelWanted);
            Assert.Equal(PlayerAction.None, c.OnKey(Of(KeyCodes.Menu), 1.0));
            c.Tick(1.0);
            Assert.True(c.PanelWanted);
            Assert.Equal(PlayerAction.None, c.OnKey(Of(KeyCodes.Menu), 2.0));
            c.Tick(2.0);
            Assert.False(c.PanelWanted);
        }

        [Fact]
        public void RewindAndFastForwardOfTheRemoteSeekTenSecondsEachWay()
        {
            Assert.Equal(PlayerAction.SeekBack, Playing().OnKey(Of(KeyCodes.MediaRewind), 1.0));
            Assert.Equal(PlayerAction.SeekForward, Playing().OnKey(Of(KeyCodes.MediaFastForward), 1.0));
            // the game pad's shoulder buttons do the same
            Assert.Equal(PlayerAction.SeekBack, Playing().OnKey(Of(KeyCodes.ButtonL1), 1.0));
            Assert.Equal(PlayerAction.SeekForward, Playing().OnKey(Of(KeyCodes.ButtonR1), 1.0));
        }

        [Fact]
        public void PlayPauseTogglesThePauseFromTheRemoteTheGamePadsSticksAndAFirstGenerationController()
        {
            foreach (int code in new[] { KeyCodes.MediaPlayPause, KeyCodes.ButtonThumbL, KeyCodes.ButtonThumbR })
                Assert.Equal(PlayerAction.TogglePause, Playing().OnKey(Of(code), 1.0));
            // while paused it toggles back
            var c = Playing();
            c.OnStatus(new PlaybackStatus { Phase = PlayPhase.Paused, IsLive = true }, 2.0);
            Assert.Equal(PlayerAction.TogglePause, c.OnKey(Of(KeyCodes.MediaPlayPause), 3.0));
        }

        [Fact]
        public void BackAndTheGamePadsBLeaveThePlayer()
        {
            Assert.Equal(PlayerAction.Exit, Playing().OnKey(Of(KeyCodes.Back), 1.0));
            Assert.Equal(PlayerAction.Exit, Playing().OnKey(Of(KeyCodes.ButtonB), 1.0));
            Assert.Equal(PlayerAction.Exit, Playing().OnKey(Of(KeyCodes.Escape), 1.0));
        }

        [Fact]
        public void SelectAndTheGamePadsAWithThePanelHiddenBringUpThePanel()
        {
            var c = Playing();
            Assert.Equal(PlayerAction.None, c.OnKey(Of(KeyCodes.DpadCenter), 1.0));
            c.Tick(1.0);
            Assert.True(c.PanelWanted);
            // and with it shown, Select acts on the focused button: pause, by default
            Assert.Equal(PlayerAction.TogglePause, c.OnKey(Of(KeyCodes.ButtonA), 1.5));
        }

        [Fact]
        public void TheArrowsJumpTenSecondsWithThePanelHiddenAndMoveTheFocusWithItShown()
        {
            Assert.Equal(PlayerAction.SeekBack, Playing().OnKey(Of(KeyCodes.DpadLeft), 1.0));
            Assert.Equal(PlayerAction.SeekForward, Playing().OnKey(Of(KeyCodes.DpadRight), 1.0));
            var c = Playing();
            c.OnKey(Of(KeyCodes.Menu), 1.0); c.Tick(1.0);
            Assert.Equal(PlayerAction.None, c.OnKey(Of(KeyCodes.DpadRight), 1.2));
            Assert.Equal(PanelButton.Forward10, c.Focus);
        }

        [Fact]
        public void ChannelKeysChangeChannelWhereTheRemoteHasThem()
        {
            Assert.Equal(PlayerAction.NextChannel, Playing().OnKey(Of(KeyCodes.ChannelUp), 1.0));
            Assert.Equal(PlayerAction.PrevChannel, Playing().OnKey(Of(KeyCodes.ChannelDown), 1.0));
        }

        [Fact]
        public void HeldRewindAndFastForwardKeepSeekingButAHeldPlayPauseOrMenuActsOnce()
        {
            var d = new KeyDispatcher(); KeyPress p; bool raise;
            d.OnKey(KeyCodes.MediaFastForward, true, 0, 0, false, false, out p, out raise);
            for (int i = 1; i < 4; i++) { Assert.True(d.OnKey(KeyCodes.MediaFastForward, true, i, 0, false, false, out p, out raise)); Assert.True(raise); Assert.Equal(UiKey.Forward, p.Key); }
            d.OnKey(KeyCodes.MediaPlayPause, true, 0, 0, false, false, out p, out raise); Assert.True(raise);
            for (int i = 1; i < 4; i++) { Assert.True(d.OnKey(KeyCodes.MediaPlayPause, true, i, 0, false, false, out p, out raise)); Assert.False(raise); }
            d.OnKey(KeyCodes.Menu, true, 0, 0, false, false, out p, out raise); Assert.True(raise);
            Assert.True(d.OnKey(KeyCodes.Menu, true, 1, 0, false, false, out p, out raise)); Assert.False(raise);
        }

        [Fact]
        public void TheMarkdownOfTheTableNamesEveryKeyCode()
        {
            string md = FireTvKeyTable.ToMarkdown();
            foreach (var row in FireTvKeyTable.Rows) Assert.Contains("`" + AndroidKeys.Name(row.KeyCode) + "`", md);
        }
    }
}
