using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;

namespace AceEngine.Probe
{
    public enum LinkState
    {
        /// <summary>Not probed yet.</summary>
        Pending,
        Checking,
        /// <summary>seeds &gt; 0 or peers &gt; 0 (or, for http, the stream answers).</summary>
        Good,
        /// <summary>Invalid tracker responses or unknown infohash.</summary>
        Dead,
        /// <summary>(0,0), network error or could not be resolved: it is retried in the next cycle.</summary>
        Unknown,
        /// <summary>Scheme or kind that is not probed (web, label...).</summary>
        Unsupported
    }

    /// <summary>Why a content_id was not resolved: nobody serves its file, or the resolution ran out of resources (connections, DHT queries) and did not conclude.</summary>
    public enum ResolveFailure { None, NoServer, Starved }

    public sealed class ProbeResult
    {
        public LinkState State = LinkState.Pending;
        public int Seeders, Peers;
        /// <summary>true if it came from the fallback (announce or DHT) and not from a scrape: approximate figures.</summary>
        public bool Approximate;
        /// <summary>true for live http stream links: there are no figures for seeds or peers.</summary>
        public bool NoCounts;
        public string Method;                 // "scrape-http", "scrape-udp", "announce", "dht", "http"
        public string InfoHash;
        public string Error;
        public DateTime TimeUtc;
        public TimeSpan Elapsed;
        public List<ScrapeResult> Trackers = new List<ScrapeResult>();
        public string Source;                 // where the transport came from (cache, magnet...)
        /// <summary>Times the trackers of this link were counted (2 if the 10 s rule was applied).</summary>
        public int Rounds;
        /// <summary>The content_id could not be turned into a transport file (nothing is known about the channel).</summary>
        public bool Unresolved;
        /// <summary>If Unresolved: nobody serves it (it will be hidden) or lack of resources (it is retried and not hidden).</summary>
        public ResolveFailure Failure;
        /// <summary>What happened in the resolution (connections, DHT queries) for content_ids resolved over the network or that timed out.</summary>
        public AceEngine.Resolve.ResolveStats ResolveStats;
        /// <summary>How long the content_id resolution took.</summary>
        public TimeSpan ResolveElapsed;
        /// <summary>
        /// The channel's transport file was delivered by a single peer or a single network (TrustLevel.Unconfirmed, the same rule as playback:
        /// "Canal no confirmado: lo entrego un solo par"). It only makes sense for resolved content_ids; false for the rest.
        /// </summary>
        public bool Unconfirmed;
        /// <summary>Bitrate of the live stream in kbit/s according to its descriptor (0 if unknown): used to estimate the quality of channels whose name does not say it.</summary>
        public int BitrateKbps;

        public int Score { get { return Seeders + Peers; } }

        /// <summary>
        /// Live probe (LiveProber): connectable peers observed, recent pieces and position advance. Null = not done (trackers and DHT only).
        /// The scrape (Seeders, Peers, Score) counts peers announced in the last 30-60 min, almost all behind NAT or switched off; this is what can really be used.
        /// </summary>
        public LiveProbeResult Live;
        /// <summary>The live probe confirmed that the channel can be played now (connectable peers with recent pieces and the live stream advancing).</summary>
        public bool LiveVerified { get { return Live != null && Live.Verdict == LiveVerdict.Alive; } }
        /// <summary>The number to display: observed connectable active peers if the channel is verified; otherwise, the raw tracker count (unreliable).</summary>
        public int DisplayPeers { get { return LiveVerified ? Live.ActivePeers : Score; } }

        /// <summary>Classification with the figures already obtained.</summary>
        public static LinkState Classify(int seeders, int peers, bool anyInvalid)
        {
            if (seeders > 0 || peers > 0) return LinkState.Good;
            return anyInvalid ? LinkState.Dead : LinkState.Unknown;
        }

        public override string ToString()
        {
            return State + (State == LinkState.Good ? (NoCounts ? " (http)" : (LiveVerified ? " A " + Live.ActivePeers + (Live.Saturated ? "+" : "") + " /" : "") + " S " + Seeders + " P " + Peers + (Approximate ? " approx." : "")) : "") + (Method != null ? " [" + Method + "]" : "");
        }
    }

