# Donation service protocols

Everything here comes from public documentation (sources at the end) plus the Socket.IO / Engine.IO protocol specs; open-source clients were read for wire formats only.
No service is contacted by the tests; `isTest` events from "Send test event" never leave the machine.

## Streamlabs Socket API (official)

- **Token:** the *Socket API Token* from streamlabs.com → Settings → API Settings → API Tokens. It goes in the
  query string, so URLs are never logged (all reasons/status texts go through `Secrets.Redact`).
- **Endpoint:** `https://sockets.streamlabs.com?token=<socket token>`, Socket.IO, websocket transport only.
  Cuebot connects to `wss://sockets.streamlabs.com/socket.io/?token=…&EIO=4&transport=websocket`.
- **Protocol version:** the official docs still load the Socket.IO **2.0.3** client (Engine.IO 3), the only documented
  protocol; Speaker.bot's changelog says it uses Socket.IO v4. Since 2026-11-11 a fresh install tries **EIO=3 first**
  (`streamlabs.engineIoVersion` defaults to 3) and falls back to EIO=4 when it is refused or no handshake arrives, then
  remembers what worked. Which one `sockets.streamlabs.com` accepts today is still a live check.
- **Refusal:** the Socket API documents no refusal signal for a bad token. A Socket.IO `CONNECT_ERROR` is treated as
  fatal through `SocketIoSessionEnd.Fatal` (carried by `SocketIoClient.Disconnected(reason, fatal)`), never by matching
  the reason text (a local "connection actively refused" is only a lost connection and retried).
- **Event name:** `event`, one argument `{ "type", "for"?, "message": [ … ], "event_id"? }`. One bot event is published
  per entry of `message` (an object instead of an array is accepted too).
- **Dedupe:** an entry is a duplicate when its `event_id:index` or its `_id` (alert id; `id` when there is no `_id`)
  was seen in the last 500 events; the key includes the type, because the docs' sample `_id`s repeat across types. A
  donation also gets `donation:donation_id:<id>` (its `id` equals `donation_id` of `GET /donations`), so the REST
  back-fill recognises donations the socket already delivered. Test events are never deduplicated.

