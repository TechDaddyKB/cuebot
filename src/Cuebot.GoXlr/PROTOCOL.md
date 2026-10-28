# GoXLR (through the GoXLR Utility)

A Cuebot extra: Streamer.bot has no GoXLR integration, so nothing here follows Streamer.bot names. Research was done
on 2026-10-28. The wire format comes from the GoXLR Utility's own MIT-licensed source
(github.com/GoXLR-on-Linux/goxlr-utility: `ipc/src/lib.rs`, `ipc/src/device.rs`, `types/src/lib.rs`,
`daemon/src/servers/http_server.rs`, `daemon/src/servers/server_packet.rs`, `daemon/src/cli.rs`). It was read for the
protocol only; no code was copied.

## Options considered

| Option | How it works | Windows | Linux | macOS | Verdict |
| --- | --- | --- | --- | --- | --- |
| **GoXLR Utility HTTP + WebSocket API** | The open-source utility replaces TC-Helicon's app and drives the GoXLR over USB. Its daemon serves a web UI and a JSON API on `localhost:14564`: `POST /api/command`, `GET /api/get-devices` and the websocket `GET /api/websocket` (requests with ids, plus JSON-patch status updates pushed to every client). Its own web UI and its Stream Deck plugin use the same API. | ✅ | ✅ | ✅ | **Chosen**: documented by its source, the one cross-platform path, and it pushes every change |
| TC-Helicon GoXLR App | Windows only. No public API. Its Stream Deck integration is an undocumented private websocket in which the App connects out to the Stream Deck plugin on port 6805 (known only from community reverse-engineering, e.g. goxlr-bridge, the TouchPortal and Loupedeck plugins). It covers routing and profiles, and the App and the GoXLR Utility cannot run at the same time. | ✅ | ❌ | ❌ | Rejected: undocumented, Windows only, and it means pretending to be the Stream Deck plugin |
| Talking to the GoXLR over USB directly | Reimplements the utility's USB protocol | — | — | — | Rejected: the utility already owns the device, and two drivers would fight over it |

## Transport

- **WebSocket** `ws://<host>:<port>/api/websocket`. Host `localhost` by default. The utility binds to `localhost`,
  or to `0.0.0.0` when "Allow network access" is on in its settings (or `--http-bind-address`). Port `14564`
  (`--http-port`). There is no authentication. Text frames only: a binary frame makes the utility close the socket.
- **Request:** `{"id": <u64>, "data": <DaemonRequest>}`. **Answer:** `{"id": <same id>, "data": <DaemonResponse>}`.
  If a message is JSON with an `id` but not a valid request, the answer is an `Error`. Without an id, or when the
  message is not JSON, the utility closes the socket.
- **Status updates:** after any change (hardware, its UI, another client, Cuebot) the daemon diffs the old and new
  status and sends `{"id": 18446744073709551615, "data": {"Patch": [<RFC 6902 operations>]}}` to every websocket client.
  The patches come from the json-patch crate's `diff`, which uses `add`, `remove` and `replace`. Cuebot's
  `Protocol/JsonPatch.cs` implements all six operations.

Serde's default "externally tagged" enum encoding is used throughout:

| Rust | JSON |
| --- | --- |
| Unit variant `Ping` | `"Ping"` |
| One field `SetFXEnabled(bool)` | `{"SetFXEnabled": true}` |
| Several fields `SetVolume(ChannelName, u8)` | `{"SetVolume": ["Mic", 255]}` |
| No fields `SaveProfile()` | `{"SaveProfile": []}` |
| `Option<String>` | the string or `null` |

`DaemonRequest`: `Ping`, `GetStatus`, `Daemon(DaemonCommand)`, `GetMicLevel(serial)`, `Command(serial, GoXLRCommand)`,
and firmware requests. `DaemonResponse`: `"Ok"`, `{"Error": "..."}`, `{"MicLevel": f64}`, `{"Status": DaemonStatus}`,
`{"Patch": [...]}`.

