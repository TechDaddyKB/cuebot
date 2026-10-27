# Third-party notices

Cuebot is MIT licensed (see [LICENSE](LICENSE)). It ships with, or downloads on demand, the components below.
Each keeps its own license; full license texts are available from the linked projects and inside the NuGet packages.

## Bundled libraries

| Component | Used for | License |
|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Desktop, Wayland, Themes.Fluent) | UI framework | MIT |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | UI font | SIL Open Font License 1.1 |
| [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) | Code editor | MIT |
| [TextMateSharp](https://github.com/danipen/TextMateSharp) (via AvaloniaEdit.TextMate) and its bundled grammars/themes | Syntax highlighting | MIT; grammars and themes keep their own (mostly MIT) licenses |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / Skia (via Avalonia) | Rendering | MIT / BSD-3-Clause |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) / HarfBuzz (via Avalonia) | Text shaping | MIT / Old MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM helpers | MIT |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | Dependency injection | MIT |
| [Roslyn scripting](https://github.com/dotnet/roslyn) (Microsoft.CodeAnalysis.CSharp.Scripting) | C# execute-code sub-actions | MIT |
| [System.Speech](https://github.com/dotnet/runtime) | Windows SAPI voices | MIT |
| [SoundFlow](https://github.com/LSXPrime/SoundFlow) | Audio playback and capture | MIT |
| [miniaudio](https://github.com/mackron/miniaudio) (native library inside SoundFlow) | Audio backend | MIT No Attribution or public domain (Unlicense), at your choice |
| [NVorbis](https://github.com/NVorbis/NVorbis) | Ogg Vorbis decoding | MIT |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | Ed25519 verification of Throne webhooks | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) (self-contained builds) | Runtime | MIT |

## Downloaded on demand

| Component | When | License |
|---|---|---|
| [python-build-standalone](https://github.com/astral-sh/python-build-standalone) | The first time a Python execute-code sub-action needs the built-in interpreter | Python Software Foundation License, plus the licenses of the libraries it bundles (OpenSSL, SQLite, libffi, zlib, …) listed in the distribution |
| Python packages installed with the library manager | When you install them | Each package's own license |

## Online services

Cuebot talks to third-party services (Twitch, YouTube, Kick, Rumble, OBS Studio, TTS providers and others) through their
public APIs, using the user's own accounts and keys. Their terms of service apply to that use. Cuebot is not affiliated
with or endorsed by any of them, nor with Streamer.bot or Speaker.bot.
