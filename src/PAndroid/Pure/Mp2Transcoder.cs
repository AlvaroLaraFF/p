using System;

namespace PAndroid
{
    /// <summary>
    /// The part of the MP2 to PCM layer that has no Android in it (see Mp2ExtractorsFactory): the bytes of a frame come in pieces (ExoPlayer's MPEG audio reader appends the four bytes of the
    /// header and then the rest, and says "that was one sample" when the frame is complete), and what leaves is the PCM of that frame, as little-endian 16 bit samples. A frame that does not
    /// decode becomes a frame of silence of the same length, so the audio timeline keeps its place and the picture stays in step.
    /// </summary>
    public sealed class Mp2Transcoder
    {
        private readonly Mp2Decoder _decoder = new Mp2Decoder();
        private readonly short[] _pcm = new short[Mp2Decoder.SamplesPerFrame * 2];
        private byte[] _buf = new byte[Mp2Decoder.MaxFrameBytes * 2];
        private int _count;

        /// <param name="sampleRate">The track's, from the format the extractor announced.</param>
        /// <param name="channels">The track's.</param>
        public Mp2Transcoder(int sampleRate, int channels)
        {
            SampleRate = sampleRate; Channels = channels;
        }

        /// <summary>The format announced to the player: it changes only when a frame says otherwise (see <see cref="Complete"/>).</summary>
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public long FramesDecoded { get; private set; }
        public long FramesSilenced { get; private set; }
        /// <summary>Bytes appended and not yet turned into a sample.</summary>
        public int Pending { get { return _count; } }

        /// <summary>The most bytes of PCM a frame can make: 1152 samples of two channels.</summary>
        public const int MaxPcmBytes = Mp2Decoder.SamplesPerFrame * 2 * 2;

        public void Reset() { _count = 0; _decoder.Reset(); }

        /// <summary>Room for <paramref name="length"/> more bytes at the end: the array and the position to write them at (then call <see cref="Added"/>).</summary>
        public byte[] Room(int length, out int position)
        {
            if (_count + length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _count + length));
            position = _count;
            return _buf;
        }

        public void Added(int bytes) { if (bytes > 0) _count += bytes; }

        public void Append(byte[] data, int offset, int count)
        {
            int pos;
            var buf = Room(count, out pos);
            Buffer.BlockCopy(data, offset, buf, pos, count);
            _count += count;
        }

        /// <summary>
        /// The sample is complete: <paramref name="size"/> bytes that end <paramref name="offset"/> bytes before the end of what was appended (the offset is how many bytes of the next sample
        /// have already arrived). Writes its PCM to <paramref name="pcmBytes"/> (at least <see cref="MaxPcmBytes"/> long) and returns the number of bytes. <paramref name="formatChanged"/> is
        /// true when the frame's sampling rate or channels are not the ones announced before: the player has to be told before the samples of the new format.
        /// </summary>
        public int Complete(int size, int offset, byte[] pcmBytes, out bool formatChanged)
        {
            formatChanged = false;
            int start = _count - offset - size, channels = Channels, rate = SampleRate;
            bool ok = start >= 0 && size > 0 && _decoder.Decode(_buf, start, size, _pcm, out channels, out rate);
            // what followed this sample is the beginning of the next one
            if (offset > 0 && _count - offset >= 0) Buffer.BlockCopy(_buf, _count - offset, _buf, 0, offset);
            _count = Math.Max(0, offset);
            if (ok) FramesDecoded++;
            else
            {
                FramesSilenced++;
                Array.Clear(_pcm, 0, _pcm.Length);
                channels = Channels; rate = SampleRate;
            }
            if (channels != Channels || rate != SampleRate) { Channels = channels; SampleRate = rate; formatChanged = true; }
            int bytes = Mp2Decoder.SamplesPerFrame * Channels * 2;
            Buffer.BlockCopy(_pcm, 0, pcmBytes, 0, bytes);
            return bytes;
        }
    }
}
