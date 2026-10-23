using System;
using System.Collections.Generic;
using System.Text;
using Android.Content;
using Android.Media;
using Android.OS;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// Pauses the channel when the TV goes away (see <see cref="HdmiState"/> and <see cref="HdmiPolicy"/>): Amazon requires a Fire TV app to pause on an HDMI disconnection, which is what the
    /// TV being switched off or set to another input looks like, and to stay paused when the link returns. It watches the audio output devices (AudioDeviceCallback, API 23: the preferred way on
    /// Fire OS 6 and later) and calls <paramref name="onLost"/> on the main thread once the HDMI output has been gone for <see cref="HdmiPolicy.PauseAfterMs"/>. It also writes to the log what
    /// the outputs are, with the audio encodings each accepts (whether the TV takes AC-3 over HDMI is the kind of thing that decides what the audio of a channel does).
    /// </summary>
    internal sealed class HdmiMonitor
    {
        private const string Tag = "hdmi";
        private readonly AudioManager _audio;
        private readonly Handler _main = new Handler(Looper.MainLooper);
        private readonly HdmiState _state = new HdmiState();
        private readonly Action _onLost;
        private Callback _callback;
        private int _generation;

        public HdmiMonitor(Context context, Action onLost)
        {
            _audio = (AudioManager)context.GetSystemService(Context.AudioService);
            _onLost = onLost;
        }

        public void Start()
        {
            if (_audio == null || _callback != null) return;
            try
            {
                LogOutputs();
                _callback = new Callback(this);
                _audio.RegisterAudioDeviceCallback(_callback, _main);          // it first reports the outputs that are already there
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "could not watch the audio outputs: " + ex.Message); _callback = null; }
        }

        public void Stop()
        {
            var cb = _callback; _callback = null;
            _generation++;
            if (cb != null && _audio != null) { try { _audio.UnregisterAudioDeviceCallback(cb); } catch (Exception) { } }
        }

        private static List<KeyValuePair<int, int>> Pairs(AudioDeviceInfo[] devices)
        {
            var l = new List<KeyValuePair<int, int>>();
            if (devices != null) foreach (var d in devices) if (d != null && d.IsSink) l.Add(new KeyValuePair<int, int>(d.Id, (int)d.Type));
            return l;
        }

        private void OnAdded(AudioDeviceInfo[] devices)
        {
            var change = _state.Added(Pairs(devices));
            if (change == HdmiChange.Restored)
            {
                _generation++;                                                   // a disconnection that was waiting to be acted on is off
                UdpLog.Info(Tag, "HDMI output present" + (_generation > 1 ? " (back): playback stays paused until the viewer presses play" : ""));
            }
            if (devices != null && devices.Length > 0) LogOutputs();
        }

        private void OnRemoved(AudioDeviceInfo[] devices)
        {
            if (_state.Removed(Pairs(devices)) != HdmiChange.Lost) return;
            int gen = ++_generation;
            UdpLog.Info(Tag, "HDMI output gone (the TV is off or on another input): pausing in " + HdmiPolicy.PauseAfterMs + " ms if it does not come back");
            _main.PostDelayed(() =>
            {
                if (gen != _generation || _state.Connected) return;
                UdpLog.Info(Tag, "HDMI still gone: pausing the channel");
                try { _onLost(); } catch (Exception ex) { UdpLog.Warn(Tag, "pause: " + ex.Message); }
            }, HdmiPolicy.PauseAfterMs);
        }

        /// <summary>Every audio output the system lists, with what it accepts.</summary>
        private void LogOutputs()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var d in _audio.GetDevices(GetDevicesTargets.Outputs))
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(d.Type).Append(" #").Append(d.Id);
                    string name = d.ProductName != null ? d.ProductName.ToString() : "";
                    if (name.Length > 0) sb.Append(" \"").Append(name).Append('"');
                    var enc = d.GetEncodings();
                    if (enc != null && enc.Length > 0) sb.Append(" encodings=").Append(AudioEncodingNames.List(Array.ConvertAll(enc, e => (int)e)));
                    var ch = d.GetChannelCounts();
                    if (ch != null && ch.Length > 0) sb.Append(" channels=").Append(string.Join("/", ch));
                }
                UdpLog.Info(Tag, "audio outputs: " + (sb.Length == 0 ? "(none)" : sb.ToString()));
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "listing the audio outputs: " + ex.Message); }
        }

        private sealed class Callback : AudioDeviceCallback
        {
            private readonly HdmiMonitor _m;
            public Callback(HdmiMonitor m) { _m = m; }
            public override void OnAudioDevicesAdded(AudioDeviceInfo[] addedDevices) { try { _m.OnAdded(addedDevices); } catch (Exception ex) { UdpLog.Warn(Tag, "added: " + ex.Message); } }
            public override void OnAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) { try { _m.OnRemoved(removedDevices); } catch (Exception ex) { UdpLog.Warn(Tag, "removed: " + ex.Message); } }
        }
    }
}
