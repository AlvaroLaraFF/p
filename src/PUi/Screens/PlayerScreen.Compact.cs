using System;
using System.Collections.Generic;
using System.Globalization;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Util;
using PUi.Core;
using PUi.Gfx;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// The player on a phone or a tablet (design PhonePlayer, PhonePlayerLoading and TabletPlayer). Two layouts, chosen by the surface:
    ///  - PAGE (portrait): the video is a 16:9 area at the top of the screen, and below it, always on screen, the channel's information, the memory bar, pause and +-10 s, the actions (full screen,
    ///    send to the TV) and more channels of the same list. The interface is opaque everywhere except the video area, which is left transparent for the video behind it (IVideoPlacement).
    ///  - OVERLAY (landscape): the video fills the whole screen and the controls sit over it, shown and hidden by a tap like the TV's panel. A phone gets this layout only while the player is full
    ///    screen: "Pantalla completa" asks the platform for landscape (IOrientationControl) and the arrow (or Back) asks to leave it.
    /// The loading lamp, the buffer ring, the pause veil, the live/behind logic and the player controller are the TV's.
    /// </summary>
    public sealed partial class PlayerScreen
    {
        private bool _fullscreen;
        private PixelRect? _videoRect;

        /// <summary>The user pressed "Pantalla completa": the screen was asked to turn landscape and leaving the player's full screen must ask for the orientation back (for the tests).</summary>
        public bool IsFullscreen { get { return _fullscreen; } }
        /// <summary>The video rectangle the player last asked the platform for, in surface pixels (null: the whole surface or nothing asked yet). For the tests.</summary>
        public PixelRect? VideoPixels { get { return _videoRect; } }

        private bool Compact { get { return _ctx != null && _ctx.Compact; } }
        /// <summary>The portrait layout: video above, information and controls below.</summary>
        public bool PageLayout { get { return _ctx != null && _ctx.Compact && _ctx.Kind != DeviceKind.TabletLandscape; } }
        /// <summary>The landscape layout: the video fills the screen and the controls are over it.</summary>
        public bool OverlayLayout { get { return _ctx != null && _ctx.Compact && _ctx.Kind == DeviceKind.TabletLandscape; } }

        // ---------------------------------------------------------------- full screen and orientation
        private void EnterFullscreen()
        {
            if (_fullscreen) return;
            _fullscreen = true;
            _ctl.ShowPanel(_ctx.Now);                                // the controls are up when the picture turns
            var o = _ctx.OrientationControl;
            if (o != null) { try { o.Request(OrientationRequest.Landscape); } catch (Exception ex) { AceLog.Warn("ui", "orientation: " + ex.Message); } }
            _ctx.Nav.Invalidate();
        }

        private void ExitFullscreen()
        {
            if (!_fullscreen) return;
            _fullscreen = false;
            var o = _ctx.OrientationControl;
            if (o != null) { try { o.Request(_ctx.Surface.IsPhone ? OrientationRequest.Portrait : OrientationRequest.Device); } catch (Exception ex) { AceLog.Warn("ui", "orientation: " + ex.Message); } }
            _ctx.Nav.Invalidate();
        }

        /// <summary>On leaving the player: full screen is undone (the orientation goes back) and the video has the whole surface again.</summary>
        private void LeaveCompact()
        {
            if (_ctx == null || !_ctx.Compact) return;
            ExitFullscreen();
            if (_videoRect.HasValue)
            {
                _videoRect = null;
                var v = _ctx.VideoPlacement;
                if (v != null) { try { v.ClearVideoRect(); } catch (Exception ex) { AceLog.Warn("ui", "video rect: " + ex.Message); } }
            }
        }

        /// <summary>Tells the platform where the video goes, whenever it changes (rotation, full screen).</summary>
        private void UpdateVideoRect()
        {
            if (!Compact) return;
            var g = Cg();
            var m = _ctx.Surface;
            PixelRect r = g.Page ? m.ToPixels(g.Video.Left, g.Video.Top, g.Video.Right, g.Video.Bottom) : new PixelRect(0, 0, m.PixelWidth, m.PixelHeight);
            if (_videoRect.HasValue && _videoRect.Value.Equals(r)) return;
            _videoRect = r;
            var v = _ctx.VideoPlacement;
            if (v != null) { try { v.SetVideoRect(r); } catch (Exception ex) { AceLog.Warn("ui", "video rect: " + ex.Message); } }
        }

        // ---------------------------------------------------------------- geometry
        private struct CGeo
        {
            public bool Page;
            public SKRect Video, Back, Cast, Compress;
            public float X0, X1, TitleX, TitleY, TitleSize, BadgeY;
            public SKRect Bar; public float BarCy, TrackH, KnobR, LabelTop;
            public SKRect Back10, Play, Fwd10, Live;
            public SKRect ActFull, ActCast; public bool HasCast, HasFull;
            public SKRect[] More; public int[] MoreIndex;
        }

        private CGeo Cg()
        {
            var st = _ctx.Style; var gx = _ctx.Gx;
            var g = new CGeo();
            float W = st.W, H = st.H;
            g.HasCast = _ctx.Cast.IsAvailable;
            g.HasFull = _ctx.OrientationControl != null;          // without a way to turn the screen the button would do nothing: it is not shown
            if (PageLayout)
            {
                g.Page = true;
                float vh = st.VideoHeight;
                g.Video = new SKRect(0, 0, W, vh);
                float bb = st.Phone ? 44 : 48;
                g.Back = new SKRect(6, 6, 6 + bb, 6 + bb);
                float cw = st.Phone ? W - 40 : Math.Min(W - 40, 560);
                g.X0 = (W - cw) / 2; g.X1 = g.X0 + cw;
                g.TitleSize = st.Phone ? 22 : 26;
                g.TitleY = vh + 20;
                g.BadgeY = g.TitleY + g.TitleSize * 1.35f + 8;
                float barTop = g.BadgeY + 24 + 25;
                g.Bar = new SKRect(g.X0, barTop, g.X1, barTop + 24); g.BarCy = barTop + 12; g.TrackH = 4; g.KnobR = 9; g.LabelTop = barTop + 28;
                float cy = barTop + 96, total = 56 + 22 + 76 + 22 + 56, xs = (W - total) / 2;
                g.Back10 = new SKRect(xs, cy - 28, xs + 56, cy + 28);
                g.Play = new SKRect(xs + 78, cy - 38, xs + 78 + 76, cy + 38);
                g.Fwd10 = new SKRect(xs + 78 + 76 + 22, cy - 28, xs + 78 + 76 + 22 + 56, cy + 28);
                float lw = gx.MeasureText("EN DIRECTO", 13, 800, 0.52f) + 18;
                g.Live = new SKRect(g.X0, g.BadgeY, g.X0 + lw, g.BadgeY + 24);
                float aTop = cy + 38 + 26;
                bool anyAction = g.HasCast || g.HasFull;
                if (g.HasCast && g.HasFull)
                {
                    float half = (cw - 10) / 2;
                    g.ActCast = new SKRect(g.X0, aTop, g.X0 + half, aTop + 52);
                    g.ActFull = new SKRect(g.X0 + half + 10, aTop, g.X1, aTop + 52);
                }
                else if (g.HasCast) g.ActCast = new SKRect(g.X0, aTop, g.X1, aTop + 52);
                else if (g.HasFull) g.ActFull = new SKRect(g.X0, aTop, g.X1, aTop + 52);
                float mTop = anyAction ? aTop + 52 + 26 : aTop;
                int fit = (int)Math.Floor((H - mTop - 4) / 64f), avail = Math.Max(0, _pc.List.Count - 1);
                int n = Math.Max(0, Math.Min(Math.Min(fit, 6), avail));
                g.More = new SKRect[n]; g.MoreIndex = new int[n];
                for (int i = 0; i < n; i++) { g.More[i] = new SKRect(g.X0 - 4, mTop + i * 64, g.X1 + 4, mTop + i * 64 + 56); g.MoreIndex[i] = ((_pc.Index + 1 + i) % _pc.List.Count); }
                return g;
            }
            g.Page = false;
            g.Video = _ctx.SurfaceLogical;
            g.Back = new SKRect(24, 16, 72, 64);
            g.TitleX = 84; g.TitleSize = 24;
            float right = W - 24;
            if (g.HasCast)
            {
                float cwid = gx.MeasureText("Enviar a la tele", 15, 700) + 18 + 20 + 10 + 18;
                g.Cast = new SKRect(right - cwid, 16, right, 64); right -= cwid + 12;
            }
            if (_fullscreen) g.Compress = new SKRect(right - 48, 16, right, 64);
            g.X0 = 40; g.X1 = W - 40;
            float barBottom = H - 28 - 80 - 18;
            g.Bar = new SKRect(g.X0, barBottom - 28, g.X1, barBottom); g.BarCy = barBottom - 14; g.TrackH = 5; g.KnobR = 11;
            float ccy = H - 28 - 40, chipW = gx.MeasureText("EN DIRECTO", 13, 800, 0.78f) + 24;
            float tot = 60 + 28 + 80 + 28 + 60 + 28 + chipW, x = (W - tot) / 2;
            g.Back10 = new SKRect(x, ccy - 30, x + 60, ccy + 30); x += 88;
            g.Play = new SKRect(x, ccy - 40, x + 80, ccy + 40); x += 108;
            g.Fwd10 = new SKRect(x, ccy - 30, x + 60, ccy + 30); x += 88;
            g.Live = new SKRect(x, ccy - 15, x + chipW, ccy + 15);
            g.More = new SKRect[0]; g.MoreIndex = new int[0];
            return g;
        }

        // ---------------------------------------------------------------- touch
        private void CollectCompactTargets(List<TouchTarget> into, double now)
        {
            if (_castMode) { CollectCastTargets(into); return; }
            var g = Cg();
            var st = _ctx.Style;
            var mode = _ctl.Mode;
            // the arrow is always there: while loading, on the error screen and over the video. In full screen it leaves full screen, otherwise the player.
            if (g.Page || _panelShown || mode == PlayerMode.Loading || mode == PlayerMode.Error)
                into.Add(new TouchTarget { Id = "back", Rect = g.Back, Radius = g.Back.Height / 2, Layer = 1, Tap = () => BackPressed() });
            if (mode == PlayerMode.Loading) return;
            if (mode == PlayerMode.Error && !_ctl.Started) return;
            if (!g.Page && !_panelShown) return;
            // over the video the controls' band takes the touches on its empty parts (they only keep it on screen), like the TV's panel
            if (!g.Page) into.Add(new TouchTarget { Id = "panel", Rect = new SKRect(0, g.Bar.Top - 16, st.W, st.H), Press = false, Tap = () => _ctl.ShowPanel(_ctx.Now) });
            if (g.Page == false && _fullscreen) into.Add(new TouchTarget { Id = "compress", Rect = g.Compress, Radius = 24, Layer = 1, Tap = () => ExitFullscreen() });
            if (g.HasCast)
            {
                var cast = g.Page ? g.ActCast : g.Cast;
                into.Add(new TouchTarget { Id = "cast", Rect = cast, Radius = g.Page ? 18 : 24, Layer = 1, Tap = () => _ctx.CastUi.Open(_pc.Current) });
            }
            if (CanScrub)
                into.Add(new TouchTarget { Id = "timeline", Rect = new SKRect(g.X0 - 14, g.BarCy - 22, g.X1 + 14, g.BarCy + 22), Radius = 17, Layer = 1, Press = false });
            into.Add(Button("back10", g.Back10, g.Back10.Height / 2, PanelButton.Back10));
            into.Add(Button("play", g.Play, g.Play.Height / 2, PanelButton.PlayPause));
            into.Add(Button("forward10", g.Fwd10, g.Fwd10.Height / 2, PanelButton.Forward10));
            // the badge is drawn small (24 or 30 tall): its target is the 44 a finger needs
            float lcy = (g.Live.Top + g.Live.Bottom) / 2;
            into.Add(Button("live", new SKRect(g.Live.Left, lcy - 22, g.Live.Right, lcy + 22), 22, PanelButton.Live));
            if (g.Page)
            {
                if (g.HasFull) into.Add(new TouchTarget { Id = "fullscreen", Rect = g.ActFull, Radius = 18, Layer = 1, Tap = () => EnterFullscreen() });
                for (int i = 0; i < g.More.Length; i++)
                {
                    int idx = g.MoreIndex[i];
                    into.Add(new TouchTarget { Id = "more" + i, Rect = g.More[i], Radius = 18, Layer = 1, Tap = () => { if (_pc.MoveTo(idx)) StartChannel(); } });
                }
            }
        }

        private void BackPressed()
        {
            if (_fullscreen) ExitFullscreen();
            else Perform(PlayerAction.Exit);
        }

        // ---------------------------------------------------------------- frame
        private bool PageWanted(PlayerMode mode) { return mode != PlayerMode.Loading; }

        // ---------------------------------------------------------------- drawing
        private void DrawCompact(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            var g = Cg();
            var mode = _ctl.Mode;
            float p = _panel.Value(now);
            float scrim = _scrim.Value(now), veil = _veil.Value(now);
            if (g.Page)
            {
                _ctx.DrawBackdrop(c, now);
                DrawVideoArea(c, g.Video);
                if (scrim > 0.01f) gx.FillRR(c, g.Video, 0, Tokens.C(5, 6, 12, 0.28f * scrim));
                if (veil > 0.01f)
                {
                    gx.FillRR(c, g.Video, 0, Tokens.C(5, 6, 12, 0.10f * veil));
                    c.Save(); c.ClipRect(g.Video);
                    DrawBufferRingIn(c, now, veil, g.Video, 36, 10, 15, 12);
                    c.Restore();
                }
                if (p > 0.01f) DrawPageHud(c, g, now, p);
            }
            else
            {
                c.Clear(SKColors.Transparent);
                if (MockVideo) DrawMockVideoIn(c, _ctx.SurfaceLogical, 14);
                else if (mode == PlayerMode.Error) c.Clear(new SKColor(5, 6, 12, 255));
                var sl = _ctx.SurfaceLogical;
                if (scrim > 0.01f) gx.FillRR(c, sl, 0, Tokens.C(5, 6, 12, 0.28f * scrim));
                if (veil > 0.01f)
                {
                    gx.FillRR(c, sl, 0, Tokens.C(5, 6, 12, 0.10f * veil));
                    DrawBufferRingIn(c, now, veil, new SKRect(0, 0, _ctx.CW, _ctx.CH), 52, 14, 20, 14);
                }
                if (p > 0.01f) DrawOverlayHud(c, g, now, p);
            }
            DrawPress(c, now);
            if (mode == PlayerMode.Error) DrawErrorCard(c, now);
            _drewEmpty = g.Page ? false : p <= 0.01f;
            _drewStatic = true;
        }

        /// <summary>The video's area on the page: transparent (the video behind shows), or a dark fill where there is none; on the bench the fake video.</summary>
        private void DrawVideoArea(SKCanvas c, SKRect v)
        {
            var gx = _ctx.Gx;
            if (MockVideo) { DrawMockVideoIn(c, v, 12); return; }
            if (_ctl.WindowTransparent)
            {
                c.Save(); c.ClipRect(v); c.DrawColor(SKColors.Transparent, SKBlendMode.Src); c.Restore();
            }
            else gx.FillRR(c, v, 0, new SKColor(5, 6, 12, 255));
        }

        /// <summary>The bench's fake video: the design's dark gradient with the label, inside a rectangle.</summary>
        private void DrawMockVideoIn(SKCanvas c, SKRect v, float labelSize)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, v, 0, SKColors.Black);
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(v.Left, v.Top), new SKPoint(v.Right, v.Bottom), new[] { SKColor.Parse("#1a2a3a"), SKColor.Parse("#0e1a24"), SKColor.Parse("#203b2c") }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
                gx.FillRR(c, v, 0, sh);
            string t = "VÍDEO EN DIRECTO";
            gx.DrawTextAligned(c, t, (v.Left + v.Right) / 2, gx.Baseline((v.Top + v.Bottom) / 2 - 12, 24, labelSize, 700), labelSize, 700, Tokens.TextA(0.35f), 0.5f, labelSize * 0.12f);
        }

        // ---------------------------------------------------------------- the page (portrait)
        private void DrawPageHud(SKCanvas c, CGeo g, double now, float p)
        {
            var gx = _ctx.Gx;
            var st = _ctl.Status;
            bool atLive = _ctl.AtLive(now);
            // back arrow over the video
            gx.FillRR(c, g.Back, g.Back.Height / 2, Tokens.C(7, 7, 17, 0.45f * p));
            gx.DrawIcon(c, Icons.Back, (g.Back.Left + g.Back.Right) / 2, (g.Back.Top + g.Back.Bottom) / 2, 22, Tokens.WithAlpha(Tokens.Text, p), 2f);
            // channel: title, then the live badge, peers and speed
            string title = _pc.Current != null ? _pc.Current.Name : st.Name;
            gx.DrawText(c, gx.Ellipsize(title ?? "", g.TitleSize, 800, g.X1 - g.X0), g.X0, gx.Baseline(g.TitleY, g.TitleSize * 1.35f, g.TitleSize, 800), g.TitleSize, 800, Tokens.WithAlpha(Tokens.Text, p), -g.TitleSize * 0.02f);
            float x = DrawLiveBadge(c, g.Live, atLive, p, now);
            x += 12;
            float my = g.BadgeY + 12;
            string peers = st.Peers.ToString(CultureInfo.InvariantCulture), speed = PlaybackStatus.FormatSpeed(st.DownBytesPerSec);
            _ctx.Fx.PulseDot(c, x + 3.5f, my, 3.5f, now, 7);
            float lh = gx.LineHeight(13, 600);
            gx.DrawText(c, peers, x + 13, gx.Baseline(my - lh / 2, lh, 13, 600), 13, 600, Tokens.TextA(0.70f * p));
            x += 13 + gx.MeasureText(peers, 13, 600) + 12;
            gx.DrawText(c, speed, x, gx.Baseline(my - lh / 2, lh, 13, 600), 13, 600, Tokens.TextA(0.70f * p));
            // position
            DrawTimeline(c, g, now, p, atLive);
            float ly = g.LabelTop, ll = gx.LineHeight(12, 600);
            if (_ctl.WindowMs >= PlayerController.MinWindowMs) gx.DrawText(c, FormatBehind(_ctl.WindowMs), g.X0, gx.Baseline(ly, ll, 12, 600), 12, 600, Tokens.TextA(0.55f * p));
            gx.DrawTextAligned(c, atLive ? "en directo" : FormatBehind(_ctl.BehindMs(now)), g.X1, gx.Baseline(ly, ll, 12, 600), 12, 600, Tokens.TextA(0.55f * p), 1f);
            // pause and +-10
            bool paused = _ctl.Mode == PlayerMode.Paused;
            DrawStep(c, g.Back10, "−10", 15, PanelButton.Back10, p, now, 0.06f, 0.12f);
            DrawPlayButton(c, g.Play, paused, 28, p, now);
            DrawStep(c, g.Fwd10, "+10", 15, PanelButton.Forward10, p, now, 0.06f, 0.12f);
            // actions
            if (g.HasCast) DrawAction(c, g.ActCast, Icons.Cast, "Enviar a la tele", true, p);
            if (g.HasFull) DrawAction(c, g.ActFull, Icons.Expand, "Pantalla completa", false, p);
            // more channels of the list
            for (int i = 0; i < g.More.Length; i++) DrawMoreRow(c, g.More[i], _pc.List[g.MoreIndex[i]], p, now);
        }

        private void DrawMoreRow(SKCanvas c, SKRect r, Channel ch, float p, double now)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, r, 18, Tokens.White(0.05f * p));
            gx.StrokeRR(c, r, 18, 1, Tokens.White(0.09f * p));
            var cs = _ctx.Services.Checker.GetStatus(ch);
            string peers = cs.IsLive ? cs.PeersText : "";
            float right = r.Right - 16, pw = peers.Length > 0 ? 7 + 6 + gx.MeasureText(peers, 13, 600) : cs.IsChecking ? 12 : 0;
            gx.DrawText(c, gx.Ellipsize(ch.Name, 15, 700, r.Width - 32 - pw - 12), r.Left + 16, gx.Baseline(r.Top, r.Height, 15, 700), 15, 700, Tokens.WithAlpha(Tokens.Text, p));
            float my = r.Top + r.Height / 2, lh = gx.LineHeight(13, 600);
            if (peers.Length > 0)
            {
                float x = right - pw;
                gx.FillCircle(c, x + 3.5f, my, 3.5f, Tokens.WithAlpha(Tokens.Accent, p));
                gx.DrawText(c, peers, x + 13, gx.Baseline(my - lh / 2, lh, 13, 600), 13, 600, Tokens.TextA(0.70f * p));
            }
            else if (cs.IsChecking) _ctx.Fx.Spinner(c, right - 6, my, 12, now, Tokens.WithAlpha(Tokens.Accent2, p));
        }

        private void DrawAction(SKCanvas c, SKRect r, Icon icon, string label, bool cast, float p)
        {
            var gx = _ctx.Gx;
            if (cast)
            {
                gx.FillRR(c, r, 18, Tokens.C(63, 232, 207, 0.10f * p));
                gx.StrokeRR(c, r, 18, 1, Tokens.C(63, 232, 207, 0.45f * p));
            }
            else
            {
                gx.FillRR(c, r, 18, Tokens.White(0.06f * p));
                gx.StrokeRR(c, r, 18, 1, Tokens.White(0.12f * p));
            }
            float tw = gx.MeasureText(label, 15, 700), total = 20 + 10 + tw, x = (r.Left + r.Right) / 2 - total / 2, my = (r.Top + r.Bottom) / 2;
            gx.DrawIcon(c, icon, x + 10, my, 20, cast ? Tokens.WithAlpha(Tokens.Accent, p) : Tokens.WithAlpha(Tokens.Text, p), 1.8f);
            gx.DrawText(c, label, x + 30, gx.Baseline(r.Top, r.Height, 15, 700), 15, 700, Tokens.WithAlpha(Tokens.Text, p));
        }

        /// <summary>The "EN DIRECTO" badge (filled when at live) or, when behind, an outlined "DIRECTO" to go back to live. Returns the x where it ends.</summary>
        private float DrawLiveBadge(SKCanvas c, SKRect r, bool atLive, float p, double now)
        {
            var gx = _ctx.Gx;
            string t = atLive ? "EN DIRECTO" : "DIRECTO";
            if (atLive) gx.FillRR(c, r, 8, Tokens.C(63, 232, 207, 0.16f * p));
            else { gx.FillRR(c, r, 8, Tokens.C(63, 232, 207, 0.06f * p)); gx.StrokeRR(c, r, 8, 1.5f, Tokens.WithAlpha(Tokens.Accent, p)); }
            gx.DrawTextAligned(c, t, (r.Left + r.Right) / 2, gx.Baseline(r.Top, r.Height, 13, 800), 13, 800, Tokens.WithAlpha(Tokens.Accent, p), 0.5f, 0.52f);
            if (!_quiet && _ctl.Focus == PanelButton.Live) _ctx.Fx.FluidRing(c, r, 8, p, now);
            return r.Right;
        }

        private void DrawStep(SKCanvas c, SKRect r, string label, float size, PanelButton btn, float p, double now, float fill, float border)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, r, r.Height / 2, Tokens.White(fill * p));
            gx.StrokeRR(c, r, r.Height / 2, 1, Tokens.White(border * p));
            gx.DrawTextAligned(c, label, (r.Left + r.Right) / 2, gx.Baseline(r.Top, r.Height, size, 800), size, 800, Tokens.WithAlpha(Tokens.Text, p), 0.5f);
            if (!_quiet && _ctl.Focus == btn) _ctx.Fx.FluidRing(c, r, r.Height / 2, p, now);
        }

        private void DrawPlayButton(SKCanvas c, SKRect r, bool paused, float iconSize, float p, double now)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, r, r.Height / 2, Tokens.WithAlpha(Tokens.Text, p));
            gx.DrawIcon(c, paused ? Icons.Play : Icons.Pause, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, iconSize, Tokens.C(7, 7, 17, p), 2f);
            if (!_quiet && _ctl.Focus == PanelButton.PlayPause) _ctx.Fx.FluidRing(c, r, r.Height / 2, p, now);
        }

        /// <summary>The memory bar: the whole track is what is stored, the fill reaches the playhead, the knob is the playhead (at the right end = live). Drag it to move.</summary>
        private void DrawTimeline(SKCanvas c, CGeo g, double now, float p, bool atLive)
        {
            var gx = _ctx.Gx;
            float half = g.TrackH / 2, cy = g.BarCy;
            float frac = _scrub ? _scrubFrac : _ctl.Fraction(now);
            float kx = g.X0 + (g.X1 - g.X0) * frac;
            gx.FillRR(c, new SKRect(g.X0, cy - half, g.X1, cy + half), half, Tokens.White((g.Page ? 0.12f : 0.16f) * p));
            gx.FillRR(c, new SKRect(g.X0, cy - half, Math.Max(g.X0 + 1, kx), cy + half), half, Tokens.WithAlpha(Tokens.Accent2, (g.Page ? 0.55f : 0.65f) * p));
            gx.FillCircle(c, kx, cy + 1.5f, g.KnobR + 1, Tokens.C(0, 0, 0, 0.28f * p));
            gx.FillCircle(c, kx, cy, g.KnobR, Tokens.WithAlpha(Tokens.Text, p));
            if (!atLive || _scrub)
            {
                string b = FormatBehind(_scrub ? (long)((1f - _scrubFrac) * _ctl.WindowMs) : _ctl.BehindMs(now));
                float bw = gx.MeasureText(b, 13, 700) + 18, bh = gx.LineHeight(13, 700) + 8;
                var br = new SKRect(kx - bw / 2, cy - g.KnobR - 10 - bh, kx + bw / 2, cy - g.KnobR - 10);
                if (br.Left < g.X0 - 10) br = new SKRect(g.X0 - 10, br.Top, g.X0 - 10 + bw, br.Bottom);
                if (br.Right > g.X1 + 10) br = new SKRect(g.X1 + 10 - bw, br.Top, g.X1 + 10, br.Bottom);
                gx.FillRR(c, br, 8, Tokens.C(0, 0, 0, 0.6f * p));
                gx.DrawTextAligned(c, b, (br.Left + br.Right) / 2, gx.Baseline(br.Top, bh, 13, 700), 13, 700, Tokens.WithAlpha(Tokens.Text, p), 0.5f);
            }
        }

        // ---------------------------------------------------------------- the overlay (landscape)
        private void DrawOverlayHud(SKCanvas c, CGeo g, double now, float p)
        {
            var gx = _ctx.Gx;
            var st = _ctl.Status;
            bool atLive = _ctl.AtLive(now);
            var sl = _ctx.SurfaceLogical;
            // scrims for the text to be read over any picture
            float topH = 140 - sl.Top, botH = 260 + (sl.Bottom - _ctx.CH);
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, sl.Top), new SKPoint(0, sl.Top + topH), new[] { Tokens.C(0, 0, 0, 0.65f * p), Tokens.C(0, 0, 0, 0f) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp))
                gx.FillRR(c, new SKRect(sl.Left, sl.Top, sl.Right, sl.Top + topH), 0, sh);
            using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, sl.Bottom), new SKPoint(0, sl.Bottom - botH), new[] { Tokens.C(0, 0, 0, 0.80f * p), Tokens.C(0, 0, 0, 0f) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp))
                gx.FillRR(c, new SKRect(sl.Left, sl.Bottom - botH, sl.Right, sl.Bottom), 0, sh);
            // header: arrow, title, peers and speed, buttons
            c.Save(); c.Translate(0, (1 - p) * -24);
            gx.FillRR(c, g.Back, 24, Tokens.White(0.10f * p));
            gx.DrawIcon(c, Icons.Back, (g.Back.Left + g.Back.Right) / 2, (g.Back.Top + g.Back.Bottom) / 2, 24, Tokens.WithAlpha(Tokens.Text, p), 2f);
            float rightLimit = (g.HasCast ? g.Cast.Left : _fullscreen ? g.Compress.Left : _ctx.CW - 24) - 16;
            string title = _pc.Current != null ? _pc.Current.Name : st.Name;
            gx.DrawText(c, gx.Ellipsize(title ?? "", 24, 800, rightLimit - g.TitleX), g.TitleX, gx.Baseline(12, 32, 24, 800), 24, 800, Tokens.WithAlpha(Tokens.Text, p), -0.48f);
            string peers = st.Peers.ToString(CultureInfo.InvariantCulture), speed = PlaybackStatus.FormatSpeed(st.DownBytesPerSec);
            float sy = 12 + 32 + 4 + 9.5f, lh = gx.LineHeight(14, 600);
            _ctx.Fx.PulseDot(c, g.TitleX + 3.5f, sy, 3.5f, now, 7);
            gx.DrawText(c, peers, g.TitleX + 13, gx.Baseline(sy - lh / 2, lh, 14, 600), 14, 600, Tokens.TextA(0.75f * p));
            gx.DrawText(c, speed, g.TitleX + 13 + gx.MeasureText(peers, 14, 600) + 12, gx.Baseline(sy - lh / 2, lh, 14, 600), 14, 600, Tokens.TextA(0.75f * p));
            if (g.HasCast)
            {
                gx.FillRR(c, g.Cast, 24, Tokens.C(63, 232, 207, 0.12f * p));
                gx.StrokeRR(c, g.Cast, 24, 1, Tokens.C(63, 232, 207, 0.45f * p));
                gx.DrawIcon(c, Icons.Cast, g.Cast.Left + 18 + 10, (g.Cast.Top + g.Cast.Bottom) / 2, 20, Tokens.WithAlpha(Tokens.Accent, p), 1.8f);
                gx.DrawText(c, "Enviar a la tele", g.Cast.Left + 18 + 30, gx.Baseline(g.Cast.Top, g.Cast.Height, 15, 700), 15, 700, Tokens.WithAlpha(Tokens.Text, p));
            }
            if (_fullscreen)
            {
                gx.FillRR(c, g.Compress, 24, Tokens.White(0.10f * p));
                gx.DrawIcon(c, Icons.Compress, (g.Compress.Left + g.Compress.Right) / 2, (g.Compress.Top + g.Compress.Bottom) / 2, 22, Tokens.WithAlpha(Tokens.Text, p), 1.8f);
            }
            c.Restore();
            // bottom: the memory bar and the controls
            c.Save(); c.Translate(0, (1 - p) * 32);
            DrawTimeline(c, g, now, p, atLive);
            bool paused = _ctl.Mode == PlayerMode.Paused;
            DrawStep(c, g.Back10, "−10", 16, PanelButton.Back10, p, now, 0.08f, 0.14f);
            DrawPlayButton(c, g.Play, paused, 30, p, now);
            DrawStep(c, g.Fwd10, "+10", 16, PanelButton.Forward10, p, now, 0.08f, 0.14f);
            var lr = g.Live;
            if (atLive) gx.FillRR(c, lr, 10, Tokens.C(63, 232, 207, 0.18f * p));
            else { gx.FillRR(c, lr, 10, Tokens.C(63, 232, 207, 0.06f * p)); gx.StrokeRR(c, lr, 10, 1.5f, Tokens.WithAlpha(Tokens.Accent, p)); }
            gx.DrawTextAligned(c, atLive ? "EN DIRECTO" : "DIRECTO", (lr.Left + lr.Right) / 2, gx.Baseline(lr.Top, lr.Height, 13, 800), 13, 800, Tokens.WithAlpha(Tokens.Accent, p), 0.5f, 0.78f);
            if (!_quiet && _ctl.Focus == PanelButton.Live) _ctx.Fx.FluidRing(c, lr, 10, p, now);
            c.Restore();
        }

        // ---------------------------------------------------------------- buffer ring
        private void DrawBufferRingIn(SKCanvas c, double now, float a, SKRect area, float outerR, float thick, float pctSize, float rowSize)
        {
            var gx = _ctx.Gx;
            var st = _ctl.Status;
            string ptxt = _ctl.BufferPercentShown + " %";
            float pctH = pctSize * 1.35f, rowH = rowSize * 1.4f, gap1 = 10, gap2 = 10;
            float colH = 2 * outerR + gap1 + pctH + gap2 + rowH, top = (area.Top + area.Bottom) / 2 - colH / 2, cx = (area.Left + area.Right) / 2;
            DrawFluidDisc(c, cx, top + outerR, outerR, thick, now, a);
            gx.DrawTextAligned(c, ptxt, cx, gx.Baseline(top + 2 * outerR + gap1, pctH, pctSize, 800), pctSize, 800, Tokens.WithAlpha(Tokens.Text, a), 0.5f);
            float ry = top + 2 * outerR + gap1 + pctH + gap2 + rowH / 2;
            string seeds = st.Seeds.ToString(CultureInfo.InvariantCulture), peers = st.Peers.ToString(CultureInfo.InvariantCulture);
            float w1 = 16 + 6 + gx.MeasureText(seeds, rowSize, 600), w2 = 16 + 6 + gx.MeasureText(peers, rowSize, 600);
            float x = cx - (w1 + 18 + w2) / 2;
            var col = Tokens.TextA(0.70f * a);
            gx.DrawIcon(c, Icons.Seeds, x + 8, ry, 16, col, 1.8f); gx.DrawText(c, seeds, x + 22, gx.Baseline(ry - rowH / 2, rowH, rowSize, 600), rowSize, 600, col);
            x += w1 + 18;
            gx.DrawIcon(c, Icons.People, x + 8, ry, 16, col, 1.8f); gx.DrawText(c, peers, x + 22, gx.Baseline(ry - rowH / 2, rowH, rowSize, 600), rowSize, 600, col);
        }

        // ---------------------------------------------------------------- loading
        private void DrawLoadingCompact(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            var st = _ctx.Style;
            c.Clear(Tokens.BgPlayerLoading);
            _ctx.Lava.Draw(c, _ctx.Gpu, now, _ctx.Surface.HasBars ? (SKRect?)_ctx.SurfaceLogical : null);
            gx.FillRR(c, _ctx.SurfaceLogical, 0, Tokens.C(5, 5, 12, 0.18f));
            var g = Cg();
            gx.DrawIcon(c, Icons.Back, (g.Back.Left + g.Back.Right) / 2, (g.Back.Top + g.Back.Bottom) / 2, 24, Tokens.Text, 2f);
            float W = st.W, H = st.H;
            float tsize = st.Phone ? 26 : st.SideRail ? 34 : 30, barW = st.Phone ? 160 : 200, small = st.Phone ? 14 : 15;
            float maxW = Math.Min(W - 64, 640), lineH = tsize * 1.2f;
            string title = _pc.Current != null ? _pc.Current.Name : "";
            var lines = gx.WrapText(title, tsize, 800, maxW, 2);
            float stats = small * 1.45f, colH = lines.Count * lineH + 18 + 4 + 18 + stats, top = H / 2f - colH / 2 - 8;
            for (int i = 0; i < lines.Count; i++)
                ShadowText(c, lines[i], W / 2f - gx.MeasureText(lines[i], tsize, 800, -tsize * 0.02f) / 2, gx.Baseline(top + i * lineH, lineH, tsize, 800), tsize, 800, Tokens.Text, -tsize * 0.02f);
            float by = top + lines.Count * lineH + 18, bx0 = W / 2f - barW / 2, bx1 = W / 2f + barW / 2;
            gx.FillRR(c, new SKRect(bx0, by, bx1, by + 4), 2, Tokens.White(0.14f));
            if (_ctl.LoadIndeterminate)
            {
                float phase = (float)((now % 1.6) / 1.6), gw = barW * 0.32f, gxl = bx0 - gw + (barW + gw) * phase;
                c.Save();
                using (var rr = new SKRoundRect(new SKRect(bx0, by, bx1, by + 4), 2, 2)) c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
                using (var sh = SKShader.CreateLinearGradient(new SKPoint(gxl, 0), new SKPoint(gxl + gw, 0), new[] { Tokens.TextA(0f), Tokens.TextA(0.95f), Tokens.TextA(0f) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp))
                using (var paint = new SKPaint { Shader = sh, IsAntialias = true })
                    c.DrawRect(new SKRect(gxl, by, gxl + gw, by + 4), paint);
                c.Restore();
                float shown = _loadBar.Value(now);
                if (shown > 0.06f) gx.FillRR(c, new SKRect(bx0, by, bx0 + barW * shown, by + 4), 2, Tokens.TextA(0.35f));
            }
            else gx.FillRR(c, new SKRect(bx0, by, bx0 + barW * Math.Max(0.03f, _loadBar.Value(now)), by + 4), 2, Tokens.TextA(0.95f));
            var ps = _ctl.Status;
            string peers = ps.Peers.ToString(CultureInfo.InvariantCulture), speed = PlaybackStatus.FormatSpeed(ps.DownBytesPerSec);
            float w1 = 7 + 6 + gx.MeasureText(peers, small, 600), w2 = gx.MeasureText(speed, small, 600);
            float x = W / 2f - (w1 + 14 + w2) / 2, cy = by + 4 + 18 + stats / 2;
            var col = Tokens.TextA(0.72f);
            gx.FillCircle(c, x + 3.5f, cy, 3.5f, Tokens.Accent);
            gx.DrawText(c, peers, x + 13, gx.Baseline(cy - stats / 2, stats, small, 600), small, 600, col);
            gx.DrawText(c, speed, x + w1 + 14, gx.Baseline(cy - stats / 2, stats, small, 600), small, 600, col);
        }

        // ---------------------------------------------------------------- error
        private void DrawErrorCardCompact(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            var st = _ctx.Style;
            string msg = _ctl.Status.Error ?? "";
            string title = _pc.Current != null ? _pc.Current.Name : "";
            float w = Math.Min(st.W - 48, 520);
            var lines = gx.WrapText(msg, 16, 600, w - 48, 4);
            const string hint = "Toca para reintentar";
            float h = 24 + 32 + 10 + lines.Count * 22 + 14 + 20 + 24;
            var r = new SKRect(st.W / 2f - w / 2, st.H / 2f - h / 2, st.W / 2f + w / 2, st.H / 2f + h / 2);
            gx.FillRR(c, r, 24, Tokens.C(10, 12, 20, 0.80f));
            gx.StrokeRR(c, r, 24, 1.5f, Tokens.WithAlpha(Tokens.Warn, 0.7f));
            gx.DrawIcon(c, Icons.Alert, r.Left + 24 + 12, r.Top + 24 + 16, 24, Tokens.Warn, 2f);
            gx.DrawText(c, gx.Ellipsize(title, 18, 800, w - 48 - 40), r.Left + 24 + 36, gx.Baseline(r.Top + 24, 32, 18, 800), 18, 800, Tokens.Text, -0.36f);
            for (int i = 0; i < lines.Count; i++) gx.DrawText(c, lines[i], r.Left + 24, gx.Baseline(r.Top + 24 + 32 + 10 + i * 22, 22, 16, 600), 16, 600, Tokens.TextA(0.80f));
            gx.DrawText(c, hint, r.Left + 24, gx.Baseline(r.Top + 24 + 32 + 10 + lines.Count * 22 + 14, 20, 14, 600), 14, 600, Tokens.TextA(0.52f));
        }
    }
}
