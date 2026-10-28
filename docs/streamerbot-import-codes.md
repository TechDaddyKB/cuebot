# Streamer.bot import: type codes

Streamer.bot export strings store each sub-action and trigger type as an undocumented number. This table lists
the codes the importer (`src/Cuebot.Core/Transfer/StreamerBot/StreamerBotRules.cs`) maps. They were worked out from
public material only: the Streamer.bot docs repository (sub-action, trigger and parameter pages), the official 2024
starter pack, the docs' example imports, a community repository and a survey of extensions.streamer.bot exports.
The evidence is each code's fields, the values in context, the names of the actions using it and the documented
option order. No Streamer.bot code or binary was used.

Confidence:

- **high**: fields and usage leave one reading.
- **medium**: one reading fits best, but rests on action names, the documented option order or few examples.
  These rules are marked `Tentative`; the import report lists them as "type code meaning inferred; please check".
- **low**: not mapped. Such items import as disabled placeholders with their original JSON, as before.

Users can add or override codes with a `streamerbot-mapping.json` file
(`{"subActions": {"<code>": "<rule name>"}, "triggers": {"<code>": "<rule name>"}}`).

## Export layouts

- **v3, v10, v11 (0.1.x to 0.2.x)**: sub-actions sit in one flat `actions` list. A grouped sub-action names its
  group in `group`; the action lists its groups in `actionGroups` (`id`, `name`, `random`, `index`). Groups and
  ungrouped sub-actions share one `index` order. If/Else (120) runs an action per outcome (`actionId` /
  `elseActionId`, all-zero GUID = none) and then continues (`afterAction` 0) or breaks (1).
- **v23, v24 (1.0.x)**: sub-actions sit in `subActions`, nested. Groups are code 99900. If/Else has two children,
  99901 (true branch) and 99902 (false branch), each with its own `subActions`.
- **C# code** (99999): v23+ stores the source as plain text in `code`. Older exports store it in `byteCode` as
  base64 of the UTF-8 source text (not compiled code), so it is fully recoverable. The importer copies it into a
  Cuebot `code.csharp` step and always imports the action disabled; nothing is compiled or run until it is enabled.
  `precompile`, `delayStart` and "Save Result to Variable" are carried over; `references` (Streamer.bot's own
  assemblies) is not, since Cuebot always references the framework.
- **Commands**: `mode` 0 Basic / 1 Regex; `location` 0 Start / 1 Exact / 2 Anywhere; `grantType` 0 Allow /
  1 Deny for `permittedUsers` / `permittedGroups`; `sources` is a list of indexes in v10 (0 Twitch message,
  1 YouTube message, 2 Twitch whisper, …) and a number in v23 (1 = Twitch message).
- **Timers**: `interval`, `randomInterval`, `upperInterval` (0 = unused), `lines`, `repeat`.
- OBS steps reference connections by `connectionId` / `obsId`; Cuebot uses the default connection and warns.

## Sub-actions

