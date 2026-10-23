using System;
using System.Collections.Generic;
using System.Linq;
using PUi.Core;
using PUi.Gfx;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Screens
{
    /// <summary>
    /// Settings on a phone or a tablet (design PhoneSettings): groups in rounded panels (the switches, the parameters with their round "-" and "+", the system information) instead of the TV's cards,
    /// one column on a phone and on a tablet in portrait, two on a tablet in landscape. The switches, the selectors and the data are the TV's.
    /// </summary>
    public sealed partial class SettingsScreen
    {
        private sealed class CGroup { public SKRect R; public readonly List<Row> Rows = new List<Row>(); public bool Dim; public float[] Heights; public int Id; }
        private readonly List<CGroup> _cgroups = new List<CGroup>();

        private const float SwitchW = 52, SwitchH = 32, GroupRadius = 22;

        protected override void OnSurfaceChanged(double now)
        {
            Build(now, false);
            Ctx.Nav.Invalidate();
        }

        protected override void DrawHeader(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            float size = st.Phone ? 28 : 32;
            Ctx.Gx.DrawText(c, "Ajustes", st.TitleXNoBack, Ctx.Gx.Baseline(st.HeaderCy - 24, 48, size, 800), size, 800, Tokens.Text, -size * 0.02f);
        }

        // ---------------------------------------------------------------- layout
        private void LayoutCompact()
        {
            var st = Ctx.Style;
            var gx = Ctx.Gx;
            float top = st.SideRail ? st.HeaderCy + 24 + 16 : st.Main.Top;
            float colW, colL0, colL1;
            if (st.SideRail) { float gap = 24; colW = (st.ListR - st.ListL - gap) / 2; colL0 = st.ListL; colL1 = st.ListL + colW + gap; }
            else { colW = Math.Min(st.ListR - st.ListL, st.Phone ? 400 : 680); colL0 = colL1 = (st.W - colW) / 2; }
            _cgroups.Clear();
            // the three groups: switches, parameters, system information. Two columns in landscape (switches | parameters and information), one column otherwise.
            float[] colY = { top, top };
            float gapY = 14;
            for (int gi = 0; gi < 3; gi++)
            {
                var g = new CGroup { Id = gi, Dim = gi == 2 };
                foreach (var r in _rows) if (r.Group == gi) g.Rows.Add(r);
                int col = st.SideRail ? (gi == 0 ? 0 : 1) : 0;
                float x = col == 0 ? colL0 : colL1, y = colY[col];
                float h = 0;
                g.Heights = new float[g.Rows.Count];
                for (int i = 0; i < g.Rows.Count; i++)
                {
                    var r = g.Rows[i];
                    float rh;
                    if (r.Kind == Kind.Toggle)
                    {
                        r.TextW = colW - 18 - 16 - SwitchW - 14;
                        r.DescLines = gx.WrapText(r.CDesc ?? r.Desc, 13, 500, r.TextW, 3);
                        rh = Math.Max(78, 14 + gx.LineHeight(16, 700) + 3 + r.DescLines.Count * 13 * 1.35f + 14);
                    }
                    else if (r.Kind == Kind.Stepper) { rh = 64; r.DescLines = new List<string>(); }
                    else { rh = 12 + r.Lines.Count * 34; r.DescLines = new List<string>(); }
                    g.Heights[i] = rh;
                    r.Item.R = new SKRect(x, y + h, x + colW, y + h + rh);
                    if (r.Kind == Kind.Stepper) { r.Sel.Flat = true; r.Sel.W = 44 + 4 + 92 + 4 + 44; r.Sel.H = 44; r.Sel.Layout(r.Item.R, 10); }
                    h += rh;
                }
                g.R = new SKRect(x, y, x + colW, y + h);
                _cgroups.Add(g);
                colY[col] = y + h + gapY;
            }
            Scroll.ViewTop = top; Scroll.ViewBottom = st.Main.Bottom;
            Scroll.ContentHeight = Math.Max(colY[0], colY[1]) - gapY;
            Scroll.Margin = 12;
        }

        // ---------------------------------------------------------------- drawing
        private void DrawCompactContent(SKCanvas c, double now)
        {
            var st = Ctx.Style;
            float sy = Scroll.Offset(now), top = Scroll.ViewTop - 8;
            c.Save();
            c.ClipRect(new SKRect(st.Main.Left, top, st.Main.Right, st.Main.Bottom + 8));
            c.Translate(0, -sy);
            foreach (var g in _cgroups) DrawGroup(c, g, now);
            c.Restore();
        }

        private void DrawGroup(SKCanvas c, CGroup g, double now)
        {
            var gx = Ctx.Gx;
            var bounds = new SKRect((float)Math.Floor(g.R.Left) - 2, (float)Math.Floor(g.R.Top) - 2, (float)Math.Ceiling(g.R.Right) + 2, (float)Math.Ceiling(g.R.Bottom) + 2);
            string key = "cgroup|" + g.Id + "|" + (int)g.R.Width + "x" + (int)g.R.Height + "|" + string.Join("¦", g.Rows.Select(r => r.Id + "=" + r.Title + "=" + (r.CDesc ?? "") + "=" + (r.Lines == null ? "" : string.Join("/", r.Lines.Select(l => l.Key + ":" + l.Value)))));
            var img = Ctx.Sprites.Get(key, bounds, Ctx.K, cv => DrawGroupStatic(cv, g));
            gx.DrawImage(c, img, bounds);
            foreach (var r in g.Rows)
            {
                if (r.Kind == Kind.Sysinfo) continue;
                // focus: a soft highlight of the row and the fluid border (only while a key drives the page)
                var it = r.Item;
                float f = it.Focus.Value(now), ring = it.Ring.Value(now);
                if (f > 0.01f) gx.FillRR(c, it.R, 14, Tokens.White(0.07f * f));
                if (ring > 0.01f) Ctx.Fx.FluidRing(c, it.R, 14, ring, now);
                float midY = it.R.Top + it.R.Height / 2;
                if (r.Kind == Kind.Toggle)
                {
                    float k = r.Knob.Value(now);
                    float x = it.R.Right - 16 - SwitchW;
                    var track = new SKRect(x, midY - SwitchH / 2, x + SwitchW, midY + SwitchH / 2);
                    gx.FillRR(c, track, SwitchH / 2, Tokens.Lerp(Tokens.White(0.18f), Tokens.Accent, k));
                    gx.FillCircle(c, x + 3 + 13 + 20 * k, midY, 13, Tokens.Lerp(Tokens.Text, Tokens.KnobOn, k));
                }
                else if (r.Kind == Kind.Stepper) r.Sel.Draw(Ctx, c, now);
            }
        }

        private void DrawGroupStatic(SKCanvas c, CGroup g)
        {
            var gx = Ctx.Gx;
            var R = g.R;
            gx.FillRR(c, R, GroupRadius, Tokens.White(g.Dim ? 0.03f : 0.05f));
            gx.StrokeRR(c, R, GroupRadius, 1, Tokens.White(g.Dim ? 0.07f : 0.09f));
            for (int i = 0; i < g.Rows.Count; i++)
            {
                var r = g.Rows[i];
                var rr = r.Item.R;
                bool last = i == g.Rows.Count - 1;
                if (r.Kind == Kind.Sysinfo)
                {
                    for (int j = 0; j < r.Lines.Count; j++)
                    {
                        float top = rr.Top + 6 + j * 34;
                        gx.DrawText(c, r.Lines[j].Key, rr.Left + 18, gx.Baseline(top, 34, 13, 600), 13, 600, Tokens.TextA(0.62f));
                        gx.DrawTextAligned(c, gx.Ellipsize(r.Lines[j].Value, 13, 700, rr.Width - 36 - 90), rr.Right - 18, gx.Baseline(top, 34, 13, 700), 13, 700, Tokens.Text, 1f);
                    }
                    continue;
                }
                if (!last) gx.FillRR(c, new SKRect(rr.Left, rr.Bottom - 1, rr.Right, rr.Bottom), 0, Tokens.White(0.07f));
                if (r.Kind == Kind.Toggle)
                {
                    float x = rr.Left + 18, titleH = gx.LineHeight(16, 700);
                    float total = titleH + 3 + r.DescLines.Count * 13 * 1.35f, y0 = rr.Top + (rr.Height - total) / 2;
                    gx.DrawText(c, r.Title, x, gx.Baseline(y0, titleH, 16, 700), 16, 700, Tokens.Text);
                    for (int k = 0; k < r.DescLines.Count; k++)
                        gx.DrawText(c, r.DescLines[k], x, gx.Baseline(y0 + titleH + 3 + k * 13 * 1.35f, 13 * 1.35f, 13, 500), 13, 500, Tokens.TextA(0.62f));
                }
                else gx.DrawText(c, r.Title, rr.Left + 18, gx.Baseline(rr.Top, rr.Height, 16, 700), 16, 700, Tokens.Text);
            }
        }
    }
}
