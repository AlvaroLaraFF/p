using System.Linq;
using AceEngine.App;
using AceEngine.Probe;
using PUi.Demo;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The interface demo mode (what AppConfig.UiDemo shows on the device): sample data and a scripted playback, without the engine.</summary>
    public class DemoModeTests
    {
        [Fact]
        public void BuildsServicesWithRecentsLinkStatesAndAScriptedPlayback()
        {
            var cfg = new AppConfig { UiDemo = true };
            var s = DemoMode.Create(cfg);
            Assert.Same(cfg, s.Config);
            Assert.Equal(5, s.Recents.Items.Count);
            Assert.Equal("M+ Liga de Campeones 1080p", s.Recents.Items[0].Name);
            Assert.True(s.Catalog.Channels.Count > 40);
            // random states with a fixed seed: the same on every run, with live, dead and checking channels
            var states = s.Catalog.Channels.Select(c => s.Checker.GetStatus(c).State).ToList();
            Assert.Contains(LinkState.Good, states); Assert.Contains(LinkState.Dead, states); Assert.Contains(LinkState.Checking, states);
            var again = DemoMode.Create(cfg);
            Assert.Equal(states, again.Catalog.Channels.Select(c => again.Checker.GetStatus(c).State).ToList());
            var pb = (ScriptedPlayback)s.Playback.Create();
            Assert.Equal(3, pb.Script.Count);
        }
    }
}
