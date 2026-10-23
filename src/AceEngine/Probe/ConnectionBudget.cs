using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Probe
{
    /// <summary>
    /// Budget of outgoing TCP connections of the checker, shared by all the checks in progress (resolving the descriptor and probing the live stream): each one asks for the
    /// "units" (connections) it is going to use and releases them when done, so that the simultaneous total never exceeds the capacity. It is for the home network: 56 units keep the peak
    /// below ~60 connections. Whoever waits for its turn does so without spending its deadline (a channel's deadline counts from when it gets the budget). Two priorities: probing a channel
    /// that is already resolved goes ahead of new resolutions (otherwise a resolved channel would wait behind all those that are not yet); within each one, in arrival order, which is the
    /// order of the list (visible ones first).
    /// </summary>
    public sealed class ConnectionBudget
    {
        public const int High = 0, Low = 1;
        private sealed class Waiter { public int Units; public TaskCompletionSource<bool> Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); public bool Done; }

        private readonly object _gate = new object();
        private readonly LinkedList<Waiter>[] _queues = { new LinkedList<Waiter>(), new LinkedList<Waiter>() };
        private int _used, _peak;

        public ConnectionBudget(int capacity) { Capacity = Math.Max(1, capacity); }

        public int Capacity { get; private set; }
        public int InUse { get { lock (_gate) return _used; } }
        public int Peak { get { lock (_gate) return _peak; } }
        public int Waiting { get { lock (_gate) return _queues[0].Count + _queues[1].Count; } }

        /// <summary>Waits until 'units' units are free and takes them; they must be released with Release (the amount asked for, capped to the capacity).</summary>
        public async Task<int> AcquireAsync(int units, int priority, CancellationToken ct)
        {
            units = Math.Max(1, Math.Min(units, Capacity));
            Waiter w;
            LinkedListNode<Waiter> node;
            lock (_gate)
            {
                if (_queues[0].Count == 0 && (priority == High || _queues[1].Count == 0) && _used + units <= Capacity) { Take(units); return units; }
                w = new Waiter { Units = units };
                node = _queues[priority == High ? 0 : 1].AddLast(w);
            }
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);
            if (ct.CanBeCanceled)
                reg = ct.Register(() =>
                {
                    lock (_gate) { if (!w.Done) { w.Done = true; try { node.List.Remove(node); } catch (InvalidOperationException) { } } }
                    w.Tcs.TrySetCanceled();
                });
            try { await w.Tcs.Task.ConfigureAwait(false); }
            finally { reg.Dispose(); }
            return units;
        }

        public void Release(int units)
        {
            lock (_gate)
            {
                _used = Math.Max(0, _used - units);
                Grant();
            }
        }

        private void Take(int units)
        {
            _used += units;
            if (_used > _peak) _peak = _used;
        }

        /// <summary>Distributes what was released: first the high-priority ones in order; the low ones only if no high one is left waiting.</summary>
        private void Grant()
        {
            for (int q = 0; q < _queues.Length; q++)
            {
                var list = _queues[q];
                while (list.First != null)
                {
                    var w = list.First.Value;
                    if (_used + w.Units > Capacity) return;                    // the first in the queue does not fit: wait (nobody within the same priority is overtaken)
                    list.RemoveFirst();
                    if (w.Done) continue;
                    w.Done = true;
                    Take(w.Units);
                    w.Tcs.TrySetResult(true);
                }
            }
        }
    }
}
