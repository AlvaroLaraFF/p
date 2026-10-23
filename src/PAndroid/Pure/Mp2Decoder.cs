using System;

namespace PAndroid
{
    /// <summary>The fields of an MPEG audio frame header that matter to a Layer II decoder.</summary>
    public struct Mp2Header
    {
        /// <summary>true for MPEG-1 (32, 44.1 and 48 kHz), false for MPEG-2 "low sampling frequency" (16, 22.05 and 24 kHz).</summary>
        public bool IsMpeg1;
        public int BitrateKbps, SampleRate;
        /// <summary>0 stereo, 1 joint stereo, 2 dual channel, 3 mono.</summary>
        public int Mode;
        public int ModeExtension;
        public bool HasCrc;
        public bool Padding;
        /// <summary>The whole frame, header included, in bytes.</summary>
        public int FrameBytes;
        public int Channels { get { return Mode == 3 ? 1 : 2; } }
        public int BitrateIndex;

        /// <summary>
        /// Reads the header at <paramref name="offset"/>. false if there is no MPEG audio Layer II header there (no sync, another layer, MPEG 2.5, a reserved bitrate or sampling rate): the
        /// extractor that cuts the frames has already checked this, so a false is a corrupt frame.
        /// </summary>
        public static bool TryParse(byte[] data, int offset, int length, out Mp2Header h)
        {
            h = default(Mp2Header);
            if (data == null || length < 4 || offset < 0 || offset + 4 > data.Length) return false;
            int b0 = data[offset], b1 = data[offset + 1], b2 = data[offset + 2], b3 = data[offset + 3];
            if (b0 != 0xFF || (b1 & 0xE0) != 0xE0) return false;                     // 11 bits of sync
            int version = (b1 >> 3) & 3, layer = (b1 >> 1) & 3;
            if (layer != 2) return false;                                            // 01 = III, 10 = II, 11 = I
            if (version != 3 && version != 2) return false;                          // 11 = MPEG-1, 10 = MPEG-2; 00 = MPEG-2.5 and 01 = reserved are not for Layer II
            int bitrateIndex = (b2 >> 4) & 15;
            if (bitrateIndex == 0 || bitrateIndex == 15) return false;               // free format and reserved
            int srIndex = (b2 >> 2) & 3;
            if (srIndex == 3) return false;
            h.IsMpeg1 = version == 3;
            h.BitrateIndex = bitrateIndex - 1;
            h.BitrateKbps = Mp2Decoder.BitRates[(h.IsMpeg1 ? 0 : 14) + h.BitrateIndex];
            h.SampleRate = Mp2Decoder.SampleRates[(h.IsMpeg1 ? 0 : 4) + srIndex];
            h.Padding = ((b2 >> 1) & 1) != 0;
            h.HasCrc = (b1 & 1) == 0;                                                // protection bit 0 = a 16 bit CRC follows the header
            h.Mode = (b3 >> 6) & 3;
            h.ModeExtension = (b3 >> 4) & 3;
            h.FrameBytes = 144000 * h.BitrateKbps / h.SampleRate + (h.Padding ? 1 : 0);
            return true;
        }
    }

    /// <summary>
    /// A software decoder for MPEG-1 and MPEG-2 (low sampling frequency) audio Layer II, "MP2": the audio of most digital television transport streams (DVB) and of many IPTV channels. Android
    /// does not require a device to decode it and Fire TV's specifications do not list it (they list AAC, AC-3, E-AC-3, MP3...), so a stick may have no decoder for it: ExoPlayer would play such a
    /// channel silently. The player turns the MP2 frames of the channel into PCM with this class when the device has no decoder of its own (see Mp2ExtractorsFactory).
    ///
    /// It decodes one frame at a time into 16 bit PCM, keeping the synthesis filter's history between frames, so the frames of a stream have to go through one instance in order. It is
    /// pure managed code with no allocation per frame, about 1.5 million multiply-adds per second of stereo audio.
    ///
    /// Derived from pl_mpeg (https://github.com/phoboslab/pl_mpeg, MIT License, Copyright (c) 2019 Dominic Szablewski), whose audio part is based on kjmp2 by Martin J. Fiedler
    /// (http://keyj.emphy.de/kjmp2/, a permissive licence). The tables and the arithmetic (including the fast 32-point IDCT) are theirs; the frame and header handling, the bit reader, MPEG-2 low
    /// sampling frequency, mono output, the clipping to 16 bits and the protection against corrupt frames are new. Checked against FFmpeg's decoder (tests/PAndroid.Tests/Mp2DecoderTests).
    ///
    /// MIT License: Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software
    /// without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to
    /// whom the Software is furnished to do so, subject to the following conditions: the above copyright notice and this permission notice shall be included in all copies or substantial
    /// portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR
    /// A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
    /// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
    /// </summary>
    public sealed class Mp2Decoder
    {
        /// <summary>Samples per channel in every Layer II frame.</summary>
        public const int SamplesPerFrame = 1152;
        /// <summary>The largest frame: 384 kbit/s at 32 kHz is 1728 bytes, plus padding.</summary>
        public const int MaxFrameBytes = 1729;

