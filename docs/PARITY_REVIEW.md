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
- **Section 7.** The M count is dominated by **100 documented `CPH` method names** that `CphProxy` does not define.
  Any Streamer.bot script that calls one of them fails to compile in Cuebot.

## Most user-visible gaps (prioritized)

1. **100 documented C# (`CPH`) methods are missing.** A pasted or imported `CPHInline` script that calls any of them
   fails to compile. This contradicts PARITY.md §7, which says scripts "compile unchanged".
   - Missing families: all 11 `Kick*`/Kick user-var methods; 18 `YouTube*` methods; 8 `MeldStudio*`; 7 OBS filter
     methods; 44 Twitch methods (rewards, redemptions, pins, AutoMod, blocked terms, suspicious users, clips,
     `TwitchWarnUser`, `TwitchReplyToMessage`, mods/VIPs); 11 `Command*` cooldown/counter methods; `Parse`.
   - Many of these already exist as Cuebot script functions (`kick.ban`, `youtube.setTitle`, ...). Only the CPH
     names are missing. Source: `CS:3.methods/**`. Full list in §7.
2. **Twitch trigger variables are named differently from Streamer.bot.** 77 of the 100 Twitch triggers that list
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
3. **Sub-action output variables are named differently.** Flagged: 32 of 36 Twitch, 12 of 12 Kick and 2 of 2
   YouTube sub-actions that list outputs. Examples: `createClipUrl`/`createClipId` (Cuebot: `clipUrl`), `banResult`
   and `bannedUser*`, `timeoutResult`, `followAgeLong` / `followAgeShort` / `followDate`,
   `targetUserProfileImageUrl`, `targetIsPartner`, `poll.*` / `prediction.*` from the Get/Lock/Resolve sub-actions,
   `titleSuccess`, `gameSuccess`. Source: `SA:twitch/**`, `SA:kick/**`, `SA:youtube/moderation/*`.
4. **No `rawInputEscaped` / `rawInputUrlEncoded` / `inputEscaped#` / `inputUrlEncoded#` anywhere.** These are
   commonly used to build URLs for Fetch URL. Cuebot has `$urlencode()$` instead.
   Source: `TR:core/commands/command-triggered.md`, `TR:twitch/channel-reward/reward-redemption.md`,
   `TR:kick/kicks/gifted.md`, `TR:youtube/chat/first-words.md`.
5. **Newtonsoft.Json is not available to C# code, and custom DLL references cannot be added.** The docs say
   Newtonsoft "is included in Streamer.bot by default", and a recipe covers custom DLLs. Cuebot compiles against the
   framework plus its own assemblies only, so `using Newtonsoft.Json;` fails.
   Source: `CS:0.guide/1.variables.md`, `CS:1.recipes/custom-dlls.md`, `CS:0.guide/5.debugging.md` (References, Find Refs).
6. **The action-queue sub-actions are missing:** Clear Action Queue, Set Action Queue Blocking State and Set Action
   Queue Pause State. Queues can only be controlled from the UI and through `CPH.Pause/ResumeActionQueue`.
   Source: `SA:core/action-queues/*`.
7. **The action, trigger and sub-action toggles are missing:** Set Trigger State, Set Sub-Action State, Set
   Sub-Action Weight, Get Action Group State, Set Action Group State and Trigger Custom Event.
   Source: `SA:core/triggers/*`, `SA:core/actions/*`.
8. **The C# `EventType` and `EventSource` enums are incomplete,** so `EventType.X` / `EventSource.X` in scripts does
   not compile for most events:
   - `EventType` has 74 of 508 documented values.
   - `EventSource` has 26 of 46.
   - 17 data classes and 3 enums are missing (`TwitchReward`, `QuoteData`, `TeamInfo`, `YouTubeUserInfo`, ...).
   - Source: `CS:5.enums/event-type.md`, `CS:5.enums/event-source.md`, `CS:4.classes/*`.
9. **About 100 WebSocket events go out under names Streamer.bot clients don't subscribe to.** Examples:
   - Twitch chat modes are `EmoteModeOn` instead of `ChatEmoteModeOn`.
   - Streamlabs Desktop's source is `Slobs` instead of `StreamlabsDesktop`.
   - Pulsoid sends `HeartRate` instead of `HeartRatePulse`.
   - T.I.T.S.'s source is `Tits` instead of `ThrowingSystem`.
   - `Raw.Action` and `Raw.SubAction` are never emitted.
   - Source: `CS:5.enums/event-type.md`, `WK:Servers-Clients/WebSocket-Server/Events/**`.
10. **Get Input is a different feature.** Streamer.bot's Get Input opens an interactive dialog (Title, Prompt,
    Original Text, `success`). Cuebot's "Get input" splits text into `input0..N`. Source: `SA:core/logic/get-input.md`.
