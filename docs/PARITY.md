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
| Triggers → actions, many-to-many, test run | ✓ (+ Test Trigger with custom args, Trigger Viewer) | Test trigger with custom args (engine); Trigger Viewer missing | 🟡 | 2 |
| Named action queues (blocking, pause/resume, clear, always-run, skip queue) | ✓ | Named queues, blocking, pause/resume/clear, always-run (skip-queue for rewards pending) | ✅ | 2 |
| Random action / weights, concurrent sub-actions | ✓ | ✓ | ✅ | 2 |
| Sub-action groups (nestable, coloured, random) | ✓ | ✓ | ✅ | 2 |
| Logic: If/Else, Switch, While, Loop Break/Continue, Break, Get Input, Random Number | ✓ | ✓ | ✅ | 2 |
| Signals: local/remote send & wait | ✓ | Local ✓, remote missing | 🟡 | 3 |
| Pending actions + Action History (inspect variables, requeue) | ✓ | ✓ (engine); page in progress | 🟡 | 2 |
| Node-graph editor | — | ✓ | ➕ | — |
| Copy/paste triggers, sub-actions, actions; undo/redo | ✓ | — | ❌ | 2 |

## 2. Variables and text

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| `%arg%` arguments, globals (persisted/temporary) | ✓ | ✓ | ✅ | — |
| Per-user globals (per platform) | ✓ | ✓ | ✅ | 2 |
| Increment/decrement, auto type | ✓ | ✓ | ✅ | 2 |
| Formats (`%date:fmt%`, `%amount:c2%`), built-ins (`%time%`, `%unixtime%`, `%lf%`...) | ✓ | ✓ | ✅ | 2 |
| Inline functions `$math()$`, `$length()$`, `$parse()$` | ✓ | ✓ (+ text helpers) | ✅ | 2 |
| Global Variable Viewer (persisted / temporary / per-user tabs) | ✓ | Single list | 🟡 | 2 |

## 3. Commands, timers, users

| Area | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Commands manager (aliases, Start/Exact/Anywhere, regex, sources, case) | ✓ | ✓ | ✅ | 2 |
| Permissions allow/deny users & groups; global/user cooldowns; cooldown trigger; counters | ✓ | ✓ | ✅ | 2 |
| Timers (random interval, min chat lines, enable/disable sub-action), cron | ✓ | ✓ | ✅ | 2 |
| User groups (triggers, sub-actions), present viewers, first words | ✓ | ✓ (present viewers inferred from chat) | ✅ | 2 |
| Points / currency system, leaderboard [1.1α] | ✓ | — | ❌ | 4 |
| Quotes, Credits | ✓ | — | ❌ | 4 |

## 4. Platforms

| Platform | Streamer.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Twitch: 131 triggers, 95 sub-actions (rewards, polls, predictions, moderation, raids, ads, hype train, shoutouts, shared chat, whispers, bot account) | ✓ | 11 triggers, 1 sub-action, EventSub + Helix chat | 🟡 | 3 |
| YouTube: 24 triggers, 9 sub-actions (chat, super chats/stickers, members, broadcasts) | ✓ | Stream push only | ❌ | 3 |
| Kick: 18 triggers, 14 sub-actions | ✓ (via Streamer.bot website webhook relay) | Stream push only | ❌ | 3 |
| Rumble: chat, follows, subs, rants, live state | — | In progress | ➕ | 1 |
| Combined chat (send to one/all, platform badges) + activity feed | ✓ (Streamer.bot Chat) | ✓ (new Chat page) | 🟡 | 1–3 |
| Chat extras: emotes (Twitch/YouTube/7TV/BTTV/FFZ), quick actions, slash commands, OBS chat/event docks | ✓ | — | ❌ | 3 |

## 5. Stream apps and integrations

| Integration | Status | Phase | Integration | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| OBS Studio (9 triggers, 29 sub-actions) | 🟡 7 + 7 | 3 | Streamlabs Desktop | ❌ | 5 |
| Meld Studio | ❌ | 5 | VTube Studio | ❌ | 5 |
| Voicemod | ❌ | 5 | Streamlabs / StreamElements donations | ❌ | 4 |
| Ko-fi, Patreon, Fourthwall, Shopify, Throne, TipeeeStream, DonorDrive, StreamLoots, TreatStream | ❌ | 5 | Pulsoid, HypeRate | ❌ | 5 |
| Crowd Control | ❌ | 5 | Elgato (Stream Deck, Camera Hub, Control Center, Wave Link, Studio: 105 triggers) | ❌ | 5 |
| T.I.T.S., Lumia, Pally.gg, StreamFog, Mtion, PolyPop, OBSBOT | ❌ | 5 | Pronouns (alejo.io) | ❌ | 4 |

Webhook-based services (Kick events, Ko-fi, Patreon, ...) need a publicly reachable endpoint. Streamer.bot relays them
through its website. **Cuebot's decision:** use each service's direct API (WebSocket, polling or SDK) wherever one exists;
only where a service delivers events exclusively by webhook, use a **self-hosted Cloudflare relay** shipped in `relay/`:
a Worker + Durable Object that verifies each service's webhook signature and forwards events to Cuebot over an
outbound, token-authenticated WebSocket (no port forwarding; short offline buffering), deployed with `wrangler deploy`.

## 6. System, input and services

