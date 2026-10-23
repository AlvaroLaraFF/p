using System;
using System.Collections.Generic;
using AndroidX.Media3.Common;
using AndroidX.Media3.Common.Util;
using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.TS;
using AFormat = AndroidX.Media3.Common.Format;

namespace PAndroid
{
    /// <summary>
    /// Plays the MP2 audio of a transport stream on a device that has no MP2 decoder. ExoPlayer's TS extractor cuts the MP2 frames out of the stream; this layer sits between the extractor and
    /// the player and turns each frame into 16 bit PCM with <see cref="Mp2Decoder"/> before the player sees it, announcing the track as raw PCM, which every device plays (it is what a WAV file
    /// is). Nothing else changes: the video, the other audio formats (AAC, AC-3...), the timestamps and the seeking are the extractor's own. It only comes into play for a stream whose audio track
    /// turns out to be "audio/mpeg-L2": for any other format every call goes straight through.
    ///
    /// This is the same place the FFmpeg extension would sit if it were used (it decodes in the renderer instead), without a native library: the extension has to be built from source with the
    /// NDK and ships no MP2 build, and a library of that size is a lot to carry for one audio format.
    ///
    /// Every method here is called by ExoPlayer's loading thread, from Java: an exception of ours must never go back into it (the app would die), so decoding failures are swallowed and logged
    /// and the frame becomes silence; Java exceptions of the extractor underneath (end of input, malformed data) are left to go through, as they would without this layer.
    /// </summary>
    internal sealed class Mp2ExtractorsFactory : Java.Lang.Object, IExtractorsFactory
    {
        private readonly IExtractorsFactory _inner;
        private readonly Action<string> _log;

        public Mp2ExtractorsFactory(IExtractorsFactory inner, Action<string> log) { _inner = inner; _log = log; }

        public IExtractor[] CreateExtractors() { return Wrap(_inner.CreateExtractors()); }

        public IExtractor[] CreateExtractors(Android.Net.Uri uri, IDictionary<string, IList<string>> responseHeaders) { return Wrap(_inner.CreateExtractors(uri, responseHeaders)); }

        // The interface's other members have default implementations in Java, but the Java peer of this class overrides every method of the interface: they are given here, passing the setting on
        public IExtractorsFactory ExperimentalSetCodecsToParseWithinGopSampleDependencies(int codecs) { _inner.ExperimentalSetCodecsToParseWithinGopSampleDependencies(codecs); return this; }
        public IExtractorsFactory ExperimentalSetTextTrackTranscodingEnabled(bool enabled) { _inner.ExperimentalSetTextTrackTranscodingEnabled(enabled); return this; }
        public IExtractorsFactory SetParseHagcMetadata(bool parse) { _inner.SetParseHagcMetadata(parse); return this; }
        public IExtractorsFactory SetSubtitleParserFactory(AndroidX.Media3.Extractor.Text.ISubtitleParserFactory factory) { _inner.SetSubtitleParserFactory(factory); return this; }

        private IExtractor[] Wrap(IExtractor[] extractors)
        {
            var wrapped = new IExtractor[extractors.Length];
            for (int i = 0; i < extractors.Length; i++)
                wrapped[i] = IsTransportStream(extractors[i]) ? (IExtractor)new Mp2PcmExtractor(extractors[i], _log) : extractors[i];
            return wrapped;
        }

        /// <summary>The MPEG transport stream extractor (the only one whose audio can be MP2 here), by its type or, if the runtime handed back a generic wrapper, by the name of its Java class.</summary>
        private static bool IsTransportStream(IExtractor e)
        {
            if (e is TsExtractor) return true;
            try { var jo = e as Java.Lang.Object; return jo != null && jo.Class != null && jo.Class.Name != null && jo.Class.Name.EndsWith(".TsExtractor", StringComparison.Ordinal); }
            catch (Exception) { return false; }
        }
    }

    internal sealed class Mp2PcmExtractor : Java.Lang.Object, IExtractor
    {
        private readonly IExtractor _inner;
        private readonly Action<string> _log;
        private Mp2PcmOutput _output;

        public Mp2PcmExtractor(IExtractor inner, Action<string> log) { _inner = inner; _log = log; }

        public bool Sniff(IExtractorInput input) { return _inner.Sniff(input); }

        public void Init(IExtractorOutput output)
        {
            _output = new Mp2PcmOutput(output, _log);
            _inner.Init(_output);
        }

        public int Read(IExtractorInput input, PositionHolder seekPosition) { return _inner.Read(input, seekPosition); }

        public void Seek(long position, long timeUs)
        {
            var o = _output;
            if (o != null) o.Reset();             // the filter history belongs to the old position
            _inner.Seek(position, timeUs);
        }

        public void Release() { _inner.Release(); }

        // defaults of the interface, given explicitly (see Mp2ExtractorsFactory): what the extractor underneath says, since this layer adds nothing to either
        public IList<ISniffFailure> SniffFailureDetails { get { return _inner.SniffFailureDetails; } }
        public IExtractor UnderlyingImplementation { get { return _inner.UnderlyingImplementation; } }
    }

    internal sealed class Mp2PcmOutput : Java.Lang.Object, IExtractorOutput
    {
        private readonly IExtractorOutput _inner;
        private readonly Action<string> _log;
        private readonly List<Mp2PcmTrack> _tracks = new List<Mp2PcmTrack>();

