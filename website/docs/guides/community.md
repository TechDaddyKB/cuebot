# Points, store and giveaways

These features give your viewers something to do: collect points, spend them, win giveaways, vote in polls and save
funny quotes. They all live under **Chat** in the left rail, and they work on **every platform** at once.

## Points

**Points** are your own channel money. Viewers earn them by watching and chatting, and spend them in your store, on
giveaways and on bets. They are Cuebot's own points, not Twitch channel points.

Go to **Chat > Points**.

### How viewers earn points

Under **Earning** (these are the starting values; change any of them):

| Setting | Starts at | Means |
| --- | --- | --- |
| **Earning on** | on | Points are handed out at all. |
| **Only while live** | on | Only while you stream. |
| **Watching** | 1 every 1 min | Viewers who are watching get 1 point a minute. |
| **Chat message** | 1, at most every 60 s | Chatting gives a point, but not more than once a minute. |
| **Follow bonus** | 10 | Extra points for following. |
| **Subscription bonus** | 100 | Extra points for subscribing. |
| **Per gifted sub** | 50 | For each sub someone gifts. |
| **Raid bonus** | 0 | For raiding you. |
| **Maximum balance (0 = none)** | 0 | The most points one viewer can have. 0 means no limit. |

**Multipliers** give some viewers more: for example set **VIPs** to `2` and VIPs earn double. You can also set a
multiplier per group, like `Regulars=1.5`.

**Never earn automatically (bots)**: list your bots here so they do not collect points.

### The name of your money

Under the currency, set **Name** (like `cookies`), **One** (like `cookie`) and a **Symbol**. **Add currency** makes a
second kind of money if you want one.

### Giving and taking by hand

Pick the platform, type the **User name** and an amount, then click **Give** or **Take**.

The leaderboard below lists everyone, with **Refresh** and **Reset balances** (click twice to be sure).

### A `!points` command

Cuebot does not add chat commands for you. You make them, so you choose the words. For `!points`:

1. Make a `!points` command (see [Chat commands](commands.md)) and an action with a **Command triggered** trigger.
2. Add the **Get points** sub-action. It looks up the name typed after the command (`!points bob`), or the person who
   typed it when there is no name. Look at its **Variables this adds for the next steps** to see the name of the
   balance variable.
3. Add **Send chat message (any platform)** that uses that variable, like `%user%, you have ... points!`

Other points sub-actions include **Add points**, **Remove points**, **Transfer points** (for a `!give bob 50`
command) and **Get points leaderboard**. The trigger **Points threshold reached** runs when someone reaches an amount
you set under **Thresholds**.

## Giveaways & Store

Go to **Chat > Giveaways & Store**. Both use your Cuebot points.

### A giveaway

1. Type the **Prize**, like `A Steam game`.
2. **Join command** is `!join`. Viewers type it to enter. `!join 3` buys 3 tickets.
3. Set **Ticket cost** (0 for free), **Tickets per viewer**, how many **Winners**, and **Seconds** (how long entries
   stay open).
4. Optional: **Subscribers only**, **Subscriber luck** (better odds for subs), **Announce winners in chat**.
5. Click **Open giveaway**.
6. When it is time, click **Close entries**, then **Draw winners**. Winner not here? **Draw one more**.

**Cancel and refund** stops it and gives everyone their points back. Click it twice: the first click only asks you to
click again within 5 seconds. Show it on stream with the **Giveaway** widget on
your [overlay](overlays.md).

### The store

The store lets viewers spend points on things you offer, like "Pick my next game" or "I do 10 push-ups".

1. Type a **New item name** and its cost, then click **+ Item**.
2. Set **STOCK** (how many there are; `-1` means no limit), and cooldowns: **PER VIEWER** and **EVERYONE**, in seconds.
3. Tick **Needs approval** if you want to say yes or no first.

Viewers type `!redeem` and the item name, like `!redeem push-ups`.

Items that need approval wait under **Waiting for approval**. Click **Approve**, or **Reject (refund)** to give the points
back. **Latest purchases** shows what was bought.

To make something happen when an item is bought, add the **Store item redeemed** trigger to an action.

## Polls and predictions

### On every platform

Go to **Overlays > Overlays & Widgets** and open **Chat poll and prediction (every platform)**.

- **Poll**: type a question and choices, then **Start poll**. Viewers vote with `!vote 1`, `!vote 2` and so on.
  **End poll** finishes it.
- **Prediction (Cuebot points)**: viewers bet points with `!bet 1 500` (or `!bet 1 all`). Click **Lock bets** when
  the game starts, then pick the **Winner** and **Pay out**. Winners share the whole pot, by how much they bet.
  **Cancel** gives everyone their points back.

Show it on stream with the **Poll and prediction** widget.

### Twitch's own polls

