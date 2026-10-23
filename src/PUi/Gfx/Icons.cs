using System;
using System.Collections.Generic;
using SkiaSharp;

namespace PUi.Gfx
{
    /// <summary>A vector icon on a 24x24 canvas (like the design's SVGs): strokes and, optionally, fills.</summary>
    public sealed class Icon
    {
        public readonly List<SKPath> StrokePaths = new List<SKPath>();
        public readonly List<SKPath> FillPaths = new List<SKPath>();
        public Icon S(string d) { StrokePaths.Add(SvgPath.Parse(d)); return this; }
        public Icon F(string d) { FillPaths.Add(SvgPath.Parse(d)); return this; }
        public Icon SCircle(float cx, float cy, float r) { var p = new SKPath(); p.AddCircle(cx, cy, r); StrokePaths.Add(p); return this; }
        public Icon SRoundRect(float x, float y, float w, float h, float rx) { var p = new SKPath(); p.AddRoundRect(new SKRect(x, y, x + w, y + h), rx, rx); StrokePaths.Add(p); return this; }
    }

    /// <summary>Interface icons (paths copied from the design).</summary>
    public static class Icons
    {
        private const string Circle = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z";

        public static readonly Icon Tv = new Icon().SRoundRect(3, 5, 18, 12, 2.5f).S("M8 21h8M12 17v4");
        public static readonly Icon Search = new Icon().SCircle(11, 11, 6.5f).S("M20 20l-4.2-4.2");
        public static readonly Icon Sliders = new Icon().S("M4 7h10M18 7h2M4 17h4M12 17h8").SCircle(16, 7, 2).SCircle(10, 17, 2);
        public static readonly Icon Chevron = new Icon().S("M9 6l6 6-6 6");
        public static readonly Icon People = new Icon().S("M10 11a3 3 0 1 0 0-6a3 3 0 1 0 0 6z M4 19v-1a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v1 M16 5.3a3 3 0 0 1 0 5.4 M20 19v-1a4 4 0 0 0-2.5-3.7");
        public static readonly Icon ArrowDown = new Icon().S("M12 4v14 M6 12l6 6 6-6");
        public static readonly Icon ArrowUp = new Icon().S("M12 20V6 M6 12l6-6 6 6");
        public static readonly Icon Seeds = new Icon().S("M12 21v-9 M12 12c0-4 3-6.5 7.5-6.5 0 4.2-3 6.5-7.5 6.5z M12 14c0-3.2-2.4-5.5-6.5-5.5 0 3.3 2.4 5.5 6.5 5.5z");
        public static readonly Icon Alert = new Icon().S("M12 4l9 16H3z M12 10v4.5 M12 17.5v.5");
        public static readonly Icon EyeOff = new Icon().S("M3 3l18 18 M10.6 6.1A9.6 9.6 0 0 1 12 6c5 0 8.5 4.5 9.5 6-.5.8-1.5 2.2-3 3.5 M6.6 7.6C4.6 9 3.2 10.9 2.5 12c1 1.5 4.5 6 9.5 6 1.6 0 3-.4 4.3-1.1");
        public static readonly Icon Back10 = new Icon().S("M11 6l-6 6 6 6 M19 6l-6 6 6 6");
        public static readonly Icon Forward10 = new Icon().S("M5 6l6 6-6 6 M13 6l6 6-6 6");
        public static readonly Icon Play = new Icon().F("M8 5v14l11-7z");
        public static readonly Icon Pause = new Icon().F("M7 5h3.5v14H7z M13.5 5H17v14h-3.5z");
        public static readonly Icon Backspace = new Icon().S("M9 5h11a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H9l-6-7z M12 9.5l5 5 M17 9.5l-5 5");
        public static readonly Icon Space = new Icon().S("M4 10v5h16v-5");
        public static readonly Icon SortBars = new Icon().S("M4 7h16 M4 12h11 M4 17h6");
        public static readonly Icon Arrival = new Icon().S("M12 4v11 M7 11l5 5 5-5 M5 20h14");
        public static readonly Icon Close = new Icon().S("M6 6l12 12 M18 6L6 18");

