# Local API servers and Streamer.bot / Speaker.bot compatibility

Module: `src/Cuebot.Server` (servers) and `src/Cuebot.Scripting/CSharp/Compat` (CPH layer, event-name map, code
triggers). Settings: `servers.json`. UI: Servers page (`ViewModels/Servers`, `Views/Servers`).

Hosting: Kestrel from the shared ASP.NET Core framework (`FrameworkReference Microsoft.AspNetCore.App`, no NuGet
package). Unlike `HttpListener` (http.sys on Windows) it needs no admin rights or URL ACLs for LAN binding, behaves the
same on Windows and Linux, and has WebSockets built in. An empty host is used: no config files, env vars or logging.

## Security defaults

- Every server is **off** until enabled on the Servers page, except the MCP endpoint (`/mcp` on the API listener,
  127.0.0.1:7475), which is on by default for the Assistant page and AI apps: it needs the API token, and destructive
  tools ask for confirmation; and the overlay server (127.0.0.1:7476), which OBS browser sources need: each overlay
  has its own token. Turn either off on the Servers page.
- Every server binds **127.0.0.1**. A non-loopback address (0.0.0.0, a LAN IP) is used only when "Allow LAN
  connections" (`allowLan`) is on; otherwise it is downgraded to 127.0.0.1 with a log warning.
- **Origin check** on every WebSocket upgrade and every HTTP API call: no Origin (apps, plugins, curl), `null`
  (file:// pages, OBS local-file browser sources), loopback pages and pages served by Cuebot on the same IP are
  allowed; other web origins only when listed in `allowedOrigins` (or `*`). Plain cross-site GETs (`Sec-Fetch-Site:
  cross-site`) are refused too, and the HTTP `DoAction`/`ExecuteCodeTrigger` need POST. Names are never trusted
  for the same-host rule (DNS rebinding).
- Optional WebSocket password (Streamer.bot handshake). Enforced for every request by default; when not enforced,
  privileged requests (DoAction, SendMessage, ExecuteCodeTrigger, ClearCredits, ClearFirstWordsCache) still need it.
  The password is never logged; comparison is constant-time.
- 1 MB limit per WebSocket message and HTTP body. Static files never leave their mapped folder.
- CPH never exposes platform tokens (`TwitchOAuthToken`, `TwitchClientId` throw).

## Streamer.bot WebSocket server (default 127.0.0.1:8080, endpoint `/`)

Hello on connect: `{timeStamp, session, request: "Hello", info: {instanceId, name: "Cuebot", os, osVersion, version,
mode, darkMode, source: "websocketServer"}, authentication?: {salt, challenge}}`. `Authenticate {authentication}` with
`base64(sha256(base64(sha256(password + salt)) + challenge))`. Responses `{status: "ok"|"error", id, ...}`.

| Request | Response / behaviour |
| --- | --- |
| Authenticate | ok / error "Authentication failed" |
| GetInfo | `info` |
| GetEvents | `events: {Source: [Type...]}`: every name the loaded modules' triggers can emit, plus Raw/Application/General.Custom/Custom.Event/Misc.StreamerbotExiting (Subscribe accepts any names) |
| Subscribe / UnSubscribe | `events: {Source: [Type...]}` (current subscriptions; `"*"` = all types) |
| GetActions | `actions: [{id, name, group, enabled, subaction_count, trigger_count}], count` |
| DoAction | `{action: {id \| name}, args}` → queued (not awaited); error if missing/disabled |
| GetBroadcaster | `platforms: {twitch: {broadcastUser, broadcastUserName, broadcastUserId, broadcastIsAffiliate, broadcastIsPartner (and broadcasterIs*), botUser, botUserName, botUserId}, youtube: {broadcastUserName, broadcastUserId, broadcastUserProfileImage, bot*}, kick: {broadcasterLogin, broadcasterUserName, broadcasterUserId, broadcasterProfileUrl, bot*}}` (from the modules' `*.getBroadcaster`/`getBot` functions; each also has Cuebot's `name`, `state`, `status`), `connected`/`disconnected: ["twitch", ...]`, `cuebotConnections: {obs: {name, state, status}...}` |
| GetActiveViewers | `viewers: [{id, type, login, display, platform, role, subscribed, previousActive, ...}], count` |
| GetCodeTriggers | `triggers: [{name, eventName, category}], count` |
| ExecuteCodeTrigger | `{triggerName, args}` → fires the "Custom code event" trigger |
| GetCommands | `commands: [{id, name, enabled, group, mode, location, commands, caseSensitive, ignoreInternal, ignoreBotAccount, globalCooldown, userCooldown, sources}], count` |
| GetGlobals / GetGlobal | `{persisted}` → `variables: {name: {name, value, lastWrite}}, count` / `{variable}` → `variable` plus `variables: {name: ...}, count: 1` (the reference documents `variable`; the official client reads `variables[name]`) |
| TwitchGetUserGlobals / TwitchGetUserGlobal (+ YouTube*, Kick*, Trovo* (always empty), GetUserGlobals/GetUserGlobal with `platform`) | `variables: [{userId, userLogin, userName, platform, name, value, lastWrite}], count`; *GetUserGlobal with `variable` → `variable: {name, value, lastWrite}` plus `variables: [that one], count: 1` (error when missing) |
| GetCredits / TestCredits / ClearCredits | `events: {follows, cheers, subs, resubs, giftsubs, giftbombs, raided, rewardredemptions, ...}, users: {moderators, vips, subscribers, users, ...}, hypeTrain, top, groups, custom` (user names), plus `credits: {events: {cuebotSection: [...]}, cuebot: snapshot}` |
| SendMessage | `{message, platform, bot?, internal?, replyId?, broadcastId?}` → combined chat send; `bot: true` the bot account, `false` the broadcaster (absent: the platform's setting); `internal: false` leaves the echo unflagged so commands react to it (Twitch, Kick); `replyId` Twitch/Kick, `broadcastId` YouTube; privileged |
| TwitchGetEmotes / YouTubeGetEmotes | `emotes: {userEmotes: [{name, type, imageUrl}], bttvEmotes, ffzEmotes, sevenTvEmotes: [{name, type: "BTTVGlobal"/"7TVChannel"/..., imageUrl, zeroWidth}]}`: native emotes Cuebot has seen in chat and the loaded 7TV/BTTV/FFZ sets |
| GetMonitoredYouTubeBroadcasts | `broadcasts: [{id, title, description, privacy, status, liveChatId, channelId, actualStartTime, actualEndTime, scheduledStartTime, publishedAt, ...}], count` from the YouTube module (fields Cuebot does not have are empty) |
| GetUserPronouns | `{platform, userLogin}` → `userLogin, pronoun: {pronounSubject, pronounObject, pronounPossessive, pronounPronoun, pronounReflexive, pronounPastTense, pronounCurrentTense, pronounPluralLower, ...Lower, pronouns: "(She/Her)", pronounsPlain, userFound}` (alejo.io, Twitch only; looked up and cached) |
| ClearFirstWordsCache | resets first-words tracking |

Events: `{timeStamp, event: {source, type}, data}`. `data` holds the Cuebot event arguments plus `cuebotEvent` (the
original type), with Streamer.bot's documented nested fields on top (`StreamerBotPayloads`, from the official client's
event types and the wiki's event pages): Twitch `ChatMessage` has the `message` object (`data.message.message`,
`msgId`, `username`, `role`, `emotes`, `badges`, ...), `RewardRedemption` has `reward: {id, title, cost, prompt}`
and `user_login`/`user_input`, `Follow` has `user_id`/`user_login`/`user_name`/`followed_at`, subs and gifts have
`subTier`, `recipientUsername`, `gifts`, ...; YouTube events carry the YouTube user object; and every event with a
user gets `user: {id, login, name, display, role, subscribed, type, color, badges}`. Where a documented field has a
flat arg's name (Twitch chat's `message`), the documented one wins and the text is in `text`. Also sent:
`Application.ActionAdded/Deleted/Updated` (`{id, name, group, enabled}`), `Application.CommandAdded/Deleted/Updated`,
`Raw.Action` and `Raw.SubAction` (only while a client subscribes to them; `{id: run, actionId, name, arguments}`,
sub-actions also `parentId`, `parentName`, `type`), `Raw.ActionCompleted` (`id`, `actionId`, `name`, `duration`,
`queuedAt`, `startedAt`, `completedAt`, `success`, `excludeFromHistory`, `arguments`, plus Cuebot's `actionName`,
`runId`, `state`, `error`), `Misc.StreamerbotExiting` on shutdown, and `General.Custom` for CPH
`WebsocketBroadcastJson/String` and the "WebSocket server: broadcast" sub-action. Custom events
(`CPH.TriggerEvent`/`TriggerCodeEvent`, "Trigger custom event", ExecuteCodeTrigger) go out as `Custom.CodeEvent` (flat
args) and as `Custom.Event {eventName, args}`, which the official client's `customEventResponse` waits for.

