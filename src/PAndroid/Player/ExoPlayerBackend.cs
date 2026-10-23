using System;
using System.Collections.Generic;
using Android.Content;
using Android.OS;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.TS;
using AceEngine.App;
using PUi.Screens;

namespace PAndroid
{
    internal sealed class PlayerFactory : IPlayerFactory
    {
        private readonly Context _app;
        private readonly VideoFrame _video;
        private readonly AndroidAudioFocus _focus;
        private readonly AndroidDeviceProfile _profile;
        private readonly AndroidOptions _options;

        public PlayerFactory(Context applicationContext, VideoFrame video, AndroidAudioFocus focus, AndroidDeviceProfile profile, AndroidOptions options)
        {
            _app = applicationContext; _video = video; _focus = focus; _profile = profile; _options = options;
        }

        public IPlayerBackend Create()
        {
            return _options.Player == PlayerKind.MediaPlayer ? (IPlayerBackend)new MediaPlayerBackend(_app, _video, _focus) : new ExoPlayerBackend(_app, _video, _focus, _profile, _options);
        }
    }

    /// <summary>
    /// The player: Media3 / ExoPlayer on the engine's local HTTP URL (an MPEG-TS stream for live, anything the engine serves for video on demand), drawing into the video SurfaceView below the interface.
    ///  - HTTP read timeout of 30 s (the engine can take a while to produce the next bytes of a live stream); the data source only talks to 127.0.0.1.
    ///  - A small load control (2 to 10 s of buffer, and a size cap): the engine already buffers, and what ExoPlayer holds is delay behind live and memory.
    ///  - The TS extractor accepts streams that start on a non-IDR key frame and detects access units without delimiters, which is what broadcast streams are.
    ///  - The start completes at onRenderedFirstFrame (or, for a stream without picture, shortly after it is ready).
    ///  - Errors are classified by their Media3 code (ExoErrors): unsupported media is fatal, everything else is worth a retry.
    ///  - Audio focus is handled by the app (handleAudioFocus = false), see PlayerBackendBase.
    ///  - What the device cannot decode is said, not hidden: a track with no decoder does not make ExoPlayer fail, it plays the rest, so the shell shows a notice over the picture ("Sin sonido: este
    ///    dispositivo no puede decodificar el audio MP2 de este canal") and the log lists the tracks. MP2 audio, which a stick may not decode (Amazon does not list it), is decoded by the app
    ///    itself when the device has no decoder for it (Mp2ExtractorsFactory); a decoder that fails at run time is replaced by that, or the audio is dropped and said so, without stopping the channel.
    ///  - A decoder that cannot start is followed by the next one that can (decoder fallback).
    /// </summary>
    internal sealed class ExoPlayerBackend : PlayerBackendBase
    {
        private const int ReadTimeoutMs = 30000, ConnectTimeoutMs = 8000;
        private const int MinBufferMs = 2000, MaxBufferMs = 10000, BufferForPlaybackMs = 1000, BufferForPlaybackAfterRebufferMs = 2000;
        private const int StateBuffering = 2, StateReady = 3, StateEnded = 4;
        private const int DiscontinuityReasonSeek = 1;

        private readonly AndroidDeviceProfile _profile;
        private readonly AndroidOptions _options;
        private IExoPlayer _player;
        private Listener _listener;
        private bool _firstFrame, _noPictureTimerSet, _softwareMp2, _audioDropped;

        /// <summary>The device's own MP2 decoder failed on this run: from then on, in Auto mode, the app decodes MP2 itself (set by the first player that finds out; the next one reads it).</summary>
        private static volatile bool DeviceMp2DecoderFailed;

        public ExoPlayerBackend(Context applicationContext, VideoFrame video, AndroidAudioFocus focus, AndroidDeviceProfile profile, AndroidOptions options) : base(applicationContext, video, focus)
        {
            _profile = profile; _options = options ?? new AndroidOptions();
        }

        protected override bool HasPlayer { get { return _player != null; } }

