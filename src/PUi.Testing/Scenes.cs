using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.App;
using PUi.Core;
using PUi.Demo;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using SkiaSharp;

namespace PUi.Testing
{
    /// <summary>A screen to render on: its size, what the system covers, its density and how it is driven.</summary>
    public sealed class Device
    {
        public string Name;
        public int Width = 1920, Height = 1080;
        public ScreenInsets Insets;
        public float Density = 1f;
        public PlatformCapabilities Capabilities = PlatformCapabilities.FullRemote;

        /// <summary>The Samsung TV: 1920x1080, a full remote, nothing covered.</summary>
        public static readonly Device Tv = new Device { Name = "tv" };
        /// <summary>A 6.5 inch phone in portrait: 1080x2400 (20:9, 405 dpi, density 2.625 = 411 x 914 dp) with the cutout and status bar at the top (118 px) and the gesture bar at the bottom (63 px). Touch only.</summary>
        public static Device Phone { get { return new Device { Name = "phone", Width = 1080, Height = 2400, Insets = new ScreenInsets(0, 118, 0, 63), Density = 2.625f, Capabilities = TouchOnly }; } }
        /// <summary>The same phone turned on its side (what the full screen player gets): 2400x1080, the cutout on the left (132 px) and the gesture bar at the bottom (48 px).</summary>
        public static Device PhoneLandscape { get { return new Device { Name = "phoneLandscape", Width = 2400, Height = 1080, Insets = new ScreenInsets(132, 0, 0, 48), Density = 2.625f, Capabilities = TouchOnly }; } }
        /// <summary>A 10 inch tablet in landscape (16:10, 2560x1600, density 2.0 = 1280 x 800 dp) with a status bar (48 px) and a gesture bar (32 px). Touch only.</summary>
        public static Device Tablet { get { return new Device { Name = "tablet", Width = 2560, Height = 1600, Insets = new ScreenInsets(0, 48, 0, 32), Density = 2.0f, Capabilities = TouchOnly }; } }
        /// <summary>The same tablet in portrait (1600x2560, 800 x 1280 dp).</summary>
        public static Device TabletPortrait { get { return new Device { Name = "tabletPortrait", Width = 1600, Height = 2560, Insets = new ScreenInsets(0, 48, 0, 32), Density = 2.0f, Capabilities = TouchOnly }; } }

        public static PlatformCapabilities TouchOnly { get { return PlatformCapabilities.TouchOnly; } }

        public static Device ByName(string name)
        {
            switch ((name ?? "tv").ToLowerInvariant())
            {
                case "tv": return Tv;
                case "phone": return Phone;
                case "phonelandscape": return PhoneLandscape;
                case "tablet": return Tablet;
                case "tabletportrait": return TabletPortrait;
            }
            throw new ArgumentException("unknown device: " + name + " (tv, phone, phoneLandscape, tablet, tabletPortrait)");
        }

        public bool HasTouch { get { return Capabilities != null && Capabilities.HasTouch; } }
    }

    /// <summary>Scenarios of the design's screens with fake data: builds the app, takes it to the state and draws a frame at instant t.</summary>
    public static class Scenes
    {
        public static readonly string[] All =
        {
            "main-loading", "main", "category-loading", "category", "search", "search-results", "settings", "settings-edit",
            "player-loading", "player", "player-paused", "player-buffering"
        };

        /// <summary>Extra scenes for touch devices (a finger held down, a list in the middle of a drag, the player's position bar being dragged...). Not valid on the TV.</summary>
        public static readonly string[] TouchOnly =
        {
            "category-press", "category-drag", "settings-minus", "search-ime", "player-scrub", "main-press", "main-focus", "player-fullscreen", "search-keyboard"
        };

        /// <summary>The scenes of "Enviar a la tele" (phone and tablet only): the sheet looking for devices, with devices (one that cannot play the channel), finding none, while sending, the player and the lists while sending, and the notices.</summary>
        public static readonly string[] CastScenes =
        {
            "cast-sheet-searching", "cast-sheet", "cast-sheet-checking", "cast-sheet-empty", "cast-sheet-sending", "cast-player-starting", "cast-player-sending", "cast-player-paused",
            "cast-pill", "cast-pill-starting", "cast-armed", "cast-notice"
        };

