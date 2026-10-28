# Stream apps: Meld Studio, PolyPop, T.I.T.S., Lumia Stream, OBSBOT, mtion studio (and StreamFog)

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
