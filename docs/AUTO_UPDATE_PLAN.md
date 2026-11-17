# Cuebot auto-update and companion compatibility: plan

- **Status (2026-11-17):** proposal, nothing built. Written while v1.0.0 is being released. Section 11 lists the
  decisions Kris needs to make before phase 1 starts.
- **Owner:** Kris.
- **Builders:** the Linux session (DailyDriver) builds the desktop updater, the server side of the compatibility work
  and the release pipeline. The Android work runs where the Android tests run. The Mac session builds the iOS side and,
  later, the macOS updater.
- **Scope:** Cuebot on the desktop (Windows, Linux, macOS) and Cuebot Companion on Android and iOS. The Stream Deck and
  StreamController plugins are mentioned only where they share a mechanism.

## 1. Goals and non-goals

**Goals**
- Cuebot updates itself on Windows (zip) and Linux (tarball, AppImage), with the streamer's approval, never while they
  are live, and can always go back.
- Every downloaded update is checked against a signed list of checksums before anything is unpacked.
- A broken update rolls itself back without the streamer needing to understand what happened.
- The phone apps work with a Cuebot that is up to **three API levels older or newer** than they are, hiding what the
  other side lacks and saying in plain words which side needs updating.
- Some things change without a phone app release: texts, hints, which dashboard cards appear, simple new controls.
- One release pipeline produces everything: signed manifest, checksums, pre-release first, then a staged stable
  rollout, and a way to withdraw a bad release.

**Not in v1 of this plan**
- Delta (binary diff) updates. A full package is 60 to 120 MB; that is acceptable once per release.
- Self-update for Flatpak and AUR installs. Their package managers own them; Cuebot shows instructions, as today.
- Self-update on macOS until the app is Developer ID signed and notarized (section 4.5).
- Silent updates with no approval at all. Even "Update automatically after my stream" (section 4.1) is something the
  streamer turns on.
- Updating the phone apps from inside the desktop app on iOS (impossible) or on Play builds (not allowed).

## 2. Proposed decisions

| # | Topic | Proposal |
|---|---|---|
| 1 | Updater | **Custom** updater with a small signed helper, not Velopack (section 4.9). Keeps the portable Windows zip and the Linux tarball as they are. |
| 2 | Integrity | Each release publishes `cuebot-release.json` (SHA-256 and size of every asset), signed with Ed25519. The private key is a CI secret; the public keys are compiled into Cuebot. |
| 3 | Live rule | Never install while any platform is live. "After my stream" waits until the stream has been over for 10 minutes. |
| 4 | Safety net | A pinned `before-update-vX` backup that pruning skips, the previous program folder kept, automatic rollback after two failed starts, and a "Go back to vX" button. |
| 5 | Compatibility | An integer `apiLevel`, a `minCompanionLevel` and a feature map on `/api/v1/status`. The apps send their own level in a header. Support window: 3 levels each way, and at least 90 days. |
| 6 | Deprecation | Deprecated endpoints and fields stay for at least 3 API levels **and** 90 days after their replacement reached stable. |
| 7 | Android channels | Google Play is the main channel. The GitHub APK stays for people without Play. The paired PC offers the matching APK to sideload users later. One signing key for all three. |
| 8 | Rollout | Pre-release channel first (at least 3 days), then stable at 10 %, 50 %, 100 % over about a week. |

## 3. What exists today

### Desktop updates
- `src/Cuebot.App/Updates/UpdateChecker.cs` asks the GitHub Releases API (`/releases/latest` for stable, `/releases`
  for the pre-release channel) and compares the tag with this build using `SemVersion`
  (`src/Cuebot.App/Updates/SemVersion.cs`, SemVer 2.0 ordering, leading `v` allowed). It is read-only by design: "Cuebot
  never installs updates itself".
- `src/Cuebot.App/Updates/UpdateSettings.cs` stores `updates.json` in the shared data folder: check on start (at most
  once per 24 hours), the channel, the last check and its result.
- `src/Cuebot.App/Updates/Packaging.cs` detects how Cuebot was installed (`WindowsZip`, `AppImage`, `Flatpak`,
  `SystemPackage` for AUR, `LinuxTarball`, `MacApp`, `Other`), names the matching release asset and holds per-format
  install instructions. This is the right switch for the new updater, too.
- `src/Cuebot.App/ViewModels/Support/UpdatesViewModel.cs` shows the version, the release notes and a **Download**
  button that opens the asset URL in the browser. Nothing is downloaded by Cuebot.
- There are no checksums and no signatures on the release. Windows files are Authenticode-signed with Azure Artifact
  Signing (`docs/CODE_SIGNING.md`, `windows-package` job in `.github/workflows/build.yml`); Linux and macOS builds are
  not signed (macOS is ad-hoc signed, see `docs/IOS_COMPANION_PLAN.md` section 8).
- The data folder is separate from the program (`src/Cuebot.Core/AppPaths.cs`: `%APPDATA%\Cuebot`,
  `~/.config/Cuebot`, `~/Library/Application Support/Cuebot`, or `CUEBOT_DATA`). Replacing the program folder never
  touches the streamer's data.

### Backups
- `src/Cuebot.App/Shell/BackupService.cs` zips the data folder (every profile) into `backups/` as
  `cuebot-yyyyMMdd-HHmmss-reason-vVersion.zip`, keeps the newest N (`Prune`), and never includes or replaces the secret
  store and keys. Restores are scheduled (`ScheduleRestore`, a `restore.pending` marker) and applied at the next start
  before any store opens (`ApplyPendingRestore`), after a `pre-restore` safety backup.
