using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.Probe
{
    /// <summary>
    /// Probe scheduler: active only while the listing is seen (Start / Stop with cancellation of everything in progress), at most
    /// 'Concurrency' links at once, the visible ones first, result cached 60 s per link and a new cycle every 60 s while it stays open.
    /// Ordering and filter: only the live ones (score &gt; 0), by seeds + peers descending, tie by seeds and then by name.
    /// </summary>
    public sealed class ProbeScheduler : IDisposable
    {
        private const string Tag = "scheduler";
        private readonly ILinkProber _prober;
        private readonly ProbeOptions _opt;
        private readonly object _gate = new object();
        private readonly Dictionary<string, ProbeResult> _results = new Dictionary<string, ProbeResult>(StringComparer.Ordinal);     // by Key: cache between cycles and listing sessions
        private List<LinkEntry> _entries = new List<LinkEntry>();
        private HashSet<string> _visible = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _inFlight, _maxInFlight;
        private volatile bool _resting;
        private int _retryRunning;
        private readonly Dictionary<string, DateTime> _evidenceAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);       // when the player notified about each channel (under _gate)
        private readonly SemaphoreSlim _liveGate;
        private long _probes;
        private readonly ProbeRunStats _stats = new ProbeRunStats();
        private TaskCompletionSource<bool> _firstCycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Fired (on a pool thread) every time the result of a link changes.</summary>
        public event Action<LinkEntry, ProbeResult> ResultChanged;

        public ProbeScheduler(ILinkProber prober, ProbeOptions options = null)
        {
            _prober = prober;
            _opt = options ?? prober.Options;
            _liveGate = new SemaphoreSlim(Math.Max(1, _opt.LiveVerifyConcurrency));
        }

        public bool IsRunning { get { lock (_gate) return _cts != null && !_cts.IsCancellationRequested; } }
        /// <summary>The UI is at rest: the retries of what is unverified go at a low pace (ProbeOptions.IdleRetryConcurrency / IdleRetryPause).</summary>
        public bool Resting { get { return _resting; } }

        /// <summary>
        /// The UI reports that nobody has touched the remote for a while (true) or that it has been touched again (false). At rest, the looping retries of unverified channels drop to
        /// IdleRetryConcurrency at once and take IdleRetryPause of breather after each attempt; the first pass and the renewal of expiring cards do not change. It returns to the normal pace as
        /// soon as activity is reported (attempts in progress finish on their own and nothing is cancelled).
        /// </summary>
        public void SetResting(bool resting) { _resting = resting; }

        private async Task AcquireRetrySlotAsync(CancellationToken ct)
        {
            // the limit depends on rest and may change while waiting: it is looked at again every 250 ms
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int limit = _resting ? Math.Max(1, _opt.IdleRetryConcurrency) : Math.Max(1, _opt.RetryConcurrency);
                if (Interlocked.Increment(ref _retryRunning) <= limit) return;
                Interlocked.Decrement(ref _retryRunning);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        private async Task ReleaseRetrySlotAsync(CancellationToken ct)
        {
            // at rest, a breather after each attempt before leaving the slot free (it is cut short as soon as the remote is touched again or it is cancelled)
            try
            {
                var pause = _opt.IdleRetryPause;
                for (var waited = TimeSpan.Zero; _resting && waited < pause && !ct.IsCancellationRequested; waited += TimeSpan.FromMilliseconds(250))
                    await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Decrement(ref _retryRunning); }
        }
        /// <summary>Maximum links probed at once; in phases, of links being RESOLVED at once (the tracker count goes separately).</summary>
        public int MaxObservedConcurrency { get { return Math.Max(Volatile.Read(ref _maxInFlight), _stats.MaxResolving); } }
        public long ProbesStarted { get { return Interlocked.Read(ref _probes); } }

        /// <summary>Starts probing these links (replaces the previous list; cached results are kept by key).</summary>
        public void Start(IEnumerable<LinkEntry> entries)
        {
            lock (_gate)
            {
                if (_cts != null) _cts.Cancel();
                _entries = entries.ToList();
                _cts = new CancellationTokenSource();
                _firstCycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = _cts.Token;
                var first = _firstCycle;
                _loop = Task.Run(() => LoopAsync(token, first));
            }
        }

        /// <summary>Cancels everything in progress (on leaving the listing: play, menu, Home). Results are kept.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            Task loop;
            lock (_gate) { cts = _cts; loop = _loop; _cts = null; _loop = null; }
            if (cts == null) return;
            cts.Cancel();
            try { if (loop != null) loop.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            cts.Dispose();
        }

        /// <summary>
        /// Like Stop() but without waiting for the probes in progress to finish (they are cancelled and end on their own): to call it from the UI thread.
        /// Results are kept; a later Start() resumes.
        /// </summary>
        public void Cancel()
        {
            CancellationTokenSource cts;
            lock (_gate) { cts = _cts; _cts = null; _loop = null; }
            if (cts == null) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Seeds an already known result (for example, from a previous scheduler whose engine was released): it counts as cached until its TimeUtc expires.
        /// It does not overwrite a result that already exists and does not fire ResultChanged. Results without a conclusion (Pending/Checking) are ignored.
        /// </summary>
        public void Seed(LinkEntry entry, ProbeResult result)
        {
            if (entry == null || entry.Key == null || result == null || result.State == LinkState.Pending || result.State == LinkState.Checking) return;
            lock (_gate) { if (!_results.ContainsKey(entry.Key)) _results[entry.Key] = result; }
        }

        /// <summary>Keys of the links visible on screen: they are probed before the rest.</summary>
        public void SetVisible(IEnumerable<string> keys)
        {
            lock (_gate) _visible = new HashSet<string>(keys, StringComparer.Ordinal);
        }

        /// <summary>Completes when the first cycle over all the links ends (or is cancelled).</summary>
        public Task WaitFirstCycleAsync(CancellationToken ct)
        {
            Task t;
            lock (_gate) t = _firstCycle.Task;
            if (!ct.CanBeCanceled) return t;
            return Task.WhenAny(t, Task.Delay(Timeout.Infinite, ct)).ContinueWith(x => { ct.ThrowIfCancellationRequested(); }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public ProbeResult GetResult(LinkEntry e)
        {
            if (e.Key == null) return new ProbeResult { State = LinkState.Unsupported };
            lock (_gate)
            {
                ProbeResult r;
                if (_results.TryGetValue(e.Key, out r)) return r;
            }
            return new ProbeResult { State = e.IsProbeable ? LinkState.Pending : LinkState.Unsupported };
        }

        private async Task LoopAsync(CancellationToken ct, TaskCompletionSource<bool> first)
        {
            try
            {
                int cycle = 0;
                while (!ct.IsCancellationRequested)
                {
                    await RunCycleAsync(ct, ++cycle).ConfigureAwait(false);
                    first.TrySetResult(true);
                    await Task.Delay(_opt.CycleInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { first.TrySetCanceled(); }
            catch (Exception ex) { AceLog.Warn(Tag, "cycle: " + ex.Message); first.TrySetException(ex); }
        }

        private async Task RunCycleAsync(CancellationToken ct, int cycle)
        {
            // One pass over all the channels that are still "present" (those of the list being seen; on leaving it is cancelled). Order and split:
            //  1) those NEVER checked (visible first, then the list order): they take priority over any retry;
            //  2) those that were verified and whose cache expired (or were kept by an isolated negative): they are cards on screen, renewed without removing them;
            //  3) the unverified (unknown, no descriptor, no pieces, stopped): retried in rotation, the one that has gone longest without being tried first (visible before).
            // The one that finishes its pass waits for the next (CycleInterval is the minimum breather between retries of the same channel). Those of group 3 are limited to RetryConcurrency at once.
            List<LinkEntry> todo;
            var group = new Dictionary<string, int>(StringComparer.Ordinal);
            lock (_gate)
            {
                var visible = _visible;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var probeable = _entries.Where(e => e.IsProbeable && e.Key != null && seen.Add(e.Key)).ToList();
                var keyed = new List<KeyValuePair<LinkEntry, int>>();
                var stamp = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                foreach (var e in probeable)
                {
                    ProbeResult r;
                    int g;
                    if (!_results.TryGetValue(e.Key, out r) || r.State == LinkState.Pending) g = 0;
                    else if (DateTime.UtcNow - r.TimeUtc < TtlOf(r)) continue;                        // valid: shown as is
                    else { g = r.LiveVerified ? 1 : 2; stamp[e.Key] = r.TimeUtc; }
                    group[e.Key] = g;
                    keyed.Add(new KeyValuePair<LinkEntry, int>(e, g));
                }
                int idx = 0;
                var order = keyed.Select(k => new { E = k.Key, G = k.Value, I = idx++ })
                    .OrderBy(x => x.G).ThenBy(x => visible.Contains(x.E.Key) ? 0 : 1)
                    .ThenBy(x => x.G == 0 ? DateTime.MinValue : stamp[x.E.Key]).ThenBy(x => x.I).ToList();
                todo = order.Select(x => x.E).ToList();
            }
            var sem = new SemaphoreSlim(Math.Max(1, _opt.Concurrency));
            var tasks = new List<Task>();
            var phased = _prober as IPhasedProber;
            foreach (var e in todo)
            {
                ct.ThrowIfCancellationRequested();
                bool limited = group[e.Key] == 2;
                if (limited) await AcquireRetrySlotAsync(ct).ConfigureAwait(false);                       // throws if cancelled while waiting: it does not hold the slot
                try { await sem.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (limited) Interlocked.Decrement(ref _retryRunning); throw; }
                if (ct.IsCancellationRequested) { sem.Release(); if (limited) Interlocked.Decrement(ref _retryRunning); ct.ThrowIfCancellationRequested(); }       // the wait may have completed right as it was cancelled
                var entry = e;
                if (phased != null)
                {
                    // in phases: the slot of 'sem' is only held while RESOLVING; the tracker count goes separately, grouped and with a brake per tracker
                    tasks.Add(Task.Run(async () =>
                    {
                        try { await ProbePhasedAsync(phased, entry, sem, ct).ConfigureAwait(false); }
                        finally { if (limited) await ReleaseRetrySlotAsync(ct).ConfigureAwait(false); }
                    }));
                    continue;
                }
                tasks.Add(Task.Run(async () =>
                {
                    int now = Interlocked.Increment(ref _inFlight);
                    int prev;
                    while (now > (prev = Volatile.Read(ref _maxInFlight))) Interlocked.CompareExchange(ref _maxInFlight, now, prev);
                    Interlocked.Increment(ref _probes);
                    try
                    {
                        var startedUtc = DateTime.UtcNow;
                        Set(entry, new ProbeResult { State = LinkState.Checking, TimeUtc = startedUtc });
                        var res = await _prober.ProbeAsync(entry, ct).ConfigureAwait(false);
                        SetProbed(entry, res, startedUtc);
                    }
                    catch (OperationCanceledException) { ResetChecking(entry); }
                    finally { Interlocked.Decrement(ref _inFlight); sem.Release(); if (limited) await ReleaseRetrySlotAsync(ct).ConfigureAwait(false); }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// How long a result is valid before the channel becomes a candidate again: available, AvailableTtl (10 min; a kept negative, ResultTtl); an http or a live count, ResultTtl;
        /// anything else (unknown, stopped, no descriptor, no pieces) nothing: it is checked again in the next pass, while the list stays open.
        /// </summary>
        private TimeSpan TtlOf(ProbeResult r)
        {
            if (r.LiveVerified) return r.Live.Strikes > 0 ? _opt.ResultTtl : _opt.AvailableTtl;
            if (r.State == LinkState.Good) return _opt.ResultTtl;
            return TimeSpan.Zero;
        }

        // ---------- Phased probing ----------

        /// <summary>Counters of the pass (resolution, 10 s rule, fallback).</summary>
        public ProbeRunStats Stats { get { return _stats; } }

        /// <summary>
        /// A link in phases. 1) Resolve (takes a slot of 'sem' = ProbeOptions.Concurrency); 2) count by trackers, grouped and with a brake per
        /// tracker (takes no slot); 3) 10 s rule: at zero or without data it is not discarded, the count is repeated after RecheckDelay.
        /// </summary>
        private async Task ProbePhasedAsync(IPhasedProber ph, LinkEntry entry, SemaphoreSlim sem, CancellationToken ct)
        {
            bool holding = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var startedUtc = DateTime.UtcNow;                                  // what the player sees after this probe starts is worth more than its result (SetProbed)
            Interlocked.Increment(ref _probes);
            LiveProbeResult previousLive = null;
            bool keepShown = false;
            lock (_gate) { ProbeResult prev; if (_results.TryGetValue(entry.Key, out prev)) { previousLive = prev.Live; keepShown = prev.State != LinkState.Pending && prev.State != LinkState.Checking; } }        // before putting "checking": its live verdict can be reused
            try
            {
                // A channel that already had a result (available or not) is checked again in the background WITHOUT going through "checking" until it is known how it ends: a card does not disappear
                // or flicker, and the list progress does not go back on each retry
                if (!keepShown) Set(entry, new ProbeResult { State = LinkState.Checking, TimeUtc = DateTime.UtcNow });
                ResolveStep step = null;
                int attempt = 0, starved = 0;
                while (true)
                {
                    attempt++;
                    var rsw = System.Diagnostics.Stopwatch.StartNew();
                    _stats.ResolveStarted();
                    try { step = await ResolveSafeAsync(ph, entry, ct).ConfigureAwait(false); }
                    finally { _stats.ResolveEnded(); if (holding) { holding = false; sem.Release(); } }
                    bool needed = entry.Link != null && (entry.Kind == LinkKind.AceStream && entry.Link.Kind == AceEngine.Links.AceLinkKind.ContentId
                        || _opt.ResolveInfoHashLinks && (entry.Link.Kind == AceEngine.Links.AceLinkKind.Magnet || entry.Link.Kind == AceEngine.Links.AceLinkKind.InfoHash));
                    if (!needed) { if (attempt == 1) _stats.NoteNoResolveNeeded(); }
                    else if (step.Final != null && step.Final.Unresolved) _stats.NoteResolve(false, rsw.Elapsed.TotalSeconds, attempt > 1, step.Final.ResolveStats, step.Final.Failure, entry.Key);
                    else _stats.NoteResolve(true, rsw.Elapsed.TotalSeconds, attempt > 1, step.Target != null ? step.Target.Stats : null, ResolveFailure.None, entry.Key);
                    // Without resources (system errors, DHT queries waiting for a slot) is NOT "nobody serves it": it is retried, and the link stays "checking" (it is never hidden)
                    if (step.Final != null && step.Final.Unresolved && step.Final.Failure == ResolveFailure.Starved && starved < _opt.StarvedRetries)
                    {
                        starved++;
                        _stats.NoteStarvedRetry();
                        await Task.Delay(TimeSpan.FromTicks(_opt.StarvedRetryDelay.Ticks * starved), ct).ConfigureAwait(false);
                        await sem.WaitAsync(ct).ConfigureAwait(false);
                        holding = true;
                        continue;
                    }
                    bool retry = step.Final != null && step.Final.Unresolved && step.Final.Failure != ResolveFailure.Starved && attempt - starved == 1 && _opt.RecheckUnresolved && _opt.RecheckDelay > TimeSpan.Zero;
                    if (!retry) break;
                    _stats.NoteResolveRetried();
                    await Task.Delay(_opt.RecheckDelay, ct).ConfigureAwait(false);
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    holding = true;
                }
                // Quick check (default): without counting announced peers; the descriptor is resolved and the live stream probed, all within CheckBudget.
                // Only if it is not a live stream (VOD, no live prober) does the usual count continue.
                if (step.Final == null && step.Target != null && _opt.VerifyLive && !_opt.CountBeforeVerify && ph is ILiveVerifier)
                {
                    var fast = await FastVerifyAsync((ILiveVerifier)ph, previousLive, step.Target, sw, ct).ConfigureAwait(false);
                    if (fast != null) { _stats.NoteFinal(fast.State); SetProbed(entry, fast, startedUtc); return; }
                }
                ProbeResult final = step.Final != null ? step.Final : await CountWithRecheckAsync(ph, step.Target, ct).ConfigureAwait(false);
                if (step.Final != null) { final.Rounds = attempt; _stats.NoteFinal(final.State); }
                if (final.Unresolved && final.Failure == ResolveFailure.Starved)
                {
                    // still without resources after the retries: it is not considered dead or unknown; it stays "checking" and the next cycle tries again without waiting for the TTL
                    _stats.NoteStarvedGiveUp();
                    final.State = LinkState.Checking;
                    final.Error = "no resources to resolve it: " + final.Error;
                    Set(entry, new ProbeResult { State = LinkState.Checking, TimeUtc = DateTime.MinValue, Failure = ResolveFailure.Starved, Error = final.Error, Unresolved = true, ResolveStats = final.ResolveStats });
                    return;
                }
                final.TimeUtc = DateTime.UtcNow;
                final.Elapsed = sw.Elapsed;
                if (step.Target != null && final.State == LinkState.Good && ph is ILiveVerifier && _opt.VerifyLive)
                {
                    if (_opt.PublishBeforeVerify) SetProbed(entry, final, startedUtc);
                    await VerifyLiveAsync((ILiveVerifier)ph, previousLive, step.Target, final, ct).ConfigureAwait(false);
                    final.TimeUtc = DateTime.UtcNow;
                    final.Elapsed = sw.Elapsed;
                }
                SetProbed(entry, final, startedUtc);
            }
            catch (OperationCanceledException) { ResetChecking(entry); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                AceLog.Warn(Tag, entry.Name + ": " + ex.GetType().Name + " " + ex.Message);
                SetProbed(entry, new ProbeResult { State = LinkState.Unknown, Error = ex.Message, TimeUtc = DateTime.UtcNow, Elapsed = sw.Elapsed }, startedUtc);
            }
            finally { if (holding) sem.Release(); }
        }

        /// <summary>
        /// Phase 4: the tracker count said there are peers; here it is checked that they can be connected and that the live stream advances. Alive: alive with the figure of active peers observed;
        /// Stale (sender stopped): dead; no connectable peers or no recent pieces: unknown (it is retried in the next cycle) and it does not appear in the list.
        /// </summary>
        private async Task VerifyLiveAsync(ILiveVerifier v, LiveProbeResult previous, SwarmTarget target, ProbeResult final, CancellationToken ct)
        {
            LiveProbeResult lp = null;
            // A recent verdict is reused: connecting to peers every minute for each channel adds nothing
            if (previous != null && previous.Verdict != LiveVerdict.Unknown)
            {
                var ttl = previous.Verdict == LiveVerdict.Alive ? _opt.LiveAliveTtl : _opt.LiveDeadTtl;
                if (ttl > TimeSpan.Zero && DateTime.UtcNow - previous.TimeUtc < ttl) lp = previous;
            }
            if (lp == null)
            {
                await _liveGate.WaitAsync(ct).ConfigureAwait(false);
                try { lp = await v.VerifyLiveAsync(target, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Debug(Tag, "live probe: " + ex.GetType().Name + " " + ex.Message); }
                finally { _liveGate.Release(); }
            }
            if (lp == null) return;                     // it could not be checked (VOD, http link, no descriptor): it keeps the count
            bool negative = lp.Verdict == LiveVerdict.Stale || lp.Verdict == LiveVerdict.Unreachable || lp.Verdict == LiveVerdict.NoPieces || lp.Verdict == LiveVerdict.Unknown;
            if (negative && previous != null && previous.Verdict == LiveVerdict.Alive && previous.Strikes == 0 && _opt.AliveGrace > TimeSpan.Zero && DateTime.UtcNow - previous.TimeUtc < _opt.AliveGrace)
            {
                // It was alive a short while ago and not this time: a single negative check does not remove it from the list (it is checked again in the next cycle)
                lp = previous.Held();
            }
            final.Live = lp;
            switch (lp.Verdict)
            {
                case LiveVerdict.Alive: break;
                case LiveVerdict.Stale: final.State = LinkState.Dead; final.Error = "the sender is stopped: peers receive no new pieces"; break;
                case LiveVerdict.Unreachable: final.State = LinkState.Unknown; final.Error = "no connectable peer (" + lp.Tried + " attempts)"; break;
                case LiveVerdict.NoPieces: final.State = LinkState.Unknown; final.Error = "the connected peers have no recent pieces"; break;
                // Inconclusive (peers with recent pieces but no time to see the head advance): it is not considered alive with the count of announced peers, which is exactly the misleading figure;
                // it stays unknown and is checked again in the next cycle (an Unknown verdict is not reused)
                default: final.State = LinkState.Unknown; final.Error = "live probe without a conclusion"; break;
            }
        }

        /// <summary>
        /// Quick check of an already resolved live stream: probes the peers (with what is left of CheckBudget) and concludes. Alive = available (connectable peers with recent pieces and the live stream advancing)
        /// with the number of peers observed; stopped = Dead; no connectable peers, no pieces or no conclusion = Unknown (not shown and retried later). A channel that was available
        /// and comes out negative ONCE is kept (AliveGrace) until the next check. Null if it is not a live stream that can be probed (the caller continues with the count).
        /// </summary>
        private async Task<ProbeResult> FastVerifyAsync(ILiveVerifier v, LiveProbeResult previous, SwarmTarget target, System.Diagnostics.Stopwatch sw, CancellationToken ct)
        {
            LiveProbeResult lp = null;
            await _liveGate.WaitAsync(ct).ConfigureAwait(false);
            try { lp = await v.VerifyLiveAsync(target, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Debug(Tag, "live probe: " + ex.GetType().Name + " " + ex.Message); lp = new LiveProbeResult { Verdict = LiveVerdict.Unknown, Detail = ex.Message }; }
            finally { _liveGate.Release(); }
            if (lp == null) return null;
            bool negative = lp.Verdict != LiveVerdict.Alive;
            if (negative && previous != null && previous.Verdict == LiveVerdict.Alive && previous.Strikes == 0 && _opt.AliveGrace > TimeSpan.Zero && DateTime.UtcNow - previous.TimeUtc < _opt.AliveGrace)
                lp = previous.Held();                                    // it was available a short while ago: an isolated negative does not remove it from the list
            var res = new ProbeResult
            {
                InfoHash = target.InfoHash == null ? null : Hex.ToHex(target.InfoHash), Source = target.Source, ResolveElapsed = target.ResolveElapsed, ResolveStats = target.Stats,
                Unconfirmed = target.Unconfirmed, BitrateKbps = target.BitrateKbps, Method = "live", Rounds = 1, Live = lp, TimeUtc = DateTime.UtcNow, Elapsed = sw.Elapsed
            };
            switch (lp.Verdict)
            {
                case LiveVerdict.Alive: res.State = LinkState.Good; res.Peers = lp.ActivePeers; break;
                case LiveVerdict.Stale: res.State = LinkState.Dead; res.Error = "the sender is stopped: peers receive no new pieces"; break;
                case LiveVerdict.Unreachable: res.State = LinkState.Unknown; res.Error = "no connectable peer (" + lp.Tried + " attempts)"; break;
                case LiveVerdict.NoPieces: res.State = LinkState.Unknown; res.Error = "the connected peers have no recent pieces"; break;
                default: res.State = LinkState.Unknown; res.Error = "live probe without a conclusion"; break;
            }
            return res;
        }

        private static async Task<ResolveStep> ResolveSafeAsync(IPhasedProber ph, LinkEntry entry, CancellationToken ct)
        {
            try { return await ph.ResolveAsync(entry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return new ResolveStep { Final = new ProbeResult { State = LinkState.Unknown, Error = ex.Message, Unresolved = entry.Kind == LinkKind.AceStream || entry.Kind == LinkKind.Magnet } };
            }
        }

        private async Task<ProbeResult> FallbackSafeAsync(IPhasedProber ph, SwarmTarget t, CancellationToken ct)
        {
            try { return await ph.FallbackAsync(t, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Debug(Tag, "fallback: " + ex.Message); return null; }
        }

        /// <summary>
        /// 10 s rule (extended): a first count at zero or without data does not discard the link. While waiting RecheckDelay the DHT fallback runs
        /// (if it gives peers, alive "approx." without waiting longer); after the deadline it is counted again by trackers and only if it stays at zero is it hidden.
        /// </summary>
        private async Task<ProbeResult> CountWithRecheckAsync(IPhasedProber ph, SwarmTarget t, CancellationToken ct)
        {
            var r1 = await ph.CountAsync(t, ct).ConfigureAwait(false);
            if (r1.State == LinkState.Good) { _stats.NoteGood(1, false); return r1; }
            _stats.NoteRechecked();
            _stats.NoteFallbackStarted();
            using (var fbCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var fbTask = FallbackSafeAsync(ph, t, fbCts.Token);
                try
                {
                    if (_opt.RecheckDelay > TimeSpan.Zero)
                    {
                        var wait = Task.Delay(_opt.RecheckDelay, ct);
                        await Task.WhenAny(fbTask, wait).ConfigureAwait(false);
                        if (fbTask.IsCompleted)
                        {
                            var fb = fbTask.Result;
                            if (fb != null && fb.State == LinkState.Good) { _stats.NoteGood(1, true); return Combine(r1, null, fb, 1); }
                        }
                        await wait.ConfigureAwait(false);
                    }
                    ProbeResult r2 = null;
                    if (_opt.RecheckDelay > TimeSpan.Zero)
                    {
                        r2 = await ph.CountAsync(t, ct).ConfigureAwait(false);
                        if (r2.State == LinkState.Good) { _stats.NoteGood(2, false); return Combine(r1, r2, null, 2); }
                    }
                    var fbFinal = await fbTask.ConfigureAwait(false);
                    if (fbFinal != null && fbFinal.State == LinkState.Good) { _stats.NoteGood(2, true); return Combine(r1, r2, fbFinal, 2); }
                    var last = Combine(r1, r2, null, r2 == null ? 1 : 2);
                    _stats.NoteFinal(last.State);
                    return last;
                }
                finally { try { fbCts.Cancel(); } catch { } }
            }
        }

        /// <summary>Joins the rounds into a single result: the best of the tracker numbers, or the approximate DHT figure if the trackers gave nothing.</summary>
        private static ProbeResult Combine(ProbeResult r1, ProbeResult r2, ProbeResult fallback, int rounds)
        {
            var best = (r2 != null && r2.Score >= r1.Score) ? r2 : r1;
            var res = new ProbeResult { InfoHash = r1.InfoHash, Source = r1.Source, ResolveElapsed = r1.ResolveElapsed, Rounds = rounds, Method = best.Method, Seeders = best.Seeders, Peers = best.Peers, Unconfirmed = r1.Unconfirmed, BitrateKbps = r1.BitrateKbps };
            res.Trackers.AddRange(r1.Trackers);
            if (r2 != null) res.Trackers.AddRange(r2.Trackers);
            if (res.Score == 0 && fallback != null && fallback.Score > 0)
            {
                res.Peers = fallback.Peers; res.Approximate = true; res.Method = fallback.Method;
            }
            bool dead = r1.State == LinkState.Dead && (r2 == null || r2.State == LinkState.Dead);
            res.State = ProbeResult.Classify(res.Seeders, res.Peers, dead);
            return res;
        }

        private void Set(LinkEntry e, ProbeResult r)
        {
            lock (_gate) _results[e.Key] = r;
            Raise(e, r);
        }

        /// <summary>
        /// Result of a probe that has already finished. If the player reported about this channel AFTER the probe started, what the player saw (the real download) is worth more than what the
        /// probe observed and the result is discarded; otherwise, the probe wins, as always.
        /// </summary>
        private void SetProbed(LinkEntry e, ProbeResult r, DateTime startedUtc)
        {
            lock (_gate)
            {
                DateTime at;
                if (_evidenceAt.TryGetValue(e.Key, out at) && at > startedUtc) return;
                _results[e.Key] = r;
            }
            Raise(e, r);
        }

        private void Raise(LinkEntry e, ProbeResult r)
        {
            var h = ResultChanged;
            if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(e, r); } catch { } });
        }

        // ---------- What the player sees ----------

        /// <summary>
        /// The player reports what it has seen of a channel. Stream arrived: fresh verification (the channel counts as verified from now on and is not probed again until
        /// AvailableTtl expires). Real failure (no sources, live stopped, session lost): it stops counting as verified; the card is updated with the checker rule
        /// (ProbeOptions.PlaybackFailureStrikes) and the channel becomes a candidate again in the next pass: if its screen is still open, it enters the looping retries; if not, the
        /// first pass on returning. It starts nothing by itself nor goes through "checking": the card does not flicker. Safe from any thread.
        /// </summary>
        public void NotePlayback(PlaybackEvidence evidence)
        {
            if (evidence == null || evidence.Entry == null || evidence.Entry.Key == null || !evidence.Entry.IsProbeable) return;
            var e = evidence.Entry;
            ProbeResult next, before;
            lock (_gate)
            {
                ProbeResult prev;
                _results.TryGetValue(e.Key, out prev);
                before = prev;
                var now = DateTime.UtcNow;
                next = ApplyPlayback(prev, evidence, _opt, now);
                if (next == null) return;
                _results[e.Key] = next;
                _evidenceAt[e.Key] = now;
            }
            bool wasShown = before != null && before.LiveVerified, isShown = next.LiveVerified;
            // when what is shown changes (appears, switches off or is kept with a mark) it stays in the TV log; the renewals of every minute do not
            if (wasShown != isShown || (isShown && next.Live.Strikes != (before == null ? 0 : before.Live.Strikes))) AceLog.Info(Tag, "player notice (" + evidence + "): " + (wasShown ? "available" : "unverified") + " -> " + (isShown ? "available" + (next.Live.Strikes > 0 ? " (mark " + next.Live.Strikes + ")" : "") : next.State.ToString()));
            else AceLog.Debug(Tag, "player notice (" + evidence + "): no changes");
            Raise(e, next);
        }

        /// <summary>
        /// What happens to the result of a channel when the player sees something (pure function, to test it). Returns the new result, or null if there is nothing to change
        /// (a failure of a channel that was not shown as available: it is already a retry candidate and there is no card to remove).
        /// </summary>
        public static ProbeResult ApplyPlayback(ProbeResult prev, PlaybackEvidence ev, ProbeOptions opt, DateTime now)
        {
            if (ev == null || ev.Entry == null) return null;
            if (opt == null) opt = new ProbeOptions();
            bool shown = prev != null && prev.LiveVerified;
            if (ev.Signal == PlaybackSignal.Streaming)
            {
                // The card's number is that of the active peers of the probe (1, 2, 3, 4+): if there was one it is kept; otherwise, the session's on the same scale
                int target = Math.Max(1, opt.LiveProbe.TargetPeers);
                var live = new LiveProbeResult { Verdict = LiveVerdict.Alive, TimeUtc = now, Strikes = 0, Detail = "player: stream arrives" + (ev.Detail != null ? " (" + ev.Detail + ")" : "") };
                if (shown) { live.ActivePeers = prev.Live.ActivePeers; live.Saturated = prev.Live.Saturated; live.EstimatedConnectable = prev.Live.EstimatedConnectable; live.GoodPeers.AddRange(prev.Live.GoodPeers); }
                else { live.ActivePeers = Math.Max(1, Math.Min(target, ev.Peers)); live.Saturated = ev.Peers >= target; live.EstimatedConnectable = Math.Max(live.ActivePeers, ev.Peers); }
                return Carry(prev, new ProbeResult { State = LinkState.Good, Peers = live.ActivePeers, Method = "playback", Live = live, TimeUtc = now, Rounds = 1 });
            }
            if (!shown) return null;
            int weight = Math.Max(1, opt.PlaybackFailureStrikes);
            int strikes = prev.Live.Strikes + weight;
            if (strikes < 2)
            {
                // Like an isolated negative of the probe: the card is kept (same figure, same verdict) and it is looked at again in the next pass
                return Carry(prev, new ProbeResult { State = LinkState.Good, Peers = prev.Peers, Method = prev.Method, Live = prev.Live.Held(), TimeUtc = DateTime.MinValue, Rounds = prev.Rounds });
            }
            var neg = new LiveProbeResult { TimeUtc = now, Detail = "player: " + ev.Signal + (ev.Detail != null ? " (" + ev.Detail + ")" : ""), Strikes = 0 };
            var r = new ProbeResult { Method = "playback", Live = neg, TimeUtc = now, Rounds = 1 };
            if (ev.Signal == PlaybackSignal.Stalled) { neg.Verdict = LiveVerdict.Stale; r.State = LinkState.Dead; r.Error = "the sender is stopped: the player receives no new pieces"; }
            else { neg.Verdict = LiveVerdict.NoPieces; r.State = LinkState.Unknown; r.Error = "the player has not received data" + (ev.Detail != null ? ": " + ev.Detail : ""); }
            return Carry(prev, r);
        }

        /// <summary>What is known about the channel from its descriptor and does not depend on this verdict (so that the next probe and the UI do not lose it).</summary>
        private static ProbeResult Carry(ProbeResult prev, ProbeResult next)
        {
            if (prev == null) return next;
            next.InfoHash = prev.InfoHash; next.Source = prev.Source; next.BitrateKbps = prev.BitrateKbps; next.Unconfirmed = prev.Unconfirmed;
            next.ResolveElapsed = prev.ResolveElapsed; next.ResolveStats = prev.ResolveStats; next.Elapsed = prev.Elapsed;
            return next;
        }

        private void ResetChecking(LinkEntry e)
        {
            lock (_gate)
            {
                ProbeResult r;
                if (_results.TryGetValue(e.Key, out r) && r.State == LinkState.Checking) _results.Remove(e.Key);
            }
        }

        // ---------- Ordering and filter ----------

        /// <summary>
        /// Rows of the listing: the live ones with figures, by descending score (tie: more seeds, then name). Live http ones (without figures) go
        /// after. With showAll, the rest (checking, unknown, dead) at the end and labels are not shown. With byGroup, the order is
        /// applied within each group and the groups keep the order of appearance.
        /// </summary>
        public IList<ListRow> Snapshot(bool showAll = false, bool byGroup = false)
        {
            List<LinkEntry> entries;
            lock (_gate) entries = _entries.ToList();
            var rows = new List<ListRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!e.IsProbeable || e.Key == null) continue;
                if (!seen.Add(e.Key)) continue;
                rows.Add(new ListRow { Entry = e, Result = GetResult(e) });
            }
            return Sort(rows, showAll, byGroup);
        }

        public static IList<ListRow> Sort(IList<ListRow> rows, bool showAll, bool byGroup)
        {
            Func<IEnumerable<ListRow>, IEnumerable<ListRow>> order = src =>
            {
                var good = src.Where(r => r.Result.State == LinkState.Good && !r.Result.NoCounts && r.Result.Score > 0)
                    .OrderByDescending(r => r.Result.LiveVerified ? r.Result.Live.ActivePeers : -1).ThenByDescending(r => r.Result.LiveVerified ? r.Result.Live.EstimatedConnectable : 0)
                    .ThenByDescending(r => r.Result.Score).ThenByDescending(r => r.Result.Seeders).ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var http = src.Where(r => r.Result.State == LinkState.Good && r.Result.NoCounts).OrderBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var res = good.Concat(http).ToList();
                if (showAll)
                    res.AddRange(src.Where(r => !res.Contains(r)).OrderBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase));
                return res;
            };
            if (!byGroup) return order(rows).ToList();
            var groups = new List<string>();
            foreach (var r in rows) { var g = r.Entry.Group ?? ""; if (!groups.Contains(g)) groups.Add(g); }
            var res2 = new List<ListRow>();
            foreach (var g in groups) res2.AddRange(order(rows.Where(r => (r.Entry.Group ?? "") == g)));
            return res2;
        }

        public void Dispose() { Stop(); }
    }
}
