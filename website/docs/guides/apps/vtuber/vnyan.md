# VNyan

Cuebot can run your VNyan node graph, set VNyan parameters, and start Cuebot actions from VNyan. Its card is on
**Apps > Stream apps**, tab **VTuber apps**. New to this? See [VTuber apps](index.md) for what Cuebot can do with each app.

## Run a node graph (API Message)

Nothing needs switching on in VNyan for this.

1. In VNyan's node graph, add an **API Message** node and type a name into it, for example
   `Wave`. Connect whatever should happen after it.
2. In Cuebot, add the step **VNyan > Send to VNyan** to an action and type the same name.
3. Optional: under **Values** add lines like `user=%user%`. The node passes them on; if you type a dictionary name into
   the node, VNyan also stores the values there for other nodes to read.

VNyan's API listens on port 8069. To check the connection, type the name in the VNyan card on the **VTuber apps** tab
and press **Send test**. VNyan only says it got the message, not whether a node used it, so check that your node's
name matches exactly (capital letters matter). The card lists the names your actions use and warns when two differ
only in capital letters.

**Chat messages in values.** Variables are filled in after Cuebot has read your lines, so a chat message with quotes,
`=` or line breaks stays inside its own value. That also works in the JSON form, for example
`{"msg":"%message%"}`.

**Numbers.** VNyan's dictionaries hold text, so Cuebot sends every value as text by default (`"100"`). If your graph
reads the node's **json** output and needs real numbers, set **Numbers** to **As numbers**: values written like `100`,
`-3` or `2.5` then go as numbers (`100`), while `007` or `1,5` stay text. Use a dot for decimals. Values you write as
a JSON object keep the type you gave them (`{"bits":100}` is a number, `{"bits":"100"}` text).

## WebSocket nodes (graphs made for Stream Deck, Streamer.bot or Mix It Up)

Many shared VNyan graphs start with a **WebSocket** node instead of an API Message node. They keep working:

1. In VNyan open **Settings > Misc > Websockets** and tick **Start Receiver**. Note the port.
2. Type that port into **WebSocket port** on Cuebot's VNyan card (VNyan's docs use 8000).
3. Add **VNyan > Send WebSocket command** and type the node's **Command Text**, for example `Throw`. A **Value** such as
   `5` is sent after it (`Throw 5`); the node gives it on as **Value** and `[message]`.

Cuebot keeps this connection open after the first command and opens it again if VNyan was restarted.

## Set VNyan parameters

**VNyan > Set parameter** puts a value straight into a VNyan parameter, with no node needed: graphs read it as
`[name]` (numbers) or `<name>` (text), for example the latest donor's name on a text prop. **Set several parameters**
sends many at once (one `name=value` per line). **Set avatar parameter (VRChat style)** sends
`/avatar/parameters/<name>`, which VNyan also understands.

These steps use OSC. Look up the **OSC Receiver Port** under **Settings > Misc** in VNyan and type it into **OSC port**
on the VNyan card. VNyan's **Monitor** window shows the parameters arriving.

## Start Cuebot actions from VNyan

VNyan's **Websocket Message** node (an action node) sends text to a WebSocket address. Cuebot can listen for it, so a
prop hitting you, an avatar loading, a gesture or a VNyan hotkey can run Cuebot actions (a scene change, a sound, a
chat reply).

1. On the VNyan card tick **Listen for VNyan on port** (8070 unless you change it). The card shows the address, for
   example `ws://127.0.0.1:8070/`.
2. In VNyan add a **Websocket Message** node where something should happen, put that address in, and type the text,
   for example `Hit 5` or `{"event":"Hit","user":"<lastHitter>"}`.
3. In Cuebot add the trigger **VNyan > VNyan message** to an action. Type `Hit` under **Commands** to react only to that
   one.

The action gets `%message%` (the whole text), `%vnyanCommand%` (the first word, or the JSON's `command` or `event`),
`%vnyanValue%` (the rest, a number when it is one) and, for JSON, every key as a variable (`%user%`). Cuebot only
listens on this computer.

Prefer a server you already have? A [custom WebSocket server](../../../reference/triggers/websocket.md) works too: point
the node at it and use its message trigger.

## VNyan on another computer

Type its address under **Host**. VNyan's documentation only talks about the same computer, so this may not work; if
Cuebot says VNyan is not reachable, run Cuebot on the VNyan PC.
