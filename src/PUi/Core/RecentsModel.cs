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
    public sealed class RecentCard
    {
        public Channel Channel;
        public Focusable Item;
        public ChannelView View;
        public ChannelStatus Status = new ChannelStatus();
        public readonly Anim X = new Anim(0, Bezier.Pop), Appear = new Anim(0, Bezier.Pop), Fade = new Anim(1, Bezier.Pop);
        public float TargetX;
        public bool Shown;
    }

    /// <summary>
    /// Recents of the TV section: of the saved history (up to 20) at most 10 are shown, and only those that have peers right now. While they are being checked they
    /// appear in a waiting state; those that turn out to have no signal fade out and the rest slide over to close the gap (no odd gaps). Saved entries
    /// beyond the first 10 only enter to fill a gap once they are known to be live.
    /// </summary>
    public sealed class RecentsModel
    {
        public const int MaxVisible = 10;
        public float Left = Tokens.MainLeft, Pitch = 330.4f, Top = 96, W = 302.4f, H = 132;
        /// <summary>Scale of a focused card (the TV's 1.06; a phone or tablet uses less).</summary>
        public float FocusScale = 1.06f;
        private readonly Dictionary<string, RecentCard> _byId = new Dictionary<string, RecentCard>();
        private List<Channel> _stored = new List<Channel>();
        private readonly List<RecentCard> _visible = new List<RecentCard>();
        private readonly List<RecentCard> _fading = new List<RecentCard>();

        public IReadOnlyList<RecentCard> Visible { get { return _visible; } }
        public IReadOnlyList<RecentCard> Fading { get { return _fading; } }
        public IReadOnlyList<Channel> Stored { get { return _stored; } }

        public void SetStored(IReadOnlyList<Channel> stored)
        {
            _stored = stored.GroupBy(c => c.Id).Select(g => g.First()).ToList();
            var keep = new HashSet<string>(_stored.Select(c => c.Id));
            foreach (var id in _byId.Keys.Where(k => !keep.Contains(k)).ToList()) _byId.Remove(id);
        }

        /// <summary>Channels to check (everything saved: the first ones earlier).</summary>
        public List<Channel> ToCheck() { return _stored.ToList(); }

        public void Refresh(ILiveChecker checker, bool waiting, double now)
        {
            var chosen = new List<RecentCard>();
            for (int i = 0; i < _stored.Count && chosen.Count < MaxVisible; i++)
            {
                var ch = _stored[i];
                RecentCard card;
                if (!_byId.TryGetValue(ch.Id, out card)) { card = new RecentCard { Channel = ch }; _byId[ch.Id] = card; }
                var st = checker.GetStatus(ch);
                if (waiting && st.State == LinkState.Pending) st = new ChannelStatus { State = LinkState.Checking };
                card.Status = st;
                card.View = Views.Of(ch, st);
                bool dead = st.IsNoSignal;
                bool primary = i < MaxVisible;
                if (dead) continue;
                if (!primary && !st.IsLive) continue;
                chosen.Add(card);
            }
            var chosenSet = new HashSet<RecentCard>(chosen);
            foreach (var c in _visible) if (!chosenSet.Contains(c) && !_fading.Contains(c)) { c.Fade.Set(0, now, Tokens.ReorderSecs); _fading.Add(c); }
            _fading.RemoveAll(c => chosenSet.Contains(c) || (!c.Fade.Active(now) && c.Fade.Value(now) <= 0.001f));
            _visible.Clear(); _visible.AddRange(chosen);
            for (int i = 0; i < chosen.Count; i++)
            {
                var c = chosen[i];
                float x = Left + i * Pitch;
                if (c.Item == null) c.Item = new Focusable { Id = "r" + c.Channel.Id, Group = "recents", FocusScale = FocusScale };
                c.Item.FocusScale = FocusScale;
                c.Item.R = new SKRect(x, Top, x + W, Top + H); c.Item.Tag = i;
                if (!c.Shown) { c.Shown = true; c.X.Snap(x); c.TargetX = x; c.Appear.Snap(0); c.Appear.Set(1, now, Tokens.RiseSecs); c.Fade.Snap(1); }
                else
                {
                    if (Math.Abs(x - c.TargetX) > 0.5f) c.X.Set(x, now, Tokens.ReorderSecs);
                    c.TargetX = x;
                    if (c.Fade.Target < 1f) c.Fade.Set(1, now, Tokens.ReorderSecs);
                }
            }
        }

        public bool Animating(double now)
        {
            foreach (var c in _visible) if (c.X.Active(now) || c.Appear.Active(now) || c.Fade.Active(now) || (c.Item != null && c.Item.Animating(now))) return true;
            foreach (var c in _fading) if (c.Fade.Active(now)) return true;
            return false;
        }

        /// <summary>true if any saved recent among the candidates to be shown is still being checked.</summary>
        public bool AnyChecking { get { for (int i = 0; i < _visible.Count; i++) if (_visible[i].Status.IsChecking) return true; return false; } }
    }
}