    public sealed class ProbeOptions
    {
        // ---------- User rule: a channel is only shown when real stream has arrived, in 15 s at most ----------

        /// <summary>
        /// Deadline of a live check: resolve the descriptor + probe the peers. It counts working time, not time waiting for a turn. After it, the channel stays "unknown" (it is not shown).
        /// Measured: the first data of the channels that work arrives in 2.5 s median, 5 s at p90 and only one in 75 takes over 15 s.
        /// </summary>
        public TimeSpan CheckBudget = TimeSpan.FromSeconds(15);
        /// <summary>
        /// Simultaneous outgoing TCP connections of ALL the checks together (resolve + probe). The home network holds ~60; the measured peak comes out a few units below this
        /// (some connections are closing). 0 = no shared budget (only the quotas of each phase).
        /// </summary>
        public int TcpBudget = 52;
        /// <summary>A channel verified as available is valid for this long; when it expires it is NOT checked again on its own, only when it is requested again (on being shown or opened).</summary>
        public TimeSpan AvailableTtl = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Looping retries: while the list where a channel appears stays open, every unverified channel (unknown by deadline, without descriptor, without pieces, stopped) is checked again,
        /// each attempt with its CheckBudget deadline, in rotation and after those never checked. At most this many retries of unverified channels at once: the ones that already
        /// failed must not keep the TV's network or CPU at maximum all the time (the first pass and the already verified cards that expire use the whole budget).
        /// </summary>
        public int RetryConcurrency = 4;
        /// <summary>
        /// With the UI AT REST (nobody has touched the remote for a while: ProbeScheduler.SetResting) the retries of what is unverified slow down: at most IdleRetryConcurrency at once
        /// and IdleRetryPause of breather after each attempt. Measured on the PC: the retry loop with 4 at once is the only thing that consumes CPU in the engine at rest
        /// (about 6-9 points of one core on the PC, ~25 times more per unit of work on the TV) and it recovers 0-3 channels every 3 minutes; on touching the remote again it recovers its pace.
        /// </summary>
        public int IdleRetryConcurrency = 1;
        public TimeSpan IdleRetryPause = TimeSpan.FromSeconds(10);
        /// <summary>
        /// Count first the peers announced on trackers/DHT (the old phase 2) and probe the live stream only if there are any. Disabled: the count decides nothing (the announced ones are almost never
        /// connectable) and takes up to 30 s; the probe already asks trackers and DHT for peers on its own.
        /// </summary>
        public bool CountBeforeVerify = false;

        // ---------- Live probe: the "alive" criterion that matters (LiveProber) ----------

