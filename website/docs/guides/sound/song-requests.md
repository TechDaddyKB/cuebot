# Song requests

Let your viewers put songs in your Spotify queue with a `!sr` command. Viewers type `!sr never gonna give you up` (or
paste a Spotify song link), and the song goes into your queue. You need to [sign in to Spotify](spotify.md) first, and
adding songs needs **Spotify Premium**.

!!! warning "Media Requests can also listen for `!sr`"
    Cuebot's **Media Requests** page ([YouTube videos](media-requests.md)) also uses `!sr` once you tick **Taking
    requests** there. It starts switched off. If you turn it on and also use `!sr` for Spotify, change one of the two
    words, for example Media Requests' **Request** word to `!video`. Otherwise one `!sr` does both.

## Make the !sr command

1. Go to **Automation > Commands**, click **+**, name it `sr` and type `!sr` under **Commands**. Set a cooldown so one
   viewer cannot fill your queue, for example a **Per user** cooldown of 120 seconds.
2. Go to **Automation > Actions**, make an action called `Song request` and add the trigger
   **Commands › Command triggered** with the command `sr`.
3. Click **+ Sub-action** and pick **Spotify › Add song to Spotify queue**. Leave **Song** on `%rawInput%` (what the
   viewer typed after `!sr`).
4. This step has two lists: **Added** and **Not added**. Select **Added** (the word **BRANCH** shows up) and add
   **Send chat message (any platform)** with:

    ```text
    @%user% added %spotify.title% by %spotify.artist% 🎶
    ```

5. Select **Not added** and add another **Send chat message (any platform)** with:

    ```text
    @%user% sorry, that did not work: %spotify.error%
    ```

Tick **Reply on the platform the trigger came from** in both messages.

Viewers can send:

- a song name: `!sr bohemian rhapsody queen`. Cuebot takes Spotify's first search result, like typing it into
  Spotify's search box. Adding the artist helps.
- a Spotify song link: `!sr https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT`. Album and playlist links do not
  work, only songs.

## Other Spotify steps

| Step | What it does |
| --- | --- |
| **Spotify › Skip song** | Next song. Good for a `!skip` command for moderators, or a [Stream Deck](../apps/stream-deck.md) button. |
| **Spotify › Previous song** | Back one song. |
| **Spotify › Pause / resume** | Pause, play, or switch between the two. |
| **Spotify › Set volume** | Volume from 0 to 100, like `!volume 30` with `%input0%`. Some devices (many phones) do not allow it. |
| **Now playing › Media control** | Play/pause, next, previous on any player, including players on this computer. |

## Troubleshooting

| What you see | What to do |
| --- | --- |
| **"Open Spotify on a device first and play a song"** | Spotify only takes commands while it is open somewhere. Open Spotify (PC, phone or web player), play any song, then try again. |
| **"Spotify needs Premium to change songs"** | Adding to the queue, skipping, pausing and volume need Spotify Premium on the account you signed in with. Showing the song works without it. |
| **"Spotify found no song for …"** | Spotify's search found nothing. Try other words, add the artist, or send a song link. |
| **"Spotify is busy. Try again in … seconds."** | Spotify asks apps to slow down sometimes. Wait the few seconds. A command cooldown helps. |

Sign-in problems are on the [Spotify](spotify.md#troubleshooting) page.
