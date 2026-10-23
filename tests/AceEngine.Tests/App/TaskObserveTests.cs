using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>A connect that loses its race against a timeout is abandoned: its failure must not come back later as an unobserved task exception (it showed in the Android log every time a channel stopped).</summary>
    // These tests listen to the process-wide UnobservedTaskException event: run them alone, or exceptions leaked by other tests running in parallel are counted too
    [Collection(AceEngine.Tests.Cast.CastIsolatedCollection.Name)]
    public class TaskObserveTests
    {
        [Fact]
        public async Task AnObservedFailureIsNeverReportedAsUnobservedButStillThrowsWhenAwaited()
        {
            var reported = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) => reported.Enqueue(e.Exception);
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                for (int i = 0; i < 20; i++) Abandon(Fail().Observed());
                var kept = Fail().Observed();
                await Task.Delay(50);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
                await Task.Delay(50);
                Assert.True(reported.IsEmpty, "observed tasks were reported as unobserved");
                await Assert.ThrowsAsync<SocketException>(() => kept);
            }
            finally { TaskScheduler.UnobservedTaskException -= handler; }
        }

        [Fact]
        public void ObservingNothingIsHarmless() { Assert.Null(((Task)null).Observed()); }

        [Fact]
        public async Task ARealAbandonedConnectToAClosedPortDoesNotLeakAnUnobservedException()
        {
            var reported = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) => reported.Enqueue(e.Exception);
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    var tcp = new TcpClient(AddressFamily.InterNetwork);
                    var connect = tcp.ConnectAsync(IPAddress.Loopback, 1).Observed();           // refused at once; nobody awaits it, as when the timeout won
                    tcp.Close();
                    Abandon(connect);
                }
                await Task.Delay(100);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
                await Task.Delay(50);
                Assert.True(reported.IsEmpty);
            }
            finally { TaskScheduler.UnobservedTaskException -= handler; }
        }

        private static Task Fail() { return Task.Run(() => { throw new SocketException(125); }); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Abandon(Task t) { }
    }
}
