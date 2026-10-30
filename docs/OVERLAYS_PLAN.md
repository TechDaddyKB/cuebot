# Overlays & Widgets designer: plan (draft)

Status: phase 0 in progress. Part 1 (overlay server, stores, Overlays page list) built 2026-10-30; part 2 (event
adapters) and part 3 (session data) next. Started 2026-10-30.

## Goal

A StreamElements-style overlay and widget designer in Cuebot. Purchased StreamElements (and Streamlabs) custom widgets
run unchanged and receive chat and events from every platform Cuebot supports (Twitch, YouTube, Kick, Rumble).
Alerts get a different design per trigger.

## Decisions so far

| # | Topic | Decision |
|---|---|---|
| 1 | Where the designer lives | Browser-based designer served by Cuebot's local web server (widgets are HTML; preview needs a real browser engine). |
| 2 | Import formats | StreamElements custom widgets (incl. widget.io zip), then Streamlabs custom widgets. |
| 3 | First release scope | Full StreamElements parity across all widget categories. |
| 4 | Overlay URL access | This PC by default; a setting allows other computers on the LAN (two-PC setups). |
| 5 | Alert designs | Reusable library (like SE "Elements"), shared across overlays. |
| 6 | YouTube popout-chat CSS themes | Supported: Cuebot's chat box reproduces YouTube's popout chat structure so those CSS files apply. |
| 7 | Licensing | Users import widgets they own; Cuebot never bundles or redistributes them; only "compatible with StreamElements widgets" wording, no SE marks. |
| 8 | SE-service widgets (Merch, Store, Contest/Giveaway, Media Request, Charity) | Cuebot's own features by default; pull from the StreamElements API when an SE account is connected. |
| 9 | Canvas sizes | 1920x1080, 2560x1440, 3840x2160, vertical 1080x1920, and custom sizes. |
| 10 | Existing SE overlays | Import them through the connected SE account (layers + settings) to edit and run locally. |
| 11 | Sharing | One-file overlay and alert packages (purchased widget code excluded unless the user confirms rights), plus a community gallery. |
| 12 | Gallery hosting | A public GitHub repo; gallery items arrive as pull requests. No server to run. |
| 13 | Gallery moderation | Review before publish: an upload goes live only when a maintainer merges it. |
| 14 | Stream totals with an SE account connected | Cuebot's cross-platform totals by default; each goal or label widget can switch to StreamElements' numbers. |
| 15 | Widget network access | Open internet, as on StreamElements; Cuebot logs which hosts each widget contacts. Widgets can never reach Cuebot's control API. |
| 16 | Designer access from other computers | Same network setting as overlays (one switch covers both). |

## Research: how StreamElements does it

### Overlay editor (observed in the dashboard)
- Overlay = one web page (default 1920x1080), added to OBS as a browser source by a URL with a secret token.
- Layers: group, ungroup, duplicate, delete, show/hide, lock, rename. Per layer: X/Y, W/H, opacity, rotation, center,
  enter/exit animation with durations.
- Toolbar: Emulate (fire test events), undo/redo, zoom, reset zoom, fit, snapping, mute editor audio, session data
  view, copy URL, preview, save.
- "Elements" (dashboard) = saved widgets/alerts reusable across overlays; Elements gallery and Overlays gallery.

### Widget catalog
- Alerts: AlertBox, Merchandise Alert Box, HypeCup, Kappagen, Store redemptions, HypeBoss.
- Engagement: Countdown, Contest, Giveaway, Twitch Polls, Twitch Predictions, Media Request, Merchandise Goals,
  Subscribers Credit Roll.
- Labels: Subscribers, Followers, Bits, Tips, Raids, Other (latest/recent names and amounts, goal progress, revenue
  goal, merch goals).
- Static / Custom: Text, Image, Video, Asset rotator/slideshow, Merch products rotator, Custom widget.
- Charity & Seasonal: Seasonal, Charity.
- Stream tools: chat box ("Your stream's chat").

