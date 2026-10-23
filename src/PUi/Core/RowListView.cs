using System;
using System.Linq;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>Drawing of a ChannelListModel as 92 px rows: the fading ones, the rest and, on top, the focused one. The canvas is already shifted by the scroll.</summary>
    public static class RowListView
    {
        public static void Draw(UiContext u, SKCanvas c, ChannelListModel model, Focusable focused, double now, float scrollY)
        {
            foreach (var r in model.Fading) DrawRow(u, c, model, r, now, scrollY);
            foreach (var r in model.Visible) if (r.Item != focused) DrawRow(u, c, model, r, now, scrollY);
            if (focused != null && focused.Group == "rows")
            {
                var fr = model.Visible.FirstOrDefault(r => r.Item == focused);
                if (fr != null) DrawRow(u, c, model, fr, now, scrollY);
            }
        }

        private static void DrawRow(UiContext u, SKCanvas c, ChannelListModel model, ListRow r, double now, float sy)
        {
            float y = r.Y.Value(now);
            float dy = y - r.TargetY;
            float ap = r.Appear.Value(now);
            float alpha = ap * r.Fade.Value(now);
            if (r.Item == null) return;
            if (alpha <= 0.003f)
            {
                // not visible yet (it starts appearing in a few frames): its texture is created now so the entrance is just an animation, without an expensive frame midway
                if (r.Appear.Value(now) <= 0.003f && r.Fade.Value(now) > 0.5f && r.View.Key != null) Cards.Row(u, c, r.Item, r.View, now, 1f, true);
                return;
            }
            if (y + model.RowH - sy < -60 || y - sy > u.CH + 60) return;
            c.Save();
            c.Translate(0, dy + (1 - ap) * 24);
            Cards.Row(u, c, r.Item, r.View, now, alpha);
            c.Restore();
        }
    }
}
