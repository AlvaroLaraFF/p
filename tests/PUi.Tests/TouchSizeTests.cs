using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PUi.Core;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>
    /// Touch target sizes on a 6.5 inch phone in portrait (1080x2400, density 2.625), a 10 inch tablet in landscape and in portrait. The designs' boxes are built for a finger (44 CSS pixels, which are
    /// logical units here, about 7 mm): every box that reacts to a touch is at least 44 units each way or, if it is smaller (the sort button, a chip), its touch zone is enlarged to 7 mm and what stops it
    /// is another target (or the edge of what can be touched), never empty space.
    /// </summary>
    public class TouchSizeTests
    {
        private readonly ITestOutputHelper _out;
        public TouchSizeTests(ITestOutputHelper output) { _out = output; }

        /// <summary>How far the target answers a touch along a line through the center of its box (logical units): from and to.</summary>
        private static void Extent(IReadOnlyList<TouchTarget> all, TouchTarget t, float min, SKRect canvas, bool horizontal, out float lo, out float hi, out bool loBlocked, out bool hiBlocked)
        {
            float cx = (t.Rect.Left + t.Rect.Right) / 2, cy = (t.Rect.Top + t.Rect.Bottom) / 2;
            Func<float, TouchTarget> at = d => horizontal ? TouchHit.Find(all, cx + d, cy, min, canvas) : TouchHit.Find(all, cx, cy + d, min, canvas);
            lo = 0; hi = 0; loBlocked = hiBlocked = false;
            const float limit = 600;
            for (float d = 1; d <= limit; d++) { if (at(d) == t) hi = d; else { hiBlocked = Blocked(t, horizontal ? cx + d : cx, horizontal ? cy : cy + d, at(d), canvas); break; } if (d == limit) hiBlocked = true; }
            for (float d = 1; d <= limit; d++) { if (at(-d) == t) lo = d; else { loBlocked = Blocked(t, horizontal ? cx - d : cx, horizontal ? cy : cy - d, at(-d), canvas); break; } if (d == limit) loBlocked = true; }
        }

        /// <summary>The end of a target's reach is fine if another target took the space, or the point is outside the canvas or outside where the target can be touched at all.</summary>
        private static bool Blocked(TouchTarget t, float x, float y, TouchTarget other, SKRect canvas)
        {
            if (other != null) return true;
            if (!canvas.Contains(x, y)) return true;
            if (t.Clip.HasValue && !t.Clip.Value.Contains(x, y)) return true;
            return false;
        }

        private static SKRect CanvasOf(ScreenMetrics m) { return new SKRect(0, 0, m.CanvasLogicalWidth, m.CanvasLogicalHeight); }

        private static readonly string[] SceneNames = { "main", "category", "search", "search-results", "settings", "player", "player-paused" };

        private static IEnumerable<KeyValuePair<string, Scenes.Rig>> Screens(params Device[] devices)
        {
            foreach (var d in devices)
                foreach (var s in SceneNames)
                    yield return new KeyValuePair<string, Scenes.Rig>(d.Name + " " + s, Scenes.Build(s, Fonts, d));
        }

        [Fact]
        public void NoTouchTargetIsLimitedByEmptySpaceBelowSevenMillimetres()
        {
            var report = new StringBuilder();
            var bad = new List<string>();
            foreach (var kv in Screens(Device.Phone, Device.Tablet, Device.TabletPortrait))
            {
                var r = kv.Value;
                var m = r.Platform.Screen;
                float min = TouchSizes.MinTargetLogical(m);
                var canvas = CanvasOf(m);
                Assert.InRange(TouchSizes.LogicalToMm(m, min), 6.99f, 7.01f);
                var all = r.App.Current.TouchTargets();
                Assert.NotEmpty(all);
                foreach (var t in all)
                {
                    if (t.Id == "panel" || t.Id == "kb") continue;                  // they only hold other targets: nothing to aim at
                    float xl, xh, yl, yh; bool xlb, xhb, ylb, yhb;
                    Extent(all, t, min, canvas, true, out xl, out xh, out xlb, out xhb);
                    Extent(all, t, min, canvas, false, out yl, out yh, out ylb, out yhb);
                    float w = xl + xh, h = yl + yh;
                    float wmm = TouchSizes.LogicalToMm(m, w), hmm = TouchSizes.LogicalToMm(m, h);
                    float drawnW = TouchSizes.LogicalToMm(m, t.Rect.Width), drawnH = TouchSizes.LogicalToMm(m, t.Rect.Height);
                    string line = string.Format("{0,-24} {1,-30} drawn {2,4:0.0} x {3,4:0.0} mm   touch {4,5:0.0} x {5,4:0.0} mm{6}", kv.Key, t.Id.Length > 30 ? t.Id.Substring(0, 30) : t.Id, drawnW, drawnH, wmm, hmm,
                        (wmm < 6.95f || hmm < 6.95f) ? "   (limited by its neighbours)" : "");
                    report.AppendLine(line);
                    if (wmm < 6.95f && !(xlb && xhb)) bad.Add(kv.Key + " " + t.Id + ": " + wmm.ToString("0.0") + " mm wide and not stopped by a neighbour");
                    if (hmm < 6.95f && !(ylb && yhb)) bad.Add(kv.Key + " " + t.Id + ": " + hmm.ToString("0.0") + " mm tall and not stopped by a neighbour");
                }
            }
            _out.WriteLine(report.ToString());
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void EveryBoxThatReactsToATouchIsAtLeastFortyFourCssPixelsEachWay()
        {
            // 44 CSS px of the design = 44 logical units = about 7 mm on these devices. The panel (a band that only keeps the controls up) and the keys of PUi's own keyboard (ten keys across a
            // phone cannot be 44 wide; a real touch device has a system keyboard) are not buttons to aim at.
            var bad = new List<string>();
            foreach (var kv in Screens(Device.Phone, Device.Tablet, Device.TabletPortrait, Device.PhoneLandscape))
                foreach (var t in kv.Value.App.Current.TouchTargets())
                {
                    if (t.Id == "panel" || t.Id == "kb" || t.Id.StartsWith("key:")) continue;
                    float w = t.Rect.Width, h = t.Rect.Height;
                    if (t.Clip.HasValue)
                    {
                        var vis = TouchHit.Intersect(t.Rect, t.Clip.Value);
                        if (vis.Height < h - 0.5f || vis.Width < w - 0.5f) continue;            // cut by the edge of the list: only its visible part is touchable, the box itself is whole
                    }
                    if (Math.Min(w, h) < 43.5f) bad.Add(kv.Key + " " + t + " is " + w.ToString("0.#") + " x " + h.ToString("0.#"));
                }
            Assert.True(bad.Count == 0, string.Join("\n", bad.Distinct()));
        }

        [Fact]
        public void TheSmallDrawingsOfThePlayerHaveAFingerSizedTarget()
        {
            // the "live" badge is drawn 24 tall on the page and 30 over the video, the sort button of a list is a 44 circle: their targets are 44 tall
            var pg = Scenes.Build("player", Fonts, Device.Phone);
            Assert.True(TouchRig.Target(pg, "live").Rect.Height >= 43.5f);
            var ov = Scenes.Build("player", Fonts, Device.Tablet);
            var m = ov.Platform.Screen; float min = TouchSizes.MinTargetLogical(m);
            var live = TouchRig.Target(ov, "live");
            Assert.True(live.Rect.Height >= 43.5f);
            Assert.Equal("live", TouchHit.Find(ov.App.Current.TouchTargets(), (live.Rect.Left + live.Rect.Right) / 2, live.Rect.Bottom - 2, min, CanvasOf(m)).Id);
            var cat = Scenes.Build("category", Fonts, Device.Phone);
            Assert.True(TouchRig.Target(cat, "sort").Rect.Height >= 43.5f);
        }

        [Fact]
        public void TheZoneSizeDependsOnTheScreenNotOnTheDesign()
        {
            // a bigger, sharper screen needs fewer logical units for 7 mm; the TV (no touch) never asks
            var phone = new ScreenMetrics(1080, 2400, new ScreenInsets(0, 118, 0, 63), 2.625f, true);
            var tablet = new ScreenMetrics(2560, 1600, default(ScreenInsets), 2.0f, true);
            var tabletP = new ScreenMetrics(1600, 2560, default(ScreenInsets), 2.0f, true);
            Assert.InRange(TouchSizes.MinTargetLogical(phone), 41f, 43f);          // 44 CSS px is a little more than 7 mm here, so the design's 44 is already enough
            Assert.InRange(TouchSizes.MinTargetLogical(tablet), 43.5f, 44.5f);     // 1 unit = 1 dp: 44 CSS px is exactly 7 mm
            Assert.InRange(TouchSizes.MinTargetLogical(tabletP), 43.5f, 44.5f);
            Assert.True(TouchSizes.Slop(phone) > 6 && TouchSizes.Slop(phone) < 12);
            Assert.Equal(7f, TouchSizes.LogicalToMm(phone, TouchSizes.MinTargetLogical(phone)), 2);
        }

        [Fact]
        public void TheDrawingOfAScreenDoesNotChangeBecauseOfTheDensityOnlyTheTouchZonesDo()
        {
            var lo = new Device { Name = "lo", Width = 1080, Height = 2400, Insets = new ScreenInsets(0, 118, 0, 63), Density = 2.0f, Capabilities = Device.TouchOnly };
            var hi = new Device { Name = "hi", Width = 1080, Height = 2400, Insets = new ScreenInsets(0, 118, 0, 63), Density = 3.0f, Capabilities = Device.TouchOnly };
            var a = Scenes.Build("category", Fonts, lo);
            var b = Scenes.Build("category", Fonts, hi);
            var aRows = ((CategoryScreen)a.App.Current).Model.Visible.Select(v => v.Item.R).ToList();
            var bRows = ((CategoryScreen)b.App.Current).Model.Visible.Select(v => v.Item.R).ToList();
            Assert.Equal(aRows, bRows);
            Assert.NotEqual(TouchSizes.MinTargetLogical(a.Platform.Screen), TouchSizes.MinTargetLogical(b.Platform.Screen));
        }
    }
}
