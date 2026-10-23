using System;
using System.Collections.Generic;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>The pure arithmetic between the Android view (pixels of the view), the drawing surface (pixels of the buffer we draw on) and the logical 1920-wide space PUi works in.</summary>
    public static class ScreenMath
    {
        /// <summary>
        /// Size of the drawing buffer for a view of viewW x viewH: the view's own size, shrunk (keeping its shape) when its longest side is over <paramref name="maxLongSide"/>
        /// (0 = no limit). It is never larger than the view: the compositor scales the buffer up to the view, so a smaller buffer is how a weak device draws less.
        /// </summary>
        public static void RenderSize(int viewW, int viewH, int maxLongSide, out int w, out int h)
        {
            w = Math.Max(1, viewW); h = Math.Max(1, viewH);
            int longSide = Math.Max(w, h);
            if (maxLongSide <= 0 || longSide <= maxLongSide) return;
            double s = (double)maxLongSide / longSide;
            w = Math.Max(1, (int)Math.Round(w * s));
            h = Math.Max(1, (int)Math.Round(h * s));
        }

        /// <summary>
        /// The ScreenMetrics of a view whose content is drawn on a bufferW x bufferH buffer. The insets come in view pixels and the density in view pixels per dp: both go to buffer pixels, which
        /// are the pixels the interface counts in. <paramref name="touch"/> is PlatformCapabilities.HasTouch: with it the interface lays itself out as a phone or tablet (from the size in dp),
        /// without it as a TV, whatever the size. Before the first layout (no view size) it answers with the 1920x1080 design size.
        /// </summary>
        public static ScreenMetrics Metrics(int viewW, int viewH, int bufferW, int bufferH, ScreenInsets viewInsets, float density, bool touch = false)
        {
            if (viewW <= 0 || viewH <= 0 || bufferW <= 0 || bufferH <= 0) return new ScreenMetrics(ScreenMetrics.DesignWidth, ScreenMetrics.DesignHeight, ScreenInsets.None, density, touch);
            double k = (double)bufferW / viewW;
            var ins = new ScreenInsets((int)Math.Round(viewInsets.Left * k), (int)Math.Round(viewInsets.Top * k), (int)Math.Round(viewInsets.Right * k), (int)Math.Round(viewInsets.Bottom * k));
            return new ScreenMetrics(bufferW, bufferH, ins, (float)(density * k), touch);
        }

        /// <summary>Pixels of the drawing surface per pixel of the view along one axis (1 when either is unknown): a touch position in view pixels times this is a position on the surface.</summary>
        public static float SurfaceScale(int viewPixels, int surfacePixels) { return viewPixels <= 0 || surfacePixels <= 0 ? 1f : surfacePixels / (float)viewPixels; }

        /// <summary>
        /// A rectangle of the drawing surface (pixels of the buffer: what IVideoPlacement.SetVideoRect gives) in pixels of the VIEW, which is where the video view is laid out: the interface
        /// draws on a buffer that may be smaller than the view.
        /// </summary>
        public static void SurfaceRectToView(PixelRect rect, int viewW, int viewH, int surfaceW, int surfaceH, out int left, out int top, out int width, out int height)
        {
            float kx = viewW <= 0 || surfaceW <= 0 ? 1f : viewW / (float)surfaceW, ky = viewH <= 0 || surfaceH <= 0 ? 1f : viewH / (float)surfaceH;
            left = (int)Math.Round(rect.Left * kx); top = (int)Math.Round(rect.Top * ky);
            width = Math.Max(0, (int)Math.Round(rect.Right * kx) - left); height = Math.Max(0, (int)Math.Round(rect.Bottom * ky) - top);
        }
    }

    /// <summary>One finger of a touch event, in view pixels.</summary>
    public struct TouchPoint
    {
        public int Id;
        public float X, Y;
        public TouchPoint(int id, float x, float y) { Id = id; X = x; Y = y; }
    }

    /// <summary>Android MotionEvent actions to PointerEvents in logical coordinates (the contract of IInputSource.Pointer), without the Android API.</summary>
    public static class TouchTranslator
    {
        // android.view.MotionEvent
        public const int ActionMask = 0xff, ActionPointerIndexMask = 0xff00, ActionPointerIndexShift = 8;
        public const int ActionDown = 0, ActionUp = 1, ActionMove = 2, ActionCancel = 3, ActionPointerDown = 5, ActionPointerUp = 6;

        /// <summary>
        /// Appends the PointerEvents of one MotionEvent to <paramref name="output"/>. <paramref name="action"/> is MotionEvent.Action (with the pointer index in its high bits),
        /// <paramref name="points"/> all the fingers currently down (view pixels) and <paramref name="time"/> the event time in monotonic seconds. The position goes to logical coordinates with
        /// the screen's own conversion (origin at the canvas corner): view pixels times <paramref name="surfaceX"/> / <paramref name="surfaceY"/> (ScreenMath.SurfaceScale) are pixels of the
        /// drawing surface, which ScreenMetrics.ToLogicalX / ToLogicalY turn into logical units. Down and Up concern the finger that changed; Move concerns every finger; Cancel ends them all.
        /// Hover, scroll and the other actions give nothing.
        /// </summary>
        public static void Translate(int action, IReadOnlyList<TouchPoint> points, double time, ScreenMetrics screen, float surfaceX, float surfaceY, List<PointerEvent> output)
        {
            int a = action & ActionMask;
            int index = (action & ActionPointerIndexMask) >> ActionPointerIndexShift;
            switch (a)
            {
                case ActionDown:
                case ActionPointerDown:
                    if (index >= 0 && index < points.Count) output.Add(Make(PointerAction.Down, points[index], time, screen, surfaceX, surfaceY));
                    break;
                case ActionUp:
                case ActionPointerUp:
                    if (index >= 0 && index < points.Count) output.Add(Make(PointerAction.Up, points[index], time, screen, surfaceX, surfaceY));
                    break;
                case ActionMove:
                    for (int i = 0; i < points.Count; i++) output.Add(Make(PointerAction.Move, points[i], time, screen, surfaceX, surfaceY));
                    break;
                case ActionCancel:
                    for (int i = 0; i < points.Count; i++) output.Add(Make(PointerAction.Cancel, points[i], time, screen, surfaceX, surfaceY));
                    break;
            }
        }

        private static PointerEvent Make(PointerAction action, TouchPoint p, double time, ScreenMetrics screen, float sx, float sy)
        {
            return new PointerEvent(action, screen.ToLogicalX(p.X * sx), screen.ToLogicalY(p.Y * sy), time, p.Id);
        }
    }

    /// <summary>Where the video goes inside its container so that it keeps its shape (letterbox): Android's scaling of a surface stretches the picture, so the surface itself is sized.</summary>
    public static class AspectFit
    {
        /// <summary>
        /// The rectangle of the video inside a containerW x containerH container. <paramref name="aspect"/> is width/height of the picture (pixel shape included); 0 or less = unknown, fill the container.
        /// </summary>
        public static void Compute(int containerW, int containerH, float aspect, out int left, out int top, out int width, out int height)
        {
            ComputeIn(0, 0, containerW, containerH, aspect, out left, out top, out width, out height);
        }

        /// <summary>The same inside a rectangle that does not start at the corner (the video rectangle the interface leaves transparent).</summary>
        public static void ComputeIn(int rectLeft, int rectTop, int containerW, int containerH, float aspect, out int left, out int top, out int width, out int height)
        {
            left = rectLeft; top = rectTop; width = Math.Max(0, containerW); height = Math.Max(0, containerH);
            if (aspect <= 0 || containerW <= 0 || containerH <= 0) return;
            float containerAspect = (float)containerW / containerH;
            if (aspect > containerAspect) { width = containerW; height = Math.Max(1, (int)Math.Round(containerW / aspect)); }
            else { height = containerH; width = Math.Max(1, (int)Math.Round(containerH * aspect)); }
            left = (containerW - width) / 2; top = (containerH - height) / 2;
            left += rectLeft; top += rectTop;
        }
    }
}
