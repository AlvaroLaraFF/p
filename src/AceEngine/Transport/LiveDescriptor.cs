using System;
using System.Collections.Generic;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.Transport
{
    /// <summary>
    /// BT descriptor of the new AceStreamTransport format, that of the classic live streams.
    /// Here it is only read and its infohash computed; the live protocol is implemented elsewhere.
    /// </summary>
    public sealed class LiveDescriptor
    {
        /// <summary>hash_fields of the descriptor, in this order.</summary>
        public static readonly string[] HashFields =
        {
            "name", "authmethod", "pubkey", "piece_length", "chunk_length", "bitrate", "premium", "commerce",
            "provider_key", "provider_sid", "access", "sharing", "private", "tns", "license"
        };

        public BDict Meta { get; private set; }
        public string Name { get; private set; }
        public string AuthMethod { get; private set; }
        public byte[] PubKey { get; private set; }
        public long PieceLength { get; private set; }
        public long Bitrate { get; private set; }
        public long ChunkLength { get; private set; }
        public IList<string> Trackers { get; private set; }
        public IList<string> MetaTrackers { get; private set; }
        public IList<string> StartupNodes { get; private set; }
        /// <summary>Informational channel metadata (real fields: quality, categories, permanent, provider_key, provider_sid).</summary>
        public string Quality { get; private set; }
        public IList<string> Categories { get; private set; }
        public bool Permanent { get; private set; }
        public string ProviderKey { get; private set; }
        public string ProviderSid { get; private set; }
        public bool AllowPublicTrackers { get; private set; }
        public bool IsPrivate { get; private set; }

        /// <summary>infohash = SHA1(bencode([[k, meta[k]] ...])) using the raw bytes of each value (the recommended variant).</summary>
        public byte[] InfoHash { get; private set; }

        public string InfoHashHex { get { return Hex.ToHex(InfoHash); } }

        public static LiveDescriptor FromMeta(BDict meta)
        {
            return new LiveDescriptor(meta);
        }

        /// <summary>
        /// Old-format live stream: torrent with info['live'] (authmethod, pubkey) whose infohash is the SHA-1 of the 'info'.
        /// INFERRED: piece length from info, bitrate in live['bps'] / info['bps'] / x-ts-properties['bitrate']; there is no real capture of this format.
        /// </summary>
        public static LiveDescriptor FromLiveTorrent(TorrentMeta t)
        {
            if (t == null || !t.IsLive) throw new TransportException("the torrent is not a live stream");
            return new LiveDescriptor(t);
        }

        private LiveDescriptor(TorrentMeta t)
        {
            var live = t.LiveInfo;
            Meta = t.Info;
            Name = t.Name;
            AuthMethod = (live == null ? null : live.GetString("authmethod")) ?? "None";
            PubKey = live == null ? null : live.GetBytes("pubkey");
            PieceLength = t.PieceLength;
            long? bps = live == null ? null : live.GetInt("bps");
            if (!bps.HasValue) bps = t.Info.GetInt("bps");
            if (!bps.HasValue && t.XTsProperties != null) bps = t.XTsProperties.GetInt("bitrate");
            Bitrate = bps ?? 0;
            if (Bitrate <= 0) throw new TransportException("old-format live stream without bitrate (bps)");
            ChunkLength = PieceLength;
            var tr = new List<string>();
            foreach (var tier in t.GetTrackerTiers()) foreach (var u in tier) tr.Add(u);
            Trackers = tr;
            MetaTrackers = new List<string>();
            StartupNodes = new List<string>();
            Categories = new List<string>();
            AllowPublicTrackers = true;
            IsPrivate = false;
            InfoHash = t.InfoHash;
        }

        private LiveDescriptor(BDict meta)
        {
            Meta = meta;
            // Required: if any is missing -> "missing required param '<k>'".
            foreach (var k in new[] { "name", "authmethod", "pubkey", "piece_length", "bitrate" })
                if (!meta.ContainsKey(k)) throw new TransportException("missing required param '" + k + "'");

            Name = meta.GetString("name") ?? "";
            AuthMethod = meta.GetString("authmethod") ?? "";
            PubKey = meta.GetBytes("pubkey");
            PieceLength = meta.GetInt("piece_length") ?? 0;
            Bitrate = meta.GetInt("bitrate") ?? 0;
            ChunkLength = meta.GetInt("chunk_length") ?? PieceLength;      // get_chunk_length
            Trackers = StringList(meta.Get("trackers"));
            MetaTrackers = StringList(meta.Get("meta_trackers"));
            StartupNodes = ParseNodeList(meta.Get("startup_nodes"));
            Quality = meta.GetString("quality");
            Categories = StringList(meta.Get("categories"));
            var perm = meta.Get("permanent") as BInt;
            Permanent = perm != null && perm.Value != 0;
            ProviderKey = meta.GetString("provider_key");
            ProviderSid = meta.GetString("provider_sid");
            var apt = meta.Get("allow_public_trackers") as BInt;
            AllowPublicTrackers = apt == null || apt.Value != 0;
            var priv = meta.Get("private") as BInt;
            IsPrivate = priv != null && priv.Value != 0;
            Validate();
            InfoHash = ComputeInfoHash(meta, true);
        }

        public const long MinPieceLength = 16 * 1024, MaxPieceLength = 16L * 1024 * 1024;
        public const long MinBitrate = 1000, MaxBitrate = 125000000;
        public const int MaxListEntries = 64, MaxStringLength = 512, MaxPubKeyLength = 1024, MaxNameLength = 256;

        /// <summary>Strict structural validation of the descriptor (it comes from the network): ranges of the numbers, size of the key and of the lists.</summary>
        private void Validate()
        {
            if (Name.Length > MaxNameLength) throw new TransportException("'name' too long");
            Name = TorrentMeta.Sanitize(Name);
            if (AuthMethod.Length == 0 || AuthMethod.Length > 16) throw new TransportException("invalid 'authmethod'");
            if (PubKey == null || PubKey.Length < 16 || PubKey.Length > MaxPubKeyLength) throw new TransportException("'pubkey' with an invalid size");
            if (PieceLength < MinPieceLength || PieceLength > MaxPieceLength) throw new TransportException("'piece_length' out of range (" + PieceLength + ")");
            if (ChunkLength < 1024 || ChunkLength > PieceLength) throw new TransportException("'chunk_length' out of range (" + ChunkLength + ")");
            if (Bitrate < MinBitrate || Bitrate > MaxBitrate) throw new TransportException("'bitrate' out of range (" + Bitrate + ")");
            Trackers = Limit(Trackers, true);
            MetaTrackers = Limit(MetaTrackers, true);
            StartupNodes = Limit(StartupNodes, false);
        }

        private static IList<string> Limit(IList<string> l, bool urls)
        {
            var res = new List<string>();
            foreach (var s in l)
            {
                if (res.Count >= MaxListEntries) break;
                if (s.Length == 0 || s.Length > MaxStringLength) continue;
                if (urls && !(s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) continue;
                res.Add(s);
            }
            return res;
        }

        /// <summary>
        /// Computes the infohash of a descriptor.
        /// useRawValues=true: concatenates the raw bytes of each value (does not depend on reproducing ensure_unicode).
        /// useRawValues=false: re-serializes with Ace's order (only for comparing).
        /// </summary>
        public static byte[] ComputeInfoHash(BDict meta, bool useRawValues)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                ms.WriteByte((byte)'l');
                foreach (var k in HashFields)
                {
                    BValue v;
                    if (!meta.TryGetValue(k, out v)) continue;
                    ms.WriteByte((byte)'l');
                    var kb = Encoding.UTF8.GetBytes(k);
                    var prefix = Encoding.ASCII.GetBytes(kb.Length.ToString() + ":");
                    ms.Write(prefix, 0, prefix.Length);
                    ms.Write(kb, 0, kb.Length);
                    var vb = BencodeWriter.Encode(v, new BencodeWriteOptions { Order = KeyOrder.Ace, UseRawBytes = useRawValues });
                    ms.Write(vb, 0, vb.Length);
                    ms.WriteByte((byte)'e');
                }
                ms.WriteByte((byte)'e');
                return HashUtil.Sha1(ms.ToArray());
            }
        }

        /// <summary>
        /// List of strings; a single element can carry several URLs separated by whitespace (CONFIRMED with a real descriptor:
        /// a single string 'udp://a udp://b udp://c'), so it is split on whitespace.
        /// </summary>
        private static IList<string> StringList(BValue v)
        {
            var res = new List<string>();
            var l = v as BList;
            if (l == null) return res;
            foreach (var i in l.Items)
            {
                var s = i as BString;
                if (s == null) continue;
                foreach (var part in s.AsText().Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)) res.Add(part);
            }
            return res;
        }

        /// <summary>
        /// startup_nodes: structure not validated in the original. "ip:port" strings are accepted (also several per string)
        /// and [ip, port] pairs.
        /// </summary>
        public static IList<string> ParseNodeList(BValue v)
        {
            var res = new List<string>();
            var l = v as BList;
            if (l == null) return res;
            foreach (var i in l.Items)
            {
                var s = i as BString;
                if (s != null)
                {
                    foreach (var part in s.AsText().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)) res.Add(part);
                    continue;
                }
                var pair = i as BList;
                if (pair != null && pair.Count == 2)
                {
                    var host = pair[0] as BString;
                    var port = pair[1] as BInt;
                    if (host != null && port != null) res.Add(host.AsText() + ":" + port.Value);
                }
            }
            return res;
        }
    }
}
