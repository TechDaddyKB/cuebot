# Streamer profiles (personas)

Some streamers, VTubers most of all, stream as more than one persona. Each persona has its own Twitch, YouTube, Kick
and Rumble accounts, its own mods, donation services, webhooks, Discord bot, actions, commands, overlays and viewers.
A **profile** is one persona's whole Cuebot; the streamer switches between them in a click.

## Decisions

| # | Decision | Choice |
| --- | --- | --- |
| 1 | What a profile is | One data folder, `profiles/<id>/`, holding everything a single Cuebot holds today (settings files, overlays, uploads, `secrets.bin`). Modules do not know profiles exist: `AppPaths.Root` is the active profile's folder. |
| 2 | What is shared | The computer, not the persona: `app.json` (theme, window, tray, text format, backups), `updates.json`, `profiles.json`, logs, backups, crashes, reports, and the big downloads (`python`, `piper`, `models`, `cache`). `AppPaths.Shared` is the data folder itself. |
| 3 | How many run | One profile at a time; one Cuebot per data folder, as now (the lock is on the shared folder). Two personas live at once needs separate ports and processes; later, if asked. |
| 4 | Switching | Save, then restart on the other profile (the existing restart path), with the window where it was. No start-up backup on a switch. In-process swapping can come later; a restart cannot leak one persona's state into the other. |
| 5 | Ids | A profile's folder name is a slug of its first name (`main`, `kitsune`, `kitsune-2`) and never changes; renaming changes the shown name only. |
| 6 | Existing data | On the first start of this version the data folder's persona files move into `profiles/main/` ("Main"), after a backup. The move is resumable: entries go one by one into the profile folder and `profiles.json` is written last. Secrets of `app.json` (the Google Drive sign-in) are copied into the shared folder's store; the profile's key is re-saved for its new folder (the Linux keyring keeps one item per folder). |
| 7 | New profiles | Empty, or copied from another profile by parts: actions/timers/queues, commands, overlays and widgets, decks/hotkeys/controllers, variables, stream apps/devices/servers, voices. Sign-ins of platforms, mods (portal), donations, webhooks, the Discord bot and the relay are never copied, so a new persona never posts as the old one. Secrets of the copied files (an OBS password, a TTS key, the REST token) are copied with them. |
| 8 | Telling them apart | A name and a colour (and later an avatar), shown in the top bar and the tray, and in the window title. |
| 9 | Backups | Still one zip of the whole data folder (every profile). Secret files are left out at any depth, and a restore keeps them at any depth. |
| 10 | Starting on a profile | `--profile <name or id>`. A second launch with `--profile` asks the running Cuebot to switch. |

## Phases

| Phase | What | Done when |
| --- | --- | --- |
| 1 ✅ | Shared and profile folders, the move, `ProfileCatalog` (create, copy by parts, rename, colour, delete), switching by restart, `--profile`, the top-bar profile chip and menu, the tray's Profiles menu, profiles in the Ctrl+K jump box, a Profiles tab in Settings, docs | Two profiles with different Twitch accounts switch both ways; tests for the move, the catalog, copying with secrets, backups and the options |
| 2 | Triggers **Profile started** / **Profile stopping**, a **Switch profile** sub-action, a live guard (asks first while streaming), REST `GET /profiles`, `POST /profiles/{id}/activate`, the phone app showing and switching the persona | A deck button switches persona and the started action changes the OBS scene collection |
| 3 | Copy single actions, commands and overlays to another profile; desk connections (OBS, Stream Deck, MIDI, lights) shared by default with a per-profile "own settings" switch; restoring one profile from a backup; avatars and a tinted tray icon | — |
| Later | Two personas live at once | — |

## Things to say in the docs

- Only the active persona is online: its mod portal, relay webhooks (Ko-fi, Patreon…) and chat. Mods of another
  persona see Cuebot as offline; webhooks for it are missed (the relay stores nothing).
- OBS browser sources point at one persona's overlays; give each persona its own OBS scene collection.

## Phase 1 notes (2026-11-06)

- `AppPaths` has `Shared` / `SharedFile` / `GetSharedDirectory`; `app.json`, `updates.json`, logs, crashes, reports,
  `python`, `piper`, `models/whisper` and the emote caches use them. `ProfileCatalog` (Cuebot.Core/Profiles) owns
  `profiles.json`, the move, create/copy/rename/colour/delete. `SecretStore.CopyFileTo` copies one file's secrets.
- Program: the lock stays on the data folder; `Prepare` backs up (`before-profiles`) and moves before opening the
  profile; `--profile` picks one, or asks a running Cuebot to switch (`profile <name>` on the instance socket). A switch
  sets the profile active and uses the normal restart, with `CUEBOT_PROFILE_SWITCH` so the new process skips the
  start-up backup.
- UI: the top bar's profile chip and menu, the window title, `Profile: name` in the jump box, the tray's Profiles
  menu, Settings > Profiles.
- Checked live: an old-layout folder made by the previous build moved into `profiles/main` with its stored sign-ins
  readable; switching headless and with the window (on Xvfb) via `--profile` restarted on the other profile, and the
  servers bound their ports again. Not checked live: the Linux keyring path of the move (tests and the live run used
  the key file), Windows and macOS.
