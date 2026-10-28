# Streamer.bot import: type codes

Streamer.bot export strings store each sub-action and trigger type as an undocumented number. These tables list
the codes the importer maps (`src/Cuebot.Core/Transfer/StreamerBot/`: sub-action rules in `StreamerBotRules.cs` and
`StreamerBotSubActionRules.cs`, trigger rules in `StreamerBotTriggerRules.cs`). Every code was verified against a
real **Streamer.bot 1.0.7** (2026-10-28) used as a black box: for triggers, each one added in its UI and its saved file
read back; for sub-actions, candidate codes imported into it and the ones it kept read back with the default settings
it fills in (see [Sub-actions](#sub-actions) and [Triggers](#triggers)). Which sub-action a code is was then worked
out from those fields, its menu, the public docs and real exports. No Streamer.bot code was used.

Codes not in these tables (and the ones listed as having no Cuebot equivalent) import as disabled placeholders with
their original JSON.

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

Verified against a real **Streamer.bot 1.0.7** (2026-10-28): candidate codes were imported into it, and its own saved
file shows which codes it keeps (all others it drops) and the default settings it fills in for each (field names,
types and defaults). That gives all **395** sub-action codes it accepts: 390 are the entries of its Add
Sub-Action menu, 5 are structure (group 99900, if/else branches 99901/99902, switch case 99903 and default 99904).
**351** map to a Cuebot sub-action with their settings, **44** have no Cuebot equivalent. The codes and default
settings are the test fixture `tests/Cuebot.Tests/TestData/streamerbot-1.0.7-subaction-defaults.json`;
`StreamerBotSubActionCodesTests` imports all of them and Cuebot.UiTests checks every mapped one against the sub-action
types, parameters and choices the app registers.

Streamer.bot does not store a sub-action's name, so each code was paired with its menu name from its fields, the
menu, the docs' parameter pages, the order Streamer.bot added sub-actions in (wiki changelogs), Streamer.bot's own
display text for codes 1–9 and real exports. Confidence of that pairing:

- **certain** (232): the fields (or the display text, or a real export's use) leave one reading.
- **likely** (105): the fields fit, and the cluster or the order the sub-actions were added in picks this one.
- **unsure** (58): a sibling with the same fields could be meant (for example OBS Set Source Mute State 40 and Set
  State of a Random Filter 42, or the Twitch empty-field "get" sub-actions); the order is a best guess.

Likely and unsure codes (146 of the mapped ones) are marked `Tentative`; the import report lists them as "type code
meaning inferred; please check". One Twitch menu name, **Get Latest Charity Campaign**, is left without a code by
this pairing (code 25 has the fields of the docs' Send Announcement, a second announcement layout next to 23).

Settings conventions:

- A state or mode is an index into the documented options: 0 Enabled / On / Visible, 1 Disabled / Off / Hidden,
  2 Toggle (Twitch chat modes, shield mode and branded content: `status` 0 on, 1 off). An index past Cuebot's
  options (for example a toggle where Cuebot can only switch on or off) imports as a placeholder.
- OBS Streaming (33) and Streamlabs Desktop Streaming (404): a real export ends the stream with state 0, so 0 Stop,
  1 Start (Recording 45 / 415 follow the same order, then Pause, Resume).
- Ids (all-zero GUID or null = not set): actions, commands, timers and queues in the export are resolved; rewards
  are passed on (Cuebot takes a reward id); decks, VTube Studio models/hotkeys, Wave Link inputs and the like use
  the name saved next to the id; sub-action, trigger, file tail, group, MIDI device, remote instance and voice
  control command ids are kept as text and reported. OBS, Streamlabs Desktop and Meld Studio connections use the
  default connection (reported when a specific one was chosen).
- Fields Cuebot's step cannot use (a random colour, an image on a Discord webhook, a pinned message, a reward
  cooldown's "additive", ...) are reported when they hold a value; the step is imported without them.
- Steps that cannot run as imported (no action, no category, no OBS request, no method, Keyboard Press keys) are
  imported disabled and reported.
- File / Folder Exists (1039 / 1040) set the same True/False argument Streamer.bot does (`fileExists` /
  `folderExists` or the chosen name) from Cuebot's two branches. Switch (127) turns each case's values into one
  case line (`a | b`) with its sub-actions, the default case into the Default branch.
- Delay (1002): `value` → milliseconds, `random` with `maxValue` → random up to "To".

| Code | Streamer.bot | Confidence | Cuebot type | Settings (Cuebot parameters set) |
| --- | --- | --- | --- | --- |
| 99902 | (structural) If/Else false branch | certain | core.group | name, random, color |
| 99901 | (structural) If/Else true branch | certain | core.group | name, random, color |
| 99900 | (structural) Sub-action group | certain | core.group | name, random, color |
| 99903 | (structural) Switch case | certain | core.group | name, random, color |
| 99904 | (structural) Switch default case | likely | core.group | name, random, color |
| 1005 | Core > Action Queues > Clear Action Queue | certain | core.clearActionQueue | queue |
| 1022 | Core > Action Queues > Set Action Queue Blocking State | certain | core.setActionQueueBlocking | queue, state |
| 1001 | Core > Action Queues > Set Action Queue Pause State | certain | core.setActionQueuePause | queue, state, clear |
| 1017 | Core > Actions > Get Action Group State | likely | core.getActionGroupState | group |
| 1016 | Core > Actions > Get Action State | certain | core.getActionState | action, name |
| 4 | Core > Actions > Run Action | certain | core.runAction | action, wait |
| 1011 | Core > Actions > Set Action Group State | likely | core.setActionGroupState | group, state |
| 1004 | Core > Actions > Set Action State | certain | core.setActionState | action, state |
| 1028 | Core > Actions > Set Sub-Action State | certain | core.setSubActionState | action, subAction, state |
| 1038 | Core > Actions > Set Sub-Action Weight | likely | core.setSubActionWeight | action, subAction, weight |
| 123 | Core > Arguments > Set Argument | certain | core.setArgument | name, value, autoType |
| 99999 | Core > C# > Execute C# Code | certain | code.csharp | code |
| 99998 | Core > C# > Execute C# Method | certain | code.csharp | code |
| 1015 | Core > Commands > Get Command Group State | likely | commands.getGroupState | group |
| 1014 | Core > Commands > Get Command State | certain | commands.getState | command |
| 1013 | Core > Commands > Get Commands | certain | commands.getCommands | argument, group, show, includeAll, onlyPermitted |
| 1010 | Core > Commands > Set Command Group State | likely | commands.setGroupState | group, state |
| 9 | Core > Commands > Set Command State | certain | commands.setState | command, state |
| 1002 | Core > Delay | certain | core.delay | milliseconds |
| 1039 | Core > File IO > File Exists | certain | core.fileExists | path, kind |
| 1040 | Core > File IO > Folder Exists | certain | core.fileExists | path, kind |
| 1006 | Core > File IO > Read Lines From File | certain | core.readLines | path, variable, parseVariables, autoType |
| 5 | Core > File IO > Read Random Line From File | certain | core.readRandomLine | path, variable, count, parseVariables, autoType |
| 1021 | Core > File IO > Read Specific Line From File | likely | core.readSpecificLine | path, variable, lineNumber, parseVariables, autoType |
| 3 | Core > File IO > Write To File | certain | core.writeFile | path, content, mode |
| 1030 | Core > File Tail > Set File Tail State | certain | core.setWatcherState | watcher, state |
| 125 | Core > Globals > Clear Non-Persisted Globals | likely | core.clearTemporaryGlobals |  |
| 126 | Core > Globals > Clear Non-Persisted User Globals | likely | core.clearTemporaryUserVariables |  |
| 121 | Core > Globals > Global (Get) | certain | core.getGlobal | name, destination, defaultValue, autoType |
| 122 | Core > Globals > Global (Set) | certain | core.setGlobal | name, mode, value, persisted, autoType |
| 124 | Core > Logic > Break | certain | core.stop |  |
| 1023 | Core > Logic > Get Input | certain | core.getInputDialog | title, prompt, originalText, variable, autoType |
| 1003 | Core > Logic > Get Random Number | certain | core.randomNumber | name, min, max |
| 120 | Core > Logic > If/Else | certain | core.if | left, operator, right |
| 129 | Core > Logic > Loop Break | likely | core.loopBreak |  |
| 130 | Core > Logic > Loop Continue | likely | core.loopContinue |  |
| 127 | Core > Logic > Switch | certain | core.switch | input, operator, cases |
| 128 | Core > Logic > While | certain | core.while | left, operator, right, maxIterations |
| 9002 | Core > MIDI > Control Change | certain | midi.controlChange | name, device, channel, controller, value |
| 9003 | Core > MIDI > Generic Event | certain | midi.genericEvent | name, device, channel, command, data1, data2 |
| 9001 | Core > MIDI > Note On | certain | midi.noteOn | name, device, channel, note, velocity, duration, sendNoteOff |
| 1009 | Core > Misc > Comment | certain | core.comment | text, color |
| 1020 | Core > Misc > Log | certain | core.log | message, level |
| 1007 | Core > Network > Fetch URL | certain | core.httpRequest | method, url, headers, variable, parseJson |
| 2 | Core > Network > UDP Broadcast | certain | core.udpBroadcast | port, payload |
| 1037 | Core > Quotes > Add Quote | certain | quotes.add | text |
| 1036 | Core > Quotes > Delete Quote | certain | quotes.delete | id |
| 1000 | Core > Quotes > Get Quote | certain | quotes.get | id |
| 1035 | Core > Quotes > Get Quote Count | likely | quotes.count |  |
| 1031 | Core > Signals > Send Local Signal | certain | core.sendSignal | name, includeArgs, arguments, queue |
| 1032 | Core > Signals > Wait For Local Signal | certain | core.waitForSignal | name, timeoutMs, overwrite |
| 1 | Core > Sounds > Play Sound | certain | audio.playSound | file, volume, wait, device, tag, useFileName |
| 7 | Core > Sounds > Play Sound From Folder | certain | audio.playSoundFromFolder | folder, subfolders, volume, wait, device, tag, useFileName |
| 30001 | Core > Sounds > Stop All Sound Playback | likely | audio.stopSounds |  |
| 30002 | Core > Sounds > Stop Sound Playback | certain | audio.stopSounds | tag |
| 1012 | Core > System > Keyboard Press | certain | input.keyboardPress | keys |
| 6 | Core > System > Run a Program | certain | core.runProgram | path, arguments, workingDirectory, wait, waitSeconds, captureOutput, environment |
| 1024 | Core > System > Toast Notification | certain | desktop.showNotification | title, message, icon, id |
| 8 | Core > Timers > Set Timer State | certain | timers.setState | timer, state |
| 1034 | Core > Triggers > Custom Event Trigger | certain | core.triggerCustomEvent | eventName, useArgs |
| 1033 | Core > Triggers > Set Trigger State | certain | core.setTriggerState | action, trigger, state |
| 10001 | Core > Users > Clear Users From Group | certain | groups.clear | group |
| 1018 | Core > Voice Control > Set Voice Control Command | certain | voiceControl.setCommand | command, phrase |
| 1019 | Core > Voice Control > Set Voice Control Command State | certain | voiceControl.setCommandState | command, state |
| 14312 | Elgato > Camera Hub > Webcam > Set Anti Flicker | certain | elgato.cameraHub.webcam.setAntiFlicker | device, antiFlicker |
| 14308 | Elgato > Camera Hub > Webcam > Set Auto White Balance | likely | elgato.cameraHub.webcam.setAutoWhiteBalance | device, mode |
| 14302 | Elgato > Camera Hub > Webcam > Set Brightness | certain | elgato.cameraHub.webcam.setBrightness | device, adjustment |
| 14301 | Elgato > Camera Hub > Webcam > Set Contrast | certain | elgato.cameraHub.webcam.setContrast | device, adjustment |
| 14307 | Elgato > Camera Hub > Webcam > Set Focus | certain | elgato.cameraHub.webcam.setFocus | device, adjustment |
| 14305 | Elgato > Camera Hub > Webcam > Set Pan | certain | elgato.cameraHub.webcam.setPan | device, adjustment |
| 14303 | Elgato > Camera Hub > Webcam > Set Saturation | certain | elgato.cameraHub.webcam.setSaturation | device, adjustment |
| 14313 | Elgato > Camera Hub > Webcam > Set Sharpness | certain | elgato.cameraHub.webcam.setSharpness | device, adjustment |
| 14306 | Elgato > Camera Hub > Webcam > Set Tilt | certain | elgato.cameraHub.webcam.setTilt | device, adjustment |
| 14309 | Elgato > Camera Hub > Webcam > Set White Balance | certain | elgato.cameraHub.webcam.setWhiteBalance | device, adjustment |
| 14304 | Elgato > Camera Hub > Webcam > Set Zoom | certain | elgato.cameraHub.webcam.setZoom | device, adjustment |
| 11002 | Elgato > Stream Deck > Set Background | certain | streamDeck.setBackground | buttonId, image, state |
| 11004 | Elgato > Stream Deck > Set State | certain | streamDeck.setState | buttonId, state |
| 11001 | Elgato > Stream Deck > Set Title | certain | streamDeck.setTitle | buttonId, title, state |
| 11008 | Elgato > Stream Deck > Set Value | certain | streamDeck.setValue | buttonId, value |
| 11005 | Elgato > Stream Deck > Show Alert | unsure | streamDeck.showAlert | buttonId |
| 11006 | Elgato > Stream Deck > Show Ok | unsure | streamDeck.showOk | buttonId |
| 11007 | Elgato > Stream Deck > Toggle State | unsure | streamDeck.toggleState | buttonId |
| 14016 | Elgato > Wave Link > Get Filter State | certain | wavelink.legacy.getFilterState | channel, effect |
| 14011 | Elgato > Wave Link > Get Input Information | certain | wavelink.legacy.getInputInfo | channel |
| 14012 | Elgato > Wave Link > Get Microphone Information | certain | wavelink.legacy.getMicrophoneInfo | inputDevice |
| 14009 | Elgato > Wave Link > Get Output Volumes | unsure | wavelink.legacy.getOutputVolumes |  |
| 14007 | Elgato > Wave Link > Get Selected Output | unsure | wavelink.legacy.getSelectedOutput |  |
| 14003 | Elgato > Wave Link > Mute Input | likely | wavelink.legacy.muteInput | channel, mix, state |
| 14001 | Elgato > Wave Link > Mute Microphone | certain | wavelink.legacy.muteMicrophone | inputDevice, state |
| 14002 | Elgato > Wave Link > Mute Output | certain | wavelink.legacy.muteOutput | mix, state |
| 14004 | Elgato > Wave Link > Set Filter Bypass State | likely | wavelink.legacy.setFilterBypass | channel, mix, state |
| 14005 | Elgato > Wave Link > Set Filter State | certain | wavelink.legacy.setFilterState | channel, effect, state |
| 14010 | Elgato > Wave Link > Set Input Volume | certain | wavelink.legacy.setInputVolume | channel, mix, volume, relative |
| 14015 | Elgato > Wave Link > Set Microphone Balance | certain | wavelink.legacy.setMicrophoneBalance | inputDevice, balance, relative |
| 14013 | Elgato > Wave Link > Set Microphone Gain | certain | wavelink.legacy.setMicrophoneGain | inputDevice, gain, relative |
| 14014 | Elgato > Wave Link > Set Microphone Output Volume | certain | wavelink.legacy.setMicrophoneOutputVolume | inputDevice, volume, relative |
| 14006 | Elgato > Wave Link > Set Output Monitor Device | certain | wavelink.legacy.setOutputMonitorDevice | outputDevice |
| 14008 | Elgato > Wave Link > Set Output Volume | certain | wavelink.legacy.setOutputVolume | mix, volume, relative |
| 7001 | Integrations > Discord > Basic Webhook | certain | discord.basicWebhook | name, webhookUrl, content, username, avatarUrl, textToSpeech |
| 8002 | Integrations > IFTTT > IFTTT Notification (Advanced) | certain | ifttt.notificationAdvanced | eventName, arguments |
| 8001 | Integrations > IFTTT > IFTTT Notification (Basic) | certain | ifttt.notificationBasic | eventName, text |
| 6001 | Integrations > Lumia Stream > Send Command | certain | lumia.sendCommand | command |
| 6002 | Integrations > Lumia Stream > Set Color | certain | lumia.setColor | color, brightness, duration, transition, default, skipQueue, lights |
| 6004 | Integrations > Lumia Stream > Set To Default | likely | lumia.setToDefault |  |
| 40000 | Integrations > mtion > Get Active Clubhouse | certain | mtion.getActiveClubhouse |  |
| 40001 | Integrations > mtion > Send Trigger | certain | mtion.sendTrigger | trigger, parameters |
| 15001 | Integrations > Pronouns > Add Pronouns for User | certain | pronouns.addForUser | user, prefix |
| 19001 | Integrations > Throwing System > Activate Trigger | certain | tits.activateTrigger | trigger |
| 19002 | Integrations > Throwing System > Activate Trigger by Name | certain | tits.activateTriggerByName | trigger |
| 19003 | Integrations > Throwing System > Throw Item | certain | tits.throwItem | item, delay, amount |
| 19004 | Integrations > Throwing System > Throw Item by Name | certain | tits.throwItemByName | item, delay, amount |
| 19005 | Integrations > Throwing System > Throw Items | likely | tits.throwItems | items, delay, amount |
| 19006 | Integrations > Throwing System > Throw Items by Name | likely | tits.throwItemsByName | items, delay, amount |
| 5001 | Integrations > VoiceMod > Get Current Voice | likely | voicemod.getCurrentVoice |  |
| 5010 | Integrations > VoiceMod > Play Sound | certain | voicemod.playSound | sound |
| 5003 | Integrations > VoiceMod > Select Random Voice | certain | voicemod.selectRandomVoice | mode |
| 5002 | Integrations > VoiceMod > Select Voice | certain | voicemod.selectVoice | voice |
| 5009 | Integrations > VoiceMod > Select Voice By Id | certain | voicemod.selectVoice | voice |
| 5012 | Integrations > VoiceMod > Select Voice by Name | certain | voicemod.selectVoice | voice |
| 5005 | Integrations > VoiceMod > Set Background Effect State | unsure | voicemod.setBackgroundEffects | state |
| 5007 | Integrations > VoiceMod > Set Censor State | unsure | voicemod.setCensorBeep | state |
| 5006 | Integrations > VoiceMod > Set Hear My Voice State | unsure | voicemod.setHearMyself | state |
| 5008 | Integrations > VoiceMod > Set Mute State | unsure | voicemod.setMute | state |
| 5004 | Integrations > VoiceMod > Set Voice Changer State | unsure | voicemod.setVoiceChanger | state |
| 5011 | Integrations > VoiceMod > Stop All Sounds | likely | voicemod.stopAllSounds |  |
| 12007 | Integrations > VTube Studio > Color Tint | certain | vtubestudio.colorTint | color, nameContains, tagContains, mix |
| 12006 | Integrations > VTube Studio > Get Current Model | likely | vtubestudio.getCurrentModel |  |
| 12001 | Integrations > VTube Studio > Load Model | certain | vtubestudio.loadModel | model |
| 12002 | Integrations > VTube Studio > Load Model by Name | certain | vtubestudio.loadModel | model |
| 12005 | Integrations > VTube Studio > Move Model | certain | vtubestudio.moveModel | time, relative, positionX, positionY, rotation, size |
| 12008 | Integrations > VTube Studio > Reset All Color Tints | likely | vtubestudio.resetColorTint |  |
| 12010 | Integrations > VTube Studio > Send Raw Request | certain | vtubestudio.rawRequest | messageType, data, resultArgument, variablePrefix |
| 12009 | Integrations > VTube Studio > Set Expression State | certain | vtubestudio.setExpression | expression, state, fadeTime |
| 12003 | Integrations > VTube Studio > Trigger Hotkey | certain | vtubestudio.triggerHotkey | hotkey |
| 12004 | Integrations > VTube Studio > Trigger Hotkey by Name | certain | vtubestudio.triggerHotkey | hotkey |
| 35031 | Kick > Channel > Set Channel Category | certain | kick.setCategory | category |
| 35030 | Kick > Channel > Set Channel Title | certain | kick.setTitle | title |
| 35002 | Kick > Chat > Reply To Message | certain | kick.replyToMessage | message, replyTo, useBot, fallback |
| 35001 | Kick > Chat > Send Message to Channel | certain | kick.sendMessage | message, useBot, fallback |
| 35040 | Kick > Moderation > Ban User | certain | kick.banUser | user, reason |
| 35042 | Kick > Moderation > Timeout User | certain | kick.timeoutUser | user, duration, reason |
| 35041 | Kick > Moderation > Unban User | likely | kick.unbanUser | user |
| 35043 | Kick > Moderation > UnTimeout User | likely | kick.untimeoutUser | user |
| 35108 | Kick > Rewards > Get Reward Info | likely | kick.getRewardInfo | reward |
| 35113 | Kick > Rewards > Set Background Color | certain | kick.setRewardColor | reward, color |
| 35103 | Kick > Rewards > Set Cost | certain | kick.setRewardCost | reward, cost |
| 35106 | Kick > Rewards > Set Description | certain | kick.setRewardDescription | reward, description |
| 35101 | Kick > Rewards > Set Enabled State | likely | kick.setRewardEnabled | reward, enabled |
| 35102 | Kick > Rewards > Set Paused State | likely | kick.setRewardPaused | reward, paused |
| 35105 | Kick > Rewards > Set Title | certain | kick.setRewardTitle | reward, title |
| 35107 | Kick > Rewards > Update | certain | core.group | name, random, color |
| 35010 | Kick > User > Add Broadcaster Information | certain | kick.addBroadcasterInfo |  |
| 35012 | Kick > User > Add Random Users | certain | kick.addRandomUsers | count, excludeCaller, prefix |
| 35011 | Kick > User > Get User Info for Target | certain | kick.getUserInfo | user, prefix |
| 35020 | Kick > User Groups > Add User To Group | likely | kick.addUserToGroup | group, user, userId, create |
| 35021 | Kick > User Groups > Remove User from Group | likely | kick.removeUserFromGroup | group, user, userId |
| 35022 | Kick > User Groups > User In Group | likely | kick.userInGroup | group, user, userId, argument |
| 41007 | Meld Studio > Effects > Set Effect Enabled State | certain | meldstudio.setEffectState | scene, layer, effect, state |
| 41001 | Meld Studio > Get Current Scene | unsure | meldstudio.getCurrentScene |  |
| 41002 | Meld Studio > Get Status | unsure | meldstudio.getStatus |  |
| 41014 | Meld Studio > Layers > Set Browser Layer Url | unsure | meldstudio.setBrowserLayerUrl | scene, layer, url |
| 41006 | Meld Studio > Layers > Set Layer Visibility State | certain | meldstudio.setLayerVisibility | scene, layer, state |
| 41017 | Meld Studio > Record Clip | unsure | meldstudio.recordClip |  |
| 41003 | Meld Studio > Recording | unsure | meldstudio.recording | state |
| 41005 | Meld Studio > Show Scene | certain | meldstudio.showScene | scene |
| 41004 | Meld Studio > Streaming | unsure | meldstudio.streaming | state |
| 41010 | Meld Studio > Take Screenshot | likely | meldstudio.takeScreenshot | orientation |
| 41013 | Meld Studio > Take Vertical Screenshot | unsure | meldstudio.takeVerticalScreenshot |  |
| 41008 | Meld Studio > Tracks > Set Layer Track Muted State | certain | meldstudio.setLayerTrackMuted | scene, layer, state |
| 41009 | Meld Studio > Tracks > Set Track Muted State | certain | meldstudio.setTrackMuted | track, state |
| 329 | OBS Studio > Create Record Chapter | certain | obs.createRecordChapter | chapterName, connection |
| 42 | OBS Studio > Filters > Set State of a Random Filter | unsure | obs.setRandomFilterState | connection, source, state, resultArgument |
| 43 | OBS Studio > Get Current Scene | likely | obs.getCurrentScene | connection, resultArgument |
| 328 | OBS Studio > Get Status | certain | obs.getStatus | connection |
| 31 | OBS Studio > Groups > Set Random Group Source Visible | unsure | obs.randomGroupSource | connection, group, resultArgument |
| 46 | OBS Studio > Raw | certain | obs.rawRequest | requestType, requestData, resultArgument, connection |
| 45 | OBS Studio > Recording | likely | obs.recording | connection, mode |
| 321 | OBS Studio > Replay > Set Replay Buffer State | likely | obs.replayBuffer | connection, mode |
| 324 | OBS Studio > Scenes > Get Scene Item Properties | likely | obs.getSceneItemProperties | connection, scene, source |
| 325 | OBS Studio > Scenes > Hide Scene's Sources | likely | obs.hideSceneSources | connection, scene |
| 326 | OBS Studio > Scenes > Set Random Scene Source Visible | likely | obs.randomSceneSource | connection, scene, resultArgument |
| 47 | OBS Studio > Scenes > Set Scene Filter State | certain | obs.setFilterState | source, filter, state, connection |
| 34 | OBS Studio > Set Active Scene | certain | obs.setScene | scene, connection |
| 36 | OBS Studio > Sources > Flip Source | certain | obs.flipSource | connection, scene, source, axis |
| 32 | OBS Studio > Sources > Hide Group's Sources | unsure | obs.hideGroupSources | connection, group |
| 41 | OBS Studio > Sources > Hide Source's Filters | likely | obs.setAllFiltersState | connection, source, state |
| 37 | OBS Studio > Sources > Rotate Source | certain | obs.rotateSource | connection, scene, source, degrees, relative |
| 38 | OBS Studio > Sources > Set Browser Source URL | certain | obs.setBrowserUrl | input, url, connection |
| 327 | OBS Studio > Sources > Set Color Source Color | certain | obs.setColor | connection, input, color |
| 39 | OBS Studio > Sources > Set GDI Text | certain | obs.setText | input, text, connection |
| 323 | OBS Studio > Sources > Set Image Source File | likely | obs.setImageFile | input, file, connection |
| 322 | OBS Studio > Sources > Set Media Source File | certain | obs.setMediaFile | input, file, connection |
| 49 | OBS Studio > Sources > Set Media State | likely | obs.mediaControl | connection, input, command |
| 44 | OBS Studio > Sources > Set Source Audio Track State | certain | obs.setAudioTrack | connection, input, track, state |
| 35 | OBS Studio > Sources > Set Source Filter State | certain | obs.setFilterState | source, filter, state, connection |
| 40 | OBS Studio > Sources > Set Source Mute State | unsure | obs.setInputMute | connection, input, state |
| 30 | OBS Studio > Sources > Set Source Visibility State | certain | obs.setSourceVisibility | scene, source, state, connection |
| 33 | OBS Studio > Streaming | likely | obs.streaming | connection, mode |
| 48 | OBS Studio > Take Screenshot | certain | obs.screenshot | connection, source, file, quality, resultArgument |
| 701 | PolyPop > Trigger Alert | certain | polypop.triggerAlert | alertName, variables |
| 1008 | Settings > Pick Color | certain | core.pickColor | color, name |
| 1026 | Settings > Reset Credits | likely | credits.reset |  |
| 1027 | Settings > Reset First Words | likely | users.resetFirstWords | platform |
| 1025 | Settings > Set Voice Control Input | certain | voiceControl.setInput | device |
| 607 | Speaker.bot > Activate VoiceGate Profile | certain | speech.activateVoiceGate | profile |
| 606 | Speaker.bot > Clear Pending | unsure | speech.clearQueue |  |
| 603 | Speaker.bot > Set Enabled State | unsure | speech.setEnabled | mode |
| 604 | Speaker.bot > Set Paused State | unsure | speech.pause | mode |
| 602 | Speaker.bot > Speak | likely | speech.speak | alias, text, badWordFilter, silent |
| 601 | Speaker.bot > Speak (UDP) | likely | speech.speak | alias, text, badWordFilter |
| 605 | Speaker.bot > Stop Speaking | unsure | speech.skip |  |
| 21007 | Streamer.bot > Decks > Deck Notify | certain | decks.notify | deck, title, description |
| 21004 | Streamer.bot > Decks > Item Set Background | certain | decks.itemSetBackground | deck, item, color, url, state |
| 21005 | Streamer.bot > Decks > Item Set Icon | certain | decks.itemSetIcon | deck, item, color, name, url, state |
| 21001 | Streamer.bot > Decks > Item Set State | certain | decks.itemSetState | deck, item, state |
| 21003 | Streamer.bot > Decks > Item Set Title | certain | decks.itemSetTitle | deck, item, title, color, state |
| 21006 | Streamer.bot > Decks > Item Set Value | certain | decks.itemSetValue | deck, item, value, state |
| 21002 | Streamer.bot > Decks > Item Toggle State | certain | decks.itemToggleState | deck, item |
| 21009 | Streamer.bot > Decks > Page Next | unsure | decks.pageNext | deck |
| 21010 | Streamer.bot > Decks > Page Previous | unsure | decks.pagePrevious | deck |
| 21008 | Streamer.bot > Decks > Page Set | certain | decks.pageSet | deck, page |
| 20002 | Streamer.bot > Remote > Send Remote Signal | certain | remote.sendSignal | instance, name, includeArgs, arguments |
| 20003 | Streamer.bot > Remote > Wait For Remote Signal | certain | remote.waitForSignal | instance, name, timeoutMs, overwrite |
| 413 | Streamlabs Desktop > Filters > Set State of a Random Filter | unsure | slobs.setRandomFilterState | scene, source, state, resultArgument |
| 403 | Streamlabs Desktop > Groups > Hide Group's Sources | unsure | slobs.hideGroupSources | scene, group |
| 402 | Streamlabs Desktop > Groups > Set Random Group Source Visible | unsure | slobs.randomGroupSource | scene, group, resultArgument |
| 415 | Streamlabs Desktop > Recording | likely | slobs.recording | mode |
| 414 | Streamlabs Desktop > Scenes > Get Current Scene | likely | slobs.getCurrentScene | resultArgument |
| 405 | Streamlabs Desktop > Scenes > Set Active Scene | certain | slobs.setScene | scene |
| 417 | Streamlabs Desktop > Scenes > Set Scene Filter State | certain | slobs.setSceneFilterState | scene, filter, state |
| 407 | Streamlabs Desktop > Sources > Flip Source | certain | slobs.flipSource | scene, source, axis |
| 412 | Streamlabs Desktop > Sources > Hide Source's Filters | likely | slobs.hideSourceFilters | scene, source |
| 408 | Streamlabs Desktop > Sources > Rotate Source | certain | slobs.rotateSource | scene, source, degrees |
| 409 | Streamlabs Desktop > Sources > Set Browser Source URL | certain | slobs.setBrowserUrl | source, url |
| 410 | Streamlabs Desktop > Sources > Set GDI Text | certain | slobs.setText | source, text |
| 406 | Streamlabs Desktop > Sources > Set Source Filter State | certain | slobs.setFilterState | source, filter, state |
| 411 | Streamlabs Desktop > Sources > Set Source Mute State | unsure | slobs.setSourceMute | source, state |
| 401 | Streamlabs Desktop > Sources > Set Source Visibility State | certain | slobs.setSourceVisibility | scene, source, state |
| 404 | Streamlabs Desktop > Streaming | likely | slobs.streaming | mode |
| 555 | Twitch > Ads > Get Ad Schedule | unsure | twitch.getAdSchedule |  |
| 556 | Twitch > Ads > Get Pre-roll Free Time | unsure | twitch.getAdSchedule |  |
| 19 | Twitch > Ads > Run Commercial | certain | twitch.runAd | length |
| 557 | Twitch > Ads > Snooze Ad | unsure | twitch.snoozeAd |  |
| 24 | Twitch > Channel > Add Viewer Count | likely | twitch.getStreamInfo |  |
| 18 | Twitch > Channel > Create Clip | certain | twitch.createClip | hasDelay |
| 17 | Twitch > Channel > Create Stream Marker | certain | twitch.createMarker | description |
| 564 | Twitch > Channel > Set Channel Branded Content | likely | twitch.setBrandedContent | enabled |
| 16 | Twitch > Channel > Set Channel Game | certain | twitch.setGame | game |
| 15 | Twitch > Channel > Set Channel Title | certain | twitch.setTitle | title |
| 510 | Twitch > Chat > Clear Chat Messages | unsure | twitch.clearChat |  |
| 575 | Twitch > Chat > Get Pinned Chat Message | unsure | twitch.getPinnedMessage |  |
| 576 | Twitch > Chat > Pin Chat Message | certain | twitch.pinMessage | messageId, duration |
| 560 | Twitch > Chat > Reply To Message | certain | twitch.replyToMessage | message, messageId, sendAs |
| 23 | Twitch > Chat > Send Announcement to Channel | likely | twitch.sendAnnouncement | message, color |
| 25 | Twitch > Chat > Send Announcement to Channel (older layout?) | unsure | twitch.sendAnnouncement | message, color |
| 10 | Twitch > Chat > Send Message to Channel | certain | twitch.sendMessage | message, sendAs |
| 578 | Twitch > Chat > Unpin Chat Message | unsure | twitch.unpinMessage |  |
| 577 | Twitch > Chat > Updated Pinned Chat Message Duration | certain | twitch.updatePinnedDuration | messageId, duration |
| 51 | Twitch > Followers > Get Follow Age Info for Target | certain | twitch.getFollowAge | user |
| 506 | Twitch > Followers > Get Follower Count | certain | twitch.getFollowerCount |  |
| 508 | Twitch > Followers > Get Latest Follower | unsure | twitch.getFollowerCount |  |
| 528 | Twitch > Guest Star > Assign Guest Star Slot | likely | twitch.assignGuestStarSlot | user, slot |
| 523 | Twitch > Guest Star > Create Guest Star Session | likely | twitch.createGuestStarSession |  |
| 527 | Twitch > Guest Star > Delete Guest Star Invite | likely | twitch.deleteGuestStarInvite | user |
| 530 | Twitch > Guest Star > Delete Guest Star Slot | likely | twitch.deleteGuestStarSlot | user, slot |
| 524 | Twitch > Guest Star > End Guest Star Session | likely | twitch.endGuestStarSession |  |
| 520 | Twitch > Guest Star > Get Channel Guest Star Settings | likely | twitch.getGuestStarSettings |  |
| 525 | Twitch > Guest Star > Get Guest Star Invites | likely | twitch.getGuestStarInvites |  |
| 522 | Twitch > Guest Star > Get Guest Star Session | likely | twitch.getGuestStarSession |  |
| 526 | Twitch > Guest Star > Send Guest Star Invite | likely | twitch.sendGuestStarInvite | user |
| 521 | Twitch > Guest Star > Update Channel Guest Star Settings | certain | twitch.updateGuestStarSettings | slotCount, layout, moderatorSendLive, browserSourceAudio, regenerateBrowserSources |
| 529 | Twitch > Guest Star > Update Guest Star Slot | certain | twitch.updateGuestStarSlot | slot, toSlot |
| 531 | Twitch > Guest Star > Update Guest Star Slot Settings | certain | twitch.updateGuestStarSlotSettings | slot, audio, video, live, volume |
| 504 | Twitch > Moderation > Add Moderator | unsure | twitch.addModerator | user |
| 502 | Twitch > Moderation > Add VIP | unsure | twitch.addVip | user |
| 511 | Twitch > Moderation > Ban User | certain | twitch.ban | user, reason |
| 13 | Twitch > Moderation > Chat Modes > Emote Only | likely | twitch.emoteOnly | enabled |
| 28 | Twitch > Moderation > Chat Modes > Follow Mode | likely | twitch.followerMode | enabled, minutes |
| 14 | Twitch > Moderation > Chat Modes > Slow Mode | likely | twitch.slowMode | enabled, seconds |
| 12 | Twitch > Moderation > Chat Modes > Subscriber Only | likely | twitch.subscriberMode | enabled |
| 505 | Twitch > Moderation > Remove Moderator | unsure | twitch.removeModerator | user |
| 503 | Twitch > Moderation > Remove VIP | unsure | twitch.removeVip | user |
| 29 | Twitch > Moderation > Send Shoutout | certain | twitch.sendShoutout | user |
| 26 | Twitch > Moderation > Shield > Get Shield Mode Status | likely | twitch.getShieldMode |  |
| 27 | Twitch > Moderation > Shield > Update Shield Mode Status | likely | twitch.setShieldMode | enabled |
| 20 | Twitch > Moderation > Timeout User | certain | twitch.timeout | user, duration, reason |
| 512 | Twitch > Moderation > Unban User | likely | twitch.unban | user |
| 513 | Twitch > Moderation > UnTimeout User | likely | twitch.untimeout | user |
| 565 | Twitch > Moderation > Warn User | certain | twitch.warn | user, reason |
| 563 | Twitch > Polls > Archive Active Poll | unsure | twitch.endPoll | mode |
| 561 | Twitch > Polls > Create Poll | certain | twitch.createPoll | title, choices, duration, pointsPerVote |
| 566 | Twitch > Polls > Get Active Poll | unsure | twitch.getPoll |  |
| 562 | Twitch > Polls > Terminate Active Poll | unsure | twitch.endPoll | mode |
| 570 | Twitch > Predictions > Cancel Active Prediction | unsure | twitch.cancelPrediction |  |
| 568 | Twitch > Predictions > Create Prediction | certain | twitch.createPrediction | title, outcomes, window |
| 567 | Twitch > Predictions > Get Active Prediction | unsure | twitch.getPrediction |  |
| 569 | Twitch > Predictions > Lock Active Prediction | unsure | twitch.lockPrediction |  |
| 571 | Twitch > Predictions > Resolve Last Prediction | certain | twitch.resolvePrediction | winner, title |
| 572 | Twitch > Predictions > Resolve Last Prediction By Title | certain | twitch.resolvePrediction | winner, title |
| 501 | Twitch > Raid > Cancel Raid | likely | twitch.cancelRaid |  |
| 500 | Twitch > Raid > Start Raid | likely | twitch.startRaid | user |
| 203 | Twitch > Rewards > Configure Rewards | certain | twitch.configureRewards | disableNotConfigured |
| 208 | Twitch > Rewards > Get Reward Info | likely | twitch.getRewardInfo | reward |
| 209 | Twitch > Rewards > Reset Reward Counter | likely | twitch.resetRewardCounter | reward |
| 210 | Twitch > Rewards > Reset Reward User Counters | likely | twitch.resetRewardUserCounters | reward |
| 213 | Twitch > Rewards > Set Background Color | certain | twitch.setRewardColor | reward, color, random |
| 202 | Twitch > Rewards > Set Cost | certain | twitch.updateReward | reward, cost, operator |
| 200 | Twitch > Rewards > Set Enabled State | likely | twitch.setRewardEnabled | reward, enabled |
| 204 | Twitch > Rewards > Set Global Cooldown | certain | twitch.setRewardCooldown | reward, seconds |
| 211 | Twitch > Rewards > Set Group Enabled State | likely | twitch.setRewardGroupEnabled | group, state |
| 212 | Twitch > Rewards > Set Group Paused State | likely | twitch.setRewardGroupPaused | group, state |
| 214 | Twitch > Rewards > Set Max Per Stream | likely | twitch.setRewardMaxPerStream | reward, max, additive |
| 215 | Twitch > Rewards > Set Max per User per Stream | likely | twitch.setRewardMaxPerUser | reward, max, additive |
| 201 | Twitch > Rewards > Set Paused State | likely | twitch.setRewardPaused | reward, paused |
| 206 | Twitch > Rewards > Set Prompt | certain | twitch.setRewardPrompt | reward, prompt |
| 205 | Twitch > Rewards > Set Title | certain | twitch.setRewardTitle | reward, title |
| 207 | Twitch > Rewards > Update | certain | twitch.updateReward | reward, title, prompt, cost, operator |
| 11 | Twitch > Rewards > Update Redemption Status | likely | twitch.updateRedemption | rewardId, redemptionId, status |
| 551 | Twitch > Sub Counter > Get Twitch Sub Counter Count | likely | twitch.getSubCounter |  |
| 552 | Twitch > Sub Counter > Set Twitch Sub Counter Count | likely | twitch.setSubCounter | count |
| 509 | Twitch > Subscribers > Get Latest Subscriber | unsure | twitch.getLatestSubscriber |  |
| 507 | Twitch > Subscribers > Get Subscriber Count | unsure | twitch.getSubscriberCount |  |
| 253 | Twitch > Tags > Add Channel Tag | likely | twitch.addTag | tag |
| 251 | Twitch > Tags > Clear Channel Tags | certain | twitch.clearTags |  |
| 254 | Twitch > Tags > Remove Channel Tag | likely | twitch.removeTag | tag |
| 252 | Twitch > Tags > Set Channel Tags | certain | twitch.setTags | tags |
| 21 | Twitch > User > Add Broadcaster Information | likely | twitch.getBroadcasterInfo |  |
| 550 | Twitch > User > Add Present User | certain | twitch.getPresentUser | index |
| 22 | Twitch > User > Add Random Users | certain | twitch.getRandomUsers | count, excludeBroadcaster |
| 52 | Twitch > User > Get Team Info for Target | likely | twitch.getTeamInfo | user |
| 50 | Twitch > User > Get User Info for Target | certain | twitch.getUserInfo | user |
| 553 | Twitch > User Groups > Add User To Group | likely | groups.addUser | group, platform, user, userId, create |
| 554 | Twitch > User Groups > Remove User from Group | likely | groups.removeUser | group, platform, user, userId |
| 558 | Twitch > User Groups > User In Group | likely | groups.userInGroup | group, platform, user, userId, argument |
| 4008 | YouTube > Moderation > Ban User | certain | youtube.banUser | user, broadcastId |
| 4009 | YouTube > Moderation > Timeout User | certain | youtube.timeoutUser | user, duration, broadcastId |
| 4011 | YouTube > Polls > Create Poll | certain | youtube.createPoll | question, options, broadcastId |
| 4012 | YouTube > Polls > End Poll | certain | youtube.endPoll | pollMessageId |
| 4001 | YouTube > Send Message to Channel | certain | youtube.sendMessage | message, useBot, fallback, broadcastId |
| 4003 | YouTube > Set Description | certain | youtube.setDescription | description, broadcastId |
| 4002 | YouTube > Set Title | certain | youtube.setTitle | title, broadcastId |
| 4004 | YouTube > Set Title and Description | certain | youtube.setTitleAndDescription | title, description, broadcastId |
| 4010 | YouTube > User > Add Broadcaster Information | likely | youtube.addBroadcasterInfo |  |
| 4005 | YouTube > User Groups > Add User To Group | likely | groups.addUser | group, platform, user, userId, create |
| 4006 | YouTube > User Groups > Remove User from Group | likely | groups.removeUser | group, platform, user, userId |
| 4007 | YouTube > User Groups > User In Group | likely | groups.userInGroup | group, platform, user, userId, argument |

If/Else operators (120), in the documented order: 0 Equals, 1 Not Equals, 2 Contains, 3 Regex Match, 4 Less Than,
5 Greater Than, 6 Does Not Exist, 7 Equals (ignore case), 8 Not Equals (ignore case), 9 Is Null or Empty. Usage in
the exports confirms 0, 1, 2, 3, 4, 6, 8 and 9; 5 and 7 follow from the order. Cuebot compares text
case-insensitively, so 0/7 and 1/8 map to the same operator. "Does Not Exist" becomes
`$parse(name)$` equals `\$parse(name)$`, which holds exactly when the argument is unset. Older exports name a bare
argument in `variableName`; it becomes `%name%`. While (128): 0 Equals, 1 Not Equals, 2 Greater Than, 3 Less Than
(3 from a real export).

### Sub-actions with no Cuebot equivalent

These import as disabled placeholders with their original JSON.

| Code | Streamer.bot | Confidence | Why |
| --- | --- | --- | --- |
| 14401 | Elgato > Camera Hub > EpocCam > Set AR Lens | certain | Cuebot has no EpocCam (phone camera) control |
| 14403 | Elgato > Camera Hub > EpocCam > Set Bitrate | certain | Cuebot has no EpocCam (phone camera) control |
| 14402 | Elgato > Camera Hub > EpocCam > Set Camera | certain | Cuebot has no EpocCam (phone camera) control |
| 14404 | Elgato > Camera Hub > EpocCam > Set Flash Mode | certain | Cuebot has no EpocCam (phone camera) control |
| 14601 | Elgato > Camera Hub > Prompter > Fast Forward Chapter | unsure | Cuebot has no Camera Hub Prompter connection |
| 14616 | Elgato > Camera Hub > Prompter > Get Selected Script | likely | Cuebot has no Camera Hub Prompter connection |
| 14602 | Elgato > Camera Hub > Prompter > Rewind Chapter | unsure | Cuebot has no Camera Hub Prompter connection |
| 14611 | Elgato > Camera Hub > Prompter > Set Auto Scroll State | unsure | Cuebot has no Camera Hub Prompter connection |
| 14610 | Elgato > Camera Hub > Prompter > Set Brightness | certain | Cuebot has no Camera Hub Prompter connection |
| 14603 | Elgato > Camera Hub > Prompter > Set Chapter | certain | Cuebot has no Camera Hub Prompter connection |
| 14615 | Elgato > Camera Hub > Prompter > Set Crosshair Image | certain | Cuebot has no Camera Hub Prompter connection |
| 14614 | Elgato > Camera Hub > Prompter > Set Crosshair State | unsure | Cuebot has no Camera Hub Prompter connection |
| 14604 | Elgato > Camera Hub > Prompter > Set Font | certain | Cuebot has no Camera Hub Prompter connection |
| 14605 | Elgato > Camera Hub > Prompter > Set Font Size | certain | Cuebot has no Camera Hub Prompter connection |
| 14608 | Elgato > Camera Hub > Prompter > Set Horizontal Margin | unsure | Cuebot has no Camera Hub Prompter connection |
| 14607 | Elgato > Camera Hub > Prompter > Set Line Spacing | certain | Cuebot has no Camera Hub Prompter connection |
| 14612 | Elgato > Camera Hub > Prompter > Set Mode | certain | Cuebot has no Camera Hub Prompter connection |
| 14606 | Elgato > Camera Hub > Prompter > Set Opacity | certain | Cuebot has no Camera Hub Prompter connection |
| 14617 | Elgato > Camera Hub > Prompter > Set Script | certain | Cuebot has no Camera Hub Prompter connection |
| 14609 | Elgato > Camera Hub > Prompter > Set Vertical Margin | unsure | Cuebot has no Camera Hub Prompter connection |
| 14314 | Elgato > Camera Hub > Webcam > Set Dynamic Range | certain | Cuebot's direct (UVC) webcam control has no dynamic range setting |
| 14311 | Elgato > Camera Hub > Webcam > Set Noise Reduction | likely | Cuebot's direct (UVC) webcam control has no noise reduction setting |
| 14310 | Elgato > Camera Hub > Webcam > Set White Balance Tint | certain | Cuebot's direct (UVC) webcam control has no tint setting |
| 14701 | Elgato > Stream Deck > Execute MCP Action | likely | Cuebot's Stream Deck plugin has no MCP actions |
| 37014 | Integrations > Streamfog > Backgrounds > Play Video Background | certain | Cuebot has no Streamfog connection |
| 37015 | Integrations > Streamfog > Backgrounds > Play Video Background by Name | certain | Cuebot has no Streamfog connection |
| 37001 | Integrations > Streamfog > Lenses > Activate Lens | certain | Cuebot has no Streamfog connection |
| 37004 | Integrations > Streamfog > Lenses > Activate Lens by Name | certain | Cuebot has no Streamfog connection |
| 37002 | Integrations > Streamfog > Lenses > Deactivate Lens | certain | Cuebot has no Streamfog connection |
| 37011 | Integrations > Streamfog > Outfits > Activate Outfit | certain | Cuebot has no Streamfog connection |
| 37013 | Integrations > Streamfog > Outfits > Activate Outfit by Name | certain | Cuebot has no Streamfog connection |
| 37012 | Integrations > Streamfog > Outfits > Reset Look | certain | Cuebot has no Streamfog connection |
| 44001 | Integrations > Streamlabs > Spin Wheel | likely | Cuebot has no Streamlabs (cloud) wheel |
| 35104 | Kick > Rewards > Configure Rewards | certain | Cuebot has no bulk Kick reward configuration |
| 35109 | Kick > Rewards > Reset Reward Counter | likely | Cuebot keeps no Kick reward counters |
| 35110 | Kick > Rewards > Reset Reward User Counters | likely | Cuebot keeps no Kick reward counters |
| 35111 | Kick > Rewards > Set Group Enabled State | likely | Cuebot has no Kick reward groups |
| 35112 | Kick > Rewards > Set Group Paused State | likely | Cuebot has no Kick reward groups |
| 41016 | Meld Studio > Layers > Set Media Layer Source | unsure | Cuebot's Meld Studio steps cannot change a media layer's file |
| 41012 | Meld Studio > Layers > Set Text | certain | Cuebot's Meld Studio steps cannot set a text layer |
| 20001 | Streamer.bot > Remote > Send Remote Trigger | certain | Cuebot's remote link sends signals only |
| 573 | Twitch > Clips > Get Clip Download Urls | certain | Cuebot has no clip download step |
| 579 | Twitch > Moderation > Add Suspicious Status | likely | Cuebot has no suspicious-user moderation step |
| 580 | Twitch > Moderation > Remove Suspicious Status | likely | Cuebot has no suspicious-user moderation step |

Gaps by integration: Camera Hub Prompter (16) and EpocCam (4) need a Camera Hub connection Cuebot does not have, and
three webcam settings (white balance tint, noise reduction, dynamic range) are not in Cuebot's direct UVC control;
Streamfog (8) has no Cuebot connection; Kick's bulk reward configuration, reward counters and reward groups (5),
two Meld Studio layer setters, Twitch's suspicious-user status and clip downloads, Stream Deck MCP actions,
Streamer.bot Remote triggers and the Streamlabs spin wheel are not in Cuebot.

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

Sub-action codes: 351 of Streamer.bot 1.0.7's 395 mapped (205 certain, 146 likely or unsure and flagged), 44 with no
Cuebot equivalent; every code verified. Trigger codes: 371 of its 419 mapped, all verified (plus 18002). Some numbers
are both a sub-action and a trigger code (for example 4001, 15001); the two lists are separate. On the public samples
tried, every sub-action maps (942 of 942; 924 before the verified sub-action codes) and 94 % of triggers (before the
verified trigger codes: 72 %); the remaining trigger placeholders there are the Test trigger (702) and 153, a code from
an older Streamer.bot that 1.0.7 no longer offers.
