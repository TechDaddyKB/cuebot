using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>
/// The built-in trigger rules: one per Streamer.bot 1.0.7 trigger code that has a Cuebot equivalent. The codes and
/// the settings each trigger saves were read from a real Streamer.bot 1.0.7 install (each trigger added in its UI and
/// its saved file read back, 2026-10-28); docs/streamerbot-import-codes.md lists them, and
/// <see cref="NoEquivalentTriggers"/> the codes Cuebot has no trigger for.
/// <para>
/// Settings conventions: "min"/"max" are Streamer.bot's Range filter (-1 = open; a Max without a Min means exactly
/// that value); ids (all-zero GUID or null = not set) reference Streamer.bot objects Cuebot cannot resolve unless the
/// export carries them (commands, timers). A filter Cuebot's trigger has no setting for is dropped with a warning.
/// </para>
/// </summary>
public sealed partial class StreamerBotMappingTable
{
    /// <summary>Streamer.bot 1.0.7 trigger codes with no Cuebot trigger, and why. They import as disabled placeholders.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoEquivalentTriggers = BuildNoEquivalent();

    private static Dictionary<string, string> BuildNoEquivalent()
    {
        var gaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["702"] = "Test: only feeds test arguments to Streamer.bot's Test Trigger button; Cuebot runs actions by hand from the editor",
            ["23001"] = "Speaker.bot Connected: Cuebot speaks itself and never connects to Speaker.bot",
            ["23002"] = "Speaker.bot Disconnected: Cuebot speaks itself and never connects to Speaker.bot",
            ["116"] = "Twitch Community Goal Contribution: Twitch retired community goals and Cuebot has no such event",
            ["117"] = "Twitch Community Goal Ended: Twitch retired community goals and Cuebot has no such event",
            ["496"] = "Twitch Custom Power-Up Redemption: Cuebot's Power-up trigger covers Twitch's built-in power-ups only",
        };
        const string prompter = "Camera Hub Prompter: Cuebot controls webcams directly (UVC) and has no Elgato Prompter connection";
        for (var code = 21401; code <= 21426; code++) gaps[code.ToString(CultureInfo.InvariantCulture)] = prompter;
        const string webcam = "a Camera Hub setting or event that Cuebot's direct (UVC) webcam control does not see";
        foreach (var (code, name) in new[]
                 {
                     ("21353", "Selected Webcam Changed"), ("21357", "Exposure Auto Lock Enabled"), ("21358", "Exposure Auto Lock Disabled"),
                     ("21359", "Snapshot Taken"), ("21367", "Noise Reduction Enabled"), ("21368", "Noise Reduction Disabled"),
                     ("21369", "ISO Changed"), ("21373", "Lens Changed"), ("21374", "AR Lens Changed"), ("21375", "Bitrate Changed"),
                     ("21376", "Flash Enabled"), ("21377", "Flash Disabled"), ("21380", "Overscan Changed"),
                     ("21384", "White Balance Tint Changed"), ("21387", "Live ISO Changed"), ("21390", "Live White Balance Tint Changed"),
                 })
            gaps[code] = $"Camera Hub Webcam {name}: {webcam}";
        return gaps;
    }

    /// <summary>Cuebot settings for one trigger, from its Streamer.bot settings; <c>label</c> names it in warnings.</summary>
    private delegate (string Key, string Value)[] SbSettings(JsonObject source, ISbMapContext context, string label);

    private static void AddTriggers(StreamerBotMappingTable t)
    {
        // A rule per code: the label is Streamer.bot's trigger name (for warnings), the rule name the label without
        // spaces. "Plain" triggers carry no settings; the fields listed are filters Cuebot cannot apply.
        void Plain(string code, string label, string target, params string[] unsupported) =>
            With(code, label, target, (s, c, l) =>
            {
                Unsupported(s, c, l, unsupported);
                return [];
            });
        void With(string code, string label, string target, SbSettings settings)
        {
            var name = RuleName(label);
            t.Add(new SbTriggerRule(name, target, [name, code], (s, c) => Trigger(target, s, settings(s, c, label))));
        }
        void Obs(string code, string label, string target, SbSettings? settings = null) =>
            With(code, label, target, (s, c, l) =>
            {
                ObsConnection(s, c);
                return [("connection", ""), .. (settings?.Invoke(s, c, l) ?? [])];
            });

        // ---------------------------------------------------------------- core
        t.Add(new SbTriggerRule("AppStarted", "core.appStarted", ["StreamerbotStarted", "706"], (s, _) => Trigger("core.appStarted", s)));
        t.Add(new SbTriggerRule("TimedAction", "core.timer", ["TimedAction", "TimedActions", "Timer", "TimerTrigger", "701"],
            (s, c) => Trigger("core.timer", s, ("timer", c.TimerRef(Str(s, "timerId", "timer", "timerName")))))
        {
            Signatures = [["timerId"]],
        });
        t.Add(new SbTriggerRule("Command", "command.triggered", ["Command", "CommandTriggered", "CommandTrigger", "401"],
            (s, c) => Trigger("command.triggered", s, ("command", c.CommandRef(Str(s, "commandId", "command", "commandName")))))
        {
            Signatures = [["commandId"]],
        });
        With("402", "Command Cooldown", "command.cooldown", (s, c, _) => [("command", c.CommandRef(Str(s, "commandId")))]);
        Plain("511", "File Tail Changed", "core.fileTailLine", "fileTailId");
        Plain("501", "File Watcher Changed", "core.fileChanged", "watcherId");
        Plain("502", "File Watcher Created", "core.fileCreated", "watcherId");
        Plain("503", "File Watcher Deleted", "core.fileDeleted", "watcherId");
        Plain("504", "File Watcher Renamed", "core.fileRenamed", "watcherId");
        With("709", "Global Variable Updated", "variables.globalUpdated", GlobalVariable);
        With("711", "Global Variable Created", "variables.globalCreated", GlobalVariable);
        With("712", "Global Variable Deleted", "variables.globalDeleted", GlobalVariable);
        With("710", "User Global Variable Updated", "variables.userUpdated", (s, c, l) =>
            [.. GlobalVariable(s, c, l), ("userLogin", Str(s, "userLogon", "userLogin") ?? ""), ("platform", Str(s, "platform") ?? "")]);
        Plain("32001", "Group Added", "groups.added");
        Plain("32002", "Group Removed", "groups.removed");
        Plain("32003", "Group Cleared", "groups.cleared", "groupId");
        Plain("32004", "Group Users Added", "groups.usersAdded", "groupId");
        Plain("32005", "Group Users Removed", "groups.usersRemoved", "groupId");
        t.Add(new SbTriggerRule("KeyPress", "hotkeys.pressed", ["KeyPress", "16001"], (s, c) =>
        {
            // keyCode/mask are not decoded: an empty hotkey would run on every Cuebot hotkey, so the trigger is disabled.
            c.Warn($"'{c.ActionName}': a Key Press trigger (key code {Str(s, "keyCode") ?? "?"}, modifiers {Str(s, "mask") ?? "?"}) was imported "
                   + "disabled; set its hotkey in Cuebot and enable it.");
            var trigger = Trigger("hotkeys.pressed", s, ("hotkey", ""));
            trigger.Enabled = false;
            return trigger;
        }));
        t.Add(new SbTriggerRule("MouseClick", "input.mouseClick", ["MouseClick", "16002"], (s, c) =>
        {
            c.Warn($"'{c.ActionName}': a Mouse Click trigger (buttons {Str(s, "buttons") ?? "?"}) was imported disabled; choose its button in "
                   + "Cuebot and enable it.");
            var trigger = Trigger("input.mouseClick", s, ("button", "Any"));
            trigger.Enabled = false;
            return trigger;
        }));
        With("703", "Process Started", "core.processStarted", ProcessWatcher);
        With("704", "Process Stopped", "core.processStopped", ProcessWatcher);
        Plain("601", "Quote Added", "quotes.added");
        Plain("602", "Show Quote", "quotes.shown");
        Plain("42001", "Clipboard Changed", "desktop.clipboardChanged");
        With("708", "Toast Activation", "desktop.toastActivated", (s, _, _) => [("toastId", Str(s, "toastId") ?? "")]);
        Plain("301", "Voice Control Dictation", "voiceControl.dictation", "commandId");
        Plain("302", "Voice Control Command", "voiceControl.command", "commandId");
        Plain("803", "Websocket Client Opened", "websocket.clientOpen", "clientId");
        Plain("804", "Websocket Client Closed", "websocket.clientClose", "clientId");
        Plain("805", "Websocket Client Message", "websocket.clientMessage", "clientId");
        Plain("2001", "Custom Server Connection Opened", "websocket.serverOpen", "serverId");
        Plain("2002", "Custom Server Connection Closed", "websocket.serverClose", "serverId");
        Plain("2003", "Custom Server Message", "websocket.serverMessage", "serverId");
        Plain("15001", "MIDI Message", "midi.message", "midiDevice", "midiEvent");
        // Custom events (CPH.TriggerEvent) and registered code triggers (CPH.TriggerCodeEvent) both name an event; Cuebot
        // fires both through its "Custom code event" trigger. 18002 is not in Streamer.bot 1.0.7's trigger menu.
        t.Add(new SbTriggerRule("CustomEvent", "custom.codeEvent", ["Custom", "CustomEvent", "18001", "CustomCodeEvent", "18002"],
            (s, _) => Trigger("custom.codeEvent", s, ("eventName", Str(s, "eventName") ?? "")))
        {
            Signatures = [["eventName"]],
        });

        // ---------------------------------------------------------------- Elgato
        Plain("21301", "Camera Hub Connected", "elgato.cameraHub.connected");
        Plain("21302", "Camera Hub Disconnected", "elgato.cameraHub.disconnected");
        // "Activated" = ready for use in Camera Hub; Cuebot raises webcam connected/disconnected when a webcam appears or goes.
        foreach (var (code, label, cue) in new[]
                 {
                     ("21351", "Webcam Activated", "connected"), ("21352", "Webcam Deactivated", "disconnected"),
                     ("21354", "Webcam Mirrored", "mirrored"), ("21355", "Webcam Flipped", "flipped"),
                     ("21356", "Webcam Device Orientation Changed", "deviceOrientationChanged"), ("21360", "Webcam Zoom Changed", "zoomChanged"),
                     ("21361", "Webcam Contrast Changed", "contrastChanged"), ("21362", "Webcam White Balance Changed", "whiteBalanceChanged"),
                     ("21363", "Webcam Auto Exposure Enabled", "autoExposureEnabled"), ("21364", "Webcam Auto Exposure Disabled", "autoExposureDisabled"),
                     ("21365", "Webcam Auto White Balance Enabled", "autoWhiteBalanceEnabled"),
                     ("21366", "Webcam Auto White Balance Disabled", "autoWhiteBalanceDisabled"),
                     ("21370", "Webcam Shutter Speed Changed", "shutterSpeedChanged"), ("21371", "Webcam Sharpness Changed", "sharpnessChanged"),
                     ("21372", "Webcam Anti Flicker Changed", "antiFlickerChanged"), ("21378", "Webcam Pan Changed", "panChanged"),
                     ("21379", "Webcam Tilt Changed", "tiltChanged"), ("21381", "Webcam Auto Focus Enabled", "autoFocusEnabled"),
                     ("21382", "Webcam Auto Focus Disabled", "autoFocusDisabled"), ("21383", "Webcam Focus Changed", "focusChanged"),
                     ("21385", "Webcam Brightness Changed", "brightnessChanged"), ("21386", "Webcam Saturation Changed", "saturationChanged"),
                     ("21388", "Webcam Live Shutter Speed Changed", "liveShutterSpeedChanged"),
                     ("21389", "Webcam Live White Balance Changed", "liveWhiteBalanceChanged"),
                 })
            Plain(code, "Camera Hub " + label, "elgato.cameraHub.webcam." + cue);
        Plain("17002", "Stream Deck Connected", "streamDeck.connected");
        Plain("17003", "Stream Deck Disconnected", "streamDeck.disconnected");
        Plain("17004", "Stream Deck Info", "streamDeck.info");
        // Wave Link (1.x protocol): Cuebot's legacy Wave Link triggers.
        Plain("21017", "Wave Link Connected", "wavelink.connected");
        Plain("21018", "Wave Link Disconnected", "wavelink.disconnected");
        foreach (var (code, label, cue) in new[]
                 {
                     ("21001", "Output Switched", "outputSwitched"), ("21002", "Output Volume Changed", "outputVolumeChanged"),
                     ("21003", "Output Mute Changed", "outputMuteChanged"), ("21004", "Selected Output Changed", "selectedOutputChanged"),
                     ("21005", "Input Volume Changed", "inputVolumeChanged"), ("21006", "Input Mute Changed", "inputMuteChanged"),
                     ("21007", "Input Name Changed", "inputNameChanged"), ("21008", "Microphone Gain Changed", "microphoneGainChanged"),
                     ("21009", "Microphone Output Volume Changed", "microphoneOutputVolumeChanged"),
                     ("21010", "Microphone Balance Changed", "microphoneBalanceChanged"), ("21011", "Microphone Mute Changed", "microphoneMuteChanged"),
                     ("21012", "Microphone Setting Changed", "microphoneSettingChanged"), ("21013", "Filter Added", "filterAdded"),
                     ("21014", "Filter Changed", "filterChanged"), ("21015", "Filter Deleted", "filterDeleted"),
                     ("21016", "Filter Bypass State Changed", "filterBypassStateChanged"),
                 })
            Plain(code, "Wave Link " + label, "wavelink.legacy." + cue);
        With("21019", "Wave Link Input Level Meter Changed", "wavelink.legacy.inputLevelMeterChanged", (s, c, l) =>
        {
            // The input is saved by Wave Link identifier and name; Cuebot's filter matches the name.
            if (Str(s, "name") is { Length: > 0 } name) return [("levelName", name)];
            Unsupported(s, c, l, "identifier");
            return [];
        });
        // "com.elgato.mix.all" (the default) is both mixes.
        Plain("21020", "Wave Link Output Level Meter Changed", "wavelink.legacy.outputLevelMeterChanged", "mixerType");

        // ---------------------------------------------------------------- integrations
        With("20009", "Crowd Control Coin Exchange", "crowdcontrol.coinExchange", (s, c, l) =>
        {
            Unsupported(s, c, l, "exchangeSource");
            return [("coinType", Choice(s, c, l, "coinType", ["paid", "free"])), .. Range(s, c, l, "minAmount", null)];
        });
        With("20003", "Crowd Control Effect Request", "crowdcontrol.effectRequest", CrowdControlEffect);
        With("20004", "Crowd Control Effect Success", "crowdcontrol.effectSuccess", CrowdControlEffect);
        With("20005", "Crowd Control Effect Failure", "crowdcontrol.effectFailure", CrowdControlEffect);
        Plain("20001", "Crowd Control Game Session Start", "crowdcontrol.gameSessionStart");
        Plain("20002", "Crowd Control Game Session End", "crowdcontrol.gameSessionEnd");
        Plain("20006", "Crowd Control Timed Effect Started", "crowdcontrol.timedEffectStarted");
        Plain("20007", "Crowd Control Timed Effect Ended", "crowdcontrol.timedEffectEnded");
        Plain("20008", "Crowd Control Timed Effect Update", "crowdcontrol.timedEffectUpdated");
        Plain("3001", "DonorDrive Donation", "donordrive.donation", "donorDriveId");
        Plain("3002", "DonorDrive Profile Updated", "donordrive.profileUpdated", "donorDriveId");
        Plain("3003", "DonorDrive Incentive", "donordrive.incentive", "donorDriveId");
        With("24006", "Fourthwall Donation", "fourthwall.donation", Amount);
        foreach (var (code, label, cue) in new[]
                 {
                     ("24001", "Product Created", "productCreated"), ("24002", "Product Updated", "productUpdated"),
                     ("24003", "Gift Purchase", "giftPurchase"), ("24004", "Order Placed", "orderPlaced"), ("24005", "Order Updated", "orderUpdated"),
                     ("24007", "Subscription Purchased", "subscriptionPurchased"), ("24008", "Subscription Expired", "subscriptionExpired"),
                     ("24009", "Subscription Changed", "subscriptionChanged"), ("24010", "Thank You Sent", "thankYouSent"),
                     ("24011", "Newsletter Subscribed", "newsletterSubscribed"), ("24012", "Gift Draw Started", "giftDrawStarted"),
                     ("24013", "Gift Draw Ended", "giftDrawEnded"),
                 })
            Plain(code, "Fourthwall " + label, "fourthwall." + cue);
        // Heart rate: Cuebot's per-pulse trigger has no range; its Above/Below triggers fire on crossings instead.
        Plain("6001", "HypeRate Heart Rate Pulse", "hyperate.heartRate", "min", "max");
        Plain("6002", "HypeRate Twitch Clip Created", "hyperate.clipCreated");
        Plain("6003", "HypeRate Connected", "hyperate.connected");
        Plain("6004", "HypeRate Disconnected", "hyperate.disconnected");
        Plain("5001", "Pulsoid Heart Rate Pulse", "pulsoid.heartRate", "min", "max");
        With("7001", "Kofi Donation", "kofi.donation", Amount);
        Plain("7002", "Kofi Subscription", "kofi.subscription");
        Plain("7003", "Kofi Resubscription", "kofi.resubscription");
        Plain("7004", "Kofi Shop Order", "kofi.shopOrder");
        Plain("7005", "Kofi Commission", "kofi.commission");
        Plain("28001", "Pally Connected", "pally.connected");
        Plain("28002", "Pally Disconnected", "pally.disconnected");
        With("28003", "Pally Campaign Tip", "pally.campaignTip", Amount);
        Plain("8001", "Patreon Follow Created", "patreon.followCreated");
        Plain("8002", "Patreon Follow Deleted", "patreon.followDeleted");
        Plain("8003", "Patreon Pledge Created", "patreon.pledgeCreated");
        Plain("8004", "Patreon Pledge Updated", "patreon.pledgeUpdated");
        Plain("8005", "Patreon Pledge Deleted", "patreon.pledgeDeleted");
        Plain("13001", "Shopify Order Created", "shopify.orderCreated");
        Plain("13002", "Shopify Order Paid", "shopify.orderPaid");
        With("1201", "StreamElements Tip", "streamelements.tip", Amount);
        Plain("1202", "StreamElements Merch", "streamelements.merch");
        Plain("1203", "StreamElements Connected", "streamelements.connected");
        Plain("1204", "StreamElements Disconnected", "streamelements.disconnected");
        With("201", "Streamlabs Donation", "streamlabs.donation", Amount);
        Plain("202", "Streamlabs Merchandise", "streamlabs.merch");
        Plain("203", "Streamlabs Connected", "streamlabs.connected");
        Plain("204", "Streamlabs Disconnected", "streamlabs.disconnected");
        With("205", "Streamlabs Charity Donation", "streamlabs.charityDonation", Amount);
        Plain("36001", "StreamLoots Pack Purchased", "streamloots.packPurchased");
        Plain("36002", "StreamLoots Pack Gifted", "streamloots.packGifted");
        Plain("36003", "StreamLoots Card Redeemed", "streamloots.cardRedeemed");
        With("36004", "StreamLoots Reaction", "streamloots.alert", (_, c, _) =>
        {
            c.Warn($"'{c.ActionName}': a StreamLoots Reaction trigger became Cuebot's \"Other alert\" trigger, which runs for every StreamLoots "
                   + "alert that is not a pack or card; set its alert types to limit it.");
            return [];
        });
        Plain("37001", "Throne Gift Purchased", "throne.giftPurchased");
        Plain("37002", "Throne Contribution Purchased", "throne.contributionPurchased");
        Plain("37003", "Throne Gift Crowdfunded", "throne.giftCrowdfunded");
        Plain("27001", "Throwing System Connected", "tits.connected");
        Plain("27002", "Throwing System Websocket Connected", "tits.websocketConnected");
        Plain("27003", "Throwing System Websocket Disconnected", "tits.websocketDisconnected");
        Plain("27004", "Throwing System Events Connected", "tits.eventsConnected");
        Plain("27005", "Throwing System Events Disconnected", "tits.eventsDisconnected");
        With("27010", "Throwing System Item Hit", "tits.itemHit", (s, c, l) => [("item", NameOrId(s, c, l, "itemName", "itemId"))]);
        With("27011", "Throwing System Trigger Activated", "tits.triggerActivated", (s, c, l) => [("trigger", NameOrId(s, c, l, "triggerName", "triggerId"))]);
        With("27012", "Throwing System Trigger Ended", "tits.triggerEnded", (s, c, l) => [("trigger", NameOrId(s, c, l, "triggerName", "triggerId"))]);
        With("10001", "TipeeeStream Donation", "tipeeestream.donation", Amount);
        Plain("11001", "TreatStream Treat", "treatstream.treat");
        Plain("30001", "VoiceMod Connected", "voicemod.connected");
        Plain("30002", "VoiceMod Disconnected", "voicemod.disconnected");
        // Cuebot's voice filter takes Voicemod's own voice ids as well as names.
        With("30003", "VoiceMod Voice Loaded", "voicemod.voiceChanged", (s, _, _) => [("voice", Str(s, "voiceId") ?? "")]);
        Plain("30004", "VoiceMod Soundboard Changed", "voicemod.soundboardChanged", "soundboardId");
        With("19001", "VTube Studio Model Loaded", "vtubestudio.modelLoaded", (s, c, l) => [("modelName", NameOrId(s, c, l, "modelName", "modelId"))]);
        Plain("19002", "VTube Studio Model Unloaded", "vtubestudio.modelUnloaded", "modelName", "modelId");
        Plain("19003", "VTube Studio Background Changed", "vtubestudio.backgroundChanged");
        Plain("19004", "VTube Studio Model Configuration Changed", "vtubestudio.modelConfigChanged", "modelName", "modelId");
        Plain("19005", "VTube Studio Hotkey Triggered", "vtubestudio.hotkeyTriggered", "modelName", "modelId");
        Plain("19006", "VTube Studio Model Animation", "vtubestudio.modelAnimation", "modelName", "modelId");
        Plain("19007", "VTube Studio Connected", "vtubestudio.connected");
        Plain("19008", "VTube Studio Disconnected", "vtubestudio.disconnected");
        Plain("19009", "VTube Studio Tracking Status Changed", "vtubestudio.trackingStatusChanged");
        Plain("19010", "VTube Studio Item Event", "vtubestudio.itemEvent");
        Plain("19011", "VTube Studio Model Clicked", "vtubestudio.modelClicked", "modelName", "modelId");
        Plain("29001", "Remote Instance Connected", "remote.connected", "instanceId");
        Plain("29002", "Remote Instance Disconnected", "remote.disconnected", "instanceId");
        With("29003", "Remote Instance Trigger", "remote.signal", (s, c, l) =>
        {
            Unsupported(s, c, l, "instanceId");
            return [("signalName", Str(s, "eventName") ?? "")];
        });
        With("34001", "Custom Webhook", "webhook.custom", (s, c, l) => [("hookName", NameOrId(s, c, l, "webhookName", "webhookId"))]);

        // ---------------------------------------------------------------- Kick
        t.Add(new SbTriggerRule("KickFirstWords", "users.firstWords", ["KickFirstWords", "35008"], (s, c) => FirstWords(s, c, "Kick")));
        Plain("35009", "Kick Present Viewers", "kick.presentViewers");
        Plain("35010", "Kick Chat Message", "kick.chatMessage");
        Plain("35011", "Kick Follow", "kick.follow");
        Plain("35012", "Kick Stream Online", "kick.streamOnline");
        Plain("35013", "Kick Stream Offline", "kick.streamOffline");
        Plain("35014", "Kick Subscription", "kick.subscription");
        Plain("35015", "Kick Resubscription", "kick.resubscription");
        Plain("35016", "Kick Gift Subscription", "kick.giftSubscription");
        With("35017", "Kick Mass Gift Subscription", "kick.massGiftSubscription", (s, c, l) => Range(s, c, l, "minGifts", null, 1));
        Plain("35018", "Kick User Timed Out", "kick.userTimedOut", "min", "max");
        Plain("35019", "Kick User Banned", "kick.userBanned");
        Plain("35020", "Kick Channel Update", "kick.channelUpdate", "gameOnly", "gameId", "gameName");
        Plain("35021", "Kick SevenTV Emote Added", "kick.sevenTvEmoteAdded");
        Plain("35022", "Kick SevenTV Emote Removed", "kick.sevenTvEmoteRemoved");
        Plain("35023", "Kick Viewer Count Update", "kick.viewerCountUpdate", "min", "max");
        // Kick rewards are saved by id only; Cuebot's filter needs the title.
        Plain("35024", "Kick Reward Redemption", "kick.rewardRedemption", "rewardId");
        With("35025", "Kick Kicks Gifted", "kick.kicksGifted", (s, c, l) => Range(s, c, l, "minKicks", null));

        // ---------------------------------------------------------------- broadcast software
        // Meld Studio, Streamlabs Desktop: Cuebot has one connection each, so the connection id is only reported.
        Plain("33001", "Meld Studio Connected", "meldstudio.connected", "meldStudioId");
        Plain("33002", "Meld Studio Disconnected", "meldstudio.disconnected", "meldStudioId");
        Plain("33003", "Meld Studio Streaming Started", "meldstudio.streamingStarted", "meldStudioId");
        Plain("33004", "Meld Studio Streaming Stopped", "meldstudio.streamingStopped", "meldStudioId");
        Plain("33005", "Meld Studio Recording Started", "meldstudio.recordingStarted", "meldStudioId");
        Plain("33006", "Meld Studio Recording Stopped", "meldstudio.recordingStopped", "meldStudioId");
        With("33007", "Meld Studio Scene Changed", "meldstudio.sceneChanged", (s, c, l) =>
        {
            Unsupported(s, c, l, "meldStudioId");
            return [("scene", Str(s, "sceneName") ?? "")];
        });
        With("33012", "Meld Studio Event", "meldstudio.event", (s, c, l) =>
        {
            Unsupported(s, c, l, "meldStudioId");
            return [("event", Choice(s, c, l, "eventName", MeldEvents))];
        });
        Obs("14001", "OBS Connected", "obs.connected");
        Obs("14002", "OBS Disconnected", "obs.disconnected");
        Obs("14003", "OBS Event", "obs.event", (s, _, _) => [("eventType", Str(s, "eventName") ?? "")]);
        t.Add(new SbTriggerRule("ObsSceneChanged", "obs.sceneChanged", ["ObsSceneChanged", "14004"],
            (s, c) => ObsTrigger(Trigger("obs.sceneChanged", s, ("scene", Str(s, "sceneName") ?? "")), s, c)));
        t.Add(new SbTriggerRule("ObsStreamingStarted", "obs.streamStarted", ["ObsStreamingStarted", "14005"],
            (s, c) => ObsTrigger(Trigger("obs.streamStarted", s), s, c)));
        Obs("14006", "OBS Streaming Stopped", "obs.streamStopped");
        Obs("14007", "OBS Recording Started", "obs.recordStarted");
        Obs("14008", "OBS Recording Stopped", "obs.recordStopped");
        Obs("14009", "OBS Vendor Event", "obs.vendorEvent",
            (s, _, _) => [("vendor", Str(s, "vendorName") ?? ""), ("vendorEventType", Str(s, "eventName") ?? "")]);
        Plain("22001", "Streamlabs Desktop Connected", "slobs.connected", "sdId");
        Plain("22002", "Streamlabs Desktop Disconnected", "slobs.disconnected", "sdId");
        With("22003", "Streamlabs Desktop Scene Changed", "slobs.sceneChanged", (s, c, l) =>
        {
            Unsupported(s, c, l, "sdId");
            return [("scene", Str(s, "sceneName") ?? "")];
        });
        Plain("22004", "Streamlabs Desktop Streaming Started", "slobs.streamStarted", "sdId");
        Plain("22005", "Streamlabs Desktop Streaming Stopped", "slobs.streamStopped", "sdId");
        Plain("22006", "Streamlabs Desktop Recording Started", "slobs.recordStarted", "sdId");
        Plain("22007", "Streamlabs Desktop Recording Stopped", "slobs.recordStopped", "sdId");

        // ---------------------------------------------------------------- Twitch
        t.Add(new SbTriggerRule("Follow", "twitch.follow", ["Follow", "TwitchFollow", "101"], (s, _) => Trigger("twitch.follow", s)));
        t.Add(new SbTriggerRule("Cheer", "twitch.cheer", ["Cheer", "TwitchCheer", "102"], (s, _) =>
        {
            // Older exports name the bounds differently; "max" alone (exact amount) is read as min = max.
            var min = Num(s, "min", "minBits", "minimum") is { } lo and >= 0 ? lo : (double?)null;
            var max = Num(s, "max", "maxBits", "maximum") is { } hi and > 0 ? hi : (double?)null;
            return Trigger("twitch.cheer", s, ("minBits", Text(Math.Max(1, min ?? max ?? 1))), ("maxBits", max is { } m ? Text(m) : ""));
        }));
        t.Add(new SbTriggerRule("Sub", "twitch.sub", ["Sub", "Subscription", "TwitchSub", "TwitchSubscription", "103"],
            (s, c) => Trigger("twitch.sub", s, ("tier", Tiers(s, c, "Twitch Subscription")))));
        t.Add(new SbTriggerRule("ReSub", "twitch.resub", ["ReSub", "Resubscription", "TwitchReSub", "104"],
            (s, c) => Trigger("twitch.resub", s, [("tier", Tiers(s, c, "Twitch Resubscription")), .. Range(s, c, "Twitch Resubscription", "minMonths", null)])));
        // Streamer.bot fires Gift Subscription once per recipient, like Cuebot's twitch.subGift (twitch.giftSub is the gifter's total).
        t.Add(new SbTriggerRule("GiftSub", "twitch.subGift", ["GiftSub", "TwitchGiftSub", "105"], (s, c) =>
        {
            Unsupported(s, c, "Twitch Gift Subscription", "min", "max", "subType", "monthsGifted");
            return Trigger("twitch.subGift", s, ("tier", Tiers(s, c, "Twitch Gift Subscription")));
        }));
        t.Add(new SbTriggerRule("GiftBomb", "twitch.giftBomb", ["GiftBomb", "TwitchGiftBomb", "106"], (s, c) =>
        {
            Unsupported(s, c, "Twitch Gift Bomb", "subType");
            return Trigger("twitch.giftBomb", s, [("tier", Tiers(s, c, "Twitch Gift Bomb")), .. Range(s, c, "Twitch Gift Bomb", "minTotal", null, 1)]);
        }));
        t.Add(new SbTriggerRule("Raid", "twitch.raid", ["Raid", "TwitchRaid", "107"],
            (s, c) => Trigger("twitch.raid", s, ("minViewers", Text(Math.Max(0, Num(s, "minViewers", "minimum") ?? RangeMin(s, c, "Twitch Raid") ?? 0))))));
        t.Add(new SbTriggerRule("RewardRedemption", "twitch.rewardRedemption",
            ["RewardRedemption", "ChannelPointReward", "TwitchRewardRedemption", "Reward", "112"],
            (s, c) => Trigger("twitch.rewardRedemption", s, ("rewardTitle", RewardTitle(s, c))))
        {
            Signatures = [["rewardId"], ["rewardName"]],
        });
        With("171", "Twitch Reward Redemption Updated", "twitch.rewardRedemptionUpdated", (s, c, _) => [("rewardTitle", RewardTitle(s, c))]);
        With("194", "Twitch Automatic Reward Redemption", "twitch.automaticRewardRedemption", (s, _, _) => [("rewardType", Str(s, "rewardType") ?? "")]);
        With("196", "Twitch Power Up Redemption", "twitch.powerUp",
            (s, c, l) => [("powerUpType", Choice(s, c, l, "powerUpType", ["gigantify_an_emote", "celebration", "message_effect"]))]);
        t.Add(new SbTriggerRule("StreamUpdate", "twitch.streamUpdate", ["StreamUpdate", "TwitchStreamUpdate", "118"], (s, c) =>
        {
            if (Bool(s, "gameOnly") == true)
                c.Warn($"'{c.ActionName}': a Stream update trigger was set to category changes only; Cuebot's also runs on title changes.");
            return Trigger("twitch.streamUpdate", s, ("categoryName", Str(s, "gameName") ?? ""));
        }));
        t.Add(new SbTriggerRule("FirstWords", "users.firstWords", ["FirstWords", "FirstWord", "TwitchFirstWords", "120"],
            (s, c) => FirstWords(s, c, "Twitch")));
        t.Add(new SbTriggerRule("PresentViewers", "twitch.presentViewers", ["PresentViewers", "TwitchPresentViewers", "124"],
            (s, _) => Trigger("twitch.presentViewers", s)));
        t.Add(new SbTriggerRule("ChatMessage", "twitch.chatMessage", ["ChatMessage", "TwitchChatMessage", "133"], (s, _) => Trigger("twitch.chatMessage", s)));
        t.Add(new SbTriggerRule("UserBanned", "twitch.userBanned", ["UserBanned", "TwitchUserBanned", "137"], (s, _) => Trigger("twitch.userBanned", s)));
        t.Add(new SbTriggerRule("AdRun", "twitch.adRun", ["AdRun", "TwitchAdRun", "139"], (s, _) => Trigger("twitch.adRun", s, ("kind", "Any"))));
        t.Add(new SbTriggerRule("StreamOnline", "twitch.streamOnline", ["StreamOnline", "TwitchStreamOnline", "154"], (s, _) => Trigger("twitch.streamOnline", s)));
        t.Add(new SbTriggerRule("StreamOffline", "twitch.streamOffline", ["StreamOffline", "TwitchStreamOffline", "155"], (s, _) => Trigger("twitch.streamOffline", s)));
        t.Add(new SbTriggerRule("UpcomingAd", "twitch.upcomingAd", ["UpcomingAd", "TwitchUpcomingAd", "186"], (s, c) =>
        {
            var minutes = Get(s, "minutes") is JsonArray list ? list.Select(AsString).Where(m => m is not null).ToList() : [];
            if (minutes.Count > 1) c.Warn($"'{c.ActionName}': an Upcoming ad trigger listed several minute marks; it was imported for every mark.");
            return Trigger("twitch.upcomingAd", s, ("minutes", minutes.Count == 1 ? minutes[0]! : "Any"));
        }));
        t.Add(new SbTriggerRule("WatchStreak", "twitch.watchStreak", ["WatchStreak", "TwitchWatchStreak", "472"],
            (s, c) => Trigger("twitch.watchStreak", s, Range(s, c, "Twitch Watch Streak", "minStreak", null))));
        With("164", "Twitch Viewer Count Update", "twitch.viewerCountUpdate", (s, c, l) => Range(s, c, l, "minViewers", null));
        foreach (var (code, label, cue) in new[]
                 {
                     // Channel goals, charity, chat
                     ("148", "Goal Begin", "goalBegin"), ("149", "Goal Progress", "goalProgress"), ("150", "Goal End", "goalEnd"),
                     ("146", "Charity Started", "charityStarted"), ("147", "Charity Progress", "charityProgress"),
                     ("142", "Charity Completed", "charityCompleted"), ("480", "Bits Badge Tier", "bitsBadgeTier"),
                     ("140", "Bot Whispers", "botWhisper"), ("119", "Whispers", "whisper"),
                     // Chat modes
                     ("451", "Emote Mode On", "emoteModeOn"), ("452", "Emote Mode Off", "emoteModeOff"),
                     ("453", "Follower Mode On", "followerModeOn"), ("454", "Follower Mode Off", "followerModeOff"),
                     ("455", "Follower Mode Changed", "followerModeChanged"), ("456", "Slow Mode On", "slowModeOn"),
                     ("457", "Slow Mode Off", "slowModeOff"), ("458", "Slow Mode Changed", "slowModeChanged"),
                     ("459", "Subscriber Mode On", "subscriberModeOn"), ("460", "Subscriber Mode Off", "subscriberModeOff"),
                     ("461", "Unique Mode On", "uniqueModeOn"), ("462", "Unique Mode Off", "uniqueModeOff"),
                     // Connections: Cuebot's bot account connects chat and EventSub together.
                     ("173", "Broadcaster Authenticated", "broadcasterAuthenticated"),
                     ("174", "Broadcaster Chat Connected", "broadcasterChatConnected"),
                     ("175", "Broadcaster Chat Disconnected", "broadcasterChatDisconnected"),
                     ("178", "Broadcaster EventSub Connected", "broadcasterConnected"),
                     ("179", "Broadcaster EventSub Disconnected", "broadcasterDisconnected"),
                     ("184", "Bot Chat Connected", "botConnected"), ("185", "Bot Chat Disconnected", "botDisconnected"),
                     // General, Guest Star, Hype Train
                     ("138", "Announcement", "announcement"), ("156", "Shoutout Received", "shoutoutReceived"),
                     ("165", "Guest Star Session Begin", "guestStarSessionBegin"), ("166", "Guest Star Session End", "guestStarSessionEnd"),
                     ("169", "Guest Star Settings Update", "guestStarSettingsUpdate"),
                     ("108", "Hype Train Start", "hypeTrainStart"), ("109", "Hype Train Update", "hypeTrainUpdate"),
                     ("111", "Hype Train End", "hypeTrainEnd"),
                     // Moderation
                     ("135", "Chat Message Deleted", "chatMessageDeleted"), ("144", "Shoutout Created", "shoutoutCreated"),
                     ("145", "User Untimed Out", "userUntimedOut"), ("151", "Shield Mode Begin", "shieldModeBegin"),
                     ("152", "Shield Mode End", "shieldModeEnd"), ("157", "Chat Cleared", "chatCleared"),
                     ("187", "Moderator Added", "moderatorAdded"), ("188", "Moderator Removed", "moderatorRemoved"),
                     ("189", "VIP Added", "vipAdded"), ("190", "VIP Removed", "vipRemoved"), ("191", "User Unbanned", "userUnbanned"),
                     ("192", "Unban Request Approved", "unbanRequestApproved"), ("193", "Unban Request Denied", "unbanRequestDenied"),
                     ("195", "Unban Request Created", "unbanRequestCreated"), ("463", "AutoMod Message Held", "automodMessageHeld"),
                     ("464", "AutoMod Message Updated", "automodMessageUpdated"), ("465", "Blocked Terms Added", "blockedTermsAdded"),
                     ("466", "Blocked Terms Deleted", "blockedTermsDeleted"), ("467", "Warned User", "userWarned"),
                     ("468", "Suspicious User Update", "suspiciousUserUpdate"), ("469", "Permitted Terms Added", "permittedTermsAdded"),
                     ("470", "Permitted Terms Deleted", "permittedTermsDeleted"), ("471", "Warning Acknowledged", "warningAcknowledged"),
                     ("495", "Suspicious User Message", "suspiciousUserMessage"),
                     // Polls, predictions, pyramid, raid
                     ("125", "Poll Created", "pollCreated"), ("126", "Poll Updated", "pollUpdated"), ("127", "Poll Completed", "pollCompleted"),
                     ("161", "Poll Terminated", "pollTerminated"), ("473", "Poll Archived", "pollArchived"),
                     ("128", "Prediction Created", "predictionCreated"), ("129", "Prediction Updated", "predictionUpdated"),
                     ("130", "Prediction Completed", "predictionCompleted"), ("131", "Prediction Canceled", "predictionCanceled"),
                     ("132", "Prediction Locked", "predictionLocked"), ("162", "Pyramid Success", "pyramid"),
                     ("163", "Pyramid Broken", "pyramidBroken"), ("158", "Raid Start", "raidStart"), ("159", "Raid Send", "raidSend"),
                     ("160", "Raid Cancelled", "raidCancelled"),
                     // Shared chat without filters
                     ("474", "Shared Chat Session Begin", "sharedChatBegin"), ("475", "Shared Chat Session Update", "sharedChatUpdate"),
                     ("476", "Shared Chat Session End", "sharedChatEnd"), ("481", "Shared Chat Announcement", "sharedChatAnnouncement"),
                     ("483", "Shared Chat Prime Paid Upgrade", "sharedChatPrimePaidUpgrade"),
                     ("484", "Shared Chat Gift Paid Upgrade", "sharedChatGiftPaidUpgrade"),
                     ("485", "Shared Chat Pay It Forward", "sharedChatPayItForward"), ("490", "Shared Chat User Banned", "sharedChatUserBanned"),
                     ("491", "Shared Chat User Unbanned", "sharedChatUserUnbanned"),
                     ("493", "Shared Chat User Untimed Out", "sharedChatUserUntimedOut"),
                     ("494", "Shared Chat Message Deleted", "sharedChatMessageDeleted"),
                     // Subscriptions
                     ("121", "Sub Counter Rollover", "subCounterRollover"), ("477", "Prime Paid Upgrade", "primePaidUpgrade"),
                     ("478", "Pay It Forward", "payItForward"), ("479", "Gift Paid Upgrade", "giftPaidUpgrade"),
                 })
            Plain(code, "Twitch " + label, "twitch." + cue);
        Plain("141", "Twitch Charity Donation", "twitch.charityDonation", "min", "max");
        With("497", "Twitch Modiversary", "twitch.modiversary", (s, c, l) => Range(s, c, l, "minMonths", null));
        With("110", "Twitch Hype Train Level Up", "twitch.hypeTrainLevelUp", (s, c, l) => Range(s, c, l, "minLevel", null, 2));
        With("136", "Twitch User Timed Out", "twitch.userTimedOut", (s, c, l) => Range(s, c, l, "minDuration", null));
        With("167", "Twitch Guest Star Guest Update", "twitch.guestStarGuestUpdate",
            (s, c, l) => [("state", Choice(s, c, l, "state", ["invited", "accepted", "ready", "backstage", "live", "removed"]))]);
        // BetterTTV and 7TV changes: Cuebot's emote triggers filter by provider (not by platform).
        With("180", "Twitch SevenTV Emote Added", "chat.emoteAdded", (_, _, _) => [("provider", "7TV")]);
        With("181", "Twitch SevenTV Emote Removed", "chat.emoteRemoved", (_, _, _) => [("provider", "7TV")]);
        With("182", "Twitch BetterTTV Emote Added", "chat.emoteAdded", (_, _, _) => [("provider", "BTTV")]);
        With("183", "Twitch BetterTTV Emote Removed", "chat.emoteRemoved", (_, _, _) => [("provider", "BTTV")]);
        Plain("482", "Twitch Shared Chat Raid", "twitch.sharedChatRaid", "min", "max");
        Plain("486", "Twitch Shared Chat Sub", "twitch.sharedChatSub", "tiers");
        Plain("487", "Twitch Shared Chat Resub", "twitch.sharedChatResub", "tiers", "min", "max");
        Plain("488", "Twitch Shared Chat Sub Gift", "twitch.sharedChatSubGift", "tiers", "min", "max", "subType", "monthsGifted");
        Plain("489", "Twitch Shared Chat Community Sub Gift", "twitch.sharedChatGiftBomb", "tiers", "min", "max", "subType");
        Plain("492", "Twitch Shared Chat User Timed Out", "twitch.sharedChatUserTimedOut", "min", "max");
        Plain("498", "Twitch Shared Modiversary", "twitch.sharedChatModiversary", "min", "max");

        // ---------------------------------------------------------------- YouTube
        t.Add(new SbTriggerRule("YouTubeBroadcastStarted", "youtube.broadcastStarted", ["YouTubeBroadcastStarted", "4001"],
            (s, _) => Trigger("youtube.broadcastStarted", s)));
        t.Add(new SbTriggerRule("YouTubeBroadcastEnded", "youtube.broadcastEnded", ["YouTubeBroadcastEnded", "4002"],
            (s, _) => Trigger("youtube.broadcastEnded", s)));
        t.Add(new SbTriggerRule("YouTubeMessage", "youtube.chatMessage", ["YouTubeMessage", "YouTubeChatMessage", "4003"],
            (s, _) => Trigger("youtube.chatMessage", s)));
        t.Add(new SbTriggerRule("YouTubeUserBanned", "youtube.userBanned", ["YouTubeUserBanned", "4005"], (s, _) => Trigger("youtube.userBanned", s)));
        t.Add(new SbTriggerRule("YouTubeFirstWords", "users.firstWords", ["YouTubeFirstWords", "4016"], (s, c) => FirstWords(s, c, "YouTube")));
        // Super Chat / Super Sticker ranges are on the amount.
        With("4006", "YouTube Super Chat", "youtube.superChat", Amount);
        With("4007", "YouTube Super Sticker", "youtube.superSticker", Amount);
        With("4017", "YouTube Present Viewers", "users.presentViewers", (_, _, _) => [("platform", "YouTube")]);
        With("4030", "YouTube Jewels Gifted", "youtube.jewelsGifted", (s, c, l) => Range(s, c, l, "minJewels", null));
        With("4031", "YouTube User Timed Out", "youtube.userTimedOut", (s, c, l) => Range(s, c, l, "minDuration", null));
        foreach (var (code, label, cue) in new[]
                 {
                     ("4008", "New Sponsor", "newSponsor"), ("4009", "Member Milestone", "memberMilestone"),
                     ("4010", "Sponsor Only Started", "sponsorOnlyStarted"), ("4011", "Sponsor Only Ended", "sponsorOnlyEnded"),
                     ("4012", "Statistics Updated", "broadcastStatisticsUpdated"), ("4013", "Broadcast Updated", "broadcastUpdated"),
                     ("4014", "Membership Gift", "membershipGift"), ("4015", "Gift Membership Received", "giftMembershipReceived"),
                     ("4018", "New Subscriber", "newSubscriber"), ("4019", "Broadcast Monitoring Started", "broadcastMonitoringStarted"),
                     ("4020", "Broadcast Monitoring Ended", "broadcastMonitoringEnded"), ("4021", "Broadcast Added", "broadcastAdded"),
                     ("4022", "Broadcast Removed", "broadcastRemoved"), ("4027", "Poll Closed", "pollClosed"),
                     ("4028", "Poll Started", "pollStarted"), ("4029", "Poll Updated", "pollUpdated"),
                 })
            Plain(code, "YouTube " + label, "youtube." + cue);
    }

    // ---- trigger helpers ----

    /// <summary>Meld Studio session events Cuebot's Meld "Event" trigger can filter on.</summary>
    private static readonly string[] MeldEvents =
    [
        "LayerVisibilityChanged", "EffectEnabledStateChanged", "TrackMutedStateChanged", "TrackMonitoringStateChanged", "ObjectAdded",
        "ObjectUpdated", "ObjectRemoved", "SceneStaged", "TrackGainChanged",
    ];

    /// <summary>The label without spaces or punctuation ("Twitch Goal Begin" → "TwitchGoalBegin").</summary>
    private static string RuleName(string label) => new(label.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Reports the listed Streamer.bot filter fields that hold a value: Cuebot's trigger cannot filter on them, so
    /// it runs for every event. Unset values (null, "", -1, 0, false, the all-zero GUID) and the "everything"
    /// defaults (tiers 16, monthsGifted 15, mixerType all) are not reported.
    /// </summary>
    private static void Unsupported(JsonObject s, ISbMapContext c, string label, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (Get(s, field) is not { } node || IsUnsetFilter(field, node)) continue;
            c.Warn($"'{c.ActionName}': a {label} trigger filters on '{field}' ({AsString(node) ?? node.ToJsonString()}), which Cuebot's trigger "
                   + "cannot match; it was imported without that filter.");
        }
    }

    private static bool IsUnsetFilter(string field, JsonNode node)
    {
        if (node is JsonObject o) return o.Count == 0;
        if (node is JsonArray a) return a.Count == 0;
        var text = AsString(node);
        return (field, text) switch
        {
            ("tiers", "16") or ("monthsGifted", "15") or ("mixerType", "com.elgato.mix.all") => true,
            (_, null or "" or "-1" or "0" or "false") => true,
            _ => !IsRealId(text),
        };
    }

    /// <summary>
    /// Streamer.bot's Range filter as Cuebot min/max settings. -1 is open; a Max without a Min means exactly that
    /// value, so it becomes the minimum too. A bound Cuebot's trigger has no setting for is reported.
    /// </summary>
    private static (string Key, string Value)[] Range(JsonObject s, ISbMapContext c, string label, string? minKey, string? maxKey, double floor = 0)
    {
        var min = RangeMin(s, c, label, warnExact: maxKey is null);
        var max = Num(s, "max") is { } hi and >= 0 ? hi : (double?)null;
        var settings = new List<(string Key, string Value)>();
        if (minKey is not null) settings.Add((minKey, Text(Math.Max(floor, min ?? floor))));
        else if (min is not null) Unsupported(s, c, label, "min");
        if (maxKey is not null) settings.Add((maxKey, max is { } m ? Text(m) : "0"));
        else if (max is not null && Num(s, "min") is >= 0) Unsupported(s, c, label, "max");
        return [.. settings];
    }

    /// <summary>The Range filter's lower bound (an exact Max counts as one), or null when open.</summary>
    private static double? RangeMin(JsonObject s, ISbMapContext c, string label, bool warnExact = true)
    {
        if (Num(s, "min") is { } lo and >= 0) return lo;
        if (Num(s, "max") is not ({ } hi and >= 0)) return null;
        if (warnExact)
            c.Warn($"'{c.ActionName}': a {label} trigger matched exactly {Text(hi)}; it was imported as \"at least {Text(hi)}\".");
        return hi;
    }

    /// <summary>Donation-style amount range (minAmount / maxAmount, 0 = no maximum).</summary>
    private static (string Key, string Value)[] Amount(JsonObject s, ISbMapContext c, string label) => Range(s, c, label, "minAmount", "maxAmount");

    /// <summary>
    /// Sub tier: older exports name it in "tier" ("1000", "prime"...); 1.0.x saves a "tiers" bitmask whose default
    /// (16) is every tier. Other masks are not decoded and become Any, with a warning.
    /// </summary>
    private static string Tiers(JsonObject s, ISbMapContext c, string label)
    {
        if (Str(s, "tier") is { } tier) return Tier(tier);
        Unsupported(s, c, label, "tiers");
        return "Any";
    }

    private static string Tier(string? tier) => Normalize(tier) switch
    {
        "1000" or "1" or "tier1" or "prime" => "1",
        "2000" or "2" or "tier2" => "2",
        "3000" or "3" or "tier3" => "3",
        _ => "Any",
    };

    /// <summary>A text value that must be one of Cuebot's choices (compared ignoring case and punctuation), else Any.</summary>
    private static string Choice(JsonObject s, ISbMapContext c, string label, string field, IReadOnlyList<string> choices)
    {
        var value = Str(s, field);
        if (string.IsNullOrWhiteSpace(value)) return "Any";
        if (choices.FirstOrDefault(x => Normalize(x) == Normalize(value)) is { } match) return match;
        Unsupported(s, c, label, field);
        return "Any";
    }

    /// <summary>A filter Streamer.bot saves by name and id: the name if there is one, else any (the id is reported).</summary>
    private static string NameOrId(JsonObject s, ISbMapContext c, string label, string nameField, string idField)
    {
        if (Str(s, nameField) is { Length: > 0 } name) return name;
        Unsupported(s, c, label, idField);
        return "";
    }

    private static string RewardTitle(JsonObject s, ISbMapContext c)
    {
        var title = Str(s, "rewardName", "rewardTitle", "title", "name") ?? "";
        if (title.Length == 0 && Str(s, "rewardId") is { Length: > 0 } id)
            c.Warn($"'{c.ActionName}': reward trigger names reward id {id} only; set the reward title in Cuebot.");
        return title;
    }

    /// <summary>Global variable triggers: the name, and "persisted" in the documented order 0 Either, 1 Yes, 2 No.</summary>
    private static (string Key, string Value)[] GlobalVariable(JsonObject s, ISbMapContext c, string label)
    {
        var persisted = Normalize(Str(s, "persisted")) switch
        {
            "" or "0" => "Either",
            "1" or "true" => "Yes",
            "2" or "false" => "No",
            var other => Unknown(other),
        };
        return [("variable", Str(s, "variableName") ?? ""), ("persisted", persisted)];

        string Unknown(string value)
        {
            c.Warn($"'{c.ActionName}': a {label} trigger's 'persisted' value {value} is not known; it was set to Either.");
            return "Either";
        }
    }

    /// <summary>Streamer.bot watches a process by name; Cuebot by a named process watcher.</summary>
    private static (string Key, string Value)[] ProcessWatcher(JsonObject s, ISbMapContext c, string label)
    {
        if (Str(s, "processName") is { Length: > 0 } process)
            c.Warn($"'{c.ActionName}': a {label} trigger watches '{process}'; add a process watcher for it in Cuebot and pick it in the trigger.");
        return [("watcher", "")];
    }

    /// <summary>Crowd Control effect triggers: effect and game by name, the range on the price.</summary>
    private static (string Key, string Value)[] CrowdControlEffect(JsonObject s, ISbMapContext c, string label)
    {
        Unsupported(s, c, label, "packName", "effectType");
        return
        [
            ("effectName", NameOrId(s, c, label, "effectName", "effectId")),
            ("gameName", NameOrId(s, c, label, "gameName", "gameId")),
            .. Range(s, c, label, "minPrice", null),
        ];
    }

    private static TriggerDefinition ObsTrigger(TriggerDefinition trigger, JsonObject s, ISbMapContext c)
    {
        ObsConnection(s, c);
        trigger.Settings["connection"] = "";
        return trigger;
    }

    private static TriggerDefinition FirstWords(JsonObject s, ISbMapContext c, string platform)
    {
        if (Str(s, "username") is { Length: > 0 } user)
            c.Warn($"'{c.ActionName}': a First words trigger was limited to '{user}'; Cuebot's runs for every user. Add a condition on %userName%.");
        return Trigger("users.firstWords", s, ("platform", platform));
    }
}