        public Mp2PcmOutput(IExtractorOutput inner, Action<string> log) { _inner = inner; _log = log; }

        public ITrackOutput Track(int id, int type)
        {
            var t = _inner.Track(id, type);
            if (type != C.TrackTypeAudio) return t;
            var w = new Mp2PcmTrack(t, _log);
            lock (_tracks) _tracks.Add(w);
            return w;
        }

        public void EndTracks() { _inner.EndTracks(); }

        public void SeekMap(ISeekMap seekMap) { _inner.SeekMap(seekMap); }

        public void Reset()
        {
            lock (_tracks) foreach (var t in _tracks) t.Reset();
        }
    }

    internal sealed class Mp2PcmTrack : Java.Lang.Object, ITrackOutput
    {
        private const int MaxSilencedToLog = 5;
        private readonly ITrackOutput _inner;
        private readonly Action<string> _log;
        private readonly byte[] _pcmBytes = new byte[Mp2Transcoder.MaxPcmBytes];
        private readonly ParsableByteArray _out = new ParsableByteArray();
        private Mp2Transcoder _transcoder;                  // null while the track is not MP2: everything goes straight through
        private long _silencedLogged;

        public Mp2PcmTrack(ITrackOutput inner, Action<string> log) { _inner = inner; _log = log; }

        public void Reset() { var t = _transcoder; if (t != null) t.Reset(); }

        public void Format(AFormat format)
        {
            if (format != null && string.Equals(format.SampleMimeType, MimeTypes.AudioMpegL2, StringComparison.OrdinalIgnoreCase))
            {
                int rate = format.SampleRate > 0 ? format.SampleRate : 48000, channels = format.ChannelCount > 0 ? format.ChannelCount : 2;
                _transcoder = new Mp2Transcoder(rate, channels);
                _log("MP2 audio (" + rate + " Hz, " + channels + " ch) is decoded by the app: this device has no MP2 decoder, or the software decoder was asked for");
                _inner.Format(PcmFormat(format, rate, channels));
            }
            else
            {
                _transcoder = null;
                _inner.Format(format);
            }
        }

        private static AFormat PcmFormat(AFormat mp2, int rate, int channels)
        {
            return mp2.BuildUpon()
                .SetSampleMimeType(MimeTypes.AudioRaw)
                .SetCodecs((string)null)
                .SetPcmEncoding(C.EncodingPcm16bit)
                .SetSampleRate(rate)
                .SetChannelCount(channels)
                .SetAverageBitrate(rate * channels * 16)
                .SetPeakBitrate(-1)
                .SetMaxInputSize(-1)
                .Build();
        }

        // defaults of the interface, given explicitly (see Mp2ExtractorsFactory)
        public void DurationUs(long durationUs) { _inner.DurationUs(durationUs); }
        public int SampleData(IDataReader input, int length, bool allowEndOfInput) { return SampleData(input, length, allowEndOfInput, 0); }
        public void SampleData(ParsableByteArray data, int length) { SampleData(data, length, 0); }

        public int SampleData(IDataReader input, int length, bool allowEndOfInput, int sampleDataPart)
        {
            var t = _transcoder;
            if (t == null) return _inner.SampleData(input, length, allowEndOfInput, sampleDataPart);
            int pos;
            var room = t.Room(length, out pos);
            int n = input.Read(room, pos, length);
            if (n > 0) t.Added(n);
            else if (n < 0 && !allowEndOfInput) throw new Java.IO.EOFException();
            return n;
        }

        public void SampleData(ParsableByteArray data, int length, int sampleDataPart)
        {
            var t = _transcoder;
            if (t == null) { _inner.SampleData(data, length, sampleDataPart); return; }
            int pos;
            var room = t.Room(length, out pos);
            data.ReadBytes(room, pos, length);
            t.Added(length);
        }

        public void SampleMetadata(long timeUs, int flags, int size, int offset, TrackOutputCryptoData cryptoData)
        {
            var t = _transcoder;
            if (t == null) { _inner.SampleMetadata(timeUs, flags, size, offset, cryptoData); return; }
            try
            {
                long silencedBefore = t.FramesSilenced;
                bool formatChanged;
                int bytes = t.Complete(size, offset, _pcmBytes, out formatChanged);
                if (t.FramesSilenced != silencedBefore && ++_silencedLogged <= MaxSilencedToLog) _log("MP2 software decoder: a frame did not decode (" + size + " bytes): silence in its place");
                if (formatChanged)
                {
                    // the stream changed its format (it does not normally): the player is told before the samples of the new one
                    _log("MP2 stream changed to " + t.SampleRate + " Hz, " + t.Channels + " ch");
                    _inner.Format(new AFormat.Builder().SetSampleMimeType(MimeTypes.AudioRaw).SetPcmEncoding(C.EncodingPcm16bit).SetSampleRate(t.SampleRate).SetChannelCount(t.Channels).Build());
                }
                _out.Reset(_pcmBytes, bytes);
                _inner.SampleData(_out, bytes, 0);
                _inner.SampleMetadata(timeUs, flags, bytes, 0, null);
            }
            catch (Java.Lang.Throwable) { throw; }
            catch (Exception ex) { _log("MP2 software decoder: " + ex.GetType().Name + " " + ex.Message); }
        }
    }
}
