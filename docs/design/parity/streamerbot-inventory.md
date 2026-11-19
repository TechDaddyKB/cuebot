# Streamer.bot Feature Inventory (parity checklist for Cuebot)

> Clean-room inventory compiled **only from public sources**: docs.streamer.bot (guide, API reference, FAQ, changelogs; mirrored by the site's own `llms.txt` / `llms-full.txt` index), streamer.bot (official site). No decompiled/extracted material was consulted.
> Snapshot date: 2026-10-26. Current stable: **v1.0.7** (Aug 2026, hotfix of 1.0.5/1.0.6). Current alpha: **v1.1.0-alpha** (Oct 2026). The docs site already documents several 1.1.0-alpha features (Points, Cron, Elgato Studio / Control Center / Wave Link 3, controller input); these are tagged **[1.1α]** where known.
> Conventions: names are as the docs title them. `[CL]` = known only from a changelog entry (no API reference page yet). `[EV]` = exists as a WebSocket/C# EventType but has no trigger reference page.

Primary sources:
- Docs index: https://docs.streamer.bot/llms.txt and full text https://docs.streamer.bot/llms-full.txt
- Guide root: https://docs.streamer.bot/guide , Triggers: https://docs.streamer.bot/api/triggers , Sub-actions: https://docs.streamer.bot/api/sub-actions , C#: https://docs.streamer.bot/api/csharp , WebSocket: https://docs.streamer.bot/api/websocket , HTTP: https://docs.streamer.bot/api/http , UDP: https://docs.streamer.bot/api/servers/udp
- Changelogs: https://docs.streamer.bot/changelogs , https://docs.streamer.bot/changelogs/v1.0.0 , https://docs.streamer.bot/changelogs/v1.1.0
- Official site: https://streamer.bot

## 0. Counts (documented reference pages)

| Category | Count |
|---|---|
| Trigger reference pages | **431** (Twitch 131, YouTube 24, Kick 18, Core 37, Custom 2, OBS 9, Meld 8, Streamlabs Desktop 7, Elgato 105, other integrations 90) |
| Sub-action reference pages | **417** in index + 4 extra in full text (Clear Users From Group, Reset Credits, Reset First Words, Set Voice Control Input) = **421** (Core 81+, Twitch 95, Kick 14, YouTube 9, OBS 29, SLOBS 16, Meld 10, Elgato 90, Streamer.bot Decks/Remote 13, Speaker.bot 7, integrations 45+, settings 1+) |
| Changelog-only sub-actions `[CL]` (no page yet) | ~20 (see Appendix A) |
| C# CPH method reference pages | **202** (+ ~60 more signatures published in 1.0.0/1.1.0 changelogs) |
| C# classes / enums | 29 classes, 6 enums (EventType ~500 values) |
| WebSocket requests | **22** |
| WebSocket event sources | **~50** sources, ~500 event types |
| HTTP server endpoints | 6 (+ static file mappings) |
| Platforms | 3 current (Twitch, YouTube, Kick); Trovo **removed in 1.0.5**; TikTok not supported (3rd-party TikFinity) |
| Integrations / stream apps (distinct) | **~38** (see §3) |

---

## 1. Core concepts & application features

Sources: https://docs.streamer.bot/guide/core/actions , /guide/core/triggers , /guide/core/variables , /guide/core/commands , /guide/core/timers , /guide/core/hotkeys , /guide/core/voice-control , /guide/core/midi , /guide/core/import-export , /guide/core/backup , /guide/core/credits , /guide/core/quotes , /guide/advanced/shortcuts , /guide/advanced/switches , /faq/*

### 1.1 Actions (Actions & Queues > Actions)
- Three-pane view: Actions list (grouped) | Triggers | Sub-Actions.
- Action fields: **Name**, **Group** (collapsible UI folder), **Queue**, **Enabled**, **Random Action (RA)** (run one random sub-action; enables per-sub-action **Weight**), **Concurrent (CC)** (run all sub-actions simultaneously; ignores Delay), **Always Run** (run even if queue paused), **Exclude from Action Queue Pending/History**.
- Context menu: Add, Edit, Delete (optional confirm, re-enable in Settings), Duplicate (incl. sub-actions), Copy Action ID, Collapse All, Expand All, Add to Export, quick toggles.
- Default settings for new Actions/Commands (1.0.0).
- Undo/Redo on Actions page, toolbar button **[1.1α]**.
- Each action has unique GUID; `%actionId%`, `%actionName%`, `%runningActionId%`, `%actionQueuedAt%` args.
- Actions can be executed by: triggers, commands, timers, other actions (Run Action), C# (`RunAction`), WebSocket/HTTP/UDP `DoAction`, chat window quick actions, Stream Deck plugin, Decks, IFTTT, remote instances.

### 1.2 Sub-actions & sub-action groups/logic
- Add via right-click hierarchical menu; **Favorite** sub-actions.
- Context: Edit (double-click), Copy, Paste (also via clipboard Base64), Duplicate, Delete, Delete all, **Add Group**, Move to Group, Delete Group, Rename Group, **Enabled** toggle (disabled shown red), **Random** (group runs one random child), **Weight**, Move Up/Down/Top/Bottom (Ctrl+arrows, drag & drop).
- **Nestable groups** with **custom color** (1.0.0).
- Logic sub-actions (Core > Logic): **If / Else** (Input field supports %vars%/~globals~/inline functions, Operation, Value, Auto Type; True/False are container groups; regex match groups added as args; "Does Not Exist" check), **Switch** (cases with multiple values, first match wins, Default case), **While** (Input/Operation/Value loop; right-click "Break Running"), **Loop Break**, **Loop Continue**, **Break** (stop action), **Get Input** (parse input words), **Get Random Number**.
- Other flow: **Delay** (ms), **Run Action** (optionally wait), **Send/Wait for Local Signal**, **Send/Wait for Remote Signal**, **Comment**, **Log**.
- Help `?` button on every sub-action/trigger dialog opens docs.

### 1.3 Action queues (Actions & Queues > Queues)
- `Default` queue (non-blocking) always exists; user-created named queues.
- Queue properties: **Name**, **Blocking** (sequential execution), Paused state; columns Pending Count, Completed Count, Paused, Blocking.
- Context: Add, Pause/Resume, Blocking toggle, Clear (pending), Pause All Queues, Resume All Queues.
- Channel-point reward option **Redemption Skips Queue** (ignore blocking).
- **[1.1α]** paused-queue control: Get Action Queue State, Get Action Queue Pending Count, Force Next Action, Force Random Action, Shuffle Actions in Queue (+ C# `IsQueuePaused`, `GetQueuePendingCount`, `GetPausedActions`, `ShuffleActionsInQueue`, `ForceNextAction`, `ForceActionById`, `ForceRandomAction`).
- **Pending Actions** view (blocked/queued) and **Action History** view: columns Name, State (Completed/Cancelled/Pending), Variables count, Duration (ms), Queue, Queued At, Started At, Completed At. Context: **Inspect Variables When Queued**, **Inspect Variables After Run**, **Requeue**, Clear History. **[1.1α]** sortable columns, filter by queue/action name, Actions-per-second metric; times stored UTC.

### 1.4 Triggers (generic behaviour)
- Many triggers per action; one event can fire many actions.
- Criteria types: **Basic**, **Toggle** (checkbox filters, e.g. sub tiers), **Select** (dropdown incl. `Any`), **Range** (Min/Max inclusive; Max-only = exact; blank = all; **Always Run** flag for overlapping ranges, shown blue), **Text** (filter by text, blank = all).
- Context: Test Trigger (F5), Edit, Copy (Base64 string), Paste, Delete, Enabled, Refresh Triggers, Clear All, Enable All, Disable All, Always Run.
- **Trigger Viewer** (overview of all triggers → actions).
- **Test** trigger with custom args (+ Comment field [1.1α]); Simulated events.
- **Custom triggers** registered from C# (`RegisterCustomTrigger(label, name, categories[])`, nested categories) and fired by `TriggerCodeEvent`; generic `TriggerEvent` → "Custom" trigger.

### 1.5 Arguments & variables
- **Arguments** (argument stack, action-scoped): `%name%` substitution in most text fields; each sub-action can add/modify args; trigger populates source-specific args.
- **Global variables**: persisted vs non-persisted (temporary until restart); Get/Set Global Variable sub-actions; `~persistedGlobal~` direct syntax (persisted non-user only).
- **User global variables** (per user, per platform: Twitch/YouTube/Kick), persisted/non-persisted.
- Set Global modes (1.0.0+): **Value** (with parsing, inline functions, Auto Type), **Increment**, **Decrement**.
- **Global Variable Viewer** window (toolbar): tabs persisted / non-persisted / per-platform user globals; live updates; Add/Edit (Name, Value, Auto Type), Delete, Copy Name, Delete all, delete-selected-for-all-users.
- Generic always-available vars: `%date%` (`%date:fmt%`), `%time%` (`%time:fmt%`), `%longtime%`, `%unixtime%`, `%filedatetime%`, `%lf%`, `%actionId%`, `%actionName%`, `%runningActionId%`, `%actionQueuedAt%`, `__source` (EventType), `eventSource` (EventSource).
- **Formatting**: C# numeric (`%tipAmount:c2%`) and date/time format strings (`%time:t%`).
- **Inline functions**: `$math(expr)$` (mXparser), `$length(text)$`, `$parse(name)$` (double-parse e.g. `$parse(line%i%)$`).
- Variable names may not contain spaces (1.0.5 breaking change).
- Common variable groups documented: General, Twitch Broadcaster/Chat/Reply/User/Shared Chat Source, YouTube Broadcast/Broadcaster/User, Kick Broadcaster/Chat/User, Patreon (event id, member/tier/user attributes), Meld event, VTube model, WebSocket client/custom server.
- Twitch shared-chat args on most events: `isInSharedChat`, `isSharedChatHost`, `isFromSharedChatGuest`; Twitch chat fragments incl. `%gif.count%`, `%gif.#.text|gifId|url%` [1.1α]; `firstMessage` for Kick/YouTube [1.1α].

### 1.6 Commands manager (Commands tab)
- Platform-agnostic chat commands (Twitch, YouTube, Kick).
- Fields: **Name**, **Enabled**, **Include** (in Get Commands output), **Mode** (`Basic` / `Regex`), **Location** (`Start` / `Exact` / `Anywhere`), **Commands** (multiple aliases one per line; cooldown shared across aliases), **Group**.
- Options: **Ignore Bot Account**, **Ignore Internal Messages** (chat-window loopback), **Persist Counter**, **Persist per User Counter** (`counter` / `userCounter` args), **Case Sensitive**.
- **Sources**: Twitch Message, Twitch Whisper, Twitch Subscription Message, Twitch Re-subscription Message, YouTube Message, Kick Message.
- **Cooldowns**: Global (s), User (s); broadcaster always exempt.
- **Permissions**: Grant Type `Allow`/`Deny` + Allowed/Denied users & groups; broadcaster exempt.
- Context: Add, Edit, Delete, Rename Group, Set Group, Enabled, Reset Counters, Reset User Counters, Add to Export, Copy Command Id.
- Triggers: Command Triggered, Command Cooldown (args incl. `commandLocation` 1.0.5).
- Sub-actions: Get/Set Command State, Get/Set Command Group State, Get Commands. Many C# cooldown/counter methods (§5.5).

### 1.7 Timers / timed actions (Services > Timers)
- Fields: **Enabled**, **Repeat**, **Name**, **Interval** (s), **Random** interval (min..max), **Lines** (min chat lines; broken with multiple platforms).
- Trigger: Timed Actions. Sub-action: Set Timer State. C#: Enable/DisableTimerById, GetTimerState, SetTimerInterval.
- **Cron** service + Cron trigger **[1.1α]**.

### 1.8 Users / viewers / groups / points
- **User groups** (Settings > Groups): named groups of users (per platform), used by command permissions, credits, random-user picks, points payouts. Triggers: Group Added/Cleared/Removed/Users Added/Users Removed. Sub-actions: per-platform Add User To Group / Remove User From Group / User In Group, Clear Users From Group. C#: AddGroup, DeleteGroup, GetGroups, GroupExists, AddUserToGroup, AddUserIdToGroup, RemoveUserFromGroup, RemoveUserIdFromGroup, UserInGroup, UserIdInGroup, UsersInGroup, ClearUsersFromGroup.
- Per-user data = user global variables (persisted). Present-viewer tracking (Present Viewers triggers per platform; Add Present User; Add Random Users; GetActiveViewers WS request). First-words cache (reset interval, Reset First Words, ClearFirstWordsCache).
- No documented built-in "watch time" tracker (implemented by community via present-viewer triggers + user globals).
- **Points / currency system [1.1α]**: multiple point definitions (name/plural), payout rules (amount, interval minute-aligned, platforms, groups, only-active-viewer, max balance cap), group multipliers, per-user multipliers, leaderboard. Sub-actions: Get/Reset Points Definition, Award Points to All, Award Points to Group, Get/Set/Remove Group Multiplier, Get Points Leaderboard, Award/Check/Delete/Get/Reset User/Set/Spend/Transfer/Update Points, Get User Multiplier. C#: GetPointDefinitions, GetPointDefinition, ResetPointDefinition, GetPoints, GetPointsAndRank, AddPoints, UpdatePoints, RemovePoints, SetPoints, SpendPoints, DeletePoints, ResetUserPoints, TransferPoints, HasPoints, GetPointsForUsers, UpdateBulkPoints, GetUserMultiplier, AwardPointsToGroup, Get/Set/RemoveGroupMultiplier, AwardPointsToAll, GetLeaderboard, GetPointsByCutoff, GetPayoutRules, SetPayoutRuleState.

### 1.9 Quotes (Services > Quotes)
- Built-in quote DB (id, user, text, game capture, date); `!quote` built-in command removed in 1.0.0, replaced by sub-actions: **Add Quote**, **Delete Quote**, **Get Quote** (by id/random), **Get Quote Count**. Re-index quotes. Triggers: Quote Added, Show Quote. C#: `AddQuoteForKick` etc., QuoteData class. Example commands: https://docs.streamer.bot/examples/quotes-commands

### 1.10 Credits (Services > Credits)
- Events to record: Follows, Cheers, Subs, ReSubs, Gift Subs, Gift Bombs, Raided, Reward Redemptions, Goal Contributions, Game Updates, Pyramids, Hype Trains.
- Users Present (spoke): Editors, Moderators, VIPs, Groups (all/selected), Users. Track Top: Cheer, Channel Points Used.
- Auto Reset after N hours (default 12); manual Reset; **Reset Credits** sub-action; HTTP/WS GetCredits, TestCredits, ClearCredits.

### 1.11 Twitch extras (Platforms > Twitch)
- Channel Point Rewards manager (create/own rewards; see §2.1), Polls, Predictions, **Pyramids** (detection; Shared Chat Participation option), Sub counter, Settings.

### 1.12 Hotkeys / input
- Since 1.0.0 hotkeys are triggers: **Key Press** (any key combo), **Mouse Click**; game-controller input (button/axis/hat via SDL2; custom gamecontrollerdb) **[1.1α]**. Output: **Keyboard Press** sub-action.

### 1.13 MIDI
- MIDI In / MIDI Out devices (Name, Device, Auto Open on Startup, Reopen on Close, Retry Interval; context Add/Edit/Delete/Auto Open/Reopen/Open).
- MIDI In events: Enabled, Name, Command (type), Channel / Ignore Channel, Data / Ignore Data, Raw Message, Ignore NoteOff; auto-detect by pressing control. Trigger: MIDI Message.
- MIDI Out sub-actions: Note On, Control Change, Generic Event (NoteOn, NoteOff, ControlChange, PatchChange, ChannelAfterTouch, PitchWheelChange), Midi Sysex Event `[CL 1.1α]`. USB-removal monitoring.

### 1.14 Voice control (speech-to-text)
- Windows Speech Recognition engine (Whisper.net-based voice control **[1.1α]**).
- Commands: Name, Enabled, Command phrase, Location (`Exact`/`Start`/`Anywhere`), Stop After, Override Global confidence, Confidence Threshold (0-100).
- Settings: Start/Stop Listening, Auto Start Listen, Locale, global Confidence Threshold, Log Speech to Text Dictation, Audio Input Device. Live dictation Log view.
- Triggers: Voice Control Command, Dictation. Sub-actions: Set Voice Control Command, Set Voice Control Command State, Set Voice Control Input.

### 1.15 Other services
- **File/Folder Watcher** (Services) → Changed/Created/Deleted/Renamed triggers.
- **File Tails** (Settings > File Tails) → File Tail Changed trigger; Set File Tail State sub-action.
- **Process Watcher** (Services) → Process Started/Stopped triggers.
- **Clipboard** monitor → Clipboard Changed trigger (also headless).
- **Toast notifications** (sub-action) + Toast Activation trigger (by id).
- **Sounds**: Play Sound (file, volume, wait), Play Sound From Folder (random), Stop Sound Playback, Stop All Sound Playback; output device settings (Settings > Output).
- **Pronouns** (alejo.io) integration: Add Pronouns for User sub-action; GetUserPronouns WS request.
- Emote providers: Twitch, YouTube, 7TV (incl. zero-width), BTTV, FFZ; emote added/removed triggers.

### 1.16 Import / Export
- Exportable: Actions & Sub-Actions, Triggers, Action Queues, Commands, Timed Actions, WebSocket Clients, WebSocket Servers (keeps C# code, groups, queues).
- Import string = compressed/encoded text ("UUEncoded" per docs; starts with `U0JBRR...` style Base64 header); drag & drop file; per-item **Include in Import** and **Overwrite**; warning when import contains Execute C# Code; "import too old" handling.
- Export dialog: Name, Author, Version, Description, Clear, Export to Clipboard, Export to File; "Add to Export" context item everywhere.
- Copy/paste of individual triggers / sub-actions / actions as Base64 clipboard strings.
- Community extensions site: https://extensions.streamer.bot

### 1.17 Backups, settings, app shell
- Automatic local backup on every start and before updates (`backups/` folder, `[date]-[time]-[version].zip` of `data/`); manual backup; configurable backup folder; restore by copying into `data/`; version rollback via old builds.
- **Cloud backups** (Patreon supporter perk; optional `auth.db`; restore from other instances).
- Auto-save every 5 min; Save (Ctrl+S) with toast.
- Settings areas seen in docs: General, Groups, Backups, Output, Webhooks (website), Timed Actions, Notifications, Delete-confirmation re-enable, Minimize to Tray on Close, Dark/Light mode (1.0.0 WPF UI), release channel (stable/beta/alpha), built-in updater ("Check for Updates").
- Tray icon (internal handler [1.1α]); **headless mode**; IPC via named pipes (1.0.3).
- **Startup switches**: `/nologin`, `/nowebview2`, `/resetwindow`, `/headless`, `/shutdown`.
- Logs: log folder (Ctrl+Alt+L), Log sub-action, C# LogInfo/LogWarn/LogError/LogDebug/LogVerbose.
- **Streamer.bot account login** (bottom-left) → website integration (Kick webhooks, Decks, IFTTT, Ko-fi, Patreon, Shopify, Fourthwall, Throne, Custom Webhooks, Cloud Backups, Remote instances, supporter perks).
- **Keyboard shortcuts**: F1 docs, Ctrl+S save, Ctrl+Alt+I import, Ctrl+Alt+E export, Ctrl+Alt+C chat, Ctrl+Alt+V globals, Ctrl+Alt+L log folder, Ctrl+V paste triggers/actions/groups; per-view Delete, Ctrl+D duplicate, Ctrl+C copy, Ctrl+O toggle enabled, Ctrl+R rename group, Ctrl+Up/Down/Home/End move, F5 test trigger, Ctrl+* / Ctrl+- expand/collapse, Ctrl+A add reward.
- Linux (Wine: install script, Bottles, Lutris, UIAutomationCore fix) and macOS (Wine-based) support notes.

### 1.18 Companion apps / remote clients
- **Streamer.bot Chat** (built-in WebView2 window + https://chat.streamer.bot web): multi-stream tabs/combined (Twitch/YouTube/Kick), send-to-selected/all, send as bot, slash commands (`/action`, multi-platform `/title`, ...), `!` command menu, `:` emote autocomplete (Twitch, YouTube, 7TV, FFZ, BTTV), Quick Actions (Global/User/Message/Event), highlights, OBS browser dock (`/feed/chat`, `/feed/events`) via WebSocket server, chat overlay (experimental), CSS data-attributes. Trigger-side: ChatWindowAction event.
- **Streamer.bot Decks** (web stream deck, via website, Decks 2.0) + sub-actions/C# (§3).
- **Elgato Stream Deck plugin** (official) — StreamDeck Action/Connected/Disconnected/Info events; set title/state/background/value, show alert/OK.
- **HTML Overlay** desktop app (Splat, Show Image, Emote Rain; uses @streamerbot/client).
- **@streamerbot/client** JS/TS library; VoiceAttack plugin (UDP); AutoHotKey examples (UDP).
- Remote instances (Streamer.bot Remote): Remote Instance Connected/Disconnected/Trigger, Send Remote Trigger/Signal, Wait For Remote Signal.

### 1.19 UI pages (navigation)
- Actions & Queues: Actions, Queues, Pending Actions, Action History
- Commands
- Platforms: Twitch (Accounts, Channel Point Rewards, Polls, Predictions, Pyramids, Settings), YouTube (Accounts, Broadcasts, Stream Info, Settings), Kick (Accounts, Rewards, Settings)
- Stream Apps: OBS, Streamlabs Desktop, Meld Studio, PolyPop
- Integrations: Crowd Control, DonorDrive, Elgato (Camera Hub, Control Center, Stream Deck, Studio, Wave Link), HypeRate.io, Lumia Stream, Mtion, OBSBOT [1.1α], Pally.gg, Pulsoid, Speaker.bot, StreamElements, Streamer.bot Website/Remote, StreamFog, Streamlabs, StreamLoots, TipeeeStream, T.I.T.S., TreatStream, VoiceMod, VTube Studio, Pronouns
- Servers/Clients: WebSocket Server, WebSocket Servers (custom), WebSocket Clients, HTTP Server, UDP Server
- Services: Timers, Quotes, Credits, File/Folder Watcher, Process Watcher, Points [1.1α], Cron [1.1α]
- MIDI (In / Out), Voice Control (Commands, Settings, Log)
- Settings (General, Groups, Backups, Output, File Tails, ...), Global Variables window, Import, Export, Chat window, Events window

---

## 2. Platforms

Sources: https://docs.streamer.bot/guide/platforms/twitch , /guide/platforms/youtube , /guide/platforms/kick , https://docs.streamer.bot/api/triggers/{twitch,youtube,kick} , https://docs.streamer.bot/api/sub-actions/{twitch,youtube,kick} , /faq/platform-support

Supported: **Twitch**, **YouTube**, **Kick**. **Trovo** existed (0.2.x–1.0.4; sub-actions, triggers, C# TrovoGetBroadcaster/GetBot/TimeoutUser/BanUser/UnbanUser/SetTitle, SendTrovoMessage) and was **removed in 1.0.5**. **TikTok**: not supported (only via third-party TikFinity).

### 2.1 Twitch
**Auth/accounts**: Broadcaster account (required; OAuth Login; auto-connect; EventSub websocket; chat via EventSub since 1.0.5, IRC removed; PubSub removed in 1.0.0); optional Bot account (OAuth; limited scope: send chat / whispers); experimental onboarded accounts. Connection triggers for broadcaster chat/EventSub and bot chat.
**Channel Point Rewards**: Title, Enabled, Paused, Cost, User Input Required, Prompt, Background Color, Redemption Skips Queue, Max per stream, Max per user per stream, Global Cooldown, Persist Counter, Persist per User Counter, Group, Sub Only; Delete from SB vs from Twitch (owned rewards); Duplicate; Copy Reward ID; counters `%counter%`/`%userCounter%`.
**Settings**: first-words reset, present viewers interval, pyramid options, sub counter.
**Triggers**
  _(131 pages)_
  - *ads*: Ad Mid Roll, Ad Run, Upcoming Ad
  - *channel-goal*: Goal Begin, Goal End, Goal Progress
  - *channel-power-ups*: Custom Power Up Redemption
  - *channel-reward*: Automatic Reward Redemption, Reward Redemption, Reward Redemption Updated
  - *channel*: Follow, Stream Offline, Stream Online, Viewer Count Update
  - *charity*: Completed, Donation, Progress, Started
  - *chat*: Bits Badge Tier, Bot Whispers, Cheer, First Words, Message, Modiversary, Watch Streak, Whispers
  - *chat/mode*: Emote Mode Off, Emote Mode On, Follower Mode Changed, Follower Mode Off, Follower Mode On, Slow Mode Changed, Slow Mode Off, Slow Mode On, Subscriber Mode Off, Subscriber Mode On, Unique Mode Off, Unique Mode On
  - *community-goal*: Contribution, Ended
  - *connections*: Bot Chat Connected, Bot Chat Disconnected, Broadcaster Authenticated, Broadcaster Chat Connected, Broadcaster Chat Disconnected, Broadcaster Eventsub Connected, Broadcaster Eventsub Disconnected, Broadcaster Pubsub Connected, Broadcaster Pubsub Disconnected
  - *emotes*: BetterTTV Emote Added, BetterTTV Emote Removed, SevenTV Emote Added, SevenTV Emote Removed
  - *general*: Announcement, Present Viewers, Shoutout Received, Stream Update
  - *guest-star*: Guest Update, Session Begin, Session End, Settings Update
  - *hype-train*: End, Level Up, Start, Update
  - *moderation*: AutoMod Message Held, AutoMod Message Updated, Blocked Terms Added, Blocked Terms Deleted, Chat Cleared, Chat Message Deleted, Moderator Added, Moderator Removed, Permitted Terms Added, Permitted Terms Deleted, Shield Mode Begin, Shield Mode End, Shoutout Created, Suspicious User Update, Unban Request Approved, Unban Request Created, Unban Request Denied, User Banned, User Timed Out, User Unbanned, User Untimed Out, VIP Added, VIP Removed, Warned User, Warning Acknowledged
  - *polls*: Poll Archived, Poll Completed, Poll Created, Poll Terminated, Poll Updated
  - *predictions*: Prediction Canceled, Prediction Completed, Prediction Created, Prediction Locked, Prediction Updated
  - *pyramid*: Broken, Success
  - *raid*: Cancelled, Raid, Send, Start
  - *shared-chat*: Announcement, Community Sub Gift, Gift Paid Upgrade, Message Deleted, Modiversary, Pay It Forward, Prime Paid Upgrade, Resub, Session Begin, Session End, Session Update, Sub, Sub Gift, User Banned, User Timed Out, User Unbanned, User Untimed Out
  - *subscriptions*: Gift Bomb, Gift Paid Upgrade, Gift Subscription, Pay It Forward, Prime Paid Upgrade, Resubscription, Sub Counter Rollover, Subscription

**Sub-actions**
  _(95 pages)_
  - *ads*: Get Ad Schedule, Get Pre Roll Free Time, Run Commercial, Snooze Ad
  - *channel*: Add Viewer Count, Create Clip, Create Stream Marker, Set Channel Branded Content, Set Channel Game, Set Channel Title
  - *charity*: Get Latest Charity Campaign
  - *chat*: Clear Chat Messages, Get Pinned Chat Message, Pin Chat Message, Reply To Message, Send Announcement To Channel, Send Message To Channel, Unpin Chat Message, Update Pinned Chat Message Duration
  - *followers*: Get Follow Age Info For Target, Get Follower Count, Get Latest Follower
  - *guest-star*: Assign Guest Star Slot, Create Guest Star Session, Delete Guest Star Invite, Delete Guest Star Slot, End Guest Star Session, Get Channel Guest Star Settings, Get Guest Star Invites, Get Guest Star Session, Send Guest Star Invite, Update Channel Guest Star Settings, Update Guest Star Slot, Update Guest Star Slot Settings
  - *moderation*: Add Moderator, Add VIP, Ban User, Remove Moderator, Remove VIP, Send Shoutout, Timeout User, Unban User, UnTimeout User, Warn User
  - *moderation/chat-modes*: Emote Only, Follow Mode, Slow Mode, Subscriber Only
  - *moderation/shield*: Get Shield Mode Status, Update Shield Mode Status
  - *polls*: Archive Active Poll, Create Poll, Get Active Poll, Terminate Active Poll
  - *predictions*: Cancel Active Prediction, Create Prediction, Get Active Prediction, Lock Active Prediction, Resolve Last Prediction, Resolve Last Prediction By Title
  - *raid*: Cancel Raid, Start Raid
  - *rewards*: Configure Rewards, Get Reward Info, Reset Reward Counter, Reset Reward User Counters, Set Background Color, Set Cost, Set Enabled State, Set Global Cooldown, Set Group Enabled State, Set Group Paused State, Set Max Per Stream, Set Max Per User Per Stream, Set Paused State, Set Prompt, Set Title, Update, Update Redemption Status
  - *sub-counter*: Get Twitch Sub Counter Count, Set Twitch Sub Counter Count
  - *subscribers*: Get Latest Subscriber, Get Subscriber Count
  - *tags*: Add Channel Tag, Clear Channel Tags, Remove Channel Tag, Set Channel Tags
  - *user-groups*: Add User To Group, Remove User From Group, User In Group
  - *user*: Add Broadcaster Information, Add Present User, Add Random Users, Get Team Info For Target, Get User Info For Target

- `[CL]` Get Clip Download Urls (1.0.3).
- `[EV]` event types without trigger pages: CoinCheer, HypeChat, HypeChatLevel, RewardCreated/Updated/Deleted, SharedChatRaid, SuspiciousUserMessage, StreamUpdateGameOnConnect, GuestStarSlotUpdate, BotEventSubConnected/Disconnected, PowerUpRedemption.
- C# (documented): SendMessage, SendWhisper, TwitchAnnounce, TwitchReplyToMessage, TwitchSendAndPinMessage, TwitchPin/Unpin/GetPinnedMessage, TwitchUpdatePinnedMessageDuration, TwitchClearChatMessages, TwitchDeleteChatMessage; Enable/Disable/PauseReward, TwitchGetRewards, TwitchRedemptionFulfill/Cancel, reward counters (Get/Reset reward & user counters), UpdateRewardMaxPerStream/PerUserPerStream, TwitchGetChannelPointsUsedByUserId; CreateClip, CreateStreamMarker, GetClips/GetAllClips/GetClipsForGame/GetClipsForUser(ById), TwitchGetClipDownloadUrls [CL]; TwitchAddBlockedTerm/RemoveBlockedTerm/GetBlockedTerms, Approve/DenyAutoHeldMessage, suspicious-user status add/remove, TwitchWarnUser; TwitchGetBroadcaster, TwitchGetBot, TwitchGetExtendedUserInfoById, TwitchGetMods, TwitchGetVips; Twitch user vars (Get/Set/Unset/Increment...ById).

### 2.2 YouTube
**Auth/accounts**: Broadcaster (Google OAuth; brand accounts via owning account; Auto Connect) + optional Bot account. Uses YouTube **PushAPI** for near-real-time chat (1.0.0); quota awareness (FAQ "YouTube Quota"). Broadcast monitoring (multiple broadcasts; latest monitored), Stream Info page (Title, Description, Privacy), Copy Dashboard Link [1.1α]. Settings: First Words reset cache (hours), Present Viewers interval (min).
**Triggers**
  _(24 pages)_
  - *broadcast*: Added, Ended, Monitoring Ended, Monitoring Started, Removed, Started, Statistics Updated, Updated
  - *chat*: First Words, Jewels Gifted, Message, Super Chat, Super Sticker
  - *general*: New Sponsor, New Subscriber, Present Viewers, Sponsor Only Ended, Sponsor Only Started, User Banned, User Timed Out
  - *membership*: Gift, Gift Membership Received, Member Milestone
  - *polls*: Poll Closed

**Sub-actions**
  _(9 pages)_
  - *moderation*: Ban User, Timeout User
  - Send Message To Channel, Set Description, Set Title, Set Title And Description
  - *user-groups*: Add User To Group, Remove User From Group, User In Group

- `[CL]` Create Poll, End Poll (1.0.0); Add YouTube Broadcaster Information (1.0.0).
- `[EV]` PollStarted, PollUpdated, MessageDeleted, BetterTTV/SevenTV emote added/removed.
- C#: SendYouTubeMessage(message, useBot, fallback, broadcastId), SendYouTubeMessageToLatestMonitored, YouTubeGetBroadcaster/GetBot, YouTubeGetLatestMonitoredBroadcast, YouTubeGetMonitoredBroadcasts, YouTubeSetTitle/SetDescription/SetMetaData/SetCategory/SetPrivacy, YouTubeAdd/Remove/ClearTags, YouTubeBan/TimeoutUserById/ByName, YouTube user vars (Increment...).

### 2.3 Kick
**Auth/accounts**: Broadcaster Kick OAuth in app **and** link Kick on streamer.bot website (events arrive as Kick webhooks forwarded through the Streamer.bot website integration; requires being logged into SB account); bot account supported for sending (useBot/fallback). Kick Channel Rewards manager (1.0.3; refresh, groups, persist counters). Kick Message is a command source.
**Triggers**
  _(18 pages)_
  - *channel-reward*: Reward Redemption
  - *channel*: Follow, Stream Offline, Stream Online, Viewer Count Update
  - *chat*: First Words, Message
  - *emotes*: SevenTV Emote Added, SevenTV Emote Removed
  - *general*: Channel Update, Present Viewers
  - *kicks*: Gifted
  - *moderation*: User Banned, User Timed Out
  - *subscriptions*: Gift Subscription, Mass Gift Subscription, Resubscription, Subscription

**Sub-actions**
  _(14 pages)_
  - *channel*: Set Channel Category, Set Channel Title
  - *chat*: Reply To Message, Send Message To Channel
  - *moderation*: Ban User, Timeout User, Unban User, UnTimeout User
  - *user-groups*: Add User To Group, Remove User From Group, User In Group
  - *user*: Add Broadcaster Information, Add Random Users, Get User Info For Target

- `[CL]` Kick Channel Reward sub-actions (1.0.3; create/update/enable/pause etc.).
- `[EV]` BroadcasterAuthenticated, BroadcasterChatConnected/Disconnected.
- C#: SendKickMessage, KickReplyToMessage, KickDeleteChatMessage, KickGetBroadcaster/GetBot, KickSetTitle, KickSetCategory, KickTimeoutUser, KickBanUser, KickUnbanUser, AddQuoteForKick, Kick user vars (Get/Set/Unset/Increment/IncrementOrCreate ...ById).

---

## 3. Integrations (stream apps & services)

Sources: https://docs.streamer.bot/guide/stream-apps/* , https://docs.streamer.bot/guide/integrations/* , https://docs.streamer.bot/api/triggers/integrations , https://docs.streamer.bot/api/sub-actions/integrations , https://docs.streamer.bot/api/triggers/elgato , https://docs.streamer.bot/api/sub-actions/elgato

Connection patterns: multi-connection list (OBS/SLOBS/Meld: Name, Host, Port, Password/Token, Auto Connect on Startup, Reconnect on Disconnect, Retry Interval; context Default-for-imports, Force (temporary override all), Update All Actions To..., Connect; Status panel: version, current scene, stream/record status, sources/layers) vs single connection (Address/Port/Token/API key/OAuth, Auto Connect, Auto Reconnect) vs website-webhook (Ko-fi, Patreon, Shopify, Fourthwall, Throne, Custom Webhooks: create webhook on streamer.bot website, paste URL into service, paste verify secret).

### 3.1 OBS Studio (obs-websocket v5 default port 4455; v4.9 compat 4444)
**Triggers**
  _(9 pages)_
  - Connected, Disconnected, Event, Recording Started, Recording Stopped, Scene Changed, Streaming Started, Streaming Stopped, Vendor Event

**Sub-actions**
  _(29 pages)_
  - Create Record Chapter, Get Current Scene, Get Status, Raw, Recording, Set Active Scene, Streaming, Take Screenshot
  - *filters*: Set State Of A Random Filter
  - *groups*: Set Random Group Source Visible
  - *replay*: Set Replay Buffer State
  - *scenes*: Get Scene Item Properties, Hide Scenes Sources, Set Random Scene Source Visible, Set Scene Filter State
  - *sources*: Flip Source, Hide Groups Sources, Hide Sources Filters, Rotate Source, Set Browser Source URL, Set Color Source Color, Set GDI Text, Set Image Source File, Set Media Source File, Set Media State, Set Source Audio Track State, Set Source Filter State, Set Source Mute State, Set Source Visibility State

- Sub-actions mention OBS Raw (raw obs-websocket request JSON, batch), Set Browser Source URL, Set GDI Text, Set Media/Image file, Media State (play/pause/restart/stop/next/prev), Replay Buffer (start/stop/save), Recording (start/stop/pause/resume/toggle), Streaming (start/stop/toggle), Create Record Chapter, Take Screenshot, filters, scene items, audio tracks.
- C# (documented): ObsShowFilter, ObsHideFilter, ObsToggleFilter, ObsSetFilterState, ObsSetRandomFilterState, ObsIsFilterEnabled, ObsHideScenesFilters, ObsHideSourcesFilters, ObsCreateRecordChapter, ObsSendRaw (example), ObsSetColorSourceColor (changelog). Many more Obs* methods exist but are not yet documented.

### 3.2 Streamlabs Desktop (Remote Control API; port 59650 + token; output type Both/Horizontal/Vertical)
**Triggers**
  _(7 pages)_
  - Connected, Disconnected, Recording Started, Recording Stopped, Scene Changed, Streaming Started, Streaming Stopped

**Sub-actions**
  _(16 pages)_
  - *filters*: Set State Of A Random Filter
  - *groups*: Hide Groups Sources, Set Random Group Source Visible
  - Recording, Streaming
  - *scenes*: Get Current Scene, Set Active Scene, Set Scene Filter State
  - *sources*: Flip Source, Hide Sources Filters, Rotate Source, Set Browser Source URL, Set GDI Text, Set Source Filter State, Set Source Mute State, Set Source Visibility State


### 3.3 Meld Studio (WebChannel/remote connections; Host 127.0.0.1)
**Triggers**
  _(8 pages)_
  - Connected, Disconnected, Event, Recording Started, Recording Stopped, Scene Changed, Streaming Started, Streaming Stopped

**Sub-actions**
  _(10 pages)_
  - *effects*: Set Effect Enabled State
  - Get Current Scene, Get Status, Recording, Show Scene, Streaming, Take Screenshot
  - *layers*: Set Layer Visibility State
  - *tracks*: Set Layer Track Muted State, Set Track Muted State

- `[CL]` Set Browser Layer URL, Take Vertical Screenshot (1.0.0). `[EV]` LayerVisibilityChanged, EffectEnabledStateChanged, TrackMonitoringStateChanged, TrackMutedStateChanged.
- C#: MeldStudioConnect/Disconnect/IsConnected/GetConnectionByName, MeldStudioShowScene(ByName), MeldStudioShow/HideLayerByName.

### 3.4 PolyPop (community WebSocket plugin; 127.0.0.1:9652 endpoint `/`)
**Sub-actions**
  _(1 pages)_
  - Trigger Alert


### 3.5 VTube Studio (API, localhost:8001; approve plugin in VTS)
**Triggers**
  _(11 pages)_
  - Background Changed, Connected, Disconnected, Hotkey Triggered, Item Event, Model Animation, Model Clicked, Model Configuration Changed, Model Loaded, Model Unloaded, Tracking Status Changed

**Sub-actions**
  _(10 pages)_
  - Color Tint, Get Current Model, Load Model, Load Model By Name, Move Model, Reset All Color Tints, Send Raw Request, Set Expression State, Trigger Hotkey, Trigger Hotkey By Name

- C# class VTSModelPosition.

### 3.6 Voicemod (v3.12+ required; Set Censor State removed in 1.0.0 for v3)
**Triggers**
  _(4 pages)_
  - Connected, Disconnected, Soundboard Changed, Voice Loaded

**Sub-actions**
  _(12 pages)_
  - Get Current Voice, Play Sound, Select Random Voice, Select Voice, Select Voice By ID, Select Voice By Name, Set Background Effect State, Set Censor State, Set Hear My Voice State, Set Mute State, Set Voice Changer State, Stop All Sounds


### 3.7 Speaker.bot — see §6
**Triggers**
  _(2 pages)_
  - Connected, Disconnected

**Sub-actions**
  _(7 pages)_
  - Activate VoiceGate Profile, Clear Pending, Set Enabled State, Set Paused State, Speak, Speak (UDP), Stop Speaking


### 3.8 Elgato
- **Stream Deck** (official SB plugin; list of connected Stream Deck apps; events StreamDeck Action/Connected/Disconnected/Info, Elgato StreamDeckMcp Connected/Disconnected)
**Sub-actions**
  _(7 pages)_
  - Set Background, Set State, Set Title, Set Value, Show Alert, Show Ok, Toggle State

- **Camera Hub** (webcams incl. Facecam/EpocCam, Prompter)
**Triggers**
  _(68 pages)_
  - Connected, Disconnected
  - *prompter*: Auto Scroll Chapter Disabled, Auto Scroll Chapter Enabled, Auto Scroll Disabled, Auto Scroll Enabled, Background Color Changed, Brightness Changed, Channels Changed, Connected, Contrast Changed, Crosshair Color Changed, Crosshair Disabled, Crosshair Enabled, Crosshair Image Changed, Disconnected, Font Changed, Font Color Changed, Font Size Changed, Horizontal Margin Changed, Line Spacing Changed, Mode Changed, Opacity Changed, Scroll Speed Changed, Selected Channel Changed, Selected Chapter Changed, Selected Script Changed, Vertical Margin Changed
  - *webcam*: Activated, Anti Flicker Changed, AR Lens Changed, Auto Exposure Disabled, Auto Exposure Enabled, Auto Focus Disabled, Auto Focus Enabled, Auto White Balance Disabled, Auto White Balance Enabled, Bitrate Changed, Brightness Changed, Contrast Changed, Deactivated, Device Orientation Changed, Exposure Auto Lock Disabled, Exposure Auto Lock Enabled, Flash Disabled, Flash Enabled, Flipped, Focus Changed, ISO Changed, Lens Changed, Live ISO Changed, Live Shutter Speed Changed, Live White Balance Changed, Live White Balance Tint Changed, Mirrored, Noise Reduction Disabled, Noise Reduction Enabled, Overscan Changed, Pan Changed, Saturation Changed, Selected Webcam Changed, Sharpness Changed, Shutter Speed Changed, Snapshot Taken, Tilt Changed, White Balance Changed, White Balance Tint Changed, Zoom Changed

**Sub-actions**
  _(34 pages)_
  - *epoccam*: Set AR Lens, Set Bitrate, Set Camera, Set Flash Mode
  - *prompter*: Fast Forward Chapter, Get Selected Script, Rewind Chapter, Set Auto Scroll State, Set Brightness, Set Crosshair, Set Crosshair Image, Set Crosshair Images, Set Font, Set Font Size, Set Horizontal Margin, Set Line Spacing, Set Mode, Set Opacity, Set Scripts, Set Vertical Margin
  - *webcam*: Set Anti Flicker, Set Auto White Balance, Set Brightness, Set Contrast, Set Dynamic Range, Set Focus, Set Noise Reduction, Set Pan, Set Saturation, Set Sharpness, Set Tilt, Set White Balance, Set White Balance Tint, Set Zoom

- **Control Center** (Key Lights etc.) **[1.1α]**
**Triggers**
  _(9 pages)_
  - Brightness Changed, Device Added, Device Configuration Changed, Device Power State Changed, Device Powered Off, Device Powered On, Device Removed, Device Renamed, Temperature Changed

**Sub-actions**
  _(3 pages)_
  - *brightness*: Set Brightness
  - *power*: Set Power State
  - *temperature*: Set Temperature

- **Elgato Studio** **[1.1α]**
**Triggers**
  _(9 pages)_
  - Connected, Disconnected, Level Changed, Preview Fullscreen Changed, Preview Volume Changed, Preview Volume Mute Changed, Recording Changed, Recording Finished, Snapshot Taken

**Sub-actions**
  _(9 pages)_
  - Open Storage Location, Take Snapshot
  - *preview*: Get Preview Info, Set Preview Fullscreen State, Set Preview Mute State, Set Preview Volume
  - *recording*: Get Last Recording Info, Get Recording Info, Set Recording State

- **Wave Link 3.x** **[1.1α]**
**Triggers**
  _(19 pages)_
  - *channels*: Channel Effects Changed, Channel Mute Changed, Channel Updated, Channel Volume Changed, Channels Changed
  - Connected, Disconnected
  - *inputs*: DSP Effects Changed, Input Device Updated, Input Devices Changed, Input Gain Changed
  - *mixes*: Mix Updated, Mixes Changed
  - *outputs*: Main Output Device Changed, Output Device Updated, Output Mix Changed
  - *system*: Create Profile Requested, Focused App Changed, Level Meter Updated

**Sub-actions**
  _(21 pages)_
  - *channels*: Add Application To Channel, Adjust Channel Volume, Get Channel Information, Mute Channel, Set Channel Effect State, Set Channel Mix Mute, Set Channel Volume
  - *inputs*: Get Input Device Information, Mute Input Device, Set Input Clipguard State, Set Input Gain, Set Input Gain Lock State, Set Input Low Cut Filter, Set Mic/PC Balance
  - *mixes*: Adjust Mix Volume, Get Mix Information, Set Mix Volume
  - *outputs*: Add Output To Device Mix, Get Selected Output Device, Set Monitored Mix, Set Output Volume

- **Wave Link (1.x/2.x, legacy)** — events (no trigger pages) `[EV]`: Connected, Disconnected, FilterAdded, FilterBypassStateChanged, FilterChanged, FilterDeleted, InputLevelMeterChanged, InputMuteChanged, InputNameChanged, InputVolumeChanged, MicrophoneBalanceChanged, MicrophoneGainChanged, MicrophoneMuteChanged, MicrophoneOutputVolumeChanged, MicrophoneSettingChanged, OutputLevelMeterChanged, OutputMuteChanged, OutputSwitched, OutputVolumeChanged, SelectedOutputChanged.
**Sub-actions**
  _(16 pages)_
  - Get Filter State, Get Input Information, Get Microphone Information, Get Output Volumes, Get Selected Output, Mute Input, Mute Microphone, Mute Output, Set Filter Bypass State, Set Filter State, Set Input Volume, Set Microphone Balance, Set Microphone Gain, Set Microphone Output Volume, Set Output Monitor Device, Set Output Volume


### 3.9 Donation / commerce / alert services
- **Streamlabs** (Socket API token)
**Triggers**
  _(5 pages)_
  - Charity Donation, Connected, Disconnected, Donation, Merchandise

- `[CL]` Streamlabs Spin Wheel sub-action (1.0.0). `[EV]` Authenticated.

- **StreamElements** (OAuth)
**Triggers**
  _(4 pages)_
  - Connected, Disconnected, Merch, Tip

- **TipeeeStream**
**Triggers**
  _(1 pages)_
  - Donation

- **TreatStream** (`[EV]` Authenticated/Connected/Disconnected)
**Triggers**
  _(1 pages)_
  - Treat

- **DonorDrive** (multiple services; charity provider or custom API endpoint)
**Triggers**
  _(3 pages)_
  - Donation, Incentive, Profile Updated

- **Pally.gg** (API key)
**Triggers**
  _(3 pages)_
  - Campaign Tip, Connected, Disconnected

- **StreamLoots** (Alerts Widget URL; `[EV]` Reaction)
**Triggers**
  _(3 pages)_
  - Card Redeemed, Pack Gifted, Pack Purchased

- **Ko-fi** (website webhook + verification token)
**Triggers**
  _(5 pages)_
  - Commission, Donation, Resubscription, Shop Order, Subscription

- **Patreon** (website webhook + secret)
**Triggers**
  _(5 pages)_
  - Follow Created, Follow Deleted, Pledge Created, Pledge Deleted, Pledge Updated

- **Shopify** (website webhook)
**Triggers**
  _(2 pages)_
  - Order Created, Order Paid

- **Fourthwall** (website webhook)
**Triggers**
  _(13 pages)_
  - Donation, Gift Draw Ended, Gift Draw Started, Gift Purchase, Newsletter Subscribed, Order Placed, Order Updated, Product Created, Product Updated, Subscription Changed, Subscription Expired, Subscription Purchased, Thank You Sent

- **Throne** (website webhook)
**Triggers**
  _(3 pages)_
  - Contribution Purchased, Gift Crowdfunded, Gift Purchased

- **Custom Webhooks** (supporter perk, website) — event `Streamer.bot.CustomWebook` (no trigger page yet) `[EV]`.

### 3.10 Interactive / game / hardware
- **Crowd Control** (Overlay URL; auto connect/reconnect)
**Triggers**
  _(9 pages)_
  - Coin Exchange, Effect Failure, Effect Request, Effect Success, Game Session End, Game Session Start, Timed Effect Ended, Timed Effect Started, Timed Effect Update

- **T.I.T.S.** (Twitch Integrated Throwing System)
**Triggers**
  _(8 pages)_
  - *connections*: Connected, Events Connected, Events Disconnected, Websocket Connected, Websocket Disconnected
  - Item Hit, Trigger Activated, Trigger Ended

**Sub-actions**
  _(6 pages)_
  - Activate Trigger, Activate Trigger By Name, Throw Item, Throw Item By Name, Throw Items, Throw Items By Name

- **HypeRate.io** (ID; `internal-testing` for fake data)
**Triggers**
  _(4 pages)_
  - Connected, Disconnected, Heart Rate Pulse, Twitch Clip Created

- **Pulsoid** (OAuth)
**Triggers**
  _(1 pages)_
  - Heart Rate Pulse

- **StreamFog** (Remote Key) — two sub-action doc trees exist (old + new)
**Sub-actions**
  _(8 pages)_
  - *backgrounds*: Play Video Background, Play Video Background By Name
  - *lenses*: Activate Lens, Activate Lens By Name, Deactivate Lens
  - *outfits*: Activate Outfit, Activate Outfit By Name, Deactivate Outfit

**Sub-actions**
  _(6 pages)_
  - *lenses*: Activate Lens, Activate Lens By Name, Deactivate Lens
  - *outfits*: Activate Outfit, Activate Outfit By Name, Reset Look

- **Lumia Stream** (Developer API token; smart lights — covers LIFX/Hue indirectly; no native LIFX/Hue integration)
**Sub-actions**
  _(3 pages)_
  - Send Command, Set Color, Set To Default

- **Mtion**
**Sub-actions**
  _(2 pages)_
  - Get Active Clubhouse, Send Trigger

- **OBSBOT Center** cameras **[1.1α]** — events Obsbot Connected, Disconnected, DeviceConnected, DeviceDisconnected, DeviceSleepStateChanged; sub-actions WIP (not yet documented).
- **Discord** (webhook URL) — sub-action Basic Webhook; C# DiscordPostTextToWebhook.
**Sub-actions**
  _(1 pages)_
  - Basic Webhook

- **IFTTT** (website; IFTTT actions *Execute Action* (`iftttText`) and *Execute Action (JSON)* (top-level keys → args); IFTTT trigger *IFTTT Notification Sub-Action*)
**Sub-actions**
  _(2 pages)_
  - Notification Basic, Notification Advanced

- **Pronouns** (alejo.io)
**Sub-actions**
  _(1 pages)_
  - Add Pronouns For User

- **Streamer.bot Website / Remote / Decks**
**Triggers**
  _(3 pages)_
  - Remote Instance Connected, Remote Instance Disconnected, Remote Instance Trigger

**Sub-actions**
  _(13 pages)_
  - *decks*: Deck Notify, Item Set Background, Item Set Icon, Item Set State, Item Set Title, Item Set Value, Item Toggle State, Page Next, Page Previous, Page Set
  - *remote*: Send Remote Signal, Send Remote Trigger, Wait For Remote Signal

- Decks C# (1.0.0): DeckNotify, DeckItemSetState, DeckItemToggleState, DeckItemSetTitle, DeckItemSetBackground, DeckItemSetIcon, DeckItemSetValue, DeckPageSet, DeckPageNext, DeckPagePrev.
- Not integrated natively (checked): Lumia covers lights; no Philips Hue/LIFX, no Mix It Up/SAMMI, no TikTok, no Trovo (removed). PolyPop/Mtion are minimal (1–2 sub-actions).

---

## 4. Core triggers & sub-actions (not platform-specific)

Sources: https://docs.streamer.bot/api/triggers/core , https://docs.streamer.bot/api/triggers/custom , https://docs.streamer.bot/api/sub-actions/core , https://docs.streamer.bot/api/sub-actions/settings

**Triggers**
  _(37 pages)_
  - *commands*: Command Cooldown, Command Triggered
  - *file-folder-watcher*: Changed, Created, Deleted, Renamed
  - *file-io/file-tail*: Changed
  - *global-variables*: Global Variable Created, Global Variable Deleted, Global Variable Updated, User Global Variable Updated
  - *groups*: Added, Cleared, Removed, Users Added, Users Removed
  - *inputs*: Key Press, Mouse Click
  - *midi*: Message
  - *processes*: Process Started, Process Stopped
  - *quotes*: Quote Added, Show Quote
  - *schedule*: Cron, Timed Actions
  - Streamer.bot Started, Test
  - *system*: Clipboard Changed, Toast Activation
  - *voice-control*: Command, Dictation
  - *websocket/client*: Websocket Client Closed, Websocket Client Message, Websocket Client Opened
  - *websocket/custom-server*: Connection Closed, Connection Opened, Message

**Triggers**
  _(2 pages)_
  - Code Event, Custom

**Sub-actions**
  _(81 pages)_
  - *action-queues*: Clear Action Queue, Set Action Queue Blocking State, Set Action Queue Pause State
  - *actions*: Get Action Group State, Get Action State, Run Action, Set Action Group State, Set Action State, Set Sub Action State, Set Sub Action Weight
  - *arguments*: Set Argument
  - *commands*: Get Command Group State, Get Command State, Get Commands, Set Command Group State, Set Command State
  - *csharp*: Execute Csharp Code, Execute Csharp Method
  - Delay
  - *file-io*: File Exists, Folder Exists, Read Lines From File, Read Random Line From File, Read Specific Line From File, Write To File
  - *file-tail*: Set File Tail State
  - *globals*: Clear Non Persisted Globals, Clear Non Persisted User Globals, Global Get, Global Set
  - *logic*: Break, Get Input, Get Random Number, If Else, Loop Break, Loop Continue, Switch, While
  - *midi*: Control Change, Generic Event, Note On
  - *misc*: Comment, Log
  - *network*: Fetch URL, UDP Broadcast
  - *points/admin*: Get Points Definition, Reset Points Definition
  - *points/bulk*: Award Points To All
  - *points/groups*: Award Points To Group, Get Group Multiplier, Remove Group Multiplier, Set Group Multiplier
  - *points/leaderboards*: Get Points Leaderboard
  - *points/user*: Award Points, Check Points, Delete Points, Get Points, Get User Multiplier, Reset User Points, Set Points, Spend Points, Transfer Points, Update Points
  - *quotes*: Add Quote, Delete Quote, Get Quote, Get Quote Count
  - *signals*: Send Local Signal, Wait For Local Signal
  - *sounds*: Play Sound, Play Sound From Folder, Stop All Sound Playback, Stop Sound Playback
  - *system*: Keyboard Press, Run A Program, Toast Notification
  - *timers*: Set Timer State
  - *triggers*: Custom Event Trigger, Set Trigger State
  - *voice-control*: Set Voice Control Command, Set Voice Control Command State

**Sub-actions**
  _(1 pages)_
  - Pick Color

- Extra core sub-action pages in full text (not in index): **Clear Users From Group**, **Reset Credits**, **Reset First Words**, **Set Voice Control Input**.
- Notable core sub-action behaviours: Fetch URL (GET, parse as JSON → args, "like Nightbot URL commands"), UDP Broadcast (port, payload), Run a Program (path, args, working dir, wait), Write To File (append/overwrite), Read Lines/Random/Specific line from file, Execute C# Code (Monaco editor, references, precompile on start, Save & Compile, Find Refs), Execute C# Method (call public method of another code action), Set Argument (with inline math), Trigger Custom Event, Set Trigger State, Set Sub-Action State/Weight, Set Action/Action Group State, Toast Notification.
- `[CL 1.1α]` Get Action Queue State, Get Action Queue Pending Count, Force Next Action, Force Random Action, Shuffle Actions in Queue, Midi Sysex Event.
- `[EV]` misc events: Application ActionAdded/Deleted/Updated, CommandAdded/Deleted/Updated, ApplicationImport, ChatWindowAction, StreamerbotExiting, Raw Action/ActionCompleted/SubAction, Inputs ControllerAxis/Button/Hat [1.1α], SpeechToText Log.

---

## 5. Servers / APIs

### 5.1 WebSocket Server (Servers/Clients > WebSocket Server)
Source: https://docs.streamer.bot/api/websocket , /api/websocket/guide/{configuration,client,requests,events,authentication} , /api/websocket/recipes/{overlays,remote-access}
- Config: Auto Start, Address (127.0.0.1; 0.0.0.0 for LAN), Port **8080**, Endpoint `/`, Authentication (password), Enforce (auth for all requests; otherwise only privileged e.g. SendMessage).
- Protocol: JSON `{request, id, ...}` → `{status: ok|error, id, ...}`; on connect server sends **Hello** (`info`: instanceId, name, version, os, osVersion, mode, darkMode, source; `authentication`: salt, challenge). Auth: `base64(sha256(base64(sha256(password+salt)) + challenge))` sent as `Authenticate` request.
- Event push: `{timeStamp, event:{source,type}, data}`; only after **Subscribe**.
- **Requests (22)**: Subscribe, UnSubscribe, GetEvents, GetActions, DoAction (by id or name, args; returns queued id + args), GetBroadcaster, GetCredits, TestCredits, ClearCredits, GetInfo, GetActiveViewers, GetCodeTriggers, ExecuteCodeTrigger, GetCommands, TwitchGetEmotes, YouTubeGetEmotes, GetGlobals, GetGlobal, TwitchGetUserGlobals, TwitchGetUserGlobal, SendMessage (auth required), GetUserPronouns. (Also Authenticate.)
- **Event sources → types** (subscribe categories):
  - Application: ActionAdded, ActionDeleted, ActionUpdated, CommandAdded, CommandDeleted, CommandUpdated
  - Command: Cooldown, Triggered
  - CrowdControl: CoinExchange, EffectFailure, EffectRequest, EffectSuccess, GameSessionEnd, GameSessionStart, TimedEffectEnded, TimedEffectStarted, TimedEffectUpdated
  - Custom: CodeEvent, Event; General: Custom
  - DonorDrive: Donation, Incentive, ProfileUpdated
  - Elgato: CameraHub* (≈68), ControlCenter* (11), StreamDeckMcp Connected/Disconnected, Studio* (9), WaveLink* (20 legacy), WaveLinkV3* (19)
  - FileTail: Changed; FileWatcher: Changed, Created, Deleted, Renamed
  - Fourthwall: Donation, GiftDrawEnded, GiftDrawStarted, GiftPurchase, NewsletterSubscribed, OrderPlaced, OrderUpdated, ProductCreated, ProductUpdated, SubscriptionChanged, SubscriptionExpired, SubscriptionPurchased, ThankYouSent
  - Group: Added, Cleared, Removed, UsersAdded, UsersRemoved
  - HypeRate: Connected, Disconnected, HeartRatePulse, TwitchClipCreated
  - Inputs: InputControllerAxis, InputControllerButton, InputControllerHat, InputKeyPress, InputMouseClick
  - Kick: BroadcasterAuthenticated, BroadcasterChatConnected, BroadcasterChatDisconnected, ChannelUpdate, ChatMessage, FirstWords, Follow, GiftSubscription, KicksGifted, MassGiftSubscription, PresentViewers, Resubscription, RewardRedemption, SevenTVEmoteAdded, SevenTVEmoteRemoved, StreamOffline, StreamOnline, Subscription, UserBanned, UserTimedOut, ViewerCountUpdate
  - Kofi: Commission, Donation, Resubscription, ShopOrder, Subscription
  - MeldStudio: Connected, Disconnected, Event, LayerVisbilityChanged, LEffectEnabledStateChanged, RecordingStarted, RecordingStopped, SceneChanged, StreamingStarted, StreamingStopped, TrackMonitoringStateChanged, TrackMustedStateChanged
  - Midi: Message
  - Misc: ApplicationImport, ChatWindowAction, GlobalVariableCreated, GlobalVariableDeleted, GlobalVariableUpdated, ProcessStarted, ProcessStopped, StreamerbotExiting, StreamerbotStarted, Test, TimedAction, ToastActivation, UserGlobalVariableUpdated
  - Obs: Connected, Disconnected, Event, RecordingStarted, RecordingStopped, SceneChanged, StreamingStarted, StreamingStopped, VendorEvent
  - Obsbot [1.1α]: Connected, DeviceConnected, DeviceDisconnected, DeviceSleepStateChanged, Disconnected
  - Pallygg: CampaignTip, Connected, Disconnected; Patreon: FollowCreated, FollowDeleted, PledgeCreated, PledgeDeleted, PledgeUpdated; Pulsoid: HeartRatePulse
  - Quote: Added, Show; Raw: Action, ActionCompleted, SubAction; Shopify: OrderCreated, OrderPaid
  - SpeakerBot: Connected, Disconnected; SpeechToText: Command, Dictation, Log
  - StreamDeck: Action, Connected, Disconnected, Info
  - StreamElements: Authenticated, Connected, Disconnected, Merch, Tip
  - StreamLoots: CardRedeemed, PackGifted, PackPurchased, Reaction
  - StreamerBot: CustomWebook; StreamerBotRemote: InstanceConnected, InstanceDisconnected, InstanceSignal, InstanceTrigger
  - Streamlabs: Authenticated, CharityDonation, Connected, Disconnected, Donation, Merchandise
  - StreamlabsDesktop: Connected, Disconnected, RecordingStarted, RecordingStopped, SceneChanged, StreamingStarted, StreamingStopped
  - System: ClipboardChanged, Cron
  - Throne: ContributionPurchased, GiftCrowdfunded, GiftPurchased; TipeeeStream: Donation
  - ThrowingSystem (T.I.T.S.): Connected, EventsConnected, EventsDisconnected, ItemHit, TriggerActivated, TriggerEnded, WebsocketConnected, WebsocketDisconnected
  - TreatStream: Authenticated, Connected, Disconnected, Treat
  - Twitch (~150): AdRun, Announcement, AutomaticRewardRedemption, AutoModMessageHeld, AutoModMessageUpdate, BetterTTVEmoteAdded/Removed, BitsBadgeTier, BlockedTermsAdded/Deleted, BotEventSubConnected/Disconnected, BotWhisper, BroadcasterAuthenticated, BroadcasterChatConnected/Disconnected, BroadcasterEventSubConnected/Disconnected, CharityCompleted/Donation/Progress/Started, ChatCleared, ChatEmoteModeOff/On, ChatFollowerModeChanged/Off/On, ChatMessage, ChatMessageDeleted, ChatSlowModeChanged/Off/On, ChatSubscriberModeOff/On, ChatUniqueModeOff/On, Cheer, CoinCheer, CommunityGoalContribution/Ended, CustomPowerUpRedemption, FirstWord, Follow, GiftBomb, GiftPaidUpgrade, GiftSub, GoalBegin/End/Progress, GuestStarGuestUpdate/SessionBegin/SessionEnd/SettingsUpdate/SlotUpdate, HypeChat, HypeChatLevel, HypeTrainEnd/LevelUp/Start/Update, ModeratorAdded/Removed, Modiversary, PayItForward, PermittedTermsAdded/Deleted, PollArchived/Completed/Created/Terminated/Updated, PowerUpRedemption, PredictionCanceled/Completed/Created/Locked/Updated, PresentViewers, PrimePaidUpgrade, PyramidBroken/Success, Raid, RaidCancelled, RaidSend, RaidStart, ReSub, RewardCreated/Deleted/Redemption/RedemptionUpdated/Updated, SevenTVEmoteAdded/Removed, SharedChat{Announcement, CommunitySubGift, GiftPaidUpgrade, MessageDeleted, PayItForward, PrimePaidUpgrade, Raid, Resub, SessionBegin, SessionEnd, SessionUpdate, Sub, SubGift, UserBanned, UserTimedout, UserUnbanned, UserUntimedout}, SharedModiversary, ShieldModeBegin/End, ShoutoutCreated/Received, StreamOffline/Online, StreamUpdate, StreamUpdateGameOnConnect, Sub, SubCounterRollover, SuspiciousUserMessage/Update, UnbanRequestApproved/Created/Denied, UpcomingAd, UserBanned/TimedOut/Unbanned/UntimedOut, ViewerCountUpdate, VipAdded/Removed, WarnedUser, WarningAcknowledged, WatchStreak, Whisper
  - VoiceMod: Connected, Disconnected, SoundboardChanged, VoiceLoaded
  - VTubeStudio: BackgroundChanged, Connected, Disconnected, HotkeyTriggered, ItemEvent, ModelAnimation, ModelClicked, ModelConfigChanged, ModelLoaded, ModelUnloaded, TrackingStatusChanged
  - WebsocketClient: Close, Message, Open; WebsocketCustomServer: Close, Message, Open
  - YouTube: BetterTTVEmoteAdded/Removed, BroadcastAdded/Ended/MonitoringEnded/MonitoringStarted/Removed/Started/Updated, FirstWords, GiftMembershipReceived, JewelsGifted, MemberMileStone, MembershipGift, Message, MessageDeleted, NewSponsor, NewSponsorOnlyEnded/Started, NewSubscriber, PollClosed/Started/Updated, PresentViewers, SevenTVEmoteAdded/Removed, StatisticsUpdated, SuperChat, SuperSticker, UserBanned, UserTimedout
- Breaking notes: legacy `message` object removed from WS payloads (1.0.5); payload reshapes for Twitch/YouTube (1.1α); camelCase.

### 5.2 Custom WebSocket servers & clients (Servers/Clients)
- **WebSocket Servers** (user-defined, multiple; exportable): triggers Connection Opened/Closed, Message; C# WebsocketCustomServerStart/Stop/IsListening/Broadcast/CloseSession/CloseAllSessions/GetConnectionByName.
- **WebSocket Clients** (outgoing connections, multiple; exportable): triggers Opened/Closed/Message; C# WebsocketConnect/Disconnect/IsConnected/Send (by index/name). Main server broadcast: WebsocketBroadcastJson/WebsocketBroadcastString.

### 5.3 HTTP Server (Servers/Clients > HTTP Server; Kestrel since 1.0.5)
Source: https://docs.streamer.bot/api/http
- Config: Auto Start (off), Host 127.0.0.1, Port **7474**, **Mappings** (Path → local folder/file; static file hosting, 1.0.0).
- Endpoints (prefer `/api/` prefix; un-prefixed deprecated): `POST /DoAction` (action id/name + args), `GET /GetActions`, `GET /GetCredits`, `GET /TestCredits`, `GET /ClearCredits` (204), `GET /ClearFirstWordsCache`. Other WS-equivalent GETs exist (e.g. emotes, user globals per changelog).

### 5.4 UDP Server (Servers/Clients > UDP Server)
Source: https://docs.streamer.bot/api/servers/udp
- Auto Start (off), Port **4242**. Message: `{"request":"DoAction","action":{"id"|"name"},"args":{...}}`. Used by VoiceAttack plugin, AutoHotKey. UDP Broadcast sub-action for outgoing; Speaker.bot Speak (UDP).

### 5.5 C# (CPH) API
Source: https://docs.streamer.bot/api/csharp (guide: intro, variables, lifecycle-methods, custom-methods, threading-best-practices, debugging; recipes: custom DLLs, HttpClient, VS Code)
- Model: Execute C# Code sub-action hosts class `CPHInline` with `public bool Execute()` (return false stops action), optional `Init()` (on compile/startup), `Dispose()` (on destroy/shutdown), custom public methods callable via Execute C# Method sub-action or `CPH.ExecuteMethod(codeName, method)`; `args` dictionary; `CPH.TryGetArg<T>`; references (DLLs, Find Refs), Precompile on Application Start, Compile / Save and Compile, compiling log, per-run args isolation [1.1α]. .NET Framework 4.8.1.
- Method groups (documented pages, 202):
  - Action queues: PauseActionQueue, PauseAllActionQueues, ResumeActionQueue, ResumeAllActionQueues (+1.1α queue methods §1.3)
  - Actions: ActionExists, EnableAction(ById), DisableAction(ById), GetActions, RunAction, RunActionById, SendAction
  - Commands: Command{AddToAllUserCooldowns, AddToGlobalCooldown, AddToUserCooldown, GetCounter, GetUserCounter(ById), RemoveAllUserCooldowns, RemoveGlobalCooldown, RemoveUserCooldown, ResetAllUserCooldowns, ResetAllUserCounters, ResetCounter, ResetGlobalCooldown, ResetUserCooldown, ResetUserCounter, ResetUsersCounters, SetGlobalCooldownDuration, SetUserCooldownDuration}, EnableCommand, DisableCommand, GetCommands
  - Events: GetEventType, GetSource
  - Globals: GetGlobalVar<T>, SetGlobalVar, UnsetGlobalVar, GetGlobalVarValues, GetUserVar, SetUserVar, UnsetUserVar, UnsetUser, UnsetAllUsersVar, ClearNonPersistedGlobals, ClearNonPersistedUserGlobals (+ per-platform Twitch/YouTube/Kick user var families)
  - Misc: Between, NextDouble, Wait, GetVersion, Parse(ParseVariables), SetArgument, TryGetArg, LogInfo/LogWarn/LogError/LogDebug/LogVerbose, TwitchClientId/TwitchOAuthToken
  - Sounds/System: StopSoundPlayback, StopAllSoundPlayback, PlaySound (implied), KeyboardPress
  - Timers: EnableTimerById, DisableTimerById, GetTimerState, SetTimerInterval
  - Triggers: RegisterCustomTrigger, TriggerCodeEvent, TriggerEvent
  - Users/groups: AddGroup, DeleteGroup, GetGroups, GroupExists, AddUserToGroup, AddUserIdToGroup, RemoveUserFromGroup, RemoveUserIdFromGroup, UserInGroup, UserIdInGroup, UsersInGroup, ClearUsersFromGroup
  - WebSocket: WebsocketBroadcastJson/String, WebsocketConnect/Disconnect/IsConnected/Send, WebsocketCustomServer{Broadcast, CloseAllSessions, CloseSession, GetConnectionByName, IsListening, Start, Stop}
  - Integrations: DiscordPostTextToWebhook; Meld*; Obs* filters/raw; Decks*; Points* [1.1α]
  - Platforms: Twitch*, YouTube*, Kick* (see §2)
- Classes: ActionData, Cheermote(+Image/Images/Tier), ClipData, CommandCounter, CommandData, GameInfo, GlobalVariableValue, GroupUser, GuestSession, GuestStar, GuestStarInvite, GuestStarSettings, KickCategory, KickUserInfo, MediaSettings, QuoteData, StreamMarker, TeamInfo, TwitchReward, TwitchRewardCounter, TwitchUserInfo, TwitchUserInfoEx, UserVariableValue<T>, VTSModelPosition, YouTubeBroadcastInfo, YouTubeUserInfo (+1.1α PointDefinitionDto, UserPointDto, PayoutRuleDto, WaitingAction).
- Enums: EventSource (Application, Command, CrowdControl, Custom, DonorDrive, Elgato, FileTail, FileWatcher, Fourthwall, General, Group, HypeRate, Inputs, Invalid, Kick, Kofi, MeldStudio, Midi, Misc, Obs, Pallygg, Patreon, Pulsoid, Quote, Raw, Shopify, SpeakerBot, SpeechToText, StreamDeck, StreamElements, StreamerBot, StreamerBotRemote, Streamlabs, StreamlabsDesktop, StreamLoots, System, Throne, ThrowingSystem, TipeeeStream, TreatStream, Twitch, VoiceMod, VTubeStudio, WebsocketClient, WebsocketCustomServer, YouTube), EventType (~500 values = Source+Type as in §5.1), Platform (Twitch, YouTube, Kick), StreamlabsDesktopOutputType (Both, Horizontal, Vertical), TwitchBlockContext (None, Chat, Whisper), TwitchBlockReason (None, Harassment, Spam, Other).

### 5.6 @streamerbot/client (JS)
- `new StreamerbotClient({host, port, endpoint, password})`, `doAction(id | {name})`, `.on('Source.Type' | 'Source.*' | '*', handler)` auto-subscribe, auto-auth. Used by Chat, Decks, HTML Overlay, overlays recipe.

---

## 6. Speaker.bot integration points
Sources: https://docs.streamer.bot/guide/integrations/speakerbot , https://docs.streamer.bot/api/sub-actions/speakerbot , https://docs.streamer.bot/changelogs/speaker.bot , /faq/tts-delay , /faq/tts-speaks-twice
- Connection: Integrations > Speaker.bot — Auto Connect, Auto Reconnect, Address (127.0.0.1), Port **7580** (Speaker.bot Settings > WebSocket Server); password support **[1.1α]**; supports Speaker.bot 0.1.4+ and 1.0.0-alpha.
- Triggers: Speaker.bot Connected, Disconnected (EventSource SpeakerBot).
- Sub-actions: **Speak** (Voice Alias [list of aliases 1.1α], Pass Through Bad Word Filter, Silent (process only → `speechFile` arg), Delay/wait until spoken (30 s cap), Message), **Speak (UDP)** (legacy; Voice Alias, bad-word filter, Message; may double-speak), **Stop Speaking**, **Clear Pending**, **Set Enabled State** (Enabled/Disabled/Toggle), **Set Paused State** (Paused/Enabled/Toggle), **Activate VoiceGate Profile**.
- Pattern: paused queue + Force Next Action for controlled TTS queue (1.1α).
- Speaker.bot itself also connects to Streamer.bot's WebSocket server to read chat/events (separate product; not inventoried here).

---

## Appendix A. Changelog-only / undocumented items to verify
- Sub-actions: Twitch Get Clip Download Urls; Kick Channel Reward sub-actions (set of); Streamlabs Spin Wheel; YouTube Create Poll / End Poll; Add YouTube/Trovo Broadcaster Information; Meld Set Browser Layer URL, Take Vertical Screenshot; Get Action Queue State, Get Action Queue Pending Count, Force Next Action, Force Random Action, Shuffle Actions in Queue; Midi Sysex Event; OBSBOT sub-actions (WIP).
- Removed features (don't need parity): Twitch PubSub, IRC chat, Ad Mid Roll trigger (page still exists), Voicemod Set Censor State (v3), built-in `!quote`, Trovo platform, WinForms UI.
- Documentation gaps: most sub-action parameter tables, trigger variable tables (rendered client-side; counts only), Settings pages, Users/Viewers page, many Obs*/Twitch* CPH methods.

## Appendix B. Could not access / limitations
- Variable tables on trigger pages are collapsed components; not extracted (only category names + counts).
- `docs.streamer.bot/raw/changelogs/*.md` returns HTTP 500; changelogs were read from rendered HTML.
- Speaker.bot, Decks, Chat, Client, Stream Deck plugin changelogs (docs.streamer.bot/changelogs/{speaker.bot,decks,chat,client,stream-deck-plugin}) not read in detail.
