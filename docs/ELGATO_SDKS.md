# Elgato developer offerings: what Cuebot can use

Research date: 2026-10-28. Sources are Elgato's Maker Documentation (https://docs.elgato.com), the Elgato Marketplace
and Elgato help pages, plus the npm registry. Each finding below names its source. Cuebot already integrates:

- **Lights:** Key Lights and light strips over the lights' own HTTP API (`src/Cuebot.Elgato`).
- **Webcams:** Facecam settings over UVC/V4L2 on Linux.
- **Camera Hub and Elgato Studio:** detected when running, nothing more.
- **Wave Link:** `src/Cuebot.WaveLink`.
- **Stream Deck:** its own Stream Deck plugin (`streamdeck/`, `src/Cuebot.StreamDeck`).

## Summary

| Product | Official SDK / API | Would give Cuebot | Linux | Recommendation |
| --- | --- | --- | --- | --- |
| Stream Deck (app) | ✅ **Stream Deck SDK** (Node.js, `@elgato/streamdeck`, MIT); v3.0 is current, Cuebot's plugin uses v2.1 | Already used. New in v3 / recent apps: Neo Infobar actions, deep-linking, bundled profiles | ❌ (the app is Windows/macOS) | Keep. Upgrade to SDK v3 when adding Neo Infobar actions; optional pairing by deep link |
| Stream Deck (hardware) | ✅ **Stream Deck HID API**: the USB HID protocol of Classic, XL, Neo, +, + XL and Mini | Drive the deck **without the Stream Deck app**: keys, images, brightness, dials and touch, **on Linux** | ✅ (plain USB HID; works through hidraw) | **Best new opportunity** for Linux users. Build as its own feature ("headless deck"), not in this change |
| Stream Deck MCP ("MCP Actions") | ✅ Stream Deck 7.4+ bridge (`@elgato/mcp-server`) | Press any Stream Deck action from Cuebot (Streamer.bot's "Execute MCP Action") | ❌ | Already planned (streamdeck/README.md); unchanged |
| Camera Hub | ❌ **No SDK, API or plugin system.** Its only remote control is Elgato's own Stream Deck plugin, over a private channel | Dynamic range, noise reduction, white-balance tint, Prompter, EpocCam | ❌ (no Camera Hub for Linux) | **Cannot be built.** No Camera Hub plugin can be written. The three webcam controls stay ❌ |
| Wave Link | ❌ No control API or SDK. ✅ An official **audio-effects (VST) and EQ-preset** maker program | Nothing new for control. Cuebot already uses Wave Link's local WebSocket (undocumented) | ❌ (Wave Link is Windows/macOS) | Keep the current integration. VST effects do not fit Cuebot |
| Control Center (lights app) | ❌ No API. The lights' own HTTP API is undocumented but stable | Nothing beyond what Cuebot does now | ❌ app; ✅ the lights' HTTP API | Keep talking to the lights directly |
| Elgato Studio | ❌ No API; Stream Deck plugin only | Recording, snapshots, preview | ❌ | Unchanged (detected only) |
| Marketplace | ✅ Maker program: plugins, profiles, icons, screensavers, VSTs, EQ presets, OBS LUTs and backgrounds, 3D prints | Distribution for Cuebot's Stream Deck plugin | — | Optional: publish the plugin (free) after it has run against the real app |

## Stream Deck SDK (plugins)

**What exists.** Elgato's official plugin SDK is documented at https://docs.elgato.com/streamdeck/sdk/introduction/getting-started.

- **Package:** `@elgato/streamdeck` on npm. 3.0.3 is the current version, under the MIT licence. The docs for SDK 3
  ask for Node.js 24 and Stream Deck 7.1+.
- **Guides:** actions, keys, dials and touch strip, the Neo Infobar, settings, property inspectors, devices, profiles,
  embedded resources, system, deep-linking, app monitoring, logging and localisation.
- **References:** the manifest, layouts, the changelog and the WebSocket API.

**Cuebot today.** `streamdeck/` uses `@elgato/streamdeck` 2.1.2 (manifest `SDKVersion` 2, Stream Deck 7.1+). It
already supports keys and Stream Deck + dials and touch, with the `$B1` layout and dial rotate, push and touch.

**What is newer and could help:**

- **SDK 3 / Neo Infobar.** The Stream Deck Neo's info bar can host plugin actions; it needs Stream Deck 7.6+
  (https://docs.elgato.com/streamdeck/sdk/releases/upgrading/v3). Breaking changes:
  - `onDidReceiveSettings` fires only for changes from the property inspector.
  - `Action` is now a union of `KeyAction`, `DialAction` and `NeoInfobarAction`.
  - `getSecrets` was removed.

  Benefit: a "Set Value"-style readout on the Neo. Cost: a manifest and code migration, re-tested against the real app.
  **Do it together with Neo support.** It is not worth doing on its own.
- **Deep-linking.** `streamdeck://plugins/message/<plugin UUID>/...` delivers a short message (about 2,000 characters,
  local only) to the plugin; passive links need Stream Deck 7.0+
  (https://docs.elgato.com/streamdeck/sdk/guides/deep-linking). Cuebot could offer a "Pair with Stream Deck" button
  that hands the plugin the host and port, so the user does not type them. The API token should **not** go in the
  link, because URLs end up in shell and OS logs; the user would still paste it, or confirm a one-time pairing code.
  Small, nice-to-have.
- **Bundled profiles.** A plugin can ship `.streamDeckProfile` files and switch a device to them, but **only to
  profiles it ships**, not to the user's own (https://docs.elgato.com/streamdeck/sdk/guides/profiles). A ready-made
  "Cuebot" profile per device type is possible. A "switch profile" sub-action for the user's own profiles is
  **not possible** through the SDK.
- **Stream Deck 7.1 app changes:** one-click Marketplace installs, profiles that list the plugins they need, and
  devices that can be disabled so third-party software can use them directly
  (https://help.elgato.com/hc/en-us/articles/41533810232721). That last one makes the HID path below practical next to
  the app.

**Terms.** The SDK is MIT. Marketplace publishing needs a Maker organisation and the Maker Agreement. Paid listings pay
out 70/30 through Stripe Connect; free listings have no payout requirements (https://docs.elgato.com/marketplace/become-a-maker).

**Linux.** The Stream Deck app, and therefore any SDK plugin, runs only on Windows and macOS. Cuebot on Linux can use
it only with the app on another PC over the LAN, as streamdeck/README.md describes.

## Stream Deck HID API (the hardware, without the app)

**What exists.** https://docs.elgato.com/streamdeck/hid/intro documents the USB HID protocol:

- **Devices:** a "main protocol" for Classic, XL, Neo, Stream Deck + and + XL, and a legacy protocol for the Mini, each
  with its own page.
- **Workflow:** detect, open, configure, read and write reports, close.
- **Keys:** input report `0x01` carries one byte per key; Elgato suggests polling every 50 ms.
- **Images:** JPEG, sent as output reports of up to 1024 bytes in chunks with a "transfer done" flag. Rotation varies
  per model.
- **Feature reports:** brightness (`0x03`/`0x08`, 0-100), firmware versions, serial number, sleep timer, fill the LCD
  or a key with a colour, and show the logo.

Elgato says the material is meant for advanced use only, at the user's own risk. There is no separate licence; the
site's terms of use apply.

**What it would let Cuebot do.** Be the Stream Deck driver itself: draw key images from Cuebot state, react to key
presses, dials and touch, and set brightness, with no Stream Deck app. On Linux there is no Elgato app at all, and
today the only options are third-party apps (OpenDeck, StreamController), so this would be a real gain.

**Constraints.**

- The device is exclusive: either the Stream Deck app or Cuebot drives it. Since 7.1 the app can disable a device.
- Linux needs a udev rule for hidraw access (vendor 0x0fd9).
- Windows uses the HID API (`hid.dll` / SetupAPI) through P/Invoke.
- Key images must be rendered and JPEG-encoded. The app already ships Skia through Avalonia, so this can be done
  without new dependencies.
- Nothing here can be tested without hardware.

**Recommendation.** This is the most valuable Elgato item for Cuebot's Linux users. It is a feature in its own right:
a "headless deck" module with device discovery, a key and page model, image rendering, and Decks-style layouts reused
from `Cuebot.Core.Decks`. Plan it as a separate task with a real Stream Deck to test on. **Not built in this change.**

## Camera Hub

**What exists:** nothing public for developers.

- **Docs and marketplace:** Elgato's Maker Documentation has no Camera Hub section (https://docs.elgato.com lists
  Stream Deck, Wave Link, iCUE, OBS and Marketplace only). The Marketplace lists no Camera Hub plugin type.
- **Remote control:** Camera Hub is controlled from outside only by Elgato's own "Camera Hub" Stream Deck plugin
  (https://marketplace.elgato.com/product/camera-hub-c2dc2cc4-a75b-43e8-be87-6ba81cdfc325), over a private,
  undocumented channel.
- **Community:** the request at bitfocus/companion-module-requests#679 for scripting Camera Hub settings has no answer.
- **Plugins:** Camera Hub has **no plugin system**. Its "plugins" are Stream Deck plugins that run inside the Stream
  Deck app.

**Can a plugin be written for Camera Hub to expose dynamic range, noise reduction and white-balance tint?** **No.**

- Camera Hub has no plugin or extension API to write one against.
- A Stream Deck plugin cannot reach Camera Hub either: the only plugin that talks to Camera Hub is Elgato's own, and
  its protocol is private. Reverse-engineering it is ruled out by Cuebot's clean-room rule.
- On the camera, these three settings are not standard UVC controls. Standard UVC/V4L2 has no HDR, noise-reduction or
  tint control, so they are almost certainly vendor extension-unit (XU) controls.
- Elgato does not document XUs for its cameras. The Linux UVC driver can map documented XUs
  (https://docs.kernel.org/userspace-api/media/drivers/uvcvideo.html), but there is nothing documented to map.

**Recommendation.** Keep those three sub-actions ❌, with the reason now recorded in src/Cuebot.Elgato/PROTOCOL.md and
docs/PARITY.md. Two things could change this: Elgato documenting a Camera Hub API, or publishing the Facecam XU layout.
Ask Elgato (maker@elgato.com / developer support) for either. Windows UVC control through DirectShow is still the
open, documented next step for the standard controls; it is listed in the Elgato PROTOCOL.md.

**Linux.** Camera Hub does not exist for Linux, so even an official Camera Hub API would not help there. The camera's
own controls (V4L2) are the Linux path, and Cuebot uses them.

## Wave Link

**What exists:**

- **No control API:** Elgato documents none. Its Maker docs cover only **audio effects (VST plugins) and EQ presets**
  for the Marketplace (https://docs.elgato.com/wave-link/audio-effects/getting-started,
  https://docs.elgato.com/wave-link/eq-presets).
- **Undocumented WebSocket:** Wave Link's local JSON-RPC WebSocket is what Elgato's own Stream Deck plugin uses, and
  it is what Cuebot's `src/Cuebot.WaveLink` speaks. It is documented only by the community (gists and unofficial
  SDKs such as `@darrellvs/node-wave-link-sdk`).

**What it would add.** Nothing for control. Cuebot already covers Wave Link 3 and 1.x/2.x. A Cuebot VST (for example
an effect that Cuebot switches) would need a C++/JUCE plugin, Elgato review and Wave Link on Windows/macOS. It does
not fit Cuebot.

**Terms.** VSTs and EQ presets are submitted by email under the Maker Agreement.

**Linux.** Wave Link has no Linux version. Cuebot's Wave Link service reports that and stays idle.

**Recommendation.** No change. If Elgato ever publishes a Wave Link API, re-check the method names against
`src/Cuebot.WaveLink/PROTOCOL.md`.

## Control Center (Key Lights, light strips)

**What exists:** no API for the Control Center app (Windows/macOS), and no section in Elgato's Maker docs. Each light
runs its own small HTTP API (port 9123, mDNS `_elg._tcp`). Elgato has not documented it, but it is stable and widely
used, and Cuebot already talks to the lights directly on Windows and Linux, without Control Center (see
src/Cuebot.Elgato/PROTOCOL.md §1).

**Recommendation.** No change. Talking to the lights directly is better than any app API would be: it works on Linux
and needs no app running.

## Elgato Studio

There is no API; Elgato names only its Stream Deck plugin for remote control (see src/Cuebot.Elgato/PROTOCOL.md §3).
No change.

## Elgato Marketplace

**What exists.** The Maker program (https://docs.elgato.com/marketplace/become-a-maker) covers:

- **Stream Deck:** plugins, profiles, icons and screensavers.
- **Wave Link:** VST audio effects and EQ presets.
- **OBS:** LUTs and backgrounds.
- **3D-print files.**

Makers keep their IP. The split is 70/30, paid through Stripe Connect. Selling elsewhere is allowed.

**What it would give Cuebot.** A one-click install of Cuebot's Stream Deck plugin from inside the Stream Deck app (7.1+)
instead of a downloaded `.streamDeckPlugin`. A "Cuebot" icon pack or profile could follow.

**Recommendation.** Publish the plugin for free once it has been tried against the real Stream Deck app. That needs a
Maker organisation signed by the project owner, so it is not something to do from here.

## What was built in this change

**Nothing new on the Elgato side, and that is deliberate.** Two items have a clear, documented path:

- the Stream Deck SDK v3 upgrade, which pays off only with Neo Infobar support;
- the HID API, a separate feature that needs hardware to test.

Neither belongs in this change. The Camera Hub question has a clear answer (no API, so no plugin), and the docs now
say so.

The GoXLR integration built alongside this research is in `src/Cuebot.GoXlr` (see its PROTOCOL.md).
