using System;
using System.Collections.Generic;
using AceEngine.Bencode;
using AceEngine.P2P;

namespace AceEngine.Live
{
    /// <summary>Message identifiers of the live protocol. VOD closes on ids 10-39.</summary>
    public static class LiveId
    {
        public const byte Choke = 0, Unchoke = 1, Interested = 2, NotInterested = 3, Have = 4, Bitfield = 5, Request = 6, Piece = 7, Cancel = 8, Port = 9;
        public const byte Lost = 10, MyInfo = 11, Peers = 12, Close = 13, HpRequest = 14, Broadcast = 15, Unicast = 16;
        public const byte ChangePos = 18, Seek = 19, Extend = 20, SeekResp = 21, HlsMetadataStreams = 22, SeekPos = 24, SeekPosResp = 25;
        public const byte HlsMetadataPieceList = 32, HlsStartedDownload = 33, Ping = 34, Pong = 35, BroadcastV2 = 36, UnicastV2 = 37, GetPeers = 38, LogData = 39;

        /// <summary>Assigned ids; 17, 23, 26-31 and &gt;39 do not exist and the original closes the connection on them.</summary>
        public static bool IsKnown(byte id)
        {
            if (id <= 16) return true;
            switch (id)
            {
                case 18: case 19: case 20: case 21: case 22: case 24: case 25: case 32: case 33: case 34: case 35: case 36: case 37: case 38: case 39:
                    return true;
                default: return false;
            }
        }

        public static string Name(byte id)
        {
            switch (id)
            {
                case Choke: return "CHOKE"; case Unchoke: return "UNCHOKE"; case Interested: return "INTERESTED"; case NotInterested: return "NOT_INTERESTED";
                case Have: return "HAVE"; case Bitfield: return "BITFIELD"; case Request: return "REQUEST"; case Piece: return "PIECE"; case Cancel: return "CANCEL";
                case Port: return "PORT"; case Lost: return "LOST"; case MyInfo: return "MYINFO"; case Peers: return "PEERS"; case Close: return "CLOSE";
                case HpRequest: return "HP_REQUEST"; case Broadcast: return "BROADCAST"; case Unicast: return "UNICAST"; case ChangePos: return "CHANGEPOS";
                case Seek: return "SEEK"; case Extend: return "EXTEND"; case SeekResp: return "SEEKRESP"; case SeekPos: return "SEEKPOS"; case SeekPosResp: return "SEEKPOSRESP";
                case Ping: return "PING"; case Pong: return "PONG"; case BroadcastV2: return "BROADCAST_V2"; case UnicastV2: return "UNICAST_V2"; case GetPeers: return "GET_PEERS";
                case LogData: return "LOG_DATA";
                default: return "id" + id;
            }
        }
    }

    /// <summary>Capabilities deduced from the remote's 'v'.</summary>
    public sealed class LiveCaps
    {
        public long RemoteVersion;
        public bool TimestampsV2, MyInfoV2, Ping, BroadcastV2, WideChunk;

        public static LiveCaps FromVersion(long v)
        {
            return new LiveCaps
            {
                RemoteVersion = v,
                TimestampsV2 = (v > 3000040 && v < 3000100) || v >= 3000400,
                MyInfoV2 = (v > 3000040 && v < 3000100) || v >= 3000400,
                Ping = v > 3012599,
                BroadcastV2 = v > 3012599,
                WideChunk = v > 3002399
            };
        }
    }

    /// <summary>A decoded BROADCAST / BROADCAST_V2.</summary>
    public sealed class LiveBroadcast
    {
        public int Version;
        public int Ttl, MaxTtl;
        public byte[] OriginIp;
        public int OriginPort;
        public byte[] OriginPeerId;
        public ulong Seq;
        public int MessageId;
        public ulong CreatedMicro;
        public byte[] Payload;
        public int PayloadOffset;
    }

    /// <summary>Building and parsing of the live messages. All big-endian; the builders return the frame with its length prefix.</summary>
    public static class LiveWire
    {
        private static byte[] Frame(byte id, int payloadLen)
        {
            var b = new byte[5 + payloadLen];
            Wire.PutU32(b, 0, (uint)(1 + payloadLen));
            b[4] = id;
            return b;
        }

        public static byte[] Simple(byte id) { return new byte[] { 0, 0, 0, 1, id }; }

        public static void PutDouble(byte[] b, int off, double v)
        {
            Wire.PutU64(b, off, (ulong)BitConverter.DoubleToInt64Bits(v));
        }

