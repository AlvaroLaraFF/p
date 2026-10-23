using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AceEngine.Probe
{
    /// <summary>Counters of a probe pass (those that do not come from trackers or the DHT): resolution, the 10 s rule and the fallback.</summary>
    public sealed class ProbeRunStats
    {
        private readonly object _gate = new object();
        private readonly List<double> _resolveOk = new List<double>();
        private readonly List<double> _resolveFail = new List<double>();
        private int _resolved, _resolveFailed, _resolveRetried, _resolveRecovered, _noResolveNeeded;
        private int _rechecked, _goodFirstRound, _goodAfterRecheck, _goodByFallback, _fallbackStarted, _finalUnknown, _finalDead;
        private int _resolving, _maxResolving;

        /// <summary>content_ids converted into a transport file, and those that were not.</summary>
        public int Resolved { get { return Volatile.Read(ref _resolved); } }
        public int ResolveFailed { get { return Volatile.Read(ref _resolveFailed); } }
        /// <summary>content_ids that were not resolved at first and were retried after RecheckDelay, and how many were resolved then.</summary>
        public int ResolveRetried { get { return Volatile.Read(ref _resolveRetried); } }
        public int ResolveRecovered { get { return Volatile.Read(ref _resolveRecovered); } }
        /// <summary>Links that did not need resolving (magnet, infohash, http, unsupported).</summary>
        public int NoResolveNeeded { get { return Volatile.Read(ref _noResolveNeeded); } }
        /// <summary>Links at zero (or without data) that were checked again (the 10 s rule).</summary>
        public int Rechecked { get { return Volatile.Read(ref _rechecked); } }
        public int GoodFirstRound { get { return Volatile.Read(ref _goodFirstRound); } }
        /// <summary>Links that were at zero in the first check and live in the second (what would have been lost without the rule).</summary>
        public int GoodAfterRecheck { get { return Volatile.Read(ref _goodAfterRecheck); } }
        public int GoodByFallback { get { return Volatile.Read(ref _goodByFallback); } }
        public int FallbackStarted { get { return Volatile.Read(ref _fallbackStarted); } }
        public int FinalUnknown { get { return Volatile.Read(ref _finalUnknown); } }
        public int FinalDead { get { return Volatile.Read(ref _finalDead); } }
        /// <summary>Simultaneous resolutions: maximum observed (never exceeds ProbeOptions.Concurrency).</summary>
        public int MaxResolving { get { return Volatile.Read(ref _maxResolving); } }

        internal void ResolveStarted()
        {
            int now = Interlocked.Increment(ref _resolving);
            int prev;
            while (now > (prev = Volatile.Read(ref _maxResolving))) Interlocked.CompareExchange(ref _maxResolving, now, prev);
        }

        internal void ResolveEnded() { Interlocked.Decrement(ref _resolving); }
        /// <summary>One entry for each resolution done over the network (with what happened in it): to see why some succeed and others do not.</summary>
        public sealed class ResolveRecord
        {
            public bool Ok, Retry;
            public string Key;
            public double Seconds;
            public AceEngine.Resolve.ResolveStats Stats;
            public ResolveFailure Failure;
        }

        private readonly List<ResolveRecord> _records = new List<ResolveRecord>();
        public IList<ResolveRecord> Records() { lock (_gate) return _records.ToList(); }

        internal void NoteResolve(bool ok, double seconds, bool wasRetry, AceEngine.Resolve.ResolveStats stats = null, ResolveFailure failure = ResolveFailure.None, string key = null)
        {
            lock (_gate) { (ok ? _resolveOk : _resolveFail).Add(seconds); if (stats != null) _records.Add(new ResolveRecord { Key = key, Ok = ok, Retry = wasRetry, Seconds = seconds, Stats = stats, Failure = failure }); }
            if (ok) { Interlocked.Increment(ref _resolved); if (wasRetry) Interlocked.Increment(ref _resolveRecovered); }
            else if (!wasRetry) Interlocked.Increment(ref _resolveFailed);
        }
        internal void NoteResolveRetried() { Interlocked.Increment(ref _resolveRetried); }
        private int _starvedRetries, _starvedGiveUps;
        /// <summary>Resolutions that ran out of resources and were retried, and those that still lacked them after the retries (they stay "checking", not hidden).</summary>
        public int StarvedRetries { get { return Volatile.Read(ref _starvedRetries); } }
        public int StarvedGiveUps { get { return Volatile.Read(ref _starvedGiveUps); } }
        internal void NoteStarvedRetry() { Interlocked.Increment(ref _starvedRetries); }
        internal void NoteStarvedGiveUp() { Interlocked.Increment(ref _starvedGiveUps); }
        internal void NoteNoResolveNeeded() { Interlocked.Increment(ref _noResolveNeeded); }
        internal void NoteRechecked() { Interlocked.Increment(ref _rechecked); }
        internal void NoteGood(int round, bool byFallback)
        {
            if (byFallback) Interlocked.Increment(ref _goodByFallback);
            else if (round <= 1) Interlocked.Increment(ref _goodFirstRound);
            else Interlocked.Increment(ref _goodAfterRecheck);
        }
        internal void NoteFallbackStarted() { Interlocked.Increment(ref _fallbackStarted); }
        internal void NoteFinal(LinkState s)
        {
            if (s == LinkState.Unknown) Interlocked.Increment(ref _finalUnknown);
            else if (s == LinkState.Dead) Interlocked.Increment(ref _finalDead);
        }

        /// <summary>Seconds the successful resolutions took: mean, median, p95 and maximum.</summary>
        public double[] ResolveOkSeconds() { return Summarize(_resolveOk); }
        public double[] ResolveFailSeconds() { return Summarize(_resolveFail); }

        private double[] Summarize(List<double> l)
        {
            lock (_gate)
            {
                if (l.Count == 0) return new double[] { 0, 0, 0, 0 };
                var s = l.OrderBy(x => x).ToList();
                return new[] { s.Average(), s[(int)Math.Floor((s.Count - 1) * 0.5)], s[(int)Math.Floor((s.Count - 1) * 0.95)], s[s.Count - 1] };
            }
        }
    }
}
