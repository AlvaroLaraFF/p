using System;
using System.Collections.Generic;
using PUi.Platform;
using Xunit;

namespace PAndroid.Tests
{
    public class ScreenTests
    {
        // ------------------------------------------------------------------ draw size

        [Theory]
        [InlineData(1920, 1080, 0, 1920, 1080)]
        [InlineData(1920, 1080, 1920, 1920, 1080)]
        [InlineData(1920, 1080, 2560, 1920, 1080)]       // never larger than the view
        [InlineData(3840, 2160, 1920, 1920, 1080)]       // a 4K screen draws the 1080p interface
        [InlineData(1920, 1080, 1280, 1280, 720)]
        [InlineData(2400, 1080, 1280, 1280, 576)]
        [InlineData(1080, 2400, 1280, 576, 1280)]        // portrait: the long side is the height
        [InlineData(0, 0, 1280, 1, 1)]
        public void TheDrawBufferKeepsTheShapeAndNeverGrows(int vw, int vh, int cap, int ew, int eh)
        {
            int w, h;
            ScreenMath.RenderSize(vw, vh, cap, out w, out h);
            Assert.Equal(ew, w); Assert.Equal(eh, h);
        }

        // ------------------------------------------------------------------ metrics

        [Fact]
        public void ATvDrawnAtItsOwnSizeFillsTheSurfaceWithTheCanvas()
        {
            var m = ScreenMath.Metrics(1920, 1080, 1920, 1080, ScreenInsets.None, 1f);
            Assert.Equal(1f, m.Scale, 4); Assert.Equal(0f, m.CanvasLeft); Assert.Equal(0f, m.CanvasTop); Assert.Equal(1920f, m.CanvasWidth);
            Assert.False(m.HasBars);
        }

        [Fact]
        public void APhoneGetsAnInsetAwareLetterboxedCanvas()
        {
            var m = ScreenMath.Metrics(2400, 1080, 2400, 1080, new ScreenInsets(100, 0, 0, 60), 2.625f);
            Assert.Equal(2400, m.PixelWidth); Assert.Equal(1080, m.PixelHeight);
            Assert.Equal(100, m.SafeInsets.Left); Assert.Equal(60, m.SafeInsets.Bottom);
            Assert.Equal(2.625f, m.Density, 3);
            Assert.Equal(ScreenOrientation.Landscape, m.Orientation);
            Assert.True(m.HasBars);
            // never under the notch (left 100) or the gesture bar (bottom 60)
            Assert.True(m.CanvasLeft >= 100); Assert.True(m.CanvasTop + m.CanvasHeight <= 1080 - 60 + 0.5f);
            Assert.Equal(1920f / 1080f, m.CanvasWidth / m.CanvasHeight, 3);
        }

        [Fact]
        public void InsetsAndDensityFollowTheBufferWhenTheViewIsDrawnSmaller()
        {
            // 3840x2160 view drawn on a 1920x1080 buffer: everything is in buffer pixels
            var m = ScreenMath.Metrics(3840, 2160, 1920, 1080, new ScreenInsets(80, 40, 80, 40), 2f);
            Assert.Equal(1920, m.PixelWidth); Assert.Equal(1080, m.PixelHeight);
            Assert.Equal(40, m.SafeInsets.Left); Assert.Equal(20, m.SafeInsets.Top); Assert.Equal(40, m.SafeInsets.Right); Assert.Equal(20, m.SafeInsets.Bottom);
            Assert.Equal(1f, m.Density, 3);
        }

        [Fact]
        public void BeforeTheFirstLayoutTheMetricsAreTheDesignSize()
        {
            var m = ScreenMath.Metrics(0, 0, 0, 0, ScreenInsets.None, 3f);
            Assert.Equal(1920, m.PixelWidth); Assert.Equal(1080, m.PixelHeight); Assert.True(m.SafeInsets.IsEmpty); Assert.Equal(3f, m.Density);
        }

        [Fact]
        public void RotationChangesTheOrientationAndTheMetricsAreComparable()
        {
            var a = ScreenMath.Metrics(2400, 1080, 2400, 1080, ScreenInsets.None, 2.625f);
            var b = ScreenMath.Metrics(2400, 1080, 2400, 1080, ScreenInsets.None, 2.625f);
            var c = ScreenMath.Metrics(1080, 2400, 1080, 2400, ScreenInsets.None, 2.625f);
            Assert.Equal(a, b); Assert.NotEqual(a, c);
            Assert.Equal(ScreenOrientation.Portrait, c.Orientation);
        }

