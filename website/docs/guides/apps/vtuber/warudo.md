# Warudo

Cuebot can run Warudo blueprints, flip toggles and send text. It starts actions when a toggle changes. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

Cuebot uses the same connection as Warudo's built-in Stream Deck support, so you need no mod.

1. In Warudo, open a blueprint and add one of these nodes. Type a **Receiver Name** into it, for example `Confetti`:
    - **On Stream Deck Trigger**: runs once each time Cuebot sends it.
    - **On Stream Deck Toggle**: has an On and an Off path, for things like showing a hat.
    - **On Stream Deck Message**: runs and passes on text, for example a viewer's name.
2. In Cuebot press **Connect** in the Warudo card. Your receiver names appear in a list with a test button each.
3. Use the steps **Warudo > Trigger Warudo blueprint**, **Warudo toggle on/off** or **Send message to Warudo**.

Warudo only opens this connection once the open scene has at least one of these nodes. If Cuebot says Warudo is not
reachable, add a node first, then press **Connect** again. After adding more nodes, press **Reload receivers**.

## Warudo and the Streamer.bot plugin

The community **StreamerBot Integration** plugin for Warudo ("Do StreamerBot Action" node) also works with Cuebot. Turn
on Cuebot's Streamer.bot-compatible WebSocket server (**Apps > Servers**, port 8080; see [WebSocket API](../../developers/websocket-api.md)) and point the plugin's StreamerBot
asset at it. Its node lists your Cuebot actions and runs them.
