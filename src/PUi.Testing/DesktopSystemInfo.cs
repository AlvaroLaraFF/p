using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PUi.Platform;

namespace PUi.Testing
{
    /// <summary>Desktop system data (for the captures and the tests).</summary>
    public sealed class DesktopSystemInfo : ISystemInfo
    {
        public static SystemInfoData Fixed;        // the captures use fixed data so they do not change from one run to the next

        public SystemInfoData Read()
        {
            if (Fixed != null) return Fixed;
            var d = new SystemInfoData();
            try
            {
                d.OsName = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "macOS";
                d.OsVersion = Environment.OSVersion.Version.ToString();
                d.Runtime = RuntimeInformation.FrameworkDescription;
                var gc = GC.GetGCMemoryInfo();
                d.MemoryTotalMiB = gc.TotalAvailableMemoryBytes / (1 << 20);
                d.MemoryFreeMiB = Math.Max(0, (gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes) / (1 << 20));
                d.LocalIp = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).FirstOrDefault() ?? "";
            }
            catch (Exception) { }
            return d;
        }
    }
}
