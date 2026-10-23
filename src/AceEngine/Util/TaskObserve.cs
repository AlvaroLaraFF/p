using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Util
{
    internal static class TaskObserve
    {
        /// <summary>
        /// Marks the task's failure as observed, whatever happens to it, and returns the same task (awaiting it still throws as before). For a task that may be ABANDONED: a connect raced
        /// against a timeout with Task.WhenAny, where the loser's connection is then closed and the task fails (on Android: SocketException 125 "Operation Canceled") with nobody left to look
        /// at it, and the runtime reports it later as an unobserved task exception.
        /// </summary>
        public static Task Observed(this Task task)
        {
            if (task != null)
                task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
}
