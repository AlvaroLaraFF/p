using System;
using System.Collections.Generic;
using AceEngine.Live;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Streaming;

namespace AceEngine.P2P
{
    /// <summary>
    /// Engine parameters. Everything inferred or unknown about the protocol is configurable here
    /// so it can be tuned when testing against the real network. The defaults follow what was confirmed about Ace
    /// or, where nothing is known (window and prebuffer sizes), Tribler-style values.
    /// </summary>
    public sealed class EngineOptions
    {
        // ---------- Identity on the wire ----------

        /// <summary>Handshake reserved. Ace: 00 00 00 00 00 30 00 00 (LTEP 0x10 + Merkle-Tribler 0x20; no DHT or Fast). 0x10 alone is also valid.</summary>
        public byte[] HandshakeReserved = { 0, 0, 0, 0, 0, 0x30, 0, 0 };

        /// <summary>peer_id prefix (6 characters). Ace uses "R30---" followed by "---" and 11 random characters.</summary>
        public string PeerIdPrefix = "R30------";

        /// <summary>Key 'v' of the extended handshake (version_short with '-' replaced by ' ').</summary>
        public string ExtendedClientName = "ACEStream 2.0";

        /// <summary>Value 'v' of the DHT messages (NS\x01\x18).</summary>
        public byte[] DhtVersion = { (byte)'N', (byte)'S', 0x01, 0x18 };

        // ---------- Network ----------

        /// <summary>Listen for incoming connections (not essential). Port 0 = dynamic.</summary>
        public bool EnableListener = true;
        /// <summary>TCP listening port. 0 = the listener tries PreferredListenPort first and, if it is busy, a random one. Non-zero = exactly that one.</summary>
        public int ListenPort = 0;

        /// <summary>
        /// Port tried first when ListenPort is 0: 8621, Ace's default port (Ace peers try it on the local network and it is the one
        /// opened on the router with UPnP). If it is busy it falls back to a random one. 0 = go straight to a random one.
        /// </summary>
        public int PreferredListenPort = 8621;

        public bool EnableDht = true;

        /// <summary>
        /// DHT queries in flight at a time (all searches together). Each search launches 8 queries in parallel (DhtClient.SearchWidth): with 50 resolutions at once
        /// that is 400. The prober only admits as many resolutions as fit here (MaxInFlight / 8), so none waits for a slot; the cap avoids saturating
        /// the UDP socket and the router's NAT table. A single search (playing) uses 8.
        /// </summary>
        public int DhtMaxInFlight = 400;

        /// <summary>
        /// JSON file where the DHT nodes that responded are saved, so as not to start from scratch (only with the 4 routers) every time the engine is created. null = not saved.
        /// Loaded when the DHT starts and saved after 90 s of running and when the engine is disposed.
        /// </summary>
        public string DhtNodesPath = null;
        public bool EnableTrackers = true;
        public bool EnablePex = true;

        /// <summary>Fixed "host:port" peers added to every swarm (debugging and tests).</summary>
        public IList<string> StaticPeers = new List<string>();

        /// <summary>DHT bootstrap nodes.</summary>
        public IList<string> DhtBootstrap = new List<string>
        {
            "router.bittorrent.com:6881", "dht.transmissionbt.com:6881", "router.utorrent.com:6881", "dht.libtorrent.org:25401"
        };

        /// <summary>Maximum connected peers (40 on the TV; the original uses 50 in VOD).</summary>
        public int MaxPeers = 40;

        /// <summary>Simultaneous outgoing handshakes (MAX_INCOMPLETE = 32).</summary>
        public int MaxPendingConnects = 32;

        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
        public TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

        /// <summary>If nothing is received in this time the peer is closed (timeout=300 s).</summary>
        public TimeSpan PeerIdleTimeout = TimeSpan.FromSeconds(300);

        /// <summary>Keepalive to all connections (120 s).</summary>
        public TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(120);

        /// <summary>Maximum message length accepted from a peer (Ace: 8 MiB; here 512 KiB: the largest legitimate message is a BITFIELD or a PONG of ~128 KiB).</summary>
        public int MaxMessageLength = 512 * 1024;

        // ---------- Download ----------

        public int BlockSize = 16384;
        public int InitialBacklog = 4;

        /// <summary>
        /// Seconds of data kept requested from each peer: backlog = 2 + BacklogSeconds*rate/block (the original uses 4, meant
        /// for downloading fast). In streaming a long queue on a slow peer holds pieces the player needs right now, so
        /// here it is smaller.
        /// </summary>
        public double BacklogSeconds = 2;
        public int MaxBacklog = 50;
        public TimeSpan SnubTime = TimeSpan.FromSeconds(30);

        /// <summary>Factor of the "mid" window relative to "high" (MU = 4).</summary>
        public int MidFactor = 4;

