using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OperationCanceledException = System.OperationCanceledException;
using Android.Content;
using Android.OS;
using AceEngine.App;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>
    /// What the two Android players (ExoPlayer, MediaPlayer) share, written once against the IPlayerBackend contract: the one-decoder-at-a-time gate, the audio focus (taken when playback starts, the
    /// player pauses or ducks when the system takes it away), the start protocol (it completes when the first frame is out), the cached position that never blocks the session, the
    /// "no event after Dispose" rule and the order of the release. The subclasses only open, control and release their own native player, always on the player thread (PlayerThread).
    ///
    /// Pausing because of the focus is invisible to PlaybackSession, which would call a position that stops a failure after 30 s and retry (taking the focus back from the other app). While the player is
    /// held paused by the system for more than a few seconds this class reports itself as buffering, which the session accepts as a reason for the position not to move.
    /// </summary>
    internal abstract class PlayerBackendBase : IPlayerBackend
    {
        protected const string Tag = "player";
        private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(6);
        private const int TickMs = 250;
        private const int FocusHeartbeatAfterMs = 15000, FocusHeartbeatEveryMs = 3000;
        private const float DuckVolume = 0.25f;
        private static readonly Handler Main = new Handler(Looper.MainLooper);

        protected readonly Context AppContext;
        protected readonly VideoFrame Video;
        private readonly AndroidAudioFocus _focus;
        private readonly CancellationTokenSource _life = new CancellationTokenSource();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private IDisposable _lease;
        private TaskCompletionSource<bool> _startTcs;
        private TaskCompletionSource<bool> _seekTcs;
        private int _prepareCalled, _disposedFlag, _ticking;
        private bool _focusSubscribed, _focusCounted;
        private long _pos = -1, _dur = -1;
        private volatile string _info = "";
        private volatile bool _started, _userPaused, _focusPaused, _focusLostForGood, _buffering;
        private long _focusPausedAtMs, _lastHeartbeatMs;
        private long _openedAtMs;
        private readonly object _noticeGate = new object();
        private readonly System.Collections.Generic.List<string> _pendingNotices = new System.Collections.Generic.List<string>();
        private bool _noticeRaised;

        protected PlayerBackendBase(Context applicationContext, VideoFrame video, AndroidAudioFocus focus)
        {
            AppContext = applicationContext; Video = video; _focus = focus;
        }

        // ================================================================ the contract: events
        public event Action<PlayerFailure> Failed;
        public event Action Completed;
        public event Action<int> Buffering;

        protected bool IsDisposed { get { return Volatile.Read(ref _disposedFlag) != 0; } }
        protected bool IsLive { get; private set; }
        protected bool IsStarted { get { return _started; } }

        protected void RaiseFailed(PlayerFailure f) { if (IsDisposed) return; var h = Failed; if (h != null) { try { h(f); } catch (Exception ex) { UdpLog.Warn(Tag, "Failed handler: " + ex.Message); } } }
        protected void RaiseCompleted() { if (IsDisposed) return; var h = Completed; if (h != null) { try { h(); } catch (Exception ex) { UdpLog.Warn(Tag, "Completed handler: " + ex.Message); } } }
        protected void RaiseBuffering(int percent) { if (IsDisposed) return; var h = Buffering; if (h != null) { try { h(percent); } catch (Exception ex) { UdpLog.Warn(Tag, "Buffering handler: " + ex.Message); } } }

        /// <summary>A failure of the native player: during the start it fails the start, afterwards it is raised as an event.</summary>
        protected void FailFromPlayer(PlayerFailure f)
        {
            var t = _startTcs;
            if (t != null && t.TrySetException(f)) return;
            RaiseFailed(f);
        }

        // ================================================================ the contract: position and info
        public long PositionMs { get { return IsDisposed || !_started ? -1 : Interlocked.Read(ref _pos); } }
        public long DurationMs { get { return IsDisposed || !_started || IsLive ? -1 : Interlocked.Read(ref _dur); } }
        public string StreamInfo { get { return _info; } }

        // ================================================================ what a subclass provides (all on the player thread)
        /// <summary>Creates the native player, gives it the video surface and the URL and starts it. Calls StartSucceeded when the first frame is out (or the stream turned out to have no picture) or FailFromPlayer.</summary>
        protected abstract void Open(string url, bool isLive);
        /// <summary>Stops and releases the native player and every listener. Must not throw. Safe to call when nothing was opened.</summary>
        protected abstract void ReleasePlayer();
        /// <summary>Position in ms, or -1 if unknown.</summary>
        protected abstract long ReadPosition();
        /// <summary>Duration in ms, or -1 if unknown.</summary>
        protected abstract long ReadDuration();
        protected abstract void SetPlaying(bool play);
        protected abstract void SetVolume(float volume);
        /// <summary>Starts a seek; the subclass calls SeekDone when the picture is at the new position.</summary>
        protected abstract void StartSeek(long positionMs);
        /// <summary>Text for StreamInfo, read when the start succeeds.</summary>
        protected abstract string DescribeStreams();
        /// <summary>The native player has no player at all yet (Open has not created it).</summary>
        protected abstract bool HasPlayer { get; }

        // ================================================================ start
        public async Task PrepareAndStartAsync(string url, bool isLive, CancellationToken ct)
        {
            if (IsDisposed) throw new OperationCanceledException();
            if (Interlocked.Exchange(ref _prepareCalled, 1) == 1) throw new PlayerFailure("PrepareAndStartAsync was already called on this player", false);
            IsLive = isLive;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token))
            {
                var token = linked.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var lease = await DecoderGate.Shared.TryAcquireAsync(GateTimeout, token).ConfigureAwait(false);
                    if (lease == null) throw new PlayerFailure("el decodificador sigue ocupado por el reproductor anterior", false);
                    Interlocked.Exchange(ref _lease, lease);
                    if (IsDisposed) { ReleaseLease(); throw new OperationCanceledException(); }
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _startTcs = tcs;
                    _focus.Changed += OnFocusChanged; _focusSubscribed = true;
                    _focus.PlayerStarted(); _focusCounted = true;
                    _openedAtMs = _clock.ElapsedMilliseconds;
                    PlayerThread.Post(() => OpenOnThread(url, isLive));
                    using (token.Register(() => tcs.TrySetCanceled()))
                        await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (PlayerFailure) { throw; }
                catch (Exception ex) { throw new PlayerFailure("error del reproductor: " + ex.GetType().Name + " " + ex.Message, false); }
            }
        }

        private void OpenOnThread(string url, bool isLive)
        {
            var t = _startTcs;
            try
            {
                if (IsDisposed) { t.TrySetCanceled(); return; }
                Open(url, isLive);
                StartTicker();
            }
            catch (PlayerFailure pf) { t.TrySetException(pf); }
            catch (Exception ex) { t.TrySetException(new PlayerFailure("error del reproductor: " + ex.GetType().Name + " " + ex.Message, false)); }
        }

        /// <summary>The native player has started (first frame out). Player thread.</summary>
        protected void StartSucceeded()
        {
            var t = _startTcs;
            if (t == null || t.Task.IsCompleted || IsDisposed) return;
            try { _info = DescribeStreams() ?? ""; } catch (Exception ex) { _info = "streaminfo: " + ex.Message; }
            long p = ReadPosition();
            Interlocked.Exchange(ref _pos, Math.Max(0, p));
            Interlocked.Exchange(ref _dur, IsLive ? -1 : ReadDuration());
            _started = true;
            UdpLog.Info(Tag, "started in " + (_clock.ElapsedMilliseconds - _openedAtMs) + " ms; " + _info);
            FlushNotices();
            t.TrySetResult(true);
        }

        /// <summary>
        /// Something the viewer has to be told about the channel that is playing ("this device cannot decode its audio"). Held back until the picture is out (the loading screen would hide it),
        /// then shown over the picture by the shell; taken away when this player goes. Any thread.
        /// </summary>
        protected void Notify(string text)
        {
            if (IsDisposed || string.IsNullOrEmpty(text)) return;
            lock (_noticeGate)
            {
                if (!_started) { if (!_pendingNotices.Contains(text)) _pendingNotices.Add(text); return; }
                _noticeRaised = true;
            }
            PlayerNotices.Raise(this, text);
        }

        private void FlushNotices()
        {
            string[] pending;
            lock (_noticeGate) { pending = _pendingNotices.ToArray(); _pendingNotices.Clear(); if (pending.Length > 0) _noticeRaised = true; }
            if (pending.Length > 0) PlayerNotices.Raise(this, string.Join("\n", pending));
        }

        // ================================================================ position ticker (player thread)
        private void StartTicker()
        {
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            PlayerThread.PostDelayed(Tick, TickMs);
        }

        private void Tick()
        {
            if (IsDisposed) return;
            try
            {
                if (_started)
                {
                    long p = ReadPosition();
                    if (p >= 0) Interlocked.Exchange(ref _pos, p);
                    if (!IsLive) { long d = ReadDuration(); if (d > 0) Interlocked.Exchange(ref _dur, d); }
                }
                long now = _clock.ElapsedMilliseconds;
                if (_focusPaused && !_userPaused && now - _focusPausedAtMs > FocusHeartbeatAfterMs && now - _lastHeartbeatMs > FocusHeartbeatEveryMs)
                {
                    _lastHeartbeatMs = now;
                    RaiseBuffering(50);          // "buffering": the session does not call a position that does not move a failure while this is recent
                }
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "tick: " + ex.Message); }
            if (!IsDisposed) PlayerThread.PostDelayed(Tick, TickMs);
        }

        /// <summary>The native player's own buffering state, for the subclass to report.</summary>
        protected void SetBuffering(bool buffering, int percent)
        {
            if (buffering) { _buffering = true; RaiseBuffering(Math.Max(0, Math.Min(99, percent))); }
            else if (_buffering) { _buffering = false; RaiseBuffering(100); }
        }

        // ================================================================ pause, resume, seek
        public void Pause()
        {
            if (IsDisposed) return;
            _userPaused = true; _focusPaused = false;
            PlaybackMonitor.SetPaused(this, true);
            PlayerThread.Post(() => { if (!IsDisposed && HasPlayer) SetPlaying(false); });
        }

        public void Resume()
        {
            if (IsDisposed) return;
            _userPaused = false; _focusPaused = false; _focusLostForGood = false;
            PlaybackMonitor.SetPaused(this, false);
            if (_started) { try { _focus.RequestAgain(); } catch (Exception ex) { UdpLog.Warn(Tag, "focus on resume: " + ex.Message); } }
            PlayerThread.Post(() => { if (!IsDisposed && HasPlayer) { SetVolume(1f); SetPlaying(true); } });
        }

        public async Task SeekAsync(long positionMs, CancellationToken ct)
        {
            if (IsDisposed || !_started || IsLive) return;               // on live the session reopens the player with ?back= instead
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _seekTcs = tcs;
            PlayerThread.Post(() =>
            {
                try { if (IsDisposed || !HasPlayer) { tcs.TrySetResult(true); return; } StartSeek(Math.Max(0, positionMs)); }
                catch (Exception ex) { tcs.TrySetException(new PlayerFailure("seek: " + ex.Message, false)); }
            });
            using (ct.Register(() => tcs.TrySetCanceled()))
            using (_life.Token.Register(() => tcs.TrySetResult(true)))
                await tcs.Task.ConfigureAwait(false);
            Interlocked.Exchange(ref _pos, Math.Max(0, positionMs));      // the cache catches up with the seek without waiting for the next tick
        }

        /// <summary>The picture is at the new position (called by the subclass on the player thread).</summary>
        protected void SeekDone() { var t = _seekTcs; if (t != null) t.TrySetResult(true); }

        // ================================================================ audio focus
        private void OnFocusChanged(AudioFocusChange change)
        {
            PlayerThread.Post(() =>
            {
                if (IsDisposed || !HasPlayer || !_started) return;
                switch (change)
                {
                    case AudioFocusChange.Duck: SetVolume(DuckVolume); break;
                    case AudioFocusChange.Gained:
                        SetVolume(1f);
                        if (_focusPaused && !_userPaused && !_focusLostForGood) { _focusPaused = false; PlaybackMonitor.SetPaused(this, false); SetPlaying(true); UdpLog.Info(Tag, "focus back: playing again"); }
                        break;
                    case AudioFocusChange.LostTransient:
                    case AudioFocusChange.Lost:
                        if (change == AudioFocusChange.Lost) _focusLostForGood = true;
                        if (!_userPaused && !_focusPaused)
                        {
                            _focusPaused = true; _focusPausedAtMs = _clock.ElapsedMilliseconds; _lastHeartbeatMs = _focusPausedAtMs;
                            PlaybackMonitor.SetPaused(this, true);
                            SetPlaying(false);
                            UdpLog.Info(Tag, "paused: another app took the audio focus (" + change + ")" + (change == AudioFocusChange.Lost ? "; it stays paused until the user resumes" : ""));
                        }
                        break;
                }
            });
        }

        // ================================================================ video shape
        /// <summary>The picture's shape changed (width / height with the pixel shape): the video surface follows it. Any thread.</summary>
        protected void SetVideoAspect(float aspect)
        {
            Main.Post(() => { try { Video.SetAspect(aspect); } catch (Exception ex) { UdpLog.Warn(Tag, "video aspect: " + ex.Message); } });
        }

        // ================================================================ dispose
        private void ReleaseLease() { var l = Interlocked.Exchange(ref _lease, null); if (l != null) l.Dispose(); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1) return;
            bool hadNotice;
            lock (_noticeGate) { hadNotice = _noticeRaised; _pendingNotices.Clear(); }
            if (hadNotice) PlayerNotices.Clear(this);
            PlaybackMonitor.Gone(this);
            try { _life.Cancel(); } catch (Exception) { }
            Interlocked.Exchange(ref _pos, -1);
            bool released = PlayerThread.Run(() => { try { ReleasePlayer(); } catch (Exception ex) { UdpLog.Warn(Tag, "release: " + ex.Message); } }, 5000);
            if (!released) UdpLog.Warn(Tag, "the player did not release within 5 s");
            if (_focusSubscribed) { _focus.Changed -= OnFocusChanged; _focusSubscribed = false; }
            // the decoder is free only once the native player has let go of it
            ReleaseLease();
            if (_focusCounted) { _focusCounted = false; _focus.PlayerStopped(); }
            UdpLog.Info(Tag, "player released");
        }
    }
}
