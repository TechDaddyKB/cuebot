# Unified multistream mod chat: research and plan

- **Status (2026-11-12):** research and design only. Nothing here is built. Kris decides what gets built.
- **Owner:** Kris.
- **Scope:** one window where a moderator sees the Twitch, YouTube, Kick and Rumble chats of one streamer and moderates
  them with one set of tools; a record of mod actions; cross-platform identity links and linked bans.
- **Sources:** official API documentation and terms first, read on 2026-11-12 (section 12). Anything marked
  **[community]** or **[unofficial]** is not from the platform's own documentation. Nothing was tested against a live
  channel. Not legal advice.
- **Clean room:** no Streamer.bot or Speaker.bot code was read for this.

## 1. Executive summary

**Recommendation**

1. **Build it inside the Mod portal**, as a new Chat page plus a pop-out "mod view" layout, not as a separate add-on.
   The portal already has invites, sign-in, roles, device keys, end-to-end encryption and a change log; a separate
   add-on would have to rebuild all of it.
2. **The streamer's Cuebot stays the hub and holds the tokens.** A mod's click travels over the encrypted portal link;
   Cuebot checks the member's permission and calls the platform with the broadcaster's token, as it does today from the
   desktop chat. Cuebot's own log records which mod did it. Acting with the moderator's own platform token is possible
   on Twitch, YouTube and Kick and is kept as a later option (section 5, option C).
3. **Keep the action log and the identity links on the streamer's PC only**, served to mods through the existing
   relay. No D1, no KV. The relay stores nothing today and that promise is worth more than offline access to a log.
4. **Identity links have three confidence levels** (viewer-verified, confirmed by two people, suggested). A linked ban
   is never automatic: it always shows a preview of every account it will touch, and it can be undone as one unit.

**Biggest platform gaps**

| Platform | What the official API allows | What is missing |
|---|---|---|
| Twitch | Nearly everything: ban, timeout, unban, warn, delete, clear, chat modes, blocked terms, AutoMod queue, Shield Mode, suspicious users, unban requests, pins. A moderator's own token works for all of it. | Mod notes (website only). No endpoint lists suspicious users. |
| Kick | Ban, timeout, unban, delete message. A moderator's own token works. | Everything else: clear chat, slow/followers/subscribers/emote-only modes, blocked words, AutoMod, pins. The only moderation event is `moderation.banned`. |
| YouTube | Ban, timeout, unban, delete message. A moderator's own token works. | Slow mode, members-only and subscribers-only chat, blocked words, held-message review, pins, "hide user". **Quota is the real limit:** a ban costs 200 of 10,000 daily units, so about 50 bans a day for the whole API project. |
| Rumble | **Nothing.** The only official API is a read-only JSON feed. | All moderation. Rumble's website can delete, mute (5 minutes, this stream, or permanently) and pin, but only its internal, undocumented endpoints do that, and Rumble's terms forbid automated use without written permission. |

**Top open questions for Kris** (full list in section 11)

1. Rumble: read-only with a "moderate on Rumble" shortcut, or opt-in moderation through the unofficial endpoints
   Cuebot already uses for reading and sending chat?
2. YouTube quota: the built-in Google project's 10,000 units a day are shared by every Cuebot user. Apply for a quota
   extension, or require streamers who moderate YouTube from Cuebot to use their own Google project?
3. Is "the platform's own mod log shows the broadcaster, Cuebot's log shows the real mod" acceptable for the first
   version, or is acting with each moderator's own token a requirement from the start?
4. May a Standard Mod place linked bans, or only linked timeouts?

## 2. What Cuebot has today

Paths are relative to the repository root.

### Moderation abstraction
- `src/Cuebot.Core/Chat/Moderation/ChatModeration.cs`: `ChatModerationActions` has three flags (`DeleteMessage`,
  `Timeout`, `Ban`). `IChatModerator` has `DeleteMessageAsync`, `TimeoutAsync`, `BanAsync` and a live `Available`.
  `ChatModerationService` finds the moderator for a platform.
- Implementations: `TwitchChatModerator` (`src/Cuebot.Twitch/TwitchChat.cs`), `YouTubeChatModerator`,
  `KickChatModerator`. **Rumble has none.**
- Every implementation acts with the **broadcaster's** token. The bot accounts are never used for moderation, and no
  moderator-owned token is ever kept (portal sign-in revokes the provider token at once).
- The Twitch module already has far more Helix moderation than the interface exposes (`Helix/TwitchApi.cs`,
  `TwitchApi.Extra.cs`): unban, warn, clear chat, pins, Shield Mode, blocked terms, suspicious users, unban requests,
  the banned list, AutoMod settings. They are reachable as action steps, not from the chat window.
- YouTube and Kick both have an unban call that the interface does not expose.

### Chat
- `src/Cuebot.Core/Chat/ChatHub.cs` merges chat from `IChatEventAdapter`s, keeps the last 500 messages in memory and
  re-publishes `chat.message`.
- `ChatMessage(Platform, Id, User, UserId, Text, Time)` plus roles, colour and amount. It has no login name separate
  from the display name, no badge list, and no channel or broadcast id.
- Reading: Twitch EventSub WebSocket; YouTube `liveChatMessages.streamList` with polling as a fallback; Kick official
  webhooks plus the unofficial realtime socket; Rumble the **unofficial** chat stream (`web7.rumble.com`), with the
  official Live Stream API polled only for follows, subs and rants.
- `src/Cuebot.Core/Users/UserRegistry.cs` stores one record per `platform:userId` in `users.json`. `ViewerData`
  handles "forget this viewer" and a 30-day YouTube retention rule. **There is no cross-platform identity.**

### Mod portal
- One Cloudflare Worker (`portal/worker`) with one Durable Object per relay ID. No D1, KV or R2. It forwards
  end-to-end encrypted frames and stores nothing except the Kick webhook route ("Kick user id to relay ID", 30 days).
- Mods join by a one-time invite and sign in with Google, Twitch or Discord. Cuebot keeps the provider's permanent
  user ID, not a token. Each device holds a non-extractable signing key; there is no bearer token to steal.
- Roles (`PortalRole`) combine a step risk ceiling with permissions and a change mode (Live, Live and tell me, Needs
  approval). `PortalChanges` logs changes to actions, commands, uploads, overlays and widgets, with undo.
