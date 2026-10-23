using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Probe
{
    /// <summary>How a batch of infohashes is sent to a tracker (the real implementation uses the network; the tests, simulated trackers).</summary>
    public interface IScrapeTransport
    {
        /// <summary>How many of these infohashes fit in one request to this tracker (at least 1). UDP: up to ~70; HTTP: up to 50 and without exceeding the URL length.</summary>
        int FitCount(string trackerUrl, IList<byte[]> infoHashes);
        /// <summary>A single request with all the infohashes. One result per infohash, in the same order.</summary>
        Task<IList<ScrapeResult>> ScrapeAsync(string trackerUrl, IList<byte[]> infoHashes, CancellationToken ct);
    }

    /// <summary>Scrape real: UDP (BEP 15) o HTTP (BEP 48) en lotes.</summary>
    public sealed class NetScrapeTransport : IScrapeTransport
    {
        private readonly HttpClient _http;
        private readonly ProbeOptions _o;
        private readonly UdpScraper _udp;

        public NetScrapeTransport(HttpClient http, ProbeOptions options)
        {
            _http = http;
            _o = options;
            _udp = new UdpScraper { ConnectionTtl = options.UdpConnectionTtl };
        }

        public int FitCount(string trackerUrl, IList<byte[]> infoHashes)
        {
            Uri u;
            if (infoHashes.Count == 0) return 0;
            if (Uri.TryCreate(trackerUrl, UriKind.Absolute, out u) && u.Scheme == "udp") return Math.Max(1, Math.Min(Math.Min(infoHashes.Count, _o.UdpBatchSize), UdpScrape.MaxHashesPerPacket));
            return HttpScrape.FitCount(trackerUrl, infoHashes, Math.Max(1, _o.HttpBatchSize), Math.Max(256, _o.HttpMaxUrlLength));
        }

        public async Task<IList<ScrapeResult>> ScrapeAsync(string trackerUrl, IList<byte[]> infoHashes, CancellationToken ct)
        {
            Uri u;
            if (!Uri.TryCreate(trackerUrl, UriKind.Absolute, out u))
                return infoHashes.Select(h => new ScrapeResult { Tracker = trackerUrl, Status = ScrapeStatus.Unsupported, Error = "invalid URL" }).ToList();
            if (u.Scheme == "http" || u.Scheme == "https") return await HttpScrape.ScrapeBatchAsync(_http, trackerUrl, infoHashes, _o.HttpTimeout, ct).ConfigureAwait(false);
            if (u.Scheme == "udp") return await _udp.ScrapeAsync(u.Host, u.Port > 0 ? u.Port : 80, trackerUrl, infoHashes, _o.UdpTimeoutsSeconds, ct).ConfigureAwait(false);
            return infoHashes.Select(h => new ScrapeResult { Tracker = trackerUrl, Status = ScrapeStatus.Unsupported, Error = "scheme " + u.Scheme }).ToList();
        }
    }

    /// <summary>
    /// Grouped counting per tracker. Each link asks "give me the numbers of this infohash on this tracker"; the service gathers what is pending for each tracker
    /// (host:port) and sends ONE request per batch (UDP ~70 infohashes, HTTP up to 50), at most one every TrackerInterval, with queue, backoff and quarantine
    /// (TrackerLimiter). There are never more than TrackerMaxConcurrent requests at once to the same tracker, and 50 links asking for the same tracker are 1 request.
    /// </summary>
    public sealed class TrackerScrapeService : IDisposable
    {
        private sealed class Item
        {
            public byte[] Hash;
            public string HashHex;
            public TaskCompletionSource<ScrapeResult> Tcs = new TaskCompletionSource<ScrapeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Reg;
        }

        private sealed class Queue
        {
            public string Key;
            public string Url;
            public readonly List<Item> Pending = new List<Item>();
            public int Workers;
        }

        private readonly IScrapeTransport _transport;
        private readonly TrackerLimiter _limiter;
        private readonly ProbeOptions _o;
        private readonly Dictionary<string, Queue> _queues = new Dictionary<string, Queue>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _disposed;

        public TrackerScrapeService(IScrapeTransport transport, TrackerLimiter limiter, ProbeOptions options)
        {
            _transport = transport;
            _limiter = limiter;
            _o = options;
        }

        public TrackerLimiter Limiter { get { return _limiter; } }

        /// <summary>Scrape requests sent so far (batches), across all trackers.</summary>
        public int RequestsSent { get { return _limiter.Snapshot().Sum(x => x.Requests); } }

        /// <summary>
        /// Numbers of an infohash on a tracker. The real request is a batch together with other infohashes pending for that tracker. It is cancelled with ct
        /// (if the link no longer needs it and the batch has not gone out, the infohash is not even sent). A tracker that is waiting or benched returns a Skipped result.
        /// </summary>
        public Task<ScrapeResult> ScrapeAsync(string trackerUrl, byte[] infoHash, CancellationToken ct)
        {
            string key = TrackerKey.From(trackerUrl);
            if (key == null)
                return Task.FromResult(new ScrapeResult { Tracker = trackerUrl, Status = ScrapeStatus.Unsupported, Error = "invalid tracker URL" });
            if (ct.IsCancellationRequested) return CanceledTask(ct);
            var item = new Item { Hash = infoHash, HashHex = Hex.ToHex(infoHash) };
            if (ct.CanBeCanceled) item.Reg = ct.Register(() => item.Tcs.TrySetCanceled(ct));
            bool start = false;
            Queue q;
            lock (_gate)
            {
                if (_disposed != 0) { item.Tcs.TrySetCanceled(); return item.Tcs.Task; }
                if (!_queues.TryGetValue(key, out q)) { q = new Queue { Key = key, Url = trackerUrl }; _queues[key] = q; }
                else if (q.Pending.Count == 0 && q.Workers == 0) q.Url = PreferUdp(q.Url, trackerUrl);
                q.Pending.Add(item);
                if (q.Workers < Math.Max(1, Math.Min(2, _o.TrackerMaxConcurrent))) { q.Workers++; start = true; }
            }
            if (start) { var qq = q; var ignored = Task.Run(() => WorkerAsync(qq)); }
            return item.Tcs.Task;
        }

        private static Task<ScrapeResult> CanceledTask(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ScrapeResult>();
            tcs.TrySetCanceled(ct);
            return tcs.Task;
        }

        private static string PreferUdp(string current, string other)
        {
            return current.StartsWith("udp:", StringComparison.OrdinalIgnoreCase) ? current : (other.StartsWith("udp:", StringComparison.OrdinalIgnoreCase) ? other : current);
        }

        /// <summary>Infohashes pending to be sent to this tracker (for tests and diagnostics).</summary>
        public int PendingFor(string key)
        {
            lock (_gate)
            {
                Queue q;
                if (!_queues.TryGetValue(key, out q)) return 0;
                return q.Pending.Count(i => !i.Tcs.Task.IsCompleted);
            }
        }

        private async Task WorkerAsync(Queue q)
        {
            var token = _cts.Token;
            bool leftClean = false;
            bool windowDone = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        q.Pending.RemoveAll(i => i.Tcs.Task.IsCompleted);
                        if (q.Pending.Count == 0) { q.Workers--; leftClean = true; return; }
                    }
                    var delay = _limiter.Delay(q.Key);
                    if (delay > _o.MaxQueueWait)
                    {
                        // the tracker will take too long to be able to serve us (backoff, quarantine, min_request_interval): the links are not made to wait
                        FlushSkipped(q, "tracker waiting or benched (" + (int)delay.TotalSeconds + " s)");
                        continue;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                        windowDone = true;                        // what accumulated during the wait already forms the batch
                        continue;
                    }
                    if (!windowDone && _o.BatchWindow > TimeSpan.Zero)
                    {
                        // the tracker is free: wait a moment for more links to arrive so as not to spend a request on a batch of one
                        windowDone = true;
                        try { await Task.Delay(_o.BatchWindow, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    if (!_limiter.TryBegin(q.Key)) { try { await Task.Delay(20, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; } continue; }
                    List<Item> batch;
                    string url;
                    lock (_gate)
                    {
                        q.Pending.RemoveAll(i => i.Tcs.Task.IsCompleted);
                        url = q.Url;
                        batch = TakeBatch(q);
                    }
                    if (batch.Count == 0) { _limiter.Abort(q.Key); continue; }
                    windowDone = false;
                    await RunBatchAsync(q, url, batch, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                AceLog.Warn("scrape", "worker of " + q.Key + ": " + ex.Message);
            }
            finally
            {
                if (!leftClean)
                {
                    bool restart = false;
                    lock (_gate)
                    {
                        if (token.IsCancellationRequested)
                        {
                            foreach (var i in q.Pending) i.Tcs.TrySetCanceled();
                            q.Pending.Clear();
                            q.Workers = Math.Max(0, q.Workers - 1);
                        }
                        else
                        {
                            // exit due to an unexpected error: if work remains, a new worker; if not, this one is retired
                            q.Pending.RemoveAll(i => i.Tcs.Task.IsCompleted);
                            if (q.Pending.Count > 0) restart = true; else q.Workers = Math.Max(0, q.Workers - 1);
                        }
                    }
                    if (restart) { var ignored = Task.Run(() => WorkerAsync(q)); }
                }
            }
        }

        /// <summary>Takes from the queue up to a batch of distinct infohashes (repeated ones travel together). Called with the lock held.</summary>
        private List<Item> TakeBatch(Queue q)
        {
            var unique = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in q.Pending)
            {
                if (unique.Count >= 128) break;
                if (seen.Add(i.HashHex)) unique.Add(i.Hash);
            }
            int n = unique.Count == 0 ? 0 : Math.Max(1, Math.Min(_transport.FitCount(q.Url, unique), unique.Count));
            var take = new HashSet<string>(unique.Take(n).Select(Hex.ToHex), StringComparer.Ordinal);
            var batch = q.Pending.Where(i => take.Contains(i.HashHex)).ToList();
            q.Pending.RemoveAll(i => take.Contains(i.HashHex));
            return batch;
        }

        private void FlushSkipped(Queue q, string reason)
        {
            List<Item> items;
            lock (_gate) { items = q.Pending.Where(i => !i.Tcs.Task.IsCompleted).ToList(); q.Pending.Clear(); }
            if (items.Count == 0) return;
            _limiter.NoteSkipped(q.Key, items.Select(i => i.HashHex).Distinct().Count());
            foreach (var i in items) i.Tcs.TrySetResult(new ScrapeResult { Tracker = q.Url, Status = ScrapeStatus.Wait, Skipped = true, Error = reason });
        }

        private async Task RunBatchAsync(Queue q, string url, List<Item> batch, CancellationToken token)
        {
            var hashes = new List<byte[]>();
            var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var i in batch) if (!firstOf.ContainsKey(i.HashHex)) { firstOf[i.HashHex] = hashes.Count; hashes.Add(i.Hash); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IList<ScrapeResult> results;
            try { results = await _transport.ScrapeAsync(url, hashes, token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _limiter.Abort(q.Key);
                foreach (var i in batch) i.Tcs.TrySetCanceled();
                return;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                results = hashes.Select(h => new ScrapeResult { Tracker = url, Status = ScrapeStatus.NetworkError, Error = ex.GetType().Name + ": " + ex.Message }).ToList();
            }
            if (results == null || results.Count != hashes.Count)
                results = hashes.Select(h => new ScrapeResult { Tracker = url, Status = ScrapeStatus.NetworkError, Error = "incomplete batch response" }).ToList();
            var first = results[0];
            int mri = results.Max(r => r.MinRequestInterval);
            var outcome = Classify(results);
            AceLog.Debug("scrape", q.Key + ": batch of " + hashes.Count + " -> " + outcome + (first.Error != null ? " (" + first.Error + ")" : "") + " in " + (int)sw.Elapsed.TotalMilliseconds + " ms");
            _limiter.End(q.Key, outcome, sw.Elapsed, hashes.Count, first.Packets, mri, first.RetryAfterSeconds);
            if (results.Any(r => r.Status == ScrapeStatus.Wait && !r.Skipped)) _limiter.Wait(q.Key, mri);
            foreach (var i in batch)
            {
                var r = results[firstOf[i.HashHex]];
                i.Tcs.TrySetResult(r);
            }
        }

        /// <summary>Overall result of the batch for the brake.</summary>
        public static RequestOutcome Classify(IList<ScrapeResult> results)
        {
            var r = results[0];
            if (results.Any(x => x.Rejected)) return RequestOutcome.Rejected;
            if (results.All(x => x.Status == ScrapeStatus.Unsupported)) return RequestOutcome.Unsupported;
            if (results.Any(x => x.Status == ScrapeStatus.Ok || x.Status == ScrapeStatus.Wait)) return RequestOutcome.Ok;
            if (r.Status == ScrapeStatus.NetworkError) return r.TimedOut ? RequestOutcome.Timeout : RequestOutcome.Error;
            if (results.All(x => x.Malformed)) return RequestOutcome.Error;
            // Invalid: the tracker answered (it does not know the infohash, or gave a 'failure reason'); it is alive
            return RequestOutcome.Ok;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            lock (_gate)
            {
                foreach (var q in _queues.Values) { foreach (var i in q.Pending) i.Tcs.TrySetCanceled(); q.Pending.Clear(); }
            }
        }
    }
}
