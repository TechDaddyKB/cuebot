# Live testing

The normal test suite never touches the network. A separate set of **opt-in** tests drives the real
services end to end through Cuebot's own modules and action engine, exactly as the app does:

| Suite | What it checks | Needs |
| --- | --- | --- |
| `Cuebot.LiveTests` › Twitch chat | Sign-in (device code), EventSub connects, a Helix chat message comes back as a `channel.chat.message` event, `!cuebottest` → command trigger → action → chat reply | Twitch client ID |
| `Cuebot.LiveTests` › Twitch going live | Cuebot starts the stream in OBS → `obs.streamStarted`, Twitch `stream.online` and the online trigger's chat message, healthy output for the configured duration, stop → `obs.streamStopped` and Twitch `stream.offline` | Twitch client ID + stream key, OBS, streaming enabled |
| `Cuebot.LiveTests` › YouTube / Kick | The platform's ingest accepts a stream Cuebot starts and stops: live, data flowing, no reconnects, no dropped frames | Stream key (and Kick ingest URL), OBS, streaming enabled |
| `Cuebot.LiveTests` › Rumble | The Live Stream API URL works (channel name, follower count); when streaming is enabled: Cuebot goes live on Rumble via OBS → `rumble.streamOnline` from the API, the live chat attaches, stop → `rumble.streamOffline` | Rumble API URL; for going live also the Rumble ingest URL + stream key, OBS, streaming enabled |
| `Cuebot.LiveTests` › YouTube API | Device-code sign-in (approved once), connection and channel info; with `CUEBOT_LIVE_YOUTUBE_CHAT=1` while live: a sent message comes back through the chat poll | Google OAuth client ID + secret; for chat a live broadcast |
| `Cuebot.LiveTests` › Kick API | OAuth sign-in (browser, once), the official API reads the channel, a chat message sent through the API comes back through the real-time (Centrifugo) chat as `kick.chatMessage` | Kick app Client ID + Client Secret |
| `Cuebot.LiveTests` › Meld Studio / PolyPop | Meld: WebChannel handshake, scene list, re-show the current scene, optionally toggle a named layer and restore it (the session update must come back). PolyPop: the WebSocket plugin connects to Cuebot and a test alert is delivered | Meld Studio with remote connections on; PolyPop with the WebSocket plugin |
| `Cuebot.Obs.Tests` › `LiveObsTests` | Scenes, sources, text, events and a trigger → action → OBS round trip against a running OBS (creates and removes "Cuebot Test" scenes) | OBS |
| `Cuebot.Scripting.Tests` › Python | The real Python worker: arguments, host calls, `print` capture, tracebacks, timeouts | A Python interpreter |

Every test **skips** unless its settings are present, so CI and normal `dotnet test` runs are unaffected. The
`Cuebot.LiveTests` suite additionally runs only when `CUEBOT_LIVE_TESTS=1` is set by the caller (the runner scripts
do this; it is never read from `live.env`), so having a filled-in `live.env` can't make an ordinary `dotnet test`
of the solution go live. To run that suite directly, set `CUEBOT_LIVE_TESTS=1` yourself.

## Setup

1. Copy [`docs/live.env.example`](live.env.example) to `~/.cuebot/live.env` (on Windows
   `%USERPROFILE%\.cuebot\live.env`) and fill in what you want tested. The file stays outside the repository;
   `live.env` is also git-ignored. Stream keys are only ever handed to OBS and are never logged.
2. **Twitch app**: register one at <https://dev.twitch.tv/console> with category *Chat Bot*, client type
   **Public**, redirect `http://localhost`, and put its Client ID in `CUEBOT_LIVE_TWITCH_CLIENT_ID`.
3. **OBS**: enable the websocket server (Tools → WebSocket Server Settings) and create a scene for test
   streams, e.g. "Cuebot Dev Testing" showing a card that says it is an automated test. Name it in
   `CUEBOT_LIVE_OBS_SCENE`.
4. **Platforms**: set each stream's title to something like "dev testing" beforehand; the tests stream
   whatever title the platform has. For YouTube use a reusable **stream key**, not OBS's "Connect Account"
   (that needs the Manage Broadcast dialog), and consider making it Unlisted.
5. Going live is public, so nothing streams until you set `CUEBOT_LIVE_STREAM=1`.

### Rumble

