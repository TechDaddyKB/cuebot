# Chat for mods

The **Chat** page of the [mod portal](mod-portal.md) shows your Twitch, YouTube, Kick and Rumble chat in one list.
Your mods can delete messages, time people out, ban and unban from there, from their phone or browser. Cuebot does it
for them with your accounts, and writes down who did what.

## What your mods see

- **One list for every platform.** Each line shows the time, the platform and the person's name. **All platforms**
  at the top switches to one platform.
- **Click a line** to get the tools for it: **Delete message**, **Timeout** (pick how long), **Ban**, **Unban** and
  **Warn**, and a box for a reason.
- **Click a name** for the person's **user card**: their platform, their user id, their recent messages here, what
  mods did to them before, and the same tools.
- **Mod log** (next to **Chat**) lists every moderation action, newest first: by you, by mods in the portal, by
  Cuebot's own rules, and by mods on the platform's website.
- **Pop out** opens the chat on its own in a tall window, without the menu. Put it on a second screen.

New messages, deletions and log rows appear by themselves. If a mod's browser cannot get live updates, the page
refreshes every few seconds instead.

!!! tip "Look at the user id, not the name"
    Anyone can copy a display name, with lookalike letters too. The user card shows the platform and the account's
    user id, which nobody can copy. Check it before a ban.

## Each tool says what it will do

The platforms do not all allow the same things. Before a mod clicks, each button shows (hover over it) what will
happen on that platform:

| Button looks | What it means | Example |
|---|---|---|
| Normal | It works. | Ban on Twitch |
| Normal, with a note | It does the closest thing, and the note says how it differs. | Kick counts timeouts in whole minutes: 9 min 30 s becomes 10 min |
| **(website)** | There is no way to do it from Cuebot. The button opens the platform's own page. | Clear chat on Kick |
| Greyed out | Not possible on this platform, or not part of the mod's role. The note says which. | Warn on Rumble |

The timeout list shows what a platform really applies: on Kick, **14 days (applies 7 days)**, because Kick's longest
timeout is 7 days. On YouTube each action uses some of your daily YouTube allowance (a ban costs 200 of 10,000 units);
the tool notes say how much.

**Chat modes and clear chat** (slow mode, followers-only, subscribers-only, emote-only, and Shield Mode on Twitch)
are in a fold-out bar above the chat, one row per platform, for roles that have them.

Mods cannot send chat messages as you or your bot from the portal.

## Who may do what

Moderation is not a "change": it never waits for your approval. It happens at once and goes into the mod log. What a
mod may do comes from their role (**Chat > Mod portal > Roles**):

| Right | Standard Mod | Lead Mod | Tech Lead |
|---|---|---|---|
| See the combined chat | Yes | Yes | Yes |
| Delete messages, time out, warn | Yes | Yes | Yes |
| Ban and unban | Yes | Yes | Yes |
| Chat modes, clear chat, Shield Mode | No | Yes | Yes |
| See the mod log (every mod's actions) | Yes | Yes | Yes |

The roles also list rights for blocked terms, notes about viewers and linked accounts. Those features are not built
yet; the rights are there so you can decide in advance.

**Bans by your mods are made with your account.** The platform's own log shows you, not the mod. On Twitch and Kick
the reason starts with the mod's name, like `[mod: Sam] spam`, and Cuebot's mod log always names the mod.

!!! note "Portals set up before the Chat page"
    Standard Mod, Lead Mod and Tech Lead get the rights in the table above when Cuebot starts. Roles you made yourself
    get **See the combined chat** and **See the mod log** only, or every chat right if the role could already end the
    stream and ban (**End stream, ban, title and category**). Tick more under **Roles** if you want.

## Safety limits

Cuebot checks every click from a mod before it reaches the platform:

- **Limits per mod.** A mod can make 10 bans and 30 timeouts in 10 minutes. After that the portal says
  *"Sam has made 10 bans in the last 10 min. Wait a little, or ask the streamer."* Other mods are not held up.
- **Protected people.** Mods cannot act against you, your bot accounts, platform moderators and VIPs, or anyone who
  is a member of your mod portal. Only you can, from Cuebot.
- **Paused and removed mods** can do nothing, at once. With **Must stay a Twitch mod** ticked, a mod you remove on
  Twitch is paused here too.

A refused click is written to the mod log as **Not sent**, with the reason.

## Tell your mods (and viewers)

**The mod log records what each mod does**: who deleted, timed out, warned or banned whom, when, the reason, and the
text of deleted messages. Tell your mods before you invite them. The invite page says so too.

The log stays on your computer, in Cuebot's data folder (it is part of your backups). Nothing about your chat is kept
on the portal relay. Old details are blanked after a while: deleted message text after 60 days, timeouts after 180
days, and a ban 24 months after it was lifted. Change these in `moderation.json` if you want them shorter.

It is a good idea to say in your channel rules that you moderate every platform you stream on together, and how
viewers can ask about a ban.