        // ------------------------------------------------------------------ touch devices are laid out as phones and tablets

        [Fact]
        public void APhoneSurfaceWithTouchIsAPhoneInPortraitWhoseCanvasIsTheSafeArea()
        {
            // a motorola edge 60 neo: 1200x2670 at 450 dpi (density 2.8125), status bar 100 px, gesture bar 66 px
            var m = ScreenMath.Metrics(1200, 2670, 1200, 2670, new ScreenInsets(0, 100, 0, 66), 450 / 160f, touch: true);
            Assert.True(m.Touch);
            Assert.Equal(PUi.Platform.DeviceKind.PhonePortrait, m.Kind);
            Assert.True(m.IsPhone);
            Assert.Equal(390f, m.CanvasLogicalWidth, 3);
            Assert.Equal(100f, m.CanvasTop, 1); Assert.Equal(1200f, m.CanvasWidth, 1); Assert.Equal(2670f - 100 - 66, m.CanvasHeight, 1);
        }

        [Fact]
        public void TheSameSurfaceWithoutTouchIsATv()
        {
            var m = ScreenMath.Metrics(1200, 2670, 1200, 2670, ScreenInsets.None, 450 / 160f, touch: false);
            Assert.Equal(PUi.Platform.DeviceKind.Tv, m.Kind);
            Assert.Equal(1920f, m.CanvasLogicalWidth);
        }

        [Fact]
        public void APhoneTurnedForFullScreenGetsTheLandscapeLayout()
        {
            var m = ScreenMath.Metrics(2670, 1200, 2670, 1200, new ScreenInsets(100, 0, 0, 0), 450 / 160f, touch: true);
            Assert.Equal(PUi.Platform.DeviceKind.TabletLandscape, m.Kind);
            Assert.True(m.IsPhone);
        }

        [Fact]
        public void ATabletIsATabletInBothOrientations()
        {
            var portrait = ScreenMath.Metrics(1600, 2560, 1600, 2560, ScreenInsets.None, 2f, touch: true);
            var landscape = ScreenMath.Metrics(2560, 1600, 2560, 1600, ScreenInsets.None, 2f, touch: true);
            Assert.Equal(PUi.Platform.DeviceKind.TabletPortrait, portrait.Kind);
            Assert.Equal(PUi.Platform.DeviceKind.TabletLandscape, landscape.Kind);
        }

        [Fact]
        public void ADrawnSmallerSurfaceStillKnowsItIsAPhoneBecauseTheDensityFollowsTheBuffer()
        {
            // the view is 1200x2670 at density 2.8125 but the buffer is drawn at 600x1335: pixels and density halve together, so the size in dp does not change
            var m = ScreenMath.Metrics(1200, 2670, 600, 1335, new ScreenInsets(0, 100, 0, 66), 2.8125f, touch: true);
            Assert.Equal(PUi.Platform.DeviceKind.PhonePortrait, m.Kind);
            Assert.Equal(1.40625f, m.Density, 4);
            Assert.Equal(50, m.SafeInsets.Top);
        }

        // ------------------------------------------------------------------ the video rectangle (IVideoPlacement) in view pixels

        [Fact]
        public void TheVideoRectOfTheSurfaceIsTheSameRectInTheViewWhenTheyAreTheSameSize()
        {
            int l, t, w, h;
            ScreenMath.SurfaceRectToView(new PixelRect(0, 100, 1200, 775), 1200, 2670, 1200, 2670, out l, out t, out w, out h);      // 16:9 at the top of a phone's player
            Assert.Equal(0, l); Assert.Equal(100, t); Assert.Equal(1200, w); Assert.Equal(675, h);
        }

        [Fact]
        public void TheVideoRectScalesWhenTheSurfaceIsSmallerThanTheView()
        {
            int l, t, w, h;
            ScreenMath.SurfaceRectToView(new PixelRect(0, 50, 600, 387), 1200, 2670, 600, 1335, out l, out t, out w, out h);
            Assert.Equal(0, l); Assert.Equal(100, t); Assert.Equal(1200, w); Assert.Equal(674, h);
        }

        [Fact]
        public void TheWholeSurfaceIsTheWholeView()
        {
            int l, t, w, h;
            ScreenMath.SurfaceRectToView(new PixelRect(0, 0, 1280, 576), 2400, 1080, 1280, 576, out l, out t, out w, out h);
            Assert.Equal(0, l); Assert.Equal(0, t); Assert.Equal(2400, w); Assert.Equal(1080, h);
        }

