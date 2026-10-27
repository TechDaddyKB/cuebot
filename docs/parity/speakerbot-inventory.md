# Speaker.bot Feature Inventory (public docs only)

Clean-room parity reference for Cuebot. Built only from public docs, release notes, and archived public wiki pages. Nothing here comes from decompiled or extracted binaries.

- Current stable: **0.1.7** (2026-05-25). Earlier releases: 0.1.6 (2026-01-14), 0.1.5 (2025-04-10), 0.1.4 (2024-07-05), 0.1.3 (2023-11-29), 0.1.2 (2023-10-31), 0.1.1 (2023-04-14), 0.1.0 (2023-02-22, renamed from **TwitchSpeaker**). TwitchSpeaker ran 0.0.4 to 0.0.49 (2020-2022).
- Platform: Windows, .NET Framework 4.8.1 (since 0.1.5). Portable zip, no installer. Runs `Speaker.bot.exe`. EV code-signed since 0.1.6.
- Status tags used below:
  - **[doc]** current docs (speaker.bot)
  - **[cl]** changelog or release notes
  - **[wiki]** archived TwitchSpeaker wiki (2022-2024). It may be stale.
  - **[inferred]** reasoned from indirect evidence. Verify before building.

## Primary sources
- Docs site: https://speaker.bot (sources at https://github.com/Streamerbot/docs/tree/main/speakerbot)
  - /get-started/installation, /get-started/accounts, /get-started/speech-engines, /get-started/quick-start
  - /guide/commands, /guide/events, /guide/config (general), /guide/config/ignore, /guide/config/speak-options, /guide/config/replace, /guide/config/users
  - /api/websocket, /api/websocket/requests, /api/udp, /api/udp/requests
  - /changelogs/v0.1.6 (the other versions on this site are client-rendered and do not fetch)
- Changelogs mirror: https://docs.streamer.bot/changelogs/speaker.bot/v0.1.2 … v0.1.5
- Release API (JSON release notes): https://streamer.bot/api/releases/speaker.bot and https://streamer.bot/api/releases/speaker.bot/{version}
- Streamer.bot integration docs:
  - https://docs.streamer.bot/guide/integrations/speakerbot
  - https://docs.streamer.bot/api/sub-actions/speakerbot
  - https://docs.streamer.bot/api/triggers/integrations/speakerbot
  - https://docs.streamer.bot/api/csharp/methods/speakerbot
  - FAQ: https://docs.streamer.bot/get-started/_faq/tts-delay and .../tts-speaks-twice
- Archived wiki: https://web.archive.org/web/2023/https://wiki.streamer.bot/en/TwitchSpeaker (subpages listed in each section below)

---

## 1. TTS engines (10 current, 1 removed)

Sources: https://speaker.bot/get-started/speech-engines, wiki `/TwitchSpeaker/Speech-Engines/*` (archived), changelogs 0.0.11, 0.0.34, 0.0.46, 0.1.2 to 0.1.6.

### Engine management (all engines)
- Page: **Settings > Speech Engines**. Click **Add**, then pick an engine type from a dropdown. [doc]
- Engines are fully dynamic: add or remove them at runtime, and re-adding works (0.1.2, 0.1.3). [cl]
- The page shows the voice list for every configured engine. [doc]
- Voices refresh after engines initialise (0.1.1). [cl]
- Enabled engines start in parallel or fast (0.1.2). [cl]
- If an engine fails to add, the app shows a prompt instead of crashing (0.1.5). [cl]
- Init errors are handled without crashing (0.0.12). [cl]
- Google and Watson check that the credential file exists (0.1.3). [cl]
- Character usage tracking ("Usage" database, `usage.dat`) since 0.0.11. [cl]
- Voice metadata shown in voice selections: engine, locale, name, and tier labels such as Standard, Neural, or WaveNet. [doc/wiki]
- Per-voice adjustments in aliases are **Pitch, Rate, Volume, and Emphasis**. Ranges are listed per engine below. [cl 0.0.34]
  - History shows them as `Pitch:1, Rate:0, Volume 162%`, so volume goes above 100%. [wiki Speaking-History]

