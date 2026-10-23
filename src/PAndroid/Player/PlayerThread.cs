using System;
using System.Threading;
using Android.OS;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// The thread the players live on: a HandlerThread with its own Looper, shared by every player of the app. ExoPlayer wants every call on its "application looper" and MediaPlayer delivers its
    /// callbacks on the looper of the thread that created it; with their own thread, neither depends on the main thread (which draws the interface) and a player call may wait for it from the
    /// main thread without deadlocking.
    /// </summary>
    internal static class PlayerThread
    {
        private static readonly object Gate = new object();
        private static HandlerThread _thread;
        private static Handler _handler;

        public static Handler Handler
        {
            get
            {
                lock (Gate)
                {
                    if (_handler == null)
                    {
                        _thread = new HandlerThread("P-player");
                        _thread.Start();
                        _handler = new Handler(_thread.Looper);
                    }
                    return _handler;
                }
            }
        }

        public static Looper Looper { get { return Handler.Looper; } }

        public static bool IsPlayerThread { get { return Looper.MyLooper() == Looper; } }

        /// <summary>Runs the action later on the player thread. An exception in it is logged, never thrown into the looper.</summary>
        public static void Post(Action action)
        {
            Handler.Post(() => Guard(action));
        }

        public static void PostDelayed(Action action, long delayMs)
        {
            Handler.PostDelayed(() => Guard(action), delayMs);
        }

        /// <summary>Runs the action on the player thread and waits for it (inline if already there). false if it did not finish within the timeout.</summary>
        public static bool Run(Action action, int timeoutMs)
        {
            if (IsPlayerThread) { Guard(action); return true; }
            using (var done = new ManualResetEventSlim(false))
            {
                Handler.Post(() => { try { Guard(action); } finally { done.Set(); } });
                return done.Wait(timeoutMs);
            }
        }

        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { UdpLog.Error("player", "exception on the player thread: " + ex); }
        }
    }
}
