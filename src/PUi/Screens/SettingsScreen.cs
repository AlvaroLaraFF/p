using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
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
    /// Settings (design Settings), in two columns. Left, the SWITCHES: only live, UPnP with its state and Ace's central search
    /// (off by default). Right, the SELECTORS: minimum resolution (any, 720p, 1080p, 4K), language (all, Spanish, English, Portuguese, Polish and those
    /// present in the data) and upload (always on, maximum from 128 KB/s to 2 MB/s), and below the system information (P! version, operating system, device,
    /// runtime, memory, IP and P2P port; each shell contributes it through IPlatform.SystemInfo). The selectors only change value in edit mode (OK enters; see
    /// EditableSelector); left from the left column goes to the menu. The sender's signature is still active internally but no longer shown; TSService has been removed from the app (see UserSettings.TsServiceEnabled).
    /// </summary>
    public sealed partial class SettingsScreen : SectionScreen
    {
        private enum Kind { Toggle, Stepper, Sysinfo }

        private sealed class Row
        {
            public string Id, Title, Desc, Value;
            public Kind Kind;
            public int Column;
            public float GapBefore;
            public Focusable Item;
            public readonly Anim Knob = new Anim(0, Bezier.Pop);
            public string Status; public int Tone;           // 0 ok, 1 warning
            public List<string> DescLines;
            public float TextW;
            public EditableSelector Sel;
            public List<KeyValuePair<string, string>> Lines;      // Kind.Sysinfo
            public string CDesc;                                  // the phone's shorter description
            public int Group;                                     // phone and tablet: the group (panel) the row is in
        }

        private const float ColW = 792, ColGap = 40, RowPadX = 28, RowPadY = 24, TopY = 96;
        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<string, EditableSelector> _sels = new Dictionary<string, EditableSelector>();

        /// <summary>Row titles (for the tests) and system information rows.</summary>
        public IReadOnlyList<string> RowTitles { get { return _rows.Where(r => r.Kind != Kind.Sysinfo).Select(r => r.Title).ToList(); } }
        public IReadOnlyList<KeyValuePair<string, string>> SystemInfoLines { get { var r = _rows.FirstOrDefault(x => x.Kind == Kind.Sysinfo); return r == null ? new List<KeyValuePair<string, string>>() : r.Lines; } }
        public int ColumnOf(string id) { var r = _rows.FirstOrDefault(x => x.Id == id); return r == null ? -1 : r.Column; }

        public override Section Section { get { return Section.Settings; } }
        public override string Name { get { return "Settings"; } }

        protected override void OnEnter(bool first)
        {
            Ctx.Services.Network.Changed += MarkDirty;
            Ctx.Services.Settings.Changed += MarkDirty;
            if (first) { Build(Ctx.Now, true); FocusFirst(Ctx.Now, true); }
            else MarkDirty();
        }

        public override void Leave()
        {
            base.Leave();                                   // ends any editing of a selector
            Ctx.Services.Network.Changed -= MarkDirty;
            Ctx.Services.Settings.Changed -= MarkDirty;
        }

        private void FocusFirst(double now, bool instant)
        {
            var f = _rows.FirstOrDefault(r => r.Item.Enabled);
            if (f != null) SetFocus(f.Item, now, instant);
        }

        private static string FormatUpload(int kb) { return kb >= 1024 ? (kb / 1024) + " MB/s" : kb + " KB/s"; }

        private void Build(double now, bool first)
        {
            var s = Ctx.Services.Settings;
            var net = Ctx.Services.Network.Status;
            var cfg = Ctx.Services.Config;
            var rows = new List<Row>
            {
                // left column: switches
                new Row { Id = "onlylive", Title = "Solo canales vivos", Desc = "Oculta los canales sin nadie emitiendo. Se comprueba al abrir cada categoría.", CDesc = "Oculta los canales sin emisión.", Kind = Kind.Toggle, Column = 0, Group = 0 },
                new Row { Id = "upnp", Title = "UPnP", Desc = "Pide al router que abra un puerto para que otros espectadores te encuentren: más pares y arranques más rápidos.", Kind = Kind.Toggle, Column = 0, GapBefore = 14, Group = 0,
                    CDesc = s.UpnpEnabled && net.Upnp == UpnpState.Open ? "Puerto " + net.UpnpPort + " abierto en el router." : "Pide al router abrir un puerto: más pares y arranques más rápidos.",
                    Status = s.UpnpEnabled && net.Upnp == UpnpState.Open ? "Puerto " + net.UpnpPort + " abierto" : null, Tone = 0 },
                new Row { Id = "acesearch", Title = "Buscador central de Ace", Desc = "Busca en todo el catálogo de Ace Stream. Usa claves de la app oficial y puede dejar de funcionar si Ace las cambia.", Kind = Kind.Toggle, Column = 0, GapBefore = 14, Group = 0, CDesc = "Busca en todo el catálogo de Ace Stream." },
                // right column: selectors and, below, the system information
                new Row { Id = "resolution", Title = "Resolución", Desc = "Resolución mínima de los canales. Los que no indican su calidad solo salen con «Cualquiera».", Kind = Kind.Stepper, Column = 1, Group = 1, Value = ChannelResolution.StepName(s.MinResolution) },
                new Row { Id = "language", Title = "Idioma", Desc = "Filtra las categorías y la búsqueda por idioma. Los canales sin idioma conocido salen con «Todos» y con «Español».", Kind = Kind.Stepper, Column = 1, GapBefore = 14, Group = 1, Value = ChannelLanguages.DisplayName(s.Language) },
                new Row { Id = "upload", Title = "Subida", Desc = "Lo que ves también se comparte con otros espectadores. Siempre activa: sin ella te atienden peor. Ajusta el máximo.", Kind = Kind.Stepper, Column = 1, GapBefore = 14, Group = 1, Value = FormatUpload(s.UploadKBps), Status = "Siempre activa", Tone = 0 },
                new Row { Id = "sysinfo", Title = "", Desc = "", Kind = Kind.Sysinfo, Column = 1, GapBefore = 34, Group = 2, Lines = SystemLines(net) },
            };
            // keep the animations and identity of the Focusables across rebuilds
            string focusId = Focused != null && !RailFocused ? Focused.Id : null;
            var old = _rows.ToDictionary(r => r.Id);
            var gx = Ctx.Gx;
            float[] colY = { TopY, TopY };
            foreach (var r in rows)
            {
                Row o;
                old.TryGetValue(r.Id, out o);
                if (r.Kind == Kind.Stepper)
                {
                    EditableSelector sel;
                    if (!_sels.TryGetValue(r.Id, out sel)) { sel = MakeSelector(r.Id); _sels[r.Id] = sel; }
                    r.Sel = sel; r.Item = sel.Row;
                }
                else if (o != null) r.Item = o.Item;
                else r.Item = new Focusable { Id = r.Id, Group = "rows", FocusScale = 1f };
                r.Item.Enabled = r.Kind != Kind.Sysinfo;
                r.Knob.Snap(ToggleValue(r));
                if (o != null) r.Knob.Snap(o.Knob.Target);
                if (Ctx.Compact) continue;                          // a phone or a tablet places the rows below, by groups
                if (r.Kind == Kind.Sysinfo)
                {
                    float hs = 2 * RowPadY + r.Lines.Count * SysLineH;
                    float xs = Tokens.MainLeft + r.Column * (ColW + ColGap), ys = colY[r.Column] + r.GapBefore;
                    r.Item.R = new SKRect(xs, ys, xs + ColW, ys + hs);
                    colY[r.Column] = ys + hs;
                    r.DescLines = new List<string>();
                    continue;
                }
                // text: available width depending on the control on the right
                float controlW = r.Kind == Kind.Toggle ? 76 : 248;
                r.TextW = ColW - 2 * RowPadX - controlW - 28;
                r.DescLines = gx.WrapText(r.Desc, 18, 400, r.TextW, 4);
                float titleH = Math.Max(gx.LineHeight(24, 700), r.Status != null ? gx.LineHeight(15, 700) + 8 : 0);
                float textH = titleH + 8 + r.DescLines.Count * 18 * 1.45f;
                float controlH = r.Kind == Kind.Toggle ? 42 : 60;
                float h = 2 * RowPadY + Math.Max(textH, controlH);
                float x = Tokens.MainLeft + r.Column * (ColW + ColGap);
                float y = colY[r.Column] + r.GapBefore;
                r.Item.R = new SKRect(x, y, x + ColW, y + h);
                if (r.Sel != null) r.Sel.Layout(r.Item.R, RowPadX);
                colY[r.Column] = y + h;
            }
            _rows.Clear(); _rows.AddRange(rows);
            TrackSelectors(_rows.Where(r => r.Sel != null).Select(r => r.Sel));
            Content.Clear(); foreach (var r in _rows) Content.Add(r.Item);
            if (Ctx.Compact) LayoutCompact();
            else { Scroll.ViewTop = Tokens.MainTop; Scroll.ViewBottom = Tokens.H - Tokens.MainBottom; Scroll.ContentHeight = Math.Max(colY[0], colY[1]); }
            if (focusId != null)
            {
                var keep = Content.FirstOrDefault(f => f.Id == focusId);
                if (keep != null && keep != Focused) { Focused = null; SetFocus(keep, now, true); }
            }
        }

        private const float SysLineH = 38;

        private static string FormatMem(long mib)
        {
            if (mib >= 1024) return (mib / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + " GiB";
            return mib + " MiB";
        }

        /// <summary>Informational system rows: what the platform shell contributes plus the P! version and the engine's P2P port. What is not known does not appear.</summary>
        private List<KeyValuePair<string, string>> SystemLines(NetworkStatus net)
        {
            var res = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, v) => { if (!string.IsNullOrWhiteSpace(v)) res.Add(new KeyValuePair<string, string>(k, v.Trim())); };
            SystemInfoData d = null;
            try { var si = Ctx.Platform.SystemInfo; if (si != null) d = si.Read(); } catch (Exception) { }
            add("Versión", "P! " + AppInfo.Version);
            if (d != null)
            {
                add("Sistema", (d.OsName + " " + d.OsVersion).Trim());
                add("Dispositivo", d.Model);
                add("Runtime", d.Runtime);
                if (d.MemoryTotalMiB > 0) add("Memoria", FormatMem(d.MemoryFreeMiB) + " libres de " + FormatMem(d.MemoryTotalMiB));
                string redTxt = d.LocalIp ?? "";
                if (net.ListenPort > 0) redTxt = (redTxt.Length > 0 ? redTxt + " · " : "") + "puerto P2P " + net.ListenPort;
                add("Red", redTxt);
            }
            else if (net.ListenPort > 0) add("Red", "puerto P2P " + net.ListenPort);
            return res;
        }

        private float ToggleValue(Row r)
        {
            var s = Ctx.Services.Settings;
            switch (r.Id)
            {
                case "onlylive": return s.OnlyLive ? 1 : 0;
                case "upnp": return s.UpnpEnabled ? 1 : 0;
                case "acesearch": return s.AceSearchEnabled ? 1 : 0;
            }
            return 0;
        }

        // ---------------------------------------------------------------- keys
        private Row RowOf(Focusable f) { return _rows.FirstOrDefault(r => r.Item == f); }

        private void Changed()
        {
            Ctx.Services.Settings.NotifyChanged();
            if (Ctx.Services.SaveSettings != null) Ctx.Services.SaveSettings();
        }

        /// <summary>
        /// Selector of a Settings row (Upload, Resolution, Language): at rest the focus is on the row; OK enters editing and then left/right change the
        /// value, which is applied and saved at once (EditableSelector, managed by SectionScreen).
        /// </summary>
        private EditableSelector MakeSelector(string id)
        {
            var sel = new EditableSelector(id) { Glyph = id == "upload" ? SelectorGlyph.MinusPlus : SelectorGlyph.Triangles };
            sel.Text = () =>
            {
                var s = Ctx.Services.Settings;
                switch (id)
                {
                    case "upload": return FormatUpload(s.UploadKBps);
                    case "resolution": return ChannelResolution.StepName(s.MinResolution);
                    default: return ChannelLanguages.DisplayName(s.Language);
                }
            };
            sel.Step = dir => Step(id, dir);
            return sel;
        }

        private List<string> LanguageOptions()
        {
            var s = Ctx.Services.Settings;
            return ChannelLanguages.Options(Ctx.Services.Catalog.Channels, s.Language);
        }

        /// <summary>Changes the selector's value by one position (dir = -1 or +1; it does not wrap at the ends), applies it and saves it. true if it changed.</summary>
        private bool Step(string id, int dir)
        {
            var s = Ctx.Services.Settings;
            bool changed = false;
            switch (id)
            {
                case "upload":
                    {
                        var steps = UserSettings.UploadStepsKBps;
                        int idx = Array.IndexOf(steps, s.UploadKBps); if (idx < 0) idx = 2;
                        int n = Math.Max(0, Math.Min(steps.Length - 1, idx + dir));
                        if (n != idx) { s.UploadKBps = steps[n]; changed = true; }
                        break;
                    }
                case "resolution":
                    {
                        var steps = ChannelResolution.Steps;
                        int idx = Array.IndexOf(steps, s.MinResolution); if (idx < 0) idx = 0;
                        int n = Math.Max(0, Math.Min(steps.Length - 1, idx + dir));
                        if (n != idx) { s.MinResolution = steps[n]; changed = true; }
                        break;
                    }
                case "language":
                    {
                        var opts = LanguageOptions();
                        int idx = opts.IndexOf(s.Language ?? ""); if (idx < 0) idx = 0;
                        int n = Math.Max(0, Math.Min(opts.Count - 1, idx + dir));
                        if (n != idx) { s.Language = opts[n]; changed = true; }
                        break;
                    }
            }
            if (changed) Changed();
            return changed;
        }

        // ---------------------------------------------------------------- touch
        protected override float RadiusOf(Focusable f) { return Ctx.Compact ? 14 : 26; }

        /// <summary>Besides the rows (a tap on a switch's row flips it), each selector's "−" and "+" halves change its value directly: no edit mode with a finger.</summary>
        protected override void CollectTargets(List<TouchTarget> into, double now)
        {
            base.CollectTargets(into, now);
            float sy = Scroll.Offset(now), min = TouchSizes.MinTargetLogical(Ctx.Surface);
            foreach (var r in _rows)
            {
                var sel = r.Sel;
                if (sel == null) continue;
                var pill = TouchHit.Offset(sel.Zone.R, 0, -sy);
                var row = TouchHit.Offset(r.Item.R, 0, -sy);
                float mid = (pill.Left + pill.Right) / 2, midY = (pill.Top + pill.Bottom) / 2;
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    int d = dir;
                    var half = d < 0 ? new SKRect(pill.Left, pill.Top, mid, pill.Bottom) : new SKRect(mid, pill.Top, pill.Right, pill.Bottom);
                    if (Ctx.Compact) half = d < 0 ? new SKRect(pill.Left, pill.Top, pill.Left + pill.Height, pill.Bottom) : new SKRect(pill.Right - pill.Height, pill.Top, pill.Right, pill.Bottom);       // the round buttons themselves
                    float cx = (half.Left + half.Right) / 2;
                    // as tall as the whole row, and at least a fingertip wide
                    var zone = new SKRect(Math.Min(half.Left, cx - min / 2), Math.Min(row.Top, midY - min / 2), Math.Max(half.Right, cx + min / 2), Math.Max(row.Bottom, midY + min / 2));
                    into.Add(new TouchTarget { Id = sel.Row.Id + (d < 0 ? ":minus" : ":plus"), Rect = half, Radius = half.Height / 2, Layer = 1, Clip = ContentRegion, ZoneOverride = zone,
                        Tap = () => TapStep(sel, d) });
                }
            }
        }

        private void TapStep(EditableSelector sel, int dir)
        {
            double now = Ctx.Now;
            SetFocus(sel.Row, now);
            sel.PressedAt = now; sel.PressedSide = dir;                  // the button lights for an instant, as in edit mode
            sel.Step(dir);
            Ctx.Nav.Invalidate();
        }

        protected override void Activate(Focusable item)
        {
            var row = RowOf(item);
            if (row == null) return;
            var s = Ctx.Services.Settings;
            double now = Ctx.Now;
            switch (row.Id)
            {
                case "onlylive": s.OnlyLive = !s.OnlyLive; break;
                case "upnp": s.UpnpEnabled = !s.UpnpEnabled; break;
                case "acesearch": s.AceSearchEnabled = !s.AceSearchEnabled; break;
                default: return;                                // the selectors are managed by SectionScreen (OK enters editing)
            }
            Changed();
        }

        // ---------------------------------------------------------------- frame
        protected override void OnUpdate(double now)
        {
            if (TakeDirty()) Build(now, false);
            foreach (var r in _rows) if (r.Kind == Kind.Toggle) r.Knob.Set(ToggleValue(r), now, Tokens.ToggleSecs);
        }

        protected override bool ExtraAnimating(double now)
        {
            foreach (var r in _rows) if (r.Knob.Active(now)) return true;
            return false;
        }

        protected override void DrawContent(SKCanvas c, double now)
        {
            if (Ctx.Compact) { DrawCompactContent(c, now); return; }
            foreach (var r in _rows) if (r.Item != Focused) DrawRow(c, r, now);
            if (Focused != null && Focused.Group == "rows") DrawRow(c, RowOf(Focused), now);
        }

        protected override void WarmItem(SKCanvas c, Focusable f, double now)
        {
            if (Ctx.Compact) return;
            var row = RowOf(f);
            if (row != null) DrawRow(c, row, now, true);
        }

        private void DrawRow(SKCanvas c, Row r, double now, bool warm = false)
        {
            var it = r.Item;
            if (r.Kind == Kind.Sysinfo)
            {
                Cards.Card(Ctx, c, it, 26, now, "sys|" + string.Join("|", r.Lines.Select(l => l.Key + "=" + l.Value)), 0.05f, Tokens.White(0.09f), 1, 20, 56,
                    (cv, gx, rr, f) => SysStatic(cv, gx, rr, r), null, 1f, false, warm);
                return;
            }
            Cards.Card(Ctx, c, it, 26, now, "set|" + r.Id + "|" + r.Title + "|" + r.Status + "|" + r.Tone + "|" + r.Desc.Length, 0.05f, Tokens.White(0.09f), 1, 20, 56,
                (cv, gx, rr, f) => RowStatic(cv, gx, rr, r), null, 1f, true, warm);
            if (!warm) DrawControl(c, r, now);
        }

        private void RowStatic(SKCanvas c, Gx gx, SKRect rr, Row r)
        {
            float x = rr.Left + RowPadX, y = rr.Top + RowPadY;
            float titleH = Math.Max(gx.LineHeight(24, 700), r.Status != null ? gx.LineHeight(15, 700) + 8 : 0);
            float tw = gx.MeasureText(r.Title, 24, 700);
            gx.DrawText(c, r.Title, x, gx.Baseline(y, titleH, 24, 700), 24, 700, Tokens.Text);
            if (r.Status != null)
            {
                float cx = x + tw + 14, h = gx.LineHeight(15, 700) + 8, w = gx.MeasureText(r.Status, 15, 700) + 24 + 9 + 8;
                var rect = new SKRect(cx, y + (titleH - h) / 2, cx + w, y + (titleH - h) / 2 + h);
                gx.FillRR(c, rect, h / 2, Tokens.White(0.07f));
                var tone = r.Tone == 0 ? Tokens.Accent : Tokens.Warn;
                gx.FillCircle(c, cx + 12 + 4.5f, rect.Top + h / 2, 4.5f, tone);
                gx.DrawText(c, r.Status, cx + 12 + 9 + 8, gx.Baseline(rect.Top, h, 15, 700), 15, 700, tone);
            }
            float dy = y + titleH + 8;
            for (int i = 0; i < r.DescLines.Count; i++)
                gx.DrawText(c, r.DescLines[i], x, gx.Baseline(dy + i * 18 * 1.45f, 18 * 1.45f, 18, 400), 18, 400, Tokens.TextA(0.66f));
        }

        /// <summary>System information: dim label on the left and value on the right, one line per item (no marketing text).</summary>
        private void SysStatic(SKCanvas c, Gx gx, SKRect rr, Row r)
        {
            for (int i = 0; i < r.Lines.Count; i++)
            {
                float top = rr.Top + RowPadY + i * SysLineH;
                gx.DrawText(c, r.Lines[i].Key, rr.Left + RowPadX, gx.Baseline(top, SysLineH, 18, 500), 18, 500, Tokens.TextA(0.58f));
                gx.DrawTextAligned(c, gx.Ellipsize(r.Lines[i].Value, 20, 700, rr.Width - 2 * RowPadX - 160), rr.Right - RowPadX, gx.Baseline(top, SysLineH, 20, 700), 20, 700, Tokens.TextA(0.86f), 1f);
            }
        }

        private void DrawControl(SKCanvas c, Row r, double now)
        {
            var gx = Ctx.Gx;
            var rr = r.Item.R;
            float f = r.Item.Focus.Value(now);
            float scale = 1 + (r.Item.FocusScale - 1) * f;
            float midY = rr.Top + rr.Height / 2;
            if (r.Kind == Kind.Toggle)
            {
                float k = r.Knob.Value(now);
                float x = rr.Right - RowPadX - 76;
                var track = new SKRect(x, midY - 21, x + 76, midY + 21);
                gx.FillRR(c, track, 21, Tokens.Lerp(Tokens.White(0.16f), Tokens.Accent, k));
                float kx = x + 5 + 34 * k + 16;
                gx.FillCircle(c, kx, midY, 16, Tokens.Lerp(Tokens.TextA(0.85f), Tokens.KnobOn, k));
                if (f > 0.01f) gx.StrokeRR(c, new SKRect(track.Left - 4, track.Top - 4, track.Right + 4, track.Bottom + 4), 25, 4, Tokens.C(157, 107, 255, 0.45f * f));
            }
            else if (r.Kind == Kind.Stepper) r.Sel.Draw(Ctx, c, now);
        }
    }
}
