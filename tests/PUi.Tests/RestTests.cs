using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Motion;
using PUi.Screens;
using PUi.Testing;
using SkiaSharp;
using Xunit;

namespace PUi.Tests
{
    /// <summary>
    /// UI idle state: the repeating decoration (fluid border, strip, pulse) slows down and stops when the remote has gone untouched for a while, the frame
    /// rate drops with it, the loading indicators do not slow down, and a key press returns everything to its speed. With movement, the same as before.
    /// </summary>
    public class DecorClockTests
    {
        [Fact]
        public void TheRateIsOneWhileInUseFallsSmoothlyAndIsZeroAtRest()
        {
            var c = new DecorClock { CalmAfter = 10, RestAfter = 30 };
            Assert.Equal(1, c.RateFor(0)); Assert.Equal(1, c.RateFor(10));
            Assert.Equal(0.5, c.RateFor(20), 6);
            Assert.Equal(0, c.RateFor(30)); Assert.Equal(0, c.RateFor(600));
            double prev = 1;
            for (double q = 10; q <= 30; q += 0.25) { double r = c.RateFor(q); Assert.True(r <= prev + 1e-12, "no sube"); prev = r; }
            Assert.True(c.RateFor(10.5) > 0.99 && c.RateFor(29.5) < 0.01);                 // starts and ends smoothly
        }

        [Fact]
        public void WhileInUseItFollowsRealTimeExactlyAndAtRestItStopsWithoutJumps()
        {
            var c = new DecorClock { CalmAfter = 10, RestAfter = 30 };
            Assert.False(c.Driven);
            double now = 100;
            for (int i = 0; i < 600; i++) { now += 1.0 / 60; c.Advance(now, 3); }
            Assert.True(c.Driven);
            Assert.Equal(now, c.Value, 6);                                                 // in use: the decoration's time IS real time (same phase as the design)
            Assert.Equal(1, c.Rate);
            double before = c.Value, maxStep = 0, last = c.Value;
            for (double q = 10; q < 40; q += 1.0 / 60) { now += 1.0 / 60; c.Advance(now, q); maxStep = Math.Max(maxStep, c.Value - last); last = c.Value; }
            Assert.True(maxStep <= 1.0 / 60 + 1e-9, "it never advances faster than real time");
            Assert.True(c.Stopped);
            double frozen = c.Value;
            for (int i = 0; i < 300; i++) { now += 1.0 / 60; c.Advance(now, 100 + i / 60.0); }
            Assert.Equal(frozen, c.Value, 9);                                              // stopped: it does not advance
            // a key press (quiet = 0): it goes back to its speed from where it was, without a jump
            now += 1.0 / 60; c.Advance(now, 0);
            Assert.False(c.Stopped);
            Assert.InRange(c.Value - frozen, 0, 1.0 / 60 + 1e-9);
            Assert.True(c.Value > before);
        }
    }

    public class RestGovernorTests
    {
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private sealed class RestChecker : ILiveChecker, IRestAware
        {
            public readonly FakeChecker Inner;
            public readonly List<bool> Calls = new List<bool>();
            public RestChecker(FakeChecker inner) { Inner = inner; }
            public ChannelStatus GetStatus(Channel c) { return Inner.GetStatus(c); }
            public void Check(IReadOnlyList<Channel> channels) { Inner.Check(channels); }
            public void Stop() { Inner.Stop(); }
            public bool Running { get { return Inner.Running; } }
            public int Done { get { return Inner.Done; } }
            public int Total { get { return Inner.Total; } }
            public event Action<Channel, ChannelStatus> StatusChanged { add { Inner.StatusChanged += value; } remove { Inner.StatusChanged -= value; } }
            public void SetResting(bool resting) { Calls.Add(resting); }
        }

        /// <summary>One screen tick every 1/60 s; returns the frames requested in 'seconds' (each one is drawn, small, as the platform would).</summary>
        private static int Frames(Scenes.Rig r, SKBitmap bmp, SKCanvas canvas, double seconds)
        {
            int frames = 0;
            double end = r.Now + seconds;
            while (r.Now < end - 1e-9)
            {
                r.Now += 1.0 / 60;
                if (r.App.NeedsFrame(r.Now)) { frames++; canvas.Clear(SKColors.Transparent); r.App.Draw(canvas, null, r.Now, bmp.Width, bmp.Height); }
            }
            return frames;
        }

