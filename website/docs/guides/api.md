# Controlling Cuebot from other programs

Other programs can talk to Cuebot: run an action, send a chat message, read your variables, or hear about every
follow the moment it happens. This page is for people who build their own tools, web pages or scripts. You do not
need any of it to stream.

Each way in is a **server**: a door with a number (a **port**). Turn them on in **Apps > Servers**; see
[Cuebot's servers](servers.md) for the switches and the safety settings.

## Which door should I use?

![Integrations > Servers](../assets/screens/page-servers.png)

| I want to… | Use | Port at the start |
| --- | --- | --- |
| Run actions and change almost anything, from a script or app | [REST API](#rest-api) | 7475 |
| Hear about events (follows, chat, …) as they happen, from a script | [REST API events](#hearing-about-events) | 7475 |
| Use an overlay, deck or tool made for Streamer.bot | [Streamer.bot WebSocket API](#streamerbot-websocket-api) | 8080 |
| Run an action from a simple web link or button | [HTTP server](#http-server) | 7474 |
| Run an action from an old tool that sends tiny messages | [UDP server](#udp-server) | 4242 |
| Make Cuebot speak, from a tool made for Speaker.bot | [Speaker.bot API](#speakerbot-api) | 7580 and 6669 |
| Let an AI app build actions for me | [MCP server](#mcp-server-for-ai-apps) | 7475 |
| Build my own WebSocket server or client inside Cuebot | [Custom WebSockets](servers.md#your-own-websocket-servers-and-clients) | you pick |

Everything listens only on **this computer** (`127.0.0.1`) unless you tick **Allow LAN connections**. Web pages
from other websites are kept out unless you list them. See [Keeping it safe](servers.md#keeping-it-safe).

## REST API

The REST API is the biggest door. It can do nearly everything the app can: actions, commands, timers, variables,
chat, polls, giveaways, the store, media requests, speech, alerts, import and export.

### Turning it on

1. Go to **Apps > Servers** and find the **REST API** card.
2. Tick **Enabled** and click **Apply and restart servers**.
3. Click **Copy** next to **Token**. The token is a long secret password for programs.

!!! warning "Keep the token secret"
    Anyone with the token can control your Cuebot. Do not show it on stream. **Regenerate** makes a new one; the old
    one stops working at once.

Every request needs the token in a header: `Authorization: Bearer YOUR_TOKEN`. Without it, Cuebot answers `401`.

### Your first request

Ask Cuebot how it is doing. Open a terminal (on Windows: **PowerShell**) and type:

=== "Windows (PowerShell)"

    ```powershell
    $headers = @{ Authorization = "Bearer YOUR_TOKEN" }
    Invoke-RestMethod -Uri "http://127.0.0.1:7475/api/v1/status" -Headers $headers
    ```

=== "curl (Linux, macOS, Windows)"

    ```bash
    curl -H "Authorization: Bearer YOUR_TOKEN" "http://127.0.0.1:7475/api/v1/status"
    ```

Cuebot answers with its version, which platforms are connected, and how many actions you have.

### Run an action

This runs the action called `Hello` and gives it the variable `%user%` = `Bob`:

=== "Windows (PowerShell)"

    ```powershell
    $headers = @{ Authorization = "Bearer YOUR_TOKEN" }
    $body = '{"args": {"user": "Bob"}}'
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:7475/api/v1/actions/Hello/run" `
      -Headers $headers -ContentType "application/json" -Body $body
    ```

=== "curl"

    ```bash
    curl -X POST -H "Authorization: Bearer YOUR_TOKEN" -H "Content-Type: application/json" \
      -d '{"args": {"user": "Bob"}}' "http://127.0.0.1:7475/api/v1/actions/Hello/run"
    ```

=== "Python"

    ```python
    import requests

    answer = requests.post(
        "http://127.0.0.1:7475/api/v1/actions/Hello/run",
        headers={"Authorization": "Bearer YOUR_TOKEN"},
        json={"args": {"user": "Bob"}, "wait": True},
    )
    print(answer.json())
    ```

=== "JavaScript"

    ```js
    const answer = await fetch("http://127.0.0.1:7475/api/v1/actions/Hello/run", {
      method: "POST",
      headers: { Authorization: "Bearer YOUR_TOKEN", "Content-Type": "application/json" },
      body: JSON.stringify({ args: { user: "Bob" } }),
    });
    console.log(await answer.json());
    ```

- An action's name with a space goes in the address as `%20`: `actions/Thank%20followers/run`. The action's id
  works too.
- Without `"wait": true`, Cuebot starts the action and answers at once (`202`). With it, Cuebot answers when the action
  is done, and says whether it worked.

### What else it can do

Every address, with what to send and an example, is in the [REST API reference](../reference/rest-api.md). The
short version:

| Address | What it is for |
| --- | --- |
| `/status`, `/openapi.json` | How Cuebot is doing; a full machine-readable description of the API |
| `/actions`, `/actions/{id}`, `/actions/{id}/run`, `/enable`, `/disable` | List, read, create, change, delete, run, switch on and off |
| `/triggers`, `/subactions`, `/scriptfunctions` | What triggers, sub-actions and script functions exist, with their settings |
| `/commands`, `/timers`, `/globals` | Chat commands, timers and global variables |
| `/history` | Recent runs, newest first |
| `/chat` | Read the combined chat, or send a message |
| `/speech`, `/alerts` | Pause, skip and mute text-to-speech and overlay alerts |
| `/polls`, `/predictions`, `/giveaway`, `/store`, `/media` | Cuebot's chat polls, points predictions, giveaways, points store and media requests |
| `/import/preview`, `/import/apply`, `/export` | Bring actions in (also from Streamer.bot), or pack them up to share |
| `/dashboard`, `/decks` | Everything the [phone app](companion-app.md) shows at once (connections, totals, subathon, top spenders, recent activity), and your decks |
| `/subathon/{timer}/{command}`, `/goals/{goal}/{command}` | Start, pause, add time to a subathon timer; add or set points on a goal |
| `/events` | A live stream of everything that happens (below) |

When something is wrong, Cuebot answers with a number and a reason, like
`{"error": "Invalid request", "details": [{"path": "subActions[0].type", "message": "Unknown sub-action type"}]}`.
`400` means "check what you sent", `404` "not found", `409` "not right now" (for example, a poll is already running).

### Hearing about events

`/events` stays open and sends a message every time something happens: a follow, a chat message, an action that
finished. Programmers call this **Server-Sent Events**.

```bash
curl -N -H "Authorization: Bearer YOUR_TOKEN" "http://127.0.0.1:7475/api/v1/events?types=twitch.*"
```

Each event looks like this:

```text
event: twitch.follow
data: {"type":"twitch.follow","time":"2026-11-01T18:00:00+00:00","args":{"user":"Bob", ...}}
```

- `types` picks what you want, separated by commas: `twitch.follow,twitch.cheer`, or `twitch.*` for all of Twitch.
  Leave it out to get everything.
- `action.runCompleted` arrives every time an action finishes.
- A line `: ping` comes every 15 seconds so you know the line is still open.
- At most 16 programs can listen at the same time.

The event names are the trigger names Cuebot uses inside, like `twitch.follow` or `obs.sceneChanged`.
`GET /triggers` lists them all.

## Streamer.bot WebSocket API

Many overlays, decks and tools were made for Streamer.bot. They talk to Streamer.bot's WebSocket server, and Cuebot
has the same server, so they work with Cuebot too.

1. Go to **Apps > Servers**, find **WebSocket server (Streamer.bot API)**, tick **Enabled**, and click **Apply and
   restart servers**.
2. Point the tool at `ws://127.0.0.1:8080/`, the same address Streamer.bot used.

The official `@streamerbot/client` JavaScript library works with Cuebot, and Cuebot's tests check that it does.

### How the messages look

Every message is JSON. You send a **request** with an `id` you make up, and Cuebot answers with the same `id`:

```json
{ "request": "DoAction", "id": "my-1", "action": { "name": "Hello" }, "args": { "user": "Bob" } }
```

```json
{ "status": "ok", "id": "my-1" }
```

A small web page that runs an action and shows every follow:

```js
const ws = new WebSocket("ws://127.0.0.1:8080/");
ws.onopen = () => {
  ws.send(JSON.stringify({ request: "Subscribe", id: "sub", events: { Twitch: ["Follow"] } }));
  ws.send(JSON.stringify({ request: "DoAction", id: "run", action: { name: "Hello" }, args: { user: "Bob" } }));
};
ws.onmessage = (message) => {
  const data = JSON.parse(message.data);
  if (data.event) console.log("Event:", data.event.source, data.event.type, data.data);
  else console.log("Answer:", data);
};
```

### Requests

| Request | What it does |
| --- | --- |
| `GetInfo` | Says who is answering (Cuebot), its version and the computer it runs on. |
| `GetEvents` | Lists every event you can subscribe to, by source (`Twitch`, `YouTube`, `Obs`, …). |
| `Subscribe` / `UnSubscribe` | Starts or stops sending you events. Send `events: { "Twitch": ["Follow", "Cheer"] }`, or `["*"]` for all of one source. |
| `GetActions` | Lists your actions with their id, name, group and whether they are switched on. |
| `DoAction` | Runs an action. Send `action: { "id": "…" }` or `action: { "name": "…" }`, and optional `args`. |
| `GetBroadcaster` | Your accounts on Twitch, YouTube and Kick, and which are connected. |
| `GetActiveViewers` | The viewers Cuebot saw in chat recently. |
| `GetCommands` | Your chat commands. |
| `GetGlobals` / `GetGlobal` | All global variables, or one (`variable: "deaths"`). Add `persisted: false` for the temporary ones. |
| `GetUserGlobals` / `GetUserGlobal` (and the `Twitch…`, `YouTube…`, `Kick…`, `Trovo…` versions) | Viewers' own variables. `Trovo…` always answers empty: Cuebot has no Trovo. |
| `GetCodeTriggers` / `ExecuteCodeTrigger` | Lists the custom triggers your code made, or fires one (`triggerName`, `args`). |
| `GetCredits` / `TestCredits` / `ClearCredits` | The end-of-stream credits, sample credits, or a fresh start. |
| `SendMessage` | Sends a chat message: `message`, `platform` (`twitch`, `youtube`, `kick`), and optional `bot`. |
| `TwitchGetEmotes` / `YouTubeGetEmotes` | Emotes Cuebot knows, including 7TV, BTTV and FFZ. |
| `GetMonitoredYouTubeBroadcasts` | Your YouTube streams Cuebot is watching. |
| `GetUserPronouns` | A viewer's pronouns (`platform`, `userLogin`), from alejo.io. |
| `ClearFirstWordsCache` | Forgets who already chatted today, so "first words" can run again. |

Events arrive as `{ "timeStamp": "…", "event": { "source": "Twitch", "type": "Follow" }, "data": { … } }`. The
names and the data follow Streamer.bot's own lists, so tools made for Streamer.bot find what they expect.

### Passwords

Tick **Require a password** on the server's card and type a **Password**. Cuebot's first message then has
`authentication: { salt, challenge }`, and the tool answers with an `Authenticate` request. Its `authentication`
value is made like this (the same as Streamer.bot):

```text
secret = base64( sha256( password + salt ) )
answer = base64( sha256( secret + challenge ) )
```

With **Require it for every request** unticked, only risky requests need the password: `DoAction`, `SendMessage`,
`ExecuteCodeTrigger`, `ClearCredits` and `ClearFirstWordsCache`.

## HTTP server

The HTTP server is the simplest door: plain web addresses, no token. Turn on **HTTP server** in **Apps > Servers**
(port 7474).

**Run an action** with a `POST` to `/DoAction` (or `/api/DoAction`):

=== "Windows (PowerShell)"

    ```powershell
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:7474/DoAction" -ContentType "application/json" `
      -Body '{"action": {"name": "Hello"}, "args": {"user": "Bob"}}'
    ```

=== "curl"

    ```bash
    curl -X POST -H "Content-Type: application/json" \
      -d '{"action": {"name": "Hello"}, "args": {"user": "Bob"}}' "http://127.0.0.1:7474/DoAction"
    ```

Cuebot answers `204` (done, nothing to say) or `404` when there is no such action. It must be a `POST`: a plain link
(a `GET`) is refused, so a web page cannot run your actions by accident.

**Read things** with a `GET` on `/api/` plus a request name from the [Streamer.bot list above](#requests), like
`/api/GetActions`, `/api/GetGlobal?variable=deaths` or `/api/GetCredits`. The answers are the same as on the WebSocket.
`SendMessage` works only on the WebSocket.

The HTTP server also serves:

- `/feed/chat` and `/feed/events`: chat and event docks for OBS (see [Cuebot's servers](servers.md#http-server));
- `/deck`: [Decks](stream-apps-and-devices.md#decks-buttons-on-your-phone), buttons on your phone;
- your own folders, under the web address you pick with **+ Folder**.

## UDP server

UDP sends one tiny message and does not wait for an answer. Some older tools and game mods use it. Turn on **UDP
server** in **Apps > Servers** (port 4242), then send:

```json
{ "action": { "name": "Hello" }, "args": { "user": "Bob" } }
```

=== "Windows (PowerShell)"

    ```powershell
    $udp = New-Object System.Net.Sockets.UdpClient
    $bytes = [Text.Encoding]::UTF8.GetBytes('{"action": {"name": "Hello"}, "args": {"user": "Bob"}}')
    $udp.Send($bytes, $bytes.Length, "127.0.0.1", 4242) | Out-Null
    ```

=== "Python"

    ```python
    import json, socket

    message = json.dumps({"action": {"name": "Hello"}, "args": {"user": "Bob"}})
    socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(message.encode(), ("127.0.0.1", 4242))
    ```

Cuebot only understands "run this action". Anything else is ignored, and noted in the log.

## Speaker.bot API

Tools and Streamer.bot setups made for Speaker.bot can make Cuebot speak. Tick **WebSocket API** (port 7580),
**UDP API** (port 6669) or both on the **Speaker.bot API** card. Voices and aliases come from the
[Text-to-speech](text-to-speech.md) page.

**WebSocket** messages look like the Streamer.bot ones:

```json
{ "request": "Speak", "id": "1", "voice": "Narrator", "message": "Thanks for the raid!" }
```

| Request | What it does |
| --- | --- |
| `Speak` | Says `message` with the voice alias `voice` (empty for your default). `badWordFilter: true` hides bad words; `wait: true` answers after it finished speaking; `silent: true` makes the sound file without playing it. Answers `success`, `duration` (milliseconds) and `speechFile`. |
| `Stop` / `Clear` | Stops what is speaking now / empties the waiting line. |
| `Enable` (`On`) / `Disable` (`Off`) | Switches speech on or off. |
| `Pause` / `Resume` | Holds the waiting line, or lets it go again. |
| `SetEnabled` / `SetPaused` | `state`: `enabled`, `disabled` or `toggle` / `paused`, `enabled` or `toggle`. |
| `Events` | `state`: `on`, `off` or `toggle`: speaking events (follows, cheers, …) on or off. |
| `Mode` | `mode`: `all` reads all chat, `command` only messages that use the speak command. |
| `ActivateVoiceGate` | Switches to another voice gate `profile`. |
| `GetAliases`, `GetVoices`, `GetState`, `GetInfo` | Lists your aliases or voices, or tells you whether speech is on and paused. |
| `Subscribe` / `UnSubscribe` | Sends you `SpeechStarted`, `SpeechFinished` and `StateChanged` events. |

**UDP** messages use `command` instead of `request`, like `{"command": "speak", "voice": "Narrator", "message":
"Hi"}`. The commands are `speak`, `stop`, `enable` (`on`), `disable` (`off`), `pause`, `resume`, `clear`, `events`
(`state`), `profile` (`profile`), `reg` (`mode` add or del, `user`), and `set` with `method: "nickname"`
(`username`, `nickname`). Speaker.bot's `set sticky` and `assign last` are not supported, and are noted in the log.

## MCP server (for AI apps)

**MCP** is a common language that AI apps, like Claude Desktop or Claude Code, use to work with other programs. With
it, you can ask your AI "make a !lurk command that thanks the viewer" and it builds the action in Cuebot.

- It is on from the start, on this computer only, at `http://127.0.0.1:7475/mcp`, with the REST API's token.
- The **MCP server** card on **Apps > Servers** has ready-made settings text for Claude Code, Claude Desktop and other
  apps. Click **Copy** and paste it into the AI app.
- Apps that start programs themselves (Claude Desktop) run `Cuebot --mcp-stdio`. Cuebot must be running.
- Keep **Confirm destructive tools** ticked: then the AI must ask you before it deletes something, imports, or sends a
  chat message.

The AI sees a list of tools. You can read the same list, with what each tool needs, in
[MCP tools](../reference/mcp-tools.md). See also [AI tools](assistant.md#using-your-own-ai-app-with-cuebot-mcp).

## Inside Cuebot instead

Want the code to run *inside* Cuebot, as a step of an action? See [Writing code (C# and Python)](scripting.md).
