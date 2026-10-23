using System;
using System.Globalization;

namespace AceEngine.Streaming
{
    public enum RangeResult
    {
        /// <summary>No Range header (or with a unit other than bytes): full 200 response.</summary>
        None,
        /// <summary>Valid and satisfiable range: 206.</summary>
        Satisfiable,
        /// <summary>Invalid range or outside the file: 416.</summary>
        NotSatisfiable
    }

    /// <summary>
    /// Interpretation of Range: bytes=a-b, bytes=a-, bytes=-n (suffix). Only one range is served;
    /// if several arrive the first one is used.
    /// </summary>
    public static class RangeHeader
    {
        public static RangeResult Parse(string header, long length, out long first, out long last)
        {
            first = 0; last = length - 1;
            if (string.IsNullOrWhiteSpace(header)) return RangeResult.None;
            header = header.Trim();
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeResult.None;
            string spec = header.Substring(6);
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma);
            spec = spec.Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return RangeResult.NotSatisfiable;
            string a = spec.Substring(0, dash).Trim();
            string b = spec.Substring(dash + 1).Trim();

            if (a.Length == 0)
            {
                // suffix: last n bytes
                long n;
                if (!TryParse(b, out n) || n <= 0) return RangeResult.NotSatisfiable;
                if (length <= 0) return RangeResult.NotSatisfiable;
                first = Math.Max(0, length - n);
                last = length - 1;
                return RangeResult.Satisfiable;
            }

            long start;
            if (!TryParse(a, out start)) return RangeResult.NotSatisfiable;
            long end;
            if (b.Length == 0) end = length - 1;
            else if (!TryParse(b, out end)) return RangeResult.NotSatisfiable;
            if (end < start) return RangeResult.NotSatisfiable;
            if (start >= length) return RangeResult.NotSatisfiable;
            if (end >= length) end = length - 1;
            first = start; last = end;
            return RangeResult.Satisfiable;
        }

        private static bool TryParse(string s, out long v)
        {
            v = 0;
            if (s.Length == 0 || s.Length > 18) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        }
    }
}
