using System;
using System.Collections.Generic;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    /// <summary>
    /// Parameters of the content_id -> transport file resolution. The P2P path and the TSService path run in
    /// parallel and the first one to deliver bytes whose SHA-1 is the content_id wins. What is INFERRED (TSService format) is configurable.
    /// </summary>
    public sealed class ResolverOptions
    {
        /// <summary>Maximum time of a complete resolution (the original waits 15 s). Zero = no limit.</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(20);

        // ---------- P2P path (MiniBitTorrent) ----------

        public bool EnableP2p = true;

        /// <summary>MiniBitTorrent peer_id: "-ST0100-" + 12 random bytes.</summary>
        public string PeerIdPrefix = "-ST0100-";

        /// <summary>MiniSwarm.MAX_CONNECTIONS.</summary>
        public int MaxConnections = 30;

        /// <summary>
        /// Connect and handshake deadlines with each peer when asking for the descriptor (zero = the engine's, 8 s and 15 s; by default 4 s and 4 s). The peers that answer do so in under 0.3 s (p90) and 1.2 s (p99):
        /// waiting 8 s for those behind NAT only wastes the connection slot (60-88 % of the attempts end in timeout).
        /// </summary>
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4), HandshakeTimeout = TimeSpan.FromSeconds(4);

        /// <summary>MiniSwarm.MAX_TIME_INACTIVE: the connection is closed after this time without messages.</summary>
        public TimeSpan InactiveTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Duration of each DHT search and wait between searches while there is no result.</summary>
        public TimeSpan DhtSearchTimeout = TimeSpan.FromSeconds(25);
        public TimeSpan DhtRetryDelay = TimeSpan.FromSeconds(3);

        /// <summary>Trackers queried in addition to the DHT for the content_id (Ace engines only announce it on the DHT).</summary>
        public IList<string> ExtraTrackers = new List<string>();

        /// <summary>
        /// Accept a transport whose SHA-1 is not the content_id if the peer answers with request_hash == content_id and the file decrypts
        /// and is interpreted. This is what Ace does (MiniSwarm.add_full_metadata) and it is necessary with real data: the content_id of a live stream is not
        /// the SHA-1 of the file the peers serve (the original documentation was wrong about this). So as not to trust a single peer:
        /// it is required that WeakQuorum distinct peers deliver exactly the same bytes, or that WeakFallbackSeconds pass since the first
        /// response (a negative value disables the deadline and always requires the quorum).
        /// </summary>
        public bool AcceptRequestHash = true;
        public int WeakQuorum = 2;

        /// <summary>
        /// Strict validation of the transport files that arrive from the network (valid PKCS#7 padding, bencode without extra bytes, ranges of the
        /// fields). The three real files from the Ace network meet it.
        /// </summary>
        public bool StrictTransport = true;

        /// <summary>
        /// Trust on first use: path of the JSON with content_id -> checksum, infohash and broadcaster key (null = memory only). With TrustEnforce,
        /// a known channel whose identity changes without a clear quorum is rejected; without it only a warning is given.
        /// </summary>
        public string TrustPath = null;
        public bool TrustEnforce = true;
        public double WeakFallbackSeconds = 4;

        /// <summary>If not null, the payloads received over P2P whose SHA-1 does not match are saved here (diagnostics).</summary>
        public string DumpDir = null;

        // ---------- Fallback path: TSService (INFERRED) ----------

        public bool EnableTsService = true;

        /// <summary>Service hosts. They are shuffled and tried in order.</summary>
        public IList<string> TsHosts = new List<string>
        {
            "http://s1.torrentstream.net", "http://s1.torrentstream.org", "http://s1.torrentstream.info",
            "http://s2.torrentstream.net", "http://s2.torrentstream.org", "http://s2.torrentstream.info",
            "http://s3.torrentstream.net", "http://s3.torrentstream.org", "http://s3.torrentstream.info",
            "http://s5.torrentstream.net"
        };

        /// <summary>Request signing secret (64 bytes; INFERRED to be the request one).</summary>
        public byte[] TsSecret = Hex.Parse(
            "b127562a240701f6203bad16822b067d20336dfd20d8340ab6b5f27028350892c915dfc783b2aeb9c95bdfc94aa0e3475d63c62aa9ef40a6d453d1f72a5409d2");

        /// <summary>Value of _v (engine version: probably "3.2.22").</summary>
        public string TsEngineVersion = "3.2.22";

        /// <summary>Additional parameters (_av, _ap, _aa, _p, _f, _n: UNKNOWN values). Empty by default.</summary>
        public IDictionary<string, string> TsExtraParams = new Dictionary<string, string>();

        /// <summary>Timeout per request and maximum number of hosts tried in a resolution.</summary>
        public TimeSpan TsRequestTimeout = TimeSpan.FromSeconds(6);
        public int TsMaxHosts = 4;

        /// <summary>Require the response signature (INFERRED: sha1(text without sig + secret)) to be correct. By default it is only logged.</summary>
        public bool TsVerifyResponseSignature = false;

        /// <summary>Method and parameter of the service.</summary>
        public string TsGetMethod = "gettorrent";

        /// <summary>
        /// Receives the result of each REAL request to a TSService host (pool thread; it must not block or throw). Used by TsServiceMonitor to know
        /// whether the service answers. Null = nobody listens.
        /// </summary>
        public Action<TsAttempt> TsObserver;

        // ---------- Cache ----------

        public TimeSpan CacheTtl = TimeSpan.FromHours(1);

        /// <summary>How long the peers that worked with a channel are remembered (PeerHintStore). Zero = forever (up to the entry cap).</summary>
        public TimeSpan HintsTtl = TimeSpan.FromHours(3);

        /// <summary>JSON file where the peer hints are saved between runs (null = memory only).</summary>
        public string HintsPath = null;
        public int CacheMaxEntries = 256;
        public long CacheMaxBytes = 2L * 1024 * 1024;

        /// <summary>Path of the persistent cache JSON (null = memory only).</summary>
        public string CachePath = null;

        /// <summary>Maximum size of a transport file downloaded by URL.</summary>
        public int MaxTransportBytes = 4 * 1024 * 1024;

        /// <summary>Maximum message of a peer when asking for the transport over P2P (transport files are hundreds of bytes; a VOD .torrent, hundreds of KiB).</summary>
        public int MaxFetchMessageBytes = 1024 * 1024;
    }
}
