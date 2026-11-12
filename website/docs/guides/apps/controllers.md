# Controllers and MIDI

Buttons are the quickest way to run an action on stream: press one, and the action runs. Cuebot can use many kinds of
buttons: a Stream Deck, buttons in a web page on your phone, music gear with pads and knobs, a game controller, keys on
your keyboard, and even your voice. Pick the one you have below to read how to set it up.

Most of them are on the **Input** page. MIDI and game controllers are on **Input > Input**, which has **MIDI** and
**Game controllers** in the list on the left.

![Input > Input > Game controllers](../../assets/screens/tab-input-game-controllers.png)

| Thing | What it does | Where in Cuebot | Guide |
| --- | --- | --- | --- |
| **Stream Deck** | Elgato's button boxes: each key runs an action and can show a picture or text. | Input > Stream Deck | [Stream Deck](stream-deck.md) |
| **Decks** (buttons in a browser) | Your own button pages, on a phone, tablet or second screen. | Input > Decks | [Decks](decks.md) |
| **MIDI devices** (pads, keyboards, knob and fader boxes) | Pads and knobs start the **MIDI Message** trigger; **Learn** fills in the details for you. Actions can send MIDI back, for example to light a pad. | Input > Input, **MIDI** | [MIDI](midi.md) |
| **Game controllers** (Xbox, PlayStation, Switch Pro, joysticks) | Buttons, button combos, sticks and the D-pad become triggers. Rumble and lights on pads that have them. | Input > Input, **Game controllers** | [Game controllers](game-controllers.md) |
| **Keyboard shortcuts** | A key or key combination runs an action, even while another window is in front. | Automation > Hotkeys | [Hotkeys](../actions/hotkeys.md) |
| **Your voice** | Say a phrase, and an action runs. | Audio > Voice Control | [Voice control](../sound/voice-control.md) |

A GoXLR's buttons can be hotkeys too: see [GoXLR](goxlr.md).

!!! warning "Partly checked"
    Sending MIDI was tried on Windows and Linux, and on a Mac with Cuebot's own pretend MIDI ports, but pressing keys
    on a real MIDI controller was not. Game controllers: Cuebot's controller part starts on Windows and Linux, but
    nobody has pressed a real controller yet. See [What's experimental](../help/experimental.md).

**On Linux**, PlayStation and Switch pads need extra permission for rumble and lights: install your distribution's
`steam-devices` or `game-devices-udev` package. See [Game controllers](game-controllers.md#what-you-need).
