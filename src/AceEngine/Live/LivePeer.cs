using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Live
{
    /// <summary>What LivePeer needs from the swarm that contains it.</summary>
    internal interface ILivePeerHost
    {
        EngineOptions Options { get; }
        byte[] InfoHash { get; }
        byte[] LocalPeerId { get; }
        /// <summary>EXTEND frame (20/0) for this peer, already signed. Sent right after the handshake.</summary>
        byte[] BuildExtendFrame(LivePeer peer);
        /// <summary>Handshake completed. Returns false to reject (duplicate, full...).</summary>
        bool OnPeerReady(LivePeer peer);
        void OnPeerMessage(LivePeer peer, byte[] msg);
        void OnPeerClosed(LivePeer peer, string reason);
    }

    /// <summary>A piece requested from a peer.</summary>
    internal sealed class LiveRequest
    {
        public long Index;
        public long SentMs;
    }

    /// <summary>
    /// TCP connection with a live peer: handshake (BitTorrent protocol or AceStreamProtocol), signed EXTEND as the first message in both
    /// directions and length framing. Here there is only I/O and the per-peer state; the logic is in LiveSwarm under its lock.
    /// </summary>
    internal sealed class LivePeer : IDisposable
    {
        private const string Tag = "livepeer";
        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private int _closed;
        private ILivePeerHost _host;

        public LivePeer(IPEndPoint endpoint, bool incoming)
        {
            EndPoint = endpoint;
            Incoming = incoming;
            CreatedMs = NowMs();
            LastReceiveMs = CreatedMs;
            Trace = new LivePeerTrace { EndPoint = endpoint.ToString(), Incoming = incoming, StartAbs = CreatedMs };
        }

        /// <summary>What happened with this connection (diagnostics and liveness criterion; see LiveFunnel).</summary>
        public LivePeerTrace Trace { get; private set; }

        public IPEndPoint EndPoint { get; private set; }
        public bool Incoming { get; private set; }
        public byte[] RemotePeerId { get; private set; }
        /// <summary>Protocol name the peer answered with ("BitTorrent protocol" or "AceStreamProtocol").</summary>
        public string RemoteProtocol { get; private set; }
        public byte[] RemoteReserved { get; private set; }
        public bool IsClosed { get { return Volatile.Read(ref _closed) != 0; } }
        public string CloseReason { get; private set; }
        public bool Ready;                       // handshake done and accepted by the swarm
        public bool Connecting;
        public long CreatedMs { get; private set; }
        public long LastReceiveMs;

        // ---- protocol state (mutated by the swarm under its lock) ----
        public bool SentExtend;
        public bool RemoteExtended;              // its EXTEND has arrived: until then only EXTEND can be sent
        public LiveExtendInfo Ext;
        public LiveCaps Caps = LiveCaps.FromVersion(0);
        public bool PeerChoking = true;
        public bool PeerInterested;
        public bool AmChoking = true;
        public bool AmInterested;
        public readonly LivePieceMap Have = new LivePieceMap();
        public bool SawTimestampsInHave;         // the peer sends HAVE/BITFIELD with timestamps (deduced from the length)
        public bool SawNoTimestamps;
        public long RemotePos = -1;              // remote's CHANGEPOS: we do not send it HAVE outside [pos, pos+window)
        public long RemoteWindow;
        public long MyInfoPosition = -1, MyInfoMax = -1, MyInfoMin = -1;
        /// <summary>Last live position this peer has reported (EXTEND, MYINFO, BITFIELD, HAVE). It is overwritten with the most recent one to see the sender's restarts.</summary>
        public long HeadClaim = -1;
        public readonly List<long> Outstanding = new List<long>();      // pieces requested from this peer
        public int OutstandingChunks;
        public int Strikes;
        public int BadPieces;
        public bool Snubbed;
        public long LastBlockMs;
        public long UnchokedAtMs;
        public readonly RateMeter Down = new RateMeter(5);
        public readonly RateMeter Up = new RateMeter(5);
        public long LastMyInfoSentMs, LastChangePosSentMs, LastKeepAliveSentMs, LastPingSentMs;
        public long SentChangePos = -1;
        public int PingSeq;
        public int MessagesIn;
        public bool GotBitfield;
        public string Label;                     // for the logs

        public static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        public override string ToString() { return (Incoming ? "<-" : "->") + EndPoint; }

        // ---------- Outgoing connection ----------

        public async Task RunOutgoingAsync(ILivePeerHost host, LiveOptions live)
        {
            _host = host;
            var opt = host.Options;
            string reason = "closed";
            try
            {
                var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                _tcp = tcp;
                Trace.Reach(LiveStage.Connecting);
                var connect = tcp.ConnectAsync(EndPoint.Address, EndPoint.Port).Observed();
                var done = await Task.WhenAny(connect, Task.Delay(live.ConnectTimeout > TimeSpan.Zero ? live.ConnectTimeout : opt.ConnectTimeout, _cts.Token)).ConfigureAwait(false);
                if (done != connect) { reason = "connect timeout"; return; }
                await connect.ConfigureAwait(false);
                _stream = tcp.GetStream();
                Trace.TcpAbs = NowMs();
                Trace.Reach(LiveStage.TcpOk);

                // 68/66-byte handshake at once. BitTorrent's reserved is VOD's (…30…); AceStreamProtocol's is zeros.
                var hs = BuildHandshake(live.UseAceProtocolName, opt.HandshakeReserved, host.InfoHash, host.LocalPeerId);
                await _stream.WriteAsync(hs, 0, hs.Length, _cts.Token).ConfigureAwait(false);

                string hsReason = await ReadHandshakeAsync(host, live.HandshakeTimeout > TimeSpan.Zero ? live.HandshakeTimeout : opt.HandshakeTimeout).ConfigureAwait(false);
                if (hsReason != null) { reason = hsReason; return; }
                Trace.RemoteProtocol = RemoteProtocol;
                Trace.HandshakeAbs = NowMs();
                Trace.Reach(LiveStage.HandshakeOk);
                reason = await RunSessionAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { reason = "cancelled"; }
            catch (SocketException ex) { reason = "socket: " + ex.SocketErrorCode; }
            catch (IOException ex) { reason = "I/O: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
            catch (ObjectDisposedException) { reason = "closed"; }
            catch (Exception ex)
            {
                reason = "error: " + ex.GetType().Name + " " + ex.Message;
                AceLog.Warn(Tag, this + " " + reason);
            }
            finally { Close(reason); }
        }

        /// <summary>Incoming connection: the listener already read the start of the handshake; here we answer with the same protocol name.</summary>
        public async Task RunIncomingAsync(ILivePeerHost host, TcpClient tcp, bool aceName, byte[] remoteReserved)
        {
            _host = host;
            _tcp = tcp;
            string reason = "closed";
            try
            {
                tcp.NoDelay = true;
                _stream = tcp.GetStream();
                RemoteProtocol = aceName ? "AceStreamProtocol" : Wire.ProtocolName;
                RemoteReserved = remoteReserved;
                var hs = BuildHandshake(aceName, host.Options.HandshakeReserved, host.InfoHash, host.LocalPeerId);
                await _stream.WriteAsync(hs, 0, hs.Length, _cts.Token).ConfigureAwait(false);
                var pid = new byte[20];
                using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    hsCts.CancelAfter(host.Options.HandshakeTimeout);
                    try { await ReadExactAsync(pid, 20, hsCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { reason = "handshake timeout"; return; }
                }
                RemotePeerId = pid;
                if (Bytes.Equal(pid, host.LocalPeerId)) { reason = "connection to self"; return; }
                Trace.RemoteProtocol = RemoteProtocol;
                Trace.TcpAbs = Trace.HandshakeAbs = NowMs();
                Trace.Reach(LiveStage.HandshakeOk);
                reason = await RunSessionAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { reason = "cancelled"; }
            catch (SocketException ex) { reason = "socket: " + ex.SocketErrorCode; }
            catch (IOException ex) { reason = "I/O: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
            catch (ObjectDisposedException) { reason = "closed"; }
            catch (Exception ex)
            {
                reason = "error: " + ex.GetType().Name + " " + ex.Message;
                AceLog.Warn(Tag, this + " " + reason);
            }
            finally { Close(reason); }
        }

        public static byte[] BuildHandshake(bool aceName, byte[] btReserved, byte[] infoHash, byte[] peerId)
        {
            if (!aceName) return Wire.BuildHandshake(btReserved, infoHash, peerId);
            const string name = "AceStreamProtocol";
            var b = new byte[1 + name.Length + 8 + 20 + 20];
            b[0] = (byte)name.Length;
            Encoding.ASCII.GetBytes(name, 0, name.Length, b, 1);
            // reserved set to zero
            Buffer.BlockCopy(infoHash, 0, b, 1 + name.Length + 8, 20);
            Buffer.BlockCopy(peerId, 0, b, 1 + name.Length + 8 + 20, 20);
            return b;
        }

        /// <summary>Reads the response to the outgoing handshake. Returns null if it is valid or the reason for the rejection.</summary>
        private async Task<string> ReadHandshakeAsync(ILivePeerHost host, TimeSpan handshakeTimeout)
        {
            var one = new byte[1];
            byte[] rest;
            int plen;
            using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                hsCts.CancelAfter(handshakeTimeout);
                try
                {
                    await ReadExactAsync(one, 1, hsCts.Token).ConfigureAwait(false);
                    plen = one[0];
                    if (plen != 19 && plen != 17) return "bad header len " + plen;
                    rest = new byte[plen + 8 + 20 + 20];
                    await ReadExactAsync(rest, rest.Length, hsCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { return "handshake timeout"; }
            }
            string pstr = Encoding.ASCII.GetString(rest, 0, plen);
            if (pstr != Wire.ProtocolName && pstr != "AceStreamProtocol") return "protocol " + pstr;
            RemoteProtocol = pstr;
            RemoteReserved = new byte[8];
            Buffer.BlockCopy(rest, plen, RemoteReserved, 0, 8);
            for (int i = 0; i < 20; i++)
                if (rest[plen + 8 + i] != host.InfoHash[i]) return "infohash mismatch (" + Hex.ToHex(rest, plen + 8, 20) + ")";
            RemotePeerId = new byte[20];
            Buffer.BlockCopy(rest, plen + 8 + 20, RemotePeerId, 0, 20);
            if (Bytes.Equal(RemotePeerId, host.LocalPeerId)) return "connection to self";
            return null;
        }

        // ---------- Session ----------

        private async Task<string> RunSessionAsync()
        {
            // The first thing sent is the signed EXTEND and nothing else until the remote's arrives.
            if (!_host.OnPeerReady(this)) return "rejected by the swarm";
            LastReceiveMs = NowMs();
            var writer = Task.Run(WriteLoopAsync);
            SentExtend = true;
            var ext = _host.BuildExtendFrame(this);
            _sendQueue.Enqueue(ext);
            _sendSignal.Release();
            return await ReadLoopAsync().ConfigureAwait(false);
        }

        private async Task<string> ReadLoopAsync()
        {
            var ct = _cts.Token;
            var lenBuf = new byte[4];
            int max = _host.Options.MaxMessageLength;
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(lenBuf, 4, ct).ConfigureAwait(false);
                LastReceiveMs = NowMs();
                uint len = Wire.GetU32(lenBuf, 0);
                if (len == 0) continue;                              // keepalive
                if (len > (uint)max) return "message too big (" + len + ")";
                var msg = await StreamUtil.ReadMessageAsync(_stream, (int)len, ct).ConfigureAwait(false);
                LastReceiveMs = NowMs();
                _host.OnPeerMessage(this, msg);
            }
            return "cancelled";
        }

        private async Task WriteLoopAsync()
        {
            var ct = _cts.Token;
            var batch = new byte[64 * 1024];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(ct).ConfigureAwait(false);
                    int used = 0;
                    byte[] frame;
                    while (_sendQueue.TryDequeue(out frame))
                    {
                        if (frame.Length > batch.Length - used)
                        {
                            if (used > 0) { await _stream.WriteAsync(batch, 0, used, ct).ConfigureAwait(false); used = 0; }
                            if (frame.Length > batch.Length) { await _stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false); continue; }
                        }
                        Buffer.BlockCopy(frame, 0, batch, used, frame.Length);
                        used += frame.Length;
                    }
                    if (used > 0) await _stream.WriteAsync(batch, 0, used, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Close("write: " + ex.Message); }
        }

        /// <summary>
        /// Enqueues a frame. Until the remote EXTEND arrives the original silently drops everything that is not EXTEND ("skip message before
        /// extend"); same here except allowBeforeExtend.
        /// </summary>
        public bool Send(byte[] frame, bool allowBeforeExtend = false)
        {
            if (IsClosed) return false;
            if (!RemoteExtended && !allowBeforeExtend) return false;
            _sendQueue.Enqueue(frame);
            _sendSignal.Release();
            return true;
        }

        public int QueuedFrames { get { return _sendQueue.Count; } }

        private async Task ReadExactAsync(byte[] buf, int count, CancellationToken ct)
        {
            int got = 0;
            while (got < count)
            {
                int n = await _stream.ReadAsync(buf, got, count - got, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("the peer closed the connection");
                got += n;
            }
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            CloseReason = reason;
            Trace.CloseReason = reason;
            Trace.ClosedAbs = NowMs();
            try { _cts.Cancel(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            var h = _host;
            if (h != null)
            {
                try { h.OnPeerClosed(this, reason); } catch (Exception ex) { AceLog.Warn(Tag, "OnPeerClosed: " + ex.Message); }
            }
        }

        public void Dispose() { Close("dispose"); }
    }

    internal static class Bytes
    {
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
