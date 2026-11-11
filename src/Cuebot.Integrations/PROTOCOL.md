# Cuebot.Integrations: protocols

This module talks to six stream apps and services. Five use websockets (`ClientWebSocket`); StreamLoots uses a
server-sent-events stream (`HttpClient`, `Common/SseTransport.cs`). Sections 7 and 8 cover music: the computer's
players (D-Bus on Linux, a PowerShell helper on Windows) and the Spotify Web API (`HttpClient`). No SDKs are used. Everything here comes from the
vendors' public documentation and, where noted, open-source clients (wire format only); no Streamer.bot code or
binaries were used. "Verified" means checked against the public docs while writing this module. Every protocol is
covered by in-memory fake-server tests in `tests/Cuebot.Integrations.Tests`. Sections 1 to 4 have not been tried
against the real service yet. Sections 5 and 6 were partly checked live (see each section).

Settings live in `integrations.json`. Secrets (VTube Studio plugin token, Voicemod client key, Pulsoid access token,
HypeRate API key, StreamLoots alert id, Crowd Control overlay key) are masked in the UI and redacted (also in
URL-encoded form) from every log line and status message. Auto-connect is off by default for all of them.

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
- Discovery: VTube Studio broadcasts `VTubeStudioAPIStateBroadcast {active, port, instanceID, windowTitle}` on UDP
  47779 every 2 s, even with the API off. Cuebot listens (shared socket, SO_REUSEADDR; only this computer's
  addresses) and, for a local host, tries the configured port first and then every other active instance's port
  (a second window takes 8002). Setting `AutoDiscover` (default on).
