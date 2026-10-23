using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.OS;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>Android / Fire OS / device data for Settings: version, model, .NET runtime, memory and local IP.</summary>
    internal sealed class AndroidSystemInfo : ISystemInfo
    {
        private readonly Context _context;
        private readonly AndroidDeviceProfile _profile;
        public AndroidSystemInfo(Context applicationContext, AndroidDeviceProfile profile) { _context = applicationContext; _profile = profile; }

        public SystemInfoData Read()
        {
            var d = new SystemInfoData { OsName = _profile != null && _profile.Kind == AndroidDeviceKind.FireTv ? "Fire OS" : "Android" };
            try
            {
                d.OsVersion = (Build.VERSION.Release ?? "") + " (API " + (int)Build.VERSION.SdkInt + ")";
                string maker = Build.Manufacturer ?? "", model = Build.Model ?? "";
                d.Model = model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : (maker + " " + model).Trim();
            }
            catch (Exception) { }
            try { d.Runtime = RuntimeInformation.FrameworkDescription; } catch (Exception) { }
            try
            {
                var am = (ActivityManager)_context.GetSystemService(Context.ActivityService);
                var mi = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                d.MemoryTotalMiB = mi.TotalMem / (1 << 20); d.MemoryFreeMiB = mi.AvailMem / (1 << 20);
            }
            catch (Exception) { }
            try
            {
                d.LocalIp = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).FirstOrDefault() ?? "";
            }
            catch (Exception) { }
            return d;
        }
    }
}
