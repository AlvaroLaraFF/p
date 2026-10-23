using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AceEngine.App.Tv;
using AceEngine.Probe;
using AceEngine.Sources;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    /// <summary>The UI's Check and Stop do not block the caller's thread (starting a batch took 150-200 ms on the paint thread), they run in order and the last one wins.</summary>
    public class AsyncLiveCheckerTests
    {
        private sealed class SlowChecker : ILiveChecker
        {
            public readonly List<string> Calls = new List<string>();
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(true);
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public int SleepMs;
            public event Action<Channel, ChannelStatus> StatusChanged;
            public bool Running { get { return false; } }
            public int Done { get { return 7; } }
            public int Total { get { return 9; } }
            public ChannelStatus GetStatus(Channel c) { return new ChannelStatus { State = LinkState.Good, Peers = 5 }; }
            public void Check(IReadOnlyList<Channel> channels) { Entered.Set(); Gate.Wait(); if (SleepMs > 0) Thread.Sleep(SleepMs); lock (Calls) Calls.Add("check:" + string.Join(",", channels.Select(c => c.Name))); }
            public void Stop() { Gate.Wait(); lock (Calls) Calls.Add("stop"); }
            public void Raise(Channel c) { var h = StatusChanged; if (h != null) h(c, new ChannelStatus()); }
        }

        private static List<Channel> Chs(params string[] names)
        {
            return names.Select((n, i) => new Channel { Id = "cid:" + i.ToString("x").PadLeft(40, 'c') + n, Name = n, Entry = LinkEntry.FromUrl(n, "acestream://" + i.ToString("x").PadLeft(40, 'd')) }).ToList();
        }

        [Fact]
        public void CheckAndStopReturnAtOnceEvenWhenTheRealCheckerIsBusy()
        {
            var inner = new SlowChecker { SleepMs = 0 };
            inner.Gate.Reset();                                   // the real checker "takes a while": it does not finish until the door is opened
            var a = new AsyncLiveChecker(inner);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a.Check(Chs("a", "b")); a.Check(Chs("c")); a.Stop(); a.Check(Chs("d"));
            Assert.True(sw.ElapsedMilliseconds < 100, "the calls must not wait for the checker (" + sw.ElapsedMilliseconds + " ms)");
            inner.Gate.Set();
            Assert.True(a.WaitIdle(3000));
            a.Dispose();
        }

        [Fact]
        public void CallsRunInOrderOnAnotherThreadAndTheLastOneWins()
        {
            var inner = new SlowChecker();
            inner.Gate.Reset();
            var a = new AsyncLiveChecker(inner);
            a.Check(Chs("primero"));
            Assert.True(inner.Entered.Wait(2000));                // the first one is already inside, blocked
            a.Check(Chs("x")); a.Check(Chs("y")); a.Check(Chs("z"));       // meanwhile three arrive: only the last one will run
            inner.Gate.Set();
            Assert.True(a.WaitIdle(3000));
            lock (inner.Calls) Assert.Equal(new[] { "check:primero", "check:z" }, inner.Calls.ToArray());
            Assert.Equal(4, a.Calls); Assert.Equal(2, a.Coalesced);
            a.Dispose();
        }

        [Fact]
        public void AStopAfterAPendingCheckCancelsItAndACheckAfterAStopWins()
        {
            var inner = new SlowChecker();
            inner.Gate.Reset();
            var a = new AsyncLiveChecker(inner);
            a.Check(Chs("primero")); Assert.True(inner.Entered.Wait(2000));
            a.Check(Chs("pendiente")); a.Stop();
            inner.Gate.Set(); Assert.True(a.WaitIdle(3000));
            lock (inner.Calls) Assert.Equal(new[] { "check:primero", "stop" }, inner.Calls.ToArray());
            inner.Gate.Reset();
            a.Stop(); Thread.Sleep(30);
            a.Check(Chs("despues")); inner.Gate.Set();
            Assert.True(a.WaitIdle(3000));
            lock (inner.Calls) Assert.Equal("check:despues", inner.Calls.Last());
            a.Dispose();
        }

        [Fact]
        public void StatusReadsAndEventsPassStraightThrough()
        {
            var inner = new SlowChecker();
            var a = new AsyncLiveChecker(inner);
            Assert.Equal(7, a.Done); Assert.Equal(9, a.Total); Assert.False(a.Running);
            var st = a.GetStatus(Chs("a")[0]);
            Assert.Equal(LinkState.Good, st.State); Assert.Equal(5, st.Peers);
            Channel got = null; a.StatusChanged += (c, s) => got = c;
            var ch = Chs("q")[0]; inner.Raise(ch);
            Assert.Same(ch, got);
            a.Dispose();
        }

        [Fact]
        public void TheBatchIsCopiedSoLaterChangesToTheCallersListDoNotAffectIt()
        {
            var inner = new SlowChecker();
            inner.Gate.Reset();
            var a = new AsyncLiveChecker(inner);
            var list = Chs("a", "b");
            a.Check(list); Assert.True(inner.Entered.Wait(2000));
            list.Clear();
            inner.Gate.Set(); Assert.True(a.WaitIdle(3000));
            lock (inner.Calls) Assert.Equal("check:a,b", inner.Calls[0]);
            a.Dispose();
        }

        [Fact]
        public void AFailingCheckerDoesNotKillTheWorker()
        {
            var calls = new List<string>();
            var inner = new ThrowOnce();
            var a = new AsyncLiveChecker(inner);
            a.Check(Chs("a")); Assert.True(a.WaitIdle(3000));
            a.Check(Chs("b")); Assert.True(a.WaitIdle(3000));
            Assert.Equal(2, inner.Calls);
            a.Dispose();
        }

        private sealed class ThrowOnce : ILiveChecker
        {
            public int Calls;
            public event Action<Channel, ChannelStatus> StatusChanged;
            public bool Running { get { return false; } } public int Done { get { return 0; } } public int Total { get { return 0; } }
            public ChannelStatus GetStatus(Channel c) { return new ChannelStatus(); }
            public void Check(IReadOnlyList<Channel> channels) { if (++Calls == 1) throw new InvalidOperationException("failure"); }
            public void Stop() { }
        }
    }
}
