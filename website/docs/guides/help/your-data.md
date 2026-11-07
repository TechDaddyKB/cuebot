# Your data and privacy

This page explains where Cuebot keeps your settings and actions, how your sign-ins are protected, and what Cuebot
sends where. Good to know before you move to a new computer or make a second setup.

## Where Cuebot keeps your things

Everything Cuebot knows (settings, actions, sign-ins, logs and backups) is in one folder, the **data folder**:

| Computer | Data folder |
| --- | --- |
| Windows | `%APPDATA%\Cuebot` (type this into the address bar of File Explorer) |
| Linux | `~/.config/Cuebot` (Flatpak: `~/.var/app/io.github.cuebot.Cuebot/config/Cuebot`) |
| macOS | `~/Library/Application Support/Cuebot` |

The program folder holds no settings. That is why you can replace it when you update.

**Want a second setup**, or Cuebot on a USB stick? Start Cuebot with `--data <folder>` to use another folder (see
[Command-line options](command-line-options.md)). Only one Cuebot can use one data folder at a time.

## Your sign-ins are locked

Sign-ins, API keys and passwords are stored **encrypted**: scrambled so that only you can unlock them.

- On **Windows**, your Windows account holds the key.
- On **Linux**, your desktop's keyring (like GNOME Keyring or KWallet) holds it. Without a keyring, Cuebot uses a file
  only your user can read.
- On **macOS**, the key is in a file called `secrets.key` in the data folder that only your user can read. (Cuebot
  doesn't use the macOS Keychain.)

Sign-ins and keys are **never** put in backups. If you restore a [backup](backups.md) on a **new** computer, sign in again.

## Privacy

Cuebot has no tracking and sends no statistics. It only connects to a service when you set it up, and only sends what
that job needs. The [privacy policy](https://techdaddydigital.com/privacy/) lists exactly what goes where.