        // phones and tablets (design-movil)
        public static readonly Icon Back = new Icon().S("M15 6l-6 6 6 6");
        public static readonly Icon Cast = new Icon().S("M3 9V7a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-6 M3 13a6 6 0 0 1 6 6 M3 17a2 2 0 0 1 2 2");
        public static readonly Icon SortArrows = new Icon().S("M7 4v16 M4 7l3-3 3 3 M17 20V4 M14 17l3 3 3-3");
        public static readonly Icon Expand = new Icon().S("M4 9V4h5 M20 9V4h-5 M4 15v5h5 M20 15v5h-5");
        public static readonly Icon Compress = new Icon().S("M9 4v5H4 M15 4v5h5 M9 20v-5H4 M15 20v-5h5");
        public static readonly Icon Volume = new Icon().S("M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z M15.5 9a4 4 0 0 1 0 6 M18 6.5a7.5 7.5 0 0 1 0 11");
        public static readonly Icon Clock = new Icon().S("M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16z M12 8v4l3 2");

        /// <summary>Explore icons by kind id (futbol, baloncesto, ...).</summary>
        public static Icon ForKind(string kindId)
        {
            Icon i;
            return KindIcons.TryGetValue(kindId ?? "", out i) ? i : KindIcons["internacional"];
        }

        private static readonly Dictionary<string, Icon> KindIcons = new Dictionary<string, Icon>
        {
            { "futbol", new Icon().S(Circle + " M12 8l3.5 2.5-1.3 4h-4.4l-1.3-4z") },
            { "baloncesto", new Icon().S(Circle + " M3 12h18 M12 3v18 M5.6 5.6c3 3 3 9.8 0 12.8 M18.4 5.6c-3 3-3 9.8 0 12.8") },
            { "motor", new Icon().S(Circle + " M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z M3.5 10.5l5.6 1 M20.5 10.5l-5.6 1 M12 15v6") },
            { "tenis", new Icon().S(Circle + " M5.6 5.6c2.5 2.5 2.5 10.3 0 12.8 M18.4 5.6c-2.5 2.5-2.5 10.3 0 12.8") },
            { "deportes", new Icon().S("M8 4h8v5a4 4 0 0 1-8 0z M8 6H5a3 3 0 0 0 3 4 M16 6h3a3 3 0 0 1-3 4 M12 13v4 M9 21h6 M10 17h4v4h-4z") },
            { "noticias", new Icon().S("M4 5h13v14H6a2 2 0 0 1-2-2z M17 9h3v8a2 2 0 0 1-2 2 M7 9h7 M7 13h7 M7 16h4") },
            { "cine", new Icon().S("M3 8h18v11H3z M3 8l3-4h4l-3 4 M10 4h4l-3 4 M14 4h4l-3 4") },
            { "series", new Icon().S("M3 5h18v12H3z M10 9l4.5 2-4.5 2z M8 21h8") },
            { "documentales", new Icon().S(Circle + " M15.5 8.5l-2 5-5 2 2-5z") },
            { "musica", new Icon().S("M9 18V6l11-2v12 M9 18a3 3 0 1 1-6 0a3 3 0 1 1 6 0z M20 16a3 3 0 1 1-6 0a3 3 0 1 1 6 0z") },
            { "infantil", new Icon().S("M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8-4.3-4.1 5.9-.9z") },
            { "internacional", new Icon().S(Circle + " M3 12h18 M12 3c2.5 2.5 3.8 5.5 3.8 9s-1.3 6.5-3.8 9 M12 3c-2.5 2.5-3.8 5.5-3.8 9s1.3 6.5 3.8 9") },
        };
    }
}