- `src/Cuebot.App/Program.cs` (`Prepare`) applies a pending restore, then makes the `startup` backup (skipped when
  relaunched for a profile switch), then the `before-profiles` backup when needed.
- `Prune` already skips one file (the pending restore). Pinning extends that rule.

### API and companion apps
- `src/Cuebot.Server/Api/ApiOperations.cs` (`Status()`) returns `app.version`, `app.apiVersion: "v1"`, connections,
  counts, chat platforms and servers. There is no level and no feature list.
- `src/Cuebot.Server/Api/ApiOperations.Companion.cs` serves the dashboard (`/api/v1/dashboard`, with a `revision` that
  matches `dashboard.changed` events), subathon timers and goals, and subathon history.
- `src/Cuebot.Server/Api/ApiServer.cs` routes on the first path segment (`status`, `dashboard`, `subathon`, `goals`,
  `portal`, `profiles`, `decks`, `chat`, ...). `src/Cuebot.Server/Api/OpenApiDocument.cs` describes the API.
- `src/Cuebot.Server/Api/PairingLink.cs` builds `cuebot://pair?v=1&...` (`Version = 1`). Android's `Endpoint.kt`
  rejects an unknown `v` with "This pairing code is from a newer Cuebot. Update Cuebot Companion."
- Android: `clients/android/core/src/main/kotlin/digital/techdaddy/cuebot/core/Tolerant.kt` reads JSON tolerantly
  (missing or mistyped fields fall back to defaults), the same rule as iOS's `CuebotKit/Sources/CuebotKit/Tolerant.swift`.
  `CuebotClient.kt` maps 404 to `NotFound`, and `DashboardViewModel.kt` and `ApprovalsViewModel.kt` turn that into a
  `tooOld` state ("Cuebot is older than this endpoint"). iOS's `CuebotClient.swift` does the same (`.notFound`).
- The apps send no version of their own. Cuebot cannot tell which app versions talk to it.
- Mod portal crypto: `PortalCrypto.kt` / `PortalCrypto.swift` use the labels `cuebot-portal-v1` and
  `cuebot-portal-device-v1`; the relay has `PROTOCOL_VERSION = 1` (`portal/worker/protocol.ts`).

### Release pipeline
- `.github/workflows/release.yml` runs on `v*` tags: build, Linux tarballs, the Android APK (signed by
  `clients/android/ci/release.sh`; `versionCode` is the workflow run number), then `gh release create` with all assets.
  A tag with a hyphen is a GitHub pre-release and skips the AUR.
- `.github/workflows/android.yml` runs the Android tests and, by hand, a signed release build to prove the secrets work.
- `.github/workflows/ios.yml` runs CuebotKit's tests; TestFlight uploads are planned (`docs/IOS_COMPANION_PLAN.md`
  section 8) and the Apple and Google developer portals are being set up now.

## 4. Desktop updates with the streamer's approval

### 4.1 Detect, then ask

The existing check (`UpdateChecker`) stays, but reads the signed channel index (section 6.2) first and falls back to
the GitHub API when the index cannot be reached. When a newer, non-withdrawn version is offered for this install
(rollout bucket, section 6.4), Cuebot shows one prompt:

> **Cuebot v1.2.0 is ready.** *Release notes (collapsed).*
> [Update now] [After my stream] [Skip this version]
> *Cuebot restarts in about 30 seconds. Your actions and settings stay; a backup is made first.*

- **Update now:** download, verify, back up, restart into the new version. Disabled while live, with the reason
  ("You are live on Twitch. Cuebot will offer this again when your stream ends.").
- **After my stream:** download and verify now, in the background, then wait. When no platform has been live for 10
  minutes and Cuebot is idle (no profile switch under way, no action queue busy), show a small toast with a
  60-second countdown and **Cancel**. A setting, off by default, skips the countdown: "Update automatically after my
  stream".
- **Skip this version:** stored in `updates.json` (`SkippedVersion`). A newer version asks again. Skipping is per
  version, never per channel.
- Closing the prompt counts as "ask me tomorrow".

**What "live" means.** `ProfileSwitcher.LivePlatforms` (`src/Cuebot.Core/Profiles/ProfileSwitcher.cs`, from the points
service) already blocks profile switches while live (`DesktopShell.cs`). The updater uses the same list, plus, when
OBS or Meld is connected, their streaming state, so a streamer with no platform account connected is still protected.
If no signal is available at all, Cuebot treats the state as unknown and only installs on **Update now**.

**Tell the phones first.** Before quitting for an update, Cuebot sends a `system.updating` event
(`{"from":"1.1.0","to":"1.2.0","expectedSeconds":30}`) on the REST event stream and the WebSocket, so the companion apps
show "Cuebot is updating to v1.2.0" instead of a connection error (section 5.12).

**Warn about the phones.** The release manifest carries the release's `minCompanionLevel`. If a paired phone was last
seen with a lower level (section 5.2), the prompt adds one line: "Your iPhone's Cuebot Companion is older than this
update supports. Remote control keeps working; some screens may ask you to update the app."

### 4.2 Download and verify

1. Fetch `cuebot-release.json` and `cuebot-release.json.sig` for the offered version (section 6.1).
2. Verify the signature against the compiled-in public keys. BouncyCastle already verifies Ed25519 for webhooks
   (`Directory.Packages.props`: "the .NET 10 BCL has no Ed25519 API"), so no new dependency.
3. Check `version` in the manifest equals the offered version (a valid old manifest must not be replayed for a new
   version) and that the version is not withdrawn.
4. Download the asset named by `PackagingDetector.AssetName` into `<data>/updates/staging/v1.2.0/`, resumable, with
   the size from the manifest as a cap.
