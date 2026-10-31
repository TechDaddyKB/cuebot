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
| `GET /o/{id}/ws?token=...` (WebSocket) | `{"type":"hello","overlay":{id,name,width,height},"session":{...},"recents":[...],"alerts":{paused,muted}}`, then every `OverlayFeed` message for that overlay: `{"type":"event","listener","event"}` (StreamElements-shaped), `{"type":"session","session"}`, `{"type":"command","command"}`. The page's own origin, loopback pages and `allowedOrigins` may connect. |
| `GET /o/{id}/l/{layerId}?token=...` | A layer's widget settings: `{src, fieldData, widgetDuration}`; `src` is the document's path on the widget port. |
| `GET /runtime/overlay.js`, `/runtime/overlay.css` | The page runtime (`window.Cuebot`: `overlay`, `on(type, fn)`, `send(text)`; reconnects with backoff, `reload` command reloads; runs each layer's widget in a frame and relays messages). |
| Widget port (default 7477) `GET /w/{id}/{layerId}?key=...` | The widget document: jQuery 3.7.1 (bundled, `/runtime/jquery-3.7.1.min.js`, as StreamElements preloads it), `/runtime/se-widget.js`, then the CSS pane, your CSS override, the HTML and JS panes, with `{field}` / `{{field}}` filled in. The key is HMAC-SHA256 of the layer id with the overlay token (it opens only that layer and changes with a new link). The widget port also serves `/runtime` and `/assets`, and nothing else. |
| `GET /assets/{sha256}.{ext}` | Files from `overlays/assets` (images, video, audio, fonts), `Cache-Control: immutable`, `Content-Security-Policy: sandbox`. |

- Tokens: `ov_` + 32 random bytes, one per overlay in `overlays.json`, compared in constant time; "New link" replaces it.
- Widgets run in frames from the widget port: another web origin (same host, other port), so a widget cannot read the
  overlay page, its token or call Cuebot's API, yet can load Cuebot's scripts, badge icons and uploaded files (browsers
  block frames without an origin of their own from reaching local servers, so `sandbox` alone does not work). The
  frames are sandboxed against popups, forms and navigating the page.
- Widget runtime (`se-widget.js`, written from StreamElements' public widget docs): `onWidgetLoad` (`channel`,
  `currency`, `fieldData`, `recents`, `session`, `overlay`), `onEventReceived` with StreamElements' queue (alerts wait
  for `widgetDuration` seconds or `SE_API.resumeQueue()`; chat, deletes, counters, kvstore, test and skip events are
  never held), `onSessionUpdate`, and `SE_API`: `store.get/set` (widget-store.json, shared by all widgets; each set
  sends `kvstore:update`), `counters.get` (numeric global variables only; changes send `bot:counter`), `sanitize` (no
  word filter yet), `cheerFilter`, `getOverlayStatus`, `resumeQueue`; `setField` does nothing outside an editor.
- Widget code lives in `overlays/widgets/{elementId}/` (`widget.html/css/js`, `fields.json`, `data.json`, `override.css`); imports take
  widget.io zips (`widget.ini`) or zips with the five panes by name. Changing an overlay or its widget code reloads the
  pages that show it.
