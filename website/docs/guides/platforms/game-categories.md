# Game categories

Cuebot sees which Steam game you are playing. When you start one, it can set your stream category to that game on
Twitch and Kick (and "Gaming" on YouTube). It **asks first**: nothing changes until you, or a mod you trust, says yes.

Find it under **Platforms > Game categories**.

## Game detection

Tick **Watch Steam for games**. The page then says what you are playing, like *Playing now: Helldivers 2*.

Cuebot reads what Steam itself records about running apps, so it works for every Steam game, on Windows, macOS and
Linux (including games that run through Proton).

**Never a game.** Streaming tools come from Steam too: VTube Studio, VBridger, Wallpaper Engine and others. Steam
reports them as running just like a game. The list under **Never a game** keeps them out. It starts with the common
ones; add your own, one per line:

- a name, as Steam shows it: `VTube Studio`
- a name with `*` as a wildcard: `Live2D Cubism*`
- a Steam app id (the number in the game's store address): `1325860`

A game that is gone for a few seconds (a quick restart) does not count as closed. If you start a second game, the new
one is the game you play.

The triggers **Game started (Steam)** and **Game stopped (Steam)** run your actions too, for example to change the
OBS scene when a game starts. See [Games triggers](../../reference/triggers/games.md).

## Switching the stream category

Tick **Ask to switch the category when a game starts**, and the platforms to switch on:

| Platform | What changes |
| --- | --- |
| **Twitch** | The game (category) of your channel. |
| **Kick** | The category of your channel. |
| **YouTube (Gaming)** | YouTube only has broad categories, so a game sets **Gaming** on your live broadcast (or the next upcoming one). Off at first. |
| Rumble | Rumble has no way for apps to change the category, so it is not listed. |

Cuebot only asks when a platform **has** a category for the game. A tool that slipped past the **Never a game** list
(say, an art program) has no category, so nobody is bothered. It also doesn't ask when the category is already right.

### Who answers, and where

The question, like *Switch the stream category to Helldivers 2? Twitch: Helldivers 2 · Kick: HELLDIVERS 2*, shows
up in every one of these places at once:

- **In Cuebot:** a strip across the top of the window with **Switch**, **Always for this game** and **Ignore**, and a
  notification.
- **In the [mod portal](../mods/mod-portal.md):** a **Needs a yes** card at the top of the Home page, for mods whose
  role may control the stream (**Lead Mod** and **Tech Lead** at first).
- **In the [phone app](../mods/phone-app.md):** on your own paired phone or tablet, under **Waiting for your OK** on
  the Mods tab, with a notification. A mod using the app gets a notification and answers on the portal's Home page.

The **first answer wins**, and the question goes away everywhere. Cuebot's log notes who answered. If nobody
answers within **10 minutes** (you can change it), the question expires and nothing changes. If you close the game
first, the question is taken back.

- **Switch**: sets the category now.
- **Always for this game**: sets it now, and from now on switches for this game without asking. The game goes on the
  **Always switch** list, where you can take it off again.
- **Ignore**: changes nothing.

### Other settings

- **Only while live**: off at first, so the category is right before you go live, too.
- **When the game closes, ask to switch back to**: for example `Just Chatting`. Empty means Cuebot doesn't ask.
- **Never ask for**: games that keep your category as it is.
- **When the platform's name differs**: one per line, like `Counter-Strike 2 = Counter-Strike`. Cuebot already
  matches names without caring about capitals, spaces and symbols (*HELLDIVERS™ 2* is *Helldivers 2*).

### Try it

Type a game's name under **Try it** and click **Ask now**. Cuebot acts as if that game just started: the question
shows up everywhere it would on stream. Answer **Ignore** if you don't want the category changed.

The **Request answered** trigger runs when anyone answers, so an action can thank the mod or post the new category in
chat.
