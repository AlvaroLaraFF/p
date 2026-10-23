using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.Cast;
using Xunit;

namespace AceEngine.Tests.Cast
{
    public class CastCompatibilityTests
    {
        private static CastVerdict Check(Action<TsGen> cfg, double seconds = 3, CastProfile profile = null)
        {
            var g = new TsGen();
            if (cfg != null) cfg(g);
            var data = g.Generate(seconds);
            return CastCompatibility.Analyze(data, 0, data.Length, profile ?? CastProfile.Chromecast2);
        }

        // ------------------------------------------------------------ the verdicts

        [Fact]
        public void H264UpTo1080pWithAdtsAacIsCompatible()
        {
            var v = Check(null);
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Empty(v.Blocking);
            Assert.Equal("", v.Reason);
            Assert.Equal("H.264 High@4.0 1920x1080p 25 fps", v.VideoDescription);
            Assert.Equal("AAC-LC 48000 Hz 2 ch (ADTS)", v.AudioDescription);
            Assert.Equal("h264+aac-adts", v.Summary);
        }

        [Theory]
        [InlineData(1280, 720)]
        [InlineData(720, 576)]
        [InlineData(1920, 1080)]
        [InlineData(1440, 1080)]
        public void CommonResolutionsAreCompatible(int w, int h)
        {
            var v = Check(g => { g.Width = w; g.Height = h; });
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Contains(w + "x" + h, v.VideoDescription);
        }

        [Fact]
        public void Mp2AudioIsNotCompatibleAndSaysWhy()
        {
            var v = Check(g => { g.Audio = TestAudio.Mp2; });
            Assert.True(v.IsIncompatible);
            Assert.Equal("audio MP2", v.Reason);
            Assert.Equal(CastIssueCode.AudioMp2, v.Blocking.Single().Code);
            Assert.Equal("h264+mpeg-audio", v.Summary);
        }

        [Fact]
        public void Mp3AudioIsCompatible()
        {
            var v = Check(g => { g.Audio = TestAudio.Mp3; });
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Equal("MP3 44100 Hz", v.AudioDescription);
        }

        [Fact]
        public void Ac3AndEac3AreNotCompatible()
        {
            var a = Check(g => { g.Audio = TestAudio.Ac3; });
            Assert.True(a.IsIncompatible);
            Assert.Equal("audio AC-3", a.Reason);
            var e = Check(g => { g.Audio = TestAudio.Eac3Private; });
            Assert.True(e.IsIncompatible);
            Assert.Equal("audio E-AC-3", e.Reason);
        }

        [Fact]
        public void Ac3CanBeAllowedByTheProfile()
        {
            var p = CastProfile.Chromecast2; p.AllowAc3 = true;
            Assert.True(Check(g => { g.Audio = TestAudio.Ac3; }, 3, p).IsCompatible);
        }

        [Fact]
        public void AacInLatmIsReportedAsSuchAndNotCompatible()
        {
            var v = Check(g => { g.Audio = TestAudio.Latm; });
            Assert.True(v.IsIncompatible);
            Assert.Equal("audio AAC LATM", v.Reason);
            Assert.Equal(CastIssueCode.AudioLatm, v.Blocking.Single().Code);
            Assert.Contains("LATM", v.AudioDescription);
            Assert.Contains("ADTS", v.Blocking.Single().Detail);
        }

        [Fact]
        public void Mpeg2VideoIsNotCompatible()
        {
            var v = Check(g => { g.Video = TestVideo.Mpeg2; });
            Assert.True(v.IsIncompatible);
            Assert.Equal("vídeo MPEG-2", v.Reason);
        }

        [Fact]
        public void HevcIsNotCompatibleEvenWithAac()
        {
            var v = Check(g => { g.Video = TestVideo.Hevc; });
            Assert.True(v.IsIncompatible);
            Assert.Equal("vídeo H.265", v.Reason);
            Assert.Contains("MPEG-TS", v.Blocking.Single().Detail);
        }