        /// <summary>
        /// playing_delay (min, max, offset) and buffering_delay (min, max, offset) of check_outstanding_requests.
        /// </summary>
        public double PlayingDelayMin = 5, PlayingDelayMax = 20, PlayingDelayOffset = -0.5;
        public double BufferingDelayMin = 7.5, BufferingDelayMax = 30, BufferingDelayOffset = 10;

        // ---------- Memory and windows (unspecified, Tribler-style values) ----------

        /// <summary>Engine RAM buffer for VOD: 64 MiB.</summary>
        public long MaxBufferBytes = 64L * 1024 * 1024;

        /// <summary>Bytes kept behind the playback position (re-reading headers, the player's second connection).</summary>
        public long KeepBehindBytes = 8L * 1024 * 1024;

        /// <summary>Assumed bitrate when the torrent does not say (bytes/s). Window and prebuffer sizes derive from it.</summary>
        public long AssumedBitrateBytesPerSec = 1024 * 1024;

        /// <summary>Strictly sequential "high" window, in seconds (Tribler: max(10 s, 5 pieces)).</summary>
        public double HighRangeSeconds = 10;
        public int HighRangeMinPieces = 5;
        public long HighRangeMinBytes = 4L * 1024 * 1024;

        /// <summary>
        /// Reserve inside the buffer for half-downloaded pieces, the pinned piece at the end and other readers. The download window
        /// ahead of playback is (MaxBufferBytes - KeepBehindBytes - this reserve): if it were larger, reaching the limit would
        /// evict already downloaded pieces to download others and the same thing would be downloaded over and over.
        /// </summary>
        public long BufferSlackBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Urgent pieces: the first ones of the window (from the position). If an urgent block has been requested from a peer for more than
        /// UrgentDuplicateAfterMs, another fast peer requests it too (the first to arrive wins). It prevents a slow peer from
        /// holding the piece the player needs right now (the original solves it with cancellations after a timeout of 5 s or more).
        /// </summary>
        public double UrgentSeconds = 2;
        public int UrgentMinPieces = 2, UrgentMaxPieces = 6;
        public int UrgentDuplicateAfterMs = 1000;

        /// <summary>Prebuffer before moving to Playing: player_buffer_time (5 s) x bitrate, minimum in pieces.</summary>
        public double PrebufferSeconds = 5;
        public int PrebufferMinPieces = 2;
        public long PrebufferMinBytes = 2L * 1024 * 1024;

        /// <summary>Besides the start, the end of the file is prebuffered (index of MP4/MKV containers).</summary>
        public int PrebufferTailPieces = 1;

        // ---------- Upload ----------

        /// <summary>Upload: the pieces that are in RAM are served. 0 disables upload.</summary>
        public int UploadSlots = 4;

        /// <summary>Global upload limit in bytes per second, for VOD and live together (0 = no limit).</summary>
        public long UploadMaxBytesPerSec = 0;

        /// <summary>
        /// Allows opening links that are local file paths (the CLI and tests). The TV app disables it: no string coming from
        /// the network (lists, links) may make the engine read a file of the device.
        /// </summary>
        public bool AllowLocalFileLinks = true;

        private AceEngine.Util.RateLimiter _uploadLimiter;
        private readonly object _limiterGate = new object();

        /// <summary>
        /// Changes the upload limit while the engine is running (Settings). Writes and reads of UploadLimiter share a lock, so on the 32-bit TV
        /// a half-written 'long' is never seen. Every upload request queries UploadLimiter, so the new maximum applies from the next one.
        /// </summary>
        public void SetUploadMaxBytesPerSec(long bytesPerSecond)
        {
            lock (_limiterGate) UploadMaxBytesPerSec = bytesPerSecond;
        }

        /// <summary>Global upload limiter (null = no limit). Created from UploadMaxBytesPerSec and shared among all sessions.</summary>
        public AceEngine.Util.RateLimiter UploadLimiter
        {
            get
            {
                lock (_limiterGate)
                {
                    if (UploadMaxBytesPerSec <= 0) return null;
                    if (_uploadLimiter == null || _uploadLimiter.Rate != UploadMaxBytesPerSec) _uploadLimiter = new AceEngine.Util.RateLimiter(UploadMaxBytesPerSec);
                    return _uploadLimiter;
                }
            }
        }

        // ---------- Session / server ----------

        public HttpServerOptions Http = new HttpServerOptions();

        /// <summary>content_id resolution.</summary>
        public ResolverOptions Resolver = new ResolverOptions();

        /// <summary>Live streams.</summary>
        public LiveOptions Live = new LiveOptions();

        /// <summary>Link sources: mylinkspaste DNS TXT.</summary>
        public DnsOptions Dns = new DnsOptions();

        /// <summary>Probing of seeds and peers.</summary>
        public ProbeOptions Probe = new ProbeOptions();

        /// <summary>Maximum time to obtain the metadata of a magnet (the session fails after this time). Zero = no limit.</summary>
        public TimeSpan MetadataTimeout = TimeSpan.FromMinutes(3);
    }
}