5. Compare SHA-256 with the manifest. Any mismatch deletes the file and reports "The download was damaged or changed;
   nothing was installed."
6. On Windows, additionally check that `Cuebot.exe` in the unpacked folder carries a valid Authenticode signature from
   the same publisher as the running one. This is a second, independent check.
7. Unpack into a staging folder **next to** the program folder (`Cuebot.new`), so the later swap is a rename on the
   same volume. Unpacking rejects entries that point outside the target (the same check `BackupService.RestoreNow`
   does).

If the program folder's parent is not writable (for example someone unpacked into `C:\Program Files`), Cuebot does not
ask for elevation in v1. It keeps today's behaviour: open the verified download in the file manager and show the
manual instructions from `PackagingDetector.Instructions`.

### 4.3 The pinned backup

Just before quitting for the update, Cuebot calls `BackupService.Create("before-update-v1.1.0")`, which gives
`cuebot-20261201-210000-before-update-v1.1.0-v1.1.0.zip`. Changes to `BackupService.cs`:

- `Prune` skips every backup whose reason starts with `before-update-` (it already skips the pending restore). They do
  not count toward "keep N".
- Pinned backups are cleaned up by the updater, not by `Prune`: after an update has passed its health check, keep the
  newest three pinned backups and delete older ones. The backups page shows them with a pin icon, and the streamer can
  delete one by hand.
- The secret store is excluded from every backup already, so restoring a pinned backup never signs anyone out.

### 4.4 The helper and the swap

A small helper, `cuebot-update` (`cuebot-update.exe` on Windows), does the parts that cannot run inside Cuebot:

- Written in C#, published Native AOT (a few MB, no runtime needed), shipped inside every package and Authenticode-signed
  with Cuebot's files on Windows (the `Cuebot*` pattern in `docs/CODE_SIGNING.md` needs one more name).
- Cuebot copies it to a temporary folder before running it, so it never locks the folder it replaces.
- It only renames, starts and waits. It does not download, parse manifests or touch the data folder; Cuebot does all of
  that before quitting. Small surface, easy to test.
- It keeps a journal, `<data>/updates/update-state.json` (`staged`, `swapping`, `swapped`, `starting`, `healthy`,
  `rolled-back`), so an interrupted swap is finished or undone the next time the helper or Cuebot runs.

Per package kind (`PackagingDetector.Detect`):

| Package | What happens |
|---|---|
| Windows zip | Wait for Cuebot to exit (timeout 60 s). Rename `Cuebot` to `Cuebot.previous`, then `Cuebot.new` to `Cuebot`. Retry renames for up to 30 s (antivirus scanners and Explorer windows hold folders open). If a rename still fails, undo and report. Start the new `Cuebot.exe --after-update 1.1.0`. |
| Linux tarball | The same folder swap. Execute bits come from the tarball. |
| AppImage | The `APPIMAGE` variable names the file. Download next to it as `<name>.new`, verify, `chmod +x`, then after Cuebot exits rename the old file to `<name>.previous` and the new one into place. Desktop entries keep pointing at the same path. |
| Flatpak | No self-update. The prompt shows the instructions from `Packaging.cs` and the verified `.flatpak` link. A Flatpak repository (or Flathub) would let `flatpak update` do this; see section 11. |
| AUR (`SystemPackage`) | No self-update. The prompt shows "update with your package manager" as today. |
| macOS app | Not until Developer ID signing and notarization exist (section 4.5). Until then: today's download link, now with a verified checksum. |
| Other | Release page link, as today. |

Only one previous version is kept (`Cuebot.previous`). It is deleted when the next update passes its health check.

### 4.5 macOS

Replacing `Cuebot.app` in place needs:
- **Developer ID signing and notarization.** An ad-hoc signature changes with every build, and macOS ties privacy
  permissions (microphone, screen recording, accessibility) to the signing identity, so every update would ask for them
  again. Gatekeeper also treats an unnotarized replacement badly.
- The swap is the same idea (rename `Cuebot.app` to `Cuebot.previous.app`), done by the helper outside the bundle.
- If Cuebot runs from the disk image or a read-only location (App Translocation), fall back to the download link.

This becomes phase 8, after the Apple developer account (being set up in another session) can sign the desktop app.

### 4.6 Health check and automatic rollback

An update counts as **healthy** when the new version has, within 3 minutes of starting: opened its profile, started
the REST API server and shown the main window, and then run for 60 seconds without crashing.

Two layers catch failures:
1. **The helper watches the first start.** It starts the new version and waits for a `healthy` marker in
   `update-state.json`. If the process exits early or no marker appears in time, it starts it **once more**. Two failed
   starts mean rollback. This also catches crashes before `Main` runs (a missing native library, a broken runtime).
2. **Cuebot counts its own starts.** Until the state says `healthy`, the first lines of `Program.Main` increase a start
   counter in `update-state.json`, and the healthy point resets it. If a later start (for example the next day, after
   the helper has gone) finds the counter at 2 without a healthy mark, Cuebot launches the helper in rollback mode
   and exits.

**Rollback** (the helper):
1. Rename `Cuebot` to `Cuebot.failed-v1.2.0` and `Cuebot.previous` back to `Cuebot`.
2. Schedule the pinned backup with the existing restore marker (`restore.pending`), so the old version restores it at
   start through `ApplyPendingRestore`, with its usual `pre-restore` safety backup of whatever the failed version wrote.
3. Start the old version with `--rolled-back-from 1.2.0`.

