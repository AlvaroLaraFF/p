using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.P2P
{
    /// <summary>What PeerConnection needs from the swarm that contains it.</summary>
    internal interface IPeerHost
    {
        EngineOptions Options { get; }
        byte[] InfoHash { get; }
        byte[] LocalPeerId { get; }
        /// <summary>Handshake completed. Returns false to reject (duplicate, full...).</summary>
        bool OnPeerReady(PeerConnection peer);
        void OnPeerMessage(PeerConnection peer, byte id, byte[] payload);
        void OnPeerClosed(PeerConnection peer, string reason);
    }

    /// <summary>Block requested from a peer.</summary>
    internal sealed class BlockRef
    {
        public int Piece;
        public int Begin;
        public int Length;
        public long RequestedAtMs;
    }

    /// <summary>
    /// TCP connection with a peer: BitTorrent handshake with Ace's particularities, framing, write
    /// queue and read loop. The protocol state (choke, interest, bitfield, requests) is mutated by the
    /// swarm under its lock; here there is only I/O.
    /// </summary>
    internal sealed class PeerConnection : IDisposable
    {
        private const string Tag = "peer";

        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private int _closed;
        private IPeerHost _host;

        public PeerConnection(IPEndPoint endpoint, bool incoming)
        {
            EndPoint = endpoint;
            Incoming = incoming;
            CreatedMs = NowMs();
            LastReceiveMs = CreatedMs;
        }

        public IPEndPoint EndPoint { get; private set; }
        public bool Incoming { get; private set; }
        public byte[] RemotePeerId { get; private set; }
        public HandshakeInfo RemoteHandshake { get; private set; }
        public bool Ready;            // handshake completed and accepted by the swarm
        public bool Connecting;       // counted in Swarm._connecting until the handshake finishes or it closes
        public bool IsClosed { get { return Volatile.Read(ref _closed) != 0; } }
        public string CloseReason { get; private set; }

        // ---- protocol state (mutated by the swarm under its lock) ----
        public bool PeerChoking = true;
        public bool PeerInterested;
        public bool AmChoking = true;
        public bool AmInterested;
        public Bitfield Have;                          // null until the number of pieces is known
        public byte[] PendingBitfield;                 // BITFIELD received before the metadata was known
        public List<int> PendingHaves;
        public bool GotAnyMessage;                     // Ace requires BITFIELD as the first non-EXTEND message
        public ExtendedHandshakeInfo Ext;
        public bool SentExtHandshake;
        public readonly List<BlockRef> Outstanding = new List<BlockRef>();
        public int Backlog;
        public int BadPerformance;
        public int BadPieces;
        public bool Snubbed;
        public long SnubUntilMs;
        public readonly RateMeter Down = new RateMeter(5);
        public readonly RateMeter Up = new RateMeter(5);
        public long LastBlockMs;
        public long UnchokedAtMs;
        public bool MetadataRequestPending;
        public int MetadataPieceInFlight = -1;
        public long LastKeepAliveSentMs;
        public int PexSentCount;
        public long LastRechokeMs;

        public long CreatedMs { get; private set; }
        public long LastReceiveMs;                      // written by the read loop, read by the inactivity watchdog

        public static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        public bool IsSeed { get { return Have != null && Have.IsComplete; } }

        public override string ToString()
        {
            return (Incoming ? "<-" : "->") + EndPoint;
        }

        // ---------- Outgoing connection ----------

        public async Task RunOutgoingAsync(IPeerHost host)
        {
            _host = host;
            var opt = host.Options;
            string reason = "closed";
            try
            {
                var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                _tcp = tcp;
                var connect = tcp.ConnectAsync(EndPoint.Address, EndPoint.Port).Observed();
                var done = await Task.WhenAny(connect, Task.Delay(opt.ConnectTimeout, _cts.Token)).ConfigureAwait(false);
                if (done != connect) { reason = "connect timeout"; return; }
                await connect.ConfigureAwait(false);          // propagates errors
                _stream = tcp.GetStream();

                // All 68 bytes are sent at once: Ace as responder reads 48, writes 68 and reads 20.
                var hs = Wire.BuildHandshake(opt.HandshakeReserved, host.InfoHash, host.LocalPeerId);
                await _stream.WriteAsync(hs, 0, hs.Length, _cts.Token).ConfigureAwait(false);

                var buf = new byte[Wire.HandshakeLength];
                using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    hsCts.CancelAfter(opt.HandshakeTimeout);
                    try { await ReadExactAsync(buf, Wire.HandshakeLength, hsCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { reason = "handshake timeout"; return; }
                }
                HandshakeInfo info;
                if (!Wire.TryParseHandshake(buf, Wire.HandshakeLength, out info)) { reason = "invalid handshake"; return; }
                if (!BytesEqual(info.InfoHash, host.InfoHash)) { reason = "infohash mismatch"; return; }
                RemoteHandshake = info;
                RemotePeerId = info.PeerId;
                if (BytesEqual(info.PeerId, host.LocalPeerId)) { reason = "connection to self"; return; }

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
            finally
            {
                Close(reason);
            }
        }

        // ---------- Incoming connection ----------

        /// <summary>
        /// The listener already read the first 48 bytes, checked the infohash and assigned the swarm. Here we reply with the
        /// 68 bytes and read the initiator's peer_id (which Ace sends after reading ours).
        /// </summary>
        public async Task RunIncomingAsync(IPeerHost host, TcpClient tcp, HandshakeInfo head)
        {
            _host = host;
            _tcp = tcp;
            string reason = "closed";
            try
            {
                tcp.NoDelay = true;
                _stream = tcp.GetStream();
                RemoteHandshake = head;
                var hs = Wire.BuildHandshake(host.Options.HandshakeReserved, host.InfoHash, host.LocalPeerId);
                await _stream.WriteAsync(hs, 0, hs.Length, _cts.Token).ConfigureAwait(false);
                var pid = new byte[20];
                using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    hsCts.CancelAfter(host.Options.HandshakeTimeout);
                    try { await ReadExactAsync(pid, 20, hsCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { reason = "handshake timeout"; return; }
                }
                RemotePeerId = pid;
                if (BytesEqual(pid, host.LocalPeerId)) { reason = "connection to self"; return; }
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
            finally
            {
                Close(reason);
            }
        }

        // ---------- Session ----------

        private async Task<string> RunSessionAsync()
        {
            if (!_host.OnPeerReady(this)) return "rejected by the swarm";
            LastReceiveMs = NowMs();
            var writer = Task.Run(WriteLoopAsync);
            string reason = await ReadLoopAsync().ConfigureAwait(false);
            return reason;
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
                if (len == 0) continue;                      // keepalive
                if (len > (uint)max) return "message too big (" + len + ")";
                var msg = await StreamUtil.ReadMessageAsync(_stream, (int)len, ct).ConfigureAwait(false);
                LastReceiveMs = NowMs();
                _host.OnPeerMessage(this, msg[0], msg);
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
                    // The whole queue is drained; small messages are grouped into a single send.
                    // Leftover semaphore signals only cause empty loops.
                    int used = 0;
                    byte[] frame;
                    while (_sendQueue.TryDequeue(out frame))
                    {
                        if (frame.Length > batch.Length - used)
                        {
                            if (used > 0)
                            {
                                await _stream.WriteAsync(batch, 0, used, ct).ConfigureAwait(false);
                                used = 0;
                            }
                            if (frame.Length > batch.Length)
                            {
                                await _stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                                continue;
                            }
                        }
                        Buffer.BlockCopy(frame, 0, batch, used, frame.Length);
                        used += frame.Length;
                    }
                    if (used > 0) await _stream.WriteAsync(batch, 0, used, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Close("write: " + ex.Message);
            }
        }

        /// <summary>Enqueues an already framed message. Does not block.</summary>
        public void Send(byte[] frame)
        {
            if (IsClosed) return;
            _sendQueue.Enqueue(frame);
            _sendSignal.Release();
        }

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

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            CloseReason = reason;
            try { _cts.Cancel(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            var h = _host;
            if (h != null)
            {
                try { h.OnPeerClosed(this, reason); } catch (Exception ex) { AceLog.Warn(Tag, "OnPeerClosed: " + ex.Message); }
            }
        }

        public void Dispose()
        {
            Close("dispose");
        }
    }
}
