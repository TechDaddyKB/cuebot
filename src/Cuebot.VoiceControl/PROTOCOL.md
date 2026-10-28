# Voice control (speech-to-text)

Clean-room implementation of Streamer.bot's Voice Control (Commands, Settings, Log; Voice Control Command and
Dictation triggers; Set Voice Control Command / Command State / Input sub-actions), built only from the public
documentation at docs.streamer.bot (`/guide/core/voice-control`, the trigger and sub-action pages) and the public
docs of the libraries below.

## Engines

| Engine | Id | Platforms | How it works |
| --- | --- | --- | --- |
| **Whisper** (default) | `whisper` | Windows, Linux | [Whisper.net](https://github.com/sandrohanea/whisper.net) 1.9 (MIT) over whisper.cpp (MIT). Local, offline, CPU by default. |
| Windows Speech Recognition | `windows` | Windows | `System.Speech` (the engine Streamer.bot 1.0 uses). Exact commands become a grammar; a dictation grammar catches the rest. |

`ISpeechRecognizerEngine` / `IRecognitionSession` keep the engines thin; everything above them (segmentation,
matching, thresholds, events, model downloads, sub-actions) is unit-tested with fakes.

### Why Whisper (and not Vosk)

* Streamer.bot 1.1α moved to Whisper.net, so Whisper is the parity choice, and it is the most accurate local option.
* Whisper.net is actively maintained (1.9.1, June 2026), MIT, ships its native runtime for win-x64/x86/arm64 and
  linux-x64/arm/arm64 in one ~2.5 MB-per-platform package, with an automatic non-AVX fallback
  (`Whisper.net.Runtime.NoAvx`) and optional CUDA/Vulkan/OpenVINO runtimes.
* Vosk was considered: good grammar support and true streaming, but its NuGet package is from 2022, ships ~26 MB of
  native code per platform (plus MinGW runtime DLLs on Windows), and its small models are noticeably less accurate.
  The engine abstraction leaves room to add it later.
* Windows-only `System.Speech` is offered as the second engine because it is what Streamer.bot users know, has real
  grammar matching for Exact commands, alternates and hypotheses, and costs nothing (already a dependency).

### Audio path

Microphone capture is the speech module's existing SoundFlow/miniaudio capture (`IAudioInput.CaptureSamples`, the
same device list as VoiceGate; WASAPI on Windows, PulseAudio/PipeWire/ALSA on Linux). Samples (48 kHz mono) are
resampled to 16 kHz (box-filter decimation) and handed to the session.

* **Whisper** transcribes clips, so `UtteranceSegmenter` (energy VAD with an adaptive noise floor, 300 ms pre-roll,
  pause = 600 ms, utterances ≥ 250 ms, cut at 15 s) cuts the stream at pauses. One worker transcribes utterances in
  order; clips under 1.1 s are padded (whisper.cpp rejects < 1 s). Decoding uses `WithNoContext`, `WithSingleSegment`,
  half the cores (max 8), and optionally the enabled command phrases as the initial prompt so Whisper spells them the
  way they are written. Non-speech markers (`[BLANK_AUDIO]`, `(music)`, `*cough*`) are stripped.
* **Interim results** (only when a command uses *Stop After* with Start/Anywhere): every second of ongoing speech the
  utterance so far is transcribed, but only when the worker is idle, so final results are never delayed.
* **Windows** gets the same 16 kHz stream as PCM16 through a blocking stream (`SetInputToAudioStream`); SAPI's
  `SpeechHypothesized` provides the interim results.

### Confidence

* Whisper: the length-weighted mean token probability of the segments (0–1). Typical clear speech: 0.6–0.95.
* Windows: `RecognitionResult.Confidence`; alternates from `RecognitionResult.Alternates`.
* A command fires when `confidence × phrase similarity ≥ threshold` (the command's own threshold with *Override
  Global*, else the global one). Dictation fires when the raw confidence reaches the global threshold.

## Matching

`PhraseText` normalises case, accents, punctuation, apostrophes and hyphens, and maps English number words 0–20 to
digits. `PhraseMatcher` compares word windows of about the phrase's length (n−1…n+1 words) by Levenshtein distance,
with and without spaces ("count down" = "countdown"); a window matches with at most ⌊tolerance × length⌋ edits
(Settings → Match tolerance, default 20 %, 0 = identical words). Whole edits keep short phrases strict: "lights on"
does not match "lights off".

* **Exact** – the whole utterance is the phrase.
* **Start** – the utterance begins with the phrase; the rest is `spokenTextInput`.
* **Anywhere** – anywhere in the utterance (the text after it is also given as `spokenTextInput`).
* Commands are checked in list order; every match fires, except that a *Stop After* command ends the check. A *Stop
  After* command fires from interim results (Start/Anywhere only, as soon as the phrase is heard) and is not fired
  again by the final result.

