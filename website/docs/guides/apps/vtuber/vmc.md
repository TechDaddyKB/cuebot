# VSeeFace, VMagicMirror and other 3D apps (VMC)

VMC is a shared language that many 3D VTuber apps understand. Cuebot uses it to change your avatar's **expression**:
a smile, an angry face, a surprised face and so on. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

!!! warning "Experimental"
    In a test, VSeeFace got Cuebot's messages and moved the avatar's head, but the expressions did not visibly change.
    So this may not work yet. See [What's experimental](../../help/experimental.md).

1. Switch on your app's **VMC receiver**:
    - **VSeeFace:** **Settings > General settings > VMC protocol > VMC receiver**. Then untick **Apply expressions**
      under it, or VSeeFace's own expressions cover Cuebot's.
    - **VMagicMirror**, **VNyan**, **Warudo**: look for "VMC" or "VMC protocol" in the settings and switch on receiving.
2. Check the port number. Most apps use **39539**, which is Cuebot's default.
3. In Cuebot press **Connect** in the VMC card, type an expression like `Joy` and press **Show for 3 seconds**. Watch
   your avatar: VMC never answers, so Cuebot cannot tell whether the app heard it.
4. In an action, use **Show avatar expression (VMC)** and pick an expression. Set **For (seconds)** to make it go away
   by itself.

Every VRM avatar has the expressions `Joy`, `Angry`, `Sorrow`, `Fun` and `Surprised`. Yours may have more; use the
exact name from your avatar. If nothing happens, check that the receiver is on, the port matches, and only one app
listens on that port.

!!! note "Keep VSeeFace tracking your face"
    VSeeFace's VMC receiver says "disables tracking". To keep VSeeFace tracking your face as well, tick **Apply
    VSeeFace tracking** under it, and tick the parts it should keep tracking (**Track face features**, **Track head and
    neck** and so on). Leave **Apply expressions** unticked so Cuebot's expressions show.