Payload shapes follow the official client's types for every Twitch event it types (43, incl. rewards, polls with
`channel_points_voting`, predictions with `top_predictors`, charity, chat cleared, untimeouts) and the wiki's pages for
YouTube, commands, timers, heart rate, StreamElements, Streamlabs, DonorDrive (`donation`/`profile` objects), file
watchers (`fullPath`, `fileName`, `name`; `Changed` adds the file's `lines`, or `data` for a JSON object, up to 256 KB),
OBS (`obs.name`, `obs.host`, `obs.studioVersion`, ...) and Shopify (the order object). Twitch chat `emotes` include
7TV/BTTV/FFZ matches (`type` "7TV"/"BTTV"/"FFZ", code-point positions, `bits`, `color`), `cheerEmotes` the cheermotes
when the message's `Cheer100`-style words add up to the bits, and Twitch user objects carry `pronouns` (the GetUserPronouns
`pronoun` object) for users whose pronouns Cuebot already looked up; building a payload never starts a lookup. Fields
Cuebot has no data for are sent empty (badge `imageUrl`, hype train `last_contribution`).

**Testing with the official client.** `StreamerBotClientNodeTests` runs `tests/Cuebot.Server.Tests/NodeClient/streamerbot-client-check.mjs`
with node against a server started in the test: `@streamerbot/client` connects with a password, subscribes with
`client.on()`, receives every documented event and calls every request method; responses and Twitch payloads are
checked against the client's `index.d.ts` with the TypeScript compiler API. Prepare a folder outside the repo once
(`npm install @streamerbot/client typescript`) and set `CUEBOT_TEST_SBCLIENT` to it (and `CUEBOT_TEST_NODE` when node
is not on PATH); without them the test is skipped. `StreamerBotDocumentedShapeTests` checks the same shapes without
node (`Fixtures/streamerbot-documented-shapes.json`).

### Event mapping (`StreamerBotEventMap`)

Names follow Streamer.bot's published event list (GetEvents output in the official client, `EventType` enum), with its
spellings. `Explicit` holds the renames, `Resolvers` the argument-dependent names; anything else maps generically
(`module.camelCase` → `Source.PascalCase`, the source spelled as in `Sources`), so new module events are forwarded
without changes. `StreamerBotEventNames` lists the documented names; tests check every name under a Streamer.bot source
against it (Cuebot additions are listed in `StreamerBotEventCatalogTests.CuebotExtras`).