        internal static readonly int[] SampleRates = { 44100, 48000, 32000, 0, 22050, 24000, 16000, 0 };
        internal static readonly int[] BitRates =
        {
            32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384,       // MPEG-1
             8, 16, 24, 32, 40, 48,  56,  64,  80,  96, 112, 128, 144, 160        // MPEG-2
        };

        /// <summary>The scale factors of ISO 11172-3 table 3-B.1: 2 * 2^(-i / 3) for i = 0 to 62.</summary>
        private static readonly float[] ScaleFactors = BuildScaleFactors();

        private static float[] BuildScaleFactors()
        {
            var t = new float[64];
            for (int i = 0; i < 63; i++) t[i] = (float)(2.0 * Math.Pow(2.0, -i / 3.0));
            return t;
        }

        /// <summary>From the filter's accumulated value to a fraction of full scale (the sign: the dequantiser's fractions are flipped). pl_mpeg's constant is 1090519040, which is 2^30 * 65 / 64 and goes with its coarser dequantisation.</summary>
        private const float OutputScale = -1.0f / 1073741824.0f;

        // Quantizer lookup, step 1: bitrate classes (index: mono = 0, stereo = 1; then the bitrate index of the MPEG-1 table)
        private static readonly byte[][] QuantLutStep1 =
        {
            //  32  48  56  64  80  96 112 128 160 192 224 256 320 384 <- bitrate
            new byte[] { 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0 },
            //  16  24  28  32  40  48  56  64  80  96 112 128 160 192 <- bitrate per channel
            new byte[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 0, 0 }
        };

        // Quantizer lookup, step 2: bitrate class, sample rate -> B2 table index (bit 6) and sblimit (low bits)
        private const int TabA = 27 | 64, TabB = 30 | 64, TabC = 8, TabD = 12;
        private static readonly byte[][] QuantLutStep2 =
        {
            //                44.1 kHz, 48 kHz, 32 kHz
            new byte[] { TabC, TabC, TabD },       // 32 - 48 kbit/s/ch
            new byte[] { TabA, TabA, TabA },       // 56 - 80 kbit/s/ch
            new byte[] { TabB, TabA, TabB }        // 96+ kbit/s/ch
        };

        // Quantizer lookup, step 3: B2 table, subband -> nbal (upper 4 bits) and row index (lower 4 bits)
        private static readonly byte[][] QuantLutStep3 =
        {
            // Low-rate table (3-B.2c and 3-B.2d)
            new byte[] { 0x44, 0x44, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34 },
            // High-rate table (3-B.2a and 3-B.2b)
            new byte[]
            {
                0x43, 0x43, 0x43, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42,
                0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31, 0x31,
                0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20
            },
            // MPEG-2 LSF table (B.1 in ISO 13818-3)
            new byte[]
            {
                0x45, 0x45, 0x45, 0x45,
                0x34, 0x34, 0x34, 0x34, 0x34, 0x34, 0x34,
                0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24,
                0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24, 0x24
            }
        };

