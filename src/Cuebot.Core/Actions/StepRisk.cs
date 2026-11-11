using System.Collections.Concurrent;

namespace Cuebot.Core.Actions;

/// <summary>
/// How much harm a step or trigger type can do in the wrong hands. Mod portal roles allow steps up to a level
/// (docs/MOD_PORTAL_PLAN.md, section 5); <see cref="Never"/> is never available through the portal.
/// </summary>
public enum StepRisk
{
    /// <summary>Chat messages, sounds, delays, TTS, overlay alerts, logic.</summary>
    Basic = 1,

    /// <summary>Scenes and sources, audio levels, raids, ads, shoutouts, polls, queues, timeouts.</summary>
    StreamControl = 2,

    /// <summary>Start or end the stream, bans, title and category, turning actions and triggers on and off.</summary>
    ShowCritical = 3,

    /// <summary>Variables, web requests, decks, MIDI, smart devices, integration-specific steps, running other actions.</summary>
    TrustedTech = 4,

    /// <summary>Code, programs, files, the clipboard, keystrokes, raw requests: never through the portal.</summary>
    Never = 5,
}

/// <summary>
/// The risk level of every step and trigger type. A rule is an exact type id or a prefix; the longest match wins.
/// Types no rule covers count as <see cref="StepRisk.Never"/> (a test makes sure every registered type is covered).
/// </summary>
public static class StepRiskCatalog
{
    private static readonly ConcurrentDictionary<string, StepRisk> StepCache = new(StringComparer.Ordinal);

    /// <summary>Cached: the engine asks before every step it runs (<see cref="ModInfluence"/>).</summary>
    public static StepRisk ForStep(string typeId) => StepCache.GetOrAdd(typeId, id => Find(StepRules, id) ?? StepRisk.Never);

    public static StepRisk ForTrigger(string typeId) => Find(TriggerRules, typeId) ?? StepRisk.Never;

    /// <summary>True when a rule covers the step type (rather than the Never fallback).</summary>
    public static bool CoversStep(string typeId) => Find(StepRules, typeId) is not null;

    public static bool CoversTrigger(string typeId) => Find(TriggerRules, typeId) is not null;

    private static StepRisk? Find(IReadOnlyDictionary<string, StepRisk> rules, string typeId)
    {
        if (rules.TryGetValue(typeId, out var exact)) return exact;
        StepRisk? best = null;
        var bestLength = 0;
        foreach (var (pattern, level) in rules)
        {
            if (!pattern.EndsWith('*') || pattern.Length - 1 <= bestLength) continue;
            var prefix = pattern[..^1];
            if (!typeId.StartsWith(prefix, StringComparison.Ordinal)) continue;
            best = level;
            bestLength = prefix.Length;
        }
        return best;
    }

    private const StepRisk L1 = StepRisk.Basic, L2 = StepRisk.StreamControl, L3 = StepRisk.ShowCritical, L4 = StepRisk.TrustedTech, No = StepRisk.Never;

