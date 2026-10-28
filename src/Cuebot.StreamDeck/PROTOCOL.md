# Cuebot Stream Deck protocol (version 1)

How Cuebot's Stream Deck plugin (`streamdeck/` in this repo) talks to Cuebot. Both ends are ours, so this is the
reference for both; the formats are covered by `tests/Cuebot.StreamDeck.Tests` (C#) and `streamdeck/test` (TypeScript).

## Transport

- WebSocket at `ws://<host>:<port>/api/v1/streamdeck` on the **REST API listener** (Servers page, default
  `127.0.0.1:7475`). The API must be on; it answers 404 otherwise.
- `Authorization: Bearer <REST API token>` on the upgrade request, checked like every other API call (401 otherwise).
  Origins follow the API rules: the plugin runs in Node and sends none.
- Text frames, one JSON object each, at most 1 MB. Unknown types and fields are ignored, so either side can add fields.
- The listener is generic: any module can register an `IApiWebSocketRoute` (`src/Cuebot.Server/Api/ApiWebSocketRoute.cs`)
  for `/api/v1/{name}`.

A connection is one Stream Deck app (one running plugin). Several may connect at once (e.g. two PCs).

## Plugin → Cuebot

| `type` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `protocol`, `plugin {uuid, version}`, `app {version, platform, language}`, `devices[]`, `buttons[]` | First message after the socket opens. Fires **Stream Deck Connected** (first hello only) and **Stream Deck Info**. |
| `devices` | `devices[]` | The device list changed. Fires **Stream Deck Info**. |
| `buttons` | `buttons[]` | Replaces the visible key list. |
| `appear` | `button` | A Cuebot key or dial became visible (or its button id changed). |
| `disappear` | `context` | It went away (page change, deleted). |
| `input` | `event`, `context`, `buttonId`, `controller`, `device`, `state?`, `ticks?`, `pressed?`, `hold?`, `tapPos?`, `args?` | A trigger key or dial was used. Fires **Stream Deck Action**. |
| `request` | `id`, `method`, `params` | See requests. |
| `ping` | | Answered with `pong`. |

`device`: `{id, name, type, size {columns, rows}, connected}` (`type` is Elgato's DeviceType number, e.g. 2 = XL, 7 = +).

`button`: `{context, buttonId, kind, controller, device, row?, column?, state, isInMultiAction}`; `kind` is `run`,
`toggle`, `global` or `trigger`; `controller` is `Keypad` or `Encoder`.

`event`: `keyDown`, `keyUp`, `dialDown`, `dialUp`, `dialRotate`, `touchTap`.

### Requests

Answered with `{type: "response", id, ok: true, result}` or `{type: "response", id, ok: false, error}`.

| `method` | `params` | `result` |
| --- | --- | --- |
| `getActions` | | `{actions: [{id, name, group, enabled}]}` |
| `runAction` | `action` (id or name), `args` (object) | as `POST /api/v1/actions/{id}/run` (queued, source "Stream Deck"); disabled actions are an error |
| `setActionEnabled` | `action`, `enabled` (`true`, `false` or `"toggle"`) | the action summary |
| `getGlobals` | | as `GET /api/v1/globals` |
| `setGlobal` | `name`, `value`, `op` (`set`, `add`, `toggle`), `persisted` (default true) | the variable; text values like `42` or `true` keep their type, `add` needs a number |

## Cuebot → plugin

| `type` | Fields | Meaning |
| --- | --- | --- |
| `welcome` | `protocol`, `session`, `app {name, version}` | Sent on connect; the plugin shows "connected" only after it. |
| `response` | see requests | |
| `command` | `command`, `buttonId`, `title?`, `state?`, `image?`, `value?`, `indicator?` | Key appearance (the Stream Deck sub-actions). Sent to **every** connected plugin; each applies it to all keys whose button id matches (case-insensitive) and remembers the latest look per id, so keys on other pages show it when they appear. |
| `actionsChanged` | `actions[]` | The action list changed (toggle keys update their state, inspectors their list). |
| `globalChanged` | `name`, `value` | A global variable changed (keys with "Show the value"). |
| `pong` | | |

Commands: `setTitle` (`title`, `state` 0/1 or absent for the current one; empty title restores the key's own),
`setState` (`state`), `setImage` (`image`: a data URI or http(s) URL, the plugin downloads URLs; Cuebot turns file paths
into data URIs; empty restores), `setValue` (`value`; dials show it on the touch strip with `indicator` 0-100, keys as
the title), `showAlert`, `showOk` (keys only), `toggleState`.

## Versioning

`protocol` is 1. A future incompatible change bumps it; the plugin then says "update the plugin" in its status line.
