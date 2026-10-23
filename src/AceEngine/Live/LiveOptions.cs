using System;
using System.Collections.Generic;

namespace AceEngine.Live
{
    /// <summary>What to do with pieces whose RSA signature does not verify.</summary>
    public enum SignatureMode
    {
        /// <summary>Every piece whose signature does not verify is discarded and the peer is punished.</summary>
        Strict,
        /// <summary>
        /// Like Strict, but if the first complete pieces (TolerantProbePieces) all fail and come from several peers, it is assumed that the
        /// problem is in our reading of the format (not in the peers) and the pieces are accepted unverified, with a warning in the log.
        /// </summary>
        Tolerant,
        /// <summary>No signature is verified.</summary>
        Off
    }

    /// <summary>How to choose the width of the chunk number in REQUEST/CANCEL/PIECE (H if v &gt; 3002399, otherwise B).</summary>
    public enum ChunkWidthMode { Auto, Byte, Short }

    /// <summary>
    /// Live client parameters. Everything inferred or unknown about the protocol is configurable here. The defaults marked
    /// CONFIRMED were obtained from a real capture of the Ace network (2026-10-16).
    /// </summary>
    public sealed class LiveOptions
    {
        // ---------- EXTEND ----------

        /// <summary>'v': engine version as an integer (3022200 = 3.2.22). Decides the capabilities the remote assumes of us.</summary>
        public int EngineVersion = 3022200;

        /// <summary>'platform' (the key is CONFIRMED; 3 in the protocol notes, 1 in a real 3.1.74 peer).</summary>
        public int Platform = 3;

        /// <summary>'pv': protocol version (2; CONFIRMED in a real capture).</summary>
        public int ProtocolVersion = 2;

        /// <summary>'nt': node type. 1 = NODE_CLIENT; CONFIRMED in a real capture (client peers send nt=1).</summary>
        public int NodeType = 1;

        /// <summary>'tt': transport type. 'bt'; CONFIRMED in a real capture.</summary>
        public string TransportType = "bt";

        /// <summary>
        /// 'is_accessible' that is announced (EXTEND and MYINFO). By default it is HONEST: true only if someone from outside has already connected to us in this session or if
        /// <see cref="AccessibleProvider"/> says so (for example, when UPnP / NAT-PMP has opened the port). Measured: big nodes (nt=1 with a lot of upload, support nodes)
        /// close right after the EXTEND on those that are not accessible (6 % acceptance versus 31 % if they claim to be), hence the value of really opening the port.
        /// </summary>
        public Func<bool> AccessibleProvider = null;

        /// <summary>
        /// Announce the configured upload limit as top_up_rate / top_session_up_rate (the maximum expected upload, MYINFO's "max_up_rate"), if it is greater than what was measured. It is the upload
        /// that is OFFERED, not a measurement: support nodes (nt=3) only keep those who declare about 2 MB/s or more (0 % acceptance with 256 KB/s, 58 % with 2 MB/s). With upload
        /// disabled or unlimited nothing is announced.
        /// </summary>
        public bool AdvertiseUploadCap = false;

        /// <summary>EXPERIMENTS ONLY: announce is_accessible = 1 even if we are not. Not used in the product (peers would try to connect to a closed port).</summary>
        public bool ClaimAccessible = false;

        /// <summary>mi.peer_type: 0 in the real peers captured (the protocol notes said UNKNOWN).</summary>
        public int PeerType = 0;

        /// <summary>
        /// Sign the EXTEND with ed25519. A real 3.1.74 peer does not sign its own and accepted ours both unsigned and signed, so it is not
        /// clear that it is required; it is signed by default in case newer engines do require it.
        /// </summary>
        public bool SignExtend = true;

        /// <summary>
        /// Verify the ed25519 signature of every remote EXTEND that carries one (about 5 ms on PC, much more on the TV). By default only
        /// those of the first LogExchangePeers peers are verified (diagnostics), except with StrictRemoteSignature, which verifies them all.
        /// </summary>
        public bool VerifyRemoteSignature = false;

        /// <summary>If the remote EXTEND carries a signature and it does not verify: false = warning only, true = close the connection (like the original).</summary>
        public bool StrictRemoteSignature = false;

        /// <summary>Close the connection if the remote EXTEND carries no signature (the real peer captured did not carry one, hence false).</summary>
        public bool RequireRemoteSignature = false;

