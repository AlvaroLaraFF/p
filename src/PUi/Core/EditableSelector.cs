using System;
using PUi.Gfx;
using PUi.Input;
using PUi.Motion;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>Marks of a selector's side buttons: "- value +" (quantities) or "◀ value ▶" (lists of values).</summary>
    public enum SelectorGlyph { MinusPlus, Triangles }

    /// <summary>
    /// Side selector with an edit mode, reusable by any settings row with "value and buttons on the sides".
    ///  - Rest: the focus is on the ROW (<see cref="Row"/>, with the fluid border). Left/right do not change the value: left goes out to the menu and
    ///    up/down go to other rows, as in the rest of the app.
    ///  - OK enters editing: the focus and the fluid border move to the selector's ZONE (<see cref="Zone"/>), which lights up instead of the row. Left/right
    ///    change the value (applied and saved at once: <see cref="Step"/> does that). Up/down do nothing (editing is a state with a clear exit and
    ///    this way you do not leave by accident to another row). OK again or Back leave editing and the focus returns to the row, without leaving the screen.
    /// The screen (SectionScreen) manages it by itself: just declare it in TrackSelectors and put <see cref="Row"/> in its Content (the zone never goes in Content).
    /// </summary>
    public sealed class EditableSelector
    {
        public const float Width = 248, Height = 60, Pad = 6, ButtonR = 24;

        public readonly Focusable Row;
        public readonly Focusable Zone;
        public SelectorGlyph Glyph = SelectorGlyph.Triangles;
        /// <summary>Phone and tablet look (design-movil): no pill, a round "-" and "+" at the ends of the zone and the value between them in the accent color. Size is W x H.</summary>
        public bool Flat;
        /// <summary>Size of the zone: the TV's pill by default; a phone or a tablet sets its own with Flat.</summary>
        public float W = Width, H = Height;
        /// <summary>Text of the current value.</summary>
        public Func<string> Text = () => "";
        /// <summary>Changes the value by one position (-1 left, +1 right) and saves it; true if it changed (it does not wrap around at the ends).</summary>
        public Func<int, bool> Step = d => false;
        public bool Editing { get; private set; }
        public double PressedAt = -10;
        public int PressedSide;

        public EditableSelector(string id)
        {
            Row = new Focusable { Id = id, Group = "rows", FocusScale = 1f };
            Zone = new Focusable { Id = id + ":zone", Group = "zone", FocusScale = 1f };
        }

        /// <summary>Places the zone (the pill) on the right edge of the row, vertically centered.</summary>
        public void Layout(SKRect row, float padRight)
        {
            float x = row.Right - padRight - W, mid = (row.Top + row.Bottom) / 2;
            Zone.R = new SKRect(x, mid - H / 2, x + W, mid + H / 2);
        }

        public void Begin() { Editing = true; }
        public void End() { Editing = false; }

        /// <summary>Key in edit mode. Always consumed (nothing reaches normal navigation). Returns true if editing must end.</summary>
        public bool OnEditKey(UiKey key, double now)
        {
            switch (key)
            {
                case UiKey.Left: case UiKey.Right:
                    {
                        int dir = key == UiKey.Right ? 1 : -1;
                        PressedAt = now; PressedSide = dir;
                        Step(dir);
                        return false;
                    }
                case UiKey.Ok: case UiKey.Back: return true;
                default: return false;                     // up, down and the rest: nothing
            }
        }

        private void DrawFlat(UiContext u, SKCanvas c, double now)
        {
            var gx = u.Gx;
            var z = Zone.R;
            float midY = (z.Top + z.Bottom) / 2, r = H / 2, f = Zone.Focus.Value(now);
            float bl = z.Left + r, br = z.Right - r;
            bool pl = now - PressedAt < 0.2 && PressedSide < 0, pr = now - PressedAt < 0.2 && PressedSide > 0;
            gx.FillCircle(c, bl, midY, r, Tokens.White(pl ? 0.22f : 0.08f + 0.05f * f));
            gx.FillCircle(c, br, midY, r, Tokens.White(pr ? 0.22f : 0.08f + 0.05f * f));
            gx.Line(c, bl - 6.5f, midY, bl + 6.5f, midY, 2.4f, Tokens.Text);
            gx.Line(c, br - 6.5f, midY, br + 6.5f, midY, 2.4f, Tokens.Text);
            gx.Line(c, br, midY - 6.5f, br, midY + 6.5f, 2.4f, Tokens.Text);
            gx.DrawTextAligned(c, Text(), (z.Left + z.Right) / 2, gx.Baseline(midY - 20, 40, 15, 700), 15, 700, Tokens.Accent, 0.5f);
            u.Fx.FluidRing(c, z, r, Zone.Ring.Value(now), now, false);
        }

        public bool Animating(double now) { return now - PressedAt < 0.3 || Zone.Animating(now); }

        /// <summary>Draws the pill with the value. In editing it lights up and carries the fluid border (instead of the row).</summary>
        public void Draw(UiContext u, SKCanvas c, double now)
        {
            if (Flat) { DrawFlat(u, c, now); return; }
            var gx = u.Gx;
            float f = Zone.Focus.Value(now);
            var pill = Zone.R;
            float x = pill.Left, midY = (pill.Top + pill.Bottom) / 2;
            gx.FillRR(c, pill, Height / 2, Tokens.White(0.08f + 0.10f * f));
            float bl = x + Pad + ButtonR, br = x + Width - Pad - ButtonR;
            bool pl = now - PressedAt < 0.2 && PressedSide < 0, pr = now - PressedAt < 0.2 && PressedSide > 0;
            gx.FillCircle(c, bl, midY, ButtonR, Tokens.White(pl ? 0.26f : 0.10f + 0.04f * f));
            gx.FillCircle(c, br, midY, ButtonR, Tokens.White(pr ? 0.26f : 0.10f + 0.04f * f));
            if (Glyph == SelectorGlyph.MinusPlus)
            {
                gx.Line(c, bl - 8, midY, bl + 8, midY, 2.6f, Tokens.Text);
                gx.Line(c, br - 8, midY, br + 8, midY, 2.6f, Tokens.Text);
                gx.Line(c, br, midY - 8, br, midY + 8, 2.6f, Tokens.Text);
            }
            else
            {
                using (var p = new SKPath())
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Tokens.Text })
                {
                    p.MoveTo(bl - 6, midY); p.LineTo(bl + 5, midY - 8); p.LineTo(bl + 5, midY + 8); p.Close();
                    p.MoveTo(br + 6, midY); p.LineTo(br - 5, midY - 8); p.LineTo(br - 5, midY + 8); p.Close();
                    c.DrawPath(p, paint);
                }
            }
            gx.DrawTextAligned(c, Text(), x + Width / 2, gx.Baseline(midY - 20, 40, 22, 800), 22, 800, Tokens.Text, 0.5f);
            u.Fx.FluidRing(c, pill, Height / 2, Zone.Ring.Value(now), now);
        }
    }
}
