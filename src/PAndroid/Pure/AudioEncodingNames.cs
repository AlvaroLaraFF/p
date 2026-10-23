using System.Collections.Generic;

namespace PAndroid
{
    /// <summary>The names of android.media.AudioFormat.ENCODING_* values, for the log lines that say what an audio output accepts (a TV over HDMI: PCM only, or also AC-3 and E-AC-3?).</summary>
    public static class AudioEncodingNames
    {
        public static string Name(int encoding)
        {
            switch (encoding)
            {
                case 2: return "pcm16";
                case 3: return "pcm8";
                case 4: return "pcm-float";
                case 5: return "ac3";
                case 6: return "e-ac3";
                case 7: return "dts";
                case 8: return "dts-hd";
                case 9: return "mp3";
                case 10: return "aac-lc";
                case 11: return "he-aac-v1";
                case 12: return "he-aac-v2";
                case 13: return "iec61937";
                case 14: return "truehd";
                case 15: return "aac-eld";
                case 16: return "xhe-aac";
                case 17: return "ac4";
                case 18: return "e-ac3-joc";
                case 19: return "dolby-mat";
                case 20: return "opus";
                case 21: return "pcm24";
                case 22: return "pcm32";
                default: return "encoding" + encoding;
            }
        }

        /// <summary>"[pcm16, ac3, e-ac3]" for a list of encodings.</summary>
        public static string List(IEnumerable<int> encodings)
        {
            var names = new List<string>();
            foreach (int e in encodings) names.Add(Name(e));
            return "[" + string.Join(", ", names) + "]";
        }
    }
}