        public sealed class Rig
        {
            public DesktopPlatform Platform;
            public PApp App;
            public TvServices Services;
            /// <summary>The cast service of the "cast-*" scenes (and of any test that builds one).</summary>
            public FakeCast Cast;
            public FakeCatalog Catalog; public FakeChecker Checker; public FakeRecents Recents; public FakePlaybackFactory Playback;
            public double Now;

            public void Advance(double to, double step = 1.0 / 30)
            {
                while (Now < to) { Now = Math.Min(to, Now + step); App.NeedsFrame(Now); }
            }

            // ---- a finger, in logical coordinates, with the rig's clock as the event time
            public void Touch(PointerAction action, float x, float y, int id = 0) { Platform.Touch(action, x, y, id, Now); }

            /// <summary>Puts a finger down and leaves it there.</summary>
            public void FingerDown(float x, float y, double hold = 0.1) { Touch(PointerAction.Down, x, y); Advance(Now + hold); }
            public void FingerUp(float x, float y, double settle = 0.4) { Touch(PointerAction.Up, x, y); Advance(Now + settle); }

            /// <summary>A tap: down, a moment, up, and time for what follows to happen.</summary>
            public void Tap(float x, float y, double hold = 0.08, double settle = 0.5) { FingerDown(x, y, hold); FingerUp(x, y, settle); }

            /// <summary>Moves the finger in a straight line from where it is (it must be down at x0,y0) to x1,y1 in 'seconds', one event per frame; lifts it at the end if lift is true.</summary>
            public void Drag(float x0, float y0, float x1, float y1, double seconds, bool lift = true, double settle = 0.5)
            {
                FingerDown(x0, y0, 1.0 / 30);
                int n = Math.Max(2, (int)Math.Round(seconds * 30));
                for (int i = 1; i <= n; i++)
                {
                    Advance(Now + seconds / n);
                    float u = (float)i / n;
                    Touch(PointerAction.Move, x0 + (x1 - x0) * u, y0 + (y1 - y0) * u);
                }
                if (lift) { Touch(PointerAction.Up, x1, y1); Advance(Now + settle); }
            }

            public SKBitmap Render(double t)
            {
                Advance(t);
                var bmp = new SKBitmap(Platform.PixelWidth, Platform.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    App.NeedsFrame(t);
                    App.Draw(canvas, null, t, Platform.PixelWidth, Platform.PixelHeight);
                }
                return bmp;
            }
        }

        public static Rig NewRig(string fontsDir, Device device = null)
        {
            var rig = new Rig();
            rig.Platform = new DesktopPlatform(fontsDir);
            if (device != null)
            {
                rig.Platform.Capabilities = device.Capabilities;
                rig.Platform.PixelWidth = device.Width; rig.Platform.PixelHeight = device.Height;
                rig.Platform.Insets = device.Insets; rig.Platform.Density = device.Density;
            }
            rig.Services = FakeServices.Demo(out rig.Catalog, out rig.Checker, out rig.Recents, out rig.Playback);
            rig.App = new PApp(rig.Services, rig.Platform);
            return rig;
        }

        /// <summary>A tap on the picture of the player: the middle of the video (the TV design's 960,330 on a letterboxed canvas).</summary>
        private static void TapPicture(Rig r)
        {
            var m = r.Platform.Screen;
            if (m.IsCompact) r.Tap(m.CanvasLogicalWidth / 2, m.Kind == DeviceKind.TabletLandscape ? m.CanvasLogicalHeight / 2 - 60 : m.VideoHeightLogical / 2, 0.08, 0.6);
            else r.Tap(960, 330, 0.08, 0.6);
        }

        private static TouchTarget Probe(Rig r, Func<TouchTarget, bool> pick, int skip = 0)
        {
            return r.App.Current.TouchTargets().Where(pick).Skip(skip).First();
        }

