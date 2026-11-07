# PNGTuber Remix

Cuebot can change your PNGTuber Remix avatar state, show or hide sprites, shake, bounce or move them, throw things at your avatar, and load another model. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

[PNGTuber Remix](https://mudkipworld.itch.io/pngremix) is a free PNGTuber app for Windows and Linux.

1. In PNGTuber Remix, look at the tabs on the right side (Properties, Movement and so on). Press the small arrow next to
   them until you see **WebSocket**, and open it.
2. Tick **Auto-Start** (so you never have to do this again) and press **Start**.
3. In Cuebot open **Apps > Stream apps**, tab **VTuber apps**, and press **Connect** in the PNGTuber Remix card. Your states appear in a
   list; press **Show** next to one to test it.
4. Use the steps under **PNGTuber Remix** in an action, for example **Change avatar state** or **Throw things at avatar**.

**Sprites** are the pieces your model is made of, like `Hat`, `Glasses` or `Head`. Type the name exactly as it is in
PNGTuber Remix's **Sprites** list (the name box suggests them once you are connected). A sprite with pieces attached
to it is a **group**: pick **This sprite and everything attached to it** to show or hide them all together.

Some ideas:

- **Raid:** **Shake sprite** on your top sprite for 2 seconds.
- **Bits:** **Throw things at avatar** with **How many** set to `%bits%`. It throws the images you added as throwables
  in PNGTuber Remix.
- **Channel point "Hat on":** **Show/hide sprite**, sprite `Hat`, **Visible**; then **Delay** 60 seconds; then the same
  step with **Hidden**.
- **Make it big:** **Move, grow or spin sprite** with **Size** `2` and **Go back after** `5`.