### Engines

| # | Engine | Auth / config | Options and notes |
|---|---|---|---|
| 1 | **SAPI5** (Windows Speech API) | None. Free, local. | Installed Windows voices. Sample rate was bumped in 0.0.9. |
| 2 | **Google Cloud TTS** | Service-account **JSON key file** (browse to the file). | Standard and WaveNet voices are labelled. Speaker.bot **sends SSML**, and SSML tags count toward billed characters. Rate -20..20 (5% steps). Pitch -19..21 (5% relative). Emphasis on Standard and WaveNet. Voices without SSML support are handled since 0.1.5. |
| 3 | **Microsoft Azure** (Cognitive Services Speech) | **Subscription key** (KEY 1) and **service location/region** (e.g. eastus). Region affects which voices are available. | Standard and Neural voices. Rate -10..10. Pitch -9..11 (5% steps). Emphasis on Standard and Neural (hit or miss on Neural). Replaced OneCore in 0.0.34. Custom voice is possible but not covered by the docs. No docs on speaking styles. |
| 4 | **Amazon Polly** | **Access Key ID and Secret Access Key** (IAM user with AmazonPollyReadOnlyAccess). Keys are masked in the UI. | Standard and Neural voices are labelled. Rate -20..20. Pitch -9..11 (not on Neural). Emphasis on Standard only. No region setting is documented [inferred: there is probably a default region]. |
| 5 | **IBM Watson** | **Credentials `.env` file** downloaded from IBM Cloud. | About 50 voices. Rate -20..20. Pitch -19..21. No emphasis. Odd results at the extremes. Free tier is 10k chars/month. |
| 6 | **UberDuck** | **Client (public key)** and **Secret key**. | Paid ("Creator" plan). Voice list handling changed in 0.1.4 and external-service voices were removed. |
| 7 | **TTS Monster** | Paste the **overlay URL**, which contains the user and key info (0.1.3+). | Custom Voices supported (0.1.6). |
| 8 | **Acapela** (Acapela Cloud) | Account credentials [exact fields not documented]. | Added in 0.0.46. |
| 9 | **CereProc** (CereVoice Cloud) | Account credentials [exact fields not documented]. | Added in 0.0.46 as "CereCloud". |
| 10 | **ElevenLabs** | **API key**. | Added in 0.1.2. Pitch/Rate sliders map to **Stability/Similarity**, and the UI relabels them for ElevenLabs voices. The default is -1, meaning the voice default. Legacy voices are supported (0.1.5). Better model handling in 0.1.6. Free tier is 10k chars/month. |
| removed | **OneCore** (Windows.Media.SpeechSynthesis) | — | Removed in 0.0.34, which dropped the hard Windows-runtime requirement. |

- Not supported per public docs: Speechify, Coqui, Piper, OpenAI TTS, Edge TTS, local neural engines, and Polly/Azure custom lexicons.

---

## 2. Voices, aliases, audio, queue, users, commands, events

### 2.1 Voice aliases
Sources: https://speaker.bot/get-started/quick-start, wiki `/TwitchSpeaker/Settings/Voice-Aliases`, changelogs 0.0.26, 0.0.41, 0.1.5, 0.1.6.
- Page: **Settings > Voice Aliases**.
- Create: enter **Name** and click **Add**. Delete is fixed in 0.1.5. The list is sorted alphabetically (0.1.6).
- Each alias holds **one or more voices**, added from a Voice dropdown. Several voices in one alias means a random pick per message [inferred from "random voice selection" and sticky random].
- Per alias: **Output device** ("Application Default" or a specific device).
- Per voice entry: Pitch, Rate, Volume, Emphasis (see section 1).
- **Test Speak** button at the bottom of the page.
- Aliases are referenced **by name** from:
  - Default Voice Alias
  - Highlight Voice Alias
  - Events global alias
  - Custom commands
  - Channel rewards
  - Users
  - WebSocket/UDP `voice`
  - Streamer.bot Speak sub-action
