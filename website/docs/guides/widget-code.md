# Writing your own overlay widgets

A **widget** is one piece of an overlay, like a chat box or a goal bar. Cuebot comes with many widgets (see
[Overlays and alerts](overlays.md#add-widgets)). You can also make your own, with a little HTML, CSS and JavaScript.

This page is for you if you know a little web code. You do not need to be a programmer. Every idea comes with a short
example you can copy.

Cuebot widgets work like **StreamElements custom widgets**. A widget made for StreamElements usually works in Cuebot
without changes. Cuebot also runs **Streamlabs** custom widget code.

!!! tip "New to overlays?"
    Read [Overlays and alerts](overlays.md) first. It shows how to make an overlay and put it in OBS.

## What a widget is made of

A widget has up to six parts. Cuebot calls them **panes**.

| Pane | What goes in it |
| --- | --- |
| **HTML** | What is on the screen: boxes, text, pictures. |
| **CSS** | How it looks: colours, sizes, fonts. |
| **JS** | What it does: the JavaScript that reacts to follows, chat and so on. |
| **Fields** | The list of settings, like "Text colour". Written as JSON (a text format with `{ }` and `"quotes"`). |
| **Data** | The values you picked for those settings. Cuebot fills this in for you. |
| **My CSS** | Your own extra CSS. It is applied after the widget's CSS. |

Each widget runs inside its own little web page. Cuebot puts that page inside your overlay.

## Make a new widget

1. In Cuebot, go to **Overlays > Overlays & Widgets**.
2. On your overlay's row, open **Add Cuebot widget** and pick **Blank custom widget (write your own code)**.
3. A widget called **My widget** shows up under the overlay, in the top left. Click **Edit…** on its row.

The new widget is not empty: it starts as the [latest follower example](#a-complete-example-latest-follower), so you
can see how the parts fit together. Change or delete anything you like, then click **Save**.

Move and size the widget with its **X**, **Y**, **W** and **H** boxes, or drag it in the
[designer](overlays.md#the-designer).

### Or add your files as a zip

Already wrote the files somewhere else? Add them as a `.zip` file instead.

1. Put these files in a folder: `widget.html`, `widget.css`, `widget.js` and `fields.json`.
2. Put the four files in a `.zip` file. In Windows: select them, right-click, **Send to > Compressed (zipped) folder**.
3. On your overlay's row, click **Add widget…** and pick your `.zip` file.

A widget added from a zip covers the whole canvas at first. Change its **X**, **Y**, **W** and **H** boxes to fit it.

!!! note "Other file names work too"
    Cuebot also reads `html.txt`, `css.txt`, `js.txt`, `fields.txt` and `data.txt` (the StreamElements "widget.io"
    format, with its `widget.ini`). The `.txt` files may also end in `.html`, `.css`, `.js` or `.json`. The files can
    be in the top of the zip or in one folder inside it. A zip needs at least the HTML or the JS.

!!! tip "Let the assistant do it"
    The [setup assistant](assistant.md) can add a custom widget from code you give it.

## The widget editor

The widget editor is where you type and change the code.

- On the Overlays page, click **Edit…** on the widget's row.
- Or, in the designer, select the widget and click **Edit widget…**.

The editor opens in your web browser. Its address starts with `http://127.0.0.1:7476/edit/`. That address has a secret
part, so only you can open it. Your OBS links cannot open the editor.

On the left are the tabs. On the right is a **preview** of your widget.

| Tab | What it does |
| --- | --- |
| **Settings** | A form made from your Fields. Change a setting and the preview updates. |
| **HTML**, **CSS**, **JS** | Type your code here. The Tab key adds two spaces. |
| **Fields** | Your settings list, as JSON. |
| **Data** | The values from the Settings tab, as JSON. |
| **My CSS** | Your own CSS, applied last. |

- The preview uses your changes even before you save. It updates half a second after you stop typing.
- Click **Save** (or press ++ctrl+s++) to keep your changes. Every overlay that uses the widget reloads by itself,
  also in OBS.
- **Reset** (next to the Emulate buttons) starts the preview again.

!!! warning "Fields and Data must be correct JSON"
    The editor will not let you leave the Fields or Data tab while the JSON there is broken. Fix the red message first.
    A missing comma or quote is the usual reason.

### My CSS: restyle a widget without touching its code

**My CSS** is for your own changes to someone else's widget. For example, a widget you bought. Your CSS is added after
the widget's CSS, so your rules win.

```css
/* Make all text bigger and pink */
body { font-size: 28px; color: hotpink; }
```

Chat messages tell you which platform they came from (`twitch`, `kick`, `rumble` or `youtube`). That is handy if the
widget writes it into the page, for example as a class name.

## Settings: the Fields pane

Fields turn into settings in the Settings tab. Then you (or someone you share the widget with) can change the look
without touching the code.

Fields is one JSON object. Each **key** is the setting's name (use letters, numbers and `_`). Each value says what kind
of setting it is.

```json
{
  "title":     { "type": "text",        "label": "Title",        "value": "Latest follower:" },
  "textColor": { "type": "colorpicker", "label": "Text colour",  "value": "#ffffff", "group": "Look" },
  "size":      { "type": "slider",      "label": "Text size",    "value": 40, "min": 10, "max": 120, "step": 1, "group": "Look" }
}
```

- **type**: what kind of setting it is (see the table).
- **label**: the name people see in the Settings tab.
- **value**: the starting value.
- **group**: puts settings together under a heading. Without it, the heading is "Settings".

### Every field type

| Type | What you get | What it saves |
| --- | --- | --- |
| `text` | A one-line text box. | Text |
| `textarea` | A big text box for many lines. | Text |
| `number` | A number box. Add `min`, `max` and `step` if you like. | A number |
| `slider` | A slider and a number box. Add `min`, `max` (0 and 100 if left out) and `step`. | A number |
| `checkbox` | A tick box. Give it `"value": true` or `false`. | `true` or `false` |
| `colorpicker` | A colour picker and a text box. The text box also takes colours like `rgba(0, 0, 0, 0.5)`. | Text, like `#ff0000` |
| `dropdown` | A list to pick from. Add `"options": { "left": "Left", "right": "Right" }`. | The key, like `left` |
| `image-input` | An address box and an **Upload…** button for a picture. | An address |
| `video-input` | The same, for a video. | An address |
| `sound-input` or `audio-input` | The same, for a sound. | An address |
| `googleFont` or `fontpicker` | A text box for a Google Fonts name, like `Poppins`. | Text |
| `button` | A button. It sends your widget a test event (see [Buttons](#buttons-in-the-settings-tab)). | Nothing |
| `hidden` | Not shown. Your code can still read its value. | Its `value` |

Any other type shows up as a plain text box.

- For `image-input`, `video-input`, `sound-input` and `audio-input`, add `"multiple": true` to get a list (one
  address per line). The value is then a list.
- A `googleFont` field only stores the name. Your widget must load the font itself, for example with this line in the
  HTML: `<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family={{font}}&display=swap">`

### Using a setting in your code

There are two ways.

**1. Placeholders.** Write the setting's key in braces: `{{title}}` or `{title}`. Cuebot swaps it for the value before
the widget starts. This works in the HTML, CSS, JS and My CSS panes.

```css
#box { color: {{textColor}}; font-size: {{size}}px; }
```

- `{{ title }}` may have spaces inside. `{title}` may not.
- Only keys that really are settings get swapped. Other braces stay as they are.
- Text goes in exactly as typed. Numbers and `true`/`false` go in as they are. Lists go in as JSON.

!!! tip "Text from a setting: use JavaScript"
    A placeholder puts the text in as it is, so a `<` or `"` in a setting can break your HTML or JS. For text that
    people type, read it in JavaScript from `fieldData` (see below) and use `textContent`.

**2. fieldData in JavaScript.** When the widget starts, it gets every setting in `obj.detail.fieldData`.

```js
window.addEventListener("onWidgetLoad", function (obj) {
  var settings = obj.detail.fieldData;
  document.getElementById("title").textContent = settings.title;
});
```

`fieldData` holds each field's `value`, replaced by the saved Data value when there is one.

## Pictures, videos, sounds and fonts

The **Upload…** button of a picture, video or sound setting copies the file into Cuebot. The setting then holds an
address like `/assets/3f9a…c2.png`. That address works inside your widget.

```html
<img id="logo" src="{{logo}}">
```

- Files can be up to 200 MB.
- Allowed types: `png`, `jpg`, `jpeg`, `gif`, `webp`, `avif`, `svg`, `apng`, `mp4`, `webm`, `mov`, `mp3`, `ogg`,
  `oga`, `wav`, `m4a`, `flac`, `opus`, `woff`, `woff2`, `ttf`, `otf`.
- The same file uploaded twice is kept once.
- You can also type any `https://` address into the box.

When you share a widget, every `/assets/…` file named in its panes or settings goes with it.

## How your code gets events

Your JavaScript listens for **window events**, the same ones StreamElements uses. jQuery 3.7.1 is already loaded, so
`$` works too.

| Event | When it comes | What `obj.detail` holds |
| --- | --- | --- |
| `onWidgetLoad` | Once, when the widget starts. | Settings, stream totals, recent alerts and more. |
| `onEventReceived` | For every chat message, alert and other event. | `{ listener, event }` |
| `onSessionUpdate` | When the stream totals change. | `{ session }` |

```js
window.addEventListener("onEventReceived", function (obj) {
  var listener = obj.detail.listener;   // what kind of event, like "follower-latest"
  var event = obj.detail.event;         // its data
  if (listener === "follower-latest") {
    console.log(event.name + " followed!");
  }
});
```

!!! note "Your widget starts when Cuebot answers"
    `onWidgetLoad` comes once the overlay has reached Cuebot. If Cuebot is closed, your widget waits.

### onWidgetLoad

```js
{
  channel: { username: "", apiToken: "", id: "", providerId: "", avatar: "" },  // always empty in Cuebot
  currency: { code: "USD", name: "U.S. Dollar", symbol: "$" },                  // always this
  fieldData: { /* your settings */ },
  recents: [ /* recent alerts, newest first (see below) */ ],
  session: { data: { /* stream totals */ }, settings: { autoReset: true, calendar: true, resetOnStart: false } },
  overlay: { isEditorMode: false, muted: false, paused: false },
  polls: [ /* the latest poll and prediction from each place */ ],              // Cuebot only
  giveaway: null,                                                                // Cuebot only: the giveaway, or null
  media: null,                                                                   // Cuebot only: media requests
  emulated: false                                                                // true in the editor preview
}
```

- `overlay.isEditorMode` is `true` in the editor's preview.
- `overlay.muted` and `overlay.paused` tell you if alerts are muted or paused right now.

Each item in `recents` looks like this:

```js
{ type: "follower", name: "PixelPanda", amount: 1, tier: null, message: "", currency: null,
  gifted: null, bulkGifted: null, sender: null, redemption: null, service: "twitch",
  createdAt: "2026-11-01T18:30:00.0000000Z" }
```

### Stream totals (session data)

`session.data` (in `onWidgetLoad`) and `session` (in `onSessionUpdate`) hold the same thing: Cuebot's stream totals,
counted from every platform together. The names are the alert names without `-latest`, like `follower`, `subscriber`,
`tip`, `cheer`, `raid`, `superchat`, `kicks`, `jewels`, `redemption`, `merch` and `charityCampaignDonation`.

| Key | What it holds |
| --- | --- |
| `follower-latest` | The last alert of that kind: `{ name, amount, … , service }` |
| `follower-session`, `-week`, `-month`, `-total` | `{ count, amount }` for this stream, week, month, all time |
| `follower-count` | `{ count }`, all time |
| `follower-goal` | `{ amount }` |
| `follower-recent` | The last 25, newest first |
| `follower-session-users` | Everyone this stream, with their amount added up |
| `tip-session-top-donation` (also `weekly`, `monthly`, `alltime`) | The biggest single one: `{ name, amount }`. Only for money-like kinds. |
| `tip-session-top-donator` (also `weekly`, `monthly`, `alltime`) | Who gave the most, added up. Only for money-like kinds. |
| `subscriber-new-session`, `subscriber-resub-session`, `subscriber-gifted-session` | `{ count }` |

A kind only shows up after Cuebot has seen at least one of it. So check before you use it:

```js
var latest = data["follower-latest"];
if (latest && latest.name) { /* there is a latest follower */ }
```

!!! note "Two different shapes"
    In `onWidgetLoad` the totals are in `obj.detail.session.data`. In `onSessionUpdate` they are in
    `obj.detail.session`. That is how StreamElements does it too.

### Every event your widget can get

These are the `listener` names that come with `onEventReceived`.

| Listener | What it is | Waits in the alert queue? |
| --- | --- | --- |
| `message` | A chat message from any platform. | No |
| `delete-message` | A chat message was deleted: `{ msgId }` | No |
| `delete-messages` | A viewer was banned or timed out: `{ userId }` | No |
| `follower-latest` | A follow (Twitch, Kick, Rumble) or a new YouTube subscriber. | Yes |
| `subscriber-latest` | A sub, resub, gift sub or membership. | Yes |
| `cheer-latest` | Twitch bits. | Yes |
| `raid-latest` | A Twitch raid or a Kick host. | Yes |
| `tip-latest` | A tip (donation services, Rumble rants). | Yes |
| `superchat-latest` | A YouTube Super Chat or Super Sticker. | Yes |
| `kicks-latest` | Kick Kicks. | Yes |
| `jewels-latest` | YouTube Jewels (Cuebot only). | Yes |
| `redemption-latest` | Channel points, or something bought in Cuebot's points store (Cuebot only). | Yes |
| `charityCampaignDonation-latest` | A charity donation. | Yes |
| `merch-latest` | Merch bought. | Yes |
| `cuebot-alert` | An alert from the **Show alert** sub-action (see [below](#send-your-own-data-to-a-widget)). | Yes |
| `event` | The raw form of each alert above: `{ type, provider, data }`. It comes right after the alert. | No |
| `kvstore:update` | A widget saved something with `SE_API.store.set`. | No |
| `bot:counter` | A number global variable changed: `{ counter, value }` | No |
| `event:skip` | **Skip alert** was used. End the alert you are showing. | No |
| `alertService:toggleSound` | Alerts were muted or unmuted: `{ muted }` | No |
| `event:test` | A [button](#buttons-in-the-settings-tab) in the editor's Settings tab was clicked. | No |
| `cuebot-poll` | A poll or prediction changed (Cuebot only). | No |
| `cuebot-giveaway` | A giveaway changed (Cuebot only). | No |
| `cuebot-media` | The media request list changed (Cuebot only). | No |
| `cuebot-charity` | A charity campaign started, moved or ended (Cuebot only). | No |

#### A chat message (`message`)

```js
{
  data: {
    time: 1730485800000,
    tags: { "badge-info": "", badges: "moderator/1", color: "#9146FF", "display-name": "PixelPanda", emotes: "",
            "first-msg": "0", id: "…", mod: "1", subscriber: "1", vip: "0", broadcaster: "0", turbo: "0",
            "tmi-sent-ts": "1730485800000", "user-id": "…", "user-type": "mod" },
    nick: "pixelpanda",
    userId: "…",
    displayName: "PixelPanda",
    displayColor: "#9146FF",
    channel: "",
    text: "Hello from Twitch Kappa",
    isAction: false,
    msgId: "…",
    badges: [ { type: "moderator", version: "1", url: "…", description: "…" } ],
    emotes: [ { type: "twitch", name: "Kappa", id: "25", gif: false, urls: { 1: "…", 2: "…", 4: "…" }, start: 18, end: 22 } ],
    avatar: "…",       // Cuebot only, when the platform gives a picture
    pronouns: ""       // Cuebot only, Twitch chatters with pronouns set
  },
  renderedText: "Hello from Twitch <img src=\"…\" title=\"Kappa\" alt=\"Kappa\" class=\"emote\">",
  service: "twitch"    // twitch, kick, rumble or youtube
}
```

- The `tags` are filled for **every** platform, not only Twitch. So `tags.mod === "1"` works for Kick and YouTube too.
- `renderedText` is the message as safe HTML, with emotes as pictures.
- A chatter without a name colour gets one of Twitch's usual colours. The same chatter always gets the same one.

#### An alert (`follower-latest`, `tip-latest` and the rest)

```js
{
  name: "PixelPanda", displayName: "PixelPanda",
  amount: 1,              // bits, viewers, months, money...
  message: "",
  type: "follower",
  providerId: "…", avatar: "…", _id: "…",
  createdAt: "2026-11-01T18:30:00.0000000Z",
  service: "twitch"       // where it came from
}
```

Some kinds add more: subs have `tier` (`"1000"`, `"2000"`, `"3000"` or `"prime"`), `gifted`, `sender`, `bulkGifted`
and `isCommunityGift`. Money has `currency`. Channel points have `redemption` (the reward's name). Merch has `items`.

### The alert queue and widgetDuration

Alerts wait in a line, one after another. This is the **alert queue**. It stops alerts from showing on top of each
other.

- Your widget gets alerts one at a time.
- Add a field called `widgetDuration` (in seconds) to wait between alerts. With `8`, the next alert comes 8 seconds
  after the last one.
- Your widget can say "I'm done" early with `SE_API.resumeQueue()`. The next alert then comes right away.
- Without `widgetDuration` (or with `0`), alerts come straight away, one after another.
- Chat and the other "No" events in the table never wait.

```json
{
  "widgetDuration": { "type": "hidden", "value": 8 }
}
```

With `"type": "hidden"` you set the wait in Fields only. To let people change it on the Settings tab, make it a setting
instead: `{ "type": "slider", "label": "Seconds between alerts", "value": 8, "min": 0, "max": 60, "step": 1 }`.

```js
window.addEventListener("onEventReceived", function (obj) {
  if (obj.detail.listener !== "follower-latest") {
    SE_API.resumeQueue();   // not an alert this widget shows, so let the next one through
    return;
  }
  showAlert(obj.detail.event.name);   // your own function
});
```

The Pause, Skip and Mute buttons (on the Overlays page, or the **Pause, skip or mute overlay alerts** sub-action) work
on your widget too:

- **Pause**: the alert showing now finishes. New alerts wait until you resume.
- **Skip**: your widget gets `event:skip` to end the alert it is showing. Then the next alert comes.
- **Mute**: your widget gets `alertService:toggleSound` with `{ muted: true }`. Your widget must turn its own sound
  down. Cuebot cannot do that for you.

!!! warning "Paused alerts wait for every widget"
    While alerts are paused, alert events wait for your widget too, even if it is just a label. Stream totals
    (`onSessionUpdate`) still come. So a label that reads the totals keeps working.

### Buttons in the Settings tab

A field with `"type": "button"` shows a button in the editor. Clicking it sends your widget an `event:test`. This only
happens in the editor's preview.

```json
{ "testButton": { "type": "button", "label": "Show a test", "value": "hello" } }
```

```js
window.addEventListener("onEventReceived", function (obj) {
  var e = obj.detail.event;
  if (obj.detail.listener === "event:test" && e.listener === "widget-button" && e.field === "testButton") {
    console.log("Button clicked, value: " + e.value);
  }
});
```

## SE_API: asking Cuebot for things

`SE_API` is ready to use in every widget. Most of its parts give back a **promise**: an answer that comes a little
later. Use `.then(...)` to get it.

| Call | What it does in Cuebot |
| --- | --- |
| `SE_API.store.set(key, value)` | Saves a value under a name. It is kept when Cuebot restarts. |
| `SE_API.store.get(key)` | Gives back the saved value, or `null`. |
| `SE_API.counters.get(name)` | Gives back `{ counter, value }` for a global variable. |
| `SE_API.resumeQueue()` | Lets the next alert through now (see [the alert queue](#the-alert-queue-and-widgetduration)). |
| `SE_API.getOverlayStatus()` | Gives back `{ isEditorMode, muted }`. |
| `SE_API.cheerFilter(text)` | Gives back the text without Twitch cheer words like `Cheer100`. |
| `SE_API.sanitize({ message })` | Gives back `{ skip: false, result: { message } }`. The message comes back unchanged. |
| `SE_API.setField(...)` | Does nothing in Cuebot. Save settings in the editor instead. |
| `SE_API.sendMessage(...)` | Only works for the calls above. Anything else is refused. |

### Saving values (store)

```js
// Save the top score
SE_API.store.set("topScore", { name: "PixelPanda", score: 42 });

// Read it back
SE_API.store.get("topScore").then(function (value) {
  if (value) console.log(value.name + " has " + value.score);
});
```

- **Every** widget shares the same store. Pick names that will not clash, like `"myQuiz.topScore"`.
- Each save sends a `kvstore:update` event to every widget on every overlay:
  `{ data: { key: "customWidget.topScore", value: … } }`. Note the `customWidget.` in front.
- Limits: names up to 200 characters, values up to 64 KB, at most 1000 names.
- A save that breaks a limit is refused (the promise fails).

!!! warning "The preview saves for real"
    `SE_API.store.set` in the editor's preview writes to the real store. Your live overlays get the
    `kvstore:update` too.

### Counters (global variables)

StreamElements has "bot counters". In Cuebot, these are your [global variables](variables.md#global-variables).

```js
SE_API.counters.get("deaths").then(function (c) {
  document.getElementById("deaths").textContent = c.value;
});
```

- Only **numbers** work. A global that holds text (or does not exist) gives `0`.
- Each time a number global changes, every widget gets a `bot:counter` event: `{ counter: "deaths", value: 5 }`.

## Send your own data to a widget

You can make a widget react to your own [actions](actions-and-triggers.md). There are two ways.

### 1. The Show alert sub-action

The **Show alert** sub-action (in the **Overlays** group) sends a `cuebot-alert` event to every overlay. Your widget
can listen for it.

```js
{
  alertType: "custom",   // the sub-action's Design
  type: "custom",
  name: "PixelPanda", displayName: "PixelPanda",   // its Name
  message: "Hype!",      // its Message
  amount: 1,             // its Amount
  currency: "",          // its Currency, in capitals
  service: "twitch",     // its Platform, in small letters
  _id: "…",
  createdAt: "…"
}
```

```js
window.addEventListener("onEventReceived", function (obj) {
  if (obj.detail.listener === "cuebot-alert" && obj.detail.event.alertType === "custom") {
    document.getElementById("shout").textContent = obj.detail.event.message;
  }
});
```

- It waits in the alert queue like a real alert.
- A `%variable%` that has no value is sent as empty.
- Cuebot's own alert box shows it too (with its **Custom (actions)** design). See
  [Alerts from your own actions](overlays.md#alerts-from-your-own-actions).

### 2. A number global variable

Use the **Set global variable** sub-action. Your widget gets a `bot:counter` event each time the number changes.

- Use the **Increment** or **Decrement** mode, or tick **Auto type**. Otherwise the value is saved as text, and
  widgets do not see text.

```js
window.addEventListener("onEventReceived", function (obj) {
  if (obj.detail.listener === "bot:counter" && obj.detail.event.counter === "deaths") {
    document.getElementById("deaths").textContent = obj.detail.event.value;
  }
});
```

!!! note "What actions cannot do"
    Cuebot has no sub-action that sends any data you like straight to a widget, and actions cannot write to
    `SE_API.store`. Use one of the two ways above.

## Test your widget

### The Emulate buttons

The editor's preview has **Emulate** buttons. They send a made-up event, with a made-up viewer, to the preview only.
Your live overlays in OBS never see them.

1. Pick a platform in the **Emulate** list.
2. Click a button.

| Platform | Buttons |
| --- | --- |
| Twitch | Chat, Follow, Sub, Resub, Gift subs, Cheer, Raid |
| Kick | Chat, Follow, Sub, Resub, Gift subs, Kicks, Raid |
| Rumble | Chat, Follow, Sub, Gift subs, Tip |
| YouTube | Chat, Follow, Sub, Resub, Gift subs, Super Chat |
| Donations | Tip |

Emulated events go through the same steps as real ones, so they look exactly like the real thing.

- An emulated event does not change the stream totals. `onSessionUpdate` does not come.
- The preview starts with your real stream totals and recent alerts.

### Testing in OBS

To see a test event on your real overlays, ask the [setup assistant](assistant.md) to "send a sample follow to my
overlays". It sends it to every overlay, and nothing else happens.

### Finding mistakes

Errors from your code show in the browser's **developer tools** (press ++f12++ in the editor's browser tab, then open
**Console**). `console.log("hello")` shows up there too.

## Limits: what a widget cannot do

Your widget runs in its own little page, on a second port: the **widget port** (7477, next to the overlay server's
7476). It is like a separate room with a locked door.

- A widget **cannot** reach Cuebot itself, its settings or the overlay's secret link. It can only talk to Cuebot
  through `SE_API`.
- A widget can load files from the internet, like Google Fonts or pictures.
- A widget cannot open pop-up windows, send forms or move the overlay page to another address.
- All panes together can be up to 5 MB.
- `channel` in `onWidgetLoad` is always empty, and `currency` is always US dollars.
- `SE_API.setField` does nothing, and `SE_API.sanitize` does not filter words yet.
- If an overlay cannot keep up (for example OBS was frozen), Cuebot may drop old chat messages and old totals updates
  for it. Alerts are kept.

!!! note "window.Cuebot is not for widgets"
    The overlay page itself has a `window.Cuebot` object and sends a `cuebot:message` window event for every message
    from Cuebot. These live in the overlay page, outside your widget's frame, so your widget code cannot use them.
    Use the events and `SE_API` above instead.

## Streamlabs widgets

Cuebot also runs Streamlabs custom code. It spots Streamlabs code by itself when you import a zip or paste code into the
editor and save:

| If the code has… | Cuebot treats it as… |
| --- | --- |
| `chatlist_item` in the HTML | A chat box |
| `eventlist_item` in the HTML | An event list |
| `goalLoad` or `goalEvent` in the JS | A goal |
| `alert-wrap` in the HTML | An alert box |
| `document.addEventListener("onLoad"…)` or `("onEventReceived"…)` | A plain widget |

Code that uses `onWidgetLoad` is StreamElements code, and Cuebot leaves it alone.

What Cuebot does for Streamlabs code:

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

Streamlabs alert events look like this: `{ type, from, name, message, amount, formattedAmount, currency, tag, isTest,
createdAt, … }`. The `type` is `follow`, `subscription`, `donation`, `bits`, `raid`, `superchat`, `merch`,
`twitchcharitydonation`, `redemption`, `kicks` or `jewels`.

!!! note "Not supported"
    Streamlabs' boss battle and spin wheel events are not sent.

## Sharing your widget

### Export it

1. On the Overlays page, find your widget's row.
2. Open **Share** and choose **Export with its code (I own the rights to share it)…**.
3. Save the `.cuebot-overlay` file.

The file holds your code, your settings, your CSS and every uploaded file the widget uses.

!!! warning "Only share code that is yours"
    Cuebot asks you to say the code is yours, because it might be a widget someone sold you. Use this only for code
    you wrote, or are allowed to share. When you export a whole overlay with plain **Export package…**, custom widget
    code is left out.

### Import one

- **A `.cuebot-overlay` widget file**: click **Add widget…** on an overlay's row and pick the file.
- **A whole overlay** (`.cuebot-overlay`): use **Import package…** on the Overlays page.
- **A StreamElements or Streamlabs `.zip`**: click **Add widget…** on an overlay's row. In the designer, use
  **+ Add > StreamElements widget (.zip)…**.

You can also share in the community gallery. See [Sharing overlays](overlays.md#sharing-overlays).

## A complete example: latest follower

This widget shows "Latest follower: PixelPanda". It has settings for the title, two colours and the text size.

=== "widget.html"

    ```html
    <div id="box">
      <span id="title"></span>
      <span id="name">nobody yet</span>
    </div>
    ```

=== "widget.css"

    ```css
    #box {
      font-family: system-ui, sans-serif;
      font-size: {{size}}px;
      font-weight: 700;
      color: {{textColor}};
      text-shadow: 0 2px 6px rgba(0, 0, 0, 0.6);
      white-space: nowrap;
    }
    #name {
      color: {{nameColor}};
    }
    ```

=== "widget.js"

    ```js
    function show(name) {
      document.getElementById("name").textContent = name;
    }

    // When the widget starts: the title, and the last follower Cuebot knows
    window.addEventListener("onWidgetLoad", function (obj) {
      document.getElementById("title").textContent = obj.detail.fieldData.title;
      var latest = obj.detail.session.data["follower-latest"];
      if (latest && latest.name) show(latest.name);
    });

    // A new follow (this also makes the editor's Emulate > Follow button work)
    window.addEventListener("onEventReceived", function (obj) {
      if (obj.detail.listener === "follower-latest") show(obj.detail.event.name);
    });

    // The stream totals changed (this still comes while alerts are paused)
    window.addEventListener("onSessionUpdate", function (obj) {
      var latest = obj.detail.session["follower-latest"];
      if (latest && latest.name) show(latest.name);
    });
    ```

=== "fields.json"

    ```json
    {
      "title":     { "type": "text",        "label": "Title",       "value": "Latest follower:" },
      "textColor": { "type": "colorpicker", "label": "Title colour", "value": "#ffffff", "group": "Look" },
      "nameColor": { "type": "colorpicker", "label": "Name colour",  "value": "#ffcc33", "group": "Look" },
      "size":      { "type": "slider",      "label": "Text size",    "value": 40, "min": 12, "max": 120, "step": 1, "group": "Look" }
    }
    ```

To try it:

1. Add a **Blank custom widget** (see [Make a new widget](#make-a-new-widget)). It already has this code, and is
   700 × 80 pixels.
2. Click **Edit…**. Pick **Twitch** in **Emulate** and click **Follow**. The name changes in the preview.
3. On the **Settings** tab, change **Name colour**. The preview changes too.
4. Click **Save**. Your overlay in OBS now shows the latest follower.