        private static void BuildTouchState(Rig r, string name, FakeCatalog cat)
        {
            var m = r.Platform.Screen;
            switch (name)
            {
                case "main-press":
                    DesignRecents(r, true);
                    r.App.Start();
                    r.Advance(0.6);
                    if (m.IsCompact) { var t = Probe(r, x => x.Focus != null && x.Focus.Group == "grid", 1); r.FingerDown((t.Rect.Left + t.Rect.Right) / 2, (t.Rect.Top + t.Rect.Bottom) / 2, 0.15); }
                    else r.FingerDown(1000, 700, 0.15);                                  // on a category card
                    break;
                case "category-press":
                case "category-drag":
                    {
                        r.App.Start();
                        var list = name == "category-drag" ? cat.Channels.ToList() : cat.Categories.First(c => c.Name == "Liga de Campeones").Channels.ToList();
                        r.Checker.Default = LinkState.Checking;
                        int[] peers = { 130, 74, 41, 12, 6, 88, 23, 17, 9, 51, 33, 4 };
                        for (int i = 0; i < list.Count; i++) if (name == "category-drag" || i % 3 != 1) r.Checker.Set(list[i], LinkState.Good, peers[i % peers.Length]);
                        r.App.Push(new CategoryScreen("Liga de Campeones", list, Section.Tv));
                        r.Advance(1.0);
                        if (name == "category-press")
                        {
                            if (m.IsCompact) { var t = Probe(r, x => x.Focus != null && x.Focus.Group == "rows", 2); r.FingerDown((t.Rect.Left + t.Rect.Right) / 2, (t.Rect.Top + t.Rect.Bottom) / 2, 0.15); }
                            else r.FingerDown(960, 437, 0.15);        // on the third row
                        }
                        else
                        {
                            // a drag in progress: the list pulled up by two thirds of a screen, the finger still down
                            float x0 = m.IsCompact ? m.CanvasLogicalWidth / 2 : 960, y0 = m.IsCompact ? m.CanvasLogicalHeight - 160 : 800, step = m.IsCompact ? 30 : 45;
                            r.FingerDown(x0, y0, 1.0 / 30);
                            for (int i = 1; i <= 12; i++) { r.Advance(r.Now + 1.0 / 30); r.Touch(PointerAction.Move, x0, y0 - step * i); }
                            r.Advance(r.Now + 0.1);
                        }
                        break;
                    }
                case "search-keyboard":
                    {
                        // a touch device with no system keyboard: PUi's own, with the finger
                        r.Platform.TextInput = null;
                        r.App.Start();
                        r.Services.Search.AddRecentQuery("nba"); r.Services.Search.AddRecentQuery("dazn f1"); r.Services.Search.AddRecentQuery("liga");
                        r.App.GoSection(Section.Search);
                        r.Advance(0.5);
                        var field = r.App.Current.TouchTargets().First(t => t.Id == "field");
                        r.Tap((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Top + field.Rect.Bottom) / 2);
                        r.Advance(r.Now + 0.6);
                        break;
                    }
                case "search-ime":
                    {
                        r.App.Start();
                        r.Services.Search.AddRecentQuery("nba"); r.Services.Search.AddRecentQuery("dazn f1"); r.Services.Search.AddRecentQuery("liga");
                        r.App.GoSection(Section.Search);
                        r.Advance(0.5);
                        r.Checker.Default = LinkState.Checking;
                        var hits = r.Services.Search.Search("liga");
                        for (int i = 0; i < Math.Min(4, hits.Count); i++) r.Checker.Set(hits[i], LinkState.Good, 74 - i * 15);
                        var field = r.App.Current.TouchTargets().First(t => t.Id == "field");
                        r.Tap((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Top + field.Rect.Bottom) / 2);          // opens the system keyboard
                        ((FakeTextInput)r.Platform.TextInput).Type("liga");                                                 // and the user types
                        r.Advance(r.Now + 0.5);
                        break;
                    }
            }
        }

