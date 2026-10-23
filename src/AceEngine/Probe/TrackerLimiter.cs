using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AceEngine.Probe
{
    /// <summary>How a request to a tracker ended, for the brake and the statistics.</summary>
    public enum RequestOutcome
    {
        /// <summary>The tracker answered with something usable (even if it says it does not know the infohash).</summary>
        Ok,
        /// <summary>No answer in time (UDP without response, HTTP without headers).</summary>
        Timeout,
        /// <summary>Network error, HTTP 5xx, response that is not bencode.</summary>
        Error,
        /// <summary>HTTP 429/403/451... or an error that speaks of a limit or a block: stay away.</summary>
        Rejected,
        /// <summary>The tracker does not support scrape (HTTP 404 on /scrape): it is not a failure, but it is not tried again for a good while.</summary>
        Unsupported
    }

    /// <summary>Identity of a tracker for the brake: host:port in lowercase, without scheme or path (udp and http on the same port count as one).</summary>
    public static class TrackerKey
    {
        public static string From(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            if (u.Scheme != "udp" && u.Scheme != "http" && u.Scheme != "https") return null;
            int port = u.Port > 0 ? u.Port : (u.Scheme == "https" ? 443 : 80);
            return u.Host.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Summary of what happened with a tracker during a probe (for `--stats`).</summary>
    public sealed class TrackerStats
    {
        public string Key;
        /// <summary>Requests (batches) sent: one per batch, not per infohash.</summary>
        public int Requests;
        /// <summary>Requests with a usable response.</summary>
        public int Responses;
        public int Timeouts, Errors, Rejections, Unsupported;
        /// <summary>Infohashes sent in total and the largest batch.</summary>
        public int HashesSent, MaxBatch;
        /// <summary>UDP packets sent (connect + scrape + retries). 0 on HTTP.</summary>
        public int Packets;
        /// <summary>Infohashes that were never asked because the tracker was waiting, benched or the queue expired.</summary>
        public int SkippedHashes;
        public int BackoffEvents, Quarantines;
        public int ConsecutiveFailures;
        public bool BenchedNow;
        /// <summary>Latency (ms) of the requests with a response.</summary>
        public double LatMin, LatAvg, LatP50, LatP95, LatMax;
        /// <summary>Smallest gap (s) between two request starts to the same tracker: it shows that the interval was respected. -1 if there were fewer than two.</summary>
        public double MinGapSeconds = -1;
        public int MaxConcurrentObserved;
        public double FirstRequestSeconds = -1, LastRequestSeconds = -1;
        /// <summary>Seconds of waiting the tracker asked for with min_request_interval (the largest).</summary>
        public int MinRequestIntervalAsked;

        public TrackerStats Clone() { return (TrackerStats)MemberwiseClone(); }
    }

    /// <summary>
    /// Per-tracker brake (host:port): at most 1 request every TrackerInterval, never more than TrackerMaxConcurrent at once, honors
    /// min_request_interval and Retry-After, exponential backoff on timeouts and errors and benches the tracker (quarantine) if it keeps failing.
    /// It does not block or wait: it says how long to wait (Delay) and is told when a request starts and ends (Begin / End). The clock is injectable for tests.
    /// </summary>
    public sealed class TrackerLimiter
    {
        private sealed class State
        {
            public string Key;
            public DateTime NextAllowed = DateTime.MinValue;
            public DateTime BenchUntil = DateTime.MinValue;
            public DateTime LastStart = DateTime.MinValue;
            public DateTime PrevNextAllowed, PrevLastStart;
            public int InFlight;
            public int Failures, RejectCount, QuarantineRounds;
            public readonly TrackerStats Stats = new TrackerStats();
            public readonly List<double> Latencies = new List<double>();
        }

        private readonly ProbeOptions _o;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new object();
        private readonly Dictionary<string, State> _t = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        private readonly DateTime _t0;

        public TrackerLimiter(ProbeOptions options, Func<DateTime> now = null)
        {
            _o = options;
            _now = now ?? (() => DateTime.UtcNow);
            _t0 = _now();
        }

        private State Get(string key)
        {
            State s;
            if (!_t.TryGetValue(key, out s)) { s = new State { Key = key }; s.Stats.Key = key; _t[key] = s; }
            return s;
        }

        private int MaxConcurrent { get { return Math.Max(1, Math.Min(2, _o.TrackerMaxConcurrent)); } }

        /// <summary>How long to wait before another request can be sent to this tracker (zero = now). Includes the interval, the backoff and the quarantine.</summary>
        public TimeSpan Delay(string key)
        {
            lock (_gate)
            {
                var s = Get(key);
                var now = _now();
                var until = s.NextAllowed > s.BenchUntil ? s.NextAllowed : s.BenchUntil;
                var wait = until - now;
                if (wait <= TimeSpan.Zero && s.InFlight >= MaxConcurrent) return TimeSpan.FromMilliseconds(50);
                return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
        }

        /// <summary>true if the tracker is benched (quarantine or no scrape) at this moment.</summary>
        public bool IsBenched(string key)
        {
            lock (_gate) return Get(key).BenchUntil > _now();
        }

        /// <summary>Records the start of a request if the brake allows it; false if it is not time yet.</summary>
        public bool TryBegin(string key)
        {
            lock (_gate)
            {
                var s = Get(key);
                var now = _now();
                if (s.InFlight >= MaxConcurrent || now < s.NextAllowed || now < s.BenchUntil) return false;
                s.PrevNextAllowed = s.NextAllowed; s.PrevLastStart = s.LastStart;
                if (s.LastStart != DateTime.MinValue)
                {
                    double gap = (now - s.LastStart).TotalSeconds;
                    if (s.Stats.MinGapSeconds < 0 || gap < s.Stats.MinGapSeconds) s.Stats.MinGapSeconds = gap;
                }
                s.LastStart = now;
                s.InFlight++;
                if (s.InFlight > s.Stats.MaxConcurrentObserved) s.Stats.MaxConcurrentObserved = s.InFlight;
                s.NextAllowed = now + _o.TrackerInterval;
                double since = (now - _t0).TotalSeconds;
                if (s.Stats.FirstRequestSeconds < 0) s.Stats.FirstRequestSeconds = since;
                s.Stats.LastRequestSeconds = since;
                return true;
            }
        }

        /// <summary>Undoes a Begin that in the end sent nothing (the whole batch was cancelled).</summary>
        public void Abort(string key)
        {
            lock (_gate)
            {
                var s = Get(key);
                if (s.InFlight > 0) s.InFlight--;
                s.NextAllowed = s.PrevNextAllowed; s.LastStart = s.PrevLastStart;
            }
        }

        /// <summary>Records the end of a request with its result: updates backoff, quarantine and statistics.</summary>
        public void End(string key, RequestOutcome outcome, TimeSpan latency, int batchSize, int packets, int minRequestIntervalSeconds, int retryAfterSeconds)
        {
            lock (_gate)
            {
                var s = Get(key);
                var now = _now();
                if (s.InFlight > 0) s.InFlight--;
                var st = s.Stats;
                st.Requests++;
                st.HashesSent += batchSize;
                if (batchSize > st.MaxBatch) st.MaxBatch = batchSize;
                st.Packets += packets;
                if (minRequestIntervalSeconds > st.MinRequestIntervalAsked) st.MinRequestIntervalAsked = minRequestIntervalSeconds;
                switch (outcome)
                {
                    case RequestOutcome.Ok:
                        st.Responses++;
                        s.Latencies.Add(latency.TotalMilliseconds);
                        s.Failures = 0; s.RejectCount = 0; s.QuarantineRounds = 0;
                        if (minRequestIntervalSeconds > 0) Push(s, now + TimeSpan.FromSeconds(Math.Min(3600, minRequestIntervalSeconds)));
                        break;
                    case RequestOutcome.Timeout:
                    case RequestOutcome.Error:
                        if (outcome == RequestOutcome.Timeout) st.Timeouts++; else st.Errors++;
                        s.Failures++;
                        st.BackoffEvents++;
                        Push(s, now + Backoff(_o.TrackerBackoffBase, s.Failures));
                        if (s.Failures >= _o.TrackerQuarantineAfter) Quarantine(s, now);
                        break;
                    case RequestOutcome.Rejected:
                        st.Rejections++;
                        s.Failures++; s.RejectCount++;
                        st.BackoffEvents++;
                        var baseDelay = TimeSpan.FromSeconds(Math.Max(retryAfterSeconds, _o.TrackerRejectedBackoff.TotalSeconds));
                        Push(s, now + Backoff(baseDelay, s.RejectCount));
                        if (s.RejectCount >= _o.TrackerQuarantineAfterRejections) Quarantine(s, now);
                        break;
                    case RequestOutcome.Unsupported:
                        st.Unsupported++;
                        s.BenchUntil = now + _o.TrackerUnsupportedBench;
                        break;
                }
                st.ConsecutiveFailures = s.Failures;
            }
        }

        /// <summary>The tracker asked to wait (flags.min_request_interval, or -3 in the original): it is not asked before. With no figure, DefaultWait.</summary>
        public void Wait(string key, int seconds)
        {
            lock (_gate)
            {
                var s = Get(key);
                var d = seconds > 0 ? TimeSpan.FromSeconds(Math.Min(3600, seconds)) : _o.DefaultWait;
                if (seconds > s.Stats.MinRequestIntervalAsked) s.Stats.MinRequestIntervalAsked = seconds;
                Push(s, _now() + d);
            }
        }

        /// <summary>Notes infohashes that were never asked to this tracker.</summary>
        public void NoteSkipped(string key, int hashes)
        {
            lock (_gate) Get(key).Stats.SkippedHashes += hashes;
        }

        private static void Push(State s, DateTime t)
        {
            if (t > s.NextAllowed) s.NextAllowed = t;
        }

        private TimeSpan Backoff(TimeSpan unit, int n)
        {
            double secs = unit.TotalSeconds * Math.Pow(2, Math.Min(Math.Max(0, n - 1), 16));
            return TimeSpan.FromSeconds(Math.Min(_o.TrackerBackoffMax.TotalSeconds, secs));
        }

        private void Quarantine(State s, DateTime now)
        {
            s.QuarantineRounds++;
            s.Stats.Quarantines++;
            double secs = _o.TrackerQuarantine.TotalSeconds * Math.Pow(2, Math.Min(s.QuarantineRounds - 1, 16));
            var until = now + TimeSpan.FromSeconds(Math.Min(_o.TrackerQuarantineMax.TotalSeconds, secs));
            if (until > s.BenchUntil) s.BenchUntil = until;
        }

        public TrackerStats Stats(string key)
        {
            lock (_gate) { State s; return _t.TryGetValue(key, out s) ? Fill(s) : new TrackerStats { Key = key }; }
        }

        /// <summary>Statistics of all the trackers seen, the one that received the most requests first.</summary>
        public IList<TrackerStats> Snapshot()
        {
            lock (_gate) return _t.Values.Select(Fill).OrderByDescending(x => x.Requests).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        }

        private TrackerStats Fill(State s)
        {
            var c = s.Stats;
            var copy = c.Clone();
            copy.BenchedNow = s.BenchUntil > _now();
            if (s.Latencies.Count > 0)
            {
                var l = s.Latencies.OrderBy(x => x).ToList();
                copy.LatMin = l[0]; copy.LatMax = l[l.Count - 1]; copy.LatAvg = l.Average();
                copy.LatP50 = l[(int)Math.Floor((l.Count - 1) * 0.5)];
                copy.LatP95 = l[(int)Math.Floor((l.Count - 1) * 0.95)];
            }
            return copy;
        }
    }
}
