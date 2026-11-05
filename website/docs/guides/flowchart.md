# The flowchart editor

Every action can be shown two ways: as a **list** of steps (the **Sub-actions** tab) or as a **flowchart** (the
**Flowchart** tab). They show the same action. Change one and the other changes too.

The flowchart is handy when an action makes decisions. You can *see* which steps run when the answer is yes, and
which run when it is no.

![An action as a flowchart: the trigger at the top, then the action, then its steps. The If step splits into True and False](../assets/screens/actions-flowchart.png)

## Open it

1. Go to **Automation > Actions**.
2. Click an action in the list.
3. Click the **Flowchart** tab, next to **Sub-actions**.

Tip: the flowchart likes room. Make the Cuebot window wide, or full screen with ++f11++.

## Reading the flowchart

Think of it as a map that you read from top to bottom.

![Close-up of the flowchart: Raid trigger, the Welcome raiders action, an If that splits into True and False, and a purple Celebrate group](../assets/screens/actions-flowchart-close.png)

| What you see | What it means |
| --- | --- |
| **Boxes along the top** (label **TRIGGER**, orange stripe) | The triggers: what starts the action, like a raid or a chat command. |
| **The box under them** (label **ACTION**, blue stripe) | The action itself. Its small text says how it runs, like "Runs queued". |
| **Boxes going down** | The steps (sub-actions), in the order they run. The small label says what kind of step it is, like **TWITCH** or **OBS**. Each kind has its own colour. |
| **Lines** | The order: Cuebot follows the line from one box to the next. |
| **A faded box** | A step that is switched off. It (and anything under it) doesn't run. |
| **A thick blue border** | The box you have selected. |

### Decisions: steps that split

Some steps can go more than one way. They have a labelled dot on their right side for each way. The steps of each
way hang to the right of the step, going down.

| Step | Its ways |
| --- | --- |
| **If** | **True** and **False**: yes and no. |
| **Switch** | One way for each case you type, plus **Default** for "none of them". |
| **While** | **Body**: the steps it repeats. |
| **File exists** | **True** and **False**. |
| **User in group** | **True** and **False**. |
| Steps that spend or check points | One way for "it worked", one for "not enough points". |

When the longest way finishes, the action carries on with the box under the decision.

In the picture above, the raid action asks **If %viewers% greater than 10**. On **True** it switches the OBS scene
and writes a big welcome. On **False** it only says thanks. Then both carry on with the **Celebrate** group.

### Groups: a frame around steps

A **Group** step draws a coloured frame around itself and the steps inside it. Its label says **GROUP**, or
**GROUP · ONE AT RANDOM** when only one of its steps runs, picked at random. Change the frame's colour with the
group's **Colour** setting. Groups can sit inside other groups.

### Steps at the same time, or one at random

Under **More**, an action can run **all its steps at the same time**, or **only one step, picked at random**. The
flowchart still draws them as one line going down. Only the small text in the **ACTION** box tells you, like
"· steps in parallel" or "· one random step". For a random action, each step's box also shows its **weight**: its
chance of being picked.

## Changing the action

### Add a step

Every box has a round **+** dot under it. **Click a +** to add something there. A search box opens: type what you
want, like `obs scene`, pick it with the arrow keys or the mouse, and click **Add** (or press ++enter++).

| Click the + … | Adds … |
| --- | --- |
| under a step | a step right after that step |
| under the **ACTION** box | a step at the very start |
| on a decision's way (like **True**) | a step at the start of that way |
| under a trigger, or the **trigger** dot next to the ACTION box | another trigger |

![The search box that opens from a +](../assets/screens/actions-sub-action-picker.png)

### Move a step to another place

Drag a box and drop it on a **+** dot somewhere else. The dot turns **green** when you can drop it there. The step
moves to that place in the action, together with any steps inside it. So you can drag a step from **False** into
**True**, or out of a group.

You can't drop a step inside itself. Triggers and the ACTION box can be moved around the picture, but not to another
place in the action.

!!! note "No free wiring"
    You can't draw a line from one dot to another. The lines always follow the order of the steps. To change the
    order, move the step.

### Tidy up the picture

- **Drag a box** a little to move it on the screen. Cuebot remembers where you put it. This only changes the
  picture, not what the action does.
- **Auto-arrange** (top right) forgets every position and lays the flowchart out neatly again.

### Change a step's settings

A step's settings are on the **Sub-actions** tab. Click the box on the flowchart, then click **Sub-actions**: the
same step is already selected, with its settings under the list.

![The same step selected on the Sub-actions tab, with its settings below](../assets/screens/actions-step-details.png)

Triggers are easier: click a trigger box and its settings show in the **Triggers** column. (On a narrow window, the
column becomes a **Triggers** tab.)

## Moving around

| To … | Do this |
| --- | --- |
| Look at another part | Drag the empty background (that also unselects the box). |
| Zoom in or out | Turn the mouse wheel. It zooms around the mouse pointer. |
| See the whole action again | Click the background, then press ++home++. |

## Right-click and keys

Right-click a box for **Copy**, **Paste**, **Duplicate**, **Delete**, **Copy whole action**, **Undo** and **Redo**.

After you click inside the flowchart, these keys work:

| Key | What it does |
| --- | --- |
| ++delete++ or ++backspace++ | Delete the selected step or trigger, without asking. ++ctrl+z++ brings it back. |
| ++ctrl+c++, ++ctrl+v++, ++ctrl+d++ | Copy, paste (after the selected step) and duplicate. |
| ++ctrl+z++, ++ctrl+y++ | Undo and redo. Moving boxes and **Auto-arrange** can be undone too. |
| ++ctrl+o++ | Switch the selected step on or off. |
| ++ctrl+up++, ++ctrl+down++ | Move the selected step up or down. |
| ++ctrl+home++, ++ctrl+end++ | Move the selected step to the top or the bottom. |
| ++home++ | Fit the whole action on the screen. |
| ++f5++ | **Test trigger…**: try the action with pretend values. |

## List or flowchart?

Use whichever you like: it's the same action.

- The **list** is quickest for short actions, and it's where you change a step's settings.
- The **flowchart** is best for actions with decisions, groups and many ways, because you can see the whole thing
  at once.

New to actions? Start with [How actions work](actions-and-triggers.md).
