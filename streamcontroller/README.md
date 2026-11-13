# Cuebot for StreamController

A [StreamController](https://github.com/StreamController/StreamController) plugin for Cuebot, for Linux users who
run their Stream Deck with StreamController instead of Cuebot's direct USB mode. User guide:
`website/docs/guides/apps/streamcontroller.md`.

- It speaks the same protocol as Cuebot's Elgato plugin (`streamdeck/`), described in
  [`src/Cuebot.StreamDeck/PROTOCOL.md`](../src/Cuebot.StreamDeck/PROTOCOL.md): one WebSocket to
  `ws://host:7475/api/v1/streamdeck` with the REST API token. So Cuebot lists it on the Stream Deck page and the
  Stream Deck sub-actions reach its keys by button id. Its `hello` says `app.name = "StreamController"` and
  `plugin.uuid = "io.github.cuebot.streamcontroller"`.
- Keys: **Run Cuebot action**, **Toggle action enabled**, **Set global variable** (keys and dials) and **Cuebot
  trigger** (keys and dials), like the Elgato plugin.
- `io_github_cuebot_Cuebot/cuebot_core.py` is the connection (websocket-client, which StreamController ships) and the
  key registry; it does not import StreamController or GTK and is tested by `tests/` with pytest.
  `main.py` is the StreamController side (plugin, key classes, settings UI).
- Icons are the Elgato plugin's SVGs rendered to PNG (`rsvg-convert -w 144`).

## Develop

```bash
cd streamcontroller
python -m pytest -q tests          # needs pytest, websocket-client, requests
scripts/pack.sh                    # dist/Cuebot-StreamController-plugin.zip
```

To try it, copy (or symlink) `io_github_cuebot_Cuebot` into StreamController's `data/plugins` folder and restart
StreamController. A test copy that leaves your own setup alone: `streamcontroller --data <empty folder>`. For a demo
Cuebot to connect to, run `tests/Cuebot.UiTests` `StreamControllerSession` (see its comment): it starts the REST API
with a token you choose, logs every run and event, and takes the documentation screenshots.

Release: the build workflow's `streamcontroller-plugin` job tests and packs it; releases attach
`Cuebot-StreamController-plugin.zip`.