11. **Streamer.bot Decks are missing entirely:** 10 sub-actions, plus the web-deck feature. PARITY.md never
    mentions them. Source: `SA:streamerbot/decks/*`, `GD:04.extra-features/2.streamerbot-decks.md`.
12. **The C# sub-action options are missing:** the Execute C# Method sub-action, Precompile on Application Start /
    Delayed Start, and Save Result to Variable. Source: `SA:core/csharp/*`, `CS:0.guide/3.custom-methods.md`.
13. **Points has missing and partial sub-actions** (PARITY.md says ✅).
    - Missing: Check Points, Delete Points, Get Points Definition.
    - Partial: the leaderboard has no `Offset`; Award/Update Points for one user cannot apply multipliers; Award
      Points to All only pays present viewers.
    - Source: `SA:core/points/**`.
14. **File and system sub-actions are incomplete:**
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
- Cloud backups are missing.
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
- P · `$math()$`: Cuebot's own evaluator, not mXparser, so the full mXparser token set is not guaranteed — `GD:01.core/03.variables.md`

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

**Not possible (already in PARITY.md)**
- NP · Ad Mid-Roll — `TR:twitch/ads/ad-mid-roll.md`
- NP · Broadcaster PubSub Connected / Disconnected — `TR:twitch/connections/broadcaster-pubsub-*.md`
- NP · Community Goal Contribution / Ended — `TR:twitch/community-goal/*`

