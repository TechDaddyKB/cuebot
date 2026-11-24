# What's new

What changed in each version of Cuebot, newest first. Each line links to the guide that explains it.

To get the newest version, see [Updating Cuebot](updating.md).

## Unreleased

A voice library for free voices on your computer, now on Mac too.

- **Voice library.** Piper voices old and new, Kokoro (in six languages), Kitten, Matcha and sherpa-onnx voices such as GLaDOS all work, on Windows, macOS and Linux, after a one-time **Get voice engine**. See [the voice library](../sound/text-to-speech.md#the-voice-library-free-voices-on-your-computer).
- **Import voices.** Add a Piper voice (`.onnx` and `.onnx.json`) or a `.tar.bz2` / `.tar.gz` voice package, and open the voices folder from Cuebot. Voices you copy in show up without a restart. Imported voices are in your backups; downloaded ones are not, since Cuebot can download them again.
- **Your Piper voices move in** by themselves, without downloading them again.

Fixes:

- **Piper voices** no longer crash Piper (`Piper failed: exit code -1073740791` on Windows). Voices rebuilt in November 2026 for a newer Piper now speak through the voice engine. Without it, they say what to do instead of crashing.

## 1.0.6

A better start and an easy way to reach us in the phone app. Update it from Google Play or the App Store.

- **Getting started.** Before you pair, the app explains what it does and offers a demo Cuebot and a link to get Cuebot.
- **Send feedback.** Settings can post a problem or an idea to Cuebot's public GitHub issues, with your app and device details. You see what is included first.
- **Rate and share.** Settings has shortcuts to rate the app and share it.
- **Accessibility.** On Android, better screen reader labels, headings and error announcements.

## 1.0.5

Fixes, safer webhooks and a signed Mac app. Update Cuebot, and update the phone app from Google Play or the App Store.

- **Signed Mac app.** The Mac download is now signed and notarized by Apple, so it opens without a security warning. macOS treats it as a new app once, so allow the microphone, Accessibility and Input Monitoring again if Cuebot asks.

Fixes:

- **Acapela speech** no longer lets chat text change how a voice speaks.
- **Discord webhooks** no longer ping people or roles through text that arrives in a variable.
- **Saving settings** that fails now keeps your changes and no longer crashes Cuebot when it closes.
- **iPhone and iPad pairing** now works with servers that use HTTPS.

## 1.0.4

New options for actions, commands and chat, plus fixes. Update Cuebot, and update the phone app from Google Play or the App Store.

- **Only while live.** Timers, commands and triggers can run only while you are live, optionally on chosen platforms.
- **Cancel running actions.** A new step stops this action or a chosen one, including queued runs.
- **Better If steps.** If, While and Switch gain *at least* and *at most*, and up to four conditions joined with AND or OR.
- **Key hold.** Keyboard press can hold keys for a set time.
- **Text reply commands.** A command can reply with text without an action, and has a description and preset arguments.
- **Richer command permissions.** Built-in groups for mods, VIPs, subscribers, sub tiers and followers, and a **Command denied** trigger.
- **Banned accounts kept out.** Banned accounts no longer reach follows, alerts, speech or credits.
- **Interface zoom.** Zoom from 80% to 200% with Ctrl+= and Ctrl+-, plus a compact layout and a separate chat text size.
- **Multi-select.** Select several actions or sub-actions to switch on or off, move, duplicate, copy or delete them together.
- **Discord webhooks.** Send embeds and link buttons from a JSON or Discohook body, post in threads, and edit or delete sent messages.
- **Twitch Shared Chat.** Chat and the chat box overlay show which channel a message came from.
- **YouTube member emotes.** Show them from your own list of codes and images.

Fixes:

- **IBM Watson speech** no longer lets chat text change how a voice speaks.
- **Renaming an action** now updates the steps, deck buttons, hotkeys and quick actions that use it.
- **Ban and timeout as the bot** now really use the bot account. Sign the bot in again to grant the ban permission.
- **YouTube quota** estimates match Google's current costs.
- **A damaged actions file** now restores the newest good backup instead of starting empty.

## 1.0.3

A fix for the Android phone app. Update it from Google Play.

- **Chat no longer crashes the phone app.** On Android, new chat messages arriving while the screen turned or the
  keyboard opened could, rarely, close the app from the Chat tab. See [Phone app](../mods/phone-app.md).

## 1.0.2

Phone app fixes for mods and tablets. Update Cuebot and the phone app from Google Play or the App Store.

- **Phone app chat on tablets.** A tablet held upright now shows the events card as a strip above chat, so chat
  gets the full width and **Send as** no longer wraps. Turning the device keeps the chat on the newest message.
  See [Events next to chat](../mods/phone-app.md#events-next-to-chat).
- **The mod portal reconnects by itself.** In the phone apps, a dropped connection no longer closes the portal: it
  shows **Reconnecting…** and carries on on the same page. See
  [For mods](../mods/phone-app.md#for-mods).
- **Test sign-in for app testers.** The phone apps and the browser mod portal accept a test server's own sign-in
  page, so testers on the Cuebot Companion test server can join as a mod without a Google, Twitch or Discord
  account. Real invites still only use Google, Twitch and Discord.
- **Mod portal touch-ups.** In the phone apps the portal's colour fills the whole screen under a short page, and
  the user card's recent messages no longer leave a wide gap after the time.

## 1.0.1

A fix for the mod portal on tablets and phones. Update Cuebot so your mods get the new portal, and update the phone app
from Google Play or the App Store.

- **The mod portal fits tablets and phones in both directions.** In the phone app, the chat list, held messages and the
  user card no longer shrink to a sliver. Turned sideways, the menu moves to the top and the chat fills what's left
  of the screen, and turning the device keeps you on the same page. The app's own bar replaces the portal's, so there's
  more room for chat. Your mods need both Cuebot 1.0.1 and phone app 1.0.1 for all of it; either one on its own
  already stops the chat from shrinking.

## 1.0.0

Cuebot's first full release. It's no longer marked **Pre-release**, so the **Stable** update channel finds it. If you
switched to **Prerelease** during testing, you can switch back (see [Updating Cuebot](updating.md)).

- **See and fix what added time.** Subathon timers and goals have a new **History** tab. It lists every change
  with who, where it came from and how much it added, for today, this week, this month or this year. Take a change
  back (a chargeback, a test that slipped in), fix its amount, add a note, or export it all as a spreadsheet. See
  [History](../services/subathon-timers.md#history-see-and-fix-what-added-time).
- **Each service's money can count differently.** Throne buys you gifts instead of paying you, and merch shops pay
  you only part of a sale. Under **Money by service**, give each service its own value for each 1 of money, or 0 so
  it doesn't count. See [Money by service](../services/subathon-timers.md#money-by-service).

## 0.1.0-rc.22

- **Fix your stream totals.** The new **Stream Data** page lists the events behind your labels, goal bars and event
  lists. Fix a wrong name or amount, mark an event as a test, or remove it, and every total is worked out again.
  Test events now count for this stream only. See [Stream data](../chat/stream-data.md).
- **Events next to chat on your phone.** The phone app's **Chat** page shows the latest follows, subs, tips and raids
  beside the chat (or in a strip above it on a phone). Pick which events it shows in **Settings**. See
  [Events next to chat](../mods/phone-app.md#events-next-to-chat).
- **A side menu in the phone app.** Tap ☰ to switch sections. On a tablet, **Keep menu open** keeps it beside the
  page. See [The menu](../mods/phone-app.md#the-menu).

## 0.1.0-rc.21

- **Moderate every platform from the Chat page.** Delete, time out, ban, unban and warn on Twitch, YouTube, Kick and
  Rumble. Each button says what it really does on that platform. See [Moderating your chat](../chat/moderation.md).
- **A mod log.** Every ban, timeout, deletion and warning is written down: who, to whom and why, also when a mod did
  it on the platform's website. See [The mod log](../chat/moderation.md#the-mod-log).
- **Write as your bot.** Pick **Broadcaster** or **Bot** next to the send box. See
  [Sending as your bot](../chat/moderation.md#sending-as-your-bot).
- **More Twitch tools.** Messages AutoMod held back (**Allow** or **Deny**), Shield Mode, unban requests, blocked
  terms and suspicious chatters, right on the Chat page. See
  [Twitch: held messages, Shield Mode and more](../chat/moderation.md#twitch-held-messages-shield-mode-and-more).
- **Rumble moderation (experimental).** An opt-in way to delete messages and mute viewers on Rumble. Read the warning
  first. See [Rumble](../chat/moderation.md#rumble).
- **Chat for your mods.** The mod portal has a **Chat** page: your mods see every platform's chat in one list and
  moderate it, from a browser or a phone. New messages show up by themselves. See [Chat for mods](../mods/chat.md).
- **Linked accounts and linked bans (off by default).** Write down that accounts on different platforms are one
  person, keep notes, and ban all of their accounts at once, with a preview and one **Undo**. See
  [Linked accounts and linked bans](../chat/identity-links.md).
- **Chat and moderation in the phone app.** Read and write chat as yourself or your bot, and moderate, from your
  phone. See [Chat on your phone](../mods/phone-app.md#chat-on-your-phone).

## 0.1.0-rc.20

- **See what the Assistant is doing.** A bar shows what it is working on and for how long. See
  [Using it](../ai/assistant.md#using-it).
- **Fewer Approve cards.** Let the Assistant do some steps (or everything in one chat) without asking. See
  [Asking less often](../ai/assistant.md#asking-less-often).
- **Errors in the log.** Assistant errors now go in Cuebot's log, so a problem report shows them.
- **Better problem reports.** **Report a problem** asks what you were doing and what you saw. See
  [Asking for help](../help/troubleshooting.md#asking-for-help).
- **Sound when your device is busy.** If another app keeps your chosen speaker to itself, Cuebot plays on your default
  device instead of staying silent. See [Troubleshooting](../help/troubleshooting.md#common-problems).
- **Flowchart editor.** Add a step anywhere with a right-click, snap boxes to a grid, drag from a **+** port to a step,
  and disconnect a wire. See [The flowchart editor](../actions/flowchart.md#add-a-step-anywhere-on-the-canvas).
- **Speech queue.** Separate **Play** and **Pause** buttons, and a label that says whether it is playing. See
  [The queue and VoiceGate](../sound/text-to-speech.md#the-queue-and-voicegate).
- **Pitch for every Windows voice.** The pitch slider now works for the newer Windows voices, Piper and OpenAI too.
  Windows voices without a name are left out of the list. See [Text-to-speech](../sound/text-to-speech.md).
