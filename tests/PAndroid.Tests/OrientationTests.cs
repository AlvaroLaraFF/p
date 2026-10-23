using PUi.Platform;
using Xunit;

namespace PAndroid.Tests
{
    public class OrientationTests
    {
        [Theory]
        [InlineData(AndroidDeviceKind.Tv, ScreenLock.SensorLandscape)]
        [InlineData(AndroidDeviceKind.FireTv, ScreenLock.SensorLandscape)]
        [InlineData(AndroidDeviceKind.Box, ScreenLock.SensorLandscape)]
        [InlineData(AndroidDeviceKind.Phone, ScreenLock.Portrait)]
        [InlineData(AndroidDeviceKind.Tablet, ScreenLock.Unspecified)]
        public void EachKindOfDeviceStartsWithItsOwnLock(AndroidDeviceKind kind, ScreenLock expected) { Assert.Equal(expected, OrientationPolicy.Startup(kind)); }

        [Theory]
        [InlineData(OrientationRequest.Landscape, ScreenLock.SensorLandscape)]
        [InlineData(OrientationRequest.Portrait, ScreenLock.Portrait)]
        [InlineData(OrientationRequest.Device, ScreenLock.Portrait)]
        public void APhoneTurnsForFullScreenAndComesBack(OrientationRequest request, ScreenLock expected) { Assert.Equal(expected, OrientationPolicy.For(AndroidDeviceKind.Phone, request)); }

        [Theory]
        [InlineData(OrientationRequest.Landscape, ScreenLock.SensorLandscape)]
        [InlineData(OrientationRequest.Portrait, ScreenLock.Portrait)]
        [InlineData(OrientationRequest.Device, ScreenLock.Unspecified)]
        public void ATabletGoesBackToTheSensorWhenItIsAskedForTheDevicesOwnOrientation(OrientationRequest request, ScreenLock expected) { Assert.Equal(expected, OrientationPolicy.For(AndroidDeviceKind.Tablet, request)); }

        [Theory]
        [InlineData(AndroidDeviceKind.Tv)]
        [InlineData(AndroidDeviceKind.FireTv)]
        [InlineData(AndroidDeviceKind.Box)]
        public void ATvIgnoresEveryRequest(AndroidDeviceKind kind)
        {
            foreach (OrientationRequest r in new[] { OrientationRequest.Device, OrientationRequest.Portrait, OrientationRequest.Landscape })
                Assert.Equal(ScreenLock.SensorLandscape, OrientationPolicy.For(kind, r));
        }

        [Fact]
        public void TheBarsAreHiddenOnATvAndInFullScreenOnly()
        {
            Assert.True(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Tv, OrientationRequest.Device));
            Assert.True(OrientationPolicy.WantsImmersive(AndroidDeviceKind.FireTv, OrientationRequest.Portrait));
            Assert.False(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Phone, OrientationRequest.Device));
            Assert.False(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Phone, OrientationRequest.Portrait));
            Assert.True(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Phone, OrientationRequest.Landscape));
            Assert.False(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Tablet, OrientationRequest.Device));
            Assert.True(OrientationPolicy.WantsImmersive(AndroidDeviceKind.Tablet, OrientationRequest.Landscape));
        }

        [Fact]
        public void OnlyPhonesAndTabletsAreTouchDevices()
        {
            Assert.True(OrientationPolicy.IsTouchDevice(AndroidDeviceKind.Phone)); Assert.True(OrientationPolicy.IsTouchDevice(AndroidDeviceKind.Tablet));
            Assert.False(OrientationPolicy.IsTouchDevice(AndroidDeviceKind.Tv)); Assert.False(OrientationPolicy.IsTouchDevice(AndroidDeviceKind.FireTv)); Assert.False(OrientationPolicy.IsTouchDevice(AndroidDeviceKind.Box));
        }
    }
}
