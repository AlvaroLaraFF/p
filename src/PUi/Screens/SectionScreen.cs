using System;
using System.Collections.Generic;
using AceEngine.App;
using PUi.Core;
using PUi.Gfx;
using PUi.Input;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// Base of the screens with a side menu (TV, Category, Search, Settings): background with lights and parallax, icon menu, focus with fluid border,
    /// scrolling and key dispatch. Left goes to the menu; Back leaves the subscreen, or the app from TV.
    /// </summary>
    public abstract partial class SectionScreen : IScreen
    {
        protected UiContext Ctx;
        protected readonly Focusable[] RailItems = new Focusable[3];
        protected readonly List<Focusable> Content = new List<Focusable>();
        protected Focusable Focused;
        protected bool RailFocused;
        protected readonly ScrollArea Scroll = new ScrollArea();
        private Focusable _lastContentFocus;
        protected double LastNow;
        /// <summary>The user has already pressed some key (before that, the initial focus may jump to the content when it arrives).</summary>
        protected bool UserMoved;
        private volatile bool _dirty;
        private double _tail;
        private int _warmIdx;

        public abstract Section Section { get; }
        public abstract string Name { get; }
        /// <summary>true on the section's root screens (TV, Search, Settings): Back from them leaves the section/app.</summary>
        public virtual bool IsRoot { get { return true; } }
        public virtual bool WindowOpaque { get { return true; } }

        protected SectionScreen()
        {
            float[] y = { 159, 249, 339 };
            for (int i = 0; i < 3; i++)
                RailItems[i] = new Focusable { Id = "rail" + i, Group = "rail", R = new SKRect(60, y[i], 132, y[i] + 72), FocusScale = 1f, Tag = (Section)i };
        }

        // ---------------------------------------------------------------- lifecycle
        private bool _entered;

        public void Enter(UiContext ctx)
        {
            Ctx = ctx;
            bool first = !_entered;
            _entered = true;
            _laidFor = ctx.Surface;
            if (ctx.Compact) LayoutNav();
            ResetTouch();
            if (first) Quiet = ctx.TouchFirst;                  // a phone: the focus starts invisible (the finger decides what is selected)
            OnEnter(first);
            Ctx.Nav.Invalidate();
        }

        public virtual void Leave() { EndEditing(); }
        public virtual void Suspend() { EndEditing(); AbortTouch(); }
        public virtual void Resume() { MarkDirty(); }
        /// <summary>On entering the screen; first = false when returning to it from another one that was pushed on top (focus and scroll are kept).</summary>
        protected abstract void OnEnter(bool first);

        /// <summary>The services' events arrive on pool threads: they only raise this flag; the screen is rebuilt in Update.</summary>
        protected void MarkDirty() { _dirty = true; if (Ctx != null) Ctx.Nav.Invalidate(); }
        protected bool TakeDirty() { if (!_dirty) return false; _dirty = false; return true; }

        // ---------------------------------------------------------------- focus
        protected void SetFocus(Focusable f, double now, bool instant = false)
        {
            if (f == null || f == Focused) { if (f != null && instant) f.SetFocused(!Quiet, now, true); return; }
            if (Focused != null) Focused.SetFocused(false, now, instant);
            Focused = f;
            f.SetFocused(!Quiet, now, instant);
            bool rail = f.Group == "rail";
            RailFocused = rail;
            if (!rail) _lastContentFocus = f;
            // parallax: the background moves opposite to the focus
            float cy = rail ? f.CenterY : f.CenterY - Scroll.Target * 0;
            float screenY = rail ? f.CenterY : f.CenterY - Scroll.Offset(now);
            Ctx.Ambient.FocusMoved(f.CenterX, screenY, now);
            if (!rail && !PointerFocus) Scroll.EnsureVisible(f.R, now, instant);
            _warmIdx = 0;
            OnFocusChanged(f, now);
        }

        protected virtual void OnFocusChanged(Focusable f, double now) { }

        protected void FocusRail(double now)
        {
            SetFocus(RailItems[(int)Section], now);
        }

        protected void FocusContent(double now)
        {
            var target = _lastContentFocus != null && Content.Contains(_lastContentFocus) ? _lastContentFocus : FirstFocusable();
            if (target != null) SetFocus(target, now);
        }

        protected virtual Focusable FirstFocusable() { return Content.Count > 0 ? Content[0] : null; }

        // ---------------------------------------------------------------- selectors with edit mode (EditableSelector)
        private readonly List<EditableSelector> _selectors = new List<EditableSelector>();

        /// <summary>The screen declares its selectors on every rebuild (their rows go in Content; the zones do not). OK on the row enters editing.</summary>
        protected void TrackSelectors(IEnumerable<EditableSelector> selectors)
        {
            var keep = new List<EditableSelector>(selectors);
            foreach (var old in _selectors) if (!keep.Contains(old) && old.Editing) old.End();
            _selectors.Clear(); _selectors.AddRange(keep);
        }

        protected EditableSelector SelectorOfRow(Focusable f) { return f == null ? null : _selectors.Find(s => s.Row == f); }
        protected EditableSelector EditingSelector { get { return _selectors.Find(s => s.Editing); } }
        protected bool AnySelectorAnimating(double now) { foreach (var s in _selectors) if (s.Animating(now)) return true; return false; }

        /// <summary>Enters editing: the focus and the fluid border move from the row to the selector's zone.</summary>
        protected void BeginEdit(EditableSelector sel, double now)
        {
            sel.Begin();
            SetFocus(sel.Zone, now);
        }

        /// <summary>Leaves editing: the focus returns to the row (without leaving the screen).</summary>
        protected void EndEdit(EditableSelector sel, double now)
        {
            sel.End();
            SetFocus(sel.Row, now);
        }

        /// <summary>Drops any editing (on leaving the screen or suspending the app).</summary>
        protected void EndEditing()
        {
            foreach (var s in _selectors)
                if (s.Editing) { s.End(); if (Focused == s.Zone) { Focused = null; s.Zone.SetFocused(false, 0, true); } }
        }

        // ---------------------------------------------------------------- keys
        public void OnKey(KeyPress key)
        {
            double now = Ctx.Now;
            UserMoved = true;
            if (Quiet && (key.Key == UiKey.Up || key.Key == UiKey.Down || key.Key == UiKey.Left || key.Key == UiKey.Right || key.Key == UiKey.Ok)) SetQuiet(false, now);       // a key brings the focus back
            if (RailFocused)
            {
                int idx = Array.IndexOf(RailItems, Focused);
                if (BottomBar)
                {
                    // the sections are along the bottom: left and right move along them, up goes back to the content
                    switch (key.Key)
                    {
                        case UiKey.Left: if (idx > 0) SetFocus(RailItems[idx - 1], now); return;
                        case UiKey.Right: if (idx < 2) SetFocus(RailItems[idx + 1], now); return;
                        case UiKey.Up: FocusContent(now); return;
                        case UiKey.Ok: Ctx.Nav.GoSection((Section)idx); return;
                        case UiKey.Back: GoBack(); return;
                    }
                    return;
                }
                switch (key.Key)
                {
                    case UiKey.Up: if (idx > 0) SetFocus(RailItems[idx - 1], now); return;
                    case UiKey.Down: if (idx < 2) SetFocus(RailItems[idx + 1], now); return;
                    case UiKey.Right: FocusContent(now); return;
                    case UiKey.Ok: Ctx.Nav.GoSection((Section)idx); return;
                    case UiKey.Back: GoBack(); return;
                }
                return;
            }
            var editing = EditingSelector;
            if (editing != null)
            {
                // edit mode: left/right change the value; OK and Back leave; the rest does nothing
                if (editing.OnEditKey(key.Key, now)) EndEdit(editing, now);
                Ctx.Nav.Invalidate();
                return;
            }
            if (HandleKey(key, now)) return;
            switch (key.Key)
            {
                case UiKey.Up: case UiKey.Down: case UiKey.Left: case UiKey.Right:
                    {
                        var next = Spatial.Find(Content, Focused, key.Key);
                        if (next != null) SetFocus(next, now);
                        else if (key.Key == UiKey.Left && !BottomBar) FocusRail(now);
                        else if (key.Key == UiKey.Down && BottomBar) FocusRail(now);
                        else OnEdge(key.Key, now);
                        return;
                    }
                case UiKey.Ok:
                    {
                        var sel = SelectorOfRow(Focused);
                        if (sel != null) { BeginEdit(sel, now); return; }
                        if (Focused != null) Activate(Focused);
                        return;
                    }
                case UiKey.Back: GoBack(); return;
            }
        }

        /// <summary>The screen's own keys (OK on a switch, letter keys...). Returns true if it consumes them.</summary>
        protected virtual bool HandleKey(KeyPress key, double now) { return false; }
        /// <summary>The edge was reached with no neighbor (up/down/right).</summary>
        protected virtual void OnEdge(UiKey dir, double now) { }
        protected abstract void Activate(Focusable item);

        protected virtual void GoBack()
        {
            if (!IsRoot) Ctx.Nav.Pop();
            else if (Section != Section.Tv) Ctx.Nav.GoSection(Section.Tv);
            else Ctx.Nav.Exit();
        }

        // ---------------------------------------------------------------- frame
        protected virtual bool ExtraAnimating(double now) { return false; }
        protected virtual void OnUpdate(double now) { }
        /// <summary>
        /// true while the screen shows a loading indicator that must keep moving (spinner, skeleton shimmer, indeterminate bar): those do not slow down at rest.
        /// Without them, the only thing that moves is the repeating decoration (the focus's fluid border, strip, live dot), which slows down and stops (FrameDemand.Decor).
        /// </summary>
        protected virtual bool ShowsProgress(double now) { return false; }
        /// <summary>Why it asked for frames the last time (for the log and the tests): "motion", "loading" or "decor".</summary>
        public string DemandReason { get; private set; }

        public FrameDemand Update(double now)
        {
            LastNow = now;
            if (!ReferenceEquals(_laidFor, Ctx.Surface))
            {
                // the surface changed (rotation, split screen, insets): a phone or a tablet lays itself out again, keeping the screen, the focus and the scroll
                _laidFor = Ctx.Surface;
                if (Ctx.Compact) { LayoutNav(); OnSurfaceChanged(now); }
            }
            OnUpdate(now);
            bool full = TouchAnimating(now) || Scroll.Active(now) || Ctx.Ambient.Parallaxing(now) || ExtraAnimating(now);
            if (!full && AnySelectorAnimating(now)) full = true;
            if (!full) foreach (var f in Content) if (f.Animating(now)) { full = true; break; }
            if (!full) foreach (var f in RailItems) if (f.Animating(now)) { full = true; break; }
            // a couple more frames after a movement ends: this way the last one (exact final value) is always drawn
            if (full) _tail = now + 0.12; else if (now < _tail) full = true;
            if (full) { DemandReason = "motion"; return FrameDemand.Full; }
            if (ShowsProgress(now)) { DemandReason = "loading"; return FrameDemand.Ambient; }
            DemandReason = "decor";
            return FrameDemand.Decor;
        }

        public void Draw(SKCanvas c, double now)
        {
            Ctx.DrawBackdrop(c, now);
            if (Ctx.Compact)
            {
                var m = Ctx.Style.Main;
                c.Save();
                c.ClipRect(new SKRect(m.Left, m.Top - 8, m.Right, m.Bottom + 8));
                DrawContent(c, now);
                c.Restore();
                DrawHeader(c, now);
                DrawNavCompact(c, now);
                DrawPress(c, now);
                WarmStep(c, now);
                return;
            }
            c.Save();
            c.ClipRect(new SKRect(Tokens.MainLeft - 40, 0, Tokens.W, Tokens.H));
            DrawContent(c, now);
            c.Restore();
            DrawRail(c, now);
            DrawPress(c, now);
            WarmStep(c, now);
        }

        /// <summary>Creates ahead of time the sprites of the cards near the focus (at most ~4 ms per frame) so moving the focus does not create textures.</summary>
        protected virtual void WarmItem(SKCanvas c, Focusable f, double now) { }
        protected void ResetWarm() { _warmIdx = 0; }

        private void WarmStep(SKCanvas c, double now)
        {
            if (_warmIdx >= Content.Count) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int guard = 0;
            while (_warmIdx < Content.Count && guard++ < 64)
            {
                if (Ctx.Sprites.OverBudget) { Ctx.Nav.Invalidate(); break; }        // the frame already created its share: the rest, in the next one
                var f = Content[_warmIdx++];
                try { WarmItem(c, f, now); } catch (Exception ex) { AceEngine.Util.AceLog.Warn("ui", "pre-warm: " + ex.Message); }
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > 4.0) break;
            }
        }

        protected abstract void DrawContent(SKCanvas c, double now);

        // ---------------------------------------------------------------- side menu
        private void DrawRail(SKCanvas c, double now)
        {
            var gx = Ctx.Gx;
            var bounds = new SKRect(36, 36, 156, 1044);
            var img = Ctx.Sprites.Get("rail|" + (int)Section, bounds, Ctx.K, cv => DrawRailStatic(cv));
            gx.DrawImage(c, img, bounds);
            for (int i = 0; i < 3; i++)
            {
                var it = RailItems[i];
                float f = it.Focus.Value(now), ring = it.Ring.Value(now);
                bool active = i == (int)Section;
                if (f > 0.01f)
                {
                    if (!active) gx.FillRR(c, it.R, 24, Tokens.White(0.12f * f));
                    gx.DrawIcon(c, RailIcon(i), it.CenterX, it.CenterY, 30, Tokens.TextA(f), 1.8f);
                }
                if (ring > 0.01f) Ctx.Fx.FluidRing(c, it.R, 24, ring, now);
            }
        }

        private static Icon RailIcon(int i) { return i == 0 ? Icons.Tv : i == 1 ? Icons.Search : Icons.Sliders; }

        private void DrawRailStatic(SKCanvas c)
        {
            var gx = Ctx.Gx;
            var r = new SKRect(Tokens.RailX, Tokens.RailY, Tokens.RailX + Tokens.RailW, Tokens.RailY + Tokens.RailH);
            gx.FillRR(c, r, Tokens.RailRadius, Tokens.White(0.06f));
            gx.StrokeRR(c, r, Tokens.RailRadius, 1, Tokens.White(0.10f));
            var logo = Ctx.Logo;
            if (logo != null)
            {
                // the brand logo, 60 units wide, centred on the rail above the first item
                float lw = 60, lh = lw * logo.Height / logo.Width;
                gx.DrawImage(c, logo, new SKRect(96 - lw / 2, 112 - lh, 96 + lw / 2, 112), 1f, SKFilterQuality.High);
            }
            else
            {
                float wP = gx.MeasureText("P", 26, 800, -1.04f), wB = gx.MeasureText("!", 26, 800, -1.04f);
                float lx = 96 - (wP + wB) / 2;
                gx.DrawText(c, "P", lx, 105, 26, 800, Tokens.Text, -1.04f);
                gx.DrawText(c, "!", lx + wP, 105, 26, 800, Tokens.Accent, -1.04f);
            }
            for (int i = 0; i < 3; i++)
            {
                var it = RailItems[i];
                bool active = i == (int)Section;
                if (active)
                {
                    gx.FillRR(c, it.R, 24, Tokens.C(157, 107, 255, 0.22f));
                    gx.StrokeRR(c, it.R, 24, 1, Tokens.C(157, 107, 255, 0.45f));
                }
                gx.DrawIcon(c, RailIcon(i), it.CenterX, it.CenterY, 30, active ? Tokens.Text : Tokens.TextA(0.66f), 1.8f);
            }
        }

        public virtual string Describe()
        {
            return Name + (RailFocused ? " [menu " + Array.IndexOf(RailItems, Focused) + "]" : Focused != null ? " [focus " + Focused.Id + "]" : "");
        }
    }
}
