using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Util;
using PUi.Core;
using PUi.Gfx;
using PUi.Motion;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// Category, Explore kind or results: title, check progress card and 92 px rows that fill in discovery order (without the existing ones moving);
    /// channels without signal do not enter (counter with a crossed-out eye). Sort button at the top right (arrival, A-Z, Z-A, most peers).
    /// Only the live ones among what is opened are checked.
    /// </summary>
    public sealed class CategoryScreen : SectionScreen
    {
        private readonly string _title;
        private readonly Section _section;
        private readonly Func<CancellationToken, Task<IReadOnlyList<Channel>>> _remote;
        private List<Channel> _channels;
        private readonly ChannelListModel _model = new ChannelListModel();
        private CancellationTokenSource _cts;
        private readonly Anim _progress = new Anim(0, Bezier.Pop), _pill = new Anim(1, Bezier.Pop);
        private bool _pillHiding;
        private SortMode _sort = SortMode.Arrival;
        private readonly Focusable _sortBtn = new Focusable { Id = "sort", Group = "sort", FocusScale = 1f, Enabled = false };
        /// <summary>The sort button can only be used with 2 or more visible channels: with 0 or 1 it looks dimmed, takes no focus and navigation skips it.</summary>
        private readonly Anim _sortEnable = new Anim(0.38f, Bezier.Pop);
        public const int MinRowsToSort = 2;
        private const float HeaderY = 72, HeaderH = 58.8f;

        public CategoryScreen(string title, IReadOnlyList<Channel> channels, Section section, Func<CancellationToken, Task<IReadOnlyList<Channel>>> remote = null)
        {
            _title = title; _channels = channels.ToList(); _section = section; _remote = remote;
        }

        public override Section Section { get { return _section; } }
        public override string Name { get { return "Category " + _title; } }
        public override bool IsRoot { get { return false; } }
        public ChannelListModel Model { get { return _model; } }
        /// <summary>Sort mode of this list (always starts at arrival when a list is opened).</summary>
        public SortMode Sort { get { return _sort; } }
        public bool SortEnabled { get { return _sortBtn.Enabled; } }

        protected override void OnEnter(bool first)
        {
            _cts = new CancellationTokenSource();
            Ctx.Services.Checker.StatusChanged += OnStatus;
            Ctx.Services.Settings.Changed += MarkDirty;
            ApplyLayout();
            if (first) _model.SetChannels(_channels);
            Ctx.Services.Checker.Check(_channels);
            Refresh(Ctx.Now, first);
            if (first && _remote != null) StartRemote();
        }

        public override void Leave()
        {
            Ctx.Services.Checker.StatusChanged -= OnStatus;
            Ctx.Services.Settings.Changed -= MarkDirty;
            if (_cts != null) { _cts.Cancel(); }
            Ctx.Services.Checker.Stop();
        }

        public override void Resume() { Ctx.Services.Checker.Check(_channels); MarkDirty(); }

        private void OnStatus(Channel c, ChannelStatus st) { MarkDirty(); }

        private void StartRemote()
        {
            var ct = _cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    var extra = await _remote(ct).ConfigureAwait(false);
                    if (extra == null || extra.Count == 0 || ct.IsCancellationRequested) return;
                    extra = ChannelFilter.Apply(extra, Ctx.Services.Settings, true);
                    if (extra.Count == 0) return;
                    Ctx.Nav.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var ids = new HashSet<string>(_channels.Select(c => c.Id));
                        var add = extra.Where(c => ids.Add(c.Id)).ToList();
                        if (add.Count == 0) return;
                        _channels = _channels.Concat(add).ToList();
                        _model.SetChannels(_channels);
                        Ctx.Services.Checker.Check(_channels);
                        MarkDirty();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AceLog.Warn("ui", "remote search: " + ex.Message); }
            });
        }

        // ---------------------------------------------------------------- layout
        /// <summary>Puts the rows where this device wants them (the TV's numbers, or the phone's and tablet's).</summary>
        private void ApplyLayout()
        {
            if (Ctx.Compact)
            {
                var st = Ctx.Style;
                _model.Left = st.ListL; _model.Width = st.ListR - st.ListL;
                _model.Top = (float)Math.Round(st.HeaderCy + st.HeaderBtn / 2 + 16);
                _model.RowH = CompactCards.RowHeight(st); _model.Gap = CompactCards.RowGap(st);
                Scroll.Margin = 12;
            }
            else _model.Top = (float)Math.Round(HeaderY + HeaderH + 48);
        }

        protected override void OnSurfaceChanged(double now)
        {
            ApplyLayout();
            Refresh(now, false);
            Ctx.Nav.Invalidate();
        }

        /// <summary>The status line at the end of the rows (phone and tablet): "looking for more channels" while the check runs, and how many are hidden for having no signal.</summary>
        private const float StatusRowH = 40;

        // ---------------------------------------------------------------- logic
        private void Refresh(double now, bool first)
        {
            string focusId = Focused != null && !RailFocused ? Focused.Id : null;
            _model.Refresh(Ctx.Services.Checker, Ctx.Services.Settings.OnlyLive, _sort, now);
            LayoutHeader();
            bool canSort = _model.Visible.Count >= MinRowsToSort;
            if (canSort != _sortBtn.Enabled) { _sortBtn.Enabled = canSort; _sortEnable.Set(canSort ? 1f : 0.38f, now, 0.25); }
            Content.Clear();
            Content.Add(_sortBtn);
            foreach (var r in _model.Visible) Content.Add(r.Item);
            if (Ctx.Compact)
            {
                Scroll.ViewTop = _model.Top; Scroll.ViewBottom = Ctx.Style.Main.Bottom;
                Scroll.ContentHeight = _model.ContentBottom + (_model.Visible.Count > 0 ? StatusRowH : 0);
            }
            else
            {
                Scroll.ViewTop = Tokens.MainTop; Scroll.ViewBottom = Tokens.H - Tokens.MainBottom - (_model.Hidden > 0 ? 40 : 0);
                Scroll.ContentHeight = _model.ContentBottom;
            }
            float prog = _model.Total == 0 ? 1 : (float)_model.Done / _model.Total;
            _progress.Set(prog, now, 0.3);
            bool running = _model.Done < _model.Total;
            if (!running && !_pillHiding) { _pillHiding = true; _pill.Set(0, now, 0.4); }
            else if (running && _pillHiding) { _pillHiding = false; _pill.Set(1, now, 0.2); }

            // focus: the same channel if it is still there; otherwise the first one available (and before the user touches anything, the first channel as soon as it arrives). The sort button never gets
            // focus on its own (not even when it is enabled: the focus does not jump to it); with no channels the focus stays on the menu.
            bool lostButton = Focused == _sortBtn && !_sortBtn.Enabled;
            if (!RailFocused || first || !UserMoved || lostButton)
            {
                Focusable keep = focusId == null ? null : Content.FirstOrDefault(f => f.Id == focusId);
                if (keep != null && !keep.Enabled) keep = null;
                Focusable target = keep ?? (_model.Visible.Count > 0 ? _model.Visible[0].Item : null);
                if (target == null)
                {
                    if (!RailFocused && (first || lostButton || !UserMoved || Focused == null))
                    {
                        if (Focused != null) Focused.SetFocused(false, now, true);
                        Focused = null; FocusRail(now);
                    }
                }
                else if (target != Focused)
                {
                    if (Focused != null) Focused.SetFocused(false, now, true);
                    Focused = null; RailFocused = false;
                    SetFocus(target, now, first || keep != null);
                }
            }
        }

        // ---------------------------------------------------------------- touch
        // the sort button is part of the fixed header; the rows scroll under it and are clipped below the header
        protected override bool ScrollsWithContent(Focusable f) { return f != _sortBtn; }
        protected override SKRect? ContentClip(Focusable f, double now)
        {
            if (Ctx.Compact)
            {
                if (f == _sortBtn) return CanvasRect;
                return new SKRect(Ctx.Style.Main.Left, _model.Top - 6, Ctx.Style.Main.Right, Scroll.ViewBottom);
            }
            if (f == _sortBtn) return ContentRegion;
            return new SKRect(Tokens.MainLeft - 40, _model.Top - 30, Tokens.W, Scroll.ViewBottom + 8);
        }
        protected override float RadiusOf(Focusable f) { return f == _sortBtn ? f.R.Height / 2 : Ctx.Compact ? 20 : 24; }

        // ---------------------------------------------------------------- sort button (top right)
        private void LayoutHeader()
        {
            if (Ctx.Compact)
            {
                var st = Ctx.Style;
                float hh = st.HeaderBtn, ww = CompactCards.SortWidth(Ctx.Gx, _sort, hh);
                _sortBtn.R = new SKRect(st.HeaderRight - ww, st.HeaderCy - hh / 2, st.HeaderRight, st.HeaderCy + hh / 2);
                return;
            }
            float w = Cards.SortButtonWidth(Ctx.Gx, _sort), h = 54;
            float x1 = Tokens.W - Tokens.MainRight, y = HeaderY + (HeaderH - h) / 2;
            _sortBtn.R = new SKRect(x1 - w, y, x1, y + h);
        }

        private void CycleSort(double now)
        {
            _sort = (SortMode)(((int)_sort + 1) % 4);
            AceLog.Info("ui", "list order: " + _sort);
            Refresh(now, false);
            Ctx.Nav.Invalidate();
        }

        protected override Focusable FirstFocusable() { return _model.Visible.Count > 0 ? _model.Visible[0].Item : null; }

        protected override void OnUpdate(double now)
        {
            if (TakeDirty()) Refresh(now, false);
        }

        protected override bool ShowsProgress(double now) { return _pill.Value(now) > 0.01f || _model.AnyChecking; }

        protected override bool ExtraAnimating(double now) { return _model.Animating(now) || _progress.Active(now) || _pill.Active(now) || _sortEnable.Active(now); }

        protected override void Activate(Focusable item)
        {
            if (item == _sortBtn) { if (_sortBtn.Enabled) CycleSort(Ctx.Now); return; }
            var row = _model.Visible.FirstOrDefault(r => r.Item == item);
            if (row == null) return;
            if (CastRedirect(row.Channel)) return;                  // sending to another screen: the channel goes there, not to the player
            var list = _model.VisibleChannels();
            Ctx.Nav.Push(new PlayerScreen(new PlayContext(list, list.IndexOf(row.Channel))));
        }

        // ---------------------------------------------------------------- drawing
        // phone and tablet: the title, the back arrow and the sort button are the fixed header
        protected override void DrawHeader(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            var gx = Ctx.Gx;
            DrawChrome(c, now);
            float size = st.Phone ? 24 : 28, ls = -size * 0.02f;
            float avail = _sortBtn.R.Left - 12 - st.TitleX;
            string t = gx.Ellipsize(_title, size, 800, avail);
            gx.DrawText(c, t, st.TitleX, gx.Baseline(st.HeaderCy - 22, 44, size, 800), size, 800, Tokens.Text, ls);
            Cards.SortButton(Ctx, c, _sortBtn, _sort, now, st.HeaderBtn, false, _sortEnable.Value(now));
        }

        private void DrawCompactContent(SKCanvas c, double now)
        {
            var gx = Ctx.Gx;
            var st = Ctx.Style;
            float sy = Scroll.Offset(now);
            c.Save();
            c.ClipRect(new SKRect(st.Main.Left, _model.Top - 6, st.Main.Right, Scroll.ViewBottom + 8));
            c.Translate(0, -sy);
            RowListView.Draw(Ctx, c, _model, Focused, now, sy);
            // status line after the rows: the number hidden for having no signal and, while checking, a dot that breathes
            if (_model.Visible.Count > 0 || _model.Hidden > 0 || _pill.Value(now) > 0.01f)
            {
                float y = _model.ContentBottom - _model.Gap + 4;
                float cy = y + StatusRowH / 2 - 2;
                float a = _pill.Value(now);
                if (_model.Hidden > 0)
                {
                    gx.DrawIcon(c, Icons.EyeOff, _model.Left + 12, cy, 16, Tokens.TextA(0.58f), 1.8f);
                    gx.DrawText(c, _model.Hidden.ToString(), _model.Left + 26, gx.Baseline(cy - 11, 22, 13, 600), 13, 600, Tokens.TextA(0.58f));
                }
                if (a > 0.01f)
                {
                    double ph = now % 1.2 / 1.2;
                    float breathe = 0.25f + 0.75f * (float)(0.5 - 0.5 * Math.Cos(ph * 2 * Math.PI));
                    gx.FillCircle(c, _model.Left + _model.Width / 2, cy, 3.5f, Tokens.WithAlpha(Tokens.Accent2, breathe * a));
                }
            }
            c.Restore();
        }

        protected override void DrawContent(SKCanvas c, double now)
        {
            if (Ctx.Compact) { DrawCompactContent(c, now); return; }
            var gx = Ctx.Gx;
            float sy = Scroll.Offset(now);
            // fixed header: title and progress card
            gx.DrawText(c, gx.Ellipsize(_title, 56, 800, 1180), Tokens.MainLeft, gx.Baseline(HeaderY, HeaderH, 56, 800), 56, 800, Tokens.Text, -1.68f);
            DrawProgress(c, now);
            Cards.SortButton(Ctx, c, _sortBtn, _sort, now, 54, false, _sortEnable.Value(now));

            c.Save();
            c.ClipRect(new SKRect(Tokens.MainLeft - 40, _model.Top - 30, Tokens.W, Scroll.ViewBottom + 8));
            c.Translate(0, -sy);
            RowListView.Draw(Ctx, c, _model, Focused, now, sy);
            c.Restore();

            if (_model.Hidden > 0) DrawHidden(c);
        }

        private void DrawProgress(SKCanvas c, double now)
        {
            float a = _pill.Value(now);
            if (a <= 0.01f) return;
            var gx = Ctx.Gx;
            const float w = 320, h = 54;
            float x = _sortBtn.R.Left - 20 - w, y = HeaderY + (HeaderH - h) / 2;
            var r = new SKRect(x, y, x + w, y + h);
            gx.FillRR(c, r, 22, Tokens.White(0.06f * a));
            gx.StrokeRR(c, r, 22, 1, Tokens.White(0.10f * a));
            Ctx.Fx.Spinner(c, x + 22 + 9, y + h / 2, 18, now, Tokens.WithAlpha(Tokens.Accent2, a));
            float bx = x + 22 + 18 + 16, bw = w - 44 - 18 - 16;
            gx.FillRR(c, new SKRect(bx, y + h / 2 - 3, bx + bw, y + h / 2 + 3), 3, Tokens.White(0.10f * a));
            float fill = Math.Max(0.02f, _progress.Value(now));
            gx.FillRR(c, new SKRect(bx, y + h / 2 - 3, bx + bw * fill, y + h / 2 + 3), 3, Tokens.WithAlpha(Tokens.Accent2, a));
        }

        private void DrawHidden(SKCanvas c)
        {
            var gx = Ctx.Gx;
            float y = Tokens.H - Tokens.MainBottom - 12;
            gx.DrawIcon(c, Icons.EyeOff, Tokens.MainLeft + 10, y, 20, Tokens.TextA(0.58f), 1.8f);
            gx.DrawText(c, _model.Hidden.ToString(), Tokens.MainLeft + 28, gx.Baseline(y - 14, 28, 16, 600), 16, 600, Tokens.TextA(0.58f));
        }

        public override string Describe()
        {
            var s = Ctx != null ? Ctx.Services.Checker : null;
            return base.Describe() + " '" + _title + "' channels=" + _model.Total + " visible=" + _model.Visible.Count + " live=" + _model.Visible.Count(r => r.Status.IsLive)
                + " hidden=" + _model.Hidden + " checked=" + _model.Done + "/" + _model.Total;
        }
    }
}
