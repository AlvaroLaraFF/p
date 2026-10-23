using System;
using System.Collections.Generic;
using System.Linq;

namespace PAndroid
{
    /// <summary>One track of the stream the player found (ExoPlayer's Tracks.Group), as plain values.</summary>
    public struct TrackFacts
    {
        public const int Audio = 1, Video = 2;
        /// <summary>Media3's C.TRACK_TYPE_AUDIO (1) or C.TRACK_TYPE_VIDEO (2); anything else is ignored.</summary>
        public int Type;
        public string Mime;
        /// <summary>The device can decode it (Tracks.Group.IsTrackSupported).</summary>
        public bool Supported;

        public TrackFacts(int type, string mime, bool supported) { Type = type; Mime = mime; Supported = supported; }
    }

    /// <summary>
    /// What to tell the viewer when a channel plays but part of it cannot: a stream whose audio the device has no decoder for (MP2, AC-3...) plays its picture in silence, and one whose video it
    /// cannot decode (H.265 on an old stick) plays only the sound. ExoPlayer does not call either an error, so without this the app would look broken for no reason. The text is shown over the
    /// picture by the shell (NoticeOverlay) on every device, a phone as much as a stick.
    /// </summary>
    public static class StreamNotices
    {
        /// <summary>The messages for a stream, in Spanish like the rest of the interface; empty when everything the stream carries can be played (or it carries no audio at all, which is not a fault).</summary>
        public static IList<string> Analyze(IEnumerable<TrackFacts> tracks)
        {
            var notices = new List<string>();
            if (tracks == null) return notices;
            var list = tracks.ToList();
            var audio = list.Where(t => t.Type == TrackFacts.Audio).ToList();
            var video = list.Where(t => t.Type == TrackFacts.Video).ToList();
            if (audio.Count > 0 && !audio.Any(t => t.Supported))
                notices.Add("Sin sonido: este dispositivo no puede decodificar el audio " + Names(audio) + " de este canal.");
            if (video.Count > 0 && !video.Any(t => t.Supported))
                notices.Add("Sin imagen: este dispositivo no puede decodificar el vídeo " + Names(video) + " de este canal.");
            return notices;
        }

        private static string Names(IEnumerable<TrackFacts> tracks)
        {
            var names = tracks.Select(t => StreamInfoText.CodecName(t.Mime)).Distinct().ToList();
            return string.Join(", ", names);
        }

        /// <summary>The same facts for the log line.</summary>
        public static string Describe(IEnumerable<TrackFacts> tracks)
        {
            if (tracks == null) return "no tracks";
            return string.Join(", ", tracks.Select(t => (t.Type == TrackFacts.Audio ? "audio " : t.Type == TrackFacts.Video ? "video " : "track ") + StreamInfoText.CodecName(t.Mime) + (t.Supported ? "" : " (NOT supported)")));
        }
    }
}
