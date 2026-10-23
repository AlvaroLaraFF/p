using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.P2P;
using AceEngine.Streaming;
using AceEngine.Util;

namespace AceEngine.Cast
{
    public enum CastState
    {
        Idle,
        /// <summary>Opening the channel (resolving it and connecting to the swarm).</summary>
        Opening,
        /// <summary>Reading the first seconds to see the codecs.</summary>
        Analyzing,
        /// <summary>Compatible: cutting the first segments and opening the LAN server.</summary>
        Starting,
        /// <summary>The URL has been handed over and the server is up.</summary>
        Casting,
        /// <summary>The channel cannot be sent (see the verdict).</summary>
        Incompatible,
        /// <summary>Stopped by the caller, by the idle timeout or because the channel ended.</summary>
        Stopped,
        Failed
    }

    public enum CastFailure
    {
        None, Incompatible, InvalidTarget, NoRoute, OpenFailed, NoData, NotSegmentable, TimeshiftDisabled, Cancelled, ServerError, Busy
    }

    /// <summary>How the receiver gets the channel.</summary>
    public enum CastDelivery
    {
        /// <summary>HLS live playlist and TS segments: what Google Cast needs.</summary>
        Hls,
        /// <summary>The MPEG-TS as it comes, progressive: what DLNA renderers take.</summary>
        ProgressiveTs
    }

    /// <summary>What to send and where.</summary>
    public sealed class CastRequest
    {
        /// <summary>The channel to open. Either this or <see cref="Session"/>.</summary>
        public AceLink Link;
        /// <summary>A live session that is already open (the Cast source does not dispose it).</summary>
        public LiveSession Session;
        /// <summary>Name of the channel, for the receiver's screen.</summary>
        public string Name = "";
        /// <summary>The receiver's IPv4 address (CastDevice.getInetAddress on Android, the SSDP source for a DLNA renderer).</summary>
        public IPAddress Target;
        /// <summary>Also accept connections from the rest of the local network.</summary>
        public bool AllowLocalNetwork;
        public CastProfile Profile = CastProfile.Chromecast2;
        public CastDelivery Delivery = CastDelivery.Hls;
        /// <summary>Progressive only: the DLNA headers and body mode this renderer wants.</summary>
        public DlnaStreamHeaders Dlna;
        public ProgressiveMode Progressive = ProgressiveMode.CloseDelimited;
        public string ProgressiveContentType = "video/mp2t";
    }

    /// <summary>The outcome of <see cref="ICastSource.StartAsync"/>.</summary>
    public sealed class CastResult
    {
        public bool Ok;
        public CastFailure Failure;
        /// <summary>Why it did not start, for the user (Spanish).</summary>
        public string Reason = "";
        /// <summary>Technical detail for the log (English).</summary>
        public string Detail = "";
        /// <summary>The URL to hand to the receiver (LOAD request for Cast, SetAVTransportURI for DLNA).</summary>
        public string Url = "";
        /// <summary>HLS: the media playlist itself (Url is a master playlist that points at it when the codecs are known). Same as Url otherwise.</summary>
        public string MediaPlaylistUrl = "";
        /// <summary>HLS: "application/vnd.apple.mpegurl" ("application/x-mpegURL" is the same thing for Cast); progressive: "video/mp2t".</summary>
        public string ContentType = "";
        /// <summary>The stream is live: Cast needs StreamType.LIVE and an unknown duration.</summary>
        public bool IsLive = true;
        public string Title = "";
        public CastVerdict Verdict;
    }

    public sealed class CastStats
    {
        public CastState State;
        public string Reason = "";
        // the channel
        public int Peers;
        public double DownloadBytesPerSec, UploadBytesPerSec;
        public int BufferPercent;
        /// <summary>Fill of the live buffer (0..1).</summary>
        public double BufferFill;
        /// <summary>How far the engine's own position is behind the head of the live stream (seconds), as the swarm reports it.</summary>
        public double SwarmLagSeconds;
        // HLS
        public long SegmentsPublished;
        public int SegmentsListed;
        public double WindowSeconds, LastSegmentSeconds;
        /// <summary>Media time between the edge of the live buffer and the end of the newest segment: the part of the delay that is the packaging.</summary>
        public double PlaylistBehindEdgeSeconds;
        public int TargetDuration;
        /// <summary>Access units whose NAL units were put back in the standard order (see AnnexBFix).</summary>
        public long NalOrderFixes;
        // server
        public long Requests, PlaylistRequests, SegmentRequests, StreamRequests, BytesSent, RejectedConnections;
        public int ActiveConnections;
        /// <summary>Seconds since the receiver last asked for anything (-1 = never).</summary>
        public double SecondsSinceLastRequest = -1;
        public string LastClient = "";
        public string Url = "";
    }

