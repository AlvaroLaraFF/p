using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>PlaybackSession with an in-memory live stream: pause, rewind, +/-10 s and LIVE over the whole engine and a fake player.</summary>
    internal static class TsRig
    {
        public const long Mib = 1024 * 1024;
        public const double TimeScale = 4;

        internal sealed class Rig : IDisposable
        {
            public LiveHarness H;
            public PlaybackSession Pb;
            public TsFakePlayerFactory F = new TsFakePlayerFactory();
            public Task Run;
            public readonly List<PlayPhase> Phases = new List<PlayPhase>();
            public readonly object Gate = new object();
            public LiveSession Live { get { return (LiveSession)Pb.EngineSession; } }
            /// <summary>Index in F.Players of the player the test starts with (after startup settles).</summary>
            public int Base;
            public int Count { get { return F.Count - Base; } }
            public TsFakePlayer At(int k) { return F.Players[Base + k]; }

            /// <summary>
            /// On startup, the live clock advances faster than real time while the engine reaches the head (the prebuffer takes a while to download and
            /// the live stream does not wait): that is real delay with respect to the live point. The tests wait for it to settle and return to live.
            /// </summary>
            public async Task SettleAsync()
            {
                var live = Live;
                double prev = live.GetTimeshift().EdgeMs;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long prevT = 0; int ok = 0;
                while (sw.Elapsed.TotalSeconds < 40 && ok < 3)
                {
                    await Task.Delay(500);
                    double edge = live.GetTimeshift().EdgeMs;
                    long t = sw.ElapsedMilliseconds;
                    double slope = (edge - prev) / Math.Max(1, t - prevT) * 1000.0;
                    ok = Math.Abs(slope - TimeScale * 1000) < 500 ? ok + 1 : 0;
                    prev = edge; prevT = t;
                }
                Assert.True(ok >= 3, "the live stream does not settle");
                if (Pb.Status.BehindLiveMs > 0 || !Pb.BehindLive.Equals(TimeSpan.Zero)) await Pb.GoLiveAsync();
                Assert.True(await Until(() => Pb.Status.Phase == PlayPhase.Playing && Pb.Status.BehindLiveMs == 0, 8000), "it did not return to live after settling: " + Pb.Status);
                Base = F.Count - 1;
            }

            public static async Task<Rig> StartAsync(long budget = 4 * Mib, Action<PlaybackOptions> tweak = null, Action<TsFakePlayer> configurePlayer = null, bool waitPlaying = true, bool settle = true)
            {
                var r = new Rig();
                r.H = TsHarness.Harness(TsHarness.Sim(o => o.TimeScale = TimeScale), budget, e => e.Live.BufferSeconds = 4);      // short live delay: startup takes less to settle
                r.H.AddSource();
                var path = r.H.PrepareHost();
                r.F.Configure = configurePlayer;
                var opt = new PlaybackOptions
                {
                    RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(30),
                    PrepareTimeout = TimeSpan.FromSeconds(5), TimeshiftMediaRate = TimeScale
                };
                if (tweak != null) tweak(opt);
                r.Pb = new PlaybackSession(r.H.Host, r.F, opt);
                r.Pb.Changed += s => { lock (r.Gate) { if (r.Phases.Count == 0 || r.Phases[r.Phases.Count - 1] != s.Phase) r.Phases.Add(s.Phase); } };
                var entry = new LinkEntry { Name = "Canal de prueba", Url = path, Kind = LinkKind.AceStream, Link = AceLink.Parse(path), Key = "t:" + path };
                r.Run = r.Pb.RunAsync(entry, "Canal de prueba", CancellationToken.None);
                if (waitPlaying) Assert.True(await Until(() => r.Pb.Status.Phase == PlayPhase.Playing, 30000), "no llego a Playing: " + r.Pb.Status);
                if (waitPlaying && settle && budget > 0) await r.SettleAsync();
                return r;
            }

            public async Task WaitWindowAsync(double seconds)
            {
                Assert.True(await Until(() => Pb.TimeshiftWindow.TotalSeconds >= seconds, 60000), "the window did not reach " + seconds + " s: " + Pb.TimeshiftWindow + " " + Pb.Status);
            }

            public PlayPhase[] PhasesCopy() { lock (Gate) return Phases.ToArray(); }

            public void Dispose()
            {
                try { Pb.Stop(); Run.Wait(TimeSpan.FromSeconds(10)); } catch { }
                try { Pb.Dispose(); } catch { }
                H.Dispose();
            }
        }

        public static async Task<bool> Until(Func<bool> c, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(25); }
            return c();
        }

        public static long BackOf(string url)
        {
            int i = url.IndexOf("back=", StringComparison.Ordinal);
            return i < 0 ? -1 : long.Parse(url.Substring(i + 5));
        }

    }
}
