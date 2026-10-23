using System.Collections.Generic;
using AceEngine.App;
using Xunit;

namespace PAndroid.Tests
{
    public class ProfileTests
    {
        private static DeviceFacts Facts(long memMiB = 4096, bool lowRam = false) { return new DeviceFacts { TotalMemBytes = memMiB * 1024 * 1024, IsLowRamDevice = lowRam, HasTouchscreen = true }; }

        // ------------------------------------------------------------------ the kind of device

        [Fact]
        public void FireTvIsRecognisedByItsSystemFeatureEvenThoughItIsALeanbackDevice()
        {
            var f = Facts(); f.HasFireTvFeature = true; f.HasLeanback = true; f.IsTelevisionUiMode = true; f.HasTouchscreen = false;
            Assert.Equal(AndroidDeviceKind.FireTv, AndroidDeviceProfile.DetectKind(f));
        }

        [Fact]
        public void AnAndroidTvIsLeanbackOrTheTelevisionUiMode()
        {
            var a = Facts(); a.HasLeanback = true; a.HasTouchscreen = false;
            Assert.Equal(AndroidDeviceKind.Tv, AndroidDeviceProfile.DetectKind(a));
            var b = Facts(); b.IsTelevisionUiMode = true; b.HasTouchscreen = false;
            Assert.Equal(AndroidDeviceKind.Tv, AndroidDeviceProfile.DetectKind(b));
        }

        [Theory]
        [InlineData(360, AndroidDeviceKind.Phone)]
        [InlineData(411, AndroidDeviceKind.Phone)]
        [InlineData(599, AndroidDeviceKind.Phone)]
        [InlineData(600, AndroidDeviceKind.Tablet)]
        [InlineData(800, AndroidDeviceKind.Tablet)]
        public void ATouchDeviceIsAPhoneOrATabletBySmallestWidth(int smallestWidthDp, AndroidDeviceKind expected)
        {
            var f = Facts(); f.SmallestWidthDp = smallestWidthDp;
            Assert.Equal(expected, AndroidDeviceProfile.DetectKind(f));
        }

        [Fact]
        public void WithoutTouchAndWithoutLeanbackItIsABoxDrivenByKeys()
        {
            var f = Facts(); f.HasTouchscreen = false;
            Assert.Equal(AndroidDeviceKind.Box, AndroidDeviceProfile.DetectKind(f));
        }

        // ------------------------------------------------------------------ capabilities

        [Fact]
        public void ThePhoneIsTouchWithoutRemoteKeys()
        {
            var f = Facts(); f.HasKeyboard = false;
            var p = AndroidDeviceProfile.From(f);
            Assert.Equal(AndroidDeviceKind.Phone, p.Kind);
            Assert.True(p.Capabilities.HasTouch); Assert.False(p.Capabilities.HasRemote);
            Assert.False(p.Capabilities.HasChannelKeys); Assert.False(p.Capabilities.HasDigitKeys); Assert.False(p.Capabilities.HasColorKeys);
            Assert.False(p.Capabilities.HasKeyboard);
            Assert.False(p.IgnoresSafeInsets);
        }

        [Fact]
        public void ThePhoneIsExactlyTheContractsTouchOnlyCapabilities()
        {
            var c = AndroidDeviceProfile.From(Facts()).Capabilities;
            var t = PUi.Platform.PlatformCapabilities.TouchOnly;
            Assert.Equal(t.HasChannelKeys, c.HasChannelKeys); Assert.Equal(t.HasDigitKeys, c.HasDigitKeys); Assert.Equal(t.HasColorKeys, c.HasColorKeys);
            Assert.Equal(t.HasRemote, c.HasRemote); Assert.Equal(t.HasTouch, c.HasTouch); Assert.Equal(t.HasKeyboard, c.HasKeyboard);
        }

        [Fact]
        public void APhoneWithAGamePadHasARemoteToo()
        {
            var f = Facts(); f.HasDpad = true;
            var c = AndroidDeviceProfile.From(f).Capabilities;
            Assert.True(c.HasRemote); Assert.True(c.HasTouch); Assert.False(c.HasChannelKeys);
        }

