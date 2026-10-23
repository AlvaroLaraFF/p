using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm : IIncomingTarget
    {
        bool IIncomingTarget.Accept(System.Net.Sockets.TcpClient tcp, bool aceProtocolName, HandshakeInfo head)
        {
            // VOD only registers "BitTorrent protocol": an AceStreamProtocol handshake is rejected
            return !aceProtocolName && AddIncoming(tcp, head);
        }

        // ---------- Candidates and outgoing connections ----------

        public void AddCandidates(IEnumerable<IPEndPoint> endpoints, string source)
        {
            int added = 0;
            lock (_sync)
            {
                if (_disposed) return;
                foreach (var ep in endpoints)
                {
                    if (ep.Port <= 0 || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.None)) continue;
                    string key = ep.ToString();
                    if (_cands.ContainsKey(key) || _banned.Contains(ep.Address.ToString())) continue;
                    if (_cands.Count >= 3000) break;
                    _cands[key] = new Candidate { EndPoint = ep, Source = source };
                    added++;
                }
            }
            if (added > 0) AceLog.Debug(Tag, "+" + added + " candidates from " + source);
        }

        /// <summary>Launches outgoing connections until the gaps are filled (MaxPeers, MaxPendingConnects).</summary>
        private void ConnectMoreLocked(long now, List<PeerConnection> toStart)
        {
            if (_disposed) return;
            int slots = _opt.MaxPeers - _all.Count;
            int pendingRoom = _opt.MaxPendingConnects - _connecting;
            int n = Math.Min(slots, pendingRoom);
            if (n <= 0) return;
            var eligible = new List<Candidate>();
            foreach (var c in _cands.Values)
            {
                if (c.InUse || c.NextAttemptMs > now) continue;
                eligible.Add(c);
            }
            if (eligible.Count == 0) return;
            Rng.Shuffle(eligible);
            foreach (var c in eligible.Take(n))
            {
                c.InUse = true;
                var peer = new PeerConnection(c.EndPoint, false);
                _all.Add(peer);
                peer.Connecting = true;
                _connecting++;
                toStart.Add(peer);
            }
        }

        private void StartPeers(List<PeerConnection> toStart)
        {
            foreach (var p in toStart)
            {
                var peer = p;
                var ignored = Task.Run(() => peer.RunOutgoingAsync(this));
            }
        }

        /// <summary>Incoming connection already identified by the listener.</summary>
        public bool AddIncoming(System.Net.Sockets.TcpClient tcp, HandshakeInfo head)
        {
            PeerConnection peer;
            lock (_sync)
            {
                if (_disposed || _all.Count >= _opt.MaxPeers) return false;
                var ep = (IPEndPoint)tcp.Client.RemoteEndPoint;
                if (_banned.Contains(ep.Address.ToString())) return false;
                peer = new PeerConnection(ep, true);
                _all.Add(peer);
                peer.Connecting = true;
                _connecting++;
            }
            var ignored = Task.Run(() => peer.RunIncomingAsync(this, tcp, head));
            return true;
        }

        // ---------- IPeerHost ----------

        public bool OnPeerReady(PeerConnection peer)
        {
            lock (_sync)
            {
                if (peer.Connecting) { peer.Connecting = false; _connecting--; }
                if (_disposed) return false;
                // There is already a connection with that peer_id: the new one is closed
                foreach (var q in _peers)
                    if (q.RemotePeerId != null && peer.RemotePeerId != null && q.RemotePeerId.SequenceEqual(peer.RemotePeerId))
                    { AceLog.Debug(Tag, peer + " duplicate of " + q); return false; }
                if (_peers.Count >= _opt.MaxPeers) return false;

                peer.Ready = true;
                peer.LastBlockMs = PeerConnection.NowMs();
                peer.Backlog = _opt.InitialBacklog;
                if (_have != null) peer.Have = new Bitfield(_pieceCount);
                _peers.Add(peer);
                var h = peer.RemoteHandshake;
                AceLog.Debug(Tag, peer + " connected (LTEP=" + h.SupportsLtep + ", DHT=" + h.SupportsDht + ", Fast=" + h.SupportsFast
                    + ", peer_id " + System.Text.Encoding.ASCII.GetString(peer.RemotePeerId).Replace('\0', '?') + ")");

                // 1) extended handshake (if the peer supports LTEP) 2) BITFIELD only if we have pieces
                if (h.SupportsLtep && (_opt.HandshakeReserved[5] & 0x10) != 0)
                {
                    SendExtHandshakeLocked(peer);
                }
                if (_have != null && _have.Count > 0) peer.Send(Wire.Bitfield(_have.ToBytes()));
                // If there is an associated candidate, it stays in use until it closes.
                return true;
            }
        }

        private void SendExtHandshakeLocked(PeerConnection peer)
        {
            peer.SentExtHandshake = true;
            int? metaSize = _meta != null ? _meta.InfoRaw.Length : (int?)null;
            byte[] yourIp = peer.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? peer.EndPoint.Address.GetAddressBytes() : null;
            var payload = ExtensionMessages.BuildExtendedHandshake(_opt.ExtendedClientName, _listenPort(), _opt.EnablePex, true, metaSize, yourIp);
            peer.Send(Wire.Extended(0, payload));
        }

        public void OnPeerClosed(PeerConnection peer, string reason)
        {
            bool wasReady;
            lock (_sync)
            {
                wasReady = peer.Ready;
                if (peer.Connecting) { peer.Connecting = false; _connecting--; }
                _all.Remove(peer);
                _peers.Remove(peer);
                string key = peer.EndPoint.ToString();
                Candidate c;
                if (_cands.TryGetValue(key, out c) && !peer.Incoming)
                {
                    c.InUse = false;
                    if (!wasReady)
                    {
                        c.Failures++;
                        // 20 s, 40 s and, after 3 consecutive failures, 10 min (the peer may be back online later)
                        c.NextAttemptMs = PeerConnection.NowMs() + (c.Failures >= 3 ? 600000L : 20000L * c.Failures);
                    }
                    else
                    {
                        c.NextAttemptMs = PeerConnection.NowMs() + 15000;      // retry after a while
                        c.Failures = 0;
                    }
                }
                if (wasReady)
                {
                    ReleaseOutstandingLocked(peer);
                    if (peer.Have != null && _avail != null)
                        for (int i = 0; i < _pieceCount; i++) if (peer.Have[i]) _avail[i]--;
                }
            }
            if (wasReady) AceLog.Debug(Tag, peer + " closed: " + reason);
            else AceLog.Debug(Tag, peer + " failed: " + reason);
        }

        // ---------- Messages ----------

        public void OnPeerMessage(PeerConnection peer, byte id, byte[] msg)
        {
            lock (_sync)
            {
                if (_disposed || !peer.Ready) return;
                try { HandleMessageLocked(peer, id, msg); }
                catch (ProtocolViolation pv)
                {
                    AceLog.Debug(Tag, peer + " violates the protocol: " + pv.Message);
                    var p = peer;
                    ThreadPool.QueueUserWorkItem(_ => p.Close("protocol: " + pv.Message));
                }
            }
        }

        private sealed class ProtocolViolation : Exception
        {
            public ProtocolViolation(string m) : base(m) { }
        }

        // msg[0] = id; the rest is the payload
        private void HandleMessageLocked(PeerConnection peer, byte id, byte[] msg)
        {
            int len = msg.Length;       // includes the id
            switch (id)
            {
                case WireId.Choke:
                    if (len != 1) throw new ProtocolViolation("CHOKE with payload");
                    peer.GotAnyMessage = true;
                    if (!peer.PeerChoking) AceLog.Debug(Tag, peer + " chokes us (" + peer.Outstanding.Count + " live requests)");
                    peer.PeerChoking = true;
                    ReleaseOutstandingLocked(peer);
                    break;

                case WireId.Unchoke:
                    if (len != 1) throw new ProtocolViolation("UNCHOKE with payload");
                    peer.GotAnyMessage = true;
                    if (peer.PeerChoking)
                    {
                        AceLog.Debug(Tag, peer + " unchokes us (interested=" + peer.AmInterested + ", has " + (peer.Have == null ? -1 : peer.Have.Count) + " pieces)");
                        peer.PeerChoking = false;
                        peer.UnchokedAtMs = PeerConnection.NowMs();
                        peer.Snubbed = false;
                        FillRequestsLocked(peer);
                    }
                    break;

                case WireId.Interested:
                    peer.GotAnyMessage = true;
                    peer.PeerInterested = true;
                    // Free upload slot: unchoked immediately instead of waiting for the next rechoke (5 s)
                    if (_opt.UploadSlots > 0 && peer.AmChoking && _peers.Count(q => !q.AmChoking) < _opt.UploadSlots)
                    {
                        peer.AmChoking = false;
                        peer.Send(Wire.Unchoke());
                    }
                    break;

                case WireId.NotInterested:
                    peer.GotAnyMessage = true;
                    peer.PeerInterested = false;
                    break;

                case WireId.Have:
                    {
                        if (len != 5) throw new ProtocolViolation("HAVE of length " + len);
                        peer.GotAnyMessage = true;
                        int idx = (int)Wire.GetU32(msg, 1);
                        if (_have == null)
                        {
                            if (peer.PendingHaves == null) peer.PendingHaves = new List<int>();
                            if (peer.PendingHaves.Count < 100000) peer.PendingHaves.Add(idx);
                            break;
                        }
                        if (idx < 0 || idx >= _pieceCount) throw new ProtocolViolation("HAVE out of range " + idx);
                        if (peer.Have.Set(idx)) _avail[idx]++;
                        UpdateInterestLocked(peer);
                        FillRequestsLocked(peer);
                        break;
                    }

                case WireId.Bitfield:
                    {
                        // Ace: it must be the first non-EXTEND message; we tolerate it but log it.
                        if (peer.GotAnyMessage) AceLog.Debug(Tag, peer + " late BITFIELD");
                        peer.GotAnyMessage = true;
                        if (_have == null)
                        {
                            peer.PendingBitfield = new byte[len - 1];
                            Buffer.BlockCopy(msg, 1, peer.PendingBitfield, 0, len - 1);
                            break;
                        }
                        ApplyBitfieldLocked(peer, msg, 1, len - 1);
                        break;
                    }

                case WireId.Request:
                    if (len != 13) throw new ProtocolViolation("REQUEST of length " + len);
                    peer.GotAnyMessage = true;
                    HandleRequestLocked(peer, (int)Wire.GetU32(msg, 1), (int)Wire.GetU32(msg, 5), (int)Wire.GetU32(msg, 9));
                    break;

                case WireId.Piece:
                    if (len <= 9) throw new ProtocolViolation("PIECE without data");
                    peer.GotAnyMessage = true;
                    HandlePieceLocked(peer, (int)Wire.GetU32(msg, 1), (int)Wire.GetU32(msg, 5), msg, 9, len - 9);
                    break;

                case WireId.Cancel:
                    if (len != 13) throw new ProtocolViolation("CANCEL of length " + len);
                    peer.GotAnyMessage = true;
                    break;      // we serve requests instantly: there is no queue to cancel

                case WireId.Port:
                    // Ace treats it as an error; other clients send it if they believe we support DHT. Ignored.
                    break;

                case WireId.Extended:
                    if (len < 3) throw new ProtocolViolation("EXTEND too short");
                    HandleExtendedLocked(peer, msg);
                    break;

                default:
                    // Fast (13-17), HASHPIECE (250), G2G (235), live ids (10-39): neither announced nor used.
                    AceLog.Debug(Tag, peer + " ignored message id=" + id);
                    break;
            }
        }

        private void ApplyBitfieldLocked(PeerConnection peer, byte[] data, int offset, int count)
        {
            var bf = Bitfield.FromBytes(data, offset, count, _pieceCount);
            if (bf == null) throw new ProtocolViolation("invalid BITFIELD (length " + count + " for " + _pieceCount + " pieces)");
            if (peer.Have != null)
                for (int i = 0; i < _pieceCount; i++) if (peer.Have[i]) _avail[i]--;
            peer.Have = bf;
            for (int i = 0; i < _pieceCount; i++) if (bf[i]) _avail[i]++;
            UpdateInterestLocked(peer);
            FillRequestsLocked(peer);
        }

        /// <summary>Applies the BITFIELD/HAVE that arrived before the metadata was known.</summary>
        private void ApplyPendingPeerStateLocked()
        {
            foreach (var peer in _peers.ToList())
            {
                peer.Have = new Bitfield(_pieceCount);
                try
                {
                    if (peer.PendingBitfield != null)
                    {
                        ApplyBitfieldLocked(peer, peer.PendingBitfield, 0, peer.PendingBitfield.Length);
                        peer.PendingBitfield = null;
                    }
                    if (peer.PendingHaves != null)
                    {
                        foreach (var idx in peer.PendingHaves)
                            if (idx >= 0 && idx < _pieceCount && peer.Have.Set(idx)) _avail[idx]++;
                        peer.PendingHaves = null;
                        UpdateInterestLocked(peer);
                    }
                }
                catch (ProtocolViolation pv)
                {
                    var p = peer;
                    ThreadPool.QueueUserWorkItem(_ => p.Close("protocol: " + pv.Message));
                }
            }
        }

        // ---------- Extensions ----------

        private void HandleExtendedLocked(PeerConnection peer, byte[] msg)
        {
            byte extId = msg[1];
            if (extId == 0)
            {
                ExtendedHandshakeInfo info;
                if (!ExtensionMessages.TryParseExtendedHandshake(msg, 2, msg.Length - 2, out info))
                    throw new ProtocolViolation("malformed extended handshake");
                peer.Ext = info;
                AceLog.Debug(Tag, peer + " ext: v='" + info.Client + "' ut_metadata=" + info.UtMetadataId + " ut_pex=" + info.UtPexId
                    + " metadata_size=" + info.MetadataSize + (info.LooksLikeAce ? " [Ace]" : ""));
                if (!peer.SentExtHandshake && (peer.RemoteHandshake.SupportsLtep)) SendExtHandshakeLocked(peer);
                OnExtHandshakeForMetadataLocked(peer);
            }
            else if (extId == ExtensionMessages.LocalUtMetadata)
            {
                HandleMetadataMessageLocked(peer, msg, 2, msg.Length - 2);
            }
            else if (extId == ExtensionMessages.LocalUtPex)
            {
                if (!_opt.EnablePex) return;
                var eps = new List<IPEndPoint>();
                if (!ExtensionMessages.TryParsePex(msg, 2, msg.Length - 2, eps)) throw new ProtocolViolation("malformed PEX");
                if (eps.Count > 0)
                {
                    foreach (var ep in eps.Take(50))
                    {
                        string key = ep.ToString();
                        if (_cands.ContainsKey(key) || _cands.Count >= 3000) continue;
                        _cands[key] = new Candidate { EndPoint = ep, Source = "pex" };
                    }
                }
            }
            else
            {
                AceLog.Debug(Tag, peer + " unknown extension id=" + extId);
            }
        }

        // ---------- Interest ----------

        /// <summary>
        /// Interest as in any BitTorrent client: the peer has some piece of the file that we lack. Not just the current
        /// window is considered: if we stopped being interested when the window fills up, peers would choke us and after a seek
        /// we would have to wait for their next round of unchokes (10-30 s in Transmission/qBittorrent).
        /// </summary>
        private bool PeerHasWantedLocked(PeerConnection peer)
        {
            if (peer.Have == null || _have == null) return false;
            if (peer.Have.Count == 0) return false;
            // First the window (the normal case); then the rest of the file.
            foreach (var p in _win.PrebufNeeded) if (!_have[p] && peer.Have[p]) return true;
            int from = Math.Max(_firstPiece, _win.PlaybackPiece);
            for (int p = from; p <= _lastPiece; p++) if (!_have[p] && peer.Have[p]) return true;
            for (int p = _firstPiece; p < from; p++) if (!_have[p] && peer.Have[p]) return true;
            return false;
        }

        private void UpdateInterestLocked(PeerConnection peer)
        {
            if (_have == null) return;
            bool want = PeerHasWantedLocked(peer);
            if (want && !peer.AmInterested)
            {
                peer.AmInterested = true;
                peer.Send(Wire.Interested());
            }
            else if (!want && peer.AmInterested && peer.Outstanding.Count == 0)
            {
                peer.AmInterested = false;
                peer.Send(Wire.NotInterested());
            }
        }
    }
}
