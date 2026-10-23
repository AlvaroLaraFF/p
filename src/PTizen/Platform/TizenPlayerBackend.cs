using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using ElmSharp;
using Tizen.Multimedia;

namespace PTizen.Platform
{
    internal sealed class TizenPlayerFactory : IPlayerFactory
    {
        private readonly Window _window;
        private readonly Func<bool> _ensureTransparent;
        private readonly TizenPlatform _platform;
        public TizenPlayerFactory(Window window, Func<bool> ensureTransparent, TizenPlatform platform) { _window = window; _ensureTransparent = ensureTransparent; _platform = platform; }
        public IPlayerBackend Create() { return new TizenPlayerBackend(_window, _ensureTransparent, _platform); }
    }

    /// <summary>The TV's player: Tizen.Multimedia.Player with Display(Window). All calls to the player go through the main thread.</summary>
    internal sealed class TizenPlayerBackend : IPlayerBackend
    {
        private const string Tag = "player";
        private readonly Window _window;
        private readonly Func<bool> _ensureTransparent;
        private readonly TizenPlatform _platform;
        private Player _player;
        private int _posPolls;
        private static volatile bool _policyUnsupported;
        private volatile bool _interrupted;
        private volatile bool _disposed;
        private volatile string _info = "";
        private long _duration = -1;

        public TizenPlayerBackend(Window window, Func<bool> ensureTransparent, TizenPlatform platform) { _window = window; _ensureTransparent = ensureTransparent; _platform = platform; }

        private string Audio() { return _platform == null ? "?" : _platform.AudioFocusText(); }

        public event Action<PlayerFailure> Failed;
        public event Action Completed;
        public event Action<int> Buffering;

        private long _lastPos = -1;

        public long PositionMs
        {
            get
            {
                var p = _player;
                if (p == null || _disposed) return -1;
                try
                {
                    var t = Ui.Run(() => { long pos = p.GetPlayPosition(); if (++_posPolls % 5 == 1) UdpLog.Info(Tag, "state=" + p.State + " position=" + pos + " ms"); return pos; });
                    if (t.Wait(500)) _lastPos = t.Result;
                }
                catch { }
                return _lastPos;
            }
        }

        public long DurationMs { get { return _duration; } }
        public string StreamInfo { get { return _info; } }

