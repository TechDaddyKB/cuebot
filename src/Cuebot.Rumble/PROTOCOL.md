# Rumble protocols used by Cuebot.Rumble

Rumble publishes exactly one documented API, the **Live Stream API**. Live chat reading and sending use the
endpoints of Rumble's own web chat, which are **unofficial**: they were learned from the open-source
[cocorum](https://github.com/thelabcat/cocorum) library (Apache-2.0, by Wilbur Jaywright) and may change without
notice. Cuebot re-implements the protocols; no code was copied.

Sources:

- Rumble FAQ, "Rumble's Live Stream API":
  <https://rumblefaq.groovehq.com/help/how-to-use-rumble-s-live-stream-api> (official, includes a sample response)
- cocorum source: `src/cocorum/__init__.py` (Live Stream API), `chatapi.py` (SSE chat, sending),
  `servicephp.py` + `utils.py` (login and password hashing), `static.py` (URLs, timings), and
  `tests/test_chatapi_sse.py` (SSE event shapes): <https://github.com/thelabcat/cocorum>
- Server-sent events format: WHATWG HTML Living Standard, section "Server-sent events".

## 1. Live Stream API (official)

`GET <url from rumble.com → Account → Live Stream API>`; the URL carries a private key in its query string
(e.g. `https://rumble.com/-livestream-api/get-data?key=...`). Cuebot never logs it (`LiveStreamApiUrl.Redact`).

- **Polling:** Rumble asks for at most one request every 5 s; Cuebot defaults to 10 s and backs off on HTTP 429.
- **Top level:** `now` (unix seconds, server clock), `type` (`user`/`channel`), `user_id`, `username`,
  `channel_id`, `channel_name`, `max_num_results` (50).
- `followers`: `num_followers`, `num_followers_total`, `latest_follower`, `recent_followers[]` of
  `{username, followed_on}`.
- `subscribers`: `num_subscribers`, `latest_subscriber`, `recent_subscribers[]` of
  `{user, username, amount_cents, amount_dollars, subscribed_on}`.
- `gifted_subs`: `num_gifted_subs`, `latest_gifted_sub`, `recent_gifted_subs[]` of
  `{total_gifts, gift_type, remaining_gifts, video_id, purchased_by}` (no id, no timestamp).
- `livestreams[]`: `{id (base 36), title, created_on, is_live, categories, stream_key, likes, dislikes,
  watching_now, chat: {latest_message, recent_messages[], latest_rant, recent_rants[]}}`. Messages are
  `{username, badges[], text, created_on}` (no ids); rants add `expires_on, amount_cents, amount_dollars`.
  `stream_key` is secret and is not kept by the parser.
- Empty lists may contain a placeholder entry whose `username` is empty; such entries are ignored.
- Timestamps are ISO 8601 with offset (`+00:00`; the chat stream writes `+0000`).

Every response repeats the *recent* windows, so Cuebot diffs successive responses (`ActivityTracker`,
`NewItemFilter`): the first response after connecting is history and publishes nothing.

## 2. Live chat stream (unofficial)

- Chat id = the live stream id converted from base 36 to base 10 (`ChatId`).
- `GET https://web7.rumble.com/chat/api/chat/{chatId}/stream` → `text/event-stream`. No login needed to read.
- Events are unnamed (`data:` lines only); comments (`: `) are keep-alives. Each data payload is JSON
  `{"type": "...", "data": {...}}`:
  - `init`: sent on every (re)connect; `data.messages[]`, `data.users[]`, `data.channels[]`, `data.config`
    (badge definitions, rant levels, `message_length_max`). The messages replay recent history.
  - `messages`: new `data.messages[]` plus the `users[]`/`channels[]` they reference.
  - also `delete_messages`, `delete_non_rant_messages`, `pin_message`, ... (ignored by Cuebot).
- Message: `{id, time, user_id, channel_id?, text, rant?: {price_cents, duration, expires_on},
  gift_purchase_notification?, raid_notification?}`. User: `{id, username, link, color ("rrggbb"), badges[],
  is_follower}`. A message with `channel_id` was posted as that channel; its `username` is the display name.
- Badge slugs seen: `admin` (channel owner), `moderator`, `verified`, `premium` (Rumble Premium), `locals`,
  `recurring_subscription`, `locals_supporter` (channel supporters), `whale-grey/yellow/blue`.
- Reconnect: a dropped connection, a clean EOF, or ~90 s without bytes → reconnect after a short delay. **HTTP 204**
  on (re)connect means the chat has ended. Because `init` replays history, messages are de-duplicated by `id`.

## 3. Sending chat and signing in (unofficial)

Sending needs a rumble.com web session, i.e. the `u_s` cookie. Cuebot obtains it only when the user explicitly
signs in (`RumbleService.SignInAsync`), stores the cookie value (never the password) in `rumble.json`, and sends
the cookie only with chat requests (its HTTP client has no cookie jar).

Login (`POST https://rumble.com/service.php?name=<step>`, form-encoded, JSON answer `{"data": {...}}`):

1. `user.get_salts` with `username` → `data.salts` = three salts `s0, s1, s2`.
2. Hashes: `stretch(p, s) = md5(s + p)`, then 128 × `h = md5(h + p)` (hex). The `password_hashes` value is
   `md5(stretch(p, s0) + s1) + "," + stretch(p, s2) + "," + s1`.
3. `user.2fa.first_step` with `legacy_password=1, login, password=<hashes>, redirect_uri` → a non-empty
   `data.totp_id` means two-factor sign-in is required (not supported by Cuebot yet).
4. `user.login` with `username, password_hashes` → `Set-Cookie: u_s=...` (and a truthy `data.session`).

Send: `POST https://web7.rumble.com/chat/api/chat/{chatId}/message` with `Cookie: u_s=...` and JSON
`{"data": {"request_id": "<random 43 chars>", "message": {"text": "..."}, "rant": null, "channel_id": null}}`.
Limits: 200 characters per message, about one message per 3 seconds.

## What may break

Everything in sections 2 and 3: host names (`web7.rumble.com`), event shapes, badge slugs, the login steps and
hashing scheme, and Cloudflare protection on rumble.com (which may reject non-browser clients). When the chat
stream is unavailable Cuebot falls back to the (slower, id-less) chat in the Live Stream API.
