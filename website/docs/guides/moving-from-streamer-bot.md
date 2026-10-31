# Moving from Streamer.bot

Cuebot works the way Streamer.bot does: actions, sub-actions, triggers, `%arguments%`, `~globals~` and queues. You
can bring your Streamer.bot actions across. Text-to-speech is built in, so you do not need Speaker.bot either.

!!! note
    Cuebot is its own project, written from scratch. It is not made by the Streamer.bot team.

## Bring your actions across

1. In **Streamer.bot**, right-click the actions you want, choose **Export**, and copy the export text.
2. In **Cuebot**, go to **Settings > Import / Export**, tab **Import**.
3. Paste the text into the big box (or click **Paste**). You can also load a file with **Open file…**.
4. Click **Preview**. Nothing runs while you import.
5. Tick what you want and click **Import selected**.

Imports bring actions (with their sub-actions, triggers and queues), commands, timers and global variables.

### Read the import report

After an import, the **Import report** has three lists:

- **Converted**: brought across and ready.
- **Kept as disabled placeholders**: things Cuebot does not have yet. They are kept, switched off, with all their
  settings, so nothing is lost.
- **Please check**: things that may not fit your new setup, like a file path that only exists on your old computer.

## After the import

1. **Sign in again.** Streamer.bot sign-ins do not come across. Sign in on each page under **Platforms** (see
   [Connecting your platforms](connecting-platforms.md)).
2. **Connect OBS** on **Apps > Stream apps** (see [Stream apps and devices](stream-apps-and-devices.md)).
3. Test your most important actions with **Test trigger…**.

## What stays the same

- The same variable signs: `%user%`, `%input0%`, `~myGlobal~` and functions like `$math()$`. See
  [Variables](variables.md).
- **Execute C# code** runs your C# code, with Streamer.bot's `CPH` methods. Almost all of them work.
- Cuebot's **WebSocket server (Streamer.bot API)** on **Apps > Servers** speaks Streamer.bot's language, so overlays
  and tools made for Streamer.bot keep working. Turn it on there first.
- The start options `/headless` and `/resetwindow` work (see [Command-line options](command-line-options.md)).

## What is different

- Cuebot runs on **Linux** as well as Windows.
- Every action can also be seen and edited as a [flowchart](actions-and-triggers.md#flowchart).
- Commands are made on **Automation > Commands** and linked with the **Command triggered** trigger (see
  [Chat commands](commands.md)).
- Cloud backups go to **your own Google Drive** (see [Backups and your data](backups-and-your-data.md)).

## Sharing your own actions

**Settings > Import / Export**, tab **Export**: tick the actions, commands, timers and global variables you want, and
click **Build string**. You get a code that starts with `CUEBOT1:`. Click **Copy** to share it, or **Save file…** for a
`.cuebot` file. Tick **Include what the selected actions use** so your friend gets everything they need.
