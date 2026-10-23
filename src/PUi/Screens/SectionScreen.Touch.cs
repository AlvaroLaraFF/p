using System;
using System.Collections.Generic;
using PUi.Core;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// Touch for the screens with a side menu. A finger does what the remote does, with the same result: a tap is "focus + OK" on the element under the finger (open a channel, enter a
    /// category, change section, flip a switch, change a selector...); a drag scrolls the lists with inertia and a soft rebound and never taps; while the finger is down the element it is on is
    /// highlighted. Nothing here runs on a platform without touch.
    ///
    /// Where the finger lands is decided with the boxes as they are DRAWN (<see cref="TouchTarget"/>); boxes smaller than a fingertip (7 mm, see TouchSizes) get a larger touch zone around
    /// them, and the drawing does not change. The focus is invisible while the user works with the finger (<see cref="Quiet"/>) and comes back with the first arrow or OK key.
    /// </summary>
    public abstract partial class SectionScreen
    {
        /// <summary>The focus is not drawn (no ring, no scale): the finger is the pointer. Set on touch-only devices from the start, and by any touch; cleared by an arrow key or OK.</summary>
        protected bool Quiet;
        /// <summary>The focus is being moved by a tap: do not scroll or slide to it (the element is where the finger is).</summary>
        protected bool PointerFocus;

        /// <summary>The focus is drawn (a remote or a key is driving the screen); false while a finger does (for the tests and the log).</summary>
        public bool FocusVisible { get { return !Quiet; } }
        /// <summary>The vertical scroll position now (for the tests and the log).</summary>
        public float ScrollPosition { get { return Ctx == null ? 0 : Scroll.Offset(Ctx.Now); } }
        /// <summary>The list is moving by itself after a flick, or bouncing back from an end.</summary>
        public bool ScrollCoasting { get { return Scroll.Coasting; } }
        /// <summary>The list is pulled or bouncing past an end.</summary>
        public bool ScrollOverscrolled { get { return Scroll.Overscrolled; } }

        /// <summary>The app goes to the background: a gesture in progress is dropped.</summary>
        protected void AbortTouch() { if (Ctx != null) AbortGesture(Ctx.Now); }

        protected void SetQuiet(bool on, double now)
        {
            if (Quiet == on) return;
            Quiet = on;
            if (Focused != null) Focused.SetFocused(!on, now);
        }

        // ---------------------------------------------------------------- what a screen says about its touch layout
        /// <summary>true if the element is drawn shifted by the vertical scroll (false for fixed headers).</summary>
        protected virtual bool ScrollsWithContent(Focusable f) { return true; }

        /// <summary>Where the element is on the screen right now (the layout position moved by the scroll and by any other shift the screen draws it with).</summary>
        protected virtual SKRect ScreenRect(Focusable f, double now, float scrollY) { return ScrollsWithContent(f) ? TouchHit.Offset(f.R, 0, -scrollY) : f.R; }

        /// <summary>The part of the screen where the element can be touched (what is not hidden under a header or outside the clip it is drawn in).</summary>
        protected virtual SKRect? ContentClip(Focusable f, double now) { return ContentRegion; }

        /// <summary>The area where dragging scrolls: everything but the side menu.</summary>
        protected virtual SKRect ContentRegion { get { return Ctx != null && Ctx.Compact ? Ctx.Style.Main : new SKRect(Tokens.MainLeft - 40, 0, Tokens.W, Tokens.H); } }

        protected virtual float RadiusOf(Focusable f) { return Math.Min(24f, Math.Min(f.R.Width, f.R.Height) / 2); }

        /// <summary>The tap on an element of the content, after it took the focus. The same as OK on it unless the screen says otherwise (the search field opens the system keyboard).</summary>
        protected virtual void TapContent(Focusable f, double now) { Activate(f); }

        /// <summary>A vertical drag starts (the search closes the system keyboard to give the list the whole screen).</summary>
        protected virtual void OnScrollStart() { }

        private static readonly IReadOnlyList<HStrip> NoStrips = new HStrip[0];
        /// <summary>The rows that scroll sideways by touch.</summary>
        protected virtual IReadOnlyList<HStrip> Strips { get { return NoStrips; } }

        /// <summary>The boxes that react to a finger. Screens add theirs (or replace the common ones) by overriding.</summary>
        protected virtual void CollectTargets(List<TouchTarget> into, double now)
        {
            foreach (var f in RailItems) into.Add(new TouchTarget { Id = f.Id, Rect = f.R, Radius = Ctx.Compact ? Ctx.Style.NavItemRadius : 24, Focus = f });
            if (Ctx.Compact) CollectChromeTargets(into);
            float sy = Scroll.Offset(now);
            var canvas = CanvasRect;
            foreach (var f in Content)
            {
                if (!f.Enabled) continue;
                var r = ScreenRect(f, now, sy);
                if (r.Bottom < -60 || r.Top > canvas.Bottom + 60 || r.Right < -60 || r.Left > canvas.Right + 60) continue;       // far outside the screen: it cannot be touched
                into.Add(new TouchTarget { Id = f.Id, Rect = r, Clip = ContentClip(f, now), Radius = RadiusOf(f), Focus = f });
            }
        }

        public IReadOnlyList<TouchTarget> TouchTargets()
        {
            var list = new List<TouchTarget>();
            if (Ctx == null) return list;
            var caps = Ctx.Platform.Capabilities;
            if (caps == null || !caps.HasTouch) return list;
            CollectTargets(list, Ctx.Now);
            return list;
        }

        // ---------------------------------------------------------------- the gesture
        private enum Gesture { None, Press, ScrollV, ScrollH, Dead }
        private Gesture _gesture = Gesture.None;
        private int _ptr = -1;
        private float _downX, _downY, _lastX, _lastY;
        private bool _tapAllowed, _scrollable;
        private TouchTarget _hit, _pressShown;
        private HStrip _strip;
        private readonly List<TouchTarget> _targets = new List<TouchTarget>();
        private readonly VelocityTracker _velX = new VelocityTracker(), _velY = new VelocityTracker();
        private readonly Anim _press = new Anim(0, Bezier.Ease);

        /// <summary>The canvas in logical units (1920 x 1080 on a TV; the phone's or tablet's own size): where a finger can land at all.</summary>
        private SKRect Canvas { get { return CanvasRect; } }

        private void ResetTouch()
        {
            _gesture = Gesture.None; _ptr = -1; _hit = null; _pressShown = null; _strip = null;
            _press.Snap(0);
        }

        public void OnPointer(PointerEvent e)
        {
            if (Ctx == null) return;
            double now = Ctx.Now;
            switch (e.Action)
            {
                case PointerAction.Down: PointerDown(e, now); break;
                case PointerAction.Move: if (e.Id == _ptr) PointerMove(e, now); break;
                case PointerAction.Up: if (e.Id == _ptr) PointerUp(e, now, false); break;
                case PointerAction.Cancel: if (e.Id == _ptr) PointerUp(e, now, true); break;
            }
        }

        /// <summary>Drops a gesture that will not finish (the app was paused with a finger down, or the platform lost an Up): a drag lets go without a flick.</summary>
        private void AbortGesture(double now)
        {
            if (_gesture == Gesture.ScrollV) Scroll.EndDrag(0, now);
            else if (_gesture == Gesture.ScrollH && _strip != null) _strip.Phys.Release(0, now);
            _gesture = Gesture.None; _ptr = -1; _hit = null; _strip = null;
            _press.Set(0, now, 0.1);
        }

        private void PointerDown(PointerEvent e, double now)
        {
            if (_ptr >= 0)
            {
                if (e.Id != _ptr) return;                           // a second finger: the first one keeps the gesture
                AbortGesture(now);                                  // the same finger again: its Up was lost
            }
            UserMoved = true;
            SetQuiet(true, now);
            _ptr = e.Id; _downX = _lastX = e.X; _downY = _lastY = e.Y;
            _gesture = Gesture.Press; _tapAllowed = true; _strip = null; _hit = null;
            // a finger on a list that is still coasting only stops it: it is not a tap
            if (Scroll.Coasting) { Scroll.Grab(now); _tapAllowed = false; }
            foreach (var s in Strips) if (s.Phys.Coasting) { s.Phys.Halt(now); _tapAllowed = false; }
            _velX.Reset(); _velY.Reset(); _velX.Add(e.Time, e.X); _velY.Add(e.Time, e.Y);
            var region = ContentRegion;
            _scrollable = region.Contains(e.X, e.Y);
            if (_scrollable) foreach (var s in Strips) if (s.Region().Contains(e.X, e.Y)) { _strip = s; break; }
            _targets.Clear();
            CollectTargets(_targets, now);
            var m = Ctx.Surface;
            _hit = TouchHit.Find(_targets, e.X, e.Y, TouchSizes.MinTargetLogical(m), Canvas);
            if (_hit != null && _tapAllowed && _hit.Press) { _pressShown = _hit; _press.Set(1, now, 0.08); }
            else if (_hit == null) { _pressShown = null; }
        }

        private void PointerMove(PointerEvent e, double now)
        {
            _velX.Add(e.Time, e.X); _velY.Add(e.Time, e.Y);
            if (_gesture == Gesture.Dead || _gesture == Gesture.None) { _lastX = e.X; _lastY = e.Y; return; }
            if (_gesture == Gesture.Press)
            {
                float dx = e.X - _downX, dy = e.Y - _downY, slop = TouchSizes.Slop(Ctx.Surface);
                if (dx * dx + dy * dy < slop * slop) return;
                // it is not a tap any more
                _tapAllowed = false;
                _press.Set(0, now, 0.12);
                if (_scrollable && Math.Abs(dy) >= Math.Abs(dx) && Scroll.MaxOffset > 0.5f)
                {
                    _gesture = Gesture.ScrollV;
                    OnScrollStart();
                    Scroll.BeginDrag(now);
                    Scroll.DragBy(-dy);                             // the content has been under the finger since it came down
                }
                else if (_strip != null && Math.Abs(dx) > Math.Abs(dy) && _strip.Max() > 0.5f)
                {
                    _gesture = Gesture.ScrollH;
                    var ph = _strip.Phys;
                    ph.Min = 0; ph.Max = _strip.Max();
                    ph.Begin(_strip.Get(now), now);
                    ph.DragBy(-dx);
                    _strip.Set(ph.Position);
                }
                else _gesture = Gesture.Dead;
                _lastX = e.X; _lastY = e.Y;
                return;
            }
            if (_gesture == Gesture.ScrollV) Scroll.DragBy(-(e.Y - _lastY));
            else if (_gesture == Gesture.ScrollH)
            {
                var ph = _strip.Phys;
                ph.Min = 0; ph.Max = _strip.Max();
                ph.DragBy(-(e.X - _lastX));
                _strip.Set(ph.Position);
            }
            _lastX = e.X; _lastY = e.Y;
        }

        private void PointerUp(PointerEvent e, double now, bool cancel)
        {
            var g = _gesture;
            _gesture = Gesture.None; _ptr = -1;
            switch (g)
            {
                case Gesture.ScrollV:
                    Scroll.EndDrag(cancel ? 0 : -_velY.Velocity(e.Time), now);
                    break;
                case Gesture.ScrollH:
                    {
                        var ph = _strip.Phys;
                        ph.Min = 0; ph.Max = _strip.Max();
                        ph.Release(cancel ? 0 : -_velX.Velocity(e.Time), now);
                        if (!ph.Active) _strip.Set(ph.Position);
                        break;
                    }
                case Gesture.Press:
                    {
                        var t = _hit; _hit = null;
                        _press.Set(0, now, 0.22);
                        if (!cancel && _tapAllowed && t != null) Tap(t, now);
                        break;
                    }
                default: _press.Set(0, now, 0.12); break;
            }
            _strip = null;
        }

        private void Tap(TouchTarget t, double now)
        {
            PointerFocus = true;
            try
            {
                if (t.Tap != null) { t.Tap(); return; }
                var f = t.Focus;
                if (f == null) return;
                SetFocus(f, now);
                if (f.Group == "rail") { Ctx.Nav.GoSection((Section)Array.IndexOf(RailItems, f)); return; }
                if (SelectorOfRow(f) != null) return;               // a selector's row: its − and + are their own targets
                TapContent(f, now);
            }
            finally { PointerFocus = false; }
        }

        // ---------------------------------------------------------------- frame
        private bool TouchAnimating(double now)
        {
            if (_press.Active(now) || _gesture == Gesture.ScrollV || _gesture == Gesture.ScrollH) return true;
            var strips = Strips;
            for (int i = 0; i < strips.Count; i++)
            {
                var s = strips[i];
                if (!s.Phys.Active) continue;
                s.Phys.Max = s.Max();
                s.Phys.Advance(now);
                s.Set(s.Phys.Position);
                if (s.Phys.Active) return true;
            }
            return false;
        }

        private void DrawPress(SKCanvas c, double now)
        {
            var t = _pressShown;
            if (t == null) return;
            float a = _press.Value(now);
            if (a <= 0.01f) { if (!_press.Active(now) && _gesture != Gesture.Press) _pressShown = null; return; }
            c.Save();
            if (t.Clip.HasValue) c.ClipRect(t.Clip.Value);
            Ctx.Gx.FillRR(c, t.Rect, t.Radius, Tokens.White(0.15f * a));
            Ctx.Gx.StrokeRR(c, t.Rect, t.Radius, 2, Tokens.White(0.38f * a));
            c.Restore();
        }
    }
}
