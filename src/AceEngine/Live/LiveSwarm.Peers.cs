using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Live
{
    internal sealed partial class LiveSwarm
    {
        private sealed class LiveProtocolViolation : Exception
        {
            public LiveProtocolViolation(string m) : base(m) { }
        }

        private readonly Dictionary<string, long> _broadcastSeen = new Dictionary<string, long>();
        private readonly Dictionary<string, KeyValuePair<long, long>> _broadcastClaims = new Dictionary<string, KeyValuePair<long, long>>();   // origin -> (index, ms)

        // msg[0] = id; the rest is the payload
        public void OnPeerMessage(LivePeer peer, byte[] msg)
        {
            byte id = msg[0];
            string closeWhy = null;
            lock (_sync)
            {
                if (_disposed || peer.IsClosed) return;
                peer.MessagesIn++;
                if (_lo.LogAllMessages && AceLog.IsEnabled(LogLevel.Debug) && id != LiveId.Piece)
                    AceLog.Debug(Tag, peer + " <- " + LiveId.Name(id) + " " + msg.Length + " B " + Hex.ToHex(msg, 1, Math.Min(msg.Length - 1, 32)));
                try
                {
                    // any message other than EXTEND before the remote EXTEND closes the connection
                    if (!peer.RemoteExtended && id != LiveId.Extend)
                        throw new LiveProtocolViolation("Close on message before extend (" + LiveId.Name(id) + ")");
                    if (!LiveId.IsKnown(id))
                        throw new LiveProtocolViolation("got_message: close on unknown message " + id);
                    Dispatch(peer, id, msg);
                }
                catch (LiveProtocolViolation ex) { closeWhy = ex.Message; }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    closeWhy = "got bad data from peer: " + ex.GetType().Name + " " + ex.Message;
                    AceLog.Warn(Tag, peer + " " + closeWhy);
                }
            }
            if (closeWhy != null) peer.Close(closeWhy);
            if (id == LiveId.Piece || id == LiveId.Have || id == LiveId.Bitfield || id == LiveId.Unchoke) Changed.Pulse();
        }

        private void Dispatch(LivePeer peer, byte id, byte[] msg)
        {
            switch (id)
            {
                case LiveId.Extend: HandleExtendLocked(peer, msg); break;

                case LiveId.Choke:
                    if (msg.Length != 1) throw new LiveProtocolViolation("Close on bad simple message");
                    peer.PeerChoking = true;
                    ReleasePeerRequestsLocked(peer);
                    break;
                case LiveId.Unchoke:
                    if (msg.Length != 1) throw new LiveProtocolViolation("Close on bad simple message");
                    peer.PeerChoking = false;
                    peer.UnchokedAtMs = LivePeer.NowMs();
                    if (peer.Trace.UnchokeAbs < 0) peer.Trace.UnchokeAbs = peer.UnchokedAtMs;
                    peer.Trace.Reach(LiveStage.Unchoked);
                    break;
                case LiveId.Interested:
                    if (msg.Length != 1) throw new LiveProtocolViolation("Close on bad simple message");
                    peer.PeerInterested = true;
                    break;
                case LiveId.NotInterested:
                    if (msg.Length != 1) throw new LiveProtocolViolation("Close on bad simple message");
                    peer.PeerInterested = false;
                    break;
                case LiveId.Close:
                    throw new LiveProtocolViolation("the peer closed (CLOSE)");

                case LiveId.Have:
                    {
                        long idx; double? ts;
                        ulong u;
                        if (!LiveWire.TryParseHave(msg, out u, out ts)) throw new LiveProtocolViolation("msg len != 9 / != 17");
                        idx = (long)u;
                        if (ts.HasValue) peer.SawTimestampsInHave = true; else peer.SawNoTimestamps = true;
                        peer.Have.Set(idx);
                        if (idx > peer.HeadClaim) peer.HeadClaim = idx;
                        NoteHaveTrace(peer, idx, 1);
                        break;
                    }
                case LiveId.Lost:
                    {
                        ulong u;
                        if (!LiveWire.TryParseLost(msg, out u)) throw new LiveProtocolViolation("LOST with wrong length");
                        peer.Have.Clear((long)u);
                        break;
                    }
                case LiveId.Bitfield:
                    {
                        ulong first; int nbits, off; double? t0, t1;
                        if (!LiveWire.TryParseBitfield(msg, out first, out nbits, out off, out t0, out t1)) throw new LiveProtocolViolation("bad bitfield");
                        if (t0.HasValue) peer.SawTimestampsInHave = true; else peer.SawNoTimestamps = true;
                        int n = 0;
                        for (int j = 0; j < nbits; j++)
                            if (LiveWire.BitAt(msg, off, j)) { peer.Have.Set((long)first + j); n++; }
                        peer.GotBitfield = true;
                        if (peer.HeadClaim < 0 || (long)first + nbits - 1 > peer.HeadClaim) peer.HeadClaim = (long)first + nbits - 1;
                        if (n > 0) NoteHaveTrace(peer, LastSetBit(msg, off, nbits, (long)first), n);
                        AceLog.Debug(Tag, peer + " BITFIELD from " + first + ": " + n + "/" + nbits + " pieces" + (t0.HasValue ? " (with timestamps)" : ""));
                        break;
                    }
                case LiveId.Request:
                case LiveId.HpRequest:
                    HandleRequestLocked(peer, msg);
                    break;
                case LiveId.Cancel:
                    break;                                      // responses were already sent when the request was received
                case LiveId.Piece:
                    HandlePieceLocked(peer, msg);
                    break;
                case LiveId.ChangePos:
                    {
                        ulong idx; uint window;
                        if (!LiveWire.TryParseChangePos(msg, out idx, out window)) throw new LiveProtocolViolation("CHANGEPOS with wrong length");
                        peer.RemotePos = (long)idx; peer.RemoteWindow = window;
                        break;
                    }
                case LiveId.MyInfo:
                    {
                        var d = LiveWire.ParseMyInfo(msg);
                        if (d == null) break;
                        var pos = d.GetInt("g"); if (pos.HasValue) peer.MyInfoPosition = pos.Value;
                        var mn = d.GetInt("i"); if (mn.HasValue) peer.MyInfoMin = mn.Value;
                        var mx = d.GetInt("j"); if (mx.HasValue) peer.MyInfoMax = mx.Value;
                        var lsp = d.GetInt("r");
                        long claim = lsp.HasValue && lsp.Value >= 0 ? lsp.Value : (mx.HasValue ? mx.Value : -1);
                        if (claim >= 0) { peer.HeadClaim = claim; if (claim > peer.Trace.HeadClaim) peer.Trace.HeadClaim = claim; }                 // overwritten: a lower value is a sender restart
                        break;
                    }
                case LiveId.Ping:
                    {
                        int seq, size;
                        if (!LiveWire.TryParsePingPong(msg, out seq, out size)) throw new LiveProtocolViolation("PING with wrong length");
                        if (size < 0) size = 0;
                        if (size > _lo.MaxPongBytes) size = _lo.MaxPongBytes;
                        peer.Send(LiveWire.Pong(seq, size));
                        break;
                    }
                case LiveId.Pong:
                    break;
                case LiveId.Broadcast:
                case LiveId.BroadcastV2:
                    HandleBroadcastLocked(peer, msg);
                    break;
                default:
                    // PEERS, UNICAST*, SEEK*, GET_PEERS, HLS_*, LOG_DATA, PORT: unused by the client
                    if (id == LiveId.Peers && _lo.DumpPeersDir != null) DumpPeersMessage(peer, msg);
                    break;
            }
        }

        private void DumpPeersMessage(LivePeer peer, byte[] msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_lo.DumpPeersDir);
                var name = "peers_" + peer.EndPoint.ToString().Replace(':', '_') + "_v" + (peer.Ext == null ? 0 : peer.Ext.V) + "_" + msg.Length + ".hex";
                System.IO.File.WriteAllText(System.IO.Path.Combine(_lo.DumpPeersDir, name), Hex.ToHex(msg));
            }
            catch (Exception) { }
        }

        // ---------- EXTEND ----------

        private void HandleExtendLocked(LivePeer peer, byte[] msg)
        {
            if (msg.Length < 2) throw new LiveProtocolViolation("EXTEND too short");
            byte extId = msg[1];
            if (extId == 0)
            {
                if (peer.RemoteExtended) { AceLog.Debug(Tag, peer + " repeated EXTEND, ignored"); return; }
                LiveExtendInfo info;
                bool verify = _lo.StrictRemoteSignature || _lo.VerifyRemoteSignature || _exchangeLoggedRecv < _lo.LogExchangePeers;
                if (!LiveExtend.TryParse(msg, out info, verify)) throw new LiveProtocolViolation("malformed remote EXTEND");
                if (peer.Ext == null && ShouldLogExchangeLocked())
                    AceLog.Info(Tag, peer + " EXTEND received: " + LiveExtend.Describe(info.Raw));
                if (info.Tt != null && !string.Equals(info.Tt, _lo.TransportType, StringComparison.Ordinal))
                    throw new LiveProtocolViolation("transport type mismatch (" + info.Tt + " != " + _lo.TransportType + ")");
                if (info.Tt == null) AceLog.Debug(Tag, peer + " EXTEND without 'tt'");
                if (_lo.BanNonAcePeers && info.Nt < 0 && info.Pv == 0 && info.V == 0)
                {
                    _banned.Add(peer.EndPoint.ToString());
                    throw new LiveProtocolViolation("not an Ace engine (EXTEND without nt/pv/v)");
                }
                if (info.Pv < _lo.ProtocolVersion) AceLog.Info(Tag, peer + " old protocol version pv=" + info.Pv);
                if (info.HasSignature)
                {
                    if (info.SignatureValid == false)
                    {
                        AceLog.Warn(Tag, peer + " ed25519 signature of the EXTEND does NOT verify (signature verification failed)");
                        if (_lo.StrictRemoteSignature) throw new LiveProtocolViolation("signature verification failed, close connection");
                    }
                }
                else if (_lo.RequireRemoteSignature) throw new LiveProtocolViolation("EXTEND without signature");
                peer.Ext = info;
                peer.Caps = LiveCaps.FromVersion(info.V);
                peer.RemoteExtended = true;
                NoteExtendTrace(peer, info);
                if (_lo.LearnPeerType && info.Nt == 1 && info.MiPeerType >= 0 && _learnedPeerType != (int)info.MiPeerType)
                {
                    _learnedPeerType = (int)info.MiPeerType;
                    AceLog.Info(Tag, "peer_type learned from a client peer: " + _learnedPeerType);
                }
                peer.HeadClaim = Math.Max(info.Lsp, Math.Max(info.MiLsp, info.MiMaxPiece));
                if (info.MiMaxPiece >= 0) peer.MyInfoMax = info.MiMaxPiece;
                if (info.MiMinPiece >= 0) peer.MyInfoMin = info.MiMinPiece;
                if (info.MiPosition >= 0) peer.MyInfoPosition = info.MiPosition;
                AfterRemoteExtendLocked(peer);
                return;
            }
            if (extId == ExtensionMessages.LocalUtMetadata)
            {
                HandleMetadataRequestLocked(peer, msg);
                return;
            }
            AceLog.Debug(Tag, peer + " EXTEND with id " + extId + " unknown; ignored");
        }

        private static long LastSetBit(byte[] msg, int off, int nbits, long first)
        {
            for (int j = nbits - 1; j >= 0; j--) if (LiveWire.BitAt(msg, off, j)) return first + j;
            return first;
        }

        private void NoteHaveTrace(LivePeer peer, long maxIndex, int count)
        {
            var t = peer.Trace;
            long now = LivePeer.NowMs();
            if (t.HaveAbs < 0) t.HaveAbs = now;
            t.HaveMessages++;
            if (maxIndex > t.MaxHave) t.MaxHave = maxIndex;
            if (maxIndex > t.HeadClaim) t.HeadClaim = maxIndex;
            t.Reach(LiveStage.HaveSeen);
        }

        private static void NoteExtendTrace(LivePeer peer, LiveExtendInfo info)
        {
            var t = peer.Trace;
            t.ExtendAbs = LivePeer.NowMs();
            t.V = info.V; t.Nt = info.Nt; t.Pv = info.Pv; t.Platform = info.Platform; t.Tt = info.Tt;
            t.HadSignature = info.HasSignature;
            t.MiPeerType = (int)info.MiPeerType;
            t.Accessible = info.Mi == null ? -1 : (int)(info.Mi.GetInt("is_accessible") ?? info.Mi.GetInt("a") ?? -1);
            long head = Math.Max(info.Lsp, Math.Max(info.MiLsp, info.MiMaxPiece));
            if (head > t.HeadClaim) t.HeadClaim = head;
            if (info.MiMaxPiece > t.MaxHave) t.MaxHave = info.MiMaxPiece;
            t.Reach(LiveStage.ExtendOk);
        }

        private bool ShouldLogExchangeLocked()
        {
            return _exchangeLoggedRecv++ < _lo.LogExchangePeers;
        }
        private int _exchangeLoggedRecv;

        /// <summary>After receiving the remote EXTEND we can talk: MYINFO, own BITFIELD, INTERESTED and CHANGEPOS.</summary>
        private void AfterRemoteExtendLocked(LivePeer peer)
        {
            long now = LivePeer.NowMs();
            if (_lo.SendMyInfo) SendMyInfoLocked(peer, now);
            if (_lo.AnnounceHave && _lo.UploadSlots > 0 && _store.Count > 0) SendBitfieldLocked(peer);
            peer.AmInterested = true;
            peer.Send(LiveWire.Simple(LiveId.Interested));
            if (_started2) SendChangePosLocked(peer, now, true);
            AceLog.Debug(Tag, peer + " ready: v=" + peer.Ext.V + " nt=" + peer.Ext.Nt + " tt=" + peer.Ext.Tt + " lsp=" + peer.Ext.Lsp + " signature="
                + (peer.Ext.SignatureValid.HasValue ? peer.Ext.SignatureValid.ToString() : "no") + " caps: ts2=" + peer.Caps.TimestampsV2 + " chunkH=" + peer.Caps.WideChunk);
        }

        private bool UseWideChunk(LivePeer peer)
        {
            switch (_lo.ChunkWidth)
            {
                case ChunkWidthMode.Byte: return false;
                case ChunkWidthMode.Short: return true;
                default: return peer.Caps.WideChunk;
            }
        }

        private bool SendTimestampsTo(LivePeer peer)
        {
            if (_lo.SendTimestamps.HasValue) return _lo.SendTimestamps.Value;
            // Auto: the peer is mimicked (by the length of its HAVE/BITFIELD); until it is known, no timestamps (real capture with 3.1.74)
            return peer.SawTimestampsInHave && !peer.SawNoTimestamps;
        }

        // ---------- MYINFO, BITFIELD, CHANGEPOS ----------

        private void SendMyInfoLocked(LivePeer peer, long now)
        {
            var st = LocalStateLocked();
            var d = new BDict();
            LiveExtend.AddMyInfoLetters(d, st, PeerTypeLocked());
            peer.Send(LiveWire.MyInfo(d));
            peer.LastMyInfoSentMs = now;
        }

        private void SendBitfieldLocked(LivePeer peer)
        {
            if (_store.Count == 0) return;
            long min = long.MaxValue, max = 0;
            foreach (var k in _store.Keys) { if (k < min) min = k; if (k > max) max = k; }
            int nbits = (int)Math.Min(100000, max - min + 1);
            var bits = new byte[(nbits + 7) / 8];
            double? t0 = null, t1 = null;
            for (int j = 0; j < nbits; j++)
            {
                LivePiece p;
                if (_store.TryGetValue(min + j, out p))
                {
                    bits[j >> 3] |= (byte)(0x80 >> (j & 7));
                    p.Announced = true;
                    if (!t0.HasValue) t0 = p.Timestamp;
                    t1 = p.Timestamp;
                }
            }
            if (!SendTimestampsTo(peer)) { t0 = null; t1 = null; }
            peer.Send(LiveWire.Bitfield((ulong)min, nbits, bits, t0, t1));
        }

        private void SendChangePosLocked(LivePeer peer, long now, bool force)
        {
            if (!peer.RemoteExtended || !_started2) return;
            if (!force && peer.SentChangePos == _outPiece) return;
            if (!force && now - peer.LastChangePosSentMs < 1000 && Math.Abs(peer.SentChangePos - _outPiece) < 3) return;
            peer.SentChangePos = _outPiece;
            peer.LastChangePosSentMs = now;
            peer.Send(LiveWire.ChangePos((ulong)_outPiece, (uint)_windowPieces));
        }

        // ---------- BROADCAST: only lsp is extracted, it is not forwarded ----------

        private void HandleBroadcastLocked(LivePeer peer, byte[] msg)
        {
            LiveBroadcast bc;
            if (!LiveWire.TryParseBroadcast(msg, out bc)) throw new LiveProtocolViolation("malformed BROADCAST");
            string origin = Hex.ToHex(bc.OriginPeerId);
            string key = origin + ":" + bc.Seq;
            if (_broadcastSeen.ContainsKey(key)) return;           // deduplication by (origin, seq)
            if (_broadcastSeen.Count > 4000) _broadcastSeen.Clear();
            _broadcastSeen[key] = LivePeer.NowMs();
            // LAST_PIECE = 0 (v1) / LAST_PIECE_V2 = 4 (v2)
            if (bc.MessageId == 0 || bc.MessageId == 4)
            {
                ulong idx; double ts;
                if (LiveWire.TryParseLastPiece(bc.Payload, out idx, out ts))
                {
                    _broadcastClaims[origin] = new KeyValuePair<long, long>((long)idx, LivePeer.NowMs());
                    if (_broadcastClaims.Count > 200) _broadcastClaims.Remove(_broadcastClaims.Keys.First());
                }
            }
        }

        // ---------- ut_metadata (serving the descriptor, optional) ----------

        private void HandleMetadataRequestLocked(LivePeer peer, byte[] msg)
        {
            MetadataMessage m;
            if (!ExtensionMessages.TryParseMetadataMessage(msg, 2, msg.Length - 2, out m)) return;
            if (peer.Ext == null || peer.Ext.UtMetadataId == 0) return;
            if (m.MsgType != 0) return;
            if (m.Full && _transportBytes != null && _lo.UploadSlots > 0)
            {
                var d = new BDict();
                d.Add("msg_type", BValue.Int(1)); d.Add("full", BValue.Int(1)); d.Add("total_size", BValue.Int(_transportBytes.Length));
                d.Add("request_hash", BValue.Bytes(_requestedContentId != null && Hex.IsHex40(_requestedContentId) ? Hex.Parse(_requestedContentId) : _infoHash));
                var payload = BencodeWriter.Encode(d).Concat(_transportBytes).ToArray();
                peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, payload));
            }
            else
            {
                peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, ExtensionMessages.BuildMetadataReject(m.Piece)));
            }
        }
    }
}