The old version then shows: "Cuebot v1.2.0 did not start properly, so Cuebot went back to v1.1.0 and restored your
data from just before the update. [Send a problem report] [OK]". The report uses the existing feedback flow
(`ApiOperations.Feedback.cs`) and attaches the failed version's start-up log. v1.2.0 is marked skipped; a newer
version (for example a fixed v1.2.1) asks again.

### 4.7 "Go back to vX"

On the Updates page, while `Cuebot.previous` and a pinned backup exist: **Go back to v1.1.0**. It asks one question:

- **Go back and restore my data from before the update (recommended).** Changes made since the update are lost, but
  the older version gets data it understands. The current data is kept as a `pre-restore` backup.
- **Go back and keep my current data.** For the case where the update only changed the program. A warning explains that
  v1.1.0 may not understand settings created by v1.2.0.

Going back is not allowed while live, and uses the same helper path as an automatic rollback. Several stores carry
their own format version (for example `servers.json` version 3, `docs/SERVERS.md`); every store that migrates on load
must keep a copy of the pre-migration file or refuse to write a newer format it does not know, so "keep my current
data" fails safely rather than silently.

### 4.8 Withdrawn releases

The signed channel index (section 6.2) lists withdrawn versions with a short reason. Cuebot:
- never offers a withdrawn version;
- if it is running a withdrawn version, shows a banner: "v1.2.0 was withdrawn: *reason*. [Go back to v1.1.0] [Update
  to v1.2.1]" (whichever exists);
- if the index marks the withdrawal `critical`, schedules **After my stream** for the fix (or the go-back) without
  waiting for a click, still with the countdown and **Cancel**.

### 4.9 Custom updater or Velopack

Velopack (MIT, the successor to Squirrel) builds installers and update packages with `vpk pack` and updates apps
through an `UpdateManager` API. Its facts below are from its documentation at the time of writing; recheck before
deciding.

| | Custom | Velopack |
|---|---|---|
| Windows portable zip | Kept exactly as today | Has its own portable package, but with its layout (`current/` folder plus `Update.exe`), so existing zip users would have to move once |
| Linux tarball | Supported | Not supported (AppImage only) |
| AppImage | Supported | Supported |
| Flatpak, AUR | Instructions (same) | Not supported (same) |
| macOS | Phase 8, our swap | Supported, with its own notarization tooling |
| Delta updates | No (not needed in v1) | Yes |
| Live-aware deferral, pinned backup, two-start rollback, go back, withdrawn flag, staged rollout | We build all of it | Mostly still ours: Velopack downloads and applies; policy stays with the app |
| Signed manifest | Ed25519 over SHA-256 list | Checksums in its release feed; no feed signature of its own, relies on HTTPS and code signing |
| File locks, restarts on Windows | We handle (retries, journal) | Mature handling |
| Dependency and lock-in | None | Packaging, layout and feed format follow Velopack |

**Recommendation: custom.** The hard parts here are policy (live rule, backups, rollback, withdrawal, rollout) and the
compatibility work in section 5; Velopack does not do those. It would change the Windows layout, drop the tarball and
leave Flatpak and AUR exactly where they are. The custom piece that Velopack would replace (download, verify, swap,
restart) is roughly a week of work. Revisit if macOS self-update proves painful or if download size becomes a complaint
(deltas).

## 5. Companion compatibility across versions (main focus)

### 5.1 The problem

Desktop releases go out the day they are tagged and, with this plan, reach most streamers within a week. The phone apps
lag: Google Play review usually takes hours to a few days; iOS goes through App Review and then a 7-day phased release,
and iPhones install updates when iOS decides to. Some people turn automatic updates off; some never open the store.
Streamers also hold back the desktop (skip a version, go back, AUR users who update monthly).

So any pairing of app and Cuebot can be **up to three levels apart in either direction**, and that is normal, not an
error. Today the apps survive this by luck: tolerant JSON plus 404 = "too old" works for whole new endpoints, but not
for a changed field, a new required parameter, a renamed event or a removed endpoint, and Cuebot never learns which app
versions talk to it.

### 5.2 API level, feature map and the app's level

**Server side.** `/api/v1/status` gains, inside `app`:

```json
"app": {
  "version": "1.3.0",
  "apiVersion": "v1",
  "apiLevel": 4,
  "minCompanionLevel": 2,
  "features": {
    "dashboard": 2, "subathon": 2, "subathonHistory": 1, "approvals": 1, "chatModeration": 2,
    "eventsCard": 1, "deck": 1, "modPortal": 1, "profiles": 1, "companionConfig": 1
  },
  "companion": { "android": { "version": "1.3.0", "versionCode": 412 }, "ios": { "version": "1.3.0" } }
}
```

- `apiLevel`: one integer for the whole REST and event-stream contract that the apps use. It lives in one place, a new
  `src/Cuebot.Server/Api/ApiLevel.cs` (`Current`, `MinCompanion`, `Features`), not in the release version files.
  `apiVersion: "v1"` stays as the URL prefix; it changes only if `/api/v2` ever exists.
- `features`: each entry is a feature's own small version. The apps decide by feature, not by level arithmetic. A
  missing key means "this Cuebot does not have it".
- `minCompanionLevel`: the oldest app level Cuebot still fully serves.
- `companion`: the newest app versions this Cuebot release knows about (from the release manifest), so an app can say
  "a newer Cuebot Companion exists" without asking a store.
- A Cuebot without `apiLevel` (v1.0.x) counts as **level 1** with the features v1.0.0 has; the apps keep the 404
  probes for that case.

**App side.** Every request carries:

```
X-Cuebot-Client: companion-android/1.3.0 level=4
```

