using System;
using System.Collections.Generic;

namespace AceEngine.Cast
{
    /// <summary>What the first seconds of a transport stream say about its program: streams from the PMT, H.264 SPS and the first audio frame header.</summary>
    public sealed class StreamInfo
    {
        /// <summary>The program as the last PMT describes it; null until a valid PMT has been seen.</summary>
        public ProgramInfo Program;
        public EsInfo Video;
        /// <summary>Parsed SPS of the H.264 video (null if not H.264 or not seen yet).</summary>
        public H264Sps Sps;
        /// <summary>The audio the HLS stream would carry (see CastCompatibility.ChooseAudio); null if the program has none.</summary>
        public EsInfo Audio;
        public readonly List<EsInfo> AudioTracks = new List<EsInfo>();
        /// <summary>Header of the first AAC frame (ADTS) of the chosen audio, if it is ADTS AAC and one was seen.</summary>
        public AdtsHeader Adts;
        /// <summary>Header of the first MPEG audio frame (layer II or III) of the chosen audio.</summary>
        public MpegAudioHeader MpegAudio;
        /// <summary>The chosen audio is declared as ADTS AAC but the payload is LATM (or the other way round).</summary>
        public bool AudioPayloadIsLatm, AudioPayloadIsAdts;
        /// <summary>The video or the audio is scrambled (transport_scrambling_control).</summary>
        public bool Scrambled;
        /// <summary>The program carries a PCR on its PCR PID.</summary>
        public bool HasPcr;
        public int ProgramCount;
        public long Packets;

        /// <summary>true when everything the verdict needs has been seen (an absent kind of detail that cannot exist counts as seen).</summary>
        public bool IsComplete;
    }

    /// <summary>
    /// Incremental analysis of a transport stream: reads the PAT/PMT, then the start of the first video PES (to find the SPS) and of the first PES of each
    /// audio stream (to read the frame header). Feed it packet-aligned data in any amount; <see cref="Snapshot"/> says what is known so far.
    /// </summary>
    internal sealed class TsStreamProbe
    {
        private const int Pkt = 188;
        private const int CollectBytes = 1536;                       // enough for AUD + SEI + SPS + PPS of any realistic encoder
        private readonly TsProgramParser _prog = new TsProgramParser();
        private string _signature = "";
        private long _packets;
        private bool _scrambled, _hasPcr;

        // video collector
        private int _videoPid = -1;
        private EsKind _videoKind;
        private readonly byte[] _vbuf = new byte[CollectBytes];
        private int _vlen;
        private bool _vcollect;
        private H264Sps _sps;

        // audio collectors (one per audio PID)
        private sealed class AudioState
        {
            public EsInfo Es;
            public byte[] Buf = new byte[CollectBytes];
            public int Len;
            public bool Collecting, Done;
            public AdtsHeader Adts;
            public MpegAudioHeader Mpeg;
            public bool LooksLatm, LooksAdts;
        }
        private readonly Dictionary<int, AudioState> _audio = new Dictionary<int, AudioState>();

        public long Packets { get { return _packets; } }

        /// <summary>Feeds whole TS packets. Data that does not start at a packet boundary is skipped up to the next sync.</summary>
        public void Feed(byte[] d, int off, int len)
        {
            int end = Math.Min(d.Length, off + len);
            int p = off;
            while (p + Pkt <= end)
            {
                if (!SyncOk(d, p, end))
                {
                    p = NextSync(d, p + 1, end);                                             // garbage in front or sync lost in the middle
                    if (p < 0) return;
                }
                Packet(d, p);
                p += Pkt;
            }
        }

        private static bool SyncOk(byte[] d, int p, int end)
        {
            return d[p] == 0x47 && (p + Pkt >= end || d[p + Pkt] == 0x47) && (p + 2 * Pkt >= end || d[p + 2 * Pkt] == 0x47);
        }

        private static int NextSync(byte[] d, int from, int end)
        {
            for (int q = from; q + Pkt <= end; q++) if (SyncOk(d, q, end)) return q;
            return -1;
        }

        /// <summary>A cut in the stream: half-received tables and PES starts are dropped.</summary>
        public void Discontinuity()
        {
            _prog.Abort();
            _vcollect = false; _vlen = 0;
            foreach (var a in _audio.Values) { a.Collecting = false; a.Len = 0; }
        }