        /// <summary>The EXTEND 'mi' dictionary uses long names (position, min_piece...; CONFIRMED in a real capture). false = MYINFO letters.</summary>
        public bool LongMiKeys = true;

        /// <summary>Send MYINFO periodically (INFERRED; real peers send it about every 1 s) and how many seconds apart.</summary>
        public bool SendMyInfo = true;
        public double MyInfoIntervalSeconds = 3;

        /// <summary>Learn peer_type from client peers (nt=1) and use it from then on.</summary>
        public bool LearnPeerType = true;

        /// <summary>Outgoing handshake with "AceStreamProtocol" (PREF protocol=1) instead of "BitTorrent protocol" (the default, PREF protocol=2).</summary>
        public bool UseAceProtocolName = false;

        /// <summary>Chunk width in REQUEST/CANCEL/PIECE.</summary>
        public ChunkWidthMode ChunkWidth = ChunkWidthMode.Auto;

        /// <summary>
        /// Timestamps in HAVE and BITFIELD (timestamps_v2). The protocol notes take them as active for remote v &gt;= 3000400, but a real 3.1.74 peer
        /// sends them WITHOUT timestamps. Auto = mimic what each peer sends (by the length of its messages); false until it is known.
        /// </summary>
        public bool? SendTimestamps = null;

        // ---------- Live position and startup ----------

        /// <summary>live_buffer_time: delay relative to live and prebuffer size.</summary>
        public double BufferSeconds = 10;

        /// <summary>core_live_vote_min_peers: peers that must agree in order to trust the live position without lsp.</summary>
        public int VoteMinPeers = 2;

        /// <summary>With a single peer reporting the position, it is accepted after this delay (INFERRED). It was 6 s: on the TV, with channels that have a single connectable peer, those 6 s were pure delay (no second peer arrives later).</summary>
        public double SinglePeerLivePosSeconds = 2.5;

        /// <summary>core_live_window_duration: window announced in CHANGEPOS and MYINFO.</summary>
        public double LiveWindowSeconds = 900;

        /// <summary>Maximum time waiting for the complete prebuffer; after this delay it starts with the contiguous part (minimum 2 pieces).</summary>
        public double PrebufferTimeoutSeconds = 25;

        // ---------- Memory ----------

        /// <summary>Cap of pieces in RAM (live up to 32 MiB).</summary>
        public long MaxBufferBytes = 32L * 1024 * 1024;

        /// <summary>Seconds kept behind the playback position (to serve others and re-read).</summary>
        public double KeepBehindSeconds = 20;

        /// <summary>Maximum pieces requested ahead of the position.</summary>
        public double AheadSeconds = 40;

        // ---------- Connection with the peers ----------

        /// <summary>
        /// Timeout of the connect and of the handshake with each live peer (zero = the engine's, 8 s and 15 s; by default 4 s and 4 s: only 0.3 % of the peers that answer take longer). Measured: peers that answer do
        /// connect + handshake in < 0.4 s (99 %), whereas 60-65 % of the candidates end in a connect timeout (NAT) and occupy the slot for the whole timeout.
        /// </summary>
        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4), HandshakeTimeout = TimeSpan.FromSeconds(4);

        /// <summary>After this many consecutive connect timeouts in a /16 network with no peer answering, its candidates go to the end of the queue (0 = never).</summary>
        public int NetworkPenaltyAfter = 6;

        /// <summary>A peer that closes right after the EXTEND without announcing pieces (full node) is retried with exponential backoff (15 s, 30 s... up to 5 min), not after 2 s.</summary>
        public bool FullPeerBackoff = true;

        /// <summary>DHT candidates are tried after those from trackers (they yield 2-10 times less).</summary>
        public bool DeprioritizeDht = true;

        /// <summary>A peer whose EXTEND carries neither nt, nor pv, nor v is not an Ace engine (any BitTorrent client announcing the same infohash): it is set aside for the whole session.</summary>
        public bool BanNonAcePeers = true;

        /// <summary>Remember the peers that worked with each channel (PeerHintStore) and try them first when opening it, even if the descriptor comes from the cache.</summary>
        public bool UsePeerHints = true;

        // ---------- Live with memory ----------