        [Fact]
        public void SeveralProblemsAreAllReported()
        {
            var v = Check(g => { g.Video = TestVideo.Mpeg2; g.Audio = TestAudio.Mp2; });
            Assert.True(v.IsIncompatible);
            Assert.Equal("vídeo MPEG-2 y audio MP2", v.Reason);
            Assert.Equal("mpeg2+mpeg-audio", v.Summary);
        }

        [Fact]
        public void PicturesBiggerThan1080pAreRejectedByTheSecondGeneration()
        {
            var v = Check(g => { g.Width = 3840; g.Height = 2160; g.Level = 51; });
            Assert.True(v.IsIncompatible);
            Assert.Contains("3840x2160", v.Reason);
        }

        [Fact]
        public void TheGoogleTvProfileTakes4kH264ButNotMpeg2()
        {
            Assert.True(Check(g => { g.Width = 3840; g.Height = 2160; g.Level = 51; g.Fps = 25; }, 3, CastProfile.GoogleTv).IsCompatible);
            Assert.True(Check(g => { g.Video = TestVideo.Mpeg2; }, 3, CastProfile.GoogleTv).IsIncompatible);
        }

        [Fact]
        public void Progressive1080p50NeedsAThirdGenerationOrNewer()
        {
            Action<TsGen> cfg = g => { g.Fps = 50; g.Level = 42; };
            var v2 = Check(cfg);
            Assert.True(v2.IsIncompatible, v2.ToString());
            Assert.Contains("50 fps", v2.Reason);
            Assert.True(Check(cfg, 3, CastProfile.Chromecast3).IsCompatible);
        }

        [Fact]
        public void Interlaced1080iAt25FramesIsAllowedWithAWarning()
        {
            var v = Check(g => { g.Interlaced = true; });
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Equal(CastIssueCode.H264Interlaced, v.Warnings.Single().Code);
            Assert.Contains("1080i", v.VideoDescription.Replace("1920x1080i", "1080i"));
        }

        [Fact]
        public void ProfilesTheDecoderCannotHandleAreRejected()
        {
            var high10 = Check(g => { g.Profile = 110; });
            Assert.True(high10.IsIncompatible);
            Assert.Contains(CastIssueCode.H264Profile, high10.Blocking.Select(i => i.Code));
            var main = Check(g => { g.Profile = 77; });
            Assert.True(main.IsCompatible, main.ToString());
            var baseline = Check(g => { g.Profile = 66; });
            Assert.True(baseline.IsCompatible, baseline.ToString());
        }

        [Fact]
        public void ALevelAboveTheDocumentedOneWithoutFrameRateIsOnlyAWarning()
        {
            var v = Check(g => { g.Level = 50; g.Width = 1280; g.Height = 720; });
            Assert.True(v.IsCompatible, v.ToString());                                       // the VUI says 25 fps at 720p: well within level 4.1
        }

        [Fact]
        public void AChannelWithoutAudioIsCompatibleWithAWarning()
        {
            var v = Check(g => { g.Audio = TestAudio.None; });
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Equal(CastIssueCode.NoAudio, v.Warnings.Single().Code);
            Assert.Equal("h264+noaudio", v.Summary);
        }

        [Fact]
        public void TheAdtsAudioIsPreferredWhenSeveralTracksExist()
        {
            // a PMT with MP2 first and AAC second: the HLS output would carry the AAC
            var info = new StreamInfo { Program = new ProgramInfo { PcrPid = 0x100 } };
            info.Program.Streams.Add(new EsInfo { Pid = 0x100, StreamType = 0x1B, Kind = EsKind.H264 });
            info.Program.Streams.Add(new EsInfo { Pid = 0x101, StreamType = 0x03, Kind = EsKind.MpegAudio });
            info.Program.Streams.Add(new EsInfo { Pid = 0x102, StreamType = 0x0F, Kind = EsKind.AacAdts });
            Assert.Equal(0x102, CastCompatibility.ChooseAudio(info.Program).Pid);
        }

