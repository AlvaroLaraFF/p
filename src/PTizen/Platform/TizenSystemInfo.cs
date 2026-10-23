using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PUi.Platform;

namespace PTizen.Platform
{
    /// <summary>TV data for Settings: Tizen version and model (Tizen.System.Information), .NET runtime, memory (/proc/meminfo) and local IP.</summary>
    internal sealed class TizenSystemInfo : ISystemInfo
    {
        public SystemInfoData Read()
        {
            var d = new SystemInfoData { OsName = "Tizen" };
            d.OsVersion = Info("http://tizen.org/feature/platform.version");
            d.Model = Info("http://tizen.org/system/model_name");
            try { d.Runtime = RuntimeInformation.FrameworkDescription; } catch (Exception) { }
            try
            {
                long total = 0, free = -1, memFree = 0, buffers = 0, cached = 0;
                foreach (var line in File.ReadAllLines("/proc/meminfo"))
                {
                    var p = line.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 2) continue;
                    long kb; long.TryParse(p[1], out kb);
                    switch (p[0]) { case "MemTotal": total = kb; break; case "MemAvailable": free = kb; break; case "MemFree": memFree = kb; break; case "Buffers": buffers = kb; break; case "Cached": cached = kb; break; }
                }
                if (free < 0) free = memFree + buffers + cached;
                d.MemoryTotalMiB = total / 1024; d.MemoryFreeMiB = free / 1024;
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

        private static string Info(string key)
        {
            try { string v; return Tizen.System.Information.TryGetValue<string>(key, out v) && v != null ? v : ""; }
            catch (Exception) { return ""; }
        }
    }
}
