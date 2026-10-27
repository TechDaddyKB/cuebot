# Cuebot.Integrations: protocols

This module talks to four stream apps and services over websockets (`ClientWebSocket` only, no SDKs). Everything here
comes from the vendors' public documentation; no Streamer.bot code or binaries were used. "Verified" means checked
against the public docs while writing this module. Every protocol is covered by in-memory fake-server tests in
`tests/Cuebot.Integrations.Tests`. None of them has been tried against the real app or service yet.

Settings live in `integrations.json`. Secrets (VTube Studio plugin token, Voicemod client key, Pulsoid access token,
HypeRate API key) are masked in the UI and redacted (also in URL-encoded form) from every log line and status message.
Auto-connect is off by default for all four.

Shared behaviour (`Common/IntegrationConnection.cs`): a background loop connects, tries each endpoint in turn, runs a
session and reconnects with backoff (2 s doubling to 30 s; it resets after a session that connected). It stops when
the user disconnects or the credentials are refused (`IntegrationAuthException`, status Error). It publishes
`<prefix>.connected` and `<prefix>.disconnected` (with `reason`).

---

## 1. VTube Studio: public plugin API

- Source: https://github.com/DenchiSoft/VTubeStudio (README, `Events/README.md`). Verified.
- Endpoint: `ws://localhost:8001`. The host and port are configurable; VTube Studio shows the port under
  Settings → "Start API (allow plugins)".
- Envelope (both directions):
  `{"apiName":"VTubeStudioPublicAPI","apiVersion":"1.0","requestID":"cuebot-N","messageType":"<X>Request","data":{...}}`.
  Responses echo `requestID`. Errors use `messageType:"APIError"` with `data.errorID` and `data.message`. Events have
  `messageType` ending in `Event`.
- Session:
  1. `APIStateRequest`, which reports `vTubeStudioVersion`.
  2. Authentication. With no saved token, send `AuthenticationTokenRequest {pluginName:"Cuebot", pluginDeveloper}`.
     VTube Studio then shows a popup. Cuebot waits up to 2 minutes for `data.authenticationToken` and saves it in
     `integrations.json`. `errorID 50` means the user clicked Deny; that error is fatal.
  3. `AuthenticationRequest {pluginName, pluginDeveloper, authenticationToken}`. If `authenticated:false` (token
     revoked or invalid), the saved token is cleared and a new one is requested once.
  4. `EventSubscriptionRequest {eventName, subscribe:true, config:{}}` for each of: ModelLoadedEvent,
     TrackingStatusChangedEvent, BackgroundChangedEvent, ModelConfigChangedEvent, ModelMovedEvent,
     HotkeyTriggeredEvent, ModelAnimationEvent, ItemEvent, ModelClickedEvent, ExpressionToggledEvent (beta),
     PostProcessingEvent. Failures are ignored, because older versions do not have every event.
  5. Caches for suggestions and the UI: CurrentModelRequest, AvailableModelsRequest, HotkeysInCurrentModelRequest,
     ExpressionStateRequest, InputParameterListRequest, ItemListRequest. They refresh in the background after
     ModelLoaded/ModelConfigChanged, item Added/Removed and ExpressionToggled.
- Requests used by sub-actions:
  - HotkeyTriggerRequest `{hotkeyID, itemInstanceID?}`. The name is resolved to an id from the cache; an unknown name
    is passed through as-is.
  - ModelLoadRequest `{modelID}`.
  - MoveModelRequest `{timeInSeconds 0-2, valuesAreRelativeToModel, positionX?, positionY?, rotation?, size?}`.
    Omitted values stay unchanged.
  - ExpressionActivationRequest `{expressionFile, fadeTime, active}`. Toggle reads ExpressionStateRequest first.
  - InjectParameterDataRequest `{faceFound, mode:"set"|"add", parameterValues:[{id, value, weight?}]}`. VTube Studio
    drops injected values after about 1 s, so "hold N seconds" re-sends every 500 ms.
  - ColorTintRequest `{colorTint:{colorR,colorG,colorB,colorA,mixWithSceneLightingColor}, artMeshMatcher:{tintAll, nameContains?, tagContains?}}`.
    Reset tints everything white at 255.
  - ItemLoadRequest. ItemUnloadRequest `{unloadAllInScene, instanceIDs, fileNames, allowUnloadingItemsLoadedByUserOrOtherPlugins:true}`.
    ItemMoveRequest `{itemsToMove:[{itemInstanceID, timeInSeconds, fadeMode, positionX?, positionY?, size?, rotation?, userCanStop:true}]}`.
    Assumed: leaving out a field in ItemMoveRequest leaves that value unchanged.
  - Any other request can be sent through "Send raw request" or the `vtubestudio.request` script function.
