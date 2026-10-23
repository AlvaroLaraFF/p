using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AceEngine.Cast
{
    /// <summary>What a Cast receiver can decode inside an HLS transport stream. The default receiver of Google Cast does the decoding, so the limits are the device's.</summary>
    public sealed class CastProfile
    {
        public string Id = "chromecast2";
        public string Name = "Chromecast (2nd gen.)";
        /// <summary>Largest picture, in pixels (1920x1080).</summary>
        public int MaxWidth = 1920, MaxHeight = 1080;
        /// <summary>Highest H.264 level_idc the device is documented to decode (41 = 4.1).</summary>
        public int MaxH264Level = 41;
        /// <summary>Highest macroblocks per second (level 4.1: 245760 = 1080p30). 0 = derive it from the level.</summary>
        public int MaxMacroblocksPerSecond = 245760;
        /// <summary>AC-3 / E-AC-3 only work as passthrough to a TV or receiver that decodes them: not reliable, so off.</summary>
        public bool AllowAc3 = false;

        /// <summary>The user's 2015 Chromecast: H.264 High up to level 4.1 (1080p30) and AAC; nothing else works in HLS.</summary>
        public static CastProfile Chromecast2
        {
            get { return new CastProfile(); }
        }

        /// <summary>Chromecast 3rd gen: H.264 High up to level 4.2 (1080p60).</summary>
        public static CastProfile Chromecast3
        {
            get { return new CastProfile { Id = "chromecast3", Name = "Chromecast (3rd gen.)", MaxH264Level = 42, MaxMacroblocksPerSecond = 522240 }; }
        }

        /// <summary>Chromecast with Google TV / Google TV Streamer: H.264 up to level 5.1 and 4K. HEVC does not work in TS even there.</summary>
        public static CastProfile GoogleTv
        {
            get { return new CastProfile { Id = "googletv", Name = "Chromecast with Google TV", MaxWidth = 3840, MaxHeight = 2160, MaxH264Level = 51, MaxMacroblocksPerSecond = 983040 }; }
        }

        public CastProfile Clone() { return (CastProfile)MemberwiseClone(); }
    }

    public enum CastSupport
    {
        /// <summary>Not enough has been read from the stream to tell.</summary>
        Unknown,
        Compatible,
        Incompatible
    }

    public enum CastIssueCode
    {
        NoProgram, Scrambled, NoVideo, VideoMpeg2, VideoHevc, VideoUnsupported,
        H264Profile, H264Format, H264Resolution, H264Level, H264Interlaced, SpsUnknown,
        NoAudio, AudioMp2, AudioAc3, AudioLatm, AudioDts, AudioUnsupported, AudioMismatch, MultiProgram,
        NoKeyframes, SourceEnded, Other
    }

    /// <summary>A reason why a channel cannot (Blocking) or may not (not Blocking) be sent to a Chromecast.</summary>
    public sealed class CastIssue
    {
        public CastIssueCode Code;
        public bool Blocking;
        /// <summary>Short sentence for the user (Spanish: it is shown in the interface), e.g. "audio MP2".</summary>
        public string Message = "";
        /// <summary>Technical detail for the log (English).</summary>
        public string Detail = "";

        public override string ToString() { return (Blocking ? "[x] " : "[!] ") + Code + ": " + Message + (Detail.Length > 0 ? " (" + Detail + ")" : ""); }
    }

    /// <summary>The answer of the compatibility check for one channel and one profile.</summary>
    public sealed class CastVerdict
    {
        public CastSupport Support = CastSupport.Unknown;
        public CastProfile Profile;
        public readonly List<CastIssue> Issues = new List<CastIssue>();
        /// <summary>"H.264 High@4.0 1920x1080p 25 fps" or "" if unknown.</summary>
        public string VideoDescription = "";
        /// <summary>"AAC-LC 48000 Hz 2 ch (ADTS)" or "" if unknown.</summary>
        public string AudioDescription = "";
        /// <summary>The program, for tooling.</summary>
        public StreamInfo Stream;

        public bool IsCompatible { get { return Support == CastSupport.Compatible; } }
        public bool IsIncompatible { get { return Support == CastSupport.Incompatible; } }

        public IEnumerable<CastIssue> Blocking { get { foreach (var i in Issues) if (i.Blocking) yield return i; } }
        public IEnumerable<CastIssue> Warnings { get { foreach (var i in Issues) if (!i.Blocking) yield return i; } }

        /// <summary>Why it cannot be sent, in one sentence for the user ("audio MP2" / "audio MP2 y vídeo H.265"); "" if it can.</summary>
        public string Reason
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var i in Blocking)
                {
                    if (sb.Length > 0) sb.Append(" y ");
                    sb.Append(i.Message);
                }
                return sb.ToString();
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Support);
            if (VideoDescription.Length > 0) sb.Append("; ").Append(VideoDescription);
            if (AudioDescription.Length > 0) sb.Append("; ").Append(AudioDescription);
            foreach (var i in Issues) sb.Append("; ").Append(i.ToString());
            return sb.ToString();
        }

        /// <summary>Short tag for CSV and logs: "ok", "h264+aac-latm"...</summary>
        public string Summary
        {
            get
            {
                var s = Stream;
                if (s == null || s.Program == null) return "no-program";
                string v = s.Video == null ? "novideo" : KindName(s.Video.Kind);
                string a = s.Audio == null ? "noaudio" : KindName(s.Audio.Kind);
                return v + "+" + a;
            }
        }

        internal static string KindName(EsKind k)
        {
            switch (k)
            {
                case EsKind.Mpeg1Video: return "mpeg1";
                case EsKind.Mpeg2Video: return "mpeg2";
                case EsKind.H264: return "h264";
                case EsKind.Hevc: return "hevc";
                case EsKind.AacAdts: return "aac-adts";
                case EsKind.AacLatm: return "aac-latm";
                case EsKind.MpegAudio: return "mpeg-audio";
                case EsKind.Ac3: return "ac3";
                case EsKind.Eac3: return "eac3";
                case EsKind.Dts: return "dts";
                default: return k.ToString().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Decides whether a channel can be sent to a Chromecast, from the PMT, the H.264 SPS and the audio frame header. Pure functions: the same input gives the same verdict.
    /// </summary>
    public static class CastCompatibility
    {
        /// <summary>Which audio stream of the program the HLS output carries: ADTS AAC first, then MP3, then LATM AAC, then the rest in PMT order. Null if there is none.</summary>
        internal static EsInfo ChooseAudio(ProgramInfo prog)
        {
            if (prog == null) return null;
            EsInfo best = null;
            int bestRank = int.MaxValue;
            foreach (var es in prog.AudioStreams)
            {
                int rank = es.Kind == EsKind.AacAdts ? 0 : es.Kind == EsKind.MpegAudio ? 1 : es.Kind == EsKind.AacLatm ? 2 : 3;
                if (rank < bestRank) { best = es; bestRank = rank; }
            }
            return best;
        }

        /// <summary>Verdict from what has been read of the stream so far. 'final' = nothing more will arrive: details still missing are judged as they are instead of waiting.</summary>
        public static CastVerdict Evaluate(StreamInfo info, CastProfile profile, bool final)
        {
            if (profile == null) profile = CastProfile.Chromecast2;
            var v = new CastVerdict { Profile = profile, Stream = info };
            if (info == null || info.Program == null)
            {
                v.Support = CastSupport.Unknown;
                if (final) Add(v, CastIssueCode.NoProgram, true, "no se ha podido leer el programa de la emisión", "no PMT was found in the stream");
                if (final) v.Support = CastSupport.Incompatible;
                return v;
            }

            var prog = info.Program;
            if (info.ProgramCount > 1) Add(v, CastIssueCode.MultiProgram, false, "la emisión trae varios programas", "PAT lists " + info.ProgramCount + " programs; the first one is used");
            if (info.Scrambled) Add(v, CastIssueCode.Scrambled, true, "emisión cifrada", "transport_scrambling_control set on the video or audio PID");

            EvaluateVideo(v, info, profile, final);
            EvaluateAudio(v, info, profile, final);

            bool blocked = false;
            foreach (var i in v.Issues) if (i.Blocking) blocked = true;
            if (blocked) v.Support = CastSupport.Incompatible;
            else if (info.IsComplete || final) v.Support = CastSupport.Compatible;
            else v.Support = CastSupport.Unknown;
            return v;
        }

        private static void Add(CastVerdict v, CastIssueCode code, bool blocking, string message, string detail)
        {
            v.Issues.Add(new CastIssue { Code = code, Blocking = blocking, Message = message, Detail = detail });
        }

        private static void EvaluateVideo(CastVerdict v, StreamInfo info, CastProfile profile, bool final)
        {
            var video = info.Video;
            if (video == null) { Add(v, CastIssueCode.NoVideo, true, "no tiene vídeo", "the PMT declares no video stream"); return; }
            switch (video.Kind)
            {
                case EsKind.Mpeg2Video:
                case EsKind.Mpeg1Video:
                    v.VideoDescription = "MPEG-2 video";
                    Add(v, CastIssueCode.VideoMpeg2, true, "vídeo MPEG-2", "the Chromecast does not decode MPEG-2 video in HLS");
                    return;
                case EsKind.Hevc:
                    v.VideoDescription = "H.265 (HEVC)";
                    Add(v, CastIssueCode.VideoHevc, true, "vídeo H.265", "HEVC is not supported inside MPEG-TS by the Cast receiver");
                    return;
                case EsKind.H264:
                    break;
                default:
                    v.VideoDescription = "stream type 0x" + video.StreamType.ToString("x2", CultureInfo.InvariantCulture);
                    Add(v, CastIssueCode.VideoUnsupported, true, "códec de vídeo no soportado", "video stream_type 0x" + video.StreamType.ToString("x2", CultureInfo.InvariantCulture));
                    return;
            }
            var sps = info.Sps;
            if (sps == null)
            {
                v.VideoDescription = "H.264";
                if (final) Add(v, CastIssueCode.SpsUnknown, false, "no se pudo leer el perfil del vídeo", "no SPS was found in the first video access units");
                return;
            }
            v.VideoDescription = sps.ToString();
            string px = sps.Width + "x" + sps.Height;
            if (sps.ProfileIdc != 66 && sps.ProfileIdc != 77 && sps.ProfileIdc != 100)
                Add(v, CastIssueCode.H264Profile, true, "vídeo H.264 " + sps.ProfileName, "H.264 profile_idc " + sps.ProfileIdc + " (only Baseline, Main and High are decoded)");
            if (sps.ChromaFormatIdc != 1 || sps.BitDepthLuma != 8 || sps.BitDepthChroma != 8)
                Add(v, CastIssueCode.H264Format, true, "vídeo de " + sps.BitDepthLuma + " bits o croma distinto de 4:2:0", "chroma_format_idc " + sps.ChromaFormatIdc + ", bit depth " + sps.BitDepthLuma);
            if (sps.Width > profile.MaxWidth || sps.Height > profile.MaxHeight + 8)         // 1088 lines without cropping still means 1080
                Add(v, CastIssueCode.H264Resolution, true, "resolución " + px + " (el máximo es " + profile.MaxWidth + "x" + profile.MaxHeight + ")", "picture " + px + " exceeds " + profile.MaxWidth + "x" + profile.MaxHeight);
            double mbps = sps.Fps > 0 ? sps.WidthInMbs * (double)sps.HeightInMbs * sps.Fps : 0;
            if (mbps > 0 && profile.MaxMacroblocksPerSecond > 0 && mbps > profile.MaxMacroblocksPerSecond * 1.01)
                Add(v, CastIssueCode.H264Level, true, "demasiados fotogramas por segundo para este Chromecast (" + sps.Fps.ToString("0.#", CultureInfo.InvariantCulture) + " fps a " + px + ")",
                    "macroblock rate " + (long)mbps + "/s exceeds " + profile.MaxMacroblocksPerSecond + "/s (level " + (profile.MaxH264Level / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + ")");
            else if (sps.LevelIdc > profile.MaxH264Level && mbps == 0)
                Add(v, CastIssueCode.H264Level, false, "nivel H.264 " + sps.Level.ToString("0.0", CultureInfo.InvariantCulture) + " superior al documentado", "level_idc " + sps.LevelIdc + " above " + profile.MaxH264Level + " and no frame rate in the stream");
            if (!sps.FrameMbsOnly) Add(v, CastIssueCode.H264Interlaced, false, "vídeo entrelazado", "interlaced H.264; the receiver may or may not deinterlace");
        }

        private static void EvaluateAudio(CastVerdict v, StreamInfo info, CastProfile profile, bool final)
        {
            var audio = info.Audio;
            if (audio == null) { Add(v, CastIssueCode.NoAudio, false, "sin audio", "the PMT declares no audio stream"); return; }
            switch (audio.Kind)
            {
                case EsKind.AacAdts:
                    if (info.AudioPayloadIsLatm)
                    {
                        v.AudioDescription = "AAC LATM (declared as ADTS)";
                        Add(v, CastIssueCode.AudioMismatch, true, "audio AAC LATM", "stream_type 0x0f (ADTS) but the PES payload is LATM; HLS needs ADTS");
                    }
                    else if (info.Adts != null)
                    {
                        v.AudioDescription = info.Adts.ToString();
                        if (info.Adts.ObjectType != 2 && info.Adts.ObjectType != 1 && info.Adts.ObjectType != 5)
                            Add(v, CastIssueCode.AudioUnsupported, true, "audio " + info.Adts.ProfileName, "AAC object type " + info.Adts.ObjectType);
                    }
                    else v.AudioDescription = "AAC (ADTS)";
                    break;
                case EsKind.AacLatm:
                    v.AudioDescription = info.AudioPayloadIsAdts ? "AAC (ADTS, declared as LATM)" : "AAC LATM";
                    if (!info.AudioPayloadIsAdts)
                        Add(v, CastIssueCode.AudioLatm, true, "audio AAC LATM", "stream_type 0x" + audio.StreamType.ToString("x2", CultureInfo.InvariantCulture) + " carries AAC in LATM; HLS needs ADTS and the engine does not convert it yet");
                    break;
                case EsKind.MpegAudio:
                    if (info.MpegAudio != null)
                    {
                        v.AudioDescription = info.MpegAudio + " " + info.MpegAudio.SampleRate + " Hz";
                        if (info.MpegAudio.Layer != 3) Add(v, CastIssueCode.AudioMp2, true, "audio MP2", "MPEG audio layer " + info.MpegAudio.Layer + " is not decoded by the Cast receiver");
                    }
                    else
                    {
                        v.AudioDescription = "MPEG audio";
                        // DVB stream types 0x03/0x04 carry layer II. If the frame header never showed, that is what it is.
                        if (final || info.IsComplete) Add(v, CastIssueCode.AudioMp2, true, "audio MP2", "MPEG audio without a readable frame header; layer II assumed (DVB)");
                    }
                    break;
                case EsKind.Ac3:
                case EsKind.Eac3:
                    v.AudioDescription = audio.Kind == EsKind.Ac3 ? "AC-3" : "E-AC-3";
                    if (!profile.AllowAc3) Add(v, CastIssueCode.AudioAc3, true, "audio " + v.AudioDescription, "AC-3 only works as passthrough to the TV or receiver, which is not reliable");
                    break;
                case EsKind.Dts:
                    v.AudioDescription = "DTS";
                    Add(v, CastIssueCode.AudioDts, true, "audio DTS", "DTS is not decoded by the Cast receiver");
                    break;
                default:
                    v.AudioDescription = "stream type 0x" + audio.StreamType.ToString("x2", CultureInfo.InvariantCulture);
                    Add(v, CastIssueCode.AudioUnsupported, true, "códec de audio no soportado", "audio stream_type 0x" + audio.StreamType.ToString("x2", CultureInfo.InvariantCulture));
                    break;
            }
        }

        // ---------- helpers for data that is already in memory ----------

        /// <summary>Verdict from a piece of transport stream (any alignment). Handy for tools and tests; the engine uses the buffer of the live session.</summary>
        public static CastVerdict Analyze(byte[] ts, int offset, int count, CastProfile profile)
        {
            var probe = new TsStreamProbe();
            probe.Feed(ts, offset, count);
            return Evaluate(probe.Snapshot(), profile, true);
        }
    }
}
