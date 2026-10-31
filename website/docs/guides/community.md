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

**Cancel and refund** stops it and gives everyone their points back. Show it on stream with the **Giveaway** widget on
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

## Users & Groups

**Chat > Users & Groups** lists the viewers Cuebot has seen. On the **Groups** tab you make groups, like `Regulars` or
`Friends`. Use groups to:

- let only that group use a command (see [Chat commands](commands.md#permissions));
- give that group a points multiplier.
