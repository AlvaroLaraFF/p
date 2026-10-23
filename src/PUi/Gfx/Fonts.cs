using System;
using System.IO;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>Manrope 400/600/700/800 (the design also uses 500: it is served with 400, the closest available). Loaded from a directory given by the platform.</summary>
    public sealed class Fonts : IDisposable
    {
        private readonly SKTypeface[] _faces = new SKTypeface[4];   // 400, 600, 700, 800
        private static readonly int[] Weights = { 400, 600, 700, 800 };

        public Fonts(string dir)
        {
            for (int i = 0; i < Weights.Length; i++) _faces[i] = Load(dir, Weights[i]);
        }

        private static SKTypeface Load(string dir, int weight)
        {
            try
            {
                string p = Path.Combine(dir ?? "", "Manrope-" + weight + ".ttf");
                if (File.Exists(p)) { var tf = SKTypeface.FromFile(p); if (tf != null) return tf; }
            }
            catch (Exception) { }
            return SKTypeface.FromFamilyName(null);
        }

        public SKTypeface Get(int weight)
        {
            if (weight <= 500) return _faces[0];
            if (weight <= 600) return _faces[1];
            if (weight <= 700) return _faces[2];
            return _faces[3];
        }

        public void Dispose() { foreach (var f in _faces) if (f != null) f.Dispose(); }
    }
}