        protected override void Open(string url, bool isLive)
        {
            long targetBytes = (_profile != null && _profile.Memory == MemoryTier.Low ? 16L : 32L) * 1024 * 1024;
            var load = new DefaultLoadControl.Builder()
                .SetBufferDurationsMs(MinBufferMs, MaxBufferMs, BufferForPlaybackMs, BufferForPlaybackAfterRebufferMs)
                .SetTargetBufferBytes((int)targetBytes)
                .SetPrioritizeTimeOverSizeThresholds(false)
                .SetBackBuffer(0, false)
                .Build();
            var http = new DefaultHttpDataSource.Factory()
                .SetUserAgent("P/" + AppInfo.Version)
                .SetReadTimeoutMs(ReadTimeoutMs)
                .SetConnectTimeoutMs(ConnectTimeoutMs)
                .SetAllowCrossProtocolRedirects(false);
            IExtractorsFactory extractors = new DefaultExtractorsFactory()
                .SetTsExtractorFlags(DefaultTsPayloadReaderFactory.FlagAllowNonIdrKeyframes | DefaultTsPayloadReaderFactory.FlagDetectAccessUnits);
            // MP2 audio on a device that cannot decode it: the app turns the frames into PCM between the extractor and the player (see Mp2ExtractorsFactory)
            _softwareMp2 = AudioFallbackPolicy.UseSoftwareMp2(_options.SoftwareAudio, AndroidCodecs.HasMp2Decoder, DeviceMp2DecoderFailed);
            if (_softwareMp2) extractors = new Mp2ExtractorsFactory(extractors, m => UdpLog.Info(Tag, m));
            var sources = new ProgressiveMediaSource.Factory(http, extractors);
            var renderers = new DefaultRenderersFactory(AppContext).SetEnableDecoderFallback(true);
            var audio = new AndroidX.Media3.Common.AudioAttributes.Builder().SetUsage(C.UsageMedia).SetContentType(C.AudioContentTypeMovie).Build();

            var player = new ExoPlayerBuilder(AppContext)
                .SetRenderersFactory(renderers)
                .SetLooper(Looper.MyLooper())
                .SetLoadControl(load)
                .SetMediaSourceFactory(sources)
                .SetAudioAttributes(audio, false)                   // the app handles the audio focus (AndroidAudioFocus)
                .SetHandleAudioBecomingNoisy(false)
                .SetVideoScalingMode(C.VideoScalingModeScaleToFit)  // the video surface is sized to the picture's shape (VideoFrame)
                .Build();
            _player = player;
            _listener = new Listener(this);
            player.AddListener(_listener);
            player.SetVideoSurfaceView(Video.Surface);
            player.SetMediaItem(MediaItem.FromUri(url));
            player.PlayWhenReady = true;
            player.Prepare();
            UdpLog.Info(Tag, "ExoPlayer prepare " + url + " (live=" + isLive + ", buffer " + MinBufferMs + "-" + MaxBufferMs + " ms, MP2 " + (_softwareMp2 ? "decoded by the app" : "left to the device") + ")");
        }

        protected override void ReleasePlayer()
        {
            var p = _player; _player = null;
            var l = _listener; _listener = null;
            if (p == null) return;
            try { if (l != null) p.RemoveListener(l); } catch (Exception) { }
            try { p.Stop(); } catch (Exception ex) { UdpLog.Warn(Tag, "Stop: " + ex.Message); }
            try { p.ClearVideoSurfaceView(Video.Surface); } catch (Exception) { }
            try { p.Release(); } catch (Exception ex) { UdpLog.Warn(Tag, "Release: " + ex.Message); }
            try { p.Dispose(); } catch (Exception) { }
            if (l != null) { try { l.Dispose(); } catch (Exception) { } }
        }

        protected override long ReadPosition() { var p = _player; return p == null ? -1 : p.CurrentPosition; }

        protected override long ReadDuration()
        {
            var p = _player;
            if (p == null) return -1;
            long d = p.Duration;
            return d == C.TimeUnset || d < 0 ? -1 : d;
        }

        protected override void SetPlaying(bool play) { var p = _player; if (p != null) p.PlayWhenReady = play; }
        protected override void SetVolume(float volume) { var p = _player; if (p != null) p.Volume = volume; }
        protected override void StartSeek(long positionMs) { var p = _player; if (p != null) p.SeekTo(positionMs); else SeekDone(); }

