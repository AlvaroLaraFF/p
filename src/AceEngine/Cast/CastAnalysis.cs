using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;

namespace AceEngine.Cast
{
    /// <summary>Compatibility check of a live channel that is already open: it reads the start of what the live buffer holds.</summary>
    public static class CastAnalysis
    {
        private const int Pkt = 188;

        /// <summary>
        /// Verdict for a live session that is already open (the channel the user is watching, or one the Cast source opened). Waits up to
        /// <paramref name="firstData"/> for the first bytes and then up to <paramref name="analysis"/> for the codecs to show: a PMT-only
        /// verdict (MP2, AC-3, HEVC...) comes back as soon as the PMT is read, usually well under a second after the data starts.
        /// Without the live buffer (LiveOptions.TimeshiftBytes = 0) it answers Unknown.
        /// </summary>
        public static Task<CastVerdict> AnalyzeSessionAsync(LiveSession session, CastProfile profile, TimeSpan firstData, TimeSpan analysis, CancellationToken ct)
        {
            if (session == null) throw new ArgumentNullException("session");
            var buf = session.TimeshiftBuffer;
            if (buf == null)
            {
                var v = new CastVerdict { Profile = profile ?? CastProfile.Chromecast2, Support = CastSupport.Unknown };
                v.Issues.Add(new CastIssue { Code = CastIssueCode.Other, Blocking = false, Message = "el directo no guarda memoria", Detail = "LiveOptions.TimeshiftBytes is 0: nothing to read the codecs from" });
                return Task.FromResult(v);
            }
            return AnalyzeBufferAsync(buf, profile, firstData, analysis, ct);
        }

        internal static async Task<CastVerdict> AnalyzeBufferAsync(TimeshiftBuffer buf, CastProfile profile, TimeSpan firstData, TimeSpan analysis, CancellationToken ct)
        {
            var probe = new TsStreamProbe();
            var chunk = new byte[Pkt * 256];
            long pos = -1;
            var started = DateTime.UtcNow;
            DateTime? dataAt = null;
            long lastCuts = buf.CutCount;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var signal = buf.Appended.Wait();
                long write = buf.WriteOffset, oldest = buf.OldestOffset;
                if (pos < 0 && write - oldest >= 4 * Pkt) { pos = oldest; dataAt = DateTime.UtcNow; }
                if (pos >= 0)
                {
                    if (pos < oldest) pos = oldest;
                    while (pos + Pkt <= write)
                    {
                        int n = (int)Math.Min(chunk.Length, (write - pos) / Pkt * Pkt);
                        if (!buf.CopyRange(pos, pos + n, chunk, 0)) { pos = buf.OldestOffset; break; }
                        probe.Feed(chunk, 0, n);
                        pos += n;
                    }
                    if (buf.CutCount != lastCuts) { lastCuts = buf.CutCount; probe.Discontinuity(); }
                    var info = probe.Snapshot();
                    var v = CastCompatibility.Evaluate(info, profile, false);
                    if (v.IsIncompatible || info.IsComplete) return v;
                    if (DateTime.UtcNow - dataAt.Value > analysis) return CastCompatibility.Evaluate(info, profile, true);
                }
                else if (DateTime.UtcNow - started > firstData)
                {
                    var v = CastCompatibility.Evaluate(probe.Snapshot(), profile, false);
                    v.Support = CastSupport.Unknown;
                    v.Issues.Add(new CastIssue { Code = CastIssueCode.Other, Blocking = false, Message = "el canal no ha enviado datos", Detail = "no data in " + (int)firstData.TotalSeconds + " s" });
                    return v;
                }
                if (buf.IsCompleted && pos >= 0 && pos + Pkt > buf.WriteOffset) return CastCompatibility.Evaluate(probe.Snapshot(), profile, true);
                var tick = Task.Delay(250, ct);                                       // a pulse wakes it; with no data it still re-checks the clocks
                await Task.WhenAny(signal, tick).ConfigureAwait(false);
            }
        }
    }
}
