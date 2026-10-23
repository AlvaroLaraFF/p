using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Live;
using PUi.Platform;

namespace PUi.Testing
{
    /// <summary>
    /// A cast service the tests and the bench drive: available or not, a list of devices that can be changed, a check that answers what the test says (at once or when the test lets it), a send that
    /// connects after a gate, and a record of every call the interface made. Events are raised on the calling thread.
    /// </summary>
    public sealed class FakeCast : ICastService
    {
        private readonly object _gate = new object();
        private bool _available;
        private readonly List<CastDevice> _devices = new List<CastDevice>();
        private CastStatus _status = CastStatus.Idle;
        private bool _searching;

        public bool IsAvailable { get { return _available; } set { if (_available == value) return; _available = value; var h = AvailabilityChanged; if (h != null) h(); } }
        public event Action AvailabilityChanged;
        public event Action Changed;
        public event Action<string> Notice;

        public IReadOnlyList<CastDevice> Devices { get { lock (_gate) return _devices.ToList(); } }
        public bool Searching { get { return _searching; } set { if (_searching == value) return; _searching = value; Raise(); } }
        public CastStatus Status { get { lock (_gate) return _status; } }

        private void Raise() { var h = Changed; if (h != null) h(); }

        // ---------------------------------------------------------------- scripting
        public void SetDevices(params CastDevice[] devices) { lock (_gate) { _devices.Clear(); _devices.AddRange(devices); } Raise(); }
        public static CastDevice Chromecast(string name, string id = null) { return new CastDevice { Id = id ?? "gcast:" + name, Name = name, Kind = CastDeviceKind.Chromecast, CanPause = true, CanVolume = true }; }
        public static CastDevice Tv(string name, string id = null, bool canPause = true) { return new CastDevice { Id = id ?? "dlna:" + name, Name = name, Kind = CastDeviceKind.Tv, CanPause = canPause, CanVolume = true }; }

        /// <summary>What a check answers; the default says every channel is fine.</summary>
        public Func<CastDevice, Channel, CastCheck> CheckResult = (d, c) => CastCheck.Ok();
        /// <summary>When set, a check does not answer until the test completes this task.</summary>
        public Task CheckGate;
        public int Checks;
        public readonly List<string> CheckedPairs = new List<string>();
        public LiveSession LastOpenSession;

        /// <summary>Whether a send succeeds, and the notice if it does not.</summary>
        public bool SendSucceeds = true;
        public string SendFailure = "No se ha podido enviar el canal.";
        /// <summary>When set, a send stays in "Starting" until the test completes this task.</summary>
        public Task StartGate;

        // ---------------------------------------------------------------- what the interface did
        public int BeginSearchCalls, EndSearchCalls, StopCalls;
        /// <summary>One entry per send: the device and the channel.</summary>
        public readonly List<KeyValuePair<CastDevice, Channel>> SendCalls = new List<KeyValuePair<CastDevice, Channel>>();
        /// <summary>The channels of the sends (for the tests written before the sheet existed: one entry per channel sent).</summary>
        public IEnumerable<Channel> Sent { get { return SendCalls.Select(x => x.Value); } }
        public readonly List<bool> PauseCalls = new List<bool>();
        public readonly List<int> VolumeCalls = new List<int>();
        public int ReleasedLocal;

        public void BeginSearch() { BeginSearchCalls++; Searching = true; }
        public void EndSearch() { EndSearchCalls++; Searching = false; }

        public async Task<CastCheck> CheckAsync(CastDevice device, Channel channel, LiveSession open, CancellationToken ct)
        {
            Checks++;
            lock (_gate) CheckedPairs.Add(device.Id + "|" + (channel == null ? "" : channel.Id));
            LastOpenSession = open;
            var gate = CheckGate;
            if (gate != null) await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return CheckResult(device, channel);
        }

        public async Task<bool> SendAsync(CastDevice device, Channel channel, Func<Task> releaseLocal, CancellationToken ct)
        {
            lock (_gate) SendCalls.Add(new KeyValuePair<CastDevice, Channel>(device, channel));
            if (releaseLocal != null) { await releaseLocal().ConfigureAwait(false); ReleasedLocal++; }
            SetStatus(new CastStatus { Phase = CastPhase.Starting, Device = device, Channel = channel });
            var gate = StartGate;
            if (gate != null)
            {
                try { await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { SetStatus(CastStatus.Idle); return false; }
            }
            if (!SendSucceeds)
            {
                SetStatus(CastStatus.Idle);
                var h = Notice; if (h != null) h(SendFailure);
                return false;
            }
            SetStatus(new CastStatus { Phase = CastPhase.Sending, Device = device, Channel = channel, Volume = 40 });
            return true;
        }

        public void Stop() { StopCalls++; SetStatus(CastStatus.Idle); }

        public void SetPaused(bool paused)
        {
            PauseCalls.Add(paused);
            var s = Status;
            if (s.Phase == CastPhase.Sending) SetStatus(new CastStatus { Phase = s.Phase, Device = s.Device, Channel = s.Channel, Paused = paused, Volume = s.Volume });
        }

        public void SetVolume(int percent)
        {
            VolumeCalls.Add(percent);
            var s = Status;
            if (s.Phase == CastPhase.Sending) SetStatus(new CastStatus { Phase = s.Phase, Device = s.Device, Channel = s.Channel, Paused = s.Paused, Volume = percent });
        }

        // ---------------------------------------------------------------- the world changing under the interface
        public void SetStatus(CastStatus status) { lock (_gate) _status = status ?? CastStatus.Idle; Raise(); }

        /// <summary>The sending ends by itself (the channel went down, it was stopped from the TV): idle again and a notice.</summary>
        public void EndByItself(string notice)
        {
            SetStatus(CastStatus.Idle);
            var h = Notice; if (h != null) h(notice);
        }

        public void RaiseNotice(string text) { var h = Notice; if (h != null) h(text); }
    }
}
