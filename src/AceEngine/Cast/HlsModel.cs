using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AceEngine.Cast
{
    /// <summary>Parameters of the live HLS segmenter.</summary>
    public sealed class HlsOptions
    {
        /// <summary>Target length of a segment. A segment is cut at the key frame that brings it closest to this.</summary>
        public double TargetSegmentSeconds = 4;
        /// <summary>Segments shorter than this are not published (a break right after a cut would leave a sliver).</summary>
        public double MinSegmentSeconds = 0.5;
        /// <summary>Without a key frame for this long (media time) the channel is declared not segmentable.</summary>
        public double MaxSegmentSeconds = 12;
        /// <summary>Segments listed in the playlist.</summary>
        public int WindowSegments = 8;
        /// <summary>Segments that stay fetchable after leaving the playlist (a slow client may still ask for them; RFC 8216 6.2.2).</summary>
        public int RetainedSegments = 4;
        /// <summary>EXT-X-PROGRAM-DATE-TIME on every segment (a consistent chain, one EXTINF after the other).</summary>
        public bool ProgramDateTime = true;
        /// <summary>When the segmenter starts on a buffer that already holds a lot, only this much (media time) before the edge is segmented.</summary>
        public double StartLookbackSeconds = 24;
        /// <summary>A segment is only listed while it lies in the newer part of the buffer: this fraction of the capacity is kept in reserve so the data is not recycled under a slow client.</summary>
        public double ReserveFraction = 0.3;
        /// <summary>A segment bigger than this (composed) is not served.</summary>
        public int MaxSegmentBytes = 24 * 1024 * 1024;
        /// <summary>
        /// Reorder the NAL units at the start of each access unit into the standard order (AUD, SPS, PPS, SEI, slices) when the encoder sent them differently: some real channels
        /// make Shaka Player (the Cast receiver's HLS player) fail with a decode error otherwise. See <see cref="AnnexBFix"/>.
        /// </summary>
        public bool NormalizeNalOrder = true;
        /// <summary>Time source (tests).</summary>
        public Func<DateTime> UtcNow;
    }

    /// <summary>What the segmenter learned about the program it is cutting: PIDs and stream types, enough to write a PAT and a PMT.</summary>
    internal sealed class HlsFormat
    {
        public int ProgramNumber = 1, PmtPid, PcrPid, VideoPid, VideoType, AudioPid = -1, AudioType;
        public byte[] AudioDescriptors;
        public string Signature = "";

        /// <summary>PCR travels on a PID that is neither the video nor the audio: its packets are copied as they are.</summary>
        public bool PcrSeparate { get { return PcrPid != VideoPid && PcrPid != AudioPid; } }
    }

    /// <summary>One published segment: byte ranges of the live buffer plus what the playlist says about it. Immutable once published.</summary>
    internal sealed class HlsSegment
    {
        public long Seq;
        /// <summary>Video (and PCR) packets live in [Start, End); audio packets in [AudioStart, AudioEnd), which can reach past End (the audio PES that was open at the cut finishes in this segment).</summary>
        public long Start, End, AudioStart, AudioEnd;
        public double Duration;
        /// <summary>The timeline restarts here: EXT-X-DISCONTINUITY goes in front of the segment.</summary>
        public bool Discontinuity;
        public DateTime ProgramDate;
        public HlsFormat Format;
        public long FirstPts = -1;
        /// <summary>Media time (buffer clock, ms) of the start of the segment.</summary>
        public double StartClockMs;
        public DateTime PublishedUtc;
        /// <summary>Media time (buffer clock, ms) of the end of the segment, for the delay statistics.</summary>
        public double EndClockMs;

        public string Name { get { return "s" + Seq.ToString(CultureInfo.InvariantCulture) + ".ts"; } }
        public long RawBytes { get { return Math.Max(End, AudioEnd) - Start; } }

        // while open
        internal long LastPts = -1;
        internal long MaxPtsDelta;
        internal int Aus;
    }

    /// <summary>Statistics of a segmenter.</summary>
    public sealed class HlsStats
    {
        public long SegmentsPublished, SegmentsListed, Discontinuities;
        public double LastSegmentSeconds, WindowSeconds, TargetSeconds;
        /// <summary>Media time between the end of the newest segment and the edge of the buffer: how far the playlist is behind what has arrived.</summary>
        public double PlaylistBehindEdgeSeconds;
        public long ScannedBytes, ComposedSegments, ComposeFailures, NalOrderFixes;
        public bool Ended, Failed;
        public string FailureReason = "";
        public CastIssueCode FailureCode;
        public int TargetDuration;
        public long FirstSeq, LastSeq;
    }

    /// <summary>A segment ready to be sent. Dispose it to give the buffer back to the pool.</summary>
    internal sealed class SegmentLease : IDisposable
    {
        private byte[] _buf;
        public int Length { get; private set; }
        public byte[] Buffer { get { return _buf; } }

        internal SegmentLease(byte[] buf, int length) { _buf = buf; Length = length; }

        public void Dispose()
        {
            var b = _buf;
            _buf = null;
            if (b != null) SegmentPool.Return(b);
        }
    }

    /// <summary>A small pool of big byte arrays: segments are a few MiB and one is built every few seconds, which would otherwise churn the large object heap.</summary>
    internal static class SegmentPool
    {
        private const int Granule = 256 * 1024;
        private const int MaxPooled = 3;
        private static readonly object Gate = new object();
        private static readonly List<byte[]> Free = new List<byte[]>();

        public static byte[] Rent(int size)
        {
            int want = (size + Granule - 1) / Granule * Granule;
            lock (Gate)
            {
                int best = -1;
                for (int i = 0; i < Free.Count; i++)
                    if (Free[i].Length >= size && (best < 0 || Free[i].Length < Free[best].Length)) best = i;
                if (best >= 0) { var b = Free[best]; Free.RemoveAt(best); return b; }
            }
            return new byte[want];
        }

        public static void Return(byte[] b)
        {
            lock (Gate)
            {
                if (b.Length > 32 * 1024 * 1024) return;
                if (Free.Count >= MaxPooled)
                {
                    int smallest = 0;
                    for (int i = 1; i < Free.Count; i++) if (Free[i].Length < Free[smallest].Length) smallest = i;
                    if (Free[smallest].Length >= b.Length) return;
                    Free.RemoveAt(smallest);
                }
                Free.Add(b);
            }
        }

        public static int PooledCount { get { lock (Gate) return Free.Count; } }
    }

    /// <summary>What an HTTP server needs from a live HLS source.</summary>
    internal interface IHlsContent
    {
        /// <summary>The playlist text, or null if there is nothing to list yet. 'ended' = it carries EXT-X-ENDLIST.</summary>
        string GetPlaylist(out bool ended);

        /// <summary>The segment, or null if it is unknown or no longer in memory.</summary>
        SegmentLease GetSegment(long seq);
    }

    /// <summary>Writes media playlists (RFC 8216).</summary>
    internal static class HlsPlaylist
    {
        /// <summary>
        /// A master playlist with ONE variant that says what the stream carries (CODECS, RESOLUTION, FRAME-RATE): the Cast receiver otherwise assumes H.264 Main 3.0 and AAC-LC
        /// (Google's streaming-protocols page), which is wrong for a 1080p High stream.
        /// </summary>
        public static string BuildMaster(int bandwidthBitsPerSec, int width, int height, double fps, string codecs, string mediaUri)
        {
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=").Append(Math.Max(1, bandwidthBitsPerSec).ToString(CultureInfo.InvariantCulture));
            if (width > 0 && height > 0) sb.Append(",RESOLUTION=").Append(width.ToString(CultureInfo.InvariantCulture)).Append('x').Append(height.ToString(CultureInfo.InvariantCulture));
            if (fps > 0) sb.Append(",FRAME-RATE=").Append(fps.ToString("0.000", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(codecs)) sb.Append(",CODECS=\"").Append(codecs).Append('"');
            sb.Append('\n').Append(mediaUri).Append('\n');
            return sb.ToString();
        }

        /// <summary>RFC 6381 codec string of an H.264 stream from its SPS: avc1.PPCCLL.</summary>
        public static string AvcCodec(H264Sps sps)
        {
            return "avc1." + sps.ProfileIdc.ToString("x2", CultureInfo.InvariantCulture) + (sps.ConstraintFlags & 0xFF).ToString("x2", CultureInfo.InvariantCulture) + sps.LevelIdc.ToString("x2", CultureInfo.InvariantCulture);
        }

        /// <summary>mp4a.40.&lt;object type&gt; for AAC (the ADTS profile + 1; 2 = AAC-LC), mp4a.40.34 for MP3; null for anything else.</summary>
        public static string AudioCodec(StreamInfo info)
        {
            if (info == null || info.Audio == null) return null;
            if (info.Audio.Kind == EsKind.AacAdts) return "mp4a.40." + (info.Adts != null ? info.Adts.ObjectType : 2).ToString(CultureInfo.InvariantCulture);
            if (info.Audio.Kind == EsKind.MpegAudio && info.MpegAudio != null && info.MpegAudio.Layer == 3) return "mp4a.40.34";
            return null;
        }

        public static string FormatDate(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string Duration(double seconds)
        {
            return seconds.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>The playlist of a live window. 'listed' is in playback order and must not be empty.</summary>
        public static string Build(IList<HlsSegment> listed, long discontinuitySequence, int targetDuration, bool ended, bool programDate)
        {
            var sb = new StringBuilder(256 + listed.Count * 96);
            sb.Append("#EXTM3U\n");
            sb.Append("#EXT-X-VERSION:3\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append(targetDuration.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(listed[0].Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(discontinuitySequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var s in listed)
            {
                if (s.Discontinuity) sb.Append("#EXT-X-DISCONTINUITY\n");
                if (programDate) sb.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(FormatDate(s.ProgramDate)).Append('\n');
                sb.Append("#EXTINF:").Append(Duration(s.Duration)).Append(",\n");
                sb.Append(s.Name).Append('\n');
            }
            if (ended) sb.Append("#EXT-X-ENDLIST\n");
            return sb.ToString();
        }
    }

    /// <summary>Builds the PAT and PMT packets that open every segment.</summary>
    internal static class TsPsiWriter
    {
        private const int Pkt = 188;

        /// <summary>One PAT packet: program → PMT PID.</summary>
        public static void WritePat(byte[] dst, int off, HlsFormat f, int cc)
        {
            var sec = new byte[16];
            sec[0] = 0x00;
            sec[1] = 0xB0; sec[2] = 13;                                                      // section_syntax_indicator, length 13
            sec[3] = 0x00; sec[4] = 0x01;                                                    // transport_stream_id
            sec[5] = 0xC1;                                                                   // version 0, current
            sec[6] = 0; sec[7] = 0;                                                          // section numbers
            sec[8] = (byte)(f.ProgramNumber >> 8); sec[9] = (byte)f.ProgramNumber;
            sec[10] = (byte)(0xE0 | (f.PmtPid >> 8)); sec[11] = (byte)f.PmtPid;
            WriteSection(dst, off, 0, sec, 12, cc);
        }

        /// <summary>One PMT packet with only the video, the chosen audio and the PCR PID.</summary>
        public static void WritePmt(byte[] dst, int off, HlsFormat f, int cc)
        {
            var sec = new byte[184];
            int n = 0;
            sec[n++] = 0x02;
            sec[n++] = 0xB0; sec[n++] = 0;                                                   // length patched below
            sec[n++] = (byte)(f.ProgramNumber >> 8); sec[n++] = (byte)f.ProgramNumber;
            sec[n++] = 0xC1;
            sec[n++] = 0; sec[n++] = 0;
            sec[n++] = (byte)(0xE0 | (f.PcrPid >> 8)); sec[n++] = (byte)f.PcrPid;
            sec[n++] = 0xF0; sec[n++] = 0;                                                   // no program descriptors
            sec[n++] = (byte)f.VideoType;
            sec[n++] = (byte)(0xE0 | (f.VideoPid >> 8)); sec[n++] = (byte)f.VideoPid;
            sec[n++] = 0xF0; sec[n++] = 0;
            if (f.AudioPid >= 0)
            {
                var desc = f.AudioDescriptors ?? new byte[0];
                if (desc.Length > 60) desc = new byte[0];                                    // keep the PMT in one packet
                sec[n++] = (byte)f.AudioType;
                sec[n++] = (byte)(0xE0 | (f.AudioPid >> 8)); sec[n++] = (byte)f.AudioPid;
                sec[n++] = (byte)(0xF0 | (desc.Length >> 8)); sec[n++] = (byte)desc.Length;
                Buffer.BlockCopy(desc, 0, sec, n, desc.Length); n += desc.Length;
            }
            int sectionLength = n - 3 + 4;                                                   // after the length field, with the CRC
            sec[1] = (byte)(0xB0 | (sectionLength >> 8)); sec[2] = (byte)sectionLength;
            WriteSection(dst, off, f.PmtPid, sec, n, cc);
        }

        private static void WriteSection(byte[] dst, int off, int pid, byte[] sec, int len, int cc)
        {
            uint crc = TsCrc.Compute(sec, 0, len);
            for (int i = 0; i < Pkt; i++) dst[off + i] = 0xFF;
            dst[off] = 0x47;
            dst[off + 1] = (byte)(0x40 | (pid >> 8)); dst[off + 2] = (byte)pid;
            dst[off + 3] = (byte)(0x10 | (cc & 0xF));
            dst[off + 4] = 0;                                                                // pointer_field
            Buffer.BlockCopy(sec, 0, dst, off + 5, len);
            dst[off + 5 + len] = (byte)(crc >> 24); dst[off + 6 + len] = (byte)(crc >> 16); dst[off + 7 + len] = (byte)(crc >> 8); dst[off + 8 + len] = (byte)crc;
        }
    }
}
