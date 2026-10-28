# MIDI in/out

Streamer.bot parity for MIDI (docs.streamer.bot `/guide/core/midi`, `/api/triggers/core/midi/message`,
`/api/sub-actions/core/midi/*`): MIDI In and MIDI Out device connections, MIDI In event listeners with automatic
detection, the **MIDI Message** trigger and the **Note On**, **Control Change**, **Generic Event** and **Sysex Event**
sub-actions. Everything except the thin device layer (`Backends/`) is portable and unit-tested against a fake backend.

## Backend choice

| Option | Windows | Linux | Hot-plug | Verdict |
| --- | --- | --- | --- | --- |
| DryWetMIDI (MIT, maintained) | yes | **no device API** (files only) | Windows only | no Linux devices |
| NAudio.Midi (MIT) | yes (WinMM) | no | no | Windows only |
| managed-midi / Commons.Music.Midi (MIT) | WinMM | ALSA | partial | unmaintained (archived) |
| RtMidi.Core (wraps native RtMidi) | yes | ALSA/JACK | no | unmaintained, ships a native lib per RID |
| Windows MIDI Services SDK (MIDI 2.0) | Windows 11 with the service + SDK runtime installed | — | yes | not on every Windows; WinMM is served by it anyway |
| **Own P/Invoke: WinMM + ALSA sequencer** | every Windows | every Linux with alsa-lib | ALSA: yes (System:Announce); Windows: polling | **chosen** |

No managed library is maintained *and* covers Linux devices, so Cuebot binds the two system APIs itself (~600 lines):

- **Windows: WinMM** (`winmm.dll`, `midiIn*`/`midiOut*`). Every MIDI driver serves it; on Windows 11 with Windows MIDI
  Services installed, WinMM goes through that service, which makes ports multi-client and adds its loopback/network
  endpoints. Input uses `CALLBACK_FUNCTION` (an `UnmanagedCallersOnly` stdcall function); the callback only queues the
  message, as WinMM forbids calling winmm from it, and a worker thread decodes it. SysEx input uses four 4 KiB
  `MIDIHDR` buffers re-queued by the worker. Output: `midiOutShortMsg`, SysEx with `midiOutLongMsg` (waits for
  `MHDR_DONE`, at most 5 s). WinMM has no device-change notification, so `MidiService` re-reads the port list every
  2 s (`MonitorIntervalMs`); port ids are indexes that shift when devices change, so ports are always opened **by name**.
  Without Windows MIDI Services a WinMM port is single-client: a device open in another program reports "in use".
