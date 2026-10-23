using System.Collections.Generic;
using System.Linq;
using PUi.Core;
using PUi.Input;
using Xunit;

namespace PUi.Tests
{
    public class KeyNamesTests
    {
        [Theory]
        [InlineData("Up", UiKey.Up)] [InlineData("KP_Up", UiKey.Up)] [InlineData("Down", UiKey.Down)] [InlineData("KP_Down", UiKey.Down)]
        [InlineData("Left", UiKey.Left)] [InlineData("KP_Left", UiKey.Left)] [InlineData("Right", UiKey.Right)] [InlineData("KP_Right", UiKey.Right)]
        [InlineData("Return", UiKey.Ok)] [InlineData("KP_Enter", UiKey.Ok)] [InlineData("Select", UiKey.Ok)] [InlineData("XF86Select", UiKey.Ok)] [InlineData("XF86Ok", UiKey.Ok)]
        [InlineData("XF86Back", UiKey.Back)] [InlineData("Escape", UiKey.Back)] [InlineData("XF86Exit", UiKey.Back)] [InlineData("XF86Stop_Back", UiKey.Back)]
        [InlineData("BackSpace", UiKey.Backspace)]
        [InlineData("XF86RaiseChannel", UiKey.ChannelUp)] [InlineData("XF86ChannelUp", UiKey.ChannelUp)] [InlineData("Prior", UiKey.ChannelUp)] [InlineData("Page_Up", UiKey.ChannelUp)] [InlineData("KP_Prior", UiKey.ChannelUp)]
        [InlineData("XF86LowerChannel", UiKey.ChannelDown)] [InlineData("XF86ChannelDown", UiKey.ChannelDown)] [InlineData("Next", UiKey.ChannelDown)] [InlineData("Page_Down", UiKey.ChannelDown)] [InlineData("KP_Next", UiKey.ChannelDown)]
        [InlineData("XF86AudioPlay", UiKey.Play)] [InlineData("XF86AudioPause", UiKey.Pause)]
        [InlineData("XF86PlayBack", UiKey.PlayPause)] [InlineData("XF86AudioPlayPause", UiKey.PlayPause)] [InlineData("space", UiKey.PlayPause)]
        [InlineData("XF86AudioStop", UiKey.Stop)]
        [InlineData("XF86AudioRewind", UiKey.Rewind)] [InlineData("XF86AudioPrev", UiKey.Rewind)] [InlineData("XF86AudioForward", UiKey.Forward)] [InlineData("XF86AudioNext", UiKey.Forward)]
        [InlineData("XF86Info", UiKey.Info)] [InlineData("Menu", UiKey.Info)] [InlineData("XF86Menu", UiKey.Info)]
        [InlineData("XF86Red", UiKey.Red)] [InlineData("F1", UiKey.Red)] [InlineData("XF86Green", UiKey.Green)] [InlineData("F2", UiKey.Green)]
        [InlineData("XF86Yellow", UiKey.Yellow)] [InlineData("F3", UiKey.Yellow)] [InlineData("XF86Blue", UiKey.Blue)] [InlineData("F4", UiKey.Blue)]
        [InlineData("XF86Unknown", UiKey.None)] [InlineData("", UiKey.None)] [InlineData(null, UiKey.None)]
        // names that are NOT X11 ones: the X11 table does not know the logical vocabulary (and "Pause" is the PC keyboard's key, not the remote's)
        [InlineData("Ok", UiKey.None)] [InlineData("ChannelUp", UiKey.None)] [InlineData("Pause", UiKey.None)] [InlineData("Back", UiKey.None)]
        public void X11NamesMapToTheKeysTheTvHasAlwaysUsed(string name, UiKey key) { Assert.Equal(key, KeyNames.FromName(name).Key); }

        [Theory]
        [InlineData("minus", '-')] [InlineData("period", '.')] [InlineData("KP_Decimal", '.')] [InlineData("slash", '/')] [InlineData("KP_Divide", '/')] [InlineData("colon", ':')]
        [InlineData("underscore", '_')] [InlineData("equal", '=')] [InlineData("question", '?')] [InlineData("ampersand", '&')] [InlineData("percent", '%')]
        [InlineData("7", '7')] [InlineData("a", 'a')] [InlineData("Z", 'Z')] [InlineData("KP_3", '3')] [InlineData("KP_0", '0')]
        public void PunctuationLettersAndDigitsAreTypedCharacters(string name, char ch)
        {
            var k = KeyNames.FromName(name);
            Assert.Equal(UiKey.Char, k.Key); Assert.Equal(ch, k.Ch); Assert.Equal(name, k.RawName);
        }