- Session:
  1. `APIStateRequest`, which reports `vTubeStudioVersion`.
  2. Authentication. With no saved token, send `AuthenticationTokenRequest {pluginName:"Cuebot", pluginDeveloper,
     pluginIcon}` (`pluginIcon`: the Cuebot logo, base64 PNG, exactly 128x128). VTube Studio then shows a popup.
     Cuebot waits up to 2 minutes for `data.authenticationToken` and saves it in `integrations.json`. `errorID 50`
     means the user clicked Deny; that error is fatal.
  3. `AuthenticationRequest {pluginName, pluginDeveloper, authenticationToken}`. If `authenticated:false` (token
     revoked or invalid), the saved token is cleared and a new one is requested once.
  4. `EventSubscriptionRequest {eventName, subscribe:true, config}` for each of: ModelLoadedEvent,
     TrackingStatusChangedEvent, BackgroundChangedEvent, ModelConfigChangedEvent, ModelMovedEvent,
     HotkeyTriggeredEvent, ModelAnimationEvent `{ignoreLive2DItems:false, ignoreIdleAnimations:false}`, ItemEvent,
     ModelClickedEvent `{onlyClicksOnModel:false}` (the spec leaves the default open; Cuebot filters itself),
     ExpressionToggledEvent (beta) `{sendAllActiveStatesOnSubscription:false, ignoreLive2DItems:false}`,
     PostProcessingEvent, Live2DCubismEditorConnectedEvent. A failure is logged (Info) and listed in
     `UnavailableEvents`; older and non-beta versions do not have every event.
  5. Caches for suggestions and the UI: CurrentModelRequest, AvailableModelsRequest, HotkeysInCurrentModelRequest,
     ExpressionStateRequest, InputParameterListRequest, ArtMeshListRequest, GetCurrentModelPhysicsRequest,
     ItemListRequest (with `includeAvailableItemFiles:true` only here and on request: it makes VTube Studio read its
     Items folder and can stutter), PostProcessingListRequest (presets and effect config ids, also a disk read; only
     on connect and on request). After ModelLoaded/ModelConfigChanged the model caches refresh; item
     Added/Removed/DroppedPinned/DroppedUnpinned refresh the instances only (debounced 250 ms, files excluded);
     ExpressionToggled bursts (one per expression with `justLoaded:true` on a model load) refresh once (debounced).
  6. Restore: custom parameters in `CustomParameters` are created again (ParameterCreationRequest is idempotent for
     the same plugin; VTube Studio deletes them on revoke) and the remembered color tints are sent again (VTube Studio
     resets a session's tints when it ends).
- Mid-session: an APIError 8 (needs authentication), 50 (access revoked) or 1 (API switched off) on any request ends
  the session, so the reconnect authenticates again (and asks for a new token if the old one is rejected). Error
  messages get a hint for the errors a streamer can fix (`VtsProtocol.Hint`).
- Requests that wait for a click in VTube Studio (AuthenticationTokenRequest, PermissionRequest,
  ArtMeshSelectionRequest, ItemLoadRequest with `customDataAskUserFirst`) use the 2-minute approval timeout, also
  through the raw request.
- Requests used by sub-actions:
  - HotkeyTriggerRequest `{hotkeyID, itemInstanceID?}`. The name is resolved to an id from the cache; an unknown name
    is passed through as-is. Errors 200 (queue full) and 203 (cooldown) are retried twice after 500 ms.
  - HotkeysInCurrentModelRequest `{modelID?, live2DItemFileName?}` for another model or a Live2D item.
  - ModelLoadRequest `{modelID}`; an empty id unloads. Error 153 (2-second model cooldown) is retried once after
    2.1 s. Next/previous/random pick from AvailableModelsRequest sorted by name.
  - MoveModelRequest `{timeInSeconds 0-2, valuesAreRelativeToModel, positionX?, positionY?, rotation?, size?}`.
    Omitted values stay unchanged (documented).
  - ExpressionActivationRequest `{expressionFile, fadeTime, active}`. Toggle reads ExpressionStateRequest first.
  - InjectParameterDataRequest `{faceFound, mode:"set"|"add", parameterValues:[{id, value, weight?}]}`. Values
    ±1,000,000 and weights 0-1 are checked first. VTube Studio drops injected values after about 1 s, so "hold N
    seconds" re-sends every 500 ms. In "set" mode the newest injection owns a parameter: an older hold stops
    re-sending it. "Set parameter (keep it)" keeps values in one background loop (500 ms) until stopped, expired or
    disconnected.
  - ParameterCreationRequest `{parameterName (4-32 alphanumeric), explanation (<256), min, max, defaultValue}`,
    ParameterDeletionRequest `{parameterName}`.
  - ColorTintRequest `{colorTint:{colorR,colorG,colorB,colorA,mixWithSceneLightingColor}, artMeshMatcher:{tintAll,
    artMeshNumber?, nameExact?, nameContains?, tagExact?, tagContains?, artMeshGroupIDExact?}}`. Group names are
    resolved to ids from ArtMeshListRequest. Reset tints everything white at 255. Rainbow sends a tint every 100 ms.
  - ArtMeshSelectionRequest `{textOverride (4-1024 chars or null), helpOverride, requestedArtMeshCount,
    activeArtMeshes}` answers only after OK/Cancel (`success`).
  - ItemLoadRequest with every documented field; `unloadWhenPluginDisconnects` defaults to true. Images use
    `customDataBase64` (PNG/JPG/GIF by magic bytes, at most 5 MB) and a generated `fileName`
    `cuebot<12 hex of SHA-256>.<ext>` (8-32 alphanumeric, as required); the `LoadCustomImagesAsItems` permission is
    requested once per session first.
  - ItemUnloadRequest `{unloadAllInScene, unloadAllLoadedByThisPlugin, allowUnloadingItemsLoadedByUserOrOtherPlugins,
    instanceIDs, fileNames}`. VTube Studio ignores unknown entries, so an entry that is neither a cached instance id
    nor a known file name goes in both lists (animation folders and Live2D items have file names without a dot).
  - ItemMoveRequest `{itemsToMove:[{itemInstanceID, timeInSeconds 0-30, fadeMode, positionX, positionY, size,
    rotation, order, setFlip, flip, userCanStop:true}]}` (up to 64). Values to keep are sent as -1000 and `order` as
    -1000, as documented. A file name moves every instance of it.
  - ItemPinRequest `{pin, itemInstanceID, angleRelativeTo, sizeRelativeTo, vertexPinType Provided|Center|Random,
    pinInfo{modelID, artMeshID, angle, size, vertexID1-3, vertexWeight1-3}}`; "where last clicked" uses the topmost
    `artMeshHits` entry of the last ModelClickedEvent.
  - ItemAnimationControlRequest (-1 keeps framerate/frame/brightness/opacity), ItemSortRequest
    (`setFrontOrder`/`setBackOrder` UseArtMeshID or UseSpecialID FullyInFront/FullyInBack, Unchanged when empty).
  - PostProcessingUpdateRequest for presets, on/off, values (`configID`/`configValue` strings, duplicates rejected)
    and `randomizeAll` with `randomizeAllChaosLevel`.
  - SetCurrentModelPhysicsRequest: base values (`setBaseValue:true`, id "", 0-100) or group multipliers (0-2),
    re-sent every 4 s with `overrideSeconds` 0.5-5 until the duration ends.
  - PermissionRequest `{requestedPermission}`; NDIConfigRequest (needs `ControlNDISettings`; reads first with
    `setNewConfig:false`, -1 keeps the resolution); StatisticsRequest, SceneColorOverlayInfoRequest, FaceFoundRequest.
  - Throwing: one ModelOutlineEvent (subscribed `{draw:false}` just long enough to get one, then unsubscribed) gives
    the head (60% from the hull center to its top; CurrentModel position as fallback). Each item loads off-screen
    (x ±1.35, `unloadWhenPluginDisconnects:true`), moves to the head (`easeIn`), the model flinches by
    InjectParameterData `mode:"add"` (FaceAngleX/Z, FacePositionX, EyeOpenLeft/Right, 350 ms), the item falls and is
    unloaded.
  - Timed changes ("Undo after"): one pending undo per target (expression file, model, position, tint, visual
    effects, item). A second timed change of the same target restarts the timer and keeps the first undo; a change
    without a timer cancels it; a lost connection drops them all.
  - Any other request can be sent through "Send raw request" or the `vtubestudio.request` script function.
- Bot events (`vtubestudio.*`): modelLoaded, modelUnloaded (`ModelLoadedEvent` with `modelLoaded:false`),
  hotkeyTriggered, trackingStatusChanged, trackingLost and trackingFound (derived from changes in `faceFound`),
  handLost and handFound (`hand` Left/Right, from changes after the first status), backgroundChanged,
  modelConfigChanged, modelMoved (the UI is refreshed at most every 250 ms), modelAnimation, itemEvent
  (`itemEventType` is Added, Removed, DroppedPinned, DroppedUnpinned, Clicked, Locked or Unlocked; the spec's example
  sends `ItemClicked`, so an `Item` prefix is dropped), modelClicked (plus `artMeshId`, the topmost hit, and
  `artMeshIds`), expressionToggled (plus `justLoaded`, `isLive2DItem`, `itemInstanceId`), postProcessingChanged,
  cubismEditorConnection, and `event` (raw: `eventName` plus `data` JSON). ModelOutlineEvent is never republished.

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

---

## 5. StreamLoots: alerts event stream (SSE)

Sources: StreamLoots help center ("Is There a Developer API in Streamloots?": there is no public API); docs.streamer.bot
(guide/integrations/streamloots: paste the Alerts Widget URL; trigger names only). Open-source clients were used for
the wire format only, no code copied: Firebot (`src/backend/integrations/builtin/streamloots`), Mix It Up
(`MixItUp.Base/Services/External/StreamlootsService.cs`), Entrivax/Streamloots-Plugin. Checked live 2026-10-27.

### Options considered

| Option | Real-time | Official | User provides | Verdict |
| --- | --- | --- | --- | --- |
| Public developer API / OAuth app | — | does not exist (help center) | — | not available |
| Webhooks (through the Cuebot relay) | — | StreamLoots offers none | — | not available |
| StreamLoots chatbot messages | chat only | yes | chat parsing | lossy: text only, and card alerts are limited to epic and legendary by default |
| **Alerts widget event stream** `widgets.streamloots.com/alerts/<id>/media-stream` | yes (SSE push) | the stream StreamLoots' own alert overlay uses | Alerts widget URL | **chosen**: the only structured real-time source; same input as Streamer.bot |

Compared with Streamer.bot's documented approach, Cuebot uses the same source but adds three things:

- It watches the 30 s keep-alive comments and reconnects after 95 s of silence. Without this, a half-open stream looks
  connected forever.
- It maps community gifts.
- It publishes every other alert type (for example reactions) as "Other alert" instead of dropping it.

### Wire format

- Request: `GET https://widgets.streamloots.com/alerts/<alert id>/media-stream` with `Accept: text/event-stream`.
- The answer is `200 text/event-stream` for any id. Observed: an all-zero GUID opens a stream too. So a typo cannot be
  detected; the Test button on the StreamLoots dashboard sends a real alert through the stream to check it. HTTP
  400/401/403/404 is treated as a rejected id (fatal).
- Keep-alive: a comment line `:` every 30 s (observed). No `id:` lines are sent, so nothing is replayed on reconnect.
- Each `message` event's data is one alert:
  `{ imageUrl, soundUrl, videoUrl?, message (alert text), data: { type, fields: [{ name, value }], cardName?, cardId?,
  cardSetId?, cardRarity?, description? } }`. Field values may be numbers or strings (`quantity`).
- `data.type` maps as follows:
  - `purchase` (fields `username`, `quantity`, optional `giftee`) → `streamloots.packPurchased`, or
    `streamloots.packGifted` when `giftee` is set.
  - `random-community-gift` (fields `username`, `quantity`) → `streamloots.packGifted` with `communityGift = true`.
  - `redemption` (`cardName`; fields `username`, `message` / `longMessage`, `rarity`) → `streamloots.cardRedeemed`.
  - Anything else → `streamloots.alert` (alertType, fields). Streamer.bot lists a "Reaction" event whose shape is not
    public; it arrives here.
- Args on every event: user, userName, message, alertMessage, alertType, imageUrl, soundUrl, videoUrl, fields (JSON of
  name → value), isTest.
  - Purchases and gifts add quantity, giftee, recipient, isGift, communityGift.
  - Redemptions add cardName, cardId, cardSetId, cardDescription, cardRarity (lower case), longMessage, hasVideo.
  - Streamer.bot's trigger pages do not document their variables, so these names follow Cuebot's conventions.
- Activity feed:
  - Redemptions are `Redemption` ("Dave redeemed Hydrate"), so they reach the credits' redemptions list.
  - Purchases and gifts are `Other`, with "3 chests" as the amount; the stream carries no money amount.
- Test alerts: purchase, gift, communityGift, redemption.

## 6. Crowd Control: PubSub websocket

Sources:

- developer.crowdcontrol.live/sockets: the PubSub overview, `reference/pub.html`, `reference/prv.html` and
  `reference/schemas.html`.
- The public OpenAPI description at openapi-docs.crowdcontrol.live (`openapi.json`).
- Crowd Control's own overlay client at overlay.crowdcontrol.live, for the wire format only (the topics and the `key`
  field).