        private void Packet(byte[] d, int p)
        {
            _packets++;
            if ((d[p + 1] & 0x80) != 0) return;
            int pid = ((d[p + 1] & 0x1F) << 8) | d[p + 2];
            if (pid == 0x1FFF) return;
            if (_prog.Wants(pid))
            {
                _prog.FeedPacket(d, p);
                var prog = _prog.Program;
                if (prog != null && prog.Signature != _signature) OnProgram(prog);
                return;
            }
            if (_prog.Program == null) return;
            int scr = (d[p + 3] >> 6) & 3;
            bool pusi = (d[p + 1] & 0x40) != 0;
            int afc = (d[p + 3] >> 4) & 3;
            int ps = p + 4;
            if ((afc & 2) != 0)
            {
                int afLen = d[p + 4];
                if (afLen > 183) return;
                if (pid == _prog.Program.PcrPid && afLen >= 7 && (d[p + 5] & 0x10) != 0) _hasPcr = true;
                ps = p + 5 + afLen;
            }
            bool hasPayload = (afc & 1) != 0 && ps < p + Pkt;
            if (pid == _videoPid)
            {
                if (scr != 0) _scrambled = true;
                if (hasPayload && _videoKind == EsKind.H264 && _sps == null) CollectVideo(d, ps, p + Pkt, pusi);
                return;
            }
            AudioState a;
            if (_audio.TryGetValue(pid, out a))
            {
                if (scr != 0 && IsChosen(a)) _scrambled = true;
                if (hasPayload && !a.Done) CollectAudio(a, d, ps, p + Pkt, pusi);
            }
        }

        private bool IsChosen(AudioState a)
        {
            var prog = _prog.Program;
            var best = CastCompatibility.ChooseAudio(prog);
            return best != null && best.Pid == a.Es.Pid;
        }

        private void OnProgram(ProgramInfo prog)
        {
            // A change of content restarts what depends on the streams (a plain version bump does not)
            _signature = prog.Signature;
            var v = prog.Video;
            int newVideo = v == null ? -1 : v.Pid;
            if (newVideo != _videoPid || (v != null && v.Kind != _videoKind))
            {
                _videoPid = newVideo; _videoKind = v == null ? EsKind.Unknown : v.Kind;
                _vcollect = false; _vlen = 0; _sps = null;
            }
            var keep = new HashSet<int>();
            foreach (var es in prog.AudioStreams)
            {
                keep.Add(es.Pid);
                AudioState a;
                if (!_audio.TryGetValue(es.Pid, out a) || a.Es.Kind != es.Kind) _audio[es.Pid] = new AudioState { Es = es };
                else a.Es = es;
            }
            var gone = new List<int>();
            foreach (var k in _audio.Keys) if (!keep.Contains(k)) gone.Add(k);
            foreach (var k in gone) _audio.Remove(k);
        }

        // ---------- video ----------

        private void CollectVideo(byte[] d, int ps, int end, bool pusi)
        {
            if (pusi)
            {
                if (_vcollect) TryFindSps();                                                 // the previous start gave nothing: try with what was collected
                _vlen = 0; _vcollect = true;
            }
            if (!_vcollect) return;
            int n = Math.Min(end - ps, _vbuf.Length - _vlen);
            if (n > 0) { Buffer.BlockCopy(d, ps, _vbuf, _vlen, n); _vlen += n; }
            // look for the SPS as soon as there is room for a complete one
            if (_vlen >= _vbuf.Length || _vlen >= 600) { TryFindSps(); if (_sps != null || _vlen >= _vbuf.Length) _vcollect = false; }
        }

        private void TryFindSps()
        {
            for (int k = 0; k + 4 < _vlen; k++)
            {
                if (_vbuf[k] != 0 || _vbuf[k + 1] != 0 || _vbuf[k + 2] != 1) continue;
                if ((_vbuf[k + 3] & 0x1F) != 7) { k += 2; continue; }
                int s = k + 3;
                int e = s + 1;
                while (e + 2 < _vlen && !(_vbuf[e] == 0 && _vbuf[e + 1] == 0 && (_vbuf[e + 2] == 1 || _vbuf[e + 2] == 0))) e++;
                if (e + 2 >= _vlen) e = _vlen;                                               // up to the end of what was collected
                H264Sps sps;
                if (H264Sps.TryParse(_vbuf, s, e - s, out sps)) { _sps = sps; return; }
                k = e - 1;
            }
            _vcollect = false;
        }

