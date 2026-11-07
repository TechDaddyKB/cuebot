# Streamlabs widgets

Have a custom widget made for **Streamlabs**? Cuebot runs Streamlabs custom code too, so you can bring it along. This
page says how Cuebot spots it and what it changes. It builds on
[Writing your own overlay widgets](widget-code.md); read that first if you are new to widget code.

## How Cuebot spots Streamlabs code

Cuebot spots Streamlabs code by itself when you import a zip or paste code into the
editor and save:

| If the code has… | Cuebot treats it as… |
| --- | --- |
| `chatlist_item` in the HTML | A chat box |
| `eventlist_item` in the HTML | An event list |
| `goalLoad` or `goalEvent` in the JS | A goal |
| `alert-wrap` in the HTML | An alert box |
| `document.addEventListener("onLoad"…)` or `("onEventReceived"…)` | A plain widget |

Code that uses `onWidgetLoad` is StreamElements code, and Cuebot leaves it alone.

## What Cuebot does for Streamlabs code

- It changes Streamlabs field types to Cuebot's: `textfield` becomes `text`, `fontpicker` becomes `googleFont`, and a
  slider's `steps` becomes `step`.
- It adds the settings Streamlabs normally gives (for example **Text color** for a chat box, a message for each alert
  type in an alert box, or **Goal**, **Title** and **Target** for a goal).
- It fires `onLoad` (with the settings) and `onEventReceived` on `document`, like Streamlabs.
- **Chat box**: each message is drawn from your `#chatlist_item` template into `#log`, with `{from}`, `{messageId}`,
  `{color}` and `{message}` filled in. At most 100 messages stay.
- **Event list**: each alert is drawn from `#eventlist_item` with `{eventType}`, `{tag}`, `{from}`, `{message}` and
  `{amount}`.
- **Alert box**: each alert fills `{name}`, `{amount}`, `{count}`, `{message}`, `{userMessage}`, `{product}`, `{img}`
  and `{messageTemplate}` in your HTML, then runs your JS. The alert stays for `widgetDuration` seconds (8 at first;
  change it in the Data tab).
- **Goal**: `goalLoad` when it starts, and `goalEvent` each time the totals change.

## Streamlabs alert events

Streamlabs alert events look like this: `{ type, from, name, message, amount, formattedAmount, currency, tag, isTest,
createdAt, … }`. The `type` is `follow`, `subscription`, `donation`, `bits`, `raid`, `superchat`, `merch`,
`twitchcharitydonation`, `redemption`, `kicks` or `jewels`.

!!! note "Not supported"
    Streamlabs' boss battle and spin wheel events are not sent.

## Add one

On your overlay's row, click **Add widget…** and pick the Streamlabs `.zip`, or add a
**Blank custom widget (write your own code)** and paste the code into the editor (see
[Make a new widget](widget-code.md#make-a-new-widget)), then click **Save**.