| Streamlabs `type` (`for`) | Bot event | Notes |
| --- | --- | --- |
| `donation` (none) | `streamlabs.donation` | `amount` (string or number), `currency`, `formatted_amount`/`formattedAmount`, `from`/`name`, `message`, `from_user_id`, `to.name` (`recipient`) |
| `streamlabscharitydonation` (`streamlabs`) | `streamlabs.charityDonation` | `charityDonationId`, `campaignId`, `amount`, `formattedAmount`, `currency`, `createdAt`. Field names beyond the donation ones are not in the public docs; parsed tolerantly |
| `merch` | `streamlabs.merch` | `from`, `product`, `message`, `image_href`/`imageHref` (undocumented shape; tolerant) |
| `loyalty_store_redemption` | `streamlabs.loyaltyStoreRedemption` | same shape as merch (undocumented; tolerant) |
| `prime_sub_gift` | `streamlabs.primeSubGift` | `name` (recipient), `from` (gifter), `giftType` (undocumented; tolerant) |
| `follow` (`twitch_account`, `youtube_account`, …) | `streamlabs.follow` | relayed; `id` = Twitch user id / YouTube channel id (`userId`) |
| `subscription`, `resub` | `streamlabs.subscription` | relayed; `months`, `sub_plan`, `sub_plan_name`, `sub_type` (`subType`, plus `isGift`/`isResub`), `gifter` (undocumented), `message`, `emotes` |
| `bits` | `streamlabs.bits` | relayed; `amount` = bits; `id` is the cheer's transaction UUID (`bitsId`), **not** the user: `userId` is `from_user_id` or empty |
| `raid` (the docs' table says `raids`; both accepted) | `streamlabs.raid` | relayed; `raiders` |
| `host` | `streamlabs.host` | **legacy**: still in the docs' table, never sent (Twitch removed hosting in Oct 2022, Mixer closed Jul 2020). Kept so old actions load; trigger labelled "legacy, never sent" |
| `superchat` (`youtube_account`) | `streamlabs.superchat` | relayed; `amount` in micros, `displayString`, `comment`; `channelId` = sender (`userId`), `id` = chat message (`superchatId`) |

Relayed events (anything with a platform `for`) are published only with *Relay platform events* on (off by
default, so they do not duplicate the native Twitch/YouTube modules). Other types (`alertPlaying`, `streamlabels`, …)
are ignored. Connection events: `streamlabs.connected`, `streamlabs.disconnected`.

Charity, merch, loyalty store and Prime gift shapes, and the "Send test event" samples for them, are Cuebot's guesses
(not documented anywhere on dev.streamlabs.com); their help text says "unverified" until real payloads are captured.

### REST API v2.0 (`https://streamlabs.com/api/v2.0/`, OAuth2; optional)

- **App:** the user registers their own app (Settings → API Settings → Register App) and pastes its client id and
  secret: the token exchange needs `client_secret`, which a desktop program cannot ship. Until Streamlabs approves an
  app, only **10 whitelisted users** may authorize it and it gets **5 requests per minute** (TESTING tier; APPROVED =
  2400/min). Cuebot limits itself to `streamlabs.requestsPerMinute` (default 5; extra calls wait) and to 2 `POST
  /alerts` / `POST /donations` a minute, as the docs recommend.
- **Sign-in:** `GET /authorize?response_type=code&client_id&redirect_uri&scope&state` (redirect
  `http://localhost:8914/callback` by default, `HttpListener`), `POST /token` (form: `grant_type=authorization_code`,
  `client_id`, `client_secret`, `redirect_uri`, `code`; the code is valid 5 minutes, single use) →
  `{access_token, token_type, refresh_token}`. The docs say tokens do not expire; Cuebot refreshes
  (`grant_type=refresh_token`) only after a 401 and retries the call once.
- **Scopes asked:** `socket.token donations.read donations.create alerts.create alerts.write profiles.write
  credits.write jar.write wheel.write points.read points.write mediashare.control` (not the restricted `legacy.token`).
- **Calls:** token only in `Authorization: Bearer` (v2.0 refuses `?access_token=`, which some guides still show), plus
  `X-Requested-With: XMLHttpRequest` as in the samples; JSON bodies. `GET /socket/token` fills the socket token,
  `GET /user` the account and linked platforms (Twitch `name` → points channel), `GET /donations?limit&after&before&currency`
  (`{data:[{donation_id, created_at (Unix s), currency, amount, name, message}]}`), `POST /donations`, `POST /alerts`,
  `POST /alerts/{skip,mute_volume,unmute_volume,pause_queue,unpause_queue,show_video,hide_video,send_test_alert}`,
  `GET /alerts/get_status`, `GET /alert_profiles/get` + `POST /alert_profiles/activate {id}`, `POST /credits/roll` (422
  when not live), `POST /jar/empty`, `POST /wheel/spin`, points (`GET /points`, `POST /points/subtract|import|add_to_all|
  user_point_edit|reset`, `GET /points/user_points`), media share (`/media-share/{command}`: the reference shows PATCH
  for enable/disable, the scopes page PUT for all; Cuebot tries the documented verb and the other one on 404/405).
- **Back-fill:** on the first socket connect Cuebot notes the newest `donation_id` (`GET /donations?limit=1`); after a
  reconnect it publishes `GET /donations?after=<last id>` (oldest first, ids above the last only) as
  `streamlabs.donation` with `isBackfill=true`, deduplicated against the socket by donation id.

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

- **Token:** the *JWT* from streamelements.com → avatar (top right) → channel name → pick the channel → copy the JWT.
  Tokens are platform-account specific (docs.streamelements.com/websockets#authentication): with several platforms
  linked, the token of the account that was selected is the one whose activities arrive. Sent in the Astro subscribe
  message and as `Authorization: Bearer <jwt>` to the REST API; never logged. The *Overlay Token* may be used instead
  for events only (Astro `token_type: "apikey"`, no REST); which topics it may read is undocumented.

### Astro gateway (current; default)

- `wss://astro.streamelements.com`. The server sends `welcome` right after the upgrade; Cuebot subscribes when it arrives
  (or after the open timeout if it never does), one command per 110 ms (Astro allows one per 100 ms, burst 100 per 10 s).
  Subscribe per topic:
  `{"type":"subscribe","nonce":"<id>","data":{"topic":"channel.activities","room":"<channel id>","token":"<jwt>","token_type":"jwt"}}`.
- **`room`:** the docs contradict each other. The WebSockets page says `data.room` "defaults to `""` (global)"; the
  topic pages (channel-session-update, chatbot-counters) say it "defaults to the authenticated channel"; every example
  sends the channel id. Cuebot sends the channel id from `GET channels/me` when it is known (always for a JWT), and
  leaves `room` out otherwise (Overlay Token, failed lookup). The response's `data.room` is "the canonical room ID
  returned by the authorization service, which may differ from the requested value"; Cuebot keeps it (shown on the
  Donations card) for any later unsubscribe.
- Responses: `{"type":"response","nonce","data":{"message","topic","room"}}`, or with `"error"`
  (`err_unauthorized`, `err_bad_request`, `rate_limit_exceeded`, …). An unauthorized response for **every** topic ends
  the session as a refusal ("StreamElements rejected the JWT token"); a refused optional topic is only logged (the
  status turns red only when `channel.activities` is refused). `err_deadline_exceeded` ("Retry the request") is
  retried after the reconnect delay; `rate_limit_exceeded` (sent without a nonce, so every unanswered subscription is
  re-sent) waits 5 s, doubling per retry (FAQ example: 5 s), up to 5 retries per session.
- Connection errors (docs: Connection): **502** while an edge drains → reconnect after 0.5 s without growing the
  backoff (another edge takes it); **429** with `Retry-After: 2` → wait `Retry-After` (at least 2 s). The status comes
  from `ClientWebSocket.HttpStatusCode` (`CollectHttpResponseDetails`). Other failures back off 2 s → 60 s.
- Messages: `{"id","ts","type":"message","topic","room","data":{…}}`.
- Keep-alive: the server sends websocket PING frames every 30 s and closes after 70 s without a PONG (answered by the
  .NET websocket). Cuebot also sends its own keep-alive pings (30 s, 30 s timeout); that Astro answers client PINGs is
  not documented and still needs a live soak.
- Reconnect: a `{"type":"reconnect","data":{"reconnect_token":"…"}}` message makes Cuebot reconnect immediately to
  `wss://astro.streamelements.com/?reconnect_token=…` without resubscribing (the server restores subscriptions and
  sends `welcome`). The token is used once. That `welcome` counts as subscribed (status Connected). After a reconnect
  that was **not** a resume, Cuebot back-fills: `GET activities/{channel}?after=<last activity createdAt>&before=<now>
  &limit=25&types=tip&types=merch&types=redemption&origin=feed&min*=0` and publishes what it missed (the
  `activity:<id>` dedupe drops what was already seen), marked `isBackfill` = true (false on live tips, merch and
  redemptions). The Tip, Merch and Store redemption triggers have *Include … fetched after a reconnect* (on by
  default); overlay alerts for them carry `isCatchUp` = true, so Cuebot's alert box skips them unless "show catch-up
  alerts" is on, while event lists and session totals count them. Streamlabs' `isBackfill` donations are treated the same.

**Topics** (docs.streamelements.com/websockets/topics; `channel.chat` only with "receive chat" on):

| Topic (scope) | Bot event(s) | Payload read |
| --- | --- | --- |
| `channel.activities` (activities:read) | see the activity table | activity |
| `channel.tips` (tips:read), `channel.tips.moderation` (tips:moderation) | `streamelements.tipPending`, `.tipRejected`; gate `streamelements.tip` | `{donation:{user:{username,geo,email},message,amount,currency},_id,provider,approved,status,approvedBy}` |
| `channel.stream.status` (stream-live:read) | `.streamOnline` / `.streamOffline` on change; global `seIsLive` | `{isLive}` |
| `channel.session.update` / `.reset` (session:read) | `.sessionUpdated` / `.sessionReset`; globals `se.<key>.<field>` | `{key,name,data:{…},provider}` / `{"<key>":{…},…}` |
| `channel.overlay.action` (overlays:read) | `.overlayAction` | `{action}`: pause, unpause, mute, unmute, skip, playNext, emptyHypeCup, reload |
| `channel.overlay.update` (overlays:read) | `.overlayUpdated` (+ re-import hint on the Overlays page) | full overlay `{_id,name,type,settings,widgets}` |
| `channel.overlay.broadcast` (overlays:broadcast) | `.overlayBroadcast` (receive only: no publish path is documented) | `{target,event,data}` |
| `channel.chatbot.counters` (none) | `.counterChanged` (reset and delete both send 0) | `{counter,value}` |
| `channel.contests` (none) | `.contestBet`, `.contestStateChanged`, `.contestWinner` | `{event:update|state|winner,payload}` |
| `channel.giveaways` (none) | `.giveawayEntered`, `.giveawayWinner`, `.giveawayStateChanged` | `{giveawayId,event,state,data:{totalUsers,totalTickets,entry|winner}}` |
| `channel.songrequest` (overlays:read) | `.songRequest` (`event` = play, pause, volume, queue.add, song.next, …) | `{event,payload}` |
| `channel.chatbot.audiencequeue` (bot:read) | `.viewerQueue` | `{event:queue.*|member.*,payload}` |
| `channel.chatbot.modules.emotecombo` / `.pyramid` (bot:read) | `.emoteCombo` / `.pyramid` | `{msg_id,user_id,user_name,emote,count}` / `{…,word,width}` |
| `channel.chatbot.status` (bot:read, Twitch only) | `.botStatus` | `{mod}` or `{banned}` |
| `channel.chat` (overlays:read) | `.chatMessage`, `.chatMessageDeleted`, `.chatUserCleared`, `.chatCleared` | v1 unified chat; the last 300 deleted ids drop late messages |

Not subscribed: `channel.chat.message` (raw per-platform chat; `channel.chat` covers it), `channel.loyalty.redemptions`,
`channel.loyalty.items`, `channel.kvstore.update`, `channel.announcements`, `channel.chatstats` (payloads undocumented).

**Tip moderation.** A tip arrives as a `tip` activity (`data.tipId`) and as a tip document (`_id`; the docs' examples
use the same id, `67b5f39d07ecd4c594e60f73`, for both, which Cuebot relies on). With "Wait for tip approval" (default)
the activity is held until its document says `approved: allowed` (and `status: success`): `pending` raises `tipPending`
and keeps waiting (no timeout; at most 200 held), `rejected` raises `tipRejected` and drops the tip. With no document
within 5 s (topic refused, ids not matching) the tip is published with `tipApproved: unknown`. Without waiting, tips
publish at once unless their document already said `rejected`. Donor `email`/`geo` are never copied into args.

| Activity `type` | Bot event | Notes |
| --- | --- | --- |
| `tip` | `streamelements.tip` (moderation-gated) | `tipId`, `amount`, `currency`, `message`; dedupe also by `tip:<tipId>`; `tipApproved`, `approvedBy` |
| `merch` | `streamelements.merch` | `items: [{name, price, quantity}]`, `amount`, `message` |
| `redemption` | `streamelements.redemption` | loyalty store item: `itemName`/`item`, `amount` = cost, `message`; `redemptionId`, `userInput` (`input`/`userInput`) read leniently (undocumented) |
| `follow`, `follower` | `streamelements.follow` | relayed |
| `subscriber` | `streamelements.subscriber` | relayed; `amount` = months, `tier`, `gifted`, `sender` |
| `cheer` | `streamelements.cheer` | relayed; `amount` = bits |
| `raid`, `host` | `streamelements.raid`, `.host` | relayed; `amount` = viewers (`host` is legacy) |
| `superchat`, `sponsor`, `communityGiftPurchase`, `charityCampaignDonation` | `streamelements.superchat`, `.sponsor`, `.communityGift`, `.charityDonation` | relayed |
| anything else (`hypetrain*`, `cheerPurchase`, `elixir`, …) | `streamelements.activity` | relayed; `activityType` says which |

Every activity also carries `updatedAt`, `sessionEventsCount` and each field of `data` as `data.<key>` (nested objects as
`data.<key>.<child>`; `email` and `geo` never). Dedupe: `activity:<activityId|_id>` (an updated activity is re-sent
with the same id).

### Legacy realtime socket (deprecated fallback option)

- `https://realtime.streamelements.com`, Socket.IO v2 (EIO=3, falls back to 4). After connecting, emit
  `42["authenticate",{"method":"jwt","token":"<jwt>"}]`; the server answers `authenticated` (or `unauthorized`) and
  then sends `event` with the same activity objects as above. docs.streamelements.com no longer mentions it (only the
  old dev.streamelements.com article does, with socket.io 2.2.0); its Developers page points integrators to Astro.
  Selecting it logs a warning, and after 3 sessions in a row that end before `authenticated` Cuebot switches to Astro
  for the rest of the run. The dashboard's emulated alerts arrive as `event:test` in the widget shape
  `{listener, event:{name, amount, message, ...}}`; `tip-latest` and `merch-latest` are published as tests.

### REST API (`https://api.streamelements.com/kappa/v2/`, Bearer JWT; giveaways `kappa/v3`)

| Use | Request | Response fields read |
| --- | --- | --- |
| channel of the token | `GET channels/me` | `_id`, `username`, `displayName`, `provider` (cached as `channelId`, shown with the platform) |
| user points | `GET points/{channel}/{user}` | `username`, `points`, `pointsAlltime`, `rank` (no schema; without `rank`, `GET points/{channel}/{user}/rank`) |
| add / remove points | `PUT points/{channel}/{user}/{amount}` (negative removes) | `newAmount` (undocumented) |
| bulk points | `PUT points/{channel}` `{users:[{username,current}],mode:"add"|"set"}` | — |
| reset a user / everyone | `DELETE points/{channel}/{user}`, `DELETE points/{channel}/reset/{current|alltime}` | — |
| leaderboards | `GET points/{channel}/top|alltime|watchtime?limit&offset` | `users[].username` + `points` / `pointsAlltime` / `minutes` (no schema; bare arrays accepted) |
| latest tips | `GET tips/{channel}?limit=N&offset=0&sort=-createdAt` | `docs[]`; tips with `deleted`, `approved` ≠ allowed or `status` ≠ success are skipped |
| tips awaiting moderation | `GET tips/{channel}/moderation` | `pending[]` tip documents |
| add a tip | `POST tips/{channel}` `{user:{username,email},provider,message,amount,currency,imported:true}` | the tip document |
| top tippers | `GET tips/{channel}/top` (no limit; sorted by `total` and cut) | `username`, `total` |
| tip / cheer leaderboard by period | `GET activities/{channel}/top?type=tip|cheer&period=alltime|months|weeks|session&limit&offset` | `[{username,total}]` |
| recent activities / replay | `GET activities/{channel}?after&before&limit&types…&origin=feed&min*=0`; `POST activities/{channel}/{id}/replay` | activity array; `{success}` |
| chatbot | `GET bot/{channel}`; `POST bot/{channel}/say {message}`; `POST bot/{channel}/mute|unmute`; `GET bot/{channel}/counters/{counter}` | `bot.{joined,muted,mod,name}`; `value` |
| timers / commands on-off | `GET bot/timers/{channel}`, `GET bot/commands/{channel}`, then `PUT …/{id}` with the listed document and `enabled` changed (server fields `_id`, `createdAt`, `updatedAt`, `__v` removed; the spec gives the body no schema) | — |
| overlay control | `PUT overlays/{channel}/action/{reload|mute|unmute|pause|play}` | — |
| session data | `GET sessions/{channel}`; set a value: GET, change `data.<key>.<field>`, `PUT sessions/{channel}` with the whole document (no body schema; sending everything cannot clear other keys); `PUT sessions/{channel}/reset` | `data` |
| loyalty store | `GET store/{channel}/items?limit=100&offset=0`; `PUT store/{channel}/items/{id}` with the listed item changed (`enabled`, `cost`, `quantity.current`); `GET store/{channel}/redemptions?limit&offset&pending`; `PUT …/redemptions/{id} {completed:true}` (body undocumented); `DELETE …/redemptions/{id}` (refund) | LoyaltyItem; redemptions read leniently (`docs` or array; `item`, `redeemer`, `input`) |
| contests | `GET contests/{channel}` (`active`/`contests`); `POST contests/{channel}` `{title,minBet,maxBet,duration,options:[{title,command}]}` (no body schema: the Contest document's fields); `PUT …/{id}/start`; `PUT …/{id}/winner {winnerId}`; `DELETE …/{id}/close`, `…/refund` | Contest |
| giveaways (v3) | `GET giveaways/{channel}`; `PUT …/{id}/start|winner|complete|reopen`; `DELETE …/{id}/close|refund` (creating one is not offered: no body schema) | Giveaway |
| song requests | `POST songrequest/{channel}/player/play|pause`; `GET …/playing`, `…/next`, `…/queue`; `POST …/queue {video}`; `DELETE …/queue/{song}` | Song |
| overlays (import) | `GET overlays/{channel}`, `GET overlays/{channel}/{id}` (the spec marks `search`/`type` required on the list, like nearly every query parameter; sent without them until a live 400 says otherwise) | `docs[]`; the overlay with `widgets` |

`limit` is clamped to 1–100. User names and ids are URL-escaped. No REST rate limit is documented. Write operations
that answer with plain text (`Created`) count as success.

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
| new `donationID` | `donordrive.donation` | `donorName`, `donorAmount`, `donorMessage`, `donorAvatarUrl`, `donorIsAnonymous`, `recipientName`, `profileName`, `profileTeamName`, `eventName`, `goal`, `raised`, `donations` (list of recent donations), `numDonations`, `isParticipant`, `isTeam`, `isIncentive`, `incentive.*`; plus `donationId`, `createdAt`, `formattedRaised`/`formattedGoal`, `profileUrl` |
| donation with `incentiveID` | also `donordrive.incentive` | the donation's variables + `incentive.id/description/amount/quantity (-1 = unlimited)/quantityClaimed/isActive/startDate/endDate/image/links.donate` |
| any profile field (name, goal, raised, count, event, team, live) | `donordrive.profileUpdated` | `name`, `teamName`, `eventName`, `goal`, `raised`, `donations` (list), `numDonations`, `isParticipant`, `isTeam`, `previousRaised`, `previousGoal`, `previousDonations` |

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
  "Replay Alert" in Pally's activity feed re-broadcasts a tip to every websocket on purpose, so tips are not
  deduplicated: a tip id seen before is published again with `isReplay` = true (the Campaign Tip trigger has an
  *Include replayed alerts* option, on by default). Unlike the catch-up flags of the other services, this replay is
  deliberate: the overlay alert has `isCatchUp` = false and the alert box plays it again.
- **Event:** `{"type":"campaigntip.notify","payload":{"campaignTip":{"id","createdAt","updatedAt","displayName",
  "message","grossAmountInCents","netAmountInCents","processingFeeInCents"},"page":{"id","slug","title","url"}}}`
  ("may contain additional/undocumented fields"). Amounts are US cents.
- **Echo:** sending `{"type":"echo","payload":<event>}` makes the server send `<event>` back on the same connection.
  Cuebot's *Test through Pally.gg* button sends a sample tip whose id starts with `cuebot-echo-`; it arrives as a real
  message and is published with `isTest`/`isEcho` = true.
- Bot event `pally.campaignTip`: Streamer.bot's `campaignId/Slug/Title/Url`, `createdAt`, `tipId`, `tipDisplayName`,
  `tipMessage`, `tipGrossAmount(/Formatted/InCents)`, `tipNetAmount(…)`, `tipProccessingFee(…)` (Streamer.bot's
  spelling) and the correctly spelled `tipProcessingFee(…)`.

## Verification against the official documentation (2026-10-28)

No donation account was available, so nothing here was tried with a real donation. Each integration was compared with
the service's official documentation and example payloads (community sources are marked); the documented examples
are test fixtures, copied verbatim, in `tests/Cuebot.Donations.Tests/DocumentedPayloadTests.cs`.

### Streamlabs

- **Sources:** https://dev.streamlabs.com/docs/socket-api (page `updatedAt` 2025-06-05), /reference/sockettoken,
  /docs/scopes, /reference/donations (all fetched 2026-10-28).
- **Verified:** endpoint and query token, websocket transport, Socket.IO `event` name, `{type, for, message:[…],
  event_id}` envelope (the docs: the value "will always be an array type"), field names and types of every documented
  sample (donation `amount` "13.37" as a string, `id` a number, `from_user_id` null; bits `amount` and host `viewers`
  as strings; superchat `amount` in micros), `_id` per alert for dedupe, the `for` values. The Socket API token is
  copied from the dashboard (or read with OAuth scope `socket.token` from `GET /api/v2.0/socket/token`).
- **Changed:** `raids` (the docs' type table) is accepted besides `raid` (the sample); a donation's `to.name` is
  `recipient`, `iconClassName` is kept; a superchat's sender is `channelId` (`userId`), its `id` the chat message
  (`superchatId`); relayed events carry `createdAt` (`created_at`/`publishedAt`) and subscriptions `since`
  (`sponsorSince`/`since`).
- **Re-checked 2026-11-11** (every `/docs/*` guide and `/reference/*` page, `updatedAt` 2025-06-05/06): bits `id` is a
  transaction id (now `bitsId`, not `userId`); `host` is legacy; EIO 3 is now tried first; REST API added (see above).
- **Not settled by the docs:** the Socket.IO version the server speaks today (the page still loads client 2.0.3; Cuebot
  now tries EIO 3 first and keeps the fallback to 4); merch, loyalty store redemption, charity (`streamlabscharitydonation`) and Prime gift
  payloads are not documented at all (that the charity type exists comes from a Streamlabs developer quoted in a
  TwitchSpawn issue, community); a test-event flag (none is documented); rate limits.

### StreamElements

- **Sources:** https://docs.streamelements.com/websockets, /websockets/examples, /websockets/topics,
  /websockets/topics/channel-activities, /websockets/topics/channel-tips (read through
  https://docs.streamelements.com/llms-full.txt); https://dev.streamelements.com/docs/api-docs (OAuth2, "Personal
  Access - Using JWT", "Connecting via Websocket using OAuth2", and the reference pages points `/:channel/:user`,
  `/:channel/:user/:amount`, `/:channel/top`, tips `/:channel`, `/:channel/top`, `/:channel/leaderboard`) (fetched
  2026-10-28).
- **Verified:** Astro URL, subscribe/response/message/reconnect shapes, error codes, `token_type` `jwt`, the 30 s
  PING / 70 s pong rule, `reconnect_token` hand-off, the `channel.activities` payload (`type`, `provider`, `channel`,
  `data.username/displayName/providerId/avatar`, `_id`, `activityId`, `createdAt`), its activity types and providers
  (`StreamElements`, `represent`, `fourthwall`, ... keep their own platform names); the legacy socket's
  `authenticate {method:"jwt"}` → `authenticated {channelId}`; REST base URL, `Authorization: Bearer <JWT>`, points
  and tips paths.
- **Changed:** subscribe only after `welcome` (as every documented client does); a resumed session's `welcome` now
  reports Connected (it stayed at "Subscribing..."); `err_deadline_exceeded` / `rate_limit_exceeded` are retried; one
  command per 110 ms; the tip leaderboard reads the documented `tips/{channel}/top` (`.../leaderboard` has no
  documented response); `offset=0` is sent where the reference marks it required; deleted tips are left out of "latest
  tips"; the legacy socket's `event:test` (dashboard emulations, widget shape `{listener, event}`) is mapped for
  `tip-latest` and `merch-latest` and published as a test.
- **Not settled by the docs:** the response bodies of `points/{channel}/{user}`, `PUT .../{amount}` and
  `points/{channel}/top` (Cuebot reads `points`/`pointsAlltime`/`rank`, `newAmount`, `users[]`); the `tip`, `merch`
  and `redemption` activity `data` beyond the common user fields (the websocket schema lists `tipId`, `amount`,
  `currency`, `message`, `quantity`, `items`, `tier`, but no merch item or store item shape); whether Astro delivers
  dashboard emulations; Streamer.bot's `merchItem#` numbering (Cuebot counts from 0); the `apikey` (overlay token)
  and `oauth2` token types (Cuebot uses the JWT only).

### TipeeeStream

- **Sources:** https://api.tipeeestream.com/api-doc/socketio ("Updated on August 7th, 2019"; deploy tag 2026-08-25),
  https://api.tipeeestream.com/api-doc/events, `GET https://api.tipeeestream.com/v2.0/site/socket` (answers
  `{"code":200,"message":"success","datas":{"port":"443","host":"https://sso-cf.tipeeestream.com"}}`) (fetched
  2026-10-28).
- **Verified:** socket lookup, `access_token` query, `join-room {room: <API key>, username}`, `new-event`
  `{appKey, event:{id, type, user, ref, inserted_at, created_at, parameters, formattedAmount, "parameters.amount"}}`,
  REST `events.json?apiKey` with `type[]`, `limit` (default 10), `offset`, `sort`, `order`; event types
  follow/subscription/donation/hosting; `resub` is a number in one sample and a string in the other.
- **Changed:** nothing; the documented samples are now fixtures.
- **Not settled by the docs:** `event.user` is the *streamer's* account in both samples (its `avatar` is an id such as
  "5236", not a URL); Streamer.bot's `avatar` is filled from it, and no donor avatar is documented; fees, campaign,
  recurring, cheers and raids are not in the docs (read tolerantly, as open-source clients see them, community); no
  test flag beyond `ref` = `simulation...` (community); no rate limits.

### DonorDrive / Extra Life

- **Sources:** https://github.com/DonorDrive/PublicAPI (README, overview.md, how-tos.md, resources/participants.md,
  teams.md, donations.md, incentives.md; last commit 2025-10-24) (fetched 2026-10-28).
