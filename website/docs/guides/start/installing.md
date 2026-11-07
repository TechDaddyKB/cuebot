# Installing Cuebot

This page shows you how to download Cuebot and start it for the first time, on Windows, Mac or Linux. Cuebot is one
download. You do not need to install anything else first.

## Pick your download

Go to the [releases page](https://github.com/TechDaddyKB/cuebot/releases) and open the newest release. While Cuebot is
in testing, releases are marked **Pre-release**. Under **Assets**, pick the file for your computer:

| Your computer | File to download |
| --- | --- |
| Windows 10 or 11 | `Cuebot-win-x64.zip` |
| Linux (most people) | `Cuebot-x86_64.AppImage` |
| Linux, with Flatpak | `Cuebot-x86_64.flatpak` |
| Linux, ARM computer or if you prefer a plain folder | `Cuebot-<version>-linux-arm64.tar.gz` or `...-linux-x64.tar.gz` |
| Mac with Apple silicon (M1 or newer) | `Cuebot-macos-arm64.dmg` |
| Mac with an Intel chip | `Cuebot-macos-x64.dmg` |
| Android phone or tablet (Android 10 or newer): the [phone app](../mods/phone-app.md) | `Cuebot-Companion-<version>.apk` |
| Elgato Stream Deck app (Windows or Mac): the [Cuebot plugin](../apps/stream-deck.md) | `io.github.cuebot.streamdeck.streamDeckPlugin` |

!!! warning "Experimental"
    Cuebot for Mac is new. See [What's experimental](../help/experimental.md).

## Windows

1. Download `Cuebot-win-x64.zip`.
2. Right-click the zip and choose **Extract All**. Put it in a folder you will keep, like `Documents\Cuebot`.
   Do not start Cuebot from inside the zip.
3. Open the folder and double-click `Cuebot.exe`.

!!! note "\"Windows protected your PC\""
    Windows may show this blue box the first few times, because Cuebot is new and Windows does not know it yet. Click
    **More info**, then **Run anyway**.

## Mac

You need macOS 15 (Sequoia) or newer. Not sure which chip your Mac has? Open the Apple menu and click
**About This Mac**: "Apple M..." means Apple silicon, "Intel" means Intel.

1. Download the `.dmg` for your Mac and open it.
2. Drag **Cuebot** onto the **Applications** folder next to it.
3. Open **Applications** and double-click **Cuebot**.

!!! note "\"Cuebot cannot be opened\" or \"Apple could not verify Cuebot\""
    macOS asks before the first start, because this Cuebot is not yet signed by Apple. Click **Done** (or
    **OK**), open **System Settings > Privacy & Security**, scroll down and click **Open Anyway** next to Cuebot. You
    only do this once. (On older Macs, right-click **Cuebot** and choose **Open** instead; macOS 15 no longer offers
    that shortcut.)

When you turn on Voice Control, macOS asks whether Cuebot may use the microphone. Click **Allow**.

Hotkeys and the **Keyboard press** step need permission too: macOS asks for **Input Monitoring** and
**Accessibility** the first time. See [Hotkeys](../actions/hotkeys.md).

!!! note "After updating on a Mac"
    This Cuebot is not signed by Apple yet, so macOS sees each update as a new app. If hotkeys or key presses stop
    working after an update while **System Settings > Privacy & Security** still shows Cuebot turned on, select Cuebot
    in **Input Monitoring** and **Accessibility**, remove it with **−**, and add it again with **+**.

!!! note "Notifications on a Mac"
    Cuebot's system notifications on a Mac come from **Script Editor** (a helper that is part of macOS). If none
    show up, open **System Settings > Notifications > Script Editor** and allow them. Or let Cuebot show its own
    notifications instead: go to **Automation > Hotkeys**, and under **Desktop** set **Notifications appear** to
    **In-app**.

A few things are not on a Mac yet. The free **Piper** voices only run on Windows and Linux, so pick another
[text-to-speech engine](../sound/text-to-speech.md).

## Linux

### AppImage

1. Download `Cuebot-x86_64.AppImage`.
2. Make it runnable. In most file managers: right-click it, open **Properties** and tick the box that allows running
   it as a program (the wording depends on your desktop). Or type this in a terminal:

    ```bash
    chmod +x Cuebot-x86_64.AppImage
    ./Cuebot-x86_64.AppImage
    ```

3. If you want Cuebot in your app menu, use a tool such as Gear Lever or AppImageLauncher.

### Flatpak

Download `Cuebot-x86_64.flatpak` and open it with your software centre, or type
`flatpak install --user Cuebot-x86_64.flatpak` in a terminal.

### Tarball (a plain folder)

```bash
tar -xzf Cuebot-<version>-linux-x64.tar.gz
./Cuebot-<version>-linux-x64/lib/Cuebot
```

### Arch Linux

The AUR packages `cuebot` and `cuebot-bin` come with the first full (non-test) release.

Linux has a few extras, like sound players and the Stream Deck. See the [Linux page](../help/linux.md).

## What next?

- [Your first five minutes](first-five-minutes.md): sign in, connect OBS and make your first command.
- [Updating Cuebot](updating.md): how to get the next version.
