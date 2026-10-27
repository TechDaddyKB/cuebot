# Feature parity: Streamer.bot + Speaker.bot → Cuebot

Goal: everything Streamer.bot (1.0.7, incl. documented 1.1-alpha features) and Speaker.bot (0.1.7) do, on Windows
**and** Linux, in one app, then build beyond them. Item-level checklists come from public documentation only:
[`parity/streamerbot-inventory.md`](parity/streamerbot-inventory.md) (431 triggers, 421 sub-actions, ~200 C# methods,
22 WebSocket requests, ~38 integrations) and [`parity/speakerbot-inventory.md`](parity/speakerbot-inventory.md)
(10 TTS engines, 18 viewer commands, WebSocket + UDP APIs).

Legend: ✅ done · 🟡 partial · ❌ missing · ➕ Cuebot-only (beyond parity). Phase = planned delivery wave (below).

## 1. Actions engine and core concepts

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Actions, groups, enable/disable, duplicate | ✓ | ✓ | ✅ | — |
| Triggers → actions, many-to-many, test run | ✓ (+ Test Trigger with custom args, Trigger Viewer) | Test trigger with custom args; Trigger Viewer (History page) | ✅ | 2–4 |
| Named action queues (blocking, pause/resume, clear, always-run, skip queue) | ✓ | Named queues, blocking, pause/resume/clear, always-run (skip-queue for rewards pending) | ✅ | 2 |
| Random action / weights, concurrent sub-actions | ✓ | ✓ | ✅ | 2 |
| Sub-action groups (nestable, coloured, random) | ✓ | ✓ | ✅ | 2 |
| Logic: If/Else, Switch, While, Loop Break/Continue, Break, Get Input, Random Number | ✓ | ✓ | ✅ | 2 |
| Signals: local/remote send & wait | ✓ | Local ✓, remote missing | 🟡 | 3 |
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
| Points / currency system, leaderboard [1.1α] | ✓ | Earn rates, bonuses, spend/transfer, leaderboard, thresholds (stored as user variables); multiple currencies and multipliers pending | 🟡 | 4 |
| Quotes, Credits | ✓ | Quotes (CRUD, random, import/export) and per-stream credits from every platform | ✅ | 4 |

## 4. Platforms

