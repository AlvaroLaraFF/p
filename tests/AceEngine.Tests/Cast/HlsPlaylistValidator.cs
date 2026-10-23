using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AceEngine.Tests.Cast
{
    /// <summary>A strict reader of media playlists for the checks of RFC 8216 that matter to a live stream. It throws FormatException with the rule that failed.</summary>
    internal sealed class HlsPlaylistModel
    {
        public sealed class Seg
        {
            public string Uri;
            public double Duration;
            public bool Discontinuity;
            public DateTime? Pdt;
        }

        public int Version = 1, TargetDuration = -1;
        public long MediaSequence = 0, DiscontinuitySequence = 0;
        public bool Ended;
        public readonly List<Seg> Segments = new List<Seg>();

        private static void Fail(string rule) { throw new FormatException("RFC 8216: " + rule); }

        private static readonly Regex Iso = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})$");

        public static HlsPlaylistModel Parse(string text, bool expectLive = true)
        {
            if (text == null) Fail("no playlist");
            if (text.Length > 0 && text[0] == (char)0xFEFF) Fail("a playlist must not start with a BOM");
            if (text.Contains("\r")) Fail("this server writes LF only");
            if (!text.EndsWith("\n")) Fail("the last line must end with a newline");
            var lines = text.Split('\n');
            if (lines[0] != "#EXTM3U") Fail("the first line must be #EXTM3U");
            var m = new HlsPlaylistModel();
            bool sawVersion = false, sawTarget = false, sawSeq = false, sawDiscSeq = false;
            double? pendingDur = null;
            bool pendingDisc = false;
            DateTime? pendingPdt = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0) continue;
                if (l != l.Trim()) Fail("whitespace around a line: '" + l + "'");
                if (l.StartsWith("#EXT"))
                {
                    int colon = l.IndexOf(':');
                    string tag = colon < 0 ? l : l.Substring(0, colon);
                    string val = colon < 0 ? "" : l.Substring(colon + 1);
                    switch (tag)
                    {
                        case "#EXT-X-VERSION":
                            if (sawVersion) Fail("EXT-X-VERSION appears more than once");
                            sawVersion = true; m.Version = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "#EXT-X-TARGETDURATION":
                            if (sawTarget) Fail("EXT-X-TARGETDURATION appears more than once");
                            if (!Regex.IsMatch(val, @"^\d+$")) Fail("TARGETDURATION must be an integer");
                            sawTarget = true; m.TargetDuration = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "#EXT-X-MEDIA-SEQUENCE":
                            if (sawSeq) Fail("EXT-X-MEDIA-SEQUENCE appears more than once");
                            if (m.Segments.Count > 0 || pendingDur.HasValue) Fail("EXT-X-MEDIA-SEQUENCE must come before the first segment");
                            sawSeq = true; m.MediaSequence = long.Parse(val, CultureInfo.InvariantCulture); break;
                        case "#EXT-X-DISCONTINUITY-SEQUENCE":
                            if (sawDiscSeq) Fail("EXT-X-DISCONTINUITY-SEQUENCE appears more than once");
                            if (m.Segments.Count > 0 || pendingDur.HasValue) Fail("EXT-X-DISCONTINUITY-SEQUENCE must come before the first segment");
                            sawDiscSeq = true; m.DiscontinuitySequence = long.Parse(val, CultureInfo.InvariantCulture); break;
                        case "#EXTINF":
                            {
                                if (pendingDur.HasValue) Fail("two EXTINF without a URI in between");
                                int comma = val.IndexOf(',');
                                if (comma < 0) Fail("EXTINF needs a comma");
                                string d = val.Substring(0, comma);
                                if (!Regex.IsMatch(d, @"^\d+(\.\d+)?$")) Fail("EXTINF duration '" + d + "'");
                                if (d.Contains(".") && m.Version < 3) Fail("a decimal EXTINF needs EXT-X-VERSION 3 or more");
                                pendingDur = double.Parse(d, CultureInfo.InvariantCulture);
                                break;
                            }
                        case "#EXT-X-DISCONTINUITY":
                            if (pendingDur.HasValue) Fail("EXT-X-DISCONTINUITY must precede EXTINF");
                            pendingDisc = true; break;
                        case "#EXT-X-PROGRAM-DATE-TIME":
                            if (pendingDur.HasValue) Fail("EXT-X-PROGRAM-DATE-TIME must precede EXTINF");
                            if (!Iso.IsMatch(val)) Fail("PROGRAM-DATE-TIME '" + val + "' is not ISO 8601");
                            pendingPdt = DateTime.Parse(val, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                            break;
                        case "#EXT-X-ENDLIST":
                            m.Ended = true; break;
                        default:
                            Fail("unexpected tag " + tag); break;
                    }
                }
                else if (l.StartsWith("#")) { /* comment */ }
                else
                {
                    if (!pendingDur.HasValue) Fail("URI without EXTINF: " + l);
                    if (l.Contains(" ") || l.Contains("\\")) Fail("URI with spaces or backslashes: " + l);
                    m.Segments.Add(new Seg { Uri = l, Duration = pendingDur.Value, Discontinuity = pendingDisc, Pdt = pendingPdt });
                    pendingDur = null; pendingDisc = false; pendingPdt = null;
                }
            }
            if (pendingDur.HasValue) Fail("EXTINF without a URI at the end");
            if (!sawTarget) Fail("EXT-X-TARGETDURATION is required");
            if (m.Segments.Count == 0) Fail("a playlist needs at least one segment");
            foreach (var s in m.Segments)
                if (Math.Round(s.Duration, MidpointRounding.AwayFromZero) > m.TargetDuration)
                    Fail("EXTINF " + s.Duration.ToString(CultureInfo.InvariantCulture) + " rounds above TARGETDURATION " + m.TargetDuration);
            if (expectLive && m.Ended) Fail("a live playlist must not carry EXT-X-ENDLIST");
            // PROGRAM-DATE-TIME chain: each date follows the previous one by its duration, unless the timeline restarted
            for (int i = 1; i < m.Segments.Count; i++)
            {
                var a = m.Segments[i - 1]; var b = m.Segments[i];
                if (a.Pdt.HasValue && b.Pdt.HasValue)
                {
                    double gap = (b.Pdt.Value - a.Pdt.Value).TotalSeconds - a.Duration;
                    if (!b.Discontinuity && Math.Abs(gap) > 0.002) Fail("PROGRAM-DATE-TIME of " + b.Uri + " is " + gap.ToString("0.000", CultureInfo.InvariantCulture) + " s off the previous date + duration");
                    if (b.Pdt.Value < a.Pdt.Value) Fail("PROGRAM-DATE-TIME goes backwards");
                }
            }
            return m;
        }
    }
}
