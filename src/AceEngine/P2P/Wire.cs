using System;
using System.Text;

namespace AceEngine.P2P
{
    /// <summary>Message identifiers of the VOD wire protocol.</summary>
    public static class WireId
    {
        public const byte Choke = 0;
        public const byte Unchoke = 1;
        public const byte Interested = 2;
        public const byte NotInterested = 3;
        public const byte Have = 4;
        public const byte Bitfield = 5;
        public const byte Request = 6;
        public const byte Piece = 7;
        public const byte Cancel = 8;
        /// <summary>PORT: Ace closes the connection when it receives it; we never send it.</summary>
        public const byte Port = 9;
        /// <summary>Fast extension (13-17): never announced or sent.</summary>
        public const byte SuggestPiece = 13, HaveAll = 14, HaveNone = 15, RejectRequest = 16, AllowedFast = 17;
        public const byte Extended = 20;
        /// <summary>Tribler/Ace's own ids, only handled in order to ignore them: G2G (235) and Merkle HASHPIECE (250).</summary>
        public const byte G2G = 235, HashPiece = 250;
    }

    public sealed class HandshakeInfo
    {
        public byte[] Reserved;
        public byte[] InfoHash;
        public byte[] PeerId;      // null if only the first 48 bytes were read
        public bool SupportsLtep { get { return (Reserved[5] & 0x10) != 0; } }
        public bool SupportsDht { get { return (Reserved[7] & 0x01) != 0; } }
        public bool SupportsFast { get { return (Reserved[7] & 0x04) != 0; } }
    }

    /// <summary>Building and parsing of the wire protocol messages. All big-endian.</summary>
    public static class Wire
    {
        public const string ProtocolName = "BitTorrent protocol";
        public const int HandshakeLength = 68;
        public const int HandshakeHeadLength = 48;      // pstrlen + pstr + reserved + infohash

        public static readonly byte[] KeepAlive = { 0, 0, 0, 0 };

        // ---------- Handshake ----------

        /// <summary>
        /// Full 68-byte handshake. As initiator Ace sends only the first 48 and its peer_id after reading ours;
        /// as responder it reads 48, writes 68 and reads 20. Sending all 68 at once works with both.
        /// </summary>
        public static byte[] BuildHandshake(byte[] reserved, byte[] infoHash, byte[] peerId)
        {
            var b = new byte[HandshakeLength];
            WriteHead(b, reserved, infoHash);
            Buffer.BlockCopy(peerId, 0, b, 48, 20);
            return b;
        }

        public static byte[] BuildHandshakeHead(byte[] reserved, byte[] infoHash)
        {
            var b = new byte[HandshakeHeadLength];
            WriteHead(b, reserved, infoHash);
            return b;
        }

        private static void WriteHead(byte[] b, byte[] reserved, byte[] infoHash)
        {
            if (reserved == null || reserved.Length != 8) throw new ArgumentException("reserved must be 8 bytes", "reserved");
            if (infoHash == null || infoHash.Length != 20) throw new ArgumentException("infohash must be 20 bytes", "infoHash");
            b[0] = 19;
            Encoding.ASCII.GetBytes(ProtocolName, 0, 19, b, 1);
            Buffer.BlockCopy(reserved, 0, b, 20, 8);
            Buffer.BlockCopy(infoHash, 0, b, 28, 20);
        }

        /// <summary>
        /// Parses the first 48 bytes (and the peer_id if there are 68). Returns false if pstrlen/pstr are not BitTorrent's
        /// (Ace's VOD only registers "BitTorrent protocol").
        /// </summary>
        public static bool TryParseHandshake(byte[] buf, int count, out HandshakeInfo info)
        {
            info = null;
            if (count < HandshakeHeadLength) return false;
            if (buf[0] != 19) return false;
            for (int i = 0; i < 19; i++) if (buf[1 + i] != (byte)ProtocolName[i]) return false;
            var h = new HandshakeInfo { Reserved = new byte[8], InfoHash = new byte[20] };
            Buffer.BlockCopy(buf, 20, h.Reserved, 0, 8);
            Buffer.BlockCopy(buf, 28, h.InfoHash, 0, 20);
            if (count >= HandshakeLength)
            {
                h.PeerId = new byte[20];
                Buffer.BlockCopy(buf, 48, h.PeerId, 0, 20);
            }
            info = h;
            return true;
        }

        // ---------- Messages (with length prefix) ----------

        public static byte[] Choke() { return Simple(WireId.Choke); }
        public static byte[] Unchoke() { return Simple(WireId.Unchoke); }
        public static byte[] Interested() { return Simple(WireId.Interested); }
        public static byte[] NotInterested() { return Simple(WireId.NotInterested); }

        private static byte[] Simple(byte id)
        {
            return new byte[] { 0, 0, 0, 1, id };
        }

        public static byte[] Have(int index)
        {
            var b = new byte[9];
            PutU32(b, 0, 5); b[4] = WireId.Have; PutU32(b, 5, (uint)index);
            return b;
        }

        public static byte[] Bitfield(byte[] bits)
        {
            var b = new byte[5 + bits.Length];
            PutU32(b, 0, (uint)(1 + bits.Length)); b[4] = WireId.Bitfield;
            Buffer.BlockCopy(bits, 0, b, 5, bits.Length);
            return b;
        }

        public static byte[] Request(int index, int begin, int length)
        {
            return Triple(WireId.Request, index, begin, length);
        }

        public static byte[] Cancel(int index, int begin, int length)
        {
            return Triple(WireId.Cancel, index, begin, length);
        }

        private static byte[] Triple(byte id, int index, int begin, int length)
        {
            var b = new byte[17];
            PutU32(b, 0, 13); b[4] = id;
            PutU32(b, 5, (uint)index); PutU32(b, 9, (uint)begin); PutU32(b, 13, (uint)length);
            return b;
        }

        public static byte[] Piece(int index, int begin, byte[] data, int offset, int count)
        {
            var b = new byte[13 + count];
            PutU32(b, 0, (uint)(9 + count)); b[4] = WireId.Piece;
            PutU32(b, 5, (uint)index); PutU32(b, 9, (uint)begin);
            Buffer.BlockCopy(data, offset, b, 13, count);
            return b;
        }

        /// <summary>EXTEND (20): extId 0 = handshake, other = id announced by the receiver.</summary>
        public static byte[] Extended(byte extId, byte[] payload)
        {
            var b = new byte[6 + payload.Length];
            PutU32(b, 0, (uint)(2 + payload.Length)); b[4] = WireId.Extended; b[5] = extId;
            Buffer.BlockCopy(payload, 0, b, 6, payload.Length);
            return b;
        }

        // ---------- Utilities ----------

        public static void PutU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }

        public static uint GetU32(byte[] b, int off)
        {
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
        }

        public static void PutU16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v >> 8); b[off + 1] = (byte)v;
        }

        public static ushort GetU16(byte[] b, int off)
        {
            return (ushort)((b[off] << 8) | b[off + 1]);
        }

        public static void PutU64(byte[] b, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (56 - 8 * i));
        }

        public static ulong GetU64(byte[] b, int off)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[off + i];
            return v;
        }
    }
}
