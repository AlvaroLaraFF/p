using System;
using System.Collections.Generic;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.Transport
{
    /// <summary>Binary format of the transport file.</summary>
    public enum TransportFormat
    {
        /// <summary>Plain bencode torrent (starts with 'd').</summary>
        PlainBencode,
        /// <summary>Header 01 02 03 04 11 02 'e' '.' + AES with KeyTdefV3.</summary>
        LegacyAesV3,
        /// <summary>Header 01 02 03 04 + AES with "tslive_key".</summary>
        LegacyAesTsLive,
        /// <summary>Header 11 02 'e' '.' + AES with KeyTdefV2.</summary>
        LegacyAesV2,
        /// <summary>"AceStreamTransport" + version 1 (BE16) + AES.</summary>
        AceStreamTransportV1,
        /// <summary>"AceStreamTransport" + version 2 (BE16) + AES.</summary>
        AceStreamTransportV2
    }

    /// <summary>A quality of a multi-quality transport.</summary>
    public sealed class TransportQuality
    {
        public string Name { get; set; }
        /// <summary>Nested transport already loaded (null if the item did not carry interpretable 'data').</summary>
        public TransportFile Data { get; set; }
        public BValue RawItem { get; set; }
    }

    /// <summary>
    /// Transport file already decrypted and analyzed. RawBytes are the bytes as they circulate:
    /// the content_id is the SHA-1 of those bytes and they are never re-serialized.
    /// </summary>
    public sealed class TransportFile
    {
        public TransportFormat Format { get; internal set; }
        public byte[] RawBytes { get; internal set; }
        /// <summary>Plain bencode (after decrypting).</summary>
        public byte[] Payload { get; internal set; }
        public BDict Root { get; internal set; }
        /// <summary>IV it was decrypted with (Auto if the format was not encrypted).</summary>
        public IvMode IvUsed { get; internal set; }
        public bool PaddingWasValid { get; internal set; }

        /// <summary>"torrent", "bt", "hls", "dash", "wrapper" or "multi".</summary>
        public string Type { get; internal set; }
        public TorrentMeta Torrent { get; internal set; }
        public LiveDescriptor Live { get; internal set; }
        public IList<TransportQuality> Qualities { get; internal set; }

        public bool IsNewFormat
        {
            get { return Format == TransportFormat.AceStreamTransportV1 || Format == TransportFormat.AceStreamTransportV2; }
        }

        public bool IsMulti { get { return Type == "multi"; } }

        public bool IsLive
        {
            get
            {
                if (IsMulti) return Qualities != null && Qualities.Count > 0 && Qualities[0].Data != null && Qualities[0].Data.IsLive;
                if (Torrent != null) return Torrent.IsLive;
                return IsNewFormat;   // new format: bt/hls/dash/wrapper count as live
            }
        }

        public string Name { get; internal set; }

        /// <summary>SHA-1 hex of RawBytes: content_id. The bytes are hashed as they are, without re-serializing.</summary>
        public string ContentId { get { return Hex.ToHex(HashUtil.Sha1(RawBytes)); } }

        /// <summary>
        /// infohash (20 bytes): torrent = SHA1 of the raw info span; live BT descriptor =
        /// SHA1(bencode(hash_items)). Null for hls/dash/wrapper/multi (formula UNKNOWN).
        /// </summary>
        public byte[] InfoHash
        {
            get
            {
                if (Torrent != null) return Torrent.InfoHash;
                if (Live != null) return Live.InfoHash;
                return null;
            }
        }

        public string InfoHashHex { get { var h = InfoHash; return h == null ? null : Hex.ToHex(h); } }

        /// <summary>For multi-quality: returns quality i as an independent transport.</summary>
        public TransportFile SelectQuality(int index)
        {
            if (!IsMulti) throw new InvalidOperationException("not a multi-quality transport");
            if (index < 0 || index >= Qualities.Count) throw new ArgumentOutOfRangeException("index");
            var q = Qualities[index].Data;
            if (q == null) throw new TransportException("quality " + index + " does not contain an interpretable transport");
            return q;
        }
    }
}
