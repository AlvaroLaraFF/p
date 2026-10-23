using System.Collections.Generic;
using System.Threading.Tasks;
using AceEngine.App;
using Xunit;

namespace AceEngine.Tests.App
{
    public class StartupDiagnosticsTests
    {
        [Fact]
        public void RuntimeLineDescribesTheRuntime()
        {
            string l = StartupDiagnostics.RuntimeLine();
            Assert.Contains("runtime=", l); Assert.Contains("cpus=", l); Assert.Contains("64bit=", l);
        }

        [Fact]
        public void LogMemoryNeverThrows()
        {
            StartupDiagnostics.LogMemory();
        }

        [Fact]
        public async Task EngineSelfTestPassesWithoutNetwork()
        {
            var lines = new List<string>();
            bool ok = await StartupDiagnostics.RunEngineSelfTestAsync(new AppConfig { DiagnosticsNetwork = false }, lines.Add);
            Assert.True(ok, string.Join("\n", lines));
            Assert.Contains(lines, x => x.StartsWith("ed25519: OK"));
            Assert.Contains(lines, x => x.StartsWith("aes-cbc") && x.EndsWith("OK"));
            Assert.Contains(lines, x => x.Contains("local HTTP server responds: HTTP/1.1 404"));
            Assert.Equal("self-diagnosis OK", lines[lines.Count - 1]);
        }
    }
}
