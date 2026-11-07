# nizima LIVE

Cuebot can turn nizima LIVE expressions on and off, play motions, press the model's hotkeys, show and hide items, swap models and switch effects. It starts actions when you select another model. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

[nizima LIVE](https://nizimalive.com/en/) is the Live2D app made by Live2D, the company behind Live2D models. It runs on
Windows and Mac.

!!! warning "Experimental"
    nizima LIVE is only checked by Cuebot's own tests so far: nizima LIVE did not start on the test computer. See
    [What's experimental](../../help/experimental.md).

1. In nizima LIVE open the **Plugin Manager** and switch the plug-in function on. It shows a port number, usually 22022.
2. In Cuebot press **Connect** in the nizima LIVE card. The first time, **Cuebot** now appears in nizima LIVE's
   Plugin Manager: switch it on there. Cuebot remembers this, so you only do it once.
3. The card lists your model's expressions, each with an **On/off** test button.
4. Use the steps under **nizima LIVE** in an action, for example **nizima LIVE expression on/off** or **Play nizima LIVE
   motion**. The name boxes suggest what your model has once you are connected.

Steps act on the **selected** model unless you name one. Most people only have one model on screen, so leave
**Model** empty.

Some ideas:

- **Channel point "Blush":** **nizima LIVE expression on/off** with `Blush`, **On**, and **Turn the other expressions off
  first** ticked; then **Delay** 10 seconds; then **nizima LIVE expressions off**.
- **Follow:** **Play nizima LIVE motion** with your wave motion.
- **Anything else:** give it a hotkey in nizima LIVE, then use **Press nizima LIVE hotkey** with that key, like `F1`.
- **Hats and props:** **Show nizima LIVE item** puts an item from your item list on screen; **Hide nizima LIVE item**
  takes it off again.

If you remove Cuebot from the Plugin Manager, press **Connect** again: Cuebot registers again and you switch it on once
more.