**Twitch triggers: partial (74 triggers with missing variable names; examples confirmed in `EventSubMapper.cs`)**
- P · Reward Redemption: `rewardName` (Cuebot `rewardTitle`), `rawInput` (Cuebot `userInput`), `rawInputEscaped`, `rawInputUrlEncoded`, `skipsQueue`, `requiresUserInput`, `maxPerStream`, `maxPerUserPerStream` — `TR:twitch/channel-reward/reward-redemption.md`
- P · Reward Redemption Updated: `rewardName`, `rewardStatus` — `TR:twitch/channel-reward/reward-redemption-updated.md`
- P · Automatic Reward Redemption: `unlockedEmote*`, `gigantifiedEmote*` — `TR:twitch/channel-reward/automatic-reward-redemption.md`
- P · Subscription: `isMultiMonth`, `multiMonthDuration`, `multiMonthTenure` — `TR:twitch/subscriptions/subscription.md`
- P · Resubscription: `cumulative` (Cuebot `cumulativeMonths`), `monthStreak` (Cuebot `streakMonths`), `isMultiMonth`, `multiMonthDuration`, `multiMonthTenure`, `streakShared` — `TR:twitch/subscriptions/resubscription.md`
- P · Gift Subscription: `recipientId` (Cuebot `recipientUserId`), `totalSubsGifted`, `monthsGifted`, `fromGiftBomb`, `subBombCount`, `random`, `totalSubsGiftedShared` — `TR:twitch/subscriptions/gift-subscription.md`
- P · Gift Bomb: `gifts`, `totalGifts` (Cuebot `total`, `cumulativeTotal`), `gift.recipientUser#`/`UserName#`/`Id#`, `totalGiftsShared`, `bonusGifts` — `TR:twitch/subscriptions/gift-bomb.md`
- P · Prime Paid Upgrade: `upgradeTier`, `upgradeTierString` — `TR:twitch/subscriptions/prime-paid-upgrade.md`
- P · Poll Created / Updated / Completed / Terminated / Archived: `poll.Title`, `poll.Id`, `poll.choice#.*`, `poll.votes`, `poll.totalVotes`, `poll._json`; Cuebot has `pollId` etc. (5) — `TR:twitch/polls/*`
- P · Prediction Created / Updated / Locked / Completed / Canceled: `prediction.*` and `prediction.outcome#.*` (5) — `TR:twitch/predictions/*`
- P · Charity Started / Progress / Donation / Completed: `charity.*` with `.value`, `.valueMinor` and `.currency`; Cuebot has `charityName` etc. (4) — `TR:twitch/charity/*`
- P · Goal Begin / Progress / End: `goal.id`, `goal.type`, `goal.currentAmount`, `goal.targetAmount`, `goal.startedAt`/`endedAt` (3) — `TR:twitch/channel-goal/*`
- P · Hype Train Start / Update / Level Up / End: `percentDecimal`, `isGoldenKappaTrain`, `isTreasureTrain`, `isInSharedChat`, `isSharedChatHost`, `top.bits.*`, `top.subscription.*`, `allTimeHighTotal`, `prevLevel` (4) — `TR:twitch/hype-train/*`
- P · User Banned / Timed Out / Unbanned / Untimed Out, plus the 4 shared-chat versions: `createdById`, `createdByUsername`, `createdByDisplayName` (8) — `TR:twitch/moderation/user-*.md`, `TR:twitch/shared-chat/user-*.md`
- P · Raid Start / Send / Cancelled: `raidUser`, `raidUserName`, `raidUserId`, `raidUserProfileImageURL`, `raidUserProfileImageEscaped` (3) — `TR:twitch/raid/start.md`, `send.md`, `cancelled.md`
- P · Pyramid Success / Broken: `totalPyramids`, `pyramidWidth`, `pyramidEmote`, `totalPyramidsBroken`, `pyramidOwner*` (2) — `TR:twitch/pyramid/*`
- P · Stream Update: `gameUpdate`, `statusUpdate`, `oldStatus`, `oldGameId`, `oldGameName`, `gameBoxArt`, `oldGameBoxArt` — `TR:twitch/general/stream-update.md`
- P · Stream Online: `tagCount`, `tagsDelimited` — `TR:twitch/channel/stream-online.md`
- P · Ad Run: `adLengthMs`, `adScheduled` — `TR:twitch/ads/ad-run.md`
- P · Announcement / Shared Announcement: `announceColor` (Cuebot `announcementColor`); shared also lacks `fromSharedChat`, `messageStripped` (2) — `TR:twitch/general/announcement.md`, `TR:twitch/shared-chat/announcement.md`
- P · Blocked / Permitted Terms Added / Deleted: `moderatorDisplayName`, `moderatorId`, `term.[#]` (4) — `TR:twitch/moderation/*-terms-*.md`
- P · Shoutout Created: `shoutoutId`, `targetUserLogin`, `targetUserDisplayName` — `TR:twitch/moderation/shoutout-created.md`
- P · Warned User / Suspicious User Update: `moderator.userName`, `moderator.login`, `moderator.id` (2) — `TR:twitch/moderation/warned-user.md`, `suspicious-user-update.md`
- P · Chat Message Deleted / Shared: `targetMessageId` (2) — `TR:twitch/moderation/chat-message-deleted.md`, `TR:twitch/shared-chat/message-deleted.md`
- P · Shared Chat Session Begin / Update / End: `host.*` (3) — `TR:twitch/shared-chat/session-*.md`
- P · Shared Resub / Sub Gift / Community Sub Gift: the same names as their non-shared versions (3) — `TR:twitch/shared-chat/*`
- P · Subscriber / Unique Mode On/Off: `subscriberOnly`, `uniqueMode` (4) — `TR:twitch/chat/mode/*`
- P · BetterTTV / SevenTV Emote Added/Removed: no `imageUrl`; Cuebot detects these only when the emote cache refreshes, not in real time (4) — `TR:twitch/emotes/*`
- P · First Words: `firstMessage` — `TR:twitch/chat/first-words.md`
- P · Watch Streak: `watchStreakId` — `TR:twitch/chat/watch-streak.md`
- P · Guest Star Guest Update: `moderatorUserLogin`, `guestUser*` — `TR:twitch/guest-star/guest-update.md`
- P · Present Viewers: `isTest` — `TR:twitch/general/present-viewers.md`
- Not checked: `commonVariables` (`TwitchUser`, `TwitchChat`, `TwitchBroadcaster`) are defined in the docs site code, not the repo, so per-user fields such as `msgId`, `userType`, `isSubscribed` and `badges` were not verified.

