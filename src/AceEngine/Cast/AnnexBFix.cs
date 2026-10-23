using System;
using System.Collections.Generic;

namespace AceEngine.Cast
{
    /// <summary>
    /// Puts the NAL units at the start of every H.264 access unit of a transport-stream segment in the order the standard describes (access unit delimiter, SPS, PPS, SEI, then the
    /// slices). Some encoders send the SEI before the parameter sets (M+ and others) or the parameter sets before the delimiter (a Russian sports channel), and the HLS player of the
    /// Cast receiver family (Shaka Player in Chrome: error 3016 PIPELINE_ERROR_DECODE) cannot decode those; with the order fixed the same streams play. The bytes are only permuted
    /// inside the access unit's own payload: the number of bytes, the PES headers and the TS packet boundaries do not change.
    /// </summary>
    internal static class AnnexBFix
    {
        private const int Pkt = 188;
        private const int MaxPrefixBytes = 8192;                 // the parameter sets and SEI of an access unit are far smaller than this
        private const int MaxScanPackets = 120;                  // packets looked at (all PIDs) to gather MaxPrefixBytes of one access unit

        /// <summary>Fixes the access units that start in buf[start, end) (whole packets of the segment, as composed). Returns how many were reordered.</summary>
        public static int NormalizeParameterSets(byte[] buf, int start, int end, int videoPid)
        {
            int fixedAus = 0;
            for (int i = start; i + Pkt <= end; i += Pkt)
            {
                if (buf[i] != 0x47 || (((buf[i + 1] & 0x1F) << 8) | buf[i + 2]) != videoPid || (buf[i + 1] & 0x40) == 0) continue;
                try { if (FixAu(buf, i, end, videoPid)) fixedAus++; }
                catch (IndexOutOfRangeException) { /* a truncated access unit: left as it is */ }
            }
            return fixedAus;
        }

        private struct Range { public int Pos, Len; }

        private static bool FixAu(byte[] buf, int first, int end, int videoPid)
        {
            // payload ranges of the first packets of this access unit (video PID only, up to the next PES start)
            var ranges = new List<Range>(48);
            int total = 0;
            bool firstPacket = true;
            for (int i = first, scanned = 0; i + Pkt <= end && scanned < MaxScanPackets && total < MaxPrefixBytes; i += Pkt, scanned++)
            {
                if (buf[i] != 0x47) return false;
                if ((((buf[i + 1] & 0x1F) << 8) | buf[i + 2]) != videoPid) continue;
                bool pusi = (buf[i + 1] & 0x40) != 0;
                if (pusi && !firstPacket) break;                                     // the next access unit
                int afc = (buf[i + 3] >> 4) & 3;
                int ps = i + 4;
                if ((afc & 2) != 0) { int af = buf[i + 4]; if (af > 183) return false; ps = i + 5 + af; }
                if ((afc & 1) == 0 || ps >= i + Pkt) { if (firstPacket) return false; continue; }
                if (firstPacket)
                {
                    // PES header: 00 00 01 E0.. len(2) flags(2) hdrLen
                    if (i + Pkt - ps < 9 || buf[ps] != 0 || buf[ps + 1] != 0 || buf[ps + 2] != 1 || (buf[ps + 3] & 0xF0) != 0xE0) return false;
                    ps += 9 + buf[ps + 8];
                    if (ps >= i + Pkt) return false;                                 // the header spills over the packet: not handled
                    firstPacket = false;
                }
                int len = i + Pkt - ps;
                ranges.Add(new Range { Pos = ps, Len = len });
                total += len;
            }
            if (total < 8) return false;
            var es = new byte[total];
            int o = 0;
            foreach (var r in ranges) { Buffer.BlockCopy(buf, r.Pos, es, o, r.Len); o += r.Len; }

            // chunks: from each 3-byte start code to the next one (the zero byte that makes a 4-byte start code ends the previous chunk and travels with it)
            var starts = new List<int>(8);
            for (int p = 0; p + 3 < total; )
            {
                if (es[p] == 0 && es[p + 1] == 0 && es[p + 2] == 1)
                {
                    starts.Add(p);
                    int t = es[p + 3] & 0x1F;
                    if (t >= 1 && t <= 5) break;                                     // the first slice: the prefix ends here
                    p += 3;
                }
                else p++;
            }
            if (starts.Count < 2) return false;
            int vcl = starts[starts.Count - 1];
            int vclType = es[vcl + 3] & 0x1F;
            if (vclType < 1 || vclType > 5) return false;                            // no slice within the bytes gathered
            int n = starts.Count - 1;                                                // chunks before the first slice
            var types = new int[n];
            var order = new int[n];
            for (int c = 0; c < n; c++)
            {
                types[c] = es[starts[c] + 3] & 0x1F;
                if (types[c] != 9 && types[c] != 7 && types[c] != 8 && types[c] != 6) return false;   // anything else in front of the slice: leave the access unit alone
                order[c] = c;
            }
            // stable order by class
            Array.Sort(order, (a, b) => { int d = Rank(types[a]) - Rank(types[b]); return d != 0 ? d : a - b; });
            bool same = true;
            for (int c = 0; c < n; c++) if (order[c] != c) same = false;
            if (same) return false;

            int lead = starts[0];                                                    // bytes before the first start code stay first
            var outp = new byte[total];
            Buffer.BlockCopy(es, 0, outp, 0, lead);
            int w = lead;
            for (int c = 0; c < n; c++)
            {
                int a = starts[order[c]];
                int b = starts[order[c] + 1];
                Buffer.BlockCopy(es, a, outp, w, b - a);
                w += b - a;
            }
            Buffer.BlockCopy(es, vcl, outp, w, total - vcl);
            o = 0;
            foreach (var r in ranges) { Buffer.BlockCopy(outp, o, buf, r.Pos, r.Len); o += r.Len; }
            return true;
        }

        private static int Rank(int nalType)
        {
            switch (nalType) { case 9: return 0; case 7: return 1; case 8: return 2; default: return 3; }
        }
    }
}