    /// <summary>
    /// What the shell needs from the engine to send a channel to another screen. It has no platform dependencies: the shell finds the device (Google Cast SDK,
    /// DLNA discovery), asks for a URL here, and tells the device to load it.
    ///
    /// Google Cast contract (Android shell). Normally the shell talks to <see cref="CastService"/>, which wraps this and adds the DLNA targets; the contract for Cast is the same.
    ///  1. The user picks a Chromecast (MediaRouter). Its IPv4 address is CastDevice.getInetAddress(); the shell adds it to CastService.Targets (kind GoogleCast, Capabilities Hls, Profile
    ///     CastProfile.Chromecast2 / Chromecast3 / GoogleTv according to the model).
    ///  2. Stop the local playback of that channel (the engine allows one session per channel), then await StartAsync(new CastRequest { Link = channelLink, Name = channelName, Target = deviceAddress }).
    ///     Ok = false with Failure = Incompatible carries Reason ("audio MP2") to show; nothing else is needed from the user. It takes 5-10 s to open the channel and cut the first segments
    ///     (a spinner is in order). CheckAsync / CastService.CheckAsync tell beforehand, for greying out the entry.
    ///  3. LOAD with the result: contentUrl = Url (a master playlist that carries CODECS/RESOLUTION; MediaPlaylistUrl is the media playlist itself, for a fallback if a receiver dislikes the master),
    ///     contentType = ContentType ("application/x-mpegURL" is the same thing), StreamType.LIVE, duration unknown (-1), metadata title = Title, autoplay on.
    ///     (RemoteMediaClient.load(MediaLoadRequestData).) The playlist keeps 8 segments of 4 s: seeking back is limited to that window; "go to live" is a seek to the infinite position.
    ///  4. Keep the engine alive while casting (a foreground service of type mediaPlayback, a Wi-Fi lock): the server and the P2P download run in this process.
    ///  5. When the Cast session ends (or the user stops it) call Stop(). The server also closes itself after CastSourceOptions.IdleTimeout without a request (KeepAlive() postpones it).
    ///  6. The Chromecast fetches over the LAN, on the address of the interface that reaches it: the phone and the Chromecast must be on the same network without client isolation. The sending is
    ///     about 16 s behind the engine's live edge (Shaka starts three segments from the end).
    /// </summary>
    public interface ICastSource : IDisposable
    {
        CastState State { get; }
        /// <summary>Verdict of the current or last channel (null before one).</summary>
        CastVerdict Verdict { get; }

        /// <summary>Opens the channel, checks it can be sent, and brings the server up. Only one channel is sent at a time: starting another stops the first.</summary>
        Task<CastResult> StartAsync(CastRequest request, CancellationToken ct);

        /// <summary>Verdict for a channel without sending it: opens it briefly (a few seconds of P2P) and closes it. The answer is remembered for a while.</summary>
        Task<CastVerdict> CheckAsync(AceLink link, CastProfile profile, CancellationToken ct);

        /// <summary>The remembered verdict for a channel, if one was found less than CastSourceOptions.VerdictTtl ago.</summary>
        bool TryGetKnownVerdict(AceLink link, out CastVerdict verdict);

        /// <summary>Stops sending: closes the server, and the channel if this source opened it. Idempotent.</summary>
        void Stop();

        /// <summary>The receiver is known to be alive (the shell has a Cast session): postpones the idle timeout.</summary>
        void KeepAlive();

        CastStats GetStats();

        /// <summary>The state changed (thread-pool thread).</summary>
        event Action<CastState> StateChanged;
    }