### AlertBox
- Sections per event: Follower, Subscriber, Tip, Cheer, Raid, Purchase, Charity campaign donation (each toggleable).
- Default design per event: image or video (+ video volume), sound (+ volume), layout (3 options), show user message,
  message template (`{name}`, `{amount}`), duration (max 90 s), username max length, custom CSS, text settings
  (primary/secondary text: font, color, weight, size, line height, spacing, alignment, casing, stroke, shadow,
  highlight, pulse), animation (enter/exit, text enter/exit, show text after, hide text before end), TTS (announce
  action first, voice, volume, delay).
- Variations: name, parameter (sub: months subscribed, tier, gift, community gift, paid upgrade, iOS extension,
  name), condition (Exact / At least), requirement amount, chance %, full design override. Shipped defaults:
  Resubscriber, Subscriber gift, Community gifts. Option: pick randomly when several match.
- Custom CSS tokens: `{name} {amount} {months} {tier} {sender} {currency} {items} {messageTemplate} {message}
  {messageRaw} {image} {video} {videoVolume} {audio} {audioVolume} {widgetDuration}`.

### Custom widget runtime (docs.streamelements.com)
- Panes: HTML, CSS, JS, FIELDS (JSON schema), DATA (saved field values). jQuery preloaded. `{field}` / `{{field}}`
  substituted as raw text before load.
- widget.io zip: `widget.ini` with `[HTML] [CSS] [JS] [FIELDS] [DATA]` sections pointing at
  `html.txt css.txt js.txt fields.txt data.txt`. Importer should tolerate missing files and alternate names.
- Events:
  - `onWidgetLoad`: `{channel, currency, fieldData, recents, session:{data,settings}, overlay:{isEditorMode,muted}}`.
  - `onEventReceived`: `{listener, event}` for `message`, `delete-message`, `delete-messages`, `follower-latest`,
    `subscriber-latest`, `tip-latest`, `cheer-latest`, `raid-latest`, `host-latest`, `kicks-latest`,
    `superchat-latest`, `sponsor-latest`, `event`, `event:test`, `event:skip`, `alertService:toggleSound`,
    `bot:counter`, `kvstore:update`, `widget-button`.
  - `onSessionUpdate`: `{session}`.
- Chat `message` payload: `event.data = {time, tags, nick, userId, displayName, displayColor, channel, text,
  isAction, msgId, badges[{type,version,url,description}], emotes[{type,name,id,gif,urls,start,end}]}`,
  plus `event.renderedText` and `event.service`.
- Subscriber fields: `tier`, `gifted`, `sender`, `bulkGifted`, `isCommunityGift`, `playedAsCommunityGift`.
- `SE_API`: `store.get/set` (emits `kvstore:update`), `counters.get`, `sanitize`, `cheerFilter`, `setField`,
  `getOverlayStatus`, `resumeQueue`, internal `sendMessage` RPC, `events.emit/broadcast`.
- Fields: `text textarea number slider checkbox colorpicker dropdown image-input video-input sound-input googleFont
  button hidden`; options `group label value min max step options multiple`; `widget*` names reserved
  (`widgetDuration` drives the alert queue hold).
- Queue: with `widgetDuration` > 0, alert events wait for the duration or `resumeQueue()`; chat, deletes, counters,
  kvstore, test/skip/mute events are never queued.
- Session data: `<type>-{latest,session,week,month,total,count,goal,recent}` for follower, subscriber, tip, cheer,
  host, raid, sponsor, superchat, kicks, merch, purchase, charityCampaignDonation; plus top donation/donator
  leaderboards and subscriber new/resub/gifted variants. Sessions reset 15 min after going offline.

### Sample: Cherry Blossom chat (Etsy, @vryhoth / @milaeshop)
- widget.io zip; 163 KB obfuscated JS; 30+ fields. Uses `message`, `delete-message(s)`, `widget-button`,
  `follower/subscriber/tip/cheer/raid-latest`. Calls the alejo.io pronouns API and Google Fonts.
- Conclusion: purchased widgets are not editable in their logic; Kick/Rumble support comes from feeding them
  SE-shaped events, plus field and CSS overrides.

