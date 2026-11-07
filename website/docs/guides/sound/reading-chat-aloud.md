# Reading chat and events aloud

Cuebot can read your chat out loud, let viewers use a `!tts` command, and say something when someone follows, subs or
raids. It uses the voices you set up on **Audio > Speech**. New to voices? Start with
[Text-to-speech](text-to-speech.md) to pick an engine and make a voice alias first.

## Reading chat out loud

Open **Chat & commands** on **Audio > Speech**.

**Read chat aloud**: switch it **On**, then choose:

- **Who is read aloud**: everyone, or only some roles, like subscribers.
- **Message template**: how a message is read, for example with the name first.
- **Speak nicknames instead of names**: handy for names like "xX_Gamer_1987_Xx".
- **Random voice per user**: each viewer gets their own voice.

**The !tts command**: switch it **On** and viewers can type `!tts hello there` to have it read. The **Command word**
is `!tts`, and you can change it. Set a **Viewer cooldown (seconds)** and which aliases viewers may pick.

The **Moderation** part can stop a message when a moderator deletes it, or stop a viewer's messages when they are timed
out or banned.

**Users** tab: give one viewer their own voice or nickname, or mark them **Ignored** (never read).

To clean up what viewers write before it is spoken (bad words, links, emotes, "loooooool"), use
[Filters](text-to-speech.md#filters).

## Reading events out loud

Open **Events**. Choose which follows, subs, gifts, raids, cheers and redemptions are spoken, and the text for each.
In the text, `%name%` is the viewer, `%gift%` the number of gifted subs, `%title%` the reward and `%input%` what the
viewer typed.
