using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm
    {
        private int _waiters;
        private long _lastRechoke, _lastOptimistic;

        public void ReaderWaiting(bool waiting)
        {
            if (waiting) Interlocked.Increment(ref _waiters); else Interlocked.Decrement(ref _waiters);
        }

        private async Task TickLoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var toStart = new List<PeerConnection>();
                var toClose = new List<PeerConnection>();
                try
                {
                    lock (_sync)
                    {
                        if (_disposed) break;
                        long now = PeerConnection.NowMs();
                        if (_meta != null)
                        {
                            UpdateWindowLocked();
                            CheckOutstandingLocked(now);
                            CheckSnubbedLocked(now);
                            DropStalePartialsLocked();
                            if (UsedBytesLocked > _opt.MaxBufferBytes) EvictLocked(UsedBytesLocked - _opt.MaxBufferBytes);
                            foreach (var p in PeersByRateLocked()) { UpdateInterestLocked(p); FillRequestsLocked(p); }
                            if (now - _lastRechoke >= 5000)
                            {
                                bool rotate = now - _lastOptimistic >= 30000;
                                if (rotate) _lastOptimistic = now;
                                _lastRechoke = now;
                                RechokeLocked(rotate);
                            }
                            CheckPrebufferLocked();
                        }
                        else
                        {
                            foreach (var p in _peers)
                            {
                                if (p.MetadataPieceInFlight >= 0 && now - p.LastBlockMs > 10000) p.MetadataPieceInFlight = -1;
                                RequestMetadataPieceLocked(p);
                            }
                            if (_opt.MetadataTimeout > TimeSpan.Zero && DateTime.UtcNow - _startedUtc > _opt.MetadataTimeout)
                            {
                                var ex = new TimeoutException("no se obtuvieron los metadatos del magnet en " + _opt.MetadataTimeout.TotalSeconds + " s");
                                Fatal = ex;
                                _message = ex.Message;
                                _metaReady.TrySetException(ex);
                            }
                        }

                        foreach (var p in _peers)
                        {
                            if (now - p.LastReceiveMs > (long)_opt.PeerIdleTimeout.TotalMilliseconds) { toClose.Add(p); continue; }
                            if (now - p.LastKeepAliveSentMs > (long)_opt.KeepAliveInterval.TotalMilliseconds)
                            {
                                p.LastKeepAliveSentMs = now;
                                p.Send(Wire.KeepAlive);
                            }
                        }
                        // Full of peers: one that has been choking us for 45 s without giving a single byte is dropped, to make room for new candidates
                        if (_all.Count >= _opt.MaxPeers - 1 && _cands.Values.Any(c => !c.InUse && c.NextAttemptMs <= now))
                        {
                            var victim = _peers.Where(p => p.PeerChoking && p.Down.Total == 0 && now - p.CreatedMs > 45000)
                                .OrderBy(p => p.CreatedMs).FirstOrDefault();
                            if (victim != null) { AceLog.Debug(Tag, victim + " contributes nothing in 45 s: dropped"); toClose.Add(victim); }
                        }
                        ConnectMoreLocked(now, toStart);
                        UpdatePhaseLocked();
                    }
                }
                catch (Exception ex)
                {
                    AceLog.Error(Tag, "tick: " + ex);
                }
                StartPeers(toStart);
                foreach (var p in toClose) p.Close("inactive");
            }
        }

        private void UpdatePhaseLocked()
        {
            SwarmPhase target;
            if (_meta == null) target = SwarmPhase.Resolving;
            else if (_peers.Count == 0 && _store.Count == 0) target = SwarmPhase.Connecting;
            else if (!_prebufDone) target = SwarmPhase.Buffering;
            else if (Volatile.Read(ref _waiters) > 0) target = SwarmPhase.Buffering;
            else target = SwarmPhase.Playing;
            SetPhaseLocked(target);
        }

        /// <summary>check_outstanding_requests of PiecePickerVOD: cancels requests that will not arrive in time.</summary>
        private void CheckOutstandingLocked(long now)
        {
            bool playing = _prebufDone;
            double minD = playing ? _opt.PlayingDelayMin : _opt.BufferingDelayMin;
            double maxD = playing ? _opt.PlayingDelayMax : _opt.BufferingDelayMax;
            double off = playing ? _opt.PlayingDelayOffset : _opt.BufferingDelayOffset;
            foreach (var peer in _peers)
            {
                if (peer.Outstanding.Count == 0) continue;
                double rate = peer.Down.Rate;
                long total = 0;
                List<BlockRef> cancels = null;
                foreach (var b in peer.Outstanding)
                {
                    total += b.Length;
                    double age = (now - b.RequestedAtMs) / 1000.0;
                    if (age < minD) continue;
                    bool critical = InHighRange(b.Piece) || (!_prebufDone && _win.PrebufNeeded.Contains(b.Piece));
                    if (!critical) continue;
                    bool cancel;
                    if (rate <= 0) cancel = true;
                    else
                    {
                        double remaining = maxD - age;
                        double deadline = playing ? Math.Min(PieceDueSeconds(b.Piece), remaining) : remaining;
                        cancel = deadline < total / rate - off;
                    }
                    if (cancel)
                    {
                        if (cancels == null) cancels = new List<BlockRef>();
                        cancels.Add(b);
                    }
                }
                if (cancels == null) continue;
                foreach (var b in cancels)
                {
                    peer.Outstanding.Remove(b);
                    ReleaseBlockLocked(b);
                    peer.Send(Wire.Cancel(b.Piece, b.Begin, b.Length));
                }
                peer.BadPerformance++;
                AceLog.Debug(Tag, peer + " cancels " + cancels.Count + " slow requests (rate " + (int)rate + " B/s, bad=" + peer.BadPerformance + ")");
            }
        }

        /// <summary>An unchoked peer that delivers no data within SnubTime is treated as choked (snubbing).</summary>
        private void CheckSnubbedLocked(long now)
        {
            foreach (var peer in _peers)
            {
                if (peer.PeerChoking || peer.Outstanding.Count == 0) continue;
                long oldestReq = peer.Outstanding.Min(b => b.RequestedAtMs);
                long last = Math.Max(Math.Max(peer.LastBlockMs, peer.UnchokedAtMs), oldestReq);
                if (now - last < (long)_opt.SnubTime.TotalMilliseconds) continue;
                AceLog.Debug(Tag, peer + " snubbed: " + peer.Outstanding.Count + " unanswered requests");
                foreach (var b in peer.Outstanding) peer.Send(Wire.Cancel(b.Piece, b.Begin, b.Length));
                ReleaseOutstandingLocked(peer);
                peer.Snubbed = true;
                peer.SnubUntilMs = now + 30000;
                if (peer.BadPerformance < 1) peer.BadPerformance = 1;
            }
        }
    }
}
