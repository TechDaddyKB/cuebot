# Subathons and goals

**Chat > Subathon & Goals** has two tools that stream events feed:

- A **subathon timer** counts down. Subs, bits, tips, Kicks and other events add time to it. When it reaches
  `0:00:00`, the subathon is over. People also call this a donothon or a marathon timer.
- A **goal ladder** collects points towards steps. For example: a new emote at 10 points, another at 20, a cosplay
  stream at 50.

Both work on every platform you connected, and both remember where they are between streams. You can have as many
timers and goals as you like, each with its own name.

## What events are worth

Every timer and every goal has its own list under **What events add**. A timer's list is in seconds. A goal's list is
in points. 0 means the event does not count.

| Event | What the number is for |
| --- | --- |
| Twitch sub, tier 1, 2 and 3 | Each sub or resub |
| Kick sub, YouTube membership, Other subscriptions | Each sub (Other is Rumble, Ko-fi, Patreon and the rest) |
| Gift subs | Count like subs, one for each sub gifted. Untick **Gift subs count like subs** to leave them out |
| Each 100 bits, Kicks or Jewels | 500 bits count 5 times |
| Each 1 of money | Tips, Super Chats, Super Stickers, rants, donations and shop orders |
| Follow | Each new follower |
| Raid, and Raid per viewer | A raid of 25 viewers counts *Raid* + 25 × *Raid per viewer* |
| Channel point rewards | One reward per line, like `Add a minute = 60` |

Money is counted in your currency from the [Top spenders](community.md#top-spenders) settings. A €5 Super Chat counts
as its value in your currency, with the exchange rate of that stream. Charity donations only count when **Count
charity donations** is ticked there. Test events never count.

You can change any number at any time, even in the middle of a subathon. The new numbers count from then on. Time and
points already added stay as they are.

### Too much or too little: the multiplier

Each timer and goal has a **Multiplier**. Everything events add is multiplied by it:

- `2` is a double-time happy hour.
- `0.5` halves what events add, when the timer is filling up too fast.
- `1` is normal.

The **Set a subathon timer's multiplier** sub-action changes it from an action. For example, a Stream Deck button
can start a happy hour, and a second action can end it an hour later with a **Delay**.

## A subathon timer

1. Click **Add a timer**. Give it a name.
2. Open **Settings**. Set the **Starting length**, like 240 minutes for 4 hours.
3. Open **What events add** and set what each event is worth.
4. Click **Start**.

The buttons under the clock work at any time: **Start** (also carries on after a pause), **Pause**, **Reset** (click
twice), and **Add**, **Take away** or **Set time left** with the number of minutes next to them.

### Settings

- **Longest in total** stops the subathon from going on forever. With 24 hours, the time already run plus the time
  left can never pass 24 hours. Extra time is not added. 0 means no limit.
- **Start or carry on when I go live** starts the timer, or carries on after a pause, when a platform goes live.
- **Pause when my stream ends** pauses it when your last live platform goes offline.
- **Events add time while it is paused** lets events count while the timer is paused. Untick it to count only while it
  runs. A timer that has not started, or has ended, never takes event time.

### Closing Cuebot

If Cuebot closes while a timer runs, the timer is frozen. When Cuebot opens again it is paused, with the time it had.
No time is lost to a crash. If **Start or carry on when I go live** is ticked, it carries on when you go live.

### When time is up

The timer stops at `0:00:00` and the **Timer ended** trigger runs. Use it to end the stream, for example with the OBS
**Streaming** sub-action set to stop, or to thank everyone. Adding time by hand to an ended timer brings it back, paused.
**Start** on an ended timer starts over from the starting length.

## A goal ladder

1. Click the **Goals** tab, then **Add a goal**. Give it a name, like `Emote ladder`.
2. Under **Steps**, write each step's points and what viewers get, like `10` and `New emote`. Click **Add a step** for
   more. Cuebot keeps the steps in order by points, so you can add them in any order.
3. Open **What events add** and set what each event is worth. A new goal counts 1 point per sub.

When the points reach a step, the **Goal step reached** trigger runs once for that step. If points go down and up
again, it does not run twice. After the last step, **Goal completed** runs. **Reset to 0** (click twice) makes every
step reachable again. Tick **Start from 0 each new stream** under **Settings** to reset it by itself. A new stream is
when you go live 15 minutes or more after you went offline, so a short disconnect does not count.

## Show it on stream

Add the **Subathon timer** or **Goal ladder** widget to an overlay (see [Overlays and alerts](overlays.md)). In its
settings, type the timer's or goal's name, or leave it empty for the first one.

- The **Subathon timer** shows the time left, and a `+5:00` that floats up when time is added. It says **Paused**
  when paused.
- The **Goal ladder** shows a bar to the next step, and a list of steps with a tick next to the ones reached.

## Commands and alerts

| You want | Use |
| --- | --- |
| `!timer` in chat | **Get a subathon timer**, then **Send chat message** with `%subathonFormatted% left!` |
| `!goal` in chat | **Get a goal**, then a chat message with `%goalPoints% points. Next: %goalNextStep% at %goalNextStepPoints%` |
| An alert when time is added | The **Time added** trigger. `%formattedSeconds%` is like `+0:05:00` |
| An alert for each step | The **Goal step reached** trigger. `%step%` is the step's name |
| Time from your own event | The **Add time to a subathon timer** sub-action, in any action |
| A mod-only pause | A command that runs **Start, pause or reset a subathon timer** with **Pause or resume** |

Scripts can do the same with `subathon.*` and `goals.*` (see
[Script functions](../reference/script-functions/index.md)).
