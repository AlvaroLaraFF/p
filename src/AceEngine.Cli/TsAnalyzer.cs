using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AceEngine.Cli
{
    /// <summary>
    /// Minimal MPEG-TS analyzer (without ffprobe): checks the 0x47 sync every 188 bytes, counts the continuity counter errors
    /// per PID and reads the PAT/PMT to list the streams (video/audio).
    /// </summary>
    internal sealed class TsAnalyzer
    {
        private readonly List<byte> _pending = new List<byte>();
        public long Bytes, Packets, SyncLosses, TransportErrors;
        public readonly Dictionary<int, long> PidPackets = new Dictionary<int, long>();
        public readonly Dictionary<int, long> CcErrors = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _lastCc = new Dictionary<int, int>();
        private readonly HashSet<int> _pmtPids = new HashSet<int>();
        public readonly Dictionary<int, int> StreamTypes = new Dictionary<int, int>();       // PID -> stream_type
        private bool _synced;
        private byte[] _carry = new byte[0];

        public void Feed(byte[] buf, int off, int count)
        {
            Bytes += count;
            var all = new byte[_carry.Length + count];
            Buffer.BlockCopy(_carry, 0, all, 0, _carry.Length);
            Buffer.BlockCopy(buf, off, all, _carry.Length, count);
            int pos = 0;
            if (!_synced)
            {
                int p = -1;
                for (int i = 0; i + 2 * 188 < all.Length; i++) if (all[i] == 0x47 && all[i + 188] == 0x47 && all[i + 376] == 0x47) { p = i; break; }
                if (p < 0) { _carry = all.Length > 376 ? all.Skip(all.Length - 376).ToArray() : all; return; }
                pos = p; _synced = true;
            }
            while (pos + 188 <= all.Length)
            {
                if (all[pos] != 0x47)
                {
                    SyncLosses++;
                    int next = -1;
                    for (int i = pos + 1; i + 188 < all.Length && i < pos + 188 * 4; i++) if (all[i] == 0x47 && all[i + 188] == 0x47) { next = i; break; }
                    if (next < 0) { pos = all.Length; break; }
                    pos = next;
                    continue;
                }
                Packet(all, pos);
                pos += 188;
            }
            _carry = pos < all.Length ? all.Skip(pos).ToArray() : new byte[0];
        }

        private void Packet(byte[] d, int o)
        {
            Packets++;
            if ((d[o + 1] & 0x80) != 0) TransportErrors++;
            bool pusi = (d[o + 1] & 0x40) != 0;
            int pid = ((d[o + 1] & 0x1f) << 8) | d[o + 2];
            int afc = (d[o + 3] >> 4) & 3;
            int cc = d[o + 3] & 0xf;
            long n; PidPackets.TryGetValue(pid, out n); PidPackets[pid] = n + 1;
            if (pid != 0x1fff && (afc & 1) != 0)
            {
                int last;
                if (_lastCc.TryGetValue(pid, out last) && cc != ((last + 1) & 0xf) && cc != last)
                {
                    long e; CcErrors.TryGetValue(pid, out e); CcErrors[pid] = e + 1;
                }
                _lastCc[pid] = cc;
            }
            int payload = o + 4;
            if ((afc & 2) != 0) payload += 1 + d[o + 4];
            if (payload >= o + 188 || (afc & 1) == 0) return;
            if (pid == 0 && pusi) ParsePat(d, payload, o + 188);
            else if (_pmtPids.Contains(pid) && pusi) ParsePmt(d, payload, o + 188);
        }

        private void ParsePat(byte[] d, int p, int end)
        {
            int ptr = d[p]; p += 1 + ptr;
            if (p + 8 > end || d[p] != 0) return;
            int len = ((d[p + 1] & 0xf) << 8) | d[p + 2];
            int e = Math.Min(end, p + 3 + len - 4);
            for (int i = p + 8; i + 4 <= e; i += 4)
            {
                int prog = (d[i] << 8) | d[i + 1];
                int pid = ((d[i + 2] & 0x1f) << 8) | d[i + 3];
                if (prog != 0) _pmtPids.Add(pid);
            }
        }

        private void ParsePmt(byte[] d, int p, int end)
        {
            int ptr = d[p]; p += 1 + ptr;
            if (p + 12 > end || d[p] != 2) return;
            int len = ((d[p + 1] & 0xf) << 8) | d[p + 2];
            int e = Math.Min(end, p + 3 + len - 4);
            int infoLen = ((d[p + 10] & 0xf) << 8) | d[p + 11];
            int i = p + 12 + infoLen;
            while (i + 5 <= e)
            {
                int st = d[i];
                int pid = ((d[i + 1] & 0x1f) << 8) | d[i + 2];
                int es = ((d[i + 3] & 0xf) << 8) | d[i + 4];
                StreamTypes[pid] = st;
                i += 5 + es;
            }
        }

        public static string StreamTypeName(int t)
        {
            switch (t)
            {
                case 0x01: return "MPEG-1 video"; case 0x02: return "MPEG-2 video"; case 0x03: return "MPEG-1 audio (MP2)"; case 0x04: return "MPEG-2 audio";
                case 0x0f: return "AAC (ADTS)"; case 0x11: return "AAC (LATM)"; case 0x1b: return "H.264"; case 0x24: return "H.265"; case 0x06: return "private PES (AC-3/subtitles)";
                case 0x81: return "AC-3"; case 0x87: return "E-AC-3"; case 0x15: return "metadata";
                default: return "0x" + t.ToString("x2");
            }
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(Bytes).Append(" B, ").Append(Packets).Append(" TS packets, sync losses ").Append(SyncLosses).Append(", transport_error ").Append(TransportErrors);
            long cc = CcErrors.Values.Sum();
            sb.Append(", continuity errors ").Append(cc);
            if (StreamTypes.Count > 0)
                sb.Append("\n    streams: ").Append(string.Join(", ", StreamTypes.OrderBy(k => k.Key).Select(k => "PID " + k.Key + " " + StreamTypeName(k.Value) + " (" + (PidPackets.ContainsKey(k.Key) ? PidPackets[k.Key] : 0) + " pkts)")));
            else sb.Append("\n    (no PAT/PMT seen)");
            return sb.ToString();
        }
    }
}
