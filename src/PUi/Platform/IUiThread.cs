using System;

namespace PUi.Platform
{
    /// <summary>
    /// Interface thread (the platform's main loop). All of PUi runs on it, and so do the platform's window, view and player objects. Anything else (the engine, timers, the
    /// key script) goes back to it with Post.
    /// </summary>
    public interface IUiThread
    {
        /// <summary>Runs the action later on the interface thread, from any thread. It always posts, even when already on that thread.</summary>
        void Post(Action action);

        /// <summary>
        /// true only when the CALLING thread is the interface thread. The platform records the thread at startup (it never answers a constant), so the code that must run on the
        /// interface thread, and the tests, can check it.
        /// </summary>
        bool IsUiThread { get; }
    }
}