- Streamlabs custom code (`StreamlabsWidget`, `sl-widget.js`, written from Streamlabs' public custom widget docs and
  starter templates): imported or pasted code is recognised by its template ids (`chatlist_item` → chat box,
  `eventlist_item` → event list, `alert-wrap` → alert box) or its `document` events (`goalLoad`/`goalEvent` → goal, any
  other → generic); code that uses `onWidgetLoad` is StreamElements code and left alone. Adapting converts the custom
  fields (`textfield` → `text`, `fontpicker` → `googleFont`, slider `steps` → `step`), adds the settings Streamlabs
  fills its built-in tokens from (chat box: `background_color`, `text_color`, `font_size`, `message_hide_delay`; event
  list: `background`, `font_family`, `max_events`, animations...; alert box: `widgetDuration` and a
  `messageTemplate_{type}` per alert; goal: `goalType`, `goalTitle`, `goalTarget`, `goalStart`), and marks it with the
  hidden `widgetFormat: "streamlabs"` and `widgetSlType` fields; it runs once (a saved widget keeps its fields). The
  document then also loads `sl-widget.js`, which fires `onLoad` and `onEventReceived` on `document`: the chat box
  renders each message into `#log` from `#chatlist_item` (`{from}`, `{messageId}`, `{color}`, `{message}` with emotes,
  badges into `.badges`, at most 100) and then sends Streamlabs' PRIVMSG detail (`platform` is `{service}_account`);
  deletes remove `[data-id]` / `[data-user-id]` items. Alerts become `{type, from, name, message, amount,
  formattedAmount, currency, tag, ...}` (follow, subscription, donation, bits, raid, host, superchat, merch,
  twitchcharitydonation, redemption...); the event list prepends `#eventlist_item` (`{eventType}`, `{tag}`, `{from}`)
  to `.widget-EventList` (made if missing; filled from recents at load). An alert box's HTML and JS are not in the
  page: each alert fills `{name}`, `{amount}`, `{message}`, `{userMessage}`, `{img}`, `{count}`, `{product}` and
  `{messageTemplate}` (HTML-escaped in the HTML, string-safe in the JS), shows it in `#sl-alert`, runs the JS, and
  clears after `widgetDuration`, which also holds the queue. Goals fire `goalLoad` and `goalEvent` (on each session
  update) from the session's `{type}-goal`. Streamlabs' boss and spin wheel events are not sent.

### Built-in chat box (`Cuebot.Core/Overlays/Widgets/BuiltIn/ChatBox`)

"Add chat box" on the Overlays page adds Cuebot's chat box (embedded resources, written on the StreamElements widget API
and stored like any imported widget, so the editor and My CSS work on it): chat from every platform with platform
tags and colours, badges, emotes, profile pictures where the platform has them, Twitch pronouns, in-chat follow, sub,
tip, cheer, Kicks, Super Chat and raid lines, command and user filters, message limit, hide-after and animation. Its
"YouTube popout" layout builds YouTube popout chat's elements (`yt-live-chat-text-message-renderer` with
`author-type`, `#author-photo`, `yt-live-chat-author-chip`, `#author-name` with owner/moderator/member classes,
`#chat-badges`, `#message` with `img.emoji`), so YouTube chat CSS themes pasted into My CSS restyle chat from every
platform. This follows YouTube's popout structure as themes target it; themes relying on other parts of YouTube's page
may need adjusting.

Chat events also carry `data.avatar` (YouTube and Kick profile pictures) and `data.pronouns` (Twitch chatters with
pronouns on alejo.io; read from Cuebot's cache, a chatter's first message starts the lookup and later ones carry it).

### Built-in alert box (`Cuebot.Core/Overlays/Widgets/BuiltIn/AlertBox`)

