# Cuebot mod portal: plan

- **Status (2026-11-06):** designed with Kris in chat. Phases 0 to 6 are built (see section 11). The relay Worker
  `cuebot-portal` is deployed and serves `portal.`, `relay.` and `hooks.techdaddydigital.com` (attached 2026-11-05).
  Mods join by invite, sign in, use the web portal, upload media and edit overlays. The Android app's mod mode and
  approvals from the phone are done (phase 7); the iOS app's are being built on the Mac.
- **Owner:** Kris.
- **Builders:** the Linux session (DailyDriver) builds Cuebot, the relay and the web portal. The Mac session builds and
  tests the iOS mod mode.

## 1. Goals and non-goals

**Goals**
- Moderators manage parts of a streamer's Cuebot from anywhere, in a browser or the phone app: commands, actions,
  sound alerts, audio levels, scenes, queues, TTS, overlays and widgets, uploads of sounds, images, GIFs and video.
- The streamer decides who gets what: roles set when the invite is made, changeable or revocable at any time.
- No setup for the streamer beyond creating an invite: no Cloudflare account, no port forwarding.
- Nothing stored about anyone on the shared relay, and the relay cannot read what passes through it.
- Uploads cannot smuggle anything but clean media into the streamer's PC.

**Not in v1**
- Code of any kind through the portal (C#, Python, run program, files, shell). Not for any role, ever.
- Offline use: when Cuebot is off, the portal does nothing and the relay queues nothing.
- A direct home-network mode for mods (everything goes through the relay).
- Discord role checks and platform mod checks for YouTube, Kick and Rumble (later, see section 12).

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | How far mods change actions | Full editing (option C), limited by step risk levels per role (section 5). |
| 2 | Changes go live or wait | Per role, overridable per member: **Live**, **Live and tell me**, **Needs approval**. |
| 3 | Upload cleaning | Bundle a minimal LGPL ffmpeg in every build; re-encode every upload (section 8). |
| 4 | Sign-in | Google, then Twitch, then Discord; redirect sign-in with PKCE through one fixed return page; an invite binds one account by its permanent provider user ID. |
| 5 | Where the portal lives | One shared relay on Kris's Cloudflare (techdaddydigital.com), end-to-end encrypted, nothing stored. Also carries webhooks, passed through and never stored. Self-hosted relay stays as an advanced option. |
| 6 | Platform mod status | The invite's role is authoritative on every platform (Twitch, YouTube, Kick, Rumble). An optional Twitch mod check per member, off by default. |

## 3. Architecture

```
Mod browser / phone app ──WSS──▶ relay.techdaddydigital.com ◀──WSS── streamer's Cuebot (outbound)
        └──────── end-to-end encrypted; the relay forwards opaque frames ────────┘

Ko-fi, Patreon, … ──POST──▶ hooks.techdaddydigital.com/<relay id>/<route secret>/<service> ──▶ (same Cuebot link)
Mod browser ──GET──▶ portal.techdaddydigital.com   (loader page and the sign-in return page only)
```

### Hosts (one Worker, Workers Paid plan, custom domains)
- `portal.techdaddydigital.com`: the static loader (crypto, transport, UI bootstrap) and `/auth/return`.
- `relay.techdaddydigital.com`: `/connect` for Cuebot, `/c/<relay id>` for mod devices.
- `hooks.techdaddydigital.com`: webhook intake.
- The domain's wildcard record (to 152.117.109.81) stays; Worker custom domains take precedence for these names.

### Relay state
- One Durable Object per relay ID, named by the ID. No SQLite, no KV, no R2, no `storage.put`.
- In memory only (and in hibernation attachments): the Cuebot socket, mod sockets, the route-secret fingerprint,
  rate counters.
- Workers observability off, no Logpush, no tail consumers, no analytics engine. Code never reads
  `CF-Connecting-IP` or request headers beyond what routing needs.

### Cuebot identity
- On first use Cuebot generates an ECDSA P-256 identity key, kept in the secret store with every other key. Backups
  hold no keys by design (`BackupService`), so the key is not in them; a separate "save a copy of the portal key"
  export is still to come.
- **Relay ID** = base32 of the first 20 bytes of SHA-256(SPKI public key). Random, not tied to any name or email.
- `/connect`: the relay sends a nonce; Cuebot answers with its public key and a signature over the nonce and host.
  The relay checks the key hashes to the ID and the signature. No registry.
- Losing the key means a new relay ID: every webhook address and mod device stops working. Cuebot shows a clear
  "your relay address changed" page listing what to update.

### End-to-end encryption (WebCrypto and .NET built-ins, no extra crypto library)
- The invite link and every pinned device carry the **identity key fingerprint** in the URL fragment
  (`https://portal.techdaddydigital.com/#i=<invite>&k=<fingerprint>`), never sent to a server.
- Handshake per connection: the device sends an ephemeral ECDH P-256 key; Cuebot answers with its ephemeral key and an
  ECDSA signature over the whole transcript with its identity key; the device checks it against the pinned
  fingerprint. HKDF-SHA256 derives one AES-256-GCM key per direction; nonces are frame counters; replays and
  reordering close the connection.
- The relay sees only the relay ID, frame sizes and timing.

### The loader and the real portal
- The loader on `portal.` is small, versioned and open source. It does the handshake, then asks Cuebot for the portal
  UI bundle over the encrypted link, so the UI always matches the streamer's Cuebot version.
- The loader is the trust anchor for web users (whoever controls the Worker could change it). The phone apps ship
  their own code and do not depend on it.
- Strict CSP on the loader: no inline script, no third-party origins, `connect-src` only the relay. The UI bundle
  arrives over the link (`ui`) and runs from `blob:` URLs the loader makes, so `script-src` and `style-src` are
  `'self' blob:`. The UI source is `portal/ui` (Preact); `npm run build` writes it to `src/Cuebot.Portal/Web`, which
  Cuebot embeds, and CI fails when the committed build is stale.

## 4. Invites, sign-in and sessions

### Invites
- The streamer creates an invite in Cuebot: **role**, optional label, expiry (24 hours default), optional lock
  (must be Twitch user X, or Google email Y; checked inside Cuebot after sign-in).
- An invite works once. The role is applied automatically when the mod finishes sign-in.
- Shown as a link and a QR code. Unused invites can be withdrawn.

### Sign-in (PKCE redirect through one fixed return page)
- Providers in build order: **Google**, **Twitch**, **Discord**. Scopes: identity only (`openid email profile` for
  Google, none for Twitch, `identify` for Discord). The email is used only for invite locks, and only when Google says
  it is verified.
- The loader asks Cuebot for the providers (`providers`), makes the PKCE verifier and a random `state`, keeps both in
  sessionStorage and sends the mod to the provider. `redirect_uri` is `https://portal.techdaddydigital.com/auth/return`
  for every provider. Back on the return page the loader checks `state`, removes the code from the address bar,
  reconnects and sends code, verifier, invite and its device key to Cuebot (`signin`, over the encrypted link).
- Cuebot (`PortalIdentityExchange`) exchanges the code, reads the provider's permanent user ID, then revokes the
  token: the portal keeps no provider token. Twitch: the token must belong to Cuebot's app (`/oauth2/validate`).
- PKCE: Google and Discord. Twitch has no PKCE; the code still needs the client secret, and the return page is on our
  own origin with `Referrer-Policy: no-referrer`.
- An already-signed-in member who opens another invite is signed in without using it up. Five sign-in attempts per
  connection.
- Cuebot stores, per member: provider, provider user ID, a display name for the streamer's own list, role,
  overrides, devices. On the streamer's PC only.
- One account per member. Lost account: the streamer removes the member and sends a fresh invite.

### Devices and sessions
- Each device makes a non-extractable ECDSA key (WebCrypto, kept in IndexedDB; Keychain or Keystore in the apps) at
  sign-in; Cuebot records its public key. On every connection the device signs `cuebot-portal-device-v1` || the
  handshake binding (SHA-256 of the transcript), so a proof cannot be replayed on another connection (`resume`). No
  bearer token to steal from storage. Each invite gets a fresh key, so two memberships never share one.
- Re-sign-in with the provider every 30 days (`PortalDirectory.Resume`). Not built yet: ending a session after 12
  hours without activity.
- The streamer sees each member's devices, and can sign out one device, one member, or everyone.

### Console setup (done 2026-11-05; details in `docs/BUILT_IN_APPS.md`)
- **Google:** project `cuebot-mod-portal` ("Cuebot Mod Portal"), separate from the YouTube project so its pending
  verification cannot affect sign-in. Web client "Cuebot mod portal (web)", basic scopes only, published.
- **Twitch:** a new Confidential app "Cuebot Mod Portal". The existing Cuebot app is Public, and Twitch allows public
  apps only the device-code flow.
- **Discord:** a new application "Cuebot Mod Portal" with Public Client on: PKCE without a secret (confirmed live).
- Values reach builds as `CUEBOT_PORTAL_*` repository secrets, like the other built-in apps.

## 5. Roles and permissions

### Step risk levels
Every step type and trigger type declares a level. A test fails when a registered type has none.

| Level | Examples |
|---|---|
| 1 Basic | Send chat message, play sound, delay, TTS say, overlay alert, comment, If/Switch/While, Run at the same time |
| 2 Stream control | Scenes and sources, audio levels and mutes, raid, ads, shoutouts, polls and predictions, queues, timeouts |
| 3 Show-critical | Start or end stream or recording, ban, title and category, turn actions or triggers on and off, restart sources |
| 4 Trusted tech | Global variables, web requests, Stream Deck, MIDI, smart devices, integration-specific steps, run another action |
| Never | C# and Python code, run program, file read and write, shell, settings, credentials, tokens, members and roles, Export to code |

### Mod-set values are marked (built in phase 1)
- A step a mod added or changed carries `editedBy` (the member). Once one has run, the run carries `__modInfluence`
  (also into actions it runs), and every Never step in that run refuses to run.
- Global and user variables a mod's step set are marked (`mod-marks.json`); a Never step whose settings, code
  included, name a marked variable refuses. The streamer's own write clears the mark.
- Mods cannot change an action that has a Never step, nor a command that starts one (that would change who reaches
  it). Running an action needs the role's level for its riskiest step (Never steps: no one), decided by Kris
  2026-11-06; it runs as the streamer set it up, with no input from the mod.

### Default roles (the streamer can rename, change and add roles)

| | Standard Mod | Lead/Head Mod | Tech Lead |
|---|---|---|---|
| Step levels | 1 | 1–3 | 1–4 |
| Commands | Create, edit | Create, edit, delete | Create, edit, delete |
| Other actions | Run allowed ones | Create, edit, delete | Create, edit, delete |
| Audio levels | Yes | Yes | Yes |
| Scenes, raid, queues, TTS settings | No | Yes | Yes |
| End stream, ban, title and category | No | Yes, with a second confirm | Yes |
| Sound uploads | Yes | Yes | Yes |
| Image, video, GIF uploads | No | Yes | Yes |
| Overlays and widgets | No | Edit | Edit, create, delete |
| Activity feed, alert history | Yes | Yes | Yes |
| Logs (scrubbed), connection status | No | No | Yes |
| Undo other mods' changes | Own only | Yes | Yes |
| Changes go live | Needs approval | Live and tell me | Live |

- Scopes: any permission can be limited to action groups, overlays or folders.
- Running an allowed action is not a change and never waits for approval.
- No "trusted with code" switch.

### Approvals and the change log
- Every change records who, what, when, before and after. One-click undo.
- The approval queue is in the desktop app and the phone app; it warns when the streamer changed the same thing since;
  pending changes expire after 7 days.
- Rate limits per member on changes and runs.

### Optional platform mod check
- Per member, off by default: Twitch members can be required to stay channel moderators (checked at sign-in and every
  15 minutes; losing mod status suspends, re-modding restores).

## 6. What mods see
- **Web portal:** dashboard (live activity, connections, quick actions), commands, actions (the editor limited to the
  member's step levels), sound alerts, audio levels, scenes, queues, TTS, overlays and widgets, uploads.
- **Phone app mod mode** (iOS and Android): join by invite link instead of the pairing QR; the same features through
  the same encrypted link.
- Pages and buttons a member has no permission for are not shown; Cuebot re-checks every request anyway.

## 7. Webhooks through the shared relay
- Address: `https://hooks.techdaddydigital.com/<relay id>/<route secret>/<service>`.
- On connect, Cuebot sends SHA-256 of its route secret; the relay keeps it in memory while connected and drops
  requests that do not match.
- **Every case** (online, offline, wrong route secret, over the limit) gets the same empty 200, so an address reveals
  nothing about whether the streamer is online. Only a malformed address (404), method (405) or body over 1 MB (413)
  differs.
- **Cuebot online:** the relay answers 200 at once, encrypts the raw request to the Cuebot identity key (ECDH with an
  ephemeral key) and forwards it. Cuebot checks the service signature as today. The readable request exists only in
  Worker memory for that request; nothing parses or logs it.
- **Cuebot offline:** the relay answers 200 and discards it. No replay of old events at the next stream.
- The self-hosted relay (`relay/`) keeps buffering for anyone who runs their own.

## 8. Uploads
- Size, duration and resolution limits per type; per-member quotas; no SVG, HTML or anything not listed.
- Accepted inputs: PNG, JPEG, WebP, GIF; MP3, Ogg (Vorbis, Opus), WAV, FLAC, AAC/M4A; MP4/MOV (H.264, HEVC, AV1),
  WebM (VP8, VP9, AV1).
- Pipeline in Cuebot: chunks (about 512 KB) reassembled in a quarantine folder; magic-byte check; full decode and
  re-encode by ffmpeg in a separate process (time and memory limits, low priority, few threads, only the one file);
  output PNG or WebP, animated WebP for GIFs, Ogg/Opus or MP3 for audio, VP9 WebM (with alpha when the source has it)
  or H.264/HEVC MP4 for video.
- Stored with random names, never executable; served with a fixed `Content-Type`, `nosniff` and a strict CSP.
- Approval follows the member's change setting; for Live members a file goes live only after the checks pass.
- As built (phase 5): chunks are 32 KB (the relay takes device frames up to 64 KB), checked against the size and
  SHA-256 the browser declares. Sounds come out as MP3, not Ogg/Opus: Cuebot's own player plays WAV, MP3, Ogg Vorbis
  and FLAC only. Stills come out as PNG, GIFs as animated WebP (checked by structure: ffmpeg 9 cannot decode animated
  WebP, so animated WebP is also not accepted as input), video as VP9/Opus WebM, transparent when the source is (libvpx
  decodes WebM alpha). AV1 input is left out (it needs dav1d). Every run: forced demuxer, `-protocol_whitelist file`,
  `-enable_drefs 0`, `-max_alloc`, two threads, below-normal priority, a time limit, a polled memory limit plus
  `ulimit -v` on Linux, an empty environment, all metadata and chapters dropped, bitexact output.
- Uploads are a change area (`upload`), so approval, undo, "My changes" and the 7-day expiry work as for actions.
  Files stay while the change log mentions them (undo); a sweep deletes the rest. The streamer can open a pending
  upload from the Mod portal page before approving.
- Mods may only point file settings (Play sound, Play sound from folder, Set image/media source file) at uploads; the
  portal sees an upload as `upload:<id>`, never the folder. Settings the streamer chose stay until a mod changes them.

### ffmpeg build
- Our own minimal LGPL build in CI for Windows x64, macOS arm64 and x64, Linux x64 and arm64: only the demuxers,
  decoders and encoders above, plus hardware encoders (NVENC, AMF, QuickSync via libvpl, VideoToolbox, VA-API).
  Expected 15–25 MB.
- As built (phase 5): `build/ffmpeg/build.sh` (FFmpeg 9.0.2, libvpx, Opus, libwebp, LAME, zlib; pinned SHA-256 sums)
  in the `ffmpeg` job of build.yml, cached by the script's hash; packages pick it up from `artifacts/ffmpeg/<rid>`
  (a publish target in Cuebot.App.csproj). Windows and Linux arm64 are cross-built on Linux, Intel Macs on Apple
  silicon. Software only so far: no hardware encoders and no H.264 output yet (VP9 WebM plays everywhere the portal
  needs). ffmpeg plus ffprobe are 32 MB installed (12 MB zipped); a shared-library build would roughly halve that.
  The Flatpak has none yet (uploads are off there).
- Decoding is always software (untrusted input never reaches GPU drivers).
- Encoding: transparent video always software VP9; other video uses the GPU only while not live, CPU at low priority
  while live; a setting for always or never GPU.
- No x264 (GPL): H.264 output uses the hardware encoders or OpenH264 (BSD).
- LGPL notice and a link to the exact source and build script in About and the docs. The Arch package depends on the
  system ffmpeg instead.

## 9. Privacy
- Stored on the relay: nothing per streamer, mod or webhook.
- Readable by the relay: nothing of the content (end-to-end encrypted), except webhook bodies for the duration of one
  request in memory.
- Unavoidable: IP addresses reach Cloudflare in transit (personal data under GDPR); Cloudflare processes them under
  its standard data processing agreement and its own network logging.
- A short privacy notice on `portal.` and in the docs states exactly this. Not legal advice; have it reviewed.

## 10. Abuse limits and cost
- Per relay ID: max 20 mod devices connected, daily byte cap, max frame size, frame rate, idle close.
- Connection attempts limited per relay ID in memory only. Not per source: that would mean reading the sender's IP
  address, which the Worker never does. Cloudflare's own DDoS protection covers floods.
- Workers Paid ($5/month: 10 million requests, Durable Objects included). Idle hibernated sockets cost almost nothing.
  A Cloudflare spending alert on the account.

## 11. Build phases

| Phase | Work | Done when |
|---|---|---|
| 0 ✅ | This plan; a crypto spike (handshake between .NET and WebCrypto, test vectors both ways) | `src/Cuebot.Portal/Crypto`, `portal/src/crypto.ts`, shared `portal/test/vectors.json` |
| 1 ✅ | Cuebot core: members, roles, risk levels on every step and trigger type, permission-checked portal API, marked values, change log and undo, approval queue, desktop UI for invites and members | `StepRiskCatalog`, `ModInfluence`, `PortalDirectory`, `PortalPolicy`, `PortalChanges`, `PortalApi`, the Mod portal page |
| 2 ✅ | Shared relay Worker: `/connect`, device routes, webhooks, limits, no-storage config; Cuebot relay client for it; deploy to techdaddydigital.com | `portal/worker`, `portal/RELAY.md`, `PortalRelayClient`; Worker tests in workerd; `PortalRelayLiveTests` passed against `wrangler dev`, the workers.dev address and `relay.techdaddydigital.com` |
| 3 ✅ | Sign-in: Google, then Twitch, then Discord; console setup | `PortalSignInHandler`, `PortalIdentityExchange`, the loader (`portal/web`); `PortalSignInLiveTests` passed 2026-11-05: a real Google, Twitch and Discord account each joined by invite through `relay.techdaddydigital.com` |
| 4 ✅ | Web portal UI: dashboard, commands, actions editor by level, sound alerts, audio, scenes, queues, TTS | `portal/ui`, `PortalControls`; `PortalControlsTests`; `PortalUiLiveTests` + `portal/shots.mjs` drove the deployed loader against the real API (sign-in, editing and saving an action, every page, a Standard Mod's view, phone width) and took the docs screenshots |
| 5 ✅ | ffmpeg CI build and the upload pipeline | `build/ffmpeg/build.sh`, `PortalUploads`, `MediaCleaner`; `MediaCleanerTests` (every format, polyglots: PNG+HTML/ZIP, GIFAR, JPEG comment, ID3, WAV chunk, MP4 atom; cut-off and fuzzed files; a pixel bomb; refused types) pass with Cuebot's build and with a system ffmpeg; Windows (Wine) and arm64 (qemu) builds converted files locally; an upload through the deployed relay went live and was picked in a step |
| 6 ✅ | Overlays and widgets in the portal | `PortalOverlays` (`overlay` and `widget` change areas), `OverlayValues`, `portal/ui/pages/overlays.tsx`; `PortalOverlaysTests` (roles, links never sent, text that cannot become code, fields a widget pastes unsafely, uploads only, alert designs, approval and undo, an undone delete keeps its link); `PortalOverlaysLiveDemo` + `shots.mjs overlays` through `relay.techdaddydigital.com`: an upload, text, picture, goal and alert edits went live on the overlay page |
| 7 (Android ✅) | Phone app mod mode, Android then iOS (on the Mac) | Android: `clients/android` `core/portal` (`PortalCrypto`, `PortalLink`, `PortalFlow`), the Mods tab (`ModViewModel`, `PortalWebView`, `ApprovalsViewModel`); `IPortalApprovals` and `/api/v1/portal/changes`; the return page hands `app.` sign-ins to the app. `PortalCryptoTest` (vectors), `PortalLinkTest`, `ModViewModelTest`, `ApprovalsViewModelTest`; `PortalPhoneLiveDemo` on an emulator through `relay.techdaddydigital.com`: a Standard Mod joined by invite, edited a command in the app, and the paired app approved it. iOS: open |
| 8 | Docs, privacy notice, wiki, release | mkdocs strict, wiki synced |

## 12. Open questions and later
- Phase 4 left for later: live updates pushed from Cuebot (the pages refresh every 3 to 5 seconds), scenes and audio
  for Streamlabs Desktop and Meld Studio, the approval queue on the phone, choice labels in the editor.
- A Cloudflare spending alert on the account (section 10).
- Phase 5 left for later: hardware encoders and H.264/HEVC output (section 8), a smaller shared-library ffmpeg,
  ffmpeg in the Flatpak, AV1 and animated WebP input, previews of uploads in the portal itself.
- Phase 6 left for later: limiting a role to some overlays (scopes cover action groups only), a live preview of the
  overlay in the portal (it shows layer boxes to scale), adding or reordering alert variations, colour pickers.
- Phase 7 left for later: the streamer's name in the portal (Cuebot does not send it; the app lets the mod name each
  streamer), invite links opening the app directly (Android App Links need assetlinks.json on portal.<domain>), a
  push notice when a change waits (the app checks while it is open).
- Later: Discord role checks; platform mod checks for YouTube, Kick and Rumble; a home-network shortcut for mods.
