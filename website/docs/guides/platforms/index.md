# Your platforms

Cuebot needs to be signed in to the places you stream, so it can read chat, see follows and post messages. Each platform
has its own page under **Platforms** in the left rail: **Twitch**, **YouTube**, **Kick** and **Rumble**.

- [Twitch](twitch.md): sign in, then run your channel, rewards, polls and moderation from Cuebot.
- [YouTube](youtube.md): sign in with Google, and keep an eye on YouTube's daily limit.
- [Kick](kick.md): sign in, set your title and category, and manage rewards.
- [Rumble](rumble.md): connect with a key link from Rumble, and optionally sign in to write in chat.
- [More than one persona (profiles)](profiles.md): give each persona its own accounts, mods, actions and overlays,
  and switch between them in two clicks.

Every platform page has **Connect** and **Disconnect** buttons at the top. Tick **Connect automatically on startup**
(on Rumble: **Connect automatically**) and Cuebot reconnects each time it starts. If the platform can't be reached at
startup (your internet isn't up yet, or the platform has a short outage), Cuebot keeps trying in the background, at
first every few seconds and then once a minute, until it connects. Clicking **Connect** or **Disconnect** yourself
stops the retries. A sign-in or key the platform rejects is not retried: sign in again or fix the key.

!!! note "Your sign-ins stay on your computer"
    Cuebot stores sign-ins in a locked (encrypted) form on your computer. They are never put in backups and never
    leave your computer, except to talk to the platform itself. See [Your data and privacy](../help/your-data.md).

## Broadcaster account and bot account

- Your **broadcaster account** is the channel you stream on. Cuebot gets every event (follows, subs, raids) through
  it. You always need this one.
- A **bot account** is optional. It is a second account, for example "MyChannelBot", that writes Cuebot's chat messages.
  Viewers then see the bot talking, not you.

!!! tip "Signing in the bot"
    Your browser is probably logged in as you. Before you sign in the bot, log in as the bot in your browser, or open
    the sign-in link in a private window. Otherwise the platform signs in the wrong account.

## Using your own app (advanced)

Cuebot signs in with its own app on Twitch, YouTube and Kick. You do not need to do anything. If you want to use your
own developer app instead, open the **Advanced** part of that platform's page (like **Advanced: use my own Twitch
application**) and follow the steps there. Leave it empty to use Cuebot's.

## Signing out

Click **Sign out** on the platform page (**Sign out bot** for the bot account). On Twitch, YouTube and Kick, Cuebot
also asks the platform to cancel its access. Rumble has no way to do that: Cuebot forgets the sign-in on your computer,
and you can sign out of other places on rumble.com.

Next: [How actions work](../actions/how-actions-work.md).