        [Fact]
        public void AnUnknownViewLeavesTheRectAsItIs()
        {
            int l, t, w, h;
            ScreenMath.SurfaceRectToView(new PixelRect(10, 20, 110, 70), 0, 0, 0, 0, out l, out t, out w, out h);
            Assert.Equal(10, l); Assert.Equal(20, t); Assert.Equal(100, w); Assert.Equal(50, h);
        }

        // ------------------------------------------------------------------ touch

        private static List<PointerEvent> T(int action, double time, ScreenMetrics m, float sx, float sy, params TouchPoint[] pts)
        {
            var o = new List<PointerEvent>();
            TouchTranslator.Translate(action, pts, time, m, sx, sy, o);
            return o;
        }

        [Fact]
        public void ADownMoveUpOfOneFingerBecomesPointerEventsInCanvasCoordinates()
        {
            var m = ScreenMath.Metrics(2400, 1080, 2400, 1080, ScreenInsets.None, 2.625f);     // canvas 1920x1080 at x=240
            var down = T(TouchTranslator.ActionDown, 1.0, m, 1, 1, new TouchPoint(0, 1200, 540));
            Assert.Single(down);
            Assert.Equal(PointerAction.Down, down[0].Action); Assert.Equal(960f, down[0].X, 3); Assert.Equal(540f, down[0].Y, 3); Assert.Equal(0, down[0].Id); Assert.Equal(1.0, down[0].Time);
            var move = T(TouchTranslator.ActionMove, 1.016, m, 1, 1, new TouchPoint(0, 1260, 540));
            Assert.Equal(PointerAction.Move, move[0].Action); Assert.Equal(1020f, move[0].X, 3);
            var up = T(TouchTranslator.ActionUp, 1.2, m, 1, 1, new TouchPoint(0, 1260, 540));
            Assert.Equal(PointerAction.Up, up[0].Action);
        }

        [Fact]
        public void TheCanvasCornersAreTheLogicalCornersAndTheBarsAreOutsideThem()
        {
            var m = ScreenMath.Metrics(2400, 1080, 2400, 1080, ScreenInsets.None, 2.625f);
            var tl = T(TouchTranslator.ActionDown, 0, m, 1, 1, new TouchPoint(0, m.CanvasLeft, m.CanvasTop));
            Assert.Equal(0f, tl[0].X, 3); Assert.Equal(0f, tl[0].Y, 3);
            var br = T(TouchTranslator.ActionDown, 0, m, 1, 1, new TouchPoint(0, m.CanvasLeft + m.CanvasWidth, m.CanvasTop + m.CanvasHeight));
            Assert.Equal(1920f, br[0].X, 2); Assert.Equal(1080f, br[0].Y, 2);
            var bar = T(TouchTranslator.ActionDown, 0, m, 1, 1, new TouchPoint(0, 10, 500));
            Assert.True(bar[0].X < 0);
        }

        [Fact]
        public void AViewDrawnSmallerConvertsTheTouchThroughTheSurfaceScale()
        {
            int vw = 2400, vh = 1080, bw, bh;
            ScreenMath.RenderSize(vw, vh, 1280, out bw, out bh);
            var m = ScreenMath.Metrics(vw, vh, bw, bh, ScreenInsets.None, 2.625f);
            float sx = ScreenMath.SurfaceScale(vw, bw), sy = ScreenMath.SurfaceScale(vh, bh);
            var centre = T(TouchTranslator.ActionDown, 0, m, sx, sy, new TouchPoint(0, 1200, 540));         // the middle of the view
            Assert.Equal(960f, centre[0].X, 1); Assert.Equal(540f, centre[0].Y, 1);
            var corner = T(TouchTranslator.ActionDown, 0, m, sx, sy, new TouchPoint(0, 2160, 1080));        // the canvas' bottom-right corner in the view
            Assert.Equal(1920f, corner[0].X, 1); Assert.Equal(1080f, corner[0].Y, 1);
        }

