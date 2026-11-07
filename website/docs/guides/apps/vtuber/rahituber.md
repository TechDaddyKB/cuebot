# RahiTuber

Cuebot can press your RahiTuber states, like their hotkeys: tap them, let go, or hold them for some seconds. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

[RahiTuber](https://rahisaurus.itch.io/rahituber) is a free PNGTuber app for Windows and Linux.

1. In RahiTuber press ++esc++ to open the menu, then **Preferences > Integration**, and tick **Control States via
   HTTP**. Leave the port at 8000.
2. In Cuebot, the RahiTuber card on **Apps > Stream apps**, tab **VTuber apps**, has a **Test state** box. Type `0` and press
   **Press it**: your first state should switch on.
3. In an action, add **RahiTuber > RahiTuber state** and type the state.

**Which state?** Type its name, or its place in RahiTuber's states list counted from **0**: the top state is 0, the
next one 1, and so on. Names are safer, because the numbers shift when you add or move states. To give a state a name,
type it in the state's settings in RahiTuber.

**Press, let go or hold.** "Press it" works like tapping the state's hotkey. If the state is set to **while held**, it
stays on until a step lets go, so use "Hold it for some seconds" to show it for a while and then let go by itself.

!!! note "RahiTuber always says OK"
    RahiTuber answers every request, even for a state it does not have. If nothing happens, check the state's name
    (capital letters matter) or number.
