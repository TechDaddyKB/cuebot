# IFTTT

IFTTT ("If This Then That") connects hundreds of apps and smart-home devices. Cuebot can tell IFTTT that something
happened, and your IFTTT applet then does the rest. Example: someone raids you, Cuebot tells IFTTT `stream_raid`, and
IFTTT flashes your smart lights or sends a notification to your phone.

This page is about Cuebot **sending** to IFTTT. To let IFTTT start a Cuebot action, use a
[custom webhook](custom-webhooks.md#ifttt) instead.

## What you need

- An IFTTT account. IFTTT may need a paid plan for its **Webhooks** service.
- Your **IFTTT Webhooks key**: a secret code that lets Cuebot start your applets. No relay is needed: Cuebot calls
  IFTTT directly.

## Set it up

1. On ifttt.com, open [ifttt.com/maker_webhooks](https://ifttt.com/maker_webhooks) and click **Documentation**. Copy
   the key shown at the top ("Your key is: ...").
2. In IFTTT, create an applet. For "If This", pick **Webhooks** and then:
    - **Receive a web request** for the basic step, or
    - **Receive a web request with a JSON payload** for the advanced step.
3. Give it an **Event name**, like `stream_alert`. Pick what happens in "Then That".
4. In Cuebot, make an action and add the sub-action **IFTTT > Notification (basic)** (or **(advanced)**).
5. Type the same **Event name**, and paste your key into **IFTTT Webhooks key**.
6. Try the action with **Test trigger…** (++f5++) on the Actions page. Your applet runs a moment later.

!!! note "Type the key into each step"
    The steps say "Leave empty to use the key already saved in Cuebot", but there is no box on any page to save it
    yet. So paste the key into every IFTTT step. It is saved inside the action, so remove it before you share an
    exported action with someone.

## The two steps

| Sub-action | What IFTTT gets | Use it for |
| --- | --- | --- |
| **Notification (basic)** | Your **Text** as `Value1`. | One line, like `%user% just raided with %viewers% viewers!` |
| **Notification (advanced)** | Several named values. | When the applet needs more than one piece of information. |

**Notification (advanced)** takes **Values (one name=value per line)**, like:

```text
user=%user%
amount=%amount%
platform=%platform%
```

IFTTT then gets:

- the first five as `Argument1Name` / `Argument1Value` up to `Argument5Name` / `Argument5Value`;
- the first three values also as `value1`, `value2` and `value3`;
- all of them together as `ArgumentsJson` (text like `{"user":"Alice","amount":"5"}`).

In a "JSON payload" applet, IFTTT shows these as ingredients in its `JsonPayload`. Full details:
[IFTTT sub-actions](../../reference/sub-actions/ifttt.md).

## Examples

**Phone notification for tips**

1. In IFTTT, make an applet: **Webhooks > Receive a web request** with event `tip`, then **Notifications > Send a
   notification from the IFTTT app** with the text `{{Value1}}`.
2. In Cuebot, make an action called "Tip to phone" with the trigger **Activity (any platform)** (Kind **Tip**).
3. Add the sub-action **IFTTT > Notification (basic)** with **Event name** `tip` and **Text**
   `%user% tipped %amount% on %platform%`.

**Lights on a raid**

1. In IFTTT, make an applet: **Webhooks > Receive a web request** with event `stream_raid`, then your smart-light
   service's "flash" or "set colour" action.
2. In Cuebot, make an action with your platform's raid trigger and the sub-action **IFTTT > Notification (basic)** with
   **Event name** `stream_raid`.

## Troubleshooting

When a step fails, the action's run shows why in [Action history](../actions/history.md).

| You see | What to do |
| --- | --- |
| "An IFTTT Webhooks key is required (ifttt.com/maker_webhooks → Documentation)." | Paste your key into **IFTTT Webhooks key**. |
| "Event name is required." | Type the event name from your applet. |
| "IFTTT refused the Webhooks key." | The key is wrong, or you made a new one on IFTTT. Copy it again. |
| "IFTTT refused the notification: HTTP ..." | IFTTT said no for another reason. Check the event name and your IFTTT plan. |
| "Could not reach IFTTT: ..." | No internet, or IFTTT is down. Try again later. |
| The step works but the applet does nothing | The event name must match exactly. Use **Receive a web request with a JSON payload** for the advanced step, and the plain **Receive a web request** for the basic one. |

!!! warning "Not checked yet"
    The IFTTT steps are checked by Cuebot's own tests only. Sending to a real IFTTT applet has not been tried yet. See
    [What's experimental](../help/experimental.md).

## How it works (for the curious)

The basic step sends `POST https://maker.ifttt.com/trigger/<event>/with/key/<key>` with `{"value1": "<text>"}`. The
advanced step sends to `.../trigger/<event>/json/with/key/<key>` with the values above. Cuebot hides the key in its
logs.
