using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Probe;
using AceEngine.Sources;

namespace AceEngine.Sim
{
    /// <summary>Fake prober: results scheduled by key and with a delay (to test the list and the interface's preview).</summary>
    public sealed class FakeProber : ILinkProber
    {
        public readonly Dictionary<string, ProbeResult> Results = new Dictionary<string, ProbeResult>();
        public TimeSpan Delay = TimeSpan.FromMilliseconds(20);
        public int Calls;
        public ProbeOptions Options { get; set; }

        public FakeProber() { Options = new ProbeOptions { CycleInterval = TimeSpan.FromSeconds(60), ResultTtl = TimeSpan.FromSeconds(60) }; }

        public async Task<ProbeResult> ProbeAsync(LinkEntry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(Delay, ct).ConfigureAwait(false);
            ProbeResult r;
            lock (Results) { if (!Results.TryGetValue(entry.Key, out r)) r = new ProbeResult { State = LinkState.Unknown }; }
            var copy = new ProbeResult { State = r.State, Seeders = r.Seeders, Peers = r.Peers, Approximate = r.Approximate, NoCounts = r.NoCounts, Unconfirmed = r.Unconfirmed, Method = r.Method, TimeUtc = DateTime.UtcNow };
            return copy;
        }
    }

    /// <summary>Fake playback with scheduled states.</summary>
    public sealed class FakePlayback : IPlayback
    {
        public PlaybackStatus Script;                 // final state reached after Delay
        public TimeSpan Delay = TimeSpan.FromMilliseconds(30);
        public bool Fail;
        public string FailMessage = "fake";
        public int RunCount;
        public bool Stopped, Disposed;
        public int ToggleCount;
        public long LastSeek;
        private PlaybackStatus _st = new PlaybackStatus();

        public PlaybackStatus Status { get { return _st.Clone(); } }
        public event Action<PlaybackStatus> Changed;
        public string EngineDetail { get { return "fake"; } }
        public long EngineLivePos { get; set; }

        private void Set(Action<PlaybackStatus> f) { var s = _st.Clone(); f(s); _st = s; var h = Changed; if (h != null) h(s.Clone()); }

        public async Task RunAsync(LinkEntry entry, string name, CancellationToken ct)
        {
            RunCount++;
            Set(s => { s.Name = name ?? entry.Name; s.Phase = PlayPhase.Resolving; });
            try
            {
                await Task.Delay(Delay, ct).ConfigureAwait(false);
                if (Fail) { Set(s => { s.Phase = PlayPhase.Error; s.Error = FailMessage; }); return; }
                Set(s => { s.Phase = PlayPhase.Buffering; s.BufferPercent = 40; s.Peers = 3; });
                await Task.Delay(Delay, ct).ConfigureAwait(false);
                Set(s =>
                {
                    s.Phase = PlayPhase.Playing; s.IsLive = Script == null ? true : Script.IsLive; s.Peers = 5; s.DownBytesPerSec = 600 * 1024; s.UpBytesPerSec = 40 * 1024; s.BufferPercent = 90;
                    s.StreamInfo = "video h264 1920x1080 25 fps / audio mp2 48000 Hz 2 ch"; s.PositionMs = 1000; s.DurationMs = s.IsLive ? -1 : 3600000;
                    if (Script != null) { s.Warning = Script.Warning; s.TrustNote = Script.TrustNote; }
                });
                while (true) { await Task.Delay(200, ct).ConfigureAwait(false); Set(s => { s.PositionMs += 200; }); EngineLivePos++; }
            }
            catch (OperationCanceledException) { Set(s => { s.Phase = PlayPhase.Idle; }); }
        }

        public void Stop() { Stopped = true; }
        public void TogglePause() { ToggleCount++; Set(s => { s.Phase = s.Phase == PlayPhase.Paused ? PlayPhase.Playing : PlayPhase.Paused; }); }
        public Task SeekRelativeAsync(long deltaMs) { LastSeek += deltaMs; return Task.CompletedTask; }
        public TimeSpan TimeshiftWindow { get; set; }
        public TimeSpan BehindLive { get; set; }
        public Task SeekBehindLiveAsync(TimeSpan delta) { BehindLive = TimeSpan.FromTicks(Math.Max(0, Math.Min(TimeshiftWindow.Ticks, BehindLive.Ticks - delta.Ticks))); return Task.CompletedTask; }
        public Task GoLiveAsync() { BehindLive = TimeSpan.Zero; return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
    }
}
