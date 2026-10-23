using System;
using System.Threading.Tasks;
using Android.Runtime;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>The app's log on Android: every line goes to logcat with the tag "P" (and, if the configuration asks for it, also over UDP: see UdpLog), and unhandled exceptions end up in it.</summary>
    internal static class AndroidLog
    {
        public const string Tag = "P";
        private static int _handlersInstalled;

        /// <summary>Points UdpLog's local sink at logcat. First thing the process does, before any other line.</summary>
        public static void InstallSink()
        {
            UdpLog.LocalSink = (level, tag, msg) =>
            {
                foreach (var chunk in LogText.Chunks(tag + ": " + msg))
                {
                    switch (level)
                    {
                        case "E": Android.Util.Log.Error(Tag, chunk); break;
                        case "W": Android.Util.Log.Warn(Tag, chunk); break;
                        case "D": Android.Util.Log.Debug(Tag, chunk); break;
                        default: Android.Util.Log.Info(Tag, chunk); break;
                    }
                }
            };
        }

        /// <summary>
        /// Exceptions nobody caught go to the log before the process dies (the same as on the TV: logging does not mask them, the runtime still ends the app). Unobserved task exceptions are only
        /// reported: a faulted fire-and-forget task must not take the app down.
        /// </summary>
        public static void InstallUnhandledHandlers()
        {
            if (System.Threading.Interlocked.Exchange(ref _handlersInstalled, 1) == 1) return;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => UdpLog.Error("app", "UNHANDLED EXCEPTION" + (e.IsTerminating ? " (terminating)" : "") + ": " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                // A connect that lost its race against a timeout fails with "Operation Canceled" when its socket is closed: nothing to report. Anything else is a real bug in some fire-and-forget task.
                if (TaskFailures.IsCancellation(e.Exception)) AceEngine.Util.AceLog.Debug("app", "unobserved cancelled task: " + e.Exception.GetBaseException().Message);
                else UdpLog.Warn("app", "unobserved task exception: " + e.Exception);
                e.SetObserved();
            };
            AndroidEnvironment.UnhandledExceptionRaiser += (s, e) => UdpLog.Error("app", "UNHANDLED EXCEPTION (from Java): " + e.Exception);
        }
    }
}
