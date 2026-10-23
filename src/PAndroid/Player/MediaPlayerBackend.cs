using System;
using System.Collections.Generic;
using Android.Content;
using Android.Media;
using AceEngine.App;

namespace PAndroid
{
    /// <summary>
    /// The alternative player, android.media.MediaPlayer, chosen with "platform": {"player": "mediaplayer"} in the configuration. It plays progressive HTTP (H.264 and AAC or MP2 in TS on most devices) and
    /// is the fallback when ExoPlayer misbehaves on a device. It shares everything else with ExoPlayerBackend (PlayerBackendBase). It is created on the player thread, whose looper receives its callbacks.
    /// </summary>
    internal sealed class MediaPlayerBackend : PlayerBackendBase
    {
        private const int MediaInfoVideoRenderingStart = 3, MediaInfoBufferingStart = 701, MediaInfoBufferingEnd = 702;
        private const int MediaErrorUnsupported = -1010, MediaErrorMalformed = -1007;

        private MediaPlayer _mp;
        private int _videoW, _videoH;
        private volatile bool _firstFrame;

        public MediaPlayerBackend(Context applicationContext, VideoFrame video, AndroidAudioFocus focus) : base(applicationContext, video, focus) { }

        protected override bool HasPlayer { get { return _mp != null; } }

        protected override void Open(string url, bool isLive)
        {
            var mp = new MediaPlayer();
            _mp = mp;
            mp.SetAudioAttributes(new Android.Media.AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Movie).Build());
            mp.SetDisplay(Video.Surface.Holder);
            mp.Error += (s, e) => Guard(() => OnError((int)e.What, e.Extra));
            mp.Completion += (s, e) => Guard(() => { if (IsStarted) RaiseCompleted(); else FailFromPlayer(new PlayerFailure("el flujo ha terminado antes de empezar a reproducir", false)); });
            mp.BufferingUpdate += (s, e) => Guard(() => { if (IsStarted && e.Percent < 100) SetBuffering(true, e.Percent); });
            mp.Info += (s, e) => Guard(() => OnInfo((int)e.What));
            mp.VideoSizeChanged += (s, e) => Guard(() => OnVideoSize(e.Width, e.Height));
            mp.SeekComplete += (s, e) => Guard(SeekDone);
            mp.Prepared += (s, e) => Guard(() => OnPrepared(mp));
            mp.SetDataSource(url);
            mp.PrepareAsync();
            UdpLog.Info(Tag, "MediaPlayer prepare " + url + " (live=" + isLive + ")");
        }

        private void OnPrepared(MediaPlayer mp)
        {
            if (IsDisposed || _mp != mp) return;
            _videoW = mp.VideoWidth; _videoH = mp.VideoHeight;
            if (_videoW > 0 && _videoH > 0) SetVideoAspect((float)_videoW / _videoH);
            mp.Start();
            // the first frame is reported by MEDIA_INFO_VIDEO_RENDERING_START; a stream without picture has none, so it counts as started a moment after start()
            PlayerThread.PostDelayed(() => { if (!IsDisposed && _mp == mp && !IsStarted && (_videoW <= 0 || _videoH <= 0 || _firstFrame)) StartSucceeded(); }, 1000);
        }

        private void OnInfo(int what)
        {
            if (IsDisposed) return;
            if (what == MediaInfoVideoRenderingStart) { _firstFrame = true; StartSucceeded(); }
            else if (what == MediaInfoBufferingStart) SetBuffering(true, 0);
            else if (what == MediaInfoBufferingEnd) SetBuffering(false, 100);
        }

        private void OnVideoSize(int w, int h)
        {
            _videoW = w; _videoH = h;
            if (w > 0 && h > 0) SetVideoAspect((float)w / h);
        }

        private void OnError(int what, int extra)
        {
            bool fatal = extra == MediaErrorUnsupported || extra == MediaErrorMalformed;
            UdpLog.Warn(Tag, "MediaPlayer error what=" + what + " extra=" + extra);
            FailFromPlayer(new PlayerFailure(fatal ? "Formato de audio/vídeo no soportado por este dispositivo (" + what + "/" + extra + ")" : "error del reproductor: " + what + "/" + extra, fatal));
        }

        protected override void ReleasePlayer()
        {
            var mp = _mp; _mp = null;
            if (mp == null) return;
            try { mp.SetOnErrorListener(null); mp.SetOnCompletionListener(null); mp.SetOnInfoListener(null); mp.SetOnPreparedListener(null); } catch (Exception) { }
            try { mp.Stop(); } catch (Exception) { }
            try { mp.Reset(); } catch (Exception) { }
            try { mp.Release(); } catch (Exception ex) { UdpLog.Warn(Tag, "Release: " + ex.Message); }
            try { mp.Dispose(); } catch (Exception) { }
        }

        protected override long ReadPosition() { var mp = _mp; try { return mp == null ? -1 : mp.CurrentPosition; } catch (Exception) { return -1; } }
        protected override long ReadDuration() { var mp = _mp; try { int d = mp == null ? -1 : mp.Duration; return d > 0 ? d : -1; } catch (Exception) { return -1; } }
        protected override void SetPlaying(bool play) { var mp = _mp; if (mp == null) return; try { if (play) mp.Start(); else mp.Pause(); } catch (Exception ex) { UdpLog.Warn(Tag, (play ? "Start" : "Pause") + ": " + ex.Message); } }
        protected override void SetVolume(float volume) { var mp = _mp; if (mp != null) { try { mp.SetVolume(volume, volume); } catch (Exception) { } } }
        protected override void StartSeek(long positionMs) { var mp = _mp; if (mp == null) { SeekDone(); return; } mp.SeekTo((int)Math.Min(positionMs, int.MaxValue)); }

        protected override string DescribeStreams()
        {
            return _videoW > 0 && _videoH > 0 ? "video " + _videoW + "x" + _videoH : "";
        }

        private static void Guard(Action a)
        {
            try { a(); } catch (Exception ex) { UdpLog.Error(Tag, "MediaPlayer callback: " + ex); }
        }
    }
}