## Events, triggers and variables

| Bus event | Trigger (type id) | Streamer.bot WebSocket name |
| --- | --- | --- |
| `speechToText.command` | Voice Control › Command (`voiceControl.command`) | `SpeechToText.Command` |
| `speechToText.dictation` | Voice Control › Dictation (`voiceControl.dictation`) | `SpeechToText.Dictation` |

The "speechToText" prefix makes the generic event map produce Streamer.bot's names. Both triggers filter on
**Voice Control** (command name or id, comma-separated; `Any` = all). Dictation fires once per final utterance and
carries the first command it fired (empty if none), so a Dictation trigger filtered on a command fires only for
utterances that contained it.

Documented variables: `spokenCommand`, `spokenText`, `spokenTextConfidence` (0–1), `spokenTextConfidencePercent`
(0–100), `spokenTextInput`, `altPhraseText#`, `altPhraseConfidence#`, `altPhraseConfidencePercent#` (numbered from 0;
0 is the best result; Whisper gives one). Cuebot extras: `altPhraseCount`, `spokenTextFinal`, `spokenLanguage`,
`voiceEngine`, `voiceCommandId`, `voiceCommandName`, `voiceCommandLocation`, `voiceCommandMatched`,
`voiceCommandSimilarity`, `voiceCommandConfidencePercent` (command) and `voiceCommandCount` (dictation).

## Sub-actions and script functions

| Sub-action (type id) | Parameters | Sets |
| --- | --- | --- |
| Set Voice Control Command (`voiceControl.setCommand`) | Command, Command phrase, Location (Unchanged/Exact/Start/Anywhere), Create if missing | `voiceCommandId/Name/Phrase` |
| Set Voice Control Command State (`voiceControl.setCommandState`) | Command, State (Enabled/Disabled/Toggle) | `voiceCommandEnabled` |
| Set Voice Control Input (`voiceControl.setInput`) | Audio Input Device (Default = system default) | – |
| Start/Stop Listening (`voiceControl.setListening`, Cuebot extra) | State (Start/Stop/Toggle) | `voiceControlListening` |

Streamer.bot documents no CPH methods for voice control. Scripts get `voiceControl.isListening`, `status`,
`startListening`, `stopListening`, `commands`, `setCommandState`, `setCommand`, `setInputDevice`, `inputDevices`,
`dictationLog`. The C# compatibility layer knows `EventSource.SpeechToText` and `EventType.SpeechToTextCommand` /
`SpeechToTextDictation` (`SpeechToTextLog` exists for source compatibility).

## Models

* Catalog (`WhisperModelCatalog`): tiny/base/small/medium (English-only and multilingual, q5 quantised) and
  large-v3-turbo q5_0, 32 MB – 574 MB. Default: Base English for English systems, Base multilingual otherwise.
* Downloaded only when the user clicks *Download*, from a pinned revision of `huggingface.co/ggerganov/whisper.cpp`
  (MIT), into `<data folder>/models/whisper/`. Streaming to `.part` with progress, SHA-256 and size checked against the
  catalog before the rename; mismatches are deleted. Never bundled, never in git; tests never download.

## Runtime requirements and packaging

* Windows: whisper.cpp needs the Microsoft Visual C++ 2015-2022 Redistributable (x64). AVX2 CPUs use the default
  build, older CPUs the NoAvx build automatically.
* Linux: glibc ≥ 2.34, libstdc++, **libgomp** (OpenMP). The AppImage bundles the build host's `libgomp.so.1` in
  `usr/lib/cuebot-deps` (AppRun puts only that folder on `LD_LIBRARY_PATH`); the Flatpak runtime
  (freedesktop 25.08) provides all three; the AUR packages depend on `gcc-libs`. A load failure shows these hints.
* The microphone in Flatpak comes through the existing `--socket=pulseaudio` permission (PipeWire's Pulse server).
* Whisper.net copies every platform's natives to `runtimes/<platform>/` (and `runtimes/noavx/...`); the root
  `Directory.Build.targets` removes the other platforms' whisper folders after a RID-specific `dotnet publish`
  (~20 MB), touching only folders that contain a `ggml-whisper` library.
* GPU: "Use GPU" puts CUDA/Vulkan first in Whisper.net's runtime order; those runtimes are not shipped (hundreds of
  MB). Dropping a matching `runtimes/cuda|vulkan/<rid>` folder from the Whisper.net.Runtime.Cuda/Vulkan packages next
  to Cuebot enables them. The order is fixed at the first load in a process, so changing it needs a restart of Cuebot.

## Not done / limits

* Streamer.bot's per-command "Action" picker is the trigger in Cuebot (as with every other trigger).
* No speaker training or custom dictionary (Windows' own dictionary still applies to the Windows engine).
* The PulseAudio stream is named "Cuebot level meter" (the shared capture's name).
