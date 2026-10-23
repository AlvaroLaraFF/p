using System.Linq;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>
    /// The system keyboard (ITextInput): touch platforms use it in Search, the remote platforms keep PUi's own on-screen keyboard.
    /// The platform side is FakeTextInput, driven by the test as the user.
    /// </summary>
    public class ImeTests
    {
        private static Scenes.Rig SearchRig(System.Action<Scenes.Rig> setup = null)
        {
            var r = Phone(rig => { rig.Services.Search.AddRecentQuery("nba"); rig.Services.Search.AddRecentQuery("dazn f1"); rig.Checker.Default = AceEngine.Probe.LinkState.Good; if (setup != null) setup(rig); });
            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            return r;
        }

        private static FakeTextInput Ime(Scenes.Rig r) { return (FakeTextInput)r.Platform.TextInput; }

        [Fact]
        public void TappingTheSearchFieldOnATouchPlatformAsksForTheSystemKeyboard()
        {
            var r = SearchRig();
            Assert.False(Ime(r).IsShown);
            TapOn(r, "field");
            Assert.True(Ime(r).IsShown);
            Assert.Equal(1, Ime(r).ShowCount);
            Assert.Equal(TextInputAction.Search, Ime(r).LastRequest.Action);
            Assert.Equal(60, Ime(r).LastRequest.MaxLength);
            Assert.DoesNotContain("keyboard", r.App.Describe());          // PUi's own keyboard is not drawn
            Assert.Contains(" ime", r.App.Describe());
            TapOn(r, "field");                                            // a second tap does not ask again
            Assert.Equal(1, Ime(r).ShowCount);
        }

        [Fact]
        public void WhatTheUserTypesInTheSystemKeyboardSearchesAsYouType()
        {
            var r = SearchRig();
            TapOn(r, "field");
            Ime(r).Type("Lig");
            Assert.Contains("query='lig'", r.App.Describe());              // PUi searches in lower case
            Ime(r).Type("a");
            r.Advance(r.Now + 0.3);
            Assert.Contains("query='liga'", r.App.Describe());
            Assert.Matches(@"results=([4-9]|\d\d)\b", r.App.Describe());           // the live matches are listed
            Ime(r).Backspace();
            Assert.Contains("query='lig'", r.App.Describe());
            Assert.Equal("Lig", Ime(r).Text);                              // PUi does not rewrite what the keyboard holds while the user types
        }

        [Fact]
        public void TheActionKeyClosesTheKeyboardRemembersTheSearchAndFocusesTheFirstResult()
        {
            var r = SearchRig();
            TapOn(r, "field");
            Ime(r).Type("liga");
            Ime(r).PressAction();
            r.Advance(r.Now + 0.3);
            Assert.False(Ime(r).IsShown);
            Assert.Equal(1, Ime(r).HideCount);
            Assert.Equal("liga", r.Services.Search.RecentQueries.First());
            Assert.DoesNotContain(" ime", r.App.Describe());
            Assert.Contains("[focus cid:", r.App.Describe());              // the first result
        }

        [Fact]
        public void WhenTheSystemClosesTheKeyboardPUiKeepsTheTextAndDoesNotCloseItAgain()
        {
            var r = SearchRig();
            TapOn(r, "field");
            Ime(r).Type("liga");
            Ime(r).Dismiss();
            r.Advance(r.Now + 0.3);
            Assert.Equal(0, Ime(r).HideCount);
            Assert.Contains("query='liga'", r.App.Describe());
            Assert.Equal("liga", r.Services.Search.RecentQueries.First());
            TapOn(r, "field");                                            // it can be opened again with the text still there
            Assert.True(Ime(r).IsShown);
            Assert.Equal("liga", Ime(r).LastRequest.Text);
        }

        [Fact]
        public void WhenPUiChangesTheTextItselfTheKeyboardIsToldAndNoTextChangedComesBack()
        {
            var r = SearchRig();
            TapOn(r, "field");
            // a recent search is tapped while the keyboard is up
            var seen = new System.Collections.Generic.List<string>();
            Ime(r).TextChanged += seen.Add;
            TapOn(r, "chip:nba");
            Assert.Equal("nba", Ime(r).Text);
            Assert.Empty(seen);
            Assert.Contains("query='nba'", r.App.Describe());
        }

        [Fact]
        public void WhileTheSystemKeyboardIsUpTypedCharactersFromTheKeyEventsAreNotUsed()
        {
            var r = SearchRig();
            TapOn(r, "field");
            Ime(r).Type("li");
            r.Platform.Type("xyz"); r.Platform.PressKey(UiKey.Backspace);
            Assert.Contains("query='li'", r.App.Describe());
        }

        [Fact]
        public void BackWhileTheKeyboardIsUpClosesItBeforeAnythingElse()
        {
            var r = SearchRig();
            TapOn(r, "field");
            Ime(r).Type("liga");
            r.Platform.Raise(new KeyPress(UiKey.Back, "back"));
            Assert.False(Ime(r).IsShown);
            Assert.Contains("query='liga'", r.App.Describe());            // the results are still there
            r.Platform.Raise(new KeyPress(UiKey.Back, "back"));
            Assert.Contains("query=''", r.App.Describe());                // the next Back clears the search, as always
        }

        [Fact]
        public void LeavingSearchOrPausingTheAppClosesTheKeyboard()
        {
            var r = SearchRig();
            TapOn(r, "field"); Ime(r).Type("liga");
            TapOn(r, "rail0");
            Assert.False(Ime(r).IsShown);

            r.App.GoSection(Section.Search); r.Advance(r.Now + 0.5);
            TapOn(r, "field");
            Assert.True(Ime(r).IsShown);
            r.App.OnPause();
            Assert.False(Ime(r).IsShown);
        }

        [Fact]
        public void ScrollingTheResultsGivesThemTheWholeScreenByClosingTheKeyboard()
        {
            var r = SearchRig(rig => { for (int i = 0; i < 30; i++) rig.Catalog.Add("Liga extra " + i, "Liga canal " + i); });
            TapOn(r, "field");
            Ime(r).Type("liga");
            r.Checker.Default = AceEngine.Probe.LinkState.Good;
            foreach (var c in r.Catalog.Channels) r.Checker.Set(c, AceEngine.Probe.LinkState.Good, 5);
            r.Advance(r.Now + 0.6);
            Assert.True(((SectionScreen)r.App.Current).TouchTargets().Count(t => t.Id.StartsWith("cid:")) > 4);
            r.Drag(200, 650, 200, 250, 0.3, lift: false); r.Advance(r.Now + 0.2); r.FingerUp(200, 250, 0.5);
            Assert.False(Ime(r).IsShown);
            Assert.IsType<SearchScreen>(r.App.Current);
        }

        // ------------------------------------------------------------------ platforms without a system keyboard

        [Fact]
        public void WithARemoteAndNoTouchPUisOwnKeyboardIsUsedAsBefore()
        {
            var tv = Scenes.NewRig(Fonts); tv.App.Start(); tv.App.GoSection(Section.Search); tv.Advance(0.5);
            tv.Platform.PressKey(UiKey.Ok);
            Assert.Contains("keyboard", tv.App.Describe());
            Assert.Equal(0, Ime(tv).ShowCount);
        }

        [Fact]
        public void ARemoteWithAKeyboardButNoTouchKeepsPUisKeyboardAndTypesDirectly()
        {
            var r = Scenes.NewRig(Fonts);
            r.Platform.Capabilities = new PlatformCapabilities(hasRemote: true, hasKeyboard: true);
            r.App.Start(); r.App.GoSection(Section.Search); r.Advance(0.5);
            r.Platform.Type("nba");
            Assert.Contains("query='nba'", r.App.Describe());              // typed straight into the field
            r.Platform.PressKey(UiKey.Ok);
            Assert.Contains("keyboard", r.App.Describe());
            Assert.Equal(0, Ime(r).ShowCount);
        }

        [Fact]
        public void ATouchPlatformWithoutASystemKeyboardGetsPUisKeyboardUsableWithTheFinger()
        {
            var r = SearchRig(rig => rig.Platform.TextInput = null);
            TapOn(r, "field");
            Assert.Contains("keyboard", r.App.Describe());
            TapOn(r, "key:l"); TapOn(r, "key:i"); TapOn(r, "key:g"); TapOn(r, "key:a");
            Assert.Contains("query='liga'", r.App.Describe());
            TapOn(r, "key:Backspace");
            Assert.Contains("query='lig'", r.App.Describe());
            TapOn(r, "key:Submit");
            Assert.DoesNotContain("keyboard", r.App.Describe());
            Assert.Equal("lig", r.Services.Search.RecentQueries.First());
        }

        [Fact]
        public void AKeyboardPanelBlocksTheTargetsUnderIt()
        {
            var r = SearchRig(rig => rig.Platform.TextInput = null);
            TapOn(r, "field");
            var panel = Target(r, "kb");
            // the tiles of Explore are drawn behind the keyboard: a tap in a gap between keys does not open one
            r.Tap(panel.Rect.Left + 4, panel.Rect.Top + 4);
            Assert.IsType<SearchScreen>(r.App.Current);
        }

        [Fact]
        public void TheTizenPlatformDoesNothingAboutTheSystemKeyboard()
        {
            // PTizen cannot be built here: its source is read. The TV has no system keyboard to ask for, so its TextInput is null and PUi uses its own
            string src = System.IO.File.ReadAllText(RepoPaths.Combine("src", "PTizen", "Platform", "TizenPlatform.cs"));
            Assert.Matches(@"ITextInput\s+TextInput\s*\{\s*get\s*\{\s*return\s+null;\s*\}\s*\}", src);
            // and the desktop test platform plays a platform without one by returning null too
            var p = new DesktopPlatform(Fonts) { TextInput = null };
            Assert.Null(((IPlatform)p).TextInput);
        }
    }
}