| Code | Streamer.bot | Cuebot type | Confidence | Notes |
| --- | --- | --- | --- | --- |
| 1 | Play Sound | audio.playSound | high | volume is a fraction (1 = 100 %) |
| 4 | Run Action | core.runAction | high | `runImmedately` (sic) → wait |
| 5 | Read Random Line | core.readRandomLine | medium | only `count` 1 |
| 6 | Run a Program | core.runProgram | high | action imported disabled; output not captured |
| 7 | Play Sound From Folder | audio.playSoundFromFolder | high | |
| 8 | Set Timer State | timers.setState | high | state 0 enable, 1 disable |
| 10 | Twitch Send Message | twitch.sendMessage | high | `useBot` read; older `preferredAccount` → Default |
| 15 | Twitch Set Title | twitch.setTitle | high | |
| 16 | Twitch Set Game | twitch.setGame | high | by name only |
| 17 | Twitch Create Stream Marker | twitch.createMarker | high | |
| 20 | Twitch Timeout User | twitch.timeout | high | |
| 21 | Add Broadcaster Information | twitch.getBroadcasterInfo | medium | Cuebot argument names differ |
| 24 | Add Viewer Count | twitch.getStreamInfo | medium | |
| 29 | Twitch Send Shoutout | twitch.sendShoutout | high | |
| 30 | OBS Set Source Visibility | obs.setSourceVisibility | high | 0 visible, 1 hidden, 2 toggle |
| 35 | OBS Set Source Filter State | obs.setFilterState | medium | 0 on, 1 off, 2 toggle |
| 38 | OBS Set Browser Source URL | obs.setBrowserUrl | high | |
| 39 | OBS Set GDI Text | obs.setText | high | |
| 46 | OBS Raw | obs.rawRequest | high | `resultsToArgs` not spread; JSON stored in the prefix argument |
| 47 | OBS Set Scene Filter State | obs.setFilterState | high | 0 on, 1 off, 2 toggle |
| 50 | Get User Info for Target | twitch.getUserInfo | high | `sourceType` 0 broadcaster, 1 user, 2 input, 3 variable; v23 `userLogin` |
| 51 | Get Follow Age Info for Target | twitch.getFollowAge | high | as 50 |
| 120 | If / Else | core.if | high | operators below |
| 121 | Get Global Variable | core.getGlobal / core.getUserVariable | high | `source` 0 global, 1 user, 2 target user |
| 122 | Set Global Variable | core.setGlobal / core.setUserVariable | high | `destination` as 121; `source` 0 argument, 1 value, 2 increment, 3 decrement |
| 123 | Set Argument | core.setArgument | high | |
| 124 | Break | core.stop | high | |
| 128 | While | core.while | medium | operator 0 equals, 1 not equals, 3 less than |
| 322 | OBS Set Media Source File | obs.setMediaFile | medium | |
| 323 | OBS Set Image Source File | obs.setImageFile | medium | |
| 328 | OBS Create Record Chapter | obs.createRecordChapter | medium | |
| 500 | Twitch Start Raid | twitch.startRaid | medium | |
| 506 | Get Follower Count | twitch.getFollowerCount | high | |
| 511 | Twitch Ban User | twitch.ban | medium | |
| 550 | Add Present User | twitch.getPresentUser | high | |
| 560 | Twitch Reply to Message | twitch.replyToMessage | high | |
| 601 | Speaker.bot Speak | speech.speak | medium | |
| 1000 | Get Quote | quotes.get | high | `quoteType` 0 random, 1 by id |
| 1002 | Delay | core.delay | high | random range imported as its minimum |
| 1003 | Get Random Number | core.randomNumber | high | `%randomNumber%` |
| 1007 | Fetch URL | core.httpRequest | high | `parseAsJson` not supported |
| 1009 | Comment | core.comment | high | colour dropped |
| 1013 | Get Commands | commands.getCommands | high | |
| 1020 | Log Message | core.log | high | level (medium): 0/1 debug, 2 info, 3 warning, 4 error |
| 1024 | Toast Notification | desktop.showNotification | high | |
| 1036 | Delete Quote | quotes.delete | high | |
| 1037 | Add Quote | quotes.add | high | |
| 4001 | YouTube Send Message | youtube.sendMessage | medium | |
| 4002 | YouTube Set Title | youtube.setTitle | medium | |
| 11001 | Stream Deck Set Title | streamDeck.setTitle | high | |
| 11002 | Stream Deck Set Background | streamDeck.setBackground | high | colour-only backgrounds dropped |
| 11008 | Stream Deck Set Value | streamDeck.setValue | high | |
| 15001 | Add Pronouns for User | pronouns.addForUser | medium | |
| 99900 | Group | core.group | high | v23 nested groups |
| 99901 / 99902 | If/Else true / false branch | branches of core.if | high | |
| 99998 | Execute C# Method | code.csharp | high | a script calling `CPH.ExecuteMethod(code name, method)`; disabled |
| 99999 | Execute C# Code | code.csharp | high | source from `code` or base64 `byteCode`; disabled |

