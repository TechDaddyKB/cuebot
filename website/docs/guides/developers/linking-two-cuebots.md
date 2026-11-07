# Linking two Cuebots

Two Cuebots, for example one on your gaming PC and one on your streaming PC, can send each other **signals**. A signal
is a short message with a name, like `go-live`. The other Cuebot can run an action when it arrives. The link goes
through the other Cuebot's [REST API](rest-api.md).

!!! warning "Experimental"
    Linking was tested with two copies of Cuebot inside Cuebot's own tests, but not yet with two real computers. See
    [What's experimental](../help/experimental.md).

## Setting it up

A link works both ways, so only **one** side has to add the other. Here, **this** computer adds the **other** one.

1. On the **other** computer, go to **Apps > Servers**, tick **Allow LAN connections**, turn on the **REST API** with
   its **Address** set to `0.0.0.0`, and click **Apply and restart servers**.
2. On the **other** computer, copy the **Token** from the **REST API** card.
3. On **this** computer, go to **Apps > Servers**. Under **Remote instances**, click **+ Instance**. Fill in:
    - **Name**: a name for the other Cuebot, like `Gaming PC`. Actions use it to choose where to send a signal;
    - **Host or URL**: the other computer's address on your network, like `192.168.1.20`;
    - **API port**: the port from the other Cuebot's REST API card (7475 at the start);
    - **Its API token**: the token you copied.
4. Click **Connect**. Tick **Connect with Cuebot** to connect by itself next time.

**This instance's name** is the name the other Cuebots see. Leave it empty to use the computer's name.

## Sending signals

- In an action, use **Remote › Send remote signal**.
- On the other Cuebot, use the trigger **Remote › Remote signal received**, or the step **Wait for remote signal**.
- The triggers **Remote instance connected** and **disconnected** run when the link comes and goes.
- **Remote signals also release 'Wait for local signal'** (ticked at the start): a signal from the other Cuebot also
  lets actions go on that wait at a **Wait for local signal** step with the same name.