- Open-source clients, for the wire format only: WarpWorld/CrowdControlWebSocketExample, qixils/countdown
  (`crowdcontrol-bar`), jayo-exe/CrowdControlVNyanPlugin, Mix It Up (`CrowdControlService.cs`).
- docs.streamer.bot: the Overlay URL setup, and trigger names only.

The refused-key reply below was captured live on 2026-10-27.

### Options considered

| Option | Real-time | Official | User provides | Verdict |
| --- | --- | --- | --- | --- |
| Application login (`generate-auth-code` + PKCE, scoped JWT with `session:control`, `effect-request:write`) | yes | yes, documented | browser sign-in | not available: needs an application id that Crowd Control's developers issue by hand, and Cuebot has none yet. It would add effect responses, session start/stop/pause and requesting effects (Max sub) |
| Legacy browser login (`whoami` connection id → auth.crowdcontrol.live) | yes | undocumented | browser sign-in | not used: 24 h JWT with unclear scopes (Cuebot does accept a pasted JWT) |
| Public topic `pub/<ccUID>` (ccUID from `GET /user/{platform}/{id}/id`) | yes | yes | nothing | too little data: no prices and no coin purchases (only `prv` carries them) |
| **Overlay key** (`subscribe` with `key`, topics `pub/self`, `prv/self`, `overlay/self`) | yes | what Crowd Control's own overlay does | Overlay URL | **chosen**: full private data, long-lived key, no sign-in; same input as Streamer.bot |
| Webhooks | — | none offered | — | not available |

