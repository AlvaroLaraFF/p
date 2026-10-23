using System;
using System.Collections.Generic;

namespace AceEngine.Cast.Dlna
{
    /// <summary>
    /// One way of presenting the live MPEG-TS to a renderer: the MIME type of the HTTP response and of the protocolInfo, how the body ends and the DLNA flags.
    /// There is no DLNA certification in force any more and every brand and firmware behaves a little differently; the sender tries the variants in order until
    /// the renderer reports PLAYING and fetches the stream.
    /// </summary>
    public sealed class DlnaVariant
    {
        public string Name = "";
        public string MimeType = "video/mp2t";
        public ProgressiveMode Mode = ProgressiveMode.CloseDelimited;
        /// <summary>DLNA.ORG_FLAGS: 8 hex digits and 24 zeros.</summary>
        public string Flags = "01700000000000000000000000000000";
        /// <summary>DLNA.ORG_PN: only when the profile is certain (it is not, for live channels): empty.</summary>
        public string ProfileName = "";

        public DlnaStreamHeaders Headers { get { return new DlnaStreamHeaders { ProfileName = ProfileName, Operation = "00", Flags = Flags }; } }
        public string Features { get { return Headers.ContentFeatures; } }
        public override string ToString() { return Name + " (" + MimeType + ", " + Mode + ")"; }
    }

    /// <summary>
    /// What is known about how each brand takes an endless MPEG-TS over HTTP. UNVERIFIED on current firmware: the entries come from public captures (a Samsung E/F series server log:
    /// Content-Type video/mpeg, Content-Length 50000000000, Accept-Ranges none, Connection close, OP=00, CI=0, FLAGS=8D5…) and forum reports (LG webOS refuses MPEG-TS with the wrong
    /// MIME, the DLNA MIME for TS is video/vnd.dlna.mpeg-tts). The order of the list is the order tried.
    /// </summary>
    public static class DlnaQuirks
    {
        /// <summary>Flags of a sender-paced live stream with no seeking (Samsung's own captures): streaming, background and v1.5, plus sender paced and the S0/SN increase bits.</summary>
        public const string SenderPacedFlags = "8D500000000000000000000000000000";
        public const string StreamingFlags = "01700000000000000000000000000000";

        public static List<DlnaVariant> VariantsFor(string manufacturer, string model)
        {
            string m = (manufacturer ?? "").ToLowerInvariant();
            var l = new List<DlnaVariant>();
            if (m.Contains("samsung"))
            {
                // The Q64T (2020) lists video/mpeg (MPEG_TS_*_ISO, plain 188-byte TS) and video/vnd.dlna.mpeg-tts (the _T profiles are 192-byte timestamped TS: not what we send) in its Sink
                // and no video/mp2t at all, so video/mpeg goes first.
                l.Add(new DlnaVariant { Name = "samsung-mpeg-biglength", MimeType = "video/mpeg", Mode = ProgressiveMode.BigContentLength, Flags = SenderPacedFlags });
                l.Add(new DlnaVariant { Name = "mpeg-close", MimeType = "video/mpeg", Mode = ProgressiveMode.CloseDelimited, Flags = SenderPacedFlags });
                l.Add(new DlnaVariant { Name = "mpeg-ts-iso-close", MimeType = "video/mpeg", Mode = ProgressiveMode.CloseDelimited, Flags = SenderPacedFlags, ProfileName = "MPEG_TS_NA_ISO" });
                l.Add(new DlnaVariant { Name = "ts-close", MimeType = "video/mp2t", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
            }
            else if (m.Contains("lg"))
            {
                l.Add(new DlnaVariant { Name = "ts-close", MimeType = "video/mp2t", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
                l.Add(new DlnaVariant { Name = "dlna-tts-close", MimeType = "video/vnd.dlna.mpeg-tts", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
                l.Add(new DlnaVariant { Name = "mpeg-close", MimeType = "video/mpeg", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
            }
            else
            {
                l.Add(new DlnaVariant { Name = "ts-close", MimeType = "video/mp2t", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
                l.Add(new DlnaVariant { Name = "mpeg-close", MimeType = "video/mpeg", Mode = ProgressiveMode.CloseDelimited, Flags = StreamingFlags });
                l.Add(new DlnaVariant { Name = "ts-chunked", MimeType = "video/mp2t", Mode = ProgressiveMode.Chunked, Flags = StreamingFlags });
            }
            return l;
        }

        /// <summary>Puts first the variants whose MIME type the renderer listed in its Sink (GetProtocolInfo), keeping the order otherwise. A renderer that lists nothing keeps the default order.</summary>
        public static List<DlnaVariant> Prefer(List<DlnaVariant> variants, IEnumerable<DlnaProtocolInfo> sink)
        {
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sink != null) foreach (var s in sink) if (s.Protocol.Equals("http-get", StringComparison.OrdinalIgnoreCase)) accepted.Add(s.ContentFormat);
            if (accepted.Count == 0) return variants;
            var first = new List<DlnaVariant>(); var rest = new List<DlnaVariant>();
            foreach (var v in variants) (accepted.Contains(v.MimeType) ? first : rest).Add(v);
            first.AddRange(rest);
            return first;
        }
    }
}
