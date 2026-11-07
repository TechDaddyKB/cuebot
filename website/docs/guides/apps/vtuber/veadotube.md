# veadotube

veadotube mini and veadotube are PNGTuber apps: your avatar is a set of pictures (states) that change when you talk. Cuebot can change your avatar state and turn push-to-talk on or off, and starts actions when the state or push-to-talk changes. You switch nothing on in veadotube: Cuebot finds it by itself while it is open. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

1. Open veadotube mini (or veadotube).
2. In Cuebot open **Apps > Stream apps**, tab **VTuber apps**, and press **Connect** in the veadotube card. Your avatar states appear in
   a list; press **Show** next to one to test it.
3. In an action, add **veadotube > Change avatar state** and type the state's name, or **Push-to-talk on/off**.

veadotube picks a new "door" (port) number every time it starts, so there is nothing to type in. Leave **Which
veadotube** empty unless you run more than one: then type its name, for example `veadotube mini`. For veadotube on
another computer, type its address and port, like `192.168.1.20:2424`.

**How to change the state.** "Switch to this state" just shows it. "Show on top" shows it but remembers the state before,
so a later "Take this state off" goes back. That is handy for a short reaction: show *surprised* on top, wait 5 seconds,
take it off.

The trigger **Avatar state changed** runs whenever the state changes, also when you click it in veadotube yourself.
