using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App.Tv;
using AceEngine.Live;

namespace PUi.Platform
{
    /// <summary>What kind of screen a channel can be sent to (the icon of its row in the picker).</summary>
    public enum CastDeviceKind { Chromecast, Tv }

    /// <summary>A screen in the "Enviar a la tele" picker: what the interface needs to know about it, nothing of how it is reached.</summary>
    public sealed class CastDevice
    {
        /// <summary>Stable and unique ("dlna:uuid", "gcast:id").</summary>
        public string Id = "";
        /// <summary>The name the user knows it by ("Salón", "Samsung Q6 Series (65)").</summary>
        public string Name = "";
        public CastDeviceKind Kind;
        /// <summary>The pause button is offered while sending to it (a DLNA television may not have a Pause action: the first try finds out).</summary>
        public bool CanPause = true;
        /// <summary>The volume slider is offered while sending to it.</summary>
        public bool CanVolume = true;

        public override string ToString() { return Kind + " '" + Name + "' (" + Id + ")"; }
    }

    /// <summary>Whether one channel can be sent to one device: the grey-out of the picker.</summary>
    public sealed class CastCheck
    {
        /// <summary>false = the engine could not tell (not enough data yet, no live buffer): the row stays enabled and says nothing.</summary>
        public bool Known;
        public bool Compatible = true;
        /// <summary>Why not, short and in Spanish, ready to show ("El Chromecast no reproduce el audio de este canal"). Empty if it can be sent.</summary>
        public string Reason = "";

        public static readonly CastCheck Unknown = new CastCheck { Known = false };
        public static CastCheck Ok() { return new CastCheck { Known = true, Compatible = true }; }
        public static CastCheck No(string reason) { return new CastCheck { Known = true, Compatible = false, Reason = reason ?? "" }; }
    }

    public enum CastPhase
    {
        /// <summary>Nothing is being sent.</summary>
        Idle,
        /// <summary>Connecting to the device, opening the channel and preparing what it will fetch (5 to 10 s).</summary>
        Starting,
        /// <summary>The device has the channel.</summary>
        Sending
    }

    /// <summary>What is being sent, to whom and in what state. A snapshot: the service makes a new one when anything changes.</summary>
    public sealed class CastStatus
    {
        public CastPhase Phase;
        public CastDevice Device;
        public Channel Channel;
        /// <summary>Paused on the device.</summary>
        public bool Paused;
        /// <summary>Volume of the device, 0 to 100; -1 if it is not known.</summary>
        public int Volume = -1;
        public bool IsActive { get { return Phase != CastPhase.Idle; } }

        public static readonly CastStatus Idle = new CastStatus();
    }

    /// <summary>
    /// OPTIONAL service: "Enviar a la tele" (send the channel to another screen: a Chromecast, or a television that is a DLNA renderer). Only a platform with a touch screen has one (a phone or a
    /// tablet); the TV is the receiver and never shows the function. The interface owns everything that is seen (the button, the sheet with the devices that were found and whether each can play
    /// the channel, the "sending to ..." state of the player, the strip over the lists, the notices); this contract is what it needs from the platform: the devices, the check of a channel against a
    /// device, send, stop, pause and volume. UiContext.Cast finds it in the platform object, or the host sets it; where nothing implements it <see cref="NoCast"/> answers "not available" and no
    /// button is drawn. Everything may be called on the interface thread; events come on any thread (the interface redraws).
    /// </summary>
    public interface ICastService
    {
        /// <summary>The device can send at all: the cast button shows. (The sheet then searches; it says so while there is nothing to list.)</summary>
        bool IsAvailable { get; }
        /// <summary>IsAvailable changed. Raised on any thread; the interface redraws.</summary>
        event Action AvailabilityChanged;

        /// <summary>The devices found so far, one entry per screen, in a stable order.</summary>
        IReadOnlyList<CastDevice> Devices { get; }
        /// <summary>A search is running (the sheet shows "Buscando aparatos…" while it runs and there is nothing to list).</summary>
        bool Searching { get; }
        /// <summary>The sheet opened: looks for devices now and keeps refreshing, lightly, until <see cref="EndSearch"/>. Idempotent. Calling it again after a search ended searches again.</summary>
        void BeginSearch();
        /// <summary>The sheet closed (or the app went to the background): no more searching. Idempotent.</summary>
        void EndSearch();

        /// <summary>What is being sent now (never null).</summary>
        CastStatus Status { get; }
        /// <summary>The devices, the search or the status changed (any thread).</summary>
        event Action Changed;
        /// <summary>Something the user has to be told: the sending ended by itself (the channel went down, it was stopped from the TV), could not start, or the device does not do what was asked. Spanish, one sentence. Any thread.</summary>
        event Action<string> Notice;

        /// <summary>
        /// Can <paramref name="channel"/> be sent to <paramref name="device"/>? <paramref name="open"/> is the live session of the channel if the player has it open (the check then reads it
        /// instead of opening the channel again: the engine allows one session per channel); null otherwise. Never throws: a failure is <see cref="CastCheck.Unknown"/>.
        /// </summary>
        Task<CastCheck> CheckAsync(CastDevice device, Channel channel, LiveSession open, CancellationToken ct);

        /// <summary>
        /// Sends <paramref name="channel"/> to <paramref name="device"/>, replacing what was being sent. <paramref name="releaseLocal"/> stops the local playback of the channel first (the engine
        /// allows one session per channel); it may be null. Returns true when the device has the channel; on false the reason has been given through <see cref="Notice"/>.
        /// </summary>
        Task<bool> SendAsync(CastDevice device, Channel channel, Func<Task> releaseLocal, CancellationToken ct);

        /// <summary>Stops sending (the device goes back to what it was doing). Idempotent.</summary>
        void Stop();
        /// <summary>Pause or resume on the device.</summary>
        void SetPaused(bool paused);
        /// <summary>Volume of the device, 0 to 100.</summary>
        void SetVolume(int percent);
    }

    /// <summary>The cast service of a device that has none (the TV, a device that cannot send): never available.</summary>
    public sealed class NoCast : ICastService
    {
        public static readonly NoCast Instance = new NoCast();
        private static readonly IReadOnlyList<CastDevice> None = new CastDevice[0];
        private NoCast() { }
        public bool IsAvailable { get { return false; } }
        public event Action AvailabilityChanged { add { } remove { } }
        public IReadOnlyList<CastDevice> Devices { get { return None; } }
        public bool Searching { get { return false; } }
        public void BeginSearch() { }
        public void EndSearch() { }
        public CastStatus Status { get { return CastStatus.Idle; } }
        public event Action Changed { add { } remove { } }
        public event Action<string> Notice { add { } remove { } }
        public Task<CastCheck> CheckAsync(CastDevice device, Channel channel, LiveSession open, CancellationToken ct) { return Task.FromResult(CastCheck.Unknown); }
        public Task<bool> SendAsync(CastDevice device, Channel channel, Func<Task> releaseLocal, CancellationToken ct) { return Task.FromResult(false); }
        public void Stop() { }
        public void SetPaused(bool paused) { }
        public void SetVolume(int percent) { }
    }
}