| Area | Status | Phase | Area | Status | Phase |
| --- | --- | --- | --- | --- | --- |
| Run program, delay, log | ✅ | — | Hotkey/mouse triggers, keyboard press output (cross-platform hook) | ❌ | 4 |
| Play sound (file; WAV/MP3/OGG/FLAC, mixed) | ✅ | 2 | Play from folder, stop sound, output device selection | ✅ | 2 |
| File/folder watcher, file tails, process watcher (clipboard pending) | 🟡 | 2 | Toast notifications + activation | ❌ | 4 |
| MIDI in/out | ❌ | 5 | Voice control (speech-to-text, Whisper) | ❌ | 5 |
| Game-controller input [1.1α] | ❌ | 5 | File read/write, HTTP request, JSON sub-actions | ✅ | 2 |

## 7. Servers, APIs and scripting

| Area | Status | Phase | Notes |
| --- | --- | --- | --- |
| C# code sub-action | ✅ | — | Roslyn; `Cb` API |
| Python code sub-action + package manager | ➕ | — | beyond Streamer.bot |
| **CPH compatibility layer** (Streamer.bot C# method names) | ❌ | 3 | lets existing Streamer.bot scripts run unchanged |
| WebSocket server (22 requests, ~500 event types, auth) | ❌ | 3 | Streamer.bot-compatible protocol so `@streamerbot/client`, Decks-style tools and overlays work |
| HTTP server (6 endpoints), UDP server (`DoAction`) | ❌ | 3 | |
| Custom WebSocket servers/clients | ❌ | 4 | |
| Import/export (own format) + Streamer.bot import string importer | ❌ | 4 | importer maps their actions to ours where a counterpart exists |
| MCP server, local REST API, in-app AI assistant | ➕ | 6 | roadmap |
| Crash / problem reporting with info gatherer | ➕ | 6 | roadmap |

## 8. Speaker.bot (text-to-speech)

| Area | Speaker.bot | Cuebot today | Status | Phase |
| --- | --- | --- | --- | --- |
| Engines: SAPI5, Google, Azure, Amazon Polly, IBM Watson, UberDuck, TTS Monster, Acapela, CereProc, ElevenLabs | 10 | SAPI, ElevenLabs, espeak-ng (➕ Linux) | 🟡 | 2 |
| Voice aliases with several voices (random pick), per-alias volume/rate/pitch | ✓ | One voice per alias | 🟡 | 2 |
| Per-user voice/alias, nickname pronunciation | ✓ | — | ❌ | 2 |
| Viewer `!tts` commands (18: voice, speed, pitch, nick, on/off, skip, clear, ...) | ✓ | — | ❌ | 2 |
| Filters: bad words (strip/skip), URLs, regex replace, max words/chars, emote stripping (6 sources), ignored prefixes, ignore users/bots, repeated chars | ✓ | Replacements, links, length, ignore users, `!` commands | 🟡 | 2 |
| Ignore profiles; stop/skip on message delete, timeout, ban; shared-chat ignore | ✓ | — | ❌ | 2 |
| Event speech with templates (16 vars) from Twitch, Streamlabs, StreamElements | ✓ | Chat only (events via actions) | 🟡 | 2 |
| Queue: pause, skip, clear, max queue, save to WAV, silent speak with duration | ✓ | Queue, pause, skip, clear | 🟡 | 2 |
| Audio output device selection (WASAPI), VoiceGate (mic level gate) | ✓ | Default device only | ❌ | 2 |
| WebSocket API (port 7580) and UDP API (port 6669), Streamer.bot Speaker.bot sub-actions | ✓ | — | ❌ | 3 |

## 9. App shell

| Area | Status | Phase |
| --- | --- | --- |
| Windows + Linux native, Wayland-first, AppImage/Flatpak/AUR | ➕ | — |
| Tray icon, minimize to tray, start minimized, headless mode, startup switches, single instance | ✅ | 2 |
| Automatic backups (on start / before update), restore | ✅ | 2 |
| Update checker / release channels | ❌ | 4 |
| Light/dark theme toggle, keyboard shortcuts | 🟡 themes ✅, shortcuts pending | 4 |

## Delivery waves

1. **Now:** combined chat + activity feed, Rumble module.
2. **Core parity:** queues, logic/groups, variables & inline functions, commands manager, users/groups, timers, history,
   copy/paste/undo, sounds + audio devices, file/HTTP/JSON sub-actions, tray/headless/backups, and the Speaker.bot
   feature set (engines, aliases, per-user voices, `!tts`, filters, event speech, audio backend with device choice).
3. **Platforms & APIs:** full Twitch, YouTube, Kick; OBS completion; Streamer.bot-compatible WebSocket/HTTP/UDP servers,
   CPH compatibility layer, Speaker.bot-compatible API; chat extras.
4. **Services:** points, quotes, credits, donations (Streamlabs/StreamElements), pronouns, hotkeys, watchers, toasts,
   custom WebSocket servers/clients, import/export + Streamer.bot importer, updater.
5. **Integrations:** stream apps, Elgato, VTube Studio, Voicemod, MIDI, voice control and the remaining services.
6. **Beyond parity:** MCP server + REST API, in-app AI assistant, crash/problem reporting.

Each wave ships with tests and updates this file; live tests cover anything that talks to a real service.