Put your Live Stream API URL (rumble.com → Account → Live Stream API; it contains a private key) in
`CUEBOT_LIVE_RUMBLE_API_URL`. That alone runs the read-only test. For the going-live test also set
`CUEBOT_LIVE_RUMBLE_RTMP_URL` and `CUEBOT_LIVE_RUMBLE_STREAM_KEY` from Rumble's stream setup page. Rumble's API
reports a stream going live or ending with a delay, so that test allows up to five minutes for each.

**The stream and the API key must belong to the same owner.** Rumble API keys are scoped to either your user
profile or one of your channels (the API response's `type` field says which). In the stream setup (Live Streams →
Edit → Settings → *Channel*) pick that same profile or channel, and set Visibility to Public. Otherwise Rumble
accepts the stream (the dashboard shows it LIVE) but the Live Stream API never lists it, so no
`rumble.streamOnline` event or chat arrives. With a **static stream key**, Rumble starts every session as a new stream
built from the key's template (Live Streams → Templates / Static Stream Keys), so set the channel and visibility on the
template itself: editing one stream only fixes that session. The live
chat endpoints Cuebot uses are unofficial (see `src/Cuebot.Rumble/PROTOCOL.md`); if the API works but the chat
never attaches, Rumble has probably changed them.

### YouTube API

Create an OAuth client of type **TVs and Limited Input devices** in your own Google Cloud project (YouTube Data
API v3 enabled; steps in `src/Cuebot.YouTube/YOUTUBE.md`) and put its ID and secret in
`CUEBOT_LIVE_YOUTUBE_CLIENT_ID` / `CUEBOT_LIVE_YOUTUBE_CLIENT_SECRET`. The first run prints a device code (also in
`<data folder>/youtube/sign-in.txt`) and waits up to five minutes for approval; the sign-in is kept for later runs.
The read-only test costs a few quota units. The chat round trip (`CUEBOT_LIVE_YOUTUBE_CHAT=1`) needs a broadcast
that is live (started by you, or by the YouTube going-live test) and posts one visible "dev testing" message
(50 units).

### Kick

Create a Kick app (kick.com → Settings → Developer), register the redirect URL `http://localhost:47582/callback`
(or set `CUEBOT_LIVE_KICK_REDIRECT_PORT` and register that port instead), and put its Client ID and Client Secret
in `CUEBOT_LIVE_KICK_CLIENT_ID` / `CUEBOT_LIVE_KICK_CLIENT_SECRET`. The first run opens the browser for the OAuth
sign-in; the tokens are kept in `CUEBOT_LIVE_DATA/kick/kick.json` for later runs. The real-time chat uses Kick's
unofficial website websocket (see `src/Cuebot.Kick/PROTOCOL.md`); if kick.com blocks the automatic channel-id
lookup, set `CUEBOT_LIVE_KICK_CHANNEL_ID` and `CUEBOT_LIVE_KICK_CHATROOM_ID`. The lookup goes through Cloudflare
and is often blocked; open `https://kick.com/api/v2/channels/<your slug>` in a browser and copy `id` and
`chatroom.id`. The sign-in URL is also printed, in case the browser that opens is not the one you are signed in to.

### Webhook relay

Deploy the relay once (`relay/setup.ps1` or `relay/setup.sh`, see `relay/README.md`) and put the three values it prints in
`CUEBOT_LIVE_RELAY_URL`, `CUEBOT_LIVE_RELAY_CONNECT_TOKEN` and `CUEBOT_LIVE_RELAY_ROUTE_SECRET`. `RelayLiveTests` connects to it,
posts a custom webhook to the public URL with a throw-away hook, checks the trigger fires with the posted fields, and
checks that a wrong hook token is rejected in Cuebot. Nothing reaches viewers.

### Streamlabs, StreamElements, VTube Studio, Voicemod

`ServicesLiveTests` connects to each service whose setting is filled in, checks it answers, and disconnects;
nothing reaches viewers. Streamlabs needs `CUEBOT_LIVE_STREAMLABS_SOCKET_TOKEN`, StreamElements
`CUEBOT_LIVE_STREAMELEMENTS_JWT`, Voicemod (running) `CUEBOT_LIVE_VOICEMOD_API_KEY`. VTube Studio runs with
`CUEBOT_LIVE_VTS=1` and the API turned on (Settings → "Start API"); the first run waits up to two minutes for you to
click **Allow** on VTube Studio's plugin popup, and the plugin token is kept for later runs.

