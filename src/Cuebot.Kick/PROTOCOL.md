# Kick protocol notes

What the Kick module talks to, what is **official** (documented by Kick, stable) and what is **unofficial**
(what kick.com's own website uses; undocumented, may change without notice). Everything here was written from
Kick's public developer documentation and publicly known client behaviour; no code was copied.

Sources:

- Kick developer docs: <https://docs.kick.com> (OAuth flow `getting-started/generating-tokens-oauth2-flow`,
  scopes `getting-started/scopes`, APIs `apis/*`, events `events/*`, webhook security `events/webhook-security`).
- Centrifugo client protocol (JSON): <https://centrifugal.dev/docs/transports/client_protocol>; Pusher protocol 7 for the former feed.
- kick.com's website traffic and its public web client (the real-time discovery and token calls, observed in a
  browser in Oct 2026), and, for the former Pusher feed, open-source clients such as
  `github.com/andreas-bylund/chatmux` `kick/kick.go`.

## 1. OAuth 2.1 (official) — `Auth/`

Host `https://id.kick.com`. Release builds carry Cuebot's own Kick app (`docs/BUILT_IN_APPS.md`, redirect
`http://localhost:47582/callback`); a user may instead register their **own** app (kick.com → Settings → Developer)
and enter its Client ID and Client Secret under Advanced; its redirect URL must be registered exactly.

| Step | Request |
| --- | --- |
| Authorize | Browser: `GET /oauth/authorize?client_id&response_type=code&redirect_uri&scope&state&code_challenge&code_challenge_method=S256` |
| Redirect | `http://localhost:{port}/callback?code=…&state=…` (default port 47582). Cuebot listens with `HttpListener` only for the sign-in, checks `state`, answers with a "you can close this tab" page. `localhost` rather than `127.0.0.1` because Kick's docs note a problem with `127.0.0.1` redirect hosts. |
| Code exchange | `POST /oauth/token` form: `grant_type=authorization_code, code, client_id, client_secret, redirect_uri, code_verifier` → `access_token, token_type, refresh_token, expires_in, scope` |
| Refresh | `POST /oauth/token` form: `grant_type=refresh_token, refresh_token, client_id, client_secret` (refresh tokens are reusable per the changelog; Cuebot stores the newest one returned) |
| Revoke | `POST /oauth/revoke?token=…&token_type_hint=refresh_token&token_hint_type=refresh_token` (on sign-out, best effort; the OpenAPI spec and RFC 7009 say `token_type_hint`, the prose docs `token_hint_type`, so both are sent) |
| Introspect | `POST /oauth/token/introspect` with `Authorization: Bearer <token>` → `data{active, client_id, token_type (user/app), scope, exp}` (moved from `/public/v1/token/introspect` in January 2026). On connect Cuebot reads the granted scopes from it and names the missing ones on the Kick page ("Sign in again to let Cuebot …"). |
| App token | `POST /oauth/token` form `grant_type=client_credentials, client_id, client_secret` → an app access token without refresh token or scopes, used for public reads (users, channels by slug/id, categories, live streams) when no broadcaster is signed in. |

PKCE: 64 random bytes → base64url verifier (86 chars), challenge = base64url(SHA-256(verifier)), checked against
RFC 7636's example in the tests. Kick requires the client secret on the token endpoint even with PKCE, so it is
stored in `kick.json` (never logged).

Tokens are refreshed when they expire within a minute, and once more after any `401` (single-flight per account).
A refresh rejected with 400/401 clears the account (sign in again).

Scopes requested — broadcaster: `user:read channel:read channel:write chat:write events:subscribe moderation:ban
moderation:chat_message:manage kicks:read channel:rewards:read channel:rewards:write ads:read ads:write streamkey:read`; optional
bot account: `user:read chat:write moderation:ban moderation:chat_message:manage`. `channel:rewards:write` was added with reward
management (Oct 2026), `ads:read ads:write streamkey:read` in November 2026: broadcasters who signed in before must **sign in
again** (Kick page) to grant them; until then those calls fail with 403 and the Kick page says which permissions are missing.

## 2. Public API (official) — `Api/KickApiClient.cs`

Base `https://api.kick.com`, `Authorization: Bearer <user token>`, JSON bodies, responses `{ "data": …, "message": … }`.

