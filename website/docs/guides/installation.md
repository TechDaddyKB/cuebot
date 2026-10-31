# Installing Cuebot

Cuebot is one download. You do not need to install anything else first.

Go to the [releases page](https://github.com/TechDaddyKB/cuebot/releases) and open the newest release. While Cuebot is
in testing, releases are marked **Pre-release**. Under **Assets**, pick the file for your computer:

| Your computer | File to download |
| --- | --- |
| Windows 10 or 11 | `Cuebot-win-x64.zip` |
| Linux (most people) | `Cuebot-x86_64.AppImage` |
| Linux, with Flatpak | `Cuebot-x86_64.flatpak` |
| Linux, ARM computer or if you prefer a plain folder | `Cuebot-<version>-linux-arm64.tar.gz` or `...-linux-x64.tar.gz` |

## Windows

1. Download `Cuebot-win-x64.zip`.
2. Right-click the zip and choose **Extract All**. Put it in a folder you will keep, like `Documents\Cuebot`.
   Do not start Cuebot from inside the zip.
3. Open the folder and double-click `Cuebot.exe`.

!!! note "\"Windows protected your PC\""
    Windows may show this blue box the first few times, because Cuebot is new and Windows does not know it yet. Click
    **More info**, then **Run anyway**.

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
./Cuebot-<version>-linux-x64/Cuebot
```

### Arch Linux

The AUR packages `cuebot` and `cuebot-bin` come with the first full (non-test) release.

Linux has a few extras, like sound players and the Stream Deck. See the [Linux page](linux.md).

## Updating

1. Close Cuebot.
2. Download the new version.
3. Unzip it over the old folder, or into a new folder.

Your actions and settings are **not** in the program folder, so nothing is lost. They live in your
[data folder](backups-and-your-data.md).

## Being told about new versions

In the left rail click **Settings**, then the **Support** page, then the **Updates** tab. Tick "Check for updates when Cuebot starts (at most once a day)".
Set **Channel** to **Prerelease** to also hear about test versions. While Cuebot is in testing, every release is a
test version, so pick **Prerelease** for now.

Next: [your first five minutes](getting-started.md).
