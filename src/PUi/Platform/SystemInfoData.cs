namespace PUi.Platform
{
    /// <summary>System data that Settings shows as informational rows. Each shell contributes its own (Tizen, Android, desktop); what is not known stays empty and is not shown.</summary>
    public sealed class SystemInfoData
    {
        /// <summary>"Tizen", "Android", "Windows"...</summary>
        public string OsName = "";
        /// <summary>"5.5", "13"...</summary>
        public string OsVersion = "";
        /// <summary>Device model (on the TV, Tizen.System.Information: "QE65Q64T"); empty if not obtained.</summary>
        public string Model = "";
        /// <summary>.NET runtime ("NET Core 3.0.0").</summary>
        public string Runtime = "";
        /// <summary>Free and total memory in MiB (0 if unknown).</summary>
        public long MemoryFreeMiB, MemoryTotalMiB;
        /// <summary>Local IP (IPv4) or empty.</summary>
        public string LocalIp = "";
    }
}