| Use | Endpoint |
| --- | --- |
| Signed-in user | `GET /public/v1/users` (no ids = token owner) → `user_id, name, email, profile_picture` |
| Users by id | `GET /public/v1/users?id=1&id=2` |
| Own channel / by slug / by id | `GET /public/v1/channels` / `?slug=` / `?broadcaster_user_id=` → `broadcaster_user_id, slug, stream_title, category{id,name,thumbnail}, channel_description, stream{is_live, viewer_count, start_time, language, is_mature, custom_tags}, active_subscribers_count` |
| Title / category | `PATCH /public/v1/channels` `{stream_title?, category_id?}` → 204 |
| Category search | `GET /public/v2/categories?limit=1000&cursor=` read page by page (`data[{id, name, tags, thumbnail}], pagination.next_cursor`), kept 12 hours and searched locally (exact name, then starts with, then contains). v1's keyword search `GET /public/v1/categories?q=` is deprecated (January 2026) and v2's `name=` only matches whole names, so v1 is only the fallback when v2 fails. `GET /public/v1/categories/{id}` for numeric ids. |
| Tags | `PATCH /public/v1/channels` `{custom_tags: [...]}` (an empty list clears them; fixed by Kick in December 2025) |
| Subscriber counts | from `GET /public/v1/channels`: `active_subscribers_count` (includes gifted subs since Kick's 2026-08-11 change), `active_gifted_subscribers_count`, `canceled_subscribers_count`; also `banner_picture` |
| Stream key | `GET /public/v1/channels` (own channel, `streamkey:read`) → `stream.url`, `stream.key`. Read only on request ("Send stream key to OBS" on the Kick page, which sets OBS's custom RTMP service), held in a type whose ToString hides the key, never logged or stored. |
| Live streams of users | `GET /public/v1/users/livestreams?user_id=1&user_id=2` → `data[{broadcaster_user{id, username, profile_picture}, channel{slug}, category{id,name,thumbnail}, title, viewer_count, started_at, language_code, has_mature_content, tags, thumbnail}]` (offline users are left out) |
| Live streams | `GET /public/v2/livestreams?limit (≤100)&category_id&language_code&cursor` → same items, oldest first, `pagination.next_cursor` (v1 `GET /public/v1/livestreams` is deprecated) |
| KICKs leaderboard | `GET /public/v1/kicks/leaderboard?top=1..100` (`kicks:read`) → `data{week, month, lifetime: [{rank, user_id, username, gifted_amount}]}` |
| Ads | `POST /public/v1/ads/ad-break` `{break_duration_seconds (7–90), id (a UUID the client makes, so a retry is recognised)}` → `{id, ad_break_id, remaining_ad_breaks}` (`ads:write`); `GET /public/v1/ads/ad-break-status` → `{opted_in, ads_blocked, auto_ads_active, remaining_ad_breaks, limits{max, period_seconds}, ad_breaks[{id, ad_break_id, break_duration_seconds, status, source, created_at}]}` (`ads:read`); `POST /public/v1/ads/enroll` (`ads:write`) |
| Send chat | `POST /public/v1/chat` `{content (≤500 graphemes, ≤2048 bytes), type: "user"|"bot", broadcaster_user_id (for user), reply_to_message_id?}` → `{message_id, is_sent}` |
| Delete message | `DELETE /public/v1/chat/{message_id}` → 204 (`moderation:chat_message:manage`) |
| Ban / timeout | `POST /public/v1/moderation/bans` `{broadcaster_user_id, user_id, duration? (minutes 1–10080; omit = permanent), reason? (≤100)}` |
| Unban / untimeout | `DELETE /public/v1/moderation/bans` `{broadcaster_user_id, user_id}` |
| Webhook public key | `GET /public/v1/public-key` → `data.public_key` (PEM) |
| Event subscriptions | `GET/POST/DELETE /public/v1/events/subscriptions`; POST `{method:"webhook", events:[{name, version}]}`; DELETE `?id=…&id=…`. Kick allows 10,000 subscriptions per event type per app, and only **1,000** for `chat.message.sent` until Kick verifies the app. |
| Rewards | `GET /public/v1/channels/rewards` (`channel:rewards:read`) → `id (ulid), title, description, cost, background_color, is_enabled, is_paused, is_user_input_required, should_redemptions_skip_request_queue` (max 50 per channel) |
| Create reward | `POST /public/v1/channels/rewards` `{title (≤50), cost (≥1), description? (≤200), background_color? (#hex, default #00e701), is_enabled?, is_user_input_required?, should_redemptions_skip_request_queue?}` (`channel:rewards:write`) |
| Update / delete reward | `PATCH /public/v1/channels/rewards/{id}` (only changed fields, plus `is_paused`) → reward; `DELETE /public/v1/channels/rewards/{id}` → 204. Only the app that created a reward may change or delete it (403 otherwise). |
| Redemptions | `GET /public/v1/channels/rewards/redemptions?reward_id&status (pending default)&id&cursor` → `data[{reward{id,title,…}, redemptions[{id, redeemed_at, redeemer{user_id}, status, user_input}]}], pagination.next_cursor` |
| Accept / reject | `POST /public/v1/channels/rewards/redemptions/accept` or `/reject` `{ids: [1..25 ulids]}` → `data` = only the failures `[{id, reason: UNKNOWN / NOT_PENDING / NOT_FOUND / NOT_OWNED}]` |

Cuebot splits long chat messages into Kick-sized parts at word boundaries. "Send as bot" uses the separately
signed-in bot account with `type: "user"` into the broadcaster's chat (falls back to the broadcaster when
allowed). Timeouts are given in seconds by users and rounded **up** to Kick's whole minutes.

Public reads (users, channels by slug or id, categories, live streams) use the broadcaster's token when signed in and an
app access token otherwise. The official API has no endpoint for the chatroom id, follower list/count, polls,
predictions, hosts, pinned messages, or real-time events other than webhooks. Polling `GET /channels` (default every 60 s, minimum 15 s) gives stream online/offline, viewer count and
title/category changes without a public URL.

## 3. Webhooks (official) — `Webhooks/`

Kick delivers events **only** by HTTP POST to the webhook URL configured in the app, i.e. a public URL. Cuebot
receives them through the user-hosted Cloudflare relay (`relay/README.md`): Kick posts to the relay, the relay
forwards the raw request over Cuebot's outbound websocket (`Cuebot.Relay.RelayClient`), and
`Webhooks/KickWebhookHandler` (route `kick`, registered by `AddKick()`) maps the `Kick-Event-*` headers to a
`KickWebhookDelivery` and calls `KickWebhookEventSource.HandleAsync`. The relay answers Kick with an empty 200 at
once (Kick expects a quick 2xx and retries otherwise); verification happens in Cuebot, so nothing secret is needed on
the relay. Handler outcomes: accepted → Handled, duplicate/unsupported type → Ignored, bad or missing signature →
Rejected, unparsable body → Failed. While the relay is connected the "Webhook relay" event source shows as connected.

Setting it up (once, after deploying the relay with `relay/setup.ps1` or `relay/setup.sh`):

1. In Cuebot open **Webhooks**, enter the relay URL, connect token and route secret, enable the relay and connect.
   Copy the URL shown for **Kick**: `https://<relay>/hook/<route secret>/kick`.
2. On <https://kick.com/settings/developer> edit the Cuebot developer app (the one whose client id/secret are in
   Cuebot's Kick settings), switch on **Enable webhooks** and paste that URL as the **Webhook URL**. Save.
3. On Cuebot's Kick page tick **Register webhook events** and reconnect Kick: connecting lists the app's event
   subscriptions (`GET /public/v1/events/subscriptions`) and subscribes the missing ones below with
   `method: "webhook"`. Kick sends every subscribed event to the app's single webhook URL.

Kick's webhook URL is per app, not per user. With their **own** Kick app a streamer points it at their own relay (or
at Cuebot's shared webhooks address, `https://hooks.techdaddydigital.com/<relay id>/<route secret>/kick`) as above.

**Built-in app: Cuebot's shared Kick intake (November 2026).** Every streamer on Cuebot's built-in app shares that
app's single webhook address, so Cuebot runs one intake for all of them: the built-in app's webhook URL is
`https://hooks.techdaddydigital.com/kick`, served by the shared portal relay Worker (`portal/worker/kick.ts`,
`portal/RELAY.md` "Kick events").

1. On connect, with **Receive Kick's official events through Cuebot's relay** on (`UseSharedEvents`, default on), Cuebot
   subscribes the broadcaster to the supported events (chat only with `WebhookChat`, because of the 1,000 chat
   subscriptions an unverified app may have) and connects the portal relay.
2. Over its authenticated relay socket Cuebot sends `{"type":"kick-route","rid":n,"token":"<broadcaster access token>"}`
   on every relay connection. The Worker asks Kick's introspection endpoint whether the token is active and belongs to
   the built-in app's client id, reads the user id (`GET /public/v1/users`), and stores only "Kick user id → relay ID"
   (Durable Object `KickRoute`, forgotten 30 days after the last refresh). The token is not kept. Answer:
   `{"type":"kick-route","rid":n,"ok":true,"userId":n}` or `ok:false` with an `error`, shown on the Kick page.
3. Kick posts an event to `/kick`; the Worker checks the timestamp (±10 minutes) and Kick's signature (with the key
   from `/public/v1/public-key`, cached an hour and fetched again on a failure), reads `broadcaster.user_id`, and seals
   the unchanged delivery (`service: "kick"`, Kick's headers, raw body) to that Cuebot's identity key on the relay's
   webhook channel. It always answers Kick with an empty 200, because Kick unsubscribes an app whose address keeps
   failing for a day; events for an offline Cuebot are dropped.
4. Cuebot verifies the signature again and handles the delivery like any relay webhook (`KickWebhookHandler`).
5. Signing out deletes the app's subscriptions for the broadcaster, revokes the token and sends `kick-unroute`
   (`{"userId":n}`; the Worker only removes a route that points to the asking relay).

Headers: `Kick-Event-Message-Id`, `Kick-Event-Subscription-Id`, `Kick-Event-Signature`,
`Kick-Event-Message-Timestamp`, `Kick-Event-Type`, `Kick-Event-Version`.

Signature: base64 RSA PKCS#1 v1.5 with SHA-256 over the bytes of `{message id}.{timestamp}.{raw body}`, verified
with Kick's public key from `/public/v1/public-key`. Cuebot caches the key and fetches it again once when a
signature fails (Kick may rotate the key). Message ids are remembered for an hour to drop redeliveries.

Event types (version 1) and their mapping:

| Webhook | Cuebot event |
| --- | --- |
| `chat.message.sent` | `kick.chatMessage` |
| `channel.followed` | `kick.follow` |
| `channel.subscription.new` / `.renewal` | `kick.subscription` / `kick.resubscription` |
| `channel.subscription.gifts` | `kick.massGiftSubscription` (when more than one) + `kick.giftSubscription` per giftee |
| `channel.reward.redemption.updated` | `kick.rewardRedemption` (with `status` pending/accepted/rejected) |
| `livestream.status.updated` | `kick.streamOnline` / `kick.streamOffline` |
| `livestream.metadata.updated` | `kick.channelUpdate` |
| `moderation.banned` | `kick.userBanned` (no `expires_at`) / `kick.userTimedOut` |
| `kicks.gifted` | `kick.kicksGifted` |

With "Register webhook events" enabled, connecting registers any missing subscriptions for these events.

## 4. Real-time websocket (UNOFFICIAL) — `Realtime/`, events decoded in `Pusher/`

kick.com's website receives chat and channel events from its own **Centrifugo** server. (Until October 2026 it
used Pusher Channels with the public app key `32cbd69e4b950bf97679`, cluster `us2`; that key is now refused on every
cluster with error 4001. The event envelopes kept Pusher's shape, which is why the decoder still lives in `Pusher/`.)

Seen in kick.com's web client and its traffic (Oct 2026):

1. **Discovery:** `POST https://web.kick.com/api/v1/realtime/channels/{channelId}/chat/connection` (or
   `/v1/realtime/connection` without a channel) with
   `{"client":{"id":"<uuid>","type":"web"},"capabilities":{"accepted_providers":[{"provider":"centrifugo"}]}}` →
   `data.connections[] = {provider:"centrifugo", credentials:{url}}`, e.g.
   `wss://realtime.us-east-1.platform.kick.com/connection/websocket`. Works without signing in. If it fails, Cuebot
   uses `wss://realtime.us-west-2.platform.kick.com/connection/websocket`.
2. **Token:** `POST https://web.kick.com/api/v1/realtime/auth/connection` `{"client_id":"<same uuid>"}` →
   `data.token` (an anonymous connection JWT; an empty body is rejected with 400). Cuebot never logs it.
3. **Connect** (Centrifugo JSON protocol, one JSON command per line; replies and pushes may be batched with `\n`):
   send `{"connect":{"token":…,"name":"js"},"id":1}` → `{"id":1,"connect":{client, version, ping, pong, expires?, ttl?}}`
   or `{"id":1,"error":{code, message}}`. Connecting without a token closes with 3501 "bad request".
4. **Subscribe** per public channel: `{"subscribe":{"channel":…,"flag":1},"id":n}` → `{"id":n,"subscribe":{…}}`.
   Channels, no channel token needed: `chatrooms.{chatroomId}.v2`, `chatroom_{chatroomId}`, `channel.{channelId}`,
   `channel_{channelId}` (the website also uses `chatrooms.{chatroomId}` and `predictions-channel-{channelId}`; the
   `private-…` ones need a signed-in website session and are not used). Events can arrive on more than one channel;
   the hub de-duplicates.
5. **Publications:** `{"push":{"channel":…,"pub":{"data":{"event":"App\Events\ChatMessageEvent","data":"<json string>"}}}}`
   (the envelope itself may also arrive JSON-encoded). Cuebot turns each into a `PusherFrame(event, channel, data)`.
6. **Keep-alive:** the server sends `{}` every `ping` seconds; when `pong` is true the client answers `{}`. No traffic
   for `ping` + 30 s → reconnect.
7. **Token expiry:** when the connect reply has `expires: true`, Cuebot fetches a new token 30 s before `ttl` and
   sends `{"refresh":{"token":…},"id":n}`. Error 109 ("token expired") reconnects immediately with a new token.
8. **Disconnects:** `{"push":{"disconnect":{code, reason}}}` or websocket close codes. 3500–3999 and 4500–4999 mean
   "don't reconnect" (Cuebot retries only at the maximum delay); everything else backs off exponentially, 1 s to 60 s.
   Every reconnect gets fresh credentials.

The **chatroom id** and **channel id** are internal ids (not the broadcaster user id) that the official API does
not return. Cuebot reads them from the website API `GET https://kick.com/api/v2/channels/{slug}` (`id`,
`chatroom.id`, `user_id`), sent with a browser User-Agent. kick.com is behind Cloudflare and often blocks such
requests (403 or a challenge page); then Cuebot keeps working without real-time events and the user can enter both
ids on the Kick page (open that URL in a browser and copy them). They are cached in `kick.json`.

Events decoded (event names are `App\Events\…`; payload fields as seen on the website):

| Event | Data (relevant fields) | Cuebot event |
| --- | --- | --- |
| `ChatMessageEvent` | `id, chatroom_id, content, type (message/reply), created_at, sender{id, username, slug, identity{color, badges[{type, text, count?}]}}, metadata{original_sender{id, username}, original_message{id, content}}` | `kick.chatMessage` |
| `MessageDeletedEvent` | `id, message{id}, aiModerated` | `kick.messageDeleted` + `chat.messageDeleted` |
| `UserBannedEvent` | `user{id, username, slug}, banned_by{…}, permanent, duration (minutes), expires_at` | `kick.userBanned` / `kick.userTimedOut` + neutral |
| `UserUnbannedEvent` | `user, unbanned_by, permanent` | `kick.userUnbanned` |
| `SubscriptionEvent` | `chatroom_id, username, months` | `kick.subscription` (1 month) / `kick.resubscription` |
| `GiftedSubscriptionsEvent` | `gifted_usernames[], gifter_username, gifter_total` | `kick.massGiftSubscription` + `kick.giftSubscription` each |
| `StreamHostEvent` | `host_username, number_viewers, optional_message` | `kick.host` (Kick's raid) |
| `ChatroomClearEvent` | `id` | `kick.chatCleared` |
| `StreamerIsLive` (channel) | `livestream{id, session_title, created_at}` | `kick.streamOnline` |
| `StopStreamBroadcast` (channel) | `livestream{id}` | `kick.streamOffline` |
| `FollowersUpdated` (channel) | `followersCount, username?, followed?` | `kick.follow` only when a follower name is present and `followed` is not false |
| `KicksGifted` | `sender{id, username, username_color}, gift{gift_id, name, amount, type, tier}, message` | `kick.kicksGifted` (best effort) |
| `RewardRedeemedEvent` | `reward_title, user_id, username, user_input` | `kick.rewardRedemption` (best effort) |
| `LivestreamUpdated` (channel) | `livestream{session_title, categories[{id, name}]}` | `kick.channelUpdate` (best effort) |

Ignored: polls, pinned messages, chatroom settings, `ChannelSubscriptionEvent` and
`LuckyUsersWhoGotGiftSubscriptionsEvent` (duplicates of the subscription/gift events above).

Emote tokens in message text look like `[emote:37226:KEKW]`; `message` has them replaced by the emote name,
`rawMessage` keeps them.

## 5. Merging sources — `Events/KickEventHub.cs`

Every source produces the same `KickEvent` (type, args, de-duplication key) through `KickEventFactory`. Keys are
chosen so the same occurrence from the websocket and from a webhook collide (chat: message id; follows, subs,
gifts, bans: lower-case user name), and duplicates within two minutes are dropped. Stream online/offline are
state transitions (the first API poll sets the baseline), so polling, the websocket and webhooks never double
them. Moderation events are also published as `chat.messageDeleted`, `chat.userBanned`, `chat.userTimedOut`
(`platform: "Kick"`).

## 6. Which events need the webhook relay

Available **now** without any public URL: chat, message deletions, bans/timeouts/unbans, subscriptions, resubs,
gifted subs, hosts, stream online/offline (websocket + API polling), viewer count and title/category changes
(polling), follows (websocket, when Kick includes the follower's name).

Reliable only through **Kick's webhooks** (best effort on the websocket): **Kicks gifted**, **channel reward
redemptions** (including accepted/rejected status updates), follows with full user ids, and every event when the
website's websocket or its id lookup is unavailable. With the built-in app they arrive through Cuebot's shared Kick
intake (section 3) with no setup; with an own app, through the streamer's relay.

## 7. 7TV emote events (public 7TV APIs, not Kick) — `SevenTv/`

The 7TV emote added/removed triggers (`kick.sevenTvEmoteAdded` / `kick.sevenTvEmoteRemoved`) come from 7TV, which
links a 7TV account to the Kick **user id** of the broadcaster. Written from 7TV's public API and EventAPI
documentation (<https://7tv.io/docs>, <https://github.com/SevenTV/EventAPI>).

1. **Emote set:** `GET https://7tv.io/v3/users/kick/{broadcaster user id}` → `emote_set.id` (the active set). 404 means
   the channel has no 7TV account; Cuebot retries at the maximum reconnect delay. The set is looked up again on every
   connection, so switching sets on 7tv.app is picked up after the next reconnect.
2. **EventAPI:** websocket `wss://events.7tv.io/v3`, JSON messages `{op, t, d}`. The server starts with Hello
   (`op 1`, `d.heartbeat_interval` ms, `session_id`); Cuebot subscribes with
   `{"op":35,"d":{"type":"emote_set.update","condition":{"object_id":"<set id>"}}}`.
3. **Changes:** Dispatch (`op 0`) with `d.type = "emote_set.update"` and `d.body` = change map
   `{id (set), actor{id, username, display_name}, pushed[{key:"emotes", value{id, name, data{animated, host{url}}}}],
   pulled[{key:"emotes", old_value{…}}], updated[…]}`. Each pushed emote is an "added", each pulled one a "removed"
   (renames in `updated` are ignored). Args: `emoteId, emoteName, emote, imageUrl (host url + /1x.webp), isAnimated,
   emoteSetId, provider "7TV", sevenTvUser/sevenTvUserName/sevenTvUserId` (the 7TV editor; deliberately not
   `user`/`userId`, which would be taken for a Kick user).
4. **Keep-alive / reconnect:** heartbeats (`op 2`) every interval; three missed intervals, Reconnect (`op 4`), End of
   stream (`op 7`) or a closed socket reconnect with the module's backoff (1 s to 60 s).
5. **Emote cache:** after a change Cuebot asks the shared emote service (7TV/BTTV/FFZ, `Cuebot.Core`) to reload, so
   the new emote shows in chat. While the EventAPI is off or not connected, the emote service's own set diffs
   (`chat.emoteAdded` / `chat.emoteRemoved` with provider 7TV, platform Kick, noticed when it refreshes) are
   turned into the same Kick triggers; the hub's de-duplication key (`7tv:{emote id}:{name}`) keeps one trigger per
   change. The Kick module also tells the emote service the broadcaster's user id (`IEmoteChannelSource`), so
   7TV channel emotes load for Kick without entering the id in the chat settings.

## 8. Not implemented

- Kick has no reward cooldowns or per-stream limits in its API, so those Twitch-style settings do not exist for Kick.
  (Reward groups and redemption counters are Cuebot's own, kept in kick.json as on Twitch: every
  `kick.rewardRedemption` is counted (`counter`, `userCounter`), counters reset on going live unless persisted, and
  the group steps change rewards one `PATCH` each, counting the ones Kick refuses with 403 as skipped.)
- `type: "bot"` chat (posting as the app's own bot user) is supported by the API client but not exposed in the UI.
- Kick Drops and organizations (docs.kick.com `drops/*`, `organizations/*`) are for game developers running campaigns
  and do not apply to a streaming bot.
- App verification (verified bot badge, 10,000 chat subscriptions) is requested by email to developers@kick.com.