        public async Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            // Releases the warmer's decoder BEFORE creating the real Player and switching the window to transparent (a single decoder).
            if (_platform != null) await Ui.Run(() => _platform.StopWarmerForHandoff()).ConfigureAwait(false);
            UdpLog.Info("audio", "transition to the player: focus before creating the Player = " + Audio());
            // Order: the window turns transparent BEFORE creating the Player and assigning it the display (the interface keeps showing the loading lamp, made of opaque pixels, until the first
            // frame). Measured on the TV (2026-10-17): changing the alpha with the video running, or between Prepare and Start with the display already assigned, gives PlaybackInterrupted
            // ResourceConflict; and assigning the display after Prepare is not possible (only in Idle state: InvalidOperationException). This is the only order that works.
            if (_ensureTransparent != null)
            {
                bool early = await Ui.Run(() => _ensureTransparent()).ConfigureAwait(false);
                if (early) { UdpLog.Info(Tag, "window transparent before creating the player; waiting 600 ms; audio focus = " + Audio()); await Task.Delay(600, ct).ConfigureAwait(false); }
            }
            var player = await Ui.Run(() =>
            {
                var p = new Player();
                UdpLog.Info("audio", "Player created: focus = " + Audio());
                // the Player uses the app's audio policy (the one that already holds the focus): it does not ask for its own and the TV source does not get the audio back in the gap
                var policy = _platform == null || _policyUnsupported ? null : _platform.AudioPolicy;
                if (policy != null)
                {
                    try { p.ApplyAudioStreamPolicy(policy); UdpLog.Info("audio", "Player with the app's audio policy: focus = " + Audio()); }
                    catch (Exception ex) { _policyUnsupported = true; UdpLog.Warn("audio", "ApplyAudioStreamPolicy not available on this TV (" + ex.GetType().Name + "): the Player uses its own audio stream; the app keeps its focus without releasing it"); }
                }
                else if (!_policyUnsupported) UdpLog.Warn("audio", "the app does not hold the audio focus when creating the Player");
                p.Display = new Display(_window);
                p.ErrorOccurred += (s, e) =>
                {
                    UdpLog.Warn(Tag, "ErrorOccurred " + e.Error);
                    var h = Failed;
                    if (h != null)
                    {
                        bool fatal = e.Error == PlayerError.VideoCodecNotSupported || e.Error == PlayerError.AudioCodecNotSupported || e.Error == PlayerError.NotSupportedFile;
                        string msg = fatal ? "Formato de audio/vídeo no soportado por la tele (" + e.Error + ")" : "error del reproductor: " + e.Error;
                        h(new PlayerFailure(msg, fatal));
                    }
                };
                p.BufferingProgressChanged += (s, e) =>
                {
                    if (e.Percent % 25 == 0 || e.Percent >= 100) UdpLog.Info(Tag, "buffering " + e.Percent + "%");
                    var h = Buffering; if (h != null) h(e.Percent);
                };
                p.PlaybackCompleted += (s, e) => { UdpLog.Info(Tag, "PlaybackCompleted"); var h = Completed; if (h != null) h(); };
                p.PlaybackInterrupted += (s, e) =>
                {
                    _interrupted = true;
                    UdpLog.Warn(Tag, "PlaybackInterrupted " + e.Reason + " (state=" + p.State + ")");
                    // an interruption leaves the player stopped and nobody resumes it: it is treated as a recoverable failure (the session retries right away)
                    var h = Failed;
                    if (h != null) h(new PlayerFailure("reproduccion interrumpida por el sistema (" + e.Reason + ")", false));
                };
                try { p.UserAgent = "P/0.3"; } catch (Exception ex) { UdpLog.Info(Tag, "UserAgent cannot be set: " + ex.Message); }
                p.SetSource(new MediaUriSource(url));
                return p;
            }).ConfigureAwait(false);
            _player = player;
            UdpLog.Info(Tag, "PrepareAsync " + url + " (live=" + isLive + ")");
            Task prepare;
            try { prepare = player.PrepareAsync(); }
            catch (Exception ex) { throw new PlayerFailure("PrepareAsync: " + ex.Message); }
            var cancelled = new TaskCompletionSource<bool>();
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var first = await Task.WhenAny(prepare, cancelled.Task).ConfigureAwait(false);
                if (first == cancelled.Task) { ct.ThrowIfCancellationRequested(); }
            }
            try { await prepare.ConfigureAwait(false); }
            catch (Exception ex) { throw new PlayerFailure("PrepareAsync: " + ex.GetType().Name + " " + ex.Message); }
            UdpLog.Info(Tag, "PrepareAsync finished in " + sw.ElapsedMilliseconds + " ms; audio focus = " + Audio());
            // The window turns transparent BETWEEN Prepare and Start: the TV source underneath is neither seen nor heard while preparing (the interface keeps showing the loading lamp, made of
            // opaque pixels) and the alpha does not change with the video already running (that gave PlaybackInterrupted ResourceConflict). If this order gave a conflict, the log says so.
            if (_interrupted) throw new PlayerFailure("reproduccion interrumpida por el sistema", false);
            await Ui.Run(() =>
            {
                try
                {
                    var v = player.StreamInfo.GetVideoProperties();
                    var a = player.StreamInfo.GetAudioProperties();
                    string vc = "?", ac = "?";
                    try { vc = player.StreamInfo.GetVideoCodec(); ac = player.StreamInfo.GetAudioCodec(); } catch { }
                    _info = "video " + vc + " " + v.Size.Width + "x" + v.Size.Height + " " + v.Fps + " fps " + (v.BitRate / 1000) + " kbps / audio " + ac + " " + a.SampleRate + " Hz " + a.Channels + " ch " + (a.BitRate / 1000) + " kbps";
                }
                catch (Exception ex) { _info = "streaminfo: " + ex.Message; }
                try { _duration = isLive ? -1 : player.StreamInfo.GetDuration(); } catch { _duration = -1; }
                player.Start();
            }).ConfigureAwait(false);
            UdpLog.Info(Tag, "Start; " + _info + "; audio focus = " + Audio());
            // The interface keeps showing the loading lamp (opaque pixels) until the video has its first frame; only then does the session switch to "Playing" and the video is allowed to show
            // (otherwise the TV source underneath shows for an instant). It waits for the position to advance (at most 1.5 s).
            long t0 = sw.ElapsedMilliseconds;
            long firstPos = -1;
            for (int i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                long pos = -1;
                try { var tt = Ui.Run(() => (long)player.GetPlayPosition()); if (tt.Wait(300)) pos = tt.Result; } catch { }
                if (firstPos < 0) firstPos = pos;
                if (pos > 0 && pos > firstPos || (firstPos > 0 && i >= 2)) break;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            UdpLog.Info(Tag, "first frame " + (sw.ElapsedMilliseconds - t0) + " ms after Start; audio focus = " + Audio());
            Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                UdpLog.Info("audio", "400 ms after the first frame: focus = " + Audio());
                await Task.Delay(1200).ConfigureAwait(false);
                UdpLog.Info("audio", "1.6 s after the first frame: focus = " + Audio());
            });
        }

        public void Pause() { var p = _player; if (p != null) Ui.Post(() => { try { p.Pause(); } catch (Exception ex) { UdpLog.Warn(Tag, "Pause: " + ex.Message); } }); }
        public void Resume() { var p = _player; if (p != null) Ui.Post(() => { try { p.Start(); } catch (Exception ex) { UdpLog.Warn(Tag, "Start: " + ex.Message); } }); }

        public async Task SeekAsync(long positionMs, CancellationToken ct)
        {
            var p = _player;
            if (p == null) return;
            var t = await Ui.Run(() => p.SetPlayPositionAsync((int)Math.Min(positionMs, int.MaxValue), false)).ConfigureAwait(false);
            await t.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var p = _player;
            _player = null;
            if (p == null) return;
            try
            {
                Ui.Run(() =>
                {
                    try { if (p.State == PlayerState.Playing || p.State == PlayerState.Paused) p.Stop(); } catch (Exception ex) { UdpLog.Warn(Tag, "Stop: " + ex.Message); }
                    try { if (p.State != PlayerState.Idle) p.Unprepare(); } catch (Exception ex) { UdpLog.Warn(Tag, "Unprepare: " + ex.Message); }
                    try { p.Dispose(); } catch (Exception ex) { UdpLog.Warn(Tag, "Dispose: " + ex.Message); }
                }).Wait(5000);
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "Dispose of the player: " + ex.Message); }
            UdpLog.Info(Tag, "player released");
            if (_platform != null) _platform.StartWarmer();
        }
    }
}
