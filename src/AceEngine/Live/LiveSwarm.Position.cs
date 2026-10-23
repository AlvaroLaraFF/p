using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Live
{
    internal enum LiveReadResult { Data, Wait, Stopped }

    /// <summary>Output state of a reader: piece/offset cursor and phase of the MPEG-TS stream.</summary>
    internal sealed class LiveReaderState
    {
        public long Piece;
        public int Offset;
        public bool Synced;
        public long Anchor = long.MinValue;       // absolute position (piece * content + offset) of a TS packet boundary
        public bool Passthrough;
        public int SyncFailures;
        public long Generation = -1;
        public long SkipSeq;
        public bool Closed;
        public long BytesDelivered;
        public bool EverRead;
        /// <summary>Cuts of the delivered stream (re-attach, skipped hole): after one, the next data starts again on an aligned packet. Used by the pump of the live with memory.</summary>
        public long Discontinuities;
    }

    internal sealed partial class LiveSwarm
    {
        private const int TsPacket = 188;

        // ---- position and startup ----
        private long _livePos = -1;
        private long _livePosFirst = -1, _livePosFirstMs, _livePosChangeMs, _playableMs;
        private int _livePosAdvances;
        private long _outPiece;
        private bool _started2;                    // the startup position was already chosen
        private bool _playable;                    // complete prebuffer
        private long _prebufStart;
        private long _generation;
        private long _startPosAtMs;
        private long _firstClaimMs;
        private long _regressSinceMs;
        private int _holesSkipped, _piecesSkipped, _rehooks;
        private long _starvedMs;
        private long _lastTickMs;
        private long _waitSinceMs;                 // since when the reader is waiting for data (0 = not waiting)
        private long _holeFirstSeenMs;
        private long _skipTo = -1;
        private long _skipSeq;
        private LiveReaderState _reader;
        private bool _hadReader;
        private int _prebufCount;

        // ---------- Main loop ----------

        private async Task TickLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    try { TickOnce(); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { AceLog.Warn(Tag, "tick: " + ex.GetType().Name + " " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void TickOnce()
        {
            long now = LivePeer.NowMs();
            lock (_sync)
            {
                if (_disposed) return;
                long dt = _lastTickMs == 0 ? 0 : Math.Min(1000, now - _lastTickMs);
                _lastTickMs = now;
                UpdateLivePosLocked(now);
                if (!_started2) TryStartLocked(now);
                else
                {
                    CheckPlayableLocked(now);
                    HolePolicyLocked(now, dt);
                    foreach (var p in _peers) SendChangePosLocked(p, now, false);
                }
                ScheduleLocked(now);
                CheckTimeoutsLocked(now);
                EvictLocked();
                RechokeLocked(now);
                PeriodicPeersLocked(now);
                ConnectMoreLocked();
                UpdatePhaseLocked(now);
            }
        }

        private void UpdatePhaseLocked(long now)
        {
            LivePhase ph;
            if (!_started2) ph = _peers.Count > 0 || _connecting > 0 ? LivePhase.Connecting : LivePhase.Connecting;
            else if (!_playable) ph = LivePhase.Buffering;
            else if (_waitSinceMs != 0 && now - _waitSinceMs > 1500) ph = LivePhase.Buffering;
            else ph = LivePhase.Playing;
            if (_phase == LivePhase.Resolving) ph = LivePhase.Connecting;
            if (!_started2) _message = _peers.Count == 0 ? "Buscando pares (" + _cands.Count + " candidatos)" : "Conectado a " + _peers.Count + " pares; esperando la posicion de directo";
            else if (!_playable) _message = "Cargando bufer " + BufferPercentLocked() + "%";
            else if (ph == LivePhase.Buffering) _message = "Sin datos del directo (esperando la pieza " + (_store.ContainsKey(_outPiece) ? _outPiece + 1 : _outPiece) + ")";
            else _message = null;
            SetPhaseLocked(ph);
        }

        // ---------- Live position ----------

        private void UpdateLivePosLocked(long now)
        {
            var claims = new List<long>();
            long maxap = -1;
            foreach (var p in _peers)
            {
                if (!p.RemoteExtended || p.Ext == null) continue;
                long c = p.HeadClaim;
                if (c < 0) continue;
                claims.Add(c);
                if (p.Ext.IsAuthorizedNode) maxap = Math.Max(maxap, c);
            }
            foreach (var kv in _broadcastClaims)
                if (now - kv.Value.Value < 180000) claims.Add(kv.Value.Key);
            if (claims.Count == 0) return;
            claims.Sort();
            claims.Reverse();
            if (_firstClaimMs == 0) _firstClaimMs = now;

            long vote = claims.Count >= _lo.VoteMinPeers ? claims[Math.Max(0, _lo.VoteMinPeers - 1)] : -1;
            long cand = Math.Max(vote, maxap);
            if (cand < 0 && now - _firstClaimMs >= _lo.SinglePeerLivePosSeconds * 1000) cand = claims[0];     // a single peer reports: it is accepted after a delay
            if (cand < 0) return;

            if (cand >= _livePos)
            {
                if (cand > _livePos)
                {
                    if (_livePos < 0) { _livePosFirst = cand; _livePosFirstMs = now; }
                    else _livePosAdvances++;
                    _livePosChangeMs = now;
                }
                _livePos = cand;
                _regressSinceMs = 0;
            }
            else if (_started2 && cand < _livePos - (long)(_lo.RestartDetectSeconds * _pps) - 1)
            {
                // The indexes go back in a sustained way: the sender has restarted
                if (_regressSinceMs == 0) _regressSinceMs = now;
                else if (now - _regressSinceMs > _lo.RestartConfirmSeconds * 1000)
                {
                    AceLog.Warn(Tag, "the live position goes back from " + _livePos + " to " + cand + ": sender restart; re-attaching");
                    _store.Clear(); _storeBytes = 0; _active.Clear();
                    foreach (var p in _peers) { p.Outstanding.Clear(); p.OutstandingChunks = 0; p.Have.Reset(); }
                    _livePos = cand;
                    _broadcastClaims.Clear();
                    RehookLocked(now, "sender restart");
                }
            }
        }

        /// <summary>Oldest piece offered by any peer (-1 if unknown).</summary>
        private long MinAvailableLocked()
        {
            long min = -1;
            foreach (var p in _peers)
            {
                long m = p.Have.Count > 0 ? p.Have.Min : (p.MyInfoMin >= 0 ? p.MyInfoMin : -1);
                if (m < 0) continue;
                if (min < 0 || m < min) min = m;
            }
            return min;
        }

        // ---------- Startup ----------

        private void TryStartLocked(long now)
        {
            if (_livePos < 0) return;
            long start = Math.Max(0, _livePos - _prebufPieces);
            // It cannot start before the oldest piece that any peer has (freshly started sender or short windows)
            long minAvail = MinAvailableLocked();
            if (minAvail > start) start = minAvail;
            _prebufCount = (int)Math.Max(1, Math.Min(_prebufPieces, _livePos - start + 1));
            _outPiece = start;
            _prebufStart = start;
            _started2 = true;
            _playable = false;
            _generation++;
            _startPosAtMs = now;
            AceLog.Info(Tag, "live position " + _livePos + "; starting at " + start + " (" + _prebufCount + " prebuffer pieces, " + _lo.BufferSeconds + " s behind), " + _peers.Count + " peers");
            foreach (var p in _peers) SendChangePosLocked(p, now, true);
            SetPhaseLocked(LivePhase.Buffering);
            Changed.Pulse();
        }

        private void CheckPlayableLocked(long now)
        {
            if (_playable) return;
            bool all = true;
            int run = 0;
            bool contiguous = true;
            for (long p = _prebufStart; p < _prebufStart + _prebufCount; p++)
            {
                if (_store.ContainsKey(p)) { if (contiguous) run++; }
                else { all = false; contiguous = false; }
            }
            long waited = now - _startPosAtMs;
            if (all || (waited > _lo.PrebufferTimeoutSeconds * 1000 && run >= 2))
            {
                _playable = true;
                if (_playableMs == 0) _playableMs = now;
                AceLog.Info(Tag, "prebuffer " + (all ? "complete" : "partial (" + run + " pieces)") + " in " + (waited / 1000.0).ToString("0.0") + " s: playable");
                SetPhaseLocked(LivePhase.Playing);
                Changed.Pulse();
            }
            else if (waited > _lo.PrebufferTimeoutSeconds * 3000)
            {
                RehookLocked(now, "the prebuffer does not complete");
            }
        }

        /// <summary>Re-attaches to live: position = live - prebuffer (live_seek(-1)).</summary>
        private void RehookLocked(long now, string reason)
        {
            if (_livePos < 0) return;
            long start = Math.Max(0, _livePos - _prebufPieces);
            long minAvail = MinAvailableLocked();
            if (minAvail > start) start = minAvail;
            _prebufCount = (int)Math.Max(1, Math.Min(_prebufPieces, _livePos - start + 1));
            AceLog.Info(Tag, "rehook (" + reason + "): " + _outPiece + " -> " + start + " (live " + _livePos + ")");
            _rehooks++;
            _outPiece = start;
            _prebufStart = start;
            _playable = false;
            _generation++;
            _startPosAtMs = now;
            _holeFirstSeenMs = 0;
            _waitSinceMs = 0;
            _started2 = true;
            foreach (var k in _active.Keys.Where(k => k < start).ToList())
            {
                var a = _active[k];
                if (a.Owner != null) CancelRequestsLocked(a, a.Owner);
                _active.Remove(k);
            }
            foreach (var p in _peers) { SendChangePosLocked(p, now, true); }
            Changed.Pulse();
        }

        // ---------- Holes and freezing ----------

        private void HolePolicyLocked(long now, long dt)
        {
            if (!_playable) return;
            var rs = _reader;
            if (rs == null || rs.Closed || _waitSinceMs == 0) { _holeFirstSeenMs = 0; return; }
            _starvedMs += dt;
            // Piece that blocks the output: the cursor's, or the next one if the cursor is waiting so as not to cut a TS packet in half
            long bp = _store.ContainsKey(rs.Piece) ? rs.Piece + 1 : rs.Piece;
            if (_store.ContainsKey(bp)) { _holeFirstSeenMs = 0; return; }
            if (_holeFirstSeenMs == 0 || _holeBlocking != bp) { _holeFirstSeenMs = now; _holeBlocking = bp; }
            long held = now - _holeFirstSeenMs;

            // Next piece that we do have
            long q = -1;
            for (long p = bp + 1; p <= bp + _ahead + 2; p++) if (_store.ContainsKey(p)) { q = p; break; }
            if (q >= 0)
            {
                int gap = (int)(q - bp);
                int after = 0;
                for (long p = q; _store.ContainsKey(p); p++) after++;
                // A short hole with many pieces behind it is skipped right away (fill_from_pieces:drop: got piece after hole), but only if no peer offers the piece:
                // if someone has it, it is still on its way (slow request or reassignment) and we wait until HoleSkipSeconds
                bool nobodyHas = true;
                for (long hp = bp; hp < q && nobodyHas; hp++)
                    foreach (var pp in _peers) if (pp.Have.Has(hp)) { nobodyHas = false; break; }
                bool dropNow = nobodyHas && gap <= _lo.DropMaxHole && after >= _lo.DropMinPiecesAfterHole && held > 300;
                if (dropNow || held > _lo.HoleSkipSeconds * 1000)
                {
                    SkipToLocked(q, gap, dropNow ? "hole of " + gap + " piece(s) with " + after + " behind" : "hole of " + gap + " piece(s) after " + (held / 1000.0).ToString("0.0") + " s");
                    return;
                }
            }
            long lag = _livePos >= 0 ? _livePos - bp : 0;
            if (_livePos >= 0 && lag > (long)(_lo.MaxLagSeconds * _pps) && held > 2000)
            {
                RehookLocked(now, "lag of " + (lag / _pps).ToString("0") + " s behind live");
                return;
            }
            if (held > _lo.FreezeJumpSeconds * 1000)
                RehookLocked(now, "frozen " + (held / 1000) + " s (check_freeze: jump to live)");
        }
        private long _holeBlocking = -1;

        private void SkipToLocked(long q, int gap, string why)
        {
            AceLog.Info(Tag, "skipping to piece " + q + " (" + why + ")");
            _skipTo = q;
            _skipSeq++;
            _holesSkipped++;
            _piecesSkipped += gap;
            _outPiece = q;
            _holeFirstSeenMs = 0;
            Changed.Pulse();
        }

        // ---------- Periodic per-peer tasks ----------

        private void PeriodicPeersLocked(long now)
        {
            foreach (var p in _peers.ToList())
            {
                if (!p.RemoteExtended) continue;
                if (_lo.SendMyInfo && now - p.LastMyInfoSentMs > _lo.MyInfoIntervalSeconds * 1000) SendMyInfoLocked(p, now);
                if (now - p.LastKeepAliveSentMs > _opt.KeepAliveInterval.TotalMilliseconds) { p.LastKeepAliveSentMs = now; p.Send(Wire.KeepAlive); }
                long idleLimit = (long)Math.Min(_opt.PeerIdleTimeout.TotalMilliseconds, 90000);
                if (now - p.LastReceiveMs > idleLimit) { ThreadPool.QueueUserWorkItem(_ => p.Close("no activity")); }
                // A peer that has had nothing useful for us for a long time is surplus if there are many
                if (now - p.CreatedMs > 60000 && p.Have.Count == 0 && _peers.Count > 8) ThreadPool.QueueUserWorkItem(_ => p.Close("no pieces"));
            }
        }

        // ---------- Output ----------

        /// <summary>Opens a reader. Only one is active: the new one replaces the previous one (like the original, which serializes the clients) and attaches to live.</summary>
        internal LiveReaderState OpenReader()
        {
            lock (_sync)
            {
                var old = _reader;
                if (old != null) old.Closed = true;
                var rs = new LiveReaderState();
                _reader = rs;
                long now = LivePeer.NowMs();
                if (_started2)
                {
                    bool far = _livePos >= 0 && _livePos - _outPiece > (long)(_prebufPieces * 1.5) + 2;
                    if (_hadReader || far) RehookLocked(now, _hadReader ? "new reader" : "the prebuffer is already old");
                }
                _hadReader = true;
                _waitSinceMs = 0;
                _holeFirstSeenMs = 0;
                Changed.Pulse();
                return rs;
            }
        }

        internal void CloseReader(LiveReaderState rs)
        {
            lock (_sync)
            {
                rs.Closed = true;
                if (_reader == rs) { _reader = null; _waitSinceMs = 0; }
            }
            Changed.Pulse();
        }

        private long Abs(long piece, long offset) { return piece * _cbs + offset; }

        private static bool IsSync3(byte[] d, int limit, int p)
        {
            return p >= 0 && p + 2 * TsPacket < limit && d[p] == 0x47 && d[p + TsPacket] == 0x47 && d[p + 2 * TsPacket] == 0x47;
        }

        /// <summary>Looks for the first aligned TS packet (0x47 at three consecutive positions) in the piece content.</summary>
        private int FindStartLocked(LiveReaderState rs, LivePiece lp)
        {
            var d = lp.Data;
            if (rs.Anchor != long.MinValue)
            {
                // The phase of the packets is deduced from the absolute position: after a hole there is no need to search
                int expected = (int)(((rs.Anchor - Abs(rs.Piece, 0)) % TsPacket + TsPacket) % TsPacket);
                if (IsSync3(d, _cbs, expected)) return expected;
            }
            for (int p = rs.Offset; p + 2 * TsPacket < _cbs; p++)
                if (d[p] == 0x47 && d[p + TsPacket] == 0x47 && d[p + 2 * TsPacket] == 0x47)
                {
                    rs.Anchor = Abs(rs.Piece, p);
                    return p;
                }
            return -1;
        }

        internal LiveReadResult TryRead(LiveReaderState rs, byte[] buffer, int offset, int count, out int n)
        {
            n = 0;
            lock (_sync)
            {
                if (_disposed) return LiveReadResult.Stopped;
                if (rs.Closed || _reader != rs) return LiveReadResult.Stopped;
                long now = LivePeer.NowMs();
                if (!_started2 || !_playable) { MarkWaitLocked(now); return LiveReadResult.Wait; }
                if (rs.Generation != _generation)
                {
                    rs.Generation = _generation;
                    rs.Piece = _outPiece;
                    rs.Offset = 0;
                    rs.Synced = false;
                    rs.Anchor = long.MinValue;              // discontinuity: the sync is searched again
                    rs.SkipSeq = _skipSeq;
                    rs.Discontinuities++;
                }
                if (rs.SkipSeq != _skipSeq)
                {
                    rs.SkipSeq = _skipSeq;
                    if (_skipTo > rs.Piece) { rs.Piece = _skipTo; rs.Offset = 0; rs.Synced = false; rs.Discontinuities++; }
                }
                for (int guard = 0; guard < 16; guard++)
                {
                    LivePiece lp;
                    if (!_store.TryGetValue(rs.Piece, out lp)) { MarkWaitLocked(now); return LiveReadResult.Wait; }
                    if (!rs.Synced && !rs.Passthrough)
                    {
                        int s = _opt.Http.AlignLiveToTs ? FindStartLocked(rs, lp) : 0;
                        if (s < 0)
                        {
                            rs.SyncFailures++;
                            if (rs.SyncFailures >= 6)
                            {
                                AceLog.Warn(Tag, "no MPEG-TS sync found in " + rs.SyncFailures + " consecutive pieces: the content does not look like TS; delivering it as is");
                                rs.Passthrough = true;
                            }
                            else
                            {
                                AceLog.Debug(Tag, "piece " + rs.Piece + " without TS sync; discarded");
                                rs.Piece++; rs.Offset = 0; _outPiece = rs.Piece;
                                continue;
                            }
                        }
                        else
                        {
                            rs.Offset = s;
                            rs.Synced = true;
                            rs.SyncFailures = 0;
                            if (s != 0) AceLog.Debug(Tag, "TS sync in piece " + rs.Piece + " + " + s);
                        }
                    }
                    int limit;
                    if (rs.Passthrough || _store.ContainsKey(rs.Piece + 1)) limit = _cbs;
                    else
                    {
                        // A TS packet is not delivered half-way: the tail is held until the next piece is available
                        int tail = (int)(((Abs(rs.Piece, _cbs) - rs.Anchor) % TsPacket + TsPacket) % TsPacket);
                        limit = _cbs - tail;
                    }
                    if (rs.Offset >= limit)
                    {
                        if (limit == _cbs) { rs.Piece++; rs.Offset = 0; _outPiece = rs.Piece; continue; }
                        MarkWaitLocked(now);
                        return LiveReadResult.Wait;
                    }
                    int take = Math.Min(count, limit - rs.Offset);
                    Buffer.BlockCopy(lp.Data, rs.Offset, buffer, offset, take);
                    rs.Offset += take;
                    if (rs.Offset >= _cbs) { rs.Piece++; rs.Offset = 0; }
                    _outPiece = rs.Piece;
                    rs.BytesDelivered += take;
                    rs.EverRead = true;
                    _waitSinceMs = 0;
                    n = take;
                    return LiveReadResult.Data;
                }
                MarkWaitLocked(now);
                return LiveReadResult.Wait;
            }
        }

        private void MarkWaitLocked(long now)
        {
            if (_waitSinceMs == 0) _waitSinceMs = now;
        }
    }
}
