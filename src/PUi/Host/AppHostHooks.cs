using System;
using PUi.App;

namespace PUi.Host
{
    /// <summary>What a shell plugs into AppHost: work that has to happen at exact points of the common lifecycle, and the platform's parts of the statistics and of the key script.</summary>
    public sealed class AppHostHooks
    {
        /// <summary>Right BEFORE PApp.OnPause (interface thread). On the TV: suspend the decoder warmer, so the player that PApp closes cannot ask for it again with the app already hidden.</summary>
        public Action BeforePause;

        /// <summary>Right AFTER PApp.OnResume (interface thread). On the TV: let the decoder warmer start again.</summary>
        public Action AfterResume;

        /// <summary>Right BEFORE the app is torn down on exit, while the window still exists. On the TV: suspend the warmer, which uses the window as its display.</summary>
        public Action BeforeShutdown;

        /// <summary>After the PApp is configured from AppConfig and right before it starts (PApp.Start starts the render loop). On the TV: the GPU sync mode.</summary>
        public Action<PApp> BeforeStart;

        /// <summary>Counters of the platform's render path for the statistics line (the TV's Evas); null = none.</summary>
        public Func<string> RenderCounters;
        /// <summary>Name of the RenderCounters section ("evas" on the TV).</summary>
        public string RenderLabel = "render";
        /// <summary>State of the platform's window for the statistics line; null = none.</summary>
        public Func<string> WindowState;

        /// <summary>How the key script presses a key: the platform raises it through the same path as a real one (on the TV, TizenPlatform.Raise). Null = straight into PApp.OnKey.</summary>
        public Action<PUi.Input.KeyPress> InjectKey;

        /// <summary>The platform's own key script labels (measurement probes, captures, GPU sync): called on the interface thread for every label that is not app_pause / app_resume.</summary>
        public Func<string, bool> ScriptLabel;
    }
}
