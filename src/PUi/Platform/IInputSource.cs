using System;
using PUi.Input;

namespace PUi.Platform
{
    public interface IInputSource
    {
        /// <summary>
        /// Remote key (or keyboard key) already translated to a logical key (PUi.Input.KeyNames), on the interface thread. A touch platform also maps its system Back
        /// gesture or button to UiKey.Back here: the interface has no other way to go back.
        /// </summary>
        event Action<KeyPress> KeyPressed;

        /// <summary>
        /// Finger or pointer events in logical coordinates (see PointerEvent), on the interface thread. Raised only by platforms with PlatformCapabilities.HasTouch; the TV never raises it
        /// (PUi ignores it anywhere else). PUi turns them into taps, drags with inertia, the player's gestures and typing in Search; the platform only reports the finger.
        /// </summary>
        event Action<PointerEvent> Pointer;
    }
}