Compared with Streamer.bot's documented approach, Cuebot uses the same input and adds:

- Refunds, retries and session pause as extra triggers.
- A keep-alive that follows the documented rule: ping, and reconnect when the pong is missing.
- A refused key stops the connection instead of retrying.
- A "Get session" sub-action that includes the running timed effects.
- A pasted JWT as an alternative credential.

Control features (answering effects, starting or pausing sessions, requesting effects) need the application login
above. Cuebot does not offer them until Crowd Control issues it an application id.

### Wire format

- Endpoint: `wss://pubsub.crowdcontrol.live/`. A `User-Agent` is required; Cuebot sends `Cuebot`.
- Overlay URL: `https://overlay.crowdcontrol.live/<page>?key=<overlay key>`.
  - Cuebot takes `key` from the query, or from a `#…?key=` fragment, and also accepts a bare key.
  - A value with three dot-separated parts is treated as a JWT and sent as `token` instead of `key`, as the overlay
    client does.
- Subscribe request: `{"action":"subscribe","data":{"topics":["pub/self","prv/self","overlay/self"],"key":"<key>"}}`.
  - Reply: `{"domain":"direct","type":"subscription-result","payload":{"success":[...],"failure":[...]}}`.
  - Captured for a wrong key: `success: []`, `failure: ["pub/self","prv/self","overlay/self"]`.
  - Cuebot counts as connected only when `prv/self` succeeded. Otherwise the key is refused (fatal, no retry).
  - No reply within 10 s → reconnect.
