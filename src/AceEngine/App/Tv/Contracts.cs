using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Probe;
using AceEngine.Sources;

// Contracts between the app logic (AceEngine) and the UI (PUi). No UI or Skia. They are implemented by AceEngine.App.Tv; PUi and its tests
// consume these interfaces (with test doubles), so the signatures are stable: if one has to change, it is changed here and in PUi at the same time.
namespace AceEngine.App.Tv
{
    /// <summary>A channel of the unified list: it is no longer known which list it came from (there is no "Elcano" in the UI).</summary>
    public sealed class Channel
    {
        /// <summary>Stable deduplication key = LinkEntry.Key ("cid:&lt;40hex&gt;", "ih:...", or the URL of an http stream).</summary>
        public string Id;
        /// <summary>Name to display, clean: without trailing "*"/"**" marks or list prefixes; keeps the quality ("M+ Liga de Campeones 1080p").</summary>
        public string Name;
        /// <summary>Name as it came in the list.</summary>
        public string SourceName;
        /// <summary>Text of the quality label ("1080p", "720p", "FHD", "HD", "SD") or "" if unknown.</summary>
        public string Quality = "";
        /// <summary>Normalized key of the main category (the same for equivalent categories of several lists).</summary>
        public string CategoryKey = "";
        /// <summary>Display name of the main category.</summary>
        public string CategoryName = "";
        /// <summary>Playable/probeable link.</summary>
        public LinkEntry Entry;
        /// <summary>Language (ISO 639-1 in lowercase: "es", "en", "pt", "pl"...) or "" if not recognized. See ChannelLanguages.</summary>
        public string Language = "";
        /// <summary>Resolution given by the Ace search (from its bitrate), or 0. It only counts if the name does not say the quality.</summary>
        public int MetaResolution;

        /// <summary>Known picture height (2160, 1080, 720, 480) or 0 if unknown: from the name label, from the search metadata or from the bitrate learned when checking the channel.</summary>
        public int Resolution
        {
            get
            {
                int r = ChannelResolution.FromLabel(Quality);
                if (r > 0) return r;
                if (MetaResolution > 0) return MetaResolution;
                return ChannelResolution.FromBitrateKbps(ChannelHints.BitrateKbps(Id));
            }
        }

        public override string ToString() { return Name + " [" + Id + "]"; }
    }

    public sealed class Category
    {
        public string Key = "";
        public string Name = "";
        public readonly List<Channel> Channels = new List<Channel>();
    }

    public enum CatalogState { Loading, Ready, Failed }

    /// <summary>The configured lists, merged into one (channels deduplicated by content_id and equivalent categories joined).</summary>
    public interface IChannelCatalog
    {
        CatalogState State { get; }
        /// <summary>Reason if State == Failed (or notice if an old copy is shown).</summary>
        string Error { get; }
        /// <summary>Categories in order of appearance in the lists; empty ones do not come out.</summary>
        IReadOnlyList<Category> Categories { get; }
        IReadOnlyList<Channel> Channels { get; }
        Channel Find(string id);
        /// <summary>Starts loading (idempotent). If there is a copy on disk, State goes to Ready right away and it is refreshed in the background.</summary>
        void Start();
        /// <summary>Changes State, Categories or Channels (fired on a pool thread).</summary>
        event Action Changed;
    }

    /// <summary>Channels played, the most recent first, at most Max, without repeats; persistent.</summary>
    public interface IRecentChannels
    {
        IReadOnlyList<Channel> Items { get; }
        void Played(Channel channel);
        event Action Changed;
    }