## Proposed architecture (to refine)
1. SE-compatible widget host page: sandboxed iframe per widget, Cuebot shim for the three events and `SE_API`
   (store -> Cuebot storage, counters -> globals, sanitize -> moderation filter). Proxy known third-party calls
   (pronouns) to Cuebot services.
2. Platform converters: Kick/Rumble/YouTube chat -> `message` with badge URLs and emote ranges; Rumble Rants and
   YouTube Super Chats -> `tip-latest`/`superchat-latest`; Kick subs/gifts -> `subscriber-latest`; `service` set.
3. Streamlabs adapter: same host, Streamlabs event names and payloads.
4. Widget editor: settings form from FIELDS, five code panes, CSS override pane, per-platform Emulate.
5. Overlay editor (web): canvas, layers, all built-in widget categories, URL per overlay.
6. Alert library: any Cuebot trigger -> alert design; variations by any trigger value, platform, range, chance;
   TTS via Cuebot speech engines; queue pause/skip/mute; "Show alert" sub-action.
7. Session data store across platforms (latest/session/week/month/total/goal/recent, leaderboards).

## Alert types and Cuebot triggers

Default mapping from StreamElements alert types to Cuebot triggers. Any other Cuebot trigger can also get an alert
design ("custom alert"), and every variation can additionally filter on platform.

| Alert type (SE listener) | Twitch | Kick | Rumble | YouTube | Other |
|---|---|---|---|---|---|
| Follower (`follower-latest`) | Follow | Follow | Follow | NewSubscriber | |
| Subscriber (`subscriber-latest`) | Sub, Resub, Gift, Community gift | Subscription, Resubscription, GiftSubscription, MassGiftSubscription | Subscribe, GiftSub | NewSponsor, MemberMilestone, MembershipGift, GiftMembershipReceived | |
| Tip (`tip-latest`) | | | Rant | | Donation services (StreamElements, Streamlabs, Tipeee, TreatStream, DonorDrive, Pally) |
| Super Chat (`superchat-latest`) | | | | SuperChat, SuperSticker | |
| Cheer (`cheer-latest`) | Bits | | | | |
| Kicks (`kicks-latest`) | | KicksGifted | | | |
| Jewels | | | | JewelsGifted | (no SE equivalent; custom alert) |
| Raid (`raid-latest`) | Raid | Host | | | |
| Redemption | Channel points | RewardRedemption | | | Cuebot store |

Variation parameters per alert type follow the trigger's own values (months, tier, gift count, amount, currency,
reward name, raider count, user name, platform), with Exact / At least / Range conditions and a chance %.

## Build phases

Each phase ships something usable. Full StreamElements parity is the goal by the end of phase 6.

### Phase 0: foundations
- Overlay web server on Cuebot's Kestrel host: per-overlay URLs with a secret token, this-PC by default, LAN
  setting, live reload.
- Data model and storage: overlays, layers, widget instances, element library (alerts and widgets), assets
  (uploads, cached Google Fonts).
- Event bus to the browser: one WebSocket per overlay page.
- Converters: Twitch, Kick, Rumble, YouTube and donation events into SE-shaped payloads (`service` set; badge URLs
  and emote ranges for Kick, Rumble and YouTube).
- Session data store across platforms: latest, session, week, month, total, count, goal, recent, leaderboards;
  resets 15 min after going offline.

#### Phase 0 design

Where the code goes, following existing patterns:
- **Contracts in `Cuebot.Core/Overlays`**: `OverlayEvent(Listener, Event JSON)`, `IOverlayEventAdapter` (one per
  module, like `IChatEventAdapter`: turns that module's `BotEvent`s into SE-shaped events), `IBadgeSource` (badge set
  and version to image URL), `SessionDataStore`, `OverlayStore`, `ElementStore`, `AssetStore`.
