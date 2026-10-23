using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Media;
using Android.OS;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// What the device can decode, read from MediaCodecList once, in the background (it takes tens of milliseconds and the activity has no need to wait for it): the startup log lists every
    /// decoder (the answer to "does this stick play MP2 or AC-3?" is in the log of the stick itself) and the player asks whether the device has an MP2 decoder before it decides how to play the audio.
    /// </summary>
    internal static class AndroidCodecs
    {
        private const string Tag = "codecs";
        private static readonly object Gate = new object();
        private static Task<IList<CodecEntry>> _task;

        /// <summary>Starts reading the list; harmless to call twice.</summary>
        public static void Start()
        {
            lock (Gate) { if (_task == null) _task = Task.Run(Read); }
        }

        /// <summary>The decoders (waits for the read to finish: a few tens of ms at most; an empty list if it failed).</summary>
        public static IList<CodecEntry> Entries
        {
            get
            {
                Start();
                try { return _task.Result; }
                catch (Exception) { return new List<CodecEntry>(); }
            }
        }

        public static bool HasMp2Decoder { get { return CodecReport.HasDecoder(Entries, CodecReport.Mp2); } }

        /// <summary>Writes the report to the log (one line per format that matters, then every decoder).</summary>
        public static void Log()
        {
            try
            {
                var entries = Entries;
                foreach (var line in CodecReport.Lines(entries)) UdpLog.Info(Tag, line);
                UdpLog.Info(Tag, CodecReport.Verdict(entries));
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "report: " + ex.Message); }
        }

        private static IList<CodecEntry> Read()
        {
            var list = new List<CodecEntry>();
            try
            {
                var infos = new MediaCodecList(MediaCodecListKind.AllCodecs).GetCodecInfos();
                foreach (var info in infos)
                {
                    try
                    {
                        var e = new CodecEntry { Name = info.Name ?? "", IsEncoder = info.IsEncoder, Types = info.GetSupportedTypes() ?? new string[0] };
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) { e.FlagsKnown = true; e.IsHardware = info.IsHardwareAccelerated; e.IsSoftwareOnly = info.IsSoftwareOnly; }
                        if (!e.IsEncoder) Capabilities(info, e);
                        list.Add(e);
                    }
                    catch (Exception ex) { UdpLog.Warn(Tag, "reading a codec: " + ex.Message); }
                }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "MediaCodecList: " + ex.Message); }
            return list;
        }

        /// <summary>The biggest picture and the number of simultaneous instances of a video decoder, where the system tells.</summary>
        private static void Capabilities(MediaCodecInfo info, CodecEntry e)
        {
            foreach (var type in e.Types)
            {
                if (!type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var caps = info.GetCapabilitiesForType(type);
                    var video = caps != null ? caps.VideoCapabilities : null;
                    if (video != null && e.MaxSize.Length == 0)
                    {
                        var w = video.SupportedWidths; var h = video.SupportedHeights;
                        if (w != null && h != null) e.MaxSize = ((Java.Lang.Integer)w.Upper).IntValue() + "x" + ((Java.Lang.Integer)h.Upper).IntValue();
                    }
                    if (caps != null && Build.VERSION.SdkInt >= BuildVersionCodes.M && e.MaxInstances == 0) e.MaxInstances = caps.MaxSupportedInstances;
                }
                catch (Exception) { /* a type the codec lists but will not describe (secure decoders on some devices) */ }
            }
        }
    }
}
