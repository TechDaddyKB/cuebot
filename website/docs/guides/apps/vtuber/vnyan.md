# VNyan

Cuebot can run your VNyan node graph with the **Send to VNyan** step. You switch nothing on in VNyan: you add an **API Message** node in its node graph. Its card is on **Apps > Stream apps**, tab **VTuber apps**. New to this? See
[VTuber apps](index.md) for what Cuebot can do with each app.

1. In VNyan's node graph, add an **API Message** node and type a name into it, for example
   `Wave`. Connect whatever should happen after it.
2. In Cuebot, add the step **VNyan > Send to VNyan** to an action and type the same name.
3. Optional: under **Values** add lines like `user=%user%`. The node passes them on; if you type a dictionary name into
   the node, VNyan also stores the values there for other nodes to read.

VNyan's API listens on port 8069. To check the connection, type the name in the VNyan card on the **VTuber apps** tab
and press **Send test**. VNyan only says it got the message, not whether a node used it, so check that your node's
name matches exactly (capital letters matter).
