# P!

P! is an app for watching Ace Stream channels and videos on a Samsung Smart TV (Tizen 5.5) and on Android: Android TV / Google TV, Fire TV, and phones and tablets (landscape, touch). It includes its own Ace Stream–compatible P2P engine written in C#, so it needs nothing else on the network. The interface is made for the remote control and is in Spanish; the Android phone and tablet interface is being adapted to touch.

All platforms share the same engine and interface. Android is one apk for all its devices: what differs between a TV, a Fire TV stick, a phone and a tablet comes from a device profile detected at startup, and the interface has its own phone and tablet layouts (see "Phones and tablets").

> Personal project, not affiliated with Ace Stream or Samsung. Keep any shared copy of this repository **private**: the engine embeds keys extracted from the official Ace Stream app.

## Projects

| Project | What it is |
|---|---|
| `src/AceEngine` | The engine and app logic, with no UI. It covers P2P (BitTorrent + Ace extensions), live streams with signed pieces and timeshift, link resolution, list loading, channel checking, search, user settings and UPnP / NAT-PMP. It also holds what every shell shares: the layered configuration (`AppConfigLoader`), the UDP log (`UdpLog`), the CPU and memory monitor and the startup self-diagnosis. It targets netstandard2.0, with managed code only and no NuGet packages. |
| `src/PUi` | The whole interface, drawn with SkiaSharp: screens, theme, animations, focus handling, the platform contract (`PUi.Platform`) and the common app startup and lifecycle (`PUi.Host.AppHost`). Two targets from the same sources: netstandard2.0 against SkiaSharp 1.60 (reference only: the TV ships its own and never loads another) and net8.0 with SkiaSharp 2.88 (desktop, tests, Android). |
| `src/PUi.Demo` | Sample data and doubles of the app services, and the interface demo mode (`uiDemo`). |
| `src/PUi.Testing` | The windowless desktop platform and the scenario rig shared by the test bench and the interface tests. |
| `src/PTizen` | The Samsung TV app (`tizen60`, .NET Core 3.0 on the TV). It holds only what is the TV's own: the Elementary window and the Evas GL view where Skia draws, the keys to register (`TizenKeys`), `Tizen.Multimedia.Player`, audio focus and the decoder warmer. |
| `src/PAndroid` | The Android app (`net10.0-android`, minSdk 25, targetSdk 35; arm64-v8a, armeabi-v7a and x86_64). It holds only what is Android's own: the activity, `AndroidSkiaView` (a TextureView with its own EGL context, drawn on the main thread, over the video SurfaceView), the keys and touch translation (including the Fire TV remotes' and game controllers' key table), audio focus, network info and multicast lock, the device profile (with the Fire TV models Amazon documents), the decoder report, the MP2 software decoder, the players (Media3 / ExoPlayer, with `MediaPlayer` as an alternative) and, on phones and tablets only, "Enviar a la tele" (the Google Cast sender in `GoogleCast/`, the foreground service of a sending in `Cast/`). `src/PAndroid/Pure` has its pure logic (key table, profile, screen and touch arithmetic, decoder gate, MP2 decoder, HDMI and screen policies, Cast models, send policy, notification text and permission rules), which `tests/PAndroid.Tests` links and tests on the PC. It is not part of `P.sln`: it needs .NET 10 with the android workload, the Android SDK and a JDK 17. |
| `src/PDesktop` | Desktop test bench: it renders any screen to PNG using fake data. |
| `src/AceEngine.Cli` | The `ace` command-line tool: play, probe, measure, search and diagnose from a PC. |
| `src/AceEngine.Sim` | A simulated Ace network for tests. |
| `tests/` | Engine/app tests, interface tests and the tests of the pure Android logic. |

## Layers

- `AceEngine` is the bottom layer and depends on nothing.
- `PUi` and `PUi.Demo` depend on `AceEngine`; `PUi.Testing` (net8.0, never shipped) on `PUi` and `PUi.Demo`.
- The shells (`PTizen`, `PAndroid`, `PDesktop`) depend on `PUi` and the engine, and on `PUi.Demo` / `PUi.Testing` where they need them. No shell knows another. The only shell sources a test project may link are pure tables: `PTizen/Platform/TizenKeys.cs` and `PAndroid/Pure`.
- The engine knows nothing about Skia or any platform. The interface knows nothing about Tizen, ElmSharp or Android: a platform plugs in through `PUi.Platform` (window, input including touch, system keyboard, audio focus, screen, paths, captures) and `AceEngine.App.PlatformHooks` (player, network info, multicast lock, playback tuning, config defaults).
- The shells do not reference each other. Conditional compilation (`#if`) exists only in `src/PUi/Gfx/SkiaCompat.cs`.
- The TV ships its own SkiaSharp 1.60: PTizen and the netstandard2.0 build of PUi compile against the 1.60.3 package as a reference only and never package it. SkiaSharp 3.x must not be used while the TV is a target.
- These rules are tests: `tests/PUi.Tests/ArchitectureTests.cs`.
- The version is written once, as `AppVersion` in `Directory.Build.props`. `src/PTizen/tizen-manifest.xml` has to repeat it (the build and a test check that they match).

## Phones and tablets (touch)