        /// <summary>
        /// After the tracker/DHT count, connect to a few peers of each channel and check that one announces recent pieces and that the live position
        /// advances. A channel with announced peers that cannot be connected, or with the sender stopped, stops showing as alive.
        /// </summary>
        public bool VerifyLive = true;
        /// <summary>Channels verified at once (each one uses up to LiveProbe.MaxConnections sockets for a few seconds).</summary>
        public int LiveVerifyConcurrency = 10;
        /// <summary>Parameters of the live probe of each channel.</summary>
        public LiveProbeOptions LiveProbe = new LiveProbeOptions();
        /// <summary>Publish the count result (alive, unverified) as soon as it exists and then the verified one; otherwise the channel stays "checking" until it has the verified one.</summary>
        public bool PublishBeforeVerify = false;
        /// <summary>An "alive" verdict of the live probe is reused for this long before connecting to the peers again (the tracker count follows its own cycle).</summary>
        public TimeSpan LiveAliveTtl = TimeSpan.FromMinutes(10);
        /// <summary>A negative verdict (stopped, no connectable peers, no pieces) is reused for this long: retrying a dead channel every minute costs connections for nothing.</summary>
        public TimeSpan LiveDeadTtl = TimeSpan.FromMinutes(3);
        /// <summary>
        /// A channel that was verified alive (less than this ago) and comes out negative once (sender stopped, no connectable peers, no pieces) does NOT disappear from the list: it is kept
        /// as alive until the next check, and only if it comes out negative again is it removed. Measured: 4-9 % of the live ones come out negative in an isolated check and come out alive
        /// in the next one (broadcast with spaced pieces, peers that just do not answer), and without this the cards disappear and reappear at the end. Zero = no tolerance.
        /// </summary>
        public TimeSpan AliveGrace = TimeSpan.FromMinutes(20);
        /// <summary>
        /// How many consecutive negatives ONE real playback failure weighs (no sources 15 s after opening, live stopped, session lost), which the player reports to the checker
        /// (ProbeScheduler.NotePlayback). 1 = like a probe negative: the card is kept the first time (it is looked at again right away) and removed the second. 2 (default) = the
        /// failure already counts as two: the card is removed at once and the channel enters the retries. Reason: an isolated negative of the probe is wrong for 4-9 % of the live ones (a probe
        /// of 6 connections and a few seconds), but the player has done the same test for 15 s with the real download and only one in 94 channels that can be watched takes longer than 15 s:
        /// removing one card too many costs a retry, leaving a card that cannot be watched costs exactly what the user wants to avoid.
        /// </summary>
        public int PlaybackFailureStrikes = 2;
        /// <summary>
        /// Infohash and magnet links (results of the Ace central search) are treated like content_ids: their descriptor is resolved over P2P and the live stream is probed before considering them alive.
        /// Before, only peers announced by trackers and DHT were counted (425 of 444 results came out alive, none checked). false = the previous behavior.
        /// </summary>
        public bool ResolveInfoHashLinks = true;

        /// <summary>
        /// Links RESOLVED at once (content_id -> transport through DHT and peers). The queries are spread among thousands of DHT nodes, so
        /// there is no single URL that bans us; what protects is the budget of connections and DHT queries (ResolveConnectionBudget and
        /// EngineOptions.DhtMaxInFlight). The tracker count does NOT use this number: it goes grouped by tracker with its own brake.
        /// </summary>
        public int Concurrency = 50;

        // ---------- 10 s rule (without data it is not discarded the first time) ----------

        /// <summary>A link with 0 seeds and 0 peers (or without data) is checked again after this time and only hidden if it is still at zero. Zero = not repeated.</summary>
        public TimeSpan RecheckDelay = TimeSpan.FromSeconds(10);
        /// <summary>
        /// The resolution of content_ids that NOBODY serves is also retried, after RecheckDelay. Disabled by default: with resources guaranteed
        /// per link it recovers none (0 of 262 with 10 at once) and it costs another full cycle of DHT and connections.
        /// </summary>
        public bool RecheckUnresolved = false;

        // ---------- Tracker count: grouped and braked per host:port ----------

