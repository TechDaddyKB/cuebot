# Game-controller input (Cuebot.Controllers)

Streamer.bot 1.1 (alpha) added controller input to its InputService through SDL2, with a community
`gamecontroller.txt` (SDL_GameControllerDB) for unusual devices. Its public docs and changelog name three events
(`InputControllerButton`, `InputControllerAxis`, `InputControllerHat`) and WebSocket payloads for button and hat input,
but publish no trigger settings or variables. Everything below the event names is therefore Cuebot's own design.

## Library choice

| Option | Verdict |
| --- | --- |
| **SDL3** (chosen) | Current SDL (3.2 stable since January 2025, 3.4.x in 2026). One API for Xbox (XInput / raw input), PlayStation 3/4/5, Switch Pro / Joy-Con, GameCube adapters, Steam controllers and plain HID joysticks, with SDL's built-in mapping database plus `SDL_AddGamepadMappingsFromFile`. Background input is a documented hint. Hot-plug, battery, rumble and LEDs work the same on Windows and Linux. zlib licence. |
| SDL2 | What Streamer.bot uses. In maintenance mode; SDL3 has the newer HIDAPI drivers (DualSense Edge, Switch 2, newer Xbox firmware), a clearer gamepad API and `bool` returns. SDL2's mapping file format is read unchanged by SDL3, so Streamer.bot users' `gamecontroller.txt` keeps working. |
| Windows.Gaming.Input / XInput | Windows only, no Linux path; XInput is Xbox-only (4 pads, no PlayStation/Switch). SDL already uses XInput and raw input underneath on Windows. |
| GameInput | Windows 10/11 only, extra redistributable. |
| Linux evdev / hidraw directly | Linux only, and we would re-implement SDL's per-controller HID drivers and mappings. |

**Bindings.** `ppy.SDL3-CS` (MIT, published by ppy for osu!) is used **only for its native libraries**
(`runtimes/{win-x64,win-arm64,win-x86,linux-x64,linux-arm64,linux-x86,linux-arm}/native`); the PackageReference sets
`ExcludeAssets="compile;runtime"`, so its generated managed bindings are neither compiled against nor shipped. Cuebot
declares the ~45 SDL functions it needs itself (`Device/Sdl/SdlNative.cs`, plain blittable `DllImport`s, no unsafe
code), taken from the public SDL3 headers. The bundled build reports `SDL 3.5.0 (SDL-3.5.0-ea7a2da)` (ppy builds from
SDL's main branch); the API used exists since SDL 3.2.0, so any SDL3 ≥ 3.2 works.

**Native loading** (`SdlNative.InstallResolver`): `CUEBOT_SDL3_LIBRARY` (full path, for testing a different build) →
the copy next to Cuebot (`SDL3.dll` / `libSDL3.so`, from the package) → the system library (`libSDL3.so.0`, e.g. Arch
`sdl3`, Debian/Ubuntu `libsdl3-0`). The Linux library's only hard dependencies are glibc ≥ 2.34 (`libc.so.6`,
`libm.so.6`); udev, D-Bus, libusb etc. are loaded on demand with `dlopen`, so it loads from the self-contained zip,
AppImage, Flatpak (freedesktop 25.08 runtime) and AUR builds. If it cannot load, the service status is
"Controller input is unavailable: …" and nothing else is affected (`SdlNative.Probe` checks before the first call).

## Threading

`SdlControllerBackend` owns one background thread (`Cuebot controllers (SDL3)`). On that thread only it sets the hints,
calls `SDL_Init(SDL_INIT_GAMEPAD | SDL_INIT_JOYSTICK)`, polls `SDL_PollEvent` every 5 ms (OS timer resolution permitting),
runs queued commands (rumble, LED, mappings) and finally closes the devices and calls `SDL_Quit`. SDL's **video**
subsystem is never initialised and no SDL window is created, so SDL does not inhibit the screensaver or sleep (the SDL2
bug Streamer.bot fixed in its alpha), does not touch Avalonia's event loop and needs no main thread. Hints:

