using System;
using System.IO;
using System.Runtime.InteropServices;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.App
{
    public class ProcMonitorTests
    {
        [Fact]
        public void ReadKbParsesProcStyleFilesAndIgnoresJunk()
        {
            string f = Path.Combine(Path.GetTempPath(), "p-meminfo-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(f, "MemTotal:        1536000 kB\nMemFree:          412000 kB\nThreads:\t17\nbad line\nEmpty:\nNotNumber:    abc kB\n");
                var d = ProcMonitor.ReadKb(f);
                Assert.Equal(1536000, d["MemTotal"]);
                Assert.Equal(412000, d["MemFree"]);
                Assert.Equal(17, d["Threads"]);
                Assert.False(d.ContainsKey("Empty")); Assert.False(d.ContainsKey("NotNumber"));
            }
            finally { try { File.Delete(f); } catch { } }
        }

        [Fact]
        public void ReadKbOfAMissingFileIsEmpty()
        {
            Assert.Empty(ProcMonitor.ReadKb(Path.Combine(Path.GetTempPath(), "p-does-not-exist-" + Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void SampleReportsCpuMemoryGcAndTheExtraText()
        {
            using (var m = new ProcMonitor(() => "ui{fps=60}"))
            {
                string first = m.Sample(false);
                Assert.Contains("cpu=", first); Assert.Contains("rss=", first); Assert.Contains("heap=", first); Assert.Contains("gc0/1/2=", first);
                Assert.EndsWith(" ui{fps=60}", first);
                double x = 0; for (int i = 0; i < 3000000; i++) x += Math.Sqrt(i);      // a little CPU between samples
                System.Threading.Thread.Sleep(80);
                string second = m.Sample(true);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Assert.Matches(@"cpu=\d+% system=(\d+|-)% rss=\d+MiB", second);        // Linux (Tizen and Android too): the numbers are real
                    Assert.Contains("samples=1", m.Summary());
                }
            }
        }

        [Fact]
        public void AFailingExtraCallbackDoesNotBreakTheSample()
        {
            using (var m = new ProcMonitor(() => { throw new InvalidOperationException("boom"); }))
                Assert.Contains("cpu=", m.Sample(false));
        }

        [Fact]
        public void SummaryOfAnEmptyMonitorIsZero()
        {
            using (var m = new ProcMonitor())
                Assert.Equal("samples=0 cpuAvg=0% cpuMax=0% rssMax=0MiB", m.Summary());
        }

        [Fact]
        public void ZeroIntervalDoesNotStartTheTimer()
        {
            using (var m = new ProcMonitor())
            {
                m.Start(0);
                Assert.Equal("samples=0 cpuAvg=0% cpuMax=0% rssMax=0MiB", m.Summary());
            }
        }
    }
}
