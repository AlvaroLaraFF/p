using System;

namespace PUi.Platform
{
    /// <summary>
    /// Audio focus: requested on opening and on resuming, released on pausing or leaving; this way the background TV source stops sounding.
    /// On the Samsung TV that is all it does (the app only has to silence the TV source and nobody takes the focus from it), so Changed is never raised there.
    /// On Android the focus is a real arbitration between apps and Changed is how the system takes it away and gives it back.
    /// </summary>
    public interface IAudioFocus
    {
        void Acquire();
        void Release();

        /// <summary>
        /// The system changed the focus after Acquire (see AudioFocusChange). Raised on the interface thread. Nothing in PUi reacts to it yet: pausing or ducking the player is the
        /// Android shell's job (it needs the player's volume).
        /// </summary>
        event Action<AudioFocusChange> Changed;
    }
}
