# Elgato Wave Link: how Cuebot talks to it

Wave Link (Windows and macOS only) runs a **local JSON-RPC 2.0 server over a WebSocket** on `127.0.0.1`. Elgato's own
Stream Deck plugins use it; Elgato publishes no API documentation, so the wire format below comes from public,
open-source clients (read for the message shapes only; no code was copied) and Streamer.bot's public docs (trigger and
sub-action names and variable names only). Two incompatible API generations exist.

## Options considered

| Option | Protocol / scope | Version detection | Verdict |
| --- | --- | --- | --- |
| **Wave Link's local JSON-RPC WebSocket, both generations, own client** | `ws://127.0.0.1:<port>`, JSON-RPC 2.0. 3.x: `getChannels`/`setChannel`/... ; 1.x/2.x: `getInputConfigs`/`setInputConfig`/... | Probe every candidate port with `getApplicationInfo`: 1.x/2.x answers `appName` (interface revision 6-7), 3.x answers `name` (revision 1) | **Chosen.** The only control surface Wave Link has; what Elgato's Stream Deck plugins use; one small client covers both generations and maps them onto one model. |
| NuGet `WaveLink.Client` (Agash, MIT, .NET 10) | Wave Link 3 only | n/a | Rejected as a dependency: no 1.x/2.x support (we need both anyway) and the protocol is small. Its PROTOCOL.md was the main source for the 3.x wire format. |
| `ElgatoWaveLinkSDK` (Professor-Melvin, GPL-3.0, C#) | The early 1.0-1.4 API (`getAllChannelInfo`, `setInputMixer`, ...) | n/a | Rejected: GPL, and the methods it targets were replaced in Wave Link 1.5+. Not read beyond the file list. |
| Bitfocus Companion module (`companion-module-elgato-wavelink`) | Node module for Companion | n/a | The repository could not be found under that name (404) when researched; Node only, not embeddable. Not used. |
| Windows per-app audio (Core Audio sessions) | OS mixer | n/a | Rejected: cannot see or change Wave Link's channels, mixes or effects; no macOS. |
| Stream Deck SDK / going through Elgato's plugin | Stream Deck app ↔ plugin | n/a | Not applicable: the plugin is itself just a client of the WebSocket above. |

Sources (all public):
- Wave Link 3 wire format: <https://github.com/Agash/WaveLink.Client> (`PROTOCOL.md` and the JSON property names in
  `src/WaveLink.Client/*.cs`, MIT), which documents what the official Stream Deck "Wave Link 3" plugin sends.
- Wave Link 1.x/2.x wire format: the legacy Elgato Stream Deck plugin's JavaScript as redistributed in
  <https://github.com/Nevylish/com.elgato.wavelink.sdPlugin> (`plugin/js/WaveLinkConstants.js`, `WaveLinkClient.js`;
  API revisions 6-7), <https://github.com/fu-raz/midi-mixer-wavelink-new> (`lib/WaveLink/*`), and for the much older 1.0
  API <https://github.com/anaisbetts/midi-mixer-wavexlr> (`original/WaveLinkClient.js`). Also seen:
  <https://github.com/CestMoiRoma/ElgatoWavePy> (MIT), <https://github.com/oddbear/Loupedeck.WaveLink.Plugin> (MIT),
  <https://github.com/kylergib/WaveLinkPluginTouchPortal>.
- Trigger / sub-action names and variables: <https://docs.streamer.bot/api/triggers/elgato/wave-link-3>,
  <https://docs.streamer.bot/api/sub-actions/elgato/wave-link-3>, <https://docs.streamer.bot/api/sub-actions/elgato/wave-link>
  (summarised in `docs/parity/streamerbot-inventory.md` §3.8). Most trigger pages say "missing documentation", so trigger
  variables follow the sub-action pages' naming (`waveLinkChannel*`, `waveLinkMix*`, `waveLinkInputDevice*`, ...).
- Elgato: <https://help.elgato.com> (user docs only; no developer API).

## Transport and discovery

- Always `127.0.0.1`; no authentication. Cuebot sends `Origin: streamdeck://` like the official plugin (recommended for
  compatibility with 3.x; harmless for 1.x/2.x).
