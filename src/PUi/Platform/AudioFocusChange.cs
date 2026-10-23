namespace PUi.Platform
{
    /// <summary>What the system did to the app's audio focus (see IAudioFocus.Changed).</summary>
    public enum AudioFocusChange
    {
        /// <summary>The focus is ours again (after a loss, or just granted): playback can go on at full volume.</summary>
        Gained,
        /// <summary>Another app took the focus for good (a call, other music): playback has to stop or pause and not resume on its own.</summary>
        Lost,
        /// <summary>Another app took it for a short while (a notification, the assistant): pause, and resume when Gained arrives.</summary>
        LostTransient,
        /// <summary>Another app needs the focus but allows us to go on at a lower volume ("ducking").</summary>
        Duck
    }
}
