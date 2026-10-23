using System;
using PUi.Platform;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The kind of device (TV, phone in portrait, tablet in portrait, tablet in landscape) and the logical canvas each one gets, decided by ScreenMetrics from touch and the physical size.</summary>
    public class DeviceKindTests
    {
        private static readonly ScreenInsets PhoneInsets = new ScreenInsets(0, 118, 0, 63), TabletInsets = new ScreenInsets(0, 48, 0, 32);

        [Fact]
        public void APhoneInPortraitGetsACanvasThatIs390UnitsWideInsideTheSafeArea()
        {
            var m = new ScreenMetrics(1080, 2400, PhoneInsets, 2.625f, true);
            Assert.Equal(DeviceKind.PhonePortrait, m.Kind);
            Assert.True(m.IsPhone); Assert.True(m.IsCompact);
            Assert.Equal(390f, m.CanvasLogicalWidth);
            Assert.Equal(1080f / 390f, m.Scale, 4);
            Assert.Equal(0f, m.CanvasLeft); Assert.Equal(118f, m.CanvasTop);
            Assert.Equal(1080f, m.CanvasWidth); Assert.Equal(2400f - 118 - 63, m.CanvasHeight);
            Assert.Equal((2400f - 118 - 63) / m.Scale, m.CanvasLogicalHeight, 2);             // as tall as the safe area needs
            Assert.Equal(-118f / m.Scale, m.SurfaceTop, 3);                                    // the status bar is above the canvas
            Assert.True(m.HasBars);
        }

        [Fact]
        public void ATabletGetsTheDesignSizeInBothOrientations()
        {
            var land = new ScreenMetrics(2560, 1600, TabletInsets, 2.0f, true);
            Assert.Equal(DeviceKind.TabletLandscape, land.Kind); Assert.False(land.IsPhone);
            Assert.Equal(1280f, land.CanvasLogicalWidth); Assert.Equal(2f, land.Scale);
            Assert.Equal((1600f - 48 - 32) / 2f, land.CanvasLogicalHeight, 2);
            var port = new ScreenMetrics(1600, 2560, TabletInsets, 2.0f, true);
            Assert.Equal(DeviceKind.TabletPortrait, port.Kind);
            Assert.Equal(800f, port.CanvasLogicalWidth); Assert.Equal(2f, port.Scale);
            Assert.Equal((2560f - 80) / 2f, port.CanvasLogicalHeight, 2);
        }

        [Fact]
        public void TheSmallerSideUnder600DpMakesAPhoneAndTheLimitIsExact()
        {
            Assert.Equal(DeviceKind.TabletPortrait, new ScreenMetrics(1200, 1920, default(ScreenInsets), 2.0f, true).Kind);          // 600 dp
            Assert.Equal(DeviceKind.PhonePortrait, new ScreenMetrics(1196, 1920, default(ScreenInsets), 2.0f, true).Kind);           // 598 dp
            Assert.Equal(DeviceKind.PhonePortrait, new ScreenMetrics(720, 1600, default(ScreenInsets), 2.0f, true).Kind);
            Assert.Equal(DeviceKind.TabletPortrait, new ScreenMetrics(1440, 2960, default(ScreenInsets), 2.0f, true).Kind);          // a foldable opened: 720 dp
            Assert.Equal(DeviceKind.PhonePortrait, new ScreenMetrics(1440, 3120, default(ScreenInsets), 3.5f, true).Kind);           // a dense 6.7 inch phone: 411 dp
        }

        [Fact]
        public void APhoneTurnedOnItsSideGetsTheLandscapeLayoutWithOneUnitPerDp()
        {
            // what the full screen player gets: the tablet's landscape layout, on a canvas as wide as the screen is in dp
            var m = new ScreenMetrics(2400, 1080, new ScreenInsets(132, 0, 0, 48), 2.625f, true);
            Assert.Equal(DeviceKind.TabletLandscape, m.Kind);
            Assert.True(m.IsPhone);
            float dp = (2400f - 132) / 2.625f;
            Assert.InRange(m.CanvasLogicalWidth, dp - 1, dp + 1);
            Assert.Equal(2.625f, m.Scale, 2);
            Assert.InRange(m.CanvasLogicalWidth, 640f, 1280f);
            // a tiny landscape screen never gets a canvas narrower than 640 units
            Assert.True(new ScreenMetrics(1000, 500, default(ScreenInsets), 2.0f, true).CanvasLogicalWidth >= 640f);
        }

        [Fact]
        public void WithoutTouchTheDeviceIsATvWhateverItsSizeOrDensity()
        {
            foreach (var m in new[]
            {
                new ScreenMetrics(1920, 1080, default(ScreenInsets), 2.0f, false),       // an Android TV at 1080p reports density 2: 540 dp, still a TV
                new ScreenMetrics(3840, 2160, default(ScreenInsets), 3.0f, false),
                new ScreenMetrics(1920, 1080),
                new ScreenMetrics(2400, 1080, new ScreenInsets(100, 0, 0, 0)),
            })
            {
                Assert.Equal(DeviceKind.Tv, m.Kind);
                Assert.False(m.IsCompact); Assert.False(m.IsPhone);
                Assert.Equal(1920f, m.CanvasLogicalWidth); Assert.Equal(1080f, m.CanvasLogicalHeight);
            }
            // and touch on a big screen of density 1 is not a TV: a tablet's layout
            Assert.Equal(DeviceKind.TabletLandscape, new ScreenMetrics(1920, 1080, default(ScreenInsets), 1f, true).Kind);
        }

        [Fact]
        public void TheCanvasOfAPhoneOrTabletStartsAtTheSafeAreaAndPointerCoordinatesFollowIt()
        {
            var m = new ScreenMetrics(1080, 2400, PhoneInsets, 2.625f, true);
            Assert.Equal(0f, m.ToLogicalX(m.CanvasLeft), 3); Assert.Equal(0f, m.ToLogicalY(m.CanvasTop), 3);
            Assert.Equal(390f, m.ToLogicalX(m.CanvasLeft + m.CanvasWidth), 2);
            Assert.True(m.ToLogicalY(10) < 0);                                                  // a touch in the status bar is above the canvas
            foreach (float lx in new[] { 0f, 123.4f, 390f }) Assert.Equal(lx, m.ToLogicalX(m.ToPixelX(lx)), 2);
            foreach (float ly in new[] { 0f, 400f, m.CanvasLogicalHeight }) Assert.Equal(ly, m.ToLogicalY(m.ToPixelY(ly)), 2);
        }

        [Fact]
        public void ALogicalRectangleBecomesPixelsOfTheSurface()
        {
            var m = new ScreenMetrics(1080, 2400, PhoneInsets, 2.625f, true);
            var r = m.ToPixels(0, 0, 390, 390f * 9 / 16);
            Assert.Equal(0, r.Left); Assert.Equal(118, r.Top); Assert.Equal(1080, r.Right);
            Assert.Equal((int)Math.Round(118 + 390f * 9 / 16 * m.Scale), r.Bottom);
            Assert.Equal(1080, r.Width);
        }

        [Fact]
        public void TheMetricsDifferWhenOnlyTheTouchFlagDiffers()
        {
            Assert.NotEqual(new ScreenMetrics(1920, 1080, default(ScreenInsets), 1f, true), new ScreenMetrics(1920, 1080, default(ScreenInsets), 1f, false));
            Assert.Equal(new ScreenMetrics(1080, 2400, PhoneInsets, 2.625f, true), new ScreenMetrics(1080, 2400, PhoneInsets, 2.625f, true));
        }
    }
}
