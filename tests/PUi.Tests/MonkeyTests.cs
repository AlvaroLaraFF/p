using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using AceEngine.Util;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>Fingers, keys, rotations and section changes at random (always the same ones) on every kind of device: nothing may throw (the interface catches what its screens throw and logs it: the test reads that log).</summary>
    public class MonkeyTests
    {
        [Fact]
        public void RandomFingersKeysRotationsAndScreensNeverBreakAnyDevice()
        {
            var errors = new List<string>();
            var old = AceLog.Sink;
            AceLog.Sink = (lvl, tag, msg) =>
            {
                if (lvl >= LogLevel.Warn && tag == "ui" && (msg.StartsWith("Update:") || msg.StartsWith("Draw ") || msg.StartsWith("key ") || msg.StartsWith("pointer ") || msg.Contains("Exception")))
                    lock (errors) errors.Add(msg.Length > 400 ? msg.Substring(0, 400) : msg);
                if (old != null) old(lvl, tag, msg);
            };
            try
            {
                foreach (var device in new[] { Device.Phone, Device.Tablet, Device.TabletPortrait, Device.PhoneLandscape })
                {
                    var rnd = new Random(7 + device.Width);
                    var r = Scenes.NewRig(Fonts, device);
                    r.Checker.Default = LinkState.Good;
                    var cast = new FakeCast(); cast.SetDevices(FakeCast.Chromecast("Salón"), FakeCast.Tv("Cocina")); cast.CheckResult = (d, c) => d.Kind == CastDeviceKind.Chromecast && c != null && c.Name.Length % 2 == 0 ? CastCheck.No("El Chromecast no reproduce el audio de este canal") : CastCheck.Ok(); r.App.Context.Cast = cast;
                    r.Platform.SimulateRotation = true;
                    r.App.Start(); r.Advance(0.5);
                    int inPlayer = 0, inCategory = 0, inSearch = 0, inSettings = 0, fullscreen = 0;
                    for (int step = 0; step < 450; step++)
                    {
                        var m = r.Platform.Screen;
                        float W = m.CanvasLogicalWidth, H = m.CanvasLogicalHeight;
                        int pick = rnd.Next(100);
                        if (pick < 35) r.Tap((float)(rnd.NextDouble() * (W + 20) - 10), (float)(rnd.NextDouble() * (H + 20) - 10), 0.05, 0.25);
                        else if (pick < 55)
                        {
                            float x0 = (float)(rnd.NextDouble() * W), y0 = (float)(rnd.NextDouble() * H), x1 = (float)(rnd.NextDouble() * W), y1 = (float)(rnd.NextDouble() * H);
                            r.Drag(x0, y0, x1, y1, 0.1 + rnd.NextDouble() * 0.4, lift: rnd.Next(5) != 0, settle: 0.2);
                            if (rnd.Next(5) == 0) { r.Touch(PointerAction.Cancel, x1, y1); r.Advance(r.Now + 0.1); }
                        }
                        else if (pick < 70)
                        {
                            var keys = new[] { UiKey.Up, UiKey.Down, UiKey.Left, UiKey.Right, UiKey.Ok, UiKey.Ok, UiKey.Back, UiKey.PlayPause, UiKey.Info, UiKey.ChannelUp, UiKey.Rewind, UiKey.Forward };
                            r.Platform.PressKey(keys[rnd.Next(keys.Length)]); r.Advance(r.Now + 0.15);
                        }
                        else if (pick < 77)
                        {
                            // turn the device (a phone's portrait and landscape surfaces are the same two shapes; insets as a phone has them)
                            bool land = m.PixelWidth > m.PixelHeight;
                            int w = Math.Min(device.Width, device.Height), h = Math.Max(device.Width, device.Height);
                            if (land) r.Platform.ChangeScreen(w, h, new ScreenInsets(0, 100, 0, 60)); else r.Platform.ChangeScreen(h, w, new ScreenInsets(120, 0, 0, 48));
                            r.Advance(r.Now + 0.4);
                        }
                        else if (pick < 85) { r.App.GoSection((Section)rnd.Next(3)); r.Advance(r.Now + 0.3); }
                        else if (pick < 90)
                        {
                            // the cast service: appears and goes, the sheet opens, a device is picked, the sending ends by itself or is stopped
                            switch (rnd.Next(6))
                            {
                                case 0: cast.IsAvailable = !cast.IsAvailable; break;
                                case 1: if (cast.IsAvailable && r.Platform.Screen.IsCompact) r.App.Context.CastUi.Open(r.App.Current is PlayerScreen ? ((PlayerScreen)r.App.Current).Context.Current : null); break;
                                case 2: if (cast.IsAvailable && r.App.Context.CastUi.IsOpen && r.Catalog.Channels.Count > 0) r.App.Context.CastUi.Send(cast.Devices[rnd.Next(2)], r.Catalog.Channels[rnd.Next(r.Catalog.Channels.Count)]); break;
                                case 3: cast.EndByItself("El envío ha terminado"); break;
                                case 4: cast.Stop(); break;
                                default: cast.Searching = !cast.Searching; break;
                            }
                            r.Advance(r.Now + 0.1);
                        }
                        else if (pick < 93) { r.Platform.Type("liga".Substring(0, rnd.Next(1, 5))); r.Advance(r.Now + 0.3); }
                        else if (pick < 96 && r.Playback.Created.Count > 0) { var pb = r.Playback.Created.Last(); var ph = new[] { PlayPhase.Playing, PlayPhase.Paused, PlayPhase.Buffering, PlayPhase.Error, PlayPhase.Connecting }[rnd.Next(5)]; pb.Push(s => { s.Phase = ph; s.IsLive = true; s.Peers = rnd.Next(20); s.TimeshiftWindowMs = 178000; s.BehindLiveMs = rnd.Next(100000); s.Error = ph == PlayPhase.Error ? "Sin fuentes por ahora" : null; }); r.Advance(r.Now + 0.3); }
                        else if (pick < 98) { r.App.OnPause(); r.Advance(r.Now + 0.2); r.App.OnResume(); r.Advance(r.Now + 0.2); }
                        else r.Advance(r.Now + rnd.NextDouble() * 1.5);
                        if (step % 9 == 0) using (var bmp = r.Render(r.Now + 0.05)) { }
                        var cur = r.App.Current;
                        if (cur is PlayerScreen) { inPlayer++; if (((PlayerScreen)cur).IsFullscreen) fullscreen++; } else if (cur is CategoryScreen) inCategory++; else if (cur is SearchScreen) inSearch++; else if (cur is SettingsScreen) inSettings++;
                        if (r.Platform.Exited) { r.Platform.Exited = false; }
                    }
                    Assert.True(inPlayer > 5 && inCategory > 5 && inSearch > 5 && inSettings > 5, device.Name + " did not visit every kind of screen: player " + inPlayer + " (full screen " + fullscreen + "), category " + inCategory + ", search " + inSearch + ", settings " + inSettings);
                    r.App.Dispose();
                }
            }
            finally { AceLog.Sink = old; }
            Assert.True(errors.Count == 0, errors.Count + " errors, the first:\n" + string.Join("\n---\n", errors.Take(3)));
        }
    }
}
