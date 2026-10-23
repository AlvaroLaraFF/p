using System;
using System.IO;
using AceEngine.App;
using ElmSharp;
using Tizen.Multimedia;

namespace PTizen.Platform
{
    /// <summary>
    /// Keeps the TV's audio/video decoder busy with a local clip, black and silent (looping), from the moment the app
    /// opens, so that the TV source (DTT/HDMI) stops sounding without waiting for a real channel to load. This TV has only
    /// one HW decoder: as soon as ANY Tizen.Multimedia.Player starts playing, the source stops (confirmed by the TV itself
    /// when tested with the real player). This "warmer" brings that moment forward to app startup instead of waiting for
    /// the chosen channel to finish loading.
    ///
    /// It is stopped (StopForHandoff) right before creating the real channel's Player -- two Players at once on this TV give
    /// PlaybackInterrupted ResourceConflict, the same error seen when changing Window.Alpha with a video running -- and it is
    /// started again (Start, which does nothing if it is already running) when that Player is released (Dispose/failure/end). It uses the
    /// SAME window (already opaque in the menus) as Display: the black video stays hidden under whatever Skia draws, just as already happens
    /// with the real player when the window is opaque.
    ///
    /// With the app hidden (Home) or closing it must never hold the decoder: Suspend releases it and blocks any start
    /// until Resume. Before, pressing Home with a channel open closed the player on pause and it asked for the warmer again
    /// with the app already hidden; and on closing the app the warmer was not released. Main suspect for why, on 2026-10-17, after leaving
    /// the app the TV's Home button stopped opening its menu (it only showed the channel).
    /// </summary>
    internal sealed class DecoderWarmer : IDisposable
    {
        private readonly Window _window;
        private readonly string _clipPath;
        private readonly object _gate = new object();
        private Player _player;
        private bool _starting;
        private bool _suspended;   // app in the background or closing: it is not started (if it started, the hidden app would keep the decoder)
        private bool _handedOff;   // the real channel's Player holds the decoder
        private int _gen;          // every release invalidates the pending starts (Ui.Post and PrepareAsync in flight)
        /// <summary>false = the warmer never starts (only to measure its CPU: AppConfig.Warmer). Default true.</summary>
        public volatile bool Enabled = true;

        public DecoderWarmer(Window window, string resourceDir)
        {
            _window = window;
            _clipPath = Path.Combine(resourceDir ?? "", "media", "silence.mp4");   // 640x360 H.264 main without B-frames + silent AAC, moov at the start
        }

        /// <summary>Starts the warmer when the app opens or when the real channel's Player is released. Does nothing if it is already running
        /// or if the app is in the background (Suspend/Resume decide that).</summary>
        public void Start() { lock (_gate) _handedOff = false; TryStart(); }

        /// <summary>Releases the decoder right before creating the real channel's Player (a single decoder on this TV). Call
        /// from the main thread (Ui.Run), right before "new Player()" in TizenPlayerBackend.PrepareAndStartAsync.</summary>
        public void StopForHandoff() { lock (_gate) _handedOff = true; Release("handoff to the player"); }

        /// <summary>The app is hidden (Home) or closed: releases the decoder and does not start again until Resume, even if the real
        /// player is released later (that happens right on pause: the player closes and would ask for the warmer with the app already hidden).</summary>
        public void Suspend() { lock (_gate) _suspended = true; Release("app in the background"); }

        /// <summary>The app is visible again: starts again unless the real player already holds the decoder.</summary>
        public void Resume() { lock (_gate) _suspended = false; TryStart(); }

        private void TryStart()
        {
            int gen;
            lock (_gate)
            {
                if (!Enabled || _suspended || _handedOff || _player != null || _starting) return;
                _starting = true; gen = _gen;
            }
            Ui.Post(() =>
            {
                Player p = null;
                try
                {
                    lock (_gate) { if (gen != _gen || _suspended || _handedOff) { _starting = false; return; } }
                    if (!File.Exists(_clipPath)) { UdpLog.Warn("warmer", _clipPath + " does not exist (res/media/silence.mp4 is missing from the package)"); lock (_gate) _starting = false; return; }
                    p = new Player { Display = new Display(_window) };
                    try { p.IsLooping = true; } catch (Exception ex) { UdpLog.Info("warmer", "IsLooping not available: " + ex.Message); }
                    var self = p;
                    p.ErrorOccurred += (s, e) => UdpLog.Warn("warmer", "ErrorOccurred " + e.Error);
                    // the system takes the decoder away (another app, the TV source): it is released completely and restarted on return (Resume)
                    p.PlaybackInterrupted += (s, e) => { UdpLog.Info("warmer", "interrupted by the system: " + e.Reason); Ui.Post(() => ReleaseIf(self, "interrupted")); };
                    // in case IsLooping does not loop the clip on this TV: restart by hand
                    p.PlaybackCompleted += (s, e) => { try { if (self.State != PlayerState.Idle) self.Start(); } catch (Exception ex) { UdpLog.Warn("warmer", "restart: " + ex.Message); } };
                    p.SetSource(new MediaUriSource(_clipPath));
                    lock (_gate) _player = p;
                    PrepareAndStart(p);
                }
                catch (Exception ex) { UdpLog.Warn("warmer", "Start: " + ex.Message); lock (_gate) { if (_player == p) _player = null; _starting = false; } try { p?.Dispose(); } catch { } }
            });
        }

        private async void PrepareAndStart(Player p)
        {
            try
            {
                await p.PrepareAsync();
                lock (_gate) { if (_player != p) return; }   // it was released while preparing (handoff, pause or close): Release already discarded it
                p.Start();
                UdpLog.Info("warmer", "warmer started (" + _clipPath + ")");
            }
            catch (Exception ex) { UdpLog.Warn("warmer", "PrepareAsync: " + ex.Message); ReleaseIf(p, "failed to prepare"); }
            finally { lock (_gate) _starting = false; }
        }

        private void ReleaseIf(Player p, string why)
        {
            lock (_gate) { if (_player != p) return; }
            Release(why);
        }

        private void Release(string why)
        {
            Player p;
            lock (_gate) { _gen++; p = _player; _player = null; }
            if (p == null) return;
            try
            {
                if (p.State == PlayerState.Playing || p.State == PlayerState.Paused) p.Stop();
                if (p.State != PlayerState.Idle) p.Unprepare();
            }
            catch (Exception ex) { UdpLog.Warn("warmer", "stop: " + ex.Message); }
            finally { try { p.Dispose(); } catch { } UdpLog.Info("warmer", "warmer stopped (" + why + ")"); }
        }

        public void Dispose() { Suspend(); }
    }
}
