# Stream apps: Meld Studio and PolyPop

`Cuebot.StreamApps` connects Cuebot to **Meld Studio** and **PolyPop**. Settings live in `streamapps.json`.
Both services implement `IConnectionStatus`, start only when their auto-connect/auto-start box is ticked, and retry
with exponential backoff (2 s doubling to 30 s). Clean-room: wire formats come from the apps' own documentation and
from open-source clients (read for the format only, no code copied); Streamer.bot's public docs gave the names of
triggers, sub-actions and variables only.

## Meld Studio

### Options considered

| Option | Official? | Events | Control | Setup | Verdict |
| --- | --- | --- | --- | --- | --- |
| **WebChannel API** (Qt WebChannel over `ws://<host>:13376`) | Yes: documented by Meld in `WebChannelAPI.md` and linked from meldstudio.co/docs/api | Full session pushes (scenes, layers, effects, tracks), `isStreaming`/`isRecording` changes, `gainUpdated` per track | Scenes, staged scenes, layers, effects, mute/monitor/gain, record/stream, screenshots, clips, replay, virtual camera, media, widget events, `setProperty` (v2) | One checkbox in Meld: "Allow remote connections" | **Chosen** |
| Stream Deck plugin (MeldStudio/streamdeck), Bitfocus Companion `meld-studio`, Firebot / Touch Portal / Loupedeck plugins, meld-cli | Mixed (the Stream Deck plugin is Meld's own) | Same | Same | n/a | They all speak the same WebChannel API with Qt's `qwebchannel.js`, so they are not separate interfaces |
| OSC, HTTP or an SDK | none found | – | – | – | Meld publishes no other remote-control interface |

There is no cheaper fallback worth keeping: the WebChannel API *is* the only interface, and Meld has no audio-meter
API (`gainUpdated` reports fader gain and mute, not levels).

### Sources

- Meld Studio, *WebChannel API Documentation*: https://github.com/MeldStudio/streamdeck/blob/main/WebChannelAPI.md
  (linked from https://meldstudio.co/docs/api/). Properties, session schema, signals, slots, `sendCommand` commands,
  `sendStreamEvent` widget events, `setProperty` (API v2).
- Qt, *Qt WebChannel JavaScript API* (https://doc.qt.io/qt-6/qtwebchannel-javascript.html) and the message-type
  numbers of Meld's own client build (`https://packages.streamwithmeld.com/qt6.8/qwebchannel.js`).
- Confirmation that these are the methods real clients use: Bitfocus Companion `companion-module-meld-studio`
  (HELP.md), Meld's Stream Deck plugin, `zunderscore/firebot-plugin-meld-studio`, the Meld Bridge notes
  (mint-shock-digital-garden.vercel.app/Meld-Bridge: no authentication; methods tested).
- Streamer.bot docs (names/variables only): docs.streamer.bot/api/triggers/meld-studio/*,
  docs.streamer.bot/api/sub-actions/meld-studio/*, docs.streamer.bot/guide/stream-apps/meld-studio.

### Protocol (Qt WebChannel, implemented in `Meld/QWebChannel.cs`)

One JSON object per websocket text frame. Message types: 1 signal, 2 propertyUpdate, 3 init, 4 idle, 5 debug,
6 invokeMethod, 7 connectToSignal, 8 disconnectFromSignal, 9 setProperty, 10 response.

1. Client → `{"type":3,"id":1}` (init). Server → `{"type":10,"id":1,"data":{"meld":{"methods":[["showScene",N],["showScene(QString)",N],...],"properties":[[idx,"session",[1|"sessionChanged",sigIdx],{"items":{...}}],...],"signals":[["gainUpdated",sigIdx],...],"enums":{}}}}`.
   A notify-signal name of `1` means "<property>Changed". Missing `version` property = API v1.
2. Client → `{"type":4}` (idle). The server sends property updates only after the client is idle, and the client
   answers every update with another idle (Cuebot does this).
3. Property updates: `{"type":2,"data":[{"object":"meld","signals":{"<sigIdx>":[]},"properties":{"<propIdx>":value}}]}`.
4. Pure signals need `{"type":7,"object":"meld","signal":<idx>}`; Cuebot connects `gainUpdated` and calls
   `registerTrackObserver("cuebot", trackId)` for each track (setting *ObserveTrackGain*, default on). Signal frames:
   `{"type":1,"object":"meld","signal":<idx>,"args":[trackId, gain, muted]}`.
5. Calls: `{"type":6,"object":"meld","method":<index from init>,"args":[...],"id":n}` → `{"type":10,"id":n,"data":...}`.
   Cuebot only calls methods the init listed and reports "this Meld version does not offer X" otherwise.

Session items are keyed by 32-hex ids with `type` scene/layer/effect/track; layers' `parent` is their scene, effects'
`parent` is their layer, layer tracks' `parent` is their layer, global tracks have none.

### Mapping

Triggers (bus event → variables). Connection variables on every event: `meldStudio.host`, `meldStudio.id` (stable
GUID in settings), `meldStudio.name`, `meldStudio.port`.

| Streamer.bot trigger | Cuebot event / trigger id | How it is detected |
| --- | --- | --- |
| Connected / Disconnected | `meldstudio.connected` / `meldstudio.disconnected` (+ `reason`) | init done / socket closed |
| Scene Changed | `meldstudio.sceneChanged`: `meldStudio.sceneName`, `meldStudio.oldSceneName` (+ ids); filter by scene | `current` scene in the session changed |
| Recording / Streaming Started / Stopped | `meldstudio.recordingStarted` ... `meldstudio.streamingStopped` | `isRecording` / `isStreaming` property updates |
| Event | `meldstudio.event`: `meldStudioEvent.event`, `meldStudioEvent._json` and the flattened payload | session diff |
| ↳ LayerVisibilityChanged | `layerId`, `layerName`, `visible` (+ `sceneId`, `sceneName`); trigger `meldstudio.layerVisibilityChanged` | layer `visible` changed |
| ↳ EffectEnabledStateChanged | `effectId`, `effectName`, `enabled` (+ `layerId`, `layerName`); `meldstudio.effectEnabledStateChanged` | effect `enabled` changed |
| ↳ TrackMutedStateChanged / TrackMonitoringStateChanged | `trackId`, `trackName`, `muted` / `monitoring`; `meldstudio.trackMutedStateChanged`, `meldstudio.trackMonitoringStateChanged` | track flags changed |
| ↳ ObjectAdded / ObjectUpdated | `id`, `name`, `objectType`, `object.*` | new id / other properties changed |
| ↳ extras: ObjectRemoved, SceneStaged, TrackGainChanged | as above; `gain`, `muted` for gain | id gone / `staged` set / `gainUpdated` |

All event names are prefixed `meldStudioEvent.` in the arguments, matching Streamer.bot's documented names.

| Streamer.bot sub-action | Cuebot type id | Meld call |
| --- | --- | --- |
| Show Scene | `meldstudio.showScene` | `showScene(id)` (name or id) |
| Get Current Scene | `meldstudio.getCurrentScene` → `currentScene` (+ `currentSceneId`) | cached session |
| Get Status | `meldstudio.getStatus` → `meldStudio.isConnected/isRecording/isStreaming` (+ the documented typo `meldSutdio.isStreaming`) | cached properties |
| Recording / Streaming (Start/Stop/Toggle) | `meldstudio.recording` / `meldstudio.streaming` | `toggleRecord()` / `toggleStream()` only when the state must change |
| Take Screenshot / Take Vertical Screenshot | `meldstudio.takeScreenshot` (orientation) / `meldstudio.takeVerticalScreenshot` → `screenshotSuccess` | `sendCommand("meld.screenshot[.vertical]")` |
| Set Layer Visibility State | `meldstudio.setLayerVisibility` | `toggleLayer(sceneId, layerId)` when needed |
| Set Effect Enabled State | `meldstudio.setEffectState` | `toggleEffect(sceneId, layerId, effectId)` when needed |
| Set Layer Track Muted State / Set Track Muted State | `meldstudio.setLayerTrackMuted` / `meldstudio.setTrackMuted` | `toggleMute(trackId)` when needed |
| Set Browser Layer URL | `meldstudio.setBrowserLayerUrl` | `setProperty(layerId, "url", url)` (API v2) |
| extras | `setTrackMonitoring`, `setTrackGain`, `stageScene`, `showStagedScene`, `recordClip` (0-90 s), `replay` (show/dismiss), `toggleVirtualCamera`, `mediaControl` (play/pause/seek), `widgetEvent` (`sendStreamEvent`), `setProperty`, `sendCommand`, `getScenes` | as named in Meld's docs |

Script functions (Streamer.bot's `MeldStudio*` C# methods map here): `meldstudio.isConnected`, `connect`,
`disconnect`, `getStatus`, `showScene`, `showSceneByName`, `showLayer`, `hideLayer`, `setLayerVisibility`,
`setEffectEnabled`, `setTrackMuted`, `setTrackGain`, `recording`, `streaming`, `screenshot`, `sendCommand`,
`getScenes`, `getSession`, `invoke` (any published method). `MeldStudioGetConnectionByName` has no equivalent:
Cuebot keeps one Meld connection (Streamer.bot allows several).

### Where Cuebot goes beyond Streamer.bot's documented Meld support

Track gain events and gain control, monitoring, staged scenes, clips, replays, virtual camera, media layers, widget
events and generic `setProperty`; state sub-actions never toggle when the item is already in the requested state; an
"object removed" event; names *or* ids accepted everywhere; a clear error when Meld's API version lacks a method.

### Not covered / untested

- Not yet run against a real Meld Studio (unit tests use an in-memory WebChannel server built from the docs; the
  live test `CUEBOT_LIVE_MELD=1` is ready). The exact shape of `meld.session` in propertyUpdate frames (wrapped in
  `items` or not) is handled both ways.
- Meld has no authentication: anyone who can reach port 13376 controls Meld. Keep "Allow remote connections" to
  trusted networks.

## PolyPop

### Options considered

| Option | Official? | Status | Verdict |
| --- | --- | --- | --- |
| **Community WebSocket plugin** (github.com/Jabbey92/PolyPopWebsocketPlugin, MIT, v1.1 June 2022; installed from PolyPop's plugin browser, Web Services tab) | No | Last release 2022; used by Streamer.bot and Mix It Up | **Chosen** (the only way in) |
| Official PolyPop API / OSC / HTTP | none found | – | PolyPop's plugin system is Lua inside PolyPop; there is no external API |
| Lua SDK (unofficial docs: JustinBacher/PolyPop-SDK-Documentation) | No | 2023 | Would mean shipping our own PolyPop plugin; not worth it for a dormant app |

**Is PolyPop maintained?** It looks dormant: the original domain polypop.live lapsed (redirects since 2024, a parking
page in 2025), a WordPress site at polypoplive.com still offers a Windows download without version or release notes,
and the plugin has not changed since 2022. The integration is small and still works with the published plugin, so
it is implemented; nothing more is planned.

### Who connects to whom

The plugin is a websocket **client**: its Lua source opens `ws://127.0.0.1:<Port>` (Port is a setting on its WebSocket
source, default 38031) and retries every 5 s. So Cuebot is the **server** by default (`Mode = Server`), listening on
Address 127.0.0.1, Port 9652 and Endpoint `/` - Streamer.bot's labels and defaults, so a plugin already set up for
Streamer.bot connects unchanged; new users set the plugin's Port to 9652 (or Cuebot's Port to 38031). The server is a
small RFC 6455 listener (TcpListener, no admin rights or URL ACLs, Windows and Linux); several plugin instances may
connect and all receive alerts. Requests with a browser `Origin` from another site are refused (the plugin sends none).
Cheap fallback: `Mode = Client` connects out to `ws://Address:Port/Endpoint` for a plugin build that listens instead.

### Wire format (from the plugin's `WebSocket.lua` / `WebSocketAlert.lua`)

- To PolyPop: `{"type":"ALERT","title":"<alert name>","variables":{"text":{"<name>":"<value>",...}}}`. The plugin
  raises every WebSocket Alert whose name equals `title`, starting from the alert's default text variables and
  overriding them with `variables.text`; in PolyPop they are wired as `<name>`.
- From PolyPop: `<payload>:<plugin instance id>[:<data>]`, where the payload is JSON such as
  `{"type":"TRIGGER","name":"...","data":"..."}` or a word such as `disconnect` (sent as `disconnect:<id>:unload`
  when the source unloads).

### Mapping

| Streamer.bot | Cuebot |
| --- | --- |
| Sub-action Trigger Alert (Alert Name; variables table: name = alert variable, value) | `polypop.triggerAlert`: Alert name + Variables (one `name=value` per line or a JSON object; `%args%` resolved) → `polypopClients` (plugins reached) |
| – | Triggers `polypop.connected`, `polypop.disconnected`, `polypop.message` (`polypop.type`, `polypop.name`, `polypop.data`, `polypop.sourceId`, `polypop.raw`; filter by type and name) |
| – | Script functions `polypop.isConnected`, `polypop.triggerAlert({alertName, variables:{...}})` |

### Not covered / untested

- Not run against real PolyPop (the live test `CUEBOT_LIVE_POLYPOP=1` is ready). Unit tests drive the real listener
  with a real `ClientWebSocket` acting as the plugin, and client mode with an in-memory server.
- The plugin's own "Trigger" action reads property names that do not exist in its definition (`AlertName`/`AlertText`
  vs `AlertTitle`/`AlertValue`), so messages *from* PolyPop may never arrive with the published build; Cuebot parses
  them if they do.