- **Verified:** unauthenticated GETs on `<site>/api/...`, the 15 s request interval and `If-None-Match`/304 pattern,
  status codes (204 out-of-range page, 304, 400, 404, 412 bad version, 429, 503), `limit` max 100 (above = 400),
  default order `createdDateUTC DESC`, all participant, team, donation and incentive field names and types (dates
  like `2019-10-28T16:02:42.583+0000`, missing `quantity` = unlimited, `amount`/`displayName`/`donorID` absent for
  private donors), `urlAlias` accepted in place of the numeric id.
- **Changed:** `donations` is now what Streamer.bot documents ("An array of the donations"): the last list read (up
  to 100, newest first; `donationId`, `donorName`, `donorAmount`, `donorMessage`, `donorAvatarUrl`,
  `donorIsAnonymous`, `recipientName`, `incentiveId`, `createdAt` each); the count moved to `numDonations`. Profile
  Updated is published after the donation list is read, so its list includes the new donation.
- **Not settled by the docs:** the API version: the overview lists 1.0 as default and 1.4 as default from "Q1 '26";
  Cuebot sends no `version` and needs only fields present since 1.0 (`isActive`, from 1.2, is optional); a numeric
  rate limit (only "one every 15 seconds"); Retry-After on 429.

### TreatStream

