using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Core;
using PUi.Input;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;
using SkiaSharp;
using Xunit;
using static PUi.Tests.TouchRig;

namespace PUi.Tests
{
    /// <summary>
    /// "Enviar a la tele" on a phone and a tablet, against a fake cast service: the button, the sheet with the devices that were found, whether each one can play the channel, the sending state
    /// of the player, the strip over the lists, a sending that ends by itself, a channel chosen while sending; and that the TV is left as it was.
    /// </summary>
    public class CastUiTests
    {
        private static CastUi Ui(Scenes.Rig r) { return r.App.Context.CastUi; }

        private static FakeCast WithCast(Scenes.Rig r, bool devices = true)
        {
            var cast = Scenes.CastRig(r, devices);
            r.Advance(r.Now + 0.2);
            return cast;
        }

        private static TouchTarget UiTarget(Scenes.Rig r, string id)
        {
            var t = Ui(r).TouchTargets().FirstOrDefault(x => x.Id == id);
            if (t == null) throw new InvalidOperationException("no cast target '" + id + "'; there are: " + string.Join(", ", Ui(r).TouchTargets().Select(x => x.Id)));
            return t;
        }

        private static bool UiHas(Scenes.Rig r, string id) { return Ui(r).TouchTargets().Any(x => x.Id == id); }

        private static void TapUi(Scenes.Rig r, string id) { var t = UiTarget(r, id); r.Tap(CenterX(t), CenterY(t)); }

        private static void WaitUntil(Func<bool> cond, string what)
        {
            Assert.True(SpinWait.SpinUntil(cond, 5000), "timed out waiting for " + what);
            Thread.Sleep(5);
        }

        private static Scenes.Rig PlayerWithCast(Device device, out FakeCast cast, bool devices = true)
        {
            var r = Player(null, device);
            cast = WithCast(r, devices);
            return r;
        }

        private static PlayerScreen Screen(Scenes.Rig r) { return (PlayerScreen)r.App.Current; }

        /// <summary>Presses the cast button of the player: over the video (a tablet in landscape) the controls have to be shown first, with a tap on the picture.</summary>
        private static void PressCast(Scenes.Rig r)
        {
            if (!Has(r, "cast")) r.Tap(r.Platform.Screen.CanvasLogicalWidth / 2, 200, 0.08, 0.6);
            TapOn(r, "cast");
        }

        private static readonly Device[] Devices = { Device.Phone, Device.TabletPortrait, Device.Tablet };

        // ------------------------------------------------------------------ the TV is not touched

        [Fact]
        public void TheTvNeverCreatesDrawsOrAsksAnythingOfTheCastInterfaceEvenWithAServiceAvailable()
        {
            var r = Scenes.NewRig(Fonts);
            var cast = new FakeCast { IsAvailable = true }; cast.SetDevices(FakeCast.Chromecast("Salón"));
            r.App.Context.Cast = cast;
            r.App.Start(); r.Advance(0.5);
            Assert.False(r.App.Context.Compact);
            Assert.False(Has(r, "cast"));
            r.Platform.PressKey(UiKey.Right); r.Platform.PressKey(UiKey.Down); r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.3);
            using (var shot = r.Render(r.Now + 0.2)) { }
            Assert.False(r.App.Context.HasCastUi);                                           // not even made
            Assert.Equal(0, cast.BeginSearchCalls);
            Assert.Empty(cast.SendCalls);
            // the player of the TV: no sending state, no registration
            r.App.Push(new PlayerScreen(new PlayContext(r.Catalog.Channels.Take(3).ToList(), 0))); r.Advance(r.Now + 0.5);
            Assert.False(((PlayerScreen)r.App.Current).CastMode);
            Assert.False(r.App.Context.HasCastUi);
        }

        // ------------------------------------------------------------------ the sheet

