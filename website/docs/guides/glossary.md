# Words you will see

A small dictionary. If a word on another page is new to you, look here.

**Action**: A job for Cuebot, with a name, like "Thank new followers". It is a list of steps (sub-actions). See [How
actions work](actions-and-triggers.md).

**Alert**: A pop-up on your stream when something happens, like a follow. Shown by the **Alert box** widget. See
[Overlays and alerts](overlays.md).

**API**: A door that lets one program talk to another. "Cuebot uses the OBS API" means Cuebot talks to OBS through it.

**API key**, **token**: A long secret code that proves a program is allowed to use something, a bit like a password.
Keep them secret.

**Argument**: Another name for a [variable](variables.md) of the current run, like `%user%`.

**Bot account**: An optional second account on a platform, like "MyChannelBot", that writes Cuebot's chat messages for
you. See [Connecting your platforms](connecting-platforms.md#broadcaster-account-and-bot-account).

**Branch**: One of the lists inside a step like **If**: the "yes" list or the "no" list. New steps can go into a
branch.

**Broadcaster account**: Your own channel, the one you stream on. Cuebot gets events like follows and raids through
it.

**Browser source**: A source in OBS that shows a web page. Cuebot's overlays are shown this way.

**Command**: A word viewers type in chat, usually with a `!` in front, like `!hello`. See [Chat
commands](commands.md).

**Cooldown**: A wait time before a command can be used again, so it cannot be spammed.

**Cron**: A short way to write a schedule, with five parts: minute, hour, day, month, weekday. `0 20 * * 5` means
"every Friday at 20:00". Used by [timers](actions-and-triggers.md#timers).

**Data folder**: The folder where Cuebot keeps all your settings and actions. See [Backups and your
data](backups-and-your-data.md).

**Deck**: A page of buttons Cuebot shows in a web browser, for your phone or tablet. Not the same as a [Stream
Deck](stream-deck.md).

**Engine**: The thing that makes a text-to-speech voice, like Windows voices or ElevenLabs.

**Encrypted**: Scrambled with a secret key, so that nobody else can read it. Cuebot stores your sign-ins this way.

**Flowchart**: A drawing of an action, with boxes and lines, instead of a list.

**Global variable**: A value Cuebot remembers between runs, like a death counter. Written with `~` signs: `~deaths~`.
See [Variables](variables.md#global-variables).

**Group**: Two meanings. A **group of actions or commands** is a folder that keeps a list tidy. A **group of viewers**
(on **Chat > Users & Groups**) is a named list of people, like "Regulars", that you can allow on a command.

**LAN**: Your home network: the computers, phones and tablets connected to your router. "Allow LAN connections" lets
those devices reach Cuebot.

**MCP**: A common way for AI apps to use another program's tools. Lets an AI app work on Cuebot. See [AI
tools](assistant.md).

**Overlay**: A see-through layer on top of your stream, like a chat box or alerts. See [Overlays and
alerts](overlays.md).

**Platform**: A site you stream on: Twitch, YouTube, Kick or Rumble.

**Points**: Cuebot's own channel money that viewers earn by watching and chatting. See [Points, store and
giveaways](community.md).

**Queue**: A waiting line for actions. A **blocking** queue runs one action at a time, so alerts do not overlap. See
[Queues](actions-and-triggers.md#queues-one-at-a-time).

**Relay**: A small helper on your own free Cloudflare account that passes webhooks on to Cuebot. See [Donations and
integrations](donations-and-integrations.md#webhooks-ko-fi-patreon-throne-and-more).

**Run**: One time an action does its job. Every run is listed on **Chat > History**.

**Scope**, **permission**: What you allow Cuebot to do on a platform, like "read chat" or "change the title". You pick
them when you sign in.

**Server**: Here: a part of Cuebot that other programs can connect to, like the overlay server that OBS uses. See
**Apps > Servers**.

**Sign-in**: Letting Cuebot use your account on a platform. You do it once in your browser, and Cuebot remembers it,
locked (encrypted) on your computer.

**Sub-action**: One step inside an action, like "send chat message" or "switch scene". Every sub-action is listed in
the [sub-action reference](../reference/sub-actions/index.md).

**Text-to-speech (TTS)**: The computer reading text out loud. See [Text-to-speech](text-to-speech.md).

**Timer**: Starts actions on a clock, like every 15 minutes. See [Timers](actions-and-triggers.md#timers).

**Trigger**: What starts an action, like "someone followed" or "someone typed `!hello`". Every trigger is listed in
the [trigger reference](../reference/triggers/index.md).

**Variable**: A word with a value behind it, written in `%` signs, like `%user%`. Cuebot swaps it for the real value
when the action runs. See [Variables](variables.md).

**Voice alias**: A nickname for a voice and its settings, like "Narrator", used by text-to-speech.

**Webhook**: A small message one web service sends to another when something happens, like a Ko-fi tip.

**Widget**: One piece of an overlay, like the chat box, a goal bar or the alert box.
