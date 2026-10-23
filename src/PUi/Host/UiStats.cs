using System;
using System.Globalization;
using PUi.App;

namespace PUi.Host
{
    /// <summary>
    /// The "ui{...}" statistics line that goes into the periodic [stats] log: frames per second, ticks by what was requested, cost of the logic and of the drawing,
    /// sprite cache and the current screen. Each call reports what happened since the previous one. The platform adds its own parts (render counters, window state).
    /// </summary>
    public sealed class UiStats
    {
        private readonly Func<PApp> _app;
        private readonly Func<string> _render, _window;
        private long _lastFrames; private double _lastDrawSum;
        private long _lastTicks, _lastTicksFull, _lastTicksAmb, _lastTicksIdle, _lastFramesFull, _lastFramesAmb; private double _lastUpd;
        private DateTime _lastStat = DateTime.UtcNow;

        /// <summary>Name of the render counters section ("evas" on the TV).</summary>
        public string RenderLabel = "render";

        /// <param name="app">The app (null until it is created: the line is then "ui{}").</param>
        /// <param name="renderCounters">Counters of the platform's render path (Evas on the TV); null = no such section.</param>
        /// <param name="windowState">State of the platform window; null = no such section.</param>
        public UiStats(Func<PApp> app, Func<string> renderCounters = null, Func<string> windowState = null)
        {
            _app = app; _render = renderCounters; _window = windowState;
        }

        public string Sample()
        {
            var app = _app();
            if (app == null) return "ui{}";
            var now = DateTime.UtcNow;
            double dt = (now - _lastStat).TotalSeconds; _lastStat = now;
            long frames = app.FramesDrawn; double sum = app.DrawMsSum;
            long tk = app.Ticks, tkF = app.TicksFull, tkA = app.TicksAmbient, tkI = app.TicksIdle, frF = app.FramesFull, frA = app.FramesAmbient; double upd = app.UpdateMsSum;
            long dTk = tk - _lastTicks, dF = tkF - _lastTicksFull, dA = tkA - _lastTicksAmb, dI = tkI - _lastTicksIdle, dFrF = frF - _lastFramesFull, dFrA = frA - _lastFramesAmb; double dUpd = upd - _lastUpd;
            _lastTicks = tk; _lastTicksFull = tkF; _lastTicksAmb = tkA; _lastTicksIdle = tkI; _lastFramesFull = frF; _lastFramesAmb = frA; _lastUpd = upd;
            long df = frames - _lastFrames; double ds = sum - _lastDrawSum;
            _lastFrames = frames; _lastDrawSum = sum;
            string tickInfo = "ticks{per_s=" + (dt > 0 ? dTk / dt : 0).ToString("0", CultureInfo.InvariantCulture) + " full=" + dF + " ambient=" + dA + " no_draw=" + dI + " frames_full=" + dFrF + " frames_ambient=" + dFrA + " logic_ms_s=" + (dt > 0 ? dUpd / dt : 0).ToString("0.0", CultureInfo.InvariantCulture)
                + " decor_speed=" + app.Context.Decor.Rate.ToString("0.00", CultureInfo.InvariantCulture) + " rest=" + (app.Context.Decor.Stopped ? "yes" : "no") + " reason=" + app.DemandReason + "}";
            string render = _render != null ? " " + RenderLabel + "{" + _render() + "}" : "";
            string window = _window != null ? " window=" + _window() : "";
            string s = "ui{fps=" + (dt > 0 ? df / dt : 0).ToString("0.0", CultureInfo.InvariantCulture) + " " + tickInfo + " draw_avg=" + (df > 0 ? ds / df : 0).ToString("0.0", CultureInfo.InvariantCulture)
                + "ms draw_max=" + app.DrawMsMax.ToString("0.0", CultureInfo.InvariantCulture) + "ms " + app.Context.Sprites.PhaseSummary() + render + " sprites=" + app.Context.Sprites.Count + "/" + (app.Context.Sprites.Bytes >> 20) + "MiB" + window + " " + app.Describe() + "}";
            app.ResetStatsMax();
            return s;
        }
    }
}