- "Assign voice" from the user context menu, the speech preview, or `!tts assign last` **auto-creates an alias** for that user (0.0.41, UDP docs).
- **Random voice selection**: picks from all voices, minus the active **Ignore Profile**. **Sticky random** keeps a user's first random voice until it is reset.
- Fallback voice selection exists (fixed in 0.0.45) [details not documented].
- Persisted in `voicealiases.dat`.

### 2.2 Ignore profiles (random voice pool)
Source: https://speaker.bot/guide/config/ignore
- Page: **Settings > Ignored Voices / Locales**.
- Named profiles: enter a Name, then click New.
- Two list panes, **Voices** and **Locales**. Double-click toggles an item between available and ignored.
- Switch the active profile, or pick `<none>`.
- Controls:
  - UDP `profile` command (`none` deselects).
  - Chat (legacy): `!tts profile`, `!tts profile list`, `!tts profile activate`.

### 2.3 Audio output
Sources: https://speaker.bot/guide/config, changelogs 0.0.20, 0.0.26, 0.0.33.
- **Audio Output Device**: global default, shown as "Application Default" in aliases. Each alias can override it.
- Uses **WASAPI**, and only WASAPI devices are listed. Disconnected devices also show (0.0.26).
- **Application Volume**: global default volume. Per-voice volume is also set in the alias.
- **Save TTS**: pick a folder and every TTS output is saved as `.wav`. Illegal characters in voice names are handled (0.1.5).

### 2.4 Queue and processing
Sources: https://speaker.bot/guide/commands, /api/websocket/requests, wiki Tabs/Speaking-Queue and Tabs/Speaking-History, changelog 0.1.2.
- One FIFO speaking queue. Each item shows engine, voice, locale, pitch/rate/volume, user, and message.
- Operations:
  - **Pause / Resume**: holds the queue.
  - **Clear**: drops pending items.
  - **Stop**: stops *only* the current speech.
  - **Disable**: per the legacy wiki this "clears, stops and disables". **Enable** turns it back on.
- **Gag** button to silence TTS instantly (0.0.11) [legacy UI; current name unknown].
- **Stop/Skip on moderation**: on Message Deleted, User Timed Out, or User Banned, stop the current speech and skip that user's queued items.
- **Processing tab** (0.1.2) merges the Speaking Queue and Speaking History.
  - History covers the current session only, and is also written to logs.
  - Past voices are stored per user.
- Speaker.bot has a **Delay/await-completion** mode through the Streamer.bot Speak sub-action. It returns the duration in ms and the speech file, with a 30 s hard timeout.
- **Silent** mode renders the audio file without playing it.
- No configurable max queue length is documented. Max message length: see 3.4.

### 2.5 VoiceGate (added 0.1.4)
Sources: changelogs 0.1.4 and 0.1.5, https://docs.streamer.bot/api/sub-actions/speakerbot/activate-voicegate-profile
- Feature with **named VoiceGate profiles**. Streamer.bot has an "Activate VoiceGate Profile" sub-action (name, returns `success`).
- [inferred] It watches a microphone input level and pauses TTS while the streamer talks. 0.1.5: "VoiceGate now resumes when there is no input level (e.g. mic muted)". Profile fields such as device, threshold, and hold time are not documented.

### 2.6 Per-user settings
Sources: https://speaker.bot/guide/config/users, wiki Tabs/Users, changelogs 0.0.15, 0.0.41, 0.1.4.
- Page: **Settings > Users**. The left pane lists users present in chat. Unchecking **Hide Not Present** shows every user ever seen.
- Read-only fields: ID, Username, Role (Broadcaster, Moderator, VIP, Viewer), Last Active, Platform (0.1.4).
- Editable:
  - **Voice Alias** override
  - **Nickname** (spoken instead of the username)
- Toggles:
  - **Ignored**
  - **Forced** (always speak all their messages, bypassing permissions)
  - **Regular**
  - **Subscribed** (read-only, from Twitch)
- **Past Voices** pane: timestamps and voices used. Clear past voices is available.
- Context menu: Assign voice (auto-creates an alias), Delete user (0.1.4).
- Persisted in `users.dat`.

