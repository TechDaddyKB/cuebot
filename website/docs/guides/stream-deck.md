# Stream Deck

An Elgato **Stream Deck** is a little box of buttons with screens on them. With Cuebot, a press on a Stream Deck button
can run any action: "BRB scene", "play the airhorn", "+1 death".

There are two ways to connect one. Pick **one**.

| | With the Stream Deck app | Direct USB |
| --- | --- | --- |
| **What it is** | Elgato's own Stream Deck app, plus Cuebot's plugin | Cuebot talks to the Stream Deck by itself |
| **Works on** | Windows and macOS (where the Stream Deck app runs) | Linux |
| **Good for** | Mixing Cuebot buttons with other plugins | Linux, where Elgato has no app |

Both are on **Input > Stream Deck**.

## Way 1: the Stream Deck app and Cuebot's plugin

You need the Elgato Stream Deck app, version 7.1 or newer.

### Step 1: turn on the REST API

The plugin talks to Cuebot through Cuebot's **REST API** (a door that other programs can knock on).

1. Go to **Input > Stream Deck**.
2. Under **1. Turn on the REST API**, click **Turn on the REST API**. Cuebot also makes a **token** for you: a long
   secret code, like a password, that the plugin uses to prove it is allowed in.

### Step 2: install the plugin

1. Under **2. Install the plugin**, click **Show file**. A folder opens with the file
   `io.github.cuebot.streamdeck.streamDeckPlugin`.
2. Double-click that file. The Stream Deck app installs it.

If there is no **Show file** button, the page tells you where to download the file.

### Step 3: pair the plugin with Cuebot

1. In the Stream Deck app, find the **Cuebot** group on the right and drag a Cuebot key onto a button.
2. In that key's settings, open the connection part. It needs three things: **Host**, **Port** and **API token**.
3. Copy them from Cuebot, under **3. Pair**. Each has a **Copy** button.
4. The dot turns green when it is connected. You only do this once: every Cuebot key shares it.

Cuebot lists connected Stream Deck apps under **Connected Stream Deck apps**.

### The Cuebot keys

| Key | What it does |
| --- | --- |
| **Run Cuebot action** | Runs the action you pick in **Action**. **Run on**: when you press (**Key down**) or let go (**Key up**). |
| **Toggle action enabled** | Turns an action on or off. |
| **Set global variable** | Sets a [global variable](variables.md#global-variables), adds to it, or flips it between true and false. On a dial, each turn adds **Step per tick**. |
| **Cuebot trigger** | Starts the **Stream Deck key or dial used** trigger, for actions that listen to it. |

Each key has a **Button ID**: a name you choose, like `brb`. Cuebot's actions use it to find the key.

## Way 2: direct USB (Linux)

On Linux there is no Stream Deck app, so Cuebot drives the Stream Deck itself.

1. Plug in your Stream Deck.
2. Go to **Input > Stream Deck**. On the **Direct USB (no Stream Deck app)** card, make sure **Use Stream Decks on
   USB** is ticked. (It is on from the start.)
3. Your Stream Deck shows up. Give it a **Name** if you like.
4. Open **Keys**. Each key has a row: **Key**, **Button ID**, **Title** and **Image**. Give each key you want to use a
   **Button ID**, like `brb`, and a **Title**.
5. Click **Save keys**.
6. In an action, add the **Stream Deck › Stream Deck key or dial used** trigger, and type that **Button ID**.

Works with Stream Deck XL, MK.2, the newer Stream Deck (V2), Neo, Stream Deck + (with dials and the touch strip)
and the Pedal. The first Stream Deck (2017), the Mini, the Studio and the + XL are not supported in direct USB mode
yet.

!!! warning "Only one program at a time"
    A Stream Deck can only listen to one program. Close other Stream Deck programs, like StreamController or OpenDeck,
    or untick **Use Stream Decks on USB**. Cuebot tells you which program is using the deck.

### Linux: the permission rule

If Cuebot says it has "no permission to open /dev/hidraw…", Linux is not letting it talk to the deck yet. Install
Cuebot's rule file once:

1. Find `70-cuebot-streamdeck.rules`. It is in the `share` folder of the tarball, or in the
   [Cuebot repository](https://github.com/TechDaddyKB/cuebot/blob/main/packaging/linux/70-cuebot-streamdeck.rules).
2. Open a terminal in that folder and type:

    ```bash
    sudo cp 70-cuebot-streamdeck.rules /etc/udev/rules.d/ && sudo udevadm control --reload && sudo udevadm trigger
    ```

3. Unplug the Stream Deck and plug it back in.

## Changing keys from your actions

Your actions can change Stream Deck keys while you stream. For example, turn the "Mute" key red when your mic is muted.
The **Stream Deck** sub-actions are **Set Title**, **Set State**, **Toggle State**, **Set key picture**, **Set key value**, **Show Alert** and **Show Ok**.

The **Stream Deck key or dial used** trigger can react to **Key down**, **Key up**, and on a Stream Deck + also **Dial rotate**,
**Dial press**, **Dial release** and **Touch tap**. Other triggers: **Stream Deck Connected**, **Stream Deck
Disconnected** and **Stream Deck devices changed**.

## No Stream Deck?

Make a [deck](stream-apps-and-devices.md#decks-buttons-on-your-phone) instead: buttons on your phone or tablet,
in a web browser.