        [Fact]
        public void AStreamWithoutAProgramIsUnknownUntilItIsFinal()
        {
            var probe = new TsStreamProbe();
            var v = CastCompatibility.Evaluate(probe.Snapshot(), null, false);
            Assert.Equal(CastSupport.Unknown, v.Support);
            var f = CastCompatibility.Evaluate(probe.Snapshot(), null, true);
            Assert.Equal(CastSupport.Incompatible, f.Support);
            Assert.Equal(CastIssueCode.NoProgram, f.Blocking.Single().Code);
        }

        [Fact]
        public void ScrambledStreamsAreRejected()
        {
            var g = new TsGen();
            var data = g.Generate(2);
            for (int i = 0; i + 188 <= data.Length; i += 188)
                if ((((data[i + 1] & 0x1F) << 8) | data[i + 2]) == 0x100) data[i + 3] |= 0x80;
            var v = CastCompatibility.Analyze(data, 0, data.Length, null);
            Assert.True(v.IsIncompatible);
            Assert.Contains(CastIssueCode.Scrambled, v.Blocking.Select(i => i.Code));
        }

        [Fact]
        public void AStreamWithOnlyAudioIsRejected()
        {
            var info = new StreamInfo { Program = new ProgramInfo() };
            info.Program.Streams.Add(new EsInfo { Pid = 0x101, StreamType = 0x0F, Kind = EsKind.AacAdts });
            var v = CastCompatibility.Evaluate(info, null, true);
            Assert.True(v.IsIncompatible);
            Assert.Equal(CastIssueCode.NoVideo, v.Blocking.First().Code);
        }

        [Fact]
        public void ADtsPayloadInsideALatmDeclarationIsRecognised()
        {
            // stream_type 0x11 but ADTS frames: it works as ADTS
            var g = new TsGen { Audio = TestAudio.Adts };
            var data = g.Generate(3);
            PatchAudioType(data, 0x11);
            var v = CastCompatibility.Analyze(data, 0, data.Length, null);
            Assert.True(v.IsCompatible, v.ToString());
            Assert.Contains("declared as LATM", v.AudioDescription);
        }

        [Fact]
        public void LatmPayloadInsideAnAdtsDeclarationIsFlagged()
        {
            var g = new TsGen { Audio = TestAudio.Latm };
            var data = g.Generate(3);
            PatchAudioType(data, 0x0F);
            var v = CastCompatibility.Analyze(data, 0, data.Length, null);
            Assert.True(v.IsIncompatible, v.ToString());
            Assert.Equal(CastIssueCode.AudioMismatch, v.Blocking.Single().Code);
        }

        private static void PatchAudioType(byte[] data, byte type)
        {
            // rewrite the stream_type of the audio ES in every PMT, fixing the CRC
            for (int i = 0; i + 188 <= data.Length; i += 188)
            {
                if ((((data[i + 1] & 0x1F) << 8) | data[i + 2]) != 0x1000) continue;
                int len = 3 + (((data[i + 6] & 0xF) << 8) | data[i + 7]);
                for (int k = i + 5 + 12; k + 5 <= i + 5 + len - 4; )
                {
                    int esLen = ((data[k + 3] & 0xF) << 8) | data[k + 4];
                    if ((((data[k + 1] & 0x1F) << 8) | data[k + 2]) == 0x101) data[k] = type;
                    k += 5 + esLen;
                }
                uint crc = TsCrcForTests.Compute(data, i + 5, len - 4);
                data[i + 5 + len - 4] = (byte)(crc >> 24); data[i + 5 + len - 3] = (byte)(crc >> 16); data[i + 5 + len - 2] = (byte)(crc >> 8); data[i + 5 + len - 1] = (byte)crc;
            }
        }

        // ------------------------------------------------------------ speed and robustness of the analysis

