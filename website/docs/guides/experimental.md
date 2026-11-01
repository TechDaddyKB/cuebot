# What's experimental

Some parts of Cuebot are **experimental**. This page lists them.

## What "experimental" means

Experimental means:

- the feature is **built**, and it is in the app;
- Cuebot's own tests check it, with a pretend copy of the other app or service;
- but nobody has tried it yet with the **real** app, device or service.

So it may work fine. Or it may not work at all, because the real app does something the pretend copy did not. Use it,
but check that it works before you rely on it on stream.

!!! tip "You can help"
    Tried one of these? Please tell us how it went, good or bad. Go to **Settings > Support**, open the **Report a
    problem** tab, write what you tried and what happened, and click **Send report**. You do not need a GitHub
    account. Tokens, passwords and keys are removed from the report before it is sent.

In the other guides, experimental features have a yellow **Experimental** box like this:

!!! warning "Experimental"
    Not tried with the real app yet. See [What's experimental](experimental.md).

## Not tried with the real thing yet

| Feature | Where it is | What was checked | What was not checked | What a tester should try |
| --- | --- | --- | --- | --- |
| [Discord bot](discord-bot.md) | **Apps > Apps & devices**, tab **Discord bot** | Cuebot's tests, with a pretend Discord | The whole feature with a real bot token and a real Discord server | Make a bot, connect it, post a message, give a role, register and use a slash command |
| [Spotify song requests and Spotify now playing](music-and-now-playing.md#set-up-spotify) | **Audio > Music** | Cuebot's tests, with a pretend Spotify | Signing in to a real Spotify account; adding, skipping and showing real songs | Make the Spotify app, sign in, show the song, add a song with `!sr` |
| [Now playing on Windows](music-and-now-playing.md#show-the-song-you-are-playing) | **Audio > Music** | Cuebot's tests, with a pretend helper. (On Linux it was tried with real players: mpv and VLC.) | The small Windows helper has never run on a real Windows computer | Tick **Watch what plays**, play a song in Spotify, a browser or another player, and check the name shows |
| [VRChat](vtuber-apps.md#vrchat) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only. The real test stopped at VRChat's terms page. | Talking to a real VRChat | Set an avatar parameter, send a chatbox message, change avatar and check the trigger runs |
| [Animaze](vtuber-apps.md#animaze) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only | Talking to a real Animaze | Connect, play an emote, load another avatar |
| [nizima LIVE](vtuber-apps.md#nizima-live) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only. nizima LIVE did not start on the test computer. | Talking to a real nizima LIVE | Connect, switch Cuebot on in the Plugin Manager, turn an expression on and off |
| [VSeeFace, VMagicMirror and other VMC apps](vtuber-apps.md#vseeface-vmagicmirror-and-other-3d-apps-vmc) | **Apps > Stream apps**, tab **VTuber apps** | VSeeFace got Cuebot's messages and moved the avatar's head | The expressions did not visibly change in that test | Show `Joy` for 3 seconds and watch your avatar's face |
| [Cloud voices except OpenAI](text-to-speech.md#1-pick-an-engine): ElevenLabs, Microsoft Azure, Google Cloud, Amazon Polly, IBM Watson, Acapela Cloud, CereProc, TTS Monster, UberDuck | **Audio > Speech**, tab **Engines** | Cuebot's tests, with pretend services | Speaking with a real account and key | Paste your key, click **Test**, and speak a chat message |
| [Meld Studio](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **Meld & PolyPop** | Cuebot's tests, built from Meld's own instructions | A real Meld Studio | Connect, switch a scene, show and hide a layer |
| [PolyPop](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **Meld & PolyPop** | Cuebot's tests only | A real PolyPop with the WebSocket plugin | Connect the plugin and send a test alert |
| [T.I.T.S.](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **T.I.T.S.** | Cuebot's tests, built from T.I.T.S.'s own instructions | A real T.I.T.S. | Connect, throw an item, run a T.I.T.S. trigger |
| [Lumia Stream](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **Lumia Stream** | Cuebot's tests, built from Lumia's own instructions | A real Lumia Stream | Connect with your token, set a colour, send a command |
| [mtion studio](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **mtion** | Cuebot's tests only. mtion has since been renamed, so its connection may have changed. | A real mtion | Connect and run a **Send trigger** step |
| [OBSBOT cameras](stream-apps-and-devices.md#other-stream-apps) | **Apps > Stream apps**, tab **OBSBOT** | Cuebot's tests, built from OBSBOT's own instructions | A real OBSBOT Center or camera | Connect, move the camera, zoom, go to a preset |
| [Elgato Key Light, Ring Light, Light Strip](stream-apps-and-devices.md#lights-mixers-and-other-apps) | **Apps > Apps & devices**, tab **Elgato lights & webcams** | Cuebot's tests only | A real light, and finding lights on a real home network | Let Cuebot find your light, or type its address; change brightness and colour |
| [Elgato Facecam and webcam controls (Linux)](stream-apps-and-devices.md#lights-mixers-and-other-apps) | **Apps > Apps & devices**, tab **Elgato lights & webcams** | Cuebot's tests only | A real webcam; spotting the Camera Hub and Elgato Studio apps | Change brightness or zoom from an action |
| [Elgato Wave Link](stream-apps-and-devices.md#lights-mixers-and-other-apps) | **Apps > Apps & devices**, tab **Wave Link** | Cuebot's tests, with a pretend Wave Link | A real Wave Link | Connect, mute a channel, change a volume |
| [Pulsoid and HypeRate](stream-apps-and-devices.md#lights-mixers-and-other-apps) (heart rate) | **Apps > Apps & devices**, tab **Apps & services** | Cuebot's tests only | A real heart-rate service | Connect and check your heart rate shows up |
| [Basic Discord webhook and IFTTT](donations-and-integrations.md#discord) | Sub-actions **Discord > Basic webhook** and **IFTTT > Notification** | Cuebot's tests only | Posting to a real Discord webhook or IFTTT | Post a test message to a channel |
| [Linking two Cuebots](servers.md#linking-two-cuebots) | **Apps > Servers**, **Remote instances** | Two copies of Cuebot inside Cuebot's tests | Two real computers on a home network | Link two Cuebots and send a signal from one to the other |
| Cuebot on a Mac | (no download yet) | Some parts of the code know about Macs | Everything: there is no Mac download, and Cuebot has never run on a Mac | Nothing yet. Cuebot is for Windows and Linux for now. |

## Partly checked

These were tried with the real thing, but only some of it.

| Feature | Where it is | What was checked with the real thing | What was not checked |
| --- | --- | --- | --- |
| [Tips and donations](donations-and-integrations.md#tip-and-donation-services): Streamlabs, StreamElements, TipeeeStream, DonorDrive, TreatStream, Pally.gg | **Apps > Donations** | Streamlabs and StreamElements connected; DonorDrive read a real Extra Life team. | A real tip or donation coming in. Not confirmed: connecting to TipeeeStream, TreatStream and Pally.gg. |
| [Webhooks](donations-and-integrations.md#webhooks-ko-fi-patreon-throne-and-more): Ko-fi, Patreon, Throne, Fourthwall, Shopify | **Apps > Webhooks** | The relay: a message sent to a real relay reached Cuebot. The services' own test messages were checked against Cuebot. | A real message sent from each service to Cuebot |
| [StreamLoots](stream-apps-and-devices.md#lights-mixers-and-other-apps) | **Apps > Apps & devices**, tab **Apps & services** | Connecting to StreamLoots | A real card or chest |
| [Crowd Control](stream-apps-and-devices.md#lights-mixers-and-other-apps) | **Apps > Apps & devices**, tab **Apps & services** | Crowd Control answering Cuebot | A real effect bought by a viewer |
| [Stream Deck with the Stream Deck app](stream-deck.md#way-1-the-stream-deck-app-and-cuebots-plugin) | **Input > Stream Deck** | The plugin connected to Cuebot in the real Stream Deck app on Windows | Not every key, dial and touch action |
| [Stream Deck over USB](stream-deck.md#way-2-direct-usb-linux) | **Input > Stream Deck** | A real Stream Deck XL on Linux | The MK.2, the newer Stream Deck (V2), Neo, Stream Deck + and the Pedal |
| [MIDI](stream-apps-and-devices.md#your-buttons-and-controllers) | **Input > Input**, tab **MIDI** | Sending MIDI on Windows and Linux; a pretend loop on Linux | Pressing keys on a real MIDI controller |
| [Game controllers](stream-apps-and-devices.md#your-buttons-and-controllers) | **Input > Input**, tab **Game controllers** | The controller part starts on Windows and Linux | A real game controller being pressed |
| [Rumble](connecting-platforms.md#rumble) | **Platforms > Rumble** | Reading: followers, live state and chat | Not confirmed: writing in Rumble chat |
| [The assistant](assistant.md) | **Home > Assistant** | Codex, Claude Code, Grok CLI and Antigravity CLI on Linux; an OpenAI key; Ollama with Qwen3.5 | Keys for Claude (Anthropic), Grok (xAI) and Gemini (Google) were only checked with pretend services. LM Studio is not confirmed. |

## Tried with the real thing

For comparison: these were tried with the real app, device or service. They can still have bugs, so please report
anything odd.

- Twitch, YouTube and Kick: signing in, chat, and going live (Kick and Rumble chat use unofficial ways in, which can
  break when those sites change).
- OBS Studio and Streamlabs Desktop.
- VTube Studio and Voicemod.
- veadotube mini, VNyan, Warudo, PNGTuber Remix and RahiTuber.
- GoXLR (with the GoXLR Utility on Linux).
- Now playing on Linux (mpv and VLC).
- Piper and OpenAI voices, eSpeak NG, and voice control with Whisper.
- Importing a real Streamer.bot setup, and the Streamer.bot WebSocket server with Streamer.bot's own client.
- The community overlay gallery.
