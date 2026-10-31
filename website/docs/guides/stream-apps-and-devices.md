# Stream apps and devices

Cuebot can work with the other programs and gadgets on your desk. Each connection gives you two things:

- **triggers**: the app or device can *start* your actions (a button press, a scene change);
- **sub-actions**: your actions can *control* the app or device (switch a scene, change a light).

Nothing connects by itself until you set it up, and most pages have a box to connect automatically next time.

## Stream apps

Go to **Apps > Stream apps**. Each app has its own tab.

### OBS Studio

1. In OBS, open **Tools > WebSocket Server Settings**, tick **Enable WebSocket server** and click **Show Connect
   Info**.
2. In Cuebot, open the **OBS & Streamlabs Desktop** tab. Under **OBS Studio**, check **Host** and **Port** and paste the
   **Password** from OBS.
3. Click **Connect**, and tick **Connect automatically and reconnect when OBS restarts**.

Once connected, the page shows your scenes and sources, with buttons like **Start / stop stream**, **Start / stop
recording** and **Save replay**. Your actions can now switch scenes (**Set scene**), show and hide sources (**Set source
visibility**), change text and filters, and much more. OBS events, like "scene changed" or "stream started", become
triggers.

**Two computers, or two copies of OBS?** Click **+ Connection** to add another. Give each a **Name**. OBS sub-actions
and triggers then let you pick which OBS to use; empty means the first one.

**OBS on another computer?** Put that computer's address in **Host** instead of `127.0.0.1`, and make sure its firewall
lets the port through.

### Other stream apps

| App | Tab | How to connect |
| --- | --- | --- |
| **Streamlabs Desktop** | OBS & Streamlabs Desktop | In Streamlabs open **Settings > Remote Control**, click the QR code to show the details, and copy the port and **API token**. |
| **Meld Studio** | Meld & PolyPop | In Meld open **File > Preferences > Advanced** and switch on "Allow remote connections". |
| **PolyPop** | Meld & PolyPop | Install the community WebSocket plugin in PolyPop, add a WebSocket source and use the port shown in Cuebot. |
| **T.I.T.S.** | T.I.T.S. | In T.I.T.S. open **Settings** and switch the API on. |
| **Lumia Stream** | Lumia Stream | In Lumia open **Settings > API**, switch on "Enable Developers API", click "Copy Token" and paste it into Cuebot. |
| **OBSBOT cameras** | OBSBOT | In OBSBOT Center open **Settings > OSC** and switch OSC on, or talk straight to a Tail 2 / Tail Air. |
| **mtion studio** | mtion | Nothing to switch on while mtion is open. Add external triggers in mtion and use them with the **Send trigger** sub-action. |

!!! note "StreamFog"
    The **StreamFog** tab explains that StreamFog has no public way for other apps to control it, so Cuebot cannot
    change its lenses or outfits. You can still show and hide its OBS source with Cuebot's OBS sub-actions.

## Your buttons and controllers

| Device | Where | What it does |
| --- | --- | --- |
| **Stream Deck** | Input > Stream Deck | See the [Stream Deck page](stream-deck.md). |
| **Decks** (buttons in a browser) | Input > Decks | See below. |
| **MIDI controllers** (pads, keyboards, knob boxes) | Input > Input, **MIDI** | Keys and knobs start the **MIDI Message** trigger; actions can send MIDI back. |
| **Game controllers** (Xbox, PlayStation, Switch Pro and others) | Input > Input, **Game controllers** | Buttons, button combos and sticks become triggers. Rumble and lights on pads that have them. |
| **Keyboard shortcuts** | Automation > Hotkeys | See [How actions work](actions-and-triggers.md#hotkeys). |
| **Your voice** | Audio > Voice Control | See [Text-to-speech](text-to-speech.md#voice-control-talking-to-cuebot). |

**MIDI tip:** add an event for each key or knob, then click **Learn** and press it. Cuebot fills in the details for you.

### Decks: buttons on your phone

A **deck** is a page of buttons that opens in any web browser: your phone, a tablet, or a dock in OBS. Press a
button, and its action runs.

1. Decks need Cuebot's **HTTP server**. Go to **Apps > Servers**, tick **Enabled** on the **HTTP server** card and
   click **Apply and restart servers**. (The Decks page reminds you if it is off.)
2. Go to **Input > Decks** and click **+ Deck**. Give it a name.
3. Set how many **Rows** and **Columns** of buttons you want.
4. Click **+ Button**. Give it a **Title** and pick the **Action** to run. Add colours and an icon if you like.
5. Click **Open** next to **Address** to see the deck in your browser.

While you stream, your actions can change a button's title, colour, icon or page with the **Decks** sub-actions.

!!! tip "Using it from your phone"
    At first, the deck only opens on this computer. Your phone is a different device, so Cuebot has to allow your home
    network first: tick **Allow LAN connections** on **Apps > Servers** and let the HTTP server listen on your network
    address. Only do this on a home network you trust.

## Lights, mixers and other apps

Go to **Apps > Apps & devices**. It has the tabs **Apps & services**, **Elgato lights & webcams**, **Wave Link** and
**GoXLR**.

| Thing | Tab | Notes |
| --- | --- | --- |
| **Elgato Key Light, Ring Light, Light Strip** | Elgato lights & webcams | Found on your home network by themselves, with or without Elgato's app. Turn them on and off, change brightness and colour from actions. |
| **Elgato Facecam and USB webcams** | Elgato lights & webcams | On Linux, Cuebot can change brightness, zoom, focus and more. |
| **Elgato Wave Link** (audio mixer) | Wave Link | Found on this computer by itself. Change mixes, volumes and mutes. |
| **GoXLR** | GoXLR | Needs the free **GoXLR Utility** program. Faders, mutes, effects and profiles. |
| **VTube Studio** | Apps & services | In VTube Studio switch on "Start API (allow plugins)", click **Connect** in Cuebot, then **Allow** in VTube Studio. |
| **Voicemod** | Apps & services | Needs the Voicemod app (Windows) and a key from Voicemod's developer program. |
| **Pulsoid**, **HypeRate** | Apps & services | Your heart rate, as a value and a trigger. |
| **StreamLoots**, **Crowd Control** | Apps & services | Cards, chests and viewer effects become triggers. |

Donations and web services are on their own page: [Donations and integrations](donations-and-integrations.md).

## On Linux

- **Game controllers:** PlayStation and Switch pads need extra permission for rumble and lights. Install your
  distribution's `steam-devices` or `game-devices-udev` package.
- **Stream Deck:** see [Stream Deck on Linux](stream-deck.md#linux-the-permission-rule).
