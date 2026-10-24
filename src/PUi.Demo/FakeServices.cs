using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Probe;
using AceEngine.Sources;

namespace PUi.Demo
{
    /// <summary>Doubles of the app's services with data from the design: to draw screens and states without network and for the PUi tests.</summary>
    public sealed class FakeCatalog : IChannelCatalog
    {
        private readonly List<Category> _cats = new List<Category>();
        private readonly List<Channel> _channels = new List<Channel>();
        public CatalogState State { get; set; }
        public string Error { get; set; }
        public event Action Changed;
        public FakeCatalog() { State = CatalogState.Ready; }
        public IReadOnlyList<Category> Categories { get { return _cats; } }
        public IReadOnlyList<Channel> Channels { get { return _channels; } }
        public Channel Find(string id) { return _channels.FirstOrDefault(c => c.Id == id); }
        public void Start() { }
        public void Raise() { var h = Changed; if (h != null) h(); }

        /// <summary>Replaces the content with a real snapshot (CatalogBuilder over the sample lists).</summary>
        public void LoadFrom(CatalogSnapshot snap)
        {
            _cats.Clear(); _channels.Clear();
            _cats.AddRange(snap.Categories); _channels.AddRange(snap.Channels);
        }

        public Channel Add(string category, string name, string quality = "1080p")
        {
            var cat = _cats.FirstOrDefault(c => c.Name == category);
            if (cat == null) { cat = new Category { Key = category.ToLowerInvariant(), Name = category }; _cats.Add(cat); }
            string id = "cid:" + (_channels.Count + 1).ToString("x").PadLeft(40, 'a');
            var ch = new Channel { Id = id, Name = name, SourceName = name, Quality = quality, Language = ChannelLanguages.Detect(name, name, category), CategoryKey = cat.Key, CategoryName = cat.Name, Entry = LinkEntry.FromUrl(name, "acestream://" + id.Substring(4)) };
            cat.Channels.Add(ch); _channels.Add(ch);
            return ch;
        }

        /// <summary>Sample catalog similar to the design's (10 categories).</summary>
        public static FakeCatalog Demo()
        {
            var c = new FakeCatalog();
            string[] liga = { "M+ Liga de Campeones 1080p", "Liga de Campeones FHD", "M+ Liga de Campeones 2 1080p", "Liga de Campeones 2 FHD", "M+ Liga de Campeones 2 720p", "M+ Liga de Campeones 3 720p", "Liga de Campeones 3 FHD", "Liga de Campeones 4", "M+ Liga de Campeones 4 1080p", "M+ Liga de Campeones 5 1080p", "M+ Liga de Campeones 6 1080p", "M+ Liga de Campeones 720p" };
            foreach (var n in liga) c.Add("Liga de Campeones", n, n.Contains("720") ? "720p" : "1080p");
            string[] others = { "Movistar", "Movistar Deportes", "DAZN", "DAZN F1", "Deportes", "Baloncesto", "NBA", "Eurosport", "Internacional" };
            foreach (var o in others)
                for (int i = 1; i <= 4; i++) c.Add(o, (o.StartsWith("DAZN") ? "DAZN " + (o == "DAZN F1" ? "F1 " : "La Liga ") : o + " ") + i + " FHD");
            c.Add("Deportes", "Teledeporte 1080p"); c.Add("Eurosport", "Eurosport 2 1080p"); c.Add("Baloncesto", "M+ Baloncesto 1080p"); c.Add("DAZN", "DAZN La Liga 1 FHD");
            return c;
        }
    }

    public sealed class FakeRecents : IRecentChannels
    {
        private readonly List<Channel> _items = new List<Channel>();
        public event Action Changed;
        public IReadOnlyList<Channel> Items { get { return _items; } }
        public void Played(Channel channel) { _items.RemoveAll(c => c.Id == channel.Id); _items.Insert(0, channel); if (_items.Count > 10) _items.RemoveAt(10); var h = Changed; if (h != null) h(); }
        public void Set(IEnumerable<Channel> items) { _items.Clear(); _items.AddRange(items); }
    }