        protected override string DescribeStreams()
        {
            var p = _player;
            if (p == null) return "";
            var v = p.VideoFormat; var a = p.AudioFormat;
            string text = StreamInfoText.Build(v != null ? v.SampleMimeType : null, v != null ? v.Width : 0, v != null ? v.Height : 0, v != null ? v.FrameRate : 0, v != null ? v.Bitrate : 0,
                a != null ? a.SampleMimeType : null, a != null ? a.SampleRate : 0, a != null ? a.ChannelCount : 0, a != null ? a.Bitrate : 0);
            // raw PCM here is MP2 that the app decoded; an audio track that exists but cannot play is named in the notices
            if (_softwareMp2 && a != null && string.Equals(a.SampleMimeType, MimeTypes.AudioRaw, StringComparison.OrdinalIgnoreCase)) text += " (MP2 decoded by the app)";
            if (_audioDropped) text += " [audio dropped: its decoder failed]";
            if (_trackSummary.Length > 0) text += " | tracks: " + _trackSummary;
            return text;
        }

        private string _trackSummary = "";

        // ================================================================ Media3 events (player thread)
        private void OnState(int state)
        {
            if (IsDisposed || _player == null) return;
            switch (state)
            {
                case StateBuffering:
                    SetBuffering(true, _player.BufferedPercentage);
                    break;
                case StateReady:
                    SetBuffering(false, 100);
                    SeekDone();
                    if (!IsStarted && !_firstFrame && !_noPictureTimerSet)
                    {
                        // a stream without a picture never renders a first frame: when it is ready and no video shows up within a moment, it has started
                        _noPictureTimerSet = true;
                        PlayerThread.PostDelayed(() =>
                        {
                            var p = _player;
                            if (IsDisposed || p == null || IsStarted) return;
                            var vs = p.VideoSize;
                            if (vs == null || vs.Width <= 0) { UdpLog.Info(Tag, "no picture in this stream: started on audio"); StartSucceeded(); }
                        }, 1500);
                    }
                    break;
                case StateEnded:
                    if (!IsStarted) FailFromPlayer(new PlayerFailure("el flujo ha terminado antes de empezar a reproducir", false));
                    else { UdpLog.Info(Tag, "playback ended"); RaiseCompleted(); }
                    break;
            }
        }

        private void OnError(PlaybackException e)
        {
            if (IsDisposed) return;
            int code = e.ErrorCode;
            bool fatal = ExoErrors.IsFatal(code);
            string name = e.ErrorCodeName ?? ("code " + code);
            UdpLog.Warn(Tag, "ExoPlayer error " + name + " (" + code + "): " + e.Message + (e.Cause != null ? " / cause: " + e.Cause.Message : ""));
            if (RecoverFromAudioDecoder(e, code, name)) return;
            string msg = fatal ? "Formato de audio/vídeo no soportado por este dispositivo (" + name + ")" : "error del reproductor: " + name;
            FailFromPlayer(new PlayerFailure(msg, fatal));
        }

        /// <summary>
        /// An audio decoder that the device lists but cannot start or keep running (ERROR_CODE_DECODER_INIT_FAILED, _QUERY_FAILED, DECODING_FAILED, AUDIO_TRACK_INIT_FAILED on the audio renderer) must not end the channel: if it
        /// is the device's MP2 decoder the next attempt decodes MP2 in the app (a non-fatal failure makes the session open a new player, which reads the flag); any other audio format has no
        /// replacement, so the audio is dropped and the picture goes on (the same player is prepared again without the audio track), and the viewer is told. Returns true if it dealt with the error.
        /// </summary>
        private bool RecoverFromAudioDecoder(PlaybackException e, int code, string name)
        {
            var exo = e as ExoPlaybackException;
            if (exo == null || exo.Type != ExoPlaybackException.TypeRenderer) return false;
            // 4001 decoder init failed, 4002 decoder query failed, 4003 decoding failed, 5001 audio track init failed (a passthrough the TV does not take, for one)
            if (code != PlaybackException.ErrorCodeDecoderInitFailed && code != PlaybackException.ErrorCodeDecoderQueryFailed && code != PlaybackException.ErrorCodeDecodingFailed &&
                code != PlaybackException.ErrorCodeAudioTrackInitFailed) return false;
            var f = exo.RendererFormat;
            string mime = f != null ? f.SampleMimeType : null;
            bool audio = (mime != null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) || (exo.RendererName != null && exo.RendererName.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!audio) return false;
            if (string.Equals(mime, MimeTypes.AudioMpegL2, StringComparison.OrdinalIgnoreCase) && !_softwareMp2 && _options.SoftwareAudio != SoftwareAudioMode.Off)
            {
                DeviceMp2DecoderFailed = true;
                UdpLog.Warn(Tag, "the device's MP2 decoder failed (" + name + "): the app decodes MP2 from now on");
                FailFromPlayer(new PlayerFailure("el decodificador MP2 del dispositivo ha fallado; se usará el del programa", false));
                return true;
            }
            var p = _player;
            if (p == null || _audioDropped) return false;
            _audioDropped = true;
            try
            {
                p.TrackSelectionParameters = p.TrackSelectionParameters.BuildUpon().SetTrackTypeDisabled(C.TrackTypeAudio, true).Build();
                p.Prepare();
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "dropping the audio: " + ex.Message); _audioDropped = false; return false; }
            UdpLog.Warn(Tag, "the audio decoder failed (" + name + ", " + StreamInfoText.CodecName(mime) + "): the channel goes on without audio");
            Notify("Sin sonido: el decodificador de audio de este dispositivo ha fallado (" + StreamInfoText.CodecName(mime) + ").");
            return true;
        }

