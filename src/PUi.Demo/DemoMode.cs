using System;
using System.Collections.Generic;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;

namespace PUi.Demo
{
    /// <summary>Interface demo mode (AppConfig.UiDemo): sample data and a scripted playback instead of the engine, to check fps and look on the device.</summary>
    public static class DemoMode
    {
        /// <summary>Services with the design's sample catalog, five recent channels, random link states (seed 7) and a playback that goes Connecting, Buffering and Playing by itself.</summary>
        public static TvServices Create(AppConfig config)
        {
            FakeCatalog cat; FakeChecker chk; FakeRecents rec; FakePlaybackFactory pb;
            var s = FakeServices.Demo(out cat, out chk, out rec, out pb);
            s.Config = config;
            var names = new[] { "M+ Liga de Campeones 1080p", "DAZN La Liga 1 FHD", "M+ Baloncesto 1080p", "Teledeporte 1080p", "Eurosport 2 1080p" };
            var list = new List<Channel>();
            foreach (var n in names) foreach (var c in cat.Channels) if (c.Name == n) { list.Add(c); break; }
            rec.Set(list);
            var rnd = new Random(7);
            foreach (var c in cat.Channels)
            {
                int r = rnd.Next(10);
                chk.Set(c, r < 6 ? LinkState.Good : r < 8 ? LinkState.Dead : LinkState.Checking, 3 + rnd.Next(120));
            }
            pb.Init = p =>
            {
                p.Script.Add(new KeyValuePair<int, Action<PlaybackStatus>>(600, st => { st.Phase = PlayPhase.Connecting; st.Peers = 3; }));
                p.Script.Add(new KeyValuePair<int, Action<PlaybackStatus>>(1800, st => { st.Phase = PlayPhase.Buffering; st.Peers = 6; st.BufferPercent = 40; st.DownBytesPerSec = 410 * 1024; }));
                p.Script.Add(new KeyValuePair<int, Action<PlaybackStatus>>(3500, st => { st.Phase = PlayPhase.Playing; st.IsLive = true; st.Peers = 9; st.Seeds = 2; st.DownBytesPerSec = 410 * 1024; st.UpBytesPerSec = 96 * 1024; st.TimeshiftWindowMs = 178000; }));
            };
            return s;
        }
    }
}