    private static readonly Dictionary<string, StepRisk> StepRules = new(StringComparer.Ordinal)
    {
        // Logic, flow and the action engine.
        ["core.comment"] = L1, ["core.delay"] = L1, ["core.log"] = L1, ["core.group"] = L1, ["core.if"] = L1,
        ["core.switch"] = L1, ["core.while"] = L1, ["core.parallel"] = L1, ["core.stop"] = L1, ["core.loopBreak"] = L1,
        ["core.loopContinue"] = L1, ["core.randomNumber"] = L1, ["core.pickColor"] = L1, ["core.jsonCount"] = L1,
        ["core.jsonGetValue"] = L1, ["core.getActionState"] = L1, ["core.getActionGroupState"] = L1,
        ["core.getInput"] = L3, ["core.getInputDialog"] = L3,
        ["core.setActionState"] = L3, ["core.setActionGroupState"] = L3, ["core.setTriggerState"] = L3,
        ["core.setSubActionState"] = L3, ["core.setSubActionWeight"] = L3, ["core.clearActionQueue"] = L3,
        ["core.setActionQueuePause"] = L3, ["core.setActionQueueBlocking"] = L3,
        ["core.runAction"] = L4, ["core.triggerCustomEvent"] = L4, ["core.sendSignal"] = L4, ["core.waitForSignal"] = L4,
        ["core.httpRequest"] = L4, ["core.udpBroadcast"] = L4,
        ["core.getGlobal"] = L4, ["core.setGlobal"] = L4, ["core.deleteGlobal"] = L4, ["core.clearTemporaryGlobals"] = L4,
        ["core.getUserVariable"] = L4, ["core.setUserVariable"] = L4, ["core.deleteUserVariable"] = L4,
        ["core.clearTemporaryUserVariables"] = L4, ["core.setArgument"] = L4,
        // Files, programs, the desktop: never.
        ["core.deleteFile"] = No, ["core.fileExists"] = No, ["core.readFile"] = No, ["core.readLines"] = No,
        ["core.readRandomLine"] = No, ["core.readSpecificLine"] = No, ["core.writeFile"] = No, ["core.runProgram"] = No,
        ["core.setWatcherState"] = No, ["core.switchProfile"] = No, ["code.*"] = No, ["desktop.getClipboard"] = No, ["desktop.setClipboard"] = No,
        ["input.*"] = No, ["desktop.showNotification"] = L1,

        // Chat on every platform.
        ["chat.*"] = L1, ["rumble.sendMessage"] = L1,
        ["twitch.*"] = L2, ["twitch.get*"] = L1, ["twitch.sendMessage"] = L1, ["twitch.replyToMessage"] = L1,
        ["twitch.sendAnnouncement"] = L1, ["twitch.sendWhisper"] = L1, ["twitch.createMarker"] = L1,
        ["twitch.ban"] = L3, ["twitch.unban"] = L3, ["twitch.addModerator"] = L3, ["twitch.removeModerator"] = L3,
        ["twitch.addVip"] = L3, ["twitch.removeVip"] = L3, ["twitch.clearChat"] = L3, ["twitch.setShieldMode"] = L3,
        ["twitch.setTitle"] = L3, ["twitch.setGame"] = L3, ["twitch.setTags"] = L3, ["twitch.addTag"] = L3,
        ["twitch.removeTag"] = L3, ["twitch.clearTags"] = L3, ["twitch.updateStreamInfo"] = L3,
        ["twitch.setContentLabel"] = L3, ["twitch.setBrandedContent"] = L3, ["twitch.createReward"] = L3,
        ["twitch.deleteReward"] = L3, ["twitch.configureRewards"] = L3, ["twitch.updateReward"] = L3,
        ["twitch.assignGuestStarSlot"] = L3, ["twitch.createGuestStarSession"] = L3, ["twitch.deleteGuestStarInvite"] = L3,
        ["twitch.endGuestStarSession"] = L3, ["twitch.updateGuestStarSlot"] = L3, ["twitch.deleteGuestStarSlot"] = L3,
        ["twitch.sendGuestStarInvite"] = L3, ["twitch.updateGuestStarSettings"] = L3, ["twitch.updateGuestStarSlotSettings"] = L3,
        ["kick.*"] = L2, ["kick.get*"] = L1, ["kick.sendMessage"] = L1, ["kick.replyToMessage"] = L1,
        ["kick.addBroadcasterInfo"] = L1, ["kick.addRandomUsers"] = L1, ["kick.userInGroup"] = L1,
        ["kick.banUser"] = L3, ["kick.unbanUser"] = L3, ["kick.setTitle"] = L3, ["kick.setCategory"] = L3,
        ["kick.createReward"] = L3, ["kick.deleteReward"] = L3, ["kick.configureRewards"] = L3,
        ["youtube.*"] = L2, ["youtube.get*"] = L1, ["youtube.sendMessage"] = L1, ["youtube.addBroadcasterInfo"] = L1,
        ["youtube.banUser"] = L3, ["youtube.unbanUser"] = L3, ["youtube.addModerator"] = L3, ["youtube.removeModerator"] = L3,
        ["youtube.setTitle"] = L3, ["youtube.setDescription"] = L3, ["youtube.setCategory"] = L3,
        ["youtube.setTitleAndDescription"] = L3, ["youtube.setPrivacy"] = L3,
        ["discord.*"] = L2, ["discord.sendMessage"] = L1, ["discord.replyToSlashCommand"] = L1,
        ["discord.addRole"] = L3, ["discord.removeRole"] = L3, ["discord.setStatus"] = L3, ["discord.basicWebhook"] = L4,

        // Alerts, sound, speech.
        ["audio.*"] = L1, ["overlays.*"] = L1,
        ["speech.*"] = L2, ["speech.speak"] = L1, ["speech.setEnabled"] = L3,

        // Broadcasting software: scenes and sources are stream control, going live is show-critical, raw requests never.
        ["obs.*"] = L2, ["obs.get*"] = L1, ["obs.streaming"] = L3, ["obs.recording"] = L3, ["obs.virtualCam"] = L3,
        ["obs.setProfile"] = L3, ["obs.setSceneCollection"] = L3, ["obs.triggerHotkey"] = L3, ["obs.setInputSettings"] = L4,
        ["obs.rawRequest"] = No, ["obs.rawBatch"] = No, ["obs.screenshot"] = No,
        ["slobs.*"] = L2, ["slobs.get*"] = L1, ["slobs.streaming"] = L3, ["slobs.recording"] = L3,
        ["slobs.raw"] = No, ["slobs.notify"] = L1, ["slobs.setSceneCollection"] = L3,
        ["meldstudio.*"] = L2, ["meldstudio.get*"] = L1, ["meldstudio.streaming"] = L3, ["meldstudio.recording"] = L3,
        ["meldstudio.toggleVirtualCamera"] = L3, ["meldstudio.sendCommand"] = No,

        // Audio mixers count as audio levels.
        ["wavelink.*"] = L2, ["wavelink.get*"] = L1, ["wavelink.legacy.get*"] = L1,
        ["goxlr.*"] = L2, ["goxlr.getStatus"] = L1, ["goxlr.loadProfile"] = L3, ["goxlr.loadMicProfile"] = L3,
        ["goxlr.sendCommand"] = L4,

        // Community features.
        ["points.*"] = L2, ["points.check"] = L1, ["points.get"] = L1, ["points.getDefinition"] = L1,
        ["points.leaderboard"] = L1, ["points.getMultiplier"] = L1, ["points.resetAll"] = L3, ["points.delete"] = L3,
        ["polls.*"] = L2, ["goals.*"] = L2, ["goals.get"] = L1, ["subathon.*"] = L2, ["subathon.get"] = L1,
        ["credits.*"] = L2, ["credits.get"] = L1, ["giveaways.*"] = L2, ["media.*"] = L2, ["spotify.*"] = L2,
        ["spending.*"] = L2, ["spending.getTop"] = L1, ["spending.newStream"] = L3,
        ["streamlabs.*"] = L1, ["streamlabs.alertQueue"] = L2, ["streamlabs.createAlert"] = L2, ["streamlabs.setAlertProfile"] = L2,
        ["streamlabs.spinWheel"] = L2, ["streamlabs.emptyJar"] = L2, ["streamlabs.rollCredits"] = L2, ["streamlabs.mediaShare"] = L2,
        ["streamlabs.createDonation"] = L2, ["streamlabs.addPoints"] = L2,
        ["streamlabs.setPoints"] = L3, ["streamlabs.addPointsToAll"] = L3, ["streamlabs.resetPoints"] = L3,
        ["streamelements.*"] = L1, ["streamelements.addUserPoints"] = L2,
        ["streamelements.botMute"] = L2, ["streamelements.overlayAction"] = L2, ["streamelements.setSessionValue"] = L2,
        ["streamelements.replayLatest"] = L2, ["streamelements.createTip"] = L2, ["streamelements.completeRedemption"] = L2,
        ["streamelements.refundRedemption"] = L2, ["streamelements.createContest"] = L2, ["streamelements.contestAction"] = L2,
        ["streamelements.giveawayAction"] = L2, ["streamelements.songPlayer"] = L2, ["streamelements.addSong"] = L2,
        ["streamelements.removeSong"] = L2,
        ["streamelements.resetSession"] = L3, ["streamelements.resetUserPoints"] = L3, ["streamelements.bulkPoints"] = L3,
        ["streamelements.resetPoints"] = L3, ["streamelements.setStoreItem"] = L3, ["streamelements.setTimer"] = L3,
        ["streamelements.setCommand"] = L3,
        ["quotes.*"] = L1, ["quotes.edit"] = L2, ["quotes.delete"] = L2,
        ["groups.*"] = L2, ["groups.userInGroup"] = L1, ["groups.clear"] = L3,
        ["users.addRandomPresentUser"] = L1, ["users.resetFirstWords"] = L2,
        ["pronouns.*"] = L1, ["nowplaying.*"] = L1, ["heartrate.*"] = L1,
        ["commands.*"] = L3, ["commands.get*"] = L1, ["timers.*"] = L3, ["timers.getState"] = L1,

        // Integrations, devices and other programs: trusted tech.
        ["store.*"] = L4, ["websocket.*"] = L4, ["remote.*"] = L4, ["ifttt.*"] = L4, ["decks.*"] = L4,
        ["streamDeck.*"] = L4, ["midi.*"] = L4, ["controller.*"] = L4, ["voiceControl.*"] = L4, ["crowdcontrol.*"] = L4,
        ["lumia.*"] = L4, ["elgato.*"] = L4, ["obsbot.*"] = L4, ["animaze.*"] = L4, ["nizima.*"] = L4,
        ["vtubestudio.*"] = L4, ["vtubestudio.rawRequest"] = No, ["pngtuberRemix.*"] = L4, ["tits.*"] = L4,
        ["veadotube.*"] = L4, ["vmc.*"] = L4, ["vnyan.*"] = L4, ["voicemod.*"] = L4, ["vrchat.*"] = L4, ["warudo.*"] = L4,
        ["mtion.*"] = L4, ["polypop.*"] = L4, ["rahituber.*"] = L4, ["streamloots.*"] = L4,
    };

