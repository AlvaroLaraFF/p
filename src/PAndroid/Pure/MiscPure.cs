using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>
    /// A single decoder at a time. Phones and TV sticks have few hardware video decoders and a second player created before the first one has let go of its own fails or steals it: the player of
    /// every attempt takes the gate before creating the decoder and gives it back when it has released it, and the next attempt waits for that.
    /// </summary>
    public sealed class DecoderGate
    {
        /// <summary>The gate of the app: every player backend uses it.</summary>
        public static readonly DecoderGate Shared = new DecoderGate();

        private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
        private int _waiting;

        /// <summary>true while a player holds the decoder.</summary>
        public bool IsHeld { get { return _sem.CurrentCount == 0; } }
        /// <summary>How many callers are waiting for it.</summary>
        public int Waiting { get { return Volatile.Read(ref _waiting); } }

        /// <summary>Waits for the gate. Returns the lease (dispose it to give the decoder back; disposing twice is harmless), or null if <paramref name="timeout"/> passed first. Cancellation throws OperationCanceledException.</summary>
        public async Task<IDisposable> TryAcquireAsync(TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                if (!await _sem.WaitAsync(timeout, ct).ConfigureAwait(false)) return null;
                return new Lease(_sem);
            }
            finally { Interlocked.Decrement(ref _waiting); }
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim _s;
            public Lease(SemaphoreSlim s) { _s = s; }
            public void Dispose() { var s = Interlocked.Exchange(ref _s, null); if (s != null) s.Release(); }
        }
    }

    /// <summary>Which of Media3's error codes (androidx.media3.common.PlaybackException.ERROR_CODE_*) are worth retrying.</summary>
    public static class ExoErrors
    {
        public const int ContainerUnsupported = 3003, ManifestUnsupported = 3004, DecodingFormatExceedsCapabilities = 4004, DecodingFormatUnsupported = 4005, CleartextNotPermitted = 2007;

        /// <summary>
        /// true when retrying is useless: a container or a format the device cannot play, or clear text blocked by the app's own policy. A dropped connection, a timeout, a decoder that could not
        /// start, a decoder the system took back, a malformed piece (a live stream can start in the middle of a packet) and behind-live-window are all worth another try.
        /// </summary>
        public static bool IsFatal(int errorCode)
        {
            switch (errorCode)
            {
                case ContainerUnsupported: case ManifestUnsupported: case DecodingFormatExceedsCapabilities: case DecodingFormatUnsupported: case CleartextNotPermitted:
                    return true;
            }
            return errorCode >= 6000 && errorCode < 7000;                      // DRM: nothing here is encrypted for us
        }
    }

    /// <summary>AudioManager.AUDIOFOCUS_* to the contract's AudioFocusChange.</summary>
    public static class AudioFocusMap
    {
        public const int Gain = 1, GainTransient = 2, GainTransientMayDuck = 3, GainTransientExclusive = 4, Loss = -1, LossTransient = -2, LossTransientCanDuck = -3;

        /// <summary>null for a value that is not a focus change.</summary>
        public static AudioFocusChange? FromAndroid(int focusChange)
        {
            switch (focusChange)
            {
                case Gain: case GainTransient: case GainTransientMayDuck: case GainTransientExclusive: return AudioFocusChange.Gained;
                case Loss: return AudioFocusChange.Lost;
                case LossTransient: return AudioFocusChange.LostTransient;
                case LossTransientCanDuck: return AudioFocusChange.Duck;
            }
            return null;
        }
    }

    public static class NetMath
    {
        /// <summary>The IPv4 mask of a prefix length (24 gives 255.255.255.0); null if the length is not 0-32.</summary>
        public static IPAddress PrefixToMask(int prefixLength)
        {
            if (prefixLength < 0 || prefixLength > 32) return null;
            uint m = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
            return new IPAddress(new[] { (byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m });
        }
    }

    public static class LogText
    {
        /// <summary>logcat cuts a line at about 4000 bytes: long text goes out in pieces (the break falls on a space or a line break when there is one close enough).</summary>
        public static IEnumerable<string> Chunks(string text, int max = 3500)
        {
            if (string.IsNullOrEmpty(text)) { yield return ""; yield break; }
            int pos = 0;
            while (pos < text.Length)
            {
                int take = Math.Min(max, text.Length - pos);
                if (pos + take < text.Length)
                {
                    int brk = text.LastIndexOfAny(new[] { ' ', '\n' }, pos + take - 1, Math.Min(take, 200));
                    if (brk > pos) take = brk - pos + 1;
                }
                yield return text.Substring(pos, take);
                pos += take;
            }
        }
    }
}