    public sealed class CastSourceOptions
    {
        /// <summary>RAM of the live buffer of a channel opened for sending: the segments live in it, so it must hold the window plus a reserve.</summary>
        public long TimeshiftBytes = 64L * 1024 * 1024;
        /// <summary>RAM when opening only to look at the codecs.</summary>
        public long CheckTimeshiftBytes = 8L * 1024 * 1024;
        public HlsOptions Hls = new HlsOptions();
        /// <summary>The channel must send data within this time of being opened.</summary>
        public TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
        /// <summary>After the first bytes, the codecs must show within this time.</summary>
        public TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Segments needed before the URL is handed out: a receiver starts a few segments from the end of the list.</summary>
        public int MinSegmentsToStart = 3;
        public TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
        /// <summary>No request from the receiver for this long: the server closes and the sending stops.</summary>
        public TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
        public int MaxConnections = 6;
        public TimeSpan VerdictTtl = TimeSpan.FromHours(6);
        /// <summary>Sending to an address outside the local network is refused unless this is set (tests).</summary>
        public bool AllowAnyTarget;
    }

    /// <summary>
    /// The sending service: opens a channel, decides whether the receiver can take it, cuts HLS (or hands the TS over as it is), and serves it to that one device.
    /// </summary>
    public sealed class CastSource : ICastSource
    {
        private const string Tag = "cast";
        private readonly Func<AceEngineHost> _host;
        private readonly INetworkInfo _net;
        private readonly CastSourceOptions _o;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, KeyValuePair<DateTime, CastVerdict>> _known = new ConcurrentDictionary<string, KeyValuePair<DateTime, CastVerdict>>();
        private Attempt _cur;
        private CastState _state = CastState.Idle;
        private CastVerdict _verdict;
        private string _reason = "";
        private bool _disposed;

        /// <param name="host">The engine (EngineService.Host); asked for only when a channel has to be opened.</param>
        /// <param name="net">Network information of the platform; null = System.Net.NetworkInformation.</param>
        public CastSource(Func<AceEngineHost> host, INetworkInfo net = null, CastSourceOptions options = null)
        {
            _host = host;
            _net = net ?? SystemNetworkInfo.Default;
            _o = options ?? new CastSourceOptions();
        }

        public event Action<CastState> StateChanged;

        public CastState State { get { lock (_gate) return _state; } }
        public CastVerdict Verdict { get { lock (_gate) return _verdict; } }
        public CastSourceOptions Options { get { return _o; } }

        private void SetState(CastState s, string reason = "")
        {
            Action<CastState> h;
            lock (_gate)
            {
                if (_state == s && _reason == reason) return;
                _state = s; _reason = reason;
                h = StateChanged;
            }
            AceLog.Info(Tag, "state: " + s + (reason.Length > 0 ? " (" + reason + ")" : ""));
            if (h != null) { try { h(s); } catch (Exception ex) { AceLog.Warn(Tag, "StateChanged handler: " + ex.Message); } }
        }

        /// <summary>Everything one sending owns, so that it can be torn down in one go.</summary>
        private sealed class Attempt
        {
            public CancellationTokenSource Cts = new CancellationTokenSource();
            public LiveSession Session;
            public bool OwnsSession, PrevHold;
            public HlsSegmenter Segmenter;
            public LanServer Server;
            public Timer Watch;
            public CastRequest Request;
            public string Url = "";
            public int Stopped;
        }

        // ---------- Start ----------

        public async Task<CastResult> StartAsync(CastRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            Attempt a;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("CastSource");
                if (_cur != null) { var old = _cur; _cur = null; Teardown(old, "replaced by another channel"); }
                a = new Attempt { Request = request };
                _cur = a;
            }
            _known.Clear();
            using (var link = CancellationTokenSource.CreateLinkedTokenSource(ct, a.Cts.Token))
            {
                try
                {
                    return await RunStartAsync(a, request, link.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    FinishFailed(a, CastState.Stopped, "cancelled");
                    return Fail(CastFailure.Cancelled, "Cancelado", "cancelled", null);
                }
                catch (Exception ex)
                {
                    AceLog.Warn(Tag, "start failed: " + ex.GetType().Name + ": " + ex.Message);
                    FinishFailed(a, CastState.Failed, ex.Message);
                    return Fail(CastFailure.OpenFailed, "No se ha podido abrir el canal: " + ex.Message, ex.GetType().Name + ": " + ex.Message, null);
                }
            }
        }

        private static CastResult Fail(CastFailure f, string reason, string detail, CastVerdict v)
        {
            return new CastResult { Ok = false, Failure = f, Reason = reason, Detail = detail, Verdict = v };
        }

        private void FinishFailed(Attempt a, CastState state, string reason)
        {
            lock (_gate) { if (_cur == a) _cur = null; }
            Teardown(a, reason);
            SetState(state, reason);
        }

