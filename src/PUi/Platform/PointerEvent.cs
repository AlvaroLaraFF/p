namespace PUi.Platform
{
    public enum PointerAction
    {
        /// <summary>A finger touched down or a button was pressed.</summary>
        Down,
        /// <summary>It moved while down.</summary>
        Move,
        /// <summary>It was lifted or released.</summary>
        Up,
        /// <summary>The system took the gesture away (a system gesture, another window): drop whatever the gesture was doing without acting on it.</summary>
        Cancel
    }

    /// <summary>
    /// One touch or pointer event, in LOGICAL coordinates (the 1920 x 1080 canvas PUi draws in, origin at its top-left corner; see ScreenMetrics): the platform turns a pixel position into
    /// them with IRenderHost.Screen.ToLogicalX / ToLogicalY, which also take the canvas's position on a letterboxed phone into account, so a touch in a side bar is negative or beyond 1920.
    /// Raised on the interface thread through IInputSource.Pointer. Multi-touch events carry the id of the finger; PUi follows the first finger of a gesture and ignores the others.
    /// </summary>
    public struct PointerEvent
    {
        public PointerAction Action;
        /// <summary>Finger id (0 for a mouse or the first finger); stays the same from Down to Up or Cancel.</summary>
        public int Id;
        public float X, Y;
        /// <summary>Monotonic seconds, the same clock as IFramePresenter.NeedsFrame's now (for drag velocity).</summary>
        public double Time;

        public PointerEvent(PointerAction action, float x, float y, double time = 0, int id = 0) { Action = action; X = x; Y = y; Time = time; Id = id; }
        public override string ToString() { return Action + " #" + Id + " (" + X + "," + Y + ")"; }
    }
}
