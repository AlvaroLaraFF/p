using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>
    /// The software MP2 decoder, against FFmpeg's. Tests/PAndroid.Tests/Data/Mp2 holds MPEG audio Layer II frames and the 16 bit PCM that libav's decoder (avdec_mp2float, the same one FFmpeg
    /// has) makes of them. Two kinds of stream: ones made by libav's own MP2 encoder (a chirp, a tone and noise at every sampling rate the encoder offers, stereo and mono, from 24 to 384 kbit/s)
    /// and random but syntactically valid frames that exercise what encoders rarely do (every quantizer class, joint stereo with intensity, dual channel, every scale factor selection, the
    /// low-rate allocation tables, MPEG-2 low sampling frequencies, CRC and padding). The same frames were also checked against mpg123, and a long stretch of real music against both.
    /// </summary>
    public class Mp2DecoderTests
    {
        private static string DataDir
        {
            get { return Path.Combine(AppContext.BaseDirectory, "Data", "Mp2"); }
        }

        public static IEnumerable<object[]> Vectors()
        {
            foreach (var f in Directory.GetFiles(DataDir, "*.mp2").OrderBy(x => x)) yield return new object[] { Path.GetFileNameWithoutExtension(f) };
        }

        /// <summary>The frames of a raw Layer II stream, found by their headers.</summary>
        private static List<byte[]> Frames(byte[] data)
        {
            var list = new List<byte[]>();
            int pos = 0;
            while (pos + 4 <= data.Length)
            {
                Mp2Header h;
                if (!Mp2Header.TryParse(data, pos, data.Length - pos, out h)) { pos++; continue; }
                if (pos + h.FrameBytes > data.Length) break;
                var f = new byte[h.FrameBytes];
                Array.Copy(data, pos, f, 0, h.FrameBytes);
                list.Add(f); pos += h.FrameBytes;
            }
            return list;
        }

        private static short[] Decode(Mp2Decoder dec, IEnumerable<byte[]> frames, out int channels, out int rate)
        {
            var all = new List<short>();
            var pcm = new short[Mp2Decoder.SamplesPerFrame * 2];
            channels = 0; rate = 0;
            foreach (var f in frames)
            {
                Assert.True(dec.Decode(f, 0, f.Length, pcm, out channels, out rate));
                all.AddRange(pcm.Take(Mp2Decoder.SamplesPerFrame * channels));
            }
            return all.ToArray();
        }

        // ------------------------------------------------------------------ against FFmpeg

        [Theory]
        [MemberData(nameof(Vectors))]
        public void DecodesLikeFfmpegToWithinOneStepOfTheLastBit(string name)
        {
            var frames = Frames(File.ReadAllBytes(Path.Combine(DataDir, name + ".mp2")));
            byte[] refBytes = File.ReadAllBytes(Path.Combine(DataDir, name + ".pcm"));
            var expected = new short[refBytes.Length / 2];
            Buffer.BlockCopy(refBytes, 0, expected, 0, refBytes.Length);
            int ch, rate;
            var got = Decode(new Mp2Decoder(), frames, out ch, out rate);
            Assert.Equal(expected.Length, got.Length);
            int worst = 0;
            for (int i = 0; i < got.Length; i++) worst = Math.Max(worst, Math.Abs(got[i] - expected[i]));
            Assert.True(worst <= 1, name + ": the largest difference from FFmpeg is " + worst + " (of 32768)");
        }

        [Fact]
        public void TheVectorsCoverEveryRateModeAndTable()
        {
            var seen = new HashSet<string>();
            foreach (var name in Vectors().Select(v => (string)v[0]))
            {
                foreach (var f in Frames(File.ReadAllBytes(Path.Combine(DataDir, name + ".mp2"))))
                {
                    Mp2Header h;
                    Assert.True(Mp2Header.TryParse(f, 0, f.Length, out h));
                    seen.Add((h.IsMpeg1 ? "mpeg1 " : "lsf ") + h.SampleRate + " mode" + h.Mode);
                }
            }
            foreach (int rate in new[] { 32000, 44100, 48000 }) foreach (int mode in new[] { 0, 1, 2, 3 }) Assert.Contains("mpeg1 " + rate + " mode" + mode, seen);
            foreach (int rate in new[] { 16000, 22050, 24000 }) Assert.Contains(seen, s => s.StartsWith("lsf " + rate));
        }

        [Fact]
        public void TheDecodedSignalIsTheRightLoudnessNotJustTheRightShape()
        {
            // the encoder's test signal peaks around 0.8 of full scale: a decoder that is 3 dB too quiet (or too loud) would fail the comparison above, this states it in plain terms
            var frames = Frames(File.ReadAllBytes(Path.Combine(DataDir, "enc_st48_192.mp2")));
            int ch, rate;
            var pcm = Decode(new Mp2Decoder(), frames, out ch, out rate);
            int peak = pcm.Max(x => Math.Abs((int)x));
            Assert.InRange(peak, 12000, 32767);
            Assert.Equal(2, ch); Assert.Equal(48000, rate);
        }

        // ------------------------------------------------------------------ the header

        private static byte[] Header(int version, int layer, int prot, int bri, int sri, int pad, int mode, int ext = 0)
        {
            return new byte[]
            {
                0xFF, (byte)(0xE0 | (version << 3) | (layer << 1) | prot),
                (byte)((bri << 4) | (sri << 2) | (pad << 1)), (byte)((mode << 6) | (ext << 4))
            };
        }

        [Theory]
        // version, bitrate index (1 = 32 kbit/s), sampling index, padding, kbit/s, rate, frame bytes
        [InlineData(3, 10, 1, 0, 192, 48000, 576)]
        [InlineData(3, 10, 1, 1, 192, 48000, 577)]
        [InlineData(3, 8, 0, 0, 128, 44100, 417)]
        [InlineData(3, 8, 0, 1, 128, 44100, 418)]
        [InlineData(3, 14, 2, 0, 384, 32000, 1728)]
        [InlineData(3, 14, 2, 1, 384, 32000, 1729)]
        [InlineData(2, 4, 1, 0, 32, 24000, 192)]     // MPEG-2 low sampling frequency: its bitrate table is 8, 16, 24, 32...
        [InlineData(2, 14, 2, 0, 160, 16000, 1440)]
        public void ReadsTheFieldsOfAHeaderAndWorksOutTheFrameSize(int version, int bri, int sri, int pad, int kbps, int rate, int bytes)
        {
            var d = Header(version, 2, 1, bri, sri, pad, 0);
            Mp2Header h;
            Assert.True(Mp2Header.TryParse(d, 0, d.Length, out h));
            Assert.Equal(kbps, h.BitrateKbps); Assert.Equal(rate, h.SampleRate); Assert.Equal(bytes, h.FrameBytes);
            Assert.Equal(version == 3, h.IsMpeg1); Assert.Equal(pad == 1, h.Padding); Assert.False(h.HasCrc);
        }

        [Fact]
        public void ReadsTheModeTheJointStereoBoundAndTheProtectionBit()
        {
            Mp2Header h;
            var j = Header(3, 2, 0, 9, 1, 0, 1, 2);
            Assert.True(Mp2Header.TryParse(j, 0, 4, out h));
            Assert.Equal(1, h.Mode); Assert.Equal(2, h.ModeExtension); Assert.Equal(2, h.Channels); Assert.True(h.HasCrc);
            Assert.True(Mp2Header.TryParse(Header(3, 2, 1, 5, 1, 0, 3), 0, 4, out h));
            Assert.Equal(1, h.Channels);
        }

        [Theory]
        [InlineData(3, 3, 9, 1)]       // layer I
        [InlineData(3, 1, 9, 1)]       // layer III
        [InlineData(1, 2, 9, 1)]       // reserved version
        [InlineData(0, 2, 9, 1)]       // MPEG 2.5 has no layer II
        [InlineData(3, 2, 0, 1)]       // free format bitrate
        [InlineData(3, 2, 15, 1)]      // reserved bitrate
        [InlineData(3, 2, 9, 3)]       // reserved sampling rate
        public void RefusesWhatIsNotALayerIIHeader(int version, int layer, int bri, int sri)
        {
            var d = Header(version, layer, 1, bri, sri, 0, 0);
            Mp2Header h;
            Assert.False(Mp2Header.TryParse(d, 0, d.Length, out h));
        }

        [Fact]
        public void RefusesNoSyncAndShortOrMissingData()
        {
            Mp2Header h;
            var d = Header(3, 2, 1, 9, 1, 0, 0);
            d[0] = 0xFE;
            Assert.False(Mp2Header.TryParse(d, 0, 4, out h));
            d[0] = 0xFF; d[1] &= 0x1F;      // the sync is eleven bits
            Assert.False(Mp2Header.TryParse(d, 0, 4, out h));
            Assert.False(Mp2Header.TryParse(null, 0, 4, out h));
            Assert.False(Mp2Header.TryParse(new byte[] { 0xFF, 0xFD, 0x90 }, 0, 3, out h));
            Assert.False(Mp2Header.TryParse(Header(3, 2, 1, 9, 1, 0, 0), 1, 3, out h));
        }

        // ------------------------------------------------------------------ the decoder's contract

        private static byte[] FirstFrame(string name) { return Frames(File.ReadAllBytes(Path.Combine(DataDir, name + ".mp2")))[0]; }

        [Fact]
        public void ADecodeReturnsFalseAndWritesNothingForADamagedHeaderAShortFrameOrASmallBuffer()
        {
            var dec = new Mp2Decoder(); int ch, rate;
            var f = FirstFrame("enc_st48_192");
            var pcm = new short[Mp2Decoder.SamplesPerFrame * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 1234;
            Assert.False(dec.Decode(f, 0, f.Length - 1, pcm, out ch, out rate));              // shorter than the header says
            Assert.False(dec.Decode(f, 0, 3, pcm, out ch, out rate));
            Assert.False(dec.Decode(f, 0, f.Length, new short[100], out ch, out rate));       // no room
            var bad = (byte[])f.Clone(); bad[0] = 0;
            Assert.False(dec.Decode(bad, 0, bad.Length, pcm, out ch, out rate));
            Assert.All(pcm, x => Assert.Equal(1234, x));
            Assert.Equal(0, dec.FramesDecoded);
        }

        [Fact]
        public void AFrameCanSitInsideALargerBuffer()
        {
            var f = FirstFrame("enc_st48_192");
            var big = new byte[f.Length + 300]; Array.Copy(f, 0, big, 100, f.Length);
            var a = new Mp2Decoder(); var b = new Mp2Decoder();
            var p1 = new short[2304]; var p2 = new short[2304]; int ch, rate;
            Assert.True(a.Decode(f, 0, f.Length, p1, out ch, out rate));
            Assert.True(b.Decode(big, 100, f.Length + 200, p2, out ch, out rate));
            Assert.Equal(p1, p2);
            Assert.False(b.Decode(big, 100 + f.Length - 10, f.Length, p2, out ch, out rate));   // claims more than there is after the offset
        }

        [Fact]
        public void AFrameThatRunsOutOfBitsIsSilenceNotNoiseAndIsCounted()
        {
            var f = FirstFrame("enc_st48_56");
            var bad = (byte[])f.Clone();
            for (int i = 4; i < bad.Length; i++) bad[i] = 0xFF;                  // every field at its maximum needs far more bits than a 56 kbit/s frame has
            var dec = new Mp2Decoder(); int ch, rate;
            var pcm = new short[2304];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 99;
            Assert.True(dec.Decode(bad, 0, bad.Length, pcm, out ch, out rate));
            Assert.Equal(2, ch); Assert.All(pcm, x => Assert.Equal(0, x));
            Assert.Equal(1, dec.FramesCorrupt); Assert.Equal(0, dec.FramesDecoded);
            // and the decoder is fine afterwards: it decodes the next good frame exactly as a new one does
            var good = Frames(File.ReadAllBytes(Path.Combine(DataDir, "enc_st48_192.mp2")))[0];
            var fresh = new Mp2Decoder(); var p2 = new short[2304];
            Assert.True(dec.Decode(good, 0, good.Length, pcm, out ch, out rate));
            Assert.True(fresh.Decode(good, 0, good.Length, p2, out ch, out rate));
            Assert.Equal(p2, pcm);
        }

        [Fact]
        public void TheFilterHistoryCarriesFromFrameToFrameAndResetForgetsIt()
        {
            var frames = Frames(File.ReadAllBytes(Path.Combine(DataDir, "enc_st48_192.mp2")));
            int ch, rate;
            var seq = new Mp2Decoder(); var p = new short[2304];
            Assert.True(seq.Decode(frames[0], 0, frames[0].Length, p, out ch, out rate));
            var afterOne = new short[2304];
            Assert.True(seq.Decode(frames[1], 0, frames[1].Length, afterOne, out ch, out rate));
            var alone = new Mp2Decoder(); var second = new short[2304];
            Assert.True(alone.Decode(frames[1], 0, frames[1].Length, second, out ch, out rate));
            Assert.NotEqual(afterOne, second);                                      // frame 2 on its own has no history behind it
            seq.Reset();
            var again = new short[2304];
            Assert.True(seq.Decode(frames[1], 0, frames[1].Length, again, out ch, out rate));
            Assert.Equal(second, again);                                            // after a Reset it is the same as a new decoder
        }

        [Fact]
        public void TwoDecodersGiveTheSameSamplesBitForBit()
        {
            var frames = Frames(File.ReadAllBytes(Path.Combine(DataDir, "enc_st44_128.mp2")));
            int ch, rate;
            Assert.Equal(Decode(new Mp2Decoder(), frames, out ch, out rate), Decode(new Mp2Decoder(), frames, out ch, out rate));
        }

        [Fact]
        public void GarbageAfterAValidHeaderNeverThrowsAndNeverLeavesTheSampleRange()
        {
            var rnd = new Random(2024);
            var dec = new Mp2Decoder(); int ch, rate;
            var pcm = new short[2304];
            var headers = new List<byte[]>();
            foreach (int version in new[] { 3, 2 }) foreach (int mode in new[] { 0, 1, 2, 3 }) foreach (int bri in new[] { 1, 5, 9, 14 })
                for (int sri = 0; sri < 3; sri++) headers.Add(Header(version, 2, rnd.Next(2), bri, sri, rnd.Next(2), mode, rnd.Next(4)));
            int decoded = 0;
            for (int n = 0; n < 1500; n++)
            {
                var h = headers[rnd.Next(headers.Count)];
                Mp2Header hh;
                Assert.True(Mp2Header.TryParse(h, 0, 4, out hh));
                var data = new byte[hh.FrameBytes];
                rnd.NextBytes(data);
                Array.Copy(h, data, 4);
                if (dec.Decode(data, 0, data.Length, pcm, out ch, out rate)) decoded++;
                Assert.InRange(ch, 1, 2);
            }
            Assert.Equal(1500, decoded);
            Assert.True(dec.FramesCorrupt + dec.FramesDecoded == 1500);
        }

        [Fact]
        public void ADecodedFrameWritesExactlyTheSamplesOfItsChannels()
        {
            var dec = new Mp2Decoder(); int ch, rate;
            var mono = FirstFrame("enc_mono32_64");
            var pcm = new short[2304];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 4321;
            Assert.True(dec.Decode(mono, 0, mono.Length, pcm, out ch, out rate));
            Assert.Equal(1, ch); Assert.Equal(32000, rate);
            Assert.All(pcm.Skip(1152), x => Assert.Equal(4321, x));                 // a mono frame touches only 1152 samples
            Assert.Contains(pcm.Take(1152), x => x != 4321);
        }
    }
}

