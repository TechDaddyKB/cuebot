# Elgato: lights (Control Center), Camera Hub, Elgato Studio

This module covers the Streamer.bot integrations listed in `docs/parity/streamerbot-inventory.md` §3.8 under
"Camera Hub", "Control Center" and "Elgato Studio". (The Stream Deck plugin and Wave Link are separate modules.)
Clean-room: trigger and sub-action names come from the public docs at docs.streamer.bot. Wire formats come from public
community documentation and open-source projects, which were read for formats only; no code was copied.
Streamer.bot itself was not inspected.

Research was done on 2026-10-27. The Streamer.bot docs (repo `Streamerbot/docs`, `streamerbot/3.api/2.triggers/elgato/**`
and `3.api/1.sub-actions/elgato/**`) list the names. They document **no variables at all** for Control Center and
Studio ("This section is missing documentation"). They give no connection details, ports or C# methods for any of the
three. Variable names below therefore follow the Camera Hub pages where those exist, and are Cuebot's own elsewhere.

---

## 1. Lights (what Control Center manages)

### Options considered

| Option | How it works | Windows | Linux | Needs | Events | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| **Talk to each light directly (HTTP, port 9123)** | Every Key Light / Key Light Air / Mini / Ring Light / Light Strip runs a small unauthenticated REST server. It answers `GET/PUT /elgato/lights`, `GET/PUT /elgato/accessory-info` and `GET/PUT /elgato/lights/settings`, plus `POST /elgato/identify`. The lights announce themselves by mDNS as `_elg._tcp`. | ✅ | ✅ | The light on the same network | No push: poll and diff | **Chosen** |
| Control Center app API | The Control Center desktop app (Windows/macOS). No public or community-documented control interface was found (searched Elgato help and developer docs, GitHub, Bitfocus Companion). Streamer.bot 1.1α names its triggers after Control Center, and that interface is private. | ? | ❌ (no Linux app) | Control Center running | Unknown | Rejected: undocumented, and not on Linux |
| keylightd (third-party Linux daemon) | Wraps the same HTTP API behind a Unix socket or REST | ❌ | ✅ | An extra daemon | Its own | Rejected: adds a dependency on top of the same HTTP API |
| Stream Deck "Control Center" plugin | Runs inside the Stream Deck app | — | — | Stream Deck | — | Not an API Cuebot can call |

Direct-to-light covers every documented trigger:

- **Power, brightness and temperature changes**: diff successive polls.
- **Device added / removed**: discovery, manual entry, and the user removing a light.
- **Renamed**: the `displayName` from accessory-info.
- **Configuration changed**: the `/elgato/lights/settings` document.

It covers all three sub-actions too, works on Windows and Linux, and works without Control Center installed.
The cost is polling. The defaults are lights every 2 s, accessory-info and settings every 30 s, and an mDNS query every
60 s, so a change made in Control Center or the phone app shows up within about 2 s.

