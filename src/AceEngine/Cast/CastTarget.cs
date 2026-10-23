using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace AceEngine.Cast
{
    /// <summary>The kind of screen a channel can be sent to.</summary>
    public enum CastTargetKind
    {
        /// <summary>A Chromecast or a TV with Google Cast built in. Found by the Cast SDK in the Android shell; the engine only serves the HLS.</summary>
        GoogleCast,
        /// <summary>A UPnP/DLNA MediaRenderer (most Smart TVs, Samsung and LG included). Found and controlled by the engine.</summary>
        Dlna,
        /// <summary>Another P! on the network (later).</summary>
        PApp
    }

    /// <summary>What a target can do with a live channel.</summary>
    [Flags]
    public enum CastCapabilities
    {
        None = 0,
        /// <summary>Plays an HLS live playlist (what Google Cast takes).</summary>
        Hls = 1,
        /// <summary>Plays a progressive MPEG-TS over HTTP (what DLNA renderers take).</summary>
        ProgressiveTs = 2,
        /// <summary>The Pause action exists (AVTransport Pause is optional).</summary>
        Pause = 4,
        Volume = 8,
        /// <summary>Tuner TVs decode MP2, AC-3, MPEG-2 and HEVC in TS by themselves: any channel can be sent as it is.</summary>
        AnyCodec = 16
    }

    /// <summary>A screen the user can pick in the "send to" list.</summary>
    public sealed class CastTarget
    {
        public CastTargetKind Kind;
        /// <summary>Stable and unique: "dlna:" + UDN, "gcast:" + the Cast device id.</summary>
        public string Id = "";
        /// <summary>Name to show ("[TV] Samsung Q6 Series (65)", "Salón").</summary>
        public string Name = "";
        public string Manufacturer = "", Model = "";
        /// <summary>IPv4 address of the screen: the only one the server will let in.</summary>
        public IPAddress Address;
        public CastCapabilities Capabilities;
        /// <summary>What it can decode, for the compatibility check of a channel. Null = the default of its kind.</summary>
        public CastProfile Profile;
        /// <summary>Who found it ("dlna", "googlecast"): a provider replaces only its own targets.</summary>
        public string Provider = "";
        public DateTime LastSeenUtc;
        /// <summary>DLNA: the renderer with its control URLs.</summary>
        public Dlna.DlnaRenderer Renderer;
        /// <summary>Other ways to reach the same screen (a Google TV that is also a DLNA renderer).</summary>
        public readonly List<CastTarget> Alternatives = new List<CastTarget>();

        public bool Supports(CastCapabilities c) { return (Capabilities & c) == c; }

        /// <summary>Preference when one screen is reachable in several ways: Cast first, then P!, then DLNA.</summary>
        internal int Rank { get { return Kind == CastTargetKind.GoogleCast ? 0 : Kind == CastTargetKind.PApp ? 1 : 2; } }

        public override string ToString() { return Kind + " '" + Name + "' " + Address + (Model.Length > 0 ? " (" + Manufacturer + " " + Model + ")" : ""); }
    }

    /// <summary>
    /// The one list the interface shows. Each provider (the DLNA discovery of the engine, the Cast SDK of the Android shell) replaces ITS targets with <see cref="Update"/>; the
    /// list merges them. Safe from any thread.
    /// </summary>
    public sealed class CastTargetList
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<CastTarget>> _byProvider = new Dictionary<string, List<CastTarget>>(StringComparer.Ordinal);
        private List<CastTarget> _merged = new List<CastTarget>();

        /// <summary>The list changed (thread-pool thread).</summary>
        public event Action Changed;

        /// <summary>Replaces everything this provider had with <paramref name="targets"/> (an empty list removes its targets).</summary>
        public void Update(string provider, IEnumerable<CastTarget> targets)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            var list = new List<CastTarget>();
            if (targets != null)
                foreach (var t in targets)
                {
                    if (t == null || t.Address == null || string.IsNullOrEmpty(t.Id)) continue;
                    t.Provider = provider;
                    if (t.LastSeenUtc == default(DateTime)) t.LastSeenUtc = DateTime.UtcNow;
                    if (list.Any(x => x.Id == t.Id)) continue;
                    list.Add(t);
                }
            bool changed;
            lock (_gate)
            {
                if (list.Count == 0) _byProvider.Remove(provider); else _byProvider[provider] = list;
                var before = Signature(_merged);
                _merged = Merge(_byProvider.Values.SelectMany(x => x));
                changed = before != Signature(_merged);
            }
            var h = Changed;
            if (changed && h != null) h();
        }

        /// <summary>The targets, one per screen, in a stable order (by name).</summary>
        public IReadOnlyList<CastTarget> Items { get { lock (_gate) return _merged.ToList(); } }

        public CastTarget Find(string id)
        {
            lock (_gate)
            {
                foreach (var t in _merged)
                {
                    if (t.Id == id) return t;
                    foreach (var a in t.Alternatives) if (a.Id == id) return a;
                }
                return null;
            }
        }

        private static string Signature(List<CastTarget> l)
        {
            return string.Join("|", l.Select(t => t.Id + "/" + t.Name + "/" + t.Address + "/" + (int)t.Capabilities + "/" + string.Join(",", t.Alternatives.Select(a => a.Id))));
        }

        /// <summary>One entry per screen: targets with the same address are the same screen, the preferred kind leads and the others are its alternatives.</summary>
        internal static List<CastTarget> Merge(IEnumerable<CastTarget> all)
        {
            var groups = all.GroupBy(t => t.Address.ToString());
            var res = new List<CastTarget>();
            foreach (var g in groups)
            {
                var ordered = g.OrderBy(t => t.Rank).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var lead = ordered[0];
                var copy = Copy(lead);
                foreach (var other in ordered.Skip(1)) copy.Alternatives.Add(other);
                res.Add(copy);
            }
            return res.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        }

        private static CastTarget Copy(CastTarget t)
        {
            return new CastTarget
            {
                Kind = t.Kind, Id = t.Id, Name = t.Name, Manufacturer = t.Manufacturer, Model = t.Model, Address = t.Address, Capabilities = t.Capabilities, Profile = t.Profile,
                Provider = t.Provider, LastSeenUtc = t.LastSeenUtc, Renderer = t.Renderer
            };
        }
    }
}
