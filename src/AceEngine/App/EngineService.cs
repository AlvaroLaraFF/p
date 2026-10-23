using System;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.App
{
    /// <summary>Owns the AceEngineHost (local HTTP server, DHT, listener). Created on first use and released when the app pauses.</summary>
    public sealed class EngineService : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<EngineOptions> _build;
        private readonly Action<AceEngineHost> _started;
        private AceEngineHost _host;

        public EngineService(Func<AppConfig> config, string dataDir) : this(() => EngineOptionsFactory.Build(config(), dataDir)) { }

        /// <param name="build">Builds the engine options each time it has to be created (with the user's current settings).</param>
        /// <param name="started">Called with the freshly started engine (to hook UPnP, etc. to it).</param>
        public EngineService(Func<EngineOptions> build, Action<AceEngineHost> started = null) { _build = build; _started = started; }

        public AceEngineHost Host
        {
            get
            {
                lock (_gate)
                {
                    if (_host == null)
                    {
                        var opt = _build();
                        _host = new AceEngineHost(opt);
                        _host.Start();
                        if (_started != null) { try { _started(_host); } catch (Exception ex) { AceLog.Warn("engine", "on start: " + ex.Message); } }
                        AceLog.Info("engine", "engine started: local server on 127.0.0.1:" + _host.Server.Port + ", P2P listening on " + _host.ListenPort
                            + ", VOD buffer " + (opt.MaxBufferBytes >> 20) + " MiB, live " + (opt.Live.MaxBufferBytes >> 20) + " MiB, upload " + opt.Live.UploadSlots + " slots / " + opt.UploadMaxBytesPerSec + " B/s");
                    }
                    return _host;
                }
            }
        }

        public bool IsRunning { get { lock (_gate) return _host != null; } }

        public void Shutdown()
        {
            AceEngineHost h;
            lock (_gate) { h = _host; _host = null; }
            if (h != null)
            {
                try { h.Dispose(); } catch (Exception ex) { AceLog.Warn("engine", "Dispose: " + ex.Message); }
                AceLog.Info("engine", "engine stopped");
            }
        }

        public void Dispose() { Shutdown(); }
    }
}