- **Wave Link 3** writes its port to `ws-info.json` (`{"port": 1884}`), on Windows in
  `%LOCALAPPDATA%\Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\`. Fallback scan **1884-1893**.
  The macOS location is unknown; two guessed paths are tried, then the scan.
- **Wave Link 1.x/2.x** takes the first free port from **1824** (clients scan 1824-1833; Cuebot scans to 1834).
- Cuebot probes all candidates **in parallel** (a refused localhost connection can take ~2 s on Windows), sends
  `getApplicationInfo` to each that accepts, and keeps the first in priority order (ws-info port, 1884-1893,
  1824-1834) whose answer looks like Wave Link. `Port` and `Api` settings pin either.
- Classification: an answer with `appName` → 1.x/2.x; with `name` (and no `appName`) → 3.x; only `appID` → by port
  range; none of `appID`/`appName`/`name` → not Wave Link.
- Not found / closed → exponential backoff (2 s doubling to 30 s) while auto-reconnect is on.
- **Linux**: Wave Link does not exist there; the service reports "Wave Link is not available on Linux" and never
  opens a socket.

JSON-RPC: requests `{"id":n,"jsonrpc":"2.0","method":m,"params":{...}}` (params omitted when there are none),
responses `{"id":n,"result":...}` or `{"id":n,"error":{"code","message"}}`, notifications without `id`.

## Wave Link 3.x

Getters (after `getApplicationInfo` → `{appID:"EWL", name:"Elgato Wave Link", interfaceRevision:1}`):

| Method | Result |
| --- | --- |
| `getChannels` | `{channels:[{id, name, type, isMuted, level 0-1, image, apps:[{id,name}], mixes:[{mixId, isMuted, level}], effects:[{id,name,isEnabled,isSupported}]}]}` |
| `getMixes` | `{mixes:[{id, name, isMuted, level, image}]}` |
| `getInputDevices` | `{inputDevices:[{id, name, isWaveDevice, inputs:[{id, name, isMuted, isGainLockOn, gain:{value 0-1, max dB}, micPcMix:{value, isInverted}, effects, dspEffects}]}]}` |
| `getOutputDevices` | `{mainOutput:{outputDeviceId, outputId}, outputDevices:[{id, name, isWaveDevice, outputs:[{id, name, isMuted, level}]}]}` |

Setters take partial objects: `setChannel {id, isMuted?, level?, mixes?:[{mixId, isMuted?, level?}], effects?:[{id, isEnabled}]}`,
`setMix {id, isMuted?, level?}`, `setInputDevice {id, inputs:[{id, isMuted?, isGainLockOn?, gain?:{value}, micPcMix?:{value}, effects?, dspEffects?}]}`,
`setOutputDevice {outputDevice:{id, outputs:[{id, isMuted?, level?}]}}` or `{mainOutput:{outputDeviceId, outputId}}`,
`addToChannel {appId, channelId}`, `setSubscription {focusedAppChanged:{isEnabled}, levelMeterChanged:{isEnabled, type, id, subId?}}`.

Notifications: `channelsChanged`/`mixesChanged`/`inputDevicesChanged`/`outputDevicesChanged` carry full lists (replace);
`channelChanged`/`mixChanged`/`inputDeviceChanged`/`outputDeviceChanged` carry `{id, ...partial}` and are merged
(nested `mixes` by `mixId`; `inputs`, `outputs`, `effects`, `dspEffects` by `id`; anything else replaced).
`focusedAppChanged {id, name, channel:{id}}`, `createProfileRequested {deviceType, mixes:[...]}`,
`levelMeterChanged {inputDevices, outputDevices, channels, mixes: [{id, subId?, levelLeftPercentage, levelRightPercentage}]}`.

**Unverified (no Wave Link 3 here):**
- An output's **mix assignment**: Streamer.bot has "Output Mix Changed", "Add Output To Device Mix" and "Set Monitored
  Mix", but no public client shows the field. Cuebot reads/writes `mixId` on an output (`setOutputDevice` with
  `outputs:[{id, mixId}]`), and treats the main output's mix as the monitored mix.
- **Clipguard / low cut** on 3.x are hardware `dspEffects`; their ids are not documented, so Cuebot matches by id or
  name ("clip", "low cut"/"lowcut"/"high pass").
- `levelLeftPercentage` is taken as 0-100 (by its name).
- Whether 3.x echoes a client's own change back as a notification: Cuebot applies its own successful changes locally
  (same merge path) and drops notifications that change nothing, so triggers fire once either way.

## Wave Link 1.x/2.x (interface revisions 6-7)

`getApplicationInfo` → `{appID:"egwl"?, appName:"Elgato Wave Link", interfaceRevision:7}`. Getters:
`getMicrophoneConfig` → `[{identifier, name, isWaveLink, isWaveXLR, gain, outputVolume, balance 0-1, isLowCutOn, lowCutType, isClipGuardOn, isGainLocked, isMicMuted}]`;
`getSwitchState` → `{value:"LocalMix"|"StreamMix"}` (which mix is monitored);
`getOutputConfig` → `{localMixer:[isMuted, volume 0-100], streamMixer:[...]}`;
`getOutputs` → `{outputs:{localMixer:[{identifier,name}], streamMixer:[...]}, selectedOutput:{localMixer:id, streamMixer:id}}` (older: one list and a string);
`getInputConfigs` → `[{identifier, name, inputType, isAvailable, localMixer:[isMuted, volume, filterBypass], streamMixer:[...], filters:[{filterID, name, isActive, pluginID}]}]`.

Mixers: `com.elgato.mix.local` (Monitor Mix), `com.elgato.mix.stream` (Stream Mix), `com.elgato.mix.all`.

Setters: `setInputConfig {property:"Volume"|"Mute", identifier, mixerID, value, forceLink}`,
`setOutputConfig {property:"Output Level"|"Output Mute", mixerID, value, forceLink}`,
`setMicrophoneConfig {identifier, property, value | boolValue}` with properties `Microphone Gain`, `Microphone Output Volume`,
`Microphone DirectMonitor Volume` (balance 0-1), `Microphone Mute`, `Microphone LowCut`, `Microphone ClipGuard`,
`Microphone GainLock`, `XLR Low Cut Type` (booleans for mute/low cut/clipguard go in `boolValue`),
`setFilter {identifier, filterID, value}`, `setFilterBypass {identifier, mixerID, value}`,
`setSelectedOutput {identifier, name, mixerID}`, `switchOutput {}` (toggles the monitored mix).

Notifications: `inputMuteChanged`/`inputVolumeChanged {identifier, mixerID, value}`, `inputNameChanged {identifier, value}`,
`inputEnabled`/`inputDisabled {identifier}`, `inputsChanged`, `profileChanged`, `filterAdded {identifier, filterID, name, isActive, pluginID}`,
`filterChanged {identifier, filterID, value}`, `filterRemoved {identifier, filterID}`, `filterBypassStateChanged {identifier, mixerID, value}`,
`microphoneConfigChanged {identifier, property, value}`, `outputMuteChanged {mixerID, value}`, `outputVolumeChanged {identifier, mixerID, value}`,
`outputSwitched {value}`, `selectedOutputChanged {value | identifier + mixerID}`,
`realTimeChanges {mixerList (rev 7) | MixerList (rev 6): [{identifier, localMixer:{levelLeft, levelRight}, streamMixer:{...}}], localMixer, streamMixer, microphoneLevel}` (levels 0-1).

The microphone gain/output volume values are the device's own scale (the plugin maps them through lookup tables);
Cuebot passes them through unchanged. The 1.0-1.4 API (`getAllChannelInfo`, `setInputMixer`, ...) is not supported.

## One model for both

Volumes, levels, gain (3.x) and balance are percentages 0-100. 1.x/2.x inputs become **channels** with two mixes
(Monitor, Stream); its two output mixers become **mixes**; its microphone(s) **input devices**; its monitor devices
**output devices**; `getSwitchState` the monitored mix. Events use one set of types (`wavelink.channelMuteChanged`, ...)
for both generations; 1.x/2.x events add `waveLinkLegacyEvent` (`InputMuteChanged`, ...) for Streamer.bot's legacy
triggers. Every event carries `waveLinkApi` (`3` / `legacy`), `waveLinkPort`, `waveLinkInterfaceRevision`, `waveLinkEvent`.

**Level meters** are off by default. When on, Wave Link 3 is subscribed (optionally filtered by type), and both
generations go through `LevelMeterThrottle`: at most *rate* events per second (default 10), each carrying every meter
that moved at least 1 point (`waveLinkLevels` JSON, loudest one flattened as `waveLinkLevel*`); the trigger picks the
loudest matching meter.