- **Adapters in each platform module** (`Cuebot.Twitch`, `Cuebot.Kick`, `Cuebot.Rumble`, `Cuebot.YouTube`,
  `Cuebot.Donations`), registered in their `Add*()` extensions. They read the args the module already publishes, so no
  platform service changes.
- **The overlay server in `Cuebot.Server/Overlays`**, as a new `ServerKind.Overlays` started by `ServerManager`. It
  reuses `KestrelListener`, `NetworkPolicy` (follows `AllowLan`) and `ApiToken`.

Overlay server (`servers.json` gains `Overlays { Enabled, Address, Port = 7476 }`, on by default from settings version 3):
- `GET /o/{overlayId}?token=...`: the overlay page. One random token per overlay (`ApiToken.Generate`), compared in
  constant time; a wrong or missing token is a 404, so overlay IDs are not discoverable.
- `GET /o/{overlayId}/ws?token=...`: WebSocket. On open, sends `onWidgetLoad` data (fieldData placeholders arrive in
  phase 1); then every converted event, session update and overlay command (reload, mute, skip).
- `GET /runtime/*`: Cuebot's own scripts and styles, embedded resources, cached by content hash.
- `GET /assets/{sha256}.{ext}`: user uploads (images, video, sound, fonts) from `AppPaths/overlays/assets`, served
  immutable and cacheable; content-addressed so the same file is stored once.
- No designer routes yet (phase 2); the network switch will cover them too, protected by the same token scheme.
- Events reach sockets through a channel pump off the publishing thread, like `ServerManager`'s outbox, with a bounded
  queue per connection (slow OBS sources drop the oldest chat messages, never alerts).

Storage (`JsonFile<T>` with debounced atomic writes, `AppPaths` null = in-memory for tests):
- `overlays.json`: overlays (id, name, canvas width/height, token, layers with position, size, opacity, rotation,
  animations, element reference or inline settings).
- `elements.json`: the reusable library (alerts and widgets).
- Custom widget code in `overlays/widgets/{id}/{html,css,js,fields,data}` so large purchased scripts stay out of the
  JSON files and diff cleanly.
- `session-data.json`: the session store below.

Event conversion (SE shapes from the research section):
- Chat: `chat.message` plus the platform args become `message` with `nick`, `userId`, `displayName`,
  `displayColor` (platform colour, or a stable colour from the user ID when the platform has none), `text`, `msgId`,
  `badges[]` with image URLs from `IBadgeSource`, `emotes[]` with ranges and URLs from `EmoteService`, `renderedText`
  (HTML with emote images), Twitch-style `tags` (filled for Twitch, synthesized for the rest so widgets reading
  `tags.mod`/`tags.subscriber` work), and `service`.
- Deletes: neutral `chat.messageDeleted`/`chat.userBanned`/`chat.userTimedOut` become `delete-message` and
  `delete-messages`. Rumble has no delete events; nothing to send.
- Alerts: the trigger mapping table above becomes `follower-latest`, `subscriber-latest`, `tip-latest`,
  `cheer-latest`, `raid-latest`, `superchat-latest`, `kicks-latest` with SE fields (`name`, `amount`, `message`,
  `tier`, `gifted`, `sender`, `bulkGifted`, `isCommunityGift`), plus a raw `event` listener for everything.
- Badge images: Twitch from Helix global and channel badges (cached under `cache/badges`); Kick from the channel's
  subscriber badges plus built-in icons for broadcaster, moderator, VIP, OG, founder, verified; Rumble and YouTube from
  built-in icons keyed by the badge names they already publish.

Session data store:
- Keys `<type>-{latest,session,week,month,total,count,goal,recent}` for follower, subscriber, tip, cheer, raid,
  superchat, kicks, plus top donation and donator per period, built from the same converted events so every platform
  counts.
- Session resets 15 minutes after the last platform goes offline (or on the next StreamOnline after that); week and
  month roll over by calendar; totals persist.
- Changes are coalesced (at most a few per second) and sent as `onSessionUpdate`.
- Goals are set by the user (phase 4 UI); until then they are stored but empty.