| Platform | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Twitch: 131 triggers, 95 sub-actions (rewards, polls, predictions, moderation, raids, ads, hype train, shoutouts, shared chat, whispers, bot account) | ✓ | 107/131 triggers, 66/95 sub-actions, 64 EventSub subscriptions, bot account; Guest Star, pinned messages, sub counter, reward counters/groups missing | 🟡 | 3 |
| YouTube: 24 triggers, 9 sub-actions (chat, super chats/stickers, members, broadcasts) | ✓ | All inventory triggers + polls/deletes/quota, 18 sub-actions; own Google Cloud OAuth client; polling (quota-aware) | ✅ | 3 |
| Kick: 18 triggers, 14 sub-actions | ✓ (via Streamer.bot website webhook relay) | 16/18 triggers (+ unban, delete, host), 11 sub-actions; official API + unofficial real-time chat (kick.com's Centrifugo server); webhook parser ready for the relay; 7TV triggers and reward management missing | 🟡 | 3 |
| Rumble: chat, follows, subs, rants, live state | — | In progress | ➕ | 1 |
| Combined chat (send to one/all, platform badges) + activity feed | ✓ (Streamer.bot Chat) | Twitch, YouTube, Kick, Rumble in one chat and feed | ✅ | 1–3 |
| Chat extras: emotes (Twitch/YouTube/7TV/BTTV/FFZ), quick actions, slash commands, OBS chat/event docks | ✓ | 7TV/BTTV/FFZ/Twitch/Kick emotes inline, highlights, autocomplete, slash commands, quick actions, message menu, `/feed/chat` + `/feed/events` docks; animated emotes show first frame | ✅ | 3 |

## 5. Stream apps and integrations

| Integration | Status | Phase | Integration | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| OBS Studio (9 triggers, 29 sub-actions) | ✅ 36 triggers, 47 sub-actions, multiple connections | 3 | Streamlabs Desktop | 🟡 8 triggers, 14 sub-actions (some calls unverified) | 3 |
| Meld Studio | ❌ | 5 | VTube Studio | ✅ 11/11 triggers, 10/10 sub-actions (+ extras); live-tested against the real app | 5 |
| Voicemod | ✅ 4/4 triggers, 11/12 sub-actions (Set Censor State removed from Voicemod V3); live-tested against the real app | 5 | Streamlabs / StreamElements donations | 🟡 tips, merch, store, relayed events, SE points; both connect live (no real tip received yet) | 4 |
| Ko-fi, Patreon, Fourthwall, Shopify, Throne, TipeeeStream, DonorDrive, StreamLoots, TreatStream | ❌ | 5 | Pulsoid, HypeRate | ✅ + threshold triggers; not yet tried against the real services | 5 |
| Crowd Control | ❌ | 5 | Elgato (Stream Deck, Camera Hub, Control Center, Wave Link, Studio: 105 triggers) | ❌ | 5 |
| T.I.T.S., Lumia, Pally.gg, StreamFog, Mtion, PolyPop, OBSBOT | ❌ | 5 | Pronouns (alejo.io) | ✅ | 3 |

Webhook-based services (Kick events, Ko-fi, Patreon, ...) need a publicly reachable endpoint. Streamer.bot relays them
through its website. **Cuebot's decision:** use each service's direct API (WebSocket, polling or SDK) wherever one exists;
only where a service delivers events exclusively by webhook, use a **self-hosted Cloudflare relay** shipped in `relay/`:
a Worker + Durable Object that verifies each service's webhook signature and forwards events to Cuebot over an
outbound, token-authenticated WebSocket (no port forwarding; short offline buffering), deployed with `wrangler deploy`.

## 6. System, input and services

| Area | Status | Phase | Area | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| Run program, delay, log | ✅ | — | Hotkey triggers (Windows, XDG portal, X11); mouse triggers and key press output pending | 🟡 | 4 |
| Play sound (file; WAV/MP3/OGG/FLAC, mixed) | ✅ | 2 | Play from folder, stop sound, output device selection | ✅ | 2 |
| File/folder watcher, file tails, process watcher, clipboard get/set/changed | ✅ | 2–4 | Toast notifications + activation (in-app, notify-send) | ✅ | 4 |
| MIDI in/out | ❌ | 5 | Voice control (speech-to-text, Whisper) | ❌ | 5 |
| Game-controller input [1.1α] | ❌ | 5 | File read/write, HTTP request, JSON sub-actions | ✅ | 2 |

## 7. Servers, APIs and scripting

| Area | Status | Phase | Notes |
| --- | --- | --- | --- |
| C# code sub-action | ✅ | — | Roslyn; `Cb` API |
| Python code sub-action + package manager | ➕ | — | beyond Streamer.bot |
| **CPH compatibility layer** (Streamer.bot C# method names) | 🟡 | 3 | ~150 methods; `CPHInline` scripts compile unchanged; 22 methods throw NotSupported (see `docs/SERVERS.md`) |
| WebSocket server (22 requests, ~500 event types, auth) | ✅ | 3 | all requests; events mapped to Streamer.bot source/type names; verified against the real `@streamerbot/client` |
| HTTP server (6 endpoints), UDP server (`DoAction`) | ✅ | 3 | localhost-only and off by default; origin checks |
| Custom WebSocket servers/clients | ✅ | 3 | triggers, sub-actions, script functions |
| Import/export (own format) + Streamer.bot import string importer | 🟡 | 4 | Cuebot bundles ✅ (string + .cuebot file, preview, conflicts, remapping). Streamer.bot strings: the wrapper is read, but its numeric type codes are undocumented, so most items import as disabled placeholders (extendable via `streamerbot-mapping.json`) |
| Local REST API (`/api/v1`, OpenAPI 3.1, SSE events) + MCP server (2025-06-18; 32 tools, resources, prompts; HTTP + `--mcp-stdio`) | ➕ ✅ | 5 | off by default, localhost, bearer token, confirmation for destructive tools |
| In-app AI assistant (user connects their own AI provider) | ➕ | 6 | builds on the MCP tools |
| Crash / problem reporting with info gatherer | ➕ ✅ | 5 | crash file + next-start banner, scrubbed report preview, GitHub issue link or `gh` |

## 8. Speaker.bot (text-to-speech)

| Area | Speaker.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Engines: SAPI5, Google, Azure, Amazon Polly, IBM Watson, UberDuck, TTS Monster, Acapela, CereProc, ElevenLabs | 10 | All 10 + espeak-ng (➕ Linux); cloud engines not yet tested against the real services | ✅ | 2 |
| Voice aliases with several voices (random pick), per-alias volume/rate/pitch | ✓ | ✓ | ✅ | 2 |
| Per-user voice/alias, nickname pronunciation | ✓ | ✓ | ✅ | 2 |
| Viewer `!tts` commands (18: voice, speed, pitch, nick, on/off, skip, clear, ...) | ✓ | All 18 + aliases; `set sticky` / `assign last` not yet | 🟡 | 2 |
| Filters: bad words (strip/skip), URLs, regex replace, max words/chars, emote stripping (6 sources), ignored prefixes, ignore users/bots, repeated chars | ✓ | ✓ (emotes: platform ranges + 7TV/BTTV/FFZ names) | ✅ | 2–3 |
| Ignore profiles; stop/skip on message delete, timeout, ban; shared-chat ignore | ✓ | ✓ (neutral moderation events from Twitch, YouTube, Kick) | ✅ | 2–3 |
| Event speech with templates (16 vars) from Twitch, Streamlabs, StreamElements | ✓ | Every platform + Streamlabs/StreamElements tips (Tip template); merch has no template yet | 🟡 | 2–4 |
| Queue: pause, skip, clear, max queue, save to WAV, silent speak with duration | ✓ | ✓ (+ remove one queued text) | ✅ | 2 |
| Audio output device selection (WASAPI), VoiceGate (mic level gate) | ✓ | ✓ (miniaudio on Windows and Linux) | ✅ | 2 |
| WebSocket API (port 7580) and UDP API (port 6669), Streamer.bot Speaker.bot sub-actions | ✓ | ✓ (UDP single-event toggles, `set sticky`, `assign last` unsupported) | 🟡 | 3 |

## 9. App shell

| Area | Status | Phase |
| --- | --- | --- |
| Windows + Linux native, Wayland-first, AppImage/Flatpak/AUR | ➕ | — |
| Tray icon, minimize to tray, start minimized, headless mode, startup switches, single instance | ✅ | 2 |
| Automatic backups (on start / before update), restore | ✅ | 2 |
| Update checker / release channels | ✅ (stable + pre-release; notifies, no auto-install) | 5 |
| Light/dark theme toggle, keyboard shortcuts | 🟡 themes ✅, shortcuts pending | 4 |

## Delivery waves

1. **Done:** combined chat + activity feed, Rumble module.
2. **Done — core parity:** queues, logic/groups, variables & inline functions, commands manager, users/groups, timers, history,
   copy/paste/undo, sounds + audio devices, file/HTTP/JSON sub-actions, tray/headless/backups, and the Speaker.bot
   feature set (engines, aliases, per-user voices, `!tts`, filters, event speech, audio backend with device choice).
3. **Done (gaps in the tables above) — platforms & APIs:** full Twitch, YouTube, Kick; OBS completion; Streamer.bot-compatible WebSocket/HTTP/UDP servers,
   CPH compatibility layer, Speaker.bot-compatible API; chat extras.
4. **Mostly done — services:** points, quotes, credits, hotkeys, toasts, donations, import/export, action history and copy/paste/undo; still to do:
   mouse/keyboard output, multiple point currencies, Streamer.bot type-code mapping.
5. **In progress — integrations:** VTube Studio ✅, Voicemod ✅, Pulsoid/HypeRate ✅; still to do: Meld, Elgato, MIDI, voice control, Ko-fi/Patreon and the other webhook services (Cloudflare relay), the remaining apps.
6. **Beyond parity:** MCP server + REST API ✅, crash/problem reporting ✅, update checker ✅; next: in-app AI assistant.

Each wave ships with tests and updates this file; live tests cover anything that talks to a real service.