"Add alert box" on the Overlays page (or in the designer's Add menu) adds Cuebot's alert box, also written on the
StreamElements widget API. Its element is marked `{"builtIn": "alertbox"}`, so its editor link opens the alert designer
(`alerts.html`; `?code=1` opens the widget editor for its code and My CSS). The designs are the `alerts` value in the
widget's DATA (the hidden `alerts` field holds the defaults): `{version, randomPick, types: {follower, subscriber, tip,
cheer, raid, superchat, kicks, redemption, jewels, charityCampaignDonation, custom}}`, each type `{enabled, design,
variations[]}`. A design is `{template, showMessage, duration, media, mediaVolume, sound, volume, layout, font, size,
color, highlight, messageSize, enter, exit, tts, ttsText, ttsAlias}`; templates use `{name}`, `{amount}`, `{formattedAmount}`, `{currency}`,
`{message}`, `{tier}`, `{sender}`, `{platform}` and `{redemption}`, shown in the highlight colour. A variation is
`{name, enabled, param, op, value, value2, chance, design}`: `param` is `amount`, `tier`, `platform` (the event's
`service`), `name` (comma-separated list), `gifted`, `bulk`, `currency` or `message`; `op` is `atLeast`, `exact`,
`between`, `is` or `contains`. The first matching variation from the top is shown (or a random matching one with
`randomPick`), each only `chance` percent of the time; otherwise the type's own design. The sound is a file, `none` or
`chime` (synthesized, no file needed). The alert box holds the queue (`widgetDuration` 90) and releases it with
`SE_API.resumeQueue()` after each alert's exit animation, so alerts play one at a time; `event:skip` ends the current
alert and `alertService:toggleSound` mutes sound and video. `cuebot-alert` events show the `custom` design (or the
type named by `alertType`). The designer previews unsaved designs with sample events (shaped to match the selected
variation) and through Emulate, which goes through the queue like live alerts.

With `tts` on, the alert box asks Cuebot's speech to read `ttsText` (same placeholders, default `{message}`) with the
`ttsAlias` voice, through the widget request `cuebot_tts {text, alias}`. Only a layer whose element is Cuebot's alert
box may make it (any other widget gets an error); the text is cut at 500 characters, speech's bad word, link and length
filters apply, and it joins the speech queue, so Pause and Speak next work on it. Muted alerts do not speak. The
designer's Test button speaks; its automatic preview does not.

**Alert queue controls** (`OverlayAlertQueue`, not saved): pause (the alert playing finishes, new ones wait; the
runtime's queue stops delivering), resume, skip (ends the alert playing; the runtime tells the widget before releasing
the queue, so the next alert is not ended too) and mute (sound, video and alert TTS). Pages get the state in their
hello and `pause` / `resume` / `skip` / `mute` / `unmute` commands when it changes; the overlay page passes pause on as
`{type: "queue", paused}` and in `onWidgetLoad`'s `overlay.paused`. Controlled from the Overlays page (Pause alerts, Skip
alert, Mute alerts), the "Alert queue" sub-action, `POST /api/v1/alerts/{command}` and the `control_alerts` MCP tool.
The "Show alert" sub-action sends a `cuebot-alert` event (design, name, message, amount, currency, platform), which
waits its turn in the queue like a real alert.

### Labels, goals and lists (`Cuebot.Core/Overlays/Widgets/BuiltIn/{Label,Goal,EventList,Credits,Countdown}`)

Five more built-in widgets on the StreamElements widget API (`BuiltInWidgets.Kinds`, added with
`WidgetLibrary.AddBuiltIn`, placed for their kind and scaled to the canvas; on the Overlays page under "Add Cuebot
widget" and in the designer's Add menu). Each is edited with the widget editor's settings form; none holds the alert
queue (no `widgetDuration`).

- **Label**: the latest name (or name and amount), a count or amount for this stream, week, month or all time, the top
  single donation or top donator, or recent names, for one alert type or "revenue" (tips, Super Chats and charity
  donations added up; amounts of different currencies are added as they are). Flashes when it changes.
- **Goal**: a bar towards a goal of followers, subs, tips, bits, Kicks, Jewels or revenue for a period, counting how
  many or the amount, plus a starting offset; numbers as "12 / 100 (12%)", progress, percent or none; a text and colour
  when reached.
- **Event list**: recent alerts from every platform (from the page's `recents`, then live `*-latest` events), filtered
  by type, newest at the top or bottom, with the platform and optionally tip messages; can hide events after a time.
- **Credits**: a roll of this stream's subscribers, followers, tippers (tips, Super Chats and charity), cheerers, Kicks
  senders and raiders from `{type}-session-users`, with headings, optional amounts, speed and looping (new names join at
  the next pass).
- **Countdown**: minutes, or to a time ("20:00", or a date and time). A minutes countdown keeps its end in the widget
  store under its timer name (`cuebot-countdown:{name}`), so reloading or another page shows the same timer; a finished
  one starts again when loaded hours later (setting), or with its "Start the countdown again" button.

Engagement widgets (phase 5a), built the same way:

- **Emote rain**: emotes in chat from every platform (their emote images, as chat events carry them) fly across the
  overlay as rain, a fountain, floating up or bouncing; a chat command (`!rain`, who may use it and a cooldown set in
  its settings) sends a big burst of the emotes in it; follows, subs, tips, cheers, Kicks and raids send bursts of
  recently seen emotes (or set fallback images or emoji before any were seen).
- **Hype cup**: follows, subs (each gift counts), bits and Kicks per 100, tips and Super Chats per 1.00, and raids drop
  coloured balls (simple 2D physics) into a cup until it is full. The contents are kept in the widget store
  (`cuebot-hypecup:{name}`), so reloading keeps them; "Empty the cup" starts over.
- **Hype boss**: the same support deals damage to the boss; the final hit makes that supporter the next boss with more
  health (gift subs are the gifter's attack), and with "self heal" the boss's own support heals them. The fight is kept
  in the widget store (`cuebot-hypeboss:{name}`). Several pages showing the same cup or fight each apply the same
  events and save the same result.

### Polls and predictions (`Cuebot.Core/Polls`, phase 5b)

`PollTracker` keeps the latest poll and prediction from each source in one shape (`PollState`: `{source: twitch |
youtube | cuebot, kind: poll | prediction, id, title, status: active | locked | ended | canceled, choices: [{id, title,
votes, points, percent}], winner, endsAt, totalVotes, totalPoints, currency, command}`). Twitch (EventSub polls and
predictions) and YouTube (live chat polls) report through `IPollAdapter`s. Every change goes to overlay pages as the
`cuebot-poll` event (never held by the alert queue; vote counts while open may be dropped for a slow page, final states
never are), and pages get the current ones in their hello (`polls`) and widgets in `onWidgetLoad`'s `polls`.

`ChatPolls` runs Cuebot's own, for every platform's chat (Kick and Rumble have none of their own), kept in
chat-polls.json:

- **Chat polls**: `!vote 2` or `!vote choice title` (the last vote counts; a bare number votes too when turned on);
  2 to 10 choices; ends after its seconds (or 0: when ended). Starting one while one runs is refused.
- **Predictions with Cuebot points**: `!bet outcome amount` (or `all`) takes the points; one side per viewer (more on
  the same side adds up); bets close after the window or when locked. Resolving pays each winner their share of the
  whole pot (pot × bet ÷ winning bets, rounded down, never less than the bet); cancelling refunds every bet. Bets are
  saved, so a restart keeps them.
- Controlled from the Overlays page ("Chat poll and prediction"), the Polls sub-actions ("Start chat poll", "End chat
  poll", "Start chat prediction", "Lock, resolve or cancel chat prediction"), and the REST API (`GET /polls`,
  `POST /polls`, `POST /polls/end`, `POST /predictions`, `POST /predictions/{lock, resolve, cancel}`). Triggers: chat
  poll started and ended, chat prediction started, locked, resolved and cancelled (`cuebot.*` events with the title,
  choices or outcomes, votes, points and winner).

The **Poll and prediction** widget shows the running poll or prediction from any source or one (polls, predictions or
both): bars with votes, percent and points, the time left, how to vote for Cuebot polls, and the winner; it hides a
set time after the end.

### Giveaways and the points store (`Cuebot.Core/Giveaways`, `Cuebot.Core/Store`, phase 5c)

Both work in every platform's chat with Cuebot points and are run from the "Giveaways & Store" page, sub-actions and
the REST API.

- **Giveaways** (giveaways.json): opened with a prize, join command (`!join`, `!join 3` for more tickets), points per
  ticket (0: free), most tickets per viewer, points currency, subscriber luck (their tickets count that many times),
  subscribers only, a time limit (0: until closed) and the number of winners. Drawing picks winners at random weighted
  by tickets and luck, never the same viewer twice; "draw one more" adds a winner; cancelling refunds every ticket.
  Winners can be announced in chat on every platform. States go to overlays as `cuebot-giveaway` (never held by the
  alert queue; in the hello as `giveaway`). Triggers: giveaway opened, entered, closed, winner drawn, cancelled.
  Sub-actions: "Open giveaway", "Close, draw or cancel giveaway". REST: `GET /giveaway`, `POST /giveaway`,
  `POST /giveaway/{close, draw, redraw, cancel}`.
- **Store** (store.json): items with a cost (and currency), stock (-1 unlimited), per-viewer and global cooldowns,
  subscribers only and "needs approval". Viewers buy with `!redeem item name anything else` (the longest matching name;
  the rest is the input). A purchase fires "Store item redeemed" (filtered by item; args item, cost, user, platform,
  input/rawInput) and shows on overlays as a `redemption-latest` alert with `store: true`; approval items wait in the
  pending list (approve, or reject to refund and restock). Sub-actions: "Set store item" (available, stock),
  "Approve or reject store redemption". REST: `GET /store`, `POST /store/redemptions/{id}/{approve, reject}`.

The **Giveaway** widget shows the prize, how to enter (with the ticket cost), entries, the latest entrants and the time
left, and on the draw spins through the entrants' names before showing the winners.

### Media requests (`Cuebot.Core/Media`, phase 5d)

Viewers on every platform request YouTube videos in chat (`!sr` with a watch, youtu.be, shorts, embed or music link,
or a bare id); `!wrongsong` takes back their latest (refunded); moderators `!skip`. Each video is looked up with
YouTube's oEmbed endpoint (title and channel, no API key or quota; private and removed videos are refused before
anyone pays). Settings (media-requests.json, with the queue and the latest 20 played): open, the three commands, cost in
Cuebot points (and currency), requests per viewer, queue size, the longest play (the player moves on after it), subscribers only,
answering in chat (place in the queue, or why it was refused) and the volume. Removing, taking back or clearing waiting
requests refunds them; what plays is not refunded when skipped.

The **Media request player** widget plays the queue with YouTube's player (video, or sound only with the now playing
bar), follows pause and volume, and reports through the widget request `cuebot_media {action, id}` when a video ends or
reaches the length limit (`ended`) or YouTube will not play it (`failed`: refunded, as owners can block playing
outside YouTube, which oEmbed does not tell). Only Cuebot's media player may send it (built-in widget elements carry
`{"builtIn": kind}`), and only for the video playing, so several players (OBS and a browser) move the queue on once. In
the editor preview it is muted and never reports. The state goes to overlays as `cuebot-media` (and `media` in the
hello).

Controlled from the "Media Requests" page (Audio: now playing, pause, skip, volume, the queue with remove and reorder,
adding a link yourself, settings), the "Add media request" and "Control media requests" sub-actions, triggers (media
requested, started playing, skipped) and REST (`GET /media`, `POST /media/requests {link, user}`,
`DELETE /media/requests/{id}`, `POST /media/{skip, pause, resume, clear, open, close}`).

### Merch, charity and seasonal (phase 5e)

- **Merch** is an alert type (`merch-latest`, `{name, amount, currency, formattedAmount, items, quantity, image}`):
  StreamElements and Streamlabs merch, Fourthwall orders, Shopify orders once paid and Ko-fi shop orders (the webhook
  adapters also send Ko-fi and Fourthwall donations as tips). It counts in the stream totals and leaderboards, has an
  alert box design (`{items}` in templates), and is in labels, goals, revenue (now tips, Super Chats, charity and
  merch), the event list, credits and the "Show alert" sub-action; emote rain, hype cup and hype boss count it like tips.
- **Charity**: Twitch charity donations are `charityCampaignDonation-latest` alerts; a campaign's start, progress and end
  go to widgets as `cuebot-charity` (`{campaignId, charity, description, logo, website, raised, target, currency,
  status}`, never queued). The **Charity campaign** widget follows a running Twitch campaign (and keeps the last one in
  the widget store), otherwise counts charity donations for a period against your goal, with the latest donation.
- **Product showcase** rotates up to six products you enter (image, name, price) with a shop line, for any shop, and
  shows who just bought merch.
- **Seasonal effects**: snow, autumn leaves, hearts, confetti, cherry blossoms, bubbles or twinkling stars over the
  overlay (canvas), with density, speed, size, wind, colours and a burst for every alert.

StreamElements' own merch product catalogue and seasonal store items need SE's private APIs; Cuebot covers them with
the widgets above instead.

The widget editor's preview loads the real stream totals and recent alerts (`session`, `recents` in the preview
answer), so these preview with live numbers.

### Overlay designer (`/design/...`, `OverlayDesigner`)

"Design…" on the Overlays page opens it (editor token). The canvas shows the live overlay with a box per layer: drag
to move (snapping to the canvas and other layers' edges and centres; Alt turns snapping off), handles to resize
(Shift keeps the shape), arrow keys to nudge (Shift: 10 px). The layer list orders layers (front first), shows/hides,
locks, groups (moved and selected together), duplicates and deletes. The properties panel sets name, position, size,
opacity, rotation, enter and exit animations (animate.css names, Cuebot's own keyframes in overlay.css) and the settings
of text, image, video and slideshow layers (uploads go through `/edit/api/assets`); widget layers link to the widget
editor. Canvas presets (1080p, 1440p, 4K, vertical) or a custom size. Undo/redo (Ctrl+Z / Ctrl+Y), Ctrl+D duplicate,
Ctrl+G group. Changes save on their own; the overlay (and OBS) reloads with them.

| Route | |
| --- | --- |
| `GET /design/{overlayId}?editor=...` | The designer page. |
| `GET` / `POST /design/api/{overlayId}` | The overlay and the element library; saving name, canvas and layers. Saving checks every layer: known types only, widget layers must use an existing library element, sizes and positions clamped, opacity 0 to 1, animation names from the list (durations 0.1 to 10 s), duplicate ids renewed, one-member groups cleared. |
| `POST .../add` `{type, elementId?}` | `text`, `image`, `video`, `slideshow` (centred, with defaults), `chatbox`, `chatbox-youtube`, or `element` (a library widget, shared rather than copied). |
| `POST .../import?name=...` | A widget zip (raw body) as a new widget layer. |
| `POST .../save-to-library` `{layerId}` | A text, image, video or slideshow layer as a library template (settings and size). |
| `POST /design/api/library/{elementId}/rename\|duplicate\|delete?overlay=...` | Library upkeep. Renaming a widget renames the layers that use it; duplicating a widget copies its code; deleting is refused while an overlay uses the element (the error names them). The reply is the given overlay's state. |

The library ("Library…" in the designer's Add menu) lists every element with the overlays using it. Widgets are
shared: one set of settings and code wherever they are placed. Templates are copied: adding one makes an independent
layer.

Static layers are drawn by the overlay page itself (no frame): text with a Google Font, colour, weight, alignment,
shadow, background and padding; images and videos (`http(s)` or `/assets/` addresses only) with fit, loop, mute and
volume; slideshows of images and videos with a time per slide and a fade.

### Widget editor (`/edit/...`, `OverlayEditor`)

Opened from the Overlays page ("Edit…") with the editor token (`overlays.editorToken` in servers.json, a secret,
created on first use); overlay links do not open it. Same network setting as overlays.

| Route | |
| --- | --- |
| `GET /edit/{overlayId}/{layerId}?editor=...` | The editor: settings form built from FIELDS (text, textarea, number, slider, checkbox, colorpicker, dropdown, image/video/sound inputs with upload, googleFont, button; `group` sections; `hidden` and `widget*` fields not shown), code panes (HTML, CSS, JS, Fields, Data, My CSS), a scaled preview of unsaved changes and Emulate buttons per platform. |
| `GET` / `POST /edit/api/{overlayId}/{layerId}?editor=...` | The widget's panes; saving them (FIELDS must be a JSON object) reloads the overlays that use the widget. |
| `POST .../preview` | A draft document, served for 10 minutes from the widget port at `/p/{id}` (the random id is the only key). |
| `POST .../request` | The preview's SE_API requests. |
| `POST /edit/api/emulate` `{platform, kind}` | Sample events (`OverlayEmulator`) converted by the real adapters (`OverlayEventBridge.Convert`) and returned to the editor; they are never broadcast to live overlays. |
| `POST /edit/api/assets?name=...` | Uploads a file (raw body, up to 200 MB) to the asset folder; returns `/assets/{sha256}.{ext}`. |

Every POST must carry the editor page's own Origin. Field buttons are sent as StreamElements' editor sends them:
`event:test` with `{listener: "widget-button", field, value}`.
- Every response has `Referrer-Policy: no-referrer` (the page also sets it), so the token in the page URL never
  reaches sites that widgets load from, and `X-Content-Type-Options: nosniff`. Only GET/HEAD.
- Each page has its own outbox: past 500 waiting messages the oldest chat message or session update is dropped;
  alerts and commands are kept up to 5000.

### Events on the overlay feed (`Cuebot.Core/Overlays/Events`)

`OverlayEventBridge` turns bot events into StreamElements-shaped messages, so widgets made for StreamElements work
with every platform:

- Combined chat (`chat.message`) becomes `message`: `{data: {time, tags, nick, userId, displayName, displayColor,
  channel, text, isAction, msgId, badges[{type, version, url, description}], emotes[{type, name, id, gif, urls, start,
  end}]}, renderedText, service}`. Twitch-style `tags` (`mod`, `subscriber`, `vip`, `badges`, `color`, ...) are filled
  for every platform. Kick's emote tokens become emote names in `text`, ranges and `renderedText`. A chatter without a
  platform colour gets a stable one of Twitch's default colours.
- `chat.messageDeleted` becomes `delete-message {msgId}`; `chat.userBanned` / `chat.userTimedOut` `delete-messages {userId}`.
- Each platform module's `IOverlayEventAdapter` (and the donation and webhook modules') turns its alerts into `*-latest` events (`{name, displayName, amount,
  message, type, providerId, avatar, _id, createdAt}` plus `tier`, `gifted`, `sender`, `bulkGifted`,
  `isCommunityGift`, `currency`... as StreamElements has them, and `service`, the platform, as on chat messages), each
  followed by the raw `event {type, provider, data}`.

| Listener | Twitch | Kick | Rumble | YouTube | Donations |
| --- | --- | --- | --- | --- | --- |
| `follower-latest` | follow | follow | follow | newSubscriber | |
| `subscriber-latest` | sub (not gift copies), resub (amount = months), prime/gift paid upgrade, subGift (recipient, `sender`, `gifted`, `isCommunityGift`), giftBomb (`bulkGifted`, amount = gifts) | subscription, resubscription, giftSubscription, massGiftSubscription | subscribe, giftSub (`bulkGifted`) | newSponsor, memberMilestone, membershipGift (`bulkGifted`), giftMembershipReceived | |
| `cheer-latest` | cheer (bits) | | | | |
| `kicks-latest` | | kicksGifted | | | |
| `tip-latest` | | | rant (dollars) | | Streamlabs, StreamElements, TipeeeStream, Pally, TreatStream |
| `superchat-latest` | | | | superChat, superSticker (`sticker`) | |
| `raid-latest` | raid | host | | | |
| `charityCampaignDonation-latest` | | | | | charity donations, DonorDrive |
| `redemption-latest` (Cuebot) | rewardRedemption | rewardRedemption | | | |
| `jewels-latest` (Cuebot) | | | | jewelsGifted | |

### Stream totals (`Cuebot.Core/Overlays/Session/SessionDataStore`, session-data.json)

Counted from the alerts above, across every platform, in StreamElements' `session.data` shape (sent in `hello` and as
`{"type":"session"}` after each change). Types are the listeners without `-latest`.

- `{type}-session-users` (everyone this session in first-seen order with their summed amount, at most 1000; cleared
  with the session), used by the credits roll. Recents also carry `currency`, `gifted`, `bulkGifted`, `sender` and
  `redemption`.
- `{type}-latest` (the last alert plus `service`), `{type}-session`, `-week`, `-month`, `-total` (`{count, amount}`),
  `{type}-count` (`{count}`, all time), `{type}-goal` (`{amount}`), `{type}-recent` (25, newest first).
- For tip, cheer, superchat, kicks, jewels and charityCampaignDonation the amount is summed (currencies are added as
  they are) and there are `{type}-{session,weekly,monthly,alltime}-top-donation` (largest single) and
  `-top-donator` (largest sum per name). For other types the amount equals the count.
- `subscriber-new-session`, `subscriber-resub-session`, `subscriber-gifted-session` (`{count}`). Gift bundles whose
  recipients arrive as their own alerts count 0; Rumble's gift bundles count every gift.
- The session resets 15 minutes after the last live platform goes offline (checked every second, and when a platform
  goes live); week (ISO week, local time) and month totals roll over by the calendar. Totals, goals and recents persist.

Badge images: Twitch's own (global and channel badges, loaded when the broadcaster connects); for Kick, Rumble,
YouTube and until Twitch's load, Cuebot's built-in icons at `/runtime/badge-{broadcaster,moderator,vip,subscriber,
founder,og,verified,staff,gifter,premium}.svg`.

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
| `GET /media`, `POST /media/requests` `{link, user}`, `DELETE /media/requests/{id}`, `POST /media/{skip, pause, resume, clear, open, close}` | media requests: the player and the queue; add a video (free; 400 with the reason when refused); remove a waiting one (refunded); control |
| `GET /giveaway`, `POST /giveaway` `{title, command, ticketCost, maxTickets, currency, subscriberLuck, subscribersOnly, seconds, winners}`, `POST /giveaway/{close, draw, redraw, cancel}` | the current giveaway; open one (409 while one runs), close entries, draw, draw one more, cancel and refund |
| `GET /store`, `POST /store/redemptions/{id}/{approve, reject}` | store items, pending and latest redemptions; approve or reject (refunds) a pending one |
| `GET /polls`, `POST /polls` `{title, choices, duration}`, `POST /polls/end` | latest poll and prediction per source; start or end a Cuebot chat poll (409 while one runs) |
| `POST /predictions` `{title, outcomes, window, currency}`, `POST /predictions/{lock, resolve, cancel}` `{winner}` | Cuebot predictions with points (409 without points or while one is open; resolve takes the winner's number from 1 or title) |
| `GET /speech`, `POST /speech/{next, pause, resume, skip}` | speech state `{enabled, paused, queueLength, current}`; `next` stops the text playing and, while paused, lets exactly one waiting text play (`stepped` says whether anything happened): pause, then call `next` to step through the queue one text at a time |
| `GET /alerts`, `POST /alerts/{pause, resume, skip, mute, unmute}` | overlay alert queue `{paused, muted}` |
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
| control_speech (status, next, pause, resume, skip), control_alerts (status, pause, resume, skip, mute, unmute) | |
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
