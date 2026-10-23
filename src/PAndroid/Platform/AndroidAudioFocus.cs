using System;
using Android.Content;
using Android.Media;
using Android.OS;
using AceEngine.App;
using PUi.Platform;

namespace PAndroid
{
    /// <summary>
    /// Audio focus on Android: a real arbitration between apps. The contract's Acquire is called by the interface when it opens and when it comes back, but on Android the app must NOT take the
    /// focus just for being open (it would stop the music of another app): Acquire does nothing here and the PLAYER asks for the focus when it starts playing (PlayerStarted) and lets it go
    /// when it ends (PlayerStopped). Release (the interface pauses or leaves) abandons it at once. Changed reports what the system does to it, on the main thread.
    /// </summary>
    internal sealed class AndroidAudioFocus : IAudioFocus
    {
        private const string Tag = "audio";
        /// <summary>After the last player is gone the focus is kept this long, so that a retry or a channel change (which creates a new player) does not let another app's music flap on and off in between.</summary>
        private const int AbandonDelayMs = 2000;

        private readonly AudioManager _am;
        private readonly Handler _main = new Handler(Looper.MainLooper);
        private readonly object _gate = new object();
        private readonly FocusListener _listener;
        private AudioFocusRequestClass _request;
        private bool _held;
        private int _players;
        private int _generation;

        public AndroidAudioFocus(Context applicationContext)
        {
            _am = (AudioManager)applicationContext.GetSystemService(Context.AudioService);
            _listener = new FocusListener(OnSystemChange);
        }

        public event Action<AudioFocusChange> Changed;

        public bool IsHeld { get { lock (_gate) return _held; } }

        // ---------------------------------------------------------------- the contract
        void IAudioFocus.Acquire() { /* deliberately empty: the player asks for the focus when it plays (see the class remarks) */ }

        void IAudioFocus.Release()
        {
            lock (_gate) { _generation++; }
            AbandonNow("the interface released it");
        }

        // ---------------------------------------------------------------- the player's side (any thread)
        /// <summary>A player is about to play: takes the focus if the app does not hold it. Returns false if the system refused it (a call in progress); the player may still play.</summary>
        public bool PlayerStarted()
        {
            lock (_gate)
            {
                _players++; _generation++;
                if (_held) return true;
            }
            return Request();
        }

        /// <summary>A player went away. The focus is abandoned shortly after the last one, unless another one starts.</summary>
        public void PlayerStopped()
        {
            int gen;
            lock (_gate) { if (_players > 0) _players--; if (_players > 0) return; gen = ++_generation; }
            _main.PostDelayed(() =>
            {
                lock (_gate) { if (gen != _generation || _players > 0) return; }
                AbandonNow("no player left");
            }, AbandonDelayMs);
        }

        /// <summary>The player asks again after being paused by a loss of the focus (the user pressed play).</summary>
        public bool RequestAgain() { return IsHeld || Request(); }

        // ---------------------------------------------------------------- AudioManager
        private bool Request()
        {
            try
            {
                if (_am == null) return false;
                AudioFocusRequest result;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var attrs = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Movie).Build();
                    var req = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                        .SetAudioAttributes(attrs).SetAcceptsDelayedFocusGain(false).SetWillPauseWhenDucked(false).SetOnAudioFocusChangeListener(_listener, _main).Build();
                    result = _am.RequestAudioFocus(req);
                    lock (_gate) _request = req;
                }
                else
                {
#pragma warning disable CA1422
                    result = _am.RequestAudioFocus(_listener, Stream.Music, AudioFocus.Gain);
#pragma warning restore CA1422
                }
                bool granted = result == AudioFocusRequest.Granted;
                lock (_gate) _held = granted;
                UdpLog.Info(Tag, "focus " + (granted ? "granted" : "REFUSED (" + result + ")"));
                return granted;
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "could not request the focus: " + ex.Message); return false; }
        }

        private void AbandonNow(string why)
        {
            AudioFocusRequestClass req;
            bool wasHeld;
            lock (_gate) { req = _request; _request = null; wasHeld = _held; _held = false; }
            if (!wasHeld && req == null) return;
            try
            {
                if (_am == null) return;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) { if (req != null) _am.AbandonAudioFocusRequest(req); }
                else
                {
#pragma warning disable CA1422
                    _am.AbandonAudioFocus(_listener);
#pragma warning restore CA1422
                }
                UdpLog.Info(Tag, "focus abandoned (" + why + ")");
            }
            catch (Exception ex) { UdpLog.Warn(Tag, "could not abandon the focus: " + ex.Message); }
        }

        /// <summary>The system changed the focus (main thread).</summary>
        private void OnSystemChange(int androidChange)
        {
            var change = AudioFocusMap.FromAndroid(androidChange);
            UdpLog.Info(Tag, "focus change " + androidChange + " -> " + (change.HasValue ? change.Value.ToString() : "ignored"));
            if (!change.HasValue) return;
            if (change.Value == AudioFocusChange.Lost) { lock (_gate) { _held = false; } AbandonNow("lost for good"); }
            else if (change.Value == AudioFocusChange.LostTransient) { /* still ours when it comes back */ }
            else if (change.Value == AudioFocusChange.Gained) { lock (_gate) { _held = true; } }
            var h = Changed;
            if (h != null) { try { h(change.Value); } catch (Exception ex) { UdpLog.Warn(Tag, "Changed handler: " + ex.Message); } }
        }

        private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
        {
            private readonly Action<int> _cb;
            public FocusListener(Action<int> cb) { _cb = cb; }
            public void OnAudioFocusChange(AudioFocus focusChange) { try { _cb((int)focusChange); } catch (Exception ex) { UdpLog.Warn("audio", "focus listener: " + ex.Message); } }
        }
    }
}
