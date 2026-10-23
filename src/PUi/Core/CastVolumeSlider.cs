using System;
using PUi.Platform;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>
    /// The finger on the volume slider of "Enviando a ..." (the sheet and the player both have one): while it is down the knob follows it and the value is sent to the device, but not more often
    /// than every <see cref="MinIntervalSecs"/> (a television answers a volume change with a network call), and always once more when the finger is lifted. The device's own value takes over
    /// again when the finger is up.
    /// </summary>
    public sealed class CastVolumeSlider
    {
        public const double MinIntervalSecs = 0.15;

        private SKRect _track;
        private int _sent = -1;
        private double _lastSentAt = -1;

        /// <summary>A finger is dragging the knob.</summary>
        public bool Dragging { get; private set; }
        /// <summary>The value under the finger, 0 to 100 (meaningful while <see cref="Dragging"/>).</summary>
        public int Value { get; private set; }

        /// <summary>The value to draw: the finger's while it is down, the device's otherwise (-1 = unknown, drawn as 0).</summary>
        public int Shown(int deviceVolume) { return Dragging ? Value : Math.Max(0, deviceVolume); }

        /// <summary>The finger came down on the track (the box where the knob can travel).</summary>
        public void Begin(SKRect track, float x, double now, Action<int> send)
        {
            _track = track; Dragging = true; _sent = -1; _lastSentAt = -1;
            Move(x, now, send);
        }

        public void Move(float x, double now, Action<int> send)
        {
            if (!Dragging) return;
            Value = FromX(x);
            if (Value != _sent && (_lastSentAt < 0 || now - _lastSentAt >= MinIntervalSecs)) { _sent = Value; _lastSentAt = now; send(Value); }
        }

        /// <summary>The finger was lifted: the last value goes out if it has not.</summary>
        public void End(float x, double now, Action<int> send, bool cancel)
        {
            if (!Dragging) return;
            if (!cancel) Value = FromX(x);
            Dragging = false;
            if (!cancel && Value != _sent) { _sent = Value; send(Value); }
        }

        public void Abort() { Dragging = false; }

        private int FromX(float x)
        {
            float w = _track.Width;
            if (w <= 0) return 0;
            return (int)Math.Round(Math.Max(0f, Math.Min(1f, (x - _track.Left) / w)) * 100f);
        }

        /// <summary>The value (0 to 100) at a position of a track: for the tests and for drawing the knob.</summary>
        public static int ValueAt(SKRect track, float x)
        {
            return track.Width <= 0 ? 0 : (int)Math.Round(Math.Max(0f, Math.Min(1f, (x - track.Left) / track.Width)) * 100f);
        }
    }
}