namespace PAndroid.Tests
{
    /// <summary>The Android-free half of the MP2 layer: frames arriving in pieces, the offset of the next sample, silence for a bad frame, a change of format.</summary>
    public class Mp2TranscoderTests
    {
        private static string DataDir { get { return Path.Combine(AppContext.BaseDirectory, "Data", "Mp2"); } }

        private static List<byte[]> Frames(string name)
        {
            var data = File.ReadAllBytes(Path.Combine(DataDir, name + ".mp2"));
            var list = new List<byte[]>();
            int pos = 0;
            while (pos + 4 <= data.Length)
            {
                Mp2Header h;
                if (!Mp2Header.TryParse(data, pos, data.Length - pos, out h)) { pos++; continue; }
                if (pos + h.FrameBytes > data.Length) break;
                var f = new byte[h.FrameBytes]; Array.Copy(data, pos, f, 0, f.Length); list.Add(f); pos += f.Length;
            }
            return list;
        }

        private static short[] Direct(byte[] frame)
        {
            var pcm = new short[2304]; int ch, rate;
            Assert.True(new Mp2Decoder().Decode(frame, 0, frame.Length, pcm, out ch, out rate));
            return pcm.Take(1152 * ch).ToArray();
        }

        private static short[] Shorts(byte[] bytes, int count)
        {
            var s = new short[count / 2];
            Buffer.BlockCopy(bytes, 0, s, 0, count);
            return s;
        }

