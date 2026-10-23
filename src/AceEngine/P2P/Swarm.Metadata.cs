using System;
using System.Linq;
using System.Security.Cryptography;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.P2P
{
    internal sealed partial class Swarm
    {
        private byte[] _metaBuf;
        private bool[] _metaGot;
        private long[] _metaReqAt;
        private int _metaSize;
        private const int MaxMetadataSize = 8 * 1024 * 1024;

        /// <summary>Fatal error interpreting the metadata (e.g. it was a live stream); the session shows it.</summary>
        public Exception Fatal { get; private set; }

        private void OnExtHandshakeForMetadataLocked(PeerConnection peer)
        {
            if (_meta != null) return;
            var e = peer.Ext;
            if (e == null || e.UtMetadataId == 0 || !e.MetadataSize.HasValue) return;
            int size = e.MetadataSize.Value;
            if (size <= 0 || size > MaxMetadataSize) return;
            if (_metaBuf == null)
            {
                _metaSize = size;
                _metaBuf = new byte[size];
                int pieces = (size + ExtensionMessages.MetadataPieceSize - 1) / ExtensionMessages.MetadataPieceSize;
                _metaGot = new bool[pieces];
                _metaReqAt = new long[pieces];
                AceLog.Info(Tag, "ut_metadata: " + size + " bytes in " + pieces + " pieces (peer " + peer + ")");
            }
            else if (size != _metaSize) return;
            RequestMetadataPieceLocked(peer);
        }

        private void RequestMetadataPieceLocked(PeerConnection peer)
        {
            if (_meta != null || _metaBuf == null || peer.Ext == null || peer.Ext.UtMetadataId == 0) return;
            if (peer.Ext.MetadataSize != _metaSize) return;
            if (peer.MetadataPieceInFlight >= 0) return;
            long now = PeerConnection.NowMs();
            for (int i = 0; i < _metaGot.Length; i++)
            {
                if (_metaGot[i]) continue;
                if (_metaReqAt[i] != 0 && now - _metaReqAt[i] < 10000) continue;
                _metaReqAt[i] = now;
                peer.MetadataPieceInFlight = i;
                peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, ExtensionMessages.BuildMetadataRequest(i)));
                return;
            }
        }

        private void HandleMetadataMessageLocked(PeerConnection peer, byte[] msg, int offset, int count)
        {
            MetadataMessage m;
            if (!ExtensionMessages.TryParseMetadataMessage(msg, offset, count, out m))
                throw new ProtocolViolation("malformed ut_metadata");
            switch (m.MsgType)
            {
                case 0:     // request from a peer: we serve 'info' (BEP9); Ace's 'full' variant is not served
                    if (_meta == null || m.Full || peer.Ext == null || peer.Ext.UtMetadataId == 0)
                    {
                        if (peer.Ext != null && peer.Ext.UtMetadataId != 0)
                            peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, ExtensionMessages.BuildMetadataReject(m.Piece)));
                        return;
                    }
                    {
                        var raw = _meta.InfoRaw;
                        int start = m.Piece * ExtensionMessages.MetadataPieceSize;
                        if (m.Piece < 0 || start >= raw.Length)
                        {
                            peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, ExtensionMessages.BuildMetadataReject(m.Piece)));
                            return;
                        }
                        int n = Math.Min(ExtensionMessages.MetadataPieceSize, raw.Length - start);
                        // Standard BEP9: total_size = total size of the metadata
                        peer.Send(Wire.Extended((byte)peer.Ext.UtMetadataId, ExtensionMessages.BuildMetadataData(m.Piece, raw.Length, raw, start, n)));
                    }
                    return;

                case 2:     // reject
                    if (peer.MetadataPieceInFlight == m.Piece) peer.MetadataPieceInFlight = -1;
                    if (_metaReqAt != null && m.Piece >= 0 && m.Piece < _metaReqAt.Length) _metaReqAt[m.Piece] = 0;
                    return;

                case 1:     // data
                    if (_meta != null || _metaBuf == null) return;
                    if (peer.MetadataPieceInFlight == m.Piece) peer.MetadataPieceInFlight = -1;
                    if (m.Piece < 0 || m.Piece >= _metaGot.Length || _metaGot[m.Piece]) { RequestMetadataPieceLocked(peer); return; }
                    {
                        int start = m.Piece * ExtensionMessages.MetadataPieceSize;
                        int expected = Math.Min(ExtensionMessages.MetadataPieceSize, _metaSize - start);
                        // Ace puts the block size in total_size instead of the total: the real data size is used.
                        if (m.DataLength != expected)
                        {
                            AceLog.Debug(Tag, peer + " metadata piece " + m.Piece + " of " + m.DataLength + " bytes (expected " + expected + ")");
                            _metaReqAt[m.Piece] = 0;
                            return;
                        }
                        Buffer.BlockCopy(msg, m.DataOffset, _metaBuf, start, expected);
                        _metaGot[m.Piece] = true;
                    }
                    if (_metaGot.All(g => g)) CompleteMetadataLocked();
                    else RequestMetadataPieceLocked(peer);
                    return;

                default:
                    throw new ProtocolViolation("ut_metadata msg_type " + m.MsgType);
            }
        }

        private void CompleteMetadataLocked()
        {
            byte[] hash;
            using (var sha = SHA1.Create()) hash = sha.ComputeHash(_metaBuf);
            if (!HashUtil.ConstantTimeEquals(hash, _infoHash))
            {
                // It is not known which peer lied: everything is discarded and started over (never trust unverified metadata)
                AceLog.Warn(Tag, "metadata with a SHA-1 different from the infohash; discarding it");
                _metaBuf = null; _metaGot = null;
                foreach (var p in _peers) p.MetadataPieceInFlight = -1;
                foreach (var p in _peers.ToList()) OnExtHandshakeForMetadataLocked(p);
                return;
            }
            try
            {
                var meta = TorrentMeta.FromInfoBytes(_metaBuf);
                ApplyMetadataLocked(meta);
            }
            catch (Exception ex)
            {
                Fatal = ex;
                _message = ex.Message;
                AceLog.Error(Tag, "unusable metadata: " + ex.Message);
                _metaReady.TrySetException(ex);
                _metaBuf = null;
                return;
            }
            _metaBuf = null; _metaGot = null;
            ApplyPendingPeerStateLocked();
            foreach (var p in _peers) { UpdateInterestLocked(p); FillRequestsLocked(p); }
            StartTrackers();      // torrent trackers (the magnet only brought its own)
            Changed.Pulse();
        }
    }
}
