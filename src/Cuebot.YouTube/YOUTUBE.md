# YouTube module

Cuebot talks to YouTube only through the official **YouTube Data API v3**. Release builds sign in with Cuebot's own
Google project (see `docs/BUILT_IN_APPS.md`), whose quota all its users share; a user's **own** Google Cloud project,
entered under *Advanced*, gives them their own quota (10,000 units per day by default, reset at midnight Pacific
time). Built from Google's public API reference only.

Each account records which client signed it in (`SignedInWith`: `builtin`, `builtin-device`, or empty for the
user's own), because Google refreshes a token only through the client that issued it.

## Setting up your own Google Cloud project (optional)

1. Open <https://console.cloud.google.com>, create a project (e.g. "Cuebot").
2. *APIs & Services → Library*: enable **YouTube Data API v3**.
3. *APIs & Services → OAuth consent screen* (Google Auth Platform → Branding/Audience): user type **External**, app
   name "Cuebot", your e-mail as support and developer contact. Under *Audience* either add your Google account(s)
   (broadcaster and bot) as **test users**, or **publish** the app ("In production"). While the app is in *Testing*,
   Google revokes refresh tokens after **7 days**; publishing avoids that. Unverified apps work for your own accounts
   (Google shows an "unverified app" warning you can continue past).
4. *Credentials → Create credentials → OAuth client ID*:
   - **TVs and Limited Input devices** (recommended): Cuebot shows a code; you enter it at google.com/device on any
     device. Scope requested: `https://www.googleapis.com/auth/youtube`.
   - or **Desktop app**: Cuebot opens the browser and receives the result on `http://127.0.0.1:<random port>/`
     (PKCE). Scopes: `youtube` and `youtube.force-ssl`.
5. Copy the client ID and client secret into Cuebot's YouTube page (*Advanced*) and sign in the **broadcaster** (pick the
   channel/brand account you stream on). Optionally sign in a second account as the **bot** for chat messages.

Tokens and the client secret are stored in `youtube.json` in the data folder and are never logged.

## What is read, and what it costs

| Call | Units | When |
| --- | --- | --- |
| `liveBroadcasts.list` (active) | 5 | every *broadcast check* (default 120 s), always while connected |
| `liveBroadcasts.list` (upcoming) | 5 | same interval, only with "Watch scheduled broadcasts" |
| `liveChatMessages.streamList` (gRPC) | 5 | per chat stream connection while live (*Stream live chat*, on by default); reconnects resume from the last page token |
| `liveChatMessages.list` | 5 | per chat poll while live, only when chat is polled: YouTube's `pollingIntervalMillis`, but at least *minimum chat seconds* (default 8 s) |
| `videos.list` (statistics, title, end) | 1 | every *statistics* interval (default 60 s) while live |
| `subscriptions.list` (myRecentSubscribers) | 1 | every *subscriber* interval (default 300 s), optional |
| `channels.list` | 1 | on connect/sign-in, and resolving an `@handle` |
| `liveChatMessages.insert` / `.delete`, `liveChatBans.insert` / `.delete`, `liveChatModerators.insert` / `.delete`, poll create/end | 50 each | on demand (sending, moderation, polls) |
| `liveChatModerators.list` | 50 | removing a moderator |
| `videos.update` (+ one `videos.list`) | 50 + 1 | set title / description / category / tags / privacy |

With the defaults and a streamed chat that is roughly **280 units per hour live** and **160 per hour idle**; the page's
estimate assumes the chat stream reconnects every 5 minutes, since YouTube does not document how long it keeps a
stream open. With chat polled every 8 s it is roughly **2,500 units per hour live** (chat dominates), i.e. about four
hours of streaming per default daily quota; raise *minimum chat seconds* to stream longer. The page shows the
estimate for the current settings and the units Cuebot has spent today (Cuebot's own count; Google's console is
authoritative).

When YouTube answers `403 quotaExceeded`, Cuebot publishes `youtube.quotaExceeded`, shows the error in the status,
and pauses **all** polling until the next Pacific midnight instead of retrying. Other errors back off
exponentially (5 s up to 5 min). `rateLimitExceeded` (polling too fast) is treated the same way.

## Behaviour

- **Broadcasts**: every active broadcast of the signed-in channel is monitored and its chat read. Broadcasts that are
  already live when Cuebot connects fire *Monitoring started* only; going live later fires *Started* too. A broadcast
  ends when it leaves the active list, its video reports an end time, or its chat ends. Actions without a broadcast id
  use the **latest** monitored broadcast.
- **Chat**: by default Cuebot holds `liveChatMessages.streamList` open over gRPC (`youtube.googleapis.com:443`), so
  messages arrive as they are posted. When YouTube closes the stream Cuebot reconnects from the last `nextPageToken`
  (after a pause if it closed within 30 s). If the stream cannot be opened three times in a row before any page
  arrived, that chat is polled with `liveChatMessages.list` instead. Turning *Stream live chat* off always polls.
  History is not replayed: the first page, and streamed items published before the stream opened, only seed the
  de-duplication set. Items are de-duplicated by id (polls by id + state, since YouTube re-sends them as votes change).
  The stream's protobuf messages are converted to the REST JSON shape (enum names such as `TEXT_MESSAGE_EVENT`
  become `textMessageEvent`), so both paths publish the same events.
- **Roles**: chat owner → broadcaster, moderator → moderator, member ("sponsor") → `isSubscriber`/`isMember`,
  verified → `isVerified`. The bot account's own messages carry `isBotAccount`.
- **Users** in sub-actions/functions may be a channel id (`UC...`), an `@handle` (one `channels.list` call) or the
  display name of someone who chatted recently.
- Super Chats and Super Stickers appear in the combined chat with their amount and in the feed as tips; memberships,
  milestones and received gift memberships as subscriptions; membership gifts as gift subscriptions; new channel
  subscribers as follows. Deletions, bans and timeouts are also published as `chat.messageDeleted`,
  `chat.userBanned` and `chat.userTimedOut` (speech skips those messages).

## Limitations

- The stream's published protobuf definition (`Api/Grpc/stream_list.proto`, from Google's guide) has no
  `messageDeletedEvent` type, so a streamed chat may not report *Message Deleted*; polling does.
- gRPC errors cannot tell a disabled chat from an ended one; both stop that chat's reader as "unavailable", and the
  broadcast check notices when the broadcast itself ends.
- YouTube returns ban ids only to whoever created the ban, so **Unban** works only for bans/timeouts Cuebot placed in
  the current session; lift others in YouTube Studio.
- New Subscriber only sees subscribers whose subscriptions are public.
- Jewels gifts (`giftEvent`) have little public documentation; the amount is read best-effort.
- Messages longer than YouTube's 200-character limit are split into several messages (50 units each).