        private static Scenes.Rig Rig(string scene, bool settle = true)
        {
            var r = Scenes.Build(scene, Fonts);
            if (settle) r.Checker.Settle(r.Catalog.Channels);
            return r;
        }

        [Fact]
        public void InUseTheDecorationDrawsThirtyFramesPerSecondLikeBefore()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("main");
                Frames(r, bmp, canvas, 3.0);                                              // settling (sprites and background)
                int f = Frames(r, bmp, canvas, 5.0);                                      // 5 s without touching anything: still in use (less than 10 s)
                Assert.InRange(f, 140, 170);                                              // 30 per s
                Assert.Equal("decor", ((SectionScreen)r.App.Current).DemandReason);
                r.App.Dispose();
            }
        }

        [Fact]
        public void WithoutTouchingTheRemoteTheFrameRateFallsAndThenStopsCompletely()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("main");
                Frames(r, bmp, canvas, 3.0);
                int active = Frames(r, bmp, canvas, 5.0);                                 // t = 3..8 s
                Frames(r, bmp, canvas, 2.0);                                              // t = 8..10 s
                int slowing = Frames(r, bmp, canvas, 20.0);                               // t = 10..30 s: slowing-down stretch
                Frames(r, bmp, canvas, 4.0);                                              // t = 30..34 s
                int rest = Frames(r, bmp, canvas, 20.0);                                  // t = 34..54 s: idle
                Assert.InRange(slowing, 20 * 30 * 0.15, 20 * 30 * 0.7);                   // the rate drops with the speed (half, half) and it keeps drawing while it moves
                Assert.Equal(0, rest);                                                    // stopped: not a single frame
                Assert.True(r.App.Context.Decor.Stopped);
                Assert.True(active > 100);
                // and nothing is left requesting frames for a long time
                Assert.Equal(0, Frames(r, bmp, canvas, 60.0));
                r.App.Dispose();
            }
        }

        [Fact]
        public void APressedKeyWakesTheInterfaceAtFullSpeedAndItStopsAgainAfterwards()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("category");
                Frames(r, bmp, canvas, 3.0);
                Frames(r, bmp, canvas, 45.0);
                Assert.Equal(0, Frames(r, bmp, canvas, 3.0));
                var screen = (CategoryScreen)r.App.Current;
                r.Platform.PressKey(UiKey.Down);
                int woke = Frames(r, bmp, canvas, 1.0);
                Assert.True(woke >= 25, "with movement it draws on every refresh: " + woke);   // focus moves: full frames
                Assert.Equal(1.0, r.App.Context.Decor.Rate, 6);                           // and the decoration recovers its speed
                int calm = Frames(r, bmp, canvas, 5.0);
                Assert.InRange(calm, 130, 195);                                           // then, 30 per s again
                Frames(r, bmp, canvas, 60.0);
                Assert.Equal(0, Frames(r, bmp, canvas, 10.0));                            // and stopped again after another 30 s without touching
                r.App.Dispose();
            }
        }

        [Fact]
        public void LoadingIndicatorsAreNeverSlowedDown()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("main", settle: false);                                      // a recent one is still being checked (loading spin)
                Frames(r, bmp, canvas, 1.0);
                Frames(r, bmp, canvas, 60.0);                                             // one minute without touching anything
                int f = Frames(r, bmp, canvas, 5.0);
                Assert.InRange(f, 140, 170);                                              // the spin continues at 30 per s
                Assert.Equal("loading", ((SectionScreen)r.App.Current).DemandReason);
                // when the checking ends, there is nothing left to move and it stops
                r.Checker.Settle(r.Catalog.Channels);
                Frames(r, bmp, canvas, 2.0);
                Assert.Equal(0, Frames(r, bmp, canvas, 5.0));
                r.App.Dispose();
            }
        }

        [Fact]
        public void TheCheckerIsToldWhenTheInterfaceRestsAndWhenItWakes()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("main");
                var rc = new RestChecker(r.Checker);
                r.Services.Checker = rc;
                Frames(r, bmp, canvas, 20.0);
                Assert.Empty(rc.Calls);                                                   // in use: nothing is notified
                Frames(r, bmp, canvas, 15.0);                                             // more than 30 s pass without touching the remote
                Assert.Equal(new[] { true }, rc.Calls.ToArray());                         // idle: only once
                Frames(r, bmp, canvas, 20.0);
                Assert.Equal(new[] { true }, rc.Calls.ToArray());
                r.Platform.PressKey(UiKey.Right);
                Frames(r, bmp, canvas, 0.2);
                Assert.Equal(new[] { true, false }, rc.Calls.ToArray());                  // a key press: activity
                r.App.Dispose();
            }
        }

        [Fact]
        public void TheOpenOnScreenKeyboardNoLongerDemandsFullFramesForever()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Rig("search");
                Frames(r, bmp, canvas, 1.0);
                r.Platform.PressKey(UiKey.Ok);                                            // the search field opens the keyboard
                Frames(r, bmp, canvas, 1.0);
                Assert.Contains("keyboard", r.App.Describe());
                int f = Frames(r, bmp, canvas, 4.0);
                Assert.InRange(f, 100, 130);                                              // before 60 per s (Full); now 30 (decoration)
                Frames(r, bmp, canvas, 40.0);
                Assert.Equal(0, Frames(r, bmp, canvas, 5.0));                             // and idle, stopped (the cursor stays on)
                r.App.Dispose();
            }
        }

        [Fact]
        public void ThePlayersErrorCardIsDrawnOnceAndThenNothingUntilItChanges()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Scenes.Build("player-loading", Fonts);                            // opening a channel: it has not started playing yet
                var pb = r.Playback.Created.Last();
                pb.Push(s => { s.Phase = PlayPhase.Error; s.Error = "Sin fuentes por ahora"; s.ErrorIsFatal = false; s.Peers = 0; });
                Frames(r, bmp, canvas, 2.0);                                              // the card is drawn
                Assert.Equal(0, Frames(r, bmp, canvas, 10.0));                            // "Sin fuentes por ahora": before, 30 frames per s the whole time the retry lasted
                pb.Push(s => { s.Phase = PlayPhase.Error; s.Error = "Sin fuentes por ahora"; s.Retry++; });
                Assert.True(Frames(r, bmp, canvas, 1.0) >= 1);                            // a state change draws it again
                Assert.Equal(0, Frames(r, bmp, canvas, 5.0));
                pb.Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 3; });
                Assert.True(Frames(r, bmp, canvas, 2.0) >= 1);                            // and when the stream arrives, the screen changes
                r.App.Dispose();
            }
        }

        [Fact]
        public void ThePausedPanelIsOnlyDecorationSoItRestsToo()
        {
            using (var bmp = new SKBitmap(192, 108)) using (var canvas = new SKCanvas(bmp))
            {
                var r = Scenes.Build("player-paused", Fonts);
                Frames(r, bmp, canvas, 2.0);
                int f = Frames(r, bmp, canvas, 4.0);
                Assert.True(f > 60, "just paused, the ring and the pulse continue: " + f);
                Frames(r, bmp, canvas, 40.0);
                Assert.Equal(0, Frames(r, bmp, canvas, 5.0));                             // long pause: nothing requests frames
                r.App.Dispose();
            }
        }

        [Fact]
        public void TheFluidRingLooksTheSameAsBeforeWhileTheRemoteIsInUse()
        {
            // same phase of the fluid border with the decoration's clock as with real time: the capture with movement does not change
            using (var a = new SKBitmap(480, 270)) using (var b = new SKBitmap(480, 270))
            {
                var r1 = Rig("category");
                var r2 = Rig("category");
                r2.App.Context.Decor.CalmAfter = 1e9; r2.App.Context.Decor.RestAfter = 2e9;     // never slows down
                foreach (var r in new[] { r1, r2 }) { r.Advance(2.0); }
                using (var c1 = new SKCanvas(a)) { c1.Clear(SKColors.Transparent); r1.App.NeedsFrame(r1.Now + 1.0 / 60); r1.App.Draw(c1, null, r1.Now + 1.0 / 60, 480, 270); }
                using (var c2 = new SKCanvas(b)) { c2.Clear(SKColors.Transparent); r2.App.NeedsFrame(r2.Now + 1.0 / 60); r2.App.Draw(c2, null, r2.Now + 1.0 / 60, 480, 270); }
                var pa = a.Bytes; var pb = b.Bytes;
                Assert.Equal(pa.Length, pb.Length);
                int diff = 0; for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) diff++;
                Assert.Equal(0, diff);
                r1.App.Dispose(); r2.App.Dispose();
            }
        }
    }
}
