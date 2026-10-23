using System;
using System.Collections.Generic;
using System.Threading;
using AceEngine.P2P;

namespace AceEngine.Resolve
{
    /// <summary>
    /// What each resolution of the probe has at its disposal (the player does not use it and keeps the engine's values). It is input and output at once:
    /// the prober sets the connection budget and the deadlines of THIS resolution and collects in Stats what happened, even if the resolution times out.
    /// </summary>
    public sealed class ResolveProfile
    {
        /// <summary>Simultaneous TCP connections of this resolution (the engine uses ResolverOptions.MaxConnections = 30). 0 = the engine's value.</summary>
        public int MaxConnections;
        /// <summary>Deadline of the connect and of the handshake with each peer (the engine uses EngineOptions.ConnectTimeout / HandshakeTimeout). Zero = the engine's value.</summary>
        public TimeSpan ConnectTimeout, HandshakeTimeout;
        /// <summary>What happened in the resolution (filled in while it runs).</summary>
        public ResolveStats Stats = new ResolveStats();
    }

    /// <summary>What happened with the connection attempts and the DHT queries of a resolution: to know why a link was considered not served.</summary>
    public sealed class ResolveStats
    {
        private readonly object _gate = new object();
        public readonly DhtSearchStats Dht = new DhtSearchStats();
        /// <summary>Candidate peers given by the DHT and the trackers, and how many were left untried at the end.</summary>
        public int Candidates, UntriedAtEnd;
        /// <summary>Connections attempted, by result.</summary>
        public int Attempts, ConnectTimeouts, Refused, HandshakeTimeouts, ClosedByPeer, ProtocolRejected, Cancelled, OtherFailures, HandshakesOk;
        /// <summary>System errors from lack of resources (too many open files, no buffers...). If there are any, the link is NOT considered not served.</summary>
        public int ResourceErrors;
        /// <summary>Seconds of connection slot occupied by attempts that ended in timeout, versus the others.</summary>
        public double SlotSecondsTimeouts, SlotSecondsOther;
        /// <summary>Maximum simultaneous connections of this resolution.</summary>
        public int PeakConnections;
        /// <summary>Seconds until the first candidate (-1 if there never was one) and total duration of the resolution.</summary>
        public double FirstCandidateSeconds = -1, ElapsedSeconds;
        /// <summary>Milliseconds the handshake took for the peers that answered.</summary>
        public readonly List<double> HandshakeOkMs = new List<double>();
        /// <summary>Budget and deadlines it ran with.</summary>
        public int BudgetConnections;
        public double ConnectTimeoutSeconds, HandshakeTimeoutSeconds;

        internal void NoteAttempt(string reason, bool handshakeOk, double handshakeMs, double slotSeconds)
        {
            lock (_gate)
            {
                Attempts++;
                bool timeout = false;
                if (handshakeOk) { HandshakesOk++; HandshakeOkMs.Add(handshakeMs); }
                if (IsResourceError(reason)) ResourceErrors++;
                else if (reason == "connect timeout") { ConnectTimeouts++; timeout = true; }
                else if (reason == "handshake timeout") { HandshakeTimeouts++; timeout = true; }
                else if (reason.StartsWith("socket ")) Refused++;
                else if (reason == "closed by peer" || reason.StartsWith("I/O")) ClosedByPeer++;
                else if (reason.StartsWith("protocol") || reason.StartsWith("bad len") || reason.StartsWith("no extensions") || reason.StartsWith("connection to self")) ProtocolRejected++;
                else if (reason == "cancelled" || reason == "closed") Cancelled++;
                else OtherFailures++;
                if (timeout) SlotSecondsTimeouts += slotSeconds; else SlotSecondsOther += slotSeconds;
            }
        }

        private static bool IsResourceError(string reason)
        {
            return reason.Contains("Too many open files") || reason.Contains("TooManyOpenSockets") || reason.Contains("NoBufferSpaceAvailable") || reason.Contains("AddressAlreadyInUse") || reason.Contains("AddressNotAvailable")
                || reason.Contains("TryAgain") || reason.Contains("SystemNotReady");
        }

        /// <summary>Fraction of the time that the searches of this resolution waited for a DHT query slot (the 8 parallel queries count separately).</summary>
        public double DhtWaitFraction
        {
            get { return ElapsedSeconds <= 0 ? 0 : Math.Min(1.0, Dht.SlotWaitSeconds / (8 * ElapsedSeconds)); }
        }
    }
}