| Hint | Why |
| --- | --- |
| `SDL_NO_SIGNAL_HANDLERS=1` | .NET owns SIGINT/SIGTERM. |
| `SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS=1` | Input while Cuebot is not focused (SDL also never drops it when it has no windows). |
| `SDL_JOYSTICK_THREAD=1` | Windows raw-input device messages on SDL's own helper thread. |
| `SDL_APP_NAME=Cuebot` | Shown by some drivers / portals. |

Backend events are raised on the SDL thread; `ControllerService` processes them under a lock and publishes bus
events after releasing it. Axis events that were throttled are flushed by a timer.

**When it runs.** `ControllerService` is an `IPeriodicService`: each core timer tick it starts SDL only when
`Mode` is `Always`, or (`Automatic`, the default) when an enabled action has an enabled controller trigger, the
Controllers page is on screen (`Hold()`), or a sub-action/script/learn used controllers in the last 5 minutes. `Off`
never starts it. A failed start is not retried until Retry (`Restart()`), so a missing library logs once.

## Platforms

- **Windows**: XInput, raw input and HIDAPI (PlayStation/Switch over USB and Bluetooth). No driver or admin rights.
- **Linux**: evdev (`/dev/input/event*`, readable by the logged-in user via systemd-logind's `uaccess` tag on
  joysticks) plus HIDAPI over `/dev/hidraw*` for PlayStation/Switch extras (LED colour, rumble on Switch, player LEDs).
  hidraw nodes are root-only unless udev rules grant access: install `steam-devices` (Debian/Ubuntu/Fedora) or
  `game-devices-udev` (AUR). Without them those controllers still work through the kernel drivers (`hid-playstation`,
  `hid-nintendo`) over evdev. Works the same under Wayland and X11: evdev is not tied to the display server.
- **Flatpak**: `--device=all` (in the manifest) exposes `/dev/input` and `/dev/hidraw*`; the narrower
  `--device=input` needs Flatpak ≥ 1.15.6 (Ubuntu 24.04 ships 1.14) and does not cover hidraw. In the sandbox SDL
  finds hot-plugged devices with inotify instead of udev.
- **AppImage / zip / AUR**: the bundled `libSDL3.so` is next to the executable; AUR source builds use the
  `ppy.SDL3-CS` native asset for their RID.

## Mapping

Gamepads (devices SDL has a mapping for) use SDL's standard layout, named by **position** so one trigger works on every
pad: `South East West North Back Guide Start LeftStick RightStick LeftShoulder RightShoulder DPadUp DPadDown DPadLeft
DPadRight Misc1 RightPaddle1 LeftPaddle1 RightPaddle2 LeftPaddle2 Touchpad Misc2..Misc6`, axes
`LeftX LeftY RightX RightY LeftTrigger RightTrigger` (Y positive = down). `buttonLabel` is the printed label for the
controller family (Xbox A/B/X/Y, View/Menu; PlayStation Cross/Circle/Square/Triangle, Create/Options; Nintendo B/A/Y/X,
Minus/Plus). Filters accept positional names, the controller's own labels and common aliases (LB, L1, RT, R2, Select,
Options, Home, …); a single-letter label only matches that controller's own label, so "A" means South on Xbox and East on
Nintendo. Devices without a mapping (flight sticks, wheels, button boxes) are reported with raw `Button1..`, `Axis1..`,
`Hat1..` (1-based names; the 0-based number is in `buttonIndex`/`axisIndex`/`hatIndex`). A gamepad's D-pad is also
reported as `Hat1` (setting `DPadAsHat`, default on).

**gamecontrollerdb.** `MappingsFile` (a path, or a file name in the data folder) is loaded after `SDL_Init` with
`SDL_AddGamepadMappingsFromFile`; when empty, `gamecontrollerdb.txt` or Streamer.bot's `gamecontroller.txt` in the data
folder is used if present. "Reload" adds it again at runtime; SDL then sends `GAMEPAD_REMAPPED` (or `GAMEPAD_ADDED` for a
joystick that just became a gamepad) and the device's names update in place.