        [Fact]
        public void EveryLogicalKeyHasANameAndRoundTrips()
        {
            foreach (UiKey k in System.Enum.GetValues(typeof(UiKey)))
            {
                string n = KeyNames.LogicalName(k);
                if (k == UiKey.None || k == UiKey.Char) { Assert.Null(n); continue; }
                Assert.NotNull(n);
                Assert.Equal(k, KeyNames.FromLogical(n).Key);
                Assert.Equal(k, KeyNames.FromLogical(n.ToUpperInvariant()).Key);          // case does not matter
                Assert.Equal(k, KeyNames.FromLogical(n.ToLowerInvariant()).Key);
            }
        }

        [Fact]
        public void LogicalNamesAreNotTypedCharactersOrJunk()
        {
            Assert.Equal(UiKey.None, KeyNames.FromLogical("Char").Key);
            Assert.Equal(UiKey.None, KeyNames.FromLogical("None").Key);
            Assert.Equal(UiKey.None, KeyNames.FromLogical("nonsense").Key);
            Assert.Equal(UiKey.None, KeyNames.FromLogical("").Key);
            Assert.Equal(UiKey.None, KeyNames.FromLogical(null).Key);
        }

        [Fact]
        public void ParseTriesTheX11NamesFirstSoNothingThatMeantSomethingChanges()
        {
            Assert.Equal(UiKey.Ok, KeyNames.Parse("Return").Key);
            Assert.Equal(UiKey.Ok, KeyNames.Parse("Ok").Key);
            Assert.Equal(UiKey.ChannelUp, KeyNames.Parse("ChannelUp").Key);
            Assert.Equal(UiKey.ChannelUp, KeyNames.Parse("XF86RaiseChannel").Key);
            Assert.Equal(UiKey.Back, KeyNames.Parse("Back").Key);
            Assert.Equal(UiKey.Up, KeyNames.Parse("Up").Key);                              // in both vocabularies
            Assert.Equal(UiKey.Backspace, KeyNames.Parse("BackSpace").Key);                // X11 spelling
            Assert.Equal(UiKey.Backspace, KeyNames.Parse("Backspace").Key);                // logical spelling
            Assert.Equal(UiKey.Char, KeyNames.Parse("x").Key);                             // an X11 letter, not a logical name
            Assert.Equal(UiKey.None, KeyNames.Parse("bogus").Key);
        }

        [Fact]
        public void ScriptParsing()
        {
            var steps = KeyScript.Parse("Right text:ab wait:500 log:x XF86Back bogus");
            Assert.Equal(6, steps.Count);
            Assert.Equal(UiKey.Right, steps[0].Key.Key);
            Assert.Equal('a', steps[1].Key.Ch);
            Assert.Equal(500, steps[3].WaitMs);
            Assert.Equal("x", steps[4].Label);
            Assert.Equal(UiKey.Back, steps[5].Key.Key);
            Assert.Empty(KeyScript.Parse(null));
        }

        [Fact]
        public void ScriptsCanUseLogicalNamesOnAnyPlatform()
        {
            var steps = KeyScript.Parse("Ok ChannelUp Red Back nonsense wait:100 Info");
            Assert.Equal(new[] { UiKey.Ok, UiKey.ChannelUp, UiKey.Red, UiKey.Back }, steps.Take(4).Select(x => x.Key.Key).ToArray());
            Assert.Equal(100, steps[4].WaitMs);
            Assert.Equal(UiKey.Info, steps[5].Key.Key);
        }
    }