### 2.7 Chat commands (Twitch chat; mods can also whisper them)
Source: https://speaker.bot/guide/commands
- The built-in `!tts` command word can be renamed (0.1.6).
- Commands:
  - `!tts status`
  - `!tts voices`: counts by engine type.
  - `!tts pause|resume`
  - `!tts clear`
  - `!tts stop`
  - `!tts mode all|command`
  - `!tts commands`
  - `!tts off|disable`, `!tts on|enable`
  - `!tts ignore add|del <user>`, `!tts ignored`
  - `!tts reg add|del <user>`
  - `!tts events on|off`
  - `!tts random reset <user>`
  - `!tts set nickname <user> <nick>`: an empty nick removes it.
  - `!tts set sticky on|off`
  - `!tts assign last <user>`
  - `!tts about|aboot`
- Legacy commands:
  - `!tts permissions` toggles specific permissions (0.0.46).
  - `!tts profile [list|activate]` for ignore profiles.
- **Allow Moderator Whispers**: mods whisper `!tts` commands with no chat feedback (0.0.32).
- **Silence Command Output**: suppresses chat replies.
- **Not Allowed Text**: chat reply when a user lacks permission.
- **TTS Speaking Commands**: a list of prefixes that make the text after them spoken. Defaults are `!say` and `!"`.
- **Custom Commands** tab: Command text, Voice Alias, and Permissions (Moderators, Subscribers, VIPs, Regulars, Everyone). Add, Update, and Delete.

### 2.8 Speaking modes and permissions
Source: https://speaker.bot/guide/config/speak-options
- **Say Everything** (mode `all`) or command-only (mode `command`).
- Global permissions: Moderators, Subscribers, VIPs, Regulars, Everyone.
- **Allowed Forced** (honour per-user Forced).
- **Highlight Voice Override** and **Highlight Voice Alias** for highlighted messages and mentions. [doc general]

### 2.9 Event-based speech
Sources: https://speaker.bot/guide/events, wiki Tabs/Events, changelogs 0.0.18, 0.0.22, 0.0.28, 0.0.45, 0.1.6.
- **Events** tab:
  - Global **Voice Alias** override for all events, with an enable toggle.
  - Global **Enabled**.
  - Event list in a nested hierarchy (0.1.6).
- Per event: **multiple messages**, each with Message text, **Weight** (weighted random pick), and Enabled.
- **Event States**: per-event enable checkboxes, plus minimums for **Raid viewers**, **Cheer bits**, and **Donation amount**. Host was removed.
- Duplicate follows are suppressed: one per user per session (0.0.21).
- Known events by source (see section 4):

| Source | Events |
|---|---|
| Twitch | Follow, Sub, Resub (with message), Gift Sub, Gift Bomb (community), Cheer, Raid, Channel Point Reward redemption, Hype Train (start/level/end; V2 since 0.1.6), Community Goal contribution/finished (lost with PubSub removal in 0.1.5) |
| Streamlabs | Donation |
| StreamElements | Tip, Merch |

- Template variables use `%name%` syntax:
  - Core: `name`, `level`, `gift`, `subtier`, `title`, `cost`, `bits`, `amount`, `percent`, `currency`, `tier`, `recipient`, `cumulative`, `message`
  - Legacy: `input` (reward user input), `daysLeft` (community goal)
- Default strings documented (0.0.18):
  - Contribution: "%name% decided to contribute %amount% towards %title%, it's %percent% finished"
  - Goal finished: "%title% was just finished, %amount% points…"
- **Channel Rewards** tab: map a reward to TTS messages. Supports weighted random messages and the `%input%` variable. Rewards load on connect.

---

## 3. Filters and text processing

Sources: https://speaker.bot/guide/config/speak-options, https://speaker.bot/guide/config/replace, wiki Settings/Replacement and Settings/Speaking-Options, changelogs.

### 3.1 Bad Word Filter
- Word or phrase list. Case-insensitive since 0.1.2.
- **Filter Action** options:
  - `Disabled`
  - `Strip` (remove the words)
  - `Yeet All` (drop the entire message)
