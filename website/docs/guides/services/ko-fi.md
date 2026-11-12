# Ko-fi

Ko-fi tells Cuebot about every donation, membership payment, commission and shop order. Each one becomes a trigger,
an item in the activity feed, a spoken event, an overlay alert and part of your totals, credits and goals.

Ko-fi reaches Cuebot through a relay. Set one up first: see [Webhooks](webhooks.md).

## Set it up

1. In Cuebot, open **Apps > Webhooks**. On the **Relay** tab, click **Copy** next to **Ko-fi**.
2. Open [ko-fi.com/manage/webhooks](https://ko-fi.com/manage/webhooks) (on Ko-fi: **Settings > API > Webhooks**).
   Paste the address into **Webhook URL** and save.
3. On the same Ko-fi page, open **Advanced** and copy the **Verification Token**.
4. Back in Cuebot, on the **Services** tab, paste it into the **Ko-fi** card and tick **Enabled**.
5. Click one of Ko-fi's **Send test** buttons. The test arrives in Cuebot as a test event (`%isTest%` is true), and
   the card shows the last delivery.

**Send test event** on the Ko-fi card makes a pretend event inside Cuebot only, so you can try actions without Ko-fi.
**privateDonation** pretends a supporter marked their support private.

!!! warning "Ko-fi sends payments only"
    Ko-fi sends a message when someone pays, and nothing else. It never says when a membership ends, and it has no
    way for apps to read your goal or past payments. If a message is lost, it is lost for good.

    With the **shared relay**, a payment made while Cuebot is closed is dropped. It is missing from your totals,
    goals, subathon timers and credits, and Ko-fi does not send it again. If that matters to you, use
    [your own relay](webhooks.md#or-your-own-relay): it keeps messages for 24 hours and hands them over when Cuebot
    starts.

## Private supports

On Ko-fi, a supporter can tick **make this private**. Cuebot respects that:

- `%user%` is **Anonymous** and `%message%` is empty, in every trigger. So a "Thank %user%: %message%" action never
  says a private name or message on stream.
- Overlay alerts, the activity feed, spoken events and credits show no name or message either.
- `%isPublic%` is false. Each Ko-fi trigger has **Supporter's choice**: **Any**, **Public only** or **Private only**.
- The real name and message are only in `%privateName%` and `%privateMessage%`, for your own use, like a private log.
  Never put those on stream.

If you really want private supporters shown like everyone else, tick **Show private supporters' names and messages**
under **Ko-fi extras**. The activity feed and credits still hide them.

Cuebot never makes variables from the supporter's email or a shop order's shipping address.

## Variables

Every Ko-fi trigger has `%user%`, `%amount%`, `%currency%`, `%formattedAmount%`, `%message%`, `%isPublic%`, `%url%`,
`%messageId%`, `%transactionId%`, `%timestamp%`, `%kofiType%`, `%isReplay%` and `%isTest%`, and the Ko-fi goal
(`%kofiGoalCurrent%`, `%kofiGoalTarget%`, `%kofiGoalPercent%`). Memberships add `%tier%`, `%isFirstPayment%` and
`%months%` (payments Cuebot has seen; Ko-fi sends no month count). Shop orders add `%itemCount%` and `%item0%`,
`%item0.quantity%`, `%item0.code%`, `%item0.image%`... The full list is on the
[Ko-fi triggers page](../../reference/triggers/ko-fi.md).

**Membership level:** the **Tier name** box takes one level or several with commas, like `Gold, Silver`. Capitals and
spaces do not matter.

## Late payments (your own relay)

Payments your own relay kept while Cuebot was closed arrive when it starts, all at once. They have `%isReplay%` true.
Tick **Skip late deliveries** on a trigger so you are not flooded with old thank-yous. Skipped or not, they still count
in the activity feed, totals, credits and the Ko-fi goal. A payment counts once, even when Cuebot crashed before it
could tell the relay it was handled: Cuebot remembers the payments it handled for two days.

## Shop product names

Ko-fi names a product only by its code (the end of its link) when it has no variations. So `%item0%` would be
"item 1a2b3c4d5e". To show the real name, list your products under **Ko-fi extras > Product names**, one per line:

```text
1a2b3c4d5e = Sticker pack | https://example.com/stickers.png
```

The picture link is optional. It ends up in `%item0.image%`.

## The Ko-fi goal

Ko-fi's own goal bar lives only in Ko-fi's alert page. Cuebot keeps its own Ko-fi goal, counted from the payments it
receives, so you can show it in your overlays and combine it with your actions.

1. Under **Ko-fi extras**, type a title, a **target**, and the **start amount** (what your Ko-fi goal shows already).
2. Pick what counts: donations, memberships, commissions and shop orders.
3. Type a **currency** like `USD` if you get paid in more than one. Cuebot does not convert, so only payments in that
   currency count. Leave it empty to count everything.

Test events never count. **Reset goal** starts again from the start amount. When the target is reached, the **Goal
Reached** trigger runs once.

To show it, add a **Goal** widget to an overlay and set **Counts** to **Ko-fi goal**. Its target and progress come from
the Webhooks page. The steps **Get the Ko-fi goal**, **Set the Ko-fi goal**, **Add to the Ko-fi goal** and **Reset the
Ko-fi goal** change it from an action, like a chat command. Scripts can use `kofi.goal`, `kofi.setGoal`,
`kofi.addToGoal` and `kofi.resetGoal`.

## Memberships that end

Ko-fi never says when someone stops their membership. Cuebot remembers when each member last paid. When that is longer
ago than **Membership probably lapsed after (days)** (35 by default), the **Membership Lapsed** trigger runs once for
them. It is an estimate: a payment can simply be a day late. The script function `kofi.members` lists who Cuebot has
seen.

Members are told apart by their Discord account when they linked one, otherwise by their Ko-fi name. Ko-fi gives no
other id, so a member who changes their name counts as new.

## Commissions

Every Ko-fi commission joins a list of open commissions. The steps **Get open commissions**, **Mark a commission
done**, **Add a commission** and **Clear done commissions** manage it, for example with a `!queue` chat command.
Overlays can read the open ones as `kofi-commissions` in the session data, and the script functions `kofi.commissions`
and `kofi.completeCommission` work too.

## Discord

When a supporter linked Discord on Ko-fi (for Discord roles), `%discordUserId%`, `%discordUsername%` and
`%discordMention%` are set. Put `%discordUserId%` in the **Member** box of **Send a direct message** to thank them, or
`%discordMention%` in a channel message to mention them. Let Ko-fi's own Discord bot hand out and take away roles: it
knows when a membership ends, and Cuebot does not.

## Ko-fi's own Twitch bot

Ko-fi has its own Twitch chat bot (`Ko-fiStreamBot`) that can post payment alerts, goal updates and answer `!kofi`.
If Cuebot thanks people too, turn off **Twitch chat alerts** in Ko-fi's settings, or each tip gets two messages.
Cuebot's speech already ignores that bot's messages.

## Ready-made actions

Download [kofi-examples.cuebot](kofi-examples.cuebot) and open it on **Settings > Import / Export**. It has seven
actions:

- thank donors in chat (late, replayed payments skipped),
- create a Twitch clip for tips of 10 or more,
- send new members a Discord DM when they linked Discord,
- the chat commands `!kofi`, `!kofigoal` and `!queue`,
- a chat message when the Ko-fi goal is reached.

Change the Ko-fi link, amounts and texts to your own.

## Any tip, any membership

To run one action for tips from Ko-fi, Streamlabs, StreamElements, Twitch and the rest, use the **Activity (any
platform)** trigger with **Kind** set to **Tip** (or **Subscription** for memberships). Ko-fi donations are tips and
Ko-fi memberships are subscriptions there. Private supports show as "Someone".

## Zapier

Ko-fi can also send to Zapier, and Zapier to a Cuebot **custom webhook**. That works, but it is slower, and the
message carries no Ko-fi token that Cuebot can check. Use the Ko-fi webhook above instead.

!!! warning "Partly checked"
    Cuebot was tested with Ko-fi's documented example messages. No real Ko-fi payment has come in yet. See
    [What's experimental](../help/experimental.md).