        // ---------- audio ----------

        private static int PesPayloadStart(byte[] b, int len)
        {
            if (len < 9 || b[0] != 0 || b[1] != 0 || b[2] != 1) return -1;
            int start = 9 + b[8];
            return start <= len ? start : -1;
        }

        private void CollectAudio(AudioState a, byte[] d, int ps, int end, bool pusi)
        {
            if (pusi) { a.Len = 0; a.Collecting = true; }
            if (!a.Collecting) return;
            int n = Math.Min(end - ps, a.Buf.Length - a.Len);
            if (n > 0) { Buffer.BlockCopy(d, ps, a.Buf, a.Len, n); a.Len += n; }
            int start = PesPayloadStart(a.Buf, a.Len);
            if (start < 0 && a.Len >= 9 + 255) { a.Collecting = false; return; }
            if (start < 0) return;
            int avail = a.Len - start;
            if (avail < 16 && a.Len < a.Buf.Length) return;                                  // wait for a few bytes of payload
            // The frame header normally opens the payload; some muxers put a few stuffing bytes first
            for (int k = start; k + 7 <= a.Len && k < start + 64; k++)
            {
                switch (a.Es.Kind)
                {
                    case EsKind.AacAdts:
                    case EsKind.AacLatm:
                        {
                            AdtsHeader h;
                            if (AdtsHeader.TryParse(a.Buf, k, a.Len, out h) && NextFrameOk(a.Buf, k, h.FrameLength, a.Len, 0xFF, 0xF6, 0xF0)) { a.Adts = h; a.LooksAdts = true; a.Done = true; return; }
                            if (a.Buf[k] == 0x56 && (a.Buf[k + 1] & 0xE0) == 0xE0) { a.LooksLatm = true; a.Done = true; return; }
                            break;
                        }
                    case EsKind.MpegAudio:
                        {
                            MpegAudioHeader h;
                            if (MpegAudioHeader.TryParse(a.Buf, k, a.Len, out h)) { a.Mpeg = h; a.Done = true; return; }
                            break;
                        }
                    default:
                        a.Done = true; return;                                               // AC-3 and the rest are decided by the PMT alone
                }
            }
            if (a.Len >= a.Buf.Length) a.Collecting = false;                                 // no luck in this PES: try the next one
        }

        private static bool NextFrameOk(byte[] b, int k, int frameLen, int len, byte sync0, byte mask1, byte value1)
        {
            int nx = k + frameLen;
            if (nx + 2 > len) return true;                                                   // the next frame is not in what we collected: accept
            return b[nx] == sync0 && (b[nx + 1] & mask1) == value1;
        }

        // ---------- result ----------

        public StreamInfo Snapshot()
        {
            var info = new StreamInfo { Packets = _packets, Scrambled = _scrambled, HasPcr = _hasPcr, ProgramCount = _prog.ProgramCount };
            var prog = _prog.Program;
            info.Program = prog;
            if (prog == null) return info;
            info.Video = prog.Video;
            info.Sps = _sps;
            foreach (var es in prog.AudioStreams) info.AudioTracks.Add(es);
            info.Audio = CastCompatibility.ChooseAudio(prog);
            bool videoDone = info.Video == null || info.Video.Kind != EsKind.H264 || _sps != null;
            bool audioDone = true;
            if (info.Audio != null)
            {
                AudioState a;
                if (_audio.TryGetValue(info.Audio.Pid, out a))
                {
                    info.Adts = a.Adts; info.MpegAudio = a.Mpeg;
                    if (info.Audio.Kind == EsKind.AacAdts) info.AudioPayloadIsLatm = a.LooksLatm;
                    if (info.Audio.Kind == EsKind.AacLatm) info.AudioPayloadIsAdts = a.LooksAdts;
                    if ((info.Audio.Kind == EsKind.AacAdts || info.Audio.Kind == EsKind.AacLatm || info.Audio.Kind == EsKind.MpegAudio) && !a.Done) audioDone = false;
                }
            }
            info.IsComplete = videoDone && audioDone && (prog.PcrPid == 0x1FFF || _hasPcr);
            return info;
        }
    }
}
