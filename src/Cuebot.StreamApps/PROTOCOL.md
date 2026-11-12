# Stream apps: Meld Studio, PolyPop, T.I.T.S., Lumia Stream, OBSBOT, mtion studio, VTuber apps (and StreamFog)

`Cuebot.StreamApps` connects Cuebot to **Meld Studio**, **PolyPop**, **T.I.T.S.**, **Lumia Stream**, **OBSBOT** and **mtion studio** (StreamFog has no public API; see its section). Settings live in `streamapps.json`.
Both services implement `IConnectionStatus`, start only when their auto-connect/auto-start box is ticked, and retry
with exponential backoff (2 s doubling to 30 s). Clean-room: wire formats come from the apps' own documentation and
from open-source clients (read for the format only, no code copied); Streamer.bot's public docs gave the names of
triggers, sub-actions and variables only.

## Meld Studio

### Options considered

| Option | Official? | Events | Control | Setup | Verdict |
| --- | --- | --- | --- | --- | --- |
| **WebChannel API** (Qt WebChannel over `ws://<host>:13376`) | Yes: documented by Meld in `WebChannelAPI.md` and linked from meldstudio.co/docs/api | Full session pushes (scenes, layers, effects, tracks), `isStreaming`/`isRecording` changes, `gainUpdated` per track | Scenes, staged scenes, layers, effects, mute/monitor/gain, record/stream, screenshots, clips, replay, virtual camera, media, widget events, `setProperty` (v2) | One checkbox, File → Preferences → Advanced → "Allow remote connections" (from Streamer.bot's setup guide; Meld's API doc only says it "will always listen on" 127.0.0.1:13376) | **Chosen** |
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
   `{"type":1,"object":"meld","signal":<idx>,"args":[trackId, gain, muted]}`. Meld answers each registration with the
   current gain at once (that first report is a baseline, not an event) and drops every observer when it rebuilds its
   audio tracks (restoring or reopening a session, socket left open), so Cuebot registers again after every session
   change, as Meld's own Stream Deck plugin does.
5. Calls: `{"type":6,"object":"meld","method":"<bare name>","args":[...],"id":n}` → `{"type":10,"id":n,"data":...}`
   (a void slot answers `data: null`). Like `qwebchannel.js`, Cuebot names the method, so Qt picks the overload whose
   parameter count matches the arguments; a call by index skips that and silently drops extra arguments. Cuebot only
   calls methods the init listed and reports "this Meld version does not offer X" otherwise. `sendStreamEvent` always
   gets two arguments (`false` when there is no data), as Meld's plugin sends it.

Session items are keyed by 32-hex ids with `type` scene/layer/effect/track; layers' `parent` is their scene, effects'
`parent` is their layer, layer tracks' `parent` is their layer, global tracks have none. Scenes with `vertical: true`
are the vertical-canvas twins of a scene (same name possible): they are kept out of the scene list and name lookups
(reachable by id), and staging one is refused because Meld resolves `setStagedScene` through horizontal scenes only.

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
| Set Image/Media Layer Source | `meldstudio.setLayerSource` | `setProperty(layerId, "source" (image layer) or "mediaSource" (media layer), file URL)` (API v2; a local path is sent as `file:///...`) |
| Set Text | none | Meld's API documents no text property for text layers |
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
  live test `CUEBOT_LIVE_MELD=1` is ready).
- Meld has no authentication: anyone who can reach port 13376 controls Meld. Keep "Allow remote connections" to
  trusted networks. Meld's own docs only mention 127.0.0.1; the checkbox comes from Streamer.bot's setup guide.

### Verification against the docs (2026-10-28)