- API callers choose whether to apply it (`badWordFilter` flag). In C# it defaults to false.

### 3.2 Regex Replacement
- Ordered list of Replace and With pairs. Plain text also works. Ctrl+Up/Down reorders.
- Invalid regex is handled gracefully, and the UI shows feedback (0.0.23).
- Applies to **all spoken text** (chat, events, and so on) and runs **last** in the pipeline.
- Example: `\d{3,}` replaced with an empty string removes long numbers.

### 3.3 URL filter
- **URL Filter**: `Disabled`, `Strip`, or `Yeet All`. A "Global URL Filter" setting was fixed in 0.1.4.

### 3.4 Length and rate limits
- **Max Words**, or **Max Characters** (0.1.3).
- **Last spoke grace period** in seconds. Within this window the username is not repeated, so it acts as a cooldown.
- **Only say username if previous was different**. It cannot be combined with the grace period.

### 3.5 Username and nickname
- **Say Username**, with a **User Said Prefix** (default `said`, as in "User said msg").
- **Replace %name% with nickname if available**.
- Nicknames are set in the Users page, with `!tts set nickname`, or through UDP `set nickname`.
- Regex can be used for name cleanup, e.g. turning underscores into spaces.

### 3.6 Emotes
- Ignore toggles: Twitch, BTTV, FFZ, 7TV, Cheermotes, and Twemoji (Unicode emoji, 0.1.4).
- Each emote service can be toggled (0.1.5).
- **Allow first emote**: speak a single instance of each emote.
- **Allowed emotes** whitelist bypasses the ignore filters. [wiki]
- 7TV Zero-Width emotes (0.1.6). Cheermote matching is case-insensitive and handles numeric prefixes like `4Head`.
- 7TV is updated live over SSE.
- Twemoji is bundled (v17.0.2 in 0.1.6).

### 3.7 Ignore rules
- **Ignored Prefixes**: skip messages that begin with configured characters (e.g. `!`).
- Ignored users: Users page or `!tts ignore`.
- **Ignore Twitch Shared Chat** messages (0.1.5).
- Replies are treated as normal messages (0.0.35).
- No explicit "ignore bots" list. Use ignored users.
- No documented filter for repeated characters or numbers. Use regex.

---

## 4. Platforms and accounts

Sources: https://speaker.bot/get-started/accounts, changelogs 0.1.1 to 0.1.6.
- Page: **Settings > Accounts**.
- **Twitch** (required, OAuth, broadcaster account):
  - Chat, events, channel point rewards, whispers.
  - Fully on **EventSub** since 0.1.6: chat messages, cheers, subs, resubs, gifts, raids, follows v2, VIP, hype train v2, and whispers. PubSub was removed in 0.1.5 and the IRC chat client was removed in 0.1.6.
  - Loads mods, VIPs, subs, cheermotes, and broadcaster emotes. Shared Chat handling.
  - Auto-connect and auto-reconnect. New scopes may require re-authorization.
- **Streamer.bot website** (OAuth): beta and alpha update channels. Access depends on Discord roles.
- **Streamlabs**: Donation events. OAuth since 0.1.5, API token before that. Socket.io v4.
- **StreamElements** (OAuth): Tip and Merch events.
- **Not supported**: YouTube (explicitly, per wiki), Kick, Trovo, TikTok, and Discord.
- **Streamer.bot link**: Streamer.bot connects *to* Speaker.bot's WebSocket server (Speaker.bot is the server). UDP is the legacy path.

---

## 5. Speech-to-text and voice commands
- **None documented.** Speaker.bot is TTS-only.
- The only microphone-related feature is **VoiceGate** (2.5), which gates on input level and does not recognise speech.
- "Voice commands for viewers" in Speaker.bot means chat commands (2.7). There is no `!voice <name>` command for viewers to pick a voice. Voices are assigned by mods (`!tts assign last`), by sticky random, or in the UI.

---

## 6. Integrations and APIs

