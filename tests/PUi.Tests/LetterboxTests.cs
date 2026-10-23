using System;
using System.Collections.Generic;
using System.Linq;
using PUi.Core;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using SkiaSharp;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>Screens that are not 16:9 and zones the system covers: the 1920x1080 canvas centered inside the safe area, the background on the whole surface.</summary>
    public class LetterboxTests
    {
        // ------------------------------------------------------------------ the numbers

        [Fact]
        public void ATvOrAnyPlainSixteenByNineSurfaceIsFilledEdgeToEdgeAsAlways()
        {
            foreach (var size in new[] { new[] { 1920, 1080 }, new[] { 1280, 720 }, new[] { 3840, 2160 }, new[] { 1366, 768 } })
            {
                var m = new ScreenMetrics(size[0], size[1]);
                Assert.False(m.HasBars);
                Assert.Equal(size[0] / 1920f, m.Scale);                    // exactly what the interface always used
                Assert.Equal(0f, m.CanvasLeft); Assert.Equal(0f, m.CanvasTop);
                Assert.Equal((float)size[0], m.CanvasWidth); Assert.Equal((float)size[1], m.CanvasHeight);
                Assert.Equal(0f, m.SurfaceLeft); Assert.Equal(0f, m.SurfaceTop);
            }
        }

        [Fact]
        public void APhoneInLandscapeGetsTheCanvasScaledToTheHeightAndCentered()
        {
            var m = new ScreenMetrics(2400, 1080);
            Assert.True(m.HasBars);
            Assert.Equal(1f, m.Scale);
            Assert.Equal(240f, m.CanvasLeft); Assert.Equal(0f, m.CanvasTop);
            Assert.Equal(1920f, m.CanvasWidth); Assert.Equal(1080f, m.CanvasHeight);
            Assert.Equal(-240f, m.SurfaceLeft); Assert.Equal(2160f, m.SurfaceRight);
            Assert.Equal(2400f, m.LogicalWidth); Assert.Equal(1080f, m.LogicalHeight);
            var ultra = new ScreenMetrics(2520, 1080);                      // 21:9
            Assert.Equal(1f, ultra.Scale); Assert.Equal(300f, ultra.CanvasLeft);
            var tall = new ScreenMetrics(2340, 1080);                       // 19.5:9
            Assert.Equal(210f, tall.CanvasLeft);
        }

        [Fact]
        public void ATabletOfAnotherShapeGetsBarsAboveAndBelow()
        {
            var m = new ScreenMetrics(2560, 1600);                          // 16:10
            Assert.Equal(2560f / 1920f, m.Scale, 5);
            Assert.Equal(0f, m.CanvasLeft); Assert.Equal(80f, m.CanvasTop); Assert.Equal(1440f, m.CanvasHeight);
            Assert.True(m.HasBars);
            Assert.Equal(-60f, m.SurfaceTop, 1); Assert.Equal(1140f, m.SurfaceBottom, 1);
        }

        [Fact]
        public void TheCanvasNeverGoesUnderANotchOrTheGestureBar()
        {
            var cases = new[]
            {
                new ScreenMetrics(2400, 1080, new ScreenInsets(132, 0, 0, 48)),
                new ScreenMetrics(2400, 1080, new ScreenInsets(0, 0, 132, 48)),
                new ScreenMetrics(2400, 1080, new ScreenInsets(90, 24, 90, 60)),
                new ScreenMetrics(2520, 1080, new ScreenInsets(120, 0, 0, 48)),
                new ScreenMetrics(1920, 1080, new ScreenInsets(100, 0, 0, 0)),      // a 16:9 phone with a cutout is not "plain" any more
                new ScreenMetrics(2000, 1080, new ScreenInsets(220, 0, 0, 0)),      // a narrow screen where the cutout reaches into the centered canvas
                new ScreenMetrics(2560, 1600, new ScreenInsets(0, 48, 0, 96)),
            };
            foreach (var m in cases)
            {
                var i = m.SafeInsets;
                Assert.True(m.CanvasLeft >= i.Left - 0.5f, m + ": left");
                Assert.True(m.CanvasTop >= i.Top - 0.5f, m + ": top");
                Assert.True(m.CanvasLeft + m.CanvasWidth <= m.PixelWidth - i.Right + 0.5f, m + ": right");
                Assert.True(m.CanvasTop + m.CanvasHeight <= m.PixelHeight - i.Bottom + 0.5f, m + ": bottom");
                Assert.True(m.HasBars, m.ToString());
            }
            // the notch only moves the canvas when the centered one would reach under it
            var wide = new ScreenMetrics(2400, 1080, new ScreenInsets(132, 0, 0, 0));
            Assert.Equal(240f, wide.CanvasLeft);                              // it already cleared the notch: centered
            var narrow = new ScreenMetrics(2000, 1080, new ScreenInsets(220, 0, 0, 0));
            Assert.Equal(220f, narrow.CanvasLeft);                            // pushed against the safe edge
        }

        [Fact]
        public void PixelsAndLogicalCoordinatesConvertBothWaysFromTheCanvasCorner()
        {
            var m = new ScreenMetrics(2400, 1080, new ScreenInsets(132, 0, 0, 48), 2.53f);
            Assert.Equal(0f, m.ToLogicalX(m.CanvasLeft), 3);
            Assert.Equal(1920f, m.ToLogicalX(m.CanvasLeft + m.CanvasWidth), 2);
            Assert.Equal(540f, m.ToLogicalY(m.CanvasTop + m.CanvasHeight / 2), 2);
            Assert.True(m.ToLogicalX(10) < 0);                                // the bar left of the canvas
            foreach (float lx in new[] { 0f, 333.3f, 960f, 1920f })
                Assert.Equal(lx, m.ToLogicalX(m.ToPixelX(lx)), 2);
            // the safe insets in logical units still measure from the surface edges
            Assert.Equal((int)Math.Round(132 / m.Scale), m.SafeInsetsLogical.Left);
        }

        // ------------------------------------------------------------------ drawing

        [Fact]
        public void ThePhoneDrawsItsBackgroundOnTheWholeSurfaceBarsAndNotchIncluded()
        {
            var r = Scenes.Build("main", Fonts, Device.Phone);
            using (var bmp = r.Render(r.Now + 0.2))
            {
                var m = r.Platform.Screen;
                // under the status bar and notch, under the gesture bar and at the far sides: opaque background, never transparent
                foreach (var p in new[] { new[] { 10, 50 }, new[] { 540, 20 }, new[] { 540, 2390 }, new[] { 1070, 1200 }, new[] { 1070, 2390 } })
                    Assert.Equal(255, bmp.GetPixel(p[0], p[1]).Alpha);
                // and it is the lights of the background (not flat): the lit bottom right corner is not the purple of the top left
                Assert.NotEqual(bmp.GetPixel(1070, 2390), bmp.GetPixel(10, 50));
                Assert.True(m.CanvasTop >= 118);
            }
        }

        [Fact]
        public void EveryTouchTargetOfEveryScreenSitsInsideTheSafeAreaOfEveryDevice()
        {
            foreach (var device in new[] { Device.Phone, Device.Tablet, Device.TabletPortrait, Device.PhoneLandscape })
            foreach (var scene in new[] { "main", "category", "search", "search-results", "settings", "player", "player-paused", "player-loading" })
            {
                var r = Scenes.Build(scene, Fonts, device);
                var m = r.Platform.Screen;
                var safe = new SKRect(m.SafeInsets.Left, m.SafeInsets.Top, m.PixelWidth - m.SafeInsets.Right, m.PixelHeight - m.SafeInsets.Bottom);
                foreach (var t in r.App.Current.TouchTargets())
                {
                    var visible = t.Clip.HasValue ? TouchHit.Intersect(t.Rect, t.Clip.Value) : t.Rect;
                    if (visible.Width <= 0 || visible.Height <= 0) continue;                          // entirely off the screen: nothing to touch
                    var px = new SKRect(m.ToPixelX(visible.Left), m.ToPixelY(visible.Top), m.ToPixelX(visible.Right), m.ToPixelY(visible.Bottom));       // the touchable part: a card slid half off the screen is only touchable where it shows
                    string what = device.Name + " " + scene + " " + t;
                    Assert.True(visible.Left >= -0.5f && visible.Top >= -0.5f, what);
                    Assert.True(px.Left >= safe.Left - 1 && px.Right <= safe.Right + 1 && px.Top >= safe.Top - 1 && px.Bottom <= safe.Bottom + 1, what + " is under a covered area: " + px + " safe " + safe);
                }
            }
        }

        [Fact]
        public void APointerEventBuiltFromPixelsWithScreenToLogicalReachesTheTargetUnderTheFinger()
        {
            // what a shell does: pixel position -> Screen.ToLogicalX/Y -> PointerEvent
            var r = Phone();
            var m = r.Platform.Screen;
            var t = Target(r, "rail1");
            float px = m.ToPixelX(CenterX(t)), py = m.ToPixelY(CenterY(t));
            r.Platform.Touch(PointerAction.Down, m.ToLogicalX(px), m.ToLogicalY(py), 0, r.Now);
            r.Advance(r.Now + 0.1);
            r.Platform.Touch(PointerAction.Up, m.ToLogicalX(px), m.ToLogicalY(py), 0, r.Now);
            r.Advance(r.Now + 0.4);
            Assert.IsType<SearchScreen>(r.App.Current);
        }

        [Fact]
        public void ATouchInTheBarsHitsNothing()
        {
            var r = Phone();
            var m = r.Platform.Screen;
            string before = r.App.Describe();
            r.Tap(m.ToLogicalX(20), m.ToLogicalY(500));                      // under the notch
            r.Tap(m.ToLogicalX(2380), m.ToLogicalY(500));                    // right bar
            Assert.Equal(before, r.App.Describe());
        }

        [Fact]
        public void ChangingTheInsetsMovesTheCanvasWithoutRestartingAnything()
        {
            var r = Phone();
            var screen = r.App.Current;
            using (r.Render(r.Now + 0.1)) { }
            var before = r.App.Context.Surface;
            Assert.Equal(118f, before.CanvasTop);
            r.Platform.ChangeScreen(1080, 2400, new ScreenInsets(0, 160, 0, 90));
            r.Advance(r.Now + 0.3);
            using (r.Render(r.Now + 0.1)) { }
            var after = r.App.Context.Surface;
            Assert.Equal(160f, after.CanvasTop);
            Assert.NotEqual(before.CanvasHeight, after.CanvasHeight);
            Assert.Same(screen, r.App.Current);
        }

        [Fact]
        public void TheCanvasOnATabletIsTheSafeAreaAndTheBackgroundCoversTheBarsAboveAndBelow()
        {
            var r = Phone(null, Device.Tablet);
            using (var bmp = r.Render(r.Now + 0.2))
            {
                Assert.Equal(255, bmp.GetPixel(1000, 10).Alpha);
                Assert.Equal(255, bmp.GetPixel(1000, 1590).Alpha);
            }
            TapOn(r, "rail2");
            Assert.IsType<SettingsScreen>(r.App.Current);
        }

        [Fact]
        public void TheBackgroundImageFollowsTheCanvasPositionAndTheTvImageIsUnchanged()
        {
            int mTv, mPhone;
            using (var tv = PUi.Gfx.Ambient.Build(1920, 1080, out mTv))
            using (var tv2 = PUi.Gfx.Ambient.Build(1920, 1080, 1920 / 1920f, 0, 0, out mTv))
            {
                Assert.Equal(tv.GetPixel(900 + mTv, 500 + mTv), tv2.GetPixel(900 + mTv, 500 + mTv));
            }
            // the same light, moved by the canvas offset: the brightest teal near the lower right of the canvas follows it
            using (var wide = PUi.Gfx.Ambient.Build(2400, 1080, 1f, 240f, 0f, out mPhone))
            using (var flat = PUi.Gfx.Ambient.Build(1920, 1080, 1f, 0f, 0f, out mTv))
            {
                var a = flat.GetPixel(1500 + mTv, 800 + mTv);
                var b = wide.GetPixel(1740 + mPhone, 800 + mPhone);            // the same canvas point, 240 pixels to the right
                Assert.True(Math.Abs(a.Green - b.Green) <= 3 && Math.Abs(a.Blue - b.Blue) <= 3, a + " vs " + b);
            }
        }
    }
}
