# Stream apps: Meld Studio, PolyPop, T.I.T.S. and Lumia Stream

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

## T.I.T.S. (Twitch Integrated Throwing System, by Remasuri3)

Code: `Tits/`. Settings `streamapps.json` → `Tits` (Host 127.0.0.1, Port 42069, AutoConnect off, AutoReconnect on).

### Source

- Remasuri3, *T.I.T.S. API* README: https://github.com/Remasuri/TITSAPI (the only public description; "extremely early").
- Streamer.bot docs (names/variables only): docs.streamer.bot/api/triggers/integrations/tits/*,
  docs.streamer.bot/api/sub-actions/integrations/tits/*, docs.streamer.bot/api/csharp/methods/integrations/tits/*.

### Protocol

Two websockets, no authentication:

- `ws://host:42069/websocket` - requests. Every message is `{"apiName":"TITSPublicApi","apiVersion":"1.0","requestID":"<id>","messageType":"<type>","data":{...}}`;
  the response echoes `requestID` with `messageType` `<X>Response` (plus a `timestamp`).
  `TITSItemListRequest` (`data.sendImage`; Cuebot sends `false`) → `data.items[{ID,name,encodedImage}]`;
  `TITSItemInfoRequest` (`data.itemID`); `TITSThrowItemsRequest` (`data.items` = ids, one picked at random per throw,
  `delayTime` seconds, `amountOfThrows`, `errorOnMissingID`) → `data.numberOfThrownItems`; `TITSTriggerListRequest` →
  `data.triggers[{ID,name}]`; `TITSTriggerActivateRequest` (`data.triggerID`, `data.triggerName` used when the id is
  empty/unknown). The README does not document errors; Cuebot treats any `messageType` containing "Error" (VTube
  Studio style `APIError` with `data.message`) as a failure.
- `ws://host:42069/events` - broadcasts `TITSHitEvent` (`itemID`, `itemName`, `triggerID`, `triggerName`, `strength`,
  `direction{x,y,z}`), `TITSTriggerActivatedEvent` and `TITSTriggerEndedEvent` (`triggerID`, `triggerName`).

The request socket is the connection (status, backoff). While it is up a second loop keeps `/events` connected with
its own backoff. If `/events` is down, broadcasts arriving on the request socket are also accepted. Item and trigger
lists are read on connect (no images) so sub-actions and filters can use names (case-insensitive; ids also accepted).

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

### Mapping

| Streamer.bot | Cuebot |
| --- | --- |
| Sub-action Send Command (Command) | `lumia.sendCommand`: Command, Command type (chat-command default, chatbot-command, twitch-points, twitch-extension, kick-points), Hold, Extra settings |
| Sub-action Set Color (Colour, Brightness, Duration, Transition, Skip Queue, Default, Light) | `lumia.setColor`: Colour (#RRGGBB, #AARRGGBB or r,g,b), Brightness, Duration, Transition, Default (sends to-default), Lights (names or brand:id), Hold. Skip Queue: not offered (see below) |
| Sub-action Set To Default | `lumia.setToDefault` |
| – | `lumia.sendAlert`, `lumia.chatbotMessage` (platform, as streamer), `lumia.tts` (voice, volume), `lumia.setLumiaState`, `lumia.setStreamMode`, `lumia.getVariable` (→ `lumia.variableValue`), `lumia.setVariable`, `lumia.send` (any Send API type + params JSON → `lumia.response`) |
| C# `LumiaSendCommand(command)` / `LumiaSetToDefault()` | `lumia.sendCommand({command, type, hold, extraSettings})` / `lumia.setToDefault()` |
| – | `lumia.isConnected`, `connect`, `disconnect`, `setColor`, `sendAlert`, `chatbotMessage`, `tts`, `getVariable`, `setVariable`, `send`, `getInfo` |
| – (Streamer.bot documents no Lumia triggers) | Triggers `lumia.connected`, `lumia.disconnected`, `lumia.chatMessage` (`user`, `userName`, `displayName`, `userId`, `message`, `messageId`, `lumia.channel`), `lumia.chatMessageDeleted` (`messageId`), `lumia.command` (`command`, `user`), `lumia.alert` (`lumia.alert`, `lumia.replay`, `lumia.dynamic.*`; filters alert names and replays), `lumia.statesChanged` (`lumia.on`, `lumia.streamMode`, `lumia.fuze`), `lumia.event` (other types, `lumia.data` JSON). Every event: `lumia.origin`, `lumia.subOrigin`, `lumia.type`, `lumia.<extraSettings key>` (nested objects one level as `lumia.<key>.<child>`), `lumia.host`, `lumia.port` |

### Not covered / untested

- Streamer.bot's Set Color "Skip Queue": Lumia's public API documents no skip-queue parameter, so it is left out.
  Streamer.bot's "empty brightness keeps the current level" is not possible either: an omitted brightness is Lumia's default 100.
- Not run against real Lumia Stream (it was not running on the dev PC). Tests use in-memory REST and websocket servers
  shaped after the docs' examples and event mocks.