        [Fact]
        public void ASecondFingerHasItsOwnIdAndTheMoveCoversBoth()
        {
            var m = ScreenMetrics.Tv;
            var fingers = new[] { new TouchPoint(0, 100, 100), new TouchPoint(7, 300, 200) };
            int second = TouchTranslator.ActionPointerDown | (1 << TouchTranslator.ActionPointerIndexShift);
            var d = T(second, 2.0, m, 1, 1, fingers);
            Assert.Single(d); Assert.Equal(PointerAction.Down, d[0].Action); Assert.Equal(7, d[0].Id); Assert.Equal(300f, d[0].X);
            var mv = T(TouchTranslator.ActionMove, 2.1, m, 1, 1, fingers);
            Assert.Equal(2, mv.Count); Assert.Equal(0, mv[0].Id); Assert.Equal(7, mv[1].Id);
            int firstUp = TouchTranslator.ActionPointerUp | (0 << TouchTranslator.ActionPointerIndexShift);
            var u = T(firstUp, 2.2, m, 1, 1, fingers);
            Assert.Single(u); Assert.Equal(PointerAction.Up, u[0].Action); Assert.Equal(0, u[0].Id);
        }

        [Fact]
        public void CancelEndsEveryFinger()
        {
            var c = T(TouchTranslator.ActionCancel, 3.0, ScreenMetrics.Tv, 1, 1, new TouchPoint(0, 1, 1), new TouchPoint(3, 2, 2));
            Assert.Equal(2, c.Count);
            Assert.All(c, e => Assert.Equal(PointerAction.Cancel, e.Action));
        }

        [Theory]
        [InlineData(7)]      // hover move
        [InlineData(8)]      // scroll
        [InlineData(9)]      // hover enter
        [InlineData(10)]     // hover exit
        [InlineData(4)]      // outside
        [InlineData(11)]     // button press
        [InlineData(12)]     // button release
        public void HoverScrollAndButtonActionsGiveNothing(int action)
        {
            Assert.Empty(T(action, 0, ScreenMetrics.Tv, 1, 1, new TouchPoint(0, 5, 5)));
        }

        [Fact]
        public void APointerIndexOutOfRangeIsIgnoredNotAnException()
        {
            Assert.Empty(T(TouchTranslator.ActionPointerDown | (5 << TouchTranslator.ActionPointerIndexShift), 0, ScreenMetrics.Tv, 1, 1, new TouchPoint(0, 5, 5)));
        }

        [Fact]
        public void SurfaceScaleIsOneWhenSomethingIsUnknown()
        {
            Assert.Equal(1f, ScreenMath.SurfaceScale(0, 1280)); Assert.Equal(1f, ScreenMath.SurfaceScale(2400, 0));
            Assert.Equal(0.5f, ScreenMath.SurfaceScale(2400, 1200), 4);
        }

        // ------------------------------------------------------------------ aspect fit

        [Theory]
        [InlineData(1920, 1080, 16f / 9, 0, 0, 1920, 1080)]
        [InlineData(1920, 1080, 4f / 3, 240, 0, 1440, 1080)]          // 4:3 picture: bars at the sides
        [InlineData(1920, 1080, 2.35f, 0, 131, 1920, 817)]            // scope: bars above and below
        [InlineData(2400, 1080, 16f / 9, 240, 0, 1920, 1080)]         // a phone: bars at the sides
        [InlineData(1920, 1080, 0f, 0, 0, 1920, 1080)]                // unknown shape: fill
        [InlineData(0, 0, 1.78f, 0, 0, 0, 0)]
        public void TheVideoKeepsItsShapeInsideItsContainer(int cw, int ch, float aspect, int l, int t, int w, int h)
        {
            int left, top, width, height;
            AspectFit.Compute(cw, ch, aspect, out left, out top, out width, out height);
            Assert.Equal(l, left); Assert.Equal(t, top); Assert.Equal(w, width); Assert.Equal(h, height);
        }

        [Fact]
        public void TheVideoIsFittedInsideTheCanvasRectangleNotTheWholeScreen()
        {
            int left, top, width, height;
            AspectFit.ComputeIn(240, 0, 1920, 1080, 4f / 3, out left, out top, out width, out height);       // a 4:3 picture in the canvas of a phone
            Assert.Equal(240 + 240, left); Assert.Equal(0, top); Assert.Equal(1440, width); Assert.Equal(1080, height);
            AspectFit.ComputeIn(240, 10, 1920, 1080, 0f, out left, out top, out width, out height);          // unknown shape: the whole canvas
            Assert.Equal(240, left); Assert.Equal(10, top); Assert.Equal(1920, width); Assert.Equal(1080, height);
        }
    }
}
