# Cuebot Stream Deck protocol (version 1)

How Cuebot's Stream Deck plugin (`streamdeck/` in this repo) talks to Cuebot. Both ends are ours, so this is the
reference for both; the formats are covered by `tests/Cuebot.StreamDeck.Tests` (C#) and `streamdeck/test` (TypeScript).

## Transport

- WebSocket at `ws://<host>:<port>/api/v1/streamdeck` on the **REST API listener** (Servers page, default
  `127.0.0.1:7475`). The API must be on; it answers 404 otherwise.
- `Authorization: Bearer <REST API token>` on the upgrade request, checked like every other API call (401 otherwise).
  Origins follow the API rules: the plugin runs in Node and sends none.
- Text frames, one JSON object each, at most 1 MB. Unknown types and fields are ignored, so either side can add fields.
- The listener is generic: any module can register an `IApiWebSocketRoute` (`src/Cuebot.Server/Api/ApiWebSocketRoute.cs`)
  for `/api/v1/{name}`.

A connection is one Stream Deck app (one running plugin). Several may connect at once (e.g. two PCs).

## Plugin → Cuebot

| `type` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `protocol`, `plugin {uuid, version}`, `app {version, platform, language}`, `devices[]`, `buttons[]` | First message after the socket opens. Fires **Stream Deck Connected** (first hello only) and **Stream Deck Info**. |
| `devices` | `devices[]` | The device list changed. Fires **Stream Deck Info**. |
| `buttons` | `buttons[]` | Replaces the visible key list. |
| `appear` | `button` | A Cuebot key or dial became visible (or its button id changed). |
| `disappear` | `context` | It went away (page change, deleted). |
| `input` | `event`, `context`, `buttonId`, `controller`, `device`, `state?`, `ticks?`, `pressed?`, `hold?`, `tapPos?`, `args?` | A trigger key or dial was used. Fires **Stream Deck Action**. |
| `request` | `id`, `method`, `params` | See requests. |
| `ping` | | Answered with `pong`. |

`device`: `{id, name, type, size {columns, rows}, connected}` (`type` is Elgato's DeviceType number, e.g. 2 = XL, 7 = +).

`button`: `{context, buttonId, kind, controller, device, row?, column?, state, isInMultiAction}`; `kind` is `run`,
`toggle`, `global` or `trigger`; `controller` is `Keypad` or `Encoder`.

`event`: `keyDown`, `keyUp`, `dialDown`, `dialUp`, `dialRotate`, `touchTap`.

### Requests

Answered with `{type: "response", id, ok: true, result}` or `{type: "response", id, ok: false, error}`.

| `method` | `params` | `result` |
| --- | --- | --- |
| `getActions` | | `{actions: [{id, name, group, enabled}]}` |
| `runAction` | `action` (id or name), `args` (object) | as `POST /api/v1/actions/{id}/run` (queued, source "Stream Deck"); disabled actions are an error |
| `setActionEnabled` | `action`, `enabled` (`true`, `false` or `"toggle"`) | the action summary |
| `getGlobals` | | as `GET /api/v1/globals` |
| `setGlobal` | `name`, `value`, `op` (`set`, `add`, `toggle`), `persisted` (default true) | the variable; text values like `42` or `true` keep their type, `add` needs a number |

## Cuebot → plugin

| `type` | Fields | Meaning |
| --- | --- | --- |
| `welcome` | `protocol`, `session`, `app {name, version}` | Sent on connect; the plugin shows "connected" only after it. |
| `response` | see requests | |
| `command` | `command`, `buttonId`, `title?`, `state?`, `image?`, `value?`, `indicator?` | Key appearance (the Stream Deck sub-actions). Sent to **every** connected plugin; each applies it to all keys whose button id matches (case-insensitive) and remembers the latest look per id, so keys on other pages show it when they appear. |
| `actionsChanged` | `actions[]` | The action list changed (toggle keys update their state, inspectors their list). |
| `globalChanged` | `name`, `value` | A global variable changed (keys with "Show the value"). |
| `pong` | | |

Commands: `setTitle` (`title`, `state` 0/1 or absent for the current one; empty title restores the key's own),
`setState` (`state`), `setImage` (`image`: a data URI or http(s) URL, the plugin downloads URLs; Cuebot turns file paths
into data URIs; empty restores), `setValue` (`value`; dials show it on the touch strip with `indicator` 0-100, keys as
the title), `showAlert`, `showOk` (keys only), `toggleState`.

## Direct USB (HID) mode

Besides the plugin, Cuebot can drive a Stream Deck itself over USB HID, with no Elgato software (the Stream Deck app
does not exist on Linux). Code: `src/Cuebot.StreamDeck/Hid/`; settings: `streamdeck-usb.json`; dashboard line
"Stream Deck (USB)"; the Stream Deck page has a "Direct USB" card for on/off, brightness and the key mapping.

**Sources.** Only public, open-source knowledge: the MIT-licensed
[python-elgato-streamdeck](https://github.com/abcminiuser/python-elgato-streamdeck) library (`Devices/StreamDeck*.py`,
`ProductIDs.py`, read 2026-10-28; no code copied) and the Linux `hidraw.h` header. Verified on a real Stream Deck XL
(0fd9:006c, firmware 1.01.000) with `tests/Cuebot.LiveTests/StreamDeckHidLiveTests.cs`.

### Devices

`StreamDeckHidModels.All` is the device table: one data entry per model (USB product ids, Elgato device type, key
count/layout, key image size, format, flip/rotation, report sizes and offsets, input layout, dials). Vendor id 0x0FD9.

| Model | Product ids | Keys | Key image | Driven |
| --- | --- | --- | --- | --- |
| Stream Deck XL (and v2, module) | 0x006C, 0x008F, 0x00BA | 32 (8×4) | 96×96 JPEG, flipped both ways | ✅ tested on hardware |
| Stream Deck MK.2 (and Scissor, module) | 0x0080, 0x00A5, 0x00B9 | 15 (5×3) | 72×72 JPEG, flipped both ways | ✅ (same format, untested) |
| Stream Deck Original V2 | 0x006D | 15 (5×3) | 72×72 JPEG, flipped both ways | ✅ (untested) |
| Stream Deck Neo | 0x009A | 8 (4×2) + 2 touch keys | 96×96 JPEG, flipped both ways | ✅ keys and touch keys (info screen not drawn; untested) |
| Stream Deck + | 0x0084 | 8 (4×2) + 4 dials + touch strip | 120×120 JPEG, not flipped | ✅ keys, dials, touch taps (touch strip not drawn; untested) |
| Stream Deck Pedal | 0x0086 | 3 pedals | none | ✅ (untested) |
| Stream Deck (2017), Mini, Mini MK.2 | 0x0060, 0x0063, 0x00B3, 0x0090, 0x00B8 | | BMP, older report format | ❌ recognized, status says "not supported yet" |
| Stream Deck Studio, + XL | 0x00AA, 0x00C6 | | not described yet | ❌ recognized, status says "not supported yet" |

### Reports (gen 2)

Every report starts with its report id, as written to and read from hidraw.

| What | Report | Layout |
| --- | --- | --- |
| Key states | input 0x01 | `01 ?? ?? ??` then one byte per key (1 = pressed), keys left to right, top to bottom from 0 |
| Stream Deck + input | input 0x01, 14 bytes | byte 1: `00` keys (states from byte 4), `03` dials (byte 4: `00` press states / `01` turn ticks as signed bytes, from byte 5), `02` touch (byte 4: 1 short, 2 long, 3 drag; x, y LE16 at 6, 8; drag end at 10, 12) |
| Key image | output 0x02, 1024 bytes | `02 07 <key> <last 0/1> <len LE16> <page LE16>` + up to 1016 bytes of the encoded image, zero-padded; pages from 0 |
| Abort an image stream | output 0x02, 1024 bytes | `02` and zeros (sent when a deck is opened) |
| Brightness | feature 0x03, 32 bytes | `03 08 <percent>` |
| Reset | feature 0x03, 32 bytes | `03 02`: clears the keys, shows the boot screen |
| Firmware | get feature 0x05, 32 bytes | text from byte 6 (+: byte 5), NUL-terminated |
| Serial | get feature 0x06, 32 bytes | text from byte 2 (+: byte 5), NUL-terminated |

Key images are drawn with SkiaSharp at the key size (picture scaled to fit, title with an outline, centred or at the
bottom over a picture; Show Alert / Show Ok overlays for 1.5 s), rotated and mirrored as the model needs, and encoded as
JPEG (quality 90). Pictures: data URIs, files, file:// and http(s) URLs (15 s timeout, 4 MB limit); SVG is not supported.

### Transport

`IHidTransport` / `IHidDevice` (enumerate, open, read input with a timeout, write output, get/set feature) keep the
protocol testable with a fake. Linux (`LinuxHidTransport`) uses only libc:

- Enumeration: `/sys/class/hidraw/*/device/uevent` (`HID_ID=0003:<vendor>:<product>`, `HID_NAME`, `HID_UNIQ` = serial);
  the USB port comes from the hidraw link (…/`<usb>`/`<interface>`/`<hid>`/hidraw/hidrawN).
- USB devices of vendor 0fd9 under `/sys/bus/usb/devices` without a hidraw node are reported as "on USB, but another
  program has taken it over": StreamController and other libusb programs detach the kernel HID driver. The node of a
  deck that another program has open (found in `/proc/*/fd`, own user only) is not opened: the status names the program.
- I/O: `open(O_RDWR|O_CLOEXEC)`, `poll` + `read` for input (200 ms timeout, so closing is prompt), `write`, and
  `ioctl` `HIDIOCGFEATURE(len)` = `_IOC(_IOC_READ|_IOC_WRITE, 'H', 0x07, len)` / `HIDIOCSFEATURE(len)` (… 0x06 …).
  `ENODEV`/`EIO`/`POLLHUP` mean unplugged. `EACCES` means no permission: on systems without systemd-logind `uaccess`
  install `packaging/linux/70-cuebot-streamdeck.rules`.
- Hot-plug: USB is scanned every 2 s (`ScanIntervalMs`); no udev dependency.

Windows and macOS: not built yet; the status says so and the plugin remains the way there.

### Triggers, sub-actions, settings

- **Stream Deck Action** fires for `keyDown`/`keyUp`, `dialDown`/`dialUp`/`dialRotate` (`ticks`, `pressed`) and
  `touchTap` (`tapX`, `tapY`, `hold`; the strip is split into one segment per dial) with the plugin's argument names
  (`buttonId`, `buttonEvent`, `controller`, `isDial`, `context`, `deviceId` = serial, `deviceName`, `deviceType`, `row`,
  `column`, `state`, `instanceId` = `usb:<serial>`) plus `keyIndex` and `connection` = `usb`.
- **Stream Deck Connected / Disconnected / Info** fire per deck when it is opened or lost (unplugged, turned off), with
  `deviceId`, `deviceName`, `deviceType`, `serial`, `firmware`, `connection` = `usb`, and Info's `device0.*` /
  `data.devices[0].*`. Shutting Cuebot down fires nothing.
- The **Stream Deck sub-actions** (and `streamdeck.*` script functions) go through `StreamDeckHub.SendCommandAsync`,
  which also hands every command to the direct USB service (`IStreamDeckKeyTarget`); the count it returns includes USB
  keys. Titles and images are kept per button id and per state (0/1), so keys sharing an id change together and a deck
  plugged in later shows them; an empty title or image restores the key's own (from the settings). `setValue` sets the
  title of the current state.
- **Button ids**: per deck serial, `keys` / `dials` entries `{index, buttonId, title, image}`; unmapped keys use
  `hid-<serial>-<index>` (dials `hid-<serial>-dial<index>`; serial reduced to letters, digits, `-`, `_`).

```json
{
  "enabled": true, "brightness": 60, "scanIntervalMs": 2000, "resetOnExit": true,
  "devices": [{ "serial": "CL50K2A01783", "name": "Desk XL", "brightness": null,
                "keys": [{ "index": 0, "buttonId": "go-live", "title": "LIVE", "image": "/home/me/live.png" }], "dials": [] }]
}
```

On exit (and when turned off) decks are reset and set to the configured brightness, so another program can take over.

## Versioning

`protocol` is 1. A future incompatible change bumps it; the plugin then says "update the plugin" in its status line.
