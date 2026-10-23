using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.App.Tv;
using AceEngine.Util;
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
    /// Player (design PlayerLoading, Player, PlayerPaused, PlayerBuffering): channel loading with lava lamp, info panel with in-memory timeline
    /// (hidden after 5 s), next-channel card (Channel +/-), pause, +/-10 s and LIVE, and buffer ring over a light veil.
    /// The window is transparent when there is video; with the panel hidden nothing is drawn (zero cost).
    /// </summary>
    public sealed partial class PlayerScreen : IScreen, ICastLocalPlayer
    {
        /// <summary>Only for desktop captures: draws a fake video (like the design's) behind the interface.</summary>
        public static bool MockVideo;

        private UiContext _ctx;
        private readonly PlayContext _pc;
        private readonly PlayerController _ctl = new PlayerController();
        private IPlayback _pb;
        private CancellationTokenSource _cts;
        private volatile PlaybackStatus _latest;
        private volatile bool _statusDirty;
        private readonly Anim _panel = new Anim(0, Bezier.Pop), _loadBar = new Anim(0, Bezier.Pop), _veil = new Anim(0, Bezier.Pop), _scrim = new Anim(0, Bezier.Pop);
        private bool _panelShown, _drewEmpty;
        /// <summary>The error card is already drawn as it is (nothing moves in it): until the state changes no further frame is needed.</summary>
        private bool _drewStatic;
        private bool _dead;
        private readonly WatchTracker _watch = new WatchTracker();
        private SKImage _mockBlob; private float _mockPad;

        public PlayerScreen(PlayContext pc) { _pc = pc; }

        public string Name { get { return "Player " + (_pc.Current != null ? _pc.Current.Name : ""); } }
        public bool WindowOpaque { get { return _castMode || !_ctl.WindowTransparent; } }
        public PlayerController Controller { get { return _ctl; } }
        /// <summary>The list and the current channel (after Channel +/-): to return to the same channel after a short app pause.</summary>
        public PlayContext Context { get { return _pc; } }

        // ---------------------------------------------------------------- lifecycle
        public void Enter(UiContext ctx)
        {
            _ctx = ctx;
            _quiet = ctx.TouchFirst;
            ResetTouch();
            SetKeepScreenOn(true);
            RegisterCastLocal();
            if (_pb == null && !_dead)
            {
                if (CastActiveNow) EnterCastMode();                 // a channel is being sent to another screen: this one shows that, it plays nothing
                else StartChannel();
            }
        }

        public void Leave() { UnregisterCastLocal(); StopPlayback(true); SetKeepScreenOn(false); LeaveCompact(); }

        public void Suspend() { UnregisterCastLocal(); _dead = true; StopPlayback(true); SetKeepScreenOn(false); ResetTouch(); LeaveCompact(); }

        /// <summary>The screen must not sleep while the player is open (and only then: menus may let it sleep). A no-op on the Samsung TV.</summary>
        private void SetKeepScreenOn(bool on)
        {
            if (_ctx == null) return;
            try { _ctx.Platform.Window.KeepScreenOn = on; } catch (Exception ex) { AceLog.Warn("ui", "keep screen on: " + ex.Message); }
        }

        public void Resume() { if (_dead) _ctx.Nav.Pop(); }

        private void StartChannel()
        {
            StopPlayback();
            var ch = _pc.Current;
            if (ch == null) { _ctx.Nav.Pop(); return; }
            _ctl.Reset(); _ctl.CanChangeChannel = _pc.List.Count > 1;
            _panel.Snap(0); _panelShown = false; _drewEmpty = false; _loadBar.Snap(0.05f); _veil.Snap(0); _scrim.Snap(0);
            _latest = new PlaybackStatus { Phase = PlayPhase.Resolving, Name = ch.Name };
            _statusDirty = true;
            _watch.Reset();
            var pb = _ctx.Services.Playback.Create();
            _pb = pb;
            var cts = new CancellationTokenSource(); _cts = cts;
            pb.Changed += st => { if (ReferenceEquals(_pb, pb)) { _latest = st; _statusDirty = true; _ctx.Nav.Invalidate(); } };
            var entry = ch.Entry; string name = ch.Name;
            _runTask = Task.Run(async () =>
            {
                try { await pb.RunAsync(entry, name, cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { AceLog.Warn("ui", "playback: " + ex.Message); }
            });
            AceLog.Info("ui", "open channel " + ch.Name);
        }

        private void StopPlayback(bool leaving = false)
        {
            // On leaving the player, FIRST the opaque window and THEN release the video, never the other way round. TV log (2026-10-17): every time the TV
            // opened its Home menu on leaving with Back, the player had been released a few ms before the window stopped being transparent (both things ran
            // in parallel and the order came out random); with the opaque window first it never happened. Changing the alpha with the video attached interrupts it, but here
            // it does not matter: it is about to close. When switching channel it does not apply (leaving=false): the window stays transparent for the next one.
            if (leaving && _ctx != null && (_pb != null || _cts != null))
            {
                try { _ctx.Platform.Window.Opaque = true; } catch (Exception ex) { AceLog.Warn("ui", "window: " + ex.Message); }
            }
            var pb = _pb; var cts = _cts;
            _pb = null; _cts = null;
            if (cts != null) { try { cts.Cancel(); } catch { } }
            if (pb != null)
            {
                try { pb.Stop(); } catch { }
                Task.Run(() => { try { pb.Dispose(); } catch { } });
            }
        }

        // ---------------------------------------------------------------- keys
        public void OnKey(KeyPress key)
        {
            if (key.Key == UiKey.Back && _fullscreen) { ExitFullscreen(); return; }            // Back leaves full screen first
            if (_castMode) { OnCastKey(key); return; }
            if (_quiet && (key.Key == UiKey.Up || key.Key == UiKey.Down || key.Key == UiKey.Left || key.Key == UiKey.Right || key.Key == UiKey.Ok)) _quiet = false;       // a key brings the panel's focus back
            var act = _ctl.OnKey(key.Key, _ctx.Now);
            AceLog.Info("ui", "key " + key.Key + " -> " + act + " (mode=" + _ctl.Mode + " panel=" + (_ctl.PanelWanted ? "yes" : "no") + ")");
            Perform(act);
        }

        /// <summary>Does what the controller decided (from a key or from a finger).</summary>
        private void Perform(PlayerAction act)
        {
            var pb = _pb;
            switch (act)
            {
                case PlayerAction.Exit: _ctx.Nav.Pop(); break;
                case PlayerAction.TogglePause: if (_castMode) _ctx.CastUi.TogglePause(); else if (pb != null) pb.TogglePause(); break;
                case PlayerAction.SeekBack: Seek(pb, -10); break;
                case PlayerAction.SeekForward: Seek(pb, 10); break;
                case PlayerAction.GoLive: if (pb != null) FireAndForget(pb.GoLiveAsync()); break;
                case PlayerAction.NextChannel: if (_pc.Move(1)) StartChannel(); break;
                case PlayerAction.PrevChannel: if (_pc.Move(-1)) StartChannel(); break;
                case PlayerAction.Retry: StartChannel(); break;
            }
        }

        private void Seek(IPlayback pb, int seconds)
        {
            if (pb == null) return;
            if (_ctl.Status.IsLive) FireAndForget(pb.SeekBehindLiveAsync(TimeSpan.FromSeconds(seconds)));
            else FireAndForget(pb.SeekRelativeAsync(seconds * 1000L));
        }

        private static void FireAndForget(Task t)
        {
            if (t == null) return;
            t.ContinueWith(x => { if (x.IsFaulted) AceLog.Warn("ui", "player: " + x.Exception.GetBaseException().Message); }, TaskContinuationOptions.OnlyOnFaulted);
        }

        // ---------------------------------------------------------------- frame
        public FrameDemand Update(double now)
        {
            if (Compact)
            {
                UpdateCast(now);
                if (_castMode) return CastFrameDemand(now);
            }
            if (_statusDirty)
            {
                _statusDirty = false;
                _drewStatic = false;
                var st = _latest;
                var before = _ctl.Mode;
                if (st != null) _ctl.OnStatus(st, now);
                _loadBar.Set(_ctl.LoadProgressShown, now, 0.6);
                if (before != _ctl.Mode) _drewEmpty = false;
            }
            _ctl.Tick(now);
            if (_watch.Update(_ctl.Mode, now) && _pc.Current != null)
            {
                // more than 5 minutes in a row of real playback: the channel enters Recents
                try { _ctx.Services.Recents.Played(_pc.Current); AceLog.Info("ui", "recent recorded: " + _pc.Current.Name); }
                catch (Exception ex) { AceLog.Warn("ui", "recents: " + ex.Message); }
            }
            bool want = (PageLayout ? PageWanted(_ctl.Mode) : _ctl.PanelWanted) && _ctl.Mode != PlayerMode.Loading;
            UpdateVideoRect();
            if (want != _panelShown) { _panelShown = want; _panel.Set(want ? 1 : 0, now, want ? Tokens.PanelInSecs : Tokens.PanelOutSecs); _drewEmpty = false; }
            _veil.Set(_ctl.Mode == PlayerMode.Buffering ? 1 : 0, now, 0.25);
            _scrim.Set(_ctl.Mode == PlayerMode.Paused ? 1 : 0, now, 0.25);

            if (_ctl.Mode == PlayerMode.Loading) return FrameDemand.Ambient;
            bool anim = _panel.Active(now) || _veil.Active(now) || _scrim.Active(now) || _loadBar.Active(now) || _press.Active(now) || _scrub;
            if (anim) { _drewEmpty = false; return FrameDemand.Full; }
            if (_ctl.Mode == PlayerMode.Buffering) return FrameDemand.Full;
            // "Sin fuentes por ahora" when opening a channel (error without having reached playback): the error screen has nothing that moves; one frame and then nothing (before, it was redrawn
            // at 30 per s the whole time the retry loop lasted)
            if (_ctl.Mode == PlayerMode.Error && !_ctl.Started) return _drewStatic ? FrameDemand.Idle : FrameDemand.Full;
            // panel visible or paused: the only thing that moves is decoration (the focused button's ring, the live dot's pulse): it slows down and stops at rest
            if (_panel.Value(now) > 0.01f || _ctl.Mode == PlayerMode.Paused) return FrameDemand.Decor;
            if (PageLayout) return FrameDemand.Decor;
            // nothing to see: one last empty (transparent) frame and then zero cost
            if (!_drewEmpty) return FrameDemand.Full;
            return MockVideo ? FrameDemand.Ambient : FrameDemand.Idle;
        }

        public void Draw(SKCanvas c, double now)
        {
            var gx = _ctx.Gx;
            if (_castMode && Compact) { DrawCastCompact(c, now); return; }
            var mode = _ctl.Mode;
            if (mode == PlayerMode.Loading) { DrawLoading(c, now); return; }
            if (mode == PlayerMode.Error && !_ctl.Started) { DrawErrorScreen(c, now); _drewStatic = true; return; }
            if (Compact) { DrawCompact(c, now); return; }
            c.Clear(SKColors.Transparent);
            if (MockVideo) DrawMockVideo(c);
            else if (mode == PlayerMode.Error) c.Clear(new SKColor(5, 6, 12, 255));
            float scrim = _scrim.Value(now);
            if (scrim > 0.01f) gx.FillRR(c, new SKRect(0, 0, Tokens.W, Tokens.H), 0, Tokens.C(5, 6, 12, 0.28f * scrim));
            float veil = _veil.Value(now);
            if (veil > 0.01f) { gx.FillRR(c, new SKRect(0, 0, Tokens.W, Tokens.H), 0, Tokens.C(5, 6, 12, 0.10f * veil)); DrawBufferRing(c, now, veil); }
            float p = _panel.Value(now);
            if (p > 0.01f) DrawHud(c, now, p);
            DrawPress(c, now);
            if (mode == PlayerMode.Error) DrawErrorCard(c, now);
            _drewEmpty = p <= 0.01f;
            _drewStatic = true;
        }

        // ---------------------------------------------------------------- channel loading (lava lamp)
        private void DrawLoading(SKCanvas c, double now)
        {
            if (Compact) { DrawLoadingCompact(c, now); return; }
            var gx = _ctx.Gx;
            c.Clear(Tokens.BgPlayerLoading);
            _ctx.Lava.Draw(c, _ctx.Gpu, now, _ctx.Surface.HasBars ? (SKRect?)_ctx.SurfaceLogical : null);
            gx.FillRR(c, _ctx.SurfaceLogical, 0, Tokens.C(5, 5, 12, 0.18f));
            string title = _pc.Current != null ? _pc.Current.Name : "";
            string t = gx.Ellipsize(title, 44, 800, 1500);
            float tw = gx.MeasureText(t, 44, 800, -1.32f);
            // centered column: title (46.2) + 26 + bar (4) + 26 + stats (23)
            float stats = 23.2f, colH = 46.2f + 26 + 4 + 26 + stats;
            float top = Tokens.H / 2f - colH / 2;
            ShadowText(c, t, Tokens.W / 2f - tw / 2, gx.Baseline(top, 46.2f, 44, 800), 44, 800, Tokens.Text, -1.32f);
            float by = top + 46.2f + 26;
            gx.FillRR(c, new SKRect(Tokens.W / 2f - 140, by, Tokens.W / 2f + 140, by + 4), 2, Tokens.White(0.22f));
            if (_ctl.LoadIndeterminate)
            {
                // no measurable progress: a shimmer that slides along the bar (not a bar that goes back and forth)
                float phase = (float)((now % 1.6) / 1.6), gw = 90, gxl = Tokens.W / 2f - 140 - gw + (280 + gw) * phase;
                c.Save();
                using (var rr = new SKRoundRect(new SKRect(Tokens.W / 2f - 140, by, Tokens.W / 2f + 140, by + 4), 2, 2)) c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
                using (var sh = SKShader.CreateLinearGradient(new SKPoint(gxl, 0), new SKPoint(gxl + gw, 0), new[] { Tokens.TextA(0f), Tokens.TextA(0.95f), Tokens.TextA(0f) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp))
                using (var paint = new SKPaint { Shader = sh, IsAntialias = true })
                    c.DrawRect(new SKRect(gxl, by, gxl + gw, by + 4), paint);
                c.Restore();
                // what has already advanced (if anything) stays as a dim fill underneath
                float shown = _loadBar.Value(now);
                if (shown > 0.06f) gx.FillRR(c, new SKRect(Tokens.W / 2f - 140, by, Tokens.W / 2f - 140 + 280 * shown, by + 4), 2, Tokens.TextA(0.35f));
            }
            else gx.FillRR(c, new SKRect(Tokens.W / 2f - 140, by, Tokens.W / 2f - 140 + 280 * Math.Max(0.03f, _loadBar.Value(now)), by + 4), 2, Tokens.TextA(0.95f));
            // peers and download
            var st = _ctl.Status;
            string peers = st.Peers.ToString(CultureInfo.InvariantCulture), speed = PlaybackStatus.FormatSpeed(st.DownBytesPerSec);
            float w1 = 20 + 8 + gx.MeasureText(peers, 17, 600), w2 = 18 + 6 + gx.MeasureText(speed, 17, 600);
            float total = w1 + 28 + w2, x = Tokens.W / 2f - total / 2, cy = by + 4 + 26 + stats / 2;
            var col = Tokens.TextA(0.86f);
            gx.DrawIcon(c, Icons.People, x + 10, cy, 20, col, 1.8f);
            gx.DrawText(c, peers, x + 28, gx.Baseline(cy - stats / 2, stats, 17, 600), 17, 600, col);
            x += w1 + 28;
            gx.DrawIcon(c, Icons.ArrowDown, x + 9, cy, 18, col, 2f);
            gx.DrawText(c, speed, x + 24, gx.Baseline(cy - stats / 2, stats, 17, 600), 17, 600, col);
        }

        private void ShadowText(SKCanvas c, string s, float x, float baseline, float size, int weight, SKColor col, float ls)
        {
            // the design's soft shadow (0 2px 24px rgba(0,0,0,.55)): three offset, very transparent passes
            var gx = _ctx.Gx;
            for (int i = 0; i < 3; i++) gx.DrawText(c, s, x + (i - 1) * 2, baseline + 3 + i, size, weight, Tokens.C(0, 0, 0, 0.16f), ls);
            gx.DrawText(c, s, x, baseline, size, weight, col, ls);
        }

        // ---------------------------------------------------------------- buffer ring
        private void DrawBufferRing(SKCanvas c, double now, float a)
        {
            var gx = _ctx.Gx;
            var st = _ctl.Status;
            int pct = _ctl.BufferPercentShown;
            string ptxt = pct + " %";
            // column: ring 128 + 22 + % (35) + 22 + row (22)
            float colH = 128 + 22 + 35.5f + 22 + 21.9f, top = Tokens.H / 2f - colH / 2, cx = Tokens.W / 2f;
            DrawFluidDisc(c, cx, top + 64, 64, 18, now, a);
            gx.DrawTextAligned(c, ptxt, cx, gx.Baseline(top + 128 + 22, 35.5f, 26, 800), 26, 800, Tokens.WithAlpha(Tokens.Text, a), 0.5f);
            float ry = top + 128 + 22 + 35.5f + 22 + 11;
            string seeds = st.Seeds.ToString(CultureInfo.InvariantCulture), peers = st.Peers.ToString(CultureInfo.InvariantCulture);
            float w1 = 18 + 7 + gx.MeasureText(seeds, 16, 600), w2 = 18 + 7 + gx.MeasureText(peers, 16, 600);
            float x = cx - (w1 + 22 + w2) / 2;
            var col = Tokens.TextA(0.70f * a);
            gx.DrawIcon(c, Icons.Seeds, x + 9, ry, 18, col, 1.8f); gx.DrawText(c, seeds, x + 25, gx.Baseline(ry - 11, 22, 16, 600), 16, 600, col);
            x += w1 + 22;
            gx.DrawIcon(c, Icons.People, x + 9, ry, 18, col, 1.8f); gx.DrawText(c, peers, x + 25, gx.Baseline(ry - 11, 22, 16, 600), 16, 600, col);
        }

        /// <summary>Thick ring with the fluid border (conic gradient that turns every 2.4 s) and its blurred halo at 60 %.</summary>
        private void DrawFluidDisc(SKCanvas c, float cx, float cy, float outerR, float thick, double now, float a)
        {
            float angle = (float)((now % Tokens.BufferRingSecs) / Tokens.BufferRingSecs * 360.0);
            var m = SKMatrix.MakeRotationDegrees(angle - 90, cx, cy);
            var key = "bufring|" + (int)outerR + "|" + (int)thick;
            var bounds = new SKRect(cx - outerR - 40, cy - outerR - 40, cx + outerR + 40, cy + outerR + 40);
            using (var sh = SKShader.CreateSweepGradient(new SKPoint(cx, cy), Tokens.RingColors, Tokens.RingPositions, m))
            {
                // halo: blurred ring as a sprite (mask) composed with the gradient
                var halo = _ctx.Sprites.GetPixels(key + "|halo", (int)Math.Ceiling(bounds.Width / 2), (int)Math.Ceiling(bounds.Height / 2), cv =>
                {
                    using (var src = new SKBitmap((int)Math.Ceiling(bounds.Width / 2), (int)Math.Ceiling(bounds.Height / 2), SKColorType.Rgba8888, SKAlphaType.Premul))
                    using (var dst = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    {
                        using (var sc = new SKCanvas(src)) using (var p = new SKPaint { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = thick / 2 })
                        { sc.Clear(SKColors.Transparent); sc.DrawCircle(src.Width / 2f, src.Height / 2f, (outerR - thick / 2) / 2, p); }
                        using (var dc = new SKCanvas(dst)) using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(8, 8) })
                        { dc.Clear(SKColors.Transparent); dc.DrawBitmap(src, 0, 0, p); }
                        using (var im = SKImage.FromBitmap(dst)) cv.DrawImage(im, 0, 0);
                    }
                });
                var gp = _ctx.Gx.Paint;
                using (var maskShader = halo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, ShaderMatrix(bounds)))
                using (var comp = SKShader.CreateCompose(maskShader, sh, SKBlendMode.SrcIn))
                {
                    gp.Style = SKPaintStyle.Fill; gp.MaskFilter = null; gp.Shader = comp; gp.Color = new SKColor(255, 255, 255, (byte)(0.6f * a * 255));
                    c.DrawRect(bounds, gp);
                    gp.Shader = null;
                }
                // sharp ring: crown of thickness 'thick' (fluid border)
                gp.Style = SKPaintStyle.Stroke; gp.StrokeWidth = thick; gp.Shader = sh; gp.MaskFilter = null; gp.Color = new SKColor(255, 255, 255, (byte)(a * 255));
                c.DrawCircle(cx, cy, outerR - thick / 2, gp);
                gp.Shader = null; gp.Style = SKPaintStyle.Fill;
            }
        }

        private static SKMatrix ShaderMatrix(SKRect b)
        {
            var m = SKMatrix.MakeScale(2f, 2f); m.TransX = b.Left; m.TransY = b.Top;
            return m;
        }

        // ---------------------------------------------------------------- HUD (panel + pills + next channel)
        private const float Hud = 72;
        private static readonly SKColor GlassFill = Tokens.C(10, 12, 20, 0.55f);

        private static string FormatBehind(long ms)
        {
            long s = (ms + 500) / 1000;
            return "−" + (s / 60) + ":" + (s % 60).ToString("00");
        }

        private void DrawHud(SKCanvas c, double now, float p)
        {
            var gx = _ctx.Gx;
            float slideTop = (1 - p) * -24, slideBottom = (1 - p) * 32;
            bool atLive = _ctl.AtLive(now);
            // --- top left: LIVE / delay
            c.Save(); c.Translate(0, slideTop);
            {
                float h = gx.LineHeight(18, 800) + 24, y = 64;
                if (atLive)
                {
                    float tw = gx.MeasureText("EN DIRECTO", 18, 800, 1.44f);
                    var r = new SKRect(Hud, y, Hud + 20 + 11 + 10 + tw + 20, y + h);
                    GlassPill(c, r, p);
                    _ctx.Fx.PulseDot(c, r.Left + 20 + 5.5f, r.Top + h / 2, 5.5f, now, 10);
                    gx.DrawText(c, "EN DIRECTO", r.Left + 20 + 11 + 10, gx.Baseline(r.Top, h, 18, 800), 18, 800, Tokens.WithAlpha(Tokens.Text, p), 1.44f);
                }
                else
                {
                    string b = FormatBehind(_ctl.BehindMs(now));
                    float tw = gx.MeasureText(b, 18, 700);
                    var r = new SKRect(Hud, y, Hud + 20 + 11 + 10 + tw + 20, y + h);
                    GlassPill(c, r, p);
                    gx.StrokeCircle(c, r.Left + 20 + 5.5f, r.Top + h / 2, 4.5f, 2, Tokens.TextA(0.6f * p));
                    gx.DrawText(c, b, r.Left + 20 + 11 + 10, gx.Baseline(r.Top, h, 18, 700), 18, 700, Tokens.TextA(0.86f * p));
                }
                // --- top right: next channel
                var next = _pc.List.Count > 1 && _ctl.Mode != PlayerMode.Paused ? _pc.Peek(1) : null;      // on pause the design does not show the next-channel card
                if (next != null) DrawNextCard(c, next, p, now);
            }
            c.Restore();
            // --- bottom: controls panel
            c.Save(); c.Translate(0, slideBottom);
            DrawPanel(c, now, p, atLive);
            c.Restore();
        }

        private void GlassPill(SKCanvas c, SKRect r, float p)
        {
            var gx = _ctx.Gx;
            gx.FillRR(c, r, r.Height / 2, Tokens.C(10, 12, 20, 0.50f * p));
            gx.StrokeRR(c, r, r.Height / 2, 1, Tokens.White(0.14f * p));
        }

        private SKRect NextCardBox(Gx gx)
        {
            const float w = 420, x = Tokens.W - Hud - w, y = 64;
            float chipH = gx.LineHeight(15, 800) + 12, h = 36 + chipH;
            return new SKRect(x, y, x + w, y + h);
        }

        private void DrawNextCard(SKCanvas c, Channel next, float p, double now)
        {
            var gx = _ctx.Gx;
            const float w = 420, x = Tokens.W - Hud - w, y = 64;
            float chipH = gx.LineHeight(15, 800) + 12, h = 36 + chipH;
            var r = new SKRect(x, y, x + w, y + h);
            gx.FillRR(c, r, 24, Tokens.C(10, 12, 20, 0.50f * p));
            gx.StrokeRR(c, r, 24, 1.5f, Tokens.WithAlpha(Tokens.Blue, 0.75f * p));
            // "CH ▲" label
            float cwid = gx.MeasureText("CH", 15, 800) + 6 + 9 + 20;
            var chip = new SKRect(x + 22, y + 18, x + 22 + cwid, y + 18 + chipH);
            gx.FillRR(c, chip, 10, Tokens.White(0.10f * p));
            gx.DrawText(c, "CH", chip.Left + 10, gx.Baseline(chip.Top, chipH, 15, 800), 15, 800, Tokens.TextA(0.86f * p));
            float tx = chip.Left + 10 + gx.MeasureText("CH", 15, 800) + 6, ty = chip.Top + chipH / 2;
            using (var tri = new SKPath()) { tri.MoveTo(tx + 4.5f, ty - 4); tri.LineTo(tx + 9, ty + 4); tri.LineTo(tx, ty + 4); tri.Close(); gx.Paint.Style = SKPaintStyle.Fill; gx.Paint.Shader = null; gx.Paint.Color = Tokens.TextA(0.86f * p); c.DrawPath(tri, gx.Paint); }
            // name and peers (if known)
            var st = _ctx.Services.Checker.GetStatus(next);
            string peers = st.IsLive ? st.PeersText : "";
            float right = x + w - 22;
            float peersW = peers.Length > 0 ? 7 + 8 + gx.MeasureText(peers, 16, 600) : 0;
            float nx = chip.Right + 16, avail = right - peersW - (peersW > 0 ? 16 : 0) - nx;
            gx.DrawText(c, gx.Ellipsize(next.Name, 19, 700, avail), nx, gx.Baseline(y, h, 19, 700), 19, 700, Tokens.WithAlpha(Tokens.Text, p));
            if (peers.Length > 0)
            {
                float px = right - peersW;
                gx.FillCircle(c, px + 3.5f, y + h / 2, 3.5f, Tokens.WithAlpha(Tokens.Accent, p));
                gx.DrawText(c, peers, px + 15, gx.Baseline(y, h, 16, 600), 16, 600, Tokens.TextA(0.70f * p));
            }
        }

        /// <summary>Where the controls panel puts things (the panel is fixed: it only slides in and out). Drawing and touch use the same numbers.</summary>
        private struct PanelGeom
        {
            public SKRect Panel;
            public float TitleH, TimelineH, X0, X1, Y0, Ty, By, Cy, BtnH;
            public float Back10Cx, PlayCx, Fwd10Cx;
            public SKRect Live;
            public SKRect Back10Box, PlayBox, Fwd10Box;
        }

        private PanelGeom Geom(Gx gx)
        {
            var g = new PanelGeom();
            g.TitleH = 44 * 1.05f; g.TimelineH = 40 + 10 + gx.LineHeight(15, 600); g.BtnH = 84;
            float H = 34 + g.TitleH + 28 + g.TimelineH + 28 + g.BtnH + 30;
            g.Panel = new SKRect(Hud, Tokens.H - 64 - H, Tokens.W - Hud, Tokens.H - 64);
            g.X0 = g.Panel.Left + 40; g.X1 = g.Panel.Right - 40; g.Y0 = g.Panel.Top + 34;
            g.Ty = g.Y0 + g.TitleH + 28;
            g.By = g.Ty + g.TimelineH + 28; g.Cy = g.By + g.BtnH / 2;
            float xb = g.X0;
            g.Back10Cx = xb + 32; xb += 64 + 18;
            g.PlayCx = xb + 42; xb += 84 + 18;
            g.Fwd10Cx = xb + 32;
            float lh = 56, lw = 24 + 10 + 10 + gx.MeasureText("DIRECTO", 17, 800, 1.36f) + 24;
            g.Live = new SKRect(g.X1 - lw, g.Cy - lh / 2, g.X1, g.Cy + lh / 2);
            g.Back10Box = new SKRect(g.Back10Cx - 32, g.Cy - 32, g.Back10Cx + 32, g.Cy + 32);
            g.PlayBox = new SKRect(g.PlayCx - 42, g.Cy - 42, g.PlayCx + 42, g.Cy + 42);
            g.Fwd10Box = new SKRect(g.Fwd10Cx - 32, g.Cy - 32, g.Fwd10Cx + 32, g.Cy + 32);
            return g;
        }

        private void DrawPanel(SKCanvas c, double now, float p, bool atLive)
        {
            var gx = _ctx.Gx;
            var st = _ctl.Status;
            var g = Geom(gx);
            float titleH = g.TitleH, timelineH = g.TimelineH;
            var panel = g.Panel;
            gx.FillRR(c, panel, 36, Tokens.C(10, 12, 20, 0.58f * p));
            gx.StrokeRR(c, panel, 36, 1, Tokens.White(0.14f * p));
            float x0 = g.X0, x1 = g.X1, y0 = g.Y0;
            // title and statistics
            string title = _pc.Current != null ? _pc.Current.Name : st.Name;
            var colText = Tokens.WithAlpha(Tokens.Text, p);
            // statistics (right-aligned, at the title's baseline)
            float sh = gx.LineHeight(20, 700);
            string sPeers = st.Peers.ToString(CultureInfo.InvariantCulture), sDown = PlaybackStatus.FormatSpeed(st.DownBytesPerSec), sUp = PlaybackStatus.FormatSpeed(st.UpBytesPerSec);
            float wUp = 22 + 8 + gx.MeasureText(sUp, 20, 700), wDown = 22 + 8 + gx.MeasureText(sDown, 20, 700), wPeers = 24 + 10 + gx.MeasureText(sPeers, 20, 700);
            float sx = x1 - (wPeers + 40 + wDown + 40 + wUp), sy = y0 + titleH - sh / 2 - 2;
            var sc = Tokens.TextA(0.80f * p);
            gx.DrawIcon(c, Icons.People, sx + 12, sy, 24, sc, 1.8f); gx.DrawText(c, sPeers, sx + 34, gx.Baseline(sy - sh / 2, sh, 20, 700), 20, 700, sc); sx += wPeers + 40;
            gx.DrawIcon(c, Icons.ArrowDown, sx + 11, sy, 22, sc, 2f); gx.DrawText(c, sDown, sx + 30, gx.Baseline(sy - sh / 2, sh, 20, 700), 20, 700, sc); sx += wDown + 40;
            var up = Tokens.WithAlpha(Tokens.Accent2, p);
            gx.DrawIcon(c, Icons.ArrowUp, sx + 11, sy, 22, up, 2f); gx.DrawText(c, sUp, sx + 30, gx.Baseline(sy - sh / 2, sh, 20, 700), 20, 700, up);
            float titleMax = (x1 - (wPeers + 40 + wDown + 40 + wUp)) - 48 - x0;
            gx.DrawText(c, gx.Ellipsize(title ?? "", 44, 800, titleMax), x0, gx.Baseline(y0, titleH, 44, 800), 44, 800, colText, -1.32f);

            // timeline
            float ty = g.Ty;
            float frac = _scrub ? _scrubFrac : _ctl.Fraction(now);
            gx.FillRR(c, new SKRect(x0, ty + 17, x1, ty + 23), 3, Tokens.White(0.14f * p));
            float kx = x0 + (x1 - x0) * frac;
            gx.FillRR(c, new SKRect(x0, ty + 17, Math.Max(x0 + 1, kx), ty + 23), 3, Tokens.TextA(0.88f * p));
            gx.FillCircle(c, kx, ty + 20, 17, Tokens.C(157, 107, 255, 0.30f * p));
            gx.FillCircle(c, kx, ty + 20, 11, Tokens.WithAlpha(Tokens.Text, p));
            _ctx.Fx.PulseDot(c, x1, ty + 20, 7, now, 10);
            if (!atLive || _scrub)
            {
                string b = FormatBehind(_scrub ? (long)((1f - _scrubFrac) * _ctl.WindowMs) : _ctl.BehindMs(now));
                float bw = gx.MeasureText(b, 15, 700) + 20, bh = gx.LineHeight(15, 700) + 8;
                var br = new SKRect(kx - bw / 2, ty - 30, kx + bw / 2, ty - 30 + bh);
                gx.FillRR(c, br, 8, Tokens.C(0, 0, 0, 0.6f * p));
                gx.DrawTextAligned(c, b, kx, gx.Baseline(br.Top, bh, 15, 700), 15, 700, Tokens.WithAlpha(Tokens.Text, p), 0.5f);
            }
            if (_ctl.WindowMs >= PlayerController.MinWindowMs)
                gx.DrawText(c, FormatBehind(_ctl.WindowMs), x0, gx.Baseline(ty + 50, gx.LineHeight(15, 600), 15, 600), 15, 600, Tokens.TextA(0.58f * p));

            // buttons
            float cy = g.Cy;
            bool paused = _ctl.Mode == PlayerMode.Paused;
            DrawRound(c, g.Back10Cx, cy, 64, Icons.Back10, PanelButton.Back10, p, now, false);
            DrawRound(c, g.PlayCx, cy, 84, paused ? Icons.Play : Icons.Pause, PanelButton.PlayPause, p, now, true);
            DrawRound(c, g.Fwd10Cx, cy, 64, Icons.Forward10, PanelButton.Forward10, p, now, false);
            // LIVE
            float lh = 56;
            var lr = g.Live;
            bool lf = !_quiet && _ctl.Focus == PanelButton.Live;
            float scale = lf ? 1.08f : 1f;
            c.Save(); c.Translate((lr.Left + lr.Right) / 2, cy); c.Scale(scale); c.Translate(-(lr.Left + lr.Right) / 2, -cy);
            if (atLive) gx.FillRR(c, lr, 28, Tokens.WithAlpha(Tokens.Accent, p));
            else { gx.FillRR(c, lr, 28, Tokens.C(63, 232, 207, 0.10f * p)); gx.StrokeRR(c, lr, 28, 1.5f, Tokens.WithAlpha(Tokens.Accent, p)); }
            var dc = atLive ? Tokens.C(4, 32, 28, p) : Tokens.WithAlpha(Tokens.Accent, p);
            gx.FillCircle(c, lr.Left + 24 + 5, cy, 5, dc);
            gx.DrawText(c, "DIRECTO", lr.Left + 24 + 10 + 10, gx.Baseline(lr.Top, lh, 17, 800), 17, 800, dc, 1.36f);
            if (lf) _ctx.Fx.FluidRing(c, lr, 28, p, now);
            c.Restore();
        }

        private void DrawRound(SKCanvas c, float cx, float cy, float d, Icon icon, PanelButton btn, float p, double now, bool big)
        {
            var gx = _ctx.Gx;
            bool focus = !_quiet && _ctl.Focus == btn;
            float scale = focus ? 1.08f : 1f;
            c.Save(); c.Translate(cx, cy); c.Scale(scale); c.Translate(-cx, -cy);
            var r = new SKRect(cx - d / 2, cy - d / 2, cx + d / 2, cy + d / 2);
            gx.FillRR(c, r, d / 2, Tokens.White((focus ? 0.18f : big ? 0.14f : 0.08f) * p));
            bool filled = icon == Icons.Play || icon == Icons.Pause;
            gx.DrawIcon(c, icon, cx, cy, big ? 34 : 28, Tokens.WithAlpha(Tokens.Text, p), 2f);
            if (focus) _ctx.Fx.FluidRing(c, r, d / 2, p, now);
            c.Restore();
        }

        // ---------------------------------------------------------------- error
        private void DrawErrorScreen(SKCanvas c, double now)
        {
            _ctx.DrawBackdrop(c, now);
            if (Compact)
            {
                var back = _ctx.Style.BackBox;
                _ctx.Gx.DrawIcon(c, Icons.Back, (back.Left + back.Right) / 2, (back.Top + back.Bottom) / 2, 24, Tokens.Text, 2f);
            }
            DrawErrorCard(c, now);
        }

        private void DrawErrorCard(SKCanvas c, double now)
        {
            if (Compact) { DrawErrorCardCompact(c, now); return; }
            var gx = _ctx.Gx;
            string msg = _ctl.Status.Error ?? "";
            string title = _pc.Current != null ? _pc.Current.Name : "";
            var lines = gx.WrapText(msg, 22, 600, 900, 3);
            const string hint = "OK  reintentar      Atrás  volver";
            float h = 56 + 36 + lines.Count * 30 + 28 + 40, w = 980;
            var r = new SKRect(Tokens.W / 2f - w / 2, Tokens.H / 2f - h / 2, Tokens.W / 2f + w / 2, Tokens.H / 2f + h / 2);
            gx.FillRR(c, r, 32, Tokens.C(10, 12, 20, 0.80f));
            gx.StrokeRR(c, r, 32, 1.5f, Tokens.WithAlpha(Tokens.Warn, 0.7f));
            gx.DrawIcon(c, Icons.Alert, r.Left + 52, r.Top + 56, 30, Tokens.Warn, 2f);
            gx.DrawText(c, gx.Ellipsize(title, 26, 800, w - 140), r.Left + 84, gx.Baseline(r.Top + 28, 56, 26, 800), 26, 800, Tokens.Text, -0.8f);
            for (int i = 0; i < lines.Count; i++) gx.DrawText(c, lines[i], r.Left + 40, gx.Baseline(r.Top + 88 + i * 30, 30, 22, 600), 22, 600, Tokens.TextA(0.80f));
            gx.DrawText(c, hint, r.Left + 40, gx.Baseline(r.Top + 88 + lines.Count * 30 + 14, 30, 18, 600), 18, 600, Tokens.TextA(0.48f));
        }

        // ---------------------------------------------------------------- fake video (desktop only)
        private void DrawMockVideo(SKCanvas c)
        {
            var gx = _ctx.Gx;
            if (_ctx.Surface.HasBars) gx.FillRR(c, _ctx.SurfaceLogical, 0, SKColors.Black);              // the video's own bars
            gx.FillRR(c, new SKRect(0, 0, Tokens.W, Tokens.H), 0, SKColor.Parse("#10131C"));
            if (_mockBlob == null) _mockBlob = Ambient.MakeBlob(1300, 620, 150, SKColor.Parse("#2A6F5E"), out _mockPad);
            gx.DrawImage(c, _mockBlob, new SKRect(300 - _mockPad, 160 - _mockPad, 300 + 1300 + _mockPad, 160 + 620 + _mockPad), 0.5f, SKFilterQuality.Medium);
            string t = "[ VÍDEO EN DIRECTO ]";
            gx.DrawTextAligned(c, t, Tokens.W / 2f, gx.Baseline(420, 30, 22, 600), 22, 600, Tokens.TextA(0.30f), 0.5f, 4.4f);
        }

        public string Describe()
        {
            var st = _ctl.Status;
            return "Player '" + (_pc.Current != null ? _pc.Current.Name : "") + "' mode=" + _ctl.Mode + " phase=" + st.Phase + " peers=" + st.Peers + " down=" + PlaybackStatus.FormatSpeed(st.DownBytesPerSec)
                + " window=" + (st.TimeshiftWindowMs / 1000) + "s behind=" + (st.BehindLiveMs / 1000) + "s panel=" + (_panelShown ? "yes" : "no") + (st.Error != null ? " error=" + st.Error : "");
        }
    }
}
