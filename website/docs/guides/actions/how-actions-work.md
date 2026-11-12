# How actions work

Everything Cuebot does is built from a few pieces. Once you know them, you know Cuebot. This page explains the ideas.
The pages after it show you every button.

![The Actions page: actions on the left, triggers in the middle, the steps of the selected action on the right](../../assets/screens/actions-sub-actions.png)

## The three pieces

| Piece | What it is | Stream example |
| --- | --- | --- |
| **Action** | A job, with a name. | "Welcome raiders" |
| **Trigger** | *When* the job starts. | "Someone raided my channel" |
| **Sub-action** | One *do this* step of the job. An action has a list of them. | "Write *Welcome, raiders!* in chat" |

Think of a recipe card. The **action** is the card. The **sub-actions** are the steps written on it, done from top to
bottom. The **trigger** is the moment you pick the card up and start cooking.

- One action can have **many triggers**. "Say thanks" could start on a follow *and* on a sub.
- One kind of trigger can start **many actions**. A raid could start "Welcome raiders" *and* "Play raid sound".
- An action with **no trigger** only runs when something starts it on purpose: you with the **Run** button, a
  command, or another action (the **Run action** sub-action).
- When one event matches two triggers of the same action, the action still runs only once.

All of this lives in **Automation > Actions**.

## Steps run in order, and some steps choose

Sub-actions run from top to bottom. Each one waits for the one before it to finish. Some steps can make decisions:
an **If** step asks a question and then runs either its **True** list or its **False** list. A **Group** keeps steps
together like a folder. **Run at the same time** does several lists at once. These lists inside a step are called
**branches**. When a branch is done, the action carries on with the next step under it.

[Logic and branches](logic-and-branches.md) explains every step that decides, repeats, waits or picks at random.

## Variables: the %words%

A trigger hands your action some facts. A raid gives `%user%` (who raided) and `%viewers%` (how many came along).
Type these words, with the `%` signs, into any sub-action text and Cuebot swaps in the real value when the action
runs. So `Welcome %user% and your %viewers% friends!` becomes *Welcome Bob and your 25 friends!*

Some sub-actions add new variables for the steps after them. Words in `~` signs, like `~deaths~`, are saved values
that last between runs. [Variables, the %words%](variables.md) explains them all.

## Queues: taking turns

When ten people follow at once, the follow action is started ten times. A **queue** is a waiting line: the runs wait
their turn, so the alerts play one after another instead of all at the same time. By default each action waits for
its own earlier runs. [Queues](queues.md) explains the options.

## On and off

Every action, trigger and sub-action has its own on/off switch.

- An action that is **off** does nothing, even when its trigger happens. It looks faded in the list.
- A trigger that is **off** no longer starts the action. Other triggers still do.
- A sub-action that is **off** is skipped, together with everything inside it.

## Two ways to look at the same action

The editor has two tabs. Both edit the **same** action. Change one and the other changes too.

| Tab | Looks like | Best for |
| --- | --- | --- |
| **Sub-actions** | A list, like a to-do list. | Adding steps and changing their settings. |
| **Flowchart** | Boxes joined by lines, like a map. | Seeing decisions and branches at a glance, and dragging steps to another place. |

## Where to go next

1. [Your first action](your-first-action.md): build a raid welcome, click by click, and test it.
2. [The action editor](action-editor.md): every button, menu and shortcut of the list editor.
3. [The flowchart editor](flowchart.md): the same action as boxes and lines.
4. [Logic and branches](logic-and-branches.md): If, Switch, loops, groups, random picks and doing things at once.
5. [Variables](variables.md) and [Queues](queues.md).

There are hundreds of triggers and sub-actions. The [trigger reference](../../reference/triggers/index.md) and the
[sub-action reference](../../reference/sub-actions/index.md) list every one, with all their settings.

## Scripts (for the curious)

When no sub-action does what you want, a code step lets you write a little program in C# or Python. This is optional
and needs some coding. The steps are in the **Code** group of the sub-action list; see the
[Code sub-actions](../../reference/sub-actions/code.md). C# code written for Streamer.bot (the `CPH` methods) works in
Cuebot too. [Writing code (C# and Python)](../developers/writing-code.md) walks you through it, and
[Turn an action into code](../developers/export-to-code.md) shows how Cuebot can write the script for you.
