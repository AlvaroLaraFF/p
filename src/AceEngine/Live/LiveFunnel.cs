using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AceEngine.Live
{
    /// <summary>Stages a connection with a peer reaches, from least to most (the funnel that `ace measure` measures).</summary>
    public enum LiveStage
    {
        Candidate = 0,
        Connecting = 1,
        /// <summary>The TCP was established.</summary>
        TcpOk = 2,
        /// <summary>A valid BT handshake arrived (same infohash).</summary>
        HandshakeOk = 3,
        /// <summary>Its EXTEND (20/0) arrived.</summary>
        ExtendOk = 4,
        /// <summary>It sent us some HAVE or BITFIELD with pieces.</summary>
        HaveSeen = 5,
        /// <summary>It UNCHOKEd us.</summary>
        Unchoked = 6,
        /// <summary>It served us at least one complete piece.</summary>
        Served = 7
    }

    /// <summary>
    /// What happened with ONE peer of a live stream (diagnostics and "alive" criterion). Timestamps are relative to the start of the swarm (ms);
    /// -1 = did not happen. Written by the connection thread and by the swarm's; reading is for diagnostics and tolerates races.
    /// </summary>
    public sealed class LivePeerTrace
    {
        public string EndPoint;
        public string Source;
        public bool Incoming;
        public int Attempt = 1;
        public LiveStage Stage = LiveStage.Candidate;
        public long StartAbs, TcpAbs = -1, HandshakeAbs = -1, ExtendAbs = -1, HaveAbs = -1, UnchokeAbs = -1, PieceAbs = -1, ClosedAbs = -1;
        public string CloseReason;
        public string RemoteProtocol;
        public long V = -1;
        public int Nt = -1, Pv = -1, Platform = -1, MiPeerType = -1, Accessible = -1;
        public string Tt;
        public bool HadSignature;
        /// <summary>Highest piece index it said it has (HAVE/BITFIELD) and highest live position it claimed (EXTEND, MYINFO...).</summary>
        public long MaxHave = -1, HeadClaim = -1;
        public int HaveMessages;
        public long Bytes;
        public int Pieces;

        public void Reach(LiveStage s) { if (s > Stage) Stage = s; }

        /// <summary>Classification of how the connection ended (or "open" while it is still open).</summary>
        public string CloseClass
        {
            get
            {
                if (ClosedAbs < 0) return "open";
                var r = CloseReason ?? "";
                if (r == "connect timeout") return "tcp_timeout";
                if (r.StartsWith("socket: ", StringComparison.Ordinal))
                {
                    if (r.IndexOf("ConnectionRefused", StringComparison.Ordinal) >= 0) return "tcp_refused";
                    if (r.IndexOf("Unreachable", StringComparison.Ordinal) >= 0 || r.IndexOf("NetworkDown", StringComparison.Ordinal) >= 0) return "tcp_unreachable";
                    if (Stage < LiveStage.TcpOk) return "tcp_error";
                }
                if (Stage < LiveStage.TcpOk) return r.StartsWith("I/O", StringComparison.Ordinal) ? "tcp_error" : "local";
                if (Stage < LiveStage.HandshakeOk)
                {
                    if (r == "handshake timeout") return "hs_timeout";
                    if (r.StartsWith("bad header", StringComparison.Ordinal) || r.StartsWith("protocol", StringComparison.Ordinal) || r.StartsWith("infohash mismatch", StringComparison.Ordinal)) return "hs_rechazado";
                    if (r.StartsWith("I/O", StringComparison.Ordinal) || r.StartsWith("socket", StringComparison.Ordinal)) return "hs_closed";
                    return "local";
                }
                if (IsLocalReason(r)) return "local";
                if (Stage < LiveStage.ExtendOk) return PeerClosed(r) ? "tras_handshake" : (r.IndexOf("extend", StringComparison.OrdinalIgnoreCase) >= 0 ? "extend_invalido" : "tras_handshake");
                if (r.StartsWith("transport type mismatch", StringComparison.Ordinal)) return "tt_distinto";
                if (PeerClosed(r)) return Stage < LiveStage.HaveSeen ? "after_extend" : "closed_by_peer";
                return "violation";
            }
        }

        /// <summary>The peer closed the connection or we lost it along the way (reading or writing on a socket the other side already closed).</summary>
        private static bool PeerClosed(string r)
        {
            return r.StartsWith("I/O", StringComparison.Ordinal) || r.StartsWith("socket", StringComparison.Ordinal) || r.StartsWith("write", StringComparison.Ordinal) || r.StartsWith("the peer closed", StringComparison.Ordinal);
        }

        private static bool IsLocalReason(string r)
        {
            return r == "session stopped" || r == "cancelled" || r == "closed" || r == "dispose" || r == "no pieces" || r == "no activity" || r.StartsWith("evicted", StringComparison.Ordinal)
                || r == "rejected by the swarm" || r.StartsWith("duplicate", StringComparison.Ordinal) || r == "connection to self" || r == "trimmed";
        }
    }

    /// <summary>Summary of the connection funnel of a live stream (DISTINCT peers at each stage) and of the live position.</summary>
    public sealed class LiveFunnel
    {
        /// <summary>Seconds since the start of the swarm.</summary>
        public double ElapsedSeconds;
        /// <summary>Distinct candidates per source (tracker, dht, resolution, startup_node, meta_tracker, StaticPeers) and in total.</summary>
        public Dictionary<string, int> CandidatesBySource = new Dictionary<string, int>();
        public int Candidates;
        /// <summary>Distinct peers that reached at least each stage.</summary>
        public int Tried, TcpOk, HandshakeOk, ExtendOk, HaveSeen, Unchoked, Served;
        /// <summary>Peers with "recent" pieces (close to the live position): see RecentWindow.</summary>
        public int RecentHave;
        /// <summary>Incoming connections accepted (and those that reached the handshake).</summary>
        public int Incoming, IncomingHandshakeOk;
        /// <summary>Per source: [tried, tcp, handshake, extend, recent pieces].</summary>
        public Dictionary<string, int[]> BySource = new Dictionary<string, int[]>();
        /// <summary>How the last connection of each peer ended (class -> peers).</summary>
        public Dictionary<string, int> CloseClasses = new Dictionary<string, int>();
        /// <summary>Remote nodes by type (nt) among those that completed the EXTEND.</summary>
        public Dictionary<int, int> NodeTypes = new Dictionary<int, int>();
        /// <summary>Peers with mi.is_accessible = 1 among those that completed the EXTEND.</summary>
        public int Accessible;
        public long LivePosFirst = -1, LivePosLast = -1;
        public int LivePosAdvances;
        public double LivePosFirstSeconds = -1, LivePosLastChangeSeconds = -1;
        public double FirstTcpSeconds = -1, FirstHandshakeSeconds = -1, FirstExtendSeconds = -1, FirstHaveSeconds = -1, FirstUnchokeSeconds = -1, FirstPieceSeconds = -1, PlayableSeconds = -1;
        /// <summary>Window (pieces) considered recent.</summary>
        public long RecentWindow;
        public List<LivePeerTrace> Peers = new List<LivePeerTrace>();

        public string Describe()
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.Append("cand=").Append(Candidates).Append(" tried=").Append(Tried).Append(" tcp=").Append(TcpOk).Append(" hs=").Append(HandshakeOk).Append(" ext=").Append(ExtendOk)
                .Append(" have=").Append(HaveSeen).Append(" recent=").Append(RecentHave).Append(" unchoke=").Append(Unchoked).Append(" serving=").Append(Served);
            sb.Append(" live ").Append(LivePosFirst).Append("->").Append(LivePosLast).Append(" (").Append(LivePosAdvances.ToString(ci)).Append(" advances)");
            sb.Append(" closes{").Append(string.Join(",", CloseClasses.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value))).Append("}");
            return sb.ToString();
        }
    }
}
