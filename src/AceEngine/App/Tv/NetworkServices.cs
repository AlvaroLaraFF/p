using System;
using AceEngine.Net;
using AceEngine.P2P;

namespace AceEngine.App.Tv
{
    /// <summary>State of the network functions in Settings: UPnP (port opened on the router) and TSService (server response).</summary>
    public sealed class NetworkServices : INetworkServices, IDisposable
    {
        private readonly UserSettings _settings;
        private readonly TsServiceMonitor _ts;
        private readonly NetworkStatus _status = new NetworkStatus();
        private readonly object _gate = new object();
        private readonly IMulticastLock _multicast;
        private bool _multicastHeld;

        public event Action Changed;

        /// <param name="multicast">The platform's multicast lock, taken while UPnP is wanted and the engine runs (null = the platform does not need one).</param>
        public NetworkServices(UserSettings settings, TsServiceMonitor ts, IMulticastLock multicast = null)
        {
            _settings = settings; _ts = ts; _multicast = multicast;
            _ts.Changed += Refresh;
            Refresh();
        }

        private AceEngineHost _host;

        public NetworkStatus Status
        {
            get { lock (_gate) return new NetworkStatus { Upnp = _status.Upnp, UpnpPort = _status.UpnpPort, ListenPort = _status.ListenPort, TsService = _status.TsService }; }
        }

        public string Describe() { var s = Status; return "upnp=" + s.Upnp + (s.UpnpPort > 0 ? ":" + s.UpnpPort : "") + " ts=" + s.TsService; }

        // The engine starts/stops (Suspend/Resume): UPnP lives with the engine's P2P listener.
        public void OnEngineStarted(AceEngineHost host)
        {
            lock (_gate) { _host = host; }
            host.PortMapperChanged += Refresh;
            UpdateMulticast();
            try { host.SetUpnpEnabled(_settings.UpnpEnabled); } catch (Exception ex) { AceEngine.Util.AceLog.Warn("net", "UPnP: " + ex.Message); }
            Refresh();
        }

        public void OnEngineStopping()
        {
            AceEngineHost h;
            lock (_gate) { h = _host; _host = null; }
            if (h != null) h.PortMapperChanged -= Refresh;
            UpdateMulticast();
            Refresh();
        }

        public void OnSettingsChanged()
        {
            AceEngineHost h;
            lock (_gate) h = _host;
            UpdateMulticast();
            if (h != null) { try { h.SetUpnpEnabled(_settings.UpnpEnabled); } catch (Exception ex) { AceEngine.Util.AceLog.Warn("net", "UPnP: " + ex.Message); } }
            Refresh();
        }

        /// <summary>Multicast is needed (SSDP search for the router) only while UPnP is wanted and the engine is running: acquire and release in pairs, as that changes.</summary>
        private void UpdateMulticast()
        {
            if (_multicast == null) return;
            bool acquire = false, release = false;
            lock (_gate)
            {
                bool want = _host != null && _settings.UpnpEnabled;
                if (want && !_multicastHeld) { _multicastHeld = true; acquire = true; }
                else if (!want && _multicastHeld) { _multicastHeld = false; release = true; }
            }
            try
            {
                if (acquire) _multicast.Acquire();
                else if (release) _multicast.Release();
            }
            catch (Exception ex) { AceEngine.Util.AceLog.Warn("net", "multicast lock: " + ex.Message); }
        }

        private void Refresh()
        {
            bool changed;
            lock (_gate)
            {
                UpnpState upnp; int port = 0;
                int listen = _host != null ? SafeListenPort(_host) : 0;
                if (!_settings.UpnpEnabled) upnp = UpnpState.Disabled;
                else if (_host == null) upnp = UpnpState.Searching;          // the engine has not started yet (it starts on first use)
                else { upnp = _host.UpnpState; if (upnp == UpnpState.Open) port = _host.UpnpOpenPort; }
                var tsState = _ts.State;
                changed = upnp != _status.Upnp || port != _status.UpnpPort || tsState != _status.TsService || listen != _status.ListenPort;
                _status.Upnp = upnp; _status.UpnpPort = port; _status.TsService = tsState; _status.ListenPort = listen;
            }
            if (changed) { var h = Changed; if (h != null) h(); }
        }

        private static int SafeListenPort(AceEngineHost h) { try { return h.ListenPort; } catch (Exception) { return 0; } }

        public void Dispose()
        {
            _ts.Changed -= Refresh;
            bool release;
            lock (_gate) { release = _multicastHeld; _multicastHeld = false; }
            if (release && _multicast != null) { try { _multicast.Release(); } catch (Exception ex) { AceEngine.Util.AceLog.Warn("net", "multicast lock: " + ex.Message); } }
        }
    }
}