```
→ {"id":1,"data":"GetStatus"}
← {"id":1,"data":{"Status":{"config":{...},"mixers":{"S210500000ABC":{...}},"paths":{...},"files":{...}}}}
→ {"id":2,"data":{"Command":["S210500000ABC",{"SetFaderMuteState":["B","MutedToX"]}]}}
← {"id":2,"data":"Ok"}
← {"id":18446744073709551615,"data":{"Patch":[{"op":"replace","path":"/mixers/S210500000ABC/fader_status/B/mute_state","value":"MutedToX"}]}}
```

### Status (`DaemonStatus`, snake_case fields; what Cuebot reads)

- `config.daemon_version`
- `files.profiles`, `files.mic_profiles` and `files.presets` (lists of names); `files.samples` (an object keyed by file name)
- `mixers.<serial>`: one entry per GoXLR. Cuebot reads these fields from it:
  - `hardware.device_type`: `Full`, `Mini` or `Unknown`
  - `profile_name`, `mic_profile_name`
  - `fader_status.{A,B,C,D}`: `channel`, `mute_type` (MuteFunction), `mute_state` (`Unmuted` / `MutedToX` / `MutedToAll`), `scribble.bottom_text`
  - `levels.volumes.<ChannelName>`: 0-255
  - `cough_button`: `is_toggle`, `mute_type`, `state` (MuteState)
  - `effects` (null on the Mini): `is_enabled` (the FX switch), `active_preset` (`Preset1`..`Preset6`), `preset_names`, and `current.{megaphone,robot,hard_tune}.is_enabled`
  - `sampler` (null on the Mini): `active_bank` (`A`/`B`/`C`), and `banks.<bank>.<TopLeft|TopRight|BottomLeft|BottomRight>` with `is_playing` and `samples`
  - `button_down.<Button>`: bool for each hardware button
  - `router.<InputDevice>.<OutputDevice>`: bool
  - `mic_status.mic_type`

**Enum names:**

| Enum | Values |
| --- | --- |
| ChannelName | Mic, LineIn, Console, System, Game, Chat, Sample, Music, Headphones, MicMonitor, LineOut |
| FaderName | A-D |
| MuteFunction | All, ToStream, ToVoiceChat, ToPhones, ToLineOut, ToStream2, ToStreams |
| InputDevice | Microphone, Chat, Music, Game, Console, LineIn, System, Samples |
| OutputDevice | Headphones, BroadcastMix, ChatMic, Sampler, LineOut, StreamMix2 |
| Button | Fader1Mute..Fader4Mute, Bleep, Cough, EffectSelect1..6, EffectFx, EffectMegaphone, EffectRobot, EffectHardTune, SamplerSelectA..C, SamplerTopLeft/TopRight/BottomLeft/BottomRight, SamplerClear |
| AnimationMode | RetroRainbow, RainbowDark, RainbowBright, Simple, Ripple, None |

## How Cuebot uses it (`GoXlrService`)

1. Connect the websocket, send `GetStatus`, and keep the status as a JSON document.
2. Apply each patch to that document, re-read the typed snapshot (`GoXlrStatusParser`), and compare it with the
   previous one per serial (`GoXlrDiff`). Because snapshots are compared, a patch that replaces a whole section (loading
   a profile) fires exactly the events for what changed. A patch that does not apply (for example a race with the
   initial status) makes Cuebot reload the status with `GetStatus`.
3. Commands go out as `Command(serial, ...)`. The serial is the one set on the GoXLR page, or else the first GoXLR. The
   resulting state change arrives as a patch like any other.
4. Reconnects use exponential backoff (2 s doubling to 30 s) while auto-reconnect is on. Auto-connect is off by default.

Volumes are 0-255 on the wire. Cuebot shows and accepts 0-100 % (`raw = round(% × 2.55)`), and events also carry the raw value.

