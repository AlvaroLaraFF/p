using System;
using System.Collections.Generic;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Texture (sprite) cache: what does not change per frame (cards, shadows, halos, blurs) is drawn once on a GPU surface
    /// (or a CPU one on the desktop) and afterwards only stamped. Bounded by number of entries and by bytes; eviction only happens at the end of a
    /// frame and never touches what the current one used (so an image in use is not freed).
    /// </summary>
    public sealed class SpriteCache : IDisposable
    {
        private sealed class Entry { public SKImage Image; public long Bytes; public long LastUsed; public string Key; }
        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>();
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private long _frame, _bytes;
        private GRContext _ctx;
        public int Created { get; private set; }
        /// <summary>
        /// Sprite creation budget per frame, in ms (0 = no limit, the default). On the TV creating a sprite on the GPU costs ~12 ms and a new screen
        /// asks for 20: all in one frame is 200-450 ms with the screen frozen. With a budget, TryGet leaves for the next frame what does not fit (at least
        /// one is always created per frame, so progress is made) and the card is seen first with its body and then with its content.
        /// </summary>
        public double FrameBudgetMs;
        private double _spentMs;
        private int _createdFrame;
        /// <summary>true if this frame already created some sprite and spent its budget (the pre-warming must not create more).</summary>
        public bool OverBudget { get { return FrameBudgetMs > 0 && _createdFrame > 0 && _spentMs >= FrameBudgetMs; } }
        /// <summary>Sprites that did not fit in this frame's budget (another frame has to be requested).</summary>
        public int Deferred { get; private set; }
        private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public SpriteCache(int maxEntries = 400, long maxBytes = 48L << 20) { _maxEntries = maxEntries; _maxBytes = maxBytes; }

        public int Count { get { return _map.Count; } }
        public long Bytes { get { return _bytes; } }

        /// <summary>Marks the start of a frame and, if the GPU context changed, drops everything (the previous context's textures are no longer valid).</summary>
        public void BeginFrame(GRContext ctx)
        {
            _frame++;
            _spentMs = 0; _createdFrame = 0; Deferred = 0;
            if (!ReferenceEquals(ctx, _ctx)) { Clear(); _ctx = ctx; }
        }

        public void EndFrame()
        {
            if (_map.Count <= _maxEntries && _bytes <= _maxBytes) return;
            var list = new List<Entry>(_map.Values);
            list.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
            foreach (var e in list)
            {
                if (_map.Count <= _maxEntries && _bytes <= _maxBytes) break;
                if (e.LastUsed == _frame) break;
                Remove(e);
            }
        }

        private void Remove(Entry e) { _map.Remove(e.Key); _bytes -= e.Bytes; if (e.Image != null) e.Image.Dispose(); }

        public void Clear()
        {
            foreach (var e in _map.Values) if (e.Image != null) e.Image.Dispose();
            _map.Clear(); _bytes = 0;
        }

        public void Remove(string key) { Entry e; if (_map.TryGetValue(key, out e)) Remove(e); }

        /// <summary>Removes all entries whose key starts with the prefix (e.g. when a row's data changes).</summary>
        public void RemovePrefix(string prefix)
        {
            var del = new List<Entry>();
            foreach (var e in _map.Values) if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) del.Add(e);
            foreach (var e in del) Remove(e);
        }

        /// <summary>
        /// Image for 'bounds' (logical coordinates of 1920x1080) at scale k (pixels per logical unit). 'draw' paints in logical coordinates.
        /// The key must include everything that changes the drawing.
        /// </summary>
        public SKImage Get(string key, SKRect bounds, float k, Action<SKCanvas> draw)
        {
            Entry e;
            if (_map.TryGetValue(key, out e)) { e.LastUsed = _frame; return e.Image; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return Create(key, bounds, k, draw); }
            finally { _spentMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * TickMs; _createdFrame++; }
        }

        /// <summary>Like Get, but returns null (and counts a deferral) if the frame already spent its creation budget; the caller draws without the content and requests another frame.</summary>
        public SKImage TryGet(string key, SKRect bounds, float k, Action<SKCanvas> draw)
        {
            Entry e;
            if (_map.TryGetValue(key, out e)) { e.LastUsed = _frame; return e.Image; }
            if (FrameBudgetMs > 0 && _createdFrame > 0 && _spentMs >= FrameBudgetMs) { Deferred++; return null; }
            return Get(key, bounds, k, draw);
        }

        private SKImage Create(string key, SKRect bounds, float k, Action<SKCanvas> draw)
        {
            Entry e;
            int pw = Math.Max(1, (int)Math.Ceiling(bounds.Width * k)), ph = Math.Max(1, (int)Math.Ceiling(bounds.Height * k));
            var img = Render(pw, ph, c =>
            {
                c.Scale(pw / bounds.Width, ph / bounds.Height);
                c.Translate(-bounds.Left, -bounds.Top);
                draw(c);
            });
            Created++;
            e = new Entry { Image = img, Bytes = (long)pw * ph * 4, LastUsed = _frame, Key = key };
            _map[key] = e; _bytes += e.Bytes;
            return img;
        }

        /// <summary>Image in pixels (no transform) of pw x ph.</summary>
        public SKImage GetPixels(string key, int pw, int ph, Action<SKCanvas> draw)
        {
            Entry e;
            if (_map.TryGetValue(key, out e)) { e.LastUsed = _frame; return e.Image; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var img = Render(pw, ph, draw);
            _spentMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * TickMs; _createdFrame++;
            Created++;
            e = new Entry { Image = img, Bytes = (long)pw * ph * 4, LastUsed = _frame, Key = key };
            _map[key] = e; _bytes += e.Bytes;
            return img;
        }

        /// <summary>Draws on a new surface and returns the image (GPU if there is a context).</summary>
        public SKImage Render(int pw, int ph, Action<SKCanvas> draw)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            using (var s = NewSurface(_ctx, pw, ph))
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                s.Canvas.Clear(SKColors.Transparent);
                draw(s.Canvas);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                s.Canvas.Flush();
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                var img = s.Snapshot();
                long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                SurfaceMs += (t1 - t0) * TickMs; DrawMs += (t2 - t1) * TickMs; FlushMs += (t3 - t2) * TickMs; SnapMs += (t4 - t3) * TickMs; Renders++;
                return img;
            }
        }

        /// <summary>Accumulated time creating sprites, by phase (creating the surface, drawing, flushing to the GPU, snapshot): to know where the time goes.</summary>
        public double SurfaceMs, DrawMs, FlushMs, SnapMs;
        public int Renders;
        public string PhaseSummary() { return Renders + " sprites: surface " + (int)SurfaceMs + " ms, draw " + (int)DrawMs + " ms, flush " + (int)FlushMs + " ms, snapshot " + (int)SnapMs + " ms"; }

        public static SKSurface NewSurface(GRContext ctx, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
            return ctx != null ? SKSurface.Create(ctx, true, info) : SKSurface.Create(info);
        }

        public void Dispose() { Clear(); }
    }
}
