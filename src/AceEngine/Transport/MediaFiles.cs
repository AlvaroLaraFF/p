using System;
using System.Collections.Generic;
using AceEngine.Bencode;
using AceEngine.Util;

namespace AceEngine.Transport
{
    /// <summary>Element of the list of playable files (the original engine's MediaFile).</summary>
    public sealed class MediaFile
    {
        public string Filename { get; set; }
        public int Index { get; set; }
        public string Infohash { get; set; }
        public string Mime { get; set; }
        public long Size { get; set; }
        public string TransportType { get; set; }
        /// <summary>"live" or "vod".</summary>
        public string Type { get; set; }
        /// <summary>Position of the file in the torrent (BT VOD only); useful for the piece selector.</summary>
        public long Offset { get; set; }
    }

    /// <summary>Parameters of get_media_files: mode=full|short, expand_wrapper, dump_transport_file.</summary>
    public sealed class MediaFilesOptions
    {
        /// <summary>mode=full (the default, like the original engine): infohash, type, mime, transport_type and size in each item. false = mode=short: only filename and index.</summary>
        public bool Full = true;
        public bool ExpandWrapper = true;
        /// <summary>dump_transport_file=1: returns transport_file_data (base64 of the transport bytes).</summary>
        public bool DumpTransportFile = false;
        /// <summary>use_local_cache_only: resolves only from the local cache ('no_transport_file_in_local_cache' if it is not there).</summary>
        public bool UseLocalCacheOnly = false;
    }

    public sealed class MediaFilesResult
    {
        public IList<MediaFile> Files { get; set; }
        public string Infohash { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string TransportType { get; set; }
        /// <summary>base64(tfile.save()) if dump_transport_file was requested.</summary>
        public string TransportFileData { get; set; }
        /// <summary>wrapper_data / wrapper_metadata (title, mime, type) of a wrapper descriptor (INFERRED).</summary>
        public IDictionary<string, object> WrapperData { get; set; }
        /// <summary>true if mode=full was requested.</summary>
        public bool Full { get; set; }

        /// <summary>JSON with the field names of MediaFilesResponse.</summary>
        public string ToJson(bool indent = false)
        {
            var files = new List<object>();
            foreach (var f in Files)
            {
                var o = new Dictionary<string, object>();
                if (Full)
                {
                    o["infohash"] = f.Infohash;
                    o["type"] = f.Type;
                    o["filename"] = f.Filename;
                    o["mime"] = f.Mime;
                    o["transport_type"] = f.TransportType;
                    o["size"] = f.Size;
                    o["index"] = f.Index;
                }
                else
                {
                    o["filename"] = f.Filename;
                    o["index"] = f.Index;
                }
                files.Add(o);
            }
            var r = new Dictionary<string, object>();
            r["files"] = files;
            r["infohash"] = Infohash;
            r["type"] = Type;
            r["transport_type"] = TransportType;
            r["name"] = Name;
            if (TransportFileData != null) r["transport_file_data"] = TransportFileData;
            if (WrapperData != null) r["wrapper_data"] = WrapperData;
            return MiniJson.Stringify(r, indent);
        }
    }

    /// <summary>Equivalent of get_media_files for an already loaded transport.</summary>
    public static class MediaFiles
    {
        public const string HlsMime = "application/vnd.apple.mpegurl";

        public static MediaFilesResult Get(TransportFile tf, bool expandWrapper = true)
        {
            return Get(tf, new MediaFilesOptions { ExpandWrapper = expandWrapper });
        }

