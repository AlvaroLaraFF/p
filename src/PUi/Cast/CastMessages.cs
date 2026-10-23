using System;
using System.Collections.Generic;
using AceEngine.Cast;
using PUi.Platform;

namespace PUi.Cast
{
    /// <summary>
    /// The sentences of "Enviar a la tele" that come from the engine's verdicts and reasons, in the Spanish the interface shows. Pure functions: the engine's own messages are technical or
    /// terse ("audio MP2"); what the user reads is one short sentence about the device and the channel.
    /// </summary>
    public static class CastMessages
    {
        public const string NotSendable = "Este canal no se puede enviar a otro aparato";
        public const string DeviceGone = "El aparato ya no está disponible";
        public const string NoPause = "Esta tele no permite pausar";

        /// <summary>"El Chromecast no reproduce el audio de este canal": why a device cannot play a channel, from the blocking issues of the verdict.</summary>
        public static string Incompatible(CastVerdict verdict, CastDeviceKind kind)
        {
            string subject = kind == CastDeviceKind.Chromecast ? "El Chromecast" : "La tele";
            if (verdict == null) return subject + " no puede reproducir este canal";
            bool audio = false, video = false, quality = false, scrambled = false, noVideo = false, other = false;
            foreach (var i in verdict.Issues)
            {
                if (!i.Blocking) continue;
                switch (i.Code)
                {
                    case CastIssueCode.AudioMp2: case CastIssueCode.AudioAc3: case CastIssueCode.AudioLatm: case CastIssueCode.AudioDts: case CastIssueCode.AudioUnsupported: case CastIssueCode.AudioMismatch:
                        audio = true; break;
                    case CastIssueCode.VideoMpeg2: case CastIssueCode.VideoHevc: case CastIssueCode.VideoUnsupported: case CastIssueCode.H264Profile: case CastIssueCode.H264Format:
                        video = true; break;
                    case CastIssueCode.H264Resolution: case CastIssueCode.H264Level:
                        quality = true; break;
                    case CastIssueCode.Scrambled: scrambled = true; break;
                    case CastIssueCode.NoVideo: noVideo = true; break;
                    default: other = true; break;
                }
            }
            if (scrambled) return "Este canal está cifrado";
            if (noVideo) return "Este canal no tiene vídeo";
            if (audio && (video || quality)) return subject + " no reproduce ni el audio ni el vídeo de este canal";
            if (audio) return subject + " no reproduce el audio de este canal";
            if (video) return subject + " no reproduce el vídeo de este canal";
            if (quality) return subject + " no puede con la calidad de este canal";
            if (other) return "No se puede enviar este canal a " + (kind == CastDeviceKind.Chromecast ? "un Chromecast" : "la tele");
            return subject + " no puede reproducir este canal";
        }

        /// <summary>The notice for a sending that ended by itself, from the engine's (English, technical) reason.</summary>
        public static string Ended(string engineReason)
        {
            string r = (engineReason ?? "").ToLowerInvariant();
            if (r.Contains("tv")) return "El envío ha terminado: se ha parado desde la tele";
            if (r.Contains("idle")) return "El envío ha terminado: el aparato ha dejado de pedir el canal";
            if (r.Contains("key frames")) return "El envío ha terminado: el canal no tiene fotogramas clave";
            if (r.Contains("channel")) return "El canal ha dejado de emitir";
            return "El envío ha terminado";
        }

        /// <summary>The notice for a send that could not start. A refused channel gets the sentence about the device; anything else, the engine's own Spanish reason.</summary>
        public static string StartFailed(CastResult result, CastDeviceKind kind)
        {
            if (result == null) return "No se ha podido enviar el canal";
            if (result.Failure == CastFailure.Incompatible && result.Verdict != null) return Incompatible(result.Verdict, kind);
            return string.IsNullOrEmpty(result.Reason) ? "No se ha podido enviar el canal" : result.Reason;
        }
    }
}
