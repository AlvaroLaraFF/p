using System;
using System.Collections.Generic;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.Transport
{
    public sealed class TorrentFileEntry
    {
        /// <summary>Position in info['files'] (0 for a single file); it is the index of get_media_files.</summary>
        public int Index { get; set; }
        public string Path { get; set; }
        public long Length { get; set; }
        /// <summary>Offset of the first byte of the file within the total content of the torrent.</summary>
        public long Offset { get; set; }
    }

    /// <summary>
    /// Ace torrent (TorrentDef): classic dictionary with extensions (live, x-ts-properties...).
    /// The infohash is computed over the original byte span of info, not by re-serializing.
    /// </summary>
    public sealed class TorrentMeta
    {
        /// <summary>Limits of a torrent that arrives from the network (a hostile descriptor cannot make us reserve gigabytes or loop forever).</summary>
        public const long MinPieceLength = 1024, MaxPieceLength = 32L * 1024 * 1024;
        public const int MaxPieces = 2000000, MaxFiles = 20000, MaxPathParts = 32, MaxPathLength = 1024, MaxNameLength = 1024;
        public const long MaxTotalLength = 1L << 50;

        public BDict Root { get; private set; }     // null if only info is available (magnet)
        public BDict Info { get; private set; }
        public byte[] InfoRaw { get; private set; }
        public byte[] InfoHash { get; private set; }
        public string InfoHashHex { get { return Hex.ToHex(InfoHash); } }

        public string Name { get; private set; }
        public long PieceLength { get; private set; }
        public byte[] Pieces { get; private set; }
        public int PieceCount { get; private set; }
        public long TotalLength { get; private set; }
        public bool IsMultiFile { get; private set; }
        public IList<TorrentFileEntry> Files { get; private set; }
        public bool IsLive { get; private set; }
        public bool IsMerkle { get; private set; }
        public BDict LiveInfo { get; private set; }
        public BDict XTsProperties { get; private set; }

        public static TorrentMeta FromRoot(BDict root)
        {
            if (root == null) throw new ArgumentNullException("root");
            var info = root.Get("info") as BDict;
            if (info == null) throw new TransportException("the torrent has no 'info'");
            return new TorrentMeta(root, info);
        }

        /// <summary>Builds from the bytes of 'info' (obtained through ut_metadata).</summary>
        public static TorrentMeta FromInfoBytes(byte[] infoBytes)
        {
            BValue v;
            try { v = BencodeParser.Parse(infoBytes, new BencodeParseOptions { AllowTrailingData = false }); }
            catch (BencodeException ex) { throw new TransportException("malformed 'info' metadata: " + ex.Message, ex); }
            var info = v as BDict;
            if (info == null) throw new TransportException("the 'info' metadata is not a dictionary");
            return new TorrentMeta(null, info);
        }

        private TorrentMeta(BDict root, BDict info)
        {
            Root = root;
            Info = info;
            InfoRaw = info.GetRawBytes();
            InfoHash = HashUtil.Sha1(InfoRaw);

            Name = Text(info, "name.utf-8") ?? Text(info, "name") ?? "";
            LiveInfo = info.Get("live") as BDict;
            IsLive = info.ContainsKey("live");
            IsMerkle = info.ContainsKey("root hash");
            if (root != null) XTsProperties = root.Get("x-ts-properties") as BDict;

            if (Name.Length > MaxNameLength) throw new TransportException("'name' too long");
            Name = Sanitize(Name);
            var pl = info.GetInt("piece length");
            if (pl.HasValue) PieceLength = pl.Value;
            Pieces = info.GetBytes("pieces");
            if (Pieces != null)
            {
                if (Pieces.Length % 20 != 0) throw new TransportException("'pieces' is not a multiple of 20 bytes");
                PieceCount = Pieces.Length / 20;
                if (PieceCount > MaxPieces) throw new TransportException("too many pieces (" + PieceCount + ")");
            }

            Files = new List<TorrentFileEntry>();
            var filesList = info.Get("files") as BList;
            var lengthVal = info.GetInt("length");
            if (filesList != null && lengthVal.HasValue)
                throw new TransportException("info has both 'files' and 'length'");
            if (filesList != null)
            {
                IsMultiFile = true;
                long off = 0;
                if (filesList.Count > MaxFiles) throw new TransportException("too many files (" + filesList.Count + ")");
                for (int i = 0; i < filesList.Count; i++)
                {
                    var fd = filesList[i] as BDict;
                    if (fd == null) throw new TransportException("'files' entry is not a dictionary");
                    var len = fd.GetInt("length");
                    if (!len.HasValue || len.Value < 0) throw new TransportException("file without a valid 'length'");
                    var path = ReadPath(fd);
                    Files.Add(new TorrentFileEntry { Index = i, Path = path, Length = len.Value, Offset = off });
                    if (len.Value > MaxTotalLength || off > MaxTotalLength - len.Value) throw new TransportException("total length out of range");
                    off += len.Value;
                }
                TotalLength = off;
            }
            else if (lengthVal.HasValue)
            {
                if (lengthVal.Value < 0) throw new TransportException("negative 'length'");
                if (lengthVal.Value > MaxTotalLength) throw new TransportException("total length out of range");
                TotalLength = lengthVal.Value;
                Files.Add(new TorrentFileEntry { Index = 0, Path = Name, Length = lengthVal.Value, Offset = 0 });
            }
            else if (!IsLive)
            {
                throw new TransportException("info without 'length' or 'files'");
            }

            if (!IsLive)
            {
                // Validations from the reference BitTorrent btformats.py.
                if (PieceLength <= 0) throw new TransportException("invalid 'piece length'");
                if (PieceLength < MinPieceLength || PieceLength > MaxPieceLength) throw new TransportException("'piece length' out of range (" + PieceLength + ")");
                if (Pieces == null && !IsMerkle) throw new TransportException("info without 'pieces'");
                if (Pieces != null)
                {
                    long expected = PieceLength == 0 ? 0 : (TotalLength + PieceLength - 1) / PieceLength;
                    if (PieceCount != expected)
                        throw new TransportException("inconsistent piece count: there are " + PieceCount + " hashes and the length implies " + expected);
                }
            }
        }

        private static string ReadPath(BDict fd)
        {
            var list = fd.Get("path.utf-8") as BList ?? fd.Get("path") as BList;
            if (list == null || list.Count == 0) throw new TransportException("file without 'path'");
            var parts = new List<string>();
            if (list.Count > MaxPathParts) throw new TransportException("'path' with too many components");
            foreach (var p in list.Items)
            {
                var s = p as BString;
                if (s == null) throw new TransportException("'path' component is not a string");
                string part = Sanitize(s.AsText());
                // Like any BitTorrent client: no ".." components or separators inside a component. The engine does not write files
                // (everything goes to RAM), but the name reaches the UI and the logs and must never be usable as a path.
                if (part == ".." || part == "." || part.IndexOf('/') >= 0 || part.IndexOf('\\') >= 0) throw new TransportException("invalid 'path' component");
                parts.Add(part);
            }
            string joined = string.Join("/", parts.ToArray());
            if (joined.Length > MaxPathLength) throw new TransportException("'path' too long");
            return joined;
        }

        /// <summary>Removes control characters (and the Unicode text-direction ones used to disguise names).</summary>
        public static string Sanitize(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < ' ' || c == 0x7f || (c >= 0x80 && c < 0xa0) || c == '\u202e' || c == '\u202d' || c == '\u2066' || c == '\u2067' || c == '\u2068' || c == '\u2069' || c == '\u200e' || c == '\u200f') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Text(BDict d, string key)
        {
            var s = d.Get(key) as BString;
            return s == null ? null : s.AsText();
        }

        /// <summary>SHA-1 of piece i (20 bytes).</summary>
        public byte[] GetPieceHash(int index)
        {
            if (Pieces == null) throw new InvalidOperationException("the torrent has no piece hashes");
            if (index < 0 || index >= PieceCount) throw new ArgumentOutOfRangeException("index");
            var h = new byte[20];
            Buffer.BlockCopy(Pieces, index * 20, h, 0, 20);
            return h;
        }

        /// <summary>Actual length of piece i (the last one may be shorter).</summary>
        public int GetPieceLength(int index)
        {
            if (index < 0 || index >= PieceCount) throw new ArgumentOutOfRangeException("index");
            long start = (long)index * PieceLength;
            return (int)Math.Min(PieceLength, TotalLength - start);
        }

        /// <summary>
        /// Infohash that Ace would give by re-serializing 'info' with its key order. For diagnostics only:
        /// if it differs from InfoHash, the key order of the file was not Ace's.
        /// </summary>
        public byte[] ComputeAceReserializedInfoHash()
        {
            var bytes = BencodeWriter.Encode(Info, new BencodeWriteOptions { Order = KeyOrder.Ace, UseRawBytes = false });
            return HashUtil.Sha1(bytes);
        }

        // ---- Top-level fields ----

        public string Announce
        {
            get { return Root == null ? null : Text(Root, "announce"); }
        }

        /// <summary>Tracker tiers: announce-list if it exists; otherwise [[announce]].</summary>
        public IList<IList<string>> GetTrackerTiers()
        {
            var tiers = new List<IList<string>>();
            if (Root == null) return tiers;
            var al = Root.Get("announce-list") as BList;
            if (al != null)
            {
                foreach (var t in al.Items)
                {
                    var tl = t as BList;
                    if (tl == null) continue;
                    var tier = new List<string>();
                    foreach (var u in tl.Items)
                    {
                        var us = u as BString;
                        if (us != null && us.Count > 0) tier.Add(us.AsText());
                    }
                    if (tier.Count > 0) tiers.Add(tier);
                }
            }
            if (tiers.Count == 0 && !string.IsNullOrEmpty(Announce))
                tiers.Add(new List<string> { Announce });
            return tiers;
        }

        /// <summary>DHT nodes of the torrent: nodes = [[host, port], ...].</summary>
        public IList<KeyValuePair<string, int>> GetDhtNodes()
        {
            var res = new List<KeyValuePair<string, int>>();
            if (Root == null) return res;
            var nodes = Root.Get("nodes") as BList;
            if (nodes == null) return res;
            foreach (var n in nodes.Items)
            {
                var pair = n as BList;
                if (pair == null || pair.Count != 2) continue;
                var host = pair[0] as BString;
                var port = pair[1] as BInt;
                if (host == null || port == null) continue;
                res.Add(new KeyValuePair<string, int>(host.AsText(), (int)port.Value));
            }
            return res;
        }

        public IList<string> GetUrlList()
        {
            return StringOrList(Root == null ? null : Root.Get("url-list"));
        }

        public IList<string> GetHttpSeeds()
        {
            return StringOrList(Root == null ? null : Root.Get("httpseeds"));
        }

        private static IList<string> StringOrList(BValue v)
        {
            var res = new List<string>();
            var s = v as BString;
            if (s != null) { res.Add(s.AsText()); return res; }
            var l = v as BList;
            if (l != null)
                foreach (var i in l.Items)
                {
                    var si = i as BString;
                    if (si != null) res.Add(si.AsText());
                }
            return res;
        }

        /// <summary>
        /// get_files_with_indexes(exts) of the original: indices = position in info['files'] (the filtered ones are counted too).
        /// exts=null returns all.
        /// </summary>
        public IList<TorrentFileEntry> GetFilesWithIndexes(IEnumerable<string> exts)
        {
            var res = new List<TorrentFileEntry>();
            HashSet<string> set = null;
            if (exts != null) set = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Files)
            {
                if (set != null && !set.Contains(MimeTypes.GetExtension(f.Path))) continue;
                res.Add(f);
            }
            return res;
        }
    }
}
