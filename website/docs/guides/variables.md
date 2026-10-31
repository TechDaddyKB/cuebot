# Variables, the %words%

A **variable** is a word with a value behind it. You type the word into a message, and when the action runs, Cuebot
swaps the word for its value.

```text
You type:      Thanks for the follow, %user%!
Chat shows:    Thanks for the follow, Bob!
```

There are three kinds, and you can tell them apart by the signs around the name:

| Looks like | Kind | Lasts | Example |
| --- | --- | --- | --- |
| `%user%` | **Variable of this run** (also called an *argument*) | Only while this action runs | the name of the person who followed |
| `~deaths~` | **Global variable** | Between runs, and after a restart if you want | a death counter for your game |
| `$math(2 * 5)$` | **Function**: a little calculator | Worked out on the spot | `10` |

Variables work in almost every text box of a sub-action (not in code boxes).

## %Variables of this run%

When a trigger fires, it hands the action some values. A follow gives you `%user%`. A cheer gives you `%user%` and
`%bits%`. A chat command gives you what was typed after it.

**Where do I find the list?** Click a trigger in your action and open **Variables this gives your action**. Every
sub-action that adds new values has a **Variables this adds for the next steps** list too. The
[trigger reference](../reference/triggers/index.md) lists them all.

Some common ones:

| Variable | What it holds | Example |
| --- | --- | --- |
| `%user%` | The viewer's display name | `Bob` |
| `%message%` | What they wrote in chat | `hi everyone` |
| `%input%` | Everything typed after a command | for `!so bob hi`: `bob hi` |
| `%input0%`, `%input1%`… | One word after the command (counting starts at 0) | for `!so bob`: `%input0%` is `bob` |
| `%bits%` | How many bits someone cheered | `500` |
| `%platform%` | Where it came from | `Twitch` |

Capital letters do not matter: `%User%` and `%user%` are the same.

**Built-in variables** work in every action, whatever the trigger:

| Variable | Gives you |
| --- | --- |
| `%date%`, `%time%` | Today's date and the time now |
| `%datetime%`, `%longtime%` | Date and time together; the time with seconds |
| `%unixtime%` | The time as one big number (seconds since 1970), handy for maths |
| `%random%` | A random whole number from 1 to 100 |
| `%random:1-6%` | A random whole number from 1 to 6 (a dice roll) |
| `%lf%` | A new line |

**Make your own** with the **Set argument** sub-action: give it a name and a value, then use it as `%name%` in later
steps of the same run.

!!! tip "A word that is not a variable stays as it is"
    If Cuebot does not know a name, it leaves the text alone. So `100%` and `%nothing%` are written just like that.

### Making numbers and dates look nice

Add a colon and a format after the name:

| You type | You get |
| --- | --- |
| `%amount:c2%` | `$5.50` (money with two decimals) |
| `%count:N0%` | `1,234` (with a thousands separator) |
| `%date:yyyy-MM-dd%` | `2026-10-31` |
| `%time:HH:mm%` | `20:15` (24-hour clock) |

The money sign and the separators come from **Culture** (your language and country), under **Text formatting** on
the **Settings** page, tab **Audio & text**.

## ~Global variables~

A **global variable** remembers a value between runs. Use it for counters and anything you want to keep.

**Example: a death counter.**

1. Make an action `Death` with a command trigger for `!death`.
2. Add **Set global variable**: **Variable name** `deaths`, **Mode** `Increment` (add one), leave **Value** empty.
3. Add **Send chat message (any platform)** with: `That is death number ~deaths~!`

Every `!death` adds one and chat shows the new number.

Settings of **Set global variable**:

- **Mode**: **Value** sets it, **Increment** adds, **Decrement** takes away. Empty **Value** means 1.
- **Keep after restart**: ticked (the default) means the value is saved. Unticked means it is *temporary* and is gone
  when Cuebot closes.
- **Auto type**: store `5` as a number and `true` as a yes/no value, instead of as text.

To use a global's value inside a calculation or an **If**, you can also copy it into a run variable with **Get global
variable** (it goes into **Store in argument**, then you use it as `%deaths%`).

### Seeing and changing them

**Automation > Variables** shows every global variable, on the tabs **Persisted** (saved) and **Temporary**. You can
type a **Name** and **Value** and click **Set**, or delete them. Press ++ctrl+alt+v++ to jump there from anywhere.

## Viewer variables

A **user variable** belongs to one viewer. "Bob has 3 hugs, Alice has 7." They are not written with signs in text.
Instead you use the sub-actions **Get user variable**, **Set user variable** and **Delete user variable**.

- Leave **User id** empty and Cuebot uses the viewer who started the action.
- **Get user variable** puts the value into a run variable, so you can write `%hugs%` in the next message.

The **Users** tab on **Automation > Variables** shows them.

!!! note
    For a channel currency, use [Points](community.md) instead. It has earning, leaderboards and a store built in.

## $Functions()$

A function does a small job with text or numbers. Write a `$`, the function name, the input in brackets, and a closing
`$`.

| You type | You get |
| --- | --- |
| `$math(2 * %bits%)$` | twice the bits |
| `$math(round(%count% / 3, 1))$` | a division, rounded to 1 decimal |
| `$upper(%user%)$` | `BOB` |
| `$lower(%message%)$` | the message in small letters |
| `$length(%message%)$` | how many letters the message has |
| `$trim(%input%)$` | the text without spaces at the start and end |
| `$replace(%message%,cat,dog)$` | the message with every "cat" changed to "dog" |
| `$substring(%user%,0,3)$` | the first 3 letters: `Bob` |
| `$pick(Heads,Tails)$` | one of them, at random |
| `$urlencode(%input%)$` | text made safe to put in a web link |
| `$parse(line%i%)$` | the value of the variable whose name is built while running, like `%line2%` |

**Example: a coin flip.** Make a `!flip` command and send: `%user% flips a coin... $pick(Heads,Tails)$!`

Cuebot first fills in the `%variables%` and `~globals~`, then works out the functions, inside ones first. What viewers
type is never run as a function. If Bob writes `$math(1+1)$` in chat, it stays plain text.

If a function has a mistake (like dividing by zero), Cuebot leaves it as written and puts a warning in the log.

Next: [Words you will see](glossary.md).
