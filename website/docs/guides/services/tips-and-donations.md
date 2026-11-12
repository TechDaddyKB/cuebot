# Tips and donations

People can support you on many tip sites. Cuebot can hear about those tips and donations, and turn each one into a
trigger: the start of an action. Example: someone tips $5 on Streamlabs, and Cuebot thanks them in chat and shows an
alert on your overlay. You connect each site once, and then it works by itself.

Keys and tokens you type in are hidden on screen, stored locked (encrypted) on your computer, and never written to the
log.

![Apps > Donations](../../assets/screens/page-donations.png)

## Connect a service

Go to **Apps > Donations** and pick a service on the left. Each one has its own page with the full steps:

| Service | What you need | Page |
| --- | --- | --- |
| **Streamlabs** | The **Socket API Token** from Streamlabs (or connect your own Streamlabs app). | [Streamlabs](streamlabs.md) |
| **StreamElements** | The **JWT token** of your StreamElements channel. | [StreamElements](streamelements.md) |
| **TipeeeStream** | The **API key** from your TipeeeStream account. | [TipeeeStream](tipeeestream.md) |
| **DonorDrive** (Extra Life and other charities) | No key: the charity and your **participant or team id**. | [DonorDrive](donordrive.md) |
| **TreatStream** | Nothing to paste: **Sign in with TreatStream** in your browser. | [TreatStream](treatstream.md) |
| **Pally.gg** | An **API key** from Pally.gg. | [Pally.gg](pally.md) |

Ko-fi, Patreon, Throne, Fourthwall and Shopify work differently: they send webhooks, through a relay. See
[Webhooks](webhooks.md).

## The steps are the same for every service

1. Paste the key (or sign in, for TreatStream).
2. Click **Connect**. The dot next to the service on the left turns green, and the status says **Connected**.
3. Tick **Connect automatically on startup**, so it connects every time Cuebot starts.
4. Pick a kind of event in the list and click **Send test event**. It shows under **Last events**, and your actions
   run with `%isTest%` = True. Nothing is sent to the real service.

**Disconnect** stops listening. Your saved settings stay.

## What a tip does

Each tip, donation or treat then:

- starts its triggers: look in the [trigger reference](../../reference/triggers/index.md) under the service's name;
- shows an alert in your [alert box](../overlays/alerts.md) (a **Tip / donation** alert; DonorDrive shows a
  **Charity donation** alert);
- goes into the activity feed, the [credits](../chat/credits.md) and spoken events.

**One action for every site:** the **Activity (any platform)** trigger with **Kind** set to **Tip** runs for tips from
all of them, and from Twitch, Ko-fi and the rest. Its **Platform** box picks one site if you want.

Every money trigger has **Minimum amount**, **Maximum amount** and **Currencies**, so one action can handle small tips
and another the big ones. Cuebot does not convert between currencies.

**Also publish...** boxes (Streamlabs, StreamElements, TipeeeStream): these sites also pass on follows, subs and raids
from Twitch or YouTube. Leave the box off if Cuebot already hears Twitch or YouTube directly, or each event runs your
actions twice.

!!! warning "Partly checked"
    Streamlabs and StreamElements connected for real, and DonorDrive read a real Extra Life team. Connecting to
    TipeeeStream, TreatStream and Pally.gg is not confirmed yet, and no real tip or donation has come in through Cuebot.
    Use **Send test event** to check your actions, and please tell us if a real tip does not show up. See
    [What's experimental](../help/experimental.md).

## More than tips

- **Streamlabs** can do more (alert box control, the wheel, jar and credits, media share, loyalty points and fetching
  donations missed while the connection was down) once you connect your own Streamlabs app: see
  [Streamlabs](streamlabs.md).
- **StreamElements** also does tip moderation, going live, session data, its chatbot, store, contests and song
  requests. Tips only arrive while you are live (and on YouTube only for public streams): see
  [StreamElements](streamelements.md).
- **Pally.gg**'s **Replay Alert** button plays an old tip again: see [Pally.gg](pally.md#replay-alert).
- **DonorDrive** also tells you about incentives and the total raised: see [DonorDrive](donordrive.md).
