using System;
using AceEngine.App;
using PUi.Input;

namespace PUi.Core
{
    public enum PlayerMode { Loading, Playing, Paused, Buffering, Error }

    public enum PlayerAction { None, Exit, TogglePause, SeekBack, SeekForward, GoLive, NextChannel, PrevChannel, Retry }

    /// <summary>Buttons of the controls panel, from left to right.</summary>
    public enum PanelButton { Back10 = 0, PlayPause = 1, Forward10 = 2, Live = 3 }

    /// <summary>
    /// Player state machine (no drawing, tested separately): derives the mode from what the playback reports (loading, playing, paused, buffer
    /// refill, error), decides when the panel is shown (hidden after 5 s, it stays on pause and on error) and translates remote keys into actions.
    /// Outside the panel, left and right jump 10 s; with the panel visible they move the focus between its buttons.
    /// </summary>
    public sealed class PlayerController
    {
        public const double PanelSeconds = 5.0;
        public const long MinWindowMs = 1000;

        public PlaybackStatus Status = new PlaybackStatus();
        /// <summary>The image has been seen at some point: from then on the window is transparent and the waits are shown as a buffer ring over the video.</summary>
        public bool Started { get; private set; }
        public PlayerMode Mode { get; private set; }
        public bool PanelWanted { get; private set; }
        public double PanelUntil;
        public PanelButton Focus = PanelButton.PlayPause;
        public bool CanChangeChannel;

        private double _behindBaseMs, _behindBaseAt;
        private bool _hasBehind;
        private float _loadShown;
        private int _bufShown;
        private PlayerMode _bufMode = PlayerMode.Loading;

        public PlayerController() { Mode = PlayerMode.Loading; }

        // ---------------------------------------------------------------- state
        public void OnStatus(PlaybackStatus st, double now)
        {
            Status = st;
            _behindBaseMs = st.BehindLiveMs; _behindBaseAt = now; _hasBehind = true;
            _loadShown = Math.Max(_loadShown, LoadProgress);                  // the loading bar only advances: if the real progress goes back, what was shown is kept
            switch (st.Phase)
            {
                case PlayPhase.Playing:
                    Started = true;
                    Mode = st.PlayerBufferPercent >= 0 && st.PlayerBufferPercent < 100 ? PlayerMode.Buffering : PlayerMode.Playing;
                    break;
                case PlayPhase.Paused: Started = true; Mode = PlayerMode.Paused; break;
                case PlayPhase.Error: Mode = PlayerMode.Error; break;
                case PlayPhase.Idle: case PlayPhase.Ended: break;
                default: Mode = Started ? PlayerMode.Buffering : PlayerMode.Loading; break;       // Resolving, Connecting, Buffering, Preparing, Retrying
            }
            // the % of the buffer ring also only advances within a single buffer episode (on leaving Buffering it starts over)
            if (Mode != PlayerMode.Buffering) _bufShown = 0;
            else _bufShown = Math.Max(_bufShown, BufferPercent);
            _bufMode = Mode;
        }

        public void Reset()
        {
            Status = new PlaybackStatus(); Started = false; Mode = PlayerMode.Loading; PanelWanted = false; PanelUntil = 0; Focus = PanelButton.PlayPause; _hasBehind = false;
            _loadShown = 0; _bufShown = 0; _bufMode = PlayerMode.Loading;
        }

        /// <summary>
        /// true if the window must be transparent (there is video behind). Also during "Preparing": the player is created then and the window has to be
        /// transparent already (changing the alpha with the video running, or with the screen already assigned, makes the system interrupt it: PlaybackInterrupted ResourceConflict).
        /// Meanwhile the interface keeps drawing the loading lamp with opaque pixels, so the TV source is not visible; only when there is a first frame does the session switch to
        /// "Playing" and the interface lets the video show.
        /// </summary>
        public bool WindowTransparent
        {
            get
            {
                if (Mode == PlayerMode.Error) return false;
                if (Status.Phase == PlayPhase.Preparing) return true;
                return Started && Mode != PlayerMode.Loading;
            }
        }

