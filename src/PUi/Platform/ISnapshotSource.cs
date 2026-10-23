using System;

namespace PUi.Platform
{
    /// <summary>Screen captures, for the tests that compare what a device draws with the desktop bench (key script "snap_*" labels). Optional: IPlatform.Snapshot is null where unsupported.</summary>
    public interface ISnapshotSource
    {
        /// <summary>Runs the action right after the next frame is drawn, where reading the surface is valid (on the TV, with the GL context current). Requests that frame.</summary>
        void AfterNextFrame(Action action);

        /// <summary>PNG of the surface as last drawn; null if it cannot be read. Call it from the action given to AfterNextFrame.</summary>
        byte[] SnapshotPng();
    }
}
