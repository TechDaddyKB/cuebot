# Linux

Cuebot runs on Linux as a normal app: no Windows emulator needed. This page covers the few things that are different.

How to install: see [Installing Cuebot](installation.md#linux).

## Wayland and X11

Linux desktops draw windows in one of two ways: the newer **Wayland**, or the older **X11**. Cuebot uses Wayland when
your desktop has it, and X11 otherwise.

If Wayland does not work, or no window shows up within 20 seconds, Cuebot restarts itself once using X11. The log says
which one it uses.

You can choose yourself:

- start Cuebot with `--backend=wayland` or `--backend=x11`, or
- set the environment variable `CUEBOT_BACKEND=wayland` or `CUEBOT_BACKEND=x11`.

`CUEBOT_WAYLAND_TIMEOUT` changes the 20-second wait (in seconds).

## The tray icon

The **tray** is the row of small icons in your desktop's panel. Cuebot puts its icon there. KDE Plasma, Cinnamon, XFCE,
waybar and most other panels show it. Plain GNOME needs the AppIndicator extension.

If your desktop has no tray, Cuebot minimizes its window instead of hiding it, so you never lose it. Starting Cuebot
again always brings the window back.

## Quitting

Logging out, or closing the terminal you started Cuebot from, closes Cuebot properly and saves everything, just like
**Quit** in the tray menu.

## Sound and speech

- Cuebot plays sound by itself. If you hear nothing, install one of these sound players: `pw-play` (PipeWire),
  `paplay` (PulseAudio) or `aplay` (ALSA).
- The free offline voice needs `espeak-ng`. Install it from your distribution's software centre. The Flatpak already
  has it.
- Voice control's speech recognition needs `libgomp` (part of GCC). The AppImage already has it.

## Devices

- **Stream Deck:** Cuebot talks to it directly over USB. You may need a permission rule once; see
  [Stream Deck](stream-deck.md#linux-the-permission-rule).
- **Game controllers:** PlayStation and Switch pads need permission for rumble and lights. Install your distribution's
  `steam-devices` or `game-devices-udev` package.
- **MIDI** works through ALSA, which almost every Linux desktop has.
- **Elgato Facecam and webcams:** Cuebot can control them directly on Linux. See
  [Stream apps and devices](stream-apps-and-devices.md#lights-mixers-and-other-apps).

## The Flatpak

The Flatpak keeps Cuebot in a closed box, for safety. Two things to know:

- It cannot reach other AI apps on your computer, so the [assistant](assistant.md) needs an API key.
- It uses X11 only when your desktop has no Wayland at all. To force X11 on an older desktop, run this once:

    ```bash
    flatpak override --user --socket=x11 io.github.cuebot.Cuebot
    ```
