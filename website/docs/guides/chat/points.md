# Points

**Points** are your own channel money. Viewers earn them by watching and chatting, and spend them in your store, on
giveaways, on bets and on [chat games](games-and-commands.md#games). They are Cuebot's own points, not Twitch channel
points, and they work on **every platform** at once: Twitch, YouTube, Kick and Rumble.

![Chat > Points](../../assets/screens/page-points.png)

Go to **Chat > Points**.

## How points work

- **Every viewer has a balance on each platform.** Someone who watches you on Twitch and on Kick has two accounts, and
  each one collects its own points.
- **Viewers can link their accounts** so they have one wallet. See [Linked accounts](#linked-accounts) below.
- **Points are saved** in a user variable (`points` at first), one per account. The points sub-actions (**Get points**,
  **Spend points**…) work with a viewer's whole linked wallet; the variable sub-actions see one account's share.
- **Cuebot answers `!points` and the leaderboard commands by itself**, with no setup. See
  [Games and commands](games-and-commands.md).

## How viewers earn points

Under **Earning** (these are the starting values; change any of them):

| Setting | Starts at | Means |
| --- | --- | --- |
| **Earning on** | on | Points are handed out at all. |
| **Only while live** | on | Only while you stream. Cuebot checks each platform when it connects, so this works even if you start Cuebot after you go live. |
| **Watching** | 1 every 1 min | Viewers who are here get 1 point a minute. |
| **Chat message** | 1, at most every 60 s | Chatting gives a point, but not more than once a minute. |
| **Follow bonus** | 10 | Extra points for following (a YouTube subscribe counts as a follow). |
| **Subscription bonus** | 100 | Extra points for subscribing, resubscribing or becoming a member. |
| **Per gifted sub** | 50 | For each sub or membership someone gifts. |
| **Raid bonus** | 0 | For raiding you (a Kick host counts as a raid). |
| **Maximum balance (0 = none)** | 0 | The most points one viewer can have. 0 means no limit. |

![The Earning card with the points settings](../../assets/screens/card-earning.png)

!!! note "Who counts as watching"
    Platforms do not tell Cuebot who is quietly watching. A viewer counts as **here** when they chatted in the last
    10 minutes (change it under **Users & Groups**). So lurkers do not earn for watching. Chatting once in a while
    keeps a viewer earning.

**Only while live** works per platform: if you are live on Twitch but not on Kick, only Twitch viewers earn. The
**Earning** card shows which platforms Cuebot thinks are live right now.

**Multipliers** give some viewers more: for example set **VIPs** to `2` and VIPs earn double. You can also set a
multiplier per group, like `Regulars=1.5`. Twitch subscriber tiers 2 and 3 can have their own multiplier.

![The Multipliers card](../../assets/screens/card-multipliers.png)

**Never earn automatically (bots)**: list your bots here so they do not collect points.

## Linked accounts

A viewer who watches on more than one platform can **link** their accounts. Linked accounts share **one wallet**:

- `!points` shows the total, and how much is on each platform: `alice has 1,250 points (rank #3) · Twitch 1,000 · Kick 250`.
- They can spend the total from any platform. Spending takes points from the platform they are on first, then from the
  others.
- What they earn stays on the platform where they earned it, so each platform's leaderboard stays fair.
- The combined leaderboard (`!top`) and [top spenders](top-spenders.md) (when you tick **Count linked accounts as one**)
  list them once.

Viewers link from chat, and both accounts have to agree, so nobody can link someone else's account:

1. On Twitch, `alice` types `!linkkick alice_k` (her Kick name).
2. Cuebot answers: *alice, to finish: as alice_k in Kick chat, type !linktwitch alice within 10 minutes.*
3. On Kick, `alice_k` types `!linktwitch alice`. Both chats see *Linked! alice (Twitch) + alice_k (Kick) now share
   1,250 points.*

The commands are `!linktwitch`, `!linkkick`, `!linkyt` and `!linkrumble`. `!unlink kick` takes the Kick account out
again, and each account keeps what is in it. You can see, add and remove links yourself on **Chat > Games & Commands >
Linked accounts**.

## The name of your money

Under the currency, set **Name** (like `cookies`), **One** (like `cookie`) and a **Symbol**. **Add currency** makes a
second kind of money if you want one. The chat commands and games use the currency you pick on **Games & Commands**.

## Giving and taking by hand

Pick the platform, type the **User name** and an amount, then click **Give** or **Take**.

The leaderboard below lists everyone, with **Refresh** and **Reset balances** (click twice to be sure). With **All**,
linked accounts are one row and the **Platform** column lists their platforms (`Twitch + Kick`). Pick one platform to
see what each account earned there.

## Commands in chat

Cuebot answers these by itself in every chat. Rename them, change who may use them or what they say, or turn them off
on **Chat > Games & Commands**:

| Viewers type | Cuebot answers |
| --- | --- |
| `!points` or `!points bob` | The balance and rank, with each linked platform's share |
| `!top` (or `!leaderboard`) | The top 5, linked accounts counted together |
| `!toptwitch`, `!topkick`, `!topyt`, `!toprumble` | The top 5 on one platform |
| `!give bob 50` | Moves 50 points to bob |
| `!gamble 100`, `!slots 50`, `!duel bob 100` | The [games](games-and-commands.md#games) |

The full list, with linking and the support commands, is on [Games and commands](games-and-commands.md).

If you would rather build `!points` yourself, make a command with the same word on **Automation > Commands**: yours runs
and Cuebot's stays quiet. The **Get points**, **Add points**, **Remove points**, **Transfer points** and **Get points
leaderboard** sub-actions work with linked wallets too. The trigger **Points threshold reached** runs when someone
reaches an amount you set under **Thresholds**.

## Spending points

- [Giveaways](giveaways.md): tickets can cost points.
- [The store](store.md): viewers buy things you offer.
- [Polls and predictions](polls-and-predictions.md): viewers bet points on what happens next.
- [Games](games-and-commands.md#games): `!gamble`, `!slots` and `!duel`.

!!! note "Not the same as goal ladder points"
    A [goal ladder](../services/goals.md) counts its own points, from subs, bits, tips and other events. Your viewers'
    Cuebot points do not go into it.