## Triggers (category "GoXLR")

Every device event carries `goXlrSerial`, `goXlrDeviceType`, `goXlrProfile` and `goXlrMicProfile`. Every device
trigger has a Devices (serial) filter.

| Trigger | Event type | Variables | Filters |
| --- | --- | --- | --- |
| Utility connected / disconnected | `goxlr.connected` / `goxlr.disconnected` | `goXlrDaemonVersion`, `goXlrDeviceCount` / `reason` | none |
| Device connected / disconnected | `goxlr.deviceConnected` / `goxlr.deviceDisconnected` | the device variables | serial |
| Volume changed | `goxlr.volumeChanged` | `goXlrChannel`, `goXlrChannelName`, `goXlrFader` ("" if not on a fader), `goXlrVolume` (%), `goXlrVolumeRaw`, `goXlrPreviousVolume`, `goXlrPreviousVolumeRaw` | channels |
| Fader channel changed | `goxlr.faderAssignmentChanged` | `goXlrFader`, `goXlrChannel`, `goXlrPreviousChannel`, mute fields | faders, channels |
| Fader mute changed | `goxlr.faderMuteChanged` | `goXlrFader`, `goXlrChannel`, `goXlrMuteState`, `goXlrMuted`, `goXlrPreviousMuteState`, `goXlrWasMuted`, `goXlrMuteFunction`, `goXlrVolume` | faders, channels, Muted/Unmuted |
| Cough / mic mute changed | `goxlr.coughMuteChanged` | `goXlrMuteState`, `goXlrMuted`, `goXlrPreviousMuteState`, `goXlrMuteFunction` | Muted/Unmuted |
| Profile changed / Mic profile changed | `goxlr.profileChanged` / `goxlr.micProfileChanged` | `goXlrPreviousProfile` / `goXlrPreviousMicProfile` | names |
| Effect turned on/off | `goxlr.effectStateChanged` | `goXlrEffect` (FX, Megaphone, Robot, HardTune), `goXlrEffectName`, `goXlrEnabled` | effects, On/Off |
| Effect preset changed | `goxlr.effectPresetChanged` | `goXlrPreset`, `goXlrPresetNumber`, `goXlrPresetName`, `goXlrPreviousPreset`, `goXlrPreviousPresetName` | numbers or names |
| Sampler bank changed | `goxlr.samplerBankChanged` | `goXlrBank`, `goXlrPreviousBank` | banks |
| Sample started/stopped | `goxlr.samplePlaybackChanged` | `goXlrBank`, `goXlrSampleButton`, `goXlrPlaying` | banks, buttons, Playing/Stopped |
| Button pressed / released | `goxlr.buttonPressed` / `goxlr.buttonReleased` | `goXlrButton`, `goXlrButtonName`, `goXlrPressed` | buttons |
| Routing changed | `goxlr.routingChanged` | `goXlrInput`, `goXlrOutput`, `goXlrEnabled` | inputs, outputs, Routed/Not routed |

A physical fader move produces a stream of volume events, about one per status patch.

## Sub-actions (category "GoXLR")