        [Fact]
        public void TheSheetOpensFromTheButtonSearchesAndClosesWithBackOrAnOutsideTap()
        {
            foreach (var device in Devices)
            {
                var r = TouchRig.Phone(null, device);
                var cast = WithCast(r);
                TapOn(r, "cast");
                Assert.True(Ui(r).IsOpen);
                Assert.Equal(1, cast.BeginSearchCalls);
                Assert.True(cast.Searching);
                r.Advance(r.Now + 0.5);
                foreach (var d in cast.Devices) Assert.True(UiHas(r, "device:" + d.Id), d.Name);
                Assert.True(UiHas(r, "cast:close"));
                using (var shot = r.Render(r.Now + 0.1)) { }
                // Back closes it and stops the search
                r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.5);
                Assert.False(Ui(r).IsOpen);
                Assert.Equal(1, cast.EndSearchCalls);
                Assert.False(cast.Searching);
                Assert.IsType<MainScreen>(r.App.Current);                                    // the Back key did not leave the screen behind
                // a tap outside the sheet closes it too
                TapOn(r, "cast"); r.Advance(r.Now + 0.5);
                Assert.True(Ui(r).IsOpen);
                r.Tap(CenterX(UiTarget(r, "cast:close")), 30);                               // the top of the screen is the scrim
                r.Advance(r.Now + 0.5);
                Assert.False(Ui(r).IsOpen);
                // and the close button
                TapOn(r, "cast"); r.Advance(r.Now + 0.5);
                TapUi(r, "cast:close"); r.Advance(r.Now + 0.5);
                Assert.False(Ui(r).IsOpen);
            }
        }

        [Fact]
        public void WithNoDevicesTheSheetSaysItIsSearchingAndThenOffersToSearchAgain()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r, false);
            TapOn(r, "cast"); r.Advance(r.Now + 0.5);
            Assert.True(cast.Searching);
            Assert.Empty(Ui(r).TouchTargets().Where(t => t.Id.StartsWith("device:")));
            Assert.False(UiHas(r, "cast:retry"));                                            // still searching: nothing to retry
            using (var shot = r.Render(r.Now + 0.1)) { }
            cast.Searching = false; r.Advance(r.Now + 0.4);
            Assert.True(UiHas(r, "cast:retry"));
            TapUi(r, "cast:retry");
            Assert.Equal(2, cast.BeginSearchCalls);
            cast.SetDevices(FakeCast.Tv("Cocina")); r.Advance(r.Now + 0.4);
            Assert.True(UiHas(r, "device:dlna:Cocina"));
            Assert.False(UiHas(r, "cast:retry"));
        }

        [Fact]
        public void DevicesThatAppearWhileTheSheetIsOpenShowUpAndAreChecked()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r, false);
            var pc = new PlayContext(r.Catalog.Channels.Take(3).ToList(), 0);
            r.App.Push(new PlayerScreen(pc)); r.Advance(r.Now + 0.4);
            Ui(r).Open(pc.Current); r.Advance(r.Now + 0.4);
            Assert.Equal(0, cast.Checks);
            cast.SetDevices(FakeCast.Chromecast("Salón")); r.Advance(r.Now + 0.3);
            WaitUntil(() => cast.Checks == 1, "the check of the new device");
            Assert.True(UiHas(r, "device:gcast:Salón"));
        }

        // ------------------------------------------------------------------ compatibility

        [Fact]
        public void TheDevicesThatCannotPlayTheChannelAreDimmedWithTheReasonAndDoNotSend()
        {
            foreach (var device in Devices)
            {
                FakeCast cast;
                var r = PlayerWithCast(device, out cast);
                PressCast(r);
                WaitUntil(() => cast.Checks == 3, "the three checks");
                r.Advance(r.Now + 0.6);
                var ch = Screen(r).Context.Current;
                Assert.All(cast.CheckedPairs, p => Assert.EndsWith("|" + ch.Id, p));         // every device is checked against the channel being watched
                var bad = cast.Devices.First(d => d.Name == "Habitación");
                var chk = Ui(r).CheckOf(bad);
                Assert.NotNull(chk);
                Assert.False(chk.Compatible);
                Assert.Equal("El Chromecast no reproduce el audio de este canal", chk.Reason);
                Assert.True(Ui(r).CheckOf(cast.Devices.First(d => d.Name == "Salón")).Compatible);
                using (var shot = r.Render(r.Now + 0.1)) { }
                // a tap on the dimmed one says why and sends nothing
                TapUi(r, "device:" + bad.Id);
                Assert.Empty(cast.SendCalls);
                Assert.Equal(chk.Reason, Ui(r).ToastText);
                Assert.True(Ui(r).IsOpen);
                using (var shot = r.Render(r.Now + 0.1)) { }
            }
        }

        [Fact]
        public void WhileAChannelIsBeingCheckedTheRowSaysSoAndStillWorksOnceTheAnswerIsUnknown()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            var gate = new TaskCompletionSource<bool>();
            cast.CheckGate = gate.Task;
            TapOn(r, "cast");
            WaitUntil(() => cast.Checks >= 1, "the first check to start");
            var d0 = cast.Devices[0];
            Assert.True(Ui(r).IsChecking(d0));
            Assert.Null(Ui(r).CheckOf(d0));
            using (var shot = r.Render(r.Now + 0.1)) { }
            cast.CheckResult = (d, c) => CastCheck.Unknown;
            gate.SetResult(true);
            WaitUntil(() => Ui(r).CheckOf(cast.Devices[2]) != null, "every check");
            Assert.False(Ui(r).CheckOf(d0).Known);                                           // unknown is not a no
            TapUi(r, "device:" + d0.Id);
            WaitUntil(() => cast.SendCalls.Count == 1, "the send");
        }

        [Fact]
        public void ClosingTheSheetCancelsTheChecksInProgress()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            cast.CheckGate = new TaskCompletionSource<bool>().Task;
            TapOn(r, "cast");
            WaitUntil(() => cast.Checks >= 1, "the first check");
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.5);
            Assert.False(Ui(r).IsChecking(cast.Devices[0]));
        }

        // ------------------------------------------------------------------ sending from the player

        [Fact]
        public void ChoosingADeviceReleasesThePlaybackFirstThenSendsAndThePlayerShowsTheSendingState()
        {
            foreach (var device in Devices)
            {
                FakeCast cast;
                var r = PlayerWithCast(device, out cast);
                var ch = Screen(r).Context.Current;
                var pb = r.Playback.Created.Last();
                var order = new List<string>();
                pb.CallObserver = c => { lock (order) order.Add(c); };
                PressCast(r);
                WaitUntil(() => cast.Checks == 3, "the checks");
                r.Advance(r.Now + 0.5);
                var salon = cast.Devices.First(d => d.Name == "Salón");
                TapUi(r, "device:" + salon.Id);
                WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "the sending");
                r.Advance(r.Now + 0.7);
                Assert.Single(cast.SendCalls);
                Assert.Equal(ch.Id, cast.SendCalls[0].Value.Id);
                Assert.Same(salon, cast.SendCalls[0].Key);
                Assert.Equal(1, cast.ReleasedLocal);                                         // the local playback was released before the send began
                Assert.Contains("stop", order);
                Assert.True(Screen(r).CastMode);
                Assert.Single(r.Playback.Created);                                           // and nothing opened another one
                Assert.False(Ui(r).IsOpen);                                                  // the sheet closed
                Assert.Contains("cast:stop", r.App.Current.TouchTargets().Select(t => t.Id));
                Assert.Contains("cast:pause", r.App.Current.TouchTargets().Select(t => t.Id));
                Assert.Contains("cast:volume", r.App.Current.TouchTargets().Select(t => t.Id));
                Assert.True(r.Platform.Opaque);                                              // no video behind: the window is opaque
                using (var shot = r.Render(r.Now + 0.1)) { }
            }
        }

        [Fact]
        public void TheSendWaitsForTheLocalSessionToCloseEvenWhenTheFrameStoppedThePlaybackFirst()
        {
            // phone log 2026-10-24: the frame entered the sending state before the send asked to release the playback; the send then found nothing to wait for and opened the channel
            // for the TV while the engine was still closing the local session ("ya hay una sesion de directo abierta"): the sending failed at once
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            var pb = r.Playback.Created.Last();
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pb.CloseGate = closed.Task;
            cast.ReleaseGate = release.Task;
            Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
            r.Advance(r.Now + 0.2);
            Assert.True(Screen(r).CastMode);                                                 // the frame came first: the playback is stopped, its session still closing
            Assert.Contains("stop", pb.Calls);
            release.TrySetResult(true);                                                      // now the send asks to release the playback
            Thread.Sleep(50);
            r.Advance(r.Now + 0.2);
            Thread.Sleep(50);
            r.Advance(r.Now + 0.2);
            Assert.Equal(0, cast.ReleasedLocal);                                             // still waiting for the session
            Assert.NotEqual(CastPhase.Starting, cast.Status.Phase);
            closed.TrySetResult(true);
            WaitUntil(() => cast.ReleasedLocal == 1, "the release once the session closed");
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "the sending");
            r.Advance(r.Now + 0.3);
            Assert.True(Screen(r).CastMode);
            Assert.Single(r.Playback.Created);
        }

        [Fact]
        public void WhileItConnectsThePlayerSaysSoAndOnlyOffersToCancel()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            cast.StartGate = new TaskCompletionSource<bool>().Task;
            Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
            WaitUntil(() => cast.Status.Phase == CastPhase.Starting, "starting");
            r.Advance(r.Now + 0.4);
            Assert.True(Screen(r).CastMode);
            var ids = r.App.Current.TouchTargets().Select(t => t.Id).ToList();
            Assert.Contains("cast:stop", ids);
            Assert.DoesNotContain("cast:pause", ids);
            Assert.DoesNotContain("cast:volume", ids);
            using (var shot = r.Render(r.Now + 0.1)) { }
            TapOn(r, "cast:stop");                                                           // "Cancelar"
            Assert.Equal(1, cast.StopCalls);
            WaitUntil(() => !Ui(r).SendInFlight, "the cancelled send to end");
            r.Advance(r.Now + 0.6);
            Assert.False(Screen(r).CastMode);
            Assert.Equal(2, r.Playback.Created.Count);                                       // the player plays here again
        }

        [Fact]
        public void PauseResumeAndVolumeGoToTheDeviceAndStopGivesThePlayerBack()
        {
            foreach (var device in Devices)
            {
                FakeCast cast;
                var r = PlayerWithCast(device, out cast);
                Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
                WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
                r.Advance(r.Now + 0.5);
                TapOn(r, "cast:pause");
                Assert.Equal(new[] { true }, cast.PauseCalls.ToArray());
                r.Advance(r.Now + 0.3);
                TapOn(r, "cast:pause");
                Assert.Equal(new[] { true, false }, cast.PauseCalls.ToArray());
                // the volume: a finger on the slider and dragged to the right
                var vol = Target(r, "cast:volume");
                var track = CastUi.VolumeTrack(vol.Rect);
                float y = CenterY(vol);
                r.FingerDown(track.Left + track.Width * 0.2f, y, 0.05);
                for (int i = 1; i <= 6; i++) { r.Advance(r.Now + 0.2); r.Touch(PointerAction.Move, track.Left + track.Width * (0.2f + 0.13f * i), y); }
                r.FingerUp(track.Right, y, 0.3);
                Assert.NotEmpty(cast.VolumeCalls);
                Assert.Equal(100, cast.VolumeCalls.Last());                                  // the last value always goes out
                Assert.True(cast.VolumeCalls.Count < 12);                                    // but not every event
                Assert.InRange(cast.VolumeCalls.First(), 15, 25);
                using (var shot = r.Render(r.Now + 0.1)) { }
                // stop
                TapOn(r, "cast:stop");
                Assert.Equal(1, cast.StopCalls);
                r.Advance(r.Now + 0.6);
                Assert.False(Screen(r).CastMode);
                Assert.Equal(2, r.Playback.Created.Count);
                WaitUntil(() => r.Playback.Created.Last().Calls.Any(), "the new playback to run");
                Assert.StartsWith("run", r.Playback.Created.Last().Calls.First());
            }
        }

        [Fact]
        public void ADeviceWithoutPauseOrVolumeDoesNotGetThoseControls()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast, false);
            var tv = new CastDevice { Id = "dlna:x", Name = "Tele sin mando", Kind = CastDeviceKind.Tv, CanPause = false, CanVolume = false };
            cast.SetDevices(tv);
            Ui(r).Send(tv, Screen(r).Context.Current);
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.4);
            var ids = r.App.Current.TouchTargets().Select(t => t.Id).ToList();
            Assert.DoesNotContain("cast:pause", ids); Assert.DoesNotContain("cast:volume", ids); Assert.Contains("cast:stop", ids);
            using (var shot = r.Render(r.Now + 0.1)) { }
        }

        [Fact]
        public void ASendThatFailsGivesTheReasonAndThePlayerPlaysHereAgain()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            cast.SendSucceeds = false; cast.SendFailure = "El Chromecast no reproduce el audio de este canal";
            Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
            WaitUntil(() => cast.SendCalls.Count == 1 && !Ui(r).SendInFlight, "the failure");
            r.Advance(r.Now + 0.6);
            Assert.Equal("El Chromecast no reproduce el audio de este canal", Ui(r).ToastText);
            Assert.False(Screen(r).CastMode);
            Assert.Equal(2, r.Playback.Created.Count);
            using (var shot = r.Render(r.Now + 0.1)) { }
        }

        // ------------------------------------------------------------------ the end of a sending

        [Fact]
        public void ASendingThatEndsByItselfIsToldAndTheNormalStateReturns()
        {
            foreach (var device in Devices)
            {
                FakeCast cast;
                var r = PlayerWithCast(device, out cast);
                Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
                WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
                r.Advance(r.Now + 0.5);
                Assert.True(Screen(r).CastMode);
                cast.EndByItself("El envío ha terminado: se ha parado desde la tele");
                r.Advance(r.Now + 0.6);
                Assert.Equal("El envío ha terminado: se ha parado desde la tele", Ui(r).ToastText);
                Assert.False(Screen(r).CastMode);
                Assert.Equal(2, r.Playback.Created.Count);                                   // back to a player that plays here
                using (var shot = r.Render(r.Now + 0.1)) { }
                // the notice goes by itself
                r.Advance(r.Now + CastUi.ToastSeconds + 1);
                using (var shot = r.Render(r.Now + 0.1)) { }
                Assert.False(UiHas(r, "cast:toast"));
            }
        }

        [Fact]
        public void ASendingThatEndsWhileTheListsAreShownRemovesTheStripAndSaysSo()
        {
            var r = Category();
            var cast = WithCast(r);
            Ui(r).Send(cast.Devices[0], r.Catalog.Channels.First());
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.5);
            Assert.True(UiHas(r, "cast:pill"));
            cast.EndByItself("El canal ha dejado de emitir");
            r.Advance(r.Now + 0.5);
            Assert.False(UiHas(r, "cast:pill"));
            Assert.True(UiHas(r, "cast:toast"));
            Assert.Equal("El canal ha dejado de emitir", Ui(r).ToastText);
        }

        // ------------------------------------------------------------------ choosing another channel while sending

        [Fact]
        public void ChoosingAnotherChannelInThePlayerWhileSendingSendsThatOneToTheSameDevice()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            var salon = cast.Devices.First(d => d.Name == "Salón");
            Ui(r).Send(salon, Screen(r).Context.Current);
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.5);
            var first = cast.Status.Channel;
            TapOn(r, "more1");
            WaitUntil(() => cast.SendCalls.Count == 2 && cast.Status.Phase == CastPhase.Sending, "the second send");
            r.Advance(r.Now + 0.5);
            Assert.NotEqual(first.Id, cast.SendCalls[1].Value.Id);
            Assert.Same(salon, cast.SendCalls[1].Key);
            Assert.Equal(cast.SendCalls[1].Value.Id, Screen(r).Context.Current.Id);          // the page shows the new channel
            Assert.Single(r.Playback.Created);                                               // and nothing played here
            Assert.True(Screen(r).CastMode);
        }

        [Fact]
        public void ChoosingAChannelInAListWhileSendingSendsItAndDoesNotOpenThePlayer()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            var salon = cast.Devices.First(d => d.Name == "Salón");
            Ui(r).Send(salon, Screen(r).Context.Current);
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.5);
            // Back to the lists: the sending goes on
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.6);
            Assert.IsType<MainScreen>(r.App.Current);
            Assert.Equal(0, cast.StopCalls);
            Assert.Equal(CastPhase.Sending, cast.Status.Phase);
            Assert.True(UiHas(r, "cast:pill"));
            // open a category and pick a channel
            var liga = r.Catalog.Categories.First(c => c.Name == "Liga de Campeones").Channels.ToList();
            r.Checker.Default = LinkState.Checking;
            for (int i = 0; i < liga.Count; i++) r.Checker.Set(liga[i], LinkState.Good, 10 + i);
            r.App.Push(new CategoryScreen("Liga de Campeones", liga, Section.Tv)); r.Advance(r.Now + 1.0);
            var row = r.App.Current.TouchTargets().Where(t => t.Focus != null && t.Focus.Group == "rows").Skip(2).First();      // not the channel that is already being sent
            r.Tap(CenterX(row), CenterY(row));
            WaitUntil(() => cast.SendCalls.Count == 2, "the second send");
            Assert.IsType<CategoryScreen>(r.App.Current);                                    // the player did not open
            Assert.Same(salon, cast.SendCalls[1].Key);
            Assert.Single(r.Playback.Created);
        }

        // ------------------------------------------------------------------ from the lists: choosing the device first

        [Fact]
        public void WithNoChannelInViewAPickedDeviceWaitsForTheNextChannelChosenInAList()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r);
            r.Checker.Default = LinkState.Good;
            TapOn(r, "cast"); r.Advance(r.Now + 0.5);
            Assert.Null(Ui(r).SheetChannel);
            Assert.Equal(0, cast.Checks);                                                    // nothing to check against
            var tv = cast.Devices.First(d => d.Kind == CastDeviceKind.Tv);
            TapUi(r, "device:" + tv.Id); r.Advance(r.Now + 0.6);
            Assert.False(Ui(r).IsOpen);
            Assert.Same(tv, Ui(r).Armed);
            Assert.Empty(cast.SendCalls);
            Assert.True(UiHas(r, "cast:pill"));
            using (var shot = r.Render(r.Now + 0.1)) { }
            // the next category opens as usual, and a channel in it goes to the television
            var cat = r.App.Current.TouchTargets().First(t => t.Focus != null && t.Focus.Group == "grid");
            r.Tap(CenterX(cat), CenterY(cat)); r.Advance(r.Now + 0.8);
            Assert.IsType<CategoryScreen>(r.App.Current);
            var row = r.App.Current.TouchTargets().First(t => t.Focus != null && t.Focus.Group == "rows");
            r.Tap(CenterX(row), CenterY(row));
            WaitUntil(() => cast.SendCalls.Count == 1, "the send");
            Assert.Same(tv, cast.SendCalls[0].Key);
            Assert.Null(Ui(r).Armed);
            Assert.IsType<CategoryScreen>(r.App.Current);
        }

        [Fact]
        public void TheArmedStripCanBeCancelledAndOpensTheSheet()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r);
            Ui(r).Open(null); r.Advance(r.Now + 0.5);
            TapUi(r, "device:" + cast.Devices[0].Id); r.Advance(r.Now + 0.6);
            Assert.NotNull(Ui(r).Armed);
            TapUi(r, "cast:pill"); r.Advance(r.Now + 0.5);
            Assert.True(Ui(r).IsOpen);
            r.Platform.PressKey(UiKey.Back); r.Advance(r.Now + 0.5);
            TapUi(r, "cast:pill:stop"); r.Advance(r.Now + 0.3);
            Assert.Null(Ui(r).Armed);
            Assert.False(UiHas(r, "cast:pill"));
            Assert.Equal(0, cast.StopCalls);                                                 // nothing was being sent
        }

        // ------------------------------------------------------------------ the strip and the sheet while sending

        [Fact]
        public void WhileSendingTheStripOverTheListsPausesStopsAndOpensTheControls()
        {
            foreach (var device in Devices)
            {
                var r = Category();
                var cast = WithCast(r);
                Ui(r).Send(cast.Devices[2], r.Catalog.Channels.First());
                WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
                r.Advance(r.Now + 0.5);
                Assert.True(UiHas(r, "cast:pill"));
                Assert.True(UiHas(r, "cast:pill:pause"));
                TapUi(r, "cast:pill:pause");
                Assert.Equal(new[] { true }, cast.PauseCalls.ToArray());
                r.Advance(r.Now + 0.3);
                using (var shot = r.Render(r.Now + 0.1)) { }
                // the body of the strip opens the sheet with the controls and the other devices
                TapUi(r, "cast:pill"); r.Advance(r.Now + 0.6);
                Assert.True(Ui(r).IsOpen);
                Assert.True(UiHas(r, "cast:pause")); Assert.True(UiHas(r, "cast:volume")); Assert.True(UiHas(r, "cast:stop"));
                Assert.False(UiHas(r, "device:" + cast.Devices[2].Id));                      // the one it is being sent to is not offered again
                Assert.True(UiHas(r, "device:" + cast.Devices[0].Id));
                using (var shot = r.Render(r.Now + 0.1)) { }
                // "Dejar de enviar" stops and closes
                TapUi(r, "cast:stop"); r.Advance(r.Now + 0.6);
                Assert.Equal(1, cast.StopCalls);
                Assert.False(Ui(r).IsOpen);
                Assert.False(UiHas(r, "cast:pill"));
                break;
            }
        }

        [Fact]
        public void TappingAnotherDeviceWhileSendingMovesTheChannelThere()
        {
            var r = Category();
            var cast = WithCast(r);
            var ch = r.Catalog.Channels.First();
            Ui(r).Send(cast.Devices[0], ch);
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            Ui(r).Open(null); r.Advance(r.Now + 0.6);
            Assert.Equal(ch.Id, Ui(r).SheetChannel.Id);                                      // the channel being sent is the one the others are checked against
            WaitUntil(() => cast.Checks >= 2, "the checks");
            r.Advance(r.Now + 0.4);
            TapUi(r, "device:" + cast.Devices[2].Id);
            WaitUntil(() => cast.SendCalls.Count == 2 && cast.Status.Phase == CastPhase.Sending, "the second send");
            Assert.Same(cast.Devices[2], cast.SendCalls[1].Key);
            Assert.Equal(ch.Id, cast.SendCalls[1].Value.Id);
        }

        [Fact]
        public void TheCastButtonOfThePageIsAquaWhileSomethingIsBeingSent()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r);
            Assert.True(Has(r, "cast"));
            Ui(r).Send(cast.Devices[0], r.Catalog.Channels.First());
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.5);
            Assert.True(Has(r, "cast"));
            using (var shot = r.Render(r.Now + 0.1)) { }
            Assert.True(Ui(r).IsCasting);
        }

        // ------------------------------------------------------------------ the app going to the background

        [Fact]
        public void TheAppGoingToTheBackgroundClosesTheSheetButDoesNotStopTheSending()
        {
            FakeCast cast;
            var r = PlayerWithCast(Device.Phone, out cast);
            TapOn(r, "cast"); r.Advance(r.Now + 0.5);
            Assert.True(Ui(r).IsOpen);
            r.App.OnPause(); r.Advance(r.Now + 0.3);
            Assert.False(Ui(r).IsOpen);
            Assert.Equal(1, cast.EndSearchCalls);
            r.App.OnResume(); r.Advance(r.Now + 0.3);

            Ui(r).Send(cast.Devices[0], Screen(r).Context.Current);
            WaitUntil(() => cast.Status.Phase == CastPhase.Sending, "sending");
            r.Advance(r.Now + 0.5);
            int playbacks = r.Playback.Created.Count;
            r.App.OnPause(); r.Advance(r.Now + 0.3);
            Assert.Equal(0, cast.StopCalls);                                                 // the screen going off does not end the sending
            r.App.OnResume(); r.Advance(r.Now + 0.5);
            Assert.IsType<PlayerScreen>(r.App.Current);
            Assert.True(Screen(r).CastMode);                                                 // the player comes back as the sending state, not as a second local playback
            Assert.Equal(playbacks, r.Playback.Created.Count);
        }

        // ------------------------------------------------------------------ sizes and legibility

        [Fact]
        public void EverythingOfTheCastInterfaceThatReactsToATouchIsAtLeastFortyFourUnitsAndNoTextIsUnderTwelve()
        {
            var bad = new List<string>();
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet, Device.PhoneLandscape })
                foreach (var scene in Scenes.CastScenes)
                {
                    var r = Scenes.Build(scene, Fonts, device);
                    var gx = r.App.Context.Gx;
                    gx.ResetTextStats();
                    using (var shot = r.Render(r.Now + 0.3)) { }
                    if (gx.SmallestTextSize < 12f) bad.Add(device.Name + " " + scene + ": text '" + gx.SmallestTextSample + "' at " + gx.SmallestTextSize);
                    foreach (var t in Ui(r).TouchTargets().Concat(r.App.Current.TouchTargets()))
                    {
                        if (t.Id == "panel" || t.Id == "cast:sheet" || t.Id == "cast:now" || t.Id == "cast:toast" || t.Id == "cast:volume") continue;
                        if (t.Clip.HasValue)
                        {
                            var vis = TouchHit.Intersect(t.Rect, t.Clip.Value);
                            if (vis.Height < t.Rect.Height - 0.5f || vis.Width < t.Rect.Width - 0.5f) continue;
                        }
                        if (Math.Min(t.Rect.Width, t.Rect.Height) < 43.5f) bad.Add(device.Name + " " + scene + ": " + t + " is " + t.Rect.Width.ToString("0.#") + " x " + t.Rect.Height.ToString("0.#"));
                    }
                    var vol = Ui(r).TouchTargets().Concat(r.App.Current.TouchTargets()).FirstOrDefault(t => t.Id == "cast:volume");
                    if (vol != null && vol.Rect.Height < 43.5f) bad.Add(device.Name + " " + scene + ": the volume row is " + vol.Rect.Height + " tall");
                    r.App.Dispose();
                }
            Assert.True(bad.Count == 0, string.Join("\n", bad.Distinct()));
        }

        [Fact]
        public void EveryCastSceneRendersOnEveryTouchDeviceAndTheSheetStaysInsideTheScreen()
        {
            foreach (var device in new[] { Device.Phone, Device.TabletPortrait, Device.Tablet, Device.PhoneLandscape })
                foreach (var scene in Scenes.CastScenes)
                {
                    var r = Scenes.Build(scene, Fonts, device);
                    using (var bmp = r.Render(r.Now + 0.3)) Assert.Equal(device.Width, bmp.Width);
                    var m = r.Platform.Screen;
                    var canvas = new SKRect(0, 0, m.CanvasLogicalWidth, m.CanvasLogicalHeight);
                    foreach (var t in Ui(r).TouchTargets())
                    {
                        if (t.Id == "cast:sheet") { Assert.True(t.Rect.Left >= canvas.Left - 1 && t.Rect.Right <= canvas.Right + 1, device.Name + " " + scene + " sheet sticks out sideways"); continue; }
                        if (t.Clip.HasValue) continue;
                        Assert.True(t.Rect.Left >= canvas.Left - 0.5f && t.Rect.Right <= canvas.Right + 0.5f && t.Rect.Top >= -0.5f && t.Rect.Bottom <= m.SurfaceBottom + 0.5f, device.Name + " " + scene + ": " + t + " is outside the screen");
                    }
                    r.App.Dispose();
                }
        }

        [Fact]
        public void ALongListOfDevicesScrollsInsideTheSheet()
        {
            var r = TouchRig.Phone();
            var cast = WithCast(r, false);
            cast.SetDevices(Enumerable.Range(1, 12).Select(i => i % 2 == 0 ? FakeCast.Chromecast("Chromecast " + i) : FakeCast.Tv("Tele " + i)).ToArray());
            Ui(r).Open(null); r.Advance(r.Now + 0.6);
            var first = UiTarget(r, "device:" + cast.Devices[0].Id);
            var last = Ui(r).TouchTargets().First(t => t.Id == "device:" + cast.Devices[11].Id);
            Assert.True(last.Rect.Top > first.Rect.Bottom + 400);                            // the last one is below the fold
            float x = CenterX(first), y0 = Math.Min(first.Rect.Top + 200, 700);
            r.Drag(x, y0, x, y0 - 400, 0.3);
            r.Advance(r.Now + 1.0);
            var moved = Ui(r).TouchTargets().First(t => t.Id == "device:" + cast.Devices[0].Id);
            Assert.True(moved.Rect.Top < first.Rect.Top - 100, "the list did not scroll");
            Assert.Empty(cast.SendCalls);                                                    // a drag never taps
            using (var shot = r.Render(r.Now + 0.1)) { }
        }

        [Fact]
        public void ThePausedAndSendingStatesAreDrawnAndTheVolumeSliderMapsPositionsToValues()
        {
            var track = new SKRect(100, 0, 300, 44);
            Assert.Equal(0, CastVolumeSlider.ValueAt(track, 50));
            Assert.Equal(50, CastVolumeSlider.ValueAt(track, 200));
            Assert.Equal(100, CastVolumeSlider.ValueAt(track, 400));
            var s = new CastVolumeSlider();
            var sent = new List<int>();
            s.Begin(track, 100, 0.0, sent.Add);
            s.Move(110, 0.05, sent.Add); s.Move(150, 0.10, sent.Add); s.Move(250, 0.30, sent.Add);
            s.End(300, 0.31, sent.Add, false);
            Assert.Equal(0, sent[0]);
            Assert.Equal(100, sent.Last());
            Assert.DoesNotContain(5, sent);                                                  // the move 50 ms after the first was held back
            Assert.False(s.Dragging);
            Assert.Equal(40, s.Shown(40));                                                   // the device's own value again once the finger is up
        }
    }
}
