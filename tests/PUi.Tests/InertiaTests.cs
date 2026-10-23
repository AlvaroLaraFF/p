using System;
using PUi.Core;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The scroll physics on their own: following the finger, resistance past the ends, inertia, soft rebound, the speed of a flick.</summary>
    public class InertiaTests
    {
        private static Inertia Scroller(float min = 0, float max = 5000) { return new Inertia { Min = min, Max = max }; }

        private static void Run(Inertia s, ref double t, double seconds, double dt)
        {
            double end = t + seconds;
            while (t < end) { t = Math.Min(end, t + dt); s.Advance(t); }
        }

        [Fact]
        public void TheContentFollowsTheFingerExactlyInsideTheRange()
        {
            var s = Scroller();
            s.Begin(1000, 0);
            s.DragBy(120); s.DragBy(-20); s.DragBy(75);
            Assert.Equal(1175f, s.Position);
            Assert.True(s.Dragging && s.Active && !s.Coasting);
        }

        [Fact]
        public void PastAnEndTheFingerMovesItLessAndNeverBeyondTheResistanceLimit()
        {
            var s = Scroller();
            s.Begin(10, 0);
            s.DragBy(-50);                                                // 40 beyond the top
            Assert.InRange(s.Position, -40f, -25f);                       // resists
            s.DragBy(-5000);
            Assert.InRange(s.Position, -s.MaxOverscroll, -s.MaxOverscroll * 0.99f);
            s.DragBy(+5000 + 1000);                                       // coming back is continuous
            Assert.InRange(s.Position, 0f, 1000f);
        }

        [Fact]
        public void AFlickCoastsAboutSpeedOverFrictionAndStops()
        {
            var s = Scroller(0, 1e6f);
            s.Begin(0, 0); s.Release(2000, 0);
            double t = 0;
            Run(s, ref t, 10, 1.0 / 60);
            Assert.False(s.Active);
            Assert.InRange(s.Position, 2000 / s.Friction * 0.95f, 2000 / s.Friction * 1.02f);
        }

        [Fact]
        public void TheResultDoesNotDependOnTheFrameRate()
        {
            float[] end = new float[3]; int k = 0;
            foreach (double dt in new[] { 1.0 / 30, 1.0 / 60, 1.0 / 144 })
            {
                var s = Scroller(0, 1e6f);
                s.Begin(0, 0); s.Release(3000, 0);
                double t = 0; Run(s, ref t, 5, dt);
                end[k++] = s.Position;
            }
            Assert.InRange(Math.Abs(end[0] - end[1]), 0f, 2f);
            Assert.InRange(Math.Abs(end[1] - end[2]), 0f, 2f);
        }

        [Fact]
        public void AFlickIntoTheEndOvershootsALittleAndComesBackWithoutWobbling()
        {
            var s = Scroller(0, 1000);
            s.Begin(950, 0); s.Release(3000, 0);
            double t = 0; float peak = 0; bool wasAbove = false, reached = false; int crossings = 0;
            for (int i = 0; i < 360; i++)
            {
                t += 1.0 / 120; s.Advance(t);
                peak = Math.Max(peak, s.Position);
                bool above = s.Position > 1000.4f;
                if (above) reached = true;
                if (wasAbove && !above) crossings++;
                wasAbove = above;
                if (reached) Assert.True(s.Position >= 999.5f, "never undershoots the end once it got there: " + s.Position);
            }
            Assert.InRange(peak, 1001f, 1000 + s.MaxOverscroll);          // soft: a little past the end
            Assert.InRange(crossings, 0, 1);                              // critically damped: it does not swing back and forth
            Assert.Equal(1000f, s.Position);
            Assert.False(s.Active);
        }

        [Fact]
        public void LettingGoWhilePulledPastTheEndSpringsBackAtOnce()
        {
            var s = Scroller(0, 1000);
            s.Begin(0, 0); s.DragBy(-400);
            Assert.True(s.Overscrolled);
            s.Release(0, 0);
            Assert.True(s.Coasting);
            double t = 0; Run(s, ref t, 2, 1.0 / 60);
            Assert.Equal(0f, s.Position);
            Assert.False(s.Active);
        }

        [Fact]
        public void LiftingTheFingerAtRestInsideTheRangeDoesNotMoveAnything()
        {
            var s = Scroller();
            s.Begin(700, 0); s.DragBy(30); s.Release(0, 0.1);
            Assert.False(s.Active);
            Assert.Equal(730f, s.Position);
            var slow = Scroller();
            slow.Begin(700, 0); slow.Release(20, 0.1);                    // below the flick threshold
            Assert.False(slow.Active);
        }

        [Fact]
        public void ContentThatDoesNotScrollJustSticksToItsOnlyPosition()
        {
            var s = Scroller(0, 0);
            s.Begin(0, 0); s.Release(2500, 0);
            double t = 0; Run(s, ref t, 3, 1.0 / 60);
            Assert.Equal(0f, s.Position);
            Assert.False(s.Active);
        }

        [Fact]
        public void HaltStopsAFlickWhereItIs()
        {
            var s = Scroller(0, 1e6f);
            s.Begin(0, 0); s.Release(2000, 0);
            double t = 0; Run(s, ref t, 0.3, 1.0 / 60);
            float p = s.Position;
            Assert.True(p > 100 && s.Coasting);
            s.Halt(t);
            Run(s, ref t, 1, 1.0 / 60);
            Assert.Equal(p, s.Position);
            Assert.False(s.Active);
        }

        // ------------------------------------------------------------------ the speed of the finger

        [Fact]
        public void TheVelocityIsTheSpeedOverTheLastFewSamplesAndZeroIfTheFingerStopped()
        {
            var v = new VelocityTracker();
            for (int i = 0; i <= 10; i++) v.Add(i / 60.0, i * 20f);       // 1200 units per second
            Assert.InRange(v.Velocity(10 / 60.0), 1150f, 1250f);
            Assert.Equal(0f, v.Velocity(10 / 60.0 + 0.3));                // the finger rested before lifting
            var slowDown = new VelocityTracker();
            for (int i = 0; i <= 6; i++) slowDown.Add(i / 60.0, i * 40f);              // fast...
            for (int i = 7; i <= 14; i++) slowDown.Add(i / 60.0, 240f + (i - 6) * 2f);  // ...then nearly stopped: only the recent speed counts
            Assert.True(slowDown.Velocity(14 / 60.0) < 500f);
            var down = new VelocityTracker();
            for (int i = 0; i <= 8; i++) down.Add(i / 60.0, -i * 30f);
            Assert.InRange(down.Velocity(8 / 60.0), -1900f, -1700f);
            Assert.Equal(0f, new VelocityTracker().Velocity(1));
        }

        [Fact]
        public void TheVelocityIsCapped()
        {
            var v = new VelocityTracker();
            v.Add(0, 0); v.Add(0.01, 500); v.Add(0.02, 1000);
            Assert.Equal(7000f, v.Velocity(0.02));
        }
    }
}
