# Donation service protocols

Everything here comes from public documentation (sources at the end) plus the Socket.IO / Engine.IO protocol specs; open-source clients were read for wire formats only.
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

## TipeeeStream (official socket.io API)

Still operating (site and API answer; api-doc deploy tag of August 2026). No shutdown notice found.

**Options considered**

| Option | Real-time | Official | User provides | Notes |
| --- | --- | --- | --- | --- |
| **Socket.IO `new-event` with the API key (chosen)** | yes | yes (api-doc/socketio) | API key | What TipeeeStream documents for alerts; used by Firebot, Twitchat, SubathonManager, nomnomzbot |
| OAuth app → `GET /v1.0/me/api` → API key → same socket | yes | yes | sign-in (needs a registered OAuth client) | Only a nicer way to get the same key; Cuebot has no TipeeeStream OAuth client, the key is shown in the account |
| REST `GET /v1.0/events.json?apiKey=…` polling | no (≥ 1 min) | yes | API key | Used only to validate the key before connecting |
| Webhooks | — | none documented | — | Not offered |

- **Socket server lookup:** `GET https://api.tipeeestream.com/v2.0/site/socket` →
  `{"code":200,"message":"success","datas":{"port":"443","host":"https://sso-cf.tipeeestream.com"}}`. Cuebot uses
  `host` (+ `port`); when the lookup fails it falls back to `https://sso-cf.tipeeestream.com` (what it has returned for
  years).
- **Key check:** `GET /v1.0/events.json?apiKey=<key>&limit=1`; 401/403 fails Connect with "TipeeeStream rejected the API
  key" instead of reconnecting forever (the socket handshake answers 403 `{"code":4,"message":"Forbidden"}` too).
