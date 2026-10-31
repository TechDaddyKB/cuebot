# The setup assistant and AI tools

Cuebot has a built-in **assistant**: a chat window where you type what you want, in plain words, and an AI (a computer
program that understands text) sets it up for you.

> "Make a !discord command that posts my Discord link, with a 30 second cooldown."

The assistant then makes the command and the action, just as if you had clicked through the pages yourself.

!!! note "The assistant is only for setting things up"
    It helps *you* build things in Cuebot. It never talks to your viewers, and your actions cannot use it.

## What you need

The assistant does not have its own AI. It borrows one you already use. Pick **one** of these:

- **An AI app you are already signed in to** on this computer: **Codex** (with a ChatGPT sign-in), **Claude Code**,
  **Grok CLI** or **Antigravity CLI**. Cuebot uses that app's sign-in, so you do not paste any key.
- **An API key**: a secret code from an AI company that lets programs use its AI. Cuebot works with keys for
  **Claude (Anthropic)**, **OpenAI**, **Grok (xAI)** and **Gemini (Google)**. The company may charge you for what you
  use.

!!! note "Flatpak on Linux"
    The Flatpak version of Cuebot cannot reach other apps on your computer, so it needs an API key.

## Setting it up

1. Go to **Home > Assistant**.
2. In **AI**, pick your choice. **Automatic (first one ready)** uses the first one that works.
3. Using an AI app? Click **Sign in** if Cuebot asks you to, then **Refresh**. **Program** is usually found by itself.
4. Using an API key? Paste it into **API key**. It is saved in Cuebot's data folder and hidden on screen.
5. Optional: pick a **Model** (the AI's "brain" size). Leave it on **Default** if you are unsure. **List models** shows
   the choices.

## Using it

Type what you want and click **Send**. Some ideas:

- "Make a !lurk command that says thanks for lurking, %user%."
- "Post my socials in chat every 20 minutes, but only if people are chatting."
- "What does my 'Raid alert' action do?"
- "Add a giveaway for a Steam key that costs 100 points."

**Stop** stops an answer, **New chat** starts fresh.

### It asks before big changes

Before the assistant deletes something, imports something, or writes in your chat, it shows **The assistant wants
to:** and waits. Click **Approve** to let it, or **Deny** to stop it.

Always check what it made. It can make mistakes, just like a person.

### What it can and cannot touch

- **Can:** actions, triggers, sub-actions, commands, timers, variables, overlays, polls, quotes, points, giveaways,
  decks and more.
- **Cannot:** your sign-ins, API keys, passwords, and the server settings. Those stay out of its reach.

Cuebot logs only the *names* of what the assistant did, not the details. The last conversation is saved in your data
folder, so it is still there next time.

## Using your own AI app with Cuebot (MCP)

You can also go the other way round: use your favourite AI app, and let it work on Cuebot. This uses **MCP** (Model
Context Protocol): a common way for AI apps to use other programs' tools.

The MCP server is **on from the start**. It only listens on this computer and asks for a token (a secret code).

1. Go to **Apps > Servers**.
2. On the **MCP server (AI assistants)** card, check that **Enabled** is ticked.
3. The card has ready-made setup text for **Claude Code (terminal)**, for **Claude Desktop and other apps that start
   an MCP process (stdio)**, and for **Other clients with HTTP support**. Click **Copy** and paste it into your AI app's
   settings. For Claude Desktop: **Settings > Developer > Edit Config**.
4. Keep Cuebot running while you use it.

The **MCP URL** is `http://127.0.0.1:7475/mcp`. The token is on the **REST API** card: **Show** shows it, **Copy**
copies it and **Regenerate** makes a new one (then update your AI app too).

!!! warning "Keep the token secret"
    Anyone with the token can change your Cuebot. Never show it on stream. If it leaks, click **Regenerate**.

The same approval step protects you here: the box **Confirm destructive tools…** on the MCP card makes Cuebot ask
before deletes and other big changes. Leave it ticked.

## The REST API (for programmers)

The **REST API** card turns on a web interface (an "API") that other programs and scripts can use to control Cuebot.
It is off until you tick **Enabled** (or click **Turn on the REST API** on the [Stream Deck](stream-deck.md) page). It listens on
`http://127.0.0.1:7475/api/v1` and every request needs the token. The full details are in the
[server documentation](https://github.com/TechDaddyKB/cuebot/blob/main/docs/SERVERS.md).
