# Alerts

An **alert** is the pop-up on stream when someone follows, subs, cheers, raids or tips: a picture, a sound and a
line of text. In Cuebot, alerts come from the **Alert box** widget on one of your [overlays](overlays.md).

## The Alert box

The **Alert box** widget shows alerts by itself. You do not need an action for that. Add it to an overlay with
**Add Cuebot widget > Alert box** (see [Add widgets](overlays.md#add-widgets)).

It has a design for each alert type: **Follow**, **Subscriber**, **Tip / donation**, **Cheer (bits)**, **Raid**,
**Super Chat**, **Kicks**, **Channel points**, **YouTube Jewels**, **Charity donation**, **Merch** and **Custom
(actions)**. Each type can have **variations**, for example a bigger alert for 1000 bits.

Alerts come from every platform and service Cuebot is connected to: Twitch, Kick, Rumble, YouTube, StreamElements,
Streamlabs and the other donation services, Ko-fi (tips, memberships, shop orders, commissions), Patreon (a free member
joining is a **Follow**, a new or upgraded pledge a **Subscriber** alert with the tier and monthly price; a downgrade
is no alert), Fourthwall (donations, orders, stream gifts) and Shopify (paid orders).

## Missed alerts (catch-up)

When Cuebot was disconnected or closed, it catches up afterwards: Streamlabs and StreamElements donations it fetches
after a reconnect, Ko-fi payments your relay kept, orders and donations Fourthwall or Shopify catch-up finds, and
Patreon's backlog after a paused webhook. These still count in event lists, goals, stream totals and top spenders, but
the alert box does **not** pop them up, so twenty old alerts never play in a row. To show them anyway, tick **Show
catch-up alerts** at the top of the alert box's editor.

A Pally.gg **Replay Alert** always plays: that is you asking to see it again.

## Try an alert

Open the alert box's editor (**Edit…** on its row) and click **Test this design**. Test events only go to that
preview, never to your live overlays. The **Emulate** list there sends a sample event from any platform or service
(for example a Ko-fi membership or a Shopify order) through the same steps as a real one.

## Pause, skip, mute

The **Alerts** card on the Overlays page has **Pause alerts**, **Skip alert** and **Mute alerts**. They work for every
overlay at once.

## Alerts from your own actions

Want an alert for something Cuebot has no alert for, like a `!hype` command? Add the **Show alert** sub-action to an
action (see [How actions work](../actions/how-actions-work.md)):

- **Design**: which alert design to use, for example `custom`.
- **Name**: who it is about. It starts as `%user%`.
- **Message**, **Amount**, **Currency**: what the alert says.

Your alert waits its turn behind other alerts, just like a real one. The **Pause, skip or mute overlay alerts**
sub-action can pause, skip or mute alerts from an action, for example while you are on your "BRB" scene.

## Mods and alert designs

With the [mod portal](../mods/mod-portal.md), you can let mods change each alert's text, picture or video, sound, look
and text-to-speech, and turn alerts and their variations on or off.
