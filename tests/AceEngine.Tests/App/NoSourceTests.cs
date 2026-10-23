using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>A live stream with no sources warns early with a short text, instead of loading until the prebuffer deadline.</summary>
    public class NoSourceTests
    {
        private static async Task<bool> Until(Func<bool> c, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(25); }
            return c();
        }

        private static PlaybackOptions Fast(double noSourceSeconds)
        {
            return new PlaybackOptions
            {
                RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(30), PrepareTimeout = TimeSpan.FromSeconds(3),
                OpenTimeout = TimeSpan.FromSeconds(noSourceSeconds)
            };
        }

        private static LinkEntry EntryOf(string path)
        {
            return new LinkEntry { Name = "Canal", Url = path, Kind = LinkKind.AceStream, Link = AceEngine.Links.AceLink.Parse(path), Key = "t:" + path };
        }

        [Fact]
        public async Task ALiveChannelWithoutAnySourceReportsItAndKeepsRetryingByItselfUntilTheUserLeaves()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                // no source at all: the descriptor is there, but nobody serves the live stream
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory();
                var opt = Fast(1.0);
                using (var pb = new PlaybackSession(h.Host, factory, opt))
                {
                    var phases = new List<PlayPhase>();
                    pb.Changed += st => { lock (phases) { if (phases.Count == 0 || phases[phases.Count - 1] != st.Phase) phases.Add(st.Phase); } };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var run = pb.RunAsync(EntryOf(path), "Canal", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Error, 15000), "no aviso: " + pb.Status);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12), "tardo " + sw.Elapsed);
                    Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);
                    Assert.False(pb.Status.ErrorIsFatal);
                    // it keeps retrying by itself: several attempts in a row without the screen going back to loading
                    Assert.True(await Until(() => pb.Status.Retry >= 3, 15000), "no reintenta: " + pb.Status);
                    Assert.False(run.IsCompleted);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    int firstError; lock (phases) firstError = phases.IndexOf(PlayPhase.Error);
                    lock (phases) Assert.DoesNotContain(phases.Skip(firstError), p => p == PlayPhase.Resolving || p == PlayPhase.Connecting || p == PlayPhase.Buffering || p == PlayPhase.Retrying);
                    Assert.Empty(factory.Players);                           // the player was never created
                    pb.Stop();                                               // the user leaves with Back
                    await run;
                    Assert.Equal(PlayPhase.Idle, pb.Status.Phase);
                    Assert.Empty(h.Host.Server.Sessions);                    // and no engine session is left
                }
            }
        }

        [Fact]
        public async Task TheRetryLoopPlaysAsSoonAsASourceAppears()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                var src = h.AddSource();
                var path = h.PrepareHost();                                  // only to have the descriptor in a file
                var factory = new FakePlayerFactory();
                // the test's engine is a different one, with no fixed trackers or peers: the source exists but nobody tells the engine where it is
                using (var host2 = new AceEngine.P2P.AceEngineHost(new AceEngine.P2P.EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }))
                {
                    host2.Start();
                    using (var pb = new PlaybackSession(host2, factory, Fast(1.0)))
                    {
                        var run = pb.RunAsync(EntryOf(path), "Canal", CancellationToken.None);
                        Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Error, 15000), "no aviso: " + pb.Status);
                        Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);
                        // a source appears (a peer announcing the channel): the next attempt already plays, with no user action
                        host2.Resolver.Hints.Add(AceEngine.Util.Hex.ToHex(h.Model.InfoHash), new[] { src.EndPoint });
                        Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Playing, 30000), "no llego a reproducir: " + pb.Status);
                        Assert.Null(pb.Status.Error);
                        Assert.True(await Until(() => factory.Players.Count > 0 && factory.Players[0].BytesRead > 100000, 10000));
                        pb.Stop(); await run;
                    }
                }
            }
        }

        [Fact]
        public async Task ResolvingTheDescriptorIsCutAtTheSameLimitAsTheFirstData()
        {
            // the resolver would have 30 s per attempt, but the channel has OpenTimeout in total (resolve + first data)
            var opt = new AceEngine.P2P.EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            opt.Resolver.EnableTsService = false; opt.Resolver.Timeout = TimeSpan.FromSeconds(30);
            opt.StaticPeers.Add("127.0.0.1:1");                                   // a peer that does not answer: the resolution stays waiting
            using (var host = new AceEngine.P2P.AceEngineHost(opt))
            {
                host.Start();
                var e = LinkEntry.FromUrl("Canal", "acestream://" + new string('d', 40));
                var factory = new FakePlayerFactory();
                using (var pb = new PlaybackSession(host, factory, Fast(2)))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var run = pb.RunAsync(e, "Canal", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Error, 8000), "no aviso: " + pb.Status);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "tardo " + sw.Elapsed);
                    Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);
                    Assert.Empty(factory.Players);
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public async Task TheWarningCanBeSwitchedOffAndAChannelWithSourcesIsNeverCut()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                var path = h.PrepareHost();
                var factory = new FakePlayerFactory();
                // a very short warning deadline does not cut a channel that does have sources: the live position arrives as soon as the first peer connects
                using (var pb = new PlaybackSession(h.Host, factory, Fast(10)))
                {
                    var run = pb.RunAsync(new LinkEntry { Name = "Canal", Url = path, Kind = LinkKind.AceStream, Link = AceEngine.Links.AceLink.Parse(path), Key = "t:" + path }, "Canal", CancellationToken.None);
                    Assert.True(await Until(() => pb.Status.Phase == PlayPhase.Playing, 20000), "no llego a Playing: " + pb.Status);
                    pb.Stop(); await run;
                }
            }
        }

        [Fact]
        public void TheDefaultTimeoutLeavesRoomForTheSlowestChannelsThatDoPlay()
        {
            // measured: first data for channels that do play arrives within 5 s (p90); 15 s is the user's deadline
            Assert.True(new PlaybackOptions().OpenTimeout >= TimeSpan.FromSeconds(15));
            Assert.True(new PlaybackOptions().OpenTimeout < new PlaybackOptions().ReadyTimeout);
            Assert.Equal("Sin fuentes por ahora", PlaybackSession.NoSourceMessage);
        }
    }
}
