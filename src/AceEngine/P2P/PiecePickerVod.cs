using System;
using System.Collections.Generic;

namespace AceEngine.P2P
{
    /// <summary>View of the state the picker needs. All calls are made with the swarm lock held.</summary>
    internal interface IPickerContext
    {
        /// <summary>The piece is already verified and in memory.</summary>
        bool Have(int piece);
        /// <summary>The peer asking for work has it.</summary>
        bool PeerHas(int piece);
        /// <summary>Some block of the piece can be requested (it is not being verified, free blocks remain and there is memory if it is new).</summary>
        bool Requestable(int piece);
        /// <summary>Number of connected peers that have the piece.</summary>
        int Availability(int piece);
        /// <summary>Some "fast" peer (without bad performance) has the piece.</summary>
        bool FastPeerHas(int piece);
    }

    public enum PickTier { None, Prebuffer, High, Mid, Low }

    /// <summary>Download windows around the playback position (in piece indexes).</summary>
    internal sealed class PickerWindow
    {
        public int FirstPiece;          // first piece of the played file
        public int LastPiece;           // last (inclusive)
        public int PlaybackPiece;
        public int HighEnd;             // exclusive: [PlaybackPiece, HighEnd) = high
        public int MidEnd;              // exclusive: [HighEnd, MidEnd) = mid
        public int WindowEnd;           // exclusive: [MidEnd, WindowEnd) = low
        public bool Prebuffering;
        public List<int> PrebufNeeded = new List<int>();
        public List<int> PrebufHighPriority = new List<int>();
    }

    /// <summary>
    /// Piece selection for VOD streaming, a reimplementation of PiecePickerVOD.next_new:
    /// prebuffer (rarest in a small range), strictly sequential "high" window, "mid" rarest and "low" rarest,
    /// and peers with bad performance only get "low" (unless no fast peer has the high-priority piece).
    /// The window sizes are decided by the caller (not specified by the original).
    /// </summary>
    internal static class PiecePickerVod
    {
        public static int Pick(PickerWindow w, IPickerContext ctx, bool allowPerf, out PickTier tier)
        {
            tier = PickTier.None;
            int choice = -1;

            if (w.Prebuffering)
            {
                choice = PickRarest(w.PrebufHighPriority, ctx);
                if (choice < 0) choice = PickRarest(w.PrebufNeeded, ctx);
                if (choice >= 0) tier = PickTier.Prebuffer;
            }

            if (choice < 0)
            {
                // high: strictly sequential from the playback position
                int end = Math.Min(w.HighEnd, w.LastPiece + 1);
                for (int p = Math.Max(w.PlaybackPiece, w.FirstPiece); p < end; p++)
                {
                    if (Eligible(p, ctx)) { choice = p; tier = PickTier.High; break; }
                }
            }

            if (choice < 0)
            {
                choice = PickRarestRange(w.HighEnd, Math.Min(w.MidEnd, w.LastPiece + 1), ctx);
                if (choice >= 0) tier = PickTier.Mid;
            }

            int hp = -1;
            PickTier hpTier = PickTier.None;
            if (!allowPerf)
            {
                // a slow peer does not get high-priority pieces
                hp = choice; hpTier = tier;
                choice = -1; tier = PickTier.None;
            }

            if (choice < 0)
            {
                choice = PickRarestRange(w.MidEnd, Math.Min(w.WindowEnd, w.LastPiece + 1), ctx);
                if (choice >= 0) tier = PickTier.Low;
            }

            if (choice < 0 && !allowPerf && hp >= 0 && !ctx.FastPeerHas(hp))
            {
                choice = hp; tier = hpTier;
            }
            return choice;
        }

        private static bool Eligible(int p, IPickerContext ctx)
        {
            return !ctx.Have(p) && ctx.PeerHas(p) && ctx.Requestable(p);
        }

        private static int PickRarest(List<int> pieces, IPickerContext ctx)
        {
            int best = -1, bestAvail = int.MaxValue, ties = 0;
            foreach (var p in pieces)
            {
                if (!Eligible(p, ctx)) continue;
                int a = ctx.Availability(p);
                if (a < bestAvail) { best = p; bestAvail = a; ties = 1; }
                else if (a == bestAvail && Rng.Next(++ties) == 0) best = p;
            }
            return best;
        }

        private static int PickRarestRange(int from, int to, IPickerContext ctx)
        {
            int best = -1, bestAvail = int.MaxValue, ties = 0;
            for (int p = from; p < to; p++)
            {
                if (!Eligible(p, ctx)) continue;
                int a = ctx.Availability(p);
                if (a < bestAvail) { best = p; bestAvail = a; ties = 1; }
                else if (a == bestAvail && Rng.Next(++ties) == 0) best = p;
            }
            return best;
        }
    }
}