    private static readonly Dictionary<string, StepRisk> TriggerRules = new(StringComparer.Ordinal)
    {
        // Platform, chat, community and donation events.
        ["twitch.*"] = L1, ["youtube.*"] = L1, ["kick.*"] = L1, ["rumble.*"] = L1, ["chat.*"] = L1, ["command.*"] = L1,
        ["discord.*"] = L1, ["points.*"] = L1, ["quotes.*"] = L1, ["goals.*"] = L1, ["subathon.*"] = L1,
        ["spending.*"] = L1, ["speech.*"] = L1, ["users.*"] = L1, ["groups.*"] = L1, ["nowplaying.*"] = L1,
        ["hyperate.*"] = L1, ["pulsoid.*"] = L1, ["cuebot.*"] = L1, ["crowdcontrol.*"] = L1,
        ["streamelements.*"] = L1, ["streamlabs.*"] = L1, ["fourthwall.*"] = L1, ["tipeeestream.*"] = L1,
        ["patreon.*"] = L1, ["kofi.*"] = L1, ["donordrive.*"] = L1, ["treatstream.*"] = L1, ["throne.*"] = L1,
        ["shopify.*"] = L1, ["pally.*"] = L1, ["streamloots.*"] = L1,
        ["core.timer"] = L1, ["core.cron"] = L1, ["core.appStarted"] = L1, ["core.profileStarted"] = L1, ["core.profileStopping"] = L1,
        ["obs.*"] = L1, ["slobs.*"] = L1, ["meldstudio.*"] = L1,

        // Devices, integrations, the network and variables.
        ["elgato.*"] = L4, ["wavelink.*"] = L4, ["goxlr.*"] = L4, ["vtubestudio.*"] = L4, ["voicemod.*"] = L4,
        ["tits.*"] = L4, ["obsbot.*"] = L4, ["lumia.*"] = L4, ["veadotube.*"] = L4, ["mtion.*"] = L4, ["warudo.*"] = L4,
        ["polypop.*"] = L4, ["nizima.*"] = L4, ["animaze.*"] = L4, ["vrchat.*"] = L4, ["pngtuberRemix.*"] = L4,
        ["controller.*"] = L4, ["midi.*"] = L4, ["streamDeck.*"] = L4, ["voiceControl.*"] = L4, ["decks.*"] = L4,
        ["websocket.*"] = L4, ["remote.*"] = L4, ["webhook.*"] = L4, ["variables.*"] = L4, ["custom.*"] = L4,
        ["hotkeys.*"] = L4, ["input.*"] = L4, ["desktop.toastActivated"] = L4,
        ["core.processStarted"] = L4, ["core.processStopped"] = L4,

        // Watching files or the clipboard hands their contents to the action: never.
        ["core.file*"] = No, ["desktop.clipboardChanged"] = No,
    };
}
