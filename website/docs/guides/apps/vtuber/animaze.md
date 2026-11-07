# Animaze

Cuebot can change your Animaze avatar, background or quickscene, play emotes, special actions, poses and idles, and turn the virtual camera on or off. It starts actions when you change avatar. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

!!! warning "Experimental"
    Animaze is only checked by Cuebot's own tests so far, not with the real app. See
    [What's experimental](../../help/experimental.md).

1. In Animaze, open **Settings > Animaze API** and switch it on. It is off at first, and it uses port 9000.
2. In Cuebot press **Connect** in the Animaze card. Your avatar's emotes appear with a **Play** button each.
3. Use the steps under **Animaze**: **Load Animaze avatar**, **Play Animaze emote**, **Play Animaze special action**,
   **Animaze pose** and so on. The name boxes suggest what your avatar has once you are connected.

Emotes, special actions, poses and idle animations belong to the avatar you are using, so the lists change when you
change avatar. Cuebot reads them again by itself. The trigger **Avatar changed** runs whenever Animaze switches avatar.
