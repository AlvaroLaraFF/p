using System;

namespace AceEngine.Util
{
    public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3, Off = 4 }

    /// <summary>
    /// Minimal engine log. Anything inferred or unknown about the protocol is logged here so it can be
    /// tuned when testing against the real network.
    /// On the TV, Sink is redirected to dlog/Tizen.Log; on PC, to the console.
    /// </summary>
    public static class AceLog
    {
        private static readonly object Gate = new object();

        public static LogLevel MinLevel = LogLevel.Info;

        /// <summary>Destination of the messages: (level, tag, text). Null = discard.</summary>
        public static Action<LogLevel, string, string> Sink;

        public static bool IsEnabled(LogLevel level)
        {
            return Sink != null && level >= MinLevel;
        }

        public static void Debug(string tag, string message) { Write(LogLevel.Debug, tag, message); }
        public static void Info(string tag, string message) { Write(LogLevel.Info, tag, message); }
        public static void Warn(string tag, string message) { Write(LogLevel.Warn, tag, message); }
        public static void Error(string tag, string message) { Write(LogLevel.Error, tag, message); }

        private static void Write(LogLevel level, string tag, string message)
        {
            var sink = Sink;
            if (sink == null || level < MinLevel) return;
            try
            {
                lock (Gate) sink(level, tag, message);
            }
            catch
            {
                // A failure of the log destination must never break the engine.
            }
        }
    }
}