        /// <summary>Advances time: decides whether the panel is requested.</summary>
        public void Tick(double now)
        {
            PanelWanted = Mode == PlayerMode.Paused || Mode == PlayerMode.Error || (Mode != PlayerMode.Loading && now < PanelUntil);
        }

        public void ShowPanel(double now) { PanelUntil = now + PanelSeconds; PanelWanted = true; }

        /// <summary>A tap on the picture: shows the panel or hides it, as the Info key does (on pause and on error it stays).</summary>
        public void TogglePanel(double now)
        {
            if (Mode == PlayerMode.Loading) return;
            bool panelShown = PanelWanted && Mode != PlayerMode.Loading;
            if (panelShown && Mode != PlayerMode.Paused) { PanelUntil = 0; PanelWanted = false; } else ShowPanel(now);
        }

        /// <summary>A tap on a button of the panel: the action OK would do on it with the panel up. The panel stays up for another PanelSeconds.</summary>
        public PlayerAction OnTouchButton(PanelButton button, double now)
        {
            if (Mode == PlayerMode.Loading || Mode == PlayerMode.Error) return PlayerAction.None;
            Focus = button;
            ShowPanel(now);
            switch (button)
            {
                case PanelButton.Back10: return PlayerAction.SeekBack;
                case PanelButton.Forward10: return PlayerAction.SeekForward;
                case PanelButton.Live: return PlayerAction.GoLive;
                default: return PlayerAction.TogglePause;
            }
        }

        // ---------------------------------------------------------------- timeline
        /// <summary>What is stored behind the live edge, in ms (0 if there is no memory).</summary>
        public long WindowMs { get { return Math.Max(0, Status.TimeshiftWindowMs); } }

        /// <summary>Distance to the live edge in ms: on pause it keeps growing (the live stream advances), capped to the window.</summary>
        public long BehindMs(double now)
        {
            if (!_hasBehind) return 0;
            double b = _behindBaseMs;
            if (Mode == PlayerMode.Paused) b += (now - _behindBaseAt) * 1000.0;
            long max = WindowMs;
            return (long)Math.Max(0, max > 0 ? Math.Min(b, max) : b);
        }

        public bool AtLive(double now) { return Mode != PlayerMode.Paused && BehindMs(now) < 2500; }

        /// <summary>Position of the playhead on the timeline: 0 = oldest stored, 1 = live.</summary>
        public float Fraction(double now)
        {
            long w = WindowMs;
            if (w < MinWindowMs) return 1f;
            return (float)Math.Max(0.0, Math.Min(1.0, 1.0 - (double)BehindMs(now) / w));
        }