- **Sources:** https://treatstream.com/api/details (fetched 2026-10-28).
- **Verified:** `Oauth2/Authorize` (`response_type`, `client_id`, `redirect_uri`, `state`, `scope=userinfo`),
  `Oauth2/Authorize/token` and `Oauth2/RefreshToken` fields and responses (`expires_in` 864000 in the examples although
  the text says 30 days; refresh tokens expire after 180 days), `Oauth2/Authorize/socketToken` (`client_id`,
  `access_token` → `socket_token`), `https://nodeapi.treatstream.com/` with `query: "token=..."` (socket.io 1.7.2
  client), the `realTimeTreat` fields, `getMonthTreats/{access_token}` (letter codes `t`/`f` for sender/receiver types).
- **Changed:** nothing.
- **Not settled by the docs:** body encoding (the curl examples send JSON, the parameter lists look form-style; Cuebot
  sends a form and retries as JSON); no treat id, amount or test flag; no rate limits.

### Pally.gg

- **Sources:** https://docs.pally.gg/advanced/websockets.md, https://docs.pally.gg/features/activity-feed.md,
  https://docs.pally.gg/llms.txt (its `api-reference/openapi.json` answers 404) (fetched 2026-10-28).
- **Verified:** `wss://events.pally.gg?auth=<key>&channel=firehose` and `...&channel=activity-feed&room=<slug>`, the
  `ping`/`pong` keep-alive (at least every 60 s), `campaigntip.notify` with `campaignTip` (amounts in cents) and
  `page`, the echo test message.
