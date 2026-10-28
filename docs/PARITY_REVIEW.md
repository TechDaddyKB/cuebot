# Parity review: Cuebot against the public Streamer.bot and Speaker.bot docs

Review date 2026-10-28, against Cuebot `4a479c4`. This is a review draft and is not committed.

**Sources.** Public docs only: `sb-docs` (github.com/Streamerbot/docs) and `sb-wiki` (the older wiki), both under
`C:\Users\docker\cuebot-sb-exports\`. On the Cuebot side I used Cuebot's own source. Nothing under `decompiled/` was opened.

**Method.**
- **Documented items.** A script read the front matter (title, parameters, variables) of every page: 379 sub-action
  pages, 454 trigger pages, 202 C# method pages, the C# classes and enums, the WebSocket/HTTP/UDP pages, the guides and
  the Speaker.bot pages.
- **Cuebot items.** A throw-away tool, built in a scratch copy of the repo, built the real DI container
  (`AddCuebotCore().AddTwitch()...`, the same list as `AppHost`) and dumped the live `Registry` and `ScriptApi`.
  That gave 552 trigger types, 494 sub-actions and 422 script functions. It also listed the public methods of `CphProxy`.
- **Matching.** Items were matched by meaning, not name. Before calling anything "missing" I grepped for it.
- **Variables.** Variables were checked by name, because imported actions, `%var%` text and `args["x"]` in C# all
  depend on the exact name. A static check looked for each documented variable as a string literal in that platform's
  module; it also handles interpolated names like `$"fw.variants[{i}].id"` and `Flatten(prefix)`. The high-traffic
  Twitch mappers were then read by hand to confirm the result (`EventSubMapper.cs`).
- **Limits of the variable check.** The variable counts are flags, not proofs, though spot checks found only real
  mismatches. They also undercount. Twitch `commonVariables` (`TwitchUser`, `TwitchChat`, ...) are defined in the
  docs site code, not in the repo, so they were not checked. And a name that exists anywhere in a module counts as
  present for every trigger of that module.

Legend: **M** missing · **P** partial (what is missing is said) · **NP** not possible (no public API, platform
limit, or already marked so in PARITY.md). Present items appear only as counts.

Path prefixes used below:

| Prefix | Path |
| --- | --- |
| `SA:` | `sb-docs/streamerbot/3.api/1.sub-actions/` |
| `TR:` | `sb-docs/streamerbot/3.api/2.triggers/` |
| `CS:` | `sb-docs/streamerbot/3.api/3.csharp/` |
| `WS:` | `sb-docs/streamerbot/3.api/4.websocket/` |
| `GD:` | `sb-docs/streamerbot/2.guide/` |
| `SP:` | `sb-docs/speakerbot/` |
| `WK:` | `sb-wiki/` |

## Summary

| PARITY.md section | Documented items checked | Present | M | P | NP |
| --- | --- | --- | --- | --- | --- |
| 1. Actions engine and core concepts | 31 sub-actions/triggers + guide features | 13 | 11 | 8 | 0 |
| 2. Variables and text | 8 + guide variables/functions | 4 | 1 | 7 | 0 |
| 3. Commands, timers, users, points, quotes | 42 | 19 | 3 | 20 | 0 |
| 4. Platforms (Twitch, YouTube, Kick) | 249 (173 triggers, 76 sub-actions) | 96 | 0 | 148 | 5 |
| 5. Stream apps and integrations | 450 (240 triggers, 210 sub-actions) | ≈291 | 21 | 44 | 94 |
| 6. System, input and services | 42 | 24 | 2 | 16 | 0 |
| 7. Servers, APIs and scripting | 268 (202 C# methods, 35 classes/enums, 2 C# sub-actions, 29 WS/HTTP/UDP requests) + 508 event types | 141 | 127 | 5 | 0 |
| 8. Speaker.bot | 9 + guide/API features | all but 1 | 1 | 0 | 0 (2 N/A) |
| 9. App shell | guide features | — | 2 | 1 | 0 |

How to read the counts:
- **Section 4.** Almost all of its partials are **variable-name gaps**. The triggers and sub-actions exist, but some
  of their documented variables are missing or named differently.
- **Section 7.** The M count was dominated by **100 documented `CPH` method names** that `CphProxy` did not define (fixed in `w14-cph`).
  Any Streamer.bot script that called one of them failed to compile in Cuebot.

## Most user-visible gaps (prioritized)

1. **100 documented C# (`CPH`) methods are missing.** (Fixed in `w14-cph`, see section 7.) A pasted or imported `CPHInline` script that calls any of them
   fails to compile. This contradicts PARITY.md §7, which says scripts "compile unchanged".
   - Missing families: all 11 `Kick*`/Kick user-var methods; 18 `YouTube*` methods; 8 `MeldStudio*`; 7 OBS filter
     methods; 44 Twitch methods (rewards, redemptions, pins, AutoMod, blocked terms, suspicious users, clips,
     `TwitchWarnUser`, `TwitchReplyToMessage`, mods/VIPs); 11 `Command*` cooldown/counter methods; `Parse`.
   - Many of these already exist as Cuebot script functions (`kick.ban`, `youtube.setTitle`, ...). Only the CPH
     names are missing. Source: `CS:3.methods/**`. Full list in §7.
2. ✅ **Fixed (w14-vars-platforms): the Streamer.bot names are now added next to the Cuebot names; what is left is in §4.** **Twitch trigger variables are named differently from Streamer.bot.** 77 of the 100 Twitch triggers that list
   variables have at least one missing name. Confirmed in `EventSubMapper.cs`:
   - Reward Redemption: `rewardName` is `rewardTitle`, `rawInput` is `userInput`, and `skipsQueue`,
     `requiresUserInput`, `maxPerStream`, `maxPerUserPerStream` are missing.
   - Resub: `cumulative` is `cumulativeMonths`, `monthStreak` is `streakMonths`, and `isMultiMonth` /
     `multiMonthDuration` are missing.
   - Gift Sub: `recipientId` is `recipientUserId`; `totalSubsGifted`, `monthsGifted`, `fromGiftBomb` and
     `subBombCount` are missing.
   - Gift Bomb: `gifts` / `totalGifts` are `total` / `cumulativeTotal`, and `gift.recipientUser#` is missing.
   - Polls, predictions, charity, goals and hype train use flat names (`pollId`, `charityName`, `goalId`) instead
     of `poll.*`, `prediction.*`, `charity.*`, `goal.*`, `top.*`.
   - Bans and timeouts have no `createdBy*`. Raid Start/Send/Cancel have no `raidUser*`.
   - Source: `TR:twitch/**`.
3. ✅ **Fixed (w14-vars-platforms), except the shared Core user-group sub-actions; see §4.** **Sub-action output variables are named differently.** Flagged: 32 of 36 Twitch, 12 of 12 Kick and 2 of 2
   YouTube sub-actions that list outputs. Examples: `createClipUrl`/`createClipId` (Cuebot: `clipUrl`), `banResult`
   and `bannedUser*`, `timeoutResult`, `followAgeLong` / `followAgeShort` / `followDate`,
   `targetUserProfileImageUrl`, `targetIsPartner`, `poll.*` / `prediction.*` from the Get/Lock/Resolve sub-actions,
   `titleSuccess`, `gameSuccess`. Source: `SA:twitch/**`, `SA:kick/**`, `SA:youtube/moderation/*`.
4. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **No `rawInputEscaped` / `rawInputUrlEncoded` / `inputEscaped#` / `inputUrlEncoded#` anywhere.** These are
   commonly used to build URLs for Fetch URL. Cuebot has `$urlencode()$` instead.
   Source: `TR:core/commands/command-triggered.md`, `TR:twitch/channel-reward/reward-redemption.md`,
   `TR:kick/kicks/gifted.md`, `TR:youtube/chat/first-words.md`.
5. **Newtonsoft.Json is not available to C# code, and custom DLL references cannot be added.** (Fixed in `w14-cph`, see section 7.) The docs say
   Newtonsoft "is included in Streamer.bot by default", and a recipe covers custom DLLs. Cuebot compiles against the
   framework plus its own assemblies only, so `using Newtonsoft.Json;` fails.
   Source: `CS:0.guide/1.variables.md`, `CS:1.recipes/custom-dlls.md`, `CS:0.guide/5.debugging.md` (References, Find Refs).
6. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **The action-queue sub-actions are missing:** Clear Action Queue, Set Action Queue Blocking State and Set Action
   Queue Pause State. Queues can only be controlled from the UI and through `CPH.Pause/ResumeActionQueue`.
   Source: `SA:core/action-queues/*`.
7. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **The action, trigger and sub-action toggles are missing:** Set Trigger State, Set Sub-Action State, Set
   Sub-Action Weight, Get Action Group State, Set Action Group State and Trigger Custom Event.
   Source: `SA:core/triggers/*`, `SA:core/actions/*`.
8. **The C# `EventType` and `EventSource` enums are incomplete,** (fixed in `w14-cph`, see section 7) so `EventType.X` / `EventSource.X` in scripts does
   not compile for most events:
   - `EventType` has 74 of 508 documented values.
   - `EventSource` has 26 of 46.
   - 17 data classes and 3 enums are missing (`TwitchReward`, `QuoteData`, `TeamInfo`, `YouTubeUserInfo`, ...).
   - Source: `CS:5.enums/event-type.md`, `CS:5.enums/event-source.md`, `CS:4.classes/*`.
9. **About 100 WebSocket events go out under names Streamer.bot clients don't subscribe to.** (Fixed in `w14-websocket`, see section 7.) Examples:
   - Twitch chat modes are `EmoteModeOn` instead of `ChatEmoteModeOn`.
   - Streamlabs Desktop's source is `Slobs` instead of `StreamlabsDesktop`.
   - Pulsoid sends `HeartRate` instead of `HeartRatePulse`.
   - T.I.T.S.'s source is `Tits` instead of `ThrowingSystem`.
   - `Raw.Action` and `Raw.SubAction` are never emitted.
   - Source: `CS:5.enums/event-type.md`, `WK:Servers-Clients/WebSocket-Server/Events/**`.
10. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **Get Input is a different feature.** Streamer.bot's Get Input opens an interactive dialog (Title, Prompt,
    Original Text, `success`). Cuebot's "Get input" splits text into `input0..N`. Source: `SA:core/logic/get-input.md`.
11. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **Streamer.bot Decks are missing entirely:** 10 sub-actions, plus the web-deck feature. PARITY.md never
    mentions them. Source: `SA:streamerbot/decks/*`, `GD:04.extra-features/2.streamerbot-decks.md`.
12. **The C# sub-action options are missing:** (Fixed in `w14-cph`, see section 7.) the Execute C# Method sub-action, Precompile on Application Start /
    Delayed Start, and Save Result to Variable. Source: `SA:core/csharp/*`, `CS:0.guide/3.custom-methods.md`.
13. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **Points has missing and partial sub-actions** (PARITY.md says ✅).
    - Missing: Check Points, Delete Points, Get Points Definition.
    - Partial: the leaderboard has no `Offset`; Award/Update Points for one user cannot apply multipliers; Award
      Points to All only pays present viewers.
    - Source: `SA:core/points/**`.
14. ✅ **Fixed (w14-core), see sections 1-3 and 6.** **File and system sub-actions are incomplete:**
    - Read Lines From File only gives `fileContent`, with no `line#` / `lineCount`.
    - Read Specific Line is missing.
    - Read Random Line has no `Count`.
    - Run a Program has no environment variables and no `output#` capture.
    - Play Sound has no `soundLength` / `soundLengthMs`.
    - Fetch URL has no "Parse result as JSON" option.
    - UDP Broadcast is missing.
    - Source: `SA:core/file-io/*`, `SA:core/system/run-a-program.md`, `SA:core/sounds/*`, `SA:core/network/*`.
15. **Streamlabs Desktop is missing 5 sub-actions and the `sd.*` trigger variables.** Missing: Set State of a Random
    Filter, Hide Group's Sources, Set Random Group Source Visible, Set Scene Filter State and Hide Source's Filters.
    None of the 7 triggers has `sd.id`, `sd.name` or `sd.host`. Source: `SA:streamlabs-desktop/**`,
    `TR:streamlabs-desktop/*`.

Close behind:
- IFTTT (2 sub-actions) and the Discord Basic Webhook sub-action are missing; Discord works only through `CPH`.
- Crowd Control variables use flat names (`effectId`) instead of `effect.*`, `game.*` and `gamePack.*`.
- Cloud backups: done with Google Drive (w16).
- Range-trigger "Always Run" overlap semantics are missing.
- If/Else regex captures (`match.count`, `match[i]`) are missing.

## PARITY.md claims that look wrong or overstated

| PARITY.md row | Claim | What the docs show |
| --- | --- | --- |
| §7 CPH compatibility layer ✅ | "~175 methods; `CPHInline` scripts compile unchanged" | 102 of 202 documented method names exist; 100 are missing. `EventType` and `EventSource` enums are incomplete, 20 classes/enums are missing, and Newtonsoft.Json and custom references are unavailable. Should be 🟡. |
| §7 WebSocket server ✅ | "events mapped to Streamer.bot source/type names" | About 100 Cuebot events map to names that differ from Streamer.bot's `EventType` names (list in §7). `Raw.Action`/`Raw.SubAction` are never sent. Payloads are flat, while Streamer.bot clients read nested fields such as `data.reward.title` (`GD:04.extra-features/3.html-overlay.md`). |
| §4 Twitch ✅ "95/95 sub-actions" | complete | The sub-actions exist, but most trigger and sub-action variables use Cuebot names (top gaps 2–3). Imported Streamer.bot actions that read `%rewardName%`, `%cumulative%`, `%createClipUrl%` and similar get empty values. |
| §4 Kick ✅ "14/14 sub-actions" | complete | All 12 Kick sub-actions that document outputs are flagged (`banResult`, `targetUser*`, `foundUser*`, `addResult`); trigger variables like `reward.*`, `kicks.*`, `createdBy*`, `monthsSubscribed` are flagged too; and the 11 Kick `CPH` methods are missing. |
| §1 Named action queues ✅ | "pause/resume/clear" | Only in the UI and through `CPH`. The three queue sub-actions are missing. |
| §1 Logic ✅ "Get Input" | present | Cuebot's Get input does something else; the dialog is missing. Random Number has no "Next Float" (`randomFloat`, `randomPercent`). If/Else has no regex capture variables. |
| §1 Signals ✅ | local send/wait | Send Local Signal has no "Include Action's Arguments" / "Arguments" / "Queue Signal". Wait has no "Overwrite Variables" and no `signalTimeout`/`hasResult`/`signalName`. |
| §1 Random action / weights ✅ | present | Weights work in the editor, but the Set Sub-Action Weight sub-action is missing. |
| §3 Points ✅ | complete apart from payout rules | Check Points, Delete Points, Get Points Definition and the leaderboard offset are missing. |
| §3 Commands ✅ | complete | The Command Triggered/Cooldown variables `commandSource`, `commandType`, `cooldownLeft`, `globalCooldownLeft`, `userCooldownLeft` and `msgId` are missing, as are the 11 `Command*` cooldown/counter `CPH` methods. |
| §5 Crowd Control ✅ | "9/9 triggers" | All triggers exist, but 7 use flat variable names instead of the documented `effect.*`/`game.*`/`gamePack.*`. |
| §7 "Python code sub-action" ➕, scripting ✅ | — | Correct as Cuebot-only. Execute C# Method, precompile and save-result options are not mentioned anywhere. |

Whole documented features PARITY.md never mentions:
- Streamer.bot Decks
- IFTTT
- Discord Basic Webhook sub-action
- Cloud backups
- the Get Input dialog
- UDP Broadcast
- Set Trigger State / Set Sub-Action State
- Execute C# Method
- custom C# references / Newtonsoft
- the `/shutdown` startup switch
- the `Raw.Action`/`Raw.SubAction` WebSocket events

---

## 1. Actions engine and core concepts

Present: 13 (Run Action, Set/Get Action State, Set Argument, Break, Loop Break/Continue, Switch, While, Log, Test
trigger, Custom/Code Event triggers, Streamer.bot Started); the queue, pending and history UIs, trigger viewer,
copy/paste and undo.

**Fixed (w14-core).** Legend: **F** fixed.
- F · Clear Action Queue (`core.clearActionQueue`, one queue or All; sets `queueCleared`) — `SA:core/action-queues/clear-action-queue.md`
- F · Set Action Queue Blocking State (`core.setActionQueueBlocking`, Enable/Disable/Toggle, saved; the Default queue never blocks) — `SA:core/action-queues/set-action-queue-blocking-state.md`
- F · Set Action Queue Pause State (`core.setActionQueuePause`, Pause/Resume/Toggle, All, Clear) — `SA:core/action-queues/set-action-queue-pause-state.md`
- F · Get Action Group State (`actionGroupState` 0/1/2, `actionsEnabled`, `actionsDisabled`) — `SA:core/actions/get-action-group-state.md`
- F · Set Action Group State — `SA:core/actions/set-action-group-state.md`
- F · Set Sub-Action State (step by position, counting steps inside groups and branches, or by id) — `SA:core/actions/set-sub-action-state.md`
- F · Set Sub-Action Weight — `SA:core/actions/set-sub-action-weight.md`
- F · Trigger Custom Event (`core.triggerCustomEvent`; Use Args passes the run's arguments; fires the "Custom code event" trigger) — `SA:core/triggers/custom-event-trigger.md`
- F · Set Trigger State (trigger by position, id or type) — `SA:core/triggers/set-trigger-state.md`
- F · Get Input: the dialog (`core.getInputDialog`: Title, Prompt, Original Text, Output Variable, Auto Type, `success`). The old text splitter keeps its type id `core.getInput` as "Split text into inputs". Headless runs count as cancelled — `SA:core/logic/get-input.md`
- F · Pick Color (`core.pickColor`: colour chosen when editing; `name.color.a/r/g/b`, `.html`, `.htmlalpha`, `.obs`) — `SA:settings/pick-color.md`
- F · Delay: Random and To — `SA:core/delay.md`
- F · Comment: colour parameter (stored; the list editor does not paint it yet) — `SA:core/misc/comment.md`
- F · Get Random Number: "Next Float" (`randomFloat`, `randomPercent`). The default argument stays `random` for older actions; the importer uses `randomNumber` — `SA:core/logic/get-random-number.md`
- F · If / Else: "matches regex" sets `match.count`, `match[i]` and named groups; "does not exist" / "exists" operators. Variable names may now contain `[ ]` — `SA:core/logic/if-else.md`
- F · Send Local Signal: Include Action's Arguments, Arguments (name=value lines), Queue Signal — `SA:core/signals/send-local-signal.md`
- F · Wait for Local Signal: Overwrite Variables, `signalTimeout`, `hasResult`, `signalName`. Cuebot still continues on timeout (optionally stops) instead of aborting — `SA:core/signals/wait-for-local-signal.md`
- F · Get Action State: always sets `actionState` (the chosen name too) — `SA:core/actions/get-action-state.md`
- F · Range triggers "Always Run": when several triggers of one range type (a numeric min/max setting pair) match, only the narrowest ranges run unless a trigger is marked Always Run (new trigger option in the editor) — `GD:01.core/02.triggers.md`

## 2. Variables and text

Present: 4 (the globals get/set/clear sub-actions); the generic variables, `%date%`/`%time%` formats, number formats,
`$length()$`, `$parse()$` and the Global Variable Viewer.

**Fixed (w14-core)**
- F · `rawInputEscaped`, `rawInputUrlEncoded`, `inputEscaped#`, `input#Escaped`, `inputUrlEncoded#`: the engine adds them to every triggered run that has `rawInput` / `input#` (core commands included; the Twitch, YouTube and Kick mappers already set them) — `TR:core/commands/command-triggered.md`
- F · `%runningActionId%` (same value as `runId`) — `GD:01.core/03.variables.md`
- F · Global Variable Created, Updated, Deleted and User Global Variable Updated: `global.name`, `global.value`/`newValue`/`oldValue`, `global.persisted`, `global.lastWrite`, `global.userId/userType/userLogin/userName`, plus the Persisted (Either/Yes/No), User Login and Platform filters — `TR:core/global-variables/*`

**Partial (not changed)**
- P · `%__source%` / `%eventSource%` are not set as arguments. The Streamer.bot event-name map lives in the scripting module, and the docs say C# is needed to read them anyway; `CPH.GetEventType()`/`GetSource()` give the same values — `GD:01.core/03.variables.md`
- P · `$math()$`: Cuebot's evaluator now takes mXparser 4.4's documented operators (`# ! % ^^`, all boolean, relation and bitwise operators), number literals, constants, random variables and the commonly used unary, binary, 3-argument and variadic functions (w16). Still different: errors are reported instead of `NaN`; no calculus/iterated operators (`sum(i,…)`, `int`, `der`, `solve`), units, physical/astronomical constants or the rarer special functions (`Bell`, `Ei`, `erf`, `LambW0`, …); mXparser itself was not added (since 5.0 it is dual-licensed and commercial use needs a paid licence; the BSD-licensed 4.x line is no longer maintained) — `GD:01.core/03.variables.md`

## 3. Commands, timers, users, points, quotes

Present: 19 (command and command-group state sub-actions, timers, groups, quotes, credits, reset first words/credits,
cron and timed-action triggers, user-group sub-actions); command options (persist counters, ignore bot/internal,
case, cooldowns, permissions).

**Fixed (w14-core)**
- F · Check Points (`points.check`, Enough/Not enough branches, Break on failure default on, `userPoints`) — `SA:core/points/user/check-points.md`
- F · Delete Points (`points.delete`, removes the record) — `SA:core/points/user/delete-points.md`
- F · Get Points Definition (`points.getDefinition`: JSON plus `<var>.name`, `.singularName`, `.symbol`, ...) — `SA:core/points/admin/get-points-definition.md`
- F · Command Triggered: `commandSource`, `commandType`, `msgId`, regex `match.count` / `match[i]`. **Behaviour change:** `rawInput` is now the text after the command, as documented (the whole message stays in `message`) — `TR:core/commands/command-triggered.md`
- F · Command Cooldown: `commandSource`, `commandType`, `cooldownLeft`, `globalCooldownLeft`, `userCooldownLeft` — `TR:core/commands/command-cooldown.md`
- F · Get Commands: Include All, Only Has Permission (checks the run's user), `<var>List`; Ignore Aliases is the existing "First alias" choice — `SA:core/commands/get-commands.md`
- F · Get Command State / Get Command Group State: `commandState`, `commandGroupState`, `commandsEnabled`, `commandsDisabled` — `SA:core/commands/get-command-state.md`, `SA:core/commands/get-command-group-state.md`
- F · Get Points Leaderboard: Offset — `SA:core/points/leaderboards/get-points-leaderboard.md`
- F · Award Points (one user) / Update Points: Apply User/Group Multipliers and a destination argument on Add points (negative amounts update down) — `SA:core/points/user/award-points.md`, `update-points.md`
- F · Award Points to All (`points.awardAll`: every known user, or Only Active) — `SA:core/points/bulk/award-points-to-all.md`
- F · Reset User Points (`points.resetUser`) — `SA:core/points/user/reset-user-points.md`
- F · Group Added, Removed, Cleared, Users Added and Users Removed: `group.id` (the name; Cuebot groups have no separate id), `group.name`, `group.bots` (always false), `group.user.count`, `group.user0.*` (one user per event) — `TR:core/groups/*`
- F · User group sub-actions: `addResult`, `removeResult`, `inGroup`, `foundUser`, `foundUserName`, `foundUserId`, `foundUserType`
- F · First Words (core `users.firstWords`): `firstMessage`
- F · Quote Added / Show: `quoteTime` (2) — `TR:core/quotes/*`
- F · Add Quote / Get Quote / Delete Quote: `quoteTime`, `success` (3) — `SA:core/quotes/*`
- F · Cron trigger (`core.cron`): its own expression, `expression`, `triggeredAt` — `TR:core/schedule/cron.md`

**Partial (not changed)**
- P · Group `isSubscribed` per user is not set (groups do not track it)

## 4. Platforms

Present: 96. Every Twitch, YouTube and Kick trigger and sub-action exists apart from the 5 not-possible triggers;
the gaps are variables and options.

**Variable names: fixed (w14-vars-platforms).** Each module now adds Streamer.bot's documented names next to its own
(nothing was renamed or removed): `Cuebot.Twitch/Compat/StreamerBotVariables.cs`, `Cuebot.YouTube/Compat/...`,
`Cuebot.Kick/Compat/...`, sharing `Cuebot.Core/Variables/StreamerBotArgs.cs` (aliases, `rawInputEscaped`,
`rawInputUrlEncoded`, `input#` / `inputEscaped#` / `inputUrlEncoded#`, `colorR/G/B`, DateTime values). Values that
need memory across events (reward settings, old title/category, raid target, gift-bomb size, deleted message text,
shared chat session) come from the Twitch router and the Kick event hub. Sub-actions write the documented outputs
directly. Tests: `StreamerBotVariableTests` in the three test projects plus `Sub_actions_write_Streamer_bot_output_names`
(Twitch, Kick) and the YouTube ban/timeout end-to-end test. Items below marked ✅ are fixed; what is left is listed
with the reason.

**Not possible (already in PARITY.md)**
- NP · Ad Mid-Roll — `TR:twitch/ads/ad-mid-roll.md`
- NP · Broadcaster PubSub Connected / Disconnected — `TR:twitch/connections/broadcaster-pubsub-*.md`
- NP · Community Goal Contribution / Ended — `TR:twitch/community-goal/*`

**Twitch triggers**
- ✅ Reward Redemption: `rewardName`, `rawInput`, `rawInputEscaped`, `rawInputUrlEncoded`, `skipsQueue`, `requiresUserInput`, `maxPerStream`, `maxPerUserPerStream`, `globalCooldown`, `backgroundColor` (the reward settings come from the reward list read at connect and from reward create/update events) — `TR:twitch/channel-reward/reward-redemption.md`
- ✅ Reward Redemption Updated: `rewardName`, `rewardStatus` — `TR:twitch/channel-reward/reward-redemption-updated.md`
- ✅ Automatic Reward Redemption: `unlockedEmote*`, `gigantifiedEmote*` — `TR:twitch/channel-reward/automatic-reward-redemption.md`
- P · Subscription: `isMultiMonth`, `multiMonthDuration`, `multiMonthTenure` (EventSub `channel.subscribe` carries no duration or tenure) — `TR:twitch/subscriptions/subscription.md`
- ✅ Resubscription: `cumulative`, `monthStreak`, `isMultiMonth`, `multiMonthDuration`, `streakShared`. Still missing: `multiMonthTenure` (not in EventSub) — `TR:twitch/subscriptions/resubscription.md`
- ✅ Gift Subscription (Cuebot `twitch.subGift`): `recipientId`, `totalSubsGifted`, `monthsGifted`, `fromGiftBomb`, `subBombCount`, `random`, `totalSubsGiftedShared`, `anonymous`. Still missing: the recipient's `cumulativeMonths` (not in EventSub). Note: the importer maps Streamer.bot's Gift Subscription trigger to `twitch.giftSub` (the gifter's total), which carries `gifts`/`totalGifts`/`anonymous` but not the recipient names — `TR:twitch/subscriptions/gift-subscription.md`
- ✅ Gift Bomb: `gifts`, `totalGifts`, `totalGiftsShared`, `anonymous`. Still missing: `gift.recipientUser#`/`UserName#`/`Id#` (the recipients arrive as separate notifications after the bomb) and `bonusGifts` (not in EventSub) — `TR:twitch/subscriptions/gift-bomb.md`
- ✅ Prime Paid Upgrade: `upgradeTier`, `upgradeTierString` ("tier 1"; the docs do not show the exact text) — `TR:twitch/subscriptions/prime-paid-upgrade.md`
- ✅ Poll Created / Updated / Completed / Terminated / Archived: `poll.Title`, `poll.Id`, `poll.StartedAt`, `poll.Duration`, `poll.DurationRemaining`, `poll.choices.count`, `poll.choice#.*`, `poll.votes`/`rewardVotes`/`totalVotes`, `poll.endedAt`, `poll.winningIndex`, `poll.winningChoice.*`, `poll._json` (5) — `TR:twitch/polls/*`
- ✅ Prediction Created / Updated / Locked / Completed / Canceled: `prediction.*`, `prediction.outcome#.*`, `prediction.winningOutcome.*`, `prediction._json` (5) — `TR:twitch/predictions/*`
- ✅ Charity Started / Progress / Donation / Completed: `charity.*` with `.value`, `.valueMinor` and `.currency`, `charity.logo`, `charity.startedAt`/`stoppedAt` (4) — `TR:twitch/charity/*`
- ✅ Goal Begin / Progress / End: `goal.*` (3) — `TR:twitch/channel-goal/*`
- ✅ Hype Train Start / Update / Level Up / End: `percentDecimal`, `isGoldenKappaTrain`, `isTreasureTrain`, `isInSharedChat`, `isSharedChatHost` (from the shared chat session), `top.bits.*`, `top.subscription.*`, `top.other.*`, `allTimeHighTotal`, `prevLevel`, `id` (4) — `TR:twitch/hype-train/*`
- ✅ User Banned / Timed Out / Unbanned / Untimed Out, plus the 4 shared-chat versions: `createdById`, `createdByUsername`, `createdByDisplayName`, `createdAt` (8) — `TR:twitch/moderation/user-*.md`, `TR:twitch/shared-chat/user-*.md`
- ✅ Raid Start / Send / Cancelled: `raidUser`, `raidUserName`, `raidUserId`, `raidUserProfileImageURL`, `raidUserProfileImageEscaped` (on Send and Cancelled the profile image and the cancelled target are known only for raids started from Cuebot) (3) — `TR:twitch/raid/*`
- ✅ Pyramid Success / Broken: `totalPyramids`, `totalPyramidsBroken` (per session), `pyramidWidth`, `pyramidEmote`, `pyramidOwner*` (2) — `TR:twitch/pyramid/*`
- ✅ Stream Update: `status`, `gameUpdate`, `statusUpdate`, `oldStatus`, `oldGameId`, `oldGameName` (compared with the channel read at connect). Still missing: `gameBoxArt`, `oldGameBoxArt` (needs a category lookup per update) — `TR:twitch/general/stream-update.md`
- P · Stream Online: `game`, `gameId`, `tagCount`, `tag#`, `tags`, `tagsDelimited` (EventSub `stream.online` has none of these; needs a Helix channel read) — `TR:twitch/channel/stream-online.md`
- ✅ Stream Offline: `endedAt` — `TR:twitch/channel/stream-offline.md`
- ✅ Ad Run: `adLength`, `adLengthMs`, `adScheduled` — `TR:twitch/ads/ad-run.md`
- ✅ Announcement / Shared Announcement: `announceColor`, `fromSharedChat`, `messageStripped` (2) — `TR:twitch/general/announcement.md`, `TR:twitch/shared-chat/announcement.md`
- ✅ Blocked / Permitted Terms Added / Deleted: `moderatorUsername`, `moderatorDisplayName`, `moderatorId`, `term.#`, `createdAt` (4) — `TR:twitch/moderation/*-terms-*.md`
- ✅ Shoutout Created: `targetUserId`, `targetUserLogin`, `targetUserDisplayName`, `viewerCount`. Still missing: `shoutoutId` (EventSub sends no id) — `TR:twitch/moderation/shoutout-created.md`
- ✅ Shoutout Received: `viewerCount` — `TR:twitch/general/shoutout-received.md`
- ✅ Warned User / Suspicious User Update: `moderator.userName`, `moderator.login`, `moderator.id` (2) — `TR:twitch/moderation/warned-user.md`, `suspicious-user-update.md`
- ✅ Chat Message Deleted / Shared: `targetMessageId`, `message` (the text of a message Cuebot saw recently) (2) — `TR:twitch/moderation/chat-message-deleted.md`, `TR:twitch/shared-chat/message-deleted.md`
- ✅ Shared Chat Session Begin / Update / End: `host.userId`, `host.userLogin`, `host.userName`. Still missing: `host.isSubscribed`, `host.isModerator`, `host.isVip` (not in EventSub) (3) — `TR:twitch/shared-chat/session-*.md`
- ✅ Shared Resub / Sub Gift / Community Sub Gift: the same names as their non-shared versions; `bonusGifts` still missing (3) — `TR:twitch/shared-chat/*`
- ✅ Subscriber / Unique Mode On/Off: `subscriberOnly`, `uniqueMode` (4) — `TR:twitch/chat/mode/*`
- ✅ Watch Streak: `watchStreakId` (the notification's message id; EventSub has no separate id) — `TR:twitch/chat/watch-streak.md`
- ✅ Guest Star Guest Update: `moderatorUserLogin`, `guestUser*`. Different: Cuebot's `moderatorUserName` is the login, Streamer.bot's is the display name (kept for existing actions) — `TR:twitch/guest-star/guest-update.md`
- ✅ Unban Request Created: `text`; AutoMod Message Held / Updated: `rawInput`, `input#` — `TR:twitch/moderation/*`
- P · BetterTTV / SevenTV Emote Added/Removed: `imageUrl` is present (Core's emote events carry it); Cuebot still detects these only when the emote cache refreshes, not in real time (4) — `TR:twitch/emotes/*`
- P · First Words: `firstMessage` — the trigger is Core's `users.firstWords`, outside the Twitch module — `TR:twitch/chat/first-words.md`
- P · Present Viewers: `isTest` added. Different: `users` is Cuebot's comma separated logins, not Streamer.bot's list of dictionaries (same name, kept); `isLive` missing — `TR:twitch/general/present-viewers.md`
- Common variables, now checked against `3.api/.variables/twitch/*.md`:
  - ✅ `TwitchBroadcaster`: `broadcastUser`, `broadcastUserName`, `broadcastUserId` on every event; `broadcastIsAffiliate`/`broadcastIsPartner` once the broadcaster was read at connect.
  - ✅ `TwitchUser`: `userType`, `isSubscribed`, `badgeCount`. Still missing: `subscriptionTier`, `userGroups`, `userPreviousActive` (user database); `badges` is Cuebot's "set/id" string, not a list.
  - ✅ `TwitchChat`: `msgId`, `role`, `colorR/G/B`, `messageStripped`, `messageCheermotesStripped`, `isHighlight`, `reply.*`, `rawInput`, `rawInputEscaped`, `rawInputUrlEncoded`, `input#`, `inputEscaped#`, `inputUrlEncoded#`, `fromSharedChat`. Still missing: `isAction` (EventSub does not flag /me), `inSharedChat`, `firstMessage` (Core); `emotes` is Cuebot's IRC-style string, not a list.
  - ✅ `TwitchSharedChatSource`: `source.userId`, `source.userLogin`, `source.userName`. Still missing: `source.isSubscribed`, `source.isModerator`, `source.isVip`.

**Twitch sub-actions**
- P · Set Background Color: no Random Color — `SA:twitch/rewards/set-background-color.md`
- P · Set Max Per Stream / Set Max per User Per Stream: no Additive (2) — `SA:twitch/rewards/set-max-per-stream.md`, `set-max-per-user-per-stream.md`
- ✅ Create Clip: `createClipSuccess`, `createClipId`, `createClipCreatedAt`, `createClipUrl` — `SA:twitch/channel/create-clip.md`
- ✅ Create Stream Marker: `streamMarker*` — `SA:twitch/channel/create-stream-marker.md`
- ✅ Get Follow Age Info: `followDate`, `followAgeLong`, `followAgeShort`, `followAgeMinutes`, `followAgeSeconds`, `followUser*`, `followAge.*`. Different: `followAgeDays` stays Cuebot's number (the docs show a formatted string "4,149") — `SA:twitch/followers/get-follow-age-info-for-target.md`
- ✅ Get User Info for Target: `addTargetResult`, `targetDescriptionEscaped`, `targetUserProfileImageUrl` (+Escaped), `targetUserType`, `targetIsAffiliate`, `targetIsPartner`, `targetIsSubscribed`, `targetSubscriptionTier`, `targetIsModerator`, `targetIsVip` (each when the scope is granted), `targetChannelTitle`, `game`, `gameId`, `createdAt`, `accountAge`, `tagCount`, `tag#`, `tags`, `tagsDelimited`. Still missing: `targetLastActive`, `targetPreviousActive` (user database) — `SA:twitch/user/get-user-info-for-target.md`
- ✅ Add Broadcaster Information: `broadcastUser*`, `broadcastIsAffiliate`, `broadcastIsPartner`, `broadcasterProfileUrl`, `broadcasterOfflineUrl`, `broadcasterCreatedAt`, `broadcasterDescription` — `SA:twitch/user/add-broadcaster-information.md`
- ✅ Add Random Users: `randomUserType#` — `SA:twitch/user/add-random-users.md`
- ✅ Ban / Timeout / Unban / UnTimeout / Warn User: `bannedUser*`/`banResult`, `timedOutUser*`/`timeoutResult`, `unbannedUser*`/`unbanResult`, `unTimedOutUser*`/`unTimedOutResult`, `warnedUser*`/`warnedResult` (the names are empty when the user is given only as `id:` and is not the triggering user; a failure stops the action rather than setting `false`) (5) — `SA:twitch/moderation/*`
- ✅ Send Shoutout / Start Raid / Clear Chat Messages / Set Channel Title / Set Channel Game / Set Branded Content: `shoutoutSuccess`, `startRaidSuccess`, `clearSuccess`, `titleSuccess`, `gameSuccess`/`gameBoxArt`, `success` (6)
- ✅ Get Ad Schedule / Get Pre-roll Free Time / Snooze Ad: `prerollFree`, `prerollFreeSeconds`, `snooze.success` (Cuebot's Get Ad Schedule covers Get Pre-roll Free Time) (3) — `SA:twitch/ads/*`
- ✅ Create Poll / Get Active Poll: `success`, `active`, `poll.*` (2) — `SA:twitch/polls/*`
- ✅ Create / Get Active / Lock / Cancel / Resolve Last / Resolve Last by Title prediction: `success`, `active`, `locked`, `cancelled`, `prediction.*`, `prediction.winningIndex`, `prediction.winningOutcome.*` (and the docs' `prediction.winningCutcome.title` spelling) (6) — `SA:twitch/predictions/*`
- ✅ Get Reward Info: `rewardSkipRequestQueue`, `rewardMaxPerUserPerStream`, `rewardGlobalCooldown`, `rewardIsOurs` — `SA:twitch/rewards/get-reward-info.md`
- P · Add User To Group / Remove User From Group / User in Group: `addResult`, `removeResult`, `foundUser*` — these are Core's shared `groups.*` sub-actions, outside the Twitch module (3) — `SA:twitch/user-groups/*`

**YouTube**
- ✅ Super Chat / Super Sticker: `microAmount`, `currencyCode`. Still missing: `stickerImageUrl` (the Data API sends no image). Different: `amount` stays Cuebot's number, Streamer.bot's is the display text (Cuebot `amountText`) (2) — `TR:youtube/chat/super-chat.md`, `super-sticker.md`
- ✅ Jewels Gifted: `gift.jewelsAmount`, `gift.name`, `gift.comboCount`, `gift.isCombo`; `gift.url`, `gift.durationInSeconds`, `gift.durationInNanoSeconds`, `gift.hasVisualEffect`, `gift.altText`, `gift.altTextLanguage` when the gift details carry them — `TR:youtube/chat/jewels-gifted.md`
- P · Message: `parts`, `emotes` (the Data API sends no message parts) — `TR:youtube/chat/message.md`
- ✅ First Words / Message: `rawInputEscaped`, `rawInputUrlEncoded`, `input#`, `input#Escaped` (and `inputEscaped#`) on `youtube.chatMessage`; First Words itself is Core's `users.firstWords` — `TR:youtube/chat/first-words.md`
- ✅ Gift Membership Received: `id`, `tier`, `gifterUser*`, `gifterUserType`, `gifterProfileUrl`, `gifterIsSponsor` (the last two when the gifter chatted recently) — `TR:youtube/membership/gift-membership-received.md`
- ✅ Membership Gift: `count`, `tier`, `id` — `TR:youtube/membership/gift.md`
- ✅ Poll Closed (and Started/Updated): `poll.question`, `poll.totalVotes`, `poll.options.count`, `poll.option#.*`, `poll.winningOption.*` — `TR:youtube/polls/poll-closed.md`
- ✅ User Banned / Timed Out: `banDuration` (2) — `TR:youtube/general/user-banned.md`, `user-timed-out.md`
- ✅ Statistics Updated: `dislikeCount`, `favoriteCount` (always 0: the Data API no longer reports them) — `TR:youtube/broadcast/statistics-updated.md`
- P · Present Viewers: `isTest`, `isLive`, `users` (the YouTube module has no present-viewers trigger) — `TR:youtube/general/present-viewers.md`
- ✅ Common variables: `YouTubeUser` `userType`, `userIsSponsor`; `YouTubeBroadcaster` `broadcastUsername`, `broadcastUserId`, `broadcastUserProfileImage`; `YouTubeBroadcast` `broadcast.id`, `.title`, `.description`, `.privacy`, `.status`. Still missing: `isSubscribed`, `userPreviousActive`, `broadcast.publishedAt`, `.defaultLanguage`, `.defaultAudioLanguage`, `.tags` (not in Cuebot's broadcast model).
- ✅ Ban / Timeout User sub-actions: `bannedUser*`, `banResult`, `timeoutResult` (2) — `SA:youtube/moderation/*`

**Kick**
- ✅ Reward Redemption: `reward.id`, `reward.title`, `reward.cost`, `reward.description`, `reward.backgroundColor`, `reward.skipsQueue`, `reward.requiresUserInput` (the settings from the reward list read at connect), `rawInputEscaped`, `rawInputUrlEncoded` — `TR:kick/channel-reward/reward-redemption.md`
- ✅ Kicks Gifted: `kicks.amount`, `kicks.name`, `kicks.type`, `kicks.tier`, `kicks.pinnedTimeSeconds`, `kicks.createdAt` (webhook), `input.count`, `input#` variants — `TR:kick/kicks/gifted.md`
- ✅ Channel Update: `categoryUpdated`, `categoryName`, `categoryThumbnail`, `oldCategory*`, `statusUpdated`, `status`, `oldStatus` (compared with the channel read at connect) — `TR:kick/general/channel-update.md`
- ✅ Stream Online / Offline: `category.name`, `category.id`, `category.thumbnail` (last known category); Offline gets `startedAt`, `title`, `endedAt` — `TR:kick/channel/stream-online.md`, `stream-offline.md`
- ✅ Subscription / Resubscription: `monthsSubscribed`, `duration`, `expiresAt` (webhook). Still missing: `badgeCount`, `badge` (subscription events carry no badges) (2) — `TR:kick/subscriptions/subscription.md`, `resubscription.md`
- ✅ Gift / Mass Gift Subscription: `recipient.*` / `recipient.#.*`, `count`, `subscribedAt` and `expiresAt` (webhook only) (2) — `TR:kick/subscriptions/gift-subscription.md`, `mass-gift-subscription.md`
- ✅ User Banned / Timed Out: `createdAt`, `createdBy*` (2) — `TR:kick/moderation/*`
- ✅ Chat Message: `isInternal` (messages Cuebot sent); `KickChat` `messageStripped`, `colorR/G/B`, `emotes.#.*`, `badgeCount`, `badge`, `badge.#.*`, `rawInput*`, `input.count`, `input#` variants; `KickUser` `userType`, `isSubscribed`; `KickBroadcaster` `broadcastUser`, `broadcastUsername`, `broadcastUserId` — `TR:kick/chat/message.md`
- P · First Words: `firstMessage` (Core's `users.firstWords`); Present Viewers: `isTest`, `isLive`, `users`, `categoryName`, `categoryThumbnail` (the Kick module has no present-viewers trigger); `KickBroadcaster` `broadcastUserProfileImage`, `KickUser` `userGroups`/`userPreviousActive` (2) — `TR:kick/chat/first-words.md`, `TR:kick/general/present-viewers.md`
- ✅ Ban / Timeout / Unban / UnTimeout User: `*User*`/`*Result` outputs (4) — `SA:kick/moderation/*`
- ✅ Set Channel Title / Category: `titleSuccess`, `category.success`/`id`/`name`/`thumbnail` (2) — `SA:kick/channel/*`
- ✅ Add Broadcaster Information / Add Random Users / Get User Info for Target: `broadcastUserType`, `broadcasterProfileUrl` (best effort), `randomUserType#`, `targetDescriptionEscaped`, `targetUserProfileImageUrl` (+Escaped), `targetUserType` (3) — `SA:kick/user/*`
- ✅ Add/Remove User To/From Group / User in Group: `addResult`, `removeResult`, `inGroup`, `foundUser*` (3) — `SA:kick/user-groups/*`

## 5. Stream apps and integrations

Present: about 291 (OBS 9/9 triggers and all sub-actions; Meld Studio; VTube Studio; Voicemod; T.I.T.S.; Lumia;
mtion; PolyPop; Stream Deck; Control Center; Wave Link 1.x and 3; Ko-fi, Patreon, Shopify, Throne, Pally.gg,
TipeeeStream, TreatStream, DonorDrive, StreamLoots, StreamElements, Streamlabs, HypeRate; Speaker.bot triggers N/A).

**Status (branch `w14-apps`).** Items below marked **Fixed** now produce Streamer.bot's names alongside Cuebot's
(nothing was renamed or removed) and are covered by tests with fakes. **Already present** means the reviewed commit
had it (the static check missed it) and a test now pins it. Stream Deck MCP is out of scope for this pass; Decks were done in `w14-core`.

**Missing**
- F (w14-core) · Streamer.bot Decks: Cuebot Decks, served by the HTTP server at `/deck/<id>` with live updates; the 10 sub-actions (Deck Notify, Item Set Background/Icon/State/Title/Value, Item Toggle State, Page Next/Previous/Set), a "Deck item pressed" trigger and a Decks page. Not the streamer.bot website: no Discord login, private/public sharing or file uploads — `SA:streamerbot/decks/*`, `GD:04.extra-features/2.streamerbot-decks.md`, `WK:Extended-Features/HTML-Decks.md`
- ~~M · IFTTT Notification (Basic) / (Advanced) (2)~~ — **Fixed**: `ifttt.notificationBasic` / `ifttt.notificationAdvanced` (Cuebot.Webhooks). Streamer.bot routes these through its own website service; Cuebot posts to IFTTT's Webhooks service (`maker.ifttt.com/trigger/{event}[/json]/with/key/{key}`), so applets use the "Webhooks → Receive a web request" trigger. Basic sends `value1`; Advanced sends `Argument1Name`/`Argument1Value` … 5, `ArgumentsJson` and `value1`–`value3`. Key from the sub-action or `webhooks.json` `IftttKey` — `SA:integrations/ifttt/*`, `GD:06.integrations/ifttt.md`
- ~~M · Streamlabs Desktop: Set State of a Random Filter, Hide Group's Sources, Set Random Group Source Visible, Set Scene Filter State, Hide Source's Filters (5)~~ — **Fixed**: `slobs.setRandomFilterState` (stores `slobsFilterName`), `slobs.hideGroupSources`, `slobs.randomGroupSource` (stores `randomSource`), `slobs.setSceneFilterState`, `slobs.hideSourceFilters`, through `SourceFiltersService.getFilters`/`setVisibility` and scene folders (Streamlabs Desktop's groups) — `SA:streamlabs-desktop/filters/*`, `groups/*`, `scenes/set-scene-filter-state.md`, `sources/hide-sources-filters.md`
- M · Elgato Camera Hub webcam: Set Dynamic Range, Set Noise Reduction, Set White Balance Tint (3) — **Not possible through UVC**: standard UVC / V4L2 has no HDR, noise-reduction or tint control (the nearest, white balance component red/blue, is a different control, and Cuebot's V4L2 backend only maps the standard controls). On Elgato cameras these would be vendor extension-unit controls with no public documentation, so they can only be reached through Camera Hub, which has no API (see the Not possible list) — `SA:elgato/camera-hub/**`
- M · Stream Deck MCP: `StreamDeckMcpConnected`/`Disconnected` and MCP actions (PARITY.md: "not yet") — `CS:5.enums/event-type.md` — *open (skipped in this pass)*

**Partial**
- ~~P · Discord Basic Webhook~~ — **Fixed**: `discord.basicWebhook` sub-action (Cuebot.Webhooks) with the same payload as `CPH.DiscordPostTextToWebhook` (content, tts, username, avatar_url), sent with `wait=true`; stores `messageId` — `SA:integrations/discord/basic-webhook.md`, `GD:06.integrations/discord.md`
- ~~P · Streamlabs Desktop triggers (7): `sd.id`, `sd.name`, `sd.host`, `sd.sceneName`~~ — **Fixed** (every Streamlabs Desktop event; Cuebot has one connection, so `sd.id` = `main`, `sd.name` = `Main`) — `TR:streamlabs-desktop/*`
- ~~P · Streamlabs Desktop Get Current Scene: `currentScene`~~ — **Fixed** — `SA:streamlabs-desktop/scenes/get-current-scene.md`
- ~~P · OBS Get Scene Item Properties: `props.*`~~ — **Fixed**: `props.*` incl. `props.groupChildren[#].*` and `props._json`; `props.scale.filter` is not produced (obs-websocket 5 does not expose it) and `props.message-id` is not produced (no Cuebot equivalent) — `SA:obs-studio/scenes/get-scene-item-properties.md`
- ~~P · OBS Get Current Scene / Create Record Chapter: `currentScene`, `success` (2)~~ — **Fixed**; Create Record Chapter now sets `success=false` and carries on when OBS refuses (like Streamer.bot); a lost connection still fails the action — `SA:obs-studio/get-current-scene.md`, `SA:obs-studio/create-record-chapter.md`
- ~~P · Crowd Control … `effect.*`, `game.*`, `gamePack.*` (6), Coin Exchange `exchangeSource` (1)~~ — **Fixed**: plus `requestID`, `requester.*`, `target.*`, `effect.category[#]` and timed `status`; session events also get `game.*`/`gamePack.*`. `timestamp` keeps Cuebot's ISO time — `TR:integrations/crowdcontrol/*`
- ~~P · Fourthwall … (7)~~ — **Fixed**: `fw.isTest`, `fw.update`, `fw.productId`, `fw.images[#]`, gift `fw.donation`/`fw.discount`, `fw.variants[#].image`/`stock`/`stockType`/`attributes.*` (incl. `weight`) for orders and products. Totals, `fw.gifts[n].*` and order variant `quantity` were already present — `TR:integrations/fourthwall/*`
- ~~P · Patreon Pledge Updated: `entitledTiers[#].updatedToTier`~~ — **Fixed** (the tier's title, else its id) — `TR:integrations/patreon/pledge-updated.md`
- ~~P · Pulsoid Heart Rate Pulse: `measuredAt`~~ — **Fixed** (Unix seconds from Pulsoid's `measured_at` / `timestamp`, else the time received; HypeRate gets it too) — `TR:integrations/pulsoid/heart-rate-pulse.md`
- ~~P · Streamlabs Merchandise: `merchandiseImageUrl`, `merchandiseImageEscaped`~~ — **Already present** (`StreamlabsEventMapper.Merch`, tested in `StreamlabsMappingTests`) — `TR:integrations/streamlabs/merchandise.md`
- ~~P · Voicemod Soundboard Changed / Voice Loaded: `soundboard.*`, `voice.*`; Get Current Voice: `currentVoiceId` (3)~~ — **Fixed** — `TR:integrations/voicemod/*`, `SA:integrations/voicemod/get-current-voice.md`
- ~~P · VTube Studio Model Clicked / Item Event / Get Current Model / Send Raw Request (4)~~ — **Fixed**: `clickPosition.X/Y`, `windowSize.X/Y`, `position.x/y`, `model.*`, and Send Raw Request's flattened `<prefix>.data.*` (new "Variable prefix" parameter, default `vtsRaw`) — `TR:integrations/vtube-studio/*`, `SA:integrations/vtube-studio/*`
- ~~P · Camera Hub webcam triggers: `old*` previous values (7)~~ — **Already present** (`CameraHubService.Diff`); now pinned by a test — `TR:elgato/camera-hub/webcam/*`
- ~~P · Wave Link 3 Get Channel Information: `waveLinkChannelMix.#.*`~~ — **Already present** (`WaveLinkArgs`); now pinned by a test — `SA:elgato/wave-link-3/channels/get-channel-information.md`

**Not possible**
- NP · Camera Hub Prompter and EpocCam: 44 triggers and 20 sub-actions (Camera Hub app has no public API; PARITY.md agrees) — `TR:elgato/camera-hub/**`, `SA:elgato/camera-hub/**`
- NP · Elgato Studio: 9 triggers, 9 sub-actions — `TR:elgato/studio/*`, `SA:elgato/studio/**`
- NP · StreamFog: 10 distinct sub-actions (lenses, outfits, reset look, video backgrounds) — `SA:integrations/streamfog/**`, `SA:streamfog/**`
- NP · Lumia Set Color "Skip Queue" — `SA:integrations/lumia-stream/set-color.md`
- NP · Voicemod Set Censor State (removed in Voicemod V3) — `SA:integrations/voicemod/set-censor-state.md`

## 6. System, input and services

Present: 24 (MIDI, voice control, keyboard press, toast, clipboard, file exists/folder, write file, file tail state,
stop sounds, hotkeys, watchers, MIDI and voice triggers, custom WebSocket triggers).

**Fixed (w14-core)**
- F · Read Specific Line From File (`core.readSpecificLine`: `line`, `fileFound`, Parse Variables, auto-typing) — `SA:core/file-io/read-specific-line-from-file.md`
- F · UDP Broadcast (`core.udpBroadcast`; address defaults to 255.255.255.255, 127.0.0.1 for this computer; `udpBytesSent`) — `SA:core/network/udp-broadcast.md`
- F · Read Lines From File (`core.readLines`: `line#`, `lineCount`, `fileFound`, Parse Variables, auto-typing; Cuebot's whole-file "Read file" stays) — `SA:core/file-io/read-lines-from-file.md`
- F · Read Random Line From File: Count, `randomLine#`, `randomLineNumber#` (from 0), `fileFound`, Parse Variables, auto-typing — `SA:core/file-io/read-random-line-from-file.md`
- F · Play Sound: `soundLength`, `soundLengthMs`, Use Filename As Name — `SA:core/sounds/play-sound.md`
- F · Play Sound From Folder: `randomSoundFile`, `randomSoundFileName`, `soundLength*` — `SA:core/sounds/play-sound-from-folder.md`
- F · Run a Program: environment variables, wait limit in seconds, `outputCount` / `output#` (from 1) and `error#` capture, `timedOut` — `SA:core/system/run-a-program.md`
- F · Fetch URL: "Parse result as JSON" (`<var>.<key>`, arrays as `<var>.items[0]`) — `SA:core/network/fetch-url.md`
- F · Key Press trigger (Cuebot hotkeys): `key`, `modifier`, `hasCtrl`, `hasAlt`, `hasShift` — `TR:core/inputs/key-press.md`
- F · Mouse Click trigger: `mouse.button`, `mouse.clicks` (1 per event), `mouse.x`, `mouse.y` — `TR:core/inputs/mouse-click.md`
- F · Process Started / Stopped: `name`, `executable`, `path` and `fullPath` (Started only, read while the process runs) — `TR:core/processes/*`
- F · File/Folder Watcher: `watcherFolder`, `watcherFilter`, `fullPath`, `oldFullPath`; Changed also `fileSize`, `empty`, `lineCount`, `line#`, `lineEscaped#` (files up to 256 KB) — `TR:core/file-folder-watcher/*`
- F · Clipboard Changed: `clipboard.text`, `containsText`, `containsImage`/`containsAudio` (false), `formats.count`, `formats.0` — `TR:core/system/clipboard-changed.md`
- F · Toast Activation: `toast.id`, `toast.title`, `toast.message`, `toast.iconPath`, `toast.localIconPath` — `TR:core/system/toast-activation.md`

**Partial (not changed)**
- P · Process `parentProcessId`: needs per-OS process APIs (WMI on Windows, /proc on Linux); not added.
- P · Clipboard images and audio: Cuebot's clipboard watcher reads text only.
- P · Renamed: Streamer.bot spells `oldFilename`; Cuebot's `oldFileName` answers it, because argument names are case-insensitive.

## 7. Servers, APIs and scripting

Present: 141 before `w14-cph` (102 of 202 C# method names; 13 classes/enums; all 22 WebSocket requests; 6 HTTP
requests plus serving files; UDP DoAction; Execute C# Code basics; `Init()`/`Dispose()` lifecycle; custom WebSocket
servers and clients). `w14-cph` closes the C# items below; details are in docs/SERVERS.md ("C# CPH compatibility").

**C# methods (fixed in `w14-cph`: all 202 documented names exist)**
- ✅ The 100 names that were missing now exist with the documented signatures and return types. A test checks
  every documented name (`tests/Cuebot.Scripting.Tests/Data/cph-methods.txt`). The groups were:
  - Commands (11): cooldowns follow Streamer.bot: Reset restarts at full length, Remove ends, AddTo adds seconds.
    `CommandResetGlobalCooldown` and `CommandResetAllUserCooldowns` used to clear cooldowns and now restart them.
  - Core (1): `Parse`.
  - Twitch rewards (17), chat (8), moderation (9), clips (5), mods/VIPs (2), user variables (3).
  - Kick (11), YouTube (18), Meld Studio (8), OBS filters and record chapter (7).
- P · Stubs that compile, log "not supported in Cuebot" and return the failure value:
  `TwitchAddActiveMonitoringSuspiciousStatus`, `TwitchAddRestrictedSuspiciousStatus` and
  `TwitchRemoveSuspiciousStatus` (Cuebot does not request `moderator:manage:suspicious_users`), and
  `TwitchGetChannelPointsUsedByUserId` (Cuebot does not track points spent; returns 0).
- Also added, beyond the 202: `UnPauseReward`, `GetCheermotes`, `TwitchBlockUser(ById)` (stubs) and older
  platform-less group overloads. `ResumeAllActionQueues(clear)` and `DiscordPostTextToWebhook` (returns the
  message id) now match the docs. 135 of the 141 `CPHInline` examples in the docs compile; the other 6 have bugs
  in the examples themselves (undefined variables, a syntax error).
- ~~Still missing, not in the 202-name reference: older wiki-era methods such as `TwitchPollCreate`,
  `TwitchPredictionCreate`, `AddQuoteForTwitch`, `ShowToastNotification`, `ObsSetColorSourceColor`, the
  `StreamDeck*` methods, the Guest Star methods and `GetTeamInfoById/ByLogin`.~~ **Fixed in `w15-cph`**: the 284
  methods named only on sub-action pages (`csharpMethods`), in changelogs or in the wiki now exist (245 work, 39 are
  stubs: Trovo, VStream, Twitch unblock, Bits per user, game by id, OBS/Streamlabs disconnect, Streamlabs recording
  pause). Tests check every name (`Data/cph-methods-more.txt`) and compile a call for each of the 582 published
  signatures (`Data/cph-signatures.txt`), which also added missing overloads (`SendYouTubeMessage(message, useBot,
  broadcastId)`, `TriggerCodeEvent(name, json)`, int-id group methods, ...). The Points methods have no published
  signatures; theirs follow the sub-action pages (definition, user name, platform). `GetTeamInfo*` fills team names
  only.
- Note: overloads follow the docs, the wiki and the changelogs. Where they give no signature, extra optional
  parameters and bool returns were chosen, so calls written either way compile.

**C# classes and enums (fixed in `w14-cph`)**
- ✅ All 17 classes and 3 enums: `Cheermote`, `CheermoteImage`, `CheermoteImages`, `CheermoteTier`,
  `CommandCounter`, `GuestSession`, `GuestStar`, `GuestStarInvite`, `GuestStarSettings`, `KickCategory`,
  `MediaSettings`, `QuoteData`, `TeamInfo`, `TwitchReward`, `TwitchRewardCounter`, `VTSModelPosition`,
  `YouTubeUserInfo`; `StreamlabsDesktopOutputType`, `TwitchBlockContext`, `TwitchBlockReason`. Members follow the
  shapes in Streamer.bot's changelogs and wiki.
- ✅ `EventType` has all 508 documented values and `EventSource` all 46. Earlier Cuebot names (`MiscTimedAction`,
  ...) remain as aliases. Numeric values are not documented, so only the names match.

**Other C# gaps (fixed in `w14-cph`)**
- ✅ Execute C# Method sub-action (`code.csharpMethod`, with Save Result to Variable); `CPH.ExecuteMethod` resolves
  the importer's calls.
- ✅ Newtonsoft.Json 13 is bundled and referenced in both script shapes.
- ✅ Custom references: a References list per C# step plus `scripting.json` `CSharpReferences`. Only listed full
  paths are loaded; nothing is fetched. "Find Refs" is not needed, because every framework assembly is referenced.
- ✅ Precompile on Application Start / Delayed Start (enabled actions only; `Init()` runs at startup).
- ✅ Execute C# Code: Save Result to Variable and a Description field. The importer carries precompile, delayed
  start and save-result over.
- P · Windows Forms and other .NET Framework-only APIs are not available (Cuebot targets .NET 10).

**WebSocket events** (fixed in `w14-websocket`; names checked against the GetEvents list published with the official
client, `github.com/Streamerbot/client` `events.const.ts`, plus `CS:5.enums/event-type.md`)
- ✅ · `Raw.Action` and `Raw.SubAction` are emitted (only while a client subscribes to them), with the documented
  fields (`id` = run, `actionId`, `name`, `arguments`; sub-actions add `parentId`, `parentName`, `type`).
  `Raw.ActionCompleted` now has Streamer.bot's fields (`id`, `actionId`, `name`, `duration`, `queuedAt`, `startedAt`,
  `completedAt`, `success`, `excludeFromHistory`, `arguments`) next to Cuebot's. `Misc.StreamerbotExiting` is sent on
  shutdown; `Application.Action*` carry `group` and `enabled` — `WK:Servers-Clients/WebSocket-Server/Events/raw/*.md`
- ✅ · Event names now match Streamer.bot's (`StreamerBotEventMap`). The renames listed in this review are done:
  - Twitch chat modes → `ChatEmoteModeOn/Off`, `ChatFollowerMode*`, `ChatSlowMode*`, `ChatSubscriberMode*`, `ChatUniqueMode*`
  - `SharedChatSessionBegin/End/Update`, `SharedChatCommunitySubGift`, `SharedModiversary`, `SharedChatUserTimedout/Untimedout`
  - `WarnedUser`, `PyramidSuccess`, `AutoModMessageHeld/Update`, `BroadcasterEventSubConnected/Disconnected`,
    `BotEventSubConnected/Disconnected`. `PowerUp` stays: the GetEvents list uses `PowerUp` (the enum's
    `PowerUpRedemption` is the C# name).
  - Gifts: `twitch.subGift` (one per recipient) is `Twitch.GiftSub`, as in Streamer.bot; EventSub's aggregate
    `twitch.giftSub` is sent as `Twitch.GiftSubscriptions` (a Cuebot addition) so it no longer doubles `GiftSub`/`GiftBomb`.
  - YouTube `StatisticsUpdated`, `NewSponsorOnlyStarted/Ended`; Kick `BroadcasterChatConnected/Disconnected`, `SevenTVEmoteAdded/Removed`
  - `StreamlabsDesktop.*` (`StreamingStarted/Stopped`, `RecordingStarted/Stopped`), `ThrowingSystem.*`, `Pallygg.*`,
    `Quote.Added/Show`, `Pulsoid.HeartRatePulse`, `HypeRate.HeartRatePulse/TwitchClipCreated`, `Streamlabs.Merchandise`,
    `DonorDrive`, `TipeeeStream`, `TreatStream`, `StreamLoots`, `CrowdControl`, `MeldStudio` source spellings
  - `System.ClipboardChanged`, `Misc.ToastActivation`, `Inputs.InputKeyPress`, `Inputs.InputMouseClick`,
    `Inputs.InputControllerButton/Axis/Hat` (game controllers), `StreamerBotRemote.InstanceConnected/Disconnected/Signal`,
    `StreamerBot.CustomWebook`
  - Emote changes per platform (`Twitch/YouTube.SevenTVEmoteAdded`, `BetterTTVEmoteAdded`, ...; FFZ stays `Chat.Emote*`)
  - Cron timers send `System.Cron`; interval timers `Misc.TimedAction`
  - Wave Link → `Elgato.WaveLinkV3*`; Control Center → `Elgato.ControlCenterDeviceOn/Off`, `DeviceBrightnessChanged`,
    `DeviceTemperatureChanged`; Camera Hub keeps Streamer.bot's spellings (`CameraHubWebcamrDisconnected`, `CameraHubTiltChanged`)
  - Meld Studio layer visibility, effect, track monitoring and track mute go out as `LayerVisbilityChanged`,
    `LEffectEnabledStateChanged`, `TrackMonitoringStateChanged`, `TrackMustedStateChanged` (Streamer.bot's spellings)
  - No Streamer.bot source exists for Lumia, mtion, OBSBOT, PolyPop, Points, Rumble, Chat, Speech or controller
    hot-plug; they keep Cuebot names. About 90 Cuebot events under a Streamer.bot source have no Streamer.bot
    counterpart (extra OBS, StreamElements, Streamlabs, TipeeeStream, VTube Studio events, ...); they keep the generic
    name and are listed in `StreamerBotEventCatalogTests.CuebotExtras`.
- ✅ · GetEvents lists exactly what `Map()` emits for the loaded modules' triggers, plus the server's own events.
- ✅ · Event payload shape: the documented nested fields are built next to the flat args (`StreamerBotPayloads`).
  Twitch `ChatMessage`/`FirstWord` (`message` object, `user`, `text`, emotes with names and positions, badges),
  `Whisper`, `Follow`, `Cheer`, `Sub`, `ReSub`, `GiftSub`, `GiftBomb`, `Raid`, `RewardRedemption`
  (`data.reward.title`), `Announcement`, `AdRun`, polls, predictions, hype trains, charity, `ShoutoutCreated`,
  bans/timeouts, `ChatMessageDeleted`, `StreamUpdate`, shield mode, pyramids; YouTube `Message`, `FirstWords`,
  member and Super Chat events (YouTube user object), broadcasts, statistics; Kick chat; `Command.*`, timers, heart
  rate, `StreamElements.Tip`, speech to text. Every other event with a user gets the `user` object.
  - P · Still flat apart from `user`: Kick subs/gifts/kicks (Streamer.bot documents no shape), the other donation
    platforms, OBS, VTube Studio, Elgato, Meld, Crowd Control and the rest of the integrations. Twitch emote
    `imageUrl`s are built from the public CDN pattern; badge `imageUrl`s are empty.

**WebSocket requests** (checked against `WS:3.requests.md` and the official client's response types)
- ✅ · Fixed: `GetActions` `trigger_count`; `GetInfo` `mode`, `darkMode`, `source: "websocketServer"`; `GetActiveViewers`
  `type`; `GetCommands` `ignoreInternal`/`ignoreBotAccount`; `TwitchGetEmotes`/`YouTubeGetEmotes` keys
  (`userEmotes`, `bttvEmotes`, `ffzEmotes`, `sevenTvEmotes`); `*GetUserGlobal` with `variable` returns one `variable`;
  `GetUserPronouns` top-level `userLogin`; `GetCredits`/`TestCredits` return `events`, `users`, `hypeTrain`, `top`,
  `groups`, `custom` at the top level (Cuebot's `credits` object stays).
- P · `GetBroadcaster` returns connection states, not the documented account fields (`broadcastUser`, `botUserId`, ...):
  Core has no broadcaster-account interface yet.
- P · `SendMessage` ignores `bot` and `internal` (ChatHub has no bot/broadcaster choice).
- P · `TwitchGetEmotes`/`YouTubeGetEmotes` return the documented keys but empty lists; `GetMonitoredYouTubeBroadcasts`
  and `GetUserPronouns` are stubs.
- P · `GetCommands` `sources` is a list of names; the docs show a number (an undocumented flag set).

**HTTP and UDP** (`sb-docs/streamerbot/3.api/5.http`, `6.udp`)
- ✅ · `GetCredits`/`TestCredits` over HTTP return the documented PascalCase shape (`Events.Follows`, `User.Moderator`,
  `TopBits.All`, ...). `DoAction` (POST, 204), `GetActions`, `ClearCredits`/`ClearFirstWordsCache` (GET, 204), file
  mappings and UDP `DoAction` already matched.
- Note · HTTP `DoAction` is POST-only on purpose (a GET could be triggered by any web page).

## 8. Speaker.bot

Present: all 10 engines, aliases, users (nickname, regular, forced, ignored), `!tts` commands, custom commands with
permissions, event speech with weighted messages and event-state minimums, filters, replacements, VoiceGate,
save-to-folder, highlight alias, WebSocket (incl. `Mode`) and UDP (incl. `profile`, `reg`) APIs, and the
Streamer.bot Speaker.bot sub-actions.

- F · Allow Moderator Whispers (w16): Speech > Chat > "Allow moderator whispers". A broadcaster whisper (`twitch.whisper`) that is a `!tts` or speaking command runs as if a moderator typed it, without chat replies, when Twitch (Helix moderation/moderators, cached 5 min) says the sender moderates the channel; other whispers are ignored — `SP:1.guide/2.config/0.index.md`
- N/A · Speaker.bot Connected / Disconnected triggers: Cuebot's TTS is built in — `TR:integrations/speakerbot/*`

## 9. App shell

- F · `/shutdown` (also `--shutdown`): asks the instance running on the data folder, windowed or headless, to quit (w14-core) — `GD:05.advanced/02.switches.md`
- F · Cloud backups (upload, restore from cloud): Google Drive instead of a Streamer.bot account (w16). Every backup is uploaded to a "Cuebot Backups" folder (`drive.file` scope, the user's own "Desktop app" OAuth client), the newest N are kept, and Settings > Google Drive lists and restores them — `GD:01.core/10.backup.md`
- P · Keyboard shortcuts (not changed in w14-core):
  - Actions view: no Ctrl+R rename group or Ctrl+Home/End to move a sub-action to the top or bottom.
  - Commands, Timers, MIDI, Voice Control and Quotes views: none of the documented Delete / Ctrl+O / Ctrl+C / Ctrl+R shortcuts.
  - Source: `GD:05.advanced/01.shortcuts.md`
- Not applicable: `/nologin` and `/nowebview2` (no Streamer.bot login or WebView2 in Cuebot).

## Notes for whoever acts on this

- **Fastest compatibility win.** Most gaps in sections 4 and 7 are aliases. An alias layer per mapper (add
  `rewardName` next to `rewardTitle`, and so on), CPH wrappers over the existing script functions, and a complete
  `EventType` enum would close a large share of them without new platform work.
- **Event-name table.** The `StreamerBotEventMap.Explicit` table is the place to fix the WebSocket names. Its
  `Catalog` should match what `Map()` actually emits.
- **Variable counts are flags.** They come from a static check scoped to each module, with spot checks against the
  mappers (Twitch subs, rewards, polls, charity, goals, Crowd Control). A runtime dump of the args each trigger's
  sample event produces would give exact lists.
