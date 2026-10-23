using System;
using AceEngine.Sources;

namespace AceEngine.Probe
{
    /// <summary>What the player saw of a channel, in the sense that matters to the checker.</summary>
    public enum PlaybackSignal
    {
        /// <summary>Stream arrived: the engine filled the live prebuffer (recent pieces from connected peers). It counts as a fresh verification.</summary>
        Streaming,
        /// <summary>Not a single piece within the opening deadline (or the descriptor was not resolved): nobody serves the channel right now.</summary>
        NoSource,
        /// <summary>The live stream stopped advancing during playback (sender stopped) or the player ran out of new data.</summary>
        Stalled,
        /// <summary>The engine session stopped on its own while the channel was open (it ran out of peers, swarm failure).</summary>
        SessionLost
    }

    /// <summary>A notice from the player to the checker: of the channel 'Entry', 'Signal' has been seen.</summary>
    public sealed class PlaybackEvidence
    {
        public LinkEntry Entry;
        public PlaybackSignal Signal;
        /// <summary>Connected peers of the session when the stream arrived (only with Streaming).</summary>
        public int Peers;
        /// <summary>Short text for the log and for the reason the card shows.</summary>
        public string Detail;

        public bool IsNegative { get { return Signal != PlaybackSignal.Streaming; } }
        public override string ToString() { return Signal + (Detail != null ? " (" + Detail + ")" : "") + (Entry != null ? " " + Entry.Name : ""); }
    }

    /// <summary>Whoever wants to learn what the player sees (the channel checker). It may be called from any thread and must not block or throw.</summary>
    public interface IPlaybackReporter
    {
        void Report(PlaybackEvidence evidence);
    }
}