    /// <summary>The list the TV shell registers with KeyGrabEx. It is part of what keeps Samsung's Home menu and the TV source from reacting: any change must be deliberate.</summary>
    public class TizenKeysTests
    {
        [Fact]
        public void TheGrabbedKeysAreExactlyTheOnesRegisteredSinceTheFirstWorkingVersion()
        {
            Assert.Equal(new[]
            {
                "XF86RaiseChannel", "XF86LowerChannel", "XF86ChannelUp", "XF86ChannelDown", "XF86AudioPlay", "XF86AudioPause", "XF86AudioStop", "XF86AudioRewind",
                "XF86AudioForward", "XF86AudioNext", "XF86AudioPrev", "XF86PlayBack", "XF86Info", "XF86Red", "XF86Green", "XF86Yellow", "XF86Blue", "XF86Back"
            }, PTizen.Platform.TizenKeys.GrabNames);
        }

        [Fact]
        public void BackIsGrabbedSoTheTvDoesNotOpenItsHomeMenuOnTopOfTheTransparentPlayerWindow()
        {
            Assert.Contains("XF86Back", PTizen.Platform.TizenKeys.GrabNames);
        }

        [Fact]
        public void NoDuplicatesAndEveryGrabbedKeyMapsToALogicalKey()
        {
            Assert.Equal(PTizen.Platform.TizenKeys.GrabNames.Length, PTizen.Platform.TizenKeys.GrabNames.Distinct().Count());
            foreach (var n in PTizen.Platform.TizenKeys.GrabNames) Assert.NotEqual(UiKey.None, KeyNames.FromName(n).Key);
        }

        [Fact]
        public void TheRemoteKeysThePlayerNeedsAreAllGrabbed()
        {
            var grabbed = new HashSet<UiKey>(PTizen.Platform.TizenKeys.GrabNames.Select(n => KeyNames.FromName(n).Key));
            foreach (var k in new[] { UiKey.ChannelUp, UiKey.ChannelDown, UiKey.Play, UiKey.Pause, UiKey.PlayPause, UiKey.Stop, UiKey.Rewind, UiKey.Forward, UiKey.Info, UiKey.Red, UiKey.Green, UiKey.Yellow, UiKey.Blue, UiKey.Back })
                Assert.Contains(k, grabbed);
        }
    }

    public class OnScreenKeyboardTests
    {
        [Fact]
        public void TypesAndEdits()
        {
            var kb = new OnScreenKeyboard();
            kb.Row = 1; kb.Cell = 0; kb.Press();                   // q
            kb.Row = 3; kb.Cell = 0; kb.Press();                   // z
            Assert.Equal("qz", kb.Text.ToString());
            kb.Row = 4; kb.Cell = 4; Assert.Equal(KbAction.Backspace, kb.Focused.Action); kb.Press();
            Assert.Equal("q", kb.Text.ToString());
            kb.Row = 4; kb.Cell = 0; Assert.Equal(KbAction.Space, kb.Focused.Action); kb.Press(); kb.Press();   // a single space in a row
            Assert.Equal("q ", kb.Text.ToString());
            kb.Row = 4; kb.Cell = 6; Assert.Equal(KbAction.Clear, kb.Focused.Action); kb.Press();
            Assert.Equal("", kb.Text.ToString());
        }

        [Fact]
        public void SubmitSpansTwoCellsAndSetsTheFlag()
        {
            var kb = new OnScreenKeyboard { Row = 4, Cell = 9 };
            Assert.Equal(KbAction.Submit, kb.Focused.Action);
            kb.Press();
            Assert.True(kb.SubmitRequested);
        }

        [Fact]
        public void MovementStaysInsideAndReportsEdges()
        {
            var kb = new OnScreenKeyboard { Row = 0, Cell = 0 };
            Assert.False(kb.Move(UiKey.Left)); Assert.False(kb.Move(UiKey.Up));
            for (int i = 0; i < 9; i++) Assert.True(kb.Move(UiKey.Right));
            Assert.False(kb.Move(UiKey.Right));
            for (int i = 0; i < 4; i++) Assert.True(kb.Move(UiKey.Down));
            Assert.False(kb.Move(UiKey.Down));
            Assert.Equal(KbAction.Submit, kb.Focused.Action);
            Assert.True(kb.Move(UiKey.Left));
            Assert.Equal(KbAction.Clear, kb.Focused.Action);
        }

        [Fact]
        public void EnforcesMaxLength()
        {
            var kb = new OnScreenKeyboard { MaxLength = 3 };
            for (int i = 0; i < 10; i++) kb.Type('a');
            Assert.Equal(3, kb.Text.Length);
        }
    }
}