        private async Task<CastResult> RunStartAsync(Attempt a, CastRequest req, CancellationToken ct)
        {
            var profile = req.Profile ?? CastProfile.Chromecast2;
            // ---- the receiver and the way to it ----
            var target = req.Target;
            if (target == null || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || target.Equals(IPAddress.Any))
            {
                FinishFailed(a, CastState.Failed, "no target"); return Fail(CastFailure.InvalidTarget, "Dirección del dispositivo no válida", "target address missing or not IPv4", null);
            }
            if (!_o.AllowAnyTarget && !IpClass.IsLanLike(target))
            {
                FinishFailed(a, CastState.Failed, "target not on the LAN"); return Fail(CastFailure.InvalidTarget, "El dispositivo no está en tu red local", "target " + target + " is not a local network address", null);
            }
            var bind = _net.LocalAddressFor(target);
            if (bind == null || (IpClass.IsLoopback(bind) && !IpClass.IsLoopback(target)))
            {
                FinishFailed(a, CastState.Failed, "no route"); return Fail(CastFailure.NoRoute, "No hay ruta de red hasta el dispositivo", "no local address reaches " + target, null);
            }
            IPAddress mask = null;
            foreach (var i in _net.GetInterfaces()) if (i.Address.Equals(bind)) mask = i.Mask;

            // ---- the channel ----
            SetState(CastState.Opening);
            if (req.Session != null)
            {
                a.Session = req.Session; a.OwnsSession = false;
            }
            else if (req.Link != null)
            {
                var host = _host();
                var opened = await host.OpenStreamAsync(req.Link, new OpenOptions { TimeshiftBytes = _o.TimeshiftBytes }, ct).ConfigureAwait(false);
                var live = opened.Session as LiveSession;
                if (live == null)
                {
                    if (opened.Session != null) { try { opened.Session.Dispose(); } catch { } }
                    FinishFailed(a, CastState.Failed, "not a live stream");
                    return Fail(CastFailure.OpenFailed, "Solo se pueden enviar canales en directo", "the link did not open as a live stream", null);
                }
                a.Session = live; a.OwnsSession = true;
            }
            else throw new ArgumentException("the request needs a Link or a Session");
            var hold = a.Session as ISessionHold;
            if (hold != null) { a.PrevHold = hold.HoldOpen; a.Session.HoldOpen = true; }             // the local server must not reap a session nobody reads locally

            var buf = a.Session.TimeshiftBuffer;
            bool hls = req.Delivery == CastDelivery.Hls;
            if (hls && buf == null)
            {
                FinishFailed(a, CastState.Failed, "no live buffer");
                return Fail(CastFailure.TimeshiftDisabled, "El directo no guarda memoria", "the session has no live buffer; HLS segments live in it", null);
            }

            // ---- can the receiver take it? ----
            CastVerdict verdict = null;
            if (buf != null)
            {
                SetState(CastState.Analyzing);
                verdict = await CastAnalysis.AnalyzeSessionAsync(a.Session, profile, _o.OpenTimeout, _o.AnalysisTimeout, ct).ConfigureAwait(false);
                lock (_gate) _verdict = verdict;
                if (req.Link != null) Remember(req.Link, verdict);
                AceLog.Info(Tag, "verdict: " + verdict);
                if (hls)
                {
                    if (verdict.IsIncompatible)
                    {
                        FinishFailed(a, CastState.Incompatible, verdict.Reason);
                        SetState(CastState.Incompatible, verdict.Reason);
                        return Fail(CastFailure.Incompatible, "Este canal no se puede enviar al " + profile.Name + ": " + verdict.Reason, verdict.ToString(), verdict);
                    }
                    if (verdict.Support == CastSupport.Unknown)
                    {
                        FinishFailed(a, CastState.Failed, "no data");
                        return Fail(CastFailure.NoData, "El canal no ha enviado datos", verdict.ToString(), verdict);
                    }
                }
            }

            // ---- the content ----
            SetState(CastState.Starting);
            var content = new LanContent(a.Session, req.Delivery);
            if (hls)
            {
                var ho = CloneHls(_o.Hls);
                a.Segmenter = new HlsSegmenter(buf, ho);
                a.Segmenter.Start();
                content.Segmenter = a.Segmenter;
                content.Master = MasterFor(verdict, buf);
                var ready = await WaitForSegmentsAsync(a, ct).ConfigureAwait(false);
                if (ready != null) { FinishFailed(a, CastState.Failed, ready.Detail); ready.Verdict = verdict; return ready; }
            }

            // ---- the server ----
            var so = new LanServerOptions
            {
                BindAddress = bind, Port = 0, Target = target, AllowLocalNetwork = req.AllowLocalNetwork, LocalNetworkMask = mask,
                MaxConnections = _o.MaxConnections, IdleTimeout = _o.IdleTimeout,
                Progressive = req.Progressive, ProgressiveContentType = req.ProgressiveContentType, Dlna = req.Dlna
            };
            try
            {
                a.Server = new LanServer(so, content);
                a.Server.Closed += reason => OnServerClosed(a, reason);
                a.Server.Start();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is ArgumentException)
            {
                FinishFailed(a, CastState.Failed, ex.Message);
                return Fail(CastFailure.ServerError, "No se ha podido abrir el servidor local", ex.GetType().Name + ": " + ex.Message, verdict);
            }
            a.Url = hls ? (content.GetMasterPlaylist() != null ? a.Server.MasterUrl : a.Server.PlaylistUrl) : a.Server.StreamUrl;
            a.Watch = new Timer(_ => WatchAttempt(a), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            SetState(CastState.Casting);
            return new CastResult
            {
                Ok = true, Url = a.Url, MediaPlaylistUrl = hls ? a.Server.PlaylistUrl : a.Url, Title = req.Name ?? "", Verdict = verdict, IsLive = true,
                ContentType = hls ? "application/vnd.apple.mpegurl" : req.ProgressiveContentType
            };
        }

        /// <summary>The master playlist text for what the verdict read of the stream, or null if the SPS was not seen (the media playlist is then served alone).</summary>
        internal static string MasterFor(CastVerdict verdict, TimeshiftBuffer buf)
        {
            var info = verdict == null ? null : verdict.Stream;
            if (info == null || info.Sps == null) return null;
            string codecs = HlsPlaylist.AvcCodec(info.Sps);
            string audio = HlsPlaylist.AudioCodec(info);
            if (audio != null) codecs += "," + audio;
            var snap = buf == null ? null : buf.Snapshot();
            double bps = snap != null && snap.Buffered.TotalSeconds > 2 ? snap.BufferedBytes * 8.0 / snap.Buffered.TotalSeconds : 4000000;
            return HlsPlaylist.BuildMaster((int)Math.Min(int.MaxValue, bps * 1.1), info.Sps.Width, info.Sps.Height, info.Sps.Fps, codecs, "index.m3u8");
        }

        private static HlsOptions CloneHls(HlsOptions o)
        {
            return new HlsOptions
            {
                TargetSegmentSeconds = o.TargetSegmentSeconds, MinSegmentSeconds = o.MinSegmentSeconds, MaxSegmentSeconds = o.MaxSegmentSeconds, WindowSegments = o.WindowSegments,
                RetainedSegments = o.RetainedSegments, ProgramDateTime = o.ProgramDateTime, StartLookbackSeconds = o.StartLookbackSeconds, ReserveFraction = o.ReserveFraction,
                MaxSegmentBytes = o.MaxSegmentBytes, NormalizeNalOrder = o.NormalizeNalOrder, UtcNow = o.UtcNow
            };
        }

        /// <summary>Waits for enough segments to start a receiver on. Returns null when ready, or the failure to hand back.</summary>
        private async Task<CastResult> WaitForSegmentsAsync(Attempt a, CancellationToken ct)
        {
            var seg = a.Segmenter;
            var limit = DateTime.UtcNow + _o.StartTimeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var signal = seg.Changed.Wait();
                seg.Process();
                if (seg.Failed)
                    return Fail(CastFailure.NotSegmentable, "No se puede enviar este canal: " + (seg.FailureCode == CastIssueCode.NoKeyframes ? "el vídeo no tiene fotogramas clave" : seg.FailureReason), "segmenter: " + seg.FailureReason, null);
                int n; double secs;
                seg.GetWindow(out n, out secs);
                if (n >= _o.MinSegmentsToStart) return null;                                   // RFC 8216 6.3.3: a client starts three target durations from the end
                if (DateTime.UtcNow > limit)
                {
                    if (n >= 1) return null;
                    return Fail(CastFailure.NoData, "El canal no produce datos suficientes", "no segment after " + (int)_o.StartTimeout.TotalSeconds + " s", null);
                }
                await Task.WhenAny(signal, Task.Delay(250, ct)).ConfigureAwait(false);
            }
        }

