using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast;
using AceEngine.Live;
using AceEngine.Net;
using AceEngine.Sim;
using AceEngine.Tests.Live;
using Xunit;

namespace AceEngine.Tests.Cast
{
    internal sealed class FakeNet : INetworkInfo
    {
        public IPAddress Local = IPAddress.Loopback;
        public IReadOnlyList<LocalInterface> GetInterfaces() { return new[] { new LocalInterface { Name = "lo", Address = Local, Mask = IPAddress.Parse("255.0.0.0") } }; }
        public IPAddress LocalAddressFor(IPAddress remote) { return Local; }
        public event Action AddressesChanged { add { } remove { } }
    }

    /// <summary>A simulated channel carrying a real-looking H.264 + audio transport stream, opened through the whole engine.</summary>
    internal sealed class CastRig : IDisposable
    {
        public readonly LiveHarness H;
        public CastSource Source;
        public readonly TsGen Gen;
        public const int Bitrate = 100000;

        public static byte[] Material(Action<TsGen> cfg, double seconds, out double bytesPerSecond)
        {
            var g = new TsGen { IdrBytes = 34000, PBytes = 3300 };
            if (cfg != null) cfg(g);
            var data = g.Generate(seconds);
            bytesPerSecond = data.Length / seconds;
            return data;
        }

        public CastRig(Action<TsGen> cfg = null, double seconds = 200, Action<CastSourceOptions> opt = null, long timeshift = 24L << 20)
        {
            double bps;
            var data = Material(cfg, seconds, out bps);
            var sim = new LiveSimOptions
            {
                TimeScale = 4, Bitrate = (int)bps, PieceLength = 65536, ChunkLength = 16384, WindowPieces = 80, StartMisalignment = 0,
                ContentProvider = (off, n) =>
                {
                    var r = new byte[n];
                    for (int i = 0; i < n; i += 188)
                    {
                        long o = off + i;
                        for (int k = 0; k < 188 && i + k < n; k++) r[i + k] = (o + k) < data.Length ? data[o + k] : (k == 0 ? (byte)0x47 : (byte)(k == 1 ? 0x1F : (k == 2 ? 0xFF : 0x10)));
                    }
                    return r;
                }
            };
            H = new LiveHarness(sim, o => { o.Live.TimeshiftBytes = timeshift; o.Live.MaxBufferBytes = 4 << 20; o.Live.BufferSeconds = 4; });
            H.AddSource();
            var so = new CastSourceOptions { AllowAnyTarget = true, OpenTimeout = TimeSpan.FromSeconds(30), AnalysisTimeout = TimeSpan.FromSeconds(8), StartTimeout = TimeSpan.FromSeconds(40) };
            if (opt != null) opt(so);
            Source = new CastSource(() => H.Host, new FakeNet(), so);
        }

        public async Task<LiveSession> OpenAsync()
        {
            await H.OpenAsync();
            return H.Session;
        }

        public void Dispose() { Source.Dispose(); H.Dispose(); }
    }

    [Collection(CastIsolatedCollection.Name)]
    public class CastSourceTests
    {
        private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url)
        {
            return await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }

