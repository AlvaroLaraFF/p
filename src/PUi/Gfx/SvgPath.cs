using System;
using System.Globalization;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>
    /// Own reader of SVG path data (M L H V C S Q T A Z, absolute and relative, with unseparated arc flags). The one in the TV's libSkiaSharp
    /// (old Skia) returned null for some of the design's paths; this way all platforms draw exactly the same.
    /// </summary>
    public static class SvgPath
    {
        public static SKPath Parse(string d)
        {
            var path = new SKPath();
            int i = 0, n = d.Length;
            float cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0;     // current point, subpath start, last control
            char cmd = ' ', prev = ' ';
            while (true)
            {
                SkipSep(d, ref i);
                if (i >= n) break;
                char ch = d[i];
                if (char.IsLetter(ch)) { cmd = ch; i++; if (cmd == 'z' || cmd == 'Z') { path.Close(); cx = sx; cy = sy; prev = 'z'; continue; } }
                else if (cmd == ' ') break;
                else if (cmd == 'M') cmd = 'L'; else if (cmd == 'm') cmd = 'l';
                bool rel = char.IsLower(cmd);
                char c = char.ToUpperInvariant(cmd);
                float x, y, x1, y1, x2, y2;
                switch (c)
                {
                    case 'M':
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.MoveTo(x, y); cx = sx = x; cy = sy = y; break;
                    case 'L':
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.LineTo(x, y); cx = x; cy = y; break;
                    case 'H': x = Num(d, ref i) + (rel ? cx : 0); path.LineTo(x, cy); cx = x; break;
                    case 'V': y = Num(d, ref i) + (rel ? cy : 0); path.LineTo(cx, y); cy = y; break;
                    case 'C':
                        x1 = Num(d, ref i) + (rel ? cx : 0); y1 = Num(d, ref i) + (rel ? cy : 0);
                        x2 = Num(d, ref i) + (rel ? cx : 0); y2 = Num(d, ref i) + (rel ? cy : 0);
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.CubicTo(x1, y1, x2, y2, x, y); lcx = x2; lcy = y2; cx = x; cy = y; break;
                    case 'S':
                        x1 = (prev == 'C' || prev == 'S') ? 2 * cx - lcx : cx; y1 = (prev == 'C' || prev == 'S') ? 2 * cy - lcy : cy;
                        x2 = Num(d, ref i) + (rel ? cx : 0); y2 = Num(d, ref i) + (rel ? cy : 0);
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.CubicTo(x1, y1, x2, y2, x, y); lcx = x2; lcy = y2; cx = x; cy = y; break;
                    case 'Q':
                        x1 = Num(d, ref i) + (rel ? cx : 0); y1 = Num(d, ref i) + (rel ? cy : 0);
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.QuadTo(x1, y1, x, y); lcx = x1; lcy = y1; cx = x; cy = y; break;
                    case 'T':
                        x1 = (prev == 'Q' || prev == 'T') ? 2 * cx - lcx : cx; y1 = (prev == 'Q' || prev == 'T') ? 2 * cy - lcy : cy;
                        x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                        path.QuadTo(x1, y1, x, y); lcx = x1; lcy = y1; cx = x; cy = y; break;
                    case 'A':
                        {
                            float rx = Num(d, ref i), ry = Num(d, ref i), rot = Num(d, ref i);
                            bool large = Flag(d, ref i), sweep = Flag(d, ref i);
                            x = Num(d, ref i) + (rel ? cx : 0); y = Num(d, ref i) + (rel ? cy : 0);
                            if (rx == 0 || ry == 0) path.LineTo(x, y);
                            else path.ArcTo(Math.Abs(rx), Math.Abs(ry), rot, large ? SKPathArcSize.Large : SKPathArcSize.Small, sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, x, y);
                            cx = x; cy = y; break;
                        }
                    default: return path;
                }
                prev = c;
            }
            return path;
        }

        private static void SkipSep(string d, ref int i) { while (i < d.Length && (d[i] == ' ' || d[i] == ',' || d[i] == '\t' || d[i] == '\n' || d[i] == '\r')) i++; }

        private static bool Flag(string d, ref int i)
        {
            SkipSep(d, ref i);
            bool v = i < d.Length && d[i] == '1';
            i++;
            return v;
        }

        private static float Num(string d, ref int i)
        {
            SkipSep(d, ref i);
            int s = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false;
            while (i < d.Length)
            {
                char c = d[i];
                if (c >= '0' && c <= '9') i++;
                else if (c == '.' && !dot) { dot = true; i++; }
                else if ((c == 'e' || c == 'E') && i + 1 < d.Length && (char.IsDigit(d[i + 1]) || d[i + 1] == '-' || d[i + 1] == '+')) { i += 2; }
                else break;
            }
            float v;
            return float.TryParse(d.Substring(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }
    }
}
