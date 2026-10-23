using System;
using System.Linq;
using AceEngine.App;
using AceEngine.Probe;
using PUi.Core;
using PUi.Platform;
using PUi.Screens;
using PUi.Testing;

namespace PUi.Tests
{
    /// <summary>What the touch tests share: a phone to play on, ways to find a touch target by its id and to put a finger on it.</summary>
    internal static class TouchRig
    {
        public static string Fonts { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"); } }

        /// <summary>A phone (2400x1080, cutout, gesture bar, touch only) with the app started on the TV section.</summary>
        public static Scenes.Rig Phone(Action<Scenes.Rig> setup = null, Device device = null)
        {
            var r = Scenes.NewRig(Fonts, device ?? Device.Phone);
            if (setup != null) setup(r);
            r.App.Start();
            r.Advance(0.5);
            return r;
        }

        public static TouchTarget Target(Scenes.Rig r, string id)
        {
            var t = r.App.Current.TouchTargets().FirstOrDefault(x => x.Id == id);
            if (t == null) throw new InvalidOperationException("no touch target '" + id + "' on " + r.App.Describe() + "; there are: " + string.Join(", ", r.App.Current.TouchTargets().Select(x => x.Id)));
            return t;
        }

        public static bool Has(Scenes.Rig r, string id) { return r.App.Current.TouchTargets().Any(x => x.Id == id); }

        public static float CenterX(TouchTarget t) { return (t.Rect.Left + t.Rect.Right) / 2; }
        public static float CenterY(TouchTarget t) { return (t.Rect.Top + t.Rect.Bottom) / 2; }

        public static void TapOn(Scenes.Rig r, string id) { var t = Target(r, id); r.Tap(CenterX(t), CenterY(t)); }

        /// <summary>The "Liga de Campeones" list opened on a phone, with every channel live.</summary>
        public static Scenes.Rig Category()
        {
            var r = Phone();
            var list = r.Catalog.Categories.First(c => c.Name == "Liga de Campeones").Channels.ToList();
            r.Checker.Default = LinkState.Checking;
            for (int i = 0; i < list.Count; i++) r.Checker.Set(list[i], LinkState.Good, 10 + i);
            r.App.Push(new CategoryScreen("Liga de Campeones", list, Section.Tv));
            r.Advance(1.0);
            return r;
        }

        /// <summary>A list that is longer than the screen: every channel of the demo catalog, all live.</summary>
        public static Scenes.Rig BigCategory()
        {
            var r = Phone();
            var list = r.Catalog.Channels.ToList();
            r.Checker.Default = LinkState.Checking;
            for (int i = 0; i < list.Count; i++) r.Checker.Set(list[i], LinkState.Good, 10 + i % 50);
            r.App.Push(new CategoryScreen("Todos", list, Section.Tv));
            r.Advance(1.0);
            return r;
        }

        /// <summary>The player open on the first channel of the Liga list, playing live with 178 s in memory (or whatever the state says).</summary>
        public static Scenes.Rig Player(Action<PlaybackStatus> state = null, Device device = null)
        {
            var r = Phone(null, device);
            var pc = new PlayContext(r.Catalog.Categories.First(c => c.Name == "Liga de Campeones").Channels, 0);
            r.Checker.Set(pc.Peek(1), LinkState.Good, 74);
            r.App.Push(new PlayerScreen(pc));
            Scenes.WaitStarted(r.Playback.Created.Last());
            r.Playback.Created.Last().Push(s =>
            {
                s.Phase = PlayPhase.Playing; s.IsLive = true; s.Peers = 9; s.DownBytesPerSec = 410 * 1024; s.TimeshiftWindowMs = 178000; s.BehindLiveMs = 0;
                if (state != null) state(s);
            });
            r.Advance(r.Now + 0.2);
            return r;
        }

        public static string[] Calls(Scenes.Rig r) { return r.Playback.Created.Last().Calls.ToArray(); }
    }
}
