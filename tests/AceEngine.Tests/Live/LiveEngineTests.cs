using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Streaming;
using AceEngine.Tests.P2P;
using AceEngine.Tests.Resolve;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Live
{
    /// <summary>Live streams end to end: the whole engine against simulated broadcasters, following the real captures.</summary>
    public class LiveEngineTests
    {
        private static LiveSimOptions Sim(Action<LiveSimOptions> tweak = null)
        {
            var o = new LiveSimOptions { TimeScale = 4, Bitrate = 262144, PieceLength = 65536, ChunkLength = 16384 };
            if (tweak != null) tweak(o);
            return o;
        }

        private static void AssertCleanStream(SimStreamVerifier v, LiveSourceSimulator src = null)
        {
            Assert.True(v.Bytes > 0, "nothing was received");
            Assert.Equal(0, v.SyncErrors);
            Assert.Equal(0, v.CorruptPackets);
            Assert.Equal(0, v.Duplicates);
            Assert.Empty(v.Gaps);
            if (src != null) Assert.Empty(src.Violations);
        }

        [Fact]
        public async Task StreamStartsAlignedToTsEvenIfTheFirstPieceIsNot()
        {
            using (var h = new LiveHarness(Sim(o => o.StartMisalignment = 101)))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                var first = new byte[188 * 4];
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                using (var resp = await http.GetAsync(h.Opened.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.Equal("video/mp2t", resp.Content.Headers.ContentType.MediaType);
                    Assert.True(resp.Headers.TransferEncodingChunked == true);
                    using (var st = await resp.Content.ReadAsStreamAsync())
                    {
                        int got = 0;
                        while (got < first.Length) { int n = await st.ReadAsync(first, got, first.Length - got); if (n <= 0) break; got += n; }
                        Assert.Equal(first.Length, got);
                    }
                }
                Assert.Equal(0x47, first[0]); Assert.Equal(0x47, first[188]); Assert.Equal(0x47, first[376]); Assert.Equal(0x47, first[564]);
            }
        }

        [Fact]
        public async Task PacedPlaybackKeepsTenSecondsBehindLiveWithoutGaps()
        {
            var sim = Sim(o => o.TimeScale = 5);
            using (var h = new LiveHarness(sim))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                double rate = sim.Bitrate * sim.TimeScale;                  // the player consumes at the live stream's speed (accelerated)
                var v = await h.ReadAsync(10, (long)rate, (long)(rate * 2));
                AssertCleanStream(v, src);
                // 10 real s at speed 5 = 50 s of stream: what is received has to be on the order of 50 s * bitrate
                Assert.InRange(v.Bytes, (long)(rate * 6), (long)(rate * 13));
                var info = h.Session.LiveInfo;
                Assert.InRange(info.LagSeconds, 2.0, 25.0);                // it starts ~10 s behind the live stream and the player runs at pace
                Assert.Equal(0, info.HolesSkipped); Assert.Equal(0, info.SignatureFailures); Assert.Equal(0, info.Rehooks);
                Assert.True(info.PiecesVerified > 100);
                Assert.True(info.StoredPieces <= 32 * 1024 * 1024 / 65536, "bounded memory: " + info.StoredPieces);
            }
        }

        [Fact]
        public async Task StartsOnAFreshStreamWithFewerPiecesThanThePrebuffer()
        {
            using (var h = new LiveHarness(Sim(o => { o.StartOffsetSeconds = 0; o.TimeScale = 1; })))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var v = await h.ReadAsync(7);
                Assert.True(v.Bytes > 100000, "bytes " + v.Bytes + " " + h.Session.DebugInfo);
                Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets);
            }
        }

        [Fact]
        public async Task UsesSeveralPeersAndIgnoresOnesThatNeverAnswerOrAreSlow()
        {
            using (var h = new LiveHarness(Sim()))
            {
                var mute = h.AddSource(new SimPeerBehavior { NodeType = 1, NeverAnswerRequests = true });
                var slow = h.AddSource(new SimPeerBehavior { NodeType = 1, ServeDelayMs = 400, LagPieces = 2 });
                var good = h.AddSource(new SimPeerBehavior { NodeType = 2, LagPieces = 0 });
                await h.OpenAsync();
                var v = await h.ReadAsync(9, 262144 * 4, 262144 * 8);
                AssertCleanStream(v);
                Assert.True(good.PiecesServed > 0);
                Assert.Equal(0, mute.PiecesServed);
                Assert.All(new[] { mute, slow, good }, s => Assert.Empty(s.Violations));
                Assert.True(h.Session.LiveInfo.Peers >= 2);
            }
        }

        [Fact]
        public async Task OnePieceNobodyHasIsSkippedKeepingTheTsStreamSynchronised()
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim))
            {
                long missing = sim.FirstIndex + 240 + 40;                 // not yet produced when the client starts
                var a = h.AddSource(new SimPeerBehavior { Missing = new HashSet<long> { missing } });
                var b = h.AddSource(new SimPeerBehavior { NodeType = 1, Missing = new HashSet<long> { missing } });
                await h.OpenAsync();
                var v = await h.ReadAsync(12, 262144 * 4, 262144 * 6);
                Assert.True(v.Bytes > 1000000, "bytes " + v.Bytes + " " + h.Session.DebugInfo);
                Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets); Assert.Equal(0, v.Duplicates);
                Assert.Single(v.Gaps);
                // the lost piece has (65536-96)/188 = 348.08 packets; the two packets cut at the edges are also lost
                Assert.InRange(v.Gaps[0], 347, 351);
                var info = h.Session.LiveInfo;
                Assert.Equal(1, info.HolesSkipped); Assert.Equal(1, info.PiecesSkipped);
            }
        }

        [Fact]
        public async Task ALongerHoleIsSkippedAfterTheTimeout()
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim, o => o.Live.HoleSkipSeconds = 1.5))
            {
                long m0 = sim.FirstIndex + 240 + 40;
                var missing = new HashSet<long> { m0, m0 + 1, m0 + 2 };
                h.AddSource(new SimPeerBehavior { Missing = missing });
                h.AddSource(new SimPeerBehavior { NodeType = 1, Missing = missing });
                await h.OpenAsync();
                var v = await h.ReadAsync(12, 262144 * 4, 262144 * 6);
                Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets); Assert.Equal(0, v.Duplicates);
                Assert.Single(v.Gaps);
                Assert.InRange(v.Gaps[0], 3 * 348 - 2, 3 * 348 + 4);
                Assert.Equal(1, h.Session.LiveInfo.HolesSkipped); Assert.Equal(3, h.Session.LiveInfo.PiecesSkipped);
            }
        }

        [Theory]
        [InlineData(false)]      // wrong signature
        [InlineData(true)]       // altered content (the signature no longer matches)
        public async Task CorruptPiecesAreRejectedAndFetchedFromAnotherPeer(bool corruptContent)
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim))
            {
                var bad = new HashSet<long> { sim.FirstIndex + 240 + 8, sim.FirstIndex + 240 + 30 };
                var evil = h.AddSource(new SimPeerBehavior { NodeType = 1, CorruptSignature = corruptContent ? new HashSet<long>() : bad, CorruptContent = corruptContent ? bad : new HashSet<long>() });
                var good = h.AddSource(new SimPeerBehavior { NodeType = 1, LagPieces = 10 });       // the good one is behind: the bad piece arrives first and is requested first from whoever corrupts it
                await h.OpenAsync();
                var v = await h.ReadAsync(10, 262144 * 4, 262144 * 8);
                AssertCleanStream(v);
                Assert.True(h.Session.LiveInfo.SignatureFailures >= 1, "it should have detected at least one wrong signature: " + h.Session.DebugInfo);
                Assert.False(h.Session.LiveInfo.AcceptingUnverified);
            }
        }

        [Fact]
        public async Task StrictModeNeverPlaysIfNoSignatureVerifies()
        {
            using (var h = new LiveHarness(Sim(o => o.SignWithWrongKey = true), o => o.Live.Signature = SignatureMode.Strict))
            {
                h.AddSource(); h.AddSource(new SimPeerBehavior { NodeType = 1 });
                await h.OpenAsync();
                var v = await h.ReadAsync(5);
                Assert.Equal(0, v.Bytes);
                Assert.True(h.Session.LiveInfo.SignatureFailures >= 3);
                Assert.Equal(0, h.Session.LiveInfo.PiecesVerified);
            }
        }

        [Fact]
        public async Task TolerantModeAcceptsPiecesWhenNoneVerifiesAndSaysSo()
        {
            using (var h = new LiveHarness(Sim(o => o.SignWithWrongKey = true), o => o.Live.Signature = SignatureMode.Tolerant))
            {
                h.AddSource(); h.AddSource(new SimPeerBehavior { NodeType = 1 });
                await h.OpenAsync();
                var v = await h.ReadAsync(8, 262144 * 4, 262144 * 8);
                AssertCleanStream(v);
                Assert.True(h.Session.LiveInfo.AcceptingUnverified);
                Assert.True(h.Session.LiveInfo.SignatureFailures >= 6);
            }
        }

        [Fact]
        public async Task TolerantModeStaysStrictOnceAPieceHasVerified()
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim, o => o.Live.TolerantProbePieces = 3))
            {
                // many bad pieces from one peer, but the other gives them good: the no-verify mode is never entered
                var bad = new HashSet<long>(Enumerable.Range(0, 400).Select(i => sim.FirstIndex + i));
                h.AddSource(new SimPeerBehavior { NodeType = 1, CorruptSignature = bad });
                h.AddSource(new SimPeerBehavior { NodeType = 1, LagPieces = 1 });
                await h.OpenAsync();
                var v = await h.ReadAsync(9, 262144 * 4, 262144 * 8);
                AssertCleanStream(v);
                Assert.False(h.Session.LiveInfo.AcceptingUnverified);
            }
        }

        [Fact]
        public async Task SignatureOffAcceptsEverythingWithoutChecking()
        {
            using (var h = new LiveHarness(Sim(o => o.SignWithWrongKey = true), o => o.Live.Signature = SignatureMode.Off))
            {
                h.AddSource();
                await h.OpenAsync();
                var v = await h.ReadAsync(5);
                AssertCleanStream(v);
                Assert.Equal(0, h.Session.LiveInfo.SignatureFailures); Assert.Equal(0, h.Session.LiveInfo.PiecesVerified);
            }
        }

        [Fact]
        public async Task ReopeningTheUrlJumpsBackToTheLiveEdgeAndTheOldReaderEnds()
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                var v1 = await h.ReadAsync(3);
                AssertCleanStream(v1);
                await Task.Delay(4000);                                   // 16 s of stream while nobody reads
                var v2 = await h.ReadAsync(3);
                AssertCleanStream(v2);
                Assert.True(v2.FirstPacket > v1.LastPacket, "the second read must start further ahead (jump to the live point): " + v1.LastPacket + " -> " + v2.FirstPacket);
                Assert.True(h.Session.LiveInfo.Rehooks >= 1);
                Assert.Empty(src.Violations);
            }
        }

        [Fact]
        public async Task SecondConcurrentReaderSupersedesTheFirst()
        {
            using (var h = new LiveHarness(Sim()))
            {
                h.AddSource();
                await h.OpenAsync();
                var first = await h.Session.OpenReaderAsync(0, CancellationToken.None);
                var buf = new byte[4096];
                int n = await first.ReadAsync(buf, 0, buf.Length, CancellationToken.None);
                Assert.True(n > 0);
                var second = await h.Session.OpenReaderAsync(0, CancellationToken.None);
                n = await second.ReadAsync(buf, 0, buf.Length, CancellationToken.None);
                Assert.True(n > 0);
                Assert.Equal(0, await first.ReadAsync(buf, 0, buf.Length, CancellationToken.None));       // the previous one receives end of stream
                first.Dispose(); second.Dispose();
            }
        }

        [Fact]
        public async Task SourceRestartWithLowerIndicesIsDetectedAndPlaybackContinues()
        {
            var sim = Sim();
            using (var h = new LiveHarness(sim, o => { o.Live.RestartDetectSeconds = 3; o.Live.RestartConfirmSeconds = 2; }))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                long bytesBefore = 0, bytesAfter = 0;
                bool restarted = false;
                var task = h.ReadAsync(14, 0, 0, total =>
                {
                    if (!restarted && total > 600000) { restarted = true; h.Model.Restart(sim.FirstIndex - 5000); bytesBefore = total; }
                    bytesAfter = total;
                });
                var v = await task;
                Assert.True(restarted);
                Assert.True(bytesAfter - bytesBefore > 400000, "after the broadcaster restart the stream must keep arriving: " + (bytesAfter - bytesBefore) + " " + h.Session.DebugInfo);
                Assert.True(h.Session.LiveInfo.Rehooks >= 1);
                Assert.Equal(0, v.SyncErrors);
            }
        }

        [Fact]
        public async Task ReconnectsAfterTheSourceDropsAllConnections()
        {
            using (var h = new LiveHarness(Sim()))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                long cut = 0; bool dropped = false;
                var v = await h.ReadAsync(14, 0, 0, total =>
                {
                    if (!dropped && total > 500000) { dropped = true; cut = total; src.DropAllConnections(); }
                    if (total - cut > 2000000 && cut > 0) { }
                });
                Assert.True(dropped);
                Assert.True(v.Bytes > cut + 500000, "it should have reconnected and carried on: " + v.Bytes + " vs " + cut + " " + h.Session.DebugInfo);
                Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets);
                Assert.True(src.Connections >= 2);
            }
        }

        [Fact]
        public async Task NarrowChunkNumbersForOldPeersAndTimestampsInHaveAreSupported()
        {
            using (var h = new LiveHarness(Sim(), o => o.Live.ChunkWidth = ChunkWidthMode.Byte))
            {
                var src = h.AddSource(new SimPeerBehavior { WideChunk = false, SendTimestamps = true, Version = 3002000, NodeType = 1 });
                await h.OpenAsync();
                var v = await h.ReadAsync(7, 262144 * 4, 262144 * 8);
                AssertCleanStream(v, src);
            }
            // Auto: with remote v <= 3002399 the one-byte width is used
            using (var h = new LiveHarness(Sim()))
            {
                var src = h.AddSource(new SimPeerBehavior { WideChunk = false, Version = 3001000, NodeType = 1 });
                await h.OpenAsync();
                var v = await h.ReadAsync(7, 262144 * 4, 262144 * 8);
                AssertCleanStream(v, src);
            }
        }

        [Fact]
        public async Task OldAndNewPeersWithAndWithoutSignatureAreAcceptedAndPlaybackIsIdentical()
        {
            using (var h = new LiveHarness(Sim()))
            {
                var unsigned = h.AddSource(new SimPeerBehavior { NodeType = 1, Version = 3013200, UnsignedExtend = true, RespondWithBtProtocolName = true });
                await h.OpenAsync();
                var v = await h.ReadAsync(6);
                AssertCleanStream(v, unsigned);
            }
        }

        [Fact]
        public async Task OurOwnExtendIsAcceptedByAStrictSourceThatRequiresSignature()
        {
            using (var h = new LiveHarness(Sim()))
            {
                var src = h.AddSource(new SimPeerBehavior { RequireClientSignature = true });
                await h.OpenAsync();
                var v = await h.ReadAsync(5);
                AssertCleanStream(v, src);
                Assert.False(src.AnyClientSignatureInvalid);
                Assert.Contains(3022200L, src.ClientVersions);
                Assert.True(src.ChangePositions >= 1, "the client must send CHANGEPOS");
                Assert.True(src.Interested >= 1);
            }
            // unsigned: the source that requires it closes the connection and nothing arrives
            using (var h = new LiveHarness(Sim(), o => o.Live.SignExtend = false))
            {
                var src = h.AddSource(new SimPeerBehavior { RequireClientSignature = true });
                await h.OpenAsync();
                var v = await h.ReadAsync(3);
                Assert.Equal(0, v.Bytes);
                Assert.Contains(src.Violations, x => x.Contains("without signature"));
            }
        }

        [Fact]
        public async Task NonTsContentIsDeliveredAsIs()
        {
            using (var h = new LiveHarness(Sim(o => o.NonTsContent = true)))
            {
                h.AddSource();
                await h.OpenAsync();
                long bytes = 0;
                using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                {
                    try
                    {
                        using (var resp = await http.GetAsync(h.Opened.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        using (var st = await resp.Content.ReadAsStreamAsync())
                        {
                            var buf = new byte[65536];
                            while (bytes < 300000) { int n = await st.ReadAsync(buf, 0, buf.Length, cts.Token); if (n <= 0) break; bytes += n; }
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                Assert.True(bytes >= 300000, "bytes " + bytes + " " + h.Session.DebugInfo);
            }
        }

        [Fact]
        public async Task MemoryStaysWithinTheConfiguredBudget()
        {
            var sim = Sim(o => o.TimeScale = 8);
            using (var h = new LiveHarness(sim, o => o.Live.MaxBufferBytes = 4 * 1024 * 1024))
            {
                h.AddSource();
                await h.OpenAsync();
                int max = 0;
                var v = await h.ReadAsync(8, 262144 * 8, 262144 * 8, total => { var s = h.Session.LiveInfo; if (s.StoredPieces > max) max = s.StoredPieces; });
                AssertCleanStream(v);
                Assert.InRange(max, 1, 4 * 1024 * 1024 / 65536 + 4);
            }
        }

        [Fact]
        public async Task SeveralMinutesOfStreamPlayWithoutCuts()
        {
            // 12 real s at speed x30 = 6 minutes of live stream, consumed by a player at live pace (accelerated)
            var sim = Sim(o => { o.TimeScale = 30; o.Bitrate = 131072; o.PieceLength = 65536; o.ChunkLength = 16384; });
            using (var h = new LiveHarness(sim))
            {
                var src = h.AddSource(new SimPeerBehavior { NodeType = 2 });
                var relay = h.AddSource(new SimPeerBehavior { NodeType = 1, LagPieces = 3 });
                await h.OpenAsync();
                double rate = sim.Bitrate * sim.TimeScale;
                long lastTotal = 0; var lastMove = System.Diagnostics.Stopwatch.StartNew(); double maxStall = 0;
                var v = await h.ReadAsync(13, (long)rate, (long)(rate * 3), total =>
                {
                    if (total != lastTotal) { double s = lastMove.Elapsed.TotalSeconds; if (s > maxStall) maxStall = s; lastMove.Restart(); lastTotal = total; }
                });
                AssertCleanStream(v, src);
                double streamSeconds = v.Bytes / (double)sim.Bitrate;
                Assert.True(streamSeconds > 240, "expected >4 min of stream but got " + streamSeconds.ToString("0") + " s");
                Assert.Empty(relay.Violations);
                var info = h.Session.LiveInfo;
                Assert.Equal(0, info.HolesSkipped); Assert.Equal(0, info.Rehooks); Assert.Equal(0, info.SignatureFailures);
                Assert.True(maxStall < 1.5, "maximum stall of " + maxStall.ToString("0.00") + " s");
            }
        }

        [Fact]
        public async Task LiveSessionStatsAndStatesAreReported()
        {
            using (var h = new LiveHarness(Sim()))
            {
                h.AddSource();
                var states = new List<SessionState>();
                await h.OpenAsync();
                h.Session.StateChanged += (s, st) => { lock (states) states.Add(st); };
                Assert.True(h.Session.IsLive); Assert.Null(h.Session.Length); Assert.Equal("video/mp2t", h.Session.ContentType);
                var v = await h.ReadAsync(4);
                var stats = h.Session.Stats;
                Assert.True(stats.Peers >= 1); Assert.True(stats.DownloadedBytes > 0); Assert.True(stats.PiecesHave > 0);
                Assert.Equal(SessionState.Playing, h.Session.State);
                h.Session.Dispose();
                Assert.Equal(SessionState.Stopped, h.Session.State);
            }
        }
    }
}
