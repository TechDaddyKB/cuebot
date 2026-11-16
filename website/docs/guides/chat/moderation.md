# Moderating your chat

The **Chat** page lets you moderate Twitch, YouTube, Kick and Rumble from one place, and keeps a **mod log** of what
happened. Every button tells you what it will do on that platform before you click it.

## The buttons

Click a message. The **Chatter** tab shows who wrote it and what you can do:

- **Delete message**, **Timeout 1 min**, **Timeout 10 min** and **Ban…** (click once, then again to confirm).
- **Unban** lifts a ban or a timeout. **Warn** warns the person.
- **Reason (optional)**: why you did it. It is saved in the mod log. Twitch and Kick also show it to their moderators.

Above the chat, buttons for the whole chat of that platform: **Clear chat**, **Slow mode**, **Followers-only**,
**Subscribers-only** and **Emote-only**. They show for the platform of the message you clicked, or for the platform
you picked in the chips above the chat. A chat mode button switches the mode on, then offers to switch it off again.

Not every platform can do everything. Hold the mouse over a button to see what it does there:

- **Works**: the platform does it.
- **Closest equivalent**: it does something similar, and says how it differs. For example, Kick counts timeouts in
  whole minutes, so a timeout of 30 seconds becomes 1 minute. When that happens, the line under the timeout buttons
  says so.
- **(website)**: the platform has no way for apps to do it. The button opens the platform's own page, where you do it
  yourself.
- Greyed out: the platform has no such feature, or you need to sign in again and allow it. The tooltip says which.

| Button | Twitch | YouTube | Kick | Rumble |
| --- | --- | --- | --- | --- |
| Delete message | Works | Works (costs 50 quota units) | Works | Website (or see below) |
| Timeout | Works, 1 second to 14 days | Works, while you are live | Whole minutes, 1 minute to 7 days | Website (or see below) |
| Ban | Works | Works, while you are live | Works | Website (or see below) |
| Unban | Works | Only bans made from Cuebot | Works | Website |
| Warn | Works | A chat message to the viewer | A chat message to the viewer | Not available |
| Clear chat | Works | Not available | Website (type `/clear`) | Not available |
| Chat modes | Works | Slow and members-only on YouTube Studio | Website | Slow and subscribers-only on the website |
| Held messages, Shield Mode, suspicious chatters, unban requests, blocked terms | Works (see below) | Not available | Not available | Not available |

**Warn** on YouTube and Kick posts a chat message like "@Bob Warning from the mods: no spoilers". These platforms have
no real warnings. You can switch this off in `moderation.json` (`"SoftWarnings": false`).

The chat shows the result of a click under the send box: "Timed out Bob for 10 min", or what went wrong.

## Messages others delete

When a mod deletes a message on the platform's website, Cuebot crosses it out too. When someone is banned or timed
out, their name card says so. This works on Twitch, YouTube and Kick, and on Rumble for deleted messages.

A message without a platform id (Rumble chat read from the key link, for example) cannot be deleted, so Cuebot does not
offer **Delete message** for it.

## Twitch: held messages, Shield Mode and more

Twitch has more moderation tools than the other platforms. On the Chat page you find them in two extra tabs and on
the **Chatter** tab.

**Held.** Twitch's AutoMod (and your blocked terms) can hold a message back before anyone sees it. Held messages wait
in the **Held** tab, oldest on top, with why they were held ("AutoMod: aggression, level 2", "Blocked term"). Click
**Allow** to let the message into the chat, or **Deny** to keep it out. Denying does not punish the viewer. The tab's
name shows how many are waiting, like **Held (2)**. When a mod decides on Twitch's website, the line says who.

**Mod tools.** This tab has three tools:

- **Shield Mode** is Twitch's emergency switch, for example during a hate raid. It turns on the strict settings you
  chose on Twitch. The tab shows whether it is on and who turned it on. Click **Turn Shield Mode on**, and later
  **Turn Shield Mode off**.
- **Unban requests**: banned viewers can ask to be unbanned. Click **Load** to see the waiting ones. Write an answer if
  you like ("One more chance, please follow the rules"), then **Approve** (the ban is lifted) or **Deny**.
