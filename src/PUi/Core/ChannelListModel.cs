using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.App.Tv;
using AceEngine.Probe;
using PUi.Motion;
using PUi.Screens;
using PUi.Theme;
using SkiaSharp;

namespace PUi.Core
{
    public enum SortMode { Arrival = 0, AlphaAsc = 1, AlphaDesc = 2, PeersDesc = 3 }

    /// <summary>A row of the channel list: its state, its drawing and its animations (position, entrance).</summary>
    public sealed class ListRow
    {
        public Channel Channel;
        public ChannelStatus Status = new ChannelStatus();
        public ChannelView View;
        public Focusable Item;
        /// <summary>Animated vertical position (0.3 s when reordering) and entrance ("rise": moves up 24 px and appears in 0.32 s).</summary>
        public readonly Anim Y = new Anim(0, Bezier.Pop);
        public readonly Anim Appear = new Anim(0, Bezier.Pop);
        public readonly Anim Fade = new Anim(1, Bezier.Pop);
        public float TargetY;
        public bool Shown;
        /// <summary>It is in the list (with "solo vivos", when it is found to be live; without it, from the start).</summary>
        public bool Listed;
        /// <summary>Arrival number: position in discovery order (does not change while it stays in the list).</summary>
        public int Seq = -1;
        public int Order = -1;
    }

    /// <summary>
    /// Channel list of a category, an Explore kind or some results. STARTS EMPTY and each channel enters when it is found to be live (real stream has arrived),
    /// AT THE END: the ones already there do not move. With "solo vivos" (default) nobody else enters; without "solo vivos" the unverified ones appear later, once the check finishes,
    /// dimmed and with 0, also at the end. The default order is arrival; the user can switch to alphabetical (A-Z, Z-A) or
    /// by peers, and then the rows are reordered with a 0.3 s animation. Rows keep their identity (the focus follows the channel, not the position).
    /// </summary>
    public sealed class ChannelListModel
    {
        public float Left = Tokens.MainLeft, Width = Tokens.MainWidth, Top = 179, RowH = 92, Gap = 14;
        private readonly Dictionary<string, ListRow> _byId = new Dictionary<string, ListRow>();
        private readonly List<ListRow> _rows = new List<ListRow>();
        private readonly List<ListRow> _visible = new List<ListRow>();
        private readonly List<ListRow> _fading = new List<ListRow>();
        private int _nextSeq;

        public IReadOnlyList<ListRow> Rows { get { return _rows; } }
        public IReadOnlyList<ListRow> Visible { get { return _visible; } }
        /// <summary>Rows that are fading out (just hidden): they keep being drawn until they finish.</summary>
        public IReadOnlyList<ListRow> Fading { get { return _fading; } }
        public int Hidden { get; private set; }
        public int Done { get; private set; }
        public int Total { get { return _rows.Count; } }
        public float Pitch { get { return RowH + Gap; } }
        public float ContentBottom { get { return Top + Math.Max(0, _visible.Count) * Pitch; } }

        public void SetChannels(IReadOnlyList<Channel> channels)
        {
            var keep = new HashSet<string>();
            var next = new List<ListRow>();
            foreach (var ch in channels)
            {
                if (!keep.Add(ch.Id)) continue;
                ListRow r;
                if (!_byId.TryGetValue(ch.Id, out r)) { r = new ListRow { Channel = ch }; _byId[ch.Id] = r; }
                next.Add(r);
            }
            foreach (var id in _byId.Keys.Where(k => !keep.Contains(k)).ToList()) _byId.Remove(id);
            _rows.Clear(); _rows.AddRange(next);
        }

        /// <summary>Visible channels in the current order (to open one with Channel +/-).</summary>
        public List<Channel> VisibleChannels() { return _visible.Select(r => r.Channel).ToList(); }

