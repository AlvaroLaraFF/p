using System;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Live
{
    /// <summary>
    /// Pump of the live stream with memory: the swarm's only real reader. It dumps the WHOLE stream into the TimeshiftBuffer while the session lives, however fast the
    /// player goes (or even if it is paused), so the swarm downloads as with a perfect player and rewinding only
    /// costs the buffer's memory. There is no backpressure: the oldest is evicted. It is asynchronous (it does not occupy a thread-pool thread while waiting).
    /// </summary>
    internal sealed class TimeshiftPump : IDisposable
    {
        private const string Tag = "timeshift";
        private const int ChunkBytes = 64 * 1024;                 // below the large object heap; it is reused
        private readonly LiveSwarm _swarm;
        private readonly TimeshiftBuffer _buf;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _task;
        private long _bytes;

        public TimeshiftPump(LiveSwarm swarm, TimeshiftBuffer buffer)
        {
            _swarm = swarm; _buf = buffer;
        }

        public long Bytes { get { return Interlocked.Read(ref _bytes); } }

        public void Start()
        {
            _task = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            var chunk = new byte[ChunkBytes];
            LiveReaderState rs = null;
            try
            {
                rs = _swarm.OpenReader();
                long seenCuts = 0;
                bool raw = false;
                while (!ct.IsCancellationRequested)
                {
                    var signal = _swarm.Changed.Wait();                        // captured BEFORE reading: wakeups are not lost
                    int n;
                    var st = _swarm.TryRead(rs, chunk, 0, chunk.Length, out n);
                    if (rs.Discontinuities != seenCuts) { seenCuts = rs.Discontinuities; _buf.Discontinuity(); }
                    if (rs.Passthrough && !raw)
                    {
                        raw = true;
                        _buf.SetRawMode();
                        AceLog.Info(Tag, "the content is not MPEG-TS: stored without analysis (no time index)");
                    }
                    if (st == LiveReadResult.Data)
                    {
                        try { _buf.Append(chunk, 0, n); }
                        catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is ObjectDisposedException))
                        {
                            // A failure of the stream analysis must not kill the pump (and with it the live stream): it keeps storing without analysis
                            AceLog.Warn(Tag, "stream analysis: " + ex.GetType().Name + ": " + ex.Message + "; storing without analysis");
                            _buf.SetRawMode();
                            raw = true;
                            _buf.Append(chunk, 0, n);
                        }
                        Interlocked.Add(ref _bytes, n);
                        continue;                                              // there is more: it keeps reading without waiting
                    }
                    if (st == LiveReadResult.Stopped) break;
                    var tick = Task.Delay(250, ct);                            // and a wait cap in case the gap policy changes without notice
                    await Task.WhenAny(signal, tick).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AceLog.Warn(Tag, "pump: " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                try { if (rs != null) _swarm.CloseReader(rs); } catch { }
                _buf.Complete();
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
