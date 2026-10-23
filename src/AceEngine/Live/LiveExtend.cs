using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Live
{
    /// <summary>Data announced in the EXTEND and in MYINFO.</summary>
    public sealed class LiveLocalState
    {
        public long Lsp = -1;
        public long Position = -1;
        public long MinPiece = -1, MaxPiece = -1;
        public long DownloadWindowEnd = -1;
        public long LiveWindowSize;
        public long DownRate, UpRate, TopUpRate, TopSessionUpRate;
        public int ListenPort;
        public bool Accessible;
        /// <summary>mi.upload_rating (real peers send 0-11; we send 0) and mi.distance_from_source (-1 = unknown).</summary>
        public int UploadRating;
        public int DistanceFromSource = -1;
    }

    /// <summary>A live EXTEND (20/0) already parsed. Fields that are not present keep their default value.</summary>
    public sealed class LiveExtendInfo
    {
        public BDict Raw;
        public long V;                      // version of the remote engine
        public int Pv, Nt = -1, Platform;
        public string Tt;
        public long Lsp = -1;
        public int Port;
        public long Ts;
        public int AceMetadataVersion;
        public int UtMetadataId;
        public byte[] NodeId, Signature, YourIp;
        public bool HasSignature;
        /// <summary>true/false if it could be checked; null if it carried no signature.</summary>
        public bool? SignatureValid;
        public BDict Mi;
        public long MiPosition = -1, MiMinPiece = -1, MiMaxPiece = -1, MiLsp = -1, MiDownloadWindowEnd = -1, MiLiveWindowSize;
        public long MiPeerType = -1;

        public bool IsAuthorizedNode { get { return Nt == 2 || Nt == 3 || Nt == 4; } }
    }

    /// <summary>
    /// Building, signing and reading of the live EXTEND: bencode with Ace-style sorted keys, signed with ed25519 over the
    /// SHA-256 of the bencode (permid.sign_message_dict): signature = 64 zeros and node_id = public key before computing the hash;
    /// afterwards the marker "64:" + 64 zeros is replaced by "64:" + signature.
    /// </summary>
    public static class LiveExtend
    {
        private static readonly byte[] ZeroSig = new byte[Ed25519.SignatureLength];

        public static BDict BuildMiDictionary(LiveOptions o, LiveLocalState s, int peerType)
        {
            var mi = new BDict();
            if (o.LongMiKeys)
            {
                // Long names as a real peer sends them (capture of 2026-10-16)
                mi.Add("distance_from_source", BValue.Int(s.DistanceFromSource));
                mi.Add("down_rate", BValue.Int(s.DownRate));
                mi.Add("download_window_end", BValue.Int(s.DownloadWindowEnd));
                mi.Add("is_accessible", BValue.Int(s.Accessible ? 1 : 0));
                mi.Add("live_window_size", BValue.Int(s.LiveWindowSize));
                mi.Add("lsp", BValue.Int(s.Lsp));
                mi.Add("mam", BValue.Int(-1));
                mi.Add("max_piece", BValue.Int(s.MaxPiece));
                mi.Add("min_piece", BValue.Int(s.MinPiece));
                mi.Add("peer_type", BValue.Int(peerType));
                mi.Add("ping_from_source", BValue.Int(-1));
                mi.Add("position", BValue.Int(s.Position));
                mi.Add("time_from_source", BValue.Int(-1));
                mi.Add("top_session_up_rate", BValue.Int(s.TopSessionUpRate));
                mi.Add("top_up_rate", BValue.Int(s.TopUpRate));
                mi.Add("up_rate", BValue.Int(s.UpRate));
                mi.Add("upload_rating", BValue.Int(s.UploadRating));
            }
            else
            {
                AddMyInfoLetters(mi, s, peerType);
            }
            return mi;
        }

        /// <summary>One-letter keys of MYINFO v2 (CONFIRMED also in a real capture of message 11).</summary>
        public static void AddMyInfoLetters(BDict d, LiveLocalState s, int peerType)
        {
            d.Add("a", BValue.Int(s.Accessible ? 1 : 0));
            d.Add("b", BValue.Int(s.DownRate));
            d.Add("c", BValue.Int(s.UpRate));
            d.Add("d", BValue.Int(s.TopUpRate));
            d.Add("e", BValue.Int(s.TopSessionUpRate));
            d.Add("f", BValue.Int(s.UploadRating));
            d.Add("g", BValue.Int(s.Position));
            d.Add("h", BValue.Int(s.LiveWindowSize));
            d.Add("i", BValue.Int(s.MinPiece));
            d.Add("j", BValue.Int(s.MaxPiece));
            d.Add("k", BValue.Int(-1));
            d.Add("l", BValue.Int(-1));
            d.Add("m", BValue.Int(-1));
            d.Add("n", BValue.Int(peerType));
            d.Add("o", BValue.Int(0));
            d.Add("p", BValue.Int(0));
            d.Add("q", BValue.Int(0));
            d.Add("r", BValue.Int(s.Lsp));
            d.Add("s", BValue.Int(s.DownloadWindowEnd));
        }

        /// <summary>
        /// EXTEND payload (without the 20/0 prefix). 'seed' and 'publicKey' are the node's; if o.SignExtend is false node_id and signature are not added.
        /// </summary>
        public static byte[] BuildPayload(LiveOptions o, LiveLocalState s, int peerType, byte[] remoteIp, long ts, byte[] seed, byte[] publicKey)
        {
            var d = new BDict();
            d.Add("ace_metadata_version", BValue.Int(1));
            var m = new BDict(); m.Add("ut_metadata", BValue.Int(ExtensionMessages.LocalUtMetadata));
            d.Add("m", m);
            d.Add("mi", BuildMiDictionary(o, s, peerType));
            d.Add("nt", BValue.Int(o.NodeType));
            d.Add("p", BValue.Int(s.ListenPort));
            d.Add("platform", BValue.Int(o.Platform));
            d.Add("pv", BValue.Int(o.ProtocolVersion));
            d.Add("tt", BValue.Str(o.TransportType));
            d.Add("ts", BValue.Int(ts));
            d.Add("v", BValue.Int(o.EngineVersion));
            d.Add("lsp", BValue.Int(s.Lsp));
            if (remoteIp != null && remoteIp.Length == 4) d.Add("yourip", BValue.Bytes(remoteIp));
            var writeOpt = new BencodeWriteOptions { Order = KeyOrder.Ace };
            if (!o.SignExtend || seed == null) return BencodeWriter.Encode(d, writeOpt);

            d.Add("signature", BValue.Bytes(ZeroSig));
            d.Add("node_id", BValue.Bytes(publicKey));
            var b = BencodeWriter.Encode(d, writeOpt);
            var sig = Ed25519.Sign(seed, HashUtil.Sha256(b));
            var marker = BencodeWriter.Encode(BValue.Bytes(ZeroSig));
            int at = IndexOf(b, marker, 0);
            if (at < 0) throw new InvalidOperationException("signature marker not found");
            Buffer.BlockCopy(sig, 0, b, at + marker.Length - Ed25519.SignatureLength, Ed25519.SignatureLength);
            return b;
        }

        public static byte[] BuildFrame(byte[] payload)
        {
            return Wire.Extended(0, payload);
        }

        /// <summary>
        /// Parses the received EXTEND (msg[0] = 20, msg[1] = 0, the bencode from msg[2]) and checks the signature if present:
        /// bencode(signature) is replaced by bencode(64 zeros) in the received bytes and Ed25519 is verified over SHA-256.
        /// </summary>
        public static bool TryParse(byte[] msg, out LiveExtendInfo info)
        {
            return TryParse(msg, out info, true);
        }

        /// <summary>verifySignature=false skips the ed25519 verification (about 5 ms on PC, quite a bit more on the TV): HasSignature is still filled in and SignatureValid stays null.</summary>
        public static bool TryParse(byte[] msg, out LiveExtendInfo info, bool verifySignature)
        {
            info = null;
            if (msg.Length < 3) return false;
            BDict d;
            int used;
            try { d = BencodeParser.Parse(msg, 2, msg.Length - 2, BencodeParseOptions.Default, out used) as BDict; }
            catch (BencodeException) { return false; }
            if (d == null) return false;
            var x = new LiveExtendInfo { Raw = d };
            x.V = d.GetInt("v") ?? 0;
            x.Pv = (int)(d.GetInt("pv") ?? 0);
            x.Nt = (int)(d.GetInt("nt") ?? -1);
            x.Platform = (int)(d.GetInt("platform") ?? 0);
            x.Tt = d.GetString("tt");
            x.Lsp = d.GetInt("lsp") ?? -1;
            x.Port = (int)(d.GetInt("p") ?? 0);
            x.Ts = d.GetInt("ts") ?? 0;
            x.AceMetadataVersion = (int)(d.GetInt("ace_metadata_version") ?? 0);
            var m = d.GetDict("m");
            if (m != null)
            {
                x.UtMetadataId = (int)(m.GetInt("ut_metadata") ?? 0);
            }
            var yip = d.GetBytes("yourip");
            if (yip != null && yip.Length == 4) x.YourIp = yip;
            x.Mi = d.GetDict("mi");
            if (x.Mi != null)
            {
                x.MiPosition = GetAny(x.Mi, "position", "g");
                x.MiMinPiece = GetAny(x.Mi, "min_piece", "i");
                x.MiMaxPiece = GetAny(x.Mi, "max_piece", "j");
                x.MiLsp = GetAny(x.Mi, "lsp", "r");
                x.MiDownloadWindowEnd = GetAny(x.Mi, "download_window_end", "s");
                x.MiLiveWindowSize = Math.Max(0, GetAny(x.Mi, "live_window_size", "h"));
                x.MiPeerType = GetAny(x.Mi, "peer_type", "n");
            }
            x.NodeId = d.GetBytes("node_id");
            x.Signature = d.GetBytes("signature");
            x.HasSignature = x.NodeId != null && x.Signature != null;
            if (x.HasSignature && verifySignature) x.SignatureValid = VerifySignature(msg, 2, msg.Length - 2, used, x.NodeId, x.Signature);
            info = x;
            return true;
        }

        private static long GetAny(BDict d, string a, string b)
        {
            var v = d.GetInt(a);
            if (!v.HasValue) v = d.GetInt(b);
            return v ?? -1;
        }

        private static bool VerifySignature(byte[] buf, int offset, int count, int used, byte[] nodeId, byte[] sig)
        {
            if (nodeId.Length != Ed25519.PublicKeyLength || sig.Length != Ed25519.SignatureLength) return false;
            var raw = new byte[used];
            Buffer.BlockCopy(buf, offset, raw, 0, used);
            var sigMarker = BencodeWriter.Encode(BValue.Bytes(sig));
            var zeroMarker = BencodeWriter.Encode(BValue.Bytes(ZeroSig));
            // Reverse substitution of ALL occurrences (Python's bytes.replace)
            var replaced = ReplaceAll(raw, sigMarker, zeroMarker);
            return Ed25519.Verify(nodeId, HashUtil.Sha256(replaced), sig);
        }

        private static byte[] ReplaceAll(byte[] data, byte[] find, byte[] repl)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                int i = 0;
                while (i < data.Length)
                {
                    int at = IndexOf(data, find, i);
                    if (at < 0) { ms.Write(data, i, data.Length - i); break; }
                    ms.Write(data, i, at - i);
                    ms.Write(repl, 0, repl.Length);
                    i = at + find.Length;
                }
                return ms.ToArray();
            }
        }

        public static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            for (int i = start; i + pattern.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        /// <summary>Readable text of the dictionary (for the exchange logs).</summary>
        public static string Describe(BDict d)
        {
            var sb = new StringBuilder();
            Describe(sb, d, 0);
            return sb.ToString();
        }

        private static void Describe(StringBuilder sb, BValue v, int depth)
        {
            var bi = v as BInt;
            if (bi != null) { sb.Append(bi.Value); return; }
            var bs = v as BString;
            if (bs != null)
            {
                string t;
                if (bs.Count <= 24 && bs.TryGetUtf8(out t) && t.All(c => !char.IsControl(c))) sb.Append('"').Append(t).Append('"');
                else sb.Append("0x").Append(Hex.ToHex(bs.Value, 0, Math.Min(bs.Count, 12))).Append(bs.Count > 12 ? "..(" + bs.Count + "B)" : "");
                return;
            }
            var bl = v as BList;
            if (bl != null)
            {
                sb.Append('[');
                for (int i = 0; i < bl.Count; i++) { if (i > 0) sb.Append(", "); Describe(sb, bl[i], depth + 1); }
                sb.Append(']');
                return;
            }
            var bd = v as BDict;
            if (bd != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var e in bd.Entries)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(e.Key.AsText()).Append('=');
                    Describe(sb, e.Value, depth + 1);
                }
                sb.Append('}');
            }
        }
    }
}