        [Fact]
        public void TheVerdictIsReadyWithinTheFirstSecondOfTheStream()
        {
            var g = new TsGen();
            var probe = new TsStreamProbe();
            double seconds = 0;
            StreamInfo info = null;
            for (int i = 0; i < 100; i++)
            {
                var chunk = g.GenerateFrames(1);
                probe.Feed(chunk, 0, chunk.Length);
                seconds += 1.0 / g.Fps;
                info = probe.Snapshot();
                if (info.IsComplete) break;
            }
            Assert.True(info.IsComplete);
            Assert.True(seconds <= 1.0, "needed " + seconds + " s of stream");
            Assert.True(CastCompatibility.Evaluate(info, null, false).IsCompatible);
        }

        [Fact]
        public void BeforeTheSpsIsSeenTheVerdictIsStillUnknown()
        {
            var g = new TsGen { SpsLate = true };
            var data = g.Generate(2);
            // feed packet by packet until the PMT is known: the video has not shown an SPS by then
            var probe = new TsStreamProbe();
            int n = 0;
            while (probe.Snapshot().Program == null && n < 50) { probe.Feed(data, n * 188, 188); n++; }
            var info = probe.Snapshot();
            Assert.NotNull(info.Program);
            Assert.Null(info.Sps);
            Assert.False(info.IsComplete);
            Assert.Equal(CastSupport.Unknown, CastCompatibility.Evaluate(info, null, false).Support);
        }

        [Fact]
        public void DataThatDoesNotStartOnAPacketBoundaryIsStillAnalysed()
        {
            var g = new TsGen();
            var data = g.Generate(3);
            var shifted = new byte[data.Length + 123];
            new Random(5).NextBytes(shifted);
            for (int i = 0; i < 123; i++) if (shifted[i] == 0x47) shifted[i] = 0x46;
            Buffer.BlockCopy(data, 0, shifted, 123, data.Length);
            var v = CastCompatibility.Analyze(shifted, 0, shifted.Length, null);
            Assert.True(v.IsCompatible, v.ToString());
        }

