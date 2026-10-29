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
| `src/Cuebot.Assistant` | The Assistant page's backend: AI providers (API keys; AI CLIs) working through Cuebot's MCP tools. See `docs/ASSISTANT.md`. |
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

User data lives in `%APPDATA%\Cuebot` (Windows) or `~/.config/Cuebot` (Linux); set `CUEBOT_DATA` or pass
`--data <folder>` to override.

## Startup switches

| Switch | What |
| --- | --- |
| `--headless` | No window: every service starts, the log goes to the console and the log file; Ctrl+C, SIGTERM or SIGHUP shuts down cleanly (stores are flushed). No display server needed. |
| `--minimized` | Start hidden in the tray (minimized when no tray is available). Same as Settings > General > Start minimized. |
| `--data <folder>` | Use this data folder (overrides `CUEBOT_DATA`). |
| `--reset-window` | Forget the saved window size and position. |
| `--backend=wayland\|x11` | Linux display backend (see below). |
| `--help` | List the switches. |

Streamer.bot-style `/headless` and `/resetwindow` are accepted too. Only one Cuebot runs per data folder: a second
launch brings the running window to the front (or is ignored by a headless instance) and exits, so two instances
can never write the same files. Different data folders can run side by side.

Shell settings live in `app.json` in the data folder (Settings page): tray icon, close-to-tray, start minimized,
theme (Dark, Light or System), backups and the saved window placement.

Backups: on every start (and before imports) the data folder is zipped into `backups/`
(`cuebot-yyyyMMdd-HHmmss-<reason>-v<version>.zip`, without logs, the Python runtime and older backups); the newest 10
are kept (configurable). Settings > Backups backs up on demand, opens the folder and restores: a restore is applied
while Cuebot restarts, before anything is loaded, after a safety backup of the current data.

### Google Drive backups

Streamer.bot's cloud backups need a Streamer.bot account; Cuebot can copy its backups to your own Google Drive instead
(Settings > Google Drive). Every new backup is uploaded into a **Cuebot Backups** folder that Cuebot creates, the newest
10 are kept there (configurable, separately from the local count), and at start the newest local backup is uploaded if
Drive does not have it yet (e.g. after being offline). The tab lists the backups on Drive; **Restore...** downloads one
into `backups/downloads/` and schedules it exactly like a local restore (safety backup first, applied at the next
start). Your Google Drive sign-in is kept when a backup is restored. Cuebot asks only for the `drive.file` scope, so it
can see and change nothing in your Drive except the files it created.

Google needs an OAuth client, and Cuebot ships none, so you create your own (free, once):

1. Open the [Google Cloud console](https://console.cloud.google.com/), create a project (or pick one) and enable the
   **Google Drive API** (APIs & Services > Library).
2. Set up the consent screen (Google Auth Platform > Branding / Audience): user type **External**, then add your Google
   account under **Test users**. Data access: add the scope `https://www.googleapis.com/auth/drive.file`.
3. Clients > **Create client**, application type **Desktop app**. Copy the **Client ID** and **Client secret** into
   Settings > Google Drive. (Google issues a secret for desktop clients but treats it as non-confidential; there is no
   redirect URL to register: Cuebot listens on a random `http://127.0.0.1` port during sign-in, with PKCE.)
4. Click **Sign in with Google**, pick the account and allow access. The page shows "Signed in as ...".

While the project's publishing status is **Testing**, Google expires the sign-in after 7 days and Cuebot shows "sign in
again"; publish the app (Audience > Publish app) to keep it signed in. The refresh token is stored in `app.json` like
other modules store their sign-ins and is never logged. Backups contain your whole data folder, including the sign-ins
of connected accounts, so they are as private as your Drive. Sign out revokes Cuebot's access at Google; backups
already on Drive stay there.

## Linux

Cuebot is **Wayland-first**: when a Wayland compositor is running it uses Avalonia's native Wayland backend,
otherwise X11. If the Wayland backend cannot start, or opens no window within 20 s (set `CUEBOT_WAYLAND_TIMEOUT` in
seconds to change that), Cuebot relaunches itself once on X11 (XWayland). Force a backend with `--backend=wayland` /
`--backend=x11` or `CUEBOT_BACKEND=wayland|x11`; the startup log line records which backend is in use.

Packages: AppImage, Flatpak (Wayland socket with X11 fallback), and the AUR: `cuebot` builds from source on the
system .NET runtime, `cuebot-bin` repackages the self-contained release. See `packaging/aur`.

Avalonia's Wayland backend needs a compositor offering `xdg_wm_base` v3 or newer (GNOME, KDE Plasma, current
sway/Hyprland all do; sway 1.9 does not). On older compositors Cuebot relaunches on XWayland automatically.
Inside the Flatpak sandbox X11 is only granted when no Wayland socket exists, so on such a compositor run
`flatpak override --user --socket=x11 io.github.cuebot.Cuebot` once (or launch with `--backend=x11`).

Tray icon: on Linux it is a StatusNotifierItem, shown by KDE Plasma, waybar and most wlroots bars, XFCE, Cinnamon and
GNOME with the AppIndicator extension. Plain GNOME and some minimal Wayland sessions have no tray; Cuebot detects that
(no `org.kde.StatusNotifierWatcher` on the session bus) and then minimizes instead of hiding to the tray, so the window
is never lost. Launching Cuebot again always brings the window back.

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

## License

[MIT](LICENSE)