**Twitch sub-actions: partial (35)**
- P · Set Background Color: no Random Color — `SA:twitch/rewards/set-background-color.md`
- P · Set Max Per Stream / Set Max per User Per Stream: no Additive (2) — `SA:twitch/rewards/set-max-per-stream.md`, `set-max-per-user-per-stream.md`
- P · Create Clip: `createClipSuccess`, `createClipId`, `createClipCreatedAt`, `createClipUrl` (Cuebot `clipId`, `clipUrl`) — `SA:twitch/channel/create-clip.md`
- P · Create Stream Marker: `streamMarker*` (Cuebot `markerId`) — `SA:twitch/channel/create-stream-marker.md`
- P · Get Follow Age Info: `followDate`, `followAgeLong`, `followAgeShort`, `followAgeMinutes`, `followAgeSeconds`, `followUser*`, `followAge.*` — `SA:twitch/followers/get-follow-age-info-for-target.md`
- P · Get User Info for Target: `addTargetResult`, `targetDescriptionEscaped`, `targetUserProfileImageUrl` (+Escaped), `targetUserType`, `targetIsAffiliate`, `targetIsPartner`, `targetLastActive`, `targetPreviousActive`, `targetIsSubscribed`, `targetSubscriptionTier`, `targetIsModerator`, `targetIsVip`, `targetChannelTitle`, `accountAge`, `tagCount`, `tagsDelimited` — `SA:twitch/user/get-user-info-for-target.md`
- P · Add Broadcaster Information: `broadcastUser*`, `broadcastIsAffiliate`, `broadcastIsPartner`, `broadcasterProfileUrl`, `broadcasterOfflineUrl`, `broadcasterCreatedAt`, `broadcasterDescription` — `SA:twitch/user/add-broadcaster-information.md`
- P · Add Random Users: `randomUserType#` — `SA:twitch/user/add-random-users.md`
- P · Ban / Timeout / Unban / UnTimeout / Warn User: `bannedUser*`/`banResult`, `timedOutUser*`/`timeoutResult`, `unbannedUser*`/`unbanResult`, `unTimedOutUser*`/`unTimedOutResult`, `warnedUser*`/`warnedResult` (5) — `SA:twitch/moderation/*`
- P · Send Shoutout / Start Raid / Clear Chat Messages / Set Channel Title / Set Channel Game / Set Branded Content: `shoutoutSuccess`, `startRaidSuccess`, `clearSuccess`, `titleSuccess`, `gameSuccess`/`gameBoxArt`, `success` (6) — `SA:twitch/moderation/send-shoutout.md`, `SA:twitch/raid/start-raid.md`, `SA:twitch/chat/clear-chat-messages.md`, `SA:twitch/channel/*`
- P · Get Ad Schedule / Get Pre-roll Free Time / Snooze Ad: `prerollFree`, `prerollFreeSeconds`, `snooze.success` (3) — `SA:twitch/ads/*`
- P · Create Poll / Get Active Poll: `success`, `poll.*` (2) — `SA:twitch/polls/*`
- P · Create / Get Active / Lock / Cancel / Resolve Last / Resolve Last by Title prediction: `success`, `prediction.*`, `prediction.winningOutcome.*` (6) — `SA:twitch/predictions/*`
- P · Get Reward Info: `rewardSkipRequestQueue`, `rewardMaxPerUserPerStream`, `rewardGlobalCooldown`, `rewardIsOurs` — `SA:twitch/rewards/get-reward-info.md`
- P · Add User To Group / Remove User From Group / User in Group: `addResult`, `removeResult`, `inGroup`, `foundUser*` (3) — `SA:twitch/user-groups/*`

**YouTube: partial (12 triggers, 2 sub-actions)**
- P · Super Chat / Super Sticker: `microAmount`, `currencyCode`, `stickerImageUrl` (2) — `TR:youtube/chat/super-chat.md`, `super-sticker.md`
- P · Jewels Gifted: `gift.*` (`jewelsAmount`, `name`, `url`, `durationInSeconds`, `isCombo`, `comboCount`, ...) — `TR:youtube/chat/jewels-gifted.md`
- P · Message: `parts`, `emotes` — `TR:youtube/chat/message.md`
- P · First Words: `rawInputEscaped`, `input#`, `input#Escaped` — `TR:youtube/chat/first-words.md`
- P · Gift Membership Received: `gifterUser*`, `gifterProfileUrl`, `gifterIsSponsor` — `TR:youtube/membership/gift-membership-received.md`
- P · Membership Gift: `count` — `TR:youtube/membership/gift.md`
- P · Poll Closed: `poll.question`, `poll.option#.*`, `poll.winningOption.*` — `TR:youtube/polls/poll-closed.md`
- P · User Banned / Timed Out: `banDuration` (2) — `TR:youtube/general/user-banned.md`, `user-timed-out.md`
- P · Statistics Updated: `dislikeCount`, `favoriteCount` — `TR:youtube/broadcast/statistics-updated.md`
- P · Present Viewers: `isTest`, `isLive`, `users` — `TR:youtube/general/present-viewers.md`
- P · Ban / Timeout User sub-actions: `bannedUser*`, `banResult`, `timeoutResult` (2) — `SA:youtube/moderation/*`

