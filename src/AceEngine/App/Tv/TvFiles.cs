using System;
using System.IO;
using System.Text;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>Bounded reading and atomic writing of the app's small files (data/catalog.json, recents.json, searches.json).</summary>
    internal static class TvFiles
    {
        /// <summary>Reads a UTF-8 text file of at most maxBytes. Returns null if it does not exist, is larger (corrupt or tampered with) or cannot be read.</summary>
        public static string ReadBounded(string path, int maxBytes)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > maxBytes) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[Math.Min(fi.Length, (long)maxBytes) + 1];
                    int total = 0, n;
                    while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0) total += n;
                    if (total > maxBytes) return null;                       // it grew while being read
                    int offset = total >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
                    return new UTF8Encoding(false, false).GetString(buf, offset, total - offset);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                AceLog.Warn("tv", "could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Writes to a temporary file and moves it into place: an interrupted write does not leave the file broken. Returns false on failure (never throws).</summary>
        public static bool WriteAtomic(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string tmp = path + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); return true; }
                    catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
                    {
                        File.Delete(path);                                   // some file systems do not support Replace
                    }
                }
                File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                AceLog.Warn("tv", "could not save " + path + ": " + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return false;
            }
        }

        public static string Clip(string s, int max) { s = s ?? ""; return s.Length <= max ? s : s.Substring(0, max); }
    }
}