The same interface runs on a phone or a tablet with its own layouts (design: `design-movil`, 390 x 844, 800 x 1280 and 1280 x 800 CSS pixels). A finger does what the remote does, the Samsung TV is not touched by any of it (the desktop bench's TV pictures are identical to the ones before this work: `PlatformCapabilities.HasTouch` is false there and every compact path is skipped).

**Kinds of device** (`DeviceKind`, decided by `ScreenMetrics` from touch and the physical size of the screen, `ScreenMetrics.Kind`):

| Kind | When | Logical canvas |
|---|---|---|
| `Tv` | no touch (a TV is a TV whatever its size or density: an Android TV at 1080p reports 540 dp) | 1920 x 1080, as always |
| `PhonePortrait` | touch, taller than wide, smaller side under 600 dp | 390 wide |
| `TabletPortrait` | touch, taller than wide, smaller side of 600 dp or more | 800 wide |
| `TabletLandscape` | touch, as wide as tall or wider | 1280 wide (a phone turned on its side, which only happens in the full screen player, gets 1 unit = 1 dp, 640 to 1280 wide) |

On a phone or a tablet the canvas **is the safe area** (`SafeInsets` are outside it: the status bar, the notch and the gesture bar never carry anything), with the design's units and as tall as the safe area needs, so the layouts are worked out from the logical width and height (`CompactStyle`, one per surface). The animated background is drawn on the whole surface. When the surface changes (`IRenderHost.ScreenChanged`: rotation, split screen, insets) every screen lays itself out again keeping the screen, the focus, the scroll and the data; a tablet turning in the middle of a list or of a channel does not restart anything.

**What each screen is** (the data, the live check, the order of discovery that does not shuffle the rows, peers and quality, the lava lamp and the background are the TV's):

- TV: a phone shows the recents in a row that slides sideways, then the categories in 2 columns; a tablet in portrait 3 across and 3 columns; in landscape 4 and 4 with a rail at the left like the TV's. The sections (TV, Search, Settings) are a floating bar at the bottom on a phone and a tablet in portrait.
- Category, Explore and results: a header with the back arrow (the TV has none), the title and the sort button; the channel rows go full width and the "looking for more" dot and the hidden count close the list.
- Search: the field stays at the top (the system keyboard, as before; PUi's own keyboard where there is none), the recent searches slide sideways, Explore in 2, 3 or 4 columns.
- Settings: groups in rounded panels with switches and round "-" / "+" (one column; two on a tablet in landscape).
- Player, portrait: the video is a 16:9 area at the top; below it, always there, the channel, live badge, peers and speed, the memory bar (drag it), pause and +-10 s, "Pantalla completa", "Enviar a la tele" (not shown, see below) and more channels of the same list (tap one to open it). Landscape: the video fills the screen and the controls are over it, shown and hidden by a tap (the TV's panel). Loading, buffering, pause and "Sin fuentes por ahora" have their phone and tablet versions.
- **"Pantalla completa" turns only the video**: the phone stays vertical everywhere else. The button asks the platform for landscape, the screen turns, and the player draws the landscape layout; the arrow, the compress button or Back give the orientation back (a phone asks for portrait, a tablet for "what the device says").
- **Send to the TV** ("Enviar a la tele"): see the next section. It is only on phones and tablets: the TV (and a Fire TV, an Android TV) is the receiver and never draws any of it.

**What a finger does** (`IInputSource.Pointer`, in the canvas's logical units):

- A tap is "focus + OK" on the element under the finger: open a channel, enter a category, change section from the bar or rail, go back with the arrow, flip a switch, press the round "-" or "+" of a selector, the sort button, a recent search, "Pantalla completa", a "more channels" row. While the finger is down the element is highlighted.
- A drag scrolls the lists with inertia and a soft rebound past the ends; the recent channels and the recent searches slide sideways. A drag never taps.
- In the player: over the video a tap shows or hides the controls; the memory bar can be dragged inside what is in memory and the player moves when the finger is lifted. The system Back gesture arrives as `UiKey.Back`.
- On a touch-only device the focus is not drawn (the finger is the pointer); an arrow key or OK brings it back (a tablet with a keyboard or a game pad). Boxes drawn smaller than a fingertip get a larger touch zone (7 mm, from `ScreenMetrics.Density`), and every box that reacts to a touch is at least 44 units (44 CSS px, about 7 mm) each way (`TouchSizeTests` print the sizes and check them). No text is smaller than 12 units (`CompactLayoutTests`).

**Text:** on a touch platform Search asks the system keyboard (`IPlatform.TextInput`, `ITextInput`). A touch platform without one gets PUi's keyboard, usable with the finger (its keys are narrow on a phone: a real touch device has a system keyboard).

**What the shell has to do** (the optional contracts are found by `UiContext` when the platform object, its `Window` or its `Render` implement them; `PlatformCapabilities`, `ScreenMetrics`, `PointerEvent` and `ITextInput` are documented in their files):

1. Build `ScreenMetrics` with the surface's size, the system insets (status bar, notch, gesture bar: `WindowInsets`), the density and **`touch: true`** (`new ScreenMetrics(w, h, insets, density, true)`), raise `ScreenChanged` whenever any of them changes (rotation, split screen, insets) and report `PlatformCapabilities.TouchOnly` (or its own with a keyboard or a remote).
2. Turn touch positions into `PointerEvent`s with `Screen.ToLogicalX` / `ToLogicalY` (they include the canvas's position inside the safe area).
3. `IVideoPlacement.SetVideoRect(PixelRect)`: put the video view (a `SurfaceView` or `TextureView` behind the transparent drawing surface) in that rectangle of the surface, fitting the picture inside it with its own aspect ratio; PUi leaves those pixels transparent and draws opaque everywhere else. `ClearVideoRect()` gives the whole surface back. It is called when the player opens, when the layout changes and when it closes. In landscape the rectangle is the whole surface.
4. `IOrientationControl.Request(OrientationRequest)`: **lock a phone to portrait when the app starts** and let a tablet follow the sensor. `Landscape` is asked by "Pantalla completa": rotate (a sensor landscape), raise `ScreenChanged` with the new surface and PUi draws the landscape player. `Portrait` (phone) or `Device` (tablet: back to the sensor) is asked on leaving full screen or the player. It is a wish: the shell may take its time or refuse (split screen) and PUi works with whatever surface it gets. In full screen the shell may also hide the system bars (immersive mode): PUi follows the insets it reports.
5. Implement `ICastService` (a phone or a tablet that can send; see "Send to the TV"): the cast button, the sheet and the sending state appear when it says it is available. A shell without it keeps `NoCast`.

The Samsung TV implements none of the optional contracts and builds `ScreenMetrics` without touch: nothing changes on it.

**Looking at it:** `pdesk --device phone` (1080 x 2400 in portrait with the status bar and the gesture bar), `tablet` (2560 x 1600), `tabletPortrait` (1600 x 2560) or `phoneLandscape` (the phone on its side) render every scene on that screen, plus the touch scenes (`category-press`, `category-drag`, `main-press`, `settings-minus`, `search-ime`, `search-keyboard` (PUi's own keyboard), `main-focus` (a key brings the focus back), `player-scrub` and `player-fullscreen`, which plays the shell and turns the phone). `--guides` marks the covered edges and the canvas; `--targets` marks every touch target (green) and its zone (yellow); `--size`, `--insets`, `--density`, `--touch` build any other screen.

**Not done:** the keyboard's height (`ITextInput` does not report it) is not used, so on a phone the system keyboard may cover the lower results (the field and the first results are at the top); the interface has no layout for a phone in landscape except the full screen player's.

## Send to the TV (phones and tablets)

"Enviar a la tele" sends the channel to a **Chromecast** (Google Cast) or to a **television that is a DLNA renderer** (most Smart TVs, Samsung and LG included). The engine does the work (`AceEngine.Cast`, see the next section; there is no transcoding); the phone only chooses and controls. The Samsung TV, a Fire TV and an Android TV are receivers: none of this is drawn or started there (`pdesk` TV pictures are byte-identical to before, and `UiContext.CastUi` is never even created).

**What the user does**

1. The cast button (headers of the TV page, the rail of a tablet in landscape, "Enviar a la tele" in the player) opens a **sheet**. It looks for devices while it is open ("Buscando aparatos..." while there is nothing to list; "No se ha encontrado ningún aparato" and "Buscar de nuevo" when the search found none). One row per screen: its name, a Chromecast or a television icon, and its kind.
2. With a channel in view (the player's), each device is **checked against it**: DLNA televisions take any channel; a Chromecast only H.264 + AAC inside its profile. The ones that cannot are dimmed with the reason ("El Chromecast no reproduce el audio de este canal"); tapping one says it again and sends nothing.
3. Tapping a device sends the channel. The local playback is released first (the engine allows one session per channel). The player then stops playing here and shows **"Enviando a Salón"**: the channel, pause and resume, the volume and "Dejar de enviar" (only what the device can do). While it connects (5 to 10 s) it says so and offers "Cancelar".
4. Back leaves for the lists and the sending goes on: a **strip** over the bottom bar ("Enviando a Salón", pause, stop; a tap opens the sheet with the same controls and the other devices). **Choosing a channel anywhere while sending sends that one** to the same device; so does a tap on "more channels" in the player, and a tap on another device moves the channel there.
5. With **no channel in view** (the button of a list page) a tapped device waits for the next channel chosen in a list ("Toca un canal de la lista").
6. If the sending **ends by itself** (stopped from the TV or from another phone, the channel went down, the device went away) the user is told ("El envío ha terminado: se ha parado desde la tele") and the player is a player again (it opens the channel here, live).

**Layers.** PUi owns everything that is seen (`CastUi`: the sheet, the strip, the notices; `PlayerScreen.Cast.cs`: the sending state) and talks to the platform only through `ICastService` (devices, search, check, send, stop, pause, volume, notices). `PUi.Cast.CastController` is the `ICastService` of a touch device, made of the engine's `CastService` (through `ICastEngine`), the shell's Google Cast bridge (`IGoogleCastBridge`) and what the shell does while sending (`ISendingHost`); it has no platform type in it and is tested on the PC with doubles (`CastControllerTests`). `PlatformHooks.KeepEngineAlive` keeps the engine running when the app goes to the background while a channel is being sent.

**Android** (`src/PAndroid`):

- **Who sends:** only a phone or a tablet (`SendPolicy`). A **TV, a Fire TV and a box never do**: they are receivers (a Fire TV also has no Google Play services and no Cast SDK), so `AndroidCastFactory` builds nothing there and the interface shows nothing. A touch device **without Google Play services** (a Fire tablet) still sends to DLNA televisions; the Google Cast sender exists only behind `AndroidCast.IsEnabled` (Play services present, not a Fire TV, `"platform": {"cast": "off"}` not set).
- **Google Cast** (`GoogleCast/`, the only code that names the SDK; binding `Xamarin.GooglePlayServices.Cast.Framework` 122.3.1.2, net10.0-android36.0): `CastOptionsProvider` with Google's Default Media Receiver (`CC1AD845`: nothing to register), no SDK notification or media session, no session that outlives the app, the receiver stops with the session. Discovery is MediaRouter's (passive while the app is visible, active while the sheet is open); every route that is a screen (a speaker is not) becomes a target of the engine's list with the profile of its model (`CastModels`: Chromecast Ultra is the 3rd gen. profile, Google TV / Shield the Google TV one, **every other model, the 2015 Chromecast included, the most restrictive**). Send: the session is opened while the engine prepares the HLS, then `RemoteMediaClient.load` with the URL, the content type, `StreamType.LIVE`, unknown duration and the title; pause and volume go to the session; the end of the session (stopped from the TV or another phone, the receiver went idle) ends the sending.
- **DLNA:** `CastService.DiscoverDlnaAsync` under its own `MulticastLock`, run quietly when the app starts and each time it comes back to the front (`CastController.WarmUp`), again when the sheet opens and every 12 s while it is open (a television that misses one search stays 40 s in the list: `CastServiceOptions.DlnaRetention`). Each television goes on the list as soon as its description arrives (M-SEARCH with MX 1), not at the end of the 4 s search: a sheet closed after a second has still shown it. Pause, resume and volume are SOAP calls; a television without `Pause` loses the button and the user is told. Its state (paused, volume) is read every 4 s while sending.
- **While sending:** `CastSendingService`, a foreground service of type `mediaPlayback`, with the notification "Enviando a Salón" and a "Dejar de enviar" button, a Wi-Fi lock (high performance) and a partial wake lock; permissions `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `WAKE_LOCK` and `POST_NOTIFICATIONS` (asked for with the system's dialog the first time a channel is sent, at most twice: the sending works without it, `NotificationPermissionPolicy`). The sending goes on with the screen off. Back on the first page moves the app to the background instead of closing it while sending; closing it (swiping it away) ends the sending, because the engine lives in the activity's process.
- **Checked on a real phone** (Android 16): selecting the route starts the Cast session (`SelectRoute`); Google's default receiver plays the engine's HLS from the LAN on a 2015 Chromecast, and keeps playing with the app in the background (the foreground service holds the engine); a Samsung QE65Q64T is found in under half a second and plays with the variant `samsung-mpeg-biglength` about 5 s after the tap.
- **Still to look at** (the log lines are tagged `cast` and `dlna`): the delay behind live on a Chromecast (about 16 s expected, 20 to 30 s by the study), how other brands answer `SetAVTransportURI` (the engine tries the brand's variants in order and logs the one that worked), that pause on a live DLNA stream works or is refused cleanly, how Android 13 to 15 treat the notification and the foreground service with the screen off, and, with target 37, the local network permission, which is not asked for yet.

**Looking at it:** `pdesk --device phone|tablet|tabletPortrait` renders the scenes `cast-sheet-searching`, `cast-sheet`, `cast-sheet-checking`, `cast-sheet-empty`, `cast-sheet-sending`, `cast-player-starting`, `cast-player-sending`, `cast-player-paused`, `cast-pill`, `cast-pill-starting`, `cast-armed` and `cast-notice` with a fake service (`FakeCast`).

## Requirements

- .NET 10 SDK (the TV app still runs on the .NET Core 3.0 that ships with the TV).
- Samsung TV: Tizen Studio command-line tools (`sdb` and `tizen`), a Samsung certificate profile created with Tizen Studio's Certificate Manager, and the TV in developer mode with the PC's IP set as the developer host.
- Android (`src/PAndroid`, outside the solution): the android workload (`dotnet workload install android`), JDK 17, and the Android SDK with platform 36 and build-tools 35 (for example in `~/Android/Sdk`; set `ANDROID_HOME`), plus `platform-tools` for `adb`.

## Build and test

```
dotnet build P.sln -c Release
dotnet test tests/AceEngine.Tests
dotnet test tests/PUi.Tests
dotnet test tests/PAndroid.Tests       # the Android shell's pure logic: keys, profiles, decoders, policies
```

## Install on the TV

```
./deploy.sh <tv-ip> --config my-config.json             # build, sign, install and start
./deploy.sh <tv-ip> --config my-config.json --listen tv.log   # also receive the log in tv.log
./deploy.sh <tv-ip> --no-run                            # install without starting the app
./deploy.sh --help
```

Environment variables:

- `PROFILE`: certificate profile name. Default: `p`.
- `SDB_PORT`: default `26101`.
- `DOTNET_DIR`: default `~/.dotnet`.
- `TIZEN_DIR`: default `~/tizen-studio`.

## Install on an Android device

```
./deploy-android.sh                                    # build (Release, all ABIs), install and start on the only connected device
./deploy-android.sh --config my-config.json            # also push a test configuration
./deploy-android.sh --listen android.log               # also receive the log over UDP in android.log
./deploy-android.sh --connect 192.168.1.50 --abi auto  # a TV or Fire TV over the network, building only for its ABI
./deploy-android.sh --logcat                           # follow logcat (tag P) after starting
./deploy-android.sh --build-only                       # build and check the apk (tools/apkcheck.py), no device needed: dist/P-<version>-universal.apk
./deploy-android.sh --connect 192.168.1.50 --no-build --no-install --udp-log --listen fire.log   # only point the log of an installed app at this PC
./deploy-android.sh --help
```

Every build is checked by `tools/apkcheck.py` (SDK levels, the LEANBACK_LAUNCHER activity and its 320 x 180 banner, features that must not be required, the `armeabi-v7a` libraries, the v2 signature, no Google Play services code). The one apk is universal: Fire TV and Android TV (leanback launcher), phones and tablets (launcher), `arm64-v8a`, `armeabi-v7a` (every Fire TV) and `x86_64`. See "Fire TV (Fire OS)" for installing on a Fire TV, with the Downloader app or with adb.

On the device, enable Developer options and USB debugging (a TV or Fire TV: network debugging / ADB debugging, then `--connect`). The apk is signed with a development key kept **outside** the repository, in `../p-interno/android-keys` next to it (`P_ANDROID_KEYS` points elsewhere): the same key for every build, so `adb install -r` always updates in place. Without that directory the build falls back to the SDK's debug key. Building by hand:

```
export JAVA_HOME=~/opt/jdk-17 ANDROID_HOME=~/Android/Sdk
cd src/PAndroid && dotnet build -c Release -p:AndroidSdkDirectory=$ANDROID_HOME     # bin/Release/net10.0-android/org.p.app-Signed.apk
```

Notes:

- The Android configuration comes in layers, each overriding the previous one: the defaults of the platform and of the device profile, `Assets/config.json` (packaged), `config.json` and `settings.json` in the app's data, and `p-config.json` in `/sdcard/Android/data/org.p.app/files/` (what `deploy-android.sh --config` pushes; restart the app to apply it).
- The log goes to logcat with the tag `P`. The UDP log is off by default.
- The app only asks for the audio focus when a channel starts playing, never when it opens.
- On phones and tablets the interface gets the real screen (`ScreenMetrics` built with `touch: true`: size, density, and the insets of the status bar, the cutout and the gesture bar) and lays out its canvas inside the safe area. The video `SurfaceView` goes exactly where PUi says (`IVideoPlacement`: the 16:9 area at the top of the portrait player, the whole screen in landscape; PUi leaves those pixels transparent). The system keyboard (`ITextInput`) is an `InputConnection` on the drawing view, active only while PUi asks for it. On a TV or a Fire TV there is no system keyboard (PUi's own, driven by the remote).
- Orientation (`IOrientationControl`, rules in `OrientationPolicy`): a TV or Fire TV is always landscape; a **phone is locked to portrait** at startup and turns to landscape only for "Pantalla completa" (then back); a tablet follows the sensor. The system bars are visible, with the interface's canvas inside the safe area they leave, except in full screen and on a TV, where the app is immersive.
- `{"platform": {...}}` in the configuration holds the options only the Android shell reads: `player` (`exo` or `mediaplayer`), `deviceKind` (`tv`, `firetv`, `phone`, `tablet`, `box`: to try one device's interface on another), `maxRenderSide` (longest side in pixels of what is drawn), `ambientFps` (frames per second of the animated background: 30, or 20 on a weak TV stick), `softwareAudio` (`auto`: the app decodes MP2 audio only if the device cannot; `off`; `force`: always, to try it), `cast` (`auto` or `off`; never on a device without Google Play services), `hdmiPause` (`true` by default: a Fire TV pauses when its HDMI output goes away; `false` if a television flickers the link and the channel pauses for nothing) and `killOnExit` (default `true`: the process ends when the app is closed).

## Fire TV (Fire OS)

The same apk runs on Amazon Fire TV (sticks, Cubes and Fire TV Edition televisions). At startup it recognises one by its system feature `amazon.hardware.fire_tv` (or an `AFT...` model) and uses the Fire TV profile: the interface of the TV, no touch, remote and game-controller keys, and the memory and drawing budgets of the device. The sources of every statement below are Amazon's own pages (developer.amazon.com/docs: *Fire TV Device Specifications*, *Identify Fire TV Devices*, *Fire OS Overview*, *Remote Control Input*, *Game Controller Input*, *Controller Behavior Guidelines*, *Requirements for Multimedia Apps*, *Handling HDMI Events*, *Connect to Fire TV Through ADB*, *Display and Layout*).

### Which Fire TV can run it

| Fire OS | Android | Devices | P! |
|---|---|---|---|
| Fire OS 8 | 11 (API 30) | Stick 4K 2nd gen (`AFTKM`), Stick 4K Max 2nd gen (`AFTKRT`), Stick 4K Plus (`AFTMA08C15`), recent Fire TV Edition televisions | yes |
| Fire OS 7 | 9 (API 28) | Stick Lite and Stick HD with Alexa Voice Remote (`AFTSS`), Stick 3rd gen (`AFTSSS`), Stick 4K Max 1st gen (`AFTKA`), Cube 2nd and 3rd gen, many televisions | yes |
| Fire OS 6 | 7.1 (API 25) | Stick 4K 1st gen (`AFTMM`), Cube 1st gen (`AFTA`), Fire TV 3rd gen pendant (`AFTN`) | yes: API 25 is the app's minSdk. Weak GPUs (IMG GE8300, Mali-450): drawn at 720p |
| Fire OS 5 | 5.1 (API 22) | Stick 1st and 2nd gen / Basic Edition (`AFTM`, `AFTT`), Fire TV 1st and 2nd gen (`AFTB`, `AFTS`) | **no**: `INSTALL_FAILED_OLDER_SDK` |
| Fire OS 14 and 16 | 14 and 15 / 16 | the newest televisions | not tried; see "Not known without a device" |
| **Vega OS** | none (a Linux system) | Fire TV Stick 4K Select (2025, `AFTCA002`), Stick HD (2026, `AFTCL001`), Stick 4K 3rd gen (2026, `AFTCR001`) | **no**: it does not run Android apps, so no apk can be installed (Downloader and adb do not help) |

Look at *Settings > My Fire TV > About* for the model. Amazon documents **every Fire TV as a 32-bit device** (ABI "32-bit"), so the libraries that run are the `armeabi-v7a` ones, which the universal apk carries (with `arm64-v8a` for phones and `x86_64` for emulators). The 32-bit-only apk (`./deploy-android.sh --build-only --abi armeabi-v7a`) is 13 MB instead of 34 MB: the one to put at the Downloader's URL.

### Install

**1. Allow it and (for option B) turn on debugging.** On the Fire TV:

- *Settings > My Fire TV > About*: select the first item (the device name) **seven times** to unlock *Developer Options* (a Fire TV Edition television: *Settings > Device & Software > Developer Options*).
- *Settings > My Fire TV > Developer Options*:
  - Fire OS 7 and 8: **Install unknown apps**, and turn **Downloader** ON (the app has to be installed first, see below). Fire OS 6: **Apps from Unknown Sources** ON.
  - Option B only: **ADB debugging** ON.
- AFTVnews reported that Fire OS 7.7.1.6 and Fire OS 8.1.8.2 had a bug that kept Downloader out of the *Install unknown apps* list, fixed by a system update in September 2026: if it is missing, check for updates (*Settings > My Fire TV > About > Check for Updates*).

**2a. With the Downloader app, from a URL.**

1. Install **Downloader** (AFTVnews, free) from the Appstore: search "Downloader" on the Fire TV. Open it once and allow file access when it asks.
2. Make the apk available: `./deploy-android.sh --build-only --embed-config my-lists.json` builds it, checks it and writes `dist/P-<version>-universal-my-lists.apk` (add `--abi armeabi-v7a` for the small Fire TV-only one). An apk installed from a URL gets no pushed configuration, so `--embed-config` packages the file (`{"lists": [...]}`, kept out of the repository) as the apk's own `assets/config.json`; without it the app starts with "No hay listas configuradas". This repository and the apk contain keys of the official Ace Stream app: **do not put it where anybody can read it**. The private GitHub repository keeps the latest apk alone on the branch `apk` (one commit, replaced with each version, so the history of the code never carries binaries):

   ```
   t=$(mktemp -d) && cp dist/P-0.3.0-universal-my-lists.apk "$t/" && git -C "$t" init -q -b apk && git -C "$t" add -A \
     && git -C "$t" commit -qm "P! 0.3.0 apk" && git -C "$t" push -f git@github.com:<owner>/p.git apk
   ```

3. In Downloader, open *Browser* (menu on the left), go to `github.com` and sign in (once; the browser keeps the session). Then open `https://github.com/<owner>/p/raw/apk/P-0.3.0-universal-my-lists.apk`: the download starts and, when it ends, select *Install* (Down and Right to reach it), then *Done* (not *Open*) and delete the apk when it offers. The URL field of Downloader's home screen does not carry the browser's session: it gets "not found" for a private repository.
4. To find it: hold *Home*, choose *Apps* (it is also under *Settings > Applications > Manage Installed Applications > P! > Launch application*).

**2b. With adb from a PC** (the PC and the Fire TV on the same network; the PC needs the Android *platform-tools*):

```
# on the Fire TV: Settings > My Fire TV > About > Network shows its IP (not the one under Settings > Network)
adb connect 192.168.1.60            # a "Allow USB debugging?" box appears on the TV: tick "Always allow", OK
./deploy-android.sh --connect 192.168.1.60 --abi auto        # builds for the stick's own ABI, installs, starts
./deploy-android.sh --connect 192.168.1.60 --config my-config.json --listen fire.log   # also pushes a configuration and receives the log
adb install -r dist/P-0.3.0-universal.apk                    # or: install an apk that is already built
```

The apk is signed with the same development key every time, so installing over the old one keeps the app's data; a different key needs `--uninstall` first.

### What the remote does

Amazon: every Fire TV remote (Alexa Voice Remote, its Lite and Pro versions, the remotes of Fire TV Edition televisions, the Cube's) has the same buttons and sends the same Android keys; Home and the microphone never reach an app, and the volume, power and app buttons "cannot be mapped to events in third-party apps". The full table, with every key code, is `FireTvKeyTable` (`src/PAndroid/Pure/FireTvKeys.cs`); `tests/PAndroid.Tests/FireTvKeyTests.cs` checks each row and what the player does with it.

| Button | Android key | P! does |
|---|---|---|
| Select | `DPAD_CENTER` | OK |
| Arrows | `DPAD_UP/DOWN/LEFT/RIGHT` | move the focus; in the player with the panel hidden, left and right jump 10 s and up, down and Select show the panel |
| Back | `BACK` | back; in the player, leave it |
| Menu (three lines) | `MENU` | shows and hides the player's information panel (Amazon's "Menu shows or hides the playback chrome") |
| Play/Pause | `MEDIA_PLAY_PAUSE` | pause and resume (consumed even where it does nothing, so another app does not start: Amazon's requirement 1.6) |
| Rewind, Fast Forward | `MEDIA_REWIND`, `MEDIA_FAST_FORWARD` | 10 seconds back, 10 seconds forward (held, they repeat) |
| Channel +/- (Pro remote, television remotes) | `CHANNEL_UP`, `CHANNEL_DOWN` | next and previous channel of the list |
| Digits (a television maker's remote) | `0` to `9` | typed (Search) |
| Home, Alexa microphone, volume, mute, power, Guide, Live, Input, app buttons | none, or `VOICE_ASSIST`, `VOLUME_*`, `POWER`, `GUIDE`, `TV`, `TV_INPUT`... | **not consumed**: the system has them |
| Game controller A / B | `BUTTON_A` / `BUTTON_B` | OK / Back (their secondary `DPAD_CENTER` / `BACK` never arrive, A and B being consumed) |
| Game controller Y, Menu, Start | `BUTTON_Y`, `MENU`, `BUTTON_START` | the information panel |
| Game controller L1 / R1 | `BUTTON_L1` / `BUTTON_R1` | 10 s back / forward |
| Game controller stick presses; first generation Fire controller Play/Pause, Rewind, Fast Forward | `BUTTON_THUMBL/R`; `MEDIA_*` | pause and resume; as the remote |
| Game controller D-pad and left stick | secondary `DPAD_*` events | arrows |
| Triggers L2 / R2, X, Select, Mode | axes (volume, the system's) / none | not consumed |

The interface has no channel-change key for a remote without Channel +/-: that choice is open (see the plan: with the panel hidden, up and down could change channel).

### What P! does on a Fire TV

- **Profile** (`AndroidDeviceProfile`, from the RAM the system reports and Amazon's documented GPU class): a 1 GB stick (Lite, HD, 3rd gen) draws the interface at 1280 x 720 (the compositor scales it up; a Fire TV surface is 1080p whatever the TV is set to), keeps 16 MiB of live buffer, 32 MiB of timeshift and 24 MiB for video on demand, and animates the background at 20 frames per second (exactly three refreshes at 60 Hz); the 4K 1st gen (1.5 GB but the same GPU) draws at 720p with 24 / 48 / 32 MiB; 2 GB devices with a normal GPU (4K, 4K Max, Cubes 2nd and 3rd gen) draw at 1080p at 30 fps with the same budgets (the Cube 1st gen and the 3rd gen pendant have a Mali-450: 720p, 20 fps); 2.8 GB and up (some televisions) keep the engine's own 24 / 64 / 48 MiB. Any of it can be set in `p-config.json`: `liveBufferMiB`, `liveTimeshiftMiB`, `vodBufferMiB` and, under `platform`, `maxRenderSide` (longest side in pixels) and `ambientFps`. The log says which profile was chosen.
- **Screen:** it stays on only while a channel plays. A paused channel, or one held by Alexa's overlay, lets the screensaver start (Amazon's requirement 6.2). Fire OS 7 and later also show "Are you still watching?" after four hours without a key press (Amazon's requirements page); it can be turned off in *Settings > Preferences > Data Usage Monitoring > Still Watching* (reported by third parties; the menu varies by version).
- **Alexa:** the overlay of a voice request pauses the activity (`OnPause`) without stopping it (`OnStop`), and takes the audio focus: P! does nothing on `OnPause` and pauses the channel on a loss of the focus (a ducking asks for a quarter of the volume); when the focus comes back, it plays again, except after a permanent loss, which stays paused until the viewer presses play. Leaving for Home does stop (`OnStop`): the player and the engine are released.
- **HDMI:** if the HDMI output goes away (the TV is switched off or to another input) for 1.5 s, the channel pauses and stays paused when the link returns (Amazon's *Handling HDMI Events*; `"platform": {"hdmiPause": false}` turns it off). The startup log lists the audio outputs and the encodings each accepts.
- **Google services:** nothing in the app needs Google Play services (a Fire TV has none). The Google Cast SDK of "Enviar a la tele" is in the apk but stays behind `AndroidCast.IsEnabled`, which is false on a Fire TV (or with `"platform": {"cast": "off"}`): nothing of it starts there (the apk has no Google provider or required library: `tools/apkcheck.py` checks it). A Fire TV is a **receiver**, so it has no "Enviar a la tele" at all: no button, no search, no service.

### Audio and video

What a Fire TV decodes, from Amazon's specifications: **video** H.264 (1080p60 on the 1 GB sticks), H.265 / HEVC, MPEG-2 (1080p60, hardware), VP9; **audio** AAC (LC, HE v1 and v2), AC-3 and E-AC-3 (Dolby Digital and Plus), MP3, PCM, FLAC, Opus, Vorbis, and Dolby and DTS passthrough over HDMI on the models whose page says so. **MP2 (MPEG-1 Layer II) is on none of Amazon's specification pages** (streaming players, Cubes, Fire TV Edition televisions), and Android does not require any device to decode it: ExoPlayer's own documentation lists it only for the FFmpeg extension. Channels from digital television and many IPTV lists carry it.

What P! does about it (ExoPlayer / Media3 plays the transport stream of the engine):

- **MP2 is decoded by the app** when the device has no MP2 decoder (the check is `MediaCodecList`) or `"platform": {"softwareAudio": "force"}`: a software decoder (`Mp2Decoder`, MPEG-1 and MPEG-2 low sampling frequency, mono and stereo, joint stereo) turns each frame into PCM between ExoPlayer's TS extractor and the player (`Mp2ExtractorsFactory`). It is checked against FFmpeg's decoder to within one step of the last bit on 30 streams (`tests/PAndroid.Tests/Data/Mp2`). No native library is needed (the FFmpeg extension has to be built from source with the NDK and has no MP2 build; the MediaCodec of the system is exactly what is missing; `MediaPlayer` uses the same system decoders, so it would not help). `"softwareAudio": "off"` never uses it; if the device's own MP2 decoder fails while decoding, the app switches to the software one for the rest of the run.
- **AC-3 and E-AC-3** go to the device's decoder or to the TV over HDMI; there is no software decoder for them.
- **A track the device cannot decode** no longer leaves the channel mute without a word: a notice over the picture says "Sin sonido: este dispositivo no puede decodificar el audio AC-3 de este canal." (or "Sin imagen..." for video), on a Fire TV and on a phone. A decoder that is listed but fails to start drops the audio and says so instead of retrying for ever, and a decoder that cannot start is followed by the next one that can.
- The startup log lists every decoder (`codecs` tag): which formats the device has one for, hardware or software, the biggest picture and how many at once.

### Looking at a Fire TV from the PC

- **Log over the network:** make a file with where the PC is (`tools/p-config.firetv-log.json` is the model: `{"log": {"enabled": true, "host": "<pc-ip>", "port": 9999, "level": "info"}}`) and push it, or let the script do it, without building or installing again:

  ```
  ./deploy-android.sh --connect 192.168.1.60 --no-build --no-install --udp-log --listen fire.log
  ```

  It writes `p-config.json` into `/sdcard/Android/data/org.p.app/files/` of the stick, starts `tools/udplog.py` on the PC and restarts the app. `--clear-config` takes the file away. By hand: `adb push p-config.json /sdcard/Android/data/org.p.app/files/` and restart the app (*Settings > Applications > Manage Installed Applications > P! > Force stop*, or `adb shell am force-stop org.p.app`). The PC's IP, not a name (a stick may not resolve `.local` names).
- **logcat:** `adb connect <ip>` then `adb logcat -v time P:V AndroidRuntime:E DEBUG:E '*:S'` (`--logcat` after a deploy does the same). To keep what has happened: `adb logcat -d -v time P:V AndroidRuntime:E DEBUG:E '*:S' > p.log`.
- **What to look for** (every line has a tag): `device` (model, Fire OS build, ABIs, RAM, the profile chosen), `codecs` (what it can decode; the `can decode:` line says it at a glance), `hdmi` (the audio outputs), `keys` (every key the remote sends, also the ones left to the system: this is how the codes of an unknown remote are learned), `player` (`tracks:` the tracks of the stream and which can play, `notice:`, `started in ... ms; <video> / <audio>`), `cast` (off on a Fire TV), `ui` (`keep screen on`), `[stats]` (CPU, memory and interface frame rate every 5 s).

| Symptom | Look at / do |
|---|---|
| `INSTALL_FAILED_OLDER_SDK` | Fire OS 5 (the model is a 2nd gen stick or older): it cannot run P! |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | an apk built without `armeabi-v7a`: build without `--abi`, or with `--abi auto` |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | signed with another key: `--uninstall`, then install |
| "Install unknown apps" does not list Downloader | install Downloader first, open it once; check for a system update (see above) |
| Downloader says it cannot download, or "not found" | open the URL from Downloader's *Browser* after signing in to GitHub, not from the URL field of its home screen (see step 3) |
| The app is not in the home screen | hold *Home* > *Apps*, or *Settings > Applications > Manage Installed Applications*; `adb shell am start -n org.p.app/org.p.app.MainActivity` |
| No sound on some channels | the `tracks:` line of the log and the `can decode:` line; a notice over the picture says it too |
| Stutters | the `[stats]` lines (frame rate, CPU); try `"platform": {"maxRenderSide": 1280, "ambientFps": 20}`, a smaller `liveTimeshiftMiB` |
| It dies at startup | `adb logcat -d -b crash` and the `AndroidRuntime` / `DEBUG` lines |

### Not known without a device

What cannot be checked from the documentation and what to look at on the first run: whether Amazon's decoders for AC-3, E-AC-3 and MP2 exist (`codecs`), how ExoPlayer treats the engine's transport streams there (key frames that are not IDR, the timestamps, reopening with `?back=`: the `player` lines), how many decoders can run at once, the real memory and the point where Fire OS kills the app (the `[stats]` lines and `adb shell dumpsys meminfo org.p.app`), the frame rate of the GPU, the codes of the remote's extra buttons (`keys`), whether `MulticastLock` is needed, how the 32-bit runtime starts (time to the first frame), and how Fire OS 14 and 16 behave: Amazon says Android 16's predictive back is enforced for all apps and that the remote's Back could stop reaching `OnBackPressed` / key events; the app targets API 35 and keeps the key path, with `OnBackPressed` as a safety net.

## Configuration

`deploy.sh --config` copies a JSON file to the TV. These keys matter most:

| Key | Purpose |
|---|---|
| `lists` | Channel lists, merged into one: `[{"code": "<mylinkspaste code or M3U URL>", "name": "label"}]` |
| `searchProviders` | Extra search providers: `[{"name": "…", "url": "https://…/search?q={query}"}]` |
| `log` | UDP log: `{"enabled": true, "host": "<pc-ip>", "port": 9999, "level": "info"}`. Off by default. `deploy.sh` turns it on and points it at the PC. |
| `uploadMaxBytesPerSec`, `uploadSlots` | Upload limits. Users can also change the limit in Settings. |
| `vodBufferMiB`, `liveBufferMiB`, `liveTimeshiftMiB` | Engine memory budgets. On Android the defaults depend on the device's memory (smaller on a 1 GB stick). |
| `platform` | Options only a shell reads (see above for Android). |

Users change the rest in the app's Settings screen.

## Logs

On Android read logcat (`adb logcat -v time P:V '*:S'`). The TV blocks `dlog`, so the app sends its log over UDP; Android can do the same with `deploy-android.sh --listen`. To receive it:

```
python3 -I tools/udplog.py tv.log
```

Every few seconds a `[stats]` line reports CPU, memory and UI frame rate. Every Android start also logs the device (`device`), the decoders it has (`codecs`), the keys it receives (`keys`) and the tracks of each channel (`player`): see "Looking at a Fire TV from the PC" for turning the UDP log on with `p-config.json`.

## Sending a channel to another screen (engine side)

`AceEngine.Cast` lets a phone send the channel it has open to a TV. There is no transcoding: what the screen cannot decode is refused with the reason. `CastService` is the one entry point; the interface shows `CastService.Targets` (one list, one entry per screen) and calls `SendAsync(target, link, name)` / `Stop()`.

| Piece | What it does |
|---|---|
| `CastCompatibility` / `TsStreamProbe` | Reads the PAT/PMT, the H.264 SPS and the first audio frame header of the live buffer and says whether a Chromecast can take the channel (profile `CastProfile.Chromecast2`: H.264 up to 1080p30 and AAC) or why not (MP2, AC-3, E-AC-3, DTS, AAC LATM, MPEG-2, HEVC, more than 1080p, interlaced warning). Usually ready within the first second of data. |
| `HlsSegmenter` | Live HLS on top of the timeshift buffer: segments cut at IDR access units (about 4 s), each served as PAT + PMT + the video, the chosen audio and the PCR; a sliding playlist of 8 segments with `EXT-X-VERSION:3`, `TARGETDURATION`, `MEDIA-SEQUENCE`, `DISCONTINUITY-SEQUENCE`, `EXT-X-DISCONTINUITY` and `PROGRAM-DATE-TIME`. Segments are byte ranges of the live buffer, copied only when the receiver asks; the NAL units at the start of each access unit are put in the standard order (some encoders send the SEI before the SPS/PPS and Shaka Player, the Cast receiver's HLS player, cannot decode that), and a one-variant master playlist says the codecs. |
| `LanServer` | The HTTP server for the receiver, opened only while sending, only on the address of the interface that reaches it, with a 128-bit token in the path, connections only from the target device, `Host` check, connection limit, CORS and `OPTIONS` on the token routes, no listings. |
| `CastSource` (`ICastSource`) | Opens the channel (or takes an open session), checks it against the receiver, brings up the server and returns the URL. |
| `Dlna` | SSDP discovery of UPnP MediaRenderers, their description, AVTransport / RenderingControl / ConnectionManager over a bounded SOAP client, DIDL-Lite metadata, and per-brand variants of how to present the endless MPEG-TS (the TV's own `GetProtocolInfo` decides the order). |

The Android shell's part, for a Google Cast target (implemented: see "Send to the TV"): the Cast SDK finds the Chromecast and the shell adds it with `service.Targets.Update("googlecast", ...)` (kind `GoogleCast`, `Address` = `CastDevice.getInetAddress()`); on send, `SendAsync` returns `CastResult.Url` (the HLS playlist), `ContentType` (`application/vnd.apple.mpegurl`), `IsLive` and `Title`, and the shell does the `LOAD` with `StreamType.LIVE`. The engine has to stay alive while sending (foreground service, Wi-Fi lock). See the comment on `ICastSource` for the full contract. DLNA targets need nothing from the shell: the engine finds them (`DiscoverDlnaAsync`) and controls them.

Tools: `ace cast <link>` serves a channel on the LAN the way the app does (`--delivery hls|ts`), `ace cast-sample --lists ...` counts how many live channels a Chromecast could take, and `ace dlna-scan` lists the renderers of the network and what they say they accept (read-only: it plays nothing).

## Notes

- Opening port 8621 (TCP and UDP) to the TV on the router, or enabling UPnP on it, gives the engine more peers.
- The Samsung certificate profile expires. Renew it before it does, or the app can no longer be installed.