        [Fact]
        public void TheProbeSurvivesGarbageAndMutatedStreams()
        {
            var rnd = new Random(11);
            var g = new TsGen();
            var good = g.Generate(4);
            for (int round = 0; round < 60; round++)
            {
                var d = (byte[])good.Clone();
                int mutations = 1 + rnd.Next(200);
                for (int m = 0; m < mutations; m++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
                if (round % 5 == 0) { d = new byte[rnd.Next(5000)]; rnd.NextBytes(d); }
                var v = CastCompatibility.Analyze(d, 0, d.Length, null);
                Assert.NotNull(v);
                Assert.NotNull(v.Reason);
                Assert.NotNull(v.ToString());
            }
        }

        [Fact]
        public void IncrementalFeedingGivesTheSameVerdictAsOneShot()
        {
            var g = new TsGen();
            var data = g.Generate(4);
            var probe = new TsStreamProbe();
            var rnd = new Random(2);
            for (int pos = 0; pos < data.Length; )
            {
                int n = Math.Min(data.Length - pos, 188 * (1 + rnd.Next(40)));
                probe.Feed(data, pos, n);
                pos += n;
            }
            var a = CastCompatibility.Evaluate(probe.Snapshot(), null, true);
            var b = CastCompatibility.Analyze(data, 0, data.Length, null);
            Assert.Equal(b.ToString(), a.ToString());
        }

        // ------------------------------------------------------------ the codec header parsers

        [Fact]
        public void AnSpsOf1080pHasTheRightSizeAfterCropping()
        {
            var nal = H264Bytes.Sps(1920, 1080, 100, 41, 29.97);
            H264Sps s;
            Assert.True(H264Sps.TryParse(nal, 0, nal.Length, out s));
            Assert.Equal(1920, s.Width); Assert.Equal(1080, s.Height);
            Assert.Equal(100, s.ProfileIdc); Assert.Equal(41, s.LevelIdc);
            Assert.True(s.FrameMbsOnly);
            Assert.InRange(s.Fps, 29.96, 29.98);
            Assert.Equal("High", s.ProfileName);
        }

        [Fact]
        public void AnSpsWithScalingListsAndWithoutVuiParses()
        {
            var nal = H264Bytes.Sps(1280, 720, 100, 31, 25, false, false, 1, 8, true);
            H264Sps s;
            Assert.True(H264Sps.TryParse(nal, 0, nal.Length, out s));
            Assert.Equal(1280, s.Width); Assert.Equal(720, s.Height);
            Assert.Equal(0, s.Fps);
        }

        [Fact]
        public void BaselineSpsHasNoChromaFields()
        {
            var nal = H264Bytes.Sps(640, 480, 66, 30, 15);
            H264Sps s;
            Assert.True(H264Sps.TryParse(nal, 0, nal.Length, out s));
            Assert.Equal(640, s.Width); Assert.Equal(480, s.Height); Assert.Equal("Baseline", s.ProfileName);
        }

        [Fact]
        public void InterlacedSpsDoublesTheMapUnitsAndAdjustsTheCrop()
        {
            var nal = H264Bytes.Sps(1920, 1080, 100, 40, 25, true);
            H264Sps s;
            Assert.True(H264Sps.TryParse(nal, 0, nal.Length, out s));
            Assert.False(s.FrameMbsOnly);
            Assert.Equal(1920, s.Width); Assert.Equal(1080, s.Height);
        }

        [Fact]
        public void TruncatedOrWrongSpsAreRejectedWithoutThrowing()
        {
            var nal = H264Bytes.Sps(1920, 1080);
            H264Sps s;
            for (int len = 0; len < 10; len++) Assert.False(H264Sps.TryParse(nal, 0, len, out s), "len " + len);
            for (int len = 10; len <= nal.Length; len++) H264Sps.TryParse(nal, 0, len, out s);      // a cut inside the VUI only costs the frame rate; none of them throws
            var other = (byte[])nal.Clone(); other[0] = 0x68;
            Assert.False(H264Sps.TryParse(other, 0, other.Length, out s));
            Assert.False(H264Sps.TryParse(null, 0, 10, out s));
            Assert.False(H264Sps.TryParse(nal, 5, nal.Length, out s));
            Assert.False(H264Sps.TryParse(nal, -1, 3, out s));
        }

        [Fact]
        public void RandomBytesNeverThrowInTheSpsParser()
        {
            var rnd = new Random(3);
            for (int i = 0; i < 20000; i++)
            {
                var b = new byte[3 + rnd.Next(60)];
                rnd.NextBytes(b);
                b[0] = 0x67;
                H264Sps s;
                if (H264Sps.TryParse(b, 0, b.Length, out s))
                {
                    Assert.InRange(s.Width, 1, 16400);
                    Assert.InRange(s.Height, 1, 33000);
                }
            }
        }

        [Fact]
        public void AdtsHeaderIsParsed()
        {
            var h = new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x13, 0x1F, 0xFC, 0 };
            AdtsHeader a;
            Assert.True(AdtsHeader.TryParse(h, 0, h.Length, out a));
            Assert.Equal(2, a.ObjectType);
            Assert.Equal(48000, a.SampleRate);
            Assert.Equal(2, a.Channels);
            Assert.Equal("AAC-LC 48000 Hz 2 ch (ADTS)", a.ToString());
            h[2] = 0x7C;                                                                     // sampling frequency index 15: invalid
            Assert.False(AdtsHeader.TryParse(h, 0, h.Length, out a));
            Assert.False(AdtsHeader.TryParse(new byte[] { 0xFF, 0xF1 }, 0, 2, out a));
            Assert.False(AdtsHeader.TryParse(new byte[] { 0xFF, 0xF3, 0x4C, 0x80, 0x13, 0x1F, 0xFC, 0 }, 0, 8, out a));   // layer != 0
        }

