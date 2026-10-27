# Streamlabs and StreamElements protocols

Everything here comes from public documentation (sources at the end) plus the Socket.IO / Engine.IO protocol specs.
No service is contacted by the tests; `isTest` events from "Send test event" never leave the machine.

## Streamlabs Socket API (official)

- **Token:** the *Socket API Token* from streamlabs.com → Settings → API Settings → API Tokens. It goes in the
  query string, so URLs are never logged (all reasons/status texts go through `Secrets.Redact`).
- **Endpoint:** `https://sockets.streamlabs.com?token=<socket token>`, Socket.IO, websocket transport only.
  Cuebot connects to `wss://sockets.streamlabs.com/socket.io/?token=…&EIO=4&transport=websocket`.
- **Protocol version:** the official docs still show the Socket.IO 2.0.3 client (Engine.IO 3); Speaker.bot's
  changelog says it uses Socket.IO v4. Cuebot tries **EIO=4 first and falls back to EIO=3** when the upgrade is
  refused or no handshake arrives, then remembers what worked (`donations.json` → `streamlabs.engineIoVersion`).
- **Event name:** `event`, one argument `{ "type", "for"?, "message": [ … ], "event_id"? }`. One bot event is published
  per entry of `message` (an object instead of an array is accepted too).
- **Dedupe:** an entry is a duplicate when its `event_id:index` or its `_id` (alert id; `id` when there is no `_id`)
  was seen in the last 500 events. Test events are never deduplicated.

| Streamlabs `type` (`for`) | Bot event | Notes |
| --- | --- | --- |
| `donation` (none) | `streamlabs.donation` | `amount` (string or number), `currency`, `formatted_amount`/`formattedAmount`, `from`/`name`, `message`, `from_user_id` |
| `streamlabscharitydonation` (`streamlabs`) | `streamlabs.charityDonation` | `charityDonationId`, `campaignId`, `amount`, `formattedAmount`, `currency`, `createdAt`. Field names beyond the donation ones are not in the public docs; parsed tolerantly |
| `merch` | `streamlabs.merch` | `from`, `product`, `message`, `image_href`/`imageHref` (undocumented shape; tolerant) |
| `loyalty_store_redemption` | `streamlabs.loyaltyStoreRedemption` | same shape as merch (undocumented; tolerant) |
| `prime_sub_gift` | `streamlabs.primeSubGift` | `name` (recipient), `from` (gifter), `giftType` (undocumented; tolerant) |
| `follow` (`twitch_account`, `youtube_account`, …) | `streamlabs.follow` | relayed |
| `subscription`, `resub` | `streamlabs.subscription` | relayed; `months`, `sub_plan`, `sub_plan_name`, `sub_type`, `message` |
| `bits` | `streamlabs.bits` | relayed; `amount` = bits |
| `raid` | `streamlabs.raid` | relayed; `raiders` |
| `host` | `streamlabs.host` | relayed; `viewers` |
| `superchat` (`youtube_account`) | `streamlabs.superchat` | relayed; `amount` in micros, `displayString`, `comment` |

Relayed events (anything with a platform `for`) are published only with *Relay platform events* on (off by
default, so they do not duplicate the native Twitch/YouTube modules). Other types (`alertPlaying`, `streamlabels`, …)
are ignored. Connection events: `streamlabs.connected`, `streamlabs.disconnected`.

Streamlabs' REST API (donations list, Spin Wheel, alerts) needs an OAuth app; the Socket API token cannot call it, so
there are no Streamlabs REST sub-actions yet.

## Socket.IO / Engine.IO framing (implemented in `SocketIo/`)

- Engine.IO packet = first character: `0` open (`{"sid","pingInterval","pingTimeout",…}`), `1` close, `2` ping,
  `3` pong, `4` message, `6` noop.
- Socket.IO packet inside `4`: type digit (`0` CONNECT, `1` DISCONNECT, `2` EVENT, `3` ACK, `4` CONNECT_ERROR,
  `5`/`6` binary), optional `/namespace,`, optional ack id digits, JSON. Events are `42["name",arg…]`.
- **EIO 4 (Socket.IO v3/v4):** after `0{…}` the client sends `40` and waits for `40{"sid"}`; the **server** sends
  `2` every `pingInterval` and the client answers `3`; no ping within `pingInterval + pingTimeout` = dead connection.
- **EIO 3 (Socket.IO v2):** the server connects `/` by itself (`40`); the **client** sends `2` every `pingInterval`
  and expects `3` within `pingTimeout`.
- `44…` (CONNECT_ERROR) is treated as a refusal (bad token) and retried only at the maximum backoff; other losses
  reconnect with exponential backoff (2 s → 60 s).

## StreamElements (official)

- **Token:** the *JWT Token* from streamelements.com → account → Channels → Show secrets. Sent in the Astro
  subscribe message and as `Authorization: Bearer <jwt>` to the REST API; never logged.