| Cuebot event | Streamer.bot |
| --- | --- |
| twitch.chatMessage / follow / sub / giftBomb / cheer / raid / rewardRedemption / streamOnline / ... | Twitch.ChatMessage / Follow / Sub / GiftBomb / Cheer / Raid / RewardRedemption / StreamOnline / ... (generic) |
| twitch.resub, twitch.subGift (per recipient), twitch.giftSub (EventSub aggregate) | Twitch.ReSub, Twitch.GiftSub, Twitch.GiftSubscriptions (Cuebot addition) |
| twitch.emoteModeOn/Off, followerMode*, slowMode*, subscriberMode*, uniqueMode* | Twitch.ChatEmoteModeOn/Off, ChatFollowerMode*, ChatSlowMode*, ChatSubscriberMode*, ChatUniqueMode* |
| twitch.sharedChatBegin/Update/End, sharedChatGiftBomb, sharedChatModiversary, sharedChatUserTimedOut/UntimedOut | Twitch.SharedChatSessionBegin/Update/End, SharedChatCommunitySubGift, SharedModiversary, SharedChatUserTimedout/Untimedout |
| twitch.userWarned, pyramid, automodMessageHeld/Updated, broadcasterConnected/Disconnected, botConnected/Disconnected | Twitch.WarnedUser, PyramidSuccess, AutoModMessageHeld/Update, BroadcasterEventSubConnected/Disconnected, BotEventSubConnected/Disconnected |
| youtube.chatMessage, memberMilestone, userTimedOut, broadcastStatisticsUpdated, sponsorOnlyStarted/Ended | YouTube.Message, MemberMileStone, UserTimedout, StatisticsUpdated, NewSponsorOnlyStarted/Ended |
| kick.chatConnected/Disconnected, sevenTvEmoteAdded/Removed (other kick.* generic) | Kick.BroadcasterChatConnected/Disconnected, SevenTVEmoteAdded/Removed |
| obs.streamStarted / streamStopped / recordStarted / recordStopped | Obs.StreamingStarted / StreamingStopped / RecordingStarted / RecordingStopped |
| slobs.* | StreamlabsDesktop.* (StreamingStarted/Stopped, RecordingStarted/Stopped) |
| meldstudio.*, meldstudio.event | MeldStudio.*; the event's kind picks LayerVisbilityChanged, LEffectEnabledStateChanged, TrackMonitoringStateChanged, TrackMustedStateChanged, else MeldStudio.Event |
| command.triggered / cooldown; groups.*; quotes.added / shown | Command.Triggered / Cooldown; Group.*; Quote.Added / Show |
| variables.*, core.appStarted, core.processStarted / processStopped | Misc.GlobalVariable* / UserGlobalVariableUpdated, Misc.StreamerbotStarted, Misc.ProcessStarted / ProcessStopped |
| core.timer | Misc.TimedAction, or System.Cron for cron timers |
| core.file*, core.fileTailLine | FileWatcher.*, FileTail.Changed |
| users.firstWords, users.presentViewers | {platform}.FirstWords (Twitch: FirstWord), {platform}.PresentViewers |
| chat.emoteAdded / emoteRemoved | {Twitch, YouTube}.SevenTVEmote* / BetterTTVEmote*, Kick.SevenTVEmote*; FFZ: Chat.Emote* |
| desktop.clipboardChanged, desktop.toastActivated | System.ClipboardChanged, Misc.ToastActivation |
| hotkeys.pressed, input.mouseClick, controller.button / axis / hat | Inputs.InputKeyPress, InputMouseClick, InputControllerButton / Axis / Hat |
| remote.connected / disconnected / signal; webhook.custom | StreamerBotRemote.InstanceConnected / InstanceDisconnected / InstanceSignal; StreamerBot.CustomWebook |
| tits.*, pally.*, pulsoid.heartRate, hyperate.heartRate / clipCreated, streamlabs.merch, voicemod.voiceChanged | ThrowingSystem.*, Pallygg.*, Pulsoid.HeartRatePulse, HypeRate.HeartRatePulse / TwitchClipCreated, Streamlabs.Merchandise, VoiceMod.VoiceLoaded |
| wavelink.*, elgato.* | Elgato.WaveLinkV3*, Elgato.CameraHub* / ControlCenter* (ControlCenterDeviceOn/Off, DeviceBrightnessChanged, DeviceTemperatureChanged) |
| custom.codeEvent; websocket.client* / server* | Custom.CodeEvent; WebsocketClient.* / WebsocketCustomServer.* |
| speech.*, chat.message / activity, rumble.*, lumia.*, mtion.*, obsbot.*, polypop.*, points.*, controller.connected / disconnected | Cuebot sources (no Streamer.bot counterpart): Speech.*, Chat.*, Rumble.*, Lumia.*, Mtion.*, Obsbot.*, PolyPop.*, Points.*, Controller.* |

## Overlay server (default 127.0.0.1:7476)

Serves Cuebot overlays (`src/Cuebot.Server/Overlays`, stores in `src/Cuebot.Core/Overlays`; plan in
`docs/OVERLAYS_PLAN.md`). On by default from servers.json version 3; overlays are managed on the Overlays & Widgets
page.