**Kick: partial (13 triggers, 12 sub-actions)**
- P · Reward Redemption: `reward.id`, `reward.title`, `reward.description`, `reward.cost`, `reward.backgroundColor`, `reward.skipsQueue`, `reward.requiresUserInput`, `rawInputEscaped`, `rawInputUrlEncoded` — `TR:kick/channel-reward/reward-redemption.md`
- P · Kicks Gifted: `kicks.amount`, `kicks.name`, `kicks.type`, `kicks.tier`, `kicks.pinnedTimeSeconds`, `kicks.createdAt`, `input#` variants — `TR:kick/kicks/gifted.md`
- P · Channel Update: `categoryUpdated`, `categoryName`, `categoryThumbnail`, `oldCategory*`, `statusUpdated`, `oldStatus` — `TR:kick/general/channel-update.md`
- P · Stream Online: `category.name`, `category.id`, `category.thumbnail` — `TR:kick/channel/stream-online.md`
- P · Subscription / Resubscription: `monthsSubscribed`, `badgeCount`, `badge` (2) — `TR:kick/subscriptions/subscription.md`, `resubscription.md`
- P · Gift / Mass Gift Subscription: `subscribedAt` (2) — `TR:kick/subscriptions/gift-subscription.md`, `mass-gift-subscription.md`
- P · User Banned / Timed Out: `createdAt`, `createdBy*` (2) — `TR:kick/moderation/*`
- P · First Words: `firstMessage`; Chat Message: `isInternal`; Present Viewers: `isTest`, `categoryName`, `categoryThumbnail` (3) — `TR:kick/chat/*`, `TR:kick/general/present-viewers.md`
- P · Ban / Timeout / Unban / UnTimeout User: `*User*`/`*Result` outputs (4) — `SA:kick/moderation/*`
- P · Set Channel Title / Category: `titleSuccess`, `category.success`/`id`/`name`/`thumbnail` (2) — `SA:kick/channel/*`
- P · Add Broadcaster Information / Add Random Users / Get User Info for Target: `broadcastUserType`, `broadcasterProfileUrl`, `randomUser*#`, `targetUser*`, `targetDescription*`, `targetUserProfileImageUrl*` (3) — `SA:kick/user/*`
- P · Add/Remove User To/From Group / User in Group: `addResult`, `removeResult`, `foundUser*` (3) — `SA:kick/user-groups/*`

## 5. Stream apps and integrations

Present: about 291 (OBS 9/9 triggers and all sub-actions; Meld Studio; VTube Studio; Voicemod; T.I.T.S.; Lumia;
mtion; PolyPop; Stream Deck; Control Center; Wave Link 1.x and 3; Ko-fi, Patreon, Shopify, Throne, Pally.gg,
TipeeeStream, TreatStream, DonorDrive, StreamLoots, StreamElements, Streamlabs, HypeRate; Speaker.bot triggers N/A).

**Missing**
- F (w14-core) · Streamer.bot Decks: Cuebot Decks, served by the HTTP server at `/deck/<id>` with live updates; the 10 sub-actions (Deck Notify, Item Set Background/Icon/State/Title/Value, Item Toggle State, Page Next/Previous/Set), a "Deck item pressed" trigger and a Decks page. Not the streamer.bot website: no Discord login, private/public sharing or file uploads — `SA:streamerbot/decks/*`, `GD:04.extra-features/2.streamerbot-decks.md`, `WK:Extended-Features/HTML-Decks.md`
- M · IFTTT Notification (Basic) / (Advanced) (2) — `SA:integrations/ifttt/*`, `GD:06.integrations/ifttt.md`
- M · Streamlabs Desktop: Set State of a Random Filter, Hide Group's Sources, Set Random Group Source Visible, Set Scene Filter State, Hide Source's Filters (5) — `SA:streamlabs-desktop/filters/*`, `groups/*`, `scenes/set-scene-filter-state.md`, `sources/hide-sources-filters.md`
- M · Elgato Camera Hub webcam: Set Dynamic Range, Set Noise Reduction, Set White Balance Tint (3; may not be reachable through UVC) — `SA:elgato/camera-hub/**`
- M · Stream Deck MCP: `StreamDeckMcpConnected`/`Disconnected` and MCP actions (PARITY.md: "not yet") — `CS:5.enums/event-type.md`

