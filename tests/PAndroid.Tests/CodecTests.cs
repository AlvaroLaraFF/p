using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>The decoder report, the choice of the software MP2 path and what the viewer is told about a channel the device cannot play in full.</summary>
    public class CodecTests
    {
        private static CodecEntry Dec(string name, bool hw, params string[] types)
        {
            return new CodecEntry { Name = name, IsHardware = hw, IsSoftwareOnly = !hw, FlagsKnown = true, Types = types };
        }

        private static CodecEntry Enc(string name, params string[] types) { return new CodecEntry { Name = name, IsEncoder = true, Types = types }; }

        /// <summary>What a Fire TV stick plausibly lists (made up for the test: the real list is what the log of a device says).</summary>
        private static List<CodecEntry> Stick(bool withMp2)
        {
            var l = new List<CodecEntry>
            {
                Dec("OMX.MTK.VIDEO.DECODER.AVC", true, "video/avc"),
                Dec("OMX.MTK.VIDEO.DECODER.HEVC", true, "video/hevc"),
                Dec("OMX.MTK.VIDEO.DECODER.MPEG2", true, "video/mpeg2"),
                Dec("OMX.google.h264.decoder", false, "video/avc"),
                Dec("OMX.google.aac.decoder", false, "audio/mp4a-latm"),
                Dec("OMX.google.mp3.decoder", false, "audio/mpeg"),
                Dec("OMX.google.raw.decoder", false, "audio/raw"),
                Dec("OMX.amazon.ac3.decoder", false, "audio/ac3", "audio/eac3"),
                Enc("OMX.MTK.VIDEO.ENCODER.AVC", "video/avc"),
            };
            if (withMp2) l.Add(Dec("OMX.MTK.AUDIO.DECODER.MP2", true, "audio/mpeg-L2"));
            return l;
        }

        // ------------------------------------------------------------------ the report

        [Fact]
        public void AnEncoderIsNotADecoderAndTheMimeTypeIsComparedWithoutCase()
        {
            var l = new List<CodecEntry> { Enc("enc", "audio/mpeg-L2"), Dec("dec", true, "audio/MPEG-l2") };
            Assert.True(CodecReport.HasDecoder(l, "audio/mpeg-L2"));
            Assert.False(CodecReport.HasDecoder(new[] { Enc("enc", "audio/mpeg-L2") }, "audio/mpeg-L2"));
            Assert.False(CodecReport.HasDecoder(null, "audio/mpeg-L2"));
            Assert.Single(CodecReport.DecodersOf(l, "audio/mpeg-l2"));
        }

        [Fact]
        public void TheReportSaysNoneWhereThereIsNoDecoderAndNamesTheOnesThatExist()
        {
            var lines = CodecReport.Lines(Stick(false));
            Assert.Contains(lines, s => s.StartsWith("MediaCodecList: 8 decoders") && s.Contains("1 encoders"));
            Assert.Contains("decoder audio/mpeg-L2 [MP2]: NONE", lines);
            var avc = lines.Single(s => s.StartsWith("decoder video/avc "));
            Assert.Contains("OMX.MTK.VIDEO.DECODER.AVC (hw)", avc); Assert.Contains("OMX.google.h264.decoder (sw)", avc);
            Assert.Contains(lines, s => s.StartsWith("decoder audio/ac3 [AC-3]: OMX.amazon.ac3.decoder"));
            Assert.Contains(lines, s => s.StartsWith("all decoders: ") && s.Contains("OMX.google.raw.decoder[audio/raw]"));
        }

        [Fact]
        public void TheReportShowsTheMp2DecoderWhenThereIsOne()
        {
            var lines = CodecReport.Lines(Stick(true));
            Assert.Contains(lines, s => s.StartsWith("decoder audio/mpeg-L2 [MP2]: OMX.MTK.AUDIO.DECODER.MP2 (hw)"));
        }

        [Fact]
        public void TheReportShowsTheSizeAndTheNumberOfInstancesWhenTheSystemSaysThem()
        {
            var e = Dec("OMX.MTK.VIDEO.DECODER.AVC", true, "video/avc"); e.MaxSize = "3840x2160"; e.MaxInstances = 4;
            var one = Dec("OMX.x.hevc", true, "video/hevc"); one.MaxInstances = 1;
            var lines = CodecReport.Lines(new[] { e, one });
            Assert.Contains(lines, s => s.Contains("OMX.MTK.VIDEO.DECODER.AVC (hw, up to 3840x2160, 4 instances)"));
            Assert.Contains(lines, s => s.Contains("OMX.x.hevc (hw, 1 instance)"));
        }

        [Fact]
        public void BeforeApi29TheKindIsGuessedFromTheName()
        {
            Assert.Equal("sw", CodecReport.Kind(new CodecEntry { Name = "OMX.google.aac.decoder" }));
            Assert.Equal("sw", CodecReport.Kind(new CodecEntry { Name = "c2.android.avc.decoder" }));
            Assert.Equal("hw?", CodecReport.Kind(new CodecEntry { Name = "OMX.MTK.VIDEO.DECODER.AVC" }));
            Assert.Equal("hw", CodecReport.Kind(Dec("x", true)));
            Assert.Equal("?", CodecReport.Kind(new CodecEntry { Name = "x", FlagsKnown = true }));
        }

        [Fact]
        public void TheVerdictLineSaysWhichChannelFormatsTheDeviceCanDecode()
        {
            string no = CodecReport.Verdict(Stick(false));
            Assert.Contains("H.264 yes", no); Assert.Contains("H.265 yes", no); Assert.Contains("MPEG-2 video yes", no); Assert.Contains("AAC yes", no);
            Assert.Contains("MP2 NO", no); Assert.Contains("software MP2 will be used", no); Assert.Contains("AC-3 yes", no);
            string yes = CodecReport.Verdict(Stick(true));
            Assert.Contains("MP2 yes", yes); Assert.Contains("software MP2 only if forced", yes);
            Assert.Contains("AC-3 NO", CodecReport.Verdict(new List<CodecEntry>()));
        }

        // ------------------------------------------------------------------ the choice of the software path

        [Theory]
        // mode, device has an MP2 decoder, it failed on this run, software MP2 wanted
        [InlineData(SoftwareAudioMode.Auto, false, false, true)]      // a stick without MP2: the software decoder is the only way to hear the channel
        [InlineData(SoftwareAudioMode.Auto, true, false, false)]      // the device's own decoder does it
        [InlineData(SoftwareAudioMode.Auto, true, true, true)]        // the device lists one but it failed: take over
        [InlineData(SoftwareAudioMode.Off, false, false, false)]      // never: the channel is silent and says so
        [InlineData(SoftwareAudioMode.Off, true, true, false)]
        [InlineData(SoftwareAudioMode.Force, true, false, true)]      // to test the software path on a device that has a decoder
        [InlineData(SoftwareAudioMode.Force, false, false, true)]
        public void TheSoftwareMp2PathIsUsedOnlyWhenTheDeviceCannotOrIsToldTo(SoftwareAudioMode mode, bool has, bool failed, bool expected)
        {
            Assert.Equal(expected, AudioFallbackPolicy.UseSoftwareMp2(mode, has, failed));
        }

        // ------------------------------------------------------------------ what the viewer is told

        [Fact]
        public void AChannelWhoseAudioHasNoDecoderIsSaidToHaveNoSound()
        {
            var n = StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Video, "video/avc", true), new TrackFacts(TrackFacts.Audio, "audio/mpeg-L2", false) });
            Assert.Single(n);
            Assert.Contains("Sin sonido", n[0]); Assert.Contains("MP2", n[0]);
        }

        [Fact]
        public void AChannelWhoseVideoHasNoDecoderIsSaidToHaveNoPicture()
        {
            var n = StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Video, "video/hevc", false), new TrackFacts(TrackFacts.Audio, "audio/mp4a-latm", true) });
            Assert.Single(n);
            Assert.Contains("Sin imagen", n[0]); Assert.Contains("H.265", n[0]);
        }

        [Fact]
        public void BothAreSaidWhenBothAreMissing()
        {
            var n = StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Video, "video/hevc", false), new TrackFacts(TrackFacts.Audio, "audio/ac3", false), new TrackFacts(TrackFacts.Audio, "audio/mpeg-L2", false) });
            Assert.Equal(2, n.Count);
            Assert.Contains("AC-3, MP2", n.Single(s => s.StartsWith("Sin sonido")));
        }

        [Fact]
        public void NothingIsSaidWhenWhatTheStreamCarriesCanBePlayed()
        {
            Assert.Empty(StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Video, "video/avc", true), new TrackFacts(TrackFacts.Audio, "audio/mp4a-latm", true) }));
            // two audio tracks and one of them plays: the player takes that one
            Assert.Empty(StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Audio, "audio/mpeg-L2", false), new TrackFacts(TrackFacts.Audio, "audio/mp4a-latm", true) }));
            // a stream with no audio at all (a silent channel) is not a fault; neither is a stream with no tracks yet, or text and metadata tracks
            Assert.Empty(StreamNotices.Analyze(new[] { new TrackFacts(TrackFacts.Video, "video/avc", true) }));
            Assert.Empty(StreamNotices.Analyze(new TrackFacts[0]));
            Assert.Empty(StreamNotices.Analyze(new[] { new TrackFacts(3, "application/id3", false) }));
            Assert.Empty(StreamNotices.Analyze(null));
        }

        [Fact]
        public void TheLogLineNamesEveryTrackAndFlagsTheOnesThatCannotPlay()
        {
            string s = StreamNotices.Describe(new[] { new TrackFacts(TrackFacts.Video, "video/avc", true), new TrackFacts(TrackFacts.Audio, "audio/mpeg-L2", false) });
            Assert.Equal("video H.264, audio MP2 (NOT supported)", s);
        }
    }
}