        public static MediaFilesResult Get(TransportFile tf, MediaFilesOptions options)
        {
            if (tf == null) throw new ArgumentNullException("tf");
            if (options == null) options = new MediaFilesOptions();
            if (tf.IsMulti) return GetMulti(tf, options);

            string infohash = tf.InfoHashHex ?? "";
            var files = new List<MediaFile>();
            string contentType;
            string transportType;
            IList<TorrentFileEntry> videofiles;
            IDictionary<string, object> wrapper = null;

            if (tf.IsNewFormat)
            {
                contentType = "live";
                transportType = tf.Type;     // bt | hls | dash | wrapper
                videofiles = new List<TorrentFileEntry> { new TorrentFileEntry { Path = tf.Name, Index = 0 } };
                if (options.ExpandWrapper && transportType == "wrapper") wrapper = WrapperMetadata(tf);
            }
            else if (tf.Torrent.IsLive)
            {
                contentType = "live";
                transportType = "bt";
                videofiles = tf.Torrent.GetFilesWithIndexes(null);
                if (videofiles.Count > 1) videofiles = new List<TorrentFileEntry> { videofiles[0] };   // in live, a single file of index 0
                if (videofiles.Count == 0) videofiles = new List<TorrentFileEntry> { new TorrentFileEntry { Path = tf.Name, Index = 0 } };
            }
            else
            {
                contentType = "vod";
                transportType = "bt";
                videofiles = tf.Torrent.GetFilesWithIndexes(MimeTypes.VideoExtensions);
            }

            foreach (var f in videofiles)
            {
                var mf = new MediaFile
                {
                    Filename = f.Path, Index = f.Index, Infohash = infohash, TransportType = transportType, Type = contentType
                };
                if (contentType == "live")
                {
                    object wm;
                    mf.Mime = wrapper != null && wrapper.TryGetValue("mime", out wm) && wm is string && ((string)wm).Length > 0 ? (string)wm
                        : (transportType == "hls" || transportType == "wrapper") ? HlsMime : "video/mp2t";
                    mf.Size = 0;
                }
                else
                {
                    mf.Mime = MimeTypes.GetMimeType(f.Path);
                    mf.Size = f.Length;
                    mf.Offset = f.Offset;
                }
                files.Add(mf);
            }
            var res = new MediaFilesResult
            {
                Files = files, Infohash = infohash, Name = tf.Name, Type = contentType, TransportType = transportType,
                WrapperData = wrapper, Full = options.Full
            };
            if (options.DumpTransportFile) res.TransportFileData = Convert.ToBase64String(tf.RawBytes);
            return res;
        }

        /// <summary>
        /// Multi-quality transport: the original documentation only covers the non-multi case. INFERRED: one item per quality (the quality name as filename,
        /// index = position in 'qualities', own infohash) and type='live' if the qualities are live streams.
        /// </summary>
        private static MediaFilesResult GetMulti(TransportFile tf, MediaFilesOptions options)
        {
            var files = new List<MediaFile>();
            string type = "vod";
            for (int i = 0; i < tf.Qualities.Count; i++)
            {
                var q = tf.Qualities[i];
                if (q.Data == null) continue;
                var inner = Get(q.Data, new MediaFilesOptions { Full = options.Full, ExpandWrapper = options.ExpandWrapper });
                type = inner.Type;
                var f0 = inner.Files.Count > 0 ? inner.Files[0] : new MediaFile { Filename = q.Name, Type = inner.Type, TransportType = inner.TransportType, Mime = "video/mp2t" };
                files.Add(new MediaFile
                {
                    Filename = string.IsNullOrEmpty(q.Name) ? f0.Filename : q.Name, Index = i, Infohash = inner.Infohash, Mime = f0.Mime, Size = f0.Size,
                    TransportType = inner.TransportType, Type = inner.Type
                });
            }
            var res = new MediaFilesResult
            {
                Files = files, Infohash = files.Count > 0 ? files[0].Infohash : "", Name = tf.Name, Type = type,
                TransportType = files.Count > 0 ? files[0].TransportType : "bt", Full = options.Full
            };
            if (options.DumpTransportFile) res.TransportFileData = Convert.ToBase64String(tf.RawBytes);
            return res;
        }

        /// <summary>wrapper_metadata (title, mime, type): keys INFERRED from the descriptor dictionary.</summary>
        private static IDictionary<string, object> WrapperMetadata(TransportFile tf)
        {
            var d = new Dictionary<string, object>();
            var root = tf.Root;
            if (root == null) return d;
            string title = root.GetString("title") ?? root.GetString("name");
            if (title != null) d["title"] = title;
            string mime = root.GetString("mime") ?? root.GetString("mimetype");
            if (mime != null) d["mime"] = mime;
            string type = root.GetString("wrapper_type") ?? root.GetString("type");
            if (type != null) d["type"] = type;
            return d;
        }
    }
}
