# Local API servers and Streamer.bot / Speaker.bot compatibility

Module: `src/Cuebot.Server` (servers) and `src/Cuebot.Scripting/CSharp/Compat` (CPH layer, event-name map, code
triggers). Settings: `servers.json`. UI: Servers page (`ViewModels/Servers`, `Views/Servers`).

Hosting: Kestrel from the shared ASP.NET Core framework (`FrameworkReference Microsoft.AspNetCore.App`, no NuGet
package). Unlike `HttpListener` (http.sys on Windows) it needs no admin rights or URL ACLs for LAN binding, behaves the
same on Windows and Linux, and has WebSockets built in. An empty host is used: no config files, env vars or logging.

## Security defaults

- Every server is **off** until enabled on the Servers page.
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
source}, authentication?: {salt, challenge}}`. `Authenticate {authentication}` with
`base64(sha256(base64(sha256(password + salt)) + challenge))`. Responses `{status: "ok"|"error", id, ...}`.

| Request | Response / behaviour |
| --- | --- |
| Authenticate | ok / error "Authentication failed" |
| GetInfo | `info` |
| GetEvents | `events: {Source: [Type...]}` (catalogue; Subscribe accepts any names) |
| Subscribe / UnSubscribe | `events: {Source: [Type...]}` (current subscriptions; `"*"` = all types) |
| GetActions | `actions: [{id, name, group, enabled, subaction_count}], count` |
| DoAction | `{action: {id \| name}, args}` → queued (not awaited); error if missing/disabled |
| GetBroadcaster | `platforms: {twitch: {name, state, status}...}, connected: [...], disconnected: [...]` |
| GetActiveViewers | `viewers: [{id, login, display, platform, role, subscribed, previousActive, ...}], count` |
| GetCodeTriggers | `triggers: [{name, eventName, category}], count` |
| ExecuteCodeTrigger | `{triggerName, args}` → fires the "Custom code event" trigger |
| GetCommands | `commands: [{id, name, enabled, group, mode, location, commands, caseSensitive, globalCooldown, userCooldown, sources}], count` |
| GetGlobals / GetGlobal | `{persisted}` → `variables: {name: {name, value, lastWrite}}, count` / `{variable}` → `variable` |
| TwitchGetUserGlobals / TwitchGetUserGlobal (+ YouTube*, Kick*, GetUserGlobals/GetUserGlobal with `platform`) | `variables: [{userId, userLogin, userName, platform, name, value, lastWrite}], count` |
| GetCredits / TestCredits / ClearCredits | `credits: {}` (Cuebot has no credits roll yet) |
| SendMessage | `{message, platform}` → combined chat send; privileged |
| TwitchGetEmotes / YouTubeGetEmotes | `emotes: {platform: [], bttv: [], ffz: [], sevenTv: []}` (empty) |
| GetMonitoredYouTubeBroadcasts | `broadcasts: []` |
| GetUserPronouns | `pronoun: {userLogin, userFound: false}` |
| ClearFirstWordsCache | resets first-words tracking |

Events: `{timeStamp, event: {source, type}, data}`. `data` holds the Cuebot event arguments plus `cuebotEvent` (the
original type); for events with a `user` argument it adds a Streamer.bot-like `user` object `{id, login, name,
display_name, color, role}` (the flat name stays as `userName`) and `text` for chat messages. Also sent:
`Application.ActionAdded/Deleted/Updated`, `Application.CommandAdded/Deleted/Updated`, `Raw.ActionCompleted`, and
`General.Custom` for CPH `WebsocketBroadcastJson/String` and the "WebSocket server: broadcast" sub-action.

### Event mapping (`StreamerBotEventMap`)

Explicit entries; anything else maps generically (`module.camelCase` → `Module.PascalCase`, module spelled as in
`Sources`: twitch→Twitch, youtube→YouTube, kick→Kick, obs→Obs, groups→Group, ...), so new module events are forwarded
without changes.

| Cuebot event | Streamer.bot |
| --- | --- |
| twitch.chatMessage / follow / sub / giftSub / giftBomb / cheer / raid / rewardRedemption / streamOnline / streamOffline | Twitch.ChatMessage / Follow / Sub / GiftSub / GiftBomb / Cheer / Raid / RewardRedemption / StreamOnline / StreamOffline (generic) |
| twitch.resub, twitch.firstWords, twitch.messageDeleted, twitch.userTimedOut | Twitch.ReSub, Twitch.FirstWord, Twitch.ChatMessageDeleted, Twitch.UserTimedOut |
| youtube.chatMessage, youtube.memberMilestone, youtube.userTimedOut | YouTube.Message, YouTube.MemberMileStone, YouTube.UserTimedout |
| kick.sub, kick.resub, kick.giftSub, kick.massGiftSub | Kick.Subscription, Kick.Resubscription, Kick.GiftSubscription, Kick.MassGiftSubscription |
| obs.connected / disconnected / sceneChanged | Obs.Connected / Disconnected / SceneChanged |
| obs.streamStarted / streamStopped / recordStarted / recordStopped | Obs.StreamingStarted / StreamingStopped / RecordingStarted / RecordingStopped |
| command.triggered / cooldown | Command.Triggered / Cooldown |
| groups.added / removed / cleared / usersAdded / usersRemoved | Group.Added / Removed / Cleared / UsersAdded / UsersRemoved |
| variables.globalCreated / globalUpdated / globalDeleted / userUpdated | Misc.GlobalVariableCreated / GlobalVariableUpdated / GlobalVariableDeleted / UserGlobalVariableUpdated |
| core.appStarted, core.timer, core.processStarted / processStopped | Misc.StreamerbotStarted, Misc.TimedAction, Misc.ProcessStarted / ProcessStopped |
| core.fileCreated / fileChanged / fileDeleted / fileRenamed, core.fileTailLine | FileWatcher.Created / Changed / Deleted / Renamed, FileTail.Changed |
| users.firstWords, users.presentViewers | {platform}.FirstWords (Twitch: FirstWord), {platform}.PresentViewers |
| custom.codeEvent | Custom.CodeEvent |
| websocket.clientOpen / clientClose / clientMessage | WebsocketClient.Open / Close / Message |
| websocket.serverOpen / serverClose / serverMessage | WebsocketCustomServer.Open / Close / Message |
| speech.started / finished / stateChanged | Speech.SpeechStarted / SpeechFinished / StateChanged (Cuebot source) |
| chat.message, chat.activity | Chat.Message, Chat.Activity (Cuebot source: combined chat/feed; used by the docks) |
| rumble.* | Rumble.* (Cuebot source) |

## HTTP server (default 127.0.0.1:7474)

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/DoAction` (and `/DoAction`) | body `{action: {id \| name}, args}` → 204; 404 unknown action; GET → 405 |
| `GET /api/GetActions`, `GetInfo`, `GetEvents`, `GetBroadcaster`, `GetActiveViewers`, `GetCodeTriggers`, `GetCommands`, `GetGlobals`, `GetGlobal?variable=`, `*GetUserGlobals`, `GetCredits`, `TestCredits`, emotes, `GetMonitoredYouTubeBroadcasts`, `GetUserPronouns` | same JSON bodies as the WebSocket responses (query string = request fields) |
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
| OBS (connection 0) | ObsIsConnected, ObsSetScene, ObsGetCurrentScene, ObsSetSourceVisibility, ObsShowSource, ObsHideSource, ObsIsSourceVisible, ObsToggleSource, ObsShowFilter/ObsHideFilter (2 overloads), ObsSetGdiText, ObsSetBrowserSource, ObsSetImageSourceFile, ObsSetMediaSourceFile, ObsStart/StopStreaming, ObsStart/StopRecording, ObsIsStreaming, ObsIsRecording, ObsSendRaw | via `obs.request` (obs-websocket v5) |
| Speech / sound | TtsSpeak, PlaySound (volume 0-1, wait, sound name tag), StopSoundPlayback, StopAllSoundPlayback | via `speech.speak` / audio sub-actions |
| Commands | EnableCommand, DisableCommand, GetCommands, CommandGetCounter, CommandResetCounter, CommandResetAllUserCounters, CommandResetGlobalCooldown, CommandResetAllUserCooldowns, CommandSetGlobalCooldownDuration, CommandSetUserCooldownDuration | cooldown resets clear global and user cooldowns together |
| Groups | AddGroup, DeleteGroup, GetGroups, GroupExists, AddUserToGroup, AddUserIdToGroup, RemoveUserFromGroup, RemoveUserIdFromGroup, UserInGroup, UserIdInGroup, UsersInGroup, ClearUsersFromGroup (+ platform-less Twitch overloads) | |
| Timers | EnableTimer, DisableTimer, EnableTimerById, DisableTimerById, GetTimerState, SetTimerInterval | |
| Triggers | RegisterCustomTrigger, TriggerCodeEvent(name, useArgs), TriggerCodeEvent(name, args) | "Custom code event" trigger |
| WebSocket | WebsocketBroadcastJson/String, WebsocketConnect/Disconnect/IsConnected/Send, WebsocketGetConnectionByName, WebsocketCustomServer{Start,Stop,IsListening,Broadcast,CloseSession,CloseAllSessions,GetConnectionByName} | needs the Servers module |
| Integrations | DiscordPostTextToWebhook | |
| **Unsupported** (throw `NotSupportedException: CPH.<Name> is not supported by Cuebot`) | TwitchClientId, TwitchOAuthToken, SendWhisper, TwitchAnnounce, TwitchGetBroadcaster, TwitchGetBot, TwitchGetUserInfoByLogin/ById, TwitchGetExtendedUserInfoByLogin/ById, TwitchTimeoutUser, TwitchBanUser, TwitchUnbanUser, TwitchRunCommercial, CreateStreamMarker, CreateClip, SetChannelTitle, SetChannelGame, YouTubeGetLatestBroadcast, KickGetBroadcaster, KeyboardPress, TriggerEvent, ExecuteMethod, SendAction | data-returning ones are typed `dynamic` so member access compiles |

Methods not listed do not exist and fail at compile time with the usual C# error. A method whose Cuebot module is
missing throws `NotSupportedException` naming the method and the script function it needs.

Limitations: scripts target .NET 10, not .NET Framework 4.8 (Windows Forms, `System.Web`, etc. are unavailable);
Newtonsoft.Json is not bundled (use System.Text.Json); "Precompile on application start" is not implemented, so
`Init()` (and `RegisterCustomTrigger` in it) runs before the script's first execution.