        [Fact]
        public void APhoneWithAKeyboardAttachedSaysSo()
        {
            var f = Facts(); f.HasKeyboard = true;
            Assert.True(AndroidDeviceProfile.From(f).Capabilities.HasKeyboard);
        }

        [Fact]
        public void TheFireTvRemoteHasNoChannelDigitOrColourKeysWhateverTheKeyLayoutSays()
        {
            var f = Facts(); f.HasFireTvFeature = true; f.HasTouchscreen = false;
            f.DeviceHasChannelKeys = f.DeviceHasDigitKeys = f.DeviceHasColorKeys = true;
            var p = AndroidDeviceProfile.From(f);
            Assert.Equal(AndroidDeviceKind.FireTv, p.Kind);
            Assert.True(p.Capabilities.HasRemote); Assert.False(p.Capabilities.HasTouch);
            Assert.False(p.Capabilities.HasChannelKeys); Assert.False(p.Capabilities.HasDigitKeys); Assert.False(p.Capabilities.HasColorKeys);
            Assert.True(p.IgnoresSafeInsets);
        }

        [Fact]
        public void AnAndroidTvTakesTheKeysTheInputDevicesCanProduce()
        {
            var f = Facts(); f.HasLeanback = true; f.HasTouchscreen = false;
            f.DeviceHasChannelKeys = false; f.DeviceHasDigitKeys = true; f.DeviceHasColorKeys = false;
            var p = AndroidDeviceProfile.From(f);
            Assert.True(p.Capabilities.HasRemote); Assert.False(p.Capabilities.HasTouch);
            Assert.False(p.Capabilities.HasChannelKeys); Assert.True(p.Capabilities.HasDigitKeys); Assert.False(p.Capabilities.HasColorKeys);
        }

        [Fact]
        public void AForcedKindWinsOverTheDetectedOne()
        {
            var f = Facts();
            var p = AndroidDeviceProfile.From(f, AndroidDeviceKind.Tv);
            Assert.Equal(AndroidDeviceKind.Tv, p.Kind);
            Assert.True(p.Capabilities.HasRemote); Assert.False(p.Capabilities.HasTouch);
        }

        // ------------------------------------------------------------------ memory

        [Theory]
        [InlineData(900, false, MemoryTier.Low)]       // a 1 GB stick
        [InlineData(1199, false, MemoryTier.Low)]
        [InlineData(1200, false, MemoryTier.Mid)]
        [InlineData(1500, false, MemoryTier.Mid)]      // a 2 GB stick
        [InlineData(2799, false, MemoryTier.Mid)]
        [InlineData(2800, false, MemoryTier.High)]
        [InlineData(7600, false, MemoryTier.High)]
        [InlineData(8000, true, MemoryTier.Low)]       // the system says so
        [InlineData(0, false, MemoryTier.High)]        // unknown total: do not punish the device
        public void TheMemoryTierComesFromTheTotalMemoryAndTheLowRamFlag(long mib, bool lowRam, MemoryTier expected)
        {
            Assert.Equal(expected, AndroidDeviceProfile.DetectMemory(Facts(mib, lowRam)));
        }

        [Fact]
        public void TheBudgetsShrinkWithTheMemoryAndAreApplied()
        {
            var low = AndroidDeviceProfile.From(Facts(900));
            var mid = AndroidDeviceProfile.From(Facts(1500));
            var high = AndroidDeviceProfile.From(Facts(6000));
            Assert.True(low.LiveTimeshiftMiB < mid.LiveTimeshiftMiB && mid.LiveTimeshiftMiB < high.LiveTimeshiftMiB);
            Assert.Equal(32, low.LiveTimeshiftMiB);
            var c = new AppConfig();
            low.ApplyBudgets(c);
            Assert.Equal(low.LiveBufferMiB, c.LiveBufferMiB); Assert.Equal(32, c.LiveTimeshiftMiB); Assert.Equal(low.VodBufferMiB, c.VodBufferMiB);
        }

        [Fact]
        public void TheHighBudgetsAreTheEnginesOwnDefaults()
        {
            var d = new AppConfig();
            var p = AndroidDeviceProfile.From(Facts(6000));
            Assert.Equal(d.LiveBufferMiB, p.LiveBufferMiB); Assert.Equal(d.LiveTimeshiftMiB, p.LiveTimeshiftMiB); Assert.Equal(d.VodBufferMiB, p.VodBufferMiB);
        }