(`companion-ios/...` on iOS; added next to `Authorization` in `CuebotClient.kt` `authorized()` and
`CuebotClient.swift`.) The level is a constant built into each app: the newest server level it was built against
(`BuildConfig.CUEBOT_API_LEVEL` on Android, a constant in CuebotKit on iOS). Each app also has a built-in
`minServerLevel`.

Cuebot remembers, per paired device (the pairing `id`), the last client string and when it was seen. It uses that
for:
- the desktop's paired-devices list ("iPhone: Cuebot Companion 1.1.0, two levels behind");
- the warning in the update prompt (section 4.1);
- compatibility shims, where a field kept for old apps can be left out for new ones (rarely needed; adding fields is
  free).

Cuebot never rejects a request because of the header. Requests without it (curl, Stream Deck plugin, older apps) are
treated as level 1.

### 5.3 How the apps decide

On every connect and reconnect (not just at pairing, because Cuebot can move up or down at any time):

1. Read `/api/v1/status`. Store `apiLevel`, `features`, `minCompanionLevel`.
2. For each screen and control, check its feature key and minimum feature version. Missing or too low: hide it, or show
   it greyed with "Update Cuebot on your PC to use this" (one line, links to a help page).
3. Keep the 404 handling as a second line of defence: a 404 on a feature that status claimed sets `tooOld` for that
   screen and logs it.
4. Never cache an assumption across reconnects. A rollback to an older Cuebot must make screens disappear again.

Android: a small `ServerCapabilities` in `clients/android/core` next to `Tolerant.kt`, exposed to the view models;
`DashboardViewModel` and `ApprovalsViewModel` read it instead of finding out by 404. iOS: the same type in CuebotKit.

### 5.4 Banners

| Situation | What the app shows |
|---|---|
| Within the window, some features missing | No banner. Per-screen hint "Update Cuebot on your PC to use this". |
| Cuebot level below the app's `minServerLevel` | Top banner: "Cuebot on your PC is too old for this app. Update Cuebot to v1.x or newer." Screens that still work stay usable. |
| App level below Cuebot's `minCompanionLevel` | Top banner: "Update Cuebot Companion to keep everything working." Button to the store (or, on sideload builds, to the APK, section 5.10). Still usable as far as it goes. |
| Cuebot more than 3 levels newer than the app | Dismissable banner: "Cuebot Companion is behind your PC. Some new features need an app update." |
| Cuebot is updating (`system.updating`) | "Cuebot is updating to v1.2.0..." in place of the connection error, for up to 3 minutes. |
| Cuebot came back older than before | One-time note: "Cuebot on your PC went back to v1.1.0; some screens are hidden." |

The app never refuses to run. The worst case is a read-only, limited app with a clear banner.

### 5.5 Deprecation rules on the server

- Additive changes (new endpoint, new field, new event type, new optional parameter) are free and bump `apiLevel`.
- A field or endpoint the apps use is never renamed in place. Add the new one, keep the old one, and list it in a new
  `docs/API_DEPRECATIONS.md` with the level it was deprecated at and the earliest removal: **at least 3 levels later and
  at least 90 days after the replacement reached a stable release, whichever is later**.
- `minCompanionLevel` is raised only to a level whose apps have been at 100 % on Play and on the App Store for at least
  four weeks. It never moves ahead of what users can actually install.
- Event names on the event stream follow the same rule: new events are free; a renamed event is sent under both names
  for the window.
- Semantics count as a change: if `POST .../time` starts meaning something different, that is a new parameter or a new
  path, never a quiet change.

### 5.6 Contract tests with saved fixtures

- **Server responses.** A new test in the server tests runs `ApiOperations` against a seeded test profile and writes
  golden responses (status, dashboard, subathon, history, approvals, profiles, deck, chat, a recorded event stream) to
  `clients/contract-fixtures/main/`. At every stable release CI copies them to `clients/contract-fixtures/v1.2.0/` and
  they never change again.
- **App side.** Android core tests (`android.yml`, `:core:test`) and CuebotKit tests (`ios.yml`) parse every fixture
  folder of **the last three stable releases plus `main`**: no exceptions, required fields present, capabilities
  computed as expected (for example, v1.0.0 fixtures give "approvals hidden").
- **Server side.** The apps' requests are recorded too (`clients/contract-fixtures/requests/<app version>/`, from the
  client tests). The server test replays the requests of the last three app releases against the current server and
  expects success and the fields those apps read. This is what enforces the deprecation window.
- **Schema diff.** CI compares the current `openapi.json` (`OpenApiDocument.cs`) with the last release's. A removed or
  renamed property fails unless `API_DEPRECATIONS.md` lists it as past its removal date. Any difference without an
  `ApiLevel.Current` bump fails too.

### 5.7 What the server can change without an app release

A new endpoint, `GET /api/v1/companion/config` (feature `companionConfig`), sends:

- **Texts:** hints, empty states, feature descriptions, help links, "what's new" notes, with a language tag. The app has
  built-in defaults and uses the server's text when present.
- **Dashboard layout:** an ordered list of cards by type (`connections`, `totals`, `subathon`, `goals`,
  `topSpenders`, `activity`, ...). The app renders the types it knows and skips the rest. The dashboard snapshot
  (`ApiOperations.Companion.cs`) already holds the data; this adds order and visibility.
- **Generic cards:** a small fixed set of building blocks the app renders from JSON: a stat row, a list, a button, a
  toggle, a number stepper, a confirm dialog. A button calls an API path from an allowlist with the app's own token.
  This lets a new desktop feature appear in older apps in a basic form. The Deck tab already works this way: buttons
  come from Cuebot.
- **Limits and validation:** maximum lengths, value ranges and allowed options come from the server instead of being
  compiled into the app.
