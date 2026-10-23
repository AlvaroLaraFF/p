using System;
using System.Collections.Generic;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Core;

namespace PUi.Screens
{
    /// <summary>Playback context: the list the channel was opened from (for Channel +/-) and the position.</summary>
    public sealed class PlayContext
    {
        public readonly IReadOnlyList<Channel> List;
        public int Index;
        public PlayContext(IReadOnlyList<Channel> list, int index) { List = list; Index = Math.Max(0, Math.Min(index, Math.Max(0, list.Count - 1))); }
        public Channel Current { get { return List.Count == 0 ? null : List[Index]; } }
        public Channel Peek(int delta) { return List.Count == 0 ? null : List[((Index + delta) % List.Count + List.Count) % List.Count]; }
        public bool Move(int delta) { if (List.Count < 2) return false; Index = ((Index + delta) % List.Count + List.Count) % List.Count; return true; }
        /// <summary>Jumps to a position of the list (a tap on one of the "more channels" of the phone player). false if it is the current one or out of range.</summary>
        public bool MoveTo(int index) { if (index < 0 || index >= List.Count || index == Index) return false; Index = index; return true; }
    }

    public static class Views
    {
        public static Signal SignalOf(ChannelStatus st)
        {
            switch (st.State)
            {
                case LinkState.Good: return Signal.Live;
                case LinkState.Dead: case LinkState.Unknown: return Signal.NoSignal;
                case LinkState.Checking: return Signal.Checking;
                default: return Signal.Pending;
            }
        }

        public static ChannelView Of(Channel ch, ChannelStatus st)
        {
            var sig = SignalOf(st);
            // live: the number of observed peers; no signal (only seen without "solo vivos"): 0; checking: nothing
            return ChannelView.Make(ch.Name, ch.Quality, sig == Signal.Live ? st.PeersText : sig == Signal.NoSignal ? "0" : "", sig);
        }
    }
}
