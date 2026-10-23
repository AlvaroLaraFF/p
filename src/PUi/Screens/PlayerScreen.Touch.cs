using System;
using System.Collections.Generic;
using AceEngine.App;
using PUi.Core;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// Touch for the player. A tap on the picture shows the controls panel or hides it (like the Info key); with the panel up, its buttons (pause, back 10 s, forward 10 s, LIVE) and the
    /// next-channel card can be tapped, and the position bar can be dragged: the knob follows the finger inside what is stored in memory (0 = the oldest, 1 = live) and the player moves
    /// when the finger is lifted. The system Back gesture arrives as UiKey.Back, like the remote's Back. Nothing here runs on a platform without touch.
    /// </summary>
    public sealed partial class PlayerScreen
    {
        /// <summary>The panel's focus ring is not drawn (the finger is the pointer).</summary>
        private bool _quiet;
        private int _ptr = -1;
        private float _downX, _downY;
        private bool _tapAllowed, _scrub;
        private float _scrubFrac;
        private TouchTarget _hit, _pressShown;
        private readonly Anim _press = new Anim(0, Bezier.Ease);
        private SKRect Canvas { get { return _ctx != null && _ctx.Compact ? new SKRect(0, 0, _ctx.CW, _ctx.CH) : new SKRect(0, 0, Tokens.W, Tokens.H); } }

        private void ResetTouch() { _ptr = -1; _hit = null; _pressShown = null; _scrub = false; _volDrag = false; _vol.Abort(); _press.Snap(0); }

        // ---------------------------------------------------------------- what can be touched
        public IReadOnlyList<TouchTarget> TouchTargets()
        {
            var list = new List<TouchTarget>();
            if (_ctx == null || !_ctx.HasTouch) return list;
            CollectTargets(list, _ctx.Now);
            return list;
        }

        /// <summary>The position bar can be dragged: there is a window of memory behind live to move in.</summary>
        private bool CanScrub { get { return _ctl.Status.IsLive && _ctl.WindowMs >= PlayerController.MinWindowMs && _pb != null; } }

        private void CollectTargets(List<TouchTarget> into, double now)
        {
            if (Compact) { CollectCompactTargets(into, now); return; }
            if (!_panelShown || _ctl.Mode == PlayerMode.Loading) return;
            var gx = _ctx.Gx;
            var g = Geom(gx);
            // the panel itself takes the touches on its empty parts (they only keep it on screen)
            into.Add(new TouchTarget { Id = "panel", Rect = g.Panel, Radius = 36, Press = false, Tap = () => _ctl.ShowPanel(_ctx.Now) });
            if (CanScrub)
                into.Add(new TouchTarget { Id = "timeline", Rect = new SKRect(g.X0 - 17, g.Ty + 3, g.X1 + 17, g.Ty + 37), Radius = 17, Layer = 1, Press = false });
            into.Add(Button("back10", g.Back10Box, 32, PanelButton.Back10));
            into.Add(Button("play", g.PlayBox, 42, PanelButton.PlayPause));
            into.Add(Button("forward10", g.Fwd10Box, 32, PanelButton.Forward10));
            into.Add(Button("live", g.Live, 28, PanelButton.Live));
            if (_pc.List.Count > 1 && _ctl.Mode != PlayerMode.Paused && _ctl.CanChangeChannel)
                into.Add(new TouchTarget { Id = "next", Rect = NextCardBox(gx), Radius = 24, Layer = 1, Tap = () => { _ctl.ShowPanel(_ctx.Now); Perform(PlayerAction.NextChannel); } });
        }

        private TouchTarget Button(string id, SKRect box, float radius, PanelButton b)
        {
            return new TouchTarget { Id = id, Rect = box, Radius = radius, Layer = 1, Tap = () => Perform(_ctl.OnTouchButton(b, _ctx.Now)) };
        }

        // ---------------------------------------------------------------- the gesture
        public void OnPointer(PointerEvent e)
        {
            if (_ctx == null) return;
            double now = _ctx.Now;
            switch (e.Action)
            {
                case PointerAction.Down: PointerDown(e, now); break;
                case PointerAction.Move: if (e.Id == _ptr) PointerMove(e, now); break;
                case PointerAction.Up: if (e.Id == _ptr) PointerUp(e, now, false); break;
                case PointerAction.Cancel: if (e.Id == _ptr) PointerUp(e, now, true); break;
            }
        }

        private void PointerDown(PointerEvent e, double now)
        {
            if (_ptr >= 0 && e.Id != _ptr) return;                  // a second finger; the same finger again means its Up was lost
            _quiet = true;
            _ptr = e.Id; _downX = e.X; _downY = e.Y; _tapAllowed = true; _scrub = false; _hit = null;
            if (_ctl.Mode == PlayerMode.Loading && !Compact) return;
            var targets = new List<TouchTarget>();
            CollectTargets(targets, now);
            _hit = TouchHit.Find(targets, e.X, e.Y, TouchSizes.MinTargetLogical(_ctx.Surface), Canvas);
            if (_hit != null && _hit.Id == "cast:volume") { _volDrag = true; _vol.Begin(CastUi.VolumeTrack(_hit.Rect), e.X, now, _ctx.CastUi.SetVolume); return; }
            if (_hit != null && _hit.Id == "timeline")
            {
                _scrub = true; _scrubFrac = FractionAt(e.X);
                _ctl.ShowPanel(now);
            }
            else if (_hit != null && _hit.Press) { _pressShown = _hit; _press.Set(1, now, 0.08); }
        }

        private void PointerMove(PointerEvent e, double now)
        {
            if (_volDrag) { _vol.Move(e.X, now, _ctx.CastUi.SetVolume); return; }
            if (_scrub) { _scrubFrac = FractionAt(e.X); _ctl.ShowPanel(now); return; }
            float dx = e.X - _downX, dy = e.Y - _downY, slop = TouchSizes.Slop(_ctx.Surface);
            if (_tapAllowed && dx * dx + dy * dy >= slop * slop) { _tapAllowed = false; _press.Set(0, now, 0.12); }
        }

        private void PointerUp(PointerEvent e, double now, bool cancel)
        {
            _ptr = -1;
            var hit = _hit; _hit = null;
            _press.Set(0, now, 0.22);
            if (_volDrag) { _volDrag = false; _vol.End(e.X, now, _ctx.CastUi.SetVolume, cancel); return; }
            if (_scrub)
            {
                _scrub = false;
                if (!cancel) { _scrubFrac = FractionAt(e.X); CommitScrub(now); }
                return;
            }
            if (cancel || !_tapAllowed) return;
            var mode = _ctl.Mode;
            if (mode == PlayerMode.Loading && !(Compact && hit != null)) return;
            if (mode == PlayerMode.Error && !_ctl.Started) { if (Compact && hit != null && hit.Tap != null) hit.Tap(); else Perform(PlayerAction.Retry); return; }
            if (hit != null) { if (hit.Tap != null) hit.Tap(); return; }
            if (PageLayout) return;                                 // the page's information is always there: a tap on the picture does nothing
            _ctl.TogglePanel(now);                                  // a tap on the picture
        }

        /// <summary>The bar's fraction (0 = oldest in memory, 1 = live) under a finger at x.</summary>
        private float FractionAt(float x)
        {
            if (Compact) { var cg = Cg(); float cw = cg.X1 - cg.X0; return cw <= 0 ? 1f : Math.Max(0f, Math.Min(1f, (x - cg.X0) / cw)); }
            var g = Geom(_ctx.Gx);
            float w = g.X1 - g.X0;
            return w <= 0 ? 1f : Math.Max(0f, Math.Min(1f, (x - g.X0) / w));
        }

        /// <summary>The finger was lifted on the bar: move the player to that point (relative to where it is now), or to live if it is at the end.</summary>
        private void CommitScrub(double now)
        {
            var pb = _pb;
            if (pb == null) return;
            long window = _ctl.WindowMs, behindNow = _ctl.BehindMs(now);
            long target = (long)Math.Round((1.0 - _scrubFrac) * window);
            _ctl.ShowPanel(now);
            if (target <= 2500) { if (behindNow > 2500 || _ctl.Mode == PlayerMode.Paused) Perform(PlayerAction.GoLive); return; }
            long delta = behindNow - target;                        // positive = forward, as SeekBehindLiveAsync takes it
            if (Math.Abs(delta) < 800) return;
            FireAndForget(pb.SeekBehindLiveAsync(TimeSpan.FromMilliseconds(delta)));
        }

        private void DrawPress(SKCanvas c, double now)
        {
            var t = _pressShown;
            if (t == null) return;
            float a = _press.Value(now);
            if (a <= 0.01f) { if (!_press.Active(now) && _ptr < 0) _pressShown = null; return; }
            float slide = PageLayout ? 0 : (1 - _panel.Value(now)) * 32;
            c.Save();
            c.Translate(0, slide);
            _ctx.Gx.FillRR(c, t.Rect, t.Radius, Tokens.White(0.18f * a));
            _ctx.Gx.StrokeRR(c, t.Rect, t.Radius, 2, Tokens.White(0.42f * a));
            c.Restore();
        }
    }
}