        /// <summary>
        /// RAM budget of the circular buffer of what was already served (pause, rewind, +/-10 s, LIVE). 0 = disabled: live works as always
        /// (one real reader and the player leads). Wired by the app from AppConfig.LiveTimeshiftMiB. Separate from MaxBufferBytes.
        /// </summary>
        public long TimeshiftBytes = 0;

        /// <summary>Size of the fixed blocks of the circular buffer (reused on eviction; no new memory is ever allocated with the buffer full).</summary>
        public int TimeshiftBlockBytes = 256 * 1024;

        // ---------- Download ----------

        public int MaxPeers = 40;
        public int MaxPendingConnects = 48;
        /// <summary>Pieces requested from the same peer at a time and live chunk requests per peer (piece_requests_max_backlog = 50).</summary>
        public int MaxPiecesPerPeer = 2;
        public int MaxChunkBacklog = 64;
        /// <summary>Base timeout for something of a requested piece to arrive (core_outstanding_request_min_deadline = 5 s).</summary>
        public double RequestDeadlineSeconds = 5;
        /// <summary>A piece requested for the next one to be played is also requested from a second peer after this time.</summary>
        public double UrgentDuplicateAfterSeconds = 2;
        public int UrgentPieces = 2;

        /// <summary>
        /// Also request from another peer the pieces that are slow: all those of the prebuffer while it fills, and the urgent ones afterwards. Measured: a piece (0.5-1 MiB) assigned to a 50-150 KB/s peer
        /// takes 7-18 s and the whole prebuffer waits for the slowest; the first to deliver wins and the other copy is discarded.
        /// </summary>
        public bool HedgeSlowPieces = true;
        /// <summary>A piece is also requested from another peer if it has been assigned for more than HedgeAfterSeconds and, at its current rate, it needs more than HedgeEtaSeconds.</summary>
        public double HedgeAfterSeconds = 1.5, HedgeEtaSeconds = 2.5;
        public double SnubSeconds = 20;

        // ---------- Piece signature ----------

        public SignatureMode Signature = SignatureMode.Tolerant;
        public int TolerantProbePieces = 6;

        // ---------- Holes and freezing ----------

        /// <summary>core_drop_max_hole and core_drop_min_pieces_after_hole: a hole of up to 1 piece is skipped if there are 10 behind it.</summary>
        public int DropMaxHole = 1;
        public int DropMinPiecesAfterHole = 10;
        /// <summary>A bigger hole is skipped after this time if there are pieces ahead (core_min_time_to_hole = 60 s in the original).</summary>
        public double HoleSkipSeconds = 20;
        /// <summary>Without advancing for this time (180 s in the original): jump to live.</summary>
        public double FreezeJumpSeconds = 45;
        /// <summary>If the delay relative to live exceeds this and the buffer emptied, jump to live.</summary>
        public double MaxLagSeconds = 90;
        /// <summary>On a sender restart (the indexes go backwards) it re-attaches when live falls below the position by this much.</summary>
        public double RestartDetectSeconds = 30;
        /// <summary>Time the regression of the live position must hold for the restart to be considered certain.</summary>
        public double RestartConfirmSeconds = 10;

        // ---------- Upload ----------

        /// <summary>Peers that get UNCHOKE. 0 = nothing is ever uploaded (the minimum the protocol allows: PING is still answered).</summary>
        public int UploadSlots = 4;
        /// <summary>How often, in seconds, the UNCHOKEs are reviewed.</summary>
        public double RechokeIntervalSeconds = 5;
        public bool AnnounceHave = true;
        /// <summary>Maximum padding bytes sent in a PONG (the PING asks for 'size' zero bytes).</summary>
        public int MaxPongBytes = 128 * 1024;

        // ---------- Discovery ----------

        public bool UseMetaTrackers = true;
        public bool UseDescriptorTrackers = true;
        public bool UseStartupNodes = true;
        /// <summary>Besides the infohash, query the DHT for the content_id (engines announce it as an alias; CONFIRMED on the real network).</summary>
        public bool DhtByContentId = true;

        // ---------- Diagnostics ----------

        /// <summary>Logs (Info level) the full EXTEND of the first N peers and, in Debug, all control messages.</summary>
        public int LogExchangePeers = 3;
        public bool LogAllMessages = false;
        /// <summary>Directory where the received PEERS messages are dumped (hex), to study their format. Null = not dumped.</summary>
        public string DumpPeersDir = null;
    }
}