- **The portal has no chat page and no moderation operations.** A mod can ban only by running an action.
- **The link is request and response only.** Cuebot cannot push; pages poll every 3 to 5 seconds.
- Storage is JSON files (`JsonFile<T>`) and an encrypted `secrets.bin`. There is no database.

### Existing features that are wrong or incomplete

| # | Where | Problem | Effect on this plan |
|---|---|---|---|
| 1 | `PortalMember.RequireTwitchModerator` | Stored and shown, never enforced. `website/docs/guides/mods/mod-portal.md` says it pauses access. | Must be enforced before mods get ban rights (phase 0). |
| 2 | `YouTubeService.BanAsync` | Timeouts and bans always go to the latest broadcast's chat, not the chat the message came from. | Wrong chat when two broadcasts are monitored. `ChatMessage` needs a chat id. |
| 3 | `YouTubeService._banIds` | Ban ids are kept in memory only. YouTube has no "list bans" call, so after a restart Cuebot cannot unban anyone. | The action log must store the ban id (section 6). |
| 4 | `ChatHub` | Ignores `chat.messageDeleted`, `chat.userBanned` and `chat.userTimedOut`, which the platform modules already publish. | A message deleted on the website stays visible in Cuebot; other mods' actions are invisible. |
| 5 | Chat adapters | When a platform message has no id, a random GUID is substituted. Deleting it then fails with a confusing error. | Mark such messages "cannot be deleted". |
| 6 | Twitch `channel.moderate` | Subscribed, but only shared-chat actions are mapped. | The full event is the source for "who did what" on Twitch. |
| 7 | Rumble `delete_messages` stream events | Ignored. | Deleted Rumble messages stay visible. |
| 8 | `IChatAnnouncer` | Defined; no platform implements it. | Not needed for this plan; noted. |
| 9 | Kick timeouts | Seconds are rounded up to whole minutes (Kick's unit), silently. | The UI must show the duration that will really apply. |
| 10 | `reason` on YouTube | Dropped (the API has no reason field). | Correct, but the reason must be kept in Cuebot's log. |

## 3. Capability matrix

"Mod token" means the moderator's own account authorizing the app, acting in a channel they moderate.
"B" = broadcaster's token, "M" = moderator's token. Costs: Twitch and Kick have rate limits, YouTube has quota units.

### 3.1 Core actions

| Action | Twitch | YouTube | Kick | Rumble |
|---|---|---|---|---|
| **Ban** | API. B or M. `moderator:manage:banned_users`. Reason up to 500 chars. | API. B or M. `liveChatBans.insert` `type=permanent`. **200 units.** No reason field. | API. B or M. `moderation:ban`. Reason up to 100 chars. | **No API.** Website: mute "permanently". [unofficial] internal endpoint. |
| **Timeout** | API. 1 s to 14 days. Same scope. | API. `type=temporary`, `banDurationSeconds` (default 300; limits not documented). **200 units.** | API. Whole minutes, 1 to 10,080 (7 days). | **No API.** Website: mute for 5 minutes or for this stream. |
| **Unban / end timeout** | API. B or M. | API. `liveChatBans.delete`, **200 units**, needs the ban id from the insert; bans cannot be listed. | API. B or M. | **No API.** Website: unmute. |
| **Delete message** | API. B or M. `moderator:manage:chat_messages`. Under 6 hours old; not a mod's message. | API. B or M. `liveChatMessages.delete`. **20 units.** | API. B or M. `moderation:chat_message:manage`. | **No API.** Website: delete. |
| **Warn** | API. B or M. `moderator:manage:warnings`. User must acknowledge before chatting. | None. | None. | None. |
| **Clear chat** | API. B or M. | None. | Website and `/clear` only. | None documented. |

### 3.2 Chat modes and filters

| Action | Twitch | YouTube | Kick | Rumble |
|---|---|---|---|---|
| Slow mode | API (3 to 120 s). B or M. `moderator:manage:chat_settings`. | Website only (owner). | Website or `/slow` only. | Not documented officially ([community]: exists). |
| Followers-only | API (0 to 129,600 min). | Does not exist. | Website only. | Not documented. |
| Subscribers / members-only | API. | Website only. Members-only start and end are visible as chat events. | Website only. | Website toggle or `/subscribers`. |
| Emote-only | API. | Does not exist. | Website only. | Does not exist. |
| Unique chat, non-mod chat delay | API. | Does not exist. | Does not exist. | Does not exist. |
| Blocked terms | API: list, add, remove. `moderator:manage:blocked_terms`. Private terms only for the broadcaster. | Website only (owner, managing moderators). | Website only (open request in Kick's tracker). | Website only (owner). |
| AutoMod held messages | API: approve or deny (`moderator:manage:automod`); events `automod.message.hold`. | Setting and review queue are website only. | Website only. | None. |
| Shield Mode | API. `moderator:manage:shield_mode`. | None. | Website "emergency tools" only. | None. |
| Suspicious users | API: set or clear monitoring or restriction; events for their messages. No list endpoint. | None ("hide user" is website only). | None. | None. |
| Unban requests | API: list and resolve. `moderator:manage:unban_requests`. | None. | Not documented. | None. |
| Pin message | API: pin, update, unpin (30 to 1,800 s). | Website only, **owner only**. | Website only. | Website (owner and mods). |
| Mod notes | Website only. | None. | None. | None. |
| Add / remove moderators | API, broadcaster only. | API (`liveChatModerators`), owner only, 200 units. | `/mod`, owner only. | Website, owner only. |

### 3.3 Reading chat and mod actions

| | Twitch | YouTube | Kick | Rumble |
|---|---|---|---|---|
| Read chat | EventSub `channel.chat.message` (`user:read:chat`). | `liveChatMessages.streamList` (about 5 units per connection) or `list` (5 units per poll; polling for 3 hours costs more than the daily quota). | Official webhook `chat.message.sent`. [unofficial] website socket. | Official feed: last 50 messages, **no message ids**. [unofficial] chat stream with ids. |
| See others' mod actions | `channel.moderate` v2: every action, with the moderator's name. A moderator can subscribe. | `userBannedEvent` names the moderator. Deletions appear, but who deleted is not reliably exposed. | `moderation.banned` only. No unban, delete or mode events. | [unofficial] delete events on the chat stream. Nothing official. |
| Limits | Token bucket per app and user; read the `Ratelimit-*` headers. An unpublished per-channel ban limit. WebSocket: 3 connections, cost 10. | **10,000 units a day per API project**, shared by every user of that project. | Not published; 429 on overuse. `chat.message.sent` capped at 1,000 subscriptions until the app is verified. | Polling only. |
| App review | None for Helix. | OAuth verification for the sensitive `youtube` scope; an unverified app is capped at 100 users for life. A quota extension needs a compliance audit. | Verification raises the event cap. | None; no developer programme was found. |

### 3.4 Can a moderator's own account act through the API?

| Platform | Answer |
|---|---|
| Twitch | **Yes.** Pass `moderator_id` = the token's user. Not for: listing bans, managing moderators or VIPs, raids. |
| YouTube | **Yes** for ban, unban and delete: the docs require "the channel owner or a moderator of the live chat". [community] Bots such as Nightbot rely on it. **Quota is still charged to the app's project**, so moderator tokens do not add quota. |
| Kick | **Yes.** Confirmed by Kick staff in the developer documentation's issue tracker (issue 77). |
| Rumble | No API at all. |

## 4. Gaps and how the window handles each

**Principle:** one toolbar, and every button states what it will do on each platform before the mod clicks. A button
never fails silently and never pretends.

Each tool has one of four states per platform:

- **Works**: the API does it.
- **Closest equivalent**: the API does something similar; the UI names the difference ("Kick: 10 minutes, rounded up
  from 9 min 30 s").
- **On the website**: no API; the button opens the platform's own moderation page or pop-out chat in the browser.
- **Not available**: the platform has no such feature. Shown greyed with a one-line reason.

| Tool | Twitch | YouTube | Kick | Rumble |
|---|---|---|---|---|
| Delete message | Works | Works (20 units) | Works | On the website (or section 4.1) |
| Timeout | Works, 1 s to 14 days | Works; limits untested (phase 0 checks) | Closest: whole minutes, 7 days at most | On the website (5 min or whole stream) |
| Ban | Works | Works (200 units) | Works | On the website (permanent mute) |
| Unban | Works | Works only for bans in Cuebot's log (needs the ban id); otherwise on the website | Works | On the website |
| Warn | Works | Closest: a chat message to the user from the streamer or bot account, clearly labelled as not a real warning; off by default | Same as YouTube | Not available |
| Clear chat | Works | Not available | On the website | Not available |
| Slow / followers / subs / emote-only | Works | On the website | On the website | On the website (subs-only) |
| Blocked terms | Works | On the website | On the website | On the website |
| Held messages (AutoMod) | Works, in a "Held" lane | On the website | On the website | Not available |
| Shield Mode | Works | Not available | On the website | Not available |
| Suspicious user flag | Works | Not available | Not available | Not available |
| Unban requests | Works | Not available | Not available | Not available |
| Pin | Works | On the website (owner only) | On the website | On the website |
| Mod notes | Cuebot's own notes (section 6), on every platform | same | same | same |

**"Apply to all platforms"** (for example slow mode) applies where the API allows and lists, in the same dialog, the
platforms where the mod must do it by hand, each with its link. The result line says "Slow mode on: Twitch. By hand:
YouTube, Kick, Rumble."

**Cuebot-side equivalents** where no platform tool exists:
- **Cuebot blocked terms**: a Cuebot list that deletes matching messages (and optionally times the sender out) on
  platforms with a delete API. It reacts after the message is visible, unlike a platform filter. On YouTube each
  deletion costs 20 units, so it needs a daily cap.
- **Mod notes and user history** live in Cuebot, so they exist for all four platforms and follow identity links.

### 4.1 Rumble

Three choices; Kris decides (open question 1).

| Option | What mods get | Risk |
|---|---|---|
| **A. Read-only (recommended default)** | Rumble chat in the window. Mod buttons open Rumble's own pop-out chat at that stream. Linked bans list the Rumble account as "do this one by hand". | None beyond today's. Note that Cuebot already reads Rumble chat from the unofficial stream. |
| B. Opt-in unofficial moderation | Delete, mute and unmute through Rumble's internal endpoints with the streamer's session cookie, which Cuebot already holds for sending chat. Off by default, labelled experimental. | Rumble's terms forbid automated software without written permission. Endpoints can change without notice. The streamer's account carries the risk. |
| C. Ask Rumble | Written permission, or access to the partner "Rumble Platform API" (its documentation page could not be read on 2026-11-12). | Slow, may get no answer. Worth one email whatever else is chosen. |

## 5. Architecture options

### 5.1 Inside the Mod portal or a separate add-on

| | Inside the Mod portal | Separate add-on |
|---|---|---|
| Sign-in, invites, roles, devices | Reused | Rebuilt, or a second way to reach the same Cuebot |
| Encryption and privacy promise | Same link, same promise | A second surface to secure and explain |
| Mods without the streamer online | Not possible (Cuebot must be running) | Possible only with tokens and data in the cloud |
| Work | A chat page, new operations, server push | All of that plus hosting, auth and a token vault |

**Recommendation: inside the Mod portal.** The one real advantage of a separate service is working while the
streamer's Cuebot is off. Chat moderation matters while the stream is live, and then Cuebot is running.

The window is one portal page with a "pop out" layout (chat only, full height) for a second monitor. The desktop app's
own Chat page gets the same tools through the same core service, so the streamer and the mods share one implementation.
The phone apps get it through the existing portal web view.

### 5.2 Who holds the tokens

| Option | How it works | For | Against |
|---|---|---|---|
| **A. Broadcaster's token in Cuebot (recommended first)** | As today. Cuebot checks the member's permission, calls the platform as the broadcaster, and logs the member. | Works now on three platforms. No new consent screens. Mods need no account on each platform. One place to revoke. Cuebot enforces roles the platforms do not have. | The platform's own mod log shows the broadcaster. Mitigation: Cuebot prefixes the ban reason with the mod's name on Twitch and Kick ("[mod: Sam] spam"). Cuebot becomes the only record of who acted on YouTube. |
| B. Bot account's token in Cuebot | Same, but calls go out as the streamer's bot, which must be a moderator. | The platform log shows the bot, not the streamer. | The bot must be modded everywhere; extra scopes; no gain in attribution. |
| C. Each moderator's own token | The mod authorizes Cuebot's app on each platform. | The platform's log names the real mod. The platform itself refuses a demoted mod. | Three consent flows per mod. Tokens must live somewhere: in the mod's browser (lost with the device, calls made from the browser) or in the streamer's Cuebot (the streamer now holds other people's tokens). No help with YouTube quota. Cuebot's role limits can be bypassed by a mod calling the API directly, which they could do anyway. |
| D. Tokens in the cloud | A Worker holds tokens and calls platforms. | Works with Cuebot off. | Breaks "the relay stores nothing". A token vault for every streamer is a large target. Not recommended. |

**Recommendation: A first, C later as an option per member on Twitch**, where the gain is largest (the full
`channel.moderate` feed and AutoMod events are per moderator). C needs a decision about where the mod's token lives;
the proposal is the mod's own browser, encrypted with the device key, never the cloud.

Acting with the broadcaster's token for a moderator is delegation by the account owner inside the owner's own app.
YouTube's policies require the authorizing user's express consent before actions are taken for them: the streamer
gives it by granting a member the permission, and the setup page should say so in plain words (section 9).

### 5.3 Where the log and the links are stored

| | Local-first: the streamer's Cuebot | Cloud: Worker with D1 or KV |
|---|---|---|
| Who can read it | The streamer; mods through the encrypted link, by permission | Whoever controls the Cloudflare account, unless everything is encrypted client-side |
| Available while Cuebot is off | No | Yes |
| Privacy position | The streamer is the only holder. Kris stores nothing about anyone. | Kris becomes a data processor for every streamer's ban lists and identity links: a processing agreement, breach duties, deletion requests, a privacy policy that covers it |
| Platform terms | Data stays with the channel owner who collected it | Twitch's developer agreement restricts passing data to third parties; YouTube's 30-day rule applies to a central store |
| Cost and operations | None | D1 schema migrations, backups, abuse limits, a bill that grows with users |
| Fits the current design | Yes: "nothing stored on the relay" | No: reverses a stated goal of the portal plan |
| Loss risk | The streamer's disk. Covered by Cuebot's backup. | Lower |

A middle path exists: the cloud stores only ciphertext that the streamer's key can read. It gives mods read access
while Cuebot is off, but they still cannot act, links still cannot be applied, and key sharing with every mod device
has to be built. The benefit is small.

**Recommendation: local-first.** The log and links live in the streamer's Cuebot data folder and are included in
backups. Mods read them through the portal. An export (CSV or JSON) covers the streamer who wants a copy elsewhere.

### 5.4 Shape of the build

```
Portal UI (Chat page, pop-out)      Desktop Chat page
        │  encrypted link, now with push       │
        ▼                                      ▼
   PortalChat (new portal ops) ──▶ ModerationService (Cuebot.Core, new)
                                        │  permission, safety checks, preview, log
                ┌───────────────┬───────┴───────┬──────────────┐
          TwitchModerator  YouTubeModerator  KickModerator  RumbleModerator
          (IChatModerator v2, capability descriptors)        (read-only or opt-in)
                                        │
                     ModLog (append-only)   IdentityLinks   QuotaBudget (YouTube)
```

New or changed pieces:

- **`IChatModerator` v2.** Replace the three flags with capability descriptors: for each action, a state (works,
  closest equivalent, on the website, not available), limits (minimum and maximum duration, unit, reason length), a
  cost (YouTube units) and a website link. Add unban, warn, clear, chat modes, pins and the Twitch-only tools as
  optional members. Every call returns a result with the platform's reference (YouTube ban id).
- **`ModerationService`** in `Cuebot.Core`: the single entry for the desktop page, the portal and action steps. It
  resolves links, builds previews, enforces safety rules, calls the platforms, and writes the log.
- **`ChatMessage`** gains a channel or chat id, the login name, and a "can be deleted" flag.
- **`ChatHub`** consumes deletion, ban and timeout events so every client sees them, and records actions made outside
  Cuebot (on the website) in the log with the actor the platform reports.
- **Server push on the portal link.** Chat cannot poll every 3 seconds. Add a subscription message type: the device
  subscribes to `chat` and Cuebot sends frames as events happen. This is already listed as "later" in the portal plan.
  The relay does not change: it forwards frames in both directions already. Frame and byte caps need a review for a
  busy chat (batch messages, drop to a summary when a device falls behind).
- **`YouTubeQuotaBudget`**: counts units spent today, shows the remainder, and refuses or asks before an action that
  would exhaust it.

## 6. Data model

Stored in the Cuebot data folder. Cuebot has no database today. The log is append-heavy and queried by user, so the
choice is between monthly append-only JSON Lines files with an in-memory index (no new dependency) and adding SQLite
(open question 7). The fields are the same either way.

### 6.1 `ModAction` (one row per platform call)

| Field | Notes |
|---|---|
| `id` | Random id. |
| `incidentId` | Groups the rows of one decision (a linked ban across three platforms is one incident). |
| `at` | UTC time. |
| `actorKind` | `streamer`, `member`, `automation` (a Cuebot action or blocked-terms rule), `external` (seen in a platform event, done on the website). |
| `actorMemberId`, `actorName` | The portal member; the name as it was then. For `external`, the platform's moderator name where reported. |
| `actorDeviceId` | Which signed-in device. |
| `platform`, `channelId` | Where. `channelId` is the YouTube live chat id where relevant. |
| `action` | `ban`, `timeout`, `unban`, `delete`, `warn`, `clear`, `mode`, `term`, `pin`, `note`, `link`, `unlink`. |
| `targetUserId`, `targetName` | Platform user id (the stable key) and the name at the time. |
| `messageId`, `messageText` | For deletions: the message, kept so a reviewer can judge the action. Subject to retention. |
| `durationSeconds` | Requested and applied (Kick rounds up). |
| `reason` | Required for bans and linked actions. |
| `viaLinkId` | Set when this row exists because of an identity link. |
| `platformRef` | The YouTube ban id; needed to unban. |
| `result` | `ok`, `failed` (with the platform's error), `skipped` (no API, quota, unchecked in the preview), `manual` (mod confirmed doing it by hand). |
| `undoneBy`, `undoneAt` | Points at the reversing row. |
| `prevHash` | Hash of the previous row, so edits and gaps are detectable. Tamper-evident, not tamper-proof. |

Rows are never edited or deleted by members. Erasure requests blank the personal fields and keep the row (section 9).

### 6.2 `IdentityLink`

A link joins two or more platform accounts into one **person**. Stored as a person record with member accounts, so
three or four accounts are one group, not a web of pairs.

| Field | Notes |
|---|---|
| `personId` | Random id. Never shown to viewers, never sent to a platform. |
| `accounts[]` | `platform`, `userId`, `nameAtLink`, `addedAt`, `addedBy`. |
| `status` | `suggested`, `confirmed`, `verified`, `rejected` (kept so the same wrong suggestion is not made again). |
| `evidence[]` | What supports it: `kind` (`viewer-said`, `verified-code`, `mod-note`, `same-name`), the message ids and a short quote, who added it, when. |
| `confirmedBy[]` | Members who confirmed, with time. |
| `note` | Free text. |
| `lastReviewed` | For the yearly review prompt and the YouTube 30-day refresh (section 9). |

### 6.3 `ModNote`
`personId` or one account, text, author, time. Follows links, so a note written on the Twitch account appears on the
linked YouTube account.

## 7. Identity links and the linked-ban flow

### 7.1 Making a link

| Way | How | Resulting status |
|---|---|---|
| **Viewer verifies (strongest)** | A mod starts "verify" on one account. Cuebot shows the mod a one-time code and the mod asks the viewer to type `!link CODE` from the other platform's account within 10 minutes (or the bot whispers it on Twitch). Both accounts have then proved control. | `verified` |
| **Mod asserts** | From a user card: "Same person as…", pick the other account from recent chatters or search, attach the messages that show it (the viewer saying so) and a note. | `suggested` until a second person confirms: the streamer or another member with the Confirm links permission. One-mod teams: the streamer confirms. |
| **Cuebot suggests** | Identical or near-identical names on two platforms, shown as a hint on the user card only. | Never more than a hint; it creates nothing until a mod asserts it. |

A viewer saying "I'm X on Twitch" from the YouTube account proves nothing about the Twitch account: anyone can claim
to be someone else, including to get them banned. That is why a one-sided claim stays `suggested` and why a `verified`
link needs action from **both** accounts. Ban evaders will not verify, so most enforcement links will be mod-asserted
and confirmed by a second person.

### 7.2 Mistake prevention

- A link can never include the broadcaster, a platform moderator or VIP, a portal member, or the bot accounts.
- An account belongs to one person. Adding it to a second asks to merge or move, and shows both histories.
- A group larger than four accounts, or with two accounts on the same platform, needs the streamer's confirmation.
  (Two accounts on one platform is a real case, an alt, but the unusual one.)
- Every link, confirmation, rejection and unlink is a `ModAction` row.
- `suggested` links are never used by default in a linked ban (the box starts unticked).

### 7.3 Undoing a link

- **Unlink** removes one account from the person or dissolves the group. It asks: "Also reverse the 2 actions that
  were applied because of this link?" and lists them (rows with this `viaLinkId` that are still in force). Reversing
  is ticked by default when the link is being marked **wrong**, unticked when it is merely no longer needed.
- A rejected link is remembered as `rejected` so it is not suggested again; the record holds only the two account ids.

### 7.4 The linked-ban flow

1. **Start.** The mod clicks Ban or Timeout on a message or user card. If the account belongs to a person with other
   accounts, the dialog becomes a linked action. Otherwise it is the plain single-platform dialog.
2. **Preview (always shown; this is the dry run).** One row per account:

   | | Account | Link | What will happen | Notes |
   |---|---|---|---|---|
   | ☑ | Twitch: `rowdy_guy` | (the one clicked) | Ban | |
   | ☑ | Kick: `rowdyguy` | verified 2026-10-02 | Ban | |
   | ☐ | YouTube: `Rowdy Guy` | suggested by Sam, not confirmed | Ban, 200 quota units (7,400 left today) | Unticked: not confirmed |
   | — | Rumble: `RowdyG` | confirmed by Kris and Sam | By hand: open Rumble | No API |

   For timeouts the row shows the duration that will really apply ("Kick: 10 min", "Twitch: 9 min 30 s").
   Cuebot refreshes each account's current name and state first, so a renamed or already-banned account is shown as
   such. Nothing has been sent to any platform at this point.
3. **Reason.** Required. One reason for the incident; trimmed per platform (Kick 100 characters) with the mod's name
   prefixed where the platform stores a reason.
4. **Confirm.** A second confirmation for a permanent ban that touches more than one platform ("Ban 3 accounts on 3
   platforms"). The role decides who may do this at all (section 8).
5. **Execute.** Each platform call is independent. The result shows per row: done, failed (with the platform's
   message and a retry), skipped, or "by hand" with the link and an "I did it" button that records `manual`.
   A failure on one platform does not roll back the others; the incident shows as partly applied.
6. **Tell the streamer.** Linked actions always notify the streamer, like "Live and tell me" changes.
7. **Undo.** "Undo incident" for 24 hours from the toast and at any time from the log: it unbans every row that
   succeeded, using the stored YouTube ban id. A deleted message cannot be restored on any platform; the undo dialog
   says so. Twitch warnings cannot be withdrawn. Undo is itself logged and costs YouTube quota (200 per unban).

**Later arrivals.** If a new account is linked to a person who is banned elsewhere, Cuebot shows "This person is
banned on Twitch and Kick. Apply here too?" It is a prompt, never automatic.

**No automatic propagation in the first version.** A ban placed on Twitch's own website appears in the log (as
`external`) and raises the same prompt for a mod to decide. A fully automatic "mirror every ban" switch can be
considered later for `verified` links only.

## 8. Security and abuse risks

| Risk | What could happen | Controls |
|---|---|---|
| Rogue moderator | Mass bans across four platforms in seconds; banning rivals through false links. | New permissions (below). Per-member rate limits (for example 10 bans and 30 timeouts per 10 minutes; linked bans 5 per hour), enforced in Cuebot. Linked permanent bans need a confirmed or verified link and, for Standard Mods, are downgraded to timeouts. The streamer is notified of every linked action and can undo an incident, or "undo everything this member did in the last hour", in one click. Removing the member ends all access at once. |
| False link as a weapon | A mod, or a viewer lying in chat, ties an innocent person to an offender. | One-sided claims never exceed `suggested`. A second person must confirm. Evidence is stored with the link. Unlink reverses what the link caused. |
| Stolen mod session | Someone uses a mod's unlocked browser or phone. | Device keys are non-extractable and there is no bearer token (already built). Add: the 12-hour idle timeout the portal plan lists as not built; a fresh provider sign-in before the first linked ban of a session; the streamer's "sign out this device". |
| Mod removed on the platform but still in the portal | A demoted mod keeps banning through the broadcaster's token. | Enforce the Twitch moderator check that is stored today and not enforced; extend it to YouTube (`liveChatModerators.list`, 50 units, owner token) and note that Kick and Rumble have no way to list moderators. The invite's role remains authoritative, as the portal plan decided. |
| Privilege confusion | With option A every mod acts with the broadcaster's power; platform limits on mods (cannot ban other mods) no longer apply. | `ModerationService` refuses targets that are the broadcaster, a platform mod or VIP (from chat roles and `IChannelRoles`), a portal member or a bot account, unless the actor is the streamer. |
| Quota exhaustion | A mod, or a blocked-terms rule, spends the YouTube quota; chat reading and the streamer's other YouTube features stop until midnight Pacific. | `YouTubeQuotaBudget` with a reserve that moderation cannot spend; a daily cap on automated deletions. |
| Log tampering | A mod hides what they did. | Members cannot edit or delete log rows through the portal. Rows are hash-chained. Only the streamer's PC holds the file. |
| Chat content as an attack | A chat message carries script or a lookalike name into the portal page. | The portal UI already runs under a strict CSP; render chat as text only; show the platform and the stable user id on the card, since display names can be copied. |
| Relay | The relay sees more traffic. | It still sees only ciphertext, sizes and timing. Chat volume makes traffic analysis slightly more informative (busy chat, live or not); this is already visible from the public stream. |
| Unofficial Rumble moderation (option B) | The streamer's session cookie performs moderation; a bug could mute the wrong user, and Rumble could act against the account. | Off by default; a clear warning; delete and mute only; the same permission and log path as every other platform. |

**New portal permissions** (added to `PortalPermission`, default roles in brackets):

| Permission | Standard Mod | Lead Mod | Tech Lead |
|---|---|---|---|
| View chat | Yes | Yes | Yes |
| Delete messages, timeout | Yes | Yes | Yes |
| Ban and unban | No (timeouts only), streamer can turn on | Yes | Yes |
| Chat modes, clear chat, Shield Mode | No | Yes | Yes |
| Blocked terms | No | Yes | Yes |
| View mod log | Own actions | All | All |
| Notes | Read and add | Read, add, delete | Read, add, delete |
| Propose links | Yes | Yes | Yes |
| Confirm links | No | Yes | Yes |
| Linked timeouts | Yes | Yes | Yes |
| Linked bans | No | Yes, second confirm | Yes, second confirm |

Moderation actions are not "changes" in the portal's approval sense: a timeout cannot wait for approval. They always
apply at once and are always logged.

## 9. Privacy and terms-of-service risks

### 9.1 What is kept
- **The mod log:** platform user ids and names of people acted on, the acting mod, the reason, and for deletions the
  message text.
- **Identity links:** that account A and account B are one person, with the evidence. This is the sensitive part: it
  can reveal that someone uses a platform they kept separate, and a link is a statement about a person made without
  their consent.
- **Notes:** free text about viewers.
- **Not kept:** provider tokens of mods (already revoked at sign-in), anything on the relay.

### 9.2 GDPR basics (not legal advice)
- **Roles.** The streamer decides why and how viewer data is used and is the controller. Kris, as the maker of software
  that stores data only on the streamer's PC, is not a processor of it. A cloud store would change that (section 5.3).
  Cloudflare still handles IP addresses in transit, as the portal plan states.
- **Legal basis.** Legitimate interest in keeping a community safe and stopping ban evasion. It needs to be balanced
  against the viewer's expectations: enforcement of the streamer's own chat fits; building profiles does not.
- **Minimisation.** Store ids, names at the time, and the minimum evidence. No IP addresses, no emails, no data from
  outside the streamer's own chats. No link suggestions from behavioural matching (writing style, timing): that is
  profiling, and Twitch's developer agreement forbids profiling users.
- **Retention (proposed defaults, streamer can shorten):** deleted-message text 30 days; timeout rows 90 days; ban and
  link rows while the ban or link stands plus 12 months; `suggested` links never confirmed 30 days; notes 12 months
  after last edit. `DataRetention` already prunes logs and gets these rules.
- **Access and deletion on request.** Extend the existing "forget this viewer" (`ViewerData`): find the person by any
  linked account, export what is held, and erase. Erasure deletes links, notes and message text and blanks names in
  log rows; the row and the platform user id of an **active ban** are kept so the ban still works and the log stays
  consistent, which GDPR allows where there is an overriding legitimate ground. The platform's own ban is unaffected.
  The streamer handles requests; the docs give them a short procedure and a template reply.
- **Transparency.** The streamer should tell viewers, in channel rules or a panel: moderation covers all platforms the
  stream is on, bans may be applied across them, and how to ask about or contest one. Provide sample wording.
- **Mods' data.** The log records what each mod did. Mods should be told at invite time; the invite page can say so.
- **Special categories.** Notes are free text. Warn in the UI not to record health, beliefs or similar.
- **No sharing between streamers.** No shared ban lists, no export to other communities. This is a privacy line and a
  terms line (below).

### 9.3 Platform terms

| Platform | Relevant rules | Effect |
|---|---|---|
| Twitch | Developer Agreement: use data only for the stated purpose; **no passing data to third parties without Twitch's written permission**; keep personal data only as long as needed; delete on user request or revocation; chat logs only as long as the service needs, with opt-out and deletion; no profiling. | Local-first storage and per-streamer scope fit. A cloud store or cross-streamer ban lists would not, without permission. Mods reading the log are the streamer's own team; say so in the docs. Cross-platform enforcement is not named in the agreement: applying a Twitch ban for behaviour seen elsewhere is the broadcaster's own decision, which Twitch lets broadcasters make. This reading is an inference. |
| YouTube | Developer Policies: API data may be stored 30 days, then deleted or refreshed; delete within 7 days of a user's request; a privacy policy with required statements; express consent before acting for a user; no combining data across different content owners. Sensitive-scope OAuth verification. | Cuebot already applies a 30-day rule to YouTube viewer data. Links and bans that contain a YouTube channel id must be **refreshed** (re-read from the API, 1 unit) at least every 30 days or dropped; build that into `IdentityLinks`. The ban log row is the streamer's own record of an action they took, but treat the id the same way to be safe. Linking a YouTube account to a Twitch account is not addressed directly; the "do not combine across content owners" rule is about different channels' data, not about one viewer on two platforms. Confirm during the pending Google verification, which must also describe this use. |
| Kick | The general terms forbid scraping, bots and harvesting; no public API-specific data rules were found; "Developer Terms and Conditions" are referenced at app setup but no public copy was found. Rate limits are unpublished. | Official endpoints only for moderation. Read the developer terms shown in the app setup page before building (phase 0). Cuebot's use of the unofficial realtime socket for reading is an existing risk, unchanged. |
| Rumble | Terms forbid automated software and systematic data retrieval without written permission, and harvesting chat users' information. | Storing Rumble user ids in links comes from the unofficial chat stream (the official feed has no ids). Option A keeps Rumble read-only; even so, a Rumble account in a link is data from an unofficial source. Ask Rumble (section 4.1, option C). |

**Acting through the broadcaster's token for someone else.** No platform's terms were found to forbid an account
owner delegating actions inside their own tool; it is how every chat bot works. The risks are attribution (the
platform holds the streamer responsible for everything done with the token) and consent (YouTube). Both are handled by
the permission model, the log, and plain wording when the streamer grants ban rights: "Bans by this member are made
with your account."

## 10. Phased implementation plan

Each phase is shippable on its own. Tests follow the repository's conventions: xunit, HTTP fakes (`ApiHarness`),
`Avalonia.Headless` UI tests, vitest for the portal UI, and opt-in live tests behind environment variables. **No live
test bans, times out or messages a real viewer:** live checks use the project's own test channel and test accounts.

| Phase | Work | Files and modules | Tests |
|---|---|---|---|
| **0. Fixes and checks** | Enforce `RequireTwitchModerator`. YouTube bans go to the message's own chat. Persist YouTube ban ids. `ChatHub` consumes delete, ban and timeout events (and Rumble's `delete_messages`). Mark messages without a real id as not deletable. Confirm on a test channel: YouTube `banDurationSeconds` limits, a moderator token on YouTube, Kick's developer terms text. | `src/Cuebot.Portal/Members/PortalDirectory.cs`, `src/Cuebot.YouTube/YouTubeService.cs`, `src/Cuebot.Core/Chat/ChatHub.cs`, `ChatModel.cs`, the three chat adapters, `src/Cuebot.Rumble/Chat` | `Cuebot.Portal.Tests` (suspension when mod status is lost), `YouTubeServiceTests` (two broadcasts), `ChatHubTests`, `ChatPageTests` |
| **1. Moderation core** | `IChatModerator` v2 with capability descriptors; unban, warn, clear, chat modes, pins; `ModerationService`; the mod log; `YouTubeQuotaBudget`; the desktop Chat page uses all of it and gains a Mod log view and user cards. | `src/Cuebot.Core/Chat/Moderation/*` (new `ModerationService.cs`, `ModLog.cs`, `ModerationCapabilities.cs`), `TwitchChat.cs`, `YouTubeChatModerator.cs`, `KickChatModerator.cs`, new `RumbleChatModerator.cs` (descriptors only), `src/Cuebot.App/ViewModels/ChatViewModel.cs`, `Storage/DataRetention.cs` | Per-platform moderator tests against HTTP fakes (limits, rounding, errors); log tests (append, hash chain, retention, undo pairing); quota budget tests; headless UI tests for each button state |
| **2. Portal chat** | Push subscriptions on the portal link; `PortalChat` operations (subscribe, history, act, log); new permissions and default roles; the Chat page and pop-out in the portal UI; per-member rate limits and target protection. | `src/Cuebot.Portal/Relay/PortalRelayClient.cs`, new `src/Cuebot.Portal/Chat/PortalChat.cs`, `Members/PortalModel.cs`, `DefaultRoles.cs`, `PortalPolicy`, `portal/ui/pages/chat.tsx`, `portal/ui/api.ts`, `portal/src` loader (push frames), `portal/RELAY.md`; the Worker only if caps change | `Cuebot.Portal.Tests` (every op by role, rate limits, protected targets); vitest for the page; Worker tests for frame caps; `PortalUiLiveTests` with a fake chat source through the deployed relay; a load test with a fast chat |
| **3. Twitch depth** | Full `channel.moderate` mapping into the log; Held (AutoMod) lane; Shield Mode, suspicious users, unban requests, blocked terms in the window. | `src/Cuebot.Twitch/EventSub/*`, `TwitchScopes.cs` (new scopes mean streamers re-authorize), `TwitchChat.cs`, portal and desktop UI | `Cuebot.Twitch.Tests` with recorded event fixtures; UI tests |
| **4. Identity links** | The person model, user cards across platforms, propose, confirm, reject, unlink, `!link` verification, notes, the 30-day YouTube refresh, "forget viewer" and export. | New `src/Cuebot.Core/Users/IdentityLinks.cs`, `ViewerData.cs`, `UserRegistry.cs`, a command handler for `!link`, portal and desktop UI | Link rules (protected accounts, merges, two-person confirm), verification expiry and replay, erasure and export, refresh job |
| **5. Linked bans** | The preview, per-platform execution, partial failure, manual rows, incident undo, later-arrival prompts, streamer notices, role limits. | `ModerationService.cs`, `PortalChat.cs`, UI dialogs on both clients, `website/docs/guides/mods/*` | Table-driven preview tests (every mix of capability, link status and quota), undo tests including a failed unban, UI tests for the dialog; a scripted end-to-end run against fakes for all four platforms |
| **6. Options** | Decided by Kris: Cuebot blocked terms; moderator-owned tokens on Twitch (option C); Rumble option B; an automatic mirror for verified links. | Per option | Per option |
| **Docs** (with each phase) | User guide for mods and streamers, the privacy notes and sample viewer wording, changelog, `docs/MOD_PORTAL_PLAN.md` updated to point here. | `website/docs/guides/mods/`, `docs/` | mkdocs strict |

Rough order of size: phase 0 small; 1 and 2 large; 3 medium; 4 medium; 5 medium.

## 11. Open questions for Kris

1. **Rumble:** option A (read-only, moderate on the website), B (opt-in, unofficial endpoints) or both with B off by
   default? Should we email Rumble about permission or the partner API first?
2. **YouTube quota:** the built-in Google project's 10,000 units a day are shared across all Cuebot users; one ban is
   200. Apply for an extension (needs the compliance audit, after the pending OAuth verification), require a
   streamer's own Google project for YouTube moderation, or both?
3. **Attribution:** is the broadcaster's token with the mod's name in the reason and in Cuebot's log enough for the
   first version, or must mods act with their own tokens on Twitch from the start?
4. **Standard Mods:** timeouts only by default, or bans too? Linked timeouts but not linked bans?
5. **Link confirmation:** is a second person always required for mod-asserted links, including for small teams where
   that second person is always the streamer?
6. **Automatic mirroring:** never, or allowed later for viewer-verified links?
7. **Storage:** append-only JSON Lines files (no new dependency) or add SQLite for the log and links?
8. **Retention defaults** in section 9.2: acceptable, and should deleted-message text be kept at all?
9. **Soft warnings** on YouTube and Kick (a chat message standing in for Twitch's warn): wanted, or leave "not
   available"?
10. **Who sees the log:** may Standard Mods see other mods' actions, or only their own?
11. **Should the streamer's desktop Chat page and the portal page ship together**, or the portal first since mods are
    the ones with four tabs open?
12. **Legal review:** the viewer-facing wording and the GDPR position should be checked by someone qualified before
    identity links ship. Who?

## 12. Sources

All accessed 2026-11-12. Detailed research notes with every claim's source are in the session notes folder
(`research-twitch-mod.md`, `research-youtube-mod.md`, `research-kick-rumble-mod.md`).

### Twitch (official)
- API reference: https://dev.twitch.tv/docs/api/reference/
- API guide (rate limits): https://dev.twitch.tv/docs/api/guide/
- EventSub reference: https://dev.twitch.tv/docs/eventsub/reference/
- EventSub subscription types: https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/
- EventSub WebSocket: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
- EventSub webhooks: https://dev.twitch.tv/docs/eventsub/handling-webhook-events/
- Managing subscriptions: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/
- Scopes: https://dev.twitch.tv/docs/authentication/scopes/
- Chat and chatbot guide: https://dev.twitch.tv/docs/chat/ and https://dev.twitch.tv/docs/chat/chatbot-guide/
- Developer Agreement: https://legal.twitch.com/legal/developer-agreement/
- [community] Forum thread on the moderation API (no mod-notes API): https://discuss.dev.twitch.com/t/the-moderation-api/21721

### YouTube (official)
- liveChatBans: https://developers.google.com/youtube/v3/live/docs/liveChatBans and `/insert`
- liveChatMessages: https://developers.google.com/youtube/v3/live/docs/liveChatMessages and `/list`, `/streamList`,
  `/insert`, `/delete`, `/transition`
- liveChatModerators: https://developers.google.com/youtube/v3/live/docs/liveChatModerators
- liveBroadcasts: https://developers.google.com/youtube/v3/live/docs/liveBroadcasts
- Revision history: https://developers.google.com/youtube/v3/live/revision_history
- Quota costs: https://developers.google.com/youtube/v3/determine_quota_cost
- Quota and compliance audits: https://developers.google.com/youtube/v3/guides/quota_and_compliance_audits
- API Services Terms of Service: https://developers.google.com/youtube/terms/api-services-terms-of-service
- Developer Policies: https://developers.google.com/youtube/terms/developer-policies
- OAuth scopes: https://developers.google.com/identity/protocols/oauth2/scopes
- OAuth verification and user caps: https://support.google.com/cloud/answer/13463817,
  https://support.google.com/cloud/answer/15549945, https://support.google.com/cloud/answer/7454865
- YouTube Help, live chat moderation: https://support.google.com/youtube/answer/9826490,
  https://support.google.com/youtube/answer/10888907, https://support.google.com/youtube/answer/15739725,
  https://support.google.com/youtube/answer/2524549
- [community] Timeout lengths in the UI: https://ppc.land/youtube-blocks-live-chat-users-for-up-to-24-hours-with-6-timeout-intervals/
- [community] Scope sensitivity: https://www.getphyllo.com/post/youtube-oauth-scopes
- [community] Old client-library docs (deleted-message event): https://googleapis.github.io/google-api-python-client/docs/dyn/youtube_v3.liveChatMessages.html

### Kick (official)
- Moderation API: https://docs.kick.com/apis/moderation
- Chat API: https://docs.kick.com/apis/chat
- Scopes: https://docs.kick.com/getting-started/scopes
- App setup and OAuth: https://docs.kick.com/getting-started/kick-apps-setup,
  https://docs.kick.com/getting-started/generating-tokens-oauth2-flow
- Events: https://docs.kick.com/events/introduction, https://docs.kick.com/events/event-types,
  https://docs.kick.com/events/subscribe-to-events, https://docs.kick.com/events/webhook-security
- Changelog and FAQ: https://docs.kick.com/changelog, https://docs.kick.com/apis/faqs
- Developer docs repository and issue 77 (moderator tokens): https://github.com/KickEngineering/KickDevDocs,
  https://github.com/KickEngineering/KickDevDocs/issues/77
- Help centre, moderation: https://help.kick.com/en/articles/7109164-how-to-moderate-your-kick-chat,
  https://help.kick.com/en/articles/10162074-moderation-features-guide,
  https://help.kick.com/en/articles/15065745-emergency-moderation-tools-on-kick
- Terms of Service: https://kick.com/terms-of-service
- [unofficial] Website chat socket, as described by a third-party library: https://docs.rs/kick-api/0.1.9/src/kick_api/live_chat.rs.html

### Rumble
- Official, Live Stream API: https://rumble.support/en/help/how-to-use-rumble-s-live-stream-api
- Official, moderation help: https://rumble.support/help/how-to-moderate,
  https://rumble.support/help/studio-admin-moderator-permissions, https://rumble.support/help/sub-only-chat
- Official, Terms: https://rumble.com/s/terms
- Official, partner API page (could not be read; HTTP 421): https://player.rumble.com/developers/Rumble-Platform-API.html
- [unofficial] cocorum library (internal chat endpoints; its README notes the terms conflict):
  https://github.com/thelabcat/cocorum, https://pypi.org/project/cocorum/
- [unofficial] Go library for the Live Stream API: https://beta.pkg.go.dev/github.com/tylertravisty/rumble-livestream-lib-go
- [community] Moderation walkthrough (slow mode): https://rumblerblog.blogspot.com/2024/07/how-to-moderate-your-rumble-live-stream.html

### Cuebot
- `docs/MOD_PORTAL_PLAN.md`, `portal/RELAY.md`, `website/docs/guides/mods/mod-portal.md`, and the source files named
  in sections 2 and 10, at commit `4bc6a68`.
