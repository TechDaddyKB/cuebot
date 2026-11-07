# Spotify

Sign Cuebot in to your Spotify account and it can show the song you play on any device, and add, skip and pause songs
for you. You need this for [song requests](song-requests.md). Spotify asks every streamer to make their own small
"app" on Spotify's developer site first. It is free, it is only for you, and you do it once (about two minutes). You
need to log in with the Spotify account you stream with.

!!! info "Why your own app?"
    Since 2026 a Spotify app that is "in development mode" may only be used by 5 people, and the person who made it
    needs Spotify Premium. One shared Cuebot app would stop working after five streamers, so each streamer makes their
    own. You do not need to send it to Spotify for review.

!!! warning "Experimental"
    **Spotify** (showing the song and song requests) is built and tested by Cuebot's own tests, but not yet with a real
    Spotify account. See [What's experimental](../help/experimental.md).

## 1. Make the Spotify app

1. In Cuebot, open **Audio > Music** and click **Open Spotify for Developers** (or go to
   [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard)).
2. Log in with your Spotify account. The first time, Spotify asks you to accept the developer terms: tick the box and
   click **Accept the terms**. (If Spotify asks you to verify your email address, do that first.)
3. Click **Create app**.
4. Fill in the form:
    - **App name**: anything, like `Cuebot for <your name>`.
    - **App description**: anything, like `Song requests for my stream`.
    - **Website**: leave it empty.
    - **Redirect URIs**: in Cuebot click **Copy redirect address**, paste it here and click **Add**. It looks like
      `http://127.0.0.1:43117/callback`.
    - **Which API/SDKs are you planning to use?**: tick **Web API**.
5. Tick that you agree with the terms and click **Save**.

!!! warning "127.0.0.1, not localhost"
    Spotify only accepts the address with `127.0.0.1`. If you type `localhost` yourself, sign-in fails with
    "INVALID_CLIENT: Invalid redirect URI". Copy the address from Cuebot and it is right.

## 2. Copy the Client ID into Cuebot

1. Your new app opens on Spotify's site. Click **Settings**.
2. Under **Basic Information** you see **Client ID**: 32 letters and numbers. Copy it.
3. In Cuebot, paste it into **Client ID**.

You do **not** need the **Client secret**. Leave it alone and never paste it anywhere.

## 3. Sign in

1. In Cuebot click **Sign in**. Your browser opens Spotify.
2. Spotify shows what Cuebot may do (see what you play, control your player). Click **Agree**.
3. The browser says "Cuebot is signed in to Spotify. You can close this tab." Close it.

The Music page now says **Signed in**. To test: open Spotify, start any song, type a song name into **Try it** and click
**Add a song**. It should show up in Spotify's queue.

Showing the song you play works like on the [Now playing](now-playing.md) page. To let viewers add songs, see
[Song requests](song-requests.md).

## Troubleshooting

| What you see | What to do |
| --- | --- |
| **"Your Spotify account is not allowed in your Spotify app yet"** | Sign in with the same Spotify account that made the app. To use another account, open your app on developer.spotify.com, go to **User Management** and add that account's name and email. |
| **"INVALID_CLIENT: Invalid redirect URI"** in the browser | The redirect address in your Spotify app does not match. In Cuebot click **Copy redirect address**, then on developer.spotify.com open your app, **Settings > Edit**, remove the old address, paste the new one, click **Add** and **Save**. |
| **"Another program already uses port …"** | Pick another **Port** on the Music page (any number from 1024 to 65535), copy the redirect address again and change it in your Spotify app too. |
| **"Spotify sign-in ran out"** / **Needs attention** | Spotify ends sign-ins after a while (about six months), or you removed Cuebot under Manage apps. Click **Sign in** again. |

To disconnect Spotify completely, click **Sign out** in Cuebot, then on [spotify.com](https://www.spotify.com/account/)
open **Account > Manage apps** and remove your app.
