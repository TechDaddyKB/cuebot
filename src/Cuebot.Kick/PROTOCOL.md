# Kick protocol notes

What the Kick module talks to, what is **official** (documented by Kick, stable) and what is **unofficial**
(what kick.com's own website uses; undocumented, may change without notice). Everything here was written from
Kick's public developer documentation and publicly known client behaviour; no code was copied.

Sources:

- Kick developer docs: <https://docs.kick.com> (OAuth flow `getting-started/generating-tokens-oauth2-flow`,
  scopes `getting-started/scopes`, APIs `apis/*`, events `events/*`, webhook security `events/webhook-security`).
- Pusher Channels protocol 7: <https://pusher.com/docs/channels/library_auth_reference/pusher-websockets-protocol/>.
- Public knowledge of kick.com's website traffic, as used by open-source clients (for example
  `github.com/andreas-bylund/chatmux` `kick/kick.go`, which documents the same app key, cluster and channel name).

## 1. OAuth 2.1 (official) — `Auth/`

Host `https://id.kick.com`. The user registers their **own** Kick app (kick.com → Settings → Developer) and
enters its Client ID and Client Secret in Cuebot; the redirect URL must be registered exactly.

| Step | Request |
| --- | --- |
| Authorize | Browser: `GET /oauth/authorize?client_id&response_type=code&redirect_uri&scope&state&code_challenge&code_challenge_method=S256` |
| Redirect | `http://localhost:{port}/callback?code=…&state=…` (default port 47582). Cuebot listens with `HttpListener` only for the sign-in, checks `state`, answers with a "you can close this tab" page. `localhost` rather than `127.0.0.1` because Kick's docs note a problem with `127.0.0.1` redirect hosts. |
| Code exchange | `POST /oauth/token` form: `grant_type=authorization_code, code, client_id, client_secret, redirect_uri, code_verifier` → `access_token, token_type, refresh_token, expires_in, scope` |
| Refresh | `POST /oauth/token` form: `grant_type=refresh_token, refresh_token, client_id, client_secret` (refresh tokens are reusable per the changelog; Cuebot stores the newest one returned) |
| Revoke | `POST /oauth/revoke?token=…&token_hint_type=refresh_token` (on sign-out, best effort) |

PKCE: 64 random bytes → base64url verifier (86 chars), challenge = base64url(SHA-256(verifier)), checked against
RFC 7636's example in the tests. Kick requires the client secret on the token endpoint even with PKCE, so it is
stored in `kick.json` (never logged).

Tokens are refreshed when they expire within a minute, and once more after any `401` (single-flight per account).
A refresh rejected with 400/401 clears the account (sign in again).

Scopes requested — broadcaster: `user:read channel:read channel:write chat:write events:subscribe moderation:ban
moderation:chat_message:manage kicks:read channel:rewards:read`; optional bot account: `user:read chat:write
moderation:ban moderation:chat_message:manage`.

## 2. Public API (official) — `Api/KickApiClient.cs`

Base `https://api.kick.com`, `Authorization: Bearer <user token>`, JSON bodies, responses `{ "data": …, "message": … }`.

| Use | Endpoint |
| --- | --- |
| Signed-in user | `GET /public/v1/users` (no ids = token owner) → `user_id, name, email, profile_picture` |
| Users by id | `GET /public/v1/users?id=1&id=2` |
| Own channel / by slug / by id | `GET /public/v1/channels` / `?slug=` / `?broadcaster_user_id=` → `broadcaster_user_id, slug, stream_title, category{id,name,thumbnail}, channel_description, stream{is_live, viewer_count, start_time, language, is_mature, custom_tags}, active_subscribers_count` |
| Title / category | `PATCH /public/v1/channels` `{stream_title?, category_id?}` → 204 |
| Category search | `GET /public/v1/categories?q=` (documented keyword search; marked deprecated in favour of `GET /public/v2/categories?name=`, which only matches names exactly) and `GET /public/v1/categories/{id}` |
| Send chat | `POST /public/v1/chat` `{content (≤500 graphemes, ≤2048 bytes), type: "user"|"bot", broadcaster_user_id (for user), reply_to_message_id?}` → `{message_id, is_sent}` |
| Delete message | `DELETE /public/v1/chat/{message_id}` → 204 (`moderation:chat_message:manage`) |
| Ban / timeout | `POST /public/v1/moderation/bans` `{broadcaster_user_id, user_id, duration? (minutes 1–10080; omit = permanent), reason? (≤100)}` |
| Unban / untimeout | `DELETE /public/v1/moderation/bans` `{broadcaster_user_id, user_id}` |
| Webhook public key | `GET /public/v1/public-key` → `data.public_key` (PEM) |
| Event subscriptions | `GET/POST/DELETE /public/v1/events/subscriptions`; POST `{method:"webhook", events:[{name, version}]}` |