        public static double GetDouble(byte[] b, int off)
        {
            return BitConverter.Int64BitsToDouble((long)Wire.GetU64(b, off));
        }

        // ---------- HAVE / LOST ----------

        /// <summary>HAVE: Q index [d timestamp] (9 / 17 message bytes).</summary>
        public static byte[] Have(ulong index, double? timestamp)
        {
            var b = Frame(LiveId.Have, timestamp.HasValue ? 16 : 8);
            Wire.PutU64(b, 5, index);
            if (timestamp.HasValue) PutDouble(b, 13, timestamp.Value);
            return b;
        }

        public static bool TryParseHave(byte[] msg, out ulong index, out double? timestamp)
        {
            index = 0; timestamp = null;
            if (msg.Length != 9 && msg.Length != 17) return false;
            index = Wire.GetU64(msg, 1);
            if (msg.Length == 17) timestamp = GetDouble(msg, 9);
            return true;
        }

        public static byte[] Lost(ulong index)
        {
            var b = Frame(LiveId.Lost, 8);
            Wire.PutU64(b, 5, index);
            return b;
        }

        public static bool TryParseLost(byte[] msg, out ulong index)
        {
            index = 0;
            if (msg.Length != 9) return false;
            index = Wire.GetU64(msg, 1);
            return true;
        }

        // ---------- BITFIELD (window) ----------

        /// <summary>BITFIELD: Q first_index, I nbits, [d first_ts, d last_ts], big-endian bitarray (bit j = piece first+j).</summary>
        public static byte[] Bitfield(ulong firstIndex, int nbits, byte[] bits, double? firstTs, double? lastTs)
        {
            bool ts = firstTs.HasValue && lastTs.HasValue;
            int need = (nbits + 7) / 8;
            if (bits.Length < need || need == 0) throw new ArgumentException("empty or short bitfield");
            var b = Frame(LiveId.Bitfield, 12 + (ts ? 16 : 0) + need);
            Wire.PutU64(b, 5, firstIndex);
            Wire.PutU32(b, 13, (uint)nbits);
            int o = 17;
            if (ts) { PutDouble(b, o, firstTs.Value); PutDouble(b, o + 8, lastTs.Value); o += 16; }
            Buffer.BlockCopy(bits, 0, b, o, need);
            return b;
        }

        /// <summary>
        /// Parses a BITFIELD. The format with or without timestamps is deduced from the length (the protocol notes took them as active for v &gt;= 3000400 but a real 3.1.74 peer
        /// sends them without): without timestamps the total is 13 + ceil(nbits/8); with them, 29 + ceil(nbits/8).
        /// </summary>
        public static bool TryParseBitfield(byte[] msg, out ulong first, out int nbits, out int bitsOffset, out double? firstTs, out double? lastTs)
        {
            first = 0; nbits = 0; bitsOffset = 0; firstTs = null; lastTs = null;
            if (msg.Length <= 13) return false;
            first = Wire.GetU64(msg, 1);
            uint n = Wire.GetU32(msg, 9);
            if (n == 0 || n > 1000000) return false;
            nbits = (int)n;
            int need = ((int)n + 7) / 8;
            if (msg.Length == 13 + need) { bitsOffset = 13; return true; }
            if (msg.Length == 29 + need)
            {
                firstTs = GetDouble(msg, 13); lastTs = GetDouble(msg, 21);
                bitsOffset = 29;
                return true;
            }
            // extra padding bits: accepted if there are at least 'need' bytes
            if (msg.Length > 13 + need) { bitsOffset = msg.Length - need; return false; }
            return false;
        }

        public static bool BitAt(byte[] msg, int bitsOffset, int j)
        {
            return ((msg[bitsOffset + (j >> 3)] >> (7 - (j & 7))) & 1) != 0;
        }

        // ---------- REQUEST / HP_REQUEST / CANCEL ----------

        public static byte[] Request(byte id, ulong index, int chunk, bool wide)
        {
            var b = Frame(id, 8 + (wide ? 2 : 1));
            Wire.PutU64(b, 5, index);
            if (wide) Wire.PutU16(b, 13, (ushort)chunk); else b[13] = (byte)chunk;
            return b;
        }

