using System;
using System.Threading.Tasks;
using AceEngine.App;
using ElmSharp;

namespace PTizen.Platform
{
    /// <summary>Access to the main thread (the Ecore loop): ElmSharp and Tizen.Multimedia.Player are touched only from there.</summary>
    internal static class Ui
    {
        public static void Post(Action a)
        {
            EcoreMainloop.PostAndWakeUp(() =>
            {
                try { a(); } catch (Exception ex) { UdpLog.Error("ui", "exception on the main thread: " + ex); }
            });
        }

        public static Task<T> Run<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            EcoreMainloop.PostAndWakeUp(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }

        public static Task Run(Action a)
        {
            return Run<int>(() => { a(); return 0; });
        }
    }
}