- Keep-alive: `{"action":"ping"}` is answered with the text `pong`.
  - The docs say to ping after 9 minutes of inactivity and to reconnect if no pong arrives within 15 s.
  - Cuebot pings every 8 minutes and reconnects when nothing arrives within 10 s of a ping.
  - The service ends sockets after 2 h; Cuebot reconnects and subscribes again.
- Messages have the shape `{ domain, type, payload, timestamp }`.
  - Effect and coin events are read from `prv` only. It adds `price`, `unitPrice` and `payments`, and the `pub` copies
    would duplicate them.
  - Exact repeats (same event, id and timestamp) are dropped.
- Event mapping:
  - `prv/effect-request`, `effect-success`, `effect-failure`, `effect-refund`, `effect-retry` →
    `crowdcontrol.effectRequest`, `effectSuccess`, `effectFailure` (+ `temporary`), `effectRefund`, `effectRetry`.
  - `prv/timed-effect-update` with `status` begin / end / pause / resume → `timedEffectStarted`, `timedEffectEnded`,
    `timedEffectUpdated` (+ timedStatus, timeRemaining in ms, timeRemainingSeconds).
  - `prv/coin-exchange` → `crowdcontrol.coinExchange` (transactionId, coinType paid/free, amount, bonus, source such
    as stripe or twitch-bits, user…, recipientName).
  - `pub/game-session-start`, `game-session-stop`, `game-session-pause` → `gameSessionStart`, `gameSessionEnd`,
    `gameSessionPaused` (+ paused, pausedUntil).
- Effect args:
  - Effect: requestId, effectId, effectName (names may arrive as `{ public, sort }`), effectDescription, effectType,
    effectImage, effectDuration (as sent), effectCategory.
  - Game: gameId, gameName, gamePackId, gamePackName, gamePackPlatform.
  - Viewer: user, userName, userId (platform id), userPlatform, userCcUid, userImage, anonymous, targetName,
    targetPlatform.
  - Request: quantity, price and unitPrice (coins), message, parameters ("Title: value, …"), sourceType, pooled,
    admin, timestamp, isTest.
  - Streamer.bot's trigger pages do not document their variables, so these names follow Cuebot's conventions.
- Activity feed:
  - `effectSuccess` → `Redemption` ("Alice redeemed Spawn Creeper", amount "150 coins"), so it reaches the credits'
    redemptions list.
  - Paid coin exchanges and session start/end → `Other` (coins are not money).
- Sub-actions:
  - "Get session" sets ccSessionActive, ccGameName, ccTimedEffects and related args.
  - "Send test event" publishes a local sample.
