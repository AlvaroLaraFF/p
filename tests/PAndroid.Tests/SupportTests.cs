using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PUi.Platform;
using Xunit;

namespace PAndroid.Tests
{
    public class SupportTests
    {
        // ------------------------------------------------------------------ decoder gate

        [Fact]
        public async Task OnlyOnePlayerHoldsTheDecoderAtATime()
        {
            var gate = new DecoderGate();
            var first = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(first); Assert.True(gate.IsHeld);
            var second = gate.TryAcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            Assert.False(second.IsCompleted); Assert.Equal(1, gate.Waiting);
            first.Dispose();
            var lease = await second;
            Assert.NotNull(lease); Assert.True(gate.IsHeld);
            lease.Dispose();
            Assert.False(gate.IsHeld); Assert.Equal(0, gate.Waiting);
        }

        [Fact]
        public async Task AWaitThatTimesOutGivesNullAndTakesNothing()
        {
            var gate = new DecoderGate();
            var held = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            var none = await gate.TryAcquireAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None);
            Assert.Null(none);
            held.Dispose();
            var again = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(again);
        }

        [Fact]
        public async Task CancellingTheWaitThrowsAndLeavesTheGateAlone()
        {
            var gate = new DecoderGate();
            var held = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            var cts = new CancellationTokenSource();
            var waiting = gate.TryAcquireAsync(TimeSpan.FromSeconds(30), cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, gate.Waiting);
            held.Dispose();
            Assert.False(gate.IsHeld);
        }