        // Quantizer lookup, step 4: table row, allocation value -> quantizer table index (0 = no bits)
        private static readonly byte[][] QuantLutStep4 =
        {
            new byte[] { 0, 1, 2, 17 },
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 17 },
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 17 },
            new byte[] { 0, 1, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 },
            new byte[] { 0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }
        };

        // The quantizers: number of levels, grouped samples (three codes in one word) and bits per code or group. Index 1..17 (0 = none).
        private static readonly int[] QuantLevels = { 0, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535 };
        private static readonly bool[] QuantGroup = { false, true, true, false, true, false, false, false, false, false, false, false, false, false, false, false, false, false };
        private static readonly int[] QuantBits = { 0, 5, 7, 3, 10, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        private static readonly float[] SynthesisWindow =
        {
            0.0f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, -1.0f,
            -1.0f, -1.0f, -1.0f, -1.5f, -1.5f, -2.0f, -2.0f, -2.5f,
            -2.5f, -3.0f, -3.5f, -3.5f, -4.0f, -4.5f, -5.0f, -5.5f,
            -6.5f, -7.0f, -8.0f, -8.5f, -9.5f, -10.5f, -12.0f, -13.0f,
            -14.5f, -15.5f, -17.5f, -19.0f, -20.5f, -22.5f, -24.5f, -26.5f,
            -29.0f, -31.5f, -34.0f, -36.5f, -39.5f, -42.5f, -45.5f, -48.5f,
            -52.0f, -55.5f, -58.5f, -62.5f, -66.0f, -69.5f, -73.5f, -77.0f,
            -80.5f, -84.5f, -88.0f, -91.5f, -95.0f, -98.0f, -101.0f, -104.0f,
            106.5f, 109.0f, 111.0f, 112.5f, 113.5f, 114.0f, 114.0f, 113.5f,
            112.0f, 110.5f, 107.5f, 104.0f, 100.0f, 94.5f, 88.5f, 81.5f,
            73.0f, 63.5f, 53.0f, 41.5f, 28.5f, 14.5f, -1.0f, -18.0f,
            -36.0f, -55.5f, -76.5f, -98.5f, -122.0f, -147.0f, -173.5f, -200.5f,
            -229.5f, -259.5f, -290.5f, -322.5f, -355.5f, -389.5f, -424.0f, -459.5f,
            -495.5f, -532.0f, -568.5f, -605.0f, -641.5f, -678.0f, -714.0f, -749.0f,
            -783.5f, -817.0f, -849.0f, -879.5f, -908.5f, -935.0f, -959.5f, -981.0f,
            -1000.5f, -1016.0f, -1028.5f, -1037.5f, -1042.5f, -1043.5f, -1040.0f, -1031.5f,
            1018.5f, 1000.0f, 976.0f, 946.5f, 911.0f, 869.5f, 822.0f, 767.5f,
            707.0f, 640.0f, 565.5f, 485.0f, 397.0f, 302.5f, 201.0f, 92.5f,
            -22.5f, -144.0f, -272.5f, -407.0f, -547.5f, -694.0f, -846.0f, -1003.0f,
            -1165.0f, -1331.5f, -1502.0f, -1675.5f, -1852.5f, -2031.5f, -2212.5f, -2394.0f,
            -2576.5f, -2758.5f, -2939.5f, -3118.5f, -3294.5f, -3467.5f, -3635.5f, -3798.5f,
            -3955.0f, -4104.5f, -4245.5f, -4377.5f, -4499.0f, -4609.5f, -4708.0f, -4792.5f,
            -4863.5f, -4919.0f, -4958.0f, -4979.5f, -4983.0f, -4967.5f, -4931.5f, -4875.0f,
            -4796.0f, -4694.5f, -4569.5f, -4420.0f, -4246.0f, -4046.0f, -3820.0f, -3567.0f,
            3287.0f, 2979.5f, 2644.0f, 2280.5f, 1888.0f, 1467.5f, 1018.5f, 541.0f,
            35.0f, -499.0f, -1061.0f, -1650.0f, -2266.5f, -2909.0f, -3577.0f, -4270.0f,
            -4987.5f, -5727.5f, -6490.0f, -7274.0f, -8077.5f, -8899.5f, -9739.0f, -10594.5f,
            -11464.5f, -12347.0f, -13241.0f, -14144.5f, -15056.0f, -15973.5f, -16895.5f, -17820.0f,
            -18744.5f, -19668.0f, -20588.0f, -21503.0f, -22410.5f, -23308.5f, -24195.0f, -25068.5f,
            -25926.5f, -26767.0f, -27589.0f, -28389.0f, -29166.5f, -29919.0f, -30644.5f, -31342.0f,
            -32009.5f, -32645.0f, -33247.0f, -33814.5f, -34346.0f, -34839.5f, -35295.0f, -35710.0f,
            -36084.5f, -36417.5f, -36707.5f, -36954.0f, -37156.5f, -37315.0f, -37428.0f, -37496.0f,
            37519.0f, 37496.0f, 37428.0f, 37315.0f, 37156.5f, 36954.0f, 36707.5f, 36417.5f,
            36084.5f, 35710.0f, 35295.0f, 34839.5f, 34346.0f, 33814.5f, 33247.0f, 32645.0f,
            32009.5f, 31342.0f, 30644.5f, 29919.0f, 29166.5f, 28389.0f, 27589.0f, 26767.0f,
            25926.5f, 25068.5f, 24195.0f, 23308.5f, 22410.5f, 21503.0f, 20588.0f, 19668.0f,
            18744.5f, 17820.0f, 16895.5f, 15973.5f, 15056.0f, 14144.5f, 13241.0f, 12347.0f,
            11464.5f, 10594.5f, 9739.0f, 8899.5f, 8077.5f, 7274.0f, 6490.0f, 5727.5f,
            4987.5f, 4270.0f, 3577.0f, 2909.0f, 2266.5f, 1650.0f, 1061.0f, 499.0f,
            -35.0f, -541.0f, -1018.5f, -1467.5f, -1888.0f, -2280.5f, -2644.0f, -2979.5f,
            3287.0f, 3567.0f, 3820.0f, 4046.0f, 4246.0f, 4420.0f, 4569.5f, 4694.5f,
            4796.0f, 4875.0f, 4931.5f, 4967.5f, 4983.0f, 4979.5f, 4958.0f, 4919.0f,
            4863.5f, 4792.5f, 4708.0f, 4609.5f, 4499.0f, 4377.5f, 4245.5f, 4104.5f,
            3955.0f, 3798.5f, 3635.5f, 3467.5f, 3294.5f, 3118.5f, 2939.5f, 2758.5f,
            2576.5f, 2394.0f, 2212.5f, 2031.5f, 1852.5f, 1675.5f, 1502.0f, 1331.5f,
            1165.0f, 1003.0f, 846.0f, 694.0f, 547.5f, 407.0f, 272.5f, 144.0f,
            22.5f, -92.5f, -201.0f, -302.5f, -397.0f, -485.0f, -565.5f, -640.0f,
            -707.0f, -767.5f, -822.0f, -869.5f, -911.0f, -946.5f, -976.0f, -1000.0f,
            1018.5f, 1031.5f, 1040.0f, 1043.5f, 1042.5f, 1037.5f, 1028.5f, 1016.0f,
            1000.5f, 981.0f, 959.5f, 935.0f, 908.5f, 879.5f, 849.0f, 817.0f,
            783.5f, 749.0f, 714.0f, 678.0f, 641.5f, 605.0f, 568.5f, 532.0f,
            495.5f, 459.5f, 424.0f, 389.5f, 355.5f, 322.5f, 290.5f, 259.5f,
            229.5f, 200.5f, 173.5f, 147.0f, 122.0f, 98.5f, 76.5f, 55.5f,
            36.0f, 18.0f, 1.0f, -14.5f, -28.5f, -41.5f, -53.0f, -63.5f,
            -73.0f, -81.5f, -88.5f, -94.5f, -100.0f, -104.0f, -107.5f, -110.5f,
            -112.0f, -113.5f, -114.0f, -114.0f, -113.5f, -112.5f, -111.0f, -109.0f,
            106.5f, 104.0f, 101.0f, 98.0f, 95.0f, 91.5f, 88.0f, 84.5f,
            80.5f, 77.0f, 73.5f, 69.5f, 66.0f, 62.5f, 58.5f, 55.5f,
            52.0f, 48.5f, 45.5f, 42.5f, 39.5f, 36.5f, 34.0f, 31.5f,
            29.0f, 26.5f, 24.5f, 22.5f, 20.5f, 19.0f, 17.5f, 15.5f,
            14.5f, 13.0f, 12.0f, 10.5f, 9.5f, 8.5f, 8.0f, 7.0f,
            6.5f, 5.5f, 5.0f, 4.5f, 4.0f, 3.5f, 3.5f, 3.0f,
            2.5f, 2.5f, 2.0f, 2.0f, 1.5f, 1.5f, 1.0f, 1.0f,
            1.0f, 1.0f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f
        };

        // ---- decoder state (it lives from frame to frame)
        private readonly float[] _d = new float[1024];
        private readonly float[][] _v = { new float[1024], new float[1024] };
        private readonly float[] _u = new float[32];
        private int _vPos;
        // ---- per frame
        private readonly int[][] _allocation = { new int[32], new int[32] };
        private readonly int[][] _scaleFactorInfo = { new int[32], new int[32] };
        private readonly int[][][] _scaleFactor = { NewTriples(), NewTriples() };
        private readonly float[][][] _sample = { NewSamples(), NewSamples() };
        // ---- the bit reader (over the frame being decoded)
        private byte[] _data;
        private int _base, _bitPos, _bitEnd;
        private bool _overrun;

        public Mp2Decoder()
        {
            Array.Copy(SynthesisWindow, 0, _d, 0, 512);
            Array.Copy(SynthesisWindow, 0, _d, 512, 512);
        }

        private static int[][] NewTriples()
        {
            var t = new int[32][];
            for (int i = 0; i < 32; i++) t[i] = new int[3];
            return t;
        }

        private static float[][] NewSamples()
        {
            var t = new float[32][];
            for (int i = 0; i < 32; i++) t[i] = new float[3];
            return t;
        }

        /// <summary>Header of the last frame that decoded.</summary>
        public Mp2Header LastHeader { get; private set; }
        /// <summary>Frames that decoded, and frames that turned out corrupt (their PCM is silence).</summary>
        public long FramesDecoded { get; private set; }
        public long FramesCorrupt { get; private set; }

        /// <summary>Forgets the filter history: a discontinuity in the stream (the next frame starts a new signal).</summary>
        public void Reset()
        {
            Array.Clear(_v[0], 0, 1024); Array.Clear(_v[1], 0, 1024); _vPos = 0;
        }

        /// <summary>
        /// Decodes the frame that starts at <paramref name="offset"/> (header first) into interleaved 16 bit PCM: <see cref="SamplesPerFrame"/> samples per channel, so
        /// <paramref name="pcm"/> needs room for 1152 * 2. Returns false, writing nothing, when the header is not a Layer II header or the data is shorter than the frame says; the frame is
        /// silence when it turns out to be corrupt (more bits needed than it has): the output is zeros, true is returned and <see cref="FramesCorrupt"/> counts it.
        /// </summary>
        public bool Decode(byte[] data, int offset, int length, short[] pcm, out int channels, out int sampleRate)
        {
            channels = 0; sampleRate = 0;
            Mp2Header h;
            if (!Mp2Header.TryParse(data, offset, length, out h)) return false;
            if (length < h.FrameBytes || offset + h.FrameBytes > data.Length || pcm == null || pcm.Length < SamplesPerFrame * h.Channels) return false;
            channels = h.Channels; sampleRate = h.SampleRate;
            LastHeader = h;
            _data = data; _base = offset; _bitPos = 32 + (h.HasCrc ? 16 : 0); _bitEnd = h.FrameBytes * 8; _overrun = false;
            DecodeFrame(h, pcm);
            _data = null;
            if (_overrun)
            {
                // a corrupt frame: its bits ran out before the audio did. Silence, and a clean filter, instead of a burst of noise
                FramesCorrupt++;
                Array.Clear(pcm, 0, SamplesPerFrame * h.Channels);
                Reset();
            }
            else FramesDecoded++;
            return true;
        }

        private int Read(int bits)
        {
            int v = 0;
            while (bits > 0)
            {
                if (_bitPos >= _bitEnd) { _overrun = true; return v << bits; }
                int inByte = _bitPos & 7, avail = 8 - inByte, take = bits < avail ? bits : avail;
                int b = (_data[_base + (_bitPos >> 3)] >> (avail - take)) & ((1 << take) - 1);
                v = (v << take) | b;
                _bitPos += take; bits -= take;
            }
            return v;
        }

        private int ReadAllocation(int sb, int tab3)
        {
            int tab4 = QuantLutStep3[tab3][sb];
            return QuantLutStep4[tab4 & 15][Read(tab4 >> 4)];
        }

        private void DecodeFrame(Mp2Header h, short[] pcm)
        {
            int channels = h.Channels;
            int tab3, sblimit;
            if (h.IsMpeg1)
            {
                int tab1 = h.Mode == 3 ? 0 : 1;
                int tab2 = QuantLutStep1[tab1][h.BitrateIndex];
                int srIndex = h.SampleRate == 44100 ? 0 : h.SampleRate == 48000 ? 1 : 2;
                tab3 = QuantLutStep2[tab2][srIndex];
                sblimit = tab3 & 63;
                tab3 >>= 6;
            }
            else { tab3 = 2; sblimit = 30; }

            int bound = h.Mode == 1 ? (h.ModeExtension + 1) << 2 : (h.Mode == 3 ? 0 : 32);
            if (bound > sblimit) bound = sblimit;
            int[] a0 = _allocation[0], a1 = _allocation[1];

            // allocation
            for (int sb = 0; sb < bound; sb++) { a0[sb] = ReadAllocation(sb, tab3); a1[sb] = ReadAllocation(sb, tab3); }
            for (int sb = bound; sb < sblimit; sb++) a0[sb] = a1[sb] = ReadAllocation(sb, tab3);
            for (int sb = sblimit; sb < 32; sb++) a0[sb] = a1[sb] = 0;

            // scale factor selection
            for (int sb = 0; sb < sblimit; sb++)
            {
                for (int ch = 0; ch < channels; ch++)
                    if (_allocation[ch][sb] != 0) _scaleFactorInfo[ch][sb] = Read(2);
                if (channels == 1) _scaleFactorInfo[1][sb] = _scaleFactorInfo[0][sb];
            }

            // scale factors
            for (int sb = 0; sb < sblimit; sb++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (_allocation[ch][sb] == 0) continue;
                    int[] sf = _scaleFactor[ch][sb];
                    switch (_scaleFactorInfo[ch][sb])
                    {
                        case 0: sf[0] = Read(6); sf[1] = Read(6); sf[2] = Read(6); break;
                        case 1: sf[0] = sf[1] = Read(6); sf[2] = Read(6); break;
                        case 2: sf[0] = sf[1] = sf[2] = Read(6); break;
                        case 3: sf[0] = Read(6); sf[1] = sf[2] = Read(6); break;
                    }
                }
                if (channels == 1) { int[] s0 = _scaleFactor[0][sb], s1 = _scaleFactor[1][sb]; s1[0] = s0[0]; s1[1] = s0[1]; s1[2] = s0[2]; }
            }

            // coefficients and synthesis
            int outPos = 0;
            for (int part = 0; part < 3; part++)
            {
                for (int granule = 0; granule < 4; granule++)
                {
                    for (int sb = 0; sb < bound; sb++)
                    {
                        ReadSamples(0, sb, part);
                        if (channels == 2) ReadSamples(1, sb, part);
                    }
                    for (int sb = bound; sb < sblimit; sb++) ReadSharedSamples(sb, part, channels == 2);
                    for (int sb = sblimit; sb < 32; sb++)
                    {
                        float[] s0 = _sample[0][sb], s1 = _sample[1][sb];
                        s0[0] = s0[1] = s0[2] = 0f; s1[0] = s1[1] = s1[2] = 0f;
                    }
                    if (_overrun) return;

                    for (int p = 0; p < 3; p++)
                    {
                        _vPos = (_vPos - 64) & 1023;                      // shifting step
                        for (int ch = 0; ch < channels; ch++)
                        {
                            float[] v = _v[ch];
                            Idct36(_sample[ch], p, v, _vPos);
                            Array.Clear(_u, 0, 32);                       // build U, window, calculate the output
                            int dIndex = 512 - (_vPos >> 1), vIndex = (_vPos % 128) >> 1;
                            while (vIndex < 1024)
                            {
                                for (int i = 0; i < 32; ++i) _u[i] += _d[dIndex++] * v[vIndex++];
                                vIndex += 128 - 32; dIndex += 64 - 32;
                            }
                            dIndex -= 512 - 32;
                            vIndex = (128 - 32 + 1024) - vIndex;
                            while (vIndex < 1024)
                            {
                                for (int i = 0; i < 32; ++i) _u[i] += _d[dIndex++] * v[vIndex++];
                                vIndex += 128 - 32; dIndex += 64 - 32;
                            }
                            for (int j = 0; j < 32; j++) pcm[(outPos + j) * channels + ch] = ToPcm16(_u[j] * OutputScale);
                        }
                        outPos += 32;
                    }
                }
            }
        }

        private static short ToPcm16(float x)
        {
            float y = x * 32768f;
            if (y >= 32767f) return 32767;                                  // clip first: a float this large does not convert to an int
            if (y <= -32768f) return -32768;
            if (float.IsNaN(y)) return 0;                                         // NaN (never, from finite input)
            return (short)((int)(y + 32768.5f) - 32768);                    // rounds to nearest, without a call
        }

        /// <summary>Reads the three codes of one subband (grouped in a single word for the quantizers that group them).</summary>
        private void ReadCodes(int q, out int c0, out int c1, out int c2)
        {
            int levels = QuantLevels[q];
            if (QuantGroup[q])
            {
                int val = Read(QuantBits[q]);                                     // three codes in one word
                c0 = val % levels; val /= levels;
                c1 = val % levels;
                c2 = val / levels;
            }
            else
            {
                int bits = QuantBits[q];
                c0 = Read(bits); c1 = Read(bits); c2 = Read(bits);
            }
        }

        /// <summary>
        /// Requantises three codes: the fraction of a code is (2 * code - (levels - 1)) / levels (ISO 11172-3, table 3-B.4: the factor C is 2^n / levels for the n bits of the code), times the scale
        /// factor, in the units of the synthesis window (65536). The sign is flipped, as in pl_mpeg; OutputScale takes it out. (pl_mpeg and kjmp2 divide by levels + 1 and do the rest in
        /// integers, which leaves the few-level quantizers up to 25 % too quiet and the rest about 2 LSB noisy.)
        /// </summary>
        private void Requantise(float[] sample, int q, int sf, int c0, int c1, int c2)
        {
            int levels = QuantLevels[q], mid = ((levels + 1) >> 1) - 1;
            float k = (sf == 63 ? 0f : ScaleFactors[sf]) * (65536f / levels);
            sample[0] = (mid - c0) * k;
            sample[1] = (mid - c1) * k;
            sample[2] = (mid - c2) * k;
        }

        private void ReadSamples(int ch, int sb, int part)
        {
            int q = _allocation[ch][sb];
            float[] sample = _sample[ch][sb];
            if (q == 0) { sample[0] = sample[1] = sample[2] = 0f; return; }       // no bits allocated for this subband
            int c0, c1, c2;
            ReadCodes(q, out c0, out c1, out c2);
            Requantise(sample, q, _scaleFactor[ch][sb][part], c0, c1, c2);
        }

        /// <summary>
        /// A subband above the joint stereo bound (intensity stereo): both channels share one set of codes, each with its own scale factor (the difference between the two scale factors is where the
        /// sound sits between the speakers). pl_mpeg copies the left channel's already scaled samples instead, which loses that.
        /// </summary>
        private void ReadSharedSamples(int sb, int part, bool bothChannels)
        {
            int q = _allocation[0][sb];
            if (q == 0) { _sample[0][sb][0] = _sample[0][sb][1] = _sample[0][sb][2] = 0f; if (bothChannels) _sample[1][sb][0] = _sample[1][sb][1] = _sample[1][sb][2] = 0f; return; }
            int c0, c1, c2;
            ReadCodes(q, out c0, out c1, out c2);
            Requantise(_sample[0][sb], q, _scaleFactor[0][sb][part], c0, c1, c2);
            if (bothChannels) Requantise(_sample[1][sb], q, _scaleFactor[1][sb][part], c0, c1, c2);
        }

        private static void Idct36(float[][] s, int ss, float[] d, int dp)
        {
            float t01, t02, t03, t04, t05, t06, t07, t08, t09, t10, t11, t12,
                t13, t14, t15, t16, t17, t18, t19, t20, t21, t22, t23, t24,
                t25, t26, t27, t28, t29, t30, t31, t32, t33;

            t01 = (float)(s[0][ss] + s[31][ss]); t02 = (float)(s[0][ss] - s[31][ss]) * 0.500602998235f;
            t03 = (float)(s[1][ss] + s[30][ss]); t04 = (float)(s[1][ss] - s[30][ss]) * 0.505470959898f;
            t05 = (float)(s[2][ss] + s[29][ss]); t06 = (float)(s[2][ss] - s[29][ss]) * 0.515447309923f;
            t07 = (float)(s[3][ss] + s[28][ss]); t08 = (float)(s[3][ss] - s[28][ss]) * 0.53104259109f;
            t09 = (float)(s[4][ss] + s[27][ss]); t10 = (float)(s[4][ss] - s[27][ss]) * 0.553103896034f;
            t11 = (float)(s[5][ss] + s[26][ss]); t12 = (float)(s[5][ss] - s[26][ss]) * 0.582934968206f;
            t13 = (float)(s[6][ss] + s[25][ss]); t14 = (float)(s[6][ss] - s[25][ss]) * 0.622504123036f;
            t15 = (float)(s[7][ss] + s[24][ss]); t16 = (float)(s[7][ss] - s[24][ss]) * 0.674808341455f;
            t17 = (float)(s[8][ss] + s[23][ss]); t18 = (float)(s[8][ss] - s[23][ss]) * 0.744536271002f;
            t19 = (float)(s[9][ss] + s[22][ss]); t20 = (float)(s[9][ss] - s[22][ss]) * 0.839349645416f;
            t21 = (float)(s[10][ss] + s[21][ss]); t22 = (float)(s[10][ss] - s[21][ss]) * 0.972568237862f;
            t23 = (float)(s[11][ss] + s[20][ss]); t24 = (float)(s[11][ss] - s[20][ss]) * 1.16943993343f;
            t25 = (float)(s[12][ss] + s[19][ss]); t26 = (float)(s[12][ss] - s[19][ss]) * 1.48416461631f;
            t27 = (float)(s[13][ss] + s[18][ss]); t28 = (float)(s[13][ss] - s[18][ss]) * 2.05778100995f;
            t29 = (float)(s[14][ss] + s[17][ss]); t30 = (float)(s[14][ss] - s[17][ss]) * 3.40760841847f;
            t31 = (float)(s[15][ss] + s[16][ss]); t32 = (float)(s[15][ss] - s[16][ss]) * 10.1900081235f;

            t33 = t01 + t31; t31 = (t01 - t31) * 0.502419286188f;
            t01 = t03 + t29; t29 = (t03 - t29) * 0.52249861494f;
            t03 = t05 + t27; t27 = (t05 - t27) * 0.566944034816f;
            t05 = t07 + t25; t25 = (t07 - t25) * 0.64682178336f;
            t07 = t09 + t23; t23 = (t09 - t23) * 0.788154623451f;
            t09 = t11 + t21; t21 = (t11 - t21) * 1.06067768599f;
            t11 = t13 + t19; t19 = (t13 - t19) * 1.72244709824f;
            t13 = t15 + t17; t17 = (t15 - t17) * 5.10114861869f;
            t15 = t33 + t13; t13 = (t33 - t13) * 0.509795579104f;
            t33 = t01 + t11; t01 = (t01 - t11) * 0.601344886935f;
            t11 = t03 + t09; t09 = (t03 - t09) * 0.899976223136f;
            t03 = t05 + t07; t07 = (t05 - t07) * 2.56291544774f;
            t05 = t15 + t03; t15 = (t15 - t03) * 0.541196100146f;
            t03 = t33 + t11; t11 = (t33 - t11) * 1.30656296488f;
            t33 = t05 + t03; t05 = (t05 - t03) * 0.707106781187f;
            t03 = t15 + t11; t15 = (t15 - t11) * 0.707106781187f;
            t03 += t15;
            t11 = t13 + t07; t13 = (t13 - t07) * 0.541196100146f;
            t07 = t01 + t09; t09 = (t01 - t09) * 1.30656296488f;
            t01 = t11 + t07; t07 = (t11 - t07) * 0.707106781187f;
            t11 = t13 + t09; t13 = (t13 - t09) * 0.707106781187f;
            t11 += t13; t01 += t11;
            t11 += t07; t07 += t13;
            t09 = t31 + t17; t31 = (t31 - t17) * 0.509795579104f;
            t17 = t29 + t19; t29 = (t29 - t19) * 0.601344886935f;
            t19 = t27 + t21; t21 = (t27 - t21) * 0.899976223136f;
            t27 = t25 + t23; t23 = (t25 - t23) * 2.56291544774f;
            t25 = t09 + t27; t09 = (t09 - t27) * 0.541196100146f;
            t27 = t17 + t19; t19 = (t17 - t19) * 1.30656296488f;
            t17 = t25 + t27; t27 = (t25 - t27) * 0.707106781187f;
            t25 = t09 + t19; t19 = (t09 - t19) * 0.707106781187f;
            t25 += t19;
            t09 = t31 + t23; t31 = (t31 - t23) * 0.541196100146f;
            t23 = t29 + t21; t21 = (t29 - t21) * 1.30656296488f;
            t29 = t09 + t23; t23 = (t09 - t23) * 0.707106781187f;
            t09 = t31 + t21; t31 = (t31 - t21) * 0.707106781187f;
            t09 += t31;    t29 += t09;    t09 += t23;    t23 += t31;
            t17 += t29;    t29 += t25;    t25 += t09;    t09 += t27;
            t27 += t23;    t23 += t19; t19 += t31;
            t21 = t02 + t32; t02 = (t02 - t32) * 0.502419286188f;
            t32 = t04 + t30; t04 = (t04 - t30) * 0.52249861494f;
            t30 = t06 + t28; t28 = (t06 - t28) * 0.566944034816f;
            t06 = t08 + t26; t08 = (t08 - t26) * 0.64682178336f;
            t26 = t10 + t24; t10 = (t10 - t24) * 0.788154623451f;
            t24 = t12 + t22; t22 = (t12 - t22) * 1.06067768599f;
            t12 = t14 + t20; t20 = (t14 - t20) * 1.72244709824f;
            t14 = t16 + t18; t16 = (t16 - t18) * 5.10114861869f;
            t18 = t21 + t14; t14 = (t21 - t14) * 0.509795579104f;
            t21 = t32 + t12; t32 = (t32 - t12) * 0.601344886935f;
            t12 = t30 + t24; t24 = (t30 - t24) * 0.899976223136f;
            t30 = t06 + t26; t26 = (t06 - t26) * 2.56291544774f;
            t06 = t18 + t30; t18 = (t18 - t30) * 0.541196100146f;
            t30 = t21 + t12; t12 = (t21 - t12) * 1.30656296488f;
            t21 = t06 + t30; t30 = (t06 - t30) * 0.707106781187f;
            t06 = t18 + t12; t12 = (t18 - t12) * 0.707106781187f;
            t06 += t12;
            t18 = t14 + t26; t26 = (t14 - t26) * 0.541196100146f;
            t14 = t32 + t24; t24 = (t32 - t24) * 1.30656296488f;
            t32 = t18 + t14; t14 = (t18 - t14) * 0.707106781187f;
            t18 = t26 + t24; t24 = (t26 - t24) * 0.707106781187f;
            t18 += t24; t32 += t18;
            t18 += t14; t26 = t14 + t24;
            t14 = t02 + t16; t02 = (t02 - t16) * 0.509795579104f;
            t16 = t04 + t20; t04 = (t04 - t20) * 0.601344886935f;
            t20 = t28 + t22; t22 = (t28 - t22) * 0.899976223136f;
            t28 = t08 + t10; t10 = (t08 - t10) * 2.56291544774f;
            t08 = t14 + t28; t14 = (t14 - t28) * 0.541196100146f;
            t28 = t16 + t20; t20 = (t16 - t20) * 1.30656296488f;
            t16 = t08 + t28; t28 = (t08 - t28) * 0.707106781187f;
            t08 = t14 + t20; t20 = (t14 - t20) * 0.707106781187f;
            t08 += t20;
            t14 = t02 + t10; t02 = (t02 - t10) * 0.541196100146f;
            t10 = t04 + t22; t22 = (t04 - t22) * 1.30656296488f;
            t04 = t14 + t10; t10 = (t14 - t10) * 0.707106781187f;
            t14 = t02 + t22; t02 = (t02 - t22) * 0.707106781187f;
            t14 += t02;    t04 += t14;    t14 += t10;    t10 += t02;
            t16 += t04;    t04 += t08;    t08 += t14;    t14 += t28;
            t28 += t10;    t10 += t20;    t20 += t02;    t21 += t16;
            t16 += t32;    t32 += t04;    t04 += t06;    t06 += t08;
            t08 += t18;    t18 += t14;    t14 += t30;    t30 += t28;
            t28 += t26;    t26 += t10;    t10 += t12;    t12 += t20;
            t20 += t24;    t24 += t02;

            d[dp + 48] = -t33;
            d[dp + 49] = d[dp + 47] = -t21;
            d[dp + 50] = d[dp + 46] = -t17;
            d[dp + 51] = d[dp + 45] = -t16;
            d[dp + 52] = d[dp + 44] = -t01;
            d[dp + 53] = d[dp + 43] = -t32;
            d[dp + 54] = d[dp + 42] = -t29;
            d[dp + 55] = d[dp + 41] = -t04;
            d[dp + 56] = d[dp + 40] = -t03;
            d[dp + 57] = d[dp + 39] = -t06;
            d[dp + 58] = d[dp + 38] = -t25;
            d[dp + 59] = d[dp + 37] = -t08;
            d[dp + 60] = d[dp + 36] = -t11;
            d[dp + 61] = d[dp + 35] = -t18;
            d[dp + 62] = d[dp + 34] = -t09;
            d[dp + 63] = d[dp + 33] = -t14;
            d[dp + 32] = -t05;
            d[dp + 0] = t05; d[dp + 31] = -t30;
            d[dp + 1] = t30; d[dp + 30] = -t27;
            d[dp + 2] = t27; d[dp + 29] = -t28;
            d[dp + 3] = t28; d[dp + 28] = -t07;
            d[dp + 4] = t07; d[dp + 27] = -t26;
            d[dp + 5] = t26; d[dp + 26] = -t23;
            d[dp + 6] = t23; d[dp + 25] = -t10;
            d[dp + 7] = t10; d[dp + 24] = -t15;
            d[dp + 8] = t15; d[dp + 23] = -t12;
            d[dp + 9] = t12; d[dp + 22] = -t19;
            d[dp + 10] = t19; d[dp + 21] = -t20;
            d[dp + 11] = t20; d[dp + 20] = -t13;
            d[dp + 12] = t13; d[dp + 19] = -t24;
            d[dp + 13] = t24; d[dp + 18] = -t31;
            d[dp + 14] = t31; d[dp + 17] = -t02;
            d[dp + 15] = t02; d[dp + 16] = 0.0f;
        }

    }
}