### Astro gateway (current; default)

- `wss://astro.streamelements.com`. Subscribe per topic:
  `{"type":"subscribe","nonce":"<id>","data":{"topic":"channel.activities","token":"<jwt>","token_type":"jwt"}}`.
- Responses: `{"type":"response","nonce","data":{"message","topic","room"}}`, or with `"error"`
  (`err_unauthorized`, `err_bad_request`, `rate_limit_exceeded`, …). An unauthorized response for every topic ends the
  session as a refusal (shown as "StreamElements rejected the JWT token").
- Messages: `{"id","ts","type":"message","topic","room","data":{…}}`.
- Keep-alive: the server sends websocket PING frames every 30 s (answered by the .NET websocket); the client also
  sends its own keep-alive pings and drops the connection if no pong arrives.
- Reconnect: a `{"type":"reconnect","data":{"reconnect_token":"…"}}` message makes Cuebot reconnect immediately to
  `wss://astro.streamelements.com/?reconnect_token=…` without resubscribing (the server restores subscriptions and
  sends `welcome`). The token is used once.
- Cuebot subscribes to **`channel.activities`** only. Its `data` is an activity:
  `{type, provider, channel, data:{username, displayName, providerId, avatar, amount, currency, message, items, …}, _id, activityId, createdAt}`.
  `channel.tips` (tip documents with `donation.user.username`, `donation.amount`, …) carries the same tips with
  moderation state; it is not subscribed to avoid publishing each tip twice.

| Activity `type` | Bot event | Notes |
| --- | --- | --- |
| `tip` | `streamelements.tip` | `tipId`, `amount`, `currency`, `message`; dedupe also by `tip:<tipId>` |
| `merch` | `streamelements.merch` | `items: [{name, price, quantity}]`, `amount`, `message` |
| `redemption` | `streamelements.redemption` | loyalty store item: `itemName`/`item`, `amount` = cost, `message` |
| `follow`, `follower` | `streamelements.follow` | relayed |
| `subscriber` | `streamelements.subscriber` | relayed; `amount` = months, `tier`, `gifted`, `sender` |
| `cheer` | `streamelements.cheer` | relayed; `amount` = bits |
| `raid`, `host` | `streamelements.raid`, `.host` | relayed; `amount` = viewers |
| `superchat`, `sponsor`, `communityGiftPurchase`, `charityCampaignDonation` | `streamelements.superchat`, `.sponsor`, `.communityGift`, `.charityDonation` | relayed |
| anything else | `streamelements.activity` | relayed; `activityType` says which |

Dedupe: `activity:<activityId|_id>` (an updated activity is re-sent with the same id).

### Legacy realtime socket (fallback option)

- `https://realtime.streamelements.com`, Socket.IO v2 (EIO=3, falls back to 4). After connecting, emit
  `42["authenticate",{"method":"jwt","token":"<jwt>"}]`; the server answers `authenticated` (or `unauthorized`) and
  then sends `event` with the same activity objects as above. Kept because it predates Astro and many tools still use
  it; StreamElements presents Astro as the gateway for new integrations, so the legacy socket may be retired.

### REST API (`https://api.streamelements.com/kappa/v2/`, Bearer JWT)

| Use | Request | Response fields read |
| --- | --- | --- |
| channel of the token | `GET channels/me` | `_id`, `username`, `displayName` (cached as `channelId`) |
| user points | `GET points/{channel}/{user}` | `username`, `points`, `pointsAlltime`, `rank` |
| add / remove points | `PUT points/{channel}/{user}/{amount}` (negative removes) | `newAmount` |
| points leaderboard | `GET points/{channel}/top?limit=N` | `users[].username`, `users[].points` |
| latest tips | `GET tips/{channel}?limit=N&sort=-createdAt` | `docs[]._id`, `donation.user.username`, `donation.amount`, `donation.currency`, `donation.message`, `createdAt` |
| tip leaderboard | `GET tips/{channel}/leaderboard?limit=N` | **unverified shape**: array or `docs`/`users`, `username`/`_id`, `total`/`amount` |

`limit` is clamped to 1–100. User names are URL-escaped.

## Sources

- Streamlabs Socket API: https://dev.streamlabs.com/docs/socket-api
- StreamElements websockets (Astro): https://docs.streamelements.com/websockets, topics
  https://docs.streamelements.com/websockets/topics, https://docs.streamelements.com/websockets/topics/channel-activities,
  https://docs.streamelements.com/websockets/topics/channel-tips
- StreamElements API reference: https://dev.streamelements.com/docs/api-docs
- Socket.IO protocol: https://github.com/socketio/socket.io-protocol, Engine.IO protocol:
  https://github.com/socketio/engine.io-protocol
- Streamer.bot trigger variable names (public docs): https://docs.streamer.bot/api/triggers/integrations/streamlabs,
  https://docs.streamer.bot/api/triggers/integrations/streamelements