**Values.** Sticks and joystick axes are scaled to −1..1, triggers to 0..1. A scaled deadzone (`StickDeadzone` 15 %,
`TriggerDeadzone` 5 %) maps the dead band to 0 and stretches the rest so full travel is still ±1. An axis event is sent
when the value moved at least `AxisMinChange` (2 %), or reached 0 or ±1, and at most once per `AxisThrottleMs` (50 ms)
per axis; the last value of a movement is always delivered (trailing flush). Slots (`controllerIndex`) are the lowest free
number from 1 at connect time and stay fixed while connected.

## Events and variables

Every controller event carries: `controllerName`, `controllerId` (SDL instance id; new on every reconnect),
`controllerGuid`, `controllerIndex` (slot), `controllerType` (Xbox 360, Xbox One, PS3, PS4, PS5, Switch Pro,
Joy-Con (L)/(R)/Pair, GameCube, Steam, Standard, Unknown, Joystick), `controllerIsGamepad`, `controllerPlayerIndex`,
`controllerVendorId`/`controllerProductId` (hex), `controllerSerial`, `controllerCanRumble`, `controllerHasLed`,
`batteryState`, `batteryPercent` (−1 = unknown).

| Event | Extra variables |
| --- | --- |
| `controller.button` | `inputType`=Button, `button`, `buttonLabel`, `buttonIndex`, `pressed`, `buttonState` (Pressed/Released), `heldButtons` (comma list), `heldButtonCount`, `holdDurationMs` (on release) |
| `controller.axis` | `inputType`=Axis, `axis`, `axisIndex`, `axisValue` (−1..1 / 0..1), `axisPercent`, `axisRaw` (−32768..32767), `axisPrevious`, `axisDirection` (Positive/Negative/Centered), `isTrigger` |
| `controller.hat` | `inputType`=Hat, `hat`, `hatIndex`, `hatValue` (bitmask 1 up, 2 right, 4 down, 8 left), `hatDirection`, `hatPrevious`, `hatPreviousDirection`, `isDPad` |
| `controller.connected` / `controller.disconnected` | `controllerCount` |

## Triggers (category "Game Controller")

| Trigger | Settings | Parity |
| --- | --- | --- |
| Controller Button | controller, button (Any / list), When (Pressed, Released, Both) | Streamer.bot InputControllerButton |
| Controller Axis | controller, axis, direction (Either/Positive/Negative), threshold %, Fire (Enter, Exit, While beyond, Any movement) | InputControllerAxis |
| Controller Hat / D-Pad | controller, hat, direction (Any, 8 directions, Centered); cardinal directions include diagonals and fire when they become active | InputControllerHat |
| Controller Button Combo | controller, buttons (`LB+RB`), no other buttons held | Cuebot extra |
| Controller Connected / Disconnected | controller | Cuebot extra |

Controller filter: empty/Any, or comma-separated `#slot`, part of the name, type, or GUID.

**Learn.** The generic trigger editor has no capture hook, so (1) the Button and Axis fields suggest the most recently
used inputs first, and (2) the Controllers page has a Learn box (also `controllers.learn` for scripts) that waits for the
next press / half-way stick movement / hat direction and prints the matching trigger settings.

## Sub-actions and scripts (all Cuebot extras)

Sub-actions: Rumble (low/high motor %, duration), Set LED Colour (`#RRGGBB` / `r,g,b`), Get Controller State
(`controllerConnected`, identity variables, `heldButtons`, `axisLeftX`…, `hat1`…), Get Controllers (`controllerCount`,
`controllerNames`, `controller<N>Name/Index/Type/Guid`). Rumble and LED set `controllersAffected` and do not fail when no
controller matches. Script functions: `controllers.status`, `controllers.list`, `controllers.get`, `controllers.rumble`,
`controllers.setLed`, `controllers.learn`, `controllers.reloadMappings`.

## Testing

`tests/Cuebot.Controllers.Tests` drives everything through `IControllerBackend` fakes (no SDL, no devices), plus SDL
backend paths that never load the library (missing library, struct layout). `tests/Cuebot.LiveTests`
`ControllerLiveTests` (`CUEBOT_LIVE_CONTROLLER=1`) starts the real SDL3 and lists controllers; with
`CUEBOT_LIVE_CONTROLLER_WAIT_SECONDS` it waits for one input.
