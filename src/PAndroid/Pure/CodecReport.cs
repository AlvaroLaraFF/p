using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PAndroid
{
    /// <summary>One entry of MediaCodecList, as plain values so the report is testable on the PC.</summary>
    public sealed class CodecEntry
    {
        public string Name = "";
        public bool IsEncoder;
        /// <summary>MediaCodecInfo.isHardwareAccelerated (API 29 and later).</summary>
        public bool IsHardware;
        /// <summary>MediaCodecInfo.isSoftwareOnly (API 29 and later).</summary>
        public bool IsSoftwareOnly;
        /// <summary>The report says whether IsHardware / IsSoftwareOnly are real answers (API 29 and later) or guesses from the name.</summary>
        public bool FlagsKnown;
        public string[] Types = new string[0];
        /// <summary>For a video decoder: "3840x2160" and the number of instances at once, if the system says (0 = unknown).</summary>
        public string MaxSize = "";
        public int MaxInstances;

        public bool Supports(string mime)
        {
            foreach (var t in Types) if (string.Equals(t, mime, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// What the device can decode, from MediaCodecList: the startup log lines that tell, on the device itself, which video and audio formats it has a decoder for (the question behind "does this
    /// channel have sound": MP2, AC-3 and E-AC-3 are not required of any device), and the yes or no the player's choices depend on.
    /// </summary>
    public static class CodecReport
    {
        /// <summary>The formats worth a line of their own, with the name the interface and the log use.</summary>
        public static readonly string[][] Interesting =
        {
            new[] { "video/avc", "H.264" }, new[] { "video/hevc", "H.265" }, new[] { "video/mpeg2", "MPEG-2 video" }, new[] { "video/mp4v-es", "MPEG-4" },
            new[] { "video/x-vnd.on2.vp9", "VP9" }, new[] { "video/av01", "AV1" },
            new[] { "audio/mp4a-latm", "AAC" }, new[] { "audio/mpeg", "MP3" }, new[] { "audio/mpeg-L2", "MP2" }, new[] { "audio/mpeg-L1", "MP1" }, new[] { "audio/ac3", "AC-3" },
            new[] { "audio/eac3", "E-AC-3" }, new[] { "audio/eac3-joc", "E-AC-3 JOC (Atmos)" }, new[] { "audio/vnd.dts", "DTS" }, new[] { "audio/opus", "Opus" }, new[] { "audio/raw", "PCM" },
        };

        public const string Mp2 = "audio/mpeg-L2", Ac3 = "audio/ac3", Eac3 = "audio/eac3", Aac = "audio/mp4a-latm", Avc = "video/avc", Hevc = "video/hevc", Mpeg2Video = "video/mpeg2";

        /// <summary>true if some decoder (not an encoder) of the list handles the format.</summary>
        public static bool HasDecoder(IEnumerable<CodecEntry> entries, string mime)
        {
            return entries != null && entries.Any(e => !e.IsEncoder && e.Supports(mime));
        }

        public static IList<CodecEntry> DecodersOf(IEnumerable<CodecEntry> entries, string mime)
        {
            return entries == null ? new List<CodecEntry>() : entries.Where(e => !e.IsEncoder && e.Supports(mime)).ToList();
        }

        /// <summary>Every software decoder is "sw", every hardware one "hw"; where the system does not say (before API 29) a name that starts with OMX.google. or c2.android. is software.</summary>
        public static string Kind(CodecEntry e)
        {
            if (e.FlagsKnown) return e.IsHardware ? "hw" : e.IsSoftwareOnly ? "sw" : "?";
            return IsSoftwareName(e.Name) ? "sw" : "hw?";
        }

        public static bool IsSoftwareName(string name)
        {
            return name != null && (name.StartsWith("OMX.google.", StringComparison.Ordinal) || name.StartsWith("c2.android.", StringComparison.Ordinal) || name.StartsWith("c2.google.", StringComparison.Ordinal));
        }

        /// <summary>The log lines: a count, one line for each format worth knowing about, and the names of every decoder.</summary>
        public static IList<string> Lines(IEnumerable<CodecEntry> entries)
        {
            var all = entries == null ? new List<CodecEntry>() : entries.ToList();
            var lines = new List<string>();
            var decoders = all.Where(e => !e.IsEncoder).ToList();
            int video = decoders.Count(e => e.Types.Any(t => t.StartsWith("video/", StringComparison.OrdinalIgnoreCase)));
            int audio = decoders.Count(e => e.Types.Any(t => t.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)));
            lines.Add("MediaCodecList: " + decoders.Count + " decoders (" + video + " video, " + audio + " audio), " + (all.Count - decoders.Count) + " encoders");
            foreach (var f in Interesting)
            {
                var list = DecodersOf(all, f[0]);
                var sb = new StringBuilder();
                sb.Append("decoder ").Append(f[0]).Append(" [").Append(f[1]).Append("]: ");
                if (list.Count == 0) sb.Append("NONE");
                else
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var e = list[i];
                        sb.Append(e.Name).Append(" (").Append(Kind(e));
                        if (e.MaxSize.Length > 0) sb.Append(", up to ").Append(e.MaxSize);
                        if (e.MaxInstances > 0) sb.Append(", ").Append(e.MaxInstances).Append(e.MaxInstances == 1 ? " instance" : " instances");
                        sb.Append(')');
                    }
                lines.Add(sb.ToString());
            }
            var names = decoders.Select(e => e.Name + "[" + string.Join(",", e.Types) + "]").ToList();
            lines.Add("all decoders: " + (names.Count == 0 ? "(none)" : string.Join(" ", names)));
            return lines;
        }

        /// <summary>One line with the verdict on the formats TV channels use, for a quick look at the top of a log.</summary>
        public static string Verdict(IEnumerable<CodecEntry> entries)
        {
            var all = entries == null ? new List<CodecEntry>() : entries.ToList();
            Func<string, string> yn = m => HasDecoder(all, m) ? "yes" : "NO";
            return "can decode: H.264 " + yn(Avc) + ", H.265 " + yn(Hevc) + ", MPEG-2 video " + yn(Mpeg2Video) + ", AAC " + yn(Aac) + ", MP2 " + yn(Mp2) + " (software MP2 " + (HasDecoder(all, Mp2) ? "only if forced" : "will be used") +
                "), AC-3 " + yn(Ac3) + ", E-AC-3 " + yn(Eac3);
        }
    }

    /// <summary>When the app decodes MP2 itself (see Mp2Decoder).</summary>
    public static class AudioFallbackPolicy
    {
        /// <param name="mode">The "softwareAudio" option.</param>
        /// <param name="deviceHasMp2Decoder">MediaCodecList has an "audio/mpeg-L2" decoder.</param>
        /// <param name="deviceDecoderFailed">The device's own MP2 decoder was tried and failed on this run (it initialised badly or died while decoding).</param>
        public static bool UseSoftwareMp2(SoftwareAudioMode mode, bool deviceHasMp2Decoder, bool deviceDecoderFailed)
        {
            switch (mode)
            {
                case SoftwareAudioMode.Off: return false;
                case SoftwareAudioMode.Force: return true;
                default: return !deviceHasMp2Decoder || deviceDecoderFailed;
            }
        }
    }
}
