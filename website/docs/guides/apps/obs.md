# OBS Studio

Connect Cuebot to OBS Studio and your actions can switch scenes, show and hide sources, and start or stop the stream.
OBS events, like "scene changed", can start your actions too. It is on **Apps > Stream apps**.

![Apps > Stream apps](../../assets/screens/page-stream-apps.png)

## What you need

OBS Studio 28 or newer (it has the WebSocket server built in). A few steps need a newer OBS:

| Feature | OBS version |
| --- | --- |
| Most steps and triggers | 28 |
| Set recording folder | 30 |
| Source settings changed and filter settings changed triggers, filter kind suggestions | 30.1 |
| Split recording file, record chapters (chapters need Hybrid MP4 recording) | 30.2 |
| Set deinterlacing | 31.1 |
| Canvases (for example a vertical canvas from a plugin) | 32.1 |

## Connect OBS

1. In OBS, open **Tools > WebSocket Server Settings**, tick **Enable WebSocket server** and click **Show Connect
   Info**.
2. In Cuebot, open **Apps > Stream apps** and the **OBS & Streamlabs Desktop** tab. Under **OBS Studio**, check **Host**
   and **Port** and paste the **Password** from OBS.
3. Click **Connect**, and tick **Connect automatically and reconnect when OBS restarts**.

When OBS is closed, Cuebot tries again after 2 seconds, then waits a little longer each time (up to 30 seconds), so
it finds OBS again soon after you reopen it. When you quit OBS, the **OBS is closing** trigger runs first.

## What you can do

Once connected, the page shows your scenes and sources, whether you are streaming or recording, and buttons like
**Start / stop stream**, **Start / stop recording** and **Save replay**. Your viewers see the stream start or stop
right away, so **Start / stop stream** asks first: click it, then click again within 5 seconds. Your actions can now
switch scenes (**Set scene**), show and hide sources (**Set source visibility**), change text and filters, and much
more. OBS events, like "scene changed" or "stream started", become triggers.

Some ideas:

- **Talking (input level)** runs when your microphone gets loud and again when you go quiet: swap a PNG-tuber image,
  or show a "talking" frame.
- **Source went live / left the live output** runs when a camera or a scene item really appears on stream, for example
  to start a timer when the BRB scene shows.
- **Hide all scene sources**, **Show random scene source** and **Set all filters state** change everything in the
  same video frame, so viewers never see the sources flip one by one.

More steps for things OBS can do:

- **Sources and scenes**: create, rename, duplicate and delete sources and scenes, add a source to a scene or take it
  out, move it in front or behind (**Set source order**), lock it, change its blend mode, and check whether it is live
  (**Is source live**).
- **Audio**: monitoring (hear an alert only in your headphones), balance, sync offset, and **Get audio settings**.
- **Filters**: add, rename, reorder and remove filters.
- **Recording**: **Split recording file** for one file per segment and **Set recording folder** per episode.
- **Transitions**: a fixed transition per scene (**Set scene transition override**), transition settings, the T-bar.
- **More**: **Take screenshot into variable** (no file needed, e.g. to post to Discord), **Send stream caption**,
  projectors, the properties / filters / interact window of a source, hotkeys by key, values kept inside OBS
  (**Save value in OBS**), profiles, video settings, the stream destination and other outputs by name.

**Get stream destination** and **Get output settings** hide the stream key. **Set stream destination** contains your
key in the step itself, so treat actions with that step like a password and never share them.

**A second canvas** (OBS 32.1+, from a plugin such as a vertical canvas): steps that work with scenes and sources have a
**Canvas** field. Leave it empty for your main canvas.

## Renaming in OBS

Steps and triggers find scenes, sources and filters by name. With **Follow renames in my actions** ticked (it is by
default), Cuebot changes the name in your OBS steps and triggers for that connection when you rename something in OBS
while Cuebot is connected. Names that use `%variables%` are left alone. If you rename while Cuebot is not connected,
fix the steps by hand.

**The same source twice in one scene?** **Set source visibility** has **Which copy**: Bottom (the lowest one in the
OBS sources list, as before), Top, or All.

## Talk to overlays and plugins through OBS

- **Send custom OBS event (JSON)** sends a message to every program connected to the OBS WebSocket server, such as
  browser-source overlays that use obs-websocket-js. Messages they send come back as the **Custom OBS event**
  trigger, which can filter on a field such as `type` or `user.name`.
- **Call OBS plugin request (advanced)** asks a plugin that offers WebSocket requests (some scene switcher,
  transition and recording plugins do) to do something. Check the plugin's own help for its name and requests;
  **Plugin message (advanced)** is the trigger for the messages plugins send.

## More than one OBS

**Two computers, or two copies of OBS?** Click **+ Connection** to add another. Give each a **Name**. OBS sub-actions
and triggers then let you pick which OBS to use; empty means the first one.

**OBS on another computer?** Put that computer's address in **Host** instead of `127.0.0.1`, and make sure its firewall
lets the port through. The OBS WebSocket server is not encrypted: keep its password on, only use it on your own
network, and never forward its port on your router.

## When it does not connect

| Cuebot says | What to do |
| --- | --- |
| Could not reach OBS | Start OBS and tick **Enable WebSocket server** in **Tools > WebSocket Server Settings**. Check the host and port. |
| OBS rejected the password | Copy the password again from **Show Connect Info**. Cuebot stops retrying until you change the password, host or port, or click **Connect**. |
| OBS ended the session | Someone clicked disconnect in OBS's **Connected clients** list. Click **Connect**. |
| OBS and Cuebot do not speak the same obs-websocket version | Update OBS to 28 or newer (and Cuebot). |

**Screenshots** are saved by OBS, on the OBS computer, so the file needs a full path like `C:\Shots\shot.png`.

The OBS steps are listed in the [OBS sub-actions](../../reference/sub-actions/obs.md) and the triggers in the
[OBS triggers](../../reference/triggers/obs.md).
