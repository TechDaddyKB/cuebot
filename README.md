# Cuebot

Cross-platform (Windows + Linux) streaming automation: a clean-room combination of a stream bot and a
text-to-speech reader in one native desktop app, built on .NET 10 and Avalonia.

- **Actions** run when **triggers** fire (chat commands, follows, subs, cheers, raids, channel-point
  redemptions, OBS scene/stream changes, timers, app start). Each action is a flow of **sub-actions**
  with real branching (`If` → True/False).
- Two editors over the same data: a **list editor** and a **node-graph editor** (drag nodes, click `+` on
  a port to add a step there, drop a node on a port to rewire it).
- **Twitch** via EventSub WebSocket + Helix (device-code sign-in, no client secret).
- **OBS Studio** via obs-websocket 5.
- **Speech**: Windows SAPI, espeak-ng, ElevenLabs; voice aliases, speak-chat with filters, a playback queue.
- **Scripting**: "Execute C# code" (Roslyn) and "Execute Python code" (a managed, downloadable CPython with
  pip package management, running out-of-process). Both call the same script API.

## Layout

| Path | What |
| --- | --- |
| `src/Cuebot.Core` | Event bus, flow-tree model, action engine, variables, timers, storage, script API. No UI, no platforms. |
| `src/Cuebot.Twitch` / `Cuebot.Obs` / `Cuebot.Speech` / `Cuebot.Scripting` | Modules: each registers triggers, sub-actions and script functions through one `AddXxx()` call. |
| `src/Cuebot.App` | Avalonia UI and composition root (`AppHost`). Editors are generated from parameter descriptors. |
| `tests/*` | xunit tests per project; `Cuebot.UiTests` renders every page headlessly and writes screenshots. |
| `packaging/` | AppImage script, Flatpak manifest, desktop/metainfo files, icons. |
| `docs/MODULE_GUIDE.md` | How to write a module. |

## Build and run

```bash
dotnet run --project src/Cuebot.App
```

```bash
dotnet test Cuebot.slnx
```

Packages:

```bash
pwsh build/publish.ps1 -Runtimes win-x64
```

```bash
packaging/linux/build-appimage.sh linux-x64
```

```bash
flatpak-builder --user --install --force-clean build-dir packaging/flatpak/io.github.cuebot.Cuebot.yml
```

CI (`.github/workflows/build.yml`) tests on Windows and Ubuntu and produces the Windows zip, the AppImage and
a Flatpak bundle.

User data lives in `%APPDATA%\Cuebot` (Windows) or `~/.config/Cuebot` (Linux); set `CUEBOT_DATA` to override.

## Linux

Cuebot is **Wayland-first**: when a Wayland compositor is running it uses Avalonia's native Wayland backend,
otherwise X11. If the Wayland backend cannot start, Cuebot relaunches itself once on X11 (XWayland). Force a
backend with `--backend=wayland` / `--backend=x11` or `CUEBOT_BACKEND=wayland|x11`; the startup log line records
which backend is in use.

Packages: AppImage, Flatpak (Wayland socket with X11 fallback), and the AUR: `cuebot` builds from source on the
system .NET runtime, `cuebot-bin` repackages the self-contained release. See `packaging/aur`.

- Audio playback uses `pw-play`, `paplay` or `aplay` (whichever is installed).
- Offline TTS on Linux uses `espeak-ng` (bundled in the Flatpak).

CI smoke-tests every Linux package on a headless Wayland compositor (sway, no X server) and on X11 (Xvfb),
and builds and installs both AUR packages in an Arch Linux container.

## Releases

Push a tag such as `v0.1.0`. The release workflow builds and tests everything, publishes a GitHub release
(Windows zip, AppImage, Flatpak, Linux tarballs) and produces pinned AUR `PKGBUILD`/`.SRCINFO` files. It pushes
them to the AUR when the repository has an `AUR_SSH_PRIVATE_KEY` secret. The AUR needs a publicly downloadable
source, so the repository (or at least its releases) must be public before publishing there.

## Variables

`%name%` inserts an action argument (`%user%`, `%message%`, `%input0%`, `%bits%`...), `~name~` a global variable.
