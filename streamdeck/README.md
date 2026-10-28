# Cuebot for Elgato Stream Deck

A Stream Deck plugin for Cuebot, and the Cuebot side that goes with it (`src/Cuebot.StreamDeck`). Keys and dials run
Cuebot actions, toggle them, set global variables and fire Cuebot's **Stream Deck Action** trigger; Cuebot's Stream Deck
sub-actions change a key's title, state, image and value and flash alert/OK.

- Plugin: TypeScript on Elgato's official Node.js SDK (`@elgato/streamdeck` 2.1, manifest SDKVersion 2, Node 20 runtime
  bundled with Stream Deck 7.1+), packed with Elgato's CLI (`@elgato/cli`).
- Transport: one WebSocket to Cuebot's REST API listener, `ws://host:7475/api/v1/streamdeck`, with the REST API token.
  The message format is in [`src/Cuebot.StreamDeck/PROTOCOL.md`](../src/Cuebot.StreamDeck/PROTOCOL.md).

## Options considered

| Option | How it works | For | Against | Verdict |
| --- | --- | --- | --- | --- |
| **Own plugin on Elgato's SDK, talking to Cuebot's local API** | A normal Stream Deck plugin (Node, `@elgato/streamdeck`) connects out to Cuebot over a WebSocket with the API token | Official, documented SDK; we own both ends so keys can show state Cuebot pushes (title, state, image, value, alert/OK), dials and touch work, protocol is versioned and tested; installs like any plugin (`.streamDeckPlugin`) | One more thing to install; Stream Deck has no Linux app (Cuebot on Linux needs the deck on a Windows/mac PC over the LAN) | **Chosen** |
| Point the official Streamer.bot Stream Deck plugin at Cuebot | Streamer.bot's plugin connects to a dedicated "Stream Deck server" inside Streamer.bot (`Integrations → Elgato → Stream Deck`, default 127.0.0.1:8059 per streamdeck.streamer.bot) | Users who already have the plugin could reuse their profiles | That server's protocol is not documented (docs.streamer.bot's Stream Deck pages say "missing documentation"; the plugin docs describe only host/port). Compatibility would mean reverse-engineering the closed plugin, which the clean-room rule forbids. It is **not** Cuebot's Streamer.bot-compatible WebSocket API (port 8080), so that server does not help | Not possible cleanly; revisit if Streamer.bot documents the protocol |
| Generic WebSocket/HTTP plugins (e.g. community "API Ninja"/"WebSocket" plugins) against `/api/v1` or Cuebot's Streamer.bot-compatible WebSocket | Keys send fixed HTTP requests or WebSocket messages | No plugin to maintain; already works today with `POST /api/v1/actions/{id}/run` + bearer token | One-way only (no state on keys), no action picker, token pasted per key | Documented below as a fallback, not the integration |
| REST API + SSE (`/api/v1/events`) from our plugin | Plugin calls REST for requests and listens on SSE for pushes | No server change | Two channels, no way to tell Cuebot which keys exist (button ids, devices), so Cuebot can't count or list them; more reconnect logic | Rejected in favour of one WebSocket |
| Cuebot's MCP server | Plugin as an MCP client | Already exists | MCP is for AI clients (tools/JSON-RPC, confirmations); wrong fit for key presses and pushes | Rejected |
| Elgato's Stream Deck MCP ("MCP Actions", Stream Deck 7.4+, `@elgato/mcp-server`) | Stream Deck exposes the actions placed on its virtual "MCP Actions" profile to MCP clients through a local bridge (stdio or HTTP on :9090) | Lets Cuebot *press Stream Deck actions* (any plugin's), which is what Streamer.bot 1.1α's "Execute MCP Action" sub-action and StreamDeckMcp Connected/Disconnected triggers are | The opposite direction: it does not let keys drive Cuebot or show Cuebot's state; needs the bridge process running | Complementary; planned as a follow-up (Cuebot as an MCP client of the bridge) |
| Direct USB/HID control (e.g. `@elgato-stream-deck/node`, `streamdeck-mcp` USB mode) | Cuebot drives the hardware itself | No Stream Deck app needed (Linux!) | Needs exclusive device access (the Stream Deck app must be closed), loses every other plugin, udev rules on Linux | Out of scope; maybe a separate "headless deck" feature for Linux later |

Why a dedicated endpoint: the plugin needs both directions on one connection (requests plus pushes) and Cuebot needs to
know which keys are visible to count and list them. The endpoint is a small hook in `src/Cuebot.Server`
(`IApiWebSocketRoute`, served on the existing API listener behind the same token) rather than another port and password.

## Install

1. In Cuebot open the **Stream Deck** tab and click **Turn on the REST API** (or Servers → REST API). Note the port
   (default 7475) and copy the **API token**.
2. Install the plugin: double-click `io.github.cuebot.streamdeck.streamDeckPlugin` (from the Cuebot release or the
   `Cuebot-StreamDeck-plugin` CI artifact). Stream Deck 7.1 or newer, Windows 10+ or macOS 12+.
