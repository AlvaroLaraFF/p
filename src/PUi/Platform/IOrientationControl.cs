namespace PUi.Platform
{
    /// <summary>What PUi asks of the screen orientation.</summary>
    public enum OrientationRequest
    {
        /// <summary>No request of its own: the orientation the device says (a tablet turns with the sensor; a phone goes back to what the shell locks it to).</summary>
        Device = 0,
        /// <summary>Vertical (portrait).</summary>
        Portrait = 1,
        /// <summary>Horizontal (landscape), whichever side the device is held on.</summary>
        Landscape = 2
    }

    /// <summary>
    /// OPTIONAL platform contract: the orientation of the screen. The Samsung TV does not implement it (it has one orientation). A phone is used only in portrait: the shell locks it
    /// there when it starts. When the user presses "Pantalla completa" in the player, PUi asks for Landscape: the shell turns the screen, raises ScreenChanged with the new (wider than
    /// tall) surface and PUi draws the full screen player. On leaving full screen (or the player) PUi asks for Portrait on a phone and for Device on a tablet, and the shell goes back to its own
    /// rule. The request is a wish: the platform may take its time (ScreenChanged tells when it is done) or refuse (a split screen); PUi works with whatever surface it is given.
    /// </summary>
    public interface IOrientationControl
    {
        void Request(OrientationRequest request);
    }
}
