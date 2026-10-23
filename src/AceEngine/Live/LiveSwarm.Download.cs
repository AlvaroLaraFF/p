using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Live
{
    internal sealed partial class LiveSwarm
    {
        // ---- signature ----
        private long _sigOk, _sigFail;
        private bool _acceptUnverified;
        private readonly HashSet<LivePeer> _sigFailPeers = new HashSet<LivePeer>();
        private long _bytesOut;
        private long _lastRechokeMs;

        private int ExpectedChunkLength(int chunk)
        {
            return chunk < _chunks - 1 ? _cl : _pl - (_chunks - 1) * _cl;
        }

        // ---------- PIECE received ----------

        private void HandlePieceLocked(LivePeer peer, byte[] msg)
        {
            bool wide = UseWideChunk(peer);
            ulong uidx = 0; double ts = 0; int chunk = 0, off = 0;
            bool ok = false;
            for (int attempt = 0; attempt < 2 && !ok; attempt++)
            {
                bool w = attempt == 0 ? wide : !wide;
                if (LiveWire.TryParsePiece(msg, w, out uidx, out ts, out chunk, out off) && chunk < _chunks && msg.Length - off == ExpectedChunkLength(chunk))
                {
                    ok = true;
                    if (attempt == 1) AceLog.Debug(Tag, peer + " PIECE with the opposite chunk width to the expected (" + (w ? "H" : "B") + ")");
                }
            }
            if (!ok) throw new LiveProtocolViolation("store_piece: bad chunk length (" + msg.Length + " B)");
            long index = (long)uidx;
            int dataLen = msg.Length - off;
            long now = LivePeer.NowMs();
            peer.Down.Add(dataLen);
            _downMeter.Add(dataLen);
            peer.LastBlockMs = now;
            peer.Snubbed = false;
            peer.OutstandingChunks = Math.Max(0, peer.OutstandingChunks - 1);

            LiveActive a;
            if (!_active.TryGetValue(index, out a) || a.Got[chunk])
            {
                _discardedBytes += dataLen;
                return;
            }
            Buffer.BlockCopy(msg, off, a.Buf, chunk * _cl, dataLen);
            a.Got[chunk] = true;
            a.GotCount++;
            a.LastProgressMs = now;
            a.Timestamp = ts;
            if (!a.Sources.Contains(peer)) a.Sources.Add(peer);
            peer.Trace.Bytes += dataLen;
            if (a.GotCount == _chunks) CompletePieceLocked(a, now);
        }

        private void CompletePieceLocked(LiveActive a, long now)
        {
            _active.Remove(a.Index);
            ReleaseActiveOwnersLocked(a);
            if (!VerifyPieceLocked(a))
            {
                foreach (var s in a.Sources)
                {
                    s.BadPieces++;
                    _sigFailPeers.Add(s);
                    if (s.BadPieces >= 3) BanLocked(s, "3 pieces with a bad signature");
                }
                AceLog.Warn(Tag, "piece " + a.Index + ": the RSA signature does not verify (verify: failed sig: index=" + a.Index + ") from " + string.Join(",", a.Sources.Select(x => x.ToString()))
                    + "; discarded and requested again");
                var fresh = NewActiveLocked(a.Index, now);
                foreach (var t in a.Tried) fresh.Tried.Add(t);
                foreach (var s in a.Sources) fresh.Tried.Add(s);
                _active[a.Index] = fresh;
                return;
            }
            var piece = new LivePiece { Index = a.Index, Data = a.Buf, Timestamp = a.Timestamp };
            foreach (var s in a.Sources)
            {
                s.Trace.Pieces++;
                if (s.Trace.PieceAbs < 0) s.Trace.PieceAbs = now;
                s.Trace.Reach(LiveStage.Served);
            }
            _store[a.Index] = piece;
            _storeBytes += _pl;
            AnnounceHaveLocked(piece);
            Changed.Pulse();
        }

        /// <summary>Verifies content || signature. Tolerant: see SignatureMode.</summary>
        private bool VerifyPieceLocked(LiveActive a)
        {
            if (!_signed || _lo.Signature == SignatureMode.Off) return true;
            bool good = _rsa.VerifySha1(a.Buf, 0, _cbs, a.Buf, _cbs);
            if (good) { _sigOk++; return true; }
            _sigFail++;
            if (_lo.Signature == SignatureMode.Strict) return false;
            if (_acceptUnverified) return true;
            if (_sigOk == 0 && _sigFail >= _lo.TolerantProbePieces && _sigFailPeers.Count + a.Sources.Count >= 2)
            {
                _acceptUnverified = true;
                AceLog.Warn(Tag, "tolerant mode: none of the first " + _sigFail + " pieces verifies its RSA signature; a format problem is assumed and pieces are accepted UNVERIFIED");
                return true;
            }
            return false;
        }

        // Piece buffer pool: with pieces of 0.5-1 MiB each one would go to the large object heap and the TV would suffer with the GC
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();

        private LiveActive NewActiveLocked(long index, long now)
        {
            byte[] buf = _pool.Count > 0 ? _pool.Pop() : new byte[_pl];
            return new LiveActive { Index = index, Buf = buf, Got = new bool[_chunks], CreatedMs = now, LastProgressMs = now };
        }

        // ---------- Scheduling ----------

        private int MissingChunks(LiveActive a) { return _chunks - a.GotCount; }

        private double ExpectedRate(LivePeer p)
        {
            return p.Down.Total > 0 ? Math.Max(p.Down.Rate, 8 * 1024) : 64 * 1024;
        }

        private LivePeer ChoosePeerLocked(long piece, LiveActive a, LivePeer exclude, double minRate = 0)
        {
            LivePeer best = null;
            double bestScore = double.MaxValue;
            int need = a == null ? _chunks : MissingChunks(a);
            foreach (var p in _peers)
            {
                if (p == exclude || p.IsClosed || !p.RemoteExtended || p.PeerChoking || p.Snubbed) continue;
                if (!p.Have.Has(piece)) continue;
                if (p.Outstanding.Count >= _lo.MaxPiecesPerPeer) continue;
                if (p.OutstandingChunks + need > Math.Max(_lo.MaxChunkBacklog, need)) continue;
                if (minRate > 0 && ExpectedRate(p) < minRate) continue;
                // A peer with no data yet is worth 64 KB/s; with HedgeSlowPieces one that already delivered and is slow keeps its real rate (minimum 8 KB/s) and stops receiving pieces before the unknown ones
                double rate = _lo.HedgeSlowPieces && p.Down.Total > 0 ? Math.Max(p.Down.Rate, 8 * 1024) : Math.Max(p.Down.Rate, 64 * 1024);
                double score = ((double)(p.Outstanding.Count + 1) * _pl) / rate;
                if (a != null && a.Tried.Contains(p)) score += 10;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private void AssignLocked(LiveActive a, LivePeer peer, bool urgent, long now, bool asSecond = false)
        {
            if (asSecond) a.Second = peer; else { a.Owner = peer; a.AssignedMs = now; a.Attempts++; }
            a.Tried.Add(peer);
            if (!peer.Outstanding.Contains(a.Index)) peer.Outstanding.Add(a.Index);
            bool wide = UseWideChunk(peer);
            byte reqId = urgent ? LiveId.HpRequest : LiveId.Request;
            int sent = 0;
            for (int c = 0; c < _chunks; c++)
            {
                if (a.Got[c]) continue;
                peer.Send(LiveWire.Request(reqId, (ulong)a.Index, c, wide));
                sent++;
            }
            peer.OutstandingChunks += sent;
        }

        private void ScheduleLocked(long now)
        {
            if (!_started2) return;
            long end = _outPiece + _ahead;
            if (_livePos >= 0) end = Math.Min(end, _livePos + 1);
            int maxActive = Math.Max(4, _lo.MaxPiecesPerPeer * Math.Min(_peers.Count, 8));
            long firstMissing = -1;
            double hedgeRate;
            for (long p = _outPiece; p < end; p++)
            {
                if (_store.ContainsKey(p)) continue;
                if (firstMissing < 0) firstMissing = p;
                bool urgent = p - firstMissing < _lo.UrgentPieces;
                LiveActive a;
                if (!_active.TryGetValue(p, out a))
                {
                    if (_active.Count >= maxActive) continue;
                    var peer = ChoosePeerLocked(p, null, null);
                    if (peer == null) continue;
                    a = NewActiveLocked(p, now);
                    _active[p] = a;
                    AssignLocked(a, peer, urgent && p == firstMissing, now);
                }
                else if (a.Owner == null)
                {
                    var peer = ChoosePeerLocked(p, a, null);
                    if (peer != null) AssignLocked(a, peer, urgent && p == firstMissing, now);
                }
                else if (urgent && a.Second == null && now - a.AssignedMs > _lo.UrgentDuplicateAfterSeconds * 1000 && now - a.LastProgressMs > _lo.UrgentDuplicateAfterSeconds * 500)
                {
                    // The next one to be played is slow: it is also requested from another peer (the first to arrive wins)
                    var peer = ChoosePeerLocked(p, a, a.Owner);
                    if (peer != null) AssignLocked(a, peer, true, now, true);
                }
                else if (a.Second == null && ShouldHedgeLocked(a, p, urgent, now, out hedgeRate))
                {
                    // Only from a peer expected to be at least twice as fast as the owner: requesting it from another slow one only lengthens its queue
                    var peer = ChoosePeerLocked(p, a, a.Owner, Math.Max(hedgeRate * 2, 24 * 1024));
                    if (peer != null)
                    {
                        AceLog.Debug(Tag, "piece " + p + ": slow with " + a.Owner + " (" + a.GotCount + "/" + _chunks + " chunks in " + ((now - a.AssignedMs) / 1000.0).ToString("0.0") + " s); also requested from " + peer);
                        AssignLocked(a, peer, urgent, now, true);
                    }
                }
            }
        }

        /// <summary>Is the piece slow for what is needed? (see LiveOptions.HedgeSlowPieces). While the prebuffer fills all its pieces count; afterwards, only the urgent ones.</summary>
        private bool ShouldHedgeLocked(LiveActive a, long piece, bool urgent, long now, out double ownerRate)
        {
            ownerRate = 0;
            if (!_lo.HedgeSlowPieces || a.Owner == null) return false;
            bool needed = _playable ? urgent : piece < _prebufStart + _prebufCount;
            if (!needed) return false;
            double age = (now - a.AssignedMs) / 1000.0;
            if (age < _lo.HedgeAfterSeconds) return false;
            double rate = a.GotCount * (double)_cl / Math.Max(0.001, age);            // what the owner of this piece has delivered since it was requested
            double missing = (_chunks - a.GotCount) * (double)_cl;
            ownerRate = rate;
            return missing / Math.Max(rate, 8 * 1024) > _lo.HedgeEtaSeconds;
        }

        private void CheckTimeoutsLocked(long now)
        {
            List<LiveActive> expired = null;
            foreach (var a in _active.Values)
            {
                if (a.Owner == null) continue;
                double rate = Math.Max(a.Owner.Down.Rate, 50 * 1024);
                double deadline = _lo.RequestDeadlineSeconds * 1000 + (double)MissingChunks(a) * _cl / rate * 2000;
                if (now - Math.Max(a.LastProgressMs, a.AssignedMs) > deadline) (expired ?? (expired = new List<LiveActive>())).Add(a);
            }
            if (expired != null)
            {
                foreach (var a in expired)
                {
                    var o = a.Owner;
                    AceLog.Debug(Tag, "piece " + a.Index + ": no response from " + o + " in " + ((now - Math.Max(a.LastProgressMs, a.AssignedMs)) / 1000.0).ToString("0.0") + " s; reassigning");
                    CancelRequestsLocked(a, o);
                    o.Strikes++;
                    if (o.Strikes >= 3 && !o.Snubbed) { o.Snubbed = true; o.Strikes = 0; AceLog.Debug(Tag, o + " marked as snubbed"); }
                    a.Owner = a.Second; a.Second = null;
                    if (a.Owner != null) { a.AssignedMs = now; a.LastProgressMs = now; }
                }
            }
            // A snubbed peer returns to normal after a while
            foreach (var p in _peers)
                if (p.Snubbed && now - p.LastBlockMs > _lo.SnubSeconds * 1000 && p.Outstanding.Count == 0) { p.Snubbed = false; }
        }

        private void CancelRequestsLocked(LiveActive a, LivePeer peer)
        {
            if (peer == null) return;
            bool wide = UseWideChunk(peer);
            int cancelled = 0;
            for (int c = 0; c < _chunks; c++)
            {
                if (a.Got[c]) continue;
                peer.Send(LiveWire.Request(LiveId.Cancel, (ulong)a.Index, c, wide));
                cancelled++;
            }
            peer.Outstanding.Remove(a.Index);
            peer.OutstandingChunks = Math.Max(0, peer.OutstandingChunks - cancelled);
        }

        private void ReleaseActiveOwnersLocked(LiveActive a)
        {
            foreach (var p in new[] { a.Owner, a.Second })
            {
                if (p == null) continue;
                p.Outstanding.Remove(a.Index);
            }
            // The chunks that were requested from the other peer and are no longer needed are cancelled
            if (a.Owner != null && a.Second != null) { }
        }

        /// <summary>A peer has closed or choked us: its requests become free again.</summary>
        private void ReleasePeerRequestsLocked(LivePeer peer)
        {
            foreach (var idx in peer.Outstanding.ToList())
            {
                LiveActive a;
                if (!_active.TryGetValue(idx, out a)) continue;
                if (a.Owner == peer) { a.Owner = a.Second; a.Second = null; a.AssignedMs = LivePeer.NowMs(); }
                else if (a.Second == peer) a.Second = null;
            }
            peer.Outstanding.Clear();
            peer.OutstandingChunks = 0;
        }

        // ---------- Piece eviction ----------

        private void EvictLocked()
        {
            if (_store.Count == 0) return;
            long lowKeep = _outPiece - _keepBehind;
            List<long> remove = null;
            foreach (var k in _store.Keys)
                if (k < lowKeep) (remove ?? (remove = new List<long>())).Add(k);
            if (_store.Count - (remove == null ? 0 : remove.Count) > _maxPieces)
            {
                // over the limit: the oldest first, up to the playback position
                var older = _store.Keys.Where(k => k < _outPiece && (remove == null || !remove.Contains(k))).OrderBy(k => k).ToList();
                int excess = _store.Count - (remove == null ? 0 : remove.Count) - _maxPieces;
                foreach (var k in older.Take(excess)) (remove ?? (remove = new List<long>())).Add(k);
            }
            if (remove == null) return;
            foreach (var k in remove)
            {
                LivePiece p;
                if (!_store.TryGetValue(k, out p)) continue;
                _store.Remove(k);
                _storeBytes -= _pl;
                if (_pool.Count < 6) _pool.Push(p.Data);          // nobody uses it anymore: all reads copy inside the lock
                if (p.Announced && _lo.AnnounceHave)
                    foreach (var peer in _peers) if (peer.RemoteExtended) peer.Send(LiveWire.Lost((ulong)k));
            }
            foreach (var peer in _peers) peer.Have.PruneBelow(lowKeep - 100);
        }

        // ---------- Upload ----------

        private void AnnounceHaveLocked(LivePiece piece)
        {
            if (!_lo.AnnounceHave || _lo.UploadSlots <= 0) return;
            foreach (var peer in _peers)
            {
                if (!peer.RemoteExtended) continue;
                if (peer.RemotePos >= 0 && peer.RemoteWindow != 0 && (piece.Index < peer.RemotePos || piece.Index >= peer.RemotePos + peer.RemoteWindow)) continue;   // skip before pos / after window
                peer.Send(LiveWire.Have((ulong)piece.Index, SendTimestampsTo(peer) ? (double?)piece.Timestamp : null));
                piece.Announced = true;
            }
        }

        private void HandleRequestLocked(LivePeer peer, byte[] msg)
        {
            ulong idx; int chunk;
            if (!LiveWire.TryParseRequest(msg, out idx, out chunk)) throw new LiveProtocolViolation("REQUEST with wrong length");
            if (_lo.UploadSlots <= 0 || peer.AmChoking) return;
            LivePiece p;
            if (!_store.TryGetValue((long)idx, out p) || chunk >= _chunks) return;
            int off = chunk * _cl;
            int len = ExpectedChunkLength(chunk);
            var limiter = _opt.UploadLimiter;
            if (limiter != null && !limiter.TryTake(len)) return;           // global upload limit: the request is dropped
            peer.Send(LiveWire.Piece(idx, p.Timestamp, chunk, UseWideChunk(peer), p.Data, off, len));
            peer.Up.Add(len);
            _upMeter.Add(len);
            _bytesOut += len;
            var r = _upMeter.Rate;
            if (r > _topUpRate) _topUpRate = (long)r;
        }

        private void RechokeLocked(long now)
        {
            if (now - _lastRechokeMs < _lo.RechokeIntervalSeconds * 1000) return;
            _lastRechokeMs = now;
            if (_lo.UploadSlots <= 0) return;
            var interested = _peers.Where(p => p.RemoteExtended && p.PeerInterested).OrderByDescending(p => p.Down.Rate).ThenBy(p => p.CreatedMs).ToList();
            var unchoke = new HashSet<LivePeer>(interested.Take(_lo.UploadSlots));
            foreach (var p in _peers)
            {
                if (!p.RemoteExtended) continue;
                bool want = unchoke.Contains(p);
                if (want && p.AmChoking) { p.AmChoking = false; p.Send(LiveWire.Simple(LiveId.Unchoke)); }
                else if (!want && !p.AmChoking) { p.AmChoking = true; p.Send(LiveWire.Simple(LiveId.Choke)); }
            }
        }
    }
}