3. Drag any Cuebot key (category **Cuebot**) onto your deck, open **Connection** at the top of its settings and enter
   host `127.0.0.1`, the port and the token. They are stored once for all Cuebot keys (the plugin's global settings).
   The dot turns green ("Connected to Cuebot x.y.z") and the Stream Deck app appears on Cuebot's Stream Deck tab.

**Another computer / Linux:** the Stream Deck app does not run on Linux. Run it on a Windows or Mac PC and point the
plugin at the Cuebot machine: on Cuebot's Servers page allow LAN access and set the REST API address to `0.0.0.0` (or the
LAN address), then enter that machine's IP as the host. The token travels in clear text on the LAN (plain `ws://`), so
only do this on a network you trust.

**From source:** `cd streamdeck && npm ci && npm run pack` writes `dist/io.github.cuebot.streamdeck.streamDeckPlugin`.
For development, `npx streamdeck link io.github.cuebot.streamdeck.sdPlugin` once, then `npm run watch` and
`npx streamdeck restart io.github.cuebot.streamdeck` after changes.

## Actions (keys and dials)

Every Cuebot key has a **Button ID** (assigned on first use, editable, shown in its settings and on Cuebot's Stream Deck
tab). Cuebot's sub-actions target keys by it; keys sharing an id change together, on any page or device.

| Action | Keys | Dials (Stream Deck +) | Settings |
| --- | --- | --- | --- |
| **Run Cuebot action** | Runs the action on key down (or up); OK on success, alert on error (unknown or disabled action, not connected) | — | Action (picked from Cuebot's list), arguments (`name=value` lines; `%buttonId%` is added) |
| **Toggle action enabled** | Toggles, enables or disables the action; shows state 0 (green, enabled) or 1 (grey, disabled) and follows changes made in Cuebot | — | Action, press = toggle / enable / disable |
| **Set global variable** | Sets the value, adds a number or toggles true/false; optionally shows the current value | Push does the same; turning adds *step × ticks* | Variable (suggestions from Cuebot), operation, value, persisted, step, show value |
| **Cuebot trigger** | Key down and up fire **Stream Deck Action** | Turn, push, release and touch fire it (`ticks`, `pressed`, `tapX/Y`, `hold`); shows values pushed by Set Value | Arguments |

The **Set Title / Set State / Set Background / Set Value / Show Alert / Show Ok / Toggle State** sub-actions work on any
of these. Stream Deck only lets a plugin change a title or image the user has not customised in the key's own settings.

## In Cuebot

Triggers (category **Stream Deck**):

| Trigger | Variables |
| --- | --- |
| Stream Deck Action (filter: button ids, event) | `buttonId`, `buttonEvent` (keyDown, keyUp, dialRotate, dialDown, dialUp, touchTap), `controller`, `isDial`, `state`, `ticks`, `pressed`, `tapX`, `tapY`, `hold`, `row`, `column`, `deviceId`, `deviceName`, `deviceType`, `context`, `instanceId`, plus the key's own arguments |
| Stream Deck Connected / Disconnected | `instanceId`, `remote`, `pluginVersion`, `streamDeckVersion`, `platform`, `deviceCount` |
| Stream Deck Info (on connect and when devices change) | the above plus `deviceNames`, `devices` (JSON), `device0.id` / `.name` / `.type`, ... |

Sub-actions: **Set Background** (file, URL or data URI; state), **Set State** (0/1), **Set Title** (title; state),
**Set Value** (value; dial indicator 0-100), **Show Alert**, **Show Ok**, **Toggle State**. Each takes a Button ID
(`%variables%` allowed, suggestions from the visible keys) and stores how many visible keys it reached in
`%streamDeckButtons%`.

Script functions: `streamdeck.setTitle`, `setState`, `setBackground`, `setValue`, `showAlert`, `showOk`,
`toggleState`, `getInstances`, `isConnected`.

The Stream Deck tab lists connected Stream Deck apps with their devices and visible Cuebot keys, and the dashboard shows
the connection status ("Stream Deck").

Fallback without the plugin: any HTTP-capable Stream Deck plugin can run an action with
`POST http://127.0.0.1:7475/api/v1/actions/{id}/run`, header `Authorization: Bearer <token>`.

## Development and tests

- `npm test`: builds, then runs vitest (protocol, connection/reconnect, key registry, inspector bridge) and an end-to-end
  test that starts the built `bin/plugin.js` exactly as Stream Deck does (Node, registration arguments, the `.sdPlugin`
  folder as working directory) against a simulated Stream Deck app and a simulated Cuebot.
- `npm run typecheck`, `npm run validate` (Elgato's manifest/file validation), `npm run pack`.
- C#: `tests/Cuebot.StreamDeck.Tests` (protocol, hub, triggers, sub-actions, the real API listener with token checks) and
  `tests/Cuebot.UiTests/StreamDeck` (the tab).
- CI: the `streamdeck-plugin` job in `.github/workflows/build.yml` runs all of the above and uploads the
  `.streamDeckPlugin`.

Not yet verified with the real Stream Deck app or hardware (none on the build machine): property inspector layout and
behaviour inside Stream Deck, dial `$B1` feedback, image rendering, multi-actions. `npm audit` reports a zod advisory
inside Elgato's SDK dependencies (`@elgato/utils`); it only parses messages from the local Stream Deck app.
