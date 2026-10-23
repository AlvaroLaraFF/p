using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;
using AceEngine.Links;
using AceEngine.Sim;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.Cast
{
    /// <summary>The engine with a simulated channel and a CastService on it, plus a renderer on the loopback.</summary>
    internal sealed class ServiceRig : IDisposable
    {
        public readonly LiveHarness H;
        public readonly CastService Service;
        public readonly FakeRenderer Renderer = new FakeRenderer();
        public readonly AceLink Link;
        public CastTarget Target;

        public ServiceRig(Action<TsGen> gen = null, Action<CastServiceOptions> opt = null, Action<FakeRenderer> cfg = null)
        {
            if (cfg != null) cfg(Renderer);
            double bps;
            var data = CastRig.Material(gen, 200, out bps);
            var sim = new LiveSimOptions
            {
                TimeScale = 4, Bitrate = (int)bps, PieceLength = 65536, ChunkLength = 16384, WindowPieces = 80, StartMisalignment = 0,
                ContentProvider = (off, n) =>
                {
                    var r = new byte[n];
                    for (int i = 0; i < n; i++) { long o = off + i; r[i] = o < data.Length ? data[o] : (byte)((o % 188 == 0) ? 0x47 : 0xFF); }
                    return r;
                }
            };
            H = new LiveHarness(sim, o => { o.Live.TimeshiftBytes = 24L << 20; o.Live.MaxBufferBytes = 4 << 20; o.Live.BufferSeconds = 4; });
            H.AddSource();
            string path = H.PrepareHost();
            Link = AceLink.Parse(path);
            var so = new CastServiceOptions
            {
                PlayWait = TimeSpan.FromSeconds(4), PollInterval = TimeSpan.FromMilliseconds(200), WatchInterval = TimeSpan.FromMilliseconds(300), WatchGrace = TimeSpan.FromMilliseconds(500),
                Source = new CastSourceOptions { AllowAnyTarget = true, OpenTimeout = TimeSpan.FromSeconds(30), AnalysisTimeout = TimeSpan.FromSeconds(8), TimeshiftBytes = 24L << 20 },
                Dlna = new DlnaOptions { Timeout = TimeSpan.FromSeconds(3) }
            };
            if (opt != null) opt(so);
            Service = new CastService(() => H.Host, new FakeNet(), null, so);
            var rd = DlnaDescription.Parse(System.Text.Encoding.UTF8.GetBytes(DescriptionOf(Renderer)), DescriptionOf(Renderer).Length, Renderer.Location);
            Target = DlnaDiscovery.ToTarget(rd);
            Service.Targets.Update("dlna", new[] { Target });
            Target = Service.Targets.Items.Single();
        }

        private static string DescriptionOf(FakeRenderer r)
        {
            using (var http = new HttpClient()) return http.GetStringAsync(r.Location).Result;
        }

        public void Dispose() { Service.Dispose(); Renderer.Dispose(); H.Dispose(); }
    }

    [Collection(CastIsolatedCollection.Name)]
    public class CastServiceTests
    {
        private static async Task<bool> UntilAsync(Func<bool> c, int ms = 8000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (c()) return true; await Task.Delay(50); }
            return c();
        }

        [Fact]
        public async Task ARendererThatListsVideoMp2tGetsTheChannelAndPlaysIt()
        {
            using (var rig = new ServiceRig())
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "Liga de Campeones", CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.Equal(CastState.Casting, rig.Service.State);
                Assert.Equal("video/mp2t", res.ContentType);
                Assert.EndsWith("/live.ts", res.Url);
                // what the TV was told
                var set = rig.Renderer.Last("SetAVTransportURI");
                Assert.Equal(res.Url, set.Args["CurrentURI"]);
                Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", set.SoapAction);
                Assert.Contains("<dc:title>Liga de Campeones</dc:title>", set.Args["CurrentURIMetaData"]);
                Assert.Contains("object.item.videoItem", set.Args["CurrentURIMetaData"]);
                Assert.Contains("http-get:*:video/mp2t:DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=0170", set.Args["CurrentURIMetaData"]);
                Assert.Equal("1", rig.Renderer.Last("Play").Args["Speed"]);
                Assert.Equal("0", rig.Renderer.Last("Play").Args["InstanceID"]);
                // what the TV got: the right headers on the stream
                Assert.True(await UntilAsync(() => rig.Renderer.StreamContentTypes.Count > 0));
                Assert.Equal("video/mp2t", rig.Renderer.StreamContentTypes.First());
                Assert.Contains("DLNA.ORG_OP=00", rig.Renderer.StreamFeatures.First());
                Assert.True(await UntilAsync(() => Interlocked.Read(ref rig.Renderer.BytesFetched) > 188 * 100));
                var t = await rig.Service.GetTransportAsync(CancellationToken.None);
                Assert.Equal(DlnaTransportState.Playing, t.State);
                Assert.NotNull(res.Verdict);
                Assert.Equal(rig.Target.Id, rig.Service.CurrentTarget.Id);

                rig.Service.Stop();
                Assert.Equal(CastState.Stopped, rig.Service.State);
                Assert.True(rig.Renderer.Count("Stop") >= 1, "the TV was not told to stop");
                Assert.Equal("STOPPED", rig.Renderer.State);
                using (var http = new HttpClient()) await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(res.Url));
            }
        }

        [Fact]
        public async Task WhateverCodecsTheChannelHasASendToATunerTvIsNotRefused()
        {
            using (var rig = new ServiceRig(g => { g.Audio = TestAudio.Mp2; }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "Polsat", CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.True(res.Verdict.IsIncompatible);                                     // for a Chromecast: yes; for the TV it does not matter
                Assert.True(await UntilAsync(() => rig.Renderer.State == "PLAYING"));
            }
        }

        [Fact]
        public async Task TheSinkOrdersTheVariantsSoTheAcceptedMimeTypeIsTriedFirst()
        {
            using (var rig = new ServiceRig(cfg: r => { r.Manufacturer = "Samsung Electronics"; r.SinkMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp2t" }; r.AcceptMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp2t" }; }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.Equal("video/mp2t", res.ContentType);                                 // the Samsung default (video/mpeg) was not tried first
                Assert.Equal(1, rig.Renderer.Count("SetAVTransportURI"));
            }
        }

        [Fact]
        public async Task ARendererThatRefusesTheFirstMimeTypeGetsTheNextVariant()
        {
            // the Sink lies: it lists video/mpeg, but only video/vnd.dlna.mpeg-tts is accepted for real. LG variants: mp2t, dlna-tts, mpeg
            using (var rig = new ServiceRig(cfg: r =>
            {
                r.Manufacturer = "LG Electronics";
                r.SinkMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mpeg" };
                r.AcceptMimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/vnd.dlna.mpeg-tts" };
            }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.Equal("video/vnd.dlna.mpeg-tts", res.ContentType);
                Assert.True(rig.Renderer.Count("SetAVTransportURI") >= 2);
                Assert.Equal("video/vnd.dlna.mpeg-tts", rig.Service.CurrentVariant.MimeType);
                Assert.True(await UntilAsync(() => rig.Renderer.State == "PLAYING"));
            }
        }

        [Fact]
        public async Task WhenNoVariantWorksTheSendFailsWithTheReasonOfEachAndTheTvIsLeftStopped()
        {
            using (var rig = new ServiceRig(cfg: r => { r.AcceptMimes = new HashSet<string>(); }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.False(res.Ok);
                Assert.Equal(CastFailure.OpenFailed, res.Failure);
                Assert.Equal("El televisor no ha aceptado el canal", res.Reason);
                Assert.Contains("Illegal MIME-Type", res.Detail);
                Assert.Contains("ts-close", res.Detail);
                Assert.Equal(CastState.Failed, rig.Service.State);
                Assert.Null(rig.Service.CurrentTarget);
            }
        }

        [Fact]
        public async Task ATvThatNeverFetchesTheUrlIsGivenUpOnAfterThePlayWait()
        {
            using (var rig = new ServiceRig(opt: o => { o.PlayWait = TimeSpan.FromSeconds(1); o.MaxVariants = 2; }, cfg: r => { r.FetchStream = false; }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.False(res.Ok);
                Assert.Contains("never asked for the stream", res.Detail);
                Assert.Equal(2, rig.Renderer.Count("Play"));
            }
        }

        [Fact]
        public async Task ASoapFaultOnSetUriMovesOnToTheNextVariantAndIsReported()
        {
            using (var rig = new ServiceRig(opt: o => o.MaxVariants = 2, cfg: r => { r.SetUriFault = 402; }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.False(res.Ok);
                Assert.Contains("invalid arguments", res.Detail);
                Assert.Equal(2, rig.Renderer.Count("SetAVTransportURI"));
                Assert.Equal(0, rig.Renderer.Count("Play"));
            }
        }

        [Fact]
        public async Task StoppingFromTheTvRemoteEndsTheSending()
        {
            using (var rig = new ServiceRig())
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.True(res.Ok, res.Reason);
                Assert.True(await UntilAsync(() => rig.Renderer.State == "PLAYING"));
                rig.Renderer.UserStops();
                Assert.True(await UntilAsync(() => rig.Service.State == CastState.Stopped, 10000), "the service did not notice: " + rig.Service.State);
                Assert.Contains("TV", rig.Service.Reason);
                using (var http = new HttpClient()) await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(res.Url));
            }
        }

        [Fact]
        public async Task PauseResumeAndVolumeGoToTheTv()
        {
            using (var rig = new ServiceRig())
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.True(res.Ok, res.Reason);
                Assert.True(await UntilAsync(() => rig.Renderer.State == "PLAYING"));
                Assert.True(await rig.Service.PauseAsync(CancellationToken.None));
                Assert.Equal("PAUSED_PLAYBACK", rig.Renderer.State);
                Assert.True(await rig.Service.ResumeAsync(CancellationToken.None));
                Assert.Equal(20, await rig.Service.GetVolumeAsync(CancellationToken.None));
                await rig.Service.SetVolumeAsync(55, CancellationToken.None);
                Assert.Equal(55, rig.Renderer.Volume);
                await rig.Service.SetVolumeAsync(500, CancellationToken.None);
                Assert.Equal(100, rig.Renderer.Volume);
            }
        }

        [Fact]
        public async Task ATvWithoutPauseSaysSoInsteadOfFailing()
        {
            using (var rig = new ServiceRig(cfg: r => { r.SupportPause = false; }))
            {
                var res = await rig.Service.SendAsync(rig.Target, rig.Link, "X", CancellationToken.None);
                Assert.True(res.Ok, res.Reason);
                Assert.False(await rig.Service.PauseAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task ControlCallsWithNothingBeingSentDoNothing()
        {
            using (var rig = new ServiceRig())
            {
                Assert.False(await rig.Service.PauseAsync(CancellationToken.None));
                Assert.False(await rig.Service.ResumeAsync(CancellationToken.None));
                Assert.Null(await rig.Service.GetTransportAsync(CancellationToken.None));
                Assert.Equal(-1, await rig.Service.GetVolumeAsync(CancellationToken.None));
                rig.Service.Stop();                                                          // idempotent
                rig.Service.Stop();
                Assert.Equal(0, rig.Renderer.Calls.Count);
            }
        }

        [Fact]
        public async Task SendingAnotherChannelReplacesTheFirst()
        {
            using (var rig = new ServiceRig())
            {
                var a = await rig.Service.SendAsync(rig.Target, rig.Link, "A", CancellationToken.None);
                Assert.True(a.Ok, a.Reason);
                var b = await rig.Service.SendAsync(rig.Target, rig.Link, "B", CancellationToken.None);
                Assert.True(b.Ok, b.Reason);
                Assert.NotEqual(a.Url, b.Url);
                using (var http = new HttpClient()) await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(a.Url));
            }
        }

        [Fact]
        public async Task TheCheckForAPickerEntryIsFreeForATunerTvAndReadsTheCodecsForAChromecast()
        {
            using (var rig = new ServiceRig(g => { g.Audio = TestAudio.Mp2; }))
            {
                var tv = await rig.Service.CheckAsync(rig.Link, rig.Target, CancellationToken.None);          // DLNA, AnyCodec: no channel is opened
                Assert.True(tv.IsCompatible);
                Assert.Equal(0, rig.H.Host.Server.Sessions.Count());
                var cc = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:1", Address = IPAddress.Loopback, Capabilities = CastCapabilities.Hls };
                var v = await rig.Service.CheckAsync(rig.Link, cc, CancellationToken.None);
                Assert.True(v.IsIncompatible);
                Assert.Equal("audio MP2", v.Reason);
                Assert.Equal(0, rig.H.Host.Server.Sessions.Count());                                           // it closed what it opened
                CastVerdict known;
                Assert.True(rig.Service.Source.TryGetKnownVerdict(rig.Link, out known));                       // and remembered the answer
                Assert.Equal("audio MP2", known.Reason);
            }
        }

        [Fact]
        public async Task CheckingAChannelThatIsAlreadyOpenDoesNotThrowAndTheOpenSessionCanBeAnalysedInstead()
        {
            using (var rig = new ServiceRig())
            {
                rig.H.Opened = await rig.H.Host.OpenStreamAsync(rig.Link, new AceEngine.P2P.OpenOptions(), CancellationToken.None);
                var cc = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:1", Address = IPAddress.Loopback, Capabilities = CastCapabilities.Hls };
                var v = await rig.Service.CheckAsync(rig.Link, cc, CancellationToken.None);
                Assert.Equal(CastSupport.Unknown, v.Support);
                Assert.Contains("ya está abierto", v.Issues.Single().Message);
                var live = (AceEngine.Live.LiveSession)rig.H.Opened.Session;
                var v2 = await rig.Service.CheckOpenSessionAsync(live, cc, CancellationToken.None);
                Assert.True(v2.IsCompatible, v2.ToString());
            }
        }

        [Fact]
        public async Task AnUnsupportedTargetKindIsRefused()
        {
            using (var rig = new ServiceRig())
            {
                var res = await rig.Service.SendAsync(new CastTarget { Kind = CastTargetKind.PApp, Id = "p:1", Address = IPAddress.Loopback }, rig.Link, "X", CancellationToken.None);
                Assert.Equal(CastFailure.InvalidTarget, res.Failure);
                var res2 = await rig.Service.SendAsync(new CastTarget { Kind = CastTargetKind.Dlna, Id = "dlna:x", Address = IPAddress.Loopback }, rig.Link, "X", CancellationToken.None);
                Assert.Equal(CastFailure.InvalidTarget, res2.Failure);
            }
        }

        [Fact]
        public async Task ACastTargetGetsTheHlsAndTheShellDoesTheLoad()
        {
            using (var rig = new ServiceRig())
            {
                var cc = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:1", Name = "Salon", Address = IPAddress.Loopback, Capabilities = CastCapabilities.Hls };
                var res = await rig.Service.SendAsync(cc, rig.Link, "Canal", CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.EndsWith("/master.m3u8", res.Url);
                Assert.EndsWith("/index.m3u8", res.MediaPlaylistUrl);
                Assert.Equal("application/vnd.apple.mpegurl", res.ContentType);
                Assert.True(res.IsLive);
                Assert.Equal("Canal", res.Title);
                Assert.Equal(CastState.Casting, rig.Service.State);
                using (var http = new HttpClient())
                {
                    var pl = HlsPlaylistModel.Parse(await http.GetStringAsync(res.MediaPlaylistUrl));
                    Assert.True(pl.Segments.Count >= 3);
                }
                Assert.Equal(0, rig.Renderer.Calls.Count);                                   // no DLNA traffic for a Cast target
                rig.Service.Stop();
                Assert.Equal(CastState.Stopped, rig.Service.State);
            }
        }

        [Fact]
        public async Task AnIncompatibleChannelForAChromecastIsReportedByTheService()
        {
            using (var rig = new ServiceRig(g => { g.Audio = TestAudio.Ac3; }))
            {
                var cc = new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:1", Address = IPAddress.Loopback, Capabilities = CastCapabilities.Hls };
                var res = await rig.Service.SendAsync(cc, rig.Link, "Canal", CancellationToken.None);
                Assert.False(res.Ok);
                Assert.Equal(CastFailure.Incompatible, res.Failure);
                Assert.Contains("audio AC-3", res.Reason);
                Assert.Equal(CastState.Incompatible, rig.Service.State);
            }
        }

        [Fact]
        public async Task CancellingDuringTheSendCleansUp()
        {
            using (var rig = new ServiceRig(cfg: r => { r.FetchStream = false; }, opt: o => o.PlayWait = TimeSpan.FromSeconds(20)))
            {
                var cts = new CancellationTokenSource();
                var task = rig.Service.SendAsync(rig.Target, rig.Link, "X", cts.Token);
                await UntilAsync(() => rig.Renderer.Count("Play") >= 1, 20000);
                cts.Cancel();
                var res = await task;
                Assert.False(res.Ok);
                Assert.Equal(CastFailure.Cancelled, res.Failure);
                Assert.Equal(CastState.Stopped, rig.Service.State);
                Assert.Null(rig.Service.CurrentTarget);
            }
        }

        [Fact]
        public async Task DiscoveryUpdatesTheListThroughTheService()
        {
            using (var rig = new ServiceRig())
            {
                Assert.Single(rig.Service.Targets.Items);
                // no UDP transport given: the real one runs; on a CI machine nothing answers, so the dlna targets are replaced by an empty set
                var o = new CastServiceOptions { Discovery = new DlnaDiscoveryOptions { Ssdp = new AceEngine.Net.SsdpOptions { Window = TimeSpan.FromMilliseconds(300), Retries = 1, SearchTargets = new List<string>(DlnaDiscoveryOptions.MediaRendererTargets), UseUnicastToGateway = false } } };
                using (var s2 = new CastService(() => rig.H.Host, new FakeNet(), new AceEngine.Net.SystemUdpTransport(), o))
                {
                    s2.Targets.Update("googlecast", new[] { new CastTarget { Kind = CastTargetKind.GoogleCast, Id = "gcast:1", Name = "Salon", Address = IPAddress.Parse("192.168.1.80") } });
                    var d = await s2.DiscoverDlnaAsync(CancellationToken.None);
                    Assert.NotNull(d);
                    Assert.Contains(s2.Targets.Items, t => t.Kind == CastTargetKind.GoogleCast);              // the Cast ones are untouched by the DLNA refresh
                }
            }
        }
    }
}