namespace PAndroid
{
    /// <summary>The text the interface and the log show for the tracks of a stream: "H.264 1920x1080 25 fps 4000 kbps / audio MP2 48000 Hz 2 ch 192 kbps".</summary>
    public static class StreamInfoText
    {
        /// <summary>A readable name for a media type ("video/avc" gives "H.264"); the type itself if it is not a known one.</summary>
        public static string CodecName(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return "?";
            switch (mime.ToLowerInvariant())
            {
                case "video/avc": return "H.264";
                case "video/hevc": return "H.265";
                case "video/mpeg2": return "MPEG-2";
                case "video/mp4v-es": return "MPEG-4";
                case "video/x-vnd.on2.vp8": return "VP8";
                case "video/x-vnd.on2.vp9": return "VP9";
                case "video/av01": return "AV1";
                case "audio/mp4a-latm": return "AAC";
                case "audio/mpeg-l2": return "MP2";
                case "audio/mpeg": return "MP3";
                case "audio/ac3": return "AC-3";
                case "audio/eac3": return "E-AC-3";
                case "audio/vnd.dts": case "audio/vnd.dts.hd": return "DTS";
                case "audio/opus": return "Opus";
                case "audio/vorbis": return "Vorbis";
                case "audio/flac": return "FLAC";
                case "audio/raw": return "PCM";
            }
            int slash = mime.IndexOf('/');
            return slash >= 0 && slash + 1 < mime.Length ? mime.Substring(slash + 1) : mime;
        }

        /// <summary>Zero or negative numbers mean "unknown" and are left out.</summary>
        public static string Build(string videoMime, int width, int height, float frameRate, int videoBitrate, string audioMime, int sampleRate, int channels, int audioBitrate)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(videoMime) || width > 0)
            {
                sb.Append(CodecName(videoMime));
                if (width > 0 && height > 0) sb.Append(' ').Append(width).Append('x').Append(height);
                if (frameRate > 0) sb.Append(' ').Append(frameRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(" fps");
                if (videoBitrate > 0) sb.Append(' ').Append(videoBitrate / 1000).Append(" kbps");
            }
            if (!string.IsNullOrEmpty(audioMime) || sampleRate > 0)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append("audio ").Append(CodecName(audioMime));
                if (sampleRate > 0) sb.Append(' ').Append(sampleRate).Append(" Hz");
                if (channels > 0) sb.Append(' ').Append(channels).Append(" ch");
                if (audioBitrate > 0) sb.Append(' ').Append(audioBitrate / 1000).Append(" kbps");
            }
            return sb.ToString();
        }
    }
}

namespace PAndroid
{
    /// <summary>Tells the exceptions that are only the cancellation of something somebody gave up on from real failures, for the log level of an unobserved task exception.</summary>
    public static class TaskFailures
    {
        /// <summary>
        /// true when every exception in the tree is a cancellation: OperationCanceledException, ObjectDisposedException (a socket closed under a pending call), or a SocketException that says the
        /// operation was aborted (SocketError.OperationAborted, or errno 125 ECANCELED, which is what Android's connect reports when its socket is closed while connecting).
        /// </summary>
        public static bool IsCancellation(System.Exception ex)
        {
            if (ex == null) return false;
            var agg = ex as System.AggregateException;
            if (agg != null)
            {
                if (agg.InnerExceptions.Count == 0) return false;
                foreach (var inner in agg.InnerExceptions) if (!IsCancellation(inner)) return false;
                return true;
            }
            if (ex is System.OperationCanceledException || ex is System.ObjectDisposedException) return true;
            var se = ex as System.Net.Sockets.SocketException;
            if (se != null) return se.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted || se.NativeErrorCode == 125;
            return false;
        }
    }
}