- **Changed:** "Replay Alert" re-broadcasts a tip to every websocket on purpose, and Pally otherwise sends each event
  once, so tips are no longer deduplicated by id: a repeat is published with `isReplay` = true (trigger option
  *Include replayed alerts*, on by default).
- **Not settled by the docs:** the feed is Beta; the currency (amounts in cents, assumed USD); no REST API.

## Where Cuebot goes beyond Streamer.bot's documented setup

- TipeeeStream: the API key is checked before connecting (clear error instead of silent retries), the socket server
  comes from TipeeeStream's lookup with a fallback, and relayed follows/subs/cheers/raids/hosts are available (off by
  default); Streamer.bot documents only Donation.
- DonorDrive: conditional requests (ETag → 304), DonorDrive's 15 s floor enforced, Retry-After/backoff on 429, the
  lagging-list re-check, no replay of old donations on connect, and a custom site for any DonorDrive program.
- TreatStream: automatic token refresh; secrets never logged.
- Pally.gg: optional single-page channel, end-to-end echo test through Pally's server, both spellings of the fee variables.

## Sources

All re-read on 2026-10-28 (see "Verification against the official documentation" above for what each settles).

- Streamlabs Socket API: https://dev.streamlabs.com/docs/socket-api
- StreamElements websockets (Astro): https://docs.streamelements.com/websockets, topics
  https://docs.streamelements.com/websockets/topics, https://docs.streamelements.com/websockets/topics/channel-activities,
  https://docs.streamelements.com/websockets/topics/channel-tips
- StreamElements API reference: https://dev.streamelements.com/docs/api-docs (legacy socket: "Connecting via Websocket using
  OAuth2"; widget event shape used by `event:test`: https://docs.streamelements.com/overlays/events)
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