        /// <summary>At most one request to each tracker (host:port) every so often, counted from start to start.</summary>
        public TimeSpan TrackerInterval = TimeSpan.FromSeconds(2);
        /// <summary>With the tracker free and the queue short, it waits this long for more links to arrive before sending the batch.</summary>
        public TimeSpan BatchWindow = TimeSpan.FromMilliseconds(500);
        /// <summary>Simultaneous requests to the same tracker (1 or 2).</summary>
        public int TrackerMaxConcurrent = 1;
        /// <summary>Infohashes per UDP packet (BEP 15 allows 74 in a datagram; ~70 leaves margin).</summary>
        public int UdpBatchSize = 70;
        /// <summary>Infohashes per HTTP request (BEP 48). Prudent: 50.</summary>
        public int HttpBatchSize = 50;
        /// <summary>Maximum length of the HTTP scrape URL (servers usually cut at 8 KiB and proxies earlier).</summary>
        public int HttpMaxUrlLength = 3500;
        /// <summary>Backoff after a timeout or error: base and cap (doubles on each consecutive failure).</summary>
        public TimeSpan TrackerBackoffBase = TimeSpan.FromSeconds(5);
        public TimeSpan TrackerBackoffMax = TimeSpan.FromMinutes(5);
        /// <summary>After this many consecutive failures the tracker is benched (quarantine) for TrackerQuarantine, double if it fails again, up to TrackerQuarantineMax.</summary>
        public int TrackerQuarantineAfter = 4;
        public TimeSpan TrackerQuarantine = TimeSpan.FromMinutes(5);
        public TimeSpan TrackerQuarantineMax = TimeSpan.FromMinutes(30);
        /// <summary>A rejection (HTTP 429/403/451...) waits at least this long (or the Retry-After) and doubles; on the second it is benched.</summary>
        public TimeSpan TrackerRejectedBackoff = TimeSpan.FromSeconds(60);
        public int TrackerQuarantineAfterRejections = 2;
        /// <summary>A tracker without scrape (HTTP 404 on /scrape) is not asked again for this time.</summary>
        public TimeSpan TrackerUnsupportedBench = TimeSpan.FromMinutes(30);
        /// <summary>If a tracker would take longer than this to be able to serve the queue (backoff, quarantine, min_request_interval), the queue is discarded instead of waiting.</summary>
        public TimeSpan MaxQueueWait = TimeSpan.FromSeconds(15);
        /// <summary>Deadline for a link to gather the numbers of its trackers (queue included); after it, what there is is used.</summary>
        public TimeSpan CountTimeout = TimeSpan.FromSeconds(20);
        /// <summary>Remember the UDP connection id (BEP 15) so as not to spend a connect on every batch.</summary>
        public TimeSpan UdpConnectionTtl = TimeSpan.FromSeconds(60);
        /// <summary>DHT searches of the fallback at once (besides the global cap of queries in flight).</summary>
        public int FallbackConcurrency = 8;
        /// <summary>
        /// Budget of each resolution: simultaneous TCP connections with peers (0 = the engine's, 30). The result of a link must NOT depend on how many
        /// are resolved at once, so each resolution has its private budget and only as many are ADMITTED as fit in ResolveConnectionBudget
        /// (and in the DHT queries, EngineOptions.DhtMaxInFlight / 8): the others wait their turn BEFORE their deadline (ResolveTimeout) starts running.
        /// </summary>
        public int ResolveConnectionsPerLink = 8;
        /// <summary>TCP connections of all the resolutions together. Resolutions admitted at once = min(Concurrency, this / ResolveConnectionsPerLink, DhtMaxInFlight / 8).</summary>
        public int ResolveConnectionBudget = 750;
        /// <summary>
        /// Connect and handshake deadlines with each peer during the probe's resolution (zero = the engine's: 8 s and 15 s). Measured: 78 % of the attempts end
        /// in a connect timeout and occupy 99 % of the connection slots' time, while the peers that answer do connect + handshake in 167 ms
        /// (median), 363 ms (p99) and 2.3 s (maximum): with 3 s each slot tries almost 3 times more peers in the same time.
        /// </summary>
        public TimeSpan ResolveConnectTimeout = TimeSpan.FromSeconds(3), ResolveHandshakeTimeout = TimeSpan.FromSeconds(4);
        /// <summary>A resolution that ran out of resources (system errors, DHT queries waiting for a slot) is NOT considered not served: it is retried, and not hidden.</summary>
        public int StarvedRetries = 3;
        public TimeSpan StarvedRetryDelay = TimeSpan.FromSeconds(3);
        /// <summary>The probe's resolution also asks TSService (Ace's fixed hosts). Disabled: they answer an empty 200 and it would be 4 requests per link to the same hosts.</summary>
        public bool ResolveViaTsService = false;
        /// <summary>HTTP scrape deadline (8 s on the TV; the original uses 30).</summary>
        public TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);
        /// <summary>Attempts and deadline of each one for the UDP scrape (BEP15, at most 2 in total).</summary>
        public int[] UdpTimeoutsSeconds = { 4, 4 };
        /// <summary>The result of a link is valid for this long and is not repeated.</summary>
        public TimeSpan ResultTtl = TimeSpan.FromSeconds(60);
        /// <summary>
        /// Breather between one pass over the list and the next (and therefore the minimum between two retries of the same channel): 10 s. The peers and trackers of a channel hardly change in less,
        /// so retrying earlier only repeats the same; with a short list it avoids hammering the same channels over and over. With a long list the rotation takes longer than this anyway.
        /// </summary>
        public TimeSpan CycleInterval = TimeSpan.FromSeconds(10);
        /// <summary>Deadline to resolve a content_id to its transport file within the probe (the ones that do not resolve are usually dead channels).</summary>
        public TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Total deadline per link (resolution + trackers + fallback).</summary>
        public TimeSpan LinkTimeout = TimeSpan.FromSeconds(45);
        /// <summary>If a tracker asks to wait and does not say how long: 5 min.</summary>
        public TimeSpan DefaultWait = TimeSpan.FromMinutes(5);
        /// <summary>Trackers probed at most per level (like the original: 16).</summary>
        public int MaxTrackersPerLink = 16;
        /// <summary>Tracker scrapes at once within a link.</summary>
        public int TrackerParallelism = 4;
        /// <summary>Fallback when no tracker gives numbers: announce with numwant and/or DHT, marked as approximate.</summary>
        public bool UseAnnounceFallback = true;
        public bool UseDhtFallback = true;
        public int AnnounceNumWant = 50;
        public TimeSpan DhtTimeout = TimeSpan.FromSeconds(12);
        /// <summary>Public trackers for infohash/magnet without trackers of their own.</summary>
        public IList<string> DefaultTrackers = new List<string>
        {
            "udp://tracker.opentrackr.org:1337/announce", "udp://open.demonii.com:1337/announce", "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce", "udp://tracker.tiny-vps.com:6969/announce"
        };
        /// <summary>Check the http(s) stream links (.m3u8, .ts...) with a short request.</summary>
        public bool ProbeHttpLinks = true;
        public TimeSpan HttpLinkTimeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>What is needed to count a swarm: its infohash and the trackers to ask (coming out of the resolution).</summary>
    public sealed class SwarmTarget
    {
        public byte[] InfoHash;
        public IList<string> Trackers = new List<string>();
        public string ContentId;
        /// <summary>The link from the list or the search (content_id, infohash or magnet): used to read its descriptor again from the resolution cache.</summary>
        public AceEngine.Links.AceLink Link;
        public string Source;
        public TimeSpan ResolveElapsed;
        public AceEngine.Resolve.ResolveStats Stats;
        /// <summary>The channel descriptor is "unconfirmed" (a single peer/network delivered it): it is carried over to the ProbeResult.</summary>
        public bool Unconfirmed;
        /// <summary>Bitrate (kbit/s) of the live descriptor, or 0.</summary>
        public int BitrateKbps;
    }

