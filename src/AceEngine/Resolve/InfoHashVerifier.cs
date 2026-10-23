using System;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>
    /// Checks that the transport file a peer delivers is the one of the requested infohash. Unlike the content_id (an id that
    /// the Ace server assigns and that cannot be computed), the infohash IS derived from the descriptor:
    ///  - Ace live stream: SHA-1(bencode([[k, v] ...])) over the hash_fields (name, authmethod, PUBLIC KEY, piece_length, chunk_length, bitrate...);
    ///  - torrent (VOD): SHA-1 of the raw span of the 'info' dictionary.
    /// A peer cannot invent a descriptor with another broadcaster key or another piece size that ends up in the same infohash: the check is
    /// cryptographic and no quorum is needed. What the hash does NOT cover (trackers, meta_trackers, startup_nodes, title...) is supplied by the peer; the pieces of the
    /// live stream remain authenticated by the public key, which is covered.
    /// </summary>
    public static class InfoHashVerifier
    {
        /// <summary>
        /// Loads 'data' and returns the transport whose infohash is 'infoHash', or null (with the cause in 'reason'). For a multi-quality it returns the quality that
        /// matches as a file of its own (its bytes can be loaded again and stored as they are); the whole multi-quality is not accepted if none matches.
        /// It does not throw on malformed data: what arrives from a peer can only end in a rejection.
        /// </summary>
        public static TransportFile Match(byte[] data, byte[] infoHash, bool strict, out string reason)
        {
            reason = null;
            if (infoHash == null || infoHash.Length != 20) { reason = "invalid requested infohash"; return null; }
            if (data == null || data.Length == 0) { reason = "empty file"; return null; }
            var options = new TransportLoadOptions { Strict = strict };
            TransportFile tf;
            try { tf = TransportLoader.Load(data, options); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                reason = "cannot be interpreted: " + ex.Message;
                return null;
            }
            if (tf.IsMulti)
            {
                if (tf.Qualities != null)
                {
                    foreach (var q in tf.Qualities)
                    {
                        var d = q.Data;
                        if (d == null || d.InfoHash == null || !HashUtil.ConstantTimeEquals(d.InfoHash, infoHash)) continue;
                        // the quality must be loadable on its own (if its 'data' was an already decoded dictionary, its RawBytes are not a file)
                        try
                        {
                            var alone = TransportLoader.Load(d.RawBytes, options);
                            if (alone.InfoHash != null && HashUtil.ConstantTimeEquals(alone.InfoHash, infoHash)) return alone;
                        }
                        catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
                    }
                }
                reason = "none of the qualities of the multi-quality has that infohash";
                return null;
            }
            var ih = tf.InfoHash;
            if (ih == null) { reason = "a '" + tf.Type + "' transport has no infohash to check"; return null; }
            if (!HashUtil.ConstantTimeEquals(ih, infoHash)) { reason = "its infohash is " + Hex.ToHex(ih); return null; }
            return tf;
        }

        /// <summary>true if 'data' is a transport file with that infohash.</summary>
        public static bool Matches(byte[] data, byte[] infoHash, bool strict)
        {
            string why;
            return Match(data, infoHash, strict, out why) != null;
        }
    }
}