- **Linux: ALSA sequencer** (`libasound.so.2`, `/dev/snd/seq`). Every USB/hardware port appears there, as do PipeWire's
  and JACK's bridged ports, software synths and `Midi Through`. Cuebot is one sequencer client named "Cuebot" with an
  input port (subscribed from each opened device and from `System:Announce` 0:1, which reports port start/exit for
  hot-plug) and an output port (events are sent straight to the device's address, no subscription). Raw bytes are
  converted with ALSA's own MIDI coder (`snd_midi_event_encode`/`decode`, running status off); only the fixed
  snd_seq_event_t header (type @0, queue @3, source @12, dest @14, data @16) and SysEx `data.ext` (packed len/ptr) are
  read by offset. Long SysEx arriving in chunks is reassembled up to F7. Port names: the port name, prefixed with
  "client: " when it does not already start with the client name; duplicates get " #2", " #3".
- Other systems get `UnavailableBackend`.

Degrading: no library (`DllNotFoundException`), no `/dev/snd/seq` (module not loaded, sandbox), or no devices all
produce an empty port list and `UnavailableReason`/"not connected" messages; nothing throws into the app. The unit
tests never open a port; one test only lists the real backend's ports to prove this on CI.

### Packaging

- **Flatpak**: `--device=all` (the sequencer node `/dev/snd/seq` is not covered by `--device=dri`; Flathub MIDI apps use
  the same permission). `libasound` comes from `org.freedesktop.Platform`.
- **AppImage / tarball**: `libasound.so.2` is taken from the host (it is on every desktop distro); not bundled.
- **AUR**: `alsa-lib` added as an optional dependency (MIDI only).
- The user needs access to `/dev/snd/seq` (normally the logged-in seat or the `audio` group).

## Connections

`midi.json` keeps `inputs` and `outputs` (separate lists; the same device may be in both). Each has `name`, `device`
(system port name), `autoOpen` (open at start), `reopenOnClose`, `retryIntervalSeconds` (default 5) and, for inputs,
`events`. States: **Open**, **Closed** (by the user, or lost without reopen), **Waiting** (lost or failed with
Reopen on Close: retried every retry interval), **Error** (failed without reopen).

A connection is lost when its port disappears from the port list (unplugged), when the backend reports it (ALSA port
exit / unsubscription, WinMM `MIM_CLOSE`) or when a send fails. Sub-actions and script sends open a closed MIDI Out
connection on demand. Clock (F8) and active sensing (FE) are dropped unless `ignoreClockAndSensing` is false.

## Messages

| Command (`commandCode`) | `commandCodeNum` | Data (listener) | `value` | Extra variables |
| --- | --- | --- | --- | --- |
| NoteOn | 144 | note | velocity | noteNumber, noteName, velocity, velocityNormal |
| NoteOff (also NoteOn with velocity 0) | 128 | note | velocity | same |
| KeyAfterTouch | 160 | note | pressure | noteNumber, noteName |
| ControlChange | 176 | controller | value | controllerNumber, controllerName |
| PatchChange | 192 | program | program | |
| ChannelAfterTouch | 208 | pressure | pressure | |
| PitchWheelChange | 224 | LSB | 0-16383 (8192 = centre) | |
| Sysex | 240 | (raw bytes compared) | 0 | |
| TimeCodeQuarterFrame, SongPosition, SongSelect, TuneRequest, TimingClock, StartSequence, ContinueSequence, StopSequence, AutoSensing, SystemReset | status byte | data1 | | |

Names follow Streamer.bot's command names. Notes: middle C (60) = `C4`. Controller names come from the MIDI 1.0 control
change table (`Pan` = 10, `MainVolume` = 7, `Sustain` = 64, `...Lsb` for 32-63; undefined numbers are the number).

### MIDI Message trigger (`midi.message`)

Settings: **Device** (a MIDI In connection's name, id or system device name, or Any) and **Event** (a listener's name or
id, or Any = every message from the device, matched or not).

Variables (Streamer.bot names): `channel` (1-16, 0 for system messages), `commandCode`, `commandCodeNum`,
`controllerName`, `controllerNumber`, `deviceName` (system port name), `name` (the connection's name), `noteName`,
`noteNumber`, `rawMessage` (the packed short message `status | data1 << 8 | data2 << 16` as in WinMM, e.g. `4209808`;
SysEx: hex bytes), `timestamp` (ms since the port opened), `value`, `valueNormal` (value / 127, pitch wheel / 16383),
`velocity`, `velocityNormal`. Cuebot extras: `rawBytes` (hex, e.g. `90 3C 40`), `deviceId`, `eventName` / `eventId`
(first matching listener, empty when none), `eventNames` / `eventIds` (all matches, newline-separated).

### Event listeners

A listener matches when it is enabled; the command is equal; the channel is equal unless **Ignore Channel** (system
messages have no channel); and the data byte is equal unless **Ignore Data** (SysEx compares the whole raw message).
**Ignore NoteOff** never matches NoteOff and makes learning skip NoteOff, so a keyboard's release does not overwrite the
NoteOn it just learned. **Learn** (automatic detection) waits for the next message and fills Command, Channel, Data,
Raw Message and, if empty, Name (e.g. `ControlChange Pan (ch 3)`).

## Sub-actions

| Type id | Name | Parameters |
| --- | --- | --- |
| `midi.noteOn` | Note On | name (label), device, channel (empty = all 16), note (0-127 or `C4`), velocity, duration (ms), sendNoteOff |
| `midi.controlChange` | Control Change | name, device, channel, controller, value |
| `midi.genericEvent` | Generic Event | name, device, command (NoteOn, NoteOff, ControlChange, PatchChange, ChannelAfterTouch, PitchWheelChange), channel, data1, data2 |
| `midi.sysexEvent` | Sysex Event | name, device, data (hex; F0/F7 added when missing) |

Note On with Send Note Off waits for the duration, then sends NoteOff (velocity 0). Pitch wheel: data1 = LSB and
data2 = MSB, or data1 = the whole 0-16383 value when data2 is empty. Streamer.bot's "Learn From" in the sub-action
dialogs has no equivalent in the generated parameter editors; the MIDI page's incoming-message monitor shows the
numbers to copy.

## Script functions

Streamer.bot documents no C# MIDI methods, so there are no CPH MIDI methods; scripts use: `midi.isAvailable`,
`midi.status`, `midi.ports {direction}`, `midi.devices`, `midi.open {device, direction}`, `midi.close`,
`midi.sendNoteOn {device, channel, note, velocity, duration, noteOff}`, `midi.sendNoteOff`,
`midi.sendControlChange {device, channel, controller, value}`, `midi.sendEvent {device, command, channel, data1, data2}`,
`midi.sendSysex {device, data}`, `midi.sendRaw {device, data}` (any one complete message in hex).

## Not implemented / not verified

- MIDI 2.0 / UMP (Windows MIDI Services SDK) and virtual ports Cuebot would create for other apps.
- Verified on Windows (WinMM listing and output to Microsoft GS Wavetable Synth). MIDI input through WinMM and the whole
  ALSA backend are covered by the opt-in live tests (`docs/LIVE_TESTING.md`) but have not yet been run on hardware.
