# Discord bot protocol notes

The streamer's own bot (their application and bot token from the Developer Portal), run from the desktop over the
Discord Gateway (WebSocket) and REST API v10. No hosted server. Written from Discord's public developer documentation
(docs.discord.com/developers, formerly discord.com/developers/docs), checked 31 October 2026. Outgoing *webhooks*
(`discord.basicWebhook`) are a separate, older feature in `src/Cuebot.Webhooks/Outgoing` and stay as they are.

## Files

| File | What |
| --- | --- |
| `Gateway/GatewayProtocol.cs` | Opcodes, intents, payload builders (identify, resume, heartbeat, presence), close-code rules. |
| `Gateway/GatewaySocket.cs` | `IGatewaySocket` (fakeable) over `ClientWebSocket`, with the close code. |
| `Rest/DiscordRestClient.cs` | HTTP with `Authorization: Bot <token>`, per-route rate limits, 429 retry, plain errors. |
| `Rest/DiscordApi.cs`, `Rest/DiscordModels.cs` | The calls Cuebot makes; message, embed and command JSON. |
| `DiscordBotService.cs` | Connection loop, session/resume state, server cache, events, slash command answers. |
| `DiscordTargets.cs` | Turns picks/names/mentions/links into ids. |
| `DiscordSubActions.cs`, `DiscordTriggers.cs` | Steps and triggers (category "Discord"). |

## Gateway

