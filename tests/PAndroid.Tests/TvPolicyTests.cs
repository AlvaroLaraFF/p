using System.Collections.Generic;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>The behaviours that Amazon's requirements for Fire TV apps ask of the shell: the screen, the HDMI link, Google Play services, and the audio output log.</summary>
    public class TvPolicyTests
    {
        // ------------------------------------------------------------------ the screen

        [Theory]
        // the player screen is open, a TV device, playback paused or held -> keep the screen on?
        [InlineData(true, true, false, true)]       // watching a channel on a Fire TV: yes
        [InlineData(true, true, true, false)]       // paused (or Alexa has the audio): let the screensaver start
        [InlineData(false, true, false, false)]     // in a menu: the interface does not ask
        [InlineData(false, true, true, false)]
        [InlineData(true, false, false, true)]      // a phone keeps the screen on while the player is open...
        [InlineData(true, false, true, true)]       // ...even on pause
        [InlineData(false, false, false, false)]
        public void TheScreenStaysOnOnlyWhileAChannelPlaysOnATvDevice(bool requested, bool releases, bool paused, bool expected)
        {
            Assert.Equal(expected, ScreenKeepPolicy.KeepOn(requested, releases, paused));
        }

        [Fact]
        public void OnlyTvDevicesReleaseTheScreenWhenNotPlaying()
        {
            var tv = new DeviceFacts { HasFireTvFeature = true, TotalMemBytes = 2000L << 20 };
            var phone = new DeviceFacts { HasTouchscreen = true, TotalMemBytes = 4000L << 20 };
            Assert.True(AndroidDeviceProfile.From(tv).ReleaseScreenWhenNotPlaying);
            Assert.False(AndroidDeviceProfile.From(phone).ReleaseScreenWhenNotPlaying);
            Assert.True(AndroidDeviceProfile.From(phone, AndroidDeviceKind.Tv).ReleaseScreenWhenNotPlaying);
        }

        // ------------------------------------------------------------------ HDMI

        private static KeyValuePair<int, int> Dev(int id, int type) { return new KeyValuePair<int, int>(id, type); }

        [Fact]
        public void TheHdmiOutputGoingAwayIsALossAndComingBackIsARestoration()
        {
            var s = new HdmiState();
            Assert.False(s.Connected);
            Assert.Equal(HdmiChange.Restored, s.Added(new[] { Dev(3, HdmiState.TypeHdmi) }));      // the first report, at registration, says there is one
            Assert.True(s.Connected);
            Assert.Equal(HdmiChange.Lost, s.Removed(new[] { Dev(3, HdmiState.TypeHdmi) }));
            Assert.False(s.Connected);
            Assert.Equal(HdmiChange.Restored, s.Added(new[] { Dev(4, HdmiState.TypeHdmi) }));      // it may come back under another id
        }

        [Fact]
        public void OnlyHdmiTypesCountAndEveryHdmiTypeDoes()
        {
            var s = new HdmiState();
            Assert.Equal(HdmiChange.None, s.Added(new[] { Dev(1, 2 /* built-in speaker */), Dev(2, 7 /* bluetooth sco */) }));
            Assert.False(s.Connected);
            Assert.Equal(HdmiChange.Restored, s.Added(new[] { Dev(5, HdmiState.TypeHdmiArc) }));
            Assert.Equal(HdmiChange.Lost, s.Removed(new[] { Dev(5, HdmiState.TypeHdmiArc) }));
            Assert.Equal(HdmiChange.Restored, s.Added(new[] { Dev(6, HdmiState.TypeHdmiEarc) }));
            Assert.True(HdmiState.IsHdmiType(9)); Assert.True(HdmiState.IsHdmiType(10)); Assert.True(HdmiState.IsHdmiType(29)); Assert.False(HdmiState.IsHdmiType(3));
        }

        [Fact]
        public void WithTwoHdmiOutputsLosingOneIsNotALoss()
        {
            var s = new HdmiState();
            s.Added(new[] { Dev(3, HdmiState.TypeHdmi), Dev(4, HdmiState.TypeHdmiArc) });
            Assert.Equal(HdmiChange.None, s.Removed(new[] { Dev(3, HdmiState.TypeHdmi) }));
            Assert.True(s.Connected);
            Assert.Equal(HdmiChange.Lost, s.Removed(new[] { Dev(4, HdmiState.TypeHdmiArc) }));
        }

        [Fact]
        public void RemovingWhatWasNeverThereChangesNothing()
        {
            var s = new HdmiState();
            Assert.Equal(HdmiChange.None, s.Removed(new[] { Dev(9, HdmiState.TypeHdmi) }));
            s.Added(new[] { Dev(3, HdmiState.TypeHdmi) });
            Assert.Equal(HdmiChange.None, s.Removed(new[] { Dev(9, HdmiState.TypeHdmi), Dev(2, 3) }));
            Assert.True(s.Connected);
        }

        [Fact]
        public void OnlyAFireTvOrABoxPausesWhenTheHdmiGoesAway()
        {
            Assert.True(HdmiPolicy.AppliesTo(AndroidDeviceKind.FireTv)); Assert.True(HdmiPolicy.AppliesTo(AndroidDeviceKind.Box));
            Assert.False(HdmiPolicy.AppliesTo(AndroidDeviceKind.Phone)); Assert.False(HdmiPolicy.AppliesTo(AndroidDeviceKind.Tablet)); Assert.False(HdmiPolicy.AppliesTo(AndroidDeviceKind.Tv));
            // long enough that a display changing its refresh rate (a link that drops and returns within a second) does not pause the channel, short enough to stop the stream soon
            Assert.InRange(HdmiPolicy.PauseAfterMs, 1000, 3000);
        }

        // ------------------------------------------------------------------ Google Play services and Cast

        [Theory]
        [InlineData(AndroidDeviceKind.Phone, true, CastMode.Auto, CastAvailability.Available)]
        [InlineData(AndroidDeviceKind.Tablet, true, CastMode.Auto, CastAvailability.Available)]
        [InlineData(AndroidDeviceKind.Tv, true, CastMode.Auto, CastAvailability.Available)]
        [InlineData(AndroidDeviceKind.Phone, false, CastMode.Auto, CastAvailability.NoPlayServices)]      // a phone without Google's apps
        [InlineData(AndroidDeviceKind.Tv, false, CastMode.Auto, CastAvailability.NoPlayServices)]
        [InlineData(AndroidDeviceKind.Box, false, CastMode.Auto, CastAvailability.NoPlayServices)]
        [InlineData(AndroidDeviceKind.FireTv, false, CastMode.Auto, CastAvailability.FireTv)]             // Fire OS: never
        [InlineData(AndroidDeviceKind.FireTv, true, CastMode.Auto, CastAvailability.FireTv)]              // not even if somebody sideloaded Google's services
        [InlineData(AndroidDeviceKind.Phone, true, CastMode.Off, CastAvailability.Disabled)]
        [InlineData(AndroidDeviceKind.FireTv, true, CastMode.Off, CastAvailability.Disabled)]
        public void CastIsOfferedOnlyWhereGooglePlayServicesAreAndTheConfigurationAllows(AndroidDeviceKind kind, bool services, CastMode mode, CastAvailability expected)
        {
            Assert.Equal(expected, CastPolicy.Decide(kind, services, mode));
        }

        [Fact]
        public void EveryAvailabilityHasItsOwnLogLine()
        {
            var seen = new HashSet<string>();
            foreach (CastAvailability a in System.Enum.GetValues(typeof(CastAvailability))) Assert.True(seen.Add(CastPolicy.Describe(a)));
            Assert.Contains("Fire TV", CastPolicy.Describe(CastAvailability.FireTv));
            Assert.Contains("available", CastPolicy.Describe(CastAvailability.Available));
        }

        // ------------------------------------------------------------------ the audio output log

        [Fact]
        public void AudioEncodingsAreNamedForTheLog()
        {
            Assert.Equal("[pcm16, ac3, e-ac3, e-ac3-joc]", AudioEncodingNames.List(new[] { 2, 5, 6, 18 }));
            Assert.Equal("encoding99", AudioEncodingNames.Name(99));
            Assert.Equal("dolby-mat", AudioEncodingNames.Name(19));
            Assert.Equal("[]", AudioEncodingNames.List(new int[0]));
        }
    }
}