        /// <summary>A cast service with devices, installed in the rig. The second Chromecast cannot play the channel (audio MP2) and the television can play anything.</summary>
        public static FakeCast CastRig(Rig r, bool withDevices = true)
        {
            var cast = new FakeCast { IsAvailable = true };
            if (withDevices)
                cast.SetDevices(FakeCast.Chromecast("Salón"), FakeCast.Chromecast("Habitación"), FakeCast.Tv("[TV] Samsung Q6 Series (65)"));
            cast.CheckResult = (d, c) => d.Name == "Habitación" ? CastCheck.No("El Chromecast no reproduce el audio de este canal") : CastCheck.Ok();
            r.Cast = cast; r.App.Context.Cast = cast;
            return cast;
        }

        private static void WaitPhase(FakeCast cast, CastPhase phase)
        {
            System.Threading.SpinWait.SpinUntil(() => cast.Status.Phase == phase, 3000);
            System.Threading.Thread.Sleep(5);
        }

        private static PlayContext PlayerOpen(Rig r, FakeCatalog cat)
        {
            var pc = new PlayContext(cat.Categories.First(c => c.Name == "Liga de Campeones").Channels, 0);
            r.Checker.Set(pc.Peek(1), LinkState.Good, 74); r.Checker.Set(pc.Peek(2), LinkState.Good, 41); r.Checker.Set(pc.Peek(3), LinkState.Good, 12);
            r.App.Push(new PlayerScreen(pc));
            var pb = r.Playback.Created.Last();
            WaitStarted(pb);
            pb.Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 9; s.DownBytesPerSec = 410 * 1024; s.TimeshiftWindowMs = 178000; s.BehindLiveMs = 0; });
            r.Advance(r.Now + 0.2);
            return pc;
        }

        /// <summary>The "cast-*" scenes: the app is taken to the state with the cast interface (the fake service) in it.</summary>
        private static void BuildCastState(Rig r, string name, FakeCatalog cat)
        {
            var cast = CastRig(r, name != "cast-sheet-empty");
            r.App.Start();
            r.Advance(0.5);
            var ui = r.App.Context.CastUi;
            var liga = cat.Categories.First(c => c.Name == "Liga de Campeones").Channels;
            var salon = cast.Devices.Count > 0 ? cast.Devices[0] : null;
            switch (name)
            {
                case "cast-sheet-searching":
                    cast.SetDevices();
                    ui.Open(null);
                    r.Advance(r.Now + 0.6);
                    break;
                case "cast-sheet-empty":
                    ui.Open(null);
                    cast.Searching = false;
                    r.Advance(r.Now + 0.6);
                    break;
                case "cast-sheet":
                case "cast-sheet-checking":
                    {
                        var pc = PlayerOpen(r, cat);
                        if (name == "cast-sheet-checking") cast.CheckGate = new System.Threading.Tasks.TaskCompletionSource<bool>().Task;
                        ui.Open(pc.Current);
                        System.Threading.Thread.Sleep(50);
                        r.Advance(r.Now + 0.7);
                        break;
                    }
                case "cast-sheet-sending":
                    {
                        r.App.Push(new CategoryScreen("Liga de Campeones", liga.ToList(), Section.Tv));
                        r.Advance(r.Now + 0.5);
                        ui.Send(salon, liga[0]);
                        WaitPhase(cast, CastPhase.Sending);
                        r.Advance(r.Now + 0.3);
                        ui.Open(null);
                        System.Threading.Thread.Sleep(50);
                        r.Advance(r.Now + 0.7);
                        break;
                    }
                case "cast-player-starting":
                case "cast-player-sending":
                case "cast-player-paused":
                    {
                        PlayerOpen(r, cat);
                        if (name == "cast-player-starting") cast.StartGate = new System.Threading.Tasks.TaskCompletionSource<bool>().Task;
                        ui.Send(salon, liga[0]);
                        WaitPhase(cast, name == "cast-player-starting" ? CastPhase.Starting : CastPhase.Sending);
                        if (name == "cast-player-paused") cast.SetPaused(true);
                        r.Advance(r.Now + 0.6);
                        break;
                    }
                case "cast-pill":
                case "cast-pill-starting":
                    {
                        if (name == "cast-pill-starting") cast.StartGate = new System.Threading.Tasks.TaskCompletionSource<bool>().Task;
                        var list = liga.ToList();
                        r.Checker.Default = LinkState.Checking;
                        int[] peers = { 130, 74, 41, 12, 6, 88, 23, 17, 9, 51, 33, 4 };
                        for (int i = 0; i < list.Count; i++) r.Checker.Set(list[i], LinkState.Good, peers[i % peers.Length]);
                        r.App.Push(new CategoryScreen("Liga de Campeones", list, Section.Tv));
                        r.Advance(r.Now + 0.8);
                        ui.Send(cast.Devices[2], liga[1]);
                        WaitPhase(cast, name == "cast-pill-starting" ? CastPhase.Starting : CastPhase.Sending);
                        r.Advance(r.Now + 0.6);
                        break;
                    }
                case "cast-armed":
                    DesignRecents(r, true);
                    r.App.Context.CastUi.Open(null);
                    r.Advance(r.Now + 0.5);
                    {
                        var t = r.App.Context.CastUi.TouchTargets().First(x => x.Id == "device:" + salon.Id);
                        r.Tap((t.Rect.Left + t.Rect.Right) / 2, (t.Rect.Top + t.Rect.Bottom) / 2);
                    }
                    r.Advance(r.Now + 0.8);
                    break;
                case "cast-notice":
                    DesignRecents(r, true);
                    r.Advance(r.Now + 0.3);
                    cast.RaiseNotice("El envío ha terminado: se ha parado desde la tele");
                    r.Advance(r.Now + 0.7);
                    break;
            }
        }

        /// <summary>With --real: path of a real M3U (samples/mylinkspaste.m3u); the catalog is built with the real CatalogBuilder.</summary>
        public static string RealM3u;

        /// <summary>The scripted playback pushes the channel's name from a pool thread as soon as it runs: wait for that (so the scene does not depend on thread timing: the player's "behind live" clock starts when the first status is read).</summary>
        public static void WaitStarted(ScriptedPlayback pb)
        {
            System.Threading.SpinWait.SpinUntil(() => !string.IsNullOrEmpty(pb.Current.Name), 3000);
            System.Threading.Thread.Sleep(5);
        }

        private static Channel Find(FakeCatalog c, string name, int fallback = 0)
        {
            return c.Channels.FirstOrDefault(x => x.Name == name) ?? c.Channels[Math.Min(fallback, c.Channels.Count - 1)];
        }

        private static void DesignRecents(Rig r, bool liveStatuses)
        {
            var names = new[] { "M+ Liga de Campeones 1080p", "DAZN La Liga 1 FHD", "M+ Baloncesto 1080p", "Teledeporte 1080p", "Eurosport 2 1080p" };
            var ch = names.Select((n, i) => Find(r.Catalog, n, i * 7)).ToList();
            r.Recents.Set(ch);
            if (liveStatuses)
            {
                r.Checker.Set(ch[0], LinkState.Good, 73); r.Checker.Set(ch[1], LinkState.Good, 41); r.Checker.Set(ch[2], LinkState.Good, 9); r.Checker.Set(ch[3], LinkState.Good, 12);
                r.Checker.Set(ch[4], LinkState.Checking);
            }
            else r.Checker.Default = LinkState.Checking;
        }

        public static Rig Build(string name, string fontsDir, Device device = null)
        {
            PlayerScreen.MockVideo = true;
            var r = NewRig(fontsDir, device);
            bool touch = device != null && device.HasTouch;
            if (RealM3u != null)
            {
                var links = AceEngine.Sources.M3uParser.Parse(AceEngine.Sources.M3uParser.DecodeText(System.IO.File.ReadAllBytes(RealM3u)));
                r.Catalog.LoadFrom(CatalogBuilder.Build(links));
            }
            var cat = r.Catalog;
            switch (name)
            {
                case "main-loading":
                    {
                        DesignRecents(r, false);
                        var empty = new FakeCatalog { State = CatalogState.Loading };
                        r.Services.Catalog = empty;
                        r.Catalog = empty;
                        // the recent cards need their channels: they are stored in Recents, which already has them
                        r.App.Start();
                        break;
                    }
                case "main":
                    DesignRecents(r, true);
                    r.App.Start();
                    break;
                case "main-focus":
                    // a keyboard or a game pad on a phone or a tablet: the focus comes back with its fluid border, on a category card
                    DesignRecents(r, true);
                    r.App.Start();
                    r.Advance(0.6);
                    r.Platform.PressKey(UiKey.Down); r.Platform.PressKey(UiKey.Right);
                    r.Advance(r.Now + 0.8);
                    break;
                case "category-loading":
                case "category":
                    {
                        r.App.Start();
                        var liga = cat.Categories.First(c => c.Name == "Liga de Campeones").Channels;
                        if (name == "category-loading")
                        {
                            r.Services.Settings.OnlyLive = false;
                            r.Checker.Default = LinkState.Checking;
                            r.Checker.Set(liga[0], LinkState.Good, 130);
                            r.Checker.Set(liga[7], LinkState.Dead);
                        }
                        else
                        {
                            r.Checker.Default = LinkState.Checking;
                            int[] peers = { 130, 74, 41, 12, 6 };
                            r.Checker.Set(liga[0], LinkState.Good, 130); r.Checker.Set(liga[1], LinkState.Good, 74); r.Checker.Set(liga[2], LinkState.Good, 41);
                            r.Checker.Set(liga[3], LinkState.Good, 12); r.Checker.Set(liga[4], LinkState.Good, 6);
                            r.Checker.Set(liga[7], LinkState.Dead); r.Checker.Set(liga[8], LinkState.Dead); r.Checker.Set(liga[9], LinkState.Dead);
                            r.Checker.Set(liga[10], LinkState.Good, 2); r.Checker.Set(liga[11], LinkState.Good, 3);
                        }
                        r.App.Push(new CategoryScreen("Liga de Campeones", liga, Section.Tv));
                        break;
                    }
                case "search":
                    r.App.Start();
                    r.Services.Search.AddRecentQuery("nba"); r.Services.Search.AddRecentQuery("dazn f1"); r.Services.Search.AddRecentQuery("liga");
                    r.App.GoSection(Section.Search);
                    break;
                case "search-results":
                    {
                        r.App.Start();
                        r.Services.Search.AddRecentQuery("nba"); r.Services.Search.AddRecentQuery("dazn f1"); r.Services.Search.AddRecentQuery("liga");
                        r.App.GoSection(Section.Search);
                        r.Checker.Default = LinkState.Checking;
                        var hits = r.Services.Search.Search("liga");
                        int[] peers = { 74, 41, 28, 12 };
                        for (int i = 0; i < Math.Min(4, hits.Count); i++) r.Checker.Set(hits[i], LinkState.Good, peers[i]);
                        r.Platform.Type("liga");
                        break;
                    }
                case "settings":
                    DesktopSystemInfo.Fixed = new PUi.Platform.SystemInfoData { OsName = "Tizen", OsVersion = "5.5", Model = "QE65Q64T", Runtime = ".NET Core 3.0.0", MemoryFreeMiB = 412, MemoryTotalMiB = 1536, LocalIp = "192.168.0.20" };
                    r.App.Start();
                    r.App.GoSection(Section.Settings);
                    r.Advance(0.2);
                    if (!touch) r.Platform.PressKey(UiKey.Down);                        // UPnP, as in the design
                    break;
                case "settings-edit":
                case "settings-minus":
                    DesktopSystemInfo.Fixed = new PUi.Platform.SystemInfoData { OsName = "Tizen", OsVersion = "5.5", Model = "QE65Q64T", Runtime = ".NET Core 3.0.0", MemoryFreeMiB = 412, MemoryTotalMiB = 1536, LocalIp = "192.168.0.20" };
                    r.App.Start();
                    r.App.GoSection(Section.Settings);
                    r.Advance(0.2);
                    if (touch)
                    {
                        // a finger on the "+" of the language selector (the second one of the right column)
                        var plus = r.App.Current.TouchTargets().First(t => t.Id == "language:plus");
                        r.FingerDown((plus.Rect.Left + plus.Rect.Right) / 2, (plus.Rect.Top + plus.Rect.Bottom) / 2, 0.12);
                        break;
                    }
                    r.Platform.PressKey(UiKey.Right); r.Platform.PressKey(UiKey.Down); r.Platform.PressKey(UiKey.Ok);       // Language in edit mode
                    r.Platform.PressKey(UiKey.Right);
                    break;
                case "player-loading":
                case "player":
                case "player-fullscreen":
                case "player-paused":
                case "player-buffering":
                    {
                        r.App.Start();
                        var pc = new PlayContext(cat.Categories.First(c => c.Name == "Liga de Campeones").Channels, 0);
                        r.Checker.Set(pc.Peek(1), LinkState.Good, 74);
                        r.App.Push(new PlayerScreen(pc));
                        var pb = r.Playback.Created.Last();
                        WaitStarted(pb);
                        if (name == "player-loading") pb.Push(s => { s.Phase = PlayPhase.Buffering; s.BufferPercent = 5; s.Peers = 6; s.DownBytesPerSec = 410 * 1024; s.IsLive = true; });
                        else if (name == "player" || name == "player-fullscreen") pb.Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 9; s.DownBytesPerSec = 410 * 1024; s.UpBytesPerSec = 96 * 1024; s.TimeshiftWindowMs = 178000; s.BehindLiveMs = 0; });
                        else if (name == "player-paused") pb.Push(s => { s.Phase = PlayPhase.Paused; s.IsLive = true; s.Peers = 9; s.DownBytesPerSec = 410 * 1024; s.UpBytesPerSec = 96 * 1024; s.TimeshiftWindowMs = 178000; s.BehindLiveMs = 64000; });
                        else pb.Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.PlayerBufferPercent = 64; s.Seeds = 2; s.Peers = 9; s.TimeshiftWindowMs = 178000; });
                        r.Advance(0.1);
                        if (name == "player") { if (touch) TapPicture(r); else r.Platform.PressKey(UiKey.Ok); }          // OK, or a tap on the picture, shows the panel
                        if (name == "player-fullscreen")
                        {
                            // the phone's "Pantalla completa": the platform turns the screen on request (here the rig plays the shell) and the player draws over the whole picture
                            r.Platform.SimulateRotation = true;
                            r.Advance(r.Now + 0.3);
                            if (r.Platform.Screen.IsCompact && r.Platform.Screen.Orientation == ScreenOrientation.Portrait) r.App.Current.TouchTargets().First(t => t.Id == "fullscreen").Tap();
                            r.Advance(r.Now + 0.8);
                        }
                        break;
                    }
                case "player-scrub":
                    {
                        r.App.Start();
                        var pc = new PlayContext(cat.Categories.First(c => c.Name == "Liga de Campeones").Channels, 0);
                        r.Checker.Set(pc.Peek(1), LinkState.Good, 74);
                        r.App.Push(new PlayerScreen(pc));
                        WaitStarted(r.Playback.Created.Last());
                        r.Playback.Created.Last().Push(s => { s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 9; s.DownBytesPerSec = 410 * 1024; s.UpBytesPerSec = 96 * 1024; s.TimeshiftWindowMs = 178000; s.BehindLiveMs = 0; });
                        r.Advance(0.1);
                        TapPicture(r);                                                               // the panel
                        // the finger on the position bar, dragged to a third of the way along what is in memory
                        var tl = r.App.Current.TouchTargets().First(t => t.Id == "timeline");
                        float mid = (tl.Rect.Top + tl.Rect.Bottom) / 2;
                        r.FingerDown(tl.Rect.Right - 60, mid, 0.1);
                        for (int i = 1; i <= 8; i++) { r.Advance(r.Now + 1.0 / 30); r.Touch(PointerAction.Move, tl.Rect.Right - 60 - (tl.Rect.Width * 0.62f) * i / 8, mid); }
                        r.Advance(r.Now + 0.4);
                        break;
                    }
                case "category-press":
                case "category-drag":
                case "main-press":
                case "search-ime":
                case "search-keyboard":
                    BuildTouchState(r, name, cat);
                    break;
                default:
                    if (Array.IndexOf(CastScenes, name) >= 0) { BuildCastState(r, name, cat); break; }
                    throw new ArgumentException("unknown scene: " + name);
            }
            return r;
        }
    }
}