**Sources:**
- bitfocus/companion-module-elgato-keylight (https://github.com/bitfocus/companion-module-elgato-keylight): lights, accessory-info, identify.
- andypiper's Key Light Mini gist (https://gist.github.com/andypiper/29d7a0c70a078318e342999c52bbb2e1): accessory-info, battery-info and lights/settings bodies.
- homebridge-keylights (https://github.com/derjayjay/homebridge-keylights): `powerOnBehavior` (1 = restore last, 2 = use the configured defaults) and `switchOnDurationMs`.
- zzampax/elgato-control-center-api (https://github.com/zzampax/elgato-control-center-api): Light Strip `hue` and `saturation`.
- open_ecc (https://docs.rs/crate/open_ecc): port 9123.
- keylightd (https://jmylchreest.github.io/keylightd): mDNS `_elg._tcp`.
- RFC 6762 (mDNS) and RFC 6763 (DNS-SD).

### Wire format (as implemented in `Lights/ElgatoLightApi.cs`)

```
GET  http://<ip>:9123/elgato/lights
  -> {"numberOfLights":1,"lights":[{"on":1,"brightness":20,"temperature":213}]}
     Light Strip: {"on":1,"brightness":40,"hue":332.0,"saturation":75.0}
PUT  /elgato/lights   {"numberOfLights":1,"lights":[{"on":1}]}   (only the changed fields; answer = new state)
GET  /elgato/accessory-info
  -> {"productName":"Elgato Key Light","hardwareBoardType":53,"firmwareBuildNumber":218,"firmwareVersion":"1.0.3",
      "serialNumber":"CW12K1A00001","displayName":"","features":["lights"],"wifi-info":{...}}
PUT  /elgato/accessory-info  {"displayName":"Desk"}
GET  /elgato/lights/settings
  -> {"powerOnBehavior":1,"powerOnBrightness":20,"powerOnTemperature":213,"switchOnDurationMs":100,
      "switchOffDurationMs":300,"colorChangeDurationMs":100, ...}
POST /elgato/identify   (blinks)
```

- **Brightness** is 0-100.
- **Temperature** is in mireds, 143-344. Kelvin = 1,000,000 / value, about 7000-2900 K. Cuebot shows and accepts Kelvin, rounded to 50 K like Control Center, and also accepts raw 143-344.
- **Identity:** a light's id is its serial number, so it survives DHCP address changes. Discovery follows a known serial to a new IP address.

**mDNS** (`Lights/Mdns.cs`, no native dependencies):
- **Query:** a one-question PTR query for `_elg._tcp.local`, with the QU bit set. It is sent from an ephemeral port on every up, multicast-capable IPv4 interface to 224.0.0.251:5353. Responders answer such "one-shot" queries by unicast (RFC 6762 §5.1, §6.7).
- **Passive listener:** a shared listener on port 5353 also catches responders that multicast their answer anyway.
- **Parsing:** answers are parsed with name compression into PTR, SRV, TXT and A/AAAA records.
- **Fallback:** a responder that sends only the PTR record falls back to the packet's source address and port 9123.
- **Validation:** each light found is confirmed with GET accessory-info before it is added.

### Triggers (category "Elgato Control Center"): 9/9 + 3 extras

Every event carries these variables:
- **Identity:** `deviceId` (serial), `deviceName` (displayName, else product name), `productName`, `serialNumber`, `firmwareVersion`, `ipAddress`, `port`.
- **State:** `online`, `on`, `powerState` ("On"/"Off"), `brightness`, `temperature` (K), `temperatureRaw` (mireds), `hue`, `saturation`.
- **Settings:** `settings` (lights/settings JSON).

Streamer.bot documents no variables for these triggers, so these names are Cuebot's own. They match the Camera Hub naming style (`brightness`/`oldBrightness`).

| Streamer.bot trigger | Event type | Extra variables |
| --- | --- | --- |
| Brightness Changed | `elgato.controlCenter.brightnessChanged` | `oldBrightness` |
| Device Added | `elgato.controlCenter.deviceAdded` | none. Fires once per new light (discovered or entered); known lights are kept in elgato.json |
| Device Configuration Changed | `elgato.controlCenter.deviceConfigurationChanged` | `settings`, `oldSettings` |
| Device Power State Changed | `elgato.controlCenter.devicePowerStateChanged` | `oldOn`, `oldPowerState` |
| Device Powered Off / On | `...devicePoweredOff` / `...devicePoweredOn` | `oldOn`, `oldPowerState` |
| Device Removed | `elgato.controlCenter.deviceRemoved` | none. Fires when the user removes a light; discovery then ignores it |
| Device Renamed | `elgato.controlCenter.deviceRenamed` | `oldDeviceName` |
| Temperature Changed | `elgato.controlCenter.temperatureChanged` | `oldTemperature` (K), `oldTemperatureRaw` |
| *(extra)* Color Changed (Light Strip) | `elgato.controlCenter.colorChanged` | `oldHue`, `oldSaturation` |
| *(extra)* Device Reachable / Unreachable | `...deviceOnline` / `...deviceOffline` | none. Unreachable means 2 failed polls in a row |

Each trigger has a "Devices" filter: names, serials or IPs, comma-separated.

Changes Cuebot makes itself, through sub-actions or the UI, fire the same triggers. The PUT answer carries the new state.

### Sub-actions: 3/3 + 3 extras

All of them set the (first) light's variables above, plus `elgatoLightCount`. The Device field takes a name, serial,
IP or comma-separated list, or "All".

| Streamer.bot sub-action | Type id | Parameters |
| --- | --- | --- |
| Set Brightness | `elgato.controlCenter.setBrightness` | Device, Brightness 0-100, Adjustment (adds) |
| Set Power State | `elgato.controlCenter.setPowerState` | Device, State Off/On/Toggle |
| Set Temperature | `elgato.controlCenter.setTemperature` | Device, Temperature K 2900-7000 (or 143-344), Adjustment (adds K) |
| *(extra)* Set Color (Light Strip) | `elgato.controlCenter.setColor` | Device, Hue 0-360, Saturation 0-100 |
| *(extra)* Identify (blink) | `elgato.controlCenter.identify` | Device |
| *(extra)* Get Device Info | `elgato.controlCenter.getDevice` | Device |

**Script functions:** `elgato.lights.list`, `isRunning`, `start`, `discover`, `add`, `setPower`, `setBrightness`,
`setTemperature`, `setColor`, `identify` and `rename`.

---

## 2. Camera Hub (Facecam, Facecam Pro/MK.2/Neo, EpocCam, Prompter)

### Options considered

| Option | How it works | Windows | Linux | Verdict |
| --- | --- | --- | --- | --- |
| Camera Hub's local control interface | Elgato's own Stream Deck plugin "Camera Hub" (needs Camera Hub 1.5+) controls the app, and so does Streamer.bot since v0.2.3. The transport is **not publicly documented**. Elgato's help pages and marketplace, GitHub, Bitfocus Companion (an open request, bitfocus/companion-module-requests#679, has no answer) and the Streamer.bot docs (guide: "Configuration :wip"; no port or settings) all came up empty. Finding it would mean reverse-engineering Elgato's app or Streamer.bot, which the clean-room rule forbids. Nothing is installed here to observe. | ? | ❌ (no Camera Hub for Linux) | **Not implemented.** Can be added if Elgato documents it, or if its traffic is captured from a real install with the user's permission. |
| **UVC controls straight on the camera, Linux (V4L2)** | Facecam models are standard UVC cameras. The kernel's V4L2 API (`VIDIOC_QUERYCAP`, `VIDIOC_QUERYCTRL`, `VIDIOC_G_CTRL`, `VIDIOC_S_CTRL`; the same calls as `v4l2-ctl`) reads and writes brightness, contrast, saturation, sharpness, white balance (K) and auto white balance, power-line frequency (anti-flicker), exposure and auto exposure, focus and auto focus, pan, tilt, zoom, and h/v flip. It only needs libc `open`/`ioctl`. | — | ✅ | **Chosen for Linux.** Small, no dependencies; this is the only way to get Facecam settings on Linux, where Camera Hub does not exist. |
| UVC controls on Windows (DirectShow `IAMVideoProcAmp` / `IAMCameraControl`, or Media Foundation) | Same device controls through COM | ✅ | — | **Not implemented yet.** It can be done with built-in COM interop. It was left out because nothing here can test it, and Camera Hub (normally running alongside on Windows) re-applies its own settings, so the two would fight. A sensible next step. |
| Camera Hub Stream Deck plugin | Runs inside the Stream Deck app | — | — | Not an API Cuebot can call |

**Sources:**
- Streamer.bot docs (camera-hub triggers and sub-actions, the v0.2.3 changelog, guide/integrations/elgato-camera-hub).
- The Elgato Camera Hub plugin overview (https://help.elgato.com/hc/en-us/articles/10290602956045).
- Linux `videodev2.h` and `v4l2-controls.h`: ioctl numbers and control ids, checked in `V4l2LayoutTests`.
- bitfocus/companion-module-requests#679.

### What is implemented

The V4L2 backend is used on Linux, polled every 2 s. Changes made by other apps, such as `v4l2-ctl` or guvcview,
become triggers.

- **Device listing:** capture-capable nodes, deduplicated by `bus_info`. The id is the bus info and the name is the driver's card name.
- **Filter:** an optional name filter, for example "Elgato".
- **Permissions:** the user needs access to `/dev/video*`, which normally means the `video` group.

Units follow Streamer.bot's sub-actions:

| Control | Units |
| --- | --- |
| Brightness, contrast, saturation, focus, pan, tilt | 0-100 % of the device range |
| Zoom | 100-400 % over the device range |
| White balance | Kelvin |
| Sharpness | Device steps (Facecam: 0-4) |
| Switches | On/off |
| Shutter speed | Exposure time shown as "1/60" |

When an automatic mode is on, white-balance and shutter changes fire the "Live" triggers.

**Triggers (category "Elgato Camera Hub"): 24 of the 68 documented, plus 2.** Every webcam trigger carries
`deviceId`, `deviceName` and `devicePath`.

- **General:** Connected, Disconnected. These fire when webcam control starts or stops; the variable is `backend`.
- **Webcam controls:**

  | Trigger | Variables |
  | --- | --- |
  | Brightness Changed | `brightness`, `oldBrightness` |
  | Contrast Changed | `contrast`, `oldContrast` |
  | Saturation Changed | `saturation`, `oldSaturation` (the Streamer.bot page lists `brightness` here, a copy-paste error) |
  | Sharpness Changed | `sharpness`, `oldSharpness` |
  | Focus Changed | `focus`, `oldFocus` |
  | Pan Changed | `pan`, `oldPan` |
  | Tilt Changed | `tilt`, `oldTilt` |
  | Zoom Changed | `zoomValue`, `zoomPercent`, `oldZoomValue`, `oldZoomPercent`, `maximumZoom` |
  | White Balance Changed, Live White Balance Changed | `whiteBalance`, `oldWhiteBalance` |
  | Shutter Speed Changed, Live Shutter Speed Changed | `shutterSpeed`, `oldShutterSpeed` (+ `exposure`) |
  | Auto Focus Enabled / Disabled | `autoFocus` |
  | Auto White Balance Enabled / Disabled | `autoWhiteBalance` |
  | Auto Exposure Enabled / Disabled | `autoExposure` |
  | Anti Flicker Changed | `antiFlicker`, `oldAntiFlicker`: "Off", "50 Hz", "60 Hz", "Auto" |
  | Mirrored, Flipped, Device Orientation Changed | `mirrored`, `flipped` |

- **Extras (in the v0.2.3 changelog, no doc page):** Webcam Connected and Webcam Disconnected, for a camera plugged in or out.

**Sub-actions: 11 of the 34 documented, plus 3.** Each takes an optional Webcam (empty means the first one) and sets
`webcamValue`.

- **Number sub-actions:** these have a value plus Adjustment.
  - Webcam Set Brightness
  - Webcam Set Contrast
  - Webcam Set Saturation
  - Webcam Set Sharpness
  - Webcam Set Focus
  - Webcam Set Pan
  - Webcam Set Tilt
  - Webcam Set White Balance
  - Webcam Set Zoom
- **Webcam Set Auto White Balance:** Enabled, Disabled or Toggle.
- **Webcam Set Anti Flicker:** Off, 50 Hz, 60 Hz or Auto.
- **Extras:** Webcam Set Auto Focus, Webcam Set Auto Exposure, and Webcam Get Settings (sets the trigger variable names above).

**Script functions:** `elgato.cameraHub.isAvailable`, `elgato.cameraHub.webcams` and `elgato.cameraHub.set`.

### Not available, and why

- **Prompter: 26 triggers and 16 sub-actions.** Scripts, chapters, fonts, colours and the crosshair live inside the Camera Hub app. There is no device-level equivalent and no public API.
- **EpocCam: AR lens, bitrate, camera/lens, flash.** The phone app is controlled through Camera Hub only.
- **Webcam settings that are not standard UVC controls:**
  - Triggers: ISO and Live ISO, Overscan, Noise Reduction, Exposure Auto Lock, Bitrate, AR Lens, Lens, Flash, Webcam Activated/Deactivated (whether the feed is in use), Selected Webcam Changed (a Camera Hub selection) and Snapshot Taken.
  - Sub-actions: Set White Balance Tint, Set Dynamic Range and Set Noise Reduction.
  - Facecam may expose some of these through UVC extension units, which are vendor-specific and undocumented.
- **Windows and macOS:** the Camera Hub service reports "Not available on this system". The UI and the live test only detect whether the Camera Hub app is running.

---

## 3. Elgato Studio (capture app, 2026; recording, preview, snapshots)

### Options considered

| Option | Finding | Verdict |
| --- | --- | --- |
| A public Studio API | Elgato's Studio overview (https://www.elgato.com/us/en/explorer/products/capture/elgato-studio-software-overview/) and the 1.0 beta changelog (https://help.elgato.com/hc/en-us/articles/35382267207569) name only the **Stream Deck plugin** for remote control. That plugin covers record, screenshot, library folder, preview volume and fullscreen. No API, port or SDK is documented. Streamer.bot 1.1α lists 9 triggers and 9 sub-actions and documents no variables or connection details. | **Not implemented**: there is no public interface, and the private one may not be reverse-engineered |
| Driving the app's UI or keyboard shortcuts | Fragile, Windows-only, and gives no events | Rejected |
| Process detection | `ElgatoAppPresence` reports whether "Elgato Studio" is running | Implemented, for status only (not an `IConnectionStatus`, so the dashboard does not show a fake connection) |

None of the Studio triggers are available: Connected, Disconnected, Level Changed, Preview Fullscreen Changed, Preview
Volume Changed, Preview Volume Mute Changed, Recording Changed, Recording Finished and Snapshot Taken. None of the
sub-actions are either: Open Storage Location, Take Snapshot, Get Preview Info, Set Preview Fullscreen State, Set
Preview Mute State, Set Preview Volume, Get Last Recording Info, Get Recording Info and Set Recording State. The
script function `elgato.studio.isRunning` reports app presence.

---

## 4. Testing

**Unit tests** (`tests/Cuebot.Elgato.Tests`):

- **Fake HTTP light:** a real HTTP/1.1 Key Light API on 127.0.0.1. It covers add, poll diffs (power, brightness, temperature, rename, configuration), offline and online, commands that PUT only the changed fields, and persistence.
- **mDNS:** a hand-built DNS answer packet with compression, a hand-written packet with a question section and pointers, malformed packets, and discovery through a fake transport (including follow-the-serial and ignore-after-remove).
- **Webcams:** a fake webcam backend covers unit conversions, Live variants, plug and unplug, and the name filter.
- **V4L2 layout:** ioctl numbers checked against the `_IOC` macro, control ids, and structure offsets.
- **Registration:** DI wiring, and sub-actions run through the action engine.

**UI test:** `tests/Cuebot.UiTests/Elgato`.

**Live tests** (`tests/Cuebot.LiveTests/ElgatoLiveTests.cs`): `CUEBOT_LIVE_ELGATO_LIGHT_IP`, `CUEBOT_LIVE_CAMERAHUB=1`
and `CUEBOT_LIVE_ELGATO_STUDIO=1`.

**Untested so far** (no hardware here):
- A real light, including firmware quirks such as Light Strip scenes and Key Light Air minimum brightness.
- mDNS on a real network.
- The V4L2 backend on a real Linux camera.
- The Studio and Camera Hub process names.
