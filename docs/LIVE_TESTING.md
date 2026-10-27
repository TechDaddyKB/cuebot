# Live testing

The normal test suite never touches the network. A separate set of **opt-in** tests drives the real
services end to end through Cuebot's own modules and action engine, exactly as the app does:

| Suite | What it checks | Needs |
| --- | --- | --- |
| `Cuebot.LiveTests` › Twitch chat | Sign-in (device code), EventSub connects, a Helix chat message comes back as a `channel.chat.message` event, `!cuebottest` → command trigger → action → chat reply | Twitch client ID |
| `Cuebot.LiveTests` › Twitch going live | Cuebot starts the stream in OBS → `obs.streamStarted`, Twitch `stream.online` and the online trigger's chat message, healthy output for the configured duration, stop → `obs.streamStopped` and Twitch `stream.offline` | Twitch client ID + stream key, OBS, streaming enabled |
| `Cuebot.LiveTests` › YouTube / Kick | The platform's ingest accepts a stream Cuebot starts and stops: live, data flowing, no reconnects, no dropped frames | Stream key (and Kick ingest URL), OBS, streaming enabled |
| `Cuebot.LiveTests` › Rumble | The Live Stream API URL works (channel name, follower count); when streaming is enabled: Cuebot goes live on Rumble via OBS → `rumble.streamOnline` from the API, the live chat attaches, stop → `rumble.streamOffline` | Rumble API URL; for going live also the Rumble ingest URL + stream key, OBS, streaming enabled |
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
`rumble.streamOnline` event or chat arrives. The live
chat endpoints Cuebot uses are unofficial (see `src/Cuebot.Rumble/PROTOCOL.md`); if the API works but the chat
never attaches, Rumble has probably changed them.

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
