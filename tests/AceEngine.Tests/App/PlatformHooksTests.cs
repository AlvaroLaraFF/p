using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Tests.NetTests;
using Xunit;

namespace AceEngine.Tests.App
{
    public class PlatformHooksTests
    {
        private sealed class CountingNetwork : INetworkInfo
        {
            public int Calls;
            public IReadOnlyList<LocalInterface> GetInterfaces() { Interlocked.Increment(ref Calls); return new List<LocalInterface>(); }
            public IPAddress LocalAddressFor(IPAddress remote) { Interlocked.Increment(ref Calls); return null; }
            public event Action AddressesChanged { add { } remove { } }
        }

        private sealed class CountingLock : IMulticastLock
        {
            public int Acquires, Releases;
            public readonly List<string> Order = new List<string>();
            public void Acquire() { Acquires++; Order.Add("acquire"); }
            public void Release() { Releases++; Order.Add("release"); }
        }

        private sealed class NoPlayers : IPlayerFactory { public IPlayerBackend Create() { throw new NotSupportedException(); } }

        private static AceEngineHost Host(IPortMapper mapper = null)
        {
            return new AceEngineHost(new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }, mapper);
        }

        // ------------------------------------------------------------------ defaults

        [Fact]
        public void TheTizenDefaultsRestateTheBuiltInValuesSoTheSamsungConfigurationDoesNotChange()
        {
            var plain = new AppConfig();
            var tizen = new AppConfig();
            AppConfigDefaults.Tizen(tizen);
            Assert.Equal(plain.ToJson(), tizen.ToJson());
            Assert.Equal("finish", tizen.GlSync); Assert.Equal(8, tizen.SpriteBudgetMs); Assert.True(tizen.Warmer);
        }

        [Fact]
        public void PlatformDefaultsLoadBeforeTheLayersThroughTheHooks()
        {
            var hooks = new PlatformHooks { ConfigDefaults = c => { c.GlSync = "none"; c.SpriteBudgetMs = 0; c.Warmer = false; } };
            var cfg = AppConfigLoader.Load(new[] { ConfigLayer.FromText("user", "{\"warmer\":true}") }, null, hooks.ConfigDefaults);
            Assert.Equal("none", cfg.GlSync); Assert.Equal(0, cfg.SpriteBudgetMs); Assert.True(cfg.Warmer);
        }

        [Fact]
        public void EveryHookIsOptionalAndNullMeansTheEnginesOwnBehavior()
        {
            var h = new PlatformHooks();
            Assert.Null(h.Players); Assert.Null(h.Network); Assert.Null(h.Multicast); Assert.Null(h.Playback); Assert.Null(h.ConfigDefaults); Assert.Null(h.KeepEngineAlive);
        }

        // ------------------------------------------------------------------ keeping the engine alive while sending to another screen

        private static TvRuntime Runtime(PlatformHooks hooks, string dir)
        {
            var cfg = new AppConfig { EnableListener = false, DiagnosticsNetwork = false };
            // no UPnP and no central search: the test must not touch the network
            UserSettingsStore.InDirectory(dir).Save(new UserSettings { UpnpEnabled = false, AceSearchEnabled = false });
            return TvServicesFactory.Create(cfg, dir, hooks, null);
        }

        [Fact]
        public void WhenTheAppGoesToTheBackgroundTheEngineStopsUnlessSomethingIsBeingSentToAnotherScreen()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p-keep-" + Guid.NewGuid().ToString("N"));
            bool sending = false;
            var hooks = new PlatformHooks { Players = new NoPlayers(), KeepEngineAlive = () => sending };
            var rt = Runtime(hooks, dir);
            try
            {
                var host = rt.Engine.Host;
                Assert.True(rt.Engine.IsRunning);
                sending = true;
                rt.Services.Suspend();
                Assert.True(rt.Engine.IsRunning);                         // kept: the channel being sent is served by it
                Assert.Same(host, rt.Engine.Host);
                sending = false;
                rt.Services.Suspend();                                    // the shell suspends again when the sending ends in the background
                Assert.False(rt.Engine.IsRunning);
            }
            finally { rt.Dispose(); try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
        }

        [Fact]
        public void WithoutTheHookOrWithOneThatThrowsTheEngineStopsAsItAlwaysDid()
        {
            foreach (var hook in new Func<bool>[] { null, () => { throw new InvalidOperationException("boom"); } })
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p-keep-" + Guid.NewGuid().ToString("N"));
                var rt = Runtime(new PlatformHooks { Players = new NoPlayers(), KeepEngineAlive = hook }, dir);
                try
                {
                    var host = rt.Engine.Host;
                    Assert.True(rt.Engine.IsRunning);
                    rt.Services.Suspend();
                    Assert.False(rt.Engine.IsRunning);
                }
                finally { rt.Dispose(); try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
            }
        }

        // ------------------------------------------------------------------ playback options

