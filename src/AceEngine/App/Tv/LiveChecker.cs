using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    public sealed class LiveCheckerOptions
    {
        /// <summary>Channels at the start of each batch that are probed before the rest (what fits on screen).</summary>
        public int VisibleCount = 12;
        /// <summary>
        /// How long a result is valid and kept between batches: 10 minutes (user rule). When it expires the channel is not checked again on its own, but when it is requested again
        /// (Check when showing the list); meanwhile it is not shown as available.
        /// </summary>
        public TimeSpan ResultTtl = TimeSpan.FromMinutes(10);
        /// <summary>Results remembered at most (the expired ones are discarded first).</summary>
        public int MaxCachedResults = 4000;
    }

    /// <summary>
    /// ILiveChecker on top of the parallel probing (ProbeScheduler). Only one active batch: Check cancels the previous one and starts the new one with the first
    /// VisibleCount channels as visible. Valid results (10 min) are kept between batches, even if the engine is recreated in between (they are seeded
    /// into the new scheduler). Each ProbeResult is translated into a ChannelStatus (Peers = Peers, Seeds = Seeders, Approximate, Unconfirmed, State) and
    /// StatusChanged fires when a channel starts being checked and when its result arrives, on pool threads. What is returned are snapshots
    /// that are not modified afterwards: whoever receives them must not change them.
    /// </summary>
    public sealed class LiveChecker : ILiveChecker, IPlaybackReporter, IRestAware, IDisposable
    {
        private const string Tag = "livechecker";

        private sealed class Rec
        {
            public ChannelStatus Status;
            public ProbeResult Result;
            public LinkEntry Entry;
            public DateTime AtUtc;
        }

        private readonly Func<ProbeScheduler> _provider;
        private readonly LiveCheckerOptions _opt;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new object();          // state shared with the pool threads
        private readonly object _opGate = new object();        // serializes Check / Stop / Dispose (UI thread)
        private readonly Dictionary<string, Rec> _cache = new Dictionary<string, Rec>(StringComparer.Ordinal);
        private Dictionary<string, Channel> _batch = new Dictionary<string, Channel>(StringComparer.Ordinal);
        private readonly HashSet<string> _doneKeys = new HashSet<string>(StringComparer.Ordinal);       // keys of the batch with a final result
        private int _done, _total;
        private bool _batchActive, _disposed;
        private volatile bool _resting;
        private ProbeScheduler _sched;
        private Action<LinkEntry, ProbeResult> _subscription;
        // With the AceEngineHost constructor: the current engine and the scheduler created on it
        private AceEngineHost _ownedHost;
        private ProbeScheduler _ownedSched;

        /// <summary>
        /// With the app's engine. 'host' is called on every Check (it may start the engine): it returns the CURRENT AceEngineHost, which changes if the engine is
        /// released and recreated (Suspend / Resume). The scheduler of an already released engine is discarded and another one is created on the new one.
        /// </summary>
        public LiveChecker(Func<AceEngineHost> host, LiveCheckerOptions options = null, Func<DateTime> utcNow = null)
        {
            if (host == null) throw new ArgumentNullException("host");
            _provider = () => SchedulerForHost(host);
            _opt = options ?? new LiveCheckerOptions();
            _now = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Seam for tests (and for whoever already has a scheduler): 'scheduler' returns the current scheduler; if it changes from one to another it is treated as a recreated engine.</summary>
        public LiveChecker(Func<ProbeScheduler> scheduler, LiveCheckerOptions options = null, Func<DateTime> utcNow = null)
        {
            if (scheduler == null) throw new ArgumentNullException("scheduler");
            _provider = scheduler;
            _opt = options ?? new LiveCheckerOptions();
            _now = utcNow ?? (() => DateTime.UtcNow);
        }

        private ProbeScheduler SchedulerForHost(Func<AceEngineHost> hostFunc)
        {
            var h = hostFunc();
            if (h == null) return null;
            if (ReferenceEquals(h, _ownedHost) && !h.IsDisposed && _ownedSched != null) return _ownedSched;
            var s = h.CreateProbeScheduler();                        // throws ObjectDisposedException if the engine was already released
            _ownedHost = h;
            _ownedSched = s;
            return s;
        }

        // ---------- ILiveChecker ----------

        public event Action<Channel, ChannelStatus> StatusChanged;

        public bool Running { get { lock (_gate) return _batchActive && _done < _total; } }
        public int Done { get { lock (_gate) return _done; } }
        public int Total { get { lock (_gate) return _total; } }

        public ChannelStatus GetStatus(Channel channel)
        {
            if (channel == null || channel.Entry == null) return new ChannelStatus();
            if (!channel.Entry.IsProbeable || channel.Entry.Key == null) return new ChannelStatus { State = LinkState.Unsupported };
            string key = channel.Entry.Key;
            lock (_gate)
            {
                Rec r;
                if (!_cache.TryGetValue(key, out r)) return new ChannelStatus();
                // While the batch is still active the latest known is shown (the scheduler refreshes it on its own); without a batch, only while it is valid
                if ((_batchActive && _batch.ContainsKey(key)) || _now() - r.AtUtc < _opt.ResultTtl) return r.Status;
                return new ChannelStatus();
            }
        }

        public void Check(IReadOnlyList<Channel> channels)
        {
            var batch = new List<Channel>();
            if (channels != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in channels)
                    if (c != null && c.Entry != null && c.Entry.IsProbeable && c.Entry.Key != null && seen.Add(c.Entry.Key)) batch.Add(c);
            }
            lock (_opGate)
            {
                if (_disposed) return;
                if (batch.Count == 0) { StopLocked(); SetBatch(batch, false); return; }
                ProbeScheduler s = null;
                try { s = _provider(); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Warn(Tag, "no probe scheduler: " + ex.GetType().Name + " " + ex.Message); }
                if (s == null) { StopLocked(); SetBatch(batch, false); return; }
                SwitchScheduler(s);
                SetBatch(batch, true);
                // Valid results from a previous scheduler move to the new one so that what is already known is not probed again
                foreach (var c in batch)
                {
                    Rec r;
                    lock (_gate) { if (!_cache.TryGetValue(c.Entry.Key, out r) || !IsFinal(r.Status) || _now() - r.AtUtc >= _opt.ResultTtl) continue; }
                    s.Seed(r.Entry, r.Result);
                }
                s.SetVisible(batch.Take(Math.Max(0, _opt.VisibleCount)).Select(c => c.Entry.Key));
                s.Start(batch.Select(c => c.Entry).ToList());
            }
        }

        /// <summary>The UI reports that it is at rest (nobody has touched the remote for a while) or that it no longer is (IRestAware): the looping retries slow down or recover their pace.</summary>
        public void SetResting(bool resting)
        {
            _resting = resting;
            ProbeScheduler s;
            lock (_gate) s = _sched;
            if (s != null) s.SetResting(resting);
        }

        /// <summary>
        /// The player reports what it has seen of a channel (IPlaybackReporter). Stream arrived: the channel counts as verified now (10 min). Real failure
        /// (no sources after 15 s, live stopped, session lost): it stops counting as verified and its card updates with the checker rule (ProbeOptions.PlaybackFailureStrikes);
        /// the channel becomes a candidate again: it enters the looping retries if its screen is still open (the scheduler runs) and, if not, the first pass on returning to it.
        /// It applies even if there is no active batch (the player opens a channel with the screen already closed for the checker). Safe from any thread.
        /// </summary>
        public void Report(PlaybackEvidence evidence)
        {
            if (evidence == null || evidence.Entry == null || evidence.Entry.Key == null || !evidence.Entry.IsProbeable) return;
            ProbeScheduler s;
            lock (_gate) { if (_disposed) return; s = _sched; }
            if (s != null)
            {
                s.NotePlayback(evidence);                                       // the scheduler notifies through ResultChanged: OnResult updates the cache and fires StatusChanged
                return;
            }
            // There has not been any batch yet (no scheduler): it is applied to what is in the cache, with the default options
            Channel channel = null; ChannelStatus status = null;
            lock (_gate)
            {
                Rec old;
                _cache.TryGetValue(evidence.Entry.Key, out old);
                var next = ProbeScheduler.ApplyPlayback(old == null ? null : old.Result, evidence, null, _now());
                if (next == null) return;
                status = Map(next);
                var rec = new Rec { Status = status, Result = next, Entry = evidence.Entry, AtUtc = _now() };
                bool same = old != null && SameStatus(old.Status, status);
                if (same) rec.Status = old.Status;
                _cache[evidence.Entry.Key] = rec;
                if (!same) _batch.TryGetValue(evidence.Entry.Key, out channel);
            }
            if (channel != null) Raise(channel, status);
        }

        /// <summary>Changes the visible channels (for example when scrolling the list): they are probed before the rest in the next cycle.</summary>
        public void SetVisible(IEnumerable<Channel> visible)
        {
            lock (_opGate)
            {
                if (_disposed || _sched == null || visible == null) return;
                _sched.SetVisible(visible.Where(c => c != null && c.Entry != null && c.Entry.Key != null).Select(c => c.Entry.Key));
            }
        }

        public void Stop()
        {
            lock (_opGate) { if (!_disposed) StopLocked(); }
        }

        public void Dispose()
        {
            lock (_opGate)
            {
                if (_disposed) return;
                StopLocked();
                lock (_gate) _disposed = true;
                Unsubscribe();
                lock (_gate) _sched = null;
            }
        }

        // ---------- Internals ----------

        private void StopLocked()
        {
            lock (_gate)
            {
                _batchActive = false;
                RemoveChecking();
            }
            if (_sched != null) _sched.Cancel();                    // without waiting: it is called from the UI thread
        }

        /// <summary>What was being checked and is cancelled does not stay as "checking" forever: it goes back to Pending.</summary>
        private void RemoveChecking()
        {
            var drop = _cache.Where(kv => !IsFinal(kv.Value.Status)).Select(kv => kv.Key).ToList();
            foreach (var k in drop) _cache.Remove(k);
        }

        private void SwitchScheduler(ProbeScheduler s)
        {
            if (ReferenceEquals(s, _sched)) return;
            Unsubscribe();
            if (_sched != null) _sched.Cancel();                    // the previous engine was released or replaced: its scheduler is discarded
            lock (_gate) _sched = s;
            s.SetResting(_resting);
            var mine = s;
            _subscription = (entry, result) => OnResult(mine, entry);
            s.ResultChanged += _subscription;
        }

        private void Unsubscribe()
        {
            if (_sched != null && _subscription != null) _sched.ResultChanged -= _subscription;
            _subscription = null;
        }

        private void SetBatch(List<Channel> batch, bool active)
        {
            lock (_gate)
            {
                RemoveChecking();
                _batch = new Dictionary<string, Channel>(StringComparer.Ordinal);
                foreach (var c in batch) _batch[c.Entry.Key] = c;
                _total = batch.Count;
                _batchActive = active && batch.Count > 0;
                // Those that already have a valid result (from a previous batch) count as checked
                _doneKeys.Clear();
                var now = _now();
                foreach (var c in batch)
                {
                    Rec r;
                    if (_cache.TryGetValue(c.Entry.Key, out r) && IsFinal(r.Status) && now - r.AtUtc < _opt.ResultTtl) _doneKeys.Add(c.Entry.Key);
                }
                _done = _doneKeys.Count;
            }
        }

        private static bool IsFinal(ChannelStatus s) { return s.State != LinkState.Pending && s.State != LinkState.Checking; }

        internal static ChannelStatus Map(ProbeResult r)
        {
            var s = new ChannelStatus { State = r.State, Peers = r.Peers, Seeds = r.Seeders, Approximate = r.Approximate, Unconfirmed = r.Unconfirmed };
            if (r.Live != null)
            {
                if (r.LiveVerified)
                {
                    // The number shown is that of active connectable peers observed, not that of those announced by trackers and DHT
                    s.Verified = true; s.Peers = r.Live.ActivePeers; s.Seeds = 0; s.Approximate = false; s.AtLeast = r.Live.Saturated; s.Estimated = r.Live.EstimatedConnectable;
                }
                else if (r.State != LinkState.Good) s.Reason = r.Error;
            }
            return s;
        }

        private static bool SameStatus(ChannelStatus a, ChannelStatus b)
        {
            return a.State == b.State && a.Peers == b.Peers && a.Seeds == b.Seeds && a.Approximate == b.Approximate && a.Unconfirmed == b.Unconfirmed
                && a.Verified == b.Verified && a.AtLeast == b.AtLeast && a.Estimated == b.Estimated && a.Reason == b.Reason;
        }

        /// <summary>
        /// The scheduler reports that something changed in a link. The notices come out of the thread pool and may arrive out of order (Checking after the
        /// result), so the scheduler's current state is read instead of trusting the one the notice carries.
        /// </summary>
        private void OnResult(ProbeScheduler from, LinkEntry entry)
        {
            if (entry == null || entry.Key == null) return;
            Channel channel = null;
            ChannelStatus status = null;
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(from, _sched)) return;                  // notice from a discarded scheduler
                // It is read INSIDE the lock: two notices for the same link on different threads always apply the latest the scheduler knows, never an old value
                // (the scheduler calls nobody while it holds its own lock, so there is no lock inversion)
                ProbeResult truth;
                try { truth = from.GetResult(entry); } catch (Exception) { return; }
                string key = entry.Key;
                Rec old;
                _cache.TryGetValue(key, out old);
                if (truth.State == LinkState.Pending)
                {
                    // The scheduler withdrew "checking" (cancelled): it is forgotten
                    if (old != null && !IsFinal(old.Status)) { _cache.Remove(key); UpdateDone(key, false); }
                    return;
                }
                status = Map(truth);
                if (truth.BitrateKbps > 0) ChannelHints.Learn(key, truth.BitrateKbps);        // the quality of the channels whose name does not say it
                // "Checking" only matters for the active batch: a late notice from a cancelled batch must not leave a channel checking forever
                if (!IsFinal(status) && (!_batchActive || !_batch.ContainsKey(key))) return;
                var rec = new Rec { Status = status, Result = truth, Entry = entry, AtUtc = _now() };
                bool same = old != null && SameStatus(old.Status, status);
                if (same) rec.Status = old.Status;                                          // same snapshot: only the time is refreshed
                _cache[key] = rec;
                UpdateDone(key, IsFinal(status));
                if (_cache.Count > _opt.MaxCachedResults) Prune();
                if (!same) _batch.TryGetValue(key, out channel);                            // notified outside the lock, and only for channels of the current batch
            }
            if (channel != null) Raise(channel, status);
        }

        private void UpdateDone(string key, bool final)
        {
            if (!_batch.ContainsKey(key)) return;
            if (final) _doneKeys.Add(key); else _doneKeys.Remove(key);
            _done = _doneKeys.Count;
        }

        private void Prune()
        {
            var now = _now();
            foreach (var k in _cache.Where(kv => now - kv.Value.AtUtc >= _opt.ResultTtl && !_batch.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList()) _cache.Remove(k);
            if (_cache.Count <= _opt.MaxCachedResults) return;
            foreach (var k in _cache.Where(kv => !_batch.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.AtUtc).Take(_cache.Count - _opt.MaxCachedResults).Select(kv => kv.Key).ToList()) _cache.Remove(k);
        }

        private void Raise(Channel c, ChannelStatus s)
        {
            var h = StatusChanged;
            if (h == null) return;
            try { h(c, s); } catch (Exception ex) { AceLog.Debug(Tag, "listener: " + ex.Message); }
        }
    }

    /// <summary>
    /// "By live peers" order of a category, as a pure and stable function:
    ///  1) the channels with signal (Good), by descending Score; tie: more seeds, then name (case-insensitive), then their original position;
    ///  2) those being checked (Pending / Checking), keeping their relative position;
    ///  3) those without signal (Dead / Unknown / Unsupported) at the end, also in their relative position.
    /// That way, while results arrive, the channels already checked go up and the rest do not dance.
    /// </summary>
    public static class ChannelOrder
    {
        public static List<Channel> ByLivePeers(IReadOnlyList<Channel> rows, Func<Channel, ChannelStatus> statusOf)
        {
            var result = new List<Channel>();
            if (rows == null) return result;
            var live = new List<KeyValuePair<int, Channel>>();
            var checking = new List<Channel>();
            var dead = new List<Channel>();
            var status = new Dictionary<Channel, ChannelStatus>();
            for (int i = 0; i < rows.Count; i++)
            {
                var c = rows[i];
                if (c == null) continue;
                ChannelStatus s = null;
                try { s = statusOf == null ? null : statusOf(c); } catch (Exception) { s = null; }
                if (s == null) s = new ChannelStatus();
                status[c] = s;
                if (s.State == LinkState.Good) live.Add(new KeyValuePair<int, Channel>(i, c));
                else if (s.IsChecking) checking.Add(c);
                else dead.Add(c);
            }
            live.Sort((a, b) =>
            {
                var sa = status[a.Value]; var sb = status[b.Value];
                int r = sb.OrderKey.CompareTo(sa.OrderKey);
                if (r != 0) return r;
                r = sb.Seeds.CompareTo(sa.Seeds);
                if (r != 0) return r;
                r = string.Compare(a.Value.Name ?? "", b.Value.Name ?? "", StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : a.Key.CompareTo(b.Key);
            });
            foreach (var p in live) result.Add(p.Value);
            result.AddRange(checking);
            result.AddRange(dead);
            return result;
        }

        public static List<Channel> ByLivePeers(IReadOnlyList<Channel> rows, ILiveChecker checker)
        {
            return ByLivePeers(rows, c => checker == null ? null : checker.GetStatus(c));
        }
    }
}
