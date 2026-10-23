using System;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.App
{
    /// <summary>Failure of the platform player.</summary>
    public sealed class PlayerFailure : Exception
    {
        /// <summary>true if retrying is useless (unsupported codec or format).</summary>
        public bool Fatal { get; private set; }
        public PlayerFailure(string message, bool fatal = false) : base(message) { Fatal = fatal; }
    }

    /// <summary>
    /// Platform player (on the TV, Tizen.Multimedia.Player; on Android, Media3/ExoPlayer). The retry and state logic is in PlaybackSession
    /// and is tested on the PC with a fake implementation. This interface is the CONTRACT a platform player has to keep for that logic to work; the executable version of it is
    /// AceEngine.Tests/App/PlayerBackendConformanceTests (abstract: every new backend that can run on the PC derives from it; the ones that cannot, like the device players,
    /// are checked against the same list by hand).
    ///
    /// Lifecycle
    ///  - One instance per playback ATTEMPT. PlaybackSession asks IPlayerFactory.Create() for a new one at the start, after a failure (retry) and every time the player has to be reopened
    ///    (seek behind live, LIVE, resume outside the timeshift window). An instance is never reused: after Dispose, or after a failure, the session throws it away.
    ///  - The session subscribes Failed, Completed and Buffering right after Create and BEFORE PrepareAndStartAsync. Events can come from any thread at any moment from then until
    ///    Dispose returns, even while PrepareAndStartAsync is still running. After Dispose has returned, no event is raised.
    ///  - Every member can be called from any thread (the session uses thread-pool threads); the implementation moves to its platform's thread itself (the TV: Ui.Run / Ui.Post).
    ///
    /// Audio focus and video surface belong to the platform, not to the player: the player draws into the surface the shell owns and takes part in the app's audio focus
    /// (IAudioFocus) instead of asking for one of its own.
    /// </summary>
    public interface IPlayerBackend : IDisposable
    {
        /// <summary>
        /// Creates the player, opens the URL (the engine's local HTTP URL), prepares and starts it. Called once per instance.
        ///  - Completes when playback has STARTED. Best: when the first frame is out, so the interface (which keeps an opaque loading screen until this returns) can reveal the video without
        ///    showing what is behind it. The TV waits up to 1.5 s for the position to advance.
        ///  - Honors <paramref name="ct"/>: when cancelled it stops promptly, leaves the instance disposable and throws OperationCanceledException. The session cancels it on its own
        ///    timeout (PlaybackOptions.PrepareTimeout) and when the user leaves. Disposing the player while it prepares ends the preparation too.
        ///  - Any failure is a PlayerFailure; Fatal = true only when retrying is useless (unsupported codec or format). Do not let NotSupportedException, ArgumentException or FormatException
        ///    escape: the session reads them as an unsupported LINK (fatal, "Enlace no soportado"), not as a player problem.
        /// </summary>
        Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct);

        /// <summary>
        /// Playback position in ms, -1 if unknown: before the player has started and after Dispose. Called every PlaybackOptions.StatusInterval (500 ms) from a thread-pool thread:
        /// it must return promptly (the TV gives it 500 ms and falls back to the last value) and never throw. It does not go backwards in normal playback, it STOPS while paused
        /// (the session calls a position that does not advance for StallTimeout, while not paused and not buffering, a failure) and on VOD it jumps on a seek.
        /// </summary>
        long PositionMs { get; }

        /// <summary>Duration in ms on VOD, -1 for live or unknown. Valid once PrepareAndStartAsync has completed.</summary>
        long DurationMs { get; }

        /// <summary>Description of the tracks ("H.264 1920x1080 25 fps / audio 48000 Hz 2 ch"). Never null (empty if unknown); valid once PrepareAndStartAsync has completed. It is logged and shown.</summary>
        string StreamInfo { get; }

        /// <summary>
        /// The player reported an error, during preparation or playback. Fatal = false for what a retry can fix (the system took the decoder away, a connection dropped), Fatal = true for
        /// unsupported media. The session decides about retries; the player does not retry on its own.
        /// </summary>
        event Action<PlayerFailure> Failed;

        /// <summary>The stream ended (VOD reached its end, or the local server closed a live stream: the session treats the second as a failure and retries).</summary>
        event Action Completed;

        /// <summary>Buffer percentage of the player itself (0-100) while it rebuffers; 100 when it is done. The session does not call a stalled position a failure while this is recent.</summary>
        event Action<int> Buffering;

        /// <summary>
        /// Pauses. Idempotent, does not block, never throws (errors are logged). While paused the player stops consuming the stream (it may keep a small buffer), which is what makes the
        /// engine's timeshift memory grow behind live instead of being overrun.
        /// </summary>
        void Pause();

        /// <summary>Resumes after Pause. Same rules: idempotent, does not block, never throws.</summary>
        void Resume();

        /// <summary>
        /// Seeks to <paramref name="positionMs"/>. VOD only (on live the session reopens the player with ?back= instead). Completes when the picture is at the new position;
        /// honors <paramref name="ct"/>. Does nothing if there is nothing to seek.
        /// </summary>
        Task SeekAsync(long positionMs, CancellationToken ct);

        // IDisposable.Dispose: releases everything (decoder, display, sockets, listeners). Idempotent, never throws, may take a short while (the TV allows 5 s). Afterwards PositionMs is -1
        // and no event is raised. Disposing before PrepareAndStartAsync, or while it is running, is allowed.
    }

    public interface IPlayerFactory
    {
        IPlayerBackend Create();
    }
}
