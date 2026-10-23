using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// Search (design Search and SearchResults): field with fluid border, recent searches and Explore by kinds; as you type, instant results over the
    /// channels of the lists (and providers if any) with the live ones being checked. Own on-screen keyboard, and the remote's keys type directly.
    /// </summary>
    public sealed class SearchScreen : SectionScreen
    {
        private const float FieldY = 96, FieldH = 96, ChipsY = 214, ChipH = 38;
        private readonly Focusable _field = new Focusable { Id = "field", Group = "field", FocusScale = 1f, R = new SKRect(Tokens.MainLeft, FieldY, Tokens.MainLeft + Tokens.MainWidth, FieldY + FieldH) };
        private readonly List<Focusable> _chips = new List<Focusable>();
        private readonly List<Focusable> _tiles = new List<Focusable>();
        private List<string> _chipTexts = new List<string>();
        private IReadOnlyList<ExploreKind> _kinds;
        private string _query = "";
        private bool ResultsMode { get { return _query.Length > 0; } }
        private readonly ChannelListModel _model = new ChannelListModel { Top = 236 };

        // where things are: the TV's numbers until Layout() puts a phone's or a tablet's
        private float _fieldY = FieldY, _fieldH = FieldH, _chipsY = ChipsY, _chipH = ChipH, _left = Tokens.MainLeft, _right = Tokens.MainLeft + Tokens.MainWidth, _rowsTop = 236, _chipGap = 12;
        private int _cols = 4;
        private float _tileH = 80, _tileGapX = 24, _tilePitchY = 104, _gridGap = 56;
        private float _keyW = 96, _keyH = 68, _keyGap = 10, _kbPad = 28;
        private List<Channel> _results = new List<Channel>();
        private OnScreenKeyboard _kb;
        private CancellationTokenSource _remoteCts;
        private readonly Anim _kbAnim = new Anim(0, Bezier.Pop);
        /// <summary>Horizontal shift of the recent searches (up to 10): if they do not fit on one line, the focus slides them.</summary>
        private readonly Anim _chipOff = new Anim(0, Bezier.Pop);

        public SearchScreen()
        {
            // the recent searches slide sideways by touch when they do not fit on one line
            _chipStrip = new HStrip
            {
                Region = () => TouchHit.Offset(ChipsRegion, 0, -Scroll.Offset(Ctx.Now)),
                Max = () => ChipsMaxOffset,
                Get = now => _chipOff.Value(now),
                Set = v => _chipOff.Snap(v)
            };
            _strips = new[] { _chipStrip };
        }

        private readonly HStrip _chipStrip;
        private readonly HStrip[] _strips;
        protected override IReadOnlyList<HStrip> Strips { get { return _strips; } }
        private SKRect ChipsRegion
        {
            get
            {
                if (Ctx != null && Ctx.Compact) return new SKRect(Ctx.Style.Main.Left, _chipsY - 12, Ctx.Style.Main.Right, _chipsY + _chipH + 12);
                return new SKRect(Tokens.MainLeft - 14, ChipsY - 40, Tokens.MainLeft + Tokens.MainWidth + 14, ChipsY + ChipH + 40);
            }
        }

        /// <summary>Puts the page's measures where this device wants them: the TV's constants, or the phone's and tablet's (design-movil PhoneSearch).</summary>
        private void Layout()
        {
            if (!Ctx.Compact) return;
            var st = Ctx.Style;
            _left = st.ListL; _right = st.ListR;
            _fieldY = st.Phone ? 8 : st.SideRail ? 32 : 16; _fieldH = st.Phone ? 54 : 58;
            _chipH = 44; _chipsY = _fieldY + _fieldH + 16; _chipGap = 8;
            _rowsTop = _fieldY + _fieldH + 14;
            _cols = st.Cols; _tileH = 72; _tileGapX = st.Phone ? 10 : 12; _tilePitchY = _tileH + _tileGapX; _gridGap = 22;
            _field.R = new SKRect(_left, _fieldY, _right, _fieldY + _fieldH);
            _model.Left = _left; _model.Width = _right - _left; _model.Top = _rowsTop; _model.RowH = CompactCards.RowHeight(st); _model.Gap = CompactCards.RowGap(st);
            // PUi's own keyboard, for a touch device without a system one: as wide as the keys allow
            float panelW = Math.Min(st.ListR - st.ListL, st.Phone ? 400 : 640);
            _keyGap = st.Phone ? 6 : 8; _kbPad = st.Phone ? 12 : 20;
            _keyW = (panelW - 2 * _kbPad - 9 * _keyGap) / 10f; _keyH = st.Phone ? 44 : 52;
            Scroll.Margin = 12;
        }

        protected override void OnSurfaceChanged(double now)
        {
            Layout();
            Build();
            _chipOff.Snap(Math.Max(0, Math.Min(_chipOff.Target, ChipsMaxOffset)));
            Ctx.Nav.Invalidate();
        }

        public override Section Section { get { return Section.Search; } }
        public override string Name { get { return ResultsMode ? "Search '" + _query + "'" : "Search"; } }
        public override bool IsRoot { get { return true; } }

        protected override void OnEnter(bool first)
        {
            Ctx.Services.Checker.StatusChanged += OnStatus;
            Ctx.Services.Settings.Changed += MarkDirty;
            Layout();
            UpdateKinds();
            if (first) { Build(); SetFocus(_field, Ctx.Now, true); }
            else
            {
                // the settings (minimum resolution, language) may have changed while this screen was not active
                if (ResultsMode) { _results = ChannelFilter.Apply(Ctx.Services.Search.Search(_query), Ctx.Services.Settings, true); _model.SetChannels(_results); if (_results.Count > 0) Ctx.Services.Checker.Check(_results); }
                Build();
                MarkDirty();
            }
        }

        public override void Suspend() { base.Suspend(); CloseIme(true, true); }

        public override void Leave()
        {
            CloseIme(true, true);
            Ctx.Services.Checker.StatusChanged -= OnStatus;
            Ctx.Services.Settings.Changed -= MarkDirty;
            if (_remoteCts != null) _remoteCts.Cancel();
            Ctx.Services.Checker.Stop();                        // on leaving the screen what is being checked (the results) is cancelled: nothing stays in the background
        }

        private void OnStatus(Channel c, ChannelStatus st) { MarkDirty(); }

        /// <summary>The Explore kinds that are shown: all of them, or with the Settings filters only those that keep some channel.</summary>
        private void UpdateKinds()
        {
            var all = Ctx.Services.Search.Kinds;
            var s = Ctx.Services.Settings;
            if (!ChannelFilter.Active(s, true)) { _kinds = all; return; }
            _kinds = all.Where(k => ChannelFilter.Apply(Ctx.Services.Search.Explore(k.Id), s, true).Count > 0).ToList();
        }

        // ---------------------------------------------------------------- layout
        private void Build()
        {
            string focusId = Focused != null && !RailFocused ? Focused.Id : null;
            Content.Clear(); Content.Add(_field);
            _chips.Clear(); _tiles.Clear();
            var gx = Ctx.Gx;
            _chipTexts = Ctx.Services.Search.RecentQueries.ToList();
            float x = _left;
            bool compact = Ctx.Compact;
            // with results, the recent searches are not shown (the design removes them and the results start right below the field)
            foreach (var t in ResultsMode ? new List<string>() : _chipTexts)
            {
                float w = compact ? gx.MeasureText(t, 14, 600) + 16 + 15 + 8 + 16 : gx.MeasureText(t, 16, 600) + 32;
                _chips.Add(new Focusable { Id = "chip:" + t, Group = "chips", FocusScale = 1f, R = new SKRect(x, _chipsY, x + w, _chipsY + _chipH), Tag = t });
                x += w + _chipGap;
            }
            Content.AddRange(_chips);
            if (_chipOff.Target > ChipsMaxOffset) _chipOff.Set(ChipsMaxOffset, Ctx.Now, 0.2);
            float gridTop = compact ? (_chips.Count > 0 ? _chipsY + _chipH + _gridGap : _chipsY) : _chips.Count > 0 ? ChipsY + ChipH + 56 : FieldY + FieldH + 56;
            if (!ResultsMode)
            {
                int cols = compact ? _cols : 4;
                float tw = compact ? (_right - _left - _tileGapX * (cols - 1)) / cols : (Tokens.MainWidth - 72) / 4f;
                for (int i = 0; i < _kinds.Count; i++)
                {
                    int col = i % cols, row = i / cols;
                    float tx = _left + col * (tw + _tileGapX), ty = gridTop + row * _tilePitchY;
                    _tiles.Add(new Focusable { Id = "k:" + _kinds[i].Id, Group = "grid", FocusScale = compact ? 1.03f : 1.05f, R = new SKRect(tx, ty, tx + tw, ty + _tileH), Tag = i });
                }
                Content.AddRange(_tiles);
                Scroll.ContentHeight = gridTop + ((_kinds.Count + cols - 1) / cols) * _tilePitchY;
            }
            else
            {
                _model.Top = _rowsTop;
                _model.Refresh(Ctx.Services.Checker, Ctx.Services.Settings.OnlyLive, Ctx.Now);
                foreach (var r in _model.Visible) Content.Add(r.Item);
                Scroll.ContentHeight = _model.ContentBottom;
            }
            if (compact) { Scroll.ViewTop = ResultsMode ? _rowsTop : _chipsY; Scroll.ViewBottom = Ctx.Style.Main.Bottom; }
            else { Scroll.ViewTop = Tokens.MainTop; Scroll.ViewBottom = Tokens.H - Tokens.MainBottom; }
            if (focusId != null && !RailFocused)
            {
                var keep = Content.FirstOrDefault(f => f.Id == focusId);
                Focused = null;
                SetFocus(keep ?? _field, Ctx.Now, true);
            }
        }

        // ---------------------------------------------------------------- query
        private void SetQuery(string text)
        {
            if (text == _query) return;
            bool modeChange = (text.Length > 0) != (_query.Length > 0);
            _query = text;
            if (_imeBound != null && !_fromIme && text != _imeText) { _imeText = text; _imeBound.SetText(text); }
            if (_remoteCts != null) _remoteCts.Cancel();
            _remoteCts = null;
            if (_query.Length == 0)
            {
                Ctx.Services.Checker.Stop();
                _results = new List<Channel>(); _model.SetChannels(_results);
                Build();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _results = ChannelFilter.Apply(Ctx.Services.Search.Search(_query), Ctx.Services.Settings, true);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _model.SetChannels(_results);
            if (_results.Count > 0) Ctx.Services.Checker.Check(_results);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            _model.Refresh(Ctx.Services.Checker, Ctx.Services.Settings.OnlyLive, Ctx.Now);
            Build();
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if ((t3 - t0) * f > 30) AceLog.Info("ui", "search '" + _query + "': search " + (int)((t1 - t0) * f) + " ms, check " + (int)((t2 - t1) * f) + " ms, layout " + (int)((t3 - t2) * f) + " ms");
            StartRemote(_query);
        }

        private void StartRemote(string q)
        {
            if (q.Length < 2) return;
            var cts = new CancellationTokenSource(); _remoteCts = cts;
            var ct = cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(450, ct).ConfigureAwait(false);
                    var extra = await Ctx.Services.Search.SearchRemoteAsync(q, ct).ConfigureAwait(false);
                    if (extra == null || extra.Count == 0 || ct.IsCancellationRequested) return;
                    extra = ChannelFilter.Apply(extra, Ctx.Services.Settings, true);
                    Ctx.Nav.Post(() =>
                    {
                        if (ct.IsCancellationRequested || q != _query) return;
                        var ids = new HashSet<string>(_results.Select(c => c.Id));
                        var add = extra.Where(c => ids.Add(c.Id)).ToList();
                        if (add.Count == 0) return;
                        _results = _results.Concat(add).ToList();
                        _model.SetChannels(_results);
                        Ctx.Services.Checker.Check(_results);
                        MarkDirty();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AceLog.Warn("ui", "remote search: " + ex.Message); }
            });
        }

        protected override void WarmItem(SKCanvas c, Focusable f, double now)
        {
            if (f.Group == "grid") { var k = _kinds[(int)f.Tag]; Cards.Explore(Ctx, c, f, k.Id, k.Name, now, true); }
            else if (f.Group == "rows")
            {
                int i = Content.IndexOf(f), fi = Focused == null ? 0 : Content.IndexOf(Focused);
                if (Math.Abs(i - Math.Max(0, fi)) > 16) return;
                var row = _model.Visible.FirstOrDefault(r => r.Item == f);
                if (row != null) Cards.Row(Ctx, c, f, row.View, now, 1f, true);
            }
        }

        // ---------------------------------------------------------------- keys
        protected override bool HandleKey(KeyPress key, double now)
        {
            if (_imeBound != null)
            {
                // the system keyboard is the only source of text while it is up
                if (key.Key == UiKey.Char || key.Key == UiKey.Backspace) return true;
                if (key.Key == UiKey.Back) { CloseIme(true, true); return true; }
            }
            if (_kb != null) { HandleKeyboard(key, now); return true; }
            if (key.Key == UiKey.Char && key.Ch != '\0')
            {
                if (_query.Length < 60) SetQuery(_query + char.ToLowerInvariant(key.Ch));
                SetFocus(_field, now);
                return true;
            }
            if (key.Key == UiKey.Backspace) { if (_query.Length > 0) SetQuery(_query.Substring(0, _query.Length - 1)); return true; }
            if (key.Key == UiKey.Ok && Focused == _field) { OpenKeyboard(now); return true; }
            return false;
        }

        // ---------------------------------------------------------------- system keyboard (touch platforms)
        private ITextInput _imeBound;           // the system keyboard while it is open (null = closed)
        private string _imeText = "";           // what the system keyboard holds, to tell it only what it does not know
        private bool _fromIme;

        /// <summary>The system keyboard, if this device types by touch and the platform has one. Otherwise null: PUi's own keyboard is used.</summary>
        private ITextInput SystemKeyboard { get { return Ctx.HasTouch ? Ctx.Platform.TextInput : null; } }

        private void OpenIme(ITextInput ime)
        {
            if (_imeBound != null) return;
            _imeBound = ime; _imeText = _query;
            ime.TextChanged += OnImeText; ime.Submitted += OnImeSubmitted; ime.Dismissed += OnImeDismissed;
            ime.Show(new TextInputRequest { Text = _query, MaxLength = 60, Action = TextInputAction.Search });
            Ctx.Nav.Invalidate();
        }

        /// <summary>Closes the system keyboard. tellPlatform=false when the platform closed it itself (Dismissed).</summary>
        private void CloseIme(bool commit, bool tellPlatform)
        {
            var ime = _imeBound;
            if (ime == null) return;
            _imeBound = null;
            ime.TextChanged -= OnImeText; ime.Submitted -= OnImeSubmitted; ime.Dismissed -= OnImeDismissed;
            if (tellPlatform) { try { ime.Hide(); } catch (Exception ex) { AceLog.Warn("ui", "ime hide: " + ex.Message); } }
            if (commit && _query.Trim().Length >= 2) Ctx.Services.Search.AddRecentQuery(_query.Trim());
            if (Ctx != null) Ctx.Nav.Invalidate();
        }

        private void OnImeText(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            if (t.Length > 60) t = t.Substring(0, 60);
            _imeText = text ?? "";
            _fromIme = true;
            try { SetQuery(t); SetFocus(_field, Ctx.Now); } finally { _fromIme = false; }
            Ctx.Nav.Invalidate();
        }

        private void OnImeSubmitted()
        {
            CloseIme(true, true);
            var first = Content.FirstOrDefault(f => f.Group == "rows");
            if (first != null) SetFocus(first, Ctx.Now);
        }

        private void OnImeDismissed() { CloseIme(true, false); }

        protected override void OnScrollStart() { CloseIme(true, true); }

        protected override void TapContent(Focusable f, double now)
        {
            if (f == _field) { OpenKeyboard(now); return; }
            base.TapContent(f, now);
        }

        private void OpenKeyboard(double now)
        {
            var ime = SystemKeyboard;
            if (ime != null) { OpenIme(ime); return; }
            _kb = new OnScreenKeyboard();
            _kb.SetText(_query);
            _kbAnim.Snap(0); _kbAnim.Set(1, now, 0.3);
        }

        private void CloseKeyboard(bool commit)
        {
            if (_kb == null) return;
            _kb = null;
            if (commit && _query.Trim().Length >= 2) Ctx.Services.Search.AddRecentQuery(_query.Trim());
        }

        private void HandleKeyboard(KeyPress key, double now)
        {
            var kb = _kb;
            switch (key.Key)
            {
                case UiKey.Up: case UiKey.Down: case UiKey.Left: case UiKey.Right:
                    if (!kb.Move(key.Key) && key.Key == UiKey.Up) CloseKeyboard(false);
                    return;
                case UiKey.Ok:
                    kb.Press();
                    SetQuery(kb.Text.ToString());
                    if (kb.SubmitRequested)
                    {
                        CloseKeyboard(true);
                        if (Content.Count > 1) { var first = Content.FirstOrDefault(f => f.Group == "rows"); if (first != null) SetFocus(first, now); }
                    }
                    return;
                case UiKey.Char: if (key.Ch != '\0') { kb.Type(char.ToLowerInvariant(key.Ch)); SetQuery(kb.Text.ToString()); } return;
                case UiKey.Backspace: kb.Backspace(); SetQuery(kb.Text.ToString()); return;
                case UiKey.Back: CloseKeyboard(true); return;
            }
        }

        protected override void GoBack()
        {
            if (ResultsMode) { SetQuery(""); SetFocus(_field, Ctx.Now); return; }
            base.GoBack();
        }

        protected override void Activate(Focusable item)
        {
            if (item.Group == "chips") { var t = (string)item.Tag; SetQuery(t); SetFocus(_field, Ctx.Now); return; }
            if (item.Group == "grid")
            {
                var kind = _kinds[(int)item.Tag];
                var channels = ChannelFilter.Apply(Ctx.Services.Search.Explore(kind.Id), Ctx.Services.Settings, true);
                string id = kind.Id;
                Ctx.Nav.Push(new CategoryScreen(kind.Name, channels, Section.Search, ct => Ctx.Services.Search.ExploreRemoteAsync(id, ct)));
                return;
            }
            if (item.Group == "rows")
            {
                var row = _model.Visible.FirstOrDefault(r => r.Item == item);
                if (row == null) return;
                if (_query.Trim().Length >= 2) Ctx.Services.Search.AddRecentQuery(_query.Trim());
                if (CastRedirect(row.Channel)) return;                  // sending to another screen: the channel goes there, not to the player
                var list = _model.VisibleChannels();
                Ctx.Nav.Push(new PlayerScreen(new PlayContext(list, list.IndexOf(row.Channel))));
            }
        }

        // ---------------------------------------------------------------- frame
        protected override void OnUpdate(double now)
        {
            if (TakeDirty())
            {
                if (ResultsMode)
                {
                    _model.Refresh(Ctx.Services.Checker, Ctx.Services.Settings.OnlyLive, now);
                    string focusId = Focused != null && !RailFocused ? Focused.Id : null;
                    Content.Clear(); Content.Add(_field); Content.AddRange(_chips);
                    foreach (var r in _model.Visible) Content.Add(r.Item);
                    Scroll.ContentHeight = _model.ContentBottom;
                    if (focusId != null && Focused != null && !Content.Contains(Focused))
                    {
                        var keep = Content.FirstOrDefault(f => f.Id == focusId);
                        if (keep != null) { Focused = null; SetFocus(keep, now, true); }
                    }
                }
                var chips = Ctx.Services.Search.RecentQueries;
                if (!chips.SequenceEqual(_chipTexts)) Build();
            }
        }

        protected override bool ShowsProgress(double now) { return ResultsMode && _model.Total > 0 && _model.Done < _model.Total; }

        // The open keyboard no longer asks for full frames non-stop: its ring and the cursor are decoration (they slow down at rest) and each pressed key redraws on its own
        protected override bool ExtraAnimating(double now) { return _model.Animating(now) || _kbAnim.Active(now) || _chipOff.Active(now); }

        /// <summary>Target horizontal shift of the recent searches (0 if they all fit on one line) and whether any is left outside.</summary>
        public float ChipOffset { get { return _chipOff.Target; } }
        public float ChipOverflow { get { return ChipsMaxOffset; } }
        public IReadOnlyList<Focusable> Chips { get { return _chips; } }

        /// <summary>Total width of the recent-search pills and maximum shift.</summary>
        private float ChipsMaxOffset
        {
            get
            {
                if (_chips.Count == 0) return 0;
                // the TV: until the last pill ends where the field does; a phone or a tablet: until it is as far from the screen's edge as the first one was from the other
                if (Ctx != null && Ctx.Compact) return Math.Max(0, _chips[_chips.Count - 1].R.Right + _left - Ctx.CW);
                return Math.Max(0, _chips[_chips.Count - 1].R.Right - (Tokens.MainLeft + Tokens.MainWidth));
            }
        }

        /// <summary>With the focus on a pill, the row slides just enough for it to be seen whole (the others are reached with the focus).</summary>
        protected override void OnFocusChanged(Focusable f, double now)
        {
            if (f.Group != "chips" || PointerFocus) return;
            float off = _chipOff.Target, left = _left, right = _right;
            if (f.R.Left - off < left) off = f.R.Left - left;
            else if (f.R.Right - off > right) off = f.R.Right - right;
            off = Math.Max(0, Math.Min(off, ChipsMaxOffset));
            if (Math.Abs(off - _chipOff.Target) > 0.5f) _chipOff.Set(off, now, 0.3);
        }

        // ---------------------------------------------------------------- drawing
        protected override void DrawHeader(SKCanvas c, double now)
        {
            DrawField(c, now);
        }

        private void DrawCompactContent(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            float sy = Scroll.Offset(now), top = (ResultsMode ? _rowsTop : _chipsY) - 6;
            c.Save();
            c.ClipRect(new SKRect(st.Main.Left, top, st.Main.Right, st.Main.Bottom + 8));
            c.Translate(0, -sy);
            if (_chips.Count > 0)
            {
                c.Save();
                c.ClipRect(new SKRect(st.Main.Left, _chipsY - 12, st.Main.Right, _chipsY + _chipH + 12));
                c.Translate(-_chipOff.Value(now), 0);
                foreach (var ch in _chips) DrawChip(c, ch, now);
                c.Restore();
            }
            if (!ResultsMode)
            {
                foreach (var t in _tiles) if (t != Focused) DrawTile(c, t, now);
                if (Focused != null && Focused.Group == "grid") DrawTile(c, Focused, now);
            }
            else RowListView.Draw(Ctx, c, _model, Focused, now, sy);
            c.Restore();
        }

        protected override void DrawContent(SKCanvas c, double now)
        {
            if (Ctx.Compact) { DrawCompactContent(c, now); if (_kb != null) DrawKeyboard(c, now); return; }
            var gx = Ctx.Gx;
            float sy = Scroll.Offset(now);
            c.Save();
            c.Translate(0, -sy);
            DrawField(c, now);
            if (_chips.Count > 0)
            {
                c.Save();
                c.ClipRect(new SKRect(Tokens.MainLeft - 14, ChipsY - 40, Tokens.MainLeft + Tokens.MainWidth + 14, ChipsY + ChipH + 40));
                c.Translate(-_chipOff.Value(now), 0);
                foreach (var ch in _chips) DrawChip(c, ch, now);
                c.Restore();
            }
            if (!ResultsMode)
            {
                foreach (var t in _tiles) if (t != Focused) DrawTile(c, t, now);
                if (Focused != null && Focused.Group == "grid") DrawTile(c, Focused, now);
            }
            else
            {
                c.Save();
                c.ClipRect(new SKRect(Tokens.MainLeft - 40, _model.Top - 30, Tokens.W, Tokens.H));
                RowListView.Draw(Ctx, c, _model, Focused, now, sy);
                c.Restore();
            }
            c.Restore();
            if (_kb != null) DrawKeyboard(c, now);
        }

        private void DrawTile(SKCanvas c, Focusable t, double now)
        {
            var k = _kinds[(int)t.Tag];
            Cards.Explore(Ctx, c, t, k.Id, k.Name, now);
        }

        private void DrawFieldCompact(SKCanvas c, double now)
        {
            var gx = Ctx.Gx;
            var st = Ctx.Style;
            string text = _query;
            bool busy = ResultsMode && _model.Total > 0 && _model.Done < _model.Total;
            bool active = _kb != null || _imeBound != null || (!Quiet && Focused == _field);
            float size = st.Phone ? 17 : 19, icon = st.Phone ? 20 : 22, textX = 16 + icon + 10;
            Cards.Card(Ctx, c, _field, _field.R.Height / 2, now, "cfield|" + text + "|" + (int)_field.R.Width + "|" + (st.Phone ? "p" : "t"), 0.08f, Tokens.WithAlpha(Tokens.Accent, 0.55f), 1.5f, 20, 56, (cv, g, r, f) =>
            {
                g.DrawIcon(cv, Icons.Search, r.Left + 16 + icon / 2, r.Top + r.Height / 2, icon, Tokens.TextA(0.70f), 1.8f);
                float by = g.Baseline(r.Top, r.Height, size, 600);
                if (text.Length == 0) g.DrawText(cv, "Buscar canales", r.Left + textX, by, size, 600, Tokens.TextA(0.40f));
                else g.DrawText(cv, g.Ellipsize(text, size, 600, r.Width - textX - 52), r.Left + textX, by, size, 600, Tokens.Text);
            }, null);
            if (busy) Ctx.Fx.Spinner(c, _field.R.Right - 20 - 9, _field.R.Top + _field.R.Height / 2, 18, now, Tokens.Accent2);
            if (active && (Ctx.Decor.Stopped || ((int)(Ctx.Decor.Driven ? Ctx.Decor.Value * 2 : now * 2)) % 2 == 0))
            {
                float tw = text.Length == 0 ? 0 : gx.MeasureText(gx.Ellipsize(text, size, 600, _field.R.Width - textX - 52), size, 600);
                float cx = _field.R.Left + textX + tw + 2, cy = _field.R.Top + _field.R.Height / 2;
                gx.FillRR(c, new SKRect(cx, cy - size * 0.6f, cx + 2, cy + size * 0.6f), 1, Tokens.WithAlpha(Tokens.Accent, 0.9f));
            }
        }

        private void DrawField(SKCanvas c, double now)
        {
            if (Ctx.Compact) { DrawFieldCompact(c, now); return; }
            var gx = Ctx.Gx;
            string text = _query;
            bool busy = ResultsMode && _model.Total > 0 && _model.Done < _model.Total;
            Cards.Card(Ctx, c, _field, 48, now, "field|" + text, 0.07f, Tokens.White(0.14f), 1, 20, 56, (cv, g, r, f) =>
            {
                g.DrawIcon(cv, Icons.Search, r.Left + 36 + 17, r.Top + 31 + 17, 34, Tokens.Text, 1.8f);
                float tx = r.Left + 92, by = g.Baseline(r.Top, r.Height, 30, 600);
                if (text.Length == 0) g.DrawText(cv, "Buscar canales", tx, by, 30, 600, Tokens.TextA(0.36f));
                else g.DrawText(cv, g.Ellipsize(text, 30, 600, r.Width - 92 - 100), tx, by, 30, 600, Tokens.Text);
            }, null);
            if (busy) Ctx.Fx.Spinner(c, _field.R.Right - 40 - 11, _field.R.Top + 37 + 11, 22, now, Tokens.Accent2);
            if (_kb != null || _imeBound != null || (!Quiet && Focused == _field))
            {
                // blinking text cursor
                // the cursor blinks with the decoration clock: at rest (stopped) it stays on
                if (Ctx.Decor.Stopped || ((int)(Ctx.Decor.Driven ? Ctx.Decor.Value * 2 : now * 2)) % 2 == 0)
                {
                    float tw = text.Length == 0 ? 0 : gx.MeasureText(gx.Ellipsize(text, 30, 600, _field.R.Width - 92 - 100), 30, 600);
                    float cx = _field.R.Left + 92 + tw + 3, cy = _field.R.Top + FieldH / 2;
                    gx.FillRR(c, new SKRect(cx, cy - 18, cx + 2.5f, cy + 18), 1, Tokens.WithAlpha(Tokens.Accent, 0.9f));
                }
            }
        }

        private void DrawChip(SKCanvas c, Focusable ch, double now)
        {
            var gx = Ctx.Gx;
            float f = ch.Focus.Value(now), ring = ch.Ring.Value(now);
            if (Ctx.Compact)
            {
                var cf = Tokens.Lerp(Tokens.White(0.06f), Tokens.C(157, 107, 255, 0.28f), f);
                gx.FillRR(c, ch.R, _chipH / 2, cf);
                gx.StrokeRR(c, ch.R, _chipH / 2, 1, Tokens.White(0.10f));
                gx.DrawIcon(c, Icons.Clock, ch.R.Left + 16 + 7.5f, (ch.R.Top + ch.R.Bottom) / 2, 15, Tokens.TextA(0.55f), 2f);
                gx.DrawText(c, (string)ch.Tag, ch.R.Left + 16 + 15 + 8, gx.Baseline(ch.R.Top, _chipH, 14, 600), 14, 600, Tokens.Text);
                if (ring > 0.01f) Ctx.Fx.FluidRing(c, ch.R, _chipH / 2, ring, now);
                return;
            }
            var fill = Tokens.Lerp(Tokens.White(0.07f), Tokens.C(157, 107, 255, 0.28f), f);
            gx.FillRR(c, ch.R, ChipH / 2, fill);
            string t = (string)ch.Tag;
            gx.DrawText(c, t, ch.R.Left + 16, gx.Baseline(ch.R.Top, ChipH, 16, 600), 16, 600, Tokens.TextA(0.86f));
            if (ring > 0.01f) Ctx.Fx.FluidRing(c, ch.R, ChipH / 2, ring, now);
        }

        // ---------------------------------------------------------------- touch
        // on a phone or a tablet the search field stays at the top while the rest scrolls under it
        protected override bool ScrollsWithContent(Focusable f) { return !(Ctx.Compact && f == _field); }

        protected override SKRect ScreenRect(Focusable f, double now, float scrollY)
        {
            var r = TouchHit.Offset(f.R, 0, f == _field && Ctx.Compact ? 0 : -scrollY);
            return f.Group == "chips" ? TouchHit.Offset(r, -_chipOff.Value(now), 0) : r;
        }

        protected override SKRect? ContentClip(Focusable f, double now)
        {
            if (Ctx.Compact)
            {
                if (f == _field) return CanvasRect;
                var main = Ctx.Style.Main;
                float top = (ResultsMode ? _rowsTop : _chipsY) - 6;
                if (f.Group == "chips") return TouchHit.Intersect(TouchHit.Offset(ChipsRegion, 0, -Scroll.Offset(now)), new SKRect(main.Left, top, main.Right, main.Bottom));
                return new SKRect(main.Left, top, main.Right, main.Bottom);
            }
            return f.Group == "chips" ? TouchHit.Offset(ChipsRegion, 0, -Scroll.Offset(now)) : ContentRegion;
        }

        protected override float RadiusOf(Focusable f)
        {
            if (Ctx.Compact) return f == _field ? _fieldH / 2 : f.Group == "chips" ? _chipH / 2 : 20;
            return f == _field ? 48 : f.Group == "chips" ? ChipH / 2 : 24;
        }

        protected override void CollectTargets(List<TouchTarget> into, double now)
        {
            base.CollectTargets(into, now);
            if (_kb == null) return;
            // PUi's own keyboard (a touch device without a system keyboard): the panel blocks what is under it and each key presses itself
            var panel = KbPanel();
            into.Add(new TouchTarget { Id = "kb", Rect = panel, Radius = 36 * KbF, Layer = 1, Press = false });
            for (int r = 0; r < _kb.Layout.Count; r++)
                foreach (var k in _kb.Layout[r])
                {
                    int row = r; var key = k;
                    into.Add(new TouchTarget { Id = "key:" + (k.Action == KbAction.Char ? k.Label : k.Action.ToString()), Rect = KeyRect(panel, r, k), Radius = 18 * KbF, Layer = 2,
                        Tap = () => { _kb.Row = row; _kb.Cell = key.Col; HandleKeyboard(new KeyPress(UiKey.Ok, "touch"), Ctx.Now); Ctx.Nav.Invalidate(); } });
                }
        }

        // ---------------------------------------------------------------- on-screen keyboard
        /// <summary>Size of the keyboard's drawing relative to the TV's (a key is 68 units tall there).</summary>
        private float KbF { get { return _keyH / 68f; } }

        private SKRect KbPanel()
        {
            float w = 10 * _keyW + 9 * _keyGap + 2 * _kbPad, h = 5 * _keyH + 4 * _keyGap + 2 * _kbPad;
            if (Ctx.Compact)
            {
                var m = Ctx.Style.Main;
                float cx = (m.Left + m.Right) / 2;
                return new SKRect(cx - w / 2, m.Bottom - h - (Ctx.Style.SideRail ? -8 : 8), cx + w / 2, m.Bottom - (Ctx.Style.SideRail ? -8 : 8));
            }
            float x = (Tokens.MainLeft + Tokens.W - Tokens.MainRight) / 2 - w / 2, y = Tokens.H - Tokens.MainBottom - h;
            return new SKRect(x, y, x + w, y + h);
        }

        private SKRect KeyRect(SKRect panel, int row, KbKey k)
        {
            float x = panel.Left + _kbPad + k.Col * (_keyW + _keyGap), y = panel.Top + _kbPad + row * (_keyH + _keyGap);
            return new SKRect(x, y, x + k.Span * _keyW + (k.Span - 1) * _keyGap, y + _keyH);
        }

        private Icon KeyIcon(KbAction a) { return a == KbAction.Space ? Icons.Space : a == KbAction.Backspace ? Icons.Backspace : a == KbAction.Clear ? Icons.Close : Icons.Search; }

        private void DrawKeyboard(SKCanvas c, double now)
        {
            var gx = Ctx.Gx;
            var panel = KbPanel();
            float a = _kbAnim.Value(now);
            c.Save();
            c.Translate(0, (1 - a) * 40);
            var bounds = new SKRect(panel.Left - 4, panel.Top - 4, panel.Right + 4, panel.Bottom + 4);
            float f0 = KbF;
            var img = Ctx.Sprites.Get("kb|static|" + (int)_keyW + "x" + (int)_keyH, bounds, Ctx.K, cv =>
            {
                gx.FillRR(cv, panel, 36 * f0, Tokens.C(10, 12, 20, Ctx.Compact ? 0.97f : 0.90f));
                gx.StrokeRR(cv, panel, 36 * f0, 1, Tokens.White(0.14f));
                for (int r = 0; r < _kb.Layout.Count; r++)
                    foreach (var k in _kb.Layout[r])
                    {
                        var kr = KeyRect(panel, r, k);
                        bool submit = k.Action == KbAction.Submit;
                        gx.FillRR(cv, kr, 18 * f0, submit ? Tokens.WithAlpha(Tokens.Accent, 0.9f) : Tokens.White(0.08f));
                        if (k.Action == KbAction.Char) gx.DrawTextAligned(cv, k.Label, (kr.Left + kr.Right) / 2, gx.Baseline(kr.Top, kr.Height, 28 * f0, 700), 28 * f0, 700, Tokens.Text, 0.5f);
                        else gx.DrawIcon(cv, KeyIcon(k.Action), (kr.Left + kr.Right) / 2, (kr.Top + kr.Bottom) / 2, 32 * f0, submit ? Tokens.KnobOn : Tokens.TextA(0.9f), 2f);
                    }
            });
            gx.DrawImage(c, img, bounds, a);
            // focused key
            var fk = _kb.Focused; var fr = KeyRect(panel, _kb.Row, fk);
            gx.FillRR(c, fr, 18 * f0, Tokens.White(0.16f * a));
            Ctx.Fx.FluidRing(c, fr, 18 * f0, a, now, false);
            c.Restore();
        }

        public override string Describe()
        {
            return base.Describe() + " query='" + _query + "' results=" + _model.Visible.Count + (_kb != null ? " keyboard" : "") + (_imeBound != null ? " ime" : "");
        }
    }
}