- Bot events (`vtubestudio.*`): modelLoaded, modelUnloaded (`ModelLoadedEvent` with `modelLoaded:false`),
  hotkeyTriggered, trackingStatusChanged, trackingLost and trackingFound (derived from changes in `faceFound`),
  backgroundChanged, modelConfigChanged, modelMoved, modelAnimation, itemEvent (`itemEventType` is Added, Removed,
  DroppedPinned, DroppedUnpinned, Clicked, Locked or Unlocked), modelClicked, expressionToggled,
  postProcessingChanged, and `event` (raw: `eventName` plus `data` JSON).

## 2. Voicemod: Control API (Voicemod V3)

- Source: https://control-api.voicemod.net (getting-started, api-reference). Verified. Requires the Voicemod desktop
  app, which is Windows-only. The Control API key is a partner key that Voicemod issues through a request form and
  sends by email. Cuebot cannot ship one, so the user pastes their own.
- Endpoint: `ws://localhost:<port>/v1`. The docs list the ports 59129, 20000, 39273, 42152, 43782, 46667, 35679,
  37170, 38501, 33952 and 30546 and say to cycle through them. Cuebot tries the configured port, then the last port
  that worked, then the rest of the list. The working port is saved in `LastPort`.
- Requests: `{"action":"<name>","id":"<uuid>","payload":{...}}`. The first message must be
  `registerClient {clientKey}`. The answer `{"action":"registerClient","id":..,"payload":{"status":{"code":200,"description":"Authorized"}}}`
  means the key was accepted; any other code (401) is fatal.
- Inbound messages: `{"actionType":"<kind>","actionID":"<id or null>","appVersion":..,"actionObject":{...}}`. Some use
  `type` + `payload` instead (getActiveSoundboardProfile), and the mute event is spelled `toggleMute` or
  `toggleMuteMic`. The parser accepts all of these. Answers are matched by `actionID`. Because the docs say it is
  sometimes null, an answer without one completes the oldest request waiting for that response kind.
- Status requests and their answer kinds: getVoiceChangerStatus → `toggleVoiceChanger {value}`,
  getHearMyselfStatus → `toggleHearMyVoice`, getBackgroundEffectStatus → `toggleBackground`,
  getMuteMicStatus → `toggleMuteMic`. The same kinds arrive unsolicited when the user toggles a switch in the app.
- Commands (no answer expected): loadVoice `{voiceID}`, selectRandomVoice `{mode}`, toggleVoiceChanger,
  toggleHearMyVoice, toggleBackground, toggleMuteMic, playMeme `{FileName, IsKeyDown:true}`, stopAllMemeSounds,
  setBeepSound `{badLanguage:1|0}`. The API can only toggle, so "Set ... state On/Off" asks for the status first and
  toggles only when needed.
- Lists: getVoices (`voices[] {id, friendlyName, favorited, isCustom, enabled}`, `currentVoice`), getCurrentVoice
  (`voiceID`), getMemes (`listOfMemes[] {Name, FileName, Type}`; `FileName` is the id that playMeme expects).
- Bot events (`voicemod.*`): voiceChanged (voiceChangedEvent, or a different voice in a getCurrentVoice/getVoices
  answer; args voiceId, voiceName, previousVoiceId, previousVoiceName), voiceChangerToggled, hearMyselfToggled,
  backgroundEffectsToggled (`enabled`), muteToggled (`muted`), memesMutedForMeToggled, soundboardChanged
  (unsolicited getMemes/getAllSoundboard, getActiveSoundboardProfile), licenseChanged. The answers Cuebot gets while
  loading the state at connect time are not reported as changes.
