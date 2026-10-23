using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using AceEngine.Bencode;

namespace AceEngine.P2P
{
    public sealed class ExtendedHandshakeInfo
    {
        /// <summary>Dictionary 'm': name -> id the REMOTE uses to receive that extension.</summary>
        public Dictionary<string, int> M = new Dictionary<string, int>(StringComparer.Ordinal);
        public int? ListenPort;
        public string Client;               // 'v'
        public int? MetadataSize;
        public int? AceMetadataVersion;
        public byte[] YourIp;
        public int? Reqq;

        public int UtMetadataId { get { int v; return M.TryGetValue("ut_metadata", out v) ? v : 0; } }
        public int UtPexId { get { int v; return M.TryGetValue("ut_pex", out v) ? v : 0; } }

        /// <summary>An Ace engine is recognized by 'ace_metadata_version' or by 'v' = "ACEStream ...".</summary>
        public bool LooksLikeAce
        {
            get
            {
                return AceMetadataVersion.HasValue || (Client != null && Client.StartsWith("ACEStream", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public sealed class MetadataMessage
    {
        public int MsgType;                 // 0 request, 1 data, 2 reject
        public int Piece;
        public long? TotalSize;
        public bool Full;
        /// <summary>request_hash of Ace's 'full' response: the hash that was requested (it may not be the SHA-1 of the data).</summary>
        public byte[] RequestHash;
        /// <summary>Position (inside the passed buffer) where the data following the dictionary starts.</summary>
        public int DataOffset;
        public int DataLength;
    }

    /// <summary>
    /// BEP10 extension messages / ut_metadata (BEP9) / ut_pex (BEP11) with Ace's particularities.
    /// Local ids: ut_pex = 1 and ut_metadata = 2 (those of MiniBitTorrent). Only the extensions
    /// that Ace handles are announced: an extension id the remote does not know makes it close the connection.
    /// </summary>
    public static class ExtensionMessages
    {
        public const byte LocalUtPex = 1;
        public const byte LocalUtMetadata = 2;
        public const int MetadataPieceSize = 16384;

        public static byte[] BuildExtendedHandshake(string clientName, int listenPort, bool pex, bool metadata, int? metadataSize, byte[] yourIp)
        {
            var m = new BDict();
            if (pex) m.Add("ut_pex", BValue.Int(LocalUtPex));
            if (metadata) m.Add("ut_metadata", BValue.Int(LocalUtMetadata));
            var d = new BDict();
            d.Add("m", m);
            if (listenPort > 0) d.Add("p", BValue.Int(listenPort));
            d.Add("v", BValue.Str(clientName));
            d.Add("e", BValue.Int(0));
            d.Add("reqq", BValue.Int(64));
            if (yourIp != null && yourIp.Length == 4) d.Add("yourip", BValue.Bytes(yourIp));
            if (metadataSize.HasValue && metadataSize.Value > 0) d.Add("metadata_size", BValue.Int(metadataSize.Value));
            return BencodeWriter.Encode(d);
        }

        public static bool TryParseExtendedHandshake(byte[] buf, int offset, int count, out ExtendedHandshakeInfo info)
        {
            info = null;
            BValue v;
            try { int used; v = BencodeParser.Parse(buf, offset, count, BencodeParseOptions.Default, out used); }
            catch (BencodeException) { return false; }
            var d = v as BDict;
            if (d == null) return false;
            var h = new ExtendedHandshakeInfo();
            var m = d.Get("m") as BDict;
            if (m != null)
            {
                foreach (var e in m.Entries)
                {
                    var iv = e.Value as BInt;
                    if (iv != null) { h.M[e.Key.AsText()] = (int)iv.Value; continue; }
                    // 1-character bytes values -> ord()
                    var sv = e.Value as BString;
                    if (sv != null && sv.Count == 1) h.M[e.Key.AsText()] = sv.Value[0];
                }
            }
            h.ListenPort = (int?)d.GetInt("p");
            h.Client = d.GetString("v");
            h.MetadataSize = (int?)d.GetInt("metadata_size");
            h.AceMetadataVersion = (int?)d.GetInt("ace_metadata_version");
            h.Reqq = (int?)d.GetInt("reqq");
            var yip = d.GetBytes("yourip");
            if (yip != null && (yip.Length == 4 || yip.Length == 16)) h.YourIp = yip;
            info = h;
            return true;
        }

        // ---------- ut_metadata ----------

        /// <summary>Standard BEP9: {msg_type:0, piece:n}.</summary>
        public static byte[] BuildMetadataRequest(int piece)
        {
            var d = new BDict();
            d.Add("msg_type", BValue.Int(0));
            d.Add("piece", BValue.Int(piece));
            return BencodeWriter.Encode(d);
        }

        /// <summary>Ace extension: {msg_type:0, version:1, full:1}; requests the complete transport.</summary>
        public static byte[] BuildAceFullMetadataRequest()
        {
            var d = new BDict();
            d.Add("msg_type", BValue.Int(0));
            d.Add("version", BValue.Int(1));
            d.Add("full", BValue.Int(1));
            return BencodeWriter.Encode(d);
        }

        public static byte[] BuildMetadataReject(int piece)
        {
            var d = new BDict();
            d.Add("msg_type", BValue.Int(2));
            d.Add("piece", BValue.Int(piece));
            return BencodeWriter.Encode(d);
        }

        public static byte[] BuildMetadataData(int piece, long totalSize, byte[] data, int offset, int count)
        {
            var d = new BDict();
            d.Add("msg_type", BValue.Int(1));
            d.Add("piece", BValue.Int(piece));
            d.Add("total_size", BValue.Int(totalSize));
            var head = BencodeWriter.Encode(d);
            var r = new byte[head.Length + count];
            Buffer.BlockCopy(head, 0, r, 0, head.Length);
            Buffer.BlockCopy(data, offset, r, head.Length, count);
            return r;
        }

        public static bool TryParseMetadataMessage(byte[] buf, int offset, int count, out MetadataMessage msg)
        {
            msg = null;
            BValue v;
            int used;
            try { v = BencodeParser.Parse(buf, offset, count, BencodeParseOptions.Default, out used); }
            catch (BencodeException) { return false; }
            var d = v as BDict;
            if (d == null) return false;
            var t = d.GetInt("msg_type");
            if (!t.HasValue) return false;
            var m = new MetadataMessage
            {
                MsgType = (int)t.Value,
                Piece = (int)(d.GetInt("piece") ?? 0),
                TotalSize = d.GetInt("total_size"),
                Full = (d.GetInt("full") ?? 0) != 0,
                RequestHash = d.GetBytes("request_hash"),
                DataOffset = offset + used,
                DataLength = count - used
            };
            msg = m;
            return true;
        }

        // ---------- ut_pex ----------

        public static bool TryParsePex(byte[] buf, int offset, int count, List<IPEndPoint> added)
        {
            BValue v;
            try { int used; v = BencodeParser.Parse(buf, offset, count, BencodeParseOptions.Default, out used); }
            catch (BencodeException) { return false; }
            var d = v as BDict;
            if (d == null) return false;
            var a = d.GetBytes("added");
            if (a == null) return true;
            if (a.Length % 6 != 0) return false;           // Ace: malformed PEX -> CLOSE
            for (int i = 0; i + 6 <= a.Length; i += 6)
            {
                var ip = new IPAddress(new[] { a[i], a[i + 1], a[i + 2], a[i + 3] });
                int port = (a[i + 4] << 8) | a[i + 5];
                if (port == 0 || ip.Equals(IPAddress.Loopback)) continue;      // Ace discards 127.0.0.1
                added.Add(new IPEndPoint(ip, port));
            }
            return true;
        }

        public static byte[] BuildPex(IList<IPEndPoint> added)
        {
            var bytes = new List<byte>();
            foreach (var ep in added)
            {
                if (ep.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                bytes.AddRange(ep.Address.GetAddressBytes());
                bytes.Add((byte)(ep.Port >> 8)); bytes.Add((byte)ep.Port);
            }
            var d = new BDict();
            d.Add("added", BValue.Bytes(bytes.ToArray()));
            d.Add("added.f", BValue.Bytes(new byte[bytes.Count / 6]));
            d.Add("dropped", BValue.Bytes(new byte[0]));
            return BencodeWriter.Encode(d);
        }
    }
}