**Partial**
- P · Discord Basic Webhook: no sub-action; only `CPH.DiscordPostTextToWebhook` — `SA:integrations/discord/basic-webhook.md`, `GD:06.integrations/discord.md`
- P · Streamlabs Desktop triggers (7): no `sd.id`, `sd.name`, `sd.host`, `sd.sceneName` — `TR:streamlabs-desktop/*`
- P · Streamlabs Desktop Get Current Scene: no `currentScene` — `SA:streamlabs-desktop/scenes/get-current-scene.md`
- P · OBS Get Scene Item Properties: `props.*` (Cuebot `obsItem.*`) — `SA:obs-studio/scenes/get-scene-item-properties.md`
- P · OBS Get Current Scene / Create Record Chapter: `currentScene` (Cuebot default `obsScene`), `success` (2) — `SA:obs-studio/get-current-scene.md`, `SA:obs-studio/create-record-chapter.md`
- P · Crowd Control Effect Request/Success/Failure, Timed Effect Started/Updated/Ended: `effect.*`, `game.*`, `gamePack.*` (Cuebot `effectId`, `gameName`, ...) (6), plus Coin Exchange `exchangeSource` (1) — `TR:integrations/crowdcontrol/*`
- P · Fourthwall Order Placed/Updated, Gift Purchase, Product Created/Updated, Newsletter Subscribed, Thank You Sent: `fw.subtotal`/`tax`/`donation`/`discount`/`total`, `fw.gifts[n].*`, `fw.variants[#].image`/`quantity`/`attributes.*`/`stock`, `fw.productId`, `fw.images[#]`, `fw.isTest` (Cuebot has `fw.testMode`) (7) — `TR:integrations/fourthwall/*`
- P · Patreon Pledge Updated: `entitledTiers[#].updatedToTier` — `TR:integrations/patreon/pledge-updated.md`
- P · Pulsoid Heart Rate Pulse: `measuredAt` — `TR:integrations/pulsoid/heart-rate-pulse.md`
- P · Streamlabs Merchandise: `merchandiseImageUrl`, `merchandiseImageEscaped` — `TR:integrations/streamlabs/merchandise.md`
- P · Voicemod Soundboard Changed / Voice Loaded: `soundboard.*`, `voice.*`; Get Current Voice: `currentVoiceId` (3) — `TR:integrations/voicemod/*`, `SA:integrations/voicemod/get-current-voice.md`
- P · VTube Studio Model Clicked / Item Event: `clickPosition.X/Y`, `windowSize.X/Y`, `position.x/y`; Get Current Model: `model.*`; Send Raw Request outputs (4) — `TR:integrations/vtube-studio/*`, `SA:integrations/vtube-studio/*`
- P · Camera Hub webcam triggers that Cuebot has (Contrast, Focus, Pan, Tilt, Sharpness, White Balance, Live White Balance): no `old*` previous values (7) — `TR:elgato/camera-hub/webcam/*`
- P · Wave Link 3 Get Channel Information: `waveLinkChannelMix.#.*` — `SA:elgato/wave-link-3/channels/get-channel-information.md`

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

