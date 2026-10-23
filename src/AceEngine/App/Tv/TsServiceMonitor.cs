using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    public sealed class TsServiceMonitorOptions
    {
        /// <summary>CONSECUTIVE failed attempts (real requests to TSService hosts) to move to NoResponse.</summary>
        public int FailuresForNoResponse = 3;
        /// <summary>Minimum spacing between active checks (since the last test of any kind, real or active).</summary>
        public TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
        /// <summary>Hosts that an active check tries at most: with the service alive it is a single request.</summary>
        public int CheckMaxHosts = 3;
    }

    /// <summary>
    /// State of TSService for Settings (TsServiceState): Disabled (switch off), Unknown (enabled and no tests yet), Responding or NoResponse.
    /// It is fed from two sources:
    ///  1) the REAL requests of TsServiceClient when resolving a link (Attach(EngineOptions) or Record): a valid file or a well-formed XML = the
    ///     service answers; empty response (HTTP 200 with no body, what the servers answer today), HTTP error, timeout, network error or
    ///     garbage = failure. FailuresForNoResponse consecutive failures (3) give NoResponse; a success puts it back in Responding;
    ///  2) an ACTIVE check (RequestCheck): a request signed for a random id when the switch is turned on and, afterwards, at most every 10 minutes
    ///     since the last test. That way the state is known without having played anything. If all the hosts it tries fail, NoResponse without waiting longer.
    /// "Sin respuesta del servidor" is what the user sees with NoResponse. The instance lives as long as the app, not as the engine: it stays valid if the engine
    /// is recreated. Notifications (Changed) come out on a pool thread, only when the state changes.
    /// </summary>
    public sealed class TsServiceMonitor : IDisposable
    {
        private const string Tag = "tsmonitor";
        private readonly UserSettings _settings;
        private readonly ResolverOptions _resolver;
        private readonly TsServiceMonitorOptions _opt;
        private readonly Func<DateTime> _now;
        private readonly Func<HttpClient> _httpFactory;
        private readonly object _gate = new object();
        private readonly Action _onSettings;
        private bool _enabled, _disposed, _checking;
        private TsServiceState _state;
        private int _failures;
        private DateTime? _lastEvidenceUtc;
        private CancellationTokenSource _checkCts;
        private HttpClient _http;
        private Task _lastCheck = Task.CompletedTask;

        /// <param name="settings">Subscribes to Changed: turning TSService on or off changes the state (and launches a check when turning on).</param>
        /// <param name="resolver">Resolution options (hosts, secret, deadlines) used by the active check: the same as the engine's. By default, the usual ones.</param>
        /// <param name="httpFactory">HTTP client of the active check (by default one of its own, released with the monitor).</param>
        public TsServiceMonitor(UserSettings settings, ResolverOptions resolver = null, TsServiceMonitorOptions options = null, Func<DateTime> utcNow = null, Func<HttpClient> httpFactory = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _resolver = resolver ?? new ResolverOptions();
            _opt = options ?? new TsServiceMonitorOptions();
            _now = utcNow ?? (() => DateTime.UtcNow);
            _httpFactory = httpFactory;
            _enabled = settings.TsServiceEnabled;
            _state = _enabled ? TsServiceState.Unknown : TsServiceState.Disabled;
            _onSettings = OnSettingsChanged;
            settings.Changed += _onSettings;
        }

        public TsServiceState State { get { lock (_gate) return _state; } }

        /// <summary>Changes State (pool thread).</summary>
        public event Action Changed;

        /// <summary>
        /// Makes the real TSService requests of the engine created with these options feed the monitor. Call after EngineOptionsFactory.Build and before
        /// creating the AceEngineHost, every time the engine is recreated.
        /// </summary>
        public void Attach(EngineOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Resolver.TsObserver = Record;
        }

        /// <summary>Records the result of a REAL request to a TSService host. Safe from any thread; never throws.</summary>
        public void Record(TsAttempt attempt)
        {
            if (attempt == null) return;
            bool changed = false;
            lock (_gate)
            {
                if (_disposed || !_enabled) return;
                _lastEvidenceUtc = _now();
                if (attempt.Success) { _failures = 0; changed = SetStateLocked(TsServiceState.Responding); }
                else
                {
                    _failures++;
                    if (_failures >= Math.Max(1, _opt.FailuresForNoResponse)) changed = SetStateLocked(TsServiceState.NoResponse);
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>
        /// Requests an active check in the background. Without 'force' it only happens if TSService is enabled, there is none in progress and CheckInterval
        /// (10 min) has passed since the last test (real or active). Call at app start and when opening Settings. Returns true if it launched one.
        /// </summary>
        public bool RequestCheck(bool force = false)
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || !_enabled || _checking) return false;
                if (!force && _lastEvidenceUtc.HasValue && _now() - _lastEvidenceUtc.Value < _opt.CheckInterval) return false;
                _checking = true;
                _lastEvidenceUtc = _now();
                _checkCts = new CancellationTokenSource();
                token = _checkCts.Token;
                _lastCheck = Task.Run(() => RunCheckAsync(token));
            }
            return true;
        }

        /// <summary>Completes when the active check in progress (or the last one) ends. For tests and to wait on shutdown.</summary>
        public Task WhenCheckDone { get { lock (_gate) return _lastCheck; } }

        private async Task RunCheckAsync(CancellationToken ct)
        {
            bool changed = false;
            try
            {
                var client = new TsServiceClient(_resolver, GetHttp());
                var res = await client.CheckAsync(_opt.CheckMaxHosts, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || !_enabled || ct.IsCancellationRequested) return;
                    _lastEvidenceUtc = _now();
                    if (res.Responding) { _failures = 0; changed = SetStateLocked(TsServiceState.Responding); }
                    else
                    {
                        // All the hosts that were tried failed: with a single active test it is already known that they do not answer
                        _failures = Math.Max(_failures + res.Attempts.Count, Math.Max(1, _opt.FailuresForNoResponse));
                        changed = SetStateLocked(TsServiceState.NoResponse);
                    }
                }
                AceLog.Info(Tag, "active check: " + (res.Responding ? "responding" : "no response") + " (" + res.Attempts.Count + " requests" + (res.Attempts.Count > 0 ? ", last: " + res.Attempts[res.Attempts.Count - 1] : "") + ")");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                AceLog.Warn(Tag, "active check: " + ex.GetType().Name + " " + ex.Message);
            }
            finally { lock (_gate) _checking = false; }
            if (changed) RaiseChanged();
        }

        private HttpClient GetHttp()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("TsServiceMonitor");
                if (_http == null) _http = _httpFactory != null ? _httpFactory() : new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = HttpLimits.ClientBuffer };
                return _http;
            }
        }

        private void OnSettingsChanged()
        {
            bool enabled = _settings.TsServiceEnabled;
            bool changed = false, start = false;
            CancellationTokenSource toCancel = null;
            lock (_gate)
            {
                if (_disposed || enabled == _enabled) return;
                _enabled = enabled;
                _failures = 0;
                _lastEvidenceUtc = null;
                if (enabled) { changed = SetStateLocked(TsServiceState.Unknown); start = true; }
                else { changed = SetStateLocked(TsServiceState.Disabled); toCancel = _checkCts; }
            }
            if (toCancel != null) { try { toCancel.Cancel(); } catch (ObjectDisposedException) { } }
            if (changed) RaiseChanged();
            if (start) RequestCheck(true);                            // when the switch is turned on it is checked right away
        }

        private bool SetStateLocked(TsServiceState s)
        {
            if (_state == s) return false;
            _state = s;
            return true;
        }

        private void RaiseChanged()
        {
            var h = Changed;
            if (h != null) ThreadPool.QueueUserWorkItem(_ => { try { h(); } catch (Exception) { /* a broken listener must not bring the monitor down */ } });
        }

        public void Dispose()
        {
            CancellationTokenSource cts;
            HttpClient http;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                cts = _checkCts; http = _http; _http = null;
            }
            _settings.Changed -= _onSettings;
            if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            if (http != null) http.Dispose();
        }
    }
}
