using System;
using System.Collections.Generic;

namespace AceEngine.Util
{
    /// <summary>Content-Type table (inferred) and list of video/audio file extensions.</summary>
    public static class MimeTypes
    {
        /// <summary>videoextdefaults of the original implementation.</summary>
        public static readonly string[] VideoExtensions =
        {
            "3gp", "aac", "ape", "asf", "avi", "dv", "divx", "flac", "flc", "flv", "m2ts", "m4a", "mka", "mkv",
            "mpeg", "mpeg4", "mpegts", "mpg4", "mp3", "mp4", "mpg", "mov", "m4v", "ogg", "ogm", "ogv", "oga", "ogx",
            "qt", "rm", "swf", "ts", "vob", "wmv", "wav", "webm"
        };

        private static readonly Dictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ts", "video/mp2t" }, { "m2ts", "video/mp2t" }, { "mpegts", "video/mp2t" },
            { "mp4", "video/mp4" }, { "m4v", "video/mp4" }, { "mov", "video/mp4" }, { "mpeg4", "video/mp4" }, { "mpg4", "video/mp4" },
            { "mkv", "video/x-matroska" }, { "mka", "audio/x-matroska" },
            { "avi", "video/x-msvideo" },
            { "webm", "video/webm" },
            { "mp3", "audio/mpeg" }, { "aac", "audio/aac" }, { "flac", "audio/flac" },
            { "m4a", "audio/mp4" }, { "ogg", "audio/ogg" }, { "oga", "audio/ogg" }, { "ogv", "video/ogg" }, { "ogm", "video/ogg" },
            { "wav", "audio/wav" }, { "wmv", "video/x-ms-wmv" }, { "asf", "video/x-ms-asf" }, { "flv", "video/x-flv" },
            { "mpg", "video/mpeg" }, { "mpeg", "video/mpeg" }, { "3gp", "video/3gpp" }
        };

        public static string GetExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return "";
            int slash = Math.Max(filename.LastIndexOf('/'), filename.LastIndexOf('\\'));
            int dot = filename.LastIndexOf('.');
            if (dot < 0 || dot < slash) return "";
            return filename.Substring(dot + 1).ToLowerInvariant();
        }

        public static string GetMimeType(string filename)
        {
            string mime;
            return ByExtension.TryGetValue(GetExtension(filename), out mime) ? mime : "application/octet-stream";
        }

        public static bool IsVideoExtension(string filename)
        {
            var ext = GetExtension(filename);
            foreach (var e in VideoExtensions) if (e == ext) return true;
            return false;
        }
    }
}