    /// <summary>Result of phase 1: either there is already a final result (http, unsupported, unresolved) or a swarm to count.</summary>
    public sealed class ResolveStep
    {
        public ProbeResult Final;
        public SwarmTarget Target;
    }

    /// <summary>
    /// Phased prober (the one the scheduler uses to go in parallel): 1) resolve, 2) count by trackers grouping by tracker,
    /// 3) fallback through DHT. An ILinkProber that does not implement it is probed in one piece, with 'Concurrency' links at once.
    /// </summary>
    public interface ILiveVerifier
    {
        /// <summary>Phase 4: live probe (connectable peers, recent pieces, advance). Null if it cannot be done (no content_id, no descriptor in the cache).</summary>
        Task<LiveProbeResult> VerifyLiveAsync(SwarmTarget target, CancellationToken ct);
    }

    public interface IPhasedProber : ILinkProber
    {
        Task<ResolveStep> ResolveAsync(LinkEntry entry, CancellationToken ct);
        /// <summary>One round of counting by trackers (batches per tracker, with brake). It does not do the fallback.</summary>
        Task<ProbeResult> CountAsync(SwarmTarget target, CancellationToken ct);
        /// <summary>Fallback when the trackers give no numbers (DHT): approximate figure. Returns a result without figures if there are no peers.</summary>
        Task<ProbeResult> FallbackAsync(SwarmTarget target, CancellationToken ct);
    }

    /// <summary>An already sorted row of the listing.</summary>
    public sealed class ListRow
    {
        public LinkEntry Entry;
        public ProbeResult Result;
    }
}