        public static bool TryParseRequest(byte[] msg, out ulong index, out int chunk)
        {
            index = 0; chunk = 0;
            if (msg.Length == 10) { index = Wire.GetU64(msg, 1); chunk = msg[9]; return true; }
            if (msg.Length == 11) { index = Wire.GetU64(msg, 1); chunk = Wire.GetU16(msg, 9); return true; }
            return false;
        }

        // ---------- PIECE ----------

        public static byte[] Piece(ulong index, double timestamp, int chunk, bool wide, byte[] data, int offset, int count)
        {
            var b = Frame(LiveId.Piece, 16 + (wide ? 2 : 1) + count);
            Wire.PutU64(b, 5, index);
            PutDouble(b, 13, timestamp);
            int o = 21;
            if (wide) { Wire.PutU16(b, o, (ushort)chunk); o += 2; } else { b[o] = (byte)chunk; o += 1; }
            Buffer.BlockCopy(data, offset, b, o, count);
            return b;
        }

        /// <summary>PIECE header with the given chunk width: Q index, d timestamp, chunk, data (from dataOffset).</summary>
        public static bool TryParsePiece(byte[] msg, bool wide, out ulong index, out double timestamp, out int chunk, out int dataOffset)
        {
            index = 0; timestamp = 0; chunk = 0; dataOffset = 0;
            int w = wide ? 2 : 1;
            if (msg.Length < 17 + w) return false;
            index = Wire.GetU64(msg, 1);
            timestamp = GetDouble(msg, 9);
            chunk = wide ? Wire.GetU16(msg, 17) : msg[17];
            dataOffset = 17 + w;
            return true;
        }

        // ---------- CHANGEPOS ----------

        public static byte[] ChangePos(ulong index, uint window)
        {
            var b = Frame(LiveId.ChangePos, 12);
            Wire.PutU64(b, 5, index);
            Wire.PutU32(b, 13, window);
            return b;
        }

        public static bool TryParseChangePos(byte[] msg, out ulong index, out uint window)
        {
            index = 0; window = 0;
            if (msg.Length != 13) return false;
            index = Wire.GetU64(msg, 1);
            window = Wire.GetU32(msg, 9);
            return true;
        }

        // ---------- PING / PONG ----------

        public static byte[] Ping(int seq, int size)
        {
            var b = Frame(LiveId.Ping, 8);
            Wire.PutU32(b, 5, (uint)seq); Wire.PutU32(b, 9, (uint)size);
            return b;
        }

        public static byte[] Pong(int seq, int size)
        {
            var b = Frame(LiveId.Pong, 8 + Math.Max(0, size));
            Wire.PutU32(b, 5, (uint)seq); Wire.PutU32(b, 9, (uint)size);
            return b;
        }

        public static bool TryParsePingPong(byte[] msg, out int seq, out int size)
        {
            seq = 0; size = 0;
            if (msg.Length < 9) return false;
            seq = (int)Wire.GetU32(msg, 1); size = (int)Wire.GetU32(msg, 5);
            return true;
        }

        // ---------- MYINFO v2 ----------

        /// <summary>MYINFO v2: bencode of a dict with one-letter keys.</summary>
        public static byte[] MyInfo(BDict dict)
        {
            var payload = BencodeWriter.Encode(dict, new BencodeWriteOptions { Order = KeyOrder.Ace });
            var b = Frame(LiveId.MyInfo, payload.Length);
            Buffer.BlockCopy(payload, 0, b, 5, payload.Length);
            return b;
        }

        public static BDict ParseMyInfo(byte[] msg)
        {
            if (msg == null || msg.Length < 2) return null;
            try
            {
                int used;
                return BencodeParser.Parse(msg, 1, msg.Length - 1, BencodeParseOptions.Default, out used) as BDict;
            }
            catch (BencodeException) { return null; }
        }

        // ---------- BROADCAST / BROADCAST_V2 ----------

        /// <summary>BROADCAST_V2: &gt;HHHH4sH20sQBQ (version=2, payload_offset=52, ttl, max_ttl, ip, port, peer_id, seq, message_id, created_at_micro) + payload.</summary>
        public static byte[] BroadcastV2(int ttl, int maxTtl, byte[] ip, int port, byte[] peerId, ulong seq, int messageId, ulong createdMicro, byte[] payload)
        {
            var b = Frame(LiveId.BroadcastV2, 51 + payload.Length);
            Wire.PutU16(b, 5, 2); Wire.PutU16(b, 7, 52); Wire.PutU16(b, 9, (ushort)ttl); Wire.PutU16(b, 11, (ushort)maxTtl);
            Buffer.BlockCopy(ip, 0, b, 13, 4);
            Wire.PutU16(b, 17, (ushort)port);
            Buffer.BlockCopy(peerId, 0, b, 19, 20);
            Wire.PutU64(b, 39, seq);
            b[47] = (byte)messageId;
            Wire.PutU64(b, 48, createdMicro);
            Buffer.BlockCopy(payload, 0, b, 56, payload.Length);
            return b;
        }

