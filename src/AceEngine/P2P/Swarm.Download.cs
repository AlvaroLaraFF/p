using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm
    {
        // ---------- IPickerContext (called with _sync held) ----------

        private PeerConnection _pickPeer;       // peer the picker is choosing for

        bool IPickerContext.Have(int piece) { return _have[piece]; }

        bool IPickerContext.PeerHas(int piece) { return _pickPeer.Have != null && _pickPeer.Have[piece]; }

        int IPickerContext.Availability(int piece) { return _avail[piece]; }

        bool IPickerContext.Requestable(int piece)
        {
            ActivePiece ap;
            if (_active.TryGetValue(piece, out ap)) return !ap.Verifying && ap.HasFree;
            return RoomForNewPieceLocked(piece);
        }

        bool IPickerContext.FastPeerHas(int piece)
        {
            foreach (var p in _peers)
                if (p.BadPerformance < 1 && p.Have != null && p.Have[piece] && !p.PeerChoking) return true;
            return false;
        }

        // ---------- Memory ----------

        private long UsedBytesLocked { get { return _storeBytes + _activeBytes; } }

        private bool RoomForNewPieceLocked(int piece)
        {
            int len = _meta.GetPieceLength(piece);
            if (UsedBytesLocked + len <= _opt.MaxBufferBytes) return true;
            EvictLocked(UsedBytesLocked + len - _opt.MaxBufferBytes);
            return UsedBytesLocked + len <= _opt.MaxBufferBytes;
        }

        /// <summary>
        /// Frees at least 'needed' bytes of verified pieces OUTSIDE the window [position - KeepBehind, WindowEnd): it never
        /// evicts something the picker would request again (that would download the same thing over and over). Order: what lies
        /// behind beyond KeepBehind (farthest first), what lies beyond the end of the window (farthest
        /// first) and, as a last resort, the KeepBehind zone. Pinned pieces (end of the file) are not touched.
        /// </summary>
        private void EvictLocked(long needed)
        {
            if (needed <= 0) return;
            int keepBehind = KeepBehindPieces;
            int protectFrom = _win.PlaybackPiece - keepBehind;
            var cand = new List<KeyValuePair<long, int>>();
            foreach (var idx in _store.Keys)
            {
                if (_pinned.Contains(idx)) continue;
                if (idx >= _win.PlaybackPiece && idx < _win.WindowEnd) continue;       // window: untouchable
                long score;
                if (idx < protectFrom) score = 3000000L + (protectFrom - idx);          // behind, far
                else if (idx >= _win.WindowEnd) score = 2000000L + (idx - _win.WindowEnd);   // ahead, far
                else score = 1000000L + (_win.PlaybackPiece - idx);                    // KeepBehind zone
                cand.Add(new KeyValuePair<long, int>(score, idx));
            }
            cand.Sort((x, y) => y.Key.CompareTo(x.Key));
            foreach (var kv in cand)
            {
                if (needed <= 0) break;
                needed -= RemoveFromStoreLocked(kv.Value);
            }
            if (needed <= 0) return;
            // Still short of space: partial pieces without live requests are released (those farthest from the position first)
            var idle = _active.Values.Where(a => !a.Verifying && !InHighRange(a.Index) && a.ReqCount.All(r => r == 0))
                .OrderByDescending(a => Math.Abs(a.Index - _win.PlaybackPiece)).ToList();
            foreach (var a in idle)
            {
                if (needed <= 0) break;
                DropActiveLocked(a);
                needed -= a.Length;
            }
        }

        /// <summary>Partial pieces left behind the playback and without live requests: nobody will want them.</summary>
        private void DropStalePartialsLocked()
        {
            List<ActivePiece> stale = null;
            foreach (var a in _active.Values)
            {
                if (a.Verifying || a.Index >= _win.PlaybackPiece) continue;
                if (a.ReqCount.Any(r => r != 0)) continue;
                if (stale == null) stale = new List<ActivePiece>();
                stale.Add(a);
            }
            if (stale != null) foreach (var a in stale) DropActiveLocked(a);
        }

        private int RemoveFromStoreLocked(int idx)
        {
            byte[] data;
            if (!_store.TryGetValue(idx, out data)) return 0;
            _store.Remove(idx);
            _storeBytes -= data.Length;
            _have.Set(idx, false);
            return data.Length;
        }

        /// <summary>After moving the position: removes what no longer fits in the window (releasing those already served).</summary>
        private void TrimBehindLocked()
        {
            int limit = _win.PlaybackPiece - KeepBehindPieces;
            if (limit <= _firstPiece) return;
            List<int> victims = null;
            foreach (var idx in _store.Keys)
                if (idx < limit && !_pinned.Contains(idx)) { if (victims == null) victims = new List<int>(); victims.Add(idx); }
            if (victims != null) foreach (var v in victims) RemoveFromStoreLocked(v);
        }

        // ---------- Requests ----------

        private int BacklogFor(PeerConnection peer)
        {
            // backlog = min(2 + int(4*rate/chunksize), ...) and cap 50, with an initial minimum to start fast
            double rate = peer.Down.Rate;
            int b = 2 + (int)(_opt.BacklogSeconds * rate / _opt.BlockSize);
            return Math.Max(_opt.InitialBacklog, Math.Min(_opt.MaxBacklog, b));
        }

        /// <summary>Peers from fastest to slowest (random tie-break): the fast ones choose first and keep the pieces at the start of the window.</summary>
        private List<PeerConnection> PeersByRateLocked()
        {
            var list = _peers.ToList();
            Rng.Shuffle(list);
            var rates = new Dictionary<PeerConnection, double>();
            foreach (var p in list) rates[p] = p.Down.Rate;
            list.Sort((a, b) => rates[b].CompareTo(rates[a]));
            return list;
        }

        private void FillRequestsLocked(PeerConnection peer)
        {
            if (_have == null || _disposed || !peer.Ready || peer.PeerChoking || !peer.AmInterested || peer.Have == null) return;
            long now = PeerConnection.NowMs();
            if (peer.Snubbed && now < peer.SnubUntilMs) return;
            peer.Backlog = BacklogFor(peer);
            _pickPeer = peer;
            try
            {
                while (peer.Outstanding.Count < peer.Backlog)
                {
                    var b = NextBlockLocked(peer, now);
                    if (b == null) break;
                    peer.Outstanding.Add(b);
                    peer.Send(Wire.Request(b.Piece, b.Begin, b.Length));
                }
            }
            finally { _pickPeer = null; }
        }

        private BlockRef NextBlockLocked(PeerConnection peer, long now)
        {
            PickTier tier;
            bool allowPerf = peer.BadPerformance < 1;
            if (allowPerf)
            {
                var dup = UrgentDuplicateLocked(peer, now);
                if (dup != null) return dup;
            }
            int piece = PiecePickerVod.Pick(_win, this, allowPerf, out tier);
            if (piece >= 0)
            {
                ActivePiece ap;
                if (!_active.TryGetValue(piece, out ap)) ap = StartPieceLocked(piece);
                int blk = ap.FirstFree();
                if (blk >= 0)
                {
                    ap.State[blk] = 1;
                    ap.ReqCount[blk] = 1;
                    ap.ReqAtMs[blk] = now;
                    ap.Free--;
                    int begin = blk * _opt.BlockSize;
                    return new BlockRef { Piece = piece, Begin = begin, Length = Math.Min(_opt.BlockSize, ap.Length - begin), RequestedAtMs = now };
                }
            }
            return EndgameBlockLocked(peer, now);
        }

        /// <summary>
        /// Duplication of urgent blocks: the first blocks from the playback position are also requested from a fast peer
        /// if they have been waiting more than UrgentDuplicateAfterMs. Without this a slow peer that received several consecutive pieces holds
        /// the start of the window for several seconds while the rest of the swarm downloads pieces that are not needed yet.
        /// </summary>
        private BlockRef UrgentDuplicateLocked(PeerConnection peer, long now)
        {
            int end = Math.Min(_win.HighEnd, _win.PlaybackPiece + _urgentLen);
            for (int p = _win.PlaybackPiece; p < end; p++)
            {
                ActivePiece ap;
                if (!_active.TryGetValue(p, out ap) || ap.Verifying || _have[p] || !peer.Have[p]) continue;
                for (int blk = 0; blk < ap.BlockCount; blk++)
                {
                    if (ap.State[blk] != 1 || ap.ReqCount[blk] >= 2) continue;
                    if (now - ap.ReqAtMs[blk] < _opt.UrgentDuplicateAfterMs) continue;
                    int begin = blk * _opt.BlockSize;
                    bool mine = false;
                    foreach (var o in peer.Outstanding) if (o.Piece == p && o.Begin == begin) { mine = true; break; }
                    if (mine) continue;
                    ap.ReqCount[blk]++;
                    return new BlockRef { Piece = p, Begin = begin, Length = Math.Min(_opt.BlockSize, ap.Length - begin), RequestedAtMs = now };
                }
            }
            return null;
        }

        /// <summary>
        /// Light endgame: if there is nothing new left to request, an idle peer duplicates blocks of the high zone
        /// that were requested more than 2 s ago from another peer. The first to arrive cancels the rest.
        /// </summary>
        private BlockRef EndgameBlockLocked(PeerConnection peer, long now)
        {
            for (int p = _win.PlaybackPiece; p < _win.HighEnd; p++)
            {
                ActivePiece ap;
                if (!_active.TryGetValue(p, out ap) || ap.Verifying || _have[p] || !peer.Have[p]) continue;
                for (int blk = 0; blk < ap.BlockCount; blk++)
                {
                    if (ap.State[blk] != 1 || ap.ReqCount[blk] >= 2) continue;
                    int begin = blk * _opt.BlockSize;
                    bool mine = false;
                    long oldest = now;
                    foreach (var o in peer.Outstanding) if (o.Piece == p && o.Begin == begin) { mine = true; break; }
                    if (mine) continue;
                    foreach (var q in _peers)
                        foreach (var o in q.Outstanding)
                            if (o.Piece == p && o.Begin == begin && o.RequestedAtMs < oldest) oldest = o.RequestedAtMs;
                    if (now - oldest < 2000) continue;
                    ap.ReqCount[blk]++;
                    return new BlockRef { Piece = p, Begin = begin, Length = Math.Min(_opt.BlockSize, ap.Length - begin), RequestedAtMs = now };
                }
            }
            return null;
        }

        private ActivePiece StartPieceLocked(int piece)
        {
            int len = _meta.GetPieceLength(piece);
            int blocks = (len + _opt.BlockSize - 1) / _opt.BlockSize;
            var ap = new ActivePiece
            {
                Index = piece, Length = len, BlockCount = blocks, Data = new byte[len],
                State = new byte[blocks], ReqCount = new byte[blocks], ReqAtMs = new long[blocks], Source = new PeerConnection[blocks], Free = blocks
            };
            _active[piece] = ap;
            _activeBytes += len;
            return ap;
        }

        private void DropActiveLocked(ActivePiece ap)
        {
            if (_active.Remove(ap.Index)) _activeBytes -= ap.Length;
        }

        /// <summary>Releases a peer's live requests (choke, close, cancellation).</summary>
        private void ReleaseOutstandingLocked(PeerConnection peer)
        {
            foreach (var b in peer.Outstanding) ReleaseBlockLocked(b);
            peer.Outstanding.Clear();
        }

        private void ReleaseBlockLocked(BlockRef b)
        {
            ActivePiece ap;
            if (!_active.TryGetValue(b.Piece, out ap) || ap.Verifying) return;
            int blk = b.Begin / _opt.BlockSize;
            if (ap.ReqCount[blk] > 0) ap.ReqCount[blk]--;
            if (ap.State[blk] == 1 && ap.ReqCount[blk] == 0) { ap.State[blk] = 0; ap.Free++; }
        }

        // ---------- Block arrival ----------

        private void HandlePieceLocked(PeerConnection peer, int index, int begin, byte[] msg, int dataOffset, int dataLen)
        {
            int found = -1;
            for (int i = 0; i < peer.Outstanding.Count; i++)
            {
                var o = peer.Outstanding[i];
                if (o.Piece == index && o.Begin == begin && o.Length == dataLen) { found = i; break; }
            }
            if (found < 0) { _discardedBytes += dataLen; return; }       // block not requested or cancelled
            peer.Outstanding.RemoveAt(found);
            peer.Down.Add(dataLen);
            _downMeter.Add(dataLen);
            peer.LastBlockMs = PeerConnection.NowMs();
            if (peer.BadPerformance > 0) peer.BadPerformance--;

            ActivePiece ap;
            if (!_active.TryGetValue(index, out ap) || ap.Verifying) { FillRequestsLocked(peer); return; }
            int blk = begin / _opt.BlockSize;
            if (ap.State[blk] != 2)
            {
                Buffer.BlockCopy(msg, dataOffset, ap.Data, begin, dataLen);
                if (ap.State[blk] == 0) ap.Free--;
                ap.State[blk] = 2;
                ap.Source[blk] = peer;
                ap.Received++;
                if (ap.ReqCount[blk] > 0) ap.ReqCount[blk]--;
                // endgame: cancel the same block on the other peers
                if (ap.ReqCount[blk] > 0)
                {
                    foreach (var q in _peers)
                    {
                        if (q == peer) continue;
                        for (int i = 0; i < q.Outstanding.Count; i++)
                        {
                            var o = q.Outstanding[i];
                            if (o.Piece == index && o.Begin == begin)
                            {
                                q.Outstanding.RemoveAt(i);
                                q.Send(Wire.Cancel(index, begin, o.Length));
                                if (ap.ReqCount[blk] > 0) ap.ReqCount[blk]--;
                                break;
                            }
                        }
                    }
                }
                if (ap.Received == ap.BlockCount)
                {
                    ap.Verifying = true;
                    var piece = ap;
                    Task.Run(() => VerifyAndCommit(piece));
                }
            }
            FillRequestsLocked(peer);
        }

        private void VerifyAndCommit(ActivePiece ap)
        {
            bool ok = false;
            try
            {
                byte[] expected;
                lock (_sync) { if (_disposed) return; expected = _meta.GetPieceHash(ap.Index); }
                byte[] hash;
                using (var sha = SHA1.Create()) hash = sha.ComputeHash(ap.Data, 0, ap.Length);
                ok = HashUtil.ConstantTimeEquals(hash, expected);
            }
            catch (Exception ex)
            {
                AceLog.Warn(Tag, "verifying piece " + ap.Index + ": " + ex.Message);
            }

            var closeList = new List<PeerConnection>();
            lock (_sync)
            {
                if (_disposed) return;
                DropActiveLocked(ap);
                if (ok)
                {
                    _store[ap.Index] = ap.Data;
                    _storeBytes += ap.Length;
                    _have.Set(ap.Index);
                    var have = Wire.Have(ap.Index);
                    foreach (var p in _peers) p.Send(have);
                    CheckPrebufferLocked();
                    TrimBehindLocked();
                    if (UsedBytesLocked > _opt.MaxBufferBytes) EvictLocked(UsedBytesLocked - _opt.MaxBufferBytes);
                }
                else
                {
                    _hashFails++;
                    AceLog.Warn(Tag, "piece " + ap.Index + ": SHA-1 mismatch, requesting it again");
                    // If all the blocks came from a single peer, it is penalized (BadDataGuard)
                    var sources = ap.Source.Where(s => s != null).Distinct().ToList();
                    if (sources.Count == 1)
                    {
                        var bad = sources[0];
                        bad.BadPieces++;
                        if (bad.BadPieces >= 3) { _banned.Add(bad.EndPoint.Address.ToString()); closeList.Add(bad); }
                    }
                }
                foreach (var p in PeersByRateLocked()) { if (!p.PeerChoking) FillRequestsLocked(p); }
            }
            foreach (var c in closeList) c.Close("bad data");
            Changed.Pulse();
        }

        private void CheckPrebufferLocked()
        {
            if (_prebufDone) return;
            foreach (var p in _win.PrebufNeeded) if (!_have[p]) return;
            _prebufDone = true;
            _win.Prebuffering = false;
            AceLog.Info(Tag, "prebuffer complete (" + _win.PrebufNeeded.Count + " pieces)");
        }

        // ---------- Playback position and reading ----------

        private sealed class ReaderInfo
        {
            public long Pos;
            public long LastMs;
        }

        private readonly Dictionary<object, ReaderInfo> _readers = new Dictionary<object, ReaderInfo>();
        private object _owner;
        private long _ownerMs;
        private const long OwnerTakeoverMs = 3000;

        /// <summary>
        /// A new reader (a new Range request = a seek) takes control of the playback position.
        /// The rule is: the last reader created, or the one that read most recently, wins.
        /// </summary>
        public void RegisterReader(object reader, long filePos)
        {
            lock (_sync)
            {
                if (_meta == null || _disposed) return;
                _readers[reader] = new ReaderInfo { Pos = filePos, LastMs = PeerConnection.NowMs() };
                AceLog.Debug(Tag, "new reader at " + filePos + " (readers " + _readers.Count + "): takes the window");
                _owner = reader;
                _ownerMs = PeerConnection.NowMs();
                SetPlaybackLocked(filePos);
            }
        }

        /// <summary>
        /// The reader has read (gotData) or is waiting. It only moves the window if it is the owner; a reader that reads
        /// while another is the owner (e.g. the player probes the end of the file over a second connection) only takes it over
        /// if the owner has been inactive for more than 3 s. This way two connections do not fight over the window.
        /// </summary>
        public void ReaderMoved(object reader, long filePos, bool gotData)
        {
            lock (_sync)
            {
                if (_meta == null || _disposed) return;
                long now = PeerConnection.NowMs();
                ReaderInfo ri;
                if (!_readers.TryGetValue(reader, out ri)) return;
                ri.Pos = filePos;
                if (gotData) ri.LastMs = now;
                if (_owner == reader)
                {
                    if (gotData) _ownerMs = now;
                    SetPlaybackLocked(filePos);
                }
                else if (gotData && (_owner == null || now - _ownerMs > OwnerTakeoverMs))
                {
                    AceLog.Debug(Tag, "reader at " + filePos + " takes the window (the owner had gone " + (now - _ownerMs) + " ms without reading)");
                    _owner = reader;
                    _ownerMs = now;
                    SetPlaybackLocked(filePos);
                }
            }
        }

        public void UnregisterReader(object reader)
        {
            lock (_sync)
            {
                if (!_readers.Remove(reader)) return;
                if (_owner != reader || _disposed || _meta == null) return;
                // The owner left: the window passes to the reader that read most recently (if any is left)
                object best = null; ReaderInfo bi = null;
                foreach (var kv in _readers) if (bi == null || kv.Value.LastMs > bi.LastMs) { best = kv.Key; bi = kv.Value; }
                _owner = best;
                _ownerMs = PeerConnection.NowMs();
                AceLog.Debug(Tag, "the window owner left; passing to " + (best == null ? "nobody" : "the reader at " + bi.Pos));
                if (best != null) SetPlaybackLocked(bi.Pos);
            }
        }

        private void SetPlaybackLocked(long filePos)
        {
            if (filePos < 0) filePos = 0;
            if (filePos > _file.Length) filePos = _file.Length;
            _playPos = filePos;
            long abs = _file.Offset + Math.Min(filePos, Math.Max(0, _file.Length - 1));
            int piece = _file.Length == 0 ? _firstPiece : (int)(abs / _pieceLen);
            piece = Math.Max(_firstPiece, Math.Min(_lastPiece, piece));
            bool firstMove = !_hasReader;
            if (!_hasReader)
            {
                _hasReader = true;
                // the prebuffer is done from where the first reader starts, plus the end of the file
                if (!_prebufDone && piece != _firstPiece && _have.Count == 0) ResetPrebufferLocked(piece);
            }
            if (piece == _playPiece) return;
            int old = _playPiece;
            _playPiece = piece;
            // natural advance of a sequential read (an HTTP block is 64 KiB); anything else is a seek
            int slack = (int)Math.Max(8, (1024 * 1024 + _pieceLen - 1) / _pieceLen);
            bool continuous = piece > old && piece - old <= slack;
            if (!continuous && !_prebufDone && !firstMove)
            {
                // The prebuffer belongs to startup (start + end of the file). If the player seeks before it finishes, it no longer makes
                // sense: it is abandoned so as not to keep requesting with priority pieces it no longer cares about.
                _prebufDone = true;
                AceLog.Info(Tag, "seek during the prebuffer: abandoning it (piece " + old + " -> " + piece + ")");
            }
            UpdateWindowLocked();
            if (!continuous) OnSeekLocked();
            else TrimBehindLocked();          // only in continuous playback; after a seek what was already downloaded is not thrown away
            foreach (var p in PeersByRateLocked()) { UpdateInterestLocked(p); FillRequestsLocked(p); }
        }

        /// <summary>Big seek: requests no longer in the window are cancelled and distant partial pieces are discarded.</summary>
        private void OnSeekLocked()
        {
            AceLog.Debug(Tag, "seek to piece " + _playPiece);
            foreach (var peer in _peers)
            {
                var drop = peer.Outstanding.Where(b => !InWindow(b.Piece) && !_win.PrebufNeeded.Contains(b.Piece)).ToList();
                foreach (var b in drop)
                {
                    peer.Outstanding.Remove(b);
                    ReleaseBlockLocked(b);
                    peer.Send(Wire.Cancel(b.Piece, b.Begin, b.Length));
                }
            }
            foreach (var ap in _active.Values.ToList())
            {
                if (ap.Verifying || InWindow(ap.Index) || _win.PrebufNeeded.Contains(ap.Index)) continue;
                if (ap.ReqCount.All(r => r == 0)) DropActiveLocked(ap);
            }
        }

        public enum ReadStatus { Data, NotAvailable, Eof, Stopped }

        /// <summary>Copies file data from filePos if the piece is in RAM.</summary>
        public ReadStatus TryRead(long filePos, byte[] buffer, int offset, int count, out int copied)
        {
            copied = 0;
            lock (_sync)
            {
                if (_disposed) return ReadStatus.Stopped;
                if (_meta == null) return ReadStatus.NotAvailable;
                if (filePos >= _file.Length) return ReadStatus.Eof;
                long abs = _file.Offset + filePos;
                int piece = (int)(abs / _pieceLen);
                byte[] data;
                if (!_store.TryGetValue(piece, out data)) return ReadStatus.NotAvailable;
                int inPiece = (int)(abs - (long)piece * _pieceLen);
                long fileRemaining = _file.Length - filePos;
                int n = (int)Math.Min(Math.Min(count, data.Length - inPiece), fileRemaining);
                Buffer.BlockCopy(data, inPiece, buffer, offset, n);
                copied = n;
                return ReadStatus.Data;
            }
        }

        public void NotifyWaiting()
        {
            // a reader is waiting for data: make sure it is being requested (e.g. after a long pause)
            lock (_sync) { foreach (var p in _peers) FillRequestsLocked(p); }
        }
    }
}
