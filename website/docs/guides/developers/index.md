# Code and connections

This part is for people who like to tinker. You can write small programs (code) that run inside your actions, or let
other programs, web pages and scripts control Cuebot from outside. You do not need any of it to stream.

## Which one do I need?

- Want code to run **inside** Cuebot, as a step of an action? Start with
  [Writing code (C# and Python)](writing-code.md).
- Want **another program** to control Cuebot, or hear about follows and chat? Start with
  [Cuebot's servers](servers.md). Its [Which door should I use?](servers.md#which-door-should-i-use) table picks the
  right server for you.

## Code inside Cuebot

- [Writing code (C# and Python)](writing-code.md): where the code goes, your first C# and Python, and which toolbox
  to use.
- [Turn an action into code](export-to-code.md): let Cuebot write a whole action out as one C# or Python script.
- [Python in detail](python.md): every `cb` helper, and a named method for every Cuebot function.
- [Using your own code editor](code-editors.md): write your code in Visual Studio Code, Visual Studio, Rider, PyCharm
  or any other editor, with suggestions as you type.

## Connections from outside

- [Cuebot's servers](servers.md): turning servers on, which one to use, and keeping them safe.
- [REST API](rest-api.md): run actions and change almost anything from a script, hear about events, and pair a phone.
- [Streamer.bot WebSocket API](websocket-api.md): use overlays, decks and tools made for Streamer.bot.
- [HTTP server](http-server.md): run actions from a web link, OBS chat and event docks, your own files and decks.
- [UDP server](udp-server.md): run actions from older tools that send tiny messages.
- [Speaker.bot API](speakerbot-api.md): make Cuebot speak from tools made for Speaker.bot.
- [Your own WebSocket servers and clients](websocket-clients.md): connect your actions to tools you build yourself.
- [Linking two Cuebots](linking-two-cuebots.md): send signals between two Cuebots, like a gaming PC and a streaming PC.

AI apps use their own server too: see [Using your own AI app with Cuebot (MCP)](../ai/ai-apps.md).
