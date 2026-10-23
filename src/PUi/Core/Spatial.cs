using System;
using System.Collections.Generic;
using PUi.Input;
using PUi.Motion;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>An element that can hold the remote's focus: its box (logical content coordinates) and its focus animations.</summary>
    public class Focusable
    {
        public string Id = "";
        public SKRect R;
        /// <summary>Scale when focused (1.06 recents, 1.05 categories, 1.015 rows...).</summary>
        public float FocusScale = 1.05f;
        public readonly Anim Focus = new Anim(0, Bezier.Pop);     // 0..1 in 0.24 s
        public readonly Anim Ring = new Anim(0, Bezier.Ease);     // 0..1 in 0.35 s
        public object Tag;
        /// <summary>Navigation group (e.g. "rail", "recents", "grid"): for exit rules between zones.</summary>
        public string Group = "";
        public bool Enabled = true;
        public float CenterX { get { return (R.Left + R.Right) / 2; } }
        public float CenterY { get { return (R.Top + R.Bottom) / 2; } }

        public void SetFocused(bool on, double now, bool instant = false)
        {
            Focus.Set(on ? 1 : 0, now, instant ? 0 : Tokens.FocusSecs);
            Ring.Set(on ? 1 : 0, now, instant ? 0 : Tokens.RingFadeSecs);
        }

        public bool Animating(double now) { return Focus.Active(now) || Ring.Active(now); }
    }

    /// <summary>
    /// Spatial navigation by remote: from the focused box it moves to the nearest one in the requested direction. Lateral deviation weighs more than distance,
    /// and it prefers those that overlap on the perpendicular axis (in a grid, the same row or the same column). Returns null if there is nothing in that direction.
    /// </summary>
    public static class Spatial
    {
        public static Focusable Find(IList<Focusable> items, Focusable cur, UiKey dir)
        {
            if (cur == null) return null;
            Focusable best = null; float bestScore = float.MaxValue;
            const float eps = 2f;
            foreach (var c in items)
            {
                if (c == cur || !c.Enabled) continue;
                float gap, lateral;
                bool overlap;
                switch (dir)
                {
                    case UiKey.Right: if (c.R.Left < cur.R.Right - eps) continue; gap = c.R.Left - cur.R.Right; lateral = Math.Abs(c.CenterY - cur.CenterY); overlap = c.R.Top < cur.R.Bottom - 1 && c.R.Bottom > cur.R.Top + 1; break;
                    case UiKey.Left: if (c.R.Right > cur.R.Left + eps) continue; gap = cur.R.Left - c.R.Right; lateral = Math.Abs(c.CenterY - cur.CenterY); overlap = c.R.Top < cur.R.Bottom - 1 && c.R.Bottom > cur.R.Top + 1; break;
                    case UiKey.Down: if (c.R.Top < cur.R.Bottom - eps) continue; gap = c.R.Top - cur.R.Bottom; lateral = Math.Abs(c.CenterX - cur.CenterX); overlap = c.R.Left < cur.R.Right - 1 && c.R.Right > cur.R.Left + 1; break;
                    case UiKey.Up: if (c.R.Bottom > cur.R.Top + eps) continue; gap = cur.R.Top - c.R.Bottom; lateral = Math.Abs(c.CenterX - cur.CenterX); overlap = c.R.Left < cur.R.Right - 1 && c.R.Right > cur.R.Left + 1; break;
                    default: return null;
                }
                float score = Math.Max(0, gap) + lateral * 3f + (overlap ? 0 : 400f);
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }
    }

    /// <summary>
    /// Animated vertical scroll of content taller than its visible zone (0.3 s): keeps the focused element in view. With touch, a finger can take it over: it follows the finger, keeps
    /// going when the finger is lifted at speed and bounces softly past the ends (see Inertia); a key or a focus change takes it back (the eased scroll resumes from where the touch left it).
    /// </summary>
    public sealed class ScrollArea
    {
        public float ViewTop, ViewBottom, ContentHeight;
        private readonly Anim _y = new Anim(0, Bezier.Pop);
        private readonly Inertia _touch = new Inertia();
        private bool _touching;           // the touch physics owns the position (finger down, coasting or rebounding)
        public float Margin = 28;

        public float Offset(double now)
        {
            if (_touching) { Advance(now); if (_touching) return _touch.Position; }
            return _y.Value(now);
        }
        public float Target { get { return _touching ? _touch.Position : _y.Target; } }
        public bool Active(double now)
        {
            if (_touching) { Advance(now); if (_touching) return true; }
            return _y.Active(now);
        }
        public void Snap(float v) { _touching = false; _touch.Halt(0); _y.Snap(v); }

        public float MaxOffset { get { return Math.Max(0, ContentHeight - ViewBottom); } }

        // ---------------------------------------------------------------- touch
        /// <summary>The finger's inertia or rebound is still moving the content.</summary>
        public bool Coasting { get { return _touching && _touch.Coasting; } }
        /// <summary>The finger is dragging the content now.</summary>
        public bool Dragging { get { return _touching && _touch.Dragging; } }
        /// <summary>Content is pulled or bouncing past an end.</summary>
        public bool Overscrolled { get { return _touching && _touch.Overscrolled; } }

        private void Advance(double now)
        {
            _touch.Min = 0; _touch.Max = MaxOffset;
            _touch.Advance(now);
            if (!_touch.Active) { _touching = false; _y.Snap(_touch.Position); }
        }

        /// <summary>A finger came down on content that is still coasting: stop it where it is (an overscrolled list keeps rebounding).</summary>
        public void Grab(double now)
        {
            if (!_touching || !_touch.Coasting || _touch.Overscrolled) return;
            Advance(now);
            if (_touching) { float p = _touch.Position; _touch.Halt(now); _touching = false; _y.Snap(p); }
        }

        /// <summary>The drag begins: from now on the finger drives the position.</summary>
        public void BeginDrag(double now)
        {
            float cur = Offset(now);
            _touch.Min = 0; _touch.Max = MaxOffset;
            _touch.Begin(cur, now);
            _touching = true;
        }

        /// <summary>The finger moved by this much (the content moves the same amount, with resistance past the ends).</summary>
        public void DragBy(float delta) { if (_touching) { _touch.Min = 0; _touch.Max = MaxOffset; _touch.DragBy(delta); } }

        /// <summary>The finger was lifted at this speed (content units per second).</summary>
        public void EndDrag(float velocity, double now)
        {
            if (!_touching) return;
            _touch.Min = 0; _touch.Max = MaxOffset;
            _touch.Release(velocity, now);
            if (!_touch.Active) { _touching = false; _y.Snap(_touch.Position); }
        }

        /// <summary>Adjusts the scroll so that 'r' (content coordinates, unscrolled) stays visible within the zone [ViewTop, ViewBottom] with a margin.</summary>
        public void EnsureVisible(SKRect r, double now, bool instant = false)
        {
            if (_touching) { float p = Math.Max(0, Math.Min(MaxOffset, _touch.Position)); _touch.Halt(now); _touching = false; _y.Snap(p); }
            float cur = _y.Target;
            float maxOff = r.Top - Margin - ViewTop;          // otherwise the top edge goes out
            float minOff = r.Bottom + Margin - ViewBottom;    // otherwise the bottom one goes out
            float t = cur;
            if (cur > maxOff) t = maxOff;
            else if (cur < minOff) t = minOff;
            t = Math.Max(0, Math.Min(MaxOffset, t));
            if (Math.Abs(t - cur) > 0.5f) _y.Set(t, now, instant ? 0 : 0.3);
        }
    }
}