| Sub-action | Type id | Command(s) |
| --- | --- | --- |
| Set volume (0-100 %, relative) | `goxlr.setVolume` | `SetVolume(channel, 0-255)` |
| Mute fader (A-D or the channel on it; Mute / Mute to all / Unmute / Toggle) | `goxlr.setFaderMute` | `SetFaderMuteState(fader, MuteState)` |
| Set fader channel | `goxlr.setFaderChannel` | `SetFader(fader, channel)` |
| Set fader mute function | `goxlr.setFaderMuteFunction` | `SetFaderMuteFunction` |
| Cough / mic mute | `goxlr.setCoughMute` | `SetCoughMuteState` |
| Load profile / Load mic profile (Remember) | `goxlr.loadProfile` / `goxlr.loadMicProfile` | `LoadProfile(name, remember)` / `LoadMicProfile` |
| Set effect on/off (FX, Megaphone, Robot, Hard Tune) | `goxlr.setEffect` | `SetFXEnabled` / `SetMegaphoneEnabled` / `SetRobotEnabled` / `SetHardTuneEnabled` |
| Select effect preset (1-6 or name) | `goxlr.setEffectPreset` | `SetActiveEffectPreset` |
| Load effect preset from library | `goxlr.loadEffectPreset` | `LoadEffectPreset(name)` |
| Select sampler bank | `goxlr.setSamplerBank` | `SetActiveSamplerBank` |
| Play sample (next / by index / stop) | `goxlr.playSample` | `PlayNextSample` / `PlaySampleByIndex` / `StopSamplePlayback` |
| Set routing | `goxlr.setRouting` | `SetRouter(input, output, bool)` |
| Set global colour / fader colours / button colours | `goxlr.setGlobalColour` / `goxlr.setFaderColours` / `goxlr.setButtonColours` | `SetGlobalColour`, `SetFaderColours`, `SetButtonColours` (colours as 6 hex digits, no `#`) |
| Set lighting animation | `goxlr.setAnimationMode` | `SetAnimationMode` (firmware with animation support) |
| Set scribble strip text | `goxlr.setScribbleText` | `SetScribbleText` (full-size GoXLR) |
| Get status | `goxlr.getStatus` | none; it sets `goXlrFader{A-D}Channel/Muted/MuteState/MuteFunction/Volume`, `goXlrVolume{Channel}` (+`Raw`), cough, effects, preset and bank variables |
| Send raw command | `goxlr.sendCommand` | any GoXLRCommand JSON, or a whole DaemonRequest with Raw request on (`goXlrResponse`) |

The GoXLR Mini has no effects, sampler or scribble strips. Those sub-actions fail with a clear message on a Mini.

**Script functions** (`goxlr.*`): `isConnected`, `connect`, `disconnect`, `getStatus`, `getRawStatus`, `setVolume`,
`setFaderMute`, `setFaderChannel`, `setCoughMute`, `loadProfile`, `loadMicProfile`, `setEffect`, `setEffectPreset`,
`setSamplerBank`, `playSample`, `stopSample`, `setRouting`, `setGlobalColour`, `command` (raw GoXLRCommand) and
`request` (raw DaemonRequest).

## Not covered

- Mic level metering (`GetMicLevel`): it has to be polled, and nothing asked for it yet.
- EQ, gate and compressor parameters, individual effect parameters (reverb amount and so on), submixes, sample
  recording and firmware updates. All of them are reachable through Send raw command / `goxlr.command`.
- `Daemon(...)` commands (open the UI, autostart, ...). Reachable with Raw request on.

## Testing

- **Unit tests** (`tests/Cuebot.GoXlr.Tests`):
  - **Fake utility:** `Support/FakeGoXlrUtility.cs` is a real HTTP/WebSocket server on localhost that serves
    `/api/websocket`, answers `Ping` and `GetStatus`, applies the commands the tests use to a realistic DaemonStatus,
    and broadcasts json-patch diffs the way the daemon does. It also covers whole-section replacement, a second device
    being plugged in and out, hardware button presses, a patch that does not apply, server-side close and reconnect,
    and a GoXLR Mini.
  - **Protocol:** JSON Patch (RFC 6902 operations, pointer escapes, errors), the serde wire format, name parsing, and
    status parsing and diffing.
  - **Module:** DI registration, the engine running sub-actions, trigger filters, and script functions.
- **UI test:** `tests/Cuebot.UiTests/GoXlr` (screenshots `goxlr-*.png`).
- **Live test:** `GoXlrLiveTests` (`CUEBOT_LIVE_GOXLR=1`; see docs/LIVE_TESTING.md). It has not been run against a
  real GoXLR yet: there is no GoXLR here.
