using System;
using System.Threading.Tasks;
using AceEngine.App;
using PUi.App;
using PUi.Input;
using PUi.Platform;

namespace PUi.Host
{
    /// <summary>
    /// Runs a key script (AppConfig.KeyScript, see KeyScript) for unattended tests: it presses each key through the same path as the remote and logs the state after it.
    /// "log:label" steps log the state; the labels "app_pause" / "app_resume" (log:app_pause) call PApp.OnPause / OnResume (the PApp part of Home and of coming back,
    /// without the platform's own pause work); "snap_*" labels take a screen capture (ISnapshotSource); any other label goes to the platform's label hook (measurement probes...). Everything that touches the app runs on the interface thread.
    /// </summary>
    public sealed class KeyScriptRunner
    {
        private readonly PApp _app;
        private readonly IUiThread _ui;
        private readonly Action<KeyPress> _press;
        private readonly Func<bool> _exiting;
        private readonly Func<string, bool> _labelHook;
        private readonly ISnapshotSource _snapshot;
        private readonly Action<string, byte[]> _snapshotSink;

        /// <summary>Wait between steps; the tests replace it so a script does not take real time.</summary>
        public Func<int, Task> Delay = ms => Task.Delay(ms);
        /// <summary>Pause before the first step (the interface has to have drawn its first screen).</summary>
        public int StartDelayMs = 2500;
        /// <summary>Pause after each key before logging the state.</summary>
        public int KeyDelayMs = 300;

        /// <param name="press">Injects the key where the remote's keys arrive (on the interface thread).</param>
        /// <param name="exiting">true once the app is closing: the script stops.</param>
        /// <param name="labelHook">The platform's own labels ("probe_start", "snap_x"...); called on the interface thread after the label has been logged. May be null.</param>
        /// <param name="snapshot">The platform's screen captures (IPlatform.Snapshot); null = the "snap_*" labels go to the label hook like any other.</param>
        /// <param name="snapshotSink">Receives each capture ("snap_main.png", png bytes) on a pool thread; on the TV it uploads it to the development PC.</param>
        public KeyScriptRunner(PApp app, IUiThread ui, Action<KeyPress> press, Func<bool> exiting, Func<string, bool> labelHook = null, ISnapshotSource snapshot = null, Action<string, byte[]> snapshotSink = null)
        {
            _app = app; _ui = ui; _press = press; _exiting = exiting; _labelHook = labelHook; _snapshot = snapshot; _snapshotSink = snapshotSink;
        }

        public async Task RunAsync(string script)
        {
            var steps = KeyScript.Parse(script);
            UdpLog.Info("script", "script of " + steps.Count + " steps");
            await Delay(StartDelayMs);
            int n = 0;
            foreach (var st in steps)
            {
                if (_exiting()) return;
                if (st.WaitMs > 0) { await Delay(st.WaitMs); int ms = st.WaitMs; await _ui.RunAsync(() => UdpLog.Info("script", "after wait " + ms + " ms: " + _app.Describe())); continue; }
                if (st.Label != null)
                {
                    string label = st.Label;
                    await _ui.RunAsync(() =>
                    {
                        UdpLog.Info("script", "[" + label + "] " + _app.Describe());
                        if (label == "app_pause") _app.OnPause();          // simulates Home: same path as the system's OnPause
                        else if (label == "app_resume") _app.OnResume();   // and the return from the apps panel
                        else if (_snapshot != null && label.StartsWith("snap_", StringComparison.Ordinal)) Snap(label);
                        else if (_labelHook != null) _labelHook(label);
                    });
                    continue;
                }
                n++;
                var key = st.Key; int step = n;
                await _ui.RunAsync(() => { UdpLog.Info("script", "step " + step + ": " + key); _press(key); });
                await Delay(KeyDelayMs);
                await _ui.RunAsync(() => UdpLog.Info("script", "   -> " + _app.Describe()));
            }
            UdpLog.Info("script", "script finished");
        }

        /// <summary>"snap_name": after the next frame, captures the surface and hands name.png to the sink.</summary>
        private void Snap(string label)
        {
            var snap = _snapshot;
            snap.AfterNextFrame(() =>
            {
                var png = snap.SnapshotPng();
                if (png != null && _snapshotSink != null) { string name = label + ".png"; var sink = _snapshotSink; Task.Run(() => sink(name, png)); }
            });
        }
    }
}
