using System;
using AceEngine.App;
using PUi.Core;
using PUi.Input;
using Xunit;

namespace PUi.Tests
{
    public class PlayerControllerTests
    {
        private static PlaybackStatus St(PlayPhase p, Action<PlaybackStatus> f = null) { var s = new PlaybackStatus { Phase = p, IsLive = true }; if (f != null) f(s); return s; }

        [Fact]
        public void LoadingThenPlayingThenBufferingKeepsTheVideoWindow()
        {
            var c = new PlayerController();
            Assert.Equal(PlayerMode.Loading, c.Mode); Assert.False(c.WindowTransparent);
            c.OnStatus(St(PlayPhase.Resolving), 0); Assert.Equal(PlayerMode.Loading, c.Mode);
            Assert.False(c.WindowTransparent);
            c.OnStatus(St(PlayPhase.Preparing), 0); Assert.Equal(PlayerMode.Loading, c.Mode);
            Assert.True(c.WindowTransparent);          // the player is created now: the window must already be transparent (otherwise the system interrupts it)
            c.OnStatus(St(PlayPhase.Buffering, s => s.BufferPercent = 50), 1); Assert.Equal(PlayerMode.Loading, c.Mode);
            Assert.InRange(c.LoadProgress, 0.5, 0.7);
            c.OnStatus(St(PlayPhase.Playing), 2);
            Assert.Equal(PlayerMode.Playing, c.Mode); Assert.True(c.Started); Assert.True(c.WindowTransparent);
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 40), 3);
            Assert.Equal(PlayerMode.Buffering, c.Mode); Assert.Equal(40, c.BufferPercent); Assert.True(c.WindowTransparent);
            c.OnStatus(St(PlayPhase.Retrying), 4);
            Assert.Equal(PlayerMode.Buffering, c.Mode);        // the video has already been seen: buffer ring, not the lamp
        }

        [Fact]
        public void ErrorMakesTheWindowOpaqueAgain()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing), 0);
            c.OnStatus(St(PlayPhase.Error, s => s.Error = "x"), 1);
            Assert.Equal(PlayerMode.Error, c.Mode); Assert.False(c.WindowTransparent);
            Assert.Equal(PlayerAction.Retry, c.OnKey(UiKey.Ok, 2));
            Assert.Equal(PlayerAction.Exit, c.OnKey(UiKey.Back, 2));
        }

        [Fact]
        public void PanelShowsOnKeysAndHidesAfterFiveSeconds()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing), 0);
            c.Tick(0.1); Assert.False(c.PanelWanted);
            Assert.Equal(PlayerAction.None, c.OnKey(UiKey.Ok, 1));
            c.Tick(1.1); Assert.True(c.PanelWanted);
            c.Tick(5.9); Assert.True(c.PanelWanted);
            c.Tick(6.1); Assert.False(c.PanelWanted);
        }

        [Fact]
        public void PanelStaysWhilePausedAndWithErrors()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Paused), 0);
            c.Tick(100); Assert.True(c.PanelWanted);
            c.OnStatus(St(PlayPhase.Playing), 101);
            c.Tick(110); Assert.False(c.PanelWanted);
        }

        [Fact]
        public void LeftAndRightSeekWhenThePanelIsHiddenAndMoveFocusWhenShown()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing), 0);
            Assert.Equal(PlayerAction.SeekBack, c.OnKey(UiKey.Left, 1));
            c.Tick(1.1); Assert.True(c.PanelWanted);
            Assert.Equal(PlayerAction.None, c.OnKey(UiKey.Right, 1.2));
            Assert.Equal(PanelButton.Forward10, c.Focus);
            Assert.Equal(PlayerAction.SeekForward, c.OnKey(UiKey.Ok, 1.3));
            c.OnKey(UiKey.Right, 1.4); Assert.Equal(PanelButton.Live, c.Focus);
            Assert.Equal(PlayerAction.GoLive, c.OnKey(UiKey.Ok, 1.5));
            c.OnKey(UiKey.Left, 1.6); c.OnKey(UiKey.Left, 1.7); c.OnKey(UiKey.Left, 1.8); c.OnKey(UiKey.Left, 1.9);
            Assert.Equal(PanelButton.Back10, c.Focus);
            Assert.Equal(PlayerAction.SeekBack, c.OnKey(UiKey.Ok, 2));
        }

        [Fact]
        public void PlayPauseKeysAndMediaKeysMapToActions()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing), 0);
            Assert.Equal(PlayerAction.TogglePause, c.OnKey(UiKey.PlayPause, 1));
            Assert.Equal(PlayerAction.None, c.OnKey(UiKey.Play, 1));      // it is already playing
            Assert.Equal(PlayerAction.TogglePause, c.OnKey(UiKey.Pause, 1));
            c.OnStatus(St(PlayPhase.Paused), 2);
            Assert.Equal(PlayerAction.TogglePause, c.OnKey(UiKey.Play, 3));
            Assert.Equal(PlayerAction.None, c.OnKey(UiKey.Pause, 3));
            Assert.Equal(PlayerAction.SeekBack, c.OnKey(UiKey.Rewind, 3));
            Assert.Equal(PlayerAction.SeekForward, c.OnKey(UiKey.Forward, 3));
        }

        [Fact]
        public void ChannelKeysOnlyWorkWithOtherChannelsAndNeverWhileLoadingIsIgnored()
        {
            var c = new PlayerController { CanChangeChannel = false };
            c.OnStatus(St(PlayPhase.Playing), 0);
            Assert.Equal(PlayerAction.None, c.OnKey(UiKey.ChannelUp, 1));
            c.CanChangeChannel = true;
            Assert.Equal(PlayerAction.NextChannel, c.OnKey(UiKey.ChannelUp, 1));
            Assert.Equal(PlayerAction.PrevChannel, c.OnKey(UiKey.ChannelDown, 1));
            var l = new PlayerController { CanChangeChannel = true };
            Assert.Equal(PlayerAction.NextChannel, l.OnKey(UiKey.ChannelUp, 0));        // the channel can be changed while loading
            Assert.Equal(PlayerAction.None, l.OnKey(UiKey.PlayPause, 0));
            Assert.Equal(PlayerAction.Exit, l.OnKey(UiKey.Back, 0));
        }

        [Fact]
        public void TimelineFractionAndBehindFollowTheWindow()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing, s => { s.TimeshiftWindowMs = 180000; s.BehindLiveMs = 0; }), 0);
            Assert.Equal(1.0, (double)c.Fraction(0), 3); Assert.True(c.AtLive(0));
            c.OnStatus(St(PlayPhase.Playing, s => { s.TimeshiftWindowMs = 180000; s.BehindLiveMs = 90000; }), 1);
            Assert.Equal(0.5, (double)c.Fraction(1), 2); Assert.False(c.AtLive(1));
            // on pause the delay grows with the clock, without going past the window
            c.OnStatus(St(PlayPhase.Paused, s => { s.TimeshiftWindowMs = 180000; s.BehindLiveMs = 170000; }), 2);
            Assert.InRange(c.BehindMs(4), 171000, 173000);
            Assert.Equal(180000, c.BehindMs(60));
            Assert.Equal(0.0, (double)c.Fraction(60), 3);
            // without a window (no memory) the line is full
            c.OnStatus(St(PlayPhase.Playing), 70);
            Assert.Equal(1.0, (double)c.Fraction(70), 3);
        }

        [Fact]
        public void TheChannelLoadBarOnlyAdvancesEvenIfTheRealProgressGoesBack()
        {
            var c = new PlayerController();
            float prev = c.LoadProgressShown;
            Action<PlaybackStatus> feed = st => { c.OnStatus(st, 0); Assert.True(c.LoadProgressShown >= prev - 1e-6f, "retrocede: " + prev + " -> " + c.LoadProgressShown); prev = c.LoadProgressShown; };
            feed(St(PlayPhase.Resolving));
            feed(St(PlayPhase.Connecting));
            feed(St(PlayPhase.Buffering, s => s.BufferPercent = 60));
            float high = c.LoadProgressShown;
            feed(St(PlayPhase.Buffering, s => s.BufferPercent = 20));                 // the prebuffer percentage goes down: what is shown is kept
            Assert.Equal(high, c.LoadProgressShown, 3);
            feed(St(PlayPhase.Retrying));                                              // retry: the phase is "worth" less, the bar does not go down
            Assert.Equal(high, c.LoadProgressShown, 3);
            feed(St(PlayPhase.Resolving));
            feed(St(PlayPhase.Buffering, s => s.BufferPercent = 95));
            Assert.True(c.LoadProgressShown > high);
            feed(St(PlayPhase.Preparing));
            Assert.True(c.LoadProgressShown >= 0.93f);
            c.Reset();                                                                 // a new channel starts again from zero
            Assert.Equal(0.05f, Math.Max(0.05f, c.LoadProgressShown), 3);
            Assert.True(c.LoadProgressShown < 0.2f);
        }

        [Fact]
        public void WithoutMeasurableProgressTheLoadIndicatorIsIndeterminate()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Resolving), 0); Assert.True(c.LoadIndeterminate);
            c.OnStatus(St(PlayPhase.Connecting), 0); Assert.True(c.LoadIndeterminate);
            c.OnStatus(St(PlayPhase.Buffering, s => s.BufferPercent = 0), 0); Assert.True(c.LoadIndeterminate);
            c.OnStatus(St(PlayPhase.Buffering, s => s.BufferPercent = 30), 0); Assert.False(c.LoadIndeterminate);
            c.OnStatus(St(PlayPhase.Retrying), 0); Assert.True(c.LoadIndeterminate);
            c.OnStatus(St(PlayPhase.Preparing), 0); Assert.False(c.LoadIndeterminate);
        }

        [Fact]
        public void TheBufferPercentDuringPlaybackOnlyAdvancesWithinAnEpisodeAndRestartsAfterwards()
        {
            var c = new PlayerController();
            c.OnStatus(St(PlayPhase.Playing), 0);
            Assert.Equal(PlayerMode.Playing, c.Mode);
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 30), 1); Assert.Equal(PlayerMode.Buffering, c.Mode); Assert.Equal(30, c.BufferPercentShown);
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 60), 2); Assert.Equal(60, c.BufferPercentShown);
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 40), 3); Assert.Equal(60, c.BufferPercentShown);          // goes down: it is kept
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 95), 4); Assert.Equal(95, c.BufferPercentShown);
            c.OnStatus(St(PlayPhase.Playing), 5); Assert.Equal(PlayerMode.Playing, c.Mode);                                          // the episode ends
            c.OnStatus(St(PlayPhase.Playing, s => s.PlayerBufferPercent = 10), 6); Assert.Equal(10, c.BufferPercentShown);          // the next one starts from zero
        }
    }
}
