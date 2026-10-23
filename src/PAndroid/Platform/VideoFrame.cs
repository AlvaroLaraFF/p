using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace PAndroid
{
    /// <summary>
    /// The video surface, always the bottom layer and always visible (a SurfaceView that is hidden loses its surface and with it the picture the player is drawing). Android scales a surface's content
    /// to fill it, stretching the picture, so the surface itself is sized to the video's shape and centred: letterbox, with black bars. The interface's TextureView above it is opaque (covering
    /// all of this) or transparent where the video shows through. PUi says where the video goes (IVideoPlacement: SetRect, in pixels of this frame): the picture is fitted inside that rectangle
    /// (the 16:9 area at the top of a phone's player, the whole screen in landscape); without one the video fills the frame.
    /// </summary>
    internal sealed class VideoFrame : ViewGroup
    {
        private float _aspect;
        private int _rectL, _rectT, _rectW, _rectH;               // the rectangle the video lives in, in pixels of this frame; width 0 = the whole frame

        public VideoFrame(Context context) : base(context)
        {
            Surface = new SurfaceView(context);
            AddView(Surface, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
            SetBackgroundColor(Color.Black);
        }

        public SurfaceView Surface { get; private set; }

        /// <summary>Width / height of the picture as it must be shown (pixel shape included); 0 or less = fill the rectangle. Main thread.</summary>
        public void SetAspect(float aspect)
        {
            if (Math.Abs(aspect - _aspect) < 0.001f) return;
            _aspect = aspect;
            RequestLayout();
        }

        /// <summary>The video belongs in this rectangle of the frame, in pixels. Main thread.</summary>
        public void SetRect(int left, int top, int width, int height)
        {
            if (width <= 0 || height <= 0) { ClearRect(); return; }
            if (left == _rectL && top == _rectT && width == _rectW && height == _rectH) return;
            _rectL = left; _rectT = top; _rectW = width; _rectH = height;
            RequestLayout();
        }

        /// <summary>The video fills the whole frame again. Main thread.</summary>
        public void ClearRect()
        {
            if (_rectW == 0 && _rectH == 0) return;
            _rectL = _rectT = _rectW = _rectH = 0;
            RequestLayout();
        }

        private void Fit(int frameW, int frameH, out int l, out int t, out int w, out int h)
        {
            if (_rectW > 0 && _rectH > 0) AspectFit.ComputeIn(_rectL, _rectT, _rectW, _rectH, _aspect, out l, out t, out w, out h);
            else AspectFit.Compute(frameW, frameH, _aspect, out l, out t, out w, out h);
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int w = MeasureSpec.GetSize(widthMeasureSpec), h = MeasureSpec.GetSize(heightMeasureSpec);
            SetMeasuredDimension(w, h);
            int l, t, fw, fh;
            Fit(w, h, out l, out t, out fw, out fh);
            Surface.Measure(MeasureSpec.MakeMeasureSpec(fw, MeasureSpecMode.Exactly), MeasureSpec.MakeMeasureSpec(fh, MeasureSpecMode.Exactly));
        }

        protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
        {
            int l, t, fw, fh;
            Fit(right - left, bottom - top, out l, out t, out fw, out fh);
            Surface.Layout(l, t, l + fw, t + fh);
        }
    }
}
