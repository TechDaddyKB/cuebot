# Linked accounts and linked bans

Some viewers chat on more than one platform: `rowdy_guy` on Twitch, `Rowdy Guy` on YouTube, `rowdyguy` on Kick.
**Identity links** let you and your mods record that these accounts belong to one person, keep notes about them, and
ban or time out all of their accounts at once.

!!! warning "Pending review: off by default"
    Identity links are **off** until you turn them on. The wording to show viewers, the privacy (GDPR) position and how
    long links and notes are kept are still being checked by a legal and privacy reviewer. Everything on this page about
    privacy, the sample wording and the template reply is a draft from that review, **not legal advice**. Leave identity
    links off until the review is done, unless you accept that.

## Turning it on

On the **Chat** page, click **Options**. Under **Moderation**:

- **Identity links (off by default)**: shows linked accounts and notes on the **Chatter** tab, lets viewers verify
  with `!link`, and makes **Ban** and **Timeout** offer a person's other accounts.
- **Automatic mirroring (off by default)**: see [Automatic mirroring](#automatic-mirroring). It needs identity links.

While identity links are off, nothing about them shows anywhere, `!link` gets no answer, no ban or timeout goes through
a link, and Cuebot suggests nothing. Links you made earlier are kept (and still removed on time, see
[How long things are kept](#how-long-things-are-kept)); they come back when you turn it on again.

## How sure is a link?

Every link has one of three levels:

| Level | How it happens | Used in a linked ban? |
| --- | --- | --- |
| **Verified** | The viewer proved both accounts are theirs with `!link CODE`. | Yes, ticked |
| **Confirmed** | A mod suggested it and a **second person** agreed: you, or a mod allowed to confirm links. | Yes, ticked |
| **Suggested** | A mod suggested it; nobody has confirmed it yet. | Shown, but **unticked**. A mod cannot tick it for a ban. |

A viewer saying "I'm X on Twitch" from their YouTube account proves nothing about the Twitch account: anyone can claim
to be someone else, even to get them banned. That is why one person's word only ever makes a suggestion, and why the
mod who suggested a link can never confirm it themselves (not even you).

## On the Chatter tab

Click a message. With identity links on, the **Chatter** tab has a **Linked accounts** section:

- The accounts linked to this person, how sure each link is ("verified 2026-10-02", "suggested by Sam, not
  confirmed", "confirmed by Kris and Sam"), and their platform user id (hold the mouse over the line). Names can be
  copied; ids cannot.
- **Confirm** and **Wrong** on a suggestion. **Wrong** removes it and remembers it, so it is not suggested again.
- **Unlink…** takes one account out, or **all** of them (see [Unlinking](#unlinking)).
- **Same person as…**: pick the platform, type their name as it shows in that chat (they must have chatted recently),
  and say what shows it, like the message where they said so. This makes a **suggestion**.
- **Verify with !link** gives you a one-time code (see below).
- Accounts on other platforms **with the same name** are shown as a hint, with **Same person…** to fill in a
  suggestion. A hint creates nothing; the same name proves nothing. Cuebot never guesses links from how people write or
  when they chat.

### Rules Cuebot keeps

- An account belongs to one person. If the other account is already linked to someone else, Cuebot shows both and asks
  whether to **merge** the two or **move** the account. Merged and moved accounts become suggestions again.
- **You (the broadcaster), platform moderators, VIPs, your mod team in the mod portal and your bot accounts can never
  be linked**, by anyone.
- A person with **more than four accounts**, or **two accounts on one platform** (an alt), needs **you** to confirm.
- Every suggestion, confirmation, rejection and unlink is a row in the [mod log](moderation.md#the-mod-log).

### Verifying with !link

1. On the viewer's first account, click **Verify with !link**. You get a code like `K7MP3Q`, valid for **10 minutes**.
2. Send the code to the viewer **privately** (a whisper or DM), not in public chat. Anyone who sees it could use it.
3. The viewer types `!link K7MP3Q` in chat from their **other** account.

Cuebot answers "@name thanks, that's confirmed." and the link is **verified**. A code works once. Wrong codes get no
answer; after five wrong codes in ten minutes an account is ignored for a while, so codes cannot be guessed.

## Notes

The **Notes** box keeps short notes about a viewer for your team, like "Warned about spoilers on Nov 14". A note
written on one account shows on every account linked to it.

Write only what helps you moderate: what happened and when. **Never** write down anything about health, religion or
beliefs, politics, sexuality, ethnicity or anything similar.

## Linked bans and timeouts

When the person you click has linked accounts, **Ban** and the **Timeout** buttons open a window that shows, before
anything is sent:

| | Account | Link | What will happen | Notes |
| --- | --- | --- | --- | --- |
| ☑ | Twitch: `rowdy_guy` | (the one clicked) | Ban | |
| ☑ | Kick: `rowdyguy` | verified 2026-10-02 | Ban | |
| ☐ | YouTube: `Rowdy Guy` | suggested by Sam, not confirmed | Ban, 50 quota units (7,400 left today) | Unticked: not confirmed |
| — | Rumble: `RowdyG` | confirmed by Kris and Sam | By hand: open Rumble | No API |

- For timeouts each row shows the time the platform will really use ("Kick: 10 min, rounded up from 9 min 30 s").
- An account that is already banned starts unticked.
- A **reason is required**. Twitch and Kick also get the mod's name in front (`[mod: Sam] ban evasion`) and the
  platform's length limit.
- A ban on **more than one platform** asks once more: "Ban 3 accounts on 3 platforms?".

After you click, each platform is asked on its own. One that fails does not undo the others: its row says what went
wrong, with **Retry**. A platform without a way for apps to ban (Rumble) shows **Open website** and **I did it**:
press it after you did it there, and it is recorded.

The whole thing is one **incident**. **Undo** lifts every ban and timeout of it that worked (YouTube included, using the
ban id Cuebot kept). It is offered on the chat page for 24 hours and at any time from the **Mod log** tab (**Undo
incident**). Deleted messages cannot be brought back on any platform, and Twitch warnings cannot be withdrawn. Bans
done by hand must be lifted by hand. Undoing is logged too, and costs YouTube quota.

### When a banned person shows up with another account

If an account is confirmed or verified as part of a person who is banned on another platform, the chat page shows:
"rowdyguy on Kick is linked to a person banned on Twitch and YouTube. Apply here too?" with **Apply here too** and
**Not now**. You get a notification.

### Automatic mirroring

With **Automatic mirroring** on, a ban on any platform (from Cuebot, from a mod, or on the platform's own website) is
applied at once to the person's **verified and confirmed** accounts, never to suggested ones, without the preview. Each
mirrored ban is in the mod log as "Automatic mirroring", you get a notification, and **Undo incident** lifts them all
(including the first ban).

## Mods and safety

- Every linked action by a mod notifies you, like a "Live and tell me" change.
- A mod can start at most **5 linked bans an hour** (on top of the usual 10 bans and 30 timeouts in 10 minutes).
- On the **Mod log** tab, a mod's row offers **Undo this mod's last hour**: it lifts the bans and timeouts they made in
  the last hour and withdraws the link suggestions they made.
- **Unlinking** asks: "Also reverse the 2 actions that were applied because of this link?" and lists them. When you
  tick **This link was wrong**, reversing starts ticked.

## Unlinking

**Unlink…** on an account asks:

- **This link was wrong**: the pair is remembered as wrong (only the two account ids are kept), so it is not suggested
  again, and the bans and timeouts made through the link start ticked for reversal.
- **Unlink all of this person's accounts**: dissolves the person.

## How long things are kept

These are the starting values from the plan; the reviewer may change them. You can make them shorter (never longer)
in `moderation.json` in your data folder.

| What | Kept for | Setting |
| --- | --- | --- |
| A suggestion nobody confirmed | 60 days | `UnconfirmedLinkDays` |
| A confirmed or verified link | While it stands | |
| Who was linked, in the mod log | While linked, then 24 months | `BanMonthsAfterEnd` |
| A link marked wrong | 24 months | `BanMonthsAfterEnd` |
| A note | 24 months after its last edit | `NoteMonthsAfterEdit` |
| A **YouTube** account in a link | Refreshed every 30 days (seeing them in chat counts), or removed | |

YouTube's rules allow keeping data from YouTube only while it is refreshed at least every 30 days. Cuebot reads a
linked YouTube account again after 25 days (1 quota unit) and removes it from the link if YouTube no longer has it or
it could not be refreshed within 30 days. Notes on YouTube accounts that are in no link go when the viewer has not
chatted for 30 days.

## Privacy

This section is part of what the reviewer is checking. Not legal advice.

- **You decide** what is kept and why, so you are responsible for it (in GDPR words, the "controller"). Everything is
  in one file on your computer (`modlog.db` in your data folder), included in your backups and never sent anywhere.
  Cuebot's makers never see it. Mods see it only through the encrypted mod portal.
- **Why you may keep it**: keeping your community safe and stopping people who dodge bans. Use it for that, not to
  build profiles of viewers.
- **Keep the minimum**: the account ids, the names at the time, and the evidence for a link. Never IP addresses or
  emails, nothing from outside your own chats.
- **No sharing between streamers**: no shared ban lists, no exports to other communities.
- **Tell your viewers** (sample wording below), and tell your mods that the mod log records what each of them did.

### Sample wording for your channel rules (draft, pending review)

> **Moderation across platforms.** We stream on Twitch, YouTube, Kick and Rumble, and our mod team moderates all of
> them together. If we are confident that accounts on different platforms belong to the same person, we may note that
> and apply a ban or timeout on all of them. We only link accounts when you verify them yourself with `!link`, or when
> two of our team agree. We keep these notes only on the streamer's computer, never share them with other channels,
> and delete them on the schedule in our rules. To ask what we hold about you, to contest a link or a ban, or to have
> your data deleted, message `<contact>`.

## When a viewer asks what you hold, or to be forgotten

1. Open the **Users & Groups** page and find the viewer (any of their accounts).
2. If they asked for a copy, click **Export data**. Cuebot saves a file in the `exports` folder of your data folder
   with their accounts, links, notes and mod log rows, for every account **confirmed or verified** as the same person.
   Send it to them privately, then delete the file.
3. Click **Forget viewer…** and confirm. Cuebot removes their points, variables, groups and so on from every linked
   account, deletes their links and notes, and blanks their names and message text in the mod log. **An active ban
   stays**: its row and the platform user id are kept so the ban keeps working, and the platform's own ban is not
   touched. Suggested links are not followed, so a wrong guess never erases someone else's data.
4. Reply within a month (YouTube asks for 7 days for its data). Daily logs and older backups keep what they already
   have until they are replaced.

### Template reply (draft, pending review)

> Hi `<name>`, thanks for your message. We have deleted what we held about you: your links between platforms, our
> notes, and your name and messages in our moderation log, for these accounts: `<accounts>`.
>
> *(Only if a ban is still active:)* Your ban on `<platforms>` is still in place, so we kept the record of that ban and
> your account id, which we need for the ban to work. We removed everything else. If you want to contest the ban,
> reply to this message.
>
> *(If they asked for a copy:)* A copy of what we held is attached.