- The session state comes from the start/stop events seen since connecting. The public "active session" lookup needs a
  username or a scoped token.

---

## 7. Now playing: the computer's music players (`NowPlaying/`)

Settings: `integrations.json` → `NowPlaying` (nothing secret). `NowPlayingService` picks the song from the chosen source
(`Automatic`: a playing system player first, the system's own "current" session first; then Spotify if it plays; then a
paused one. `Player`: sessions whose name or id contains the text. `Spotify`: the account only) and publishes
`nowplaying.songChanged` once per new song (title + artist + album), 750 ms after it settled, because players often
send a file name first and the tags a moment later. Pausing and resuming the same song announces nothing. The optional
text file is written atomically (temp file + move) whenever its text changes. "Watch what plays" (`Enabled`, default
off) keeps the sources running; the "Get now playing" and "Media control" steps start them on demand.

### Linux: MPRIS over the session bus (`NowPlaying/Mpris/`)

- Source: MPRIS D-Bus Interface Specification 2.2, https://specifications.freedesktop.org/mpris-spec/latest/. Verified
  live on 2026-10-31 with mpv 0.41 + mpv-mpris 1.3 and VLC 3.0.24 on a private `dbus-run-session` bus (song, play/pause,
  Next/Previous, players starting and quitting).
- Transport: Cuebot's own session-bus client (`Cuebot.Core/Desktop/DBus`, SASL EXTERNAL, little-endian, no fd passing);
  no D-Bus package.
- Start: `AddMatch` for `NameOwnerChanged` (`arg0namespace='org.mpris.MediaPlayer2'`) and for `PropertiesChanged` on
  path `/org/mpris/MediaPlayer2`; `ListNames` → every `org.mpris.MediaPlayer2.*` name; per player `GetNameOwner`,
  `Properties.GetAll("org.mpris.MediaPlayer2.Player")` and `Properties.Get("org.mpris.MediaPlayer2", "Identity")`
  (the friendly name; else the bus name without `.instanceNNN`).
- Metadata (`a{sv}`): `xesam:title` (s), `xesam:artist` (as, joined with ", "; falls back to `xesam:albumArtist`),
  `xesam:album` (s), `mpris:artUrl` (s, often `file://` for local players), `mpris:length` (x microseconds; t/i/u/d
  accepted too). No title → the file name from `xesam:url`. `PlaybackStatus`: Playing / Paused / Stopped.
- Signals: `PropertiesChanged(s iface, a{sv} changed, as invalidated)` is matched to players by the sender's unique
  name; an invalidated `Metadata`/`PlaybackStatus` is read again with `GetAll`. `NameOwnerChanged(name, old, new)`: empty
  `new` = the player quit. Signals are queued off the reader thread before any call is made.
- Keys: `org.mpris.MediaPlayer2.Player.PlayPause` / `Play` / `Pause` / `Next` / `Previous`. Calls time out after 3 s;
  a lost bus reconnects every 10 s.

### Windows: System Media Transport Controls (`NowPlaying/Smtc/`)

