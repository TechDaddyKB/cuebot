# Cuebot remote signal protocol (version 1)

How Cuebot instances exchange signals (`src/Cuebot.Remote`). Both ends are Cuebot; the format is covered by
`tests/Cuebot.Remote.Tests` (`ProtocolTests` for the messages, `EndToEndTests` for two real instances on loopback).

## Transport and authentication

- WebSocket at `ws://<host>:<port>/api/v1/remote` on the other instance's **REST API listener** (Servers page there,
  default port 7475). That API must be on (404 otherwise) and, for another computer, listening on a LAN address
  ("Allow LAN connections"). No extra port is opened.
- `Authorization: Bearer <the other instance's REST API token>` on the upgrade request, checked like every API call
  (401 otherwise). The token lives in this instance's `remote.json`, is sent only in that header (never in the URL), and
  is never logged or shown unmasked. Holding the token already grants full API access, so a link adds no new trust.
- Instead of a host, `remote.json` may hold a full `ws://` / `wss://` (or `http(s)://`) URL, e.g. behind a TLS reverse
  proxy; without a path, `/api/v1/remote` is appended.
- Text frames, one JSON object each, at most 1 MB. Unknown types and fields are ignored. WebSocket keep-alive pings
  every 15 s from the connecting side.

## Messages (both directions)

| `type` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `protocol` (1), `instance {id, name, os, version}` | The connecting side sends it first; the accepting side answers with its own. Signals are only exchanged after both. `id` is a GUID from `remote.json` (stable), `name` the instance name (default: computer name). |
| `signal` | `name`, `sender` (sending action, may be empty), `args {key: value}` | A remote signal. Values are strings, numbers, booleans or null; anything else is sent as text. |
| `error` | `message` | Sent before closing a link that is refused: wrong protocol version, no instance id, or an instance connecting to itself. |

Example:

```json
{"type":"hello","protocol":1,"instance":{"id":"2b0c…","name":"Studio PC","os":"Windows","version":"0.1.0"}}
{"type":"signal","name":"go-live","sender":"Start show","args":{"scene":"Main","count":3}}
```

## Behaviour

- A link carries signals **both ways**, so only one of two instances needs the other's token. If both add each other,
  a send still reaches each instance once (deduplicated by instance id, outgoing link preferred).
- Instances are addressed by the local label in `remote.json`, the announced name or the id (case-insensitive); empty,
  `*` or `any` means every connected instance.
- Outgoing links reconnect every `reconnectSeconds` (default 5) while enabled. Refusals are reported in the status
  (401 wrong token, 404 API off / not a Cuebot) and logged once per distinct message.
- A received signal publishes `remote.signal` (trigger **Remote signal received**) with the sent arguments plus
  `signalName`, `signalSender`, `instanceId`, `instanceName`, `instanceLabel`, `instanceOs`, `instanceVersion`; releases
  every **Wait for remote signal** whose name and instance filter match; and, with `releaseLocalWaiters` (default on),
  every **Wait for local signal** of the same name. Only signals arriving after a wait starts count.
- `remote.connected` / `remote.disconnected` (triggers **Remote instance connected / disconnected**) carry the
  instance fields and `direction` (`outgoing` / `incoming`).
