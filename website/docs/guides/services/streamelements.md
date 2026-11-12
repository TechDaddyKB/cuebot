# StreamElements

Cuebot connects to StreamElements for tips, merch and loyalty store redemptions, and for much more: tip moderation,
going live, session data (goals, latest follower...), the StreamElements chatbot, contests, giveaways, song requests
and your StreamElements overlays. It does all of this on top of whatever else you use; your StreamElements overlays
and settings keep working.

## Connect

1. On streamelements.com click your **avatar** (top right), choose your **channel name**, pick the channel whose
   events you want, and copy the **JWT token**.
2. In Cuebot go to **Apps > Donations**, pick **StreamElements**, paste the token and click **Connect**.
3. The card shows the channel and its platform, like *TechDaddy (Twitch)*.

!!! tip "One token per platform account"
    A StreamElements token belongs to one platform account. If Twitch, YouTube and Kick are linked to your
    StreamElements account, copy the token while switched to the account whose events you want. If the card shows the
    wrong platform, copy the token again from the right account.

**No JWT?** You can paste the **Overlay Token** instead (same page). It only receives events: points, store, bot and
the other steps need the JWT. Which events the Overlay Token may read is not documented by StreamElements.

### Nothing arrives?

- YouTube activities only arrive while your stream is **public**.
- StreamElements tips (PayPal, or SE.Pay) only work **while you are live**. SE.Pay Legacy ends on 31 December 2026;
  its replacement is in closed beta.
- Click **Replay last tip**: StreamElements sends your newest real tip again, so the whole way from StreamElements
  to your actions is tested (not only Cuebot's local **Send test event**).
- The legacy realtime socket (the *Use the legacy realtime socket* box) is deprecated: StreamElements no longer
  documents it. Leave it off. If it fails three times in a row, Cuebot switches back to the normal connection.

## Tip moderation

With **Wait for tip approval** ticked (the default), a tip runs your **Tip** actions only once StreamElements' tip
moderation allows it:

| What happens | Trigger |
| --- | --- |
| The tip is allowed (or you do not moderate tips) | **Tip** (`%tipApproved%` = allowed) |
| The tip waits for a moderator | **Tip waiting for approval**, then **Tip** once allowed |
| A moderator rejects the tip | **Tip rejected**; **Tip** never runs |

If StreamElements says nothing about a tip's moderation within a few seconds, the tip runs anyway with
`%tipApproved%` = unknown. Untick the box to run tips at once (a tip already known to be rejected still does not run).
Cuebot never copies the donor's e-mail or location into variables.

## Missed tips after a reconnect

When the connection drops and comes back without resuming (with the JWT token), Cuebot asks StreamElements for the
tips, merch and store redemptions made in between and runs them. They carry `%isBackfill%` = True, and the **Tip**,
**Merch** and **Store redemption** triggers have an **Include … fetched after a reconnect** box (ticked) if you would
rather skip them. One already heard live never runs twice. Event lists, goals and totals count them; the alert box
does not pop them up (see [Missed alerts](../overlays/alerts.md#missed-alerts-catch-up)).

## Variables StreamElements keeps for you

- `~seIsLive~` is true while StreamElements sees your stream live (any platform). **Stream went live** and
  **Stream went offline** run when it changes.
- Session data becomes globals named `se.<key>.<field>`, with the key's dashes removed: `~se.tipSession.amount~`,
  `~se.followerLatest.name~`, `~se.tipGoal.amount~`. They are filled when Cuebot connects and kept up to date;
  **Session value changed** and **Session reset** run when they change. The keys are those of StreamElements'
  session data reference (`tip-session`, `follower-latest`, `cheer-month`, `subscriber-goal`...).

## Steps you can use in actions

All of them are under **StreamElements** in the step list and need the JWT.

- **Chatbot:** send a chat message as the StreamElements bot, mute or unmute it, check its status, read a counter
  (like `deaths`), turn a timer or command on or off.
- **Overlays:** pause or play the alert queue, mute or unmute, reload every StreamElements overlay.
- **Session:** set a session value (for example the tip goal's amount), reset the session.
- **Points:** get, add, rank, reset one viewer, give points to many viewers at once, reset everyone's points, the
  current, all-time and watch-time leaderboards. Loyalty is not available on YouTube channels.
- **Tips:** latest tips (rejected and unapproved ones are left out), tips waiting for approval, tip and cheer
  leaderboards for all time, this month, this week or this stream, and **Add a tip to StreamElements** for a
  donation that came in elsewhere (so goals and leaderboards count it; no money moves).
- **Loyalty store:** get an item, turn it on or off, change its cost or stock, complete or refund a redemption
  (`%redemptionId%` from the **Store redemption** trigger), list pending redemptions.
- **Contests and giveaways:** create a contest, start, close, pick the winner or refund it; start, close, draw,
  complete, reopen or refund a giveaway.
- **Song requests:** play or pause, what is playing or next, the queue, add or remove a song.

## Other triggers

Overlay control used (a mod paused or skipped alerts), overlay changed (Cuebot also offers to import it again on the
**Overlays** page), overlay broadcast (like `!kappagen`), chatbot counter changed, contest bet / state / winner,
giveaway entry / winner / state, song request events, viewer queue events, emote combo broken, pyramid completed,
and chatbot modded or banned.

Tick **Also receive chat through StreamElements** for the four chat triggers (message, deleted, timeout or ban,
chat cleared). Your platforms' own chat already comes in through Cuebot's Twitch, YouTube and Kick connections, so
this is mostly useful for **X** chat.

## Relayed activities

With **Also publish relayed platform activities** ticked, StreamElements' copies of follows, subs, cheers, raids and
so on run the "Relayed ..." triggers. Every other activity type runs **Other relayed activity** with
`%activityType%`, and every detail StreamElements sends is a variable `%data.<name>%`:

| Activity type | Typical details |
| --- | --- |
| `cheerPurchase`, `channelPointsRedemption` | `%data.amount%`, `%data.message%` |
| `hypetrainStart`, `hypetrainProgress`, `hypetrainEnd` | `%data.amount%`, `%data.level%` (when sent) |
| `giveaway`, `purchase`, `event` | `%data.amount%`, `%data.message%` |
| `elixir` (Kick), `stars` (Facebook), `fan`, `supporter`, `share`, `videolike` | `%data.amount%` |
| `sponsorship`, `sponsorshipPassive` | `%data.amount%`, `%data.tier%` |

`host` still exists for old Twitch hosts, which Twitch ended; treat it as legacy.

!!! warning "Partly checked"
    The connection was checked against a real account earlier. The new topics, the tip
    moderation flow and the write steps (store, contests, giveaways, song requests, session values, timers and
    commands) follow StreamElements' public documentation and are tested against recorded examples, but have not
    been run against a real account yet. Some request bodies are not documented by StreamElements (setting a session
    value, completing a redemption, creating a contest); please tell us if one fails.
