using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
using PUi.Core;
using PUi.Gfx;
using PUi.Motion;
using PUi.Platform;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// TV section (design Main and MainLoading): recently watched (132 px cards with blue border, checked first and in a batch), an animated strip and
    /// the categories of the unified list (thin 80 px cards with an arrow). While the list loads, skeletons with shimmer and an indeterminate bar.
    /// On a phone or a tablet the same page has its own measures (design-movil): the recents in a row that slides sideways (or 3 / 4 cards across on a tablet), the categories in 2, 3 or
    /// 4 columns, the sections along the bottom or down a rail. The data, the live check and the order of discovery are the same.
    /// </summary>
    public sealed class MainScreen : SectionScreen
    {
        private const float Cw = 302.4f, RecentPitchTv = Cw + 28, Gw = 305.6f, CatPitchXTv = Gw + 24, CatPitchYTv = 104, RecentTopTv = 96, RecentHTv = 132, DividerYTv = 280;
        private const int VisibleRecentsTv = 5;

        /// <summary>Where the page puts things: the TV's numbers, or the phone's and tablet's worked out from the canvas. Rebuilt with the layout.</summary>
        private struct Geo
        {
            public float Left, Width;                              // x extent of the grid, the strip's cards and the divider
            public float RecentTop, RecentH, RecentW, RecentPitch, RecentGap;
            public int Visible;                                    // recent cards that fit at once
            public float DividerY, CatTopWith, CatTopWithout;
            public int Cols;
            public float CatW, CatH, CatPitchX, CatPitchY;
            public float ViewTop, ViewBottom;                      // the scrolling viewport
            public SKRect StripRegion;                             // where the recents show and where a sideways drag moves them (content coordinates)
            public float StripPadRight;                            // empty space after the last card when slid all the way (phones)
            public float FocusScaleRecent, FocusScaleGrid;
        }

        private Geo _g = TvGeo();

        private static Geo TvGeo()
        {
            return new Geo
            {
                Left = Tokens.MainLeft, Width = Tokens.MainWidth, RecentTop = RecentTopTv, RecentH = RecentHTv, RecentW = Cw, RecentPitch = RecentPitchTv, RecentGap = 28, Visible = VisibleRecentsTv,
                DividerY = DividerYTv, CatTopWith = 326, CatTopWithout = RecentTopTv, Cols = 5, CatW = Gw, CatH = 80, CatPitchX = CatPitchXTv, CatPitchY = CatPitchYTv,
                ViewTop = Tokens.MainTop, ViewBottom = Tokens.H - Tokens.MainBottom,
                StripRegion = new SKRect(Tokens.MainLeft - 28, RecentTopTv - 40, Tokens.W, RecentTopTv + RecentHTv + 60), FocusScaleRecent = 1.06f, FocusScaleGrid = 1.05f
            };
        }

        /// <summary>The phone's and tablet's geometry (design-movil Main, TabletHome and TabletPortraitHome).</summary>
        private static Geo CompactGeo(CompactStyle st)
        {
            var g = new Geo();
            g.Left = st.HomeL; g.Width = st.HomeR - st.HomeL;
            g.Cols = st.Cols;
            if (st.Phone)
            {
                g.RecentTop = st.Main.Top + 4; g.RecentH = 108; g.RecentW = 200; g.RecentGap = 12; g.RecentPitch = 212; g.Visible = Math.Max(1, (int)Math.Floor((g.Width + g.RecentGap) / g.RecentPitch));
                g.CatH = 60; g.CatPitchY = 72; float gap = 12; g.CatW = (g.Width - gap * (g.Cols - 1)) / g.Cols; g.CatPitchX = g.CatW + gap;
                g.StripPadRight = st.HomeL;
                g.DividerY = g.RecentTop + g.RecentH + 26; g.CatTopWith = g.DividerY + 2 + 24; g.CatTopWithout = st.Main.Top + 4;
            }
            else
            {
                bool land = st.SideRail;
                float rgap = land ? 18 : 16, cgap = land ? 16 : 14;
                g.RecentTop = land ? 32 : st.Main.Top + 8; g.RecentH = land ? 120 : 124; g.RecentGap = rgap; g.RecentW = (g.Width - rgap * (g.Cols - 1)) / g.Cols; g.RecentPitch = g.RecentW + rgap; g.Visible = g.Cols;
                g.CatH = 68; g.CatPitchY = 68 + cgap; g.CatW = (g.Width - cgap * (g.Cols - 1)) / g.Cols; g.CatPitchX = g.CatW + cgap;
                g.DividerY = g.RecentTop + g.RecentH + (land ? 36 : 34); g.CatTopWith = g.DividerY + 2 + (land ? 32 : 30); g.CatTopWithout = land ? 32 : st.Main.Top + 8;
                g.StripPadRight = 0;
            }
            g.ViewTop = st.Main.Top; g.ViewBottom = st.Main.Bottom;
            g.StripRegion = new SKRect(st.Main.Left, g.RecentTop - 12, st.Main.Right, g.RecentTop + g.RecentH + 24);
            g.FocusScaleRecent = 1.04f; g.FocusScaleGrid = 1.03f;
            return g;
        }

        private readonly List<Focusable> _catItems = new List<Focusable>();
        private readonly RecentsModel _rmodel = new RecentsModel();
        private List<Category> _cats = new List<Category>();
        private bool _loadingCatalog, _failed;
        private readonly Anim _gridShift = new Anim(0, Bezier.Pop);      // when the recents appear/disappear, categories and strip slide
        private bool _hadRecents;
        private int _recentCount { get { return _rmodel.Visible.Count; } }
        private readonly Anim _shift = new Anim(0, Bezier.Pop);     // horizontal shift of the recents, in cards
        private int _shiftTarget;
        private float _catTop = 326;

        public MainScreen()
        {
            // the recent cards also scroll sideways by touch (the position is in pixels; _shift counts cards)
            _recentsStrip = new HStrip
            {
                Region = () => TouchHit.Offset(_g.StripRegion, 0, -Scroll.Offset(Ctx.Now)),
                Max = () => StripMax(),
                Get = now => _shift.Value(now) * _g.RecentPitch,
                Set = v => { _shift.Snap(v / _g.RecentPitch); _shiftTarget = Math.Max(0, Math.Min(Math.Max(0, _recentCount - _g.Visible), (int)Math.Round(v / _g.RecentPitch))); }
            };
            _strips = new[] { _recentsStrip };
        }

        /// <summary>The farthest the recents slide: the TV scrolls card by card; a phone or a tablet until the last card is against the end of the row.</summary>
        private float StripMax()
        {
            if (Ctx == null || !Ctx.Compact) return Math.Max(0, _recentCount - _g.Visible) * _g.RecentPitch;
            float total = _recentCount * _g.RecentPitch - _g.RecentGap + _g.StripPadRight;
            return Math.Max(0, total - (_g.Width + (Ctx.Style.Phone ? _g.StripPadRight : 0)));
        }

        /// <summary>How far the recent cards are slid sideways, in cards (for the tests and the log).</summary>
        public float RecentsShift { get { return Ctx == null ? 0 : _shift.Value(Ctx.Now); } }
        /// <summary>The farthest the recents can slide, in cards (for the tests).</summary>
        public float RecentsShiftMax { get { return _g.RecentPitch <= 0 ? 0 : StripMax() / _g.RecentPitch; } }

        private readonly HStrip _recentsStrip;
        private readonly HStrip[] _strips;
        protected override IReadOnlyList<HStrip> Strips { get { return _strips; } }

        public override Section Section { get { return Section.Tv; } }
        public override string Name { get { return "TV"; } }

        // ---------------------------------------------------------------- touch: where the cards are on the screen
        protected override SKRect ScreenRect(Focusable f, double now, float scrollY)
        {
            if (f.Group == "recents") return TouchHit.Offset(f.R, -_shift.Value(now) * _g.RecentPitch, -scrollY);
            return TouchHit.Offset(f.R, 0, _gridShift.Value(now) - scrollY);
        }

        protected override SKRect? ContentClip(Focusable f, double now)
        {
            if (f.Group != "recents") return ContentRegion;
            return TouchHit.Offset(_g.StripRegion, 0, -Scroll.Offset(now));
        }

        protected override float RadiusOf(Focusable f) { return Ctx.Compact ? (f.Group == "recents" ? (Ctx.Style.Phone ? 22 : 24) : (Ctx.Style.Phone ? 18 : 20)) : f.Group == "recents" ? 28 : 24; }

        protected override SKRect? CastBox { get { return Ctx.Compact ? (Ctx.Style.SideRail ? Ctx.Style.RailCast : Ctx.Style.HeaderCastBox) : (SKRect?)null; } }

        protected override void OnEnter(bool first)
        {
            var s = Ctx.Services;
            s.Catalog.Changed += MarkDirty;
            s.Recents.Changed += MarkDirty;
            s.Settings.Changed += MarkDirty;
            s.Checker.StatusChanged += OnStatus;
            Layout();
            Rebuild(Ctx.Now, first);
            CheckRecents();
        }

        public override void Leave()
        {
            var s = Ctx.Services;
            s.Catalog.Changed -= MarkDirty;
            s.Recents.Changed -= MarkDirty;
            s.Settings.Changed -= MarkDirty;
            s.Checker.StatusChanged -= OnStatus;
            s.Checker.Stop();                                   // on leaving the screen what is being checked (the recents) is cancelled: nothing stays in the background
        }

        public override void Resume() { CheckRecents(); MarkDirty(); }

        private void OnStatus(Channel c, ChannelStatus st) { MarkDirty(); }

        private void CheckRecents()
        {
            var list = ChannelFilter.Apply(Ctx.Services.Recents.Items, Ctx.Services.Settings, false);      // the minimum resolution counts; the language does not
            _rmodel.SetStored(list);
            if (list.Count > 0) Ctx.Services.Checker.Check(_rmodel.ToCheck());
        }

        // ---------------------------------------------------------------- geometry
        /// <summary>Works the page's measures out for the current device (a TV keeps its own).</summary>
        private void Layout()
        {
            _g = Ctx.Compact ? CompactGeo(Ctx.Style) : TvGeo();
            _rmodel.Left = _g.Left; _rmodel.Pitch = _g.RecentPitch; _rmodel.Top = _g.RecentTop; _rmodel.W = _g.RecentW; _rmodel.H = _g.RecentH; _rmodel.FocusScale = _g.FocusScaleRecent;
            Scroll.Margin = Ctx.Compact ? 12 : 28;
        }

        protected override void OnSurfaceChanged(double now)
        {
            Layout();
            Rebuild(now, false);
            // the slid recents may no longer be in range
            float max = RecentsShiftMax;
            if (_shift.Target > max) _shift.Snap(max);
            Ctx.Nav.Invalidate();
        }

        // ---------------------------------------------------------------- data and layout
        private void Rebuild(double now, bool first)
        {
            var s = Ctx.Services;
            string focusId = Focused != null && !RailFocused ? Focused.Id : null;
            int focusIdx = Focused != null && Focused.Group == "recents" ? (int)Focused.Tag : -1;
            _cats = ChannelFilter.VisibleCategories(s.Catalog.Categories, s.Settings);
            _loadingCatalog = s.Catalog.State == CatalogState.Loading && _cats.Count == 0;
            _failed = s.Catalog.State == CatalogState.Failed && _cats.Count == 0;
            _rmodel.SetStored(ChannelFilter.Apply(s.Recents.Items, s.Settings, false));
            _rmodel.Refresh(s.Checker, _loadingCatalog, now);
            ResetWarm();

            var old = new Dictionary<string, Focusable>();
            foreach (var f in _catItems) old[f.Id] = f;
            _catItems.Clear(); Content.Clear();
            bool hasRecents = _recentCount > 0 || _rmodel.Fading.Count > 0;
            float newTop = hasRecents ? _g.CatTopWith : _g.CatTopWithout;
            if (!first && Math.Abs(newTop - _catTop) > 0.5f) { _gridShift.Snap(_catTop - newTop + _gridShift.Value(now)); _gridShift.Set(0, now, 0.3); }
            _catTop = newTop; _hadRecents = hasRecents;
            for (int i = 0; i < _cats.Count; i++)
            {
                int col = i % _g.Cols, row = i / _g.Cols;
                float x = _g.Left + col * _g.CatPitchX, y = _catTop + row * _g.CatPitchY;
                _catItems.Add(Reuse(old, "c" + _cats[i].Key, "grid", _g.FocusScaleGrid, new SKRect(x, y, x + _g.CatW, y + _g.CatH), i));
            }
            foreach (var rc in _rmodel.Visible) Content.Add(rc.Item);
            Content.AddRange(_catItems);
            int rows = (_cats.Count + _g.Cols - 1) / _g.Cols;
            Scroll.ViewTop = _g.ViewTop; Scroll.ViewBottom = _g.ViewBottom;
            Scroll.ContentHeight = _catTop + Math.Max(0, rows) * _g.CatPitchY;

            if (!RailFocused || first || !UserMoved)
            {
                Focusable keep = focusId == null ? null : Content.FirstOrDefault(f => f.Id == focusId);
                if (keep != null && ReferenceEquals(keep, Focused) && !first) { /* same element: its focus animation keeps going without restarting */ }
                else
                {
                    Focused = null; RailFocused = false;
                    Focusable target = keep;
                    if (target == null && focusIdx >= 0 && !first)
                    {
                        // the focused recent disappeared (no peers): the focus goes to the one that takes its place, or the previous one, or the categories
                        var vis = _rmodel.Visible;
                        if (vis.Count > 0) target = vis[Math.Min(focusIdx, vis.Count - 1)].Item;
                        else if (_catItems.Count > 0) target = _catItems[0];
                    }
                    if (target == null) target = _rmodel.Visible.Count > 0 ? _rmodel.Visible[0].Item : _catItems.Count > 0 ? _catItems[0] : null;
                    if (target != null) SetFocus(target, now, first || keep != null);
                    else if (first) FocusRail(now);
                }
            }
        }

        /// <summary>Reuses the Focusable from the previous rebuild (keeps its focus animations: they do not restart when a probe result arrives).</summary>
        private static Focusable Reuse(Dictionary<string, Focusable> old, string id, string group, float scale, SKRect r, int tag)
        {
            Focusable f;
            if (!old.TryGetValue(id, out f)) f = new Focusable { Id = id, Group = group, FocusScale = scale };
            f.FocusScale = scale;
            f.R = r; f.Tag = tag;
            return f;
        }

        protected override void WarmItem(SKCanvas c, Focusable f, double now)
        {
            if (f.Group == "recents") { var rc = _rmodel.Visible.FirstOrDefault(x => x.Item == f); if (rc != null) Cards.Recent(Ctx, c, f, rc.View, now, 1f, true); }
            else if (f.Group == "grid" && !_loadingCatalog) Cards.Category(Ctx, c, f, _cats[(int)f.Tag].Name, now, true);
        }

        protected override void OnUpdate(double now)
        {
            if (TakeDirty()) Rebuild(now, false);
        }

        protected override bool ShowsProgress(double now) { return _loadingCatalog || _rmodel.AnyChecking; }

        protected override bool ExtraAnimating(double now) { return _shift.Active(now) || _gridShift.Active(now) || _rmodel.Animating(now); }

        protected override void OnFocusChanged(Focusable f, double now)
        {
            if (f.Group != "recents" || PointerFocus) return;
            if (Ctx.Compact)
            {
                // slide just enough for the focused card to be seen whole
                float off = _shift.Target * _g.RecentPitch, left = _g.Left, right = _g.Left + _g.Width + (Ctx.Style.Phone ? _g.StripPadRight : 0);
                if (f.R.Left - off < left) off = f.R.Left - left;
                else if (f.R.Right - off > right) off = f.R.Right - right;
                off = Math.Max(0, Math.Min(off, StripMax()));
                _shift.Set(off / _g.RecentPitch, now, 0.3);
                return;
            }
            int i = (int)f.Tag, n = _recentCount;
            if (n <= _g.Visible) { _shiftTarget = 0; }
            else if (i > _shiftTarget + _g.Visible - 1) _shiftTarget = i - (_g.Visible - 1);
            else if (i < _shiftTarget) _shiftTarget = i;
            _shift.Set(_shiftTarget, now, 0.3);
        }

        protected override void Activate(Focusable item)
        {
            if (item.Group == "recents")
            {
                var recents = _rmodel.Visible.Select(x => x.Channel).ToList();
                if (CastRedirect(recents[(int)item.Tag])) return;                  // sending to another screen: the channel goes there, not to the player
                Ctx.Nav.Push(new PlayerScreen(new PlayContext(recents, (int)item.Tag)));
            }
            else if (item.Group == "grid") Ctx.Nav.Push(new CategoryScreen(_cats[(int)item.Tag].Name, ChannelFilter.Apply(_cats[(int)item.Tag].Channels, Ctx.Services.Settings, true), Section.Tv));
        }

        // ---------------------------------------------------------------- drawing
        protected override void DrawHeader(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            if (!st.SideRail) DrawLogoAt(c, st.HomeL, st.HeaderCy, st.Phone ? 34 : 44);
            DrawCast(c);
        }

        protected override void DrawContent(SKCanvas c, double now)
        {
            float sy = Scroll.Offset(now);
            c.Save();
            c.Translate(0, -sy);
            if (_recentCount > 0 || _rmodel.Fading.Count > 0)
            {
                float shift = _shift.Value(now) * _g.RecentPitch;
                c.Save();
                c.ClipRect(_g.StripRegion);
                c.Translate(-shift, 0);
                foreach (var rc in _rmodel.Fading) DrawRecent(c, rc, now);
                foreach (var rc in _rmodel.Visible) if (rc.Item != Focused) DrawRecent(c, rc, now);
                if (Focused != null && Focused.Group == "recents") { var fr = _rmodel.Visible.FirstOrDefault(x => x.Item == Focused); if (fr != null) DrawRecent(c, fr, now); }
                c.Restore();
            }
            c.Save();
            c.Translate(0, _gridShift.Value(now));
            if (_hadRecents) Ctx.Fx.Divider(c, _g.Left, _g.DividerY, _g.Width, now);
            if (_loadingCatalog || _failed && _cats.Count == 0) DrawSkeletons(c, now);
            else
            {
                float viewH = Ctx.Compact ? Ctx.CH : Tokens.H;
                foreach (var f in _catItems)
                {
                    if (f == Focused) continue;
                    if (f.R.Bottom - sy < -40 || f.R.Top - sy > viewH + 40) continue;
                    Cards.Category(Ctx, c, f, _cats[(int)f.Tag].Name, now);
                }
                if (Focused != null && Focused.Group == "grid") Cards.Category(Ctx, c, Focused, _cats[(int)Focused.Tag].Name, now);
            }
            c.Restore();
            c.Restore();
            if (_loadingCatalog) DrawIndeterminate(c, now);
            if (_failed) DrawError(c);
        }

        private void DrawRecent(SKCanvas c, RecentCard rc, double now)
        {
            float alpha = rc.Appear.Value(now) * rc.Fade.Value(now);
            if (alpha <= 0.003f) return;
            c.Save();
            c.Translate(rc.X.Value(now) - rc.TargetX, (1 - rc.Appear.Value(now)) * 24);
            Cards.Recent(Ctx, c, rc.Item, rc.View, now, alpha);
            c.Restore();
        }

        private static readonly float[] SkeletonWidths = { 72, 54, 80, 46, 64, 58, 76, 42, 66, 52 };

        private void DrawSkeletons(SKCanvas c, double now)
        {
            var gx = Ctx.Gx;
            int n = Ctx.Compact ? Math.Max(_g.Cols * 3, 6) : 10;
            float rad = Ctx.Compact ? (Ctx.Style.Phone ? 18 : 20) : 24, padL = Ctx.Compact ? 18 : 28;
            for (int i = 0; i < n; i++)
            {
                int col = i % _g.Cols, row = i / _g.Cols;
                float x = _g.Left + col * _g.CatPitchX, y = _catTop + row * _g.CatPitchY;
                var r = new SKRect(x, y, x + _g.CatW, y + _g.CatH);
                gx.FillRR(c, r, rad, Tokens.White(0.04f));
                gx.StrokeRR(c, r, rad, 1, Tokens.White(0.07f));
                float inner = _g.CatW - 2 * padL;
                float barH = Ctx.Compact ? 16 : 22;
                Ctx.Fx.Shimmer(c, new SKRect(x + padL, y + _g.CatH / 2 - barH / 2, x + padL + inner * SkeletonWidths[i % SkeletonWidths.Length] / 100f, y + _g.CatH / 2 + barH / 2), barH / 2, now);
            }
        }

        private void DrawIndeterminate(SKCanvas c, double now)
        {
            float x = _g.Left, y = Ctx.Compact ? _g.ViewBottom - 10 : 1020; const float w = 160, h = 4;
            Ctx.Gx.FillRR(c, new SKRect(x, y, x + w, y + h), 2, Tokens.White(0.10f));
            double ph = (now % 1.3) / 1.3;
            float e = Bezier.Material[ph];
            float bw = w * 0.4f, off = -bw + e * (bw * 3.6f);          // translateX(-100% -> 260%) of its own width
            c.Save(); c.ClipRect(new SKRect(x, y, x + w, y + h));
            Ctx.Gx.FillRR(c, new SKRect(x + off, y, x + off + bw, y + h), 2, Tokens.Accent2);
            c.Restore();
        }

        private void DrawError(SKCanvas c)
        {
            string msg = Ctx.Services.Catalog.Error;
            if (string.IsNullOrEmpty(msg)) return;
            if (Ctx.Compact) { Ctx.Gx.DrawText(c, Ctx.Gx.Ellipsize(msg, 13, 600, _g.Width), _g.Left, _g.ViewBottom - 8, 13, 600, Tokens.TextA(0.58f)); return; }
            Ctx.Gx.DrawText(c, Ctx.Gx.Ellipsize(msg, 16, 600, 900), Tokens.MainLeft, 1012, 16, 600, Tokens.TextA(0.58f));
        }

        public override string Describe()
        {
            return base.Describe() + " recents=" + _recentCount + "/" + _rmodel.Stored.Count + " categories=" + _cats.Count + (_loadingCatalog ? " loading" : "");
        }
    }
}
