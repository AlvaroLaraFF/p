namespace PUi.Platform
{
    /// <summary>A rectangle of the drawing surface, in PIXELS (the surface's own pixels, origin at its top-left corner).</summary>
    public struct PixelRect
    {
        public int Left, Top, Right, Bottom;
        public PixelRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public override string ToString() { return Left + "," + Top + " " + Width + "x" + Height; }
    }

    /// <summary>
    /// OPTIONAL platform contract: where the video goes. On the Samsung TV the video always fills the screen and the platform does not implement this (nothing to do). On a phone or a
    /// tablet the player does not always cover the whole screen: in portrait the picture is a 16:9 area at the top and the rest of the page is interface. PUi works the rectangle out in
    /// logical units (the canvas's) and converts it to pixels with ScreenMetrics.ToPixelX / ToPixelY before telling the platform.
    ///
    /// The conversation (all on the interface thread):
    ///  - SetVideoRect(rect): from now on the video picture belongs in this rectangle of the surface. The platform fits the picture inside it keeping its own aspect ratio (bars of
    ///    black if it differs) and shows nothing of the video outside it. PUi draws the interface everywhere else with opaque pixels and leaves the pixels of the rectangle
    ///    TRANSPARENT (so a video view behind the drawing surface shows through them). It is called when the player opens, when the layout changes (rotation, full screen on or off)
    ///    and never more often than the rectangle changes.
    ///  - ClearVideoRect(): the player closed (or went back to the whole surface): the video, if there is one, fills the surface again. Harmless if nothing was set.
    /// A platform that does not implement the interface keeps the video where it always put it; PUi works the same, only that the picture is not where the interface left the hole.
    /// </summary>
    public interface IVideoPlacement
    {
        void SetVideoRect(PixelRect rect);
        void ClearVideoRect();
    }
}
