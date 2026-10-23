using System;
using System.Collections.Generic;
using System.IO;
using AceEngine.Live;
using AceEngine.Sim;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>Utilities for the in-memory live stream tests: TS stream with a timeline (SimTsProfile) and reading from a reader.</summary>
    internal static class TsFeed
    {
        public const int Pkt = SimTs.PacketSize;

        public static SimTsProfile Profile(int bitrate = 262144, int gopMs = 1000, bool pcr = true, bool rai = true)
        {
            return new SimTsProfile { Bitrate = bitrate, GopMs = gopMs, EmitPcr = pcr, MarkRandomAccess = rai };
        }

        /// <summary>Packets [from, to) concatenated.</summary>
        public static byte[] Packets(SimTsProfile p, long from, long to)
        {
            var r = new byte[(to - from) * Pkt];
            for (long n = from; n < to; n++) Buffer.BlockCopy(p.Packet(n), 0, r, (int)((n - from) * Pkt), Pkt);
            return r;
        }

        /// <summary>Adds the packets [from, to) in chunks of varied sizes (to exercise the reassembly of cut packets).</summary>
        public static void Feed(TimeshiftBuffer b, SimTsProfile p, long from, long to, int seed = 1, int maxChunk = 20000)
        {
            var data = Packets(p, from, to);
            var rnd = new Random(seed);
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(data.Length - pos, 1 + rnd.Next(maxChunk));
                b.Append(data, pos, n);
                pos += n;
            }
        }

        public static TimeshiftBuffer NewBuffer(long budgetBytes, int blockBytes = 188 * 100, int bitrate = 262144, double liveDelayMs = 10000)
        {
            return new TimeshiftBuffer(new TimeshiftOptions { BudgetBytes = budgetBytes, BlockBytes = blockBytes, NominalBytesPerSecond = bitrate, LiveDelayMs = liveDelayMs });
        }

        /// <summary>Reads everything currently available from the reader.</summary>
        public static byte[] Drain(TimeshiftReader r, int max = int.MaxValue)
        {
            var ms = new MemoryStream();
            var buf = new byte[7000];
            while (ms.Length < max)
            {
                int n = r.TryRead(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        /// <summary>Numbers of the video (non-PSI) packets of a chunk of stream with a media profile.</summary>
        public static List<long> VideoNumbers(byte[] data)
        {
            var l = new List<long>();
            for (int i = 0; i + Pkt <= data.Length; i += Pkt)
            {
                int pid = ((data[i + 1] & 0x1F) << 8) | data[i + 2];
                if (pid == SimTs.VideoPid) l.Add(SimTsProfile.NumberOf(data, i));
            }
            return l;
        }
    }
}