Present: 141 (102 of 202 C# method names; 13 classes/enums; all 22 WebSocket requests; 6 HTTP requests plus serving
files; UDP DoAction; Execute C# Code basics; `Init()`/`Dispose()` lifecycle; custom WebSocket servers and clients).

**Missing C# methods (100)**
- M · Commands (11): `CommandAddToAllUserCooldowns`, `CommandAddToGlobalCooldown`, `CommandAddToUserCooldown`, `CommandGetUserCounter`, `CommandGetUserCounterById`, `CommandRemoveAllUserCooldowns`, `CommandRemoveGlobalCooldown`, `CommandRemoveUserCooldown`, `CommandResetUserCooldown`, `CommandResetUserCounter`, `CommandResetUsersCounters` — `CS:3.methods/core/commands/*`
- M · Core (1): `Parse` — `CS:3.methods/core/parse.md`
- M · Twitch rewards (17): `EnableReward`, `DisableReward`, `PauseReward`, `UpdateRewardMaxPerStream`, `UpdateRewardMaxPerUserPerStream`, `TwitchGetRewards`, `TwitchGetRewardCounter`, `TwitchGetRewardUserCounter`, `TwitchGetRewardUserCounterById`, `TwitchGetRewardUserCounters`, `TwitchResetRewardCounter`, `TwitchResetRewardUserCounters`, `TwitchResetUserRewardCounter`, `TwitchResetUserRewardCounters`, `TwitchRedemptionFulfill`, `TwitchRedemptionCancel`, `TwitchGetChannelPointsUsedByUserId` — `CS:3.methods/twitch/channel-reward/*`
- M · Twitch chat (8): `TwitchReplyToMessage`, `TwitchDeleteChatMessage`, `TwitchClearChatMessages`, `TwitchPinChatMessage`, `TwitchSendAndPinMessage`, `TwitchUnpinMessage`, `TwitchGetPinnedMessage`, `TwitchUpdatePinnedMessageDuration` — `CS:3.methods/twitch/chat/*`
- M · Twitch moderation (9): `TwitchWarnUser`, `TwitchAddBlockedTerm`, `TwitchRemoveBlockedTerm`, `TwitchGetBlockedTerms`, `TwitchApproveAutoHeldMessage`, `TwitchDenyAutoHeldMessage`, `TwitchAddActiveMonitoringSuspiciousStatus`, `TwitchAddRestrictedSuspiciousStatus`, `TwitchRemoveSuspiciousStatus` — `CS:3.methods/twitch/moderation/*`
- M · Twitch clips (5): `GetClips`, `GetAllClips`, `GetClipsForGame`, `GetClipsForUser`, `GetClipsForUserById` — `CS:3.methods/twitch/clips/*`
- M · Twitch user (2): `TwitchGetMods`, `TwitchGetVips` — `CS:3.methods/twitch/user/*`
- M · Twitch user variables (3): `IncrementAllTwitchUsersVar`, `IncrementTwitchUsersVarById`, `IncrementOrCreateTwitchUsersVarById` — `CS:3.methods/twitch/globals/*`
- M · Kick (11): `KickBanUser`, `KickTimeoutUser`, `KickUnbanUser`, `KickDeleteChatMessage`, `KickReplyToMessage`, `KickSetTitle`, `KickSetCategory`, `SetKickUsersVarById`, `IncrementAllKickUsersVar`, `IncrementAllKickUsersVarById`, `IncrementOrCreateKickUsersVarById` — `CS:3.methods/kick/*`
- M · YouTube (18): `YouTubeGetBroadcaster`, `YouTubeGetBot`, `YouTubeSetTitle`, `YouTubeSetDescription`, `YouTubeSetCategory`, `YouTubeSetPrivacy`, `YouTubeSetMetaData`, `YouTubeAddTags`, `YouTubeRemoveTags`, `YouTubeClearTags`, `YouTubeBanUserById`, `YouTubeBanUserByName`, `YouTubeTimeoutUserById`, `YouTubeTimeoutUserByName`, `SendYouTubeMessageToLatestMonitored`, `IncrementAllYouTubeUsersVar`, `IncrementYouTubeUsersVarById`, `IncrementOrCreateYouTubeUsersVarById` — `CS:3.methods/youtube/*`
- M · Meld Studio (8): `MeldStudioConnect`, `MeldStudioDisconnect`, `MeldStudioIsConnected`, `MeldStudioGetConnectionByName`, `MeldStudioShowScene`, `MeldStudioShowSceneByName`, `MeldStudioShowLayerByName`, `MeldStudioHideLayerByName` — `CS:3.methods/meld/*`
- M · OBS (7): `ObsCreateRecordChapter`, `ObsSetFilterState`, `ObsToggleFilter`, `ObsIsFilterEnabled`, `ObsSetRandomFilterState`, `ObsHideScenesFilters`, `ObsHideSourcesFilters` — `CS:3.methods/obs-studio/**`
- Note: method names were compared, not every overload's signature.

**Missing C# classes and enums (20)**
- M · `Cheermote`, `CheermoteImage`, `CheermoteImages`, `CheermoteTier`, `CommandCounter`, `GuestSession`, `GuestStar`, `GuestStarInvite`, `GuestStarSettings`, `KickCategory`, `MediaSettings`, `QuoteData`, `TeamInfo`, `TwitchReward`, `TwitchRewardCounter`, `VTSModelPosition`, `YouTubeUserInfo`; enums `StreamlabsDesktopOutputType`, `TwitchBlockContext`, `TwitchBlockReason` — `CS:4.classes/*`, `CS:5.enums/*`

**Other C# gaps**
- M · Execute C# Method sub-action (call a method of another code block) — `SA:core/csharp/execute-csharp-method.md`, `CS:0.guide/3.custom-methods.md`
- M · Newtonsoft.Json referenceable from scripts — `CS:0.guide/1.variables.md`, `sb-docs/streamerbot/4.examples/http-post.md`
- M · Custom references and Find Refs (custom DLLs) — `CS:1.recipes/custom-dlls.md`, `CS:0.guide/5.debugging.md`, `WK:Settings/CSharp-Compiler.md`
- M · Precompile on Application Start / Delayed Start — `SA:core/csharp/execute-csharp-code.md`
- P · Execute C# Code: no Save Result to Variable or Name/Description fields (Cuebot has a code name) — `SA:core/csharp/execute-csharp-code.md`
- P · `EventType` enum: 74 of 508 values — `CS:5.enums/event-type.md`
- P · `EventSource` enum: 26 of 46 (missing `CrowdControl`, `DonorDrive`, `Elgato`, `Fourthwall`, `HypeRate`, `MeldStudio`, `Pallygg`, `Patreon`, `Pulsoid`, `Quote`, `Shopify`, `StreamDeck`, `StreamLoots`, `StreamerBot`, `StreamerBotRemote`, `StreamlabsDesktop`, `System`, `Throne`, `ThrowingSystem`, `TipeeeStream`, `TreatStream`, `Invalid`) — `CS:5.enums/event-source.md`

**WebSocket events**
- M · `Raw.Action` and `Raw.SubAction` are never emitted (only `Raw.ActionCompleted`) — `WK:Servers-Clients/WebSocket-Server/Events/raw/Action.md`, `.../raw/SubAction.md`, `CS:5.enums/event-type.md`
- P · Event names that differ from Streamer.bot's. The docs repo has no WebSocket event list, so these come from `CS:5.enums/event-type.md`; where the wiki has a page, it confirms the `Source.Type` pattern (`WK:Servers-Clients/WebSocket-Server/Events/**`). Cuebot name first, Streamer.bot name second:
  - Twitch chat modes: `Twitch.EmoteModeOn/Off` and the other 9 → `Twitch.ChatEmoteModeOn/Off`, `ChatFollowerMode*`, `ChatSlowMode*`, `ChatSubscriberMode*`, `ChatUniqueMode*`
  - `Twitch.SharedChatBegin/End/Update` → `SharedChatSessionBegin/End/Update`; `SharedChatGiftBomb` → `SharedChatCommunitySubGift`; `SharedChatModiversary` → `SharedModiversary`
  - `Twitch.UserWarned` → `WarnedUser`; `PowerUp` → `PowerUpRedemption`; `Pyramid` → `PyramidSuccess`; `AutomodMessageUpdated` → `AutoModMessageUpdate`; `BroadcasterConnected/Disconnected` → `BroadcasterEventSubConnected/Disconnected`
  - `Twitch.SubGift` (each recipient) and `Twitch.GiftSub` (Cuebot's "Gift subscriptions"): check that `GiftSub` carries Streamer.bot's single-gift semantics
  - `YouTube.BroadcastStatisticsUpdated` → `StatisticsUpdated`; `YouTube.SponsorOnlyStarted/Ended` → `NewSponsorOnlyStarted/Ended`
  - `Slobs.*` → `StreamlabsDesktop.*` (also `RecordStarted` → `RecordingStarted`, `StreamStarted` → `StreamingStarted`)
  - `Tits.*` → `ThrowingSystem.*`; `Pally.*` → `Pallygg.*`; `Quotes.Added/Shown` → `Quote.Added/Show`
  - `Pulsoid.HeartRate` → `Pulsoid.HeartRatePulse`; `Hyperate.HeartRate`/`ClipCreated` → `HypeRate.HeartRatePulse`/`TwitchClipCreated`
  - `Streamlabs.Merch` → `Streamlabs.Merchandise`
  - `Desktop.ClipboardChanged` → `System.ClipboardChanged`; `Desktop.ToastActivated` → `Toast.Activation`
  - `Inputs.Pressed` → `Input.KeyPress`; `Controller.*` → `Input.ControllerButton/Axis/Hat`
  - `Remote.*` → `StreamerBotRemote.InstanceConnected/Disconnected/Signal`; `Webhook.Custom` → `StreamerBot.CustomWebook`
  - `Chat.EmoteAdded/Removed` → `Twitch/YouTube.SevenTVEmoteAdded`, `BetterTTVEmoteAdded`, ...
  - Cron timers send `Misc.TimedAction`; Streamer.bot has a separate `System.Cron`
  - Wave Link (`Wavelink.*`) and Control Center (`DevicePoweredOn` → `DeviceOn`, `BrightnessChanged` → `DeviceBrightnessChanged`, `TemperatureChanged` → `DeviceTemperatureChanged`) do not match the `Elgato*` names
  - Meld Studio layer visibility, effect, track monitoring and track mute events are not emitted under Streamer.bot's names
- P · Event payload shape: Cuebot sends its flat argument dictionary plus a `user` object and `text`. Streamer.bot clients read nested fields; for example the HTML Overlay module reads `message.data.reward.title`, which Cuebot sends as `rewardTitle` — `GD:04.extra-features/3.html-overlay.md`

## 8. Speaker.bot

Present: all 10 engines, aliases, users (nickname, regular, forced, ignored), `!tts` commands, custom commands with
permissions, event speech with weighted messages and event-state minimums, filters, replacements, VoiceGate,
save-to-folder, highlight alias, WebSocket (incl. `Mode`) and UDP (incl. `profile`, `reg`) APIs, and the
Streamer.bot Speaker.bot sub-actions.

- M · Allow Moderator Whispers (mods whisper `!tts` commands to the broadcaster). Not done in w14-core: it needs the Twitch module to deliver whispers to the broadcaster account as chat messages, which is the platform area — `SP:1.guide/2.config/0.index.md`
- N/A · Speaker.bot Connected / Disconnected triggers: Cuebot's TTS is built in — `TR:integrations/speakerbot/*`

## 9. App shell

- F · `/shutdown` (also `--shutdown`): asks the instance running on the data folder, windowed or headless, to quit (w14-core) — `GD:05.advanced/02.switches.md`
- M · Cloud backups (upload, restore from cloud). Not done: Streamer.bot backs up to its own account service; Cuebot has no cloud account. Local backups exist — `GD:01.core/10.backup.md`
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