- **Socket:** Socket.IO **v2 / Engine.IO 3** (every client and TipeeeStream's own site use socket.io-client 2.x),
  `wss://sso-cf.tipeeestream.com/socket.io/?access_token=<key>&EIO=3&transport=websocket`. The client pings (`2`) every
  `pingInterval`. If EIO 3 is refused before the handshake, the shared client tries EIO 4 once.
- **Join:** on connect, emit `42["join-room",{"room":"<key>","username":"<TipeeeStream user name>"}]`. The user name
  does not appear to be checked (open-source clients send their app name); Cuebot sends the configured one, else
  `cuebot`.
- **Events:** `42["new-event",{"appKey","event":{…}}]` (some servers wrap the argument in an array). `event`:
  `{id, type, ref, origin?, user:{username, avatar (string id, or {path,…}), providers, …}, parameters:{…},
  formattedAmount, created_at ("2015-10-05T16:58:26+0200"), inserted_at, "parameters.amount"}`.

| `type` | Bot event | Fields |
| --- | --- | --- |
| `donation` (also `tipeee`) | `tipeeestream.donation` | `parameters.username` (donor), `amount` (number or string), `currency` (string or `{code}`), `message`, `fees`, `campaignId`, `recurring`; `formattedAmount`; `id` |
| `follow` | `tipeeestream.follow` | relayed (`ref` `TWITCH_…`/`YOUTUBE_…` or `origin`) |
| `subscription` | `tipeeestream.subscription` | relayed; `parameters.resub` = months |
| `cheer` | `tipeeestream.cheer` | relayed; `parameters.amount` = bits (type not in the docs; tolerant) |
| `raid`, `hosting` | `tipeeestream.raid`, `.host` | relayed; `parameters.viewers` |

Relayed events are published only with *Relay platform events* on (off by default). `ref` starting with `simulation`
(TipeeeStream's own "simulate alert") sets `isSimulation`. Dedupe: `tipeee:id:<id>`, `tipeee:ref:<ref>`.
Streamer.bot's Donation variables are all set: `amount`, `avatar`, `currency`, `fees`, `message`, `timestamp`,
`tipeeStreamId`, `username`.

## DonorDrive (Extra Life and other DonorDrive charity programs; official Public API)

**Options considered**

| Option | Real-time | Official | User provides | Notes |
| --- | --- | --- | --- | --- |
| **Public API polling with `If-None-Match` (chosen)** | ~15–30 s | yes (github.com/DonorDrive/PublicAPI) | participant or team id (public) | DonorDrive's documented pattern: poll the profile, fetch nested lists only when its ETag changes |
| Webhooks / websocket | — | none public | — | DonorDrive offers none for participants |
| Collection endpoints (`/api/participants`, `/api/events`) | — | need credentials | — | Answer 401 on dd.extra-life.org; not needed |

- **Base URL:** `https://<program site>/api/`. Extra Life: `https://dd.extra-life.org/api/` (`www.extra-life.org/api/…`
  301-redirects there and duplicates the query string, so Cuebot uses `dd.` directly). DonorDrive's demo site
  `https://testdrive.donordrive.com/api/`. Any other program: the user's site, normalized to `…/api/`.
- **Requests** (GET, no auth, JSON): `participants/{id}` or `teams/{id}`; `…/donations?limit=100` (newest first);
  `…/incentives?limit=100`. Unknown ids answer **404 with an HTML page** (not JSON); 204 = empty page; 429 = rate
  limited (no numeric limit published; Retry-After honoured if present).
- **Rate limit:** "Please limit requests to one every 15 seconds." Cuebot polls the profile every *Check every* seconds
  (default 30, minimum 15) with `If-None-Match: <etag>`; a 304 costs nothing more. Only when the profile changes does it
  read the donations (and incentives when a donation names an unknown one). Because the donation list may be cached
  separately, it re-checks it for up to 4 polls after the totals change. 429/503/network errors back off ×2 up to 5 min.
- **First poll** only learns the current state (existing donations are not replayed).
- **Verified live (2026-10-27):** `GET https://dd.extra-life.org/api/teams/73275` → 200 JSON, `etag:
  "500E5DB…"`, `Cache-Control: public, max-age=14400`, `api-version: 1.0`; `CharityLiveTests` read it through Cuebot.
- **Dates** look like `2019-10-28T16:02:42.583+0000` (variable fraction digits, offset without colon); converted to ISO 8601.
- **Anonymous donors:** `displayName`, `amount`, `message`, `donorID` may be missing → `Anonymous`, `donorIsAnonymous`.

| Change | Bot event | Variables (Streamer.bot names + extras) |
| --- | --- | --- |
| new `donationID` | `donordrive.donation` | `donorName`, `donorAmount`, `donorMessage`, `donorAvatarUrl`, `donorIsAnonymous`, `recipientName`, `profileName`, `profileTeamName`, `eventName`, `goal`, `raised`, `donations`, `isParticipant`, `isTeam`, `isIncentive`, `incentive.*`; plus `donationId`, `createdAt`, `formattedRaised`/`formattedGoal`, `profileUrl` |
| donation with `incentiveID` | also `donordrive.incentive` | the donation's variables + `incentive.id/description/amount/quantity (-1 = unlimited)/quantityClaimed/isActive/startDate/endDate/image/links.donate` |
| any profile field (name, goal, raised, count, event, team, live) | `donordrive.profileUpdated` | `name`, `teamName`, `eventName`, `goal`, `raised`, `donations`, `isParticipant`, `isTeam`, `previousRaised`, `previousGoal`, `previousDonations` |

Only the donation is an activity-feed item (the Incentive and Profile Updated events would duplicate it). Amounts carry
no currency in the API; the *Currency* setting (default USD) is used. Streamer.bot lets several providers be added;
Cuebot has one participant/team per profile.

## TreatStream (official OAuth + socket API)

Still operating (treatstream.com answers, "© 2026", Twitch/YouTube/Kick logos; the socket server answers a handshake).

**Options considered**

| Option | Real-time | Official | User provides | Notes |
| --- | --- | --- | --- | --- |
| **OAuth2 → socket token → Socket.IO `realTimeTreat` (chosen)** | yes | yes (treatstream.com/api/details) | Client ID + Secret of their own TreatStream API app, then sign in | The only documented real-time path |
| REST `GET /api/getMonthTreats/{access_token}` | no | yes | access token | Last 30 days; not needed for alerts |
| Widget/alert URL token, webhooks | — | none documented | — | Not offered |

- **App:** the user registers a TreatStream API app (Client ID, Client Secret, Redirect URL) and sets the redirect URL to
  `http://localhost:<port>/callback` (default port 8913, shown on the card).
- **Authorize:** browser to `https://treatstream.com/Oauth2/Authorize?response_type=code&client_id&redirect_uri&state&scope=userinfo`;
  Cuebot listens on the loopback URL and checks `state`.
- **Token:** `POST https://treatstream.com/Oauth2/Authorize/token` (`grant_type=authorization_code, code, client_id,
  client_secret, redirect_uri, scope`) → `{access_token, expires_in (864000 = 10 days; the docs say 30), token_type,
  scope, refresh_token}`. Refresh: `POST /Oauth2/RefreshToken` (`grant_type=refresh_token, refresh_token, client_id,
  client_secret`); Cuebot refreshes when less than a day is left. Bodies are form-encoded (standard OAuth2, what the
  open-source clients send); the docs' curl shows JSON, so a 400/415 is retried once as JSON.
- **Socket token:** `POST /Oauth2/Authorize/socketToken` (`client_id, access_token`) → `{"socket_token"}`. 400/401/403 =
  "TreatStream rejected the access token. Sign in again." Then the `treatstream.authenticated` event is published.
- **Socket:** Socket.IO **v2 / Engine.IO 3** (the docs load socket.io 1.7.2; probe: `pingInterval` 25000, `pingTimeout`
  60000), `wss://nodeapi.treatstream.com/socket.io/?token=<socket token>&EIO=3&transport=websocket`; no emit needed.
- **Event:** `42["realTimeTreat",{"message","sender","receiver","title","sender_type","receiver_type","date_created"}]`,
  e.g. `title` "Pizza", `date_created` "2024-03-16 14:21:44 " (no zone; read as UTC). There is no amount, image or id;
  dedupe key = sender | title | date | message.
- Bot event `treatstream.treat`: Streamer.bot's `username`, `type`, `message`, `timestamp`, plus `receiver`,
  `senderType`, `receiverType`. Feed item: Tip with the treat as its "amount" (credits list it under tips).

## Pally.gg (official event websocket, Beta)

**Options considered**

| Option | Real-time | Official | User provides | Notes |
| --- | --- | --- | --- | --- |
| **Websocket `wss://events.pally.gg`, `channel=firehose` (chosen)** | yes | yes (docs.pally.gg/advanced/websockets) | API key | All pages the user owns; what Streamer.bot's key-only setup implies |
| Websocket `channel=activity-feed&room=<slug>` | yes | yes | API key + page slug | Offered as the optional *Page slug* setting |
| REST / OpenAPI | — | `openapi.json` listed but 404 | — | No documented endpoints |
| Webhooks | — | none | — | Not offered |

- **Key:** pally.gg → Dashboard → Settings → API Keys (one per integration recommended).
- **URL:** `wss://events.pally.gg?auth=<key>&channel=firehose` or `…&channel=activity-feed&room=<slug>`. Plain
  websocket (not Socket.IO). A bad key fails the upgrade (HTTP 400): shown as "Pally.gg rejected the API key" and retried
  only at the maximum delay.
- **Keep-alive:** send the text `ping` (Pally asks for one at least every 60 s; Cuebot sends one after 30 s of silence)
  and expect `pong`; no answer = reconnect with backoff.
- **Delivery:** each event goes once to the connections open at the time; there is **no replay** after a reconnect.
  Manual replays from Pally's activity feed resend an event (deduped by tip id).
- **Event:** `{"type":"campaigntip.notify","payload":{"campaignTip":{"id","createdAt","updatedAt","displayName",
  "message","grossAmountInCents","netAmountInCents","processingFeeInCents"},"page":{"id","slug","title","url"}}}`
  ("may contain additional/undocumented fields"). Amounts are US cents.
- **Echo:** sending `{"type":"echo","payload":<event>}` makes the server send `<event>` back on the same connection.
  Cuebot's *Test through Pally.gg* button sends a sample tip whose id starts with `cuebot-echo-`; it arrives as a real
  message and is published with `isTest`/`isEcho` = true.
- Bot event `pally.campaignTip`: Streamer.bot's `campaignId/Slug/Title/Url`, `createdAt`, `tipId`, `tipDisplayName`,
  `tipMessage`, `tipGrossAmount(/Formatted/InCents)`, `tipNetAmount(…)`, `tipProccessingFee(…)` (Streamer.bot's
  spelling) and the correctly spelled `tipProcessingFee(…)`.

## Where Cuebot goes beyond Streamer.bot's documented setup

- TipeeeStream: the API key is checked before connecting (clear error instead of silent retries), the socket server
  comes from TipeeeStream's lookup with a fallback, and relayed follows/subs/cheers/raids/hosts are available (off by
  default); Streamer.bot documents only Donation.
- DonorDrive: conditional requests (ETag → 304), DonorDrive's 15 s floor enforced, Retry-After/backoff on 429, the
  lagging-list re-check, no replay of old donations on connect, and a custom site for any DonorDrive program.
- TreatStream: automatic token refresh; secrets never logged.
- Pally.gg: optional single-page channel, end-to-end echo test through Pally's server, both spellings of the fee variables.

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
- TipeeeStream: socket.io doc https://api.tipeeestream.com/api-doc/socketio, events/REST doc
  https://api.tipeeestream.com/api-doc/events, socket lookup https://api.tipeeestream.com/v2.0/site/socket; wire format
  cross-checked (URLs, event and field names only) in Firebot (crowbartools/Firebot, integrations/builtin/tipeeestream),
  Twitchat (Durss/Twitchat, storeTipeee.ts), SubathonManager (WolfwithSword/SubathonManager), nomnomzbot, sogeBot
- DonorDrive Public API: https://github.com/DonorDrive/PublicAPI (README, overview.md, how-tos.md, resources/*.md)
- TreatStream API: https://treatstream.com/api/details; cross-checked in WolfwithSword/SubathonManager and
  NoMercyLabs/nomnomzbot (URLs and field names only)
- Pally.gg websockets: https://docs.pally.gg/advanced/websockets.md, activity feed https://docs.pally.gg/features/activity-feed.md
- Streamer.bot trigger names/variables (public docs): https://docs.streamer.bot/api/triggers/integrations/tipeeestream/donation,
  …/donordrive/donation, …/donordrive/incentive, …/donordrive/profile-updated, …/treatstream/treat,
  …/pallygg/campaign-tip; guides https://docs.streamer.bot/guide/integrations/pallygg (API key only)
