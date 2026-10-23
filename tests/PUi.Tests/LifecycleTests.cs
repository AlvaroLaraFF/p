using System;
using System.Linq;
using AceEngine.App;
using AceEngine.Probe;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Screens;
using PUi.Testing;
using Xunit;

namespace PUi.Tests
{
    /// <summary>App life cycle (Home, source switch, return from the apps panel) with the player open.</summary>
    public class LifecycleTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private sealed class Ctx
        {
            public Scenes.Rig R; public double T = 1000; public int Suspends, Resumes; public AceEngine.App.Tv.Channel Channel;
        }

        /// <summary>TV -> category -> a live channel playing (transparent window).</summary>
        private static Ctx PlayingChannel()
        {
            var c = new Ctx();
            var r = Scenes.NewRig(Fonts); c.R = r;
            r.Services.Suspend = () => c.Suspends++; r.Services.Resume = () => c.Resumes++;
            r.App.Clock = () => c.T;
            r.App.Start(); r.Advance(0.5);
            r.Platform.PressKey(UiKey.Ok); r.Advance(r.Now + 0.4);                          // first category
            var cat = (CategoryScreen)r.App.Current;
            c.Channel = r.Catalog.Categories.First().Channels[1];
            r.Checker.Set(c.Channel, LinkState.Good, 12); r.Advance(r.Now + 0.3);
            r.Checker.Set(r.Catalog.Categories.First().Channels[3], LinkState.Good, 8); r.Advance(r.Now + 0.5);
            r.Platform.PressKey(UiKey.Down); r.Advance(r.Now + 0.2);
            r.Platform.PressKey(UiKey.Up); r.Advance(r.Now + 0.2);
            r.Platform.PressKey(UiKey.Ok); r.Advance(r.Now + 0.4);                          // opens the channel
            Assert.IsType<PlayerScreen>(r.App.Current);
            r.Playback.Created[0].Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 5; });
            r.Advance(r.Now + 0.6);
            Assert.False(r.Platform.Opaque);                                               // with the video running the window is transparent
            return c;
        }

        private static bool Disposed(ScriptedPlayback p)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000) { if (p.Calls.Contains("dispose")) return true; System.Threading.Thread.Sleep(5); }
            return false;
        }

        [Fact]
        public void HomeWithThePlayerOpenStopsEverythingReleasesAudioAndLeavesAnOpaqueWindowOnTheList()
        {
            var c = PlayingChannel(); var r = c.R;
            Assert.True(r.Platform.AudioHeld);
            int batches = r.Checker.Batches.Count;
            r.App.OnPause();
            Assert.Contains("stop", r.Playback.Created[0].Calls.ToArray());                // the player and the engine session stop...
            Assert.True(Disposed(r.Playback.Created[0]));
            Assert.False(r.Platform.AudioHeld);                                            // ...the audio focus is released...
            Assert.Equal(1, c.Suspends);                                                   // ...and the probe and the P2P network stop (engine Suspend)
            Assert.True(r.Platform.Opaque);                                                // the window is no longer transparent when leaving
            Assert.IsType<CategoryScreen>(r.App.Current);                                  // the player is no longer on the stack
            Assert.Equal(batches, r.Checker.Batches.Count);                                // and the list did NOT reactivate the background probe
            Assert.Single(r.Playback.Created);                                             // nothing new is opened
            r.Advance(r.Now + 5);                                                          // on pause nothing is drawn or updated
            Assert.Equal(batches, r.Checker.Batches.Count);
        }

        [Fact]
        public void ShortPauseComesBackToTheSameChannelLiveWithAudioAndAnOpaqueLoadingScreen()
        {
            var c = PlayingChannel(); var r = c.R;
            r.App.OnPause();
            c.T += 45;                                                                     // 45 s on the Home menu
            r.App.OnResume();
            Assert.True(r.Platform.AudioHeld);                                             // audio focus re-acquired
            Assert.Equal(1, c.Resumes);
            Assert.IsType<PlayerScreen>(r.App.Current);                                    // same channel...
            Assert.Contains(c.Channel.Name, r.App.Current.Describe());
            Assert.Equal(2, r.Playback.Created.Count);                                     // ...with a new live playback
            r.Advance(r.Now + 0.3);
            Assert.True(r.Platform.Opaque);                                                // loading: opaque window with the loading lamp, without seeing the TV source
            Assert.Contains("mode=Loading", r.App.Current.Describe());
            // and Back in the player returns to the list
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.4);
            Assert.IsType<CategoryScreen>(r.App.Current);
            Assert.True(r.Platform.Opaque);
        }

        [Fact]
        public void LongPauseComesBackToThePreviousScreenOpaqueAndWithTheListWorkingAgain()
        {
            var c = PlayingChannel(); var r = c.R;
            r.App.OnPause();
            int batches = r.Checker.Batches.Count;
            c.T += 600;                                                                    // 10 min
            r.App.OnResume();
            Assert.True(r.Platform.AudioHeld);
            Assert.IsType<CategoryScreen>(r.App.Current);                                  // returns to the list, not to the channel
            Assert.Single(r.Playback.Created);
            Assert.True(r.Checker.Batches.Count > batches);                                // and the list resumes its checking
            r.Advance(r.Now + 0.4);
            Assert.True(r.Platform.Opaque);
            using (var bmp = r.Render(r.Now + 0.1)) Assert.NotEqual(0, bmp.GetPixel(900, 500).Alpha - 0);        // full and opaque frame
            using (var bmp = r.Render(r.Now + 0.2)) Assert.Equal(255, bmp.GetPixel(900, 500).Alpha);
        }

        [Fact]
        public void TheLimitOfAShortPauseIsTwoMinutes()
        {
            var c = PlayingChannel(); var r = c.R;
            r.App.OnPause(); c.T += 119; r.App.OnResume();
            Assert.IsType<PlayerScreen>(r.App.Current);
            r.App.OnPause(); c.T += 121; r.App.OnResume();
            Assert.IsType<CategoryScreen>(r.App.Current);
        }

        [Fact]
        public void HomeOnAScreenWithoutPlayerJustSuspendsAndResumes()
        {
            var r = Scenes.NewRig(Fonts); int susp = 0;
            r.Services.Suspend = () => susp++;
            r.App.Start(); r.Advance(0.5);
            r.App.OnPause();
            Assert.False(r.Platform.AudioHeld); Assert.Equal(1, susp);
            Assert.IsType<MainScreen>(r.App.Current);
            r.App.OnResume();
            Assert.True(r.Platform.AudioHeld); Assert.IsType<MainScreen>(r.App.Current); Assert.True(r.Platform.Opaque);
            r.App.OnResume(); r.App.OnPause(); r.App.OnPause();                           // repeated calls do not break anything
            Assert.Equal(2, susp);
        }

        [Fact]
        public void HomeWithAPausedPlayerAndAfterChannelSwitchesReturnsToTheCurrentChannel()
        {
            var c = PlayingChannel(); var r = c.R;
            r.Platform.PressKey(UiKey.ChannelUp); r.Advance(r.Now + 0.4);                  // Channel +
            var player = (PlayerScreen)r.App.Current;
            string name = player.Context.Current.Name;
            r.Playback.Created[1].Push(s => { s.Phase = PlayPhase.Paused; s.IsLive = true; });
            r.Advance(r.Now + 0.3);
            r.App.OnPause(); c.T += 30; r.App.OnResume();
            Assert.Contains(name, r.App.Current.Describe());                               // returns to the channel it was on, not the first
        }
    }
}