        // ---------------------------------------------------------------- keys
        /// <summary>Translates a key into an action. Manages the panel's focus and visibility by itself.</summary>
        public PlayerAction OnKey(UiKey key, double now)
        {
            bool panelShown = PanelWanted && Mode != PlayerMode.Loading;
            switch (key)
            {
                case UiKey.Back: return PlayerAction.Exit;
                case UiKey.ChannelUp: if (CanChangeChannel) { ShowPanel(now); return PlayerAction.NextChannel; } return PlayerAction.None;
                case UiKey.ChannelDown: if (CanChangeChannel) { ShowPanel(now); return PlayerAction.PrevChannel; } return PlayerAction.None;
                case UiKey.PlayPause: case UiKey.Play: case UiKey.Pause:
                    if (Mode == PlayerMode.Loading || Mode == PlayerMode.Error) return PlayerAction.None;
                    if (key == UiKey.Play && Mode != PlayerMode.Paused) return PlayerAction.None;
                    if (key == UiKey.Pause && Mode == PlayerMode.Paused) return PlayerAction.None;
                    ShowPanel(now); return PlayerAction.TogglePause;
                case UiKey.Rewind: if (Mode == PlayerMode.Loading || Mode == PlayerMode.Error) return PlayerAction.None; ShowPanel(now); return PlayerAction.SeekBack;
                case UiKey.Forward: if (Mode == PlayerMode.Loading || Mode == PlayerMode.Error) return PlayerAction.None; ShowPanel(now); return PlayerAction.SeekForward;
                case UiKey.Info:
                    if (Mode == PlayerMode.Loading) return PlayerAction.None;
                    if (panelShown && Mode != PlayerMode.Paused) { PanelUntil = 0; PanelWanted = false; } else ShowPanel(now);
                    return PlayerAction.None;
            }
            if (Mode == PlayerMode.Error) return key == UiKey.Ok ? PlayerAction.Retry : PlayerAction.None;
            if (Mode == PlayerMode.Loading) return PlayerAction.None;

            if (!panelShown)
            {
                // panel hidden: left/right jump 10 s; up, down and OK show the panel
                switch (key)
                {
                    case UiKey.Left: ShowPanel(now); return PlayerAction.SeekBack;
                    case UiKey.Right: ShowPanel(now); return PlayerAction.SeekForward;
                    case UiKey.Up: case UiKey.Down: case UiKey.Ok: ShowPanel(now); Focus = PanelButton.PlayPause; return PlayerAction.None;
                }
                return PlayerAction.None;
            }
            ShowPanel(now);
            switch (key)
            {
                case UiKey.Left: Focus = Focus == PanelButton.Back10 ? PanelButton.Back10 : Focus == PanelButton.Live ? PanelButton.Forward10 : (PanelButton)((int)Focus - 1); return PlayerAction.None;
                case UiKey.Right: Focus = Focus == PanelButton.Live ? PanelButton.Live : (PanelButton)((int)Focus + 1); return PlayerAction.None;
                case UiKey.Up: case UiKey.Down: Focus = PanelButton.PlayPause; return PlayerAction.None;
                case UiKey.Ok:
                    switch (Focus)
                    {
                        case PanelButton.Back10: return PlayerAction.SeekBack;
                        case PanelButton.Forward10: return PlayerAction.SeekForward;
                        case PanelButton.Live: return PlayerAction.GoLive;
                        default: return PlayerAction.TogglePause;
                    }
            }
            return PlayerAction.None;
        }

        // ---------------------------------------------------------------- loading
        /// <summary>SHOWN progress of the loading bar (0..1): the real one, but never below what was already shown in this load (monotonic).</summary>
        public float LoadProgressShown { get { return Math.Max(_loadShown, LoadProgress); } }

        /// <summary>SHOWN percentage of the buffer ring: monotonic within a buffer episode.</summary>
        public int BufferPercentShown { get { return Mode == PlayerMode.Buffering ? Math.Max(_bufShown, BufferPercent) : BufferPercent; } }

        /// <summary>
        /// true if there is no measurable progress at the moment (resolving, connecting, retrying, or filling the prebuffer with no percentage yet): instead of a bar
        /// that stays still or goes back and forth, a sliding shimmer is shown.
        /// </summary>
        public bool LoadIndeterminate
        {
            get
            {
                var s = Status;
                switch (s.Phase)
                {
                    case PlayPhase.Resolving: case PlayPhase.Connecting: case PlayPhase.Retrying: return true;
                    case PlayPhase.Buffering: return s.BufferPercent <= 0;
                    default: return false;
                }
            }
        }

        /// <summary>Progress of the channel loading bar (0..1) according to the engine phase.</summary>
        public float LoadProgress
        {
            get
            {
                var s = Status;
                switch (s.Phase)
                {
                    case PlayPhase.Resolving: return 0.10f;
                    case PlayPhase.Connecting: return 0.25f;
                    case PlayPhase.Buffering: return 0.30f + 0.60f * Math.Max(0, Math.Min(100, s.BufferPercent)) / 100f;
                    case PlayPhase.Preparing: return 0.93f;
                    case PlayPhase.Retrying: return 0.10f;
                    case PlayPhase.Playing: case PlayPhase.Paused: return 1f;
                    default: return 0.05f;
                }
            }
        }

        /// <summary>Percentage of the buffer ring (the engine's while it fills its buffer, or the player's own).</summary>
        public int BufferPercent
        {
            get
            {
                var s = Status;
                if (s.Phase == PlayPhase.Playing && s.PlayerBufferPercent >= 0) return s.PlayerBufferPercent;
                return Math.Max(0, Math.Min(100, s.BufferPercent));
            }
        }
    }
}