    /// <summary>What is known about a channel after checking it.</summary>
    public sealed class ChannelStatus
    {
        /// <summary>Pending (never checked), Checking, Good (live), Dead/Unknown (no signal).</summary>
        public LinkState State = LinkState.Pending;
        public int Peers, Seeds;
        /// <summary>Approximate figure (it came from the DHT and not from a scrape).</summary>
        public bool Approximate;
        /// <summary>The channel was resolved through a single peer/network (TrustStore): amber shield in the UI.</summary>
        public bool Unconfirmed;
        /// <summary>
        /// Peers is the number of ACTIVE CONNECTABLE peers observed by the live probe (connected, EXTEND, recent pieces and the live stream advancing), not the tracker scrape.
        /// false = only the raw tracker/DHT count is available (the live probe could not be done or is disabled).
        /// </summary>
        public bool Verified;
        /// <summary>The probe stopped on reaching its target: there are at least as many peers as Peers ("4+").</summary>
        public bool AtLeast;
        /// <summary>Estimate of the connectable peers of the swarm (breaks ties in the order between channels that saturate the probe).</summary>
        public int Estimated;
        /// <summary>Why it is not considered live, if the live probe discarded it (sender stopped, no connectable peer, no recent pieces); null if not applicable.</summary>
        public string Reason;
        /// <summary>Peers + seeds.</summary>
        public int Score { get { return Peers + Seeds; } }
        /// <summary>The number to display: "4+", "12"...</summary>
        public string PeersText { get { return Score.ToString(System.Globalization.CultureInfo.InvariantCulture) + (Verified && AtLeast ? "+" : ""); } }
        /// <summary>List order key (higher first): the verified ones before those that only have the raw count, by observed peers and then by estimate.</summary>
        public long OrderKey { get { return Verified ? 1000000L * Peers + Estimated : Score; } }
        public bool IsLive { get { return State == LinkState.Good; } }
        /// <summary>true when it is already known that it has no signal (nothing to wait for).</summary>
        public bool IsNoSignal { get { return State == LinkState.Dead || State == LinkState.Unknown; } }
        public bool IsChecking { get { return State == LinkState.Checking || State == LinkState.Pending; } }
    }

    /// <summary>Checks which channels have signal (parallel probing). A single active batch: Check replaces the previous one.</summary>
    public interface ILiveChecker
    {
        /// <summary>Never null; Pending if never checked (or the result already expired and there is no active batch).</summary>
        ChannelStatus GetStatus(Channel channel);
        /// <summary>Checks these channels (in this order: the first ones, first). Cancels the previous batch and keeps the valid results (60 s).</summary>
        void Check(IReadOnlyList<Channel> channels);
        /// <summary>Cancels what is in progress (valid results are kept).</summary>
        void Stop();
        bool Running { get; }
        /// <summary>Of the current batch: checked / total.</summary>
        int Done { get; }
        int Total { get; }
        /// <summary>A channel changes state (starts being checked, figures arrive...). Pool thread.</summary>
        event Action<Channel, ChannelStatus> StatusChanged;
    }

    /// <summary>
    /// What the checker needs from the UI to spend little at rest: the UI reports that the remote has not been touched for a while (true) or that it has been touched again (false).
    /// At rest the looping retries of what is unverified slow down (ProbeOptions.IdleRetryConcurrency / IdleRetryPause); it is optional: a checker that does not implement it is ignored.
    /// </summary>
    public interface IRestAware
    {
        void SetResting(bool resting);
    }

    public sealed class ExploreKind
    {
        /// <summary>"futbol", "baloncesto", "motor", "tenis", "deportes", "noticias", "cine", "series", "documentales", "musica", "infantil", "internacional".</summary>
        public string Id = "";
        /// <summary>"Fútbol", "Baloncesto", "Motor", "Tenis", "Deportes", "Noticias", "Cine", "Series", "Documentales", "Música", "Infantil", "Internacional".</summary>
        public string Name = "";
    }

    /// <summary>Search over the channels of the lists (local, instantaneous) and Explore by kinds.</summary>
    public interface ISearchService
    {
        /// <summary>The 12 Explore kinds, in the design order.</summary>
        IReadOnlyList<ExploreKind> Kinds { get; }
        /// <summary>Channels of an Explore kind (classification by keywords of the name and the category), in catalog order. Local only, instantaneous.</summary>
        IReadOnlyList<Channel> Explore(string kindId);
        /// <summary>What the configured providers and, if enabled, the Ace central search contribute outside the catalog for a kind; does not repeat those of Explore(). Empty if there are no providers.</summary>
        Task<IReadOnlyList<Channel>> ExploreRemoteAsync(string kindId, CancellationToken ct);
        /// <summary>Instantaneous local search by name (case- and accent-insensitive; all the words; starts-with before contains). Empty if the text is empty.</summary>
        IReadOnlyList<Channel> Search(string text);
        /// <summary>Additional results from configured providers and, if enabled, from the Ace central search; does not include those already in Search().</summary>
        Task<IReadOnlyList<Channel>> SearchRemoteAsync(string text, CancellationToken ct);
        /// <summary>Recent searches (the most recent first, at most 10, without repeats (repeating one moves it to the first) and the oldest drops out when going past 10; persistent).</summary>
        IReadOnlyList<string> RecentQueries { get; }
        void AddRecentQuery(string text);
    }