### 6.1 WebSocket server
Sources: https://speaker.bot/api/websocket, https://speaker.bot/api/websocket/requests, wiki Servers-Clients/Websocket-Server/Requests.
- Page: **Settings > WebSocket Server**.
- Settings:
  - **Auto Start** (default false) and a Start Server button
  - **Address** (default `127.0.0.1`)
  - **Port** (default **7580**; the new docs say 7680, which conflicts with the Streamer.bot docs and the wiki, both 7580)
  - **Endpoint** (default `/`)
- Request envelope: `{ "id": "<nonce>", "request": "<Name>", ...}`. This is Streamer.bot style, and responses carry the id.
- Requests:

| Request | Params | Notes |
|---|---|---|
| `Speak` | `voice` (alias), `message`, `badWordFilter`? | |
| `Stop` | — | Stops the current speech only. |
| `Enable` / `On` | — | |
| `Disable` / `Off` | — | |
| `Events` | `state`: on/off | Turns all event speech on or off. |
| `Mode` | `mode`: all/command | Fails if no commands exist. |
| `Pause` | — | |
| `Resume` | — | |
| `Clear` | — | |

- Undocumented requests and events exist. Streamer.bot sub-actions (6.3) imply requests for:
  - Set Enabled State (Enabled/Disabled/Toggle)
  - Set Paused State (Paused/Enabled/Toggle)
  - Activate VoiceGate Profile
  - Speak with Silent or Delay, returning `speechFile` and `duration`
- The changelogs mention "websocket subscribe messages" and "events broadcasting to WebSocket" (0.1.4), and "new WebSocket methods for Streamer.bot 0.2.3" (0.1.2, 0.1.3). [inferred] There is a Subscribe-style event stream. Its schema is not public.

### 6.2 UDP interface
Sources: https://speaker.bot/api/udp, https://speaker.bot/api/udp/requests
- Fixed port **6669** (cannot be changed). Always on. JSON datagrams `{ "command": ... }`.
- Commands:
  - `speak` {id, voice, message} (+ bad-word flag since 0.0.47)
  - `stop`
  - `enable`/`on`, `disable`/`off`
  - `pause`, `resume`, `clear`
  - `events` {value: event name, state: on|off}: per-event
  - `profile` {profile | "none"}: ignore profile
  - `reg` {mode: add|del, user, id}
  - `set` {method: nickname, username, nickname}
  - `set` {method: sticky, value: bool}
  - `assign` {method: last, username}
- Known issue: UDP broadcast can make TTS speak twice when a PC has several network adapters. The WebSocket route is the recommended fix.

### 6.3 Streamer.bot side
Sources: https://docs.streamer.bot/guide/integrations/speakerbot, /api/sub-actions/speakerbot/*, /api/triggers/integrations/speakerbot/*, /api/csharp/methods/speakerbot
- Integration settings: Auto Connect, Auto Reconnect, Address (127.0.0.1), Port (7580).
- Sub-actions:
  - **Speak** (WebSocket): Voice Alias, Message, Pass Through Bad Word Filter, **Silent** (render only, return the file path), **Delay** (wait until playback finishes, 30 s cap).
    - Outputs `success`, `speechFile` (needs Save TTS on), and `duration` in ms.
  - **Speak (UDP)**: Voice Alias, Message, Bad Word Filter. C# `TtsSpeak`.
  - **Stop Speaking**
  - **Clear Pending**
  - **Set Enabled State** (Enabled/Disabled/Toggle; SB 0.2.3)
  - **Set Paused State** (Paused/Enabled/Toggle; SB 0.2.3)
  - **Activate VoiceGate Profile** (Profile Name, returns `success`; SB 0.2.4)
- Triggers: **Speaker.bot Connected** and **Speaker.bot Disconnected** (SB 0.2.5).
- C#: `int CPH.TtsSpeak(string voiceAlias, string message, bool badWordFilter = false)`, sent over UDP.

### 6.4 Other
- There is no HTTP server, overlay/browser source, MIDI, or OBS integration in Speaker.bot itself. Audio plays to a device, or files are saved for OBS or Streamer.bot playback.