        [Fact]
        public async Task ACompatibleChannelIsSentAsLiveHlsEndToEnd()
        {
            using (var rig = new CastRig())
            {
                var live = await rig.OpenAsync();
                var res = await rig.Source.StartAsync(new CastRequest { Session = live, Name = "Canal de prueba", Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(res.Ok, res.Reason + " | " + res.Detail);
                Assert.Equal(CastState.Casting, rig.Source.State);
                Assert.Equal("application/vnd.apple.mpegurl", res.ContentType);
                Assert.True(res.IsLive);
                Assert.Equal("Canal de prueba", res.Title);
                Assert.StartsWith("http://127.0.0.1:", res.Url);
                Assert.EndsWith("/master.m3u8", res.Url);                                    // the SPS was read, so the master playlist can say the codecs
                Assert.EndsWith("/index.m3u8", res.MediaPlaylistUrl);
                Assert.True(res.Verdict.IsCompatible, res.Verdict.ToString());

                using (var http = new HttpClient())
                {
                    var master = await http.GetStringAsync(res.Url);
                    Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=", master);
                    Assert.Contains("RESOLUTION=1920x1080", master);
                    Assert.Contains("FRAME-RATE=25.000", master);
                    Assert.Contains("CODECS=\"avc1.640028,mp4a.40.2\"", master);
                    Assert.EndsWith("\nindex.m3u8\n", master);
                    var pr = await GetAsync(http, res.MediaPlaylistUrl);
                    Assert.Equal(200, (int)pr.StatusCode);
                    Assert.Equal("application/vnd.apple.mpegurl", pr.Content.Headers.ContentType.MediaType);
                    Assert.Equal("*", pr.Headers.GetValues("Access-Control-Allow-Origin").Single());
                    var pl = HlsPlaylistModel.Parse(await pr.Content.ReadAsStringAsync());
                    Assert.True(pl.Segments.Count >= 3, "segments " + pl.Segments.Count);
                    string baseUrl = res.MediaPlaylistUrl.Substring(0, res.MediaPlaylistUrl.LastIndexOf('/') + 1);
                    foreach (var s in pl.Segments)
                    {
                        var sr = await GetAsync(http, baseUrl + s.Uri);
                        Assert.Equal(200, (int)sr.StatusCode);
                        Assert.Equal("video/mp2t", sr.Content.Headers.ContentType.MediaType);
                        var data = await sr.Content.ReadAsByteArrayAsync();
                        var pk = TsReader.Parse(data, 0, data.Length);
                        Assert.Equal(0, pk[0].Pid); Assert.Equal(0x1000, pk[1].Pid);
                        Assert.True(pk[2].Idr);
                        Assert.InRange(s.Duration, 3.9, 4.1);
                    }
                }
                var st = rig.Source.GetStats();
                Assert.Equal(CastState.Casting, st.State);
                Assert.True(st.PlaylistRequests >= 1 && st.SegmentRequests >= 3);
                Assert.True(st.SegmentsPublished >= 3);
                Assert.True(st.BytesSent > 100000);
                rig.Source.Stop();
                Assert.Equal(CastState.Stopped, rig.Source.State);
                using (var http = new HttpClient())
                    await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(res.Url));
            }
        }

        [Fact]
        public async Task AnMp2ChannelIsRefusedWithTheReasonAndNoServerIsOpened()
        {
            using (var rig = new CastRig(g => { g.Audio = TestAudio.Mp2; }))
            {
                var live = await rig.OpenAsync();
                var res = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.False(res.Ok);
                Assert.Equal(CastFailure.Incompatible, res.Failure);
                Assert.Contains("audio MP2", res.Reason);
                Assert.True(res.Verdict.IsIncompatible);
                Assert.Equal(CastState.Incompatible, rig.Source.State);
                Assert.Equal("", res.Url);
                // the channel the caller owns is left alone
                Assert.NotEqual(AceEngine.Streaming.SessionState.Stopped, live.State);
            }
        }

        [Fact]
        public async Task AnHevcChannelIsRefused()
        {
            using (var rig = new CastRig(g => { g.Video = TestVideo.Hevc; }))
            {
                var live = await rig.OpenAsync();
                var res = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.Equal(CastFailure.Incompatible, res.Failure);
                Assert.Contains("H.265", res.Reason);
            }
        }

        [Fact]
        public async Task TheAnalysisOfAnOpenChannelIsQuick()
        {
            using (var rig = new CastRig())
            {
                var live = await rig.OpenAsync();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var v = await CastAnalysis.AnalyzeSessionAsync(live, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(8), CancellationToken.None);
                Assert.True(v.IsCompatible, v.ToString());
                Assert.True(sw.Elapsed.TotalSeconds < 6, "took " + sw.Elapsed.TotalSeconds);
                Assert.Equal("H.264 High@4.0 1920x1080p 25 fps", v.VideoDescription);
            }
        }

        [Fact]
        public async Task AMissingOrWrongTargetIsRefusedBeforeAnythingIsOpened()
        {
            using (var rig = new CastRig(opt: o => o.AllowAnyTarget = false))
            {
                var live = await rig.OpenAsync();
                var a = await rig.Source.StartAsync(new CastRequest { Session = live, Target = null }, CancellationToken.None);
                Assert.Equal(CastFailure.InvalidTarget, a.Failure);
                var b = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Parse("8.8.8.8") }, CancellationToken.None);
                Assert.Equal(CastFailure.InvalidTarget, b.Failure);
                Assert.Contains("red local", b.Reason);
                var c = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.IPv6Loopback }, CancellationToken.None);
                Assert.Equal(CastFailure.InvalidTarget, c.Failure);
            }
        }

        [Fact]
        public async Task WithoutALiveBufferHlsIsRefused()
        {
            using (var rig = new CastRig(timeshift: 0))
            {
                var live = await rig.OpenAsync();
                var res = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.Equal(CastFailure.TimeshiftDisabled, res.Failure);
            }
        }

        [Fact]
        public async Task StartingAnotherChannelStopsTheFirstSending()
        {
            using (var rig = new CastRig())
            {
                var live = await rig.OpenAsync();
                var r1 = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(r1.Ok, r1.Reason);
                var r2 = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(r2.Ok, r2.Reason);
                Assert.NotEqual(r1.Url, r2.Url);                                           // a new token every time
                using (var http = new HttpClient())
                {
                    await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(r1.Url));
                    Assert.Equal(200, (int)(await http.GetAsync(r2.Url)).StatusCode);
                }
            }
        }

        [Fact]
        public async Task TheServerStopsWhenTheChannelEnds()
        {
            using (var rig = new CastRig())
            {
                var live = await rig.OpenAsync();
                var r = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(r.Ok, r.Reason);
                var states = new List<CastState>();
                rig.Source.StateChanged += s => { lock (states) states.Add(s); };
                live.Dispose();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (rig.Source.State == CastState.Casting && sw.Elapsed.TotalSeconds < 8) await Task.Delay(100);
                Assert.Equal(CastState.Stopped, rig.Source.State);
            }
        }

        [Fact]
        public async Task TheSourceStopsItselfWhenNobodyAsksForAnything()
        {
            using (var rig = new CastRig(opt: o => o.IdleTimeout = TimeSpan.FromSeconds(2)))
            {
                var live = await rig.OpenAsync();
                var r = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(r.Ok, r.Reason);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (rig.Source.State == CastState.Casting && sw.Elapsed.TotalSeconds < 10) await Task.Delay(100);
                Assert.Equal(CastState.Stopped, rig.Source.State);
            }
        }

        [Fact]
        public async Task KeepAlivePostponesTheIdleStop()
        {
            using (var rig = new CastRig(opt: o => o.IdleTimeout = TimeSpan.FromSeconds(3)))
            {
                var live = await rig.OpenAsync();
                var r = await rig.Source.StartAsync(new CastRequest { Session = live, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.True(r.Ok, r.Reason);
                for (int i = 0; i < 8; i++) { await Task.Delay(700); rig.Source.KeepAlive(); }
                Assert.Equal(CastState.Casting, rig.Source.State);
            }
        }

        [Fact]
        public async Task ProgressiveTsIsServedForDlnaWithItsHeadersWithoutAnySegmenter()
        {
            using (var rig = new CastRig())
            {
                var live = await rig.OpenAsync();
                var res = await rig.Source.StartAsync(new CastRequest
                {
                    Session = live, Target = IPAddress.Loopback, Delivery = CastDelivery.ProgressiveTs, Dlna = new DlnaStreamHeaders(),
                    Profile = CastProfile.Chromecast2
                }, CancellationToken.None);
                Assert.True(res.Ok, res.Reason + res.Detail);
                Assert.EndsWith("/live.ts", res.Url);
                Assert.Equal("video/mp2t", res.ContentType);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    var head = new HttpRequestMessage(HttpMethod.Head, res.Url);
                    var h = await http.SendAsync(head);
                    Assert.Equal("Streaming", h.Headers.GetValues("transferMode.dlna.org").Single());
                    using (var resp = await http.GetAsync(res.Url, HttpCompletionOption.ResponseHeadersRead))
                    using (var s = await resp.Content.ReadAsStreamAsync())
                    {
                        var buf = new byte[188 * 100];
                        int got = 0;
                        while (got < buf.Length) { int n = await s.ReadAsync(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                        Assert.Equal(buf.Length, got);
                        var pk = TsReader.Parse(buf, 0, buf.Length);                        // aligned and parsable
                        Assert.Equal(100, pk.Count);
                        Assert.Contains(pk, p => p.Pid == 0);
                    }
                }
            }
        }

        [Fact]
        public async Task TheVerdictIsRemembered()
        {
            using (var rig = new CastRig(g => { g.Audio = TestAudio.Ac3; }))
            {
                var live = await rig.OpenAsync();
                var link = AceEngine.Links.AceLink.Parse("0123456789abcdef0123456789abcdef01234567");
                CastVerdict v;
                Assert.False(rig.Source.TryGetKnownVerdict(link, out v));
                var res = await rig.Source.StartAsync(new CastRequest { Session = live, Link = null, Target = IPAddress.Loopback }, CancellationToken.None);
                Assert.Equal(CastFailure.Incompatible, res.Failure);
                Assert.Equal("audio AC-3", res.Verdict.Reason);
            }
        }
    }
}