    /// <summary>A result of a remote search provider (not yet a Channel: the search service converts it, removes repeats and classifies it).</summary>
    public sealed class RemoteResult
    {
        public string Name = "";
        /// <summary>acestream://, content_id, magnet: or http stream URL.</summary>
        public string Url = "";
        /// <summary>Category given by the provider ("" if it gives none).</summary>
        public string Category = "";
        /// <summary>Peers/seeds announced by the provider (-1 if it does not give them).</summary>
        public int Peers = -1, Seeds = -1;
        /// <summary>Bitrate announced by the Ace search (kbit/s; 0 if it does not give it). Used to estimate the quality when the name does not say it.</summary>
        public int BitrateKbps;
        /// <summary>Languages and countries from the Ace search metadata ("spa", "eng", "es", "gb"...); empty if it does not give them.</summary>
        public string[] Languages = new string[0], Countries = new string[0];
    }

    /// <summary>A search provider: /search format configurable by the user (HttpSearchProvider) or the Ace central search (AceCentralSearchProvider, off by default).</summary>
    public interface IRemoteSearchProvider
    {
        string Name { get; }
        Task<IReadOnlyList<RemoteResult>> SearchAsync(string query, CancellationToken ct);
        /// <summary>Channels of the Explore kind (ExploreKind ids); empty list if the provider does not support it.</summary>
        Task<IReadOnlyList<RemoteResult>> ExploreAsync(string kindId, CancellationToken ct);
    }

    public enum UpnpState { Disabled, Searching, Open, Unavailable }
    public enum TsServiceState { Disabled, Unknown, Responding, NoResponse }

    /// <summary>State of the network functions that the UI shows in Settings.</summary>
    public sealed class NetworkStatus
    {
        public UpnpState Upnp = UpnpState.Disabled;
        /// <summary>TCP/UDP port opened on the router when Upnp == Open.</summary>
        public int UpnpPort;
        /// <summary>Port on which the engine listens for other viewers (P2P); 0 if the engine has not started yet.</summary>
        public int ListenPort;
        public TsServiceState TsService = TsServiceState.Disabled;
    }

    public interface INetworkServices
    {
        NetworkStatus Status { get; }
        event Action Changed;
    }

    /// <summary>What the user changes in Settings (saved in data/settings.json). The default values are those of the design.</summary>
    public sealed class UserSettings
    {
        public static readonly int[] UploadStepsKBps = { 128, 256, 512, 1024, 2048 };
        public bool OnlyLive = true;
        /// <summary>Minimum resolution of the channels shown: 0 (Cualquiera), 720, 1080 or 2160 (4K). A channel without a recognizable quality only shows with Cualquiera.</summary>
        public int MinResolution = 0;
        /// <summary>Language of the categories and the search: "" (Todos) or a code ("es", "en", "pt", "pl"...). Channels of unknown language show with Todos and with "es".</summary>
        public string Language = "";
        /// <summary>Maximum upload in KB/s (upload is always on). One of UploadStepsKBps.</summary>
        public int UploadKBps = 512;
        public bool UpnpEnabled = true;
        /// <summary>
        /// TSService fallback path: OFF (user decision, 17-10-2026: removed from the app until it really works). It no longer appears in Settings, the value stored in
        /// settings.json is ignored and the engine does not query those servers. It is only turned on with the "tsService" key of the configuration (AppConfig.TsService) for tests.
        /// The engine code is kept (it will be picked up again with the emulator capture task).
        /// </summary>
        public bool TsServiceEnabled = false;
        /// <summary>Central Ace search: ON by default (user decision, 17-10-2026; signatures validated against the real service).</summary>
        public bool AceSearchEnabled = true;
        public event Action Changed;
        public void NotifyChanged() { var h = Changed; if (h != null) h(); }
    }

    /// <summary>Creates a playback (one per opened channel). The real implementation sits on top of the engine and the platform player.</summary>
    public interface IPlaybackFactory
    {
        IPlayback Create();
    }

    /// <summary>Everything the UI needs from the app, already assembled. Built by the startup of each platform (TvServicesFactory) or by the tests with doubles.</summary>
    public sealed class TvServices
    {
        public AppConfig Config = new AppConfig();
        public UserSettings Settings = new UserSettings();
        public IChannelCatalog Catalog;
        public IRecentChannels Recents;
        public ILiveChecker Checker;
        public ISearchService Search;
        public INetworkServices Network;
        public IPlaybackFactory Playback;
        /// <summary>Saves UserSettings to disk (null = not saved, in tests).</summary>
        public Action SaveSettings;
        /// <summary>The app goes to the background (Home, source change): stop probing and network. What is needed is created again on use.</summary>
        public Action Suspend;
        public Action Resume;
    }
}
