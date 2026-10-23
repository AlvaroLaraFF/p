using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using PUi.Core;
using PUi.Input;
using PUi.Testing;
using SkiaSharp;

namespace PDesktop
{
    /// <summary>
    /// `pdesk --idle-profile`: how many frames the interface asks for at rest and what each part costs. Builds the app with fake data on the requested screen and
    /// simulates the platform loop in simulated time: a screen tick every 1/60 s (the TV's Ecore animator), NeedsFrame on every tick and Draw when it asks for it. Rest = no
    /// key during --seconds; with --keys keys are pressed every --key-ms (movement). Measures per simulated second: ticks, drawn frames, CPU time in NeedsFrame (the logic) and in
    /// Draw (Skia on CPU, 1920x1080: order of magnitude, the TV draws with GPU), allocated bytes. The time of Evas and of the TV's compositor cannot be measured here: it is proportional to the
    /// drawn frames (on the TV, ~16 ms of CPU per frame according to the [stats] lines of 2026-10-17).
    /// </summary>
    internal static class IdleProfile
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static int Run(string[] args)
        {
            string screen = Arg(args, "--screen", "category");
            double seconds = double.Parse(Arg(args, "--seconds", "60"), Inv);
            double settle = double.Parse(Arg(args, "--settle", "3"), Inv);
            double hz = double.Parse(Arg(args, "--hz", "60"), Inv);
            string keys = Arg(args, "--keys", null);
            double keyMs = double.Parse(Arg(args, "--key-ms", "400"), Inv);
            double idleAfterKeys = double.Parse(Arg(args, "--rest-after", "0"), Inv);
            string label = Arg(args, "--label", screen);
            int w = int.Parse(Arg(args, "--w", "1920"), Inv), h = int.Parse(Arg(args, "--h", "1080"), Inv);
            string fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
            var rig = Scenes.Build(screen, fonts);
            rig.Platform.PixelWidth = w; rig.Platform.PixelHeight = h;
            // by default the check has already finished (list open for a while: nothing is "checking"); with --checking the channels stay checking as in the design scenario
            if (Array.IndexOf(args, "--checking") < 0) rig.Checker.Settle(rig.Catalog.Channels);
            var app = rig.App;
            var keyList = new List<UiKey>();
            if (keys != null) foreach (var k in keys.Split(',')) keyList.Add((UiKey)Enum.Parse(typeof(UiKey), k.Trim(), true));
            Console.WriteLine("idle-profile '" + label + "': screen " + screen + " (" + app.Describe() + "), " + seconds + " s at " + hz + " ticks/s" + (keys != null ? ", keys " + keys + " every " + keyMs + " ms" : ", no keys"));

            using (var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bmp))
            {
                double now = 0, dt = 1.0 / hz;
                // settling: sprites and precomputed background (computed in the background, in real time); these frames do not count
                for (int phase = 0; phase < 2; phase++)
                {
                    double until = now + settle;
                    while (now < until) { now += dt; if (app.NeedsFrame(now)) { canvas.Clear(SKColors.Transparent); app.Draw(canvas, null, now, w, h); } }
                    if (phase == 0) System.Threading.Thread.Sleep(2500);
                }
                // after settling, the "time since the last key" counts from now
                double t0 = now;
                long ticks = 0, frames = 0;
                double updMs = 0, drawMs = 0;
                long allocUpd = 0, allocDraw = 0;
                var perSec = new List<string>();
                long sTicks = 0, sFrames = 0; double sUpd = 0, sDraw = 0; long sAlloc = 0;
                double nextKey = t0 + keyMs / 1000.0; int keyIdx = 0; double nextSec = t0 + 1;
                var wall = Stopwatch.StartNew();
                int gc0 = GC.CollectionCount(0);
                var framesByDemand = new Dictionary<string, long>();
                while (now - t0 < seconds)
                {
                    now += dt; ticks++; sTicks++;
                    if (keys != null && keyList.Count > 0 && now >= nextKey && (idleAfterKeys <= 0 || now - t0 < idleAfterKeys))
                    {
                        rig.Platform.PressKey(keyList[keyIdx++ % keyList.Count]);
                        nextKey += keyMs / 1000.0;
                    }
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    long t1 = Stopwatch.GetTimestamp();
                    bool need = app.NeedsFrame(now);
                    double u = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    updMs += u; sUpd += u; allocUpd += a1 - a0; sAlloc += a1 - a0;
                    if (need)
                    {
                        long t2 = Stopwatch.GetTimestamp();
                        canvas.Clear(SKColors.Transparent);
                        app.Draw(canvas, null, now, w, h);
                        double d = (Stopwatch.GetTimestamp() - t2) * 1000.0 / Stopwatch.Frequency;
                        long a2 = GC.GetAllocatedBytesForCurrentThread();
                        drawMs += d; sDraw += d; allocDraw += a2 - a1; sAlloc += a2 - a1;
                        frames++; sFrames++;
                    }
                    if (now >= nextSec)
                    {
                        perSec.Add(((int)(now - t0)).ToString(Inv).PadLeft(3) + " s  ticks " + sTicks.ToString().PadLeft(3) + "  frames " + sFrames.ToString().PadLeft(3) + "  logic " + sUpd.ToString("0.00", Inv).PadLeft(6) + " ms  draw " + sDraw.ToString("0.0", Inv).PadLeft(7) + " ms  allocated " + (sAlloc / 1024).ToString().PadLeft(6) + " KiB");
                        sTicks = 0; sFrames = 0; sUpd = 0; sDraw = 0; sAlloc = 0; nextSec += 1;
                    }
                }
                double sim = now - t0;
                // 10 seconds spread over the run are printed
                int step = Math.Max(1, perSec.Count / 10);
                for (int i = 0; i < perSec.Count; i += step) Console.WriteLine("  " + perSec[i]);
                Console.WriteLine("RESULT " + label + ": " + F(frames / sim) + " frames/s (of " + F(ticks / sim, "0") + " ticks/s), logic " + F(updMs / sim) + " ms/s (" + F(updMs / ticks, "0.000") + " ms per tick), draw " + F(drawMs / sim) + " ms/s ("
                    + (frames > 0 ? F(drawMs / frames) : "-") + " ms per frame), UI thread CPU on the PC " + F((updMs + drawMs) / sim / 10.0) + " % of a core; allocated " + F((allocUpd + allocDraw) / 1024.0 / sim, "0") + " KiB/s ("
                    + (frames > 0 ? F(allocDraw / 1024.0 / frames, "0.0") : "-") + " KiB per frame, " + F(allocUpd / 1024.0 / ticks, "0.00") + " KiB per tick); gen0 per s " + F((GC.CollectionCount(0) - gc0) / sim, "0.00") + "; reason=" + ((app.Current as PUi.Screens.SectionScreen) != null ? ((PUi.Screens.SectionScreen)app.Current).DemandReason : "-") + " decor=" + F(app.Context.Decor.Rate, "0.00") + "; screen: " + app.Describe());
            }
            rig.App.Dispose();
            return 0;
        }

        private static string F(double v, string fmt = "0.0") { return v.ToString(fmt, Inv); }

        private static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }
    }
}
