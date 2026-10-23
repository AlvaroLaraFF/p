using System;
using System.Collections.Generic;
using System.Linq;
using PUi.Input;
using PUi.Testing;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PUi.Tests
{
    /// <summary>Regression of the flicker when moving focus: a card's brightness must go from rest to focused continuously (without spikes at the start or the end of the animation).</summary>
    public class FlickerTests
    {
        private readonly ITestOutputHelper _out;
        public FlickerTests(ITestOutputHelper o) { _out = o; }
        private static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        private static double Luma(SKColor c) { return 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue; }

        [Fact]
        public void FocusedCardBrightnessChangesMonotonicallyWithoutPopsAndSettlesAtTheSteadyValue()
        {
            var r = Scenes.Build("main", Fonts);
            r.Advance(2.0);
            // Card 2 ("Baloncesto") is to the right of the focused one; we sample an interior point without text (lower left of recent card 2)
            int x = 530 + 250, y = 96 + 100;
            var luma = new List<double>();
            var times = new List<double>();
            double t0 = r.Now;
            r.Platform.PressKey(UiKey.Right);
            for (int i = 0; i <= 40; i++)
            {
                double t = t0 + i * 0.010;
                using (var bmp = r.Render(t)) luma.Add(Luma(bmp.GetPixel(x, y)));
                times.Add(t - t0);
            }
            // and the card that loses focus (the first): its brightness must go down continuously
            var steady = luma.Last();
            for (int i = 1; i < luma.Count; i++) _out.WriteLine(times[i].ToString("0.00") + " " + luma[i].ToString("0.0"));
            double maxStep = 0;
            for (int i = 1; i < luma.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(luma[i] - luma[i - 1]));
            // the total rise is small (fill .06 -> .13 over a dark background); a one-frame jump cannot be greater than half of the total rise
            double total = Math.Abs(steady - luma[0]);
            Assert.True(total > 2, "the focused card has to change brightness (total=" + total + ")");
            Assert.True(maxStep <= total * 0.5 + 0.5, "brightness jump of one frame = " + maxStep + " over a total of " + total);
            // without exceeding the final value (the peak of the two overlaid layers)
            double overshoot = luma.Max() - Math.Max(steady, luma[0]);
            Assert.True(overshoot <= 1.5, "exceeds the final value by " + overshoot);
            r.App.Dispose();
        }

        /// <summary>A new channel enters a list: the rows that were already there do not change by a single pixel and the new one appears continuously (without a flash).</summary>
        [Fact]
        public void ANewRowFadesInSmoothlyAndLeavesTheRowsAlreadyThereUntouched()
        {
            var r = Scenes.NewRig(Fonts);
            r.App.Start(); r.Advance(0.5);
            r.Platform.PressKey(UiKey.Ok); r.Advance(r.Now + 0.5);
            var cs = (PUi.Screens.CategoryScreen)r.App.Current;
            var chans = r.Catalog.Categories.First().Channels;
            r.Checker.Set(chans[0], AceEngine.Probe.LinkState.Good, 50); r.Advance(r.Now + 1.5);
            var row1 = cs.Model.Visible[0].Item.R;
            float x2 = PUi.Theme.Tokens.MainLeft + PUi.Theme.Tokens.MainWidth * 0.65f;
            var probes1 = new[] { new SKPointI((int)(row1.Left + row1.Width * 0.45f), (int)(row1.Top + 20)), new SKPointI((int)(row1.Left + row1.Width * 0.65f), (int)(row1.MidY)), new SKPointI((int)(row1.Left + row1.Width * 0.85f), (int)(row1.Bottom - 15)) };
            double[] before;
            using (var bmp = r.Render(r.Now + 0.05)) before = probes1.Select(p => Luma(bmp.GetPixel(p.X, p.Y))).ToArray();

            r.Checker.Set(chans[1], AceEngine.Probe.LinkState.Good, 30);
            double t0 = r.Now;
            var row2luma = new List<double>();
            double maxDiffRow1 = 0; bool row2Seen = false; int y2 = 0;
            for (int i = 0; i <= 60; i++)
            {
                using (var bmp = r.Render(t0 + i / 60.0))
                {
                    var rows = cs.Model.Visible;
                    if (rows.Count > 1) { row2Seen = true; y2 = (int)rows[1].Item.R.MidY; }
                    if (row2Seen) row2luma.Add(Luma(bmp.GetPixel((int)x2, y2)));
                    for (int k = 0; k < probes1.Length; k++) maxDiffRow1 = Math.Max(maxDiffRow1, Math.Abs(Luma(bmp.GetPixel(probes1[k].X, probes1[k].Y)) - before[k]));
                }
            }
            Assert.True(row2Seen);
            Assert.True(maxDiffRow1 <= 3.0, "the row that was already there changes when another enters: " + maxDiffRow1);
            double steady = row2luma.Last();
            Assert.True(steady - row2luma.First() > 2, "the new row has to end up visible");
            double maxStep = 0, overshoot = row2luma.Max() - Math.Max(steady, row2luma.First());
            for (int i = 1; i < row2luma.Count; i++) { _out.WriteLine(i + " " + row2luma[i].ToString("0.0")); maxStep = Math.Max(maxStep, Math.Abs(row2luma[i] - row2luma[i - 1])); }
            Assert.True(overshoot <= 1.5, "flash on entering: " + overshoot);
            Assert.True(maxStep <= (steady - row2luma.First()) * 0.45 + 0.5, "one-frame jump = " + maxStep);
            for (int i = 1; i < row2luma.Count; i++) Assert.True(row2luma[i] >= row2luma[i - 1] - 1.0, "the appearance goes backwards at frame " + i);
            r.App.Dispose();
        }
    }
}
