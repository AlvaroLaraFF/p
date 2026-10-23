using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Streaming;
using Xunit;

namespace AceEngine.Tests.Live
{
    /// <summary>Sets up a complete engine against simulated live broadcasters and reads the stream through the local URL.</summary>
    internal sealed class LiveHarness : IDisposable
    {
        public readonly LiveStreamModel Model;
        public readonly List<LiveSourceSimulator> Sources = new List<LiveSourceSimulator>();
        public AceEngineHost Host;
        public OpenedStream Opened;
        public LiveSession Session { get { return (LiveSession)Opened.Session; } }
        private readonly string _file;
        public EngineOptions Options;

        public LiveHarness(LiveSimOptions sim, Action<EngineOptions> tweak = null)
        {
            if (Environment.GetEnvironmentVariable("ACE_TEST_LOG") == "1")
            {
                AceEngine.Util.AceLog.MinLevel = AceEngine.Util.LogLevel.Debug;
                var path = Environment.GetEnvironmentVariable("ACE_TEST_LOGFILE");
                AceEngine.Util.AceLog.Sink = (l, tag, m) =>
                {
                    var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + tag + "] " + m;
                    if (path != null) File.AppendAllText(path, line + "\n"); else Console.Error.WriteLine(line);
                };
            }
            Model = new LiveStreamModel(sim);
            _file = Path.Combine(Path.GetTempPath(), "sim-" + Guid.NewGuid().ToString("N") + ".acelive");
            Options = new EngineOptions
            {
                EnableListener = false, EnableDht = false, EnableTrackers = false,
                ConnectTimeout = TimeSpan.FromSeconds(3), HandshakeTimeout = TimeSpan.FromSeconds(3)
            };
            Options.Http.ReadStallTimeout = TimeSpan.FromSeconds(30);
            if (tweak != null) tweak(Options);
        }

        public LiveSourceSimulator AddSource(SimPeerBehavior b = null, LiveStreamModel model = null)
        {
            var s = new LiveSourceSimulator(model ?? Model, b);
            Sources.Add(s);
            return s;
        }

        /// <summary>Creates and starts the engine without opening the live stream; returns the path of the transport file (so someone else can open it).</summary>
        public string PrepareHost()
        {
            File.WriteAllBytes(_file, Model.TransportBytes);
            foreach (var s in Sources) Options.StaticPeers.Add(s.EndPoint.ToString());
            Host = new AceEngineHost(Options);
            Host.Start();
            return _file;
        }

        public async Task OpenAsync(AceLink link = null)
        {
            File.WriteAllBytes(_file, Model.TransportBytes);
            foreach (var s in Sources) Options.StaticPeers.Add(s.EndPoint.ToString());
            Host = new AceEngineHost(Options);
            Host.Start();
            Opened = await Host.OpenStreamAsync(link ?? AceLink.Parse(_file), new OpenOptions(), CancellationToken.None);
        }

        /// <summary>
        /// Reads 'seconds' real seconds of the stream over HTTP. If bytesPerSecond > 0 the reader is a simulated player that consumes at that pace
        /// (with a fast initial buffer of 'burstBytes'); otherwise it reads as fast as it can.
        /// </summary>
        public async Task<SimStreamVerifier> ReadAsync(double seconds, long bytesPerSecond = 0, long burstBytes = 0, Action<long> onProgress = null, string url = null)
        {
            var v = new SimStreamVerifier();
            using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            {
                try
                {
                    using (var resp = await http.GetAsync(url ?? Opened.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        resp.EnsureSuccessStatusCode();
                        Assert.Equal("video/mp2t", resp.Content.Headers.ContentType.MediaType);
                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        {
                            var buf = new byte[32 * 1024];
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            long total = 0;
                            while (true)
                            {
                                int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                                if (n <= 0) break;
                                v.Feed(buf, 0, n);
                                total += n;
                                if (onProgress != null) onProgress(total);
                                if (bytesPerSecond > 0 && total > burstBytes)
                                {
                                    double due = (double)(total - burstBytes) / bytesPerSecond;
                                    double ahead = due - sw.Elapsed.TotalSeconds;
                                    if (ahead > 0.005) await Task.Delay(TimeSpan.FromSeconds(ahead), cts.Token);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }
            return v;
        }

        public void Dispose()
        {
            try { if (Host != null) Host.Dispose(); } catch { }
            foreach (var s in Sources) try { s.Dispose(); } catch { }
            try { File.Delete(_file); } catch { }
        }
    }


    public class LiveSmokeTests
    {
        [Fact]
        public async Task PlaysASimulatedLiveStreamThroughTheLocalUrl()
        {
            var sim = new LiveSimOptions { TimeScale = 4 };
            using (var h = new LiveHarness(sim))
            {
                var src = h.AddSource();
                await h.OpenAsync();
                var v = await h.ReadAsync(8);
                Assert.True(v.Bytes > 500000, "bytes " + v.Bytes + " " + h.Session.DebugInfo);
                Assert.Equal(0, v.SyncErrors);
                Assert.Equal(0, v.CorruptPackets);
                Assert.Empty(v.Gaps);
                Assert.Equal(0, v.Duplicates);
                Assert.Empty(src.Violations);
                Assert.False(src.AnyClientSignatureInvalid);
            }
        }
    }
}
