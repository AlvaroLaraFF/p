using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>The real services of the app and what has to be released on exit.</summary>
    public sealed class TvRuntime : IDisposable
    {
        public TvServices Services;
        public EngineService Engine;
        public TsServiceMonitor TsMonitor;
        public NetworkServices Network;
        public Action<string> Log;

        public void Dispose()
        {
            try { if (Services.Checker != null) Services.Checker.Stop(); } catch { }
            try { var d = Services.Checker as IDisposable; if (d != null) d.Dispose(); } catch { }
            try { ChannelHints.Save(); } catch { }
            try { var c = Services.Catalog as IDisposable; if (c != null) c.Dispose(); } catch { }
            try { if (TsMonitor != null) TsMonitor.Dispose(); } catch { }
            try { if (Network != null) Network.Dispose(); } catch { }
            try { Engine.Dispose(); } catch { }
        }
    }

    /// <summary>Assembles the app's real services on top of the engine (catalog, recents, checker, search, settings, network and playback).</summary>
    public static class TvServicesFactory
    {
        public static TvRuntime Create(AppConfig cfg, string dataDir, IPlayerFactory players, Action<string> log)
        {
            return Create(cfg, dataDir, new PlatformHooks { Players = players }, log);
        }

        /// <summary>The same with everything the platform plugs in (player, network info, multicast lock, playback tuning). See PlatformHooks.</summary>
        public static TvRuntime Create(AppConfig cfg, string dataDir, PlatformHooks hooks, Action<string> log)
        {
            if (hooks == null) hooks = new PlatformHooks();
            if (log == null) log = m => { };
            var rt = new TvRuntime { Log = log };
            ChannelHints.Attach(dataDir);                       // learned channel bitrates (quality of the ones that do not say it in the name)
            var store = UserSettingsStore.InDirectory(dataDir);
            var settings = store.Load();
            settings.TsServiceEnabled = cfg.TsService;          // TSService removed from the app: only with the "tsService" key of the configuration (tests)
            var ts = new TsServiceMonitor(settings);
            var network = new NetworkServices(settings, ts, hooks.Multicast);
            rt.TsMonitor = ts; rt.Network = network;
            var engine = new EngineService(() =>
            {
                var opt = EngineOptionsFactory.Build(cfg, settings, dataDir);
                ts.Attach(opt);
                return opt;
            }, host =>
            {
                AttachPlatform(host, hooks);                  // before UPnP starts: OnEngineStarted turns the port mapper on
                network.OnEngineStarted(host); ts.RequestCheck(); log("engine ready (" + network.Describe() + ")");
            });
            rt.Engine = engine;

            var catalog = new CatalogService(cfg, new ListLoader(), dataDir);
            var recents = new RecentChannelsStore(catalog, dataDir);
            var providers = new SearchProviderFactory(cfg, settings);
            var search = new SearchService(catalog, new IRemoteSearchProvider[] { new DynamicProviders(providers) }, dataDir);
            var checker = new AsyncLiveChecker(new LiveChecker(() => engine.Host));      // Check/Stop do not block the UI thread

            var services = new TvServices
            {
                Config = cfg, Settings = settings, Catalog = catalog, Recents = recents, Checker = checker, Search = search, Network = network,
                Playback = new PlaybackFactory(engine, hooks.Players, checker as AceEngine.Probe.IPlaybackReporter, hooks.Playback),
                SaveSettings = () =>
                {
                    store.Save(settings);
                    log("settings saved: only live=" + settings.OnlyLive + " min resolution=" + ChannelResolution.StepName(settings.MinResolution) + " language=" + ChannelLanguages.DisplayName(settings.Language) + " upload=" + settings.UploadKBps + " KB/s upnp=" + settings.UpnpEnabled + " central search=" + settings.AceSearchEnabled);
                },
                Suspend = () =>
                {
                    checker.Stop();
                    if (KeepAlive(hooks)) { log("suspend: the engine stays alive while a channel is being sent to another screen"); return; }
                    network.OnEngineStopping();
                    engine.Shutdown();
                },
                Resume = () => { },
            };
            // Changing a setting applies to the running engine (upload limit, TSService) and to UPnP
            settings.Changed += () =>
            {
                try
                {
                    if (engine.IsRunning) engine.Host.ApplyUserSettings(settings);
                    network.OnSettingsChanged();
                    ts.RequestCheck();
                }
                catch (Exception ex) { log("applying settings: " + ex.Message); }
            };
            rt.Services = services;
            return rt;
        }

        private static bool KeepAlive(PlatformHooks hooks)
        {
            try { return hooks.KeepEngineAlive != null && hooks.KeepEngineAlive(); }
            catch (Exception) { return false; }
        }

        /// <summary>Puts what the platform plugs in into a freshly started engine: the port mapper reads the network through the platform's INetworkInfo.</summary>
        internal static void AttachPlatform(AceEngineHost host, PlatformHooks hooks)
        {
            var net = hooks != null ? hooks.Network : null;
            if (net != null) host.PortMapperFactory = () => new PortMapper(new PortMapperOptions { Network = net });
        }

        /// <summary>Opens a playback on the current engine (started on first use) and the platform player.</summary>
        internal sealed class PlaybackFactory : IPlaybackFactory
        {
            private readonly EngineService _engine; private readonly IPlayerFactory _players; private readonly AceEngine.Probe.IPlaybackReporter _reporter; private readonly PlaybackOptions _options;
            public PlaybackFactory(EngineService engine, IPlayerFactory players, AceEngine.Probe.IPlaybackReporter reporter = null, PlaybackOptions options = null) { _engine = engine; _players = players; _reporter = reporter; _options = options; }
            /// <summary>What the player sees (stream arrives, no sources, live stopped) is told to the channel checker: the channel's card updates without waiting for its cache to expire.</summary>
            public IPlayback Create() { return new PlaybackSession(_engine.Host, _players, _options, _reporter); }
        }

        /// <summary>A provider that, on every call, asks SearchProviderFactory which ones are active (the central search is turned on and off in Settings without recreating anything).</summary>
        private sealed class DynamicProviders : IRemoteSearchProvider
        {
            private readonly SearchProviderFactory _factory;
            public DynamicProviders(SearchProviderFactory factory) { _factory = factory; }
            public string Name { get { return "providers"; } }

            public Task<IReadOnlyList<RemoteResult>> SearchAsync(string query, CancellationToken ct) { return Run(p => p.SearchAsync(query, ct), ct); }
            public Task<IReadOnlyList<RemoteResult>> ExploreAsync(string kindId, CancellationToken ct) { return Run(p => p.ExploreAsync(kindId, ct), ct); }

            private async Task<IReadOnlyList<RemoteResult>> Run(Func<IRemoteSearchProvider, Task<IReadOnlyList<RemoteResult>>> call, CancellationToken ct)
            {
                var list = _factory.GetProviders();
                if (list.Count == 0) return new List<RemoteResult>();
                var tasks = list.Select(p => Safe(call, p)).ToArray();
                var all = await Task.WhenAll(tasks).ConfigureAwait(false);
                return all.SelectMany(x => x).ToList();
            }

            private static async Task<IReadOnlyList<RemoteResult>> Safe(Func<IRemoteSearchProvider, Task<IReadOnlyList<RemoteResult>>> call, IRemoteSearchProvider p)
            {
                try { return await call(p).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AceLog.Warn("search", p.Name + ": " + ex.Message); return new List<RemoteResult>(); }
            }
        }
    }
}
