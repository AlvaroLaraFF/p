using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.App
{
    public sealed class PlaybackOptions
    {
        /// <summary>Retries after ErrorOccurred on a live stream (3, waiting 2 s between them).</summary>
        public int MaxLiveRetries = 3;
        /// <summary>On VOD: one retry from the last known position.</summary>
        public int MaxVodRetries = 1;
        /// <summary>Retries of the link resolution (ResolveException) before giving up.</summary>
        public int MaxResolveRetries = 2;
        public TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        /// <summary>Maximum time to wait for the engine to have the prebuffer ready (even if data trickles in). It used to be 90 s.</summary>
        public TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
        /// <summary>
        /// User rule: when a channel is opened, if NOTHING has arrived within this time (resolving the descriptor + first data), it stops and shows "Sin fuentes por ahora" instead of loading forever.
        /// Measured: for the channels that do play, the first data arrives in 2.5 s median, 5 s at p90 and only one in 75 takes over 15 s. Zero = no such notice.
        /// </summary>
        public TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
        /// <summary>
        /// User rule: a channel without sources (nothing within OpenTimeout, or an unresolved descriptor) shows "Sin fuentes por ahora" and KEEPS retrying on its own, in a loop, until a stream arrives
        /// (and then it plays) or the user leaves with Back. false = after the notice the playback ends in error (for tests and for the CLI).
        /// </summary>
        public bool KeepRetryingWithoutSource = true;
        /// <summary>Breather between a source-less attempt and the next (each attempt already lasts OpenTimeout, so this is only a minimum).</summary>
        public TimeSpan NoSourceRetryDelay = TimeSpan.FromSeconds(1);
        /// <summary>Maximum time for PrepareAsync + Start.</summary>
        public TimeSpan PrepareTimeout = TimeSpan.FromSeconds(15);
        /// <summary>If the position does not advance in this time (and the player is not loading) it is treated as a failure.</summary>
        public TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);
        /// <summary>Warning if the most recent live piece does not advance in this time (the source has stopped: the player would repeat the same chunk).</summary>
        public TimeSpan StaleLiveTimeout = TimeSpan.FromSeconds(45);
        /// <summary>
        /// While a live stream plays, how often the checker is reminded that the stream keeps arriving (renews its 10 min verification: on going back to the list, the card is not probed
        /// again). Only reported if the live piece has advanced in the last few seconds. Zero = only reported when the stream arrives.
        /// </summary>
        public TimeSpan StreamingRenewal = TimeSpan.FromSeconds(60);

        // ---------- Live with memory (timeshift); only counts if the engine has LiveOptions.TimeshiftBytes > 0 ----------

        /// <summary>How long the player takes to show after Start, relative to the first byte served (estimate of the delay behind live).</summary>
        public TimeSpan TimeshiftPlayerLatency = TimeSpan.FromMilliseconds(1000);
        /// <summary>When seeking back, this stretch is left free at the old end of the window: eviction continues while the player prepares and must not reach its cursor.</summary>
        public TimeSpan TimeshiftSeekGuard = TimeSpan.FromSeconds(3);
        /// <summary>When resuming from a pause it goes back to live if the cursor is within this of being evicted, or if the picture is further back than the window plus this tolerance.</summary>
        public TimeSpan TimeshiftResumeGuard = TimeSpan.FromSeconds(1.5);
        public TimeSpan TimeshiftResumeTolerance = TimeSpan.FromSeconds(2);
        /// <summary>Media seconds per real second: 1 in practice. Only the tests with the accelerated simulated sender change it.</summary>
        public double TimeshiftMediaRate = 1.0;
        public int? FileIndex;
        public int? QualityIndex;
    }

    /// <summary>The live stream has no sources (nobody connectable serves the channel right now); it is not a player failure and it is not retried on its own.</summary>
    public sealed class NoSourceException : TimeoutException
    {
        public NoSourceException(string message) : base(message) { }
    }

    /// <summary>What the playback screen needs from a playback (implemented by PlaybackSession; a fake one in tests).</summary>
    public interface IPlayback : IDisposable
    {
        PlaybackStatus Status { get; }
        event Action<PlaybackStatus> Changed;
        Task RunAsync(LinkEntry entry, string name, CancellationToken ct);
        void Stop();
        void TogglePause();
        /// <summary>Relative seek on VOD (does nothing on live: see SeekBehindLiveAsync).</summary>
        Task SeekRelativeAsync(long deltaMs);

        // ---------- Live with memory: pause, rewind, +/-10 s and LIVE, limited to what is in the buffer of what was already served ----------
        /// <summary>What is stored behind live (the "window"); zero if there is nothing or it is not a live stream.</summary>
        TimeSpan TimeshiftWindow { get; }
        /// <summary>Current distance from what is shown to live (zero = at live).</summary>
        TimeSpan BehindLive { get; }
        /// <summary>Moves playback relative to live: negative delta = back (-10 s), positive = forward (+10 s). Stays within the window; if it reaches live, it goes back to it.</summary>
        Task SeekBehindLiveAsync(TimeSpan delta);
        /// <summary>Goes back to live (LIVE button).</summary>
        Task GoLiveAsync();
        /// <summary>Extra engine information for the log (live: piece, lag, signatures...).</summary>
        string EngineDetail { get; }
        /// <summary>Most recent live piece known to the engine (to check that the live stream advances); -1 if not applicable.</summary>
        long EngineLivePos { get; }
    }

    /// <summary>
    /// A playback from start to finish: opens the link in the engine, waits for the prebuffer, opens the platform player
    /// on the local URL, watches the state and retries. It knows nothing about Tizen: the player comes in through IPlayerFactory.
    /// </summary>
    public sealed class PlaybackSession : IPlayback
    {
        private const string Tag = "playback";
        private readonly AceEngineHost _host;
        private readonly IPlayerFactory _factory;
        private readonly PlaybackOptions _opt;
        private readonly IPlaybackReporter _reporter;
        private LinkEntry _entry;                                // the channel being opened (to notify the checker)
        private readonly object _gate = new object();
        private PlaybackStatus _status = new PlaybackStatus();
        private CancellationTokenSource _cts;
        private IPlayerBackend _player;
        private IStreamSession _session;
        private volatile bool _disposed;
        private volatile bool _waiting;                          // no sources and retrying on its own: the phase stays in Error with the notice while this lasts
        private long _lastPositionMs;

        // ---- live with memory ----
        private readonly BehindLiveTracker _behind;
        private ReopenRequest _pendingReopen;                    // request to reopen the player (seek, LIVE, resume outside the window); under _gate
        private ReopenRequest _activeReopen;                     // the one being served (so that consecutive seeks accumulate); under _gate
        private TaskCompletionSource<bool> _wake;                // wakes the watch loop; under _gate
        private volatile bool _connectionLost;                   // the player lost the connection while paused: on resume it must be reopened

        /// <summary>Reopen the player on the same engine session: Back = how far behind the live point (null = live).</summary>
        private sealed class ReopenRequest
        {
            public TimeSpan? Back;
            public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public event Action<PlaybackStatus> Changed;

        /// <summary>The tuning this session runs with (tests).</summary>
        internal PlaybackOptions Options { get { return _opt; } }

        /// <param name="reporter">Whoever learns what the player sees of the channel (the channel checker): stream arrives, no sources, live stopped. Null = nobody.</param>
        public PlaybackSession(AceEngineHost host, IPlayerFactory factory, PlaybackOptions options = null, IPlaybackReporter reporter = null)
        {
            _host = host; _factory = factory; _opt = options ?? new PlaybackOptions(); _reporter = reporter;
            _behind = new BehindLiveTracker(_opt.TimeshiftMediaRate);
        }

        public PlaybackStatus Status { get { lock (_gate) return _status.Clone(); } }
        public IStreamSession EngineSession { get { return _session; } }

        public long EngineLivePos { get { var ls = _session as LiveSession; return ls == null ? -1 : ls.LiveInfo.LivePos; } }

        public string EngineDetail
        {
            get
            {
                var ls = _session as LiveSession;
                if (ls == null) return "";
                var li = ls.LiveInfo;
                string mem = "";
                if (ls.TimeshiftEnabled)
                {
                    var t = ls.GetTimeshift();
                    mem = " timeshift{window=" + t.Window.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s stored=" + (t.BufferedBytes >> 10) + "KiB/" + (t.BudgetBytes >> 10) + "KiB behind="
                        + (_behind.ReportedMs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s}";
                }
                return "live{piece=" + li.LivePos + " lag=" + li.LagSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s pieces=" + li.StoredPieces + " sigs=" + li.PiecesVerified + "/" + li.SignatureFailures
                    + " holes=" + li.HolesSkipped + " rehooks=" + li.Rehooks + " discarded=" + (li.DiscardedBytes >> 10) + "KiB}" + mem;
            }
        }

        /// <summary>Tells the checker what the player has seen of the open channel. Never throws or blocks; channels that are not the engine's (http) are not reported.</summary>
        private void Report(PlaybackSignal signal, string detail, int peers = 0)
        {
            var r = _reporter;
            var e = _entry;
            if (r == null || e == null || e.Link == null || !e.IsProbeable) return;
            try { r.Report(new PlaybackEvidence { Entry = e, Signal = signal, Detail = detail, Peers = peers }); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Warn(Tag, "notifying the checker: " + ex.Message); }
        }

        private void Update(Action<PlaybackStatus> change)
        {
            PlaybackStatus snap;
            lock (_gate)
            {
                var s = _status.Clone();
                change(s);
                if (_waiting)
                {
                    // No sources and retrying: the screen keeps showing the notice (it does not go back to loading on each attempt) until a stream arrives and the player is prepared
                    if (s.Phase == PlayPhase.Preparing || s.Phase == PlayPhase.Playing) _waiting = false;
                    else if (s.Phase == PlayPhase.Resolving || s.Phase == PlayPhase.Connecting || s.Phase == PlayPhase.Buffering || s.Phase == PlayPhase.Retrying)
                    { s.Phase = PlayPhase.Error; s.Error = NoSourceMessage; s.ErrorIsFatal = false; }
                }
                _status = s;
                snap = s.Clone();
            }
            var h = Changed;
            if (h != null) { try { h(snap); } catch (Exception ex) { AceLog.Warn(Tag, "Changed: " + ex.Message); } }
        }

        private void SetPhase(PlayPhase p, string error = null, bool fatal = false)
        {
            Update(s => { s.Phase = p; s.Error = error; s.ErrorIsFatal = fatal; });
        }

        /// <summary>Cancels the playback (Back): stops the engine session and the player.</summary>
        public void Stop()
        {
            CancellationTokenSource c;
            lock (_gate) c = _cts;
            if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
        }

        /// <summary>
        /// Pause. On VOD it pauses the player. On a live stream with memory it pauses the player and NOT the engine pump: the flow keeps entering the circular buffer
        /// and the delay behind live grows while it lasts. On a live stream without memory it does nothing (there is nothing to store).
        /// </summary>
        public void Pause()
        {
            var p = _player;
            var st = Status;
            if (p == null) return;
            bool tsLive = st.IsLive && TimeshiftSession != null;
            if (st.IsLive && !tsLive) return;
            if (tsLive && st.Phase != PlayPhase.Playing) return;
            try
            {
                p.Pause();
                if (tsLive) { _behind.SetAdvancing(false, NowMs()); SetHold(true); }
                Update(s => { if (s.Phase == PlayPhase.Playing) s.Phase = PlayPhase.Paused; });
            }
            catch (Exception ex) { AceLog.Warn(Tag, "Pause: " + ex.Message); }
        }

        /// <summary>
        /// Resume. On a live stream with memory: if what was being watched is still in the window (and the player cursor is not about to be
        /// evicted) it continues where it was; if the pause lasted longer than the window, or the player lost the connection, it reopens (live, or the same position).
        /// </summary>
        public void Resume()
        {
            var p = _player;
            if (p == null) return;
            var ts = TimeshiftSession;
            if (ts != null && Status.IsLive && Status.Phase == PlayPhase.Paused)
            {
                var snap = ts.GetTimeshift();
                long now = NowMs();
                double behind = _behind.PeekRawMs(now, snap.LiveMs, snap.ReaderId, snap.ReaderCursorMs);
                double windowMs = snap.Window.TotalMilliseconds;
                bool ours = snap.ReaderId == _behind.ReaderId;
                bool cursorLost = ours && (snap.ReaderEvicted || snap.ReaderCursorMs - snap.OldestMs < _opt.TimeshiftResumeGuard.TotalMilliseconds);
                if (cursorLost || behind > windowMs + _opt.TimeshiftResumeTolerance.TotalMilliseconds)
                {
                    AceLog.Info(Tag, "resume: the pause (" + (behind / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s) exceeds the window (" + (windowMs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s): back to live");
                    lock (_gate) { if (_cts != null) RequestReopenLocked(null); }
                    return;
                }
                if (_connectionLost)
                {
                    AceLog.Info(Tag, "resume: the player lost the connection during the pause; reopening at the same position (" + (behind / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s)");
                    lock (_gate) { if (_cts != null) RequestReopenLocked(BackFor(behind)); }
                    return;
                }
                _behind.SetAdvancing(true, now);
                SetHold(false);
            }
            try { p.Resume(); Update(s => { if (s.Phase == PlayPhase.Paused) s.Phase = PlayPhase.Playing; }); } catch (Exception ex) { AceLog.Warn(Tag, "Resume: " + ex.Message); }
        }

        public void TogglePause()
        {
            if (Status.Phase == PlayPhase.Paused) Resume(); else Pause();
        }

        /// <summary>Relative seek on VOD (left/right: +/-30 s).</summary>
        public async Task SeekRelativeAsync(long deltaMs)
        {
            var p = _player;
            var st = Status;
            if (p == null || st.IsLive || st.PositionMs < 0) return;
            long target = Math.Max(0, st.PositionMs + deltaMs);
            if (st.DurationMs > 0) target = Math.Min(target, Math.Max(0, st.DurationMs - 1000));
            try { await p.SeekAsync(target, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AceLog.Warn(Tag, "Seek: " + ex.Message); }
        }

        // ---------- Live with memory ----------

        private static long NowMs() { return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency; }

        /// <summary>The engine session if it is a live stream with memory (LiveOptions.TimeshiftBytes &gt; 0); otherwise null.</summary>
        private LiveSession TimeshiftSession
        {
            get { var ls = _session as LiveSession; return ls != null && ls.TimeshiftEnabled ? ls : null; }
        }

        /// <summary>What is stored behind the live point (zero if it is not a live stream with memory).</summary>
        public TimeSpan TimeshiftWindow
        {
            get { var ts = TimeshiftSession; return ts == null ? TimeSpan.Zero : ts.GetTimeshift().Window; }
        }

        /// <summary>Estimated distance from what is shown to live (zero = at live), capped at the window.</summary>
        public TimeSpan BehindLive
        {
            get
            {
                var ts = TimeshiftSession;
                if (ts == null || !_behind.Started) return TimeSpan.Zero;
                return TimeSpan.FromMilliseconds(Math.Min(_behind.ReportedMs, ts.GetTimeshift().Window.TotalMilliseconds));
            }
        }

        /// <summary>During a pause the local server must not stop the session for inactivity even if the player loses the connection.</summary>
        private void SetHold(bool hold)
        {
            var ls = _session as LiveSession;
            if (ls != null) ls.HoldOpen = hold;
        }

        private static TimeSpan? BackFor(double ms)
        {
            return ms <= BehindLiveTracker.LiveMarginMs ? (TimeSpan?)null : TimeSpan.FromMilliseconds(Math.Round(ms));
        }

        /// <summary>
        /// Moves playback relative to live: negative delta = back (-10 s), positive = forward. The target is the current delay minus delta, clamped
        /// to [0, window - margin]; if it ends up within 2 s of live it is equivalent to LIVE. Reopens the player on the same engine session with
        /// ?back=ms (the player's Prepare takes 0.4-1.7 s: it shows as Preparing and does not count as a failure). Consecutive presses accumulate on the
        /// pending target. The task finishes when the new player is playing (or could not).
        /// </summary>
        public Task SeekBehindLiveAsync(TimeSpan delta)
        {
            var ts = TimeshiftSession;
            if (ts == null) return Task.CompletedTask;
            var snap = ts.GetTimeshift();
            double seekable = Math.Max(0, snap.Window.TotalMilliseconds - _opt.TimeshiftSeekGuard.TotalMilliseconds);
            lock (_gate)
            {
                if (_cts == null) return Task.CompletedTask;
                var inflight = _pendingReopen ?? _activeReopen;
                if (inflight == null && _status.Phase != PlayPhase.Playing && _status.Phase != PlayPhase.Paused) return Task.CompletedTask;
                double cur = inflight != null ? (inflight.Back.HasValue ? inflight.Back.Value.TotalMilliseconds : 0) : (_behind.Started ? _behind.ReportedMs : 0);
                if (delta < TimeSpan.Zero && cur >= seekable) return Task.CompletedTask;                  // already at the limit of what is stored
                double target = Math.Max(0, Math.Min(seekable, cur - delta.TotalMilliseconds));
                var back = BackFor(target);
                if (!back.HasValue) return GoLiveLocked();
                return RequestReopenLocked(back);
            }
        }

        /// <summary>Goes back to live (LIVE button). If already at live and playing it does nothing.</summary>
        public Task GoLiveAsync()
        {
            if (TimeshiftSession == null) return Task.CompletedTask;
            lock (_gate)
            {
                if (_cts == null) return Task.CompletedTask;
                return GoLiveLocked();
            }
        }

        private Task GoLiveLocked()
        {
            var inflight = _pendingReopen ?? _activeReopen;
            if (inflight == null)
            {
                if (_status.Phase != PlayPhase.Playing && _status.Phase != PlayPhase.Paused) return Task.CompletedTask;       // nothing to move yet
                if (_behind.AtLive && _status.Phase == PlayPhase.Playing) return Task.CompletedTask;                          // already at live
            }
            return RequestReopenLocked(null);
        }

        private Task RequestReopenLocked(TimeSpan? back)
        {
            var r = _pendingReopen;
            if (r == null) { r = new ReopenRequest(); _pendingReopen = r; }
            r.Back = back;                                          // several requests before the first one is served merge into the last
            var w = _wake;
            if (w != null) w.TrySetResult(true);
            return r.Done.Task;
        }

        private void CompleteReopens(bool ok)
        {
            ReopenRequest a, p;
            lock (_gate) { a = _activeReopen; p = _pendingReopen; _activeReopen = null; _pendingReopen = null; }
            if (a != null) a.Done.TrySetResult(ok);
            if (p != null) p.Done.TrySetResult(ok);
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        /// <summary>Opens and plays the link. Finishes when stopped, when it ends (VOD) or when it fails for good (the cause stays in Status).</summary>
        public async Task RunAsync(LinkEntry entry, string name, CancellationToken external)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(external))
            {
                lock (_gate) _cts = cts;
                var ct = cts.Token;
                _entry = entry;
                Update(s => { s.Name = name ?? entry.Name ?? ""; s.Phase = PlayPhase.Resolving; s.Error = null; s.Retry = 0; s.ErrorIsFatal = false; s.TimeshiftWindowMs = 0; s.BehindLiveMs = 0; });
                try
                {
                    while (true)
                    {
                        try { await RunCoreAsync(entry, ct).ConfigureAwait(false); break; }
                        catch (Exception ex) when (_opt.KeepRetryingWithoutSource && !ct.IsCancellationRequested && (ex is NoSourceException || ex is ResolveException))
                        {
                            // No sources for now: notify and retry on its own, in a loop, until a stream arrives or the user leaves
                            AceLog.Warn(Tag, "no sources (" + ex.Message + "); retrying");
                            Report(PlaybackSignal.NoSource, ex.Message);
                            await CleanupAsync().ConfigureAwait(false);
                            _waiting = true;
                            Update(s => { s.Phase = PlayPhase.Error; s.Error = NoSourceMessage; s.ErrorIsFatal = false; s.Retry++; s.Peers = 0; s.BufferPercent = 0; });
                            await Task.Delay(_opt.NoSourceRetryDelay, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { if (Status.Phase != PlayPhase.Error || _waiting) { _waiting = false; SetPhase(PlayPhase.Idle); } }
                catch (Exception ex)
                {
                    AceLog.Warn(Tag, "playback failed: " + ex.GetType().Name + ": " + ex.Message);
                    if (ex is NoSourceException || ex is ResolveException) Report(PlaybackSignal.NoSource, ex.Message);
                    SetPhase(PlayPhase.Error, Describe(ex), IsFatal(ex));
                }
                finally
                {
                    await CleanupAsync().ConfigureAwait(false);
                    lock (_gate) _cts = null;
                    CompleteReopens(false);
                    _behind.Reset();
                    _connectionLost = false;
                }
            }
        }

        private static bool IsFatal(Exception ex)
        {
            return ex is NotSupportedException || ex is ArgumentException || ex is FormatException || (ex is PlayerFailure && ((PlayerFailure)ex).Fatal);
        }

        /// <summary>Text of the notice for a live stream without sources (short: the error screen shows it as is).</summary>
        public const string NoSourceMessage = "Sin fuentes por ahora";

        /// <summary>Understandable message for the screen.</summary>
        public static string Describe(Exception ex)
        {
            if (ex is NoSourceException || ex is ResolveException) return NoSourceMessage;
            if (ex is NotSupportedException) return "Enlace no soportado: " + ex.Message;
            if (ex is TimeoutException) return ex.Message;
            return ex.Message;
        }

        private async Task RunCoreAsync(LinkEntry entry, CancellationToken ct)
        {
            string url = null;
            bool isLive = false;
            bool direct = false;
            int failures = 0;
            int resolveFailures = 0;
            var openStart = DateTime.UtcNow;                         // the OpenTimeout deadline counts from when opening the channel is requested
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (url == null && failures > 0) openStart = DateTime.UtcNow;
                // ---- 1. open in the engine (or use the URL as is) ----
                if (url == null)
                {
                    SetPhase(PlayPhase.Resolving);
                    if (entry.Kind == LinkKind.HttpStream)
                    {
                        url = entry.Url; direct = true; isLive = false;
                        Update(s => s.IsLive = false);
                    }
                    else
                    {
                        if (entry.Link == null) throw new NotSupportedException("el enlace no es de Ace Stream");
                        OpenedStream opened;
                        using (var octs = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            if (_opt.OpenTimeout > TimeSpan.Zero) octs.CancelAfter(RemainingOpen(openStart));
                            try { opened = await _host.OpenStreamAsync(entry.Link, new OpenOptions { FileIndex = _opt.FileIndex, QualityIndex = _opt.QualityIndex }, octs.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new NoSourceException("the channel descriptor was not resolved within " + (int)_opt.OpenTimeout.TotalSeconds + " s");
                            }
                            catch (ResolveException rex) when (resolveFailures < _opt.MaxResolveRetries && (_opt.OpenTimeout <= TimeSpan.Zero || RemainingOpen(openStart) > _opt.RetryDelay + TimeSpan.FromSeconds(2)))
                            {
                                // A cold DHT may take a while: the resolution is retried while there is OpenTimeout left.
                                resolveFailures++;
                                AceLog.Warn(Tag, "resolution failed (" + resolveFailures + "/" + _opt.MaxResolveRetries + "): " + rex.Message);
                                Update(s => { s.Phase = PlayPhase.Retrying; s.Retry = resolveFailures; s.Error = rex.Message; });
                                await Task.Delay(_opt.RetryDelay, ct).ConfigureAwait(false);
                                continue;
                            }
                        }
                        url = opened.Url;
                        direct = opened.IsDirect;
                        _session = opened.Session;
                        isLive = opened.Session != null && opened.Session.IsLive;
                        string sname = opened.Session is LiveSession ? ((LiveSession)opened.Session).Name : null;
                        string trustNote = opened.Trust == null ? null : opened.Trust.Note;
                        if (opened.Trust != null && opened.Trust.Level == TrustLevel.Unconfirmed) AceLog.Debug(Tag, "[trust] unconfirmed " + entry.Name + " (" + (trustNote ?? "single peer") + ")");     // debugging only: the UI does not show it
                        string tname = opened.Transport == null ? null : opened.Transport.Name;
                        if (!string.IsNullOrEmpty(tname) && !string.IsNullOrEmpty(entry.Name) && !NameCheck.Plausible(entry.Name, tname))
                        {
                            AceLog.Warn(Tag, "the channel name in its descriptor ('" + tname + "') does not look like the one in the list ('" + entry.Name + "')");
                            trustNote = (trustNote == null ? "" : trustNote + ". ") + "El nombre del canal (\"" + (tname.Length > 40 ? tname.Substring(0, 40) + "…" : tname) + "\") no coincide con el de la lista";
                        }
                        Update(s => { s.IsLive = isLive; s.TrustNote = trustNote; if (string.IsNullOrEmpty(s.Name) && sname != null) s.Name = sname; });
                        AceLog.Info(Tag, "opened " + (isLive ? "live" : direct ? "direct-url" : "VOD") + " -> " + url);
                    }
                }
                // ---- 2. wait for the engine prebuffer ----
                // A requested reopen (seek, LIVE...) reuses the session that is already playing: there is no other prebuffer to wait for
                // (the circular buffer has the data even if the swarm is loading).
                ReopenRequest req;
                lock (_gate) { req = _pendingReopen; _pendingReopen = null; _activeReopen = req; }
                var tsLive = isLive ? TimeshiftSession : null;
                if (req != null && tsLive == null) { CompleteReopens(false); req = null; }
                if (_session != null && (req == null || _session.State == SessionState.Failed || _session.State == SessionState.Stopped)) await WaitReadyAsync(_session, openStart, ct).ConfigureAwait(false);

                // ---- 3. player ----
                string failure = null;
                bool fatal = false;
                bool reopening = false;
                TimeSpan? back = req != null ? req.Back : null;                  // without a request (start or retry after a failure) always at live
                string playUrl = url;
                if (tsLive != null && back.HasValue)
                    playUrl = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "back=" + ((long)back.Value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
                SetHold(false);
                if (req != null) AceLog.Info(Tag, "reopening the player " + (back.HasValue ? (back.Value.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s behind live" : "at live"));
                try
                {
                    SetPhase(PlayPhase.Preparing);
                    var tcsFail = new TaskCompletionSource<PlayerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var tcsEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var player = _factory.Create();
                    _player = player;
                    long lastBufferTick = Environment.TickCount;
                    player.Failed += f => tcsFail.TrySetResult(f);
                    player.Completed += () => tcsEnd.TrySetResult(true);
                    player.Buffering += pc =>
                    {
                        Interlocked.Exchange(ref lastBufferTick, Environment.TickCount);
                        Update(s => s.PlayerBufferPercent = pc);
                    };
                    using (var prepCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        prepCts.CancelAfter(_opt.PrepareTimeout);
                        try { await player.PrepareAndStartAsync(playUrl, isLive, prepCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PlayerFailure("el reproductor no ha terminado de preparar en " + (int)_opt.PrepareTimeout.TotalSeconds + " s"); }
                    }
                    if (!isLive && _lastPositionMs > 5000 && failures > 0)
                    {
                        try { await player.SeekAsync(_lastPositionMs, ct).ConfigureAwait(false); } catch (Exception ex) when (!(ex is OperationCanceledException)) { AceLog.Warn(Tag, "seek to the last position: " + ex.Message); }
                    }
                    _connectionLost = false;
                    if (tsLive != null)
                    {
                        // the picture starts at the first media byte that the server served to this connection (including the alignment to a random access point)
                        var snap0 = tsLive.GetTimeshift();
                        double startMs = snap0.ReaderId != 0 ? snap0.ReaderStartMs : snap0.LiveMs - (back.HasValue ? back.Value.TotalMilliseconds : 0);
                        bool atLive = !back.HasValue || (snap0.ReaderId != 0 && snap0.ReaderStartBehindMs <= BehindLiveTracker.LiveMarginMs);
                        _behind.Start(snap0.ReaderId, startMs, snap0.LiveMs, atLive, NowMs(), _opt.TimeshiftPlayerLatency.TotalMilliseconds);
                    }
                    Update(s => { s.Phase = PlayPhase.Playing; s.StreamInfo = player.StreamInfo; s.DurationMs = player.DurationMs; s.Error = null; s.PlayerBufferPercent = -1; });
                    AceLog.Info(Tag, "playing: " + player.StreamInfo);
                    if (req != null) { lock (_gate) { if (_activeReopen == req) _activeReopen = null; } req.Done.TrySetResult(true); }

                    // ---- 4. watch ----
                    long lastPos = -1;
                    long lastAdvance = Environment.TickCount;
                    long lastLivePos = -1, lastLiveChange = Environment.TickCount;
                    long lastStreamReport = Environment.TickCount;
                    var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate) { _wake = wake; if (_pendingReopen != null) wake.TrySetResult(true); }
                    try
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            var delay = Task.Delay(_opt.StatusInterval, ct);
                            var done = await Task.WhenAny(delay, tcsFail.Task, tcsEnd.Task, wake.Task).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            if (done == wake.Task) { reopening = true; break; }              // seek, LIVE or resume outside the window: the player is opened again
                            if (done == tcsFail.Task)
                            {
                                var f = tcsFail.Task.Result;
                                if (tsLive != null && Status.Phase == PlayPhase.Paused)
                                {
                                    // while paused the player may lose the connection (the server cuts when the cursor is evicted): it is not a failure, it is reopened on resume
                                    AceLog.Info(Tag, "the player failed while paused (" + f.Message + "): it will be reopened on resume");
                                    _connectionLost = true;
                                    tcsFail = new TaskCompletionSource<PlayerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
                                    continue;
                                }
                                failure = f.Message; fatal = f.Fatal; break;
                            }
                            if (done == tcsEnd.Task)
                            {
                                if (isLive)
                                {
                                    if (tsLive != null && Status.Phase == PlayPhase.Paused)
                                    {
                                        AceLog.Info(Tag, "the player ended while paused: it will be reopened on resume");
                                        _connectionLost = true;
                                        tcsEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        continue;
                                    }
                                    failure = "el reproductor ha terminado el directo"; break;
                                }
                                SetPhase(PlayPhase.Ended);
                                return;
                            }
                            long pos = player.PositionMs;
                            var sess = _session;
                            SessionStats stats = sess != null ? sess.Stats : null;
                            bool paused = Status.Phase == PlayPhase.Paused;
                            bool buffering = Environment.TickCount - Interlocked.Read(ref lastBufferTick) < 5000 && Status.PlayerBufferPercent >= 0 && Status.PlayerBufferPercent < 100;
                            long tsWindowMs = 0, behindMs = 0;
                            if (tsLive != null)
                            {
                                var snap = tsLive.GetTimeshift();
                                long now = NowMs();
                                if (snap.ReaderId != 0 && snap.ReaderId != _behind.ReaderId)
                                    _behind.Start(snap.ReaderId, snap.ReaderStartMs, snap.LiveMs, snap.ReaderStartBehindMs <= BehindLiveTracker.LiveMarginMs, now, _opt.TimeshiftPlayerLatency.TotalMilliseconds);   // the player opened another connection on its own
                                _behind.SetAdvancing(!paused && !buffering && Status.Phase == PlayPhase.Playing, now);
                                double reported = _behind.Update(now, snap.LiveMs, snap.Window.TotalMilliseconds, snap.ReaderId, snap.ReaderCursorMs);
                                tsWindowMs = (long)snap.Window.TotalMilliseconds;
                                behindMs = (long)Math.Min(reported, snap.Window.TotalMilliseconds);
                            }
                            Update(s =>
                            {
                                s.PositionMs = pos;
                                if (stats != null) { s.Peers = stats.Peers; s.Seeds = stats.Seeds; s.DownBytesPerSec = stats.DownloadBytesPerSec; s.UpBytesPerSec = stats.UploadBytesPerSec; s.BufferPercent = stats.BufferPercent; }
                                if (tsLive != null) { s.TimeshiftWindowMs = tsWindowMs; s.BehindLiveMs = behindMs; }
                            });
                            if (pos >= 0) _lastPositionMs = pos;
                            var liveSess = sess as LiveSession;
                            if (liveSess != null)
                            {
                                long lp = liveSess.LiveInfo.LivePos;
                                if (lp != lastLivePos) { lastLivePos = lp; lastLiveChange = Environment.TickCount; Update(s => s.Warning = null); }
                                else if (!paused && Environment.TickCount - lastLiveChange > _opt.StaleLiveTimeout.TotalMilliseconds && Status.Warning == null)
                                {
                                    AceLog.Warn(Tag, "the live stream has received no new pieces for " + (int)_opt.StaleLiveTimeout.TotalSeconds + " s (piece " + lp + ")");
                                    Update(s => s.Warning = "El canal no recibe datos nuevos (parece detenido)");
                                    Report(PlaybackSignal.Stalled, "no new pieces for " + (int)_opt.StaleLiveTimeout.TotalSeconds + " s");
                                }
                                if (!paused && _opt.StreamingRenewal > TimeSpan.Zero && Environment.TickCount - lastStreamReport > _opt.StreamingRenewal.TotalMilliseconds && Environment.TickCount - lastLiveChange < 10000)
                                {
                                    // the stream keeps arriving (the live piece advanced recently): the channel verification is renewed, and on going back to the list it need not be probed again
                                    lastStreamReport = Environment.TickCount;
                                    Report(PlaybackSignal.Streaming, "still arriving", stats != null ? stats.Peers : 0);
                                }
                            }
                            // Stopped is not stalled: during a pause the position does not move and, on resume, the deadline starts from zero
                            if (pos != lastPos || paused) { lastPos = pos; lastAdvance = Environment.TickCount; }
                            if (!paused && !buffering && Environment.TickCount - lastAdvance > _opt.StallTimeout.TotalMilliseconds)
                            {
                                failure = "la reproduccion no avanza desde hace " + (int)_opt.StallTimeout.TotalSeconds + " s";
                                // if in addition the live stream receives no new pieces, it is the channel (not the player): the checker is notified
                                if (liveSess != null && Environment.TickCount - lastLiveChange > _opt.StallTimeout.TotalMilliseconds) Report(PlaybackSignal.Stalled, "playback does not advance and no new pieces arrive");
                                break;
                            }
                            if (sess != null && (sess.State == SessionState.Failed || sess.State == SessionState.Stopped) && !_disposed)
                            {
                                failure = "la sesion del motor se ha parado (" + sess.State + ")"; fatal = false;
                                if (sess.State == SessionState.Failed) Report(PlaybackSignal.SessionLost, "the engine session failed");      // Stopped may be the local server closing for inactivity: it is not the channel's fault
                                break;
                            }
                        }
                    }
                    finally { lock (_gate) { if (_wake == wake) _wake = null; } }
                }
                catch (PlayerFailure pf) { failure = pf.Message; fatal = pf.Fatal; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (!(ex is NotSupportedException)) { failure = ex.GetType().Name + ": " + ex.Message; }
                finally { await DisposePlayerAsync().ConfigureAwait(false); }

                if (reopening && failure == null)
                {
                    // Reopen requested: it is not a player failure nor does it spend retries; it goes back to step 3 on the same engine session
                    _behind.SetAdvancing(false, NowMs());
                    continue;
                }
                if (req != null) { lock (_gate) { if (_activeReopen == req) _activeReopen = null; } req.Done.TrySetResult(false); }

                // ---- 5. retry ----
                failures++;
                AceLog.Warn(Tag, "failure " + failures + ": " + failure);
                int max = isLive ? _opt.MaxLiveRetries : _opt.MaxVodRetries;
                if (fatal) { SetPhase(PlayPhase.Error, failure, true); return; }
                if (failures > max) { SetPhase(PlayPhase.Error, failure, false); return; }
                Update(s => { s.Phase = PlayPhase.Retrying; s.Retry = failures; s.Error = failure; });
                await Task.Delay(_opt.RetryDelay, ct).ConfigureAwait(false);
                var sess2 = _session;
                if (sess2 != null && (sess2.State == SessionState.Failed || sess2.State == SessionState.Stopped))
                {
                    // the session is no longer usable: it is closed and opened again from scratch
                    try { sess2.Dispose(); } catch { }
                    _session = null; url = null;
                }
            }
        }

        private TimeSpan RemainingOpen(DateTime openStart)
        {
            var left = _opt.OpenTimeout - (DateTime.UtcNow - openStart);
            return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
        }

        private async Task WaitReadyAsync(IStreamSession session, DateTime start, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var st = session.State;
                var stats = session.Stats;
                if (st == SessionState.Playing)
                {
                    if (session.IsLive) Report(PlaybackSignal.Streaming, "prebuffer ready", stats.Peers);                // a stream really arrived: fresh verification of the channel
                    return;
                }
                if (st == SessionState.Failed || st == SessionState.Stopped) throw new InvalidOperationException("la sesion del motor ha fallado (" + st + (stats.Message != null ? ": " + stats.Message : "") + ")");
                PlayPhase phase = st == SessionState.Resolving ? PlayPhase.Resolving : st == SessionState.Connecting ? PlayPhase.Connecting : PlayPhase.Buffering;
                Update(s => { s.Phase = phase; s.Peers = stats.Peers; s.Seeds = stats.Seeds; s.DownBytesPerSec = stats.DownloadBytesPerSec; s.BufferPercent = stats.BufferPercent; });
                // With no data at all (not even one prebuffer piece) after OpenTimeout: there are no sources right now. With data trickling in, wait until ReadyTimeout.
                if (session.IsLive && _opt.OpenTimeout > TimeSpan.Zero && stats.BufferPercent <= 0 && DateTime.UtcNow - start > _opt.OpenTimeout)
                    throw new NoSourceException("the live stream has no sources: " + (int)_opt.OpenTimeout.TotalSeconds + " s without data (peers: " + stats.Peers + ")");
                if (DateTime.UtcNow - start > _opt.ReadyTimeout) throw new NoSourceException("the channel did not load the buffer within " + (int)_opt.ReadyTimeout.TotalSeconds + " s (peers: " + stats.Peers + ")");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        private async Task DisposePlayerAsync()
        {
            var p = _player;
            _player = null;
            if (p == null) return;
            try { await Task.Run(() => p.Dispose()).ConfigureAwait(false); } catch (Exception ex) { AceLog.Warn(Tag, "player Dispose: " + ex.Message); }
        }

        private async Task CleanupAsync()
        {
            await DisposePlayerAsync().ConfigureAwait(false);
            var s = _session;
            _session = null;
            if (s != null) { try { s.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "session Dispose: " + ex.Message); } }
        }
    }
}
