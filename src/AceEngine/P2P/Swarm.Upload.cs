using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm
    {
        private PeerConnection _optimistic;

        /// <summary>
        /// Upload: only pieces that are in RAM are served, and only to unchoked peers. A REQUEST from a peer we choked
        /// is silently dropped (like Ace). Maximum length 128 KiB: more is a reason for Ace to close.
        /// </summary>
        private void HandleRequestLocked(PeerConnection peer, int index, int begin, int length)
        {
            if (_have == null) return;
            if (index < 0 || index >= _pieceCount) throw new ProtocolViolation("REQUEST out of range " + index);
            if (length <= 0 || length > 131072) throw new ProtocolViolation("REQUEST of " + length + " bytes");
            if (peer.AmChoking || _opt.UploadSlots <= 0) return;
            byte[] data;
            if (!_store.TryGetValue(index, out data)) return;       // evicted from RAM or we do not have it yet
            if (begin < 0 || begin + length > data.Length) throw new ProtocolViolation("REQUEST outside the piece");
            var limiter = _opt.UploadLimiter;
            if (limiter != null && !limiter.TryTake(length)) return;        // upload limit: dropped, the peer will repeat it or ask another one
            peer.Send(Wire.Piece(index, begin, data, begin, length));
            peer.Up.Add(length);
            _upMeter.Add(length);
        }

        /// <summary>
        /// Simplified choker: up to UploadSlots-1 interested peers that give us the most (minimum 1000 B/s, like Ace's
        /// change) and an optimistic unchoke that rotates every 30 s.
        /// </summary>
        private void RechokeLocked(bool rotateOptimistic)
        {
            var interested = _peers.Where(p => p.PeerInterested && p.Ready).ToList();
            var chosen = new HashSet<PeerConnection>();
            if (_opt.UploadSlots > 0 && interested.Count > 0)
            {
                foreach (var p in interested.Where(p => p.Down.Rate >= 1000).OrderByDescending(p => p.Down.Rate).Take(Math.Max(0, _opt.UploadSlots - 1)))
                    chosen.Add(p);
                if (rotateOptimistic || _optimistic == null || !interested.Contains(_optimistic))
                {
                    var rest = interested.Where(p => !chosen.Contains(p)).ToList();
                    _optimistic = rest.Count == 0 ? null : rest[Rng.Next(rest.Count)];
                }
                if (_optimistic != null) chosen.Add(_optimistic);
            }
            foreach (var p in _peers)
            {
                bool should = chosen.Contains(p);
                if (should && p.AmChoking) { p.AmChoking = false; p.Send(Wire.Unchoke()); }
                else if (!should && !p.AmChoking) { p.AmChoking = true; p.Send(Wire.Choke()); }
            }
        }
    }
}
