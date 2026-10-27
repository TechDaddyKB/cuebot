# YouTube module

Cuebot talks to YouTube only through the official **YouTube Data API v3** with the user's **own** Google Cloud
project, so every request counts against that project's quota (10,000 units per day by default, reset at
midnight Pacific time). Built from Google's public API reference only.

## Setting up Google Cloud (once)

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
5. Copy the client ID and client secret into Cuebot's YouTube page and sign in the **broadcaster** (pick the
   channel/brand account you stream on). Optionally sign in a second account as the **bot** for chat messages.

Tokens and the client secret are stored in `youtube.json` in the data folder and are never logged.

## What is polled, and what it costs

| Call | Units | When |
| --- | --- | --- |
| `liveBroadcasts.list` (active) | 5 | every *broadcast check* (default 120 s), always while connected |
| `liveBroadcasts.list` (upcoming) | 5 | same interval, only with "Watch scheduled broadcasts" |
| `liveChatMessages.list` | 5 | per chat poll while live: YouTube's `pollingIntervalMillis`, but at least *minimum chat seconds* (default 8 s) |
| `videos.list` (statistics, title, end) | 1 | every *statistics* interval (default 60 s) while live |
| `subscriptions.list` (myRecentSubscribers) | 1 | every *subscriber* interval (default 300 s), optional |
| `channels.list` | 1 | on connect/sign-in, and resolving an `@handle` |
| `liveChatMessages.insert` / `.delete`, `liveChatBans.insert` / `.delete`, `liveChatModerators.insert` / `.delete`, poll create/end | 50 each | on demand (sending, moderation, polls) |
| `liveChatModerators.list` | 50 | removing a moderator |
| `videos.update` (+ one `videos.list`) | 50 + 1 | set title / description / category / tags / privacy |

With the defaults that is roughly **2,500 units per hour live** (chat dominates) and **160 per hour idle**, i.e.
about four hours of streaming per default daily quota. Raise *minimum chat seconds* to stream longer, or request a
quota increase from Google. The page shows the estimate for the current settings and the units Cuebot has spent
today (Cuebot's own count; Google's console is authoritative).

When YouTube answers `403 quotaExceeded`, Cuebot publishes `youtube.quotaExceeded`, shows the error in the status,
and pauses **all** polling until the next Pacific midnight instead of retrying. Other errors back off
exponentially (5 s up to 5 min). `rateLimitExceeded` (polling too fast) is treated the same way.

## Behaviour

- **Broadcasts**: every active broadcast of the signed-in channel is monitored and its chat read. Broadcasts that are
  already live when Cuebot connects fire *Monitoring started* only; going live later fires *Started* too. A broadcast
  ends when it leaves the active list, its video reports an end time, or its chat ends. Actions without a broadcast id
  use the **latest** monitored broadcast.
- **Chat**: the first page after attaching only seeds the de-duplication set (no history replay). Pages follow
  `nextPageToken`; items are de-duplicated by id (polls by id + state, since YouTube re-sends them as votes change).
- **Roles**: chat owner → broadcaster, moderator → moderator, member ("sponsor") → `isSubscriber`/`isMember`,
  verified → `isVerified`. The bot account's own messages carry `isBotAccount`.
- **Users** in sub-actions/functions may be a channel id (`UC...`), an `@handle` (one `channels.list` call) or the
  display name of someone who chatted recently.
- Super Chats and Super Stickers appear in the combined chat with their amount and in the feed as tips; memberships,
  milestones and received gift memberships as subscriptions; membership gifts as gift subscriptions; new channel
  subscribers as follows. Deletions, bans and timeouts are also published as `chat.messageDeleted`,
  `chat.userBanned` and `chat.userTimedOut` (speech skips those messages).

## Limitations

- Polling, not push: chat latency is the poll interval. (The gRPC `liveChatMessages.streamList` endpoint would be
  cheaper but needs a gRPC stack; not used.)
- YouTube returns ban ids only to whoever created the ban, so **Unban** works only for bans/timeouts Cuebot placed in
  the current session; lift others in YouTube Studio.
- New Subscriber only sees subscribers whose subscriptions are public.
- Jewels gifts (`giftEvent`) have little public documentation; the amount is read best-effort.
- Messages longer than YouTube's 200-character limit are split into several messages (50 units each).
