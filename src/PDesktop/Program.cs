using System;
using System.IO;
using System.Linq;
using PUi.Core;
using PUi.Platform;
using PUi.Testing;
using SkiaSharp;

namespace PDesktop
{
    internal static class Program
    {
        /// <summary>App icon: the design's dark background with the purple and light blue lights and the "P!" logo (undistorted, rounded square).</summary>
        private static void MakeIcon(string path, int size)
        {
            string fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
            using (var fnt = new PUi.Gfx.Fonts(fonts)) using (var gx = new PUi.Gfx.Gx(fnt))
            using (var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul))
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                float k = size / 100f;
                c.Scale(k);
                var r = new SKRect(0, 0, 100, 100);
                c.Save();
                using (var rr = new SKRoundRect(r, 24, 24)) c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
                gx.FillRR(c, r, 0, SKColor.Parse("#070711"));
                float pad;
                using (var a = PUi.Gfx.Ambient.MakeBlob(70, 60, 22, SKColor.Parse("#7B3DFF"), out pad)) gx.DrawImage(c, a, new SKRect(-30 - pad, -25 - pad, 40 + pad, 35 + pad), 0.85f, SKFilterQuality.Medium);
                using (var b = PUi.Gfx.Ambient.MakeBlob(70, 55, 22, SKColor.Parse("#17CDB4"), out pad)) gx.DrawImage(c, b, new SKRect(50 - pad, 55 - pad, 120 + pad, 110 + pad), 0.75f, SKFilterQuality.Medium);
                float sz = 60, wP = gx.MeasureText("P", sz, 800, -2.4f), wB = gx.MeasureText("!", sz, 800, -2.4f);
                float x = 50 - (wP + wB) / 2, by = 50 + sz * 0.36f;
                gx.DrawText(c, "P", x, by, sz, 800, SKColor.Parse("#F3F5F8"), -2.4f);
                gx.DrawText(c, "!", x + wP, by, sz, 800, SKColor.Parse("#3FE8CF"), -2.4f);
                c.Restore();
                gx.StrokeRR(c, r, 24, 1, new SKColor(255, 255, 255, 40));
                using (var img = SKImage.FromBitmap(bmp)) using (var d = img.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path, d.ToArray());
            }
            Console.WriteLine("icon -> " + path);
        }

        /// <summary>Marks the places the interface keeps out of: the covered edges (red), the canvas (cyan outline: the TV canvas, or the safe area of a phone or tablet) and, with targets, the drawn box of every touch target (green) and its touch zone (yellow).</summary>
        private static void DrawGuides(SKBitmap bmp, Scenes.Rig rig, bool guides, bool targets)
        {
            var m = rig.Platform.Screen;
            using (var c = new SKCanvas(bmp))
            using (var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill })
            {
                if (guides)
                {
                    p.Color = new SKColor(255, 40, 40, 70);
                    var i = m.SafeInsets;
                    if (i.Left > 0) c.DrawRect(new SKRect(0, 0, i.Left, m.PixelHeight), p);
                    if (i.Right > 0) c.DrawRect(new SKRect(m.PixelWidth - i.Right, 0, m.PixelWidth, m.PixelHeight), p);
                    if (i.Top > 0) c.DrawRect(new SKRect(0, 0, m.PixelWidth, i.Top), p);
                    if (i.Bottom > 0) c.DrawRect(new SKRect(0, m.PixelHeight - i.Bottom, m.PixelWidth, m.PixelHeight), p);
                    p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 2; p.Color = new SKColor(0, 220, 255, 200);
                    c.DrawRect(new SKRect(m.CanvasLeft, m.CanvasTop, m.CanvasLeft + m.CanvasWidth, m.CanvasTop + m.CanvasHeight), p);
                }
                if (targets)
                {
                    float min = TouchSizes.MinTargetLogical(m);
                    var bounds = new SKRect(0, 0, m.CanvasLogicalWidth, m.CanvasLogicalHeight);
                    p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 1;
                    foreach (var t in rig.App.Current.TouchTargets())
                    {
                        var z = TouchHit.Zone(t, min, bounds);
                        p.Color = new SKColor(255, 220, 0, 200); c.DrawRect(Px(m, z), p);
                        p.Color = new SKColor(0, 255, 120, 230); c.DrawRect(Px(m, t.Rect), p);
                    }
                }
            }
        }

        private static SKRect Px(ScreenMetrics m, SKRect r) { return new SKRect(m.ToPixelX(r.Left), m.ToPixelY(r.Top), m.ToPixelX(r.Right), m.ToPixelY(r.Bottom)); }

        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--idle-profile") >= 0) return IdleProfile.Run(args);
            string outDir = "screens", which = "all";
            double t = 3.0;
            Device device = Device.Tv;
            bool guides = false, targets = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--device" && i + 1 < args.Length) device = Device.ByName(args[++i]);
                else if (args[i] == "--size" && i + 1 < args.Length)
                {
                    var wh = args[++i].ToLowerInvariant().Split('x');
                    device = Clone(device); device.Width = int.Parse(wh[0]); device.Height = int.Parse(wh[1]);
                }
                else if (args[i] == "--insets" && i + 1 < args.Length)
                {
                    var v = args[++i].Split(',');
                    device = Clone(device); device.Insets = new ScreenInsets(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]), int.Parse(v[3]));
                }
                else if (args[i] == "--density" && i + 1 < args.Length) { device = Clone(device); device.Density = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); }
                else if (args[i] == "--touch") { device = Clone(device); device.Capabilities = Device.TouchOnly; }
                else if (args[i] == "--guides") guides = true;
                else if (args[i] == "--targets") targets = true;
                else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (args[i] == "--screen" && i + 1 < args.Length) which = args[++i];
                else if (args[i] == "--real" && i + 1 < args.Length) Scenes.RealM3u = args[++i];
                else if (args[i] == "--t" && i + 1 < args.Length) t = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (args[i] == "--help")
                {
                    Console.WriteLine("pdesk [--out DIR] [--screen " + string.Join("|", Scenes.All) + "|all] [--t SECONDS]");
                    Console.WriteLine("      [--device tv|phone|phoneLandscape|tablet|tabletPortrait] [--size WxH] [--insets L,T,R,B] [--density D] [--touch]   render on another screen (phone: 1080x2400 in portrait with status bar and gesture bar, touch only; tablet: 2560x1600; tabletPortrait: 1600x2560)");
                    Console.WriteLine("      [--guides] [--targets]   mark the covered edges and the canvas / every touch target and its touch zone");
                    Console.WriteLine("      touch devices also have the scenes: " + string.Join(", ", Scenes.TouchOnly));
                    Console.WriteLine("      and those of \"Enviar a la tele\": " + string.Join(", ", Scenes.CastScenes));
                    return 0;
                }
            }
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--icon" && i + 1 < args.Length) { MakeIcon(args[i + 1], i + 2 < args.Length ? int.Parse(args[i + 2]) : 117); return 0; }
            Directory.CreateDirectory(outDir);
            string fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
            var names = which == "all" ? Scenes.All.Concat(device.HasTouch ? Scenes.TouchOnly.Concat(Scenes.CastScenes) : new string[0]).Concat(device.Name == "tv" && !device.HasTouch ? new[] { "states" } : new string[0]).ToArray() : which.Split(',');
            foreach (var n in names)
            {
                if (n == "states")
                {
                    var r0 = Scenes.NewRig(fonts);
                    r0.Platform.PixelWidth = 1920; r0.Platform.PixelHeight = 1600;
                    using (var sb = StatesSheet.Render(r0.App.Context, t)) using (var si = SKImage.FromBitmap(sb)) using (var sd = si.Encode(SKEncodedImageFormat.Png, 95))
                    { string sp = Path.Combine(outDir, "states.png"); File.WriteAllBytes(sp, sd.ToArray()); Console.WriteLine("states -> " + sp); }
                    r0.App.Dispose();
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rig = Scenes.Build(n, fonts, device.Name == "tv" && device.Width == 1920 && device.Height == 1080 && !device.HasTouch ? null : device);
                using (var bmp = rig.Render(t))
                {
                    if (guides || targets) DrawGuides(bmp, rig, guides, targets);
                    Save(bmp, outDir, n, rig, sw);
                }
                rig.App.Dispose();
            }
            return 0;
        }

        private static Device Clone(Device d)
        {
            return new Device { Name = d.Name == "tv" ? "custom" : d.Name, Width = d.Width, Height = d.Height, Insets = d.Insets, Density = d.Density, Capabilities = d.Capabilities };
        }

        private static void Save(SKBitmap bmp, string outDir, string n, Scenes.Rig rig, System.Diagnostics.Stopwatch sw)
        {
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 95))
            {
                string path = Path.Combine(outDir, n + ".png");
                File.WriteAllBytes(path, data.ToArray());
                Console.WriteLine(n + " -> " + path + " (" + sw.ElapsedMilliseconds + " ms) :: " + rig.App.Describe());
            }
        }
    }
}
