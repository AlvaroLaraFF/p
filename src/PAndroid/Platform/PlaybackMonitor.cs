using System;
using System.Collections.Generic;

namespace PAndroid
{
    /// <summary>
    /// What the players tell the shell about whether anything is really playing, so the shell can act on it: today, the screen is kept on only while a channel plays (ScreenKeepPolicy). A
    /// player is "paused" when the viewer paused it or when another app's audio holds it (Alexa's overlay, a notification sound). Owner-aware like PlayerNotices: the players are created and
    /// disposed for every attempt, and one that goes away while the next is already playing must not clear the next one's state.
    /// </summary>
    internal static class PlaybackMonitor
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<object> Paused = new HashSet<object>();

        /// <summary>The paused state of some player changed (any thread).</summary>
        public static event Action Changed;

        /// <summary>Some live player is paused.</summary>
        public static bool AnyPaused { get { lock (Gate) return Paused.Count > 0; } }

        public static void SetPaused(object owner, bool paused)
        {
            bool changed;
            lock (Gate) changed = paused ? Paused.Add(owner) : Paused.Remove(owner);
            if (changed) Raise();
        }

        /// <summary>The player is gone.</summary>
        public static void Gone(object owner)
        {
            bool changed;
            lock (Gate) changed = Paused.Remove(owner);
            if (changed) Raise();
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) { try { h(); } catch (Exception ex) { AceEngine.App.UdpLog.Warn("player", "playback monitor handler: " + ex.Message); } }
        }
    }
}
