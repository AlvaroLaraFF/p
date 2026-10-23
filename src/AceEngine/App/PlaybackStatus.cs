using System;
using System.Globalization;

namespace AceEngine.App
{
    public enum PlayPhase
    {
        Idle,
        Resolving,
        Connecting,
        Buffering,
        Preparing,
        Playing,
        Paused,
        Retrying,
        Error,
        Ended
    }

    /// <summary>State for the UI and the log: what the engine and the player report along the way.</summary>
    public sealed class PlaybackStatus
    {
        public PlayPhase Phase;
        public string Name = "";
        public bool IsLive;
        public int Peers, Seeds;
        public double DownBytesPerSec, UpBytesPerSec;
        public int BufferPercent;
        public long PositionMs = -1, DurationMs = -1;
        /// <summary>Live: window stored behind live and current distance from live, in ms (0 = at live or without memory).</summary>
        public long TimeshiftWindowMs, BehindLiveMs;
        public string StreamInfo = "";
        public int Retry;
        public string Error;
        /// <summary>true if the error is not fixed by retrying (unsupported codec, invalid link...).</summary>
        public bool ErrorIsFatal;
        public int PlayerBufferPercent = -1;
        /// <summary>Non-fatal warning (e.g. the live stream has received no new pieces for a while).</summary>
        public string Warning;
        /// <summary>Discreet note about trust in the channel (unconfirmed, identity changed, name different from the list one).</summary>
        public string TrustNote;

        public PlaybackStatus Clone() { return (PlaybackStatus)MemberwiseClone(); }

        public bool IsBusy { get { return Phase == PlayPhase.Resolving || Phase == PlayPhase.Connecting || Phase == PlayPhase.Buffering || Phase == PlayPhase.Preparing || Phase == PlayPhase.Retrying; } }

        /// <summary>Short text for the loading screen: "Resolviendo…", "Conectando (N pares)…", "Cargando búfer X %".</summary>
        public string Message
        {
            get
            {
                switch (Phase)
                {
                    case PlayPhase.Resolving: return "Resolviendo…";
                    case PlayPhase.Connecting: return "Conectando (" + Peers + (Peers == 1 ? " par" : " pares") + ")…";
                    case PlayPhase.Buffering: return "Cargando búfer " + BufferPercent + " %  (" + Peers + (Peers == 1 ? " par" : " pares") + ")";
                    case PlayPhase.Preparing: return "Preparando el reproductor…";
                    case PlayPhase.Retrying: return "Reintentando (" + Retry + ")…";
                    case PlayPhase.Paused: return "Pausa";
                    case PlayPhase.Error: return Error ?? "Error";
                    case PlayPhase.Ended: return "Fin de la reproducción";
                    case PlayPhase.Playing: return PlayerBufferPercent >= 0 && PlayerBufferPercent < 100 ? "Cargando " + PlayerBufferPercent + " %" : "";
                    default: return "";
                }
            }
        }

        public static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSec / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
        }

        public static string FormatTime(long ms)
        {
            if (ms < 0) return "--:--";
            long s = ms / 1000;
            return s >= 3600 ? (s / 3600) + ":" + ((s / 60) % 60).ToString("00") + ":" + (s % 60).ToString("00") : (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        public override string ToString()
        {
            return Phase + " peers=" + Peers + " down=" + FormatSpeed(DownBytesPerSec) + " up=" + FormatSpeed(UpBytesPerSec) + " buffer=" + BufferPercent + "% pos=" + FormatTime(PositionMs)
                + (Retry > 0 ? " retry=" + Retry : "") + (Error != null ? " error=" + Error : "");
        }
    }
}
