using System;
using System.Globalization;

namespace AceEngine.Cast
{
    /// <summary>What a H.264 sequence parameter set says about the picture (only what the Cast compatibility check needs).</summary>
    public sealed class H264Sps
    {
        public int ProfileIdc, ConstraintFlags, LevelIdc;
        public int ChromaFormatIdc = 1, BitDepthLuma = 8, BitDepthChroma = 8;
        /// <summary>Picture size after cropping.</summary>
        public int Width, Height;
        /// <summary>false = interlaced coding (fields or MBAFF).</summary>
        public bool FrameMbsOnly = true;
        /// <summary>Frames per second from the VUI timing info, 0 if the stream does not say.</summary>
        public double Fps;

        /// <summary>Level as a number (4.1, 3.0...), with the level 1b special case folded to 1.0.</summary>
        public double Level { get { return LevelIdc / 10.0; } }

        public int WidthInMbs { get { return (Width + 15) / 16; } }
        public int HeightInMbs { get { return (Height + 15) / 16; } }

        public string ProfileName
        {
            get
            {
                switch (ProfileIdc)
                {
                    case 66: return "Baseline";
                    case 77: return "Main";
                    case 88: return "Extended";
                    case 100: return "High";
                    case 110: return "High 10";
                    case 122: return "High 4:2:2";
                    case 244: return "High 4:4:4";
                    case 44: return "CAVLC 4:4:4";
                    case 83: case 86: return "Scalable";
                    case 118: case 128: case 138: case 139: case 134: return "Multiview";
                    default: return "profile " + ProfileIdc.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        public override string ToString()
        {
            return "H.264 " + ProfileName + "@" + Level.ToString("0.0", CultureInfo.InvariantCulture) + " " + Width + "x" + Height + (FrameMbsOnly ? "p" : "i")
                + (Fps > 0 ? " " + Fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps" : "");
        }

        /// <summary>Parses an SPS NAL unit: nal[off] is the NAL header byte (type 7) and the unit has 'len' bytes. false if it is truncated or inconsistent; never throws.</summary>
        public static bool TryParse(byte[] nal, int off, int len, out H264Sps sps)
        {
            sps = null;
            if (nal == null || off < 0 || len < 5 || off + len > nal.Length) return false;
            if ((nal[off] & 0x1F) != 7) return false;
            // RBSP: emulation prevention bytes (00 00 03) removed
            var r = new byte[len - 1];
            int n = 0, zeros = 0;
            for (int i = off + 1; i < off + len; i++)
            {
                byte b = nal[i];
                if (zeros >= 2 && b == 3) { zeros = 0; continue; }
                r[n++] = b;
                zeros = b == 0 ? zeros + 1 : 0;
            }
            var br = new BitReader(r, n);
            var s = new H264Sps();
            s.ProfileIdc = (int)br.Bits(8);
            s.ConstraintFlags = (int)br.Bits(8);
            s.LevelIdc = (int)br.Bits(8);
            br.Ue();                                                                         // seq_parameter_set_id
            int chromaArray = 1;
            switch (s.ProfileIdc)
            {
                case 100: case 110: case 122: case 244: case 44: case 83: case 86: case 118: case 128: case 138: case 139: case 134: case 135:
                    s.ChromaFormatIdc = (int)br.Ue();
                    if (s.ChromaFormatIdc > 3) return false;
                    chromaArray = s.ChromaFormatIdc;
                    if (s.ChromaFormatIdc == 3 && br.Bit() == 1) chromaArray = 0;            // separate_colour_plane_flag
                    s.BitDepthLuma = 8 + (int)br.Ue();
                    s.BitDepthChroma = 8 + (int)br.Ue();
                    if (s.BitDepthLuma > 14 || s.BitDepthChroma > 14) return false;
                    br.Bit();                                                                // qpprime_y_zero_transform_bypass_flag
                    if (br.Bit() == 1)                                                       // seq_scaling_matrix_present_flag
                    {
                        int lists = s.ChromaFormatIdc != 3 ? 8 : 12;
                        for (int i = 0; i < lists; i++)
                            if (br.Bit() == 1) SkipScalingList(br, i < 6 ? 16 : 64);
                    }
                    break;
            }
            br.Ue();                                                                         // log2_max_frame_num_minus4
            uint pocType = br.Ue();
            if (pocType == 0) br.Ue();
            else if (pocType == 1)
            {
                br.Bit(); br.Se(); br.Se();
                uint cycle = br.Ue();
                if (cycle > 255) return false;
                for (uint i = 0; i < cycle; i++) br.Se();
            }
            else if (pocType != 2) return false;
            br.Ue();                                                                         // max_num_ref_frames
            br.Bit();                                                                        // gaps_in_frame_num_value_allowed_flag
            uint wMbs = br.Ue() + 1;
            uint hMapUnits = br.Ue() + 1;
            s.FrameMbsOnly = br.Bit() == 1;
            if (!s.FrameMbsOnly) br.Bit();                                                   // mb_adaptive_frame_field_flag
            br.Bit();                                                                        // direct_8x8_inference_flag
            if (br.Failed || wMbs == 0 || wMbs > 1024 || hMapUnits > 1024) return false;
            uint hMbs = (uint)(s.FrameMbsOnly ? 1 : 2) * hMapUnits;
            long w = wMbs * 16, h = hMbs * 16;
            if (br.Bit() == 1)                                                               // frame_cropping_flag
            {
                uint l = br.Ue(), rr = br.Ue(), t = br.Ue(), b = br.Ue();
                int subW = chromaArray == 1 || chromaArray == 2 ? 2 : 1;
                int subH = chromaArray == 1 ? 2 : 1;
                long cropX = chromaArray == 0 ? 1 : subW;
                long cropY = (chromaArray == 0 ? 1 : subH) * (s.FrameMbsOnly ? 1 : 2);
                w -= cropX * ((long)l + rr);
                h -= cropY * ((long)t + b);
            }
            if (br.Failed || w <= 0 || h <= 0) return false;
            s.Width = (int)w; s.Height = (int)h;
            if (br.Bit() == 1) ParseVui(br, s);                                              // vui_parameters_present_flag
            // a failure inside the VUI only costs the frame rate: the picture parameters before it were read fine
            if (br.Failed && s.Fps == 0) { /* keep what we have */ }
            sps = s;
            return true;
        }

        private static void SkipScalingList(BitReader br, int size)
        {
            int last = 8, next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0)
                {
                    int delta = br.Se();
                    next = (last + delta + 256) % 256;
                }
                last = next == 0 ? last : next;
                if (br.Failed) return;
            }
        }

        private static void ParseVui(BitReader br, H264Sps s)
        {
            if (br.Bit() == 1)                                                               // aspect_ratio_info_present_flag
            {
                if (br.Bits(8) == 255) br.Bits(32);                                          // Extended_SAR
            }
            if (br.Bit() == 1) br.Bit();                                                     // overscan
            if (br.Bit() == 1)                                                               // video_signal_type_present_flag
            {
                br.Bits(3); br.Bit();
                if (br.Bit() == 1) br.Bits(24);                                              // colour description
            }
            if (br.Bit() == 1) { br.Ue(); br.Ue(); }                                         // chroma_loc_info
            if (br.Bit() == 1)                                                               // timing_info_present_flag
            {
                uint units = br.Bits(32), scale = br.Bits(32);
                br.Bit();                                                                    // fixed_frame_rate_flag
                if (!br.Failed && units > 0 && scale > 0) s.Fps = scale / (2.0 * units);
            }
        }

        private sealed class BitReader
        {
            private readonly byte[] _d;
            private readonly int _bits;
            private int _pos;
            public bool Failed;

            public BitReader(byte[] d, int len) { _d = d; _bits = len * 8; }

            public uint Bit()
            {
                if (_pos >= _bits) { Failed = true; return 0; }
                uint v = (uint)((_d[_pos >> 3] >> (7 - (_pos & 7))) & 1);
                _pos++;
                return v;
            }

            public uint Bits(int n)
            {
                uint v = 0;
                for (int i = 0; i < n; i++) v = (v << 1) | Bit();
                return v;
            }

            public uint Ue()
            {
                int zeros = 0;
                while (Bit() == 0)
                {
                    if (Failed || ++zeros > 32) { Failed = true; return 0; }
                }
                if (zeros == 0) return 0;
                if (zeros == 32) { Failed = true; return 0; }
                return (1u << zeros) - 1 + Bits(zeros);
            }

            public int Se()
            {
                uint k = Ue();
                int v = (int)((k + 1) >> 1);
                return (k & 1) != 0 ? v : -v;
            }
        }
    }

    /// <summary>Header of an AAC frame in ADTS (ISO 13818-7) form.</summary>
    public sealed class AdtsHeader
    {
        private static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0 };
        /// <summary>Audio object type (2 = AAC-LC, 1 = Main, 3 = SSR, 4 = LTP).</summary>
        public int ObjectType;
        public int SampleRate, Channels, FrameLength;

        public string ProfileName
        {
            get { return ObjectType == 2 ? "AAC-LC" : ObjectType == 1 ? "AAC Main" : ObjectType == 3 ? "AAC SSR" : ObjectType == 4 ? "AAC LTP" : "AAC"; }
        }

        public override string ToString() { return ProfileName + " " + SampleRate + " Hz " + Channels + " ch (ADTS)"; }

        /// <summary>true if d[off..] starts with a plausible ADTS header.</summary>
        public static bool TryParse(byte[] d, int off, int end, out AdtsHeader h)
        {
            h = null;
            if (off < 0 || off + 7 > end || off + 7 > d.Length) return false;
            if (d[off] != 0xFF || (d[off + 1] & 0xF6) != 0xF0) return false;                // syncword and layer 00
            int rateIdx = (d[off + 2] >> 2) & 0xF;
            int rate = Rates[rateIdx];
            if (rate == 0) return false;
            int ch = ((d[off + 2] & 1) << 2) | (d[off + 3] >> 6);
            int frameLen = ((d[off + 3] & 3) << 11) | (d[off + 4] << 3) | (d[off + 5] >> 5);
            if (frameLen < 7) return false;
            h = new AdtsHeader { ObjectType = ((d[off + 2] >> 6) & 3) + 1, SampleRate = rate, Channels = ch == 7 ? 8 : ch, FrameLength = frameLen };
            return true;
        }
    }

