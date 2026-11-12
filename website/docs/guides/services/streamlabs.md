# Streamlabs

Cuebot connects to Streamlabs for donations, charity donations, merch, Cloudbot loyalty store redemptions and Prime
gifts. If you also connect your own Streamlabs app, Cuebot can control your Streamlabs alert box (skip, pause, mute,
custom alerts, alert profiles), spin the wheel, empty the tip jar, roll the credits, drive media share, read and change
Cloudbot loyalty points, and fetch donations it missed while the connection was down.

Streamlabs Desktop (the streaming app) is a separate connection: see [Other stream apps](../apps/other-stream-apps.md#streamlabs-desktop).

## Connect (donations only)

1. On streamlabs.com open **Settings > API Settings > API Tokens** and copy the **Socket API Token**. Streamlabs
   sometimes moves this menu; if you cannot find it, connect your own app (below), which fills the token in for you.
2. In Cuebot go to **Apps > Donations**, pick **Streamlabs**, paste the token and click **Connect**.
3. Click **Send test event** to check that your actions react. Nothing is sent to Streamlabs.

That is all you need for the donation triggers. Tick **Also publish the Twitch/YouTube events Streamlabs relays** only
if you do not connect Twitch or YouTube to Cuebot directly, or follows and subs may run twice.

## Connect your own Streamlabs app (optional)

The Streamlabs steps (category **Streamlabs** when you add a step) and the missed-donation back-fill use Streamlabs'
REST API, which needs an OAuth app. Streamlabs does not let a desktop program ship its secret, so you register your own
app once:

1. On streamlabs.com open **Settings > API Settings > Register App**. Give it any name.
2. Set the **redirect URL** to the address shown in Cuebot (by default `http://localhost:8914/callback`).
3. Add your own Streamlabs account to the app's **whitelisted users**. Until Streamlabs approves an app, only up to 10
   whitelisted users can use it.
4. Copy the **Client ID** and **Client Secret** into the **Optional: connect your own Streamlabs app** section of the
   Streamlabs card, and click **Connect with Streamlabs**. Allow the app in your browser.

The card then shows *Connected as ...* with your linked Twitch and YouTube accounts. The socket token is filled in too.

!!! info "5 requests a minute until Streamlabs approves your app"
    An app Streamlabs has not approved (their *testing* tier) may make **5 requests a minute**. Cuebot waits instead
    of failing when you press buttons faster than that. If you submit the app and Streamlabs approves it, raise
    **Requests per minute** (up to 2400). Custom alerts and added donations are always held to 2 a minute, as
    Streamlabs asks.

!!! warning "Experimental"
    The REST features were built from Streamlabs' documentation and tested against a pretend Streamlabs. They have not
    been tried with a real Streamlabs app yet. See [What's experimental](../help/experimental.md).

## Steps

| Step | What it does |
| --- | --- |
| Control Streamlabs alerts | Skip the alert on screen, pause or resume the queue, mute or unmute alert sounds, show or hide the media share video |
| Get Streamlabs alert queue status | `slAlertsPaused` |
| Show a custom Streamlabs alert | Your own text, image, sound and duration in the alert box, styled like a donation, follow, subscription or host alert |
| Send Streamlabs test alert | Streamlabs' own test alert, shown on stream |
| Switch Streamlabs alert profile | By profile name, e.g. a calmer set while Just Chatting |
| Spin the Streamlabs wheel / Empty the Streamlabs tip jar / Roll the Streamlabs credits | One click each; credits only roll while you are live |
| Control Streamlabs media share | Play, pause, skip, volume, and requests, autoplay, auto-show or moderation on/off |
| Get latest Streamlabs donations / Get top Streamlabs donors | `slDonation0User`..., `slTopDonor0User`... |
| Add a donation to Streamlabs | Record cash or another offline donation so goals count it |
| Get / Add / Set Streamlabs points, Give everyone Streamlabs points, Reset everyone's Streamlabs points, Get Streamlabs points leaderboard | Cloudbot loyalty points; the **Points channel** on the card is filled from your linked Twitch account |

Script functions: `streamlabs.alertQueue`, `streamlabs.createAlert`, `streamlabs.getDonations`, `streamlabs.getPoints`,
`streamlabs.addPoints`, `streamlabs.widget` and `streamlabs.isSignedIn` (see the
[script function reference](../../reference/script-functions/streamlabs.md)).

## Missed donations

With your app connected and **Fetch donations missed while the connection was down** ticked, Cuebot asks Streamlabs for
the donations made while the socket was reconnecting (for example after a Wi-Fi blip) and runs them. They carry
`isBackfill = true`, and the **Donation** trigger has an **Include donations fetched after a reconnect** box if you
would rather skip them. Donations already heard live are never run twice. Event lists, goals and totals count them;
the alert box does not pop them up (see [Missed alerts](../overlays/alerts.md#missed-alerts-catch-up)).

## Good to know

- **Hosts** never arrive any more: Twitch removed hosting in 2022 and Mixer closed in 2020. The *Relayed host (legacy,
  never sent)* trigger stays only so older actions load.
- **Relayed bits**: `bitsId` is the cheer's id. Streamlabs' sample sends no user id, so `userId` is usually empty.
- **Charity donations, merch, loyalty store redemptions and Prime gifts** are not documented by Streamlabs; Cuebot reads
  them from a best guess at their shape. If one of their variables comes out empty, please report it.
- Relayed subscriptions carry `subType` (like `resub` or `subgift`), `isGift`, `isResub` and `emotes`.
