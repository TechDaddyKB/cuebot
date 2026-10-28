# Feature parity: Streamer.bot + Speaker.bot → Cuebot

Goal: everything Streamer.bot (1.0.7, incl. documented 1.1-alpha features) and Speaker.bot (0.1.7) do, on Windows
**and** Linux, in one app, then build beyond them. Item-level checklists come from public documentation only:
[`parity/streamerbot-inventory.md`](parity/streamerbot-inventory.md) (431 triggers, 421 sub-actions, ~200 C# methods,
22 WebSocket requests, ~38 integrations) and [`parity/speakerbot-inventory.md`](parity/speakerbot-inventory.md)
(10 TTS engines, 18 viewer commands, WebSocket + UDP APIs).

Legend: ✅ done · 🟡 partial · ❌ missing · ⛔ out of scope · ➕ Cuebot-only (beyond parity). Phase = planned delivery wave (below).

## 1. Actions engine and core concepts

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Actions, groups, enable/disable, duplicate | ✓ | ✓ | ✅ | — |
| Triggers → actions, many-to-many, test run | ✓ (+ Test Trigger with custom args, Trigger Viewer) | Test trigger with custom args; Trigger Viewer (History page) | ✅ | 2–4 |
| Named action queues (blocking, pause/resume, clear, always-run, skip queue) | ✓ | Named queues, blocking, pause/resume/clear, always-run (skip-queue for rewards pending) | ✅ | 2 |
| Random action / weights, concurrent sub-actions | ✓ | ✓ | ✅ | 2 |
| Sub-action groups (nestable, coloured, random) | ✓ | ✓ | ✅ | 2 |
| Logic: If/Else, Switch, While, Loop Break/Continue, Break, Get Input, Random Number | ✓ | ✓ | ✅ | 2 |
| Signals: local/remote send & wait | ✓ | Local and remote: Send/Wait for remote signal (one instance or any, args, timeout, overwrite variables), Remote signal received + instance connected/disconnected triggers, script functions (`src/Cuebot.Remote`). Instances link directly over the REST API WebSocket (`/api/v1/remote`, the other instance's API token) instead of Streamer.bot's cloud Website Integration; Send Remote Trigger is covered by Send remote signal + Remote signal received; tested with two instances on loopback | ✅ | 6 |
| Pending actions + Action History (inspect variables, requeue) | ✓ | History page: inspector, filters, run again, cancel; pending work per queue | ✅ | 4 |
| Node-graph editor | — | ✓ | ➕ | — |
| Copy/paste triggers, sub-actions, actions; undo/redo | ✓ | Copy/paste (also across instances), per-action undo/redo, Ctrl+C/V/D/Z/Y; whole-action delete not undoable | ✅ | 4 |

## 2. Variables and text

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| `%arg%` arguments, globals (persisted/temporary) | ✓ | ✓ | ✅ | — |
| Per-user globals (per platform) | ✓ | ✓ | ✅ | 2 |
| Increment/decrement, auto type | ✓ | ✓ | ✅ | 2 |
| Formats (`%date:fmt%`, `%amount:c2%`), built-ins (`%time%`, `%unixtime%`, `%lf%`...) | ✓ | ✓ | ✅ | 2 |
| Inline functions `$math()$`, `$length()$`, `$parse()$` | ✓ | ✓ (+ text helpers) | ✅ | 2 |
| Global Variable Viewer (persisted / temporary / per-user tabs) | ✓ | ✓ | ✅ | 2 |

## 3. Commands, timers, users

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Commands manager (aliases, Start/Exact/Anywhere, regex, sources, case) | ✓ | ✓ | ✅ | 2 |
| Permissions allow/deny users & groups; global/user cooldowns; cooldown trigger; counters | ✓ | ✓ | ✅ | 2 |
| Timers (random interval, min chat lines, enable/disable sub-action), cron | ✓ | ✓ | ✅ | 2 |
| User groups (triggers, sub-actions), present viewers, first words | ✓ | ✓ (present viewers inferred from chat) | ✅ | 2 |
| Points / currency system, leaderboard [1.1α] | ✓ | Multiple named currencies (the first is the default; a version-1 `points.json` loads as it unchanged, balances kept), each with its own user variable, earn rates, bonuses, cap, thresholds and multipliers (moderator, VIP, subscriber tier 1/2/3, user groups, per user; highest applies, then the user's own). Sub-actions, threshold trigger and script functions take an optional currency (empty = default, so existing actions are unchanged), plus award to group / present viewers and get/set multipliers. Not yet: payout rules limited to platforms/groups, tier remembered across restarts | ✅ | 4 |
| Quotes, Credits | ✓ | Quotes (CRUD, random, import/export) and per-stream credits from every platform | ✅ | 4 |

## 4. Platforms

| Platform | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Twitch: 131 triggers, 95 sub-actions (rewards, polls, predictions, moderation, raids, ads, hype train, shoutouts, shared chat, whispers, bot account) | ✓ | 126/131 triggers, 95/95 sub-actions, 69 EventSub subscriptions, bot account; Guest Star (Twitch beta API), pinned messages, sub counter with rollover, reward counters/groups/Configure Rewards, pyramids, watch streaks, modiversaries, shared chat moderation, upcoming ad warnings. No public Twitch API for the other 5: Ad Mid Roll and PubSub Connected/Disconnected (PubSub is shut down; Streamer.bot dropped them too), Community Goal Contribution/Ended (not in Helix or EventSub). Reward "Update" has no "reset to original". New scopes (`channel:manage:guest_star`, `moderator:read:moderators`, `moderator:read:vips`) need a new sign-in | ✅ | 3 |
| YouTube: 24 triggers, 9 sub-actions (chat, super chats/stickers, members, broadcasts) | ✓ | All inventory triggers + polls/deletes/quota, 18 sub-actions; own Google Cloud OAuth client; polling (quota-aware) | ✅ | 3 |
| Kick: 18 triggers, 14 sub-actions | ✓ (via Streamer.bot website webhook relay) | 18/18 triggers (+ unban, delete, host, connection events), 14/14 sub-actions (+ delete message and 13 channel-reward sub-actions: create, delete, info, enabled/paused/title/cost/description/colour/input/skip queue, accept/reject one or all pending); rewards list/create/enable/pause/delete on the Kick page (reconnect needed for the new `channel:rewards:write` scope); 7TV emote triggers in real time via 7TV's EventAPI; official API + unofficial real-time chat (kick.com's Centrifugo server); webhook parser ready for the relay; reward groups/counters missing | ✅ | 3 |
| Rumble: chat, follows, subs, rants, live state | — | In progress | ➕ | 1 |
| Combined chat (send to one/all, platform badges) + activity feed | ✓ (Streamer.bot Chat) | Twitch, YouTube, Kick, Rumble in one chat and feed | ✅ | 1–3 |
| Chat extras: emotes (Twitch/YouTube/7TV/BTTV/FFZ), quick actions, slash commands, OBS chat/event docks | ✓ | 7TV/BTTV/FFZ/Twitch/Kick emotes inline, highlights, autocomplete, slash commands, quick actions, message menu, `/feed/chat` + `/feed/events` docks; animated emotes show first frame | ✅ | 3 |

## 5. Stream apps and integrations

| Integration | Status | Phase | Integration | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| OBS Studio (9 triggers, 29 sub-actions) | ✅ 36 triggers, 47 sub-actions, multiple connections | 3 | Streamlabs Desktop | 🟡 8 triggers, 14 sub-actions (some calls unverified) | 3 |
| Meld Studio (8 triggers, 10 sub-actions + 2 newer) | 🟡 8/8 triggers + all documented Event types (+ track gain, scene staged, object removed), 12/12 sub-actions + 12 extras (gain, monitoring, staging, clips, replay, virtual camera, media, widget events, set property); official WebChannel API (`src/Cuebot.StreamApps`); verified against Meld's WebChannelAPI.md, Qt's WebChannel protocol and Meld's own Stream Deck plugin (2026-10-28; see PROTOCOL.md), not yet tried against the real app | 5 | VTube Studio | ✅ 11/11 triggers, 10/10 sub-actions (+ extras); live-tested against the real app | 5 |
| Voicemod | ✅ 4/4 triggers, 11/12 sub-actions (Set Censor State removed from Voicemod V3); live-tested against the real app | 5 | Streamlabs / StreamElements donations | 🟡 tips, merch, store, relayed events, SE points, SE dashboard emulations (legacy socket); both connect live; event handling, Astro handshake and REST paths verified against the official docs and their example payloads (2026-10), not yet tried with a real donation; Streamlabs merch/loyalty/charity/Prime payloads are undocumented | 4 |
| Ko-fi (5), Patreon (5 + 2 extra), Fourthwall (13), Shopify (2), Throne (3), Custom Webhooks | ✅ all documented triggers, signatures verified (Throne: Ed25519 with Throne's public key, optional URL token), dedupe on each service's documented id, feed + credits, test events (`src/Cuebot.Webhooks`); payload handling checked against official docs and real captured test deliveries (Ko-fi test transaction id, Shopify test order, Throne envelope, Patreon tiers/gifts, Fourthwall dual signatures); the relay path is live-tested end to end (custom webhook), the services themselves not yet tried with real deliveries from Cuebot | 5 | | | |
| Pulsoid, HypeRate | ✅ + threshold triggers; not yet tried against the real services | 5 | StreamLoots | ✅ 3/3 triggers (Pack Purchased, Pack Gifted incl. community gifts, Card Redeemed) + "Other alert" for Reaction and future types, test alerts, feed (card redemptions in credits); SSE alerts stream with keep-alive watchdog (StreamLoots has no public API or webhooks); stream endpoint checked live, no real alert received yet | 5 |
| Crowd Control | ✅ 9/9 triggers + refund, retry, session pause; Get session / Send test event sub-actions; overlay-key PubSub like Crowd Control's own overlay (private topic: prices, coin purchases); key refusal checked live, no real effect received yet; effect control needs a Crowd Control application id | 5 | Elgato (Stream Deck, Camera Hub, Control Center, Wave Link, Studio: 105 triggers) | Stream Deck: 🟡 4/4 triggers (Stream Deck Action for keys, dials and touch, Connected, Disconnected, Info), 7/7 sub-actions by button id, connected-apps list + status; Cuebot's own plugin (`streamdeck/`, Elgato SDK v2: run/toggle actions, set globals, trigger keys and dials) over `/api/v1/streamdeck` with the REST API token (`src/Cuebot.StreamDeck`); tested against a simulated Stream Deck app, not yet the real one; Streamer.bot's own plugin cannot connect (undocumented protocol); MCP Actions (StreamDeckMcp, Execute MCP Action) not yet. / Control Center: ✅ 9/9 triggers (+ colour changed, device reachable/unreachable), 3/3 sub-actions (+ Light Strip colour, identify, get device info), talking straight to the lights' HTTP API (port 9123) with built-in mDNS discovery and manual IPs, polled for changes; Windows and Linux, Control Center not needed (`src/Cuebot.Elgato`); not yet tried with a real light / Camera Hub: 🟡 24/68 triggers and 11/34 sub-actions (+ auto focus, auto exposure, get settings): webcam settings through the camera's UVC controls on Linux (V4L2), no Camera Hub needed; the Camera Hub app (Windows/macOS) has no public API or plugin system (see ELGATO_SDKS.md), so Prompter, EpocCam, dynamic range, noise reduction and white-balance tint are ❌ and Windows webcam control is not built yet; not yet tried with a real Facecam / Studio: ❌ no public control API (Cuebot only detects the running app) / Wave Link: 🟡 19/19 Wave Link 3 triggers + 20/20 legacy 1.x/2.x events (mapped onto the same triggers where they overlap), 21/21 + 2 Wave Link 3 and 16/16 legacy sub-actions, script functions; auto-detects version and port (ws-info.json, 1884-1893, 1824-1834), reconnects, opt-in throttled level meters; "not available" on Linux (`src/Cuebot.WaveLink`, see its PROTOCOL.md); tested against fake servers only, not yet against the real app | 5 |
| TipeeeStream (Donation + relayed follow/sub/cheer/raid/host), DonorDrive / Extra Life (Donation, Incentive, Profile Updated), TreatStream (Treat, Authenticated), Pally.gg (Campaign Tip) | 🟡 all documented triggers and variables, connection status, auto-connect, feed + credits + speech, test events, script functions (`src/Cuebot.Donations`, see its PROTOCOL.md). Endpoints, auth, payloads and limits verified against each service's official docs and example payloads (2026-10; see PROTOCOL.md), Pally.gg alert replays published with `isReplay`, DonorDrive `donations` is the donation list as Streamer.bot documents it. DonorDrive read live through Cuebot (public Extra Life team); none tried with a real donation, TipeeeStream, TreatStream (OAuth with the user's own app) and Pally.gg not yet against the real services | 5 | | | |
| PolyPop (1 sub-action) | 🟡 Trigger Alert + connected/disconnected/message triggers via the community WebSocket plugin (Cuebot listens, or connects out); PolyPop looks dormant (plugin last released 2022); not yet tried against the real app | 5 | | | |
| T.I.T.S. (8 triggers, 6 sub-actions) | 🟡 8/8 triggers (+ Disconnected) with Streamer.bot's variables, 6/6 sub-actions (+ get items/triggers), both C# methods as script functions; public TITSAPI WebSocket (`/websocket` + `/events`, port 42069); verified against the TITSAPI README (2025-10-13 revision, with fallbacks for older builds; 2026-10-28), not yet tried against the real app | 6 | Lumia Stream (3 sub-actions) | 🟡 3/3 sub-actions (Send Command, Set Color, Set To Default) + alert, chat-bot message, TTS, Lumia/stream-mode state, variables, raw Send API; extra triggers (chat, commands, alerts, states, other events); official Developer API (REST + event WebSocket, token), Set Color "Skip Queue" included (`skipQueue`, from Lumia's lumia-types and SDKs); verified against Lumia's Developer-Docs, MCP server and lumia-types (2026-10-28), not yet tried against the real app | 6 |
| mtion studio (2 sub-actions) | 🟡 2/2 sub-actions (Send Trigger, Get Active Clubhouse) with Streamer.bot's variables + trigger list, started/closed/clubhouse-changed/trigger-fired triggers; official local developer API (port 35393; docs last updated 2024-04-17, the product is now "mtion worlds", endpoints may have changed); verified against mtion's External Triggers docs (2026-10-28), not yet tried against the real app | 6 | OBSBOT | 🟡 Streamer.bot documents nothing yet; Cuebot covers OBSBOT Center's official OSC command set over UDP or TCP: 23 sub-actions (PTZ, zoom, presets, AI tracking, image settings, Meet modes, recording, snapshot, state, raw OSC), 8 triggers; Linux via a Tail 2 / Tail Air's own OSC ("direct" series following OBSBOT's Tail 2 / Tail Air sheets) or Center on another PC (no Linux OBSBOT Center); verified against OBSBOT's Center (rev. 2026-05-08), Tail 2 and Tail Air OSC definitions (2026-10-28), not yet tried with a real camera | 6 |
| StreamFog (9 sub-actions) | ⛔ out of scope (removed from the requirements, 2026-10-28): no public API, Streamer.bot uses StreamFog's private "Remote Key" partnership protocol; Cuebot's StreamFog tab suggests showing/hiding StreamFog's OBS source instead | — | Pronouns (alejo.io) | ✅ | 3 |
| Streamer.bot Decks (10 sub-actions) | ✅ Cuebot Decks: pages of buttons served at `/deck/<id>` by the HTTP server (any browser, phone or OBS dock) with live updates, the 10 deck sub-actions, a "Deck item pressed" trigger and a Decks editor page; runtime changes are not persisted; no streamer.bot website account or sharing | 7 | Discord (Basic Webhook), IFTTT (Notification Basic / Advanced) | ✅ outgoing sub-actions (`src/Cuebot.Webhooks/Outgoing`); Discord returns `messageId`; IFTTT uses IFTTT's Webhooks service (applet "Receive a web request") instead of Streamer.bot's website; no app field for the IFTTT key yet; not yet tried against the real services | 7 |
| GoXLR / GoXLR Mini (no Streamer.bot integration) | ➕ 17 triggers (utility and device connected, volume, fader channel and mute, cough, profile and mic profile, effects on/off, effect preset, sampler bank and playback, hardware button pressed/released, routing) with filters, 20 sub-actions (volume, fader mute by fader or channel, fader channel and mute function, cough, load profile / mic profile, FX / Megaphone / Robot / Hard Tune, effect preset slot or library preset, sampler bank and play/stop, routing, global / fader / button colours, animation, scribble text, get status, raw command) and 20 script functions, through the open-source GoXLR Utility's local WebSocket API (port 14564, JSON commands + JSON-patch status updates; Linux, Windows, macOS); TC-Helicon's own app has no public API; reconnects; GoXLR tab on the Integrations page (`src/Cuebot.GoXlr`, see its PROTOCOL.md); tested against a fake utility, not yet a real GoXLR | 5 | | | |

Webhook-based services (Kick events, Ko-fi, Patreon, ...) need a publicly reachable endpoint. Streamer.bot relays them
through its website. **Cuebot's decision:** use each service's direct API (WebSocket, polling or SDK) wherever one exists;
only where a service delivers events exclusively by webhook, use a **self-hosted Cloudflare relay** shipped in `relay/`:
a Worker + Durable Object (free Cloudflare plan) that forwards each raw webhook to Cuebot over an outbound,
token-authenticated WebSocket (no port forwarding; up to 500 deliveries / 24 h buffered while Cuebot is offline),
deployed with `relay/setup.ps1` or `relay/setup.sh`. The relay does not verify signatures: each `IWebhookHandler` in
Cuebot does, with secrets that stay on the PC. See `relay/README.md`. Live-tested: a webhook posted to a deployed relay reaches
Cuebot and fires its trigger; a wrong token is rejected (`RelayLiveTests`).

## 6. System, input and services

| Area | Status | Phase | Area | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| Run program, delay, log | ✅ | — | Hotkey triggers (Windows, XDG portal, X11); Keyboard Press sub-action (Windows SendInput; Linux XDG RemoteDesktop portal, asks once, X11 XTest fallback); Mouse Click trigger (Windows low-level hook; X11 XInput 2 raw events). See note below | ✅ | 4 |
| Play sound (file; WAV/MP3/OGG/FLAC, mixed) | ✅ | 2 | Play from folder, stop sound, output device selection | ✅ | 2 |
| File/folder watcher, file tails, process watcher, clipboard get/set/changed | ✅ | 2–4 | Toast notifications + activation (in-app, notify-send) | ✅ | 4 |
| MIDI in/out | 🟡 all of 1.13 built (WinMM, ALSA sequencer; see src/Cuebot.Midi/PROTOCOL.md); verified on Windows output only, MIDI input and Linux not yet tested on hardware | 5 | Voice control (speech-to-text, Whisper) | 🟡 module done: Whisper (Win+Linux) and Windows Speech engines, commands, both triggers, sub-actions, model download; verified end to end on Windows (live test), Linux native load untested; page not yet wired into the app | 5 |
| Game-controller input [1.1α] | 🟡 Button / Axis / Hat triggers via SDL3 (Windows + Linux, background input, hot-plug, gamecontrollerdb override, deadzones, axis throttling) plus extras (Button Combo, Connected/Disconnected, rumble, LED, state sub-actions, scripts, learn); SDL starts live on Windows, not yet tried with a real controller or on Linux; variables are Cuebot's (Streamer.bot documents none) | 5 | File read/write, HTTP request, JSON sub-actions | ✅ | 2 |

**Input on Wayland.** Key presses work on Wayland through the RemoteDesktop portal (KDE Plasma, GNOME, and other
desktops whose portal offers a remote keyboard); the desktop shows a permission dialog at the first press and the
permission is kept with a restore token (portal version 2+; older portals ask once per Cuebot start). **Mouse Click
triggers are not possible for native Wayland applications:** Wayland only tells a program about the pointer over its
own windows, and no portal reports clicks made elsewhere (the InputCapture portal takes the input away from other
applications instead of observing it). Under a Wayland session the X11 backend still sees clicks in X11 (XWayland)
windows; with no X server the trigger reports why it is unavailable. The mouse hook only runs while an enabled action
has an enabled Mouse Click trigger.

## 7. Servers, APIs and scripting

| Area | Status | Phase | Notes |
| --- | --- | --- | --- |
| C# code sub-action | ✅ | — | Roslyn; `Cb` API |
| Python code sub-action + package manager | ➕ | — | beyond Streamer.bot |
| **CPH compatibility layer** (Streamer.bot C# method names) | ✅ | 7 | All 202 `CPH` methods in Streamer.bot's C# reference with the documented signatures: 196 work through Cuebot's modules, 6 compile but log "not supported" and return the failure value (the three suspicious-user methods, `TwitchGetChannelPointsUsedByUserId`, `TwitchBlockUser(ById)`); full `EventType` (508) and `EventSource` (46) and every documented class/enum; Newtonsoft.Json bundled; Execute C# Code has code name, description, precompile / delayed start, save result to variable and custom DLL references (only listed paths, nothing downloaded); Execute C# Method sub-action and `CPH.ExecuteMethod`. 135/141 docs examples compile (the other 6 have bugs). Plus 284 methods documented only on sub-action pages, in changelogs or in the old wiki: 245 work (quotes, points, Decks, credits, first words, pronouns, toasts, sound folder, OBS media/replay/colour/mute/groups/batches with every OBS connection, Streamlabs Desktop `Slobs*`, Twitch polls/predictions/raids/shoutouts/tags/VIP/mod/chat modes/shield/reward editing/teams/Guest Star, Stream Deck, VTube Studio, Voicemod, Wave Link, Lumia, MIDI, T.I.T.S.), 39 are stubs (Trovo and VStream (31), `TwitchUnblockUser(ById)`, `TwitchGetBitsDonatedByUserId`, `SetChannelGameById`, `ObsDisconnect`, `SlobsDisconnect`, `SlobsPause/ResumeRecording`); all 582 published signatures compile as calls. Points methods have no published signatures, so theirs are inferred from the sub-action pages. Not available: .NET Framework-only APIs (Windows Forms) |
| WebSocket server (every documented and official-client request, ~500 event types, auth) | ✅ | 7 | Tested with the official `@streamerbot/client` 2.0.1: a node test connects with a password, subscribes through `client.on()`, receives every documented event and calls every client request, checking responses and Twitch payloads against the client's own TypeScript types (skipped without node; CI runs the same shapes from `Fixtures/streamerbot-documented-shapes.json`). Events use Streamer.bot's exact names with the documented nested `data` for all 43 Twitch events the client types (incl. rewards created/updated/deleted, chat cleared, polls with `channel_points_voting`, prediction `top_predictors`, charity dates, cheermotes, 7TV/BTTV/FFZ emotes in `emotes`) and the wiki's YouTube, command, timer, heart-rate, StreamElements, Streamlabs, DonorDrive, file watcher (with the file's `lines`/`data`), OBS and Shopify payloads; Twitch users carry cached pronouns; custom events also go out as `Custom.Event` for `customEventResponse`. GetBroadcaster has the Twitch/YouTube/Kick accounts, SendMessage honours `bot`, `internal`, `replyId`, `broadcastId`, emote lists come from the emote sets, GetUserPronouns from alejo.io, GetMonitoredYouTubeBroadcasts from the YouTube module; GetGlobal/named GetUserGlobal answer in both the reference's and the client's shape; Trovo*GetUserGlobal(s) answer empty. Cannot match: badge `imageUrl`s (need Twitch's badge API), hype train `last_contribution` (EventSub v2 dropped it; sent empty), community goals (Twitch retired them), YouTube `internal` (YouTube echoes are never internal). HTTP server ✅ (incl. the documented GetCredits shape), UDP DoAction ✅ |
| HTTP server (6 endpoints), UDP server (`DoAction`) | ✅ | 3 | localhost-only and off by default; origin checks |
| Custom WebSocket servers/clients | ✅ | 3 | triggers, sub-actions, script functions |
| Import/export (own format) + Streamer.bot import string importer | 🟡 | 4 | Cuebot bundles ✅ (string + .cuebot file, preview, conflicts, remapping). Streamer.bot strings: every export layout seen (format v3–v24, flat and nested groups, old and new if/else); 58 sub-action and 30 trigger type codes mapped from public exports only (docs, starter pack, 157 community exports; see `docs/streamerbot-import-codes.md`), about 98% of sub-actions and 72% of triggers on the public samples; C# source recovered and always imported disabled; 14 sub-action and 15 trigger codes are medium confidence (flagged in the import report) until checked against a real Streamer.bot; unknown codes import as disabled placeholders (extendable via `streamerbot-mapping.json`) |
| Local REST API (`/api/v1`, OpenAPI 3.1, SSE events) + MCP server (2025-06-18; 32 tools, resources, prompts; HTTP + `--mcp-stdio`) | ➕ ✅ | 5 | off by default, localhost, bearer token, confirmation for destructive tools |
| In-app AI assistant (user connects their own AI provider) | ➕ | 6 | builds on the MCP tools |
| Crash / problem reporting with info gatherer | ➕ ✅ | 5 | crash file + next-start banner, scrubbed report preview, GitHub issue link or `gh` |


### Streamer.bot compatibility (batch 7, 2026-10-28)

`docs/PARITY_REVIEW.md` compares Cuebot item by item with Streamer.bot's public docs and old wiki; batch 7 closed most of
what it found. Streamer.bot's names are **added** next to Cuebot's own, so existing Cuebot actions keep working.

- **Variable names.** Twitch, YouTube and Kick triggers and sub-actions set Streamer.bot's documented names
  (`%rewardName%`, `%rawInput%`, `%cumulative%`, `%gifts%`, `%poll.choice0.title%`, `%charity.amount.value%`,
  `%createdByUsername%`, `%raidUserName%`, `%createClipUrl%`, `%followAgeLong%`, ...): Twitch ~64/74 flagged triggers and
  29/35 sub-actions, YouTube 10/12 + 2/2, Kick 11/13 + 12/12. Integrations too: OBS `props.*`/`currentScene`/`success`,
  Streamlabs Desktop `sd.*`, Crowd Control `effect.*`/`game.*`/`gamePack.*`, Fourthwall `fw.*`, Patreon, Pulsoid/HypeRate
  `measuredAt`, Voicemod `voice.*`/`soundboard.*`, VTube Studio `clickPosition.*`/`model.*`. Not set where the platform
  APIs lack the data (e.g. Twitch `multiMonthTenure`, `bonusGifts`, `shoutoutId`; YouTube message `parts`/`emotes`).
- **Engine and core.** Queue sub-actions (clear, blocking, pause), Set Trigger / Sub-Action State, Set Sub-Action Weight,
  Get/Set Action Group State, Trigger Custom Event, Pick Color; Get Input is Streamer.bot's dialog (the splitter is
  "Split text into inputs"); Random Number Next Float; Delay random range; If/Else regex captures and exists / does not
  exist; local signals with arguments, queueing and Overwrite Variables; Streamer.bot's narrowest-range rule with a
  per-trigger Always Run; `rawInputEscaped`/`rawInputUrlEncoded`/`inputEscaped#`/`inputUrlEncoded#` and
  `%runningActionId%` on every run; global-variable triggers with `global.*`; Cron trigger.
- **Commands and points.** `commandSource`, `commandType`, `msgId`, `match.*`, cooldown `*Left` variables; a command's
  `rawInput` is now the text after the command (the whole message stays in `message`); cooldown Reset restarts at full
  length and Remove ends it. Points: Check, Delete, Reset User, Get Points Definition, Award to All, leaderboard offset,
  multipliers for one user. Groups and First Words set the documented outputs.
- **System.** Read Lines / Read Specific Line / Read Random Lines (count), UDP Broadcast, Run a Program with environment
  variables and output capture, Fetch URL parse as JSON, Play Sound `soundLength`; Streamer.bot names on key press, mouse,
  process, file watcher, clipboard and toast triggers. `/shutdown` startup switch.
- **New sub-actions.** Streamlabs Desktop: scene filter state, random filter state, hide source's filters, hide group's
  sources, random group source (19 sub-actions now; some calls unverified). Discord Basic Webhook, IFTTT.
- **Still different.** Camera Hub Set Dynamic Range / Noise Reduction / White Balance Tint ❌ (vendor-only controls behind
  Camera Hub, not UVC; re-checked 2026-10-28: Elgato offers no Camera Hub SDK, API or plugin system, so no Camera Hub
  plugin can expose them, and the Facecam's extension units are undocumented; see [ELGATO_SDKS.md](ELGATO_SDKS.md)); cloud backups ❌ (no Cuebot cloud account); `$math()$` uses Cuebot's evaluator, not mXparser;
  keyboard shortcuts list and Speaker.bot "Allow Moderator Whispers" not done; see PARITY_REVIEW.md for the full list.

## 8. Speaker.bot (text-to-speech)

| Area | Speaker.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Engines: SAPI5, Google, Azure, Amazon Polly, IBM Watson, UberDuck, TTS Monster, Acapela, CereProc, ElevenLabs | 10 | All 10 + espeak-ng (➕ Linux); cloud engines not yet tested against the real services | ✅ | 2 |
| Voice aliases with several voices (random pick), per-alias volume/rate/pitch | ✓ | ✓ | ✅ | 2 |
| Per-user voice/alias, nickname pronunciation | ✓ | ✓ | ✅ | 2 |
| Viewer `!tts` commands (18: voice, speed, pitch, nick, on/off, skip, clear, ...) | ✓ | All 18 + aliases, including `set sticky` and `assign last` (pins the last heard voice, creating an alias for random picks) | ✅ | 2 |
| Filters: bad words (strip/skip), URLs, regex replace, max words/chars, emote stripping (6 sources), ignored prefixes, ignore users/bots, repeated chars | ✓ | ✓ (emotes: platform ranges + 7TV/BTTV/FFZ names) | ✅ | 2–3 |
| Ignore profiles; stop/skip on message delete, timeout, ban; shared-chat ignore | ✓ | ✓ (neutral moderation events from Twitch, YouTube, Kick) | ✅ | 2–3 |
| Event speech with templates (16 vars) from Twitch, Streamlabs, StreamElements | ✓ | Every platform + Streamlabs/StreamElements tips (Tip template) and merch (Merch template, + `%product%` / `%quantity%`) | ✅ | 2–4 |
| Queue: pause, skip, clear, max queue, save to WAV, silent speak with duration | ✓ | ✓ (+ remove one queued text) | ✅ | 2 |
| Audio output device selection (WASAPI), VoiceGate (mic level gate) | ✓ | ✓ (miniaudio on Windows and Linux) | ✅ | 2 |
| WebSocket API (port 7580) and UDP API (port 6669), Streamer.bot Speaker.bot sub-actions | ✓ | ✓ incl. UDP single-event toggles (`events` with an event name), `set sticky` and `assign last`; the WebSocket API also takes `Events` with `value`, `Set` and `Assign` | ✅ | 3 |

## 9. App shell

| Area | Status | Phase |
| --- | --- | --- |
| Windows + Linux native, Wayland-first, AppImage/Flatpak/AUR | ➕ | — |
| Tray icon, minimize to tray, start minimized, headless mode, startup switches, single instance | ✅ | 2 |
| Automatic backups (on start / before update), restore | ✅ | 2 |
| Update checker / release channels | ✅ (stable + pre-release; notifies, no auto-install) | 5 |
| Light/dark theme toggle, keyboard shortcuts | ✅ themes; shortcuts Ctrl+S save, F1 support, F5 test trigger, Ctrl+Alt+I/E import/export, Ctrl+Alt+C chat, Ctrl+Alt+V variables, Ctrl+Alt+L log folder; Actions page Delete, Ctrl+D, Ctrl+O, Ctrl+Up/Down, copy/paste, undo/redo (listed under Settings > About) | 4 |

## Delivery waves

1. **Done:** combined chat + activity feed, Rumble module.
2. **Done — core parity:** queues, logic/groups, variables & inline functions, commands manager, users/groups, timers, history,
   copy/paste/undo, sounds + audio devices, file/HTTP/JSON sub-actions, tray/headless/backups, and the Speaker.bot
   feature set (engines, aliases, per-user voices, `!tts`, filters, event speech, audio backend with device choice).
3. **Done (gaps in the tables above) — platforms & APIs:** full Twitch, YouTube, Kick; OBS completion; Streamer.bot-compatible WebSocket/HTTP/UDP servers,
   CPH compatibility layer, Speaker.bot-compatible API; chat extras.
4. **Done — services:** points (multiple currencies, multipliers), quotes, credits, hotkeys, keyboard/mouse input, toasts, donations,
   import/export, action history and copy/paste/undo, app shortcuts; Streamer.bot type codes mapped from public exports (medium-confidence codes still to confirm).
5. **In progress — integrations:** VTube Studio ✅, Voicemod ✅, Pulsoid/HypeRate ✅, Cloudflare relay + Ko-fi/Patreon/Fourthwall/Shopify/Throne/custom webhooks + Kick webhooks ✅, StreamLoots ✅, Crowd Control ✅, TipeeeStream/DonorDrive/TreatStream/Pally.gg 🟡, Meld Studio + PolyPop + T.I.T.S. + Lumia + mtion + OBSBOT 🟡, StreamFog ❌ (no public API); Elgato (Stream Deck, Wave Link, lights, Camera Hub on Linux) 🟡, MIDI 🟡, voice control 🟡, game controllers 🟡 (built, not yet tried on real hardware); remote signals between instances ✅. Batch 7 (Streamer.bot compatibility from the docs review) ✅; see §7.
6. **Beyond parity:** MCP server + REST API ✅, crash/problem reporting ✅, update checker ✅; next: in-app AI assistant.

Each wave ships with tests and updates this file; live tests cover anything that talks to a real service.
