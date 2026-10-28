# Streamer.bot import: type codes

Streamer.bot export strings store each sub-action and trigger type as an undocumented number. These tables list
the codes the importer (`src/Cuebot.Core/Transfer/StreamerBot/StreamerBotRules.cs`, trigger rules in
`StreamerBotTriggerRules.cs`) maps. The trigger codes were verified against a real Streamer.bot 1.0.7 (see
[Triggers](#triggers)). The sub-action codes were worked out from public material only: the Streamer.bot docs
repository (sub-action, trigger and parameter pages), the official 2024 starter pack, the docs' example imports, a
community repository and a survey of extensions.streamer.bot exports. The evidence is each code's fields, the values
in context, the names of the actions using it and the documented option order. No Streamer.bot code was used.

Confidence:

- **high**: fields and usage leave one reading.
- **medium**: one reading fits best, but rests on action names, the documented option order or few examples.
  These sub-action rules are marked `Tentative`; the import report lists them as "type code meaning inferred; please check".
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
  `precompile`, `delayStart` and "Save Result to Variable" are carried over. Of `references`, the .NET Framework
  and the DLLs Streamer.bot ships with (`.\Newtonsoft.Json.dll`, ...) are dropped, since Cuebot always references
  the framework; a DLL the user added (`.\dlls\X.dll`, or a full path) is kept under References, a relative one as
  a `#` line to replace with the full path, and the report names it. Code using Windows Forms or WPF is flagged.
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

Verified against a real **Streamer.bot 1.0.7** install (2026-10-28): every trigger in its Add Trigger menu was added
in its UI and the saved file read back, which gives each trigger's code and the settings it saves (field names, types
and defaults). All 419 are listed here; 371 map to a Cuebot trigger, 48 have no Cuebot equivalent. The codes and
default settings are the test fixture `tests/Cuebot.Tests/TestData/streamerbot-1.0.7-trigger-defaults.json`;
`StreamerBotTriggerCodesTests` imports all of them and Cuebot.UiTests checks every mapped one against the trigger
types and parameters the app registers. 18002 (custom code event, seen in older exports) is not in the 1.0.7 menu and
maps like 18001.

Settings conventions:

- `min` / `max` are Streamer.bot's Range filter: -1 is open, a Max without a Min means exactly that value. They map
  to Cuebot's minimum/maximum settings (0 = no maximum). Where Cuebot's trigger has only a minimum, an exact value is
  imported as "at least" and a maximum next to a minimum is reported; where it has no range at all, the range is
  reported.
- Ids (all-zero GUID or null = not set) name Streamer.bot objects. Commands and timers in the export are resolved; a
  name saved next to an id is carried (VTube Studio model, Throwing System item/trigger, Crowd Control game/effect,
  webhook, Wave Link input); other ids (watchers, groups, WebSocket clients/servers, MIDI devices, DonorDrive, remote
  instances, Meld/Streamlabs Desktop connections, Kick rewards) leave the filter at "any" and are reported. OBS
  connections use the default connection, as for OBS sub-actions.
- "Reported" means the import report warns when the field holds a value (not its default) that Cuebot's trigger
  cannot filter on; the trigger is then imported without that filter.
- `tiers` is a bitmask; its default 16 (every tier) maps to Any. Other masks are not decoded: tier Any, reported.
  Older exports' `tier` text ("1000", "prime", ...) still maps to the tier. `monthsGifted` 15 (all) and `subType`
  0 are the defaults.
- `persisted` follows the documented order 0 Either (the default), 1 Yes, 2 No.

| Code | Streamer.bot | Cuebot trigger | Settings |
| --- | --- | --- | --- |
| 402 | Core > Commands > Command Cooldown | command.cooldown | `commandId` → the imported command |
| 401 | Core > Commands > Command Triggered | command.triggered | `commandId` → the imported command |
| 511 | Core > File IO > File Tail > Changed | core.fileTailLine | `fileTailId` reported |
| 501 | Core > File/Folder Watcher > Changed | core.fileChanged | `watcherId` reported |
| 502 | Core > File/Folder Watcher > Created | core.fileCreated | `watcherId` reported |
| 503 | Core > File/Folder Watcher > Deleted | core.fileDeleted | `watcherId` reported |
| 504 | Core > File/Folder Watcher > Renamed | core.fileRenamed | `watcherId` reported |
| 711 | Core > Global Variables > Global Variable Created | variables.globalCreated | `variableName` → variable; `persisted` → persisted |
| 712 | Core > Global Variables > Global Variable Deleted | variables.globalDeleted | `variableName` → variable; `persisted` → persisted |
| 709 | Core > Global Variables > Global Variable Updated | variables.globalUpdated | `variableName` → variable; `persisted` → persisted |
| 710 | Core > Global Variables > User Global Variable Updated | variables.userUpdated | `userLogon` → userLogin; `platform` → platform; `variableName` → variable; `persisted` → persisted |
| 32001 | Core > Groups > Added | groups.added |  |
| 32003 | Core > Groups > Cleared | groups.cleared | `groupId` reported |
| 32002 | Core > Groups > Removed | groups.removed |  |
| 32004 | Core > Groups > Users Added | groups.usersAdded | `groupId` reported |
| 32005 | Core > Groups > Users Removed | groups.usersRemoved | `groupId` reported |
| 16001 | Core > Inputs > Key Press | hotkeys.pressed | imported disabled: `keyCode`/`mask` not decoded, set the hotkey |
| 16002 | Core > Inputs > Mouse Click | input.mouseClick | imported disabled: `buttons` not decoded, set the button |
| 15001 | Core > MIDI > MIDI Message | midi.message | `midiDevice`, `midiEvent` reported |
| 703 | Core > Processes > Process Started | core.processStarted | `processName` reported: add a process watcher |
| 704 | Core > Processes > Process Stopped | core.processStopped | `processName` reported: add a process watcher |
| 601 | Core > Quotes > Quote Added | quotes.added |  |
| 602 | Core > Quotes > Show Quote | quotes.shown |  |
| 706 | Core > Streamer.bot Started | core.appStarted |  |
| 42001 | Core > System > Clipboard Changed | desktop.clipboardChanged |  |
| 708 | Core > System > Toast Activation | desktop.toastActivated | `toastId` → toastId |
| 701 | Core > Timed Actions | core.timer | `timerId` → the imported timer |
| 302 | Core > Voice Control > Command | voiceControl.command | `commandId` reported |
| 301 | Core > Voice Control > Dictation | voiceControl.dictation | `commandId` reported |
| 804 | Core > Websocket > Client > Websocket Client Closed | websocket.clientClose | `clientId` reported |
| 805 | Core > Websocket > Client > Websocket Client Message | websocket.clientMessage | `clientId` reported |
| 803 | Core > Websocket > Client > Websocket Client Opened | websocket.clientOpen | `clientId` reported |
| 2002 | Core > Websocket > Custom Server > Custom Server Connection Closed | websocket.serverClose | `serverId` reported |
| 2001 | Core > Websocket > Custom Server > Custom Server Connection Opened | websocket.serverOpen | `serverId` reported |
| 2003 | Core > Websocket > Custom Server > Custom Server Message | websocket.serverMessage | `serverId` reported |
| 18001 | Custom > Custom | custom.codeEvent | `eventName` → eventName |
| 21301 | Elgato > Camera Hub > Connected | elgato.cameraHub.connected |  |
| 21302 | Elgato > Camera Hub > Disconnected | elgato.cameraHub.disconnected |  |
| 21372 | Elgato > Camera Hub > Webcam > Anti Flicker Changed | elgato.cameraHub.webcam.antiFlickerChanged |  |
| 21364 | Elgato > Camera Hub > Webcam > Auto Exposure Disabled | elgato.cameraHub.webcam.autoExposureDisabled |  |
| 21363 | Elgato > Camera Hub > Webcam > Auto Exposure Enabled | elgato.cameraHub.webcam.autoExposureEnabled |  |
| 21382 | Elgato > Camera Hub > Webcam > Auto Focus Disabled | elgato.cameraHub.webcam.autoFocusDisabled |  |
| 21381 | Elgato > Camera Hub > Webcam > Auto Focus Enabled | elgato.cameraHub.webcam.autoFocusEnabled |  |
| 21366 | Elgato > Camera Hub > Webcam > Auto White Balance Disabled | elgato.cameraHub.webcam.autoWhiteBalanceDisabled |  |
| 21365 | Elgato > Camera Hub > Webcam > Auto White Balance Enabled | elgato.cameraHub.webcam.autoWhiteBalanceEnabled |  |
| 21385 | Elgato > Camera Hub > Webcam > Brightness Changed | elgato.cameraHub.webcam.brightnessChanged |  |
| 21361 | Elgato > Camera Hub > Webcam > Contrast Changed | elgato.cameraHub.webcam.contrastChanged |  |
| 21356 | Elgato > Camera Hub > Webcam > Device Orientation Changed | elgato.cameraHub.webcam.deviceOrientationChanged |  |
| 21355 | Elgato > Camera Hub > Webcam > Flipped | elgato.cameraHub.webcam.flipped |  |
| 21383 | Elgato > Camera Hub > Webcam > Focus Changed | elgato.cameraHub.webcam.focusChanged |  |
| 21388 | Elgato > Camera Hub > Webcam > Live Shutter Speed Changed | elgato.cameraHub.webcam.liveShutterSpeedChanged |  |
| 21389 | Elgato > Camera Hub > Webcam > Live White Balance Changed | elgato.cameraHub.webcam.liveWhiteBalanceChanged |  |
| 21354 | Elgato > Camera Hub > Webcam > Mirrored | elgato.cameraHub.webcam.mirrored |  |
| 21378 | Elgato > Camera Hub > Webcam > Pan Changed | elgato.cameraHub.webcam.panChanged |  |
| 21386 | Elgato > Camera Hub > Webcam > Saturation Changed | elgato.cameraHub.webcam.saturationChanged |  |
| 21371 | Elgato > Camera Hub > Webcam > Sharpness Changed | elgato.cameraHub.webcam.sharpnessChanged |  |
| 21370 | Elgato > Camera Hub > Webcam > Shutter Speed Changed | elgato.cameraHub.webcam.shutterSpeedChanged |  |
| 21379 | Elgato > Camera Hub > Webcam > Tilt Changed | elgato.cameraHub.webcam.tiltChanged |  |
| 21351 | Elgato > Camera Hub > Webcam > Webcam Activated | elgato.cameraHub.webcam.connected | Activated = webcam ready (connected) |
| 21352 | Elgato > Camera Hub > Webcam > Webcam Deactivated | elgato.cameraHub.webcam.disconnected | Deactivated = webcam gone (disconnected) |
| 21362 | Elgato > Camera Hub > Webcam > White Balance Changed | elgato.cameraHub.webcam.whiteBalanceChanged |  |
| 21360 | Elgato > Camera Hub > Webcam > Zoom Changed | elgato.cameraHub.webcam.zoomChanged |  |
| 17002 | Elgato > Stream Deck > Connected | streamDeck.connected |  |
| 17003 | Elgato > Stream Deck > Disconnected | streamDeck.disconnected |  |
| 17004 | Elgato > Stream Deck > Stream Deck Info | streamDeck.info |  |
| 21017 | Elgato > Wave Link > Connected | wavelink.connected |  |
| 21018 | Elgato > Wave Link > Disconnected | wavelink.disconnected |  |
| 21013 | Elgato > Wave Link > Filter Added | wavelink.legacy.filterAdded |  |
| 21016 | Elgato > Wave Link > Filter Bypass State Changed | wavelink.legacy.filterBypassStateChanged |  |
| 21014 | Elgato > Wave Link > Filter Changed | wavelink.legacy.filterChanged |  |
| 21015 | Elgato > Wave Link > Filter Deleted | wavelink.legacy.filterDeleted |  |
| 21019 | Elgato > Wave Link > Input Level Meter Changed | wavelink.legacy.inputLevelMeterChanged | `name` → levelName (else `identifier` reported) |
| 21006 | Elgato > Wave Link > Input Mute Changed | wavelink.legacy.inputMuteChanged |  |
| 21007 | Elgato > Wave Link > Input Name Changed | wavelink.legacy.inputNameChanged |  |
| 21005 | Elgato > Wave Link > Input Volume Changed | wavelink.legacy.inputVolumeChanged |  |
| 21010 | Elgato > Wave Link > Microphone Balance Changed | wavelink.legacy.microphoneBalanceChanged |  |
| 21008 | Elgato > Wave Link > Microphone Gain Changed | wavelink.legacy.microphoneGainChanged |  |
| 21011 | Elgato > Wave Link > Microphone Mute Changed | wavelink.legacy.microphoneMuteChanged |  |
| 21009 | Elgato > Wave Link > Microphone Output Volume Changed | wavelink.legacy.microphoneOutputVolumeChanged |  |
| 21012 | Elgato > Wave Link > Microphone Setting Changed | wavelink.legacy.microphoneSettingChanged |  |
| 21020 | Elgato > Wave Link > Output Level Meter Changed | wavelink.legacy.outputLevelMeterChanged | `mixerType` reported unless all mixes |
| 21003 | Elgato > Wave Link > Output Mute Changed | wavelink.legacy.outputMuteChanged |  |
| 21001 | Elgato > Wave Link > Output Switched | wavelink.legacy.outputSwitched |  |
| 21002 | Elgato > Wave Link > Output Volume Changed | wavelink.legacy.outputVolumeChanged |  |
| 21004 | Elgato > Wave Link > Selected Output Changed | wavelink.legacy.selectedOutputChanged |  |
| 20009 | Integrations > CrowdControl > Coin Exchange | crowdcontrol.coinExchange | `coinType` → coinType; `min` → minAmount; `exchangeSource`, `max` reported |
| 20005 | Integrations > CrowdControl > Effect Failure | crowdcontrol.effectFailure | `gameName` → gameName; `effectName` → effectName; `min` → minPrice; `effectId` reported when `effectName` is empty; `gameId` reported when `gameName` is empty; `packName`, `effectType`, `max` reported |
| 20003 | Integrations > CrowdControl > Effect Request | crowdcontrol.effectRequest | `gameName` → gameName; `effectName` → effectName; `min` → minPrice; `effectId` reported when `effectName` is empty; `gameId` reported when `gameName` is empty; `packName`, `effectType`, `max` reported |
| 20004 | Integrations > CrowdControl > Effect Success | crowdcontrol.effectSuccess | `gameName` → gameName; `effectName` → effectName; `min` → minPrice; `effectId` reported when `effectName` is empty; `gameId` reported when `gameName` is empty; `packName`, `effectType`, `max` reported |
| 20002 | Integrations > CrowdControl > Game Session End | crowdcontrol.gameSessionEnd |  |
| 20001 | Integrations > CrowdControl > Game Session Start | crowdcontrol.gameSessionStart |  |
| 20007 | Integrations > CrowdControl > Timed Effect Ended | crowdcontrol.timedEffectEnded |  |
| 20006 | Integrations > CrowdControl > Timed Effect Started | crowdcontrol.timedEffectStarted |  |
| 20008 | Integrations > CrowdControl > Timed Effect Update | crowdcontrol.timedEffectUpdated |  |
| 3001 | Integrations > DonorDrive > Donation | donordrive.donation | `donorDriveId` reported |
| 3003 | Integrations > DonorDrive > Incentive | donordrive.incentive | `donorDriveId` reported |
| 3002 | Integrations > DonorDrive > Profile Updated | donordrive.profileUpdated | `donorDriveId` reported |
| 24006 | Integrations > Fourthwall > Donation | fourthwall.donation | `min` → minAmount; `max` → maxAmount |
| 24013 | Integrations > Fourthwall > Gift Draw Ended | fourthwall.giftDrawEnded |  |
| 24012 | Integrations > Fourthwall > Gift Draw Started | fourthwall.giftDrawStarted |  |
| 24003 | Integrations > Fourthwall > Gift Purchase | fourthwall.giftPurchase |  |
| 24011 | Integrations > Fourthwall > Newsletter Subscribed | fourthwall.newsletterSubscribed |  |
| 24004 | Integrations > Fourthwall > Order Placed | fourthwall.orderPlaced |  |
| 24005 | Integrations > Fourthwall > Order Updated | fourthwall.orderUpdated |  |
| 24001 | Integrations > Fourthwall > Product Created | fourthwall.productCreated |  |
| 24002 | Integrations > Fourthwall > Product Updated | fourthwall.productUpdated |  |
| 24009 | Integrations > Fourthwall > Subscription Changed | fourthwall.subscriptionChanged |  |
| 24008 | Integrations > Fourthwall > Subscription Expired | fourthwall.subscriptionExpired |  |
| 24007 | Integrations > Fourthwall > Subscription Purchased | fourthwall.subscriptionPurchased |  |
| 24010 | Integrations > Fourthwall > Thank You Sent | fourthwall.thankYouSent |  |
| 6003 | Integrations > HypeRate.io > Connected | hyperate.connected |  |
| 6004 | Integrations > HypeRate.io > Disconnected | hyperate.disconnected |  |
| 6001 | Integrations > HypeRate.io > Heart Rate Pulse | hyperate.heartRate | `min`, `max` reported |
| 6002 | Integrations > HypeRate.io > Twitch Clip Created | hyperate.clipCreated |  |
| 7005 | Integrations > Kofi > Commission | kofi.commission |  |
| 7001 | Integrations > Kofi > Donation | kofi.donation | `min` → minAmount; `max` → maxAmount |
| 7003 | Integrations > Kofi > Resubscription | kofi.resubscription |  |
| 7004 | Integrations > Kofi > Shop Order | kofi.shopOrder |  |
| 7002 | Integrations > Kofi > Subscription | kofi.subscription |  |
| 28003 | Integrations > Pally.gg > Campaign Tip | pally.campaignTip | `min` → minAmount; `max` → maxAmount |
| 28001 | Integrations > Pally.gg > Connected | pally.connected |  |
| 28002 | Integrations > Pally.gg > Disconnected | pally.disconnected |  |
| 8001 | Integrations > Patreon > Follow Created | patreon.followCreated |  |
| 8002 | Integrations > Patreon > Follow Deleted | patreon.followDeleted |  |
| 8003 | Integrations > Patreon > Pledge Created | patreon.pledgeCreated |  |
| 8005 | Integrations > Patreon > Pledge Deleted | patreon.pledgeDeleted |  |
| 8004 | Integrations > Patreon > Pledge Updated | patreon.pledgeUpdated |  |
| 5001 | Integrations > Pulsoid > Heart Rate Pulse | pulsoid.heartRate | `min`, `max` reported |
| 13001 | Integrations > Shopify > Shopify Order Created | shopify.orderCreated |  |
| 13002 | Integrations > Shopify > Shopify Order Paid | shopify.orderPaid |  |
| 1203 | Integrations > StreamElements > Connected | streamelements.connected |  |
| 1204 | Integrations > StreamElements > Disconnected | streamelements.disconnected |  |
| 1202 | Integrations > StreamElements > Merch | streamelements.merch |  |
| 1201 | Integrations > StreamElements > Tip | streamelements.tip | `min` → minAmount; `max` → maxAmount |
| 34001 | Integrations > Streamer.bot > Custom Webhook | webhook.custom | `webhookName` → hookName; `webhookId` reported when `webhookName` is empty |
| 29001 | Integrations > Streamer.bot Remote > Remote Instance Connected | remote.connected | `instanceId` reported |
| 29002 | Integrations > Streamer.bot Remote > Remote Instance Disconnected | remote.disconnected | `instanceId` reported |
| 29003 | Integrations > Streamer.bot Remote > Remote Instance Trigger | remote.signal | `eventName` → signalName; `instanceId` reported |
| 205 | Integrations > Streamlabs > Charity Donation | streamlabs.charityDonation | `min` → minAmount; `max` → maxAmount |
| 203 | Integrations > Streamlabs > Connected | streamlabs.connected |  |
| 204 | Integrations > Streamlabs > Disconnected | streamlabs.disconnected |  |
| 201 | Integrations > Streamlabs > Donation | streamlabs.donation | `min` → minAmount; `max` → maxAmount |
| 202 | Integrations > Streamlabs > Merchandise | streamlabs.merch |  |
| 36003 | Integrations > StreamLoots > Card Redeemed | streamloots.cardRedeemed |  |
| 36002 | Integrations > StreamLoots > Pack Gifted | streamloots.packGifted |  |
| 36001 | Integrations > StreamLoots > Pack Purchased | streamloots.packPurchased |  |
| 36004 | Integrations > StreamLoots > Reaction | streamloots.alert | Other alert (runs for every non-pack/card alert; reported) |
| 37002 | Integrations > Throne > Contribution Purchased | throne.contributionPurchased |  |
| 37003 | Integrations > Throne > Gift Crowdfunded | throne.giftCrowdfunded |  |
| 37001 | Integrations > Throne > Gift Purchased | throne.giftPurchased |  |
| 27001 | Integrations > Throwing System > Connections > Connected | tits.connected |  |
| 27004 | Integrations > Throwing System > Connections > Events Connected | tits.eventsConnected |  |
| 27005 | Integrations > Throwing System > Connections > Events Disconnected | tits.eventsDisconnected |  |
| 27002 | Integrations > Throwing System > Connections > Websocket Connected | tits.websocketConnected |  |
| 27003 | Integrations > Throwing System > Connections > Websocket Disconnected | tits.websocketDisconnected |  |
| 27010 | Integrations > Throwing System > Item Hit | tits.itemHit | `itemName` → item; `itemId` reported when `itemName` is empty |
| 27011 | Integrations > Throwing System > Trigger Activated | tits.triggerActivated | `triggerName` → trigger; `triggerId` reported when `triggerName` is empty |
| 27012 | Integrations > Throwing System > Trigger Ended | tits.triggerEnded | `triggerName` → trigger; `triggerId` reported when `triggerName` is empty |
| 10001 | Integrations > TipeeeStream > Donation | tipeeestream.donation | `min` → minAmount; `max` → maxAmount |
| 11001 | Integrations > TreatStream > Treat | treatstream.treat |  |
| 30001 | Integrations > VoiceMod > Connected | voicemod.connected |  |
| 30002 | Integrations > VoiceMod > Disconnected | voicemod.disconnected |  |
| 30004 | Integrations > VoiceMod > Soundboard Changed | voicemod.soundboardChanged | `soundboardId` reported |
| 30003 | Integrations > VoiceMod > Voice Loaded | voicemod.voiceChanged | `voiceId` → voice (Voicemod ids accepted) |
| 19003 | Integrations > VTube Studio > Background Changed | vtubestudio.backgroundChanged |  |
| 19007 | Integrations > VTube Studio > Connected | vtubestudio.connected |  |
| 19008 | Integrations > VTube Studio > Disconnected | vtubestudio.disconnected |  |
| 19005 | Integrations > VTube Studio > Hotkey Triggered | vtubestudio.hotkeyTriggered | `modelId` reported when `modelName` is empty; `modelName` reported |
| 19010 | Integrations > VTube Studio > Item Event | vtubestudio.itemEvent |  |
| 19006 | Integrations > VTube Studio > Model Animation | vtubestudio.modelAnimation | `modelId` reported when `modelName` is empty; `modelName` reported |
| 19011 | Integrations > VTube Studio > Model Clicked | vtubestudio.modelClicked | `modelId` reported when `modelName` is empty; `modelName` reported |
| 19004 | Integrations > VTube Studio > Model Configuration Changed | vtubestudio.modelConfigChanged | `modelId` reported when `modelName` is empty; `modelName` reported |
| 19001 | Integrations > VTube Studio > Model Loaded | vtubestudio.modelLoaded | `modelName` → modelName; `modelId` reported when `modelName` is empty |
| 19002 | Integrations > VTube Studio > Model Unloaded | vtubestudio.modelUnloaded | `modelId` reported when `modelName` is empty; `modelName` reported |
| 19009 | Integrations > VTube Studio > Tracking Status Changed | vtubestudio.trackingStatusChanged |  |
| 35011 | Kick > Channel > Follow | kick.follow |  |
| 35013 | Kick > Channel > Stream Offline | kick.streamOffline |  |
| 35012 | Kick > Channel > Stream Online | kick.streamOnline |  |
| 35023 | Kick > Channel > Viewer Count Update | kick.viewerCountUpdate | `min`, `max` reported |
| 35024 | Kick > Channel Reward > Reward Redemption | kick.rewardRedemption | `rewardId` reported |
| 35010 | Kick > Chat > Chat Message | kick.chatMessage |  |
| 35008 | Kick > Chat > First Words | users.firstWords | `username` filter reported |
| 35021 | Kick > Emotes > SevenTV Emote Added | kick.sevenTvEmoteAdded |  |
| 35022 | Kick > Emotes > SevenTV Emote Removed | kick.sevenTvEmoteRemoved |  |
| 35020 | Kick > General > Channel Update | kick.channelUpdate | `gameId` reported when `gameName` is empty; `gameOnly`, `gameName` reported |
| 35009 | Kick > General > Present Viewers | kick.presentViewers |  |
| 35025 | Kick > Kicks Gifted > Kicks Gifted | kick.kicksGifted | `min` → minKicks; `max` reported |
| 35019 | Kick > Moderation > User Banned | kick.userBanned |  |
| 35018 | Kick > Moderation > User Timed Out | kick.userTimedOut | `min`, `max` reported |
| 35016 | Kick > Subscriptions > Gift Subscription | kick.giftSubscription |  |
| 35017 | Kick > Subscriptions > Mass Gift Subscription | kick.massGiftSubscription | `min` → minGifts; `max` reported |
| 35015 | Kick > Subscriptions > Resubscription | kick.resubscription |  |
| 35014 | Kick > Subscriptions > Subscription | kick.subscription |  |
| 33001 | Meld Studio > Connected | meldstudio.connected | `meldStudioId` reported |
| 33002 | Meld Studio > Disconnected | meldstudio.disconnected | `meldStudioId` reported |
| 33012 | Meld Studio > Event | meldstudio.event | `eventName` → event; `meldStudioId` reported |
| 33005 | Meld Studio > Recording Started | meldstudio.recordingStarted | `meldStudioId` reported |
| 33006 | Meld Studio > Recording Stopped | meldstudio.recordingStopped | `meldStudioId` reported |
| 33007 | Meld Studio > Scene Changed | meldstudio.sceneChanged | `sceneName` → scene; `meldStudioId` reported |
| 33003 | Meld Studio > Streaming Started | meldstudio.streamingStarted | `meldStudioId` reported |
| 33004 | Meld Studio > Streaming Stopped | meldstudio.streamingStopped | `meldStudioId` reported |
| 14001 | OBS Studio > Connected | obs.connected | `obsId` → default connection |
| 14002 | OBS Studio > Disconnected | obs.disconnected | `obsId` → default connection |
| 14003 | OBS Studio > Event | obs.event | `eventName` → eventType; `obsId` → default connection |
| 14007 | OBS Studio > Recording Started | obs.recordStarted | `obsId` → default connection |
| 14008 | OBS Studio > Recording Stopped | obs.recordStopped | `obsId` → default connection |
| 14004 | OBS Studio > Scene Changed | obs.sceneChanged | `sceneName` → scene; `obsId` → default connection |
| 14005 | OBS Studio > Streaming Started | obs.streamStarted | `obsId` → default connection |
| 14006 | OBS Studio > Streaming Stopped | obs.streamStopped | `obsId` → default connection |
| 14009 | OBS Studio > Vendor Event | obs.vendorEvent | `vendorName` → vendor; `eventName` → vendorEventType; `obsId` → default connection |
| 22001 | Streamlabs Desktop > Connected | slobs.connected | `sdId` reported |
| 22002 | Streamlabs Desktop > Disconnected | slobs.disconnected | `sdId` reported |
| 22006 | Streamlabs Desktop > Recording Started | slobs.recordStarted | `sdId` reported |
| 22007 | Streamlabs Desktop > Recording Stopped | slobs.recordStopped | `sdId` reported |
| 22003 | Streamlabs Desktop > Scene Changed | slobs.sceneChanged | `sceneName` → scene; `sdId` reported |
| 22004 | Streamlabs Desktop > Streaming Started | slobs.streamStarted | `sdId` reported |
| 22005 | Streamlabs Desktop > Streaming Stopped | slobs.streamStopped | `sdId` reported |
| 139 | Twitch > Ads > Ad Run | twitch.adRun |  |
| 186 | Twitch > Ads > Upcoming Ad | twitch.upcomingAd | `minutes`: one mark kept, several → any |
| 101 | Twitch > Channel > Follow | twitch.follow |  |
| 155 | Twitch > Channel > Stream Offline | twitch.streamOffline |  |
| 154 | Twitch > Channel > Stream Online | twitch.streamOnline |  |
| 164 | Twitch > Channel > Viewer Count Update | twitch.viewerCountUpdate | `min` → minViewers; `max` reported |
| 148 | Twitch > Channel Goal > Goal Begin | twitch.goalBegin |  |
| 150 | Twitch > Channel Goal > Goal End | twitch.goalEnd |  |
| 149 | Twitch > Channel Goal > Goal Progress | twitch.goalProgress |  |
| 196 | Twitch > Channel Power-Ups > Power Up Redemption | twitch.powerUp | `powerUpType` → powerUpType |
| 194 | Twitch > Channel Reward > Automatic Reward Redemption | twitch.automaticRewardRedemption | `rewardType` → rewardType |
| 112 | Twitch > Channel Reward > Reward Redemption | twitch.rewardRedemption | `rewardId` reported: set the reward title |
| 171 | Twitch > Channel Reward > Reward Redemption Updated | twitch.rewardRedemptionUpdated | `rewardId` reported: set the reward title |
| 142 | Twitch > Charity > Charity Completed | twitch.charityCompleted |  |
| 141 | Twitch > Charity > Charity Donation | twitch.charityDonation | `min`, `max` reported |
| 147 | Twitch > Charity > Charity Progress | twitch.charityProgress |  |
| 146 | Twitch > Charity > Charity Started | twitch.charityStarted |  |
| 480 | Twitch > Chat > Bits Badge Tier | twitch.bitsBadgeTier |  |
| 140 | Twitch > Chat > Bot Whispers | twitch.botWhisper |  |
| 133 | Twitch > Chat > Chat Message | twitch.chatMessage |  |
| 102 | Twitch > Chat > Cheer | twitch.cheer | `min`/`max` → minBits/maxBits (max alone = exact) |
| 120 | Twitch > Chat > First Words | users.firstWords | `username` filter reported |
| 452 | Twitch > Chat > Mode > Emote Mode Off | twitch.emoteModeOff |  |
| 451 | Twitch > Chat > Mode > Emote Mode On | twitch.emoteModeOn |  |
| 455 | Twitch > Chat > Mode > Follower Mode Changed | twitch.followerModeChanged |  |
| 454 | Twitch > Chat > Mode > Follower Mode Off | twitch.followerModeOff |  |
| 453 | Twitch > Chat > Mode > Follower Mode On | twitch.followerModeOn |  |
| 458 | Twitch > Chat > Mode > Slow Mode Changed | twitch.slowModeChanged |  |
| 457 | Twitch > Chat > Mode > Slow Mode Off | twitch.slowModeOff |  |
| 456 | Twitch > Chat > Mode > Slow Mode On | twitch.slowModeOn |  |
| 460 | Twitch > Chat > Mode > Subscriber Mode Off | twitch.subscriberModeOff |  |
| 459 | Twitch > Chat > Mode > Subscriber Mode On | twitch.subscriberModeOn |  |
| 462 | Twitch > Chat > Mode > Unique Mode Off | twitch.uniqueModeOff |  |
| 461 | Twitch > Chat > Mode > Unique Mode On | twitch.uniqueModeOn |  |
| 497 | Twitch > Chat > Modiversary | twitch.modiversary | `min` → minMonths; `max` reported |
| 472 | Twitch > Chat > Watch Streak | twitch.watchStreak | `min` → minStreak; `max` reported |
| 119 | Twitch > Chat > Whispers | twitch.whisper |  |
| 184 | Twitch > Connections > Bot Chat Connected | twitch.botConnected |  |
| 185 | Twitch > Connections > Bot Chat Disconnected | twitch.botDisconnected |  |
| 173 | Twitch > Connections > Broadcaster Authenticated | twitch.broadcasterAuthenticated |  |
| 174 | Twitch > Connections > Broadcaster Chat Connected | twitch.broadcasterChatConnected |  |
| 175 | Twitch > Connections > Broadcaster Chat Disconnected | twitch.broadcasterChatDisconnected |  |
| 178 | Twitch > Connections > Broadcaster EventSub Connected | twitch.broadcasterConnected |  |
| 179 | Twitch > Connections > Broadcaster EventSub Disconnected | twitch.broadcasterDisconnected |  |
| 182 | Twitch > Emotes > BetterTTV Emote Added | chat.emoteAdded | provider BTTV |
| 183 | Twitch > Emotes > BetterTTV Emote Removed | chat.emoteRemoved | provider BTTV |
| 180 | Twitch > Emotes > SevenTV Emote Added | chat.emoteAdded | provider 7TV |
| 181 | Twitch > Emotes > SevenTV Emote Removed | chat.emoteRemoved | provider 7TV |
| 138 | Twitch > General > Announcement | twitch.announcement |  |
| 124 | Twitch > General > Present Viewers | twitch.presentViewers |  |
| 156 | Twitch > General > Shoutout Received | twitch.shoutoutReceived |  |
| 118 | Twitch > General > Stream Update | twitch.streamUpdate | `gameName` → categoryName; `gameOnly` reported (Cuebot also fires on title changes) |
| 167 | Twitch > Guest Star > Guest Star Guest Update | twitch.guestStarGuestUpdate | `state` → state |
| 165 | Twitch > Guest Star > Guest Star Session Begin | twitch.guestStarSessionBegin |  |
| 166 | Twitch > Guest Star > Guest Star Session End | twitch.guestStarSessionEnd |  |
| 169 | Twitch > Guest Star > Guest Star Settings Update | twitch.guestStarSettingsUpdate |  |
| 111 | Twitch > Hype Train > Hype Train End | twitch.hypeTrainEnd |  |
| 110 | Twitch > Hype Train > Hype Train Level Up | twitch.hypeTrainLevelUp | `min` → minLevel; `max` reported |
| 108 | Twitch > Hype Train > Hype Train Start | twitch.hypeTrainStart |  |
| 109 | Twitch > Hype Train > Hype Train Update | twitch.hypeTrainUpdate |  |
| 463 | Twitch > Moderation > AutoMod Message Held | twitch.automodMessageHeld |  |
| 464 | Twitch > Moderation > AutoMod Message Updated | twitch.automodMessageUpdated |  |
| 465 | Twitch > Moderation > Blocked Terms Added | twitch.blockedTermsAdded |  |
| 466 | Twitch > Moderation > Blocked Terms Deleted | twitch.blockedTermsDeleted |  |
| 157 | Twitch > Moderation > Chat Cleared | twitch.chatCleared |  |
| 135 | Twitch > Moderation > Chat Message Deleted | twitch.chatMessageDeleted |  |
| 187 | Twitch > Moderation > Moderator Added | twitch.moderatorAdded |  |
| 188 | Twitch > Moderation > Moderator Removed | twitch.moderatorRemoved |  |
| 469 | Twitch > Moderation > Permitted Terms Added | twitch.permittedTermsAdded |  |
| 470 | Twitch > Moderation > Permitted Terms Deleted | twitch.permittedTermsDeleted |  |
| 151 | Twitch > Moderation > Shield Mode Begin | twitch.shieldModeBegin |  |
| 152 | Twitch > Moderation > Shield Mode End | twitch.shieldModeEnd |  |
| 144 | Twitch > Moderation > Shoutout Created | twitch.shoutoutCreated |  |
| 495 | Twitch > Moderation > Suspicious User Message | twitch.suspiciousUserMessage |  |
| 468 | Twitch > Moderation > Suspicious User Update | twitch.suspiciousUserUpdate |  |
| 192 | Twitch > Moderation > Unban Request Approved | twitch.unbanRequestApproved |  |
| 195 | Twitch > Moderation > Unban Request Created | twitch.unbanRequestCreated |  |
| 193 | Twitch > Moderation > Unban Request Denied | twitch.unbanRequestDenied |  |
| 137 | Twitch > Moderation > User Banned | twitch.userBanned |  |
| 136 | Twitch > Moderation > User Timed Out | twitch.userTimedOut | `min` → minDuration; `max` reported |
| 191 | Twitch > Moderation > User Unbanned | twitch.userUnbanned |  |
| 145 | Twitch > Moderation > User Untimed Out | twitch.userUntimedOut |  |
| 189 | Twitch > Moderation > VIP Added | twitch.vipAdded |  |
| 190 | Twitch > Moderation > VIP Removed | twitch.vipRemoved |  |
| 467 | Twitch > Moderation > Warned User | twitch.userWarned |  |
| 471 | Twitch > Moderation > Warning Acknowledged | twitch.warningAcknowledged |  |
| 473 | Twitch > Polls > Poll Archived | twitch.pollArchived |  |
| 127 | Twitch > Polls > Poll Completed | twitch.pollCompleted |  |
| 125 | Twitch > Polls > Poll Created | twitch.pollCreated |  |
| 161 | Twitch > Polls > Poll Terminated | twitch.pollTerminated |  |
| 126 | Twitch > Polls > Poll Updated | twitch.pollUpdated |  |
| 131 | Twitch > Predictions > Prediction Canceled | twitch.predictionCanceled |  |
| 130 | Twitch > Predictions > Prediction Completed | twitch.predictionCompleted |  |
| 128 | Twitch > Predictions > Prediction Created | twitch.predictionCreated |  |
| 132 | Twitch > Predictions > Prediction Locked | twitch.predictionLocked |  |
| 129 | Twitch > Predictions > Prediction Updated | twitch.predictionUpdated |  |
| 163 | Twitch > Pyramid > Pyramid Broken | twitch.pyramidBroken |  |
| 162 | Twitch > Pyramid > Pyramid Success | twitch.pyramid |  |
| 107 | Twitch > Raid > Raid | twitch.raid | `min` → minViewers; `max` reported |
| 160 | Twitch > Raid > Raid Cancelled | twitch.raidCancelled |  |
| 159 | Twitch > Raid > Raid Send | twitch.raidSend |  |
| 158 | Twitch > Raid > Raid Start | twitch.raidStart |  |
| 481 | Twitch > Shared Chat > Shared Chat Announcement | twitch.sharedChatAnnouncement |  |
| 489 | Twitch > Shared Chat > Shared Chat Community Sub Gift | twitch.sharedChatGiftBomb | `tiers`, `min`, `max`, `subType` reported |
| 484 | Twitch > Shared Chat > Shared Chat Gift Paid Upgrade | twitch.sharedChatGiftPaidUpgrade |  |
| 494 | Twitch > Shared Chat > Shared Chat Message Deleted | twitch.sharedChatMessageDeleted |  |
| 485 | Twitch > Shared Chat > Shared Chat Pay It Forward | twitch.sharedChatPayItForward |  |
| 483 | Twitch > Shared Chat > Shared Chat Prime Paid Upgrade | twitch.sharedChatPrimePaidUpgrade |  |
| 482 | Twitch > Shared Chat > Shared Chat Raid | twitch.sharedChatRaid | `min`, `max` reported |
| 487 | Twitch > Shared Chat > Shared Chat Resub | twitch.sharedChatResub | `tiers`, `min`, `max` reported |
| 474 | Twitch > Shared Chat > Shared Chat Session Begin | twitch.sharedChatBegin |  |
| 476 | Twitch > Shared Chat > Shared Chat Session End | twitch.sharedChatEnd |  |
| 475 | Twitch > Shared Chat > Shared Chat Session Update | twitch.sharedChatUpdate |  |
| 486 | Twitch > Shared Chat > Shared Chat Sub | twitch.sharedChatSub | `tiers` reported |
| 488 | Twitch > Shared Chat > Shared Chat Sub Gift | twitch.sharedChatSubGift | `tiers`, `min`, `max`, `subType`, `monthsGifted` reported |
| 490 | Twitch > Shared Chat > Shared Chat User Banned | twitch.sharedChatUserBanned |  |
| 492 | Twitch > Shared Chat > Shared Chat User Timed Out | twitch.sharedChatUserTimedOut | `min`, `max` reported |
| 491 | Twitch > Shared Chat > Shared Chat User Unbanned | twitch.sharedChatUserUnbanned |  |
| 493 | Twitch > Shared Chat > Shared Chat User Untimed Out | twitch.sharedChatUserUntimedOut |  |
| 498 | Twitch > Shared Chat > Shared Modiversary | twitch.sharedChatModiversary | `min`, `max` reported |
| 106 | Twitch > Subscriptions > Gift Bomb | twitch.giftBomb | `tiers` → tier (16 = any; other masks reported); `min` → minTotal; `max`, `subType` reported |
| 479 | Twitch > Subscriptions > Gift Paid Upgrade | twitch.giftPaidUpgrade |  |
| 105 | Twitch > Subscriptions > Gift Subscription | twitch.subGift | `tiers` → tier (16 = any; other masks reported); `min`, `max`, `subType`, `monthsGifted` reported |
| 478 | Twitch > Subscriptions > Pay It Forward | twitch.payItForward |  |
| 477 | Twitch > Subscriptions > Prime Paid Upgrade | twitch.primePaidUpgrade |  |
| 104 | Twitch > Subscriptions > Resubscription | twitch.resub | `tiers` → tier (16 = any; other masks reported); `min` → minMonths; `max` reported |
| 121 | Twitch > Subscriptions > Sub Counter Rollover | twitch.subCounterRollover |  |
| 103 | Twitch > Subscriptions > Subscription | twitch.sub | `tiers` → tier (16 = any; other masks reported) |
| 4021 | YouTube > Broadcast > Broadcast Added | youtube.broadcastAdded |  |
| 4002 | YouTube > Broadcast > Broadcast Ended | youtube.broadcastEnded |  |
| 4020 | YouTube > Broadcast > Broadcast Monitoring Ended | youtube.broadcastMonitoringEnded |  |
| 4019 | YouTube > Broadcast > Broadcast Monitoring Started | youtube.broadcastMonitoringStarted |  |
| 4022 | YouTube > Broadcast > Broadcast Removed | youtube.broadcastRemoved |  |
| 4001 | YouTube > Broadcast > Broadcast Started | youtube.broadcastStarted |  |
| 4013 | YouTube > Broadcast > Broadcast Updated | youtube.broadcastUpdated |  |
| 4012 | YouTube > Broadcast > Statistics Updated | youtube.broadcastStatisticsUpdated |  |
| 4016 | YouTube > Chat > First Words | users.firstWords | `username` filter reported |
| 4030 | YouTube > Chat > Jewels Gifted | youtube.jewelsGifted | `min` → minJewels; `max` reported |
| 4003 | YouTube > Chat > Message | youtube.chatMessage |  |
| 4006 | YouTube > Chat > Super Chat | youtube.superChat | `min` → minAmount; `max` → maxAmount |
| 4007 | YouTube > Chat > Super Sticker | youtube.superSticker | `min`/`max` → minAmount/maxAmount |
| 4008 | YouTube > General > New Sponsor | youtube.newSponsor |  |
| 4018 | YouTube > General > New Subscriber | youtube.newSubscriber |  |
| 4017 | YouTube > General > Present Viewers | users.presentViewers | platform YouTube |
| 4011 | YouTube > General > Sponsor Only Ended | youtube.sponsorOnlyEnded |  |
| 4010 | YouTube > General > Sponsor Only Started | youtube.sponsorOnlyStarted |  |
| 4005 | YouTube > General > User Banned | youtube.userBanned |  |
| 4031 | YouTube > General > User Timed Out | youtube.userTimedOut | `min` → minDuration; `max` reported |
| 4015 | YouTube > Membership > Gift Membership Received | youtube.giftMembershipReceived |  |
| 4009 | YouTube > Membership > Member Milestone | youtube.memberMilestone |  |
| 4014 | YouTube > Membership > Membership Gift | youtube.membershipGift |  |
| 4027 | YouTube > Polls > Poll Closed | youtube.pollClosed |  |
| 4028 | YouTube > Polls > Poll Started | youtube.pollStarted |  |
| 4029 | YouTube > Polls > Poll Updated | youtube.pollUpdated |  |

### No Cuebot equivalent

These import as disabled placeholders with their original JSON.

| Code | Streamer.bot | Why |
| --- | --- | --- |
| 702 | Core > Test | only feeds test arguments to Streamer.bot's Test Trigger button; Cuebot runs actions by hand from the editor |
| 23001 | Integrations > Speaker.bot > Connected | Cuebot speaks itself and never connects to Speaker.bot |
| 23002 | Integrations > Speaker.bot > Disconnected | Cuebot speaks itself and never connects to Speaker.bot |
| 496 | Twitch > Channel Power-Ups > Custom Power-Up Redemption | Cuebot's Power-up trigger covers Twitch's built-in power-ups only |
| 116 | Twitch > Community Goal > Community Goal Contribution | Twitch retired community goals and Cuebot has no such event |
| 117 | Twitch > Community Goal > Community Goal Ended | Twitch retired community goals and Cuebot has no such event |
| 21401–21426 | Elgato > Camera Hub > Prompter > (all 26) | Cuebot controls webcams directly (UVC) and has no Elgato Prompter connection |
| 21353 | Elgato > Camera Hub > Webcam > Selected Webcam Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21357 | Elgato > Camera Hub > Webcam > Exposure Auto Lock Enabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21358 | Elgato > Camera Hub > Webcam > Exposure Auto Lock Disabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21359 | Elgato > Camera Hub > Webcam > Snapshot Taken | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21367 | Elgato > Camera Hub > Webcam > Noise Reduction Enabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21368 | Elgato > Camera Hub > Webcam > Noise Reduction Disabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21369 | Elgato > Camera Hub > Webcam > ISO Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21373 | Elgato > Camera Hub > Webcam > Lens Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21374 | Elgato > Camera Hub > Webcam > AR Lens Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21375 | Elgato > Camera Hub > Webcam > Bitrate Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21376 | Elgato > Camera Hub > Webcam > Flash Enabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21377 | Elgato > Camera Hub > Webcam > Flash Disabled | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21380 | Elgato > Camera Hub > Webcam > Overscan Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21384 | Elgato > Camera Hub > Webcam > White Balance Tint Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21387 | Elgato > Camera Hub > Webcam > Live ISO Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |
| 21390 | Elgato > Camera Hub > Webcam > Live White Balance Tint Changed | a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see |

Gaps in modules Cuebot already has: the Twitch community goal events (116, 117; retired by Twitch) and custom
power-up redemptions (496) would need new Twitch events; the 16 Camera Hub webcam events above are settings Camera
Hub itself exposes (ISO, flash, lenses, snapshots, ...) that Cuebot's direct UVC control does not read; the Prompter
needs a Camera Hub connection Cuebot does not have.

## Counts

Sub-action codes: 44 high, 14 medium. Trigger codes: 371 of Streamer.bot 1.0.7's 419 mapped, all verified (plus
18002); 4001 and 4002 are both a sub-action and a trigger code. On the public samples tried, about 98 % of sub-actions
and 94 % of triggers map (before the verified trigger codes: 72 %); the remaining trigger placeholders there are the
Test trigger (702) and 153, a code from an older Streamer.bot that 1.0.7 no longer offers.