        [Fact]
        public void ThePlaybackTuningOfThePlatformReachesTheSessions()
        {
            var opt = new PlaybackOptions { TimeshiftPlayerLatency = TimeSpan.FromMilliseconds(250) };
            using (var engine = new EngineService(() => new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }))
            using (var s = (PlaybackSession)new TvServicesFactory.PlaybackFactory(engine, new NoPlayers(), null, opt).Create())
                Assert.Same(opt, s.Options);
        }

        [Fact]
        public void WithoutPlatformTuningTheSessionsUseTheDefaultsMeasuredOnTheSamsungPlayer()
        {
            using (var engine = new EngineService(() => new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }))
            using (var s = (PlaybackSession)new TvServicesFactory.PlaybackFactory(engine, new NoPlayers(), null).Create())
                Assert.Equal(1000, s.Options.TimeshiftPlayerLatency.TotalMilliseconds);
        }

        // ------------------------------------------------------------------ network info

        [Fact]
        public void TheUpnpPortMapperReadsTheNetworkThroughThePlatformsNetworkInfo()
        {
            var net = new CountingNetwork();
            using (var host = Host())
            {
                TvServicesFactory.AttachPlatform(host, new PlatformHooks { Network = net });
                Assert.NotNull(host.PortMapperFactory);
                var mapper = host.PortMapperFactory();
                try
                {
                    mapper.Start(8621);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (Volatile.Read(ref net.Calls) == 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                    Assert.True(net.Calls > 0, "the port mapper never asked the platform's INetworkInfo");
                }
                finally { mapper.Stop(); mapper.Dispose(); }
            }
        }

        [Fact]
        public void WithoutAPlatformNetworkInfoTheEngineKeepsItsDefaultPortMapper()
        {
            using (var host = Host())
            {
                TvServicesFactory.AttachPlatform(host, new PlatformHooks());
                Assert.Null(host.PortMapperFactory);
                TvServicesFactory.AttachPlatform(host, null);
                Assert.Null(host.PortMapperFactory);
            }
        }

        // ------------------------------------------------------------------ multicast lock

        private static NetworkServices Services(UserSettings settings, IMulticastLock mc)
        {
            return new NetworkServices(settings, new TsServiceMonitor(settings), mc);
        }

        [Fact]
        public void TheMulticastLockIsHeldOnlyWhileUpnpIsWantedAndTheEngineRuns()
        {
            var mc = new CountingLock();
            var settings = new UserSettings { UpnpEnabled = true };
            var net = Services(settings, mc);
            using (var host = Host(new FakePortMapper()))
            {
                Assert.Equal(0, mc.Acquires);                       // the engine has not started
                net.OnEngineStarted(host);
                Assert.Equal(new[] { "acquire" }, mc.Order.ToArray());
                net.OnSettingsChanged();                            // nothing changed: not acquired twice
                Assert.Equal(1, mc.Acquires);
                settings.UpnpEnabled = false; net.OnSettingsChanged();
                Assert.Equal(new[] { "acquire", "release" }, mc.Order.ToArray());
                settings.UpnpEnabled = true; net.OnSettingsChanged();
                Assert.Equal(new[] { "acquire", "release", "acquire" }, mc.Order.ToArray());
                net.OnEngineStopping();
                Assert.Equal(new[] { "acquire", "release", "acquire", "release" }, mc.Order.ToArray());
                net.OnEngineStopping();                             // already released
                Assert.Equal(2, mc.Releases);
            }
        }

        [Fact]
        public void TheMulticastLockIsNeverTakenWhenUpnpIsOff()
        {
            var mc = new CountingLock();
            var net = Services(new UserSettings { UpnpEnabled = false }, mc);
            using (var host = Host(new FakePortMapper()))
            {
                net.OnEngineStarted(host); net.OnEngineStopping();
                Assert.Equal(0, mc.Acquires); Assert.Equal(0, mc.Releases);
            }
        }

        [Fact]
        public void DisposingReleasesAHeldMulticastLock()
        {
            var mc = new CountingLock();
            var net = Services(new UserSettings { UpnpEnabled = true }, mc);
            using (var host = Host(new FakePortMapper()))
            {
                net.OnEngineStarted(host);
                net.Dispose();
                Assert.Equal(new[] { "acquire", "release" }, mc.Order.ToArray());
                net.Dispose();
                Assert.Equal(1, mc.Releases);
            }
        }

        [Fact]
        public void APlatformWithoutAMulticastLockChangesNothing()
        {
            var net = Services(new UserSettings { UpnpEnabled = true }, null);
            using (var host = Host(new FakePortMapper())) { net.OnEngineStarted(host); net.OnEngineStopping(); net.Dispose(); }
        }

        [Fact]
        public void AFailingMulticastLockDoesNotBreakTheEngineStart()
        {
            var net = Services(new UserSettings { UpnpEnabled = true }, new ThrowingLock());
            using (var host = Host(new FakePortMapper())) { net.OnEngineStarted(host); net.OnEngineStopping(); }
        }

        private sealed class ThrowingLock : IMulticastLock
        {
            public void Acquire() { throw new InvalidOperationException("wifi off"); }
            public void Release() { throw new InvalidOperationException("wifi off"); }
        }
    }
}
