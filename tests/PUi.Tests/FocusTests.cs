using System.Collections.Generic;
using System.Linq;
using PUi.Core;
using PUi.Input;
using SkiaSharp;
using Xunit;

namespace PUi.Tests
{
    public class SpatialTests
    {
        private static List<Focusable> Grid(int cols, int rows, float w = 300, float h = 80, float gx = 24, float gy = 24, float x0 = 200, float y0 = 300)
        {
            var l = new List<Focusable>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    l.Add(new Focusable { Id = r + "," + c, R = new SKRect(x0 + c * (w + gx), y0 + r * (h + gy), x0 + c * (w + gx) + w, y0 + r * (h + gy) + h) });
            return l;
        }

        [Fact]
        public void MovesAcrossAGridInAllDirections()
        {
            var g = Grid(5, 2);
            var cur = g.First(f => f.Id == "0,2");
            Assert.Equal("0,3", Spatial.Find(g, cur, UiKey.Right).Id);
            Assert.Equal("0,1", Spatial.Find(g, cur, UiKey.Left).Id);
            Assert.Equal("1,2", Spatial.Find(g, cur, UiKey.Down).Id);
            Assert.Null(Spatial.Find(g, cur, UiKey.Up));
            Assert.Null(Spatial.Find(g, g.First(f => f.Id == "0,4"), UiKey.Right));
            Assert.Null(Spatial.Find(g, g.First(f => f.Id == "1,2"), UiKey.Down));
        }

        [Fact]
        public void RecentsRowToCategoriesPicksTheNearestColumn()
        {
            // 5 cards of 302.4 with a step of 330.4 and 5 categories of 305.6 with a step of 329.6: from card i you go down to category i
            var items = new List<Focusable>();
            for (int i = 0; i < 5; i++) items.Add(new Focusable { Id = "r" + i, R = new SKRect(200 + i * 330.4f, 96, 200 + i * 330.4f + 302.4f, 228) });
            for (int i = 0; i < 5; i++) items.Add(new Focusable { Id = "c" + i, R = new SKRect(200 + i * 329.6f, 326, 200 + i * 329.6f + 305.6f, 406) });
            for (int i = 0; i < 5; i++) Assert.Equal("c" + i, Spatial.Find(items, items[i], UiKey.Down).Id);
            for (int i = 0; i < 5; i++) Assert.Equal("r" + i, Spatial.Find(items, items[5 + i], UiKey.Up).Id);
        }

        [Fact]
        public void ListRowsMoveUpAndDownOnly()
        {
            var rows = new List<Focusable>();
            for (int i = 0; i < 5; i++) rows.Add(new Focusable { Id = "row" + i, R = new SKRect(200, 179 + i * 106, 1824, 179 + i * 106 + 92) });
            Assert.Equal("row1", Spatial.Find(rows, rows[0], UiKey.Down).Id);
            Assert.Equal("row3", Spatial.Find(rows, rows[4], UiKey.Up).Id);
            Assert.Null(Spatial.Find(rows, rows[2], UiKey.Left));
            Assert.Null(Spatial.Find(rows, rows[2], UiKey.Right));
        }

        [Fact]
        public void DisabledItemsAreSkipped()
        {
            var g = Grid(3, 1);
            g[1].Enabled = false;
            Assert.Equal("0,2", Spatial.Find(g, g[0], UiKey.Right).Id);
        }

        [Fact]
        public void TwoColumnsOfSettingsRowsConnectHorizontally()
        {
            var a = new Focusable { Id = "a", R = new SKRect(200, 96, 992, 208) };
            var b = new Focusable { Id = "b", R = new SKRect(200, 243, 992, 407) };
            var c = new Focusable { Id = "c", R = new SKRect(1032, 96, 1824, 234) };
            var l = new List<Focusable> { a, b, c };
            Assert.Equal("c", Spatial.Find(l, a, UiKey.Right).Id);
            Assert.Equal("c", Spatial.Find(l, b, UiKey.Right).Id);
            Assert.Equal("a", Spatial.Find(l, c, UiKey.Left).Id);
        }
    }

    public class ScrollAreaTests
    {
        [Fact]
        public void KeepsTheFocusedRectVisible()
        {
            var s = new ScrollArea { ViewTop = 72, ViewBottom = 1024, ContentHeight = 2000 };
            s.EnsureVisible(new SKRect(200, 100, 500, 200), 0);
            Assert.Equal(0, s.Target);
            s.EnsureVisible(new SKRect(200, 1500, 500, 1592), 0);
            Assert.InRange(s.Target, 590, 600);
            s.EnsureVisible(new SKRect(200, 100, 500, 200), 1);
            Assert.Equal(0, s.Target);
        }

        [Fact]
        public void NeverScrollsPastTheContent()
        {
            var s = new ScrollArea { ViewTop = 72, ViewBottom = 1024, ContentHeight = 1100 };
            s.EnsureVisible(new SKRect(0, 1080, 100, 1100), 0);
            Assert.True(s.Target <= s.MaxOffset + 0.01f);
        }
    }
}
