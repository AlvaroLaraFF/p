using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.Transport
{
    public sealed class TransportLoadOptions
    {
        /// <summary>
        /// IV for the old formats (torrents). With no real files of this format: it is assumed to be the same as that of AceStreamTransport, confirmed
        /// with real files (SHA1(MD5(key))[:16]; all transport files call m2_AES_decrypt with init_iv=True).
        /// Auto tries Sha1OfMd5Key, Zero and Sha1OfKey in this order.
        /// </summary>
        public IvMode LegacyIv = IvMode.Auto;

        /// <summary>
        /// IV for AceStreamTransport. CONFIRMED with real files from the Ace network (16-10-2026): SHA1(MD5(key))[0:16]
        /// (the other documented alternative; the SHA1(key)[0:16] given by the other documentation is incorrect).
        /// Auto tries Sha1OfMd5Key, Sha1OfKey and Zero in this order.
        /// </summary>
        public IvMode TransportIv = IvMode.Auto;

        /// <summary>Overridable keys in case a real file does not decrypt with the documented ones.</summary>
        public byte[] KeyTdefV3 = AceKeys.KeyTdefV3;
        public byte[] KeyTsLive = AceKeys.KeyTsLive;
        public byte[] KeyTdefV2 = AceKeys.KeyTdefV2;
        public byte[] KeyTransportV1 = AceKeys.KeyTransportV1;
        public byte[] KeyTransportV2 = AceKeys.KeyTransportV2;

        /// <summary>
        /// Strict validation (for what arrives from the network): the AES padding must be valid PKCS#7, the bencode is read entirely with no extra bytes and the
        /// nested qualities are capped. The original engine is tolerant; the three real files from the network meet all of this.
        /// </summary>
        public bool Strict = false;
        /// <summary>Maximum depth of transports inside transports (multi-quality) and maximum number of qualities.</summary>
        public int MaxNesting = 3;
        public int MaxQualities = 32;

        public static readonly TransportLoadOptions Default = new TransportLoadOptions();
    }

    /// <summary>
    /// Reading of transport files: plain torrent, the three old encrypted variants and
    /// AceStreamTransport v1/v2. Order of checks: AceStreamTransport, 8-byte header, 01020304, 11026e2e,
    /// plain bencode.
    /// </summary>
    public static class TransportLoader
    {
        private const string Tag = "transport";

        public static TransportFile LoadFile(string path, TransportLoadOptions options = null)
        {
            return Load(File.ReadAllBytes(path), options);
        }

        [ThreadStatic] private static int _depth;

        public static TransportFile Load(byte[] data, TransportLoadOptions options = null)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (options == null) options = TransportLoadOptions.Default;
            if (_depth >= options.MaxNesting) throw new TransportException("transports nested too deeply");
            _depth++;
            try { return LoadCore(data, options); }
            finally { _depth--; }
        }

        private static TransportFile LoadCore(byte[] data, TransportLoadOptions options)
        {
            if (data.Length == 0) throw new TransportException("malformed transport file: empty");

            if (StartsWith(data, AceKeys.MagicAceStreamTransport))
                return LoadAceStreamTransport(data, options);

            TransportFormat format;
            byte[] key = null;
            byte[] body = data;
            if (StartsWith(data, AceKeys.HeaderLegacyV3))
            {
                format = TransportFormat.LegacyAesV3; key = options.KeyTdefV3; body = Slice(data, 8);
            }
            else if (StartsWith(data, AceKeys.HeaderTsLive))
            {
                format = TransportFormat.LegacyAesTsLive; key = options.KeyTsLive; body = Slice(data, 4);
            }
            else if (StartsWith(data, AceKeys.HeaderLegacyV2))
            {
                format = TransportFormat.LegacyAesV2; key = options.KeyTdefV2; body = Slice(data, 4);
            }
            else if (data[0] == (byte)'d')
            {
                format = TransportFormat.PlainBencode;
            }
            else
            {
                throw new TransportException("not a transport file: unknown header 0x" + data[0].ToString("x2"));
            }

            var tf = new TransportFile { Format = format, RawBytes = data, IvUsed = IvMode.Auto, PaddingWasValid = true };
            if (key == null)
            {
                tf.Payload = body;
                tf.Root = ParseRoot(body, options.Strict, "malformed transport file");
            }
            else
            {
                IvMode used;
                bool padOk;
                BDict root;
                tf.Payload = DecryptAndParse(body, key, options.LegacyIv,
                    new[] { IvMode.Sha1OfMd5Key, IvMode.Zero, IvMode.Sha1OfKey }, options.Strict, out used, out padOk, out root);
                tf.Root = root;
                tf.IvUsed = used;
                tf.PaddingWasValid = padOk;
                AceLog.Info(Tag, "format " + format + " decrypted with IV=" + used + (padOk ? "" : " (padding not PKCS#7)"));
            }
            FillLegacy(tf, options);
            return tf;
        }

        private static TransportFile LoadAceStreamTransport(byte[] data, TransportLoadOptions options)
        {
            if (data.Length < 20) throw new TransportException("malformed transport file: fewer than 20 bytes");
            if (options.Strict && (data.Length - 20) % 16 != 0) throw new TransportException("malformed transport file: the encrypted body is not a multiple of 16");
            int version = (data[18] << 8) | data[19];
            byte[] key;
            TransportFormat format;
            if (version == 1) { key = options.KeyTransportV1; format = TransportFormat.AceStreamTransportV1; }
            else if (version == 2) { key = options.KeyTransportV2; format = TransportFormat.AceStreamTransportV2; }
            else throw new TransportException("unknown transport file version: " + version);

            var body = Slice(data, 20);
            IvMode used;
            bool padOk;
            BDict root;
            var payload = DecryptAndParse(body, key, options.TransportIv,
                new[] { IvMode.Sha1OfMd5Key, IvMode.Sha1OfKey, IvMode.Zero }, options.Strict, out used, out padOk, out root);
            AceLog.Info(Tag, "AceStreamTransport v" + version + " decrypted with IV=" + used + (padOk ? "" : " (padding not PKCS#7)"));

            var tf = new TransportFile
            {
                Format = format, RawBytes = data, Payload = payload, Root = root, IvUsed = used, PaddingWasValid = padOk
            };
            FillNewFormat(tf, options);
            return tf;
        }

        // ---------- Interpretation ----------

        private static void FillLegacy(TransportFile tf, TransportLoadOptions options)
        {
            var root = tf.Root;
            if (root.ContainsKey("info"))
            {
                tf.Torrent = TorrentMeta.FromRoot(root);
                tf.Type = "torrent";
                tf.Name = tf.Torrent.Name;
                var ace = tf.Torrent.ComputeAceReserializedInfoHash();
                if (!HashUtil.ConstantTimeEquals(ace, tf.Torrent.InfoHash))
                    AceLog.Warn(Tag, "the infohash of the raw span (" + tf.Torrent.InfoHashHex + ") differs from the one re-serialized with Ace's order ("
                        + Hex.ToHex(ace) + "); the raw one is used");
            }
            else if (root.ContainsKey("qualities"))
            {
                tf.Type = "multi";
                tf.Qualities = ReadQualities(root.Get("qualities"), options);
                tf.Name = root.GetString("name") ?? (tf.Qualities.Count > 0 && tf.Qualities[0].Data != null ? tf.Qualities[0].Data.Name : "");
            }
            else
            {
                throw new TransportException("not a transport file: neither 'info' nor 'qualities'");
            }
        }

        private static void FillNewFormat(TransportFile tf, TransportLoadOptions options)
        {
            var d = tf.Root;
            tf.Name = d.GetString("name") ?? "";
            if (d.ContainsKey("qualities"))
            {
                tf.Type = "multi";
                tf.Qualities = ReadQualities(d.Get("qualities"), options);
                return;
            }
            var type = d.GetString("type");
            if (type == "hls" || type == "dash" || type == "wrapper")
            {
                // HLS/DASH transports over P2P. The descriptor hash is UNKNOWN; it is not computed.
                tf.Type = type;
                AceLog.Info(Tag, "descriptor " + type + ": unknown infohash");
                return;
            }
            // Classic BT live stream. 'type' absent or 'bt'.
            tf.Type = "bt";
            tf.Live = LiveDescriptor.FromMeta(d);
        }

        private static IList<TransportQuality> ReadQualities(BValue v, TransportLoadOptions options)
        {
            var res = new List<TransportQuality>();
            var l = v as BList;
            if (l == null) throw new TransportException("bad qualities type: " + (v == null ? "null" : v.GetType().Name));
            if (l.Count > options.MaxQualities) throw new TransportException("too many qualities (" + l.Count + ")");
            foreach (var item in l.Items)
            {
                var q = new TransportQuality { RawItem = item };
                var qd = item as BDict;
                if (qd != null)
                {
                    q.Name = qd.GetString("name");
                    var data = qd.Get("data");
                    var bs = data as BString;
                    try
                    {
                        if (bs != null) q.Data = Load(bs.Value, options);
                        else if (data is BDict)
                        {
                            // 'data' as an already decoded BT dictionary: it is treated as a BT descriptor.
                            q.Data = new TransportFile
                            {
                                Format = TransportFormat.AceStreamTransportV2,
                                RawBytes = data.Encode(),
                                Payload = data.Encode(),
                                Root = (BDict)data,
                                Type = "bt",
                                Name = ((BDict)data).GetString("name") ?? q.Name,
                                Live = LiveDescriptor.FromMeta((BDict)data),
                                IvUsed = IvMode.Auto,
                                PaddingWasValid = true
                            };
                        }
                    }
                    catch (TransportException ex)
                    {
                        AceLog.Warn(Tag, "quality '" + q.Name + "' not interpretable: " + ex.Message);
                    }
                }
                res.Add(q);
            }
            return res;
        }

        // ---------- Decryption ----------

        private static byte[] DecryptAndParse(byte[] body, byte[] key, IvMode requested, IvMode[] autoOrder, bool strict,
            out IvMode used, out bool paddingOk, out BDict root)
        {
            var candidates = requested == IvMode.Auto ? autoOrder : new[] { requested };
            Exception last = null;
            foreach (var iv in candidates)
            {
                try
                {
                    bool pad;
                    var plain = AceCrypto.M2AesDecrypt(body, key, iv, out pad);
                    if (strict && !pad) throw new TransportException("invalid AES padding");
                    // With a wrong IV only the first block is damaged, but the bencode is no longer valid.
                    BDict r = ParseRoot(plain, strict || requested == IvMode.Auto, "malformed transport file");
                    used = iv; paddingOk = pad; root = r;
                    return plain;
                }
                catch (TransportException ex) { last = ex; }
                catch (CryptographicException ex) { last = ex; }
            }
            throw new TransportException("bad transport file format: could not decrypt or interpret with any IV variant ("
                + (last == null ? "?" : last.Message) + ")", last);
        }

        private static BDict ParseRoot(byte[] plain, bool strict, string errorText)
        {
            BValue v;
            try
            {
                v = BencodeParser.Parse(plain, new BencodeParseOptions { AllowTrailingData = !strict });
            }
            catch (BencodeException ex)
            {
                throw new TransportException(errorText + ": " + ex.Message, ex);
            }
            var d = v as BDict;
            if (d == null) throw new TransportException(errorText + ": the root is not a dictionary");
            return d;
        }

        // ---------- Utilities ----------

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static byte[] Slice(byte[] data, int from)
        {
            var r = new byte[data.Length - from];
            Buffer.BlockCopy(data, from, r, 0, r.Length);
            return r;
        }

        // ---------- Building (for tests and tools) ----------

        /// <summary>Serializes a dictionary as a transport file of the given format (inverse of Load).</summary>
        public static byte[] Build(BDict root, TransportFormat format, IvMode iv = IvMode.Zero, TransportLoadOptions options = null)
        {
            if (options == null) options = TransportLoadOptions.Default;
            var plain = BencodeWriter.Encode(root, new BencodeWriteOptions { UseRawBytes = true, Order = KeyOrder.Ace });
            switch (format)
            {
                case TransportFormat.PlainBencode:
                    return plain;
                case TransportFormat.LegacyAesV3:
                    return Concat(AceKeys.HeaderLegacyV3, AceCrypto.M2AesEncrypt(plain, options.KeyTdefV3, iv));
                case TransportFormat.LegacyAesTsLive:
                    return Concat(AceKeys.HeaderTsLive, AceCrypto.M2AesEncrypt(plain, options.KeyTsLive, iv));
                case TransportFormat.LegacyAesV2:
                    return Concat(AceKeys.HeaderLegacyV2, AceCrypto.M2AesEncrypt(plain, options.KeyTdefV2, iv));
                case TransportFormat.AceStreamTransportV1:
                    return Concat(AceKeys.MagicAceStreamTransport, new byte[] { 0, 1 }, AceCrypto.M2AesEncrypt(plain, options.KeyTransportV1, iv));
                case TransportFormat.AceStreamTransportV2:
                    return Concat(AceKeys.MagicAceStreamTransport, new byte[] { 0, 2 }, AceCrypto.M2AesEncrypt(plain, options.KeyTransportV2, iv));
                default:
                    throw new ArgumentOutOfRangeException("format");
            }
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }
}