        [Fact]
        public void TheBudgetsAreTheDefaultsLayerSoAConfigurationStillOverridesThem()
        {
            var p = AndroidDeviceProfile.From(Facts(900));
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("user", "{\"liveTimeshiftMiB\":100}") }, null, c => { AppConfigDefaults.Android(c); p.ApplyBudgets(c); });
            Assert.Equal(100, cfg.LiveTimeshiftMiB);
            Assert.Equal(p.LiveBufferMiB, cfg.LiveBufferMiB);
        }

        [Fact]
        public void EveryBudgetStaysInsideWhatTheConfigurationAcceptsAsAValue()
        {
            foreach (long mib in new long[] { 0, 500, 900, 1500, 3000, 12000 })
                foreach (bool low in new[] { false, true })
                {
                    var p = AndroidDeviceProfile.From(Facts(mib, low));
                    var c = new AppConfig(); p.ApplyBudgets(c);
                    var again = AppConfig.Parse(c.ToJson());
                    Assert.Equal(c.LiveBufferMiB, again.LiveBufferMiB); Assert.Equal(c.LiveTimeshiftMiB, again.LiveTimeshiftMiB); Assert.Equal(c.VodBufferMiB, again.VodBufferMiB);
                }
        }

        // ------------------------------------------------------------------ draw size

        [Fact]
        public void ALowRamDeviceDrawsAt720pAndATvAtFullHdAndAPhoneAtMost3200()
        {
            Assert.Equal(1280, AndroidDeviceProfile.From(Facts(900)).MaxRenderLongSide);
            var tv = Facts(3000); tv.HasLeanback = true; tv.HasTouchscreen = false;
            Assert.Equal(1920, AndroidDeviceProfile.From(tv).MaxRenderLongSide);
            Assert.Equal(3200, AndroidDeviceProfile.From(Facts(6000)).MaxRenderLongSide);
            var stick = Facts(900, true); stick.HasFireTvFeature = true; stick.HasTouchscreen = false;
            Assert.Equal(1280, AndroidDeviceProfile.From(stick).MaxRenderLongSide);
        }

        [Fact]
        public void ADrawSizeOverrideWins()
        {
            Assert.Equal(1600, AndroidDeviceProfile.From(Facts(900), null, 1600).MaxRenderLongSide);
        }

        [Fact]
        public void DescribeNamesTheKindAndTheBudgets()
        {
            var s = AndroidDeviceProfile.From(Facts(900)).Describe();
            Assert.Contains("Phone", s); Assert.Contains("Low", s); Assert.Contains("1280", s);
        }

        // ------------------------------------------------------------------ the options of the "platform" section

        [Fact]
        public void TheDefaultOptionsUseExoPlayerAndKillTheProcessOnExit()
        {
            var o = AndroidOptions.From(null);
            Assert.Equal(PlayerKind.Exo, o.Player); Assert.Null(o.ForcedKind); Assert.Equal(0, o.MaxRenderSide); Assert.True(o.KillProcessOnExit);
        }

        [Fact]
        public void TheOptionsAreReadFromThePlatformSectionOfTheConfiguration()
        {
            var cfg = AppConfig.Parse("{\"platform\":{\"player\":\"MediaPlayer\",\"deviceKind\":\"FireTV\",\"maxRenderSide\":1280,\"killOnExit\":false}}");
            var o = AndroidOptions.From(cfg.Platform);
            Assert.Equal(PlayerKind.MediaPlayer, o.Player); Assert.Equal(AndroidDeviceKind.FireTv, o.ForcedKind); Assert.Equal(1280, o.MaxRenderSide); Assert.False(o.KillProcessOnExit);
        }

        [Fact]
        public void UnknownOrWrongOptionsFallBackToTheDefaults()
        {
            var o = AndroidOptions.From(new Dictionary<string, object> { { "player", "vlc" }, { "deviceKind", "toaster" }, { "maxRenderSide", "big" }, { "killOnExit", "no" } });
            Assert.Equal(PlayerKind.Exo, o.Player); Assert.Null(o.ForcedKind); Assert.Equal(0, o.MaxRenderSide); Assert.True(o.KillProcessOnExit);
        }
    }
}
