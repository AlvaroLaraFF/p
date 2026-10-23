namespace PUi.Platform
{
    /// <summary>
    /// The kind of device the interface is laid out for. Decided by ScreenMetrics from the capabilities (touch or not) and the physical size of the screen (density), not from the
    /// platform's name:
    ///  - Tv: no touch. The 1920 x 1080 design of the TV, exactly as it always was (a remote drives it; letterboxed if the surface is not 16:9).
    ///  - PhonePortrait: touch, a surface taller than wide and a smaller side under 600 dp. 390 logical units wide, as tall as the safe area needs.
    ///  - TabletPortrait: touch, taller than wide, a smaller side of 600 dp or more. 800 units wide.
    ///  - TabletLandscape: touch and a surface as wide as tall or wider. 1280 units wide. A phone only has a landscape surface while the player is full screen (the shell asks
    ///    for it with IOrientationControl), and it gets this layout then: the full screen player with the controls over the picture.
    /// </summary>
    public enum DeviceKind { Tv = 0, PhonePortrait = 1, TabletPortrait = 2, TabletLandscape = 3 }
}
