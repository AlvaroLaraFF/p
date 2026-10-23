using System;
using System.Globalization;
using AceEngine.P2P;

namespace AceEngine.Cli
{
    /// <summary>Variants of the live client to experiment from the CLI (`ace measure`, `ace play`) without recompiling. The tool only; the engine uses its own defaults.</summary>
    internal static class LiveTuning
    {
        public static void Apply(EngineOptions o, Func<string, string, string> get, Func<string, bool> has)
        {
            var inv = CultureInfo.InvariantCulture;
            var l = o.Live;
            if (has("--legacy-live"))
            {
                // The behavior from BEFORE this work (for the "before" runs with the same binary): the engine's connect/handshake deadlines (8 s and 15 s), 16 pending connections,
                // no network penalty, no backoff for full nodes, no peer hints, no redundant request and with the DHT candidates at the same level as the trackers' ones
                l.ConnectTimeout = TimeSpan.Zero; l.HandshakeTimeout = TimeSpan.Zero; l.MaxPendingConnects = 16; l.NetworkPenaltyAfter = 0; l.FullPeerBackoff = false;
                l.DeprioritizeDht = false; l.BanNonAcePeers = false; l.UsePeerHints = false; l.HedgeSlowPieces = false; l.SinglePeerLivePosSeconds = 6;
                o.Resolver.ConnectTimeout = TimeSpan.Zero; o.Resolver.HandshakeTimeout = TimeSpan.Zero;
            }
            if (has("--nt")) l.NodeType = int.Parse(get("--nt", "1"), inv);
            if (has("--peer-type")) { l.PeerType = int.Parse(get("--peer-type", "0"), inv); l.LearnPeerType = false; }
            if (has("--platform")) l.Platform = int.Parse(get("--platform", "3"), inv);
            if (has("--pv")) l.ProtocolVersion = int.Parse(get("--pv", "2"), inv);
            if (has("--engine-version")) l.EngineVersion = int.Parse(get("--engine-version", "3022200"), inv);
            if (has("--tt")) l.TransportType = get("--tt", "bt");
            if (has("--ace-protocol-name")) l.UseAceProtocolName = true;
            if (has("--live-max-peers")) l.MaxPeers = int.Parse(get("--live-max-peers", "40"), inv);
            if (has("--live-connect-timeout")) l.ConnectTimeout = TimeSpan.FromSeconds(double.Parse(get("--live-connect-timeout", "0"), inv));
            if (has("--live-handshake-timeout")) l.HandshakeTimeout = TimeSpan.FromSeconds(double.Parse(get("--live-handshake-timeout", "0"), inv));
            if (has("--net-penalty")) l.NetworkPenaltyAfter = int.Parse(get("--net-penalty", "0"), inv);
            if (has("--full-backoff")) l.FullPeerBackoff = true;
            if (has("--ban-non-ace")) l.BanNonAcePeers = true;
            if (has("--peer-hints")) l.UsePeerHints = true;
            if (has("--claim-accessible")) l.ClaimAccessible = true;
            if (has("--hedge")) l.HedgeSlowPieces = true;
            if (has("--dht-last")) l.DeprioritizeDht = true;
            if (has("--advertise-cap")) l.AdvertiseUploadCap = true;
            if (has("--upload-max-kb")) o.UploadMaxBytesPerSec = long.Parse(get("--upload-max-kb", "0"), inv) * 1024;
            if (has("--single-peer-pos")) l.SinglePeerLivePosSeconds = double.Parse(get("--single-peer-pos", "2.5"), inv);
            if (has("--dump-peers")) l.DumpPeersDir = get("--dump-peers", null);
            if (has("--live-pending")) l.MaxPendingConnects = int.Parse(get("--live-pending", "16"), inv);
        }
    }
}
