using System;
using System.Collections.Generic;
using System.Threading;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Wrapper of ILiveChecker for the UI: Check and Stop do not block the caller. Starting a batch (creating the scheduler, seeding the queue...) can take
    /// 150-200 ms, and if it is done on the UI thread frames are lost (the screen freezes: it was seen as a flicker when searching or opening a list).
    /// Calls run on a thread of their own, in order and with "last one wins": several consecutive Checks (when typing in the search) only run the last one, and
    /// a Stop after a pending Check cancels it. GetStatus, Running, Done, Total and StatusChanged pass straight through to the real checker.
    /// </summary>
    public sealed class AsyncLiveChecker : ILiveChecker, AceEngine.Probe.IPlaybackReporter, IRestAware, IDisposable
    {
        private const string Tag = "livechecker";
        private readonly ILiveChecker _inner;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private List<Channel> _pending;
        private bool _stop, _disposed;
        private int _calls, _coalesced;

        public AsyncLiveChecker(ILiveChecker inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
            _inner.StatusChanged += (c, s) => { var h = StatusChanged; if (h != null) h(c, s); };
            _thread = new Thread(Loop) { IsBackground = true, Name = "livechecker-ui" };
            _thread.Start();
        }

        public event Action<Channel, ChannelStatus> StatusChanged;

        public ChannelStatus GetStatus(Channel channel) { return _inner.GetStatus(channel); }
        public bool Running { get { return _inner.Running; } }
        public int Done { get { return _inner.Done; } }
        public int Total { get { return _inner.Total; } }

        /// <summary>Batches requested and batches skipped because another arrived before they ran (for the log and the tests).</summary>
        public int Calls { get { return Volatile.Read(ref _calls); } }
        public int Coalesced { get { return Volatile.Read(ref _coalesced); } }

        public void Check(IReadOnlyList<Channel> channels)
        {
            var copy = channels == null ? new List<Channel>() : new List<Channel>(channels);
            lock (_gate)
            {
                if (_disposed) return;
                _calls++;
                if (_pending != null) _coalesced++;
                _pending = copy; _stop = false;
            }
            _wake.Set();
        }

        /// <summary>The UI's rest notice passes straight to the real checker (it is a write of a flag).</summary>
        public void SetResting(bool resting)
        {
            var r = _inner as IRestAware;
            if (r != null) r.SetResting(resting);
        }

        /// <summary>What the player sees passes straight to the real checker (it is fast: it starts nothing and does not touch the UI thread).</summary>
        public void Report(AceEngine.Probe.PlaybackEvidence evidence)
        {
            var r = _inner as AceEngine.Probe.IPlaybackReporter;
            if (r == null) return;
            try { r.Report(evidence); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Warn(Tag, "player notice: " + ex.GetType().Name + " " + ex.Message); }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_pending != null) _coalesced++;
                _pending = null; _stop = true;
            }
            _wake.Set();
        }

        /// <summary>Waits for what is pending to run (tests).</summary>
        public bool WaitIdle(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_gate) if (_pending == null && !_stop && !_busy) return true;
                Thread.Sleep(2);
            }
            return false;
        }

        private volatile bool _busy;

        private void Loop()
        {
            while (true)
            {
                _wake.WaitOne();
                List<Channel> batch; bool stop;
                lock (_gate)
                {
                    if (_disposed) return;
                    batch = _pending; stop = _stop; _pending = null; _stop = false;
                    _busy = batch != null || stop;
                }
                try
                {
                    if (batch != null) _inner.Check(batch);
                    else if (stop) _inner.Stop();
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { AceLog.Warn(Tag, "checker: " + ex.GetType().Name + " " + ex.Message); }
                finally { _busy = false; }
            }
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; _pending = null; }
            _wake.Set();
            try { _inner.Stop(); } catch { }
            var d = _inner as IDisposable;
            if (d != null) { try { d.Dispose(); } catch { } }
        }
    }
}