    public sealed class FakeChecker : ILiveChecker
    {
        private readonly Dictionary<string, ChannelStatus> _st = new Dictionary<string, ChannelStatus>();
        public event Action<Channel, ChannelStatus> StatusChanged;
        public bool Running { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public List<IReadOnlyList<Channel>> Batches = new List<IReadOnlyList<Channel>>();
        /// <summary>State of channels with no data: Pending (default) or Checking.</summary>
        public LinkState Default = LinkState.Pending;

        public ChannelStatus GetStatus(Channel c)
        {
            ChannelStatus s;
            return _st.TryGetValue(c.Id, out s) ? s : new ChannelStatus { State = Default };
        }

        public void Set(Channel c, LinkState state, int peers = 0)
        {
            _st[c.Id] = new ChannelStatus { State = state, Peers = peers };
            var h = StatusChanged; if (h != null) h(c, _st[c.Id]);
        }

        /// <summary>Marks the check as finished: channels without a result end up in 'final' (by default, no signal). It is the state of a list that has been open for a while (first pass done).</summary>
        public void Settle(IEnumerable<Channel> channels, LinkState final = LinkState.Dead)
        {
            Default = final;
            foreach (var c in channels.ToList())
            {
                ChannelStatus cur;
                if (!_st.TryGetValue(c.Id, out cur) || cur.State == LinkState.Checking || cur.State == LinkState.Pending) Set(c, final);
            }
        }

        public void Check(IReadOnlyList<Channel> channels) { Batches.Add(channels); Total = channels.Count; }
        public int Stops;
        public void Stop() { Running = false; Stops++; }
    }

    public sealed class FakeSearch : ISearchService
    {
        private readonly IChannelCatalog _catalog;
        private readonly List<string> _recent = new List<string>();
        public FakeSearch(IChannelCatalog catalog) { _catalog = catalog; }

        public IReadOnlyList<ExploreKind> Kinds { get; } = new[]
        {
            new ExploreKind { Id = "futbol", Name = "Fútbol" }, new ExploreKind { Id = "baloncesto", Name = "Baloncesto" }, new ExploreKind { Id = "motor", Name = "Motor" },
            new ExploreKind { Id = "tenis", Name = "Tenis" }, new ExploreKind { Id = "deportes", Name = "Deportes" }, new ExploreKind { Id = "noticias", Name = "Noticias" },
            new ExploreKind { Id = "cine", Name = "Cine" }, new ExploreKind { Id = "series", Name = "Series" }, new ExploreKind { Id = "documentales", Name = "Documentales" },
            new ExploreKind { Id = "musica", Name = "Música" }, new ExploreKind { Id = "infantil", Name = "Infantil" }, new ExploreKind { Id = "internacional", Name = "Internacional" }
        };
        public IReadOnlyList<Channel> Explore(string kindId) { return _catalog.Channels.Take(14).ToList(); }
        public Task<IReadOnlyList<Channel>> ExploreRemoteAsync(string kindId, CancellationToken ct) { return Task.FromResult<IReadOnlyList<Channel>>(new List<Channel>()); }
        public IReadOnlyList<Channel> Search(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<Channel>();
            string t = text.ToLowerInvariant();
            var words = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return _catalog.Channels.Where(c => words.All(w => c.Name.ToLowerInvariant().Contains(w))).ToList();
        }
        public Task<IReadOnlyList<Channel>> SearchRemoteAsync(string text, CancellationToken ct) { return Task.FromResult<IReadOnlyList<Channel>>(new List<Channel>()); }
        public IReadOnlyList<string> RecentQueries { get { return _recent; } }
        public void AddRecentQuery(string text) { _recent.Remove(text); _recent.Insert(0, text); if (_recent.Count > 10) _recent.RemoveAt(10); }
    }

    public sealed class FakeNetwork : INetworkServices
    {
        public NetworkStatus Status { get; } = new NetworkStatus { Upnp = UpnpState.Open, UpnpPort = 8621, ListenPort = 8621, TsService = TsServiceState.NoResponse };
        public event Action Changed;
        public void Raise() { var h = Changed; if (h != null) h(); }
    }

