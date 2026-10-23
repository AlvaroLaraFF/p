using System;
using System.Threading.Tasks;
using PUi.Platform;

namespace PUi.Host
{
    public static class UiThreadExtensions
    {
        /// <summary>Runs the action on the interface thread and completes when it has run (the exception, if any, goes to the task). It always posts, even when already on that thread.</summary>
        public static Task RunAsync(this IUiThread ui, Action action)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(() =>
            {
                try { action(); tcs.SetResult(0); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }
    }
}
