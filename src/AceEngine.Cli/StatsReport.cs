using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;

namespace AceEngine.Cli
{
    /// <summary>Samples the DHT and the resolution connections every few seconds, to see whether the response rate drops as the load rises.</summary>
    internal sealed class LoadSampler : IDisposable
    {
        internal sealed class Sample
        {
            public double T;
            public long Queries, Responses, Timeouts;
            public int InFlight, Connections;
        }

        private readonly DhtClient _dht;
        private readonly TransportResolver _resolver;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _loop;
        public readonly List<Sample> Samples = new List<Sample>();

        public LoadSampler(DhtClient dht, TransportResolver resolver, TimeSpan every)
        {
            _dht = dht;
            _resolver = resolver;
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        Take();
                        await Task.Delay(every, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private void Take()
        {
            var s = new Sample { T = _sw.Elapsed.TotalSeconds, Connections = _resolver.ActiveConnections };
            if (_dht != null) { s.Queries = _dht.QueriesSent; s.Responses = _dht.ResponsesReceived; s.Timeouts = _dht.QueryTimeouts; s.InFlight = _dht.InFlight; }
            lock (Samples) Samples.Add(s);
        }

        public void Dispose()
        {
            Take();
            _cts.Cancel();
            try { _loop.Wait(1000); } catch { }
        }
    }

    internal static class StatsReport
    {
        private static string F(double v, int dec = 1) { return v.ToString("F" + dec, CultureInfo.InvariantCulture); }

        public static void Print(ProbeScheduler sched, LinkProber prober, DhtClient dht, TransportResolver resolver, LoadSampler sampler, IList<LinkEntry> probed, double totalSeconds, EngineOptions opt)
        {
            var st = sched.Stats;
            var all = probed.Select(e => sched.GetResult(e)).ToList();
            var o = opt.Probe;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("==== Probe statistics ====");
            sb.AppendLine("Parameters: resolution " + o.Concurrency + " at a time (admitted " + prober.AdmittedResolutions + ", peak " + prober.PeakResolving + "; " + o.ResolveConnectionsPerLink + " connections per link, budget " + o.ResolveConnectionBudget + "); interval per tracker " + F(o.TrackerInterval.TotalSeconds) + " s (max. " + o.TrackerMaxConcurrent
                + " simultaneous); UDP batches " + o.UdpBatchSize + " / HTTP " + o.HttpBatchSize + "; recheck " + F(o.RecheckDelay.TotalSeconds, 0) + " s; DHT in flight max. " + opt.DhtMaxInFlight
                + "; resolution deadlines: " + F(o.ResolveTimeout.TotalSeconds, 0) + " s in total, connect " + (o.ResolveConnectTimeout > TimeSpan.Zero ? F(o.ResolveConnectTimeout.TotalSeconds, 1) : "8") + " s, handshake " + (o.ResolveHandshakeTimeout > TimeSpan.Zero ? F(o.ResolveHandshakeTimeout.TotalSeconds, 1) : "15") + " s");
            sb.AppendLine("Total time: " + F(totalSeconds) + " s");
            int good = all.Count(r => r.State == LinkState.Good);
            sb.AppendLine("Links: " + probed.Count + " probed; live " + good + " (first time " + st.GoodFirstRound + ", after the 2nd check " + st.GoodAfterRecheck + ", by DHT " + st.GoodByFallback
                + "), unknown " + all.Count(r => r.State == LinkState.Unknown) + ", dead " + all.Count(r => r.State == LinkState.Dead)
                + ", unchecked " + all.Count(r => r.State == LinkState.Pending || r.State == LinkState.Checking));
            sb.AppendLine("10 s rule: " + st.Rechecked + " links at zero or without data were checked again; " + st.GoodAfterRecheck + " came out live in the 2nd check (they would have been lost), "
                + st.GoodByFallback + " through the DHT alternative");
            var ok = st.ResolveOkSeconds(); var bad = st.ResolveFailSeconds();
            sb.AppendLine("content_id resolution: " + (st.Resolved + st.ResolveFailed - st.ResolveRecovered) + " links (" + st.NoResolveNeeded + " do not need it); resolved " + st.Resolved
                + ", unresolved at the first attempt " + st.ResolveFailed + " (retried " + st.ResolveRetried + ", recovered " + st.ResolveRecovered + "); maximum observed concurrency " + st.MaxResolving);
            sb.AppendLine("  time of the successful ones (s): mean " + F(ok[0]) + ", median " + F(ok[1]) + ", p95 " + F(ok[2]) + ", max " + F(ok[3]) + "   |   of the failed ones: mean " + F(bad[0]) + ", max " + F(bad[3]));
            sb.AppendLine("  resolution TCP connections: peak " + resolver.PeakConnections + " (budget " + o.ResolveConnectionBudget + "), attempted " + resolver.ConnectionAttempts + "; retries for lack of resources " + st.StarvedRetries + ", without resources after the retries " + st.StarvedGiveUps);
            PrintResolveDiagnostics(sb, st, opt);
            if (dht != null)
            {
                long q = dht.QueriesSent, r = dht.ResponsesReceived;
                sb.AppendLine("DHT: queries " + q + ", responses " + r + " (rate " + F(q == 0 ? 0 : 100.0 * r / q) + " %), unanswered " + dht.QueryTimeouts + ", errors " + dht.ErrorResponses + ", waited for a slot " + dht.QueriesQueued
                    + ", in flight max. " + dht.MaxInFlightObserved + " of " + opt.DhtMaxInFlight + ", known nodes " + dht.KnownNodes);
                if (sampler != null) PrintTimeline(sb, sampler);
            }
            var trackers = prober.Scrape.Limiter.Snapshot();
            long reqs = trackers.Sum(t => (long)t.Requests), hashes = trackers.Sum(t => (long)t.HashesSent);
            sb.AppendLine("Trackers: " + trackers.Count + " (host:port); " + reqs + " requests with " + hashes + " infohashes in total (average " + F(reqs == 0 ? 0 : (double)hashes / reqs) + " per request; without batches it would have been " + hashes + " requests)");
            sb.AppendLine("  " + "tracker".PadRight(34) + "req. resp. t.o. err. rjct. unsup.  hashes maxbtch pkts  lat.med/p95/max (ms)  min-gap(s)   backoff quar. benched");
            foreach (var t in trackers)
            {
                sb.AppendLine("  " + Shorten(t.Key, 33).PadRight(34)
                    + t.Requests.ToString().PadLeft(4) + t.Responses.ToString().PadLeft(6) + t.Timeouts.ToString().PadLeft(5) + t.Errors.ToString().PadLeft(5) + t.Rejections.ToString().PadLeft(6) + t.Unsupported.ToString().PadLeft(7)
                    + t.HashesSent.ToString().PadLeft(8) + t.MaxBatch.ToString().PadLeft(8) + t.Packets.ToString().PadLeft(5)
                    + "  " + (t.Responses == 0 ? "-".PadLeft(20) : (F(t.LatAvg, 0) + "/" + F(t.LatP95, 0) + "/" + F(t.LatMax, 0)).PadLeft(20))
                    + (t.MinGapSeconds < 0 ? "-" : F(t.MinGapSeconds, 2)).PadLeft(14) + t.BackoffEvents.ToString().PadLeft(8) + t.Quarantines.ToString().PadLeft(6) + (t.BenchedNow ? "  YES" : "   no")
                    + (t.SkippedHashes > 0 ? "  (" + t.SkippedHashes + " infohashes not asked)" : "") + (t.MinRequestIntervalAsked > 0 ? "  min_request_interval=" + t.MinRequestIntervalAsked + "s" : ""));
            }
            sb.AppendLine("Tracker totals: requests " + reqs + ", responses " + trackers.Sum(t => t.Responses) + ", timeouts " + trackers.Sum(t => t.Timeouts) + ", errors " + trackers.Sum(t => t.Errors)
                + ", rejections (429/403) " + trackers.Sum(t => t.Rejections) + ", without scrape " + trackers.Sum(t => t.Unsupported) + ", quarantines " + trackers.Sum(t => t.Quarantines)
                + ", max. simultaneous to the same tracker " + (trackers.Count == 0 ? 0 : trackers.Max(t => t.MaxConcurrentObserved)));
            sb.Append("SUMMARY conc=" + o.Concurrency + " total_s=" + F(totalSeconds) + " live=" + good + " probed=" + probed.Count + " resolved=" + st.Resolved + " after_recheck=" + st.GoodAfterRecheck
                + " requests=" + reqs + " resp=" + trackers.Sum(t => t.Responses) + " timeouts=" + trackers.Sum(t => t.Timeouts) + " errors=" + trackers.Sum(t => t.Errors) + " rejections=" + trackers.Sum(t => t.Rejections)
                + " dht_q=" + (dht == null ? 0 : dht.QueriesSent) + " dht_r=" + (dht == null ? 0 : dht.ResponsesReceived) + " dht_rate=" + F(dht == null || dht.QueriesSent == 0 ? 0 : 100.0 * dht.ResponsesReceived / dht.QueriesSent) + " conn_peak=" + resolver.PeakConnections);
            Console.WriteLine(sb.ToString());
        }

        private static double Pct(List<double> l, double p)
        {
            if (l.Count == 0) return 0;
            var s = l.OrderBy(x => x).ToList();
            return s[(int)Math.Floor((s.Count - 1) * p)];
        }

        private static string Dist(IEnumerable<double> v, int dec = 0)
        {
            var l = v.ToList();
            if (l.Count == 0) return "-";
            return F(l.Average(), dec) + " / " + F(Pct(l, 0.5), dec) + " / " + F(Pct(l, 0.95), dec) + " / " + F(l.Max(), dec);
        }

        /// <summary>What happened with each resolution: distribution of connections and DHT queries per link, and whether it ran out of resources.</summary>
        private static void PrintResolveDiagnostics(StringBuilder sb, ProbeRunStats st, EngineOptions opt)
        {
            var recs = st.Records().Where(r => r.Stats != null && !r.Retry).ToList();
            if (recs.Count == 0) return;
            var ok = recs.Where(r => r.Ok).ToList();
            var bad = recs.Where(r => !r.Ok).ToList();
            sb.AppendLine("Diagnostics of the network resolution (" + recs.Count + " links, first attempt only; each cell: mean / median / p95 / max):");
            Action<string, List<ProbeRunStats.ResolveRecord>> row = (name, l) =>
            {
                if (l.Count == 0) { sb.AppendLine("  " + name + ": none"); return; }
                sb.AppendLine("  " + name + " (" + l.Count + ")");
                sb.AppendLine("    time (s)                     " + Dist(l.Select(r => r.Seconds), 1));
                sb.AppendLine("    candidates (DHT peers)       " + Dist(l.Select(r => (double)r.Stats.Candidates)) + "   untried at the end: " + Dist(l.Select(r => (double)r.Stats.UntriedAtEnd)));
                sb.AppendLine("    1st candidate at (s)         " + Dist(l.Where(r => r.Stats.FirstCandidateSeconds >= 0).Select(r => r.Stats.FirstCandidateSeconds), 1) + "   without any candidate: " + l.Count(r => r.Stats.FirstCandidateSeconds < 0));
                sb.AppendLine("    connections attempted        " + Dist(l.Select(r => (double)r.Stats.Attempts)));
                sb.AppendLine("    connections at once (peak)" + " " + Dist(l.Select(r => (double)r.Stats.PeakConnections)) + "   budget: " + Dist(l.Select(r => (double)r.Stats.BudgetConnections)));
                sb.AppendLine("    DHT queries sent             " + Dist(l.Select(r => (double)r.Stats.Dht.Queries)) + "   responses: " + Dist(l.Select(r => (double)r.Stats.Dht.Responses)));
                sb.AppendLine("    DHT slot wait (s)            " + Dist(l.Select(r => r.Stats.Dht.SlotWaitSeconds), 1) + "   links waiting >25 % of the time: " + l.Count(r => r.Stats.DhtWaitFraction > 0.25));
            };
            row("resolved", ok);
            row("unresolved", bad);
            if (bad.Count > 0)
                sb.AppendLine("  unresolved: nobody serves it " + bad.Count(r => r.Failure == ResolveFailure.NoServer) + ", lack of resources (retried, not hidden) " + bad.Count(r => r.Failure == ResolveFailure.Starved));
            var all = recs.Select(r => r.Stats).ToList();
            int att = all.Sum(s => s.Attempts);
            if (att > 0)
            {
                var hs = all.SelectMany(s => s.HandshakeOkMs).ToList();
                sb.AppendLine("  connection attempts " + att + ": connect timeout " + all.Sum(s => s.ConnectTimeouts) + " (" + F(100.0 * all.Sum(s => s.ConnectTimeouts) / att, 0) + " %), refused/no route " + all.Sum(s => s.Refused)
                    + ", handshake timeout " + all.Sum(s => s.HandshakeTimeouts) + ", closed by the peer " + all.Sum(s => s.ClosedByPeer) + ", foreign protocol " + all.Sum(s => s.ProtocolRejected)
                    + ", cancelled " + all.Sum(s => s.Cancelled) + ", other " + all.Sum(s => s.OtherFailures) + ", system resource errors " + all.Sum(s => s.ResourceErrors) + "; handshake ok " + hs.Count);
                double slotTo = all.Sum(s => s.SlotSecondsTimeouts), slotOther = all.Sum(s => s.SlotSecondsOther);
                sb.AppendLine("  connection slot time: " + F(slotTo, 0) + " s in attempts that ended in a timeout (" + F(100.0 * slotTo / Math.Max(0.001, slotTo + slotOther), 0) + " %) versus " + F(slotOther, 0) + " s in the rest");
                if (hs.Count > 0) sb.AppendLine("  handshake of the peers that answered (ms): median " + F(Pct(hs, 0.5), 0) + ", p95 " + F(Pct(hs, 0.95), 0) + ", p99 " + F(Pct(hs, 0.99), 0) + ", max " + F(hs.Max(), 0));
            }
        }

        public static void WriteCsv(string path, ProbeScheduler sched, IList<LinkEntry> probed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("key;ok;retry;seconds;failure;candidates;untried;first_candidate_s;attempts;peak_conn;budget;connect_timeouts;refused;handshake_timeouts;handshakes_ok;dht_q;dht_r;dht_wait_s;final_state");
            var recs = sched.Stats.Records();
            foreach (var r in recs)
            {
                var s = r.Stats;
                string state = "";
                var e = probed.FirstOrDefault(x => x.Key == r.Key);
                if (e != null) state = sched.GetResult(e).State.ToString();
                if (s == null) { sb.AppendLine(r.Key + ";" + (r.Ok ? 1 : 0) + ";" + (r.Retry ? 1 : 0) + ";" + F(r.Seconds, 2) + ";;;;;;;;;;;;;;;;" + state); continue; }
                sb.AppendLine(r.Key + ";" + (r.Ok ? 1 : 0) + ";" + (r.Retry ? 1 : 0) + ";" + F(r.Seconds, 2) + ";" + r.Failure + ";" + s.Candidates + ";" + s.UntriedAtEnd + ";" + F(s.FirstCandidateSeconds, 2) + ";" + s.Attempts + ";" + s.PeakConnections + ";" + s.BudgetConnections
                    + ";" + s.ConnectTimeouts + ";" + s.Refused + ";" + s.HandshakeTimeouts + ";" + s.HandshakesOk + ";" + s.Dht.Queries + ";" + s.Dht.Responses + ";" + F(s.Dht.SlotWaitSeconds, 2) + ";" + state);
            }
            System.IO.File.WriteAllText(path, sb.ToString());
        }

        private static void PrintTimeline(StringBuilder sb, LoadSampler sampler)
        {
            List<LoadSampler.Sample> s;
            lock (sampler.Samples) s = sampler.Samples.ToList();
            if (s.Count < 2) return;
            sb.AppendLine("  DHT timeline (~10 s windows: queries, response rate, in flight, connections):");
            int i = 0;
            double minRate = 101; double minAt = 0;
            var line = new StringBuilder();
            while (i < s.Count - 1)
            {
                int j = i + 1;
                while (j < s.Count - 1 && s[j].T - s[i].T < 9.5) j++;
                long dq = s[j].Queries - s[i].Queries, dr = s[j].Responses - s[i].Responses;
                double rate = dq == 0 ? -1 : 100.0 * dr / dq;
                if (dq >= 30 && rate < minRate) { minRate = rate; minAt = s[i].T; }
                line.Append("    t=" + F(s[i].T, 0).PadLeft(4) + "-" + F(s[j].T, 0).PadLeft(4) + " s  q=" + dq.ToString().PadLeft(5) + "  rate=" + (rate < 0 ? "  -" : F(rate, 0).PadLeft(3) + "%") + "  in flight=" + s[j].InFlight.ToString().PadLeft(3) + "  conn=" + s[j].Connections.ToString().PadLeft(3) + "\n");
                i = j;
            }
            sb.Append(line.ToString());
            if (minRate <= 100) sb.AppendLine("  minimum response rate in a window with >= 30 queries: " + F(minRate, 0) + " % (around t=" + F(minAt, 0) + " s)");
        }

        private static string Shorten(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "~"; }
    }
}