Sources: Meld's `WebChannelAPI.md` (github.com/MeldStudio/streamdeck, last change commit `f8e27ea`, 2026-08-13; the
file has no version of its own); meldstudio.co/docs/api (only links to it); Meld's client
`packages.streamwithmeld.com/qt6.8/qwebchannel.js` (Last-Modified 2025-06-20, identical to Qt 6.8's file); Qt's
`qmetaobjectpublisher.cpp` (qtwebchannel 6.8 branch) for how the server resolves calls and queues updates; Meld's
Stream Deck plugin source at the same commit (`src/meld/connection.ts`, `types.ts`, `widget-control.ts`,
`transition-stepper.ts`, `replay.ts`). Bitfocus `companion-module-meld-studio` (2026-09-29) was read but is guesswork
(calls methods "if they exist"), so it settled nothing.

Verified as matching: port 13376; message type numbers; init → response → idle order and an idle after every
propertyUpdate; property entries `[idx, name, [1|"sig", sigIdx] | [], value]` with `1` = `<name>Changed`; frame shapes
of propertyUpdate, connectToSignal, signal and response; property names `session` (documented as an object with a
single key `items`), `isStreaming`, `isRecording`, `version` (absent = v1); every session field Cuebot reads; the
argument order of every method Cuebot calls; all 12 `sendCommand` strings and all 15 widget event types.

Changed:
- Methods were invoked by numeric index; now by bare name like `qwebchannel.js` (Qt's overload resolution by argument
  count). The fake now lists real signatures, QObject's own members, refuses a by-name call that matches no overload
  and drops extra arguments on index calls, so a wrong argument count fails the tests.
- `sendStreamEvent` without data sent one argument; now `[type, false]` (Meld's plugin).
- `gainUpdated` that changed only `muted` raised nothing; now it does. The report Meld sends on registration is a
  baseline (the fake now sends it, as the plugin documents).
- Track observers were never registered again; now after every session change.
- Vertical twin scenes were treated as ordinary scenes (name lookups, scene list, scene-changed events, staging).
- The fake now holds property updates until the client is idle (Qt's behaviour), so the idle replies are tested.

Not settled by the docs (risks until tried with the real app): whether Meld's slots are overloads or have default
arguments (only a live init dump shows the real signatures); the scale of the gain `gainUpdated` reports (only
`setGain`'s 0-1 is documented); which API version added `sendCommandWithArgs`, `sendStreamEvent`, `callFunction*`;
whether a vertical twin is `current` together with its scene; the docs' table calls a track's `parent` a scene while
its example (and Meld's plugin) use the layer, which Cuebot follows. The vertical twins, observer drop and
registration report come from Meld's plugin, not from `WebChannelAPI.md`.

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

## T.I.T.S. (Twitch Integrated Throwing System, by Remasuri3)

Code: `Tits/`. Settings `streamapps.json` → `Tits` (Host 127.0.0.1, Port 42069, AutoConnect off, AutoReconnect on).

### Source

- Remasuri3, *T.I.T.S. API* README: https://github.com/Remasuri/TITSAPI (the only public description; "extremely early").
- Streamer.bot docs (names/variables only): docs.streamer.bot/api/triggers/integrations/tits/*,
  docs.streamer.bot/api/sub-actions/integrations/tits/*, docs.streamer.bot/api/csharp/methods/integrations/tits/*.

### Protocol

Two websockets, no authentication:

- `ws://host:42069/websocket` - requests. Every message is `{"apiName":"TITSPublicApi","apiVersion":"1.0","requestID":"<id>","messageType":"<type>","data":{...}}`
  (`data` left out when there is none); the response echoes `requestID` with `messageType` `<X>Response` (plus a
  `timestamp`). `TITSItemListRequest` (`data.sendImage`; Cuebot sends `false`, also as the obsolete top-level
  `sendImage` that builds before mid-2023 read) → `data.items[{ID,name,encodedImage}]`; `TITSItemInfoRequest`
  (`data.itemID`) → `data.{ID,name,encodedImage}`; `TITSThrowItemsRequest` (`data.items` = ids, one picked at random
  per throw, `delayTime` seconds, `amountOfThrows`, `errorOnMissingID`) → `data.numberOfThrownItems`;
  `TITSTriggerListRequest` → `data.triggers[{ID,name}]` and a top-level `triggerCount`; `TITSTriggerActivateRequest`
  (`data.triggerID`, and since October 2025 `data.triggerName`, used when the id is empty or unknown) → no data.
  Cuebot sends a known trigger's id and name, and unknown text in both fields. The README does not document errors
  (issue #9); open-source clients expect `APIError` or `TITSApiError` with `data.errorID` and `data.message`, so any
  `messageType` containing "Error" is a failure. An error without a known `requestID` fails the request when exactly
  one is waiting.
- `ws://host:42069/events` - broadcasts `TITSHitEvent` (`itemID`, `itemName`, `triggerID`, `triggerName`, `strength`,
  `direction{x,y,z}`), `TITSTriggerActivatedEvent` and `TITSTriggerEndedEvent` (`triggerID`, `triggerName`), in the
  same envelope (`requestID` "someID" in the README's examples). Names a payload lacks are filled from the cached lists.

The request socket is the connection (status, backoff). While it is up a second loop keeps `/events` connected with
its own backoff. If `/events` is down, broadcasts arriving on the request socket are also accepted (not documented;
harmless). Item and trigger lists are read on connect (no images) so sub-actions and filters can use names
(case-insensitive; ids also accepted).

### Mapping

| Streamer.bot | Cuebot |
| --- | --- |
| Trigger Connected | `tits.connected` (both sockets up) |
| Triggers Websocket Connected / Disconnected | `tits.websocketConnected` / `tits.websocketDisconnected` (request socket; `reason`) |
| Triggers Events Connected / Disconnected | `tits.eventsConnected` / `tits.eventsDisconnected` |
| – | `tits.disconnected` (was fully connected, one socket dropped) |
| Trigger Item Hit (`itemId`, `itemName`, `triggerId`, `triggerName`, `strength`, `direction.x/y/z`) | `tits.itemHit`, same variables; filters item, trigger, minimum strength |
| Triggers Trigger Activated / Trigger Ended (`triggerId`) | `tits.triggerActivated` / `tits.triggerEnded` (`triggerId`, `triggerName`); trigger filter |
| Sub-actions Activate Trigger / Activate Trigger by Name | `tits.activateTrigger` (name or id) / `tits.activateTriggerByName` |
| Sub-actions Throw Item / Throw Item by Name (Item, Delay, Amount) | `tits.throwItem` / `tits.throwItemByName` → `tits.thrownItems` |
| Sub-actions Throw Items / Throw Items by Name (Items, Delay, Amount) | `tits.throwItems` / `tits.throwItemsByName` (names/ids per line or comma-separated) → `tits.thrownItems` |
| – | `tits.getItems` / `tits.getTriggers` → `tits.items` / `tits.triggers` (JSON `[{id,name}]`), `tits.itemCount` / `tits.triggerCount` |
| C# `ThrowingSystemThrowItemByName(itemName, delay = 0.05, amount = 1)` | `tits.throwItemByName({itemName, delay, amount})` → bool |
| C# `ThrowingSystemActivateTriggerByName(triggerName)` | `tits.activateTriggerByName({triggerName})` → bool |
| – | `tits.isConnected`, `tits.connect`, `tits.disconnect`, `tits.throwItems`, `tits.activateTrigger`, `tits.getItems`, `tits.getTriggers` |

Delay is in seconds (T.I.T.S.'s `delayTime`; Streamer.bot's C# default 0.05).

### Not covered / untested

- Not run against real T.I.T.S. (it was not running on the dev PC). Tests use an in-memory server shaped after the README.
- `TITSItemInfoRequest` and item images are not used (no Streamer.bot feature needs them).

### Verification against the docs (2026-10-28)

Sources: the TITSAPI README (github.com/Remasuri/TITSAPI, the repo's only file; HEAD `05ee40c`, 2025-10-13, PR #10 "Api
update 1", which added `triggerName` to activation and to both trigger events; earlier: `7274a92` 2023-06-18 moved
`sendImage` into `data`, `200f90e` 2023-01-17 added item info and changed the hit event, `436e0a8` 2023-01-11 added the
trigger events) and its issues (#9 errors undocumented, #2 extra `direction` fields such as `magnitude` and hit events
with ids only, #7). Community clients, read for field names only: Mix It Up's `TITSService.cs`, StreamConnect-Bridge's
`TITSHandler.ts`, `3e849f2e5c/TITSWebSocket` and `TITSLethalCompany`, okabot. No other official documentation exists.

Verified as matching: port 42069, paths `/websocket` and `/events`, no authentication; envelope field names and
casing; every request type and data field Cuebot sends; response type names and fields; the three event names and
their fields; Streamer.bot's variable names.

Changed:
- Activate Trigger by Name sent an empty `triggerID`, which builds before the October 2025 README update ignore; it now
  sends the cached id (reloading the list once) as well as the name. An unknown id was sent only as a name; now in
  both fields.
- Events without `triggerName` / `itemName` (older builds) published empty names, so name filters failed; the names
  now come from the cached lists.
- The item list now also carries the top-level `sendImage: false` (older builds would otherwise send every image).
- An error answer without a `requestID` timed out; it now fails the one waiting request.
- The fake now follows the README's examples: `encodedImage` in the item list, the top-level `triggerCount`, no data
  on the activation answer, `requestID`/`timestamp` on broadcasts, the id-then-name activation lookup, and a "legacy"
  mode (id-only activation, errors without `requestID`).

Not settled by the docs: the error message type and shape, and whether errors echo `requestID`; whether
`encodedImage` is left out when `sendImage` is false; whether hits from a plain throw carry trigger ids (issue #7);
whether the server is strict about casing (okabot sends `TITSPublicAPI`/`requestId`); whether the port can be changed
and which interface it binds (okabot reaches it on a LAN address); which T.I.T.S. app version added the October 2025
fields (`apiVersion` stayed "1.0").

## Lumia Stream

Code: `Lumia/`. Settings `streamapps.json` → `Lumia` (Host 127.0.0.1, Port 39231, Token, AutoConnect off, AutoReconnect on).

### Source

- Lumia Stream Developer API: https://dev.lumiastream.com/docs (source: https://github.com/lumiastream/Developer-Docs,
  `docs/rest/*`, `docs/websockets/*`).
- Streamer.bot docs (names only): docs.streamer.bot/api/sub-actions/integrations/lumia-stream/*,
  docs.streamer.bot/api/csharp/methods/integrations/lumia-stream/*, docs.streamer.bot/guide/integrations/lumia-stream.

### Protocol

- Token: Lumia → Settings → API → "Enable Developers API" → "Copy Token". Sent as `?token=` (documented on every
  version). Cuebot never logs it; HTTP error text is scrubbed of it.
- Websocket `ws://host:39231/api?token=<token>&name=Cuebot` (Lumia shows a "Cuebot connected" toast). A bad token or a
  disabled API rejects the upgrade, so an open socket means connected. Nothing is sent on it (every JSON message is
  executed as a command). Pushed envelope: `{origin, subOrigin?, type, event?, data}` with `type` chat (a deletion has
  `data.shouldRemove`), command, alert (`event` = alert name, `data.dynamic`, `data.replay`, `data.extraSettings`),
  states (`on`, `streamMode`, `fuze` as 1/0), premium, modqueue_list, chat_platform_register. `socketapi:*` replies are ignored.
- REST `POST http://host:39231/api/send?token=` with `{"type":..., "params":{...}}` for every command (it answers on
  every Lumia version; websocket replies need a recent one). Errors: `{"status":404,"code":"not_found","message":...}`
  on new versions, plain text on old ones. `GET /api/retrieve?token=&slim=true` on connect reads the user's commands
  (`options.<type>.values`), alerts, studio scenes, TTS voices and lights (`data.lights[{id,name,type}]`).
- Colours: `hex-color` (`value` "#RRGGBB") or `rgb-color` (`value` {r,g,b}); `brightness` 0-100 (default 100),
  `transition` ms (default 0), `duration` ms (default 4000), `hold`, `lights[{type,value:id}]`. `to-default` resets.
- `skipQueue: true` in `params` runs a queued type (commands, colours, alerts) at once: not on the Developer-Docs pages,
  but in Lumia's `lumia-types` (`API_ENVELOPE_PARAMS`, `ILumiaSendPack`) and sent by the official C# and JS SDKs.
- `extraSettings` values keep their JSON types (the docs send `"bits": 1000`; Lumia reads flags by truthiness, so the
  text "false" would be true). From a sub-action, a JSON object keeps its types and name=value lines are text except
  true/false. `update-variable-value` takes any value (counters also `+N`, `-N`, `=N`).
- `chatbot-message` platforms: the REST page lists twitch, youtube, facebook; `lumia-types` (which calls that page stale)
  adds kick and discord, and no platform means every connected chat bot (Cuebot's "all").

### Mapping

| Streamer.bot | Cuebot |
| --- | --- |
| Sub-action Send Command (Command) | `lumia.sendCommand`: Command, Command type (chat-command default, chatbot-command, twitch-points, twitch-extension, kick-points), Hold, Skip queue, Extra settings |
| Sub-action Set Color (Colour, Brightness, Duration, Transition, Skip Queue, Default, Light) | `lumia.setColor`: Colour (#RRGGBB, #AARRGGBB or r,g,b), Brightness, Duration, Transition, Skip Queue (`skipQueue`), Default (sends to-default), Lights (names or brand:id), Hold |
| Sub-action Set To Default | `lumia.setToDefault` |
| – | `lumia.sendAlert`, `lumia.chatbotMessage` (platform, as streamer), `lumia.tts` (voice, volume), `lumia.setLumiaState`, `lumia.setStreamMode`, `lumia.getVariable` (→ `lumia.variableValue`), `lumia.setVariable`, `lumia.send` (any Send API type + params JSON → `lumia.response`) |
| C# `LumiaSendCommand(command)` / `LumiaSetToDefault()` | `lumia.sendCommand({command, type, hold, extraSettings})` / `lumia.setToDefault()` |
| – | `lumia.isConnected`, `connect`, `disconnect`, `setColor`, `sendAlert`, `chatbotMessage`, `tts`, `getVariable`, `setVariable`, `send`, `getInfo` |
| – (Streamer.bot documents no Lumia triggers) | Triggers `lumia.connected`, `lumia.disconnected`, `lumia.chatMessage` (`user`, `userName`, `displayName`, `userId`, `message`, `messageId`, `lumia.channel`), `lumia.chatMessageDeleted` (`messageId`), `lumia.command` (`command`, `user`), `lumia.alert` (`lumia.alert`, `lumia.replay`, `lumia.dynamic.*`; filters alert names and replays), `lumia.statesChanged` (`lumia.on`, `lumia.streamMode`, `lumia.fuze`), `lumia.event` (other types, `lumia.data` JSON). Every event: `lumia.origin`, `lumia.subOrigin`, `lumia.type`, `lumia.<extraSettings key>` (nested objects one level as `lumia.<key>.<child>`), `lumia.host`, `lumia.port` |

### Not covered / untested

- Streamer.bot's "empty brightness keeps the current level" is not possible: an omitted brightness is Lumia's default 100.
- Not run against real Lumia Stream (it was not running on the dev PC). Tests use in-memory REST and websocket servers
  shaped after the docs' examples and event mocks.
- Not offered: the HTTPS/WSS variant on port 39232 (`wss://127.0.0.1:39232/api?token=`, when HTTPS is on in Lumia),
  colour temperature (`CT` 2900-7000 K in the value object), `GET /api/meta` capability checks (older Lumia answers
  `{"message":true}` even for unknown names), Twitch threaded replies (`replyParentMessageId`). Close codes are not
  told apart (1000 API off / app closing, 1012 restarting after a settings change, 1006 before open = bad token or API
  off); every close goes through the normal reconnect backoff.

### Verification against the docs (2026-10-28)

Sources: Lumia's Developer-Docs repository (github.com/lumiastream/Developer-Docs, HEAD `072ac46`, 2026-10-13;
`docs/rest/*`, `docs/websockets/*`, `docs/tts/*`, `docs/alerts/*`, cheatsheet), the live dev.lumiastream.com pages
(spot-checked, same text), Lumia's MCP server (github.com/lumiastream/MCP `86c5f20`, 2026-08-18, npm `@lumiastream/mcp`
0.5.0: exact request bodies), `lumia-types` (github.com/lumiastream/lumia-types `db603e0`, 2026-10-21, npm 4.3.3:
`api.schema.ts`, `event.types.ts`), and the older official SDKs (Lumia-SDK-CSharp `bf9e34f` 2023-01-30, Lumia-SDK-JS
`3cca5e4` 2023-01-10).

Verified as matching: port 39231 and `/api/send`, `/api/retrieve` (`slim=true`); `?token=` on REST and websocket;
`{type, params}` bodies for every type Cuebot sends (commands, alert, hex/rgb colour with brightness, transition,
duration, hold and lights, to-default, chat bot with `userToChatAs: "self"`, tts, start/stop/set-lumia,
toggle-stream-mode, get/update-variable-value); retrieve fields (`options.<type>.values`,
`options.tts.values.voices`, `lights`, `premium`); error shapes; the websocket URL with `name`, nothing sent on it,
`socketapi:*` replies ignored; the event envelope and the chat, deletion, command, alert and states payloads (the
fakes' events are the docs' event mocks).

Changed:
- Set Color's Skip Queue was missing because the Developer-Docs pages do not show it; `lumia-types` and the official
  SDKs do (`skipQueue`), so Set Color and Send Command now offer it.
- Chat bot platforms: `tiktok` (in no source, and Lumia reports success for an unknown platform) removed; `discord`
  and "all" (no platform) added.
- `extraSettings` values were all sent as text (a script's `{flag:false}` arrived as the truthy "false"); types are
  kept now. Script `lumia.setVariable` keeps number/boolean/object values.
- An alert's `extraSettings.type` (Twitch power-ups, Kick kicks) overwrote `lumia.type`; `origin`, `subOrigin` and
  `type` from `extraSettings` now go to `lumia.extraSettings.<key>`.
- `data.states` in a retrieve reply seeds Lumia's states before the first `states` push (documented for the websocket
  retrieve; the REST example does not show it, so it may never come).

Not settled by the docs: whether `twitch_point`, `twitch_extension`, `kick_point` and `pulse` events (only in
`lumia-types`) are still pushed (they would arrive as `lumia.event`); whether `value: null` toggles exactly like an
omitted value; the REST answer to a bad token (the fake's 401 body is a guess); whether REST retrieve has `states`;
`#AARRGGBB` and 3-digit hex (Lumia documents only #RRGGBB); kick/discord chat bot support beyond `lumia-types`.
Docs-internal inconsistencies, where Cuebot follows the examples: TTS voices at `options.tts.values.voices` (one page
says `options.tts.voices`), `lights` directly under `params`, `alert` rather than `send-alert` for TTS alerts.
## OBSBOT (OBSBOT Center OSC)

### Options considered

| Option | Official? | Platforms | Verdict |
| --- | --- | --- | --- |
| **OSC server of OBSBOT Center** (UDP or TCP; "OBSBOT Center OSC definition" spreadsheet, rev. 2026-05-08, linked from obsbot.com/explore/obsbot-center/osc) | Yes | Center: Windows, macOS. Tail 2 / Tail Air also run an OSC server on their own IP (port 57110), with their own sheets: Center's general commands minus the PC/Tiny/Meet ones, and their own `/OBSBOT/Camera/Tail2/` or `/OBSBOT/Camera/TailAir/` commands | **Chosen** |
| OBSBOT SDK (C++, by application) | Yes, not public | Win/macOS/Linux | Needs an agreement and native binaries; not usable in a clean-room public build |
| UVC/V4L2 pan-tilt-zoom controls | Standard | Linux/Windows | Only PTZ/zoom, none of the AI/preset features; would be a separate generic-webcam feature |

**Linux:** OBSBOT Center has no Linux build. Linux users can point Cuebot at a **Tail 2 / Tail Air** directly (they run
their own OSC server on port 57110, per OBSBOT's Tail 2 and Tail Air sheets; choose "Tail 2 direct" / "Tail Air
direct" as the series), or at OBSBOT Center on a Windows/Mac PC on the network. Tiny/Meet webcams plugged into a Linux
PC are not reachable through OSC.

**Direct to a Tail:** with series Tail 2 direct / Tail Air direct, Cuebot sends no device index, maps the Tail
sub-actions to the camera's own prefix, uses port 57110 when the Port is left at Center's 16284, applies the camera's
limits (Tail 2: pan ±159°, pitch ±62°, zoom speed 0-10, AI mode 0/1, one five-value `SetTrackingSpeed`; Tail Air: AI
modes 0, 1, 2, 3, 6, 7, tracking speed 0-2), refuses sub-actions the camera's sheet does not list (field of view,
wake/sleep, PC recording, Tiny/Meet commands, focus mode, axis locks, ...; raw OSC still goes out), and repeats the
hello on every poll because Tail 2 hardware is reported to answer only that (with its whole state).

### Wire format (`Obsbot/Osc.cs`)

OSC 1.0: address and type tags NUL-terminated and padded to 4 bytes, big-endian int32/float32 (h, d, s, b, T, F, N are
decoded too; bundles are flattened). TCP uses OSC 1.0 stream framing: an int32 length before each packet. OBSBOT Center
writes the length big-endian; Bitfocus Companion's module notes Tail hardware writes it little-endian, so a length
of at most 64 KiB in either byte order is accepted per frame. UDP: Cuebot binds the reply port (default 57120; 0 = any)
and sends from it, so Center can answer either to the source port or to its configured reply port; datagrams from
other hosts are ignored.

### Session

1. Send `/OBSBOT/WebCam/General/Connected ,i 0` (repeated while UDP gets no answer). Center replies
   `/OBSBOT/WebCam/General/ConnectedResp ,i 1` and `DeviceInfo`. TCP counts as connected once the socket is up; UDP once
   anything arrives. Commands can be sent over UDP before that (some setups never answer).
2. Every *Poll seconds* (default 5): `GetDeviceInfo`, `GetZoomInfo`, `GetGimbalPosInfo` and, per series, Tiny
   `GetAiTrackingInfo` / `GetPresetPositionInfo`, Meet `GetVirtualBackgroundInfo` / `GetAutoFramingInfo`. UDP silence
   for 3 polls (at least 15 s) = disconnected; Cuebot keeps saying hello.
3. Replies: `DeviceInfo ,isisisisiii` (4 x connected+name, selected index, run state 0 sleep/1 run, type),
   `ZoomInfo ,ii` (zoom 0-100, fov 0/1/2 = 86°/78°/65°; -1 "custom" as real devices send it), `GetGimbalPosInfoResp
   ,iii` (roll, pitch, yaw; the two-value form real devices send is read as [yaw, pitch] from Center and [pitch, yaw]
   from a Tail 2 direct), `Tiny/AiTrackingInfo ,i`, `Tiny|Meet/PresetPositionInfo ,isisis`,
   `Meet/VirtualBackgroundInfo ,i`, `Meet/AutoFramingInfo ,i`. A reply with one extra leading int carries the device
   index (the sheet's device column) and is accepted too. `General/PresetPositionInfo` is not in any sheet (Companion
   reports it from Tail 2 hardware) and is read like the Tiny one.
4. On disconnect: `/OBSBOT/WebCam/General/Disconnected ,i 0`.

Commands take an optional first int selecting the device (0-3; omitted = the camera selected in Center), then the
value(s). PC recording/snapshot/high-fps recording, `Connected`, `Disconnected` and `SelectDevice` take one argument.
A Tail reached directly takes no device index.

### Mapping

Streamer.bot's OBSBOT page (docs.streamer.bot/guide/integrations/obsbot) is an empty "missing documentation" stub and
its docs repository has no OBSBOT triggers, sub-actions or variables, so there is no documented list to match; Cuebot
covers OBSBOT Center's whole published command set instead.

| Cuebot sub-action | OSC |
| --- | --- |
| `obsbot.selectDevice` | `General/SelectDevice` |
| `obsbot.wakeSleep` (Wake/Sleep/Toggle) | `General/WakeSleep` |
| `obsbot.resetGimbal` | `General/ResetGimbal` |
| `obsbot.setZoom` (0-100, optional speed 0-11) | `General/SetZoom` or `General/SetZoomSpeed` |
| `obsbot.zoomLimit` (Max/Min) | `General/SetZoomMax` / `SetZoomMin` |
| `obsbot.moveGimbal` (Up/Down/Left/Right/Stop, speed, optional duration) | `General/SetGimbalUp/Down/Left/Right` (0 stops; Stop sends 0 to all four) |
| `obsbot.setGimbalPosition` (pan, pitch, speed; optional decimals) | `General/SetGimMotorDegree` / `SetGimMotorDegreeEx` |
| `obsbot.setView` | `General/SetView` |
| `obsbot.setMirror` | `General/SetMirror` |
| `obsbot.recording` (PC / high-fps / Tail camera) | `General/SetPCRecording`, `General/SetHighfpsRecording`, `Camera/Tail/SetRecording` |
| `obsbot.snapshot` (PC / Tail camera) | `General/PCSnapshot`, `Camera/Tail/Snapshot` |
| `obsbot.setImage` | `SetAutoFocus`, `SetManualFocus`, `Tail/SetFocusMode`, `SetAutoExposure`, `SetExposureCompensate`, `SetShutterSpeed`, `SetISO`, `SetAutoWhiteBalance`, `SetColorTemperature`, `SetWhiteBalanceShift` (floats), `SetRBGain` |
| `obsbot.aiLock` (Lock/Unlock/Toggle) | `Tiny/ToggleAILock` |
| `obsbot.triggerPreset` (1-3, series) | `Tiny/TriggerPreset` / `Camera/Tail/TriggerPreset` |
| `obsbot.setAiMode` (series) | `Tiny/SetAiMode` / `Camera/Tail/SetAiMode` |
| `obsbot.setTrackingMode` | `Tiny/SetTrackingMode` |
| `obsbot.setTrackingSpeed` (Both/Pan/Tilt) | `Tiny/SetTrackingSpeed`, `Tail/SetTrackingSpeed`, `Tail/SetPanTrackingSpeed`, `Tail/SetTiltTrackingSpeed` |
| `obsbot.setAxisLock` | `Tail/SetPanAxisLock` / `SetTiltAxisLock` |
| `obsbot.setVirtualBackground`, `obsbot.setAutoFraming`, `obsbot.standardMode` | `Meet/SetVirtualBackground`, `Meet/SetAutoFraming`, `Meet/SetStandardMode` |
| `obsbot.getState` → `obsbot.isConnected`, `obsbot.deviceIndex/deviceName/awake/zoom/fov/fovDegrees/pitch/yaw/aiLocked/virtualBackground/autoFraming/deviceCount`, `obsbot.presetN.exists/name` | the Get* queries |
| `obsbot.sendOsc` | any address and arguments |

Triggers (Cuebot extras): `obsbot.connected`, `obsbot.disconnected`, `obsbot.deviceChanged`, `obsbot.sleepStateChanged`
(filter Awake/Asleep), `obsbot.zoomChanged` (+ `obsbot.oldZoom`), `obsbot.gimbalMoved`, `obsbot.aiTrackingChanged`
(filter Locked/Unlocked), `obsbot.message` (any non-state message: `obsbot.address`, `obsbot.args` JSON,
`obsbot.arg0..2`; filter by address). All carry the state variables above plus `obsbot.host/port/transport`.

Script functions: `obsbot.isConnected`, `connect`, `disconnect`, `getState`, `send({address, args})`, `setZoom`,
`setGimbal`, `triggerPreset`.

### Sources

- OBSBOT, *OBSBOT Center OSC definition* (xlsx, rev. 2026-05-08, served with Last-Modified 2026-10-10; still the only
  Center revision linked on 2026-10-28):
  https://resource-cdn.obsbothk.com/download/obsbot-tail-2/osc/obsbot_center_osc_definition20260508.xlsx, linked from
  https://www.obsbot.com/explore/obsbot-center/osc. Every Center address, argument and value range above comes from it.
- OBSBOT, *Tail 2 OSC definition* (https://resource-cdn.obsbothk.com/download/obsbot-tail-2/osc/obsbot_tail_2_osc_definition.xlsx,
  Last-Modified 2025-08-20) and *Tail Air OSC definition*
  (https://resource-cdn.obsbothk.com/download/obsbot-tail-air/osc/OBSBOT%20Tail%20Air-%20OSC%20Definition.xlsx,
  Last-Modified 2025-03-06), linked from the same page: the cameras' own servers, port 57110 (TCP and UDP), replies
  from 57120 (UDP), "only OSC 1.0" over TCP.
- OBSBOT's TouchOSC sample layouts for Center (`OBSBOT Webcam Sample-UDP.tosc` / `-TCP.tosc`, 2025-03-06): how replies
  are parsed by OBSBOT itself.
- Bitfocus Companion `companion-module-obsbot-osc` (MIT; main `692270f`, 2026-10-05; HELP.md, `src/tcp.ts`,
  `src/config.ts`, `src/api.ts`; community, read for the wire details only): TCP length byte orders, reply port 57120
  for Center, two-value gimbal replies, fov -1, Tail 2 ignoring Get* queries.
- Port 16284 as OBSBOT Center's usual receive port: ObsbotSharp's README and community controllers; none of OBSBOT's
  documents give Center's ports (they are settings in Center), so Cuebot only defaults to it.
- OSC 1.0 specification (opensoundcontrol.org).

### Not covered / untested

- Not run against a real OBSBOT Center or camera (neither is installed here). Tests drive the real UDP and TCP
  transports against an in-process OSC server on 127.0.0.1 that answers as the spreadsheets describe (and, in Tail 2
  hardware mode, as Companion reports the real camera behaving).
- Model-specific AI-mode and tracking-speed numbers are passed through as numbers (their meaning differs per model; the
  sub-action help lists them). Tail 2 hardware's extra commands (`SetAiEnable`, `SetAutoZoom`, `SetOnlyMe`) are
  reachable with `obsbot.sendOsc`. The Talent / Talent 2 sheets were not examined.

### Verification against the docs (2026-10-28)

Verified as matching the Center sheet: the spelling and casing of every address Cuebot sends or parses (General,
Camera/Tail, Tiny, Meet, the Get* queries and every reply); argument counts and types (all int except
`SetWhiteBalanceShift` ff and `SetGimMotorDegreeEx` fff); every value table and range; the optional device index and
the one-argument commands without it; the hello/goodbye handshake; the reply layouts.

Changed:
- A Tail 2 / Tail Air reached directly got Center's `/OBSBOT/Camera/Tail/` addresses, a device index and Center's
  limits, none of which its own sheet has; the new "Tail 2 direct" / "Tail Air direct" series follow the camera sheets
  (see "Direct to a Tail" above), and polling repeats the hello so a Tail 2 that ignores Get* is not taken for lost.
- The gimbal reply in its two-value form (what real devices send, per Companion) never filled the yaw and put a Tail 2's
  yaw into the pitch.
- Ranges: Center's Tiny `SetTrackingSpeed` is 0-2 (was 0-5), Center's Tail `SetAiMode` 0-7 (was 0-9), the camera
  number 1-4 (16 was accepted).
- Gimbal Stop sent 0 to Up and Left only; it now stops all four direction addresses.
- Help texts: the Tiny 2 Lite / Tiny SE AI-mode and tracking-mode limits, and that shutter speeds 12.5, 6.25 and 2.5
  cannot be sent exactly (the sheet types the value as int while listing them).
- fov -1 shows as "custom"; the setting comments no longer present 16284 / 57120 as OBSBOT's documented Center ports.

Not settled by the docs: Center's default receive and reply ports and whether it answers the source port; the TCP
length byte order (only Companion describes it); whether Center adds a device index to replies; the real arity of
the gimbal reply (sheet 3, devices 2); what fov -1 means; device-type numbers above 3 (OBSBOT's own sample treats 6 as
a Tiny); whether Up 0 also stops a downward move; Meet presets (the sheet has `Meet/GetPresetPositionInfo` but no
`Meet/TriggerPreset`; Cuebot sends `Tiny/TriggerPreset`); the Tail 2 five-value tracking speed's auto flags (Cuebot's
reading: Both = [speed,1,5,1,5], one axis = custom with that axis manual); whether Tail hardware answers Get* at all.
OBSBOT's own UDP sample sends `GetGimPosInfo` and `Connected 1`, while all three sheets say `GetGimbalPosInfo` and
"0 recommended"; Cuebot follows the sheets.

## mtion studio ("mtion worlds")

### Options considered

| Option | Official? | Verdict |
| --- | --- | --- |
| **Developer API** (HTTP on localhost:35393 while mtion studio runs; docs.mtion.xyz/mtion/developer-api/external-triggers) | Yes | **Chosen**: the only documented external interface |
| mtion worlds SDK | Yes | For building content inside mtion, not for remote control |
| WebSocket / push events | none documented | – |

### API (`Mtion/MtionApi.cs`)

No authentication; `http://localhost:35393` (Cuebot's default host is `localhost`, as in the docs). `GET
/active-clubhouse` → `{id, name, description, external_trigger_datas:[...]}` or 404 (none open); `GET /clubhouses`;
`GET /external-trigger/triggers` → `[{id, name, output_parameters:[{parameter_index, name, data_type:
string|number|bool|enum}]}]` (404 = no clubhouse); `PATCH /external-trigger/fire-trigger/:id` with
`Content-Type: application/json` and body `[{"parameter_index":0,"value":...}]` → the trigger (404 = "No clubhouse
open or trigger not found", reported as not found and not sent). Also documented, not used by Cuebot: `GET
/clubhouse/:id`, `GET /external-trigger/trigger/:id`, and firing by `GET` on the fire path with
`?parameter_data=[...]`. Cuebot converts each value to its parameter's type (enum = option number) and only sends the
parameters given.

There is no push channel: the optional watcher polls `/active-clubhouse` (default every 5 s) to show "running" on the
dashboard, publish started/closed events and notice a different clubhouse.

### Mapping

| Streamer.bot (docs.streamer.bot/api/sub-actions/integrations/mtion) | Cuebot |
| --- | --- |
| Send Trigger → `mtion.running`, `mtion.trigger.found`, `mtion.trigger.sent` | `mtion.sendTrigger` (trigger name or id + parameters as `name=value` lines or JSON; also sets `mtion.trigger.id/name`) |
| Get Active Clubhouse → `mtion.running`, `mtion.clubhouse.found/id/name/description` | `mtion.getActiveClubhouse` |
| – | `mtion.getTriggers` → `mtion.triggerCount`, `mtion.triggerNames`, `mtion.triggers` (JSON) |
| – (Streamer.bot documents no mtion triggers) | Triggers `mtion.connected`, `mtion.disconnected`, `mtion.clubhouseChanged` (`mtion.clubhouse.*`, `mtion.oldClubhouse.id/name`), `mtion.triggerSent` |
| – | Script functions `mtion.isRunning`, `sendTrigger`, `getActiveClubhouse`, `getTriggers`, `getClubhouses` |

Streamer.bot's Send Trigger parameters are undocumented (`parameters: []`), so how it fills trigger parameters is
unknown; Cuebot's name=value form is its own.

### Not covered / untested

- Not run against a real mtion studio (not installed here); tests use an in-memory HTTP handler built from the docs.
  The API page was last updated 2024-04-17 and mtion has since been renamed "mtion worlds" (Alpha); the endpoints may
  have changed.
- The docs say the API "can be called at localhost:35393"; whether it binds loopback only, and whether the port can be
  changed, is not said. Host and Port are configurable.

### Verification against the docs (2026-10-28)

Sources: mtion's *External Triggers* page (https://docs.mtion.xyz/mtion/developer-api/external-triggers, GitBook "last
updated" 2024-04-17; the Developer API section has no other page, per docs.mtion.xyz/mtion/llms.txt). Community
clients, read for the wire format only: Mix It Up's `MtionStudioService.cs` (MixItUpBot/Desktop `c5a3497`, 2025-10-20)
and Rumbling Universal Mayhem Plugin's `mtionConnector.ts` (Renpona, 2024-05). Streamer.bot's mtion sub-action pages
(v0.2.5) for the variable names.

Verified as matching: port 35393, plain HTTP, no authentication; every path and method Cuebot uses; the fire body (the
test body equals the docs' sample apart from whitespace) and sending only some parameters (both clients do); the value
types per `data_type`; every response field name; the documented meaning of each 404.

Changed:
- Default host `127.0.0.1` → `localhost` (the docs' and Mix It Up's address; a listener that checks the Host header
  would refuse `127.0.0.1`). Saved settings keep their host.
- A 404 from the fire request itself reported the trigger as found; it now reports not found (the docs: no clubhouse
  open or trigger not found).
- The fire request's Content-Type was `application/json; charset=utf-8`; now plain `application/json` as in the docs.
- The fake now also answers `GET /clubhouse/:id`, `GET /external-trigger/trigger/:id` and the GET form of firing, as
  documented.

Not settled by the docs: whether a fractional `number` (Cuebot sends 1.5 as given) is accepted; whether an enum value
is a 0-based option index (the API exposes no option list); whether omitted parameters fall back to their defaults;
error statuses other than 404 and the error body format; whether the Host header is checked, the port configurable,
or the listener IPv4 or IPv6; whether the API still exists unchanged in "mtion worlds"; how Streamer.bot fills trigger
parameters.

## StreamFog: not supported

StreamFog (AR lenses, outfits, video backgrounds) publishes no API, SDK or protocol documentation (streamfog.com/help
has none). Streamer.bot's integration (docs.streamer.bot/guide/integrations/streamfog: a per-user "Remote Key" copied
from StreamFog's Streamer.bot page; sub-actions Activate Lens, Activate Lens by Name, Deactivate Lens, Activate Outfit,
Activate Outfit by Name, Deactivate Outfit / Reset Look, Play Video Background, Play Video Background by Name; mostly
marked "wip") is a private partnership whose wire protocol is not public. Reverse-engineering it would break the
clean-room rule, so Cuebot implements nothing and the StreamFog tab says why. Users can still show or hide StreamFog's
OBS browser source with the OBS sub-actions.

## VTuber apps: veadotube, VNyan, VRChat, Warudo

All four were checked live on 2026-10-31 (veadotube mini 2.2 on Linux, VNyan under Wine, Warudo 0.15.1 under Proton);
VRChat only through unit tests (it stopped at its terms page before OSC came up).

### veadotube (`Veadotube/`)

- Source: veado.tube/docs/tech/api (instances, nodes channel) and /docs/tech/data (data folder).
- Discovery: each open program writes `~/.veadotube/instances/<type>-<hex launch>-<hex pid>` (`%USERPROFILE%\.veadotube`
  on Windows) with JSON `{name,id,version,language,server,time}`. Files older than ~10 s or without `server` are skipped;
  the first one opened is used unless the setting names one. The port changes every launch, so the address is looked up
  on every (re)connect; "not open" is a retryable status (`StreamAppUnavailableException`).
- WebSocket `ws://<server>/?n=Cuebot` (unnamed clients are rejected); messages are `<channel>:<json>`.
- On connect: `nodes:{"event":"listen"}` and `nodes:{"event":"list"}` (listen alone does not answer). For each
  `stateEvents` and `boolean` node: a payload `listen`, then `list`+`peek` (states) or `get` (boolean).
- Commands: stateEvents payload `set|push|pop|toggle` with `state`, or `clear`; boolean `set` with `value` or `toggle`;
  number `set|add` with `value`. veadotube does not acknowledge them; the listen updates confirm.
- A boolean answering `{}` has no value yet and is treated as off, so the first push-to-talk press is a change.
- Checked live on veadotube mini 2.2 (2026-11-11): right after the upgrade veadotube sends
  `instance:{"event":"info",name,id,version,language,server}` (used for the status when connected by address); a
  stateEvents `listen` answers at once with `list` and `peek`; a boolean `get` gets no answer while it has no value, and
  `clear` is not announced; `thumb {state}` answers `{state,hash,width,height,png}` (128x129 PNG); an unknown state id in
  `thumb` answers with empty fields. Node's built-in WebSocket client (undici) cannot connect to veadotube's server
  (Python websockets and .NET can).
- Numbers: nodes of type `number` are listened to and read (`get` answers `{value}` or `{value,min,max}`). A `set`
  without a range drops the node's range (documented), so Cuebot sends the known range back with the value unless new
  `min`/`max` are given (`value:{value,min,max}`); `add` keeps it. Changes become `veadotube.numberChanged`.
- State lists are read again whenever the node list arrives and when `peek` reports a state id that is not in the list
  (another avatar loaded). Thumbnails are asked for when a state's `thumbHash` changes (64 per node at most).
- Timed changes ("For seconds"): push is undone by pop, pop by push, toggle by toggle, set/random by set to the state
  before (or clear); a boolean by setting it back. One pending undo per node (and state, for push/pop/toggle); a
  second timed change restarts the timer and keeps the first undo; dropped when the session ends.
- Other instances: a step naming another open veadotube (name, id, type or address) uses one-off GET commands
  (`http://<server>/?cmd0=nodes&cmd1=<type>&cmd2=<id>&cmd3=<event>&cmd4..`; `nodes` lists `type id` lines,
  `nodes <type> <id>` its name, `list`/`peek`/`get` answer as text). veadotube 2.2 answers every GET with the header
  `Veadotube-Command: Error`, also when the command worked, so the header is ignored.

### VNyan (`Vnyan/`)

- Sources: the VNyanDoc GitHub wiki (How-To "VNyan Rest API", Callback-Nodes "API Message"/"WebSocket", Action-Nodes
  "Websocket Message", Parameters "OSC Parameters"/"VRChat OSC Parameters", Miscellaneous), read 2026-11-11. The wiki
  has no Streamer.bot section (empty heading) and gives no default for the WebSocket or OSC receiver ports.
- HTTP API: `POST http://<host>:8069/` with `{"action":"<name>","payload":{..}}`. VNyan answers `{"result":0}` when it
  got the message; that reply was observed live, it is not documented, so a non-zero result being a refusal is
  Cuebot's reading. It fires the API Message node whose action name matches exactly (case-sensitive; an empty node
  name matches nothing); the payload goes to its outputs (`Payload` dict, `json` text) and, when the node names one, a
  VNyan dictionary. VNyan cannot say whether a node used it. No persistent connection.
- POST rather than the GET form (`/?action=Name&k=v`, since 1.4.1) because GET does not fill the node's `json` output.
- The docs only name `http://localhost:8069`; reaching it from another PC is untested, so errors for a non-loopback
  host say so.
- Payload numbers: the docs call the payload "key/value pairs of strings" but their POST example sends `"key2":2`.
  Cuebot sends name=value lines as strings by default; "As numbers" sends plain numbers (`-?(0|[1-9]\d*)(\.\d+)?`, so
  `007`, `1,5` and `1e5` stay text) unquoted. A JSON object typed into the step keeps its value types. Whether VNyan's
  dictionary treats `"100"` and `100` differently has not been checked live.
- Variables in the payload (and in the parameter bundle) are filled in after the text is parsed
  (`ParameterDescriptor.ResolveVariables = false`), so a value with quotes, `=` or line breaks stays one value; a JSON
  object that is only valid once variables are in (`{"n":%amount%}`) is resolved as a whole first.
- WebSocket receiver (Settings > Misc > Websockets, Start Receiver): `ws://<host>:<port>/vnyan`, port 8000 in the
  docs' Mix It Up example. A text message fires WebSocket nodes whose Command Text equals it or is its first
  space-separated part; the number after it is the node's `Value` (an int). Cuebot opens the connection on the first
  command and keeps it (`Common/KeptWebSocketSender`, shared with Warudo), opening it again after VNyan closed it.
- OSC receiver (UDP, port under Settings > Misc, no documented default so the setting starts empty):
  `/VNyan/Param/Float (name:s, value:f)` and `/VNyan/Param/String (name:s, value:s)` set `[name]` / `<name>`;
  bundles are accepted (several parameters go as one bundle, time tag 1). `/avatar/parameters/<name> (f)` is
  accepted too. VNyan's own strings name these addresses in lower case; Cuebot sends the documented casing.
- VNyan → Cuebot: the "Websocket Message" action node sends text to a WebSocket server address. Cuebot can listen on
  `ws://127.0.0.1:<port>/` (default 8070, any path, loopback only) and publishes `vnyan.message`: JSON objects give
  every key as an argument plus `vnyanCommand` (command/event/action/name) and `vnyanValue` (value); other text is
  split like VNyan's WebSocket node (first word, then the rest, a number when it is one). Whether the node keeps its
  connection open, and whether it needs VNyan's own receiver on, is unchecked.
- Not done: a VNyan plugin (`VNyanInterface.dll` trigger listener / `callTrigger` / parameters) would need Unity and
  VNyan's reference assembly and a live test; and ready-made graph files need VNyan to export them.
- Still to check live: the reply for unknown or malformed messages, which address 8069 binds to, the default OSC and
  WebSocket ports, whether `"100"` and `100` differ in VNyan's dictionary, and the installed version.

### VRChat (`Vrchat/`)

- OSC over UDP (docs.vrchat.com/docs/osc-overview): send to 9000, listen on 9001 (reuses `Obsbot/Osc.cs` and
  `UdpOscTransport`). `/avatar/parameters/<name>` with bool/int(0-255)/float(-1..1), `/chatbox/input [s, T|F, T|F]`
  (144 characters), `/chatbox/typing [T|F]`. Incoming `/avatar/change` and parameter changes become triggers;
  fast-changing built-ins (Velocity*, AngularY, Upright, Voice, Viseme, gesture weights...) are remembered only.
- If 9001 is taken, the transport binds another port: sending works, hearing does not, and the status says so.

### Warudo (`Warudo/`)

- Warudo's built-in Stream Deck plugin service, `ws://localhost:19069/` (websocket-sharp, localhost only; under Proton
  it binds `::1`, hence the `localhost` default). Started only once the opened scene has an On Stream Deck node, and
  stopped when the last one is removed (so switching to a scene without one closes the connection; auto-reconnect
  picks it up again once a node exists).
- Envelope `{"action":..,"data":{..}}` both ways; errors are `{"action":..,"error":".."}` without `data`. Requests:
  `trigger` / `toggle` `{receiverName}`, `message` `{receiverName,message}` (rejected without `message`),
  `getReceivers {type: trigger|toggle|message}` (answer `{"action":"getReceivers","data":[names]}`, no type in the
  answer), `getToggles` (answer `{name: bool}`). Warudo broadcasts `toggle {receiverName,state,isResponse}` on every
  toggle change, also from the Warudo UI.
- Answers carry no request id, so an answer belongs to the oldest open request with the same `action`, error or not
  (an error fails that request at once). A request that times out keeps its place, so its late answer is used up there
  instead of answering the next request; one whose answer never comes is dropped after 4x the request timeout.
- On/Off for a toggle whose state Cuebot does not know asks `getToggles` first (Warudo can only flip), and the step's
  result is the state from the following `toggle` broadcast (2 s wait). A broadcast without a boolean `state` is
  ignored; one naming an unlisted toggle re-reads the receivers. Receivers are also re-read for editor suggestions
  older than 10 s. Sending to a receiver that is not listed still sends, with one log warning per name and session.
- When 19069 refuses, a TCP probe of 19190 (below) tells "Warudo running, no On Stream Deck node in the scene" apart
  from "Warudo not running".
- Source: Hakuya Labs' public example source for the Stream Deck plugin
  (github.com/HakuyaLabs/WarudoPluginExamples/tree/main/StreamDeck, which docs.warudo.app/docs/scripting/overview
  names as the built-in plugin's source) and the `WebSocketService` envelope in the SDK stubs
  (github.com/HakuyaLabs/Warudo-Mod-Tool). The node names and ports (Exit; If Yes/If No; Message) are from
  docs.warudo.app/docs/blueprints/miscellaneous/streamdeck-integration. Confirmed live on Warudo 0.15.1.
- Warudo's own WebSocket endpoint `ws://localhost:19190` (docs.warudo.app/docs/scripting/api/external-callbacks): a
  connection without a `Channel` header goes to the raw-message pipeline, which fires On WebSocket Message Received
  with the text. Cuebot opens it on the first message (send only, anything received is ignored) and reopens it once
  when a send fails. The On WebSocket Action node's JSON format is not documented, so Cuebot has no step for it.
- Warudo -> Cuebot: the MIT WarudoStreamerBot plugin (github.com/dbqt/WarudoStreamerBot, v0.0.4) talks to Cuebot's
  Streamer.bot-compatible server: GetEvents, GetActions, DoAction with the request `id` set to the action name and
  `args` a string dictionary (or `null`). It never authenticates, so with Authentication on DoAction is refused
  (Cuebot logs this once per client). Its event node is commented out in v0.0.4, so it sends no Subscribe today; the
  test still covers the `{"Twitch":[..]}` Subscribe it would send. Covered by
  `StreamerBotWebSocketTests.Warudo_streamerbot_plugin_messages_work_as_sent` and
  `StreamerBotAuthenticationTests.Warudo_plugin_without_authentication_is_refused_with_one_warning`.

## VTuber apps: PNGTuber Remix, Animaze, VMC

Added 2026-10-31. PNGTuber Remix 1.4.7 was checked live on Linux (every step, against its demo model); the 2026-11-11
changes are covered by unit tests against a fake modelled on the app's handler, not re-checked live. VMC was checked
live against VSeeFace 1.13.38c under Wine: VSeeFace received Cuebot's packets (a VMC head bone turned the avatar), but
in that setup expressions did not visibly change, so blend shapes are covered by unit tests of the documented messages.
Animaze is covered by unit tests only (it is Windows-only and installs through Steam).

### PNGTuber Remix (`PngtuberRemix/`)

- Sources: the WebSocket documentation PNGTuber Remix ships in-app (its "WebSocket documentation" button, also written
  next to the program as `WebsocketDocumentation.txt`; no throwables), the official `OnlineDoc/websocket.md`
  (github.com/MudkipWorld/PNGTuber-Remix, branch 1.4.x; adds `throw_item`, `throw_random`, `toggle_throwables_pause`,
  `variance`/`both_sides`, `match_sprite`/`reverse`), and the app's own open-source server
  `Main/Scripts/websocket_handler.gd` (same on 1.4.x, master and v1.4.7; last changed 2026-05-20) where the docs are
  silent. Reviewed again 2026-11-11.
- `ws://127.0.0.1:9321` (port set in the app's WebSocket tab; the server starts on Start or Auto-Start). TLS exists only
  as a Godot export variable, not in the app's UI, so Cuebot uses `ws://` only. Requests are `{"event":<name>,..}`.
  Each message is handled in order and answered only to its sender, with the same `event` and `result`
  `success`/`failed` (`error`, `identifier`). `ping` is answered `{"event":"pong"}`; an unknown event or bad JSON
  `{"event":"error","message":..}`. Nothing is pushed or broadcast.
- **Not every request gets an answer.** A GDScript runtime error in a branch sends nothing: `general` without `key`,
  `toggle_sprite`/`toggle_group` for a sprite that does not exist (the handler reads `result.visible` from
  `{"success":false}`), and `match_sprite` with either sprite missing. Answers carry no request id. Cuebot keeps open
  requests in send order and gives an answer to the oldest open request with the same event (`error` to the oldest of
  all); requests before it will never be answered and fail at once. A timed-out request keeps its place so its late
  answer is used up there (dropped after 4x the timeout). Two open requests with the same event cannot be told apart:
  if the first is never answered, the second times out. Cuebot refuses sprites it knows do not exist (after one
  re-read), so the no-answer cases above are not sent.
- Godot parses JSON numbers as floats: `state_id: 2` arrives as `2.0` (the handler matches that on purpose) and
  `list_sprites` ids may come back as `537693010870.0`; Cuebot sends ids as whole numbers. Name lookups are exact
  (case-sensitive) and the first match wins; names are not unique. Cuebot resolves names against its cached lists and
  sends the exact spelling; for duplicate sprite names it warns and offers `Name (id N)` suggestions.
- Used: `list_states` (`states:[{id (1-based), name, is_current}]`, fails "no state buttons found" while a model
  loads), `list_sprites` (`sprites:[{name,id,visible,is_group,children,parent_id}]`; `children` are direct children,
  so `list_groups` is not needed), `state` (`state_name` or `state_id`), `show|hide|toggle_sprite` (`sprite_name`;
  toggle answers `visibility`), `toggle_sprite|toggle_group` with `match_sprite` and `reverse` (target only / target
  and attached sprites), `general` (`key`, a Godot key name for `OS.find_keycode_from_string`, plus `"0"`-`"9"`;
  answers `actions_found:["Hat (Toggle)", ..]`), `shake_sprite` (`intensity`, `duration`), `bounce_sprite` (`height`,
  `duration`), `move_sprite` (`x`,`y`,`duration`,`reset`,`reset_delay`), `animate_sprite` (`scale` or
  `scale_x`/`scale_y`, `rotation` in degrees, same timing fields), `throw_item` (`item_name`, `amount`),
  `throw_random` (`amount` optional; otherwise the app's per-trigger amount), both with optional `variance` (float;
  -1 = app setting) and `both_sides` (bool; omitted = app setting), `toggle_throwables_pause` (`pause` optional;
  answers `is_paused`; there is no read-only query), `load_model` (`file_path`, must end in `.pngRemix` or `.save`,
  case-sensitive; any failure answers only "failed to load model").
- `show|hide|toggle_sprite` and `*_group` call the same functions: both recurse into all attached sprites. Visibility,
  moves, scale and rotation are written into the current state (`save_state(Global.current_state)`), so they last.
- Effects capture the sprite's position/scale when the tween starts and save the state when it ends. Overlapping
  effects on one sprite capture a mid-way value and leave it offset, so Cuebot runs effects one at a time per sprite
  (shake = duration + 0.1 s return; bounce = 1.125 x duration; move/animate with reset = 2 x duration + reset_delay).
- `move_sprite` and `animate_sprite` set absolute values with defaults 0 (x, y), 1 (scale) and 0 (rotation), and no
  request reads a sprite's position, scale or rotation. So Cuebot requires X and Y together, and sends size and turn
  together (empty = 1 / 0).
- `throw_*` spawns one physics body per item, and fails "throwable spawner not active" when throwables are off. Cuebot
  caps the amount (setting, default 50).
- Polling: Cuebot reads `list_states` every 2 s (setting; 0 = off) and `list_sprites` every 5th time, for the "state
  changed" and "sprites changed" triggers. With polling off it pings every 15 s. Two missed answers in a row end the
  session (reconnects when that is on). The handler `print`s every message to its own log.
- After `load_model` the old state buttons may still be listed for a moment, so Cuebot re-reads every 200 ms until the
  states or sprites differ from before (2 s at most).
- Not available: a list of throwables, a sprite's position/scale/rotation, any push event.

### Animaze (`Animaze/`)

- Source: Holotech's "Animaze API" page (holotechconfluence.atlassian.net/wiki/spaces/HD/pages/157941761). The page
  documents request/answer shapes up to GetPoses; action names and fields after that (`TriggerPose`/`TriggerIdle`/
  `LoadQuickscene` with `index`, `Broadcast` with `toggle`) match the public AnimazePy client's usage.
- `ws://127.0.0.1:9000`, off until Settings > Animaze API is on. Requests `{"action":..,"id":..}`; the answer repeats
  both and adds `error` on failure. Cuebot sends a GUID id per request and matches on it.
- Lists: `GetAvatars` (`avatars:[names]`), `GetScenes`, `GetQuickscenes`, `GetEmotes`
  (`emotes:[{friendlyName,itemName}]`), `GetSpecialActions` (`specialActions:[{animName,index}]`), `GetPoses`,
  `GetIdleAnims`. Undocumented list answers are read leniently: the first array, items as strings (numbered by
  position) or objects (`index` plus a name field).
- Actions: `LoadAvatar`/`LoadScene` `{name}`, `TriggerEmote {itemName}`, `TriggerSpecialAction|TriggerPose|TriggerIdle|
  LoadQuickscene {index}`, `Broadcast {toggle}` (virtual camera).
- Event: `{"event":"AvatarChanged","new_avatar":..}` to every client; Cuebot re-reads the avatar's animations then.

### VMC (`Vmc/`)

- Source: protocol.vmc.info (Virtual Motion Capture protocol, OSC over UDP). Cuebot is a send-only "performer":
  `/VMC/Ext/Blend/Val [s name, f value]` per expression, then `/VMC/Ext/Blend/Apply`. Default receiver port 39539
  (VSeeFace, VMagicMirror, VNyan, Warudo). Reuses `Obsbot/Osc.cs` and `UdpOscTransport` (bound to an ephemeral port).
- A held expression is re-sent every 100 ms until it ends, then sent as 0, because receivers expect a stream.
- No answers exist in VMC, so the UI and docs tell users to watch their avatar.

## VTuber apps: nizima LIVE, RahiTuber

Added 2026-11-01. RahiTuber (Linux build from itch.io, October 2026) was checked live: every request form Cuebot sends
reached its state queue, including a name with an accent and quotes. nizima LIVE 2.7.0 is covered by unit tests only:
under Wine it aborts at start (unimplemented `GDI32.D3DKMTSetProcessSchedulingPriorityClass`).

### nizima LIVE (`Nizima/`)

- Source: Live2D's official Plugin API docs, github.com/Live2D/nizimaLIVEPluginAPI (README.md, methods.md).
- `ws://localhost:22022/` (localhost only; the port shows in the Plugin Manager and may change if taken). Every message
  is `{"nLPlugin":"1.0.0","Timestamp":ms,"Id":..,"Type":"Request|Response|Event|Error","Method":..,"Data":{..}}`; a
  Response or Error repeats the request's Id. Errors carry `Data.ErrorType` (InvalidToken, InvalidModelId, ...).
- Handshake: first `RegisterPlugin {Name,Developer,Version}` -> `{Token}`, saved in streamapps.json (Nizima.Token);
  later `EstablishConnection {Name,Token}` -> `{Enabled}`; `InvalidToken` (plug-in removed) means registering again. The
  user switches the plug-in on in the Plugin Manager; nizima LIVE then sends Event `NotifyEnabledChanged {Enabled}`.
  Other methods fail until then, so steps say how to switch it on.
- Events must be asked for every connection: `NotifyCurrentModelChanged`, `NotifyModelsChanged`, `NotifyItemsChanged`
  (`{Enabled:true}`).
- Used: GetModels, GetCurrentModelId, GetRegisteredModels, GetRegisteredItems, GetItems, GetEffectGroups,
  GetExpressions/GetMotions `{ModelId}`, Start/StopExpression `{ModelId,ExpressionPath}`, StopAllExpressions,
  StartMotion `{ModelId,MotionPath}`, StopMotion, TriggerModelHotkey `{ModelId,Key}` (QKeySequence text, e.g. "Ctrl+A"),
  AddItem `{SceneId,ItemPath}` -> `{ItemId}`, RemoveItem `{ItemId}`, ChangeModel `{ModelId,ModelPath}`,
  Enable/DisableEffectGroup `{GroupId}`. Steps take names; Cuebot looks up the paths and ids.

### RahiTuber (`Rahituber/`)

- Source: RahiTuber's BSD-licensed source at commit `ad8fe33` (2026-10-21, v14.51: `websocket.h`, `LayerManager.cpp/.h`,
  `MainEngine.h`), read for the wire format only; first written from the v12.0 devlog ("HTTP state triggers"). There is
  no websocket despite the class name: no events, no state readback, no auth.
- Preferences > Integration > "Control States via HTTP" (`listenHTTP`, off by default; `httpPort` 8000, listens on
  0.0.0.0 with mongoose). `mg_http_listen`'s result is ignored, so a taken port fails silently.
  `GET /state?[<state>,<active>]` (URL-encoded JSON array): `$[0]` is read as a JSON string, else as an integer turned
  into text; `$[1]` as an integer (-1 if missing). active 1 = like pressing the hotkey (for a Held state it stays down
  until a non-1 arrives), anything else = letting go. Any other path gets 400 with an empty body.
- The query is URL-decoded into a buffer exactly as long as the escaped query; Cuebot escapes the brackets, commas and
  quotes (`%5B..%5D`), so the decoded text is always shorter. An unescaped query may lose its final `]` (inferred from
  mongoose's decode bound, not seen live).
- RahiTuber replies 200, `Content-Type: application/json`, `{"state":<id printed with %d>, "active":<n>}` to every
  /state request before matching it; the state value is garbage. Cuebot takes the content type plus an `"active":<n>`
  field as RahiTuber's fingerprint, so another program on the port is reported. The connection check sends
  `["Cuebot connection check",-1]`, which matches no state and is dropped. (`""` would match the first unnamed state.)
- The request is logged to `RahiTuber_Log.txt` beside the program ("State change added to queue: <id> = <n>") and pushed
  onto an unlocked `std::deque` from the HTTP thread, while the render thread pops it once per frame
  (`LayerManager::CheckHotkeys`): requests are sent one at a time, with a 20 ms gap.
- Matching: the id is compared with both `to_string(index)` and the state's name, in list order; the first match wins.
  Unmatched ids are dropped. The queue is not drained while an avatar loads, a layer rename box is open, or the avatar
  has no states (requests then run in a burst later).
- State types (`activeType`): 0 Toggle (each press flips; optional timeout, 5 s by default; let go ignored), 1 Held,
  2 Permanent (overwrites the layer and tag defaults). Non-Held states ignore a change within 0.2 s of their last one
  (Cuebot spaces presses of one state 250 ms apart, `SameStateGapMs`). `canTrigger` 1/2 = only while talking/quiet; a
  press at another time is lost. Disabled states still consume their requests.
- States are saved in the avatar XML as `<hotkeys><hotkey enabled name activeType canTrigger timeout useTimeout ...>` in
  list order; older files have `toggle="true|false"` (false = Held) and no name. `config.xml` beside the program has
  `<Common lastLayerSet="<path without .xml>">` (fallback `lastLayers.xml`). With the optional folder set, Cuebot reads
  these (read-only, re-read when they change), suggests names, resolves names with spaces at the ends, and tails the
  log after each request to confirm it.
- Names are sent with only quotes and backslashes escaped, because its JSON reader may not decode \u escapes.
- Upstream wishes, not filed (the owner decides): a mutex on the queue, `GET /states`, state readback, a reply that says
  whether a state matched, and a log line when the HTTP port cannot be bound.
