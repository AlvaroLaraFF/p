using System.Collections.Generic;
using System.Linq;
using AceEngine.App;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>
    /// The device profile of the Fire TV family: detection (system feature, then model), memory budgets and draw size by the RAM the system reports (Amazon's specifications: 1 GB for the Lite and the
    /// 3rd generation sticks, 1.5 GB for the 4K 1st generation, 2 GB for the 4K, 4K Max and the Cubes) and by the GPU class, the background's frame rate and the profile's options.
    /// </summary>
    public class FireTvProfileTests
    {
        /// <summary>What the system reports for a stick: a TV device, no touch, the RAM minus what the system keeps for itself (about 10 %).</summary>
        private static DeviceFacts Stick(string model, int reportedMiB, bool feature = true)
        {
            return new DeviceFacts
            {
                Manufacturer = "Amazon", Model = model, HasFireTvFeature = feature, HasLeanback = true, IsTelevisionUiMode = true, HasTouchscreen = false,
                TotalMemBytes = reportedMiB * 1024L * 1024L, SdkInt = 28, SmallestWidthDp = 540, DeviceHasChannelKeys = true, DeviceHasDigitKeys = true, DeviceHasColorKeys = true
            };
        }

        // ------------------------------------------------------------------ detection

        [Fact]
        public void TheSystemFeatureMakesAFireTvWhateverTheModelIs()
        {
            Assert.Equal(AndroidDeviceKind.FireTv, AndroidDeviceProfile.DetectKind(Stick("SomethingNew", 1800)));
        }

        [Theory]
        [InlineData("AFTSSS")]
        [InlineData("AFTSS")]
        [InlineData("AFTMM")]
        [InlineData("AFTKA")]
        [InlineData("AFTKRT")]
        [InlineData("AFTGAZL")]
        [InlineData("AFTHA004")]           // a Fire TV Edition television
        public void AFireTvThatDoesNotReportItsFeatureIsRecognisedByItsModel(string model)
        {
            Assert.Equal(AndroidDeviceKind.FireTv, AndroidDeviceProfile.DetectKind(Stick(model, 1500, feature: false)));
        }

        [Theory]
        [InlineData("Pixel 7")]
        [InlineData("SHIELD Android TV")]
        [InlineData("AFT")]                // too short
        [InlineData("AFTer dark")]         // not capitals and digits
        [InlineData("")]
        public void ADeviceWithAnotherModelIsNotAFireTv(string model)
        {
            var f = Stick(model, 2500, feature: false);
            Assert.NotEqual(AndroidDeviceKind.FireTv, AndroidDeviceProfile.DetectKind(f));
        }

        [Fact]
        public void ATouchDeviceIsNeverAFireTvByItsModelAlone()
        {
            var f = Stick("AFTSSS", 1500, feature: false); f.HasTouchscreen = true; f.HasLeanback = false; f.IsTelevisionUiMode = false;
            Assert.NotEqual(AndroidDeviceKind.FireTv, AndroidDeviceProfile.DetectKind(f));
        }

        [Fact]
        public void TheFireTvProfileHasARemoteButNoTouchNoChannelDigitOrColourKeys()
        {
            var p = AndroidDeviceProfile.From(Stick("AFTKA", 1750));
            Assert.Equal(AndroidDeviceKind.FireTv, p.Kind);
            Assert.True(p.Capabilities.HasRemote); Assert.False(p.Capabilities.HasTouch);
            Assert.False(p.Capabilities.HasChannelKeys); Assert.False(p.Capabilities.HasDigitKeys); Assert.False(p.Capabilities.HasColorKeys);
            Assert.True(p.IgnoresSafeInsets); Assert.False(p.IsTouchDevice);
        }

        // ------------------------------------------------------------------ the sticks, by what the system reports

        [Theory]
        // model, reported MiB, memory tier, live buffer, timeshift, vod, draw long side, background fps
        [InlineData("AFTSS", 880, MemoryTier.Low, 16, 32, 24, 1280, 20)]       // Stick Lite and Stick HD 2024: 1 GB, GE8300
        [InlineData("AFTSSS", 880, MemoryTier.Low, 16, 32, 24, 1280, 20)]      // Stick 3rd gen: 1 GB, GE8300
        [InlineData("AFTMM", 1380, MemoryTier.Mid, 24, 48, 32, 1280, 20)]      // Stick 4K 1st gen: 1.5 GB but a GE8300
        [InlineData("AFTKA", 1700, MemoryTier.Mid, 24, 48, 32, 1920, 30)]      // Stick 4K Max 1st gen: 2 GB, GE9215
        [InlineData("AFTKM", 1700, MemoryTier.Mid, 24, 48, 32, 1920, 30)]      // Stick 4K 2nd gen
        [InlineData("AFTKRT", 1750, MemoryTier.Mid, 24, 48, 32, 1920, 30)]     // Stick 4K Max 2nd gen
        [InlineData("AFTMA08C15", 1750, MemoryTier.Mid, 24, 48, 32, 1920, 30)] // Stick 4K Plus
        [InlineData("AFTR", 1800, MemoryTier.Mid, 24, 48, 32, 1920, 30)]       // Cube 2nd gen
        public void EachStickGetsTheBudgetsTheDrawSizeAndTheBackgroundRateOfItsClass(string model, int reportedMiB, MemoryTier tier, int live, int timeshift, int vod, int longSide, int fps)
        {
            var p = AndroidDeviceProfile.From(Stick(model, reportedMiB));
            Assert.Equal(AndroidDeviceKind.FireTv, p.Kind);
            Assert.Equal(tier, p.Memory);
            Assert.Equal(live, p.LiveBufferMiB); Assert.Equal(timeshift, p.LiveTimeshiftMiB); Assert.Equal(vod, p.VodBufferMiB);
            Assert.Equal(longSide, p.MaxRenderLongSide);
            Assert.Equal(fps, p.AmbientFps);
            Assert.True(p.ReleaseScreenWhenNotPlaying);
        }

        [Fact]
        public void ABigFireTvTelevisionDrawsAtFullHdAndKeepsTheEnginesOwnBudgets()
        {
            var tv = Stick("AFTHA78451", 3500);              // 4 GB class
            var p = AndroidDeviceProfile.From(tv);
            Assert.Equal(MemoryTier.High, p.Memory);
            Assert.Equal(1920, p.MaxRenderLongSide); Assert.Equal(30, p.AmbientFps);
            var d = new AppConfig();
            Assert.Equal(d.LiveBufferMiB, p.LiveBufferMiB); Assert.Equal(d.LiveTimeshiftMiB, p.LiveTimeshiftMiB); Assert.Equal(d.VodBufferMiB, p.VodBufferMiB);
        }

        [Fact]
        public void WhenTheSystemDoesNotSayHowMuchMemoryThereIsTheDocumentedRamOfTheModelStandsIn()
        {
            Assert.Equal(MemoryTier.Low, AndroidDeviceProfile.DetectMemory(Stick("AFTSS", 0)));       // 1 GB
            Assert.Equal(MemoryTier.Mid, AndroidDeviceProfile.DetectMemory(Stick("AFTKA", 0)));       // 2 GB
            Assert.Equal(MemoryTier.High, AndroidDeviceProfile.DetectMemory(Stick("AFTHA78451", 0))); // a model that is not listed: do not punish it
        }

        [Fact]
        public void TheLowRamFlagWinsOverTheMemoryTotal()
        {
            var f = Stick("AFTKA", 1700); f.IsLowRamDevice = true;
            Assert.Equal(MemoryTier.Low, AndroidDeviceProfile.From(f).Memory);
        }

        [Fact]
        public void AWeakTvDeviceBackgroundIsExactlyThreeRefreshesAt60Hz()
        {
            var p = AndroidDeviceProfile.From(Stick("AFTSSS", 880));
            Assert.Equal(20, p.AmbientFps);
            Assert.InRange(p.AmbientInterval, 0.0499, 0.0501);
            // PApp draws a frame when the time since the last one is at least interval - 4 ms: at 60 Hz that is the third refresh (and the second one for 30 fps)
            Assert.Equal(3, RefreshesBetweenFrames(p.AmbientInterval, 60));
            Assert.Equal(2, RefreshesBetweenFrames(AndroidDeviceProfile.From(Stick("AFTKA", 1700)).AmbientInterval, 60));
        }

        private static int RefreshesBetweenFrames(double interval, int hz)
        {
            double refresh = 1.0 / hz; int n = 1;
            while (n * refresh < interval - 0.004) n++;
            return n;
        }

        [Fact]
        public void APhoneKeepsItsDrawSizeItsBackgroundAndItsScreenLockWhateverItsMemory()
        {
            var phone = new DeviceFacts { Manufacturer = "Samsung", Model = "SM-A515F", HasTouchscreen = true, TotalMemBytes = 900L * 1024 * 1024 };
            var p = AndroidDeviceProfile.From(phone);
            Assert.Equal(AndroidDeviceKind.Phone, p.Kind);
            Assert.Equal(30, p.AmbientFps);
            Assert.False(p.ReleaseScreenWhenNotPlaying);
            var rich = AndroidDeviceProfile.From(new DeviceFacts { HasTouchscreen = true, TotalMemBytes = 6000L * 1024 * 1024 });
            Assert.Equal(3200, rich.MaxRenderLongSide);
        }

        [Fact]
        public void TheConfigurationOverridesTheDrawSizeAndTheBackgroundRate()
        {
            var p = AndroidDeviceProfile.From(Stick("AFTSS", 880), null, 1600, 24);
            Assert.Equal(1600, p.MaxRenderLongSide); Assert.Equal(24, p.AmbientFps);
        }

        [Fact]
        public void DescribeNamesTheDocumentedModelTheGpuClassAndTheBackgroundRate()
        {
            string s = AndroidDeviceProfile.From(Stick("AFTSSS", 880)).Describe();
            Assert.Contains("FireTv", s); Assert.Contains("Fire TV Stick (3rd gen, 2020)", s); Assert.Contains("weak GPU", s); Assert.Contains("background 20 fps", s); Assert.Contains("1280", s);
        }

        // ------------------------------------------------------------------ the models

        [Fact]
        public void EveryStickTheAppCanInstallOnIsAFireOs6Or7Or8DeviceAndEveryOlderOneIsRefused()
        {
            foreach (var m in FireTvModels.All)
            {
                Assert.Equal(m.ApiLevel >= 25, m.CanInstall);
                if (m.FireOs == 5) Assert.Equal(22, m.ApiLevel);
                if (m.FireOs == 6) Assert.Equal(25, m.ApiLevel);
                if (m.FireOs == 7) Assert.Equal(28, m.ApiLevel);
                if (m.FireOs == 8) Assert.Equal(30, m.ApiLevel);
            }
            Assert.False(FireTvModels.Lookup("AFTT").CanInstall);             // Fire TV Stick 2nd gen: Fire OS 5
            Assert.True(FireTvModels.Lookup("AFTMM").CanInstall);             // Fire TV Stick 4K 1st gen: Fire OS 6, the oldest it runs on
        }

        [Fact]
        public void TheWeakGpusAreTheGe8300AndTheMali450()
        {
            foreach (string code in new[] { "AFTSS", "AFTSSS", "AFTMM", "AFTA", "AFTN" }) Assert.True(FireTvModels.Lookup(code).WeakGpu, code);
            foreach (string code in new[] { "AFTKA", "AFTKM", "AFTKRT", "AFTMA08C15", "AFTR", "AFTGAZL" }) Assert.False(FireTvModels.Lookup(code).WeakGpu, code);
        }

        [Fact]
        public void TheRamOfTheModelsIsWhatAmazonSays()
        {
            Assert.Equal(1024, FireTvModels.Lookup("AFTSS").RamMiB);          // Stick Lite
            Assert.Equal(1024, FireTvModels.Lookup("AFTSSS").RamMiB);         // Stick 3rd gen
            Assert.Equal(1536, FireTvModels.Lookup("AFTMM").RamMiB);          // Stick 4K 1st gen: 1.5 GB
            Assert.Equal(2048, FireTvModels.Lookup("AFTKA").RamMiB);          // Stick 4K Max 1st gen
            Assert.Equal(2048, FireTvModels.Lookup("AFTKRT").RamMiB);         // Stick 4K Max 2nd gen
            Assert.Null(FireTvModels.Lookup("Pixel"));
            Assert.Null(FireTvModels.Lookup(null));
        }

        [Fact]
        public void ModelCodesAreUniqueAndAllLookLikeFireTvCodes()
        {
            var codes = FireTvModels.All.Select(m => m.Code).ToList();
            Assert.Equal(codes.Count, new HashSet<string>(codes).Count);
            foreach (var c in codes) Assert.True(FireTvModels.IsFireTv("Amazon", c), c);
        }

        // ------------------------------------------------------------------ the options

        [Fact]
        public void TheNewOptionsAreReadFromThePlatformSection()
        {
            var cfg = AppConfig.Parse("{\"platform\":{\"ambientFps\":24,\"softwareAudio\":\"Force\",\"cast\":\"off\",\"hdmiPause\":false}}");
            var o = AndroidOptions.From(cfg.Platform);
            Assert.Equal(24, o.AmbientFps); Assert.Equal(SoftwareAudioMode.Force, o.SoftwareAudio); Assert.Equal(CastMode.Off, o.Cast); Assert.False(o.HdmiPause);
        }

        [Fact]
        public void TheNewOptionsDefaultToTheProfilesOwnBehaviour()
        {
            var o = AndroidOptions.From(null);
            Assert.Equal(0, o.AmbientFps); Assert.Equal(SoftwareAudioMode.Auto, o.SoftwareAudio); Assert.Equal(CastMode.Auto, o.Cast); Assert.True(o.HdmiPause);
            var bad = AndroidOptions.From(new Dictionary<string, object> { { "ambientFps", "fast" }, { "softwareAudio", "yes" }, { "cast", 3L }, { "hdmiPause", "no" } });
            Assert.Equal(0, bad.AmbientFps); Assert.Equal(SoftwareAudioMode.Auto, bad.SoftwareAudio); Assert.Equal(CastMode.Auto, bad.Cast); Assert.True(bad.HdmiPause);
        }

        [Fact]
        public void AmbientFpsIsClampedToWhatTheScreenCanShow()
        {
            Assert.Equal(60, AndroidOptions.From(new Dictionary<string, object> { { "ambientFps", 500L } }).AmbientFps);
            Assert.Equal(0, AndroidOptions.From(new Dictionary<string, object> { { "ambientFps", -5L } }).AmbientFps);
        }
    }
}
