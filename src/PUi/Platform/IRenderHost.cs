using System;

namespace PUi.Platform
{
    /// <summary>Drawing surface and frame loop.</summary>
    public interface IRenderHost
    {
        int PixelWidth { get; }
        int PixelHeight { get; }

        /// <summary>
        /// The drawing surface as the interface needs to know it to lay out: size, aspect ratio, orientation and the areas the system covers (safe insets). A TV reports its fixed
        /// 1920x1080 with no insets; a phone or tablet can change it while the app runs (rotation, split screen).
        /// </summary>
        ScreenMetrics Screen { get; }

        /// <summary>The Screen changed (the surface was resized or rotated, the insets changed). Raised on the interface thread; never on a TV.</summary>
        event Action ScreenChanged;

        void SetPresenter(IFramePresenter presenter);
        /// <summary>Starts the loop: on every vsync it calls NeedsFrame and, if true, Draw.</summary>
        void Start();
        void Stop();
        /// <summary>Requests a frame right away (state change from another thread); thread-safe.</summary>
        void RequestRedraw();
    }
}
