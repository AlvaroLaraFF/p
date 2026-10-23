using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AceEngine.Cast;
using AceEngine.Live;
using Xunit;

namespace AceEngine.Tests.Cast
{
    /// <summary>A generator, a live buffer and a segmenter wired together, fed the way the pump feeds them (chunks of any size).</summary>
    internal sealed class SegRig : IDisposable
    {
        public readonly TsGen Gen = new TsGen();
        public readonly TimeshiftBuffer Buf;
        public readonly HlsSegmenter Seg;
        public DateTime Now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        private readonly Random _rnd = new Random(7);

        public SegRig(Action<TsGen> cfg = null, Action<HlsOptions> opt = null, long budget = 24L << 20, int bitrate = 70000)
        {
            if (cfg != null) cfg(Gen);
            Buf = new TimeshiftBuffer(new TimeshiftOptions { BudgetBytes = budget, BlockBytes = 188 * 400, NominalBytesPerSecond = bitrate, LiveDelayMs = 10000 });
            var o = new HlsOptions();
            o.UtcNow = () => Now;
            if (opt != null) opt(o);
            Seg = new HlsSegmenter(Buf, o);
        }

        public void Append(byte[] data, bool process = true)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(data.Length - pos, 1 + _rnd.Next(30000));
                Buf.Append(data, pos, n);
                pos += n;
                if (process) Seg.Process();
            }
        }

        public void Feed(double seconds, bool process = true)
        {
            Append(Gen.Generate(seconds), process);
            Now = Now.AddSeconds(seconds);
        }

        public HlsPlaylistModel Playlist()
        {
            bool ended;
            string text = Seg.GetPlaylist(out ended);
            Assert.NotNull(text);
            return HlsPlaylistModel.Parse(text, !ended);
        }

        public byte[] Fetch(string uri)
        {
            long seq = long.Parse(uri.Substring(1, uri.Length - 4), CultureInfo.InvariantCulture);
            using (var lease = Seg.GetSegment(seq))
            {
                Assert.NotNull(lease);
                var r = new byte[lease.Length];
                Buffer.BlockCopy(lease.Buffer, 0, r, 0, lease.Length);
                return r;
            }
        }

        public void Dispose() { Seg.Dispose(); Buf.Dispose(); }
    }

    [Collection(CastIsolatedCollection.Name)]
    public class HlsSegmenterTests
    {
        private static List<TsReader.Pkt> Video(List<TsReader.Pkt> l, int pid = 0x100) { return l.Where(p => p.Pid == pid).ToList(); }

        [Fact]
        public void EverySegmentStartsWithPatPmtAndThenAKeyFrame()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(60);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 6);
                foreach (var s in pl.Segments)
                {
                    var pk = TsReader.Parse(rig.Fetch(s.Uri), 0, rig.Fetch(s.Uri).Length);
                    Assert.Equal(0, pk[0].Pid); Assert.True(pk[0].Pusi);
                    Assert.Equal(0x1000, pk[1].Pid); Assert.True(pk[1].Pusi);
                    Assert.Equal(0x100, pk[2].Pid);
                    Assert.True(pk[2].Pusi, "the first media packet opens a PES");
                    Assert.True(pk[2].Idr, "and that PES carries an IDR slice");
                    Assert.True(pk[2].Rai, "with the random access indicator the muxer set");
                    // the PAT and the PMT parse and describe the two streams
                    var parser = new TsProgramParser();
                    parser.FeedPacket(pk[0].Raw, 0); parser.FeedPacket(pk[1].Raw, 0);
                    Assert.NotNull(parser.Program);
                    Assert.Equal(0x100, parser.Program.Video.Pid);
                    Assert.Equal(EsKind.H264, parser.Program.Video.Kind);
                    Assert.Equal(EsKind.AacAdts, parser.Program.AudioStreams.Single().Kind);
                    Assert.Equal(0x100, parser.Program.PcrPid);
                    Assert.Equal(2, parser.Program.Streams.Count);                           // the teletext-like PID of the source is not carried
                }
            }
        }

        [Fact]
        public void SegmentsAreAboutFourSecondsAndDurationsMatchThePts()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(80);
                var pl = rig.Playlist();
                Assert.Equal(8, pl.Segments.Count);
                Assert.Equal(4, pl.TargetDuration);
                var firstPts = new List<long>();
                foreach (var s in pl.Segments)
                {
                    var data = rig.Fetch(s.Uri);
                    var pk = TsReader.Parse(data, 0, data.Length);
                    firstPts.Add(Video(pk).First(p => p.Pts >= 0).Pts);
                    Assert.InRange(s.Duration, 3.99, 4.01);
                }
                for (int i = 1; i < firstPts.Count; i++)
                {
                    double d = (firstPts[i] - firstPts[i - 1]) / 90000.0;
                    Assert.InRange(d, pl.Segments[i - 1].Duration - 0.002, pl.Segments[i - 1].Duration + 0.002);
                }
            }
        }

        [Fact]
        public void ConcatenatedSegmentsAreGaplessAndKeepTheContinuityCounters()
        {
            using (var rig = new SegRig(opt: null))
            {
                rig.Feed(70);
                var pl = rig.Playlist();
                var all = new List<TsReader.Pkt>();
                foreach (var s in pl.Segments)
                {
                    var data = rig.Fetch(s.Uri);
                    all.AddRange(TsReader.Parse(data, 0, data.Length));
                }
                foreach (int pid in new[] { 0, 0x1000, 0x100, 0x101 })
                {
                    var l = all.Where(p => p.Pid == pid && p.HasPayload).ToList();
                    Assert.NotEmpty(l);
                    for (int i = 1; i < l.Count; i++)
                        Assert.True(l[i].Cc == ((l[i - 1].Cc + 1) & 0xF), "continuity counter of pid 0x" + pid.ToString("x") + " breaks at packet " + i);
                }
                // every video frame of the covered range appears exactly once, in order, 40 ms apart
                var pts = all.Where(p => p.Pid == 0x100 && p.Pusi).Select(p => p.Pts).ToList();
                for (int i = 1; i < pts.Count; i++) Assert.Equal(3600, pts[i] - pts[i - 1]);
            }
        }

        [Fact]
        public void AudioPesNeverStraddlesASegmentBoundary()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(60);
                var pl = rig.Playlist();
                int checkedSegments = 0;
                foreach (var s in pl.Segments)
                {
                    var data = rig.Fetch(s.Uri);
                    var audio = TsReader.Parse(data, 0, data.Length).Where(p => p.Pid == 0x101).ToList();
                    Assert.NotEmpty(audio);
                    Assert.True(audio[0].Pusi, "the first audio packet of " + s.Uri + " starts a PES");
                    // reassemble: each PES has its declared length
                    var pes = new List<byte>();
                    int declared = -1, got = 0;
                    foreach (var p in audio)
                    {
                        if (p.Pusi)
                        {
                            if (declared >= 0) Assert.Equal(declared + 6, got);
                            declared = (p.Raw[p.PayloadStart + 4] << 8) | p.Raw[p.PayloadStart + 5];
                            got = 0;
                        }
                        got += 188 - p.PayloadStart;
                        // stuffing in the adaptation field is accounted for by PayloadStart
                    }
                    Assert.Equal(declared + 6, got);
                    checkedSegments++;
                }
                Assert.True(checkedSegments >= 6);
            }
        }

        [Fact]
        public void TheMediaSequenceAdvancesAndTheWindowIsBounded()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(20);
                var a = rig.Playlist();
                rig.Feed(40);
                var b = rig.Playlist();
                Assert.True(b.MediaSequence > a.MediaSequence);
                Assert.Equal(8, b.Segments.Count);
                Assert.Equal(b.MediaSequence + 7, long.Parse(b.Segments.Last().Uri.Substring(1, b.Segments.Last().Uri.Length - 4)));
                // the segments that left the playlist are still served for a while, older ones are not
                long firstListed = b.MediaSequence;
                using (var l = rig.Seg.GetSegment(firstListed - 1)) Assert.NotNull(l);
                using (var l = rig.Seg.GetSegment(firstListed - 4)) Assert.NotNull(l);
                using (var l = rig.Seg.GetSegment(firstListed - 5)) Assert.Null(l);
                using (var l = rig.Seg.GetSegment(firstListed + 100)) Assert.Null(l);
                using (var l = rig.Seg.GetSegment(-1)) Assert.Null(l);
            }
        }

        [Fact]
        public void TargetDurationNeverChangesWhileSegmentsKeepTheirLength()
        {
            using (var rig = new SegRig())
            {
                var targets = new HashSet<int>();
                for (int i = 0; i < 20; i++) { rig.Feed(5); targets.Add(rig.Playlist().TargetDuration); }
                Assert.Single(targets);
            }
        }

        [Fact]
        public void ShortGopsAreGroupedUpToTheTarget()
        {
            using (var rig = new SegRig(g => { g.GopFrames = 25; }))                         // 1 s GOP
            {
                rig.Feed(60);
                foreach (var s in rig.Playlist().Segments) Assert.InRange(s.Duration, 3.99, 4.01);
            }
        }

        [Fact]
        public void LongGopsGiveOneSegmentPerGop()
        {
            using (var rig = new SegRig(g => { g.GopFrames = 150; }))                        // 6 s GOP
            {
                rig.Feed(80);
                var pl = rig.Playlist();
                foreach (var s in pl.Segments) Assert.InRange(s.Duration, 5.99, 6.01);
                Assert.Equal(6, pl.TargetDuration);
            }
        }

        [Fact]
        public void AGopThatDoesNotDivideTheTargetPicksTheClosestCut()
        {
            using (var rig = new SegRig(g => { g.GopFrames = 75; }))                         // 3 s GOP: 3 s is closer to 4 than 6 s
            {
                rig.Feed(60);
                foreach (var s in rig.Playlist().Segments) Assert.InRange(s.Duration, 2.99, 3.01);
            }
        }

        [Fact]
        public void ParameterSetsRepeatedInFrontOfPFramesAreNotKeyFrames()
        {
            using (var rig = new SegRig(g => { g.RepeatSpsOnEveryFrame = true; }))
            {
                rig.Feed(60);
                var pl = rig.Playlist();
                foreach (var s in pl.Segments)
                {
                    Assert.InRange(s.Duration, 3.99, 4.01);
                    var data = rig.Fetch(s.Uri);
                    Assert.True(TsReader.Parse(data, 0, data.Length)[2].Idr);
                }
            }
        }

        [Fact]
        public void IntraSlicesWithoutIdrButWithParameterSetsAreAcceptedAsKeyFrames()
        {
            using (var rig = new SegRig(g => { g.NonIdrKey = true; }))
            {
                rig.Feed(60);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 6);
                foreach (var s in pl.Segments) Assert.InRange(s.Duration, 3.99, 4.01);
                Assert.False(rig.Seg.Failed);
            }
        }

        [Fact]
        public void AKeyFrameWhoseSliceStartsInALaterPacketIsStillFound()
        {
            using (var rig = new SegRig(g => { g.SpsLate = true; }))
            {
                rig.Feed(40);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 4);
                foreach (var s in pl.Segments) Assert.InRange(s.Duration, 3.99, 4.01);
            }
        }

        [Fact]
        public void StreamsWithoutPtsFallBackToThePcrClock()
        {
            using (var rig = new SegRig(g => { g.OmitPts = true; }))
            {
                rig.Feed(60);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 6);
                foreach (var s in pl.Segments) Assert.InRange(s.Duration, 3.9, 4.1);
            }
        }

        [Fact]
        public void VideoOnlyStreamsAreSegmentedToo()
        {
            using (var rig = new SegRig(g => { g.Audio = TestAudio.None; }))
            {
                rig.Feed(40);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 4);
                var data = rig.Fetch(pl.Segments[1].Uri);
                var pk = TsReader.Parse(data, 0, data.Length);
                Assert.DoesNotContain(pk, p => p.Pid == 0x101);
                var parser = new TsProgramParser(); parser.FeedPacket(pk[1].Raw, 0); parser.FeedPacket(pk[0].Raw, 0); parser.FeedPacket(pk[1].Raw, 0);
                Assert.Empty(parser.Program.AudioStreams);
            }
        }

        [Fact]
        public void ATimelineBreakWithoutNoticeFromTheEngineIsMarkedAsADiscontinuity()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(30);
                rig.Gen.Break();                                                             // a sender restart: PTS, PCR and counters jump
                rig.Feed(30);
                var pl = rig.Playlist();
                Assert.Equal(1, pl.Segments.Count(s => s.Discontinuity));
                int idx = pl.Segments.FindIndex(s => s.Discontinuity);
                Assert.True(idx > 0);
                // the segment before the break lasts what it contains, not the (meaningless) PTS difference to the next timeline
                Assert.InRange(pl.Segments[idx - 1].Duration, 0.5, 4.5);
                // the segment after the break starts at a key frame of the new timeline
                var data = rig.Fetch(pl.Segments[idx].Uri);
                var pk = TsReader.Parse(data, 0, data.Length);
                Assert.True(pk[2].Idr);
                Assert.Equal(0, pl.DiscontinuitySequence);
            }
        }

        [Fact]
        public void ACutOfTheInputEndsTheSegmentAtTheHoleAndTheNextOneStartsAtAKeyFrame()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(21);                                                                 // ends in the middle of a GOP (21 s = 10.5 GOPs)
                rig.Buf.Discontinuity();                                                     // the engine skipped a hole
                rig.Gen.GenerateFrames(30);                                                  // 1.2 s that never reach the buffer
                rig.Feed(30);
                var pl = rig.Playlist();
                Assert.Equal(1, pl.Segments.Count(s => s.Discontinuity));
                int idx = pl.Segments.FindIndex(s => s.Discontinuity);
                foreach (var s in pl.Segments)
                {
                    var data = rig.Fetch(s.Uri);
                    var pk = TsReader.Parse(data, 0, data.Length);
                    Assert.True(pk[2].Idr, s.Uri + " must open with a key frame");
                }
                Assert.InRange(pl.Segments[idx - 1].Duration, 0.5, 4.5);
            }
        }

        [Fact]
        public void TheCutAtTheVeryStartOfTheStreamDoesNotTagTheFirstSegment()
        {
            using (var rig = new SegRig())
            {
                rig.Buf.Discontinuity();                                                     // the pump reports the start of the stream as a cut
                rig.Feed(30);
                var pl = rig.Playlist();
                Assert.DoesNotContain(pl.Segments, s => s.Discontinuity);
                Assert.Equal(0, pl.DiscontinuitySequence);
                Assert.Equal(0, rig.Seg.GetStats().Discontinuities);
            }
        }

        [Fact]
        public void TheDiscontinuitySequenceCountsTheTagsThatLeftThePlaylist()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(20);
                rig.Gen.Break();
                rig.Feed(20);
                Assert.Equal(0, rig.Playlist().DiscontinuitySequence);
                Assert.Equal(1, rig.Playlist().Segments.Count(s => s.Discontinuity));
                rig.Feed(60);                                                                // the tagged segment slides out of the 8-segment window
                var pl = rig.Playlist();
                Assert.Equal(0, pl.Segments.Count(s => s.Discontinuity));
                Assert.Equal(1, pl.DiscontinuitySequence);
            }
        }

        [Fact]
        public void AProgramChangeStartsANewTimeline()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(20);
                rig.Gen.Audio = TestAudio.None;                                              // the audio stream disappears from the PMT
                rig.Feed(20);
                var pl = rig.Playlist();
                Assert.Equal(1, pl.Segments.Count(s => s.Discontinuity));
                int idx = pl.Segments.FindIndex(s => s.Discontinuity);
                var before = rig.Fetch(pl.Segments[idx - 1].Uri);
                var after = rig.Fetch(pl.Segments[idx].Uri);
                Assert.Contains(TsReader.Parse(before, 0, before.Length), p => p.Pid == 0x101);
                Assert.DoesNotContain(TsReader.Parse(after, 0, after.Length), p => p.Pid == 0x101);
            }
        }

        [Fact]
        public void ProgramDatesFormAChainOneDurationApart()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(60);
                var pl = rig.Playlist();                                                     // the validator checks the chain
                Assert.All(pl.Segments, s => Assert.True(s.Pdt.HasValue));
                var start = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
                Assert.InRange((pl.Segments[0].Pdt.Value - start).TotalSeconds, -30, 90);
            }
        }

        [Fact]
        public void ProgramDatesCanBeTurnedOff()
        {
            using (var rig = new SegRig(opt: o => o.ProgramDateTime = false))
            {
                rig.Feed(40);
                Assert.All(rig.Playlist().Segments, s => Assert.False(s.Pdt.HasValue));
            }
        }

        [Fact]
        public void WhenTheInputEndsTheLastSegmentIsPublishedAndTheListingEnds()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(21);
                bool ended;
                string before = rig.Seg.GetPlaylist(out ended);
                Assert.False(ended);
                rig.Buf.Complete();
                rig.Seg.Process();
                string text = rig.Seg.GetPlaylist(out ended);
                Assert.True(ended);
                Assert.Contains("#EXT-X-ENDLIST", text);
                var pl = HlsPlaylistModel.Parse(text, false);
                Assert.True(pl.Segments.Count > HlsPlaylistModel.Parse(before).Segments.Count);
                Assert.True(rig.Seg.Ended);
            }
        }

        [Fact]
        public void IncrementalAndOneShotProcessingGiveTheSameSegments()
        {
            byte[] data;
            var g = new TsGen();
            data = g.Generate(50);
            string a, b;
            using (var rig = new SegRig(opt: o => o.StartLookbackSeconds = 1000)) { rig.Append(data, true); bool e; a = Strip(rig.Seg.GetPlaylist(out e)); }
            using (var rig = new SegRig(opt: o => o.StartLookbackSeconds = 1000)) { rig.Append(data, false); rig.Seg.Process(); bool e; b = Strip(rig.Seg.GetPlaylist(out e)); }
            Assert.Equal(a, b);
        }

        private static string Strip(string playlist)
        {
            return string.Join("\n", playlist.Split('\n').Where(l => !l.StartsWith("#EXT-X-PROGRAM-DATE-TIME")));
        }

        [Fact]
        public void WhenTheBufferAlreadyHoldsMuchOnlyTheRecentPartIsSegmented()
        {
            using (var rig = new SegRig(budget: 64L << 20, opt: o => { o.StartLookbackSeconds = 20; o.WindowSegments = 20; }))
            {
                rig.Feed(120, false);
                rig.Seg.Process();
                var pl = rig.Playlist();
                double total = pl.Segments.Sum(s => s.Duration);
                Assert.InRange(total, 14, 22);
            }
        }

        [Fact]
        public void SegmentsInTheRecycledPartOfTheBufferAreNotListedNorServed()
        {
            // a small buffer (1 MiB ~ 15 s of this stream): the older segments are recycled quickly
            using (var rig = new SegRig(budget: 1L << 20))
            {
                for (int i = 0; i < 20; i++) rig.Feed(5);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 1);
                foreach (var s in pl.Segments) using (var l = rig.Seg.GetSegment(long.Parse(s.Uri.Substring(1, s.Uri.Length - 4)))) Assert.NotNull(l);
                using (var old = rig.Seg.GetSegment(0)) Assert.Null(old);
                Assert.True(rig.Seg.GetStats().SegmentsPublished > pl.Segments.Count);
            }
        }

        [Fact]
        public void ANonH264ChannelFailsInsteadOfProducingSegments()
        {
            using (var rig = new SegRig(g => { g.Video = TestVideo.Mpeg2; }))
            {
                rig.Feed(20);
                Assert.True(rig.Seg.Failed);
                Assert.Equal(CastIssueCode.VideoMpeg2, rig.Seg.FailureCode);
                bool e;
                Assert.Null(rig.Seg.GetPlaylist(out e));
            }
        }

        [Fact]
        public void ANoKeyFrameStreamIsReportedAsNotSegmentable()
        {
            using (var rig = new SegRig(g => { g.GopFrames = 100000; g.NonIdrKey = true; g.RepeatSpsOnEveryFrame = false; }))
            {
                // the first frame is a key (frame 0); after it there is none for a very long time
                rig.Feed(60);
                Assert.True(rig.Seg.Failed);
                Assert.Equal(CastIssueCode.NoKeyframes, rig.Seg.FailureCode);
            }
        }

        [Fact]
        public void ComposingTheSameSegmentTwiceGivesTheSameBytes()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(30);
                var s = rig.Playlist().Segments[2];
                var a = rig.Fetch(s.Uri);
                rig.Feed(5);
                var b = rig.Fetch(s.Uri);
                Assert.Equal(a, b);
            }
        }

        [Fact]
        public void SegmentsAreWholePacketsOnly()
        {
            using (var rig = new SegRig())
            {
                rig.Feed(40);
                foreach (var s in rig.Playlist().Segments)
                {
                    var data = rig.Fetch(s.Uri);
                    Assert.Equal(0, data.Length % 188);
                    for (int i = 0; i < data.Length; i += 188) Assert.Equal(0x47, data[i]);
                }
            }
        }

        [Fact]
        public void PlaylistBeforeAnySegmentIsNullAndTheSegmenterReportsItsWindow()
        {
            using (var rig = new SegRig())
            {
                bool e;
                Assert.Null(rig.Seg.GetPlaylist(out e));
                rig.Feed(3);
                Assert.Null(rig.Seg.GetPlaylist(out e));                                     // still inside the first segment
                rig.Feed(30);
                int n; double secs;
                rig.Seg.GetWindow(out n, out secs);
                Assert.InRange(n, 5, 8);
                Assert.InRange(secs, 19, 33);
                var st = rig.Seg.GetStats();
                Assert.Equal(n, st.SegmentsListed);
                Assert.True(st.PlaylistBehindEdgeSeconds < 8);
            }
        }

        [Fact]
        public void AFuzzedStreamNeverBreaksTheSegmenterAndEverySegmentStaysWellFormed()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                var rnd = new Random(seed);
                using (var rig = new SegRig())
                {
                    var data = rig.Gen.Generate(50);
                    // flip bytes, drop and duplicate packets
                    var list = new List<byte>(data);
                    int mutations = 20 + rnd.Next(60);
                    for (int m = 0; m < mutations; m++)
                    {
                        int pos = rnd.Next(list.Count - 188);
                        pos -= pos % 188;
                        switch (rnd.Next(4))
                        {
                            case 0: list[pos + 1 + rnd.Next(3)] ^= (byte)(1 << rnd.Next(8)); break;
                            case 1: list.RemoveRange(pos, 188); break;
                            case 2: list.InsertRange(pos, list.GetRange(pos, 188)); break;
                            case 3: for (int k = 4; k < 188; k++) list[pos + k] = (byte)rnd.Next(256); break;
                        }
                    }
                    rig.Append(list.ToArray());
                    bool e;
                    var text = rig.Seg.GetPlaylist(out e);
                    if (text == null) continue;
                    var pl = HlsPlaylistModel.Parse(text);
                    foreach (var s in pl.Segments)
                    {
                        using (var l = rig.Seg.GetSegment(long.Parse(s.Uri.Substring(1, s.Uri.Length - 4))))
                        {
                            if (l == null) continue;
                            Assert.Equal(0, l.Length % 188);
                            for (int i = 0; i < l.Length; i += 188) Assert.Equal(0x47, l.Buffer[i]);
                        }
                    }
                }
            }
        }
    }
}
