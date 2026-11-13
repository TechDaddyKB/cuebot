# What's experimental

Some parts of Cuebot are **experimental**: built and tested by Cuebot, but not yet tried with the real app or
service. This page lists them, so you know what to check before you rely on it on stream.

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
| [Discord bot](../services/discord-bot.md) | **Apps > Apps & devices**, tab **Discord bot** | Cuebot's tests, with a pretend Discord | The whole feature with a real bot token and a real Discord server | Make a bot, connect it, post a message, give a role, register and use a slash command |
| [Spotify song requests and Spotify now playing](../sound/spotify.md) | **Audio > Music** | Cuebot's tests, with a pretend Spotify | Signing in to a real Spotify account; adding, skipping and showing real songs | Make the Spotify app, sign in, show the song, add a song with `!sr` |
| [Now playing on Windows](../sound/now-playing.md) | **Audio > Music** | Cuebot's tests, with a pretend helper. (On Linux it was tried with real players: mpv and VLC.) | The small Windows helper has never run on a real Windows computer | Tick **Watch what plays**, play a song in Spotify, a browser or another player, and check the name shows |
| [VRChat](../apps/vtuber/vrchat.md) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only. The real test stopped at VRChat's terms page. | Talking to a real VRChat | Set an avatar parameter, send a chatbox message, change avatar and check the trigger runs |
| [Animaze](../apps/vtuber/animaze.md) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only | Talking to a real Animaze | Connect, play an emote, load another avatar |
| [nizima LIVE](../apps/vtuber/nizima-live.md) | **Apps > Stream apps**, tab **VTuber apps** | Cuebot's tests only. nizima LIVE did not start on the test computer. | Talking to a real nizima LIVE | Connect, switch Cuebot on in the Plugin Manager, turn an expression on and off |
| [VSeeFace, VMagicMirror and other VMC apps](../apps/vtuber/vmc.md) | **Apps > Stream apps**, tab **VTuber apps** | VSeeFace got Cuebot's messages and moved the avatar's head | The expressions did not visibly change in that test | Show `Joy` for 3 seconds and watch your avatar's face |
| [Cloud voices except OpenAI](../sound/text-to-speech.md#1-pick-an-engine): ElevenLabs, Microsoft Azure, Google Cloud, Amazon Polly, IBM Watson, Acapela Cloud, CereProc, TTS Monster, UberDuck | **Audio > Speech**, tab **Engines** | Cuebot's tests, with pretend services | Speaking with a real account and key | Paste your key, click **Test**, and speak a chat message |
| [Meld Studio](../apps/meld-studio.md) | **Apps > Stream apps**, tab **Meld & PolyPop** | Cuebot's tests, built from Meld's own instructions | A real Meld Studio | Connect, switch a scene, show and hide a layer |
| [PolyPop](../apps/polypop.md) | **Apps > Stream apps**, tab **Meld & PolyPop** | Cuebot's tests only | A real PolyPop with the WebSocket plugin | Connect the plugin and send a test alert |
| [T.I.T.S.](../apps/tits.md) | **Apps > Stream apps**, tab **T.I.T.S.** | Cuebot's tests, built from T.I.T.S.'s own instructions | A real T.I.T.S. | Connect, throw an item, run a T.I.T.S. trigger |
| [Lumia Stream](../apps/lumia-stream.md) | **Apps > Stream apps**, tab **Lumia Stream** | Cuebot's tests, built from Lumia's own instructions | A real Lumia Stream | Connect with your token, set a colour, send a command |
| [mtion studio](../apps/mtion-studio.md) | **Apps > Stream apps**, tab **mtion** | Cuebot's tests only. mtion has since been renamed, so its connection may have changed. | A real mtion | Connect and run a **Send trigger** step |
| [OBSBOT cameras](../apps/obsbot.md) | **Apps > Stream apps**, tab **OBSBOT** | Cuebot's tests, built from OBSBOT's own instructions | A real OBSBOT Center or camera | Connect, move the camera, zoom, go to a preset |
| [Elgato Key Light, Ring Light, Light Strip](../apps/elgato-lights.md) | **Apps > Apps & devices**, **Lights & cameras** | Cuebot's tests only | A real light, and finding lights on a real home network | Let Cuebot find your light, or type its address; change brightness and colour |
| [Elgato Facecam and webcam controls (Linux)](../apps/elgato-cameras.md) | **Apps > Apps & devices**, **Lights & cameras** | Cuebot's tests only | A real webcam; spotting the Camera Hub and Elgato Studio apps | Change brightness or zoom from an action |
| [Elgato Wave Link](../apps/wave-link.md) | **Apps > Apps & devices**, tab **Wave Link** | Cuebot's tests, with a pretend Wave Link | A real Wave Link | Connect, mute a channel, change a volume |
| [Pulsoid](../services/pulsoid.md) and [HypeRate](../services/hyperate.md) (heart rate) | **Apps > Apps & devices**, tab **Apps & services** | Cuebot's tests only | A real heart-rate service | Connect and check your heart rate shows up |
| [Basic Discord webhook](../services/discord-bot.md) and [IFTTT](../services/ifttt.md) | Sub-actions **Discord > Basic webhook** and **IFTTT > Notification** | Cuebot's tests only | Posting to a real Discord webhook or IFTTT | Post a test message to a channel |
| [Linking two Cuebots](../developers/linking-two-cuebots.md) | **Apps > Servers**, **Remote instances** | Two copies of Cuebot inside Cuebot's tests | Two real computers on a home network | Link two Cuebots and send a signal from one to the other |

## Partly checked

These were tried with the real thing, but only some of it.

| Feature | Where it is | What was checked with the real thing | What was not checked |
| --- | --- | --- | --- |
| [The phone app (Cuebot Companion)](../mods/phone-app.md) | Your phone; **Apps > Servers**, **Pair a phone** | Android: paired with a real Cuebot over a home network on Android emulators (versions 15 and 16), ran deck buttons, moved the subathon timer and goals live, joined a mod portal by invite and approved a mod's change on the **Mods** tab. iPhone and iPad: the same in Apple's simulators. | A real phone or tablet. Android 17's local network permission. Keeping the mod sign-in key in a real iPhone's Secure Enclave. The iPhone app isn't available to download yet. |
| [The mod portal](../mods/mod-portal.md) | **Chat > Mod portal** | Through the real shared relay: a real Google, Twitch and Discord account each joined by invite; a mod edited and saved actions and commands, uploaded a sound that went live, and changed overlays; a change that needed approval waited and was approved. | Mods using it during a real stream, over days, with many changes. |
| [Tips and donations](../services/tips-and-donations.md): Streamlabs, StreamElements, TipeeeStream, DonorDrive, TreatStream, Pally.gg | **Apps > Donations** | Streamlabs and StreamElements connected; DonorDrive read a real Extra Life team. | A real tip or donation coming in. Not confirmed: connecting to TipeeeStream, TreatStream and Pally.gg. Streamlabs charity donations, merch, loyalty store redemptions and Prime gifts are not documented by Streamlabs, so their shape is a guess (unverified). |
| [Streamlabs app features](../services/streamlabs.md#connect-your-own-streamlabs-app-optional): alerts, alert profiles, wheel, jar, credits, media share, loyalty points, missed-donation back-fill | **Apps > Donations**, Streamlabs card | Cuebot's tests, with a pretend Streamlabs built from its documentation | Signing in with a real Streamlabs app; every REST call | Register an app, connect it, skip an alert, spin the wheel, read your points, and pull the network cable for a minute during a test donation |
| [Streamlabs Desktop extras](../apps/streamlabs-desktop.md): studio mode, scene collections, stream health, notifications, reconnecting and error triggers, failed start reported | **Apps > Stream apps**, tab **OBS & Streamlabs Desktop** | Cuebot's tests, with a pretend Streamlabs Desktop | A real Streamlabs Desktop | Toggle studio mode, switch a collection, start a stream with no stream key and check the step fails |
| [Webhooks](../services/webhooks.md): Ko-fi, Patreon, Throne, Fourthwall, Shopify | **Apps > Webhooks** | The relay: a message sent to a real relay reached Cuebot. The services' own test messages were checked against Cuebot. | A real message sent from each service to Cuebot |
| [Shopify extras](../services/shopify.md): cancellations and refunds, merch counter and goal, stock alerts, API version check, privacy defaults, and with the Admin API products widget, stock names, totals and catch-up | **Apps > Webhooks**, **Shopify extras** | Cuebot's tests, with Shopify's published example payloads and a pretend Shopify built from its documentation | A real store's webhooks (any API version); a real Dev Dashboard app and its token |
| [Fourthwall API features](../services/fourthwall.md#connect-the-fourthwall-api-optional): webhook setup, catch-up, streaming status, gift draws from chat, promo codes, products widget, members, totals | **Apps > Webhooks**, **Fourthwall extras** | Cuebot's tests, with a pretend Fourthwall built from its documentation | A real Fourthwall shop and Open API user; a real stream gift draw |
| [StreamLoots](../services/streamloots.md) | **Apps > Apps & devices**, tab **Apps & services** | Connecting to StreamLoots | A real card or chest |
| [Crowd Control](../services/crowd-control.md) | **Apps > Apps & devices**, tab **Apps & services** | Crowd Control answering Cuebot | A real effect bought by a viewer |
| [Stream Deck with the Stream Deck app](../apps/stream-deck.md#way-1-the-stream-deck-app-and-cuebots-plugin) | **Input > Stream Deck** | The plugin connected to Cuebot in the real Stream Deck app on Windows | Not every key, dial and touch action |
| [Stream Deck plugin in OpenDeck](../apps/stream-deck.md#on-linux-opendeck) | OpenDeck (Linux) | OpenDeck 2.14.1 with a real Stream Deck XL: connecting, the action list, a key press running the action, a title sent by Cuebot | The other keys, dials |
| [StreamController plugin](../apps/streamcontroller.md) | StreamController (Linux) | StreamController 1.5.0-beta.16 with a real Stream Deck XL: every Cuebot key, the action list, the connection settings and every Stream Deck sub-action | Dials and the touch strip of a real Stream Deck + |
| [Stream Deck over USB](../apps/stream-deck.md#way-2-direct-usb) | **Input > Stream Deck** | A real Stream Deck XL on Linux | The MK.2, the newer Stream Deck (V2), Neo, Stream Deck + and the Pedal. Any real Stream Deck on a Mac. |
| [MIDI](../apps/midi.md) | **Input > Input**, tab **MIDI** | Sending MIDI on Windows and Linux; a pretend loop on Linux and on a Mac | Pressing keys on a real MIDI controller |
| [Game controllers](../apps/game-controllers.md) | **Input > Input**, tab **Game controllers** | The controller part starts on Windows and Linux | A real game controller being pressed |
| [Rumble](../platforms/rumble.md) | **Platforms > Rumble** | Reading: followers, live state and chat | Not confirmed: writing in Rumble chat |
| [Cuebot for Mac](../start/installing.md#mac) | Everything | On a Mac with Apple silicon: Cuebot starts and shows its pages, opens the speakers, shows notifications and turns recorded speech into text | A Mac with an Intel chip. Voice Control with a live microphone. Hotkeys, Keyboard press and Mouse click triggers (they need permissions turned on by hand). Signing in to platforms, OBS and other apps from a Mac. |
| [The assistant](../ai/assistant.md) | **Home > Assistant** | Codex, Claude Code, Grok CLI and Antigravity CLI on Linux; an OpenAI key; Ollama with Qwen3.5 | Keys for Claude (Anthropic), Grok (xAI) and Gemini (Google) were only checked with pretend services. LM Studio is not confirmed. |

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