        // ---------- Running ----------

        private void WatchAttempt(Attempt a)
        {
            try
            {
                lock (_gate) { if (_cur != a || Volatile.Read(ref a.Stopped) != 0) return; }
                var s = a.Session;
                if (s != null && s.State == SessionState.Stopped)
                {
                    StopAttempt(a, "the channel stopped");
                    return;
                }
                if (a.Segmenter != null && a.Segmenter.Failed && a.Segmenter.FailureCode == CastIssueCode.NoKeyframes) StopAttempt(a, "no key frames");
            }
            catch (Exception ex) { AceLog.Warn(Tag, "watch: " + ex.Message); }
        }

        private void OnServerClosed(Attempt a, string reason)
        {
            StopAttempt(a, reason);
        }

        private void StopAttempt(Attempt a, string reason)
        {
            bool mine;
            lock (_gate) { mine = _cur == a; if (mine) _cur = null; }
            Teardown(a, reason);
            if (mine) SetState(CastState.Stopped, reason);
        }

        public void Stop()
        {
            Attempt a;
            lock (_gate) { a = _cur; _cur = null; }
            if (a == null) return;
            Teardown(a, "stopped");
            SetState(CastState.Stopped, "stopped");
        }

        private void Teardown(Attempt a, string reason)
        {
            if (Interlocked.Exchange(ref a.Stopped, 1) != 0) return;
            try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
            try { if (a.Server != null) a.Server.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "server dispose: " + ex.Message); }
            try { if (a.Segmenter != null) a.Segmenter.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "segmenter dispose: " + ex.Message); }
            var t = a.Watch; a.Watch = null; if (t != null) t.Dispose();
            var s = a.Session;
            if (s != null)
            {
                if (a.OwnsSession) { try { s.Dispose(); } catch (Exception ex) { AceLog.Warn(Tag, "session dispose: " + ex.Message); } }
                else if (s.State != SessionState.Stopped) { try { s.HoldOpen = a.PrevHold; } catch { } }
            }
            AceLog.Info(Tag, "teardown: " + reason);
        }

        public void KeepAlive()
        {
            Attempt a;
            lock (_gate) a = _cur;
            if (a != null && a.Server != null) a.Server.Touch();
        }

        // ---------- Probe ----------

        public async Task<CastVerdict> CheckAsync(AceLink link, CastProfile profile, CancellationToken ct)
        {
            if (link == null) throw new ArgumentNullException("link");
            profile = profile ?? CastProfile.Chromecast2;
            CastVerdict known;
            if (TryGetKnownVerdict(link, out known)) return known;
            LiveSession live = null;
            try
            {
                var host = _host();
                OpenedStream opened;
                try { opened = await host.OpenStreamAsync(link, new OpenOptions { TimeshiftBytes = _o.CheckTimeshiftBytes }, ct).ConfigureAwait(false); }
                catch (InvalidOperationException)
                {
                    // the engine allows one session per channel: it is already open (the player has it). The caller has a better way: CastService.CheckOpenSessionAsync.
                    var open = new CastVerdict { Profile = profile, Support = CastSupport.Unknown };
                    open.Issues.Add(new CastIssue { Code = CastIssueCode.Other, Blocking = false, Message = "el canal ya está abierto", Detail = "a session of this channel is already open; analyze that one" });
                    return open;
                }
                live = opened.Session as LiveSession;
                if (live == null)
                {
                    if (opened.Session != null) opened.Session.Dispose();
                    var v = new CastVerdict { Profile = profile, Support = CastSupport.Incompatible };
                    v.Issues.Add(new CastIssue { Code = CastIssueCode.Other, Blocking = true, Message = "no es un canal en directo", Detail = "not a live stream" });
                    return v;
                }
                var verdict = await CastAnalysis.AnalyzeSessionAsync(live, profile, _o.OpenTimeout, _o.AnalysisTimeout, ct).ConfigureAwait(false);
                if (verdict.Support != CastSupport.Unknown) Remember(link, verdict);
                return verdict;
            }
            finally
            {
                if (live != null) { try { live.Dispose(); } catch { } }
            }
        }

        private static string Key(AceLink l) { return l.Kind + ":" + l.Value; }

        private void Remember(AceLink link, CastVerdict v)
        {
            if (link == null || v == null || v.Support == CastSupport.Unknown) return;
            _known[Key(link)] = new KeyValuePair<DateTime, CastVerdict>(DateTime.UtcNow, v);
        }

        public bool TryGetKnownVerdict(AceLink link, out CastVerdict verdict)
        {
            verdict = null;
            KeyValuePair<DateTime, CastVerdict> e;
            if (link == null || !_known.TryGetValue(Key(link), out e)) return false;
            if (DateTime.UtcNow - e.Key > _o.VerdictTtl) { _known.TryRemove(Key(link), out e); return false; }
            verdict = e.Value;
            return true;
        }

        // ---------- Statistics ----------

        public CastStats GetStats()
        {
            Attempt a;
            CastState st; string reason;
            lock (_gate) { a = _cur; st = _state; reason = _reason; }
            var r = new CastStats { State = st, Reason = reason };
            if (a == null) return r;
            r.Url = a.Url;
            try
            {
                var s = a.Session;
                if (s != null)
                {
                    var ss = s.Stats;
                    r.Peers = ss.Peers; r.DownloadBytesPerSec = ss.DownloadBytesPerSec; r.UploadBytesPerSec = ss.UploadBytesPerSec; r.BufferPercent = ss.BufferPercent;
                    r.SwarmLagSeconds = s.LiveInfo.LagSeconds;
                    var b = s.TimeshiftBuffer;
                    if (b != null && b.CapacityBytes > 0) r.BufferFill = Math.Min(1.0, (double)(b.WriteOffset - b.OldestOffset) / b.CapacityBytes);
                }
                var seg = a.Segmenter;
                if (seg != null)
                {
                    var hs = seg.GetStats();
                    r.SegmentsPublished = hs.SegmentsPublished; r.SegmentsListed = (int)hs.SegmentsListed; r.WindowSeconds = hs.WindowSeconds; r.LastSegmentSeconds = hs.LastSegmentSeconds;
                    r.PlaylistBehindEdgeSeconds = hs.PlaylistBehindEdgeSeconds; r.TargetDuration = hs.TargetDuration; r.NalOrderFixes = hs.NalOrderFixes;
                }
                var srv = a.Server;
                if (srv != null)
                {
                    var ls = srv.GetStats();
                    r.Requests = ls.Requests; r.PlaylistRequests = ls.PlaylistRequests; r.SegmentRequests = ls.SegmentRequests; r.StreamRequests = ls.StreamRequests; r.BytesSent = ls.BytesSent;
                    r.RejectedConnections = ls.RejectedAddress + ls.OverLimit; r.ActiveConnections = ls.ActiveConnections; r.LastClient = ls.LastClient;
                    r.SecondsSinceLastRequest = ls.Requests > 0 ? (DateTime.UtcNow - ls.LastRequestUtc).TotalSeconds : -1;
                }
            }
            catch (ObjectDisposedException) { }
            return r;
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            Stop();
        }

        // ---------- What the server serves ----------

        private sealed class LanContent : ILanContent
        {
            private readonly LiveSession _session;
            private readonly CastDelivery _delivery;
            public HlsSegmenter Segmenter;
            public string Master;

            public LanContent(LiveSession session, CastDelivery delivery) { _session = session; _delivery = delivery; }

            public bool OffersHls { get { return _delivery == CastDelivery.Hls && Segmenter != null; } }
            public bool OffersProgressive { get { return _delivery == CastDelivery.ProgressiveTs; } }
            public bool IsAvailable { get { var st = _session.State; return st != SessionState.Stopped && st != SessionState.Failed; } }

            public string GetMasterPlaylist() { return Master; }
            public string GetPlaylist(out bool ended) { ended = false; return Segmenter == null ? null : Segmenter.GetPlaylist(out ended); }
            public SegmentLease GetSegment(long seq) { return Segmenter == null ? null : Segmenter.GetSegment(seq); }

            public Task<IStreamReader> OpenProgressiveAsync(TimeSpan? back, CancellationToken ct)
            {
                if (_session.TimeshiftEnabled) return _session.OpenReaderBehindAsync(back, ct);
                return _session.OpenReaderAsync(0, ct);
            }
        }
    }
}