Delivery in three merges, each with tests:
1. Overlay server, settings, token URLs, WebSocket, runtime and asset routes, stores (`Cuebot.Server.Tests`,
   `Cuebot.Core.Tests`).
2. Event adapters and badge sources for the four platforms and donations (converter tests from recorded args; no
   signed-in data in fixtures).
3. Session data store (period rollover and reset tests with a fake clock).

### Phase 1: widget runtime and chat
- SE-compatible host: sandboxed iframe per widget; `onWidgetLoad`, `onEventReceived`, `onSessionUpdate`; `SE_API`
  (store, counters, sanitize, cheerFilter, setField, getOverlayStatus, resumeQueue, sendMessage RPC, events);
  `{field}` substitution; `widgetDuration` queue semantics; jQuery preloaded.
- Import: widget.io zip, the five code panes pasted by hand.
- Widget settings form generated from FIELDS; code panes; CSS override pane; per-platform Emulate (test messages
  and events as Twitch, Kick, Rumble, YouTube).
- Proxies for third-party calls widgets make (alejo.io pronouns -> Cuebot pronouns service).
- Built-in chat box widget, including YouTube popout-chat CSS theme compatibility.
- Milestone: the Cherry Blossom widget running in OBS with Kick and Rumble chat.

### Phase 2: web designer
- Canvas at 1080p, 1440p, 4K, vertical 1080x1920 or custom size.
- Layers: add, group, duplicate, delete, show/hide, lock, rename, reorder; position, size, opacity, rotation;
  enter/exit animations (animate.css names for SE compatibility); snapping, zoom, fit, undo/redo; mute editor audio.
- Preview, copy URL, session data view.
- Static widgets: Text, Image, Video, Asset rotator/slideshow.
- Element library: save any widget as a reusable element and place it in several overlays.
- Cuebot app: Overlays & Widgets page lists overlays, opens the designer in the browser, copies URLs.

### Phase 3: alerts
- Alert library with one design per alert type and per custom trigger.
- AlertBox widget: image or video, sound, layouts, message template and tokens, durations, username length, text
  styling (primary and secondary), enter/exit and text animations, custom CSS with the SE tokens.
- Variations: parameter, condition, amount, chance, platform filter, full design override; random pick option.
- TTS through Cuebot speech engines; queue pause, skip and mute from the app, the API and actions.
- "Show alert" sub-action so any action can fire a designed alert.

### Phase 4: labels, goals and lists
- Labels: latest and recent follower, subscriber, tip, cheer, kicks, raid; top donation and donator.
- Goal bars: follower, subscriber, tip, cheer, revenue; session, week, month or total.
- Event list, Subscribers credit roll, Countdown.

### Phase 5: engagement and service widgets
- Polls and Predictions (Twitch, YouTube, Cuebot-native for Kick and Rumble).
- Contest and Giveaway, Media Request, Store redemptions: Cuebot features by default, StreamElements data when an SE
  account is connected.
- Kappagen (emote rain), HypeCup, HypeBoss.
- Merch alerts, goals and product rotator (SE account only), Charity and Seasonal widgets.

### Phase 6: other formats and importing from StreamElements
- Streamlabs custom widget adapter (same host, Streamlabs event names and payloads).
- Import existing StreamElements overlays through the connected SE account.

### Phase 7: sharing
- One-file overlay and alert packages; purchased widget code left out unless the user confirms they own the rights.
- Community gallery: hosting, accounts, uploads, moderation, reporting and takedowns.

## Open questions
- SE overlay import (phase 6): the dashboard lists overlays with `GET api.streamelements.com/kappa/v2/overlays/{channelId}`
  (seen 2026-10-30). The per-overlay JSON (layers, widget settings, custom code) has not been inspected yet; check it
  through Cuebot's own StreamElements connection at the start of phase 6.
- Designer access: because the designer shares the overlay network switch, turning it on lets anyone on the LAN edit
  overlays. Decide in phase 0 whether that switch needs a password or a warning.
- Gallery repo: name, contribution template, and the automated checks a pull request runs (package validity, no
  purchased code, preview image).
