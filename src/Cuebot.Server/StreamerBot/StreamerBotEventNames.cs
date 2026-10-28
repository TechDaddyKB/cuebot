namespace Cuebot.Server.StreamerBot;

/// <summary>
/// The WebSocket event names Streamer.bot documents publicly: the GetEvents output published with the official client
/// (github.com/Streamerbot/client, events.const.ts) plus the <c>EventType</c> enum of the C# docs
/// (3.api/3.csharp/5.enums/event-type.md), split into source and type. Spellings are kept exactly as published.
/// Used by tests to check that Cuebot emits documented names; Cuebot-only events are not listed.
/// </summary>
public static class StreamerBotEventNames
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Documented = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["Application"] = Set(
            "ActionAdded", "ActionDeleted", "ActionUpdated", "CommandAdded", "CommandDeleted", "CommandUpdated",
            "Import"),
        ["Command"] = Set(
            "Cooldown", "Triggered"),
        ["CrowdControl"] = Set(
            "CoinExchange", "EffectFailure", "EffectRequest", "EffectSuccess", "GameSessionEnd", "GameSessionStart",
            "TimedEffectEnded", "TimedEffectStarted", "TimedEffectUpdated"),
        ["Custom"] = Set(
            "CodeEvent", "Event"),
        ["DonorDrive"] = Set(
            "Donation", "Incentive", "ProfileUpdated"),
        ["Elgato"] = Set(
            "CameraHubConnected", "CameraHubDisconnected", "CameraHubPrompterAutoScrollChapterDisabled",
            "CameraHubPrompterAutoScrollChapterEnabled", "CameraHubPrompterAutoScrollDisabled",
            "CameraHubPrompterAutoScrollEnabled", "CameraHubPrompterBackgroundColorChanged",
            "CameraHubPrompterBrightnessChanged", "CameraHubPrompterChannelsChanged", "CameraHubPrompterConnected",
            "CameraHubPrompterContrastChanged", "CameraHubPrompterCrosshairColorChanged",
            "CameraHubPrompterCrosshairDisabled", "CameraHubPrompterCrosshairEnabled",
            "CameraHubPrompterCrosshairImageChanged", "CameraHubPrompterDisconnected", "CameraHubPrompterFontChanged",
            "CameraHubPrompterFontColorChanged", "CameraHubPrompterFontSizeChanged",
            "CameraHubPrompterHorizontalMarginChanged", "CameraHubPrompterLineSpacingChanged",
            "CameraHubPrompterModeChanged", "CameraHubPrompterOpacityChanged", "CameraHubPrompterScrollSpeedChanged",
            "CameraHubPrompterSelectedChannelChanged", "CameraHubPrompterSelectedChapterChanged",
            "CameraHubPrompterSelectedScriptChanged", "CameraHubPrompterVerticalMarginChanged",
            "CameraHubSelectedWebcamChanged", "CameraHubTiltChanged", "CameraHubWebcamARLensChanged",
            "CameraHubWebcamActivated", "CameraHubWebcamAntiFlickerChanged", "CameraHubWebcamAutoExposureDisabled",
            "CameraHubWebcamAutoExposureEnabled", "CameraHubWebcamAutoFocusDisabled",
            "CameraHubWebcamAutoFocusEnabled", "CameraHubWebcamAutoWhiteBalanceDisabled",
            "CameraHubWebcamAutoWhiteBalanceEnabled", "CameraHubWebcamBitrateChanged",
            "CameraHubWebcamBrightnessChanged", "CameraHubWebcamConnected", "CameraHubWebcamContrastChanged",
            "CameraHubWebcamDeactivated", "CameraHubWebcamDeviceOrientationChanged",
            "CameraHubWebcamExposureAutoLockDisabled", "CameraHubWebcamExposureAutoLockEnabled",
            "CameraHubWebcamFlashDisabled", "CameraHubWebcamFlashEnabled", "CameraHubWebcamFlipped",
            "CameraHubWebcamFocusChanged", "CameraHubWebcamISOChanged", "CameraHubWebcamLensChanged",
            "CameraHubWebcamLiveISOChanged", "CameraHubWebcamLiveShutterSpeedChanged",
            "CameraHubWebcamLiveWhiteBalanceChanged", "CameraHubWebcamLiveWhiteBalanceTintChanged",
            "CameraHubWebcamMirrored", "CameraHubWebcamNoiseReductionDisabled", "CameraHubWebcamNoiseReductionEnabled",
            "CameraHubWebcamOverscanChanged", "CameraHubWebcamPanChanged", "CameraHubWebcamSaturationChanged",
            "CameraHubWebcamSharpnessChanged", "CameraHubWebcamShutterSpeedChanged", "CameraHubWebcamSnapshotTaken",
            "CameraHubWebcamWhiteBalanceChanged", "CameraHubWebcamWhiteBalanceTintChanged",
            "CameraHubWebcamZoomChanged", "CameraHubWebcamrDisconnected", "ControlCenterConnected",
            "ControlCenterDeviceAdded", "ControlCenterDeviceBrightnessChanged",
            "ControlCenterDeviceConfigurationChanged", "ControlCenterDeviceOff", "ControlCenterDeviceOn",
            "ControlCenterDevicePowerStateChanged", "ControlCenterDeviceRemoved", "ControlCenterDeviceRenamed",
            "ControlCenterDeviceTemperatureChanged", "ControlCenterDisconnected", "StreamDeckMcpConnected",
            "StreamDeckMcpDisconnected", "StudioConnected", "StudioDisconnected", "StudioLevelChanged",
            "StudioPreviewFullscreenChanged", "StudioPreviewVolumeChanged", "StudioPreviewVolumeMuteChanged",
            "StudioRecordingChanged", "StudioRecordingFinished", "StudioSnapshotTaken", "WaveLinkConnected",
            "WaveLinkDisconnected", "WaveLinkFilterAdded", "WaveLinkFilterBypassStateChanged", "WaveLinkFilterChanged",
            "WaveLinkFilterDeleted", "WaveLinkInputLevelMeterChanged", "WaveLinkInputMuteChanged",
            "WaveLinkInputNameChanged", "WaveLinkInputVolumeChanged", "WaveLinkMicrophoneBalanceChanged",
            "WaveLinkMicrophoneGainChanged", "WaveLinkMicrophoneMuteChanged", "WaveLinkMicrophoneOutputVolumeChanged",
            "WaveLinkMicrophoneSettingChanged", "WaveLinkOutputLevelMeterChanged", "WaveLinkOutputMuteChanged",
            "WaveLinkOutputSwitched", "WaveLinkOutputVolumeChanged", "WaveLinkSelectedOutputChanged",
            "WaveLinkV3ChannelEffectsChanged", "WaveLinkV3ChannelMuteChanged", "WaveLinkV3ChannelUpdated",
            "WaveLinkV3ChannelVolumeChanged", "WaveLinkV3ChannelsChanged", "WaveLinkV3Connected",
            "WaveLinkV3CreateProfileRequested", "WaveLinkV3Disconnected", "WaveLinkV3FocusedAppChanged",
            "WaveLinkV3InputDeviceUpdated", "WaveLinkV3InputDevicesChanged", "WaveLinkV3InputDspEffectsChanged",
            "WaveLinkV3InputGainChanged", "WaveLinkV3LevelMeterUpdated", "WaveLinkV3MainOutputDeviceChanged",
            "WaveLinkV3MixUpdated", "WaveLinkV3MixesChanged", "WaveLinkV3OutputDeviceUpdated",
            "WaveLinkV3OutputMixChanged"),
        ["FileTail"] = Set(
            "Changed"),
        ["FileWatcher"] = Set(
            "Changed", "Created", "Deleted", "Renamed"),
        ["Fourthwall"] = Set(
            "Donation", "GiftDrawEnded", "GiftDrawStarted", "GiftPurchase", "NewsletterSubscribed", "OrderPlaced",
            "OrderUpdated", "ProductCreated", "ProductUpdated", "SubscriptionChanged", "SubscriptionExpired",
            "SubscriptionPurchased", "ThankYouSent"),
        ["General"] = Set(
            "Custom"),
        ["Group"] = Set(
            "Added", "Cleared", "Removed", "UsersAdded", "UsersRemoved"),
        ["HypeRate"] = Set(
            "Connected", "Disconnected", "HeartRatePulse", "TwitchClipCreated"),
        ["Inputs"] = Set(
            "InputControllerAxis", "InputControllerButton", "InputControllerHat", "InputKeyPress", "InputMouseClick"),
        ["Kick"] = Set(
            "BroadcasterAuthenticated", "BroadcasterChatConnected", "BroadcasterChatDisconnected", "ChannelUpdate",
            "ChatMessage", "FirstWords", "Follow", "GiftSubscription", "KicksGifted", "MassGiftSubscription",
            "PresentViewers", "Resubscription", "RewardRedemption", "SevenTVEmoteAdded", "SevenTVEmoteRemoved",
            "StreamOffline", "StreamOnline", "Subscription", "UserBanned", "UserTimedOut", "ViewerCountUpdate"),
        ["Kofi"] = Set(
            "Commission", "Donation", "Resubscription", "ShopOrder", "Subscription"),
        ["MeldStudio"] = Set(
            "Connected", "Disconnected", "Event", "LEffectEnabledStateChanged", "LayerVisbilityChanged",
            "RecordingStarted", "RecordingStopped", "SceneChanged", "StreamingStarted", "StreamingStopped",
            "TrackMonitoringStateChanged", "TrackMustedStateChanged"),
        ["Midi"] = Set(
            "Message"),
        ["Misc"] = Set(
            "ApplicationImport", "ChatWindowAction", "GlobalVariableCreated", "GlobalVariableDeleted",
            "GlobalVariableUpdated", "ProcessStarted", "ProcessStopped", "StreamerbotExiting", "StreamerbotStarted",
            "Test", "TimedAction", "ToastActivation", "UserGlobalVariableUpdated"),
        ["Obs"] = Set(
            "Connected", "Disconnected", "Event", "RecordingStarted", "RecordingStopped", "SceneChanged",
            "StreamingStarted", "StreamingStopped", "VendorEvent"),
        ["Pallygg"] = Set(
            "CampaignTip", "Connected", "Disconnected"),
        ["Patreon"] = Set(
            "FollowCreated", "FollowDeleted", "PledgeCreated", "PledgeDeleted", "PledgeUpdated"),
        ["Pulsoid"] = Set(
            "HeartRatePulse"),
        ["Quote"] = Set(
            "Added", "Show"),
        ["Raw"] = Set(
            "Action", "ActionCompleted", "SubAction"),
        ["Shopify"] = Set(
            "OrderCreated", "OrderPaid"),
        ["SpeakerBot"] = Set(
            "Connected", "Disconnected"),
        ["SpeechToText"] = Set(
            "Command", "Dictation", "Log"),
        ["StreamDeck"] = Set(
            "Action", "Connected", "Disconnected", "Info"),
        ["StreamElements"] = Set(
            "Authenticated", "Connected", "Disconnected", "Merch", "Tip"),
        ["StreamLoots"] = Set(
            "CardRedeemed", "PackGifted", "PackPurchased", "Reaction"),
        ["StreamerBot"] = Set(
            "CustomWebook"),
        ["StreamerBotRemote"] = Set(
            "InstanceConnected", "InstanceDisconnected", "InstanceSignal", "InstanceTrigger"),
        ["Streamlabs"] = Set(
            "Authenticated", "CharityDonation", "Connected", "Disconnected", "Donation", "Merchandise"),
        ["StreamlabsDesktop"] = Set(
            "Connected", "Disconnected", "RecordingStarted", "RecordingStopped", "SceneChanged", "StreamingStarted",
            "StreamingStopped"),
        ["System"] = Set(
            "ClipboardChanged", "Cron"),
        ["Throne"] = Set(
            "ContributionPurchased", "GiftCrowdfunded", "GiftPurchased"),
        ["ThrowingSystem"] = Set(
            "Connected", "EventsConnected", "EventsDisconnected", "ItemHit", "TriggerActivated", "TriggerEnded",
            "WebsocketConnected", "WebsocketDisconnected"),
        ["TipeeeStream"] = Set(
            "Donation"),
        ["TreatStream"] = Set(
            "Authenticated", "Connected", "Disconnected", "Treat"),
        ["Trovo"] = Set(
            "BroadcasterAuthenticated", "BroadcasterChatConnected", "BroadcasterChatDisconnected", "ChatMessage",
            "CustomSpellCast", "FirstWords", "Follow", "GiftSubscription", "MassGiftSubscription", "PresentViewers",
            "Raid", "Resubscription", "SpellCast", "StreamOffline", "StreamOnline", "Subscription"),
        ["Twitch"] = Set(
            "AdRun", "Announcement", "AutoModMessageHeld", "AutoModMessageUpdate", "AutomaticRewardRedemption",
            "BetterTTVEmoteAdded", "BetterTTVEmoteRemoved", "BitsBadgeTier", "BlockedTermsAdded",
            "BlockedTermsDeleted", "BotEventSubConnected", "BotEventSubDisconnected", "BotWhisper", "BroadcastUpdate",
            "BroadcasterAuthenticated", "BroadcasterChatConnected", "BroadcasterChatDisconnected",
            "BroadcasterEventSubConnected", "BroadcasterEventSubDisconnected", "CharityCompleted", "CharityDonation",
            "CharityProgress", "CharityStarted", "ChatCleared", "ChatEmoteModeOff", "ChatEmoteModeOn",
            "ChatFollowerModeChanged", "ChatFollowerModeOff", "ChatFollowerModeOn", "ChatMessage",
            "ChatMessageDeleted", "ChatSlowModeChanged", "ChatSlowModeOff", "ChatSlowModeOn", "ChatSubscriberModeOff",
            "ChatSubscriberModeOn", "ChatUniqueModeOff", "ChatUniqueModeOn", "Cheer", "CoinCheer",
            "CommunityGoalContribution", "CommunityGoalEnded", "CustomPowerUpRedemption", "FirstWord", "Follow",
            "GiftBomb", "GiftPaidUpgrade", "GiftSub", "GoalBegin", "GoalEnd", "GoalProgress", "GuestStarGuestUpdate",
            "GuestStarSessionBegin", "GuestStarSessionEnd", "GuestStarSettingsUpdate", "GuestStarSlotUpdate",
            "HypeChat", "HypeChatLevel", "HypeTrainEnd", "HypeTrainLevelUp", "HypeTrainStart", "HypeTrainUpdate",
            "ModeratorAdded", "ModeratorRemoved", "Modiversary", "PayItForward", "PermittedTermsAdded",
            "PermittedTermsDeleted", "PollArchived", "PollCompleted", "PollCreated", "PollTerminated", "PollUpdated",
            "PowerUp", "PowerUpRedemption", "PredictionCanceled", "PredictionCompleted", "PredictionCreated",
            "PredictionLocked", "PredictionUpdated", "PresentViewers", "PrimePaidUpgrade", "PyramidBroken",
            "PyramidSuccess", "Raid", "RaidCancelled", "RaidSend", "RaidStart", "ReSub", "RewardCreated",
            "RewardDeleted", "RewardRedemption", "RewardRedemptionUpdated", "RewardUpdated", "SevenTVEmoteAdded",
            "SevenTVEmoteRemoved", "SharedChatAnnouncement", "SharedChatCommunitySubGift", "SharedChatGiftPaidUpgrade",
            "SharedChatMessageDeleted", "SharedChatPayItForward", "SharedChatPrimePaidUpgrade", "SharedChatRaid",
            "SharedChatResub", "SharedChatSessionBegin", "SharedChatSessionEnd", "SharedChatSessionUpdate",
            "SharedChatSub", "SharedChatSubGift", "SharedChatUserBanned", "SharedChatUserTimedout",
            "SharedChatUserUnbanned", "SharedChatUserUntimedout", "SharedModiversary", "ShieldModeBegin",
            "ShieldModeEnd", "ShoutoutCreated", "ShoutoutReceived", "StreamOffline", "StreamOnline", "StreamUpdate",
            "StreamUpdateGameOnConnect", "Sub", "SubCounterRollover", "SuspiciousUserMessage", "SuspiciousUserUpdate",
            "UnbanRequestApproved", "UnbanRequestCreated", "UnbanRequestDenied", "UpcomingAd", "UserBanned",
            "UserTimedOut", "UserUnbanned", "UserUntimedOut", "ViewerCountUpdate", "VipAdded", "VipRemoved",
            "WarnedUser", "WarningAcknowledged", "WatchStreak", "Whisper"),
        ["VTubeStudio"] = Set(
            "BackgroundChanged", "Connected", "Disconnected", "HotkeyTriggered", "ItemEvent", "ModelAnimation",
            "ModelClicked", "ModelConfigChanged", "ModelLoaded", "ModelUnloaded", "TrackingStatusChanged"),
        ["VoiceMod"] = Set(
            "Connected", "Disconnected", "SoundboardChanged", "VoiceLoaded"),
        ["WebsocketClient"] = Set(
            "Close", "Message", "Open"),
        ["WebsocketCustomServer"] = Set(
            "Close", "Message", "Open"),
        ["YouTube"] = Set(
            "BetterTTVEmoteAdded", "BetterTTVEmoteRemoved", "BroadcastAdded", "BroadcastEnded",
            "BroadcastMonitoringEnded", "BroadcastMonitoringStarted", "BroadcastRemoved", "BroadcastStarted",
            "BroadcastUpdated", "FirstWords", "GiftMembershipReceived", "JewelsGifted", "MemberMileStone",
            "MembershipGift", "Message", "MessageDeleted", "NewSponsor", "NewSponsorOnlyEnded",
            "NewSponsorOnlyStarted", "NewSubscriber", "PollClosed", "PollStarted", "PollUpdated", "PresentViewers",
            "SevenTVEmoteAdded", "SevenTVEmoteRemoved", "StatisticsUpdated", "SuperChat", "SuperSticker", "UserBanned",
            "UserTimedout"),
    };

    /// <summary>Whether Streamer.bot documents <paramref name="source"/> as an event source.</summary>
    public static bool IsDocumentedSource(string source) => Documented.ContainsKey(source);

    /// <summary>Whether Streamer.bot documents this exact source and type.</summary>
    public static bool IsDocumented(string source, string type) => Documented.TryGetValue(source, out var types) && types.Contains(type);

    private static IReadOnlySet<string> Set(params string[] types) => new HashSet<string>(types, StringComparer.Ordinal);
}
