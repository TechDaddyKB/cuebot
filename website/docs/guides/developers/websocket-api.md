# Streamer.bot WebSocket API

Many overlays, decks and tools were made for Streamer.bot. They talk to Streamer.bot's WebSocket server (a live,
two-way connection), and Cuebot has the same server, so they work with Cuebot too. For example the Warudo
[Streamer.bot plugin](../apps/vtuber/warudo.md).

## Turning it on

1. Go to **Apps > Servers**, find **WebSocket server (Streamer.bot API)**, tick **Enabled**, and click **Apply and
   restart servers**.
2. Point the tool at `ws://127.0.0.1:8080/`, the same address Streamer.bot used.

Port **8080** and **Endpoint** `/` are what Streamer.bot used, so most tools work without changes. **Connected
clients** on the card shows which tools are connected right now. Other computers need
[Allow LAN connections](servers.md#keeping-it-safe).

The official `@streamerbot/client` JavaScript library works with Cuebot, and Cuebot's tests check that it does.

## How the messages look

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

## Requests

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

## Passwords

Tick **Require a password (Streamer.bot authentication)** on the server's card and type a **Password**. The tool must
then know the password. Cuebot's first message then has
`authentication: { salt, challenge }`, and the tool answers with an `Authenticate` request. Its `authentication`
value is made like this (the same as Streamer.bot):

```text
secret = base64( sha256( password + salt ) )
answer = base64( sha256( secret + challenge ) )
```

With **Require it for every request** unticked, only risky requests need the password: `DoAction`, `SendMessage`,
`ExecuteCodeTrigger`, `ClearCredits` and `ClearFirstWordsCache`.