**Platforms > Twitch**, tab **Polls & predictions**, uses Twitch's built-in polls and predictions (with Twitch channel
points). Fill in **Question** and the choices (one per line), then **Start poll** or **Start prediction**.

## Quotes

Something funny said on stream? Save it as a quote. Go to **Chat > Quotes**.

- Type the quote in **New quote text**, optionally the **User** who said it and the game, then click **+ Quote**.
- Search with the search box, or by number like `#12`.
- **Import…** and **Export JSON…** / **Export CSV…** move quotes in and out.

For chat, make your own `!quote` and `!addquote` commands with the **Get quote** and **Add quote** sub-actions.

## Credits

**Chat > Credits** collects everyone who followed, subbed, tipped, raided or chatted during your stream, for an
end-of-stream **Credits roll** widget. **Start fresh when a stream goes live** clears the list each stream. **Show test
data** fills it with pretend names, so you can see how it looks.

## Top spenders

**Chat > Top Spenders** shows who spent the most money on your stream. It adds up everything a viewer paid for, on
every platform you connected:

- Twitch: bits (cheers and power-ups), subs, resubs and gift subs
- YouTube: Super Chats, Super Stickers, Jewels, memberships and gifted memberships
- Kick: Kicks, subs and gift subs
- Rumble: rants, subscriptions and gift subs
- Tip services: Streamlabs, StreamElements and TipeeeStream tips, and merch
- Webhooks: Ko-fi, Patreon, Fourthwall, Shopify and Throne

Pick the time at the top: **This stream**, **Today**, **This week**, **This month**, **This year** or **All time**. Pick
one platform, or **All platforms** to see everyone together. The same name on two platforms counts as two people.
Under each name you see what they spent it on, like `Cheer $10.00, Subscription $4.99`.

### Your currency

Type your currency in the **Currency** box, like `USD`, `EUR`, `GBP` or `CAD`. Everything is changed into it.

1. When your stream starts, Cuebot fetches exchange rates from free services. It tries
   [Frankfurter](https://frankfurter.dev) first, which uses the European Central Bank's rates. For currencies Frankfurter
   does not have, it uses [currency-api](https://github.com/fawazahmed0/exchange-api). Neither needs an account.
2. Those rates stay the same for the whole stream. Every purchase is saved in your currency with the rate of that day,
   and the lists are sorted by that saved value. A €10 Super Chat from last month keeps last month's value.
3. If no service answers, Cuebot uses the rates of your last stream. If there are none, purchases in other currencies
   wait, and are counted as soon as the rates arrive.

If you change your currency later, older streams are shown in the new currency using their own locked rates.

### What bits, Kicks and subs are worth

Bits, Kicks, Jewels and subs are not money, so Cuebot gives them a price in US dollars. Change the prices under **What
things are worth** if your prices are different. For example, a Twitch tier 1 sub is $4.99 and a bit is $0.01. A gift
of 5 subs counts as 5 subs. A new price only counts for new purchases.

### What is left out

- **Charity donations** go to the charity, not to you. Tick **Count charity donations** to count them anyway.
- **Test events**, like the test buttons on the **Donations** and **Webhooks** pages and sample events sent from
  Cuebot (emulate event), are never counted.
- Names in **Never list**, like `Anonymous`. Add your own test account here. Their purchases still count for
  [subathon timers and goals](subathon-and-goals.md) and still run the **Money spent** trigger. They are only left off
  the list.
- Bits, subs and Super Chats that tip services like Streamlabs pass on from Twitch or YouTube. Connect Twitch or YouTube
  in Cuebot to count those, so nothing is counted twice.

To take someone off the list after a refund, click their name and **Remove this person's purchases**. It removes what
they bought in the time you picked.

### When a stream starts and ends

A new stream starts when a platform goes live. If you go offline and come back within 15 minutes, it is still the same
stream. If no platform tells Cuebot you went live, the first purchase after you were offline starts the new stream.
You can also click **Start a new stream now**, or use the **Start a new stream for top spenders** sub-action.

### Show it on stream

Add the **Top spenders** widget to an overlay (see [Overlays and alerts](overlays.md)). In its settings, pick the
**Period**, a **Platform** (or leave it empty for all) and **How many people**.

### A `!topspender` command

1. Make a `!topspender` command and an action with a **Command triggered** trigger.
2. Add the **Get top spenders** sub-action. Pick **This stream** and leave **Platform** empty.
3. Add **Send chat message (any platform)** with `Top spender: %topSpender% (%topSpenderFormatted%)`.

The trigger **New top spender this stream** runs when someone takes first place. Use it for a "new top spender!"
alert. The trigger **Money spent** runs for every purchase, with the amount already in your currency.

## Users & Groups

**Chat > Users & Groups** lists the viewers Cuebot has seen. On the **Groups** tab you make groups, like `Regulars` or
`Friends`. Use groups to:

- let only that group use a command (see [Chat commands](commands.md#permissions));
- give that group a points multiplier.
