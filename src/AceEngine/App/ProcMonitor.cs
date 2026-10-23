using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;

namespace AceEngine.App
{
    /// <summary>
    /// Measures the process's CPU and memory (own counters in the log): /proc/self/stat (utime+stime), /proc/self/status (VmRSS, threads),
    /// /proc/stat (load of the whole system, including the video decoder) and the .NET GC. Every read failure is ignored (on a platform without /proc the numbers show "-").
    /// Shared by every shell (Tizen and Android are both Linux).
    /// </summary>
    public sealed class ProcMonitor : IDisposable
    {
        private Timer _timer;
        private long _lastProcTicks = -1, _lastTotal = -1, _lastIdle = -1;
        private DateTime _lastTime;
        private readonly object _gate = new object();
        private readonly Func<string> _extra;
        private int _samples;
        private double _cpuSum, _cpuMax;
        private long _rssMax;

        public ProcMonitor(Func<string> extra = null) { _extra = extra; }

        /// <summary>Values of the last summary: the process's average and maximum CPU (% of one core) and RSS peak in MiB.</summary>
        public string Summary()
        {
            lock (_gate)
                return "samples=" + _samples + " cpuAvg=" + (_samples == 0 ? 0 : _cpuSum / _samples).ToString("0", CultureInfo.InvariantCulture) + "% cpuMax=" + _cpuMax.ToString("0", CultureInfo.InvariantCulture) + "% rssMax=" + (_rssMax / 1024) + "MiB";
        }

        public void Start(int intervalSeconds)
        {
            if (intervalSeconds <= 0) return;
            Sample(false);
            _timer = new Timer(_ => { try { UdpLog.Info("stats", Sample(true)); } catch { } }, null, TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(intervalSeconds));
        }

        public void Dispose() { var t = _timer; _timer = null; if (t != null) t.Dispose(); }

        public static Dictionary<string, long> ReadKb(string path)
        {
            var res = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var rest = line.Substring(colon + 1).Trim();
                    int sp = rest.IndexOf(' ');
                    if (sp > 0) rest = rest.Substring(0, sp);
                    long v;
                    if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) res[line.Substring(0, colon)] = v;
                }
            }
            catch { }
            return res;
        }

        private static long Get(Dictionary<string, long> d, string k) { long v; return d.TryGetValue(k, out v) ? v : -1; }

        /// <summary>Status line: the process's and the system's CPU since the previous sample, memory, GC.</summary>
        public string Sample(bool record)
        {
            var now = DateTime.UtcNow;
            long procTicks = -1;
            try
            {
                var stat = File.ReadAllText("/proc/self/stat");
                int rp = stat.LastIndexOf(')');
                var f = stat.Substring(rp + 2).Split(' ');       // f[0] = state (field 3); utime = field 14 -> f[11]; stime = field 15 -> f[12]
                procTicks = long.Parse(f[11], CultureInfo.InvariantCulture) + long.Parse(f[12], CultureInfo.InvariantCulture);
            }
            catch { }
            long total = -1, idle = -1;
            try
            {
                var first = File.ReadAllLines("/proc/stat")[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long sum = 0;
                for (int i = 1; i < first.Length && i <= 8; i++) sum += long.Parse(first[i], CultureInfo.InvariantCulture);
                total = sum; idle = long.Parse(first[4], CultureInfo.InvariantCulture) + (first.Length > 5 ? long.Parse(first[5], CultureInfo.InvariantCulture) : 0);
            }
            catch { }
            double cpu = -1, sys = -1;
            lock (_gate)
            {
                if (_lastProcTicks >= 0 && procTicks >= 0)
                {
                    double dt = (now - _lastTime).TotalSeconds;
                    if (dt > 0.05) cpu = (procTicks - _lastProcTicks) / 100.0 / dt * 100.0;      // % of one core (CLK_TCK = 100)
                }
                if (_lastTotal >= 0 && total > _lastTotal) sys = 100.0 * (1.0 - (double)(idle - _lastIdle) / (total - _lastTotal));
                _lastProcTicks = procTicks; _lastTotal = total; _lastIdle = idle; _lastTime = now;
            }
            var st = ReadKb("/proc/self/status");
            var mi = ReadKb("/proc/meminfo");
            long rss = Get(st, "VmRSS");
            if (record && cpu >= 0)
                lock (_gate)
                {
                    _samples++; _cpuSum += cpu; if (cpu > _cpuMax) _cpuMax = cpu;
                    if (rss > _rssMax) _rssMax = rss;
                }
            string extra = null;
            try { if (_extra != null) extra = _extra(); } catch { }
            return "cpu=" + (cpu < 0 ? "-" : cpu.ToString("0", CultureInfo.InvariantCulture)) + "% system=" + (sys < 0 ? "-" : sys.ToString("0", CultureInfo.InvariantCulture)) + "%"
                + " rss=" + (rss < 0 ? "-" : (rss / 1024).ToString(CultureInfo.InvariantCulture)) + "MiB peak=" + (Get(st, "VmHWM") / 1024) + "MiB vm=" + (Get(st, "VmSize") / 1024) + "MiB threads=" + Get(st, "Threads")
                + " heap=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MiB gc0/1/2=" + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2)
                + " free=" + (Get(mi, "MemAvailable") >= 0 ? (Get(mi, "MemAvailable") / 1024) : (Get(mi, "MemFree") / 1024)) + "MiB"
                + (extra != null ? " " + extra : "");
        }
    }
}