- **Blocked terms**: words and phrases Twitch keeps out of your chat. Click **Load** to see them, type a new one and
  click **Block**, or click **Remove** next to one. `*` works as a wildcard, like `spoil*`.

**Suspicious chatters.** On the **Chatter** tab, Twitch messages get three more buttons:

- **Monitor**: the person's messages still show, marked for mods.
- **Restrict**: only mods see their messages.
- **Not suspicious**: removes the mark.

Twitch also marks people by itself, for example a likely ban evader. Their messages show **[suspicious]** in the chat,
and the Chatter tab says why ("suspicious: monitored (likely ban evader)").

**Missing permission?** These tools need Twitch permissions that you may not have given Cuebot yet. A greyed-out tool
then says which one, like "Sign in to Twitch again and allow ...". Go to the **Twitch** page, tick the permission on
the **Settings** tab (or click **Request everything**) and sign in again on the **Accounts** tab.

## The mod log

The **Mod log** tab lists every ban, timeout, unban, deletion, warning and chat mode change: who did it (you, a mod
from the [mod portal](../mods/mod-portal.md), an action, or a mod on the platform's website), to whom, and why. The
**Chatter** tab shows the same rows for the person you clicked.

On Twitch the log also lists what mods do on Twitch itself, with their name: bans, timeouts, unbans, deletions,
warnings, chat modes, Shield Mode, VIPs and moderators added or removed, raids, blocked terms, unban request answers,
held messages they allowed or denied, and actions in other channels of a shared chat. Your own actions from Cuebot
are listed once, even though Twitch reports them back too.

The log is a file on your computer (`modlog.db` in your data folder). It is part of your [backups](../help/backups.md)
and is never sent anywhere. Each row is linked to the one before it, so a changed or missing row can be noticed.

Cuebot removes personal details from old rows by itself:

- the text of a deleted message after **60 days**;
- who was timed out, warned or had a message deleted, after **180 days**;
- who was banned, **24 months** after the ban was lifted. A ban that is still in place keeps its row.

You can make these shorter (never longer) in `moderation.json` in your data folder: `MessageTextDays`, `TimeoutDays`
and `BanMonthsAfterEnd`.

## YouTube quota

Every YouTube action costs part of a daily allowance (quota) that resets at midnight Pacific time. A ban or timeout
costs 50 units, a deletion 50. The chat page shows how much is left when you work on YouTube.

Cuebot keeps some quota back so your chat keeps arriving: moderation stops when less than **Units moderation must
leave for reading chat** is left (2,000 at first; change it on the **YouTube** page under **Polling and quota**). For
more quota, use your own Google project: see **Advanced: use my own Google Cloud project** on the YouTube page.

YouTube can only lift a ban when it knows the ban's id. Cuebot keeps it in the mod log, so **Unban** works for bans you
made from Cuebot, even after a restart. Lift other bans in YouTube Studio.

## Rumble

Rumble has no official way for apps to moderate chat. At first, the buttons open your Rumble chat, where you do it
yourself.

On the **Rumble** page, under **Moderation (experimental)**, you can tick **Rumble moderation through unofficial
endpoints**. Cuebot then deletes messages and mutes viewers through the same web addresses Rumble's own chat uses, with
your chat sign-in. A timeout becomes a 5-minute mute (anything longer mutes until the stream ends) and a ban a
permanent mute. Unmute viewers on Rumble's website.

!!! warning
    Rumble's terms do not allow automated use of its website without Rumble's written permission. Rumble could act
    against your account, and the addresses can change without notice. Use it at your own risk.

## Sending as your bot

The list next to the platform picker in the send box chooses who posts your message:

- **Default**: each platform's own setting.
- **Broadcaster**: your channel.
- **Bot**: your bot account. It is offered only when a bot account is signed in on that platform (Twitch, YouTube or
  Kick).

## Mods and safety

Mods who moderate from the mod portal act with your account, so Cuebot adds a few safety rules:

- They cannot time out or ban you, platform moderators or VIPs, other portal mods, or your bot accounts. Only you can.
- Each mod can make at most 10 bans and 30 timeouts in 10 minutes.
- On Twitch and Kick their name goes in front of the reason, like `[mod: Sam] spam`, so the platform's own log shows
  who it was.