        [Fact]
        public void MpegAudioHeaderTellsLayer2FromLayer3()
        {
            MpegAudioHeader h;
            Assert.True(MpegAudioHeader.TryParse(new byte[] { 0xFF, 0xFD, 0xA4, 0x04 }, 0, 4, out h));
            Assert.Equal(2, h.Layer); Assert.Equal(48000, h.SampleRate); Assert.Equal("MP2", h.ToString());
            Assert.True(MpegAudioHeader.TryParse(new byte[] { 0xFF, 0xFB, 0x90, 0x04 }, 0, 4, out h));
            Assert.Equal(3, h.Layer); Assert.Equal(44100, h.SampleRate); Assert.Equal("MP3", h.ToString());
            Assert.False(MpegAudioHeader.TryParse(new byte[] { 0xFF, 0xE1, 0x90, 0x04 }, 0, 4, out h));
        }

        [Fact]
        public void TheProgramParserReadsPmtsThatSpanSeveralPackets()
        {
            // a PMT with many descriptors does not fit in one packet
            var es = new List<byte> { 0x1B, 0xE1, 0x00, 0xF0, 0 };
            for (int a = 0; a < 12; a++)
            {
                es.AddRange(new byte[] { 0x0F, 0xE1, (byte)(0x01 + a), 0xF0, 32, 0x0A, 4, (byte)'s', (byte)'p', (byte)'a', 0, 0xC0, 24 });
                es.AddRange(new byte[24]);
            }
            var pmt = new List<byte> { 0x02, 0xB0, 0, 0, 1, 0xC1, 0, 0, 0xE1, 0x00, 0xF0, 0 };
            pmt.AddRange(es);
            int secLen = pmt.Count - 3 + 4;
            pmt[1] = (byte)(0xB0 | (secLen >> 8)); pmt[2] = (byte)secLen;
            uint crc = TsCrcForTests.Compute(pmt.ToArray(), 0, pmt.Count);
            pmt.AddRange(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
            Assert.True(pmt.Count > 183);
            var parser = new TsProgramParser();
            var pat = new byte[188]; for (int i = 0; i < 188; i++) pat[i] = 0xFF;
            var patSec = new byte[] { 0x00, 0xB0, 13, 0, 1, 0xC1, 0, 0, 0, 1, 0xE0, 0x50 };
            uint pc = TsCrcForTests.Compute(patSec, 0, patSec.Length);
            pat[0] = 0x47; pat[1] = 0x40; pat[2] = 0; pat[3] = 0x10; pat[4] = 0;
            Buffer.BlockCopy(patSec, 0, pat, 5, patSec.Length);
            pat[17] = (byte)(pc >> 24); pat[18] = (byte)(pc >> 16); pat[19] = (byte)(pc >> 8); pat[20] = (byte)pc;
            parser.FeedPacket(pat, 0);
            Assert.Equal(0x50, parser.PmtPid);
            int pos = 0, cc = 0;
            bool first = true;
            while (pos < pmt.Count)
            {
                var p = new byte[188]; for (int i = 0; i < 188; i++) p[i] = 0xFF;
                p[0] = 0x47; p[1] = (byte)((first ? 0x40 : 0) | 0); p[2] = 0x50; p[3] = (byte)(0x10 | (cc++ & 0xF));
                int start = 4;
                if (first) { p[4] = 0; start = 5; }
                int n = Math.Min(188 - start, pmt.Count - pos);
                for (int i = 0; i < n; i++) p[start + i] = pmt[pos + i];
                pos += n; first = false;
                parser.FeedPacket(p, 0);
            }
            Assert.NotNull(parser.Program);
            Assert.Equal(13, parser.Program.Streams.Count);
        }

        [Fact]
        public void ACorruptedPmtIsIgnored()
        {
            var g = new TsGen();
            var data = g.Generate(2);
            // damage every PMT: the CRC no longer matches
            for (int i = 0; i + 188 <= data.Length; i += 188)
                if ((((data[i + 1] & 0x1F) << 8) | data[i + 2]) == 0x1000) data[i + 10] ^= 0x40;
            var v = CastCompatibility.Analyze(data, 0, data.Length, null);
            Assert.True(v.IsIncompatible);
            Assert.Equal(CastIssueCode.NoProgram, v.Blocking.Single().Code);
        }
    }
}