        /// <summary>BROADCAST v1: &gt;H4sH20sQB (ttl, ip, port, peer_id, seq, message_id) + payload.</summary>
        public static byte[] BroadcastV1(int ttl, byte[] ip, int port, byte[] peerId, ulong seq, int messageId, byte[] payload)
        {
            var b = Frame(LiveId.Broadcast, 37 + payload.Length);
            Wire.PutU16(b, 5, (ushort)ttl);
            Buffer.BlockCopy(ip, 0, b, 7, 4);
            Wire.PutU16(b, 11, (ushort)port);
            Buffer.BlockCopy(peerId, 0, b, 13, 20);
            Wire.PutU64(b, 33, seq);
            b[41] = (byte)messageId;
            Buffer.BlockCopy(payload, 0, b, 42, payload.Length);
            return b;
        }

        public static bool TryParseBroadcast(byte[] msg, out LiveBroadcast bc)
        {
            bc = null;
            if (msg.Length < 2) return false;
            if (msg[0] == LiveId.BroadcastV2)
            {
                if (msg.Length <= 52) return false;
                int version = Wire.GetU16(msg, 1), offset = Wire.GetU16(msg, 3);
                if (offset < 52 || offset > msg.Length) return false;
                var b = new LiveBroadcast
                {
                    Version = version, PayloadOffset = offset,
                    Ttl = Wire.GetU16(msg, 5), MaxTtl = Wire.GetU16(msg, 7),
                    OriginIp = Sub(msg, 9, 4), OriginPort = Wire.GetU16(msg, 13), OriginPeerId = Sub(msg, 15, 20),
                    Seq = Wire.GetU64(msg, 35), MessageId = msg[43], CreatedMicro = Wire.GetU64(msg, 44)
                };
                b.Payload = Sub(msg, offset, msg.Length - offset);
                bc = b;
                return true;
            }
            if (msg[0] == LiveId.Broadcast)
            {
                if (msg.Length <= 38) return false;
                var b = new LiveBroadcast
                {
                    Version = 1, PayloadOffset = 38, Ttl = Wire.GetU16(msg, 1), OriginIp = Sub(msg, 3, 4), OriginPort = Wire.GetU16(msg, 7),
                    OriginPeerId = Sub(msg, 9, 20), Seq = Wire.GetU64(msg, 29), MessageId = msg[37]
                };
                b.Payload = Sub(msg, 38, msg.Length - 38);
                bc = b;
                return true;
            }
            return false;
        }

        private static byte[] Sub(byte[] b, int o, int n)
        {
            var r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        /// <summary>BROADCAST_LAST_PIECE: v1 = bencode([index, timestamp]); v2 = bencode({'a': index, 'b': timestamp}). Tolerant of the types.</summary>
        public static bool TryParseLastPiece(byte[] payload, out ulong index, out double timestamp)
        {
            index = 0; timestamp = 0;
            BValue v;
            try { v = BencodeParser.Parse(payload); }
            catch (BencodeException) { return false; }
            BValue a = null, bv = null;
            var d = v as BDict;
            if (d != null) { a = d.Get("a"); bv = d.Get("b"); }
            var l = v as BList;
            if (l != null && l.Count >= 1) { a = l[0]; if (l.Count >= 2) bv = l[1]; }
            var ai = a as BInt;
            if (ai == null || ai.Value < 0) return false;
            index = (ulong)ai.Value;
            var bi = bv as BInt;
            if (bi != null) timestamp = bi.Value;
            else
            {
                var bs = bv as BString;
                double t;
                if (bs != null && double.TryParse(bs.AsText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)) timestamp = t;
            }
            return true;
        }

        public static byte[] LastPiecePayloadV2(ulong index, long timestamp)
        {
            var d = new BDict();
            d.Add("a", BValue.Int((long)index));
            d.Add("b", BValue.Int(timestamp));
            return BencodeWriter.Encode(d, new BencodeWriteOptions { Order = KeyOrder.Ace });
        }
    }
}