- **Quirks and kill switches:** for example `{"disable": ["chatImages"], "forClients": "companion-ios<1.2.1"}`, to
  switch off a screen known to misbehave with this Cuebot.

What stays in app releases: new native permissions, navigation, crypto, pairing, anything that needs new native UI.
Apple's guidelines forbid downloading code that changes the app's features; JSON that configures built-in components is
normal practice, but the generic cards must stay within the app's described purpose (remote control of the user's own
Cuebot).

### 5.8 Pairing and mod-portal crypto

- **Pairing link** (`PairingLink.cs`, `v=1`): new parameters are added without changing `v`; the apps already ignore
  unknown query parameters and must keep doing so (a fixture test locks it in). `v` changes only for a breaking change,
  and then Cuebot can still show a `v=1` code ("Pair an older app") for the support window.
- **Tokens:** an update never changes the API token, the deck token or the ports. The secret store stays outside
  backups and the program folder, so updates and rollbacks keep every pairing.
- **Portal crypto** (`cuebot-portal-v1`, relay `PROTOCOL_VERSION = 1`): the relay is one deployed Worker shared by every
  Cuebot and every app version in the field, so it must accept every protocol version still in the window. The
  handshake gets a list of supported versions from each side (`"protocols": [1, 2]`), and both ends pick the highest
  shared one. A new crypto suite is added beside the old one, never in place; the old one is removed only after the
  window, and the relay last. `PortalCryptoTests` and `PortalCryptoTest.kt` keep test vectors of every supported
  version.

### 5.9 Feature list to start with

| Key | First level | Notes |
|---|---|---|
| `dashboard` | 1 | `/api/v1/dashboard` and `dashboard.changed` |
| `subathon` | 1 | timer and goal commands |
| `subathonHistory` | 1 | history with corrections (newest commit on `main`) |
| `deck` | 1 | deck routes and deck token |
| `profiles` | 1 | `/api/v1/profiles`, 503 when unavailable |
| `chatModeration` | 1 | per `docs/UNIFIED_MOD_CHAT_PLAN.md` |
| `approvals` | 1 | mod portal approvals from the phone |
| `modPortal` | 1 | portal pairing and relay |
| `eventsCard` | 1 | activity card |
| `companionConfig` | 2 | section 5.7 |

The exact list is fixed in phase 1 by reading what each app screen calls.

### 5.10 Android: from the paired PC, from GitHub, or from Play

| | From the paired PC | GitHub release APK | Google Play |
|---|---|---|---|
| Matches the desktop version | Always (Cuebot offers the APK of its own release) | If the user picks the right one | No: whatever Play has rolled out |
| Updates automatically | No, but Cuebot can tell the app "a matching APK is ready" | No | Yes |
| Review delay | None | None | Hours to days |
| Needs "install unknown apps" | Yes | Yes | No |
| Allowed to self-update | Yes (sideload build) | Yes (sideload build) | No; Play policy restricts `REQUEST_INSTALL_PACKAGES` |
| Effort | Medium | Exists today | Store listing, policies, testing track |

**Recommendation:**
- **Play is the main channel** for most users, with its staged rollout.
- **GitHub APK stays** (as `release.yml` produces it now) for people without Play.
- **From the PC** later (phase 7) as a convenience for sideload users: Cuebot fetches the companion APK listed in its
  own release manifest, verifies its SHA-256, and serves it to paired phones at `GET /api/v1/companion/android`
  (version, versionCode, size, SHA-256) and a download path, token-protected. The app compares `versionCode`, downloads,
  checks the hash **and that the APK's signing certificate equals its own** (Android refuses otherwise anyway), then
  hands it to the package installer.
- Two build flavors: `play` (no install permission, no self-update code) and `github` (both). The app's level and
  features are the same in both.

**One signing key everywhere.** An app signed by Play's own key cannot be updated by the GitHub APK and the other way
round: users would have to uninstall and pair again. When enrolling in Play App Signing, upload the existing release
key (the one in `ANDROID_KEYSTORE_BASE64`) instead of letting Google create one. This has to be decided **before the
first Play upload**, which the portal-setup session is close to; see section 11.

`versionCode` is the release workflow's run number (`release.sh`), which only grows, so Play accepts every build.
Google requires new personal developer accounts to run a closed test with a minimum number of testers for two weeks
before production; plan the first Play release around that.

### 5.11 iOS: TestFlight and the App Store

- **TestFlight** (internal first, then external testers) gets every pre-release. External builds need a short beta
  review the first time per version; builds expire after 90 days.
- **App Store** with **phased release** over 7 days for automatic updates (anyone can update by hand right away).
  Phased release can be paused if a problem shows up; a fix goes in with an expedited review request.
- There is no way to withdraw a build already on a phone, which is why iOS sets the pace for the deprecation window.
- The app shows "a newer Cuebot Companion exists" from `app.companion.ios.version` in status, not from a store lookup.
- Mac Catalyst or a Mac build of the companion is out of scope.

### 5.12 When the desktop updates and the phone has not

| Moment | What the phone sees | What it does |
|---|---|---|
| Cuebot announces the update | `system.updating` event | "Cuebot is updating to v1.2.0..." |
| Cuebot is down (about 30 s) | Connection refused | Keeps retrying quietly with backoff for up to 3 minutes before showing an error |
| Cuebot is back, one or two levels newer | Higher `apiLevel`, more features | Works as before; new features it does not know simply do not appear; server-driven texts and generic cards may show some of them |
| Cuebot is back, more than 3 levels newer | | Soft banner (section 5.4) |
| A field the old app reads was replaced | Old field still sent (deprecation window) | Nothing visible |
| `minCompanionLevel` is now above the app | | "Update Cuebot Companion" banner, app keeps working as far as it can |
| The update was rolled back | Lower `apiLevel` than at last connect | Screens re-gated, one-time note |
| A profile switch happened in the update | Different profile in status | Same as a profile switch today |