        [Fact]
        public void AFrameAppendedInPiecesComesOutAsTheSamePcmTheDecoderMakes()
        {
            var frame = Frames("enc_st48_192")[0];
            var t = new Mp2Transcoder(48000, 2);
            // the way the MPEG audio reader appends: the four bytes of the header, then the rest in a few bites
            t.Append(frame, 0, 4);
            for (int pos = 4; pos < frame.Length; pos += 100) t.Append(frame, pos, Math.Min(100, frame.Length - pos));
            Assert.Equal(frame.Length, t.Pending);
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes]; bool changed;
            int n = t.Complete(frame.Length, 0, bytes, out changed);
            Assert.Equal(1152 * 2 * 2, n); Assert.False(changed); Assert.Equal(0, t.Pending);
            Assert.Equal(Direct(frame), Shorts(bytes, n));
        }

        [Fact]
        public void TheBytesOfTheNextSampleThatAlreadyArrivedAreKept()
        {
            var frames = Frames("enc_st48_192");
            var t = new Mp2Transcoder(48000, 2);
            t.Append(frames[0], 0, frames[0].Length);
            t.Append(frames[1], 0, 100);
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes]; bool changed;
            int n = t.Complete(frames[0].Length, 100, bytes, out changed);
            Assert.Equal(Direct(frames[0]), Shorts(bytes, n));
            Assert.Equal(100, t.Pending);
            t.Append(frames[1], 100, frames[1].Length - 100);
            n = t.Complete(frames[1].Length, 0, bytes, out changed);
            // the second frame decodes with the first one's filter history behind it, as in a stream
            var both = new Mp2Decoder(); var pcm = new short[2304]; int ch, rate;
            both.Decode(frames[0], 0, frames[0].Length, pcm, out ch, out rate);
            both.Decode(frames[1], 0, frames[1].Length, pcm, out ch, out rate);
            Assert.Equal(pcm.Take(2304).ToArray(), Shorts(bytes, n));
            Assert.Equal(2, t.FramesDecoded); Assert.Equal(0, t.FramesSilenced);
        }

        [Fact]
        public void BytesCanBeWrittenStraightIntoTheRoomItGives()
        {
            var frame = Frames("enc_mono32_64")[0];
            var t = new Mp2Transcoder(32000, 1);
            int pos;
            var room = t.Room(frame.Length, out pos);
            Array.Copy(frame, 0, room, pos, frame.Length);
            t.Added(frame.Length);
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes]; bool changed;
            int n = t.Complete(frame.Length, 0, bytes, out changed);
            Assert.Equal(1152 * 2, n);
            Assert.Equal(Direct(frame), Shorts(bytes, n));
            t.Added(-5); t.Added(0);
            Assert.Equal(0, t.Pending);
        }

        [Fact]
        public void AFrameThatDoesNotDecodeBecomesSilenceOfTheSameLength()
        {
            var t = new Mp2Transcoder(48000, 2);
            var junk = new byte[576]; new Random(1).NextBytes(junk); junk[0] = 0; // no sync
            t.Append(junk, 0, junk.Length);
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = 7;
            bool changed;
            int n = t.Complete(junk.Length, 0, bytes, out changed);
            Assert.Equal(1152 * 2 * 2, n); Assert.False(changed);
            Assert.All(bytes.Take(n), b => Assert.Equal(0, b));
            Assert.Equal(1, t.FramesSilenced); Assert.Equal(0, t.FramesDecoded);
            // a sample that claims more bytes than arrived is silence too, and nothing throws
            n = t.Complete(1000, 0, bytes, out changed);
            Assert.Equal(1152 * 2 * 2, n); Assert.Equal(2, t.FramesSilenced);
        }

        [Fact]
        public void AChangeOfFormatIsReportedOnceAndTheNewFormatIsKept()
        {
            var t = new Mp2Transcoder(48000, 2);
            var mono = Frames("enc_mono32_64");
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes]; bool changed;
            t.Append(mono[0], 0, mono[0].Length);
            int n = t.Complete(mono[0].Length, 0, bytes, out changed);
            Assert.True(changed); Assert.Equal(32000, t.SampleRate); Assert.Equal(1, t.Channels); Assert.Equal(1152 * 2, n);
            t.Append(mono[1], 0, mono[1].Length);
            n = t.Complete(mono[1].Length, 0, bytes, out changed);
            Assert.False(changed); Assert.Equal(1152 * 2, n);
        }

        [Fact]
        public void ResetThrowsAwayWhatWasAppendedAndTheFilterHistory()
        {
            var frames = Frames("enc_st44_128");
            var t = new Mp2Transcoder(44100, 2);
            var bytes = new byte[Mp2Transcoder.MaxPcmBytes]; bool changed;
            t.Append(frames[0], 0, frames[0].Length);
            t.Complete(frames[0].Length, 0, bytes, out changed);
            t.Append(frames[1], 0, 50);
            t.Reset();
            Assert.Equal(0, t.Pending);
            t.Append(frames[1], 0, frames[1].Length);
            int n = t.Complete(frames[1].Length, 0, bytes, out changed);
            Assert.Equal(Direct(frames[1]), Shorts(bytes, n));          // as if it were the first frame of a stream
        }

        [Fact]
        public void TheBufferGrowsForALargeAppend()
        {
            var t = new Mp2Transcoder(48000, 2);
            var big = new byte[10000];
            t.Append(big, 0, big.Length);
            Assert.Equal(10000, t.Pending);
        }
    }
}