- API: `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (Windows 10 1809+), the sessions behind
  the volume flyout. Cuebot is one `net10.0` build without the Windows SDK projection, so a Windows PowerShell 5.1 helper
  (`smtc-helper.ps1`, embedded, started as `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass
  -EncodedCommand …`) uses the WinRT types through `System.Runtime.WindowsRuntime` (`AsTask` on `IAsyncOperation`).
- Helper output (stdout, UTF-8, one JSON object per line): `{"type":"sessions","current":"<AUMID>","sessions":[{"id",
  "status","title","artist","album","albumArtist","durationMs"}]}` when the list changed (checked once a second);
  `{"type":"error","message"}`; `{"type":"unsupported","message"}` (no WinRT type / API → Cuebot stops for good).
  `status` is `GlobalSystemMediaTransportControlsSessionPlaybackStatus`: 4 Playing, 5 Paused, else stopped.
- Helper input (stdin): `playpause|play|pause|next|previous<TAB><AUMID>` → `TryTogglePlayPauseAsync` /
  `TryPlayAsync` / `TryPauseAsync` / `TrySkipNextAsync` / `TrySkipPreviousAsync`.
- Lifetime: restarted when it exits (2 s, doubling to 1 min; reset after a run longer than a minute); killed (whole
  tree) when Cuebot stops; it also exits by itself when stdin closes, so it never outlives Cuebot.
- Thumbnails are stream references, not links, so `artUrl` is empty for Windows players.
- Not yet run on a real Windows machine; the parsing and the process handling are covered by tests with a fake helper.

### macOS

Not supported for the computer's players (status text says so). Spotify works.

---

## 8. Spotify Web API (`Spotify/`)

- Sources: https://developer.spotify.com/documentation/web-api (Authorization Code with PKCE tutorial, Redirect URIs,
  Player and Search references, rate limits). Not tried against the real service (it needs the streamer's account);
  covered by fake-HTTP tests.
- Each streamer uses their **own** Spotify app (developer.spotify.com → Create app → Web API). Since 2026, apps in
  development mode allow 5 users and their owner needs Premium, so one shared Cuebot client ID cannot scale. Settings:
  `integrations.json` → `Spotify`: `ClientId` (not secret), `RedirectPort` (default 43117), `[Secret]` `AccessToken` and
  `RefreshToken` (secret store, never logged), `AccessTokenExpires`, `TokenClientId`, `PollSeconds` (5).
- Sign-in: `GET https://accounts.spotify.com/authorize?client_id&response_type=code&redirect_uri&code_challenge_method=S256&code_challenge&state&scope`
  with scopes `user-read-currently-playing user-read-playback-state user-modify-playback-state`. The redirect must be
  a loopback IP literal (`http://127.0.0.1:PORT/callback`; "localhost" is rejected by Spotify) and must match the app
  exactly, so the port is fixed (Core `LoopbackReceiver(service, port, "/callback")`). PKCE from `Core.Auth.Pkce`.
  Waits 5 minutes.
- Tokens: `POST https://accounts.spotify.com/api/token` form `grant_type=authorization_code&code&redirect_uri&client_id&code_verifier`;
  refresh with `grant_type=refresh_token&refresh_token&client_id` (no secret). Refreshed 60 s before expiry and once on a
  401. A new `refresh_token` replaces the old one; none → keep the old one. `invalid_grant` on refresh (Spotify now ends
  refresh tokens after about six months, or the user removed the app) → tokens cleared, status Error "sign in again".
- Endpoints (base `https://api.spotify.com/v1/`):
  - `GET me/player/currently-playing?additional_types=episode` every `PollSeconds` (2–60) while signed in; 204 or
    `item:null` (ads) = nothing. Track: `name`, `artists[].name`, `album.name`, `album.images[0].url`, `duration_ms`,
    `external_urls.spotify`, `uri`; episodes use `show.name` as the artist. `is_playing`.
  - `GET search?type=track&limit=1&q=…` (new apps get at most 10 results per page; Cuebot asks for 1).
  - `GET tracks/{id}` for links (`spotify:track:ID`, `open.spotify.com/[intl-xx/][embed/]track/ID`). A 403/400 (e.g.
    development-mode limits) still queues the bare URI; 404 → "no song with that link". Album/playlist/`spotify.link`
    links are refused with a plain message.
  - `POST me/player/queue?uri=…`, `POST me/player/next`, `POST me/player/previous`, `PUT me/player/pause`,
    `PUT me/player/play`, `PUT me/player/volume?volume_percent=0..100`, `GET me/player` (for toggle). Bodies are empty
    with `Content-Length: 0`. A player command wakes the poll early.
- Errors (`SpotifyErrors`): 404 / `NO_ACTIVE_DEVICE` → "Open Spotify on a device first…"; 403 `PREMIUM_REQUIRED` →
  "Spotify needs Premium to change songs"; 403 "user may not be registered" → add the account under User Management;
  403 `VOLUME_CONTROL_DISALLOW`; 429 → `Retry-After` honoured (the poll waits at least that long; steps say "try again in
  N seconds"); 5xx → "Spotify is having trouble".
- "Add song to Spotify queue" never fails the action: it runs the **Added** or **Not added** branch and sets
  `spotify.added`, `spotify.title`, `spotify.artist`, `spotify.album`, `spotify.uri`, `spotify.link`, `spotify.error`.
