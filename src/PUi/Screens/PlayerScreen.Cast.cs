using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Live;
using AceEngine.Util;
using PUi.Core;
using PUi.Gfx;
using PUi.Input;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// The player while the channel is being sent to another screen (phone and tablet only). The phone stops playing it: the picture area shows "Enviando a ..." and, below, the channel, pause
    /// and resume (if the device can), the volume (if it can), "Dejar de enviar" and the other channels of the list (a tap sends that one to the same device). Back leaves for the lists and the
    /// sending goes on (the strip over the lists shows it). When the sending ends, by the user or by itself, the player is a player again: it opens the channel here, live. Not a mode of
    /// the controller: nothing of the local playback exists meanwhile.
    /// </summary>
    public sealed partial class PlayerScreen
    {
        private bool _castMode;
        private Task _runTask;
        private readonly CastVolumeSlider _vol = new CastVolumeSlider();
        private bool _volDrag;

        /// <summary>The channel is being sent: the screen shows the sending state and plays nothing (for the tests).</summary>
        public bool CastMode { get { return _castMode; } }

        private bool CastActiveNow { get { return _ctx != null && _ctx.Compact && _ctx.Cast.IsAvailable && _ctx.CastUi.IsCasting; } }

        // ---------------------------------------------------------------- what the cast interface asks of the player
        Channel ICastLocalPlayer.Channel { get { return _pc.Current; } }

        LiveSession ICastLocalPlayer.OpenSession { get { var s = _pb as PlaybackSession; return s == null ? null : s.EngineSession as LiveSession; } }

        Task ICastLocalPlayer.ReleaseAsync() { return ReleaseForCastAsync(); }

        private void RegisterCastLocal()
        {
            if (_ctx != null && _ctx.Compact && _ctx.Cast.IsAvailable) _ctx.CastUi.Local = this;
        }

        private void UnregisterCastLocal()
        {
            if (_ctx != null && _ctx.HasCastUi && ReferenceEquals(_ctx.CastUi.Local, this)) _ctx.CastUi.Local = null;
        }

        /// <summary>Called from the cast task: stops the local playback on the interface thread and completes when the engine has released the channel's session.</summary>
        private async Task ReleaseForCastAsync()
        {
            var posted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ctx.Nav.Post(() => { try { posted.TrySetResult(EnterCastMode()); } catch (Exception ex) { posted.TrySetException(ex); } });
            var run = await posted.Task.ConfigureAwait(false);
            if (run != null) await Task.WhenAny(run, Task.Delay(8000)).ConfigureAwait(false);
        }

        /// <summary>The playback is stopped and the screen shows the sending state. Returns the task of the playback that was running (it ends when its engine session is released), or null if there was none.</summary>
        private Task EnterCastMode()
        {
            var run = _pb != null ? _runTask : null;
            _castMode = true;
            _ctl.Reset(); _panel.Snap(1); _panelShown = true; _drewEmpty = false; _drewStatic = false; _veil.Snap(0); _scrim.Snap(0);
            _volDrag = false; _vol.Abort();
            StopPlayback();
            if (_fullscreen) ExitFullscreen();                       // nothing to see in full screen: the phone goes back to its page
            SetKeepScreenOn(false);                                  // the phone does not play anything: it may sleep (the sending goes on)
            AceLog.Info("ui", "player: the channel is being sent to another screen");
            _ctx.Nav.Invalidate();
            return run;
        }

        private void UpdateCast(double now)
        {
            if (_ctx == null || !_ctx.Cast.IsAvailable)
            {
                if (_castMode) { _castMode = false; if (!_dead && _pc.Current != null) StartChannel(); }
                return;
            }
            var cu = _ctx.CastUi;
            if (!ReferenceEquals(cu.Local, this)) cu.Local = this;
            bool casting = cu.IsCasting;
            if (casting && !_castMode) EnterCastMode();
            else if (!casting && _castMode)
            {
                // the sending ended (the user stopped it, the device did, the channel went down, it could not start): this is a player again
                _castMode = false; _volDrag = false; _vol.Abort();
                AceLog.Info("ui", "player: the sending ended, playing here again");
                if (!_dead && _pc.Current != null) { SetKeepScreenOn(true); StartChannel(); }
            }
            if (_castMode)
            {
                var v = cu.View;
                if (v.Channel != null && _pc.Current != null && v.Channel.Id != _pc.Current.Id)
                    for (int i = 0; i < _pc.List.Count; i++) if (_pc.List[i].Id == v.Channel.Id) { _pc.MoveTo(i); break; }
            }
        }

        private FrameDemand CastFrameDemand(double now)
        {
            var v = _ctx.CastUi.View;
            if (_press.Active(now) || _volDrag) return FrameDemand.Full;
            if (v.Phase == CastPhase.Starting) return FrameDemand.Ambient;       // the spinner
            return FrameDemand.Decor;                                // the pulse around the icon
        }

        private void OnCastKey(KeyPress key)
        {
            switch (key.Key)
            {
                case UiKey.Back: Perform(PlayerAction.Exit); break;
                case UiKey.PlayPause: case UiKey.Play: case UiKey.Pause: _ctx.CastUi.TogglePause(); break;
                // a keyboard or a remote on a tablet: another channel of the list goes to the same device
                case UiKey.ChannelUp: SendNeighbour(1); break;
                case UiKey.ChannelDown: SendNeighbour(-1); break;
            }
        }

        private void SendNeighbour(int delta)
        {
            var v = _ctx.CastUi.View;
            if (v.Device != null && _pc.Move(delta)) _ctx.CastUi.Send(v.Device, _pc.Current);
        }

        // ---------------------------------------------------------------- geometry
        private struct CastG
        {
            public bool Page, HasPause, HasVol;
            public SKRect Video, Back, Pause, Vol, Stop;
            public float X0, X1, TitleY, TitleSize, BadgeY, Cx, DiscCy, DiscR, NameSize;
            public bool Short;
            public float LabelY, NameY, ChanY;
            public SKRect[] More; public int[] MoreIndex;
        }

        private CastG CastGeo(CastStatus v)
        {
            var st = _ctx.Style;
            var g = new CastG();
            float W = st.W, H = st.H;
            bool sending = v.Phase == CastPhase.Sending, dev = v.Device != null;
            g.HasPause = sending && dev && v.Device.CanPause;
            g.HasVol = sending && dev && v.Device.CanVolume;
            g.More = new SKRect[0]; g.MoreIndex = new int[0];
            if (PageLayout)
            {
                g.Page = true;
                float vh = st.VideoHeight;
                g.Video = new SKRect(0, 0, W, vh);
                float bb = st.Phone ? 44 : 48;
                g.Back = new SKRect(6, 6, 6 + bb, 6 + bb);
                float cw = st.Phone ? W - 40 : Math.Min(W - 40, 560);
                g.X0 = (W - cw) / 2; g.X1 = g.X0 + cw; g.Cx = W / 2;
                g.TitleSize = st.Phone ? 22 : 26;
                g.TitleY = vh + 20;
                g.BadgeY = g.TitleY + g.TitleSize * 1.35f + 8;
                g.DiscR = st.Phone ? 32 : 40;
                g.DiscCy = vh / 2 - (st.Phone ? 22 : 26);
                float y = g.BadgeY + 24 + 30;
                if (g.HasPause) { g.Pause = new SKRect(W / 2 - 38, y, W / 2 + 38, y + 76); y += 76 + 24; }
                if (g.HasVol) { g.Vol = new SKRect(g.X0, y, g.X1, y + 44); y += 44 + 20; }
                g.Stop = new SKRect(g.X0, y, g.X1, y + 52); y += 52 + 26;
                int fit = (int)Math.Floor((H - y - 4) / 64f), avail = Math.Max(0, _pc.List.Count - 1);
                int n = Math.Max(0, Math.Min(Math.Min(fit, 6), avail));
                g.More = new SKRect[n]; g.MoreIndex = new int[n];
                for (int i = 0; i < n; i++) { g.More[i] = new SKRect(g.X0 - 4, y + i * 64, g.X1 + 4, y + i * 64 + 56); g.MoreIndex[i] = (_pc.Index + 1 + i) % _pc.List.Count; }
                return g;
            }
            // landscape: the whole surface. A tablet gets a centred column; a short surface (a phone on its side: about 380 units tall) two columns, the device at the left and the controls at the right
            g.Page = false;
            g.Video = _ctx.SurfaceLogical;
            g.Back = new SKRect(24, 16, 72, 64);
            g.Short = H < 540;
            g.NameSize = g.Short ? 28 : 34;
            if (g.Short)
            {
                float lx = W * 0.30f, rx = W * 0.70f;
                g.Cx = lx; g.DiscR = 44; g.DiscCy = H / 2 - 46;
                g.LabelY = g.DiscCy + 44 + 14; g.NameY = g.LabelY + 24; g.ChanY = g.NameY + 40;
                float tot = (g.HasPause ? 76 + 16 : 0) + (g.HasVol ? 44 + 14 : 0) + 52, yy2 = H / 2 - tot / 2;
                if (g.HasPause) { g.Pause = new SKRect(rx - 38, yy2, rx + 38, yy2 + 76); yy2 += 76 + 16; }
                if (g.HasVol) { g.Vol = new SKRect(rx - 150, yy2, rx + 150, yy2 + 44); yy2 += 44 + 14; }
                g.Stop = new SKRect(rx - 150, yy2, rx + 150, yy2 + 52);
                g.X0 = rx - 150; g.X1 = rx + 150;
                return g;
            }
            g.Cx = W / 2; g.DiscR = 52;
            float total = 104 + 22 + 18 + 6 + 44 + 4 + 28 + 30 + (g.HasPause ? 84 + 22 : 0) + (g.HasVol ? 44 + 18 : 0) + 52;
            float top = Math.Max(84, H / 2 - total / 2);
            g.DiscCy = top + 52;
            float yy = top + 104 + 22;
            g.LabelY = yy; yy += 18 + 6;
            g.NameY = yy; yy += 44 + 4;
            g.ChanY = yy; yy += 28 + 30;
            if (g.HasPause) { g.Pause = new SKRect(W / 2 - 42, yy, W / 2 + 42, yy + 84); yy += 84 + 22; }
            if (g.HasVol) { g.Vol = new SKRect(W / 2 - 190, yy, W / 2 + 190, yy + 44); yy += 44 + 18; }
            g.Stop = new SKRect(W / 2 - 150, yy, W / 2 + 150, yy + 52);
            g.X0 = W / 2 - 190; g.X1 = W / 2 + 190;
            return g;
        }

        private void CollectCastTargets(List<TouchTarget> into)
        {
            var v = _ctx.CastUi.View;
            var g = CastGeo(v);
            into.Add(new TouchTarget { Id = "back", Rect = g.Back, Radius = g.Back.Height / 2, Layer = 1, Tap = () => BackPressed() });
            if (g.HasPause) into.Add(new TouchTarget { Id = "cast:pause", Rect = g.Pause, Radius = g.Pause.Height / 2, Layer = 1, Tap = () => _ctx.CastUi.TogglePause() });
            if (g.HasVol) into.Add(new TouchTarget { Id = "cast:volume", Rect = g.Vol, Radius = 22, Layer = 1, Press = false });
            into.Add(new TouchTarget { Id = "cast:stop", Rect = g.Stop, Radius = 18, Layer = 1, Tap = () => _ctx.CastUi.StopSending() });
            for (int i = 0; i < g.More.Length; i++)
            {
                int idx = g.MoreIndex[i];
                into.Add(new TouchTarget { Id = "more" + i, Rect = g.More[i], Radius = 18, Layer = 1, Tap = () => { if (_pc.MoveTo(idx) && v.Device != null) _ctx.CastUi.Send(v.Device, _pc.Current); } });
            }
        }

        // ---------------------------------------------------------------- drawing
        private string CastTitle(CastStatus v) { return v.Channel != null ? v.Channel.Name : _pc.Current != null ? _pc.Current.Name : ""; }

        private void DrawCastCompact(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            var v = _ctx.CastUi.View;
            var g = CastGeo(v);
            if (v.Device == null) { _ctx.DrawBackdrop(c, now); return; }
            bool starting = v.Phase == CastPhase.Starting;
            _ctx.DrawBackdrop(c, now);
            if (g.Page)
            {
                DrawCastCard(c, g, v, now);
                gx.FillRR(c, g.Back, g.Back.Height / 2, Tokens.C(7, 7, 17, 0.45f));
                gx.DrawIcon(c, Icons.Back, (g.Back.Left + g.Back.Right) / 2, (g.Back.Top + g.Back.Bottom) / 2, 22, Tokens.Text, 2f);
                gx.DrawText(c, gx.Ellipsize(CastTitle(v), g.TitleSize, 800, g.X1 - g.X0), g.X0, gx.Baseline(g.TitleY, g.TitleSize * 1.35f, g.TitleSize, 800), g.TitleSize, 800, Tokens.Text, -g.TitleSize * 0.02f);
                // the state chip and what kind of screen it is
                string chip = starting ? "CONECTANDO" : v.Paused ? "EN PAUSA" : "ENVIANDO";
                float cwid = gx.MeasureText(chip, 13, 800, 0.52f) + 18;
                var cr = new SKRect(g.X0, g.BadgeY, g.X0 + cwid, g.BadgeY + 24);
                var tint = starting ? Tokens.Accent2 : Tokens.Accent;
                gx.FillRR(c, cr, 8, Tokens.WithAlpha(tint, 0.16f));
                gx.DrawTextAligned(c, chip, (cr.Left + cr.Right) / 2, gx.Baseline(cr.Top, cr.Height, 13, 800), 13, 800, tint, 0.5f, 0.52f);
                float lh = gx.LineHeight(13, 600);
                gx.DrawText(c, v.Device.Kind == CastDeviceKind.Tv ? "Televisor" : "Chromecast", cr.Right + 12, gx.Baseline(cr.Top + 12 - lh / 2, lh, 13, 600), 13, 600, Tokens.TextA(0.70f));
            }
            else
            {
                gx.FillRR(c, _ctx.SurfaceLogical, 0, Tokens.C(5, 6, 12, 0.35f));
                gx.FillRR(c, g.Back, 24, Tokens.White(0.10f));
                gx.DrawIcon(c, Icons.Back, (g.Back.Left + g.Back.Right) / 2, (g.Back.Top + g.Back.Bottom) / 2, 24, Tokens.Text, 2f);
                DrawCastDisc(c, g.Cx, g.DiscCy, g.DiscR, v, now);
                gx.DrawTextAligned(c, starting ? "CONECTANDO CON" : "ENVIANDO A", g.Cx, gx.Baseline(g.LabelY, 18, 13, 700), 13, 700, Tokens.TextA(0.60f), 0.5f, 1.0f);
                float tw = g.Short ? _ctx.Style.W * 0.40f : 640;
                gx.DrawTextAligned(c, gx.Ellipsize(v.Device.Name, g.NameSize, 800, tw), g.Cx, gx.Baseline(g.NameY, g.NameSize * 1.3f, g.NameSize, 800), g.NameSize, 800, Tokens.Text, 0.5f, -0.5f);
                gx.DrawTextAligned(c, gx.Ellipsize((v.Paused ? "En pausa · " : "") + CastTitle(v), g.Short ? 17 : 20, 700, tw), g.Cx, gx.Baseline(g.ChanY, 28, g.Short ? 17 : 20, 700), g.Short ? 17 : 20, 700, Tokens.TextA(0.78f), 0.5f);
            }
            if (g.HasPause)
            {
                gx.FillRR(c, g.Pause, g.Pause.Height / 2, Tokens.Text);
                gx.DrawIcon(c, v.Paused ? Icons.Play : Icons.Pause, (g.Pause.Left + g.Pause.Right) / 2, (g.Pause.Top + g.Pause.Bottom) / 2, g.Page ? 28 : 32, Tokens.C(7, 7, 17), 2f);
            }
            if (g.HasVol) CastUi.DrawVolume(_ctx, c, g.Vol, _vol.Shown(v.Volume), v.Volume >= 0 || _vol.Dragging, 1f);
            _ctx.CastUi.DrawStopButton(c, g.Stop, starting ? "Cancelar" : "Dejar de enviar", 1f);
            for (int i = 0; i < g.More.Length; i++) DrawMoreRow(c, g.More[i], _pc.List[g.MoreIndex[i]], 1f, now);
            DrawPress(c, now);
            _drewStatic = true;
        }

        /// <summary>The picture area of the page while sending: where the video would be, a dark panel with the device.</summary>
        private void DrawCastCard(SKCanvas c, CastG g, CastStatus v, double now)
        {
            var gx = _ctx.Gx;
            var r = g.Video;
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), new[] { SKColor.Parse("#12303a"), SKColor.Parse("#0d1424"), SKColor.Parse("#1b1640") }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp))
                gx.FillRR(c, r, 0, sh);
            bool starting = v.Phase == CastPhase.Starting;
            DrawCastDisc(c, g.Cx, g.DiscCy, g.DiscR, v, now);
            float top = g.DiscCy + g.DiscR + (_ctx.Style.Phone ? 12 : 14);
            gx.DrawTextAligned(c, starting ? "CONECTANDO CON" : "ENVIANDO A", g.Cx, gx.Baseline(top, 16, 12, 700), 12, 700, Tokens.TextA(0.60f), 0.5f, 0.9f);
            float ns = _ctx.Style.Phone ? 20 : 24;
            gx.DrawTextAligned(c, gx.Ellipsize(v.Device.Name, ns, 800, r.Width - 48), g.Cx, gx.Baseline(top + 18, ns * 1.3f, ns, 800), ns, 800, Tokens.Text, 0.5f, -ns * 0.02f);
        }

        /// <summary>The device's icon in a disc: pulsing while it plays, a spinner while it connects.</summary>
        private void DrawCastDisc(SKCanvas c, float cx, float cy, float r, CastStatus v, double now)
        {
            var gx = _ctx.Gx;
            bool starting = v.Phase == CastPhase.Starting;
            if (!starting && !v.Paused)
            {
                float ph = (float)((now % 1.8) / 1.8);
                gx.StrokeCircle(c, cx, cy, r + 4 + r * 0.45f * ph, 1.5f, Tokens.C(63, 232, 207, 0.40f * (1 - ph)));
            }
            gx.FillCircle(c, cx, cy, r, Tokens.C(63, 232, 207, 0.14f));
            gx.StrokeCircle(c, cx, cy, r - 0.5f, 1, Tokens.C(63, 232, 207, 0.45f));
            if (starting) _ctx.Fx.Spinner(c, cx, cy, r * 0.9f, now, Tokens.Accent2);
            else gx.DrawIcon(c, v.Device != null && v.Device.Kind == CastDeviceKind.Tv ? Icons.Tv : Icons.Cast, cx, cy, r * 0.95f, Tokens.Accent, 1.8f);
        }
    }
}