- Deprecated: the Voicemod V2 "Set censor state" API is gone; the V3 equivalent is `setBeepSound`, offered as
  "Set censor beep".

## 3. Pulsoid: realtime heart rate

- Sources: https://docs.pulsoid.net/heart-rate-and-sensor-data/read-data-via-websocket-v2 (current) and
  `read-heart-rate-via-websocket` (v1, marked legacy). Verified.
- Token: pulsoid.net/ui/keys → "Create new token" (needs a BRO plan or the trial). Scope `data:heart_rate:read`.
- Endpoint: `wss://dev.pulsoid.net/api/v2/data/real_time?types=heart_rate` by default, or
  `wss://dev.pulsoid.net/api/v1/data/real_time` (option). The token is sent as an `Authorization: Bearer <token>`
  header, which the docs allow instead of `?access_token=`, so it never appears in a URL.
- Frames:
  - v2: `{"type":"heart_rate","timestamp":"…","heartRate":{"bpm":85}}`. Other types (battery_level,
    ingestion_pause) are ignored.
  - v1: `{"measured_at":…,"data":{"heart_rate":40}}`.
  - A plain number (`response_mode=text_plain_only_heart_rate`) is also accepted.
- Errors: HTTP 401 (invalid or missing token) and 400 (error 6003, missing scope) on the upgrade are fatal; anything
  else is retried with backoff, as the docs recommend.

## 4. HypeRate: Phoenix channels

- Source: https://github.com/HypeRate/DevDocs (README, Clips.md). Verified. The API key comes from
  https://www.hyperate.io/api. The ID is the one shown in the HypeRate app; `internal-testing` sends fake data.
- Endpoint: `wss://app.hyperate.io/socket/websocket?token=<API key>`. The key must go in the URL, so it is redacted
  from all output, and the logged endpoint is just the host. This is Phoenix's V1 JSON serializer (object frames).
- Join: `{"topic":"hr:<ID>","event":"phx_join","payload":{},"ref":N}`. The reply is
  `{"event":"phx_reply","topic":"hr:<ID>","ref":N,"payload":{"status":"ok"|"error","response":{…}}}`; "error" is
  fatal. When enabled, Cuebot also joins `clips:<ID>`.
- Heartbeat every 10 s (the docs require it): `{"topic":"phoenix","event":"heartbeat","payload":{},"ref":N}`. If no
  heartbeat reply arrives for 3 intervals, Cuebot reconnects and joins again. `phx_error` or `phx_close` on the hr
  topic also triggers a reconnect.
- Data: `{"event":"hr_update","topic":"hr:<ID>","payload":{"hr":79}}`. Clips:
  `{"event":"clip:created","topic":"clips:<ID>","payload":{"twitch_slug":"…"}}` → `hyperate.clipCreated` (twitchSlug,
  clipUrl).
- Upgrade 401 or 403 means the API key was rejected; this is fatal. Assumed: HypeRate answers a bad key at the HTTP
  level; the docs do not say.

## Heart rate triggers and variables

Each reading from either source publishes `<pulsoid|hyperate>.heartRate` (args heartRate, previousHeartRate, source).
It also updates the shared `HeartRateHub` and the temporary globals `heartRate`, `heartRateSource`, `pulsoidHeartRate`
and `hyperateHeartRate`. Readings outside 1-299 BPM are ignored (0 means no sensor contact). The trigger types for each
source are:

- Heart rate pulse: every reading.
- Heart rate changed: throttled per trigger instance by a minimum interval (default 5 s) and a minimum change
  (default 1 BPM).
- Heart rate above / below threshold: fires once when the threshold is crossed. It re-arms only after the rate moves
  back by more than the hysteresis (default 5 BPM). State is kept per trigger instance.

The "Get heart rate" sub-action and the `heartrate.get` script function read the latest value (Latest, Pulsoid or
HypeRate).
