using System;
using AceEngine.Cast;
using PAndroid;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>The pure part of "Enviar a la tele" on Android: what a Chromecast can decode by its model, who offers the function, the notification of a sending and the permission it asks for.</summary>
    public class CastPureTests
    {
        // ------------------------------------------------------------------ profiles by model
        [Theory]
        [InlineData("Chromecast", "chromecast2")]                      // the user's 2015 one, and every other plain dongle: the model name does not say the generation
        [InlineData("chromecast", "chromecast2")]
        [InlineData("Chromecast Ultra", "chromecast3")]
        [InlineData("Chromecast with Google TV", "googletv")]
        [InlineData("Google TV Streamer", "googletv")]
        [InlineData("SHIELD Android TV", "googletv")]
        [InlineData("BRAVIA 4K GB", "chromecast2")]                    // a television with Cast built in: not known to be better, so the most restrictive
        [InlineData("Philips 55PUS8506/12", "chromecast2")]
        [InlineData("Mi Box S", "chromecast2")]
        [InlineData("", "chromecast2")]
        [InlineData(null, "chromecast2")]
        [InlineData("  CHROMECAST  ", "chromecast2")]
        public void TheProfileComesFromTheModelAndWhenInDoubtIsTheMostRestrictive(string model, string profileId)
        {
            var p = CastModels.ProfileFor(model);
            Assert.NotNull(p);
            Assert.Equal(profileId, p.Id);
        }

        [Theory]
        [InlineData("Chromecast Audio")]
        [InlineData("Google Home")]
        [InlineData("Google Home Mini")]
        [InlineData("Nest Mini")]
        [InlineData("Google Nest Audio")]
        public void SpeakersAreNotScreens(string model)
        {
            Assert.Equal(CastModelClass.AudioOnly, CastModels.Classify(model));
            Assert.Null(CastModels.ProfileFor(model));
            Assert.False(CastModels.IsScreen(false, false, model));
        }

        [Fact]
        public void TheCapabilityTheSdkReportsWinsOverTheModelName()
        {
            Assert.True(CastModels.IsScreen(true, true, "Google Home"));              // the SDK says it has video out
            Assert.False(CastModels.IsScreen(true, false, "Chromecast"));             // the SDK says it has none
            Assert.True(CastModels.IsScreen(false, false, "Chromecast"));             // not reported: the model decides
        }

        [Fact]
        public void TheProfilesAreTheEnginesAndGrowWithTheModel()
        {
            var c2 = CastModels.ProfileFor("Chromecast"); var c3 = CastModels.ProfileFor("Chromecast Ultra"); var gt = CastModels.ProfileFor("Google TV Streamer");
            Assert.Equal(41, c2.MaxH264Level); Assert.Equal(42, c3.MaxH264Level); Assert.Equal(51, gt.MaxH264Level);
            Assert.True(c2.MaxMacroblocksPerSecond < c3.MaxMacroblocksPerSecond && c3.MaxMacroblocksPerSecond < gt.MaxMacroblocksPerSecond);
            Assert.Equal(1920, c2.MaxWidth); Assert.Equal(3840, gt.MaxWidth);
            Assert.False(c2.AllowAc3);
        }

        // ------------------------------------------------------------------ who sends
        [Fact]
        public void OnlyAPhoneOrATabletOffersToSendAndATvAFireTvAndABoxAreReceivers()
        {
            Assert.True(SendPolicy.Offers(AndroidDeviceKind.Phone));
            Assert.True(SendPolicy.Offers(AndroidDeviceKind.Tablet));
            Assert.False(SendPolicy.Offers(AndroidDeviceKind.Tv));
            Assert.False(SendPolicy.Offers(AndroidDeviceKind.FireTv));
            Assert.False(SendPolicy.Offers(AndroidDeviceKind.Box));
        }

        [Fact]
        public void TheLogSaysWhatEachDeviceSendsTo()
        {
            Assert.Contains("receiver", SendPolicy.Describe(AndroidDeviceKind.FireTv, CastAvailability.FireTv));
            Assert.Contains("receiver", SendPolicy.Describe(AndroidDeviceKind.Tv, CastAvailability.Available));
            Assert.Contains("DLNA televisions and Google Cast", SendPolicy.Describe(AndroidDeviceKind.Phone, CastAvailability.Available));
            Assert.Contains("DLNA televisions only", SendPolicy.Describe(AndroidDeviceKind.Tablet, CastAvailability.NoPlayServices));
            Assert.Contains("DLNA televisions only", SendPolicy.Describe(AndroidDeviceKind.Phone, CastAvailability.Disabled));
        }

        [Fact]
        public void TheCastSdkIsOnlyEverUsedWhereGooglePlayServicesAreAndTheConfigurationAllowsIt()
        {
            Assert.Equal(CastAvailability.Available, CastPolicy.Decide(AndroidDeviceKind.Phone, true, CastMode.Auto));
            Assert.Equal(CastAvailability.NoPlayServices, CastPolicy.Decide(AndroidDeviceKind.Phone, false, CastMode.Auto));
            Assert.Equal(CastAvailability.NoPlayServices, CastPolicy.Decide(AndroidDeviceKind.Tablet, false, CastMode.Auto));      // a Fire tablet
            Assert.Equal(CastAvailability.FireTv, CastPolicy.Decide(AndroidDeviceKind.FireTv, true, CastMode.Auto));              // never, not even asked
            Assert.Equal(CastAvailability.Disabled, CastPolicy.Decide(AndroidDeviceKind.Phone, true, CastMode.Off));
        }

        // ------------------------------------------------------------------ the notification
        [Fact]
        public void TheNotificationSaysWhereAndWhatIsBeingSent()
        {
            Assert.Equal("Enviando a Salón", CastNotificationText.Title("Salón"));
            Assert.Equal("Enviando a [TV] Samsung Q6 Series (65)", CastNotificationText.Title("  [TV]  Samsung\tQ6 Series (65) \n"));
            Assert.Equal("Enviando a la tele", CastNotificationText.Title(""));
            Assert.Equal("Enviando a la tele", CastNotificationText.Title(null));
            Assert.Equal("M+ Liga de Campeones 1080p", CastNotificationText.Text("M+ Liga de Campeones 1080p", false));
            Assert.Equal("En pausa · M+ Liga de Campeones 1080p", CastNotificationText.Text("M+ Liga de Campeones 1080p", true));
            Assert.Equal("En pausa", CastNotificationText.Text("", true));
            Assert.Equal("", CastNotificationText.Text(null, false));
        }

        [Fact]
        public void LongOrOddNamesAreCutAndCleaned()
        {
            string longName = new string('x', 200);
            var t = CastNotificationText.Title(longName);
            Assert.True(t.Length <= "Enviando a ".Length + CastNotificationText.MaxDevice);
            Assert.EndsWith("…", t);
            var c = CastNotificationText.Text(longName, false);
            Assert.Equal(CastNotificationText.MaxChannel, c.Length);
            Assert.Equal("a b", CastNotificationText.Clean("a\u0000\u0007b", 10, "-"));          // a control character is a separator, never printed
            Assert.Equal("a b", CastNotificationText.Clean("a\r\n\r\n   b", 10, "-"));
            Assert.Equal("-", CastNotificationText.Clean(" \t ", 10, "-"));
        }

        [Fact]
        public void TheNotificationHasItsOwnIdentifiersAndTheStopLabelIsTheOneOfTheScreen()
        {
            Assert.Equal("Dejar de enviar", CastNotificationText.StopLabel);
            Assert.StartsWith("org.p.app.", CastNotificationText.ActionStop);
            Assert.NotEqual(0, CastNotificationText.NotificationId);
            Assert.False(string.IsNullOrEmpty(CastNotificationText.ChannelId));
            Assert.Equal("Envío a otra pantalla", CastNotificationText.ChannelName);
        }

        // ------------------------------------------------------------------ the permission
        [Theory]
        [InlineData(24, false, 0, NotificationAsk.No)]          // before Android 13 there is nothing to ask
        [InlineData(32, false, 0, NotificationAsk.No)]
        [InlineData(33, false, 0, NotificationAsk.Ask)]         // the first time a channel is sent
        [InlineData(33, false, 1, NotificationAsk.Ask)]         // refused once: once more is allowed
        [InlineData(33, false, 2, NotificationAsk.No)]          // refused twice: the system will not show it any more, so P! does not ask
        [InlineData(34, true, 0, NotificationAsk.No)]           // granted
        [InlineData(35, false, 5, NotificationAsk.No)]
        public void TheNotificationPermissionIsAskedForWithGraceAndNeverNagged(int sdk, bool granted, int asked, NotificationAsk expected)
        {
            Assert.Equal(expected, NotificationPermissionPolicy.Decide(sdk, granted, asked));
        }

        // ------------------------------------------------------------------ the foreground service
        [Fact]
        public void TheForegroundServiceIsAMediaPlaybackOneFromAndroidTenAndStartedTheWayEachApiWants()
        {
            Assert.Equal(0, ForegroundServicePolicy.TypeFor(25));
            Assert.Equal(0, ForegroundServicePolicy.TypeFor(28));
            Assert.Equal(2, ForegroundServicePolicy.TypeFor(29));      // FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            Assert.Equal(2, ForegroundServicePolicy.TypeFor(34));
            Assert.Equal(2, ForegroundServicePolicy.TypeFor(35));
            Assert.False(ForegroundServicePolicy.NeedsStartForegroundService(25));
            Assert.True(ForegroundServicePolicy.NeedsStartForegroundService(26));
            Assert.False(ForegroundServicePolicy.NeedsNotificationChannel(25));
            Assert.True(ForegroundServicePolicy.NeedsNotificationChannel(26));
        }
    }
}
