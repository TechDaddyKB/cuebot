# Other stream apps

Not using OBS, or using something next to it? Cuebot also talks to these streaming apps. Each app has its own tab on
**Apps > Stream apps**, and each one gives you triggers and sub-actions, like OBS does.

| App | Tab | How to connect |
| --- | --- | --- |
| **Streamlabs Desktop** | OBS & Streamlabs Desktop | In Streamlabs open **Settings > Remote Control**, click the QR code to show the details, and copy the port and **API token**. |
| **Meld Studio** | Meld & PolyPop | In Meld open **File > Preferences > Advanced** and switch on "Allow remote connections". |
| **PolyPop** | Meld & PolyPop | Install the community WebSocket plugin in PolyPop, add a WebSocket source and use the port shown in Cuebot. |
| **T.I.T.S.** | T.I.T.S. | In T.I.T.S. open **Settings** and switch the API on. |
| **Lumia Stream** | Lumia Stream | In Lumia open **Settings > API**, switch on "Enable Developers API", click "Copy Token" and paste it into Cuebot. |
| **OBSBOT cameras** | OBSBOT | In OBSBOT Center open **Settings > OSC** and switch OSC on, or talk straight to a Tail 2 / Tail Air. |
| **mtion studio** | mtion | Nothing to switch on while mtion is open. Add external triggers in mtion and use them with the **Send trigger** sub-action. |
| **veadotube, PNGTuber Remix, RahiTuber, VNyan, VRChat, Warudo, nizima LIVE, Animaze, VSeeFace and other VMC apps** | VTuber apps | See [VTuber apps](vtuber/index.md). |

For **OBS Studio**, see [OBS Studio](obs.md).

!!! warning "Experimental"
    **Meld Studio**, **PolyPop**, **T.I.T.S.**, **Lumia Stream**, **mtion studio** and **OBSBOT** are built from each
    app's own instructions and checked by Cuebot's tests, but not yet with the real apps. **OBS Studio** and
    **Streamlabs Desktop** were tried for real. See [What's experimental](../help/experimental.md).

!!! note "StreamFog"
    The **StreamFog** tab explains that StreamFog has no public way for other apps to control it, so Cuebot cannot
    change its lenses or outfits. You can still show and hide its OBS source with Cuebot's OBS sub-actions.