        /// <summary>Alphabetical sort key: lowercase and without Spanish accents.</summary>
        public static string SortKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                char l = char.ToLowerInvariant(c);
                switch (l)
                {
                    case 'á': case 'à': case 'ä': case 'â': l = 'a'; break;
                    case 'é': case 'è': case 'ë': case 'ê': l = 'e'; break;
                    case 'í': case 'ì': case 'ï': case 'î': l = 'i'; break;
                    case 'ó': case 'ò': case 'ö': case 'ô': l = 'o'; break;
                    case 'ú': case 'ù': case 'ü': case 'û': l = 'u'; break;
                }
                sb.Append(l);
            }
            return sb.ToString();
        }

        private static int CompareNames(ListRow a, ListRow b)
        {
            int c = string.CompareOrdinal(SortKey(a.Channel.Name), SortKey(b.Channel.Name));
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        public void Refresh(ILiveChecker checker, bool onlyLive, double now) { Refresh(checker, onlyLive, SortMode.Arrival, now); }

        public void Refresh(ILiveChecker checker, bool onlyLive, SortMode sort, double now)
        {
            foreach (var r in _rows)
            {
                r.Status = checker.GetStatus(r.Channel);
                r.View = Views.Of(r.Channel, r.Status);
            }
            Done = _rows.Count(r => !r.Status.IsChecking);
            Hidden = onlyLive ? _rows.Count(r => r.Status.IsNoSignal) : 0;

            // who is in the list: live ones enter as soon as they are found (at the end, without moving the ones already there). With "solo vivos" nobody else enters; without it, those that have not
            // been verified appear LATER, when the check of the whole list finishes, at the end and with 0 peers (and those that already entered are not removed)
            bool allChecked = _rows.Count > 0 && !_rows.Any(r => r.Status.IsChecking);
            foreach (var r in _rows)
            {
                bool want = onlyLive ? r.Status.IsLive : (r.Listed || r.Status.IsLive || allChecked);
                if (want && !r.Listed) { r.Listed = true; r.Seq = _nextSeq++; r.Shown = false; }
                else if (!want && r.Listed) r.Listed = false;
            }
            var listed = _rows.Where(r => r.Listed);
            List<ListRow> order;
            switch (sort)
            {
                case SortMode.AlphaAsc: order = listed.ToList(); order.Sort(CompareNames); break;
                case SortMode.AlphaDesc: order = listed.ToList(); order.Sort((a, b) => CompareNames(b, a)); break;
                case SortMode.PeersDesc:
                    order = listed.Where(r => r.Status.IsLive).OrderByDescending(r => r.Status.OrderKey).ThenByDescending(r => r.Status.Seeds).ThenBy(r => r.Seq).ToList();
                    order.AddRange(listed.Where(r => !r.Status.IsLive).OrderBy(r => r.Seq));
                    break;
                default: order = listed.OrderBy(r => r.Seq).ToList(); break;
            }

            var shownNow = new HashSet<ListRow>(order);
            // the ones that stop being shown fade out
            foreach (var r in _visible)
                if (!shownNow.Contains(r) && !_fading.Contains(r)) { r.Fade.Set(0, now, Tokens.ReorderSecs); _fading.Add(r); }
            _fading.RemoveAll(r => shownNow.Contains(r) || r.Fade.Value(now) <= 0.001f && !r.Fade.Active(now));
            _visible.Clear(); _visible.AddRange(order);
            for (int i = 0; i < order.Count; i++)
            {
                var r = order[i];
                float y = Top + i * Pitch;
                r.Order = i;
                if (r.Item == null) r.Item = new Focusable { Id = r.Channel.Id, Group = "rows", FocusScale = 1.015f };
                r.Item.R = new SKRect(Left, y, Left + Width, y + RowH);
                if (!r.Shown)
                {
                    // entrance: appears in place (moves up 24 px and fades in over 0.32 s) after a couple of frames, so the texture is already created when it is seen
                    r.Shown = true; r.Y.Snap(y); r.TargetY = y;
                    r.Appear.Snap(0); r.Appear.Set(1, now + 0.05, Tokens.RiseSecs);
                    r.Fade.Snap(1);
                }
                else
                {
                    if (Math.Abs(y - r.TargetY) > 0.5f) r.Y.Set(y, now, Tokens.ReorderSecs);
                    r.TargetY = y;
                    if (r.Fade.Target < 1f) r.Fade.Set(1, now, Tokens.ReorderSecs);
                }
            }
        }

        /// <summary>true if any visible row is still being checked (its loading spinner is drawn).</summary>
        public bool AnyChecking { get { for (int i = 0; i < _visible.Count; i++) if (_visible[i].Status.IsChecking) return true; return false; } }

        public bool Animating(double now)
        {
            foreach (var r in _visible) if (r.Y.Active(now) || r.Appear.Active(now) || r.Fade.Active(now) || (r.Item != null && r.Item.Animating(now))) return true;
            foreach (var r in _fading) if (r.Fade.Active(now)) return true;
            return false;
        }
    }
}
