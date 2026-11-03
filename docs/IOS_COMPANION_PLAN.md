# Cuebot iOS companion: v1 plan (Deck + Dashboard)

- **Status (2026-11-02):** planned and queued. Work starts after the macOS port (branch `macos-support`) is merged into main.
- **Owner:** Kris.
- **Builders:**
  - The Mac session ("Cuebot handover preparation", on Kris's MacBook) builds the iOS app.
  - The Linux session (DailyDriver) builds the server-side pieces in Cuebot.
- **Fleet memory pointer:** `cuebot.linux-dev`, section "2026-11-02: macOS port and iOS companion".

## 1. Goals and non-goals

**v1 goals**
- A native Deck on iPhone and iPad that replaces a hardware Stream Deck for many streamers.
- A live Dashboard: connections, totals, subathon timers, goals, top spenders, the activity feed and quick actions.
- Pairing that works in under a minute by scanning a QR code, with no account and no cloud.

**Not in v1**
- Remote access from outside the home network.
- Push notifications when the app is closed (they need a relay server).
- Chat and moderation.
- Editing decks or actions on the phone. Editing stays in the desktop app.
- An Android version.

## 2. User experience

### Tabs

**Deck**
- **Choosing a deck:** the app asks which deck to show on first use, then remembers it. It shows the deck's grid (`rows × columns`) with its background color and gap.
- **Buttons:** each button shows its background color or image, an icon, a title in its title color, and a status value in the corner. Pressing shows a pressed animation and a light haptic. A failed press shows a red flash and a heavy haptic.
- **Pages:** the user swipes left or right, or uses the arrows in the bar. A page change shows on every open copy of the deck, which is how the desktop deck already behaves.
- **Notifications:** a `notify` message from the deck (sent by the "Deck notify" sub-action) shows as a banner.
- **Stream mode:** a toggle in the toolbar. It hides the tab bar and status bar, keeps the screen awake, and locks the rotation. A long press in a corner exits stream mode.
- **iPad:** works in landscape and in Split View and Slide Over, so the deck can sit beside OBS's iPad app or Discord.

**Dashboard**
- **Header:** each connection as a colored dot (Connected is green, Connecting is amber, Error is red, Disconnected is grey) with its name. Tapping one shows its status message.
- **Subathon card:** one card per timer.
  - Shows the name, the big countdown, the state, the time added and the multiplier.
  - Controls: start/pause/resume, add time (+1 min, +5 min, and a custom amount) and set the multiplier (0.5×, 1×, 2×, custom).
  - The countdown runs on the phone from `endsAt`. It does not need an update every second.
- **Goals card:** one card per goal. It shows a progress bar to the next step, each step ticked off once reached, and add-points and set-points controls.
- **Top spenders card:** this stream's top 5 in the chosen currency, with platform icons. A picker switches between day, week, month and year, and between all platforms and one platform.
- **Stream totals card:** followers, subs, bits, tips and raids this stream.
- **Activity feed:** the last 50 events, newest first, with platform icons. Test events have a "TEST" badge.
- **Quick actions:** a grid of pinned actions. Tapping runs the action. Actions that are risky or can't be undone ask for confirmation first, and the user can switch that off per action.

**Settings**
- **Paired Cuebots:** a list with names, where the user can add, rename, remove or switch the active one.
- **Pairing:** "Scan QR code" opens the camera. "Enter manually" asks for the host, the ports and the token.
- **Preferences:** the default deck, the pinned actions, haptics on or off, and the stream mode defaults.
- **About:** the app version, the paired Cuebot's version and the API version, and a troubleshooting link to the docs page.

### Errors and states
- **Can't reach Cuebot:** a full-screen card shows the address it tried and a checklist:
  - Is Cuebot running?
  - Is "Allow LAN" on in Cuebot?
  - Are the phone and the PC on the same Wi-Fi?
  - Did iOS block Local Network access?

  It has a "Retry" button and an "Open Settings" button.
- **Token rejected (401):** asks the user to pair again.
- **Cuebot too old** (the dashboard endpoint returns 404): the Deck tab still works, and the Dashboard tab says that Cuebot needs updating.
- **Reconnecting:** the live streams reconnect with backoff of 1, 2, 4 and 8 seconds, then every 15 seconds. A thin "Reconnecting…" bar shows while that happens. The Deck re-fetches its state after each reconnect.

## 3. Connection and pairing

### Ports
These are the defaults; a streamer can change them in Cuebot.

| Server | Default port | Auth today | Used for |
|---|---|---|---|
| REST API | 7475 | Bearer token | status, actions, events, dashboard, controls |
| HTTP server (decks) | 7474 | none, only an Origin check | deck state, deck events, press, page |

Both servers listen on 127.0.0.1 until the streamer turns on **Allow LAN** in Cuebot. The phone can't connect until then.

### Pairing QR payload (new, server side)
The QR code is shown in Cuebot under Settings → API → "Pair a phone". It is a URL, so the iPhone's Camera app can open the companion app directly:

```
cuebot://pair?v=1&name=<instance name>&host=<LAN IPv4>&api=7475&http=7474&token=<api token>&deckToken=<deck token>&id=<instanceId>
```

- `host` is the first private IPv4 address of the active network interface. The QR screen lists the alternatives when the PC has more than one.
- `id` is `status.app.instanceId`. The app uses it to recognize the same Cuebot again after its IP address changes.
- The app stores the token and the deck token in the Keychain with `kSecAttrAccessibleAfterFirstUnlock`, and the rest in UserDefaults.

### Discovery (new, server side)
When Allow LAN is on, Cuebot advertises a Bonjour service `_cuebot._tcp` on the API port. The TXT record holds `v=1`, `id=<instanceId>`, `name=<instance name>` and `http=<port>`, and never the token. The app uses Bonjour to find a paired Cuebot again after its IP address changes, and to suggest a host when the user enters the details manually.

### iOS permissions (Info.plist)
- `NSLocalNetworkUsageDescription`: "Cuebot Companion connects to Cuebot on your computer over your local network."
- `NSBonjourServices`: `["_cuebot._tcp"]`
- `NSCameraUsageDescription`: "Scan the pairing QR code shown in Cuebot."
- `NSAppTransportSecurity` → `NSAllowsLocalNetworking = true`. Cuebot serves plain HTTP on the LAN, and this setting allows that without turning off App Transport Security everywhere.
- A URL scheme `cuebot` for the pairing link.

## 4. Data contracts

### Existing endpoints, as they work today

**`GET /api/v1/status`** with `Authorization: Bearer <token>`:
```json
{ "app": { "name": "Cuebot", "version": "…", "runtime": "…", "os": "…", "instanceId": "…", "apiVersion": "v1" },
  "connections": [ { "name": "Twitch", "state": "Connected", "status": "…" } ],
  "counts": { "actions": 0, "enabledActions": 0, "timers": 0, "queues": 0, "commands": 0, "globals": 0, "triggerTypes": 0, "subActionTypes": 0 } }
```
`state` is one of `Disconnected`, `Connecting`, `Connected` or `Error`.

**`GET /api/v1/actions`** lists actions, and **`POST /api/v1/actions/{id}/run`** runs one. The app should accept extra fields, because the exact list shape may change.

**`GET /api/v1/events?types=a,b`** is a server-sent events stream. It opens with `: connected`. Each message looks like this:
```
id: <n>
event: <event type, e.g. twitch.cheer>
data: <JSON args>
```
A limit on the number of open streams answers HTTP 429. The app keeps a single stream open per paired Cuebot.

**`GET /deck/{id}/state`** returns the deck view:
```json
{ "id": "…", "name": "…", "rows": 3, "columns": 5, "background": "#16161c", "gap": 8,
  "page": 0, "pageName": "Main", "pageCount": 2,
  "items": [ { "id": "…", "name": "…", "row": 0, "column": 0, "state": 0,
               "title": "…", "titleColor": "#fff", "background": "#333", "backgroundImage": "",
               "icon": "mdi:microphone", "iconColor": "#fff", "iconUrl": "", "value": "" } ] }
```
- `icon` is an Iconify name. The app fetches `https://api.iconify.design/<prefix>/<name>.svg?color=<iconColor>`, caches it on disk and draws it as a template image. When the phone is offline it shows the title only.
- `iconUrl` and `backgroundImage` may be relative to the HTTP server. The app resolves them against `http://host:httpPort`.

**`GET /deck/{id}/events`** is a server-sent events stream. It sends `event: state` with the full view (first on connect, then after each change) and `event: notify` with `{"title","description"}`, plus `: keep-alive` comments.

**`POST /deck/{id}/press`** takes `{"item":"<item id>"}` and answers 204, or 404 for an unknown or disabled item. **`POST /deck/{id}/page`** takes `{"delta":1}` or `{"page":2}` and answers 204.

**Gap:** `GET /deck` returns an HTML page, not JSON. The new `GET /api/v1/decks` in section 5 fixes that.

### Shapes the dashboard reuses
These come from the overlay session data and appear inside the new dashboard snapshot.

**Subathon** (`cuebot-subathon`):
```json
{ "timers": [ { "id": "…", "name": "…", "state": "running|paused|stopped", "remaining": 3540.5,
                "endsAt": 1762130000000, "elapsed": 60, "added": 3590.0, "multiplier": 1.0 } ],
  "goals":  [ { "id": "…", "name": "…", "points": 12, "multiplier": 1.0,
                "steps": [ { "name": "Emote", "points": 10, "reached": true } ],
                "next": { "name": "Second goal", "points": 20 }, "previous": { } } ] }
```
`endsAt` is Unix time in milliseconds when the timer is running, and null otherwise. The app counts down as `endsAt - now`. When the timer is paused it shows `remaining`.

**Top spenders** (`cuebot-top-spenders`):
```json
{ "currency": "USD",
  "periods": { "stream": { "all": { "total": 0, "formattedTotal": "$0.00", "spenders": [
      { "rank": 1, "name": "…", "platform": "Twitch", "amount": 22.34, "formatted": "$22.34", "count": 2 } ] },
      "twitch": { } }, "day": { }, "week": { }, "month": { }, "year": { } } }
```
- The period keys are the lowercase values of the `SpendingPeriod` enum. The app reads them from the snapshot and does not hard-code them.
- Each platform key is the lowercase platform name.

## 5. Server-side work (Linux session, on branch `companion-api`)

Each item comes with tests in Cuebot.Server.Tests, its entry in `OpenApiDocument`, and a page in the API reference docs.

1. **`GET /api/v1/dashboard`** returns one snapshot:
   ```json
   { "instanceId": "…", "version": "…", "connections": [ … as in status … ],
     "totals": { … stream totals session snapshot … },
     "subathon": { … cuebot-subathon … }, "topSpenders": { … cuebot-top-spenders … },
     "feed": [ { "platform": "Twitch", "kind": "Tip", "user": "…", "title": "…", "message": "…", "amount": "500 bits", "time": "…", "isTest": false } ],
     "revision": 42 }
   ```
   `revision` goes up whenever any part changes. A new `dashboard.changed` event on `/api/v1/events` carries `{"revision": n}`, sent at most 4 times per second. The app re-fetches the snapshot when it sees that event.
2. **`GET /api/v1/decks`** lists the enabled decks as `[{id, name, rows, columns, pageCount}]`. The existing `ApiOperations.Decks()` already builds most of this.
3. **Subathon and goal controls.** These are thin wrappers over the existing sub-action logic, so behavior matches the UI and actions:
   - `POST /api/v1/subathon/{timer}/start|pause|resume|toggle|reset` (the timer can be given by id or name)
   - `POST /api/v1/subathon/{timer}/time` with `{"add": seconds}` or `{"set": seconds}`
   - `POST /api/v1/subathon/{timer}/multiplier` with `{"value": 2.0}`
   - `POST /api/v1/goals/{goal}/points` with `{"add": n}` or `{"set": n}`
   - `POST /api/v1/goals/{goal}/multiplier` with `{"value": 2.0}`
   - `POST /api/v1/goals/{goal}/reset`
4. **Deck token.**
   - A new optional `DeckToken` setting. When it is set, `/deck/{id}/state|events|press|page` require either the header `X-Cuebot-Deck-Token` or the query parameter `?token=` (server-sent events can't send headers in browsers).
   - The deck page and the deck links Cuebot shows include the token automatically.
   - When the streamer turns on Allow LAN, Cuebot creates a deck token and turns this on by default.
   - Existing loopback-only setups keep working as they are.
5. **Pairing.** Settings → API gets a "Pair a phone" section with the QR code from section 3 (rendered in-app by a small QR library or our own encoder), a host picker, and a "Rotate token" button.
6. **Bonjour advertising.**
   - On Linux it uses Avahi through the existing DBus connection, on macOS `dns-sd` (or a managed mDNS responder), and on Windows the built-in DNS-SD APIs.
   - It only runs when Allow LAN is on, and can be turned off.
   - This item can come after items 1–5 if a platform turns out awkward.
7. **Docs:** a new guide page, "Companion app", covering pairing, Allow LAN, the firewall prompts on Windows and macOS, and troubleshooting.

## 6. iOS app architecture (Mac session, on branch `ios-companion`)

- **Project:** `clients/ios/CuebotCompanion`. An Xcode project made with SwiftPM-style folders, with targets for the app, `CuebotKit` (a framework: networking and models) and the tests.
- **Stack:** Swift 6 with strict concurrency, SwiftUI and the Observation framework. iOS/iPadOS 18 or later. No third-party packages.
- **CuebotKit:**
  - `CuebotEndpoint`: host, ports, token, deck token and instance ID.
  - `CuebotClient` (an actor): typed REST calls (`status()`, `dashboard()`, `decks()`, `runAction(id)`, `subathon(...)`, `goal(...)`, `press(deck:item:)`, `page(deck:delta:)`).
  - `SSEStream`: an `AsyncThrowingStream<ServerEvent>` built on `URLSession.bytes(for:)`. It parses `id`, `event`, `data` and comments, honors `Last-Event-ID` and reconnects with backoff.
  - Models: `Codable` structs that tolerate unknown and missing fields, using `decodeIfPresent` everywhere.
- **Stores:** `@Observable` and `@MainActor`.
  - `ConnectionStore` handles the active endpoint, the reachability state and re-checking after a 401.
  - `DeckStore` holds the current view, sends presses and pages, and passes on notifications.
  - `DashboardStore` holds the snapshot and its revision, and re-fetches on `dashboard.changed`.
- **Icons:** `IconCache` fetches Iconify SVGs and turns them into images with `UIImage` symbol rendering through a lightweight SVG path, or simply a WKWebView snapshot. Measure which is simpler; if SVG proves painful, ask the server to provide PNGs (`/deck/icon?name=&color=&size=`).
- **Background behavior:** the streams close when the app goes to the background and reopen when it returns to the foreground. The app does no background networking in v1.
- **Accessibility:** buttons get VoiceOver labels from the title or the name, the app supports Dynamic Type outside the deck grid, and the deck grid keeps its fixed layout.

## 7. Milestones and acceptance

| # | Milestone | Done when |
|---|---|---|
| S1 | Server: dashboard, decks list, controls | Endpoints and tests are merged to main, and the OpenAPI document and docs are updated |
| S2 | Server: deck token and pairing QR | QR pairing works against a test Cuebot; the browser deck page still works |
| S3 | Server: Bonjour | `dns-sd -B _cuebot._tcp` on the Mac finds the Linux Cuebot |
| I1 | iOS: CuebotKit and tests | Unit tests pass against a stub server, including server-sent-event parsing and reconnect |
| I2 | iOS: pairing and the Deck tab | In the iPad simulator, pairing works and pressing runs an action in Cuebot on the Mac within 150 ms on the LAN; state changes show live |
| I3 | iOS: Dashboard tab | Emulated events (cheer, gift bomb, Super Chat) update the feed, top spenders, subathon and goals live; the subathon controls work |
| I4 | Polish | Errors and states, stream mode, accessibility and icons are done, checked on iPhone and iPad (simulator and Kris's real devices) |
| R1 | TestFlight | Internal TestFlight build installed on Kris's devices (needs the runbook in section 9) |

The order is S1 → I1 → I2 (it needs S2 for the token, but can start with a loopback, tokenless deck) → I3 → S3 → I4 → R1.

## 8. Testing

- **Server:** unit tests for each endpoint, a token-required test for the deck routes, a test that a `dashboard.changed` event follows a feed item, and a test that the QR payload is built correctly.
- **iOS:**
  - CuebotKit tests with a local stub server (`URLProtocol` mocks plus a tiny NWListener server-sent-event server).
  - Snapshot-style checks of the deck grid at iPhone and iPad sizes.
  - End to end in the simulator against Cuebot running on the Mac (from the macOS port), using `emulate_event` with target `everything` to drive the dashboard.
- **Real devices:** Kris's iPhone and iPad on the home Wi-Fi against the Linux DailyDriver Cuebot. This checks the Local Network permission prompt and Bonjour.

## 8a. Continuous integration (GitHub Actions)

The repository is public, so GitHub's macOS runners cost nothing. Each macOS job takes 10 to 20 minutes.

**macOS desktop build** (part of the macOS port, on `macos-support`):
- Add `macos-latest` (Apple silicon) to the `test` matrix in `.github/workflows/build.yml`. Install the test dependencies with `brew install sdl3`; Python 3 is already on the runner. Run the same `dotnet build` and `dotnet test -m:2` steps as Windows and Linux.
- Add a `macos-package` job that runs `packaging/macos/build-app.sh` for `osx-arm64` and `osx-x64`. It uploads the `.dmg` files as artifacts and uses the same built-in OAuth client ID secrets as `windows-package`.
- `release.yml` attaches `Cuebot-macos-arm64.dmg` and `Cuebot-macos-x64.dmg` to the release.
- Signing: ad-hoc signing for now. Developer ID signing and notarization come later, with secrets for a Developer ID certificate (.p12 + password) and an App Store Connect API key, documented in `docs/CODE_SIGNING.md`. Until then, macOS Gatekeeper shows "unidentified developer" on first open, and the docs explain right-click → Open.

**iOS app** (a new workflow, `.github/workflows/ios.yml`):
- It runs on pushes and pull requests that touch `clients/ios/**`, and on demand.
- `runs-on: macos-latest`. It picks the newest Xcode with `sudo xcode-select -s /Applications/Xcode_<n>.app`, or uses `maxim-lobanov/setup-xcode`.
- `xcodebuild test -project clients/ios/CuebotCompanion/CuebotCompanion.xcodeproj -scheme CuebotCompanion -destination 'platform=iOS Simulator,name=iPhone <current>' -destination 'platform=iOS Simulator,name=iPad Pro <current>' CODE_SIGNING_ALLOWED=NO`
  - It runs the unit tests and the UI smoke tests on an iPhone simulator and an iPad simulator.
  - It needs no Apple account or signing, so pull requests and forks work too.
  - It uploads the `.xcresult` bundle as an artifact when a test fails.
- **Later: TestFlight uploads from CI** (milestone R1+), on tags or by hand only:
  - It uses an App Store Connect API key stored as secrets: `ASC_KEY_ID`, `ASC_ISSUER_ID`, and `ASC_KEY_P8` (base64).
  - It archives with `xcodebuild archive -allowProvisioningUpdates -authenticationKeyPath ...` and uploads with `xcrun altool --upload-app` or `xcodebuild -exportArchive` with `destination=upload`.
  - Kris creates the key in App Store Connect and adds the secrets in GitHub. The runbook in section 9 will include these steps click by click, and agents never handle the key.

## 9. Runbook needed from Kris: Apple Developer and TestFlight

Kris prefers click-by-click instructions for manual steps. The Mac session writes the final runbook as `docs/owner/ios-testflight.md` in the repo when it reaches R1. It will cover:
1. Joining the Apple Developer Program (or checking an existing membership) under the Tech Daddy Digital entity or a personal account. That's Kris's call; organization enrollment needs a D-U-N-S number.
2. Signing in to Xcode → Settings → Accounts with that Apple ID. Only Kris does this; agents never enter Apple credentials.
3. Creating the bundle ID (`digital.techdaddy.cuebot.companion` proposed) and the App Store Connect app record.
4. Archiving and uploading from Xcode (the agent can do this once Kris has signed in), then adding Kris's devices to internal TestFlight testers.

## 10. Open questions for Kris (before or during the build)

1. **App name:** "Cuebot Companion" or "Cuebot Remote"? The bundle ID depends on this choice.
2. **Developer account:** personal account or the Tech Daddy Digital organization? This affects the time to TestFlight, since D-U-N-S verification can take days.
3. **Minimum iOS:** 18 (proposed; covers iPhone XS and later) or 17 for older iPads?
4. **Quick actions:** should running an action from the phone need a confirmation by default, or only for actions marked as risky?

## 11. Later (v2+)

- **Mod console:** combined chat with reply, timeouts and bans, polls and predictions, giveaways, and channel point redemption review.
- **Live Activity and Dynamic Island:** the subathon countdown and the next goal. Updates while the phone is locked need either push (a relay) or running on the same network with the app in the foreground.
- **Home Screen and lock screen widgets:** stream totals and top spender.
- **Siri Shortcuts / App Intents:** run an action, start or pause the subathon, add points.
- **iPhone as a stream device:** a wireless mic or camera into Cuebot or OBS.
- **Remote access outside the LAN:** through Tailscale (works today with Allow LAN, just document it) or an optional relay.
- **Apple Watch:** quick actions and the subathon time.