Cuebot splits long chat messages into Kick-sized parts at word boundaries. "Send as bot" uses the separately
signed-in bot account with `type: "user"` into the broadcaster's chat (falls back to the broadcaster when
allowed). Timeouts are given in seconds by users and rounded **up** to Kick's whole minutes.

The official API has no endpoint for the chatroom id, follower list/count, or real-time events other than
webhooks. Polling `GET /channels` (default every 60 s, minimum 15 s) gives stream online/offline, viewer count and
title/category changes without a public URL.

## 3. Webhooks (official) — `Webhooks/`

Kick delivers events **only** by HTTP POST to the webhook URL configured in the app, i.e. a public URL. Cuebot
will receive them through the user-hosted Cloudflare relay (see docs/PARITY.md, "Webhook-based services"); the
module already contains everything the relay connection needs: `KickWebhookEventSource.HandleAsync(delivery)`.

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

## 4. Real-time websocket (UNOFFICIAL) — `Pusher/`

kick.com's website receives chat and channel events from Pusher Channels:

- URL: `wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0&flash=false`
  (public app key `32cbd69e4b950bf97679`, cluster `us2`; older clients used key `eb1d5f283081a78b932c`).
- Public channels, no auth: `chatrooms.{chatroomId}.v2`, `chatroom_{chatroomId}`, `channel.{channelId}`,
  `channel_{channelId}`. Events can arrive on more than one of them; the hub de-duplicates.
- The **chatroom id** and **channel id** are internal ids (not the broadcaster user id) that the official API does
  not return. Cuebot reads them from the website API `GET https://kick.com/api/v2/channels/{slug}` (`id`,
  `chatroom.id`, `user_id`), sent with a browser User-Agent. kick.com is behind Cloudflare and sometimes blocks
  such requests (403 or a challenge page); then Cuebot keeps working without real-time events and the user can
  enter both ids on the Kick page (open that URL in a browser and copy them). They are cached in `kick.json`.

Protocol (Pusher 7): server sends `pusher:connection_established` with `{socket_id, activity_timeout}` (data is a
JSON-encoded string, as for every event). Client sends `{"event":"pusher:subscribe","data":{"auth":"","channel":…}}`
per channel, gets `pusher_internal:subscription_succeeded`. The client answers `pusher:ping` with `pusher:pong`
and sends its own `pusher:ping` after `activity_timeout` seconds without traffic; no reply within 30 s →
reconnect. `pusher:error` / close codes: 4000–4099 don't reconnect (Cuebot retries only at the maximum delay),
4100–4199 back off, 4200–4299 reconnect immediately. Reconnects back off exponentially from 1 s to 60 s.

Events decoded (event names are `App\Events\…`; payload fields as seen on the website):

| Pusher event | Data (relevant fields) | Cuebot event |
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

Reliable only through the **webhook relay** (best effort on the websocket today): **Kicks gifted**, **channel
reward redemptions** (including accepted/rejected status updates), follows with full user ids, and every event
when the website's websocket or its id lookup is unavailable. All of them are parsed and verified already; the
relay only has to forward deliveries.

## 7. Not implemented

- 7TV emote added/removed triggers (need 7TV's event API, not Kick).
- Kick channel-reward management sub-actions (the API has `channel:rewards:write`; planned with the rewards UI).
- `type: "bot"` chat (posting as the app's own bot user) is supported by the API client but not exposed in the UI.