    /// <summary>Header of an MPEG-1/2 audio frame (Layer I, II or III).</summary>
    public sealed class MpegAudioHeader
    {
        /// <summary>1, 2 or 3.</summary>
        public int Layer;
        public int SampleRate, Channels;

        public override string ToString() { return Layer == 3 ? "MP3" : Layer == 2 ? "MP2" : "MPEG audio layer " + Layer; }

        public static bool TryParse(byte[] d, int off, int end, out MpegAudioHeader h)
        {
            h = null;
            if (off < 0 || off + 4 > end || off + 4 > d.Length) return false;
            if (d[off] != 0xFF || (d[off + 1] & 0xE0) != 0xE0) return false;
            int version = (d[off + 1] >> 3) & 3;                                             // 0 = 2.5, 2 = MPEG-2, 3 = MPEG-1
            int layerBits = (d[off + 1] >> 1) & 3;                                           // 1 = III, 2 = II, 3 = I
            if (version == 1 || layerBits == 0) return false;
            int bitrateIdx = d[off + 2] >> 4;
            int rateIdx = (d[off + 2] >> 2) & 3;
            if (bitrateIdx == 0xF || rateIdx == 3) return false;
            int[] baseRates = { 44100, 48000, 32000 };
            int rate = baseRates[rateIdx] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            h = new MpegAudioHeader { Layer = 4 - layerBits, SampleRate = rate, Channels = ((d[off + 3] >> 6) & 3) == 3 ? 1 : 2 };
            return true;
        }
    }
}