        [Fact]
        public async Task DisposingALeaseTwiceDoesNotOpenTheGateForTwoPlayers()
        {
            var gate = new DecoderGate();
            var a = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            a.Dispose(); a.Dispose();
            var b = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(b);
            Assert.Null(await gate.TryAcquireAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            b.Dispose();
        }

        [Fact]
        public async Task ManyAttemptsInARowAreServedOneByOne()
        {
            var gate = new DecoderGate();
            int inside = 0, maxInside = 0;
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                var l = await gate.TryAcquireAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                int n = Interlocked.Increment(ref inside);
                int seen; while ((seen = Volatile.Read(ref maxInside)) < n && Interlocked.CompareExchange(ref maxInside, n, seen) != seen) { }
                await Task.Delay(2);
                Interlocked.Decrement(ref inside);
                l.Dispose();
            }).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(1, maxInside);
        }

        // ------------------------------------------------------------------ player errors

        [Theory]
        [InlineData(3003)]       // container unsupported
        [InlineData(3004)]
        [InlineData(4004)]       // format exceeds capabilities
        [InlineData(4005)]       // format unsupported
        [InlineData(2007)]       // clear text not permitted
        [InlineData(6001)]       // DRM
        [InlineData(6999)]
        public void UnsupportedMediaIsFatal(int code) { Assert.True(ExoErrors.IsFatal(code)); }

        [Theory]
        [InlineData(1000)]       // unspecified
        [InlineData(1002)]       // behind live window
        [InlineData(1003)]       // timeout
        [InlineData(2000)]
        [InlineData(2001)]       // connection failed
        [InlineData(2002)]       // connection timeout
        [InlineData(2003)]       // invalid content type
        [InlineData(2004)]       // bad http status
        [InlineData(3001)]       // malformed container: a live stream can start mid-packet
        [InlineData(4001)]       // decoder init failed
        [InlineData(4003)]       // decoding failed
        [InlineData(4006)]       // resources reclaimed
        [InlineData(5001)]       // audio track init
        [InlineData(7000)]
        public void ConnectionAndDecoderTroublesAreWorthARetry(int code) { Assert.False(ExoErrors.IsFatal(code)); }

        // ------------------------------------------------------------------ audio focus

        [Theory]
        [InlineData(1, AudioFocusChange.Gained)]
        [InlineData(2, AudioFocusChange.Gained)]
        [InlineData(-1, AudioFocusChange.Lost)]
        [InlineData(-2, AudioFocusChange.LostTransient)]
        [InlineData(-3, AudioFocusChange.Duck)]
        public void AndroidFocusChangesMapToTheContractsOnes(int android, AudioFocusChange expected)
        {
            Assert.Equal(expected, AudioFocusMap.FromAndroid(android));
        }

        [Fact]
        public void ANumberThatIsNoFocusChangeMapsToNothing() { Assert.Null(AudioFocusMap.FromAndroid(0)); Assert.Null(AudioFocusMap.FromAndroid(-7)); }

        // ------------------------------------------------------------------ network

        [Theory]
        [InlineData(0, "0.0.0.0")]
        [InlineData(8, "255.0.0.0")]
        [InlineData(16, "255.255.0.0")]
        [InlineData(24, "255.255.255.0")]
        [InlineData(25, "255.255.255.128")]
        [InlineData(30, "255.255.255.252")]
        [InlineData(32, "255.255.255.255")]
        public void APrefixLengthIsAMask(int prefix, string mask) { Assert.Equal(IPAddress.Parse(mask), NetMath.PrefixToMask(prefix)); }

        [Fact]
        public void AWrongPrefixLengthIsNoMask() { Assert.Null(NetMath.PrefixToMask(-1)); Assert.Null(NetMath.PrefixToMask(33)); }

        // ------------------------------------------------------------------ log

        [Fact]
        public void ShortTextIsOneChunkAndLongTextKeepsEverything()
        {
            Assert.Equal(new[] { "hello" }, LogText.Chunks("hello").ToArray());
            Assert.Equal(new[] { "" }, LogText.Chunks("").ToArray());
            var text = string.Join(" ", Enumerable.Range(0, 3000).Select(i => "word" + i));
            var chunks = LogText.Chunks(text, 1000).ToArray();
            Assert.True(chunks.Length > 10);
            Assert.All(chunks, c => Assert.True(c.Length <= 1000));
            Assert.Equal(text, string.Concat(chunks));
        }

        [Fact]
        public void ATextWithoutSpacesIsCutAtTheLimit()
        {
            var chunks = LogText.Chunks(new string('x', 2500), 1000).ToArray();
            Assert.Equal(new[] { 1000, 1000, 500 }, chunks.Select(c => c.Length).ToArray());
        }

        // ------------------------------------------------------------------ stream info text

        [Theory]
        [InlineData("video/avc", "H.264")]
        [InlineData("video/hevc", "H.265")]
        [InlineData("video/mpeg2", "MPEG-2")]
        [InlineData("audio/mpeg-L2", "MP2")]
        [InlineData("audio/mp4a-latm", "AAC")]
        [InlineData("audio/ac3", "AC-3")]
        [InlineData("audio/eac3", "E-AC-3")]
        [InlineData("audio/raw", "PCM")]
        [InlineData("audio/x-weird", "x-weird")]
        [InlineData("", "?")]
        [InlineData(null, "?")]
        public void CodecNamesAreReadable(string mime, string expected) { Assert.Equal(expected, StreamInfoText.CodecName(mime)); }

        [Fact]
        public void TheStreamInfoLinesTheTracksUpLikeTheTvDoes()
        {
            Assert.Equal("H.264 1920x1080 25 fps 4000 kbps / audio MP2 48000 Hz 2 ch 192 kbps", StreamInfoText.Build("video/avc", 1920, 1080, 25f, 4000000, "audio/mpeg-L2", 48000, 2, 192000));
            Assert.Equal("H.264 1280x720 29.97 fps / audio AAC 44100 Hz 2 ch", StreamInfoText.Build("video/avc", 1280, 720, 29.97f, 0, "audio/mp4a-latm", 44100, 2, 0));
        }

        [Fact]
        public void AStreamWithOnlyOneKindOfTrackSaysOnlyThat()
        {
            Assert.Equal("audio AAC 48000 Hz 2 ch", StreamInfoText.Build(null, 0, 0, 0, 0, "audio/mp4a-latm", 48000, 2, 0));
            Assert.Equal("H.264 640x360", StreamInfoText.Build("video/avc", 640, 360, 0, 0, null, 0, 0, 0));
            Assert.Equal("", StreamInfoText.Build(null, 0, 0, 0, 0, null, 0, 0, 0));
        }

        // ------------------------------------------------------------------ task failures

        [Fact]
        public void ACancelledConnectIsACancellationWhateverWrapsIt()
        {
            var se = new System.Net.Sockets.SocketException(125);                                 // ECANCELED: what Android reports when the socket is closed while connecting
            Assert.True(TaskFailures.IsCancellation(se));
            Assert.True(TaskFailures.IsCancellation(new AggregateException(se)));
            Assert.True(TaskFailures.IsCancellation(new AggregateException(new AggregateException(new OperationCanceledException()), new ObjectDisposedException("socket"))));
            Assert.True(TaskFailures.IsCancellation(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.OperationAborted)));
        }

        [Fact]
        public void ARealFailureIsNotACancellationEvenNextToOne()
        {
            Assert.False(TaskFailures.IsCancellation(null));
            Assert.False(TaskFailures.IsCancellation(new InvalidOperationException("bug")));
            Assert.False(TaskFailures.IsCancellation(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
            Assert.False(TaskFailures.IsCancellation(new AggregateException(new System.Net.Sockets.SocketException(125), new NullReferenceException())));
            Assert.False(TaskFailures.IsCancellation(new AggregateException()));
        }
    }
}
