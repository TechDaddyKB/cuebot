# Overlays and alerts

An **overlay** is a see-through picture on top of your stream: your chat box, a follower alert, a goal bar. In Cuebot,
an overlay is a little web page that Cuebot shows on your own computer. OBS shows that page on stream with a
**browser source** (an OBS source that shows a web page).

Each overlay holds **widgets**: the separate pieces, like the chat box or the alert box.

Everything is on **Overlays > Overlays & Widgets**.

## Make your first overlay

1. Go to **Overlays > Overlays & Widgets**.
2. Type a name in **New overlay name**, like `Main`.
3. Pick the canvas size. Choose the same size as your stream, usually **1080p (1920 × 1080)**. Streaming in
   portrait for phones? Pick **Vertical (1080 × 1920)**.
4. Click **+ Overlay**.

## Put it in OBS

1. On your overlay's row, click **Copy link**.
2. In OBS, in your scene, click **+** under **Sources** and choose **Browser**. Name it `Cuebot Main`.
3. Paste the link into **URL**.
4. Set **Width** and **Height** to the overlay's size, for example `1920` and `1080`.
5. Click **OK**.

That's it. Anything you add to the overlay in Cuebot now shows up in OBS.

!!! warning "Keep the link private"
    The link has a secret part at the end. Do not show it on stream. If it ever leaks, click **New link** and paste the
    new link into OBS.

!!! note "Nothing shows up?"
    The page needs the **Overlay server** to be running. If it is off, the Overlays page tells you. Turn it on at
    **Apps > Servers**, card **Overlay server**, then click **Apply and restart servers**.

## Add widgets

On your overlay's row, open **Add Cuebot widget** and pick one:

| Widget | What it shows |
| --- | --- |
| **Chat box** | Your chat, from every connected platform |
| **Alert box** | Pop-up alerts for follows, subs, cheers, raids, tips and more |
| **Label** | A line of text, like "Latest follower: Bob" |
| **Goal bar** | A bar that fills up, like "50 / 100 followers" |
| **Event list** | The latest follows, subs, tips and more |
| **Credits roll** | End-of-stream credits that scroll |
| **Countdown** | Counts down minutes, or to a time of day |
| **Emote rain** | Chat emotes fly across the screen |
| **Hype cup** | Follows, subs, bits and tips fill a cup with balls |
| **Hype boss** | Support hits a "boss"; the final hit makes that viewer the next boss |
| **Poll and prediction** | Live results of a poll |
| **Giveaway** | Your giveaway and its winner |
| **Media request player** | Videos viewers asked for (see **Audio > Media Requests**) |
| **Charity campaign** | Progress of a charity drive |
| **Product showcase** | Your merch, and merch purchases |
| **Seasonal effects** | Snow, leaves, hearts, confetti and more |

Each widget appears under the overlay, with **X**, **Y**, **W** and **H** boxes (its position and size, in pixels),
plus **Edit…** to change its settings and **Remove**.

Already have widgets from StreamElements or Streamlabs? **Add widget…** imports their `.zip` files. **Import from
StreamElements…** copies an overlay from your StreamElements account (your StreamElements overlays stay as they are).

## The designer

Click **Design…** to open the overlay in your web browser, where you can drag things around with the mouse.

- **+ Add** puts in text, images, videos, a slideshow or any widget.
- The **Layers** panel lists everything; the **Properties** panel changes the selected thing.
- Undo, redo, zoom, bring forward / send backward, group and delete are in the toolbar.
- Changes save on their own.

## Alerts

The **Alert box** widget shows alerts by itself. You do not need an action for that. It has a design for each
alert type: **Follow**, **Subscriber**, **Tip / donation**, **Cheer (bits)**, **Raid**, **Super Chat**, **Kicks**,
**Channel points**, **YouTube Jewels**, **Charity donation**, **Merch** and **Custom (actions)**. Each type can have
**variations**, for example a bigger alert for 1000 bits.

**Try an alert:** open the alert box's editor and click **Test this design**. Test events only go to that preview,
never to your live overlays.

**Pause, skip, mute:** the **Alerts** card on the Overlays page has **Pause alerts**, **Skip alert** and **Mute
alerts**. They work for every overlay at once.

### Alerts from your own actions

Want an alert for something Cuebot has no alert for, like a `!hype` command? Add the **Show alert** sub-action to an
action:

- **Design**: which alert design to use, for example `custom`.
- **Name**: who it is about. It starts as `%user%`.
- **Message**, **Amount**, **Currency**: what the alert says.

Your alert waits its turn behind other alerts, just like a real one. The **Pause, skip or mute overlay alerts** sub-action can pause, skip or
mute alerts from an action, for example while you are on your "BRB" scene.

## Sharing overlays

- **Share > Export package…** saves an overlay as a `.cuebot-overlay` file for a friend.
- **Import package…** loads one.
- **Browse gallery…** shows overlays and widgets that other streamers shared.

## Chat polls on stream

The **Chat poll and prediction (every platform)** part of the page runs a poll in chat on every platform at once. Viewers
vote with `!vote 1`. Show it with the **Poll and prediction** widget. See
[Points, store and giveaways](community.md#polls-and-predictions).
