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
| Triggers | RegisterCustomTrigger, TriggerCodeEvent(name, useArgs), TriggerCodeEvent(name, args), TriggerEvent(name, useArgs) | "Custom code event" trigger; TriggerEvent is the same as TriggerCodeEvent (Cuebot has one custom event trigger) |
| Other code | ExecuteMethod(codeName, methodName) | runs a public parameterless method on another C# sub-action's CPHInline instance (same instance its Execute() uses) with this run's args; the code is found by the sub-action's "Code name", else as the first CPHInline code in the action named `codeName`; returns the method's bool (void → true), false when not found |
| Twitch chat | SendWhisper (bool), TwitchAnnounce, SendAction | whisper uses the bot when `bot` and it is signed in; announcements are sent by the broadcaster (colour `default` → primary); SendAction sends a /me line through the Twitch action announcer when one is registered, otherwise as a plain chat message |
| Twitch users | TwitchGetBroadcaster, TwitchGetBot, TwitchGetUserInfoByLogin/ById → `TwitchUserInfo`; TwitchGetExtendedUserInfoByLogin/ById → `TwitchUserInfoEx` | null when not found. Roles and LastActive come from Cuebot's user list (what chat showed); PreviousActive is not tracked. Ex: follow/subscription filled when the scopes allow; GameId and Tags stay empty; AccountAge is in seconds |
| Twitch moderation / stream | TwitchTimeoutUser, TwitchBanUser, TwitchUnbanUser, TwitchRunCommercial (bool, 30-180 s), SetChannelTitle (bool), SetChannelGame → `GameInfo`, CreateStreamMarker → `StreamMarker`, CreateClip() / CreateClip(title, duration) → `ClipData` | done as the broadcaster (`useBot` ignored); failures return false/null and log a warning. Clip title/duration are not sent (Twitch's API has no such fields); ClipData carries what Twitch returns on creation (id, url, edit/embed url, broadcaster) |
| YouTube | YouTubeGetLatestMonitoredBroadcast (alias YouTubeGetLatestBroadcast), YouTubeGetMonitoredBroadcasts → `YouTubeBroadcastInfo` | id, channel, live chat id, title, description, privacy, status, url; category, tags, languages and dates stay empty |
| Kick | KickGetBroadcaster, KickGetBot → `KickUserInfo` | null when not signed in |
| Keyboard | KeyboardPress (SendKeys syntax: `{W}`, `+{W}`, `^{W}`, `%{W}`) | uses Cuebot's keyboard output (SendInput on Windows; XDG RemoteDesktop portal or X11 XTest on Linux) |
| WebSocket | WebsocketBroadcastJson/String, WebsocketConnect/Disconnect/IsConnected/Send, WebsocketGetConnectionByName, WebsocketCustomServer{Start,Stop,IsListening,Broadcast,CloseSession,CloseAllSessions,GetConnectionByName} | needs the Servers module |
| Integrations | DiscordPostTextToWebhook | |
| **Unsupported** (throw `NotSupportedException: CPH.<Name> is not supported by Cuebot`) | TwitchClientId, TwitchOAuthToken | Cuebot never hands platform credentials to scripts |

Data classes use Streamer.bot's names and namespaces: `TwitchUserInfo`, `TwitchUserInfoEx`, `GameInfo`,
`YouTubeBroadcastInfo`, `KickUserInfo` in `Streamer.bot.Plugin.Interface.Model` (imported globally, as Streamer.bot's
examples assume), `ClipData` and `StreamMarker` in `Twitch.Common.Models.Api`.

Methods not listed do not exist and fail at compile time with the usual C# error. A method whose Cuebot module is
missing throws `NotSupportedException` naming the method and the script function it needs.

Limitations: scripts target .NET 10, not .NET Framework 4.8 (Windows Forms, `System.Web`, etc. are unavailable);
Newtonsoft.Json is not bundled (use System.Text.Json); "Precompile on application start" is not implemented, so
`Init()` (and `RegisterCustomTrigger` in it) runs before the script's first execution.

## REST API (default 127.0.0.1:7475, prefix `/api/v1`)

Module: `src/Cuebot.Server/Api` (`ApiServer` routing, `ApiOperations` shared with MCP, `ActionValidator`,
`CatalogJson`, `OpenApiDocument`). Settings: `servers.json` → `api {enabled, address, port, token}`. UI: Servers page,
"REST API" card.

- **Off by default.** Binds 127.0.0.1; another address only with `allowLan` (else downgraded with a warning). One
  listener serves the REST API and MCP (`/mcp`); each half answers 404 while switched off.
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