| Route | |
| --- | --- |
| `GET /o/{id}?token=...` | The overlay page (transparent, the overlay's canvas size). A wrong or missing token answers 404, like an unknown id. |
| `GET /o/{id}/ws?token=...` (WebSocket) | `{"type":"hello","overlay":{id,name,width,height}}`, then every `OverlayFeed` message for that overlay: `{"type":"event","listener","event"}` (StreamElements-shaped), `{"type":"session","session"}`, `{"type":"command","command"}`. The page's own origin, loopback pages and `allowedOrigins` may connect. |
| `GET /runtime/overlay.js`, `/runtime/overlay.css` | The page runtime (`window.Cuebot`: `overlay`, `on(type, fn)`, `send(text)`; reconnects with backoff, `reload` command reloads). |
| `GET /assets/{sha256}.{ext}` | Files from `overlays/assets` (images, video, audio, fonts), `Cache-Control: immutable`, `Content-Security-Policy: sandbox`. |

- Tokens: `ov_` + 32 random bytes, one per overlay in `overlays.json`, compared in constant time; "New link" replaces it.
- Every response has `Referrer-Policy: no-referrer` (the page also sets it), so the token in the page URL never
  reaches sites that widgets load from, and `X-Content-Type-Options: nosniff`. Only GET/HEAD.
- Each page has its own outbox: past 500 waiting messages the oldest chat message or session update is dropped;
  alerts and commands are kept up to 5000.

## HTTP server (default 127.0.0.1:7474)

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/DoAction` (and `/DoAction`) | body `{action: {id \| name}, args}` → 204; 404 unknown action; GET → 405 |
| `GET /api/GetActions`, `GetInfo`, `GetEvents`, `GetBroadcaster`, `GetActiveViewers`, `GetCodeTriggers`, `GetCommands`, `GetGlobals`, `GetGlobal?variable=`, `*GetUserGlobals`, `GetCredits`, `TestCredits`, emotes, `GetMonitoredYouTubeBroadcasts`, `GetUserPronouns` | same JSON bodies as the WebSocket responses (query string = request fields) |
| `GET /api/GetCredits`, `TestCredits` | the documented HTTP shape (`Events.Follows`, `User.Moderator`, `TopBits.All`, ...) plus the WebSocket fields |
| `GET /api/ClearCredits`, `/api/ClearFirstWordsCache` | 204 |
| `POST /api/ExecuteCodeTrigger` | `{triggerName, args}` → 204 |
| `/feed/chat`, `/feed/events` | built-in OBS docks (combined chat, activity feed); connect to the WebSocket server; `?transparent=1`, `?password=`, `?port=` |
| mapped paths | static files from `http.mappings` (path → folder or file; folders serve index.html) |
| `/` | small index page |

CORS: allowed origins get `Access-Control-Allow-Origin` echoed and preflights answered. SendMessage is WebSocket-only.

## UDP server (default 127.0.0.1:4242)

`{"action": {"id" | "name"}, "args": {...}}`, optionally with `"request": "DoAction"`. Other requests are logged
and ignored; non-JSON datagrams are ignored.

## Speaker.bot APIs (WebSocket default 7580, UDP default 6669)

| WebSocket request | Mapping onto SpeechService |
| --- | --- |
| Speak `{voice, message, badWordFilter, silent?, wait? / delay?}` | `SpeakAsync` with alias; returns `success, duration (ms), speechFile` |
| Stop / Clear | skip current / clear queue |
| Enable, On / Disable, Off | `Enabled` |
| Pause / Resume | `Paused` |
| Events `{state: on/off/toggle}` | `EventsEnabled` |
| Mode `{mode: all/command}` | chat reading mode |
| SetEnabled `{state: enabled/disabled/toggle}`, SetPaused `{state: paused/enabled/toggle}` | as Streamer.bot's sub-actions |
| ActivateVoiceGate `{profile}` | VoiceGate profile → `success` |
| GetAliases, GetVoices, GetState, GetInfo | lists / state (Cuebot additions) |
| Subscribe `{events?: [...]}` / UnSubscribe | pushes `{timeStamp, event: {source: "SpeakerBot", type: SpeechStarted / SpeechFinished / StateChanged}, data}` |

| UDP command | Mapping |
| --- | --- |
| speak `{voice, message, badWordFilter?}` | queued speech |
| stop, enable/on, disable/off, pause, resume, clear | as above |
| events `{value, state}` | all events on/off (single events: logged as unsupported) |
| profile `{profile}` | voice ignore profile |
| reg `{mode: add/del, user, id}` | user "regular" flag |
| set `{method: nickname, username, nickname}` | nickname |
| set sticky, assign last | logged as unsupported |

## Custom WebSocket servers and clients

`servers.json` `customServers` (name, address, port, endpoint, autoStart) and `customClients` (name, url,
autoConnect, autoReconnect, reconnectSeconds). Addressed by index (Streamer.bot's `connection`), id or name.

- Triggers: Custom server: connection opened / message / closed; Client: connected / message / disconnected (filter
  by name). Args: serverName, serverIndex, serverId, sessionId, remote, message / clientName, clientIndex, clientId,
  url, message, reason.
- Sub-actions: Custom server: send message (all sessions or `sessionId`), Custom server: start / stop, Client: send
  message, Client: connect / disconnect, WebSocket server: broadcast (General.Custom).
- Script functions: `websocket.broadcast`, `websocket.client{Connect,Disconnect,IsConnected,Send,Index}`,
  `websocket.server{Start,Stop,IsListening,Broadcast,CloseSession,CloseAll,Index}`.

## C# CPH compatibility

Two shapes run in "Execute C# code":

1. Streamer.bot classes, unchanged: `public class CPHInline { public bool Execute() {...} }` with optional `Init()` /
   `Dispose()`. Compiled as a normal C# library (all framework assemblies referenced; `using Streamer.bot.Plugin.
   Interface[.Enums|.Model]` and `Streamer.bot.Common.Events` resolve); `CPHInline` is made to derive from
   `CPHInlineBase`, which provides `CPH` and `args` (per run). One instance per distinct code for the app's lifetime:
   fields persist, `Init()` runs once before the first run, `Dispose()` at shutdown. `Execute()` returning false stops
   the action.
2. Cuebot script bodies: `CPH` is a global next to `Cb`, `Args` (and its alias `args`).

| Group | Supported | Notes |
| --- | --- | --- |
| Arguments | TryGetArg&lt;T&gt;, TryGetArg(object), SetArgument, args, GetSource, GetEventType | args is the run's case-insensitive dictionary (changes are kept) |
| Logging / misc | LogInfo, LogWarn, LogError, LogDebug, LogVerbose, Wait, Between (inclusive), NextDouble, GetVersion | Wait honours cancel/timeout |
| Actions | ActionExists, GetActions, EnableAction(ById), DisableAction(ById), RunAction(ById)(name, runImmediately) | runImmediately: nested run, waits; false: queued |
| Queues | PauseActionQueue, ResumeActionQueue(name, clear), PauseAllActionQueues, ResumeAllActionQueues | |
| Globals | GetGlobalVar&lt;T&gt;, SetGlobalVar, UnsetGlobalVar, GetGlobalVarValues, ClearNonPersistedGlobals | objects stored as JSON text and deserialized on Get |
| User variables | Get/Set/Unset{Twitch,YouTube,Kick}UserVar(ById), UnsetAll{Platform}UsersVar, Get{Platform}UsersVar&lt;T&gt;, GetUserVar/SetUserVar/UnsetUserVar/UnsetUser (Twitch), UnsetAllUsersVar, ClearNonPersistedUserGlobals | unknown users keyed by lower-case name; persisted flag ignored on Get |
| Chat | SendMessage, SendYouTubeMessage, SendKickMessage, SendMessageToAll (Cuebot) | `bot`/`fallback` ignored (Cuebot sends with its connected account) |
| OBS (connection = index in Cuebot's OBS connection list, 0 the default) | ObsCreateRecordChapter, ObsIsFilterEnabled, ObsSetFilterState (0 visible, 1 hidden, 2 toggle), ObsToggleFilter, ObsSetRandomFilterState, ObsHideSourcesFilters, ObsHideScenesFilters, ObsIsConnected, ObsSetScene, ObsGetCurrentScene, ObsSetSourceVisibility, ObsShowSource, ObsHideSource, ObsIsSourceVisible, ObsToggleSource, ObsShowFilter/ObsHideFilter (2 overloads), ObsSetGdiText, ObsSetBrowserSource, ObsSetImageSourceFile, ObsSetMediaSourceFile, ObsStart/StopStreaming, ObsStart/StopRecording, ObsIsStreaming, ObsIsRecording, ObsSendRaw | via `obs.request` (obs-websocket v5) |
| Speech / sound | TtsSpeak, PlaySound (volume 0-1, wait, sound name tag), StopSoundPlayback, StopAllSoundPlayback | via `speech.speak` / audio sub-actions |
| Commands | EnableCommand, DisableCommand, GetCommands, CommandGetCounter, CommandResetCounter, CommandResetAllUserCounters, CommandGetUserCounter(ById) → `CommandCounter`, CommandResetUserCounter, CommandResetUsersCounters, CommandSetGlobalCooldownDuration, CommandSetUserCooldownDuration, CommandAddTo{Global,User,AllUser}Cooldown(s), CommandReset{Global,User,AllUser}Cooldown(s), CommandRemove{Global,User,AllUser}Cooldown(s) | as in Streamer.bot: Reset starts a cooldown again at its full length, Remove ends it, AddTo adds seconds. User counters are always persisted (`persisted` is ignored) |
| Groups | AddGroup, DeleteGroup, GetGroups, GroupExists, AddUserToGroup, AddUserIdToGroup, RemoveUserFromGroup, RemoveUserIdFromGroup, UserInGroup, UserIdInGroup, UsersInGroup, ClearUsersFromGroup (+ platform-less Twitch overloads) | |
| Timers | EnableTimer, DisableTimer, EnableTimerById, DisableTimerById, GetTimerState, SetTimerInterval | |
| Triggers | RegisterCustomTrigger, TriggerCodeEvent(name, useArgs), TriggerCodeEvent(name, args), TriggerEvent(name, useArgs) | "Custom code event" trigger; TriggerEvent is the same as TriggerCodeEvent (Cuebot has one custom event trigger) |
| Other code | ExecuteMethod(codeName, methodName) | runs a public parameterless method on another C# sub-action's CPHInline instance (same instance its Execute() uses) with this run's args; the code is found by the sub-action's "Code name", else as the first CPHInline code in the action named `codeName`; returns the method's bool (void → true), false when not found |
| Twitch chat | SendWhisper (bool), TwitchAnnounce, SendAction | whisper uses the bot when `bot` and it is signed in; announcements are sent by the broadcaster (colour `default` → primary); SendAction sends a /me line through the Twitch action announcer when one is registered, otherwise as a plain chat message |
| Twitch users | TwitchGetBroadcaster, TwitchGetBot, TwitchGetUserInfoByLogin/ById → `TwitchUserInfo`; TwitchGetExtendedUserInfoByLogin/ById → `TwitchUserInfoEx` | null when not found. Roles and LastActive come from Cuebot's user list (what chat showed); PreviousActive is not tracked. Ex: follow/subscription filled when the scopes allow; GameId and Tags stay empty; AccountAge is in seconds |
| Twitch moderation / stream | TwitchTimeoutUser, TwitchBanUser, TwitchUnbanUser, TwitchRunCommercial (bool, 30-180 s), SetChannelTitle (bool), SetChannelGame → `GameInfo`, CreateStreamMarker → `StreamMarker`, CreateClip() / CreateClip(title, duration) → `ClipData` | done as the broadcaster (`useBot` ignored); failures return false/null and log a warning. Clip title/duration are not sent (Twitch's API has no such fields); ClipData carries what Twitch returns on creation (id, url, edit/embed url, broadcaster) |
| YouTube | YouTubeGetLatestMonitoredBroadcast (alias YouTubeGetLatestBroadcast), YouTubeGetMonitoredBroadcasts → `YouTubeBroadcastInfo` | id, channel, live chat id, title, description, privacy, status, url; category, tags, languages and dates stay empty |
| Twitch rewards | TwitchGetRewards → `TwitchReward`, Enable/Disable/Pause/UnPauseReward, UpdateRewardMaxPerStream, UpdateRewardMaxPerUserPerStream, TwitchRedemptionFulfill/Cancel, TwitchGetRewardCounter, TwitchGetRewardUserCounter(ById), TwitchGetRewardUserCounters → `TwitchRewardCounter`, TwitchResetRewardCounter, TwitchResetRewardUserCounters, TwitchResetUserRewardCounter(s) | only rewards Cuebot's Twitch app created can change (`IsOurs`); counters are Cuebot's (persistence is set per reward on the Twitch page, `persisted` is ignored); LastRedeemed is not tracked |
| Twitch chat (more) | TwitchReplyToMessage, TwitchDeleteChatMessage, TwitchClearChatMessages, TwitchPinChatMessage, TwitchSendAndPinMessage, TwitchUnpinMessage, TwitchUpdatePinnedMessageDuration, TwitchGetPinnedMessage → `TwitchPinnedMessage` (a Cuebot class) | pins last 30-1800 s, 0 until the stream ends; send-and-pin sends as the broadcaster |
| Twitch moderation (more) | TwitchWarnUser, TwitchAddActiveMonitoringSuspiciousStatus, TwitchAddRestrictedSuspiciousStatus, TwitchRemoveSuspiciousStatus (by user id; `moderator:manage:suspicious_users`), TwitchAddBlockedTerm, TwitchRemoveBlockedTerm (by text), TwitchGetBlockedTerms → `List<string>`, TwitchApproveAutoHeldMessage, TwitchDenyAutoHeldMessage, TwitchGetMods, TwitchGetVips → `List<TwitchUserInfo>`, GetCheermotes → `List<Cheermote>` | |
| Twitch clips | GetAllClips, GetClips, GetClipsForGame, GetClipsForUser (name or int id), GetClipsForUserById, with count / start-end / TimeSpan / isFeatured overloads → `List<ClipData>` | most viewed first, at most 1000 (Twitch's limits) |
| YouTube (more) | YouTubeGetBroadcaster, YouTubeGetBot → `YouTubeUserInfo`; YouTubeSetTitle, SetDescription, SetCategory (name or id), SetPrivacy, SetMetaData, AddTags, RemoveTags, ClearTags, BanUserById/ByName, TimeoutUserById/ByName (bool); SendYouTubeMessageToLatestMonitored | a null `broadcastId` means the latest monitored broadcast |
| Kick | KickGetBroadcaster, KickGetBot → `KickUserInfo`; KickBanUser, KickTimeoutUser, KickUnbanUser, KickDeleteChatMessage, KickReplyToMessage, KickSetTitle (bool), KickSetCategory → `KickCategory` | null when not signed in; Kick times out in whole minutes (rounded up); KickCategory carries the name only |
| Meld Studio | MeldStudioConnect, Disconnect, IsConnected, GetConnectionByName, ShowScene, ShowSceneByName, ShowLayerByName, HideLayerByName | one connection: index -1 or 0 (other indexes return false) |
| Bulk user variables | IncrementAll{Twitch,YouTube,Kick}UsersVar, Increment{Twitch,YouTube}UsersVarById, IncrementAllKickUsersVarById, IncrementOrCreate{Twitch,YouTube,Kick}UsersVarById, SetKickUsersVarById | only variables with the given persistence change; "OrCreate" starts missing ones at 0 |
| Parse | Parse(text, args?) | `%arg%`, `~global~` and inline functions, with the given args or this run's |
| Keyboard | KeyboardPress (SendKeys syntax: `{W}`, `+{W}`, `^{W}`, `%{W}`) | uses Cuebot's keyboard output (SendInput on Windows; XDG RemoteDesktop portal or X11 XTest on Linux) |
| WebSocket | WebsocketBroadcastJson/String, WebsocketConnect/Disconnect/IsConnected/Send, WebsocketGetConnectionByName, WebsocketCustomServer{Start,Stop,IsListening,Broadcast,CloseSession,CloseAllSessions,GetConnectionByName} | needs the Servers module |
| Integrations | DiscordPostTextToWebhook | returns the new message's id |
| OBS (more) | ObsMediaPlay/Pause/Restart/Stop/Next/Previous, ObsSetMediaState (0 play ... 5 previous), ObsReplayBufferStart/Stop/Save, ObsSetReplayBufferState, ObsPause/ResumeRecording, ObsSourceMute/UnMute/MuteToggle, ObsSetSourceMuteState, ObsSetScene/SourceFilterState, ObsHideGroupsSources, ObsHideSceneSources, ObsSetRandomGroup/SceneSourceVisible, ObsGetGroupSources, ObsSetColorSourceColor (ARGB or hex), ObsSetColorSourceRandomColor, ObsConvertColorHex/Rgb, ObsTakeScreenshot, ObsGetSceneItemProperties (props JSON), ObsSendBatchRaw, ObsGetConnectionByName, ObsConnect | ObsConnect only reports the state (Cuebot keeps OBS connected); batches run in order (executionType ignored) |
| Streamlabs Desktop | Slobs{Set,Get Current}Scene, Start/Stop Streaming/Recording, IsStreaming/IsRecording, Show/Hide/SetSourceVisibility(State), IsSourceVisible, GetGroupSources, HideGroupsSources, SetRandomGroupSourceVisible, SetGdiText, SetBrowserSource, SourceMute/UnMute/MuteToggle, SetSourceMuteState, Show/Hide/Toggle/SetFilterState, SetRandomFilterState, IsFilterEnabled, IsConnected, Connect | one connection (0); the output (horizontal/vertical) argument is ignored |
| Quotes, points, Decks | AddQuoteFor{Twitch,YouTube,Kick}, GetQuote → `QuoteData`, GetQuoteCount, DeleteQuote; Get/Add/Update/Set/Spend/Remove/Reset/TransferPoints, UpdateBulkPoints, AwardPointsToAll/ToGroup, Get/Set/RemoveGroupMultiplier, GetUserMultiplier, GetLeaderboard → `PointsLeaderboardEntry`, GetPointDefinition(s) → `PointDefinition`, ResetPointDefinition; DeckItemSet{Background,Icon,State,Title,Value}, DeckItemToggleState, DeckNotify, DeckPageNext/Prev/Set | points take (definition, user name, platform): Streamer.bot publishes no signatures, so they follow its sub-action pages; a point definition is a Cuebot currency (null = default); Deck states count from 0, pages from 1 |
| Twitch channel (more) | TwitchPollCreate/Terminate/Archive, TwitchPredictionCreate/Lock/Cancel/Resolve, TwitchStartRaidByName/ById, TwitchCancelRaid, TwitchSendShoutoutByLogin/ById, TwitchAdd/Remove/Set/ClearChannelTag(s), TwitchAdd/RemoveModerator, TwitchAdd/RemoveVip, TwitchEmoteOnly, TwitchSubscriberOnly, TwitchFollowMode, TwitchSlowMode, TwitchShieldModeOn/Off, UpdateReward(Title/Prompt/Cost/BackgroundColor/Cooldown), TwitchRewardGroup{Enable,Disable,ToggleEnable,Pause,UnPause,TogglePause}, TwitchIsUserSubscribed, GetTeamInfoById/ByLogin, Guest Star (sessions, invites, slots, settings) | polls, predictions and Guest Star act on the channel's active one (ids are not needed); Bits voting is gone from Twitch; teams carry names only |
| Apps | StreamDeckSet{Title,Value,State,BackgroundColor,BackgroundLocal,BackgroundUrl}, StreamDeckToggleState, StreamDeckShowAlert/Ok; VTubeStudio{TriggerHotkey,LoadModel}ById/ByName, (De)ActivateExpression, GetModelPosition, MoveModel, ColorTint{All,ByNames,ByNameContains,ByTags,ByTagContains,ByNumber}, RandomColorTint, ResetAllColorTints, SendRawRequest; VoiceMod{SelectVoice,GetCurrentVoice,VoiceChangerOn/Off,HearMyVoiceOn/Off,BackgroundEffectOn/Off,CensorOn/Off,GetVoiceChangerStatus,GetHearMyselfStatus}; WaveLink input/microphone/output mute, volume, filter and bypass methods; LumiaSendCommand, LumiaSetToDefault; MidiSendNoteOn/ControlChange/Raw(ByName); ThrowingSystemThrowItemByName, ThrowingSystemActivateTriggerByName | Stream Deck keys by Button ID (a colour is sent as a one-colour image; the colour behind an image is ignored); Voicemod status is the last state Voicemod reported; MIDI channels 1-16 |
| Misc | ShowToastNotification (2 overloads), PlaySoundFromFolder, ResetCredits, ResetFirstWords, PronounLookup, PronounClearForUserlogin (clears the whole cache), SendYouTubeMessage(…, broadcastId), SendYouTubeMessageToAllMonitored, Set{Twitch,YouTube}UsersVarById, IncrementKickUsersVarById, Unset{Twitch,YouTube,Kick}User(ById), TriggerCodeEvent(name, json), UrlEncode, EscapeString, BroadcastUdp (to 127.0.0.1) | |
| **Unsupported** (throw `NotSupportedException: CPH.<Name> is not supported by Cuebot`) | TwitchClientId, TwitchOAuthToken | Cuebot never hands platform credentials to scripts |
| **Stubs** (log "CPH.<Name> is not supported in Cuebot" and return the failure value) | TwitchBlockUser, TwitchBlockUserById, TwitchUnblockUser, TwitchUnblockUserById, SetChannelGameById (false); TwitchGetChannelPointsUsedByUserId, TwitchGetBitsDonatedByUserId (0); every Trovo and VStream method (Send/Get/Set/Unset/Increment…, AddQuoteForTrovo -1); ObsDisconnect, SlobsDisconnect, SlobsPauseRecording, SlobsResumeRecording | Cuebot does not request the blocked-user scope, does not track the points users spend or Bits per user, sets categories by name, has no Trovo or VStream connection, keeps OBS and Streamlabs Desktop connected, and Streamlabs Desktop cannot pause recordings |

Data classes and enums use Streamer.bot's names and namespaces: every documented class (`TwitchUserInfo`,
`TwitchReward`, `TwitchRewardCounter`, `CommandCounter`, `QuoteData`, `TeamInfo`, `KickCategory`, `YouTubeUserInfo`,
`Cheermote*`, `GuestStar*`, `VTSModelPosition`, ...) is in `Streamer.bot.Plugin.Interface.Model`; `ClipData` and
`StreamMarker` in `Twitch.Common.Models.Api`; `Platform`, `TwitchBlockContext`, `TwitchBlockReason` and
`StreamlabsDesktopOutputType` in `Streamer.bot.Plugin.Interface.Enums`; the complete `EventType` (508 values) and
`EventSource` (46) in `Streamer.bot.Common.Events`. All of these namespaces are imported globally in CPHInline code,
as Streamer.bot's examples assume. Their numeric values are not documented, so compare enum values by name.
`tests/Cuebot.Scripting.Tests/Data` lists the documented names, and a test checks that each one exists.

Code options (Streamer.bot's sub-action settings):

- **Code name / Description.** Other code calls this code's methods by its name.
- **Precompile on application start / Delayed start.** The code compiles while Cuebot starts (delayed: once it has
  started), so a CPHInline `Init()` runs at startup, with a CPH that has no action run. Only enabled steps of enabled
  actions are precompiled. Without the option, `Init()` runs before the first execution.
- **Save result to variable / Variable name.** The bool result goes into that argument and the action continues
  whatever it is (by default false stops the action).
- **References.** Extra .NET DLLs, one full path per line; `scripting.json` → `CSharpReferences` lists DLLs for all
  code. Only listed files are loaded, from that exact path, when the script needs them; nothing is searched for or
  downloaded. Streamer.bot's "Find Refs" is not needed: every framework assembly is referenced already.
- **Execute C# method** sub-action (`code.csharpMethod`): calls a public parameterless method (or `Execute`) of the
  named code's CPHInline instance. False stops the action unless the result is saved; unknown code or methods fail
  the step. `CPH.ExecuteMethod` does the same from code (and returns false instead of failing).

Newtonsoft.Json 13 is bundled and referenced, as in Streamer.bot: add `using Newtonsoft.Json;`.

Methods not listed do not exist and fail at compile time with the usual C# error. A method whose Cuebot module is
missing throws `NotSupportedException` naming the method and the script function it needs.

Limitations: scripts target .NET 10, not .NET Framework 4.8 (Windows Forms, `System.Web`, etc. are unavailable).

## REST API (default 127.0.0.1:7475, prefix `/api/v1`)

Module: `src/Cuebot.Server/Api` (`ApiServer` routing, `ApiOperations` shared with MCP, `ActionValidator`,
`CatalogJson`, `OpenApiDocument`). Settings: `servers.json` → `api {enabled, address, port, token}`. UI: Servers page,
"REST API" card.

- **Off by default.** Binds 127.0.0.1; another address only with `allowLan` (else downgraded with a warning). One
  listener serves the REST API and MCP (`/mcp`); each half answers 404 while switched off. The listener runs whenever
  either half is on, and MCP is on by default (below), so it normally starts with Cuebot with only `/mcp` answering.
- **Bearer token for every request** (`Authorization: Bearer cb_...`). Generated (32 random bytes, base64url) when the
  API or MCP is first enabled or started; shown masked on the Servers page with Show / Copy / Regenerate. Compared in
  constant time (SHA-256 of both, `FixedTimeEquals`), never logged. Wrong or missing token → 401 with
  `WWW-Authenticate: Bearer`.
- **Origins** as for the other servers (no Origin, `null`, loopback and same-IP pages; others only when listed in
  `allowedOrigins`); `Sec-Fetch-Site: cross-site` without Origin is refused → 403. CORS preflights answered for allowed
  origins (Authorization, Content-Type, Mcp-Session-Id, MCP-Protocol-Version).
- Never exposed: settings files (twitch.json, youtube.json, servers.json...), platform tokens, the API token. Action
  parameters are user content and are returned as stored. Edits are logged ("Action 'X' updated") without values.
- 1 MB request body limit. Errors are `{error, details?: [{path, message}]}`.

JSON shapes are those of actions.json / commands.json (camelCase, enums as strings). All edits go through
`ProfileService.EditAsync`, `CommandStore` and `GlobalVariables`, so the UI updates at once.

| Endpoint | Behaviour |
| --- | --- |
| `GET /status` | app version, runtime, OS, platform connections, server statuses, counts, chat platforms |
| `GET /openapi.json` | OpenAPI 3.1 description of everything below |
| `GET /actions?group=&search=` / `POST /actions` | summaries / create (201) |
| `GET` / `PUT` / `PATCH` / `DELETE /actions/{id or name}` | full definition / replace / change given top-level fields / delete |
| `POST /actions/{id}/run` `{args, wait, timeoutSeconds}` | 202 queued, or 200 with the run (state, error, args) when `wait` |
| `POST /actions/{id}/enable`, `/disable` | |
| `GET /triggers?category=&search=`, `GET /triggers/{type}` | trigger types; one type adds `settingsSchema` (JSON Schema) and live suggestions |
| `GET /subactions?category=&search=`, `GET /subactions/{type}` | sub-action types (parameters, branches); one type adds `parametersSchema` |
| `GET /scriptfunctions?search=` | functions for C#/Python code |
| `GET` / `POST /commands`, `GET` / `PUT` / `PATCH` / `DELETE /commands/{id or name}` | GET one adds `linkedActions` (actions with a `command.triggered` trigger for it); regex commands are compiled to check them |
| `GET` / `POST /timers`, `GET` / `PUT` / `PATCH` / `DELETE /timers/{id or name}` | with runtime `state`; cron and intervals validated |
| `POST /timers/{id}/state` `{enabled, intervalSeconds}` | runtime override (like "Set timer state", not saved) |
| `GET /globals?persisted=`, `GET` / `PUT` / `DELETE /globals/{name}` | PUT `{value, persisted (default true)}` |
| `GET /history?limit=&action=&includeArgs=` | recent and pending runs, newest first |
| `GET /chat?limit=`, `POST /chat` `{message, platform (default All)}` | combined chat + activity feed / send (409 when nothing can send) |
| `POST /import/preview` `{data}`, `POST /import/apply` `{data, mode, items}` | Cuebot bundles and Streamer.bot exports; `mode` overwrite / addAsNew / skip; `items: [{kind, name or sourceId, include, mode}]` |
| `GET /export?actions=&commands=&timers=&globals=&includeDependencies=&name=` | `{data: "CUEBOT1:...", counts}` (comma separated ids or names) |
| `GET /events?types=` | Server-Sent Events: `event: <type>`, `data: {type, time, args}` for every bus event plus `action.runCompleted`; `types` filter with `prefix.*`; `: ping` every 15 s; max 16 streams (429) |

Validation (`ActionValidator`): unknown trigger / sub-action types and values that do not fit their descriptor (Number
not a number, Boolean not true/false, Choice not one of the choices) → 400 with one `details` entry per problem, e.g.
`subActions[4].branches.True[0].type`. Values containing `%`, `~` or `$` (templates) are not checked. Unknown parameter
keys and branches the step does not offer are only `warnings` (imports and older files carry them). Empty or duplicate
ids are replaced; on create an existing id is 409.

### Remote instances (`/api/v1/remote` WebSocket)

Module: `src/Cuebot.Remote` (wire format in `src/Cuebot.Remote/PROTOCOL.md`). Settings: `remote.json` →
`{instanceId, instanceName, releaseLocalWaiters, instances: [{id, name, host, port, token, enabled, reconnectSeconds}]}`.
UI: Servers page, "Remote instances" card. Other Cuebot instances connect to this endpoint with this instance's API
token; this instance connects to the ones in `remote.json` with theirs. One link carries signals both ways.

| Kind | Name | Notes |
| --- | --- | --- |
| Sub-action | Send remote signal (`remote.sendSignal`) | instance (empty = all), name, include action arguments, extra `key=value` lines; sets `%remoteSignalSent%` |
| Sub-action | Wait for remote signal (`remote.waitForSignal`) | instance (empty = any), name, timeout (default 30000 ms, 0 = forever), stop on timeout, overwrite variables; sets `%signalReceived%`, `%signalTimeout%`, `%hasResult%`, the signal's arguments and instance fields |
| Trigger | Remote signal received (`remote.signal`) | filters: signal name, instance |
| Trigger | Remote instance connected / disconnected | filter: instance |
| Script | `remote.sendSignal {name, instance?, args?}`, `remote.waitForSignal {name, instance?, timeoutMs?}`, `remote.getInstances` | |

## MCP server (`/mcp` on the API listener)

Module: `src/Cuebot.Server/Mcp`. Settings: `servers.json` → `mcp {enabled, requireConfirmation (default true)}`; uses the
API's port and token. UI: Servers page, "MCP server" card with the URL and copyable client configs. No SDK: JSON-RPC 2.0
implemented in `McpServer`.

- **Starts with Cuebot** (`servers.json` version 2), so AI tools on this computer and the Assistant page
  ([ASSISTANT.md](ASSISTANT.md)) can use it: 127.0.0.1 only, the bearer token is required, and destructive tools still
  need confirmation. A version 1 file had MCP off only because that was the default, so `ServerManager` switches it on
  once (logged) and saves version 2; turning it off on the Servers page sticks. Settings built in memory (tests) keep
  the old default, off.

- Protocol revision **2025-06-18** (also answers 2025-11-25, 2025-03-26 and 2024-11-05 clients in their revision; an
  unknown one gets 2025-06-18). Capabilities: `tools`, `resources`, `prompts` (no list-changed notifications, no
  subscriptions). Methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`,
  `resources/templates/list`, `resources/read`, `prompts/list`, `prompts/get`; notifications (`initialized`,
  `cancelled`...) are accepted and need no answer; batches are answered too.
- **Streamable HTTP**: `POST /mcp` with one message or a batch → `application/json` (or a single SSE event when the
  client accepts only `text/event-stream`); notifications → 202. The initialize response carries `Mcp-Session-Id`;
  later requests may send it (unknown → 404, so clients re-initialize); `DELETE /mcp` ends a session; `GET /mcp` → 405
  (no server-initiated stream). Unsupported `MCP-Protocol-Version` → 400. Same token and origin rules as the REST API.
- **stdio**: `Cuebot --mcp-stdio` (`McpStdioBridge`) does not start Cuebot: it reads `servers.json` in the data folder
  (`--data` / `CUEBOT_DATA` respected), forwards each stdin line to the running instance's `/mcp` with the token and
  writes the answers to stdout (diagnostics on stderr). `CUEBOT_MCP_URL` / `CUEBOT_API_TOKEN` override. When Cuebot is
  not running, requests get JSON-RPC error -32000 "Cannot reach Cuebot ..."; an expired session is dropped and retried.
- Tool calls are logged as `MCP: Tool call: <name>`, never with argument values.
- **Assistant conversations** with an AI app get their own bearer token (`McpServer.RegisterAssistant`), accepted on
  `/mcp` only (the REST API answers 401) until the conversation ends. With it, destructive tools ask the streamer on the
  Assistant page while the call waits, instead of answering with a confirmation token ([ASSISTANT.md](ASSISTANT.md)).

Tools (all backed by the REST operations; schemas include JSON Schemas built from the registry descriptors where it
helps: `describe_*`, `create_action` with a recursive `$defs/subAction`):

| Tool | Notes |
| --- | --- |
| list_actions, get_action, create_action, update_action (merge, or `replace`), delete_action (destructive), run_action (waits by default), set_action_enabled | |
| list_trigger_types, describe_trigger_type, list_subaction_types (category / search), describe_subaction_type, list_script_functions | read-only |
| list_commands, get_command, create_command, update_command, delete_command (destructive) | |
| list_timers, create_timer, update_timer, set_timer_state, delete_timer (destructive) | |
| get_globals (all or one), set_global, delete_global (destructive) | |
| get_status, get_history, get_recent_chat, send_chat_message (destructive) | |
| preview_import, apply_import (destructive), export_bundle | |

Annotations: read-only tools `readOnlyHint: true`; destructive tools `destructiveHint: true`; everything else
`destructiveHint: false`; run_action, get_recent_chat and send_chat_message `openWorldHint: true`. With
`requireConfirmation` on, a destructive tool first answers `{confirmationRequired: true, action: "Delete the action 'X'",
confirmationToken, expiresInSeconds: 300}` and does nothing; calling it again with the same arguments plus
`confirmationToken` performs it. Tokens are single-use, bound to the tool and its exact arguments, and expire after 5
minutes. Tool failures are results with `isError: true` and `{error, details?}`; unknown tools and methods are JSON-RPC
errors (-32602 / -32601).

Resources: `cuebot://actions` (summaries), `cuebot://actions/{id}` (template; every action is also listed),
`cuebot://catalog/subactions`, `cuebot://catalog/triggers` (full catalogue with schemas), `cuebot://docs/variables`
(%arguments%, ~globals~, formats, $functions()$), `cuebot://docs/actions` (how actions, branches, commands and timers
fit together). Unknown → -32002.

Prompts: `create_action_from_description {description, trigger?}` (a step-by-step recipe using the catalogue tools) and
`troubleshoot_action {action, problem?}` (embeds the action JSON and its last 10 runs with arguments).

### Client setup

- Claude Code: `claude mcp add --transport http cuebot http://127.0.0.1:7475/mcp --header "Authorization: Bearer TOKEN"`
- HTTP clients (.mcp.json style): `{"mcpServers": {"cuebot": {"type": "http", "url": "http://127.0.0.1:7475/mcp",
  "headers": {"Authorization": "Bearer TOKEN"}}}}`
- Claude Desktop and other stdio clients: `{"mcpServers": {"cuebot": {"command": "<path to Cuebot>", "args":
  ["--mcp-stdio"]}}}` (add `"--data", "<folder>"` for a non-default data folder). Cuebot must be running with MCP on.

The Servers page fills in the real URL, path and token (masked on screen unless Show is on; Copy copies the real one).
