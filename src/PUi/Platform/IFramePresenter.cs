using SkiaSharp;

namespace PUi.Platform
{
    /// <summary>What the platform asks of PUi on every screen refresh.</summary>
    public interface IFramePresenter
    {
        /// <summary>Called on every screen tick (vsync) on the interface thread. now = monotonic seconds. Returns true if a redraw is needed right away.</summary>
        bool NeedsFrame(double now);
        /// <summary>
        /// Draws a frame. The canvas measures pixelWidth x pixelHeight; the design is 1920x1080 logical and PUi scales it, letterboxed inside the safe area when the surface is not the
        /// design's 16:9 shape (see ScreenMetrics), and draws the background on the whole surface. context is the GRContext when the
        /// surface is a GPU one (to create textures and off-screen surfaces on the GPU) and null on CPU surfaces.
        /// </summary>
        void Draw(SKCanvas canvas, GRContext context, double now, int pixelWidth, int pixelHeight);
    }
}
