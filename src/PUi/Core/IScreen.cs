using System;
using System.Collections.Generic;
using PUi.Platform;
using PUi.Input;
using SkiaSharp;

namespace PUi.Core
{
    /// <summary>
    /// How much needs redrawing: nothing; only the repeating decoration (the focus's fluid border, strip, pulse: low rate that slows down at rest until it stops, see DecorClock);
    /// loading indicators that do not slow down (spinner, skeleton, lamp: constant low rate); or every screen refresh (focus, scrolling, entrances).
    /// </summary>
    public enum FrameDemand { Idle = 0, Decor = 1, Ambient = 2, Full = 3 }

    /// <summary>A screen of the interface. Everything is called from the interface thread.</summary>
    public interface IScreen
    {
        string Name { get; }
        /// <summary>true = opaque window (all screens except the player with video); false = transparent (the video shows through from behind).</summary>
        bool WindowOpaque { get; }
        void Enter(UiContext ctx);
        void Leave();
        /// <summary>The app goes to the background / comes back.</summary>
        void Suspend();
        void Resume();
        void OnKey(KeyPress key);
        /// <summary>A finger event in logical coordinates (see PointerEvent), only on platforms with touch. Taps, drags and flicks are worked out here, from Down to Up or Cancel.</summary>
        void OnPointer(PointerEvent e);
        /// <summary>
        /// The places that react to a finger right now, in screen (logical) coordinates, as the next Down would find them: for the tests that check their size and for the test bench's
        /// touch-zone view. Empty on a platform without touch.
        /// </summary>
        IReadOnlyList<TouchTarget> TouchTargets();
        /// <summary>Advances the logic and the animations up to 'now'; returns how much needs drawing.</summary>
        FrameDemand Update(double now);
        void Draw(SKCanvas canvas, double now);
        /// <summary>One-line summary (for the on-TV test log).</summary>
        string Describe();
    }
}
