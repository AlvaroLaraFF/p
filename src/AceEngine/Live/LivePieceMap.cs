using System;
using System.Collections.Generic;

namespace AceEngine.Live
{
    /// <summary>Sparse set of piece indexes (uint64) that a peer has, with maximum and minimum (live streams have no npieces).</summary>
    internal sealed class LivePieceMap
    {
        private readonly HashSet<long> _set = new HashSet<long>();
        public long Max { get; private set; }
        public long Min { get; private set; }

        public int Count { get { return _set.Count; } }
        public bool Has(long i) { return _set.Contains(i); }

        public void Set(long i)
        {
            if (_set.Add(i))
            {
                if (_set.Count == 1) { Max = i; Min = i; }
                else { if (i > Max) Max = i; if (i < Min) Min = i; }
            }
        }

        public void Clear(long i)
        {
            if (_set.Remove(i) && _set.Count > 0 && (i == Max || i == Min)) Recompute();
        }

        /// <summary>Removes what precedes 'below'.</summary>
        public void PruneBelow(long below)
        {
            if (_set.Count == 0 || Min >= below) return;
            _set.RemoveWhere(x => x < below);
            if (_set.Count > 0) Recompute();
        }

        public void Reset() { _set.Clear(); Max = 0; Min = 0; }

        private void Recompute()
        {
            bool first = true;
            foreach (var x in _set)
            {
                if (first) { Max = x; Min = x; first = false; }
                else { if (x > Max) Max = x; if (x < Min) Min = x; }
            }
        }
    }
}
