# RahiTuber

Cuebot can press your RahiTuber states, like their hotkeys: tap them, let go, or hold them for some seconds. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

[RahiTuber](https://rahisaurus.itch.io/rahituber) is a free PNGTuber app for Windows and Linux.

1. In RahiTuber press ++esc++ to open the menu, then **Preferences > Integration**, and tick **Control States via
   HTTP**. Leave the port at 8000.
2. Optional, but it helps a lot: on the RahiTuber card in Cuebot, set **Folder** to the folder where RahiTuber's program
   is (the one with `config.xml` and `RahiTuber_Log.txt` in it). Cuebot then lists your avatar's states with their type,
   suggests their names in steps, and confirms each request from RahiTuber's log. Cuebot only reads these files.
3. Press **Check connection**. It says "RahiTuber answers at 127.0.0.1:8000" without changing your avatar.
4. To try a state, type its name in **Test state** (or use **Press** next to it in the list) and watch your avatar.
   Pick a Toggle or "while held" state, not a Permanent one: a Permanent state rewrites your avatar's defaults. A Toggle
   state that is already on switches off when pressed.
5. In an action, add **RahiTuber > RahiTuber state** and type the state.

**Which state?** Type its name exactly as in RahiTuber (capital letters and spaces matter), or its place in RahiTuber's
states list counted from **0**: the top state is 0, the next one 1, and so on. Names are safer, because the numbers shift
when you add or move states. To give a state a name, type it in the state's settings in RahiTuber.

- Give every state its own name. RahiTuber uses the first state in its list that matches.
- Avoid names that are plain numbers. RahiTuber reads `2` both as the state named "2" and as the state in place 2, and
  the one higher up the list wins.
- Avoid spaces at the start or end of a name. With the folder set, Cuebot finds the name anyway; without it, Cuebot
  sends the name without those spaces and RahiTuber does not find it.

## Press, let go or hold

What a press does depends on the state's type in RahiTuber (the card shows it when the folder is set):

| Type in RahiTuber | Press it | Let go | Hold for some seconds |
| --- | --- | --- | --- |
| **Toggle** (the usual one) | Switches it on, or off if it is on | Nothing | Just a press: the state stays on until its timeout or the next press |
| **While held** | On, until something lets go | Off | On for that long, then off |
| **Permanent** | Changes the avatar's defaults **for good** | Nothing | Just a press |

- A Toggle state with **timeout** ticked (5 seconds at first) switches itself off after that time. Use that for
  "show it for a while" with Toggle states.
- RahiTuber ignores a second press of the same Toggle state within 0.2 seconds. Cuebot keeps presses of one state at
  least 250 milliseconds apart, so quick redeems are not lost.
- A state set to work only **while talking** (or only **while quiet**) ignores presses at other times. They are not saved
  for later.
- When an action is stopped during a hold, Cuebot still lets go, so a "while held" state never stays stuck on.

## Ready-made: redeems, subs and outfit changes

1. Download [rahituber-examples.cuebot](rahituber-examples.cuebot) and open it on **Settings > Import / Export**, tab
   **Import**. It adds three actions in the group **RahiTuber**:
    - **RahiTuber: surprised face on redeem**: the Twitch reward **Surprise** presses the state `Surprised`. Make that a
      Toggle state with a timeout, so it goes away by itself.
    - **RahiTuber: hat for 30 seconds on a sub**: a new sub holds the state `Hat` for 30 seconds. Set `Hat` to "while
      held" in RahiTuber.
    - **RahiTuber: outfit change on !outfit**: the chat command `!outfit` presses `Jacket`, waits 0.3 seconds, then
      presses `Scarf`. The command has a 30-second cooldown, so chat cannot spam it.
2. Change the reward, command and state names to your own. To limit a channel point reward, set its cooldown on Twitch.

To change several states at once, add one **RahiTuber state** step per state, as in the outfit example. A short
**Delay** between them is optional; Cuebot already sends them one after the other.

## Show RahiTuber in OBS

Cuebot cannot change how RahiTuber is captured. In RahiTuber's **Preferences > Appearance**, tick **Transparent** and
add a **Window Capture** of RahiTuber in OBS. On Windows, RahiTuber can also send its picture over Spout2
(**Preferences > Integration > Use Spout2**); OBS needs the Spout2 plugin to receive it.

## Troubleshooting

!!! note "RahiTuber always says OK"
    RahiTuber answers every request, even for a state it does not have. If nothing happens, check the state's name
    (capital letters matter) or number. With the folder set, Cuebot says in its status and log when a name is not in
    your avatar, and adds "confirmed by RahiTuber_Log.txt" once RahiTuber has logged the request.

- **"Something else answers"**: another program (for example a local web server) has port 8000, and RahiTuber could not
  get it. RahiTuber does not show an error when that happens. Pick another port in RahiTuber's Preferences and the same
  one on the Cuebot card, then restart RahiTuber.
- **"Not reachable"**: RahiTuber is closed, **Control States via HTTP** is off, or the ports differ. If RahiTuber is open,
  look in `RahiTuber_Log.txt` in its folder: it lists every request it gets.
- **Presses arrive late, all at once**: RahiTuber holds requests while it is loading an avatar or a layer's rename box is
  open, and while the avatar has no states at all. They all run when that ends.
- **Changed your states?** Save the avatar in RahiTuber, then press **Reload states** on the card.