Pairing, tokens and ports survive all of these (section 5.8).

## 6. Release pipeline changes

### 6.1 The signed release manifest

A new `manifest` job in `.github/workflows/release.yml`, after the packages are built and before `publish`:

1. Compute SHA-256 and size of every asset that `publish` uploads (zip, AppImage, flatpak, tarballs, dmg, plugins,
   companion APK).
2. Write `cuebot-release.json`:

```json
{
  "schema": 1,
  "version": "1.2.0",
  "tag": "v1.2.0",
  "channel": "stable",
  "published": "2026-12-01T18:00:00Z",
  "apiLevel": 4,
  "minCompanionLevel": 2,
  "minUpdateFrom": "1.0.0",
  "companion": { "android": { "version": "1.2.0", "versionCode": 412 } },
  "assets": [
    { "name": "Cuebot-win-x64.zip", "size": 98123456, "sha256": "..." }
  ]
}
```

3. Sign it with the Ed25519 key from the secret `UPDATE_SIGNING_KEY` (in the existing **signing** environment, not a
   repository secret) and write `cuebot-release.json.sig`. Also write a plain `SHA256SUMS` and its signature, so people
   who download by hand (Linux, macOS) can check too.
4. `publish` attaches all four files.

The key is generated once, offline; Kris keeps an offline copy. Cuebot embeds **two** public keys (current and next),
so the key can be rotated: a release signed with the next key is accepted by every Cuebot that shipped after the next
key was added. The signing step prints only the public key's fingerprint, never the key.

`minUpdateFrom` lets a release say "update to v1.1.x first" when a data migration needs a stepping stone; the updater
then offers that version instead.

### 6.2 Channel index

The release manifest of one version cannot change after the fact, but rollout percentages and withdrawals must. A small
signed index per channel does that:

```json
{ "schema": 1, "serial": 57, "issued": "2026-12-03T10:00:00Z", "channel": "stable",
  "latest": { "version": "1.2.0", "rollout": 50 },
  "withdrawn": [ { "version": "1.1.1", "reason": "Kick chat stopped after a reconnect", "critical": false } ] }
```

- `serial` only grows; Cuebot ignores an index with a lower serial than the last one it accepted (protection against
  an attacker serving an old index).
- Hosted at a fixed HTTPS address, not the GitHub API (no rate limit, no token). Candidates: GitHub Pages from
  `docs.yml`, or the existing Cloudflare Worker on techdaddydigital.com. See section 11.
- The release workflow writes the index for the pre-release channel on `-rc` tags and for stable at `rollout: 10` on
  release tags.

### 6.3 API level bump rules

- Bump `ApiLevel.Current` in the same pull request as any change the apps can see: new or changed endpoint, field,
  event or parameter. Internal changes do not bump.
- A level is not tied to a version: several releases can share a level, and one release can include several bumps
  (it still publishes only the final number).
- Raise `ApiLevel.MinCompanion` only by the rule in section 5.5.
- The apps raise their own built-in level when they start using something new from the server.
- The schema-diff check (section 5.6) catches forgotten bumps.

### 6.4 Pre-release first, then stable, staged

1. Tag `v1.2.0-rc.1`. Pre-release channel index updated; TestFlight build; Play internal or closed testing track.
2. At least three days on the pre-release channel, including one real stream by Kris (`docs/LIVE_TESTING.md`).
3. Tag `v1.2.0`. Stable index at `rollout: 10`. Play production staged rollout at 10 %. App Store phased release
   starts after review.
4. After two quiet days, a `rollout.yml` workflow (run by hand, input: version and percentage) re-signs the index at
   50 %, then 100 %.
5. Cuebot decides its bucket from a random install ID stored in `updates.json` (it never leaves the PC):
   `hash(installId + version) mod 100 < rollout`. **Check now** ignores the rollout, so anyone who wants the update
   gets it.

### 6.5 Withdrawing a bad release

A `withdraw.yml` workflow (by hand; inputs: version, reason, critical):
1. Adds the version to `withdrawn` in the channel index, sets `latest` back to the previous good version, bumps
   `serial`, signs.
2. Marks the GitHub release as a pre-release and puts the reason at the top of its notes, so new manual downloads avoid
   it.
3. Prints the store checklist: halt the Play staged rollout; pause the App Store phased release; ship the fix with an
   expedited review.
4. AUR: push the previous version's PKGBUILD with a higher `pkgrel` (or the fix).

Installed Cuebots react as in section 4.8. The withdrawal is reversed by running the workflow again with
`undo: true`.

## 7. Phases and effort

Estimates are working days for one builder session, including tests, docs and the changelog.

| Phase | Content | Effort | Depends on |
|---|---|---|---|
| 0 | Update signing key, `manifest` job, `SHA256SUMS`, signed channel index for both channels (no client use yet) | 2 | v1.0.0 out |
| 1 | `ApiLevel.cs`, status fields, `X-Cuebot-Client` header, last-seen per paired device, capability gating and banners on Android and iOS | 4 (server 1, Android 1.5, iOS 1.5) | none |
| 2 | Contract fixtures (server responses, app requests), replay tests, schema diff, `API_DEPRECATIONS.md` | 3 | 1 |
| 3 | In-app download and verification, the three-button prompt, live deferral, `system.updating` event; installation still by hand (opens the verified folder) | 3 | 0 |
| 4 | `cuebot-update` helper, folder swap (Windows zip, Linux tarball), AppImage swap, pinned backups, health check and automatic rollback | 7 | 3 |
| 5 | "Go back to vX", withdrawn handling, staged rollout bucket, `rollout.yml`, `withdraw.yml` | 3 | 4 |
| 6 | `companion/config`: texts, card layout, generic cards, limits, quirks; both apps | 5 | 1 |
| 7 | Android `play` and `github` flavors, Play pipeline, APK served by the paired PC | 4, plus Play's testing period | Play account, signing decision |
| 8 | macOS self-update | 3 | Developer ID and notarization |

