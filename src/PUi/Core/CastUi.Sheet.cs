using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
using PUi.Gfx;
using PUi.Input;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>
    /// The sheet of "Enviar a la tele" (design: the glass of the phone's other panels, aqua for what is live, purple for what is searching, Manrope, the least text that says it) and the strip and
    /// notices that go with it. A bottom sheet on a phone; a floating card on a tablet. The numbers are the phone and tablet design's: touch targets are at least 44 units each way and no text is
    /// under 12.
    /// </summary>
    public sealed partial class CastUi
    {
        private const float Handle = 36f;

        private sealed class RowLayout
        {
            public CastDevice Device;
            public SKRect R;                     // in content space (the top of the scrolled content is 0)
            public List<string> Lines;
            public CastCheck Check;
            public bool Checking;
        }

        private sealed class SheetLayout
        {
            public SKRect Sheet, Panel, Close;
            public float Pad, TitleSize, TitleTop, SubTop;
            public float SearchX;
            public bool HasNow;
            public SKRect Now, NowIcon, NowPause, NowVol, NowStop;
            public bool NowHasPause, NowHasVolume, NowHasControls;
            public float LabelTop; public bool HasLabel;
            public readonly List<RowLayout> Rows = new List<RowLayout>();
            public bool Empty; public float EmptyTop, EmptyH; public SKRect Retry; public bool HasRetry;
            public float ContentH, MaxScroll;
        }

        private static string KindLabel(CastDevice d) { return d.Kind == CastDeviceKind.Chromecast ? "Chromecast" : "Televisor"; }

        // ---------------------------------------------------------------- layout of the sheet

        private SheetLayout Lay(double now)
        {
            var st = _ctx.Style; var gx = _ctx.Gx; var sl = _ctx.SurfaceLogical;
            var L = new SheetLayout();
            bool phone = st.Phone;
            float pad = L.Pad = phone ? 20 : 24;
            float areaL = st.SideRail ? st.Main.Left : 0, areaW = st.W - areaL;
            float sheetW = phone ? st.W : Math.Min(560, areaW - 48);
            float sheetL = phone ? 0 : areaL + (areaW - sheetW) / 2;
            float innerW = sheetW - 2 * pad;
            float bottomEdge = phone ? sl.Bottom : st.H - 20;
            float bottomPad = phone ? 16 + Math.Max(0, sl.Bottom - st.H) : 20;
            float headerH = phone ? 86 : 96;
            L.TitleSize = phone ? 20 : 22;
            var v = View;
            var cur = v.IsActive ? v.Device : null;

            // ---- content, in its own space
            float y = 0;
            if (v.IsActive && v.Device != null)
            {
                L.HasNow = true;
                bool starting = v.Phase == CastPhase.Starting;
                L.NowHasPause = !starting && v.Device.CanPause;
                L.NowHasVolume = !starting && v.Device.CanVolume;
                L.NowHasControls = L.NowHasPause || L.NowHasVolume;
                float h = 16 + 48 + (L.NowHasControls ? 14 + 56 : 0) + 14 + 48 + 16;
                L.Now = new SKRect(0, y, innerW, y + h);
                float cy = y + 16;
                L.NowIcon = new SKRect(16, cy + 2, 16 + 44, cy + 46);
                float controlsTop = cy + 48 + 14;
                if (L.NowHasControls)
                {
                    float x = 16;
                    if (L.NowHasPause) { L.NowPause = new SKRect(x, controlsTop, x + 56, controlsTop + 56); x += 56 + 16; }
                    if (L.NowHasVolume) L.NowVol = new SKRect(x, controlsTop + 6, innerW - 16, controlsTop + 50);
                }
                float stopTop = y + h - 16 - 48;
                L.NowStop = new SKRect(16, stopTop, innerW - 16, stopTop + 48);
                y += h + 18;
            }
            var devices = Cast.Devices.Where(d => cur == null || d.Id != cur.Id).ToList();
            if (L.HasNow && devices.Count > 0) { L.HasLabel = true; L.LabelTop = y; y += 22 + 8; }
            float rowBase = phone ? 68 : 72;
            float textL = 14 + 44 + 14, textR = 14 + 22;
            foreach (var d in devices)
            {
                var chk = CheckOf(d);
                var row = new RowLayout { Device = d, Check = chk, Checking = chk == null && IsChecking(d) };
                int lines = 1;
                if (chk != null && chk.Known && !chk.Compatible && chk.Reason.Length > 0)
                {
                    row.Lines = gx.WrapText(chk.Reason, 13, 600, innerW - textL - textR, 3);
                    lines = Math.Max(1, row.Lines.Count);
                }
                float h = Math.Max(rowBase, 14 + 20 + 3 + 17 * lines + 14);
                row.R = new SKRect(0, y, innerW, y + h);
                L.Rows.Add(row);
                y += h + 10;
            }
            if (devices.Count > 0) y -= 10;
            if (devices.Count == 0 && !L.HasNow)
            {
                L.Empty = true; L.EmptyTop = y;
                bool searching = Cast.Searching;
                L.HasRetry = !searching;
                L.EmptyH = searching ? 150 : 150 + 64;
                if (L.HasRetry) L.Retry = new SKRect(innerW / 2 - 110, y + 150, innerW / 2 + 110, y + 150 + 48);
                y += L.EmptyH;
            }
            else if (devices.Count == 0 && L.HasNow) y -= 18;
            L.ContentH = y;

            // ---- the sheet around it
            float maxSheet = (sl.Bottom - sl.Top) * (phone ? 0.86f : 0.80f);
            float viewH = Math.Min(L.ContentH, Math.Max(120, maxSheet - headerH - bottomPad));
            L.MaxScroll = Math.Max(0, L.ContentH - viewH);
            float sheetH = headerH + viewH + bottomPad;
            float top = bottomEdge - sheetH;
            L.Sheet = new SKRect(sheetL, top, sheetL + sheetW, bottomEdge);
            L.Panel = new SKRect(sheetL + pad, top + headerH, sheetL + pad + innerW, top + headerH + viewH);
            L.Close = new SKRect(sheetL + sheetW - pad - 44, top + 14, sheetL + sheetW - pad, top + 14 + 44);
            L.TitleTop = top + 22; L.SubTop = top + 22 + L.TitleSize * 1.3f + 2;
            L.SearchX = L.Close.Left - 10 - 12;
            return L;
        }

        /// <summary>A rectangle of the content (its own space) put on the screen: moved to the panel and by the scroll.</summary>
        private static SKRect OnScreen(SheetLayout L, SKRect r, float scroll) { return new SKRect(L.Panel.Left + r.Left, L.Panel.Top + r.Top - scroll, L.Panel.Left + r.Right, L.Panel.Top + r.Bottom - scroll); }

        // ---------------------------------------------------------------- the strip over the lists

        /// <summary>The strip ("Enviando a Salón") is on screen: something is being sent or a device is armed, the sheet is closed and the current screen is not the player (which shows the state itself).</summary>
        public bool PillVisible { get { return _ctx.Compact && !OnPlayerScreen && !_open && !_anim.Active(_ctx.Now) && (View.IsActive || Armed != null); } }

        private SKRect PillRect()
        {
            var st = _ctx.Style;
            float h = 60, w, left, bottom;
            if (st.Phone) { w = st.W - 32; left = 16; bottom = st.NavBox.Top - 10; }
            else if (!st.SideRail) { w = Math.Min(560, st.W - 64); left = (st.W - w) / 2; bottom = st.NavBox.Top - 12; }
            else { float areaL = st.Main.Left; w = Math.Min(560, st.W - areaL - 64); left = areaL + (st.W - areaL - w) / 2; bottom = st.H - 20; }
            return new SKRect(left, bottom - h, left + w, bottom);
        }

        private bool PillHasPause(CastStatus v) { return v.Phase == CastPhase.Sending && v.Device != null && v.Device.CanPause; }

        private void PillButtons(SKRect p, CastStatus v, out SKRect pause, out SKRect stop)
        {
            stop = new SKRect(p.Right - 8 - 44, p.Top + 8, p.Right - 8, p.Top + 8 + 44);
            pause = PillHasPause(v) ? new SKRect(stop.Left - 4 - 44, p.Top + 8, stop.Left - 4, p.Top + 8 + 44) : new SKRect();
        }

        // ---------------------------------------------------------------- touch targets (also for the tests and the bench)

        /// <summary>The places that react to a finger right now: the sheet's, or the strip's and the notice's. Empty where the function is not available.</summary>
        public IReadOnlyList<TouchTarget> TouchTargets()
        {
            var list = new List<TouchTarget>();
            if (_disposed || !Available) return list;
            Collect(list, _ctx.Now);
            return list;
        }

        private void Collect(List<TouchTarget> into, double now)
        {
            if (_open)
            {
                var L = Lay(now);
                float dy = (1 - _anim.Value(now)) * (L.Sheet.Height + 40);
                float scroll = _scroll.Position;
                into.Add(new TouchTarget { Id = "cast:close", Rect = TouchHit.Offset(L.Close, 0, dy), Radius = 22, Layer = 2, Tap = () => Close() });
                var panel = TouchHit.Offset(L.Panel, 0, dy);
                if (L.HasNow)
                {
                    var now0 = TouchHit.Offset(OnScreen(L, L.Now, scroll), 0, dy);
                    if (L.NowHasPause) into.Add(new TouchTarget { Id = "cast:pause", Rect = TouchHit.Offset(OnScreen(L, L.NowPause, scroll), 0, dy), Radius = 28, Clip = panel, Layer = 1, Tap = () => TogglePause() });
                    if (L.NowHasVolume) into.Add(new TouchTarget { Id = "cast:volume", Rect = TouchHit.Offset(OnScreen(L, L.NowVol, scroll), 0, dy), Radius = 22, Clip = panel, Layer = 1, Press = false });
                    var stop = TouchHit.Offset(OnScreen(L, L.NowStop, scroll), 0, dy);
                    into.Add(new TouchTarget { Id = "cast:stop", Rect = stop, Radius = 18, Clip = panel, Layer = 1, Tap = () => { Close(); StopSending(); } });
                    into.Add(new TouchTarget { Id = "cast:now", Rect = now0, Radius = 24, Clip = panel, Press = false });
                }
                foreach (var r in L.Rows)
                {
                    var d = r.Device;
                    into.Add(new TouchTarget { Id = "device:" + d.Id, Rect = TouchHit.Offset(OnScreen(L, r.R, scroll), 0, dy), Radius = 20, Clip = panel, Layer = 1, Tap = () => OnDeviceTapped(d) });
                }
                if (L.HasRetry) into.Add(new TouchTarget { Id = "cast:retry", Rect = TouchHit.Offset(OnScreen(L, L.Retry, scroll), 0, dy), Radius = 18, Clip = panel, Layer = 1, Tap = () => { try { Cast.BeginSearch(); } catch (Exception) { } _ctx.Nav.Invalidate(); } });
                into.Add(new TouchTarget { Id = "cast:sheet", Rect = TouchHit.Offset(L.Sheet, 0, dy), Radius = 28, Press = false });
                return;
            }
            if (ToastVisible(now) && _toast.Length > 0) into.Add(new TouchTarget { Id = "cast:toast", Rect = ToastRect(now), Radius = 22, Layer = 3, Press = false, Tap = () => { _toast = ""; _ctx.Nav.Invalidate(); } });
            if (PillVisible)
            {
                var v = View; var p = PillRect();
                SKRect pause, stop; PillButtons(p, v, out pause, out stop);
                into.Add(new TouchTarget { Id = "cast:pill", Rect = p, Radius = 30, Layer = 1, Tap = () => Open(null) });
                if (PillHasPause(v)) into.Add(new TouchTarget { Id = "cast:pill:pause", Rect = pause, Radius = 22, Layer = 2, Tap = () => TogglePause() });
                into.Add(new TouchTarget { Id = "cast:pill:stop", Rect = stop, Radius = 22, Layer = 2, Tap = () => { if (v.IsActive) StopSending(); else CancelArmed(); } });
            }
        }

        private void OnDeviceTapped(CastDevice d)
        {
            var ch = _channel;
            if (ch == null)
            {
                Armed = d;                                  // the strip says "Toca un canal de la lista"
                Close();
                return;
            }
            var chk = CheckOf(d);
            if (chk != null && chk.Known && !chk.Compatible)
            {
                ShowToast(chk.Reason.Length > 0 ? chk.Reason : "Este aparato no puede reproducir el canal", true);
                return;
            }
            Close();
            Send(d, ch);
        }

        // ---------------------------------------------------------------- the gesture

        private int _ptr = -1;
        private float _downX, _downY, _lastY;
        private bool _tapAllowed, _scrolling, _sliding;
        private TouchTarget _hit, _pressShown;
        private readonly CastVolumeSlider _slider = new CastVolumeSlider();
        private readonly VelocityTracker _vel = new VelocityTracker();
        private readonly Anim _press = new Anim(0, Bezier.Ease);

        private void ResetSheetGesture()
        {
            if (_scrolling) { _scroll.Release(0, _ctx.Now); _scrolling = false; }
            _sliding = false; _slider.Abort(); _ptr = -1; _hit = null; _pressShown = null; _press.Snap(0);
        }

        private void ResetPillGesture() { if (!_open) { _ptr = -1; _hit = null; _pressShown = null; _press.Snap(0); } }

        /// <summary>A finger event. Returns true if the cast interface took it (the sheet takes every one while it is open; the strip and the notice take the ones that land on them).</summary>
        public bool OnPointer(PointerEvent e)
        {
            if (_disposed || !Available) return false;
            double now = _ctx.Now;
            switch (e.Action)
            {
                case PointerAction.Down:
                    {
                        if (_ptr >= 0 && e.Id != _ptr) return true;
                        if (_ptr >= 0) { ResetSheetGesture(); }
                        var targets = new List<TouchTarget>();
                        Collect(targets, now);
                        var hit = TouchHit.Find(targets, e.X, e.Y, TouchSizes.MinTargetLogical(_ctx.Surface), _ctx.SurfaceLogical);
                        bool onSheet = hit != null && hit.Id == "cast:sheet";
                        if (!_open && hit == null) return false;
                        _ptr = e.Id; _downX = e.X; _downY = _lastY = e.Y; _tapAllowed = true; _scrolling = false; _sliding = false;
                        _hit = hit != null && hit.Id == "cast:sheet" ? null : hit;
                        _vel.Reset(); _vel.Add(e.Time > 0 ? e.Time : now, e.Y);
                        if (_scroll.Coasting) { _scroll.Halt(now); _tapAllowed = false; }
                        if (_hit != null && _hit.Id == "cast:volume")
                        {
                            _sliding = true;
                            _slider.Begin(VolumeTrack(_hit.Rect), e.X, now, SetVolume);
                        }
                        else if (_hit != null && _hit.Press) { _pressShown = _hit; _press.Set(1, now, 0.08); }
                        _outsideDown = _open && !onSheet && hit == null;
                        return true;
                    }
                case PointerAction.Move:
                    {
                        if (_ptr < 0) return false;
                        if (e.Id != _ptr) return true;
                        if (_sliding) { _slider.Move(e.X, now, SetVolume); return true; }
                        _vel.Add(e.Time > 0 ? e.Time : now, e.Y);
                        float dx = e.X - _downX, dy = e.Y - _downY, slop = TouchSizes.Slop(_ctx.Surface);
                        if (_tapAllowed && dx * dx + dy * dy >= slop * slop)
                        {
                            _tapAllowed = false; _press.Set(0, now, 0.12);
                            if (_open && !_outsideDown && Math.Abs(dy) >= Math.Abs(dx))
                            {
                                var L = Lay(now);
                                _scroll.Min = 0; _scroll.Max = L.MaxScroll;
                                if (L.MaxScroll > 0.5f) { _scrolling = true; _scroll.Begin(_scroll.Position, now); _scroll.DragBy(-dy); }
                            }
                        }
                        else if (_scrolling) _scroll.DragBy(-(e.Y - _lastY));
                        _lastY = e.Y;
                        return true;
                    }
                case PointerAction.Up:
                case PointerAction.Cancel:
                    {
                        if (_ptr < 0) return false;
                        if (e.Id != _ptr) return true;
                        bool cancel = e.Action == PointerAction.Cancel;
                        var hit = _hit; _ptr = -1; _hit = null;
                        _press.Set(0, now, 0.22);
                        if (_sliding) { _sliding = false; _slider.End(e.X, now, SetVolume, cancel); return true; }
                        if (_scrolling) { _scrolling = false; _scroll.Release(cancel ? 0 : _vel.Velocity(now) * -1f, now); return true; }
                        if (cancel || !_tapAllowed) return true;
                        if (hit != null) { if (hit.Tap != null) hit.Tap(); }
                        else if (_open && _outsideDown) Close();
                        return true;
                    }
            }
            return false;
        }

        private bool _outsideDown;

        /// <summary>A key. While the sheet is open Back closes it and every other key is ignored (the screen behind must not move).</summary>
        public bool OnKey(KeyPress key)
        {
            if (_disposed || !Available || !_open) return false;
            if (key.Key == UiKey.Back) Close();
            return true;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>Draws over the current screen: the sheet, or the strip, and the notice.</summary>
        public void Draw(SKCanvas c, double now)
        {
            if (_disposed || !Available) return;
            Watch();
            float a = _anim.Value(now);
            if (_open || a > 0.003f) DrawSheet(c, now, a);
            else if (PillVisible) DrawPill(c, now);
            DrawToast(c, now);
        }

        private void DrawSheet(SKCanvas c, double now, float a)
        {
            var gx = _ctx.Gx; var st = _ctx.Style; var sl = _ctx.SurfaceLogical;
            var L = Lay(now);
            _scroll.Min = 0; _scroll.Max = L.MaxScroll;
            float scroll = _scroll.Position;
            gx.FillRR(c, sl, 0, Tokens.C(4, 4, 10, 0.62f * a));
            float dy = (1 - a) * (L.Sheet.Height + 40);
            c.Save();
            c.Translate(0, dy);
            var sheet = L.Sheet;
            var body = st.Phone ? new SKRect(sheet.Left, sheet.Top, sheet.Right, sheet.Bottom + 48) : sheet;
            gx.FillRR(c, body, 28, Tokens.C(17, 17, 30, 1f));
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, body.Top), new SKPoint(0, body.Top + 160), new[] { Tokens.C(157, 107, 255, 0.10f), Tokens.C(157, 107, 255, 0f) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp))
                gx.FillRR(c, new SKRect(body.Left, body.Top, body.Right, body.Top + 160), 28, sh);
            gx.StrokeRR(c, body, 28, 1, Tokens.White(0.10f));
            float hcx = (sheet.Left + sheet.Right) / 2;
            gx.FillRR(c, new SKRect(hcx - Handle / 2, sheet.Top + 8, hcx + Handle / 2, sheet.Top + 12), 2, Tokens.White(0.22f));
            // header
            float tx = L.Panel.Left;
            gx.DrawText(c, "Enviar a la tele", tx, gx.Baseline(L.TitleTop, L.TitleSize * 1.3f, L.TitleSize, 800), L.TitleSize, 800, Tokens.Text, -L.TitleSize * 0.02f);
            string sub = _channel != null ? _channel.Name : "Elige un aparato y luego un canal";
            float subMax = L.SearchX - 30 - tx;
            gx.DrawText(c, gx.Ellipsize(sub ?? "", 13, 600, subMax), tx, gx.Baseline(L.SubTop, 18, 13, 600), 13, 600, Tokens.TextA(0.62f));
            if (Cast.Searching && Cast.Devices.Count > 0) _ctx.Fx.Spinner(c, L.SearchX, L.Close.Top + 22, 16, now, Tokens.Accent2);
            // close
            gx.FillRR(c, L.Close, 22, Tokens.White(0.06f));
            gx.DrawIcon(c, Icons.Close, (L.Close.Left + L.Close.Right) / 2, (L.Close.Top + L.Close.Bottom) / 2, 20, Tokens.Text, 2f);
            // content
            c.Save();
            c.ClipRect(L.Panel);
            if (L.HasNow) DrawNow(c, now, L, OnScreen(L, L.Now, scroll), scroll);
            if (L.HasLabel)
            {
                float ly = L.Panel.Top + L.LabelTop - scroll;
                gx.DrawText(c, "ENVIAR A OTRO APARATO", L.Panel.Left + 4, gx.Baseline(ly, 22, 12, 700), 12, 700, Tokens.TextA(0.50f), 0.8f);
            }
            foreach (var r in L.Rows) DrawRow(c, now, L, r, OnScreen(L, r.R, scroll));
            if (L.Empty) DrawEmpty(c, now, L, scroll);
            if (_pressShown != null && _press.Value(now) > 0.01f) DrawPressOver(c, now, dy);
            c.Restore();
            if (L.MaxScroll > 1f && _scroll.Active || _scrolling) DrawScrollBar(c, L, scroll);
            c.Restore();
        }

        private void DrawPressOver(SKCanvas c, double now, float dy)
        {
            var t = _pressShown;
            if (t == null) return;
            float a = _press.Value(now);
            _ctx.Gx.FillRR(c, TouchHit.Offset(t.Rect, 0, -dy), t.Radius, Tokens.White(0.12f * a));
        }

        private void DrawScrollBar(SKCanvas c, SheetLayout L, float scroll)
        {
            float trackH = L.Panel.Height, thumbH = Math.Max(36, trackH * trackH / (trackH + L.MaxScroll));
            float p = L.MaxScroll <= 0 ? 0 : Math.Max(0, Math.Min(1, scroll / L.MaxScroll));
            float ty = L.Panel.Top + (trackH - thumbH) * p;
            _ctx.Gx.FillRR(c, new SKRect(L.Panel.Right + 5, ty, L.Panel.Right + 8, ty + thumbH), 1.5f, Tokens.White(0.25f));
        }

        private void DrawNow(SKCanvas c, double now, SheetLayout L, SKRect card, float scroll)
        {
            var gx = _ctx.Gx;
            var v = View;
            var dev = v.Device;
            bool starting = v.Phase == CastPhase.Starting;
            gx.FillRR(c, card, 24, Tokens.C(63, 232, 207, 0.10f));
            gx.StrokeRR(c, card, 24, 1, Tokens.C(63, 232, 207, 0.45f));
            // icon, with a slow pulse while the device is playing
            var ic = new SKRect(card.Left + L.NowIcon.Left, card.Top + L.NowIcon.Top - L.Now.Top, card.Left + L.NowIcon.Right, card.Top + L.NowIcon.Bottom - L.Now.Top);
            float cx = (ic.Left + ic.Right) / 2, cy = (ic.Top + ic.Bottom) / 2;
            if (!starting) PulseRing(c, cx, cy, 22, now);
            gx.FillRR(c, ic, 22, Tokens.C(63, 232, 207, 0.18f));
            if (starting) _ctx.Fx.Spinner(c, cx, cy, 22, now, Tokens.Accent2);
            else gx.DrawIcon(c, dev.Kind == CastDeviceKind.Tv ? Icons.Tv : Icons.Cast, cx, cy, 22, Tokens.Accent, 1.8f);
            float tx = ic.Right + 14, tw = card.Right - 16 - tx;
            string l1 = starting ? "Conectando con " + dev.Name + "…" : "Enviando a " + dev.Name;
            string l2 = v.Channel != null ? v.Channel.Name : "Preparando el canal…";
            if (starting && v.Channel != null) l2 = v.Channel.Name;
            else if (v.Paused && v.Channel != null) l2 = "En pausa · " + l2;
            gx.DrawText(c, gx.Ellipsize(l1, 16, 800, tw), tx, gx.Baseline(cy - 22, 24, 16, 800), 16, 800, Tokens.Text, -0.2f);
            gx.DrawText(c, gx.Ellipsize(l2, 13, 600, tw), tx, gx.Baseline(cy + 2, 20, 13, 600), 13, 600, Tokens.TextA(0.70f));
            float dyCard = card.Top - L.Now.Top;
            if (L.NowHasPause)
            {
                var pr = TouchHit.Offset(L.NowPause, card.Left - L.Now.Left, dyCard);
                gx.FillRR(c, pr, 28, Tokens.Text);
                gx.DrawIcon(c, v.Paused ? Icons.Play : Icons.Pause, (pr.Left + pr.Right) / 2, (pr.Top + pr.Bottom) / 2, 26, Tokens.C(7, 7, 17), 2f);
            }
            if (L.NowHasVolume)
            {
                var vb = TouchHit.Offset(L.NowVol, card.Left - L.Now.Left, dyCard);
                DrawVolume(_ctx, c, vb, _slider.Shown(v.Volume), v.Volume >= 0 || _slider.Dragging, 1f);
            }
            var sr = TouchHit.Offset(L.NowStop, card.Left - L.Now.Left, dyCard);
            DrawStopButton(c, sr, starting ? "Cancelar" : "Dejar de enviar", 1f);
        }

        /// <summary>The "stop" button of the sending state: warm, so that it is not mistaken for the controls.</summary>
        public void DrawStopButton(SKCanvas c, SKRect r, string label, float alpha)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, r, 18, Tokens.WithAlpha(Tokens.Warn, 0.10f * alpha));
            gx.StrokeRR(c, r, 18, 1, Tokens.WithAlpha(Tokens.Warn, 0.50f * alpha));
            gx.DrawTextAligned(c, label, (r.Left + r.Right) / 2, gx.Baseline(r.Top, r.Height, 15, 700), 15, 700, Tokens.WithAlpha(Tokens.Text, alpha), 0.5f);
        }

        /// <summary>The volume row: a speaker and a track whose knob is the value. <paramref name="box"/> is the whole row; the track is <see cref="VolumeTrack"/>.</summary>
        public static void DrawVolume(UiContext ctx, SKCanvas c, SKRect box, int value, bool known, float alpha)
        {
            var gx = ctx.Gx;
            float cy = (box.Top + box.Bottom) / 2;
            gx.DrawIcon(c, Icons.Volume, box.Left + 11, cy, 22, Tokens.WithAlpha(Tokens.Text, (known ? 0.86f : 0.45f) * alpha), 1.8f);
            var tr = VolumeTrack(box);
            float frac = Math.Max(0, Math.Min(100, value)) / 100f, half = 2;
            float kx = tr.Left + tr.Width * frac;
            gx.FillRR(c, new SKRect(tr.Left, cy - half, tr.Right, cy + half), half, Tokens.White(0.14f * alpha));
            gx.FillRR(c, new SKRect(tr.Left, cy - half, Math.Max(tr.Left + 1, kx), cy + half), half, Tokens.WithAlpha(Tokens.Accent2, 0.75f * alpha));
            gx.FillCircle(c, kx, cy + 1.5f, 10, Tokens.C(0, 0, 0, 0.28f * alpha));
            gx.FillCircle(c, kx, cy, 9, Tokens.WithAlpha(Tokens.Text, (known ? 1f : 0.6f) * alpha));
        }

        /// <summary>Where the track of a volume row is: right of the speaker, with room for the knob at both ends.</summary>
        public static SKRect VolumeTrack(SKRect box) { return new SKRect(box.Left + 38, box.Top, box.Right - 6, box.Bottom); }

        private void PulseRing(SKCanvas c, float cx, float cy, float r, double now)
        {
            float ph = (float)((now % 1.8) / 1.8);
            _ctx.Gx.StrokeCircle(c, cx, cy, r + 2 + 8 * ph, 1.5f, Tokens.C(63, 232, 207, 0.40f * (1 - ph)));
        }

        private void DrawRow(SKCanvas c, double now, SheetLayout L, RowLayout row, SKRect r)
        {
            if (r.Bottom < L.Panel.Top - 4 || r.Top > L.Panel.Bottom + 4) return;
            var gx = _ctx.Gx;
            var d = row.Device;
            bool dim = row.Check != null && row.Check.Known && !row.Check.Compatible;
            float a = dim ? 0.40f : 1f;
            gx.FillRR(c, r, 20, Tokens.White(0.06f * (dim ? 0.7f : 1f)));
            gx.StrokeRR(c, r, 20, 1, Tokens.White(dim ? 0.07f : 0.10f));
            float cy = (r.Top + r.Bottom) / 2;
            var ic = new SKRect(r.Left + 14, cy - 22, r.Left + 14 + 44, cy + 22);
            gx.FillRR(c, ic, 22, dim ? Tokens.White(0.06f) : Tokens.C(63, 232, 207, 0.12f));
            gx.DrawIcon(c, d.Kind == CastDeviceKind.Tv ? Icons.Tv : Icons.Cast, (ic.Left + ic.Right) / 2, cy, 22, dim ? Tokens.TextA(0.45f) : Tokens.Accent, 1.8f);
            float tx = ic.Right + 14, tw = r.Right - 14 - 22 - 8 - tx;
            int lines = row.Lines == null ? 1 : Math.Max(1, row.Lines.Count);
            float blockH = 20 + 3 + 17 * lines, top = cy - blockH / 2;
            gx.DrawText(c, gx.Ellipsize(d.Name, 16, 700, tw), tx, gx.Baseline(top, 20, 16, 700), 16, 700, Tokens.WithAlpha(Tokens.Text, a));
            float sy = top + 23;
            if (row.Lines != null && dim)
            {
                for (int i = 0; i < row.Lines.Count; i++)
                    gx.DrawText(c, row.Lines[i], tx, gx.Baseline(sy + i * 17, 17, 13, 600), 13, 600, Tokens.WithAlpha(Tokens.Warn, 0.95f));
            }
            else if (row.Checking)
            {
                _ctx.Fx.Spinner(c, tx + 6, sy + 8.5f, 12, now, Tokens.Accent2);
                gx.DrawText(c, "Comprobando el canal…", tx + 20, gx.Baseline(sy, 17, 13, 600), 13, 600, Tokens.TextA(0.55f));
            }
            else gx.DrawText(c, KindLabel(d), tx, gx.Baseline(sy, 17, 13, 600), 13, 600, Tokens.TextA(0.55f));
            if (!dim) gx.DrawIcon(c, Icons.Chevron, r.Right - 14 - 11, cy, 22, Tokens.TextA(0.42f), 2f);
        }

        private void DrawEmpty(SKCanvas c, double now, SheetLayout L, float scroll)
        {
            var gx = _ctx.Gx;
            float top = L.Panel.Top + L.EmptyTop - scroll, cx = (L.Panel.Left + L.Panel.Right) / 2, w = L.Panel.Width - 40;
            bool searching = Cast.Searching;
            float y = top + 24;
            if (searching) _ctx.Fx.Spinner(c, cx, y + 16, 32, now, Tokens.Accent2);
            else gx.DrawIcon(c, Icons.Tv, cx, y + 16, 36, Tokens.TextA(0.35f), 1.6f);
            y += 48;
            string title = searching ? "Buscando aparatos…" : "No se ha encontrado ningún aparato";
            gx.DrawTextAligned(c, gx.Ellipsize(title, 16, 700, w), cx, gx.Baseline(y, 22, 16, 700), 16, 700, Tokens.Text, 0.5f);
            y += 28;
            var hint = gx.WrapText("Comprueba que estén encendidos y conectados a la misma red Wi-Fi.", 13, 600, w, 3);
            for (int i = 0; i < hint.Count; i++) gx.DrawTextAligned(c, hint[i], cx, gx.Baseline(y + i * 18, 18, 13, 600), 13, 600, Tokens.TextA(0.55f), 0.5f);
            if (L.HasRetry)
            {
                var rr = OnScreen(L, L.Retry, scroll);
                gx.FillRR(c, rr, 18, Tokens.C(63, 232, 207, 0.10f));
                gx.StrokeRR(c, rr, 18, 1, Tokens.C(63, 232, 207, 0.45f));
                gx.DrawTextAligned(c, "Buscar de nuevo", cx, gx.Baseline(rr.Top, rr.Height, 15, 700), 15, 700, Tokens.Text, 0.5f);
            }
        }

        // ---------------------------------------------------------------- the strip

        private void DrawPill(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            var v = View; var p = PillRect();
            bool armed = !v.IsActive && Armed != null;
            var dev = v.IsActive ? v.Device : Armed;
            if (dev == null) return;
            bool starting = v.Phase == CastPhase.Starting;
            gx.FillRR(c, new SKRect(p.Left, p.Top + 4, p.Right, p.Bottom + 4), 30, Tokens.C(0, 0, 0, 0.22f));
            gx.FillRR(c, p, 30, Tokens.C(20, 20, 34, 1f));
            gx.StrokeRR(c, p, 30, 1, Tokens.C(63, 232, 207, 0.45f));
            var ic = new SKRect(p.Left + 10, p.Top + 10, p.Left + 10 + 40, p.Top + 10 + 40);
            float cx = (ic.Left + ic.Right) / 2, cy = (ic.Top + ic.Bottom) / 2;
            if (v.Phase == CastPhase.Sending) PulseRing(c, cx, cy, 20, now);
            gx.FillRR(c, ic, 20, Tokens.C(63, 232, 207, 0.14f));
            if (starting) _ctx.Fx.Spinner(c, cx, cy, 20, now, Tokens.Accent2);
            else gx.DrawIcon(c, dev.Kind == CastDeviceKind.Tv ? Icons.Tv : Icons.Cast, cx, cy, 20, Tokens.Accent, 1.8f);
            SKRect pause, stop; PillButtons(p, v, out pause, out stop);
            float tx = ic.Right + 12, right = (PillHasPause(v) ? pause.Left : stop.Left) - 6, tw = right - tx;
            string l1 = armed ? "Enviar a " + dev.Name : starting ? "Conectando con " + dev.Name + "…" : "Enviando a " + dev.Name;
            string l2 = armed ? "Toca un canal de la lista" : v.Channel != null ? (v.Paused ? "En pausa · " : "") + v.Channel.Name : "Preparando el canal…";
            gx.DrawText(c, gx.Ellipsize(l1, 14, 800, tw), tx, gx.Baseline(p.Top + 9, 20, 14, 800), 14, 800, Tokens.Text);
            gx.DrawText(c, gx.Ellipsize(l2, 12, 600, tw), tx, gx.Baseline(p.Top + 31, 18, 12, 600), 12, 600, Tokens.TextA(0.70f));
            if (PillHasPause(v))
            {
                gx.FillRR(c, pause, 22, Tokens.White(0.08f));
                gx.DrawIcon(c, v.Paused ? Icons.Play : Icons.Pause, (pause.Left + pause.Right) / 2, (pause.Top + pause.Bottom) / 2, 22, Tokens.Text, 2f);
            }
            gx.FillRR(c, stop, 22, Tokens.White(0.08f));
            gx.DrawIcon(c, Icons.Close, (stop.Left + stop.Right) / 2, (stop.Top + stop.Bottom) / 2, 20, Tokens.Text, 2f);
            if (_pressShown != null && _press.Value(now) > 0.01f) DrawPressOver(c, now, 0);
        }

        // ---------------------------------------------------------------- the notice

        private SKRect ToastRect(double now)
        {
            var st = _ctx.Style; var gx = _ctx.Gx;
            float maxW = Math.Min(st.W - 32, 440);
            var lines = gx.WrapText(_toast, 14, 700, maxW - 36, 2);
            float tw = 0;
            foreach (var l in lines) tw = Math.Max(tw, gx.MeasureText(l, 14, 700));
            float w = Math.Min(maxW, tw + 36), h = 18 + lines.Count * 20 + 18 - 4;
            float cx = st.SideRail ? (st.Main.Left + st.W) / 2 : st.W / 2;
            // above what sits at the bottom: the open sheet, the strip, or the section bar (the player has none)
            float bottom = _open ? Lay(now).Sheet.Top - 12 : PillVisible ? PillRect().Top - 10 : OnPlayerScreen || st.SideRail ? st.H - 20 : st.NavBox.Top - 12;
            return new SKRect(cx - w / 2, bottom - h, cx + w / 2, bottom);
        }

        private void DrawToast(SKCanvas c, double now)
        {
            if (_toast.Length == 0) return;
            double age = now - _toastAt;
            if (age > ToastSeconds + 0.4) { _toast = ""; return; }
            float a = age < 0.2 ? (float)(age / 0.2) : age > ToastSeconds ? (float)Math.Max(0, 1 - (age - ToastSeconds) / 0.4) : 1f;
            if (a <= 0.01f) return;
            var gx = _ctx.Gx;
            var r = ToastRect(now);
            var lines = gx.WrapText(_toast, 14, 700, r.Width - 36, 2);
            float dy = (1 - a) * 10;
            gx.FillRR(c, new SKRect(r.Left, r.Top + dy + 3, r.Right, r.Bottom + dy + 3), 22, Tokens.C(0, 0, 0, 0.25f * a));
            gx.FillRR(c, new SKRect(r.Left, r.Top + dy, r.Right, r.Bottom + dy), 22, Tokens.C(12, 13, 22, 0.97f * a));
            gx.StrokeRR(c, new SKRect(r.Left, r.Top + dy, r.Right, r.Bottom + dy), 22, 1, Tokens.WithAlpha(_toastWarn ? Tokens.Warn : Tokens.Accent, 0.60f * a));
            for (int i = 0; i < lines.Count; i++)
                gx.DrawTextAligned(c, lines[i], (r.Left + r.Right) / 2, gx.Baseline(r.Top + dy + 9 + i * 20, 20, 14, 700), 14, 700, Tokens.WithAlpha(Tokens.Text, a), 0.5f);
        }
    }
}
