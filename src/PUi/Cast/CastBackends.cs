using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;
using AceEngine.Links;
using AceEngine.Live;

namespace PUi.Cast
{
    /// <summary>
    /// What <see cref="CastController"/> needs from the engine's send service (<see cref="CastService"/>): an interface so that the controller is tested without a network and so that nothing of the
    /// engine's concrete classes is in its logic. <see cref="CastEngine"/> is the real one.
    /// </summary>
    public interface ICastEngine : IDisposable
    {
        /// <summary>The list of screens: the engine adds the DLNA ones, the shell's Google Cast bridge adds the Chromecasts.</summary>
        CastTargetList Targets { get; }
        /// <summary>One search for DLNA renderers; replaces (with some retention) the "dlna" targets of the list.</summary>
        Task DiscoverAsync(CancellationToken ct);
        /// <summary>Whether the channel can go to the target. <paramref name="open"/> is the live session of the channel if it is open locally; null otherwise.</summary>
        Task<CastVerdict> CheckAsync(AceLink link, CastTarget target, LiveSession open, CancellationToken ct);
        Task<CastResult> SendAsync(CastTarget target, AceLink link, string name, CancellationToken ct);
        void Stop();
        /// <summary>The receiver is known to be alive (the Cast session is up): postpones the engine's idle timeout.</summary>
        void KeepAlive();
        CastState State { get; }
        /// <summary>Why the state is what it is (engine's English, for the log and for <see cref="CastMessages.Ended"/>).</summary>
        string Reason { get; }
        event Action<CastState> StateChanged;
        // control of a DLNA renderer (a Google Cast device is controlled through its session)
        Task<bool> PauseAsync(CancellationToken ct);
        Task<bool> ResumeAsync(CancellationToken ct);
        /// <summary>The renderer's transport state, or null if nothing is being sent to a renderer.</summary>
        Task<DlnaTransportInfo> GetTransportAsync(CancellationToken ct);
        Task<int> GetVolumeAsync(CancellationToken ct);
        Task SetVolumeAsync(int volume, CancellationToken ct);
    }

    /// <summary>The real <see cref="ICastEngine"/>: a thin wrapper of the engine's <see cref="CastService"/>.</summary>
    public sealed class CastEngine : ICastEngine
    {
        private readonly CastService _s;
        private readonly bool _owns;

        public CastEngine(CastService service, bool ownsIt = true) { _s = service; _owns = ownsIt; }

        public CastTargetList Targets { get { return _s.Targets; } }
        public Task DiscoverAsync(CancellationToken ct) { return _s.DiscoverDlnaAsync(ct); }

        public Task<CastVerdict> CheckAsync(AceLink link, CastTarget target, LiveSession open, CancellationToken ct)
        {
            // a tuner television takes any channel: nothing to read from the stream
            if (target != null && target.Supports(CastCapabilities.AnyCodec)) return _s.CheckAsync(link, target, ct);
            if (open != null) return _s.CheckOpenSessionAsync(open, target, ct);
            return _s.CheckAsync(link, target, ct);
        }

        public Task<CastResult> SendAsync(CastTarget target, AceLink link, string name, CancellationToken ct) { return _s.SendAsync(target, link, name, ct); }
        public void Stop() { _s.Stop(); }
        public void KeepAlive() { _s.Source.KeepAlive(); }
        public CastState State { get { return _s.State; } }
        public string Reason { get { return _s.Reason; } }
        public event Action<CastState> StateChanged { add { _s.StateChanged += value; } remove { _s.StateChanged -= value; } }
        public Task<bool> PauseAsync(CancellationToken ct) { return _s.PauseAsync(ct); }
        public Task<bool> ResumeAsync(CancellationToken ct) { return _s.ResumeAsync(ct); }
        public Task<DlnaTransportInfo> GetTransportAsync(CancellationToken ct) { return _s.GetTransportAsync(ct); }
        public Task<int> GetVolumeAsync(CancellationToken ct) { return _s.GetVolumeAsync(ct); }
        public Task SetVolumeAsync(int volume, CancellationToken ct) { return _s.SetVolumeAsync(volume, ct); }
        public void Dispose() { if (_owns) _s.Dispose(); }
    }

    /// <summary>
    /// The Google Cast side of a phone (the shell implements it with the Cast SDK; nothing else may name the SDK). It finds the Chromecasts and adds them to the engine's target list itself;
    /// the controller connects, tells it to load what the engine prepared, and controls it. All members may be called from any thread.
    /// </summary>
    public interface IGoogleCastBridge : IDisposable
    {
        /// <summary>Look hard for Chromecasts (the sheet is open) or only passively (it is not).</summary>
        void SetActiveScan(bool on);
        /// <summary>Selects the device and starts a Cast session on it. Completes when the session is up; throws if it cannot be started.</summary>
        Task ConnectAsync(CastTarget target, CancellationToken ct);
        /// <summary>Loads what the engine prepared (the HLS playlist, live, unknown duration, the title) on the connected device and starts it.</summary>
        Task LoadAsync(CastResult result, CancellationToken ct);
        void Pause();
        void Resume();
        /// <summary>0 to 100.</summary>
        void SetVolume(int percent);
        /// <summary>Ends the session and the receiver application (the Chromecast goes back to what it was doing). Idempotent. Must not raise <see cref="SessionEnded"/>.</summary>
        void Disconnect();
        /// <summary>The device's state changed: paused, and its volume (0 to 100, -1 unknown).</summary>
        event Action<bool, int> StatusChanged;
        /// <summary>The session ended by itself: the user stopped it from the TV or another phone, the device went away, the channel failed. The argument is the notice (Spanish).</summary>
        event Action<string> SessionEnded;
    }

    /// <summary>What the shell does while something is being sent: the foreground service with its notification and the locks that keep Wi-Fi and the process alive with the screen off.</summary>
    public interface ISendingHost
    {
        /// <summary>A send starts (called while the app is in the foreground, from a user's action).</summary>
        void Begin(string deviceName, string channelName);
        /// <summary>The state changed (paused, another channel).</summary>
        void Update(string deviceName, string channelName, bool paused);
        /// <summary>Nothing is being sent any more.</summary>
        void End();
    }
}