- `GET /gateway/bot` (bot token) gives the `url`; Cuebot connects to `<url>/?v=10&encoding=json` (no compression, no
  sharding: one shard handles up to 2,500 servers, far more than a streamer's bot). If the call fails for another reason
  than 401, `wss://gateway.discord.gg` is used.
- **Hello (op 10)** carries `heartbeat_interval` (ms). The first **Heartbeat (op 1)** waits `interval × jitter`
  (jitter random 0..1), then one per interval with `d` = last sequence number (or null). **Heartbeat ACK (op 11)** must
  arrive before the next beat, otherwise the connection is "zombied": close it with a non-1000 code and resume.
  **op 1 from Discord** asks for an immediate heartbeat.
- **Identify (op 2)**: `token`, `intents`, `properties {os, browser, device}`, optional `presence`. Answered by the
  `READY` dispatch: `session_id`, `resume_gateway_url`, `user`, `application.id`, unavailable `guilds`. Each guild then
  arrives as `GUILD_CREATE` with `channels` and `roles` (this fills Cuebot's server/channel/role lists).
- **Resume (op 6)**: `token`, `session_id`, `seq`, sent on a new connection to `resume_gateway_url` (plus the same
  query). Discord replays missed events and sends `RESUMED`. Closing with **1000 or 1001 invalidates the session** (and
  shows the bot offline), so Cuebot closes with **4000** whenever it wants to resume, and with 1000 when the user clicks
  Disconnect.
- **Reconnect (op 7)**: close (4000) and resume at once. **Invalid Session (op 9)**: `d: true` → resume; `d: false` →
  forget the session, wait a random 1-5 s, identify again on the `/gateway/bot` address.
- **Presence Update (op 3)**: `{since: null, activities: [{name, type, state?, url?}], status, afk: false}`. Activity
  types: 0 Playing, 1 Streaming (`url` must be a Twitch or YouTube link), 2 Listening, 3 Watching, 4 Custom (text in
  `state`), 5 Competing. Cuebot keeps the last one and sends it in the next Identify.
- Dispatches used: `READY`, `RESUMED`, `GUILD_CREATE/UPDATE/DELETE`, `CHANNEL_CREATE/UPDATE/DELETE`,
  `GUILD_ROLE_CREATE/UPDATE/DELETE`, `MESSAGE_CREATE`, `GUILD_MEMBER_ADD`, `INTERACTION_CREATE`.

### Intents

| Intent | Bit | Privileged | Cuebot |
| --- | --- | --- | --- |
| `GUILDS` | `1 << 0` | no | always (server, channel, role lists) |
| `GUILD_MEMBERS` | `1 << 1` | yes: "Server Members Intent" | only when ticked; needed for `GUILD_MEMBER_ADD` |
| `GUILD_MESSAGES` | `1 << 9` | no | always (`MESSAGE_CREATE`; without Message Content the text is empty) |
| `MESSAGE_CONTENT` | `1 << 15` | yes: "Message Content Intent" | only when ticked |

Privileged intents are switches on the portal's Bot page (Privileged Gateway Intents). The docs: apps with fewer than
10,000 users can switch them on themselves; above that, and for verified apps (needed at 100+ servers), Discord reviews
them. A streamer's own bot in their own server stays far below that.

### Close codes

| Code | Meaning | Cuebot |
| --- | --- | --- |
| 4000, 4001, 4002, 4003, 4005, 4008, no code | errors / dropped connection | reconnect and resume |
| 4007 invalid seq, 4009 session timed out | session gone | reconnect with a new Identify |
| 4004 authentication failed | bad token | stop: "Reset Token in the portal and paste the new one" |
| 4014 disallowed intents | a privileged switch is off in the portal | stop: names the switch(es) ticked in Cuebot |
| 4013 invalid intents, 4010/4011 sharding, 4012 API version | Cuebot bug / unsupported | stop with a plain message |

Reconnect backoff: 2 s doubling to 60 s; immediate after op 7 / op 9 true.

## REST

Base `https://discord.com/api/v10/`. Headers: `Authorization: Bot <token>`,
`User-Agent: DiscordBot (https://github.com/TechDaddyKB/cuebot, 1.0)` (Discord's required form), JSON bodies.
Interaction callback and interaction webhook calls need no `Authorization` and get none.

| Use | Call |
| --- | --- |
| Gateway address | `GET /gateway/bot` |
| Application id (invite link, commands) | `GET /applications/@me` (also `READY.application.id`) |
| Servers, channels, roles (Refresh button) | `GET /users/@me/guilds`, `GET /guilds/{g}/channels`, `GET /guilds/{g}/roles` |
| Send / edit / delete message | `POST /channels/{c}/messages`, `PATCH`/`DELETE /channels/{c}/messages/{m}` |
| Give / take role | `PUT` / `DELETE /guilds/{g}/members/{u}/roles/{r}` (204; `X-Audit-Log-Reason` URL-encoded) |
| Member by name | `GET /guilds/{g}/members/search?query=&limit=10` (user name or nickname prefix) |
| Direct message | `POST /users/@me/channels {recipient_id}`, then send to that channel |
| Slash commands | `PUT /applications/{app}/guilds/{g}/commands` (bulk overwrite, guild commands update instantly) |
| Acknowledge | `POST /interactions/{id}/{token}/callback {type: 5}` (+ `data.flags: 64` for "only they see it") |
| Answer | `PATCH /webhooks/{app}/{token}/messages/@original`; later answers `POST /webhooks/{app}/{token}` |

Message JSON: `content` (≤ 2000), `embeds: [{title ≤ 256, description ≤ 4096, url, color (int 0..0xFFFFFF), image: {url}}]`.
An edit without `content` keeps the text; `embeds: []` removes embeds. Command JSON: `{name, description, type: 1,
options: [{type: 3 (STRING), name, description, required}]}`; names match `^[-_\p{L}\p{N}]{1,32}$` and must be lowercase.

### Slash command timing

Discord drops an interaction not acknowledged within **3 seconds**, so Cuebot defers (type 5, "Bot is thinking…") as
soon as `INTERACTION_CREATE` arrives, before the actions run. The token then stays valid for **15 minutes**. The run's
`%discordInteractionId%` lets "Reply to slash command" find it (tokens are not put in variables). Cuebot counts the runs
started for that interaction (`ActionEngine.RunStarted/RunCompleted`); when they all finished (or none started within
3 s) and none replied, it edits the answer to the default reply ("Done ✅"). A run that only starts after that (a busy
queue) can still reply: the edit replaces the default.

### Rate limits

Per route (method + path with ids replaced except the major parameters channel, guild, webhook id/token):
`X-RateLimit-Remaining` 0 with `X-RateLimit-Reset-After` (seconds) makes the next call on that route wait. A **429**
body has `retry_after` (seconds, float) and `global`; Cuebot waits and retries (up to 4 attempts), and a global limit
blocks every route. A wait over 60 s fails with "Discord is rate limiting the bot" instead of hanging the action.
Discord's own bucket hashes (`X-RateLimit-Bucket`) are not tracked: per-route keys are a little more conservative.

### Errors shown to streamers

| JSON code | Message |
| --- | --- |
| 50013 Missing Permissions | "The bot is missing a permission: it needs \"Send Messages\"…" (the step names what it needs; roles add the role-order hint) |
| 50001 Missing Access | "The bot cannot see that channel. Give the bot's role \"View Channel\"…" |
| 10003 / 10008 / 10007 / 10011 / 10004 | unknown channel / message / member / role / server |
| 50007 | the user does not accept direct messages |
| 10062 | slash command too old to answer (15 minutes) |
| 50035 | first field error, e.g. `embeds.0.url: Not a well formed URL.` |
| HTTP 401 | bad token: reset it in the portal |

## Invite link

`https://discord.com/oauth2/authorize?client_id={app}&scope=bot%20applications.commands&permissions=2416012288`:
View Channels (1<<10), Send Messages (1<<11), Manage Messages (1<<13), Embed Links (1<<14), Read Message History
(1<<16), Manage Roles (1<<28), Use Application Commands (1<<31). A bot can only give roles below its highest role.

## Testing

`tests/Cuebot.Discord.Tests` drives the service against an in-memory Gateway (`FakeGateway`) and REST API
(`FakeDiscordHttp`): hello/identify/heartbeat/ack, zombie detection and resume, op 7 and op 9, 4004/4014/4007/4009,
slash command defer + reply + follow-up + default reply through the real action engine, 429 and bucket waits, message
and embed JSON, roles, DMs, command registration. Not yet checked against the live Discord service (needs a bot token).