    /// <summary>Playback with a hand-scripted state (there is no engine): the test or the capture sets the PlaybackStatus.</summary>
    public sealed class ScriptedPlayback : IPlayback
    {
        public PlaybackStatus Current = new PlaybackStatus();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        /// <summary>Called, on the calling thread, with every call ("stop", "dispose"...) as it happens: for the tests that check the order against other things.</summary>
        public Action<string> CallObserver;
        private void Record(string call) { Calls.Enqueue(call); var h = CallObserver; if (h != null) h(call); }
        public event Action<PlaybackStatus> Changed;
        public PlaybackStatus Status { get { return Current.Clone(); } }
        public string EngineDetail { get { return "scripted"; } }
        public long EngineLivePos { get { return -1; } }
        public TimeSpan TimeshiftWindow { get { return TimeSpan.FromMilliseconds(Current.TimeshiftWindowMs); } }
        public TimeSpan BehindLive { get { return TimeSpan.FromMilliseconds(Current.BehindLiveMs); } }

        private readonly object _pushGate = new object();
        /// <summary>Changes the state and notifies. Atomic and in order: RunAsync pushes from a pool thread and the test from its own; without this one push could overwrite the other.</summary>
        public void Push(Action<PlaybackStatus> change) { lock (_pushGate) { var s = Current.Clone(); change(s); Current = s; var h = Changed; if (h != null) h(s.Clone()); } }
        /// <summary>States that are given after starting (milliseconds since the start): for the demo mode and the timed tests.</summary>
        public readonly List<KeyValuePair<int, Action<PlaybackStatus>>> Script = new List<KeyValuePair<int, Action<PlaybackStatus>>>();
        /// <summary>When set, a cancelled run does not end until the test completes this task: the engine is still closing the channel's session.</summary>
        public Task CloseGate;

        public async Task RunAsync(LinkEntry entry, string name, CancellationToken ct)
        {
            Record("run " + name);
            Push(s => { s.Name = name; });
            try
            {
                int last = 0;
                foreach (var step in Script)
                {
                    await Task.Delay(Math.Max(0, step.Key - last), ct).ConfigureAwait(false);
                    last = step.Key;
                    Push(step.Value);
                }
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            var close = CloseGate;
            if (close != null) await close.ConfigureAwait(false);
        }
        public void Stop() { Record("stop"); }
        public void TogglePause() { Record("toggle"); Push(s => s.Phase = s.Phase == PlayPhase.Paused ? PlayPhase.Playing : PlayPhase.Paused); }
        public Task SeekRelativeAsync(long deltaMs) { Record("seek " + deltaMs); return Task.CompletedTask; }
        public Task SeekBehindLiveAsync(TimeSpan delta) { Record("seeklive " + (int)delta.TotalSeconds); return Task.CompletedTask; }
        public Task GoLiveAsync() { Record("golive"); return Task.CompletedTask; }
        public void Dispose() { Record("dispose"); }
    }

    public sealed class FakePlaybackFactory : IPlaybackFactory
    {
        public readonly List<ScriptedPlayback> Created = new List<ScriptedPlayback>();
        public Action<ScriptedPlayback> Init;
        public IPlayback Create() { var p = new ScriptedPlayback(); if (Init != null) Init(p); Created.Add(p); return p; }
    }

    public static class FakeServices
    {
        public static TvServices Demo(out FakeCatalog catalog, out FakeChecker checker, out FakeRecents recents, out FakePlaybackFactory playback)
        {
            catalog = FakeCatalog.Demo();
            checker = new FakeChecker();
            recents = new FakeRecents();
            playback = new FakePlaybackFactory();
            var s = new TvServices
            {
                Catalog = catalog, Checker = checker, Recents = recents, Search = new FakeSearch(catalog), Network = new FakeNetwork(), Playback = playback,
                Settings = new UserSettings()
            };
            return s;
        }
    }
}