        /// <summary>The tracks of the stream are known: the log names them and a track the device cannot decode is said (see StreamNotices).</summary>
        private void OnTracks(Tracks tracks)
        {
            if (IsDisposed || tracks == null) return;
            var facts = new List<TrackFacts>();
            var groups = tracks.Groups;
            int n = groups != null ? groups.Size() : 0;
            for (int i = 0; i < n; i++)
            {
                var g = groups.Get(i).JavaCast<Tracks.Group>();
                int type = g.Type;
                if (type != C.TrackTypeAudio && type != C.TrackTypeVideo) continue;
                for (int t = 0; t < g.Length; t++)
                {
                    var f = g.GetTrackFormat(t);
                    facts.Add(new TrackFacts(type == C.TrackTypeAudio ? TrackFacts.Audio : TrackFacts.Video, f != null ? f.SampleMimeType : null, g.IsTrackSupported(t)));
                }
            }
            if (facts.Count == 0) return;                                            // the player reports "no tracks" first, while it prepares
            _trackSummary = StreamNotices.Describe(facts);
            UdpLog.Info(Tag, "tracks: " + _trackSummary);
            foreach (var notice in StreamNotices.Analyze(facts)) Notify(notice);
        }

        private void OnFirstFrame()
        {
            if (IsDisposed) return;
            _firstFrame = true;
            StartSucceeded();
        }

        private void OnVideoSize(VideoSize vs)
        {
            if (vs == null || vs.Width <= 0 || vs.Height <= 0) return;
            float w = vs.Width, h = vs.Height;
            if (vs.UnappliedRotationDegrees == 90 || vs.UnappliedRotationDegrees == 270) { w = vs.Height; h = vs.Width; }
            float ratio = vs.PixelWidthHeightRatio > 0 ? vs.PixelWidthHeightRatio : 1f;
            SetVideoAspect(w * ratio / h);
        }

        private void OnDiscontinuity(int reason)
        {
            if (reason == DiscontinuityReasonSeek && _player != null && _player.PlaybackState == StateReady) SeekDone();
        }

        /// <summary>The Media3 listener; only what is needed (the other methods have default implementations). A managed exception must never go back into the Java looper, so every handler is guarded.</summary>
        private sealed class Listener : Java.Lang.Object, IPlayerListener
        {
            private readonly ExoPlayerBackend _b;
            public Listener(ExoPlayerBackend b) { _b = b; }

            public void OnPlaybackStateChanged(int playbackState) { Guard(() => _b.OnState(playbackState)); }
            public void OnPlayerError(PlaybackException error) { Guard(() => _b.OnError(error)); }
            public void OnRenderedFirstFrame() { Guard(_b.OnFirstFrame); }
            public void OnTracksChanged(Tracks tracks) { Guard(() => _b.OnTracks(tracks)); }
            public void OnVideoSizeChanged(VideoSize videoSize) { Guard(() => _b.OnVideoSize(videoSize)); }
            public void OnPositionDiscontinuity(PlayerPositionInfo oldPosition, PlayerPositionInfo newPosition, int reason) { Guard(() => _b.OnDiscontinuity(reason)); }

            private static void Guard(Action a)
            {
                try { a(); } catch (Exception ex) { UdpLog.Error(Tag, "listener: " + ex); }
            }
        }
    }
}