---

## 7. UI, app settings, and maintenance

Sources: https://speaker.bot/guide/config, https://speaker.bot/get-started/installation, wiki Backup/Update/Event-Log, changelogs.
- **Main tabs** (legacy plus current):
  - Event Log
  - Processing (Queue and History merged)
  - Users
  - Events
  - Custom Commands
  - Channel Rewards
  - Settings
- **Settings subpages**:
  - General
  - Accounts
  - Ignored Voices/Locales
  - Speaking Options
  - Replacement
  - Voice Aliases
  - Speech Engines
  - WebSocket Server
  - VoiceGate [inferred location]
- **General settings**:
  - Instance **Identifier** (unique ID, resettable) and **Name** (shown in the title bar)
  - Enabled (global)
  - Allow Moderator Whispers
  - **Log Level** (Verbose and others)
  - Audio Output Device
  - Application Volume
  - Save TTS folder
  - Default Voice Alias
  - Highlight Voice Override/Alias
- **Generic Speaker** window (0.0.47): a free-form box to type text and speak it with a chosen voice. There is also a speech preview window for editing voices and assigning a voice to a user.
- **Tray and window**:
  - Minimize to tray (0.1.1)
  - Tray tooltip limited to 128 chars
  - Confirm on close (0.1.1)
  - Remembers window position (0.1.6)
- **Single instance** only (0.0.41).
- **Headless mode** with no GUI (0.1.2) [the launch flag is not documented].
- **Logs**: `logs/` folder with text logs, plus an in-app Event Log tab.
- **Data**:
  - `data/` folder holds `settings.json`, `voicealiases.dat`, `users.dat`, and `usage.dat`.
  - LiteDB-based storage [cl 0.1.2 "LiteDB handling"].
  - Safer save routines to limit corruption (0.1.7).
  - Settings integrity checks so files are not wiped (0.0.12).
- **Backups**: an auto backup on every start zips the config into a timestamped file in `backups/`. Restoring means unzipping into `data/`.
- **Import/export**: no import/export feature is documented beyond copying files and backups.
- **Updates**:
  - Built-in auto-updater with stable, beta, and alpha channels. Beta and alpha need a Streamer.bot account login.
  - Auto-update and restart handling.
  - Release feed at `https://streamer.bot/api/releases/speaker.bot`, giving version, notes, download URL, SHA-256, size, and a `force` flag.
- **Dialogs**: prompts auto-time out. The Twitch auth error dialog closes after 10 s.
- Privacy policy and terms: https://speaker.bot/privacy, https://speaker.bot/terms

---

## Feature checklist count (for parity tracking)

| Area | Count |
|---|---|
| Engines (current) | 10 (+1 removed: OneCore) |
| Chat `!tts` subcommands | 18 current + 2 legacy |
| WebSocket requests (documented) | 9 (+2 aliases: On/Off) |
| UDP commands | 14 |
| Streamer.bot sub-actions | 7 |
| Streamer.bot triggers | 2 |
| Event template variables | 16 |
| Event sources | 3 (Twitch, Streamlabs, StreamElements) |
| Filter/processing features | ~20 (bad words ×3 modes, regex, URL ×3, max words/chars, grace period, username/prefix, nickname, 6 emote sources, allow-first, allowed emotes, ignored prefixes, shared-chat ignore, moderation stop/skip) |

## Gaps: not publicly documented (verify via black-box testing only)
- Per-engine credential fields for Acapela and CereProc, and the Polly region.
- Azure styles and roles, and whether SSML passthrough is allowed from user text.
- Exact per-alias voice selection rules (random vs weighted) and the fallback order.
- VoiceGate settings and the UI location.
- WebSocket response schemas, event and subscription schemas, and undocumented requests used by Streamer.bot 0.2.3+.
- Headless mode flags, plus queue limits and timeouts beyond the 30 s Delay cap.
- Detailed changelogs for 0.1.1 to 0.1.5 on speaker.bot. They render client-side, so they were taken from the docs.streamer.bot mirror and the release API instead.