If/Else operators (120), in the documented order: 0 Equals, 1 Not Equals, 2 Contains, 3 Regex Match, 4 Less Than,
5 Greater Than, 6 Does Not Exist, 7 Equals (ignore case), 8 Not Equals (ignore case), 9 Is Null or Empty. Usage in
the exports confirms 0, 1, 2, 3, 4, 6, 8 and 9 (high); 5 and 7 follow from the order (medium). Cuebot compares text
case-insensitively, so 0/7 and 1/8 map to the same operator. "Does Not Exist" becomes
`$parse(name)$` equals `\$parse(name)$`, which holds exactly when the argument is unset. Older exports name a bare
argument in `variableName`; it becomes `%name%`.

Not mapped (low): 12, 13, 14, 27 (the four Twitch chat-mode switches: which is which is not clear), 33 (an OBS
output switch whose state values contradict the docs), 9 (command state), 22 (add random users with group
filters), 23 (announcement colours), 1004, 1010, 1011 (action/group states), 1008 (colour argument), 1012
(key press), 1006, 1021 (file reads), 1023 (input dialog), 42, 48, 49, 200, 324, 561, 563, 2, 7001 (Discord
webhook), 17001, 17008 and 18001 (Kick and other platforms).

## Triggers

| Code | Streamer.bot | Cuebot type | Confidence | Notes |
| --- | --- | --- | --- | --- |
| 101 | Twitch Follow | twitch.follow | high | |
| 102 | Twitch Cheer | twitch.cheer | medium | min/max bits; 102 vs 107 by order |
| 103 | Twitch Sub | twitch.sub | high | `tiers` |
| 104 | Twitch Resub | twitch.resub | high | min months |
| 105 | Twitch Gift Sub | twitch.giftSub | high | `monthsGifted` |
| 106 | Twitch Gift Bomb | twitch.giftBomb | high | min gifts |
| 107 | Twitch Raid | twitch.raid | medium | min viewers |
| 112 | Twitch Reward Redemption | twitch.rewardRedemption | high | reward id only; title must be set |
| 118 | Twitch Stream Update | twitch.streamUpdate | high | `gameOnly` not supported |
| 120 | Twitch First Words | users.firstWords (Twitch) | high | user filter dropped |
| 124 | Twitch Present Viewers | twitch.presentViewers | medium | |
| 133 | Twitch Chat Message | twitch.chatMessage | high | |
| 137 | Twitch User Banned | twitch.userBanned | medium | |
| 139 | Twitch Ad Run | twitch.adRun | medium | |
| 154 / 155 | Twitch Stream Online / Offline | twitch.streamOnline / streamOffline | medium | |
| 186 | Twitch Upcoming Ad | twitch.upcomingAd | high | `minutes` |
| 401 | Command Triggered | command.triggered | high | |
| 472 | Twitch Watch Streak | twitch.watchStreak | medium | |
| 701 | Timed Action | core.timer | high | |
| 706 | Streamer.bot Started | core.appStarted | medium | |
| 4001 / 4002 | YouTube Broadcast Started / Ended | youtube.broadcastStarted / broadcastEnded | medium | |
| 4003 | YouTube Message | youtube.chatMessage | medium | |
| 4005 | YouTube User Banned | youtube.userBanned | medium | |
| 4016 | YouTube First Words | users.firstWords (YouTube) | medium | |
| 14004 | OBS Scene Changed | obs.sceneChanged | high | |
| 14005 | OBS Streaming Started | obs.streamStarted | medium | |
| 18001 | Custom event | custom.codeEvent | high | `eventName` |
| 18002 | Custom code event | custom.codeEvent | high | `eventName` |

Not mapped (low): 702 (the Test trigger, with test arguments; Cuebot runs actions by hand instead), 108–111 (likely
hype train), 125–132 (polls and predictions: order unclear), 135, 136, 140, 144, 145, 153, 158, 159, 161, 173,
191–195, 201, 1201, 7001, 10001, 24006, 28003 (donation triggers of various services), 301, 402, 463, 467, 471,
477–479, 495, 501, 502, 601, 709, 803–805, 4006–4009, 4014, 4015, 4017, 4018, 14003, 14007, 14008, 16001,
25008–25010 and 35010+.

## Counts

Sub-action codes: 44 high, 14 medium. Trigger codes: 15 high, 15 medium (4001 and 4002 are both a sub-action and a
trigger code). On the public samples tried, about 98 % of sub-actions and 72 % of triggers map.
