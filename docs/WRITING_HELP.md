# Writing help text

Cuebot is made for streamers, and most streamers are not programmers. Every description, field help and tooltip
should read as if you were explaining it to a five-year-old: short words, one idea at a time, and an example
whenever an example is quicker than an explanation.

## Where help lives

Help for triggers and sub-actions lives in `src/Cuebot.Core/Help/*.json` (one file per area). The action editor,
the type picker, the REST/MCP catalogue and the documentation site all read it from there. A field with no entry
falls back to the `Help` written next to its `ParameterDescriptor` in the module code.

```json
{
  "triggers": {
    "twitch.follow": {
      "description": "Runs when someone follows your Twitch channel. Great for a thank-you message or an alert.",
      "fields": { "key": "What to type here, with an example." },
      "variables": { "user": "The name of the person who followed" }
    }
  },
  "subActions": { "...": { "description": "...", "fields": {}, "variables": {} } }
}
```

- **triggers / subActions**: keyed by type id (as shown in the catalogue).
- **description**: what it does and when you would use it, in one or two sentences (at most about 220 characters).
- **fields**: one entry per setting, keyed by the setting's key (at most about 200 characters each).
- **variables**: the `%variables%` the trigger gives the action (or the sub-action adds for later steps), keyed by
  name without the `%` signs. List the useful ones, not every internal value.

A test fails when a trigger or sub-action has no description, when a setting has no help at all, or when the file
names a type or setting that does not exist. Adding a trigger or sub-action therefore means adding its help.

## How to write it

- **Triggers start with "Runs when…"**: "Runs when someone raids your channel."
- **Sub-actions start with a verb**: "Sends a message in your Twitch chat." "Switches OBS to another scene."
- **Say what it is good for** when that is not obvious: "Handy for a 'thanks for the raid' shout-out."
- **Say what to type**, with a real example in quotes: `The scene name exactly as OBS shows it, like "Just Chatting".`
- **Say what happens when it is left empty**, or that most people can leave it alone.
- **Give units**: seconds, milliseconds, percent, 0–100.
- **Say where to find a value**: "Copy it from OBS: right-click the source and choose Properties."
- **Checkboxes**: say what ticking it does. **Lists**: explain the options that are not obvious.
- Talk to the reader ("you", "your viewers", "your chat"), present tense, no marketing.

Avoid programmer words. If one cannot be avoided, explain it in the same sentence.

| Instead of | Write |
| --- | --- |
| argument, parameter, arg | variable (the `%name%` words) or setting |
| boolean, true/false | tick / untick |
| string, integer, float | text, whole number, number |
| null, empty string | empty |
| regex | search pattern (explain what it matches) |
| payload, JSON, API, endpoint | the data, the address, the web link (explain if needed) |
| execute, invoke | run |
| instantiate, initialize | start, set up |
| enum, choice | list, option |

Only describe what the code really does. If you are not sure how a setting behaves, read the module code before
writing; never guess.

## Tooltips elsewhere in the app

The same rules apply to `ToolTip.Tip` and help lines in the other pages: say what the control does and what happens
when you use it, in one short sentence, without jargon.
