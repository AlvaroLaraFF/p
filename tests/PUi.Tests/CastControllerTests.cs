using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.Sources;
using PUi.Cast;
using PUi.Platform;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The phone's cast controller against doubles of the engine, of the Google Cast bridge and of what the shell does while sending: no network, no Android.</summary>
    public class CastControllerTests
    {
        // ------------------------------------------------------------------ doubles
        private sealed class FakeEngine : ICastEngine
        {
            public CastTargetList Targets { get; private set; }
            public readonly List<string> Log = new List<string>();
            public List<CastTarget> Found = new List<CastTarget>();
            public int Discoveries;
            public TimeSpan DiscoverTime = TimeSpan.Zero;
            public Func<CastTarget, CastVerdict> Verdict = t => new CastVerdict { Support = CastSupport.Compatible };
            public Exception CheckThrows;
            public Func<CastTarget, CastResult> Result = t => new CastResult { Ok = true, Url = "http://192.168.1.5:9000/c/x/index.m3u8", ContentType = "application/vnd.apple.mpegurl", IsLive = true };
            public Task SendGate;
            public readonly List<KeyValuePair<CastTarget, string>> Sends = new List<KeyValuePair<CastTarget, string>>();
            public int Stops, KeepAlives;
            public bool PauseOk = true;
            public DlnaTransportState Transport = DlnaTransportState.Playing;
            public int Volume = 33;
            public readonly List<int> VolumesSet = new List<int>();
            public readonly List<string> Order = new List<string>();
            private CastState _state = CastState.Idle;
            public FakeEngine() { Targets = new CastTargetList(); }
            public CastState State { get { return _state; } }
            public string Reason { get; set; }
            public event Action<CastState> StateChanged;
            public void RaiseState(CastState s, string reason) { _state = s; Reason = reason; var h = StateChanged; if (h != null) h(s); }

            public async Task DiscoverAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref Discoveries);
                if (DiscoverTime > TimeSpan.Zero) await Task.Delay(DiscoverTime, ct).ConfigureAwait(false);
                Targets.Update("dlna", Found);
            }

            public Task<CastVerdict> CheckAsync(AceEngine.Links.AceLink link, CastTarget target, LiveSession open, CancellationToken ct)
            {
                if (CheckThrows != null) throw CheckThrows;
                return Task.FromResult(Verdict(target));
            }

            public async Task<CastResult> SendAsync(CastTarget target, AceEngine.Links.AceLink link, string name, CancellationToken ct)
            {
                lock (Order) Order.Add("engine.send");
                lock (Sends) Sends.Add(new KeyValuePair<CastTarget, string>(target, name));
                var g = SendGate;
                if (g != null) await Task.WhenAny(g, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var r = Result(target);
                if (r.Ok) _state = CastState.Casting;
                return r;
            }

            public void Stop() { Interlocked.Increment(ref Stops); bool was = _state == CastState.Casting; _state = CastState.Stopped; if (was) { var h = StateChanged; if (h != null) h(CastState.Stopped); } }
            public void KeepAlive() { Interlocked.Increment(ref KeepAlives); }
            public Task<bool> PauseAsync(CancellationToken ct) { Log.Add("pause"); return Task.FromResult(PauseOk); }
            public Task<bool> ResumeAsync(CancellationToken ct) { Log.Add("resume"); return Task.FromResult(true); }
            public Task<DlnaTransportInfo> GetTransportAsync(CancellationToken ct) { return Task.FromResult(new DlnaTransportInfo { State = Transport, RawState = Transport.ToString() }); }
            public Task<int> GetVolumeAsync(CancellationToken ct) { return Task.FromResult(Volume); }
            public Task SetVolumeAsync(int v, CancellationToken ct) { lock (VolumesSet) VolumesSet.Add(v); return Task.CompletedTask; }
            public void Dispose() { }
        }

        private sealed class FakeBridge : IGoogleCastBridge
        {
            public readonly List<string> Calls = new List<string>();
            public Task ConnectGate;
            public Exception ConnectThrows, LoadThrows;
            public CastResult Loaded;
            public int Connects, Loads, Disconnects;
            public readonly List<bool> Scans = new List<bool>();
            public List<int> Volumes = new List<int>();
            public FakeEngine Engine;
            public void SetActiveScan(bool on) { lock (Scans) Scans.Add(on); }
            public async Task ConnectAsync(CastTarget target, CancellationToken ct)
            {
                Interlocked.Increment(ref Connects); lock (Calls) Calls.Add("connect " + target.Id);
                if (Engine != null) lock (Engine.Order) Engine.Order.Add("bridge.connect");
                if (ConnectThrows != null) throw ConnectThrows;
                var g = ConnectGate; if (g != null) await Task.WhenAny(g, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            public Task LoadAsync(CastResult result, CancellationToken ct)
            {
                Interlocked.Increment(ref Loads); Loaded = result; lock (Calls) Calls.Add("load");
                if (LoadThrows != null) throw LoadThrows;
                return Task.CompletedTask;
            }
            public void Pause() { lock (Calls) Calls.Add("pause"); }
            public void Resume() { lock (Calls) Calls.Add("resume"); }
            public void SetVolume(int p) { lock (Volumes) Volumes.Add(p); }
            public void Disconnect() { Interlocked.Increment(ref Disconnects); lock (Calls) Calls.Add("disconnect"); }
            public event Action<bool, int> StatusChanged;
            public event Action<string> SessionEnded;
            public void RaiseStatus(bool paused, int vol) { var h = StatusChanged; if (h != null) h(paused, vol); }
            public void RaiseEnded(string text) { var h = SessionEnded; if (h != null) h(text); }
            public void Dispose() { }
        }

        private sealed class FakeHost : ISendingHost
        {
            public readonly List<string> Calls = new List<string>();
            public void Begin(string d, string c) { lock (Calls) Calls.Add("begin " + d + "|" + c); }
            public void Update(string d, string c, bool p) { lock (Calls) Calls.Add("update " + d + "|" + c + "|" + p); }
            public void End() { lock (Calls) Calls.Add("end"); }
            public int Count(string prefix) { lock (Calls) return Calls.Count(x => x.StartsWith(prefix)); }
        }

        private sealed class FakeLock : IMulticastLock
        {
            public int Acquired, Released;
            public void Acquire() { Interlocked.Increment(ref Acquired); }
            public void Release() { Interlocked.Increment(ref Released); }
        }

        private sealed class Rig : IDisposable
        {
            public FakeEngine Engine = new FakeEngine();
            public FakeBridge Bridge = new FakeBridge();
            public FakeHost Host = new FakeHost();
            public FakeLock Lock = new FakeLock();
            public CastController C;
            public readonly List<string> Notices = new List<string>();
            public int Idled, Changes;
            public CastTarget Chromecast, Tv;
            public Rig(bool withBridge = true, Action<CastControllerOptions> cfg = null)
            {
                Bridge.Engine = Engine;
                var o = new CastControllerOptions { RefreshInterval = TimeSpan.FromMilliseconds(120), PollInterval = TimeSpan.FromMilliseconds(80), CallTimeout = TimeSpan.FromSeconds(2) };
                if (cfg != null) cfg(o);
                C = new CastController(Engine, withBridge ? Bridge : null, Host, Lock, o);
                C.Notice += n => { lock (Notices) Notices.Add(n); };
                C.Idled += () => Interlocked.Increment(ref Idled);
                C.Changed += () => Interlocked.Increment(ref Changes);
                Chromecast = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:salon", Name = "Salón", Address = IPAddress.Parse("192.168.1.80"), Capabilities = CastCapabilities.Hls | CastCapabilities.Pause | CastCapabilities.Volume, Profile = CastProfile.Chromecast2 };
                Tv = new CastTarget { Kind = CastTargetKind.Dlna, Id = "dlna:tv", Name = "[TV] Samsung", Address = IPAddress.Parse("192.168.1.68"), Capabilities = CastCapabilities.ProgressiveTs | CastCapabilities.AnyCodec | CastCapabilities.Pause | CastCapabilities.Volume };
                Engine.Targets.Update("googlecast", new[] { Chromecast });
                Engine.Targets.Update("dlna", new[] { Tv });
            }
            public CastDevice Dev(string id) { return C.Devices.First(d => d.Id == id); }
            public string[] NoticesNow() { lock (Notices) return Notices.ToArray(); }
            public void Dispose() { C.Dispose(); }
        }

        private static Channel Ch(string name, string hex = null)
        {
            hex = hex ?? new string(name.Length % 2 == 0 ? 'a' : 'b', 40);
            return new Channel { Id = "cid:" + hex, Name = name, Entry = LinkEntry.FromUrl(name, "acestream://" + hex) };
        }

        private static void Until(Func<bool> cond, string what)
        {
            Assert.True(SpinWait.SpinUntil(cond, 5000), "timed out waiting for " + what);
        }

        private static CastVerdict Incompatible(params CastIssueCode[] codes)
        {
            var v = new CastVerdict { Support = CastSupport.Incompatible };
            foreach (var c in codes) v.Issues.Add(new CastIssue { Code = c, Blocking = true, Message = c.ToString() });
            return v;
        }

        // ------------------------------------------------------------------ devices
        [Fact]
        public void TheDevicesAreTheEnginesTargetsOnePerScreenWithWhatEachCanDo()
        {
            using (var r = new Rig())
            {
                Assert.Equal(2, r.C.Devices.Count);
                var cc = r.Dev("gcast:salon"); var tv = r.Dev("dlna:tv");
                Assert.Equal(CastDeviceKind.Chromecast, cc.Kind); Assert.Equal(CastDeviceKind.Tv, tv.Kind);
                Assert.True(cc.CanPause && cc.CanVolume && tv.CanPause && tv.CanVolume);
                // a renderer with no RenderingControl has no volume; another P! is not a screen of this list
                r.Engine.Targets.Update("dlna", new[] { r.Tv, new CastTarget { Kind = CastTargetKind.Dlna, Id = "dlna:mute", Name = "Mudo", Address = IPAddress.Parse("192.168.1.70"), Capabilities = CastCapabilities.ProgressiveTs | CastCapabilities.AnyCodec | CastCapabilities.Pause },
                    new CastTarget { Kind = CastTargetKind.PApp, Id = "papp:1", Name = "Otra P!", Address = IPAddress.Parse("192.168.1.71") } });
                Assert.Equal(3, r.C.Devices.Count);
                Assert.False(r.Dev("dlna:mute").CanVolume);
                Assert.DoesNotContain(r.C.Devices, d => d.Id == "papp:1");
            }
        }

        [Fact]
        public void TheAvailabilityCanChange()
        {
            using (var r = new Rig())
            {
                int n = 0; r.C.AvailabilityChanged += () => n++;
                Assert.True(r.C.IsAvailable);
                r.C.SetAvailable(false); r.C.SetAvailable(false); r.C.SetAvailable(true);
                Assert.Equal(2, n);
            }
        }

        // ------------------------------------------------------------------ search
        [Fact]
        public void OpeningTheSheetSearchesWithTheMulticastLockAndRefreshesWhileItIsOpen()
        {
            using (var r = new Rig())
            {
                r.Engine.DiscoverTime = TimeSpan.FromMilliseconds(60);
                r.C.BeginSearch();
                Assert.True(r.C.Searching);
                Assert.Equal(new[] { true }, r.Bridge.Scans.ToArray());
                Until(() => !r.C.Searching, "the first search to finish");
                Assert.True(r.Engine.Discoveries >= 1);
                Until(() => r.Engine.Discoveries >= 3, "the periodic refresh");
                Assert.False(r.C.Searching);                                           // the refreshes are quiet
                Until(() => r.Lock.Released == r.Lock.Acquired, "the lock to be given back");
                Assert.True(r.Lock.Acquired >= 3);
                r.C.EndSearch();
                Assert.Equal(new[] { true, false }, r.Bridge.Scans.ToArray());
                Thread.Sleep(150);
                int n = r.Engine.Discoveries;
                Thread.Sleep(350);
                Assert.True(r.Engine.Discoveries <= n + 1, "it kept searching after the sheet closed");
                Until(() => r.Lock.Released == r.Lock.Acquired, "the lock to be given back");
            }
        }

        [Fact]
        public void ASearchThatFindsNothingEndsAndAnotherOneCanBeStartedAndFoundDevicesAppear()
        {
            using (var r = new Rig())
            {
                r.Engine.Targets.Update("dlna", new CastTarget[0]); r.Engine.Targets.Update("googlecast", new CastTarget[0]);
                Assert.Empty(r.C.Devices);
                r.C.BeginSearch();
                Until(() => !r.C.Searching, "the search");
                Assert.Empty(r.C.Devices);
                r.Engine.Found = new List<CastTarget> { r.Tv };
                r.C.BeginSearch();                                                     // "Buscar de nuevo"
                Until(() => r.C.Devices.Count == 1, "the television to be found");
                r.C.EndSearch();
            }
        }

        [Fact]
        public void ASearchThatFailsDoesNotBreakAnything()
        {
            using (var r = new Rig())
            {
                r.Engine.DiscoverTime = TimeSpan.Zero;
                var failing = new ThrowingEngine();
                using (var c = new CastController(failing, null, null, r.Lock))
                {
                    c.BeginSearch();
                    Until(() => !c.Searching && failing.Calls >= 1, "the failed search");
                    c.EndSearch();
                }
                Assert.Equal(r.Lock.Acquired, r.Lock.Released);
            }
        }

        [Fact]
        public async Task WarmingUpSearchesOnceQuietlyAheadOfTheSheetButNeverWhileSending()
        {
            using (var r = new Rig())
            {
                r.Engine.Targets.Update("dlna", new CastTarget[0]);
                r.Engine.Found = new List<CastTarget> { r.Tv };
                r.Engine.DiscoverTime = TimeSpan.FromMilliseconds(60);
                r.C.WarmUp();
                r.C.WarmUp();                                                          // a search is already going on: not another one
                Assert.False(r.C.Searching);                                           // nothing on screen says "searching"
                Until(() => r.C.Devices.Any(d => d.Id == "dlna:tv"), "the television to be listed before the sheet opens");
                Until(() => r.Lock.Acquired == 1 && r.Lock.Released == 1, "the lock to be given back");
                Thread.Sleep(300);
                Assert.Equal(1, r.Engine.Discoveries);                                 // no refresh: that is only while the sheet is open
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("DAZN 1"), null, CancellationToken.None));
                r.C.WarmUp();
                Thread.Sleep(150);
                Assert.Equal(1, r.Engine.Discoveries);                                 // sending: the network is left alone
                r.C.Stop();
            }
        }

        private sealed class ThrowingEngine : ICastEngine
        {
            public int Calls;
            public CastTargetList Targets { get; private set; }
            public ThrowingEngine() { Targets = new CastTargetList(); }
            public Task DiscoverAsync(CancellationToken ct) { Calls++; throw new InvalidOperationException("no network"); }
            public Task<CastVerdict> CheckAsync(AceEngine.Links.AceLink l, CastTarget t, LiveSession o, CancellationToken ct) { return Task.FromResult<CastVerdict>(null); }
            public Task<CastResult> SendAsync(CastTarget t, AceEngine.Links.AceLink l, string n, CancellationToken ct) { throw new NotSupportedException(); }
            public void Stop() { }
            public void KeepAlive() { }
            public CastState State { get { return CastState.Idle; } }
            public string Reason { get { return ""; } }
            public event Action<CastState> StateChanged { add { } remove { } }
            public Task<bool> PauseAsync(CancellationToken ct) { return Task.FromResult(false); }
            public Task<bool> ResumeAsync(CancellationToken ct) { return Task.FromResult(false); }
            public Task<DlnaTransportInfo> GetTransportAsync(CancellationToken ct) { return Task.FromResult<DlnaTransportInfo>(null); }
            public Task<int> GetVolumeAsync(CancellationToken ct) { return Task.FromResult(-1); }
            public Task SetVolumeAsync(int v, CancellationToken ct) { return Task.CompletedTask; }
            public void Dispose() { }
        }

        // ------------------------------------------------------------------ check
        [Fact]
        public async Task TheCheckSaysCompatibleOrTheReasonInOneSentencePerKindOfDevice()
        {
            using (var r = new Rig())
            {
                var ch = Ch("DAZN 1");
                Assert.True((await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Compatible);
                r.Engine.Verdict = t => Incompatible(CastIssueCode.AudioMp2);
                var no = await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None);
                Assert.True(no.Known); Assert.False(no.Compatible);
                Assert.Equal("El Chromecast no reproduce el audio de este canal", no.Reason);
                Assert.Equal("La tele no reproduce el audio de este canal", (await r.C.CheckAsync(r.Dev("dlna:tv"), ch, null, CancellationToken.None)).Reason);
                r.Engine.Verdict = t => Incompatible(CastIssueCode.VideoHevc);
                Assert.Equal("El Chromecast no reproduce el vídeo de este canal", (await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Reason);
                r.Engine.Verdict = t => Incompatible(CastIssueCode.AudioMp2, CastIssueCode.VideoMpeg2);
                Assert.Equal("El Chromecast no reproduce ni el audio ni el vídeo de este canal", (await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Reason);
                r.Engine.Verdict = t => Incompatible(CastIssueCode.H264Resolution);
                Assert.Equal("El Chromecast no puede con la calidad de este canal", (await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Reason);
                r.Engine.Verdict = t => Incompatible(CastIssueCode.Scrambled);
                Assert.Equal("Este canal está cifrado", (await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Reason);
                // a verdict that is not final is not a no
                r.Engine.Verdict = t => new CastVerdict { Support = CastSupport.Unknown };
                Assert.False((await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Known);
                // an engine that throws is not a no either
                r.Engine.CheckThrows = new InvalidOperationException("boom");
                Assert.False((await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None)).Known);
            }
        }

        [Fact]
        public async Task AChannelThatIsNotAnAceStreamCannotBeSentAnywhere()
        {
            using (var r = new Rig())
            {
                var http = new Channel { Id = "http://x/a.m3u8", Name = "Directo web", Entry = LinkEntry.FromUrl("Directo web", "http://x/a.m3u8") };
                var chk = await r.C.CheckAsync(r.Dev("dlna:tv"), http, null, CancellationToken.None);
                Assert.False(chk.Compatible);
                Assert.Equal(CastMessages.NotSendable, chk.Reason);
                Assert.False(await r.C.SendAsync(r.Dev("dlna:tv"), http, null, CancellationToken.None));
                Assert.Equal(new[] { CastMessages.NotSendable }, r.NoticesNow());
                Assert.False(r.C.Status.IsActive);
            }
        }

        [Fact]
        public async Task ACastDeviceThatIsAlsoADlnaTelevisionIsOfferedForWhatOnlyTheTelevisionCanPlayAndTheSendGoesThatWay()
        {
            using (var r = new Rig())
            {
                // the same screen reachable both ways: the target list merges them, Cast first
                var androidTv = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:sony", Name = "Sony", Address = IPAddress.Parse("192.168.1.90"), Capabilities = CastCapabilities.Hls | CastCapabilities.Pause | CastCapabilities.Volume };
                var sonyDlna = new CastTarget { Kind = CastTargetKind.Dlna, Id = "dlna:sony", Name = "Sony DLNA", Address = IPAddress.Parse("192.168.1.90"), Capabilities = CastCapabilities.ProgressiveTs | CastCapabilities.AnyCodec | CastCapabilities.Pause | CastCapabilities.Volume };
                r.Engine.Targets.Update("googlecast", new[] { r.Chromecast, androidTv });
                r.Engine.Targets.Update("dlna", new[] { r.Tv, sonyDlna });
                Assert.Equal(3, r.C.Devices.Count);
                r.Engine.Verdict = t => t.Kind == CastTargetKind.GoogleCast ? Incompatible(CastIssueCode.AudioMp2) : new CastVerdict { Support = CastSupport.Compatible };
                var ch = Ch("La 1");
                var plain = await r.C.CheckAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None);
                Assert.False(plain.Compatible);                                        // a plain Chromecast: no way
                var sony = await r.C.CheckAsync(r.Dev("gcast:sony"), ch, null, CancellationToken.None);
                Assert.True(sony.Compatible);                                          // the Sony can, through DLNA
                Assert.True(await r.C.SendAsync(r.Dev("gcast:sony"), ch, null, CancellationToken.None));
                Assert.Equal("dlna:sony", r.Engine.Sends[0].Key.Id);
                Assert.Equal(0, r.Bridge.Connects);                                    // no Cast session was opened
            }
        }

        // ------------------------------------------------------------------ send
        [Fact]
        public async Task SendingToATelevisionReleasesTheLocalPlaybackFirstThenSendsAndFollowsTheState()
        {
            using (var r = new Rig())
            {
                var ch = Ch("DAZN 1");
                var order = new List<string>();
                var statuses = new List<CastPhase>();
                r.C.Changed += () => { lock (statuses) statuses.Add(r.C.Status.Phase); };
                bool ok = await r.C.SendAsync(r.Dev("dlna:tv"), ch, () => { lock (r.Engine.Order) r.Engine.Order.Add("release"); return Task.CompletedTask; }, CancellationToken.None);
                Assert.True(ok);
                Assert.Equal(new[] { "release", "engine.send" }, r.Engine.Order.ToArray());
                lock (statuses) Assert.Equal(new[] { CastPhase.Starting, CastPhase.Sending }, statuses.Distinct().ToArray());
                var st = r.C.Status;
                Assert.Equal(CastPhase.Sending, st.Phase); Assert.Equal("dlna:tv", st.Device.Id); Assert.Same(ch, st.Channel);
                Assert.True(r.C.Busy);
                Assert.Equal(1, r.Host.Count("begin [TV] Samsung|DAZN 1"));
                Assert.Equal(1, r.Host.Count("update [TV] Samsung|DAZN 1|False"));
                Assert.Equal(0, r.Bridge.Connects);
                r.C.Stop();
                Assert.False(r.C.Busy);
                Assert.Equal(1, r.Host.Count("end"));
                Assert.Equal(1, r.Idled);
                Assert.Empty(r.NoticesNow());                                           // the user asked: no notice
                r.C.Stop();                                                             // idempotent
                Assert.Equal(1, r.Idled);
            }
        }

        [Fact]
        public async Task SendingToAChromecastConnectsWhileTheEnginePreparesThenLoadsWhatTheEnginePrepared()
        {
            using (var r = new Rig())
            {
                var gate = new TaskCompletionSource<bool>();
                r.Engine.SendGate = gate.Task;
                var ch = Ch("La 1");
                var task = r.C.SendAsync(r.Dev("gcast:salon"), ch, null, CancellationToken.None);
                Until(() => r.Engine.Sends.Count == 1 && r.Bridge.Connects == 1, "both to start");    // the session does not wait for the engine
                Assert.Equal(CastPhase.Starting, r.C.Status.Phase);
                Assert.Equal(0, r.Bridge.Loads);
                gate.SetResult(true);
                Assert.True(await task);
                Assert.Equal(1, r.Bridge.Loads);
                Assert.Equal("application/vnd.apple.mpegurl", r.Bridge.Loaded.ContentType);
                Assert.True(r.Bridge.Loaded.IsLive);
                Assert.Equal(CastPhase.Sending, r.C.Status.Phase);
                Assert.Equal(new[] { "connect gcast:salon", "load" }, r.Bridge.Calls.Take(2).ToArray());
                // the engine is told the receiver is alive while the session is
                int k = r.Engine.KeepAlives;
                Until(() => r.Engine.KeepAlives > k, "a keep-alive");
            }
        }

        [Fact]
        public async Task ChangingTheChannelOnTheSameChromecastKeepsTheSessionAndLoadsAgain()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 2 HD", new string('c', 40)), null, CancellationToken.None));
                Assert.Equal(1, r.Bridge.Connects);
                Assert.Equal(2, r.Bridge.Loads);
                Assert.Equal("La 2 HD", r.C.Status.Channel.Name);
                Assert.Equal(0, r.Bridge.Disconnects);
                // another device ends the session first
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal(1, r.Bridge.Disconnects);
                Assert.Equal("dlna:tv", r.C.Status.Device.Id);
            }
        }

        [Fact]
        public async Task ASendReplacesOneThatIsStillConnecting()
        {
            using (var r = new Rig())
            {
                var gate = new TaskCompletionSource<bool>();
                r.Engine.SendGate = gate.Task;
                var first = r.C.SendAsync(r.Dev("dlna:tv"), Ch("Uno"), null, CancellationToken.None);
                Until(() => r.Engine.Sends.Count == 1, "the first send");
                var second = r.C.SendAsync(r.Dev("dlna:tv"), Ch("Tres", new string('d', 40)), null, CancellationToken.None);
                Until(() => r.Engine.Sends.Count == 2, "the second send");
                gate.SetResult(true);
                Assert.False(await first);
                Assert.True(await second);
                Assert.Equal("Tres", r.C.Status.Channel.Name);
                Assert.Equal(CastPhase.Sending, r.C.Status.Phase);
                Assert.Empty(r.NoticesNow());                                           // the replaced one is not an error
            }
        }

        [Fact]
        public async Task StoppingWhileItConnectsCancelsSilentlyAndReleasesEverything()
        {
            using (var r = new Rig())
            {
                r.Engine.SendGate = new TaskCompletionSource<bool>().Task;
                r.Bridge.ConnectGate = new TaskCompletionSource<bool>().Task;
                var task = r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None);
                Until(() => r.C.Status.Phase == CastPhase.Starting && r.Bridge.Connects == 1, "starting");
                r.C.Stop();
                Assert.False(await task);
                Assert.False(r.C.Status.IsActive);
                Assert.Empty(r.NoticesNow());
                Assert.True(r.Engine.Stops >= 1);
                Assert.True(r.Bridge.Disconnects >= 1);
                Assert.Equal(1, r.Host.Count("end"));
            }
        }

        [Fact]
        public async Task ACallerThatCancelsWhileTheCastSessionIsStillOpeningDoesNotLeaveASessionBehind()
        {
            using (var r = new Rig())
            {
                r.Bridge.ConnectGate = new TaskCompletionSource<bool>().Task;
                var cts = new CancellationTokenSource();
                var task = r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, cts.Token);
                Until(() => r.Bridge.Connects == 1, "the session to be requested");
                cts.Cancel();
                Assert.False(await task);
                Assert.True(r.Bridge.Disconnects >= 1);
                Assert.False(r.C.Status.IsActive);
                Assert.Empty(r.NoticesNow());
            }
        }

        [Fact]
        public async Task ASendThatTheEngineRefusesGivesTheSentenceAboutTheDeviceAndLeavesNothingRunning()
        {
            using (var r = new Rig())
            {
                r.Engine.Result = t => new CastResult { Ok = false, Failure = CastFailure.Incompatible, Reason = "audio MP2", Verdict = Incompatible(CastIssueCode.AudioMp2) };
                Assert.False(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal(new[] { "El Chromecast no reproduce el audio de este canal" }, r.NoticesNow());
                Assert.False(r.C.Status.IsActive);
                Assert.Equal(1, r.Host.Count("end"));
                Assert.Equal(1, r.Idled);
                Assert.True(r.Bridge.Disconnects >= 1);                                 // the session opened in parallel is closed again
                Assert.Equal(0, r.Bridge.Loads);
                // another kind of failure: the engine's own Spanish reason
                r.Engine.Result = t => new CastResult { Ok = false, Failure = CastFailure.NoRoute, Reason = "No hay ruta de red hasta el dispositivo" };
                Assert.False(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal("No hay ruta de red hasta el dispositivo", r.NoticesNow().Last());
            }
        }

        [Fact]
        public async Task ASessionThatCannotBeOpenedOrLoadedIsToldAndTheEngineStops()
        {
            using (var r = new Rig())
            {
                r.Bridge.ConnectThrows = new InvalidOperationException("no session");
                Assert.False(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal("No se ha podido conectar con Salón", r.NoticesNow().Last());
                Assert.True(r.Engine.Stops >= 1);
                Assert.False(r.C.Status.IsActive);
            }
            using (var r = new Rig())
            {
                r.Bridge.LoadThrows = new InvalidOperationException("load failed");
                Assert.False(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal("El Chromecast no ha podido cargar el canal", r.NoticesNow().Last());
                Assert.False(r.C.Status.IsActive);
                Assert.True(r.Bridge.Disconnects >= 1);
            }
        }

        [Fact]
        public async Task WithoutTheCastBridgeAChromecastCannotBeUsedButTelevisionsCan()
        {
            using (var r = new Rig(withBridge: false))
            {
                Assert.False(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                Assert.Equal("Este móvil no puede enviar a un Chromecast", r.NoticesNow().Last());
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
            }
        }

        [Fact]
        public async Task AReleaseThatFailsDoesNotStopTheSend()
        {
            using (var r = new Rig())
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), () => { throw new InvalidOperationException("player gone"); }, CancellationToken.None));
        }

        // ------------------------------------------------------------------ the end of a sending
        [Fact]
        public async Task ATelevisionThatStopsTheChannelEndsTheSendingAndTheUserIsTold()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.Engine.RaiseState(CastState.Stopped, "stopped from the TV");
                Assert.False(r.C.Status.IsActive);
                Assert.Equal(new[] { "El envío ha terminado: se ha parado desde la tele" }, r.NoticesNow());
                Assert.Equal(1, r.Idled);
                Assert.Equal(1, r.Host.Count("end"));
                Assert.True(r.Engine.Stops >= 1);
            }
        }

        [Fact]
        public async Task TheChannelGoingDownOrTheDeviceGoingQuietEndsItWithTheRightSentence()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.Engine.RaiseState(CastState.Stopped, "the channel stopped");
                Assert.Equal("El canal ha dejado de emitir", r.NoticesNow().Last());
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.Engine.RaiseState(CastState.Stopped, "idle");
                Assert.Equal("El envío ha terminado: el aparato ha dejado de pedir el canal", r.NoticesNow().Last());
            }
        }

        [Fact]
        public async Task ACastSessionEndedFromTheTvOrAnotherPhoneEndsTheSendingAndStopsTheEngine()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                int stops = r.Engine.Stops;
                r.Bridge.RaiseEnded("El envío ha terminado: se ha parado desde otro dispositivo");
                Assert.False(r.C.Status.IsActive);
                Assert.Equal("El envío ha terminado: se ha parado desde otro dispositivo", r.NoticesNow().Last());
                Assert.True(r.Engine.Stops > stops);
                Assert.Equal(1, r.Idled);
                Assert.Equal(1, r.Host.Count("end"));
                // an event with nothing being sent is ignored
                r.Bridge.RaiseEnded("otra vez");
                Assert.Single(r.NoticesNow());
            }
        }

        [Fact]
        public async Task OurOwnStopDoesNotComeBackAsAnEndByItself()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.C.Stop();
                Assert.Empty(r.NoticesNow());
                Assert.Equal(1, r.Idled);
                Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                r.C.Stop();
                Assert.Empty(r.NoticesNow());
                Assert.Equal(2, r.Idled);
            }
        }

        // ------------------------------------------------------------------ pause, volume, following the device
        [Fact]
        public async Task PauseAndVolumeGoToTheChromecastSession()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
                r.C.SetPaused(true);
                Until(() => r.C.Status.Paused, "paused");
                r.C.SetPaused(false);
                Until(() => !r.C.Status.Paused, "resumed");
                Assert.Equal(new[] { "connect gcast:salon", "load", "pause", "resume" }, r.Bridge.Calls.ToArray());
                r.C.SetVolume(70);
                Until(() => r.C.Status.Volume == 70, "volume");
                Assert.Equal(new[] { 70 }, r.Bridge.Volumes.ToArray());
                // the device's own changes (the TV remote) are followed
                r.Bridge.RaiseStatus(true, 20);
                Assert.True(r.C.Status.Paused); Assert.Equal(20, r.C.Status.Volume);
                Assert.True(r.Host.Count("update Salón|La 1|True") >= 1);
            }
        }

        [Fact]
        public async Task PauseAndVolumeGoToTheTelevisionAndATelevisionWithoutPauseLosesTheButton()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.C.SetVolume(55);
                Until(() => r.Engine.VolumesSet.Contains(55), "volume");
                r.C.SetPaused(true);
                Until(() => r.C.Status.Paused, "paused");
                Assert.Contains("pause", r.Engine.Log);
                r.C.SetPaused(false);
                Until(() => !r.C.Status.Paused, "resumed");
                // a television that has no Pause
                r.Engine.PauseOk = false;
                r.C.SetPaused(true);
                Until(() => r.NoticesNow().Contains(CastMessages.NoPause), "the notice");
                Assert.False(r.C.Status.Paused);
                Assert.False(r.C.Status.Device.CanPause);
                Assert.False(r.Dev("dlna:tv").CanPause);
            }
        }

        [Fact]
        public async Task ATelevisionsOwnStateIsReadWhileSending()
        {
            using (var r = new Rig())
            {
                Assert.True(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
                r.Engine.Transport = DlnaTransportState.PausedPlayback; r.Engine.Volume = 12;
                Until(() => r.C.Status.Paused && r.C.Status.Volume == 12, "the television's state");
                r.Engine.Transport = DlnaTransportState.Playing;
                Until(() => !r.C.Status.Paused, "playing again");
            }
        }

        [Fact]
        public async Task DisposingStopsTheSending()
        {
            var r = new Rig();
            Assert.True(await r.C.SendAsync(r.Dev("gcast:salon"), Ch("La 1"), null, CancellationToken.None));
            r.Dispose();
            Assert.False(r.C.Status.IsActive);
            Assert.True(r.Bridge.Disconnects >= 1);
            Assert.False(await r.C.SendAsync(r.Dev("dlna:tv"), Ch("La 1"), null, CancellationToken.None));
        }

        // ------------------------------------------------------------------ messages
        [Fact]
        public void TheEndAndFailureMessagesAreTheOnesTheUserReads()
        {
            Assert.Equal("El envío ha terminado", CastMessages.Ended(""));
            Assert.Equal("El envío ha terminado", CastMessages.Ended(null));
            Assert.Equal("El envío ha terminado: el canal no tiene fotogramas clave", CastMessages.Ended("no key frames"));
            Assert.Equal("No se ha podido enviar el canal", CastMessages.StartFailed(null, CastDeviceKind.Tv));
            Assert.Equal("La tele no reproduce el vídeo de este canal", CastMessages.Incompatible(Incompatible(CastIssueCode.VideoMpeg2), CastDeviceKind.Tv));
            Assert.Equal("Este canal no tiene vídeo", CastMessages.Incompatible(Incompatible(CastIssueCode.NoVideo), CastDeviceKind.Chromecast));
            Assert.Equal("No se puede enviar este canal a un Chromecast", CastMessages.Incompatible(Incompatible(CastIssueCode.NoProgram), CastDeviceKind.Chromecast));
        }
    }
}
