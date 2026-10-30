# Contributing to Cuebot

Thanks for helping. Bug reports, feature ideas and pull requests are all welcome.

- **Bugs and ideas:** open an [issue](https://github.com/TechDaddyKB/cuebot/issues/new/choose). The templates ask for
  what we need to reproduce a problem.
- **Security problems:** do not open a public issue. See [SECURITY.md](SECURITY.md).
- **Questions about using Cuebot:** start with the [wiki](https://github.com/TechDaddyKB/cuebot/wiki).

## Clean room

Cuebot is written from scratch. It aims to work like Streamer.bot and Speaker.bot and can import Streamer.bot exports,
but no code, decompiled or otherwise, comes from either product. Contributions must follow the same rule: work only
from public documentation, public APIs and your own observations of how the software behaves. Do not paste, translate
or paraphrase code from closed-source products.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Cuebot.App
```

```bash
dotnet test Cuebot.slnx
```

On Windows, clone to a short folder (for example `C:\cb\cuebot`) or run `git config --global core.longpaths true`
first: some repository paths are long, and deep folders can pass Windows' 260-character path limit. A Windows build
copies only the Windows native libraries into `bin` (set `-p:CuebotLocalRuntimeFamily=` to keep every platform's).

Linux tests that use Python or espeak-ng need `python3` and `espeak-ng` installed.

### Packages

```bash
pwsh build/publish.ps1 -Runtimes win-x64
```

```bash
packaging/linux/build-appimage.sh linux-x64
```

```bash
packaging/linux/build-tarball.sh 0.1.0 linux-x64
```

```bash
flatpak-builder --user --install --force-clean build-dir packaging/flatpak/io.github.cuebot.Cuebot.yml
```

A build made from source has no built-in sign-in apps, so each platform page asks for your own app (Twitch client ID,
Google Cloud project and so on). See [docs/BUILT_IN_APPS.md](docs/BUILT_IN_APPS.md) to build with your own.

## Layout

| Path | What |
| --- | --- |
| `src/Cuebot.Core` | Event bus, flow-tree model, action engine, variables, timers, storage, script API. No UI, no platforms. |
| `src/Cuebot.Twitch`, `Cuebot.YouTube`, `Cuebot.Kick`, `Cuebot.Rumble` | Platform modules. |
| `src/Cuebot.Obs`, `Cuebot.StreamApps` | OBS Studio, Streamlabs Desktop and other stream apps. |
| `src/Cuebot.Speech`, `Cuebot.VoiceControl` | Text-to-speech engines and the queue; speech recognition. |
| `src/Cuebot.StreamDeck`, `Cuebot.Midi`, `Cuebot.Controllers`, `Cuebot.Elgato`, `Cuebot.GoXlr`, `Cuebot.WaveLink` | Devices. |
| `src/Cuebot.Donations`, `Cuebot.Integrations`, `Cuebot.Webhooks`, `Cuebot.Relay` | Donation services, other integrations, incoming webhooks. |
| `src/Cuebot.Server`, `Cuebot.Remote` | Local WebSocket/HTTP/UDP servers and links between Cuebot instances. |
| `src/Cuebot.Scripting` | C# and Python script runners. |
| `src/Cuebot.Assistant` | The Assistant page: AI providers working through Cuebot's MCP tools ([docs/ASSISTANT.md](docs/ASSISTANT.md)). |
| `src/Cuebot.App` | Avalonia UI and composition root. |
| `relay/` | Optional Cloudflare Worker that forwards webhooks to Cuebot. |
| `streamdeck/` | Stream Deck plugin. |
| `tests/*` | xunit tests per project. `Cuebot.UiTests` renders every page headlessly and writes screenshots. |
| `packaging/` | AppImage, Flatpak, AUR, desktop and metainfo files, icons. |

Each module registers its triggers, sub-actions and script functions through one `AddXxx()` call; see
[docs/MODULE_GUIDE.md](docs/MODULE_GUIDE.md). Protocol notes for each integration live in a `PROTOCOL.md` beside its
code.

## Tests

- Unit and UI tests run with `dotnet test Cuebot.slnx`. Keep them green; add a test that fails without your change.
- Live tests against real services are opt-in and never run in CI: see [docs/LIVE_TESTING.md](docs/LIVE_TESTING.md).

## Pull requests

- Keep each pull request to one change, with a clear description of what changed and why.
- Match the style of the surrounding code; no unrelated reformatting.
- Update the docs when behaviour changes.
- By contributing you agree that your work is licensed under the project's [MIT license](LICENSE).

## Python runtime pin

Cuebot installs one exact python-build-standalone build per platform, checked against SHA-256 sums in
`src/Cuebot.Scripting/Python/PinnedPython.cs`. To move to a newer build, run `tools/update-python-pin.py [release-tag]`
and paste its output into that file. `CUEBOT_TEST_PYTHON_INSTALL=1 dotnet test tests/Cuebot.Scripting.Tests` then
downloads and installs the new build for real.

## Releases (maintainers)

Push a tag such as `v0.1.0`. The release workflow builds and tests everything, publishes a GitHub release (Windows
zip, AppImage, Flatpak, Linux tarballs) and prepares the AUR `PKGBUILD`/`.SRCINFO` files, pushing them to the AUR when
the repository has an `AUR_SSH_PRIVATE_KEY` secret. A tag with a hyphen (`v0.1.0-rc.1`) is a release candidate: a
GitHub pre-release, and nothing goes to the AUR. CI also smoke-tests every Linux package on a headless Wayland
compositor and on X11.