Phases 1 and 2 come first because they protect users from the store lag as soon as the first app update ships. Phase 0
can run in parallel. The first release that installs updates by itself is phase 4 (about 4 weeks of work from the
start, phases 0 to 4).

## 8. Risks

| Risk | Mitigation |
|---|---|
| Signing key leaks | Stored only in the **signing** environment; two embedded public keys allow rotation; a leak plus a compromised GitHub account is needed to ship to users, and Windows Authenticode is checked as well. |
| Swap fails halfway (power cut, antivirus) | Rename-only operations, a journal, retries, and the helper finishing or undoing on the next run. |
| New version migrates data and the old one cannot read it | Pinned backup restored on rollback by default; stores keep pre-migration copies or refuse unknown newer formats. |
| Rollback loses data written since the update | The automatic rollback only happens after failed starts (nothing written). Manual go-back asks and keeps a `pre-restore` backup. |
| "Live" detection misses a stream | OBS and Meld streaming state as extra signals; unknown state means no automatic install. |
| The helper has a bug and breaks updating | The helper is tiny, tested on all platforms in CI; every full package ships a new helper; the manual download path always stays. |
| API level discipline erodes | Schema diff and replay tests fail the build. |
| Phone apps fall far behind | Deprecation window tied to real store rollout; banners never block; server-driven pieces fill gaps. |
| App Review objects to generic cards | Keep them to configuration of built-in components; describe them in the review notes. |
| Play and GitHub APKs signed differently | Upload the existing key to Play App Signing before the first upload. |
| GitHub API rate limits | Channel index on a static host; GitHub API only as fallback. |
| Program folder not writable | Detect before downloading; fall back to the manual path. |

## 9. Test plan

**Unit tests (desktop)**
- Manifest and index verification: good signature, wrong key, changed byte, replayed manifest for another version,
  lower `serial`, second (rotation) key.
- SHA-256 mismatch deletes the download; size cap.
- Prompt state machine: update now while live (refused), after my stream (waits for 10 quiet minutes, countdown,
  cancel), skip, a newer version after a skip.
- `BackupService.Prune` never deletes `before-update-` backups; pinned clean-up keeps three.
- Health counter: healthy resets it; two unhealthy starts trigger rollback mode.
- Rollout bucket: stable for a given install and version; **Check now** ignores it.
- Zip and tarball unpacking rejects entries outside the target.

**Integration tests (CI, Windows and Linux runners)**
- Build two fake versions of a tiny app with the real helper: update, healthy; update to a version that crashes at
  start (rollback, data restored, notice shown); update to a version that crashes before `Main`; kill the helper between
  the two renames (recovery on next run); locked folder (retries, then clean failure).
- AppImage replacement in a container with FUSE.

**Contract tests**
- Section 5.6 on every push to `main`: app parsers against the last three releases' fixtures, server against the last
  three apps' requests, schema diff.

**Manual matrix (before the first self-updating release)**
- Windows zip in Downloads, on a second drive, in Program Files (manual fallback); Linux tarball, AppImage, Flatpak
  (instructions), AUR (instructions); macOS (download link with checksum).
- A real stream with **After my stream**: nothing happens while live, toast 10 minutes after ending.
- Phones: the current store app against the new Cuebot, the app from two releases ago against the new Cuebot, the new
  app against v1.0.0 Cuebot; during an update (updating banner and reconnect) and after a rollback.
- Withdraw drill on the pre-release channel: withdraw an rc, check the banner and that it is no longer offered, undo.
- Key rotation drill once before 1.x ends: sign an rc with the next key.

## 10. Order of work against v1.0.0

Nothing in this plan changes v1.0.0. The first app releases after v1.0.0 should already send `X-Cuebot-Client` and
read `apiLevel` (phase 1), because every app version shipped without them is one Cuebot will have to guess about for
the length of the window.

## 11. Open decisions for Kris

1. **Custom or Velopack.** The plan recommends custom (section 4.9).
2. **Play App Signing key:** upload the existing release key (recommended) or let Google create one and accept that
   GitHub and Play installs cannot update each other. Needed before the first Play upload.
3. **Where the channel index lives:** GitHub Pages or the Cloudflare Worker on techdaddydigital.com.
4. **Support window size:** 3 levels and 90 days (proposed), or longer.
5. **"Update automatically after my stream"** as an opt-in setting: yes (proposed) or always ask.
6. **Write access fallback:** manual instructions only (proposed), or ask for elevation on Windows.
7. **How many pinned backups** to keep: three (proposed).
8. **Flatpak:** stay with bundle downloads, or set up a Flatpak repository or Flathub so `flatpak update` works.
9. **Paired-PC APK (phase 7):** worth building, or Play plus GitHub is enough.
10. **Generic server-driven cards (phase 6):** full set, or texts and layout only at first.
11. **Telemetry:** none is planned. Rollout health is judged from problem reports and GitHub issues. An opt-in
    "update succeeded" ping would make staged rollouts more meaningful; decide whether that is wanted at all.
