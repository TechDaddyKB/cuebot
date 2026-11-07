# HTTP server

The HTTP server is the simplest door into Cuebot: plain web addresses, no token. Use it to run an action from a web
link or button, to put Cuebot's chat and event docks in OBS, to show your own files, and for
[Decks](../apps/decks.md), buttons on your phone.

## Turning it on

1. Go to **Apps > Servers** and find the **HTTP server** card.
2. Tick **Enabled** and click **Apply and restart servers**.

It listens on `http://127.0.0.1:7474`. Other devices need [Allow LAN connections](servers.md#keeping-it-safe).

## Run an action

Send a `POST` to `/DoAction` (or `/api/DoAction`):

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
(a `GET`) is refused, so a web page cannot run your actions by accident. The same goes for `ExecuteCodeTrigger`.

## Read things

Send a `GET` on `/api/` plus a request name from the [Streamer.bot request list](websocket-api.md#requests), like
`/api/GetActions`, `/api/GetGlobal?variable=deaths` or `/api/GetCredits`. The answers are the same as on the WebSocket.
`SendMessage` works only on the WebSocket.

## OBS docks

The card shows web links for a chat dock (`/feed/chat`) and an event dock (`/feed/events`). Click **Copy**, then in
OBS open **Docks > Custom Browser Docks** and paste the link. You can also add one as a Browser source.

- The docks get their messages from the [WebSocket server](websocket-api.md), so turn that on too.
- Add `?transparent=1` to the end of the link for a see-through background.
- If the WebSocket server asks for a password, add `?password=...` (with your password) to the link.

## Your own files

Click **+ Folder**. Type a web address part, like `/overlays`, and pick a folder on your computer. The files in that
folder now open at `http://127.0.0.1:7474/overlays/...`. Handy for your own overlay pages.

## Decks and the deck token

[Decks](../apps/decks.md) (buttons on your phone) open at `/deck`, and need this server.

### Deck token

When you tick **Allow LAN connections**, Cuebot makes a secret deck token. Other devices then need it to open or press
your decks; the deck links on the **Decks** page and the [phone pairing code](rest-api.md#pair-a-phone) include it,
so you do not have to type it. Decks opened on this computer, like OBS docks, never need it. **New deck token** (in
**Pair a phone**) cancels the old one; paired phones and deck links on other devices must then be set up again.