### StreamLoots, Crowd Control

`InteractiveLiveTests` connects read-only and disconnects; nothing is sent. StreamLoots needs
`CUEBOT_LIVE_STREAMLOOTS_ALERT_URL` (your Alerts widget URL); the test keeps the alerts stream open through one 30 s
keep-alive. StreamLoots opens the stream for any id, so the test proves the endpoint, not your id: click **Test** on
the StreamLoots alerts page while Cuebot is connected to see a real alert arrive. Crowd Control needs
`CUEBOT_LIVE_CROWDCONTROL_OVERLAY_URL` (Overlays & Assets in the Crowd Control app); it passes only when Crowd Control
accepts the overlay key for the private topic.

### TipeeeStream, DonorDrive, TreatStream, Pally.gg

`CharityLiveTests` connects to each of these whose variable is set, checks it answers and disconnects; nothing reaches
viewers. TipeeeStream needs `CUEBOT_LIVE_TIPEEESTREAM_API_KEY` (optional `CUEBOT_LIVE_TIPEEESTREAM_USERNAME`).
DonorDrive needs only a public `CUEBOT_LIVE_DONORDRIVE_PARTICIPANT_ID` (Extra Life by default; `CUEBOT_LIVE_DONORDRIVE_TEAM=1`
for a team id, `CUEBOT_LIVE_DONORDRIVE_BASE_URL` for another DonorDrive charity) and makes two or three requests.
TreatStream needs `CUEBOT_LIVE_TREATSTREAM_TOKEN` (an access token from signing in on the Donations page) and
`CUEBOT_LIVE_TREATSTREAM_CLIENT_ID` (your TreatStream app). Pally.gg needs `CUEBOT_LIVE_PALLY_API_KEY`; the test also asks
Pally's server to echo a sample tip and checks it arrives as a test campaign tip.

### Meld Studio, PolyPop

`StreamAppsLiveTests` (nothing goes live). **Meld Studio** runs with `CUEBOT_LIVE_MELD=1`: turn on File → Preferences →
Advanced → "Allow remote connections" (Meld listens on 13376; `CUEBOT_LIVE_MELD_HOST` for another PC). The test
connects, checks the scene list and re-shows the current scene; with `CUEBOT_LIVE_MELD_LAYER=<layer name>` it also
toggles that layer, waits for Meld's session update and restores it. **PolyPop** runs with `CUEBOT_LIVE_POLYPOP=1`:
install the community WebSocket plugin, add a WebSocket source with Port 9652 (or `CUEBOT_LIVE_POLYPOP_PORT`) and a
WebSocket Alert named `Cuebot Test` (or `CUEBOT_LIVE_POLYPOP_ALERT`). Cuebot listens and waits up to 90 s for the
plugin (it retries every 5 s), then sends the alert with `<message>` = "dev testing".

## Running

```powershell
./build/run-live-tests.ps1
```

```bash
build/run-live-tests.sh
```

Pass a name fragment to run a subset, e.g. `./build/run-live-tests.ps1 -Filter Kick`. Reports (`.trx` and
console logs) go to `artifacts/live-tests/`.

**First run:** the Twitch tests open the device-activation page in your browser and wait up to five minutes
for you to approve "Cuebot" (the code is also written to `<data folder>/sign-in.txt`). The sign-in is kept in
`CUEBOT_LIVE_DATA` (default: `<temp>/cuebot-live-tests`), so later runs, including unattended ones, need no
approval. To revoke it, delete that folder and disconnect the app on <https://www.twitch.tv/settings/connections>.

## Unattended runs

With the env file filled in and the Twitch sign-in approved once, the runner needs no interaction, so it
can run from Task Scheduler / cron. Keep in mind:

- Every Twitch go-live sends your followers a notification. Schedule sparingly.
- Each test stream lasts `CUEBOT_LIVE_STREAM_SECONDS` (default 60) and each platform is streamed once per run.
- The tests point OBS at each platform with a custom RTMP server and restore your previous OBS stream
  settings afterwards. If a run is killed mid-stream, check OBS's Settings → Stream.
- OBS must be running with the websocket enabled; the scene named in `CUEBOT_LIVE_OBS_SCENE` must exist.
